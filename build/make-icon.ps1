# Draws the viddl icon and writes src/viddl.App/Assets/viddl.ico (16-256 px) plus a 256 px PNG.
# Usage: powershell -ExecutionPolicy Bypass -File build/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/viddl.App/Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Draw-Icon([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $s = $size / 256.0

    # Rounded square with a blue-to-violet gradient
    $r = 56 * $s; $m = 8 * $s; $w = $size - 2 * $m
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $r, $r, 180, 90); $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90); $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new(0, 0)), ([Drawing.PointF]::new($size, $size)), ([Drawing.Color]::FromArgb(255, 37, 99, 235)), ([Drawing.Color]::FromArgb(255, 124, 58, 237))
    $g.FillPath($brush, $path)

    # Play triangle
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
    $tri = [Drawing.PointF[]]@(
        [Drawing.PointF]::new(92 * $s, 58 * $s), [Drawing.PointF]::new(92 * $s, 150 * $s), [Drawing.PointF]::new(172 * $s, 104 * $s))
    $g.FillPolygon($white, $tri)

    # Download bar and arrow underneath
    $g.FillRectangle($white, 64 * $s, 196 * $s, 128 * $s, 16 * $s)
    $arrow = [Drawing.PointF[]]@(
        [Drawing.PointF]::new(108 * $s, 160 * $s), [Drawing.PointF]::new(148 * $s, 160 * $s), [Drawing.PointF]::new(148 * $s, 170 * $s),
        [Drawing.PointF]::new(166 * $s, 170 * $s), [Drawing.PointF]::new(128 * $s, 190 * $s), [Drawing.PointF]::new(90 * $s, 170 * $s),
        [Drawing.PointF]::new(108 * $s, 170 * $s))
    $g.FillPolygon($white, $arrow)
    $g.Dispose()
    $bmp
}

# ICO file with PNG-compressed images
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bmp = Draw-Icon $size
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $assets 'viddl.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object IO.MemoryStream
$wr = New-Object IO.BinaryWriter $out
$wr.Write([uint16]0); $wr.Write([uint16]1); $wr.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $wr.Write([byte]$dim); $wr.Write([byte]$dim); $wr.Write([byte]0); $wr.Write([byte]0)
    $wr.Write([uint16]1); $wr.Write([uint16]32); $wr.Write([uint32]$images[$i].Length); $wr.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $wr.Write($img) }
[IO.File]::WriteAllBytes((Join-Path $assets 'viddl.ico'), $out.ToArray())
Write-Host "Wrote $(Join-Path $assets 'viddl.ico')"
