# Regenerates src/Jarvis.App/Assets/jarvis.ico (exe/taskbar icon). Same drawing as TrayIconRenderer (idle state).
# Run with Windows PowerShell: powershell -File tools/New-AppIcon.ps1
Add-Type -AssemblyName PresentationCore, WindowsBase

function New-Color([int]$r, [int]$g, [int]$b, [int]$a = 255) { [Windows.Media.Color]::FromArgb($a, $r, $g, $b) }
function New-Point([double]$x, [double]$y) { New-Object Windows.Point $x, $y }
function New-Rect([double]$x, [double]$y, [double]$w, [double]$h) { New-Object Windows.Rect $x, $y, $w, $h }

function Get-IconPng([int]$size) {
    $accent = New-Color 0x00 0xE5 0xFF
    $accentBrush = New-Object Windows.Media.SolidColorBrush $accent
    $glow = New-Object Windows.Media.RadialGradientBrush (New-Color 0x00 0xE5 0xFF 0xB0), (New-Color 0x00 0xE5 0xFF 0)
    $shell = New-Object Windows.Media.LinearGradientBrush (New-Color 0xFF 0xFF 0xFF), (New-Color 0xA9 0xB6 0xF0), 90.0
    $dark = New-Object Windows.Media.SolidColorBrush (New-Color 0x16 0x1A 0x35)
    $outline = New-Object Windows.Media.Pen $dark, 2.5
    $stem = New-Object Windows.Media.Pen $dark, 4
    $visor = New-Object Windows.Media.LinearGradientBrush (New-Color 0x23 0x2A 0x55), (New-Color 0x10 0x13 0x2B), 90.0

    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $s = $size / 64.0
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform $s, $s))
    $dc.DrawLine($stem, (New-Point 32 14), (New-Point 32 8))
    $dc.DrawEllipse($accentBrush, $outline, (New-Point 32 7), 5.5, 5.5)
    $dc.DrawRoundedRectangle($shell, $outline, (New-Rect 2.5 13 59 49), 19, 19)
    $dc.DrawRoundedRectangle($visor, $null, (New-Rect 8 21 48 33), 14, 14)
    foreach ($x in 21.5, 42.5) {
        $dc.DrawEllipse($glow, $null, (New-Point $x 37.5), 13, 13)
        $dc.DrawRoundedRectangle($accentBrush, $null, (New-Rect ($x - 6) 28.5 12 18), 6, 6)
        $dc.DrawEllipse([Windows.Media.Brushes]::White, $null, (New-Point ($x - 2) 33), 2, 2)
    }
    $dc.Close()

    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    , $stream.ToArray()
}

$sizes = 16, 20, 24, 32, 48, 64, 256
$images = @($sizes | ForEach-Object { , (Get-IconPng $_) })

# ICO container with PNG-compressed entries.
$out = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Flush()

$target = Join-Path $PSScriptRoot '..\src\Jarvis.App\Assets\jarvis.ico'
[IO.File]::WriteAllBytes($target, $out.ToArray())
"Wrote $target ($($out.Length) bytes)"
