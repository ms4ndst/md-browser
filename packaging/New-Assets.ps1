<#
.SYNOPSIS
    Generate placeholder MSIX tile assets and a Windows .ico for MdBrowser.

.DESCRIPTION
    Produces a Catppuccin-Mocha-themed set of PNG tiles required by Package.appxmanifest,
    plus a multi-format .ico used as the WPF window/taskbar icon.

    Visual: Mocha Base background (#1E1E2E) with a centered Mauve diamond glyph (#CBA6F7)
    and a thin Mauve border. Simple, readable, and recognizable in every tile size.

    Outputs:
      packaging/Assets/Square44x44Logo.png      (44 x 44)
      packaging/Assets/Square150x150Logo.png    (150 x 150)
      packaging/Assets/Wide310x150Logo.png      (310 x 150)
      packaging/Assets/LargeTile.png            (310 x 310)
      packaging/Assets/SmallTile.png            (71 x 71)
      packaging/Assets/StoreLogo.png            (50 x 50)
      packaging/Assets/SplashScreen.png         (620 x 300)
      src/MdBrowser/Assets/MdBrowser.ico        (multi-size: 16, 32, 48, 64, 128, 256)

.EXAMPLE
    .\packaging\New-Assets.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$msixAssets = Join-Path $PSScriptRoot 'Assets'
$appAssets  = Join-Path $repoRoot 'src\MdBrowser\Assets'
New-Item -ItemType Directory -Force -Path $msixAssets | Out-Null
New-Item -ItemType Directory -Force -Path $appAssets  | Out-Null

# Catppuccin Mocha palette pieces we'll use
$baseHex    = '#1E1E2E'
$mantleHex  = '#181825'
$mauveHex   = '#CBA6F7'
$lavHex     = '#B4BEFE'

function HexToColor([string]$hex) {
    return [System.Drawing.ColorTranslator]::FromHtml($hex)
}

function New-TileBitmap {
    param(
        [int]$Width,
        [int]$Height,
        [bool]$DrawText
    )
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode    = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode= [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint= [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Background gradient: Mantle -> Base
    $rect  = [System.Drawing.Rectangle]::new(0, 0, $Width, $Height)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect, (HexToColor $mantleHex), (HexToColor $baseHex), 90)
    $g.FillRectangle($brush, $rect)
    $brush.Dispose()

    # Centered diamond glyph (Mauve)
    $cx = [single]($Width  / 2.0)
    $cy = [single]($Height / 2.0)
    $r  = [single]([Math]::Min($Width, $Height) * 0.34)
    $diamond = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx,        $cy - $r),
        [System.Drawing.PointF]::new($cx + $r,   $cy),
        [System.Drawing.PointF]::new($cx,        $cy + $r),
        [System.Drawing.PointF]::new($cx - $r,   $cy)
    )
    $fill = [System.Drawing.SolidBrush]::new((HexToColor $mauveHex))
    $g.FillPolygon($fill, $diamond)
    $fill.Dispose()

    # Inner accent line (Lavender) for a subtle highlight
    $penWidth = [single][Math]::Max(1, [int]($Width / 80))
    $pen = [System.Drawing.Pen]::new((HexToColor $lavHex), $penWidth)
    $g.DrawPolygon($pen, $diamond)
    $pen.Dispose()

    # Optional "MD" wordmark on wide/large tiles
    if ($DrawText -and $Width -ge 220) {
        $fontSize  = [single][Math]::Max(14, [int]($Height * 0.18))
        $font      = [System.Drawing.Font]::new('Segoe UI Variable', $fontSize, [System.Drawing.FontStyle]::Bold)
        $textBrush = [System.Drawing.SolidBrush]::new((HexToColor '#CDD6F4'))
        $text      = 'MD'
        $size      = $g.MeasureString($text, $font)
        $x = ($Width  - $size.Width)  / 2
        $y = $cy + $r + ($Height * 0.04)
        if ($y + $size.Height -lt $Height - 4) {
            $g.DrawString($text, $font, $textBrush, $x, $y)
        }
        $textBrush.Dispose()
        $font.Dispose()
    }

    $g.Dispose()
    return $bmp
}

function Save-Tile {
    param([int]$Width, [int]$Height, [string]$OutPath, [bool]$DrawText = $false)
    $bmp = New-TileBitmap -Width $Width -Height $Height -DrawText $DrawText
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  wrote $OutPath ($Width x $Height)"
}

Write-Host "Generating MSIX tile PNGs..." -ForegroundColor Cyan
Save-Tile  44  44 (Join-Path $msixAssets 'Square44x44Logo.png')
Save-Tile  50  50 (Join-Path $msixAssets 'StoreLogo.png')
Save-Tile  71  71 (Join-Path $msixAssets 'SmallTile.png')
Save-Tile 150 150 (Join-Path $msixAssets 'Square150x150Logo.png')
Save-Tile 310 150 (Join-Path $msixAssets 'Wide310x150Logo.png')       -DrawText $true
Save-Tile 310 310 (Join-Path $msixAssets 'LargeTile.png')             -DrawText $true
Save-Tile 620 300 (Join-Path $msixAssets 'SplashScreen.png')          -DrawText $true

Write-Host ""
Write-Host "Building multi-size .ico for the WPF executable..." -ForegroundColor Cyan

# Build an ICO with multiple PNG-compressed entries (Vista+ supports embedded PNG).
$iconSizes = 16, 32, 48, 64, 128, 256
$entries = foreach ($s in $iconSizes) {
    $bmp = New-TileBitmap -Width $s -Height $s -DrawText $false
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
}

$icoMs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $icoMs

# ICONDIR (6 bytes)
$bw.Write([uint16]0)            # reserved
$bw.Write([uint16]1)            # type = 1 (icon)
$bw.Write([uint16]$entries.Count)

$headerSize = 6 + (16 * $entries.Count)
$cursor = $headerSize

# ICONDIRENTRY[] (16 bytes each)
foreach ($e in $entries) {
    $w = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $h = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$w)
    $bw.Write([byte]$h)
    $bw.Write([byte]0)          # colors in palette (0 = true color)
    $bw.Write([byte]0)          # reserved
    $bw.Write([uint16]1)        # color planes
    $bw.Write([uint16]32)       # bits per pixel
    $bw.Write([uint32]$e.Bytes.Length)
    $bw.Write([uint32]$cursor)
    $cursor += $e.Bytes.Length
}
# Concatenated PNG payloads
foreach ($e in $entries) {
    $bw.Write($e.Bytes)
}
$bw.Flush()

$icoPath = Join-Path $appAssets 'MdBrowser.ico'
[System.IO.File]::WriteAllBytes($icoPath, $icoMs.ToArray())
$bw.Dispose()
Write-Host "  wrote $icoPath (sizes: $($iconSizes -join ', '))"

Write-Host ""
Write-Host "Done." -ForegroundColor Green
