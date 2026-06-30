# 📨 Teams Proactive Messaging — .NET 8 / AKS / Service Bus + Redis

[![ORCID](https://img.shields.io/badge/ORCID-0009--0006--0765--4201-A6CE39?logo=orcid&logoColor=white)](https://orcid.org/0009-0006-0765-4201)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Azure](https://img.shields.io/badge/Cloud-Azure-0078D4?logo=microsoftazure&logoColor=white)](#)
[![.NET](https://img.shields.io/badge/.NET-8.0--LTS-512BD4?logo=dotnet&logoColor=white)](#)
[![AKS](https://img.shields.io/badge/Compute-AKS-326CE5?logo=kubernetes&logoColor=white)](#)
[![Last commit](https://img.shields.io/github/last-commit/EdneiMonteiro/teams_msgs_dotnet)](https://github.com/EdneiMonteiro/teams_msgs_dotnet/commits)

Port em **.NET 8** da demo [`teams_msgs`](https://github.com/EdneiMonteiro/teams_msgs) (Node/TS), mantendo os mesmos componentes Azure: **Azure Service Bus** (fila), **Redis** (counters, índice de refs, cache e rate limit) e **Table Storage** (refs duráveis). A diferença é o compute: **AKS** + **KEDA** + **Istio** no lugar do ACA. Mesma proposta: envio de mensagens proativas 1:1 em massa via Microsoft Teams (Bot Framework), respeitando rate limits.

> ⚠️ Este repositório é uma **demo / prova de conceito**. Antes de usar em produção, revise: segurança, escalabilidade, observabilidade, custos e conformidade. Veja [DISCLAIMER.md](./DISCLAIMER.md) e [SUPPORT.md](./SUPPORT.md).

---

## ✨ Highlights da versão atual

| Capacidade | Como |
|---|---|
| 🚀 Fan-out assíncrono | Streaming generator das refs (Table Storage) + envio em **batches** do Service Bus no enqueue |
| 🎯 Rate limit | **Token bucket Lua global no Redis** no hot path do worker (limite total entre todas as réplicas, como o repo original) |
| 🎴 Adaptive Cards | `POST /api/send` aceita texto **ou** `{ type:"AdaptiveCard", content:<card> }` |
| 🔁 Idempotência | **Dedup nativa do Service Bus** (Standard) via `messageId` determinístico `{jobId}:{md5(rowKey)}:{repeat}` |
| 📊 Counters em Redis | `HINCRBY` atômico no hash `job:{id}` (sent/failed/total), com cache do payload da mensagem e TTL 24h |
| 🩺 Health probes | `/healthz` (liveness) + `/readyz` (Redis + Table Storage + Service Bus) |
| 🔒 Hardened auth | `x-api-key` com `CryptographicOperations.FixedTimeEquals` |
| 🔑 Connection strings | Storage, Service Bus e Redis autenticam por connection string em K8s Secret (Workload Identity removido) |
| 🛡️ HTTPS Let's Encrypt | cert-manager 1.20 + Gateway API + solver `gatewayHTTPRoute` (sem NGINX) |
| 🧪 Testes | xUnit (38 testes) cobrindo validator, retry/cancelamento, status do Bot, message-id, token bucket, safe row key |
| 🏗️ IaC | Bicep subscription-scope (AKS + Istio + KEDA + Storage + Service Bus + Redis + Log Analytics + Bot); ACR compartilhado em RG externo |
| 🚢 Deploy | Helm chart com KEDA `ScaledObject` (`azure-servicebus`), HPA CPU para API |

---

## Por que essa arquitetura?

Em cenários de comunicação corporativa em massa via Teams, as alternativas comumente tentadas têm comportamentos diferentes:

| Abordagem | Realidade |
|---|---|
| **Power Automate** | Limites por licença e conector, não projetado para processamento massivo; pode sofrer throttling e latência. |
| **Microsoft Graph** | Possui throttling multi-nível (app, tenant, user); adequado para integração, não para broadcast massivo. |
| **Bot Framework** | Canal nativo para notificações e proactive messaging; melhor suporte para escala, com rate limits dinâmicos. |

Esta demo **não burla rate limits** — usa o canal certo. O envio depende do Teams App estar instalado para cada usuário (org-wide via Admin Center), o que faz o bot capturar uma `conversationReference` por usuário e usá-la depois para mandar mensagens 1:1 sem nova interação.

---

## Diferenças vs. o repo original (TypeScript)

Mesma lógica, componentes Azure diferentes:

| Camada | `teams_msgs` (TS) | `teams_msgs_dotnet` |
|---|---|---|
| Runtime | Node 20 + TypeScript | **.NET 8 LTS** (ASP.NET Core Minimal API + Worker Service) |
| Fila | Azure Service Bus (queue `send-messages`) | **Azure Service Bus** (Standard) |
| Cache / counters / rate-limit | Azure Cache for Redis (HMSET + HINCRBY + Lua bucket) | **Azure Managed Redis** (HMSET + HINCRBY + Lua bucket) |
| Compute | Azure Container Apps + KEDA | **AKS + KEDA azure-servicebus + HPA CPU** |
| Ingress | (não tinha — ACA built-in) | **AKS managed Istio + Gateway API + cert-manager + Let's Encrypt** |
| Auth aos serviços | Connection string | **Connection string** (Storage, Service Bus, Redis em K8s Secret) |
| Idempotência | `messageId` dedup do SB | **`messageId` dedup do SB** (Standard) |
| DLQ | SB DLQ automática | **SB DLQ automática** (`maxDeliveryCount`) |
| Limite por msg | 256 KB (SB) | **256 KB** (Service Bus Standard) |
| IaC | (livre escolha) | Bicep subscription-scope |
| CD | (livre escolha) | Helm chart + GitHub Actions OIDC (manual / `workflow_dispatch`) |

---

## Índice

- [Arquitetura](#arquitetura)
- [Componentes Azure](#componentes-azure)
- [Fluxo de funcionamento](#fluxo-de-funcionamento)
- [Endpoints da API](#endpoints-da-api)
- [Rate limit — token bucket Redis](#rate-limit--token-bucket-redis)
- [Idempotência sem dedup nativa](#idempotência-sem-dedup-nativa)
- [Counters em Redis](#counters-em-redis)
- [Segurança](#segurança)
- [Estrutura do projeto](#estrutura-do-projeto)
- [Testes](#testes)
- [Como iniciar (dev local)](#como-iniciar-dev-local)
- [Deploy em Azure](#deploy-em-azure)
- [Deploy do Teams App](#deploy-do-teams-app)
- [Load test](#load-test)
- [Troubleshooting](#troubleshooting)

---

## Arquitetura

### Visão geral

```mermaid
graph LR
    Client[Aplicação chamadora]
    Users[👥 Usuários do Teams]
    BF[Bot Framework]

    subgraph "AKS Cluster: aks-<seu-cluster>"
        subgraph "Namespace aks-istio-ingress"
            Gateway[Istio Gateway<br/>Public LB<br/>HTTP→443 TLS<br/>Let's Encrypt]
        end
        subgraph "Namespace teams-msgs"
            API[teams-msgs-api<br/>Deployment<br/>HPA 1..5 CPU]
            Worker[teams-msgs-worker<br/>Deployment<br/>KEDA 0..10 azure-servicebus]
        end
        subgraph "Namespace cert-manager"
            CM[cert-manager<br/>gatewayHTTPRoute solver]
        end
    end

    subgraph "Data plane"
        Refs[(Table Storage<br/>conversationrefs)]
        RedisDB[(Redis<br/>counters · refs index<br/>cache · rate bucket)]
        SB[(Service Bus<br/>send-messages)]
    end

    subgraph "Support / control plane"
        AzBot[Azure Bot<br/>bot-<seu-bot>]
        ACR[Container Registry<br/>compartilhado · RG externo]
        Logs[Log Analytics<br/>log-<seu-workspace><br/>25MB/dia cap]
    end

    Client -->|POST /api/send<br/>x-api-key| Gateway
    Client -->|GET /api/jobs/:id| Gateway
    Gateway --> API
    API -->|streamRefs| Refs
    API -->|createJob HMSET| RedisDB
    API -->|SendMessageBatch| SB
    SB -->|KEDA scaler| Worker
    Worker -->|getMessage / counters| RedisDB
    Worker -->|acquire token| RedisDB
    Worker -->|ContinueConversation| BF
    BF -->|1:1| Users
    Worker -->|deleteEntity em 403/410| Refs
    Worker -->|Complete / DeadLetter| SB

    Users -.->|conversationUpdate| AzBot
    AzBot -.->|POST /api/messages| Gateway
    Gateway -.-> API
    API -.->|upsertEntity| Refs

    ACR -.->|imagens| API
    ACR -.->|imagens| Worker
    API -.->|logs| Logs
    Worker -.->|logs| Logs
```

**Princípios:**

- **Redis = caminho quente**: counters atômicos (`HINCRBY`), índice ativo de refs (`SCARD` para contagem O(1)), cache do payload da mensagem e token bucket global (rate limit Lua).
- **Table Storage = durabilidade dos refs**: fonte da verdade dos `conversationReferences` (varredura por partition no fan-out). Idempotência é nativa do Service Bus.
- **Service Bus = fan-out**: dedup nativa (`messageId`), DLQ automática (`maxDeliveryCount`), batches no enqueue. Limite de 256 KB por msg.
- **Istio Gateway + cert-manager = entrada única**: HTTPS terminado pelo gateway com cert Let's Encrypt automático. Substitui NGINX Ingress completamente.
- **AKS = compute**: API com `HPA` por CPU + Worker com `KEDA azure-servicebus` 0→10. Control plane Free tier; 2 nodes `Standard_D2ds_v5` (OS disk efêmero).
- **Connection strings**: API e Worker autenticam em Storage, Service Bus e Redis via connection string montada de um K8s Secret.

### Detalhe — caminho de uma mensagem

```mermaid
flowchart LR
    M1["/api/send<br/>POST"] --> M2["MessageValidator"]
    M2 -->|"texto / card"| M3["RedisJobTracker.CreateAsync<br/>HMSET job (total=0)"]
    M3 --> M4["for each ref<br/>streamRefs Table"]
    M4 --> M5["buffer 500<br/>QueueMessageBody"]
    M5 --> M6["EnqueueBatchAsync<br/>(Service Bus batch)"]
    M6 --> M7["UpdateTotalAsync<br/>(reconcilia total)"]
    M7 --> M8["KEDA escala worker<br/>0→10 réplicas"]
    M8 --> M9["BackgroundService<br/>ReceiveMessages (PeekLock)"]
    M9 --> M11["CloudAdapter.ContinueConversation"]
    M11 --> M12["acquire token<br/>(token bucket Redis)"]
    M12 --> M13{"outcome"}
    M13 -->|"200"| M14["IncrementSentAsync<br/>HINCRBY + Complete"]
    M13 -->|"403/410"| M15["RemoveByRowKeyAsync<br/>IncrementFailedAsync"]
    M13 -->|"429/5xx"| M16["Abandon<br/>(broker reentrega → DLQ)"]
    style M3 fill:#FFE082,color:#000
    style M12 fill:#FFAB91,color:#000
    style M14 fill:#A5D6A7,color:#000
    style M15 fill:#EF9A9A,color:#000
```

---

## Componentes Azure

| Recurso | Nome na demo | SKU | Função |
|---|---|---|---|
| App Registration | associado ao Azure Bot | Free | Identidade do bot (SingleTenant) |
| Azure Bot | `bot-<seu-bot>` | F0 | Registro Bot Framework + canal Teams |
| AKS | `aks-<seu-cluster>` | Base / Free control plane | Cluster gerenciado; nodes `Standard_D2ds_v5` x2, OS disk efêmero |
| AKS add-on KEDA | nativo do cluster | — | Escala worker pela profundidade da fila do Service Bus |
| AKS add-on Istio | `mesh enable` + `mesh enable-ingress-gateway` | — | Service mesh + Istio Gateway externo para ingress |
| Gateway API CRDs | v1.2.0 | — | Padrão Kubernetes Gateway (substitui Ingress) |
| cert-manager | jetstack/cert-manager v1.20.2 | — | Let's Encrypt automático via `gatewayHTTPRoute` solver |
| Storage Account | `sttmd…` | Standard_LRS | Table `conversationrefs` (refs duráveis) |
| Service Bus | `sb-tmd…` | Standard | Fila `send-messages` (dedup nativa + DLQ) |
| Azure Managed Redis | `redis-tmd…` | Balanced_B0 | Counters, índice de refs, cache de msg, token bucket |
| Container Registry | compartilhado (RG externo) | Basic | Imagens API + Worker (fora do RG da PoC) |
| Log Analytics | `log-<seu-workspace>` | PerGB2018 (cap 25 MB/dia) | Container Insights do AKS |

---

## Fluxo de funcionamento

### Fase 1 — Registro de usuários (passivo)

```mermaid
sequenceDiagram
    participant Admin as Teams Admin
    participant Teams as Microsoft Teams
    participant AzBot as Azure Bot Registration
    participant GW as Istio Gateway
    participant API as teams-msgs-api
    participant Table as Table Storage (conversationrefs)

    Admin->>Teams: Deploy do app (org-wide)
    loop para cada usuário
        Teams->>AzBot: conversationUpdate / install event
        AzBot->>GW: POST https://FQDN/api/messages
        GW->>API: HTTP /api/messages (TLS termina aqui)
        API->>API: extrai conversationReference
        API->>Table: UpsertEntity (PartitionKey="refs")
    end
    Note over Table: N refs duráveis<br/>RowKey = base64url(conversationId)
```

### Fase 2 — Disparo do comunicado

```mermaid
sequenceDiagram
    participant Client as App chamadora
    participant GW as Istio Gateway
    participant API as teams-msgs-api
    participant Refs as Table conversationrefs
    participant Redis as Redis (job + bucket)
    participant SB as Service Bus
    participant Worker as Worker (KEDA 0→N)
    participant Bot as Bot Framework
    participant User as 👤

    Client->>GW: POST /api/send (x-api-key)<br/>{message | AdaptiveCard, repeat?}
    GW->>API: HTTPS
    API->>API: MessageValidator.Validate()
    API->>Redis: HMSET job (status=queued)
    Note over API,Refs: IAsyncEnumerable streaming<br/>(não materializa array)
    loop buffer de 500 refs (Table)
        API->>SB: EnqueueBatchAsync (ServiceBusMessageBatch)<br/>messageId determinístico
    end
    API->>Redis: UpdateTotal + status=processing
    API-->>GW: 202 {jobId, total, statusUrl}
    GW-->>Client: 202

    Note over SB,Worker: KEDA detecta msgCount<br/>→ escala 0..10

    par workers em paralelo
        Worker->>SB: ReceiveMessages (PeekLock)
        Worker->>Redis: getMessage (cache local 5min)
        Worker->>Redis: acquire token (bucket Lua)
        Worker->>Bot: ContinueConversationAsync(ref, msg)
        Bot->>User: mensagem 1:1
        alt sucesso 200
            Worker->>Redis: IncrementSent (HINCRBY)
            Worker->>SB: Complete
        else 403/410
            Worker->>Refs: DeleteEntity (rowKey)
            Worker->>Redis: IncrementFailed
            Worker->>SB: Complete
        else 429/5xx transiente
            Worker->>SB: Abandon (reentrega → DLQ)
        end
    end

    Client->>GW: GET /api/jobs/:id
    GW->>API: HTTPS
    API->>Redis: HGETALL job
    API-->>Client: {progress, sent, failed, status}
```

### Fase 3 — Tratamento de erros

```mermaid
flowchart TD
    A[Worker recebe msg do Service Bus] --> C{ContinueConversation<br/>+ token bucket Redis}
    C -->|✅ 200| D[IncrementSent<br/>Complete]
    C -->|⚠️ 429| E[Sleep Retry-After] --> C
    C -->|⚠️ 5xx| F[Backoff exponencial] --> C
    C -->|❌ 403/410| G[Remove ref<br/>IncrementFailed<br/>Complete]
    C -->|❌ 4xx outro| H[IncrementFailed<br/>Complete permanent]
    C -->|❌ transiente<br/>após retries| I[Abandon<br/>broker reentrega]
    I -->|DeliveryCount > max| J[DLQ nativa<br/>do Service Bus]
    style D fill:#A5D6A7,color:#000
    style G fill:#FFCC80,color:#000
    style H fill:#FFCC80,color:#000
    style I fill:#EF9A9A,color:#000
    style J fill:#EF9A9A,color:#000
```

#### Confiabilidade do envio (worker)

Três detalhes garantem que a vazão não colapse sob carga (validados no teste de 50k):

- **Classificação de status correta** — o Bot Framework lança `ErrorResponseException`, que expõe o código HTTP em `Response.StatusCode` (não no topo). `BotHttpStatus.ExtractStatus` lê esse campo; sem isso, um `400` viraria status `0` → seria tratado como transitório → `Abandon` infinito.
- **Timeout ≠ shutdown** — `TaskCanceledException` (timeout do HttpClient) deriva de `OperationCanceledException`. O worker só propaga o cancelamento quando o `stoppingToken` foi realmente cancelado (shutdown); um timeout vira falha transitória. Sem essa distinção, o timeout escapava dos `catch` e **derrubava o host** do worker (exit 0 → restart → perda de locks → reprocessamento).
- **Timeout curto no conector** — `BotConnectorHttpClientFactory` injeta um `HttpClient` com `Worker:SendTimeout` (default **20s**) no conector do Bot. O default de 100s prenderia um slot de concorrência por mensagem lenta, estrangulando a vazão.

| Parâmetro (values.yaml) | Default | Significado |
|---|---:|---|
| `worker.maxConcurrent` | `10` | Envios simultâneos por pod (SemaphoreSlim) |
| `worker.pollBatchSize` | `32` | Mensagens por `ReceiveAsync` (PeekLock) |
| `worker.sendTimeout` | `00:00:20` | Timeout do HttpClient do conector do Bot |
| `worker.messageCacheTtl` | `00:05:00` | TTL do cache L1 (`IMemoryCache`) do payload |

---

## Endpoints da API

| Método | Path | Auth | Descrição |
|---|---|---|---|
| POST | `/api/messages` | Bot Framework token (JWT) | Endpoint do Bot Framework (configure como Messaging Endpoint no Azure Bot) |
| POST | `/api/send` | `x-api-key` | Enfileira N mensagens no Service Bus (batches) e retorna `202 Accepted` |
| GET | `/api/jobs/{id}` | `x-api-key` | Progresso do job (Table Storage) |
| GET | `/api/status` | `x-api-key` | Contagem de usuários registrados |
| GET | `/healthz` | — | Liveness simples |
| GET | `/readyz` | — | Readiness (Redis + Table Storage + Service Bus acessíveis) |

### `POST /api/send`

Aceita **texto** ou **Adaptive Card**.

**Texto:**
```http
POST /api/send
Content-Type: application/json
x-api-key: <API_KEY>

{
  "message": "📢 Comunicado importante para todos os colaboradores!",
  "repeat": 1
}
```

**Adaptive Card:**
```http
POST /api/send
Content-Type: application/json
x-api-key: <API_KEY>

{
  "message": {
    "type": "AdaptiveCard",
    "content": {
      "type": "AdaptiveCard",
      "version": "1.5",
      "body": [
        { "type": "TextBlock", "size": "Medium", "weight": "Bolder", "text": "Atualização" },
        { "type": "TextBlock", "text": "Conteúdo da mensagem.", "wrap": true }
      ],
      "actions": [
        { "type": "Action.OpenUrl", "title": "Saiba mais", "url": "https://exemplo.com" }
      ]
    }
  }
}
```

| Campo | Tipo | Obrigatório | Descrição |
|---|---|---|---|
| `message` | string \| object | sim | Texto OU `{ type:"AdaptiveCard", content:<card json> }` |
| `repeat` | int | não | Cópias por usuário (default `1`). Útil para testes controlados de stress; use com cautela. |

```json
HTTP/1.1 202 Accepted
{
  "jobId": "fde42c3cc1754f3c8ade21c4555797e2",
  "refs": 1,
  "repeat": 1,
  "total": 1,
  "enqueued": 1,
  "drops": 0,
  "messageType": "text",
  "status": "queued",
  "statusUrl": "/api/jobs/fde42c3cc1754f3c8ade21c4555797e2"
}
```

> ⚠️ **`repeat` multiplica `refs × repeat`** mensagens reais para o Bot Framework. Use com cuidado em produção — útil principalmente para load testing.
>
> O `202 Accepted` é retornado **depois** que a API termina o streaming das referências e enfileira as mensagens. O HTTPRoute do Istio Gateway tem `timeouts.request: 600s` para suportar fan-outs grandes; ainda assim, o padrão de produção recomendado é mover o fan-out para um `BackgroundService` + `Channel<T>` e retornar 202 imediatamente.

### `GET /api/jobs/{id}`

```json
{
  "jobId": "fde42c3cc1754f3c8ade21c4555797e2",
  "message": "📢 Comunicado importante para todos os colaboradores!",
  "messageType": "text",
  "total": 1,
  "sent": 1,
  "failed": 0,
  "status": "completed",
  "progress": 100,
  "createdAt": "2026-06-11T07:54:04+00:00",
  "updatedAt": "2026-06-11T07:54:13+00:00",
  "errors": []
}
```

| `status` | Significado |
|---|---|
| `queued` | Job criado, mensagens sendo enfileiradas |
| `processing` | Workers estão enviando |
| `completed` | Todas processadas (`sent + failed = total`) |
| `failed` | Falha no enqueue (recebido em 503 da API) |

---

## Rate limit — token bucket Redis

Idêntico ao repo original: um **token bucket Lua atômico no Redis**, **global** entre todas as réplicas do worker (não por pod). O worker adquire 1 token antes de cada envio ao Bot Framework:

```csharp
// worker hot path, antes de ContinueConversationAsync:
if (rateLimit.Enabled)
    await rateLimiter.AcquireAsync(ct);   // bloqueia c/ backoff+jitter até obter token
```

```lua
-- RedisTokenBucket: refill por tempo decorrido, consome 1 token se disponível
local elapsed = math.max(0, now_ms - last) / 1000.0
tokens = math.min(capacity, tokens + elapsed * rate)
if tokens >= 1 then tokens = tokens - 1; allowed = 1 end
```

Como todos os workers competem pela **mesma chave** (`ratelimit:bot`), o limite é o teto **total** de envios — independente de quantas réplicas o KEDA criou.

| Parâmetro (values.yaml) | Default | Significado |
|---|---:|---|
| `rateLimit.enabled` | `true` | Liga/desliga o token bucket |
| `rateLimit.capacity` | `50` | Burst máximo (tokens) |
| `rateLimit.ratePerSec` | `50` | Taxa sustentada (tokens/s) |
| `rateLimit.key` | `ratelimit:bot` | Chave Redis do bucket |

---

## Idempotência — dedup nativa do Service Bus

O Service Bus (Standard) faz **deduplicação nativa** por `messageId`. O produtor gera um `messageId` determinístico, então reentregas/duplicatas dentro da janela de detecção são descartadas pelo broker:

```csharp
// ServiceBusMessageId.Compute:
var messageId = $"{jobId}:{md5hex(rowKey)}:{repeatIndex}";
```

A fila é criada com `requiresDuplicateDetection = true` e janela `PT10M` (Bicep). Não há tabela `sentmarks` nem escrita extra por mensagem — a idempotência fica a cargo do broker.

---

## Counters em Redis

Contadores de job (`sent`, `failed`, `total`) ficam num hash Redis `job:{id}`, com `HINCRBY` atômico — igual ao repo original:

```csharp
// incremento atômico
var sent = await db.HashIncrementAsync($"job:{jobId}", "sent", 1);
// status derivado: (sent + failed) >= total ? "completed" : "processing"
```

- `HMSET` na criação do job + `EXPIRE` 24h.
- O **payload da mensagem** (texto/AdaptiveCard) também mora no hash → o worker resolve via Redis (com `IMemoryCache` local como L1).
- O **índice de refs** (`refs:active`, `SCARD`) dá contagem O(1) para `/api/status` e o total do job.

`HINCRBY` é atômico no servidor Redis, então não há contenção como num registro único de Table Storage — by design.

---

## Segurança

- `POST /api/send`, `GET /api/jobs/{id}`, `GET /api/status` exigem header `x-api-key` quando `Api:ApiKey` está definida.
- Comparação com `CryptographicOperations.FixedTimeEquals` (não vulnerável a timing attacks).
- Em **dev local**, deixar `Api:ApiKey` vazia desliga a checagem (log de warning ao iniciar).
- **Connection strings**: API e Worker autenticam em Storage, Service Bus e Redis via connection string lida de um `Secret` do Kubernetes (Workload Identity removido).
- **TLS terminado no Istio Gateway** com cert Let's Encrypt válido (auto-renew via cert-manager).
- Secrets sensíveis (`Bot:AppPassword`, `Api:ApiKey`, connection strings) ficam em `Secret` do Kubernetes; recomenda-se mover para Azure Key Vault + CSI driver em produção.

### Roles RBAC criadas pelo Bicep

| Identidade | Role | Scope |
|---|---|---|
| AKS kubelet identity | AcrPull | ACR compartilhado |

> Sem RBAC de data plane: os serviços usam connection string. O único role assignment é o `AcrPull` do kubelet no ACR compartilhado (módulo `acr-rbac.bicep`).

---

## Estrutura do projeto

```
teams_msgs_dotnet/
├── src/
│   ├── TeamsMsgs.Api/                # ASP.NET Core Minimal API
│   │   ├── Program.cs                # bootstrap + endpoints
│   │   ├── Auth/                     # ApiKeyEndpointFilter (FixedTimeEquals)
│   │   ├── Bot/                      # BotRegistration (CloudAdapter)
│   │   ├── Endpoints/                # Bot, Send, Jobs, Health
│   │   └── Hosting/                  # DI helpers
│   ├── TeamsMsgs.Worker/             # Worker Service (BackgroundService)
│   │   ├── Program.cs                # bootstrap
│   │   └── Hosting/
│   │       ├── QueueConsumerService.cs  # consome Service Bus → ContinueConversation
│   │       ├── BotHttpStatus.cs         # extrai status (Response.StatusCode) + Retry-After
│   │       └── BotConnectorHttpClientFactory.cs  # HttpClient do conector c/ timeout curto
│   └── TeamsMsgs.Shared/             # biblioteca compartilhada
│       ├── Validation/MessageValidator.cs
│       ├── Configuration/Options.cs
│       ├── Azure/AzureClientFactory.cs  # TableClient via connection string
│       ├── Redis/RedisConnection.cs     # ConnectionMultiplexer (StackExchange.Redis)
│       ├── Storage/ConversationRefStore.cs  # Table + índice Redis (SCARD)
│       ├── Jobs/RedisJobTracker.cs   # counters HINCRBY + cache de msg
│       ├── Messaging/ServiceBusSendQueue.cs       # produtor (batches)
│       ├── Messaging/ServiceBusSendQueueReceiver.cs  # consumidor (PeekLock + DLQ)
│       ├── Messaging/ServiceBusMessageId.cs       # messageId determinístico
│       ├── RateLimiting/RedisTokenBucket.cs       # token bucket Lua
│       ├── Sending/SendWithRetry.cs
│       └── Bot/ProactiveBot.cs
├── tests/
│   └── TeamsMsgs.Tests/              # xUnit (31 testes)
│       ├── MessageValidatorTests.cs  # validador (text/AdaptiveCard)
│       ├── SendWithRetryTests.cs     # retry (429/5xx/403/410/4xx)
│       ├── TokenBucketTests.cs       # token bucket (função pura)
│       ├── MessageIdTests.cs         # messageId determinístico
│       └── RowKeyTests.cs            # safe row key
├── deploy/
│   ├── bicep/
│   │   ├── main.bicep                # orchestrator subscription-scope
│   │   ├── main.bicepparam
│   │   └── modules/
│   │       ├── storage.bicep         # Table conversationrefs
│   │       ├── servicebus.bicep      # namespace Standard + fila (dedup + DLQ)
│   │       ├── redis.bicep           # Azure Managed Redis Balanced_B0
│   │       ├── acr-rbac.bicep        # AcrPull no ACR compartilhado (RG externo)
│   │       ├── aks.bicep
│   │       ├── loganalytics.bicep
│   │       └── bot.bicep
│   ├── helm/teams-msgs/              # chart: api, worker, configmap, secret,
│   │                                 # serviceaccount, scaledobject, hpa,
│   │                                 # namespace
│   ├── istio-gateway.yaml            # Gateway + HTTPRoute (Gateway API)
│   ├── clusterissuer-gw.yaml         # ClusterIssuer Let's Encrypt
│   └── istio-cert.yaml               # Certificate request
├── docker/
│   ├── Dockerfile.api                # mcr.microsoft.com/dotnet/aspnet:8.0-alpine
│   └── Dockerfile.worker             # mcr.microsoft.com/dotnet/aspnet:8.0-alpine
├── manifest/
│   ├── manifest.json                 # template Teams App
│   ├── color.png                     # 192×192
│   ├── outline.png                   # 32×32
│   ├── build.ps1                     # gera build/teams-msgs-dotnet-app.zip
│   └── build/                        # .zip ignorado pelo git
├── load_test/
│   ├── run-50k.js                    # insere refs fictícias + 1 job + limpeza
│   └── package.json
├── .github/workflows/
│   ├── ci.yml                        # build+test+lint (PR)
│   └── cd.yml                        # CD manual (workflow_dispatch): OIDC + ACR build + helm upgrade
├── docs/
│   ├── architecture.md
│   ├── deploy.md
│   └── troubleshooting.md
├── TeamsMsgs.sln
├── Directory.Build.props             # net8.0, nullable, NoWarn pedantes
├── Directory.Packages.props          # central package management
├── DISCLAIMER.md
├── SUPPORT.md
├── CITATION.cff
├── LICENSE
└── README.md
```

---

## Testes

```bash
dotnet test
```

```
Passed!  - Failed: 0, Passed: 38, Skipped: 0, Total: 38, Duration: 25 ms
```

| Suíte | Cobertura |
|---|---|
| `MessageValidatorTests` (10) | null, string vazia, whitespace, AdaptiveCard válido/inválido, número, array |
| `SendWithRetryTests` (12) | 200/429/403/410/400/500/transient/Retry-After, retry-then-success, e timeout (TaskCanceled) vs shutdown |
| `RowKeyTests` (3) | `ToSafeRowKey` (base64url, determinístico) para o RowKey de `conversationrefs` |
| `MessageIdTests` (4) | messageId determinístico do Service Bus `{jobId}:{md5(rowKey)}:{repeat}` (dedup nativa) |
| `TokenBucketTests` (4) | função pura `BucketStep` do token bucket Redis (recarga, capacidade, consumo) |
| `BotHttpStatusTests` (5) | extração do status HTTP do `ErrorResponseException` do Bot Framework (`Response.StatusCode`), incl. 400/403/timeout/desconhecido |

Os helpers `MessageValidator`, `SendWithRetry`, `ServiceBusMessageId.Compute`, `RedisTokenBucket.Step`, `BotHttpStatus.ExtractStatus` e `ConversationRefStore.ToSafeRowKey` foram **extraídos** para classes/funções puras, permitindo unit tests sem mocks pesados.

---

## Como iniciar (dev local)

```bash
git clone git@github.com:EdneiMonteiro/teams_msgs_dotnet.git
cd teams_msgs_dotnet

# Restore + testes
dotnet test

# Rodar API local — exige Storage Account configurado
dotnet run --project src/TeamsMsgs.Api
# default: http://localhost:5000  (ASPNETCORE_URLS sobrescreve)

# Worker local
dotnet run --project src/TeamsMsgs.Worker
```

`appsettings.json` aceita connection strings em `Storage:ConnectionString`, `ServiceBus:ConnectionString` e `Redis:ConnectionString` (em dev, aponte para emuladores/instâncias locais; em prod, vêm do K8s Secret). Os parâmetros do worker (`Worker:MaxConcurrent`, `Worker:PollBatchSize`, `Worker:SendTimeout`, etc.) também vêm de `appsettings.json` ou da ConfigMap (`Worker__*`) no cluster.

Para expor o `/api/messages` ao Bot Framework em dev:
```bash
# Em outro terminal:
ngrok http 5000
# Atualize Messaging Endpoint do Azure Bot para https://<ngrok>/api/messages
```

---

## Deploy em Azure

Passo-a-passo detalhado em [`docs/deploy.md`](./docs/deploy.md). Resumo:

```bash
# 1) Cria App Registration (precisa do tenant Entra)
APP_ID=$(az ad app create --display-name "teams-msgs-dotnet-bot" \
  --sign-in-audience AzureADMyOrg --query appId -o tsv)
az ad sp create --id "$APP_ID"
PWD=$(az ad app credential reset --id "$APP_ID" --append \
  --display-name "k8s" --years 1 --query password -o tsv)

# 2) Bicep — cria RG + Storage + Service Bus + Redis + Log Analytics + AKS
#     + Azure Bot (se botMsaAppId for fornecido); AcrPull no ACR compartilhado
export SHARED_ACR_NAME=<acr-compartilhado>; export SHARED_ACR_RG=<rg-do-acr>
az deployment sub create \
  --location brazilsouth \
  --template-file deploy/bicep/main.bicep \
  --parameters deploy/bicep/main.bicepparam

# 3) AKS add-ons (Istio Gateway externo + Gateway API CRDs)
az aks mesh enable-ingress-gateway -g rg-<seu-rg> -n aks-<seu-cluster> \
  --ingress-gateway-type external
az aks get-credentials -g rg-<seu-rg> -n aks-<seu-cluster>
kubectl apply -f \
  https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.2.0/standard-install.yaml

# 4) cert-manager
helm repo add jetstack https://charts.jetstack.io
helm upgrade --install cert-manager jetstack/cert-manager \
  -n cert-manager --create-namespace --set crds.enabled=true --wait

# 5) Atribui DNS label ao Public IP do Istio Gateway
MC_RG=$(az aks show -g rg-<seu-rg> -n aks-<seu-cluster> --query nodeResourceGroup -o tsv)
IP_NAME=$(az network public-ip list -g $MC_RG \
  --query "[?contains(name,'kubernetes')] | [0].name" -o tsv)
az network public-ip update -g $MC_RG -n $IP_NAME --dns-name teams-msgs-dotnet

# 6) Aplica Gateway/HTTPRoute/ClusterIssuer/Certificate
kubectl apply -f deploy/clusterissuer-gw.yaml \
              -f deploy/istio-gateway.yaml \
              -f deploy/istio-cert.yaml

# 7) Build das imagens no ACR compartilhado (resolve por nome, RG externo)
ACR=<acr-compartilhado>
az acr build -r $ACR --image teams-msgs/api:0.2.0 -f docker/Dockerfile.api .
az acr build -r $ACR --image teams-msgs/worker:0.2.0 -f docker/Dockerfile.worker .

# 8) Helm: cria values-poc.yaml a partir do template e dos outputs do Bicep
cp deploy/helm/teams-msgs/values-poc.yaml.template deploy/helm/teams-msgs/values-poc.yaml
# preencha: image.registry, storage/serviceBus/redis.connectionString (outputs @secure),
#           bot.appId, bot.appPassword, api.apiKey

helm upgrade --install teams-msgs deploy/helm/teams-msgs \
  -n teams-msgs --create-namespace \
  -f deploy/helm/teams-msgs/values.yaml \
  -f deploy/helm/teams-msgs/values-poc.yaml

# 9) Atualiza Messaging Endpoint do Azure Bot
az bot update -g rg-<seu-rg> -n bot-<seu-bot> \
  --endpoint "https://teams-msgs-dotnet.brazilsouth.cloudapp.azure.com/api/messages"
```

> Stack validado em ambiente PoC (região `brazilsouth`). Outputs do Bicep e os passos completos estão em [`docs/deploy.md`](./docs/deploy.md).

---

## Deploy do Teams App

1. Gere o pacote: `pwsh ./manifest/build.ps1 -AppId <APP_ID> -Fqdn <FQDN>` → cria `manifest/build/teams-msgs-dotnet-app.zip` (ignorado pelo `.gitignore`).
2. Suba em **Teams Admin Center → Manage apps → Upload custom app**.
3. **Setup policies → Global → Installed apps → Add apps** (org-wide).
4. Propagação org-wide leva 24–48h. Para testes imediatos, instale manualmente em **Apps → Built for your org**.

---

## Load test

```bash
cd load_test
npm install

$env:BOT_URL = "https://teams-msgs-dotnet.brazilsouth.cloudapp.azure.com"
$env:API_KEY = "<api-key>"
$env:STORAGE_CONNECTION = "<storage-connection-string>"   # semeia refs na tabela conversationrefs
node run-50k.js --refs 5000           # parâmetros válidos: --refs N, --skip-seed, --cleanup
```

O script:
1. Insere `N` referências **fictícias** na tabela `conversationrefs` (clonando 1 referência real, com `conversationId` falso → o envio real retorna `BadRequest`, esperado)
2. Dispara `POST /api/send`
3. Consulta `/api/jobs/{id}` a cada 2 s até `completed`
4. Gera `load_test/report.json` com vazão e tempos
5. Remove as referências fictícias ao final

> Para receber **N mensagens reais** no seu Teams (em vez de testar o pipeline), use `repeat: N` no `POST /api/send` em vez do teste de carga.

Resultado validado neste cluster (teste de carga de 50.000 refs):
- **50.001 mensagens processadas** (job `completed`) em **~16 min**
- **3.149 msg/min (~52,5 msg/s)** sustentado — no teto do rate limiter global (50/s)
- KEDA escalou o worker **0→10→0** conforme a profundidade da fila do Service Bus

---

## Troubleshooting

| Sintoma | Causa | Solução |
|---|---|---|
| `Authorization denied` no envio | Service Principal ausente no tenant alvo | `az ad sp create --id <app-id>` |
| `401 Unauthorized` em `/api/send`, `/api/jobs`, `/api/status` | `x-api-key` faltando ou divergente | Veja `Api:ApiKey` na ConfigMap/Secret do Helm |
| `401` em `/api/messages` (Bot Framework) | App Registration sem credencial OU `MicrosoftAppPassword` errado no Secret | Reseta com `az ad app credential reset` e atualiza `bot.appPassword` no `values-poc.yaml` |
| `403 Forbidden` / auth no Storage, Service Bus ou Redis | Connection string ausente/errada no Secret | Cheque `Storage__ConnectionString`, `ServiceBus__ConnectionString`, `Redis__ConnectionString` no `values-poc.yaml` |
| KEDA não escala o worker | `TriggerAuthentication` sem a connection string do Service Bus | Confira o secret key `ServiceBus__ConnectionString` referenciado no `scaledobject.yaml` |
| Workers em `CrashLoopBackOff` com "No frameworks were found" | `Dockerfile.worker` usando `dotnet/runtime` em vez de `dotnet/aspnet` (Bot integration precisa do ASP.NET runtime) | Usar `mcr.microsoft.com/dotnet/aspnet:8.0-alpine` |
| `Unable to activate type 'CloudAdapter'. Constructors are ambiguous` | DI sem factory explícita do CloudAdapter | Registrar via `sp => new CloudAdapter(sp.GetRequiredService<BotFrameworkAuthentication>(), …)` em vez de `AddSingleton<CloudAdapter>()` |
| `504 Gateway Timeout` em `/api/send` com 50k+ refs | Timeout default 60s do ingress | `HTTPRoute.spec.rules[].timeouts.request: 600s` (já aplicado no chart) OU refatorar para BackgroundService + Channel |
| Total = 0 em job grande | API cancelou o fan-out porque o cliente desconectou (504) | Mesmo fix do anterior. Em dev rápido: ignorar `RequestAborted` no `/api/send` handler |
| `/readyz` retorna 503 | Redis, Storage ou Service Bus inacessível | Cheque as 3 connection strings no Secret e a conectividade |
| `OverQuota` no Log Analytics | Daily cap atingido (configurado em 25 MB/dia) | Subir cap com `az monitor log-analytics workspace update --quota X` ou reduzir log level para `Warning` |
| KEDA escala mas mensagens não somem da fila | Pod do worker em crash | `kubectl -n teams-msgs logs deployment/teams-msgs-worker --tail=50` |
| `Bot Service: Agent.MessagesUrl must not be an IP address` | Bicep `bot.bicep` recebeu URL com IP cru | Configure DNS label no Public IP do Istio Gateway antes do deploy do módulo bot |
| Job travado em `processing` indefinidamente | Mensagens na DLQ ou refs órfãs | `kubectl -n teams-msgs logs deployment/teams-msgs-worker` e/ou inspecione a subfila `$DeadLetterQueue` do Service Bus |

---

## Suporte e Aviso Legal

- Sem SLA nem suporte oficial. Veja [SUPPORT.md](./SUPPORT.md).
- Uso sujeito a [DISCLAIMER.md](./DISCLAIMER.md).
- **Não afiliado nem endossado pela Microsoft.** Marcas usadas apenas para descrição.
