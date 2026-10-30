targetScope = 'resourceGroup'
param location string = 'koreacentral'
param environmentName string
param registryName string
param hostIdentityName string = 'id-agent-lgesama'
param hostIdentityClientId string
param mcpIdentityName string = 'id-mcp-lgesama'
param blueprintId string
param agentIdentityId string
param channelAppId string
param mcpAppId string
param hostImage string
param mcpImage string
param tenantId string = tenant().tenantId
param foundryEndpoint string
param foundryModel string
param promptShieldEndpoint string
param applicationInsightsConnectionString string = ''
resource containerEnvironment 'Microsoft.App/managedEnvironments@2026-01-01' existing = { name: environmentName }
resource hostIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: hostIdentityName }
resource mcpIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: mcpIdentityName }
var registry = '${registryName}.azurecr.io'
var profiles = ['news', 'ir', 'market']
var baseEnv = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
]
module mcp 'container-app.bicep' = [for profile in profiles: {
  name: 'lgesama-${profile}'
  params: {
    appName: 'ca-${profile}-lgesama'
    location: location
    environmentId: containerEnvironment.id
    identityId: mcpIdentity.id
    registry: registry
    image: mcpImage
    // Keep demo tools warm: scale-from-zero exceeded the MCP client's 30-second timeout.
    minReplicas: 1
    env: concat(baseEnv, [
      { name: 'McpProfile', value: profile }
      { name: 'McpAuthorization__TenantId', value: tenantId }
      { name: 'McpAuthorization__Audience', value: mcpAppId }
      { name: 'McpAuthorization__RequiredScope', value: 'Mcp.Invoke' }
    ])
  }
}]
var connectionGroups = [for name in ['ServiceConnection', 'OboServiceConnection', 'OboChannelConnection']: [
  { name: 'Connections__${name}__Settings__AuthorityEndpoint', value: '${environment().authentication.loginEndpoint}${tenantId}' }
  { name: 'Connections__${name}__Settings__ClientId', value: name == 'OboChannelConnection' ? channelAppId : blueprintId }
  { name: 'Connections__${name}__Settings__FederatedClientId', value: hostIdentityClientId }
]]
var connectionEnv = flatten(connectionGroups)
module host 'container-app.bicep' = {
  name: 'lgesama-host'
  params: {
    appName: 'ca-agent-lgesama'
    location: location
    environmentId: containerEnvironment.id
    identityId: hostIdentity.id
    registry: registry
    image: hostImage
    external: true
    host: true
    minReplicas: 1
    healthPath: '/api/health/ready'
    env: concat(baseEnv, connectionEnv, [
      { name: 'AZURE_CLIENT_ID', value: hostIdentity.properties.clientId }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
      { name: 'AgentHost__FoundryProjectEndpoint', value: foundryEndpoint }
      { name: 'AgentHost__FoundryModelDeployment', value: foundryModel }
      { name: 'PromptShield__Endpoint', value: promptShieldEndpoint }
      { name: 'PurviewDlp__TenantId', value: tenantId }
      { name: 'PurviewDlp__ApplicationId', value: blueprintId }
      { name: 'PurviewDlp__UseCompatibilityProxy', value: 'true' }
      { name: 'TokenValidation__TenantId', value: tenantId }
      { name: 'TokenValidation__Audiences__OnBehalfOf', value: channelAppId }
      { name: 'AgentIdentityObo__AgentId', value: agentIdentityId }
      { name: 'InternalMcp__Audience', value: 'api://${mcpAppId}' }
      { name: 'InternalMcp__NewsEndpoint', value: 'https://${mcp[0].outputs.fqdn}/mcp' }
      { name: 'InternalMcp__IrEndpoint', value: 'https://${mcp[1].outputs.fqdn}/mcp' }
      { name: 'InternalMcp__MarketEndpoint', value: 'https://${mcp[2].outputs.fqdn}/mcp' }
      { name: 'Agent365Observability__TenantId', value: tenantId }
      { name: 'Agent365Observability__AgentBlueprintId', value: blueprintId }
      { name: 'Agent365Observability__ClientId', value: blueprintId }
      { name: 'ENABLE_A365_OBSERVABILITY_EXPORTER', value: 'true' }
    ])
  }
}
output messagingEndpoint string = 'https://${host.outputs.fqdn}/api/messages/obo'
output hostFqdn string = host.outputs.fqdn
