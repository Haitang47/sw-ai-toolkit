using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwKit.Core.Urdf;

namespace SwKit.Core.Fea
{
    /// <summary>Auto：薄板自动用板件模式；Solid：三维（可以挖出不穿透的槽）；Plate：板件，只开沿板厚方向的穿透孔（激光 / 水刀 / CNC 切板）。</summary>
    public enum TopoMode { Auto, Solid, Plate }

    public sealed class TopoSettings
    {
        public double VolumeFraction = 0.4;     // 保留的材料比例
        public double FilterFactor = 2.0;       // 过滤半径 = 系数 × 平均单元边长（越大结构越粗壮、细节越少）
        public int MaxIterations = 80;
        public double Penalty = 3;              // SIMP 惩罚指数
        public double Move = 0.2;               // 每轮密度最大变化
        public double EMin = 1e-3;              // 空单元的相对刚度（太小迭代求解器难收敛）
        public bool KeepBoundary = true;        // 固定面、受载面附近的单元保持实体（很大的面除外，见 Run）
        public TopoMode Mode = TopoMode.Auto;
    }

    public sealed class TopoResult
    {
        public double[] Density;                // 每个单元的密度（0–1，过滤后的「物理密度」）
        public double[] NodeDensity;            // 每个节点（角点）的密度
        public double Compliance0, Compliance;  // 实心零件、优化后的柔度（越小越刚）
        public double VolumeFraction;
        public double TargetFraction;
        public double PassiveFraction;          // 必须保留（不参与优化）的材料占比
        public bool PlateMode;
        public Vec3 PlateNormal;
        public List<int> IgnoredKeepFaces = new List<int>();   // 太大、没有强制保留附近材料的约束 / 载荷面
        public List<string> Warnings = new List<string>();     // 结果可能不对、需要用户处理的问题
        public int Iterations;
        public List<double> History = new List<double>();
        public float[] Surface;                 // 优化后的外形（三角形，零件坐标，米）
        public double Seconds;
        /// <summary>优化后保留的刚度（相对实心零件）= 实心柔度 / 优化后柔度。</summary>
        public double StiffnessRatio => Compliance > 0 ? Compliance0 / Compliance : 0;
        /// <summary>没能减到目标比例（通常是必须保留的区域太大），结果基本没有意义。</summary>
        public bool Ineffective => VolumeFraction > TargetFraction + 0.1;
    }

    /// <summary>薄板判断：最大的一组平行平面的法向、板厚、面内尺寸。</summary>
    public sealed class PlateInfo
    {
        public Vec3 Normal;
        public double Thickness;    // 最大的一对平行平面之间的距离（米）
        public double Extent;       // 沿法向的总高度（含凸台、加强筋）
        public double InPlane;      // 面内较短的那个尺寸
        public bool IsPlate => Thickness > 0 && Extent < 0.15 * InPlane;
        public override string ToString() => $"板厚 {Thickness * 1000:0.##} mm，面内 {InPlane * 1000:0} mm，法向 {Normal}";
    }

    /// <summary>
    /// 拓扑优化：在给定的约束和载荷下，只保留指定比例的材料，让零件尽量刚（柔度最小）。
    /// 方法是业界和学术界最常用的 SIMP（Bendsøe 1989；Sigmund 2001 的 99 行程序；Andreassen 2011 的 88 行程序）：
    /// 单元刚度 E(ρ) = E·(Emin + (1−Emin)·ρ^p)，密度过滤（Bruns &amp; Tortorelli 2001，按体积加权，适合非结构网格），最优准则法（OC）更新。
    /// 有限元部分直接用静力分析的二次四面体单元和求解器（单元刚度只算一次，每轮按密度缩放组装）。
    /// </summary>
    public static class TopoOpt
    {
        /// <summary>
        /// 拓扑优化用的网格尺寸：让单元数大约为 target（线性四面体，6 万个左右既有细节又能在 1 分钟左右算完）。
        /// volume：零件体积（米³），可以用应力分析网格算出来。
        /// </summary>
        public static double SuggestMeshSize(double volume, int target = 60000) => Math.Pow(0.73 * volume / (0.1179 * target), 1.0 / 3);

        /// <summary>
        /// 拓扑优化的网格尺寸：按目标单元数算，薄板再限制到板厚的 2/3 以下（板厚方向至少两层单元，
        /// 线性四面体太少层时弯曲会偏刚，结果偏向面内受力）。
        /// </summary>
        public static double SuggestMeshSize(FeMesh analysisMesh, int target, out PlateInfo plate)
        {
            plate = DetectPlate(analysisMesh);
            var h = SuggestMeshSize(analysisMesh.Volume(), target);
            if (plate.IsPlate) h = Math.Min(h, plate.Thickness / 1.5);
            return h;
        }

        /// <summary>
        /// 拓扑优化用的网格：单元数控制在 target 附近。圆孔、圆角只做轻度加密（每圈 6 个单元，最小尺寸为一半）——
        /// 应力分析要的是孔边应力，拓扑优化只要形状，孔多的零件（例如有几十个螺丝孔的机架板）按应力分析的加密方式会多出几倍单元。
        /// 划完单元数仍超过目标 1.5 倍时，按比例放大尺寸重划一次（薄板不超过板厚的 2/3）。
        /// </summary>
        public static FeMesh MeshFor(string step, FeMesh analysisMesh, int target, Action<string> log, out PlateInfo plate)
        {
            var h = SuggestMeshSize(analysisMesh, target, out plate);
            log?.Invoke($"拓扑优化：正在划分网格（尺寸 {h * 1000:0.##} mm）");
            var m = Gmsh.Mesh(step, h, log, minSize: h / 2, curvature: 6);
            if (m.Elements > 1.5 * target)
            {
                double h2 = h * Math.Pow(m.Elements / (double)target, 1.0 / 3);
                if (plate.IsPlate) h2 = Math.Min(h2, plate.Thickness / 1.5);
                if (h2 > h * 1.1)
                {
                    log?.Invoke($"单元偏多（{m.Elements:N0}），网格尺寸放大到 {h2 * 1000:0.##} mm 重划");
                    m = Gmsh.Mesh(step, h2, log, minSize: h2 / 2, curvature: 6);
                }
            }
            return m;
        }

        /// <summary>找面积最大的一组平行平面，判断是不是薄板（板厚 / 面内尺寸 &lt; 0.15）。</summary>
        public static PlateInfo DetectPlate(FeMesh m)
        {
            var info = new PlateInfo();
            var planes = m.Faces.Where(f => f.IsPlane && f.Area > 0).ToList();
            if (planes.Count < 2) return info;
            // 按法向分组（正反算同一组），取总面积最大的方向
            var groups = new List<(Vec3 n, double area)>();
            foreach (var f in planes.OrderByDescending(f => f.Area))
            {
                int k = groups.FindIndex(g => Math.Abs(g.n.Dot(f.Normal)) > 0.995);
                if (k < 0) groups.Add((f.Normal.Normalized(), f.Area)); else groups[k] = (groups[k].n, groups[k].area + f.Area);
            }
            var n = groups.OrderByDescending(g => g.area).First().n;
            // 这一组里面积最大的两个反向平面之间的距离 = 板厚
            var pos = planes.Where(f => f.Normal.Dot(n) > 0.995).OrderByDescending(f => f.Area).FirstOrDefault();
            var neg = planes.Where(f => f.Normal.Dot(n) < -0.995).OrderByDescending(f => f.Area).FirstOrDefault();
            // 面内两个方向
            var u = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            u = (u - n * u.Dot(n)).Normalized(); var v = n.Cross(u);
            double nMin = double.MaxValue, nMax = double.MinValue, uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
            for (int i = 0; i < m.Nodes; i++)
            {
                var p = m.P(i);
                double a = p.Dot(n), b = p.Dot(u), c = p.Dot(v);
                if (a < nMin) nMin = a; if (a > nMax) nMax = a;
                if (b < uMin) uMin = b; if (b > uMax) uMax = b;
                if (c < vMin) vMin = c; if (c > vMax) vMax = c;
            }
            info.Normal = n;
            info.Extent = nMax - nMin;
            info.InPlane = Math.Min(uMax - uMin, vMax - vMin);
            info.Thickness = pos != null && neg != null ? Math.Abs(pos.Centroid.Dot(n) - neg.Centroid.Dot(n)) : info.Extent;
            if (info.Thickness < 1e-6) info.Thickness = info.Extent;
            return info;
        }

        public static TopoResult Run(FeModel model, TopoSettings s, Action<string> log, Action<int, TopoResult> iterDone, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var m = model.Mesh;
            int nn = m.Nodes, ne = m.Elements;
            var D = FeSolver.Elasticity(model.Material);
            if (model.FixedFaces.Count == 0 || model.Loads.Count == 0) throw new InvalidOperationException("拓扑优化需要先设置固定面和载荷");
            var res = new TopoResult { TargetFraction = s.VolumeFraction };

            // ---- 只用四面体的 4 个角点（线性单元）：自由度少 7 倍左右，网格可以细得多，形状细节更好 ----
            log?.Invoke("正在准备拓扑优化……");
            var corner = new int[nn];
            for (int i = 0; i < nn; i++) corner[i] = -1;
            int nc = 0;
            var tet = new int[4 * ne];
            for (int e = 0; e < ne; e++)
                for (int a = 0; a < 4; a++)
                {
                    int n = m.Tets[10 * e + a];
                    if (corner[n] < 0) corner[n] = nc++;
                    tet[4 * e + a] = corner[n];
                }
            int nd = 3 * nc;
            var xc = new double[3 * nc];
            for (int n = 0; n < nn; n++) if (corner[n] >= 0) { xc[3 * corner[n]] = m.X[3 * n]; xc[3 * corner[n] + 1] = m.X[3 * n + 1]; xc[3 * corner[n] + 2] = m.X[3 * n + 2]; }
            Vec3 PC(int c) => new Vec3(xc[3 * c], xc[3 * c + 1], xc[3 * c + 2]);

            // 稀疏结构
            var nbr = new List<int>[nc];
            {
                var sets = new HashSet<int>[nc];
                for (int i = 0; i < nc; i++) sets[i] = new HashSet<int>();
                for (int e = 0; e < ne; e++) for (int a = 0; a < 4; a++) for (int b = 0; b < 4; b++) sets[tet[4 * e + a]].Add(tet[4 * e + b]);
                for (int i = 0; i < nc; i++) { nbr[i] = sets[i].ToList(); nbr[i].Sort(); }
            }
            var rowPtr = new int[nd + 1];
            for (int i = 0; i < nc; i++) for (int c = 0; c < 3; c++) rowPtr[3 * i + c + 1] = rowPtr[3 * i + c] + 3 * nbr[i].Count;
            var cols = new int[rowPtr[nd]];
            for (int i = 0; i < nc; i++)
                for (int c = 0; c < 3; c++) { int p = rowPtr[3 * i + c]; foreach (var j in nbr[i]) for (int d = 0; d < 3; d++) cols[p++] = 3 * j + d; }
            var epos = new int[16 * ne];
            for (int e = 0; e < ne; e++) for (int a = 0; a < 4; a++) for (int b = 0; b < 4; b++) epos[16 * e + 4 * a + b] = nbr[tet[4 * e + a]].BinarySearch(tet[4 * e + b]);
            nbr = null;

            // 单元刚度（常应变四面体，12×12）、体积、形心
            var ke = new float[144L * ne];
            var vol = new double[ne];
            var cen = new Vec3[ne];
            Parallel.For(0, ne, e =>
            {
                var p0 = PC(tet[4 * e]); var p1 = PC(tet[4 * e + 1]); var p2 = PC(tet[4 * e + 2]); var p3 = PC(tet[4 * e + 3]);
                cen[e] = (p0 + p1 + p2 + p3) / 4;
                var a = p1 - p0; var b = p2 - p0; var c = p3 - p0;
                double det = a.Dot(b.Cross(c));
                double sign = det < 0 ? -1 : 1;
                vol[e] = Math.Abs(det) / 6;
                // 线性形函数的梯度：∇N1 = (b×c)/det，∇N2 = (c×a)/det，∇N3 = (a×b)/det，∇N0 = −(∇N1+∇N2+∇N3)
                var g1 = b.Cross(c) / det; var g2 = c.Cross(a) / det; var g3 = a.Cross(b) / det; var g0 = -(g1 + g2 + g3);
                var g = new[] { g0, g1, g2, g3 };
                var B = new double[6, 12];
                for (int k = 0; k < 4; k++)
                {
                    double x = g[k].X, y = g[k].Y, z = g[k].Z; int o = 3 * k;
                    B[0, o] = x; B[1, o + 1] = y; B[2, o + 2] = z;
                    B[3, o] = y; B[3, o + 1] = x; B[4, o + 1] = z; B[4, o + 2] = y; B[5, o] = z; B[5, o + 2] = x;
                }
                var DB = new double[6, 12];
                for (int i = 0; i < 6; i++) for (int k = 0; k < 12; k++) { double sum = 0; for (int j = 0; j < 6; j++) sum += D[i, j] * B[j, k]; DB[i, k] = sum; }
                for (int r = 0; r < 12; r++)
                    for (int cc = 0; cc < 12; cc++) { double sum = 0; for (int i = 0; i < 6; i++) sum += B[i, r] * DB[i, cc]; ke[144L * e + 12 * r + cc] = (float)(sum * vol[e]); }
            });

            // ---- 载荷：在二次网格上按面积分，再把边中点上的力平分给两端角点（等价于线性三角形的一致载荷）----
            var f10 = new double[3 * nn];
            foreach (var load in model.Loads) FeSolver.ApplyLoad(m, model.Material, load, f10);
            var f0 = new double[nd];
            int[,] edges = { { 0, 1 }, { 1, 2 }, { 2, 0 }, { 3, 0 }, { 3, 2 }, { 3, 1 } };
            var midDone = new bool[nn];
            for (int e = 0; e < ne; e++)
                for (int k = 0; k < 6; k++)
                {
                    int mid = m.Tets[10 * e + 4 + k];
                    if (midDone[mid]) continue;
                    midDone[mid] = true;
                    int ca = corner[m.Tets[10 * e + edges[k, 0]]], cb = corner[m.Tets[10 * e + edges[k, 1]]];
                    for (int d = 0; d < 3; d++) { f0[3 * ca + d] += f10[3 * mid + d] / 2; f0[3 * cb + d] += f10[3 * mid + d] / 2; }
                }
            for (int n = 0; n < nn; n++) if (corner[n] >= 0) for (int d = 0; d < 3; d++) f0[3 * corner[n] + d] += f10[3 * n + d];
            var fixedNodes = m.FaceNodes(model.FixedFaces);
            var isFixed = new bool[nd];
            foreach (var n in fixedNodes) if (corner[n] >= 0) for (int d = 0; d < 3; d++) isFixed[3 * corner[n] + d] = true;

            double edgeSum = 0;
            for (int e = 0; e < ne; e++)
            {
                var p0 = PC(tet[4 * e]); var p1 = PC(tet[4 * e + 1]); var p2 = PC(tet[4 * e + 2]); var p3 = PC(tet[4 * e + 3]);
                edgeSum += ((p0 - p1).Length + (p0 - p2).Length + (p0 - p3).Length + (p1 - p2).Length + (p1 - p3).Length + (p2 - p3).Length) / 6;
            }
            double hAvg = edgeSum / ne, rmin = s.FilterFactor * hAvg;
            double vTotal = vol.Sum();
            double vTarget = s.VolumeFraction * vTotal;
            double surfTotal = m.Faces.Sum(f => f.Area);
            string FaceText(IEnumerable<int> ids) => string.Join("、", ids.Take(4)) + (ids.Count() > 4 ? " 等" : "");

            // ---- 很大的约束面 / 受载面：通常是选错了面（例如把力加在整块板的上表面），提醒用户 ----
            var loadFaceIds = model.Loads.Where(l => l.Kind != LoadKind.Gravity).SelectMany(l => l.Faces).Distinct().ToList();
            var bigLoad = m.Faces.Where(f => loadFaceIds.Contains(f.Id) && f.Area > 0.2 * surfTotal).ToList();
            var bigFix = m.Faces.Where(f => model.FixedFaces.Contains(f.Id) && f.Area > 0.2 * surfTotal).ToList();
            if (bigLoad.Count > 0)
                res.Warnings.Add($"载荷加在了很大的面上（面 {FaceText(bigLoad.Select(f => f.Id))}，占零件表面 {bigLoad.Sum(f => f.Area) / surfTotal:P0}）：力会平均摊到整个面上。" +
                                 "如果力实际是从电机、螺栓、轴承传进来的，应该只选那几个安装孔（孔的圆柱面）或安装座的小平面。");
            if (bigFix.Count > 0)
                res.Warnings.Add($"固定了很大的面（面 {FaceText(bigFix.Select(f => f.Id))}，占零件表面 {bigFix.Sum(f => f.Area) / surfTotal:P0}）：整个面都不能动，力直接传到这个面上，零件内部几乎不需要传力路径。" +
                                 "通常应该只固定安装孔或安装面。");

            // ---- 不参与优化的单元（保持实体）：碰到固定面或受载面的单元。
            //      很大的面不算（整块板的上表面会让所有单元都变成「必须保留」，优化就没有意义了）：
            //      按每个面带来的保留体积从小到大加入，超过目标体积的一半就停 ----
            var passive = new bool[ne];
            if (s.KeepBoundary)
            {
                var bcFaces = model.FixedFaces.Concat(loadFaceIds).Distinct().ToList();
                var nodeFaces = new Dictionary<int, List<int>>();
                foreach (var fid in bcFaces)
                    foreach (var n in m.FaceNodes(new[] { fid }))
                    {
                        if (!nodeFaces.TryGetValue(n, out var lst)) nodeFaces[n] = lst = new List<int>();
                        lst.Add(fid);
                    }
                var faceElems = bcFaces.ToDictionary(f => f, f => new HashSet<int>());
                for (int e = 0; e < ne; e++)
                    for (int a = 0; a < 4; a++)
                        if (nodeFaces.TryGetValue(m.Tets[10 * e + a], out var fl)) foreach (var fid in fl) faceElems[fid].Add(e);
                double vKeep = 0;
                foreach (var kv in faceElems.OrderBy(kv => kv.Value.Sum(e => vol[e])))
                {
                    double add = kv.Value.Where(e => !passive[e]).Sum(e => vol[e]);
                    if (kv.Value.Sum(e => vol[e]) > 0.25 * vTotal || vKeep + add > 0.5 * vTarget) { res.IgnoredKeepFaces.Add(kv.Key); continue; }
                    foreach (var e in kv.Value) passive[e] = true;
                    vKeep += add;
                }
                if (res.IgnoredKeepFaces.Count > 0)
                    log?.Invoke($"面 {FaceText(res.IgnoredKeepFaces)} 太大，附近的材料不强制保留（只保留小的安装孔、安装面附近的材料）");
            }

            // ---- 设计变量：三维模式每个单元一个；板件模式沿板厚方向的一列单元共用一个（只会开穿透孔，能直接切板）----
            PlateInfo plate = s.Mode == TopoMode.Solid ? null : DetectPlate(m);
            bool plateMode = plate != null && (s.Mode == TopoMode.Plate || plate.IsPlate);
            res.PlateMode = plateMode;
            int nCell;
            var cellOf = new int[ne];
            Vec3[] cellPos; double[] cellVol;
            if (!plateMode)
            {
                nCell = ne;
                for (int e = 0; e < ne; e++) cellOf[e] = e;
                cellPos = cen; cellVol = vol;
            }
            else
            {
                var n = plate.Normal;
                res.PlateNormal = n;
                var pu = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
                pu = (pu - n * pu.Dot(n)).Normalized(); var pv = n.Cross(pu);
                double cs = hAvg;
                var map = new Dictionary<(int, int), int>();
                var pos = new List<Vec3>(); var cv = new List<double>();
                for (int e = 0; e < ne; e++)
                {
                    var p = cen[e];
                    var key = ((int)Math.Floor(p.Dot(pu) / cs), (int)Math.Floor(p.Dot(pv) / cs));
                    if (!map.TryGetValue(key, out var k)) { k = map[key] = pos.Count; pos.Add(Vec3.Zero); cv.Add(0); }
                    cellOf[e] = k;
                    pos[k] = pos[k] + (p - n * p.Dot(n)) * vol[e];
                    cv[k] += vol[e];
                }
                nCell = pos.Count;
                cellVol = cv.ToArray();
                cellPos = new Vec3[nCell];
                for (int k = 0; k < nCell; k++) cellPos[k] = pos[k] / cellVol[k];
                log?.Invoke($"薄板（{plate}）：按板件优化，只开穿透孔；{nCell:N0} 列");
            }
            var cellPassive = new bool[nCell];
            for (int e = 0; e < ne; e++) if (passive[e]) cellPassive[cellOf[e]] = true;
            double vPassive = 0; for (int k = 0; k < nCell; k++) if (cellPassive[k]) vPassive += cellVol[k];
            res.PassiveFraction = vPassive / vTotal;
            if (vPassive > vTarget * 0.9)
                res.Warnings.Add($"约束面和受载面附近必须保留的材料已经占 {vPassive / vTotal:P0}，接近或超过目标的 {s.VolumeFraction:P0}，优化几乎没有可去掉的材料。把保留比例调大，或者只选安装孔、安装面这些小面。");
            log?.Invoke($"必须保留的材料占 {vPassive / vTotal:P1}");

            // ---- 密度过滤器：形心距离小于 rmin 的设计变量，权重 (rmin − d)·V_j（板件模式按面内距离）----
            log?.Invoke($"正在建立过滤器（{nCell:N0} 个设计变量，过滤半径 {rmin * 1000:0.##} mm）……");
            var fPtr = new int[nCell + 1];
            var fIdxL = new List<int>(nCell * 48); var fWL = new List<float>(nCell * 48);
            var wSum = new double[nCell];
            {
                var grid = new Dictionary<(int, int, int), List<int>>();
                (int, int, int) Cell(Vec3 p) => ((int)Math.Floor(p.X / rmin), (int)Math.Floor(p.Y / rmin), (int)Math.Floor(p.Z / rmin));
                for (int k = 0; k < nCell; k++)
                {
                    var c = Cell(cellPos[k]);
                    if (!grid.TryGetValue(c, out var list)) grid[c] = list = new List<int>();
                    list.Add(k);
                }
                for (int k = 0; k < nCell; k++)
                {
                    var (cx, cy, cz) = Cell(cellPos[k]);
                    for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                        foreach (var j in list)
                        {
                            var d = (cellPos[k] - cellPos[j]).Length;
                            if (d >= rmin) continue;
                            var w = (rmin - d) * cellVol[j];
                            fIdxL.Add(j); fWL.Add((float)w); wSum[k] += w;
                        }
                    }
                    fPtr[k + 1] = fIdxL.Count;
                }
            }
            var fIdx = fIdxL.ToArray(); var fW = fWL.ToArray();
            fIdxL = null; fWL = null;

            double[] Filter(double[] x)
            {
                var y = new double[nCell];
                Parallel.For(0, nCell, k =>
                {
                    double sum = 0;
                    for (int p = fPtr[k]; p < fPtr[k + 1]; p++) sum += fW[p] * x[fIdx[p]];
                    y[k] = sum / wSum[k];
                });
                return y;
            }
            // Heaviside 投影（Wang, Lazarov &amp; Sigmund 2011）：把过滤后的灰色密度推向 0 / 1，β 逐步加大
            const double Eta = 0.5;
            double beta = 1;
            double Proj(double r) => (Math.Tanh(beta * Eta) + Math.Tanh(beta * (r - Eta))) / (Math.Tanh(beta * Eta) + Math.Tanh(beta * (1 - Eta)));
            double DProj(double r) { var t = Math.Tanh(beta * (r - Eta)); return beta * (1 - t * t) / (Math.Tanh(beta * Eta) + Math.Tanh(beta * (1 - Eta))); }
            double[] Physical(double[] xt) { var y = new double[nCell]; for (int k = 0; k < nCell; k++) y[k] = cellPassive[k] ? 1 : Proj(xt[k]); return y; }
            double[] ToElements(double[] rc) { if (!plateMode) return rc; var y = new double[ne]; for (int e = 0; e < ne; e++) y[e] = rc[cellOf[e]]; return y; }

            // ---- 有限元：按密度组装、求解 ----
            var u = new double[nd];
            Amg amg = null; bool amgFailed = false; long amgIters = 0; int lastIts = 0; string lastSolver = "";
            double SolveFor(double[] rho, double[] ce)
            {
                var vals = new double[cols.Length];
                for (int e = 0; e < ne; e++)
                {
                    double sc = s.EMin + (1 - s.EMin) * Math.Pow(rho[e], s.Penalty);
                    long off = 144L * e;
                    for (int a = 0; a < 4; a++)
                    {
                        int na = tet[4 * e + a];
                        for (int b = 0; b < 4; b++)
                        {
                            int pos = epos[16 * e + 4 * a + b];
                            for (int c = 0; c < 3; c++)
                            {
                                int bi = rowPtr[3 * na + c] + 3 * pos;
                                long kr = off + 12 * (3 * a + c) + 3 * b;
                                vals[bi] += ke[kr] * sc; vals[bi + 1] += ke[kr + 1] * sc; vals[bi + 2] += ke[kr + 2] * sc;
                            }
                        }
                    }
                }
                var f = (double[])f0.Clone();
                for (int r = 0; r < nd; r++)
                {
                    for (int p = rowPtr[r]; p < rowPtr[r + 1]; p++) if (isFixed[r] || isFixed[cols[p]]) vals[p] = cols[p] == r ? 1 : 0;
                    if (isFixed[r]) f[r] = 0;
                }
                var A = new FeSolver.Csr { N = nd, RowPtr = rowPtr, Cols = cols, Vals = vals, Block3 = true };
                // 代数多重网格：第一轮建立层次，之后每轮只更新数值（聚集和试探延拓不变）
                bool ok = false;
                if (FeSolver.UseAmg && !amgFailed)
                {
                    try
                    {
                        if (amg == null) amg = Amg.Build(A, xc, isFixed); else amg.Rebuild(A);
                        ok = FeSolver.Pcg(A, f, u, 1e-7, 400, out var its, null, ct, amg);
                        amgIters += its; lastIts = its; lastSolver = "AMG";
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { ok = false; }
                    if (!ok) { amgFailed = true; amg = null; if (u.Any(v => double.IsNaN(v))) Array.Clear(u, 0, u.Length); log?.Invoke("多重网格没有收敛，改用不完全分解预条件（会慢一些）"); }
                }
                if (!ok) { FeSolver.Pcg(A, f, u, 1e-7, 20000, out var its2, null, ct); lastIts = its2; lastSolver = "IC0"; }
                Parallel.For(0, ne, e =>
                {
                    var ue = new double[12];
                    for (int a = 0; a < 4; a++) { int n = tet[4 * e + a]; ue[3 * a] = u[3 * n]; ue[3 * a + 1] = u[3 * n + 1]; ue[3 * a + 2] = u[3 * n + 2]; }
                    long off = 144L * e;
                    double sum = 0;
                    for (int r = 0; r < 12; r++) { double row = 0; for (int c = 0; c < 12; c++) row += ke[off + 12 * r + c] * ue[c]; sum += ue[r] * row; }
                    ce[e] = sum;
                });
                return FeSolver.Dot(f, u);
            }

            var ce = new double[ne];
            log?.Invoke("正在计算实心零件的刚度（作为对比）……");
            res.Compliance0 = SolveFor(Enumerable.Repeat(1.0, ne).ToArray(), ce);

            var x = new double[nCell];
            double x0 = Math.Max(0.05, Math.Min(1, (vTarget - vPassive) / Math.Max(vTotal - vPassive, 1e-30)));
            for (int k = 0; k < nCell; k++) x[k] = cellPassive[k] ? 1 : x0;
            var xt = Filter(x);
            var rhoPhys = Physical(xt);
            var dcCell = new double[nCell]; var dc = new double[nCell]; var dv = new double[nCell];
            int sinceBeta = 0; double lastC = double.MaxValue;
            for (int it = 1; it <= s.MaxIterations; it++)
            {
                ct.ThrowIfCancellationRequested();
                var rhoE = ToElements(rhoPhys);
                double c = SolveFor(rhoE, ce);
                // 灵敏度：dc/dρ̄（单元）→ 按列求和（板件模式）→ dρ̄/dρ̃（投影）→ 过滤器的链式法则 → dc/dx
                Array.Clear(dcCell, 0, nCell); Array.Clear(dc, 0, nCell); Array.Clear(dv, 0, nCell);
                for (int e = 0; e < ne; e++)
                    dcCell[cellOf[e]] += -s.Penalty * (1 - s.EMin) * Math.Pow(rhoE[e], s.Penalty - 1) * ce[e];
                for (int k = 0; k < nCell; k++)
                {
                    if (cellPassive[k]) continue;
                    double dp = DProj(xt[k]);
                    double dcT = dcCell[k] * dp / wSum[k];
                    double dvT = cellVol[k] * dp / wSum[k];
                    for (int p = fPtr[k]; p < fPtr[k + 1]; p++) { int j = fIdx[p]; dc[j] += fW[p] * dcT; dv[j] += fW[p] * dvT; }
                }
                // 最优准则法：二分找拉格朗日乘子，使投影后的体积等于目标
                double num = 0; int cntS = 0;
                for (int k = 0; k < nCell; k++) if (!cellPassive[k] && dv[k] > 0) { num += -dc[k] / dv[k]; cntS++; }
                double l1 = 0, l2 = Math.Max(1e-30, cntS > 0 ? num / cntS : 1) * 1e4;
                var xNew = new double[nCell]; double[] xtNew = xt, rhoNew = rhoPhys;
                // β 大时投影很陡，同样的 x 变化会让物理密度在 0 / 1 之间来回跳（振荡），移动限随 β 减小
                double move = beta >= 16 ? Math.Min(s.Move, 0.05) : beta >= 8 ? Math.Min(s.Move, 0.1) : s.Move;
                for (int bis = 0; bis < 100 && (l2 - l1) / (l1 + l2 + 1e-300) > 1e-5; bis++)
                {
                    double lm = 0.5 * (l1 + l2);
                    for (int k = 0; k < nCell; k++)
                    {
                        if (cellPassive[k]) { xNew[k] = 1; continue; }
                        double be = dv[k] > 0 ? Math.Max(0, -dc[k]) / (lm * dv[k]) : 0;
                        xNew[k] = Math.Max(0, Math.Max(x[k] - move, Math.Min(1, Math.Min(x[k] + move, x[k] * Math.Sqrt(be)))));
                    }
                    xtNew = Filter(xNew);
                    rhoNew = Physical(xtNew);
                    double v = 0; for (int k = 0; k < nCell; k++) v += rhoNew[k] * cellVol[k];
                    if (v > vTarget) l1 = lm; else l2 = lm;
                }
                double change = 0;
                for (int k = 0; k < nCell; k++) change = Math.Max(change, Math.Abs(xNew[k] - x[k]));
                x = (double[])xNew.Clone(); xt = xtNew; rhoPhys = rhoNew;

                res.History.Add(c);
                res.Iterations = it;
                res.Compliance = c;
                res.Density = ToElements(rhoPhys);
                double vf = 0, gray = 0;
                for (int k = 0; k < nCell; k++) { vf += rhoPhys[k] * cellVol[k]; gray += 4 * rhoPhys[k] * (1 - rhoPhys[k]) * cellVol[k]; }
                res.VolumeFraction = vf / vTotal;
                log?.Invoke($"拓扑优化 第 {it} 轮：保留 {res.VolumeFraction:P0}，刚度为实心的 {res.Compliance0 / c:P0}，灰度 {gray / vTotal:P0}，β={beta:0}，变化 {change:0.000}（{lastSolver} {lastIts} 次迭代，累计 {sw.Elapsed.TotalSeconds:0} 秒）");
                iterDone?.Invoke(it, res);

                // β 加倍的时机：形状基本稳定（或已经过了足够多轮）。
                // 「稳定」：设计变量几乎不变，或者最近 3 轮的柔度起伏小于 0.5%（少量单元来回翻转不影响结果）
                sinceBeta++;
                var h = res.History;
                bool flat = h.Count >= 3 && sinceBeta >= 3 && (h.Skip(h.Count - 3).Max() - h.Skip(h.Count - 3).Min()) / c < 0.005;
                bool stableNow = change < 0.02 || Math.Abs(lastC - c) / c < 0.002 || flat;
                lastC = c;
                if (beta < 16 && (stableNow && sinceBeta >= 6 || sinceBeta >= 18)) { beta *= 2; sinceBeta = 0; rhoPhys = Physical(xt); }
                else if (beta >= 16 && stableNow && sinceBeta >= 4) break;
            }
            var rhoFinal = ToElements(rhoPhys);
            res.Compliance = SolveFor(rhoFinal, ce);
            res.Density = rhoFinal;
            { double vf = 0; for (int e = 0; e < ne; e++) vf += rhoFinal[e] * vol[e]; res.VolumeFraction = vf / vTotal; }
            if (res.Ineffective)
                res.Warnings.Add($"没能减到目标的 {s.VolumeFraction:P0}（现在 {res.VolumeFraction:P0}）：结果基本就是原零件，没有参考价值。请先处理上面的问题。");
            var rhoPhysE = rhoFinal;
            res.NodeDensity = NodeDensity(m, rhoPhysE, vol);
            res.Surface = TopoSurface.Extract(m, res.NodeDensity, 0.5, 6);
            res.Seconds = sw.Elapsed.TotalSeconds;
            return res;
        }

        /// <summary>节点（角点）密度：相邻单元按体积加权平均。</summary>
        public static double[] NodeDensity(FeMesh m, double[] rho, double[] vol)
        {
            int nn = m.Nodes, ne = m.Elements;
            var s = new double[nn]; var w = new double[nn];
            for (int e = 0; e < ne; e++)
                for (int a = 0; a < 4; a++) { int n = m.Tets[10 * e + a]; s[n] += rho[e] * vol[e]; w[n] += vol[e]; }
            for (int n = 0; n < nn; n++) s[n] = w[n] > 0 ? s[n] / w[n] : 0;
            return s;
        }

        /// <summary>中间结果的外形（迭代过程中实时显示用）。</summary>
        public static float[] PreviewSurface(FeMesh m, double[] rho)
        {
            var vol = new double[m.Elements];
            for (int e = 0; e < m.Elements; e++) vol[e] = 1;
            return TopoSurface.Extract(m, NodeDensity(m, rho, vol), 0.5, 2);
        }
    }

    /// <summary>
    /// 从节点密度提取等值面（ρ = 0.5）得到优化后的外形：
    /// 内部用 marching tetrahedra，零件外表面上把三角形裁到 ρ ≥ 0.5 的部分，两者拼成封闭曲面；
    /// 再对内部的点做几轮 Taubin 平滑（不缩水），外表面上的点保持不动。
    /// </summary>
    public static class TopoSurface
    {
        public static float[] Extract(FeMesh m, double[] rho, double iso, int smoothIters)
        {
            var verts = new List<Vec3>();
            var keyIndex = new Dictionary<long, int>();
            var tris = new List<int>();
            long nn = m.Nodes;
            long NodeKey(int n) => n;
            long EdgeKey(int a, int b) { if (a > b) { var t = a; a = b; b = t; } return nn + a * nn + b; }
            int Vertex(long key, Func<Vec3> pos)
            {
                if (keyIndex.TryGetValue(key, out var i)) return i;
                verts.Add(pos());
                return keyIndex[key] = verts.Count - 1;
            }
            int EdgePoint(int a, int b)
            {
                return Vertex(EdgeKey(a, b), () =>
                {
                    double va = rho[a], vb = rho[b];
                    double t = Math.Abs(vb - va) < 1e-12 ? 0.5 : (iso - va) / (vb - va);
                    t = Math.Max(0, Math.Min(1, t));
                    return m.P(a) + (m.P(b) - m.P(a)) * t;
                });
            }
            void AddTri(int i0, int i1, int i2, Vec3 outward)
            {
                var n = (verts[i1] - verts[i0]).Cross(verts[i2] - verts[i0]);
                if (n.Dot(outward) < 0) { tris.Add(i0); tris.Add(i2); tris.Add(i1); }
                else { tris.Add(i0); tris.Add(i1); tris.Add(i2); }
            }

            // ---- 内部：marching tetrahedra ----
            var c = new int[4];
            for (int e = 0; e < m.Elements; e++)
            {
                int inside = 0;
                for (int a = 0; a < 4; a++) { c[a] = m.Tets[10 * e + a]; if (rho[c[a]] >= iso) inside++; }
                if (inside == 0 || inside == 4) continue;
                var ins = c.Where(n => rho[n] >= iso).ToArray();
                var outs = c.Where(n => rho[n] < iso).ToArray();
                var cIn = ins.Aggregate(Vec3.Zero, (s, n) => s + m.P(n)) / ins.Length;
                var cOut = outs.Aggregate(Vec3.Zero, (s, n) => s + m.P(n)) / outs.Length;
                var outward = cOut - cIn;
                if (inside == 1)
                    AddTri(EdgePoint(ins[0], outs[0]), EdgePoint(ins[0], outs[1]), EdgePoint(ins[0], outs[2]), outward);
                else if (inside == 3)
                    AddTri(EdgePoint(outs[0], ins[0]), EdgePoint(outs[0], ins[1]), EdgePoint(outs[0], ins[2]), outward);
                else
                {
                    int p0 = EdgePoint(ins[0], outs[0]), p1 = EdgePoint(ins[0], outs[1]), p2 = EdgePoint(ins[1], outs[1]), p3 = EdgePoint(ins[1], outs[0]);
                    AddTri(p0, p1, p2, outward); AddTri(p0, p2, p3, outward);
                }
            }

            // ---- 外表面：把表面三角形裁到 ρ ≥ iso 的部分 ----
            var surfaceEdges = new HashSet<long>();
            for (int t = 0; t < m.TriFace.Length; t++)
            {
                int a = m.Tris[6 * t], b = m.Tris[6 * t + 1], d = m.Tris[6 * t + 2];
                surfaceEdges.Add(EdgeKey(a, b)); surfaceEdges.Add(EdgeKey(b, d)); surfaceEdges.Add(EdgeKey(d, a));
                var outward = m.TriNormal(t, out _);
                var poly = new List<int>();
                var tri = new[] { a, b, d };
                for (int k = 0; k < 3; k++)
                {
                    int p = tri[k], q = tri[(k + 1) % 3];
                    bool pin = rho[p] >= iso, qin = rho[q] >= iso;
                    if (pin) poly.Add(Vertex(NodeKey(p), () => m.P(p)));
                    if (pin != qin) poly.Add(EdgePoint(p, q));
                }
                for (int k = 1; k + 1 < poly.Count; k++) AddTri(poly[0], poly[k], poly[k + 1], outward);
            }

            // ---- 平滑内部的点（Taubin λ|μ，不会整体缩小）----
            if (smoothIters > 0 && verts.Count > 0)
            {
                var fixedV = new bool[verts.Count];
                foreach (var kv in keyIndex) if (kv.Key < nn || surfaceEdges.Contains(kv.Key)) fixedV[kv.Value] = true;
                var nbr = new HashSet<int>[verts.Count];
                for (int i = 0; i < verts.Count; i++) nbr[i] = new HashSet<int>();
                for (int t = 0; t < tris.Count; t += 3)
                    for (int k = 0; k < 3; k++) { int a = tris[t + k], b = tris[t + (k + 1) % 3]; nbr[a].Add(b); nbr[b].Add(a); }
                var cur = verts.ToArray(); var next = new Vec3[cur.Length];
                for (int it = 0; it < smoothIters * 2; it++)
                {
                    double lam = it % 2 == 0 ? 0.5 : -0.53;
                    for (int i = 0; i < cur.Length; i++)
                    {
                        if (fixedV[i] || nbr[i].Count == 0) { next[i] = cur[i]; continue; }
                        var avg = Vec3.Zero; foreach (var j in nbr[i]) avg = avg + cur[j];
                        avg = avg / nbr[i].Count;
                        next[i] = cur[i] + (avg - cur[i]) * lam;
                    }
                    var tmp = cur; cur = next; next = tmp;
                }
                for (int i = 0; i < cur.Length; i++) verts[i] = cur[i];
            }

            var outArr = new float[tris.Count * 3];
            for (int i = 0; i < tris.Count; i++)
            {
                var p = verts[tris[i]];
                outArr[3 * i] = (float)p.X; outArr[3 * i + 1] = (float)p.Y; outArr[3 * i + 2] = (float)p.Z;
            }
            return outArr;
        }

        /// <summary>写二进制 STL（单位毫米）。</summary>
        public static void WriteStl(float[] tris, string path)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                var header = new byte[80];
                var text = System.Text.Encoding.ASCII.GetBytes("SwKit topology optimization result (mm)");
                Array.Copy(text, header, Math.Min(80, text.Length));
                w.Write(header);
                int n = tris.Length / 9;
                w.Write(n);
                for (int t = 0; t < n; t++)
                {
                    int o = 9 * t;
                    var a = new Vec3(tris[o], tris[o + 1], tris[o + 2]); var b = new Vec3(tris[o + 3], tris[o + 4], tris[o + 5]); var c = new Vec3(tris[o + 6], tris[o + 7], tris[o + 8]);
                    var nrm = (b - a).Cross(c - a).Normalized();
                    w.Write((float)nrm.X); w.Write((float)nrm.Y); w.Write((float)nrm.Z);
                    for (int k = 0; k < 9; k++) w.Write(tris[o + k] * 1000f);
                    w.Write((ushort)0);
                }
            }
        }

        /// <summary>封闭曲面的体积（散度定理），米³。</summary>
        public static double Volume(float[] tris)
        {
            double v = 0;
            for (int o = 0; o + 8 < tris.Length; o += 9)
            {
                var a = new Vec3(tris[o], tris[o + 1], tris[o + 2]); var b = new Vec3(tris[o + 3], tris[o + 4], tris[o + 5]); var c = new Vec3(tris[o + 6], tris[o + 7], tris[o + 8]);
                v += a.Dot(b.Cross(c)) / 6;
            }
            return v;
        }
    }
}
