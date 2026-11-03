# Draws the PhotoScope icon and writes src\PhotoScope\Assets\PhotoScope.ico (16-256 px) and PhotoScope.png (256 px).
# Each size is rendered separately from vector shapes so small sizes stay crisp.
# Run with Windows PowerShell in STA mode:  powershell -STA -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase, PresentationFramework

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\PhotoScope\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function P([double]$x, [double]$y, [double]$s) { [System.Windows.Point]::new($x * $s, $y * $s) }

function Render([int]$size) {
    $s = [double]$size
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()

    # Rounded tile with a soft blue gradient (Windows 11 accent blues).
    $margin = [Math]::Max(0.5, $s * 0.04)
    $tile = [System.Windows.Rect]::new($margin, $margin, $s - 2 * $margin, $s - 2 * $margin)
    $radius = $s * 0.22
    $grad = [System.Windows.Media.LinearGradientBrush]::new(
        [System.Windows.Media.Color]::FromRgb(0x3A, 0x9B, 0xEF),
        [System.Windows.Media.Color]::FromRgb(0x00, 0x5F, 0xB8),
        [System.Windows.Point]::new(0, 0), [System.Windows.Point]::new(1, 1))
    $dc.DrawRoundedRectangle($grad, $null, $tile, $radius, $radius)

    # Subtle top highlight for depth (skipped at the smallest sizes).
    if ($size -ge 32) {
        $hl = [System.Windows.Media.LinearGradientBrush]::new(
            [System.Windows.Media.Color]::FromArgb(0x38, 0xFF, 0xFF, 0xFF),
            [System.Windows.Media.Color]::FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90)
        $dc.DrawRoundedRectangle($hl, $null, [System.Windows.Rect]::new($margin, $margin, $s - 2 * $margin, ($s - 2 * $margin) * 0.5), $radius, $radius)
    }

    # Sun.
    $sun = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0xFF, 0xD8, 0x6B))
    $dc.DrawEllipse($sun, $null, (P 0.68 0.33 $s), $s * 0.095, $s * 0.095)

    # Mountains: a far one (translucent) and a near one (white).
    $far = New-Object System.Windows.Media.StreamGeometry
    $ctx = $far.Open()
    $ctx.BeginFigure((P 0.40 0.80 $s), $true, $true)
    $ctx.LineTo((P 0.64 0.50 $s), $true, $true)
    $ctx.LineTo((P 0.86 0.80 $s), $true, $true)
    $ctx.Close()
    $dc.DrawGeometry((New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(0xA6, 0xFF, 0xFF, 0xFF))), $null, $far)

    $near = New-Object System.Windows.Media.StreamGeometry
    $ctx = $near.Open()
    $ctx.BeginFigure((P 0.14 0.80 $s), $true, $true)
    $ctx.LineTo((P 0.40 0.42 $s), $true, $true)
    $ctx.LineTo((P 0.62 0.80 $s), $true, $true)
    $ctx.Close()
    $dc.DrawGeometry([System.Windows.Media.Brushes]::White, $null, $near)

    $dc.Close()
    $rtb = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    # 32-bit PNG entries with straight alpha (what the Windows icon decoder expects).
    $bgra = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new($rtb, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bgra))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = @{}
foreach ($size in $sizes) { $images[$size] = Render $size }

# ICO container: header, directory, then PNG-compressed images.
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($size in $sizes) {
    $b = if ($size -ge 256) { 0 } else { $size }
    $data = $images[$size]
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($size in $sizes) { $w.Write($images[$size]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'PhotoScope.ico'), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $assets 'PhotoScope.png'), $images[256])
"Wrote PhotoScope.ico ($($sizes -join ', ') px) and PhotoScope.png to $assets"
