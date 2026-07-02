<#
.SYNOPSIS
    Generate placeholder MSIX tile assets and a Windows .ico for md-editor.

.DESCRIPTION
    Produces a Catppuccin-Mocha-themed set of PNG tiles required by Package.appxmanifest,
    plus a multi-format .ico used as the WPF window/taskbar icon.

    Visual: Mocha Base background (#1E1E2E) with a centered Markdown mark - a Mauve
    rounded-rectangle badge (#CBA6F7) carrying a dark "M" and down arrow, echoing the
    standard Markdown logo. Readable and recognizable in every tile size.

    Outputs:
      packaging/Assets/Square44x44Logo.png      (44 x 44)
      packaging/Assets/Square150x150Logo.png    (150 x 150)
      packaging/Assets/Wide310x150Logo.png      (310 x 150)
      packaging/Assets/LargeTile.png            (310 x 310)
      packaging/Assets/SmallTile.png            (71 x 71)
      packaging/Assets/StoreLogo.png            (50 x 50)
      packaging/Assets/SplashScreen.png         (620 x 300)
      src/md-editor/Assets/md-editor.ico        (multi-size: 16, 32, 48, 64, 128, 256)

.EXAMPLE
    .\packaging\New-Assets.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$msixAssets = Join-Path $PSScriptRoot 'Assets'
$appAssets  = Join-Path $repoRoot 'src\md-editor\Assets'
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

    # ---- Markdown mark: rounded-rect badge with an "M" and a down arrow ----
    # Dark glyph on the light Mauve badge for maximum contrast at small sizes.
    $glyphColor = HexToColor $baseHex

    # Badge geometry (Markdown-logo aspect ~ 1.6 : 1), centered.
    $badgeW = [single]([Math]::Min($Width, $Height) * 0.82)
    $badgeH = [single]($badgeW / 1.6)
    $badgeX = [single](($Width  - $badgeW) / 2.0)
    $badgeY = [single](($Height - $badgeH) / 2.0)
    $radius = [single]([Math]::Max(2, $badgeH * 0.18))
    $d      = [single]($radius * 2)

    # Rounded-rectangle badge path
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($badgeX,                     $badgeY,                     $d, $d, 180, 90)
    $path.AddArc($badgeX + $badgeW - $d,      $badgeY,                     $d, $d, 270, 90)
    $path.AddArc($badgeX + $badgeW - $d,      $badgeY + $badgeH - $d,      $d, $d,   0, 90)
    $path.AddArc($badgeX,                     $badgeY + $badgeH - $d,      $d, $d,  90, 90)
    $path.CloseFigure()

    $badgeFill = [System.Drawing.SolidBrush]::new((HexToColor $mauveHex))
    $g.FillPath($badgeFill, $path)
    $badgeFill.Dispose()
    $borderW = [single][Math]::Max(1, $badgeH * 0.06)
    $borderPen = [System.Drawing.Pen]::new((HexToColor $lavHex), $borderW)
    $g.DrawPath($borderPen, $path)
    $borderPen.Dispose()
    $path.Dispose()

    # Content box inside the badge
    $insetX = [single]($badgeW * 0.16)
    $insetY = [single]($badgeH * 0.24)
    $cx0 = [single]($badgeX + $insetX)
    $cx1 = [single]($badgeX + $badgeW - $insetX)
    $cy0 = [single]($badgeY + $insetY)
    $cy1 = [single]($badgeY + $badgeH - $insetY)
    $contentW = [single]($cx1 - $cx0)

    $stroke = [single][Math]::Max([single]1.2, $badgeH * 0.16)
    $pen = [System.Drawing.Pen]::new($glyphColor, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # "M" occupies the left ~52% of the content box
    $mx0  = [single]$cx0
    $mx1  = [single]($cx0 + $contentW * 0.52)
    $mMid = [single](($mx0 + $mx1) / 2.0)
    $mDip = [single]($cy0 + ($cy1 - $cy0) * 0.60)
    $mPts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($mx0,  $cy1),
        [System.Drawing.PointF]::new($mx0,  $cy0),
        [System.Drawing.PointF]::new($mMid, $mDip),
        [System.Drawing.PointF]::new($mx1,  $cy0),
        [System.Drawing.PointF]::new($mx1,  $cy1)
    )
    $g.DrawLines($pen, $mPts)

    # Down arrow on the right of the content box
    $ax    = [single]($cx0 + $contentW * 0.82)
    $headH = [single](($cy1 - $cy0) * 0.42)
    $headW = [single]($contentW * 0.16)
    $g.DrawLine($pen, $ax, $cy0, $ax, [single]($cy1 - $headH * 0.30))
    $pen.Dispose()
    $head = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([single]($ax - $headW), [single]($cy1 - $headH)),
        [System.Drawing.PointF]::new([single]($ax + $headW), [single]($cy1 - $headH)),
        [System.Drawing.PointF]::new($ax,                    $cy1)
    )
    $headBrush = [System.Drawing.SolidBrush]::new($glyphColor)
    $g.FillPolygon($headBrush, $head)
    $headBrush.Dispose()

    # Optional "Markdown" wordmark on wide/large tiles
    if ($DrawText -and $Width -ge 300) {
        $fontSize  = [single][Math]::Max(14, [int]($Height * 0.12))
        $font      = [System.Drawing.Font]::new('Segoe UI Variable', $fontSize, [System.Drawing.FontStyle]::Bold)
        $textBrush = [System.Drawing.SolidBrush]::new((HexToColor '#CDD6F4'))
        $text      = 'Markdown'
        $size      = $g.MeasureString($text, $font)
        $x = ($Width - $size.Width) / 2
        $y = $badgeY + $badgeH + ($Height * 0.04)
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

$icoPath = Join-Path $appAssets 'md-editor.ico'
[System.IO.File]::WriteAllBytes($icoPath, $icoMs.ToArray())
$bw.Dispose()
Write-Host "  wrote $icoPath (sizes: $($iconSizes -join ', '))"

Write-Host ""
Write-Host "Done." -ForegroundColor Green
