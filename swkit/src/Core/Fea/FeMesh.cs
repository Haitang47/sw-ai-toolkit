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
    /// <summary>零件表面的一个面（对应 STEP / SolidWorks 里的一个面），由若干二次三角形组成。</summary>
    public sealed class FeFace
    {
        public int Id;                     // Gmsh 的曲面编号（= STEP 里的面）
        public List<int> Tris = new List<int>();
        public double Area;
        public Vec3 Centroid;
        public Vec3 Normal;                // 面积加权的平均外法向（平面时就是它的法向）
        public bool IsPlane;
        public override string ToString() => $"面 {Id}（{(IsPlane ? "平面" : "曲面")}，{Area * 1e6:0.#} mm²）";
    }

    /// <summary>
    /// 二次四面体网格（TET10）及其表面（TRI6）。坐标单位米。
    /// 节点编号顺序与 Gmsh 一致：TET10 = 4 个角点 + 边中点 (0-1, 1-2, 2-0, 3-0, 3-2, 3-1)；TRI6 = 3 个角点 + 边中点 (0-1, 1-2, 2-0)。
    /// </summary>
    public sealed class FeMesh
    {
        public double[] X;                 // 3N
        public int[] Tets;                 // 10E
        public int[] Tris;                 // 6T（外表面）
        public int[] TriFace;              // T：所属面编号
        public int[] TriTet;               // T：相邻的四面体（用来确定外法向）
        public List<FeFace> Faces = new List<FeFace>();
        public int Nodes => X.Length / 3;
        public int Elements => Tets.Length / 10;
        public Vec3 P(int n) => new Vec3(X[3 * n], X[3 * n + 1], X[3 * n + 2]);

        public void Bounds(out Vec3 min, out Vec3 max)
        {
            min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            for (int i = 0; i < Nodes; i++)
            {
                var p = P(i);
                min = new Vec3(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new Vec3(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
        }

        /// <summary>零件体积（米³）：用表面三角形按散度定理算。</summary>
        /// <summary>
        /// 零件体积（m³）：散度定理 ∮ x·n dA / 3，表面按二次曲边三角形（TRI6，边中点在曲面上）积分，
        /// 圆孔、圆柱不会像直边三角形那样被弦切掉（直边会偏小 1% 左右）。
        /// </summary>
        public double Volume()
        {
            double v = 0;
            var x = new Vec3[6];
            for (int t = 0; t < TriFace.Length; t++)
            {
                for (int k = 0; k < 6; k++) x[k] = P(Tris[6 * t + k]);
                var outward = TriNormal(t, out _);
                double sign = (x[1] - x[0]).Cross(x[2] - x[0]).Dot(outward) < 0 ? -1 : 1;
                double dv = 0;
                foreach (var q in TriQ6)
                {
                    double xi = q[0], eta = q[1], l0 = 1 - xi - eta;
                    var p = x[0] * (l0 * (2 * l0 - 1)) + x[1] * (xi * (2 * xi - 1)) + x[2] * (eta * (2 * eta - 1)) + x[3] * (4 * l0 * xi) + x[4] * (4 * xi * eta) + x[5] * (4 * eta * l0);
                    var dx = x[0] * -(4 * l0 - 1) + x[1] * (4 * xi - 1) + x[3] * (4 * (l0 - xi)) + x[4] * (4 * eta) + x[5] * (-4 * eta);
                    var de = x[0] * -(4 * l0 - 1) + x[2] * (4 * eta - 1) + x[3] * (-4 * xi) + x[4] * (4 * xi) + x[5] * (4 * (l0 - eta));
                    dv += 0.5 * q[2] * p.Dot(dx.Cross(de)) / 3;
                }
                v += sign * dv;
            }
            return v;
        }

        static readonly double[][] TriQ6 =
        {
            new[] { 0.445948490915965, 0.445948490915965, 0.223381589678011 }, new[] { 0.108103018168070, 0.445948490915965, 0.223381589678011 },
            new[] { 0.445948490915965, 0.108103018168070, 0.223381589678011 }, new[] { 0.091576213509771, 0.091576213509771, 0.109951743655322 },
            new[] { 0.816847572980459, 0.091576213509771, 0.109951743655322 }, new[] { 0.091576213509771, 0.816847572980459, 0.109951743655322 },
        };

        /// <summary>某个面上的所有节点（含边中点）。</summary>
        public HashSet<int> FaceNodes(IEnumerable<int> faceIds)
        {
            var ids = new HashSet<int>(faceIds);
            var r = new HashSet<int>();
            for (int t = 0; t < TriFace.Length; t++)
                if (ids.Contains(TriFace[t])) for (int k = 0; k < 6; k++) r.Add(Tris[6 * t + k]);
            return r;
        }

        // ================= 读 Gmsh 的 .msh（2.2 文本格式） =================

        public static FeMesh ReadMsh2(string path)
        {
            var nodeIndex = new Dictionary<int, int>();
            var xs = new List<double>();
            var tets = new List<int>();
            var tris = new List<int>();
            var triFace = new List<int>();
            using (var r = new StreamReader(path))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line == "$Nodes")
                    {
                        int n = int.Parse(r.ReadLine());
                        for (int i = 0; i < n; i++)
                        {
                            var s = r.ReadLine().Split(' ');
                            nodeIndex[int.Parse(s[0])] = xs.Count / 3;
                            xs.Add(double.Parse(s[1], CultureInfo.InvariantCulture));
                            xs.Add(double.Parse(s[2], CultureInfo.InvariantCulture));
                            xs.Add(double.Parse(s[3], CultureInfo.InvariantCulture));
                        }
                    }
                    else if (line == "$Elements")
                    {
                        int n = int.Parse(r.ReadLine());
                        for (int i = 0; i < n; i++)
                        {
                            var s = r.ReadLine().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            int type = int.Parse(s[1]), ntags = int.Parse(s[2]);
                            int entity = ntags >= 2 ? int.Parse(s[4]) : 0;
                            int first = 3 + ntags;
                            if (type == 11)      // 10 节点四面体
                                for (int k = 0; k < 10; k++) tets.Add(nodeIndex[int.Parse(s[first + k])]);
                            else if (type == 9)  // 6 节点三角形
                            {
                                for (int k = 0; k < 6; k++) tris.Add(nodeIndex[int.Parse(s[first + k])]);
                                triFace.Add(entity);
                            }
                        }
                    }
                }
            }
            if (tets.Count == 0) throw new InvalidOperationException("网格里没有二次四面体单元（零件可能不是实体，或网格划分失败）");
            var m = new FeMesh { X = xs.ToArray(), Tets = tets.ToArray(), Tris = tris.ToArray(), TriFace = triFace.ToArray() };
            m.StraightenedElements = m.StraightenInverted();
            m.BuildFaces();
            return m;
        }

        /// <summary>读网格时拉直了几个翻转的曲边单元（见 StraightenInverted）。</summary>
        public int StraightenedElements;

        static readonly int[,] TetEdges = { { 0, 1 }, { 1, 2 }, { 2, 0 }, { 3, 0 }, { 3, 2 }, { 3, 1 } };

        /// <summary>
        /// 曲面附近的二次单元偶尔会被 Gmsh 弯得「翻过来」（积分点处雅可比 ≤ 0，常见于很薄的台阶、
        /// 两个同轴圆柱面接在一起的孔）。把这些单元的边中点拉回直边中点（在那里退回成直边单元），
        /// 几何上只差几微米，但刚度矩阵就能正常组装了。返回拉直的单元数。
        /// </summary>
        public int StraightenInverted()
        {
            int total = 0, ne = Elements;
            var bad = new bool[ne];
            for (int pass = 0; pass < 3; pass++)
            {
                System.Threading.Tasks.Parallel.For(0, ne, () => (new double[10], new double[10, 3], new double[10, 3]), (e, st, s) =>
                {
                    bad[e] = false;
                    foreach (var g in FeSolver.Gauss)
                        if (FeSolver.Gradients(this, e, g, s.Item1, s.Item2, s.Item3) <= 0) { bad[e] = true; break; }
                    return s;
                }, s => { });
                int n = 0;
                for (int e = 0; e < ne; e++)
                {
                    if (!bad[e]) continue;
                    n++;
                    for (int k = 0; k < 6; k++)
                    {
                        int mid = Tets[10 * e + 4 + k], a = Tets[10 * e + TetEdges[k, 0]], b = Tets[10 * e + TetEdges[k, 1]];
                        for (int c = 0; c < 3; c++) X[3 * mid + c] = 0.5 * (X[3 * a + c] + X[3 * b + c]);
                    }
                }
                if (pass == 0) total = n;
                if (n == 0) break;
            }
            return total;
        }

        /// <summary>找每个表面三角形相邻的四面体（确定外法向），并汇总每个面的面积、形心、法向。</summary>
        void BuildFaces()
        {
            var faceOf = new Dictionary<(int, int, int), int>();
            int[][] tetFaces = { new[] { 1, 2, 3 }, new[] { 0, 2, 3 }, new[] { 0, 1, 3 }, new[] { 0, 1, 2 } };
            for (int e = 0; e < Elements; e++)
                foreach (var f in tetFaces)
                    faceOf[Key(Tets[10 * e + f[0]], Tets[10 * e + f[1]], Tets[10 * e + f[2]])] = e;
            TriTet = new int[TriFace.Length];
            var byId = new Dictionary<int, FeFace>();
            for (int t = 0; t < TriFace.Length; t++)
            {
                int a = Tris[6 * t], b = Tris[6 * t + 1], c = Tris[6 * t + 2];
                TriTet[t] = faceOf.TryGetValue(Key(a, b, c), out var e) ? e : -1;
                if (!byId.TryGetValue(TriFace[t], out var face)) byId[TriFace[t]] = face = new FeFace { Id = TriFace[t] };
                face.Tris.Add(t);
                var n = TriNormal(t, out var area);
                var cen = (P(a) + P(b) + P(c)) / 3;
                face.Area += area;
                face.Centroid = face.Centroid + cen * area;
                face.Normal = face.Normal + n * area;
            }
            foreach (var f in byId.Values)
            {
                f.Centroid = f.Centroid / f.Area;
                var avg = f.Normal.Length / f.Area;    // 平面时为 1
                f.Normal = f.Normal.Normalized();
                f.IsPlane = avg > 0.9999;
            }
            Faces = byId.Values.OrderBy(f => f.Id).ToList();
        }

        static (int, int, int) Key(int a, int b, int c)
        {
            if (a > b) { var t = a; a = b; b = t; }
            if (b > c) { var t = b; b = c; c = t; }
            if (a > b) { var t = a; a = b; b = t; }
            return (a, b, c);
        }

        /// <summary>表面三角形（按角点）的单位外法向和面积。</summary>
        public Vec3 TriNormal(int t, out double area)
        {
            var a = P(Tris[6 * t]); var b = P(Tris[6 * t + 1]); var c = P(Tris[6 * t + 2]);
            var n = (b - a).Cross(c - a);
            area = n.Length / 2;
            n = n.Normalized();
            var e = TriTet != null && t < TriTet.Length ? TriTet[t] : -1;
            if (e >= 0)
            {
                // 外法向：背离四面体的第四个顶点
                var tc = Vec3.Zero;
                for (int k = 0; k < 4; k++) tc = tc + P(Tets[10 * e + k]);
                tc = tc / 4;
                if (n.Dot((a + b + c) / 3 - tc) < 0) n = -n;
            }
            return n;
        }
    }

    /// <summary>调用 Gmsh 划分网格。</summary>
    public static class Gmsh
    {
        /// <summary>找 gmsh.exe：程序旁边的 gmsh 文件夹，或开发时的 tools\gmsh。</summary>
        public static string FindExe()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[] { Path.Combine(dir, "gmsh", "gmsh.exe"), Path.Combine(dir, "..", "tools", "gmsh", "gmsh.exe") })
                if (File.Exists(c)) return Path.GetFullPath(c);
            return null;
        }

        /// <summary>
        /// 划分二次四面体网格。input 可以是 .step / .stp（坐标会换成米）或 .geo（几何直接用米写）。
        /// size：最大单元尺寸（米）。按曲率自动加密，保证圆孔、圆角上有足够的单元。
        /// </summary>
        public static FeMesh Mesh(string input, double size, Action<string> log, int threads = 1, string backgroundPos = null, double minSize = 0, int curvature = 16)
        {
            // 先用 HXT（比 Delaunay 快约 40%）；单线程，同样的输入每次得到同样的网格（多线程时每次略有不同，结果不可复现）。
            // HXT 偶尔对个别几何失败，这时改用 Delaunay 再划一次。
            try { return MeshWith(input, size, log, 10, backgroundPos, minSize, curvature); }
            catch (TimeoutException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke("网格划分换一种算法重试（" + ex.Message + "）");
                return MeshWith(input, size, log, 1, backgroundPos, minSize, curvature);
            }
        }

        static FeMesh MeshWith(string input, double size, Action<string> log, int algorithm3D, string backgroundPos, double minSize, int curvature)
        {
            var exe = FindExe() ?? throw new InvalidOperationException("找不到 gmsh.exe（应该在程序旁边的 gmsh 文件夹里）");
            var tmp = Path.Combine(Path.GetTempPath(), "swkit_fea_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var msh = Path.Combine(tmp, "mesh.msh");
            var args = new StringBuilder();
            args.Append($"\"{input}\" -3 -order 2 -format msh2 -save_all -o \"{msh}\" -nt 1 -v 2");
            args.Append(" -setstring Geometry.OCCTargetUnit M");
            args.Append($" -setnumber Mesh.MeshSizeMax {size.ToString("R", CultureInfo.InvariantCulture)}");
            args.Append($" -setnumber Mesh.MeshSizeMin {(minSize > 0 ? minSize : size / 8).ToString("R", CultureInfo.InvariantCulture)}");
            // 自适应加密：背景网格（.pos 里每个点的目标单元尺寸），在误差大的地方加密
            if (backgroundPos != null) args.Append($" -bgm \"{backgroundPos}\" -setnumber Mesh.MeshSizeExtendFromBoundary 0");
            args.Append($" -setnumber Mesh.MeshSizeFromCurvature {curvature}");   // 一整圈至少几个单元（圆孔、圆角处加密）
            args.Append($" -setnumber Mesh.Algorithm3D {algorithm3D}");
            args.Append(" -setnumber Mesh.Optimize 1");
            var psi = new ProcessStartInfo(exe, args.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            var output = new StringBuilder();
            try
            {
                using (var p = Process.Start(psi))
                {
                    try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // 不和用户正在用的 SolidWorks 抢 CPU
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(10 * 60 * 1000)) { try { p.Kill(); } catch { } throw new TimeoutException("网格划分超过 10 分钟，已停止。请把网格调粗一些"); }
                    p.WaitForExit();
                    if (p.ExitCode != 0 || !File.Exists(msh))
                        throw new InvalidOperationException("网格划分失败：" + LastLines(output.ToString(), 6));
                }
                var m = FeMesh.ReadMsh2(msh);
                log?.Invoke($"网格：{m.Elements:N0} 个二次四面体单元，{m.Nodes:N0} 个节点，{m.Faces.Count} 个面");
                return m;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
        /// <summary>零件的包围盒（米）：只划一维网格（很快），读节点坐标。</summary>
        public static void Bounds(string input, out Vec3 min, out Vec3 max)
        {
            var exe = FindExe() ?? throw new InvalidOperationException("找不到 gmsh.exe（应该在程序旁边的 gmsh 文件夹里）");
            var tmp = Path.Combine(Path.GetTempPath(), "swkit_fea_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var msh = Path.Combine(tmp, "b.msh");
            try
            {
                var psi = new ProcessStartInfo(exe, $"\"{input}\" -1 -format msh2 -save_all -o \"{msh}\" -v 1 -setstring Geometry.OCCTargetUnit M -setnumber Mesh.MeshSizeMax 1e9")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } }
                }
                if (!File.Exists(msh)) throw new InvalidOperationException("读取零件几何失败（文件可能不是实体 STEP）");
                min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
                max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
                var lines = File.ReadAllLines(msh);
                int i = Array.IndexOf(lines, "$Nodes");
                int n = int.Parse(lines[i + 1]);
                for (int k = 0; k < n; k++)
                {
                    var s = lines[i + 2 + k].Split(' ');
                    var v = new Vec3(double.Parse(s[1], CultureInfo.InvariantCulture), double.Parse(s[2], CultureInfo.InvariantCulture), double.Parse(s[3], CultureInfo.InvariantCulture));
                    min = new Vec3(Math.Min(min.X, v.X), Math.Min(min.Y, v.Y), Math.Min(min.Z, v.Z));
                    max = new Vec3(Math.Max(max.X, v.X), Math.Max(max.Y, v.Y), Math.Max(max.Z, v.Z));
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        static string LastLines(string s, int n) => string.Join(" / ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Reverse().Take(n).Reverse());
    }
}
