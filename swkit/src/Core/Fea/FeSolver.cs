using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwKit.Core.Urdf;

namespace SwKit.Core.Fea
{
    /// <summary>
    /// 材料（各向同性线弹性）。数值取常用牌号的典型值（手册 / 厂家数据表），实际批次会有差别，重要零件请按供应商数据核对。
    /// 塑性材料（钢、铝、钛、铜、多数塑料）按 von Mises 等效应力对比屈服强度；
    /// 脆性材料（铸铁、碳纤维 / 玻纤复合材料、亚克力、光敏树脂）按最大主应力对比抗拉强度、最小主应力对比抗压强度。
    /// </summary>
    public sealed class FeMaterial
    {
        public string Name, Category, Note;
        public double E, Nu, Yield, Ultimate, Compressive, Density;   // Pa, -, Pa, Pa, Pa, kg/m³
        public bool Brittle;                   // 脆性：用主应力判据
        public bool Printed;                   // 3D 打印材料（层间强度低）
        public bool Custom;

        /// <summary>判断安全系数用的强度：塑性材料用屈服强度，脆性材料用抗拉强度。</summary>
        public double Strength => Brittle ? Ultimate : Yield;
        public double CompStrength => Compressive > 0 ? Compressive : Strength;

        static FeMaterial M(string cat, string name, double eGPa, double nu, double yMPa, double uMPa, double rho, string note = null, bool brittle = false, bool printed = false, double cMPa = 0) =>
            new FeMaterial { Category = cat, Name = name, E = eGPa * 1e9, Nu = nu, Yield = yMPa * 1e6, Ultimate = uMPa * 1e6, Compressive = cMPa * 1e6, Density = rho, Note = note, Brittle = brittle, Printed = printed };

        public static readonly List<FeMaterial> All = new List<FeMaterial>
        {
            // ---- 钢 ----
            M("钢", "Q235", 206, 0.30, 235, 375, 7850, "普通碳素结构钢"),
            M("钢", "Q355", 206, 0.30, 355, 470, 7850, "低合金高强度结构钢（原 Q345）"),
            M("钢", "45 钢", 206, 0.30, 355, 600, 7850, "正火状态；调质后屈服强度约 500 MPa"),
            M("钢", "40Cr 调质", 206, 0.30, 785, 980, 7850, "轴、齿轮常用"),
            M("钢", "65Mn 弹簧钢", 206, 0.30, 785, 980, 7850, "淬火回火"),
            M("钢", "灰铸铁 HT200", 110, 0.26, 0, 200, 7200, "脆性，抗压强度约为抗拉的 3.5 倍", brittle: true, cMPa: 700),
            // ---- 不锈钢 ----
            M("不锈钢", "304 不锈钢", 193, 0.29, 205, 520, 8000),
            M("不锈钢", "316 不锈钢", 193, 0.30, 205, 515, 8000, "耐腐蚀更好"),
            M("不锈钢", "17-4PH H1150", 197, 0.27, 725, 930, 7800, "沉淀硬化不锈钢"),
            // ---- 铝合金 ----
            M("铝合金", "6061-T6", 68.9, 0.33, 276, 310, 2700, "最常用的 CNC 铝合金"),
            M("铝合金", "6063-T5", 68.9, 0.33, 145, 186, 2700, "铝型材常用"),
            M("铝合金", "7075-T6", 71.7, 0.33, 503, 572, 2810, "高强度 CNC 铝合金（航空铝）"),
            M("铝合金", "2A12-T4", 73.1, 0.33, 325, 470, 2780, "硬铝，相当于 2024-T3"),
            M("铝合金", "5052-H32", 70.3, 0.33, 193, 228, 2680, "钣金常用"),
            M("铝合金", "ADC12 压铸铝", 71, 0.33, 150, 310, 2740, "压铸件，延伸率低"),
            // ---- 钛合金 ----
            M("钛合金", "TC4 (Ti-6Al-4V)", 113.8, 0.342, 880, 950, 4430, "比强度高，退火态"),
            M("钛合金", "TA2 纯钛", 105, 0.34, 275, 345, 4510, "工业纯钛 Grade 2"),
            // ---- 铜合金 ----
            M("铜合金", "黄铜 H62", 105, 0.34, 200, 370, 8430, "半硬态"),
            M("铜合金", "锡青铜 QSn6.5-0.1", 110, 0.34, 250, 400, 8800, "耐磨，轴套常用"),
            // ---- 复合材料（按准各向同性等效，只适合面内受力）----
            M("复合材料", "碳纤维板（3K 斜纹）", 55, 0.30, 0, 550, 1550, "按准各向同性层合板等效；厚度方向（层间）强度低得多", brittle: true, cMPa: 450),
            M("复合材料", "碳纤维管（轴向）", 110, 0.30, 0, 1000, 1600, "按轴向性能等效，只适合轴向拉压和弯曲；环向和扭转偏乐观", brittle: true, cMPa: 700),
            M("复合材料", "玻璃纤维板 FR4/G10", 20, 0.15, 0, 300, 1850, "按面内性能等效", brittle: true, cMPa: 400),
            M("复合材料", "PA66-GF30 玻纤尼龙", 9, 0.35, 0, 180, 1350, "干态；吸湿后模量和强度下降约 30%", brittle: true, cMPa: 220),
            // ---- 工程塑料（CNC）----
            M("工程塑料", "POM 赛钢", 2.9, 0.35, 65, 70, 1410, "CNC 常用，耐磨"),
            M("工程塑料", "MC 尼龙 PA6", 3.0, 0.39, 70, 80, 1150, "干态"),
            M("工程塑料", "PEEK", 3.6, 0.40, 100, 110, 1310, "耐高温"),
            M("工程塑料", "PC 聚碳酸酯", 2.4, 0.37, 62, 70, 1200),
            M("工程塑料", "亚克力 PMMA", 3.2, 0.37, 0, 70, 1190, "脆性", brittle: true, cMPa: 110),
            // ---- 3D 打印 ----
            M("3D 打印", "PLA", 3.5, 0.36, 50, 55, 1240, null, printed: true),
            M("3D 打印", "PETG", 2.1, 0.38, 50, 53, 1270, null, printed: true),
            M("3D 打印", "ABS", 2.2, 0.35, 40, 43, 1050, null, printed: true),
            M("3D 打印", "尼龙碳纤维 PA-CF", 6, 0.35, 70, 80, 1150, null, printed: true),
            M("3D 打印", "光敏树脂（标准）", 2.5, 0.38, 0, 55, 1180, "SLA / DLP 打印，脆性", brittle: true, printed: true, cMPa: 80),
        };

        public static IEnumerable<string> Categories => All.Select(m => m.Category).Distinct();

        public static FeMaterial CreateCustom(double eGPa, double nu, double strengthMPa, double rho, bool brittle) =>
            new FeMaterial { Category = "自定义", Name = "自定义材料", E = eGPa * 1e9, Nu = nu, Yield = brittle ? 0 : strengthMPa * 1e6, Ultimate = strengthMPa * 1e6, Density = rho, Brittle = brittle, Custom = true };
    }

    public enum LoadKind { Force, Pressure, Torque, Gravity }

    /// <summary>
    /// 载荷：
    ///  · Force：在所选面上均匀分布的总力 Value（N），方向 Dir；AlongNormal 时沿面的内法向（压向零件）。
    ///  · Pressure：压强 Value（Pa），垂直压向面。
    ///  · Torque：绕过所选面形心、方向为 Dir 的轴的扭矩 Value（N·m），按切向分布（适合轴的圆柱面、法兰端面）。
    ///  · Gravity：重力（或惯性力）Value 倍 g，方向 Dir，作用在整个零件上。
    /// </summary>
    public sealed class FeLoad
    {
        public LoadKind Kind;
        public List<int> Faces = new List<int>();
        public double Value;
        public Vec3 Dir = new Vec3(0, -1, 0);
        public bool AlongNormal;
        public int Case;                   // 属于第几个工况（FeModel.Cases 的下标；只有一个工况时不用管）
        public FeLoad Clone() => new FeLoad { Kind = Kind, Faces = Faces.ToList(), Value = Value, Dir = Dir, AlongNormal = AlongNormal, Case = Case };
    }

    /// <summary>
    /// 工况：零件在使用中会遇到的一种受力情况（例如无人机机架的「满油门」「偏航急转」「硬着陆」「侧面磕碰」）。
    /// 约束所有工况共用，载荷各工况分开；分别求解后取包络（每个点取所有工况里最危险的值）。
    /// 拓扑优化按各工况柔度（先除以实心零件在这个工况下的柔度，让大小不同的工况同样重要）的加权和优化。
    /// </summary>
    public sealed class FeLoadCase
    {
        public string Name = "工况 1";
        public double Weight = 1;          // 拓扑优化时的权重
        public FeLoadCase Clone() => new FeLoadCase { Name = Name, Weight = Weight };
    }

    public sealed class FeModel
    {
        public FeMesh Mesh;
        public FeMaterial Material;
        public List<int> FixedFaces = new List<int>();
        public List<FeLoad> Loads = new List<FeLoad>();
        /// <summary>工况列表。空或只有一个：所有载荷同时作用（普通静力分析）。</summary>
        public List<FeLoadCase> Cases = new List<FeLoadCase>();
        public int CaseCount => Math.Max(1, Cases.Count);
        public string CaseName(int c) => c < Cases.Count ? Cases[c].Name : $"工况 {c + 1}";
        public double CaseWeight(int c) => c < Cases.Count ? Math.Max(0, Cases[c].Weight) : 1;
        public IEnumerable<FeLoad> LoadsOf(int c) => CaseCount == 1 ? Loads : Loads.Where(l => l.Case == c);
        /// <summary>复制约束、载荷和工况（换网格时用）。</summary>
        public FeModel With(FeMesh mesh) => new FeModel { Mesh = mesh, Material = Material, FixedFaces = FixedFaces.ToList(), Loads = Loads.Select(l => l.Clone()).ToList(), Cases = Cases.Select(c => c.Clone()).ToList() };
    }

    /// <summary>自适应加密的每一轮。</summary>
    public sealed class AdaptivePass
    {
        public int Elements, Nodes;
        public double MaxVm, MaxVmAway, MaxP1, Eta, Seconds;
    }

    public sealed class FeResult
    {
        public double[] U;                 // 位移 3N（米）
        public double[] VonMises;          // 节点等效应力（Pa）
        public double[] Principal1, Principal3;   // 节点最大 / 最小主应力（Pa）
        public double MaxVm, MaxVmAway;    // 最大等效应力；离开约束面一层单元以外的最大值
        public int MaxVmNode = -1, MaxVmAwayNode = -1;
        public double MaxP1, MinP3; public int MaxP1Node = -1, MinP3Node = -1;
        public double MaxDisp; public int MaxDispNode = -1;
        public Vec3 Applied, Reaction;     // 外载合力、约束反力合力
        public double EquilibriumError;    // |外载 + 反力| / |外载|
        public double SafetyFactor;        // 塑性：屈服强度 / 最大等效应力；脆性：min(抗拉 / 最大主应力, 抗压 / |最小主应力|)
        public int SafetyNode = -1;        // 安全系数最小的位置
        public bool Brittle;               // 用的是主应力判据
        public double StrainEnergy;        // ½ uᵀKu（J）
        public double ErrorEstimate;       // 估计的相对误差（能量范数，Zienkiewicz–Zhu）
        public double[] ElemError2;        // 每个单元的误差平方（自适应加密用）
        public int Iterations; public bool Converged;
        public double Seconds;
        // ---- 多工况 ----
        public string CaseName;            // 这个结果属于哪个工况（包络结果是「包络」；单工况时为 null）
        public List<FeResult> CaseResults; // 包络结果上：每个工况各自的结果
        public int[] GoverningCase;        // 包络结果上：每个节点「最危险」的工况（等效应力最大）
        public int MaxCase = -1;           // 包络结果上：离开约束后的最大应力出现在哪个工况
        public List<string> Notes = new List<string>();
        public List<AdaptivePass> Passes = new List<AdaptivePass>();
        public string Timing;              // 各阶段耗时（调优用）
    }

    /// <summary>
    /// 组装好的稀疏结构：每对相邻节点一个 3×3 块；ElemPos 记录每个单元 10×10 个节点对在行里的位置，组装时不用再查找。
    /// 静力分析和拓扑优化共用。
    /// </summary>
    internal sealed class FeSystem
    {
        public FeMesh Mesh;
        public int N;                // 自由度数
        public int[] RowPtr, Cols;
        public int[] ElemPos;        // 100 × 单元数
        public double[] ElemVolume;

        public static FeSystem Build(FeMesh m)
        {
            int nn = m.Nodes, nd = 3 * nn, ne = m.Elements;
            var nbr = new List<int>[nn];
            {
                var sets = new HashSet<int>[nn];
                for (int i = 0; i < nn; i++) sets[i] = new HashSet<int>();
                for (int e = 0; e < ne; e++)
                    for (int a = 0; a < 10; a++)
                    {
                        var s = sets[m.Tets[10 * e + a]];
                        for (int b = 0; b < 10; b++) s.Add(m.Tets[10 * e + b]);
                    }
                for (int i = 0; i < nn; i++) { nbr[i] = sets[i].ToList(); nbr[i].Sort(); }
            }
            var rowPtr = new int[nd + 1];
            for (int i = 0; i < nn; i++) for (int c = 0; c < 3; c++) rowPtr[3 * i + c + 1] = rowPtr[3 * i + c] + 3 * nbr[i].Count;
            var cols = new int[rowPtr[nd]];
            for (int i = 0; i < nn; i++)
                for (int c = 0; c < 3; c++)
                {
                    int p = rowPtr[3 * i + c];
                    foreach (var j in nbr[i]) for (int d = 0; d < 3; d++) cols[p++] = 3 * j + d;
                }
            var pos = new int[100 * ne];
            for (int e = 0; e < ne; e++)
                for (int a = 0; a < 10; a++)
                {
                    var list = nbr[m.Tets[10 * e + a]];
                    for (int b = 0; b < 10; b++) pos[100 * e + 10 * a + b] = list.BinarySearch(m.Tets[10 * e + b]);
                }
            var sys = new FeSystem { Mesh = m, N = nd, RowPtr = rowPtr, Cols = cols, ElemPos = pos };
            // 单元体积
            sys.ElemVolume = new double[ne];
            Parallel.For(0, ne, () => new Scratch(), (e, st, s) =>
            {
                double v = 0;
                foreach (var g in FeSolver.Gauss) v += FeSolver.Gradients(m, e, g, s.N, s.dN, s.dNdx) * FeSolver.GW;
                sys.ElemVolume[e] = v;
                return s;
            }, s => { });
            return sys;
        }

        internal sealed class Scratch
        {
            public double[] N = new double[10]; public double[,] dN = new double[10, 3], dNdx = new double[10, 3];
            public double[,] B = new double[6, 30], DB = new double[6, 30];
            public double[] Ke = new double[900];
        }

        /// <summary>单元刚度（30×30，按行存进 ke[off..off+900)）。</summary>
        public void ElementK(int e, double[,] D, Scratch s, double[] ke, int off)
        {
            var m = Mesh;
            Array.Clear(ke, off, 900);
            var B = s.B; var DB = s.DB;
            foreach (var g in FeSolver.Gauss)
            {
                var det = FeSolver.Gradients(m, e, g, s.N, s.dN, s.dNdx);
                if (det <= 0) throw new InvalidOperationException("网格里有翻转的单元，请换一个网格尺寸重试");
                Array.Clear(B, 0, B.Length);
                for (int a = 0; a < 10; a++)
                {
                    double x = s.dNdx[a, 0], y = s.dNdx[a, 1], z = s.dNdx[a, 2]; int c = 3 * a;
                    B[0, c] = x; B[1, c + 1] = y; B[2, c + 2] = z;
                    B[3, c] = y; B[3, c + 1] = x; B[4, c + 1] = z; B[4, c + 2] = y; B[5, c] = z; B[5, c + 2] = x;
                }
                for (int i = 0; i < 6; i++)
                    for (int k = 0; k < 30; k++)
                    {
                        double sum = 0; for (int j = 0; j < 6; j++) sum += D[i, j] * B[j, k];
                        DB[i, k] = sum;
                    }
                var w = det * FeSolver.GW;
                for (int r = 0; r < 30; r++)
                    for (int c = r; c < 30; c++)
                    {
                        double sum = 0; for (int i = 0; i < 6; i++) sum += B[i, r] * DB[i, c];
                        ke[off + 30 * r + c] += sum * w;
                    }
            }
            for (int r = 0; r < 30; r++) for (int c = 0; c < r; c++) ke[off + 30 * r + c] = ke[off + 30 * c + r];
        }

        /// <summary>把一个单元的刚度乘以 scale 加进 vals。</summary>
        public void Scatter(int e, double[] ke, int off, double scale, double[] vals)
        {
            var m = Mesh;
            for (int a = 0; a < 10; a++)
            {
                int na = m.Tets[10 * e + a];
                for (int b = 0; b < 10; b++)
                {
                    int pos = ElemPos[100 * e + 10 * a + b];
                    for (int c = 0; c < 3; c++)
                    {
                        int baseIdx = RowPtr[3 * na + c] + 3 * pos;
                        int kr = off + 30 * (3 * a + c) + 3 * b;
                        vals[baseIdx] += ke[kr] * scale; vals[baseIdx + 1] += ke[kr + 1] * scale; vals[baseIdx + 2] += ke[kr + 2] * scale;
                    }
                }
            }
        }

        public void Scatter(int e, float[] ke, long off, double scale, double[] vals)
        {
            var m = Mesh;
            for (int a = 0; a < 10; a++)
            {
                int na = m.Tets[10 * e + a];
                for (int b = 0; b < 10; b++)
                {
                    int pos = ElemPos[100 * e + 10 * a + b];
                    for (int c = 0; c < 3; c++)
                    {
                        int baseIdx = RowPtr[3 * na + c] + 3 * pos;
                        long kr = off + 30 * (3 * a + c) + 3 * b;
                        vals[baseIdx] += ke[kr] * scale; vals[baseIdx + 1] += ke[kr + 1] * scale; vals[baseIdx + 2] += ke[kr + 2] * scale;
                    }
                }
            }
        }

        /// <summary>组装整体刚度：按块并行算单元刚度，再顺序写入（避免加锁）。</summary>
        public double[] Assemble(double[,] D, CancellationToken ct)
        {
            var vals = new double[Cols.Length];
            int ne = Mesh.Elements;
            const int Chunk = 2048;
            var buf = new double[Chunk * 900];
            for (int e0 = 0; e0 < ne; e0 += Chunk)
            {
                ct.ThrowIfCancellationRequested();
                int cnt = Math.Min(Chunk, ne - e0);
                Parallel.For(0, cnt, () => new Scratch(), (k, st, s) => { ElementK(e0 + k, D, s, buf, 900 * k); return s; }, s => { });
                for (int k = 0; k < cnt; k++) Scatter(e0 + k, buf, 900 * k, 1, vals);
            }
            return vals;
        }

        /// <summary>固定的自由度：该行该列清零，对角置 1，右端为 0。</summary>
        public void ApplyFixed(double[] vals, double[] f, bool[] isFixed)
        {
            for (int r = 0; r < N; r++)
            {
                for (int p = RowPtr[r]; p < RowPtr[r + 1]; p++)
                {
                    if (!isFixed[r] && !isFixed[Cols[p]]) continue;
                    vals[p] = Cols[p] == r ? 1 : 0;
                }
                if (isFixed[r]) f[r] = 0;
            }
        }
    }

    /// <summary>线弹性静力分析，二次四面体单元（TET10）。</summary>
    public static class FeSolver
    {
        // ---------------- 形函数 ----------------
        static readonly int[,] Edges = { { 0, 1 }, { 1, 2 }, { 2, 0 }, { 3, 0 }, { 3, 2 }, { 3, 1 } };
        static readonly double[,] DL = { { -1, -1, -1 }, { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };   // dL_i/dξ_k

        /// <summary>TET10 形函数值和对自然坐标的导数。</summary>
        internal static void Shape(double xi, double eta, double zeta, double[] N, double[,] dN)
        {
            double l0 = 1 - xi - eta - zeta;
            double L0 = l0, L1 = xi, L2 = eta, L3 = zeta;
            double Li(int i) => i == 0 ? L0 : i == 1 ? L1 : i == 2 ? L2 : L3;
            for (int i = 0; i < 4; i++)
            {
                var L = Li(i);
                N[i] = L * (2 * L - 1);
                for (int k = 0; k < 3; k++) dN[i, k] = (4 * L - 1) * DL[i, k];
            }
            for (int e = 0; e < 6; e++)
            {
                int i = Edges[e, 0], j = Edges[e, 1];
                double a = Li(i), b = Li(j);
                N[4 + e] = 4 * a * b;
                for (int k = 0; k < 3; k++) dN[4 + e, k] = 4 * (b * DL[i, k] + a * DL[j, k]);
            }
        }

        static readonly double GA = 0.5854101966249685, GB = 0.1381966011250105;
        internal static readonly double[][] Gauss = { new[] { GB, GB, GB }, new[] { GA, GB, GB }, new[] { GB, GA, GB }, new[] { GB, GB, GA } };
        internal const double GW = 1.0 / 24;
        // 10 个节点的自然坐标（用于在节点处求应力）
        static readonly double[][] NodeXi =
        {
            new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 },
            new[] { 0.5, 0, 0 }, new[] { 0.5, 0.5, 0 }, new[] { 0.0, 0.5, 0 }, new[] { 0.0, 0, 0.5 }, new[] { 0.0, 0.5, 0.5 }, new[] { 0.5, 0, 0.5 },
        };

        /// <summary>在自然坐标处求 dN/dx（10×3）和 det J。</summary>
        internal static double Gradients(FeMesh m, int e, double[] xi, double[] N, double[,] dN, double[,] dNdx)
        {
            Shape(xi[0], xi[1], xi[2], N, dN);
            double j00 = 0, j01 = 0, j02 = 0, j10 = 0, j11 = 0, j12 = 0, j20 = 0, j21 = 0, j22 = 0;
            for (int a = 0; a < 10; a++)
            {
                int n = 3 * m.Tets[10 * e + a];
                double x = m.X[n], y = m.X[n + 1], z = m.X[n + 2];
                double d0 = dN[a, 0], d1 = dN[a, 1], d2 = dN[a, 2];
                j00 += d0 * x; j01 += d0 * y; j02 += d0 * z;
                j10 += d1 * x; j11 += d1 * y; j12 += d1 * z;
                j20 += d2 * x; j21 += d2 * y; j22 += d2 * z;
            }
            double det = j00 * (j11 * j22 - j12 * j21) - j01 * (j10 * j22 - j12 * j20) + j02 * (j10 * j21 - j11 * j20);
            double i00 = (j11 * j22 - j12 * j21) / det, i01 = (j02 * j21 - j01 * j22) / det, i02 = (j01 * j12 - j02 * j11) / det;
            double i10 = (j12 * j20 - j10 * j22) / det, i11 = (j00 * j22 - j02 * j20) / det, i12 = (j02 * j10 - j00 * j12) / det;
            double i20 = (j10 * j21 - j11 * j20) / det, i21 = (j01 * j20 - j00 * j21) / det, i22 = (j00 * j11 - j01 * j10) / det;
            // dN/dx_j = Σ_k inv[j,k] dN/dξ_k
            for (int a = 0; a < 10; a++)
            {
                double d0 = dN[a, 0], d1 = dN[a, 1], d2 = dN[a, 2];
                dNdx[a, 0] = i00 * d0 + i01 * d1 + i02 * d2;
                dNdx[a, 1] = i10 * d0 + i11 * d1 + i12 * d2;
                dNdx[a, 2] = i20 * d0 + i21 * d1 + i22 * d2;
            }
            return det;
        }

        internal static double[,] Elasticity(FeMaterial mat)
        {
            double E = mat.E, nu = mat.Nu, lam = E * nu / ((1 + nu) * (1 - 2 * nu)), mu = E / (2 * (1 + nu));
            var D = new double[6, 6];
            for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) D[i, j] = lam; D[i, i] = lam + 2 * mu; D[3 + i, 3 + i] = mu; }
            return D;
        }

        /// <summary>应变 ε = B u（工程剪应变），u 为单元 30 个位移。</summary>
        internal static void Strain(double[,] dNdx, double[] ue, double[] eps)
        {
            Array.Clear(eps, 0, 6);
            for (int a = 0; a < 10; a++)
            {
                double ux = ue[3 * a], uy = ue[3 * a + 1], uz = ue[3 * a + 2], nx = dNdx[a, 0], ny = dNdx[a, 1], nz = dNdx[a, 2];
                eps[0] += nx * ux; eps[1] += ny * uy; eps[2] += nz * uz;
                eps[3] += ny * ux + nx * uy; eps[4] += nz * uy + ny * uz; eps[5] += nz * ux + nx * uz;
            }
        }

        /// <summary>应力的余能密度 ×2：σᵀ C σ（C = D⁻¹，各向同性）。</summary>
        static double ComplianceNorm(double[] s, double E, double nu)
        {
            double a = s[0] * s[0] + s[1] * s[1] + s[2] * s[2], b = s[0] * s[1] + s[1] * s[2] + s[2] * s[0], t = s[3] * s[3] + s[4] * s[4] + s[5] * s[5];
            return (a - 2 * nu * b + 2 * (1 + nu) * t) / E;
        }

        /// <summary>对称 3×3 应力张量的主应力（从大到小）。</summary>
        internal static void Principal(double sx, double sy, double sz, double txy, double tyz, double tzx, out double s1, out double s3)
        {
            double p1 = txy * txy + tyz * tyz + tzx * tzx;
            double q = (sx + sy + sz) / 3;
            if (p1 < 1e-30 * (sx * sx + sy * sy + sz * sz + 1e-300))
            {
                s1 = Math.Max(sx, Math.Max(sy, sz)); s3 = Math.Min(sx, Math.Min(sy, sz)); return;
            }
            double p2 = (sx - q) * (sx - q) + (sy - q) * (sy - q) + (sz - q) * (sz - q) + 2 * p1;
            double p = Math.Sqrt(p2 / 6);
            double b00 = (sx - q) / p, b11 = (sy - q) / p, b22 = (sz - q) / p, b01 = txy / p, b12 = tyz / p, b02 = tzx / p;
            double det = b00 * (b11 * b22 - b12 * b12) - b01 * (b01 * b22 - b12 * b02) + b02 * (b01 * b12 - b11 * b02);
            double r = Math.Max(-1, Math.Min(1, det / 2));
            double phi = Math.Acos(r) / 3;
            s1 = q + 2 * p * Math.Cos(phi);
            s3 = q + 2 * p * Math.Cos(phi + 2 * Math.PI / 3);
        }

        // ================= 求解 =================

        public static FeResult Solve(FeModel model, Action<string> log, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var m = model.Mesh;
            int nn = m.Nodes, nd = 3 * nn, ne = m.Elements;
            var mat = model.Material;
            var D = Elasticity(mat);
            if (model.FixedFaces.Count == 0) throw new InvalidOperationException("没有设置固定面：至少要固定一个面，零件才不会整体飞走");

            log?.Invoke("正在组装刚度矩阵……");
            var tm = Stopwatch.StartNew();
            var sys = FeSystem.Build(m);
            var tBuild = tm.Elapsed.TotalSeconds; tm.Restart();
            var vals = sys.Assemble(D, ct);
            var tAsm = tm.Elapsed.TotalSeconds; tm.Restart();

            // ---- 载荷（每个工况一个载荷向量）----
            int nc = model.CaseCount;
            var fs = new double[nc][]; var applieds = new Vec3[nc];
            for (int c = 0; c < nc; c++)
            {
                fs[c] = new double[nd];
                foreach (var load in model.LoadsOf(c)) applieds[c] = applieds[c] + ApplyLoad(m, mat, load, fs[c]);
                if (nc > 1 && !model.LoadsOf(c).Any()) throw new InvalidOperationException($"「{model.CaseName(c)}」还没有载荷：给它加载荷，或者删掉这个工况");
            }

            // ---- 约束（所有工况共用）----
            var fExts = fs.Select(x => (double[])x.Clone()).ToArray();     // 约束前的外载，算反力时要用（落在固定节点上的外载直接由支撑承担）
            var fixedNodes = m.FaceNodes(model.FixedFaces);
            var isFixed = new bool[nd];
            foreach (var n in fixedNodes) for (int c = 0; c < 3; c++) isFixed[3 * n + c] = true;
            sys.ApplyFixed(vals, fs[0], isFixed);
            for (int c = 1; c < nc; c++) for (int r = 0; r < nd; r++) if (isFixed[r]) fs[c][r] = 0;
            log?.Invoke($"矩阵：{nd:N0} 个自由度，{vals.Length:N0} 个非零元，固定了 {isFixed.Count(x => x):N0} 个自由度" + (nc > 1 ? $"；{nc} 个工况" : ""));

            // ---- 求解 K u = f：预条件共轭梯度。多个工况共用一次组装和一次预条件 ----
            var A = new Csr { N = nd, RowPtr = sys.RowPtr, Cols = sys.Cols, Vals = vals, Block3 = true };
            var tFix = tm.Elapsed.TotalSeconds; tm.Restart();
            string precName = "不完全 Cholesky";
            double amgSetup = 0;
            Amg amgP = null;
            // 多重网格比不完全 Cholesky 快很多但更占内存（约 3.5 KB / 自由度）；特别大的模型（> 60 万自由度，约 2 GB）为了不占满内存退回不完全 Cholesky
            if (UseAmg && nd > 3000 && nd < 600000)
            {
                try
                {
                    amgP = Amg.Build(A, m.X, isFixed);
                    amgSetup = amgP.SetupSeconds;
                    log?.Invoke($"代数多重网格：{amgP.Info}（{amgSetup:0.00} 秒）");
                    precName = "代数多重网格 " + amgP.Info;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { log?.Invoke("多重网格出错（" + ex.Message + "），改用不完全 Cholesky"); amgP = null; }
            }
            LastPrecondSeconds = amgSetup;
            var results = new List<FeResult>();
            double tPcgSum = 0, tPostSum = 0;
            for (int c = 0; c < nc; c++)
            {
                if (nc > 1) log?.Invoke($"正在求解「{model.CaseName(c)}」（{c + 1}/{nc}）……");
                var u = new double[nd];
                var res = new FeResult { Brittle = mat.Brittle, CaseName = nc > 1 ? model.CaseName(c) : null };
                res.Converged = false;
                if (amgP != null)
                {
                    try
                    {
                        res.Converged = Pcg(A, fs[c], u, Tol, 600, out res.Iterations, log, ct, amgP);
                        if (!res.Converged) { log?.Invoke("多重网格没有收敛，改用不完全 Cholesky"); if (u.Any(v => double.IsNaN(v))) Array.Clear(u, 0, u.Length); }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { log?.Invoke("多重网格出错（" + ex.Message + "），改用不完全 Cholesky"); Array.Clear(u, 0, u.Length); }
                }
                if (!res.Converged)
                {
                    int it0 = res.Iterations;
                    res.Converged = Pcg(A, fs[c], u, Tol, 20000, out res.Iterations, log, ct);
                    res.Iterations += it0;
                    if (amgSetup > 0) precName = "不完全 Cholesky（多重网格未收敛）";
                }
                tPcgSum += tm.Elapsed.TotalSeconds; tm.Restart();
                if (!res.Converged) res.Notes.Add($"迭代 {res.Iterations} 次没有完全收敛，结果可能不准（约束可能不足以限制零件的整体运动）");
                if (model.Mesh.StraightenedElements > 0) res.Notes.Add($"有 {model.Mesh.StraightenedElements} 个曲边单元被网格程序弯翻了，已自动拉直（只影响那几个单元的曲面精度）");
                res.U = u;

                log?.Invoke("正在计算应力……");
                PostProcess(m, sys, mat, D, u, fExts[c], fixedNodes, applieds[c], res, ct);
                tPostSum += tm.Elapsed.TotalSeconds; tm.Restart();
                results.Add(res);
            }
            amgP = null; GC.Collect();
            var final = nc == 1 ? results[0] : Envelope(m, mat, results);
            final.Seconds = sw.Elapsed.TotalSeconds;
            final.Timing = $"结构 {tBuild:0.00} 秒，组装 {tAsm:0.00}，约束 {tFix:0.00}，预条件 {amgSetup:0.00}，迭代 {tPcgSum:0.00}（{final.Iterations} 次，{precName}），后处理 {tPostSum:0.00}";
            return final;
        }

        /// <summary>
        /// 多工况的包络：每个节点取所有工况里最大的等效应力、最大主应力、最小主应力（最危险的值），
        /// 安全系数取最小，位移取最大位移的那个工况。各工况的结果放在 CaseResults 里。
        /// </summary>
        public static FeResult Envelope(FeMesh m, FeMaterial mat, List<FeResult> rs)
        {
            int nn = m.Nodes, ne = m.Elements;
            var env = new FeResult { Brittle = mat.Brittle, CaseName = "包络", CaseResults = rs, Converged = rs.All(r => r.Converged) };
            env.VonMises = new double[nn]; env.Principal1 = new double[nn]; env.Principal3 = new double[nn];
            env.GoverningCase = new int[nn];
            for (int n = 0; n < nn; n++)
            {
                double vm = 0, p1 = double.MinValue, p3 = double.MaxValue; int g = 0;
                for (int c = 0; c < rs.Count; c++)
                {
                    if (rs[c].VonMises[n] > vm) { vm = rs[c].VonMises[n]; g = c; }
                    p1 = Math.Max(p1, rs[c].Principal1[n]); p3 = Math.Min(p3, rs[c].Principal3[n]);
                }
                env.VonMises[n] = vm; env.Principal1[n] = p1; env.Principal3[n] = p3; env.GoverningCase[n] = g;
            }
            var dispCase = rs.Select((r, i) => (r, i)).OrderByDescending(t => t.r.MaxDisp).First();
            env.U = dispCase.r.U; env.MaxDisp = dispCase.r.MaxDisp; env.MaxDispNode = dispCase.r.MaxDispNode;
            env.MaxVm = -1; env.MaxVmAway = -1; env.MaxP1 = double.MinValue; env.MinP3 = double.MaxValue; env.SafetyFactor = double.PositiveInfinity;
            for (int c = 0; c < rs.Count; c++)
            {
                var r = rs[c];
                if (r.MaxVm > env.MaxVm) { env.MaxVm = r.MaxVm; env.MaxVmNode = r.MaxVmNode; }
                if (r.MaxVmAway > env.MaxVmAway) { env.MaxVmAway = r.MaxVmAway; env.MaxVmAwayNode = r.MaxVmAwayNode; env.MaxCase = c; }
                if (r.MaxP1 > env.MaxP1) { env.MaxP1 = r.MaxP1; env.MaxP1Node = r.MaxP1Node; }
                if (r.MinP3 < env.MinP3) { env.MinP3 = r.MinP3; env.MinP3Node = r.MinP3Node; }
                if (r.SafetyFactor < env.SafetyFactor) { env.SafetyFactor = r.SafetyFactor; env.SafetyNode = r.SafetyNode; }
                env.EquilibriumError = Math.Max(env.EquilibriumError, r.EquilibriumError);
                env.ErrorEstimate = Math.Max(env.ErrorEstimate, r.ErrorEstimate);
                env.StrainEnergy = Math.Max(env.StrainEnergy, r.StrainEnergy);
                env.Iterations += r.Iterations;
            }
            env.ElemError2 = new double[ne];
            for (int e = 0; e < ne; e++) env.ElemError2[e] = rs.Max(r => r.ElemError2[e]);
            var sfCase = rs.Select((r, i) => (r, i)).OrderBy(t => t.r.SafetyFactor).First();
            env.Applied = sfCase.r.Applied; env.Reaction = sfCase.r.Reaction;
            env.Notes.Add($"包络：每个点取 {rs.Count} 个工况里最危险的值。最危险的工况是「{sfCase.r.CaseName}」（安全系数 {sfCase.r.SafetyFactor:0.##}）" +
                          (env.MaxCase >= 0 && env.MaxCase != sfCase.i ? $"；离开约束后的最大等效应力出现在「{rs[env.MaxCase].CaseName}」" : ""));
            if (env.MaxVmNode >= 0 && env.MaxVmAway > 0 && env.MaxVm > 1.3 * env.MaxVmAway && rs.Any(r => r.Notes.Any(x => x.StartsWith("最大应力出现在固定面边缘"))))
                env.Notes.Add($"最大应力出现在固定面边缘。固定约束比真实支撑更「硬」，那里的应力往往偏高；离开固定面后的最大应力是 {env.MaxVmAway / 1e6:0.##} MPa");
            if (mat.Brittle)
                env.Notes.Add($"{mat.Name} 是脆性材料，按最大主应力判断：最大拉应力 {Math.Max(0, env.MaxP1) / 1e6:0.##} MPa（抗拉 {mat.Strength / 1e6:0} MPa），最大压应力 {Math.Max(0, -env.MinP3) / 1e6:0.##} MPa（抗压 {mat.CompStrength / 1e6:0} MPa）");
            foreach (var r in rs)
                foreach (var note in r.Notes.Where(x => x.StartsWith("迭代 "))) env.Notes.Add($"「{r.CaseName}」：{note}");
            foreach (var note in rs.SelectMany(r => r.Notes).Where(x => !x.StartsWith("最大应力出现在固定面边缘") && !x.StartsWith(mat.Name + " 是脆性材料") && !x.StartsWith("迭代 ")).Distinct()) env.Notes.Add(note);
            return env;
        }

        /// <summary>节点应力（单元节点处求值再平均）、主应力、反力、误差估计、最大值和安全系数。</summary>
        static void PostProcess(FeMesh m, FeSystem sys, FeMaterial mat, double[,] D, double[] u, double[] fExt, HashSet<int> fixedNodes, Vec3 applied, FeResult res, CancellationToken ct)
        {
            int nn = m.Nodes, nd = 3 * nn, ne = m.Elements;
            var sigSum = new double[6 * nn];
            var cnt = new int[nn];
            var fint = new double[nd];
            const int Chunk = 4096;
            var bufS = new double[Chunk * 60];   // 每个单元 10 个节点 × 6 个应力分量
            var bufF = new double[Chunk * 30];   // 每个单元的内力
            for (int e0 = 0; e0 < ne; e0 += Chunk)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(Chunk, ne - e0);
                Parallel.For(0, count, () => new FeSystem.Scratch(), (k, st, s) =>
                {
                    int e = e0 + k;
                    var ue = new double[30]; var eps = new double[6]; var sig = new double[6];
                    for (int a = 0; a < 10; a++) { int n = m.Tets[10 * e + a]; ue[3 * a] = u[3 * n]; ue[3 * a + 1] = u[3 * n + 1]; ue[3 * a + 2] = u[3 * n + 2]; }
                    for (int a = 0; a < 10; a++)
                    {
                        Gradients(m, e, NodeXi[a], s.N, s.dN, s.dNdx);
                        Strain(s.dNdx, ue, eps);
                        for (int i = 0; i < 6; i++) { double v = 0; for (int j = 0; j < 6; j++) v += D[i, j] * eps[j]; bufS[60 * k + 6 * a + i] = v; }
                    }
                    Array.Clear(bufF, 30 * k, 30);
                    foreach (var g in Gauss)
                    {
                        var det = Gradients(m, e, g, s.N, s.dN, s.dNdx);
                        Strain(s.dNdx, ue, eps);
                        for (int i = 0; i < 6; i++) { double v = 0; for (int j = 0; j < 6; j++) v += D[i, j] * eps[j]; sig[i] = v; }
                        var w = det * GW;
                        for (int a = 0; a < 10; a++)
                        {
                            double x = s.dNdx[a, 0], y = s.dNdx[a, 1], z = s.dNdx[a, 2];
                            bufF[30 * k + 3 * a] += (x * sig[0] + y * sig[3] + z * sig[5]) * w;
                            bufF[30 * k + 3 * a + 1] += (y * sig[1] + x * sig[3] + z * sig[4]) * w;
                            bufF[30 * k + 3 * a + 2] += (z * sig[2] + y * sig[4] + x * sig[5]) * w;
                        }
                    }
                    return s;
                }, s => { });
                for (int k = 0; k < count; k++)
                {
                    int e = e0 + k;
                    for (int a = 0; a < 10; a++)
                    {
                        int n = m.Tets[10 * e + a];
                        for (int i = 0; i < 6; i++) sigSum[6 * n + i] += bufS[60 * k + 6 * a + i];
                        cnt[n]++;
                        fint[3 * n] += bufF[30 * k + 3 * a]; fint[3 * n + 1] += bufF[30 * k + 3 * a + 1]; fint[3 * n + 2] += bufF[30 * k + 3 * a + 2];
                    }
                }
            }
            // 平均后的节点应力（作为「恢复应力」σ*）
            for (int n = 0; n < nn; n++) if (cnt[n] > 0) for (int i = 0; i < 6; i++) sigSum[6 * n + i] /= cnt[n];
            res.VonMises = new double[nn]; res.Principal1 = new double[nn]; res.Principal3 = new double[nn];
            Parallel.For(0, nn, n =>
            {
                if (cnt[n] == 0) return;
                double sx = sigSum[6 * n], sy = sigSum[6 * n + 1], sz = sigSum[6 * n + 2], txy = sigSum[6 * n + 3], tyz = sigSum[6 * n + 4], tzx = sigSum[6 * n + 5];
                res.VonMises[n] = Math.Sqrt(0.5 * ((sx - sy) * (sx - sy) + (sy - sz) * (sy - sz) + (sz - sx) * (sz - sx)) + 3 * (txy * txy + tyz * tyz + tzx * tzx));
                Principal(sx, sy, sz, txy, tyz, tzx, out var s1, out var s3);
                res.Principal1[n] = s1; res.Principal3[n] = s3;
            });

            // ---- 误差估计（Zienkiewicz–Zhu）：恢复应力 σ* 与单元应力 σh 之差的能量范数 ----
            var err2 = new double[ne]; var ener2 = new double[ne];
            Parallel.For(0, ne, () => new FeSystem.Scratch(), (e, st, s) =>
            {
                var ue = new double[30]; var eps = new double[6]; var sh = new double[6]; var ds = new double[6];
                for (int a = 0; a < 10; a++) { int n = m.Tets[10 * e + a]; ue[3 * a] = u[3 * n]; ue[3 * a + 1] = u[3 * n + 1]; ue[3 * a + 2] = u[3 * n + 2]; }
                double ee = 0, uu = 0;
                foreach (var g in Gauss)
                {
                    var det = Gradients(m, e, g, s.N, s.dN, s.dNdx);
                    Strain(s.dNdx, ue, eps);
                    for (int i = 0; i < 6; i++) { double v = 0; for (int j = 0; j < 6; j++) v += D[i, j] * eps[j]; sh[i] = v; }
                    for (int i = 0; i < 6; i++)
                    {
                        double v = 0;
                        for (int a = 0; a < 10; a++) v += s.N[a] * sigSum[6 * m.Tets[10 * e + a] + i];
                        ds[i] = v - sh[i];
                    }
                    var w = det * GW;
                    ee += ComplianceNorm(ds, mat.E, mat.Nu) * w;
                    uu += ComplianceNorm(sh, mat.E, mat.Nu) * w;
                }
                err2[e] = ee; ener2[e] = uu;
                return s;
            }, s => { });
            double E2 = err2.Sum(), U2 = ener2.Sum();
            res.ElemError2 = err2;
            res.StrainEnergy = U2 / 2;
            res.ErrorEstimate = U2 + E2 > 0 ? Math.Sqrt(E2 / (U2 + E2)) : 0;

            // 反力（支撑作用在零件上的力）：固定节点上 内力 − 外载
            var reaction = Vec3.Zero;
            foreach (var n in fixedNodes)
                reaction = reaction + new Vec3(fint[3 * n] - fExt[3 * n], fint[3 * n + 1] - fExt[3 * n + 1], fint[3 * n + 2] - fExt[3 * n + 2]);
            res.Applied = applied; res.Reaction = reaction;
            res.EquilibriumError = applied.Length > 0 ? (applied + reaction).Length / applied.Length : 0;

            // 最大值；「离开约束一层单元」的最大值（固定约束边缘常有不真实的应力集中）
            var nearFixed = new HashSet<int>(fixedNodes);
            for (int e = 0; e < ne; e++)
            {
                bool touches = false;
                for (int a = 0; a < 10 && !touches; a++) touches = fixedNodes.Contains(m.Tets[10 * e + a]);
                if (touches) for (int a = 0; a < 10; a++) nearFixed.Add(m.Tets[10 * e + a]);
            }
            res.MaxP1 = double.MinValue; res.MinP3 = double.MaxValue;
            double minSf = double.PositiveInfinity, minSfAway = double.PositiveInfinity;
            double ut = mat.Strength, uc = mat.CompStrength;
            for (int n = 0; n < nn; n++)
            {
                if (cnt[n] == 0) continue;
                var vm = res.VonMises[n];
                if (vm > res.MaxVm) { res.MaxVm = vm; res.MaxVmNode = n; }
                if (!nearFixed.Contains(n) && vm > res.MaxVmAway) { res.MaxVmAway = vm; res.MaxVmAwayNode = n; }
                if (res.Principal1[n] > res.MaxP1) { res.MaxP1 = res.Principal1[n]; res.MaxP1Node = n; }
                if (res.Principal3[n] < res.MinP3) { res.MinP3 = res.Principal3[n]; res.MinP3Node = n; }
                var d = Math.Sqrt(u[3 * n] * u[3 * n] + u[3 * n + 1] * u[3 * n + 1] + u[3 * n + 2] * u[3 * n + 2]);
                if (d > res.MaxDisp) { res.MaxDisp = d; res.MaxDispNode = n; }
                double sf;
                if (mat.Brittle)
                {
                    double a = res.Principal1[n] > 0 ? ut / res.Principal1[n] : double.PositiveInfinity;
                    double b = res.Principal3[n] < 0 ? uc / -res.Principal3[n] : double.PositiveInfinity;
                    sf = Math.Min(a, b);
                }
                else sf = vm > 0 ? ut / vm : double.PositiveInfinity;
                if (sf < minSf) { minSf = sf; res.SafetyNode = n; }
                if (!nearFixed.Contains(n) && sf < minSfAway) minSfAway = sf;
            }
            res.SafetyFactor = minSf;
            if (res.MaxVmNode >= 0 && nearFixed.Contains(res.MaxVmNode) && res.MaxVmAway > 0 && res.MaxVm > 1.3 * res.MaxVmAway)
                res.Notes.Add($"最大应力出现在固定面边缘。固定约束比真实支撑更「硬」，那里的应力往往偏高；离开固定面后的最大应力是 {res.MaxVmAway / 1e6:0.##} MPa");
            if (mat.Brittle)
                res.Notes.Add($"{mat.Name} 是脆性材料，按最大主应力判断：最大拉应力 {Math.Max(0, res.MaxP1) / 1e6:0.##} MPa（抗拉 {ut / 1e6:0} MPa），最大压应力 {Math.Max(0, -res.MinP3) / 1e6:0.##} MPa（抗压 {uc / 1e6:0} MPa）");
            if (mat.Printed)
                res.Notes.Add("3D 打印件层与层之间的强度可能只有标称值的一半左右，受力方向垂直于打印层时安全系数建议取 3 以上");
            if (mat.Category == "复合材料")
                res.Notes.Add("复合材料按各向同性等效计算，只能粗略估计；铺层方向、层间强度、开孔和连接处要单独校核");
        }

        // ================= 载荷换算成节点力 =================

        // 三角形 6 点积分（4 阶精度），重心坐标 (L1, L2) 和权重（权重和为 1）
        static readonly double[][] TriQ =
        {
            new[] { 0.445948490915965, 0.445948490915965, 0.223381589678011 }, new[] { 0.108103018168070, 0.445948490915965, 0.223381589678011 },
            new[] { 0.445948490915965, 0.108103018168070, 0.223381589678011 }, new[] { 0.091576213509771, 0.091576213509771, 0.109951743655322 },
            new[] { 0.816847572980459, 0.091576213509771, 0.109951743655322 }, new[] { 0.091576213509771, 0.816847572980459, 0.109951743655322 },
        };

        /// <summary>在面上积分：对每个积分点调用 fn(位置, 外法向) 得到面力（N/m²），累加到 6 个节点上。返回合力。</summary>
        static Vec3 SurfaceIntegrate(FeMesh m, IEnumerable<int> faces, double[] f, Func<Vec3, Vec3, Vec3> traction)
        {
            var ids = new HashSet<int>(faces);
            var total = Vec3.Zero;
            var Nt = new double[6];
            for (int t = 0; t < m.TriFace.Length; t++)
            {
                if (!ids.Contains(m.TriFace[t])) continue;
                var outward = m.TriNormal(t, out _);
                foreach (var q in TriQ)
                {
                    double xi = q[0], eta = q[1], l0 = 1 - xi - eta;
                    Nt[0] = l0 * (2 * l0 - 1); Nt[1] = xi * (2 * xi - 1); Nt[2] = eta * (2 * eta - 1);
                    Nt[3] = 4 * l0 * xi; Nt[4] = 4 * xi * eta; Nt[5] = 4 * eta * l0;
                    // 对 ξ、η 的导数
                    var dxi = new[] { -(4 * l0 - 1), 4 * xi - 1, 0, 4 * (l0 - xi), 4 * eta, -4 * eta };
                    var deta = new[] { -(4 * l0 - 1), 0, 4 * eta - 1, -4 * xi, 4 * xi, 4 * (l0 - eta) };
                    Vec3 x = Vec3.Zero, gx = Vec3.Zero, ge = Vec3.Zero;
                    for (int a = 0; a < 6; a++)
                    {
                        var p = m.P(m.Tris[6 * t + a]);
                        x = x + p * Nt[a]; gx = gx + p * dxi[a]; ge = ge + p * deta[a];
                    }
                    var cr = gx.Cross(ge);
                    var dA = cr.Length * 0.5 * q[2];
                    var n = cr.Normalized();
                    if (n.Dot(outward) < 0) n = -n;
                    var tr = traction(x, n) * dA;
                    total = total + tr;
                    for (int a = 0; a < 6; a++)
                    {
                        int node = m.Tris[6 * t + a];
                        f[3 * node] += tr.X * Nt[a]; f[3 * node + 1] += tr.Y * Nt[a]; f[3 * node + 2] += tr.Z * Nt[a];
                    }
                }
            }
            return total;
        }

        internal static Vec3 ApplyLoad(FeMesh m, FeMaterial mat, FeLoad load, double[] f)
        {
            var faces = m.Faces.Where(x => load.Faces.Contains(x.Id)).ToList();
            // 按曲面实际积分的面积（与下面施加面力时的积分一致），保证合力正好等于设定值
            double area = SurfaceIntegrateScalar(m, load.Faces, x => 1);
            switch (load.Kind)
            {
                case LoadKind.Force:
                    if (area <= 0) return Vec3.Zero;
                    if (load.AlongNormal) return SurfaceIntegrate(m, load.Faces, f, (x, n) => n * (-load.Value / area));
                    var d = load.Dir.Normalized();
                    return SurfaceIntegrate(m, load.Faces, f, (x, n) => d * (load.Value / area));
                case LoadKind.Pressure:
                    return SurfaceIntegrate(m, load.Faces, f, (x, n) => n * (-load.Value));
                case LoadKind.Torque:
                {
                    // 轴：方向 a，过所选面的形心 c。极惯性矩 Jp = ∫ r⊥² dA，面力 t = T/Jp · (a × r⊥)，合力矩 = T·a
                    var a = load.Dir.Normalized();
                    if (area <= 0) return Vec3.Zero;
                    // 形心要和下面施加面力用同一种（曲面）积分算：否则形心偏一点，∫ r⊥ dA ≠ 0，扭矩会带出一个假的合力
                    var c = new Vec3(SurfaceIntegrateScalar(m, load.Faces, x => x.X), SurfaceIntegrateScalar(m, load.Faces, x => x.Y), SurfaceIntegrateScalar(m, load.Faces, x => x.Z)) * (1 / area);
                    var jp = SurfaceIntegrateScalar(m, load.Faces, x => { var r = x - c; var rp = r - a * r.Dot(a); return rp.Dot(rp); });
                    if (jp <= 0) return Vec3.Zero;
                    SurfaceIntegrate(m, load.Faces, f, (x, n) => { var r = x - c; var rp = r - a * r.Dot(a); return a.Cross(rp) * (load.Value / jp); });
                    return Vec3.Zero;   // 纯扭矩，合力为 0
                }
                case LoadKind.Gravity:
                {
                    var b = load.Dir.Normalized() * (mat.Density * 9.80665 * load.Value);
                    var N = new double[10]; var dN = new double[10, 3]; var dNdx = new double[10, 3];
                    var total = Vec3.Zero;
                    for (int e = 0; e < m.Elements; e++)
                        foreach (var g in Gauss)
                        {
                            var w = Gradients(m, e, g, N, dN, dNdx) * GW;
                            for (int k = 0; k < 10; k++)
                            {
                                int n = m.Tets[10 * e + k];
                                f[3 * n] += b.X * N[k] * w; f[3 * n + 1] += b.Y * N[k] * w; f[3 * n + 2] += b.Z * N[k] * w;
                            }
                            total = total + b * w;
                        }
                    return total;
                }
            }
            return Vec3.Zero;
        }

        /// <summary>标量面积分（借用 SurfaceIntegrate：x 分量的"合力"就是积分值）。</summary>
        static double SurfaceIntegrateScalar(FeMesh m, IEnumerable<int> faces, Func<Vec3, double> g)
        {
            var dummy = new double[m.X.Length];
            return SurfaceIntegrate(m, faces, dummy, (x, n) => new Vec3(g(x), 0, 0)).X;
        }

        // ================= 稀疏矩阵和预条件共轭梯度 =================

        internal sealed class Csr
        {
            public int N; public int[] RowPtr, Cols; public double[] Vals;
            /// <summary>按节点 3×3 分块（每个节点的 3 行列结构相同、列成组出现）：乘法时 3 行一起算，列号只读一次。</summary>
            public bool Block3;
            public void Mul(double[] x, double[] y)
            {
                if (Block3) { Mul3(x, y); return; }
                Parallel.For(0, (N + 4095) / 4096, blk =>
                {
                    int r0 = blk * 4096, r1 = Math.Min(N, r0 + 4096);
                    for (int r = r0; r < r1; r++)
                    {
                        double s = 0;
                        for (int p = RowPtr[r]; p < RowPtr[r + 1]; p++) s += Vals[p] * x[Cols[p]];
                        y[r] = s;
                    }
                });
            }

            void Mul3(double[] x, double[] y)
            {
                int nn = N / 3;
                Parallel.For(0, (nn + 1023) / 1024, blk =>
                {
                    int i0 = blk * 1024, i1 = Math.Min(nn, i0 + 1024);
                    var V = Vals; var C = Cols;
                    for (int i = i0; i < i1; i++)
                    {
                        int p0 = RowPtr[3 * i], p1 = RowPtr[3 * i + 1], p2 = RowPtr[3 * i + 2], len = p1 - p0;
                        double s0 = 0, s1 = 0, s2 = 0;
                        for (int k = 0; k < len; k += 3)
                        {
                            int j = C[p0 + k];
                            double x0 = x[j], x1 = x[j + 1], x2 = x[j + 2];
                            s0 += V[p0 + k] * x0 + V[p0 + k + 1] * x1 + V[p0 + k + 2] * x2;
                            s1 += V[p1 + k] * x0 + V[p1 + k + 1] * x1 + V[p1 + k + 2] * x2;
                            s2 += V[p2 + k] * x0 + V[p2 + k + 1] * x1 + V[p2 + k + 2] * x2;
                        }
                        y[3 * i] = s0; y[3 * i + 1] = s1; y[3 * i + 2] = s2;
                    }
                });
            }
        }

        /// <summary>不完全 Cholesky（零填充）：只在 A 的下三角非零位置上分解，L 按行存。主元非正时加对角偏移重来。</summary>
        sealed class Ic0
        {
            int n; int[] rp, ci; double[] lv; double[] diag;

            public static Ic0 Build(Csr A)
            {
                for (double shift = 0; shift < 0.5; shift = shift == 0 ? 1e-4 : shift * 4)
                {
                    var ic = TryBuild(A, shift);
                    if (ic != null) return ic;
                }
                return null;
            }

            static Ic0 TryBuild(Csr A, double shift)
            {
                int n = A.N;
                var rp = new int[n + 1];
                for (int r = 0; r < n; r++)
                {
                    int c = 0;
                    for (int p = A.RowPtr[r]; p < A.RowPtr[r + 1]; p++) if (A.Cols[p] < r) c++;
                    rp[r + 1] = rp[r] + c;
                }
                var ci = new int[rp[n]]; var lv = new double[rp[n]]; var diag = new double[n];
                for (int r = 0; r < n; r++)
                {
                    int q = rp[r];
                    double dRow = 0;
                    for (int p = A.RowPtr[r]; p < A.RowPtr[r + 1]; p++)
                    {
                        if (A.Cols[p] < r) { ci[q] = A.Cols[p]; lv[q] = A.Vals[p]; q++; }
                        else if (A.Cols[p] == r) dRow = A.Vals[p];
                    }
                    // L_rk = (A_rk − Σ_{j<k} L_rj L_kj) / L_kk
                    for (int a = rp[r]; a < rp[r + 1]; a++)
                    {
                        int k = ci[a];
                        double s = lv[a];
                        int i1 = rp[r], i2 = rp[k];
                        while (i1 < a && i2 < rp[k + 1])
                        {
                            if (ci[i1] == ci[i2]) { s -= lv[i1] * lv[i2]; i1++; i2++; }
                            else if (ci[i1] < ci[i2]) i1++; else i2++;
                        }
                        lv[a] = s / diag[k];
                    }
                    double d = dRow * (1 + shift);
                    for (int a = rp[r]; a < rp[r + 1]; a++) d -= lv[a] * lv[a];
                    if (d <= 1e-14 * Math.Abs(dRow) || double.IsNaN(d)) return null;
                    diag[r] = Math.Sqrt(d);
                }
                return new Ic0 { n = n, rp = rp, ci = ci, lv = lv, diag = diag };
            }

            /// <summary>z = (L Lᵀ)⁻¹ r</summary>
            public void Apply(double[] r, double[] z)
            {
                var y = z;
                for (int i = 0; i < n; i++)
                {
                    double s = r[i];
                    for (int p = rp[i]; p < rp[i + 1]; p++) s -= lv[p] * y[ci[p]];
                    y[i] = s / diag[i];
                }
                for (int i = n - 1; i >= 0; i--)
                {
                    y[i] /= diag[i];
                    var yi = y[i];
                    for (int p = rp[i]; p < rp[i + 1]; p++) y[ci[p]] -= lv[p] * yi;
                }
            }
        }

        /// <summary>预条件共轭梯度。x 是初值（可以是上一次的解，拓扑优化时用来加速）。</summary>
        internal static bool Pcg(Csr A, double[] b, double[] x, double tol, int maxIter, out int iters, Action<string> log, CancellationToken ct, IPrecond pre = null)
        {
            int n = A.N;
            var tp = Stopwatch.StartNew();
            var ic = pre == null ? Ic0.Build(A) : null;
            LastPrecondSeconds = tp.Elapsed.TotalSeconds;
            double[] invDiag = null;
            if (pre == null && ic == null)
            {
                invDiag = new double[n];
                for (int r = 0; r < n; r++) for (int k = A.RowPtr[r]; k < A.RowPtr[r + 1]; k++) if (A.Cols[k] == r) invDiag[r] = 1 / A.Vals[k];
            }
            void Precond(double[] r, double[] z) { if (pre != null) pre.Apply(r, z); else if (ic != null) ic.Apply(r, z); else for (int i = 0; i < n; i++) z[i] = r[i] * invDiag[i]; }

            var r0 = (double[])b.Clone();
            var z = new double[n]; var p = new double[n]; var q = new double[n];
            double bn = Math.Sqrt(Dot(b, b));
            if (bn == 0) { Array.Clear(x, 0, n); iters = 0; return true; }
            if (x.Any(v => v != 0))
            {
                A.Mul(x, q);
                for (int i = 0; i < n; i++) r0[i] -= q[i];
            }
            if (Math.Sqrt(Dot(r0, r0)) <= tol * bn) { iters = 0; return true; }
            Precond(r0, z);
            Array.Copy(z, p, n);
            double rz = Dot(r0, z);
            var t = Stopwatch.StartNew();
            for (iters = 1; iters <= maxIter; iters++)
            {
                if ((iters & 63) == 0) ct.ThrowIfCancellationRequested();
                A.Mul(p, q);
                double pq = Dot(p, q);
                if (!(pq > 0) || double.IsNaN(rz)) return false;   // 预条件器不正定或数值出问题：交给调用方换方法
                double alpha = rz / pq;
                for (int i = 0; i < n; i++) { x[i] += alpha * p[i]; r0[i] -= alpha * q[i]; }
                double rn = Math.Sqrt(Dot(r0, r0));
                if (rn <= tol * bn) return true;
                if (t.ElapsedMilliseconds > 1500) { log?.Invoke($"正在求解：第 {iters} 次迭代，残差 {rn / bn:0.0e0}"); t.Restart(); }
                Precond(r0, z);
                double rzNew = Dot(r0, z);
                double beta = rzNew / rz;
                rz = rzNew;
                for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
            }
            return false;
        }

        [ThreadStatic] internal static double LastPrecondSeconds;
        /// <summary>是否用代数多重网格预条件（对比测试时可以关掉：环境变量 SWKIT_NO_AMG=1）。</summary>
        internal static bool UseAmg = Environment.GetEnvironmentVariable("SWKIT_NO_AMG") != "1";
        /// <summary>迭代收敛标准（相对残差）。1e-8 远小于离散误差（百分之一量级），再小只是多花时间。</summary>
        internal const double Tol = 1e-8;

        internal static double Dot(double[] a, double[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }
    }
}
