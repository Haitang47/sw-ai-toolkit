using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SwKit.Core.Report;
using SwKit.Core.Urdf;

namespace SwKit.Core.Dfm
{
    /// <summary>把可制造性分析结果整理成报告（ReportDoc）：零件信息、方法、指标、问题和修改建议、图、参考文献。</summary>
    public static class DfmReport
    {
        // 参考文献（GB/T 7714）
        const string RefDfam = "Thompson M K, Moroni G, Vaneker T, et al. Design for Additive Manufacturing: Trends, opportunities, considerations, and constraints[J]. CIRP Annals, 2016, 65(2): 737-760.";
        const string RefAm = "Gibson I, Rosen D, Stucker B, et al. Additive Manufacturing Technologies[M]. 3rd ed. Cham: Springer, 2021.";
        const string RefSupport = "Strano G, Hao L, Everson R M, et al. A new approach to the design and optimisation of support structures in additive manufacturing[J]. The International Journal of Advanced Manufacturing Technology, 2013, 66(9-12): 1247-1254.";
        const string RefOrient = "Ezair B, Massarwi F, Elber G. Orientation analysis of 3D objects toward minimal support volume in 3D-printing[J]. Computers & Graphics, 2015, 51: 117-124.";
        const string RefSdf = "Shapira L, Shamir A, Cohen-Or D. Consistent mesh partitioning and skeletonisation using the shape diameter function[J]. The Visual Computer, 2008, 24(4): 249-259.";
        const string RefVisibility = "Frank M C, Wysk R A, Joshi S B. Determining setup orientations from the visibility of slice geometry for rapid computer numerically controlled machining[J]. Journal of Manufacturing Science and Engineering, 2006, 128(1): 228-238.";
        const string RefDfma = "Boothroyd G, Dewhurst P, Knight W A. Product Design for Manufacture and Assembly[M]. 3rd ed. Boca Raton: CRC Press, 2010.";
        const string RefRay = "Möller T, Trumbore B. Fast, minimum storage ray-triangle intersection[J]. Journal of Graphics Tools, 1997, 2(1): 21-28.";
        const string RefBvh = "Wald I. On fast construction of SAH-based bounding volume hierarchies[C]//2007 IEEE Symposium on Interactive Ray Tracing. Ulm: IEEE, 2007: 33-40.";

        const string RefAmStd = "ISO/ASTM 52910:2018 Additive manufacturing — Design — Requirements, guidelines and recommendations[S]. Geneva: ISO, 2018.";
        const string RefSlm = "Kranz J, Herzog D, Emmelmann C. Design guidelines for laser additive manufacturing of lightweight structures in TiAl6V4[J]. Journal of Laser Applications, 2015, 27(S1): S14001.";
        const string RefSheet = "Marciniak Z, Duncan J L, Hu S J. Mechanics of Sheet Metal Forming[M]. 2nd ed. Oxford: Butterworth-Heinemann, 2002.";
        const string RefLaser = "Steen W M, Mazumder J. Laser Material Processing[M]. 4th ed. London: Springer, 2010.";
        const string RefComposite = "Sheikh-Ahmad J Y. Machining of Polymer Composites[M]. New York: Springer, 2009.";
        const string RefMold = "Malloy R A. Plastic Part Design for Injection Molding: An Introduction[M]. 2nd ed. Munich: Hanser, 2010.";
        const string RefParting = "Chen L L, Chou S Y, Woo T C. Parting directions for mould and die design[J]. Computer-Aided Design, 1993, 25(12): 762-768.";

        static string F(double v, string f = "0.##") => v.ToString(f, CultureInfo.InvariantCulture);

        public static string ProcessName(DfmProcess p)
        {
            switch (p)
            {
                case DfmProcess.Fdm: return "FDM 熔融沉积 3D 打印";
                case DfmProcess.Sla: return "光固化 3D 打印（SLA / DLP）";
                case DfmProcess.Cnc: return "CNC 铣削";
                case DfmProcess.Sls: return "尼龙粉末烧结 3D 打印（SLS / MJF）";
                case DfmProcess.Slm: return "金属 3D 打印（SLM / DMLS）";
                case DfmProcess.Sheet: return "钣金（激光切割 + 折弯）";
                case DfmProcess.Cut: return "板材切割（激光 / 水刀 / CNC 雕刻）";
                default: return "注塑";
            }
        }

        /// <summary>短名字（界面上的工艺按钮、状态栏）。</summary>
        public static string ShortName(DfmProcess p) => new[] { "FDM 打印", "光固化", "CNC 铣削", "尼龙烧结", "金属打印", "钣金", "板材切割", "注塑" }[(int)p];

        /// <summary>估算卡片的标题。</summary>
        public static string MetricsTitle(DfmProcess p) => DfmProcesses.Printing(p) ? "打印估算" : p == DfmProcess.Mold ? "模具和成型估算" : "加工估算";

        static string MethodName(CutMethod m) => m == CutMethod.Laser ? "激光切割" : m == CutMethod.Waterjet ? "水刀切割" : "CNC 雕刻";

        /// <summary>报告和界面里「设备 / 关键参数」那一行。</summary>
        public static (string key, string value) KeySetting(DfmProcess p, DfmSettings s, DfmResult r)
        {
            switch (p)
            {
                case DfmProcess.Cnc: return ("最小壁厚要求", F(s.MinWall) + " mm");
                case DfmProcess.Sheet: return ("板厚", double.IsNaN(r.Thickness) ? "—" : F(r.Thickness) + " mm");
                case DfmProcess.Cut: return ("加工方式", MethodName(s.CutMethod) + (s.CutMethod == CutMethod.Router ? $"（Φ{F(s.CutterDia)} 铣刀）" : "") + (double.IsNaN(r.Thickness) ? "" : $"，板厚 {F(r.Thickness)} mm"));
                case DfmProcess.Mold: return ("开模方向", r.Orientation?.Label ?? "—");
                default: var b = s.Bed; return (p == DfmProcess.Fdm || p == DfmProcess.Sla ? "打印机" : "设备", $"{b.Name}（{F(b.X, "0")} × {F(b.Y, "0")} × {F(b.Z, "0")} mm）");
            }
        }

        /// <summary>figures：（图片路径, 图题）。</summary>
        public static ReportDoc Build(DfmMesh m, DfmResult r, DfmSettings s, IEnumerable<(string path, string caption)> figures)
        {
            var d = new ReportDoc
            {
                Title = "可制造性分析报告",
                Subtitle = $"{Path.GetFileName(m.Source)} · {ProcessName(r.Process)}",
                Author = "SW 工具箱",
            };
            int nErr = r.Issues.Count(i => i.Severity == Severity.Error), nWarn = r.Issues.Count(i => i.Severity == Severity.Warning);

            d.H1("1 概要");
            d.P(nErr + nWarn == 0
                ? $"按{ProcessName(r.Process)}的常用设计准则检查，没有发现需要修改的问题。"
                : $"按{ProcessName(r.Process)}的常用设计准则检查，发现 {nErr} 个必须修改的问题、{nWarn} 个建议修改的问题，详见第 4 节，每条都给出了修改建议。");
            d.KV(new List<(string, string)>
            {
                ("零件", Path.GetFileName(m.Source)),
                ("尺寸（包围盒）", $"{F(m.Max.X - m.Min.X, "0.#")} × {F(m.Max.Y - m.Min.Y, "0.#")} × {F(m.Max.Z - m.Min.Z, "0.#")} mm"),
                ("体积 / 表面积", $"{F(m.Volume / 1000, "0.##")} cm³ / {F(m.Area / 100, "0.#")} cm²"),
                ("表面网格", $"{m.Tris:N0} 个三角形，{m.Faces.Count} 个面" + (m.Closed ? "" : "（网格不封闭，体积和壁厚可能不准）")),
                ("工艺", ProcessName(r.Process)),
                ("材料", s.MaterialName),
                KeySetting(r.Process, s, r),
            });

            d.H1("2 方法");
            d.P($"零件表面离散为三角网格（STEP 由 Gmsh 按曲率加密划分，STL 直接读入并统一法向朝外；内部封闭的空腔单独定向），所有射线求交用包围体层次加速 [{d.Ref(RefBvh)}]、Möller–Trumbore 算法求交 [{d.Ref(RefRay)}]。各项检查方法如下：");
            if (r.Process == DfmProcess.Sls)
            {
                d.List(new[]
                {
                    $"**摆放和尺寸**：粉末烧结靠周围的粉末托住零件，不需要支撑 [{d.Ref(RefAm)}]；在 6 个坐标轴方向和最大的 12 个平面朝下的方向里取高度最低、放得进成型仓（{s.SlsMachine.Name}）的方向。",
                    $"**壁厚**：沿内法向的 5 条锥形射线取中位数（形状直径函数 [{d.Ref(RefSdf)}]），薄于 {F(s.MinWall)} mm 提示；孔径小于 {F(s.MinHoleDia)} mm 提示（粉末清不出来）[{d.Ref(RefAmStd)}]。",
                    $"**缝隙**：从每个面沿外法向投射射线，{F(s.MinGap)} mm 以内碰到朝向相反的面就是会被烧结粘住的窄缝。",
                    "**封闭空腔**：网格里被别的壳包住的壳（射线奇偶判断）就是封闭空腔，粉末出不来。",
                    "**翘曲**：面积大于 20 cm²、长度大于 80 mm、厚度不超过 3 mm 的平板提示翘曲风险。",
                    "**费用**：零件体积 × 每立方厘米单价 + 包围盒体积 × 0.03 元（占成型仓的空间）；时间按整炉高度约 25 mm/小时。都是估算。",
                });
            }
            else if (r.Process == DfmProcess.Slm)
            {
                d.List(new[]
                {
                    $"**悬垂和支撑**：与水平面夹角小于 {F(90 - s.OverhangDeg, "0")}° 的朝下的面需要支撑 [{d.Ref(RefDfam)}][{d.Ref(RefSlm)}]，Φ8 mm 以下的水平孔按能自支撑处理；支撑体积按竖直投射射线估算 [{d.Ref(RefSupport)}]。",
                    $"**摆放方向**：同 3D 打印，在 200 多个方向里按支撑体积、高度和底面稳定性打分 [{d.Ref(RefOrient)}]。",
                    "**支撑能否取出**：在悬垂区域上抽样，沿法向半球投射 13 条射线，几乎都被零件挡住就说明在封闭流道 / 内腔里，支撑取不出来。",
                    $"**壁厚、孔、缝隙、空腔**：壁厚按形状直径函数 [{d.Ref(RefSdf)}]，薄于 {F(s.MinWall)} mm 提示；水平孔大于 Φ8 mm 提示改成泪滴形；{F(s.MinGap)} mm 以内的缝和封闭空腔提示粉末取不出。",
                    "**残余应力**：平行于基板、面积大于 25 cm² 的平面提示翘曲 / 开裂风险。",
                    "**费用**：（零件 + 支撑按 20% 实体）重量 × 每克单价 + 后处理；时间 = 层数 × 铺粉时间 + 体积 ÷ 成形速率。都是估算。",
                });
            }
            else if (r.Process == DfmProcess.Sheet)
            {
                d.List(new[]
                {
                    $"**板厚和是否等厚**：按形状直径函数测每个三角形的局部厚度 [{d.Ref(RefSdf)}]，面积加权的众数就是板厚 t；厚度 ≈ t 的面是板面（表皮），和板面相连的其他面是切割边；隔着材料正对板面、距离却不是 t 的面（凸台顶面、沉槽底面）和离板面很远的部分是钣金做不出来的厚实部分。等厚部分不到表面的 55%、或体积超过同面积板材 25% 时判为不能做钣金。",
                    $"**折弯**：一对同轴、半径相差 t 的凹 / 凸圆柱面就是一个折弯，凹面半径是内折弯半径 R，覆盖角是折弯角；两块板面直接相交的凹棱是没有圆角的尖角折弯。R 按材料检查（{s.SheetMaterial.Name}建议 ≥ {F(s.SheetMaterial.RecR, "0.#")}t）[{d.Ref(RefSheet)}]。",
                    "**折边长度**：和折弯内侧相邻的平面，沿垂直于折弯线的方向量到最远处是直边长度，外尺寸 ≈ 直边 + R + t，要求 ≥ max(4t, 3t + R)。",
                    "**孔和开口**：切割边连成的环就是轮廓（最大的是外形），孔边到折弯圆角起点 ≥ 2t、孔边到板边 ≥ 1.5t、孔间距 ≥ t、孔径 ≥ t；距离用从切割边沿法向投射的射线测量。",
                    $"**费用**：展开面积 ≈ 表皮面积 ÷ 2；切割长度 = 切割边面积 ÷ 板厚；激光切割按长度和穿孔计价 [{d.Ref(RefLaser)}]，折弯按刀数，材料按 75% 排料利用率 [{d.Ref(RefDfma)}]。都是估算。",
                });
            }
            else if (r.Process == DfmProcess.Cut)
            {
                d.List(new[]
                {
                    "**板面和轮廓**：面积最大的一组平行平面是板面，上下板面之间的距离是板厚；法向垂直于板面、贯穿整个板厚的面是切割边，其余的面（凸起、沉槽、沉头孔、倒角）都要另外铣削。",
                    $"**内角和窄槽**：CNC 雕刻的铣刀是圆的，内角至少留下刀具半径（Φ{F(s.CutterDia)} 刀 → R{F(s.CutterDia / 2)}）的圆角，比刀窄的槽铣不出来；水刀的水柱约 Φ0.8–1 mm；激光切缝 0.1–0.3 mm，内角基本是尖的 [{d.Ref(RefLaser)}]。",
                    $"**筋宽和孔边距**：从切割边沿法向往材料里投射射线到对面切割边的距离就是筋宽；{s.PlateMaterial.Name}要求 ≥ {F(s.PlateMaterial.MinWeb(double.IsNaN(r.Thickness) ? 3 : r.Thickness), "0.#")} mm，孔到板边 ≥ 1.5t。碳纤维 / 玻纤板铣削表层会切断纤维、窄的地方容易分层 [{d.Ref(RefComposite)}]。",
                    "**费用**：切割长度 = 切割边面积 ÷ 板厚；激光按长度和穿孔计价，水刀和雕刻按机时（切割长度 × 层数 ÷ 进给速度）计价；材料按外接矩形每边留 5 mm。都是估算。",
                });
            }
            else if (r.Process == DfmProcess.Mold)
            {
                d.List(new[]
                {
                    $"**开模方向和倒扣**：对候选方向（坐标轴、最大平面的法向、斜向）计算每个面沿 +d / −d 是否可见（可见性方法 [{d.Ref(RefVisibility)}]），两边都看不到的面就是倒扣，取倒扣面积最小、深度最浅的方向 [{d.Ref(RefParting)}]；倒扣区域再按 8 个侧向方向判断能否用滑块抽出。",
                    $"**拔模角**：侧壁的拔模角 = 面法向与开模方向夹角的余角，小于 {F(s.MinDraft, "0.#")}° 提示，小于 0.5° 必须修改 [{d.Ref(RefMold)}]。",
                    $"**壁厚**：名义壁厚取形状直径函数 [{d.Ref(RefSdf)}] 的面积加权中位数；超过名义壁厚 1.5 倍或 {s.MoldMaterial.Name} 推荐上限 {F(s.MoldMaterial.MaxWall)} mm 的地方，再用内切球修正（排除加强筋根部）后提示缩痕 / 气孔；薄于 {F(s.MinWall)} mm 提示充不满。",
                    "**尖内角**：法向夹角大于 30° 的凹棱就是没有圆角的内角，建议 R ≥ 0.5t。",
                    $"**费用**：锁模力 = 投影面积 × 型腔压力系数；冷却时间 ≈ 系数 × 壁厚²；模具费按尺寸、深度、滑块 / 斜顶数分档估算 [{d.Ref(RefDfma)}]。都是估算。",
                });
            }
            else if (r.Process != DfmProcess.Cnc)
            {
                d.List(new[]
                {
                    $"**悬垂**：面的外法向与打印方向的夹角大于 {F(90 + s.OverhangDeg, "0")}°（即与竖直方向的悬垂角超过 {F(s.OverhangDeg, "0")}°，常用的「45° 规则」）时需要支撑 [{d.Ref(RefDfam)}][{d.Ref(RefAm)}]；贴在打印平台上的面不计。",
                    $"**支撑体积**：在悬垂面上按面积均匀采样，沿打印方向反向投射射线，到平台或到零件下方表面的距离乘以投影面积求和 [{d.Ref(RefSupport)}]。",
                    $"**摆放方向**：在 6 个坐标轴方向、最大的 12 个平面朝下的方向和球面上均匀分布的 200 个方向中，按「支撑体积 / 零件体积 + 0.25 × 高度 / 对角线 + 0.5 × 底面接触不足」打分，取得分最低且彼此相差 20° 以上的 3 个 [{d.Ref(RefOrient)}]。",
                    $"**壁厚**：从每个三角形沿内法向投射 5 条锥形射线（锥角 15°），只取打到背面（法向大致相反）的距离，取中位数作为局部壁厚（形状直径函数 [{d.Ref(RefSdf)}]）。",
                    "**用料和时间**：外壳体积 = 表面积 × 外壳厚度，填充体积 = （零件体积 − 外壳）× 填充率，支撑按 15% 密度；FDM 时间 = 用料 ÷ 平均挤出量 + 层数 × 每层固定开销；光固化时间 = 层数 × 每层时间。都是估算，实际以切片软件为准。",
                });
            }
            else
            {
                d.List(new[]
                {
                    $"**可达性和装夹**：对 ±X / ±Y / ±Z{(s.FiveAxisTilts ? " 和 12 个 45° 斜向" : "")}，从每个三角形的 4 个采样点沿刀轴方向投射射线，没有遮挡且面不背对刀具就算可加工（可见性方法 [{d.Ref(RefVisibility)}]）；再用贪心法选最少的装夹方向覆盖全部表面，哪个方向都够不到的面就是倒扣。",
                    "**内角**：两个平面在凹边上相交、凹边平行于刀轴且两面都是侧壁时，立铣刀做不出尖角；凹圆柱面的半径就是能用的最大刀具半径，半径小或长径比大于 4 时提示。",
                    $"**深腔 / 窄槽**：在朝刀具的底面上，沿水平 4 个方向测两侧到侧壁的距离之和作为宽度，侧壁顶部到底面的高度作为深度，深宽比大于 {F(s.DeepPocketRatio, "0")} 或宽度小于最小刀具时提示。",
                    $"**孔**：同轴同半径的凹圆柱面合成一个孔，深径比大于 {F(s.DeepHoleRatio, "0")} 或直径小于 1 mm 时提示。",
                    $"**壁厚**：同 3D 打印，按形状直径函数 [{d.Ref(RefSdf)}]。",
                    $"**时间和费用**：毛坯 = 包围盒每边加 {F(s.StockMargin)} mm；时间 = 去除体积 ÷ 粗加工去除率 + 表面积 ÷ 精加工速度 + 装夹次数 × 每次装夹时间；费用 = 机时 × 小时费率 + 毛坯重量 × 材料单价 [{d.Ref(RefDfma)}]。都是估算。",
                });
            }

            d.H1("3 结果");
            d.Table("主要指标", new[] { "项目", "数值", "说明" }, r.Metrics.Select(x => new[] { x.Name, x.Value, x.Note ?? "" }), "指标");
            if (r.Process == DfmProcess.Sls && r.Candidates.Count > 0)
                d.Table("推荐的摆放方向（高度越低越快）", new[] { "序号", "方向", "高度 (mm)", "放得下" },
                    r.Candidates.Select((c, i) => new[] { (i + 1).ToString(), c.Label, F(c.Height, "0.#"), c.Fits ? "是" : "否" }), "摆放方向");
            else if (r.Process == DfmProcess.Mold && r.Candidates.Count > 0)
                d.Table("候选开模方向（倒扣越少越好）", new[] { "序号", "方向", "倒扣面积 (mm²)", "深度 (mm)" },
                    r.Candidates.Select((c, i) => new[] { (i + 1).ToString(), c.Label, F(c.SupportArea, "0"), F(c.Height, "0.#") }), "开模方向");
            else if (DfmProcesses.Printing(r.Process) && r.Candidates.Count > 0)
                d.Table("推荐的摆放方向（得分越低越好）", new[] { "序号", "方向", "支撑体积 (cm³)", "高度 (mm)", "底面接触 (mm²)", "得分" },
                    r.Candidates.Select((c, i) => new[] { (i + 1).ToString(), c.Label, F(c.SupportVolume / 1000, "0.##"), F(c.Height, "0.#"), F(c.BaseArea, "0"), F(c.Score, "0.###") }), "摆放方向");
            foreach (var (path, caption) in figures ?? Enumerable.Empty<(string, string)>()) d.Img(path, caption, 14);
            if (r.Notes.Count > 0)
            {
                d.H2("工艺提示");
                d.List(r.Notes);
            }

            d.H1("4 问题和修改建议");
            if (r.Issues.Count == 0) d.P("没有发现问题。");
            else
            {
                d.Table("问题清单", new[] { "序号", "严重程度", "问题", "数值", "位置 (mm)" },
                    r.Issues.Select((x, i) => new[] { (i + 1).ToString(), Sev(x.Severity), x.Title, x.Value, $"({F(x.Location.X, "0.#")}, {F(x.Location.Y, "0.#")}, {F(x.Location.Z, "0.#")})" }), "问题清单");
                d.H2("修改建议");
                d.List(r.Issues.Select((x, i) => $"**{i + 1}. {x.Title}**：{x.Suggestion}"));
            }
            d.H1("5 说明");
            d.Note("本报告由 SW 工具箱根据零件几何自动生成，检查的是常见的设计准则，不能代替加工厂的工艺评审；估算的时间和费用只用于方案比较。");
            return d;
        }

        public static string Sev(Severity s) => s == Severity.Error ? "必须修改" : s == Severity.Warning ? "建议修改" : "提示";
    }
}
