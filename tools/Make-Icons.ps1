<#
.SYNOPSIS
    Draws the Papery app icon (coral rounded square with a white folded page)
    at every size the MSIX package and the window need.

.DESCRIPTION
    Writes PNGs into src/Papery.App/Assets and a multi-size Papery.ico.
    Re-run after changing the design:   powershell -ExecutionPolicy Bypass -File tools/Make-Icons.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Papery.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$coral = [System.Drawing.Color]::FromArgb(255, 229, 83, 61)    # #E5533D brand colour
$fold  = [System.Drawing.Color]::FromArgb(255, 250, 205, 196)  # light coral for the folded corner

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Draws the icon into a square of side $s at ($ox, $oy).
function Draw-Icon($g, [single]$ox, [single]$oy, [single]$s) {
    $bg = New-RoundedRect $ox $oy $s $s ($s * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush $coral), $bg)

    # The page: 50% wide, 62% tall, centred; top-right corner folded.
    $pw = $s * 0.50; $ph = $s * 0.62
    $px = $ox + ($s - $pw) / 2; $py = $oy + ($s - $ph) / 2
    $f = $pw * 0.36
    $page = New-Object System.Drawing.Drawing2D.GraphicsPath
    $page.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF($px, $py)),
        (New-Object System.Drawing.PointF(($px + $pw - $f), $py)),
        (New-Object System.Drawing.PointF(($px + $pw), ($py + $f))),
        (New-Object System.Drawing.PointF(($px + $pw), ($py + $ph))),
        (New-Object System.Drawing.PointF($px, ($py + $ph)))))
    $g.FillPath([System.Drawing.Brushes]::White, $page)

    $corner = New-Object System.Drawing.Drawing2D.GraphicsPath
    $corner.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(($px + $pw - $f), $py)),
        (New-Object System.Drawing.PointF(($px + $pw - $f), ($py + $f))),
        (New-Object System.Drawing.PointF(($px + $pw), ($py + $f)))))
    $g.FillPath((New-Object System.Drawing.SolidBrush $fold), $corner)

    # Text lines only where they stay crisp.
    if ($s -ge 64) {
        $pen = New-Object System.Drawing.Pen($fold, [single]($s * 0.035))
        foreach ($i in 0..2) {
            $ly = $py + $ph * (0.48 + $i * 0.14)
            $g.DrawLine($pen, $px + $pw * 0.18, $ly, $px + $pw * ($(if ($i -eq 2) { 0.6 } else { 0.82 })), $ly)
        }
    }
}

# Writes a $w x $h PNG with the icon centred at $iconScale of the shorter side.
function Save-Png([string]$name, [int]$w, [int]$h, [double]$iconScale = 1.0) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = [Math]::Min($w, $h) * $iconScale
    Draw-Icon $g (($w - $s) / 2) (($h - $s) / 2) $s
    $g.Dispose()
    $path = Join-Path $assets $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $path
}

foreach ($scale in 100, 200, 400) {
    $k = $scale / 100
    Save-Png "Square44x44Logo.scale-$scale.png" (44 * $k) (44 * $k) | Out-Null
    Save-Png "Square150x150Logo.scale-$scale.png" (150 * $k) (150 * $k) 0.6 | Out-Null
    Save-Png "Wide310x150Logo.scale-$scale.png" (310 * $k) (150 * $k) 0.6 | Out-Null
    Save-Png "SmallTile.scale-$scale.png" (71 * $k) (71 * $k) 0.7 | Out-Null
    Save-Png "LargeTile.scale-$scale.png" (310 * $k) (310 * $k) 0.5 | Out-Null
    Save-Png "StoreLogo.scale-$scale.png" (50 * $k) (50 * $k) | Out-Null
    Save-Png "SplashScreen.scale-$scale.png" (620 * $k) (300 * $k) 0.4 | Out-Null
}

# Plain files for use inside the app's own UI (title bar, About). Qualified names like
# "targetsize-24" are resource variants, so XAML can't reference them directly.
Save-Png "AppIcon-32.png" 32 32 | Out-Null
Save-Png "AppIcon-64.png" 64 64 | Out-Null

# Taskbar / Start / File Explorer sizes, both plated and "unplated" (no system backplate).
$icoFrames = @()
foreach ($t in 16, 20, 24, 32, 40, 48, 64, 256) {
    $p = Save-Png "Square44x44Logo.targetsize-$t.png" $t $t
    Copy-Item $p (Join-Path $assets "Square44x44Logo.targetsize-${t}_altform-unplated.png") -Force
    Copy-Item $p (Join-Path $assets "Square44x44Logo.targetsize-${t}_altform-lightunplated.png") -Force
    $icoFrames += , @($t, [System.IO.File]::ReadAllBytes($p))
}

# Multi-size .ico (PNG-compressed frames, supported since Windows Vista).
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$icoFrames.Count)
$offset = 6 + 16 * $icoFrames.Count
foreach ($f in $icoFrames) {
    $size = $f[0]; $data = $f[1]
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($f in $icoFrames) { $bw.Write([byte[]]$f[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'Papery.ico'), $ico.ToArray())

Write-Host "Icons written to $assets"
