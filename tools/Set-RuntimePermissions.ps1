# Run AFTER Agent 365 CLI permissions commands. Full CLI setup may reintroduce broad defaults.
. "$PSScriptRoot/Tenant.Common.ps1"
Assert-DemoTenant
$state=Get-DemoState
if(-not $state.blueprintPrincipalId -or -not $state.agentIdentityId){throw 'Initialize this demo first.'}
$graph=(Invoke-Graph GET ('v1.0/servicePrincipals?$filter='+[uri]::EscapeDataString("appId eq '00000003-0000-0000-c000-000000000000'"))).value[0]
$scopes=@('Content.Process.User','ProtectionScopes.Compute.User','ContentActivity.Write')
Ensure-Grant $state.blueprintPrincipalId $graph.id $scopes
$config=Get-Content (Join-Path $PrivateRoot 'onboarding/a365.config.json') -Raw|ConvertFrom-Json
foreach($permission in $config.customBlueprintPermissions){
  $resource=(Invoke-Graph GET ('v1.0/servicePrincipals?$filter='+[uri]::EscapeDataString("appId eq '$($permission.resourceAppId)'"))).value[0]
  if(-not $resource.id){throw "Resource service principal missing: $($permission.resourceAppId)"}
  Ensure-Grant $state.blueprintPrincipalId $resource.id $permission.scopes
}
# No native Work IQ or Power Platform connector is used. Remove only this new blueprint's unused grants.
$unneeded=@('ea9ffc3e-8a23-4a7d-836d-234d7c7565c1','8578e004-a5c6-46e7-913e-12f58912df43')
foreach($app in $unneeded){
  $sp=(Invoke-Graph GET ('v1.0/servicePrincipals?$filter='+[uri]::EscapeDataString("appId eq '$app'"))).value[0]
  $grants=(Invoke-Graph GET "v1.0/servicePrincipals/$($state.blueprintPrincipalId)/oauth2PermissionGrants").value
  foreach($grant in @($grants|Where-Object resourceId -eq $sp.id)){Invoke-Graph DELETE "v1.0/oauth2PermissionGrants/$($grant.id)"|Out-Null}
}
$scope="/subscriptions/$SubscriptionId/resourceGroups/$FoundryResourceGroup/providers/Microsoft.CognitiveServices/accounts/$FoundryAccountName"
az role assignment create --subscription $SubscriptionId --assignee-object-id $state.agentIdentityId --assignee-principal-type ServicePrincipal --role a97b65f3-24c7-4388-baec-2e87135dc908 --scope $scope --only-show-errors --output none
if($LASTEXITCODE -ne 0){throw 'Foundry agent RBAC assignment failed.'}
# The signed-in OBO caller also needs data-plane access. Do not grant users/groups automatically here.
$grants=(Invoke-Graph GET "v1.0/servicePrincipals/$($state.blueprintPrincipalId)/oauth2PermissionGrants").value
$grants|Select-Object resourceId,scope|ConvertTo-Json -Depth 5|Set-Content (Join-Path $PrivateRoot 'validation/runtime-grants.json')
Write-Output 'Runtime grants narrowed to the demo; agent Foundry RBAC configured.'
