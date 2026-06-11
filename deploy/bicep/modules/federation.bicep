// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Federa um UAMI a um ServiceAccount Kubernetes (Workload Identity).

param identityName string

param federationName string

param oidcIssuerUrl string

param serviceAccountNamespace string

param serviceAccountName string

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: identityName
}

resource federation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  name: federationName
  parent: uami
  properties: {
    issuer: oidcIssuerUrl
    subject: 'system:serviceaccount:${serviceAccountNamespace}:${serviceAccountName}'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}
