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

## Runtime / Workload Identity

### `403 AuthorizationPermissionMismatch` no Table Storage

Pod autenticou OK no Entra (Workload Identity), mas o UAMI não tem RBAC data plane. Adicione `Storage Table Data Contributor` + `Storage Queue Data Contributor` no UAMI sobre o Storage Account.

### `AADSTS700213: No matching federated identity record found`

A SA do KEDA operator (`system:serviceaccount:kube-system:keda-operator`) precisa de federated credential própria no UAMI — não basta a da API/worker. Aplique o módulo `federation-keda.bicep`.

Verifique também:
- SA do pod anotado com `azure.workload.identity/client-id: <UAMI_CLIENT_ID>`
- Pod labelado com `azure.workload.identity/use: "true"`

### `403 do meu user via az storage` mesmo sendo Owner da subscription

Owner cobre management plane, não data plane. Atribua explicitamente `Storage Table Data Contributor` / `Storage Queue Data Contributor` ao seu user.

## Ingress / TLS

### `504 Gateway Timeout` em `/api/send` com fan-out grande

O timeout default do NGINX Ingress era 60s. Mesmo problema acontece em qualquer ingress sem timeout estendido. No Istio Gateway (atual), o `HTTPRoute` já tem:

```yaml
timeouts:
  request: 600s
  backendRequest: 600s
```

Pattern de produção: refatore o fan-out para `BackgroundService` + `Channel<T>` e retorne 202 imediato. A API só registra o job e enfileira em background.

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

Quase sempre Workload Identity quebrada (ver acima). Logs do operator:
```bash
kubectl -n kube-system logs deployment/keda-operator --tail=50
```

### Worker pods reiniciando após processar lote grande de erros

Sintoma na PoC: 10 workers processando 30k+ msgs com `BadRequest` tiveram 3-4 restarts em 15min. Causas prováveis: OOM (limit padrão 512 MiB) e/ou exception não tratada no caminho do `ContinueConversationAsync` para refs inválidas.

Mitigações: bumpar `worker.resources.limits.memory` para `1Gi` no `values.yaml` e revisar logs para classificar a exception.

### Mensagens não somem da fila apesar do worker rodar

Provavelmente a msg está sendo lida + reentregue por causa de exception transient (Storage Queue redelivery). Após `DequeueCount > MaxDequeueCount` (default 5), nosso `QueueConsumerService.SendToPoisonAsync` move para `send-messages-poison`.

```bash
kubectl -n teams-msgs logs deployment/teams-msgs-worker --tail=100 | grep -i poison
az storage message peek -q send-messages-poison --account-name <sa> --auth-mode login
```

## Logs / Custo

### Log Analytics em `OverQuota`

Capacidade atingida do daily cap (configuramos 25 MB/dia no `log-tmd-poc`). Reset diário às 09:00 UTC. Para subir:

```bash
az monitor log-analytics workspace update -g rg-tmd-poc -n log-tmd-poc --quota 1
```

Para reduzir ingest: subir `Logging:LogLevel:Default` para `Warning` ou `Error` no `ConfigMap`.

### `az aks command invoke` falha com `UnicodeEncodeError: 'charmap'`

Bug do `colorama` em Windows quando log tem emoji (U+1F916, etc). Workarounds (em ordem de simplicidade):

1. Filtre a linha ofensiva: `... | grep -v '🤖'`
2. `chcp 65001 && $env:PYTHONIOENCODING='utf-8'`
3. Remova emojis dos logs da aplicação
4. Use `--query "logs" -o tsv` (parece evitar o caminho do colorama)
