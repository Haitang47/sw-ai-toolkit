using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using SwKit.Core.Fea;
using SwKit.Core.Urdf;

namespace SwKit.Cli
{
    /// <summary>拓扑优化结果的俯视图（沿板的法向，或者零件最薄的方向看），命令行里检查结果用。</summary>
    static class TopoPreview
    {
        public static void SaveTopView(FeMesh m, TopoResult r, string path, int px = 1400)
        {
            Vec3 n;
            if (r.PlateMode) n = r.PlateNormal;
            else
            {
                m.Bounds(out var a, out var b);
                var d = b - a;
                n = d.X <= d.Y && d.X <= d.Z ? new Vec3(1, 0, 0) : d.Y <= d.Z ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);
            }
            var u = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            u = (u - n * u.Dot(n)).Normalized(); var v = n.Cross(u);
            double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
            for (int i = 0; i < m.Nodes; i++)
            {
                var p = m.P(i);
                uMin = Math.Min(uMin, p.Dot(u)); uMax = Math.Max(uMax, p.Dot(u));
                vMin = Math.Min(vMin, p.Dot(v)); vMax = Math.Max(vMax, p.Dot(v));
            }
            const int pad = 30, header = 40;
            double scale = (px - 2 * pad) / Math.Max(uMax - uMin, vMax - vMin);
            int w = (int)((uMax - uMin) * scale) + 2 * pad, h = (int)((vMax - vMin) * scale) + 2 * pad + header;
            using (var bmp = new Bitmap(w, h))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                PointF P(int node) { var p = m.P(node); return new PointF((float)((p.Dot(u) - uMin) * scale + pad), (float)((vMax - p.Dot(v)) * scale + pad + header)); }
                // 先画原外形（浅灰），再按密度从低到高画单元（高密度盖在上面）
                var order = Enumerable.Range(0, m.Elements).OrderBy(e => r.Density[e]).ToArray();
                foreach (var e in order)
                {
                    double rho = r.Density[e];
                    var pts = Hull(Enumerable.Range(0, 4).Select(a => P(m.Tets[10 * e + a])).ToArray());
                    int c = rho < 0.5 ? 236 : (int)(200 - 160 * (rho - 0.5) / 0.5);
                    using (var br = new SolidBrush(rho < 0.5 ? Color.FromArgb(236, 236, 240) : Color.FromArgb(c, (int)(c * 0.85), (int)(c * 0.7))))
                        g.FillPolygon(br, pts);
                }
                using (var f = new Font("Microsoft YaHei", 13))
                    g.DrawString($"{(r.PlateMode ? "板件模式" : "三维模式（俯视，取最大密度）")}  保留 {r.VolumeFraction:P0}（目标 {r.TargetFraction:P0}） 刚度 {r.StiffnessRatio:P0}  必须保留 {r.PassiveFraction:P0}  {r.Iterations} 轮",
                        f, Brushes.Black, pad, 8);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        static PointF[] Hull(PointF[] p)
        {
            var s = p.OrderBy(a => a.X).ThenBy(a => a.Y).ToArray();
            double Cross(PointF o, PointF a, PointF b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var hull = new PointF[2 * s.Length]; int k = 0;
            for (int i = 0; i < s.Length; i++) { while (k >= 2 && Cross(hull[k - 2], hull[k - 1], s[i]) <= 0) k--; hull[k++] = s[i]; }
            for (int i = s.Length - 2, t = k + 1; i >= 0; i--) { while (k >= t && Cross(hull[k - 2], hull[k - 1], s[i]) <= 0) k--; hull[k++] = s[i]; }
            return hull.Take(Math.Max(1, k - 1)).ToArray();
        }
    }
}
