param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# Deterministic application identity: navy rounded tile, turntable platter,
# and turquoise metadata tag. Native ICO for EXE, taskbar and shortcuts.
$pngImages = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in @(16, 32, 48, 256)) {
    $bmp = [System.Drawing.Bitmap]::new(
        $size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.ScaleTransform([single]($size / 256.0), [single]($size / 256.0))

        $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $shape.AddArc(8, 8, 72, 72, 180, 90)
        $shape.AddArc(176, 8, 72, 72, 270, 90)
        $shape.AddArc(176, 176, 72, 72, 0, 90)
        $shape.AddArc(8, 176, 72, 72, 90, 90)
        $shape.CloseFigure()
        $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.Point]::new(8,8),
            [System.Drawing.Point]::new(248,248),
            [System.Drawing.Color]::FromArgb(255, 17, 34, 68),
            [System.Drawing.Color]::FromArgb(255, 8, 16, 34))
        $g.FillPath($brush, $shape)

        $rim = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(255, 94, 232, 221), 12)
        $g.DrawEllipse($rim, 47, 47, 155, 155)
        $disc = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 229, 244, 250))
        $g.FillEllipse($disc, 91, 91, 67, 67)
        $hole = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 17, 34, 68))
        $g.FillEllipse($hole, 117, 117, 15, 15)

        $tag = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $tag.AddPolygon([System.Drawing.Point[]]@(
            [System.Drawing.Point]::new(145, 150),
            [System.Drawing.Point]::new(218, 150),
            [System.Drawing.Point]::new(237, 178),
            [System.Drawing.Point]::new(195, 220),
            [System.Drawing.Point]::new(145, 170)))
        $tagFill = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 74, 221, 206))
        $g.FillPath($tagFill, $tag)
        $g.FillEllipse($hole, 206, 164, 11, 11)

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngImages.Add($stream.ToArray())
        }
        finally { $stream.Dispose() }
    }
    finally {
        $g.Dispose(); $bmp.Dispose()
    }
}

$parent = [System.IO.Path]::GetDirectoryName(
    [System.IO.Path]::GetFullPath($OutputPath))
[System.IO.Directory]::CreateDirectory($parent) | Out-Null
$stream = [System.IO.File]::Open(
    $OutputPath, [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$pngImages.Count)
        $offset = 6 + 16 * $pngImages.Count
        for ($i = 0; $i -lt $pngImages.Count; $i++) {
            $size = @(16, 32, 48, 256)[$i]
            $writer.Write([byte]($size % 256))
            $writer.Write([byte]($size % 256))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$pngImages[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $pngImages[$i].Length
        }
        foreach ($image in $pngImages) { $writer.Write([byte[]]$image) }
    }
    finally { $writer.Dispose() }
}
finally { $stream.Dispose() }
