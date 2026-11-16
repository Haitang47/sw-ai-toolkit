# 从 src/ 生成 dist/：Claude Skill 压缩包、ChatGPT 指令与知识库、通用提示词。
# 用法：powershell -ExecutionPolicy Bypass -File .\build.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$problems = New-Object System.Collections.ArrayList

function Read-Utf8([string]$path) { [IO.File]::ReadAllText($path, $utf8).TrimStart([char]0xFEFF) }
function Write-Utf8([string]$path, [string]$text) {
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
    [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n"), $utf8)
}

# 展开 {{include:相对 src 的路径}}，支持嵌套
function Expand([string]$rel, [int]$depth = 0) {
    if ($depth -gt 10) { throw "include 嵌套过深：$rel" }
    $path = Join-Path $src $rel
    if (-not (Test-Path $path)) { throw "找不到被引用的文件：$rel" }
    $text = Read-Utf8 $path
    return [regex]::Replace($text, '\{\{include:([^}]+)\}\}', {
            param($m)
            (Expand $m.Groups[1].Value.Trim() ($depth + 1)).TrimEnd()
        })
}

# 清空 dist（只删除本脚本生成的 dist 目录）
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
[void](New-Item -ItemType Directory -Path $dist)

# 各交付物共用的参考文件：发布名 → 源文件
$refs = [ordered]@{
    'L0-triage.md'         = 'levels/L0-triage.md'
    'L1-simple.md'         = 'levels/L1-simple.md'
    'L2-mechanical.md'     = 'levels/L2-mechanical.md'
    'L3-complex-shape.md'  = 'levels/L3-complex-shape.md'
    'L4-assembly.md'       = 'levels/L4-assembly.md'
    'output-format.md'     = 'common/output-format.md'
    'standard-values.md'   = 'common/standard-values.md'
    'macro-rules.md'       = 'macro/macro-rules.md'
    'sw-api-cheatsheet.md' = 'macro/sw-api-cheatsheet.md'
}

# ---------- 1. Claude Skill ----------
$skillName = 'sw-image-to-model'
$claudeDir = Join-Path $dist '1-Claude-Skill'
$stage = Join-Path ([IO.Path]::GetTempPath()) ("skill-build-" + [guid]::NewGuid().ToString('N'))
$skillDir = Join-Path $stage $skillName
$skillMd = Expand 'claude/SKILL.md'
Write-Utf8 (Join-Path $skillDir 'SKILL.md') $skillMd
foreach ($k in $refs.Keys) { Write-Utf8 (Join-Path $skillDir "references/$k") (Expand $refs[$k]) }

# 校验 frontmatter
if ($skillMd -notmatch '(?s)^---\s*\nname:\s*(\S+)\s*\ndescription:\s*(.+?)\n---') { [void]$problems.Add('SKILL.md 缺少正确的 frontmatter') }
else {
    $fmName = $Matches[1]; $fmDesc = $Matches[2].Trim()   # 先存下来，后面的 -match 会覆盖 $Matches
    if ($fmName -ne $skillName) { [void]$problems.Add("SKILL.md 的 name ($fmName) 与文件夹名 ($skillName) 不一致") }
    if ($fmName -notmatch '^[a-z0-9-]{1,64}$') { [void]$problems.Add('SKILL.md 的 name 只能用小写字母、数字和连字符，最长 64') }
    $descLen = $fmDesc.Length
    if ($descLen -gt 200) { [void]$problems.Add("SKILL.md 的 description 有 $descLen 字符，超过 200（Claude 网页版上传限制）") }
    Write-Host "SKILL.md description 长度：$descLen / 200"
}

# 打包 zip：根目录是 skill 文件夹，路径分隔符用 /
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[void](New-Item -ItemType Directory -Path $claudeDir -Force)
$zipPath = Join-Path $claudeDir "$skillName.zip"
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $skillDir -Recurse -File | Sort-Object FullName | ForEach-Object {
        $entry = $skillName + '/' + $_.FullName.Substring($skillDir.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry)
    }
}
finally { $zip.Dispose() }
Remove-Item $stage -Recurse -Force
Write-Utf8 (Join-Path $claudeDir '安装说明.md') (Expand 'claude/安装说明.md')

# ---------- 2. ChatGPT ----------
$gptDir = Join-Path $dist '2-ChatGPT'
$instr = Expand 'chatgpt/instructions.md'
Write-Utf8 (Join-Path $gptDir 'GPT指令.txt') $instr
$instrLen = $instr.Replace("`r`n", "`n").Length
Write-Host "GPT 指令长度：$instrLen / 8000 字符"
if ($instrLen -gt 8000) { [void]$problems.Add("GPT 指令有 $instrLen 字符，超过 8000 上限") }
foreach ($k in $refs.Keys) { Write-Utf8 (Join-Path $gptDir "知识库/$k") (Expand $refs[$k]) }
Write-Utf8 (Join-Path $gptDir '安装说明.md') (Expand 'chatgpt/安装说明.md')

# ---------- 3. 通用提示词 ----------
$pDir = Join-Path $dist '3-通用提示词'
Get-ChildItem (Join-Path $src 'prompts') -File -Filter *.md | ForEach-Object {
    $text = Expand ("prompts/" + $_.Name)
    Write-Utf8 (Join-Path $pDir $_.Name) $text
    if ($_.Name -ne '使用说明.md') { Write-Host ("通用提示词 {0}：{1} 字符" -f $_.Name, $text.Length) }
}

# ---------- 总说明 ----------
Write-Utf8 (Join-Path $dist 'README.md') (Expand 'README.md')

# ---------- 检查 ----------
Get-ChildItem $dist -Recurse -File -Include *.md, *.txt | ForEach-Object {
    if ((Read-Utf8 $_.FullName) -match '\{\{include:') { [void]$problems.Add("未展开的 include：$($_.FullName)") }
}
$check = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    Write-Host "zip 内容："
    $check.Entries | ForEach-Object { Write-Host ("  {0}  ({1} 字节)" -f $_.FullName, $_.Length) }
    if (-not ($check.Entries | Where-Object FullName -eq "$skillName/SKILL.md")) { [void]$problems.Add('zip 根目录下缺少 SKILL.md') }
}
finally { $check.Dispose() }

if ($problems.Count -gt 0) {
    Write-Host "构建完成，但有问题：" -ForegroundColor Yellow
    $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
    exit 1
}
Write-Host "构建成功：$dist" -ForegroundColor Green
