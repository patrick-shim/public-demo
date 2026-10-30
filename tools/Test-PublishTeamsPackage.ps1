#Requires -Version 7.0
# Runs an isolated copy with mocked authentication/Graph calls. Never publishes or edits project state.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('lges-publish-test-' + [guid]::NewGuid().ToString('N'))
$fixtureTools = Join-Path $fixtureRoot 'tools'
$fixtureArtifacts = Join-Path $fixtureRoot 'artifacts'
$fixturePackages = Join-Path $fixtureArtifacts 'packages'
$fixtureScript = Join-Path $fixtureTools 'Publish-TeamsPackage.ps1'
$fixtureCommon = Join-Path $fixtureTools 'Tenant.Common.ps1'
$fixtureManifest = Join-Path $fixtureRoot 'manifest.json'
$fixtureZip = Join-Path $fixturePackages 'LGES-AMA-Teams-Copilot.zip'
$appId = '11111111-2222-3333-4444-555555555555'
$catalogId = 'catalog+id/opaque='
$catalogBase = 'https://graph.microsoft.com/v1.0/appCatalogs/teamsApps'

function Assert-Test([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
}

function Connect-MgGraph {
    param($TenantId, $Scopes, $ContextScope, [switch]$NoWelcome)
    Assert-Test ($Scopes -eq 'AppCatalog.ReadWrite.All') 'Do not expand the app catalog permission.'
    $global:PublishPackageTest.AuthCalls++
}
function az {
    $global:LASTEXITCODE = 0
    $global:PublishPackageTest.AuthCalls++
    return 'mock-access-token'
}
function Invoke-MgGraphRequest {
    param($Method, $Uri, $ContentType, $InputFilePath)
    Invoke-TestCatalog $Method $Uri $ContentType $InputFilePath
}
function Invoke-RestMethod {
    param($Method, $Uri, $Headers, $ContentType, $InFile)
    Assert-Test ($Headers.Authorization -eq 'Bearer mock-access-token') 'CLI transport must use the mocked token.'
    Invoke-TestCatalog $Method $Uri $ContentType $InFile
}
function Invoke-TestCatalog($Method, $Uri, $ContentType, $InputFile) {
    $context = $global:PublishPackageTest
    $context.Requests.Add(@{ Method = $Method; Uri = $Uri })
    if ($Method -eq 'GET') {
        if ($Uri.StartsWith($catalogBase + '?')) { return @{ value = @($context.Existing | Where-Object { $null -ne $_ }) } }
        $expectedDefinitions = $catalogBase + '/' + [uri]::EscapeDataString($catalogId) + '/appDefinitions'
        Assert-Test ($Uri.StartsWith($expectedDefinitions + '?')) 'Definition lookup must use the catalog ID.'
        if ($context.Paged -and $Uri.Contains('skiptoken')) { return @{ value = @(@{ version = '1.0.10' }) } }
        $response = @{ value = @(@{ version = $context.CatalogVersion }) }
        if ($context.Paged) { $response['@odata.nextLink'] = $expectedDefinitions + '?$skiptoken=next' }
        return $response
    }
    Assert-Test ($Method -eq 'POST') 'Only expected Graph GET and POST operations are allowed.'
    Assert-Test ($ContentType -eq 'application/zip' -and $InputFile -eq $fixtureZip) 'Post the reviewed ZIP without changing it.'
    $context.Posts++
    if ($context.FailPost) { throw 'Mock Graph upload failed.' }
    if ($context.Existing) {
        Assert-Test ($Uri -eq ($catalogBase + '/' + [uri]::EscapeDataString($catalogId) + '/appDefinitions')) 'Existing apps require POST appDefinitions.'
        return # Graph returns 204 No Content for an update.
    }
    Assert-Test ($Uri -eq $catalogBase) 'New apps require POST to the catalog collection.'
    return @{ id = $catalogId; externalId = $appId; distributionMethod = 'organization' }
}

try {
    New-Item -ItemType Directory -Path $fixtureTools, $fixturePackages | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Publish-TeamsPackage.ps1') -Destination $fixtureScript
    @'
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path $PSScriptRoot -Parent
$TenantId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
function Assert-DemoTenant { $global:PublishPackageTest.TenantChecks++ }
function Get-DemoState { return $global:PublishPackageTest.State.Clone() }
function Save-DemoState($State) { $global:PublishPackageTest.Saved = $State.Clone() }
'@ | Set-Content -LiteralPath $fixtureCommon -Encoding utf8NoBOM

    $cases = @(
        @{ Name = 'create'; Version = '1.0.0'; ExpectedOperation = 'Created' },
        @{ Name = 'update'; Version = '1.0.10'; Existing = $true; ExpectedOperation = 'Updated' },
        @{ Name = 'same version'; Version = '1.0.9'; Existing = $true; Error = 'must be newer than catalog version' },
        @{ Name = 'older version'; Version = '1.0.8'; Existing = $true; Error = 'must be newer than catalog version' },
        @{ Name = 'paginated higher version'; Version = '1.0.10'; Existing = $true; Paged = $true; Error = 'must be newer than catalog version 1.0.10' },
        @{ Name = 'wrong app ID'; Version = '1.0.10'; WrongId = $true; Error = 'does not match'; BeforeAuth = $true },
        @{ Name = 'invalid version'; Version = '1.0'; Error = 'invalid app version'; BeforeAuth = $true },
        @{ Name = 'upload failure'; Version = '1.0.10'; Existing = $true; FailPost = $true; Error = 'Mock Graph upload failed' }
    )
    $passed = 0
    foreach ($usePowerShell in @($true, $false)) {
        foreach ($case in $cases) {
            $existing = if ($case.Existing) { @{ id = $catalogId; externalId = $appId; distributionMethod = 'organization' } } else { $null }
            $global:PublishPackageTest = @{
                State = @{ teamsAppId = $appId }; Existing = $existing; Saved = $null
                Requests = [Collections.Generic.List[object]]::new(); Posts = 0; AuthCalls = 0; TenantChecks = 0
                CatalogVersion = '1.0.9'; Paged = $case.Paged; FailPost = $case.FailPost
            }
            $manifestId = if ($case.WrongId) { '99999999-2222-3333-4444-555555555555' } else { $appId }
            @{ id = $manifestId; version = $case.Version; name = @{ short = 'Mock AMA' } } |
                ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $fixtureManifest -Encoding utf8NoBOM
            Compress-Archive -LiteralPath $fixtureManifest -DestinationPath $fixtureZip -Force
            $beforeHash = (Get-FileHash -LiteralPath $fixtureZip -Algorithm SHA256).Hash
            $caught = $null
            $result = $null
            try { $result = & $fixtureScript -UseGraphPowerShell:$usePowerShell } catch { $caught = $_.Exception.Message }
            $context = $global:PublishPackageTest
            if ($case.Error) {
                Assert-Test ($caught -and $caught.Contains($case.Error)) "$($case.Name): expected error, got '$caught'."
                Assert-Test ($null -eq $context.Saved) "$($case.Name): failed upload must not save catalog state."
                $expectedPosts = if ($case.FailPost) { 1 } else { 0 }
                Assert-Test ($context.Posts -eq $expectedPosts) "$($case.Name): unexpected cloud mutation."
            } else {
                Assert-Test (-not $caught) "$($case.Name): unexpected error '$caught'."
                Assert-Test ($context.Posts -eq 1) "$($case.Name): exactly one upload is required."
                Assert-Test ($result.operation -eq $case.ExpectedOperation) "$($case.Name): report the actual operation."
                Assert-Test ($result.id -eq $catalogId -and $result.externalId -eq $appId) "$($case.Name): preserve both catalog and manifest IDs."
                Assert-Test ($context.Saved.teamsAppId -eq $appId -and $context.Saved.teamsCatalogId -eq $catalogId) "$($case.Name): save the existing catalog ID, not the empty 204 response."
            }
            if ($case.BeforeAuth) {
                Assert-Test ($context.AuthCalls -eq 0 -and $context.TenantChecks -eq 0 -and $context.Requests.Count -eq 0) "$($case.Name): reject before authentication."
            }
            Assert-Test ((Get-FileHash -LiteralPath $fixtureZip -Algorithm SHA256).Hash -eq $beforeHash) "$($case.Name): do not modify the ZIP."
            $passed++
        }
    }
    Write-Output "PASS: $passed isolated publisher scenarios across Graph PowerShell and Azure CLI transports. No network calls or project-state writes."
} finally {
    Remove-Variable -Name PublishPackageTest -Scope Global -ErrorAction SilentlyContinue
    # Delete only the known files and empty folders created by this test; no recursive deletion.
    foreach ($file in @($fixtureScript, $fixtureCommon, $fixtureManifest, $fixtureZip)) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    foreach ($folder in @($fixturePackages, $fixtureArtifacts, $fixtureTools, $fixtureRoot)) {
        if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder }
    }
}
