using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SwKit.Core.Fea
{
    internal interface IPrecond { void Apply(double[] r, double[] z); }

    /// <summary>
    /// 光滑聚集代数多重网格（Smoothed Aggregation AMG，Vaněk–Mandel–Brezina 1996），用作共轭梯度的预条件器。
    /// 三维弹性问题的标准做法（Trilinos ML / MueLu、PETSc GAMG 都是这一类）：
    ///  · 近零空间用 6 个刚体模态（3 个平移 + 3 个转动），每个聚集块做 QR 得到试探延拓 T；
    ///  · 延拓 P = (I − ω D⁻¹A) T，粗网格 A_c = Pᵀ A P；
    ///  · 光滑器用 Chebyshev 多项式（只需要矩阵乘向量，可以并行），最粗层稠密 Cholesky；
    ///  · V 循环前后光滑相同，整体对称，可以给共轭梯度用。
    /// 迭代次数基本不随网格加密而增加（不完全 Cholesky 会越来越多），网格越细优势越大。
    /// 拓扑优化每轮矩阵数值变了但结构不变：Rebuild 复用聚集和 T，只重算 P 和粗网格矩阵。
    /// </summary>
    internal sealed class Amg : IPrecond
    {
        sealed class Level
        {
            public FeSolver.Csr A; public int N;
            public double[] InvDiag; public double LMax;
            public FeSolver.Csr T, P, R, NextA; public int NCoarse;     // 到下一层（更粗）的延拓、下一层的矩阵
            public double[] b, x, r, d, t;
        }

        readonly List<Level> _lv = new List<Level>();
        double[] _chol; int _nc;           // 最粗层的稠密 Cholesky
        static readonly int Degree = int.TryParse(Environment.GetEnvironmentVariable("SWKIT_AMG_DEGREE"), out var dg) ? dg : 3;   // Chebyshev 次数
        const int CoarseMax = 1200;        // 最粗层自由度上限（稠密分解）
        const double Theta = 0.04;         // 强连接阈值
        public string Info { get; private set; }
        public double SetupSeconds { get; private set; }

        /// <summary>
        /// A：已施加约束的刚度矩阵（固定自由度所在行列只有对角 1），自由度按节点排列（3i + c）。
        /// coords：每个节点的坐标（3N）；isFixed：每个自由度是否固定。
        /// </summary>
        public static Amg Build(FeSolver.Csr A, double[] coords, bool[] isFixed)
        {
            var sw = Stopwatch.StartNew();
            var amg = new Amg();
            int nn = A.N / 3;
            var nodeStart = Enumerable.Range(0, nn + 1).Select(i => 3 * i).ToArray();
            var nodeFixed = new bool[nn];
            for (int i = 0; i < nn; i++) nodeFixed[i] = isFixed[3 * i] && isFixed[3 * i + 1] && isFixed[3 * i + 2];
            double[] B = null;   // 当前层的近零空间（N×6），第 0 层在 T 里按坐标现算
            var cur = A;
            for (int level = 0; level < 10; level++)
            {
                var L = new Level { A = cur, N = cur.N };
                Smoother(L);
                amg._lv.Add(L);
                if (cur.N <= CoarseMax) break;
                var agg = Aggregate(cur, nodeStart, nodeFixed, out int nAgg);
                if (nAgg == 0) break;
                var T = Tentative(cur.N, nodeStart, agg, nAgg, level == 0 ? coords : null, B, out var Bc, out var coarseStart);
                int nc = coarseStart[nAgg];
                if (nc >= 0.85 * cur.N || nc == 0) { break; }
                L.T = T; L.NCoarse = nc;
                Galerkin(L);
                cur = L.NextA;
                nodeStart = coarseStart; nodeFixed = new bool[nAgg]; B = Bc;
            }
            amg.CoarseSetup();
            amg.SetupSeconds = sw.Elapsed.TotalSeconds;
            amg.Info = $"{amg._lv.Count} 层：" + string.Join(" → ", amg._lv.Select(l => l.N.ToString("N0")));
            return amg;
        }

        /// <summary>同样的稀疏结构、新的数值（拓扑优化每轮）：复用聚集和 T，重算光滑器、P 和粗网格。</summary>
        public void Rebuild(FeSolver.Csr A)
        {
            var sw = Stopwatch.StartNew();
            var cur = A;
            for (int l = 0; l < _lv.Count; l++)
            {
                var L = _lv[l];
                L.A = cur; L.N = cur.N;
                Smoother(L);
                if (L.T == null) break;
                Galerkin(L);
                cur = L.NextA;
            }
            CoarseSetup();
            SetupSeconds = sw.Elapsed.TotalSeconds;
        }

        // ================= 设置 =================

        static void Smoother(Level L)
        {
            var A = L.A; int n = A.N;
            L.InvDiag = new double[n];
            for (int r = 0; r < n; r++)
                for (int p = A.RowPtr[r]; p < A.RowPtr[r + 1]; p++)
                    if (A.Cols[p] == r) { L.InvDiag[r] = A.Vals[p] > 0 ? 1 / A.Vals[p] : 0; break; }
            // 幂迭代估计 D⁻¹A 的最大特征值
            var v = new double[n]; var w = new double[n];
            var rnd = new Random(12345);
            for (int i = 0; i < n; i++) v[i] = rnd.NextDouble() - 0.5;
            double lam = 1;
            for (int it = 0; it < 12; it++)
            {
                double nv = Math.Sqrt(Dot(v, v));
                if (nv == 0) break;
                for (int i = 0; i < n; i++) v[i] /= nv;
                A.Mul(v, w);
                for (int i = 0; i < n; i++) w[i] *= L.InvDiag[i];
                lam = Math.Sqrt(Dot(w, w));
                var t = v; v = w; w = t;
            }
            L.LMax = Math.Max(lam, 1e-12);
            L.b = new double[n]; L.x = new double[n]; L.r = new double[n]; L.d = new double[n]; L.t = new double[n];
        }

        /// <summary>按节点聚集（三遍法）。返回每个节点所属的聚集编号（固定节点为 −1）。</summary>
        static int[] Aggregate(FeSolver.Csr A, int[] nodeStart, bool[] nodeFixed, out int nAgg)
        {
            int nn = nodeStart.Length - 1;
            var nodeOf = new int[A.N];
            for (int i = 0; i < nn; i++) for (int d = nodeStart[i]; d < nodeStart[i + 1]; d++) nodeOf[d] = i;
            // 节点之间的块范数（Frobenius）
            var diagNorm = new double[nn];
            var adjPtr = new int[nn + 1];
            var adjList = new List<int>[nn]; var strList = new List<double>[nn];
            Parallel.For(0, nn, () => (new int[nn].Select(_ => -1).ToArray(), new double[nn]), (i, st, loc) =>
            {
                var (mark, acc) = loc;
                var nbrs = new List<int>(64);
                for (int r = nodeStart[i]; r < nodeStart[i + 1]; r++)
                    for (int p = A.RowPtr[r]; p < A.RowPtr[r + 1]; p++)
                    {
                        int j = nodeOf[A.Cols[p]];
                        if (mark[j] != i) { mark[j] = i; acc[j] = 0; nbrs.Add(j); }
                        acc[j] += A.Vals[p] * A.Vals[p];
                    }
                var s = new List<double>(nbrs.Count);
                foreach (var j in nbrs) s.Add(Math.Sqrt(acc[j]));
                adjList[i] = nbrs; strList[i] = s;
                int self = nbrs.IndexOf(i);
                diagNorm[i] = self >= 0 ? s[self] : 0;
                return loc;
            }, loc => { });
            // 强连接
            var strong = new List<int>[nn];
            Parallel.For(0, nn, i =>
            {
                var l = new List<int>();
                if (!nodeFixed[i])
                    for (int k = 0; k < adjList[i].Count; k++)
                    {
                        int j = adjList[i][k];
                        if (j == i || nodeFixed[j]) continue;
                        if (strList[i][k] >= Theta * Math.Sqrt(diagNorm[i] * diagNorm[j])) l.Add(j);
                    }
                strong[i] = l;
            });
            var agg = new int[nn];
            for (int i = 0; i < nn; i++) agg[i] = nodeFixed[i] || nodeStart[i + 1] == nodeStart[i] ? -2 : -1;
            nAgg = 0;
            // 第一遍：自己和所有强邻居都没被聚集 → 组成新的聚集块
            for (int i = 0; i < nn; i++)
            {
                if (agg[i] != -1) continue;
                bool free = true;
                foreach (var j in strong[i]) if (agg[j] != -1) { free = false; break; }
                if (!free || strong[i].Count == 0) continue;
                agg[i] = nAgg;
                foreach (var j in strong[i]) agg[j] = nAgg;
                nAgg++;
            }
            // 第二遍：剩下的节点并入强邻居所在的聚集块
            var first = (int[])agg.Clone();
            for (int i = 0; i < nn; i++)
            {
                if (agg[i] != -1) continue;
                double best = -1; int to = -1;
                for (int k = 0; k < adjList[i].Count; k++)
                {
                    int j = adjList[i][k];
                    if (j == i || first[j] < 0) continue;
                    if (strList[i][k] > best) { best = strList[i][k]; to = first[j]; }
                }
                if (to >= 0) agg[i] = to;
            }
            // 第三遍：还剩下的自己组成聚集块
            for (int i = 0; i < nn; i++)
            {
                if (agg[i] != -1) continue;
                agg[i] = nAgg;
                foreach (var j in strong[i]) if (agg[j] == -1) agg[j] = nAgg;
                nAgg++;
            }
            for (int i = 0; i < nn; i++) if (agg[i] < 0) agg[i] = -1;
            return agg;
        }

        /// <summary>
        /// 试探延拓 T：每个聚集块里把近零空间（刚体模态）做 QR，Q 放进 T，R 成为粗网格的近零空间。
        /// 第 0 层的刚体模态按节点坐标（相对聚集块中心）现算。
        /// </summary>
        static FeSolver.Csr Tentative(int n, int[] nodeStart, int[] agg, int nAgg, double[] coords, double[] B, out double[] Bc, out int[] coarseStart)
        {
            var members = new List<int>[nAgg];
            for (int a = 0; a < nAgg; a++) members[a] = new List<int>();
            for (int i = 0; i < agg.Length; i++) if (agg[i] >= 0) members[agg[i]].Add(i);
            var Q = new double[nAgg][]; var Rm = new double[nAgg][]; var kept = new int[nAgg]; var rows = new int[nAgg][];
            Parallel.For(0, nAgg, a =>
            {
                var dofs = new List<int>();
                foreach (var i in members[a]) for (int d = nodeStart[i]; d < nodeStart[i + 1]; d++) dofs.Add(d);
                int m = dofs.Count;
                var M = new double[m * 6];   // 行主序 m×6
                if (coords != null)
                {
                    double cx = 0, cy = 0, cz = 0;
                    foreach (var i in members[a]) { cx += coords[3 * i]; cy += coords[3 * i + 1]; cz += coords[3 * i + 2]; }
                    cx /= members[a].Count; cy /= members[a].Count; cz /= members[a].Count;
                    double h = 1e-30;
                    foreach (var i in members[a]) h = Math.Max(h, Math.Max(Math.Abs(coords[3 * i] - cx), Math.Max(Math.Abs(coords[3 * i + 1] - cy), Math.Abs(coords[3 * i + 2] - cz))));
                    int row = 0;
                    foreach (var i in members[a])
                    {
                        double x = (coords[3 * i] - cx) / h, y = (coords[3 * i + 1] - cy) / h, z = (coords[3 * i + 2] - cz) / h;
                        // 平移 x、y、z；绕 x、y、z 转动
                        double[][] rr = { new[] { 1.0, 0, 0, 0, z, -y }, new[] { 0.0, 1, 0, -z, 0, x }, new[] { 0.0, 0, 1, y, -x, 0 } };
                        for (int c = 0; c < 3; c++, row++) for (int k = 0; k < 6; k++) M[row * 6 + k] = rr[c][k];
                    }
                }
                else
                    for (int r = 0; r < m; r++) for (int k = 0; k < 6; k++) M[r * 6 + k] = B[dofs[r] * 6 + k];
                // 修正 Gram-Schmidt（两遍），丢掉线性相关的列
                var q = new List<double[]>(); var R = new double[6 * 6];
                for (int k = 0; k < 6; k++)
                {
                    var v = new double[m];
                    for (int r = 0; r < m; r++) v[r] = M[r * 6 + k];
                    double orig = Math.Sqrt(v.Sum(t => t * t));
                    for (int pass = 0; pass < 2; pass++)
                        for (int j = 0; j < q.Count; j++)
                        {
                            double s = 0; for (int r = 0; r < m; r++) s += q[j][r] * v[r];
                            R[j * 6 + k] += s;
                            for (int r = 0; r < m; r++) v[r] -= s * q[j][r];
                        }
                    double nrm = Math.Sqrt(v.Sum(t => t * t));
                    if (orig > 0 && nrm > 1e-8 * orig && q.Count < m)
                    {
                        for (int r = 0; r < m; r++) v[r] /= nrm;
                        R[q.Count * 6 + k] = nrm;
                        q.Add(v);
                    }
                }
                kept[a] = q.Count;
                var Qa = new double[m * q.Count];
                for (int r = 0; r < m; r++) for (int j = 0; j < q.Count; j++) Qa[r * q.Count + j] = q[j][r];
                Q[a] = Qa; Rm[a] = R; rows[a] = dofs.ToArray();
            });
            coarseStart = new int[nAgg + 1];
            for (int a = 0; a < nAgg; a++) coarseStart[a + 1] = coarseStart[a] + kept[a];
            int nc = coarseStart[nAgg];
            Bc = new double[nc * 6];
            for (int a = 0; a < nAgg; a++)
                for (int j = 0; j < kept[a]; j++) for (int k = 0; k < 6; k++) Bc[(coarseStart[a] + j) * 6 + k] = Rm[a][j * 6 + k];
            // CSR：每个细自由度一行，列是所在聚集块的粗自由度
            var rowLen = new int[n]; var rowAgg = new int[n]; var rowLocal = new int[n];
            for (int i = 0; i < n; i++) rowAgg[i] = -1;
            for (int a = 0; a < nAgg; a++) for (int r = 0; r < rows[a].Length; r++) { rowAgg[rows[a][r]] = a; rowLocal[rows[a][r]] = r; rowLen[rows[a][r]] = kept[a]; }
            var T = new FeSolver.Csr { N = n, RowPtr = new int[n + 1] };
            for (int i = 0; i < n; i++) T.RowPtr[i + 1] = T.RowPtr[i] + rowLen[i];
            T.Cols = new int[T.RowPtr[n]]; T.Vals = new double[T.RowPtr[n]];
            for (int i = 0; i < n; i++)
            {
                int a = rowAgg[i]; if (a < 0) continue;
                int p = T.RowPtr[i], k = kept[a];
                for (int j = 0; j < k; j++) { T.Cols[p + j] = coarseStart[a] + j; T.Vals[p + j] = Q[a][rowLocal[i] * k + j]; }
            }
            return T;
        }

        /// <summary>P = (I − ω D⁻¹A) T，Pᵀ，A_c = Pᵀ A P。</summary>
        static void Galerkin(Level L)
        {
            double omega = 4.0 / (3.0 * L.LMax);
            var AT = SpGemm(L.A, L.T, L.NCoarse);
            // P = T − ω D⁻¹ AT（T 的非零位置包含在 AT 里，按行合并）
            var P = new FeSolver.Csr { N = L.N, RowPtr = AT.RowPtr, Cols = AT.Cols, Vals = new double[AT.Vals.Length] };
            Parallel.For(0, L.N, i =>
            {
                double s = -omega * L.InvDiag[i];
                int q = L.T.RowPtr[i], qe = L.T.RowPtr[i + 1];
                for (int p = AT.RowPtr[i]; p < AT.RowPtr[i + 1]; p++)
                {
                    double v = s * AT.Vals[p];
                    while (q < qe && L.T.Cols[q] < AT.Cols[p]) q++;
                    if (q < qe && L.T.Cols[q] == AT.Cols[p]) v += L.T.Vals[q];
                    P.Vals[p] = v;
                }
            });
            L.P = P;
            L.R = Transpose(P, L.NCoarse);
            AT = null;
            var AP = SpGemm(L.A, P, L.NCoarse);
            L.NextA = SpGemm(L.R, AP, L.NCoarse);
            AP = null;
            GC.Collect();
        }

        void CoarseSetup()
        {
            var L = _lv[_lv.Count - 1];
            int n = L.N; _nc = n;
            _chol = null;
            if (n > 3000) return;   // 太大：用 Chebyshev 多迭代几次代替
            var M = new double[n * n];
            for (int r = 0; r < n; r++) for (int p = L.A.RowPtr[r]; p < L.A.RowPtr[r + 1]; p++) M[r * n + L.A.Cols[p]] = L.A.Vals[p];
            double maxDiag = 0; for (int i = 0; i < n; i++) maxDiag = Math.Max(maxDiag, M[i * n + i]);
            for (int j = 0; j < n; j++)
            {
                double d = M[j * n + j];
                for (int k = 0; k < j; k++) d -= M[j * n + k] * M[j * n + k];
                if (d <= 1e-12 * maxDiag) d = Math.Max(1e-12 * maxDiag, 1e-300);
                double ljj = Math.Sqrt(d);
                M[j * n + j] = ljj;
                Parallel.For(j + 1, n, i =>
                {
                    double s = M[i * n + j];
                    int bi = i * n, bj = j * n;
                    for (int k = 0; k < j; k++) s -= M[bi + k] * M[bj + k];
                    M[bi + j] = s / ljj;
                });
            }
            _chol = M;
        }

        // ================= 求解 =================

        public void Apply(double[] r, double[] z) => VCycle(0, r, z);

        void VCycle(int l, double[] b, double[] x)
        {
            var L = _lv[l];
            if (l == _lv.Count - 1) { CoarseSolve(L, b, x); return; }
            Array.Clear(x, 0, L.N);
            Chebyshev(L, b, x, true, Degree);
            L.A.Mul(x, L.t);
            Par(L.N, (s, e) => { for (int i = s; i < e; i++) L.r[i] = b[i] - L.t[i]; });
            var C = _lv[l + 1];
            L.R.Mul(L.r, C.b);
            VCycle(l + 1, C.b, C.x);
            L.P.Mul(C.x, L.t);
            Par(L.N, (s, e) => { for (int i = s; i < e; i++) x[i] += L.t[i]; });
            Chebyshev(L, b, x, false, Degree);
        }

        static int Blocks(int n) => (n + 8191) / 8192;

        void CoarseSolve(Level L, double[] b, double[] x)
        {
            int n = L.N;
            if (_chol == null) { Array.Clear(x, 0, n); Chebyshev(L, b, x, true, 20); return; }
            var M = _chol;
            for (int i = 0; i < n; i++) { double s = b[i]; for (int k = 0; k < i; k++) s -= M[i * n + k] * x[k]; x[i] = s / M[i * n + i]; }
            for (int i = n - 1; i >= 0; i--) { double s = x[i]; for (int k = i + 1; k < n; k++) s -= M[k * n + i] * x[k]; x[i] = s / M[i * n + i]; }
        }

        /// <summary>Chebyshev 光滑（预条件 D⁻¹，特征值区间 [λmax/30, 1.1 λmax]）。zeroInit：x 从 0 开始（省一次矩阵乘法）。</summary>
        static void Chebyshev(Level L, double[] b, double[] x, bool zeroInit, int degree)
        {
            int n = L.N;
            double upper = 1.1 * L.LMax, lower = upper / 30;
            double theta = (upper + lower) / 2, delta = (upper - lower) / 2, sigma = theta / delta, rho = 1 / sigma;
            var r = L.r; var d = L.d; var t = L.t; var D = L.InvDiag;
            if (zeroInit) Array.Copy(b, r, n);
            else { L.A.Mul(x, t); Par(n, (s, e) => { for (int i = s; i < e; i++) r[i] = b[i] - t[i]; }); }
            double it0 = 1 / theta;
            Par(n, (s, e) => { for (int i = s; i < e; i++) d[i] = D[i] * r[i] * it0; });
            for (int k = 1; k <= degree; k++)
            {
                Par(n, (s, e) => { for (int i = s; i < e; i++) x[i] += d[i]; });
                if (k == degree) break;
                L.A.Mul(d, t);
                double rhoNew = 1 / (2 * sigma - rho);
                double c1 = rhoNew * rho, c2 = 2 * rhoNew / delta;
                Par(n, (s, e) => { for (int i = s; i < e; i++) { r[i] -= t[i]; d[i] = c1 * d[i] + c2 * D[i] * r[i]; } });
                rho = rhoNew;
            }
        }

        /// <summary>按块并行：f(起点, 终点)。小向量直接串行（并行的调度开销比计算还大）。</summary>
        static void Par(int n, Action<int, int> f)
        {
            if (n < 20000) { f(0, n); return; }
            Parallel.For(0, Blocks(n), blk => f(blk * 8192, Math.Min(n, blk * 8192 + 8192)));
        }

        static double Dot(double[] a, double[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

        // ================= 稀疏矩阵运算 =================

        /// <summary>C = A·B（Gustavson，按行并行，两遍：先数个数再填值）。bCols：B 的列数。</summary>
        static FeSolver.Csr SpGemm(FeSolver.Csr A, FeSolver.Csr B, int bCols)
        {
            int n = A.N;
            var cnt = new int[n];
            Parallel.For(0, n, () => Fill(new int[bCols], -1), (i, st, mark) =>
            {
                int c = 0;
                for (int p = A.RowPtr[i]; p < A.RowPtr[i + 1]; p++)
                {
                    int k = A.Cols[p];
                    for (int q = B.RowPtr[k]; q < B.RowPtr[k + 1]; q++) { int j = B.Cols[q]; if (mark[j] != i) { mark[j] = i; c++; } }
                }
                cnt[i] = c;
                return mark;
            }, m => { });
            var C = new FeSolver.Csr { N = n, RowPtr = new int[n + 1] };
            for (int i = 0; i < n; i++) C.RowPtr[i + 1] = C.RowPtr[i] + cnt[i];
            C.Cols = new int[C.RowPtr[n]]; C.Vals = new double[C.RowPtr[n]];
            Parallel.For(0, n, () => (Fill(new int[bCols], -1), new double[bCols]), (i, st, loc) =>
            {
                var (mark, acc) = loc;
                int start = C.RowPtr[i], len = 0;
                for (int p = A.RowPtr[i]; p < A.RowPtr[i + 1]; p++)
                {
                    int k = A.Cols[p]; double av = A.Vals[p];
                    for (int q = B.RowPtr[k]; q < B.RowPtr[k + 1]; q++)
                    {
                        int j = B.Cols[q];
                        if (mark[j] != i) { mark[j] = i; acc[j] = av * B.Vals[q]; C.Cols[start + len++] = j; }
                        else acc[j] += av * B.Vals[q];
                    }
                }
                Array.Sort(C.Cols, start, len);
                for (int p = start; p < start + len; p++) C.Vals[p] = acc[C.Cols[p]];
                return loc;
            }, loc => { });
            return C;
        }

        static int[] Fill(int[] a, int v) { for (int i = 0; i < a.Length; i++) a[i] = v; return a; }

        static FeSolver.Csr Transpose(FeSolver.Csr A, int nCols)
        {
            var T = new FeSolver.Csr { N = nCols, RowPtr = new int[nCols + 1], Cols = new int[A.Cols.Length], Vals = new double[A.Vals.Length] };
            foreach (var c in A.Cols) T.RowPtr[c + 1]++;
            for (int i = 0; i < nCols; i++) T.RowPtr[i + 1] += T.RowPtr[i];
            var pos = (int[])T.RowPtr.Clone();
            for (int r = 0; r < A.N; r++)
                for (int p = A.RowPtr[r]; p < A.RowPtr[r + 1]; p++) { int q = pos[A.Cols[p]]++; T.Cols[q] = r; T.Vals[q] = A.Vals[p]; }
            return T;
        }
    }
}
