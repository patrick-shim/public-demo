#Requires -Version 7.0
# Executes an isolated copy with in-memory command mocks. No cloud calls or authentication.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('lges-purview-policy-test-' + [guid]::NewGuid().ToString('N'))
$fixtureScript = Join-Path $fixtureRoot 'Set-LgesPurviewDlpPolicy.ps1'
$expectedTenant = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$expectedBlueprint = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'

function Assert-Test([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
}

function Copy-TestValue($Value) {
    ConvertTo-Json -InputObject $Value -Depth 40 -Compress | ConvertFrom-Json
}

function Get-ConnectionInformation {
    [pscustomobject]@{
        ConnectionId = 'mock-compliance-session'
        State = 'Connected'
        TenantID = $global:LgesPurviewPolicyMock.Tenant
        ConnectionUri = 'https://nam12b.ps.compliance.protection.outlook.com'
    }
}

function Connect-IPPSSession { throw 'Test must never authenticate.' }
function Disconnect-ExchangeOnline { throw 'Test must not disconnect a real session.' }
function Import-Module { throw 'Test must not load a real cloud module.' }

function Get-DlpCompliancePolicy {
    [CmdletBinding()]
    param([string]$Identity, [switch]$DistributionDetail)
    $state = $global:LgesPurviewPolicyMock
    $state.PolicyReads++
    Assert-Test ($Identity -in @($state.Policy.Name, $state.Policy.Identity)) 'Read only the selected policy.'
    Assert-Test $DistributionDetail.IsPresent 'Read back policy distribution details.'
    Copy-TestValue $state.Policy
}

function Get-DlpComplianceRule {
    [CmdletBinding()]
    param([string]$Policy)
    $state = $global:LgesPurviewPolicyMock
    $state.RuleReads++
    Assert-Test ($Policy -eq $state.Policy.Identity) 'Read rules only from the selected policy.'
    Copy-TestValue $state.Rules
}

function Set-DlpCompliancePolicy {
    [CmdletBinding()]
    param([string]$Identity, [string]$Locations, [switch]$Confirm)
    $state = $global:LgesPurviewPolicyMock
    $state.SetCalls++
    $state.SetterParameters = @($PSBoundParameters.Keys)
    Assert-Test ($Identity -eq $state.Policy.Identity) 'Update only the inspected policy identity.'
    Assert-Test (@($PSBoundParameters.Keys | Where-Object { $_ -notin @('Identity','Locations','Confirm') }).Count -eq 0) 'Setter must not change unrelated policy fields.'
    $delta = @($Locations | ConvertFrom-Json -AsHashtable)
    Assert-Test ($delta.Count -eq 1) 'Send one additive application target.'
    $target = $delta[0]
    $expectedFields = @('Workload','Location','LocationDisplayName','LocationSource','LocationType','AddInclusions')
    Assert-Test (@($target.Keys | Where-Object { $_ -notin $expectedFields }).Count -eq 0 -and $target.Count -eq $expectedFields.Count) 'Delta must use AddInclusions without replacements or removals.'
    Assert-Test ($target.Workload -eq 'Applications' -and $target.Location -eq $expectedBlueprint) 'Add only the LGES blueprint.'
    Assert-Test ($target.LocationSource -eq 'Entra' -and $target.LocationType -eq 'Individual') 'Use the Entra individual application location.'
    Assert-Test ($target.AddInclusions.Count -eq 1 -and $target.AddInclusions[0].Type -eq 'Tenant' -and $target.AddInclusions[0].Identity -eq 'All') 'Target inclusions must match the reviewed scope.'
    $state.Delta = Copy-TestValue $target
    $storedLocations = @($state.Policy.Locations | ConvertFrom-Json)
    if (-not $state.SkipTarget) {
        $storedLocations += [pscustomobject]@{
            Workload = $target.Workload
            Location = $target.Location
            LocationDisplayName = $target.LocationDisplayName
            LocationSource = $target.LocationSource
            LocationType = $target.LocationType
            Inclusions = @($target.AddInclusions)
            Exclusions = @()
        }
    }
    if ($state.Drift -eq 'location') { $storedLocations[0].LocationDisplayName = 'Concurrent edit' }
    if ($state.Drift -eq 'rule') { $state.Rules[0].NotifyUser = @('DifferentRecipient') }
    if ($state.Drift -eq 'mode') { $state.Policy.Mode = 'TestWithNotifications' }
    $state.Policy.Locations = ConvertTo-Json -InputObject $storedLocations -Depth 20 -Compress
}

function New-TestState {
    $locations = @(
        [pscustomobject]@{
            Workload = 'Applications'; Location = '11111111-1111-4111-8111-111111111111'
            LocationDisplayName = 'Existing agent'; LocationSource = 'Entra'; LocationType = 'Individual'
            Inclusions = @([pscustomobject]@{ Type = 'Tenant'; Identity = 'All' }); Exclusions = @()
        },
        [pscustomobject]@{
            Workload = 'Applications'; Location = '22222222-2222-4222-8222-222222222222'
            LocationDisplayName = 'Restricted existing agent'; LocationSource = 'Entra'; LocationType = 'Individual'
            Inclusions = @([pscustomobject]@{ Type = 'Group'; Identity = 'existing-group' })
            Exclusions = @([pscustomobject]@{ Type = 'User'; Identity = 'existing-exclusion' })
        }
    )
    @{
        Tenant = $expectedTenant; PolicyReads = 0; RuleReads = 0; SetCalls = 0
        SetterParameters = @(); Delta = $null; SkipTarget = $false; Drift = $null
        Policy = [pscustomobject]@{
            Name = 'Mock existing DLP'; Identity = 'mock-policy-identity'; Mode = 'Enable'; Enabled = $true
            EnforcementPlanes = @('Application'); Priority = 3; PolicyRBACScopes = @('existing-rbac-scope')
            DistributionStatus = 'Success'; Locations = (ConvertTo-Json -InputObject $locations -Depth 20 -Compress)
            Comment = 'Existing customer policy'
        }
        Rules = @(
            [pscustomobject]@{
                Name = 'Block sensitive data'; Identity = 'rule-one'; Disabled = $false; Priority = 0
                AdvancedRule = '{"Condition":"sensitive-information-type"}'; BlockAccess = $true
                NotifyUser = @('LastModifier'); GenerateIncidentReport = @('ExistingOwner')
                NotifyPolicyTipCustomText = 'Existing policy tip'; WhenChanged = '2026-10-01T00:00:00Z'
            },
            [pscustomobject]@{
                Name = 'Audit another condition'; Identity = 'rule-two'; Disabled = $false; Priority = 1
                AdvancedRule = '{"Condition":"another-condition"}'; BlockAccess = $false
                NotifyUser = @(); GenerateIncidentReport = @(); NotifyPolicyTipCustomText = ''
                WhenChanged = '2026-10-01T00:00:00Z'
            }
        )
    }
}

function Add-ExistingTarget([switch]$Restricted, [switch]$Duplicate) {
    $state = $global:LgesPurviewPolicyMock
    $locations = @($state.Policy.Locations | ConvertFrom-Json)
    $target = [pscustomobject]@{
        Workload = 'Applications'; Location = $expectedBlueprint
        LocationDisplayName = 'LG Energy Solution AMA'; LocationSource = 'Entra'; LocationType = 'Individual'
        Inclusions = @([pscustomobject]@{ Type = 'Tenant'; Identity = 'All' }); Exclusions = @()
    }
    if ($Restricted) { $target.Exclusions = @([pscustomobject]@{ Type = 'User'; Identity = 'excluded-user' }) }
    $locations += $target
    if ($Duplicate) { $locations += (Copy-TestValue $target) }
    $state.Policy.Locations = ConvertTo-Json -InputObject $locations -Depth 20 -Compress
}

try {
    Assert-Test ($null -eq (Get-Variable LgesPurviewPolicyMock -Scope Global -ErrorAction SilentlyContinue)) 'Reserved mock state must not already exist.'
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'Set-LgesPurviewDlpPolicy.ps1') -Destination $fixtureScript
    $cases = @(
        @{ Name = 'default inspection'; ExpectedSets = 0 },
        @{ Name = 'WhatIf with Apply'; Apply = $true; WhatIf = $true; ExpectedSets = 0 },
        @{ Name = 'additive apply preserves targets and rules'; Apply = $true; ExpectedSets = 1 },
        @{ Name = 'second apply is idempotent'; Apply = $true; Twice = $true; ExpectedSets = 1 },
        @{ Name = 'existing target is no-op'; Apply = $true; Present = $true; ExpectedSets = 0 },
        @{ Name = 'wrong tenant aborts before policy read'; Apply = $true; WrongTenant = $true; ExpectedSets = 0; Error = 'expected tenant' },
        @{ Name = 'restricted target is not broadened'; Apply = $true; Restricted = $true; ExpectedSets = 0; Error = 'restricted scope' },
        @{ Name = 'duplicate target aborts'; Apply = $true; Duplicate = $true; ExpectedSets = 0; Error = 'Duplicate blueprint' },
        @{ Name = 'disabled policy aborts'; Apply = $true; Disabled = $true; ExpectedSets = 0; Error = 'already be enabled' },
        @{ Name = 'policy without rules aborts'; Apply = $true; NoRules = $true; ExpectedSets = 0; Error = 'no rules' },
        @{ Name = 'missing target on read-back detected'; Apply = $true; SkipTarget = $true; ExpectedSets = 1; Error = 'not visible after save' },
        @{ Name = 'existing target drift detected'; Apply = $true; Drift = 'location'; ExpectedSets = 1; Error = 'existing target changed' },
        @{ Name = 'notification rule drift detected'; Apply = $true; Drift = 'rule'; ExpectedSets = 1; Error = 'rule content changed' },
        @{ Name = 'policy mode drift detected'; Apply = $true; Drift = 'mode'; ExpectedSets = 1; Error = 'Policy settings' }
    )
    $passed = 0
    foreach ($case in $cases) {
        $global:LgesPurviewPolicyMock = New-TestState
        $state = $global:LgesPurviewPolicyMock
        if ($case.WrongTenant) { $state.Tenant = '99999999-9999-4999-8999-999999999999' }
        if ($case.Disabled) { $state.Policy.Enabled = $false }
        if ($case.NoRules) { $state.Rules = @() }
        if ($case.Present -or $case.Restricted -or $case.Duplicate) {
            Add-ExistingTarget -Restricted:([bool]$case.Restricted) -Duplicate:([bool]$case.Duplicate)
        }
        $state.SkipTarget = [bool]$case.SkipTarget
        $state.Drift = $case.Drift
        $beforePolicy = Copy-TestValue $state.Policy
        $beforeRules = ConvertTo-Json -InputObject $state.Rules -Depth 40 -Compress
        $beforeFiles = @(Get-ChildItem -LiteralPath $fixtureRoot -File -Recurse).Count
        $failure = $null
        $invoke = @{
            TenantId = $expectedTenant; BlueprintAppId = $expectedBlueprint
            UseExistingSession = $true; PolicyName = $state.Policy.Name
            Apply = [bool]$case.Apply; WhatIf = [bool]$case.WhatIf; Confirm = $false
        }
        try {
            & $fixtureScript @invoke *> $null
            if ($case.Twice) { & $fixtureScript @invoke *> $null }
        } catch { $failure = $_.Exception.Message }

        Assert-Test ($state.SetCalls -eq $case.ExpectedSets) "$($case.Name): setter call count"
        if ($case.Error) {
            Assert-Test ($null -ne $failure -and $failure.Contains($case.Error, [StringComparison]::OrdinalIgnoreCase)) "$($case.Name): expected rejection, got '$failure'"
        } else {
            Assert-Test ($null -eq $failure) "$($case.Name): unexpected failure '$failure'"
            Assert-Test ((ConvertTo-Json -InputObject $state.Rules -Depth 40 -Compress) -ceq $beforeRules) "$($case.Name): every rule and notification property preserved"
            $oldLocations = @($beforePolicy.Locations | ConvertFrom-Json)
            $afterLocations = @($state.Policy.Locations | ConvertFrom-Json)
            foreach ($old in $oldLocations) {
                $match = @($afterLocations | Where-Object Location -EQ $old.Location)
                Assert-Test ($match.Count -eq 1 -and (ConvertTo-Json $match[0] -Depth 20 -Compress) -ceq (ConvertTo-Json $old -Depth 20 -Compress)) "$($case.Name): existing inclusion/exclusion target unchanged"
            }
            Assert-Test ($afterLocations.Count -eq $oldLocations.Count + $case.ExpectedSets) "$($case.Name): only one target added"
            $state.Policy.Locations = $beforePolicy.Locations
            Assert-Test ((ConvertTo-Json $state.Policy -Depth 40 -Compress) -ceq (ConvertTo-Json $beforePolicy -Depth 40 -Compress)) "$($case.Name): every non-location policy setting preserved"
        }
        if ($case.ExpectedSets -eq 0) {
            Assert-Test (@(Get-ChildItem -LiteralPath $fixtureRoot -File -Recurse).Count -eq $beforeFiles) "$($case.Name): no backup or result files written"
        }
        if ($case.WrongTenant) { Assert-Test ($state.PolicyReads -eq 0 -and $state.RuleReads -eq 0) 'Wrong tenant must abort before reading policy data.' }
        $passed++
        Write-Output "PASS: $($case.Name)"
    }
    Write-Output "$passed isolated policy tests passed. No cloud calls or authentication."
} finally {
    Remove-Variable LgesPurviewPolicyMock -Scope Global -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $fixtureRoot) {
        $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedFixture.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($resolvedFixture)).StartsWith('lges-purview-policy-test-', [StringComparison]::Ordinal)) {
            throw 'Refusing to delete a fixture outside the expected temporary directory.'
        }
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
