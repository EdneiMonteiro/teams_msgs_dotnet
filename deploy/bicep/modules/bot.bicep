// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Azure Bot Service (Bot Channels Registration). REQUER um Microsoft Entra
// App Registration pré-existente — Bicep não consegue criar App Registration
// (precisa de Microsoft Graph). Use `az ad app create` antes do deploy.

@minLength(2)
@maxLength(42)
param botName string

@description('Display name visível no portal Bot.')
param displayName string = botName

@description('Microsoft Entra App Registration ID (clientId).')
param msaAppId string

@allowed([
  'SingleTenant'
  'MultiTenant'
  'UserAssignedMSI'
])
param msaAppType string = 'SingleTenant'

@description('Tenant ID — obrigatório se msaAppType=SingleTenant.')
param msaAppTenantId string = ''

@description('URL do endpoint /api/messages exposto pelo Ingress AKS.')
param messagingEndpoint string

param tags object = {}

resource bot 'Microsoft.BotService/botServices@2022-09-15' = {
  name: botName
  location: 'global'
  tags: tags
  sku: {
    name: 'F0'
  }
  kind: 'azurebot'
  properties: {
    displayName: displayName
    endpoint: messagingEndpoint
    msaAppId: msaAppId
    msaAppType: msaAppType
    msaAppTenantId: msaAppType == 'SingleTenant' ? msaAppTenantId : ''
    disableLocalAuth: false
  }
}

resource msTeamsChannel 'Microsoft.BotService/botServices/channels@2022-09-15' = {
  name: 'MsTeamsChannel'
  parent: bot
  location: 'global'
  properties: {
    channelName: 'MsTeamsChannel'
    properties: {
      isEnabled: true
    }
  }
}

output id string = bot.id
output name string = bot.name
