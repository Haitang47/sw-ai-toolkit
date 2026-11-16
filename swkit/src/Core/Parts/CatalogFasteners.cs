using System;
using System.Collections.Generic;
using System.Linq;

namespace SwKit.Core.Parts
{
    /// <summary>紧固件：紧定螺钉、锁紧螺母、法兰螺母 / 螺栓、垫圈、销、铆钉、热熔螺母、铜柱、T 型螺母、半圆头 / 盘头螺钉。</summary>
    public static partial class Catalog
    {
        // ISO 4026/4027/4029 内六角紧定螺钉：内六角对边 s、深 t、平端直径 dp、凹端直径 dz、可选长度范围
        static readonly Dictionary<string, double[]> SetScrew = new Dictionary<string, double[]>
        {
            ["M2"] = new double[] { 0.9, 0.8, 1.0, 1.0, 2, 10 }, ["M2.5"] = new double[] { 1.3, 1.2, 1.5, 1.2, 3, 12 }, ["M3"] = new double[] { 1.5, 1.2, 2.0, 1.4, 3, 16 },
            ["M4"] = new double[] { 2, 1.5, 2.5, 2.0, 3, 20 }, ["M5"] = new double[] { 2.5, 2.0, 3.5, 2.5, 4, 25 }, ["M6"] = new double[] { 3, 2.0, 4.0, 3.0, 5, 30 },
            ["M8"] = new double[] { 4, 3.0, 5.5, 5.0, 6, 40 }, ["M10"] = new double[] { 5, 4.0, 7.0, 6.0, 8, 50 }, ["M12"] = new double[] { 6, 4.8, 8.5, 8.0, 10, 60 },
        };
        // DIN 985 尼龙锁紧螺母：总高 h（对边同 ISO 4032）
        static readonly Dictionary<string, double> NylocH = new Dictionary<string, double>
        {
            ["M2"] = 2.8, ["M2.5"] = 3.5, ["M3"] = 4, ["M4"] = 5, ["M5"] = 5, ["M6"] = 6, ["M8"] = 8, ["M10"] = 10, ["M12"] = 12,
        };
        // ISO 4161 六角法兰螺母：对边 s、高 m、法兰直径 dc、法兰厚 c
        static readonly Dictionary<string, double[]> FlangeNut = new Dictionary<string, double[]>
        {
            ["M5"] = new double[] { 8, 5, 11.8, 1.0 }, ["M6"] = new double[] { 10, 6, 14.2, 1.1 }, ["M8"] = new double[] { 13, 8, 17.9, 1.2 },
            ["M10"] = new double[] { 15, 10, 21.8, 1.5 }, ["M12"] = new double[] { 18, 12, 26.0, 1.8 },
        };
        // GB/T 5787 六角法兰面螺栓：对边 s、头高 k（含法兰）、法兰直径 dc、法兰厚 c
        static readonly Dictionary<string, double[]> FlangeBolt = new Dictionary<string, double[]>
        {
            ["M5"] = new double[] { 8, 5.4, 11.8, 1.0 }, ["M6"] = new double[] { 10, 6.6, 14.2, 1.1 }, ["M8"] = new double[] { 13, 8.1, 18.0, 1.2 },
            ["M10"] = new double[] { 15, 9.2, 22.3, 1.5 }, ["M12"] = new double[] { 18, 11.5, 26.6, 1.8 },
        };
        // GB 93 标准型弹簧垫圈：内径 d1、截面 s（= b）
        static readonly Dictionary<string, double[]> SpringWasher = new Dictionary<string, double[]>
        {
            ["M2"] = new double[] { 2.1, 0.5 }, ["M2.5"] = new double[] { 2.6, 0.65 }, ["M3"] = new double[] { 3.1, 0.8 }, ["M4"] = new double[] { 4.1, 1.1 },
            ["M5"] = new double[] { 5.1, 1.3 }, ["M6"] = new double[] { 6.1, 1.6 }, ["M8"] = new double[] { 8.1, 2.1 }, ["M10"] = new double[] { 10.2, 2.6 },
            ["M12"] = new double[] { 12.2, 3.1 },
        };
        // ISO 7093-1 大垫圈：内径 d1、外径 d2、厚 h
        static readonly Dictionary<string, double[]> LargeWasher = new Dictionary<string, double[]>
        {
            ["M3"] = new double[] { 3.2, 9, 0.8 }, ["M4"] = new double[] { 4.3, 12, 1 }, ["M5"] = new double[] { 5.3, 15, 1.2 }, ["M6"] = new double[] { 6.4, 18, 1.6 },
            ["M8"] = new double[] { 8.4, 24, 2 }, ["M10"] = new double[] { 10.5, 30, 2.5 }, ["M12"] = new double[] { 13, 37, 3 },
        };
        // ISO 8752 重型弹性圆柱销：壁厚 s
        static readonly Dictionary<string, double> SpringPinWall = new Dictionary<string, double>
        {
            ["1.5"] = 0.3, ["2"] = 0.4, ["2.5"] = 0.5, ["3"] = 0.6, ["4"] = 0.8, ["5"] = 1.0, ["6"] = 1.2, ["8"] = 1.5, ["10"] = 2.0, ["12"] = 2.5,
        };
        // GB/T 12618 开口型扁圆头抽芯铆钉：钉体直径 → 头径 dk、头高 k、芯轴直径 dm
        static readonly Dictionary<string, double[]> BlindRivet = new Dictionary<string, double[]>
        {
            ["2.4"] = new double[] { 5.0, 1.0, 1.5 }, ["3.2"] = new double[] { 6.4, 1.3, 1.9 }, ["4"] = new double[] { 8.0, 1.7, 2.45 }, ["4.8"] = new double[] { 9.6, 2.0, 2.9 },
        };
        // 热熔滚花铜螺母（3D 打印常用，无统一标准）：外径、可选长度
        static readonly Dictionary<string, (double od, string[] lens)> HeatInsert = new Dictionary<string, (double, string[])>
        {
            ["M2"] = (3.2, new[] { "3", "4" }), ["M2.5"] = (3.5, new[] { "3", "4", "5" }), ["M3"] = (4.2, new[] { "3", "4", "5", "6", "8" }),
            ["M4"] = (5.6, new[] { "4", "6", "8" }), ["M5"] = (6.4, new[] { "6", "8", "10" }), ["M6"] = (8.0, new[] { "8", "10", "12" }),
        };
        // 六角铜柱：对边、公头螺纹长度
        static readonly Dictionary<string, double[]> Standoff = new Dictionary<string, double[]>
        {
            ["M2"] = new double[] { 3.5, 3.5 }, ["M2.5"] = new double[] { 5, 5 }, ["M3"] = new double[] { 5, 6 }, ["M4"] = new double[] { 7, 7 },
        };
        // ISO 7380-1 内六角半圆头螺钉：头径 dk、头高 k、内六角对边 s、深 t
        static readonly Dictionary<string, double[]> Iso7380 = new Dictionary<string, double[]>
        {
            ["M3"] = new double[] { 5.7, 1.65, 2, 1.04 }, ["M4"] = new double[] { 7.6, 2.2, 2.5, 1.3 }, ["M5"] = new double[] { 9.5, 2.75, 3, 1.56 },
            ["M6"] = new double[] { 10.5, 3.3, 4, 2.08 }, ["M8"] = new double[] { 14, 4.4, 5, 2.6 }, ["M10"] = new double[] { 17.5, 5.5, 6, 3.12 },
            ["M12"] = new double[] { 21, 6.6, 8, 4.16 },
        };
        // ISO 7045 十字槽盘头螺钉：头径 dk、头高 k、十字槽宽 m
        static readonly Dictionary<string, double[]> Iso7045 = new Dictionary<string, double[]>
        {
            ["M2"] = new double[] { 4.0, 1.6, 1.9 }, ["M2.5"] = new double[] { 5.0, 2.0, 2.7 }, ["M3"] = new double[] { 5.6, 2.4, 3.0 },
            ["M4"] = new double[] { 8.0, 3.1, 4.4 }, ["M5"] = new double[] { 9.5, 3.7, 4.9 }, ["M6"] = new double[] { 12, 4.6, 6.9 },
        };
        // 型材 T 型螺母：型材单元 → 可选螺纹、长度
        static readonly Dictionary<string, (string[] threads, double len)> TNut = new Dictionary<string, (string[], double)>
        {
            ["20"] = (new[] { "M3", "M4", "M5" }, 10), ["30"] = (new[] { "M4", "M5", "M6" }, 13), ["40"] = (new[] { "M4", "M5", "M6" }, 16),
        };

        static PartGeom SetScrewGeom(Dictionary<string, string> p, string point)
        {
            var t = SetScrew[p["size"]];
            double d = Nominal(p["size"]) * MM, L = D(p, "len") * MM, s = t[0] * MM, depth = t[1] * MM, dp = t[2] * MM, dz = t[3] * MM, ct = 0.08 * d;
            double r = d / 2;
            List<V2> pts;
            switch (point)
            {
                case "cone":
                    pts = new List<V2> { new V2(0, 0), new V2(r, r), new V2(r, L - ct), new V2(r - ct, L), new V2(0, L) };
                    if (L - depth < s / Math.Sqrt(3) + 0.1 * MM) throw new ArgumentException("长度太短：锥端和内六角重叠");
                    break;
                case "cup":
                    double rc = 0.35 * dz, hc = rc * 0.6;
                    pts = new List<V2> { new V2(0, hc), new V2(rc, 0), new V2(dz / 2, 0), new V2(r, (d - dz) / 2), new V2(r, L - ct), new V2(r - ct, L), new V2(0, L) };
                    if (L - depth < hc + 0.2 * MM) throw new ArgumentException("长度太短：凹端和内六角重叠");
                    break;
                default:
                    pts = new List<V2> { new V2(0, 0), new V2(dp / 2, 0), new V2(r, (d - dp) / 2), new V2(r, L - ct), new V2(r - ct, L), new V2(0, L) };
                    break;
            }
            return new PartGeom().Add(Op.Revolve("螺钉", Loop.Polygon(pts))).Add(Op.Cut("内六角", L, depth, Loop.Hexagon(s)));
        }

        static PartType SetScrewType(string id, string std, string name, string point, string note) => new PartType
        {
            Id = id, Category = "紧固件", Name = name, Standard = std, Keywords = "紧定螺钉 顶丝 机米 set screw grub",
            Note = note + "螺纹按光杆画。",
            Params =
            {
                Choice("size", "规格", _ => SetScrew.Keys.ToArray(), "M3"),
                Choice("len", "长度", p => LengthsFor(p["size"], SetScrew[p["size"]][4], SetScrew[p["size"]][5]), "6", "mm"),
            },
            Size = p => $"{p["size"]}×{p["len"]}",
            Material = _ => "steel",
            Build = p => SetScrewGeom(p, point),
        };

        static IEnumerable<PartType> Fasteners() => new List<PartType>
        {
            SetScrewType("set_flat", "ISO 4026", "紧定螺钉（平端）", "flat", "GB/T 77 同尺寸。"),
            SetScrewType("set_cone", "ISO 4027", "紧定螺钉（锥端）", "cone", "GB/T 78 同尺寸，锥端 90°。"),
            SetScrewType("set_cup", "ISO 4029", "紧定螺钉（凹端）", "cup", "GB/T 80 同尺寸，凹端为简化的锥形凹坑。"),
            new PartType
            {
                Id = "button_head", Category = "紧固件", Name = "内六角半圆头螺钉", Standard = "ISO 7380", Keywords = "圆头 盘头 内六角 button head",
                Note = "GB/T 70.2 同尺寸。头部为球冠，内六角处为小平台。螺纹按光杆画。",
                Params =
                {
                    Choice("size", "规格", _ => Iso7380.Keys.ToArray(), "M3"),
                    Choice("len", "长度", p => LengthsFor(p["size"], Nominal(p["size"]) * 1.5, Math.Min(Nominal(p["size"]) * 10, 60)), "8", "mm", "不含头部"),
                },
                Size = p => $"{p["size"]}×{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var t = Iso7380[p["size"]];
                    double d = Nominal(p["size"]) * MM, L = D(p, "len") * MM, dk = t[0] * MM, k = t[1] * MM, s = t[2] * MM, depth = t[3] * MM, c = 0.08 * d;
                    double e = 0.15 * k, R = (dk * dk / 4 + (k - e) * (k - e)) / (2 * (k - e)), yc = L + k - R;
                    double rf = s / Math.Sqrt(3) * 1.12, yf = yc + Math.Sqrt(R * R - rf * rf);
                    var loop = new PathBuilder(new V2(0, 0)).Line(d / 2 - c, 0).Line(d / 2, c).Line(d / 2, L).Line(dk / 2, L).Line(dk / 2, L + e)
                        .Arc(new V2(0, yc), new V2(rf, yf)).Line(0, yf).Close();
                    return new PartGeom().Add(Op.Revolve("螺钉", loop)).Add(Op.Cut("内六角", yf, depth, Loop.Hexagon(s)));
                },
            },
            new PartType
            {
                Id = "pan_cross", Category = "紧固件", Name = "十字槽盘头螺钉", Standard = "ISO 7045", Keywords = "十字 盘头 机丝 pan head phillips",
                Note = "GB/T 818 同尺寸。十字槽按简化的十字形画。螺纹按光杆画。",
                Params =
                {
                    Choice("size", "规格", _ => Iso7045.Keys.ToArray(), "M3"),
                    Choice("len", "长度", p => LengthsFor(p["size"], Math.Max(3, Nominal(p["size"]) * 1.2), Math.Min(Nominal(p["size"]) * 10, 60)), "8", "mm", "不含头部"),
                },
                Size = p => $"{p["size"]}×{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var t = Iso7045[p["size"]];
                    double d = Nominal(p["size"]) * MM, L = D(p, "len") * MM, dk = t[0] * MM, k = t[1] * MM, m = t[2] * MM, c = 0.08 * d, rr = 0.3 * k;
                    var loop = new PathBuilder(new V2(0, 0)).Line(d / 2 - c, 0).Line(d / 2, c).Line(d / 2, L).Line(dk / 2, L).Line(dk / 2, L + k - rr)
                        .Arc(new V2(dk / 2 - rr, L + k - rr), new V2(dk / 2 - rr, L + k)).Line(0, L + k).Close();
                    double a = m / 2, b = 0.11 * m;
                    var cross = Loop.Polygon(new V2(a, -b), new V2(a, b), new V2(b, b), new V2(b, a), new V2(-b, a), new V2(-b, b), new V2(-a, b), new V2(-a, -b),
                                             new V2(-b, -b), new V2(-b, -a), new V2(b, -a), new V2(b, -b));
                    return new PartGeom().Add(Op.Revolve("螺钉", loop)).Add(Op.Cut("十字槽", L + k, 0.55 * k, cross));
                },
            },
            new PartType
            {
                Id = "flange_bolt", Category = "紧固件", Name = "六角法兰面螺栓", Standard = "GB/T 5787", Keywords = "法兰螺栓 flange bolt",
                Note = "法兰按平垫画（实际为锥形），螺纹按光杆画。",
                Params =
                {
                    Choice("size", "规格", _ => FlangeBolt.Keys.ToArray(), "M6"),
                    Choice("len", "长度", p => LengthsFor(p["size"], Nominal(p["size"]) * 1.6, Nominal(p["size"]) * 10), "20", "mm", "不含头部"),
                },
                Size = p => $"{p["size"]}×{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var t = FlangeBolt[p["size"]];
                    double d = Nominal(p["size"]) * MM, L = D(p, "len") * MM, s = t[0] * MM, k = t[1] * MM, dc = t[2] * MM, c = t[3] * MM;
                    return new PartGeom()
                        .Add(Op.Revolve("螺杆", ChamferedRod(d / 2, L, 0.08 * d, 0)))
                        .Add(Op.Extrude("法兰", L, c, Loop.Circle(dc / 2)))
                        .Add(Op.Extrude("六角头", L + c, k - c, Loop.Hexagon(s)));
                },
            },
            new PartType
            {
                Id = "nyloc_nut", Category = "紧固件", Name = "尼龙锁紧螺母", Standard = "DIN 985", Keywords = "防松螺母 尼龙 锁紧 nyloc ISO 10511",
                Note = "ISO 10511 / GB/T 889.1 同类。对边同 ISO 4032，尼龙圈部分按圆柱画。",
                Params = { Choice("size", "规格", _ => NylocH.Keys.ToArray(), "M3") },
                Size = p => p["size"],
                Material = _ => "steel",
                Build = p =>
                {
                    double d = Nominal(p["size"]) * MM, s = Iso4032[p["size"]][0] * MM, h = NylocH[p["size"]] * MM, hh = Math.Round(0.7 * h / MM, 1) * MM;
                    return new PartGeom()
                        .Add(Op.Extrude("六角", 0, hh, Loop.Hexagon(s), Loop.Circle(d / 2)))
                        .Add(Op.Extrude("尼龙圈", hh, h - hh, Loop.Circle(0.95 * s / 2), Loop.Circle(d / 2)));
                },
            },
            new PartType
            {
                Id = "flange_nut", Category = "紧固件", Name = "六角法兰螺母", Standard = "ISO 4161", Keywords = "法兰螺母 flange nut GB/T 6177",
                Note = "GB/T 6177.1 同类。法兰按平垫画（实际为锥形）。",
                Params = { Choice("size", "规格", _ => FlangeNut.Keys.ToArray(), "M6") },
                Size = p => p["size"],
                Material = _ => "steel",
                Build = p =>
                {
                    var t = FlangeNut[p["size"]];
                    double d = Nominal(p["size"]) * MM, s = t[0] * MM, m = t[1] * MM, dc = t[2] * MM, c = t[3] * MM;
                    return new PartGeom()
                        .Add(Op.Extrude("法兰", 0, c, Loop.Circle(dc / 2), Loop.Circle(d / 2)))
                        .Add(Op.Extrude("六角", c, m - c, Loop.Hexagon(s), Loop.Circle(d / 2)));
                },
            },
            new PartType
            {
                Id = "spring_washer", Category = "紧固件", Name = "弹簧垫圈", Standard = "GB 93", Keywords = "弹垫 spring lock washer DIN 127",
                Note = "按压平后的形状画（开口环，不画螺旋翘起）。",
                Params = { Choice("size", "规格", _ => SpringWasher.Keys.ToArray(), "M3") },
                Size = p => p["size"],
                Material = _ => "steel",
                Build = p =>
                {
                    var t = SpringWasher[p["size"]];
                    double ri = t[0] / 2 * MM, s = t[1] * MM;
                    return new PartGeom().Add(Op.Extrude("弹垫", 0, s, CRing(ri, ri + s, s)));
                },
            },
            new PartType
            {
                Id = "large_washer", Category = "紧固件", Name = "大垫圈", Standard = "ISO 7093", Keywords = "大垫片 加大垫圈 fender washer GB/T 96",
                Note = "GB/T 96.1 同尺寸。",
                Params = { Choice("size", "规格", _ => LargeWasher.Keys.ToArray(), "M4") },
                Size = p => p["size"],
                Material = _ => "steel",
                Build = p =>
                {
                    var t = LargeWasher[p["size"]];
                    return new PartGeom().Add(Op.Extrude("垫圈", 0, t[2] * MM, Loop.Circle(t[1] / 2 * MM), Loop.Circle(t[0] / 2 * MM)));
                },
            },
            new PartType
            {
                Id = "dowel_pin", Category = "紧固件", Name = "圆柱销", Standard = "ISO 8734", Keywords = "定位销 销钉 dowel pin GB/T 119",
                Note = "淬硬圆柱销，m6 公差，GB/T 119.2 同类。两端倒角。",
                Params =
                {
                    Choice("d", "直径", _ => new[] { "1.5", "2", "2.5", "3", "4", "5", "6", "8", "10", "12" }, "3", "mm"),
                    Choice("len", "长度", p => Lengths.Where(l => l >= Math.Max(3, 2 * D(p, "d")) && l <= 10 * D(p, "d")).Select(l => l.ToString()).ToArray(), "10", "mm"),
                },
                Size = p => $"{p["d"]}×{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    double d = D(p, "d") * MM, L = D(p, "len") * MM, c = 0.12 * d;
                    return new PartGeom().Add(Op.Revolve("销", ChamferedRod(d / 2, L, c, c)));
                },
            },
            new PartType
            {
                Id = "spring_pin", Category = "紧固件", Name = "弹性圆柱销", Standard = "ISO 8752", Keywords = "弹性销 开口销 卷销 spring pin roll pin GB/T 879",
                Note = "重型直槽弹性销，GB/T 879.1 同类。按公称直径画开口管（不画倒角）。",
                Params =
                {
                    Choice("d", "直径", _ => SpringPinWall.Keys.ToArray(), "3", "mm"),
                    Choice("len", "长度", p => Lengths.Where(l => l >= Math.Max(4, 2 * D(p, "d")) && l <= 10 * D(p, "d")).Select(l => l.ToString()).ToArray(), "12", "mm"),
                },
                Size = p => $"{p["d"]}×{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    double d = D(p, "d") * MM, s = SpringPinWall[p["d"]] * MM;
                    return new PartGeom().Add(Op.Extrude("弹性销", 0, D(p, "len") * MM, CRing(d / 2 - s, d / 2, 0.2 * d)));
                },
            },
            new PartType
            {
                Id = "blind_rivet", Category = "紧固件", Name = "抽芯铆钉", Standard = "GB/T 12618", Keywords = "拉铆钉 铆钉 pop rivet blind rivet",
                Note = "开口型扁圆头，按铆接前的状态画（含芯轴，芯轴露出 25 mm）。长度 = 钉体长度。",
                Params =
                {
                    Choice("d", "直径", _ => BlindRivet.Keys.ToArray(), "3.2", "mm"),
                    Choice("len", "钉体长度", _ => new[] { "6", "8", "10", "12", "16", "20" }, "8", "mm", "铆接厚度约为钉体长度 − 2 mm"),
                },
                Size = p => $"{p["d"]}×{p["len"]}",
                Material = _ => "aluminum",
                Build = p =>
                {
                    var t = BlindRivet[p["d"]];
                    double d = D(p, "d") * MM, L = D(p, "len") * MM, dk = t[0] * MM, k = t[1] * MM, dm = t[2] * MM, pm = 25 * MM;
                    double e = 0.15 * k, R = (dk * dk / 4 + (k - e) * (k - e)) / (2 * (k - e)), yc = L + k - R, ym = yc + Math.Sqrt(R * R - dm * dm / 4);
                    var loop = new PathBuilder(new V2(0, 0)).Line(d / 2, 0).Line(d / 2, L).Line(dk / 2, L).Line(dk / 2, L + e)
                        .Arc(new V2(0, yc), new V2(dm / 2, ym)).Line(dm / 2, L + k + pm).Line(0, L + k + pm).Close();
                    return new PartGeom().Add(Op.Revolve("铆钉", loop));
                },
            },
            new PartType
            {
                Id = "heat_insert", Category = "紧固件", Name = "热熔铜螺母", Standard = null, Keywords = "热熔螺母 滚花螺母 预埋螺母 heat set insert 3D 打印",
                Note = "3D 打印件常用的滚花铜螺母（无统一标准，按常见规格）。滚花按两段圆柱加中间凹槽画。打印预留孔径一般取外径减 0.2–0.4 mm，以厂家建议为准。",
                Params =
                {
                    Choice("size", "螺纹", _ => HeatInsert.Keys.ToArray(), "M3"),
                    Choice("len", "长度", p => HeatInsert[p["size"]].lens, "4", "mm"),
                },
                Size = p => $"{p["size"]}×{p["len"]}",
                Material = _ => "brass",
                Build = p =>
                {
                    double ri = Nominal(p["size"]) / 2 * MM, ro = HeatInsert[p["size"]].od / 2 * MM, L = D(p, "len") * MM, rg = ro - 0.3 * MM, ch = 0.3 * MM;
                    return new PartGeom().Add(Op.Revolve("螺母", Loop.Polygon(new V2(ri, 0), new V2(ro - ch, 0), new V2(ro, ch), new V2(ro, 0.4 * L), new V2(rg, 0.4 * L),
                        new V2(rg, 0.6 * L), new V2(ro, 0.6 * L), new V2(ro, L), new V2(ri, L))));
                },
            },
            new PartType
            {
                Id = "standoff", Category = "紧固件", Name = "六角铜柱", Standard = null, Keywords = "铜柱 隔离柱 支撑柱 standoff spacer PCB",
                Note = "电路板常用的六角隔离柱。双通 = 两端内螺纹；单通 = 一端外螺纹一端内螺纹。长度不含外螺纹。螺纹按公称直径画。",
                Params =
                {
                    Choice("size", "螺纹", _ => Standoff.Keys.ToArray(), "M3"),
                    Choice("kind", "类型", _ => new[] { "双通（母-母）", "单通（公-母）" }, "双通（母-母）"),
                    Choice("len", "长度", _ => new[] { "3", "4", "5", "6", "8", "10", "12", "15", "18", "20", "25", "30", "35", "40" }, "10", "mm"),
                },
                Size = p => $"{p["size"]}×{p["len"]}{(p["kind"].StartsWith("单") ? "+" + F(Standoff[p["size"]][1]) : "")}",
                Material = _ => "brass",
                Build = p =>
                {
                    var t = Standoff[p["size"]];
                    double d = Nominal(p["size"]) * MM, s = t[0] * MM, L = D(p, "len") * MM, stud = t[1] * MM;
                    var g = new PartGeom();
                    if (p["kind"].StartsWith("双")) return g.Add(Op.Extrude("铜柱", 0, L, Loop.Hexagon(s), Loop.Circle(d / 2)));
                    var depth = Math.Min(L - 0.5 * MM, Math.Max(3 * MM, 0.6 * L));
                    return g.Add(Op.Extrude("铜柱", 0, L, Loop.Hexagon(s)))
                            .Add(Op.Cut("螺纹孔", L, depth, Loop.Circle(d / 2)))
                            .Add(Op.Extrude("外螺纹", -stud, stud, Loop.Circle(d / 2)));
                },
            },
            new PartType
            {
                Id = "t_nut", Category = "紧固件", Name = "型材 T 型螺母", Standard = null, Keywords = "T型螺母 滑块螺母 弹片螺母 型材螺母 t-slot nut",
                Note = "欧标型材槽用的滑块螺母，截面按槽腔简化画（不含弹片 / 钢珠）。螺纹孔按公称直径画。",
                Params =
                {
                    Choice("series", "型材", _ => new[] { "20", "30", "40" }, "20", null, "20 = 2020 系列，30 = 3030 系列，40 = 4040 系列"),
                    Choice("size", "螺纹", p => TNut[p["series"]].threads, "M5"),
                },
                Size = p => $"{p["series"]} 系列 {p["size"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var c = ProfileCell[p["series"]].Select(v => v * MM).ToArray();
                    double w = c[1], lip = c[2], wi = c[3], ds = c[4], cc = c[5];
                    double wn = w - 0.4 * MM, hn = lip - 0.2 * MM, wb = wi - 0.6 * MM, hb = ds - lip - 0.5 * MM, cb = Math.Min(cc, hb * 0.6), len = TNut[p["series"]].len * MM;
                    double d = Nominal(p["size"]) * MM;
                    if (d / 2 >= wn / 2 - 0.3 * MM) throw new ArgumentException("螺纹孔太大，放不进槽口");
                    var section = Loop.Polygon(new V2(-(wb / 2 - cb), 0), new V2(wb / 2 - cb, 0), new V2(wb / 2, cb), new V2(wb / 2, hb), new V2(wn / 2, hb), new V2(wn / 2, hb + hn),
                                               new V2(-wn / 2, hb + hn), new V2(-wn / 2, hb), new V2(-wb / 2, hb), new V2(-wb / 2, cb));
                    return new PartGeom()
                        .Add(Op.Extrude("螺母", -len / 2, len, section).Along(Axis.X))
                        .Add(Op.Cut("螺纹孔", hb + hn, hb + hn, Loop.Circle(d / 2)));
                },
            },
        };
    }
}
