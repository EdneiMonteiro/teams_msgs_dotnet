using 'main.bicep'

param prefix = 'tmd'
param location = 'brazilsouth'
param environment = 'poc'
// Preencha após criar o App Registration:
//   az ad app create --display-name "teams-msgs-dotnet-bot" --sign-in-audience AzureADMyOrg
//   az ad app show --id <appId> --query appId
param botMsaAppId = '9cbc1ab5-d092-4d6b-a5e6-59e4f84547c2'
param botMsaAppType = 'SingleTenant'
// botMsaAppTenantId default = subscription().tenantId
// botMessagingEndpoint = placeholder até o Ingress estar pronto
// ACR compartilhado: valores reais via env vars (local-only), nunca commitados.
//   PowerShell: $env:SHARED_ACR_NAME = '<acr-compartilhado>'; $env:SHARED_ACR_RG = '<rg-do-acr>'
param sharedAcrName = readEnvironmentVariable('SHARED_ACR_NAME', '')
param sharedAcrResourceGroup = readEnvironmentVariable('SHARED_ACR_RG', '')
