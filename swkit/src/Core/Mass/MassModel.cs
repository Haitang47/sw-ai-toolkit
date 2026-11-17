using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SwKit.Core.Urdf;

namespace SwKit.Core.Mass
{
    /// <summary>装配体里的一种零件（同一个文件 + 配置算一种），数量 = 实例个数。几何量是 SI 单位（米）。</summary>
    public sealed class MassPart
    {
        public int Index;
        public string Key;                  // 文件路径（小写）+ 配置，用来记住材料选择
        public string Name, File, Config;
        public int Quantity;
        public double Volume, Area;         // m³、m²（单件）
        public Vec3 Size;                   // 包围盒尺寸（m）
        public string SwMaterial;           // SolidWorks 里设的材料名（没有为 null）
        public double SwDensity;            // kg/m³（有材料时）
        public List<Vec3> Coms = new List<Vec3>();   // 每个实例的质心（装配体坐标，m）；读不到时为空
        public string Note;                 // 读取时的问题
        internal Vec3? LocalCom;            // 零件坐标里的质心（读取时用）

        // ---- 用户的选择 ----
        public string Material;             // 材料名；SwChoice = 按 SolidWorks 里的材料；null = 还没选
        public string MaterialWhy;          // 为什么是这个材料（SolidWorks / 文件名 / 上次选的）
        public double? MeasuredGrams;       // 实测单件质量（g），填了就用它
        public bool Excluded;               // 不计入总质量
    }

    public sealed class MassAssembly
    {
        public string File, Name;
        public bool IsAssembly;
        public List<MassPart> Parts = new List<MassPart>();
        public List<string> Notes = new List<string>();
        public bool HasComs => Parts.Any(p => p.Coms.Count > 0);
        public int Instances => Parts.Sum(p => p.Quantity);
    }

    public sealed class MassSettings
    {
        public double Infill = 0.15;        // FDM 填充率
        public double ShellMm = 0.8;        // FDM 外壳厚度（壁 + 顶底，mm）
    }

    public sealed class MassSummary
    {
        public double TotalGrams;
        public int Counted, Unassigned, Excluded, Measured;
        public Vec3? Com;                   // 总质心（m），没有位置信息时为 null
        public List<(string material, double grams, int parts)> ByMaterial = new List<(string, double, int)>();
        public List<MassPart> UnassignedParts = new List<MassPart>();
    }

    /// <summary>质量计算。</summary>
    public static class MassCalc
    {
        public const string SwChoice = "@sw";

        /// <summary>零件当前材料的显示名。</summary>
        public static string MaterialLabel(MassPart p) =>
            p.Material == null ? "未选材料" : p.Material == SwChoice ? $"SolidWorks：{p.SwMaterial}" : p.Material;

        /// <summary>实心密度（g/cm³），没选材料返回 NaN。</summary>
        public static double Density(MassPart p)
        {
            if (p.Material == SwChoice) return p.SwDensity > 0 ? p.SwDensity / 1000 : double.NaN;
            var m = MassMaterials.Find(p.Material);
            return m?.Density ?? double.NaN;
        }

        public static bool IsFdm(MassPart p) => p.Material != null && p.Material != SwChoice && MassMaterials.Find(p.Material)?.Fdm == true;

        /// <summary>
        /// 计算用的有效体积（cm³）。FDM 打印件：外壳体积 ≈ 表面积 × 外壳厚度（不超过零件体积），里面按填充率；
        /// 其他工艺按实心。
        /// </summary>
        public static double EffectiveVolumeCm3(MassPart p, MassSettings s)
        {
            double v = p.Volume * 1e6, a = p.Area * 1e4;   // cm³、cm²
            if (!IsFdm(p)) return v;
            double shell = Math.Min(v, a * s.ShellMm / 10);
            return shell + (v - shell) * Math.Max(0, Math.Min(1, s.Infill));
        }

        /// <summary>单件质量（g）：实测值优先；没选材料返回 NaN。</summary>
        public static double UnitGrams(MassPart p, MassSettings s)
        {
            if (p.MeasuredGrams.HasValue) return p.MeasuredGrams.Value;
            double rho = Density(p);
            return double.IsNaN(rho) ? double.NaN : rho * EffectiveVolumeCm3(p, s);
        }

        public static double SubtotalGrams(MassPart p, MassSettings s) => p.Excluded ? 0 : UnitGrams(p, s) * p.Quantity;

        public static MassSummary Summarize(IEnumerable<MassPart> parts, MassSettings s)
        {
            var r = new MassSummary();
            var by = new Dictionary<string, (double g, int n)>();
            Vec3 moment = Vec3.Zero; double comMass = 0; bool comOk = true;
            foreach (var p in parts)
            {
                if (p.Excluded) { r.Excluded++; continue; }
                double unit = UnitGrams(p, s);
                if (double.IsNaN(unit)) { r.Unassigned++; r.UnassignedParts.Add(p); continue; }
                r.Counted++;
                if (p.MeasuredGrams.HasValue) r.Measured++;
                double sub = unit * p.Quantity;
                r.TotalGrams += sub;
                var key = p.MeasuredGrams.HasValue ? "实测质量" : MaterialLabel(p);
                by[key] = by.TryGetValue(key, out var x) ? (x.g + sub, x.n + 1) : (sub, 1);
                if (p.Coms.Count == p.Quantity && p.Quantity > 0) foreach (var c in p.Coms) { moment = moment + c * unit; comMass += unit; }
                else if (sub > 0) comOk = false;
            }
            if (comMass > 0 && comOk) r.Com = moment / comMass;
            r.ByMaterial = by.OrderByDescending(kv => kv.Value.g).Select(kv => (kv.Key, kv.Value.g, kv.Value.n)).ToList();
            return r;
        }

        static string F(double v, string f = "0.##") => double.IsNaN(v) ? "" : v.ToString(f, CultureInfo.InvariantCulture);

        /// <summary>零件清单 CSV（Excel 直接打开，UTF-8 带 BOM）。</summary>
        public static string ToCsv(MassAssembly asm, MassSettings s)
        {
            var sb = new StringBuilder();
            string Q(string x) => "\"" + (x ?? "").Replace("\"", "\"\"") + "\"";
            sb.AppendLine("零件,配置,数量,体积/cm³,表面积/cm²,材料,密度/(g/cm³),单件质量/g,小计/g,来源,备注");
            foreach (var p in asm.Parts)
            {
                double unit = UnitGrams(p, s);
                var src = p.Excluded ? "不计入" : p.MeasuredGrams.HasValue ? "实测" : IsFdm(p) ? $"FDM 外壳 {F(s.ShellMm)} mm + 填充 {F(s.Infill * 100, "0")}%" : double.IsNaN(unit) ? "未选材料" : "按体积 × 密度";
                sb.AppendLine(string.Join(",", Q(p.Name), Q(p.Config), p.Quantity, F(p.Volume * 1e6, "0.###"), F(p.Area * 1e4, "0.##"), Q(MaterialLabel(p)), F(Density(p), "0.###"),
                    F(unit, "0.##"), F(p.Excluded ? 0 : unit * p.Quantity, "0.##"), Q(src), Q(p.Note)));
            }
            var sum = Summarize(asm.Parts, s);
            sb.AppendLine();
            sb.AppendLine($"总质量/g,{F(sum.TotalGrams, "0.##")}");
            if (sum.Com is Vec3 c) sb.AppendLine($"重心/mm,{F(c.X * 1000, "0.##")},{F(c.Y * 1000, "0.##")},{F(c.Z * 1000, "0.##")}");
            if (sum.Unassigned > 0) sb.AppendLine($"未选材料（没有计入）,{sum.Unassigned} 种零件");
            return sb.ToString();
        }
    }

    /// <summary>记住每个零件选过的材料、实测质量（按零件文件 + 配置），下次打开同一个装配体或别的装配体用到同一零件时自动带上。</summary>
    public static class MassStore
    {
        static string PathOf() => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwKit", "mass-choices.json");
        public static string OverridePath;   // 测试用

        public static Dictionary<string, Dictionary<string, object>> Load()
        {
            try
            {
                var f = OverridePath ?? PathOf();
                if (!File.Exists(f)) return new Dictionary<string, Dictionary<string, object>>();
                var raw = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                return raw?.Where(kv => kv.Value is Dictionary<string, object>).ToDictionary(kv => kv.Key, kv => (Dictionary<string, object>)kv.Value) ?? new Dictionary<string, Dictionary<string, object>>();
            }
            catch { return new Dictionary<string, Dictionary<string, object>>(); }
        }

        /// <summary>把选择应用到零件上（只填用户选过的；返回应用了几个）。</summary>
        public static int Apply(IEnumerable<MassPart> parts)
        {
            var d = Load(); int n = 0;
            foreach (var p in parts)
            {
                if (!d.TryGetValue(p.Key, out var c)) continue;
                if (c.TryGetValue("material", out var m) && m is string ms && (ms == MassCalc.SwChoice ? p.SwMaterial != null : MassMaterials.Find(ms) != null))
                { p.Material = ms; p.MaterialWhy = "上次选的"; n++; }
                if (c.TryGetValue("measured", out var g) && g != null) { try { p.MeasuredGrams = Convert.ToDouble(g, CultureInfo.InvariantCulture); } catch { } }
                if (c.TryGetValue("excluded", out var x) && x is bool xb) p.Excluded = xb;
            }
            return n;
        }

        /// <summary>保存用户改过的零件（自动猜的材料不存，避免把猜测当成选择）。</summary>
        public static void Save(IEnumerable<MassPart> parts)
        {
            try
            {
                var d = Load();
                foreach (var p in parts)
                {
                    bool chosen = p.MaterialWhy == "手动选择" || p.MaterialWhy == "上次选的";
                    if (!chosen && !p.MeasuredGrams.HasValue && !p.Excluded) { d.Remove(p.Key); continue; }
                    var c = new Dictionary<string, object>();
                    if (chosen && p.Material != null) c["material"] = p.Material;
                    if (p.MeasuredGrams.HasValue) c["measured"] = p.MeasuredGrams.Value;
                    if (p.Excluded) c["excluded"] = true;
                    d[p.Key] = c;
                }
                var f = OverridePath ?? PathOf();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
                File.WriteAllText(f, new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(d), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>没选过的零件自动猜材料：SolidWorks 里设的材料 → 能对应到材料表就用表里的，否则直接用 SolidWorks 的密度；再按文件名猜。</summary>
        public static void AutoAssign(IEnumerable<MassPart> parts)
        {
            foreach (var p in parts)
            {
                if (p.Material != null) continue;
                if (!string.IsNullOrWhiteSpace(p.SwMaterial))
                {
                    var g = MassMaterials.Guess(p.SwMaterial, false, out _);
                    if (g != null) { p.Material = g.Name; p.MaterialWhy = $"SolidWorks 材料「{p.SwMaterial}」"; continue; }
                    if (p.SwDensity > 0) { p.Material = MassCalc.SwChoice; p.MaterialWhy = $"SolidWorks 材料「{p.SwMaterial}」（密度 {p.SwDensity / 1000:0.###}）"; continue; }
                }
                var byName = MassMaterials.Guess(p.Name, true, out var token);
                if (byName != null) { p.Material = byName.Name; p.MaterialWhy = $"文件名里有「{token}」"; }
            }
        }
    }
}
