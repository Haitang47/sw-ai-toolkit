using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace SwKit.Core.Workspace
{
    /// <summary>装配体 / 工程图引用检查里的一条问题。</summary>
    public sealed class RefIssue
    {
        public string Document;   // 哪个装配体或工程图
        public string Name;       // 引用的文件名
        public string Stored;     // 文档里记录的路径
        public string Resolved;   // SolidWorks 按搜索规则实际会找到的路径
        public string Kind;       // 缺失 / 会用错文件 / 工作区外
        public string Detail;
    }

    public sealed class RefReport
    {
        public string Document;
        public int Count;                                     // 引用总数（含嵌套）
        public List<RefIssue> Issues = new List<RefIssue>();
        public string Error;
    }

    /// <summary>
    /// 用 SolidWorks 的 GetDocumentDependencies2 读装配体 / 工程图的引用（不需要打开文档，所以很快）：
    /// 一次取文档里记录的路径，一次取按 SolidWorks 搜索规则实际会找到的路径，两者对比找出缺失、用错文件、引用工作区外文件的情况。
    /// 只能在工具自己启动的后台 SolidWorks 里调用。
    /// </summary>
    public static class RefCheck
    {
        public static RefReport Check(ISldWorks app, string doc, string workspace)
        {
            var rep = new RefReport { Document = doc };
            try
            {
                // 逐层展开：每个子装配体用它自己实际会被找到的位置继续查（SolidWorks 自带的 traverse 在中间路径失效时会漏掉下层）
                var queue = new Queue<string>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                queue.Enqueue(Path.GetFullPath(doc)); visited.Add(Path.GetFullPath(doc));
                while (queue.Count > 0)
                {
                    var parent = queue.Dequeue();
                    CheckOne(app, parent, workspace, rep, seen, child =>
                    {
                        if (child.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) && File.Exists(child) && visited.Add(Path.GetFullPath(child)) && visited.Count < 2000)
                            queue.Enqueue(Path.GetFullPath(child));
                    });
                }
            }
            catch (Exception ex) { rep.Error = ex.Message; }
            return rep;
        }

        static void CheckOne(ISldWorks app, string doc, string workspace, RefReport rep, HashSet<string> seen, Action<string> child)
        {
            {
                var stored = Pairs((object)app.GetDocumentDependencies2(doc, false, false, false));
                var resolved = Pairs((object)app.GetDocumentDependencies2(doc, false, true, false));
                rep.Count += stored.Count;
                var resolvedByName = resolved.GroupBy(r => r.name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(x => x.path).ToList(), StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < stored.Count; i++)
                {
                    var (name, sp) = stored[i];
                    string rp = resolved.Count == stored.Count && string.Equals(resolved[i].name, name, StringComparison.OrdinalIgnoreCase) ? resolved[i].path
                              : resolvedByName.TryGetValue(name, out var l) ? l.First() : sp;
                    child(File.Exists(rp) ? rp : sp);
                    if (!seen.Add(doc + "|" + name + "|" + sp + "|" + rp)) continue;
                    bool sExists = File.Exists(sp), rExists = File.Exists(rp);
                    if (!rExists && !sExists)
                        rep.Issues.Add(new RefIssue { Document = doc, Name = name, Stored = sp, Resolved = rp, Kind = "缺失", Detail = "找不到这个文件，打开时 SolidWorks 会提示找不到参考，零件会被压缩。" });
                    else if (rExists && !string.Equals(Path.GetFullPath(rp), sExists ? Path.GetFullPath(sp) : "", StringComparison.OrdinalIgnoreCase))
                    {
                        // 记录的和实际找到的不是同一个文件：内容相同（例如整个项目复制过来）就没关系，内容不同才是问题
                        var same = sExists && WorkspaceScanner.SameContent(new List<FileEntry> { Entry(sp), Entry(rp) });
                        if (!same)
                            rep.Issues.Add(new RefIssue
                            {
                                Document = doc, Name = name, Stored = sp, Resolved = rp, Kind = sExists ? "会用错文件" : "位置变了",
                                Detail = sExists ? "文档里记录的是一个文件，但按 SolidWorks 的搜索规则会先找到另一个内容不同的同名文件（通常是装配体所在文件夹里的那个），装配体实际用的是它。"
                                                 : "记录的位置已经没有这个文件，SolidWorks 会改用找到的另一个同名文件。确认它就是你要的零件，然后保存一次装配体。",
                            });
                    }
                    if (rExists && workspace != null && !WorkspaceSettings.IsUnder(Path.GetFullPath(rp), workspace))
                        rep.Issues.Add(new RefIssue { Document = doc, Name = name, Stored = sp, Resolved = rp, Kind = "工作区外", Detail = "引用了工作区外面的文件。拷给别人或换电脑时这个文件不会跟着走，建议用 Pack and Go 收进项目文件夹。" });
                }
            }
        }

        static FileEntry Entry(string p) { var fi = new FileInfo(p); return new FileEntry { Path = p, Size = fi.Length, Modified = fi.LastWriteTime }; }

        /// <summary>GetDocumentDependencies2 的返回值是 [名字, 路径, 名字, 路径, …]。</summary>
        static List<(string name, string path)> Pairs(object raw)
        {
            var list = new List<(string, string)>();
            if (!(raw is Array arr)) return list;
            var items = arr.Cast<object>().Select(o => o as string ?? "").ToList();
            for (int i = 0; i + 1 < items.Count; i += 2)
                if (items[i + 1].Length > 0) list.Add((items[i], items[i + 1]));
            return list;
        }
    }
}
