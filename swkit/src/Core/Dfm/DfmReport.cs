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

        static string F(double v, string f = "0.##") => v.ToString(f, CultureInfo.InvariantCulture);

        public static string ProcessName(DfmProcess p) => p == DfmProcess.Fdm ? "FDM 熔融沉积 3D 打印" : p == DfmProcess.Sla ? "光固化 3D 打印（SLA / DLP）" : "CNC 铣削";

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
                ("材料", r.Process == DfmProcess.Cnc ? s.CncMaterial.Name : s.PrintMaterial.Name),
                (r.Process == DfmProcess.Cnc ? "最小壁厚要求" : "打印机", r.Process == DfmProcess.Cnc ? F(s.MinWall) + " mm" : $"{s.Printer.Name}（{F(s.Printer.X, "0")} × {F(s.Printer.Y, "0")} × {F(s.Printer.Z, "0")} mm）"),
            });

            d.H1("2 方法");
            d.P($"零件表面离散为三角网格（STEP 由 Gmsh 按曲率加密划分，STL 直接读入并统一法向朝外），所有射线求交用包围体层次加速 [{d.Ref(RefBvh)}]、Möller–Trumbore 算法求交 [{d.Ref(RefRay)}]。各项检查方法如下：");
            if (r.Process != DfmProcess.Cnc)
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
            if (r.Process != DfmProcess.Cnc && r.Candidates.Count > 0)
                d.Table("推荐的摆放方向（得分越低越好）", new[] { "序号", "方向", "支撑体积 (cm³)", "高度 (mm)", "底面接触 (mm²)", "得分" },
                    r.Candidates.Select((c, i) => new[] { (i + 1).ToString(), c.Label, F(c.SupportVolume / 1000, "0.##"), F(c.Height, "0.#"), F(c.BaseArea, "0"), F(c.Score, "0.###") }), "摆放方向");
            foreach (var (path, caption) in figures ?? Enumerable.Empty<(string, string)>()) d.Img(path, caption, 14);

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
