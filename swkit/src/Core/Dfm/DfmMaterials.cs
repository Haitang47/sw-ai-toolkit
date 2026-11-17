using System;
using System.Collections.Generic;

namespace SwKit.Core.Dfm
{
    // 新增工艺用的材料和设备表。价格都是国内常见的小批量报价的估算（2025 年前后），只用来比较方案。

    /// <summary>钣金材料：最小内折弯半径按板厚的倍数（推荐值 / 低于就容易开裂），激光切割的单价和速度随板厚变化。</summary>
    public sealed class SheetMaterial
    {
        public string Name, Note;
        public double Density, PricePerKg;          // g/cm³、元/kg
        public double RecR, MinR;                    // 推荐最小内 R、绝对最小内 R（× 板厚）
        public double MinHoleK = 1.0;                // 最小孔径（× 板厚）
        public double MaxBendT;                      // 常见折弯机能折的最大板厚（mm）
        public double LaserBase, LaserPerT;          // 激光切割单价 = LaserBase + LaserPerT × t（元/m）
        public double SpeedK, SpeedExp;              // 切割速度 = SpeedK / t^SpeedExp（m/min，3 kW 光纤激光）
        public double LaserPrice(double t) => LaserBase + LaserPerT * t;
        public double LaserSpeed(double t) => SpeedK / Math.Pow(Math.Max(t, 0.3), SpeedExp);
        static SheetMaterial M(string n, double rho, double price, double rec, double min, double maxT, double lb, double lt, double sk, double se, string note = null, double hole = 1.0) =>
            new SheetMaterial { Name = n, Density = rho, PricePerKg = price, RecR = rec, MinR = min, MaxBendT = maxT, LaserBase = lb, LaserPerT = lt, SpeedK = sk, SpeedExp = se, Note = note, MinHoleK = hole };
        public static readonly List<SheetMaterial> All = new List<SheetMaterial>
        {
            M("冷轧钢 SPCC", 7.85, 6, 1.0, 0.5, 6, 0.5, 0.6, 20, 1.1),
            M("Q235 钢板", 7.85, 5, 1.0, 0.5, 8, 0.5, 0.6, 20, 1.1, "热轧板表面有氧化皮，外观件用冷轧板"),
            M("镀锌板 SGCC", 7.85, 7, 1.0, 0.5, 4, 0.5, 0.65, 18, 1.1, "切割边没有镀层，会生锈"),
            M("304 不锈钢", 7.93, 18, 1.0, 0.5, 6, 0.8, 1.0, 15, 1.15, "不锈钢回弹大，一般 R ≥ 1–1.5t", 1.2),
            M("5052-H32 铝", 2.68, 28, 1.0, 0.5, 6, 0.8, 1.2, 12, 1.2, "5052 是最常用的折弯铝板，R ≈ 1–1.5t 即可"),
            M("6061-T6 铝", 2.70, 32, 2.5, 1.5, 6, 0.8, 1.2, 12, 1.2, "6061-T6 硬、延展性差，小 R 折弯外侧会开裂；R ≥ 2–3t，或者改用 5052-H32"),
        };
    }

    public enum CutMethod { Router, Laser, Waterjet }

    /// <summary>板材（2D 切割）材料。MaxT：各方式能切的最大厚度（mm，0 = 不适合）。</summary>
    public sealed class PlateMaterial
    {
        public string Name, Short;
        public double Density, PricePerKg;
        public bool Composite, Metal;
        public double LaserBase, LaserPerT, LaserSpeedK;   // 单价 = LaserBase + LaserPerT × t（元/m），速度 = K / t^1.1（m/min）
        public double WaterjetK;                            // 水刀速度 = K / t（m/min）
        public double RouterFeed, RouterDoc;                // 雕刻机：Φ2 刀进给（mm/min）、每层切深（× 刀径）
        public double WebK, WebAbs;                         // 最小筋宽 = max(WebK × t, WebAbs)
        public double MaxLaser, MaxWaterjet, MaxRouter;
        public string LaserKind = "光纤激光";
        public double MinWeb(double t) => Math.Max(WebK * t, WebAbs);
        public double MaxT(CutMethod m) => m == CutMethod.Laser ? MaxLaser : m == CutMethod.Waterjet ? MaxWaterjet : MaxRouter;
        public static readonly List<PlateMaterial> All = new List<PlateMaterial>
        {
            new PlateMaterial { Name = "碳纤维板 3K", Short = "碳板", Density = 1.55, PricePerKg = 260, Composite = true, WaterjetK = 4, RouterFeed = 700, RouterDoc = 0.75, WebK = 1, WebAbs = 2, MaxWaterjet = 50, MaxRouter = 10 },
            new PlateMaterial { Name = "玻纤板 FR4 / G10", Short = "玻纤板", Density = 1.85, PricePerKg = 36, Composite = true, WaterjetK = 5, RouterFeed = 900, RouterDoc = 0.75, WebK = 1, WebAbs = 1.5, MaxWaterjet = 50, MaxRouter = 12 },
            new PlateMaterial { Name = "铝板 6061", Short = "铝板", Density = 2.70, PricePerKg = 32, Metal = true, LaserBase = 0.8, LaserPerT = 1.2, LaserSpeedK = 12, WaterjetK = 3, RouterFeed = 600, RouterDoc = 0.5, WebK = 1, WebAbs = 1, MaxLaser = 12, MaxWaterjet = 100, MaxRouter = 10 },
            new PlateMaterial { Name = "铝板 5052", Short = "铝板", Density = 2.68, PricePerKg = 28, Metal = true, LaserBase = 0.8, LaserPerT = 1.2, LaserSpeedK = 12, WaterjetK = 3, RouterFeed = 600, RouterDoc = 0.5, WebK = 1, WebAbs = 1, MaxLaser = 12, MaxWaterjet = 100, MaxRouter = 10 },
            new PlateMaterial { Name = "不锈钢板 304", Short = "不锈钢板", Density = 7.93, PricePerKg = 18, Metal = true, LaserBase = 0.8, LaserPerT = 1.0, LaserSpeedK = 15, WaterjetK = 1.5, WebK = 1, WebAbs = 1, MaxLaser = 20, MaxWaterjet = 100 },
            new PlateMaterial { Name = "钢板 Q235", Short = "钢板", Density = 7.85, PricePerKg = 5.5, Metal = true, LaserBase = 0.5, LaserPerT = 0.6, LaserSpeedK = 20, WaterjetK = 1.8, WebK = 1, WebAbs = 1, MaxLaser = 25, MaxWaterjet = 100 },
            new PlateMaterial { Name = "亚克力", Short = "亚克力", Density = 1.19, PricePerKg = 30, LaserBase = 0.5, LaserPerT = 0.4, LaserSpeedK = 5, WaterjetK = 6, RouterFeed = 1500, RouterDoc = 1.0, WebK = 1, WebAbs = 1.5, MaxLaser = 20, MaxWaterjet = 50, MaxRouter = 20, LaserKind = "CO₂ 激光" },
        };
    }

    /// <summary>粉末床打印材料：尼龙（按 cm³ 计价）和金属（按 g 计价）。</summary>
    public sealed class PowderMaterial
    {
        public string Name, Note;
        public double Density, MinWall, Price;    // g/cm³、mm、元/cm³（尼龙）或 元/g（金属）
        public double BuildRate, Layer;           // 金属：成形速率 cm³/h、层厚 mm
        public static readonly List<PowderMaterial> Sls = new List<PowderMaterial>
        {
            new PowderMaterial { Name = "PA12 尼龙", Density = 1.01, MinWall = 0.8, Price = 0.9, Note = "最常用，强度、韧性均衡" },
            new PowderMaterial { Name = "PA11 尼龙", Density = 1.03, MinWall = 0.8, Price = 1.2, Note = "韧性更好、不易断，适合卡扣和铰链" },
            new PowderMaterial { Name = "PA12 + 玻璃微珠", Density = 1.30, MinWall = 1.0, Price = 1.1, Note = "更硬、尺寸更稳，但更脆" },
            new PowderMaterial { Name = "TPU 弹性体", Density = 1.20, MinWall = 1.0, Price = 1.8, Note = "软胶件、缓冲件" },
        };
        public static readonly List<PowderMaterial> Slm = new List<PowderMaterial>
        {
            new PowderMaterial { Name = "AlSi10Mg 铝合金", Density = 2.67, MinWall = 0.6, Price = 3.0, BuildRate = 20, Layer = 0.03, Note = "去应力：约 300 °C × 2 小时" },
            new PowderMaterial { Name = "316L 不锈钢", Density = 7.98, MinWall = 0.4, Price = 1.5, BuildRate = 10, Layer = 0.03, Note = "去应力：约 650 °C × 2 小时" },
            new PowderMaterial { Name = "Ti6Al4V 钛合金", Density = 4.43, MinWall = 0.4, Price = 5.0, BuildRate = 12, Layer = 0.03, Note = "真空去应力：约 800 °C × 2 小时" },
            new PowderMaterial { Name = "18Ni300 模具钢", Density = 8.00, MinWall = 0.5, Price = 2.0, BuildRate = 10, Layer = 0.04, Note = "时效：约 490 °C × 6 小时（硬度到 50 HRC 以上）" },
        };
    }

    /// <summary>注塑材料：推荐壁厚范围、流长比、收缩率、锁模压力（t/cm²）、冷却时间系数（秒 / mm²）。</summary>
    public sealed class MoldMaterial
    {
        public string Name;
        public double Density, PricePerKg, MinWall, MaxWall, FlowRatio, Shrink, ClampP, CoolK;
        static MoldMaterial M(string n, double rho, double price, double min, double max, double flow, double shrink, double clamp, double cool) =>
            new MoldMaterial { Name = n, Density = rho, PricePerKg = price, MinWall = min, MaxWall = max, FlowRatio = flow, Shrink = shrink, ClampP = clamp, CoolK = cool };
        public static readonly List<MoldMaterial> All = new List<MoldMaterial>
        {
            M("ABS", 1.05, 13, 1.0, 3.5, 150, 0.5, 0.35, 2.2),
            M("PP", 0.91, 9, 0.8, 3.8, 250, 1.5, 0.30, 3.0),
            M("PC", 1.20, 18, 1.0, 4.0, 100, 0.6, 0.50, 2.0),
            M("PC/ABS", 1.14, 17, 1.0, 3.5, 120, 0.5, 0.40, 2.2),
            M("POM", 1.41, 15, 0.8, 3.0, 150, 2.0, 0.45, 2.6),
            M("PA66 + 30% 玻纤", 1.36, 22, 0.8, 3.0, 150, 0.4, 0.50, 2.4),
        };
    }
}
