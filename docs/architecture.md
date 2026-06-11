# Arquitetura

Documento detalhado da stack `.NET 8 + AKS + Storage Queue + Table Storage + Istio + Workload Identity`.

## Visão geral

```mermaid
graph LR
    Client[Aplicação chamadora]
    Users[👥 Usuários do Teams]
    BF[Bot Framework]

    subgraph "AKS Cluster: aks-tmd-poc"
        subgraph "Namespace aks-istio-ingress"
            Gateway[Istio Gateway<br/>Public LB<br/>HTTP→443 TLS]
        end
        subgraph "Namespace teams-msgs"
            API[teams-msgs-api<br/>HPA 1..5]
            Worker[teams-msgs-worker<br/>KEDA 0..10]
            EF[EnvoyFilter<br/>local_ratelimit]
        end
        subgraph "Namespace cert-manager"
            CM[cert-manager v1.20<br/>gatewayHTTPRoute solver]
        end
    end

    Refs[(conversationrefs)]
    Jobs[(jobs - ETag)]
    Marks[(sentmarks)]
    SQ[(send-messages)]
    Poison[(send-messages-poison)]
    AzBot[Azure Bot]
    UAMI[UAMI<br/>3 federations]

    Client -->|POST /api/send| Gateway
    Gateway --> API
    API --> Refs
    API --> Jobs
    API -->|fan-out| SQ
    SQ -->|KEDA| Worker
    Worker --> Marks
    Worker --> Jobs
    Worker -->|ContinueConversation| EF
    EF --> BF
    BF -->|1:1| Users
    Worker -->|DequeueCount>5| Poison
    Users -.-> AzBot
    AzBot -.-> Gateway
    UAMI -.-> API
    UAMI -.-> Worker
```

## Componentes por camada

### Ingress / TLS

- **AKS managed Istio mesh** (`az aks mesh enable`) — sidecar injection no namespace `teams-msgs` via label `istio-injection: enabled`.
- **AKS managed Istio ingress gateway external** (`az aks mesh enable-ingress-gateway`) — provê um `Deployment` + `Service LoadBalancer` no namespace `aks-istio-ingress`.
- **Kubernetes Gateway API CRDs** (v1.2.0) — `Gateway`, `HTTPRoute`, etc. Padrão K8s sucessor do `Ingress`.
- **cert-manager v1.20.2** + **ClusterIssuer** `letsencrypt-prod` com solver `http01.gatewayHTTPRoute`. Emite cert para a `Certificate` `teams-msgs-gw-tls` em `aks-istio-ingress`. `Gateway` referencia esse Secret para terminação TLS.
- DNS label `teams-msgs-dotnet` atribuído ao Public IP do LB do Istio Gateway → resolve para `teams-msgs-dotnet.brazilsouth.cloudapp.azure.com`.

### Compute

- **`teams-msgs-api` Deployment** — pod único por default. `HPA` por CPU (1..5 réplicas, target 70%). Service tipo `ClusterIP` (Istio Gateway faz o ingress).
- **`teams-msgs-worker` Deployment** — `replicas: 0` no manifest. `KEDA ScaledObject` com trigger `azure-queue` (sem connection string — usa `TriggerAuthentication podIdentity provider=azure-workload`). Escala 0..10.
- **`HPA` keda-hpa-teams-msgs-worker** — gerenciado pelo KEDA, métrica externa `s0-azure-queue-send-messages`.

### Data plane (Table Storage)

| Tabela | PartitionKey | RowKey | Conteúdo | Substitui |
|---|---|---|---|---|
| `conversationrefs` | `refs` | `base64url(conversationId)` | `refJson` (JSON serializado do `ConversationReference`) | mesma da versão TS |
| `jobs` | `jobs` | `jobId` (GUID) | `total`, `sent`, `failed`, `status`, `messageType`, `message`, `errors`, timestamps | Redis HMSET + HINCRBY |
| `sentmarks` | `jobId` | `md5_hex(refRowKey)_r{repeatIndex}` | `claimedAt` | dedup `messageId` do Service Bus |

#### Counters atômicos sem Redis

`TableJobTracker` faz `read-modify-write` com `If-Match` (ETag) + `Polly` retry em 412 Precondition Failed. Em alta concorrência o retry exponencial absorve as colisões.

```csharp
await pipeline.ExecuteAsync(async token => {
    var response = await table.GetEntityAsync<TableEntity>(pk, rk, ct: token);
    var entity = response.Value;
    mutate(entity);                 // ex.: entity["sent"] = sent + 1
    await table.UpdateEntityAsync(entity, entity.ETag,
        TableUpdateMode.Replace, token);    // If-Match implícito
});
```

#### Idempotência sem dedup nativa

`SentMarkStore.TryClaimAsync` faz `AddEntity` antes do envio. Se 409 Conflict → outro pod já entregou → completa fila silenciosamente.

### Queue (Storage Queue)

- `send-messages` (worker consome)
- `send-messages-poison` (manual DLQ — `QueueConsumerService.SendToPoisonAsync` quando `DequeueCount > MaxDequeueCount`)

Limites relevantes:
- 64 KB por mensagem (após base64 encoding); AdaptiveCards grandes não cabem
- 32 mensagens por `ReceiveMessages` chamada
- Sem dedup nativa, sem DLQ nativa

### Identidade / Auth

```
UAMI: id-tmd-poc-app
  │
  ├── federated to: system:serviceaccount:teams-msgs:teams-msgs-api
  ├── federated to: system:serviceaccount:teams-msgs:teams-msgs-worker
  ├── federated to: system:serviceaccount:kube-system:keda-operator
  │
  └── role assignments:
        ├── Storage Table Data Contributor on sttmd…
        └── Storage Queue Data Contributor on sttmd…
```

Pods marcados `azure.workload.identity/use=true` recebem token projetado. `DefaultAzureCredential` (Azure SDK) usa esse token para obter access token sem secret.

### Rate limit (Envoy local_ratelimit)

`EnvoyFilter` aplicado ao sidecar OUTBOUND do worker. Token bucket no proxy:

```yaml
configPatches:
  - applyTo: HTTP_FILTER
    match:
      context: SIDECAR_OUTBOUND
    patch:
      value:
        name: envoy.filters.http.local_ratelimit
        typed_config:
          token_bucket:
            max_tokens: 50
            tokens_per_fill: 50
            fill_interval: 1s
```

**Limitação**: limite é por pod. Global aproximado = `maxReplicaCount × tokensPerFill / fillInterval`.

### Observabilidade

- **Container Insights** (Log Analytics workspace `log-tmd-poc`) coleta stdout/stderr de todos os pods e métricas do nó.
- **Daily cap 25 MB** configurado para conter custo durante PoC. Status visível em `workspaceCapping.dataIngestionStatus`.
- **KEDA operator logs** em `kube-system/keda-operator` mostram quando o azure-queue scaler consegue ou não pegar queue length.

## Diferenças críticas vs versão TS

| Categoria | TS | .NET PoC |
|---|---|---|
| Tamanho máx. mensagem | 256KB (SB) | 64KB (Storage Queue) |
| Dedup | `messageId` nativo SB | `sentmarks` insert-or-conflict |
| DLQ | SB DLQ automática | `send-messages-poison` (manual, `DequeueCount > 5`) |
| Rate limit | Redis Lua bucket global | Envoy `local_ratelimit` por pod |
| Counters | Redis HINCRBY (atômico) | Table Storage ETag + Polly retry |
| Cache msg | Redis HMSET | `IMemoryCache` local 5min |
| Scale-to-zero compute | ACA KEDA | AKS KEDA (control-plane sempre on) |
| Ingress | ACA built-in | Istio Gateway + cert-manager Let's Encrypt |
| Auth ao Storage | Connection string | Workload Identity (sem secret) |
| Auth ao Bot Framework | App ID + password | Mesmo (SingleTenant App Registration) |
