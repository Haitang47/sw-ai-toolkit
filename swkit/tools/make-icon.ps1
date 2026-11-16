# 生成程序图标：橙色等轴测立方体，左面白色 S、右面白色 W（随面倾斜），右下角白字黑边 "tool"
# 输出：src\App\app.ico（16–256 多尺寸：exe、窗口、任务栏）、src\App\logo.png（256：侧栏、README）、tools\icon-preview.png（预览）
# 每个尺寸单独按矢量渲染；≤ 32 像素不画 "tool"（太小看不清），立方体放大一些。
# 用法：powershell -STA -ExecutionPolicy Bypass -File .\tools\make-icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$outIco = Join-Path $root 'src\App\app.ico'
$outPng = Join-Path $root 'src\App\logo.png'

function C([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
function B([string]$hex) { $b = New-Object System.Windows.Media.SolidColorBrush (C $hex); $b.Freeze(); $b }
function P([double]$x, [double]$y) { New-Object System.Windows.Point($x, $y) }

function Poly($pts) {
    $g = New-Object System.Windows.Media.StreamGeometry
    $ctx = $g.Open()
    $ctx.BeginFigure($pts[0], $true, $true)
    for ($i = 1; $i -lt $pts.Count; $i++) { $ctx.LineTo($pts[$i], $true, $true) }
    $ctx.Close()
    $g.Freeze()
    return $g
}

# 把文字轮廓放进一个平行四边形（面上的字）：先缩放到 w×h，再按面的斜率 slope 做竖直错切，最后平移到 (x, y)
function GlyphOnFace([string]$text, [string]$font, $weight, [double]$x, [double]$y, [double]$w, [double]$h, [double]$slope) {
    $face = New-Object System.Windows.Media.Typeface((New-Object System.Windows.Media.FontFamily($font)), [System.Windows.FontStyles]::Normal, $weight, [System.Windows.FontStretches]::Normal)
    $ft = New-Object System.Windows.Media.FormattedText($text, [Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $face, 100, [System.Windows.Media.Brushes]::White, 1.0)
    $geo = $ft.BuildGeometry((P 0 0))
    $b = $geo.Bounds
    $tg = New-Object System.Windows.Media.TransformGroup
    $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform(-$b.X, -$b.Y)))
    $tg.Children.Add((New-Object System.Windows.Media.ScaleTransform(($w / $b.Width), ($h / $b.Height))))
    $tg.Children.Add((New-Object System.Windows.Media.MatrixTransform((New-Object System.Windows.Media.Matrix(1, $slope, 0, 1, 0, 0)))))
    $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform($x, $y)))
    $pg = $geo.GetFlattenedPathGeometry()
    $pg.Transform = $tg
    return $pg
}

function Render([int]$size) {
    $s = [double]$size
    $small = $size -le 32
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    # 立方体的几何（以 256 为基准，按比例缩放）。小尺寸时立方体占满画布
    $k = $s / 256.0
    if ($small) { $ox = 26; $oy = 10; $cw = 204; $tz = 46 } else { $ox = 10; $oy = 8; $cw = 190; $tz = 44 }
    $half = $cw / 2
    $ch = if ($small) { 236 } else { 196 }                  # 立方体总高（顶点到底角）
    $sideH = $ch - $tz                                      # 侧面竖直高度（不含顶面）
    $apex = P (($ox + $half) * $k) ($oy * $k)
    $lt = P ($ox * $k) (($oy + $tz / 2) * $k)
    $rt = P (($ox + $cw) * $k) (($oy + $tz / 2) * $k)
    $front = P (($ox + $half) * $k) (($oy + $tz) * $k)
    $lb = P ($ox * $k) (($oy + $tz / 2 + $sideH) * $k)
    $rb = P (($ox + $cw) * $k) (($oy + $tz / 2 + $sideH) * $k)
    $fb = P (($ox + $half) * $k) (($oy + $tz + $sideH) * $k)
    $outline = New-Object System.Windows.Media.Pen((B '#3A1600'), [Math]::Max(0.8, 5.5 * $k))
    $outline.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $edge = New-Object System.Windows.Media.Pen((B '#5A2400'), [Math]::Max(0.5, 2.0 * $k))
    # 三个面：顶面最亮、左面中等、右面最暗（和原版红色立方体同样的明暗关系，换成橙色）
    $topG = New-Object System.Windows.Media.LinearGradientBrush((C '#FFB04A'), (C '#FF8A1A'), 90)
    $leftG = New-Object System.Windows.Media.LinearGradientBrush((C '#F2780F'), (C '#D95F00'), 90)
    $rightG = New-Object System.Windows.Media.LinearGradientBrush((C '#A84800'), (C '#8A3A00'), 90)
    $dc.DrawGeometry($leftG, $null, (Poly @($lt, $front, $fb, $lb)))
    $dc.DrawGeometry($rightG, $null, (Poly @($front, $rt, $rb, $fb)))
    $dc.DrawGeometry($topG, $null, (Poly @($apex, $rt, $front, $lt)))
    # 内部棱线
    $dc.DrawLine($edge, $front, $fb)
    $dc.DrawLine($edge, $lt, $front)
    $dc.DrawLine($edge, $front, $rt)
    # 外轮廓
    $dc.DrawGeometry($null, $outline, (Poly @($apex, $rt, $rb, $fb, $lb, $lt)))

    # 面上的字：左面 S、右面 W，随面倾斜（左面向右下倾，右面向右上倾）
    $slope = ($tz / 2) / $half
    $white = [System.Windows.Media.Brushes]::White
    $sw = $half * 0.62; $sh = $sideH * 0.66
    $sx = ($ox + $half * 0.19) * $k; $sy = ($oy + $tz / 2 + $sideH * 0.13 + $half * 0.19 * $slope) * $k
    $dc.DrawGeometry($white, $null, (GlyphOnFace 'S' 'Arial' ([System.Windows.FontWeights]::Bold) $sx $sy ($sw * $k) ($sh * $k) $slope))
    $wx = ($ox + $half * 1.13) * $k; $wy = ($oy + $tz - $half * 0.13 * $slope + $sideH * 0.10) * $k
    $dc.DrawGeometry($white, $null, (GlyphOnFace 'W' 'Arial' ([System.Windows.FontWeights]::Bold) $wx $wy (($half * 0.74) * $k) (($sideH * 0.62) * $k) (-$slope)))

    # 右下角 "tool"：白字 + 粗黑边（原版 "2025" 的样式）
    if (-not $small) {
        $face = New-Object System.Windows.Media.Typeface((New-Object System.Windows.Media.FontFamily('Arial')), [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::Black, [System.Windows.FontStretches]::Normal)
        $ft = New-Object System.Windows.Media.FormattedText('tool', [Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $face, 100, $white, 1.0)
        $g = $ft.BuildGeometry((P 0 0))
        $b = $g.Bounds
        $tw = 150 * $k; $th = 62 * $k
        $tx = (250 - 150) * $k; $ty = (250 - 62) * $k
        $tg = New-Object System.Windows.Media.TransformGroup
        $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform(-$b.X, -$b.Y)))
        $tg.Children.Add((New-Object System.Windows.Media.ScaleTransform(($tw / $b.Width), ($th / $b.Height))))
        $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform($tx, $ty)))
        $pg = $g.GetFlattenedPathGeometry(); $pg.Transform = $tg
        $stroke = New-Object System.Windows.Media.Pen((B '#111111'), [Math]::Max(1.2, 13 * $k))
        $stroke.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        $dc.DrawGeometry($null, $stroke, $pg)
        $dc.DrawGeometry($white, $null, $pg)
    }
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$pngs = @{}
foreach ($z in $sizes) { $pngs[$z] = Render $z }
[IO.File]::WriteAllBytes($outPng, $pngs[256])

# ICO：每个尺寸一个 PNG 帧（Windows Vista 起支持）
$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter($ms)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($z in $sizes) {
    $d = $pngs[$z]
    $w.Write([byte]($(if ($z -ge 256) { 0 } else { $z }))); $w.Write([byte]($(if ($z -ge 256) { 0 } else { $z })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$d.Length); $w.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($z in $sizes) { $w.Write($pngs[$z]) }
$w.Flush()
[IO.File]::WriteAllBytes($outIco, $ms.ToArray())
Write-Host "已生成 $outIco 和 $outPng" -ForegroundColor Green

# 预览：浅色、深色背景各一行
$prev = Join-Path $root 'tools\icon-preview.png'
$dv = New-Object System.Windows.Media.DrawingVisual
$dc = $dv.RenderOpen()
$dc.DrawRectangle([System.Windows.Media.Brushes]::White, $null, (New-Object System.Windows.Rect(0, 0, 760, 600)))
$dc.DrawRectangle((B '#202024'), $null, (New-Object System.Windows.Rect(0, 300, 760, 300)))
$xx = 12
foreach ($z in $sizes) {
    $src = New-Object System.Windows.Media.Imaging.BitmapImage
    $src.BeginInit(); $src.StreamSource = New-Object IO.MemoryStream(, $pngs[$z]); $src.EndInit()
    $dc.DrawImage($src, (New-Object System.Windows.Rect($xx, 20, $z, $z)))
    $dc.DrawImage($src, (New-Object System.Windows.Rect($xx, 320, $z, $z)))
    $xx += [Math]::Min($z, 256) + 12
    if ($xx -gt 700) { break }
}
$dc.Close()
$bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(760, 600, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($dv)
$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
$fs = [IO.File]::Create($prev); $enc.Save($fs); $fs.Close()
