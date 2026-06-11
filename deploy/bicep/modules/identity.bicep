// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

param identityName string

param location string

param tags object = {}

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

output id string = uami.id
output name string = uami.name
output clientId string = uami.properties.clientId
output principalId string = uami.properties.principalId
output tenantId string = uami.properties.tenantId
