using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;

namespace SwKit.Core
{
    public enum SwMode
    {
        /// <summary>另外启动一个隐藏的 SolidWorks 专门做导出，不影响用户正在用的 SolidWorks（默认）。</summary>
        Isolated = 0,
        /// <summary>直接使用用户已经打开的 SolidWorks，省去启动时间，但导出时会切换它的活动窗口。</summary>
        AttachRunning = 1,
    }

    /// <summary>
    /// 与 SolidWorks 的连接。所有方法都要在同一个 STA 线程上调用。
    /// </summary>
    public sealed class SwSession : IDisposable
    {
        public const int MinSupportedMajor = 31; // SolidWorks 2023

        public ISldWorks App { get; }
        public bool LaunchedByUs { get; }
        public string Revision { get; }
        public bool ExitOnDispose { get; set; } = true;
        readonly Process _process;

        SwSession(ISldWorks app, bool launchedByUs, Process process)
        {
            App = app;
            LaunchedByUs = launchedByUs;
            _process = process;
            Revision = app.RevisionNumber();
        }

        public int MajorVersion => ParseMajor(Revision);
        public string DisplayName => Describe(Revision);

        public static int ParseMajor(string revision)
        {
            if (string.IsNullOrEmpty(revision)) return 0;
            var dot = revision.IndexOf('.');
            int.TryParse(dot > 0 ? revision.Substring(0, dot) : revision, out var major);
            return major;
        }

        /// <summary>主版本号 33 对应 SolidWorks 2025（主版本号 + 1992）。</summary>
        public static string Describe(string revision)
        {
            var major = ParseMajor(revision);
            return major > 0 ? $"SolidWorks {major + 1992}（{revision}）" : "SolidWorks";
        }

        static ISldWorks TryGetRunning()
        {
            try { return (ISldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
            catch (COMException) { return null; }
        }

        /// <summary>只检查是否有 SolidWorks 在运行，不启动。返回版本号；没有运行时返回 null。</summary>
        public static string ProbeRunningRevision()
        {
            var app = TryGetRunning();
            if (app == null) return null;
            try { return app.RevisionNumber(); }
            catch { return null; }
            finally { Marshal.ReleaseComObject(app); }
        }

        /// <summary>注册表里最新版本的 SLDWORKS.exe 路径；没装时返回 null。</summary>
        public static string FindExecutable()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks"))
            {
                if (key == null) return null;
                foreach (var name in key.GetSubKeyNames().Where(n => n.StartsWith("SOLIDWORKS 2")).OrderByDescending(n => n))
                {
                    using (var setup = key.OpenSubKey(name + @"\Setup"))
                    {
                        var dir = setup?.GetValue("SolidWorks Folder") as string;
                        if (dir == null) continue;
                        var exe = Path.Combine(dir, "SLDWORKS.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
            return null;
        }

        public static bool IsInstalled() => FindExecutable() != null || Type.GetTypeFromProgID("SldWorks.Application") != null;

        public static SwSession Connect(SwMode mode, bool showLaunched, Action<string> log, CancellationToken ct)
        {
            if (mode == SwMode.AttachRunning)
            {
                var running = TryGetRunning();
                if (running != null)
                {
                    var s = new SwSession(running, false, null);
                    log?.Invoke($"使用正在运行的 {s.DisplayName}（导出期间请不要操作 SolidWorks）");
                    return s;
                }
                log?.Invoke("没有正在运行的 SolidWorks，改为后台启动一个。");
            }
            return LaunchIsolated(showLaunched, log, ct);
        }

        static SwSession LaunchIsolated(bool show, Action<string> log, CancellationToken ct)
        {
            var exe = FindExecutable() ?? throw new InvalidOperationException("本机没有安装 SolidWorks（注册表里找不到 SLDWORKS.exe）。");
            log?.Invoke("正在后台启动一个独立的 SolidWorks（第一次大约需要 20–60 秒）……");
            var started = DateTime.Now;
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WindowStyle = show ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe),
                // SolidWorks 的 DWG/DXF 转换器会往控制台打印大量进度，接住并丢弃，不让它刷屏
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 SolidWorks。");
            proc.OutputDataReceived += (s, e) => { };
            proc.ErrorDataReceived += (s, e) => { };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            ChildProcessKiller.Track(proc);   // 本程序退出或崩溃时，这个后台 SolidWorks 会被一起结束

            try
            {
                ISldWorks app = null;
                var moniker = "SolidWorks_PID_" + proc.Id;
                while (app == null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (proc.HasExited) throw new InvalidOperationException($"SolidWorks 启动后退出了（退出码 {proc.ExitCode}）。");
                    if ((DateTime.Now - started).TotalSeconds > 240) throw new TimeoutException("等待 SolidWorks 启动超时（4 分钟）。");
                    app = Rot.Get(moniker) as ISldWorks;
                    if (app == null) Thread.Sleep(500);
                }
                // 实测：窗口隐藏启动时 StartupProcessCompleted 会一直是 false，所以最多等 30 秒，
                // 之后用一次实际调用确认可用（SolidWorks 忙时，消息过滤器会自动重试）
                var waitStart = DateTime.Now;
                while ((DateTime.Now - waitStart).TotalSeconds < 30)
                {
                    ct.ThrowIfCancellationRequested();
                    bool done;
                    try { done = app.StartupProcessCompleted; } catch (COMException) { done = false; }
                    if (done) break;
                    Thread.Sleep(500);
                }
                app.RevisionNumber();
                if (!show) Hide(app);
                var session = new SwSession(app, true, proc);
                log?.Invoke($"后台 {session.DisplayName} 已就绪，用时 {(DateTime.Now - started).TotalSeconds:0} 秒");
                return session;
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                throw;
            }
        }

        static void Hide(ISldWorks app)
        {
            try { app.Visible = false; } catch { }
            try { if (app.Frame() is Frame f) f.KeepInvisible = true; } catch { }
        }

        public void Dispose()
        {
            if (LaunchedByUs && ExitOnDispose)
            {
                try { App.ExitApp(); } catch { }
                try { Marshal.ReleaseComObject(App); } catch { }
                try
                {
                    if (_process != null && !_process.WaitForExit(30000)) _process.Kill();
                }
                catch { }
            }
            else
            {
                try { Marshal.ReleaseComObject(App); } catch { }
            }
        }
    }

    /// <summary>运行对象表（ROT）。每个 SolidWorks 进程都会登记为 "SolidWorks_PID_进程号"，可以按进程号连到指定实例。</summary>
    static class Rot
    {
        [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
        [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx ctx);

        public static object Get(string displayName)
        {
            if (GetRunningObjectTable(0, out var rot) != 0) return null;
            CreateBindCtx(0, out var ctx);
            rot.EnumRunning(out var monikers);
            var one = new IMoniker[1];
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                one[0].GetDisplayName(ctx, null, out var name);
                if (name == displayName && rot.GetObject(one[0], out var obj) == 0) return obj;
            }
            return null;
        }
    }

    /// <summary>用 Windows 作业对象把后台 SolidWorks 绑定到本程序：本程序结束（包括崩溃）时自动结束它，避免留下看不见的进程。</summary>
    static class ChildProcessKiller
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInfo info, int length);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimitInfo
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public int LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public int ActiveProcessLimit;
            public long Affinity;
            public int PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimitInfo
        {
            public BasicLimitInfo Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        const int JobObjectExtendedLimitInformation = 9;
        const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        static IntPtr _job = IntPtr.Zero;
        static readonly object _lock = new object();

        public static void Track(Process p)
        {
            try
            {
                lock (_lock)
                {
                    if (_job == IntPtr.Zero)
                    {
                        _job = CreateJobObject(IntPtr.Zero, null);
                        var info = new ExtendedLimitInfo { Basic = new BasicLimitInfo { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
                        SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(typeof(ExtendedLimitInfo)));
                    }
                    AssignProcessToJobObject(_job, p.Handle);
                }
            }
            catch { /* 绑定失败不影响导出，只是崩溃时可能留下后台进程 */ }
        }
    }
}
