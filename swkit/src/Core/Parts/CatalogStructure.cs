using System;
using System.Collections.Generic;
using System.Linq;

namespace SwKit.Core.Parts
{
    /// <summary>型材附件和结构件：角码、端盖、方管、圆管、角钢、槽钢、工字钢、棒料、板材。</summary>
    public static partial class Catalog
    {
        // 型材角码：单元边长 → 边长 L、厚 t、孔径
        internal static readonly Dictionary<string, double[]> Brackets = new Dictionary<string, double[]>
        {
            ["2020"] = new double[] { 20, 3, 5.5 }, ["3030"] = new double[] { 30, 4, 6.5 }, ["4040"] = new double[] { 40, 4.5, 8.5 },
        };
        // GB/T 706 热轧等边角钢：边宽 b、边厚 d、内圆弧半径 r（端部圆弧 r1 = d/3）
        static readonly Dictionary<string, double[]> Angles = new Dictionary<string, double[]>
        {
            ["20×3"] = new double[] { 20, 3, 3.5 }, ["25×3"] = new double[] { 25, 3, 3.5 }, ["25×4"] = new double[] { 25, 4, 3.5 }, ["30×3"] = new double[] { 30, 3, 4.5 },
            ["30×4"] = new double[] { 30, 4, 4.5 }, ["40×3"] = new double[] { 40, 3, 5 }, ["40×4"] = new double[] { 40, 4, 5 }, ["40×5"] = new double[] { 40, 5, 5 },
            ["50×4"] = new double[] { 50, 4, 5.5 }, ["50×5"] = new double[] { 50, 5, 5.5 }, ["63×5"] = new double[] { 63, 5, 7 }, ["63×6"] = new double[] { 63, 6, 7 },
            ["75×6"] = new double[] { 75, 6, 9 }, ["75×8"] = new double[] { 75, 8, 9 },
        };
        // GB/T 706 热轧槽钢：高 h、腿宽 b、腰厚 d、平均腿厚 t、内圆弧 r、腿端圆弧 r1
        static readonly Dictionary<string, double[]> Channels = new Dictionary<string, double[]>
        {
            ["5"] = new double[] { 50, 37, 4.5, 7, 7, 3.5 }, ["6.3"] = new double[] { 63, 40, 4.8, 7.5, 7.5, 3.8 }, ["8"] = new double[] { 80, 43, 5, 8, 8, 4 },
            ["10"] = new double[] { 100, 48, 5.3, 8.5, 8.5, 4.2 }, ["12.6"] = new double[] { 126, 53, 5.5, 9, 9, 4.5 }, ["14a"] = new double[] { 140, 58, 6, 9.5, 9.5, 4.8 },
            ["16a"] = new double[] { 160, 63, 6.5, 10, 10, 5 },
        };
        // GB/T 706 热轧工字钢：高 h、腿宽 b、腰厚 d、平均腿厚 t、内圆弧 r、腿端圆弧 r1
        static readonly Dictionary<string, double[]> IBeams = new Dictionary<string, double[]>
        {
            ["10"] = new double[] { 100, 68, 4.5, 7.6, 6.5, 3.3 }, ["12.6"] = new double[] { 126, 74, 5, 8.4, 7, 3.5 }, ["14"] = new double[] { 140, 80, 5.5, 9.1, 7.5, 3.8 },
            ["16"] = new double[] { 160, 88, 6, 9.9, 8, 4 }, ["18"] = new double[] { 180, 94, 6.5, 10.7, 8.5, 4.3 }, ["20a"] = new double[] { 200, 100, 7, 11.4, 9, 4.5 },
        };

        /// <summary>等边角钢截面（外角在原点，两边沿 +x 和 +y）。</summary>
        static Loop AngleSection(double b, double d, double r)
        {
            double r1 = d / 3;
            return new PathBuilder(new V2(0, 0)).Line(b, 0).Line(b, d - r1).Arc(new V2(b - r1, d - r1), new V2(b - r1, d))
                .Line(d + r, d).Arc(new V2(d + r, d + r), new V2(d, d + r), cw: true)
                .Line(d, b - r1).Arc(new V2(d - r1, b - r1), new V2(d - r1, b)).Line(0, b).Close();
        }

        /// <summary>槽钢截面（腰在 x = 0..d，开口朝 +x，上下对称）。翼缘按平均厚度画，不画斜度。</summary>
        static Loop ChannelSection(double h, double b, double d, double t, double r, double r1)
        {
            double y0 = -h / 2, y1 = h / 2;
            return new PathBuilder(new V2(0, y0)).Line(b, y0).Line(b, y0 + t - r1).Arc(new V2(b - r1, y0 + t - r1), new V2(b - r1, y0 + t))
                .Line(d + r, y0 + t).Arc(new V2(d + r, y0 + t + r), new V2(d, y0 + t + r), cw: true)
                .Line(d, y1 - t - r).Arc(new V2(d + r, y1 - t - r), new V2(d + r, y1 - t), cw: true)
                .Line(b - r1, y1 - t).Arc(new V2(b - r1, y1 - t + r1), new V2(b, y1 - t + r1))
                .Line(b, y1).Line(0, y1).Close();
        }

        /// <summary>工字钢截面（中心在原点）。翼缘按平均厚度画，不画斜度。</summary>
        static Loop IBeamSection(double h, double b, double d, double t, double r, double r1)
        {
            double y0 = -h / 2, y1 = h / 2, x1 = b / 2, w = d / 2;
            return new PathBuilder(new V2(-x1, y0)).Line(x1, y0).Line(x1, y0 + t - r1).Arc(new V2(x1 - r1, y0 + t - r1), new V2(x1 - r1, y0 + t))
                .Line(w + r, y0 + t).Arc(new V2(w + r, y0 + t + r), new V2(w, y0 + t + r), cw: true)
                .Line(w, y1 - t - r).Arc(new V2(w + r, y1 - t - r), new V2(w + r, y1 - t), cw: true)
                .Line(x1 - r1, y1 - t).Arc(new V2(x1 - r1, y1 - t + r1), new V2(x1, y1 - t + r1))
                .Line(x1, y1).Line(-x1, y1).Line(-x1, y1 - t + r1).Arc(new V2(-x1 + r1, y1 - t + r1), new V2(-x1 + r1, y1 - t))
                .Line(-w - r, y1 - t).Arc(new V2(-w - r, y1 - t - r), new V2(-w, y1 - t - r), cw: true)
                .Line(-w, y0 + t + r).Arc(new V2(-w - r, y0 + t + r), new V2(-w - r, y0 + t), cw: true)
                .Line(-x1 + r1, y0 + t).Arc(new V2(-x1 + r1, y0 + t - r1), new V2(-x1, y0 + t - r1))
                .Close();
        }

        /// <summary>型材角码几何：外角在原点，一条边沿 +X（厚度向 +Y），另一条边沿 +Y（厚度向 +X），宽度沿 Z 居中。</summary>
        internal static PartGeom BracketGeom(string series)
        {
            var t0 = Brackets[series];
            double L = t0[0] * MM, t = t0[1] * MM, hole = t0[2] * MM, w = L, c = t + (L - t) / 2, g = Math.Min(0.3 * L, c - hole / 2 - t - 0.5 * MM);
            var section = Loop.Polygon(new V2(0, 0), new V2(L, 0), new V2(L, t), new V2(t + g, t), new V2(t, t + g), new V2(t, L), new V2(0, L));
            return new PartGeom()
                .Add(Op.Extrude("角码", -w / 2, w, section).Along(Axis.Z))
                .Add(Op.Cut("孔 1", t, t, Loop.Circle(new V2(c, 0), hole / 2)))
                .Add(Op.Cut("孔 2", t, t, Loop.Circle(new V2(0, c), hole / 2)).Along(Axis.X));
        }

        static IEnumerable<PartType> Structure() => new List<PartType>
        {
            new PartType
            {
                Id = "corner_bracket", Category = "型材", Name = "型材角码", Standard = null, Keywords = "角码 角件 连接件 L 型 corner bracket 2020",
                Note = "压铸角码的简化外形：L 形加内侧加强筋，两条边各一个安装孔。外角在原点，两边沿 +X、+Y。",
                Params = { Choice("series", "系列", _ => Brackets.Keys.ToArray(), "2020") },
                Size = p => p["series"],
                Material = _ => "aluminum",
                Build = p => BracketGeom(p["series"]),
            },
            new PartType
            {
                Id = "profile_cap", Category = "型材", Name = "型材端盖", Standard = null, Keywords = "端盖 堵头 型材盖 end cap",
                Note = "盖板加插进中心孔的定位柱（简化外形）。",
                Params = { Choice("series", "系列", _ => ProfileNames, "2020") },
                Size = p => p["series"],
                Material = _ => "abs",
                Build = p =>
                {
                    var (cell, nx, ny) = ProfileSeries[p["series"]];
                    var c = ProfileCell[cell];
                    double a = c[0] * MM, W = a * nx, H = a * ny, rp = c[6] / 2 * MM - 0.05 * MM, plug = 4 * MM;
                    var g = new PartGeom().Add(Op.Extrude("盖板", 0, 2 * MM, Loop.RoundedRect(W, H, 1.5 * MM)));
                    for (int i = 0; i < nx; i++)
                        for (int j = 0; j < ny; j++)
                            g.Add(Op.Extrude("定位柱", -plug, plug, Loop.Circle(new V2(-W / 2 + a / 2 + i * a, -H / 2 + a / 2 + j * a), rp)));
                    return g;
                },
            },
            new PartType
            {
                Id = "rect_tube", Category = "结构件", Name = "方管 / 矩形管", Standard = null, Keywords = "方管 矩形管 方通 square tube",
                Note = "冷弯方管、矩形管。外圆角取 2 倍壁厚，内圆角取壁厚。长度沿 Y。",
                Params =
                {
                    Number("w", "宽", 5, 400, 20, "mm"), Number("h", "高", 5, 400, 20, "mm"), Number("t", "壁厚", 0.5, 16, 1.5, "mm"),
                    Number("len", "长度", 5, 6000, 200, "mm"), MaterialParam("steel", "steel", "stainless", "aluminum"),
                },
                Size = p => $"{p["w"]}×{p["h"]}×{p["t"]} L{p["len"]}",
                Material = MaterialFromParam("steel", "stainless", "aluminum"),
                Build = p =>
                {
                    double w = D(p, "w") * MM, h = D(p, "h") * MM, t = D(p, "t") * MM;
                    if (t * 2 >= Math.Min(w, h) - 1 * MM) throw new ArgumentException("壁厚太大");
                    return new PartGeom().Add(Op.Extrude("方管", 0, D(p, "len") * MM, Loop.RoundedRect(w, h, 2 * t), Loop.RoundedRect(w - 2 * t, h - 2 * t, t)));
                },
            },
            new PartType
            {
                Id = "round_tube", Category = "结构件", Name = "圆管", Standard = null, Keywords = "圆管 钢管 铝管 碳管 round tube pipe",
                Note = "外径 × 壁厚。长度沿 Y。",
                Params =
                {
                    Number("d", "外径", 2, 500, 20, "mm"), Number("t", "壁厚", 0.2, 30, 1.5, "mm"),
                    Number("len", "长度", 5, 6000, 200, "mm"), MaterialParam("steel", "steel", "stainless", "aluminum", "brass"),
                },
                Size = p => $"Φ{p["d"]}×{p["t"]} L{p["len"]}",
                Material = MaterialFromParam("steel", "stainless", "aluminum", "brass"),
                Build = p =>
                {
                    double d = D(p, "d") * MM, t = D(p, "t") * MM;
                    if (t * 2 >= d - 0.2 * MM) throw new ArgumentException("壁厚太大");
                    return new PartGeom().Add(Op.Extrude("圆管", 0, D(p, "len") * MM, Loop.Circle(d / 2), Loop.Circle(d / 2 - t)));
                },
            },
            new PartType
            {
                Id = "angle_steel", Category = "结构件", Name = "等边角钢", Standard = "GB/T 706", Keywords = "角钢 角铁 angle steel L 型钢",
                Note = "热轧等边角钢，带内圆弧和边端圆弧（r1 = d/3），截面积与国标表一致。外角在原点，长度沿 Y。",
                Params = { Choice("size", "规格", _ => Angles.Keys.ToArray(), "40×4", "mm", "边宽 × 边厚"), Number("len", "长度", 5, 12000, 500, "mm") },
                Size = p => $"∠{p["size"]} L{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var a = Angles[p["size"]];
                    return new PartGeom().Add(Op.Extrude("角钢", 0, D(p, "len") * MM, AngleSection(a[0] * MM, a[1] * MM, a[2] * MM)));
                },
            },
            new PartType
            {
                Id = "channel_steel", Category = "结构件", Name = "槽钢", Standard = "GB/T 706", Keywords = "槽钢 U 型钢 channel",
                Note = "热轧槽钢，翼缘按平均厚度画（不画 1:10 斜度），截面积与国标相差 1% 以内。长度沿 Y，开口朝 +X。",
                Params = { Choice("size", "型号", _ => Channels.Keys.ToArray(), "10", null, "号数 = 高度（cm）"), Number("len", "长度", 5, 12000, 500, "mm") },
                Size = p => $"[{p["size"]} L{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var c = Channels[p["size"]].Select(v => v * MM).ToArray();
                    return new PartGeom().Add(Op.Extrude("槽钢", 0, D(p, "len") * MM, ChannelSection(c[0], c[1], c[2], c[3], c[4], c[5])));
                },
            },
            new PartType
            {
                Id = "i_beam", Category = "结构件", Name = "工字钢", Standard = "GB/T 706", Keywords = "工字钢 I 型钢 i-beam",
                Note = "热轧工字钢，翼缘按平均厚度画（不画斜度）。长度沿 Y。",
                Params = { Choice("size", "型号", _ => IBeams.Keys.ToArray(), "10", null, "号数 = 高度（cm）"), Number("len", "长度", 5, 12000, 500, "mm") },
                Size = p => $"I{p["size"]} L{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var c = IBeams[p["size"]].Select(v => v * MM).ToArray();
                    return new PartGeom().Add(Op.Extrude("工字钢", 0, D(p, "len") * MM, IBeamSection(c[0], c[1], c[2], c[3], c[4], c[5])));
                },
            },
            new PartType
            {
                Id = "bar_stock", Category = "结构件", Name = "棒料", Standard = null, Keywords = "圆钢 方钢 扁钢 六角钢 棒料 圆棒 bar",
                Note = "圆棒、方棒、扁钢（宽 × 厚）、六角棒（对边）。长度沿 Y。",
                Params =
                {
                    Choice("shape", "形状", _ => new[] { "圆棒", "方棒", "扁钢", "六角棒" }, "圆棒"),
                    Number("a", "尺寸", 1, 300, 20, "mm", "圆棒 = 直径，方棒 = 边长，扁钢 = 宽，六角棒 = 对边"),
                    Number("b", "厚度", 0.5, 100, 5, "mm", "只对扁钢有效"),
                    Number("len", "长度", 2, 6000, 100, "mm"), MaterialParam("steel", "steel", "stainless", "aluminum", "brass"),
                },
                Size = p => (p["shape"] == "圆棒" ? $"Φ{p["a"]}" : p["shape"] == "方棒" ? $"□{p["a"]}" : p["shape"] == "扁钢" ? $"{p["a"]}×{p["b"]}" : $"六角{p["a"]}") + $" L{p["len"]}",
                Material = MaterialFromParam("steel", "stainless", "aluminum", "brass"),
                Build = p =>
                {
                    double a = D(p, "a") * MM, b = D(p, "b") * MM, L = D(p, "len") * MM;
                    Loop s;
                    switch (p["shape"])
                    {
                        case "方棒": s = Loop.Rect(a, a); break;
                        case "扁钢": if (b >= a) throw new ArgumentException("扁钢的厚度要小于宽度"); s = Loop.Rect(a, b); break;
                        case "六角棒": s = Loop.Hexagon(a); break;
                        default: s = Loop.Circle(a / 2); break;
                    }
                    return new PartGeom().Add(Op.Extrude("棒料", 0, L, s));
                },
            },
            new PartType
            {
                Id = "plate", Category = "结构件", Name = "板材", Standard = null, Keywords = "板 钢板 铝板 底板 安装板 plate",
                Note = "长 × 宽 × 厚的板，可以加圆角和孔（四角孔或矩形阵列孔，孔边距从板边量到孔中心）。厚度沿 Y。",
                Params =
                {
                    Number("l", "长", 5, 3000, 100, "mm"), Number("w", "宽", 5, 3000, 60, "mm"), Number("t", "厚", 0.3, 100, 3, "mm"),
                    Number("r", "圆角", 0, 200, 3, "mm"),
                    Choice("holes", "孔", _ => new[] { "无", "四角孔", "阵列孔" }, "四角孔"),
                    Number("hd", "孔径", 1, 100, 3.4, "mm", "M3 过孔 3.4，M4 4.5，M5 5.5，M6 6.6"),
                    Number("e", "孔边距", 1, 500, 6, "mm"),
                    Number("nx", "长向孔数", 1, 50, 3, "", "阵列孔用"), Number("ny", "宽向孔数", 1, 50, 2, "", "阵列孔用"),
                    MaterialParam("aluminum", "aluminum", "steel", "stainless", "abs", "pla"),
                },
                Size = p => $"{p["l"]}×{p["w"]}×{p["t"]}" + (p["holes"] == "无" ? "" : p["holes"] == "四角孔" ? $" 4-Φ{p["hd"]}" : $" {p["nx"]}×{p["ny"]}-Φ{p["hd"]}") + (D(p, "r") > 0 ? $" R{p["r"]}" : ""),
                Material = MaterialFromParam("aluminum", "steel", "stainless", "abs", "pla"),
                Build = p =>
                {
                    double l = D(p, "l") * MM, w = D(p, "w") * MM, t = D(p, "t") * MM, r = D(p, "r") * MM, hd = D(p, "hd") * MM, e = D(p, "e") * MM;
                    var loops = new List<Loop> { Loop.RoundedRect(l, w, r) };
                    if (p["holes"] != "无")
                    {
                        int nx = p["holes"] == "四角孔" ? 2 : I(p, "nx"), ny = p["holes"] == "四角孔" ? 2 : I(p, "ny");
                        if (e - hd / 2 < 0.5 * MM) throw new ArgumentException("孔边距太小：孔会碰到板边");
                        if (2 * e >= l || 2 * e >= w) throw new ArgumentException("孔边距太大");
                        double sx = nx > 1 ? (l - 2 * e) / (nx - 1) : 0, sy = ny > 1 ? (w - 2 * e) / (ny - 1) : 0;
                        if (nx > 1 && sx < hd + 0.5 * MM || ny > 1 && sy < hd + 0.5 * MM) throw new ArgumentException("孔太密：相邻孔会重叠");
                        // 圆角处的孔不能碰到圆弧
                        if (r > 0 && Math.Sqrt(2) * (r - e) + hd / 2 > r - 0.3 * MM && e < r) throw new ArgumentException("四角的孔离圆角太近：加大孔边距或减小圆角");
                        loops.AddRange(HoleGrid(sx, sy, hd, nx, ny));
                    }
                    return new PartGeom().Add(Op.Extrude("板", 0, t, loops));
                },
            },
        };
    }
}
