#Requires -Version 7.0
# Isolated settings and deployment-parameter tests. Never calls Azure.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('lges-lab-settings-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$fixture/tools", "$fixture/.local" -Force | Out-Null
foreach ($name in @('Tenant.Common.ps1','New-DeploymentParameters.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../tools/$name") -Destination "$fixture/tools/$name"
}
$settings = @{
    TenantId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    SubscriptionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
    ResourceGroup = 'rg-test'; Location = 'koreacentral'
    RegistryName = 'labregistry'; EnvironmentName = 'lab-environment'
    FoundryResourceGroup = 'rg-ai-test'; FoundryAccountName = 'lab-ai'
    FoundryEndpoint = 'https://lab-ai.services.ai.azure.com'
    FoundryModel = 'lab-deployment'; PromptShieldEndpoint = 'https://lab-safety.cognitiveservices.azure.com'
}
function Save-Settings { $settings | ConvertTo-Json | Set-Content "$fixture/.local/lab.settings.json" }
function Assert-Test($condition, $message) { if (-not $condition) { throw $message } }
function Assert-Rejected([scriptblock]$action, [string]$expected) {
    $message = ''
    try { & $action } catch { $message = $_.Exception.Message }
    Assert-Test ($message.Contains($expected)) "Expected rejection containing '$expected'; got '$message'."
}
$global:LgesLabSettingsMock = @{ AcrCalls = 0; WrongTenant = $false }
function az {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'account') {
        return (@{ tenantId = $(if ($global:LgesLabSettingsMock.WrongTenant) { 'wrong-tenant' } else { $settings.TenantId }); id = $settings.SubscriptionId } | ConvertTo-Json)
    }
    if ($args[0] -eq 'acr') {
        Assert-Test ($args -contains 'labregistry') 'ACR must use configured registry.'
        Assert-Test ($args -contains $settings.SubscriptionId) 'ACR must use configured subscription.'
        $global:LgesLabSettingsMock.AcrCalls++
        return ('sha256:' + ('a' * 64))
    }
    throw 'Unexpected Azure command in test.'
}
try {
    . "$fixture/tools/Tenant.Common.ps1"
    Assert-Rejected { Import-LabSettings } 'Copy lab.settings.example.json'
    Save-Settings
    Import-LabSettings
    Assert-Test ($RegistryName -eq 'labregistry') 'Import custom registry.'
    $settings.TenantId = '<tenant>'
    Save-Settings
    Assert-Rejected { Import-LabSettings } 'Configure TenantId'
    $settings.TenantId = 'not-a-guid'
    Save-Settings
    Assert-Rejected { Import-LabSettings } 'Invalid TenantId'
    $settings.TenantId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    $settings.FoundryEndpoint += '/api/projects/wrong'
    Save-Settings
    Assert-Rejected { Import-LabSettings } 'HTTPS account endpoint'
    $settings.FoundryEndpoint = 'https://lab-ai.services.ai.azure.com'
    Save-Settings
    @{blueprintId='blueprint';agentIdentityId='agent';channelAppId='channel';mcpAppId='mcp'} | ConvertTo-Json | Set-Content "$fixture/.local/deployment.json"
    @{hostClientId=@{value='host-client'}} | ConvertTo-Json | Set-Content "$fixture/.local/foundation.json"
    $global:LgesLabSettingsMock.WrongTenant = $true
    Assert-Rejected { & "$fixture/tools/New-DeploymentParameters.ps1" -ReleaseTag test-1 } 'Select the tenant'
    Assert-Test ($global:LgesLabSettingsMock.AcrCalls -eq 0) 'Mismatch must stop before accessing ACR.'
    $global:LgesLabSettingsMock.WrongTenant = $false
    & "$fixture/tools/New-DeploymentParameters.ps1" -ReleaseTag test-1
    $parameters = (Get-Content "$fixture/.local/main.parameters.json" -Raw | ConvertFrom-Json).parameters
    foreach ($pair in @(@('registryName','labregistry'),@('environmentName','lab-environment'),@('foundryModel','lab-deployment'),@('hostIdentityClientId','host-client'))) {
        Assert-Test ($parameters.($pair[0]).value -eq $pair[1]) "Incorrect $($pair[0])."
    }
    Assert-Test ($parameters.hostImage.value -eq ('labregistry.azurecr.io/lges-ama/host@sha256:' + ('a' * 64))) 'Pin configured registry image by digest.'
    Assert-Test ($global:LgesLabSettingsMock.AcrCalls -eq 2) 'Resolve both image digests.'
    Write-Output 'PASS: missing/placeholder/invalid settings rejected, account endpoint enforced, tenant mismatch stops ACR, custom settings and digests preserved.'
} finally {
    # Remove only the known isolated fixture after verifying its absolute location.
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'lges-lab-settings-*') { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    Remove-Variable -Name LgesLabSettingsMock -Scope Global
}
