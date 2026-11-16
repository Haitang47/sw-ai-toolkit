using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SwKit.Core.Fea;
using SwKit.Core.Urdf;

namespace SwKit.Core.Dfm
{
    public enum FaceKind { Plane, Cylinder, Other }

    /// <summary>零件上的一个面（STEP 的面，或 STL 按法向连续分出来的区域），附带平面 / 圆柱识别结果。</summary>
    public sealed class DfmFace
    {
        public int Id;
        public List<int> Tris = new List<int>();
        public double Area;
        public Vec3 Centroid;
        public FaceKind Kind = FaceKind.Other;
        public Vec3 Normal;                       // 平面的法向（朝外）
        public Vec3 Axis, AxisPoint;              // 圆柱轴线
        public double Radius, Coverage, T0, T1;   // 半径（mm）、绕轴角度覆盖（弧度）、沿轴范围
        public bool Concave;                      // 圆柱是孔 / 内圆角（材料在外侧）
        public double Length => T1 - T0;
        public override string ToString() => Kind == FaceKind.Cylinder ? $"{(Concave ? "孔" : "轴")} R{Radius:0.##}" : Kind.ToString();
    }

    /// <summary>
    /// 可制造性分析用的表面三角网格（单位 mm，法向朝外，三角形一致定向）。
    /// 来源：STL，或者 STEP 经 Gmsh 划分表面网格（保留每个面的编号）。
    /// </summary>
    public sealed class DfmMesh
    {
        public double[] V;        // 3 × 顶点数（mm）
        public int[] T;           // 3 × 三角形数
        public int[] FaceOf;      // 每个三角形所属面的编号（-1 = 未分）
        public string Source, Note;
        public bool FromStep;

        public int Tris => T.Length / 3;
        public int Verts => V.Length / 3;
        public Vec3 P(int i) => new Vec3(V[3 * i], V[3 * i + 1], V[3 * i + 2]);

        // 由 Prepare() 计算
        public Vec3[] N, C; public double[] A;
        public int[] Adj;         // 3 × 三角形数：第 k 条边（v_k → v_{k+1}）对面的三角形，-1 = 边界
        public Vec3 Min, Max;
        public double Volume, Area, Diag;
        public List<DfmFace> Faces = new List<DfmFace>();
        public Dictionary<int, DfmFace> FaceById = new Dictionary<int, DfmFace>();
        public bool Closed;       // 没有边界边（水密）

        // ================= 读入 =================

        public static DfmMesh Load(string path, Action<string> log)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            DfmMesh m = ext == ".stl" ? LoadStl(path) : ext == ".step" || ext == ".stp" ? LoadStep(path, log) :
                throw new ArgumentException("只支持 .stl、.step、.stp（.sldprt 先在后台 SolidWorks 里转成 STEP）");
            m.Source = path;
            m.Prepare();
            log?.Invoke($"表面网格：{m.Tris:N0} 个三角形，{m.Faces.Count} 个面，体积 {m.Volume / 1000:0.##} cm³");
            return m;
        }

        public static DfmMesh LoadStl(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var pts = new List<double>();
            bool binary = bytes.Length >= 84 && 84 + 50L * BitConverter.ToUInt32(bytes, 80) == bytes.Length;
            if (binary)
            {
                int n = (int)BitConverter.ToUInt32(bytes, 80);
                for (int t = 0; t < n; t++)
                {
                    int o = 84 + 50 * t + 12;
                    for (int k = 0; k < 9; k++) pts.Add(BitConverter.ToSingle(bytes, o + 4 * k));
                }
            }
            else
            {
                foreach (var line in Encoding.ASCII.GetString(bytes).Split('\n'))
                {
                    var s = line.Trim();
                    if (!s.StartsWith("vertex")) continue;
                    var p = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int k = 1; k <= 3; k++) pts.Add(double.Parse(p[k], CultureInfo.InvariantCulture));
                }
            }
            var m = Weld(pts, null);
            // 单位：STL 没有单位。尺寸小于 1（大概率是米）时换成毫米
            double ext = 0;
            for (int i = 0; i < m.V.Length; i++) ext = Math.Max(ext, Math.Abs(m.V[i]));
            if (ext > 0 && ext < 1.0) { for (int i = 0; i < m.V.Length; i++) m.V[i] *= 1000; m.Note = "STL 尺寸小于 1，按米处理，已换算成毫米"; }
            return m;
        }

        /// <summary>STEP：用 Gmsh 划分表面三角网格（按曲率加密，保留面编号），单位 mm。</summary>
        public static DfmMesh LoadStep(string path, Action<string> log)
        {
            var exe = Gmsh.FindExe() ?? throw new InvalidOperationException("找不到 gmsh.exe（应该在程序旁边的 gmsh 文件夹里）");
            var input = path;
            string tmpCopy = null;
            if (path.Any(c => c > 127))
            {
                tmpCopy = Path.Combine(Path.GetTempPath(), "swkit_dfm_" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                File.Copy(path, tmpCopy, true);
                input = tmpCopy;
            }
            Gmsh.Bounds(input, out var bmin, out var bmax);
            double diag = (bmax - bmin).Length * 1000;
            var tmp = Path.Combine(Path.GetTempPath(), "swkit_dfm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var msh = Path.Combine(tmp, "s.msh");
            try
            {
                log?.Invoke("正在生成表面网格（Gmsh）");
                var ci = CultureInfo.InvariantCulture;
                var args = $"\"{input}\" -2 -format msh2 -save_all -o \"{msh}\" -nt 1 -v 1 -setstring Geometry.OCCTargetUnit MM" +
                           $" -setnumber Mesh.MeshSizeMax {(diag / 50).ToString("R", ci)} -setnumber Mesh.MeshSizeMin {(diag / 3000).ToString("R", ci)}" +
                           " -setnumber Mesh.MeshSizeFromCurvature 20 -setnumber Mesh.Algorithm 6";
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                    var o1 = p.StandardOutput.ReadToEndAsync(); var o2 = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(5 * 60 * 1000)) { try { p.Kill(); } catch { } throw new TimeoutException("表面网格划分超过 5 分钟"); }
                    if (p.ExitCode != 0 || !File.Exists(msh)) throw new InvalidOperationException("表面网格划分失败：" + (o2.Result + o1.Result).Trim().Split('\n').LastOrDefault());
                }
                var m = ReadMsh2(msh);
                m.FromStep = true;
                return m;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
                if (tmpCopy != null) try { File.Delete(tmpCopy); } catch { }
            }
        }

        static DfmMesh ReadMsh2(string path)
        {
            var idx = new Dictionary<int, int>();
            var xs = new List<double>(); var tris = new List<int>(); var face = new List<int>();
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
                            idx[int.Parse(s[0])] = xs.Count / 3;
                            xs.Add(double.Parse(s[1], CultureInfo.InvariantCulture)); xs.Add(double.Parse(s[2], CultureInfo.InvariantCulture)); xs.Add(double.Parse(s[3], CultureInfo.InvariantCulture));
                        }
                    }
                    else if (line == "$Elements")
                    {
                        int n = int.Parse(r.ReadLine());
                        for (int i = 0; i < n; i++)
                        {
                            var s = r.ReadLine().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (int.Parse(s[1]) != 2) continue;   // 3 节点三角形
                            int nt = int.Parse(s[2]); int first = 3 + nt;
                            for (int k = 0; k < 3; k++) tris.Add(idx[int.Parse(s[first + k])]);
                            face.Add(nt >= 2 ? int.Parse(s[4]) : 0);
                        }
                    }
                }
            }
            if (tris.Count == 0) throw new InvalidOperationException("表面网格是空的（文件可能不是实体）");
            return new DfmMesh { V = xs.ToArray(), T = tris.ToArray(), FaceOf = face.ToArray() };
        }

        /// <summary>三角形汤 → 共享顶点（按 1e-4 mm 量化合并）。</summary>
        static DfmMesh Weld(List<double> pts, List<int> faceOf)
        {
            var map = new Dictionary<(long, long, long), int>();
            var v = new List<double>(); var t = new List<int>(pts.Count / 3);
            for (int i = 0; i + 2 < pts.Count; i += 3)
            {
                var key = ((long)Math.Round(pts[i] * 1e4), (long)Math.Round(pts[i + 1] * 1e4), (long)Math.Round(pts[i + 2] * 1e4));
                if (!map.TryGetValue(key, out var k)) { k = v.Count / 3; v.Add(pts[i]); v.Add(pts[i + 1]); v.Add(pts[i + 2]); map[key] = k; }
                t.Add(k);
            }
            // 去掉退化三角形
            var tt = new List<int>(t.Count);
            for (int i = 0; i < t.Count; i += 3)
                if (t[i] != t[i + 1] && t[i + 1] != t[i + 2] && t[i] != t[i + 2]) { tt.Add(t[i]); tt.Add(t[i + 1]); tt.Add(t[i + 2]); }
            return new DfmMesh { V = v.ToArray(), T = tt.ToArray(), FaceOf = Enumerable.Repeat(-1, tt.Count / 3).ToArray() };
        }

        // ================= 整理：定向、法向、邻接、分面、识别平面 / 圆柱 =================

        public void Prepare()
        {
            int m = Tris;
            BuildAdjacency();
            OrientConsistently();
            N = new Vec3[m]; C = new Vec3[m]; A = new double[m];
            Min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue); Max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            for (int i = 0; i < Verts; i++)
            {
                var p = P(i);
                Min = new Vec3(Math.Min(Min.X, p.X), Math.Min(Min.Y, p.Y), Math.Min(Min.Z, p.Z));
                Max = new Vec3(Math.Max(Max.X, p.X), Math.Max(Max.Y, p.Y), Math.Max(Max.Z, p.Z));
            }
            Diag = (Max - Min).Length;
            double vol = 0, area = 0;
            for (int t = 0; t < m; t++)
            {
                var a = P(T[3 * t]); var b = P(T[3 * t + 1]); var c = P(T[3 * t + 2]);
                var cr = (b - a).Cross(c - a);
                A[t] = cr.Length / 2; N[t] = cr.Normalized(); C[t] = (a + b + c) / 3;
                vol += a.Dot(b.Cross(c)) / 6; area += A[t];
            }
            if (vol < 0)
            {
                // 整体朝里：全部翻转
                for (int t = 0; t < m; t++) { var x = T[3 * t + 1]; T[3 * t + 1] = T[3 * t + 2]; T[3 * t + 2] = x; N[t] = -N[t]; }
                vol = -vol;
                BuildAdjacency();
            }
            Volume = vol; Area = area;
            if (!FromStep || FaceOf.All(f => f <= 0)) Segment();
            BuildFaces();
        }

        void BuildAdjacency()
        {
            int m = Tris;
            Adj = new int[3 * m];
            var edges = new Dictionary<long, int>(3 * m);
            for (int i = 0; i < Adj.Length; i++) Adj[i] = -1;
            int boundary = 0;
            for (int t = 0; t < m; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = T[3 * t + k], b = T[3 * t + (k + 1) % 3];
                    long key = Math.Min(a, b) * (long)Verts + Math.Max(a, b);
                    if (edges.TryGetValue(key, out var o))
                    {
                        int ot = o / 3;
                        Adj[3 * t + k] = ot; Adj[o] = t;
                        edges.Remove(key);
                    }
                    else edges[key] = 3 * t + k;
                }
            boundary = edges.Count;
            Closed = boundary == 0;
        }

        /// <summary>沿共享边传播，让相邻三角形的绕向一致（两侧走同一条边的方向相反）。</summary>
        void OrientConsistently()
        {
            int m = Tris;
            var done = new bool[m];
            var q = new Queue<int>();
            for (int s = 0; s < m; s++)
            {
                if (done[s]) continue;
                done[s] = true; q.Enqueue(s);
                while (q.Count > 0)
                {
                    int t = q.Dequeue();
                    for (int k = 0; k < 3; k++)
                    {
                        int o = Adj[3 * t + k];
                        if (o < 0 || done[o]) continue;
                        int a = T[3 * t + k], b = T[3 * t + (k + 1) % 3];
                        // 相邻三角形里这条边应该是 b → a
                        bool same = false;
                        for (int j = 0; j < 3; j++) if (T[3 * o + j] == a && T[3 * o + (j + 1) % 3] == b) same = true;
                        if (same) { var x = T[3 * o + 1]; T[3 * o + 1] = T[3 * o + 2]; T[3 * o + 2] = x; }
                        done[o] = true; q.Enqueue(o);
                    }
                }
            }
            BuildAdjacency();
        }

        /// <summary>STL 没有面信息：按法向连续（相邻三角形法向夹角小于 20°）分区。</summary>
        void Segment()
        {
            int m = Tris, id = 0;
            for (int i = 0; i < m; i++) FaceOf[i] = -1;
            double cosLim = Math.Cos(20 * Math.PI / 180);
            var q = new Queue<int>();
            for (int s = 0; s < m; s++)
            {
                if (FaceOf[s] >= 0) continue;
                FaceOf[s] = ++id; q.Enqueue(s);
                while (q.Count > 0)
                {
                    int t = q.Dequeue();
                    for (int k = 0; k < 3; k++)
                    {
                        int o = Adj[3 * t + k];
                        if (o < 0 || FaceOf[o] >= 0) continue;
                        if (N[t].Dot(N[o]) < cosLim) continue;
                        FaceOf[o] = id; q.Enqueue(o);
                    }
                }
            }
        }

        void BuildFaces()
        {
            FaceById.Clear();
            for (int t = 0; t < Tris; t++)
            {
                if (!FaceById.TryGetValue(FaceOf[t], out var f)) FaceById[FaceOf[t]] = f = new DfmFace { Id = FaceOf[t] };
                f.Tris.Add(t);
                f.Area += A[t];
                f.Centroid = f.Centroid + C[t] * A[t];
            }
            Faces = FaceById.Values.OrderBy(f => f.Id).ToList();
            foreach (var f in Faces) { f.Centroid = f.Centroid / Math.Max(f.Area, 1e-30); Classify(f); }
        }

        void Classify(DfmFace f)
        {
            var n = Vec3.Zero;
            foreach (var t in f.Tris) n = n + N[t] * A[t];
            double avg = n.Length / Math.Max(f.Area, 1e-30);
            if (avg > 0.9995) { f.Kind = FaceKind.Plane; f.Normal = n.Normalized(); return; }
            if (f.Tris.Count < 4) return;
            // 圆柱：所有法向都垂直于同一个轴（法向协方差最小特征值对应的方向）
            var M = new double[3, 3];
            foreach (var t in f.Tris) { var v = N[t]; double w = A[t]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) M[i, j] += w * v[i] * v[j]; }
            var ev = LinAlg.SymEigen(M, out var vec);
            int kMin = ev[0] <= ev[1] && ev[0] <= ev[2] ? 0 : ev[1] <= ev[2] ? 1 : 2;
            var axis = new Vec3(vec[0, kMin], vec[1, kMin], vec[2, kMin]).Normalized();
            if (f.Tris.Any(t => Math.Abs(N[t].Dot(axis)) > 0.08)) return;
            // 在垂直于轴的平面里拟合圆（Kåsa 最小二乘）
            axis.Perpendiculars(out var e1, out var e2);
            var verts = f.Tris.SelectMany(t => new[] { T[3 * t], T[3 * t + 1], T[3 * t + 2] }).Distinct().ToList();
            int cnt = verts.Count;
            double mu = 0, mv = 0;
            foreach (var vi in verts) { var p = P(vi); mu += p.Dot(e1); mv += p.Dot(e2); }
            mu /= cnt; mv /= cnt;
            double Suu = 0, Svv = 0, Suv = 0, Su3 = 0, Sv3 = 0, Sr = 0;
            foreach (var vi in verts)
            {
                var p = P(vi); double u = p.Dot(e1) - mu, w = p.Dot(e2) - mv, rr = u * u + w * w;
                Suu += u * u; Svv += w * w; Suv += u * w; Su3 += u * rr; Sv3 += w * rr; Sr += rr;
            }
            // 最小化 Σ(u² + w² + D·u + E·w + F)²（中心化后方程解耦）
            double det = Suu * Svv - Suv * Suv;
            if (Math.Abs(det) < 1e-18) return;
            double D = -(Su3 * Svv - Sv3 * Suv) / det, E = -(Sv3 * Suu - Su3 * Suv) / det, F = -Sr / cnt;
            double uc = -D / 2, vc = -E / 2;
            double r2 = uc * uc + vc * vc - F;
            if (r2 <= 0) return;
            double r = Math.Sqrt(r2);
            uc += mu; vc += mv;            // 拟合误差
            double err = 0;
            var angles = new List<double>();
            double tMin = double.MaxValue, tMax = double.MinValue;
            foreach (var vi in verts)
            {
                var p = P(vi); double u = p.Dot(e1) - uc, w = p.Dot(e2) - vc;
                err = Math.Max(err, Math.Abs(Math.Sqrt(u * u + w * w) - r));
                angles.Add(Math.Atan2(w, u));
                var ta = p.Dot(axis); tMin = Math.Min(tMin, ta); tMax = Math.Max(tMax, ta);
            }
            if (r <= 0 || err > Math.Max(0.02 * r, 0.02)) return;
            angles.Sort();
            double gap = 2 * Math.PI - (angles[angles.Count - 1] - angles[0]);
            for (int i = 1; i < angles.Count; i++) gap = Math.Max(gap, angles[i] - angles[i - 1]);
            f.Kind = FaceKind.Cylinder; f.Axis = axis; f.Radius = r;
            f.AxisPoint = e1 * uc + e2 * vc;
            f.Coverage = 2 * Math.PI - gap;
            f.T0 = tMin; f.T1 = tMax;
            // 凹（孔）：法向指向轴线
            double s = 0;
            foreach (var t in f.Tris) { var d = C[t] - f.AxisPoint; d = d - axis * d.Dot(axis); s += N[t].Dot(d) * A[t]; }
            f.Concave = s < 0;
        }

        /// <summary>按旋转矩阵变换后的点（用于摆放方向）。</summary>
        public double Extent(Vec3 dir, out double min, out double max)
        {
            min = double.MaxValue; max = double.MinValue;
            for (int i = 0; i < Verts; i++) { var h = P(i).Dot(dir); if (h < min) min = h; if (h > max) max = h; }
            return max - min;
        }
    }
}
