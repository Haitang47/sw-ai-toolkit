using System;
using System.Collections.Generic;
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
    /// swkit-cli dfm &lt;零件.step|.stp|.stl|.sldprt&gt; [工艺] [--process fdm|sla|cnc|sls|slm|sheet|cut|mold] [--material 名称] [--machine 名称]
    ///   [--method router|laser|waterjet] [--cutter mm] [--qty 数量] [--draft 度] [--up x,y,z] [--overhang 45] [--min-wall mm] [--min-hole mm] [--tilts] [--report 报告.docx]
    /// 可制造性检查（命令行版）。.sldprt 会在隔离的后台 SolidWorks 里转成 STEP（有缓存）。
    /// </summary>
    static class DfmCli
    {
        public const string Usage = "用法：swkit-cli dfm <零件.step|.stp|.stl|.sldprt> [工艺：" + DfmProcesses.Ids + "] [--material 名称] [--machine 设备] [--method router|laser|waterjet] " +
                                    "[--cutter mm] [--qty 数量] [--draft 度] [--up x,y,z（打印摆放 / 注塑开模方向）] [--overhang 45] [--min-wall mm] [--min-hole mm] [--tilts] [--report 报告.docx]";

        public static int Run(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine(Usage); return 2; }
            var path = Path.GetFullPath(args[0]);
            var s = new DfmSettings();
            Vec3? up = null; string report = null;
            string material = null, machine = null; double? minWall = null;
            double Num(string x) => double.Parse(x, CultureInfo.InvariantCulture);
            for (int i = 1; i < args.Length; i++)
                switch (args[i])
                {
                    case "--process": s.Process = DfmProcesses.Parse(args[++i]) ?? throw new ArgumentException("不认识的工艺：" + args[i] + "（可选：" + DfmProcesses.Ids + "）"); break;
                    case "--up": case "--pull": var v = args[++i].Split(',').Select(Num).ToArray(); up = new Vec3(v[0], v[1], v[2]); break;
                    case "--overhang": s.OverhangDeg = Num(args[++i]); break;
                    case "--min-wall": minWall = Num(args[++i]); break;
                    case "--min-hole": s.MinHoleUser = Num(args[++i]); break;
                    case "--tilts": s.FiveAxisTilts = true; break;
                    case "--material": material = args[++i]; break;
                    case "--machine": case "--printer": machine = args[++i]; break;
                    case "--method":
                        var mt = args[++i].ToLowerInvariant();
                        s.CutMethod = mt.StartsWith("laser") || mt.Contains("激光") ? CutMethod.Laser : mt.StartsWith("water") || mt.Contains("水刀") ? CutMethod.Waterjet : CutMethod.Router;
                        break;
                    case "--cutter": s.CutterDia = Num(args[++i]); break;
                    case "--qty": s.Quantity = s.MoldQuantity = Math.Max(1, (int)Num(args[++i])); break;
                    case "--draft": s.MinDraft = Num(args[++i]); break;
                    case "--report": report = Path.GetFullPath(args[++i]); break;
                    default:
                        if (!args[i].StartsWith("--") && DfmProcesses.Parse(args[i]) is DfmProcess p) s.Process = p;
                        else { Console.WriteLine("不认识的参数：" + args[i]); Console.WriteLine(Usage); return 2; }
                        break;
                }
            if (s.Process == DfmProcess.Sla) { s.PrintMaterial = PrintMaterial.All.First(x => x.Resin); s.Printer = Printer.All.First(x => x.Resin); }
            if (s.Process == DfmProcess.Mold && !Array.Exists(args, a => a == "--qty")) s.MoldQuantity = 1000;
            if (minWall.HasValue) { if (s.Process == DfmProcess.Cnc) s.MinWallCnc = minWall.Value; else s.MinWallPrint = minWall.Value; }
            if (material != null) s.SetMaterial(material);
            if (machine != null) s.SetMachine(machine);

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
            Console.WriteLine($"读入 {t0.Elapsed.TotalSeconds:0.0} 秒；网格{(mesh.Closed ? "封闭" : "不封闭")}；包围盒 {(mesh.Max - mesh.Min)} mm；面：平面 {mesh.Faces.Count(f => f.Kind == FaceKind.Plane)}、圆柱 {mesh.Faces.Count(f => f.Kind == FaceKind.Cylinder)}、其他 {mesh.Faces.Count(f => f.Kind == FaceKind.Other)}" +
                              (mesh.Shells.Count > 1 ? $"；{mesh.Shells.Count} 个壳（空腔 {mesh.Shells.Count(x => x.Cavity)}）" : ""));
            var t1 = System.Diagnostics.Stopwatch.StartNew();
            var an = new DfmAnalyzer(mesh);
            var r = an.Analyze(s, up, Console.WriteLine, CancellationToken.None);
            Console.WriteLine($"分析 {t1.Elapsed.TotalSeconds:0.00} 秒（{DfmReport.ProcessName(r.Process)}，材料 {s.MaterialName}）");
            foreach (var m in r.Metrics) Console.WriteLine($"  {m.Name}：{m.Value}{(m.Note != null ? "（" + m.Note + "）" : "")}");
            if (r.Candidates.Count > 0)
            {
                Console.WriteLine(r.Process == DfmProcess.Mold ? "候选开模方向：" : "推荐摆放：");
                foreach (var c in r.Candidates)
                    Console.WriteLine(r.Process == DfmProcess.Mold ? $"  {c.Label}：倒扣 {c.SupportArea:0} mm²，深 {c.Height:0.#} mm"
                                    : r.Process == DfmProcess.Sls ? $"  {c.Label}：高 {c.Height:0.#} mm{(c.Fits ? "" : "，放不下")}"
                                    : $"  {c.Label}：支撑 {c.SupportVolume:0} mm³，高 {c.Height:0.#} mm，底面 {c.BaseArea:0} mm²，得分 {c.Score:0.###}");
            }
            if (r.Setups.Count > 0) Console.WriteLine("装夹方向：" + string.Join("、", r.Setups.Select(a => $"({a.X:0.##},{a.Y:0.##},{a.Z:0.##})")));
            foreach (var n in r.Notes) Console.WriteLine("提示：" + n);
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
