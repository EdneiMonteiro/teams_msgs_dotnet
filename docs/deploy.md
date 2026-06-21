# Deploy guide

Passo-a-passo completo para deployar do zero. Para resumo, ver [`README.md`](../README.md#deploy-em-azure).

## Pré-requisitos

- `az` CLI logado na subscription destino
- `az bicep install` (Bicep CLI)
- `helm` v3
- `kubectl` (ou usar `az aks command invoke` se preferir não instalar)
- `node` 20+ (apenas para load test)

## 1. Pré-criar Microsoft Entra App Registration do bot

Bicep não cria App Registration (precisa do Microsoft Graph).

```bash
APP_ID=$(az ad app create \
  --display-name "teams-msgs-dotnet-bot" \
  --sign-in-audience AzureADMyOrg \
  --query appId -o tsv)
az ad sp create --id "$APP_ID"
PWD=$(az ad app credential reset --id "$APP_ID" --append \
  --display-name "k8s" --years 1 --query password -o tsv)
echo "APP_ID=$APP_ID"
echo "APP_PWD=$PWD"
```

## 2. Provisionar infraestrutura (Bicep)

Edite `deploy/bicep/main.bicepparam` preenchendo `botMsaAppId` (pode ficar vazio no primeiro deploy e re-aplicar depois).

O ACR é compartilhado (em outro RG da subscription). **Pré-requisito:** um ACR já existente onde você tenha permissão de atribuir `AcrPull` — se não tiver, crie um: `az acr create -n <acr-compartilhado> -g <rg-do-acr> --sku Basic`. Informe os nomes via env vars **local-only** (não commitar) — o `main.bicepparam` lê de `SHARED_ACR_NAME`/`SHARED_ACR_RG`:

```bash
az account set --subscription <SUB_ID>
export SHARED_ACR_NAME=<acr-compartilhado>      # PowerShell: $env:SHARED_ACR_NAME='...'
export SHARED_ACR_RG=<rg-do-acr-compartilhado>  # PowerShell: $env:SHARED_ACR_RG='...'
az deployment sub create \
  --location brazilsouth \
  --template-file deploy/bicep/main.bicep \
  --parameters deploy/bicep/main.bicepparam
```

> O kubelet do AKS recebe `AcrPull` no ACR compartilhado via `modules/acr-rbac.bicep` (scope no RG do ACR) — requer permissão de role assignment nesse RG.

Outputs relevantes (`az deployment sub show -n <name> --query properties.outputs`):
- `acrLoginServer`
- `storageTableServiceUri`
- `storageQueueServiceUri`
- `uamiClientId`
- `aksName`

## 3. Habilitar Istio managed ingress gateway

```bash
az aks mesh enable-ingress-gateway -g rg-<seu-rg> -n aks-<seu-cluster> \
  --ingress-gateway-type external
az aks get-credentials -g rg-<seu-rg> -n aks-<seu-cluster>
```

## 4. Instalar Gateway API CRDs e cert-manager

```bash
kubectl apply -f \
  https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.2.0/standard-install.yaml

helm repo add jetstack https://charts.jetstack.io
helm repo update
helm upgrade --install cert-manager jetstack/cert-manager \
  --namespace cert-manager --create-namespace \
  --set crds.enabled=true --wait --timeout=5m
```

## 5. Atribuir DNS label ao Public IP do Istio Gateway

```bash
MC_RG=$(az aks show -g rg-<seu-rg> -n aks-<seu-cluster> --query nodeResourceGroup -o tsv)
ISTIO_IP=$(kubectl -n aks-istio-ingress get svc \
  aks-istio-ingressgateway-external -o jsonpath='{.status.loadBalancer.ingress[0].ip}')
PIP_NAME=$(az network public-ip list -g $MC_RG \
  --query "[?ipAddress=='$ISTIO_IP'].name" -o tsv)
az network public-ip update -g $MC_RG -n $PIP_NAME --dns-name teams-msgs-dotnet
# → teams-msgs-dotnet.brazilsouth.cloudapp.azure.com
```

## 6. Aplicar Gateway, HTTPRoute, ClusterIssuer, Certificate

```bash
kubectl apply -f deploy/clusterissuer-gw.yaml \
              -f deploy/istio-gateway.yaml \
              -f deploy/istio-cert.yaml
# aguarde ~30s
kubectl -n aks-istio-ingress get certificate teams-msgs-gw-tls
# Ready=True quando o cert estiver pronto
```

## 7. Build & push das imagens (ACR compartilhado)

> As imagens vão para o ACR compartilhado (outro RG da subscription); `az acr build` resolve por nome, independente do RG.

```bash
ACR=<acr-compartilhado>
# A tag deve casar com image.apiTag/workerTag do values-poc.yaml (o template já vem com 0.1.6).
az acr build --registry "$ACR" \
  --image teams-msgs/api:0.1.6 --image teams-msgs/api:latest \
  --file docker/Dockerfile.api .
az acr build --registry "$ACR" \
  --image teams-msgs/worker:0.1.6 --image teams-msgs/worker:latest \
  --file docker/Dockerfile.worker .
```

## 8. Configurar Helm `values-poc.yaml`

```bash
cp deploy/helm/teams-msgs/values-poc.yaml.template deploy/helm/teams-msgs/values-poc.yaml
```

Edite com:
- `image.registry`: `${ACR}.azurecr.io` (output do Bicep)
- `image.apiTag`, `image.workerTag`: a tag que você buildou no passo 7 (template já vem com `0.1.6`)
- `workloadIdentity.uamiClientId`: `uamiClientId` (output do Bicep)
- `storage.tableServiceUri`, `storage.queueServiceUri`: outputs do Bicep
- `bot.appId`, `bot.appPassword`: APP_ID e PWD do passo 1
- `bot.tenantId`, `azure.tenantId`: seu tenant Entra
- `api.apiKey`: string aleatória forte

## 9. Deploy via Helm

```bash
helm upgrade --install teams-msgs deploy/helm/teams-msgs \
  --create-namespace -n teams-msgs \
  -f deploy/helm/teams-msgs/values.yaml \
  -f deploy/helm/teams-msgs/values-poc.yaml
```

## 10. Atualizar Bot messaging endpoint

```bash
az bot update -n bot-<seu-bot> -g rg-<seu-rg> \
  --endpoint "https://teams-msgs-dotnet.brazilsouth.cloudapp.azure.com/api/messages"
```

## 11. Validar

```bash
# Health
curl https://teams-msgs-dotnet.brazilsouth.cloudapp.azure.com/healthz
curl https://teams-msgs-dotnet.brazilsouth.cloudapp.azure.com/readyz

# Status (precisa do x-api-key)
curl -H "x-api-key: <API_KEY>" \
  https://teams-msgs-dotnet.brazilsouth.cloudapp.azure.com/api/status

# Disparo
curl -X POST https://teams-msgs-dotnet.brazilsouth.cloudapp.azure.com/api/send \
  -H "Content-Type: application/json" \
  -H "x-api-key: <API_KEY>" \
  -d '{"message":"📢 hello from .NET PoC","repeat":1}'
```

## 12. Deploy do Teams App

```bash
pwsh ./manifest/build.ps1 \
  -AppId "$APP_ID" \
  -Fqdn "teams-msgs-dotnet.brazilsouth.cloudapp.azure.com"
# → manifest/build/teams-msgs-dotnet-app.zip
```

Upload em **Teams Admin Center → Manage apps → Upload custom app**, depois **Setup policies → Global → Installed apps → Add apps** para instalar org-wide.

## Teardown

```bash
az group delete -n rg-<seu-rg> --yes --no-wait
az ad app delete --id "$APP_ID"
```
