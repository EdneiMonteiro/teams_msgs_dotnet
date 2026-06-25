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
var sbName = toLower('sb-${prefix}-${environment}-${take(uniqueString(subscription().id, rgName), 6)}')
var redisName = toLower('redis-${prefix}-${environment}-${take(uniqueString(subscription().id, rgName), 6)}')
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

module serviceBus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  scope: rg
  params: {
    namespaceName: sbName
    location: location
    tags: tags
  }
}

module redis 'modules/redis.bicep' = {
  name: 'redis'
  scope: rg
  params: {
    redisName: redisName
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

module acrRbac 'modules/acr-rbac.bicep' = {
  name: 'acr-rbac'
  scope: resourceGroup(sharedAcrResourceGroup)
  params: {
    acrName: sharedAcrName
    kubeletPrincipalId: aks.outputs.kubeletIdentityObjectId
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
output serviceBusQueueName string = serviceBus.outputs.queueName
output redisHostName string = redis.outputs.hostName
output acrName string = sharedAcrName
output acrLoginServer string = '${sharedAcrName}.azurecr.io'
output logAnalyticsWorkspaceId string = log.outputs.id
output aksName string = aks.outputs.name

@description('Connection strings (secrets) para o K8s Secret — não commitar.')
@secure()
output storageConnectionString string = storage.outputs.connectionString

@secure()
output serviceBusConnectionString string = serviceBus.outputs.connectionString

@secure()
output redisConnectionString string = redis.outputs.connectionString
