using System;
using System.Collections.Generic;
using System.Linq;
using SwKit.Core.Urdf;

namespace SwKit.Core.Parts
{
    /// <summary>预览网格：实体部分，以及切除（孔、内六角）画成的深色"贴片"。</summary>
    public sealed class PreviewMesh
    {
        public Mesh Body = new Mesh();
        public Mesh Cuts = new Mesh();
    }

    /// <summary>
    /// 不用 SolidWorks，直接把 PartGeom 变成三角网格用于界面预览。
    /// 凸台按约定互不重叠，直接把每个凸台的网格放在一起；切除只在所在平面上画深色贴片。
    /// </summary>
    public static class PartMesher
    {
        const int RevolveSegments = 72;

        public static PreviewMesh Build(PartGeom g)
        {
            var r = new PreviewMesh();
            foreach (var op in g.Ops)
            {
                switch (op.Kind)
                {
                    case OpKind.Extrude: Extrude(r.Body, op); break;
                    case OpKind.Revolve: Revolve(r.Body, op.Loops[0]); break;
                    case OpKind.Cut:
                        foreach (var l in op.Loops)
                        {
                            var p = Ccw(l.Tessellate());
                            foreach (var t in Triangulate(p, new List<List<V2>>()))
                                AddTri(r.Cuts, Op.Map(op.Dir, t.Item1, op.Y0 + 2e-5), Op.Map(op.Dir, t.Item2, op.Y0 + 2e-5), Op.Map(op.Dir, t.Item3, op.Y0 + 2e-5));
                        }
                        break;
                }
            }
            return r;
        }


        static void AddTri(Mesh m, Vec3 a, Vec3 b, Vec3 c)
        {
            m.Data.Add((float)a.X); m.Data.Add((float)a.Y); m.Data.Add((float)a.Z);
            m.Data.Add((float)b.X); m.Data.Add((float)b.Y); m.Data.Add((float)b.Z);
            m.Data.Add((float)c.X); m.Data.Add((float)c.Y); m.Data.Add((float)c.Z);
        }

        static List<V2> Ccw(List<V2> p) { if (Loop.SignedArea(p) < 0) p.Reverse(); return p; }
        static List<V2> Cw(List<V2> p) { if (Loop.SignedArea(p) > 0) p.Reverse(); return p; }

        static void Extrude(Mesh m, Op op)
        {
            double y0 = op.Y0, y1 = op.Y0 + op.Height;
            var outer = Ccw(op.Loops[0].Tessellate());
            var holes = op.Loops.Skip(1).Select(l => Cw(l.Tessellate())).ToList();
            // 草图坐标逆时针 → 映射到模型后从拉伸方向的正向看也是逆时针（三个基准面都是这样），顶面法向朝外
            Vec3 Top(V2 p, double h) => Op.Map(op.Dir, p, h);
            foreach (var t in Triangulate(outer, holes))
            {
                AddTri(m, Top(t.Item1, y1), Top(t.Item2, y1), Top(t.Item3, y1));
                AddTri(m, Top(t.Item1, y0), Top(t.Item3, y0), Top(t.Item2, y0));
            }
            foreach (var ring in new[] { outer }.Concat(holes))
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    AddTri(m, Top(a, y0), Top(b, y0), Top(b, y1));
                    AddTri(m, Top(a, y0), Top(b, y1), Top(a, y1));
                }
            }
        }

        static void Revolve(Mesh m, Loop profile)
        {
            var p = profile.Tessellate();
            int n = RevolveSegments;
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i]; var b = p[(i + 1) % p.Count];
                if (a.X <= 1e-12 && b.X <= 1e-12) continue;   // 在轴上的边
                for (int k = 0; k < n; k++)
                {
                    double t0 = 2 * Math.PI * k / n, t1 = 2 * Math.PI * (k + 1) / n;
                    Vec3 P(V2 q, double t) => new Vec3(q.X * Math.Cos(t), q.Y, q.X * Math.Sin(t));
                    if (a.X > 1e-12) AddTri(m, P(a, t0), P(b, t0), P(a, t1));
                    if (b.X > 1e-12) AddTri(m, P(b, t0), P(b, t1), P(a, t1));
                }
            }
        }

        // ================= 带孔多边形三角剖分（耳切法，孔用"桥"接到外轮廓上） =================

        public static List<(V2, V2, V2)> Triangulate(List<V2> outer, List<List<V2>> holes)
        {
            if (holes.Count == 0) return EarClip(outer);
            // 先把整个图形转一个很小的"不整"的角度再剖分：孔排成一列（x 相同）或一行（y 相同）时，
            // 桥接边会和别的边共线，耳切法会卡住。转一下就没有这种巧合了，剖分完再转回来（三角形顶点就是原来的点）。
            const double rot = 0.0123456;
            var map = new Dictionary<(double, double), V2>();
            V2 R(V2 p) { var q = p.Rotate(rot); map[(q.X, q.Y)] = p; return q; }
            var poly = outer.Select(R).ToList();
            foreach (var hole in holes.Select(h => h.Select(R).ToList()).OrderByDescending(h => h.Max(v => v.X)))
                poly = Bridge(poly, hole);
            return EarClip(poly).Select(t => (map[(t.Item1.X, t.Item1.Y)], map[(t.Item2.X, t.Item2.Y)], map[(t.Item3.X, t.Item3.Y)])).ToList();
        }

        static List<V2> Bridge(List<V2> poly, List<V2> hole)
        {
            // 孔上最右边的点 M，找外轮廓上能直接看到它的最近点 P，用 M-P 两条重合的边把孔接进来
            int mi = 0;
            for (int i = 1; i < hole.Count; i++) if (hole[i].X > hole[mi].X) mi = i;
            var M = hole[mi];
            int best = -1; double bestD = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                var P = poly[i];
                var d = (P - M).Length;
                if (d >= bestD) continue;
                if (P.X < M.X - 1e-12) continue;
                if (Crosses(M, P, poly) || Crosses(M, P, hole)) continue;
                best = i; bestD = d;
            }
            if (best < 0)   // 没找到严格可见的点：退一步取最近点（预览而已）
                for (int i = 0; i < poly.Count; i++) { var d = (poly[i] - M).Length; if (d < bestD) { bestD = d; best = i; } }
            var r = new List<V2>(poly.Count + hole.Count + 2);
            for (int i = 0; i <= best; i++) r.Add(poly[i]);
            for (int k = 0; k <= hole.Count; k++) r.Add(hole[(mi + k) % hole.Count]);
            for (int i = best; i < poly.Count; i++) r.Add(poly[i]);
            return r;
        }

        static bool Crosses(V2 a, V2 b, List<V2> ring)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                var c = ring[i]; var d = ring[(i + 1) % ring.Count];
                if (Same(c, a) || Same(c, b) || Same(d, a) || Same(d, b)) continue;
                if (SegInter(a, b, c, d)) return true;
            }
            return false;
        }

        static bool Same(V2 a, V2 b) => Math.Abs(a.X - b.X) < 1e-12 && Math.Abs(a.Y - b.Y) < 1e-12;
        static double Cross(V2 o, V2 a, V2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        static bool SegInter(V2 a, V2 b, V2 c, V2 d)
        {
            double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
            return (d1 > 0) != (d2 > 0) && (d3 > 0) != (d4 > 0) && Math.Abs(d1) > 1e-18 && Math.Abs(d2) > 1e-18;
        }

        static List<(V2, V2, V2)> EarClip(List<V2> pts)
        {
            var res = new List<(V2, V2, V2)>();
            var idx = Enumerable.Range(0, pts.Count).ToList();
            int i = 0, fails = 0;
            while (idx.Count > 3)
            {
                int n = idx.Count;
                if (i >= n) i = 0;
                var a = pts[idx[(i + n - 1) % n]];
                var b = pts[idx[i]];
                var c = pts[idx[(i + 1) % n]];
                bool ear = Cross(a, b, c) > 1e-20;   // 凸角才可能是耳朵
                if (ear)
                {
                    // 只有凹角顶点可能落在耳朵里
                    for (int k = 0; k < n && ear; k++)
                    {
                        var p = pts[idx[k]];
                        if (Same(p, a) || Same(p, b) || Same(p, c)) continue;
                        if (Cross(pts[idx[(k + n - 1) % n]], p, pts[idx[(k + 1) % n]]) > 0) continue;
                        if (Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0) ear = false;
                    }
                }
                if (ear)
                {
                    res.Add((a, b, c));
                    idx.RemoveAt(i);
                    if (i > 0) i--;
                    fails = 0;
                }
                else if (++fails > n)
                {
                    idx.RemoveAt(i);   // 转了一圈都找不到耳朵（数值退化）：丢掉这个点继续，预览不受影响
                    fails = 0;
                }
                else i++;
            }
            if (idx.Count == 3) res.Add((pts[idx[0]], pts[idx[1]], pts[idx[2]]));
            return res;
        }
    }
}
