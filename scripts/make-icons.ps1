param([string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src'))
# Draws the Controller and Agent icons at 16/24/32/48/64/256 px and writes PNG-compressed .ico files.
# Controller (blue): a window with a send arrow. Agent (green): a window with a pointer on an element.
# Run with Windows PowerShell 5.1 (System.Drawing). The generated .ico files are committed; rerun only to change the design.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
function New-Frame([int]$size, [string]$role) {
    $bitmap = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
    $s = $size / 256.0
    function Rect([double]$x, [double]$y, [double]$w, [double]$h) { New-Object Drawing.RectangleF ([float]($x*$s)), ([float]($y*$s)), ([float]($w*$s)), ([float]($h*$s)) }
    function RoundRect([Drawing.RectangleF]$r, [double]$radius) {
        $p = New-Object Drawing.Drawing2D.GraphicsPath; $d = [float]([Math]::Max(1, $radius*$s*2))
        $p.AddArc($r.X, $r.Y, $d, $d, 180, 90); $p.AddArc($r.Right-$d, $r.Y, $d, $d, 270, 90)
        $p.AddArc($r.Right-$d, $r.Bottom-$d, $d, $d, 0, 90); $p.AddArc($r.X, $r.Bottom-$d, $d, $d, 90, 90); $p.CloseFigure(); return $p
    }
    $top = if ($role -eq 'Controller') { [Drawing.Color]::FromArgb(255, 59, 130, 246) } else { [Drawing.Color]::FromArgb(255, 16, 185, 129) }
    $bottom = if ($role -eq 'Controller') { [Drawing.Color]::FromArgb(255, 29, 78, 216) } else { [Drawing.Color]::FromArgb(255, 4, 120, 87) }
    $tile = Rect 8 8 240 240
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush $tile, $top, $bottom, 90
    $g.FillPath($brush, (RoundRect $tile 52))
    # A desktop window: white body with a tinted title bar.
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
    $tint = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 226, 232, 240))
    $g.FillPath($white, (RoundRect (Rect 40 56 176 136) 14))
    $g.FillPath($tint, (RoundRect (Rect 40 56 176 34) 14)); $g.FillRectangle($tint, (Rect 40 76 176 14))
    $accent = New-Object Drawing.SolidBrush $bottom
    if ($size -ge 32) { foreach ($x in 58, 80, 102) { $g.FillEllipse($accent, (Rect $x 66 14 14)) } }
    if ($role -eq 'Controller') {
        # Lines of a request and an arrow leaving the window.
        if ($size -ge 32) { $g.FillRectangle($tint, (Rect 58 112 92 14)); $g.FillRectangle($tint, (Rect 58 140 64 14)) }
        $arrow = New-Object Drawing.Drawing2D.GraphicsPath
        $points = @((128,150),(178,150),(178,126),(226,170),(178,214),(178,190),(128,190)) | ForEach-Object { New-Object Drawing.PointF ([float]($_[0]*$s)), ([float]($_[1]*$s)) }
        $arrow.AddPolygon([Drawing.PointF[]]$points)
        $g.FillPath($accent, $arrow); $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::White), ([float]([Math]::Max(1, 8*$s)))), $arrow)
    } else {
        # A highlighted element and a mouse pointer on it.
        $g.FillPath($accent, (RoundRect (Rect 58 112 104 40) 10))
        $pointer = New-Object Drawing.Drawing2D.GraphicsPath
        $points = @((150,128),(150,226),(174,202),(192,240),(210,232),(192,194),(226,194)) | ForEach-Object { New-Object Drawing.PointF ([float]($_[0]*$s)), ([float]($_[1]*$s)) }
        $pointer.AddPolygon([Drawing.PointF[]]$points)
        $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 15, 23, 42))), $pointer)
        $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::White), ([float]([Math]::Max(1, 9*$s)))), $pointer)
    }
    $g.Dispose()
    $stream = New-Object IO.MemoryStream; $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
    return ,$stream.ToArray()
}
foreach ($role in 'Controller', 'Agent') {
    $sizes = 16, 24, 32, 48, 64, 256
    $frames = @($sizes | ForEach-Object { ,(New-Frame $_ $role) })
    $file = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $file
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $px = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$px); $w.Write([byte]$px); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $w.Write([byte[]]$frame) }
    $w.Flush()
    $target = Join-Path $Output ($role + '\' + $role + '.ico')
    [IO.File]::WriteAllBytes($target, $file.ToArray())
    Write-Output "$target ($($file.Length) bytes)"
}
