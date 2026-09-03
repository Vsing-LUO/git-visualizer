[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$PreviewPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot 'src\GitVisualizer.App\Assets\GitVisualizer.ico'
}
if ([string]::IsNullOrWhiteSpace($PreviewPath)) {
    $PreviewPath = Join-Path $PSScriptRoot 'src\GitVisualizer.App\Assets\GitVisualizerLogo-256.png'
}
Add-Type -AssemblyName System.Drawing

function New-LogoPngBytes {
    param([Parameter(Mandatory)][int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $stream = [System.IO.MemoryStream]::new()

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $scale = $Size / 32.0
        $background = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml('#142238'))
        $accent = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml('#2478FF'))

        try {
            # Exact rasterization of AppLogoImage from themes/controls.xaml.
            $graphics.FillRectangle($background, 0, 0, $Size, $Size)
            $graphics.FillEllipse($accent, 7 * $scale, 4 * $scale, 18 * $scale, 18 * $scale)
            $graphics.FillRectangle($accent, 13.5 * $scale, 15 * $scale, 5 * $scale, 12 * $scale)
            $graphics.FillRectangle($accent, 11.5 * $scale, 25 * $scale, 9 * $scale, 4 * $scale)
            $graphics.FillEllipse($background, 13 * $scale, 10 * $scale, 6 * $scale, 6 * $scale)
        }
        finally {
            $background.Dispose()
            $accent.Dispose()
        }

        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    [pscustomobject]@{
        Size = $size
        Bytes = New-LogoPngBytes -Size $size
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$fileStream = [System.IO.File]::Open(
    $OutputPath,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($fileStream)

try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

[System.IO.File]::WriteAllBytes($PreviewPath, [byte[]]$images[-1].Bytes)
Write-Host "Application icon generated: $OutputPath"
Write-Host "Logo preview generated: $PreviewPath"
