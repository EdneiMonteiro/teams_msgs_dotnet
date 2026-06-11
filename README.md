# 📨 Teams Proactive Messaging — .NET 8 PoC

[![ORCID](https://img.shields.io/badge/ORCID-0009--0006--0765--4201-A6CE39?logo=orcid&logoColor=white)](https://orcid.org/0009-0006-0765-4201)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Azure](https://img.shields.io/badge/Cloud-Azure-0078D4?logo=microsoftazure&logoColor=white)](#)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](#)

> **PoC** — port da demo [`EdneiMonteiro/teams_msgs`](https://github.com/EdneiMonteiro/teams_msgs) (Node 20 + TS) para **.NET 8** com substituição de componentes Azure. Mantém o mesmo objetivo: envio em massa de mensagens proativas 1:1 via Microsoft Teams, respeitando rate limits do Bot Framework.
>
> ⚠️ Esta é uma **prova de conceito**. Antes de usar em produção revise segurança, escalabilidade, observabilidade, custos e conformidade. Veja [DISCLAIMER.md](./DISCLAIMER.md) e [SUPPORT.md](./SUPPORT.md).

---

## Trocas em relação ao repo original

| Componente | Original (TS) | Este repo (.NET) | Motivo |
|---|---|---|---|
| Runtime | Node 20 + TypeScript | **.NET 8 LTS** (ASP.NET Core Minimal API + Worker Service) | LTS até nov/2026, Bot Builder .NET maduro |
| Fila | Azure Service Bus (queue `send-messages`) | **Azure Storage Queue** | PoC sem mensagens longas, custo menor |
| Cache / counters / rate-limit | Azure Cache for Redis (HMSET + Lua bucket) | **Table Storage** (counters via ETag) + **Istio EnvoyFilter** (rate-limit egress) | Elimina recurso gerenciado adicional |
| Compute | Azure Container Apps + KEDA | **AKS** + KEDA (`azure-queue` scaler) + HPA (CPU) | Mais controle, pedido do usuário |
| Idempotência | `messageId` dedup do SB | Tabela `sentmarks` (insert-or-conflict) | SB tinha dedup nativa, Storage Queue não |
| DLQ | Service Bus DLQ built-in | Queue `send-messages-poison` (manual, após `dequeueCount > 5`) | Storage Queue não tem DLQ nativa |

## Trade-offs assumidos (PoC)

- AdaptiveCards **> 64KB ficam fora de escopo** (limite da Storage Queue, base64-encoded).
- Rate-limit Istio é **por pod** (`MAX_REPLICAS × per_pod = total`). Rate-limit global cross-pod exigiria Redis externo, contradizendo a remoção do Redis.
- AKS tem **custo de control plane mesmo idle** (vs scale-to-zero do ACA). Documentado em [docs/costs.md](./docs/costs.md).
- Sem migração de dados: a Storage Account `conversationrefs` pode ser reusada se desejado.

## Arquitetura

> Documentação detalhada em [docs/architecture.md](./docs/architecture.md).

```
Cliente ──→ Ingress AKS ──→ TeamsMsgs.Api (Deployment, min=1)
Teams   ──→ /api/messages ─┘   │
                                ├──→ Storage Queue (send-messages)
                                │            │
                                │            │ KEDA azure-queue scaler 0..10
                                │            ▼
                                │   TeamsMsgs.Worker (BackgroundService)
                                │            │
                                │            │ Istio EnvoyFilter (local_ratelimit)
                                │            ▼
                                │   Bot Framework ──→ Usuário Teams
                                ▼
                       Table Storage (refs / jobs / sentmarks)
```

## Estrutura do repo

```
src/
  TeamsMsgs.Api/          ASP.NET Core Minimal API (Bot + endpoints)
  TeamsMsgs.Worker/       Worker Service (consome Storage Queue)
  TeamsMsgs.Shared/       Storage, Validation, Sending, Bot
tests/
  TeamsMsgs.Tests/        xUnit + Moq
deploy/
  bicep/                  IaC: RG, Storage, ACR, AKS, Log Analytics, Bot Registration
  helm/teams-msgs/        Chart: Deployments, KEDA ScaledObject, HPA, EnvoyFilter
docker/
  Dockerfile.api
  Dockerfile.worker
manifest/                 Microsoft Teams app manifest (reaproveitado do repo original)
.github/workflows/        CI/CD (build, test, push ACR, helm upgrade)
load_test/                k6 scripts
```

## Status

🚧 PoC em construção. Veja [progresso](https://github.com/EdneiMonteiro/teams_msgs_dotnet/issues) ou o [`plan.md`](https://github.com/EdneiMonteiro/teams_msgs_dotnet/blob/main/docs/plan.md) na sessão.

## Como começar (em breve)

Documentação de deploy local (Azurite + .NET) e em AKS via Bicep+Helm estará em [docs/deploy.md](./docs/deploy.md) ao final da PoC.

## Licença

MIT — veja [LICENSE](./LICENSE).
