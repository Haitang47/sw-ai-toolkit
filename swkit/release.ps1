# 编译并打包发布版：release\SW工具箱\（程序 + 命令行版 + 提示词包 + 使用说明）、release\SW工具箱-v版本.zip（绿色版）和 release\SW工具箱-v版本-安装程序.exe（单文件安装程序）
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

# 单文件安装程序：把上面的压缩包嵌进去，双击安装（不需要管理员权限，建开始菜单 / 桌面快捷方式，可在「设置 → 应用」里卸载）
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$csc = & $vswhere -latest -products * -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$gen = Join-Path $env:TEMP "swkit-setup-$Version"
New-Item -ItemType Directory -Force $gen | Out-Null
$info = Join-Path $gen 'BuildInfo.g.cs'
[IO.File]::WriteAllText($info, "namespace SwKit.Setup { static class BuildInfo { public const string Version = `"$Version`"; } }")
$setup = Join-Path $rel "SW工具箱-v$Version-安装程序.exe"
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'Microsoft.CSharp.dll' | ForEach-Object { "/reference:$fw\$_" }
& $csc /nologo /noconfig /target:winexe /optimize+ /platform:anycpu /utf8output /codepage:65001 /langversion:latest "/out:$setup" `
    "/win32icon:$(Join-Path $root 'src\App\app.ico')" "/win32manifest:$(Join-Path $root 'src\App\app.manifest')" `
    "/resource:$zip,payload.zip" "/resource:$(Join-Path $root 'src\App\app.ico'),app.ico" "/resource:$(Join-Path $root 'src\App\logo.png'),logo.png" @refs (Join-Path $root 'src\Setup\Setup.cs') $info
if ($LASTEXITCODE -ne 0) { throw '安装程序编译失败' }

Write-Host "发布文件夹：$app" -ForegroundColor Green
Write-Host "安装程序：$setup（$([math]::Round((Get-Item $setup).Length / 1KB)) KB）" -ForegroundColor Green
Write-Host "发布压缩包：$zip（$([math]::Round((Get-Item $zip).Length / 1KB)) KB）" -ForegroundColor Green
