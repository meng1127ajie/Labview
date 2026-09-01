param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\V3RttMonitor.App\Assets\JustFloatMonitor.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath([System.Drawing.RectangleF]$Rect, [float]$Radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($Rect.Left, $Rect.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Rect.Right - $diameter, $Rect.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Rect.Right - $diameter, $Rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rect.Left, $Rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$entries = [System.Collections.Generic.List[object]]::new()

foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(0.5, $size * 0.025)
    $rect = [System.Drawing.RectangleF]::new($inset, $inset, $size - 2 * $inset, $size - 2 * $inset)
    $radius = [float]($size * 0.21)
    $path = New-RoundedRectanglePath $rect $radius
    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect,
        [System.Drawing.ColorTranslator]::FromHtml('#075985'),
        [System.Drawing.ColorTranslator]::FromHtml('#0C4A6E'),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $graphics.FillPath($background, $path)

    $borderWidth = [float][Math]::Max(0.7, $size * 0.018)
    $border = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#38BDF8'), $borderWidth)
    $graphics.DrawPath($border, $path)

    $normalized = @(
        @(0.11, 0.58), @(0.25, 0.58), @(0.33, 0.34), @(0.43, 0.76),
        @(0.54, 0.22), @(0.65, 0.63), @(0.76, 0.44), @(0.89, 0.44)
    )
    $points = [System.Drawing.PointF[]]($normalized | ForEach-Object {
        [System.Drawing.PointF]::new([float]($_[0] * $size), [float]($_[1] * $size))
    })
    $waveWidth = [float][Math]::Max(1.4, $size * 0.075)
    $wave = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#38BDF8'), $waveWidth)
    $wave.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $wave.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $wave.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawLines($wave, $points)

    $memory = [System.IO.MemoryStream]::new()
    $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries.Add([pscustomobject]@{ Size = $size; Bytes = $memory.ToArray() })

    $memory.Dispose()
    $wave.Dispose()
    $border.Dispose()
    $background.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

$directory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutputPath))
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$output = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($entry in $entries) {
    $writer.Write([byte]($(if ($entry.Size -eq 256) { 0 } else { $entry.Size })))
    $writer.Write([byte]($(if ($entry.Size -eq 256) { 0 } else { $entry.Size })))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$entry.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $entry.Bytes.Length
}
foreach ($entry in $entries) { $writer.Write([byte[]]$entry.Bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($OutputPath), $output.ToArray())
$writer.Dispose()
$output.Dispose()

Write-Output "Generated: $([System.IO.Path]::GetFullPath($OutputPath))"
