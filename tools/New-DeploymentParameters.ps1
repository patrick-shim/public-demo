[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_.-]+$')][string]$ReleaseTag)
. "$PSScriptRoot/Tenant.Common.ps1"
Assert-DemoTenant
$state=Get-DemoState
$foundation=Get-Content (Join-Path $PrivateRoot 'foundation.json') -Raw|ConvertFrom-Json
$values=@{
  blueprintId=$state.blueprintId;agentIdentityId=$state.agentIdentityId;channelAppId=$state.channelAppId;mcpAppId=$state.mcpAppId
  hostIdentityClientId=$foundation.hostClientId.value
  tenantId=$TenantId;location=$Location;environmentName=$EnvironmentName;registryName=$RegistryName
  foundryEndpoint=$FoundryEndpoint;foundryModel=$FoundryModel;promptShieldEndpoint=$PromptShieldEndpoint
}
foreach($kind in @('host','mcp')){
  $digest=az acr repository show --subscription $SubscriptionId -n $RegistryName --image "lges-ama/${kind}:$ReleaseTag" --query digest -o tsv
  if($LASTEXITCODE -ne 0 -or $digest -notmatch '^sha256:[a-f0-9]{64}$'){throw "Missing $kind image digest."}
  $values["${kind}Image"]="${RegistryName}.azurecr.io/lges-ama/${kind}@$digest"
}
$parameters=@{}
foreach($key in $values.Keys){if(-not $values[$key]){throw "Missing deployment value: $key"};$parameters[$key]=@{value=$values[$key]}}
@{'$schema'='https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#';contentVersion='1.0.0.0';parameters=$parameters}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $PrivateRoot 'main.parameters.json') -Encoding utf8NoBOM
Write-Output 'Deployment parameters saved with immutable container image digests.'
