// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Azure Service Bus (Standard) + fila send-messages com duplicate detection
// (idempotência nativa via MessageId) e dead-lettering nativo.

@minLength(6)
@maxLength(50)
param namespaceName string

param location string

param tags object = {}

param queueName string = 'send-messages'

@minValue(1)
@maxValue(2000)
param maxDeliveryCount int = 5

resource sbNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    minimumTlsVersion: '1.2'
  }
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  name: queueName
  parent: sbNamespace
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: maxDeliveryCount
    lockDuration: 'PT5M'
    maxSizeInMegabytes: 1024
  }
}

resource rootRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2022-10-01-preview' existing = {
  name: 'RootManageSharedAccessKey'
  parent: sbNamespace
}

output namespaceName string = sbNamespace.name
output queueName string = queue.name

@description('Connection string (secret) — não commitar; usar em K8s Secret.')
@secure()
output connectionString string = rootRule.listKeys().primaryConnectionString
