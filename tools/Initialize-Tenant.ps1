# New AMA registrations only; never reads or changes the reference registrations.
. "$PSScriptRoot/Tenant.Common.ps1"
Assert-DemoTenant
$state=Get-DemoState
$generated=Get-Content (Join-Path $PrivateRoot 'onboarding/a365.generated.config.json') -Raw|ConvertFrom-Json
$state.blueprintId=$generated.agentBlueprintId
$state.blueprintPrincipalId=$generated.agentBlueprintServicePrincipalObjectId
$state.agentIdentityId=$generated.agenticAppId
if(-not $state.agentIdentityId){throw 'Complete a365 OBO setup first.'}
$operator=Invoke-Graph GET 'v1.0/me'
foreach($kind in @('mcp','channel')){
  $name=if($kind -eq 'mcp'){'LGES AMA MCP API'}else{'LGES AMA OBO Channel'}
  if(-not $state["${kind}AppId"]){
    $found=Invoke-Graph GET ('v1.0/applications?$filter='+[uri]::EscapeDataString("displayName eq '$name'"))
    $apps=@($found.value | Where-Object { $null -ne $_ })
    if($apps.Count -gt 1){throw "More than one $name application; resolve before continuing."}
    $app=if($apps.Count -eq 1){$apps[0]}else{Invoke-Graph POST 'v1.0/applications' @{displayName=$name;signInAudience='AzureADMyOrg';tags=@('lges-ama','customer-demo')}}
    $state["${kind}AppId"]=$app.appId
    $state["${kind}ObjectId"]=$app.id
    Save-DemoState $state
  }
  $sp=(Invoke-Graph GET ('v1.0/servicePrincipals?$filter='+[uri]::EscapeDataString("appId eq '$($state["${kind}AppId"])'"))).value
  if(-not $sp){$sp=Invoke-Graph POST 'v1.0/servicePrincipals' @{appId=$state["${kind}AppId"]}}
  $state["${kind}PrincipalId"]=@($sp)[0].id
  $owners=(Invoke-Graph GET "v1.0/applications/$($state["${kind}ObjectId"])/owners").value
  if($operator.id -notin @($owners.id)){Invoke-Graph POST ('v1.0/applications/'+$state["${kind}ObjectId"]+'/owners/$ref') @{'@odata.id'="https://graph.microsoft.com/v1.0/directoryObjects/$($operator.id)"}|Out-Null}
}
if(-not $state.mcpScopeId){$state.mcpScopeId=[guid]::NewGuid().ToString()}
if(-not $state.channelScopeId){$state.channelScopeId=[guid]::NewGuid().ToString()}
if(-not $state.teamsAppId){$state.teamsAppId=[guid]::NewGuid().ToString()}
Save-DemoState $state
Invoke-Graph PATCH "v1.0/applications/$($state.mcpObjectId)" @{
  identifierUris=@("api://$($state.mcpAppId)")
  api=@{requestedAccessTokenVersion=2;oauth2PermissionScopes=@(@{id=$state.mcpScopeId;value='Mcp.Invoke';type='Admin';isEnabled=$true;adminConsentDisplayName='Invoke LGES AMA MCP';adminConsentDescription='Invoke the read-only LGES AMA company information tools.'})}
}|Out-Null
$bp=Invoke-Graph GET "beta/applications/$($state.blueprintId)"
$ingress=@($bp.api.oauth2PermissionScopes|Where-Object value -eq 'access_agent_as_user')[0]
if(-not $ingress){throw 'Blueprint ingress scope was not created.'}
Invoke-Graph PATCH "v1.0/applications/$($state.channelObjectId)" @{
  identifierUris=@("api://botid-$($state.channelAppId)")
  web=@{redirectUris=@('https://token.botframework.com/.auth/web/redirect')}
  api=@{requestedAccessTokenVersion=2;oauth2PermissionScopes=@(@{id=$state.channelScopeId;value='access_as_user';type='User';isEnabled=$true;adminConsentDisplayName='Use LGES AMA';adminConsentDescription='Use the LGES AMA demo as the signed-in user.';userConsentDisplayName='Use LGES AMA';userConsentDescription='Use the LGES AMA demo as you.'})}
  requiredResourceAccess=@(@{resourceAppId=$state.blueprintId;resourceAccess=@(@{id=$ingress.id;type='Scope'})})
}|Out-Null
# Scope must exist before preauthorization can reference it.
Invoke-Graph PATCH "v1.0/applications/$($state.channelObjectId)" @{
  api=@{preAuthorizedApplications=@(
    @{appId='1fec8e78-bce4-4aaf-ab1b-5451cc387264';delegatedPermissionIds=@($state.channelScopeId)},
    @{appId='5e3ce6c0-2b1f-4285-8d4b-75ee78787346';delegatedPermissionIds=@($state.channelScopeId)}
  )}
}|Out-Null
Ensure-Grant $state.channelPrincipalId $state.blueprintPrincipalId @('access_agent_as_user')
$foundation=Get-Content (Join-Path $PrivateRoot 'foundation.json') -Raw|ConvertFrom-Json
foreach($target in @(@{id=$state.blueprintId;api='beta'},@{id=$state.channelObjectId;api='v1.0'})){
  $ficPath="$($target.api)/applications/$($target.id)/federatedIdentityCredentials"
  $fics=(Invoke-Graph GET $ficPath).value
  if('lgesama-container-host' -notin @($fics.name)){
    Invoke-Graph POST $ficPath @{name='lgesama-container-host';issuer="https://login.microsoftonline.com/$TenantId/v2.0";subject=$foundation.hostPrincipalId.value;audiences=@('api://AzureADTokenExchange')}|Out-Null
  }
}
# Bot Token Service needs an OAuth client secret. Persist it only as Windows DPAPI ciphertext.
$secretFile=Join-Path $PrivateRoot 'channel-secret.dpapi'
if(-not (Test-Path $secretFile)){
  $credential=Invoke-Graph POST "v1.0/applications/$($state.channelObjectId)/addPassword" @{passwordCredential=@{displayName='LGES AMA Bot OAuth';endDateTime=(Get-Date).ToUniversalTime().AddMonths(6).ToString('o')}}
  ConvertTo-SecureString $credential.secretText -AsPlainText -Force|ConvertFrom-SecureString|Set-Content $secretFile
  $state.channelCredentialId=$credential.keyId
  $state.channelCredentialExpires=$credential.endDateTime
  $credential=$null
}
$configPath=Join-Path $PrivateRoot 'onboarding/a365.config.json'
$config=Get-Content $configPath -Raw|ConvertFrom-Json -AsHashtable
$config.customBlueprintPermissions=@(
  @{resourceAppId=$state.mcpAppId;scopes=@('Mcp.Invoke')},
  @{resourceAppId='18a66f5f-dbdf-4c17-9dd7-1634712a9cbe';scopes=@('user_impersonation')},
  @{resourceAppId='7d312290-28c8-473c-a0ed-8e53749b6d6d';scopes=@('user_impersonation')},
  @{resourceAppId='00000003-0000-0000-c000-000000000000';scopes=@('Content.Process.User','ProtectionScopes.Compute.User','ContentActivity.Write')}
)
$config|ConvertTo-Json -Depth 15|Set-Content $configPath -Encoding utf8NoBOM
Save-DemoState $state
Write-Output 'AMA MCP API, OBO channel, federation and encrypted OAuth credential are configured.'
