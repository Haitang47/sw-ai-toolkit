using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SwKit.Core.Workspace
{
    /// <summary>一个存档版本（元数据存在同名 .json 里）。</summary>
    [DataContract]
    public sealed class HistoryVersion
    {
        [DataMember(Order = 1)] public string Id;            // 存档时间，yyyyMMdd-HHmmss-fff
        [DataMember(Order = 2)] public string Rel;           // 相对工作区的路径
        [DataMember(Order = 3)] public string Source;        // 存档时的完整路径
        [DataMember(Order = 4)] public DateTime Time;        // 存档时间
        [DataMember(Order = 5)] public DateTime FileModified;
        [DataMember(Order = 6)] public long Size;
        [DataMember(Order = 7)] public string Sha1;
        [DataMember(Order = 8)] public string Note;
        [DataMember(Order = 9)] public string Reason;        // 自动 / 手动 / 恢复前 / 恢复
        public string StoredPath;                             // 历史库里的副本
    }

    /// <summary>历史库里某个文件的概况。</summary>
    public sealed class HistoryFile
    {
        public string Rel, Path;
        public int Count;
        public DateTime Latest;
        public long Bytes;
        public bool Exists;
    }

    /// <summary>
    /// 版本历史库：放在 %LOCALAPPDATA%\SwKit\history\&lt;工作区哈希&gt;\files\&lt;文件哈希&gt;\&lt;时间&gt;.sldprt（+ .json），不往工作区里写任何东西。
    /// 内容和上一个版本相同的不重复存。
    /// </summary>
    public sealed class HistoryStore
    {
        public static string Root => Path.Combine(WorkspaceSettings.OverrideDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwKit"), "history");

        public string Workspace { get; }
        public string Dir { get; }

        static readonly ConcurrentDictionary<string, object> Locks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        object Lock => Locks.GetOrAdd(Dir, _ => new object());

        public HistoryStore(string workspace)
        {
            Workspace = WorkspaceSettings.Normalize(workspace);
            Dir = Path.Combine(Root, WorkspaceSettings.Hash(Workspace, 10));
        }

        void EnsureDir()
        {
            Directory.CreateDirectory(Path.Combine(Dir, "files"));
            var info = Path.Combine(Dir, "workspace.txt");
            if (!File.Exists(info)) File.WriteAllText(info, Workspace, Encoding.UTF8);
        }

        public string RelOf(string abs)
        {
            abs = Path.GetFullPath(abs);
            if (!WorkspaceSettings.IsUnder(abs, Workspace)) throw new ArgumentException("文件不在这个工作区里：" + abs);
            return abs.Substring(Workspace.Length).TrimStart('\\');
        }

        string FileDir(string rel) => Path.Combine(Dir, "files", WorkspaceSettings.Hash(rel, 16));

        // ---------------- 存档 ----------------

        /// <summary>存一份当前文件。内容和最近一个版本相同时不存，返回 null（evenIfUnchanged 时照样存）。文件被 SolidWorks 打开着也能读。</summary>
        public HistoryVersion Save(string abs, string reason, string note = null, bool evenIfUnchanged = false)
        {
            abs = Path.GetFullPath(abs);
            if (!File.Exists(abs)) throw new FileNotFoundException("文件不存在：" + abs);
            var rel = RelOf(abs);
            lock (Lock)
            {
                EnsureDir();
                var dir = FileDir(rel);
                Directory.CreateDirectory(dir);
                var now = DateTime.Now;
                var id = now.ToString("yyyyMMdd-HHmmss-fff");
                for (int k = 1; File.Exists(Path.Combine(dir, id + ".json")); k++) id = now.ToString("yyyyMMdd-HHmmss-fff") + "-" + k;
                var ext = Path.GetExtension(abs);
                var tmp = Path.Combine(dir, id + ".tmp");
                var fi = new FileInfo(abs);
                string hash; long size = 0;
                using (var src = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var sha = SHA1.Create())
                {
                    var buf = new byte[1 << 16];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        size += n;
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    hash = string.Concat(sha.Hash.Select(b => b.ToString("x2")));
                }
                var latest = ReadVersions(dir, rel).FirstOrDefault();
                if (!evenIfUnchanged && latest != null && latest.Sha1 == hash) { File.Delete(tmp); return null; }
                var stored = Path.Combine(dir, id + ext);
                File.Move(tmp, stored);
                File.SetLastWriteTime(stored, fi.LastWriteTime);
                var v = new HistoryVersion { Id = id, Rel = rel, Source = abs, Time = now, FileModified = fi.LastWriteTime, Size = size, Sha1 = hash, Note = note, Reason = reason, StoredPath = stored };
                using (var fs = File.Create(Path.Combine(dir, id + ".json")))
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, true, true, "  "))
                    new DataContractJsonSerializer(typeof(HistoryVersion)).WriteObject(w, v);
                return v;
            }
        }

        /// <summary>给工作区里所有 SolidWorks 文件各存一份（没变的跳过）。返回（新存的，没变的，失败的）。</summary>
        public (int saved, int unchanged, List<string> failed) SaveAll(IEnumerable<string> files, string note, Action<int, int> progress, CancellationToken ct)
        {
            var list = files.ToList();
            int saved = 0, same = 0, i = 0;
            var failed = new List<string>();
            foreach (var f in list)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(++i, list.Count);
                try { if (Save(f, "手动", note) != null) saved++; else same++; }
                catch (Exception ex) { failed.Add(Path.GetFileName(f) + "：" + ex.Message); }
            }
            return (saved, same, failed);
        }

        // ---------------- 浏览 ----------------

        /// <summary>某个文件的所有版本，最新的在前。</summary>
        public List<HistoryVersion> Versions(string abs)
        {
            var rel = RelOf(abs);
            lock (Lock) return ReadVersions(FileDir(rel), rel);
        }

        static List<HistoryVersion> ReadVersions(string dir, string rel)
        {
            var list = new List<HistoryVersion>();
            if (!Directory.Exists(dir)) return list;
            var ser = new DataContractJsonSerializer(typeof(HistoryVersion));
            foreach (var j in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    HistoryVersion v;
                    using (var fs = File.OpenRead(j)) v = (HistoryVersion)ser.ReadObject(fs);
                    v.StoredPath = Path.Combine(dir, v.Id + Path.GetExtension(v.Rel ?? rel));
                    if (File.Exists(v.StoredPath)) list.Add(v);
                }
                catch { }
            }
            return list.OrderByDescending(v => v.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>历史库里有记录的所有文件。</summary>
        public List<HistoryFile> Files()
        {
            var result = new List<HistoryFile>();
            var root = Path.Combine(Dir, "files");
            if (!Directory.Exists(root)) return result;
            lock (Lock)
                foreach (var d in Directory.GetDirectories(root))
                {
                    var vs = ReadVersions(d, null);
                    if (vs.Count == 0) continue;
                    var path = Path.Combine(Workspace, vs[0].Rel);
                    result.Add(new HistoryFile { Rel = vs[0].Rel, Path = path, Count = vs.Count, Latest = vs[0].Time, Bytes = vs.Sum(v => v.Size), Exists = File.Exists(path) });
                }
            return result.OrderByDescending(f => f.Latest).ToList();
        }

        public long TotalBytes()
        {
            var root = Path.Combine(Dir, "files");
            if (!Directory.Exists(root)) return 0;
            return new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.Extension != ".json").Sum(f => f.Length);
        }

        // ---------------- 恢复 ----------------

        /// <summary>
        /// 把文件恢复成某个版本：先把当前文件存一份（「恢复前」），再覆盖。文件被占用（多半是在 SolidWorks 里打开着）时抛异常，不做任何改动。
        /// </summary>
        public HistoryVersion Restore(string abs, string versionId)
        {
            abs = Path.GetFullPath(abs);
            var v = Versions(abs).FirstOrDefault(x => x.Id == versionId) ?? throw new ArgumentException("找不到这个版本：" + versionId);
            if (File.Exists(abs))
            {
                if (IsLocked(abs) || IsLocked(SwLockFile(abs)))
                    throw new IOException($"「{Path.GetFileName(abs)}」正被占用，多半是在 SolidWorks 里打开着。请先在 SolidWorks 里关闭它（以及引用它的装配体），再恢复。");
                Save(abs, "恢复前", $"恢复到 {v.Time:MM-dd HH:mm:ss} 的版本之前自动存的");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            var tmp = abs + ".swkit-restore";
            File.Copy(v.StoredPath, tmp, true);
            if (File.Exists(abs)) File.Replace(tmp, abs, null);
            else File.Move(tmp, abs);
            File.SetLastWriteTime(abs, DateTime.Now);
            // 记一笔「恢复」，内容和刚恢复的版本相同；之后自动存档看到内容没变就不会重复存
            return Save(abs, "恢复", $"恢复自 {v.Time:yyyy-MM-dd HH:mm:ss} 的版本", evenIfUnchanged: true);
        }

        /// <summary>SolidWorks 打开文档时在旁边放的锁文件 ~$名字.扩展名。</summary>
        public static string SwLockFile(string abs) => Path.Combine(Path.GetDirectoryName(abs), "~$" + Path.GetFileName(abs));

        /// <summary>文件是否被别的程序以独占方式打开着。</summary>
        public static bool IsLocked(string path)
        {
            if (!File.Exists(path)) return false;
            try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        // ---------------- 清理 ----------------

        /// <summary>每个文件只留最近 keepPerFile 个版本；总大小超过 maxBytes 时从最旧的删起（每个文件的最新版本总是保留）。返回删掉的个数。</summary>
        public int Prune(int keepPerFile, long maxBytes)
        {
            var root = Path.Combine(Dir, "files");
            if (!Directory.Exists(root)) return 0;
            int removed = 0;
            lock (Lock)
            {
                var all = new List<HistoryVersion>();
                long total = 0;
                foreach (var d in Directory.GetDirectories(root))
                {
                    var vs = ReadVersions(d, null);
                    for (int i = 0; i < vs.Count; i++)
                    {
                        if (i >= Math.Max(1, keepPerFile)) { Delete(vs[i]); removed++; continue; }
                        total += vs[i].Size;
                        if (i > 0) all.Add(vs[i]);
                    }
                }
                foreach (var v in all.OrderBy(v => v.Id, StringComparer.Ordinal))
                {
                    if (total <= maxBytes) break;
                    Delete(v); removed++; total -= v.Size;
                }
            }
            return removed;
        }

        static void Delete(HistoryVersion v)
        {
            try { File.Delete(v.StoredPath); } catch { }
            try { File.Delete(Path.ChangeExtension(v.StoredPath, ".json")); } catch { }
        }
    }

    /// <summary>
    /// 自动存档：监视工作区，SolidWorks 文件保存后（3 秒内没有新的改动、大小和时间都稳定）复制一份到历史库。
    /// </summary>
    public sealed class HistoryWatcher : IDisposable
    {
        public event Action<HistoryVersion> Saved;
        public event Action<string, Exception> Failed;

        readonly Dictionary<string, FileSystemWatcher> _watchers = new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, Pending> _pending = new ConcurrentDictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        readonly Timer _timer;
        readonly Func<WorkspaceSettings> _settings;
        int _savesSincePrune;
        sealed class Pending { public DateTime LastEvent; public long Size = -1; public DateTime Mod; public int Tries; }

        public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

        public HistoryWatcher(Func<WorkspaceSettings> settings)
        {
            _settings = settings;
            _timer = new Timer(_ => Tick(), null, 1000, 1000);
        }

        public IEnumerable<string> Watched { get { lock (_watchers) return _watchers.Keys.ToList(); } }

        /// <summary>按当前设置开 / 关各工作区的监视。</summary>
        public void Sync(IEnumerable<string> workspaces, bool enabled)
        {
            var want = new HashSet<string>(enabled ? workspaces.Where(Directory.Exists) : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            lock (_watchers)
            {
                foreach (var k in _watchers.Keys.Where(k => !want.Contains(k)).ToList()) { _watchers[k].Dispose(); _watchers.Remove(k); }
                foreach (var w in want.Where(w => !_watchers.ContainsKey(w)))
                {
                    try
                    {
                        var fsw = new FileSystemWatcher(w) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                        fsw.Changed += (s, e) => Touch(e.FullPath);
                        fsw.Created += (s, e) => Touch(e.FullPath);
                        fsw.Renamed += (s, e) => Touch(e.FullPath);
                        fsw.Error += (s, e) => Failed?.Invoke(w, e.GetException());
                        fsw.EnableRaisingEvents = true;
                        _watchers[w] = fsw;
                    }
                    catch (Exception ex) { Failed?.Invoke(w, ex); }
                }
            }
        }

        void Touch(string path)
        {
            try
            {
                if (!FastScan.IsSwFile(Path.GetFileName(path))) return;
                if (WorkspaceSettings.IsUnder(path, HistoryStore.Root)) return;   // 工作区包含了历史库本身时，避免自己存自己
                _pending.AddOrUpdate(path, _ => new Pending { LastEvent = DateTime.Now }, (_, p) => { p.LastEvent = DateTime.Now; return p; });
            }
            catch { }
        }

        int _ticking;
        void Tick()
        {
            if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
            try
            {
                foreach (var kv in _pending.ToList())
                {
                    var p = kv.Value;
                    if (DateTime.Now - p.LastEvent < Quiet) continue;
                    var fi = new FileInfo(kv.Key);
                    if (!fi.Exists) { _pending.TryRemove(kv.Key, out _); continue; }
                    // 大小和修改时间要连续两次检查都不变，避免读到写了一半的文件
                    if (fi.Length != p.Size || fi.LastWriteTime != p.Mod) { p.Size = fi.Length; p.Mod = fi.LastWriteTime; continue; }
                    var s = _settings();
                    var ws = s.WorkspaceOf(kv.Key);
                    if (ws == null) { _pending.TryRemove(kv.Key, out _); continue; }
                    try
                    {
                        var store = new HistoryStore(ws);
                        var v = store.Save(kv.Key, "自动");
                        _pending.TryRemove(kv.Key, out _);
                        if (v != null)
                        {
                            Saved?.Invoke(v);
                            if (++_savesSincePrune >= 20) { _savesSincePrune = 0; store.Prune(s.KeepPerFile, (long)(s.MaxHistoryGb * (1L << 30))); }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (++p.Tries >= 5) { _pending.TryRemove(kv.Key, out _); Failed?.Invoke(kv.Key, ex); }
                        else p.LastEvent = DateTime.Now;   // 过一会儿再试
                    }
                }
            }
            catch { }   // 定时器线程上的异常会让整个程序退出，这里一律吞掉，下一秒再试
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }

        /// <summary>还在等待存档的文件数（测试用）。</summary>
        public int PendingCount => _pending.Count;

        public void Dispose()
        {
            _timer.Dispose();
            lock (_watchers) { foreach (var w in _watchers.Values) w.Dispose(); _watchers.Clear(); }
        }
    }
}
