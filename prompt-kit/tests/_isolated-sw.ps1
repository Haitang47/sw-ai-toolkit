# 测试脚本共用：另外启动一个隐藏的 SolidWorks 来跑测试，不碰用户正在用的 SolidWorks。
# 背景：2026-11-16 曾在用户正在工作的 SolidWorks 里跑批量导出测试，导致它崩溃、未保存的修改丢失。
# 用法：. "$PSScriptRoot\_isolated-sw.ps1"; $h = Start-IsolatedSw; $sw = $h.App; ...; Stop-IsolatedSw $h

if (-not ('SwRot' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class SwRot {
    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int r, out IRunningObjectTable t);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int r, out IBindCtx c);
    public static object Get(string name) {
        IRunningObjectTable rot; if (GetRunningObjectTable(0, out rot) != 0) return null;
        IBindCtx ctx; CreateBindCtx(0, out ctx);
        IEnumMoniker e; rot.EnumRunning(out e);
        var m = new IMoniker[1];
        while (e.Next(1, m, IntPtr.Zero) == 0) {
            string n; m[0].GetDisplayName(ctx, null, out n);
            object o; if (n == name && rot.GetObject(m[0], out o) == 0) return o;
        }
        return null;
    }
}
'@
}

function Find-SwExe {
    $key = 'HKLM:\SOFTWARE\SolidWorks'
    foreach ($k in (Get-ChildItem $key -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^SOLIDWORKS 2\d{3}$' } | Sort-Object PSChildName -Descending)) {
        $dir = (Get-ItemProperty (Join-Path $k.PSPath 'Setup') -ErrorAction SilentlyContinue).'SolidWorks Folder'
        if ($dir -and (Test-Path (Join-Path $dir 'SLDWORKS.exe'))) { return (Join-Path $dir 'SLDWORKS.exe') }
    }
    throw '本机没有安装 SolidWorks'
}

function Start-IsolatedSw {
    $exe = Find-SwExe
    Write-Host '正在后台启动一个独立的 SolidWorks 用于测试（约 20–60 秒）……'
    $psi = New-Object System.Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute = $false
    $psi.WindowStyle = 'Hidden'
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.BeginOutputReadLine(); $p.BeginErrorReadLine()
    $start = Get-Date; $app = $null
    while (-not $app) {
        if ($p.HasExited) { throw "SolidWorks 启动后退出了（退出码 $($p.ExitCode)）" }
        if (((Get-Date) - $start).TotalSeconds -gt 240) { $p.Kill(); throw '等待 SolidWorks 启动超时' }
        $app = [SwRot]::Get("SolidWorks_PID_$($p.Id)")
        if (-not $app) { Start-Sleep -Milliseconds 500 }
    }
    Start-Sleep -Seconds 3
    [void][System.__ComObject].InvokeMember('RevisionNumber', [Reflection.BindingFlags]::InvokeMethod, $null, $app, @())
    try { [void][System.__ComObject].InvokeMember('Visible', [Reflection.BindingFlags]::SetProperty, $null, $app, @($false)) } catch {}
    Write-Host ("后台 SolidWorks 已就绪（进程 {0}，用时 {1:0} 秒）" -f $p.Id, ((Get-Date) - $start).TotalSeconds)
    return @{ App = $app; Process = $p }
}

function Stop-IsolatedSw($h) {
    if (-not $h) { return }
    try { [void][System.__ComObject].InvokeMember('ExitApp', [Reflection.BindingFlags]::InvokeMethod, $null, $h.App, @()) } catch {}
    try { if (-not $h.Process.WaitForExit(30000)) { $h.Process.Kill() } } catch {}
    Write-Host '已关闭测试用的后台 SolidWorks'
}
