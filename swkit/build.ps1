# 编译 SW 工具箱：生成 bin\SwKit.exe（界面版）和 bin\swkit-cli.exe（命令行版）
# 需要：Visual Studio 2019 及以上或 Build Tools（提供新版 C# 编译器）、本机安装的 SolidWorks（提供接口定义）
# 用法：powershell -ExecutionPolicy Bypass -File .\build.ps1
# 生成的 exe 只依赖 Windows 自带的 .NET Framework 4.8，SolidWorks 接口定义已嵌入 exe，可以直接拷给同事用。

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bin = Join-Path $root 'bin'

# 1. 找新版 C# 编译器（Roslyn）
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$csc = $null
if (Test-Path $vswhere) { $csc = & $vswhere -latest -products * -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1 }
if (-not $csc) { throw '找不到 Visual Studio 的 C# 编译器（Roslyn csc.exe）。请安装 Visual Studio 或 Build Tools。' }

# 2. 找 SolidWorks 接口定义（取已安装的最新版本）
$swDir = Get-ChildItem 'HKLM:\SOFTWARE\SolidWorks' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -match '^SOLIDWORKS \d{4}$' } | Sort-Object PSChildName -Descending |
    ForEach-Object { (Get-ItemProperty (Join-Path $_.PSPath 'Setup') -ErrorAction SilentlyContinue).'SolidWorks Folder' } |
    Where-Object { $_ -and (Test-Path (Join-Path $_ 'api\redist\SolidWorks.Interop.sldworks.dll')) } | Select-Object -First 1
if (-not $swDir) { throw '找不到 SolidWorks 安装目录下的 api\redist\SolidWorks.Interop.sldworks.dll。' }
$redist = Join-Path $swDir 'api\redist'

# 3. .NET Framework 4.8 程序集
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = Join-Path $fw 'WPF'
$refsCommon = @("$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Runtime.Serialization.dll", "$fw\System.Xml.dll", "$fw\System.Xml.Linq.dll", "$fw\Microsoft.CSharp.dll",
                "$fw\System.Web.Extensions.dll", "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll", "$fw\System.Net.Http.dll")
$refsUi = $refsCommon + @("$fw\System.Xaml.dll", "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll")
$links = @("$redist\SolidWorks.Interop.sldworks.dll", "$redist\SolidWorks.Interop.swconst.dll")   # /link = 把接口定义嵌入 exe

$core = Get-ChildItem (Join-Path $root 'src\Core') -Filter *.cs -Recurse | ForEach-Object FullName
$cli = Get-ChildItem (Join-Path $root 'src\Cli') -Filter *.cs | ForEach-Object FullName
$app = Get-ChildItem (Join-Path $root 'src\App') -Filter *.cs -ErrorAction SilentlyContinue | ForEach-Object FullName

New-Item -ItemType Directory -Force -Path $bin | Out-Null
$common = @('/nologo', '/noconfig', '/langversion:latest', '/optimize+', '/platform:anycpu', '/utf8output', '/codepage:65001', '/warn:4', '/nowarn:1701,1702')

function Compile($target, $out, $refs, $sources, $extra = @()) {
    $argsList = $common + @("/target:$target", "/out:$out") + ($refs | ForEach-Object { "/reference:$_" }) + ($links | ForEach-Object { "/link:$_" }) + $extra + $sources
    & $csc @argsList
    if ($LASTEXITCODE -ne 0) { throw "编译失败：$out" }
    Write-Host "已生成 $out" -ForegroundColor Green
}

Write-Host "编译器：$csc"
Write-Host "SolidWorks 接口：$redist"
$icon = "/win32icon:$(Join-Path $root 'src\App\app.ico')"   # 程序图标：橙色 SW tool（tools\make-icon.ps1 生成）
Compile 'exe' (Join-Path $bin 'swkit-cli.exe') $refsCommon ($core + $cli) @($icon)
if ($app) {
    # 界面布局和样式（.xaml）作为资源嵌入 exe，运行时加载
    $xaml = Get-ChildItem (Join-Path $root 'src\App') -Filter *.xaml | ForEach-Object { "/resource:$($_.FullName),SwKit.$($_.Name)" }
    # 清单文件：按屏幕缩放（PerMonitorV2），4K 和普通屏混用时都清晰
    $manifest = "/win32manifest:$(Join-Path $root 'src\App\app.manifest')"
    $res = "/resource:$(Join-Path $root 'src\App\app.ico'),SwKit.app.ico"
    $logo = "/resource:$(Join-Path $root 'src\App\logo.png'),SwKit.logo.png"
    Compile 'winexe' (Join-Path $bin 'SwKit.exe') $refsUi ($core + $app) ($xaml + @($manifest, $icon, $res, $logo))
    Copy-Item (Join-Path $root 'src\App\SwKit.exe.config') (Join-Path $bin 'SwKit.exe.config') -Force
}
