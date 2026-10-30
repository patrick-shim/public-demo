[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$project=Join-Path $root 'server/mcp/Lges.Ama.Mcp/Lges.Ama.Mcp.csproj'
dotnet build $project -c Release --nologo
if($LASTEXITCODE -ne 0){ throw 'MCP build failed.' }
$output=Join-Path $root '.local/run'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$dll=Join-Path $root 'server/mcp/Lges.Ama.Mcp/bin/Release/net10.0/Lges.Ama.Mcp.dll'
$env:ASPNETCORE_ENVIRONMENT='Development'
$running=@()
$profiles=@('news','ir','market')
for($i=0;$i -lt $profiles.Length;$i++){
    $port=5211+$i
    if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){ throw "Port $port is already in use; stop its owning process before starting." }
    $profile=$profiles[$i]
    $process=Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @($dll,'--urls',"http://127.0.0.1:$port",'--McpProfile',$profile) -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput "$output/$profile.out.log" -RedirectStandardError "$output/$profile.err.log"
    $running += [pscustomobject]@{profile=$profile;port=$port;processId=$process.Id}
}
$running | ConvertTo-Json | Set-Content -LiteralPath "$output/processes.json"
$running | Format-Table
