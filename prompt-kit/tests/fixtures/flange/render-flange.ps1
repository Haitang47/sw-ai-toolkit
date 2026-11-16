# 测试件：联轴法兰（真值：法兰 外径60 厚6；轮毂 外径25 高14；中心孔 直径8 贯穿；4×直径3.4 在 PCD46 上；
# 径向 M3 顶丝底孔 直径2.5，高度 Y=13，只在 +Z 一侧；轮毂顶外缘倒角 C1）
$ErrorActionPreference = 'Stop'
$BF = [Reflection.BindingFlags]
function Call($o, $n, [object[]]$a = @()) { [System.__ComObject].InvokeMember($n, $BF::InvokeMethod, $null, $o, $a) }
function GetP($o, $n) { [System.__ComObject].InvokeMember($n, $BF::GetProperty, $null, $o, @()) }
function SetP($o, $n, $v) { [void][System.__ComObject].InvokeMember($n, $BF::SetProperty, $null, $o, @(, $v)) }
$NOTHING = [System.Runtime.InteropServices.DispatchWrapper]::new([object]$null)
$MM = 0.001
$out = $PSScriptRoot
New-Item -ItemType Directory -Force -Path $out | Out-Null
$sw = [System.Runtime.InteropServices.Marshal]::GetActiveObject('SldWorks.Application')
$m = Call $sw 'NewDocument' @('C:\ProgramData\SolidWorks\SOLIDWORKS 2025\templates\gb_part.prtdot', 0, 0.0, 0.0)
$title = Call $m 'GetTitle'
try {
    $sm = GetP $m 'SketchManager'; $fm = GetP $m 'FeatureManager'
    function Plane($i) { $f = Call $m 'FirstFeature'; $n = 0; while ($f) { if ((Call $f 'GetTypeName2') -eq 'RefPlane') { $n++; if ($n -eq $i) { return $f } }; $f = Call $f 'GetNextFeature' } }
    function Sk($p) { [void](Call $m 'ClearSelection2' @($true)); [void](Call $p 'Select2' @($false, 0)); SetP $sm 'AddToDB' $true; [void](Call $sm 'InsertSketch' @($true)) }
    function EndSk { SetP $sm 'AddToDB' $false; [void](Call $sm 'InsertSketch' @($true)); [void](Call $m 'ClearSelection2' @($true)); [void](Call (Call $m 'FeatureByPositionReverse' @(0)) 'Select2' @($false, 0)) }
    function L($x1, $y1, $x2, $y2) { [void](Call $sm 'CreateLine' @(($x1 * $MM), ($y1 * $MM), 0.0, ($x2 * $MM), ($y2 * $MM), 0.0)) }

    Sk (Plane 1)
    [void](Call $sm 'CreateCenterLine' @(0.0, -0.05, 0.0, 0.0, 0.05, 0.0))
    L 4 0 30 0; L 30 0 30 6; L 30 6 12.5 6; L 12.5 6 12.5 20; L 12.5 20 4 20; L 4 20 4 0
    EndSk
    $f = Call $fm 'FeatureRevolve2' @($true, $true, $false, $false, $false, $false, 0, 0, (2 * [math]::PI), 0.0, $false, $false, 0.0, 0.0, 0, 0.0, 0.0, $true, $true, $true)
    if (-not $f) { throw 'revolve failed' }

    Sk (Plane 2)
    foreach ($c in @(@(23, 0), @(-23, 0), @(0, 23), @(0, -23))) { [void](Call $sm 'CreateCircleByRadius' @(($c[0] * $MM), ($c[1] * $MM), 0.0, (1.7 * $MM))) }
    EndSk
    $f = Call $fm 'FeatureCut4' @($false, $false, $false, 1, 1, 0.01, 0.01, $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $false, $true, $true, $true, $true, $false, 0, 0.0, $false, $false)
    if (-not $f) { throw 'holes failed' }

    Sk (Plane 1)
    [void](Call $sm 'CreateCircleByRadius' @(0.0, (13 * $MM), 0.0, (1.25 * $MM)))
    EndSk
    $f = Call $fm 'FeatureCut4' @($true, $false, $true, 0, 0, (13 * $MM), (13 * $MM), $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $false, $true, $true, $true, $true, $false, 0, 0.0, $false, $false)
    if (-not $f) { throw 'set screw failed' }

    [void](Call $m 'ClearSelection2' @($true))
    $best = $null; $bd = 1e30
    foreach ($b in (Call $m 'GetBodies2' @(0, $false))) { foreach ($e in (Call $b 'GetEdges')) { $p = @(Call $e 'GetClosestPointOn' @((12.5 * $MM), (20 * $MM), 0.0)); $d = [math]::Pow($p[0] - 12.5 * $MM, 2) + [math]::Pow($p[1] - 20 * $MM, 2) + [math]::Pow($p[2], 2); if ($d -lt $bd) { $bd = $d; $best = $e } } }
    [void](Call $best 'Select4' @($false, $NOTHING))
    $f = Call $fm 'InsertFeatureChamfer' @(4, 1, (1 * $MM), ([math]::PI / 4), 0.0, 0.0, 0.0, 0.0)
    if (-not $f) { throw 'chamfer failed' }

    $v = [double](GetP (Call (GetP $m 'Extension') 'CreateMassProperty') 'Volume') * 1e9
    Write-Host ("法兰体积 = {0:N1} mm3" -f $v)
    foreach ($view in @(@(7, 'iso'), @(5, 'top'), @(1, 'front'))) {
        [void](Call $m 'ShowNamedView2' @('', $view[0])); [void](Call $m 'ViewZoomtofit2')
        [void](Call $m 'SaveAs3' @([string]("$out\" + $view[1] + '.jpg'), 0, 2))
    }
}
finally { [void](Call $sw 'CloseDoc' @($title)); Write-Host "closed $title" }
