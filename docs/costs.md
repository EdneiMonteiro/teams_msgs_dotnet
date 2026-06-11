# Custos estimados (PoC, brazilsouth)

Tabela rápida — preços públicos consultar [Azure Pricing Calculator](https://azure.microsoft.com/pricing/calculator/).

| Recurso | SKU | Custo estimado (USD/mês) |
|---|---|---|
| AKS control plane | Free tier | **$0** |
| AKS node pool (2x `Standard_D2s_v5`) | Pay-as-you-go | ~$140 |
| Storage Account `Standard_LRS` (tables + queues, <1 GB) | Pay-per-use | <$1 |
| Container Registry | Basic | ~$5 |
| Log Analytics | Pay-per-GB (30d retention, <2GB) | ~$5 |
| Azure Bot Service | F0 | $0 |
| Public LoadBalancer Standard + IP | Pay-per-use | ~$10 |
| **Total estimado** | | **~$160/mês** |

## Diferença vs versão TS (ACA)

| Versão | Idle | Sob carga | Notas |
|---|---|---|---|
| TS (ACA) | ~$5 (API min=1) | ~$30/job | Scale-to-zero do worker, sem control plane fixo |
| .NET (AKS) | ~$160 | ~$170 | Control plane "free" mas nodes 24/7 |

Para PoC curta (dias), AKS é viável. Para idle de longo prazo, ACA é
significativamente mais barato. Estratégias para reduzir custo no AKS:
- Reduzir node pool para 1x `B2s` (~$30/mês), perde HA.
- Usar `--enable-cluster-autoscaler` agressivo + cordon dos nodes.
- Schedule de shutdown via Logic App / Azure Functions.
- Trocar para AKS Automatic ou ACI virtual nodes.
