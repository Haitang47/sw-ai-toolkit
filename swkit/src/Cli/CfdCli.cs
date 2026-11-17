using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using SwKit.Core.Cfd;
using SwKit.Core.Dfm;
using SwKit.Core.Urdf;

namespace SwKit.Cli
{
    /// <summary>
    /// swkit-cli cfd：外流场 CFD 的命令行入口和验证算例。
    ///   devices                 列出计算设备
    ///   disk                    单个作动盘（自由空气）：入流 vs 动量理论、远尾流速度、下游平面的动量通量
    ///   sphere [--compare]      圆球阻力（Re ≈ 100）vs 经验公式；--compare 同一个算例 CPU 和显卡对比
    ///   bench                   不同网格规模的速度（MLUPS）
    ///   run <模型> --disk …     任意模型 + 作动盘
    /// </summary>
    static class CfdCli
    {
        const string Usage = @"用法：
  swkit-cli cfd devices
  swkit-cli cfd disk   [--cells 3e6] [--thrust 5] [--diameter 127] [--hub 0] [--prop] [--device cpu|gpu|名字] [--time 秒] [--vtk 文件]
  swkit-cli cfd sphere [--re 100] [--d 16（直径格子数）] [--cs 0.12] [--device …] [--compare]
  swkit-cli cfd bench  [--cells 1e6,4e6,1e7] [--steps 2000] [--device …]
  swkit-cli cfd run <模型.step|.stl> --disk x,y,z:ax,ay,az:直径mm:推力N[:反扭矩N·m] … [--cells N] [--cell mm] [--up +y] [--wind x,y,z]
                    [--ground mm] [--time 秒] [--prop] [--device …] [--vtk 文件]
      --disk 的坐标、直径单位是 mm（和模型一样），轴向是推力方向（悬停一般 0,1,0）";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static int Run(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine(Usage); return 2; }
            var o = new Opts(args.Skip(1).ToArray());
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "devices": return Devices();
                    case "disk": return Disk(o);
                    case "sphere": return Sphere(o);
                    case "bench": return Bench(o);
                    case "run": return RunModel(o);
                    default: Console.WriteLine(Usage); return 2;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is FormatException || ex is IOException)
            {
                Console.Error.WriteLine("出错：" + ex.Message);
                return 1;
            }
        }

        // ================= 参数 =================

        sealed class Opts
        {
            public readonly Dictionary<string, List<string>> Map = new Dictionary<string, List<string>>();
            public readonly List<string> Pos = new List<string>();
            public Opts(string[] a)
            {
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i].StartsWith("--"))
                    {
                        var k = a[i].Substring(2).ToLowerInvariant();
                        string v = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "";
                        if (!Map.TryGetValue(k, out var l)) Map[k] = l = new List<string>();
                        l.Add(v);
                    }
                    else Pos.Add(a[i]);
                }
            }
            public bool Has(string k) => Map.ContainsKey(k);
            public string Str(string k, string def = null) => Map.TryGetValue(k, out var l) ? l[l.Count - 1] : def;
            public double Num(string k, double def) => Map.TryGetValue(k, out var l) && l[l.Count - 1].Length > 0 ? double.Parse(l[l.Count - 1], Inv) : def;
            public List<string> All(string k) => Map.TryGetValue(k, out var l) ? l : new List<string>();
        }

        static Vec3 ParseVec(string s)
        {
            var p = s.Split(',').Select(x => double.Parse(x, Inv)).ToArray();
            if (p.Length != 3) throw new FormatException("向量要写成 x,y,z：" + s);
            return new Vec3(p[0], p[1], p[2]);
        }

        static Vec3 ParseUp(string s)
        {
            s = (s ?? "+y").Trim().ToLowerInvariant();
            int sg = s.StartsWith("-") ? -1 : 1;
            s = s.TrimStart('+', '-');
            return s == "x" ? new Vec3(sg, 0, 0) : s == "z" ? new Vec3(0, 0, sg) : new Vec3(0, sg, 0);
        }

        static CfdResult Solve(CfdSetup s, int fixedSteps = 0, bool quietProgress = false, CancellationToken ct = default(CancellationToken))
        {
            var last = DateTime.MinValue;
            return CfdSolver.Run(s, m => Console.WriteLine("  " + m), p =>
            {
                if (quietProgress || p.Steps == 0) return;
                if ((DateTime.Now - last).TotalSeconds < 3 && p.Step != p.Steps) return;
                last = DateTime.Now;
                Console.WriteLine($"  [{p.Phase}] {p.Step,7:N0}/{p.Steps:N0}  t = {p.Time * 1000:0.##} ms  最大速度 {p.MaxSpeed:0.##} m/s  合力 {p.BodyForce}  {p.MLups:0} MLUPS" +
                                  (p.Slice != null ? $"  切面 {p.SliceW}×{p.SliceH}，最大 {p.Slice.Max():0.#} m/s" : ""));
            }, ct, fixedSteps);
        }

        /// <summary>--cancel-after 秒：先跑一次、到时间取消（检查显存释放和取消响应），再正常算。</summary>
        static void CancelTest(Opts o, CfdSetup s)
        {
            if (!o.Has("cancel-after")) return;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(o.Num("cancel-after", 2))))
            {
                var t0 = DateTime.Now;
                try { Solve(s, 0, true, cts.Token); Console.WriteLine("（取消测试：没来得及取消就算完了）"); }
                catch (OperationCanceledException) { Console.WriteLine($"取消测试：已取消，从发出取消到停下 {(DateTime.Now - t0).TotalSeconds - o.Num("cancel-after", 2):0.###} 秒"); }
            }
        }

        static void Summary(CfdResult r)
        {
            Console.WriteLine($"网格 {r.Nx}×{r.Ny}×{r.Nz} = {r.Cells / 1e6:0.##} M 格，格子 {r.Dx * 1000:0.###} mm，{r.Steps:N0} 步，物理时间 {r.SimTime * 1000:0.#} ms，" +
                              $"{r.Device}，用时 {r.Seconds:0.#} 秒（计算 {r.MLups:0} MLUPS）");
            foreach (var n in r.Notes) Console.WriteLine("  · " + n);
        }

        // ================= devices =================

        static int Devices()
        {
            foreach (var d in CfdSolver.Devices()) Console.WriteLine(d);
            return 0;
        }

        // ================= 单个作动盘 =================

        static int Disk(Opts o)
        {
            double D = o.Num("diameter", 127) / 1000, R = D / 2, T = o.Num("thrust", 5), rho = 1.204;
            var disk = new CfdDisk { Name = "桨", Center = Vec3.Zero, Axis = new Vec3(0, 1, 0), Radius = R, HubRadius = o.Num("hub", 0) / 1000, Thrust = T,
                                     Loading = o.Has("prop") ? CfdLoading.Prop : CfdLoading.Uniform, Torque = o.Num("torque", 0) };
            var s = new CfdSetup
            {
                Disks = new List<CfdDisk> { disk }, Up = new Vec3(0, 1, 0), Density = rho,
                PadSide = o.Num("pad-side", 1.5) * D, PadUp = o.Num("pad-up", 1.5) * D, PadDown = o.Num("pad-down", 4) * D,
                TargetCells = (long)o.Num("cells", 3e6), Device = o.Str("device"), SimTime = o.Num("time", 0), Smagorinsky = o.Num("cs", 0.12),
                Wind = o.Has("wind") ? new Vec3(-o.Num("wind", 0), 0, 0) : Vec3.Zero,
            };
            double vi = CfdDomain.IdealInflow(disk, rho);
            if (s.Wind.Length > 0)
            {
                // 平飞（来流平行于桨盘）：Glauert 动量理论 v⁴ + V²v² = v_h⁴
                double V = s.Wind.Length, mu = V / vi, vg = vi * Math.Sqrt(-mu * mu / 2 + Math.Sqrt(Math.Pow(mu, 4) / 4 + 1));
                Console.WriteLine($"平飞 {V} m/s（来流沿 −X，平行于桨盘），Glauert 诱导速度 {vg:0.###} m/s（悬停 {vi:0.###}）");
                var rw = Solve(s);
                Summary(rw);
                var sw = rw.DiskStats[0];
                Console.WriteLine($"盘面平均轴向速度 {sw.InflowSpeed:0.###} m/s，Glauert {vg:0.###} m/s（{(sw.InflowSpeed / vg - 1) * 100:+0.0;-0.0} %）");
                foreach (var png in o.All("png")) { var f = png.Split(new[] { ',' }, 3); SlicePng(rw, f[2], f[0].ToLowerInvariant(), double.Parse(f[1], Inv) / 1000, o.Num("png-vmax", 0)); }
                return 0;
            }
            Console.WriteLine($"单个作动盘：Φ{D * 1000:0} mm，推力 {T} N，{(disk.Loading == CfdLoading.Prop ? "桨型载荷" : "均匀载荷")}；动量理论 v_i = {vi:0.###} m/s，远尾流 2v_i = {2 * vi:0.###} m/s");
            CancelTest(o, s);
            var r = Solve(s);
            Summary(r);
            var st = r.DiskStats[0];
            Console.WriteLine();
            Console.WriteLine($"盘面平均入流 {st.InflowSpeed:0.###} m/s，动量理论 {st.IdealInflow:0.###} m/s（{(st.InflowSpeed / st.IdealInflow - 1) * 100:+0.0;-0.0} %）");
            // 轴线上的轴向速度（向下为正）
            Console.WriteLine("轴线上的平均速度（向下为正）：");
            foreach (var k in new[] { 1.0, 0.5, 0.0, -0.5, -1, -1.5, -2, -3, -4, -6 })
            {
                var u = r.Sample(new Vec3(0, k * R, 0));
                Console.WriteLine($"  y = {k,5:0.0} R：{-u.Y,7:0.###} m/s  = {-u.Y / vi:0.00} v_i");
            }
            // 下游平面：最大轴向速度、动量通量 ∫(ρu² + p − p0)dA、流量、尾流半径
            Console.WriteLine("下游平面：");
            foreach (var k in new[] { -0.5, -1, -2, -3, -4 })
            {
                int j = (int)Math.Floor((k * R - r.Origin.Y) / r.Dx);
                if (j < 1 || j >= r.Ny - 1) continue;
                double mom = 0, momU = 0, pr = 0, umax = 0, flux = 0, a2 = r.Dx * r.Dx, rr = 0, ru = 0, ang = 0;
                for (int kk = 1; kk < r.Nz - 1; kk++)
                    for (int i = 1; i < r.Nx - 1; i++)
                    {
                        long n = i + (long)r.Nx * (j + (long)r.Ny * kk);
                        double ud = -r.U[3 * n + 1], p = r.P[n];
                        mom += (rho * ud * ud + p) * a2; momU += rho * ud * ud * a2; pr += p * a2;
                        if (ud > umax) umax = ud;
                        {
                            // 绕 +Y 的角动量通量 ∫ρ·u_下·(x·u_z − z·u_x)·(−1) dA（右手绕 +Y：在 +X 处切向是 −Z）
                            double xx = r.Origin.X + (i + 0.5) * r.Dx, zz = r.Origin.Z + (kk + 0.5) * r.Dx;
                            ang += rho * ud * (zz * r.U[3 * n] - xx * r.U[3 * n + 2]) * a2;
                        }
                        if (ud > 0) { flux += ud * a2; double x = r.Origin.X + (i + 0.5) * r.Dx, z = r.Origin.Z + (kk + 0.5) * r.Dx; rr += ud * ud * (x * x + z * z); ru += ud * ud; }
                    }
                double yPlane = r.Origin.Y + (j + 0.5) * r.Dx;
                Console.WriteLine($"  y = {yPlane / R,5:0.00} R：最大 {umax:0.###} m/s = {umax / vi:0.00} v_i；动量通量 {mom:0.###} N = {mom / T:0.000} T（ρu² {momU:0.###} + 压力 {pr:0.###}）；" +
                                  $"向下流量 {flux * 1000:0.#} L/s；尾流半径（u² 加权 RMS）{Math.Sqrt(rr / Math.Max(1e-12, ru)) / R:0.00} R" +
                                  (disk.Torque != 0 ? $"；角动量通量 {ang:0.####} N·m = {ang / disk.Torque:0.00} × 反扭矩" : ""));
            }
            Console.WriteLine($"（动量理论：盘面流量 {Math.PI * R * R * vi * 1000:0.#} L/s；远尾流半径 {1 / Math.Sqrt(2):0.00} R）");
            if (o.Has("vtk")) { CfdVtk.Write(r, o.Str("vtk")); Console.WriteLine("已写出 " + o.Str("vtk")); }
            return 0;
        }

        // ================= 圆球阻力 =================

        static float[] Icosphere(double radius, int level)
        {
            double t = (1 + Math.Sqrt(5)) / 2;
            var v = new List<Vec3> { new Vec3(-1, t, 0), new Vec3(1, t, 0), new Vec3(-1, -t, 0), new Vec3(1, -t, 0), new Vec3(0, -1, t), new Vec3(0, 1, t),
                new Vec3(0, -1, -t), new Vec3(0, 1, -t), new Vec3(t, 0, -1), new Vec3(t, 0, 1), new Vec3(-t, 0, -1), new Vec3(-t, 0, 1) };
            var f = new List<int[]> { new[]{0,11,5}, new[]{0,5,1}, new[]{0,1,7}, new[]{0,7,10}, new[]{0,10,11}, new[]{1,5,9}, new[]{5,11,4}, new[]{11,10,2}, new[]{10,7,6}, new[]{7,1,8},
                new[]{3,9,4}, new[]{3,4,2}, new[]{3,2,6}, new[]{3,6,8}, new[]{3,8,9}, new[]{4,9,5}, new[]{2,4,11}, new[]{6,2,10}, new[]{8,6,7}, new[]{9,8,1} };
            for (int i = 0; i < v.Count; i++) v[i] = v[i].Normalized();
            for (int l = 0; l < level; l++)
            {
                var mid = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = Math.Min(a, b) * 1000000L + Math.Max(a, b);
                    if (mid.TryGetValue(key, out int m)) return m;
                    v.Add(((v[a] + v[b]) / 2).Normalized()); mid[key] = v.Count - 1; return v.Count - 1;
                }
                var nf = new List<int[]>();
                foreach (var tr in f)
                {
                    int a = Mid(tr[0], tr[1]), b = Mid(tr[1], tr[2]), c = Mid(tr[2], tr[0]);
                    nf.Add(new[] { tr[0], a, c }); nf.Add(new[] { tr[1], b, a }); nf.Add(new[] { tr[2], c, b }); nf.Add(new[] { a, b, c });
                }
                f = nf;
            }
            var r = new float[f.Count * 9];
            for (int i = 0; i < f.Count; i++)
                for (int k = 0; k < 3; k++) { var p = v[f[i][k]] * radius; r[9 * i + 3 * k] = (float)p.X; r[9 * i + 3 * k + 1] = (float)p.Y; r[9 * i + 3 * k + 2] = (float)p.Z; }
            return r;
        }

        static CfdSetup SphereSetup(double re, int dCells, double cs, string device)
        {
            double D = 0.02, nu = 1.5e-5, U = re * nu / D;
            return new CfdSetup
            {
                Triangles = Icosphere(D / 2, 4), TriPart = null, PartNames = new[] { "圆球" },
                Wind = new Vec3(0, -U, 0), Up = new Vec3(0, 1, 0), Viscosity = nu,
                CellSize = D / dCells, PadSide = 3.5 * D, PadUp = 3 * D, PadDown = 8 * D, Smagorinsky = cs, Device = device,
            };
        }

        static double Cd(CfdResult r, CfdSetup s, double D = 0.02)
        {
            double U = s.Wind.Length;
            return r.BodyForce.Dot(s.Wind / U) / (0.5 * s.Density * U * U * Math.PI * D * D / 4);
        }

        static int Sphere(Opts o)
        {
            double re = o.Num("re", 100), cs = o.Num("cs", 0.12);
            int dc = (int)o.Num("d", o.Has("compare") ? 10 : 16);
            double cdRef = 24 / re * (1 + 0.15 * Math.Pow(re, 0.687));          // Schiller–Naumann（Re < 800）
            double cdRef2 = 24 / re * (1 + 0.1935 * Math.Pow(re, 0.6305));     // Clift–Grace–Weber（20 < Re ≤ 260）
            Console.WriteLine($"圆球：Re = {re}，直径 {dc} 个格子，Smagorinsky Cs = {cs}；参考 Cd = {cdRef:0.###}（Schiller–Naumann），{cdRef2:0.###}（Clift–Grace–Weber）");
            if (!o.Has("compare"))
            {
                var s = SphereSetup(re, dc, cs, o.Str("device"));
                var r = Solve(s);
                Summary(r);
                double cd = Cd(r, s);
                Console.WriteLine($"Cd = {cd:0.####}（相对 Schiller–Naumann {(cd / cdRef - 1) * 100:+0.0;-0.0} %）；侧向力 {Math.Sqrt(r.BodyForce.X * r.BodyForce.X + r.BodyForce.Z * r.BodyForce.Z):0.###e0} N，阻力 {r.BodyForce.Dot(s.Wind.Normalized()):0.###e0} N" + (r.Oscillating ? "（受力有脉动）" : ""));
                PrintHistory(r, s);
                if (o.Has("vtk")) { CfdVtk.Write(r, o.Str("vtk")); Console.WriteLine("已写出 " + o.Str("vtk")); }
                return 0;
            }
            // CPU 和显卡对比
            var sg = SphereSetup(re, dc, cs, "gpu");
            var rg = Solve(sg, 0, true);
            Console.Write("显卡："); Summary(rg);
            var sc = SphereSetup(re, dc, cs, "cpu");
            var rc = Solve(sc, 0, true);
            Console.Write("CPU： "); Summary(rc);
            double cg = Cd(rg, sg), cc = Cd(rc, sc), U = sg.Wind.Length;
            double se = 0, sm = 0; float maxd = 0;
            for (long i = 0; i < rg.U.Length; i++) { double d = rg.U[i] - rc.U[i]; se += d * d; maxd = Math.Max(maxd, Math.Abs((float)d)); }
            for (long n = 0; n < rg.P.Length; n++) sm += Math.Abs(rg.P[n] - rc.P[n]);
            Console.WriteLine($"Cd：显卡 {cg:0.#####}，CPU {cc:0.#####}，相差 {(cg / cc - 1) * 100:+0.000;-0.000} %（参考 {cdRef:0.###}）");
            Console.WriteLine($"时间平均速度场：RMS 差 {Math.Sqrt(se / rg.U.Length) / U * 100:0.0000} % U∞，最大差 {maxd / U * 100:0.000} % U∞；压力平均差 {sm / rg.P.Length:0.###e0} Pa");
            Console.WriteLine($"速度：显卡 {rg.MLups:0} MLUPS，CPU {rc.MLups:0} MLUPS");
            return 0;
        }

        static void PrintHistory(CfdResult r, CfdSetup s)
        {
            int n = r.HistoryTime.Count;
            if (n == 0) return;
            var w = s.Wind.Normalized();
            Console.Write("阻力历史（N）：");
            for (int k = 1; k <= 8; k++) { int i = Math.Min(n - 1, k * n / 8 - 1); Console.Write($"{r.HistoryTime[i] * 1000:0}ms {r.HistoryForce[i].Dot(w):0.####e0}  "); }
            Console.WriteLine();
        }

        // ================= 速度测试 =================

        static int Bench(Opts o)
        {
            var sizes = (o.Str("cells") ?? "1e6,4e6,1e7").Split(',').Select(x => (long)double.Parse(x, Inv)).ToArray();
            int steps = (int)o.Num("steps", 2000);
            foreach (var n in sizes)
            {
                var s = new CfdSetup
                {
                    Disks = new List<CfdDisk> { new CfdDisk { Center = Vec3.Zero, Radius = 0.0635, HubRadius = 0.01, Thrust = 5 } },
                    TargetCells = n, Device = o.Str("device"),
                };
                var r = Solve(s, steps, true);
                Console.WriteLine($"{r.Cells / 1e6,6:0.00} M 格  {r.Steps} 步  {r.Seconds,6:0.0} 秒（含准备）  {r.MLups,6:0} MLUPS  {r.Device}");
                foreach (var x in r.Notes) Console.WriteLine("  · " + x);
                GC.Collect();
            }
            return 0;
        }

        // ================= 任意模型 =================

        static int RunModel(Opts o)
        {
            if (o.Pos.Count == 0) { Console.WriteLine(Usage); return 2; }
            var path = o.Pos[0];
            var mesh = DfmMesh.Load(path, Console.WriteLine);
            int nt = mesh.Tris;
            var tris = new float[9 * nt];
            for (int t = 0; t < nt; t++)
                for (int k = 0; k < 3; k++)
                {
                    int v = mesh.T[3 * t + k];
                    tris[9 * t + 3 * k] = (float)(mesh.V[3 * v] / 1000); tris[9 * t + 3 * k + 1] = (float)(mesh.V[3 * v + 1] / 1000); tris[9 * t + 3 * k + 2] = (float)(mesh.V[3 * v + 2] / 1000);
                }
            var triPart = mesh.ShellOf != null ? (int[])mesh.ShellOf.Clone() : null;
            int nShell = triPart != null && triPart.Length > 0 ? triPart.Max() + 1 : 1;
            var names = Enumerable.Range(0, nShell).Select(i => $"实体 {i + 1}").ToArray();
            var disks = new List<CfdDisk>();
            int di = 0;
            foreach (var a in o.All("disk"))
            {
                var f = a.Split(':');
                if (f.Length < 4) throw new FormatException("--disk 要写成 x,y,z:ax,ay,az:直径mm:推力N[:反扭矩]：" + a);
                double dia = double.Parse(f[2], Inv) / 1000;
                disks.Add(new CfdDisk
                {
                    Name = $"桨 {++di}", Center = ParseVec(f[0]) / 1000, Axis = ParseVec(f[1]).Normalized(), Radius = dia / 2, HubRadius = 0.15 * dia / 2,
                    Thrust = double.Parse(f[3], Inv), Torque = f.Length > 4 ? double.Parse(f[4], Inv) : 0, Loading = o.Has("prop") ? CfdLoading.Prop : CfdLoading.Uniform,
                });
            }
            if (o.Has("scale")) { double k = o.Num("scale", 1); for (int i = 0; i < tris.Length; i++) tris[i] *= (float)k; }   // 开发用：几何整体缩放 / 镜像
            if (o.Has("y-to-z"))   // 绕 X 转 90°：(x, y, z) → (x, −z, y)，Y 向上的模型变成 Z 向上
            {
                for (int i = 0; i < tris.Length; i += 3) { float y = tris[i + 1], z = tris[i + 2]; tris[i + 1] = -z; tris[i + 2] = y; }
                foreach (var d in disks) { d.Center = new Vec3(d.Center.X, -d.Center.Z, d.Center.Y); d.Axis = new Vec3(d.Axis.X, -d.Axis.Z, d.Axis.Y); }
            }
            if (o.Has("flip-y")) { for (int i = 1; i < tris.Length; i += 3) tris[i] = -tris[i]; foreach (var d in disks) { d.Center = new Vec3(d.Center.X, -d.Center.Y, d.Center.Z); d.Axis = new Vec3(d.Axis.X, -d.Axis.Y, d.Axis.Z); } }
            var s = new CfdSetup
            {
                Triangles = tris, TriPart = triPart, PartNames = names, Disks = disks,
                Up = ParseUp(o.Str("up")), Wind = o.Has("wind") ? ParseVec(o.Str("wind")) : Vec3.Zero,
                Ground = o.Has("ground"), GroundDistance = o.Num("ground", 300) / 1000,
                CellSize = o.Num("cell", 0) / 1000, TargetCells = (long)o.Num("cells", 4e6), SimTime = o.Num("time", 0), Device = o.Str("device"),
                Smagorinsky = o.Num("cs", 0.12),
            };
            long est = CfdSolver.EstimateCells(s, out double dx, out _, out int nx, out int ny, out int nz);
            Console.WriteLine($"模型 {nt:N0} 个三角形，{nShell} 个实体；{disks.Count} 个桨盘；估计网格 {nx}×{ny}×{nz} = {est / 1e6:0.##} M 格，格子 {dx * 1000:0.##} mm");
            var r = Solve(s);
            Summary(r);
            Console.WriteLine($"作用在模型上的合力（时间平均）：{r.BodyForce} N" + (r.Oscillating ? "（有明显脉动）" : ""));
            {
                // 平均段里瞬时合力的统计
                double t0 = r.SimTime * s.AverageFrom;
                var win = r.HistoryForce.Where((f, i) => r.HistoryTime[i] > t0).ToList();
                if (win.Count > 2)
                {
                    var mean = win.Aggregate(Vec3.Zero, (x, y) => x + y) / win.Count;
                    var sd = new Vec3(Math.Sqrt(win.Average(f => (f.X - mean.X) * (f.X - mean.X))), Math.Sqrt(win.Average(f => (f.Y - mean.Y) * (f.Y - mean.Y))), Math.Sqrt(win.Average(f => (f.Z - mean.Z) * (f.Z - mean.Z))));
                    Console.WriteLine($"  合力历史（{win.Count} 段平均）：平均 {mean}，标准差 {sd}，最小 ({win.Min(f => f.X):0.##}, {win.Min(f => f.Y):0.##}, {win.Min(f => f.Z):0.##})，最大 ({win.Max(f => f.X):0.##}, {win.Max(f => f.Y):0.##}, {win.Max(f => f.Z):0.##})");
                }
            }
            foreach (var pf in r.PartForces.Take(12)) Console.WriteLine($"  {pf.Part}：{pf.Force} N");
            foreach (var st in r.DiskStats) Console.WriteLine($"  {st.Disk.Name}：入流 {st.InflowSpeed:0.##} m/s，动量理论 {st.IdealInflow:0.##} m/s");
            if (o.Has("vtk")) { CfdVtk.Write(r, o.Str("vtk")); Console.WriteLine("已写出 " + o.Str("vtk")); }
            // 开发用：--png 轴,坐标mm,文件（可以多个）写出切面的速度大小图（固体灰色）
            foreach (var png in o.All("png"))
            {
                var f = png.Split(new[] { ',' }, 3);
                SlicePng(r, f[2], f[0].ToLowerInvariant(), double.Parse(f[1], Inv) / 1000, o.Num("png-vmax", 0));
            }
            return 0;
        }

        static void SlicePng(CfdResult r, string path, string axis, double at, double vmax)
        {
            int an = axis == "x" ? 0 : axis == "y" ? 1 : 2;
            int au = an == 0 ? 2 : 0, av = an == 1 ? 2 : 1;     // 法向 z：横 x、竖 y；法向 x：横 z、竖 y；法向 y：横 x、竖 z
            int[] dim = { r.Nx, r.Ny, r.Nz };
            double[] org = { r.Origin.X, r.Origin.Y, r.Origin.Z };
            int c = Math.Max(0, Math.Min(dim[an] - 1, (int)Math.Floor((at - org[an]) / r.Dx)));
            int w = dim[au], h = dim[av], sc = Math.Max(1, 600 / Math.Max(w, h));
            if (vmax <= 0) { for (long i = 0; i < r.U.Length; i += 3) vmax = Math.Max(vmax, Math.Sqrt(r.U[i] * r.U[i] + r.U[i + 1] * r.U[i + 1] + r.U[i + 2] * r.U[i + 2])); }
            using (var bmp = new System.Drawing.Bitmap(w * sc, h * sc))
            {
                var ijk = new int[3];
                for (int v = 0; v < h * sc; v++)
                    for (int u = 0; u < w * sc; u++)
                    {
                        ijk[an] = c; ijk[au] = u / sc; ijk[av] = v / sc;
                        long n = ijk[0] + (long)r.Nx * (ijk[1] + (long)r.Ny * ijk[2]);
                        System.Drawing.Color col;
                        if (r.Solid[n] != 0) col = System.Drawing.Color.FromArgb(150, 150, 150);
                        else
                        {
                            double s = Math.Sqrt(r.U[3 * n] * r.U[3 * n] + r.U[3 * n + 1] * r.U[3 * n + 1] + r.U[3 * n + 2] * r.U[3 * n + 2]) / vmax;
                            s = Math.Max(0, Math.Min(1, s));
                            // 简单的蓝-青-黄-红色带
                            double rr = Math.Max(0, Math.Min(1, 1.5 - Math.Abs(4 * s - 3))), gg = Math.Max(0, Math.Min(1, 1.5 - Math.Abs(4 * s - 2))), bb = Math.Max(0, Math.Min(1, 1.5 - Math.Abs(4 * s - 1)));
                            col = System.Drawing.Color.FromArgb((int)(255 * rr), (int)(255 * gg), (int)(255 * bb));
                        }
                        bmp.SetPixel(u, h * sc - 1 - v, col);
                    }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine($"已写出切面图 {path}（{"xyz"[an]} = {(org[an] + (c + 0.5) * r.Dx) * 1000:0.#} mm，{w}×{h}，色标 0–{vmax:0.#} m/s）");
        }
    }
}
