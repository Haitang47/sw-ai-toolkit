# 编译并打包发布版：release\SW工具箱\（程序 + 命令行版 + 提示词包 + 使用说明）和 release\SW工具箱-v版本.zip
# 用法：powershell -ExecutionPolicy Bypass -File .\release.ps1 [-Version 0.5]

param([string]$Version = '0.5')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$kitRoot = Join-Path (Split-Path $root -Parent) 'prompt-kit'

& powershell -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw '程序编译失败' }
& powershell -ExecutionPolicy Bypass -File (Join-Path $kitRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw '提示词包构建失败' }

$rel = Join-Path $root 'release'
$app = Join-Path $rel 'SW工具箱'
if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }
New-Item -ItemType Directory -Force -Path $app | Out-Null

Copy-Item (Join-Path $root 'bin\SwKit.exe') $app
Copy-Item (Join-Path $root 'bin\SwKit.exe.config') $app
Copy-Item (Join-Path $root 'bin\swkit-cli.exe') $app
Copy-Item (Join-Path $root '使用说明.md') $app
Copy-Item (Join-Path $kitRoot 'dist') (Join-Path $app '提示词包') -Recurse
# 应力分析用的网格划分程序 Gmsh（GPL 开源，官网 gmsh.info），连同许可证一起带上
Copy-Item (Join-Path $root 'tools\gmsh') (Join-Path $app 'gmsh') -Recurse
# 应力分析 / 拓扑优化的示例零件（STEP，连同生成它们的 Gmsh 脚本）
Copy-Item (Join-Path $root 'tools\samples') (Join-Path $app '示例') -Recurse
# 可制造性检查的测试件（已知的悬垂、薄壁、倒扣、深孔、尖内角）
New-Item -ItemType Directory -Force (Join-Path $app '示例\可制造性') | Out-Null
Copy-Item (Join-Path $root 'tools\dfm-tests\*') (Join-Path $app '示例\可制造性')

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $rel "SW工具箱-v$Version.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

Write-Host "发布文件夹：$app" -ForegroundColor Green
Write-Host "发布压缩包：$zip（$([math]::Round((Get-Item $zip).Length / 1KB)) KB）" -ForegroundColor Green
