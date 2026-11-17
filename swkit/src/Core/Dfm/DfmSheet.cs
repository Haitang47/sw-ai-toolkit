using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwKit.Core.Urdf;

namespace SwKit.Core.Dfm
{
    /// <summary>
    /// 板材类工艺：钣金（激光切割 + 折弯）和板材切割（激光 / 水刀 / CNC 雕刻切 2D 板）。
    /// 钣金：按壁厚分布找板厚 t，「表皮」（厚度 ≈ t 的面）旁边的面是切割边；隔着材料正对表皮但距离不是 t 的面（凸台顶面、沉槽底面）、离表皮很远的部分是钣金做不出来的；
    /// 折弯 = 同轴、半径差 t 的一对凹 / 凸圆柱面。板材切割：面积最大的一组平行平面是板面，垂直贯穿板厚的面是切割轮廓，其余的面都要另外铣。
    /// </summary>
    public sealed partial class DfmAnalyzer
    {
        // ================= 钣金 =================

        sealed class Bend
        {
            public Vec3 Axis, AxisPoint, Centroid;
            public double Ri, Angle, Length, T0, T1;
            public bool HasOuter;
            public List<int> Tris = new List<int>(), InnerTris = new List<int>();
            public HashSet<int> Faces = new HashSet<int>();
            public List<Flange> Flanges = new List<Flange>();
        }
        sealed class Flange { public DfmFace Face; public Vec3 U; public double U0, Straight; }

        void AnalyzeSheet(DfmSettings s, DfmResult r, Action<string> log, CancellationToken ct)
        {
            var m = Mesh; var mat = s.SheetMaterial;
            log?.Invoke("正在识别板厚和折弯");
            double t0 = DominantThickness(null);
            if (double.IsNaN(t0))
            {
                r.Issues.Add(new DfmIssue { Kind = "板厚", Severity = Severity.Error, Location = (m.Min + m.Max) / 2, Title = "测不出板厚", Value = "壁厚测量没有结果（网格可能不封闭）", Suggestion = "用 SolidWorks 重新导出 STEP（实体，不要曲面），再检查。" });
                return;
            }
            double tol = Math.Max(0.05, 0.06 * t0);
            r.Thickness = t0;
            // 表皮：中位厚度 ≈ t0 的面
            var skinFace = new HashSet<int>();
            foreach (var f in m.Faces)
            {
                double med = FaceMedianThickness(f, out double valid);
                if (!double.IsNaN(med) && valid > 0.2 && Math.Abs(med - t0) <= tol) skinFace.Add(f.Id);
            }
            var skin = new bool[m.Tris];
            for (int t = 0; t < m.Tris; t++) skin[t] = skinFace.Contains(m.FaceOf[t]);
            // 离板面的距离：离得很远（超过 2 倍板厚）的是实心块
            var dist = DistanceFrom(skin, 3 * t0);
            // 和板面相邻的面（切割边）不算「正对板面的厚实部分」：切割边的射线有时会斜着打到折弯的外表面
            var nextToSkin = new HashSet<int>();
            for (int t = 0; t < m.Tris; t++) if (!skin[t]) for (int k = 0; k < 3; k++) { int o = m.Adj[3 * t + k]; if (o >= 0 && skin[o]) { nextToSkin.Add(m.FaceOf[t]); break; } }
            var feature = new bool[m.Tris]; var edge = new bool[m.Tris];
            double aSkin = 0, aFeat = 0, aEdge = 0;
            for (int t = 0; t < m.Tris; t++)
            {
                double th = _thickness[t];
                if (skin[t])
                {
                    // 板面上厚度不对：正对着凸台顶面 / 沉槽底面；对面是沉头孔、倒角这类切割边上的斜面的不算（另外按斜边报）
                    int hs = _thickHit[t];
                    feature[t] = !double.IsNaN(th) && (th > 1.3 * t0 + tol || th < 0.7 * t0 - tol) && (hs < 0 || skin[hs] || !nextToSkin.Contains(m.FaceOf[hs]));
                    if (!feature[t]) aSkin += m.A[t];
                }
                else
                {
                    // 厚实部分：① 和板面平行、隔着材料正对着板面（凸台顶面、沉槽底面），但距离不是板厚；② 离板面很远（实心块）
                    double dv = Math.Max(dist[m.T[3 * t]], Math.Max(dist[m.T[3 * t + 1]], dist[m.T[3 * t + 2]]));
                    int h = _thickHit[t];
                    bool facing = h >= 0 && skin[h] && !nextToSkin.Contains(m.FaceOf[t]) && m.N[t].Dot(m.N[h]) < -0.9 && !double.IsNaN(th) && Math.Abs(th - t0) > tol;
                    feature[t] = facing || dv > 2 * t0 + tol;
                    edge[t] = !feature[t];
                    if (edge[t]) aEdge += m.A[t];
                }
                if (feature[t]) aFeat += m.A[t];
            }
            double rho = m.Volume / Math.Max(t0 * aSkin / 2, 1e-9);
            // 凸台的顶面和它正下方的板面是两片，相距不到厚度的合成一处
            var featRegions = MergeNear(Regions(t => feature[t]), g => g.Where(t => !double.IsNaN(_thickness[t])).Select(t => _thickness[t]).DefaultIfEmpty(t0).Max())
                              .Where(g => g.Sum(t => m.A[t]) > Math.Max(1, 2e-4 * m.Area)).ToList();
            bool notSheet = aSkin < 0.55 * m.Area || rho > 1.25 || aFeat > 0.12 * m.Area;
            double flat = aSkin / 2, mass = m.Volume / 1000 * mat.Density;
            r.Metrics.Add(new Metric("板厚", $"{F(t0)} mm", $"按壁厚分布自动识别；厚度 ≈ {F(t0)} mm 的部分占表面 {F(aSkin / m.Area * 100, "0")}%"));
            if (notSheet)
            {
                var tris = featRegions.SelectMany(g => g).ToList();
                foreach (var t in tris) r.Flags[t] |= TriFlag.Undercut;
                double maxTh = tris.Where(t => !double.IsNaN(_thickness[t])).Select(t => _thickness[t]).DefaultIfEmpty(t0).Max();
                r.Issues.Add(new DfmIssue
                {
                    Kind = "非钣金", Flag = TriFlag.Undercut, Severity = Severity.Error, Tris = tris, Location = tris.Count > 0 ? Centroid(tris) : (m.Min + m.Max) / 2, Number = rho,
                    Title = "不是等厚的板，不能直接做成钣金",
                    Value = $"按 {F(t0)} mm 板厚算，等厚部分只占表面 {F(aSkin / m.Area * 100, "0")}%，体积是同样面积板材的 {F(rho, "0.0")} 倍" + (tris.Count > 0 ? $"；紫色部分厚到约 {F(maxTh, "0.#")} mm" : ""),
                    Suggestion = "钣金只能做等厚的板（激光切割外形和孔，再用折弯机折边）。用 SolidWorks 的钣金特征（基体法兰、边线法兰、斜接法兰）重新建模：厚的部分改成折边或加强筋，螺柱、螺母座用压铆螺柱 / 压铆螺母；整体是块状的零件请改选 CNC、3D 打印或注塑。",
                });
                r.Metrics.Add(new Metric("重量", $"{F(mass, "0.#")} g", $"体积 {F(m.Volume / 1000, "0.##")} cm³ × {F(mat.Density)} g/cm³"));
                r.ViewUp = BaseNormal(skinFace);
                return;
            }
            foreach (var g in featRegions)
            {
                foreach (var t in g) r.Flags[t] |= TriFlag.Undercut;
                var ths = g.Where(t => !double.IsNaN(_thickness[t])).Select(t => _thickness[t]).ToList();
                bool thin = ths.Count > 0 && ths.Average() < t0;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "局部不等厚", Flag = TriFlag.Undercut, Severity = Severity.Warning, Tris = g, Location = Centroid(g), Number = g.Sum(t => m.A[t]),
                    Title = thin ? "局部变薄（沉槽 / 台阶）" : "局部加厚（凸台 / 实心块）",
                    Value = thin ? $"这里比 {F(t0)} mm 板厚薄（约 {F(ths.Min(), "0.##")} mm）" : $"这里比 {F(t0)} mm 板厚厚（最厚约 {F(ths.DefaultIfEmpty(t0).Max(), "0.#")} mm）",
                    Suggestion = thin
                        ? "钣金件上的沉槽、减薄要另外铣削或冲压，成本高。改成通孔 / 通槽；需要沉头就用沉头铆钉、压铆螺钉，或者改成冲压沉孔（和加工厂确认）。"
                        : "钣金件上做不出凸台和加厚。螺柱、螺母座改成压铆螺柱 / 压铆螺母（PEM），加强用折边、冲压加强筋或翻边；必须加厚的地方做成单独零件焊接 / 铆接。",
                });
            }
            r.ViewUp = BaseNormal(skinFace);

            // ---- 折弯 ----
            var bends = FindBends(f => skinFace.Contains(f.Id), t0, tol);
            var bendTri = new HashSet<int>();
            foreach (var b in bends) foreach (var t in b.Tris) { bendTri.Add(t); r.Flags[t] |= TriFlag.Bend; }
            foreach (var b in bends)
            {
                double ri = b.Ri, k = ri / t0;
                string head = $"{F(b.Angle, "0")}° 折弯 R{F(ri)}";
                if (k < mat.RecR - 0.02)
                {
                    bool crack = k < mat.MinR - 0.02;
                    double rec = Math.Ceiling(mat.RecR * t0 * 2) / 2;
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "折弯半径", Flag = TriFlag.Bend, Severity = crack ? Severity.Error : Severity.Warning, Tris = b.Tris, Location = b.Centroid, Number = ri,
                        Title = $"{head}：内半径太小",
                        Value = $"内 R = {F(ri)} mm = {F(k, "0.##")}t（{mat.Name} 建议 ≥ {F(mat.RecR, "0.#")}t = {F(mat.RecR * t0, "0.#")} mm" + (crack ? $"，小于 {F(mat.MinR, "0.#")}t 外侧容易开裂）" : "）"),
                        Suggestion = $"把折弯内半径改到 R{F(rec, "0.#")} 以上（SolidWorks 钣金参数里改「折弯半径」）。" + (mat.Note != null ? mat.Note + "。" : "") + "半径太小时折弯外侧拉伸过大会开裂，回弹也难控制；加工厂一般按自己的上模 R 折，和模型不一致时展开尺寸会差。",
                    });
                }
                else if (ri > Math.Max(10 * t0, 25))
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "大圆弧", Flag = TriFlag.Bend, Severity = Severity.Info, Tris = b.Tris, Location = b.Centroid, Number = ri,
                        Title = $"{head}：大圆弧折弯", Value = $"内 R = {F(ri, "0.#")} mm，普通折弯机的上模 R 一般只有 0.2–3 mm",
                        Suggestion = "大圆弧要用大 R 上模、滚圆机，或者分很多次小角度折弯（表面会有棱）；回弹大、角度精度差。不是外观需要的话改成 R ≈ t 的普通折弯。",
                    });
                // 折边长度
                double need = Math.Max(4 * t0, 3 * t0 + ri);           // 外尺寸
                double needStraight = need - ri - t0;
                foreach (var fl in b.Flanges)
                {
                    if (fl.Straight >= needStraight - 0.05) continue;
                    double outDim = fl.Straight + ri + t0;
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "折边太短", Flag = TriFlag.Bend, Severity = fl.Straight < 0.5 * needStraight ? Severity.Error : Severity.Warning,
                        Tris = fl.Face.Tris.ToList(), Location = fl.Face.Centroid, Number = outDim,
                        Title = $"折边太短：外尺寸约 {F(outDim, "0.#")} mm",
                        Value = $"直边 {F(fl.Straight, "0.#")} mm（从折弯圆角结束处量），外尺寸 ≈ 直边 + R + t = {F(outDim, "0.#")} mm；{F(t0)} mm 板至少要 {F(need, "0.#")} mm（max(4t, 3t + R)）",
                        Suggestion = $"折弯时板要搭在下模 V 槽的两边，太短的边会滑进槽里、折不出来或折歪。把这条边加长到外尺寸 ≥ {F(Math.Ceiling(need * 2) / 2, "0.#")} mm；实在不能加长，可以先折长边再切掉（多一道工序），或者问加工厂有没有更窄的 V 槽下模。",
                    });
                }
                // 止裂槽：折弯没有贯穿到板边（两侧的板比折弯宽）
                bool inside = false;
                foreach (var fl in b.Flanges)
                {
                    var ax = FaceVerts(fl.Face).Select(v => m.P(v).Dot(b.Axis)).ToList();
                    if (ax.Min() < b.T0 - t0 || ax.Max() > b.T1 + t0) inside = true;
                }
                if (inside)
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "止裂槽", Flag = TriFlag.Bend, Severity = Severity.Info, Tris = b.Tris, Location = b.Centroid, Number = b.Length,
                        Title = $"{head}：折弯端部在板内", Value = $"折弯长 {F(b.Length, "0.#")} mm，旁边的板比折弯宽，折弯线两端没有到板边",
                        Suggestion = $"确认折弯两端开了止裂槽（宽 ≥ t = {F(t0)} mm，深度超过折弯区 ≥ R + t = {F(ri + t0, "0.#")} mm），否则折弯时端部会撕裂、鼓包。SolidWorks 钣金的「自动切释放槽」可以直接生成。",
                    });
            }
            // 尖角折弯：两块表皮在凹棱上直接相交（没有内圆角）
            var sharp = SharpEdges(5, true).Where(g => skinFace.Contains(g.FaceA) && skinFace.Contains(g.FaceB) &&
                                                      m.FaceById[g.FaceA].Kind == FaceKind.Plane && m.FaceById[g.FaceB].Kind == FaceKind.Plane && g.Length > t0).ToList();
            foreach (var g in sharp)
            {
                foreach (var t in g.Tris) { r.Flags[t] |= TriFlag.Bend; bendTri.Add(t); }
                r.Issues.Add(new DfmIssue
                {
                    Kind = "尖角折弯", Flag = TriFlag.Bend, Severity = Severity.Warning, Tris = g.Tris, Location = g.Mid, Number = g.Length,
                    Title = $"{F(g.Angle, "0")}° 折弯没有内圆角（R0）",
                    Value = $"两块板在 {F(g.Length, "0.#")} mm 长的棱边上直接相交",
                    Suggestion = $"实际折弯一定有内圆角（约等于板厚，或下模 V 槽宽的 1/6）。模型画成尖角，展开尺寸会不准、和别的零件配合可能干涉。在 SolidWorks 钣金里把折弯半径设成 {F(Math.Max(mat.RecR * t0, 0.5), "0.#")} mm，或者用钣金特征重建。",
                });
            }
            // 外侧尖角：内侧是圆角（不是板面），两边连着两块不平行的板面，外侧却没有对应的圆角 —— 实体拉伸画出来的 L 形，不是折出来的
            var solidCorner = new HashSet<int>();
            foreach (var f in m.Faces.Where(f => f.Kind == FaceKind.Cylinder && f.Concave && f.Coverage < 5.8 && !skinFace.Contains(f.Id) && !bends.Any(b => b.Faces.Contains(f.Id))))
            {
                var nb = new HashSet<int>();
                foreach (var t in f.Tris) for (int k = 0; k < 3; k++) { int o = m.Adj[3 * t + k]; if (o >= 0 && skin[o] && m.FaceOf[o] != f.Id) nb.Add(m.FaceOf[o]); }
                var planes = nb.Select(id => m.FaceById[id]).Where(x => x.Kind == FaceKind.Plane).ToList();
                if (!planes.Any(a => planes.Any(b => Math.Abs(a.Normal.Dot(b.Normal)) < 0.9))) continue;
                foreach (var t in f.Tris) { solidCorner.Add(t); r.Flags[t] |= TriFlag.Bend; }
                double ang = 180 - Math.Acos(Math.Max(-1, Math.Min(1, planes[0].Normal.Dot(planes.First(b => Math.Abs(planes[0].Normal.Dot(b.Normal)) < 0.9).Normal)))) * 180 / Math.PI;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "外侧尖角", Flag = TriFlag.Bend, Severity = Severity.Warning, Tris = f.Tris.ToList(), Location = f.Centroid, Number = f.Radius,
                    Title = $"{F(180 - ang, "0")}° 拐角：外侧是尖角（内 R{F(f.Radius)}、外 R0）",
                    Value = "内侧有圆角，外侧却是尖角，拐角处比板厚厚，这是实体拉伸 / 机加工的形状，折不出来",
                    Suggestion = $"钣金折弯后外侧一定是圆角：外 R = 内 R + t = {F(f.Radius + t0, "0.#")} mm。用 SolidWorks 钣金特征（基体法兰 + 边线法兰）重建，会自动生成内外圆角；如果这个件本来就要机加工或用型材，请改选 CNC 工艺检查。",
                });
            }
            int nBends = bends.Count + sharp.Count + r.Issues.Count(x => x.Kind == "外侧尖角");
            if (t0 > 2 * mat.MaxBendT)
                r.Issues.Add(new DfmIssue
                {
                    Kind = "板厚", Severity = Severity.Error, Location = (m.Min + m.Max) / 2, Number = t0,
                    Title = $"{F(t0)} mm 太厚，不是钣金件", Value = "钣金一般是 0.5–6 mm 的板（激光切割 + 折弯）",
                    Suggestion = "这么厚的零件请改选 CNC（块状零件）或板材切割（平板，激光 / 水刀 / 等离子）；需要折边的厚板改成几块板焊接。",
                });
            else if (nBends > 0 && t0 > mat.MaxBendT)
                r.Issues.Add(new DfmIssue
                {
                    Kind = "板厚", Severity = t0 > 2 * mat.MaxBendT ? Severity.Error : Severity.Warning, Location = (m.Min + m.Max) / 2, Number = t0,
                    Title = $"板太厚：{F(t0)} mm 要折弯", Value = $"{mat.Name} 常见的钣金厂折弯能力约 {F(mat.MaxBendT, "0")} mm",
                    Suggestion = "找有大吨位折弯机的厂家（价格高、R 大），或者改成几块板焊接 / 螺栓连接，或者减薄板厚、用折边和加强筋补强度。",
                });

            // ---- 切割边、孔 ----
            var cs = Contours(t => edge[t], out var contourOf, g => true);
            foreach (var c in cs.Where(c => c.Hole != null)) foreach (var t in c.Tris) r.Flags[t] |= TriFlag.Hole;
            var outer = cs.FirstOrDefault(c => c.Outer);
            double cutLen = aEdge / t0;
            var mbCut = s.SheetCutPrice;
            foreach (var c in cs.Where(c => !c.Outer))
            {
                ct.ThrowIfCancellationRequested();
                bool round = c.Hole != null;
                string what = round ? $"Φ{F(c.Dia)} 孔" : "开口 / 槽";
                // 孔径
                if (round && c.Dia < mat.MinHoleK * t0 - 0.01)
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "小孔", Flag = TriFlag.Hole, Severity = Severity.Warning, Tris = c.Tris, Location = c.Center, Number = c.Dia,
                        Title = $"小孔 Φ{F(c.Dia)}：比板厚还小", Value = $"孔径 {F(c.Dia)} mm < {(mat.MinHoleK > 1 ? F(mat.MinHoleK, "0.#") + " × " : "")}板厚 {F(t0)} mm",
                        Suggestion = $"激光切 / 冲的孔径小于板厚时，孔壁挂渣、锥度大、尺寸不准（冲孔的冲头也容易断）。孔径改到 ≥ {F(Math.Ceiling(mat.MinHoleK * t0 * 10) / 10, "0.#")} mm；必须这么小就先切小孔、再钻孔或铰孔。",
                    });
                // 离折弯太近
                if (c.Tris.Any(t => Touches(t, bendTri))) continue;   // 切进折弯区的是止裂槽 / 缺口
                var verts = c.Tris.SelectMany(t => new[] { m.T[3 * t], m.T[3 * t + 1], m.T[3 * t + 2] }).Distinct().Select(m.P).ToList();
                double best = double.MaxValue; Bend bb = null;
                foreach (var b in bends)
                    foreach (var fl in b.Flanges)
                    {
                        var nF = fl.Face.Normal; var pF = fl.Face.Centroid;
                        foreach (var p in verts)
                        {
                            double off = (p - pF).Dot(nF);
                            if (off > tol || off < -t0 - tol) continue;
                            double ax = p.Dot(b.Axis);
                            if (ax < b.T0 - t0 || ax > b.T1 + t0) continue;
                            double d = p.Dot(fl.U) - fl.U0;
                            if (d < -tol) continue;
                            if (d < best) { best = d; bb = b; }
                        }
                    }
                if (bb != null && best < 2 * t0 - 0.01)
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "孔离折弯太近", Flag = TriFlag.Hole, Severity = Severity.Warning, Tris = c.Tris, Location = c.Center, Number = best,
                        Title = $"{what}离折弯太近",
                        Value = $"孔边到折弯圆角起点 {F(Math.Max(0, best), "0.#")} mm（建议 ≥ 2t = {F(2 * t0, "0.#")} mm，即离折弯内侧 ≥ 2t + R = {F(2 * t0 + bb.Ri, "0.#")} mm）",
                        Suggestion = $"折弯时这里的材料会被拉伸，孔会变成椭圆、孔边起皱，位置也会偏。把孔移到离折弯圆角起点 ≥ {F(2 * t0, "0.#")} mm 的地方；位置不能动就在孔和折弯之间切一条工艺槽（宽 ≥ t，切到折弯线），或者折弯后再钻孔。",
                    });
            }
            // 孔边距、孔间距、窄条：从切割边往材料里测到对面切割边的距离
            if (outer != null)
            {
                var (wd, wh) = Across(t => edge[t], false, 1.5 * t0 + 0.01);
                double Req(Contour a, Contour b) => a.Outer != b.Outer ? 1.5 * t0 : t0;
                foreach (var kv in GroupPairs(contourOf, wd, wh, (a, b) => Req(cs[a], cs[b])))
                {
                    var A = cs[kv.Key.Item1]; var B = cs[kv.Key.Item2];
                    var tris = kv.Value; double w = tris.Min(t => wd[t]);
                    var all = tris.Concat(tris.Select(t => wh[t])).Distinct().ToList();
                    foreach (var t in all) r.Flags[t] |= TriFlag.Thin;
                    double req = Req(A, B);
                    var inner = A.Outer ? B : A;
                    string title = A.Outer && B.Outer ? $"窄条：只有 {F(w)} mm 宽" : A.Outer != B.Outer ? $"{(inner.Hole != null ? $"Φ{F(inner.Dia)} 孔" : "开口")}离板边太近：{F(w)} mm" : $"孔 / 开口之间太近：{F(w)} mm";
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "边距", Flag = TriFlag.Thin, Severity = w < 0.5 * req ? Severity.Error : Severity.Warning, Tris = all, Location = Centroid(tris), Number = w,
                        Title = title, Value = $"最窄 {F(w)} mm（建议 ≥ {(A.Outer != B.Outer ? "1.5t" : "t")} = {F(req, "0.#")} mm）",
                        Suggestion = A.Outer != B.Outer
                            ? $"孔边离板边太近，切割时热变形、冲孔时会把边撑裂，受力后也容易从这里撕开。把孔往里移，孔边到板边 ≥ {F(req, "0.#")} mm；受力的螺丝孔最好孔中心到边 ≥ 2 倍孔径。"
                            : $"太窄的地方切割时受热变形、容易断。加宽到 ≥ {F(req, "0.#")} mm，或者把两个孔合成一个长槽。",
                    });
                }
                // 窄槽（只看内部开口自己的两侧）
                var (sd, sh) = Across(t => edge[t], true, t0 - 0.01);
                foreach (var c in cs.Where(c => !c.Outer && c.Hole == null))
                {
                    var tris = c.Tris.Where(t => !double.IsNaN(sd[t]) && contourOf[sh[t]] == c.Id).ToList();
                    if (tris.Sum(t => m.A[t]) < Math.Max(0.2, 0.05 * c.Area)) continue;
                    double w = tris.Min(t => sd[t]);
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "窄槽", Flag = TriFlag.Hole, Severity = Severity.Warning, Tris = c.Tris, Location = Centroid(tris), Number = w,
                        Title = $"窄槽：宽 {F(w)} mm", Value = $"槽宽小于板厚 {F(t0)} mm",
                        Suggestion = $"激光切窄槽时两侧熔渣容易粘连、槽宽不准（冲压更做不了）。槽宽改到 ≥ {F(t0)} mm。",
                    });
                }
            }
            // 斜边（沉头孔、倒角）：切割边的法向和旁边的板面不垂直
            var bevelTris = new List<int>();
            foreach (var f in m.Faces)
            {
                if (skinFace.Contains(f.Id)) continue;
                double sum = 0, cnt = 0;
                foreach (var t in f.Tris)
                {
                    if (!edge[t]) continue;
                    for (int k = 0; k < 3; k++) { int o = m.Adj[3 * t + k]; if (o >= 0 && skin[o]) { sum += Math.Abs(m.N[t].Dot(m.N[o])); cnt++; } }
                }
                if (cnt > 0 && sum / cnt > 0.3) bevelTris.AddRange(f.Tris.Where(t => edge[t] && !solidCorner.Contains(t)));
            }
            foreach (var g in Regions(new HashSet<int>(bevelTris).Contains).Where(g => g.Sum(t => m.A[t]) > 0.5))
            {
                foreach (var t in g) r.Flags[t] |= TriFlag.Undercut;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "斜边", Flag = TriFlag.Undercut, Severity = Severity.Warning, Tris = g, Location = Centroid(g), Number = g.Sum(t => m.A[t]),
                    Title = "沉头孔 / 倒角 / 斜边", Value = "激光切出来的边都垂直于板面，这里是斜的",
                    Suggestion = "沉头孔要另外锪孔（或者冲压沉孔），倒角要另外打磨，多一道工序。可以改用沉头铆钉、压铆螺钉，或者普通孔 + 平头 / 杯头螺丝；只是去毛刺的话在图纸上注「锐边倒钝」就行。",
                });
            }
            if (!m.FromStep) r.Notes.Add("STL 没有面信息，折弯（圆柱面）识别可能不准；建议用 STEP 检查钣金件。");

            // ---- 估算 ----
            int pierces = cs.Count;
            double laserPrice = double.IsNaN(mbCut) ? mat.LaserPrice(t0) : mbCut;
            double speed = mat.LaserSpeed(t0);
            double laserMin = cutLen / 1000 / speed + pierces * (0.5 + 0.3 * t0) / 60;
            double laser = cutLen / 1000 * laserPrice + pierces * (0.1 + 0.05 * t0);
            double blankKg = flat * t0 / 1e6 * mat.Density / 0.75;
            double material = blankKg * mat.PricePerKg;
            double bendCost = nBends * s.BendPrice;
            double setup = 30.0 / Math.Max(1, s.Quantity);
            double total = material + laser + bendCost + setup;
            var bendNote = string.Join("；", bends.Select(b => $"{F(b.Angle, "0")}° R{F(b.Ri)} × {F(b.Length, "0.#")} mm").Concat(sharp.Select(g => $"{F(g.Angle, "0")}° R0 × {F(g.Length, "0.#")} mm")).Concat(r.Issues.Where(x => x.Kind == "外侧尖角").Select(x => $"内 R{F(x.Number)}、外侧尖角")));
            r.Metrics.Add(new Metric("折弯", nBends == 0 ? "没有（平板）" : $"{nBends} 处", nBends == 0 ? null : bendNote));
            r.Metrics.Add(new Metric("展开面积（估算）", $"{F(flat / 100, "0.#")} cm²", "等厚部分表面积的一半（中性层系数按 0.5）；准确的展开尺寸用 SolidWorks 钣金「展开」看"));
            r.Metrics.Add(new Metric("切割长度（估算）", $"{F(cutLen / 1000, "0.##")} m，{pierces} 个轮廓", "切割边（侧面）的面积 ÷ 板厚 = 外轮廓和所有孔的周长；每个轮廓穿孔一次"));
            r.Metrics.Add(new Metric("重量", $"{F(mass, "0.#")} g", $"体积 {F(m.Volume / 1000, "0.##")} cm³ × {F(mat.Density)} g/cm³（{mat.Name}）"));
            r.Metrics.Add(new Metric("材料费（估算）", $"{F(material, "0.#")} 元", $"展开面积 × 板厚 × 密度 ÷ 排料利用率 75% = {F(blankKg, "0.###")} kg × {F(mat.PricePerKg, "0.#")} 元/kg"));
            r.Metrics.Add(new Metric("激光切割（估算）", $"{F(laser, "0.#")} 元（约 {Time(laserMin * 60)}）", $"{F(cutLen / 1000, "0.##")} m × {F(laserPrice, "0.##")} 元/m + 穿孔 {pierces} × {F(0.1 + 0.05 * t0, "0.##")} 元；切割速度约 {F(speed, "0.#")} m/min（3 kW 光纤激光）"));
            r.Metrics.Add(new Metric("折弯（估算）", $"{F(bendCost, "0.#")} 元", $"{nBends} 刀 × {F(s.BendPrice, "0.#")} 元/刀"));
            r.Metrics.Add(new Metric("单件合计（估算）", $"{F(total, "0")} 元", $"材料 + 切割 + 折弯 + 编程调机 30 元 ÷ {s.Quantity} 件；不含表面处理（喷粉、镀锌、拉丝）和压铆件"));
            r.Notes.Add("钣金件的尺寸公差一般 ±0.1–0.2 mm（切割）、折弯角度 ±0.5–1°、折弯后尺寸 ±0.2–0.5 mm；要求高的配合尺寸在图纸上单独标注。");
        }

        /// <summary>展示用：面积最大的表皮平面朝上（钣金件平放）。</summary>
        Vec3? BaseNormal(HashSet<int> skinFace)
        {
            var f = Mesh.Faces.Where(x => x.Kind == FaceKind.Plane && skinFace.Contains(x.Id)).OrderByDescending(x => x.Area).FirstOrDefault();
            if (f == null) return null;
            // 让零件「摆在桌上」：面积最大的板面所在的一侧朝下（在零件的最低处）
            var up = f.Normal;
            Mesh.Extent(up, out var lo, out var hi);
            double h = f.Centroid.Dot(up);
            return h - lo < hi - h ? up : -up;
        }

        List<Bend> FindBends(Func<DfmFace, bool> isSkin, double t0, double tol)
        {
            var m = Mesh;
            var cyl = m.Faces.Where(f => f.Kind == FaceKind.Cylinder && isSkin(f) && f.Coverage < 5.8 && f.Length > 1.2 * t0).ToList();
            var inner = GroupCylinders(cyl.Where(f => f.Concave));
            var outer = GroupCylinders(cyl.Where(f => !f.Concave));
            var used = new HashSet<CylGroup>();
            var bends = new List<Bend>();
            foreach (var gi in inner)
            {
                var go = outer.Where(g => !used.Contains(g) && Math.Abs(g.Axis.Dot(gi.Axis)) > 0.999 && Perp(g.AxisPoint - gi.AxisPoint, gi.Axis) < 0.05 + 0.05 * gi.Radius &&
                                          Math.Abs(g.Radius - gi.Radius - t0) < 2 * tol)
                              .OrderBy(g => Math.Abs(g.Radius - gi.Radius - t0)).FirstOrDefault();
                if (go != null) used.Add(go);
                var b = new Bend { Axis = gi.Axis, AxisPoint = gi.AxisPoint, Ri = gi.Radius, Angle = gi.Coverage * 180 / Math.PI, Length = gi.Length, T0 = gi.T0, T1 = gi.T1, HasOuter = go != null, Centroid = gi.Centroid };
                b.InnerTris.AddRange(gi.Tris); b.Tris.AddRange(gi.Tris);
                foreach (var f in gi.Faces) b.Faces.Add(f.Id);
                if (go != null) { b.Tris.AddRange(go.Tris); foreach (var f in go.Faces) b.Faces.Add(f.Id); }
                bends.Add(b);
            }
            // 折边：和内侧圆柱面相邻的表皮平面；直边长度 = 平面沿「垂直于折弯线」方向离开切线的最远距离
            foreach (var b in bends)
            {
                var shared = new Dictionary<int, List<int>>();
                foreach (var t in b.InnerTris)
                    for (int k = 0; k < 3; k++)
                    {
                        int o = m.Adj[3 * t + k]; if (o < 0) continue;
                        int fo = m.FaceOf[o]; if (b.Faces.Contains(fo)) continue;
                        var fc = m.FaceById[fo];
                        if (fc.Kind != FaceKind.Plane || !isSkin(fc) || Math.Abs(fc.Normal.Dot(b.Axis)) > 0.05) continue;
                        if (!shared.TryGetValue(fo, out var l)) shared[fo] = l = new List<int>();
                        l.Add(m.T[3 * t + k]); l.Add(m.T[3 * t + (k + 1) % 3]);
                    }
                foreach (var kv in shared)
                {
                    var fc = m.FaceById[kv.Key];
                    var u = b.Axis.Cross(fc.Normal).Normalized();
                    var mid = Vec3.Zero; foreach (var v in kv.Value) mid = mid + m.P(v); mid = mid / kv.Value.Count;
                    if ((fc.Centroid - mid).Dot(u) < 0) u = -u;
                    double u0 = kv.Value.Average(v => m.P(v).Dot(u));
                    double umax = FaceVerts(fc).Max(v => m.P(v).Dot(u));
                    b.Flanges.Add(new Flange { Face = fc, U = u, U0 = u0, Straight = Math.Max(0, umax - u0) });
                }
            }
            return bends;
        }

        // ================= 板材切割（2D） =================

        void AnalyzeCut(DfmSettings s, DfmResult r, Action<string> log, CancellationToken ct)
        {
            var m = Mesh; var mat = s.PlateMaterial; var method = s.CutMethod;
            log?.Invoke("正在识别板面和轮廓");
            // 板面法向：面积最大的一组平行平面
            var planes = m.Faces.Where(f => f.Kind == FaceKind.Plane).ToList();
            Vec3 n = new Vec3(0, 0, 1); double bestA = -1;
            foreach (var f in planes)
            {
                double a = planes.Where(g => Math.Abs(g.Normal.Dot(f.Normal)) > 0.999).Sum(g => g.Area);
                if (a > bestA + 1e-6) { bestA = a; n = f.Normal.Canonical(); }
            }
            // 板面高度：朝下的面（底面）和朝上的面（顶面）按高度分层，取面积最大的一对
            var bottom = new List<(double h, double a)>(); var top = new List<(double h, double a)>();
            foreach (var f in planes.Where(f => Math.Abs(f.Normal.Dot(n)) > 0.999))
            {
                var list = f.Normal.Dot(n) < 0 ? bottom : top;
                double h = f.Centroid.Dot(n);
                int i = list.FindIndex(x => Math.Abs(x.h - h) < 0.01);
                if (i >= 0) list[i] = (list[i].h, list[i].a + f.Area); else list.Add((h, f.Area));
            }
            double hb = double.NaN, ht = double.NaN, pairA = -1;
            foreach (var b in bottom) foreach (var tp in top)
                    if (tp.h > b.h + 0.05 && b.a + tp.a > pairA) { pairA = b.a + tp.a; hb = b.h; ht = tp.h; }
            if (double.IsNaN(hb))
            {
                r.Issues.Add(new DfmIssue { Kind = "非板件", Severity = Severity.Error, Location = (m.Min + m.Max) / 2, Title = "找不到平行的上下板面", Value = "板材切割要求零件是一块平板（上下两个平行的平面）", Suggestion = "确认零件是平板；曲面或块状的零件请改选 CNC、3D 打印或钣金。" });
                return;
            }
            double t = ht - hb, tol = Math.Max(0.02, 0.01 * t);
            r.Thickness = t; r.PlateNormal = n; r.ViewUp = n;
            // 每个面分类：0 板面、1 贯穿的切割边、2 要另外铣的地方。
            // 切割边 = 垂直于板面、而且外侧整个板厚范围内都没有材料（沉头孔下面的圆柱段也算，沉槽的侧壁不算）
            var cls = new int[m.Tris]; var faceCls = new Dictionary<int, int>(); var faceH = new Dictionary<int, (double lo, double hi)>();
            double mid = (hb + ht) / 2;
            bool Through(DfmFace f)
            {
                int ok = 0, cnt = 0;
                foreach (var x in f.Tris.OrderByDescending(x => m.A[x]).Take(5))
                {
                    var p = m.C[x] + m.N[x] * Math.Min(0.05, 0.02 * t);
                    p = p + n * (mid - p.Dot(n));
                    double up = Bvh.Cast(p, n, -1, out int h1, t / 2 - tol), dn = Bvh.Cast(p, -n, -1, out int h2, t / 2 - tol);
                    cnt++; if (h1 < 0 && h2 < 0) ok++;
                }
                return ok * 2 > cnt;
            }
            foreach (var f in m.Faces)
            {
                var hs = FaceVerts(f).Select(v => m.P(v).Dot(n)).ToList();
                double lo = hs.Min(), hi = hs.Max(); faceH[f.Id] = (lo, hi);
                int c;
                if (f.Kind == FaceKind.Plane && Math.Abs(f.Normal.Dot(n)) > 0.999)
                {
                    double h = f.Centroid.Dot(n);
                    c = f.Normal.Dot(n) < 0 && Math.Abs(h - hb) < tol || f.Normal.Dot(n) > 0 && Math.Abs(h - ht) < tol ? 0 : 2;
                }
                else if (Vertical(f, n) && hi > hb + tol && lo < ht - tol) c = lo <= hb + tol && hi >= ht - tol || Through(f) ? 1 : 2;
                else c = 2;
                faceCls[f.Id] = c;
                foreach (var x in f.Tris) cls[x] = c;
            }
            double skinA = 0, featA = 0;
            for (int x = 0; x < m.Tris; x++) { if (cls[x] == 0) skinA += m.A[x]; else if (cls[x] == 2) featA += m.A[x]; }
            if (skinA < 0.3 * m.Area)
                r.Issues.Add(new DfmIssue
                {
                    Kind = "非板件", Severity = Severity.Error, Location = (m.Min + m.Max) / 2, Number = skinA / m.Area,
                    Title = "看起来不是平板零件", Value = $"上下板面（相距 {F(t)} mm）只占表面 {F(skinA / m.Area * 100, "0")}%",
                    Suggestion = "板材切割只能做「平板 + 垂直贯穿的外形和孔」。块状或立体的零件请改选 CNC、3D 打印；带折边的请选钣金。",
                });
            double maxT = mat.MaxT(method);
            string mname = MethodName(method, mat);
            if (maxT <= 0)
                r.Issues.Add(new DfmIssue
                {
                    Kind = "工艺", Severity = mat.Composite ? Severity.Warning : Severity.Error, Location = (m.Min + m.Max) / 2,
                    Title = $"{mat.Name}一般不用{mname}",
                    Value = method == CutMethod.Laser ? "激光切复合材料时树脂会烧焦、碳化，边缘分层，烟尘有毒" : "雕刻机切钢 / 不锈钢很慢、刀磨损快、容易断刀",
                    Suggestion = method == CutMethod.Laser ? "碳纤维板、玻纤板用 CNC 雕刻（Φ1.5–3 碳板专用铣刀）或水刀切割。" : "钢板、不锈钢板用激光或水刀切割。",
                });
            else if (t > maxT + 0.01)
                r.Issues.Add(new DfmIssue
                {
                    Kind = "板厚", Severity = Severity.Error, Location = (m.Min + m.Max) / 2, Number = t,
                    Title = $"板太厚：{F(t)} mm", Value = $"{mname}切{mat.Name}一般最厚约 {F(maxT, "0")} mm",
                    Suggestion = method == CutMethod.Waterjet ? "换更薄的板，或者改用 CNC 铣削。" : "改用水刀切割（可以切很厚的板），或者换更薄的板。",
                });
            if (method == CutMethod.Waterjet && mat.Composite)
                r.Notes.Add("水刀切碳板 / 玻纤板：穿孔的地方容易分层，可以先钻引孔或低压穿孔；切面比铣的略粗糙。");
            if (method == CutMethod.Router && mat.Composite)
                r.Notes.Add("CNC 雕刻切碳板：用碳板专用铣刀（Φ1.5–3），开吸尘（碳粉导电，对呼吸道有害），板下垫牺牲板；切完边缘用细砂纸倒钝，涂一层 CA 胶可以防止边缘起毛分层。");
            if (method == CutMethod.Laser) r.Notes.Add($"{mat.LaserKind}切割：切缝约 0.1–0.3 mm，内角基本是尖的；切割边有轻微热影响区，{(mat.Metal ? "铝板、不锈钢用氮气切，边缘不发黑" : "亚克力切口光亮")}。");

            // ---- 要另外铣的地方 ----
            var cs = Contours(x => cls[x] == 1, out var contourOf, g => Math.Abs(g.Axis.Dot(n)) > 0.999);
            foreach (var c in cs.Where(c => c.Hole != null)) foreach (var x in c.Tris) r.Flags[x] |= TriFlag.Hole;
            var holeTri = new HashSet<int>(cs.Where(c => c.Hole != null).SelectMany(c => c.Tris));
            var feats = new Dictionary<string, (List<int> tris, int count, double num)>();
            foreach (var g in Regions(x => cls[x] == 2))
            {
                double area = g.Sum(x => m.A[x]);
                if (area < Math.Max(0.3, 1e-5 * m.Area)) continue;
                double above = 0, depth = 0; bool raised = false, recess = false, incl = false, round = false;
                foreach (var f in g.Select(x => m.FaceOf[x]).Distinct().Select(id => m.FaceById[id]))
                {
                    if (f.Kind == FaceKind.Plane && Math.Abs(f.Normal.Dot(n)) > 0.999)
                    {
                        double h = f.Centroid.Dot(n); bool up = f.Normal.Dot(n) > 0;
                        if (up && h > ht + tol) { raised = true; above = Math.Max(above, h - ht); }
                        else if (!up && h < hb - tol) { raised = true; above = Math.Max(above, hb - h); }
                        else if (up) { recess = true; depth = Math.Max(depth, ht - h); }
                        else { recess = true; depth = Math.Max(depth, h - hb); }
                    }
                    else if (f.Kind == FaceKind.Cylinder && Math.Abs(f.Axis.Dot(n)) < 0.1) round = true;
                    else if (!Vertical(f, n)) incl = true;
                }
                bool csk = !raised && !recess && incl && g.Any(x => Touches(x, holeTri));
                string kind = raised ? "凸起" : recess ? "沉槽 / 台阶" : csk ? "沉头孔" : round ? "边缘圆角" : incl ? "倒角 / 斜面" : "非贯穿特征";
                feats.TryGetValue(kind, out var acc);
                feats[kind] = ((acc.tris ?? new List<int>()).Concat(g).ToList(), acc.count + 1, Math.Max(acc.num, raised ? above : depth));
            }
            foreach (var kv in feats)
            {
                var (tris, count, num) = kv.Value;
                foreach (var x in tris) r.Flags[x] |= TriFlag.Undercut;
                double area = tris.Sum(x => m.A[x]);
                string fiber = mat.Composite ? "；碳板 / 玻纤板铣掉表层会切断纤维，强度明显下降，边缘也容易分层起毛" : "";
                string title, sug;
                switch (kv.Key)
                {
                    case "凸起":
                        title = $"凸起：高出板面 {F(num, "0.##")} mm（{count} 处）";
                        sug = $"板材切割做不出凸起。要做的话得用更厚的板（{F(Math.Max(t + num, m.Extent(n, out _, out _)), "0.#")} mm）把其余表面铣掉，费工费料{fiber}。建议改成纯 {F(t)} mm 平板：凸起部分做成单独的零件，用螺丝 / 胶粘 / 卡扣装上；或者整件改用 CNC 铣铝 / 3D 打印。" + (num > 3 * t ? "如果凸起的是折弯出来的立边，请改选「钣金」工艺检查。" : "");
                        break;
                    case "沉槽 / 台阶":
                        title = $"沉槽 / 台阶：深 {F(num, "0.##")} mm（{count} 处）";
                        sug = $"沉槽、台阶要另外铣（控制深度），多一道工序{fiber}。能改成通孔、通槽或用垫片代替就改；必须保留就在图纸上单独标注深度。";
                        break;
                    case "沉头孔":
                        title = $"沉头孔（{count} 处）";
                        sug = mat.Composite
                            ? "沉头孔要另外锪孔（换刀、多一道工序）；碳板锪沉头会切断表层纤维、孔边容易崩，受力的孔建议改成普通孔 + 圆头 / 杯头螺丝（加垫片）。"
                            : "沉头孔要另外锪孔（换刀、多一道工序）。不需要齐平的话改成普通孔 + 圆头 / 杯头螺丝。";
                        break;
                    default:
                        title = $"{kv.Key}（{count} 处）";
                        sug = $"板材切割只能切出垂直于板面的边，{kv.Key}要另外铣或手工打磨{fiber}。不是必需的话去掉，图纸上注「锐边倒钝」即可。";
                        break;
                }
                r.Issues.Add(new DfmIssue
                {
                    Kind = kv.Key, Flag = TriFlag.Undercut, Severity = Severity.Warning, Tris = tris, Location = Centroid(tris), Number = num,
                    Title = title, Value = $"不是贯穿板厚的垂直边，要另外铣削（面积 {F(area, "0.#")} mm²）", Suggestion = sug,
                });
            }

            // ---- 切割轮廓 ----
            double cutLen = 0;
            for (int x = 0; x < m.Tris; x++)
                if (cls[x] == 1) { var (lo, hi) = faceH[m.FaceOf[x]]; cutLen += m.A[x] / Math.Max(hi - lo, 1e-9); }
            var outer = cs.FirstOrDefault(c => c.Outer);
            double minSlot = method == CutMethod.Router ? s.CutterDia : method == CutMethod.Waterjet ? 1.0 : mat.Metal ? Math.Max(t, 0.5) : 1.0;
            double minHole = method == CutMethod.Router ? s.CutterDia : method == CutMethod.Waterjet ? 1.5 : mat.Metal ? t : 1.0;
            // 窄槽（切割边往外测到对面的切割边）
            var (sd, sh) = Across(x => cls[x] == 1, true, minSlot - 0.01);
            var slotGroups = new Dictionary<(int, int), List<int>>();
            for (int x = 0; x < m.Tris; x++)
            {
                if (double.IsNaN(sd[x]) || holeTri.Contains(x)) continue;
                int a = contourOf[x], b = contourOf[sh[x]];
                if (a < 0 || b < 0) continue;
                var key = a < b ? (a, b) : (b, a);
                if (!slotGroups.TryGetValue(key, out var l)) slotGroups[key] = l = new List<int>();
                l.Add(x);
            }
            var slotContours = new HashSet<int>();
            foreach (var kv in slotGroups)
            {
                var tris = kv.Value; if (tris.Sum(x => m.A[x]) < 0.2) continue;
                if (kv.Key.Item1 == kv.Key.Item2 && !cs[kv.Key.Item1].Outer) slotContours.Add(kv.Key.Item1);
                double w = tris.Min(x => sd[x]);
                var all = tris.Concat(tris.Select(x => sh[x])).Distinct().ToList();
                foreach (var x in all) r.Flags[x] |= TriFlag.Corner;
                bool cant = method != CutMethod.Laser;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "窄槽", Flag = TriFlag.Corner, Severity = cant ? Severity.Error : Severity.Warning, Tris = all, Location = Centroid(tris), Number = w,
                    Title = $"窄槽：宽 {F(w)} mm",
                    Value = method == CutMethod.Router ? $"比 Φ{F(s.CutterDia)} 铣刀还窄，刀进不去" : method == CutMethod.Waterjet ? "比水柱（约 Φ0.8–1）还窄" : $"小于 {F(minSlot, "0.#")} mm，两侧熔渣容易粘连",
                    Suggestion = method == CutMethod.Router
                        ? $"槽宽加到 ≥ {F(s.CutterDia + 0.2, "0.#")} mm（比刀大一点，排屑好、尺寸准）；或者换小刀（Φ{F(Math.Max(0.8, Math.Floor(w * 10) / 10), "0.#")}，更慢更容易断）、改用激光（金属 / 亚克力）。"
                        : method == CutMethod.Waterjet ? "槽宽加到 ≥ 1.2 mm，或者改用激光（金属 / 亚克力）。" : $"槽宽加到 ≥ {F(minSlot, "0.#")} mm。",
                });
            }
            // 内角（窄槽里的不再重复报）
            if (method != CutMethod.Laser)
            {
                double rTool = method == CutMethod.Router ? s.CutterDia / 2 : 0.5;
                var corners = SharpEdges(10, true).Where(g => faceCls[g.FaceA] == 1 && faceCls[g.FaceB] == 1 && Math.Abs(g.Dir.Dot(n)) > 0.9 && !slotContours.Contains(contourOf[g.Tris[0]])).ToList();
                var cornerIssues = corners.Select(g => new DfmIssue
                {
                    Kind = "尖内角", Flag = TriFlag.Corner, Severity = method == CutMethod.Router ? Severity.Warning : Severity.Info, Tris = g.Tris, Location = g.Mid, Number = rTool,
                    Title = "尖内角",
                    Value = method == CutMethod.Router ? $"Φ{F(s.CutterDia)} 铣刀是圆的，这里会留下 R{F(rTool)} 的圆角" : "水刀的水柱有粗细，内角会留下约 R0.5 的圆角",
                    Suggestion = method == CutMethod.Router
                        ? $"在模型里给内角倒 R{F(Math.Max(rTool + 0.5, 1), "0.#")} 以上的圆角（比刀具半径大一点走刀更顺）；需要插入方形零件的地方，在角上开「狗骨头」避空孔（Φ{F(s.CutterDia)} 的圆孔让刀）。"
                        : "需要尖角配合的地方在角上开避空孔；一般零件不用改。",
                }).ToList();
                AddMerged(r, cornerIssues, 3, n2 => $"{n2} 个尖内角");
                foreach (var g in corners) foreach (var x in g.Tris) r.Flags[x] |= TriFlag.Corner;
                // 小内圆角
                foreach (var f in m.Faces.Where(f => f.Kind == FaceKind.Cylinder && f.Concave && f.Coverage <= 5.8 && faceCls[f.Id] == 1 && Math.Abs(f.Axis.Dot(n)) > 0.995 && f.Radius < rTool - 0.01 && !slotContours.Contains(contourOf[f.Tris[0]])))
                {
                    foreach (var x in f.Tris) r.Flags[x] |= TriFlag.Corner;
                    r.Issues.Add(new DfmIssue
                    {
                        Kind = "小内圆角", Flag = TriFlag.Corner, Severity = method == CutMethod.Router ? Severity.Warning : Severity.Info, Tris = f.Tris.ToList(), Location = f.Centroid, Number = f.Radius,
                        Title = $"内圆角 R{F(f.Radius)} 比刀具半径小", Value = $"{(method == CutMethod.Router ? $"Φ{F(s.CutterDia)} 刀" : "水刀")}最小只能做到 R{F(rTool)}",
                        Suggestion = $"圆角加大到 R{F(Math.Max(rTool + 0.5, 1), "0.#")} 以上，或者换更小的刀（Φ{F(Math.Max(0.8, 2 * f.Radius), "0.#")}，更慢、更容易断）。",
                    });
                }
            }
            // 孔
            foreach (var c in cs.Where(c => c.Hole != null && c.Dia < minHole - 0.01))
            {
                bool drill = method == CutMethod.Router && c.Dia >= 0.99;
                r.Issues.Add(new DfmIssue
                {
                    Kind = "小孔", Flag = TriFlag.Hole, Severity = drill ? Severity.Info : Severity.Warning, Tris = c.Tris, Location = c.Center, Number = c.Dia,
                    Title = $"小孔 Φ{F(c.Dia)}",
                    Value = method == CutMethod.Router ? $"比 Φ{F(s.CutterDia)} 铣刀小，铣不出来" : $"孔径 < {F(minHole, "0.#")} mm",
                    Suggestion = method == CutMethod.Router
                        ? (drill ? $"要换 Φ{F(c.Dia)} 的钻头钻孔（多一次换刀，问清楚加工厂能不能做）；不是配合孔的话可以放大到 Φ{F(s.CutterDia + 0.2, "0.#")} 以上直接铣。" : "Φ1 以下的钻头很容易断，碳板上更难钻。放大孔径，或者改用激光（金属 / 亚克力）。")
                        : method == CutMethod.Laser ? $"孔径小于板厚时孔壁挂渣、锥度大、尺寸不准。改到 ≥ {F(minHole, "0.#")} mm；必须这么小就切小一点再钻 / 铰。"
                        : "水刀小孔穿孔时容易崩边 / 分层，尺寸也不准。放大孔径，或者切完再钻孔。",
                });
            }
            // 筋宽、孔边距（切割边往材料里测到对面的切割边）
            double web = mat.MinWeb(t);
            double ReqWeb(Contour a, Contour b) => a.Outer != b.Outer && (a.Hole != null || b.Hole != null) ? Math.Max(1.5 * t, web) : web;
            var (wd, wh) = Across(x => cls[x] == 1, false, Math.Max(1.5 * t, web) + 0.01);
            foreach (var kv in GroupPairs(contourOf, wd, wh, (a, b) => ReqWeb(cs[a], cs[b])))
            {
                var A = cs[kv.Key.Item1]; var B = cs[kv.Key.Item2];
                var tris = kv.Value; if (tris.Sum(x => m.A[x]) < 0.1) continue;
                double w = tris.Min(x => wd[x]), req = ReqWeb(A, B);
                var all = tris.Concat(tris.Select(x => wh[x])).Distinct().ToList();
                foreach (var x in all) r.Flags[x] |= TriFlag.Thin;
                var inner = A.Outer ? B : A;
                bool edgeHole = A.Outer != B.Outer;
                string title = A.Outer && B.Outer ? $"窄筋：只有 {F(w)} mm 宽" : edgeHole ? $"{(inner.Hole != null ? $"Φ{F(inner.Dia)} 孔" : "开口")}离板边太近：{F(w)} mm" : $"孔 / 槽之间太近：{F(w)} mm";
                r.Issues.Add(new DfmIssue
                {
                    Kind = "窄筋", Flag = TriFlag.Thin, Severity = w < 0.5 * req ? Severity.Error : Severity.Warning, Tris = all, Location = Centroid(tris), Number = w,
                    Title = title, Value = $"最窄 {F(w)} mm（{mat.Short} {F(t)} mm 建议 ≥ {F(req, "0.#")} mm{(edgeHole && inner.Hole != null ? "，孔边距 1.5t" : "")}）",
                    Suggestion = mat.Composite
                        ? $"碳板 / 玻纤板太窄的地方加工时容易崩边、分层，受力后也会从这里断。加宽到 ≥ {F(req, "0.#")} mm；受力的螺丝孔最好孔中心到板边 ≥ 2–3 倍孔径。"
                        : $"太窄的地方切割时受热变形、容易断，受力后也会从这里撕开。加宽到 ≥ {F(req, "0.#")} mm。",
                });
            }

            // ---- 估算 ----
            var (w1, w2) = InPlaneSize(n);
            double sheetA = (w1 + 10) * (w2 + 10);                      // 每边留 5 mm
            double boardT = Math.Max(t, m.Extent(n, out _, out _));
            double matCost = sheetA / 1e6 * boardT * mat.Density * mat.PricePerKg;
            int pierces = cs.Count;
            double minutes, cost; string how;
            switch (method)
            {
                case CutMethod.Laser:
                    {
                        double price = mat.LaserBase + mat.LaserPerT * t, sp = Math.Max(0.1, mat.LaserSpeedK / Math.Pow(Math.Max(t, 0.3), 1.1));
                        minutes = cutLen / 1000 / sp + pierces * (0.5 + 0.3 * t) / 60;
                        cost = cutLen / 1000 * price + pierces * (0.1 + 0.05 * t) + 20.0 / Math.Max(1, s.Quantity);
                        how = $"{F(cutLen / 1000, "0.##")} m × {F(price, "0.##")} 元/m + 穿孔 {pierces} × {F(0.1 + 0.05 * t, "0.##")} 元 + 起步 20 元 ÷ {s.Quantity} 件；速度约 {F(sp, "0.#")} m/min";
                        break;
                    }
                case CutMethod.Waterjet:
                    {
                        double sp = Math.Max(0.05, mat.WaterjetK / Math.Max(t, 0.5));
                        minutes = cutLen / 1000 / sp + pierces * 3.0 / 60;
                        cost = minutes / 60 * s.CutRate + 30.0 / Math.Max(1, s.Quantity);
                        how = $"切割 {F(cutLen / 1000, "0.##")} m ÷ {F(sp, "0.##")} m/min + 穿孔 {pierces} × 3 秒，机时 {F(s.CutRate, "0")} 元/小时 + 上下料 30 元 ÷ {s.Quantity} 件";
                        break;
                    }
                default:
                    {
                        double feed = Math.Max(100, mat.RouterFeed * Math.Min(1.5, s.CutterDia / 2));
                        int passes = (int)Math.Ceiling(t / Math.Max(0.1, mat.RouterDoc * s.CutterDia) - 1e-9);
                        minutes = cutLen * passes / feed + pierces * 5.0 / 60;
                        cost = minutes / 60 * s.CutRate + 20.0 / Math.Max(1, s.Quantity);
                        how = $"切割 {F(cutLen / 1000, "0.##")} m × {passes} 层 ÷ {F(feed, "0")} mm/min + 每个轮廓下刀 5 秒，机时 {F(s.CutRate, "0")} 元/小时 + 编程装夹 20 元 ÷ {s.Quantity} 件";
                        break;
                    }
            }
            double mass = m.Volume / 1000 * mat.Density;
            r.Metrics.Add(new Metric("板厚", $"{F(t)} mm" + (boardT > t + 0.01 ? $"（最厚处 {F(boardT, "0.##")} mm）" : ""), "上下板面之间的距离（面积最大的一对平行平面）"));
            r.Metrics.Add(new Metric("外形尺寸", $"{F(w1, "0.#")} × {F(w2, "0.#")} mm", "板面内最小的外接矩形"));
            r.Metrics.Add(new Metric("切割长度", $"{F(cutLen / 1000, "0.##")} m，{pierces} 个轮廓", "所有垂直贯穿的切割边的周长；每个轮廓下刀 / 穿孔一次"));
            r.Metrics.Add(new Metric("重量", $"{F(mass, "0.#")} g", $"体积 {F(m.Volume / 1000, "0.##")} cm³ × {F(mat.Density)} g/cm³（{mat.Name}）"));
            r.Metrics.Add(new Metric("材料费（估算）", $"{F(matCost, "0.#")} 元", $"板料 {F(sheetA / 100, "0")} cm²（外形每边留 5 mm）× {F(boardT, "0.#")} mm × {F(mat.Density)} g/cm³ × {F(mat.PricePerKg, "0")} 元/kg"));
            r.Metrics.Add(new Metric("加工时间（估算）", Time(minutes * 60), how));
            r.Metrics.Add(new Metric($"{mname}费（估算）", $"{F(cost, "0.#")} 元", how));
            r.Metrics.Add(new Metric("单件合计（估算）", $"{F(cost + matCost, "0")} 元", $"{s.Quantity} 件时的单件价格，不含另外铣削（凸起、沉头孔等）和表面处理"));
        }

        /// <summary>面是不是垂直于板面（网格化的短圆柱单个三角形会偏几度，所以按面积平均判断）。</summary>
        bool Vertical(DfmFace f, Vec3 n)
        {
            var m = Mesh;
            if (f.Kind == FaceKind.Plane) return Math.Abs(f.Normal.Dot(n)) < 0.02;
            if (f.Kind == FaceKind.Cylinder) return Math.Abs(f.Axis.Dot(n)) > 0.995;
            // 网格化的竖直曲面：三角形一会儿朝上偏、一会儿朝下偏，带符号的平均接近 0（= 在板面上的投影面积接近 0）；斜面、锥面不会抵消
            double sum = 0, signed = 0, mx = 0;
            foreach (var t in f.Tris) { double c = m.N[t].Dot(n); sum += Math.Abs(c) * m.A[t]; signed += c * m.A[t]; mx = Math.Max(mx, Math.Abs(c)); }
            double a = Math.Max(f.Area, 1e-30);
            return Math.Abs(signed) / a < 0.02 && sum / a < 0.15 && mx < 0.35;
        }

        static string MethodName(CutMethod m, PlateMaterial mat) => m == CutMethod.Laser ? "激光切割" : m == CutMethod.Waterjet ? "水刀切割" : "CNC 雕刻";

        /// <summary>板面内最小的外接矩形（绕法向转 0–90°）。</summary>
        (double, double) InPlaneSize(Vec3 n)
        {
            var m = Mesh; n.Perpendiculars(out var e1, out var e2);
            double best = double.MaxValue, b1 = 0, b2 = 0;
            for (int k = 0; k < 45; k++)
            {
                double a = k * Math.PI / 90;
                var u = e1 * Math.Cos(a) + e2 * Math.Sin(a); var v = n.Cross(u);
                double w1 = m.Extent(u, out _, out _), w2 = m.Extent(v, out _, out _);
                if (w1 * w2 < best) { best = w1 * w2; b1 = Math.Max(w1, w2); b2 = Math.Min(w1, w2); }
            }
            return (b1, b2);
        }

        // ================= 板材共用 =================

        sealed class Contour
        {
            public int Id; public List<int> Tris; public double Area; public bool Outer; public CylGroup Hole; public Vec3 Center;
            public double Dia => Hole != null ? 2 * Hole.Radius : double.NaN;
        }

        /// <summary>切割轮廓：切割边按共享边连成的区域；面积最大的是外轮廓，其余是孔和开口（整圈圆柱面的是圆孔）。</summary>
        List<Contour> Contours(Func<int, bool> isWall, out int[] contourOf, Func<CylGroup, bool> holeOk)
        {
            var m = Mesh;
            var list = Regions(isWall).Select((g, i) => new Contour { Id = i, Tris = g, Area = g.Sum(t => m.A[t]), Center = Centroid(g) }).ToList();
            contourOf = new int[m.Tris];
            for (int i = 0; i < m.Tris; i++) contourOf[i] = -1;
            foreach (var c in list) foreach (var t in c.Tris) contourOf[t] = c.Id;
            if (list.Count > 0) list.OrderByDescending(c => c.Area).First().Outer = true;
            var groupOf = new Dictionary<int, CylGroup>();
            foreach (var g in CylGroups().Where(g => g.Coverage > 5.8 && holeOk(g))) foreach (var t in g.Tris) groupOf[t] = g;
            foreach (var c in list.Where(c => !c.Outer))
            {
                CylGroup g0 = null; bool all = true;
                foreach (var t in c.Tris)
                {
                    if (!groupOf.TryGetValue(t, out var g) || g0 != null && g != g0) { all = false; break; }
                    g0 = g;
                }
                if (all && g0 != null) { c.Hole = g0; c.Center = g0.Centroid; }
            }
            return list;
        }

        /// <summary>从切割边沿法向（outward：往外测缝宽；否则往材料里测筋宽）到对面切割边的距离，maxDist 以外不算。</summary>
        (double[] dist, int[] hit) Across(Func<int, bool> isWall, bool outward, double maxDist)
        {
            var m = Mesh; var dist = new double[m.Tris]; var hit = new int[m.Tris];
            Parallel.For(0, m.Tris, t =>
            {
                dist[t] = double.NaN; hit[t] = -1;
                if (!isWall(t)) return;
                var d = outward ? m.N[t] : -m.N[t];
                var dd = Bvh.Cast(m.C[t] + d * 1e-4, d, t, out int h, maxDist);
                if (h < 0 || !isWall(h) || m.N[h].Dot(m.N[t]) > -0.5) return;
                dist[t] = dd + 1e-4; hit[t] = h;
            });
            return (dist, hit);
        }

        /// <summary>按「本轮廓, 对面轮廓」把小于要求的三角形分组。</summary>
        Dictionary<(int, int), List<int>> GroupPairs(int[] contourOf, double[] dist, int[] hit, Func<int, int, double> req)
        {
            var g = new Dictionary<(int, int), List<int>>();
            for (int t = 0; t < Mesh.Tris; t++)
            {
                if (double.IsNaN(dist[t])) continue;
                int a = contourOf[t], b = contourOf[hit[t]];
                if (a < 0 || b < 0 || dist[t] >= req(a, b) - 0.01) continue;
                var key = a < b ? (a, b) : (b, a);
                if (!g.TryGetValue(key, out var l)) g[key] = l = new List<int>();
                l.Add(t);
            }
            return g;
        }

        /// <summary>形心相距小于 1.5 × 尺度（例如厚度）的区域合成一个（同一个凸台 / 厚块的几个面）。</summary>
        List<List<int>> MergeNear(List<List<int>> regs, Func<List<int>, double> scale)
        {
            var c = regs.Select(Centroid).ToList(); var s = regs.Select(scale).ToList();
            var parent = Enumerable.Range(0, regs.Count).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (int i = 0; i < regs.Count; i++)
                for (int j = i + 1; j < regs.Count; j++)
                    if ((c[i] - c[j]).Length < 1.5 * Math.Max(s[i], s[j])) parent[Find(i)] = Find(j);
            return Enumerable.Range(0, regs.Count).GroupBy(Find).Select(g => g.SelectMany(i => regs[i]).ToList()).ToList();
        }

        /// <summary>同类问题太多时，前 keep 个单独列出，其余合成一条。</summary>
        static void AddMerged(DfmResult r, List<DfmIssue> list, int keep, Func<int, string> title)
        {
            if (list.Count <= keep + 1) { r.Issues.AddRange(list); return; }
            r.Issues.AddRange(list.Take(keep));
            var rest = list.Skip(keep).ToList();
            var first = rest[0];
            r.Issues.Add(new DfmIssue
            {
                Kind = first.Kind, Flag = first.Flag, Severity = rest.Min(x => x.Severity), Tris = rest.SelectMany(x => x.Tris).Distinct().ToList(), Location = first.Location, Number = rest.Count,
                Title = title(rest.Count), Value = first.Value + $"（共 {rest.Count} 处，模型上都标出来了）", Suggestion = first.Suggestion,
            });
        }

        sealed class EdgeGroup { public int FaceA, FaceB; public double Length, Angle; public Vec3 Mid, Dir; public List<int> Tris = new List<int>(); public HashSet<int> Verts = new HashSet<int>(); }

        /// <summary>首尾相连（共用顶点）的折棱连成一串，例如盒子底面一圈的内角、一个螺丝柱根部一圈。</summary>
        static List<List<EdgeGroup>> Chains(List<EdgeGroup> groups)
        {
            var parent = Enumerable.Range(0, groups.Count).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            var owner = new Dictionary<int, int>();
            for (int i = 0; i < groups.Count; i++)
                foreach (var v in groups[i].Verts)
                {
                    if (owner.TryGetValue(v, out var j)) parent[Find(i)] = Find(j); else owner[v] = i;
                }
            return Enumerable.Range(0, groups.Count).GroupBy(Find).Select(g => g.Select(i => groups[i]).ToList()).ToList();
        }

        /// <summary>
        /// 两个面之间的折棱（两侧法向夹角大于 minDeg），按面对分组。concave：凹棱（内角）还是凸棱。
        /// 平面和圆柱用解析法向（圆柱被划成三角形后相邻三角形会差十几度，两个半圆柱面的接缝不能当成折棱）。
        /// </summary>
        List<EdgeGroup> SharpEdges(double minDeg, bool concave)
        {
            var m = Mesh; double c = Math.Cos(minDeg * Math.PI / 180);
            double cOther = Math.Cos(Math.Max(minDeg, 25) * Math.PI / 180);   // 自由曲面只有三角形法向，相切的接缝也会差十几度
            var map = new Dictionary<(int, int), EdgeGroup>();
            for (int t = 0; t < m.Tris; t++)
                for (int k = 0; k < 3; k++)
                {
                    int o = m.Adj[3 * t + k];
                    if (o < 0 || o < t) continue;
                    int fa = m.FaceOf[t], fb = m.FaceOf[o];
                    if (fa == fb) continue;
                    var pa = m.P(m.T[3 * t + k]); var pb = m.P(m.T[3 * t + (k + 1) % 3]);
                    var em = (pa + pb) / 2;
                    var FA = m.FaceById[fa]; var FB = m.FaceById[fb];
                    double dot = SurfaceNormal(FA, em, m.N[t]).Dot(SurfaceNormal(FB, em, m.N[o]));
                    if (dot > (FA.Kind == FaceKind.Other || FB.Kind == FaceKind.Other ? cOther : c)) continue;
                    bool cc = (m.C[o] - m.C[t]).Dot(m.N[t]) > 1e-6;
                    if (cc != concave) continue;
                    var e = pb - pa; double len = e.Length; if (len < 1e-9) continue;
                    var key = fa < fb ? (fa, fb) : (fb, fa);
                    if (!map.TryGetValue(key, out var g)) map[key] = g = new EdgeGroup { FaceA = key.Item1, FaceB = key.Item2, Dir = e / len };
                    g.Length += len; g.Mid = g.Mid + (pa + pb) / 2 * len;
                    g.Angle += Math.Acos(Math.Max(-1, Math.Min(1, dot))) * 180 / Math.PI * len;
                    g.Tris.Add(t); g.Tris.Add(o);
                    g.Verts.Add(m.T[3 * t + k]); g.Verts.Add(m.T[3 * t + (k + 1) % 3]);
                }
            foreach (var g in map.Values) { g.Mid = g.Mid / g.Length; g.Angle /= g.Length; g.Tris = g.Tris.Distinct().ToList(); }
            return map.Values.ToList();
        }

        /// <summary>面在点 p 处的外法向：平面用面法向，圆柱按轴线算（凹的朝轴线），其他面用三角形法向。</summary>
        static Vec3 SurfaceNormal(DfmFace f, Vec3 p, Vec3 fallback)
        {
            if (f.Kind == FaceKind.Plane) return f.Normal;
            if (f.Kind != FaceKind.Cylinder) return fallback;
            var d = p - f.AxisPoint; d = d - f.Axis * d.Dot(f.Axis);
            if (d.Length < 1e-9) return fallback;
            d = d.Normalized();
            return f.Concave ? -d : d;
        }

        /// <summary>面积加权的壁厚众数（1% 分箱、前后 2 箱平滑），再在众数 ±3% 以内取面积加权中位数。</summary>
        double DominantThickness(Func<int, bool> filter)
        {
            var m = Mesh; double lg = Math.Log(1.01);
            var bins = new Dictionary<int, double>();
            for (int t = 0; t < m.Tris; t++)
            {
                double th = _thickness[t];
                if (double.IsNaN(th) || th < 1e-3 || filter != null && !filter(t)) continue;
                int k = (int)Math.Round(Math.Log(th) / lg);
                bins.TryGetValue(k, out var a); bins[k] = a + m.A[t];
            }
            if (bins.Count == 0) return double.NaN;
            int best = 0; double bestA = -1;
            foreach (var k in bins.Keys.OrderBy(x => x))
            {
                double a = 0; for (int j = -2; j <= 2; j++) if (bins.TryGetValue(k + j, out var x)) a += x;
                if (a > bestA) { bestA = a; best = k; }
            }
            double center = Math.Pow(1.01, best);
            var list = new List<(double th, double a)>();
            for (int t = 0; t < m.Tris; t++)
            {
                double th = _thickness[t];
                if (!double.IsNaN(th) && Math.Abs(th - center) <= 0.03 * center && (filter == null || filter(t))) list.Add((th, m.A[t]));
            }
            if (list.Count == 0) return center;
            list.Sort((x, y) => x.th.CompareTo(y.th));
            double half = list.Sum(x => x.a) / 2, acc = 0;
            foreach (var x in list) { acc += x.a; if (acc >= half) return x.th; }
            return center;
        }

        double FaceMedianThickness(DfmFace f, out double validFrac)
        {
            var list = f.Tris.Where(t => !double.IsNaN(_thickness[t])).Select(t => (th: _thickness[t], a: Mesh.A[t])).OrderBy(x => x.th).ToList();
            double tot = list.Sum(x => x.a); validFrac = tot / Math.Max(f.Area, 1e-30);
            if (list.Count == 0) return double.NaN;
            double acc = 0;
            foreach (var x in list) { acc += x.a; if (acc >= tot / 2) return x.th; }
            return list[list.Count - 1].th;
        }

        IEnumerable<int> FaceVerts(DfmFace f) => f.Tris.SelectMany(t => new[] { Mesh.T[3 * t], Mesh.T[3 * t + 1], Mesh.T[3 * t + 2] }).Distinct();

        bool Touches(int t, HashSet<int> set)
        {
            for (int k = 0; k < 3; k++) { int o = Mesh.Adj[3 * t + k]; if (o >= 0 && set.Contains(o)) return true; }
            return false;
        }

        List<int>[] _vnb;
        /// <summary>
        /// 每个顶点到 src 三角形顶点的距离：沿网格边传播「最近的源点」，距离取到那个源点的直线距离
        /// （比沿边累加更接近真实距离，粗网格的长三角形不会把距离算大）。超过 cap 就不再往外走。
        /// </summary>
        double[] DistanceFrom(bool[] src, double cap)
        {
            var m = Mesh; int nv = m.Verts;
            if (_vnb == null)
            {
                var sets = new HashSet<int>[nv];
                for (int i = 0; i < nv; i++) sets[i] = new HashSet<int>();
                for (int t = 0; t < m.Tris; t++)
                    for (int k = 0; k < 3; k++) { int a = m.T[3 * t + k], b = m.T[3 * t + (k + 1) % 3]; sets[a].Add(b); sets[b].Add(a); }
                _vnb = sets.Select(x => x.ToList()).ToArray();
            }
            var dist = new double[nv]; var from = new int[nv];
            for (int i = 0; i < nv; i++) { dist[i] = double.PositiveInfinity; from[i] = -1; }
            var heap = new SortedSet<(double d, int v)>();
            for (int t = 0; t < m.Tris; t++)
                if (src[t]) for (int k = 0; k < 3; k++) { int v = m.T[3 * t + k]; if (dist[v] > 0) { dist[v] = 0; from[v] = v; heap.Add((0, v)); } }
            while (heap.Count > 0)
            {
                var (d, v) = heap.Min; heap.Remove(heap.Min);
                if (d > cap) break;
                var ps = m.P(from[v]);
                foreach (var w in _vnb[v])
                {
                    double nd = (m.P(w) - ps).Length;
                    if (nd >= dist[w] - 1e-12) continue;
                    if (!double.IsInfinity(dist[w])) heap.Remove((dist[w], w));
                    dist[w] = nd; from[w] = from[v]; heap.Add((nd, w));
                }
            }
            return dist;
        }
    }
}
