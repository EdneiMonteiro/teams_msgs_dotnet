# Troubleshooting

Catálogo dos sintomas observados durante a construção e operação desta PoC. Casos novos vão sendo adicionados aqui.

## Build / DI

### `Unable to activate type 'CloudAdapter'. The following constructors are ambiguous`

`Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter` tem dois construtores:
- `(IConfiguration, IHttpClientFactory, ILogger)`
- `(BotFrameworkAuthentication, ILogger)`

O container DI não consegue escolher. Registre com factory explícita:

```csharp
services.AddSingleton<CloudAdapter>(sp => new CloudAdapter(
    sp.GetRequiredService<BotFrameworkAuthentication>(),
    sp.GetRequiredService<ILogger<CloudAdapter>>()));
```

### Worker em `CrashLoopBackOff` com `No frameworks were found`

`Microsoft.Bot.Builder.Integration.AspNet.Core` puxa dependências de ASP.NET Core. Se o `Dockerfile.worker` usa `mcr.microsoft.com/dotnet/runtime:8.0-alpine`, falta o framework. Use `dotnet/aspnet:8.0-alpine`.

### `error NU1109: Detected package downgrade`

Central Package Management exige que todas as versões transitivas sejam ≥ que as pinadas. Bump a versão central até cobrir o que as deps Azure Core puxam (`Microsoft.Extensions.* >= 10.0.3` na versão atual do SDK).

### `error NU1008: should not define the version on the PackageReference items`

Em projetos com central management, `<PackageReference Include="..." Version="..." />` é proibido. Use só `<PackageReference Include="..." />` e defina a `<PackageVersion ... Version="..." />` no `Directory.Packages.props`.

## Bicep / Deploy

### `InvalidChannelData — Agent.MessagesUrl must not be an IP address`

Azure Bot exige FQDN no `messagingEndpoint`. Configure DNS label no Public IP do Istio Gateway antes:

```bash
az network public-ip update -g <MC_RG> -n <PIP_NAME> --dns-name teams-msgs-dotnet
# → teams-msgs-dotnet.brazilsouth.cloudapp.azure.com
```

### `DnsRecordInUse` ao mover DNS label entre Public IPs

`--dns-name ""` não funciona. Use:

```bash
az network public-ip update -g <MC_RG> -n <PIP_OLD> --remove dnsSettings
```

## Runtime / Connection strings

### `401`/`403` ao acessar Storage, Service Bus ou Redis

Com connection strings (Workload Identity foi removido), erros de auth quase sempre são connection string ausente ou errada no `Secret`. Verifique:

```bash
kubectl -n teams-msgs get secret teams-msgs-secrets -o jsonpath='{.data}' | \
  jq 'keys'   # deve conter Storage__ConnectionString, ServiceBus__ConnectionString, Redis__ConnectionString
```

- `Storage__ConnectionString` → `TableClient` (tabela `conversationrefs`)
- `ServiceBus__ConnectionString` → `ServiceBusClient` (producer/consumer) **e** o `TriggerAuthentication` do KEDA
- `Redis__ConnectionString` → `ConnectionMultiplexer` (counters/index/cache/bucket)

Reaplique com os outputs `@secure` do Bicep (`storageConnectionString`/`serviceBusConnectionString`/`redisConnectionString`) e `helm upgrade`.

### Redis indisponível → `/readyz` 503

O `RedisConnection` usa `abortConnect=False`, mas se o host/porta/chave estiverem errados o ping falha. Confira o formato `({host}:10000,password=<key>,ssl=True,abortConnect=False)` (Azure Managed Redis usa a porta TLS 10000) e que o firewall do Azure Managed Redis permite o egress do cluster.

## Ingress / TLS

### `504 Gateway Timeout` em `/api/send` com fan-out grande

O timeout default do NGINX Ingress era 60s. Mesmo problema acontece em qualquer ingress sem timeout estendido. No Istio Gateway (atual), o `HTTPRoute` já tem:

```yaml
timeouts:
  request: 600s
  backendRequest: 600s
```

Padrão de produção: refatore o fan-out para `BackgroundService` + `Channel<T>` e retorne 202 imediatamente. A API só registra o job e enfileira as mensagens em segundo plano.

### Cert-manager não cria `Certificate` a partir da annotation `cert-manager.io/cluster-issuer` no `Gateway`

A integração Gateway API do cert-manager 1.20 funciona, mas se não acionar, crie a `Certificate` explicitamente:

```yaml
apiVersion: cert-manager.io/v1
kind: Certificate
metadata:
  name: teams-msgs-gw-tls
  namespace: aks-istio-ingress
spec:
  secretName: teams-msgs-gw-tls
  issuerRef: { name: letsencrypt-prod, kind: ClusterIssuer }
  dnsNames: ["teams-msgs-dotnet.brazilsouth.cloudapp.azure.com"]
```

## KEDA / Workers

### KEDA `READY=False  Reason=TriggerError`

Em geral é a connection string do Service Bus ausente/errada no `TriggerAuthentication` (o `scaledobject.yaml` referencia o secret key `ServiceBus__ConnectionString`). Logs do operator:
```bash
kubectl -n kube-system logs deployment/keda-operator --tail=50
```

### Worker pods reiniciando (exit 0 / "Completed") sob carga

Sintoma na PoC: workers processando dezenas de milhares de msgs com `BadRequest` reiniciavam (`restartCount` crescente), **sem OOM** — `lastState.terminated.exitCode = 0`, `reason: Completed`, memória baixa (~60 MiB de 512). A causa NÃO é memória: era uma `TaskCanceledException` (timeout do `HttpClient`) que, por derivar de `OperationCanceledException`, escapava dos `catch` (que excluíam `OperationCanceledException`), faltava o `Task.WhenAll` e **encerrava o `BackgroundService`** — derrubando o pod (exit 0 → restart → perda de locks → reprocessamento).

Correção (já aplicada no código): só propagar o cancelamento quando `stoppingToken.IsCancellationRequested`; caso contrário, tratar como falha transitória. Ver `QueueConsumerService` e `SendWithRetry`.

### Vazão colapsa (~0,4 msg/s) e o job nunca completa

Dois sintomas correlatos, ambos corrigidos no código:
1. **Classificação errada de status** — logs mostram `transitória: 0 ... 'BadRequest'. Abandon.` O status saía `0` porque o `ErrorResponseException` do Bot guarda o código em `Response.StatusCode`. `BotHttpStatus.ExtractStatus` agora lê esse campo → `400` vira permanente (Complete) em vez de `Abandon` infinito.
2. **Sends lentos prendendo slots** — logs com `The request was canceled due to the configured HttpClient.Timeout of 100 seconds`. Cada envio lento prendia um slot de concorrência por 100s. `BotConnectorHttpClientFactory` aplica `Worker:SendTimeout` (default 20s); reduza-o ainda mais via `worker.sendTimeout` no `values.yaml` se necessário.

### Mensagens não somem da fila apesar do worker rodar

Provavelmente a msg está sendo lida e reentregue por causa de exception transient (o worker faz `Abandon` no PeekLock). Após exceder `maxDeliveryCount` na fila do Service Bus, o broker move a mensagem para a **DLQ nativa** (`$DeadLetterQueue`) — não há poison queue manual.

```bash
kubectl -n teams-msgs logs deployment/teams-msgs-worker --tail=100 | grep -iE 'abandon|deadletter'
# inspecione a DLQ nativa da fila send-messages (subfila $DeadLetterQueue) via Service Bus Explorer no portal
```

## Logs / Custo

### Log Analytics em `OverQuota`

Capacidade atingida do limite diário (configuramos 25 MB/dia em `log-<seu-workspace>`). Reinício diário às 09:00 UTC. Para aumentar:

```bash
az monitor log-analytics workspace update -g rg-<seu-rg> -n log-<seu-workspace> --quota 1
```

Para reduzir ingest: subir `Logging:LogLevel:Default` para `Warning` ou `Error` no `ConfigMap`.

### `az aks command invoke` falha com `UnicodeEncodeError: 'charmap'`

Bug do `colorama` em Windows quando log tem emoji (U+1F916, etc). Workarounds (em ordem de simplicidade):

1. Filtre a linha ofensiva: `... | grep -v '🤖'`
2. `chcp 65001 && $env:PYTHONIOENCODING='utf-8'`
3. Remova emojis dos logs da aplicação
4. Use `--query "logs" -o tsv` (parece evitar o caminho do colorama)
