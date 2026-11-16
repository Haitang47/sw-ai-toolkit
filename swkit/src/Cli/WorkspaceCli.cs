using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SwKit.Core;
using SwKit.Core.Workspace;

namespace SwKit.Cli
{
    /// <summary>
    /// swkit-cli ws scan &lt;文件夹&gt;                      统计 SolidWorks 文件
    /// swkit-cli ws dup &lt;文件夹&gt; [--global] [--roots 目录1;目录2]   重名检查（工作区内；--global 再查全电脑）
    /// swkit-cli ws refs &lt;装配体或文件夹&gt; [--ws 工作区]   检查引用（在后台 SolidWorks 里）
    /// swkit-cli ws snapshot &lt;文件夹&gt; [--note 备注]       全部存档
    /// swkit-cli ws history &lt;文件&gt; [--ws 工作区]          列出版本
    /// swkit-cli ws restore &lt;文件&gt; &lt;版本&gt; [--ws 工作区]    恢复
    /// swkit-cli ws watch &lt;文件夹&gt; &lt;秒数&gt;               自动存档（测试用）
    /// swkit-cli ws recovery                             SolidWorks 的自动恢复 / 备份文件
    /// 通用选项：--data &lt;文件夹&gt;  设置和历史库放到这里（测试用，不碰用户设置）
    /// </summary>
    static class WorkspaceCli
    {
        public static int Run(string[] args)
        {
            var a = args.ToList();
            var data = Opt(a, "--data");
            if (data != null) WorkspaceSettings.OverrideDir = Path.GetFullPath(data);
            if (a.Count == 0) { Console.WriteLine("用法：swkit-cli ws scan|dup|refs|snapshot|history|restore|watch|recovery …"); return 2; }
            var cmd = a[0]; a.RemoveAt(0);
            switch (cmd)
            {
                case "scan": return Scan(a);
                case "dup": return Dup(a);
                case "refs": return Refs(a);
                case "snapshot": return Snapshot(a);
                case "history": return History(a);
                case "restore": return Restore(a);
                case "watch": return Watch(a);
                case "recovery": return Recovery();
                default: Console.Error.WriteLine("未知子命令：" + cmd); return 2;
            }
        }

        static string Opt(List<string> a, string name)
        {
            var i = a.IndexOf(name);
            if (i < 0) return null;
            var v = i + 1 < a.Count ? a[i + 1] : "";
            a.RemoveRange(i, Math.Min(2, a.Count - i));
            return v;
        }

        static bool Flag(List<string> a, string name) => a.Remove(name);

        static int Scan(List<string> a)
        {
            var r = WorkspaceScanner.Scan(WorkspaceSettings.Normalize(a[0]), null, CancellationToken.None);
            Console.WriteLine($"{r.Root}：零件 {r.Parts}、装配体 {r.Assemblies}、工程图 {r.Drawings}、STEP {r.Steps}、STL {r.Stls}，共 {WorkspaceScanner.SizeText(r.TotalBytes)}，{r.DirsScanned} 个文件夹，用时 {r.Seconds:0.00} 秒");
            foreach (var f in r.Files.OrderByDescending(f => f.Modified).Take(5)) Console.WriteLine($"  最近修改 {f.Modified:yyyy-MM-dd HH:mm}  {f.Path}");
            return 0;
        }

        static int Dup(List<string> a)
        {
            var global = Flag(a, "--global");
            var roots = Opt(a, "--roots");
            var root = WorkspaceSettings.Normalize(a[0]);
            var sw = Stopwatch.StartNew();
            var scan = WorkspaceScanner.Scan(root, null, CancellationToken.None);
            var groups = WorkspaceScanner.InternalDuplicates(scan, CancellationToken.None);
            Console.WriteLine($"工作区内：{scan.SwFiles.Count()} 个 SolidWorks 文件，{groups.Count} 组同名（危险 {groups.Count(g => !g.Identical)}，副本 {groups.Count(g => g.Identical)}），{sw.Elapsed.TotalSeconds:0.0} 秒");
            Print(groups, root);
            if (global)
            {
                sw.Restart();
                var p = new FastScan.Progress();
                var rootList = roots?.Split(';').Where(x => x.Length > 0).ToList() ?? FastScan.FixedDrives();
                var t = new Thread(() => { while (true) { Thread.Sleep(5000); Console.Error.WriteLine($"  …已扫描 {p.Dirs:N0} 个文件夹、{p.Files:N0} 个文件"); } }) { IsBackground = true };
                t.Start();
                var gg = WorkspaceScanner.GlobalDuplicates(scan, rootList, p, CancellationToken.None);
                Console.WriteLine($"全电脑（{string.Join("、", rootList)}）：扫描 {p.Dirs:N0} 个文件夹、{p.Files:N0} 个文件，用时 {sw.Elapsed.TotalSeconds:0.0} 秒；工作区外同名 {gg.Count} 组（危险 {gg.Count(g => !g.Identical)}，副本 {gg.Count(g => g.Identical)}）");
                Print(gg, root);
            }
            return 0;
        }

        static void Print(List<DupGroup> groups, string root)
        {
            foreach (var g in groups.Take(40))
            {
                Console.WriteLine($"  [{g.Level}] {g.Name}");
                foreach (var f in g.Inside) Console.WriteLine($"      内 {g.Content[f.Path]} {f.Modified:yyyy-MM-dd HH:mm} {WorkspaceScanner.SizeText(f.Size),9}  {f.Path}");
                foreach (var f in g.Outside.Take(6)) Console.WriteLine($"      外 {g.Content[f.Path]} {f.Modified:yyyy-MM-dd HH:mm} {WorkspaceScanner.SizeText(f.Size),9}  {f.Path}");
                if (g.Outside.Count > 6) Console.WriteLine($"      …还有 {g.Outside.Count - 6} 个");
            }
            if (groups.Count > 40) Console.WriteLine($"  …还有 {groups.Count - 40} 组");
        }

        static int Refs(List<string> a)
        {
            var ws = Opt(a, "--ws");
            var target = Path.GetFullPath(a[0]);
            var docs = Directory.Exists(target)
                ? WorkspaceScanner.Scan(target, null, CancellationToken.None).Files.Where(f => f.Ext == ".sldasm" || f.Ext == ".slddrw").Select(f => f.Path).ToList()
                : new List<string> { target };
            ws = ws != null ? WorkspaceSettings.Normalize(ws) : Directory.Exists(target) ? WorkspaceSettings.Normalize(target) : Path.GetDirectoryName(target);
            using (var s = SwSession.Connect(SwMode.Isolated, false, m => Console.Error.WriteLine(m), CancellationToken.None))
            {
                foreach (var d in docs)
                {
                    var sw = Stopwatch.StartNew();
                    var r = RefCheck.Check(s.App, d, ws);
                    var open = (object)s.App.GetDocuments() is object[] o ? o.Length : 0;
                    Console.WriteLine($"{Path.GetFileName(d)}：{r.Count} 个引用，{r.Issues.Count} 个问题，{sw.Elapsed.TotalSeconds:0.00} 秒（检查后 SolidWorks 里打开的文档数：{open}）" + (r.Error != null ? " 出错：" + r.Error : ""));
                    foreach (var i in r.Issues.Take(30)) Console.WriteLine($"  [{i.Kind}] {i.Name}（在 {Path.GetFileName(i.Document)} 里）\n      记录 {i.Stored}\n      实际 {i.Resolved}");
                }
            }
            return 0;
        }

        static string WsFor(string file, string ws)
        {
            if (ws != null) return WorkspaceSettings.Normalize(ws);
            return WorkspaceSettings.Load().WorkspaceOf(Path.GetFullPath(file)) ?? Path.GetDirectoryName(Path.GetFullPath(file));
        }

        static int Snapshot(List<string> a)
        {
            var note = Opt(a, "--note");
            var root = WorkspaceSettings.Normalize(a[0]);
            var scan = WorkspaceScanner.Scan(root, null, CancellationToken.None);
            var store = new HistoryStore(root);
            var sw = Stopwatch.StartNew();
            var (saved, same, failed) = store.SaveAll(scan.SwFiles.Select(f => f.Path), note, null, CancellationToken.None);
            Console.WriteLine($"存档：新存 {saved} 个，没变 {same} 个，失败 {failed.Count} 个，{sw.Elapsed.TotalSeconds:0.0} 秒；历史库 {WorkspaceScanner.SizeText(store.TotalBytes())}（{store.Dir}）");
            foreach (var f in failed) Console.WriteLine("  失败 " + f);
            return failed.Count == 0 ? 0 : 1;
        }

        static int History(List<string> a)
        {
            var ws = Opt(a, "--ws");
            var file = Path.GetFullPath(a[0]);
            var store = new HistoryStore(WsFor(file, ws));
            foreach (var v in store.Versions(file)) Console.WriteLine($"{v.Id}  {v.Reason,-4} {WorkspaceScanner.SizeText(v.Size),9}  {v.Sha1.Substring(0, 8)}  {v.Note}");
            return 0;
        }

        static int Restore(List<string> a)
        {
            var ws = Opt(a, "--ws");
            var file = Path.GetFullPath(a[0]);
            var store = new HistoryStore(WsFor(file, ws));
            try
            {
                var v = store.Restore(file, a[1]);
                Console.WriteLine($"已恢复：{file} ← {a[1]}（记录为 {v?.Id}）");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("恢复失败：" + ex.Message); return 1; }
        }

        static int Watch(List<string> a)
        {
            var root = WorkspaceSettings.Normalize(a[0]);
            var secs = a.Count > 1 ? int.Parse(a[1]) : 20;
            var settings = new WorkspaceSettings { Workspaces = new List<string> { root } };
            using (var w = new HistoryWatcher(() => settings))
            {
                w.Saved += v => Console.WriteLine($"自动存档：{v.Rel}  {v.Id}  {WorkspaceScanner.SizeText(v.Size)}");
                w.Failed += (p, ex) => Console.WriteLine($"自动存档失败：{p}：{ex.Message}");
                w.Sync(settings.Workspaces, true);
                Console.WriteLine($"监视 {root}，{secs} 秒……");
                Thread.Sleep(secs * 1000);
                Console.WriteLine($"结束，还在等待的 {w.PendingCount} 个");
            }
            return 0;
        }

        static int Recovery()
        {
            foreach (var s in SwRecovery.ReadSettings())
                Console.WriteLine($"{s.Version}：自动恢复 {(s.AutoRecoverOn ? "开" : "关")}（间隔 {s.AutoRecoverInterval}），保存时备份 {(s.BackupsOn ? $"开（{s.BackupCopies} 份）" : "关")}");
            foreach (var f in SwRecovery.Folders()) Console.WriteLine($"  {f.Kind} {(f.Exists ? "存在" : "不存在")} {(f.IsDefault ? "（默认位置）" : "")} {f.Path}");
            var files = SwRecovery.Files();
            Console.WriteLine($"恢复 / 备份文件 {files.Count} 个");
            foreach (var (f, kind) in files.Take(20)) Console.WriteLine($"  [{kind}] {f.Modified:yyyy-MM-dd HH:mm} {WorkspaceScanner.SizeText(f.Size),9} {f.Path} → 建议名 {SwRecovery.SuggestName(f.Path)}");
            return 0;
        }
    }
}
