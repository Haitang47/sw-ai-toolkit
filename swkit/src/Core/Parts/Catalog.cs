using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SwKit.Core.Parts
{
    public enum ParamKind { Choice, Number }

    public sealed class ParamDef
    {
        public string Key, Label, Unit, Hint;
        public ParamKind Kind;
        /// <summary>可选项（可以依赖其他参数，例如螺钉长度依赖规格）。</summary>
        public Func<Dictionary<string, string>, string[]> Choices;
        public double Min, Max;
        public Func<Dictionary<string, string>, string> Default;
    }

    public class PartType
    {
        public string Id, Category, Name, Standard, Note;
        /// <summary>搜索用的别名（例如「GT2 同步轮 皮带轮」）。</summary>
        public string Keywords;
        /// <summary>生成时加一根沿 Y 的基准轴（名为「旋转轴」），装配时用来加同轴 / 齿轮配合。</summary>
        public bool AddAxis;
        public List<ParamDef> Params = new List<ParamDef>();
        public Func<Dictionary<string, string>, PartGeom> Build;
        /// <summary>规格文字，例如 "M3×10"。</summary>
        public Func<Dictionary<string, string>, string> Size;
        /// <summary>材料键：steel / stainless / aluminum / nylon / pla，null 表示不设置。</summary>
        public Func<Dictionary<string, string>, string> Material = p => null;

        public string Title(Dictionary<string, string> p) => (Standard != null ? Standard + " " : Name + " ") + Size(p);
        public string FileName(Dictionary<string, string> p) => Safe(Title(p));

        public Dictionary<string, string> Defaults()
        {
            var d = new Dictionary<string, string>();
            foreach (var pd in Params) d[pd.Key] = pd.Default(d);
            return d;
        }

        /// <summary>把参数修正到有效范围（例如换了规格后，原来的长度不在新的可选项里）。</summary>
        public void Normalize(Dictionary<string, string> p)
        {
            foreach (var pd in Params)
            {
                if (!p.TryGetValue(pd.Key, out var v) || v == null) { p[pd.Key] = pd.Default(p); continue; }
                if (pd.Kind == ParamKind.Choice)
                {
                    var ch = pd.Choices(p);
                    if (!ch.Contains(v)) p[pd.Key] = ch.Contains(pd.Default(p)) ? pd.Default(p) : ch[0];
                }
                else if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || x < pd.Min || x > pd.Max)
                    p[pd.Key] = pd.Default(p);
            }
        }

        static string Safe(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace('×', 'x');
        }
    }

    /// <summary>标准件目录。尺寸按 ISO / GB 标准表取常用值（螺纹做成光杆，和 SolidWorks Toolbox 的简化画法一样）。</summary>
    public static partial class Catalog
    {
        const double MM = 0.001;
        static double D(Dictionary<string, string> p, string k) => double.Parse(p[k], CultureInfo.InvariantCulture);
        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        static ParamDef Choice(string key, string label, Func<Dictionary<string, string>, string[]> choices, string def, string unit = null, string hint = null) =>
            new ParamDef { Key = key, Label = label, Kind = ParamKind.Choice, Choices = choices, Default = p => { var c = choices(p); return c.Contains(def) ? def : c[0]; }, Unit = unit, Hint = hint };
        static ParamDef Number(string key, string label, double min, double max, double def, string unit, string hint = null) =>
            new ParamDef { Key = key, Label = label, Kind = ParamKind.Number, Min = min, Max = max, Default = _ => F(def), Unit = unit, Hint = hint };

        // ---------------- 尺寸表（毫米） ----------------

        static readonly string[] Metric = { "M2", "M2.5", "M3", "M4", "M5", "M6", "M8", "M10", "M12" };
        static double Nominal(string m) => double.Parse(m.Substring(1), CultureInfo.InvariantCulture);

        // ISO 4762 内六角圆柱头：头径 dk、头高 k、内六角对边 s、内六角深 t
        static readonly Dictionary<string, double[]> Iso4762 = new Dictionary<string, double[]>
        {
            ["M2"] = new double[] { 3.8, 2, 1.5, 1.0 }, ["M2.5"] = new double[] { 4.5, 2.5, 2, 1.1 }, ["M3"] = new double[] { 5.5, 3, 2.5, 1.3 },
            ["M4"] = new double[] { 7.0, 4, 3, 2.0 }, ["M5"] = new double[] { 8.5, 5, 4, 2.5 }, ["M6"] = new double[] { 10, 6, 5, 3.0 },
            ["M8"] = new double[] { 13, 8, 6, 4.0 }, ["M10"] = new double[] { 16, 10, 8, 5.0 }, ["M12"] = new double[] { 18, 12, 10, 6.0 },
        };
        // ISO 10642 内六角沉头：头径 dk、头高 k、内六角对边 s、内六角深 t
        static readonly Dictionary<string, double[]> Iso10642 = new Dictionary<string, double[]>
        {
            ["M3"] = new double[] { 6.72, 1.86, 2, 1.1 }, ["M4"] = new double[] { 8.96, 2.48, 2.5, 1.5 }, ["M5"] = new double[] { 11.2, 3.1, 3, 1.9 },
            ["M6"] = new double[] { 13.44, 3.72, 4, 2.2 }, ["M8"] = new double[] { 17.92, 4.96, 5, 3.0 }, ["M10"] = new double[] { 22.4, 6.2, 6, 3.6 },
            ["M12"] = new double[] { 26.88, 7.44, 8, 4.3 },
        };
        // ISO 4017 外六角螺栓：对边 s、头高 k
        static readonly Dictionary<string, double[]> Iso4017 = new Dictionary<string, double[]>
        {
            ["M3"] = new double[] { 5.5, 2 }, ["M4"] = new double[] { 7, 2.8 }, ["M5"] = new double[] { 8, 3.5 }, ["M6"] = new double[] { 10, 4 },
            ["M8"] = new double[] { 13, 5.3 }, ["M10"] = new double[] { 16, 6.4 }, ["M12"] = new double[] { 18, 7.5 },
        };
        // ISO 4032 六角螺母：对边 s、厚 m
        static readonly Dictionary<string, double[]> Iso4032 = new Dictionary<string, double[]>
        {
            ["M2"] = new double[] { 4, 1.6 }, ["M2.5"] = new double[] { 5, 2 }, ["M3"] = new double[] { 5.5, 2.4 }, ["M4"] = new double[] { 7, 3.2 }, ["M5"] = new double[] { 8, 4.7 },
            ["M6"] = new double[] { 10, 5.2 }, ["M8"] = new double[] { 13, 6.8 }, ["M10"] = new double[] { 16, 8.4 }, ["M12"] = new double[] { 18, 10.8 },
        };
        // ISO 7089 平垫圈：内径 d1、外径 d2、厚 h
        static readonly Dictionary<string, double[]> Iso7089 = new Dictionary<string, double[]>
        {
            ["M2"] = new double[] { 2.2, 5, 0.3 }, ["M2.5"] = new double[] { 2.7, 6, 0.5 }, ["M3"] = new double[] { 3.2, 7, 0.5 }, ["M4"] = new double[] { 4.3, 9, 0.8 },
            ["M5"] = new double[] { 5.3, 10, 1 }, ["M6"] = new double[] { 6.4, 12, 1.6 }, ["M8"] = new double[] { 8.4, 16, 1.6 }, ["M10"] = new double[] { 10.5, 20, 2 },
            ["M12"] = new double[] { 13, 24, 2.5 },
        };
        static readonly int[] Lengths = { 3, 4, 5, 6, 8, 10, 12, 14, 16, 18, 20, 22, 25, 28, 30, 35, 40, 45, 50, 55, 60, 65, 70, 80, 90, 100 };
        static string[] LengthsFor(string size, double min, double max) =>
            Lengths.Where(l => l >= min && l <= max).Select(l => l.ToString()).ToArray();

        // 深沟球轴承：内径 d、外径 D、宽 B
        static readonly Dictionary<string, double[]> Bearings = new Dictionary<string, double[]>
        {
            ["MR63"] = new double[] { 3, 6, 2.5 }, ["MR85"] = new double[] { 5, 8, 2.5 }, ["MR105"] = new double[] { 5, 10, 4 }, ["MR115"] = new double[] { 5, 11, 4 },
            ["MR126"] = new double[] { 6, 12, 4 }, ["MR128"] = new double[] { 8, 12, 3.5 },
            ["623"] = new double[] { 3, 10, 4 }, ["624"] = new double[] { 4, 13, 5 }, ["625"] = new double[] { 5, 16, 5 }, ["626"] = new double[] { 6, 19, 6 },
            ["627"] = new double[] { 7, 22, 7 }, ["628"] = new double[] { 8, 24, 8 }, ["629"] = new double[] { 9, 26, 8 },
            ["604"] = new double[] { 4, 12, 4 }, ["605"] = new double[] { 5, 14, 5 }, ["606"] = new double[] { 6, 17, 6 }, ["607"] = new double[] { 7, 19, 6 },
            ["608"] = new double[] { 8, 22, 7 }, ["609"] = new double[] { 9, 24, 7 },
            ["688"] = new double[] { 8, 16, 5 }, ["689"] = new double[] { 9, 17, 5 }, ["698"] = new double[] { 8, 19, 6 },
            ["6000"] = new double[] { 10, 26, 8 }, ["6001"] = new double[] { 12, 28, 8 }, ["6002"] = new double[] { 15, 32, 9 }, ["6003"] = new double[] { 17, 35, 10 },
            ["6004"] = new double[] { 20, 42, 12 }, ["6005"] = new double[] { 25, 47, 12 },
            ["6200"] = new double[] { 10, 30, 9 }, ["6201"] = new double[] { 12, 32, 10 }, ["6202"] = new double[] { 15, 35, 11 }, ["6203"] = new double[] { 17, 40, 12 },
            ["6204"] = new double[] { 20, 47, 14 }, ["6205"] = new double[] { 25, 52, 15 },
            ["6800"] = new double[] { 10, 19, 5 }, ["6801"] = new double[] { 12, 21, 5 }, ["6802"] = new double[] { 15, 24, 5 }, ["6803"] = new double[] { 17, 26, 5 },
            ["6804"] = new double[] { 20, 32, 7 }, ["6805"] = new double[] { 25, 37, 7 },
        };

        // 步进电机：机身边长 A、倒角边长 C、安装孔距 P、孔径 h、孔深 hd、止口直径 Db、止口高 hb、可选机身长度
        sealed class Stepper { public double A, C, P, Hole, HoleDepth, Boss, BossH, ShaftLen; public string[] Lengths; public string[] Shafts; }
        static readonly Dictionary<string, Stepper> Steppers = new Dictionary<string, Stepper>
        {
            ["NEMA8 (20)"] = new Stepper { A = 20.3, C = 1.5, P = 16, Hole = 2, HoleDepth = 2.5, Boss = 15, BossH = 1.5, ShaftLen = 15, Lengths = new[] { "30", "38" }, Shafts = new[] { "4" } },
            ["NEMA11 (28)"] = new Stepper { A = 28.2, C = 2.5, P = 23, Hole = 2.5, HoleDepth = 3, Boss = 22, BossH = 1.5, ShaftLen = 20, Lengths = new[] { "32", "45", "51" }, Shafts = new[] { "5" } },
            ["NEMA14 (35)"] = new Stepper { A = 35.2, C = 3, P = 26, Hole = 3, HoleDepth = 3.5, Boss = 22, BossH = 2, ShaftLen = 24, Lengths = new[] { "28", "34", "42" }, Shafts = new[] { "5" } },
            ["NEMA17 (42)"] = new Stepper { A = 42.3, C = 4.5, P = 31, Hole = 3, HoleDepth = 4.5, Boss = 22, BossH = 2, ShaftLen = 24, Lengths = new[] { "34", "40", "48", "60" }, Shafts = new[] { "5" } },
            ["NEMA23 (57)"] = new Stepper { A = 56.4, C = 5.5, P = 47.14, Hole = 5, HoleDepth = 5, Boss = 38.1, BossH = 1.6, ShaftLen = 21, Lengths = new[] { "41", "56", "76" }, Shafts = new[] { "6.35", "8" } },
        };

        // 铝型材（欧标 B 型）单元：边长 a、槽口宽 w、槽口壁厚 t、槽腔宽 wi、槽深 ds、槽底倒角 cc、中心孔 dc
        static readonly Dictionary<string, double[]> ProfileCell = new Dictionary<string, double[]>
        {
            ["20"] = new double[] { 20, 6.2, 1.8, 10.6, 6.1, 2.2, 4.2 },
            ["30"] = new double[] { 30, 8.2, 2.2, 16.0, 9.0, 3.0, 6.8 },
            ["40"] = new double[] { 40, 8.2, 4.3, 20.0, 12.2, 4.0, 6.8 },
        };
        // 型材系列 → 单元边长、x 方向单元数、y 方向单元数
        static readonly Dictionary<string, (string cell, int nx, int ny)> ProfileSeries = new Dictionary<string, (string, int, int)>
        {
            ["2020"] = ("20", 1, 1), ["2040"] = ("20", 2, 1), ["2060"] = ("20", 3, 1), ["2080"] = ("20", 4, 1), ["4040A"] = ("20", 2, 2),
            ["3030"] = ("30", 1, 1), ["3060"] = ("30", 2, 1), ["3090"] = ("30", 3, 1),
            ["4040"] = ("40", 1, 1), ["4080"] = ("40", 2, 1),
        };
        internal static string[] ProfileNames => ProfileSeries.Keys.Where(k => k != "4040A").ToArray();
        internal static double ProfileWidth(string series) => ProfileCell[ProfileSeries[series].cell][0] * ProfileSeries[series].nx;
        internal static double ProfileHeight(string series) => ProfileCell[ProfileSeries[series].cell][0] * ProfileSeries[series].ny;

        /// <summary>
        /// 铝型材截面（米）：nx × ny 个单元拼成的矩形，四条边上每个单元一个 T 型槽，每个单元一个中心孔。
        /// 轮廓逆时针：底边从左到右、右边从下到上、顶边从右到左、左边从上到下，槽向内凹。
        /// </summary>
        internal static List<Loop> ProfileSection(string series)
        {
            var (cell, nx, ny) = ProfileSeries[series];
            var t = ProfileCell[cell].Select(v => v * MM).ToArray();
            double a = t[0], w = t[1] / 2, lip = t[2], wi = t[3] / 2, ds = t[4], cc = t[5];
            double W = a * nx, H = a * ny;
            // 槽的模板：(沿边方向 u, 向内深度 v)
            var slot = new[] { new V2(-w, 0), new V2(-w, lip), new V2(-wi, lip), new V2(-wi, ds - cc), new V2(-wi + cc, ds), new V2(wi - cc, ds), new V2(wi, ds - cc), new V2(wi, lip), new V2(w, lip), new V2(w, 0) };
            var pts = new List<V2>();
            void Side(V2 start, V2 dir, int cells)
            {
                var n = new V2(-dir.Y, dir.X);   // 逆时针转 90°：指向型材内部
                pts.Add(start);
                for (int i = 0; i < cells; i++)
                {
                    var c = start + dir * (a / 2 + i * a);
                    foreach (var s in slot) pts.Add(c + dir * s.X + n * s.Y);
                }
            }
            Side(new V2(-W / 2, -H / 2), new V2(1, 0), nx);
            Side(new V2(W / 2, -H / 2), new V2(0, 1), ny);
            Side(new V2(W / 2, H / 2), new V2(-1, 0), nx);
            Side(new V2(-W / 2, H / 2), new V2(0, -1), ny);
            var loops = new List<Loop> { Loop.Polygon(pts) };
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    loops.Add(Loop.Circle(new V2(-W / 2 + a / 2 + i * a, -H / 2 + a / 2 + j * a), t[6] / 2));
            return loops;
        }

        // ---------------- 零件类型 ----------------

        static List<PartType> Base() => new List<PartType>
        {
            new PartType
            {
                Id = "socket_cap", Category = "紧固件", Name = "内六角圆柱头螺钉", Standard = "ISO 4762",
                Note = "GB/T 70.1 同尺寸。螺纹按光杆画（与 Toolbox 简化画法一致）。",
                Params =
                {
                    Choice("size", "规格", _ => Metric, "M3"),
                    Choice("len", "长度", p => LengthsFor(p["size"], Nominal(p["size"]) * 1.5, Nominal(p["size"]) * 12), "10", "mm", "不含头部"),
                },
                Size = p => $"{p["size"]}×{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var d = Nominal(p["size"]) * MM; var L = D(p, "len") * MM; var t = Iso4762[p["size"]];
                    double dk = t[0] * MM, k = t[1] * MM, s = t[2] * MM, depth = t[3] * MM, c = 0.08 * d, e = 0.06 * dk;
                    return new PartGeom()
                        .Add(Op.Revolve("螺钉", Loop.Polygon(new V2(0, 0), new V2(d / 2 - c, 0), new V2(d / 2, c), new V2(d / 2, L), new V2(dk / 2, L),
                                                              new V2(dk / 2, L + k - e), new V2(dk / 2 - e, L + k), new V2(0, L + k))))
                        .Add(Op.Cut("内六角", L + k, depth, Loop.Hexagon(s)));
                },
            },
            new PartType
            {
                Id = "countersunk", Category = "紧固件", Name = "内六角沉头螺钉", Standard = "ISO 10642",
                Note = "GB/T 70.3 同尺寸。长度含头部。螺纹按光杆画。",
                Params =
                {
                    Choice("size", "规格", _ => Iso10642.Keys.ToArray(), "M3"),
                    Choice("len", "长度", p => LengthsFor(p["size"], Math.Max(6, Nominal(p["size"]) * 2), Nominal(p["size"]) * 12), "10", "mm", "含头部"),
                },
                Size = p => $"{p["size"]}×{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var d = Nominal(p["size"]) * MM; var L = D(p, "len") * MM; var t = Iso10642[p["size"]];
                    double dk = t[0] * MM, k = t[1] * MM, s = t[2] * MM, depth = t[3] * MM, c = 0.08 * d;
                    return new PartGeom()
                        .Add(Op.Revolve("螺钉", Loop.Polygon(new V2(0, 0), new V2(d / 2 - c, 0), new V2(d / 2, c), new V2(d / 2, L - k), new V2(dk / 2, L), new V2(0, L))))
                        .Add(Op.Cut("内六角", L, depth, Loop.Hexagon(s)));
                },
            },
            new PartType
            {
                Id = "hex_bolt", Category = "紧固件", Name = "外六角螺栓", Standard = "ISO 4017",
                Note = "GB/T 5783 同尺寸（全螺纹）。螺纹按光杆画。",
                Params =
                {
                    Choice("size", "规格", _ => Iso4017.Keys.ToArray(), "M5"),
                    Choice("len", "长度", p => LengthsFor(p["size"], Nominal(p["size"]) * 1.6, Nominal(p["size"]) * 12), "20", "mm", "不含头部"),
                },
                Size = p => $"{p["size"]}×{p["len"]}",
                Material = _ => "steel",
                Build = p =>
                {
                    var d = Nominal(p["size"]) * MM; var L = D(p, "len") * MM; var t = Iso4017[p["size"]];
                    double s = t[0] * MM, k = t[1] * MM, c = 0.08 * d;
                    return new PartGeom()
                        .Add(Op.Revolve("螺杆", Loop.Polygon(new V2(0, 0), new V2(d / 2 - c, 0), new V2(d / 2, c), new V2(d / 2, L), new V2(0, L))))
                        .Add(Op.Extrude("六角头", L, k, Loop.Hexagon(s)));
                },
            },
            new PartType
            {
                Id = "hex_nut", Category = "紧固件", Name = "六角螺母", Standard = "ISO 4032",
                Note = "GB/T 6170 同尺寸。孔按公称直径画。",
                Params = { Choice("size", "规格", _ => Metric, "M3") },
                Size = p => p["size"],
                Material = _ => "steel",
                Build = p =>
                {
                    var d = Nominal(p["size"]) * MM; var t = Iso4032[p["size"]];
                    return new PartGeom().Add(Op.Extrude("螺母", 0, t[1] * MM, Loop.Hexagon(t[0] * MM), Loop.Circle(d / 2)));
                },
            },
            new PartType
            {
                Id = "washer", Category = "紧固件", Name = "平垫圈", Standard = "ISO 7089",
                Note = "GB/T 97.1 同尺寸。",
                Params = { Choice("size", "规格", _ => Metric, "M3") },
                Size = p => p["size"],
                Material = _ => "steel",
                Build = p =>
                {
                    var t = Iso7089[p["size"]];
                    return new PartGeom().Add(Op.Extrude("垫圈", 0, t[2] * MM, Loop.Circle(t[1] / 2 * MM), Loop.Circle(t[0] / 2 * MM)));
                },
            },
            new PartType
            {
                Id = "ball_bearing", Category = "轴承", Name = "深沟球轴承", Standard = null,
                Note = "外形按内径、外径、宽度画，内外圈之间画出防尘盖的凹槽（简化外形，不含滚珠）。",
                Params = { Choice("model", "型号", _ => Bearings.Keys.ToArray(), "608") },
                Size = p => { var b = Bearings[p["model"]]; return $"{p["model"]}（{F(b[0])}×{F(b[1])}×{F(b[2])}）"; },
                Material = _ => "steel",
                Build = p =>
                {
                    var b = Bearings[p["model"]];
                    double ri = b[0] / 2 * MM, ro = b[1] / 2 * MM, B = b[2] * MM, h = ro - ri;
                    double a = ri + 0.3 * h, bb = ro - 0.3 * h, e = Math.Max(0.15 * MM, 0.08 * B), c = Math.Min(0.3 * MM, 0.08 * h);
                    return new PartGeom().Add(Op.Revolve("轴承", Loop.Polygon(
                        new V2(ri + c, 0), new V2(a, 0), new V2(a, e), new V2(bb, e), new V2(bb, 0), new V2(ro - c, 0), new V2(ro, c),
                        new V2(ro, B - c), new V2(ro - c, B), new V2(bb, B), new V2(bb, B - e), new V2(a, B - e), new V2(a, B),
                        new V2(ri + c, B), new V2(ri, B - c), new V2(ri, c))));
                },
            },
            new PartType
            {
                Id = "spur_gear", Category = "传动", Name = "直齿轮", Standard = null, AddAxis = true, Keywords = "齿轮 直齿轮 渐开线 变位 spur gear",
                Note = "渐开线齿廓（压力角 20°，可以变位）。标准中心距 = 模数 ×（齿数1 + 齿数2）÷ 2；只知道中心距时用「装配生成 → 齿轮副」自动选模数、齿数和变位。3D 打印建议齿侧间隙 0.1–0.2 mm。带一根名为「旋转轴」的基准轴，方便加配合。",
                Params =
                {
                    Choice("m", "模数", _ => new[] { "0.5", "0.8", "1", "1.25", "1.5", "2", "2.5", "3" }, "1"),
                    Number("z", "齿数", 8, 200, 20, "", "8–200"),
                    Number("b", "齿宽", 1, 100, 8, "mm"),
                    Choice("bore", "孔", _ => new[] { "圆孔", "D 形孔", "无孔" }, "圆孔"),
                    Number("d", "孔径", 1, 100, 5, "mm", "D 形孔的平面深 0.5 mm，适合步进电机轴"),
                    Number("hubD", "轮毂直径", 0, 200, 0, "mm", "0 表示不要轮毂"),
                    Number("hubL", "轮毂长度", 0, 100, 0, "mm"),
                    Number("bl", "齿侧间隙", 0, 1, 0, "mm"),
                    Number("x", "变位系数", -0.8, 1.5, 0, "", "0 = 标准齿轮；「装配生成 → 齿轮副」凑中心距时会自动算"),
                    Choice("mat", "材料", _ => new[] { "钢", "尼龙", "PLA", "不设置" }, "钢"),
                },
                Size = p => $"m{p["m"]} z{p["z"]} b{p["b"]}" + (p["bore"] == "无孔" ? "" : $" {(p["bore"] == "D 形孔" ? "D孔" : "孔")}{p["d"]}") +
                            (D(p, "hubD") > 0 && D(p, "hubL") > 0 ? $" hub{p["hubD"]}x{p["hubL"]}" : "") + (D(p, "bl") > 0 ? $" bl{p["bl"]}" : "") + (Math.Abs(D(p, "x")) > 1e-9 ? $" x{p["x"]}" : ""),
                Material = p => p["mat"] == "钢" ? "steel" : p["mat"] == "尼龙" ? "nylon" : p["mat"] == "PLA" ? "pla" : null,
                Build = p =>
                {
                    double m = D(p, "m") * MM, b = D(p, "b") * MM, bore = D(p, "d") * MM, hubD = D(p, "hubD") * MM, hubL = D(p, "hubL") * MM;
                    int z = (int)Math.Round(D(p, "z"));
                    var rf = m * z / 2 - (1.25 - D(p, "x")) * m;
                    var hole = p["bore"] == "无孔" ? null : p["bore"] == "D 形孔" ? Loop.DShape(bore / 2, bore / 2 - 0.5 * MM) : Loop.Circle(bore / 2);
                    if (hole != null && bore / 2 >= rf * 0.85) throw new ArgumentException("孔太大：孔径要明显小于齿根圆直径 " + F(rf * 2 / MM) + " mm");
                    var g = new PartGeom();
                    var outline = Gear.Outline(m, z, 20 * Math.PI / 180, D(p, "bl") * MM, 12, D(p, "x"));
                    g.Add(hole != null ? Op.Extrude("齿轮", 0, b, outline, hole) : Op.Extrude("齿轮", 0, b, outline));
                    if (hubD > 0 && hubL > 0)
                    {
                        if (hole != null && hubD <= bore) throw new ArgumentException("轮毂直径要大于孔径");
                        g.Add(hole != null ? Op.Extrude("轮毂", b, hubL, Loop.Circle(hubD / 2), hole) : Op.Extrude("轮毂", b, hubL, Loop.Circle(hubD / 2)));
                    }
                    return g;
                },
            },
            new PartType
            {
                Id = "stepper", Category = "电机", Name = "步进电机", Standard = null,
                Note = "NEMA 8 / 11 / 14 / 17 / 23 外形尺寸：机身、止口、带平面的输出轴、前端面 4 个安装孔。安装面在原点，轴朝 +Y。质量请按实物另行设置。", Keywords = "步进电机 42 57 35 28 20 stepper nema",
                Params =
                {
                    Choice("frame", "机座", _ => Steppers.Keys.ToArray(), "NEMA17 (42)"),
                    Choice("len", "机身长度", p => Steppers[p["frame"]].Lengths, "40", "mm"),
                    Choice("shaft", "轴径", p => Steppers[p["frame"]].Shafts, "5", "mm"),
                },
                Size = p => $"{p["frame"].Split(' ')[0]}-{p["len"]} 轴{p["shaft"]}",
                Build = p =>
                {
                    var s = Steppers[p["frame"]];
                    double A = s.A * MM, C = s.C * MM, L = D(p, "len") * MM, sd = D(p, "shaft") * MM;
                    double h = A / 2, c = h - C;
                    var body = Loop.Polygon(new V2(c, -h), new V2(h, -c), new V2(h, c), new V2(c, h), new V2(-c, h), new V2(-h, c), new V2(-h, -c), new V2(-c, -h));
                    double q = s.P / 2 * MM;
                    return new PartGeom()
                        .Add(Op.Extrude("机身", -L, L, body))
                        .Add(Op.Cut("安装孔", 0, s.HoleDepth * MM, Loop.Circle(new V2(q, q), s.Hole / 2 * MM), Loop.Circle(new V2(-q, q), s.Hole / 2 * MM),
                                                               Loop.Circle(new V2(-q, -q), s.Hole / 2 * MM), Loop.Circle(new V2(q, -q), s.Hole / 2 * MM)))
                        .Add(Op.Extrude("止口", 0, s.BossH * MM, Loop.Circle(s.Boss / 2 * MM)))
                        .Add(Op.Extrude("输出轴", s.BossH * MM, (s.ShaftLen - s.BossH) * MM, Loop.DShape(sd / 2, sd / 2 - 0.5 * MM)));
                },
            },
            new PartType
            {
                Id = "alu_profile", Category = "型材", Name = "铝型材", Standard = null,
                Note = "欧标 B 型（2020 系列 6 槽、3030 / 4040 系列 8 槽）。槽口宽度、槽口壁厚、中心孔按常用尺寸；槽内形状和多孔型材的内腔为简化画法，配 T 型螺母前请核对厂家图纸。",
                Keywords = "铝型材 欧标 2020 2040 3030 4040 型材 框架 aluminum profile extrusion",
                Params =
                {
                    Choice("series", "系列", _ => ProfileNames, "2020"),
                    Number("len", "长度", 5, 6000, 100, "mm"),
                },
                Size = p => $"{p["series"]} L{p["len"]}",
                Material = _ => "aluminum",
                Build = p => new PartGeom().Add(Op.Extrude("型材", 0, D(p, "len") * MM, ProfileSection(p["series"]))),
            },
        };

        /// <summary>分类的显示顺序。</summary>
        public static readonly string[] CategoryOrder = { "紧固件", "轴承", "直线运动", "传动", "型材", "结构件", "电机", "电子" };

        static List<PartType> _all;
        /// <summary>全部零件类型（第一次访问时组装：各分部文件里的列表按分类顺序排好）。</summary>
        public static List<PartType> All => _all ?? (_all = Base().Concat(Fasteners()).Concat(Motion()).Concat(Drive()).Concat(Structure()).Concat(Devices())
                                                              .OrderBy(t => Array.IndexOf(CategoryOrder, t.Category) is int i && i >= 0 ? i : 99).ToList());

        public static PartType Get(string id) => All.First(t => t.Id == id);
        public static PartType Find(string id) => All.FirstOrDefault(t => t.Id == id);
        public static IEnumerable<string> Categories => All.Select(t => t.Category).Distinct();

        /// <summary>按关键字搜索（名称、标准号、分类、编号、备注，忽略大小写；空格分隔的多个词都要匹配）。</summary>
        public static IEnumerable<PartType> Search(string text, string category = null)
        {
            var words = (text ?? "").Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToUpperInvariant()).ToList();
            // 先按名称、标准号、关键词匹配；再按规格 / 型号（例如 608、M3、GT2 20T）匹配，名称匹配的排在前面
            var byName = All.Where(t => (category == null || t.Category == category) &&
                                        words.All(w => (t.Name + " " + t.Standard + " " + t.Category + " " + t.Id + " " + t.Keywords + " " + t.Note).ToUpperInvariant().Contains(w))).ToList();
            var bySize = All.Where(t => (category == null || t.Category == category) && !byName.Contains(t) &&
                                        words.All(w => (t.Name + " " + t.Standard + " " + t.Keywords + " " + SizesText(t)).ToUpperInvariant().Contains(w)));
            return byName.Concat(bySize);
        }

        static readonly Dictionary<string, string> _sizes = new Dictionary<string, string>();
        /// <summary>零件类型所有「选择」参数的可选值（默认参数下），用于按型号搜索。</summary>
        static string SizesText(PartType t)
        {
            lock (_sizes)
            {
                if (_sizes.TryGetValue(t.Id, out var s)) return s;
                var sb = new System.Text.StringBuilder();
                try
                {
                    var p = t.Defaults();
                    foreach (var pd in t.Params.Where(x => x.Kind == ParamKind.Choice))
                        try { foreach (var c in pd.Choices(p)) sb.Append(' ').Append(c); } catch { }
                }
                catch { }
                return _sizes[t.Id] = sb.ToString();
            }
        }
    }
}
