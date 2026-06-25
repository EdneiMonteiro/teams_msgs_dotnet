// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Azure Managed Redis (Balanced_B0) — job counters, índice de refs, cache de
// mensagem e token bucket global. TLS-only (porta 10000).
//
// O Azure Cache for Redis clássico (Basic C0) e o Redis Enterprise (Enterprise_E*)
// entraram em retirement. O produto atual é o Azure Managed Redis, que usa o mesmo
// provider Microsoft.Cache/redisEnterprise porém com SKUs Balanced_B*/MemoryOptimized_*
// /ComputeOptimized_*. Balanced_B0 é o menor (~US$15/mês). highAvailability=Disabled
// mantém o menor custo (sem zona redundante). clusteringPolicy=EnterpriseCluster expõe
// um único endpoint compatível com StackExchange.Redis sem configuração de cluster.

@minLength(1)
@maxLength(60)
param redisName string

param location string

param tags object = {}

@description('SKU do Azure Managed Redis. Balanced_B0 é o menor disponível.')
param skuName string = 'Balanced_B0'

@description('Alta disponibilidade (zona redundante). Disabled = menor custo para PoC.')
@allowed([
  'Enabled'
  'Disabled'
])
param highAvailability string = 'Disabled'

resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: redisName
  location: location
  tags: tags
  sku: {
    name: skuName
  }
  properties: {
    highAvailability: highAvailability
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource redisDb 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  parent: redis
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    clusteringPolicy: 'EnterpriseCluster'
    evictionPolicy: 'NoEviction'
    accessKeysAuthentication: 'Enabled'
  }
}

output name string = redis.name
output hostName string = redis.properties.hostName

@description('Connection string (secret) StackExchange.Redis — não commitar.')
@secure()
output connectionString string = '${redis.properties.hostName}:10000,password=${redisDb.listKeys().primaryKey},ssl=True,abortConnect=False'
