using 'main.bicep'

param prefix = 'tmd'
param location = 'brazilsouth'
param environment = 'poc'
// Preencha após criar o App Registration:
//   az ad app create --display-name "teams-msgs-dotnet-bot" --sign-in-audience AzureADMyOrg
//   az ad app show --id <appId> --query appId
param botMsaAppId = ''
param botMsaAppType = 'SingleTenant'
// botMsaAppTenantId default = subscription().tenantId
// botMessagingEndpoint = placeholder até o Ingress estar pronto
