# Custos estimados (PoC, brazilsouth)

Tabela rápida — confirme preços em [Azure Pricing Calculator](https://azure.microsoft.com/pricing/calculator/) para sua região.

## Validado durante a PoC

| Recurso | SKU | Custo (USD/dia) |
|---|---|---:|
| 2× `Standard_D2s_v5` Linux (AKS nodes) | PAYG | 7,20 |
| 2× OS Disk Premium SSD P10 (128 GB) | LRS | 1,31 |
| 1× Public IP Standard Static (Istio Gateway LB) | — | 0,12 |
| Load Balancer Standard (1 rule) | — | 0,60 |
| Container Registry Basic | $5/mês | 0,17 |
| Storage Account Standard_LRS (tabelas + queues, <1 GB) | PAYG | <0,01 |
| Log Analytics Pay-per-GB (cap 25 MB/dia → free tier) | — | 0 |
| Azure Bot Service F0 | Free | 0 |
| AKS control plane (Free tier) | Free | 0 |
| UAMI, federated credentials, RBAC | — | 0 |
| NSG, VNet, Route Table | — | 0 |
| **Total estimado** | | **≈ 9,40** |

> Após substituir NGINX por Istio: economia de **1 Public IP** (~$0,12/dia) + **1 LB rule** (~$0,60/dia). Total atual ~$9,40/dia em vez de ~$10,12/dia.

## Diferença vs versão TS (ACA)

| Versão | Idle | Sob carga | Notas |
|---|---|---|---|
| TS (ACA) | ~$5 (API min=1) | ~$30/job | Scale-to-zero do worker, sem control plane fixo |
| .NET (AKS) | ~$9,40 | ~$10 | Control plane "free" mas nodes 24/7 |

Para PoC curta (dias), AKS é viável. Para idle de longo prazo, ACA é significativamente mais barato.

## Estratégias para reduzir custo no AKS

| Ação | Economia diária | Trade-off |
|---|---:|---|
| `az aks stop` quando ocioso | ~$7,20 (paga só disk + LB) | Cold start ~3-5 min |
| 1 node `B2s` (em vez de 2× D2s_v5) | ~$6,70 | Sem HA, sem capacidade pra carga |
| Ephemeral OS disk (recriar nodepool com `--node-osdisk-type Ephemeral`) | ~$1,31 | Sem disco persistente; recriar pool |
| Logic App para shutdown agendado (off-hours) | ~$5 | Disponibilidade reduzida |
| AKS Automatic ou Virtual Nodes (ACI) | varia | Modelo serverless |
| Delete RG completamente | $9,40 | Provisioning total ao recriar (~15 min) |

## Componentes que cobram por uso (variável)

- **Storage Queue operations**: $0,0004 / 10k operações. Para 50k msgs/dia ≈ $0,002/dia.
- **Table Storage operations**: $0,00036 / 10k. Para 50k jobs com ~5 ops cada ≈ $0,01/dia.
- **Egress de dados do AKS para internet**: $0,087/GB acima dos primeiros 5 GB grátis. Bot Framework usa pouco (msgs pequenas).
- **Log Analytics**: gratuito até 5 GB/mês. Acima disso $2,30/GB.
- **Container Insights**: mesma conta do workspace; sem cobrança separada.
- **Public IP outbound**: $0,005/hora por IP estático.

## Quanto custou a PoC completa (referência)

Operando o ambiente por **~6 horas contínuas** (período do dev + smoke + load test 50k): aproximadamente **$2,40 USD**. Maior parte foram os 2 nodes do AKS rodando.
