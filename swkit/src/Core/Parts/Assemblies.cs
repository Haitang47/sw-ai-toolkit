using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwKit.Core.Urdf;

namespace SwKit.Core.Parts
{
    /// <summary>装配体里的一个零部件：零件类型 + 参数，以及在装配体里的位姿（零件坐标轴在装配体里的方向 + 原点位置，米）。</summary>
    public sealed class AsmPart
    {
        public PartType Type;
        public Dictionary<string, string> P;
        public Vec3 Ex = new Vec3(1, 0, 0), Ey = new Vec3(0, 1, 0), Ez = new Vec3(0, 0, 1), T;
        public string Role;   // 用于配合，例如 "gear1"、"gear2"

        public Vec3 Apply(Vec3 v) => Ex * v.X + Ey * v.Y + Ez * v.Z + T;

        /// <summary>绕 Y 轴转 angle（弧度，右手定则）。</summary>
        public AsmPart RotY(double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            Ex = new Vec3(c, 0, -s); Ey = new Vec3(0, 1, 0); Ez = new Vec3(s, 0, c);
            return this;
        }
        /// <summary>零件的 X、Y 轴分别对准装配体的 x、y 方向（Z = X × Y）。</summary>
        public AsmPart Orient(Vec3 x, Vec3 y) { Ex = x; Ey = y; Ez = x.Cross(y); return this; }
        public AsmPart At(double x, double y, double z) { T = new Vec3(x, y, z); return this; }
    }

    /// <summary>生成方案：零部件列表、文件名、说明（中心距、切割清单等）。</summary>
    public sealed class AsmPlan
    {
        public string Title;
        public List<AsmPart> Parts = new List<AsmPart>();
        public List<string> Info = new List<string>();
        public List<string> Warnings = new List<string>();
        /// <summary>需要加的配合：gear（两个齿轮的旋转轴 + 齿轮配合）；为 null 时全部固定。</summary>
        public string Mates;
        public int Z1, Z2;
        public Dictionary<string, object> Data = new Dictionary<string, object>();   // 给 API 返回的结构化数据

        /// <summary>预览网格（装配体坐标）。</summary>
        public PreviewMesh Preview()
        {
            var r = new PreviewMesh();
            var cache = new Dictionary<string, PreviewMesh>();
            foreach (var part in Parts)
            {
                var key = part.Type.Id + "|" + part.Type.Title(part.P);
                if (!cache.TryGetValue(key, out var m)) cache[key] = m = PartMesher.Build(part.Type.Build(part.P));
                Append(r.Body, m.Body, part); Append(r.Cuts, m.Cuts, part);
            }
            return r;
        }

        static void Append(Mesh dst, Mesh src, AsmPart part)
        {
            for (int i = 0; i < src.Data.Count; i += 3)
            {
                var v = part.Apply(new Vec3(src.Data[i], src.Data[i + 1], src.Data[i + 2]));
                dst.Data.Add((float)v.X); dst.Data.Add((float)v.Y); dst.Data.Add((float)v.Z);
            }
        }
    }

    /// <summary>装配生成器：参数界面和零件一样（PartType 的参数机制），但生成的是装配方案。</summary>
    public sealed class AsmType : PartType
    {
        public Func<Dictionary<string, string>, AsmPlan> Plan;
        public AsmType() { Category = "装配生成"; Material = _ => null; Build = p => throw new InvalidOperationException("装配生成器没有单个零件的几何"); }
    }

    /// <summary>齿轮副的一个可行方案。</summary>
    public sealed class GearPairOption
    {
        public double M; public int Z1, Z2; public double X1, X2, XSum;
        public double A0, A, AlphaW;           // 标准中心距、实际中心距（米）、啮合角（弧度）
        public double Ratio, RatioError;       // 实际传动比 z2/z1、相对误差
        public bool Shifted, Undercut, SecondSeries;
        public double Score;
        public string Label => $"m{F(M)} z{Z1}/{Z2}" + (Shifted ? $" 变位 {X1:+0.00;-0.00}/{X2:+0.00;-0.00}" : "") + $" i={Ratio:0.###}";
        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public static class Assemblies
    {
        const double MM = 0.001;
        static double D(Dictionary<string, string> p, string k) => double.Parse(p[k], CultureInfo.InvariantCulture);
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static readonly double Alpha = 20 * Math.PI / 180;

        static readonly double[] FirstSeries = { 0.3, 0.4, 0.5, 0.6, 0.8, 1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 8 };
        static readonly double[] SecondSeries = { 0.7, 0.9, 1.75, 2.25, 2.75, 3.5, 4.5, 5.5, 7 };

        /// <summary>
        /// 已知中心距 a（米）和目标传动比 i = z2/z1，列出可行的模数 / 齿数 / 变位组合，按好坏排序。
        /// 规则：模数 × 齿数和 = 2a 时用标准齿轮；凑不成整数时取相邻的齿数和，用变位凑中心距
        /// （无侧隙啮合方程：inv α' = inv α + 2 tan α (x1 + x2)/(z1 + z2)，cos α' = a0 cos α / a），总变位限制在 −0.8 … +1.2。
        /// 变位按齿数反比分配（小齿轮多分），并尽量让两个齿轮都不根切。
        /// </summary>
        public static List<GearPairOption> PlanGearPair(double a, double ratio, int z1Fixed = 0, double mFixed = 0, int max = 12)
        {
            if (a <= 0) throw new ArgumentException("中心距要大于 0");
            if (ratio <= 0) throw new ArgumentException("传动比要大于 0");
            var list = new List<GearPairOption>();
            var mods = mFixed > 0 ? new[] { mFixed } : FirstSeries.Concat(SecondSeries).ToArray();
            foreach (var m0 in mods)
            {
                double m = m0 * MM, zs = 2 * a / m;
                var sums = new List<int>();
                int zr = (int)Math.Round(zs);
                if (Math.Abs(zs - zr) < 1e-6) { sums.Add(zr); sums.Add(zr - 1); sums.Add(zr + 1); }
                else { sums.Add((int)Math.Floor(zs)); sums.Add((int)Math.Ceiling(zs)); sums.Add((int)Math.Floor(zs) - 1); sums.Add((int)Math.Ceiling(zs) + 1); }
                foreach (var zsum in sums)
                {
                    var z1s = z1Fixed > 0 ? new[] { z1Fixed } : new[] { (int)Math.Round(zsum / (1 + ratio)) - 1, (int)Math.Round(zsum / (1 + ratio)), (int)Math.Round(zsum / (1 + ratio)) + 1 };
                    foreach (var z1 in z1s.Distinct())
                    {
                        int z2 = zsum - z1;
                        if (z1 < 8 || z2 < 8 || z1 > 400 || z2 > 400) continue;
                        var o = new GearPairOption { M = m0, Z1 = z1, Z2 = z2, A = a, A0 = m * zsum / 2, SecondSeries = SecondSeries.Contains(m0) };
                        o.Ratio = (double)z2 / z1;
                        o.RatioError = Math.Abs(o.Ratio / ratio - 1);
                        if (Math.Abs(o.A0 - a) > 1e-9)
                        {
                            var cw = o.A0 * Math.Cos(Alpha) / a;
                            if (cw >= 1 || cw <= 0.5) continue;
                            o.AlphaW = Math.Acos(cw);
                            o.XSum = (Gear.Inv(o.AlphaW) - Gear.Inv(Alpha)) * zsum / (2 * Math.Tan(Alpha));
                            if (o.XSum < -0.8 || o.XSum > 1.2) continue;
                            o.Shifted = true;
                            o.X1 = o.XSum * z2 / zsum;
                            double min1 = Gear.MinShift(z1), min2 = Gear.MinShift(z2);
                            if (o.X1 < min1) o.X1 = Math.Min(min1, o.XSum - min2 > min1 ? o.XSum - min2 : min1);
                            o.X2 = o.XSum - o.X1;
                            if (o.X2 < min2 && o.XSum - min2 >= min1) { o.X2 = min2; o.X1 = o.XSum - min2; }
                            o.X1 = Math.Round(o.X1, 3); o.X2 = Math.Round(o.XSum - o.X1, 3);
                        }
                        else o.AlphaW = Alpha;
                        o.Undercut = o.X1 < Gear.MinShift(z1) - 0.03 || o.X2 < Gear.MinShift(z2) - 0.03;
                        // 评分：传动比误差最重要，其次是不要根切、不要变位、用第一系列模数、小齿轮齿数在 17–40 之间
                        o.Score = o.RatioError * 300 + (o.Undercut ? 3 : 0) + (o.Shifted ? 0.6 + Math.Abs(o.XSum) : 0) + (o.SecondSeries ? 0.8 : 0)
                                + (Math.Min(z1, z2) > 35 ? (Math.Min(z1, z2) - 35) * 0.03 : 0) + (Math.Min(z1, z2) < 17 ? (17 - Math.Min(z1, z2)) * 0.15 : 0)
                                + (m0 < 0.5 ? 0.3 : 0) + Math.Abs(Math.Min(z1, z2) - 20) * 0.004;   // 最后一项只用来在同分时偏向常见的 20 齿左右
                        list.Add(o);
                    }
                }
            }
            return list.GroupBy(o => o.Label).Select(g => g.First()).OrderBy(o => o.Score).Take(max).ToList();
        }

        /// <summary>同步带长度（节线长，米）：开口传动 L = 2C cos θ + π(D1 + D2)/2 + θ(D2 − D1)，sin θ = (D2 − D1)/(2C)。</summary>
        public static double BeltLength(double d1, double d2, double c)
        {
            var th = Math.Asin((d2 - d1) / (2 * c));
            return 2 * c * Math.Cos(th) + Math.PI * (d1 + d2) / 2 + th * (d2 - d1);
        }

        /// <summary>已知带长反求中心距（二分法）。</summary>
        public static double CenterForBelt(double d1, double d2, double length)
        {
            double lo = Math.Abs(d2 - d1) / 2 + 1e-6, hi = length;
            for (int i = 0; i < 200; i++)
            {
                var mid = (lo + hi) / 2;
                if (BeltLength(d1, d2, mid) < length) lo = mid; else hi = mid;
            }
            return (lo + hi) / 2;
        }

        static ParamDef Num(string key, string label, double min, double max, double def, string unit, string hint = null) =>
            new ParamDef { Key = key, Label = label, Kind = ParamKind.Number, Min = min, Max = max, Default = _ => def.ToString("0.###", CultureInfo.InvariantCulture), Unit = unit, Hint = hint };
        static ParamDef Pick(string key, string label, Func<Dictionary<string, string>, string[]> choices, string def, string unit = null, string hint = null) =>
            new ParamDef { Key = key, Label = label, Kind = ParamKind.Choice, Choices = choices, Default = p => { var c = choices(p); return c.Contains(def) ? def : c[0]; }, Unit = unit, Hint = hint };

        // ================= 齿轮副 =================

        static List<GearPairOption> Options(Dictionary<string, string> p)
        {
            try { return PlanGearPair(D(p, "a") * MM, D(p, "i"), (int)Math.Round(D(p, "z1")), D(p, "m")); }
            catch { return new List<GearPairOption>(); }
        }

        public static readonly AsmType GearPair = new AsmType
        {
            Id = "gear_pair", Name = "齿轮副（按中心距）", Keywords = "齿轮副 中心距 传动比 变位 gear pair",
            Note = "只知道两根轴的中心距和想要的传动比时，自动列出可行的模数、齿数组合：能用标准齿轮凑整就用标准齿轮，凑不成整数时用变位齿轮精确凑中心距。生成两个齿轮和装配体，齿对齿槽摆好；能加 SolidWorks 齿轮配合时会加上（转一个另一个跟着转），否则固定位置。",
            Params =
            {
                Num("a", "中心距", 2, 2000, 30, "mm"),
                Num("i", "传动比 z2/z1", 0.05, 20, 2, "", "大于 1 = 减速"),
                Num("z1", "小齿轮齿数", 0, 300, 0, "", "0 = 自动"),
                Num("m", "模数", 0, 20, 0, "mm", "0 = 自动"),
                Pick("plan", "方案", p => { var o = Options(p); return o.Count > 0 ? o.Select(x => x.Label).ToArray() : new[] { "没有可行方案" }; }, "", null, "按推荐程度排序，第一个最好"),
                Num("b", "齿宽", 1, 200, 8, "mm"),
                Num("d1", "齿轮 1 孔径", 0, 200, 5, "mm", "0 = 无孔"),
                Num("d2", "齿轮 2 孔径", 0, 200, 5, "mm", "0 = 无孔"),
                Num("bl", "齿侧间隙", 0, 1, 0.05, "mm", "3D 打印建议 0.1–0.2"),
                Pick("mat", "材料", _ => new[] { "钢", "尼龙", "PLA", "不设置" }, "钢"),
                Num("plate", "安装板孔径", 0, 200, 0, "mm", "大于 0 时在齿轮下方加一块带两个孔的安装板（例如 608 轴承填 22）"),
            },
            Size = p => { var o = Pick(p); return o == null ? $"a{p["a"]} 无方案" : $"a{F(o.A / MM)} m{F(o.M)} z{o.Z1}-{o.Z2}" + (o.Shifted ? " 变位" : ""); },
            Plan = p => GearPairPlan(p),
        };

        static GearPairOption Pick(Dictionary<string, string> p) => Options(p).FirstOrDefault(o => o.Label == p["plan"]) ?? Options(p).FirstOrDefault();

        static AsmPlan GearPairPlan(Dictionary<string, string> p)
        {
            var o = Pick(p) ?? throw new ArgumentException("这个中心距和传动比找不到可行方案：换一个模数或放宽传动比");
            var gear = Catalog.Get("spur_gear");
            Dictionary<string, string> G(int z, double x, double bore) => new Dictionary<string, string>
            {
                ["m"] = F(o.M), ["z"] = z.ToString(), ["b"] = p["b"], ["bore"] = bore > 0 ? "圆孔" : "无孔", ["d"] = F(bore > 0 ? bore : 1),
                ["hubD"] = "0", ["hubL"] = "0", ["bl"] = p["bl"], ["x"] = F(x), ["mat"] = p["mat"],
            };
            // spur_gear 的模数是可选项：不在列表里的模数（例如 0.4、1.75）直接用，不经过 Normalize
            var plan = new AsmPlan { Title = $"齿轮副 {GearPair.Size(p)}", Mates = "gear", Z1 = o.Z1, Z2 = o.Z2 };
            plan.Parts.Add(new AsmPart { Type = gear, P = G(o.Z1, o.X1, D(p, "d1")), Role = "gear1" });
            // 齿轮 2 在 +X 方向：它朝向齿轮 1 的方向（草图角度 π）要对着齿槽。偶数齿时 π 处正好是齿，转半个齿距
            plan.Parts.Add(new AsmPart { Type = gear, P = G(o.Z2, o.X2, D(p, "d2")), Role = "gear2" }.RotY(o.Z2 % 2 == 0 ? Math.PI / o.Z2 : 0).At(o.A, 0, 0));
            double m = o.M * MM, ra1 = m * o.Z1 / 2 + m * (1 + o.X1), ra2 = m * o.Z2 / 2 + m * (1 + o.X2);
            plan.Info.Add($"中心距 {F(o.A / MM)} mm（标准中心距 {F(o.A0 / MM)} mm）");
            plan.Info.Add($"模数 {F(o.M)}，齿数 {o.Z1} / {o.Z2}，传动比 {o.Ratio:0.####}（目标 {p["i"]}，误差 {o.RatioError:P2}）");
            plan.Info.Add($"分度圆直径 {F(m * o.Z1 / MM)} / {F(m * o.Z2 / MM)} mm，齿顶圆直径 {F(2 * ra1 / MM)} / {F(2 * ra2 / MM)} mm");
            if (o.Shifted)
            {
                plan.Info.Add($"变位系数 x1 = {o.X1:+0.000;-0.000}，x2 = {o.X2:+0.000;-0.000}（总变位 {o.XSum:+0.000;-0.000}），啮合角 {o.AlphaW * 180 / Math.PI:0.00}°");
                var dy = o.XSum - (o.A - o.A0) / m;
                if (Math.Abs(dy) > 0.005) plan.Info.Add($"没有做齿顶缩短：顶隙比标准值变化 {F(-dy * o.M)} mm（{(dy > 0 ? "略小" : "略大")}），一般没有影响");
            }
            if (o.Undercut) plan.Warnings.Add("小齿轮齿数少于 17 且变位不足，会有轻微根切（齿根变细）。可以选其他方案，或者指定更多的齿数");
            var plateHole = D(p, "plate") * MM;
            if (plateHole > 0)
            {
                double margin = Math.Max(8 * MM, 0.4 * plateHole), t = 5 * MM;
                var plateType = new PartType
                {
                    Id = "gear_plate", Category = "装配", Name = "齿轮安装板",
                    Params = { },
                    Size = _ => $"a{F(o.A / MM)} 孔{F(plateHole / MM)}",
                    Material = _ => "aluminum",
                    Build = _ =>
                    {
                        double L = o.A + plateHole + 2 * margin, W = plateHole + 2 * margin;
                        return new PartGeom().Add(Op.Extrude("安装板", 0, t, Loop.RoundedRect(L, W, W / 2 * 0.99), Loop.Circle(new V2(-o.A / 2, 0), plateHole / 2), Loop.Circle(new V2(o.A / 2, 0), plateHole / 2)));
                    },
                };
                plan.Parts.Add(new AsmPart { Type = plateType, P = new Dictionary<string, string>(), Role = "plate" }.At(o.A / 2, -t - 1 * MM, 0));
                plan.Info.Add($"安装板：两孔中心距 {F(o.A / MM)} mm，孔径 {F(plateHole / MM)} mm，厚 5 mm，在齿轮下方 1 mm");
            }
            plan.Data["module"] = o.M; plan.Data["z1"] = o.Z1; plan.Data["z2"] = o.Z2; plan.Data["x1"] = o.X1; plan.Data["x2"] = o.X2;
            plan.Data["center_distance_mm"] = o.A / MM; plan.Data["standard_center_distance_mm"] = o.A0 / MM; plan.Data["ratio"] = o.Ratio;
            plan.Data["shifted"] = o.Shifted; plan.Data["undercut"] = o.Undercut;
            return plan;
        }

        // ================= 铝型材框架 =================

        public static readonly AsmType Frame = new AsmType
        {
            Id = "profile_frame", Name = "铝型材框架", Keywords = "框架 机架 型材框 铝型材 立方框 frame",
            Note = "输入外形尺寸（长 × 宽 × 高）和型材系列，生成 12 根型材的立方框：4 根立柱全高，8 根横梁夹在立柱之间（长度自动扣除型材宽度），可以在每根横梁两端加角码。附切割清单。",
            Params =
            {
                Num("l", "长（X）", 40, 5000, 400, "mm"),
                Num("w", "宽（Z）", 40, 5000, 300, "mm"),
                Num("h", "高（Y）", 40, 5000, 300, "mm"),
                Pick("series", "型材", _ => new[] { "2020", "3030", "4040" }, "2020"),
                Pick("brackets", "角码", _ => new[] { "每个连接处 1 个", "不要" }, "每个连接处 1 个"),
            },
            Size = p => $"{p["series"]} {p["l"]}×{p["w"]}×{p["h"]}" + (p["brackets"] == "不要" ? " 无角码" : ""),
            Plan = p => FramePlan(p),
        };

        static AsmPlan FramePlan(Dictionary<string, string> p)
        {
            double L = D(p, "l") * MM, W = D(p, "w") * MM, H = D(p, "h") * MM, a = double.Parse(p["series"].Substring(0, 2), CultureInfo.InvariantCulture) * MM;
            if (L <= 3 * a || W <= 3 * a || H <= 3 * a) throw new ArgumentException($"尺寸太小：每个方向都要大于 {F(3 * a / MM)} mm");
            var prof = Catalog.Get("alu_profile");
            var plan = new AsmPlan { Title = $"型材框架 {Frame.Size(p)}" };
            Dictionary<string, string> Len(double len) => new Dictionary<string, string> { ["series"] = p["series"], ["len"] = F(Math.Round(len / MM, 2)) };
            var X = new Vec3(1, 0, 0); var Y = new Vec3(0, 1, 0); var Z = new Vec3(0, 0, 1);
            double hx = L / 2 - a / 2, hz = W / 2 - a / 2;
            // 立柱：零件轴线就是 +Y
            foreach (var sx in new[] { -1, 1 }) foreach (var sz in new[] { -1, 1 })
                plan.Parts.Add(new AsmPart { Type = prof, P = Len(H) }.At(sx * hx, 0, sz * hz));
            // X 向横梁：零件 +Y → 装配 +X；Z 向横梁：零件 +Y → 装配 +Z
            foreach (var y in new[] { a / 2, H - a / 2 })
            {
                foreach (var sz in new[] { -1, 1 })
                    plan.Parts.Add(new AsmPart { Type = prof, P = Len(L - 2 * a) }.Orient(-Y, X).At(-L / 2 + a, y, sz * hz));
                foreach (var sx in new[] { -1, 1 })
                    plan.Parts.Add(new AsmPart { Type = prof, P = Len(W - 2 * a) }.Orient(X, Z).At(sx * hx, y, -W / 2 + a));
            }
            if (p["brackets"] != "不要")
            {
                var br = Catalog.Get("corner_bracket");
                var bp = new Dictionary<string, string> { ["series"] = p["series"] };
                // 角码：零件 +X = 沿横梁离开立柱的方向，零件 +Y = 沿立柱离开横梁的方向（底层朝上、顶层朝下），外角贴在立柱面和横梁面的交线上
                foreach (var (y, up) in new[] { (a, Y), (H - a, -Y) })
                {
                    foreach (var sz in new[] { -1, 1 })
                        foreach (var sx in new[] { -1, 1 })
                        {
                            // X 向横梁两端
                            plan.Parts.Add(new AsmPart { Type = br, P = bp }.Orient(X * -sx, up).At(sx * (L / 2 - a), y, sz * hz));
                            // Z 向横梁两端
                            plan.Parts.Add(new AsmPart { Type = br, P = bp }.Orient(Z * -sz, up).At(sx * hx, y, sz * (W / 2 - a)));
                        }
                }
            }
            plan.Info.Add($"外形 {F(L / MM)} × {F(W / MM)} × {F(H / MM)} mm，{p["series"]} 型材");
            plan.Info.Add($"切割清单：{F(H / MM)} mm × 4（立柱）、{F((L - 2 * a) / MM)} mm × 4（长向横梁）、{F((W - 2 * a) / MM)} mm × 4（宽向横梁）");
            var total = 4 * H + 4 * (L - 2 * a) + 4 * (W - 2 * a);
            plan.Info.Add($"型材总长 {F(total / MM)} mm" + (p["brackets"] != "不要" ? "，角码 16 个（每个角码配 2 套 T 型螺母和螺钉）" : ""));
            plan.Data["cut_list_mm"] = new Dictionary<string, object> { [F(H / MM)] = 4, [F((L - 2 * a) / MM)] = 4, [F((W - 2 * a) / MM)] = 4 };
            plan.Data["total_length_mm"] = total / MM;
            return plan;
        }

        // ================= 同步带传动 =================

        public static readonly AsmType BeltDrive = new AsmType
        {
            Id = "belt_drive", Name = "同步带传动", Keywords = "同步带 皮带 带轮 中心距 带长 GT2 belt drive",
            Note = "给两个带轮的齿数和大致中心距，自动选最接近的标准闭口带长，再反算精确中心距，生成两个带轮和皮带的装配体。还会检查小带轮的啮合齿数（建议 ≥ 6）。",
            Params =
            {
                Pick("profile", "齿形", _ => Catalog.Belts.Keys.ToArray(), "GT2"),
                Pick("z1", "主动轮齿数", p => Catalog.Belts[p["profile"]].Teeth, "20"),
                Pick("z2", "从动轮齿数", p => Catalog.Belts[p["profile"]].Teeth, "40"),
                Num("c", "大致中心距", 10, 3000, 100, "mm"),
                Pick("w", "带宽", p => Catalog.Belts[p["profile"]].Widths, "6", "mm"),
                Num("d1", "主动轮孔径", 1, 60, 5, "mm"),
                Num("d2", "从动轮孔径", 1, 60, 8, "mm"),
            },
            Size = p => { var (len, c) = Belt(p); return $"{p["profile"]} {p["z1"]}-{p["z2"]}T L{F(len / MM)} C{F(Math.Round(c / MM, 2))} W{p["w"]}"; },
            Plan = p => BeltPlan(p),
        };

        /// <summary>选标准带长（与大致中心距对应的带长最接近的那个），返回带长和精确中心距（米）。</summary>
        static (double len, double c) Belt(Dictionary<string, string> p)
        {
            var b = Catalog.Belts[p["profile"]];
            double d1 = b.PitchDiameter(int.Parse(p["z1"])) * MM, d2 = b.PitchDiameter(int.Parse(p["z2"])) * MM, c0 = D(p, "c") * MM;
            if (c0 <= (d1 + d2) / 2 + 2 * MM) c0 = (d1 + d2) / 2 + 2 * MM;
            var want = BeltLength(d1, d2, c0);
            var len = b.Lengths.Select(l => l * MM).Where(l => l > BeltLength(d1, d2, (d1 + d2) / 2 + 1 * MM)).OrderBy(l => Math.Abs(l - want)).FirstOrDefault();
            if (len <= 0) len = Math.Round(want / (b.Pitch * MM)) * b.Pitch * MM;
            return (len, CenterForBelt(d1, d2, len));
        }

        static AsmPlan BeltPlan(Dictionary<string, string> p)
        {
            var b = Catalog.Belts[p["profile"]];
            int z1 = int.Parse(p["z1"]), z2 = int.Parse(p["z2"]);
            var (len, c) = Belt(p);
            double d1 = b.PitchDiameter(z1) * MM, d2 = b.PitchDiameter(z2) * MM, w = D(p, "w") * MM;
            var pul = Catalog.Get("timing_pulley");
            Dictionary<string, string> P(int z, string bore) => new Dictionary<string, string>
            {
                ["profile"] = p["profile"], ["z"] = z.ToString(), ["w"] = p["w"], ["d"] = bore, ["hubD"] = "0", ["hubL"] = F(b.Pitch * 3.5), ["flange"] = "双挡边", ["mat"] = "铝合金",
            };
            var p1 = P(z1, p["d1"]); var p2 = P(z2, p["d2"]);
            pul.Normalize(p1); pul.Normalize(p2);
            // 带轮的齿中间高度对齐到 y = 0（两个带轮规格不同时轮毂高度可能不同）
            Catalog.PulleyGeom(p["profile"], z1, D(p, "w"), D(p1, "d") * MM, 0, D(p1, "hubL") * MM, true, out var mid1);
            Catalog.PulleyGeom(p["profile"], z2, D(p, "w"), D(p2, "d") * MM, 0, D(p2, "hubL") * MM, true, out var mid2);
            var plan = new AsmPlan { Title = $"同步带传动 {BeltDrive.Size(p)}" };
            plan.Parts.Add(new AsmPart { Type = pul, P = p1, Role = "pulley1" }.At(0, -mid1, 0));
            plan.Parts.Add(new AsmPart { Type = pul, P = p2, Role = "pulley2" }.At(c, -mid2, 0));
            // 皮带：节线外 0.6 倍齿高到节线内（带轮外圆）之间的环带
            double inner = b.PitchDiameter(1) * 0 + b.Pld / 2 * MM, back = b.Depth * 0.85 * MM;
            var beltType = new PartType
            {
                Id = "timing_belt", Category = "装配", Name = "同步带",
                Params = { },
                Size = _ => $"{p["profile"]}-{F(len / MM)} W{p["w"]} 带轮{z1}-{z2}T",
                Material = _ => "rubber",
                Build = _ => new PartGeom().Add(Op.Extrude("同步带", -w / 2, w, BeltLoop(d1 / 2 + back, d2 / 2 + back, c), BeltLoop(d1 / 2 - inner, d2 / 2 - inner, c))),
            };
            plan.Parts.Add(new AsmPart { Type = beltType, P = new Dictionary<string, string>(), Role = "belt" });
            var th = Math.Asin((d2 - d1) / (2 * c));
            var wrap = Math.PI - 2 * Math.Abs(th);
            var teethInMesh = Math.Min(z1, z2) * wrap / (2 * Math.PI);
            plan.Info.Add($"标准带长 {F(len / MM)} mm（{F(len / MM / b.Pitch)} 齿），精确中心距 {F(Math.Round(c / MM, 3))} mm（你给的是 {p["c"]} mm）");
            plan.Info.Add($"节径 {F(d1 / MM)} / {F(d2 / MM)} mm，传动比 {(double)z2 / z1:0.###}，小带轮包角 {wrap * 180 / Math.PI:0.#}°，啮合齿数 {teethInMesh:0.#}");
            if (teethInMesh < 6) plan.Warnings.Add("小带轮啮合齿数少于 6，容易跳齿：加大中心距或小带轮齿数");
            plan.Data["belt_length_mm"] = len / MM; plan.Data["center_distance_mm"] = c / MM; plan.Data["teeth_in_mesh"] = teethInMesh;
            return plan;
        }

        /// <summary>两个圆（圆心 (0,0) 半径 r1，圆心 (c,0) 半径 r2）的外公切线围成的环形轮廓（逆时针）。</summary>
        internal static Loop BeltLoop(double r1, double r2, double c)
        {
            var th = Math.Asin((r2 - r1) / c);
            V2 N(double sy) => new V2(-Math.Sin(th), sy * Math.Cos(th));
            var c1 = new V2(0, 0); var c2 = new V2(c, 0);
            return new PathBuilder(c1 + N(-1) * r1).Line(c2 + N(-1) * r2).Arc(c2, c2 + N(1) * r2).Line(c1 + N(1) * r1).Arc(c1, c1 + N(-1) * r1).Close();
        }

        public static readonly List<AsmType> All = new List<AsmType> { GearPair, Frame, BeltDrive };
        public static AsmType Find(string id) => All.FirstOrDefault(a => a.Id == id);
    }
}
