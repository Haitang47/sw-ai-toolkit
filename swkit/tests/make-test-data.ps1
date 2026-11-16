# 在指定文件夹里生成导出测试用的 SolidWorks 文件（文件名用中文，顺便测试中文路径）：
#   测试板.sldprt   80×50×8 带孔底板，两个配置（默认、备用）
#   法兰.sldprt     旋转体
#   钣金L板.sldprt  L 形钣金件（用于展开 DXF）
#   测试装配.sldasm 测试板 + 法兰
#   测试板.slddrw   测试板的三视图工程图
# 用法：powershell -ExecutionPolicy Bypass -File .\make-test-data.ps1 -OutDir D:\swkit-test\数据
# 脚本会另外启动一个隐藏的 SolidWorks 来生成，不影响你正在用的 SolidWorks，生成完自动关闭。

param([Parameter(Mandatory = $true)][string]$OutDir)
$ErrorActionPreference = 'Stop'
$BF = [Reflection.BindingFlags]
function Call($o, $n, [object[]]$a = @()) { [System.__ComObject].InvokeMember($n, $BF::InvokeMethod, $null, $o, $a) }
function GetP($o, $n) { [System.__ComObject].InvokeMember($n, $BF::GetProperty, $null, $o, @()) }
function SetP($o, $n, $v) { [void][System.__ComObject].InvokeMember($n, $BF::SetProperty, $null, $o, @(, $v)) }
$NOTHING = [System.Runtime.InteropServices.DispatchWrapper]::new([object]$null)
$MM = 0.001

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
. "$PSScriptRoot\_isolated-sw.ps1"
$iso = Start-IsolatedSw
$sw = $iso.App
try {
$tplDir = (Call $sw 'GetUserPreferenceStringValue' @(6)).Split(';')[0]
function Tpl($pattern) { [string](Get-ChildItem (Join-Path $tplDir $pattern) | Select-Object -First 1).FullName }

function NewDoc($tpl, $paper = 0, $w = 0.0, $h = 0.0) { Call $sw 'NewDocument' @([string]$tpl, $paper, [double]$w, [double]$h) }
function Plane($m, $i) { $f = Call $m 'FirstFeature'; $n = 0; while ($f) { if ((Call $f 'GetTypeName2') -eq 'RefPlane') { $n++; if ($n -eq $i) { return $f } }; $f = Call $f 'GetNextFeature' } }
function Sk($m, $p) { $sm = GetP $m 'SketchManager'; [void](Call $m 'ClearSelection2' @($true)); [void](Call $p 'Select2' @($false, 0)); SetP $sm 'AddToDB' $true; [void](Call $sm 'InsertSketch' @($true)); $sm }
function EndSk($m) { $sm = GetP $m 'SketchManager'; SetP $sm 'AddToDB' $false; [void](Call $sm 'InsertSketch' @($true)); [void](Call $m 'ClearSelection2' @($true)); [void](Call (Call $m 'FeatureByPositionReverse' @(0)) 'Select2' @($false, 0)) }
function Save($m, $name) {
    $path = [string](Join-Path $OutDir $name)
    $err = Call $m 'SaveAs3' @($path, 0, 1)
    if ($err -ne 0) { throw "保存 $name 失败，错误码 $err" }
    Write-Host "已生成 $path"
    return $path
}
$opened = New-Object System.Collections.ArrayList

# ---- 测试板（两个配置） ----
$m = NewDoc (Tpl '*.prtdot'); [void]$opened.Add($m)
$sm = Sk $m (Plane $m 2)
[void](Call $sm 'CreateCenterRectangle' @(0.0, 0.0, 0.0, (40 * $MM), (25 * $MM), 0.0))
[void](Call $sm 'CreateCircleByRadius' @((20 * $MM), 0.0, 0.0, (5 * $MM)))
EndSk $m
$fm = GetP $m 'FeatureManager'
[void](Call $fm 'FeatureExtrusion3' @($true, $false, $false, 0, 0, (8 * $MM), (8 * $MM), $false, $false, $false, $false, 0.0, 0.0, $false, $false, $false, $false, $true, $true, $true, 0, 0.0, $false))
[void](Call $m 'AddConfiguration3' @('备用', '测试用第二个配置', '', 0))
[void](Call $m 'ShowConfiguration2' @('默认'))
$platePath = Save $m '测试板.sldprt'

# ---- 法兰（旋转） ----
$m = NewDoc (Tpl '*.prtdot'); [void]$opened.Add($m)
$sm = Sk $m (Plane $m 1)
[void](Call $sm 'CreateCenterLine' @(0.0, -0.05, 0.0, 0.0, 0.05, 0.0))
$pts = @(@(4, 0), @(30, 0), @(30, 6), @(12.5, 6), @(12.5, 20), @(4, 20), @(4, 0))
for ($i = 0; $i -lt $pts.Count - 1; $i++) { [void](Call $sm 'CreateLine' @(($pts[$i][0] * $MM), ($pts[$i][1] * $MM), 0.0, ($pts[$i + 1][0] * $MM), ($pts[$i + 1][1] * $MM), 0.0)) }
EndSk $m
$fm = GetP $m 'FeatureManager'
[void](Call $fm 'FeatureRevolve2' @($true, $true, $false, $false, $false, $false, 0, 0, (2 * [math]::PI), 0.0, $false, $false, 0.0, 0.0, 0, 0.0, 0.0, $true, $true, $true))
$flangePath = Save $m '法兰.sldprt'

# ---- 钣金 L 板 ----
$m = NewDoc (Tpl '*.prtdot'); [void]$opened.Add($m)
$sm = Sk $m (Plane $m 1)
[void](Call $sm 'CreateLine' @(0.0, 0.0, 0.0, (50 * $MM), 0.0, 0.0))
[void](Call $sm 'CreateLine' @((50 * $MM), 0.0, 0.0, (50 * $MM), (30 * $MM), 0.0))
EndSk $m
$fm = GetP $m 'FeatureManager'
# 实测：InsertSheetMetalBaseFlange2 用这组参数会返回空，旧版 InsertSheetMetalBaseFlange 可以
$flange = Call $fm 'InsertSheetMetalBaseFlange' @((2 * $MM), $false, (2 * $MM), (40 * $MM), 0.0, $false, 0, 0, 0, $NOTHING, $true, 1, (1 * $MM), (1 * $MM), 0.5, $false)
if (-not $flange) { throw '钣金基体法兰创建失败' }
$smPath = Save $m '钣金L板.sldprt'

# ---- 装配体 ----
$a = NewDoc (Tpl '*.asmdot'); [void]$opened.Add($a)
[void](Call $a 'AddComponent5' @($platePath, 0, '', $false, '', 0.0, 0.0, 0.0))
[void](Call $a 'AddComponent5' @($flangePath, 0, '', $false, '', 0.0, (0.008), 0.0))
$asmPath = Save $a '测试装配.sldasm'

# ---- 工程图 ----
$d = NewDoc (Tpl 'gb_a3.drwdot') 12 0.42 0.297; [void]$opened.Add($d)
[void](Call $d 'Create3rdAngleViews2' @($platePath))
$drwPath = Save $d '测试板.slddrw'

foreach ($doc in $opened) { try { [void](Call $sw 'CloseDoc' @([string](Call $doc 'GetTitle'))) } catch {} }
# 装配体关闭后，被引用的零件可能还留在内存里，再按路径关一次
foreach ($p in @($platePath, $flangePath, $smPath, $asmPath, $drwPath)) { $o = Call $sw 'GetOpenDocumentByName' @($p); if ($o) { try { [void](Call $sw 'CloseDoc' @([string](Call $o 'GetTitle'))) } catch {} } }
Write-Host '测试文件生成完毕，已关闭。'
}
finally { Stop-IsolatedSw $iso }
