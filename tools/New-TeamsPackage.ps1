#Requires -Version 7.0
[CmdletBinding()]
param([switch]$IncrementVersion)

. "$PSScriptRoot/Tenant.Common.ps1"
. "$PSScriptRoot/TeamsPackage.Validation.ps1"
$templatePath = Join-Path $ProjectRoot 'channel/appPackage/manifest.template.json'
$template = Get-Content -LiteralPath $templatePath -Raw
$current = $template | ConvertFrom-Json
if ($current.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'The app version must have the form major.minor.patch, for example 1.0.0.'
}
$version = [version]$current.version
if ($IncrementVersion) {
    if ($version.Build -eq [int]::MaxValue) { throw 'The patch version cannot be incremented further.' }
    $nextVersion = '{0}.{1}.{2}' -f $version.Major, $version.Minor, ($version.Build + 1)
    # Preserve formatting and manifestVersion; only the app version changes.
    $pattern = '(?m)(^\s*"version"\s*:\s*")[^"]+("\s*,)'
    if ([regex]::Matches($template, $pattern).Count -ne 1) { throw 'Expected one top-level app version in the template.' }
    $template = [regex]::Replace($template, $pattern, ('${1}' + $nextVersion + '${2}'))
} else {
    $nextVersion = $current.version
}

$state = Get-DemoState
$outputs = Get-Content -LiteralPath (Join-Path $PrivateRoot 'app-outputs.json') -Raw | ConvertFrom-Json
foreach ($key in @('teamsAppId', 'channelAppId')) {
    $parsedId = [guid]::Empty
    if (-not [guid]::TryParse([string]$state[$key], [ref]$parsedId) -or $parsedId -eq [guid]::Empty) {
        throw "Missing or invalid $key in .local/deployment.json."
    }
}
$hostDomain = [string]$outputs.hostFqdn.value
if ([uri]::CheckHostName($hostDomain) -ne [System.UriHostNameType]::Dns) {
    throw 'Missing or invalid hostFqdn in .local/app-outputs.json.'
}
$manifest = $template.Replace('{{TEAMS_APP_ID}}', $state.teamsAppId).Replace('{{BOT_APP_ID}}', $state.channelAppId).Replace('{{HOST_DOMAIN}}', $hostDomain)
if ($manifest.Contains('{{')) { throw 'Unresolved package placeholders.' }
$null = $manifest | ConvertFrom-Json
foreach ($icon in @('color.png', 'outline.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot "channel/appPackage/$icon") -PathType Leaf)) {
        throw "Missing app icon: $icon"
    }
}

# Build in an isolated folder so a failed build does not replace the existing upload ZIP.
$stage = Join-Path $PrivateRoot ('package-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $manifest | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
    foreach ($icon in @('color.png', 'outline.png')) {
        Copy-Item -LiteralPath (Join-Path $ProjectRoot "channel/appPackage/$icon") -Destination $stage
    }
    $candidate = Join-Path $stage 'candidate.zip'
    $files = @('manifest.json', 'color.png', 'outline.png') | ForEach-Object { Join-Path $stage $_ }
    Compress-Archive -LiteralPath $files -DestinationPath $candidate
    $archive = [System.IO.Compression.ZipFile]::OpenRead($candidate)
    try {
        if ((($archive.Entries.FullName | Sort-Object) -join ',') -ne 'color.png,manifest.json,outline.png') {
            throw 'The upload ZIP must contain only manifest.json, color.png and outline.png at its root.'
        }
        $reader = [System.IO.StreamReader]::new($archive.GetEntry('manifest.json').Open())
        try { $packed = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($packed.version -ne $nextVersion -or $packed.id -ne $state.teamsAppId -or $packed.bots[0].botId -ne $state.channelAppId) {
            throw 'The packaged version or app IDs do not match the expected values.'
        }
        foreach ($icon in @('color.png', 'outline.png')) {
            $iconStream = $archive.GetEntry($icon).Open()
            $buffer = [IO.MemoryStream]::new()
            try {
                $iconStream.CopyTo($buffer)
                Assert-TeamsPackageIcon -Bytes $buffer.ToArray() -Name $icon
            } finally { $iconStream.Dispose(); $buffer.Dispose() }
        }
    } finally { $archive.Dispose() }

    $destination = Join-Path $ProjectRoot 'artifacts/packages'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $versionedZip = Join-Path $destination "LGES-AMA-Teams-Copilot-$nextVersion.zip"
    $latestZip = Join-Path $destination 'LGES-AMA-Teams-Copilot.zip'
    Copy-Item -LiteralPath $candidate -Destination $versionedZip -Force
    Copy-Item -LiteralPath $candidate -Destination $latestZip -Force
    $savedStage = Join-Path $PrivateRoot 'package'
    New-Item -ItemType Directory -Force -Path $savedStage | Out-Null
    foreach ($file in $files) { Copy-Item -LiteralPath $file -Destination $savedStage -Force }
    if ($IncrementVersion) {
        [System.IO.File]::WriteAllText($templatePath, $template, [System.Text.UTF8Encoding]::new($false))
        Write-Output "App version: $($current.version) -> $nextVersion"
    }
    Write-Output "Upload package: $versionedZip"
    Write-Output "Latest package: $latestZip"
} finally {
    # Remove only this invocation's known temporary files, without recursive deletion.
    foreach ($name in @('manifest.json', 'color.png', 'outline.png', 'candidate.zip')) {
        $temporaryFile = Join-Path $stage $name
        if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile }
    }
    Remove-Item -LiteralPath $stage
}
