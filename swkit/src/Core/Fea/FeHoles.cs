using System;
using System.Collections.Generic;
using System.Linq;
using SwKit.Core.Urdf;

namespace SwKit.Core.Fea
{
    /// <summary>
    /// 「选同组孔」：从选中的孔面出发，找同样大小、挨在一起的孔（例如一个电机座的 4 个螺丝孔，每个孔常被拆成两个半圆柱面）。
    /// 做法：面积相同（±4%）的曲面作为候选；离种子最近的另一个孔的距离 × 1.35 作为连接距离，单链接扩展。
    /// 不同电机座之间隔得远，不会连到一起。
    /// </summary>
    public static class FeHoles
    {
        public static List<int> Group(FeMesh m, IEnumerable<int> seeds)
        {
            var curved = m.Faces.Where(f => !f.IsPlane && f.Area > 0).ToDictionary(f => f.Id);
            var result = new HashSet<int>();
            m.Bounds(out var bmin, out var bmax);
            double diag = (bmax - bmin).Length;
            foreach (var id in seeds)
            {
                if (!curved.TryGetValue(id, out var seed)) continue;
                result.Add(id);
                double size = FaceSize(m, id);
                var cands = curved.Values.Where(f => Math.Abs(f.Area - seed.Area) <= 0.04 * seed.Area).ToList();
                // 最近的另一个孔（跳过同一个孔的另一半：两半形心相距不到一个孔径）
                double nn = cands.Where(f => f.Id != id).Select(f => (f.Centroid - seed.Centroid).Length).Where(d => d > 1.5 * size).DefaultIfEmpty(double.NaN).Min();
                if (double.IsNaN(nn))
                {
                    foreach (var f in cands) if ((f.Centroid - seed.Centroid).Length <= 1.5 * size) result.Add(f.Id);
                    continue;
                }
                double link = Math.Min(1.35 * nn, 0.15 * diag);
                var queue = new Queue<FeFace>(); queue.Enqueue(seed);
                var seen = new HashSet<int> { id };
                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    foreach (var f in cands)
                        if (!seen.Contains(f.Id) && (f.Centroid - cur.Centroid).Length <= link) { seen.Add(f.Id); result.Add(f.Id); queue.Enqueue(f); }
                }
            }
            return result.ToList();
        }

        /// <summary>面的包围盒最大边长（米）。半圆柱孔面约等于孔径或孔深。</summary>
        static double FaceSize(FeMesh m, int id)
        {
            var nodes = m.FaceNodes(new[] { id });
            if (nodes.Count == 0) return 0;
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (var n in nodes)
            {
                var p = m.P(n);
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            return Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
        }
    }
}
