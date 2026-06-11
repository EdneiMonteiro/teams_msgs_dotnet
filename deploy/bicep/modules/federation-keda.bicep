// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Federa o UAMI da app ao ServiceAccount do KEDA operator no kube-system.
// Sem isto, o azure-queue scaler retorna AADSTS700213 ao tentar ler a
// profundidade da fila via Workload Identity.

param identityName string
param oidcIssuerUrl string

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: identityName
}

resource fedKeda 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  name: 'fed-keda'
  parent: uami
  properties: {
    issuer: oidcIssuerUrl
    subject: 'system:serviceaccount:kube-system:keda-operator'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}
