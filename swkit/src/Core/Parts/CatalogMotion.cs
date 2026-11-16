using System;
using System.Collections.Generic;
using System.Linq;

namespace SwKit.Core.Parts
{
    /// <summary>轴承和直线运动件：法兰轴承、推力轴承、带座轴承、直线轴承、光轴、光轴支座、导轨和滑块、丝杆和螺母。</summary>
    public static partial class Catalog
    {
        // 法兰深沟球轴承：内径 d、外径 D、宽 B、法兰外径 Df、法兰厚 Bf
        static readonly Dictionary<string, double[]> FlangeBearing = new Dictionary<string, double[]>
        {
            ["MF63"] = new double[] { 3, 6, 2.5, 7.2, 0.6 }, ["MF74"] = new double[] { 4, 7, 2.5, 8.2, 0.6 }, ["MF85"] = new double[] { 5, 8, 2.5, 9.2, 0.6 },
            ["MF105"] = new double[] { 5, 10, 4, 11.6, 0.8 }, ["MF115"] = new double[] { 5, 11, 4, 12.5, 0.8 }, ["MF126"] = new double[] { 6, 12, 4, 13.6, 0.8 },
            ["MF128"] = new double[] { 8, 12, 3.5, 13.6, 0.8 }, ["F623"] = new double[] { 3, 10, 4, 11.5, 1 }, ["F624"] = new double[] { 4, 13, 5, 15, 1 },
            ["F625"] = new double[] { 5, 16, 5, 18, 1 }, ["F626"] = new double[] { 6, 19, 6, 22, 1.5 }, ["F695"] = new double[] { 5, 13, 4, 15, 1 },
            ["F605"] = new double[] { 5, 14, 5, 16, 1 }, ["F688"] = new double[] { 8, 16, 5, 18, 1.1 }, ["F608"] = new double[] { 8, 22, 7, 25, 1.5 },
        };
        // 推力球轴承：内径 d、外径 D、高 T
        static readonly Dictionary<string, double[]> ThrustBearing = new Dictionary<string, double[]>
        {
            ["F6-12M"] = new double[] { 6, 12, 4.5 }, ["F8-16M"] = new double[] { 8, 16, 5 }, ["F10-18M"] = new double[] { 10, 18, 5.5 },
            ["51100"] = new double[] { 10, 24, 9 }, ["51101"] = new double[] { 12, 26, 9 }, ["51102"] = new double[] { 15, 28, 9 }, ["51103"] = new double[] { 17, 30, 9 },
            ["51104"] = new double[] { 20, 35, 10 }, ["51105"] = new double[] { 25, 42, 11 }, ["51106"] = new double[] { 30, 47, 11 },
        };
        // UCP 立式带座轴承（ISO 3228）：d、中心高 H、底座长 L、螺栓孔距 J、底座宽 A、螺栓孔宽 N、底座厚 H1、总高 H2、内圈宽 B
        static readonly Dictionary<string, double[]> Ucp = new Dictionary<string, double[]>
        {
            ["UCP201"] = new double[] { 12, 30.2, 127, 95, 38, 13, 14, 62, 31 }, ["UCP202"] = new double[] { 15, 30.2, 127, 95, 38, 13, 14, 62, 31 },
            ["UCP203"] = new double[] { 17, 30.2, 127, 95, 38, 13, 14, 62, 31 }, ["UCP204"] = new double[] { 20, 33.3, 127, 95, 38, 13, 14, 65, 31 },
            ["UCP205"] = new double[] { 25, 36.5, 140, 105, 38, 13, 15, 71, 34.1 }, ["UCP206"] = new double[] { 30, 42.9, 165, 121, 48, 17, 17, 83, 38.1 },
        };
        // UCF 方形法兰带座轴承：d、法兰边长 L、孔距 J、总高 A、法兰厚 A1、孔径 N
        static readonly Dictionary<string, double[]> Ucf = new Dictionary<string, double[]>
        {
            ["UCF201"] = new double[] { 12, 86, 64, 25.5, 11, 12 }, ["UCF204"] = new double[] { 20, 86, 64, 25.5, 11, 12 },
            ["UCF205"] = new double[] { 25, 95, 70, 27, 13, 12 }, ["UCF206"] = new double[] { 30, 108, 83, 31, 13, 12 },
        };
        // 直线轴承：内径 dr、外径 D、长 L、卡簧槽外侧间距 B（0 = 无槽）、槽宽 W、槽底径 D1
        static readonly Dictionary<string, double[]> LinearBearing = new Dictionary<string, double[]>
        {
            ["LM6UU"] = new double[] { 6, 12, 19, 13.5, 1.1, 11.5 }, ["LM8UU"] = new double[] { 8, 15, 24, 17.5, 1.1, 14.3 }, ["LM10UU"] = new double[] { 10, 19, 29, 22, 1.3, 18 },
            ["LM12UU"] = new double[] { 12, 21, 30, 23, 1.3, 20 }, ["LM16UU"] = new double[] { 16, 28, 37, 26.5, 1.6, 27 }, ["LM20UU"] = new double[] { 20, 32, 42, 30.5, 1.6, 30.5 },
            ["LM25UU"] = new double[] { 25, 40, 59, 41, 1.85, 38 },
            ["LM6LUU"] = new double[] { 6, 12, 35, 0, 0, 0 }, ["LM8LUU"] = new double[] { 8, 15, 45, 0, 0, 0 }, ["LM10LUU"] = new double[] { 10, 19, 55, 0, 0, 0 },
            ["LM12LUU"] = new double[] { 12, 21, 57, 0, 0, 0 }, ["LM16LUU"] = new double[] { 16, 28, 70, 0, 0, 0 }, ["LM20LUU"] = new double[] { 20, 32, 80, 0, 0, 0 },
        };
        // 圆法兰直线轴承：dr、D、L、法兰外径 Df、法兰厚 t、孔节圆 PCD、孔径
        static readonly Dictionary<string, double[]> LinearFlange = new Dictionary<string, double[]>
        {
            ["LMF6UU"] = new double[] { 6, 12, 19, 28, 5, 20, 3.5 }, ["LMF8UU"] = new double[] { 8, 15, 24, 32, 5, 24, 3.5 }, ["LMF10UU"] = new double[] { 10, 19, 29, 40, 6, 29, 4.5 },
            ["LMF12UU"] = new double[] { 12, 21, 30, 42, 6, 32, 4.5 }, ["LMF16UU"] = new double[] { 16, 28, 37, 48, 6, 38, 4.5 }, ["LMF20UU"] = new double[] { 20, 32, 42, 54, 8, 43, 5.5 },
        };
        // SK 光轴支座：d、中心高 h、总宽 W、厚 L、总高 F、底座厚 G、孔距 P、中间块宽 B、孔径 S
        static readonly Dictionary<string, double[]> ShaftSupport = new Dictionary<string, double[]>
        {
            ["SK8"] = new double[] { 8, 20, 42, 14, 32.8, 6, 32, 18, 5.5 }, ["SK10"] = new double[] { 10, 20, 42, 14, 32.8, 6, 32, 18, 5.5 },
            ["SK12"] = new double[] { 12, 23, 42, 14, 37.5, 6, 32, 20, 5.5 }, ["SK16"] = new double[] { 16, 27, 48, 16, 44, 8, 38, 25, 5.5 },
            ["SK20"] = new double[] { 20, 31, 60, 20, 51, 10, 45, 30, 6.6 },
        };
        // 直线导轨：轨宽 W、轨高 H、孔距 P、通孔 d、沉孔 D、沉孔深 h
        static readonly Dictionary<string, double[]> Rail = new Dictionary<string, double[]>
        {
            ["MGN7"] = new double[] { 7, 4.8, 15, 2.4, 4.2, 2.3 }, ["MGN9"] = new double[] { 9, 6.5, 20, 3.5, 6, 3.5 }, ["MGN12"] = new double[] { 12, 8, 25, 3.5, 6, 4.5 },
            ["MGN15"] = new double[] { 15, 10, 40, 3.5, 6, 4.5 }, ["HGR15"] = new double[] { 15, 15, 60, 4.5, 7.5, 5.3 }, ["HGR20"] = new double[] { 20, 17.5, 60, 6, 9.5, 8.5 },
            ["HGR25"] = new double[] { 23, 22, 60, 7, 11, 9 },
        };
        // 导轨滑块：宽 W、长 L、总高 H（从导轨底面算）、滑块底面高 H1、导轨型号、安装螺纹、横向孔距 B、纵向孔距 C
        static readonly Dictionary<string, (double W, double L, double H, double H1, string rail, string M, double B, double C)> Carriage =
            new Dictionary<string, (double, double, double, double, string, string, double, double)>
        {
            ["MGN7H"] = (17, 30.8, 8, 1.5, "MGN7", "M2", 12, 13), ["MGN9C"] = (20, 28.9, 10, 2, "MGN9", "M3", 15, 10), ["MGN9H"] = (20, 39.9, 10, 2, "MGN9", "M3", 15, 16),
            ["MGN12C"] = (27, 34.7, 13, 3, "MGN12", "M3", 20, 15), ["MGN12H"] = (27, 45.4, 13, 3, "MGN12", "M3", 20, 20), ["MGN15H"] = (32, 58.8, 16, 4, "MGN15", "M3", 25, 25),
            ["HGH15CA"] = (34, 61.4, 28, 4.3, "HGR15", "M4", 26, 26), ["HGH20CA"] = (44, 77.5, 30, 4.6, "HGR20", "M5", 32, 36), ["HGH25CA"] = (48, 84, 40, 5.5, "HGR25", "M6", 35, 35),
        };
        // 丝杆螺母：螺杆直径、法兰外径、法兰厚、削边宽（0 = 不削边）、本体外径、总长、孔节圆、孔数、孔径、材料
        static readonly Dictionary<string, (double d, double Df, double t, double flat, double D, double L, double pcd, int n, double hole, string mat)> ScrewNut =
            new Dictionary<string, (double, double, double, double, double, double, double, int, double, string)>
        {
            ["T8"] = (8, 22, 3.5, 0, 10.2, 15, 16, 4, 3.5, "brass"),
            ["SFU1204"] = (12, 40, 10, 30, 24, 35, 32, 4, 4.5, "steel"),
            ["SFU1605"] = (16, 48, 10, 40, 28, 42, 38, 6, 5.5, "steel"),
            ["SFU1610"] = (16, 48, 10, 40, 28, 57, 38, 6, 5.5, "steel"),
            ["SFU2005"] = (20, 58, 10, 44, 36, 42, 47, 6, 6.6, "steel"),
        };
        static readonly Dictionary<string, double> ScrewRod = new Dictionary<string, double>
        {
            ["T8"] = 8, ["Tr10"] = 10, ["Tr12"] = 12, ["Tr16"] = 16, ["SFU1204"] = 12, ["SFU1605"] = 16, ["SFU1610"] = 16, ["SFU2005"] = 20, ["SFU2505"] = 25,
        };

        /// <summary>深沟球轴承的简化旋转轮廓（带防尘盖凹槽），可以在外圈顶部带法兰。</summary>
        static Loop BearingProfile(double ri, double ro, double B, double rf = 0, double bf = 0)
        {
            double h = ro - ri, a = ri + 0.3 * h, bb = ro - 0.3 * h, e = Math.Max(0.15 * MM, 0.08 * B), c = Math.Min(0.3 * MM, 0.08 * h);
            var pb = new PathBuilder(new V2(ri + c, 0)).Line(a, 0).Line(a, e).Line(bb, e).Line(bb, 0).Line(ro - c, 0).Line(ro, c);
            if (rf > ro && bf > 0) pb.Line(ro, B - bf).Line(rf, B - bf).Line(rf, B);
            else pb.Line(ro, B - c).Line(ro - c, B);
            return pb.Line(bb, B).Line(bb, B - e).Line(a, B - e).Line(a, B).Line(ri + c, B).Line(ri, B - c).Line(ri, c).Close();
        }

        static IEnumerable<PartType> Motion() => new List<PartType>
        {
            new PartType
            {
                Id = "flange_bearing", Category = "轴承", Name = "法兰轴承", Standard = null, Keywords = "法兰轴承 带边轴承 flanged bearing MF F608",
                Note = "外圈一端带法兰的深沟球轴承，简化外形（不含滚珠）。法兰在 +Y 一端。",
                Params = { Choice("model", "型号", _ => FlangeBearing.Keys.ToArray(), "F608") },
                Size = p => { var b = FlangeBearing[p["model"]]; return $"{p["model"]}（{F(b[0])}×{F(b[1])}×{F(b[2])}）"; },
                Material = _ => "steel",
                Build = p =>
                {
                    var b = FlangeBearing[p["model"]].Select(v => v * MM).ToArray();
                    return new PartGeom().Add(Op.Revolve("轴承", BearingProfile(b[0] / 2, b[1] / 2, b[2], b[3] / 2, b[4])));
                },
            },
            new PartType
            {
                Id = "thrust_bearing", Category = "轴承", Name = "推力球轴承", Standard = null, Keywords = "推力轴承 平面轴承 thrust 51100",
                Note = "51100 系列和 F 系列微型推力轴承，简化外形：两片垫圈之间画出保持架的凹槽。",
                Params = { Choice("model", "型号", _ => ThrustBearing.Keys.ToArray(), "51100") },
                Size = p => { var b = ThrustBearing[p["model"]]; return $"{p["model"]}（{F(b[0])}×{F(b[1])}×{F(b[2])}）"; },
                Material = _ => "steel",
                Build = p =>
                {
                    var b = ThrustBearing[p["model"]].Select(v => v * MM).ToArray();
                    double ri = b[0] / 2, ro = b[1] / 2, T = b[2], h = ro - ri, r1 = ri + 0.25 * h, r2 = ro - 0.25 * h;
                    return new PartGeom().Add(Op.Revolve("轴承", Loop.Polygon(new V2(ri, 0), new V2(ro, 0), new V2(ro, 0.35 * T), new V2(r2, 0.35 * T), new V2(r2, 0.65 * T),
                        new V2(ro, 0.65 * T), new V2(ro, T), new V2(ri, T), new V2(ri, 0.65 * T), new V2(r1, 0.65 * T), new V2(r1, 0.35 * T), new V2(ri, 0.35 * T))));
                },
            },
            new PartType
            {
                Id = "ucp_block", Category = "轴承", Name = "立式带座轴承", Standard = "UCP", Keywords = "带座轴承 立式轴承座 pillow block UCP204",
                Note = "按 ISO 3228 的外形尺寸：底座、轴承座、两侧伸出的内圈，底座两个长圆螺栓孔。轴线沿 Z，中心高 H。座体为简化外形。",
                Params = { Choice("model", "型号", _ => Ucp.Keys.ToArray(), "UCP204") },
                Size = p => $"{p["model"]}（轴径 {F(Ucp[p["model"]][0])}）",
                Material = _ => "castiron",
                Build = p =>
                {
                    var u = Ucp[p["model"]].Select(v => v * MM).ToArray();
                    double d = u[0], H = u[1], L = u[2], J = u[3], A = u[4], N = u[5], H1 = u[6], H2 = u[7], B = u[8];
                    double Rh = H2 - H, wb = Math.Min(Rh + 4 * MM, J / 2 - N - 1 * MM) * 2, A1 = 0.55 * A, rin = d / 2 + 4 * MM;
                    var body = new PathBuilder(new V2(wb / 2, H1)).Line(Rh, H).Arc(new V2(0, H), new V2(-Rh, H)).Line(-wb / 2, H1).Close();
                    var g = new PartGeom()
                        .Add(Op.Extrude("底座", -A / 2, A, Loop.Rect(L, H1, new V2(0, H1 / 2))).Along(Axis.Z))
                        .Add(Op.Extrude("轴承座", -A1 / 2, A1, body, Loop.Circle(new V2(0, H), d / 2)).Along(Axis.Z))
                        .Add(Op.Cut("螺栓孔", H1, H1, Loop.Slot(1.4 * N, N, new V2(-J / 2, 0)), Loop.Slot(1.4 * N, N, new V2(J / 2, 0))));
                    if (B > A1)
                    {
                        var ring = new[] { Loop.Circle(new V2(0, H), rin), Loop.Circle(new V2(0, H), d / 2) };
                        g.Add(Op.Extrude("内圈", A1 / 2, (B - A1) / 2, ring).Along(Axis.Z)).Add(Op.Extrude("内圈 ", -B / 2, (B - A1) / 2, ring).Along(Axis.Z));
                    }
                    return g;
                },
            },
            new PartType
            {
                Id = "ucf_block", Category = "轴承", Name = "方形法兰带座轴承", Standard = "UCF", Keywords = "带座轴承 法兰座 菱形座 UCF204 flange block",
                Note = "按常用外形尺寸：方形法兰带 4 个安装孔、轴承座凸台。轴线沿 Y。",
                Params = { Choice("model", "型号", _ => Ucf.Keys.ToArray(), "UCF204") },
                Size = p => $"{p["model"]}（轴径 {F(Ucf[p["model"]][0])}）",
                Material = _ => "castiron",
                Build = p =>
                {
                    var u = Ucf[p["model"]].Select(v => v * MM).ToArray();
                    double d = u[0], L = u[1], J = u[2], A = u[3], A1 = u[4], N = u[5];
                    var flange = new List<Loop> { Loop.RoundedRect(L, L, 0.12 * L), Loop.Circle(d / 2) };
                    flange.AddRange(HoleGrid(J, J, N));
                    return new PartGeom()
                        .Add(Op.Extrude("法兰", 0, A1, flange))
                        .Add(Op.Extrude("轴承座", A1, A - A1, Loop.Circle(Math.Min(J / 2 - N / 2 - 2 * MM, d / 2 + 18 * MM)), Loop.Circle(d / 2)));
                },
            },
            new PartType
            {
                Id = "linear_bearing", Category = "直线运动", Name = "直线轴承", Standard = "LM-UU", Keywords = "直线轴承 线性轴承 LM8UU linear bearing",
                Note = "标准型带两道卡簧槽；加长型（LUU）不画槽。外形按 THK / 国产通用尺寸。",
                Params = { Choice("model", "型号", _ => LinearBearing.Keys.ToArray(), "LM8UU") },
                Size = p => { var b = LinearBearing[p["model"]]; return $"{p["model"]}（{F(b[0])}×{F(b[1])}×{F(b[2])}）"; },
                Material = _ => "steel",
                Build = p =>
                {
                    var b = LinearBearing[p["model"]].Select(v => v * MM).ToArray();
                    double ri = b[0] / 2, ro = b[1] / 2, L = b[2], B = b[3], W = b[4], rg = b[5] / 2;
                    if (B <= 0) return new PartGeom().Add(Op.Revolve("轴承", Loop.Polygon(new V2(ri, 0), new V2(ro, 0), new V2(ro, L), new V2(ri, L))));
                    double g1 = (L - B) / 2, g2 = (L + B) / 2;
                    return new PartGeom().Add(Op.Revolve("轴承", Loop.Polygon(new V2(ri, 0), new V2(ro, 0), new V2(ro, g1), new V2(rg, g1), new V2(rg, g1 + W), new V2(ro, g1 + W),
                        new V2(ro, g2 - W), new V2(rg, g2 - W), new V2(rg, g2), new V2(ro, g2), new V2(ro, L), new V2(ri, L))));
                },
            },
            new PartType
            {
                Id = "linear_flange", Category = "直线运动", Name = "法兰直线轴承", Standard = "LMF-UU", Keywords = "法兰直线轴承 圆法兰 LMF8UU",
                Note = "圆法兰型直线轴承，法兰上 4 个安装孔。法兰在底部（Y = 0）。",
                Params = { Choice("model", "型号", _ => LinearFlange.Keys.ToArray(), "LMF8UU") },
                Size = p => p["model"],
                Material = _ => "steel",
                Build = p =>
                {
                    var b = LinearFlange[p["model"]].Select(v => v * MM).ToArray();
                    var flange = new List<Loop> { Loop.Circle(b[3] / 2), Loop.Circle(b[0] / 2) };
                    flange.AddRange(HoleCircle(b[5], 4, b[6], Math.PI / 4));
                    return new PartGeom().Add(Op.Extrude("法兰", 0, b[4], flange)).Add(Op.Extrude("本体", b[4], b[2] - b[4], Loop.Circle(b[1] / 2), Loop.Circle(b[0] / 2)));
                },
            },
            new PartType
            {
                Id = "linear_shaft", Category = "直线运动", Name = "光轴", Standard = null, Keywords = "光轴 导向轴 直线轴 圆棒 linear shaft rod",
                Note = "镀铬直线光轴（GCr15，h7），两端倒角。",
                Params =
                {
                    Choice("d", "直径", _ => new[] { "3", "4", "5", "6", "8", "10", "12", "13", "15", "16", "20", "25", "30" }, "8", "mm"),
                    Number("len", "长度", 5, 3000, 200, "mm"),
                },
                Size = p => $"Φ{p["d"]} L{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    double d = D(p, "d") * MM, c = (d < 6 * MM ? 0.3 : 0.5) * MM;
                    return new PartGeom().Add(Op.Revolve("光轴", ChamferedRod(d / 2, D(p, "len") * MM, c, c)));
                },
            },
            new PartType
            {
                Id = "shaft_support", Category = "直线运动", Name = "光轴支座", Standard = "SK", Keywords = "光轴支座 立式 SK8 shaft support",
                Note = "立式光轴支座外形（不画夹紧槽和夹紧螺钉）。轴线沿 Z，底面在 Y = 0。",
                Params = { Choice("model", "型号", _ => ShaftSupport.Keys.ToArray(), "SK8") },
                Size = p => p["model"],
                Material = _ => "aluminum",
                Build = p =>
                {
                    var s = ShaftSupport[p["model"]].Select(v => v * MM).ToArray();
                    double d = s[0], h = s[1], W = s[2], L = s[3], Ft = s[4], G = s[5], P = s[6], B = s[7], S = s[8];
                    var outline = Loop.Polygon(new V2(-W / 2, 0), new V2(W / 2, 0), new V2(W / 2, G), new V2(B / 2, G), new V2(B / 2, Ft), new V2(-B / 2, Ft), new V2(-B / 2, G), new V2(-W / 2, G));
                    return new PartGeom()
                        .Add(Op.Extrude("支座", -L / 2, L, outline, Loop.Circle(new V2(0, h), d / 2)).Along(Axis.Z))
                        .Add(Op.Cut("安装孔", G, G, Loop.Circle(new V2(-P / 2, 0), S / 2), Loop.Circle(new V2(P / 2, 0), S / 2)));
                },
            },
            new PartType
            {
                Id = "linear_rail", Category = "直线运动", Name = "直线导轨", Standard = null, Keywords = "导轨 线轨 MGN12 HGR20 linear guide rail",
                Note = "MGN（微型）和 HGR（重载）导轨。侧面滚道按矩形凹槽简化。沿 X 方向，安装孔从两端对称排布。",
                Params =
                {
                    Choice("series", "型号", _ => Rail.Keys.ToArray(), "MGN12"),
                    Number("len", "长度", 30, 4000, 300, "mm"),
                },
                Size = p => $"{p["series"]} L{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var r = Rail[p["series"]].Select(v => v * MM).ToArray();
                    double W = r[0], H = r[1], P = r[2], d = r[3], Dc = r[4], hc = r[5], L = D(p, "len") * MM;
                    double gd = 0.12 * W, g0 = 0.55 * H, g1 = 0.75 * H;
                    var section = Loop.Polygon(new V2(-W / 2, 0), new V2(W / 2, 0), new V2(W / 2, g0), new V2(W / 2 - gd, g0), new V2(W / 2 - gd, g1), new V2(W / 2, g1),
                                               new V2(W / 2, H), new V2(-W / 2, H), new V2(-W / 2, g1), new V2(-W / 2 + gd, g1), new V2(-W / 2 + gd, g0), new V2(-W / 2, g0));
                    int n = Math.Max(1, (int)Math.Floor((L - 2 * Math.Max(Dc, 0.25 * P)) / P) + 1);
                    var xs = Enumerable.Range(0, n).Select(i => (L - (n - 1) * P) / 2 + i * P).ToList();
                    return new PartGeom()
                        .Add(Op.Extrude("导轨", 0, L, section).Along(Axis.X))
                        .Add(Op.Cut("沉孔", H, hc, xs.Select(x => Loop.Circle(new V2(x, 0), Dc / 2))))
                        .Add(Op.Cut("通孔", H - hc, H - hc, xs.Select(x => Loop.Circle(new V2(x, 0), d / 2))));
                },
            },
            new PartType
            {
                Id = "rail_carriage", Category = "直线运动", Name = "导轨滑块", Standard = null, Keywords = "滑块 线轨滑块 MGN12H carriage block",
                Note = "按 MGN / HGH 外形尺寸画成带槽的方块（不含端盖、油嘴、滚珠）。坐标原点在导轨底面中心，可以直接和同型号导轨配合。",
                Params = { Choice("model", "型号", _ => Carriage.Keys.ToArray(), "MGN12H") },
                Size = p => p["model"],
                Material = _ => "steel",
                Build = p =>
                {
                    var c = Carriage[p["model"]];
                    var r = Rail[c.rail];
                    double W = c.W * MM, L = c.L * MM, H = c.H * MM, H1 = c.H1 * MM, rw = r[0] * MM / 2 + 0.3 * MM, rh = r[1] * MM + 0.3 * MM;
                    var section = Loop.Polygon(new V2(-W / 2, H1), new V2(-rw, H1), new V2(-rw, rh), new V2(rw, rh), new V2(rw, H1), new V2(W / 2, H1), new V2(W / 2, H), new V2(-W / 2, H));
                    return new PartGeom()
                        .Add(Op.Extrude("滑块", -L / 2, L, section).Along(Axis.X))
                        .Add(Op.Cut("安装孔", H, H - rh - 0.5 * MM, HoleGrid(c.C * MM, c.B * MM, Nominal(c.M) * MM)));
                },
            },
            new PartType
            {
                Id = "screw_rod", Category = "直线运动", Name = "丝杆", Standard = null, Keywords = "丝杆 梯形丝杆 滚珠丝杆 T8 SFU1605 lead screw ball screw",
                Note = "梯形丝杆（T8 / Tr）和滚珠丝杆（SFU）按公称直径画光杆，不画螺纹和端部加工。",
                Params =
                {
                    Choice("series", "型号", _ => ScrewRod.Keys.ToArray(), "T8"),
                    Number("len", "长度", 20, 3000, 300, "mm"),
                },
                Size = p => $"{p["series"]} L{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    double d = ScrewRod[p["series"]] * MM, c = 0.5 * MM;
                    return new PartGeom().Add(Op.Revolve("丝杆", ChamferedRod(d / 2, D(p, "len") * MM, c, c)));
                },
            },
            new PartType
            {
                Id = "screw_nut", Category = "直线运动", Name = "丝杆螺母", Standard = null, Keywords = "丝杆螺母 铜螺母 滚珠螺母 T8 SFU1605 法兰螺母",
                Note = "T8 黄铜法兰螺母和 SFU 滚珠丝杆螺母（DIN 69051 外形，法兰两侧削边）。孔按螺杆公称直径画。法兰在底部。",
                Params = { Choice("model", "型号", _ => ScrewNut.Keys.ToArray(), "T8") },
                Size = p => p["model"],
                Material = p => ScrewNut[p["model"]].mat,
                Build = p =>
                {
                    var s = ScrewNut[p["model"]];
                    double d = s.d * MM, Rf = s.Df / 2 * MM, t = s.t * MM, L = s.L * MM;
                    var flange = new List<Loop> { s.flat > 0 ? Loop.DoubleD(Rf, s.flat / 2 * MM) : Loop.Circle(Rf), Loop.Circle(d / 2) };
                    var holes = HoleCircle(s.pcd * MM, s.n, s.hole * MM, s.n == 6 ? Math.PI / 6 : Math.PI / 4).ToList();
                    // 孔碰到削边时改成 4 个孔（45° 方向）
                    if (s.flat > 0 && holes.Any(h => Math.Abs(h.Center.X) + h.Radius > s.flat / 2 * MM - 0.5 * MM))
                        holes = HoleCircle(s.pcd * MM, 4, s.hole * MM, Math.PI / 4).ToList();
                    flange.AddRange(holes);
                    return new PartGeom().Add(Op.Extrude("法兰", 0, t, flange)).Add(Op.Extrude("本体", t, L - t, Loop.Circle(s.D / 2 * MM), Loop.Circle(d / 2)));
                },
            },
        };
    }
}
