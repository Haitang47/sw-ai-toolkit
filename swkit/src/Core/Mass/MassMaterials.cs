using System;
using System.Collections.Generic;
using System.Linq;

namespace SwKit.Core.Mass
{
    /// <summary>质量估算用的材料：只需要密度。FDM 打印件按外壳 + 填充率算（实际重量比实心轻得多）。</summary>
    public sealed class MassMaterial
    {
        public string Category, Name;
        public double Density;          // g/cm³（实心）
        public bool Fdm;                // FDM 打印：按外壳厚度 + 填充率算
        public string Note;
        public override string ToString() => Name;
    }

    /// <summary>
    /// 常用材料的密度表（g/cm³）。数值取牌号标准、材料厂家数据表的典型值；
    /// 同一材料不同批次、不同工艺会有 ±1–3% 的差别，复合材料和泡沫的差别更大（见各条备注）。
    /// </summary>
    public static class MassMaterials
    {
        static MassMaterial M(string cat, string name, double rho, string note = null, bool fdm = false) =>
            new MassMaterial { Category = cat, Name = name, Density = rho, Note = note, Fdm = fdm };

        public static readonly List<MassMaterial> All = new List<MassMaterial>
        {
            // 钢、铸铁
            M("钢 / 铸铁", "Q235 低碳钢", 7.85), M("钢 / 铸铁", "45 钢", 7.85), M("钢 / 铸铁", "40Cr 合金钢", 7.85), M("钢 / 铸铁", "65Mn 弹簧钢", 7.85),
            M("钢 / 铸铁", "Cr12MoV 模具钢", 7.70), M("钢 / 铸铁", "冷轧钢板 SPCC", 7.85), M("钢 / 铸铁", "镀锌钢板 SGCC", 7.85),
            M("钢 / 铸铁", "灰铸铁 HT200", 7.20), M("钢 / 铸铁", "球墨铸铁 QT450", 7.10),
            // 不锈钢
            M("不锈钢", "304 不锈钢", 7.93), M("不锈钢", "316L 不锈钢", 7.98), M("不锈钢", "301 弹簧不锈钢", 7.93), M("不锈钢", "420 不锈钢", 7.73), M("不锈钢", "17-4PH 不锈钢", 7.78),
            // 铝合金
            M("铝合金", "6061-T6 铝", 2.70, "CNC 最常用"), M("铝合金", "6063 铝（型材）", 2.69, "铝型材"), M("铝合金", "7075-T6 铝", 2.81, "高强度"),
            M("铝合金", "5052 铝（钣金）", 2.68, "钣金、折弯件常用"), M("铝合金", "5083 铝", 2.66), M("铝合金", "1060 纯铝", 2.71), M("铝合金", "3003 铝", 2.73),
            M("铝合金", "2A12 / 2024 硬铝", 2.78), M("铝合金", "ADC12 压铸铝", 2.74), M("铝合金", "A356 铸铝", 2.68),
            // 镁、钛
            M("镁 / 钛", "AZ31B 镁合金", 1.77), M("镁 / 钛", "AZ91D 压铸镁", 1.81), M("镁 / 钛", "TA2 纯钛", 4.51), M("镁 / 钛", "TC4 钛合金（Ti-6Al-4V）", 4.43),
            // 铜和其他金属
            M("铜 / 其他金属", "T2 紫铜", 8.94), M("铜 / 其他金属", "H62 黄铜", 8.43), M("铜 / 其他金属", "H59 黄铜", 8.40), M("铜 / 其他金属", "锡青铜 QSn6.5-0.1", 8.80),
            M("铜 / 其他金属", "铍铜 C17200", 8.25), M("铜 / 其他金属", "锌合金 Zamak 3", 6.60), M("铜 / 其他金属", "铅（配重）", 11.34),
            M("铜 / 其他金属", "钨合金 W90（配重）", 17.0), M("铜 / 其他金属", "钕铁硼磁铁", 7.50),
            // 复合材料
            M("复合材料", "碳纤维板 3K（预浸料压制）", 1.55, "纤维体积含量约 55–60%；手糊板约 1.4"),
            M("复合材料", "碳纤维板（单向 UD）", 1.60),
            M("复合材料", "碳纤维管（卷制）", 1.55, "按管壁实体体积算"),
            M("复合材料", "碳纤维管（拉挤）", 1.60),
            M("复合材料", "玻纤板 FR4", 1.85), M("复合材料", "玻纤板 G10", 1.80), M("复合材料", "芳纶（Kevlar）层压板", 1.38),
            // 工程塑料（注塑、CNC 板材 / 棒材）
            M("工程塑料", "ABS", 1.05), M("工程塑料", "PC 聚碳酸酯", 1.20), M("工程塑料", "PA6 尼龙", 1.14), M("工程塑料", "PA66 尼龙", 1.14),
            M("工程塑料", "MC 尼龙（浇铸）", 1.15), M("工程塑料", "PA66-GF30 玻纤尼龙", 1.36), M("工程塑料", "POM 赛钢", 1.41), M("工程塑料", "PEEK", 1.30),
            M("工程塑料", "PEI（Ultem）", 1.27), M("工程塑料", "亚克力 PMMA", 1.19), M("工程塑料", "PP 聚丙烯", 0.90), M("工程塑料", "HDPE 高密度聚乙烯", 0.95),
            M("工程塑料", "UHMWPE 超高分子量聚乙烯", 0.93), M("工程塑料", "PTFE 铁氟龙", 2.20), M("工程塑料", "PVC", 1.40), M("工程塑料", "PET", 1.38), M("工程塑料", "PPS", 1.35),
            // 橡胶和软材料
            M("橡胶 / 软材料", "TPU（注塑）", 1.20), M("橡胶 / 软材料", "硅胶", 1.15), M("橡胶 / 软材料", "丁腈橡胶 NBR", 1.15), M("橡胶 / 软材料", "EPDM 橡胶", 1.15), M("橡胶 / 软材料", "天然橡胶", 0.93),
            // FDM 打印（实心密度；算质量时按外壳 + 填充率）
            M("FDM 打印", "PLA", 1.24, null, true), M("FDM 打印", "PETG", 1.27, null, true), M("FDM 打印", "ABS（打印）", 1.04, null, true), M("FDM 打印", "ASA", 1.07, null, true),
            M("FDM 打印", "TPU 95A", 1.21, null, true), M("FDM 打印", "PC（打印）", 1.20, null, true), M("FDM 打印", "PA-CF 尼龙碳纤维", 1.17, null, true),
            M("FDM 打印", "PETG-CF", 1.25, null, true), M("FDM 打印", "PLA-CF", 1.22, null, true), M("FDM 打印", "PEEK（打印）", 1.30, null, true),
            // 光固化
            M("光固化", "标准树脂", 1.18), M("光固化", "韧性树脂", 1.15), M("光固化", "耐高温树脂", 1.20), M("光固化", "类 ABS 树脂", 1.12),
            // 尼龙烧结（零件密度，比原料低）
            M("尼龙烧结", "PA12（SLS）", 0.95, "烧结件有少量孔隙"), M("尼龙烧结", "PA12（MJF）", 1.01), M("尼龙烧结", "PA11", 1.05), M("尼龙烧结", "PA12 玻璃微珠（GB）", 1.30), M("尼龙烧结", "TPU（烧结）", 1.10),
            // 金属打印
            M("金属打印", "AlSi10Mg", 2.67), M("金属打印", "316L（打印）", 7.95), M("金属打印", "Ti6Al4V（打印）", 4.41), M("金属打印", "18Ni300 模具钢", 8.00),
            M("金属打印", "17-4PH（打印）", 7.78), M("金属打印", "Inconel 718", 8.19), M("金属打印", "CuCrZr 铜合金", 8.90),
            // 木材、泡沫
            M("木材 / 泡沫", "巴沙木", 0.16, "0.1–0.25 之间，按实物挑"), M("木材 / 泡沫", "椴木层板", 0.55), M("木材 / 泡沫", "桦木层板", 0.68), M("木材 / 泡沫", "MDF 密度板", 0.75),
            M("木材 / 泡沫", "EPP 泡沫", 0.03, "常见 20–60 g/L"), M("木材 / 泡沫", "EPS 泡沫", 0.02), M("木材 / 泡沫", "EVA 泡沫", 0.10), M("木材 / 泡沫", "PU 硬泡", 0.04),
            // 其他
            M("其他", "玻璃", 2.50), M("其他", "氧化铝陶瓷", 3.90), M("其他", "PCB（FR4 覆铜板）", 1.90, "不含元件"), M("其他", "水", 1.00),
        };

        public static IEnumerable<string> Categories => All.Select(m => m.Category).Distinct();

        public static MassMaterial Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return All.FirstOrDefault(m => m.Name == name)
                ?? All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? All.FirstOrDefault(m => m.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 从 SolidWorks 材料名或文件名猜材料。只认比较明确的词（避免把「PLATE」认成 PLA、「零件45」认成 45 钢）；
        /// 猜不出返回 null。token 是命中的词，用来告诉用户为什么这么选。
        /// </summary>
        public static MassMaterial Guess(string text, bool fromFileName, out string token)
        {
            token = null;
            if (string.IsNullOrWhiteSpace(text)) return null;
            var s = text.ToUpperInvariant();
            var rules = new List<(string key, string mat)>
            {
                ("碳纤维管", "碳纤维管（卷制）"), ("碳管", "碳纤维管（卷制）"), ("碳纤", "碳纤维板 3K（预浸料压制）"), ("碳板", "碳纤维板 3K（预浸料压制）"),
                ("CARBON", "碳纤维板 3K（预浸料压制）"), ("CFRP", "碳纤维板 3K（预浸料压制）"),
                ("PA-CF", "PA-CF 尼龙碳纤维"), ("PACF", "PA-CF 尼龙碳纤维"), ("PETG-CF", "PETG-CF"), ("PLA-CF", "PLA-CF"),
                ("玻纤", "玻纤板 FR4"), ("FR4", "玻纤板 FR4"), ("FR-4", "玻纤板 FR4"), ("G10", "玻纤板 G10"),
                ("7075", "7075-T6 铝"), ("6061", "6061-T6 铝"), ("6063", "6063 铝（型材）"), ("5052", "5052 铝（钣金）"), ("5083", "5083 铝"),
                ("2A12", "2A12 / 2024 硬铝"), ("ADC12", "ADC12 压铸铝"), ("型材", "6063 铝（型材）"), ("铝", "6061-T6 铝"), ("ALUMIN", "6061-T6 铝"),
                ("AZ31", "AZ31B 镁合金"), ("AZ91", "AZ91D 压铸镁"), ("镁", "AZ31B 镁合金"),
                ("TC4", "TC4 钛合金（Ti-6Al-4V）"), ("TI-6AL", "TC4 钛合金（Ti-6Al-4V）"), ("TI6AL", "TC4 钛合金（Ti-6Al-4V）"), ("钛", "TA2 纯钛"), ("TITAN", "TA2 纯钛"),
                ("SUS316", "316L 不锈钢"), ("316L", "316L 不锈钢"), ("17-4PH", "17-4PH 不锈钢"), ("不锈", "304 不锈钢"), ("STAINLESS", "304 不锈钢"), ("SUS", "304 不锈钢"),
                ("黄铜", "H62 黄铜"), ("BRASS", "H62 黄铜"), ("青铜", "锡青铜 QSn6.5-0.1"), ("BRONZE", "锡青铜 QSn6.5-0.1"), ("紫铜", "T2 紫铜"), ("COPPER", "T2 紫铜"),
                ("铸铁", "灰铸铁 HT200"), ("CAST IRON", "灰铸铁 HT200"), ("40CR", "40Cr 合金钢"), ("65MN", "65Mn 弹簧钢"), ("SPCC", "冷轧钢板 SPCC"),
                ("钕铁硼", "钕铁硼磁铁"), ("磁铁", "钕铁硼磁铁"), ("MAGNET", "钕铁硼磁铁"),
                ("亚克力", "亚克力 PMMA"), ("PMMA", "亚克力 PMMA"), ("ACRYLIC", "亚克力 PMMA"), ("POM", "POM 赛钢"), ("赛钢", "POM 赛钢"), ("DELRIN", "POM 赛钢"),
                ("PEEK", "PEEK"), ("PTFE", "PTFE 铁氟龙"), ("铁氟龙", "PTFE 铁氟龙"), ("TEFLON", "PTFE 铁氟龙"), ("UHMW", "UHMWPE 超高分子量聚乙烯"),
                ("PETG", "PETG"), ("TPU", "TPU 95A"), ("硅胶", "硅胶"), ("SILICONE", "硅胶"), ("橡胶", "丁腈橡胶 NBR"), ("RUBBER", "丁腈橡胶 NBR"),
                ("尼龙", "PA6 尼龙"), ("NYLON", "PA6 尼龙"), ("树脂", "标准树脂"), ("RESIN", "标准树脂"),
                ("巴沙", "巴沙木"), ("BALSA", "巴沙木"), ("椴木", "椴木层板"), ("层板", "椴木层板"), ("PLYWOOD", "椴木层板"), ("EPP", "EPP 泡沫"), ("EVA", "EVA 泡沫"),
                ("PCB", "PCB（FR4 覆铜板）"), ("玻璃", "玻璃"), ("GLASS", "玻璃"),
            };
            if (!fromFileName)
            {
                // SolidWorks 材料名可以放宽：这些词在材料名里不会有歧义（文件名里的纯数字可能是尺寸或年份，不用）
                rules.InsertRange(0, new[] { ("2024", "2A12 / 2024 硬铝"), ("1060", "1060 纯铝"), ("316", "316L 不锈钢"), ("304", "304 不锈钢"), ("17-4", "17-4PH 不锈钢") });
                rules.AddRange(new[] { ("ABS", "ABS"), ("PLA", "PLA"), ("PC ", "PC 聚碳酸酯"), ("POLYCARBONATE", "PC 聚碳酸酯"), ("PP ", "PP 聚丙烯"), ("POLYPROPYLENE", "PP 聚丙烯"),
                                       ("1045", "45 钢"), ("AISI 1020", "Q235 低碳钢"), ("PLAIN CARBON", "Q235 低碳钢"), ("ALLOY STEEL", "40Cr 合金钢"), ("钢", "Q235 低碳钢"), ("STEEL", "Q235 低碳钢") });
            }
            else
            {
                rules.AddRange(new[] { ("钢板", "冷轧钢板 SPCC"), ("钢", "Q235 低碳钢") });
            }
            foreach (var (key, mat) in rules)
                if (s.Contains(key))
                {
                    var m = All.FirstOrDefault(x => x.Name == mat);
                    if (m != null) { token = key.Trim(); return m; }
                }
            return null;
        }
    }
}
