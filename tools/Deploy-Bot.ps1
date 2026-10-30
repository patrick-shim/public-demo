[CmdletBinding()]
param([switch]$WhatIf)
. "$PSScriptRoot/Tenant.Common.ps1"
Assert-DemoTenant
$state=Get-DemoState
$outputs=Get-Content (Join-Path $PrivateRoot 'app-outputs.json') -Raw|ConvertFrom-Json
$secret=(Get-Content (Join-Path $PrivateRoot 'channel-secret.dpapi') -Raw).Trim()|ConvertTo-SecureString
$credential=[pscredential]::new('oauth',$secret)
$parameterPath=Join-Path $PrivateRoot 'bot.parameters.private.json'
try {
  $p=@{channelAppId=@{value=$state.channelAppId};blueprintId=@{value=$state.blueprintId};messagingEndpoint=@{value=$outputs.messagingEndpoint.value};oauthClientSecret=@{value=$credential.GetNetworkCredential().Password}}
  @{parameters=$p}|ConvertTo-Json -Depth 8|Set-Content $parameterPath -Encoding utf8NoBOM
  $mode=if($WhatIf){'what-if'}else{'create'}
  az deployment group $mode --subscription $SubscriptionId -g $ResourceGroup -n lgesama-bot --template-file (Join-Path $ProjectRoot 'infra/bot.bicep') --parameters "@$parameterPath" --only-show-errors --output none
  if($LASTEXITCODE -ne 0){throw "Bot deployment $mode failed."}
} finally {if(Test-Path -LiteralPath $parameterPath){Remove-Item -LiteralPath $parameterPath};$credential=$null;$secret=$null}
Write-Output "Bot $mode succeeded. Only the Teams channel is configured."
