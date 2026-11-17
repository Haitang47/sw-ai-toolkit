using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwKit.Core.Urdf;

namespace SwKit.Core.Cfd
{
    /// <summary>格子类型：flags 的低 3 位是类型，8 = 格子里有作动盘体积力，16 = 流体格子旁边有固体（要做反弹）。</summary>
    internal static class CellType
    {
        public const byte Fluid = 0, Solid = 1, Wall = 2, Far = 3, Inlet = 4, Outflow = 5, Forced = 8, NearWall = 16;
        public static bool IsWall(byte f) { int t = f & 7; return t == Solid || t == Wall; }
    }

    /// <summary>
    /// 计算域：模型坐标轴对齐的长方体规则网格。最外一层格子是边界（远场 / 入口 / 出口 / 地面），
    /// 格子 (i, j, k) 的中心在 Origin + (i + 0.5, j + 0.5, k + 0.5)·Dx。
    /// </summary>
    internal sealed class CfdDomain
    {
        public double Dx;
        public Vec3 Origin;
        public int Nx, Ny, Nz;
        public long N => (long)Nx * Ny * Nz;
        public int UpAxis, UpSign;           // 「上」是哪根轴、正负
        public int H1, H2;                   // 两根水平轴（和界面的 Horizontal() 一致）
        public byte[] FaceType = new byte[6];   // 下标 2·轴 + (0 = 低端面, 1 = 高端面)
        public Vec3 GeoMin, GeoMax;          // 几何 ∪ 桨盘的包围盒
        public double Span;                  // 水平跨度
        public Vec3 WakeDir;                 // 尾流方向（单位向量）
        public double VFarMax;               // 最强桨盘的远尾流速度 2·v_i（m/s）
        public double VFarMean;              // 全部桨盘按总推力、总面积算的远尾流速度
        public double[] Pad = new double[6]; // 各个面的延伸量（m）
        public long CappedFrom;              // 目标格子数被设备上限截断前的值（0 = 没截断）

        public int Dim(int a) => a == 0 ? Nx : a == 1 ? Ny : Nz;
        public double Extent(int a) => Dim(a) * Dx;
        public Vec3 Center(int i, int j, int k) => new Vec3(Origin.X + (i + 0.5) * Dx, Origin.Y + (j + 0.5) * Dx, Origin.Z + (k + 0.5) * Dx);

        /// <summary>动量理论的诱导速度 v_i = √(T / (2ρA))，A 取桨毂外的环形面积。</summary>
        public static double IdealInflow(CfdDisk d, double rho) => Math.Sqrt(Math.Max(0, d.Thrust) / (2 * rho * DiskArea(d)));
        public static double DiskArea(CfdDisk d)
        {
            double rh = Math.Max(0, Math.Min(d.HubRadius, 0.95 * d.Radius));
            return Math.PI * (d.Radius * d.Radius - rh * rh);
        }

        static double Comp(Vec3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
        static Vec3 Axis(int a, double s = 1) => a == 0 ? new Vec3(s, 0, 0) : a == 1 ? new Vec3(0, s, 0) : new Vec3(0, 0, s);

        /// <summary>按设置算出计算域。maxCells：设备能放下的格子数（自动网格时截断目标格子数）。</summary>
        public static CfdDomain Build(CfdSetup s, long maxCells)
        {
            var d = new CfdDomain();
            // ---- 上方向（只支持坐标轴方向）----
            var up = s.Up.Length > 1e-12 ? s.Up.Normalized() : new Vec3(0, 1, 0);
            d.UpAxis = Math.Abs(up.X) >= Math.Abs(up.Y) && Math.Abs(up.X) >= Math.Abs(up.Z) ? 0 : Math.Abs(up.Y) >= Math.Abs(up.Z) ? 1 : 2;
            d.UpSign = Comp(up, d.UpAxis) >= 0 ? 1 : -1;
            if (d.UpAxis == 1) { d.H1 = 0; d.H2 = 2; } else if (d.UpAxis == 2) { d.H1 = 0; d.H2 = 1; } else { d.H1 = 1; d.H2 = 2; }
            var upv = Axis(d.UpAxis, d.UpSign);

            // ---- 包围盒：几何 ∪ 桨盘 ----
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            var t = s.Triangles ?? new float[0];
            for (int i = 0; i + 2 < t.Length; i += 3)
                for (int a = 0; a < 3; a++) { lo[a] = Math.Min(lo[a], t[i + a]); hi[a] = Math.Max(hi[a], t[i + a]); }
            var disks = s.Disks ?? new List<CfdDisk>();
            foreach (var k in disks)
            {
                var ax = k.Axis.Normalized();
                for (int a = 0; a < 3; a++)
                {
                    double na = Comp(ax, a), r = k.Radius * Math.Sqrt(Math.Max(0, 1 - na * na)) + 0.02 * k.Radius;
                    lo[a] = Math.Min(lo[a], Comp(k.Center, a) - r); hi[a] = Math.Max(hi[a], Comp(k.Center, a) + r);
                }
            }
            if (lo[0] > hi[0]) throw new InvalidOperationException("没有几何也没有桨盘，没法确定计算域");
            d.GeoMin = new Vec3(lo[0], lo[1], lo[2]); d.GeoMax = new Vec3(hi[0], hi[1], hi[2]);
            double span = Math.Max(hi[d.H1] - lo[d.H1], hi[d.H2] - lo[d.H2]);
            span = Math.Max(span, 0.5 * (hi[d.UpAxis] - lo[d.UpAxis]));
            span = Math.Max(span, 1e-3);
            d.Span = span;

            // ---- 尾流速度和方向 ----
            double vMax = 0, tSum = 0, aSum = 0;
            foreach (var k in disks)
            {
                if (k.Thrust <= 0 || k.Radius <= 0) continue;
                vMax = Math.Max(vMax, 2 * IdealInflow(k, s.Density));
                tSum += k.Thrust; aSum += DiskArea(k);
            }
            d.VFarMax = vMax;
            d.VFarMean = aSum > 0 ? 2 * Math.Sqrt(tSum / (2 * s.Density * aSum)) : 0;
            var wake = upv * -d.VFarMax + s.Wind;
            d.WakeDir = wake.Length > 1e-9 ? wake.Normalized() : -upv;

            // ---- 各个面的延伸 ----
            double padSide = s.PadSide > 0 ? s.PadSide : 1.0 * span;
            double padUp = s.PadUp > 0 ? s.PadUp : 1.0 * span;
            double padDown = s.PadDown > 0 ? s.PadDown : 2.5 * span;
            for (int a = 0; a < 3; a++)
                for (int side = 0; side < 2; side++)
                {
                    int sg = side == 0 ? -1 : 1;
                    var nOut = Axis(a, sg);
                    double pad;
                    if (a == d.UpAxis)
                    {
                        bool top = sg == d.UpSign;
                        if (top) pad = padUp;
                        else if (s.Ground) pad = Math.Max(0, s.GroundDistance);
                        else pad = s.PadDown > 0 ? s.PadDown : Math.Max(padSide, padDown * Math.Max(0, nOut.Dot(d.WakeDir)));
                    }
                    else pad = Math.Max(padSide, padDown * Math.Max(0, nOut.Dot(d.WakeDir)));
                    d.Pad[2 * a + side] = pad;
                    if (side == 0) lo[a] -= pad; else hi[a] += pad;
                }

            // ---- 格子尺寸 ----
            double vol = (hi[0] - lo[0]) * (hi[1] - lo[1]) * (hi[2] - lo[2]);
            long target = Math.Max(10_000, s.TargetCells);
            if (maxCells > 0 && target > maxCells && s.CellSize <= 0) { d.CappedFrom = target; target = maxCells; }
            double dx = s.CellSize > 0 ? s.CellSize : Math.Pow(vol / target, 1.0 / 3);
            // 自动网格时，边界层格子也算进总数：缩一点让总数接近目标
            if (s.CellSize <= 0)
                for (int it = 0; it < 4; it++)
                {
                    double cells = 1;
                    for (int a = 0; a < 3; a++) cells *= Math.Ceiling((hi[a] - lo[a]) / dx) + 2;
                    dx *= Math.Pow(cells / target, 1.0 / 3);
                }
            d.Dx = dx;
            int[] n = new int[3];
            double[] org = new double[3];
            for (int a = 0; a < 3; a++)
            {
                n[a] = Math.Max(3, (int)Math.Ceiling((hi[a] - lo[a]) / dx - 1e-9)) + 2;
                org[a] = lo[a] - dx;
            }
            // 有地面时网格对齐到地面：地面那一层边界格子和第一层流体格子的分界正好在地面上
            if (s.Ground && d.UpSign < 0) org[d.UpAxis] = hi[d.UpAxis] + dx - n[d.UpAxis] * dx;
            d.Nx = n[0]; d.Ny = n[1]; d.Nz = n[2];
            d.Origin = new Vec3(org[0], org[1], org[2]);
            if (d.N > 1_500_000_000L) throw new InvalidOperationException($"网格太大（{d.N / 1e6:0} M 格），请加大格子尺寸");

            // ---- 边界类型 ----
            var wind = s.Wind;
            double wl = wind.Length;
            for (int a = 0; a < 3; a++)
                for (int side = 0; side < 2; side++)
                {
                    int sg = side == 0 ? -1 : 1;
                    var nOut = Axis(a, sg);
                    byte ft;
                    if (a == d.UpAxis && sg != d.UpSign && s.Ground) ft = CellType.Wall;
                    else if (wl > 1e-9 && (-nOut).Dot(wind) > 0.3 * wl) ft = CellType.Inlet;    // 来流从这个面吹进来
                    else if (nOut.Dot(d.WakeDir) > 0.5) ft = CellType.Outflow;                    // 尾流从这个面出去
                    else if (wl > 1e-9 && nOut.Dot(wind) > 0.5 * wl) ft = CellType.Outflow;      // 来流从这个面出去
                    else ft = CellType.Far;
                    d.FaceType[2 * a + side] = ft;
                }
            return d;
        }

        /// <summary>边界格子的类型：一个格子同时在几个面上时，地面 > 出口 > 入口 > 远场。</summary>
        public byte BoundaryType(int i, int j, int k)
        {
            byte best = 255;
            int[] c = { i, j, k };
            for (int a = 0; a < 3; a++)
            {
                int side = c[a] == 0 ? 0 : c[a] == Dim(a) - 1 ? 1 : -1;
                if (side < 0) continue;
                byte ft = FaceType[2 * a + side];
                if (best == 255 || Rank(ft) > Rank(best)) best = ft;
            }
            return best;
        }
        static int Rank(byte t) => t == CellType.Wall ? 4 : t == CellType.Outflow ? 3 : t == CellType.Inlet ? 2 : 1;

        public static string FaceName(byte t) => t == CellType.Wall ? "地面" : t == CellType.Outflow ? "出口" : t == CellType.Inlet ? "入口" : "远场";
    }

    /// <summary>
    /// 把三角网格体素化成固体格子：
    ///  1) 每个零件单独按射线奇偶判断内外（沿 X、Y、Z 三个方向各做一次，取多数），互相重叠、接触的零件互不影响；
    ///  2) 再查所有穿过表面的格子连线（D3Q19 的 18 个方向）：两端都是流体的连线把离表面近的一端设成固体，
    ///     这样比格子还薄的板、涵道壁不会漏气，也不会被奇偶判断漏掉。
    /// </summary>
    internal static class CfdVoxelizer
    {
        // 射线位置的微小偏移（格子单位），避免射线正好穿过三角形的边或顶点
        const double EpsB = 1.372e-4, EpsC = 2.917e-4;

        public static void Run(CfdSetup s, CfdDomain d, byte[] solid, ushort[] part, out int thinCells, CancellationToken ct)
        {
            thinCells = 0;
            var tri = s.Triangles ?? new float[0];
            int nt = tri.Length / 9;
            if (nt == 0) return;
            // 三角形顶点换成格子坐标（整数 = 格子中心）
            var g = new double[nt * 9];
            double ox = d.Origin.X, oy = d.Origin.Y, oz = d.Origin.Z, idx = 1 / d.Dx;
            Parallel.For(0, nt, t =>
            {
                for (int v = 0; v < 3; v++)
                {
                    int o = 9 * t + 3 * v;
                    g[o] = (tri[o] - ox) * idx - 0.5; g[o + 1] = (tri[o + 1] - oy) * idx - 0.5; g[o + 2] = (tri[o + 2] - oz) * idx - 0.5;
                }
            });
            var tp = new int[nt];
            if (s.TriPart != null) for (int t = 0; t < nt && t < s.TriPart.Length; t++) tp[t] = Math.Max(0, Math.Min(65534, s.TriPart[t]));

            var votes = new byte[d.N];
            for (int a = 0; a < 3; a++) { ct.ThrowIfCancellationRequested(); CastAxis(g, tp, nt, d, a, votes, part); }
            Parallel.For(0, d.Nz, k =>
            {
                long b = (long)k * d.Nx * d.Ny, e = b + (long)d.Nx * d.Ny;
                for (long n = b; n < e; n++)
                {
                    int v = votes[n];
                    int cnt = (v & 1) + ((v >> 1) & 1) + ((v >> 2) & 1);
                    if (cnt >= 2) solid[n] = 1; else part[n] = 0;
                }
            });
            votes = null;
            ct.ThrowIfCancellationRequested();
            thinCells = CutLinks(g, tp, nt, d, solid, part);
        }

        /// <summary>沿 a 轴的射线（穿过每一列格子中心）和每个零件求交，按零件分别做奇偶填充。</summary>
        static void CastAxis(double[] g, int[] tp, int nt, CfdDomain d, int a, byte[] votes, ushort[] part)
        {
            int b = (a + 1) % 3, c = (a + 2) % 3;
            int na = d.Dim(a), nb = d.Dim(b), nc = d.Dim(c);
            long rays = (long)nb * nc;
            var count = new int[rays];
            // 第一遍数交点，第二遍填
            for (int pass = 0; pass < 2; pass++)
            {
                int[] start = null, cursor = null; float[] hitA = null; int[] hitP = null;
                if (pass == 1)
                {
                    start = new int[rays + 1];
                    long tot = 0;
                    for (long r = 0; r < rays; r++) { start[r] = (int)tot; tot += count[r]; }
                    if (tot > int.MaxValue - 1) throw new InvalidOperationException("模型太复杂，体素化的交点太多");
                    start[rays] = (int)tot;
                    cursor = new int[rays];
                    hitA = new float[tot]; hitP = new int[tot];
                }
                Parallel.For(0, nt, t =>
                {
                    int o = 9 * t;
                    double b0 = g[o + b], c0 = g[o + c], b1 = g[o + 3 + b], c1 = g[o + 3 + c], b2 = g[o + 6 + b], c2 = g[o + 6 + c];
                    double det = (b1 - b0) * (c2 - c0) - (c1 - c0) * (b2 - b0);
                    if (Math.Abs(det) < 1e-14) return;
                    int jb0 = Math.Max(0, (int)Math.Ceiling(Math.Min(b0, Math.Min(b1, b2)) - EpsB)), jb1 = Math.Min(nb - 1, (int)Math.Floor(Math.Max(b0, Math.Max(b1, b2)) - EpsB));
                    int jc0 = Math.Max(0, (int)Math.Ceiling(Math.Min(c0, Math.Min(c1, c2)) - EpsC)), jc1 = Math.Min(nc - 1, (int)Math.Floor(Math.Max(c0, Math.Max(c1, c2)) - EpsC));
                    double a0 = g[o + a], a1 = g[o + 3 + a], a2 = g[o + 6 + a];
                    for (int jc = jc0; jc <= jc1; jc++)
                        for (int jb = jb0; jb <= jb1; jb++)
                        {
                            double qb = jb + EpsB, qc = jc + EpsC;
                            double w0 = (b1 - qb) * (c2 - qc) - (c1 - qc) * (b2 - qb);
                            double w1 = (b2 - qb) * (c0 - qc) - (c2 - qc) * (b0 - qb);
                            double w2 = (b0 - qb) * (c1 - qc) - (c0 - qc) * (b1 - qb);
                            bool inside = det > 0 ? (w0 > 0 && w1 > 0 && w2 > 0) : (w0 < 0 && w1 < 0 && w2 < 0);
                            if (!inside) continue;
                            long r = jb + (long)nb * jc;
                            if (pass == 0) Interlocked.Increment(ref count[r]);
                            else
                            {
                                int slot = start[r] + Interlocked.Increment(ref cursor[r]) - 1;
                                hitA[slot] = (float)((w0 * a0 + w1 * a1 + w2 * a2) / det);
                                hitP[slot] = tp[t];
                            }
                        }
                });
                if (pass == 0) continue;
                // 每条射线：按 (零件, 位置) 排序，同一零件的交点两两配对填充；某个零件交点数为奇数（网格不封闭）就跳过它
                Parallel.For(0, nc, jc =>
                {
                    var keys = new List<(int p, float x)>();
                    for (int jb = 0; jb < nb; jb++)
                    {
                        long r = jb + (long)nb * jc;
                        int s0 = start[r], s1 = start[r + 1];
                        if (s1 - s0 < 2) continue;
                        keys.Clear();
                        for (int q = s0; q < s1; q++) keys.Add((hitP[q], hitA[q]));
                        keys.Sort((u, v) => u.p != v.p ? u.p.CompareTo(v.p) : u.x.CompareTo(v.x));
                        int i0 = 0;
                        while (i0 < keys.Count)
                        {
                            int i1 = i0;
                            while (i1 < keys.Count && keys[i1].p == keys[i0].p) i1++;
                            if (((i1 - i0) & 1) == 0)
                                for (int q = i0; q < i1; q += 2)
                                {
                                    int lo = Math.Max(0, (int)Math.Floor(keys[q].x) + 1), hi = Math.Min(na - 1, (int)Math.Ceiling(keys[q + 1].x) - 1);
                                    for (int ia = lo; ia <= hi; ia++)
                                    {
                                        long n = Index(d, a, ia, b, jb, c, jc);
                                        votes[n] |= (byte)(1 << a);
                                        if (part[n] == 0) part[n] = (ushort)(keys[q].p + 1);
                                    }
                                }
                            i0 = i1;
                        }
                    }
                });
            }
        }

        static long Index(CfdDomain d, int a, int ia, int b, int ib, int c, int ic)
        {
            int x = 0, y = 0, z = 0;
            if (a == 0) x = ia; else if (a == 1) y = ia; else z = ia;
            if (b == 0) x = ib; else if (b == 1) y = ib; else z = ib;
            if (c == 0) x = ic; else if (c == 1) y = ic; else z = ic;
            return x + (long)d.Nx * (y + (long)d.Ny * z);
        }

        // D3Q19 的 9 个「正向」连线方向（另外 9 个是它们的反向）
        static readonly int[,] Links = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 }, { 1, 1, 0 }, { 1, -1, 0 }, { 1, 0, 1 }, { 1, 0, -1 }, { 0, 1, 1 }, { 0, 1, -1 } };

        /// <summary>穿过三角形、两端都是流体的格子连线：把离三角形近的那一端设成固体。返回新增的固体格子数。</summary>
        static int CutLinks(double[] g, int[] tp, int nt, CfdDomain d, byte[] solid, ushort[] part)
        {
            var all = new List<long>();
            var lk = new object();
            int[] dims = { d.Nx, d.Ny, d.Nz };
            Parallel.For(0, nt, () => new List<long>(), (t, st, loc) =>
            {
                int o = 9 * t;
                double p0x = g[o], p0y = g[o + 1], p0z = g[o + 2];
                double e1x = g[o + 3] - p0x, e1y = g[o + 4] - p0y, e1z = g[o + 5] - p0z;
                double e2x = g[o + 6] - p0x, e2y = g[o + 7] - p0y, e2z = g[o + 8] - p0z;
                double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                double an = Math.Max(Math.Abs(nx), Math.Max(Math.Abs(ny), Math.Abs(nz)));
                if (an < 1e-14) return loc;
                int m = Math.Abs(nx) == an ? 0 : Math.Abs(ny) == an ? 1 : 2;
                int u = (m + 1) % 3, w = (m + 2) % 3;
                double[] nv = { nx, ny, nz }, p0 = { p0x, p0y, p0z };
                double[] vu = { g[o + u], g[o + 3 + u], g[o + 6 + u] }, vw = { g[o + w], g[o + 3 + w], g[o + 6 + w] };
                int iu0 = Math.Max(0, (int)Math.Floor(vu.Min()) - 1), iu1 = Math.Min(dims[u] - 1, (int)Math.Ceiling(vu.Max()) + 1);
                int iw0 = Math.Max(0, (int)Math.Floor(vw.Min()) - 1), iw1 = Math.Min(dims[w] - 1, (int)Math.Ceiling(vw.Max()) + 1);
                // 投影到 (u, w) 平面做点在三角形内的判断
                double qu0 = vu[0], qw0 = vw[0], qu1 = vu[1], qw1 = vw[1], qu2 = vu[2], qw2 = vw[2];
                double det = (qu1 - qu0) * (qw2 - qw0) - (qw1 - qw0) * (qu2 - qu0);
                if (Math.Abs(det) < 1e-14) return loc;
                const double tol = 1e-9;
                var cpos = new int[3];
                for (int iw = iw0; iw <= iw1; iw++)
                    for (int iu = iu0; iu <= iu1; iu++)
                    {
                        double pm = p0[m] - (nv[u] * (iu - p0[u]) + nv[w] * (iw - p0[w])) / nv[m];
                        int im0 = Math.Max(0, (int)Math.Ceiling(pm - 3)), im1 = Math.Min(dims[m] - 1, (int)Math.Floor(pm + 3));
                        for (int im = im0; im <= im1; im++)
                        {
                            cpos[m] = im; cpos[u] = iu; cpos[w] = iw;
                            double sa = nx * (cpos[0] - p0x) + ny * (cpos[1] - p0y) + nz * (cpos[2] - p0z);
                            for (int l = 0; l < 9; l++)
                            {
                                int dxl = Links[l, 0], dyl = Links[l, 1], dzl = Links[l, 2];
                                int bx = cpos[0] + dxl, by = cpos[1] + dyl, bz = cpos[2] + dzl;
                                if (bx < 0 || by < 0 || bz < 0 || bx >= d.Nx || by >= d.Ny || bz >= d.Nz) continue;
                                double sb = sa + nx * dxl + ny * dyl + nz * dzl;
                                if ((sa > 0 && sb > 0) || (sa < 0 && sb < 0) || sa == sb) continue;
                                double tt = sa / (sa - sb);
                                // 交点投影到 (u, w)，看是否在三角形里
                                double[] dl = { dxl, dyl, dzl };
                                double pu = cpos[u] + tt * dl[u], pw = cpos[w] + tt * dl[w];
                                double w0 = ((qu1 - pu) * (qw2 - pw) - (qw1 - pw) * (qu2 - pu)) / det;
                                double w1 = ((qu2 - pu) * (qw0 - pw) - (qw2 - pw) * (qu0 - pu)) / det;
                                double w2 = 1 - w0 - w1;
                                if (w0 < -tol || w1 < -tol || w2 < -tol) continue;
                                long na_ = cpos[0] + (long)d.Nx * (cpos[1] + (long)d.Ny * cpos[2]);
                                long nb_ = bx + (long)d.Nx * (by + (long)d.Ny * bz);
                                if (solid[na_] != 0 || solid[nb_] != 0) continue;
                                long cell = tt < 0.5 ? na_ : nb_;
                                loc.Add((cell << 16) | (long)(tp[t] & 0xFFFF));
                            }
                        }
                    }
                return loc;
            }, loc => { lock (lk) all.AddRange(loc); });
            int added = 0;
            foreach (var e in all)
            {
                long cell = e >> 16;
                if (solid[cell] != 0) continue;
                solid[cell] = 1;
                part[cell] = (ushort)((e & 0xFFFF) + 1);
                added++;
            }
            return added;
        }
    }

    /// <summary>一个作动盘落在网格上的格子和每格的体积力。</summary>
    internal sealed class DiskCells
    {
        public CfdDisk Disk;
        public List<long> Cells = new List<long>();
        public List<double> Geo = new List<double>();      // 格子里属于盘的体积比例（统计入流速度用）
        public List<Vec3> Force = new List<Vec3>();        // 加在空气上的力（N）
        public double LostFraction;                        // 落在固体里、被丢掉的推力比例（已重新归一化）
        public bool Empty;

        /// <summary>
        /// 盘厚 2 个格子，每个格子 4×4×4 个采样点算体积比例和径向载荷，再归一化：所有流体格子上的轴向力之和正好等于推力、
        /// 切向力对盘心的力矩之和正好等于反扭矩。
        /// </summary>
        public static DiskCells Rasterize(CfdDisk k, CfdDomain d, byte[] solid)
        {
            var res = new DiskCells { Disk = k };
            var a = k.Axis.Normalized();
            double R = k.Radius, Rh = Math.Max(0, Math.Min(k.HubRadius, 0.95 * R)), th = 2 * d.Dx, dx = d.Dx;
            double reach = R + th + dx;
            int i0 = Math.Max(1, (int)Math.Floor((k.Center.X - reach - d.Origin.X) / dx)), i1 = Math.Min(d.Nx - 2, (int)Math.Ceiling((k.Center.X + reach - d.Origin.X) / dx));
            int j0 = Math.Max(1, (int)Math.Floor((k.Center.Y - reach - d.Origin.Y) / dx)), j1 = Math.Min(d.Ny - 2, (int)Math.Ceiling((k.Center.Y + reach - d.Origin.Y) / dx));
            int k0 = Math.Max(1, (int)Math.Floor((k.Center.Z - reach - d.Origin.Z) / dx)), k1 = Math.Min(d.Nz - 2, (int)Math.Ceiling((k.Center.Z + reach - d.Origin.Z) / dx));
            const int S = 4;
            var cells = new List<(long n, Vec3 c, double geo, double wt, Vec3 tan, bool solid)>();
            var lk = new object();
            Parallel.For(k0, k1 + 1, kk =>
            {
                var loc = new List<(long, Vec3, double, double, Vec3, bool)>();
                for (int jj = j0; jj <= j1; jj++)
                    for (int ii = i0; ii <= i1; ii++)
                    {
                        var c = d.Center(ii, jj, kk);
                        var rel = c - k.Center;
                        double ax = rel.Dot(a);
                        if (Math.Abs(ax) > th / 2 + 0.87 * dx) continue;
                        double rc = (rel - a * ax).Length;
                        if (rc > R + 0.87 * dx || rc < Rh - 0.87 * dx) continue;
                        double geo = 0, wt = 0; var tan = Vec3.Zero;
                        for (int sz = 0; sz < S; sz++)
                            for (int sy = 0; sy < S; sy++)
                                for (int sx = 0; sx < S; sx++)
                                {
                                    var p = rel + new Vec3(((sx + 0.5) / S - 0.5) * dx, ((sy + 0.5) / S - 0.5) * dx, ((sz + 0.5) / S - 0.5) * dx);
                                    double pa = p.Dot(a);
                                    if (Math.Abs(pa) > th / 2) continue;
                                    var rv = p - a * pa;
                                    double r = rv.Length;
                                    if (r > R || r < Rh) continue;
                                    geo += 1;
                                    double w = 1;
                                    if (k.Loading == CfdLoading.Prop)
                                    {
                                        double rs = (r - Rh) / Math.Max(1e-12, R - Rh);
                                        w = rs * Math.Sqrt(Math.Max(0, 1 - rs));
                                    }
                                    wt += w;
                                    if (r > 1e-12) tan += a.Cross(rv / r) * r;   // 切向，大小 ∝ r
                                }
                        if (geo == 0) continue;
                        long n = ii + (long)d.Nx * (jj + (long)d.Ny * kk);
                        double inv = 1.0 / (S * S * S);
                        loc.Add((n, c, geo * inv, wt * inv, tan * inv, solid[n] != 0));
                    }
                lock (lk) cells.AddRange(loc);
            });
            cells.Sort((x, y) => x.n.CompareTo(y.n));
            double wFluid = 0, wAll = 0, q = 0;
            foreach (var e in cells)
            {
                wAll += e.wt;
                if (e.solid) continue;
                wFluid += e.wt;
                q += (e.c - k.Center).Cross(e.tan).Dot(a);
            }
            res.LostFraction = wAll > 0 ? 1 - wFluid / wAll : 1;
            if (wFluid <= 0) { res.Empty = true; return res; }
            double sT = k.Thrust / wFluid;
            double sQ = Math.Abs(k.Torque) > 0 && Math.Abs(q) > 1e-30 ? Math.Abs(k.Torque) / q * Math.Sign(k.Torque) : 0;
            foreach (var e in cells)
            {
                if (e.solid) continue;
                res.Cells.Add(e.n);
                res.Geo.Add(e.geo);
                res.Force.Add(a * (-sT * e.wt) + e.tan * sQ);
            }
            return res;
        }
    }
}
