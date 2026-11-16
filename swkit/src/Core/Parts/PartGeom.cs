using System;
using System.Collections.Generic;
using System.Linq;
using SwKit.Core.Urdf;

namespace SwKit.Core.Parts
{
    /// <summary>草图平面上的二维点（米）。</summary>
    public struct V2
    {
        public double X, Y;
        public V2(double x, double y) { X = x; Y = y; }
        public static V2 operator +(V2 a, V2 b) => new V2(a.X + b.X, a.Y + b.Y);
        public static V2 operator -(V2 a, V2 b) => new V2(a.X - b.X, a.Y - b.Y);
        public static V2 operator *(V2 a, double s) => new V2(a.X * s, a.Y * s);
        public double Length => Math.Sqrt(X * X + Y * Y);
        public static V2 Polar(double r, double a) => new V2(r * Math.Cos(a), r * Math.Sin(a));
        public static V2 Mm(double x, double y) => new V2(x / 1000, y / 1000);
        public V2 Rotate(double a) => new V2(X * Math.Cos(a) - Y * Math.Sin(a), X * Math.Sin(a) + Y * Math.Cos(a));
        public override string ToString() => $"({X * 1000:0.###}, {Y * 1000:0.###}) mm";
    }

    /// <summary>草图里的一段：直线、圆弧或样条（经过所有点）。</summary>
    public abstract class Seg
    {
        public abstract V2 Start { get; }
        public abstract V2 End { get; }
        /// <summary>离散成折线（含起点，不含终点）。</summary>
        public abstract IEnumerable<V2> Tessellate(double maxAngle);
    }

    public sealed class LineSeg : Seg
    {
        public V2 A, B;
        public LineSeg(V2 a, V2 b) { A = a; B = b; }
        public override V2 Start => A;
        public override V2 End => B;
        public override IEnumerable<V2> Tessellate(double maxAngle) { yield return A; }
    }

    /// <summary>
    /// 圆弧：圆心 C、半径 R，角度范围 [A0, A1]（A1 &gt; A0）。默认从 A0 逆时针走到 A1；
    /// Cw = true 时沿同一段圆弧反向走（从 A1 顺时针到 A0），用于凹进去的圆弧（带轮齿槽、内圆角）。
    /// 草图里画出来的几何一样，只是在轮廓里的走向不同。
    /// </summary>
    public sealed class ArcSeg : Seg
    {
        public V2 C; public double R, A0, A1; public bool Cw;
        public ArcSeg(V2 c, double r, double a0, double a1) { C = c; R = r; A0 = a0; A1 = a1 > a0 ? a1 : a1 + 2 * Math.PI; }
        /// <summary>顺时针圆弧：从角度 from 顺时针转到 to。</summary>
        public static ArcSeg Clockwise(V2 c, double r, double from, double to) => new ArcSeg(c, r, to, from) { Cw = true };
        public V2 P0 => C + V2.Polar(R, A0);
        public V2 P1 => C + V2.Polar(R, A1);
        public override V2 Start => Cw ? P1 : P0;
        public override V2 End => Cw ? P0 : P1;
        public override IEnumerable<V2> Tessellate(double maxAngle)
        {
            int n = Math.Max(2, (int)Math.Ceiling((A1 - A0) / maxAngle));
            for (int i = 0; i < n; i++) yield return C + V2.Polar(R, Cw ? A1 - (A1 - A0) * i / n : A0 + (A1 - A0) * i / n);
        }
    }

    /// <summary>样条：经过给定的点（用于渐开线齿廓）。</summary>
    public sealed class SplineSeg : Seg
    {
        public List<V2> Pts;
        public SplineSeg(IEnumerable<V2> pts) { Pts = pts.ToList(); }
        public override V2 Start => Pts[0];
        public override V2 End => Pts[Pts.Count - 1];
        public override IEnumerable<V2> Tessellate(double maxAngle) => Pts.Take(Pts.Count - 1);
    }

    /// <summary>按顺序拼接直线和圆弧的轮廓：new PathBuilder(起点).Line(..).Arc(圆心, 终点, cw).Close()。</summary>
    public sealed class PathBuilder
    {
        readonly Loop _l = new Loop();
        readonly V2 _start; V2 _cur;
        public PathBuilder(V2 start) { _start = _cur = start; }
        public V2 Current => _cur;
        public PathBuilder Line(V2 p) { if ((p - _cur).Length > 1e-12) _l.Segs.Add(new LineSeg(_cur, p)); _cur = p; return this; }
        public PathBuilder Line(double x, double y) => Line(new V2(x, y));
        /// <summary>绕圆心 c 从当前点走到 p 所在方向（半径取当前点到圆心的距离），cw = true 顺时针。</summary>
        public PathBuilder Arc(V2 c, V2 p, bool cw = false)
        {
            var r = (_cur - c).Length;
            double a0 = Math.Atan2(_cur.Y - c.Y, _cur.X - c.X), a1 = Math.Atan2(p.Y - c.Y, p.X - c.X);
            if (cw) { while (a0 <= a1 + 1e-12) a0 += 2 * Math.PI; _l.Segs.Add(ArcSeg.Clockwise(c, r, a0, a1)); }
            else { while (a1 <= a0 + 1e-12) a1 += 2 * Math.PI; _l.Segs.Add(new ArcSeg(c, r, a0, a1)); }
            _cur = c + V2.Polar(r, Math.Atan2(p.Y - c.Y, p.X - c.X));
            return this;
        }
        public Loop Close() { Line(_start); return _l; }
    }

    /// <summary>封闭轮廓：首尾相连的若干段，或一个整圆。</summary>
    public sealed class Loop
    {
        public List<Seg> Segs = new List<Seg>();
        public bool IsCircle; public V2 Center; public double Radius;

        public static Loop Circle(V2 c, double r) => new Loop { IsCircle = true, Center = c, Radius = r };
        public static Loop Circle(double r) => Circle(new V2(0, 0), r);

        /// <summary>按顺序连接各点的多边形（自动闭合）。</summary>
        public static Loop Polygon(IEnumerable<V2> pts)
        {
            var p = pts.ToList();
            var l = new Loop();
            for (int i = 0; i < p.Count; i++) l.Segs.Add(new LineSeg(p[i], p[(i + 1) % p.Count]));
            return l;
        }
        public static Loop Polygon(params V2[] pts) => Polygon((IEnumerable<V2>)pts);
        public static Loop PolygonMm(params double[] xyMm)
        {
            var pts = new List<V2>();
            for (int i = 0; i + 1 < xyMm.Length; i += 2) pts.Add(new V2(xyMm[i] / 1000, xyMm[i + 1] / 1000));
            return Polygon(pts);
        }

        /// <summary>正六边形，对边宽 s，有一对边与 x 轴平行。</summary>
        public static Loop Hexagon(double s, double rotate = 0) => Hexagon(new V2(0, 0), s, rotate);
        public static Loop Hexagon(V2 c, double s, double rotate = 0)
        {
            var r = s / Math.Sqrt(3);
            return Polygon(Enumerable.Range(0, 6).Select(i => c + V2.Polar(r, rotate + i * Math.PI / 3)));
        }

        /// <summary>带一个平面的轴 / 孔（D 形）：半径 r，平面到中心距离 f（平面在 +y 一侧）。</summary>
        public static Loop DShape(double r, double f)
        {
            var a = Math.Asin(f / r);                // 平面两端点的角度：a 和 π−a
            var l = new Loop();
            l.Segs.Add(new ArcSeg(new V2(0, 0), r, Math.PI - a, 2 * Math.PI + a));
            l.Segs.Add(new LineSeg(V2.Polar(r, a), V2.Polar(r, Math.PI - a)));
            return l;
        }

        /// <summary>两侧削平的圆（双 D 形）：半径 r，两个平面到中心的距离都是 f（平面垂直于 x 轴）。</summary>
        public static Loop DoubleD(double r, double f, V2 c = default(V2))
        {
            if (f >= r) return Circle(c, r);
            var a = Math.Acos(f / r);
            var l = new Loop();
            l.Segs.Add(new LineSeg(c + V2.Polar(r, -a), c + V2.Polar(r, a)));
            l.Segs.Add(new ArcSeg(c, r, a, Math.PI - a));
            l.Segs.Add(new LineSeg(c + V2.Polar(r, Math.PI - a), c + V2.Polar(r, Math.PI + a)));
            l.Segs.Add(new ArcSeg(c, r, Math.PI + a, 2 * Math.PI - a));
            return l;
        }

        /// <summary>圆角矩形（中心 c，宽 w，高 h，圆角 r；r = 0 时为直角），逆时针。</summary>
        public static Loop RoundedRect(double w, double h, double r, V2 c = default(V2))
        {
            double a = w / 2, b = h / 2;
            r = Math.Max(0, Math.Min(r, Math.Min(a, b) * 0.999));
            if (r <= 1e-12) return Polygon(c + new V2(-a, -b), c + new V2(a, -b), c + new V2(a, b), c + new V2(-a, b));
            return new PathBuilder(c + new V2(-a + r, -b))
                .Line(c + new V2(a - r, -b)).Arc(c + new V2(a - r, -b + r), c + new V2(a, -b + r))
                .Line(c + new V2(a, b - r)).Arc(c + new V2(a - r, b - r), c + new V2(a - r, b))
                .Line(c + new V2(-a + r, b)).Arc(c + new V2(-a + r, b - r), c + new V2(-a, b - r))
                .Line(c + new V2(-a, -b + r)).Arc(c + new V2(-a + r, -b + r), c + new V2(-a + r, -b))
                .Close();
        }
        public static Loop Rect(double w, double h, V2 c = default(V2)) => RoundedRect(w, h, 0, c);

        /// <summary>长圆形（腰形），中心 c，总长 len（沿 x，含两端半圆），宽 w。</summary>
        public static Loop Slot(double len, double w, V2 c = default(V2))
        {
            double r = w / 2, a = Math.Max(len / 2 - r, 0);
            if (a <= 1e-12) return Circle(c, r);
            return new PathBuilder(c + new V2(-a, -r))
                .Line(c + new V2(a, -r)).Arc(c + new V2(a, 0), c + new V2(a, r))
                .Line(c + new V2(-a, r)).Arc(c + new V2(-a, 0), c + new V2(-a, -r))
                .Close();
        }

        /// <summary>
        /// 圆周上均布 n 个圆形凹槽的外轮廓（同步带轮、链轮的简化齿形）：外圆半径 R，
        /// 凹槽圆心在半径 rc 处、凹槽半径 rn，第一个凹槽在角度 phase。
        /// </summary>
        public static Loop Notched(double R, int n, double rc, double rn, double phase = 0)
        {
            var cosd = (R * R + rc * rc - rn * rn) / (2 * R * rc);
            if (cosd >= 1 || cosd <= -1) throw new ArgumentException("凹槽和外圆不相交");
            var d = Math.Acos(cosd);
            var pitch = 2 * Math.PI / n;
            if (2 * d >= pitch * 0.98) throw new ArgumentException("凹槽太宽，相邻凹槽重叠");
            var l = new Loop();
            for (int k = 0; k < n; k++)
            {
                var t = phase + k * pitch;
                var nc = V2.Polar(rc, t);
                var pm = V2.Polar(R, t - d); var pp = V2.Polar(R, t + d);
                double fm = Math.Atan2(pm.Y - nc.Y, pm.X - nc.X), fp = Math.Atan2(pp.Y - nc.Y, pp.X - nc.X);
                while (fm <= fp) fm += 2 * Math.PI;           // 顺时针：从 fm 减小到 fp（经过靠近圆心的一侧）
                l.Segs.Add(ArcSeg.Clockwise(nc, rn, fm, fp));
                l.Segs.Add(new ArcSeg(new V2(0, 0), R, t + d, t + pitch - d));
            }
            return l;
        }

        /// <summary>离散成多边形（逆时针或顺时针取决于定义），maxAngle 为圆弧每段最大角度。</summary>
        public List<V2> Tessellate(double maxAngle = Math.PI / 32)
        {
            if (IsCircle)
            {
                int n = Math.Max(12, (int)Math.Ceiling(2 * Math.PI / maxAngle));
                return Enumerable.Range(0, n).Select(i => Center + V2.Polar(Radius, 2 * Math.PI * i / n)).ToList();
            }
            return Segs.SelectMany(s => s.Tessellate(maxAngle)).ToList();
        }

        /// <summary>精确面积（圆、多边形精确；圆弧和样条按细分计算）。</summary>
        public double Area()
        {
            if (IsCircle) return Math.PI * Radius * Radius;
            return Math.Abs(SignedArea(Tessellate(Math.PI / 720)));
        }

        public static double SignedArea(IList<V2> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++) { var u = p[i]; var v = p[(i + 1) % p.Count]; a += u.X * v.Y - v.X * u.Y; }
            return a / 2;
        }
    }

    public enum OpKind { Extrude, Revolve, Cut }

    /// <summary>拉伸 / 切除的方向（草图所在基准面的法向）。</summary>
    public enum Axis
    {
        /// <summary>上视基准面（或其等距面），草图 (x, y) → 模型 (X, h, −y)，沿 +Y 拉伸、−Y 切除。</summary>
        Y,
        /// <summary>前视基准面（或其等距面），草图 (x, y) → 模型 (x, y, h)，沿 +Z 拉伸、−Z 切除。</summary>
        Z,
        /// <summary>右视基准面（或其等距面），草图 (x, y) → 模型 (h, y, −x)，沿 +X 拉伸、−X 切除。</summary>
        X,
    }

    /// <summary>
    /// 建模步骤。坐标约定（与 SolidWorks 一致，零件轴线沿 +Y）：
    ///  · Extrude：在等距 Y0 的水平面（上视基准面或其等距面）上画 Loops（第一个是外轮廓，其余是孔），沿 +Y 拉伸 Height。
    ///    草图坐标 (x, y) 对应模型 (X, −Z)，与 SolidWorks 上视基准面的映射相同。
    ///    Along(Axis.Z / Axis.X) 改为画在前视 / 右视基准面的等距面上（Y0 是沿 Z / X 的距离），映射见 Axis。
    ///  · Revolve：在前视基准面上画轮廓 Loops[0]，草图 x = 半径（≥ 0），y = 高度，绕 Y 轴旋转 360°。
    ///  · Cut：在等距 Y0 的平面上画 Loops，沿法向的反方向切除深度 Height（盲孔、内六角孔）。
    /// </summary>
    public sealed class Op
    {
        public OpKind Kind;
        public List<Loop> Loops = new List<Loop>();
        public double Y0, Height;
        public string Name;
        public Axis Dir = Axis.Y;
        public Op Along(Axis a) { Dir = a; return this; }

        public static Op Extrude(string name, double y0, double height, params Loop[] loops) => new Op { Kind = OpKind.Extrude, Name = name, Y0 = y0, Height = height, Loops = loops.ToList() };
        public static Op Extrude(string name, double y0, double height, IEnumerable<Loop> loops) => Extrude(name, y0, height, loops.ToArray());
        public static Op Revolve(string name, Loop profile) => new Op { Kind = OpKind.Revolve, Name = name, Loops = { profile } };
        public static Op Cut(string name, double yTop, double depth, params Loop[] loops) => new Op { Kind = OpKind.Cut, Name = name, Y0 = yTop, Height = depth, Loops = loops.ToList() };
        public static Op Cut(string name, double yTop, double depth, IEnumerable<Loop> loops) => Cut(name, yTop, depth, loops.ToArray());

        /// <summary>草图坐标 (x, y) 在等距距离 h 处对应的模型坐标。</summary>
        public static Vec3 Map(Axis a, V2 p, double h)
        {
            switch (a)
            {
                case Axis.Z: return new Vec3(p.X, p.Y, h);
                case Axis.X: return new Vec3(h, p.Y, -p.X);
                default: return new Vec3(p.X, h, -p.Y);
            }
        }
    }

    /// <summary>一个零件的几何描述：既用来在 SolidWorks 里建模，也用来生成预览网格和核对体积。</summary>
    public sealed class PartGeom
    {
        public List<Op> Ops = new List<Op>();
        public PartGeom Add(Op op) { Ops.Add(op); return this; }
        public PartGeom AddIf(bool cond, Func<Op> op) { if (cond) Ops.Add(op()); return this; }

        /// <summary>
        /// 理论体积（立方米）。约定：各个凸台互不重叠（叠放），切除完全落在实体内部，
        /// 所以体积 = 凸台体积之和 − 切除体积之和。
        /// </summary>
        public double Volume()
        {
            double v = 0;
            foreach (var op in Ops)
            {
                switch (op.Kind)
                {
                    case OpKind.Extrude:
                        v += (op.Loops[0].Area() - op.Loops.Skip(1).Sum(l => l.Area())) * op.Height;
                        break;
                    case OpKind.Cut:
                        v -= op.Loops.Sum(l => l.Area()) * op.Height;
                        break;
                    case OpKind.Revolve:
                        // Pappus：2π × 面积 × 形心半径 = 2π ∮ x²/2 dy（对精细离散的轮廓积分）
                        var p = op.Loops[0].Tessellate(Math.PI / 720);
                        double s = 0;
                        for (int i = 0; i < p.Count; i++)
                        {
                            var a = p[i]; var b = p[(i + 1) % p.Count];
                            s += (a.X * a.X + a.X * b.X + b.X * b.X) * (b.Y - a.Y) / 3;   // ∫ x² dy 沿边
                        }
                        v += Math.Abs(Math.PI * s);
                        break;
                }
            }
            return v;
        }
    }
}
