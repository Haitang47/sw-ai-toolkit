# 按与 sw-api-cheatsheet.md 中示例宏完全相同的调用顺序，在 SolidWorks 中建出示例零件并核对体积。
# 同时检查 ShellIn 依赖的抽壳特征类型名 "Shell"。
# 用法：powershell -ExecutionPolicy Bypass -File .\example-macro-check.ps1

$ErrorActionPreference = 'Stop'
$BF = [Reflection.BindingFlags]
function Call($o, $n, [object[]]$a = @()) { [System.__ComObject].InvokeMember($n, $BF::InvokeMethod, $null, $o, $a) }
function GetP($o, $n) { [System.__ComObject].InvokeMember($n, $BF::GetProperty, $null, $o, @()) }
function SetP($o, $n, $v) { [void][System.__ComObject].InvokeMember($n, $BF::SetProperty, $null, $o, @(, $v)) }
$NOTHING = [System.Runtime.InteropServices.DispatchWrapper]::new([object]$null)
$MM = 0.001; $DEG = [math]::PI / 180

# 另外启动一个隐藏的 SolidWorks 来测试，不影响正在用的 SolidWorks
. "$PSScriptRoot\_isolated-sw.ps1"
$iso = Start-IsolatedSw
$swApp = $iso.App
$titles = New-Object System.Collections.ArrayList

function NewPart {
    $tmpl = Call $swApp 'GetUserPreferenceStringValue' @(8)
    if ($tmpl -and -not $tmpl.StartsWith('~')) { $m = Call $swApp 'NewDocument' @($tmpl, 0, 0.0, 0.0); if ($m) { return $m } }
    $folder = (Call $swApp 'GetUserPreferenceStringValue' @(6)).Split(';')[0]
    if (-not $folder.EndsWith('\')) { $folder += '\' }
    $f = Get-ChildItem ($folder + '*.prtdot') | Select-Object -First 1
    return (Call $swApp 'NewDocument' @($f.FullName, 0, 0.0, 0.0))
}
function GetPlane([int]$idx) { $f = Call $script:swModel 'FirstFeature'; $n = 0; while ($f) { if ((Call $f 'GetTypeName2') -eq 'RefPlane') { $n++; if ($n -eq $idx) { return $f } }; $f = Call $f 'GetNextFeature' } }
function OffsetPlane($base, [double]$dist, [bool]$flip = $false) {
    $opt = 8; if ($flip) { $opt = 8 + 256 }
    [void](Call $script:swModel 'ClearSelection2' @($true)); [void](Call $base 'Select2' @($false, 0))
    $rp = Call $script:swFeatMgr 'InsertRefPlane' @($opt, $dist, 0, 0.0, 0, 0.0)
    if ($null -eq $rp) { return $null }
    return (Call $script:swModel 'FeatureByPositionReverse' @(0))
}
function StartSketch($plane) { [void](Call $script:swModel 'ClearSelection2' @($true)); [void](Call $plane 'Select2' @($false, 0)); SetP $script:swSketchMgr 'AddToDB' $true; [void](Call $script:swSketchMgr 'InsertSketch' @($true)) }
function EndSketch { SetP $script:swSketchMgr 'AddToDB' $false; [void](Call $script:swSketchMgr 'InsertSketch' @($true)); [void](Call $script:swModel 'ClearSelection2' @($true)); [void](Call (Call $script:swModel 'FeatureByPositionReverse' @(0)) 'Select2' @($false, 0)) }
function ExtrudeBoss([double]$depth, [bool]$reverse = $false, [bool]$midPlane = $false) { $ec = 0; if ($midPlane) { $ec = 6 }; Call $script:swFeatMgr 'FeatureExtrusion3' @($true, $false, $reverse, $ec, 0, $depth, $depth, $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $true, $true, $true, 0, 0.0, $false) }
function CutBlind([double]$depth, [bool]$reverse = $false) { Call $script:swFeatMgr 'FeatureCut4' @($true, $false, $reverse, 0, 0, $depth, $depth, $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $false, $true, $true, $true, $true, $false, 0, 0.0, $false, $false) }
function CutThroughAll { Call $script:swFeatMgr 'FeatureCut4' @($false, $false, $false, 1, 1, 0.01, 0.01, $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $false, $true, $true, $true, $true, $false, 0, 0.0, $false, $false) }
function SelectNearest([string]$kind, [double]$x, [double]$y, [double]$z, [bool]$append = $false) {
    if (-not $append) { [void](Call $script:swModel 'ClearSelection2' @($true)) }
    $best = $null; $bd = 1e30
    foreach ($b in (Call $script:swModel 'GetBodies2' @(0, $false))) {
        $ents = if ($kind -eq 'EDGE') { Call $b 'GetEdges' } else { Call $b 'GetFaces' }
        foreach ($e in $ents) { $p = @(Call $e 'GetClosestPointOn' @($x, $y, $z)); $d = [math]::Pow($p[0] - $x, 2) + [math]::Pow($p[1] - $y, 2) + [math]::Pow($p[2] - $z, 2); if ($d -lt $bd) { $bd = $d; $best = $e } }
    }
    if ($best) { return [bool](Call $best 'Select4' @($append, $NOTHING)) }; return $false
}
function Fillet([double]$r) { Call $script:swFeatMgr 'FeatureFillet3' @(195, $r, 0.0, 0.0, 0, 0, 0, $null, $null, $null, $null, $null, $null, $null) }
function Chamfer([double]$dist, [double]$angleDeg = 45) { Call $script:swFeatMgr 'InsertFeatureChamfer' @(4, 1, $dist, ($angleDeg * $DEG), 0.0, 0.0, 0.0, 0.0) }
function Check($feat, $stepNo, $name) { if ($null -eq $feat) { Write-Host "[FAIL] 步骤 $stepNo $name"; return $false }; SetP $feat 'Name' $name; Write-Host "[ OK ] 步骤 $stepNo $name"; return $true }
function Vol { [double](GetP (Call (GetP $script:swModel 'Extension') 'CreateMassProperty') 'Volume') * 1e9 }

try {
    # ===== 与示例宏 main 一一对应 =====
    $script:swModel = NewPart; [void]$titles.Add((Call $swModel 'GetTitle'))
    $script:swSketchMgr = GetP $swModel 'SketchManager'; $script:swFeatMgr = GetP $swModel 'FeatureManager'
    $ok = $true

    StartSketch (GetPlane 2)
    [void](Call $swSketchMgr 'CreateCenterRectangle' @(0.0, 0.0, 0.0, (40 * $MM), (25 * $MM), 0.0)); EndSketch
    $ok = $ok -and (Check (ExtrudeBoss (8 * $MM)) 1 '底板')

    StartSketch (OffsetPlane (GetPlane 2) (8 * $MM))
    [void](Call $swSketchMgr 'CreateCircleByRadius' @(0.0, 0.0, 0.0, (15 * $MM))); EndSketch
    $ok = $ok -and (Check (ExtrudeBoss (12 * $MM)) 2 '轴承凸台')

    StartSketch (OffsetPlane (GetPlane 2) (20 * $MM))
    [void](Call $swSketchMgr 'CreateCircleByRadius' @(0.0, 0.0, 0.0, (11 * $MM))); EndSketch
    $ok = $ok -and (Check (CutBlind (7 * $MM)) 3 '608轴承孔')

    StartSketch (GetPlane 2)
    [void](Call $swSketchMgr 'CreateCircleByRadius' @(0.0, 0.0, 0.0, (5 * $MM)))
    for ($i = 0; $i -le 3; $i++) {
        $cx = if ($i % 2 -eq 0) { 30 } else { -30 }; $cy = if ($i -lt 2) { 17.5 } else { -17.5 }
        [void](Call $swSketchMgr 'CreateCircleByRadius' @(($cx * $MM), ($cy * $MM), 0.0, (2.25 * $MM)))
    }
    EndSketch
    $ok = $ok -and (Check (CutThroughAll) 4 '通孔')

    [void](SelectNearest 'EDGE' (40 * $MM) (4 * $MM) (25 * $MM))
    [void](SelectNearest 'EDGE' (-40 * $MM) (4 * $MM) (25 * $MM) $true)
    [void](SelectNearest 'EDGE' (40 * $MM) (4 * $MM) (-25 * $MM) $true)
    [void](SelectNearest 'EDGE' (-40 * $MM) (4 * $MM) (-25 * $MM) $true)
    $ok = $ok -and (Check (Fillet (5 * $MM)) 5 '底板圆角R5')

    [void](SelectNearest 'EDGE' (15 * $MM) (20 * $MM) 0.0)
    $ok = $ok -and (Check (Chamfer (1 * $MM)) 6 '凸台倒角C1')

    $v = Vol
    $pi = [math]::PI
    $exp = 80 * 50 * 8 + $pi * 15 * 15 * 12 - $pi * 11 * 11 * 7 - $pi * 5 * 5 * (8 + 5) - 4 * $pi * 2.25 * 2.25 * 8 - 4 * (1 - $pi / 4) * 25 * 8 - 0.5 * 1 * 1 * 2 * $pi * (15 - 1.0 / 3)
    Write-Host ("示例零件体积 = {0:N1} mm³，解析期望 ≈ {1:N1} mm³，偏差 {2:N2}%" -f $v, $exp, (100 * ($v - $exp) / $exp))

    # ===== ShellIn 用到的特征类型名 =====
    $script:swModel = NewPart; [void]$titles.Add((Call $swModel 'GetTitle'))
    $script:swSketchMgr = GetP $swModel 'SketchManager'; $script:swFeatMgr = GetP $swModel 'FeatureManager'
    StartSketch (GetPlane 1)
    [void](Call $swSketchMgr 'CreateCenterRectangle' @(0.0, 0.0, 0.0, 0.03, 0.02, 0.0)); EndSketch
    [void](ExtrudeBoss 0.02)
    [void](SelectNearest 'FACE' 0.0 0.0 0.02)
    [void](Call $swModel 'InsertFeatureShell' @(0.002, $false))
    Write-Host ("抽壳特征类型名 = " + (Call (Call $swModel 'FeatureByPositionReverse' @(0)) 'GetTypeName2'))
}
finally {
    foreach ($t in $titles) { try { [void](Call $swApp 'CloseDoc' @($t)) } catch {} }
    Write-Host ("已关闭临时零件: " + ($titles -join ', '))
    Stop-IsolatedSw $iso
}
