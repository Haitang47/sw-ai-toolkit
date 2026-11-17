using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SwKit.Core.Fea
{
    /// <summary>
    /// 自适应网格加密（h-adaptive，和 SolidWorks Simulation / ANSYS 的自适应思路一样）：
    /// 1. 用 Zienkiewicz–Zhu 误差估计算出每个单元的误差（恢复应力与单元应力之差的能量范数）；
    /// 2. 按「误差均布」原则算每个单元的新尺寸 h_new = h·(e_允许 / e_单元)^(1/p)，p = 2（二次单元）；
    /// 3. 写成 Gmsh 的背景网格（.pos），重新划分网格再算；
    /// 4. 直到估计误差低于目标、并且最大应力（离开约束面）变化小于 3%，或达到最大轮数。
    /// </summary>
    public static class FeAdaptive
    {
        public static AdaptivePass Pass(FeMesh m, FeResult r) => new AdaptivePass
        {
            Elements = m.Elements, Nodes = m.Nodes, MaxVm = r.MaxVm, MaxVmAway = r.MaxVmAway, MaxP1 = r.MaxP1, Eta = r.ErrorEstimate, Seconds = r.Seconds,
        };

        /// <summary>
        /// 从已有的一次结果开始自适应加密。step：划网格用的几何文件；sizeMax：最大单元尺寸（米）；target：目标误差（例如 0.03）。
        /// onPass：每算完一轮回调（网格、结果、第几轮）。
        /// </summary>
        public static (FeMesh mesh, FeResult result) Run(FeModel model, FeResult first, string step, double sizeMax, double target, int maxPasses, int maxElements,
                                                       Action<string> log, Action<FeMesh, FeResult, int> onPass, CancellationToken ct,
                                                       Func<string, double, FeMesh> remesh = null)
        {
            var mesh = model.Mesh;
            var res = first ?? FeSolver.Solve(model, log, ct);
            var passes = new List<AdaptivePass> { Pass(mesh, res) };
            for (int k = 1; k <= maxPasses; k++)
            {
                ct.ThrowIfCancellationRequested();
                log?.Invoke($"第 {k} 轮自适应加密：估计误差 {res.ErrorEstimate:P1}，按误差分布重新划分网格");
                var dir = Path.Combine(Path.GetTempPath(), "swkit_fea_bg");
                Directory.CreateDirectory(dir);
                var pos = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".pos");
                double hMin;
                try
                {
                    hMin = WriteSizeField(mesh, res, sizeMax, target, maxElements, pos);
                    // remesh（装配体）：按背景尺寸场重划（参数：.pos 文件、最小尺寸）
                    var m2 = remesh != null ? remesh(pos, hMin) : Gmsh.Mesh(step, sizeMax, log, 4, pos, hMin * 0.7);
                    var model2 = model.With(m2);
                    var r2 = FeSolver.Solve(model2, log, ct);
                    passes.Add(Pass(m2, r2));
                    double change = res.MaxVmAway > 0 ? Math.Abs(r2.MaxVmAway - res.MaxVmAway) / res.MaxVmAway : 1;
                    mesh = m2; res = r2; model = model2;
                    onPass?.Invoke(mesh, res, k);
                    if (res.ErrorEstimate <= target && change < 0.03) break;
                }
                finally { try { File.Delete(pos); } catch { } }
            }
            res.Passes = passes;
            return (mesh, res);
        }

        /// <summary>算每个单元的新尺寸，节点取相邻单元的最小值，写成 Gmsh 背景网格。返回最小尺寸。</summary>
        public static double WriteSizeField(FeMesh m, FeResult r, double sizeMax, double target, int maxElements, string path)
        {
            int ne = m.Elements, nn = m.Nodes;
            double E2 = r.ElemError2.Sum(), U2 = 2 * r.StrainEnergy;
            double eAllow = target * Math.Sqrt((U2 + E2) / ne);
            var h = new double[ne]; var hNew = new double[ne];
            double hMinAllowed = sizeMax / 25;
            for (int e = 0; e < ne; e++)
            {
                double s = 0;
                int[] c = { m.Tets[10 * e], m.Tets[10 * e + 1], m.Tets[10 * e + 2], m.Tets[10 * e + 3] };
                for (int a = 0; a < 4; a++) for (int b = a + 1; b < 4; b++) s += (m.P(c[a]) - m.P(c[b])).Length;
                h[e] = s / 6;
                double eK = Math.Sqrt(Math.Max(r.ElemError2[e], 1e-300));
                double ratio = Math.Pow(eAllow / eK, 0.5);
                ratio = Math.Max(0.3, Math.Min(1.6, ratio));
                hNew[e] = Math.Max(hMinAllowed, Math.Min(sizeMax, h[e] * ratio));
            }
            // 预计单元数超过上限时整体放大
            double predicted = 0;
            for (int e = 0; e < ne; e++) predicted += Math.Pow(h[e] / hNew[e], 3);
            if (predicted > maxElements)
            {
                double s = Math.Pow(predicted / maxElements, 1.0 / 3);
                for (int e = 0; e < ne; e++) hNew[e] = Math.Min(sizeMax, hNew[e] * s);
            }
            var nodeSize = new double[nn];
            for (int i = 0; i < nn; i++) nodeSize[i] = double.MaxValue;
            for (int e = 0; e < ne; e++) for (int a = 0; a < 4; a++) { int n = m.Tets[10 * e + a]; nodeSize[n] = Math.Min(nodeSize[n], hNew[e]); }
            var ci = CultureInfo.InvariantCulture;
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine("View \"size\" {");
                var sb = new StringBuilder();
                for (int e = 0; e < ne; e++)
                {
                    sb.Clear().Append("SS(");
                    for (int a = 0; a < 4; a++)
                    {
                        var p = m.P(m.Tets[10 * e + a]);
                        if (a > 0) sb.Append(',');
                        sb.Append(p.X.ToString("R", ci)).Append(',').Append(p.Y.ToString("R", ci)).Append(',').Append(p.Z.ToString("R", ci));
                    }
                    sb.Append("){");
                    for (int a = 0; a < 4; a++) { if (a > 0) sb.Append(','); sb.Append(nodeSize[m.Tets[10 * e + a]].ToString("R", ci)); }
                    sb.Append("};");
                    w.WriteLine(sb.ToString());
                }
                w.WriteLine("};");
            }
            return hNew.Min();
        }
    }
}
