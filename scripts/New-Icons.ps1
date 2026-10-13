<#
.SYNOPSIS
    Renders the DevPortal "hub" logo into the app/tray icons and the favicon.
.DESCRIPTION
    The same geometry as wwwroot/favicon.svg (32x32 viewBox), drawn with GDI+ so no external tools are needed.
    Sizes up to 24 px use heavier strokes and larger nodes so the mark stays readable in the tray.
    Writes src/DevPortal/Assets/devportal.ico, devportal-stopped.ico and src/DevPortal/wwwroot/favicon.ico.
#>
[CmdletBinding()]
param([string]$Root = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-LogoBitmap([int]$size, [string]$background) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 32, $size / 32)

    $small = $size -le 24
    $inset = if ($small) { 0 } else { 1 }
    $radius = if ($small) { 7 } else { 8 }
    $spoke = if ($small) { 3.4 } else { 2.6 }
    $hub = if ($small) { 5 } else { 4.2 }
    $node = if ($small) { 3.9 } else { 3 }
    $ring = if ($small) { 1.3 } else { 1.6 }

    $dark = [System.Drawing.ColorTranslator]::FromHtml('#0b1f1b')
    $tile = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $extent = 32 - 2 * $inset
    $d = 2 * $radius
    $tile.AddArc($inset, $inset, $d, $d, 180, 90)
    $tile.AddArc($inset + $extent - $d, $inset, $d, $d, 270, 90)
    $tile.AddArc($inset + $extent - $d, $inset + $extent - $d, $d, $d, 0, 90)
    $tile.AddArc($inset, $inset + $extent - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($background)), $tile)

    $pen = [System.Drawing.Pen]::new($dark, $spoke)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $nodes = @(@(8, 8.5), @(24, 8.5), @(16, 25))
    foreach ($n in $nodes) { $g.DrawLine($pen, 16, 16, $n[0], $n[1]) }
    $g.FillEllipse([System.Drawing.SolidBrush]::new($dark), 16 - $hub, 16 - $hub, 2 * $hub, 2 * $hub)

    $ringPen = [System.Drawing.Pen]::new($dark, $ring)
    foreach ($n in $nodes) {
        $g.FillEllipse([System.Drawing.Brushes]::White, $n[0] - $node, $n[1] - $node, 2 * $node, 2 * $node)
        $g.DrawEllipse($ringPen, $n[0] - $node, $n[1] - $node, 2 * $node, 2 * $node)
    }
    $g.Dispose()
    $bitmap
}

# Classic 32-bit DIB entries below 256 px (widest loader support), PNG for 256 px.
function Get-IconEntry([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $stream = [System.IO.MemoryStream]::new()
    if ($size -ge 256) {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    $writer = [System.IO.BinaryWriter]::new($stream)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($size * $size * 4 + $maskStride * $size))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bitmap.GetPixel($x, $y)
            $writer.Write([byte]$c.B); $writer.Write([byte]$c.G); $writer.Write([byte]$c.R); $writer.Write([byte]$c.A)
        }
    }
    $writer.Write([byte[]]::new($maskStride * $size))
    $writer.Flush()
    ,$stream.ToArray()
}

function Write-Icon([string]$path, [string]$background) {
    $entries = foreach ($size in $sizes) {
        $bitmap = New-LogoBitmap $size $background
        [pscustomobject]@{ Size = $size; Data = [byte[]](Get-IconEntry $bitmap) }
        $bitmap.Dispose()
    }
    $stream = [System.IO.File]::Create($path)
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]$e.Data.Length); $writer.Write([int]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $writer.Write($e.Data) }
    $writer.Dispose()
    Write-Host "Wrote $path"
}

$assets = Join-Path $Root 'src/DevPortal/Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
Write-Icon (Join-Path $assets 'devportal.ico') '#22c3a6'
Write-Icon (Join-Path $assets 'devportal-stopped.ico') '#8b939e'
Copy-Item (Join-Path $assets 'devportal.ico') (Join-Path $Root 'src/DevPortal/wwwroot/favicon.ico') -Force
