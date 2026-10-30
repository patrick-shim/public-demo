$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$record=Join-Path $root '.local/run/processes.json'
if(-not (Test-Path $record)){return}
foreach($entry in @(Get-Content $record -Raw|ConvertFrom-Json)){
  $process=Get-CimInstance Win32_Process -Filter "ProcessId=$($entry.processId)"
  $expected=Join-Path $root 'server/mcp/Lges.Ama.Mcp/bin/Release/net10.0/Lges.Ama.Mcp.dll'
  if($process -and $process.CommandLine.Contains($expected)){
    Stop-Process -Id $entry.processId
    Write-Output "Stopped local $($entry.profile) MCP."
  }elseif($process){throw "Process $($entry.processId) no longer belongs to this demo; no action taken."}
}
Remove-Item -LiteralPath $record
