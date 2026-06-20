// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Orquestrador subscription-scope. Cria RG + chama todos os módulos.
// Naming: CAF abbreviations (https://learn.microsoft.com/en-us/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations).
//
// Para criar o App Registration do bot ANTES do deploy:
//   az ad app create --display-name "teams-msgs-dotnet-bot"
//   # copie o appId e tenantId para os parâmetros

targetScope = 'subscription'

@description('Prefix curto para resource names. Default: tmd.')
@minLength(2)
@maxLength(6)
param prefix string = 'tmd'

@description('Região para todos os recursos regionais.')
param location string = 'brazilsouth'

@description('Tag de ambiente.')
@allowed([
  'poc'
  'dev'
  'prod'
])
param environment string = 'poc'

@description('App Registration ID do bot (clientId). Se vazio, módulo bot é pulado.')
param botMsaAppId string = ''

@description('Tipo do App Registration do bot.')
@allowed([
  'SingleTenant'
  'MultiTenant'
])
param botMsaAppType string = 'SingleTenant'

@description('Tenant ID do bot (obrigatório se SingleTenant).')
param botMsaAppTenantId string = subscription().tenantId

@description('URL pública do endpoint /api/messages (depois do Ingress estar pronto). Pode deixar placeholder e atualizar depois.')
param botMessagingEndpoint string = 'https://example.invalid/api/messages'

@description('Nome do ACR compartilhado (em outro RG da mesma subscription). Informe no deploy; não commitar o valor real.')
param sharedAcrName string

@description('Resource group do ACR compartilhado. Informe no deploy; não commitar o valor real.')
param sharedAcrResourceGroup string

var tags = {
  project: 'teams-msgs-dotnet'
  env: environment
  managedBy: 'bicep'
  source: 'https://github.com/EdneiMonteiro/teams_msgs_dotnet'
}

var rgName = 'rg-${prefix}-${environment}'
var storageName = toLower('st${prefix}${environment}${take(uniqueString(subscription().id, rgName), 6)}')
var logName = 'log-${prefix}-${environment}'
var aksName = 'aks-${prefix}-${environment}'
var uamiName = 'id-${prefix}-${environment}-app'
var botName = 'bot-${prefix}-${environment}'

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: rgName
  location: location
  tags: tags
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    storageAccountName: storageName
    location: location
    tags: tags
  }
}

module log 'modules/loganalytics.bicep' = {
  name: 'log'
  scope: rg
  params: {
    workspaceName: logName
    location: location
    tags: tags
  }
}

module identity 'modules/identity.bicep' = {
  name: 'identity'
  scope: rg
  params: {
    identityName: uamiName
    location: location
    tags: tags
  }
}

module aks 'modules/aks.bicep' = {
  name: 'aks'
  scope: rg
  params: {
    clusterName: aksName
    location: location
    tags: tags
    logAnalyticsWorkspaceId: log.outputs.id
  }
}

module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  scope: rg
  params: {
    storageAccountName: storage.outputs.name
    uamiPrincipalId: identity.outputs.principalId
  }
}

module acrRbac 'modules/acr-rbac.bicep' = {
  name: 'acr-rbac'
  scope: resourceGroup(sharedAcrResourceGroup)
  params: {
    acrName: sharedAcrName
    kubeletPrincipalId: aks.outputs.kubeletIdentityObjectId
  }
}

module fedApi 'modules/federation.bicep' = {
  name: 'fed-api'
  scope: rg
  params: {
    identityName: identity.outputs.name
    federationName: 'fed-api'
    oidcIssuerUrl: aks.outputs.oidcIssuerUrl
    serviceAccountNamespace: 'teams-msgs'
    serviceAccountName: 'teams-msgs-api'
  }
}

module fedWorker 'modules/federation.bicep' = {
  name: 'fed-worker'
  scope: rg
  params: {
    identityName: identity.outputs.name
    federationName: 'fed-worker'
    oidcIssuerUrl: aks.outputs.oidcIssuerUrl
    serviceAccountNamespace: 'teams-msgs'
    serviceAccountName: 'teams-msgs-worker'
  }
}

module fedKeda 'modules/federation-keda.bicep' = {
  name: 'fed-keda'
  scope: rg
  params: {
    identityName: identity.outputs.name
    oidcIssuerUrl: aks.outputs.oidcIssuerUrl
  }
}

module bot 'modules/bot.bicep' = if (!empty(botMsaAppId)) {
  name: 'bot'
  scope: rg
  params: {
    botName: botName
    msaAppId: botMsaAppId
    msaAppType: botMsaAppType
    msaAppTenantId: botMsaAppTenantId
    messagingEndpoint: botMessagingEndpoint
    tags: tags
  }
}

output resourceGroupName string = rg.name
output storageAccountName string = storage.outputs.name
output storageTableServiceUri string = storage.outputs.tableServiceUri
output storageQueueServiceUri string = storage.outputs.queueServiceUri
output acrName string = sharedAcrName
output acrLoginServer string = '${sharedAcrName}.azurecr.io'
output logAnalyticsWorkspaceId string = log.outputs.id
output aksName string = aks.outputs.name
output aksOidcIssuerUrl string = aks.outputs.oidcIssuerUrl
output uamiClientId string = identity.outputs.clientId
output uamiId string = identity.outputs.id
