using System;
using System.Collections.Generic;
using System.Linq;

namespace SwKit.Core.Parts
{
    /// <summary>传动件：同步带轮、齿条、链轮、联轴器、平键、挡圈、O 型圈。</summary>
    public static partial class Catalog
    {
        /// <summary>同步带齿形：节距 p、节线差 2u（外径 = 节径 − 2u）、齿槽深 hd、齿槽圆弧半径 rn、挡边厚、可选齿数、可选带宽。</summary>
        internal sealed class BeltProfile
        {
            public double Pitch, Pld, Depth, Rn, FlangeT, FlangeExtra; public string[] Teeth, Widths; public double[] Lengths;
            public double PitchDiameter(int z) => z * Pitch / Math.PI;
            public double OuterDiameter(int z) => PitchDiameter(z) - Pld;
        }
        internal static readonly Dictionary<string, BeltProfile> Belts = new Dictionary<string, BeltProfile>
        {
            ["GT2"] = new BeltProfile
            {
                Pitch = 2, Pld = 0.508, Depth = 0.75, Rn = 0.555, FlangeT = 1, FlangeExtra = 1.75,
                Teeth = new[] { "16", "20", "24", "25", "30", "32", "36", "40", "48", "50", "60", "72", "80" }, Widths = new[] { "6", "9", "10", "15" },
                Lengths = new double[] { 100, 110, 112, 120, 122, 140, 150, 158, 160, 166, 180, 188, 200, 202, 210, 220, 232, 240, 250, 252, 260, 270, 280, 288, 300, 310,
                                         320, 330, 348, 350, 360, 376, 380, 400, 410, 420, 440, 450, 474, 480, 500, 510, 524, 550, 576, 600, 610, 640, 696, 710, 752, 800, 852, 900, 1000 },
            },
            ["HTD3M"] = new BeltProfile
            {
                Pitch = 3, Pld = 0.762, Depth = 1.14, Rn = 0.86, FlangeT = 1.5, FlangeExtra = 2.5,
                Teeth = new[] { "15", "18", "20", "24", "30", "36", "40", "48", "60", "72" }, Widths = new[] { "6", "9", "10", "15" },
                Lengths = new double[] { 129, 141, 150, 159, 168, 174, 180, 186, 201, 210, 213, 225, 237, 240, 243, 255, 264, 267, 276, 285, 288, 300, 312, 324, 330, 339, 345,
                                         357, 384, 390, 420, 447, 450, 474, 480, 486, 501, 513, 531, 537, 552, 564, 570, 597, 600, 633, 648, 669, 711, 735, 750, 804, 882, 945, 1062 },
            },
            ["HTD5M"] = new BeltProfile
            {
                Pitch = 5, Pld = 1.144, Depth = 1.92, Rn = 1.43, FlangeT = 2, FlangeExtra = 3.5,
                Teeth = new[] { "15", "18", "20", "24", "30", "36", "40", "48", "60", "72" }, Widths = new[] { "10", "15", "20", "25" },
                Lengths = new double[] { 225, 250, 255, 270, 280, 300, 330, 340, 350, 375, 400, 420, 425, 450, 475, 500, 525, 535, 550, 565, 575, 600, 615, 635, 640, 670, 700,
                                         710, 740, 750, 800, 835, 850, 890, 900, 950, 1000, 1050, 1125, 1195, 1270, 1420, 1500 },
            },
        };

        /// <summary>同步带轮几何：底部轮毂、挡边、齿、挡边（从下往上叠放）。返回几何和齿的中间高度（用于装配时对齐皮带）。</summary>
        internal static PartGeom PulleyGeom(string profile, int z, double beltW, double bore, double hubD, double hubL, bool flanges, out double toothMid)
        {
            var b = Belts[profile];
            double R = b.OuterDiameter(z) / 2 * MM, rc = R - b.Depth * MM + b.Rn * MM, rn = b.Rn * MM;
            double tw = (beltW + 1) * MM, ft = flanges ? b.FlangeT * MM : 0, rf = R + b.FlangeExtra * MM;
            if (bore / 2 >= rc - rn - 0.8 * MM) throw new ArgumentException($"孔太大：孔径要小于 {F((rc - rn - 0.8 * MM) * 2 / MM)} mm");
            if (hubD <= 0) hubD = Math.Max(bore + 8 * MM, 0.35 * 2 * R);
            if (hubL > 0 && hubD <= bore + 1 * MM) throw new ArgumentException("轮毂直径要比孔径大 1 mm 以上");
            var g = new PartGeom();
            double y = 0;
            var hole = Loop.Circle(bore / 2);
            if (hubL > 0) { g.Add(Op.Extrude("轮毂", y, hubL, Loop.Circle(hubD / 2), hole)); y += hubL; }
            if (ft > 0) { g.Add(Op.Extrude("挡边", y, ft, Loop.Circle(rf), hole)); y += ft; }
            g.Add(Op.Extrude("齿", y, tw, Loop.Notched(R, z, rc, rn), hole));
            toothMid = y + tw / 2;
            y += tw;
            if (ft > 0) g.Add(Op.Extrude("挡边 ", y, ft, Loop.Circle(rf), hole));
            return g;
        }

        // ISO 606 滚子链：节距 p、滚子直径 d1、内节内宽 b1
        static readonly Dictionary<string, double[]> Chains = new Dictionary<string, double[]>
        {
            ["04C (25)"] = new double[] { 6.35, 3.30, 3.18 }, ["05B"] = new double[] { 8, 5, 3 }, ["06B"] = new double[] { 9.525, 6.35, 5.72 },
            ["08A (40)"] = new double[] { 12.7, 7.92, 7.85 }, ["08B"] = new double[] { 12.7, 8.51, 7.75 }, ["10A (50)"] = new double[] { 15.875, 10.16, 9.4 },
        };
        // 联轴器外形：外径 D、总长 L
        static readonly Dictionary<string, double[]> Couplings = new Dictionary<string, double[]>
        {
            ["D14L22"] = new double[] { 14, 22 }, ["D20L25"] = new double[] { 20, 25 }, ["D20L30"] = new double[] { 20, 30 }, ["D25L30"] = new double[] { 25, 30 },
            ["D30L35"] = new double[] { 30, 35 }, ["D32L40"] = new double[] { 32, 40 }, ["D40L50"] = new double[] { 40, 50 },
        };
        // GB/T 1096 普通平键：b → h、可选长度范围
        static readonly Dictionary<string, double[]> FlatKeys = new Dictionary<string, double[]>
        {
            ["2"] = new double[] { 2, 6, 20 }, ["3"] = new double[] { 3, 6, 36 }, ["4"] = new double[] { 4, 8, 45 }, ["5"] = new double[] { 5, 10, 56 },
            ["6"] = new double[] { 6, 14, 70 }, ["8"] = new double[] { 7, 18, 90 }, ["10"] = new double[] { 8, 22, 110 }, ["12"] = new double[] { 8, 28, 140 },
            ["14"] = new double[] { 9, 36, 160 }, ["16"] = new double[] { 10, 45, 180 }, ["18"] = new double[] { 11, 50, 200 }, ["20"] = new double[] { 12, 56, 220 },
        };
        static readonly int[] KeyLengths = { 6, 8, 10, 12, 14, 16, 18, 20, 22, 25, 28, 32, 36, 40, 45, 50, 56, 63, 70, 80, 90, 100, 110, 125, 140, 160, 180, 200, 220 };
        // DIN 471 轴用挡圈：轴径 → 厚 s、自由内径 d3；DIN 472 孔用挡圈：孔径 → 厚 s、自由外径 d3
        static readonly Dictionary<string, double[]> ShaftRing = new Dictionary<string, double[]>
        {
            ["4"] = new double[] { 0.4, 3.7 }, ["5"] = new double[] { 0.6, 4.7 }, ["6"] = new double[] { 0.7, 5.6 }, ["8"] = new double[] { 0.8, 7.4 }, ["10"] = new double[] { 1, 9.3 },
            ["12"] = new double[] { 1, 11 }, ["15"] = new double[] { 1, 13.8 }, ["17"] = new double[] { 1, 15.7 }, ["20"] = new double[] { 1.2, 18.5 }, ["25"] = new double[] { 1.2, 23.2 },
            ["30"] = new double[] { 1.5, 27.9 }, ["35"] = new double[] { 1.5, 32.2 }, ["40"] = new double[] { 1.75, 36.5 },
        };
        static readonly Dictionary<string, double[]> BoreRing = new Dictionary<string, double[]>
        {
            ["10"] = new double[] { 1, 10.8 }, ["12"] = new double[] { 1, 13 }, ["15"] = new double[] { 1, 16.2 }, ["16"] = new double[] { 1, 17.3 }, ["19"] = new double[] { 1, 20.5 },
            ["22"] = new double[] { 1, 23.5 }, ["24"] = new double[] { 1.2, 25.9 }, ["26"] = new double[] { 1.2, 27.9 }, ["28"] = new double[] { 1.2, 30.1 }, ["30"] = new double[] { 1.2, 32.1 },
            ["32"] = new double[] { 1.2, 34.4 }, ["35"] = new double[] { 1.5, 37.8 }, ["40"] = new double[] { 1.75, 43.5 }, ["47"] = new double[] { 1.75, 50.5 },
        };

        static IEnumerable<PartType> Drive() => new List<PartType>
        {
            new PartType
            {
                Id = "timing_pulley", Category = "传动", Name = "同步带轮", Standard = null, AddAxis = true, Keywords = "同步轮 皮带轮 GT2 HTD 3M 5M timing pulley",
                Note = "节径 = 齿数 × 节距 ÷ π，外径 = 节径 − 节线差（GT2 0.508、3M 0.762、5M 1.144 mm）。齿槽按圆弧简化（槽深、槽宽接近标准齿形），用于选型、装配和 3D 打印。顺序从下往上：轮毂、挡边、齿、挡边。",
                Params =
                {
                    Choice("profile", "齿形", _ => Belts.Keys.ToArray(), "GT2"),
                    Choice("z", "齿数", p => Belts[p["profile"]].Teeth, "20"),
                    Choice("w", "带宽", p => Belts[p["profile"]].Widths, "6", "mm"),
                    Number("d", "孔径", 1, 60, 5, "mm"),
                    Number("hubD", "轮毂直径", 0, 120, 0, "mm", "0 = 自动"),
                    Number("hubL", "轮毂长度", 0, 60, 7, "mm", "0 = 不要轮毂"),
                    Choice("flange", "挡边", _ => new[] { "双挡边", "无挡边" }, "双挡边"),
                    Choice("mat", "材料", _ => new[] { "铝合金", "钢", "PLA" }, "铝合金"),
                },
                Size = p => $"{p["profile"]}-{p["z"]}T W{p["w"]} 孔{p["d"]}" + (D(p, "hubL") > 0 ? "" : " 无毂") + (p["flange"] == "无挡边" ? " 无挡边" : ""),
                Material = p => p["mat"] == "钢" ? "steel" : p["mat"] == "PLA" ? "pla" : "aluminum",
                Build = p => PulleyGeom(p["profile"], I(p, "z"), D(p, "w"), D(p, "d") * MM, D(p, "hubD") * MM, D(p, "hubL") * MM, p["flange"] == "双挡边", out _),
            },
            new PartType
            {
                Id = "gear_rack", Category = "传动", Name = "齿条", Standard = null, Keywords = "齿条 直齿条 rack 齿轮齿条",
                Note = "标准直齿条（压力角 20°、齿顶高 m、齿根高 1.25m）。沿 X 方向，齿朝 +Y，齿宽沿 Z。节线高度 = 齿根以下厚度 + 1.25m；和齿轮啮合时，齿轮中心到齿条底面 = 节线高度 + 分度圆半径。",
                Params =
                {
                    Choice("m", "模数", _ => new[] { "0.5", "0.8", "1", "1.25", "1.5", "2", "2.5", "3", "4" }, "1"),
                    Number("n", "齿数", 3, 400, 30, ""),
                    Number("b", "齿宽", 1, 100, 10, "mm"),
                    Number("base", "齿根以下厚度", 0.5, 100, 6, "mm", "截面总高 = 此厚度 + 2.25 × 模数"),
                    Choice("mat", "材料", _ => new[] { "钢", "尼龙", "PLA", "不设置" }, "钢"),
                },
                Size = p => $"m{p["m"]} {p["n"]}齿 b{p["b"]} 底{p["base"]}",
                Material = p => p["mat"] == "钢" ? "steel" : p["mat"] == "尼龙" ? "nylon" : p["mat"] == "PLA" ? "pla" : null,
                Build = p =>
                {
                    double m = D(p, "m") * MM, b = D(p, "b") * MM, H = D(p, "base") * MM + 2.25 * m, pitch = Math.PI * m, ta = Math.Tan(20 * Math.PI / 180);
                    int n = I(p, "n");
                    double yp = H - m, yr = yp - 1.25 * m, yt = yp + m, hr = pitch / 4 + 1.25 * m * ta, ht = pitch / 4 - m * ta, L = n * pitch;

                    var pts = new List<V2> { new V2(0, 0), new V2(L, 0), new V2(L, yr) };
                    for (int k = n - 1; k >= 0; k--)
                    {
                        var xc = (k + 0.5) * pitch;
                        pts.Add(new V2(xc + hr, yr)); pts.Add(new V2(xc + ht, yt)); pts.Add(new V2(xc - ht, yt)); pts.Add(new V2(xc - hr, yr));
                    }
                    pts.Add(new V2(0, yr));
                    return new PartGeom().Add(Op.Extrude("齿条", -b / 2, b, Loop.Polygon(pts)).Along(Axis.Z));
                },
            },
            new PartType
            {
                Id = "sprocket", Category = "传动", Name = "链轮", Standard = "ISO 606", AddAxis = true, Keywords = "链轮 链条 sprocket 08B 06B",
                Note = "单排滚子链链轮。节圆直径 = 节距 ÷ sin(180°/z)，齿槽按滚子直径的圆弧简化（不画齿侧倒角）。顶圆直径取标准范围的中值。",
                Params =
                {
                    Choice("chain", "链号", _ => Chains.Keys.ToArray(), "08B"),
                    Number("z", "齿数", 9, 80, 15, ""),
                    Number("d", "孔径", 2, 80, 10, "mm"),
                    Number("hubD", "轮毂直径", 0, 150, 0, "mm", "0 = 自动"),
                    Number("hubL", "轮毂长度", 0, 60, 0, "mm", "0 = 不要轮毂"),
                },
                Size = p => $"{p["chain"].Split(' ')[0]}-{p["z"]}T 孔{p["d"]}" + (D(p, "hubL") > 0 ? $" 毂{p["hubL"]}" : ""),
                Material = _ => "steel",
                Build = p =>
                {
                    var c = Chains[p["chain"]].Select(v => v * MM).ToArray();
                    double pch = c[0], d1 = c[1], b1 = c[2];
                    int z = I(p, "z");
                    double dp = pch / Math.Sin(Math.PI / z), daMax = dp + 1.25 * pch - d1, daMin = dp + pch * (1 - 1.6 / z) - d1, da = (daMax + daMin) / 2;
                    double re = 0.505 * d1 + 0.069 * Math.Pow(d1 / MM, 1.0 / 3) * MM, bf = 0.93 * b1, bore = D(p, "d") * MM;
                    double rootR = dp / 2 - re;
                    if (bore / 2 >= rootR - 1.5 * MM) throw new ArgumentException($"孔太大：孔径要小于 {F((rootR - 1.5 * MM) * 2 / MM)} mm");
                    double hubL = D(p, "hubL") * MM, hubD = D(p, "hubD") * MM;
                    if (hubD <= 0) hubD = Math.Min(Math.Max(bore + 10 * MM, 1.6 * bore), 2 * (rootR - 1 * MM));
                    var g = new PartGeom().Add(Op.Extrude("链轮", 0, bf, Loop.Notched(da / 2, z, dp / 2, re), Loop.Circle(bore / 2)));
                    if (hubL > 0) g.Add(Op.Extrude("轮毂", bf, hubL, Loop.Circle(hubD / 2), Loop.Circle(bore / 2)));
                    return g;
                },
            },
            new PartType
            {
                Id = "coupling", Category = "传动", Name = "联轴器", Standard = null, Keywords = "联轴器 梅花 膜片 刚性 弹性 coupling jaw",
                Note = "按外形画：刚性（夹紧式，一体）、梅花（两个半联轴器加弹性体）、膜片（两个轮毂加膜片组）、弹性（梁式，外圆三道槽表示）。两端孔径可以不同。不画夹紧螺钉和开槽。",
                Params =
                {
                    Choice("style", "形式", _ => new[] { "刚性", "梅花", "膜片", "弹性" }, "梅花"),
                    Choice("size", "规格", _ => Couplings.Keys.ToArray(), "D20L25"),
                    Number("d1", "孔径 1", 1, 30, 5, "mm"),
                    Number("d2", "孔径 2", 1, 30, 8, "mm"),
                },
                Size = p => $"{p["style"]} {p["size"]} {p["d1"]}-{p["d2"]}",
                Material = p => "aluminum",
                Build = p =>
                {
                    var c = Couplings[p["size"]];
                    double Dm = c[0] * MM, L = c[1] * MM, r1 = D(p, "d1") / 2 * MM, r2 = D(p, "d2") / 2 * MM, R = Dm / 2;
                    if (Math.Max(r1, r2) > R - 2 * MM) throw new ArgumentException($"孔太大：外径 {F(c[0])} mm 的联轴器孔径最大约 {F(c[0] - 4)} mm");
                    var g = new PartGeom();
                    switch (p["style"])
                    {
                        case "刚性":
                            return g.Add(Op.Extrude("轮毂 1", 0, L / 2, Loop.Circle(R), Loop.Circle(r1))).Add(Op.Extrude("轮毂 2", L / 2, L / 2, Loop.Circle(R), Loop.Circle(r2)));
                        case "弹性":
                            {
                                double gw = 0.06 * L, gd = 0.12 * R;
                                var pb = new PathBuilder(new V2(r1, 0)).Line(R, 0);
                                foreach (var yc in new[] { 0.35 * L, 0.5 * L, 0.65 * L })
                                    pb.Line(R, yc - gw / 2).Line(R - gd, yc - gw / 2).Line(R - gd, yc + gw / 2).Line(R, yc + gw / 2);
                                var loop = pb.Line(R, L).Line(r2, L).Line(r2, L / 2).Line(r1, L / 2).Close();
                                return g.Add(Op.Revolve("联轴器", loop));
                            }
                        default:
                            {
                                bool jaw = p["style"] == "梅花";
                                double mid = (jaw ? 0.2 : 0.12) * L, hub = (L - mid) / 2, rm = (jaw ? 0.96 : 0.9) * R;
                                return g.Add(Op.Extrude("轮毂 1", 0, hub, Loop.Circle(R), Loop.Circle(r1)))
                                        .Add(Op.Extrude(jaw ? "弹性体" : "膜片组", hub, mid, Loop.Circle(rm), Loop.Circle(Math.Max(r1, r2))))
                                        .Add(Op.Extrude("轮毂 2", hub + mid, hub, Loop.Circle(R), Loop.Circle(r2)));
                            }
                    }
                },
            },
            new PartType
            {
                Id = "flat_key", Category = "传动", Name = "普通平键", Standard = "GB/T 1096", Keywords = "平键 键 key 键槽 DIN 6885",
                Note = "A 型圆头、B 型平头、C 型单圆头。键宽 b × 键高 h × 长 L，长度沿 X。不画倒角。",
                Params =
                {
                    Choice("type", "形式", _ => new[] { "A 型（圆头）", "B 型（平头）", "C 型（单圆头）" }, "A 型（圆头）"),
                    Choice("b", "键宽", _ => FlatKeys.Keys.ToArray(), "6", "mm"),
                    Choice("len", "长度", p => KeyLengths.Where(l => l >= FlatKeys[p["b"]][1] && l <= FlatKeys[p["b"]][2]).Select(l => l.ToString()).ToArray(), "20", "mm"),
                },
                Size = p => $"{p["type"].Substring(0, 1)} {p["b"]}×{F(FlatKeys[p["b"]][0])}×{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    double b = D(p, "b") * MM, h = FlatKeys[p["b"]][0] * MM, L = D(p, "len") * MM;
                    Loop plan;
                    switch (p["type"][0])
                    {
                        case 'B': plan = Loop.Rect(L, b); break;
                        case 'C':
                            plan = new PathBuilder(new V2(-L / 2, -b / 2)).Line(L / 2 - b / 2, -b / 2).Arc(new V2(L / 2 - b / 2, 0), new V2(L / 2 - b / 2, b / 2)).Line(-L / 2, b / 2).Close();
                            break;
                        default: plan = Loop.Slot(L, b); break;
                    }
                    return new PartGeom().Add(Op.Extrude("平键", 0, h, plan));
                },
            },
            new PartType
            {
                Id = "shaft_circlip", Category = "传动", Name = "轴用挡圈", Standard = "DIN 471", Keywords = "卡簧 挡圈 轴用 弹性挡圈 circlip GB/T 894",
                Note = "GB/T 894 同类。按自由状态的内径和厚度画开口环（不画耳部和钳孔）。",
                Params = { Choice("d", "轴径", _ => ShaftRing.Keys.ToArray(), "8", "mm") },
                Size = p => $"d{p["d"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var t = ShaftRing[p["d"]];
                    double d = D(p, "d") * MM, ri = t[1] / 2 * MM, w = 0.1 * d + 0.8 * MM;
                    return new PartGeom().Add(Op.Extrude("挡圈", 0, t[0] * MM, CRing(ri, ri + w, 0.25 * d)));
                },
            },
            new PartType
            {
                Id = "bore_circlip", Category = "传动", Name = "孔用挡圈", Standard = "DIN 472", Keywords = "卡簧 挡圈 孔用 弹性挡圈 circlip GB/T 893",
                Note = "GB/T 893 同类。按自由状态的外径和厚度画开口环（不画耳部和钳孔）。",
                Params = { Choice("d", "孔径", _ => BoreRing.Keys.ToArray(), "22", "mm") },
                Size = p => $"D{p["d"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var t = BoreRing[p["d"]];
                    double d = D(p, "d") * MM, ro = t[1] / 2 * MM, w = 0.1 * d + 0.8 * MM;
                    return new PartGeom().Add(Op.Extrude("挡圈", 0, t[0] * MM, CRing(ro - w, ro, 0.25 * d)));
                },
            },
            new PartType
            {
                Id = "o_ring", Category = "传动", Name = "O 型圈", Standard = "GB/T 3452.1", Keywords = "O型圈 密封圈 o-ring seal",
                Note = "内径 d1 × 截面直径 d2。中心在原点，轴线沿 Y。",
                Params =
                {
                    Number("d1", "内径", 1, 500, 10, "mm"),
                    Choice("d2", "截面直径", _ => new[] { "1", "1.5", "1.8", "2", "2.4", "2.5", "2.65", "3", "3.1", "3.5", "3.55", "4", "5", "5.3", "5.7", "7" }, "1.8", "mm"),
                },
                Size = p => $"{p["d1"]}×{p["d2"]}",
                Material = _ => "rubber",
                Build = p =>
                {
                    double d1 = D(p, "d1") * MM, d2 = D(p, "d2") * MM;
                    return new PartGeom().Add(Op.Revolve("O 型圈", Loop.Circle(new V2(d1 / 2 + d2 / 2, 0), d2 / 2)));
                },
            },
        };
    }
}
