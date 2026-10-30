param channelAppId string
param blueprintId string
param messagingEndpoint string
param tenantId string = tenant().tenantId
@secure()
param oauthClientSecret string
param botName string = 'bot-lgesama'
resource bot 'Microsoft.BotService/botServices@2022-09-15' = {
  name: botName
  location: 'global'
  kind: 'azurebot'
  sku: { name: 'F0' }
  tags: { project: 'lges-ama', purpose: 'customer-demo' }
  properties: {
    displayName: 'LG Energy Solution AMA'
    endpoint: messagingEndpoint
    msaAppId: channelAppId
    msaAppTenantId: tenantId
    msaAppType: 'SingleTenant'
    publicNetworkAccess: 'Enabled'
    isStreamingSupported: false
  }
}
resource teams 'Microsoft.BotService/botServices/channels@2022-09-15' = {
  parent: bot
  name: 'MsTeamsChannel'
  location: 'global'
  properties: { channelName: 'MsTeamsChannel', properties: { isEnabled: true, acceptedTerms: true, enableCalling: false } }
}
resource oauth 'Microsoft.BotService/botServices/connections@2022-09-15' = {
  parent: bot
  name: 'lges-ama-obo'
  location: 'global'
  properties: {
    clientId: channelAppId
    clientSecret: oauthClientSecret
    scopes: 'api://${blueprintId}/access_agent_as_user'
    serviceProviderId: '30dd229c-58e3-4a48-bdfd-91ec48eb906c'
    serviceProviderDisplayName: 'Azure Active Directory v2'
    parameters: [
      { key: 'tenantId', value: tenantId }
      { key: 'tokenExchangeUrl', value: 'api://botid-${channelAppId}' }
    ]
  }
}
output botId string = bot.id
