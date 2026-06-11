// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

@minLength(3)
@maxLength(63)
param clusterName string

param location string

param tags object = {}

param logAnalyticsWorkspaceId string

@allowed([
  'Standard_B2s'
  'Standard_B2ms'
  'Standard_D2s_v5'
  'Standard_D4s_v5'
])
param systemNodeVmSize string = 'Standard_D2s_v5'

@minValue(1)
@maxValue(5)
param systemNodeCount int = 2

@description('Habilitar Istio managed add-on (service mesh). Necessário para EnvoyFilter de rate-limit.')
param enableIstio bool = true

resource aks 'Microsoft.ContainerService/managedClusters@2024-09-01' = {
  name: clusterName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'Base'
    tier: 'Free'
  }
  properties: {
    dnsPrefix: clusterName
    enableRBAC: true
    kubernetesVersion: null
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        count: systemNodeCount
        vmSize: systemNodeVmSize
        osType: 'Linux'
        osSKU: 'AzureLinux'
        type: 'VirtualMachineScaleSets'
        enableAutoScaling: true
        minCount: 1
        maxCount: 3
      }
    ]
    networkProfile: {
      networkPlugin: 'kubenet'
      loadBalancerSku: 'standard'
    }
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
    workloadAutoScalerProfile: {
      keda: {
        enabled: true
      }
    }
    serviceMeshProfile: enableIstio ? {
      mode: 'Istio'
      istio: {
        components: {
          ingressGateways: []
        }
      }
    } : {
      mode: 'Disabled'
    }
    addonProfiles: {
      omsagent: {
        enabled: true
        config: {
          logAnalyticsWorkspaceResourceID: logAnalyticsWorkspaceId
        }
      }
    }
    autoUpgradeProfile: {
      upgradeChannel: 'patch'
    }
  }
}

output id string = aks.id
output name string = aks.name
output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL
output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId
output principalId string = aks.identity.principalId
