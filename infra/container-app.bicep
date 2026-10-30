param appName string
param location string
param environmentId string
param identityId string
param registry string
param image string
param external bool = false
param env array
param healthPath string = '/health'
param minReplicas int = 0
param host bool = false
resource app 'Microsoft.App/containerApps@2026-01-01' = {
  name: appName
  location: location
  tags: { project: 'lges-ama', purpose: 'customer-demo' }
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identityId}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: external, targetPort: 8080, allowInsecure: false, transport: 'auto', traffic: [{ latestRevision: true, weight: 100 }] }
      registries: [{ server: registry, identity: identityId }]
    }
    template: {
      containers: [{
        name: 'app'
        image: image
        env: env
        resources: { cpu: host ? any('0.5') : any('0.25'), memory: host ? '1Gi' : '0.5Gi' }
        probes: [
          { type: 'Liveness', httpGet: { path: host ? '/api/health/live' : healthPath, port: 8080 }, initialDelaySeconds: 20, periodSeconds: 30, timeoutSeconds: 5, failureThreshold: 3 }
          { type: 'Readiness', httpGet: { path: healthPath, port: 8080 }, initialDelaySeconds: 10, periodSeconds: 10, timeoutSeconds: 5, failureThreshold: 6 }
        ]
      }]
      // The host uses MemoryStorage; more than one replica would split conversations.
      scale: { minReplicas: minReplicas, maxReplicas: 1 }
    }
  }
}
output fqdn string = app.properties.configuration.ingress.fqdn
