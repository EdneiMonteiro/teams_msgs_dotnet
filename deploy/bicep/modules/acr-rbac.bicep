// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// AcrPull para o kubelet do AKS em um ACR compartilhado (em outro RG da
// mesma subscription). Este módulo é deployado com scope no RG do ACR
// compartilhado, então requer permissão de role assignment nesse RG.

param acrName string

param kubeletPrincipalId string

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = {
  name: acrName
}

// AcrPull
var roleAcrPull = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPullAssign 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, kubeletPrincipalId, roleAcrPull)
  scope: acr
  properties: {
    principalId: kubeletPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleAcrPull)
  }
}
