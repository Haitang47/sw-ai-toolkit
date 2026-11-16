using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace SwKit.Core.Urdf
{
    /// <summary>圆柱面特征（同一零件里同轴、同半径、同凹凸的面合并成一个）。</summary>
    [Serializable]
    public sealed class CylFeat
    {
        public Vec3 P, D;            // 轴线上一点、单位方向
        public double R;
        public bool Convex;          // true = 轴、销（材料在轴线一侧）；false = 孔
        public double T0, T1;        // 沿 D 的范围（相对 P）
        public double Coverage;      // 绕轴的角度覆盖（弧度），整圈为 2π
        public double Area;
        public double Length => T1 - T0;
        public Vec3 Mid => P + D * ((T0 + T1) / 2);
        public CylFeat Transformed(Mat3 r, Vec3 t) => new CylFeat { P = r * P + t, D = r * D, R = R, Convex = Convex, T0 = T0, T1 = T1, Coverage = Coverage, Area = Area };
        public override string ToString() => $"{(Convex ? "轴" : "孔")} Φ{R * 2000:0.##} 轴线 {Mid} 方向 {D} 长 {Length * 1000:0.#} mm 覆盖 {Coverage * 180 / Math.PI:0}°";
    }

    /// <summary>平面特征：外法向、面上的三角形（用于判断两个面是否贴合）。</summary>
    [Serializable]
    public sealed class PlaneFeat
    {
        public Vec3 P, N;
        public double Area;
        public float[] Tris;         // 每个三角形 9 个数
        public Vec3 Min, Max;
        public PlaneFeat Transformed(Mat3 r, Vec3 t)
        {
            var f = new PlaneFeat { P = r * P + t, N = r * N, Area = Area, Tris = new float[Tris.Length] };
            for (int i = 0; i < Tris.Length; i += 3)
            {
                var v = r * new Vec3(Tris[i], Tris[i + 1], Tris[i + 2]) + t;
                f.Tris[i] = (float)v.X; f.Tris[i + 1] = (float)v.Y; f.Tris[i + 2] = (float)v.Z;
            }
            f.UpdateBox();
            return f;
        }
        public void UpdateBox()
        {
            Min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            Max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            for (int i = 0; i < Tris.Length; i += 3)
            {
                Min = new Vec3(Math.Min(Min.X, Tris[i]), Math.Min(Min.Y, Tris[i + 1]), Math.Min(Min.Z, Tris[i + 2]));
                Max = new Vec3(Math.Max(Max.X, Tris[i]), Math.Max(Max.Y, Tris[i + 1]), Math.Max(Max.Z, Tris[i + 2]));
            }
        }
    }

    /// <summary>一个零件的 B-rep 几何特征（圆柱面、平面），用于在没有配合时推断关节。</summary>
    [Serializable]
    public sealed class PartFeatures
    {
        public List<CylFeat> Cyls = new List<CylFeat>();
        public List<PlaneFeat> Planes = new List<PlaneFeat>();
        public int Faces;
        public bool Truncated;
        public PartFeatures Transformed(Mat3 r, Vec3 t)
        {
            var f = new PartFeatures { Faces = Faces, Truncated = Truncated };
            f.Cyls.AddRange(Cyls.Select(c => c.Transformed(r, t)));
            f.Planes.AddRange(Planes.Select(p => p.Transformed(r, t)));
            return f;
        }
        public PartFeatures Transformed(Pose p) => Transformed(p.R, p.T);
    }

    /// <summary>
    /// 从零件文档读 B-rep：圆柱面（轴线、半径、凹凸、轴向范围、角度覆盖）和平面（外法向、三角化）。
    /// 坐标是零件自己的坐标系；用零部件位姿换到装配体坐标（不依赖 Component2.GetBodies3 返回的是哪个坐标系）。
    /// </summary>
    public static class FeatureScanner
    {
        public const int MaxFaces = 6000;
        const int MaxPlaneTris = 400;
        /// <summary>开发用：平面的三角化法向与 Face2.Normal 相反的次数。</summary>
        public static int NormalFlips;

        public static PartFeatures Scan(ModelDoc2 doc)
        {
            var result = new PartFeatures();
            if (!(doc is PartDoc part)) return result;
            var bodies = part.GetBodies2(0, true) as object[];
            if (bodies == null) return result;
            var raw = new List<(CylFeat c, List<double> angles)>();
            foreach (Body2 b in bodies)
            {
                var faces = b.GetFaces() as object[];
                if (faces == null) continue;
                foreach (Face2 f in faces)
                {
                    if (result.Faces++ >= MaxFaces) { result.Truncated = true; break; }
                    Surface s;
                    try { s = f.GetSurface() as Surface; } catch { continue; }
                    if (s == null) continue;
                    bool cyl, plane = false;
                    try { cyl = s.IsCylinder(); if (!cyl) plane = s.IsPlane(); } catch { continue; }
                    if (!cyl && !plane) continue;
                    float[] tess = null;
                    try { tess = f.GetTessTriangles(true) as float[]; } catch { }
                    double area = 0;
                    try { area = f.GetArea(); } catch { }
                    if (cyl)
                    {
                        var prm = s.CylinderParams as double[];
                        if (prm == null || prm.Length < 7) continue;
                        // 凹凸：实测 FaceInSurfaceSense 为 true 的是孔（与直觉相反），所以优先用三角化的顶点法向（指向材料外侧）判断：
                        // 法向背离轴线 = 轴（凸），指向轴线 = 孔（凹）
                        bool sense = true;
                        try { sense = f.FaceInSurfaceSense(); } catch { }
                        var c = new CylFeat { P = Vec3.From(prm, 0), D = Vec3.From(prm, 3).Normalized(), R = prm[6], Convex = !sense, Area = area };
                        var angles = new List<double>();
                        if (tess != null && tess.Length >= 9)
                        {
                            float[] norms = null;
                            try { norms = f.GetTessNorms() as float[]; } catch { }
                            c.D.Perpendiculars(out var t1, out var t2);
                            double lo = double.MaxValue, hi = double.MinValue, outward = 0;
                            for (int i = 0; i + 2 < tess.Length; i += 3)
                            {
                                var v = new Vec3(tess[i], tess[i + 1], tess[i + 2]) - c.P;
                                var ax = v.Dot(c.D);
                                lo = Math.Min(lo, ax); hi = Math.Max(hi, ax);
                                var radial = v - c.D * ax;
                                angles.Add(Math.Atan2(radial.Dot(t2), radial.Dot(t1)));
                                if (norms != null && i + 2 < norms.Length) outward += new Vec3(norms[i], norms[i + 1], norms[i + 2]).Dot(radial.Normalized());
                            }
                            if (Math.Abs(outward) > 1e-6) c.Convex = outward > 0;
                            c.T0 = lo; c.T1 = hi;
                        }
                        else
                        {
                            // 没有三角化数据：用面积估算长度（假定整圈）
                            c.T0 = -area / (4 * Math.PI * c.R); c.T1 = -c.T0;
                            angles.Add(double.NaN);
                        }
                        raw.Add((c, angles));
                    }
                    else
                    {
                        Vec3 n;
                        try { n = Vec3.From((double[])f.Normal).Normalized(); } catch { continue; }
                        if (n.Length < 0.5 || tess == null || tess.Length < 9) continue;
                        // 以三角化法向为准（指向材料外侧）；与 Face2.Normal 不一致时记下来
                        try
                        {
                            if (f.GetTessNorms() is float[] tn && tn.Length >= 3)
                            {
                                var m = new Vec3(tn[0], tn[1], tn[2]).Normalized();
                                if (m.Length > 0.5) { if (m.Dot(n) < 0) NormalFlips++; n = m; }
                            }
                        }
                        catch { }
                        var pf = new PlaneFeat { N = n, Area = area, P = new Vec3(tess[0], tess[1], tess[2]) };
                        if (tess.Length / 9 > MaxPlaneTris)
                        {
                            // 三角形太多（细节很多的面）：只保留面积最大的一部分，足够判断贴合
                            var idx = Enumerable.Range(0, tess.Length / 9).OrderByDescending(t => TriArea(tess, t)).Take(MaxPlaneTris).ToList();
                            pf.Tris = new float[idx.Count * 9];
                            for (int k = 0; k < idx.Count; k++) Array.Copy(tess, idx[k] * 9, pf.Tris, k * 9, 9);
                        }
                        else pf.Tris = tess;
                        pf.UpdateBox();
                        result.Planes.Add(pf);
                    }
                }
            }

            // 合并：同轴、同半径、同凹凸的圆柱面（SolidWorks 常把一整圈拆成两个半圆柱面）
            var used = new bool[raw.Count];
            for (int i = 0; i < raw.Count; i++)
            {
                if (used[i]) continue;
                var c = raw[i].c;
                var angles = new List<double>(raw[i].angles);
                double area = c.Area, lo = c.T0, hi = c.T1;
                for (int j = i + 1; j < raw.Count; j++)
                {
                    if (used[j]) continue;
                    var o = raw[j].c;
                    if (o.Convex != c.Convex || Math.Abs(o.R - c.R) > 1e-6 || Math.Abs(o.D.Dot(c.D)) < 0.99999) continue;
                    var dp = o.P - c.P;
                    if ((dp - c.D * dp.Dot(c.D)).Length > 1e-6) continue;
                    // 轴向范围要相接或重叠（同一根轴上分开的两段保持分开）
                    var shift = dp.Dot(c.D);
                    var sgn = o.D.Dot(c.D) > 0 ? 1 : -1;
                    double a0 = shift + sgn * o.T0, a1 = shift + sgn * o.T1;
                    double olo = Math.Min(a0, a1), ohi = Math.Max(a0, a1);
                    if (olo > hi + 1e-5 || ohi < lo - 1e-5) continue;
                    lo = Math.Min(lo, olo); hi = Math.Max(hi, ohi);
                    area += o.Area;
                    // 角度：换到 c 的参考方向
                    if (raw[j].angles.Any(double.IsNaN)) angles.Add(double.NaN);
                    else
                    {
                        c.D.Perpendiculars(out var t1, out var t2);
                        o.D.Perpendiculars(out var u1, out var u2);
                        foreach (var ang in raw[j].angles)
                        {
                            var v = u1 * Math.Cos(ang) + u2 * Math.Sin(ang);
                            angles.Add(Math.Atan2(v.Dot(t2), v.Dot(t1)));
                        }
                    }
                    used[j] = true;
                }
                c.T0 = lo; c.T1 = hi; c.Area = area;
                c.Coverage = angles.Any(double.IsNaN) ? 2 * Math.PI : Coverage(angles);
                result.Cyls.Add(c);
            }
            return result;
        }

        static double TriArea(float[] t, int k)
        {
            int o = k * 9;
            var a = new Vec3(t[o], t[o + 1], t[o + 2]); var b = new Vec3(t[o + 3], t[o + 4], t[o + 5]); var c = new Vec3(t[o + 6], t[o + 7], t[o + 8]);
            return (b - a).Cross(c - a).Length / 2;
        }

        /// <summary>角度覆盖 = 2π − 最大空隙。</summary>
        static double Coverage(List<double> angles)
        {
            if (angles.Count < 2) return 0;
            var a = angles.OrderBy(x => x).ToList();
            double gap = a[0] + 2 * Math.PI - a[a.Count - 1];
            for (int i = 1; i < a.Count; i++) gap = Math.Max(gap, a[i] - a[i - 1]);
            return 2 * Math.PI - gap;
        }
    }
}
