<#
.SYNOPSIS
  Draws FreeSystemTray's icon (src/FreeSystemTray/FreeSystemTray.ico): a small dark tray with three
  coloured icons (and a grip from 24 px up), drawn separately at each size so the small ones stay crisp.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Make-AppIcon.ps1
#>
param([string]$Out = (Join-Path $PSScriptRoot "..\src\FreeSystemTray\FreeSystemTray.ico"))

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# Whole-pixel layout at every size, so small sizes stay crisp: a dark bar across the middle with a
# light border, a grip from 24 px up, and three equal coloured squares.
function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $border = [Math]::Max(1, [Math]::Floor($size / 16))
    $gap = [Math]::Max(1, [Math]::Floor($size / 16))
    $square = [Math]::Max(3, [int][Math]::Round($size * 3 / 16, [MidpointRounding]::AwayFromZero))
    $top = [Math]::Floor($size / 4); $height = $size - 2 * $top
    $grip = if ($size -ge 24) { [Math]::Max(2, [Math]::Floor($size / 12)) } else { 0 }

    $borderBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x9A, 0x9A, 0x9A))
    $fillBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x2B, 0x2B, 0x2B))
    if ($size -ge 32) {
        # Rounded corners, anti-aliased, on whole pixels.
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $r = [Math]::Floor($size / 10)
        function Rounded([double]$x, [double]$y, [double]$w, [double]$h, [double]$rad) {
            $p = New-Object System.Drawing.Drawing2D.GraphicsPath
            $d = 2 * $rad
            $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
            $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
            $p.CloseFigure()
            return $p
        }
        $g.FillPath($borderBrush, (Rounded 0 $top $size $height $r))
        $g.FillPath($fillBrush, (Rounded $border ($top + $border) ($size - 2 * $border) ($height - 2 * $border) ([Math]::Max(1, $r - $border))))
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
    }
    else {
        # Square corners with the corner pixels left out.
        $g.FillRectangle($borderBrush, 0, $top, $size, $height)
        $g.FillRectangle($fillBrush, $border, $top + $border, $size - 2 * $border, $height - 2 * $border)
        foreach ($c in @(@(0, $top), @(($size - 1), $top), @(0, ($top + $height - 1)), @(($size - 1), ($top + $height - 1)))) {
            $bmp.SetPixel($c[0], $c[1], [System.Drawing.Color]::Transparent)
        }
    }

    $content = 3 * $square + 2 * $gap + $(if ($grip) { $grip + 2 * $gap } else { 0 })
    $x = [Math]::Floor(($size - $content) / 2)
    if ($grip) {
        $gripHeight = $height - 2 * $border - 2 * $gap
        $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x6A, 0x6A, 0x6A))), $x, $top + $border + $gap, $grip, $gripHeight)
        $x += $grip + 2 * $gap
    }
    $y = $top + [Math]::Floor(($height - $square) / 2)
    $colors = @([System.Drawing.Color]::FromArgb(255, 0x3D, 0xA5, 0xF4), [System.Drawing.Color]::FromArgb(255, 0x4C, 0xC3, 0x8A), [System.Drawing.Color]::FromArgb(255, 0xF5, 0xA5, 0x24))
    foreach ($color in $colors) {
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $color), $x, $y, $square, $square)
        $x += $square + $gap
    }
    $g.Dispose()
    return $bmp
}

# One ICO image: a 32-bit DIB (BITMAPINFOHEADER, bottom-up BGRA rows, an empty AND mask), or PNG at 256.
function ImageBytes([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $ms.ToArray()
    }
    $w = New-Object System.IO.BinaryWriter $ms
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($size * $size * 4 + $maskStride * $size)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskStride * $size)))
    $w.Flush()
    return , $ms.ToArray() # the comma keeps PowerShell from sending the bytes one by one
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = @()
foreach ($s in $sizes) { $images += , [byte[]](ImageBytes (Draw $s)) }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Out), $ico.ToArray())
Write-Host "Wrote $([System.IO.Path]::GetFullPath($Out)) ($($sizes -join ', ') px)"
