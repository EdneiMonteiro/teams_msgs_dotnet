// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Storage Account + Tables (conversationrefs, jobs, sentmarks) +
// Queues (send-messages, send-messages-poison).

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
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Enabled'
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  name: 'default'
  parent: storage
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  name: 'default'
  parent: storage
}

var tableNames = [
  'conversationrefs'
  'jobs'
  'sentmarks'
]

resource tables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [for name in tableNames: {
  name: name
  parent: tableService
}]

var queueNames = [
  'send-messages'
  'send-messages-poison'
]

resource queues 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = [for name in queueNames: {
  name: name
  parent: queueService
}]

output id string = storage.id
output name string = storage.name
output tableServiceUri string = storage.properties.primaryEndpoints.table
output queueServiceUri string = storage.properties.primaryEndpoints.queue
