using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SwKit.Core.Urdf;

namespace SwKit.Core.Dfm
{
    /// <summary>
    /// 注塑：开模方向按可见性选（沿开模方向两侧都看不到的面就是倒扣，倒扣面积最小的方向最好，Chen 1993）；
    /// 侧壁拔模角 = 面法向和开模方向的夹角的余角；壁厚均匀性按形状直径函数 + 内切球修正；尖内角按凹棱。
    /// </summary>
    public sealed partial class DfmAnalyzer
    {
        void AnalyzeMold(DfmSettings s, Vec3? pull, DfmResult r, Action<string> log, CancellationToken ct)
        {
            var m = Mesh; var mat = s.MoldMaterial;
            log?.Invoke("正在找开模方向");
            double cell = Math.Max(m.Diag / 120, 0.2);
            var axes = new List<(Vec3 d, string label)> { (new Vec3(0, 0, 1), "Z 向开模"), (new Vec3(0, 1, 0), "Y 向开模"), (new Vec3(1, 0, 0), "X 向开模") };
            double c3 = Math.Cos(3 * Math.PI / 180);
            foreach (var f in m.Faces.Where(f => f.Kind == FaceKind.Plane).OrderByDescending(f => f.Area).Take(12))
            {
                var d = f.Normal.Canonical();
                if (!axes.Any(a => Math.Abs(a.d.Dot(d)) > c3)) axes.Add((d, $"沿平面 {f.Id} 的法向开模"));
            }
            foreach (var v in new[] { new Vec3(1, 1, 0), new Vec3(1, -1, 0), new Vec3(1, 0, 1), new Vec3(1, 0, -1), new Vec3(0, 1, 1), new Vec3(0, 1, -1),
                                      new Vec3(1, 1, 1), new Vec3(1, 1, -1), new Vec3(1, -1, 1), new Vec3(-1, 1, 1) })
            {
                var d = v.Normalized();
                if (!axes.Any(a => Math.Abs(a.d.Dot(d)) > c3)) axes.Add((d, $"斜向 {Vec(d)} 开模"));
            }
            var cands = new List<Orientation>();
            foreach (var (d, label) in axes)
            {
                ct.ThrowIfCancellationRequested();
                var vp = MoldVisibility(d, cell, true); var vm = MoldVisibility(-d, cell, true);
                double und = 0;
                for (int t = 0; t < m.Tris; t++) if (!vp[t] && !vm[t]) und += m.A[t];
                double depth = m.Extent(d, out _, out _);
                cands.Add(new Orientation { Up = d, SupportArea = und, Height = depth, Label = label, Score = und / m.Area + 0.02 * depth / m.Diag });
            }
            r.Candidates = PickDistinct(cands.OrderBy(c => c.Score), 3, 10, true);
            var dir = pull.HasValue ? pull.Value.Normalized() : r.Candidates[0].Up;
            string dlabel = pull.HasValue ? $"{AxisName(dir, false)} 向开模（手动指定）" : r.Candidates[0].Label;

            log?.Invoke("正在检查倒扣和拔模");
            var vP = MoldVisibility(dir, cell, false); var vM = MoldVisibility(-dir, cell, false);
            var under = new bool[m.Tris];
            for (int t = 0; t < m.Tris; t++) under[t] = !vP[t] && !vM[t];
            double depthAll = m.Extent(dir, out _, out _);
            r.Orientation = new Orientation { Up = dir, Label = dlabel, Height = depthAll };
            r.ViewUp = dir; r.Arrows = new List<Vec3> { dir, -dir };

            // ---- 倒扣 ----
            dir.Perpendiculars(out var e1, out var e2);
            var sides = Enumerable.Range(0, 8).Select(k => e1 * Math.Cos(k * Math.PI / 4) + e2 * Math.Sin(k * Math.PI / 4)).ToList();
            int slides = 0, lifters = 0; double underA = 0;
            foreach (var region in Regions(t => under[t]))
            {
                double area = region.Sum(t => m.A[t]);
                if (area < Math.Max(1, 2e-4 * m.Area)) continue;
                underA += area;
                foreach (var t in region) r.Flags[t] |= TriFlag.Undercut;
                Vec3 sd = Vec3.Zero; double bestF = 0;
                foreach (var a in sides)
                {
                    double v = region.Sum(t => MoldVisible(t, a, cell, false) ? m.A[t] : 0) / area;
                    if (v > bestF + 1e-9) { bestF = v; sd = a; }
                }
                bool slide = bestF >= 0.8;
                if (slide) slides++; else lifters++;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "倒扣", Flag = TriFlag.Undercut, Severity = Severity.Warning, Tris = region, Location = Centroid(region), Number = area,
                    Title = slide ? $"倒扣：要从 {AxisName(sd, true)} 侧抽芯（滑块）" : "倒扣：要斜顶 / 内抽芯",
                    Value = $"面积 {F(area, "0.#")} mm²，沿开模方向两侧都脱不出来",
                    Suggestion = (slide ? "模具要加一个滑块（侧向抽芯），模具费约 +3000–8000 元，周期也会变长。" : "零件内侧的倒扣要用斜顶或内抽芯，模具更贵、更容易出问题（约 +5000–10000 元）。") +
                                 "能改就改：侧孔改成从顶面开的缺口，卡扣下方开通孔做成「穿透式」卡扣，内凹的台阶改成斜面；或者把开模方向换成下面推荐的其他方向试试。",
                });
            }

            // ---- 拔模 ----
            double sinMin = Math.Sin(Math.Max(0, s.MinDraft - 0.05) * Math.PI / 180);
            var bad = new bool[m.Tris]; var draftSin = new double[m.Tris];
            for (int t = 0; t < m.Tris; t++) { draftSin[t] = DraftSin(t, dir); bad[t] = !under[t] && draftSin[t] < sinMin; }
            var draftIssues = new List<DfmIssue>();
            foreach (var region in Regions(t => bad[t]))
            {
                double area = region.Sum(t => m.A[t]);
                if (area < Math.Max(0.5, 1e-4 * m.Area)) continue;
                var hs = region.SelectMany(t => new[] { m.T[3 * t], m.T[3 * t + 1], m.T[3 * t + 2] }).Distinct().Select(v => m.P(v).Dot(dir)).ToList();
                double depth = hs.Max() - hs.Min();
                if (depth < 0.2) continue;
                double mean = region.Sum(t => m.A[t] * Math.Asin(Math.Min(1, draftSin[t]))) / area * 180 / Math.PI;
                foreach (var t in region) r.Flags[t] |= TriFlag.Draft;
                double need = Math.Max(1, s.MinDraft);
                draftIssues.Add(new DfmIssue
                {
                    Kind = "拔模", Flag = TriFlag.Draft, Severity = mean < 0.5 && depth >= 1 ? Severity.Error : Severity.Warning, Tris = region, Location = Centroid(region), Number = mean,
                    Title = mean < 0.05 ? $"侧壁没有拔模（深 {F(depth, "0.#")} mm）" : $"拔模角只有 {F(mean, "0.0")}°（深 {F(depth, "0.#")} mm）",
                    Value = $"面积 {F(area, "0.#")} mm²，要求 ≥ {F(s.MinDraft, "0.#")}°",
                    Suggestion = $"侧壁加 ≥ {F(need, "0.#")}° 的拔模（深的面、咬花面加到 2–3°，咬花每 0.025 mm 纹深再加 1°），不然顶出时会拉伤表面、卡模、变形。SolidWorks：插入 → 特征 → 拔模，中性面选分型面。深 {F(depth, "0.#")} mm 时 1° 拔模单边只差 {F(depth * Math.Tan(Math.PI / 180), "0.##")} mm。",
                });
            }
            AddMerged(r, draftIssues.OrderByDescending(x => x.Tris.Sum(t => m.A[t])).ToList(), 8, n => $"还有 {n} 处侧壁拔模不足");

            // ---- 壁厚 ----
            var ths = Enumerable.Range(0, m.Tris).Where(t => !double.IsNaN(_thickness[t])).Select(t => (th: _thickness[t], a: m.A[t])).OrderBy(x => x.th).ToList();
            double tNom = double.NaN;
            if (ths.Count > 0) { double half = ths.Sum(x => x.a) / 2, acc = 0; foreach (var x in ths) { acc += x.a; if (acc >= half) { tNom = x.th; break; } } }
            double thickMax = double.NaN;
            if (!double.IsNaN(tNom))
            {
                if (tNom > mat.MaxWall * 1.15)
                {
                    var tris = Enumerable.Range(0, m.Tris).Where(t => !double.IsNaN(_thickness[t]) && _thickness[t] > mat.MaxWall).ToList();
                    foreach (var t in tris) r.Flags[t] |= TriFlag.Thick;
                    thickMax = tNom;
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "厚壁", Flag = TriFlag.Thick, Severity = Severity.Error, Tris = tris, Location = (m.Min + m.Max) / 2, Number = tNom,
                        Title = $"整体太厚：名义壁厚 {F(tNom, "0.#")} mm",
                        Value = $"{mat.Name} 推荐壁厚 {F(mat.MinWall)}–{F(mat.MaxWall)} mm",
                        Suggestion = $"注塑件要做成均匀的薄壁：实心部分抽壳到 {F(Math.Min(mat.MaxWall, Math.Max(mat.MinWall * 1.5, 2)), "0.#")} mm 左右，用加强筋（厚度 0.5–0.6 倍壁厚）补强度。太厚会缩痕、内部气孔，冷却时间随壁厚平方增加（成本高）。",
                    });
                }
                else
                {
                    double lim = Math.Max(1.25 * tNom, Math.Min(1.5 * tNom, mat.MaxWall));
                    var ins = new double[m.Tris];
                    var thick = new bool[m.Tris];
                    for (int t = 0; t < m.Tris; t++)
                    {
                        if (double.IsNaN(_thickness[t]) || _thickness[t] <= lim) continue;
                        ins[t] = Inscribed(t);
                        thick[t] = ins[t] > lim;
                    }
                    // 同一个厚块的几个侧面各自连成一片：形心相距不到厚度的合成一处
                    foreach (var region in MergeNear(Regions(t => thick[t]), g => g.Max(t => ins[t])))
                    {
                        double area = region.Sum(t => m.A[t]);
                        if (area < Math.Max(1, 2e-4 * m.Area)) continue;
                        double mx = region.Max(t => ins[t]);
                        thickMax = double.IsNaN(thickMax) ? mx : Math.Max(thickMax, mx);
                        foreach (var t in region) { r.Flags[t] |= TriFlag.Thick; int h = _thickHit[t]; if (h >= 0) r.Flags[h] |= TriFlag.Thick; }
                        r.Issues.Add(new DfmIssue
                        {
                            Kind = "厚壁", Flag = TriFlag.Thick, Severity = mx > 2 * tNom && mx > mat.MaxWall ? Severity.Error : Severity.Warning, Tris = region, Location = Centroid(region), Number = mx,
                            Title = $"局部太厚：约 {F(mx, "0.#")} mm（名义壁厚 {F(tNom, "0.#")} mm）",
                            Value = $"超过名义壁厚的 1.5 倍或 {mat.Name} 的推荐上限 {F(mat.MaxWall)} mm，面积 {F(area, "0.#")} mm²",
                            Suggestion = $"厚的地方冷却慢，表面会缩痕、内部有气孔，零件也容易变形。把这里掏空（留 {F(tNom, "0.#")} mm 左右的均匀壁厚），需要强度就加筋（筋厚 ≤ 0.6 倍壁厚 ≈ {F(0.6 * tNom, "0.#")} mm）；螺丝柱做成空心柱，外径约 2 倍孔径，用筋连到侧壁。",
                        });
                    }
                }
                // 流长比
                double flow = m.Diag / 2 / tNom;
                if (flow > mat.FlowRatio)
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "流长比", Severity = Severity.Warning, Location = (m.Min + m.Max) / 2, Number = flow,
                        Title = $"流长比偏大：约 {F(flow, "0")}", Value = $"从中心进胶流到最远处约 {F(m.Diag / 2, "0")} mm，壁厚 {F(tNom, "0.#")} mm；{mat.Name} 一般 ≤ {F(mat.FlowRatio, "0")}",
                        Suggestion = "可能充不满、熔接痕明显。加厚壁厚、多点进胶（热流道），或者换流动性更好的牌号；最好让模具厂做一次模流分析。",
                    });
            }

            // ---- 尖内角 ----
            var cornerIssues = new List<DfmIssue>();
            double tr = double.IsNaN(tNom) ? 1 : tNom;
            foreach (var chain in Chains(SharpEdges(30, true)))
            {
                var region = chain.SelectMany(g => g.Tris).Where(t => !under[t]).Distinct().ToList();
                double len = chain.Sum(g => g.Length);
                if (region.Count == 0 || len < 0.5) continue;
                foreach (var t in region) r.Flags[t] |= TriFlag.Corner;
                var mid = chain.Aggregate(Vec3.Zero, (acc, g) => acc + g.Mid * g.Length) / len;
                cornerIssues.Add(new DfmIssue
                {
                    Kind = "尖内角", Flag = TriFlag.Corner, Severity = Severity.Warning, Tris = region, Location = mid, Number = len,
                    Title = $"尖内角（没有圆角），长 {F(len, "0")} mm", Value = $"内角 R0；建议内 R ≥ 0.5t = {F(0.5 * tr, "0.#")} mm",
                    Suggestion = $"尖内角应力集中、容易开裂，塑料流动也不顺。内角倒 R{F(Math.Max(0.5, 0.5 * tr), "0.#")} 以上（0.5–0.6 倍壁厚），对应的外角倒 R{F(Math.Max(0.5, 0.5 * tr) + tr, "0.#")}（内 R + 壁厚），保持壁厚均匀。",
                });
            }
            AddMerged(r, cornerIssues.OrderByDescending(x => x.Number).ToList(), 5, n => $"还有 {n} 处尖内角");

            // ---- 估算 ----
            var e = Extents(dir);
            double size = Math.Max(e.w1, e.w2);
            double ap = 0;
            for (int t = 0; t < m.Tris; t++) { double c = m.N[t].Dot(dir); if (c > 0) ap += m.A[t] * c; }
            ap /= 100;   // cm²
            double clamp = ap * mat.ClampP * 1.2;
            double rate = clamp <= 80 ? 60 : clamp <= 160 ? 80 : clamp <= 300 ? 110 : clamp <= 600 ? 160 : 250;
            // 冷却时间由最厚的地方决定（厚壁按 0.8 倍算，最多 8 mm）
            double tn = double.IsNaN(tNom) ? 2 : tNom;
            double tc = Math.Min(double.IsNaN(thickMax) ? tn : Math.Max(tn, thickMax * 0.8), 8);
            double cool = mat.CoolK * tc * tc, cycle = cool + 8 + e.h / 25, cycleNom = mat.CoolK * tn * tn + 8 + e.h / 25;
            double grams = m.Volume / 1000 * mat.Density;
            double matCost = grams * 1.1 / 1000 * mat.PricePerKg, machine = cycle / 3600 * rate;
            double lo = size <= 80 ? 6000 : size <= 200 ? 12000 : size <= 400 ? 30000 : 80000;
            double hi = size <= 80 ? 12000 : size <= 200 ? 30000 : size <= 400 ? 80000 : 200000;
            if (e.h > 100) { lo *= 1.3; hi *= 1.3; }
            lo += slides * 3000 + lifters * 5000; hi += slides * 8000 + lifters * 10000;
            int qty = Math.Max(1, s.MoldQuantity);
            double unit = matCost + machine + (lo + hi) / 2 / qty;
            r.Metrics.Add(new Metric("开模方向", dlabel, $"倒扣面积 {F(underA, "0")} mm²；候选方向里倒扣最少、深度最浅的"));
            r.Metrics.Add(new Metric("倒扣", slides + lifters == 0 ? "没有" : $"{slides + lifters} 处（滑块 {slides}、斜顶 {lifters}）"));
            if (!double.IsNaN(tNom)) r.Metrics.Add(new Metric("名义壁厚", $"{F(tNom, "0.##")} mm", $"面积加权的壁厚中位数；{mat.Name} 推荐 {F(mat.MinWall)}–{F(mat.MaxWall)} mm"));
            r.Metrics.Add(new Metric("投影面积", $"{F(ap, "0.#")} cm²", "沿开模方向的投影面积，决定锁模力"));
            r.Metrics.Add(new Metric("锁模力（估算）", $"约 {F(clamp, "0")} 吨", $"投影面积 × {F(mat.ClampP)} t/cm² × 1.2 余量；机时按 {F(rate, "0")} 元/小时"));
            r.Metrics.Add(new Metric("成型周期（估算）", $"约 {F(cycle, "0")} 秒" + (cycle > cycleNom + 3 ? $"（厚壁改掉后约 {F(cycleNom, "0")} 秒）" : ""),
                $"冷却 {F(mat.CoolK, "0.#")} × {F(tc, "0.#")}²（最厚处）≈ {F(cool, "0")} 秒 + 注射、开合模、顶出约 {F(8 + e.h / 25, "0")} 秒"));
            r.Metrics.Add(new Metric("单件重量", $"{F(grams, "0.#")} g", $"{F(m.Volume / 1000, "0.##")} cm³ × {F(mat.Density)} g/cm³（{mat.Name}）"));
            r.Metrics.Add(new Metric("单件料费 + 机时（估算）", $"{F(matCost + machine, "0.##")} 元", $"料 {F(matCost, "0.##")} 元（含 10% 流道和损耗，{F(mat.PricePerKg, "0")} 元/kg）+ 机时 {F(machine, "0.##")} 元（一模一穴）"));
            r.Metrics.Add(new Metric("模具费（估算）", $"约 {F(lo / 10000, "0.#")}–{F(hi / 10000, "0.#")} 万元", "按零件尺寸、深度和滑块 / 斜顶数估算（P20 / 718H 钢模，寿命 10–30 万次）；铝模或简易模可以便宜 30–50%"));
            r.Metrics.Add(new Metric($"{qty} 件时单价（估算）", $"{F(unit, "0.##")} 元", "料费 + 机时 + 模具费中值 ÷ 数量"));
            r.Notes.Add($"{mat.Name} 收缩率约 {F(mat.Shrink, "0.#")}%（模具按这个放大）；配合尺寸在图纸上标公差，外观面、浇口和顶针位置要和模具厂确认。");
            r.Notes.Add("模具厂报价前一般会做模流分析；小批量（几百件以内）可以先考虑 3D 打印或硅胶复模。");
        }

        /// <summary>
        /// 注塑用的可见性：和 CNC 一样按面积采样，但射线擦着平行的面走（碰到的面几乎和射线平行）时继续往前，
        /// 不然没拔模的侧壁、短圆柱（三角形法向会偏几度）会被自己旁边的三角形挡住，误判成倒扣。
        /// </summary>
        bool[] MoldVisibility(Vec3 a, double cell, bool fast)
        {
            var v = new bool[Mesh.Tris];
            System.Threading.Tasks.Parallel.For(0, Mesh.Tris, t => v[t] = MoldVisible(t, a, cell, fast));
            return v;
        }

        bool MoldVisible(int t, Vec3 a, double cell, bool fast)
        {
            var m = Mesh;
            var n = SurfaceNormal(m.FaceById[m.FaceOf[t]], m.C[t], m.N[t]);   // 圆柱用解析法向：短圆柱的三角形法向会偏
            if (n.Dot(a) < -0.02) return false;
            if (fast) return Escapes(m.C[t] + n * 0.02 + a * 1e-4, a, t);
            var pa = m.P(m.T[3 * t]); var pb = m.P(m.T[3 * t + 1]); var pc = m.P(m.T[3 * t + 2]);
            int k = Math.Min(16, Math.Max(3, (int)Math.Ceiling(m.A[t] / (cell * cell))));
            int ok = 0;
            for (int i = 0; i < k; i++)
            {
                double r1 = Frac((i + 0.5) * 0.7548776662), r2 = Frac((i + 0.5) * 0.5698402910);
                if (r1 + r2 > 1) { r1 = 1 - r1; r2 = 1 - r2; }
                var p = pa + (pb - pa) * r1 + (pc - pa) * r2;
                if (Escapes(p + n * 0.02 + a * 1e-4, a, t)) ok++;
                if (ok * 2 >= k) break;
            }
            return ok * 2 >= k;
        }

        bool Escapes(Vec3 o, Vec3 a, int skip)
        {
            var m = Mesh;
            for (int i = 0; i < 8; i++)
            {
                double d = Bvh.Cast(o, a, skip, out int h);
                if (h < 0) return true;
                if (Math.Abs(m.N[h].Dot(a)) > 0.2) return false;   // 正面挡住
                o = o + a * (d + 1e-4); skip = h;                   // 擦边：继续
            }
            return false;
        }

        /// <summary>侧壁和开模方向夹角的正弦：平面、和开模方向平行的圆柱用解析值（网格三角形的法向会偏），其他按三角形。</summary>
        double DraftSin(int t, Vec3 d)
        {
            var f = Mesh.FaceById[Mesh.FaceOf[t]];
            if (f.Kind == FaceKind.Plane) return Math.Abs(f.Normal.Dot(d));
            if (f.Kind == FaceKind.Cylinder && Math.Abs(f.Axis.Dot(d)) > 0.999) return 0;
            return Math.Abs(Mesh.N[t].Dot(d));
        }

        /// <summary>内切球修正后的壁厚：在内法向射线的中点往四周看，碰到别的面就说明这里没有那么厚（加强筋根部之类）。</summary>
        double Inscribed(int t)
        {
            var m = Mesh; double th = _thickness[t];
            if (double.IsNaN(th)) return double.NaN;
            var n = m.N[t]; var c = m.C[t] - n * (th / 2);
            n.Perpendiculars(out var e1, out var e2);
            double rmin = th / 2;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                var d = e1 * Math.Cos(a) + e2 * Math.Sin(a);
                double dd = Bvh.Cast(c, d, -1, out int h, rmin);
                if (h >= 0) rmin = Math.Min(rmin, dd);
            }
            return 2 * rmin;
        }

        /// <summary>方向的名字：坐标轴给「+X」（signed）或「X」，其他给数值。</summary>
        static string AxisName(Vec3 a, bool signed)
        {
            foreach (var (v, n) in new[] { (new Vec3(1, 0, 0), "X"), (new Vec3(0, 1, 0), "Y"), (new Vec3(0, 0, 1), "Z") })
            {
                double d = a.Dot(v);
                if (Math.Abs(d) > 0.999) return signed ? (d > 0 ? "+" : "−") + n : n;
            }
            return Vec(signed ? a : a.Canonical());
        }
    }
}
