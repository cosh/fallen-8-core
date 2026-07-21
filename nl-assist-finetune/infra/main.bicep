// Azure IaC for a one-shot phi4-f8 / phi4-f8-mini fine-tune on an NVIDIA A10 (NVadsA10v5) VM.
// Deployed resource-group-scoped by deploy.sh into a DEDICATED resource group that the VM
// deletes itself at the end (see bootstrap.sh) - so nothing lingers. On-demand by default
// (Spot A10 quota is often unavailable); set useSpot=true to try Spot. A system-assigned
// managed identity + a Contributor role assignment on this RG let it run `az group delete`.

@description('Azure region. Must have NVadsA10v5 capacity + quota (Total Regional vCPUs and the family).')
param location string = resourceGroup().location

@description('VM name / computer name.')
param vmName string = 'f8-finetune'

@description('VM size. Full A10 (24GB) is needed for 14B QLoRA - Standard_NV36ads_A10_v5.')
param vmSize string = 'Standard_NV36ads_A10_v5'

@description('Admin username for SSH.')
param adminUsername string = 'azureuser'

@description('SSH public key for the admin user (contents of your ~/.ssh/*.pub).')
param adminSshPublicKey string

@secure()
@description('Base64 cloud-init (cloud-config) that installs, trains, publishes, self-destroys. Secure: it carries the injected Ollama key and any GIT_TOKEN.')
param customData string

@description('Use a Spot (low-priority) VM instead of on-demand. Default false - Spot A10 quota is often unavailable / auto-declined.')
param useSpot bool = false

@description('Spot max price as a string (only used when useSpot=true): "-1" = up to on-demand, or a decimal cap like "0.50".')
param spotMaxPrice string = '-1'

@description('CIDR allowed to SSH in (to watch progress). Default your deploy IP; * is open.')
param allowedSshCidr string = '*'

@description('Optional GRID driver pin (e.g. "535.161" = GRID 16.5). Empty = the extensions current default (18.x); only pin an older driver if the default misbehaves with CUDA on A10.')
param driverVersion string = ''

@description('OS disk size (GB). Both variants + the 14B fp16 merge + f16/Q4 GGUFs + HF base caches need lots of scratch.')
param osDiskSizeGb int = 512

var contributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')

resource nsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: '${vmName}-nsg'
  location: location
  properties: {
    securityRules: [
      {
        name: 'AllowSSH'
        properties: {
          priority: 1000
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: allowedSshCidr
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '22'
        }
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: '${vmName}-vnet'
  location: location
  properties: {
    addressSpace: { addressPrefixes: [ '10.0.0.0/24' ] }
    subnets: [
      {
        name: 'default'
        properties: {
          addressPrefix: '10.0.0.0/24'
          networkSecurityGroup: { id: nsg.id }
        }
      }
    ]
  }
}

resource publicIp 'Microsoft.Network/publicIPAddresses@2023-11-01' = {
  name: '${vmName}-pip'
  location: location
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
  }
}

resource nic 'Microsoft.Network/networkInterfaces@2023-11-01' = {
  name: '${vmName}-nic'
  location: location
  properties: {
    ipConfigurations: [
      {
        name: 'ipconfig1'
        properties: {
          subnet: { id: vnet.properties.subnets[0].id }
          privateIPAllocationMethod: 'Dynamic'
          publicIPAddress: { id: publicIp.id }
        }
      }
    ]
  }
}

resource vm 'Microsoft.Compute/virtualMachines@2023-09-01' = {
  name: vmName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    hardwareProfile: { vmSize: vmSize }
    priority: useSpot ? 'Spot' : 'Regular'
    evictionPolicy: useSpot ? 'Deallocate' : null
    billingProfile: useSpot ? { maxPrice: json(spotMaxPrice) } : null
    storageProfile: {
      imageReference: {
        publisher: 'Canonical'
        offer: '0001-com-ubuntu-server-jammy'
        sku: '22_04-lts-gen2'
        version: 'latest'
      }
      osDisk: {
        createOption: 'FromImage'
        diskSizeGB: osDiskSizeGb
        managedDisk: { storageAccountType: 'Premium_LRS' }
      }
    }
    osProfile: {
      computerName: vmName
      adminUsername: adminUsername
      customData: customData
      linuxConfiguration: {
        disablePasswordAuthentication: true
        ssh: {
          publicKeys: [
            {
              path: '/home/${adminUsername}/.ssh/authorized_keys'
              keyData: adminSshPublicKey
            }
          ]
        }
      }
    }
    networkProfile: { networkInterfaces: [ { id: nic.id } ] }
    diagnosticsProfile: { bootDiagnostics: { enabled: true } }
  }
}

// The VM's identity may delete this resource group (self-teardown in bootstrap.sh).
resource selfDestructRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, vm.id, 'contributor-self-destruct')
  properties: {
    roleDefinitionId: contributorRoleId
    principalId: vm.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Installs the NVIDIA GRID driver. 535.161 is pinned because the extension's default (17.5)
// breaks CUDA on A10 (documented Azure known issue).
resource gpuDriver 'Microsoft.Compute/virtualMachines/extensions@2023-09-01' = {
  parent: vm
  name: 'NvidiaGpuDriverLinux'
  location: location
  properties: {
    publisher: 'Microsoft.HpcCompute'
    type: 'NvidiaGpuDriverLinux'
    // 1.13 is the latest real handler (1.11 does NOT exist - that was the deploy failure).
    // autoUpgradeMinorVersion keeps it on the latest 1.x patch. Its default GRID driver is 18.x
    // (the old 17.5 A10-CUDA bug is no longer the default). Only send driverVersion when the
    // param is set, so a fresh deploy can't request a driver the handler doesn't ship.
    typeHandlerVersion: '1.13'
    autoUpgradeMinorVersion: true
    settings: empty(driverVersion) ? {} : {
      driverVersion: driverVersion
    }
  }
}

output publicIp string = publicIp.properties.ipAddress
output sshCommand string = 'ssh ${adminUsername}@${publicIp.properties.ipAddress}'
output watchCommand string = 'ssh ${adminUsername}@${publicIp.properties.ipAddress} "tail -f /var/log/f8-finetune.log"'
