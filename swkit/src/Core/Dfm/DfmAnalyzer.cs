using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwKit.Core.Urdf;

namespace SwKit.Core.Dfm
{
    /// <summary>工艺。Sls = 尼龙粉末烧结（SLS / MJF），Slm = 金属粉末熔化（SLM / DMLS），Sheet = 钣金（激光切割 + 折弯），Cut = 板材切割（2D），Mold = 注塑。</summary>
    public enum DfmProcess { Fdm, Sla, Cnc, Sls, Slm, Sheet, Cut, Mold }
    public enum Severity { Error, Warning, Info }

    [Flags]
    public enum TriFlag : ushort { None = 0, Overhang = 1, Thin = 2, Undercut = 4, Corner = 8, OnPlate = 16, Hole = 32, Bend = 64, Draft = 128, Thick = 256, Cavity = 512 }

    public static class DfmProcesses
    {
        /// <summary>命令行 / 接口里用的工艺代号。</summary>
        public static string Id(DfmProcess p) => new[] { "fdm", "sla", "cnc", "sls", "slm", "sheet", "cut", "mold" }[(int)p];
        public const string Ids = "fdm | sla | cnc | sls | slm | sheet | cut | mold";

        /// <summary>按代号或中文名识别工艺（认不出返回 null）。</summary>
        public static DfmProcess? Parse(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return null;
            if (s == "fdm" || s.Contains("fdm") || s.Contains("熔融")) return DfmProcess.Fdm;
            if (s == "sla" || s.Contains("sla") || s.Contains("dlp") || s.Contains("光固化") || s.Contains("树脂")) return DfmProcess.Sla;
            if (s == "sls" || s.Contains("sls") || s.Contains("mjf") || s.Contains("尼龙") || s.Contains("烧结")) return DfmProcess.Sls;
            if (s == "slm" || s.Contains("slm") || s.Contains("dmls") || s.Contains("金属打印") || s.Contains("金属 3d") || s.Contains("金属3d") || s.Contains("熔化")) return DfmProcess.Slm;
            if (s == "sheet" || s.Contains("sheet") || s.Contains("钣金") || s.Contains("折弯")) return DfmProcess.Sheet;
            if (s == "cut" || s.Contains("plate") || s.Contains("laser") || s.Contains("waterjet") || s.Contains("router") || s.Contains("板材") || s.Contains("切割") || s.Contains("碳板") || s.Contains("激光") || s.Contains("水刀") || s.Contains("雕刻")) return DfmProcess.Cut;
            if (s == "mold" || s == "mould" || s.Contains("mold") || s.Contains("injection") || s.Contains("注塑") || s.Contains("开模")) return DfmProcess.Mold;
            if (s == "cnc" || s.Contains("cnc") || s.Contains("铣") || s.Contains("mill")) return DfmProcess.Cnc;
            return null;
        }

        public static bool Printing(DfmProcess p) => p == DfmProcess.Fdm || p == DfmProcess.Sla || p == DfmProcess.Sls || p == DfmProcess.Slm;
    }

    public sealed class Printer
    {
        public string Name; public double X, Y, Z; public bool Resin, Mjf;
        /// <summary>尼龙粉末烧结（SLS / MJF）设备。</summary>
        public static readonly List<Printer> Sls = new List<Printer>
        {
            new Printer { Name = "EOS P 396（SLS）", X = 340, Y = 340, Z = 600 },
            new Printer { Name = "HP MJF 5200", X = 380, Y = 284, Z = 380, Mjf = true },
            new Printer { Name = "华曙 HT403P（SLS）", X = 400, Y = 400, Z = 450 },
        };
        /// <summary>金属粉末熔化（SLM / DMLS）设备。</summary>
        public static readonly List<Printer> Slm = new List<Printer>
        {
            new Printer { Name = "EOS M 290", X = 250, Y = 250, Z = 325 },
            new Printer { Name = "铂力特 BLT-S310", X = 250, Y = 250, Z = 400 },
            new Printer { Name = "华曙 FS421M", X = 425, Y = 425, Z = 420 },
        };
        public static readonly List<Printer> All = new List<Printer>
        {
            new Printer { Name = "拓竹 X1 / P1 系列", X = 256, Y = 256, Z = 256 },
            new Printer { Name = "拓竹 A1", X = 256, Y = 256, Z = 256 },
            new Printer { Name = "拓竹 A1 mini", X = 180, Y = 180, Z = 180 },
            new Printer { Name = "Prusa MK4", X = 250, Y = 210, Z = 220 },
            new Printer { Name = "创想三维 Ender-3 V3", X = 220, Y = 220, Z = 250 },
            new Printer { Name = "Voron 2.4（350）", X = 350, Y = 350, Z = 340 },
            new Printer { Name = "Elegoo Saturn 4 Ultra（光固化）", X = 218, Y = 123, Z = 220, Resin = true },
            new Printer { Name = "Formlabs Form 4（光固化）", X = 200, Y = 125, Z = 210, Resin = true },
        };
    }

    public sealed class PrintMaterial
    {
        public string Name; public double Density, PricePerKg; public bool Resin;   // g/cm³，元/kg（树脂按元/kg）
        public static readonly List<PrintMaterial> All = new List<PrintMaterial>
        {
            new PrintMaterial { Name = "PLA", Density = 1.24, PricePerKg = 80 },
            new PrintMaterial { Name = "PETG", Density = 1.27, PricePerKg = 80 },
            new PrintMaterial { Name = "ABS", Density = 1.05, PricePerKg = 90 },
            new PrintMaterial { Name = "PA-CF 尼龙碳纤维", Density = 1.15, PricePerKg = 300 },
            new PrintMaterial { Name = "TPU", Density = 1.21, PricePerKg = 150 },
            new PrintMaterial { Name = "标准光敏树脂", Density = 1.18, PricePerKg = 200, Resin = true },
            new PrintMaterial { Name = "韧性树脂", Density = 1.15, PricePerKg = 400, Resin = true },
        };
    }

    public sealed class CncMaterial
    {
        public string Name; public double Density, PricePerKg, Mrr, MinWall; bool _metal;   // g/cm³、元/kg、粗加工去除率 cm³/min、最小壁厚 mm
        public bool Metal => _metal;
        static CncMaterial M(string n, double rho, double price, double mrr, double wall, bool metal) => new CncMaterial { Name = n, Density = rho, PricePerKg = price, Mrr = mrr, MinWall = wall, _metal = metal };
        public static readonly List<CncMaterial> All = new List<CncMaterial>
        {
            M("6061 铝", 2.70, 40, 12, 1.0, true), M("7075 铝", 2.81, 70, 10, 1.0, true), M("45 钢", 7.85, 15, 3, 1.0, true),
            M("304 不锈钢", 8.0, 30, 2, 1.0, true), M("黄铜", 8.5, 60, 10, 1.0, true), M("POM", 1.41, 50, 25, 1.5, false), M("尼龙", 1.15, 60, 25, 1.5, false),
        };
    }

    public sealed class DfmSettings
    {
        public DfmProcess Process = DfmProcess.Fdm;
        // 打印
        public double OverhangDeg = 45;          // 自支撑的最大悬垂角（与竖直方向），超过就要支撑
        public double MinWallPrint = double.NaN; // NaN = 按工艺默认（FDM 0.8，光固化 0.5）
        public double Infill = 0.15, LayerHeight = 0.2, ShellThickness = 0.8;
        public double FlowMm3PerS = 10;          // 平均挤出量（含空驶、回抽的折算）
        public double SecondsPerLayer = 4;       // 每层的固定开销（换层、回抽、擦嘴）
        public double ResinSecondsPerLayer = 7;  // 光固化每层（曝光 + 抬升）
        public double SupportDensity = 0.15;
        public Printer Printer = Printer.All[0];
        public PrintMaterial PrintMaterial = PrintMaterial.All[0];
        public double MinHoleUser = double.NaN;  // NaN = 按工艺默认（FDM / 光固化 2，尼龙烧结 1.5，金属打印 1）
        // CNC
        public CncMaterial CncMaterial = CncMaterial.All[0];
        public double MinWallCnc = double.NaN;   // NaN = 按材料默认
        public bool FiveAxisTilts;               // 3+2：除了 6 个正交方向，再考虑 45° 斜向
        public double StockMargin = 3, SetupMinutes = 15, FinishCm2PerMin = 15, HourRate = 150, MinToolDia = 1.0;
        public double DeepPocketRatio = 4, DeepHoleRatio = 10;
        // 尼龙粉末烧结 / 金属打印
        public Printer SlsMachine = Printer.Sls[0], SlmMachine = Printer.Slm[0];
        public PowderMaterial SlsMaterial = PowderMaterial.Sls[0], SlmMaterial = PowderMaterial.Slm[0];
        // 钣金
        public SheetMaterial SheetMaterial = SheetMaterial.All[0];
        public double BendPrice = 1.5;           // 元 / 刀
        public double SheetCutPrice = double.NaN;// 激光切割 元/m，NaN = 按材料和板厚
        public int Quantity = 1;                 // 钣金、板材切割的数量（摊编程和调机）
        // 板材切割
        public CutMethod CutMethod = CutMethod.Router;
        public PlateMaterial PlateMaterial = PlateMaterial.All[0];
        public double CutterDia = 2.0;           // 雕刻机刀具直径
        public double CutHourRate = double.NaN;  // NaN = 雕刻机 100、水刀 240 元/小时
        // 注塑
        public MoldMaterial MoldMaterial = MoldMaterial.All[0];
        public double MinDraft = 1.0;            // 最小拔模角（度）
        public int MoldQuantity = 1000;

        public double MinWall
        {
            get
            {
                switch (Process)
                {
                    case DfmProcess.Cnc: return double.IsNaN(MinWallCnc) ? CncMaterial.MinWall : MinWallCnc;
                    case DfmProcess.Sls: return double.IsNaN(MinWallPrint) ? Math.Max(SlsMaterial.MinWall, SlsMachine.Mjf ? 0.7 : 0.8) : MinWallPrint;
                    case DfmProcess.Slm: return double.IsNaN(MinWallPrint) ? SlmMaterial.MinWall : MinWallPrint;
                    case DfmProcess.Mold: return double.IsNaN(MinWallPrint) ? MoldMaterial.MinWall : MinWallPrint;
                    case DfmProcess.Sla: return double.IsNaN(MinWallPrint) ? 0.5 : MinWallPrint;
                    default: return double.IsNaN(MinWallPrint) ? 0.8 : MinWallPrint;
                }
            }
        }
        /// <summary>按名称（包含即可）设置当前工艺的材料。</summary>
        public void SetMaterial(string n)
        {
            T Pick<T>(IEnumerable<T> list, Func<T, string> name) =>
                list.FirstOrDefault(x => name(x).IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) ?? throw new ArgumentException("没有这个材料：" + n + "（可选：" + string.Join("、", list.Select(name)) + "）");
            switch (Process)
            {
                case DfmProcess.Cnc: CncMaterial = Pick(CncMaterial.All, x => x.Name); MinWallCnc = double.NaN; break;
                case DfmProcess.Sls: SlsMaterial = Pick(PowderMaterial.Sls, x => x.Name); break;
                case DfmProcess.Slm: SlmMaterial = Pick(PowderMaterial.Slm, x => x.Name); break;
                case DfmProcess.Sheet: SheetMaterial = Pick(SheetMaterial.All, x => x.Name); SheetCutPrice = double.NaN; break;
                case DfmProcess.Cut: PlateMaterial = Pick(PlateMaterial.All, x => x.Name); break;
                case DfmProcess.Mold: MoldMaterial = Pick(MoldMaterial.All, x => x.Name); break;
                default: PrintMaterial = Pick(PrintMaterial.All.Where(x => x.Resin == (Process == DfmProcess.Sla)), x => x.Name); break;
            }
        }

        /// <summary>按名称设置当前工艺的设备（3D 打印机、粉末烧结 / 金属打印设备）。</summary>
        public void SetMachine(string n)
        {
            var list = Process == DfmProcess.Sls ? Printer.Sls : Process == DfmProcess.Slm ? Printer.Slm : Printer.All;
            var p = list.FirstOrDefault(x => x.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) ?? throw new ArgumentException("没有这个设备：" + n + "（可选：" + string.Join("、", list.Select(x => x.Name)) + "）");
            if (Process == DfmProcess.Sls) SlsMachine = p; else if (Process == DfmProcess.Slm) SlmMachine = p; else Printer = p;
        }

        public double MinHoleDia => !double.IsNaN(MinHoleUser) ? MinHoleUser : Process == DfmProcess.Sls ? 1.5 : Process == DfmProcess.Slm ? 1.0 : 2.0;
        /// <summary>粉末床打印：小于这个的缝会粘在一起（mm）。</summary>
        public double MinGap => Process == DfmProcess.Slm ? 0.3 : SlsMachine.Mjf ? 0.4 : 0.5;
        public double CutRate => !double.IsNaN(CutHourRate) ? CutHourRate : CutMethod == CutMethod.Waterjet ? 240 : 100;
        /// <summary>当前工艺用的设备（画打印平台、检查尺寸）。</summary>
        public Printer Bed => Process == DfmProcess.Sls ? SlsMachine : Process == DfmProcess.Slm ? SlmMachine : Printer;
        public string MaterialName => Process == DfmProcess.Cnc ? CncMaterial.Name : Process == DfmProcess.Sls ? SlsMaterial.Name : Process == DfmProcess.Slm ? SlmMaterial.Name :
                                      Process == DfmProcess.Sheet ? SheetMaterial.Name : Process == DfmProcess.Cut ? PlateMaterial.Name : Process == DfmProcess.Mold ? MoldMaterial.Name : PrintMaterial.Name;
    }

    public sealed class DfmIssue
    {
        public string Kind, Title, Value, Suggestion;
        public Severity Severity;
        public Vec3 Location;           // mm，零件坐标
        public List<int> Tris = new List<int>();
        public double Number;           // 关键数值（排序、报告用）
        public TriFlag Flag;
    }

    public sealed class Orientation
    {
        public Vec3 Up;                 // 零件坐标里朝上（打印方向）的方向
        public double SupportArea, SupportVolume, Height, BaseArea, Score;
        public bool Fits = true;
        public string Label;
    }

    public sealed class Metric { public string Name, Value, Note; public Metric(string n, string v, string note = null) { Name = n; Value = v; Note = note; } }

    public sealed class DfmResult
    {
        public DfmProcess Process;
        public List<DfmIssue> Issues = new List<DfmIssue>();
        public TriFlag[] Flags;
        public Orientation Orientation;
        public List<Orientation> Candidates = new List<Orientation>();
        public List<Metric> Metrics = new List<Metric>();
        public List<Vec3> Setups = new List<Vec3>();      // CNC：选中的装夹方向（刀具从这一侧下来）
        public double Seconds;
        // 显示用
        public Vec3? ViewUp;                              // 3D 视图里朝上的方向（null = 按 SolidWorks 的 Y 向上）
        public Printer Bed;                               // 画出来的打印平台（null = 不画）
        public List<Vec3> Arrows = new List<Vec3>();      // 箭头：CNC 装夹方向、注塑开模方向
        public List<string> Notes = new List<string>();   // 工艺提示（后处理、注意事项）
        // 板材类（钣金、板材切割）
        public double Thickness = double.NaN;             // 识别出的板厚（mm）
        public Vec3? PlateNormal;
    }

    /// <summary>
    /// 可制造性分析（3D 打印：FDM / 光固化 / 尼龙粉末烧结 / 金属打印；CNC：3 轴和 3+2；钣金；板材切割；注塑）。
    /// 所有几何量都在表面三角网格上算：悬垂按面法向与打印方向的夹角；支撑体积按向下投射射线到平台或零件；
    /// 壁厚按沿内法向的锥形射线取中位数（形状直径函数，Shapira 2008）；CNC 可达性、注塑脱模按沿刀轴 / 开模方向的可见性（Frank 2006）。
    /// </summary>
    public sealed partial class DfmAnalyzer
    {
        public readonly DfmMesh Mesh;
        public readonly Bvh Bvh;
        double[] _thickness;    // 每个三角形的局部壁厚（mm，NaN = 测不到）
        int[] _thickHit;        // 对应的「背面」三角形
        List<bool[]> _setupVis;  // CNC：每个选中的装夹方向下，每个三角形能不能加工到

        /// <summary>某个面从第 k 个装夹方向能加工到的面积比例。</summary>
        double FaceVis(int k, DfmFace f)
        {
            if (_setupVis == null || k < 0 || k >= _setupVis.Count) return 0;
            double a = 0; foreach (var t in f.Tris) if (_setupVis[k][t]) a += Mesh.A[t];
            return a / Math.Max(f.Area, 1e-30);
        }

        public DfmAnalyzer(DfmMesh m) { Mesh = m; Bvh = new Bvh(m); }

        static string F(double v, string f = "0.##") => v.ToString(f, CultureInfo.InvariantCulture);

        public DfmResult Analyze(DfmSettings s, Vec3? up, Action<string> log, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var m = Mesh;
            var r = new DfmResult { Process = s.Process, Flags = new TriFlag[m.Tris] };
            if (_thickness == null) { log?.Invoke("正在测量壁厚"); Thickness(ct); }
            switch (s.Process)
            {
                case DfmProcess.Cnc: AnalyzeCnc(s, r, log, ct); break;
                case DfmProcess.Sls: AnalyzeSls(s, up, r, log, ct); break;
                case DfmProcess.Slm: AnalyzeSlm(s, up, r, log, ct); break;
                case DfmProcess.Sheet: AnalyzeSheet(s, r, log, ct); break;
                case DfmProcess.Cut: AnalyzeCut(s, r, log, ct); break;
                case DfmProcess.Mold: AnalyzeMold(s, up, r, log, ct); break;
                default: AnalyzePrint(s, up, r, log, ct); break;
            }
            // 钣金、板材切割的厚度和孔按板材规则单独检查
            if (s.Process != DfmProcess.Sheet && s.Process != DfmProcess.Cut)
            {
                ThinWalls(s, r);
                Holes(s, r);
            }
            if (s.Process == DfmProcess.Sla) Cavities(s, r);
            int bodies = m.Shells.Count(x => x.Closed && !x.Cavity);
            if (bodies > 1) r.Notes.Add($"模型里有 {bodies} 个互不相连的实体，按一个零件检查；如果是多个零件，建议分开检查。");
            if (!m.Closed) r.Notes.Add("网格不封闭（STL 有破洞），壁厚、体积和空腔的结果可能不准。");
            r.Issues = r.Issues.OrderBy(i => i.Severity).ThenByDescending(i => i.Tris.Sum(t => m.A[t])).ToList();
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        // ================= 3D 打印 =================

        void AnalyzePrint(DfmSettings s, Vec3? up, DfmResult r, Action<string> log, CancellationToken ct)
        {
            var m = Mesh;
            PlaceForPrint(s, up, r, log, ct);
            var o = r.Orientation;
            OverhangIssues(s, r, null);
            FitIssue(s, r);

            // 估算
            double V = m.Volume, S = m.Area;
            double shell = Math.Min(V, S * s.ShellThickness);
            var mat = s.PrintMaterial;
            r.Metrics.Add(new Metric("摆放方向", o.Label ?? Vec(o.Up)));
            r.Metrics.Add(new Metric("打印高度", F(o.Height, "0.#") + " mm"));
            r.Metrics.Add(new Metric("需要支撑的面积", F(o.SupportArea, "0") + " mm²", "悬垂面在平台上的投影面积"));
            r.Metrics.Add(new Metric("支撑体积（估算）", F(o.SupportVolume / 1000, "0.##") + " cm³", "从悬垂面向下到平台或零件的体积"));
            r.Metrics.Add(new Metric("底面接触面积", F(o.BaseArea, "0") + " mm²", "越大越稳、越不容易翘边"));
            if (s.Process == DfmProcess.Fdm)
            {
                double infillV = Math.Max(0, V - shell) * s.Infill;
                double supportV = o.SupportVolume * s.SupportDensity;
                double plastic = shell + infillV + supportV;
                double grams = plastic / 1000 * mat.Density;
                double layers = Math.Ceiling(o.Height / s.LayerHeight);
                double seconds = plastic / s.FlowMm3PerS + layers * s.SecondsPerLayer;
                r.Metrics.Add(new Metric("用料（估算）", $"{F(grams, "0.#")} g（{F(plastic / 1000, "0.##")} cm³）", $"外壳 {F(s.ShellThickness)} mm + 填充 {F(s.Infill * 100, "0")}% + 支撑 {F(s.SupportDensity * 100, "0")}%"));
                r.Metrics.Add(new Metric("材料费（估算）", $"{F(grams / 1000 * mat.PricePerKg, "0.##")} 元", $"{mat.Name} {F(mat.PricePerKg, "0")} 元/kg"));
                r.Metrics.Add(new Metric("打印时间（估算）", Time(seconds), $"用料 ÷ {F(s.FlowMm3PerS)} mm³/s + {layers:0} 层 × {F(s.SecondsPerLayer)} 秒"));
            }
            else
            {
                double resin = V + o.SupportVolume * 0.1;
                double grams = resin / 1000 * mat.Density;
                double layers = Math.Ceiling(o.Height / 0.05);
                r.Metrics.Add(new Metric("树脂用量（估算）", $"{F(resin / 1000, "0.##")} mL（{F(grams, "0.#")} g）", "零件体积 + 支撑约 10%"));
                r.Metrics.Add(new Metric("材料费（估算）", $"{F(grams / 1000 * mat.PricePerKg, "0.##")} 元", $"{mat.Name} {F(mat.PricePerKg, "0")} 元/kg"));
                r.Metrics.Add(new Metric("打印时间（估算）", Time(layers * s.ResinSecondsPerLayer), $"{layers:0} 层（0.05 mm）× {F(s.ResinSecondsPerLayer)} 秒"));
            }
            r.Metrics.Add(new Metric("零件体积", F(V / 1000, "0.##") + " cm³"));
        }

        /// <summary>选摆放方向（推荐 3 个，或者用户指定的 up），标出悬垂和贴平台的三角形。</summary>
        void PlaceForPrint(DfmSettings s, Vec3? up, DfmResult r, Action<string> log, CancellationToken ct)
        {
            log?.Invoke("正在找最佳摆放方向");
            r.Candidates = FindOrientations(s, ct);
            r.Orientation = up.HasValue ? Evaluate(up.Value.Normalized(), s, true, r.Flags) : null;
            if (r.Orientation == null)
            {
                r.Orientation = r.Candidates.Count > 0 ? Evaluate(r.Candidates[0].Up, s, true, r.Flags) : Evaluate(new Vec3(0, 0, 1), s, true, r.Flags);
                r.Orientation.Label = r.Candidates.Count > 0 ? r.Candidates[0].Label : "Z 向上";
            }
            else r.Orientation.Label = UpLabel(up.Value.Normalized());
            r.ViewUp = r.Orientation.Up; r.Bed = s.Bed;
        }

        /// <summary>悬垂问题：按连通区域分组。skip：不算的三角形（例如金属打印里能自支撑的小孔）。</summary>
        void OverhangIssues(DfmSettings s, DfmResult r, Func<int, bool> skip)
        {
            var m = Mesh; var d = r.Orientation.Up;
            bool metal = s.Process == DfmProcess.Slm;
            foreach (var region in Regions(t => (r.Flags[t] & TriFlag.Overhang) != 0 && (skip == null || !skip(t))))
            {
                double area = region.Sum(t => m.A[t]);
                if (area < Math.Max(1, 1e-4 * m.Area)) continue;
                double minBeta = region.Min(t => Math.Acos(Math.Max(-1, Math.Min(1, -m.N[t].Dot(d)))) * 180 / Math.PI);   // 与水平面夹角
                var loc = Centroid(region);
                bool ceiling = minBeta < 5;
                double target = 90 - s.OverhangDeg;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "悬垂", Flag = TriFlag.Overhang, Severity = Severity.Warning, Tris = region, Location = loc, Number = area,
                    Title = ceiling ? "朝下的水平面（需要支撑）" : $"悬垂面：与水平面成 {F(minBeta, "0")}°",
                    Value = $"面积 {F(area, "0.#")} mm²，最小 {F(minBeta, "0")}°（自支撑要 ≥ {F(target, "0")}°）",
                    Suggestion = metal
                        ? (ceiling
                            ? "金属打印朝下的平面必须加支撑（还要靠支撑导热、防翘曲），支撑要用线切割 / 钳工去掉，留下的表面很粗糙（Ra 15–25 µm）。改成 ≥ 45° 的斜面或屋顶形，或者换摆放方向；必须保留就在这个面留 0.3–0.5 mm 余量，打印后再 CNC。"
                            : $"低于 {F(target, "0")}° 的面要加支撑，去支撑费工、表面粗糙。把倾角改到 ≥ {F(target, "0")}°，或者换下面推荐的摆放方向。")
                        : ceiling
                        ? "改成「屋顶」形状（例如 45° 倒角或菱形 / 泪滴形孔），或换个摆放方向让它朝上；实在避免不了就加支撑，打印后打磨支撑面。"
                        : $"把这个面的倾角改到 ≥ {F(target, "0")}°（例如 {F(minBeta, "0")}° 倒角改成 45° 倒角），或者加支撑；也可以换下面推荐的摆放方向。",
                });
            }
        }

        /// <summary>摆放后是否超出设备的打印范围。</summary>
        void FitIssue(DfmSettings s, DfmResult r)
        {
            var m = Mesh; var o = r.Orientation; var bedP = s.Bed;
            var ext = Extents(o.Up);
            var bed = new[] { bedP.X, bedP.Y }.OrderBy(x => x).ToArray();
            var xy = new[] { ext.w1, ext.w2 }.OrderBy(x => x).ToArray();
            o.Fits = xy[0] <= bed[0] && xy[1] <= bed[1] && ext.h <= bedP.Z;
            if (!o.Fits)
                r.Issues.Add(new DfmIssue
                {
                    Kind = "尺寸", Severity = Severity.Error, Location = (m.Min + m.Max) / 2, Number = ext.h,
                    Title = $"超出 {bedP.Name} 的打印范围",
                    Value = $"摆放后 {F(ext.w1, "0.#")} × {F(ext.w2, "0.#")} × {F(ext.h, "0.#")} mm，打印范围 {F(bedP.X, "0")} × {F(bedP.Y, "0")} × {F(bedP.Z, "0")} mm",
                    Suggestion = s.Process == DfmProcess.Slm
                        ? "换一个摆放方向（斜放可以多出一些空间）、换成型尺寸更大的设备，或者把零件拆成几块打印后焊接 / 螺钉连接。"
                        : s.Process == DfmProcess.Sls
                        ? "换一个摆放方向（粉末烧结斜放也不用加支撑）、换成型仓更大的设备，或者把零件拆成几块，用榫卯 / 螺钉 / 胶水拼接。"
                        : "换一个摆放方向（斜放可以多出一些空间）、换大一点的打印机，或者把零件拆成几块，用榫卯 / 螺钉 / 胶水拼接。",
                });
        }

        static string Time(double s) => s >= 3600 ? $"{(int)(s / 3600)} 小时 {(int)(s % 3600 / 60)} 分" : $"{Math.Max(1, (int)Math.Round(s / 60))} 分钟";
        static string Vec(Vec3 v) => $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";
        static string UpLabel(Vec3 d)
        {
            if (Math.Abs(d.X) > 0.999) return (d.X > 0 ? "+X" : "−X") + " 朝上";
            if (Math.Abs(d.Y) > 0.999) return (d.Y > 0 ? "+Y" : "−Y") + " 朝上";
            if (Math.Abs(d.Z) > 0.999) return (d.Z > 0 ? "+Z" : "−Z") + " 朝上";
            return "斜放 " + Vec(d);
        }

        (double w1, double w2, double h) Extents(Vec3 d)
        {
            d.Perpendiculars(out var e1, out var e2);
            // 绕竖直轴转一转，找占地最小的朝向
            double best = double.MaxValue, bw1 = 0, bw2 = 0;
            for (int k = 0; k < 18; k++)
            {
                double a = k * Math.PI / 36;
                var u = e1 * Math.Cos(a) + e2 * Math.Sin(a); var v = d.Cross(u);
                double w1 = Mesh.Extent(u, out _, out _), w2 = Mesh.Extent(v, out _, out _);
                if (w1 * w2 < best) { best = w1 * w2; bw1 = w1; bw2 = w2; }
            }
            return (bw1, bw2, Mesh.Extent(d, out _, out _));
        }

        /// <summary>
        /// 某个摆放方向（d = 朝上）下的悬垂、支撑和底面。flags 不为 null 时标记悬垂三角形和贴在平台上的三角形。
        /// accurate：按面积细分采样投射射线（最终结果）；否则每个三角形一条射线（方向搜索）。
        /// </summary>
        public Orientation Evaluate(Vec3 d, DfmSettings s, bool accurate, TriFlag[] flags)
        {
            var m = Mesh;
            m.Extent(d, out var hMin, out var hMax);
            double sinT = Math.Sin((s.OverhangDeg + 0.5) * Math.PI / 180);   // 留 0.5° 容差：正好 45° 的面不算
            double area = 0, vol = 0, baseA = 0;
            var down = -d;
            double cell = Math.Max(m.Diag / 150, 0.2);
            object lk = new object();
            Parallel.For(0, m.Tris, () => (0.0, 0.0, 0.0), (t, st, acc) =>
            {
                double c = m.N[t].Dot(d);
                if (c >= -sinT + 1e-9) return acc;
                // 贴在平台上的面
                int a = m.T[3 * t], b = m.T[3 * t + 1], e = m.T[3 * t + 2];
                double ha = m.P(a).Dot(d), hb = m.P(b).Dot(d), he = m.P(e).Dot(d);
                if (Math.Max(ha, Math.Max(hb, he)) - hMin < 0.05 && c < -0.9998)
                {
                    if (flags != null) flags[t] |= TriFlag.OnPlate;
                    return (acc.Item1, acc.Item2, acc.Item3 + m.A[t]);
                }
                if (flags != null) flags[t] |= TriFlag.Overhang;
                double proj = m.A[t] * -c;
                // 支撑高度：从面向下投射
                double h;
                if (accurate && m.A[t] > cell * cell)
                {
                    int k = Math.Min(64, (int)Math.Ceiling(m.A[t] / (cell * cell)));
                    double sum = 0;
                    var pa = m.P(a); var pb = m.P(b); var pe = m.P(e);
                    for (int i = 0; i < k; i++)
                    {
                        // 三角形内均匀分布的点（低差异序列）
                        double r1 = Frac((i + 0.5) * 0.7548776662), r2 = Frac((i + 0.5) * 0.5698402910);
                        if (r1 + r2 > 1) { r1 = 1 - r1; r2 = 1 - r2; }
                        var p = pa + (pb - pa) * r1 + (pe - pa) * r2;
                        sum += Drop(p, t, down, d, hMin);
                    }
                    h = sum / k;
                }
                else h = Drop(m.C[t], t, down, d, hMin);
                return (acc.Item1 + proj, acc.Item2 + proj * h, acc.Item3);
            }, acc => { lock (lk) { area += acc.Item1; vol += acc.Item2; baseA += acc.Item3; } });
            return new Orientation { Up = d, SupportArea = area, SupportVolume = vol, Height = hMax - hMin, BaseArea = baseA };
        }

        static double Frac(double x) => x - Math.Floor(x);

        double Drop(Vec3 p, int t, Vec3 down, Vec3 d, double hMin)
        {
            double toPlate = p.Dot(d) - hMin;
            double hit = Bvh.Cast(p + down * 1e-4, down, t, out _, toPlate);
            return Math.Max(0, Math.Min(toPlate, hit));
        }

        /// <summary>摆放方向搜索：坐标轴、最大几个平面朝下、球面上均匀的 200 个方向；按支撑体积、高度、底面稳定性打分，取前 3 个（彼此相差 20° 以上）。</summary>
        public List<Orientation> FindOrientations(DfmSettings s, CancellationToken ct)
        {
            var m = Mesh;
            var dirs = new List<(Vec3 d, string label)>
            {
                (new Vec3(0, 0, 1), "+Z 朝上"), (new Vec3(0, 0, -1), "−Z 朝上"), (new Vec3(0, 1, 0), "+Y 朝上"),
                (new Vec3(0, -1, 0), "−Y 朝上"), (new Vec3(1, 0, 0), "+X 朝上"), (new Vec3(-1, 0, 0), "−X 朝上"),
            };
            foreach (var f in m.Faces.Where(f => f.Kind == FaceKind.Plane).OrderByDescending(f => f.Area).Take(12))
                dirs.Add((-f.Normal, $"平面 {f.Id} 朝下"));
            int n = 200;
            double ga = Math.PI * (3 - Math.Sqrt(5));
            for (int i = 0; i < n; i++)
            {
                double z = 1 - 2 * (i + 0.5) / n, rr = Math.Sqrt(1 - z * z), th = ga * i;
                dirs.Add((new Vec3(rr * Math.Cos(th), rr * Math.Sin(th), z), null));
            }
            var res = new Orientation[dirs.Count];
            double V = Math.Max(m.Volume, 1e-9), D = Math.Max(m.Diag, 1e-9), Aref = 0.05 * D * D;
            for (int i = 0; i < dirs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var o = Evaluate(dirs[i].d, s, false, null);
                o.Fits = FitsPrinter(dirs[i].d, s, true);
                o.Score = o.SupportVolume / V + 0.25 * o.Height / D + 0.5 * Math.Max(0, 1 - o.BaseArea / Aref) + (o.Fits ? 0 : 10);
                o.Label = dirs[i].label ?? $"斜放 {Vec(dirs[i].d)}";
                res[i] = o;
            }
            var picked = new List<Orientation>();
            double best = res.Min(x => x.Score);
            foreach (var o in res.OrderBy(x => x.Score))
            {
                if (picked.Any(p => p.Up.Dot(o.Up) > Math.Cos(20 * Math.PI / 180))) continue;
                // 底面几乎没有贴平台的斜放方向，只有比最好的方向明显好时才推荐（实际很难放稳、要加很多底座支撑）
                if (o.BaseArea < 0.01 * Aref && o.Score > best * 0.8 && picked.Count > 0) continue;
                picked.Add(o);
                if (picked.Count == 3) break;
            }
            return picked;
        }

        // ================= 壁厚 =================

        void Thickness(CancellationToken ct)
        {
            var m = Mesh;
            var th = new double[m.Tris]; var hitOf = new int[m.Tris];
            // 沿内法向的锥形射线（轴线 + 4 条偏 15°），只算碰到「背面」（法向大致相反）的，取中位数
            double ang = 15 * Math.PI / 180;
            Parallel.For(0, m.Tris, t =>
            {
                if ((t & 4095) == 0 && ct.IsCancellationRequested) return;
                var n = m.N[t];
                n.Perpendiculars(out var e1, out var e2);
                var dirs = new[] { -n, (-n * Math.Cos(ang) + e1 * Math.Sin(ang)), (-n * Math.Cos(ang) - e1 * Math.Sin(ang)), (-n * Math.Cos(ang) + e2 * Math.Sin(ang)), (-n * Math.Cos(ang) - e2 * Math.Sin(ang)) };
                var hits = new List<(double d, int t)>(5);
                var o = m.C[t] - n * 1e-5;
                foreach (var d in dirs)
                {
                    var dist = Bvh.Cast(o, d, t, out int ht);
                    if (ht < 0 || m.N[ht].Dot(n) > -0.5) continue;
                    hits.Add((dist * Math.Abs(d.Dot(n)), ht));   // 换算成沿法向的厚度
                }
                if (hits.Count < 2) { th[t] = double.NaN; hitOf[t] = -1; return; }
                hits.Sort((a, b) => a.d.CompareTo(b.d));
                th[t] = hits[hits.Count / 2].d; hitOf[t] = hits[hits.Count / 2].t;
            });
            ct.ThrowIfCancellationRequested();
            _thickness = th; _thickHit = hitOf;
        }

        HashSet<(int, int)> _faceAdj;
        bool FacesAdjacent(int a, int b)
        {
            if (_faceAdj == null)
            {
                var m = Mesh; _faceAdj = new HashSet<(int, int)>();
                for (int t = 0; t < m.Tris; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        int o = m.Adj[3 * t + k];
                        if (o >= 0 && m.FaceOf[o] != m.FaceOf[t]) _faceAdj.Add((m.FaceOf[t], m.FaceOf[o]));
                    }
            }
            return _faceAdj.Contains((a, b));
        }
        /// <summary>
        /// 薄壁：局部壁厚小于最小壁厚。按「本面 + 背面」合并成一条问题。
        /// 如果背面是和本面相邻的面（两个面在一条锐角棱边上相交），那是棱边附近的楔形尖角，单独列为「锐边」提示。
        /// </summary>
        void ThinWalls(DfmSettings s, DfmResult r)
        {
            var m = Mesh;
            double lim = s.MinWall;
            var groups = new Dictionary<(int, int), List<int>>();
            for (int t = 0; t < m.Tris; t++)
            {
                if (double.IsNaN(_thickness[t]) || _thickness[t] >= lim - 0.01) continue;   // 0.01 mm 容差：正好等于要求的不算
                int fa = m.FaceOf[t], fb = m.FaceOf[_thickHit[t]];
                var key = fa < fb ? (fa, fb) : (fb, fa);
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<int>();
                l.Add(t);
            }
            foreach (var kv in groups)
            {
                var tris = kv.Value;
                double area = tris.Sum(t => m.A[t]);
                bool wedge = kv.Key.Item1 != kv.Key.Item2 && FacesAdjacent(kv.Key.Item1, kv.Key.Item2);
                if (area < (wedge ? Math.Max(1, 1e-4 * m.Area) : Math.Max(0.3, 1e-5 * m.Area))) continue;
                double min = tris.Min(t => _thickness[t]);
                if (wedge)
                {
                    var fa = m.FaceById[kv.Key.Item1]; var fb = m.FaceById[kv.Key.Item2];
                    double dih = fa.Kind == FaceKind.Plane && fb.Kind == FaceKind.Plane ? 180 - Math.Acos(Math.Max(-1, Math.Min(1, fa.Normal.Dot(fb.Normal)))) * 180 / Math.PI : double.NaN;
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "锐边", Flag = TriFlag.Thin, Severity = Severity.Info, Tris = tris, Location = Centroid(tris), Number = dih,
                        Title = double.IsNaN(dih) ? "锐角棱边（棱边附近很薄）" : $"锐角棱边：两面夹角 {F(dih, "0")}°",
                        Value = $"离棱边 {F(lim / Math.Max(0.1, Math.Tan((double.IsNaN(dih) ? 30 : dih) * Math.PI / 180)), "0.#")} mm 以内的地方薄于 {F(lim)} mm",
                        Suggestion = s.Process == DfmProcess.Cnc
                            ? "锐边容易磕碰、加工后有毛刺：倒 C0.5 / R0.5 的角（图纸上也可以注「锐边倒钝」）。"
                            : s.Process == DfmProcess.Mold
                            ? "模具上对应的是又窄又深的尖槽，难加工、容易磨损，塑料也不容易充进去：倒 R0.3–0.5 的圆角。"
                            : "打印出来棱边会变圆、强度很低：如果是受力边，倒个 0.5–1 mm 的圆角 / 倒角；只是外观可以不改。",
                    });
                    continue;
                }
                foreach (var t in tris) r.Flags[t] |= TriFlag.Thin;
                foreach (var t in tris) { int h = _thickHit[t]; if (h >= 0) r.Flags[h] |= TriFlag.Thin; }
                string why = s.Process == DfmProcess.Fdm ? "FDM 一般至少 2 条挤出线（0.4 mm 喷嘴约 0.8 mm）"
                           : s.Process == DfmProcess.Sla ? "光固化一般至少 0.5 mm，否则容易断、变形"
                           : s.Process == DfmProcess.Sls ? $"粉末烧结的薄壁强度低、清粉时容易断（{(s.SlsMachine.Mjf ? "MJF" : "SLS")} 一般 ≥ {F(lim)} mm，受力的地方 ≥ 1.5–2 mm）"
                           : s.Process == DfmProcess.Slm ? $"金属打印的薄壁容易变形、刮刀一碰就倒（{s.SlmMaterial.Name}一般 ≥ {F(lim)} mm）"
                           : s.Process == DfmProcess.Mold ? $"{s.MoldMaterial.Name} 薄于 {F(lim)} mm 很难充满（缺料、熔接痕明显），推荐壁厚 {F(s.MoldMaterial.MinWall)}–{F(s.MoldMaterial.MaxWall)} mm"
                           : $"{s.CncMaterial.Name}薄壁加工时会振刀、变形";
                double target = Math.Max(lim * 1.5, Math.Ceiling(lim * 10 * 1.2) / 10);
                r.Issues.Add(new DfmIssue
                {
                    Kind = "薄壁", Flag = TriFlag.Thin, Severity = min < lim * 0.6 ? Severity.Error : Severity.Warning, Tris = tris, Location = Centroid(tris), Number = min,
                    Title = $"薄壁：最薄 {F(min)} mm",
                    Value = $"最薄 {F(min)} mm（要求 ≥ {F(lim)} mm），面积 {F(area, "0.#")} mm²",
                    Suggestion = $"壁厚加到 {F(target, "0.#")} mm 以上；{why}。不方便加厚的话加筋或倒圆角补强。",
                });
            }
        }
        // ================= 孔 =================

        void Holes(DfmSettings s, DfmResult r)
        {
            var m = Mesh;
            foreach (var f in CylGroups().Where(g => g.Coverage > 5.8))
            {
                double dia = 2 * f.Radius, depth = f.Length;
                foreach (var t in f.Tris) r.Flags[t] |= TriFlag.Hole;
                if (s.Process == DfmProcess.Cnc)
                {
                    if (depth / dia > s.DeepHoleRatio)
                        r.Issues.Add(new DfmIssue
                        {
                            Kind = "深孔", Flag = TriFlag.Hole, Severity = Severity.Warning, Tris = f.Tris.ToList(), Location = f.Centroid, Number = depth / dia,
                            Title = $"深孔：Φ{F(dia)} × {F(depth, "0.#")} mm（深径比 {F(depth / dia, "0.#")}）",
                            Value = $"深径比 {F(depth / dia, "0.#")} > {F(s.DeepHoleRatio, "0")}",
                            Suggestion = $"普通麻花钻一般到 {F(s.DeepHoleRatio, "0")} 倍直径；改短孔深、加大孔径，或从两头各钻一半；必须这么深就要用枪钻（贵、交期长）。",
                        });
                    if (dia < 0.99)
                        r.Issues.Add(new DfmIssue
                        {
                            Kind = "小孔", Flag = TriFlag.Hole, Severity = Severity.Warning, Tris = f.Tris.ToList(), Location = f.Centroid, Number = dia,
                            Title = $"小孔 Φ{F(dia)} mm", Value = $"直径 {F(dia)} mm < 1 mm",
                            Suggestion = "Φ1 以下的钻头很容易断，改大到 Φ1 以上，或者改成电火花 / 激光加工。",
                        });
                }
                else if (s.Process == DfmProcess.Mold)
                {
                    // 注塑：孔由型芯成形，细长的型芯会被料流冲弯、折断
                    if (dia < 0.99 || dia < 5 && depth / dia > 4)
                        r.Issues.Add(new DfmIssue
                        {
                            Kind = "细长型芯", Flag = TriFlag.Hole, Severity = Severity.Warning, Tris = f.Tris.ToList(), Location = f.Centroid, Number = depth / dia,
                            Title = $"细长孔 Φ{F(dia)} × {F(depth, "0.#")} mm", Value = $"深径比 {F(depth / dia, "0.#")}（小孔一般 ≤ 3–4）",
                            Suggestion = "孔由模具里的型芯成形，又细又长的型芯会被料流冲弯、折断，孔也会偏。加大孔径或减小深度；通孔可以做成两侧型芯对插（各一半深），或者成型后再钻孔。",
                        });
                }
                else if (dia < s.MinHoleDia - 0.01)
                {
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "小孔", Flag = TriFlag.Hole, Severity = Severity.Warning, Tris = f.Tris.ToList(), Location = f.Centroid, Number = dia,
                        Title = $"小孔 Φ{F(dia)} mm", Value = $"直径 {F(dia)} mm < {F(s.MinHoleDia)} mm",
                        Suggestion = s.Process == DfmProcess.Fdm
                            ? "FDM 打的小孔通常偏小甚至被糊住：打印后用钻头扩孔，或者在模型里放大 0.2–0.3 mm；需要精确配合的孔建议打印后铰孔。"
                            : s.Process == DfmProcess.Sls
                            ? $"粉末烧结的小孔会被半烧结的粉末堵住、清不出来（孔越长越难清）。孔径放大到 ≥ {F(s.MinHoleDia)} mm（长孔再大一些），或者先不打孔、打印后再钻。"
                            : s.Process == DfmProcess.Slm
                            ? $"金属打印小于 Φ{F(s.MinHoleDia)} 的孔容易被粉末烧结堵住。打印时不做或做小，打印后钻孔 / 铰孔；也可以改用电火花打孔。"
                            : "光固化小孔里的树脂不容易洗干净：放大孔径，或者打印后用细针清理并补光固化。",
                    });
                }
            }
        }

        // ================= CNC =================

        void AnalyzeCnc(DfmSettings s, DfmResult r, Action<string> log, CancellationToken ct)
        {
            var m = Mesh;
            var dirs = new List<Vec3> { new Vec3(0, 0, 1), new Vec3(0, 0, -1), new Vec3(1, 0, 0), new Vec3(-1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, -1, 0) };
            if (s.FiveAxisTilts)
                foreach (var a in new[] { 1.0, -1.0 }) foreach (var b in new[] { 1.0, -1.0 })
                {
                    dirs.Add(new Vec3(a, 0, b).Normalized()); dirs.Add(new Vec3(0, a, b).Normalized()); dirs.Add(new Vec3(a, b, 0).Normalized());
                }
            log?.Invoke("正在检查刀具可达性");
            double cell = Math.Max(m.Diag / 120, 0.2);
            var vis = new bool[dirs.Count][];
            for (int k = 0; k < dirs.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                vis[k] = Visibility(dirs[k], cell, false);
            }
            // 贪心选最少的装夹方向覆盖尽量多的面积
            var covered = new bool[m.Tris];
            double total = m.Area, cov = 0;
            var used = new List<int>();
            while (used.Count < dirs.Count)
            {
                int best = -1; double gain = 0;
                for (int k = 0; k < dirs.Count; k++)
                {
                    if (used.Contains(k)) continue;
                    double g = 0;
                    for (int t = 0; t < m.Tris; t++) if (!covered[t] && vis[k][t]) g += m.A[t];
                    if (g > gain + 1e-9) { gain = g; best = k; }
                }
                if (best < 0 || gain < Math.Max(1e-3 * total, 1.0)) break;   // 多一次装夹至少要加工 0.1% 的表面（或 1 mm²），网格碎片不值得
                used.Add(best);
                for (int t = 0; t < m.Tris; t++) if (vis[best][t] && !covered[t]) { covered[t] = true; cov += m.A[t]; }
                if (cov > 0.9995 * total) break;
            }
            r.Setups = used.Select(k => dirs[k]).ToList();
            r.Arrows = r.Setups;
            _setupVis = used.Select(k => vis[k]).ToList();
            // 倒扣：哪个方向都够不到
            foreach (var region in Regions(t => !covered[t]))
            {
                double area = region.Sum(t => m.A[t]);
                if (area < Math.Max(1.0, 2e-4 * total)) continue;
                foreach (var t in region) r.Flags[t] |= TriFlag.Undercut;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "倒扣", Flag = TriFlag.Undercut, Severity = Severity.Error, Tris = region, Location = Centroid(region), Number = area,
                    Title = s.FiveAxisTilts ? "倒扣：3+2 轴也加工不到" : "倒扣：3 轴从 6 个方向都加工不到",
                    Value = $"面积 {F(area, "0.#")} mm²",
                    Suggestion = "改成从一侧能直接看到的形状（例如把 T 型槽改成通槽、把内凹的台阶改成斜面），或者把零件拆成两块再装配；必须保留就要用 T 型铣刀 / 燕尾铣刀、线切割或 5 轴加工（成本明显增加）。",
                });
            }
            // 内角（竖直的尖内角、小半径内圆角）
            InnerCorners(s, r);
            Pockets(s, r, ct);

            // 估算
            var cm = s.CncMaterial;
            double sx = m.Max.X - m.Min.X + 2 * s.StockMargin, sy = m.Max.Y - m.Min.Y + 2 * s.StockMargin, sz = m.Max.Z - m.Min.Z + 2 * s.StockMargin;
            double stock = sx * sy * sz, removed = Math.Max(0, stock - m.Volume);
            double minutes = removed / 1000 / cm.Mrr + m.Area / 100 / s.FinishCm2PerMin + Math.Max(1, used.Count) * s.SetupMinutes;
            double stockKg = stock / 1e6 * cm.Density;
            r.Metrics.Add(new Metric("装夹次数", $"{Math.Max(1, used.Count)} 次", string.Join("、", used.Select(k => DirName(dirs[k])))));
            r.Metrics.Add(new Metric("毛坯", $"{F(sx, "0.#")} × {F(sy, "0.#")} × {F(sz, "0.#")} mm", $"包围盒每边加 {F(s.StockMargin)} mm"));
            r.Metrics.Add(new Metric("去除体积", F(removed / 1000, "0.#") + " cm³", $"占毛坯 {F(removed / stock * 100, "0")}%"));
            r.Metrics.Add(new Metric("加工时间（估算）", Time(minutes * 60), $"粗加工 {F(cm.Mrr)} cm³/min + 精加工 {F(s.FinishCm2PerMin)} cm²/min + 每次装夹 {F(s.SetupMinutes)} 分钟"));
            r.Metrics.Add(new Metric("费用（估算）", $"{F(minutes / 60 * s.HourRate + stockKg * cm.PricePerKg, "0")} 元", $"机时 {F(s.HourRate, "0")} 元/小时 + 料 {F(stockKg, "0.###")} kg × {F(cm.PricePerKg, "0")} 元/kg"));
            r.Metrics.Add(new Metric("零件体积", F(m.Volume / 1000, "0.##") + " cm³", $"重 {F(m.Volume / 1000 * cm.Density, "0.#")} g（{cm.Name}）"));
        }

        /// <summary>
        /// 每个三角形沿方向 a 能不能「看到外面」（刀具从 a 这一侧下来能加工到、注塑时这一半模具能脱出）：
        /// 面不背对 a，且从面上沿 a 看出去没有遮挡。按面积采样（大三角形多采几个点），一半以上的点看得到就算；
        /// 这样粗网格和细网格的结果一致。fast：每个三角形只看形心一个点（方向搜索用）。
        /// </summary>
        bool[] Visibility(Vec3 a, double cell, bool fast)
        {
            var m = Mesh; var v = new bool[m.Tris];
            Parallel.For(0, m.Tris, t => v[t] = VisibleTri(t, a, cell, fast));
            return v;
        }

        bool VisibleTri(int t, Vec3 a, double cell, bool fast)
        {
            var m = Mesh;
            var n = m.N[t];
            if (n.Dot(a) < -0.02) return false;
            if (fast) return double.IsInfinity(Bvh.Cast(m.C[t] + n * 0.02 + a * 1e-4, a, t, out _));
            var pa = m.P(m.T[3 * t]); var pb = m.P(m.T[3 * t + 1]); var pc = m.P(m.T[3 * t + 2]);
            int k = Math.Min(24, Math.Max(4, (int)Math.Ceiling(m.A[t] / (cell * cell))));
            int ok = 0;
            for (int i = 0; i < k; i++)
            {
                double r1 = Frac((i + 0.5) * 0.7548776662), r2 = Frac((i + 0.5) * 0.5698402910);
                if (r1 + r2 > 1) { r1 = 1 - r1; r2 = 1 - r2; }
                var p = pa + (pb - pa) * r1 + (pc - pa) * r2;
                var o = p + n * 0.02 + a * 1e-4;
                if (double.IsInfinity(Bvh.Cast(o, a, t, out _))) ok++;
                if (ok * 2 >= k) break;
            }
            return ok * 2 >= k;
        }

        static string DirName(Vec3 a)
        {
            string Ax(double v, string n) => Math.Abs(v) < 1e-6 ? "" : (v > 0 ? "+" : "−") + n;
            var s = Ax(a.X, "X") + Ax(a.Y, "Y") + Ax(a.Z, "Z");
            return s + " 方向下刀";
        }

        /// <summary>
        /// 竖直内角：两个平面在一条凹边上相交、这条边平行于某个装夹方向的刀轴、两个面都是侧壁 → 立铣刀做不出尖角。
        /// 小内圆角：凹圆柱面（不是整圈的孔），轴线平行于刀轴，半径小 → 需要很小的刀。
        /// </summary>
        void InnerCorners(DfmSettings s, DfmResult r)
        {
            var m = Mesh;
            var axes = r.Setups.Count > 0 ? r.Setups : new List<Vec3> { new Vec3(0, 0, 1) };
            var pairs = new Dictionary<(int, int), (double len, Vec3 mid, List<int> tris, Vec3 dir)>();
            for (int t = 0; t < m.Tris; t++)
                for (int k = 0; k < 3; k++)
                {
                    int o = m.Adj[3 * t + k];
                    if (o < 0 || o < t) continue;
                    int fa = m.FaceOf[t], fb = m.FaceOf[o];
                    if (fa == fb) continue;
                    var A = m.FaceById[fa]; var B = m.FaceById[fb];
                    if (A.Kind != FaceKind.Plane || B.Kind != FaceKind.Plane) continue;
                    // 凹边：对面三角形的形心在本面的前方
                    if ((m.C[o] - m.C[t]).Dot(m.N[t]) <= 1e-6) continue;
                    if (A.Normal.Dot(B.Normal) > 0.95) continue;
                    var pa = m.P(m.T[3 * t + k]); var pb = m.P(m.T[3 * t + (k + 1) % 3]);
                    var e = pb - pa; double len = e.Length; if (len < 1e-9) continue;
                    var key = fa < fb ? (fa, fb) : (fb, fa);
                    pairs.TryGetValue(key, out var acc);
                    if (acc.tris == null) acc = (0, Vec3.Zero, new List<int>(), e / len);
                    acc.len += len; acc.mid = acc.mid + (pa + pb) / 2 * len; acc.tris.Add(t); acc.tris.Add(o);
                    pairs[key] = acc;
                }
            var corners = new Dictionary<(int, long, long, long), (double len, Vec3 mid, List<int> tris)>();
            foreach (var kv in pairs)
            {
                var (len, mid, tris, dir) = kv.Value;
                var A = m.FaceById[kv.Key.Item1]; var B = m.FaceById[kv.Key.Item2];
                int ai = Enumerable.Range(0, axes.Count).FirstOrDefault(k => Math.Abs(dir.Dot(axes[k])) > 0.98 && Math.Abs(A.Normal.Dot(axes[k])) < 0.05 && Math.Abs(B.Normal.Dot(axes[k])) < 0.05 &&
                                                                       tris.Count(t => _setupVis != null && _setupVis[k][t]) * 2 >= tris.Count);
                var axis = Math.Abs(dir.Dot(axes[ai])) > 0.98 && Math.Abs(A.Normal.Dot(axes[ai])) < 0.05 && Math.Abs(B.Normal.Dot(axes[ai])) < 0.05 && tris.Count(t => _setupVis != null && _setupVis[ai][t]) * 2 >= tris.Count ? axes[ai] : Vec3.Zero;
                if (axis.Length < 0.5 || len < 0.2) continue;
                // 「底面 + 侧壁」的角：从底面朝上的方向用平底立铣刀可以直接铣出尖角（底面要从这个方向加工得到），不算问题
                if (Enumerable.Range(0, axes.Count).Any(k => A.Normal.Dot(axes[k]) > 0.99 && Math.Abs(B.Normal.Dot(axes[k])) < 0.05 && FaceVis(k, A) > 0.5 ||
                                                         B.Normal.Dot(axes[k]) > 0.99 && Math.Abs(A.Normal.Dot(axes[k])) < 0.05 && FaceVis(k, B) > 0.5)) continue;
                foreach (var t in tris) r.Flags[t] |= TriFlag.Corner;
                // 同一条角线（侧壁被分成几个面时会断成几段）合并成一条
                var c = mid / len;
                var pc = c - axis * c.Dot(axis);
                var key = (ai, (long)Math.Round(pc.X * 20), (long)Math.Round(pc.Y * 20), (long)Math.Round(pc.Z * 20));
                if (!corners.TryGetValue(key, out var cr)) corners[key] = cr = (0, Vec3.Zero, new List<int>());
                corners[key] = (cr.len + len, cr.mid + mid, cr.tris.Concat(tris).ToList());
            }
            foreach (var cr in corners.Values)
            {
                double len = cr.len;
                // 推荐圆角：刀具长径比不超过 4（加工深度 = 内角长度）
                double tool = StdTool(Math.Max(s.MinToolDia, len / 4));
                r.Issues.Add(new DfmIssue
                {
                    Kind = "尖内角", Flag = TriFlag.Corner, Severity = Severity.Warning, Tris = cr.tris.Distinct().ToList(), Location = cr.mid / len, Number = len,
                    Title = $"竖直内角是尖角（深 {F(len, "0.#")} mm）",
                    Value = $"两个侧壁相交成尖内角，沿刀轴深 {F(len, "0.#")} mm",
                    Suggestion = $"立铣刀是圆的，做不出尖内角，至少会留下刀具半径的圆角。在模型里加 R{F(tool / 2 + 0.5, "0.#")} 以上的圆角（可以用 Φ{F(tool, "0.#")} 刀，比圆角略小走刀更顺）；必须是尖角就要清角（电火花）或在角上开「避空孔 / 狗骨头」。",
                });
            }
            foreach (var f in CylGroups().Where(g => g.Coverage <= 5.8))
            {
                var axis = axes.FirstOrDefault(a => Math.Abs(f.Axis.Dot(a)) > 0.98);
                if (axis.Length < 0.5) continue;
                double maxTool = 2 * f.Radius;
                double depth = f.Length;
                bool small = f.Radius < 1.5 || depth / maxTool > 4;
                if (!small) continue;
                foreach (var t in f.Tris) r.Flags[t] |= TriFlag.Corner;
                double rec = StdTool(Math.Max(depth / 4, 3)) / 2 + 0.5;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "小内圆角", Flag = TriFlag.Corner, Severity = f.Radius < 0.5 ? Severity.Error : Severity.Warning, Tris = f.Tris.ToList(), Location = f.Centroid, Number = f.Radius,
                    Title = $"内圆角 R{F(f.Radius)}（深 {F(depth, "0.#")} mm）",
                    Value = $"只能用 Φ{F(maxTool)} 以下的刀，长径比 {F(depth / maxTool, "0.#")}",
                    Suggestion = $"小刀又长又细，加工慢、容易断刀、精度差。圆角加大到 R{F(rec, "0.#")} 以上（可以用 Φ{F((rec - 0.5) * 2, "0.#")} 刀），或者减小这里的深度。",
                });
            }
        }

        static double StdTool(double d)
        {
            foreach (var t in new[] { 1.0, 1.5, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20 }) if (t >= d - 1e-9) return t;
            return 20;
        }

        /// <summary>深腔、窄槽：在朝刀具的平面「底面」上，水平方向测宽度、往上测深度。</summary>
        void Pockets(DfmSettings s, DfmResult r, CancellationToken ct)
        {
            var m = Mesh;
            for (int si = 0; si < r.Setups.Count; si++)
            {
                var a = r.Setups[si];
                m.Extent(a, out _, out var top);
                a.Perpendiculars(out var e1, out var e2);
                foreach (var f in m.Faces.Where(f => f.Kind == FaceKind.Plane && f.Normal.Dot(a) > 0.999 && FaceVis(si, f) > 0.5))
                {
                    ct.ThrowIfCancellationRequested();
                    int t0 = f.Tris.OrderByDescending(t => m.A[t]).First();
                    var p = m.C[t0];
                    double h = p.Dot(a);
                    if (top - h < 1) continue;   // 顶面
                    var o = p + a * Math.Min(0.3, (top - h) / 10);
                    double width = double.MaxValue, depthSum = 0; int depthCnt = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        double ang = k * Math.PI / 4;
                        var dir = e1 * Math.Cos(ang) + e2 * Math.Sin(ang);
                        double d1 = Bvh.Cast(o, dir, -1, out int h1), d2 = Bvh.Cast(o, -dir, -1, out int h2);
                        if (h1 < 0 || h2 < 0) continue;
                        width = Math.Min(width, d1 + d2);
                        foreach (var ht in new[] { h1, h2 })
                        {
                            var face = m.FaceById[m.FaceOf[ht]];
                            double fTop = face.Tris.SelectMany(t => new[] { m.T[3 * t], m.T[3 * t + 1], m.T[3 * t + 2] }).Max(v => m.P(v).Dot(a));
                            depthSum = Math.Max(depthSum, fTop - h); depthCnt++;   // 取最高的侧壁（侧壁可能被分成几个面）
                        }
                    }
                    if (width == double.MaxValue || depthCnt == 0) continue;
                    double depth = depthSum;
                    if (depth < 0.5) continue;
                    double ratio = depth / width;
                    bool narrow = width < s.MinToolDia;
                    if (ratio <= s.DeepPocketRatio && !narrow) continue;
                    double tool = StdTool(Math.Min(width * 0.8, depth / 4));
                    bool sliver = width < 0.3;
                    if (sliver)
                    {
                        r.Issues.Add(new DfmIssue
                        {
                            Kind = "细缝", Flag = TriFlag.Corner, Severity = Severity.Warning, Tris = f.Tris.ToList(), Location = p, Number = width,
                            Title = $"只有 {F(width)} mm 宽的缝（深 {F(depth, "0.#")} mm）",
                            Value = "比任何刀具都窄，多半是建模时两个特征没对齐留下的缝",
                            Suggestion = "先确认这条缝是不是设计需要：不需要就在 SolidWorks 里把两个面对齐 / 合并（例如把拉伸改成「成形到一面」）；需要的话改成 ≥ 1 mm 的槽，或用线切割加工。",
                        });
                        continue;
                    }
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = narrow ? "窄槽" : "深腔", Flag = TriFlag.Corner, Severity = narrow ? Severity.Error : Severity.Warning, Tris = f.Tris.ToList(), Location = p, Number = ratio,
                        Title = narrow ? $"窄槽：宽 {F(width)} mm" : $"深腔 / 深槽：深 {F(depth, "0.#")} mm、宽 {F(width, "0.#")} mm",
                        Value = $"深宽比 {F(ratio, "0.#")}（建议 ≤ {F(s.DeepPocketRatio, "0")}）",
                        Suggestion = narrow
                            ? $"比最小刀具 Φ{F(s.MinToolDia)} 还窄，铣刀进不去：加宽到 {F(Math.Max(s.MinToolDia * 1.5, 2), "0.#")} mm 以上，或者改成线切割 / 电火花。"
                            : $"刀具需要伸出很长（Φ{F(Math.Max(width * 0.8, 1), "0.#")} 左右的刀要伸出 {F(depth, "0.#")} mm），容易振刀、断刀、侧壁不直。减小深度、加宽槽，或者把深腔改成从两面加工 / 拆件。",
                    });
                }
            }
        }

        // ================= 工具 =================

        /// <summary>同轴、同半径的凹圆柱面合成一组（SolidWorks 导出的孔常常是两个半圆柱面）。</summary>
        public sealed class CylGroup
        {
            public List<DfmFace> Faces = new List<DfmFace>();
            public double Radius, Coverage, T0, T1;
            public Vec3 Axis, AxisPoint, Centroid;
            public List<int> Tris = new List<int>();
            public double Length => T1 - T0;
        }

        List<CylGroup> _groups;
        public List<CylGroup> CylGroups() => _groups ?? (_groups = GroupCylinders(Mesh.Faces.Where(f => f.Kind == FaceKind.Cylinder && f.Concave)));

        /// <summary>把同轴、同半径、沿轴向重叠的圆柱面合成一组。</summary>
        List<CylGroup> GroupCylinders(IEnumerable<DfmFace> faces)
        {
            var m = Mesh;
            var gs = new List<CylGroup>();
            foreach (var f in faces)
            {
                var g = gs.FirstOrDefault(x => Math.Abs(x.Axis.Dot(f.Axis)) > 0.999 && Math.Abs(x.Radius - f.Radius) < 0.01 * f.Radius + 0.005 &&
                                               Perp(f.AxisPoint - x.AxisPoint, x.Axis) < 0.02 * f.Radius + 0.01 &&
                                               Overlap(x, f));
                if (g == null) { g = new CylGroup { Radius = f.Radius, Axis = f.Axis, AxisPoint = f.AxisPoint, T0 = double.MaxValue, T1 = double.MinValue }; gs.Add(g); }
                g.Faces.Add(f);
                g.Coverage = Math.Min(2 * Math.PI, g.Coverage + f.Coverage);
                double s0 = f.T0, s1 = f.T1;
                if (f.Axis.Dot(g.Axis) < 0) { s0 = -f.T1; s1 = -f.T0; }
                g.T0 = Math.Min(g.T0, s0); g.T1 = Math.Max(g.T1, s1);
                g.Tris.AddRange(f.Tris);
            }
            foreach (var g in gs)
            {
                double a = 0; var c = Vec3.Zero;
                foreach (var t in g.Tris) { c = c + m.C[t] * m.A[t]; a += m.A[t]; }
                g.Centroid = c / Math.Max(a, 1e-30);
            }
            return gs;
        }

        static double Perp(Vec3 v, Vec3 axis) => (v - axis * v.Dot(axis)).Length;
        static bool Overlap(CylGroup g, DfmFace f)
        {
            double s0 = f.T0, s1 = f.T1;
            if (f.Axis.Dot(g.Axis) < 0) { s0 = -f.T1; s1 = -f.T0; }
            return s0 <= g.T1 + 0.01 && s1 >= g.T0 - 0.01;
        }

        /// <summary>零件在这个摆放方向下能不能放进打印机（零件比打印范围小很多时直接跳过计算）。</summary>
        bool FitsPrinter(Vec3 d, DfmSettings s, bool fast)
        {
            var m = Mesh; var p = s.Bed;
            double minBed = Math.Min(p.X, Math.Min(p.Y, p.Z));
            if (m.Diag <= minBed) return true;
            var ext = Extents(d);
            var bed = new[] { p.X, p.Y }.OrderBy(x => x).ToArray();
            var xy = new[] { ext.w1, ext.w2 }.OrderBy(x => x).ToArray();
            return xy[0] <= bed[0] && xy[1] <= bed[1] && ext.h <= p.Z;
        }

        /// <summary>满足条件的三角形按共享边分成连通区域。</summary>
        public List<List<int>> Regions(Func<int, bool> pred)
        {
            var m = Mesh;
            var seen = new bool[m.Tris];
            var res = new List<List<int>>();
            var q = new Queue<int>();
            for (int s = 0; s < m.Tris; s++)
            {
                if (seen[s] || !pred(s)) continue;
                var reg = new List<int>();
                seen[s] = true; q.Enqueue(s);
                while (q.Count > 0)
                {
                    int t = q.Dequeue(); reg.Add(t);
                    for (int k = 0; k < 3; k++)
                    {
                        int o = m.Adj[3 * t + k];
                        if (o < 0 || seen[o] || !pred(o)) continue;
                        seen[o] = true; q.Enqueue(o);
                    }
                }
                res.Add(reg);
            }
            return res;
        }

        Vec3 Centroid(List<int> tris)
        {
            var m = Mesh; double a = 0; var c = Vec3.Zero;
            foreach (var t in tris) { c = c + m.C[t] * m.A[t]; a += m.A[t]; }
            return a > 0 ? c / a : m.C[tris[0]];
        }

        public double ThicknessAt(int t) => _thickness == null ? double.NaN : _thickness[t];
    }
}
