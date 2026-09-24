$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 32, 48, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()
$assetDirectory = $PSScriptRoot

foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)

    $tile = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $tile.AddArc(0, 0, 96, 96, 180, 90)
    $tile.AddArc(160, 0, 96, 96, 270, 90)
    $tile.AddArc(160, 160, 96, 96, 0, 90)
    $tile.AddArc(0, 160, 96, 96, 90, 90)
    $tile.CloseFigure()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 18, 107, 90))
    $graphics.FillPath($background, $tile)

    $signal = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $signal.AddBezier(42, 98, 87, 58, 169, 58, 214, 98)
    $signal.StartFigure()
    $signal.AddBezier(72, 129, 101, 101, 155, 101, 184, 129)
    $signal.StartFigure()
    $signal.AddBezier(103, 159, 116, 147, 140, 147, 153, 159)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 13)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawPath($pen, $signal)
    $graphics.FillEllipse([System.Drawing.Brushes]::White, 116, 199, 24, 24)

    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images.Add($stream.ToArray())
    if ($size -eq 256) {
        [System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'NetHog-icon.png'), $stream.ToArray())
    }

    $stream.Dispose()
    $pen.Dispose()
    $signal.Dispose()
    $background.Dispose()
    $tile.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

$iconStream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($iconStream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Count)

$nextOffset = 6 + (16 * $sizes.Count)
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $size = $sizes[$index]
    $payload = $images[$index]
    $dimension = if ($size -eq 256) { [byte]0 } else { [byte]$size }
    $writer.Write($dimension)
    $writer.Write($dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$payload.Length)
    $writer.Write([UInt32]$nextOffset)
    $nextOffset += $payload.Length
}

foreach ($payload in $images) { $writer.Write($payload) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'NetHog.ico'), $iconStream.ToArray())
$writer.Dispose()
$iconStream.Dispose()
