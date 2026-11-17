using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SwKit.Core.Fea;
using SwKit.Core.Urdf;

namespace SwKit.Cli
{
    /// <summary>
    /// swkit-cli fea bench            用有解析解的算例验证求解器（拉杆、悬臂梁、带孔板、扭转轴、自重悬臂梁）
    /// swkit-cli fea faces &lt;零件.step&gt; [尺寸mm]   划网格并列出所有面（编号、面积、形心），给 run 选面用
    /// swkit-cli fea run &lt;零件.step&gt; --material 名称 --fix 面,面 --force 面,面:Fx,Fy,Fz [--size mm]
    /// </summary>
    static class FeaCli
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("用法：swkit-cli fea bench | materials | faces <零件.step> [网格尺寸mm]\n" +
                                  "  swkit-cli fea run <零件.step> --fix 面号,... --force 面号,...:Fx,Fy,Fz [--torque 面号,...:ax,ay,az:N·m] [--gravity x,y,z:倍数] [--material 名称] [--size mm] [--curv 每圈单元数]\n" +
                                  "      多工况：--case 名称[:权重]，之后的载荷属于这个工况（结果给出包络和每个工况；拓扑优化同时兼顾）\n" +
                                  "      拓扑优化：--topo 保留比例 [--mode auto|plate|3d] [--detail 单元数] [--keep-layer y:最小mm:最大mm（凸边布局）] [--stl 输出] [--png 俯视图]");
                return 2;
            }
            switch (args[0])
            {
                case "bench": return Bench();
                case "faces": return Faces(args[1], args.Length > 2 ? double.Parse(args[2]) / 1000 : 0);
                case "run": return RunCase(args.Skip(1).ToArray());
                case "sample": return SampleCase(args.Skip(1).ToArray());
                case "materials":
                    foreach (var g in FeMaterial.All.GroupBy(x => x.Category))
                    {
                        Console.WriteLine(g.Key);
                        foreach (var x in g) Console.WriteLine($"  {x.Name,-22} E {x.E / 1e9,6:0.#} GPa  ν {x.Nu:0.00}  {(x.Brittle ? "抗拉" : "屈服")} {x.Strength / 1e6,5:0} MPa  ρ {x.Density,5:0}  {(x.Brittle ? "脆性（主应力判据）" : "")}");
                    }
                    return 0;
                default: Console.Error.WriteLine("未知子命令：" + args[0]); return 2;
            }
        }

        /// <summary>
        /// swkit-cli fea sample bracket|cantilever [--size mm] [--adaptive 目标误差] [--topo 保留比例] [--stl 输出.stl]
        /// 用自带的示例零件测：普通计算、自适应加密、拓扑优化。
        /// </summary>
        static int SampleCase(string[] args)
        {
            var s = FeSamples.All.First(x => x.Id == args[0]);
            var step = FeSamples.PathOf(s) ?? throw new FileNotFoundException("找不到示例文件夹");
            double size = 0, adaptive = 0, topo = 0, filter = 2.2; string stl = null; int iters = 50;
            for (int i = 1; i < args.Length; i++)
                switch (args[i])
                {
                    case "--size": size = double.Parse(args[++i]) / 1000; break;
                    case "--adaptive": adaptive = double.Parse(args[++i]); break;
                    case "--topo": topo = double.Parse(args[++i]); break;
                    case "--filter": filter = double.Parse(args[++i]); break;
                    case "--iters": iters = int.Parse(args[++i]); break;
                    case "--stl": stl = args[++i]; break;
                }
            Gmsh.Bounds(step, out var bmin, out var bmax);
            if (size <= 0) size = (bmax - bmin).Length / 24;
            Console.WriteLine($"示例 {s.Name}：包围盒 {(bmax - bmin) * 1000} mm，网格尺寸 {size * 1000:0.##} mm");
            var model = new FeModel { Material = FeMaterial.All.First(x => x.Name == s.Material), Mesh = Gmsh.Mesh(step, size, Console.WriteLine) };
            FeSamples.Setup(s, model.Mesh, model.FixedFaces, model.Loads);
            Console.WriteLine($"固定面 {string.Join(",", model.FixedFaces)}；载荷 {string.Join("; ", model.Loads.Select(l => $"{l.Kind} {l.Value} 面 {string.Join(",", l.Faces)}"))}");
            if (topo > 0)
            {
                // 拓扑优化用更细的网格（线性四面体约 6 万个）
                model.Mesh = TopoOpt.MeshFor(step, model.Mesh, 60000, Console.WriteLine, out _);
                model.FixedFaces.Clear(); model.Loads.Clear();
                FeSamples.Setup(s, model.Mesh, model.FixedFaces, model.Loads);
                var r = TopoOpt.Run(model, new TopoSettings { VolumeFraction = topo, FilterFactor = filter, MaxIterations = iters }, Console.WriteLine, null, CancellationToken.None);
                Console.WriteLine($"拓扑优化：{r.Iterations} 轮，{r.Seconds:0.0} 秒；保留 {r.VolumeFraction:P1}，刚度为实心的 {r.StiffnessRatio:P1}；外形 {r.Surface.Length / 9:N0} 个三角形，体积 {TopoSurface.Volume(r.Surface) * 1e9:N0} mm³（实心 {model.Mesh.Elements} 单元）");
                if (stl != null) { TopoSurface.WriteStl(r.Surface, stl); Console.WriteLine("已写出 " + stl); }
                return 0;
            }
            var res = FeSolver.Solve(model, Console.WriteLine, CancellationToken.None);
            PrintSample(model, res);
            if (adaptive > 0)
            {
                var (m2, r2) = FeAdaptive.Run(model, res, step, size, adaptive, 3, 120000, Console.WriteLine, null, CancellationToken.None);
                Console.WriteLine("自适应加密：");
                foreach (var p in r2.Passes)
                    Console.WriteLine($"  {p.Elements,8:N0} 单元  最大应力 {p.MaxVm / 1e6,8:0.##} MPa  离开约束 {p.MaxVmAway / 1e6,8:0.##} MPa  估计误差 {p.Eta,6:P1}  {p.Seconds,5:0.0} 秒");
                model.Mesh = m2;
                PrintSample(model, r2);
            }
            return 0;
        }

        static void PrintSample(FeModel model, FeResult r)
        {
            Console.WriteLine($"最大等效应力 {r.MaxVm / 1e6:0.###} MPa（{model.Mesh.P(r.MaxVmNode) * 1000} mm），离开约束 {r.MaxVmAway / 1e6:0.###} MPa；最大主应力 {r.MaxP1 / 1e6:0.###} MPa");
            Console.WriteLine($"最大位移 {r.MaxDisp * 1000:0.####} mm，安全系数 {r.SafetyFactor:0.##}，估计误差 {r.ErrorEstimate:P2}，平衡误差 {r.EquilibriumError:P4}，{r.Iterations} 次迭代，{r.Seconds:0.0} 秒");
            foreach (var n in r.Notes) Console.WriteLine("  注：" + n);
            if (r.Timing != null) Console.WriteLine("  耗时：" + r.Timing);
        }

        static double AutoSize(FeMesh probe) { probe.Bounds(out var a, out var b); return (b - a).Length / 25; }

        static int Faces(string step, double size)
        {
            var m = Gmsh.Mesh(Path.GetFullPath(step), size > 0 ? size : 0.005, Console.WriteLine);
            m.Bounds(out var mn, out var mx);
            Console.WriteLine($"包围盒 {(mx - mn) * 1000} mm，体积 {m.Volume() * 1e9:0.###} mm³");
            foreach (var f in m.Faces)
                Console.WriteLine($"  {f.Id,4}  {(f.IsPlane ? "平面" : "曲面")}  面积 {f.Area * 1e6,10:0.##} mm²  形心 {f.Centroid * 1000} mm  法向 {f.Normal}");
            return 0;
        }

        /// <summary>
        /// swkit-cli fea run &lt;零件.step&gt; --fix 面,面 --force 面,面:Fx,Fy,Fz --torque 面,面:ax,ay,az:N·m [--material 名称] [--size mm]
        ///   [--topo 保留比例 [--detail 单元数] [--mode auto|3d|plate] [--stl 输出.stl] [--iters n] [--png 俯视图.png]]
        /// 力、扭矩可以重复多次（例如四个电机各一组）。--gravity x,y,z:倍数 加惯性力。
        /// 多工况：--case 名称[:权重]，之后的载荷属于这个工况（例如 --case 满油门 … --case 偏航 … --case 硬着陆 …）；
        /// 静力分析给出包络和每个工况的结果，拓扑优化同时兼顾所有工况。
        /// </summary>
        static int RunCase(string[] args)
        {
            string step = Path.GetFullPath(args[0]); double size = 0; string mat = "6061-T6";
            double topo = 0; int detail = 60000, iters = 80; string stl = null, png = null, mode = "auto";
            Vec3 keepAxis = Vec3.Zero; double keepMin = double.NaN, keepMax = double.NaN;
            int curv = 16;   // 孔、圆角处每圈至少几个单元（孔很多的板可以调小，单元会少很多）
            var model = new FeModel();
            int curCase = 0;
            List<int> Ids(string s) => s.Split(',').Select(int.Parse).ToList();
            Vec3 V(string s) { var v = s.Split(',').Select(double.Parse).ToArray(); return new Vec3(v[0], v[1], v[2]); }
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--size": size = double.Parse(args[++i]) / 1000; break;
                    case "--material": mat = args[++i]; break;
                    case "--fix": model.FixedFaces.AddRange(Ids(args[++i])); break;
                    case "--force":
                    {
                        var parts = args[++i].Split(':');
                        var dir = V(parts[1]);
                        model.Loads.Add(new FeLoad { Kind = LoadKind.Force, Faces = Ids(parts[0]), Value = dir.Length, Dir = dir.Normalized(), Case = curCase });
                        break;
                    }
                    case "--torque":
                    {
                        var parts = args[++i].Split(':');
                        model.Loads.Add(new FeLoad { Kind = LoadKind.Torque, Faces = Ids(parts[0]), Dir = V(parts[1]).Normalized(), Value = double.Parse(parts[2]), Case = curCase });
                        break;
                    }
                    case "--gravity":
                    {
                        // 例如 0,-1,0:3 —— 沿 −Y 3 倍重力（惯性力）
                        var parts = args[++i].Split(':');
                        model.Loads.Add(new FeLoad { Kind = LoadKind.Gravity, Dir = V(parts[0]).Normalized(), Value = parts.Length > 1 ? double.Parse(parts[1]) : 1, Case = curCase });
                        break;
                    }
                    case "--case":
                    {
                        // 多工况：--case 名称[:权重]，之后的 --force / --torque / --gravity 属于这个工况；在第一个 --case 之前给的载荷算「工况 1」
                        var parts = args[++i].Split(':');
                        if (model.Cases.Count == 0 && model.Loads.Count > 0) model.Cases.Add(new FeLoadCase { Name = "工况 1" });
                        model.Cases.Add(new FeLoadCase { Name = parts[0], Weight = parts.Length > 1 ? double.Parse(parts[1]) : 1 });
                        curCase = model.Cases.Count - 1;
                        break;
                    }
                    case "--topo": topo = double.Parse(args[++i]); break;
                    case "--detail": detail = int.Parse(args[++i]); break;
                    case "--mode": mode = args[++i]; break;
                    case "--iters": iters = int.Parse(args[++i]); break;
                    case "--stl": stl = args[++i]; break;
                    case "--png": png = args[++i]; break;
                    case "--curv": curv = int.Parse(args[++i]); break;
                    case "--keep-layer":
                    {
                        // 例如 y:0:3 —— 沿 Y 坐标 0–3 mm 的板保留，两侧是凸边设计层
                        var kp = args[++i].Split(':');
                        keepAxis = kp[0].ToLowerInvariant() == "x" ? new Vec3(1, 0, 0) : kp[0].ToLowerInvariant() == "z" ? new Vec3(0, 0, 1) : new Vec3(0, 1, 0);
                        keepMin = double.Parse(kp[1]) / 1000; keepMax = double.Parse(kp[2]) / 1000;
                        break;
                    }
                }
            }
            model.Material = FeMaterial.All.FirstOrDefault(x => x.Name == mat) ?? FeMaterial.All.First(x => x.Name.Contains(mat));
            if (size <= 0) { Gmsh.Bounds(step, out var bmin, out var bmax); size = (bmax - bmin).Length / 25; }
            model.Mesh = Gmsh.Mesh(step, size, Console.WriteLine, curvature: curv);
            if (topo > 0)
            {
                model.Mesh = TopoOpt.MeshFor(step, model.Mesh, detail, Console.WriteLine, out _);
                var ts = new TopoSettings { VolumeFraction = topo, MaxIterations = iters, Mode = mode == "plate" ? TopoMode.Plate : mode == "3d" ? TopoMode.Solid : TopoMode.Auto,
                                            KeepLayerNormal = keepAxis, KeepLayerMin = keepMin, KeepLayerMax = keepMax };
                var r = TopoOpt.Run(model, ts, Console.WriteLine, null, CancellationToken.None);
                Console.WriteLine($"拓扑优化：{r.Iterations} 轮，{r.Seconds:0.0} 秒；{(r.PlateMode ? "板件模式（只开穿透孔）" : "三维模式")}；保留 {r.VolumeFraction:P1}，刚度为实心的 {r.StiffnessRatio:P1}；必须保留 {r.PassiveFraction:P1}");
                if (r.CaseNames != null) for (int c = 0; c < r.CaseNames.Length; c++) Console.WriteLine($"  「{r.CaseNames[c]}」：刚度为实心的 {r.CaseStiffness[c]:P1}");
                foreach (var w in r.Warnings) Console.WriteLine("  ⚠ " + w);
                if (stl != null) { TopoSurface.WriteStl(r.Surface, stl); Console.WriteLine("已写出 " + stl); }
                if (png != null)
                {
                    if (r.Side != null)
                    {
                        var top = Path.ChangeExtension(png, null) + "-上层.png"; var bot = Path.ChangeExtension(png, null) + "-下层.png";
                        TopoPreview.SaveTopView(model.Mesh, r, top, side: 1); TopoPreview.SaveTopView(model.Mesh, r, bot, side: -1);
                        Console.WriteLine("已写出 " + top + "、" + bot);
                    }
                    else { TopoPreview.SaveTopView(model.Mesh, r, png); Console.WriteLine("已写出 " + png); }
                }
                return 0;
            }
            var res = FeSolver.Solve(model, Console.WriteLine, CancellationToken.None);
            Print(model, res);
            return 0;
        }

        static void Print(FeModel model, FeResult r)
        {
            Console.WriteLine($"最大等效应力 {r.MaxVm / 1e6:0.###} MPa（位置 {model.Mesh.P(r.MaxVmNode) * 1000} mm），离开约束后 {r.MaxVmAway / 1e6:0.###} MPa{(r.MaxVmAwayNode >= 0 && r.MaxVmAwayNode != r.MaxVmNode ? $"（位置 {model.Mesh.P(r.MaxVmAwayNode) * 1000} mm）" : "")}");
            Console.WriteLine($"最大位移 {r.MaxDisp * 1000:0.####} mm，安全系数 {r.SafetyFactor:0.##}（{model.Material.Name} {(model.Material.Brittle ? "抗拉强度" : "屈服")} {model.Material.Strength / 1e6:0} MPa{(model.Material.Brittle ? "，按主应力判断" : "")}）");
            Console.WriteLine($"外载合力 {r.Applied} N，反力合力 {r.Reaction} N，平衡误差 {r.EquilibriumError:P4}；迭代 {r.Iterations} 次，{(r.Converged ? "收敛" : "未收敛")}，{r.Seconds:0.0} 秒");
            if (r.CaseResults != null)
                foreach (var c in r.CaseResults)
                    Console.WriteLine($"  「{c.CaseName}」：最大等效应力 {c.MaxVm / 1e6:0.###} MPa，离开约束后 {c.MaxVmAway / 1e6:0.###} MPa{(c.MaxVmAwayNode >= 0 ? $"（位置 {model.Mesh.P(c.MaxVmAwayNode) * 1000} mm）" : "")}，最大位移 {c.MaxDisp * 1000:0.####} mm，安全系数 {c.SafetyFactor:0.##}，外载 {c.Applied} N，平衡误差 {c.EquilibriumError:P3}");
            foreach (var n in r.Notes) Console.WriteLine("  注：" + n);
            if (r.Timing != null) Console.WriteLine("  耗时：" + r.Timing);
        }

        // ================= 解析解算例 =================

        sealed class Case { public string Name, Geo; public double Size; public Action<FeModel> Setup; public Func<FeModel, FeResult, List<(string what, double fe, double exact, double tol)>> Check; }

        static int Bench()
        {
            var steel = FeMaterial.All.First(x => x.Name == "Q235");
            double E = steel.E, nu = steel.Nu, G = E / (2 * (1 + nu));
            List<int> Where(FeMesh m, Func<FeFace, bool> p) => m.Faces.Where(p).Select(f => f.Id).ToList();
            IEnumerable<int> NodesWhere(FeMesh m, Func<Vec3, bool> p) => Enumerable.Range(0, m.Nodes).Where(n => p(m.P(n)));

            var cases = new List<Case>
            {
                new Case
                {
                    Name = "拉杆 100×10×10 mm，拉力 10 kN", Size = 0.0025,
                    Geo = "Box(1) = {0, 0, 0, 0.1, 0.01, 0.01};",
                    Setup = md =>
                    {
                        md.FixedFaces = Where(md.Mesh, f => f.Centroid.X < 1e-6);
                        md.Loads.Add(new FeLoad { Kind = LoadKind.Force, Faces = Where(md.Mesh, f => f.Centroid.X > 0.1 - 1e-6), Value = 10000, Dir = new Vec3(1, 0, 0) });
                    },
                    Check = (md, r) =>
                    {
                        var m = md.Mesh;
                        var tip = NodesWhere(m, p => p.X > 0.1 - 1e-6).Average(n => r.U[3 * n]);
                        var mid = NodesWhere(m, p => Math.Abs(p.X - 0.05) < 0.005).Average(n => r.VonMises[n]);
                        return new List<(string, double, double, double)> { ("自由端伸长 (mm)", tip * 1000, 10000 * 0.1 / (E * 1e-4) * 1000, 0.02), ("杆中部应力 (MPa)", mid / 1e6, 100, 0.01) };
                    },
                },
                new Case
                {
                    Name = "悬臂梁 200×20×10 mm，端部 500 N", Size = 0.004,
                    Geo = "Box(1) = {0, 0, 0, 0.2, 0.02, 0.01};",
                    Setup = md =>
                    {
                        md.FixedFaces = Where(md.Mesh, f => f.Centroid.X < 1e-6);
                        md.Loads.Add(new FeLoad { Kind = LoadKind.Force, Faces = Where(md.Mesh, f => f.Centroid.X > 0.2 - 1e-6), Value = 500, Dir = new Vec3(0, -1, 0) });
                    },
                    Check = (md, r) =>
                    {
                        var m = md.Mesh;
                        double I = 0.01 * Math.Pow(0.02, 3) / 12, A = 0.01 * 0.02;
                        var tip = -NodesWhere(m, p => p.X > 0.2 - 1e-6).Average(n => r.U[3 * n + 1]);
                        var exact = 500 * Math.Pow(0.2, 3) / (3 * E * I) + 500 * 0.2 / (5.0 / 6 * G * A);   // 弯曲 + 剪切变形
                        var mid = NodesWhere(m, p => Math.Abs(p.X - 0.1) < 0.002 && (p.Y < 1e-6 || p.Y > 0.02 - 1e-6)).Max(n => r.VonMises[n]);
                        return new List<(string, double, double, double)> { ("端部挠度 (mm)", tip * 1000, exact * 1000, 0.02), ("跨中上下表面弯曲应力 (MPa)", mid / 1e6, 500 * 0.1 * 0.01 / I / 1e6, 0.03) };
                    },
                },
                new Case
                {
                    Name = "带孔板 200×100×5 mm，孔 Ø20，名义拉应力 10 MPa", Size = 0.005,
                    Geo = "Box(1) = {0, 0, 0, 0.2, 0.1, 0.005};\nCylinder(2) = {0.1, 0.05, -0.001, 0, 0, 0.007, 0.01};\nBooleanDifference(3) = { Volume{1}; Delete; }{ Volume{2}; Delete; };",
                    Setup = md =>
                    {
                        md.FixedFaces = Where(md.Mesh, f => f.Centroid.X < 1e-6);
                        md.Loads.Add(new FeLoad { Kind = LoadKind.Force, Faces = Where(md.Mesh, f => f.Centroid.X > 0.2 - 1e-6), Value = 10e6 * 0.1 * 0.005, Dir = new Vec3(1, 0, 0) });
                    },
                    Check = (md, r) =>
                    {
                        // Heywood：Kt(净截面) = 2 + (1 − d/W)³
                        var kt = 2 + Math.Pow(1 - 0.2, 3);
                        var sigNet = 10e6 * 0.1 / (0.1 - 0.02);
                        var hole = NodesWhere(md.Mesh, p => Math.Abs(Math.Sqrt((p.X - 0.1) * (p.X - 0.1) + (p.Y - 0.05) * (p.Y - 0.05)) - 0.01) < 1e-5).Max(n => r.VonMises[n]);
                        return new List<(string, double, double, double)> { ("孔边最大应力 (MPa)", hole / 1e6, kt * sigNet / 1e6, 0.05), ("全局最大应力在孔边 (MPa)", r.MaxVm / 1e6, kt * sigNet / 1e6, 0.05) };
                    },
                },
                new Case
                {
                    Name = "扭转轴 Ø20×100 mm，扭矩 50 N·m", Size = 0.003,
                    Geo = "Cylinder(1) = {0, 0, 0, 0.1, 0, 0, 0.01};",
                    Setup = md =>
                    {
                        md.FixedFaces = Where(md.Mesh, f => f.IsPlane && f.Centroid.X < 1e-6);
                        md.Loads.Add(new FeLoad { Kind = LoadKind.Torque, Faces = Where(md.Mesh, f => f.IsPlane && f.Centroid.X > 0.1 - 1e-6), Value = 50, Dir = new Vec3(1, 0, 0) });
                    },
                    Check = (md, r) =>
                    {
                        var m = md.Mesh;
                        double R = 0.01, J = Math.PI * Math.Pow(R, 4) / 2;
                        var end = NodesWhere(m, p => p.X > 0.1 - 1e-6 && Math.Sqrt(p.Y * p.Y + p.Z * p.Z) > 0.5 * R).ToList();
                        var phi = end.Average(n => { var p = m.P(n); var rr = p.Y * p.Y + p.Z * p.Z; return (p.Y * r.U[3 * n + 2] - p.Z * r.U[3 * n + 1]) / rr; });
                        var surf = NodesWhere(m, p => Math.Abs(p.X - 0.05) < 0.004 && Math.Sqrt(p.Y * p.Y + p.Z * p.Z) > R - 1e-6).Average(n => r.VonMises[n]);
                        return new List<(string, double, double, double)> { ("自由端扭转角 (mrad)", phi * 1000, 50 * 0.1 / (G * J) * 1000, 0.03), ("中段表面等效应力 (MPa)", surf / 1e6, Math.Sqrt(3) * 50 * R / J / 1e6, 0.03) };
                    },
                },
                new Case
                {
                    Name = "悬臂梁 200×20×10 mm，自重 ×100", Size = 0.004,
                    Geo = "Box(1) = {0, 0, 0, 0.2, 0.02, 0.01};",
                    Setup = md =>
                    {
                        md.FixedFaces = Where(md.Mesh, f => f.Centroid.X < 1e-6);
                        md.Loads.Add(new FeLoad { Kind = LoadKind.Gravity, Value = 100, Dir = new Vec3(0, -1, 0) });
                    },
                    Check = (md, r) =>
                    {
                        double I = 0.01 * Math.Pow(0.02, 3) / 12, q = 100 * 9.80665 * md.Material.Density * 0.01 * 0.02;
                        var tip = -NodesWhere(md.Mesh, p => p.X > 0.2 - 1e-6).Average(n => r.U[3 * n + 1]);
                        return new List<(string, double, double, double)> { ("端部挠度 (mm)", tip * 1000, q * Math.Pow(0.2, 4) / (8 * E * I) * 1000, 0.03), ("外载 = 零件重量 ×100 (N)", r.Applied.Length, q * 0.2, 1e-6) };
                    },
                },
            };

            int fails = 0;
            var tmp = Path.Combine(Path.GetTempPath(), "swkit_fea_bench");
            Directory.CreateDirectory(tmp);
            foreach (var c in cases)
            {
                Console.WriteLine("=== " + c.Name);
                var geo = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".geo");
                File.WriteAllText(geo, "SetFactory(\"OpenCASCADE\");\n" + c.Geo + "\n", new UTF8Encoding(false));
                var model = new FeModel { Material = steel, Mesh = Gmsh.Mesh(geo, c.Size, Console.WriteLine) };
                c.Setup(model);
                var r = FeSolver.Solve(model, null, CancellationToken.None);
                Print(model, r);
                foreach (var (what, fe, exact, tol) in c.Check(model, r))
                {
                    var err = Math.Abs(fe - exact) / Math.Abs(exact);
                    var ok = err <= tol;
                    if (!ok) fails++;
                    Console.WriteLine($"  {(ok ? "通过" : "不符")}  {what}：有限元 {fe:0.#####}，理论 {exact:0.#####}，差 {err:P2}（允许 {tol:P0}）");
                }
                if (r.EquilibriumError > 1e-4 && r.Applied.Length > 0) { fails++; Console.WriteLine($"  不符  外载与反力不平衡：{r.EquilibriumError:P3}"); }
                File.Delete(geo);
            }
            Console.WriteLine(fails == 0 ? "全部通过" : $"{fails} 项不符");
            return fails == 0 ? 0 : 1;
        }
    }
}
