#Requires -Version 7.0
<#
.SYNOPSIS
Add the LGES agent blueprint to an EXISTING Purview DLP policy.
.DESCRIPTION
Default: inspect and print the proposed change. Use -Apply to save it.
Preserves existing targets, rules, enforcement mode and notifications. Does not
create policies or rules. Repeated application is a no-op for an existing target.
The agent's PurviewDlp__ApplicationId must equal BlueprintAppId; channel OAuth
audiences are separate and must not be changed to implement this policy scope.
Backups and verification results are saved under .local/purview in this project.
No passwords or tokens are accepted or stored by this script.
.EXAMPLE
./Set-LgesPurviewDlpPolicy.ps1 -TenantId <tenant-guid> -BlueprintAppId <blueprint-guid> -UserPrincipalName <admin-upn>
.EXAMPLE
./Set-LgesPurviewDlpPolicy.ps1 -TenantId <tenant-guid> -BlueprintAppId <blueprint-guid> -UserPrincipalName <admin-upn> -Apply
.EXAMPLE
./Set-LgesPurviewDlpPolicy.ps1 -TenantId <tenant-guid> -BlueprintAppId <blueprint-guid> -UseExistingSession -Apply -WhatIf
.NOTES
Prerequisite: Install-Module ExchangeOnlineManagement -Scope CurrentUser
Microsoft reference supplied for this demo:
https://github.com/microsoft/purview-api-samples/blob/main/DLPforCustomAIApps/Create-DlpPolicyForCustomAIApps.ps1
For EXISTING policy targeting, use Set's additive AddInclusions contract:
https://learn.microsoft.com/powershell/module/exchangepowershell/set-dlpcompliancepolicy#-locations
The reference sample also changes rules/mode; this script intentionally performs
only the requested additive target update.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [ValidateNotNullOrEmpty()]
    [string]$PolicyName = 'a365-chat-app-hosted-dlp',
    [Parameter(Mandatory)][guid]$BlueprintAppId,
    [string]$BlueprintDisplayName = 'LG Energy Solution AMA',
    [Parameter(Mandatory)][guid]$TenantId,
    [string]$UserPrincipalName,
    [switch]$UseExistingSession,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

function Get-Locations($Policy) {
    if ([string]::IsNullOrWhiteSpace([string]$Policy.Locations)) { return }
    $Policy.Locations | ConvertFrom-Json -AsHashtable
}

function Get-CanonicalJson($Value) {
    function Convert-Canonical($Item) {
        if ($null -eq $Item) { return $null }
        if ($Item -is [System.Collections.IDictionary]) {
            $ordered = [ordered]@{}
            foreach ($key in @($Item.Keys | Sort-Object -CaseSensitive)) {
                $ordered[$key] = Convert-Canonical $Item[$key]
            }
            return $ordered
        }
        if ($Item -is [array]) {
            $array = @($Item | ForEach-Object { Convert-Canonical $_ })
            return ,$array
        }
        return $Item
    }
    $plain = ConvertTo-Json -InputObject $Value -Depth 80 -Compress | ConvertFrom-Json -AsHashtable
    ConvertTo-Json -InputObject (Convert-Canonical $plain) -Depth 80 -Compress
}

function Get-RuleFingerprint($Rules) {
    # Compare every exposed rule property except transport/server metadata.
    $volatile = @('OriginatingServer','ObjectState','WhenChanged','WhenChangedUTC',
        'ObjectVersion','DirectoryObjectVersion','LastModifiedBy','ErrorMetadata')
    $normalized = foreach ($rule in @($Rules | Sort-Object Name)) {
        $map = ConvertTo-Json -InputObject $rule -Depth 80 | ConvertFrom-Json -AsHashtable
        foreach ($key in $volatile) { $map.Remove($key) | Out-Null }
        $map
    }
    Get-CanonicalJson @($normalized)
}

function Test-TargetScope($Locations) {
    $targets = @($Locations | Where-Object {
        $_.Workload -eq 'Applications' -and $_.Location -eq $BlueprintAppId.ToString()
    })
    if ($targets.Count -eq 0) { return $false }
    if ($targets.Count -ne 1) { throw 'Duplicate blueprint targets require manual review.' }
    $target = $targets[0]
    $all = @($target.Inclusions | Where-Object { $_.Type -eq 'Tenant' -and $_.Identity -eq 'All' })
    if ($target.LocationSource -ne 'Entra' -or $target.LocationType -ne 'Individual' -or
        $all.Count -ne 1 -or @($target.Exclusions | Where-Object { $null -ne $_ }).Count -gt 0) {
        throw 'Blueprint target has a different or restricted scope. Refusing to broaden it automatically.'
    }
    return $true
}

$createdConnection = $null
try {
    if (-not $UseExistingSession) {
        if ([string]::IsNullOrWhiteSpace($UserPrincipalName)) { throw 'Specify -UserPrincipalName for the compliance administrator, or use -UseExistingSession.' }
        Import-Module ExchangeOnlineManagement -ErrorAction Stop
        $previousIds = @(Get-ConnectionInformation | ForEach-Object ConnectionId)
        Connect-IPPSSession -UserPrincipalName $UserPrincipalName -DisableWAM -ShowBanner:$false
        $createdConnection = @(Get-ConnectionInformation | Where-Object { $_.ConnectionId -notin $previousIds })
    }
    $connections = @(Get-ConnectionInformation | Where-Object {
        $_.State -eq 'Connected' -and $_.ConnectionUri -match '^https://[^/]*\.compliance\.protection\.outlook\.com/?$'
    })
    if ($connections.Count -ne 1 -or [string]$connections[0].TenantID -ne $TenantId.ToString()) {
        throw 'Exactly one Security & Compliance session for the expected tenant is required. No change made.'
    }
    foreach ($name in @('Get-DlpCompliancePolicy','Set-DlpCompliancePolicy','Get-DlpComplianceRule')) {
        Get-Command $name -ErrorAction Stop | Out-Null
    }
    $policy = Get-DlpCompliancePolicy -Identity $PolicyName -DistributionDetail
    if ($policy.Mode -ne 'Enable' -or $policy.Enabled -ne $true -or
        'Application' -notin @($policy.EnforcementPlanes)) {
        throw 'Existing policy must already be enabled on the Application enforcement plane. No change made.'
    }
    $rules = @(Get-DlpComplianceRule -Policy $policy.Identity)
    if ($rules.Count -eq 0) { throw 'Policy has no rules. No change made.' }
    $beforeLocations = @(Get-Locations $policy)
    $alreadyPresent = Test-TargetScope $beforeLocations
    $delta = ConvertTo-Json -InputObject @(@{
        Workload = 'Applications'
        Location = $BlueprintAppId.ToString()
        LocationDisplayName = $BlueprintDisplayName
        LocationSource = 'Entra'
        LocationType = 'Individual'
        AddInclusions = @(@{ Type = 'Tenant'; Identity = 'All' })
    }) -Depth 8 -Compress

    [pscustomobject]@{
        Policy = $policy.Name; TenantId = $TenantId; BlueprintAppId = $BlueprintAppId
        AlreadyPresent = $alreadyPresent; Mode = $policy.Mode
        DistributionStatus = $policy.DistributionStatus; ExistingTargets = $beforeLocations.Count
        RuleCount = $rules.Count; ProposedAdditiveLocations = $delta
    } | Format-List | Out-Host

    if ($alreadyPresent) { Write-Host 'Blueprint already included; no change needed.'; return }
    if (-not $Apply) { Write-Host 'Inspection only. Run with -Apply to add the blueprint.'; return }
    if (-not $PSCmdlet.ShouldProcess($PolicyName, "Add blueprint $BlueprintAppId; retain all existing targets and rules")) { return }

    $backup = Join-Path $PSScriptRoot ('.local/purview/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    New-Item -ItemType Directory -Path $backup -Force -WhatIf:$false | Out-Null
    $policy | ConvertTo-Json -Depth 80 | Set-Content (Join-Path $backup 'policy-before.json') -Encoding utf8
    ConvertTo-Json -InputObject $rules -Depth 80 | Set-Content (Join-Path $backup 'rules-before.json') -Encoding utf8
    $delta | Set-Content (Join-Path $backup 'locations-delta.json') -Encoding utf8
    $beforeRules = Get-RuleFingerprint $rules
    $policyFields = @('Mode','Enabled','EnforcementPlanes','Priority','PolicyRBACScopes')
    $beforeSettings = Get-CanonicalJson ($policy | Select-Object -Property $policyFields)

    # Only additive location targeting: no mode, rule, priority or notification setter.
    Set-DlpCompliancePolicy -Identity $policy.Identity -Locations $delta -Confirm:$false | Out-Null

    $after = Get-DlpCompliancePolicy -Identity $policy.Identity -DistributionDetail
    $afterRules = @(Get-DlpComplianceRule -Policy $policy.Identity)
    $after | ConvertTo-Json -Depth 80 | Set-Content (Join-Path $backup 'policy-after.json') -Encoding utf8
    ConvertTo-Json -InputObject $afterRules -Depth 80 | Set-Content (Join-Path $backup 'rules-after.json') -Encoding utf8
    $afterLocations = @(Get-Locations $after)
    if (-not (Test-TargetScope $afterLocations)) { throw "Blueprint target not visible after save. Inspect $backup before retrying." }
    foreach ($old in $beforeLocations) {
        $match = @($afterLocations | Where-Object { $_.Workload -eq $old.Workload -and $_.Location -eq $old.Location })
        if ($match.Count -ne 1 -or (Get-CanonicalJson $match[0]) -cne (Get-CanonicalJson $old)) {
            throw "An existing target changed unexpectedly. Inspect $backup; do not overwrite concurrent edits."
        }
    }
    if ($beforeSettings -cne (Get-CanonicalJson ($after | Select-Object -Property $policyFields)) -or
        $beforeRules -cne (Get-RuleFingerprint $afterRules)) {
        throw "Policy settings or rule content changed unexpectedly. Inspect $backup; no automatic rollback attempted."
    }
    $result = [ordered]@{
        VerifiedAtUtc = [DateTime]::UtcNow.ToString('o'); PolicyName = $after.Name
        TenantId = $TenantId.ToString(); BlueprintAppId = $BlueprintAppId.ToString()
        TargetAdded = $true; ExistingTargetsPreserved = $true; RulesUnchanged = $true
        PolicySettingsUnchanged = $true; DistributionStatus = [string]$after.DistributionStatus
        BackupDirectory = $backup
    }
    $result | ConvertTo-Json | Set-Content (Join-Path $backup 'result.json') -Encoding utf8
    $result | ConvertTo-Json | Write-Output
    Write-Host 'Policy saved and read back. Runtime enforcement must be checked separately after policy propagation.'
} finally {
    foreach ($connection in @($createdConnection | Where-Object { $null -ne $_ })) {
        Disconnect-ExchangeOnline -ConnectionId $connection.ConnectionId -Confirm:$false -ErrorAction SilentlyContinue
    }
}
