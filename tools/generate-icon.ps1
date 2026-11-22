$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot '..\FrameShift\Assets'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = @()

function New-RingPng([int]$size) {
    $scale = 4
    $canvas = New-Object System.Drawing.Bitmap ($size * $scale), ($size * $scale)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#2EEA84')), ([single]($size * $scale * 64 / 512))
    $offset = [single]($size * $scale * 80 / 512)
    $diameter = [single]($size * $scale * 352 / 512)
    $graphics.DrawEllipse($pen, $offset, $offset, $diameter, $diameter)
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $resizer = [System.Drawing.Graphics]::FromImage($bitmap)
    $resizer.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $resizer.DrawImage($canvas, 0, 0, $size, $size)
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    $resizer.Dispose()
    $bitmap.Dispose()
    $pen.Dispose()
    $graphics.Dispose()
    $canvas.Dispose()
    return ,$bytes
}

foreach ($size in $sizes) { $images += ,(New-RingPng $size) }
[System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'app.png'), (New-RingPng 512))
[System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'app-preview.png'), (New-RingPng 128))
$file = [System.IO.File]::Create((Join-Path $assetDirectory 'app.ico'))
$writer = New-Object System.IO.BinaryWriter $file
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Length)
    $offset = 6 + 16 * $sizes.Length
    for ($i = 0; $i -lt $sizes.Length; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
}
finally { $writer.Dispose(); $file.Dispose() }
Write-Output 'Generated transparent green ring PNGs and a nine-size Windows icon.'
