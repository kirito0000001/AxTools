[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePath,
    [string]$AssetsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$sourceFullPath = [IO.Path]::GetFullPath($SourcePath)
$assetsFullPath = [IO.Path]::GetFullPath($AssetsDirectory)
if (!(Test-Path -LiteralPath $sourceFullPath -PathType Leaf)) {
    throw "图标母版不存在：$sourceFullPath"
}

New-Item -ItemType Directory -Path $assetsFullPath -Force | Out-Null
$sourceImage = [Drawing.Bitmap]::new($sourceFullPath)

function New-ScaledBitmap {
    param(
        [Parameter(Mandatory)][int]$Width,
        [Parameter(Mandatory)][int]$Height,
        [Parameter(Mandatory)][Drawing.Color]$Background
    )

    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear($Background)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality

        $side = [Math]::Min($Width, $Height)
        $x = [int](($Width - $side) / 2)
        $y = [int](($Height - $side) / 2)
        $graphics.DrawImage(
            $sourceImage,
            [Drawing.Rectangle]::new($x, $y, $side, $side),
            0,
            0,
            $sourceImage.Width,
            $sourceImage.Height,
            [Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Save-PngAsset {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$Width,
        [Parameter(Mandatory)][int]$Height,
        [Parameter(Mandatory)][Drawing.Color]$Background
    )

    $bitmap = New-ScaledBitmap -Width $Width -Height $Height -Background $Background
    try {
        $bitmap.Save(
            (Join-Path $assetsFullPath $Name),
            [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Get-IconPngBytes {
    param(
        [Parameter(Mandatory)][int]$Size,
        [Parameter(Mandatory)][Drawing.Color]$Background
    )

    $bitmap = New-ScaledBitmap -Width $Size -Height $Size -Background $Background
    $stream = [IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

function Save-MultiSizeIcon {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int[]]$Sizes,
        [Parameter(Mandatory)][Drawing.Color]$Background
    )

    $images = @($Sizes | ForEach-Object {
        [pscustomobject]@{
            Size = $_
            Bytes = [byte[]](Get-IconPngBytes -Size $_ -Background $Background)
        }
    })
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$images.Count)

        $offset = 6 + (16 * $images.Count)
        foreach ($image in $images) {
            $dimensionByte = if ($image.Size -ge 256) { [byte]0 } else { [byte]$image.Size }
            $writer.Write($dimensionByte)
            $writer.Write($dimensionByte)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$image.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $image.Bytes.Length
        }

        foreach ($image in $images) {
            $writer.Write($image.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

try {
    $background = $sourceImage.GetPixel(0, 0)
    Copy-Item -LiteralPath $sourceFullPath -Destination (Join-Path $assetsFullPath 'AxToolsIconSource.jpg') -Force

    Save-PngAsset -Name 'AxToolsIcon.png' -Width 256 -Height 256 -Background $background
    Save-PngAsset -Name 'LockScreenLogo.scale-200.png' -Width 48 -Height 48 -Background $background
    Save-PngAsset -Name 'SplashScreen.scale-200.png' -Width 1240 -Height 600 -Background $background
    Save-PngAsset -Name 'Square150x150Logo.scale-200.png' -Width 300 -Height 300 -Background $background
    Save-PngAsset -Name 'Square44x44Logo.scale-200.png' -Width 88 -Height 88 -Background $background
    Save-PngAsset -Name 'Square44x44Logo.targetsize-24_altform-unplated.png' -Width 24 -Height 24 -Background $background
    Save-PngAsset -Name 'StoreLogo.png' -Width 50 -Height 50 -Background $background
    Save-PngAsset -Name 'Wide310x150Logo.scale-200.png' -Width 620 -Height 300 -Background $background
    Save-MultiSizeIcon `
        -Path (Join-Path $assetsFullPath 'AxTools.ico') `
        -Sizes @(16, 24, 32, 48, 64, 128, 256) `
        -Background $background
}
finally {
    $sourceImage.Dispose()
}

Write-Output "PASS: AxTools 图标资源已生成到 $assetsFullPath"
