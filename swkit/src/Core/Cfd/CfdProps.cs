using System;
using System.Collections.Generic;
using System.Linq;
using SwKit.Core.Fea;
using SwKit.Core.Urdf;

namespace SwKit.Core.Cfd
{
    /// <summary>
    /// 模型里建好的桨叶 → 作动盘：从桨叶实体的三角形算出盘心、转轴（推力方向）和直径。
    /// · 盘心：表面积加权的形心（桨叶对称，形心在转轴上）；
    /// · 转轴：表面分布最「薄」的方向（面积加权的二阶矩最小特征值的特征向量），斜装 / 倾转的桨也对；
    /// · 直径：顶点到转轴的最大距离 × 2。
    /// 自动识别：扁平（最薄方向 / 最长方向 &lt; 0.2）、几乎没有平面（桨叶是扭转的曲面，机臂、底板大多是平面）、
    /// 直径 ≥ 30 mm；再按直径（±5%）分组，同样大小的有 2 个以上才算（四旋翼的 4 个桨）。
    /// </summary>
    public static class CfdProps
    {
        public sealed class PartShape
        {
            public int Part;
            public Vec3 Center, Axis;
            public double Radius, Area, Flatness, PlanarFraction, Thickness;
        }

        /// <summary>每个零件（实体）的形状统计。tris：9 个数一个三角形（m）；triPart：每个三角形属于哪个零件。</summary>
        public static Dictionary<int, PartShape> Shapes(float[] tris, int[] triPart, IEnumerable<int> only = null)
        {
            var want = only != null ? new HashSet<int>(only) : null;
            int nt = tris.Length / 9;
            var area = new Dictionary<int, double>(); var mom = new Dictionary<int, Vec3>();
            Vec3 P(int t, int k) => new Vec3(tris[9 * t + 3 * k], tris[9 * t + 3 * k + 1], tris[9 * t + 3 * k + 2]);
            // 第一遍：面积和形心
            for (int t = 0; t < nt; t++)
            {
                int b = triPart != null ? triPart[t] : 0;
                if (want != null && !want.Contains(b)) continue;
                var a = P(t, 0); var bb = P(t, 1); var c = P(t, 2);
                double ar = (bb - a).Cross(c - a).Length / 2;
                area.TryGetValue(b, out var A0); area[b] = A0 + ar;
                mom.TryGetValue(b, out var M0); mom[b] = M0 + (a + bb + c) * (ar / 3);
            }
            var cov = area.Keys.ToDictionary(b => b, b => new double[3, 3]);
            var center = area.Keys.ToDictionary(b => b, b => area[b] > 0 ? mom[b] * (1 / area[b]) : Vec3.Zero);
            // 第二遍：面积加权的二阶矩（三角形三个顶点和形心各算一次，细长三角形也稳）
            for (int t = 0; t < nt; t++)
            {
                int b = triPart != null ? triPart[t] : 0;
                if (!cov.TryGetValue(b, out var C)) continue;
                var a = P(t, 0); var bb = P(t, 1); var c = P(t, 2);
                double ar = (bb - a).Cross(c - a).Length / 2;
                var o = center[b];
                foreach (var q in new[] { a, bb, c, (a + bb + c) / 3 })
                {
                    var d = q - o; double[] v = { d.X, d.Y, d.Z };
                    for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) C[i, j] += ar / 4 * v[i] * v[j];
                }
            }
            var res = new Dictionary<int, PartShape>();
            foreach (var b in area.Keys)
            {
                if (area[b] <= 0) continue;
                Eigen3.Symmetric(cov[b], out var lam, out var vec);
                res[b] = new PartShape { Part = b, Center = center[b], Axis = vec[0], Area = area[b], Flatness = Math.Sqrt(Math.Max(0, lam[0]) / Math.Max(lam[2], 1e-30)) };
            }
            // 第三遍：到转轴的最大距离、沿轴的厚度、「正好是平面」的面积比例（法向和转轴平行）
            var lo = res.Keys.ToDictionary(b => b, b => double.MaxValue); var hi = res.Keys.ToDictionary(b => b, b => double.MinValue);
            var planar = res.Keys.ToDictionary(b => b, b => 0.0);
            for (int t = 0; t < nt; t++)
            {
                int b = triPart != null ? triPart[t] : 0;
                if (!res.TryGetValue(b, out var s)) continue;
                var a = P(t, 0); var bb = P(t, 1); var c = P(t, 2);
                var cr = (bb - a).Cross(c - a); double ar = cr.Length / 2;
                if (ar > 0 && Math.Abs(cr.Normalized().Dot(s.Axis)) > 0.9995) planar[b] += ar;
                foreach (var q in new[] { a, bb, c })
                {
                    var d = q - s.Center; double h = d.Dot(s.Axis);
                    var r = (d - s.Axis * h).Length;
                    if (r > s.Radius) s.Radius = r;
                    if (h < lo[b]) lo[b] = h; if (h > hi[b]) hi[b] = h;
                }
            }
            foreach (var s in res.Values) { s.PlanarFraction = planar[s.Part] / s.Area; s.Thickness = hi[s.Part] - lo[s.Part]; }
            return res;
        }

        /// <summary>几个实体合在一起（桨毂 + 叶片分成几个实体时）的形状。</summary>
        public static PartShape Combined(float[] tris, int[] triPart, IList<int> parts)
        {
            var set = new HashSet<int>(parts);
            var map = new int[triPart.Length];
            for (int t = 0; t < map.Length; t++) map[t] = set.Contains(triPart[t]) ? parts[0] : -1;
            return Shapes(tris, map, new[] { parts[0] }).TryGetValue(parts[0], out var s) ? s : null;
        }

        /// <summary>零件 → 作动盘（推力方向朝 up 那一侧；水平的桨保持特征向量的方向，界面上可以反向）。</summary>
        public static CfdDisk DiskFrom(PartShape s, Vec3 up, string name)
        {
            var ax = s.Axis;
            if (ax.Dot(up) < 0) ax = -ax;
            return new CfdDisk
            {
                Name = name, Center = s.Center, Axis = ax.Normalized(), Radius = s.Radius,
                HubRadius = Math.Max(0.004, s.Radius * 0.15), Loading = CfdLoading.Prop, SourcePart = s.Part,
            };
        }

        /// <summary>像桨叶的零件（见类说明）。返回零件编号。</summary>
        public static List<int> Detect(Dictionary<int, PartShape> shapes, double modelSpan)
        {
            var cand = shapes.Values.Where(s => s.Radius >= 0.015 && s.Radius <= 0.6 * modelSpan && s.Flatness < 0.2 && s.PlanarFraction < 0.25 && s.Thickness < 0.6 * s.Radius).ToList();
            var groups = new List<List<PartShape>>();
            foreach (var s in cand.OrderByDescending(x => x.Radius))
            {
                var g = groups.FirstOrDefault(gr => Math.Abs(gr[0].Radius - s.Radius) <= 0.05 * gr[0].Radius);
                if (g == null) groups.Add(new List<PartShape> { s }); else g.Add(s);
            }
            // 同一个桨不会被拆成挨在一起的两个实体：同组里中心太近（< 半径）的只留一个
            var result = new List<int>();
            foreach (var g in groups.Where(gr => gr.Count >= 2 || cand.Count == 1))
            {
                var kept = new List<PartShape>();
                foreach (var s in g) if (kept.All(k => (k.Center - s.Center).Length > k.Radius)) kept.Add(s);
                if (kept.Count >= 2 || cand.Count == 1) result.AddRange(kept.Select(k => k.Part));
            }
            return result;
        }
    }
}
