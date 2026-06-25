// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Storage Account + Table `conversationrefs` (refs duráveis — fonte da verdade).
// Counters/cache/rate-limit migraram para o Redis; a fila migrou para o
// Service Bus. Autenticação por connection string (Workload Identity removido).

@minLength(3)
@maxLength(24)
param storageAccountName string

param location string

param tags object = {}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  name: 'default'
  parent: storage
}

resource refsTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  name: 'conversationrefs'
  parent: tableService
}

output id string = storage.id
output name string = storage.name

@description('Connection string (secret) da Storage Account — não commitar.')
@secure()
output connectionString string = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
