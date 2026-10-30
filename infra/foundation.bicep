targetScope = 'resourceGroup'
param location string = resourceGroup().location
param registryName string
param identityName string = 'id-agent-lgesama'
param pullIdentityName string = 'id-mcp-lgesama'
var tags = { project: 'lges-ama', purpose: 'customer-demo' }
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = { name: registryName }
resource hostIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}
resource mcpIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: pullIdentityName
  location: location
  tags: tags
}
var acrPull = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
resource hostPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, hostIdentity.id, acrPull)
  scope: registry
  properties: { principalId: hostIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: acrPull }
}
resource mcpPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, mcpIdentity.id, acrPull)
  scope: registry
  properties: { principalId: mcpIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: acrPull }
}
output hostIdentityId string = hostIdentity.id
output hostClientId string = hostIdentity.properties.clientId
output hostPrincipalId string = hostIdentity.properties.principalId
output mcpIdentityId string = mcpIdentity.id
