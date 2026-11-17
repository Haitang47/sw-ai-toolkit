using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SwKit.Core.Urdf;

namespace SwKit.Core.Fea
{
    /// <summary>装配体里的一个实体（零件）。</summary>
    public sealed class FeBody
    {
        public int Tag;                 // STEP 里的实体编号（Gmsh 的体编号）
        public string Name;             // 零件名（从 SolidWorks 装配体读到时）或「零件 n」
        public Vec3 Min, Max;           // 包围盒（米）
        public bool Included = true;    // 参与计算（不参与的从模型里删掉，例如电池、电子件——它们的重量可以作为载荷加）
        public FeMaterial Material;
        public double Volume;           // 网格算出来的体积（m³）；不参与计算的零件为 0
        public double CadVolume;        // CAD 几何的精确体积（m³，读入时 OCC 算的）
        /// <summary>质量用的体积：参与计算时用网格体积（和计算一致），否则用 CAD 体积。</summary>
        public double MassVolume => Volume > 0 ? Volume : CadVolume;
        public int Faces;               // 面数（形状复杂程度：厂家的电机、舵机模型常有几百上千个面）
        public Vec3 Size => Max - Min;
        /// <summary>板状：最薄的方向不到最长方向的 6%（底板、碳板、钣金件）。</summary>
        public bool PlateLike { get { var d = new[] { Size.X, Size.Y, Size.Z }.OrderBy(x => x).ToArray(); return d[0] < 0.06 * d[2]; } }
    }

    /// <summary>
    /// 装配体应力分析：每个实体一个零件，各自划网格，贴在一起的面绑定（Tie：从属节点的位移由对方表面插值，求解时约束消元）——等于把零件粘在一起
    /// （和 SolidWorks Simulation 的「接合」接触相同，接触面上的网格不需要对齐）。
    /// 每个单元记住属于哪个零件，刚度、强度、密度用各自的材料。有间隙（超过绑定容差）的零件传不了力。
    /// </summary>
    public static class FeAssembly
    {
        /// <summary>
        /// STEP 里的实体和包围盒（米）。brepOut 不为空时顺便把几何存成 BREP（单位米）：之后每次划网格读 BREP，
        /// 比重新解析 STEP 快 3~4 倍（几百个零件的整机 STEP 要十几秒）。实体编号和 STEP 里相同。
        /// </summary>
        public static List<FeBody> Read(string step, string brepOut = null)
        {
            var exe = Gmsh.FindExe() ?? throw new InvalidOperationException("找不到 gmsh.exe（应该在程序旁边的 gmsh 文件夹里）");
            var tmp = Path.Combine(Path.GetTempPath(), "swkit_asm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var local = Path.Combine(tmp, "a.step");
                File.Copy(step, local, true);
                var geo = Path.Combine(tmp, "v.geo");
                File.WriteAllText(geo, "SetFactory(\"OpenCASCADE\");\nMerge \"a.step\";\nv() = Volume{:};\nFor i In {0:#v()-1}\n  bb() = BoundingBox Volume{v(i)};\n  sf() = Boundary{ Volume{v(i)}; };\n" +
                    "  m = Mass Volume{v(i)};\n" +
                    "  Printf(\"SWKITVOL %g %g %g %g %g %g %g %g %g\", v(i), bb(0), bb(1), bb(2), bb(3), bb(4), bb(5), #sf(), m);\nEndFor\n", new UTF8Encoding(false));
                var outArg = brepOut != null ? $" -o \"{brepOut}\"" : "";
                var psi = new ProcessStartInfo(exe, "v.geo -0 -nopopup -setstring Geometry.OCCTargetUnit M" + outArg)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = tmp };
                string output;
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(240000)) { try { p.Kill(); } catch { } throw new TimeoutException("读取装配体实体超过 4 分钟"); }
                }
                if (brepOut != null && !File.Exists(brepOut)) throw new InvalidOperationException("装配体几何转换失败");
                var list = new List<FeBody>();
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    int k = t.IndexOf("SWKITVOL ", StringComparison.Ordinal);
                    if (k < 0) continue;
                    var s = t.Substring(k + 9).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    list.Add(new FeBody { Tag = (int)s[0], Min = new Vec3(s[1], s[2], s[3]), Max = new Vec3(s[4], s[5], s[6]), Faces = s.Length > 7 ? (int)s[7] : 0, CadVolume = s.Length > 8 ? Math.Abs(s[8]) : 0 });
                }
                for (int i = 0; i < list.Count; i++) list[i].Name = $"零件 {i + 1}";
                return list;
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        /// <summary>
        /// 划网格的几档设置（从粗到细）。Gmsh 默认把边界上的小尺寸扩散到整个面（ExtendFromBoundary）：
        /// 电路板一类的薄板上有上百个零点几毫米的过孔时，整块板都会被划得很细（一个零件十几万节点），
        /// 所以薄板先关掉扩散；但形状复杂的小零件关掉扩散、或者最小尺寸太大时，小面上的三角形会互相重叠，划不出来——
        /// 这时按更细的一档单独重划。
        /// </summary>
        static readonly (bool extend, double minFrac, int curvature)[] Levels =
        {
            (false, 1.0 / 8, 8),     // 0：薄板
            (true, 1.0 / 10, 8),     // 1：一般零件
            (true, 1.0 / 20, 8),     // 2：失败后加密
            (true, 1.0 / 40, 12),    // 3：再失败
        };

        /// <summary>
        /// 装配体划网格：参与计算的零件按设置分组、每组再按复杂程度均分成几块，每块一个 Gmsh 进程（并行）只划这块的体
        /// （其余隐藏），最后拼成一个网格。每个进程单线程：Gmsh 多线程时每次的网格略有不同、偶尔失败或卡死；
        /// 单线程多进程既快又可复现。零件之间不做布尔（几百个零件时 OCC 的布尔要十几分钟），接触面在划完网格后用面绑定连起来（Tie）。
        /// 某个零件划不出来时：Gmsh 的 Delaunay 遇到第一个失败的体就停下，后面的体都没划——
        /// 把编号最小的那个没划出来的零件（和它一模一样的复制件）换细一档单独重划，其余的照原设置再划一次。
        /// 某块超时（Gmsh 偶尔卡死）：拆成两半重划，只剩一个零件时换细一档。
        /// 最细一档也失败的零件放进 failed（调用方把它去掉并提示）。
        /// threads：本机可用的核数（决定同时开几个进程）。backgroundPos / bgMin：自适应加密的背景尺寸场。
        /// </summary>
        public static FeMesh Mesh(string brep, IList<FeBody> included, double size, int threads, Action<string> log, out List<FeBody> failed,
                                  string backgroundPos = null, double bgMin = 0)
        {
            var exe = Gmsh.FindExe() ?? throw new InvalidOperationException("找不到 gmsh.exe（应该在程序旁边的 gmsh 文件夹里）");
            failed = new List<FeBody>();
            var level = new Dictionary<FeBody, int>();
            foreach (var b in included) level[b] = b.PlateLike ? 0 : 1;
            // 同时开几个 Gmsh：核数的一半、每个约留 700 MB 内存，最多 6 个
            int maxProc = Math.Max(1, Math.Min(6, Math.Min(threads / 2, (int)(FeSolver.AvailableMemoryBytes() / (700L << 20)))));
            var done = new List<(string path, ISet<int> accept)>();
            var tmp = Path.Combine(Path.GetTempPath(), "swkit_fea_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var sw = Stopwatch.StartNew();
            try
            {
                var loose = included.ToList();                 // 按设置分组、再均分成几块
                var isolated = new List<List<FeBody>>();       // 超时拆开的：原样单独划
                for (int pass = 0; loose.Count + isolated.Count > 0; pass++)
                {
                    if (pass >= 8) { failed.AddRange(loose); failed.AddRange(isolated.SelectMany(x => x)); break; }
                    var items = new List<List<FeBody>>(isolated);
                    int nLoose = loose.Count;
                    foreach (var g in loose.GroupBy(b => level[b]).OrderBy(g => g.Key))
                    {
                        var list = g.OrderByDescending(b => b.Faces).ToList();
                        int k = Math.Max(1, Math.Min((int)Math.Ceiling(list.Count / 3.0), (int)Math.Round((double)maxProc * list.Count / nLoose)));
                        var chunks = Enumerable.Range(0, k).Select(_ => new List<FeBody>()).ToList();
                        var load = new long[k];
                        foreach (var b in list) { int c = Array.IndexOf(load, load.Min()); chunks[c].Add(b); load[c] += 20 + b.Faces; }
                        items.AddRange(chunks.Where(c => c.Count > 0));
                    }
                    int total = items.Sum(x => x.Count);
                    if (pass == 0) log?.Invoke($"正在划分网格：{total} 个零件，分 {items.Count} 块同时划");
                    else log?.Invoke($"正在重划 {total} 个零件（第 {pass + 1} 轮，已用 {sw.Elapsed.TotalSeconds:0} 秒）");
                    var results = new (HashSet<int> meshed, bool timedOut)[items.Count];
                    var mshs = items.Select((x, i) => Path.Combine(tmp, $"p{pass}_{i}.msh")).ToArray();
                    System.Threading.Tasks.Parallel.For(0, items.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = maxProc }, i =>
                    {
                        var it = items[i];
                        // 同一块里的零件设置相同（超时拆开的块也来自同一档）
                        results[i] = RunGroup(exe, brep, it.Select(b => b.Tag), Levels[level[it[0]]], size, mshs[i], tmp, backgroundPos, bgMin, 90 + 5 * it.Count);
                    });
                    loose = new List<FeBody>(); isolated = new List<List<FeBody>>();
                    for (int i = 0; i < items.Count; i++)
                    {
                        var bodies = items[i]; var r = results[i];
                        if (r.timedOut)
                        {
                            if (bodies.Count > 1) { int h = bodies.Count / 2; isolated.Add(bodies.Take(h).ToList()); isolated.Add(bodies.Skip(h).ToList()); }
                            else if (level[bodies[0]] + 1 < Levels.Length) { level[bodies[0]]++; isolated.Add(bodies); }
                            else failed.Add(bodies[0]);
                            continue;
                        }
                        var ok = new HashSet<int>(bodies.Where(b => r.meshed.Contains(b.Tag)).Select(b => b.Tag));
                        if (ok.Count > 0) done.Add((mshs[i], ok));
                        var missing = bodies.Where(b => !ok.Contains(b.Tag)).OrderBy(b => b.Tag).ToList();
                        if (missing.Count == 0) continue;
                        // 第一个没划出来的是出错的那个；和它形状一样的复制件（同样的面数、尺寸）多半也会错，一起换细一档
                        var bad = missing[0];
                        foreach (var b in missing)
                        {
                            if (b == bad || LookAlike(b, bad))
                            {
                                if (level[b] + 1 < Levels.Length) { level[b]++; loose.Add(b); }
                                else failed.Add(b);
                            }
                            else loose.Add(b);
                        }
                    }
                }
                if (done.Count == 0) throw new InvalidOperationException("所有零件都没有划出网格");
                var m = FeMesh.ReadMsh2(done);
                log?.Invoke($"网格：{m.Elements:N0} 个二次四面体单元，{m.Nodes:N0} 个节点（{sw.Elapsed.TotalSeconds:0} 秒）" +
                            (m.DroppedElements > 0 ? $"；去掉了 {m.DroppedElements} 个几乎没有体积的坏单元（最差质量 {m.WorstQuality:0.0e0}）" : "") +
                            (failed.Count > 0 ? $"；{failed.Count} 个零件划不出网格：" + string.Join("、", failed.Take(4).Select(b => b.Name)) : ""));
                return m;
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        static bool LookAlike(FeBody a, FeBody b)
        {
            if (a.Faces != b.Faces) return false;
            var sa = new[] { a.Size.X, a.Size.Y, a.Size.Z }.OrderBy(x => x).ToArray();
            var sb = new[] { b.Size.X, b.Size.Y, b.Size.Z }.OrderBy(x => x).ToArray();
            for (int k = 0; k < 3; k++) if (Math.Abs(sa[k] - sb[k]) > 1e-4 + 0.01 * sb[k]) return false;
            return true;
        }

        /// <summary>划一组体（其余隐藏，单线程），返回划出了四面体的体编号；超过 timeoutSec 秒（Gmsh 卡死）时杀掉进程，timedOut = true。</summary>
        static (HashSet<int> meshed, bool timedOut) RunGroup(string exe, string brep, IEnumerable<int> tags, (bool extend, double minFrac, int curvature) s, double size,
                                     string msh, string tmp, string backgroundPos, double bgMin, int timeoutSec)
        {
            const int threads = 1;
            var geo = Path.ChangeExtension(msh, ".geo");
            File.WriteAllText(geo, "SetFactory(\"OpenCASCADE\");\n" + $"Merge \"{brep.Replace('\\', '/')}\";\n" +
                                   "Mesh.MeshOnlyVisible = 1;\nHide {:}\n" + $"Recursive Show {{ Volume{{{string.Join(", ", tags)}}}; }}\n", new UTF8Encoding(false));
            string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            double minSize = backgroundPos != null && bgMin > 0 ? Math.Min(bgMin, size * s.minFrac) : size * s.minFrac;
            var args = new StringBuilder();
            args.Append($"\"{geo}\" -3 -order 2 -format msh2 -save_all -o \"{msh}\" -nt {Math.Max(1, threads)} -v 2");
            args.Append($" -setnumber Mesh.MeshSizeMax {F(size)} -setnumber Mesh.MeshSizeMin {F(minSize)}");
            args.Append($" -setnumber Mesh.MeshSizeFromCurvature {s.curvature}");
            if (backgroundPos != null) args.Append($" -bgm \"{backgroundPos}\"");
            if (!s.extend) args.Append(" -setnumber Mesh.MeshSizeExtendFromBoundary 0");
            args.Append(" -setnumber Mesh.Algorithm3D 1 -setnumber Mesh.Optimize 1");   // Delaunay：一个体一个体地划（HXT 把所有体放在一起，一个出错全部失败）
            var psi = new ProcessStartInfo(exe, args.ToString())
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = tmp };
            using (var p = Process.Start(psi))
            {
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                p.OutputDataReceived += (o, e) => { }; p.ErrorDataReceived += (o, e) => { };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutSec * 1000))
                {
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
                    try { File.Delete(msh); } catch { }
                    return (new HashSet<int>(), true);
                }
                p.WaitForExit();
            }
            var meshed = new HashSet<int>();
            if (!File.Exists(msh)) return (meshed, false);
            using (var r = new StreamReader(msh))
            {
                string line;
                while ((line = r.ReadLine()) != null && line != "$Elements") { }
                if (line == null) return (meshed, false);
                int n = int.Parse(r.ReadLine());
                for (int i = 0; i < n; i++)
                {
                    var t = r.ReadLine();
                    // 「编号 类型 标签数 物理组 实体 ...」：只看类型 11（二次四面体）
                    int a = t.IndexOf(' '), b = t.IndexOf(' ', a + 1);
                    if (t.Substring(a + 1, b - a - 1) != "11") continue;
                    var s2 = t.Split(' ');
                    meshed.Add(int.Parse(s2[4]));
                }
            }
            return (meshed, false);
        }

        /// <summary>
        /// 每个单元属于哪个零件（included 里的下标）。布尔分割保留原来的体编号；干涉部分的新体按单元形心落在哪个零件的包围盒里（取最小的那个）。
        /// 同时算出每个零件的体积。
        /// </summary>
        public static void AssignBodies(FeMesh m, IList<FeBody> included)
        {
            int ne = m.Elements;
            var byTag = new Dictionary<int, int>();
            for (int i = 0; i < included.Count; i++) byTag[included[i].Tag] = i;
            var body = new int[ne];
            var newTag = new Dictionary<int, int>();
            foreach (var b in included) b.Volume = 0;
            for (int e = 0; e < ne; e++)
            {
                int tag = m.TetVolume != null ? m.TetVolume[e] : 0;
                if (!byTag.TryGetValue(tag, out var bi) && !newTag.TryGetValue(tag, out bi))
                {
                    var c = (m.P(m.Tets[10 * e]) + m.P(m.Tets[10 * e + 1]) + m.P(m.Tets[10 * e + 2]) + m.P(m.Tets[10 * e + 3])) * 0.25;
                    bi = 0; double best = double.MaxValue;
                    for (int i = 0; i < included.Count; i++)
                    {
                        var b = included[i];
                        const double eps = 1e-5;
                        if (c.X < b.Min.X - eps || c.Y < b.Min.Y - eps || c.Z < b.Min.Z - eps || c.X > b.Max.X + eps || c.Y > b.Max.Y + eps || c.Z > b.Max.Z + eps) continue;
                        double v = Math.Max(b.Size.X, 1e-6) * Math.Max(b.Size.Y, 1e-6) * Math.Max(b.Size.Z, 1e-6);
                        if (v < best) { best = v; bi = i; }
                    }
                    newTag[tag] = bi;
                }
                body[e] = bi;
                var p0 = m.P(m.Tets[10 * e]);
                included[bi].Volume += Math.Abs((m.P(m.Tets[10 * e + 1]) - p0).Dot((m.P(m.Tets[10 * e + 2]) - p0).Cross(m.P(m.Tets[10 * e + 3]) - p0))) / 6;
            }
            m.TetBody = body;
        }

        /// <summary>
        /// 面绑定：零件各自划网格（不做布尔，几百个零件也很快），贴在一起的面绑定（求解时按约束消元，见 FeSystem.SlaveMap）。
        /// 每对相邻零件里体积小的一方做从属面：它表面上的每个节点投影到对方表面三角形上，距离小于容差就绑定
        /// （u_从属 = 投影点处主面三角形 6 个节点按二次形函数插值）。容差取 0.05 mm 和局部单元尺寸 10%（最多 0.3 mm）中较大的，
        /// 模型里差一点点没贴上的面也能连上。返回绑定的节点数。
        /// </summary>
        public static int Tie(FeMesh m, IList<FeBody> included, double tolMm = 0.05)
        {
            if (m.TetBody == null) return 0;
            int nt = m.TriFace.Length;
            var triBody = new int[nt];
            for (int t = 0; t < nt; t++) triBody[t] = m.TriTet[t] >= 0 ? m.TetBody[m.TriTet[t]] : -1;
            // 平均三角形边长 → 网格桶大小
            double edgeSum = 0; int ecount = 0;
            for (int t = 0; t < nt; t += Math.Max(1, nt / 2000)) { edgeSum += (m.P(m.Tris[6 * t]) - m.P(m.Tris[6 * t + 1])).Length; ecount++; }
            double h0 = ecount > 0 ? edgeSum / ecount : 1e-3;
            double cell = Math.Max(h0, 1e-4);
            long Key(int x, int y, int z) => ((long)(x + 1048576) << 42) | ((long)(y + 1048576) << 21) | (long)(z + 1048576);
            var grid = new Dictionary<long, List<int>>();
            double tolMax = 3e-4;
            for (int t = 0; t < nt; t++)
            {
                if (triBody[t] < 0) continue;
                var a = m.P(m.Tris[6 * t]); var b = m.P(m.Tris[6 * t + 1]); var c = m.P(m.Tris[6 * t + 2]);
                int x0 = (int)Math.Floor((Math.Min(a.X, Math.Min(b.X, c.X)) - tolMax) / cell), x1 = (int)Math.Floor((Math.Max(a.X, Math.Max(b.X, c.X)) + tolMax) / cell);
                int y0 = (int)Math.Floor((Math.Min(a.Y, Math.Min(b.Y, c.Y)) - tolMax) / cell), y1 = (int)Math.Floor((Math.Max(a.Y, Math.Max(b.Y, c.Y)) + tolMax) / cell);
                int z0 = (int)Math.Floor((Math.Min(a.Z, Math.Min(b.Z, c.Z)) - tolMax) / cell), z1 = (int)Math.Floor((Math.Max(a.Z, Math.Max(b.Z, c.Z)) + tolMax) / cell);
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                {
                    var k = Key(x, y, z);
                    if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
                    list.Add(t);
                }
            }
            // 每个零件表面上的节点，和节点处的局部单元尺寸（相邻表面三角形最短的平均边长）
            var surfNodes = new Dictionary<int, int>();   // 节点 → 所在零件
            var nodeH = new Dictionary<int, double>();
            for (int t = 0; t < nt; t++)
            {
                if (triBody[t] < 0) continue;
                var a = m.P(m.Tris[6 * t]); var b = m.P(m.Tris[6 * t + 1]); var c = m.P(m.Tris[6 * t + 2]);
                double h = ((a - b).Length + (b - c).Length + (c - a).Length) / 3;
                for (int k = 0; k < 6; k++)
                {
                    int nd = m.Tris[6 * t + k];
                    surfNodes[nd] = triBody[t];
                    nodeH[nd] = nodeH.TryGetValue(nd, out var hPrev) ? Math.Min(hPrev, h) : h;
                }
            }
            double Vol(int b) => b < included.Count ? included[b].Volume : 0;
            // 这对零件里谁是主面：体积大的；一样大按编号
            bool IsMaster(int other, int self) => Vol(other) > Vol(self) || (Vol(other) == Vol(self) && other < self);
            var ties = new List<FeTie>();
            var lockObj = new object();
            var nodes = surfNodes.Keys.ToArray();
            System.Threading.Tasks.Parallel.For(0, nodes.Length, i =>
            {
                int n = nodes[i]; int self = surfNodes[n];
                var p = m.P(n);
                int cx = (int)Math.Floor(p.X / cell), cy = (int)Math.Floor(p.Y / cell), cz = (int)Math.Floor(p.Z / cell);
                if (!grid.TryGetValue(Key(cx, cy, cz), out var cand)) return;
                double best = double.MaxValue; int bt = -1; double bl0 = 0, bl1 = 0, bl2 = 0, bh = 0;
                foreach (var t in cand)
                {
                    int other = triBody[t];
                    if (other == self || !IsMaster(other, self)) continue;
                    var a = m.P(m.Tris[6 * t]); var b = m.P(m.Tris[6 * t + 1]); var c = m.P(m.Tris[6 * t + 2]);
                    ClosestOnTriangle(p, a, b, c, out var q, out var l0, out var l1, out var l2);
                    double d = (p - q).Length;
                    double h = ((a - b).Length + (b - c).Length + (c - a).Length) / 3;
                    double tol = Math.Max(tolMm / 1000, Math.Min(0.1 * h, tolMax));
                    if (d < tol && d < best) { best = d; bt = t; bl0 = l0; bl1 = l1; bl2 = l2; bh = h; }
                }
                if (bt < 0) return;
                var w = new[] { bl0 * (2 * bl0 - 1), bl1 * (2 * bl1 - 1), bl2 * (2 * bl2 - 1), 4 * bl0 * bl1, 4 * bl1 * bl2, 4 * bl2 * bl0 };
                var masters = new int[6]; for (int k = 0; k < 6; k++) masters[k] = m.Tris[6 * bt + k];
                if (masters.Contains(n)) return;
                // 罚刚度按两边较小的局部尺寸：薄板（3 mm 单元）贴在粗网格零件（几十 mm 的三角形）上时，按主面尺寸会比局部刚度大上千倍，迭代很难收敛
                lock (lockObj) ties.Add(new FeTie { Slave = n, Masters = masters, W = w, SlaveBody = self, MasterBody = triBody[bt], H = Math.Min(bh, nodeH[n]) });
            });
            // 只在一个点、或一条线上贴着的两个零件（例如圆柱压在平面上、螺丝头的一圈棱边）：粘上以后能绕这个点 / 线转动（机构），
            // 受力时刚度矩阵奇异、迭代不收敛。这种连接不算：去掉它们的绑定，零件按「没接触上」处理（不受力就固定住，受力就提示）
            m.WeakLinks = new List<(int a, int b, int nodes)>();
            foreach (var g in ties.GroupBy(t => t.SlaveBody < t.MasterBody ? (t.SlaveBody, t.MasterBody) : (t.MasterBody, t.SlaveBody)).ToList())
            {
                var pts = g.Select(t => m.P(t.Slave)).ToList();
                bool weak = pts.Count < 3;
                if (!weak)
                {
                    var c = pts.Aggregate(Vec3.Zero, (s, p) => s + p) * (1.0 / pts.Count);
                    var cov = new double[3, 3];
                    foreach (var p in pts) { var d = p - c; var v = new[] { d.X, d.Y, d.Z }; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) cov[i, j] += v[i] * v[j] / pts.Count; }
                    Eigen3.Symmetric(cov, out var ev, out _);
                    weak = ev[1] < 0.5e-3 * 0.5e-3;    // 第二主方向上的分布不到 ±0.5 mm：基本是一条线
                }
                if (!weak) continue;
                m.WeakLinks.Add((g.Key.Item1, g.Key.Item2, pts.Count));
                var drop = new HashSet<FeTie>(g);
                ties.RemoveAll(drop.Contains);
            }
            m.Ties = ties.Count > 0 ? ties.OrderBy(t => t.Slave).ToList() : null;
            return ties.Count;
        }

        /// <summary>点到三角形的最近点和重心坐标（Ericson, Real-Time Collision Detection 5.1.5）。</summary>
        static void ClosestOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c, out Vec3 q, out double l0, out double l1, out double l2)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            double d1 = ab.Dot(ap), d2 = ac.Dot(ap);
            if (d1 <= 0 && d2 <= 0) { q = a; l0 = 1; l1 = 0; l2 = 0; return; }
            var bp = p - b; double d3 = ab.Dot(bp), d4 = ac.Dot(bp);
            if (d3 >= 0 && d4 <= d3) { q = b; l0 = 0; l1 = 1; l2 = 0; return; }
            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) { double v = d1 / (d1 - d3); q = a + ab * v; l0 = 1 - v; l1 = v; l2 = 0; return; }
            var cp = p - c; double d5 = ab.Dot(cp), d6 = ac.Dot(cp);
            if (d6 >= 0 && d5 <= d6) { q = c; l0 = 0; l1 = 0; l2 = 1; return; }
            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) { double w = d2 / (d2 - d6); q = a + ac * w; l0 = 1 - w; l1 = 0; l2 = w; return; }
            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { double w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); q = b + (c - b) * w; l0 = 0; l1 = 1 - w; l2 = w; return; }
            double denom = 1 / (va + vb + vc); double vv = vb * denom, ww = vc * denom;
            q = a + ab * vv + ac * ww; l0 = 1 - vv - ww; l1 = vv; l2 = ww;
        }

        /// <summary>
        /// 连通性检查：网格按共用节点分成几块（接触粘上的零件在同一块）。没有固定面的块：
        /// · 不受力 → 返回它的节点（求解时固定住，不影响结果）；
        /// · 受力 → 放进 floatingLoaded（这些零件会「飞走」，要提示用户：没接触上，或者要给它加约束）。
        /// </summary>
        public static HashSet<int> FloatingNodes(FeModel md, out List<int> floatingLoadedBodies)
        {
            var m = md.Mesh; int nn = m.Nodes, ne = m.Elements;
            floatingLoadedBodies = new List<int>();
            var parent = new int[nn]; for (int i = 0; i < nn; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int e = 0; e < ne; e++) { int a = Find(m.Tets[10 * e]); for (int k = 1; k < 10; k++) { int b = Find(m.Tets[10 * e + k]); if (a != b) parent[b] = a; } }
            if (m.Ties != null) foreach (var t in m.Ties) { int a = Find(t.Slave), b = Find(t.Masters[0]); if (a != b) parent[b] = a; }
            var fixedRoots = new HashSet<int>(m.FaceNodes(md.FixedFaces).Select(Find));
            var loadedRoots = new HashSet<int>();
            foreach (var l in md.Loads)
            {
                if (l.Kind == LoadKind.Gravity) { for (int n = 0; n < nn; n++) loadedRoots.Add(Find(n)); break; }
                foreach (var n in m.FaceNodes(l.Faces)) loadedRoots.Add(Find(n));
            }
            var result = new HashSet<int>();
            var bodyOfRoot = new Dictionary<int, HashSet<int>>();
            for (int e = 0; e < ne; e++)
            {
                int r = Find(m.Tets[10 * e]);
                if (fixedRoots.Contains(r)) continue;
                if (!bodyOfRoot.TryGetValue(r, out var set)) bodyOfRoot[r] = set = new HashSet<int>();
                set.Add(m.BodyOfElement(e));
            }
            foreach (var kv in bodyOfRoot)
            {
                if (loadedRoots.Contains(kv.Key)) floatingLoadedBodies.AddRange(kv.Value);
                else for (int n = 0; n < nn; n++) if (Find(n) == kv.Key) result.Add(n);
            }
            return result;
        }

        /// <summary>
        /// 零件之间的连通关系（共用节点或面绑定连在一起的算一块）：返回每个零件所在块的编号，
        /// main = 机身主体那一块（包围盒最大的一块：机架从一头伸到另一头，电池、电机这些没粘上的零件自己成块）。
        /// </summary>
        public static int[] Components(FeMesh m, IList<FeBody> included, out int main)
        {
            int nb = included.Count;
            var parent = new int[nb]; for (int i = 0; i < nb; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            if (m.Ties != null) foreach (var t in m.Ties) { int a = Find(t.SlaveBody), b = Find(t.MasterBody); if (a != b) parent[b] = a; }
            var comp = new int[nb];
            for (int i = 0; i < nb; i++) comp[i] = Find(i);
            main = -1; double best = -1;
            foreach (var g in Enumerable.Range(0, nb).GroupBy(i => comp[i]))
            {
                var mn = new Vec3(g.Min(i => included[i].Min.X), g.Min(i => included[i].Min.Y), g.Min(i => included[i].Min.Z));
                var mx = new Vec3(g.Max(i => included[i].Max.X), g.Max(i => included[i].Max.Y), g.Max(i => included[i].Max.Z));
                double d = (mx - mn).Length;
                if (d > best) { best = d; main = g.Key; }
            }
            return comp;
        }

        /// <summary>
        /// 机构检查：把每个零件当成刚体（6 个自由度），绑定点把两个零件在那一点的位移连起来，固定面上的点把零件连到地上，
        /// 求这个小系统的零特征值——每个零模态就是一种「整体能动」的方式（只靠一个点 / 一条线连着的零件能转，
        /// 或者一串零件整体没约束住）。返回每个零模态里动的零件（下标同 included）。几十个零件不到一秒。
        /// extraFixed：已经整体固定的节点（不受力的悬空零件）。
        /// </summary>
        /// <summary>
        /// 刚体刚度的特征值低于 最大对角线 × 这个值，就算「能动」（机构或近似机构）。点 / 线接触在 1e-10 以下；
        /// 只靠一小片接触挂着的（实际装配靠螺丝，例如电机座压在机臂上）在 1e-8 ~ 1e-6——迭代求解几千次也收敛不了；
        /// 牢固的连接（机臂贴在中心板上）在 1e-5 以上。开发时可用 SWKIT_MECH_TOL 改。
        /// </summary>
        public static double MechanismThreshold = double.TryParse(System.Environment.GetEnvironmentVariable("SWKIT_MECH_TOL"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mt) ? mt : 1e-6;

        /// <summary>上一次机构检查最小的几个特征值（相对最大对角线）和对应动的零件（诊断用）。</summary>
        [ThreadStatic] public static List<(double ev, List<int> bodies)> LastModes;

        /// <param name="groundBody">≥ 0 时把这个零件当作固定的参考（还没设约束时，找相对机身主体能动的零件）。</param>
        public static List<List<int>> Mechanisms(FeMesh m, int nBodies, IEnumerable<int> fixedFaces, HashSet<int> extraFixed, int groundBody = -1)
        {
            var res = new List<List<int>>();
            if (m.TetBody == null || nBodies == 0) return res;
            int n = 6 * nBodies;
            var K = new double[n, n];
            // 每个零件的参考点（节点平均位置）和尺度
            var cen = new Vec3[nBodies]; var cnt = new int[nBodies];
            var bodyOfNode = new int[m.Nodes]; for (int i = 0; i < bodyOfNode.Length; i++) bodyOfNode[i] = -1;
            for (int e = 0; e < m.Elements; e++) for (int k = 0; k < 4; k++) bodyOfNode[m.Tets[10 * e + k]] = m.TetBody[e];
            for (int i = 0; i < m.Nodes; i++) if (bodyOfNode[i] >= 0) { cen[bodyOfNode[i]] = cen[bodyOfNode[i]] + m.P(i); cnt[bodyOfNode[i]]++; }
            for (int b = 0; b < nBodies; b++) if (cnt[b] > 0) cen[b] = cen[b] * (1.0 / cnt[b]);
            m.Bounds(out var mn, out var mx); double L = Math.Max((mx - mn).Length, 1e-6);
            // 点 p 处零件 b 的位移 = u_b + θ_b × (p − c_b)：3 行 6 列（转角按 L 缩放，矩阵条件数好一些）
            double[,] Row(int b, Vec3 p)
            {
                var r = (p - cen[b]) * (1 / L);
                // θ × r = (θy rz − θz ry, θz rx − θx rz, θx ry − θy rx)
                return new double[3, 6] { { 1, 0, 0, 0, r.Z, -r.Y }, { 0, 1, 0, -r.Z, 0, r.X }, { 0, 0, 1, r.Y, -r.X, 0 } };
            }
            void Add(int a, double[,] Ga, int b, double[,] Gb, double w)
            {
                // 约束 Ga·q_a − Gb·q_b = 0（b < 0 表示接地）
                for (int i = 0; i < 3; i++)
                    for (int x = 0; x < 6; x++)
                        for (int y = 0; y < 6; y++)
                        {
                            K[6 * a + x, 6 * a + y] += w * Ga[i, x] * Ga[i, y];
                            if (b >= 0)
                            {
                                K[6 * b + x, 6 * b + y] += w * Gb[i, x] * Gb[i, y];
                                K[6 * a + x, 6 * b + y] -= w * Ga[i, x] * Gb[i, y];
                                K[6 * b + x, 6 * a + y] -= w * Gb[i, x] * Ga[i, y];
                            }
                        }
            }
            if (m.Ties != null) foreach (var t in m.Ties) { var p = m.P(t.Slave); Add(t.SlaveBody, Row(t.SlaveBody, p), t.MasterBody, Row(t.MasterBody, p), 1); }
            foreach (var nd in m.FaceNodes(fixedFaces)) { int b = bodyOfNode[nd]; if (b >= 0) Add(b, Row(b, m.P(nd)), -1, null, 1); }
            if (extraFixed != null)
            {
                var whole = new HashSet<int>();
                foreach (var nd in extraFixed) { int b = bodyOfNode[nd]; if (b >= 0) whole.Add(b); }
                foreach (var b in whole) for (int x = 0; x < 6; x++) K[6 * b + x, 6 * b + x] += 1e3;
            }
            if (groundBody >= 0 && groundBody < nBodies) for (int x = 0; x < 6; x++) K[6 * groundBody + x, 6 * groundBody + x] += 1e3;
            double kmax = 0; for (int i = 0; i < n; i++) kmax = Math.Max(kmax, K[i, i]);
            if (kmax <= 0) return res;
            Eigen3.SymmetricN(K, out var ev, out var vec);
            LastModes = new List<(double, List<int>)>();
            for (int k = 0; k < Math.Min(n, 8); k++)
            {
                var v = vec[k]; double vm = 0; var amp = new double[nBodies];
                for (int b = 0; b < nBodies; b++) { double s = 0; for (int x = 0; x < 6; x++) s += v[6 * b + x] * v[6 * b + x]; amp[b] = Math.Sqrt(s); vm = Math.Max(vm, amp[b]); }
                LastModes.Add((ev[k] / kmax, Enumerable.Range(0, nBodies).Where(b => amp[b] > 0.2 * vm).ToList()));
            }
            for (int k = 0; k < n; k++)
            {
                if (ev[k] > MechanismThreshold * kmax) break;
                var v = vec[k];
                double vm = 0; var amp = new double[nBodies];
                for (int b = 0; b < nBodies; b++) { double s = 0; for (int x = 0; x < 6; x++) s += v[6 * b + x] * v[6 * b + x]; amp[b] = Math.Sqrt(s); vm = Math.Max(vm, amp[b]); }
                res.Add(Enumerable.Range(0, nBodies).Where(b => amp[b] > 0.2 * vm && cnt[b] > 0).ToList());
            }
            return res;
        }

        /// <summary>
        /// 每个单元所在的「块」：按共用三角形面连通（只共用一个顶点或一条边的两团单元算两块——它们之间是铰链，能转动）。
        /// 返回块编号；pieces = 块数。
        /// </summary>
        public static int[] FacePieces(FeMesh m, out int pieces)
        {
            int ne = m.Elements;
            var parent = new int[ne]; for (int i = 0; i < ne; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var faceOf = new Dictionary<(int, int, int), int>(4 * ne);
            int[][] fs = { new[] { 1, 2, 3 }, new[] { 0, 2, 3 }, new[] { 0, 1, 3 }, new[] { 0, 1, 2 } };
            for (int e = 0; e < ne; e++)
                foreach (var f in fs)
                {
                    int a = m.Tets[10 * e + f[0]], b = m.Tets[10 * e + f[1]], c = m.Tets[10 * e + f[2]];
                    if (a > b) { var t = a; a = b; b = t; }
                    if (b > c) { var t = b; b = c; c = t; }
                    if (a > b) { var t = a; a = b; b = t; }
                    if (faceOf.TryGetValue((a, b, c), out var o)) { int ra = Find(e), rb = Find(o); if (ra != rb) parent[rb] = ra; }
                    else faceOf[(a, b, c)] = e;
                }
            var id = new Dictionary<int, int>();
            var res = new int[ne];
            for (int e = 0; e < ne; e++) { int r = Find(e); if (!id.TryGetValue(r, out var k)) id[r] = k = id.Count; res[e] = k; }
            pieces = id.Count;
            return res;
        }

        /// <summary>按包围盒把 SolidWorks 的零部件名对应到 STEP 的实体（包围盒重合最多的那个）。</summary>
        public static void NameFrom(IList<FeBody> bodies, IList<(string name, Vec3 min, Vec3 max)> comps)
        {
            if (comps == null || comps.Count == 0) return;
            foreach (var b in bodies)
            {
                double best = 0; string name = null;
                foreach (var (n, mn, mx) in comps)
                {
                    double ox = Math.Min(b.Max.X, mx.X) - Math.Max(b.Min.X, mn.X), oy = Math.Min(b.Max.Y, mx.Y) - Math.Max(b.Min.Y, mn.Y), oz = Math.Min(b.Max.Z, mx.Z) - Math.Max(b.Min.Z, mn.Z);
                    if (ox < -1e-4 || oy < -1e-4 || oz < -1e-4) continue;
                    double inter = Math.Max(ox, 1e-5) * Math.Max(oy, 1e-5) * Math.Max(oz, 1e-5);
                    double vb = Math.Max(b.Size.X, 1e-5) * Math.Max(b.Size.Y, 1e-5) * Math.Max(b.Size.Z, 1e-5);
                    double vc = Math.Max(mx.X - mn.X, 1e-5) * Math.Max(mx.Y - mn.Y, 1e-5) * Math.Max(mx.Z - mn.Z, 1e-5);
                    double score = inter / Math.Max(vb, vc);   // 交并比的近似：两个盒子大小、位置都接近才高
                    if (score > best) { best = score; name = n; }
                }
                if (name != null && best > 0.3) b.Name = name;
            }
            // 重名的加编号
            foreach (var g in bodies.GroupBy(b => b.Name).Where(g => g.Count() > 1))
            {
                int k = 1;
                foreach (var b in g) b.Name = $"{g.Key} #{k++}";
            }
        }
    }
}
