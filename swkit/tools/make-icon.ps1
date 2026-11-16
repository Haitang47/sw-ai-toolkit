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
    # 立方体（以 256 为基准）：和原版一样几乎占满图标；顶面高度约为宽度的 0.26
    $k = $s / 256.0
    if ($small) { $ox = 12; $oy = 8; $cw = 232; $ch = 240 } else { $ox = 8; $oy = 6; $cw = 222; $ch = 214 }
    $half = $cw / 2
    $tz = $cw * 0.26
    $sideH = $ch - $tz
    $apex = P (($ox + $half) * $k) ($oy * $k)
    $lt = P ($ox * $k) (($oy + $tz / 2) * $k)
    $rt = P (($ox + $cw) * $k) (($oy + $tz / 2) * $k)
    $front = P (($ox + $half) * $k) (($oy + $tz) * $k)
    $lb = P ($ox * $k) (($oy + $tz / 2 + $sideH) * $k)
    $rb = P (($ox + $cw) * $k) (($oy + $tz / 2 + $sideH) * $k)
    $fb = P (($ox + $half) * $k) (($oy + $tz + $sideH) * $k)
    # 三个面：顶面最亮、左面中等、右面最暗（原版红色立方体的明暗关系，换成橙色）
    $topG = New-Object System.Windows.Media.LinearGradientBrush((C '#FFB451'), (C '#FF8C1C'), 90)
    $leftG = New-Object System.Windows.Media.LinearGradientBrush((C '#F7801A'), (C '#DE6206'), 90)
    $rightG = New-Object System.Windows.Media.LinearGradientBrush((C '#B05008'), (C '#8E3D02'), 90)
    $dc.DrawGeometry($leftG, $null, (Poly @($lt, $front, $fb, $lb)))
    $dc.DrawGeometry($rightG, $null, (Poly @($front, $rt, $rb, $fb)))
    $dc.DrawGeometry($topG, $null, (Poly @($apex, $rt, $front, $lt)))
    # 棱线高光：前竖棱、顶面两条前棱用浅色细线，立体感更强
    if (-not $small) {
        $hl = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(150, 255, 214, 160))), (2.2 * $k))
        $dc.DrawLine($hl, (P ($front.X) ($front.Y + 3 * $k)), (P ($fb.X) ($fb.Y - 4 * $k)))
        $dc.DrawLine($hl, (P ($lt.X + 5 * $k) ($lt.Y + 1.5 * $k)), (P ($front.X - 2 * $k) ($front.Y - 0.5 * $k)))
        $hl2 = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(90, 255, 230, 190))), (2.0 * $k))
        $dc.DrawLine($hl2, (P ($front.X + 2 * $k) ($front.Y - 0.5 * $k)), (P ($rt.X - 5 * $k) ($rt.Y + 1.5 * $k)))
    }
    # 外轮廓：深棕色粗描边
    $outline = New-Object System.Windows.Media.Pen((B '#3A1500'), [Math]::Max(0.9, 7.0 * $k))
    $outline.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $dc.DrawGeometry($null, $outline, (Poly @($apex, $rt, $rb, $fb, $lb, $lt)))

    # 面上的字：左面 S、右面 W，随面倾斜，几乎占满各自的面
    $slope = ($tz / 2) / $half
    $white = [System.Windows.Media.Brushes]::White
    $shadow = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(110, 80, 25, 0))
    $sx0 = $ox + $half * 0.20; $sw0 = $half * 0.60
    $sy0 = $oy + $tz / 2 + $sideH * 0.12 + ($sx0 - $ox) * $slope; $sh0 = $sideH * 0.64
    $gS = GlyphOnFace 'S' 'Arial' ([System.Windows.FontWeights]::Bold) ($sx0 * $k) ($sy0 * $k) ($sw0 * $k) ($sh0 * $k) $slope
    $wx0 = $ox + $half * 1.12; $ww0 = $half * 0.76
    $wy0 = $oy + $tz - ($wx0 - $ox - $half) * $slope + $sideH * 0.10; $wh0 = $sideH * 0.60
    $gW = GlyphOnFace 'W' 'Arial' ([System.Windows.FontWeights]::Bold) ($wx0 * $k) ($wy0 * $k) ($ww0 * $k) ($wh0 * $k) (-$slope)
    if (-not $small) {
        # 字的投影（往右下错开一点），像原版那样有一点浮雕感
        foreach ($g in @($gS, $gW)) {
            $sh = $g.Clone(); $tg = New-Object System.Windows.Media.TransformGroup
            $tg.Children.Add($g.Transform); $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform((2.5 * $k), (3 * $k))))
            $sh.Transform = $tg
            $dc.DrawGeometry($shadow, $null, $sh)
        }
    }
    $dc.DrawGeometry($white, $null, $gS)
    $dc.DrawGeometry($white, $null, $gW)

    # 右下角 "tool"：白字 + 粗黑边，压在立方体底部（原版 "2025" 的样式和大小）
    if (-not $small) {
        $face = New-Object System.Windows.Media.Typeface((New-Object System.Windows.Media.FontFamily('Arial Black')), [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::Black, [System.Windows.FontStretches]::Normal)
        $ft = New-Object System.Windows.Media.FormattedText('tool', [Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $face, 100, $white, 1.0)
        $g = $ft.BuildGeometry((P 0 0))
        $b = $g.Bounds
        $th = 54 * $k; $tw = $th * $b.Width / $b.Height * 0.98
        $tx = (248 * $k) - $tw; $ty = (246 * $k) - $th
        $tg = New-Object System.Windows.Media.TransformGroup
        $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform(-$b.X, -$b.Y)))
        $tg.Children.Add((New-Object System.Windows.Media.ScaleTransform(($tw / $b.Width), ($th / $b.Height))))
        $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform($tx, $ty)))
        $pg = $g.GetFlattenedPathGeometry(); $pg.Transform = $tg
        $stroke = New-Object System.Windows.Media.Pen((B '#0E0E0E'), [Math]::Max(1.6, 15 * $k))
        $stroke.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        $dc.DrawGeometry($null, $stroke, $pg)
        $dc.DrawGeometry($white, $null, $pg)
    }
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    return $bmp
}

function ToPng($bmp) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

# 传统 ICO 帧（BITMAPINFOHEADER + 32 位 BGRA 自下而上 + 全零 AND 掩码）：所有读图标的程序都认
function ToDib($bmp) {
    $n = $bmp.PixelWidth
    $px = New-Object byte[] ($n * $n * 4)
    $bmp.CopyPixels($px, $n * 4, 0)
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter($ms)
    $w.Write([UInt32]40); $w.Write([Int32]$n); $w.Write([Int32]($n * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $i = ($y * $n + $x) * 4
            $a = $px[$i + 3]
            if ($a -gt 0 -and $a -lt 255) {
                $w.Write([byte][Math]::Min(255, [Math]::Round($px[$i] * 255.0 / $a))); $w.Write([byte][Math]::Min(255, [Math]::Round($px[$i + 1] * 255.0 / $a))); $w.Write([byte][Math]::Min(255, [Math]::Round($px[$i + 2] * 255.0 / $a)))
            } else { $w.Write($px[$i]); $w.Write($px[$i + 1]); $w.Write($px[$i + 2]) }
            $w.Write($a)
        }
    }
    $maskRow = [Math]::Ceiling($n / 32.0) * 4
    $w.Write((New-Object byte[] ($maskRow * $n)))
    $w.Flush()
    return ,$ms.ToArray()
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$pngs = @{}; $frames = @{}
# 注意不能写成 $x = if (...) { 数组 }：PowerShell 会把字节数组拆开
foreach ($z in $sizes) {
    $bmpZ = Render $z
    $pngs[$z] = ToPng $bmpZ
    if ($z -ge 256) { $frames[$z] = $pngs[$z] } else { $frames[$z] = ToDib $bmpZ }
}
[IO.File]::WriteAllBytes($outPng, $pngs[256])

# ICO：256 用 PNG 帧，其余用传统位图帧
$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter($ms)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($z in $sizes) {
    $d = $frames[$z]
    $w.Write([byte]($(if ($z -ge 256) { 0 } else { $z }))); $w.Write([byte]($(if ($z -ge 256) { 0 } else { $z })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$d.Length); $w.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($z in $sizes) { $w.Write($frames[$z]) }
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
