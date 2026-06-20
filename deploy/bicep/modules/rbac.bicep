// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Atribuições de RBAC:
//   - UAMI da app → Storage Table/Queue Data Contributor (data plane)
// (AcrPull do kubelet é feito em modules/acr-rbac.bicep, no RG do ACR compartilhado.)

param storageAccountName string

param uamiPrincipalId string

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

// Storage Table Data Contributor
var roleTableContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
// Storage Queue Data Contributor
var roleQueueContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'

resource tableRoleAssign 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, uamiPrincipalId, roleTableContributor)
  scope: storage
  properties: {
    principalId: uamiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleTableContributor)
  }
}

resource queueRoleAssign 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, uamiPrincipalId, roleQueueContributor)
  scope: storage
  properties: {
    principalId: uamiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleQueueContributor)
  }
}
