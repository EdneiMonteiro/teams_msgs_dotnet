// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Azure Cache for Redis (Basic C0) — job counters, índice de refs, cache de
// mensagem e token bucket global. TLS-only.

@minLength(1)
@maxLength(63)
param redisName string

param location string

param tags object = {}

resource redis 'Microsoft.Cache/redis@2023-08-01' = {
  name: redisName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'Basic'
      family: 'C'
      capacity: 0
    }
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
    redisVersion: '6'
  }
}

output name string = redis.name
output hostName string = redis.properties.hostName

@description('Connection string (secret) StackExchange.Redis — não commitar.')
@secure()
output connectionString string = '${redis.properties.hostName}:6380,password=${redis.listKeys().primaryKey},ssl=True,abortConnect=False'
