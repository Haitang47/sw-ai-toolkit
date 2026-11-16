using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SwKit.Core;
using SwKit.Core.Parts;
using SwKit.Core.Urdf;

namespace SwKit.Cli
{
    /// <summary>
    /// swkit-cli parts list
    /// swkit-cli parts make &lt;类型&gt; [参数=值 ...] [--out 文件夹] [--overwrite]
    /// swkit-cli parts check                     （不需要 SolidWorks：检查每种零件的几何和预览网格）
    /// swkit-cli parts selftest &lt;文件夹&gt; [类型 ...]  （在后台 SolidWorks 里生成代表性零件，核对体积、包围盒、实体数）
    /// swkit-cli parts gear-plan a=中心距 i=传动比 [z1=] [m=]   （不需要 SolidWorks：列出齿轮副方案）
    /// swkit-cli parts asm &lt;gear_pair|profile_frame|belt_drive&gt; [参数=值 ...] [--out 文件夹] [--no-mates]
    /// swkit-cli parts asmtest &lt;文件夹&gt;           （生成齿轮副、框架、同步带三个装配体，重新打开核对位置和干涉）
    /// </summary>
    static class PartsCli
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("用法：swkit-cli parts list | make <类型> [参数=值 ...] [--out 文件夹] | check | selftest <文件夹> [类型 ...] | gear-plan a= i= | asm <生成器> [参数=值 ...] | asmtest <文件夹>"); return 2; }
            switch (args[0])
            {
                case "list": return List();
                case "check": return Check();
                case "make": return Make(args.Skip(1).ToArray());
                case "selftest": return SelfTest(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "swkit_parts_selftest"), args.Skip(2).ToArray());
                case "axistest": return AxisTest(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "swkit_parts_axistest"));
                case "gear-plan": return GearPlan(args.Skip(1).ToArray());
                case "asm": return Asm(args.Skip(1).ToArray());
                case "asmtest": return AsmTest(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "swkit_asm_test"));
                default: Console.Error.WriteLine("未知子命令：" + args[0]); return 2;
            }
        }

        static int List()
        {
            foreach (var t in Catalog.All.Concat(Assemblies.All))
            {
                Console.WriteLine($"{t.Id,-14} {t.Category} / {t.Name} {t.Standard}");
                foreach (var pd in t.Params)
                    Console.WriteLine($"    {pd.Key,-7} {pd.Label}：" + (pd.Kind == ParamKind.Choice ? string.Join(" | ", pd.Choices(t.Defaults())) : $"{pd.Min}–{pd.Max} {pd.Unit}") + $"（默认 {pd.Default(t.Defaults())}）");
            }
            return 0;
        }

        static Dictionary<string, string> KeyValues(IEnumerable<string> args) =>
            args.Where(a => a.Contains("=") && !a.StartsWith("--")).Select(a => a.Split(new[] { '=' }, 2)).ToDictionary(kv => kv[0], kv => kv[1]);

        static int GearPlan(string[] args)
        {
            var kv = KeyValues(args);
            double D(string k, double def) => kv.TryGetValue(k, out var v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : def;
            var list = Assemblies.PlanGearPair(D("a", 30) / 1000, D("i", 2), (int)D("z1", 0), D("m", 0));
            Console.WriteLine($"中心距 {D("a", 30)} mm，传动比 {D("i", 2)}：{list.Count} 个方案");
            foreach (var o in list)
                Console.WriteLine($"  {o.Label,-36} 误差 {o.RatioError,7:P2}  标准中心距 {o.A0 * 1000:0.###}" + (o.Undercut ? "  根切" : "") + $"  评分 {o.Score:0.00}");
            return list.Count > 0 ? 0 : 1;
        }

        static int Asm(string[] args)
        {
            if (args.Length == 0) { Console.Error.WriteLine("缺少生成器：gear_pair | profile_frame | belt_drive"); return 2; }
            var t = Assemblies.Find(args[0]);
            if (t == null) { Console.Error.WriteLine("未知生成器：" + args[0]); return 2; }
            var p = t.Defaults();
            foreach (var kv in KeyValues(args.Skip(1))) p[kv.Key] = kv.Value;
            t.Normalize(p);
            var outDir = PartsLibrary.DefaultRoot;
            var i = Array.IndexOf(args, "--out"); if (i >= 0 && i + 1 < args.Length) outDir = args[i + 1];
            var plan = t.Plan(p);
            foreach (var s in plan.Info) Console.WriteLine("  " + s);
            foreach (var s in plan.Warnings) Console.WriteLine("  注意：" + s);
            using (var s = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
            {
                var r = SwAssemblyBuilder.Build(s.App, plan, outDir, !args.Contains("--no-mates"), Console.WriteLine);
                Console.WriteLine($"{r.Path}（{r.Components} 个零部件，新生成 {r.NewParts} 个零件，{r.Seconds:0.0} 秒）{(r.MateNote != null ? "，" + r.MateNote : "")}");
                return r.Interferences > 0 || r.MaxPositionError > 1e-6 ? 1 : 0;
            }
        }

        /// <summary>齿轮副（标准 + 变位 + 安装板）、型材框架（带角码）、同步带三个装配体：生成、保存、重新打开核对位置，干涉检查必须为 0。</summary>
        static int AsmTest(string dir)
        {
            Directory.CreateDirectory(dir);
            var cases = new List<(AsmType t, Dictionary<string, string> set)>
            {
                (Assemblies.GearPair, new Dictionary<string, string> { ["a"] = "30", ["i"] = "2" }),
                (Assemblies.GearPair, new Dictionary<string, string> { ["a"] = "41.3", ["i"] = "3", ["b"] = "6", ["plate"] = "22", ["d1"] = "5", ["d2"] = "8" }),
                (Assemblies.Frame, new Dictionary<string, string> { ["l"] = "400", ["w"] = "300", ["h"] = "250", ["series"] = "2020" }),
                (Assemblies.BeltDrive, new Dictionary<string, string> { ["z1"] = "20", ["z2"] = "60", ["c"] = "120" }),
            };
            int bad = 0;
            using (var s = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
            {
                foreach (var (t, set) in cases)
                {
                    var p = t.Defaults();
                    foreach (var kv in set) p[kv.Key] = kv.Value;
                    t.Normalize(p);
                    var plan = t.Plan(p);
                    Console.WriteLine($"== {plan.Title}");
                    foreach (var line in plan.Info) Console.WriteLine("   " + line);
                    try
                    {
                        var r = SwAssemblyBuilder.Build(s.App, plan, dir, true, null);
                        var back = SwAssemblyBuilder.ReadBack(s.App, r.Path);
                        // 重新打开后的零部件位置：和方案里的原点逐个配对（按位置最近）
                        double worst = 0;
                        var want = plan.Parts.Select(x => x.T).ToList();
                        foreach (var (name, o) in back)
                        {
                            var d = want.Min(x => (x - o).Length);
                            worst = Math.Max(worst, d);
                        }
                        var ok = back.Count == plan.Parts.Count && worst < 1e-6 && r.Interferences == 0;
                        if (!ok) bad++;
                        Console.WriteLine($"   {(ok ? "通过" : "失败")}：{back.Count} 个零部件，重新打开后位置最大偏差 {worst * 1000:0.####} mm，干涉 {r.Interferences} 处" +
                                          (r.Interferences > 0 ? $"（{r.InterferenceVolume * 1e9:0.###} mm³）" : "") + $"，{r.MateNote ?? "全部固定"}，{r.Seconds:0.0} 秒");
                        Console.WriteLine("   " + r.Path);
                        if (t == Assemblies.GearPair)
                        {
                            var g = back.Where(b => b.name.Contains("直齿轮")).Select(b => b.origin).ToList();
                            if (g.Count == 2) Console.WriteLine($"   齿轮中心距（重新打开后量）：{(g[0] - g[1]).Length * 1000:0.####} mm");
                        }
                    }
                    catch (Exception ex) { bad++; Console.WriteLine("   失败：" + ex.Message); }
                }

                // 对照：故意把齿轮 2 的相位转错（齿对齿），干涉检查必须能查出来——证明上面的「0 处干涉」有意义
                {
                    var p = Assemblies.GearPair.Defaults(); p["a"] = "30"; p["i"] = "2"; Assemblies.GearPair.Normalize(p);
                    var plan = Assemblies.GearPair.Plan(p);
                    plan.Title += " 相位错误对照";
                    var g2 = plan.Parts.First(x => x.Role == "gear2");
                    g2.RotY(0);
                    plan.Mates = null;
                    var r = SwAssemblyBuilder.Build(s.App, plan, dir, false, null);
                    var ok = r.Interferences > 0;
                    if (!ok) bad++;
                    Console.WriteLine($"== 对照（齿对齿）：干涉 {r.Interferences} 处（{r.InterferenceVolume * 1e9:0.###} mm³）→ {(ok ? "能查出来，检查有效" : "没查出来，检查无效！")}");
                    try { File.Delete(r.Path); } catch { }
                }

                // 截图：在后台 SolidWorks 里打开生成的装配体，等轴测、缩放到合适，另存为 PNG
                foreach (var f in Directory.GetFiles(Path.Combine(dir, "装配"), "*.SLDASM"))
                {
                    int e = 0, w = 0;
                    var doc = s.App.OpenDoc6(f, 2, 1 | 2, "", ref e, ref w) as SolidWorks.Interop.sldworks.ModelDoc2;
                    if (doc == null) continue;
                    try
                    {
                        s.App.ActivateDoc3(doc.GetTitle(), false, 0, ref e);
                        doc.ShowNamedView2("*Isometric", 7);
                        doc.ViewZoomtofit2();
                        var png = Path.ChangeExtension(f, ".png");
                        doc.Extension.SaveAs(png, 0, 1, null, ref e, ref w);
                        Console.WriteLine("截图：" + (File.Exists(png) ? png : "没有生成（错误码 " + e + "）"));
                    }
                    finally { s.App.CloseDoc(doc.GetTitle()); }
                }
            }
            Console.WriteLine(bad == 0 ? "装配生成全部通过" : $"{bad} 项失败");
            return bad == 0 ? 0 : 1;
        }

        /// <summary>代表性参数组合：每种零件的默认值，加上每个可选参数的最小和最大选项。</summary>
        static IEnumerable<Dictionary<string, string>> Samples(PartType t)
        {
            var seen = new HashSet<string>();
            var list = new List<Dictionary<string, string>> { t.Defaults() };
            foreach (var pd in t.Params.Where(x => x.Kind == ParamKind.Choice))
            {
                foreach (var pick in new Func<string[], string>[] { c => c.First(), c => c.Last() })
                {
                    var p = t.Defaults();
                    p[pd.Key] = pick(pd.Choices(p));
                    t.Normalize(p);
                    list.Add(p);
                }
            }
            if (t.Id == "spur_gear")
            {
                list.Add(new Dictionary<string, string> { ["m"] = "1", ["z"] = "60", ["b"] = "6", ["bore"] = "D 形孔", ["d"] = "5", ["hubD"] = "14", ["hubL"] = "6", ["bl"] = "0.15", ["mat"] = "PLA" });
                list.Add(new Dictionary<string, string> { ["m"] = "0.5", ["z"] = "12", ["b"] = "3", ["bore"] = "无孔", ["d"] = "1", ["hubD"] = "0", ["hubL"] = "0", ["bl"] = "0", ["mat"] = "不设置" });
                // 变位齿轮（齿轮副按中心距凑整时会用到）
                list.Add(new Dictionary<string, string> { ["m"] = "1", ["z"] = "14", ["b"] = "6", ["bore"] = "圆孔", ["d"] = "4", ["hubD"] = "0", ["hubL"] = "0", ["bl"] = "0.05", ["x"] = "0.5", ["mat"] = "钢" });
                list.Add(new Dictionary<string, string> { ["m"] = "0.8", ["z"] = "78", ["b"] = "6", ["bore"] = "圆孔", ["d"] = "8", ["hubD"] = "0", ["hubL"] = "0", ["bl"] = "0.05", ["x"] = "-0.3", ["mat"] = "钢" });
            }
            foreach (var p in list)
            {
                t.Normalize(p);
                if (seen.Add(t.Title(p))) yield return p;
            }
        }

        static int Check()
        {
            int bad = 0;
            foreach (var t in Catalog.All)
            {
                foreach (var p in Samples(t))
                {
                    try
                    {
                        var g = t.Build(p);
                        var v = g.Volume();
                        // 三角剖分面积 = 外轮廓面积 − 孔的面积
                        double worst = 0;
                        foreach (var op in g.Ops.Where(o => o.Kind == OpKind.Extrude))
                        {
                            var outer = op.Loops[0].Tessellate();
                            if (Loop.SignedArea(outer) < 0) outer.Reverse();
                            var holes = op.Loops.Skip(1).Select(l => { var h = l.Tessellate(); if (Loop.SignedArea(h) > 0) h.Reverse(); return h; }).ToList();
                            var tri = PartMesher.Triangulate(outer, holes).Sum(x => Math.Abs((x.Item2.X - x.Item1.X) * (x.Item3.Y - x.Item1.Y) - (x.Item2.Y - x.Item1.Y) * (x.Item3.X - x.Item1.X)) / 2);
                            var want = Math.Abs(Loop.SignedArea(outer)) - holes.Sum(h => Math.Abs(Loop.SignedArea(h)));
                            worst = Math.Max(worst, Math.Abs(tri - want) / want);
                        }
                        var mesh = PartMesher.Build(g);
                        var ok = worst < 1e-6 && v > 0 && mesh.Body.Triangles > 0;
                        if (!ok) bad++;
                        Console.WriteLine($"{(ok ? "通过" : "失败")}  {t.Title(p),-40} 体积 {v * 1e9,10:0.0} mm³  预览 {mesh.Body.Triangles,6} 个三角形  剖分误差 {worst:P4}");
                    }
                    catch (Exception ex) { bad++; Console.WriteLine($"失败  {t.Title(p),-40} {ex.Message}"); }
                }
            }
            Console.WriteLine(bad == 0 ? "全部通过" : $"{bad} 项失败");
            return bad == 0 ? 0 : 1;
        }

        static int Make(string[] args)
        {
            if (args.Length == 0) { Console.Error.WriteLine("缺少零件类型，见 swkit-cli parts list"); return 2; }
            var t = Catalog.All.FirstOrDefault(x => x.Id == args[0]);
            if (t == null) { Console.Error.WriteLine("未知零件类型：" + args[0]); return 2; }
            var p = t.Defaults();
            string outDir = PartsLibrary.DefaultRoot; bool overwrite = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--out") outDir = args[++i];
                else if (args[i] == "--overwrite") overwrite = true;
                else if (args[i].Contains("=")) { var kv = args[i].Split(new[] { '=' }, 2); p[kv[0]] = kv[1]; }
            }
            t.Normalize(p);
            using (var s = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
            {
                var r = SwPartBuilder.Build(s.App, t, p, outDir, overwrite, Console.WriteLine);
                Print(t, p, r);
                return r.VolumeError < 0.01 ? 0 : 1;
            }
        }

        static void Print(PartType t, Dictionary<string, string> p, PartBuildResult r) =>
            Console.WriteLine($"{(r.Reused ? "已存在" : r.VolumeError < 0.01 ? "通过" : "体积不符")}  {t.Title(p),-40} " +
                              (r.Reused ? "" : $"体积 {r.Volume * 1e9:0.0} / 理论 {r.Expected * 1e9:0.0} mm³（差 {r.VolumeError:P3}）  {r.Seconds:0.0} 秒  材料 {r.Material ?? "-"}{(r.MaterialNote != null ? "（" + r.MaterialNote + "）" : "")}"));

        /// <summary>验证三个方向的拉伸 / 切除在 SolidWorks 里的坐标映射：零件故意做成不对称的，比较包围盒和体积。</summary>
        static int AxisTest(string dir)
        {
            Directory.CreateDirectory(dir);
            const double MM = 0.001;
            var t = new PartType
            {
                Id = "axis_test", Category = "测试", Name = "方向测试", Params = { },
                Size = _ => "A",
                Build = _ => new PartGeom()
                    .Add(Op.Extrude("底板", 0, 5 * MM, Loop.PolygonMm(0, 0, 40, 0, 40, 20, 0, 20)))
                    .Add(Op.Extrude("Z 向块", -20 * MM, 4 * MM, Loop.PolygonMm(0, 5, 10, 5, 10, 30, 0, 30)).Along(Axis.Z))
                    .Add(Op.Extrude("X 向块", 40 * MM, 6 * MM, Loop.PolygonMm(2, 0, 12, 0, 12, 5, 2, 5)).Along(Axis.X))
                    .Add(Op.Cut("Y 孔", 5 * MM, 5 * MM, Loop.Circle(V2.Mm(30, 10), 2 * MM)))
                    .Add(Op.Cut("Z 孔", -16 * MM, 4 * MM, Loop.Circle(V2.Mm(5, 20), 2 * MM)).Along(Axis.Z))
                    .Add(Op.Cut("X 孔", 46 * MM, 6 * MM, Loop.Circle(V2.Mm(7, 2.5), 1 * MM)).Along(Axis.X)),
            };
            var p = new Dictionary<string, string>();
            var mesh = PartMesher.Build(t.Build(p));
            mesh.Body.Bounds(out var mn, out var mx);
            Console.WriteLine($"预览包围盒 {F3(mn)} – {F3(mx)}（应为 X 0..46、Y 0..30、Z −20..0）");
            using (var s = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
            {
                var r = SwPartBuilder.Build(s.App, t, p, dir, true, Console.WriteLine);
                Print(t, p, r);
                Console.WriteLine($"SolidWorks 包围盒 {F3(r.BoxMin)} – {F3(r.BoxMax)}");
                var ok = r.VolumeError < 0.001 && (r.BoxMin - mn).Length < 2e-4 && (r.BoxMax - mx).Length < 2e-4;
                Console.WriteLine(ok ? "方向映射正确" : "方向映射不一致！");
                return ok ? 0 : 1;
            }
        }

        static string F3(Vec3 v) => $"({v.X * 1000:0.###}, {v.Y * 1000:0.###}, {v.Z * 1000:0.###})";

        /// <summary>
        /// 在后台 SolidWorks 里逐个生成代表性零件，核对三件事：体积和理论值相差 &lt; 0.5%、
        /// 包围盒和预览网格一致（检查坐标映射没有镜像或错位）、只有一个实体（几何连成一体）。
        /// ids 不为空时只测这些类型。
        /// </summary>
        static int SelfTest(string dir, string[] ids = null)
        {
            Directory.CreateDirectory(dir);
            int bad = 0, total = 0;
            double worstVol = 0;
            var sw = Stopwatch.StartNew();
            using (var s = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
            {
                foreach (var t in Catalog.All.Where(x => ids == null || ids.Length == 0 || ids.Contains(x.Id)))
                {
                    foreach (var p in Samples(t))
                    {
                        total++;
                        try
                        {
                            var r = SwPartBuilder.Build(s.App, t, p, dir, true, null);
                            var mesh = PartMesher.Build(t.Build(p));
                            mesh.Body.Bounds(out var mn, out var mx);
                            var tol = 0.004 * (mx - mn).Length + 5e-5;
                            var boxErr = Math.Max((r.BoxMin - mn).Length, (r.BoxMax - mx).Length);
                            var ok = r.VolumeError < 0.005 && boxErr < tol && r.Bodies == 1;
                            worstVol = Math.Max(worstVol, r.VolumeError);
                            Console.WriteLine($"{(ok ? "通过" : "失败")}  {t.Title(p),-40} 体积差 {r.VolumeError:P3}  包围盒差 {boxErr * 1000:0.###} mm  实体 {r.Bodies}  {r.Seconds:0.0} 秒  材料 {r.Material ?? "-"}");
                            if (!ok) { bad++; Console.WriteLine($"      SolidWorks 包围盒 {F3(r.BoxMin)} – {F3(r.BoxMax)}，预览 {F3(mn)} – {F3(mx)}"); }
                        }
                        catch (Exception ex) { bad++; Console.WriteLine($"失败  {t.Title(p),-40} {ex.Message}"); }
                    }
                }
            }
            Console.WriteLine($"{total} 个零件，{bad} 个失败，最大体积差 {worstVol:P3}，用时 {sw.Elapsed.TotalSeconds:0} 秒");
            return bad == 0 ? 0 : 1;
        }
    }
}
