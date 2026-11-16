# SolidWorks 宏 API 自检脚本
# 用途：在本机 SolidWorks（2023 及以上）上逐项验证 sw-api-cheatsheet.md 里的 API 能否正常工作。
# 脚本会另外启动一个隐藏的 SolidWorks 来测试，不影响你正在用的 SolidWorks，测完自动关闭。
# 用法：在 PowerShell 中运行：
#   powershell -ExecutionPolicy Bypass -File .\sw-api-selftest.ps1
# 脚本只会新建临时零件，测完不保存直接关闭，不会修改已打开的文件；会临时关闭"输入尺寸值"选项，结束时恢复原值。

$ErrorActionPreference = 'Stop'
$BF = [Reflection.BindingFlags]
function Call($o, $n, [object[]]$a = @()) { [System.__ComObject].InvokeMember($n, $BF::InvokeMethod, $null, $o, $a) }
function GetP($o, $n) { [System.__ComObject].InvokeMember($n, $BF::GetProperty, $null, $o, @()) }
function SetP($o, $n, $v) { [void][System.__ComObject].InvokeMember($n, $BF::SetProperty, $null, $o, @(, $v)) }

$results = New-Object System.Collections.ArrayList
function Report($name, [bool]$ok, $detail) {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    [void]$results.Add([pscustomobject]@{ Test = $name; Result = $tag; Detail = $detail })
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $name, $detail)
}
function Near($a, $b, $tol = 1e-7) { [math]::Abs([double]$a - [double]$b) -le $tol }

. "$PSScriptRoot\_isolated-sw.ps1"
$iso = Start-IsolatedSw
$sw = $iso.App
Write-Host ("SolidWorks 版本号: " + (Call $sw 'RevisionNumber'))

$created = New-Object System.Collections.ArrayList
$PI = [math]::PI

function NewPart {
    # 默认模板可能是 "~BLANK_PART_TEMPLATE.prtdot" 这种占位名，实测不稳定，所以优先用模板文件夹里真实的 .prtdot
    $tmpl = Call $sw 'GetUserPreferenceStringValue' @(8)          # swDefaultTemplatePart
    $m = $null
    if ($tmpl -and -not $tmpl.StartsWith('~') -and (Test-Path $tmpl)) { $m = Call $sw 'NewDocument' @($tmpl, 0, 0.0, 0.0) }
    if ($null -eq $m) {
        $dir = (Call $sw 'GetUserPreferenceStringValue' @(6)).Split(';')[0]   # swFileLocationsDocumentTemplates
        $f = Get-ChildItem $dir -Filter *.prtdot | Select-Object -First 1
        $m = Call $sw 'NewDocument' @($f.FullName, 0, 0.0, 0.0)
    }
    [void]$created.Add((Call $m 'GetTitle'))
    return $m
}
function GetPlane($m, [int]$idx) {   # 1=前视 2=上视 3=右视（按特征树顺序，与界面语言无关）
    $f = Call $m 'FirstFeature'; $n = 0
    while ($null -ne $f) {
        if ((Call $f 'GetTypeName2') -eq 'RefPlane') { $n++; if ($n -eq $idx) { return $f } }
        $f = Call $f 'GetNextFeature'
    }
    return $null
}
function StartSketchOn($m, $planeFeat) {
    [void](Call $m 'ClearSelection2' @($true))
    [void](Call $planeFeat 'Select2' @($false, 0))
    $sm = GetP $m 'SketchManager'
    SetP $sm 'AddToDB' $true
    [void](Call $sm 'InsertSketch' @($true))
    return $sm
}
function EndSketch($m) {
    $sm = GetP $m 'SketchManager'
    SetP $sm 'AddToDB' $false
    [void](Call $sm 'InsertSketch' @($true))
    $sk = Call $m 'FeatureByPositionReverse' @(0)
    [void](Call $m 'ClearSelection2' @($true))
    [void](Call $sk 'Select2' @($false, 0))
    return $sk
}
function Extrude($m, [double]$d, [int]$endCond = 0, [bool]$merge = $true) {
    $fm = GetP $m 'FeatureManager'
    Call $fm 'FeatureExtrusion3' @($true, $false, $false, $endCond, 0, $d, $d, $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $merge, $true, $true, 0, 0.0, $false)
}
function CutThroughBoth($m) {   # 两个方向都完全贯穿：不用关心草图在实体哪一侧
    $fm = GetP $m 'FeatureManager'
    Call $fm 'FeatureCut4' @($false, $false, $false, 1, 1, 0.01, 0.01, $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $false, $true, $true, $true, $true, $false, 0, 0.0, $false, $false)
}
function Vol($m) { $mp = Call (GetP $m 'Extension') 'CreateMassProperty'; [double](GetP $mp 'Volume') }
function Boxes($m) {
    $bodies = Call $m 'GetBodies2' @(0, $false)
    $out = @()
    foreach ($b in $bodies) { $bb = Call $b 'GetBodyBox'; $out += , (@($bb) | ForEach-Object { [math]::Round([double]$_ * 1000, 3) }) }
    return , $out
}
function Sel($m, [string]$type, [double]$x, [double]$y, [double]$z, [bool]$append = $false, [int]$mark = 0) {
    # VBA 里 Callout 参数直接传 Nothing；PowerShell 里要包一层 DispatchWrapper，否则类型不匹配
    $ext = GetP $m 'Extension'
    $nothing = [System.Runtime.InteropServices.DispatchWrapper]::new([object]$null)
    return [bool](Call $ext 'SelectByID2' @('', $type, $x, $y, $z, $append, $mark, $nothing, 0))
}
# 按几何距离选面/边：遍历实体上的所有面或边，选离给定点最近的那个。与视角无关。
function SelNearest($m, [string]$kind, [double]$x, [double]$y, [double]$z, [bool]$append = $false) {
    $bodies = Call $m 'GetBodies2' @(0, $false)
    $best = $null; $bd = [double]::MaxValue
    foreach ($b in $bodies) {
        $ents = if ($kind -eq 'EDGE') { Call $b 'GetEdges' } else { Call $b 'GetFaces' }
        foreach ($e in $ents) {
            $p = @(Call $e 'GetClosestPointOn' @($x, $y, $z))
            $d = [math]::Sqrt([math]::Pow($p[0] - $x, 2) + [math]::Pow($p[1] - $y, 2) + [math]::Pow($p[2] - $z, 2))
            if ($d -lt $bd) { $bd = $d; $best = $e }
        }
    }
    if ($null -eq $best) { return $false }
    $nothing = [System.Runtime.InteropServices.DispatchWrapper]::new([object]$null)
    return [bool](Call $best 'Select4' @($append, $nothing))
}

$dimToggleOld = Call $sw 'GetUserPreferenceToggle' @(10)   # swInputDimValOnCreate

try {
    # ---------- T1 新建零件 + 基准面 ----------
    $m = NewPart
    $names = @(1, 2, 3) | ForEach-Object { GetP (GetPlane $m $_) 'Name' }
    Report 'T1 新建零件/按顺序取基准面' ($null -ne $m -and $names.Count -eq 3) ("基准面名称: " + ($names -join ', '))

    # ---------- T2 前视面中心矩形 + 拉伸 ----------
    $sm = StartSketchOn $m (GetPlane $m 1)
    [void](Call $sm 'CreateCenterRectangle' @(0.0, 0.0, 0.0, 0.05, 0.03, 0.0))
    [void](EndSketch $m)
    $f = Extrude $m 0.01
    SetP $f 'Name' '底板'
    $v = Vol $m; $bx = (Boxes $m)[0]
    Report 'T2 中心矩形+拉伸凸台 FeatureExtrusion3' ($null -ne $f -and (Near $v 6e-5 1e-9)) ("体积=$v 期望 6e-5；包围盒(mm)=" + ($bx -join ','))
    Report 'T2b 特征改名' ((GetP $f 'Name') -eq '底板') ("名称=" + (GetP $f 'Name'))

    # ---------- T3 圆 + 完全贯穿两侧切除 ----------
    $sm = StartSketchOn $m (GetPlane $m 1)
    [void](Call $sm 'CreateCircleByRadius' @(0.02, 0.0, 0.0, 0.005))
    [void](EndSketch $m)
    $f = CutThroughBoth $m
    $v2 = Vol $m; $exp = 6e-5 - $PI * 0.005 * 0.005 * 0.01
    Report 'T3 圆孔 FeatureCut4 双向完全贯穿(Sd=False,T1=T2=1)' ($null -ne $f -and (Near $v2 $exp 1e-9)) ("体积=$v2 期望 $exp")

    # ---------- T3b 等距面上画圆 + 给定深度切除（检查切除默认方向） ----------
    [void](Call $m 'ClearSelection2' @($true))
    [void](Call (GetPlane $m 1) 'Select2' @($false, 0))
    [void](Call (GetP $m 'FeatureManager') 'InsertRefPlane' @(8, 0.01, 0, 0.0, 0, 0.0))
    $topPlane = Call $m 'FeatureByPositionReverse' @(0)
    $sm = StartSketchOn $m $topPlane
    [void](Call $sm 'CreateCircleByRadius' @(-0.02, 0.0, 0.0, 0.003))
    [void](EndSketch $m)
    $vBefore = Vol $m
    $fm0 = GetP $m 'FeatureManager'
    $f = Call $fm0 'FeatureCut4' @($true, $false, $false, 0, 0, 0.004, 0.004, $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $false, $true, $true, $true, $true, $false, 0, 0.0, $false, $false)
    $exp = $PI * 0.003 * 0.003 * 0.004
    $rem = $vBefore - (Vol $m)
    Report 'T3b 给定深度切除默认方向(沿基准面法向反方向)' ($null -ne $f -and (Near $rem $exp 1e-10)) ("从 Z=10 的等距面向下切 4mm：去除体积=$rem 期望 $exp")

    # ---------- T4 圆角（预选边线 + FeatureFillet3） ----------
    $okRay = Sel $m 'EDGE' 0.05 0.03 0.005
    [void](Call $m 'ClearSelection2' @($true))
    $ok = SelNearest $m 'EDGE' 0.05 0.03 0.005
    $fm = GetP $m 'FeatureManager'
    $vBefore = Vol $m
    $f = Call $fm 'FeatureFillet3' @(195, 0.002, 0.0, 0.0, 0, 0, 0, $null, $null, $null, $null, $null, $null, $null)
    $vAfter = Vol $m; $exp = (1 - $PI / 4) * 0.002 * 0.002 * 0.01
    Report 'T4 圆角 FeatureFillet3(按最近距离预选边线)' ($ok -and $null -ne $f -and (Near ($vBefore - $vAfter) $exp 1e-10)) ("选中=$ok 去除体积=$($vBefore-$vAfter) 期望 $exp（对照：SelectByID2 坐标选边返回 $okRay，但实测会按视角选到别的边）")

    # ---------- T5 倒角 InsertFeatureChamfer ----------
    $ok = SelNearest $m 'EDGE' -0.05 0.03 0.005
    $vBefore = Vol $m
    $f = Call $fm 'InsertFeatureChamfer' @(4, 1, 0.001, ($PI / 4), 0.0, 0.0, 0.0, 0.0)
    $vAfter = Vol $m; $exp = 0.5 * 0.001 * 0.001 * 0.01
    Report 'T5 倒角 InsertFeatureChamfer(角度-距离)' ($ok -and $null -ne $f -and (Near ($vBefore - $vAfter) $exp 1e-10)) ("选中=$ok 去除体积=$($vBefore-$vAfter) 期望 $exp")

    # ---------- T6 抽壳 ----------
    $m2 = NewPart
    $sm = StartSketchOn $m2 (GetPlane $m2 1)
    [void](Call $sm 'CreateCenterRectangle' @(0.0, 0.0, 0.0, 0.05, 0.03, 0.0))
    [void](EndSketch $m2)
    [void](Extrude $m2 0.04)
    $ok = SelNearest $m2 'FACE' 0.0 0.0 0.04
    $f = Call $m2 'InsertFeatureShell' @(0.002, $false)
    $v = Vol $m2; $exp = 0.1 * 0.06 * 0.04 - 0.096 * 0.056 * 0.038
    Report 'T6 抽壳 InsertFeatureShell(预选开口面)' ($ok -and (Near $v $exp 1e-9)) ("选中=$ok 体积=$v 期望 $exp")

    # ---------- T7 等距基准面 + 在其上画草图 ----------
    $m3 = NewPart
    [void](Call $m3 'ClearSelection2' @($true))
    [void](Call (GetPlane $m3 2) 'Select2' @($false, 0))
    $fm3 = GetP $m3 'FeatureManager'
    $rp = Call $fm3 'InsertRefPlane' @(8, 0.02, 0, 0.0, 0, 0.0)
    $rpFeat = Call $m3 'FeatureByPositionReverse' @(0)
    $sm = StartSketchOn $m3 $rpFeat
    [void](Call $sm 'CreateCircleByRadius' @(0.0, 0.0, 0.0, 0.01))
    [void](EndSketch $m3)
    [void](Extrude $m3 0.005)
    $bx = (Boxes $m3)[0]
    Report 'T7 等距基准面 InsertRefPlane(8=距离)' ($null -ne $rp -and (Near $bx[1] 20 0.01)) ("新基准面类型=" + (Call $rpFeat 'GetTypeName2') + "；圆柱包围盒(mm)=" + ($bx -join ','))

    # ---------- T7b 反向等距基准面 + 坐标映射 ----------
    [void](Call $m3 'ClearSelection2' @($true))
    [void](Call (GetPlane $m3 2) 'Select2' @($false, 0))
    [void](Call $fm3 'InsertRefPlane' @((8 + 256), 0.02, 0, 0.0, 0, 0.0))
    $rpFeat2 = Call $m3 'FeatureByPositionReverse' @(0)
    $sm = StartSketchOn $m3 $rpFeat2
    [void](Call $sm 'CreateCornerRectangle' @(0.01, 0.02, 0.0, 0.03, 0.05, 0.0))
    [void](EndSketch $m3)
    [void](Extrude $m3 0.005 0 $false)
    $all = Boxes $m3
    Report 'T7b 反向等距面(8+256) 上画矩形(10,20)-(30,50) 拉伸5' ($all.Count -eq 2) ("全部实体包围盒(mm): " + (($all | ForEach-Object { '[' + ($_ -join ',') + ']' }) -join ' '))

    # ---------- T2c/T2d 两侧对称拉伸、反向拉伸 ----------
    $m9 = NewPart
    $sm = StartSketchOn $m9 (GetPlane $m9 1)
    [void](Call $sm 'CreateCornerRectangle' @(0.0, 0.0, 0.0, 0.02, 0.02, 0.0))
    [void](EndSketch $m9)
    $fm9 = GetP $m9 'FeatureManager'
    [void](Call $fm9 'FeatureExtrusion3' @($true, $false, $false, 6, 0, 0.01, 0.01, $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $false, $true, $true, 0, 0.0, $false))
    $sm = StartSketchOn $m9 (GetPlane $m9 1)
    [void](Call $sm 'CreateCornerRectangle' @(0.05, 0.0, 0.0, 0.07, 0.02, 0.0))
    [void](EndSketch $m9)
    [void](Call $fm9 'FeatureExtrusion3' @($true, $false, $true, 0, 0, 0.01, 0.01, $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $false, $true, $true, 0, 0.0, $false))
    $all = Boxes $m9
    Report 'T2c/T2d 两侧对称(T1=6) 与 反向(Dir=True) 拉伸' ($all.Count -eq 2) ("包围盒(mm): " + (($all | ForEach-Object { '[' + ($_ -join ',') + ']' }) -join ' ') + "（期望一个 Z=-5..5，一个 Z=-10..0）")

    # ---------- T8 旋转（草图内只有一条中心线） ----------
    $m4 = NewPart
    $sm = StartSketchOn $m4 (GetPlane $m4 1)
    [void](Call $sm 'CreateCenterLine' @(0.0, -0.05, 0.0, 0.0, 0.05, 0.0))
    [void](Call $sm 'CreateCornerRectangle' @(0.005, 0.0, 0.0, 0.02, 0.03, 0.0))
    [void](EndSketch $m4)
    $fm4 = GetP $m4 'FeatureManager'
    $f = Call $fm4 'FeatureRevolve2' @($true, $true, $false, $false, $false, $false, 0, 0, (2 * $PI), 0.0, $false, $false, 0.0, 0.0, 0, 0.0, 0.0, $true, $true, $true)
    $v = 0.0; if ($null -ne $f) { $v = Vol $m4 }
    $exp = $PI * (0.02 * 0.02 - 0.005 * 0.005) * 0.03
    Report 'T8 旋转 FeatureRevolve2(自动识别唯一中心线)' ($null -ne $f -and (Near $v $exp 1e-9)) ("体积=$v 期望 $exp")

    # ---------- T9 草图坐标与模型坐标的对应关系 ----------
    $m5 = NewPart
    $sm = StartSketchOn $m5 (GetPlane $m5 2)
    [void](Call $sm 'CreateCornerRectangle' @(0.01, 0.02, 0.0, 0.03, 0.05, 0.0))
    [void](EndSketch $m5)
    [void](Extrude $m5 0.005 0 $false)
    $sm = StartSketchOn $m5 (GetPlane $m5 3)
    [void](Call $sm 'CreateCornerRectangle' @(0.01, 0.02, 0.0, 0.03, 0.05, 0.0))
    [void](EndSketch $m5)
    [void](Extrude $m5 0.005 0 $false)
    $all = Boxes $m5
    Report 'T9 上视/右视草图坐标映射' ($all.Count -eq 2) ("草图矩形(10,20)-(30,50)mm 拉伸5 → 上视体 & 右视体包围盒[xmin,ymin,zmin,xmax,ymax,zmax](mm): " + (($all | ForEach-Object { '[' + ($_ -join ',') + ']' }) -join ' '))

    # ---------- T10 直线 + 圆弧方向 ----------
    $m6 = NewPart
    $sm = StartSketchOn $m6 (GetPlane $m6 1)
    [void](Call $sm 'CreateLine' @(0.0, 0.0, 0.0, 0.02, 0.0, 0.0))
    [void](Call $sm 'CreateArc' @(0.02, 0.01, 0.0, 0.02, 0.0, 0.0, 0.02, 0.02, 0.0, [int16]1))
    [void](Call $sm 'CreateLine' @(0.02, 0.02, 0.0, 0.0, 0.02, 0.0))
    [void](Call $sm 'CreateLine' @(0.0, 0.02, 0.0, 0.0, 0.0, 0.0))
    [void](EndSketch $m6)
    $f = Extrude $m6 0.01
    $v = 0.0; if ($null -ne $f) { $v = Vol $m6 }
    $out = (0.02 * 0.02 + $PI * 0.01 * 0.01 / 2) * 0.01
    $in = (0.02 * 0.02 - $PI * 0.01 * 0.01 / 2) * 0.01
    $dir = if (Near $v $out 1e-9) { 'Direction=1 为逆时针(从起点绕圆心逆时针到终点)' } elseif (Near $v $in 1e-9) { 'Direction=1 为顺时针' } else { '无法判断' }
    Report 'T10 直线+圆弧 CreateArc 方向' ($null -ne $f -and ((Near $v $out 1e-9) -or (Near $v $in 1e-9))) ("体积=$v；$dir")

    # ---------- T11 多边形 ----------
    $m7 = NewPart
    $sm = StartSketchOn $m7 (GetPlane $m7 1)
    [void](Call $sm 'CreatePolygon' @(0.0, 0.0, 0.0, 0.01, 0.0, 0.0, 6, $true))
    [void](EndSketch $m7)
    $f = Extrude $m7 0.01
    $v = 0.0; if ($null -ne $f) { $v = Vol $m7 }
    $insc = 2 * [math]::Sqrt(3) * 0.01 * 0.01 * 0.01         # 内切圆半径 10：对边 20
    $circ = 1.5 * [math]::Sqrt(3) * 0.01 * 0.01 * 0.01       # 外接圆半径 10：对角 20
    $mean = if (Near $v $insc 1e-9) { 'Inscribed=True: 给定点到中心距离=内切圆半径(对边距/2)' } elseif (Near $v $circ 1e-9) { 'Inscribed=True: 给定点到中心距离=外接圆半径(对角距/2)' } else { '无法判断' }
    Report 'T11 正六边形 CreatePolygon(Inscribed=True)' ($null -ne $f) ("体积=$v；$mean")
    $m7b = NewPart
    $sm = StartSketchOn $m7b (GetPlane $m7b 1)
    [void](Call $sm 'CreatePolygon' @(0.0, 0.0, 0.0, 0.01, 0.0, 0.0, 6, $false))
    [void](EndSketch $m7b)
    $f = Extrude $m7b 0.01
    $v = 0.0; if ($null -ne $f) { $v = Vol $m7b }
    $mean = if (Near $v $insc 1e-9) { 'Inscribed=False: 给定点到中心距离=内切圆半径(对边距/2)' } elseif (Near $v $circ 1e-9) { 'Inscribed=False: 给定点到中心距离=外接圆半径(对角距/2)' } else { '无法判断' }
    Report 'T11b 正六边形 CreatePolygon(Inscribed=False)' ($null -ne $f) ("体积=$v；$mean")

    # ---------- T12 尺寸标注并改值 ----------
    [void](Call $sw 'SetUserPreferenceToggle' @(10, $false))
    $m8 = NewPart
    $sm = StartSketchOn $m8 (GetPlane $m8 1)
    $seg = Call $sm 'CreateLine' @(0.0, 0.0, 0.0, 0.02, 0.0, 0.0)
    [void](Call $m8 'ClearSelection2' @($true))
    [void](Call $seg 'Select4' @($false, [System.Runtime.InteropServices.DispatchWrapper]::new([object]$null)))
    $dd = Call $m8 'AddDimension2' @(0.01, -0.01, 0.0)
    $len = $null
    if ($null -ne $dd) {
        $dim = Call $dd 'GetDimension2' @(0)
        SetP $dim 'SystemValue' 0.035
        [void](Call $m8 'EditRebuild3')
        $len = [double](GetP $dim 'SystemValue')
    }
    SetP $sm 'AddToDB' $false
    [void](Call $sm 'InsertSketch' @($true))
    Report 'T12 AddDimension2 + Dimension.SystemValue' ($null -ne $dd -and (Near $len 0.035 1e-9)) ("尺寸值=$len 期望 0.035")
}
catch {
    Report '异常中断' $false $_.Exception.Message
}
finally {
    [void](Call $sw 'SetUserPreferenceToggle' @(10, [bool]$dimToggleOld))
    foreach ($t in $created) { try { [void](Call $sw 'CloseDoc' @($t)) } catch {} }
    Write-Host ("已关闭临时零件: " + ($created -join ', '))
    $pass = @($results | Where-Object Result -eq 'PASS').Count
    Write-Host ("结果: {0}/{1} 通过" -f $pass, $results.Count)
    Stop-IsolatedSw $iso
}
