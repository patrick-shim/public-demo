# PNG decoding uses the Windows image library; package creation needs no Node install.
Add-Type -AssemblyName System.Drawing

function Assert-TeamsPackageIcon {
    param(
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][ValidateSet('color.png', 'outline.png')][string]$Name
    )
    if ($Bytes.Length -lt 8 -or [Convert]::ToHexString($Bytes[0..7]) -ne '89504E470D0A1A0A') {
        throw "$Name must be a PNG image."
    }
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    try {
        $bitmap = [System.Drawing.Bitmap]::new($stream)
        try {
            $size = if ($Name -eq 'outline.png') { 32 } else { 192 }
            if ($bitmap.Width -ne $size -or $bitmap.Height -ne $size) {
                throw "$Name must be ${size}x${size} pixels."
            }
            if ($Name -eq 'outline.png') {
                $visible = 0
                $transparent = 0
                for ($y = 0; $y -lt $size; $y++) {
                    for ($x = 0; $x -lt $size; $x++) {
                        $pixel = $bitmap.GetPixel($x, $y)
                        if ($pixel.A -eq 0) { $transparent++; continue }
                        $visible++
                        if ($pixel.R -ne 255 -or $pixel.G -ne 255 -or $pixel.B -ne 255) {
                            throw "outline.png contains a non-white visible pixel at ($x,$y). Use only white artwork on a transparent background."
                        }
                    }
                }
                if ($visible -eq 0 -or $transparent -eq 0) {
                    throw 'outline.png must contain visible white artwork and a transparent background.'
                }
            }
        } finally { $bitmap.Dispose() }
    } finally { $stream.Dispose() }
}
