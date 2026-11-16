using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace SwKit.Core.Workspace
{
    public sealed class ScanResult
    {
        public string Root;
        public List<FileEntry> Files = new List<FileEntry>();   // SolidWorks 文件和 STEP / STL
        public int Parts, Assemblies, Drawings, Steps, Stls;
        public long TotalBytes;
        public DateTime Newest;
        public double Seconds;
        public long DirsScanned;
        public IEnumerable<FileEntry> SwFiles => Files.Where(f => FastScan.IsSwFile(f.Name));
    }

    /// <summary>一组同名文件。</summary>
    public sealed class DupGroup
    {
        public string Name;
        public List<FileEntry> Inside = new List<FileEntry>();    // 工作区里的
        public List<FileEntry> Outside = new List<FileEntry>();   // 工作区外的（全电脑检查）
        public bool Identical;                                    // 所有文件内容完全相同
        public bool Global;
        /// <summary>内容分组：路径 → 「A」「B」…（内容相同的文件字母相同，A 是最近修改的那份内容）。</summary>
        public Dictionary<string, string> Content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Level => Identical ? "副本" : "危险";
        public string Advice => Global
            ? (Identical
                ? "工作区外有内容完全相同的副本（可能是备份或旧项目）。暂时没问题，但只改其中一份后就会变成危险的重名；不要同时打开两边的项目。"
                : "工作区外有同名但内容不同的文件。如果 SolidWorks 里先打开了外面那个，再打开本工作区的装配体，SolidWorks 会直接用内存里那个，装配体就用错了零件。建议把其中一个改名。")
            : (Identical
                ? "内容完全相同的副本。现在不会出错，但以后只改了其中一份，就会变成危险的重名。建议删掉多余的副本，或者改成不同的名字。"
                : "同名但内容不同。SolidWorks 同一时间只能打开一个叫这个名字的文件，装配体可能会用错零件。建议在 SolidWorks 里用「重命名」或 Pack and Go 改名，这样引用会一起更新。");
        public IEnumerable<FileEntry> All => Inside.Concat(Outside);
    }

    public static class WorkspaceScanner
    {
        /// <summary>扫描一个工作区里的模型文件。</summary>
        public static ScanResult Scan(string root, FastScan.Progress progress, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var bag = new ConcurrentBag<FileEntry>();
            var p = progress ?? new FastScan.Progress();
            // 工作区里只跳过版本库、回收站这类目录
            var history = HistoryStore.Root;
            FastScan.Walk(new[] { root }, (full, name) => name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                                                          || full.Equals(history, StringComparison.OrdinalIgnoreCase),
                FastScan.IsModelFile, bag.Add, p, ct);
            var r = new ScanResult { Root = root, Files = bag.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), DirsScanned = p.Dirs };
            foreach (var f in r.Files)
            {
                switch (f.Ext)
                {
                    case ".sldprt": r.Parts++; break;
                    case ".sldasm": r.Assemblies++; break;
                    case ".slddrw": r.Drawings++; break;
                    case ".step": case ".stp": r.Steps++; break;
                    case ".stl": r.Stls++; break;
                }
                r.TotalBytes += f.Size;
                if (f.Modified > r.Newest) r.Newest = f.Modified;
            }
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        /// <summary>工作区内部的同名文件（按文件名、不区分大小写；零件和装配体扩展名不同不算重名）。</summary>
        public static List<DupGroup> InternalDuplicates(ScanResult scan, CancellationToken ct)
        {
            var groups = scan.SwFiles.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                .Select(g => new DupGroup { Name = g.First().Name, Inside = g.ToList() }).ToList();
            foreach (var g in groups) { ct.ThrowIfCancellationRequested(); Classify(g); }
            return Sort(groups);
        }

        /// <summary>
        /// 在 roots（默认所有固定磁盘）里找和工作区文件同名的外部文件。跳过系统目录、程序目录、缓存、工作区本身和历史库。
        /// </summary>
        public static List<DupGroup> GlobalDuplicates(ScanResult scan, IEnumerable<string> roots, FastScan.Progress progress, CancellationToken ct)
        {
            var mine = scan.SwFiles.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            if (mine.Count == 0) return new List<DupGroup>();
            var found = new ConcurrentDictionary<string, ConcurrentBag<FileEntry>>(StringComparer.OrdinalIgnoreCase);
            var skip = FastScan.SystemSkipper(new[] { scan.Root, HistoryStore.Root });
            FastScan.Walk(roots ?? FastScan.FixedDrives(), skip, name => FastScan.IsSwFile(name) && mine.ContainsKey(name),
                f => found.GetOrAdd(f.Name, _ => new ConcurrentBag<FileEntry>()).Add(f), progress, ct);
            var groups = new List<DupGroup>();
            foreach (var kv in found)
            {
                ct.ThrowIfCancellationRequested();
                var outside = kv.Value.Where(f => !WorkspaceSettings.IsUnder(f.Path, scan.Root)).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
                if (outside.Count == 0) continue;
                var g = new DupGroup { Name = mine[kv.Key][0].Name, Inside = mine[kv.Key], Outside = outside, Global = true };
                Classify(g);
                groups.Add(g);
            }
            return Sort(groups);
        }

        static List<DupGroup> Sort(List<DupGroup> groups) =>
            groups.OrderBy(g => g.Identical).ThenBy(g => Path.GetExtension(g.Name).ToLowerInvariant() == ".sldprt" ? 0 : 1).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>按内容给组里的文件分 A / B / C（大小不同直接算不同，大小相同的才算哈希）。A 是最近修改的那份内容。</summary>
        static void Classify(DupGroup g)
        {
            var files = g.All.ToList();
            var sizeCount = files.GroupBy(f => f.Size).ToDictionary(x => x.Key, x => x.Count());
            string Key(FileEntry f) => sizeCount[f.Size] > 1 ? f.Size + ":" + (FileHash(f.Path) ?? f.Path) : f.Size + ":" + f.Path;
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) keys[f.Path] = Key(f);
            var order = files.GroupBy(f => keys[f.Path]).OrderByDescending(x => x.Max(f => f.Modified)).Select(x => x.Key).ToList();
            foreach (var f in files) g.Content[f.Path] = ((char)('A' + Math.Min(25, order.IndexOf(keys[f.Path])))).ToString();
            g.Identical = order.Count == 1;
        }

        /// <summary>所有文件内容是否完全相同（先比大小，大小都一样才算哈希）。</summary>
        public static bool SameContent(List<FileEntry> files)
        {
            if (files.Count < 2) return true;
            if (files.Select(f => f.Size).Distinct().Count() > 1) return false;
            var hashes = files.Select(f => FileHash(f.Path)).ToList();
            return hashes.All(h => h != null) && hashes.Distinct().Count() == 1;
        }

        static readonly ConcurrentDictionary<string, (long size, DateTime mod, string hash)> _hashCache = new ConcurrentDictionary<string, (long, DateTime, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>文件内容的 SHA-1（按大小和修改时间缓存）。文件被 SolidWorks 打开时也能读。读不了返回 null。</summary>
        public static string FileHash(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (_hashCache.TryGetValue(path, out var c) && c.size == fi.Length && c.mod == fi.LastWriteTimeUtc) return c.hash;
                string h;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                using (var sha = SHA1.Create())
                    h = string.Concat(sha.ComputeHash(fs).Select(b => b.ToString("x2")));
                _hashCache[path] = (fi.Length, fi.LastWriteTimeUtc, h);
                return h;
            }
            catch { return null; }
        }

        public static string SizeText(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes} B";
    }
}
