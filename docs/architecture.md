# Arquitetura

## Visão geral

```
┌────────────────────────┐
│  Cliente (curl / app)  │
└──────────┬─────────────┘
           │ POST /api/send (x-api-key)
           │ GET  /api/jobs/{id}
           ▼
┌───────────────────────────────────┐         ┌──────────────────────┐
│  Ingress (Service LoadBalancer)   │◀────────│  Microsoft Teams Bot │
└──────────┬────────────────────────┘  /api/  │   Framework          │
           │                          messages└──────────┬───────────┘
           ▼                                              │
┌─────────────────────────┐  Workload Identity            │
│ teams-msgs-api (Pod)    │──────────┐                    │
│  ASP.NET Core MinAPI    │          │                    │
│  - MapBotEndpoint       │          ▼                    │
│  - MapSendEndpoint      │   ┌────────────────┐          │
│  - MapJobsEndpoint      │   │ Storage Queue  │          │
│  - MapHealthEndpoints   │   │  send-messages │          │
└─────────────────────────┘   └───────┬────────┘          │
                                      │ KEDA              │
                                      │ azure-queue       │
                                      ▼ scaler 0..10      │
                          ┌────────────────────────┐      │
                          │ teams-msgs-worker (Pod)│      │
                          │  BackgroundService     │      │
                          │  - resolve msg cache   │      │
                          │  - SentMarkStore claim │      │
                          │  - ContinueConversation│──────┘
                          │  - Send w/ retry       │
                          └──────────┬─────────────┘
                                     │ Istio EnvoyFilter
                                     │ local_ratelimit (token bucket)
                                     ▼
                          ┌────────────────────────┐
                          │ Bot Framework Channel  │
                          └──────────┬─────────────┘
                                     ▼
                          ┌────────────────────────┐
                          │ Microsoft Teams        │
                          │  (1:1 chat com user)   │
                          └────────────────────────┘
```

## Data plane (Table Storage)

| Tabela | PartitionKey | RowKey | Conteúdo | Substitui |
|---|---|---|---|---|
| `conversationrefs` | `refs` | `base64url(conversationId)` | `refJson` (JSON serializado do `ConversationReference`) | mesma do TS |
| `jobs` | `jobs` | `jobId` (GUID) | `total`/`sent`/`failed`/`status`/`messageType`/`message`/`errors` | Redis HMSET + HINCRBY |
| `sentmarks` | `jobId` | `md5(refRowKey)_r{repeatIndex}` | `claimedAt` | dedup `messageId` do Service Bus |

### Counters atômicos sem Redis

`TableJobTracker` faz `read-modify-write` com `If-Match` (ETag) +
Polly retry em 412 Precondition Failed. Em alta concorrência o retry
exponencial absorve as colisões; a contenção fica concentrada na
ordem de poucos updates/segundo por `jobId`.

### Idempotência sem dedup nativa

`SentMarkStore` faz `AddEntity` antes do envio real. Se a inserção
falhar com 409 Conflict → outra réplica já enviou aquela combinação
`(jobId, refRowKey, repeatIndex)` → no-op silencioso e completa a fila.

## Control / observabilidade

- **Container Insights** (Log Analytics workspace `log-tmd-poc`) coleta
  stdout/stderr de todos os pods e métricas do nó.
- **AKS managed KEDA add-on** observa profundidade da Storage Queue e
  escala o Deployment do worker.
- **AKS managed Istio add-on** injeta sidecars Envoy e aplica
  `local_ratelimit` no egress do worker para o Bot Framework.

## Workload Identity

```
UAMI (id-tmd-poc-app)
  │
  ├── federated to system:serviceaccount:teams-msgs:teams-msgs-api
  ├── federated to system:serviceaccount:teams-msgs:teams-msgs-worker
  │
  └── role assignments:
        ├── Storage Table Data Contributor on sttmd...
        └── Storage Queue Data Contributor on sttmd...
```

Os pods marcados `azure.workload.identity/use=true` recebem um token
projetado que o `DefaultAzureCredential` (Azure SDK) usa para obter
um access token sem connection string ou secret.

## Diferenças críticas vs versão TS

| Categoria | TS | .NET PoC |
|---|---|---|
| Tamanho máx. mensagem | 256KB (SB) | 64KB (Storage Queue) |
| Dedup | `messageId` nativo SB | `sentmarks` insert-or-conflict |
| DLQ | SB DLQ automática | `send-messages-poison` (manual, `DequeueCount > 5`) |
| Rate limit | Redis Lua bucket global | Envoy `local_ratelimit` por pod |
| Counters | Redis HINCRBY (atômico) | Table Storage ETag + Polly retry |
| Cache msg | Redis HMSET | `IMemoryCache` local 5min |
| Scale-to-zero | ACA KEDA | AKS KEDA (control-plane sempre on) |
| Auth ao Storage | Connection string | Workload Identity (sem secret) |
