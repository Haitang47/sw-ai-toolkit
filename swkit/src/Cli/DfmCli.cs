using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using SwKit.Core;
using SwKit.Core.Dfm;
using SwKit.Core.Fea;
using SwKit.Core.Report;
using SwKit.Core.Urdf;

namespace SwKit.Cli
{
    /// <summary>
    /// swkit-cli dfm &lt;零件.step|.stp|.stl|.sldprt&gt; [--process fdm|sla|cnc] [--up x,y,z] [--overhang 45] [--min-wall mm] [--tilts] [--report 报告.docx]
    /// 可制造性检查（命令行版）。.sldprt 会在隔离的后台 SolidWorks 里转成 STEP（有缓存）。
    /// </summary>
    static class DfmCli
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("用法：swkit-cli dfm <零件.step|.stp|.stl|.sldprt> [--process fdm|sla|cnc] [--up x,y,z] [--overhang 45] [--min-wall mm] [--tilts] [--report 报告.docx]"); return 2; }
            var path = Path.GetFullPath(args[0]);
            var s = new DfmSettings();
            Vec3? up = null; string report = null;
            for (int i = 1; i < args.Length; i++)
                switch (args[i])
                {
                    case "--process": var p = args[++i].ToLowerInvariant(); s.Process = p == "cnc" ? DfmProcess.Cnc : p == "sla" ? DfmProcess.Sla : DfmProcess.Fdm; break;
                    case "--up": var v = args[++i].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(); up = new Vec3(v[0], v[1], v[2]); break;
                    case "--overhang": s.OverhangDeg = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--min-wall": if (s.Process == DfmProcess.Cnc) s.MinWallCnc = double.Parse(args[++i], CultureInfo.InvariantCulture); else s.MinWallPrint = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--tilts": s.FiveAxisTilts = true; break;
                    case "--report": report = Path.GetFullPath(args[++i]); break;
                }
            if (s.Process == DfmProcess.Sla) s.PrintMaterial = PrintMaterial.All.First(x => x.Resin);
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var input = path;
            if (Path.GetExtension(path).ToLowerInvariant() == ".sldprt")
            {
                if (!StepCache.TryGet(path, out input, out _))
                {
                    using (var ses = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
                    {
                        ses.ExitOnDispose = true;
                        int e = 0, w = 0;
                        var doc = ses.App.OpenDoc6(path, 1, 1 | 2, "", ref e, ref w) as SolidWorks.Interop.sldworks.ModelDoc2 ?? throw new InvalidOperationException("打开零件失败");
                        var tmp = Path.Combine(Path.GetTempPath(), "swkit_dfm_" + Guid.NewGuid().ToString("N") + ".step");
                        ses.App.ActivateDoc3(doc.GetTitle(), false, 1, ref e);
                        doc.Extension.SaveAs(tmp, 0, 1, null, ref e, ref w);
                        ses.App.CloseDoc(doc.GetTitle());
                        if (!File.Exists(tmp)) throw new InvalidOperationException("导出 STEP 失败");
                        input = StepCache.Put(path, tmp, null);
                    }
                }
                Console.WriteLine($"STEP：{input}（{t0.Elapsed.TotalSeconds:0.0} 秒）");
            }
            var mesh = DfmMesh.Load(input, Console.WriteLine);
            mesh.Source = path;
            Console.WriteLine($"读入 {t0.Elapsed.TotalSeconds:0.0} 秒；网格{(mesh.Closed ? "封闭" : "不封闭")}；包围盒 {(mesh.Max - mesh.Min)} mm；面：平面 {mesh.Faces.Count(f => f.Kind == FaceKind.Plane)}、圆柱 {mesh.Faces.Count(f => f.Kind == FaceKind.Cylinder)}、其他 {mesh.Faces.Count(f => f.Kind == FaceKind.Other)}");
            var t1 = System.Diagnostics.Stopwatch.StartNew();
            var an = new DfmAnalyzer(mesh);
            var r = an.Analyze(s, up, Console.WriteLine, CancellationToken.None);
            Console.WriteLine($"分析 {t1.Elapsed.TotalSeconds:0.00} 秒（{DfmReport.ProcessName(r.Process)}）");
            foreach (var m in r.Metrics) Console.WriteLine($"  {m.Name}：{m.Value}{(m.Note != null ? "（" + m.Note + "）" : "")}");
            if (r.Candidates.Count > 0)
            {
                Console.WriteLine("推荐摆放：");
                foreach (var c in r.Candidates) Console.WriteLine($"  {c.Label}：支撑 {c.SupportVolume:0} mm³，高 {c.Height:0.#} mm，底面 {c.BaseArea:0} mm²，得分 {c.Score:0.###}");
            }
            if (r.Setups.Count > 0) Console.WriteLine("装夹方向：" + string.Join("、", r.Setups.Select(a => $"({a.X:0.##},{a.Y:0.##},{a.Z:0.##})")));
            Console.WriteLine($"问题 {r.Issues.Count} 个：");
            foreach (var i in r.Issues) Console.WriteLine($"  [{DfmReport.Sev(i.Severity)}] {i.Kind}｜{i.Title}｜{i.Value}｜位置 {i.Location}\n      建议：{i.Suggestion}");
            if (report != null)
            {
                var doc = DfmReport.Build(mesh, r, s, null);
                Directory.CreateDirectory(Path.GetDirectoryName(report));
                DocxWriter.Save(doc, report);
                MarkdownWriter.Save(doc, Path.ChangeExtension(report, ".md"));
                Console.WriteLine("报告：" + report);
            }
            return 0;
        }
    }
}
