#Requires -Version 7.0
# Publishes only this demo to the demo tenant's organization catalog. Does not assign users.
# Updates use the catalog ID, not the manifest ID, and can return 204 with no body:
# https://learn.microsoft.com/graph/api/teamsapp-update?view=graph-rest-1.0
[CmdletBinding()]
param([switch]$UseGraphPowerShell)

. "$PSScriptRoot/Tenant.Common.ps1"

function ConvertTo-PackageVersion([string]$Value, [string]$Source) {
    $parsed = $null
    if ($Value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        -not [version]::TryParse($Value, [ref]$parsed)) {
        throw "$Source has an invalid app version '$Value'; expected major.minor.patch."
    }
    return $parsed
}

# Validate the package before opening an authentication window or using a token.
$state = Get-DemoState
$expectedId = [guid]::Empty
if (-not [guid]::TryParse([string]$state.teamsAppId, [ref]$expectedId) -or $expectedId -eq [guid]::Empty) {
    throw 'Missing or invalid teamsAppId in .local/deployment.json.'
}
$packagePath = Join-Path $ProjectRoot 'artifacts/packages/LGES-AMA-Teams-Copilot.zip'
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw 'App package is missing. Run tools/New-TeamsPackage.ps1 first.'
}
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entries = @($archive.Entries | Where-Object FullName -CEQ 'manifest.json')
    if ($entries.Count -ne 1) { throw 'The ZIP must contain exactly one manifest.json at its root.' }
    if ($entries[0].Length -gt 1MB) { throw 'The packaged manifest exceeds the expected size limit.' }
    $reader = [System.IO.StreamReader]::new($entries[0].Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
} finally { $archive.Dispose() }
$packageId = [guid]::Empty
if (-not [guid]::TryParse([string]$manifest.id, [ref]$packageId) -or $packageId -ne $expectedId) {
    throw "Package app ID '$($manifest.id)' does not match this project's teamsAppId '$expectedId'. Rebuild the package without changing its ID."
}
$packageVersion = ConvertTo-PackageVersion ([string]$manifest.version) 'Package'

Assert-DemoTenant
$token = $null
$headers = $null
try {
    if ($UseGraphPowerShell) {
        Connect-MgGraph -TenantId $TenantId -Scopes 'AppCatalog.ReadWrite.All' -ContextScope Process -NoWelcome
    } else {
        $token = az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) { throw 'Graph login is required.' }
        $headers = @{ Authorization = "Bearer $token" }
    }

    function Invoke-CatalogRequest([string]$Method, [string]$Uri, [switch]$Upload) {
        if ($UseGraphPowerShell) {
            $request = @{ Method = $Method; Uri = $Uri }
            if ($Upload) { $request.ContentType = 'application/zip'; $request.InputFilePath = $packagePath }
            return Invoke-MgGraphRequest @request
        }
        $request = @{ Method = $Method; Uri = $Uri; Headers = $headers }
        if ($Upload) { $request.ContentType = 'application/zip'; $request.InFile = $packagePath }
        return Invoke-RestMethod @request
    }

    function Get-CatalogItems([string]$Uri) {
        do {
            # Keep bearer tokens on Graph even when following pagination links.
            $address = [uri]$Uri
            if ($address.Scheme -ne 'https' -or $address.Host -ne 'graph.microsoft.com' -or
                -not $address.IsDefaultPort -or $address.UserInfo -or
                -not $address.AbsolutePath.StartsWith('/v1.0/appCatalogs/teamsApps', [StringComparison]::Ordinal)) {
                throw 'Unexpected app catalog pagination URL.'
            }
            $page = Invoke-CatalogRequest GET $Uri
            foreach ($item in $page.value) { if ($null -ne $item) { $item } }
            $Uri = [string]$page.'@odata.nextLink'
        } while ($Uri)
    }

    $catalogBase = 'https://graph.microsoft.com/v1.0/appCatalogs/teamsApps'
    $filter = [uri]::EscapeDataString("externalId eq '$expectedId'")
    $existing = @(Get-CatalogItems ($catalogBase + '?$filter=' + $filter))
    if ($existing.Count -gt 1) { throw 'Multiple catalog apps match this manifest ID; resolve the duplicate before publishing.' }

    if ($existing.Count -eq 1) {
        $published = $existing[0]
        if ($published.externalId -ne $expectedId.ToString() -or -not $published.id) {
            throw 'The catalog lookup did not return the expected app identity.'
        }
        if ($published.distributionMethod -ne 'organization') {
            throw 'Only an app in the organization catalog can be updated by this script.'
        }
        $definitionsUri = $catalogBase + '/' + [uri]::EscapeDataString([string]$published.id) + '/appDefinitions'
        $definitions = @(Get-CatalogItems ($definitionsUri + '?$select=id,version,publishingState'))
        if ($definitions.Count -eq 0) { throw 'The existing app has no readable app definitions; its version cannot be checked safely.' }
        $highestVersion = $null
        foreach ($definition in $definitions) {
            $version = ConvertTo-PackageVersion ([string]$definition.version) 'Catalog app definition'
            if ($null -eq $highestVersion -or $version -gt $highestVersion) { $highestVersion = $version }
        }
        if ($packageVersion -le $highestVersion) {
            throw "Package version $packageVersion must be newer than catalog version $highestVersion. Run .\Update-TeamsPackage.ps1 in the project root, then retry. The Teams app ID must stay unchanged."
        }
        # Successful updates can have no response body. Retain the existing catalog ID.
        $null = Invoke-CatalogRequest POST $definitionsUri -Upload
        $operation = 'Updated'
    } else {
        $published = Invoke-CatalogRequest POST $catalogBase -Upload
        if (-not $published.id -or $published.externalId -ne $expectedId.ToString()) {
            throw 'The publish response did not confirm the expected app identity. Inspect the catalog before retrying.'
        }
        $operation = 'Created'
    }

    $state.teamsCatalogId = [string]$published.id
    Save-DemoState $state
    [pscustomobject]@{
        operation = $operation
        id = [string]$published.id
        externalId = $expectedId.ToString()
        version = $packageVersion.ToString()
        displayName = [string]$manifest.name.short
        distributionMethod = [string]$published.distributionMethod
    }
} finally { $headers = $null; $token = $null }
