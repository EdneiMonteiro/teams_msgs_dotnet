# Deploy guide

## Pré-requisitos

- Azure CLI (`az`) com login na subscription destino.
- Bicep CLI (`az bicep install`).
- `kubectl`, `helm` v3.
- (opcional) `k6` para load tests.

## 1. Pré-criar Microsoft Entra App Registration do bot

Bicep não consegue criar App Registration (precisa do Microsoft Graph).

```bash
APP_ID=$(az ad app create \
  --display-name "teams-msgs-dotnet-bot" \
  --sign-in-audience AzureADMyOrg \
  --query appId -o tsv)
SP_ID=$(az ad sp create --id "$APP_ID" --query id -o tsv)
PWD=$(az ad app credential reset --id "$APP_ID" --append \
  --display-name "k8s" --years 1 --query password -o tsv)
echo "APP_ID=$APP_ID"
echo "APP_PWD=$PWD"
```

## 2. Provisionar infraestrutura (Bicep)

Edite `deploy/bicep/main.bicepparam` preenchendo `botMsaAppId`
(opcional para o primeiro deploy — pode ser feito depois).

```bash
az account set --subscription <seu-subscription-id>
az deployment sub create \
  --location brazilsouth \
  --template-file deploy/bicep/main.bicep \
  --parameters deploy/bicep/main.bicepparam
```

Anote os outputs (cm `az deployment sub show -n <name> --query properties.outputs`):
- `acrLoginServer`
- `storageTableServiceUri`
- `storageQueueServiceUri`
- `uamiClientId`
- `aksName`

## 3. Build & push das imagens (ACR)

```bash
ACR=$(az acr list -g rg-tmd-poc --query "[0].name" -o tsv)

az acr build --registry "$ACR" \
  --image api:0.1.0 --image api:latest \
  --file docker/Dockerfile.api .

az acr build --registry "$ACR" \
  --image worker:0.1.0 --image worker:latest \
  --file docker/Dockerfile.worker .
```

## 4. Configurar `values-poc.yaml` para o Helm

```bash
cp deploy/helm/teams-msgs/values-poc.yaml.template deploy/helm/teams-msgs/values-poc.yaml
```

Edite com os outputs do Bicep + APP_ID/APP_PWD do passo 1.

## 5. Deploy via Helm

```bash
az aks get-credentials -g rg-tmd-poc -n aks-tmd-poc
helm upgrade --install teams-msgs deploy/helm/teams-msgs \
  --create-namespace -n teams-msgs \
  -f deploy/helm/teams-msgs/values.yaml \
  -f deploy/helm/teams-msgs/values-poc.yaml
```

## 6. Atualizar endpoint do Bot

```bash
LB_IP=$(kubectl -n teams-msgs get svc teams-msgs-api -o jsonpath='{.status.loadBalancer.ingress[0].ip}')
az bot update -n bot-tmd-poc -g rg-tmd-poc \
  --endpoint "https://$LB_IP/api/messages"
```

> ⚠️ Sem TLS por padrão. Para produção configure cert-manager + Ingress
> ou termine TLS no Azure Application Gateway.

## 7. Validar

```bash
kubectl -n teams-msgs get pods,svc,scaledobject

# Health
curl http://$LB_IP/healthz
curl http://$LB_IP/readyz

# Status (precisa do x-api-key)
curl -H "x-api-key: change-me-poc" http://$LB_IP/api/status

# Disparo
curl -X POST http://$LB_IP/api/send \
  -H "Content-Type: application/json" \
  -H "x-api-key: change-me-poc" \
  -d '{"message":"📢 hello from .NET PoC","repeat":1}'
```

## 8. Teardown

```bash
az group delete -n rg-tmd-poc --yes --no-wait
# App Registration:
az ad app delete --id "$APP_ID"
```
