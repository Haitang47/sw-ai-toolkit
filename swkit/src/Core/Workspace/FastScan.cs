using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SwKit.Core.Workspace
{
    public sealed class FileEntry
    {
        public string Path;
        public long Size;
        public DateTime Modified;
        public string Name => System.IO.Path.GetFileName(Path);
        public string Ext => System.IO.Path.GetExtension(Path).ToLowerInvariant();
        public override string ToString() => Path;
    }

    /// <summary>
    /// 快速遍历文件夹：直接调用 FindFirstFileEx（大批量读取、不取短文件名），多线程，
    /// 跳过无权限的目录和符号链接 / 目录联接（避免死循环），支持超长路径。几十万个文件也只要几秒到几十秒。
    /// </summary>
    public static class FastScan
    {
        public static readonly string[] SwExtensions = { ".sldprt", ".sldasm", ".slddrw" };
        public static readonly string[] ExchangeExtensions = { ".step", ".stp", ".stl" };

        public static bool IsSwFile(string name) =>
            !name.StartsWith("~$", StringComparison.Ordinal) && SwExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        public static bool IsModelFile(string name) =>
            !name.StartsWith("~$", StringComparison.Ordinal) && (SwExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || ExchangeExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

        /// <summary>扫描进度（多线程累加，界面定时读取）。</summary>
        public sealed class Progress
        {
            public long Dirs, Files, Matched;
            public volatile string Current;
        }

        /// <summary>
        /// 遍历 roots 下所有文件。skipDir(完整路径, 目录名) 返回 true 时不进入该目录；wantFile(文件名) 返回 true 的文件交给 onFile（可能在多个线程上同时调用）。
        /// </summary>
        public static void Walk(IEnumerable<string> roots, Func<string, string, bool> skipDir, Func<string, bool> wantFile, Action<FileEntry> onFile,
                                Progress progress, CancellationToken ct, int threads = 0)
        {
            var stack = new ConcurrentStack<string>(roots.Where(Directory.Exists).Select(r => r.TrimEnd('\\', '/')).Select(r => r.EndsWith(":") ? r + "\\" : r));
            if (stack.IsEmpty) return;
            if (threads <= 0) threads = Math.Max(2, Math.Min(8, Environment.ProcessorCount / 2));
            int busy = 0;
            var workers = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref busy);
                    if (stack.TryPop(out var dir))
                    {
                        try { ScanDir(dir, skipDir, wantFile, onFile, stack, progress); }
                        catch { }
                        finally { Interlocked.Decrement(ref busy); }
                        continue;
                    }
                    Interlocked.Decrement(ref busy);
                    if (stack.IsEmpty && Volatile.Read(ref busy) == 0) break;
                    Thread.Sleep(1);
                }
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(workers);
            ct.ThrowIfCancellationRequested();
        }

        static void ScanDir(string dir, Func<string, string, bool> skipDir, Func<string, bool> wantFile, Action<FileEntry> onFile, ConcurrentStack<string> stack, Progress progress)
        {
            if (progress != null) { Interlocked.Increment(ref progress.Dirs); progress.Current = dir; }
            var query = LongPath(dir.EndsWith("\\") ? dir + "*" : dir + "\\*");
            var h = FindFirstFileExW(query, 1 /* FindExInfoBasic */, out var d, 0, IntPtr.Zero, 2 /* FIND_FIRST_EX_LARGE_FETCH */);
            if (h == INVALID) return;   // 无权限、已删除等
            try
            {
                var subdirs = new List<string>();
                do
                {
                    var name = d.cFileName;
                    if (name == "." || name == "..") continue;
                    var full = dir.EndsWith("\\") ? dir + name : dir + "\\" + name;
                    if ((d.dwFileAttributes & 0x10) != 0)   // 目录
                    {
                        if ((d.dwFileAttributes & 0x400) != 0) continue;   // 符号链接 / 目录联接：不跟进
                        if (skipDir != null && skipDir(full, name)) continue;
                        subdirs.Add(full);
                    }
                    else
                    {
                        if (progress != null) Interlocked.Increment(ref progress.Files);
                        if (wantFile != null && !wantFile(name)) continue;
                        if (progress != null) Interlocked.Increment(ref progress.Matched);
                        long size = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
                        long ft = ((long)(uint)d.ftLastWriteTime.dwHighDateTime << 32) | (uint)d.ftLastWriteTime.dwLowDateTime;
                        DateTime mod;
                        try { mod = DateTime.FromFileTime(ft); } catch { mod = DateTime.MinValue; }
                        onFile(new FileEntry { Path = full, Size = size, Modified = mod });
                    }
                } while (FindNextFileW(h, out d));
                if (subdirs.Count > 0) stack.PushRange(subdirs.ToArray());
            }
            finally { FindClose(h); }
        }

        /// <summary>超长路径（超过 260 字符）要加 \\?\ 前缀。</summary>
        static string LongPath(string p)
        {
            if (p.StartsWith(@"\\?\")) return p;
            if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }

        // ---------------- 全电脑扫描时跳过的目录 ----------------

        static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "Windows.old", "Recovery", "Config.Msi",
            "node_modules", ".git", ".svn", ".hg", "__pycache__", ".vs", ".gradle", ".m2", ".nuget", ".cache", ".conda", "site-packages",
        };

        /// <summary>全电脑扫描时要跳过的目录（系统、程序、缓存、本工具自己的历史库）。</summary>
        public static Func<string, string, bool> SystemSkipper(IEnumerable<string> extraAbsolute = null)
        {
            string Env(Environment.SpecialFolder f) { try { return Environment.GetFolderPath(f); } catch { return null; } }
            var local = Env(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Env(Environment.SpecialFolder.ApplicationData);
            var abs = new[]
            {
                Env(Environment.SpecialFolder.Windows), Env(Environment.SpecialFolder.ProgramFiles), Env(Environment.SpecialFolder.ProgramFilesX86),
                Env(Environment.SpecialFolder.CommonApplicationData),
                local == null ? null : Path.Combine(local, "Temp"), local == null ? null : Path.Combine(local, "Microsoft"),
                local == null ? null : Path.Combine(local, "Packages"), local == null ? null : Path.Combine(local, "swcachedir"),
                local == null ? null : Path.Combine(local, "Google"), local == null ? null : Path.Combine(local, "SwKit"),
                roaming == null ? null : Path.Combine(roaming, "Microsoft"),
                Path.GetTempPath().TrimEnd('\\'),
            }.Concat(extraAbsolute ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x.TrimEnd('\\'));
            var set = new HashSet<string>(abs, StringComparer.OrdinalIgnoreCase);
            return (full, name) => SkipNames.Contains(name) || set.Contains(full);
        }

        /// <summary>本机所有就绪的固定磁盘。</summary>
        public static List<string> FixedDrives() =>
            DriveInfo.GetDrives().Where(d => { try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; } }).Select(d => d.RootDirectory.FullName).ToList();

        // ---------------- Win32 ----------------

        static readonly IntPtr INVALID = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATA lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr hFindFile);
    }
}
