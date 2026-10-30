$ErrorActionPreference='Stop'
$ProjectRoot = Split-Path $PSScriptRoot -Parent
$PrivateRoot = Join-Path $ProjectRoot '.local'
function Import-LabSettings {
  $path = Join-Path $PrivateRoot 'lab.settings.json'
  if (-not (Test-Path -LiteralPath $path)) { throw 'Copy lab.settings.example.json to .local/lab.settings.json and fill in your environment first.' }
  $settings = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
  foreach ($key in @('TenantId','SubscriptionId','ResourceGroup','Location','RegistryName','EnvironmentName','FoundryResourceGroup','FoundryAccountName','FoundryEndpoint','FoundryModel','PromptShieldEndpoint')) {
    if ([string]::IsNullOrWhiteSpace($settings[$key]) -or $settings[$key] -match '<|>') { throw "Configure $key in .local/lab.settings.json." }
  }
  foreach ($key in @('TenantId','SubscriptionId')) {
    $id = [guid]::Empty
    if (-not [guid]::TryParse($settings[$key], [ref]$id) -or $id -eq [guid]::Empty) { throw "Invalid $key in lab settings." }
  }
  foreach ($key in @('FoundryEndpoint','PromptShieldEndpoint')) {
    $uri = $null
    if (-not [uri]::TryCreate($settings[$key], [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) { throw "$key must be an HTTPS account endpoint with no path, query or credentials." }
  }
  foreach ($key in $settings.Keys) {
    if ($key -in @('TenantId','SubscriptionId','ResourceGroup','Location','RegistryName','EnvironmentName','FoundryResourceGroup','FoundryAccountName','FoundryEndpoint','FoundryModel','PromptShieldEndpoint')) { Set-Variable -Name $key -Value $settings[$key] -Scope Script }
  }
  New-Item -ItemType Directory -Path (Join-Path $PrivateRoot 'validation') -Force | Out-Null
}
function Invoke-Graph([string]$Method,[string]$Path,$Body=$null) {
  $argsList=@('rest','--method',$Method,'--url',"https://graph.microsoft.com/$Path",'--output','json','--only-show-errors')
  $tmp=$null
  try {
    if($null -ne $Body){
      $tmp=Join-Path $PrivateRoot ('request-'+[guid]::NewGuid()+'.json')
      $Body|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $tmp -Encoding utf8NoBOM
      $argsList+=@('--body',"@$tmp",'--headers','Content-Type=application/json')
    }
    $raw=& az @argsList
    if($LASTEXITCODE -ne 0){throw "Graph $Method failed for $Path"}
    if($raw){return ($raw -join "`n"|ConvertFrom-Json -AsHashtable)}
  } finally {if($tmp -and (Test-Path -LiteralPath $tmp)){Remove-Item -LiteralPath $tmp}}
}
function Assert-DemoTenant {
  Import-LabSettings
  $account=az account show -o json|ConvertFrom-Json
  if($LASTEXITCODE -ne 0 -or $account.tenantId -ne $TenantId -or $account.id -ne $SubscriptionId){throw 'Select the tenant and subscription configured in .local/lab.settings.json before proceeding.'}
}
function Save-DemoState($State){$State|ConvertTo-Json -Depth 15|Set-Content (Join-Path $PrivateRoot 'deployment.json') -Encoding utf8NoBOM}
function Get-DemoState {
  $path=Join-Path $PrivateRoot 'deployment.json'
  if(Test-Path $path){return (Get-Content $path -Raw|ConvertFrom-Json -AsHashtable)}
  return @{}
}
function Ensure-Grant([string]$Client,[string]$Resource,[string[]]$Scopes) {
  $existing=(Invoke-Graph GET "v1.0/servicePrincipals/$Client/oauth2PermissionGrants").value | Where-Object {$_.resourceId -eq $Resource -and $_.consentType -eq 'AllPrincipals'}
  if(@($existing).Count -gt 1){throw 'Unexpected duplicate tenant-wide grants; inspect before continuing.'}
  $scope=($Scopes|Sort-Object -Unique)-join ' '
  if($existing){Invoke-Graph PATCH "v1.0/oauth2PermissionGrants/$($existing.id)" @{scope=$scope}|Out-Null}
  else{Invoke-Graph POST 'v1.0/oauth2PermissionGrants' @{clientId=$Client;resourceId=$Resource;consentType='AllPrincipals';scope=$scope}|Out-Null}
}
