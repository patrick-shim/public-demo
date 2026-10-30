#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../tools/TeamsPackage.Validation.ps1')

function New-TestPng {
    param([int]$Size = 32, [switch]$RedPixel, [switch]$Opaque, [switch]$Empty)
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    try {
        if ($Opaque) {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try { $graphics.Clear([System.Drawing.Color]::White) } finally { $graphics.Dispose() }
        }
        if (-not $Empty) {
            $bitmap.SetPixel(1, 1, [System.Drawing.Color]::White)
            # Partially transparent white edges are valid, but colored edges are not.
            $pixel = if ($RedPixel) { [System.Drawing.Color]::FromArgb(80, 165, 0, 52) } else { [System.Drawing.Color]::FromArgb(80, 255, 255, 255) }
            $bitmap.SetPixel(2, 1, $pixel)
        }
        $stream = [IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        } finally { $stream.Dispose() }
    } finally { $bitmap.Dispose() }
}

Assert-TeamsPackageIcon -Bytes (New-TestPng) -Name 'outline.png'
Assert-TeamsPackageIcon -Bytes (New-TestPng -Size 192 -Opaque) -Name 'color.png'
foreach ($case in @(
    @{ Name = 'colored antialiased pixel'; Bytes = (New-TestPng -RedPixel) },
    @{ Name = 'opaque outline background'; Bytes = (New-TestPng -Opaque) },
    @{ Name = 'empty outline'; Bytes = (New-TestPng -Empty) },
    @{ Name = 'incorrect outline dimensions'; Bytes = (New-TestPng -Size 31) },
    @{ Name = 'non-PNG data'; Bytes = [byte[]](1, 2, 3) }
)) {
    $rejected = $false
    try { Assert-TeamsPackageIcon -Bytes $case.Bytes -Name 'outline.png' } catch { $rejected = $true }
    if (-not $rejected) { throw "Expected rejection: $($case.Name)." }
}
Write-Output 'PASS: valid icons accepted; colored pixels, opaque/empty outlines, bad dimensions and non-PNG data rejected.'
