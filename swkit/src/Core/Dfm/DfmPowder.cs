using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwKit.Core.Urdf;

namespace SwKit.Core.Dfm
{
    /// <summary>粉末床打印：尼龙粉末烧结（SLS / MJF）和金属粉末熔化（SLM / DMLS）。</summary>
    public sealed partial class DfmAnalyzer
    {
        // ================= 尼龙粉末烧结（SLS / MJF） =================

        void AnalyzeSls(DfmSettings s, Vec3? up, DfmResult r, Action<string> log, CancellationToken ct)
        {
            var m = Mesh; var mc = s.SlsMachine; var mat = s.SlsMaterial;
            log?.Invoke("正在找摆放方向");
            // 不需要支撑：摆放只影响高度（时间）和能不能放进成型仓。候选 = 6 个坐标轴 + 最大的几个平面朝下
            var cands = new List<Orientation>();
            foreach (var (d, label) in AxisAndPlaneDirs())
            {
                ct.ThrowIfCancellationRequested();
                var ext = Extents(d);
                bool fits = FitsBox(ext, mc);
                cands.Add(new Orientation { Up = d, Height = ext.h, Fits = fits, Label = label, Score = ext.h / Math.Max(m.Diag, 1e-9) + (fits ? 0 : 10) });
            }
            r.Candidates = PickDistinct(cands.OrderBy(c => c.Score), 3, 20, true);   // 不用支撑，朝上朝下一样
            Orientation o;
            if (up.HasValue)
            {
                var d = up.Value.Normalized(); var ext = Extents(d);
                o = new Orientation { Up = d, Height = ext.h, Fits = FitsBox(ext, mc), Label = UpLabel(d) };
            }
            else o = r.Candidates.FirstOrDefault() ?? new Orientation { Up = new Vec3(0, 0, 1), Height = m.Max.Z - m.Min.Z, Label = "+Z 朝上" };
            r.Orientation = o; r.ViewUp = o.Up; r.Bed = mc;
            FitIssue(s, r);

            Cavities(s, r);
            Gaps(s, r, s.MinGap);
            FlatWarp(s, r);

            // 估算：按零件体积 + 包围盒体积（占成型仓的空间）计价；整炉打印时间按高度
            var e = Extents(o.Up);
            double V = m.Volume / 1000, box = e.w1 * e.w2 * e.h / 1000;
            double grams = V * mat.Density;
            double price = Math.Max(15, V * mat.Price + box * 0.03);
            double hours = o.Height / 25;
            r.Metrics.Add(new Metric("摆放方向", o.Label ?? Vec(o.Up), "粉末烧结不需要支撑，摆放主要影响高度（打印时间）和台阶纹的方向"));
            r.Metrics.Add(new Metric("打印高度", F(o.Height, "0.#") + " mm"));
            r.Metrics.Add(new Metric("零件体积", F(V, "0.##") + " cm³", $"重 {F(grams, "0.#")} g（{mat.Name} {F(mat.Density)} g/cm³）"));
            r.Metrics.Add(new Metric("包围盒体积", F(box, "0.#") + " cm³", "占成型仓的空间，影响排版和价格"));
            r.Metrics.Add(new Metric("打印费（估算）", $"{F(price, "0")} 元", $"零件体积 × {F(mat.Price)} 元/cm³ + 包围盒体积 × 0.03 元/cm³，单件最低 15 元（国内常见在线报价，仅供比较）"));
            r.Metrics.Add(new Metric("打印时间（估算）", $"约 {F(Math.Max(1, hours), "0.#")} 小时 + 同样长的冷却", "整炉按高度约 25 mm/小时（和别的零件一起排版）；交期一般 3–5 天"));
            r.Notes.Add($"{(mc.Mjf ? "MJF" : "SLS")} 零件表面有细小颗粒感（Ra 约 6–10 µm），本色{(mc.Mjf ? "灰黑" : "白")}，可以喷砂、染黑、蒸汽抛光。尺寸精度约 ±0.3%（最小 ±0.3 mm）。");
            r.Notes.Add($"活动配合的间隙单边留 ≥ {F(s.MinGap)} mm；螺纹建议用热熔铜螺母或自攻螺丝，不要直接打印细牙螺纹。");
            if (mat.Note != null) r.Notes.Add($"{mat.Name}：{mat.Note}。");
        }

        // ================= 金属粉末熔化（SLM / DMLS） =================

        void AnalyzeSlm(DfmSettings s, Vec3? up, DfmResult r, Action<string> log, CancellationToken ct)
        {
            var m = Mesh; var mat = s.SlmMaterial;
            PlaceForPrint(s, up, r, log, ct);
            var o = r.Orientation; var d = o.Up;
            // Φ8 以下的水平孔能自支撑（顶部略粗糙），不算悬垂
            var selfSupport = new HashSet<int>();
            foreach (var g in CylGroups().Where(g => g.Coverage > 5.8 && Math.Abs(g.Axis.Dot(d)) < 0.26 && 2 * g.Radius <= 8.01))
                foreach (var t in g.Tris) selfSupport.Add(t);
            log?.Invoke("正在检查支撑能不能取出来");
            var inside = InternalSupports(s, r, selfSupport, ct);
            OverhangIssues(s, r, t => selfSupport.Contains(t) || inside.Contains(t) || m.Shells[m.ShellOf[t]].Cavity);
            FitIssue(s, r);
            Cavities(s, r);
            Gaps(s, r, s.MinGap);

            // 水平大孔
            foreach (var g in CylGroups().Where(g => g.Coverage > 5.8 && Math.Abs(g.Axis.Dot(d)) < 0.26 && 2 * g.Radius > 8.01))
            {
                double dia = 2 * g.Radius;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "水平孔", Flag = TriFlag.Hole, Severity = Severity.Warning, Tris = g.Tris.ToList(), Location = g.Centroid, Number = dia,
                    Title = $"水平大孔 Φ{F(dia, "0.#")}", Value = $"孔轴线是水平的，Φ > 8 mm 的孔顶部会塌、挂渣，要在孔里加支撑",
                    Suggestion = "把孔的上半部分改成泪滴形（顶部 45°–60° 的尖角）或菱形，就不用支撑；要求圆孔的话打印时做成泪滴形或做小，打印后再镗 / 铰到尺寸。也可以换摆放方向让孔竖起来。",
                });
            }

            // 大面积平面平行于基板：残余应力大
            double plateA = 0; var plateTris = new List<int>();
            for (int t = 0; t < m.Tris; t++) if ((r.Flags[t] & TriFlag.OnPlate) != 0) { plateA += m.A[t]; plateTris.Add(t); }
            var bigFlat = m.Faces.Where(f => f.Kind == FaceKind.Plane && Math.Abs(f.Normal.Dot(d)) > 0.996 && f.Area > 2500).OrderByDescending(f => f.Area).FirstOrDefault();
            if (plateA > 2500 || bigFlat != null)
            {
                var tris = plateA > 2500 ? plateTris : bigFlat.Tris.ToList();
                double a = plateA > 2500 ? plateA : bigFlat.Area;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "残余应力", Flag = TriFlag.OnPlate, Severity = Severity.Warning, Tris = tris, Location = Centroid(tris), Number = a,
                    Title = $"大平面平行于基板（{F(a / 100, "0")} cm²）",
                    Value = "一层里要熔化的面积大，冷却收缩产生的残余应力大，容易翘曲、开裂或者把支撑拉断",
                    Suggestion = "把零件斜放 30–45°（每层的截面积变小）；大平面做成镂空 / 格栅，或者加加强筋；打印后先去应力退火再从基板上切下来。",
                });
            }

            // 估算
            double V = m.Volume / 1000, sv = o.SupportVolume / 1000 * 0.2;   // 支撑按 20% 实体估算
            double grams = V * mat.Density, sg = sv * mat.Density;
            double print = (grams + sg) * mat.Price;
            double post = 80 + o.SupportArea / 100 * 0.5;
            double layers = Math.Ceiling(o.Height / mat.Layer);
            double hours = layers * 8 / 3600 + (V + sv) / mat.BuildRate;
            r.Metrics.Add(new Metric("摆放方向", o.Label ?? Vec(o.Up)));
            r.Metrics.Add(new Metric("打印高度", F(o.Height, "0.#") + " mm"));
            r.Metrics.Add(new Metric("需要支撑的面积", F(o.SupportArea, "0") + " mm²", "悬垂面在基板上的投影面积（Φ8 以下的水平孔不算）"));
            r.Metrics.Add(new Metric("支撑体积（估算）", F(o.SupportVolume / 1000, "0.##") + " cm³", "从悬垂面向下到基板或零件的体积；实际支撑是网格，按 20% 实体算用料"));
            r.Metrics.Add(new Metric("零件重量", $"{F(grams, "0.#")} g", $"{F(V, "0.##")} cm³ × {F(mat.Density)} g/cm³（{mat.Name}）"));
            r.Metrics.Add(new Metric("打印费（估算）", $"{F(print, "0")} 元", $"（零件 {F(grams, "0.#")} g + 支撑 {F(sg, "0.#")} g）× {F(mat.Price)} 元/g（国内常见报价，仅供比较）"));
            r.Metrics.Add(new Metric("后处理（估算）", $"{F(post, "0")} 元", "线切割下基板、去应力热处理、去支撑、喷砂：80 元 + 支撑面积 × 0.5 元/cm²"));
            r.Metrics.Add(new Metric("合计（估算）", $"{F(print + post, "0")} 元", "不含 CNC 精加工和表面处理"));
            r.Metrics.Add(new Metric("打印时间（估算）", Time(hours * 3600), $"{layers:0} 层（{F(mat.Layer * 1000, "0")} µm）× 铺粉 8 秒 + 体积 ÷ {F(mat.BuildRate, "0")} cm³/小时"));
            r.Notes.Add($"后处理：线切割从基板上取下 → 去应力热处理（{mat.Note}）→ 去支撑 → 喷砂。");
            r.Notes.Add("配合面、密封面、螺纹孔留 0.3–0.5 mm 余量，打印后 CNC 精加工；打印件精度约 ±0.1–0.2 mm，表面 Ra 约 6–15 µm。");
        }

        /// <summary>
        /// 支撑取不出来：悬垂面在封闭的流道 / 腔体里（从面上往外各个方向看都被零件挡住）。
        /// 每个悬垂区域抽几个三角形，沿法向半球投射 13 条射线，几乎全被挡住就算封闭。
        /// </summary>
        HashSet<int> InternalSupports(DfmSettings s, DfmResult r, HashSet<int> skip, CancellationToken ct)
        {
            var m = Mesh; var inside = new HashSet<int>();
            foreach (var region in Regions(t => (r.Flags[t] & TriFlag.Overhang) != 0 && !skip.Contains(t)))
            {
                ct.ThrowIfCancellationRequested();
                double area = region.Sum(t => m.A[t]);
                if (area < Math.Max(1, 1e-4 * m.Area)) continue;
                if (m.Shells[m.ShellOf[region[0]]].Cavity) continue;   // 封闭空腔另外报
                var sample = region.OrderByDescending(t => m.A[t]).Take(12).ToList();
                int enclosed = sample.Count(t => Enclosed(t));
                if (enclosed * 2 < sample.Count) continue;
                foreach (var t in region) inside.Add(t);
                r.Issues.Add(new DfmIssue
                {
                    Kind = "内部支撑", Flag = TriFlag.Overhang, Severity = Severity.Error, Tris = region, Location = Centroid(region), Number = area,
                    Title = "封闭流道 / 内腔里的悬垂：支撑取不出来",
                    Value = $"面积 {F(area, "0.#")} mm²，从这里往外看四周都被零件挡住",
                    Suggestion = "金属支撑只能用工具从外面去掉，封在流道里的支撑拿不出来（还会堵住流道）。把流道截面改成自支撑形状（泪滴形、菱形，顶部 ≥ 45°），水平流道直径尽量 ≤ 8 mm，或者换摆放方向让流道竖起来。",
                });
            }
            return inside;
        }

        bool Enclosed(int t)
        {
            var m = Mesh; var n = m.N[t];
            n.Perpendiculars(out var e1, out var e2);
            var o = m.C[t] + n * 0.02;
            int blocked = 0, total = 0;
            if (!double.IsInfinity(Bvh.Cast(o, n, t, out _))) blocked++;
            total++;
            foreach (var tilt in new[] { 35.0, 70.0 })
                for (int k = 0; k < 6; k++)
                {
                    double a = k * Math.PI / 3 + (tilt > 50 ? Math.PI / 6 : 0), tr = tilt * Math.PI / 180;
                    var d = n * Math.Cos(tr) + (e1 * Math.Cos(a) + e2 * Math.Sin(a)) * Math.Sin(tr);
                    if (!double.IsInfinity(Bvh.Cast(o, d, t, out _))) blocked++;
                    total++;
                }
            return blocked >= total - 1;
        }

        // ================= 共用 =================

        /// <summary>封闭空腔（网格里被包在实体内部的壳）：粉末 / 树脂出不来。</summary>
        void Cavities(DfmSettings s, DfmResult r)
        {
            var m = Mesh;
            foreach (var sh in m.Shells.Where(x => x.Cavity))
            {
                double vol = -sh.Volume;
                if (vol < 1) continue;
                foreach (var t in sh.Tris) r.Flags[t] |= TriFlag.Cavity;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "封闭空腔", Flag = TriFlag.Cavity, Severity = s.Process == DfmProcess.Sla ? Severity.Warning : Severity.Error, Tris = sh.Tris.ToList(), Location = Centroid(sh.Tris), Number = vol,
                    Title = $"封闭空腔：{F(vol / 1000, "0.##")} cm³",
                    Value = "零件内部有完全封闭的空腔",
                    Suggestion = s.Process == DfmProcess.Sls
                        ? $"空腔里的粉末出不来（零件变重，粉末也浪费）。至少开 2 个排粉孔，{(s.SlsMachine.Mjf ? "Φ3.5 mm" : "Φ5 mm")} 以上，放在空腔的两头便于吹粉；不需要空腔就改成实心，想减重可以改成开放的格栅。"
                        : s.Process == DfmProcess.Slm
                        ? "金属粉末取不出来，空腔里的支撑也去不掉。开 2 个以上 Φ2–3 mm 的排粉孔，空腔顶部做成 ≥ 45° 的自支撑形状（菱形 / 泪滴形）；不需要的话改成实心。"
                        : "未固化的树脂会困在里面（以后会渗出、让零件开裂），打印时还会有吸盘效应把零件从平台上拉下来。开 2 个 Φ3 mm 以上的排液孔（一个进气、一个出液），放在靠近平台的一侧。",
                });
            }
        }

        /// <summary>缝隙：从面上沿外法向，在 limit 以内碰到朝向相反的面（两个面之间只有很窄的缝）。按面对合并。</summary>
        void Gaps(DfmSettings s, DfmResult r, double limit)
        {
            var m = Mesh;
            var gap = new double[m.Tris]; var hitOf = new int[m.Tris];
            Parallel.For(0, m.Tris, t =>
            {
                gap[t] = double.NaN; hitOf[t] = -1;
                var n = m.N[t];
                var dist = Bvh.Cast(m.C[t] + n * 1e-4, n, t, out int h, limit - 0.01);
                if (h < 0 || m.N[h].Dot(n) > -0.5 || dist < 1e-3) return;
                gap[t] = dist; hitOf[t] = h;
            });
            var groups = new Dictionary<(int, int), List<int>>();
            for (int t = 0; t < m.Tris; t++)
            {
                if (double.IsNaN(gap[t])) continue;
                int fa = m.FaceOf[t], fb = m.FaceOf[hitOf[t]];
                var key = fa < fb ? (fa, fb) : (fb, fa);
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<int>();
                l.Add(t);
            }
            foreach (var kv in groups)
            {
                var tris = kv.Value;
                double area = tris.Sum(t => m.A[t]);
                if (area < Math.Max(0.5, 1e-5 * m.Area)) continue;
                double min = tris.Min(t => gap[t]);
                foreach (var t in tris) r.Flags[t] |= TriFlag.Corner;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "缝隙", Flag = TriFlag.Corner, Severity = Severity.Warning, Tris = tris, Location = Centroid(tris), Number = min,
                    Title = $"缝隙太窄：{F(min)} mm",
                    Value = $"两个面之间只有 {F(min)} mm（建议 ≥ {F(limit)} mm），面积 {F(area, "0.#")} mm²",
                    Suggestion = s.Process == DfmProcess.Slm
                        ? $"小于 {F(limit)} mm 的缝里的粉末会被熔在一起，两个面粘死、缝也清不干净。加宽到 {F(limit)} mm 以上；需要配合的面打印后再加工。"
                        : $"小于 {F(limit)} mm 的缝里的粉末会被热量烧结，两边粘在一起（活动件会卡死）、粉也清不出来。间隙加到单边 ≥ {F(limit)} mm；如果是一体打印的铰链 / 卡扣，按 0.5–0.8 mm 留。",
                });
            }
        }

        /// <summary>粉末烧结：大面积的薄平板冷却不均，容易翘曲。按「面 + 背面」合并。</summary>
        void FlatWarp(DfmSettings s, DfmResult r)
        {
            var m = Mesh; var seen = new HashSet<(int, int)>();
            foreach (var f in m.Faces.Where(f => f.Kind == FaceKind.Plane && f.Area > 2000).OrderByDescending(f => f.Area))
            {
                var th = f.Tris.Where(t => !double.IsNaN(_thickness[t])).Select(t => _thickness[t]).OrderBy(x => x).ToList();
                if (th.Count == 0) continue;
                double med = th[th.Count / 2];
                if (med > 3) continue;
                f.Normal.Perpendiculars(out var e1, out var e2);
                var verts = f.Tris.SelectMany(t => new[] { m.T[3 * t], m.T[3 * t + 1], m.T[3 * t + 2] }).Distinct().Select(m.P).ToList();
                double len = Math.Max(verts.Max(p => p.Dot(e1)) - verts.Min(p => p.Dot(e1)), verts.Max(p => p.Dot(e2)) - verts.Min(p => p.Dot(e2)));
                if (len < 80) continue;
                var back = f.Tris.Select(t => _thickHit[t]).Where(h => h >= 0).GroupBy(h => m.FaceOf[h]).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
                var key = f.Id < back ? (f.Id, back) : (back, f.Id);
                if (!seen.Add(key)) continue;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "翘曲", Flag = TriFlag.Thin, Severity = Severity.Warning, Tris = f.Tris.ToList(), Location = f.Centroid, Number = len,
                    Title = $"大面积薄平板：{F(len, "0")} mm 长、{F(med, "0.#")} mm 厚",
                    Value = $"面积 {F(f.Area / 100, "0.#")} cm²，粉末烧结冷却时收缩不均，大薄板容易翘曲",
                    Suggestion = "背面加加强筋（高 3–5 mm、厚 1–1.5 mm、间距 20–30 mm），或者整体加厚到 ≥ 3 mm；也可以把大平面改成略带弧度的曲面。",
                });
            }
        }

        /// <summary>6 个坐标轴方向 + 最大的 12 个平面朝下的方向（摆放 / 开模方向的候选）。</summary>
        List<(Vec3 d, string label)> AxisAndPlaneDirs()
        {
            var dirs = new List<(Vec3 d, string label)>
            {
                (new Vec3(0, 0, 1), "+Z 朝上"), (new Vec3(0, 0, -1), "−Z 朝上"), (new Vec3(0, 1, 0), "+Y 朝上"),
                (new Vec3(0, -1, 0), "−Y 朝上"), (new Vec3(1, 0, 0), "+X 朝上"), (new Vec3(-1, 0, 0), "−X 朝上"),
            };
            foreach (var f in Mesh.Faces.Where(f => f.Kind == FaceKind.Plane).OrderByDescending(f => f.Area).Take(12))
                if (!dirs.Any(x => x.d.Dot(-f.Normal) > 0.9999)) dirs.Add((-f.Normal, $"平面 {f.Id} 朝下"));
            return dirs;
        }

        static bool FitsBox((double w1, double w2, double h) ext, Printer p)
        {
            var bed = new[] { p.X, p.Y }.OrderBy(x => x).ToArray();
            var xy = new[] { ext.w1, ext.w2 }.OrderBy(x => x).ToArray();
            return xy[0] <= bed[0] && xy[1] <= bed[1] && ext.h <= p.Z;
        }

        /// <summary>按顺序取彼此相差 minDeg 以上的前 n 个方向。</summary>
        static List<Orientation> PickDistinct(IEnumerable<Orientation> sorted, int n, double minDeg, bool axisOnly = false)
        {
            var picked = new List<Orientation>();
            double c = Math.Cos(minDeg * Math.PI / 180);
            foreach (var o in sorted)
            {
                if (picked.Any(p => axisOnly ? Math.Abs(p.Up.Dot(o.Up)) > c : p.Up.Dot(o.Up) > c)) continue;
                picked.Add(o);
                if (picked.Count == n) break;
            }
            return picked;
        }
    }
}
