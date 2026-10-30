<#
.SYNOPSIS
    Draws the logo at every size and the Store's listing art into branding/. Same mark as the app icon:
    a coral rounded square with a white folded page. Flat colour only.
    Run:  powershell -ExecutionPolicy Bypass -File tools/Make-Branding.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path $PSScriptRoot -Parent
$branding = Join-Path $repo 'branding'
foreach ($d in 'logo', 'store') { New-Item -ItemType Directory -Force (Join-Path $branding $d) | Out-Null }

$coral = [System.Drawing.Color]::FromArgb(255, 229, 83, 61)
$fold = [System.Drawing.Color]::FromArgb(255, 250, 205, 196)
$paperWarm = [System.Drawing.Color]::FromArgb(255, 250, 248, 245)
$ink = [System.Drawing.Color]::FromArgb(255, 44, 36, 32)
$white = [System.Drawing.Color]::White

function New-Rounded([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# The folded page, inside a square of the given size at (x, y).
function Draw-Page($g, [single]$x, [single]$y, [single]$s) {
    $left = $x + ($s * 0.27); $top = $y + ($s * 0.19); $w = $s * 0.46; $h = $s * 0.62; $cut = $s * 0.16
    $page = New-Object System.Drawing.Drawing2D.GraphicsPath
    $page.AddLines([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF($left, $top)),
        (New-Object System.Drawing.PointF(($left + $w - $cut), $top)),
        (New-Object System.Drawing.PointF(($left + $w), ($top + $cut))),
        (New-Object System.Drawing.PointF(($left + $w), ($top + $h))),
        (New-Object System.Drawing.PointF($left, ($top + $h)))))
    $page.CloseFigure()
    $b = New-Object System.Drawing.SolidBrush($white)
    $g.FillPath($b, $page)
    $b.Dispose()
    $corner = New-Object System.Drawing.Drawing2D.GraphicsPath
    $corner.AddLines([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(($left + $w - $cut), $top)),
        (New-Object System.Drawing.PointF(($left + $w), ($top + $cut))),
        (New-Object System.Drawing.PointF(($left + $w - $cut), ($top + $cut)))))
    $corner.CloseFigure()
    $f = New-Object System.Drawing.SolidBrush($fold)
    $g.FillPath($f, $corner)
    $f.Dispose()
}

function Draw-Mark($g, [single]$x, [single]$y, [single]$s) {
    $path = New-Rounded $x $y $s $s ($s * 0.2)
    $b = New-Object System.Drawing.SolidBrush($coral)
    $g.FillPath($b, $path)
    $b.Dispose()
    Draw-Page $g $x $y $s
}

function New-Canvas([int]$w, [int]$h) {
    $bitmap = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'HighQuality'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.PixelOffsetMode = 'HighQuality'
    return @($bitmap, $g)
}

foreach ($size in 16, 24, 32, 44, 48, 64, 128, 256, 512, 1024) {
    $c = New-Canvas $size $size
    Draw-Mark $c[1] 0 0 $size
    $c[1].Dispose()
    $c[0].Save((Join-Path $branding "logo\papery-logo-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $c[0].Dispose()
}

Copy-Item (Join-Path $repo 'src\Papery.App\Assets\Papery.ico') (Join-Path $branding 'logo\papery.ico') -Force
@'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><rect width="100" height="100" rx="20" fill="#E5533D"/><path d="M27 19H57L73 35V81H27Z" fill="#FFF"/><path d="M57 19L73 35H57Z" fill="#FACDC4"/></svg>
'@ | Set-Content (Join-Path $branding 'logo\papery-logo.svg') -Encoding utf8

# ---- Store art: logo on flat colour, and nothing that could look like a screenshot ----
function Draw-Centred($g, [string]$text, [string]$fontName, [single]$size, [System.Drawing.FontStyle]$style, $colour, [single]$cx, [single]$y) {
    $font = New-Object System.Drawing.Font($fontName, $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush($colour)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'
    $g.DrawString($text, $font, $brush, $cx, $y, $format)
    $format.Dispose(); $brush.Dispose(); $font.Dispose()
}

function Save-Art([string]$name, [int]$w, [int]$h) {
    $c = New-Canvas $w $h
    $g = $c[1]
    $g.Clear($coral)
    $unit = [Math]::Min($w, $h)
    if ($h -gt $w) {
        # Tall poster
        $s = $unit * 0.6
        Draw-Page $g (($w - $s) / 2) ($h * 0.16) $s
        Draw-Centred $g 'Papery' 'Segoe UI Semibold' ($unit * 0.17) 'Regular' $white ($w / 2) ($h * 0.56)
        Draw-Centred $g 'PDF editor, merge & sign' 'Segoe UI' ($unit * 0.06) 'Regular' $white ($w / 2) ($h * 0.72)
    }
    elseif ($w -gt $h * 1.2) {
        # Wide hero: the art Windows crops, so everything sits in the middle
        $s = $h * 0.46
        Draw-Page $g (($w - $s) / 2) ($h * 0.10) $s
        Draw-Centred $g 'Papery' 'Segoe UI Semibold' ($h * 0.18) 'Regular' $white ($w / 2) ($h * 0.52)
        Draw-Centred $g 'One payment. No subscription. Ever.' 'Segoe UI' ($h * 0.055) 'Regular' $white ($w / 2) ($h * 0.74)
    }
    else {
        # Square: just the mark, for tiles and small places
        Draw-Page $g 0 0 $unit
    }

    $g.Dispose()
    $c[0].Save((Join-Path $branding "store\$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $c[0].Dispose()
}

Save-Art 'StoreLogo-1x1-300x300' 300 300
Save-Art 'StoreLogo-1x1-1080x1080' 1080 1080
Save-Art 'StoreLogo-1x1-2160x2160' 2160 2160
Save-Art 'PosterArt-9x16-720x1080' 720 1080
Save-Art 'PosterArt-9x16-1440x2160' 1440 2160
Save-Art 'HeroArt-16x9-1920x1080' 1920 1080
Save-Art 'HeroArt-16x9-3840x2160' 3840 2160
Write-Host "Done: $branding" -ForegroundColor Green
