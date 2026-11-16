# 检查导出结果是否正确（不依赖 SolidWorks）：
#   - STL：读取包围盒和单位，确认是对的模型、对的坐标
#   - STEP：读取文件头里的协议（AP203 / AP214 / AP242）
# 用法：powershell -ExecutionPolicy Bypass -File .\verify-outputs.ps1 -Path <文件或文件夹>

param([Parameter(Mandatory = $true)][string]$Path)

function StlBox([string]$file) {
    $bytes = [IO.File]::ReadAllBytes($file)
    $isAscii = [Text.Encoding]::ASCII.GetString($bytes, 0, [Math]::Min(5, $bytes.Length)) -eq 'solid' -and
               ([Text.Encoding]::ASCII.GetString($bytes, 0, [Math]::Min(400, $bytes.Length)) -match 'facet')
    $min = @([double]::MaxValue, [double]::MaxValue, [double]::MaxValue); $max = @([double]::MinValue, [double]::MinValue, [double]::MinValue)
    $n = 0
    if ($isAscii) {
        foreach ($m in [regex]::Matches([Text.Encoding]::ASCII.GetString($bytes), 'vertex\s+(\S+)\s+(\S+)\s+(\S+)')) {
            for ($k = 0; $k -lt 3; $k++) { $v = [double]$m.Groups[$k + 1].Value; if ($v -lt $min[$k]) { $min[$k] = $v }; if ($v -gt $max[$k]) { $max[$k] = $v } }
        }
        $n = -1
    } else {
        $n = [BitConverter]::ToUInt32($bytes, 80)
        for ($t = 0; $t -lt $n; $t++) {
            $o = 84 + $t * 50 + 12
            for ($vtx = 0; $vtx -lt 3; $vtx++) { for ($k = 0; $k -lt 3; $k++) { $v = [BitConverter]::ToSingle($bytes, $o + $vtx * 12 + $k * 4); if ($v -lt $min[$k]) { $min[$k] = $v }; if ($v -gt $max[$k]) { $max[$k] = $v } } }
        }
    }
    $fmt = if ($isAscii) { 'ASCII' } else { "二进制 $n 个三角形" }
    "{0}  [{1}]  X {2:0.###}..{3:0.###}  Y {4:0.###}..{5:0.###}  Z {6:0.###}..{7:0.###}" -f (Split-Path $file -Leaf), $fmt, $min[0], $max[0], $min[1], $max[1], $min[2], $max[2]
}

function StepSchema([string]$file) {
    $head = (Get-Content -LiteralPath $file -TotalCount 40 -Encoding UTF8) -join ' '
    $schema = if ($head -match "FILE_SCHEMA\s*\(\s*\(\s*'([^']+)'") { $Matches[1] } else { '（未找到）' }
    $ap = switch -Regex ($schema) { 'AP242|MANAGED_MODEL' { 'AP242' } 'AUTOMOTIVE_DESIGN' { 'AP214' } 'CONFIG_CONTROL_DESIGN' { 'AP203' } default { '?' } }
    "{0}  {1}  ({2})" -f (Split-Path $file -Leaf), $ap, $schema
}

$files = if (Test-Path $Path -PathType Container) { Get-ChildItem $Path -Recurse -File } else { Get-Item $Path }
foreach ($f in $files) {
    switch ($f.Extension.ToLowerInvariant()) {
        '.stl' { StlBox $f.FullName }
        '.step' { StepSchema $f.FullName }
        '.stp' { StepSchema $f.FullName }
    }
}
