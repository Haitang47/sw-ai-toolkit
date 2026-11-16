using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SwKit.Core.Urdf
{
    /// <summary>几何推断的依据种类。</summary>
    public enum EvKind { MotorShaft, Rotor, Bearing, LinearBearing, LeadScrew, Rod, Rail, Shaft, AlignedHoles, FastenerPin, Planar }

    /// <summary>一条几何依据：某两个分析单元之间的接触说明了什么。</summary>
    public sealed class Evidence
    {
        public EvKind Kind;
        public double Conf;
        public string Text;
        public bool HasAxis;
        public Vec3 P, D;            // 轴线（HasAxis 时）
        public bool PreferPrismatic; // 同心的两自由度（转动 + 滑动）里更像移动
        public PartInst Guide, Mover; // 估计行程用：导向件（光轴、导轨）和运动件
        public bool Actuated;        // 电机 / 螺旋桨：主动关节，不按「空转零件」处理
    }

    /// <summary>零件按名字和外形分的角色。</summary>
    enum Role { Normal, Fastener, Motor, Bearing, LinearBearing, Rod, Rail, Slider, LeadScrew }

    /// <summary>
    /// 从零件的 B-rep 几何（圆柱面、平面）推断零部件之间的接触，转成与配合相同形式的约束行，
    /// 交给 JointSolver 用零空间分析得到关节。规则：
    ///  · 同轴的轴 / 孔（允许间隙）→ 同心约束；两个零件上同轴的孔（里面没画销）→ 同心约束（虚拟销）；
    ///  · 有同心约束的两个单元之间，只保留法向与轴平行的贴合面（端面），其他贴合面当作限位，不约束；
    ///  · 没有同心约束、但有两个以上不平行的贴合面 → 平面约束（导轨、滑槽）；只有一个贴合面 → 只记为「挨着」，最后按固定连接挂上去；
    ///  · 紧固件（螺钉、螺母、垫片、铜柱）并入拧进去的零件；销、轴并入配合最紧的一侧；轴承外圈并入轴承座；
    ///  · 电机 / 舵机：套在输出轴上的零件相对电机只能绕轴转（D 形轴、键也不锁死），其余接触电机机身的零件和电机刚性连接；
    ///  · 轴承内孔里的轴相对轴承转动；直线轴承、光轴、丝杆、导轨倾向移动。
    /// </summary>
    public static class JointInference
    {
        public const double PlaneTol = 5e-5;          // 贴合面距离
        public const double AxisTol = 2e-4;           // 同轴：轴线距离
        public static readonly double AxisCos = Math.Cos(1.5 * Math.PI / 180);
        const double MinCoverage = 2.95;              // 圆柱面至少覆盖约 170°（排除 90° 的圆角；靠边的半圆螺钉孔、腰形孔端部算）

        public sealed class Result
        {
            /// <summary>刚性并在一起的单元（单元编号 a, b, 原因）。</summary>
            public List<(int a, int b, string why)> Unions = new List<(int, int, string)>();
            public Dictionary<(int, int), List<double[]>> Rows = new Dictionary<(int, int), List<double[]>>();
            public Dictionary<(int, int), List<string>> Texts = new Dictionary<(int, int), List<string>>();
            public Dictionary<(int, int), List<Evidence>> Evid = new Dictionary<(int, int), List<Evidence>>();
            /// <summary>只是挨着（一个贴合面）的单元对和接触面积：不约束运动，最后用来把散件挂到相邻的连杆上。</summary>
            public Dictionary<(int, int), double> Touch = new Dictionary<(int, int), double>();
            public HashSet<int> Touched = new HashSet<int>();
            public int Contacts;
            public Dictionary<int, string> RoleText = new Dictionary<int, string>();
            /// <summary>开发用：接触、角色、并入的明细。</summary>
            public List<string> Debug = new List<string>();
        }

        /// <summary>开发用：为 true 时把明细写进 Result.Debug。</summary>
        public static bool Verbose;

        sealed class PartRef
        {
            public PartInst P; public int Unit; public Role Role; public string Name; public Vec3 Min, Max;
            public CylFeat ShaftAxis;   // 电机输出轴（或轴承内孔）
        }

        enum CKind { Coax, Aligned, Plane }

        sealed class Contact
        {
            public CKind Kind;
            public PartRef A, B;         // Coax：A 是凸（轴）的一方，B 是凹（孔）的一方
            public CylFeat Ca, Cb;
            public PlaneFeat Pa, Pb;
            public double Dr, Overlap, Area;
            public Vec3 P, D;            // 轴线 / 平面（P 在接触区域里，D 为 A 侧外法向）
            public List<Vec3> Samples;
        }

        // ================= 入口 =================

        public static Result Analyze(List<TopComp> units)
        {
            var res = new Result();
            var parts = new List<PartRef>();
            foreach (var u in units)
                foreach (var p in u.Parts.Where(x => !x.Hidden && x.Geom != null))
                {
                    var r = new PartRef { P = p, Unit = u.Index, Name = NameOf(p) };
                    BoxOf(p, out r.Min, out r.Max);
                    r.Role = Classify(r);
                    parts.Add(r);
                    if (r.Role != Role.Normal) res.RoleText[u.Index] = RoleName(r.Role);
                }

            // ---- 接触 ----
            var contacts = new List<Contact>();
            for (int i = 0; i < parts.Count; i++)
                for (int j = i + 1; j < parts.Count; j++)
                {
                    var a = parts[i]; var b = parts[j];
                    if (a.Unit == b.Unit || !Overlap(a, b, 1e-3)) continue;
                    FindContacts(a, b, contacts);
                }
            res.Contacts = contacts.Count;
            if (Verbose)
            {
                foreach (var p in parts.Where(x => x.Role != Role.Normal)) res.Debug.Add($"角色 {p.P.Name2}：{RoleName(p.Role)}");
                foreach (var c in contacts)
                    res.Debug.Add(c.Kind == CKind.Plane
                        ? $"贴合 {c.A.P.Name2} ↔ {c.B.P.Name2} 法向 {c.D} 面积 {c.Area * 1e6:0.#} mm²"
                        : $"{(c.Kind == CKind.Coax ? "轴孔" : "对孔")} {c.A.P.Name2}（{c.Ca}）↔ {c.B.P.Name2}（{c.Cb}）间隙 {c.Dr * 1000:0.###} 重叠 {c.Overlap * 1000:0.#} mm");
            }

            // ---- 电机输出轴、轴承内孔 ----
            foreach (var m in parts.Where(p => p.Role == Role.Motor))
            {
                var cands = new List<CylFeat>();
                foreach (var c in contacts.Where(c => c.Kind == CKind.Coax))
                {
                    if (c.A == m && c.B.Role != Role.Fastener) cands.Add(c.Ca);                                  // 电机轴插在别的零件孔里
                    else if (c.B == m && c.A.Role != Role.Fastener && PinLike(c.A, c.Ca)) cands.Add(c.Cb);       // 单独画的轴插在电机里
                }
                m.ShaftAxis = cands.OrderBy(c => c.R).FirstOrDefault();
                if (Verbose) res.Debug.Add($"电机 {m.P.Name2} 输出轴：{(m.ShaftAxis == null ? "没找到" : m.ShaftAxis.ToString())}");
            }
            foreach (var b in parts.Where(p => p.Role == Role.Bearing || p.Role == Role.LinearBearing))
                b.ShaftAxis = b.P.Geom.Cyls.Where(c => !c.Convex && c.Coverage >= MinCoverage).OrderBy(c => c.R).FirstOrDefault();

            // ---- 刚性并入 ----
            var unionSet = new HashSet<(int, int)>();
            void Union(int a, int b, string why)
            {
                if (a == b || !unionSet.Add(Key(a, b))) return;
                res.Unions.Add((a, b, why));
                if (Verbose) res.Debug.Add("并入：" + why);
            }
            // 轴承外圈 → 轴承座
            foreach (var b in parts.Where(p => p.Role == Role.Bearing || p.Role == Role.LinearBearing))
            {
                var host = contacts.Where(c => c.Kind == CKind.Coax && c.A == b && !OnAxis(c.Ca, b.ShaftAxis, true)).OrderByDescending(c => c.Overlap).FirstOrDefault();
                if (host != null) Union(b.Unit, host.B.Unit, $"轴承 {b.Name} 压装在 {host.B.Name} 里");
            }
            // 紧固件 → 拧进去 / 配合最紧的零件；没有就并入接触面积最大的
            foreach (var f in parts.Where(p => p.Role == Role.Fastener))
            {
                var mine = contacts.Where(c => c.A == f || c.B == f).ToList();
                if (mine.Count == 0) continue;
                var tight = mine.Where(c => c.Kind == CKind.Coax && c.Dr <= 5e-5).ToList();
                if (tight.Count > 0) foreach (var c in tight) Union(f.Unit, Other(c, f).Unit, $"紧固件 {f.Name} 拧在 {Other(c, f).Name} 上");
                else
                {
                    var best = mine.OrderByDescending(c => c.Kind == CKind.Plane ? c.Area : c.Overlap * 1e-3).First();
                    Union(f.Unit, Other(best, f).Unit, $"紧固件 {f.Name} 固定在 {Other(best, f).Name} 上");
                }
            }
            // 销、轴：同一根轴线上连着两个以上单元时，并入配合最紧的一侧（另一侧相对它转动）
            foreach (var p in parts.Where(x => x.Role == Role.Normal || x.Role == Role.Rod || x.Role == Role.LeadScrew))
            {
                var coax = contacts.Where(c => c.Kind == CKind.Coax && c.A == p && PinLike(p, c.Ca) && !Rotating(c)).ToList();
                var groups = coax.GroupBy(c => c.B.Unit).ToList();
                if (groups.Count < 2) continue;
                var best = coax.OrderBy(c => Math.Abs(c.Dr)).ThenByDescending(c => c.Overlap).First();
                Union(p.Unit, best.B.Unit, $"轴 / 销 {p.Name} 固定在 {best.B.Name} 上");
            }
            // 电机 / 舵机：哪一侧是输出端？
            //  · 连接图里，去掉电机后和基座不连通的那一侧（电机后面挂着的部分）是输出端 —— 舵机、关节模组即使外形对称也能分清；
            //  · 两侧都连着基座（闭环）时，套在输出轴上的零件是输出端。
            // 输出端的零件相对电机只能绕输出轴转（D 形轴、螺钉连接的法兰都不锁死）；其余接触电机的零件和电机刚性连接。
            var rotorPairs = new Dictionary<(int, int), Evidence>();
            var motors = parts.Where(p => p.Role == Role.Motor).ToList();
            if (motors.Count > 0)
            {
                var rep = new Dictionary<int, int>();
                int Find(int x) { while (rep.TryGetValue(x, out var p) && p != x) x = p; return x; }
                foreach (var (a, b, _) in res.Unions) { int ra = Find(a), rb = Find(b); if (ra != rb) rep[Math.Max(ra, rb)] = Math.Min(ra, rb); }
                var adj = new Dictionary<int, HashSet<int>>();
                foreach (var c in contacts)
                {
                    int a = Find(c.A.Unit), b = Find(c.B.Unit);
                    if (a == b) continue;
                    if (!adj.TryGetValue(a, out var sa)) adj[a] = sa = new HashSet<int>();
                    if (!adj.TryGetValue(b, out var sb)) adj[b] = sb = new HashSet<int>();
                    sa.Add(b); sb.Add(a);
                }
                var baseUnit = units.Where(u => u.Fixed).OrderByDescending(u => u.Parts.Sum(p => p.Mass)).FirstOrDefault()
                               ?? units.OrderByDescending(u => u.Parts.Sum(p => p.Mass)).First();
                foreach (var m in motors)
                {
                    int mr = Find(m.Unit), br = Find(baseUnit.Index);
                    var mine = contacts.Where(c => c.A == m || c.B == m).ToList();
                    var neighbors = mine.Select(c => Find(Other(c, m).Unit)).Where(r => r != mr).Distinct().ToList();
                    if (neighbors.Count == 0) continue;
                    // 去掉电机后，从基座出发能到的单元
                    var reach = new HashSet<int>();
                    if (br != mr)
                    {
                        var q = new Queue<int>(); q.Enqueue(br); reach.Add(br);
                        while (q.Count > 0)
                        {
                            var x = q.Dequeue();
                            if (!adj.TryGetValue(x, out var nx)) continue;
                            foreach (var y in nx) if (y != mr && reach.Add(y)) q.Enqueue(y);
                        }
                    }
                    var distal = br == mr ? new List<int>() : neighbors.Where(n => !reach.Contains(n)).ToList();
                    var shaftSide = neighbors.Where(n => mine.Any(c => Find(Other(c, m).Unit) == n && RotorSide(c, m))).ToList();
                    var rotor = distal.Count > 0 && distal.Count < neighbors.Count ? distal : shaftSide;
                    if (Verbose) res.Debug.Add($"电机 {m.P.Name2}：相邻 {neighbors.Count}，远离基座 {distal.Count}，套在输出轴上 {shaftSide.Count} → 输出端 {rotor.Count}");
                    // 电机只和输出端的零件接触（机身和安装板之间留了间隙、螺钉没画）：机身固定到包围盒重叠最多的相邻零件上
                    if (rotor.Count > 0 && neighbors.All(n => rotor.Contains(n)))
                    {
                        var rotorUnits = new HashSet<int>(mine.Select(c => Other(c, m).Unit));
                        var host = parts.Where(p => p.Unit != m.Unit && !rotorUnits.Contains(p.Unit) && Overlap(p, m, 2e-3))
                                        .OrderByDescending(p => OverlapVolume(p, m)).FirstOrDefault();
                        if (host != null) Union(m.Unit, host.Unit, $"电机 {m.Name} 的机身没有和安装件接触，按位置固定在 {host.Name} 上");
                    }
                    foreach (var n in neighbors)
                    {
                        var withN = mine.Where(c => Find(Other(c, m).Unit) == n).ToList();
                        if (rotor.Contains(n))
                        {
                            var ev = OutputAxis(m, withN);
                            if (ev == null) continue;
                            foreach (var c in withN) rotorPairs[Key(c.A.Unit, c.B.Unit)] = ev;
                            if (Verbose) res.Debug.Add($"  输出端 {Other(withN[0], m).P.Name2}：{ev.Text}（{ev.Conf:0.00}）");
                        }
                        else if (withN.Any(c => c.Kind != CKind.Plane) || withN.Sum(c => c.Area) > 2e-4)
                            Union(m.Unit, withN[0] == null ? n : Other(withN[0], m).Unit, $"{Other(withN[0], m).Name} 安装在电机 {m.Name} 的机身上");
                    }
                }
            }

            // ---- 约束行（按单元对）----
            foreach (var g in contacts.GroupBy(c => Key(c.A.Unit, c.B.Unit)))
            {
                var list = g.ToList();
                var key = g.Key;
                foreach (var c in list) { res.Touched.Add(c.A.Unit); res.Touched.Add(c.B.Unit); }
                var rows = new List<double[]>();
                var texts = new List<string>();
                var ev = new List<Evidence>();

                // 电机输出端：只能绕输出轴转
                if (rotorPairs.TryGetValue(key, out var rev))
                {
                    rows.AddRange(Revolute(rev.P, rev.D));
                    ev.Add(rev);
                    texts.Add("几何：" + rev.Text);
                    Add(res, key, rows, texts, ev);
                    continue;
                }

                var coaxial = list.Where(c => c.Kind != CKind.Plane).ToList();
                var axes = DistinctAxes(coaxial);
                foreach (var c in coaxial)
                {
                    var e = CoaxEvidence(c);
                    ev.Add(e);
                    texts.Add("几何：" + e.Text);
                    rows.AddRange(e.Kind == EvKind.Bearing ? Revolute(c.P, c.D) : Concentric(c.P, c.D));
                }
                var planes = list.Where(c => c.Kind == CKind.Plane).ToList();
                if (axes.Count == 1)
                {
                    // 只保留端面（法向与轴平行）；其他贴合面当作限位
                    var d = axes[0].D;
                    foreach (var c in planes.Where(c => Math.Abs(c.D.Dot(d)) > 0.999))
                    {
                        rows.AddRange(PlaneRows(c.P, c.D));
                        texts.Add("几何：端面贴合");
                    }
                }
                else if (axes.Count == 0)
                {
                    var normals = DistinctNormals(planes);
                    if (normals >= 2)
                    {
                        foreach (var c in planes) rows.AddRange(PlaneRows(c.P, c.D));
                        texts.Add($"几何：{planes.Count} 处平面贴合（{normals} 个方向）");
                        var guide = planes.Select(c => c.A.Role == Role.Rail ? c.A : c.B.Role == Role.Rail ? c.B : null).FirstOrDefault(x => x != null);
                        var slider = planes.Select(c => c.A.Role == Role.Slider ? c.A : c.B.Role == Role.Slider ? c.B : null).FirstOrDefault(x => x != null);
                        ev.Add(new Evidence
                        {
                            Kind = guide != null ? EvKind.Rail : EvKind.Planar, Conf = guide != null ? 0.85 : slider != null ? 0.75 : 0.35, PreferPrismatic = true,
                            Guide = guide?.P ?? planes[0].A.P, Mover = guide != null ? Other(planes.First(c => c.A == guide || c.B == guide), guide).P : planes[0].B.P,
                            Text = guide != null ? $"导轨 {guide.Name} 上的滑块" : slider != null ? $"滑块 {slider.Name} 贴着两个方向的面" : "两个方向的平面贴在一起（可能是导向，也可能只是贴在一起）",
                        });
                    }
                    else if (planes.Count > 0)
                    {
                        res.Touch[key] = planes.Sum(c => c.Area);
                        continue;
                    }
                }
                else
                {
                    foreach (var c in planes) rows.AddRange(PlaneRows(c.P, c.D));
                    texts.Add($"几何：{axes.Count} 处不同轴线的轴 / 孔（多个螺钉或销），刚性连接");
                }
                if (rows.Count > 0) Add(res, key, rows, texts, ev);
            }
            return res;
        }

        static void Add(Result res, (int, int) key, List<double[]> rows, List<string> texts, List<Evidence> ev)
        {
            res.Rows[key] = rows;
            res.Texts[key] = texts.Distinct().ToList();
            res.Evid[key] = ev;
        }

        // ================= 依据 =================

        static Evidence CoaxEvidence(Contact c)
        {
            var e = new Evidence { HasAxis = true, P = c.P, D = c.D };
            if (c.Kind == CKind.Aligned)
            {
                e.Kind = EvKind.AlignedHoles;
                var big = Math.Min(c.Ca.R, c.Cb.R) >= 3.6e-3;
                e.Conf = big ? 0.6 : 0.4;
                e.Text = $"{c.A.Name} 和 {c.B.Name} 上的孔 Φ{Mm(c.Ca.R * 2)} / Φ{Mm(c.Cb.R * 2)} 同轴（孔里没有画轴或销）" + (big ? "" : "，孔径像螺钉孔");
                return e;
            }
            var shaft = c.A; var hole = c.B;
            var fit = $"Φ{Mm(c.Ca.R * 2)} 与孔 Φ{Mm(c.Cb.R * 2)}";
            if ((hole.Role == Role.Bearing || hole.Role == Role.LinearBearing) && OnAxis(c.Cb, hole.ShaftAxis, false))
            {
                var linear = hole.Role == Role.LinearBearing;
                e.Kind = linear ? EvKind.LinearBearing : EvKind.Bearing;
                e.Conf = linear ? 0.85 : hole.Name.Length > 0 && NameIsBearing(hole.Name) ? 0.9 : 0.75;
                e.PreferPrismatic = linear;
                e.Guide = shaft.P; e.Mover = hole.P;
                e.Text = linear ? $"光轴 {shaft.Name} 穿过直线轴承 {hole.Name}（{fit}）" : $"{shaft.Name} 装在轴承 {hole.Name} 的内孔里（{fit}）";
                return e;
            }
            if (shaft.Role == Role.Fastener || hole.Role == Role.Fastener)
            {
                e.Kind = EvKind.FastenerPin; e.Conf = 0.3;
                e.Text = $"只靠一个紧固件 {(shaft.Role == Role.Fastener ? shaft.Name : hole.Name)} 连接（{fit}），通常是固定的";
                return e;
            }
            if (shaft.Role == Role.LeadScrew || hole.Role == Role.LeadScrew)
            {
                e.Kind = EvKind.LeadScrew; e.Conf = 0.8; e.PreferPrismatic = true;
                e.Guide = shaft.P; e.Mover = hole.P;
                e.Text = $"丝杆 {shaft.Name} 与螺母 {hole.Name}（{fit}）";
                return e;
            }
            var rodLen = c.Ca.Length;
            if (shaft.Role == Role.Rod && c.Overlap < 0.5 * rodLen || shaft.Role == Role.Rod && hole.Role == Role.Slider)
            {
                e.Kind = EvKind.Rod; e.Conf = 0.75; e.PreferPrismatic = true;
                e.Guide = shaft.P; e.Mover = hole.P;
                e.Text = $"光轴 {shaft.Name} 穿过 {hole.Name}（{fit}，接触长 {Mm(c.Overlap)} mm，轴长 {Mm(rodLen)} mm）";
                return e;
            }
            if (JointSolver.LooksLikeRotor(hole.Name) || JointSolver.LooksLikeRotor(shaft.Name))
            {
                e.Kind = EvKind.Rotor; e.Conf = 0.85; e.Actuated = true;
                e.Text = $"{(JointSolver.LooksLikeRotor(hole.Name) ? hole.Name : shaft.Name)} 套在轴上（{fit}）";
                return e;
            }
            e.Kind = EvKind.Shaft; e.Conf = 0.7;
            e.Text = $"{shaft.Name} 的轴 {fit}（{hole.Name}）同轴" + (c.Dr > 2e-5 ? $"，间隙 {Mm(c.Dr * 2)} mm" : "");
            return e;
        }

        /// <summary>电机输出端零件的转轴：与输出轴同轴 > 法兰中心孔 > 一圈螺钉孔的圆心 > 输出轴 > 贴合面法向。</summary>
        static Evidence OutputAxis(PartRef m, List<Contact> withN)
        {
            var other = Other(withN[0], m);
            var kind = JointSolver.LooksLikeRotor(other.Name) ? EvKind.Rotor : EvKind.MotorShaft;
            Evidence Ev(Vec3 p, Vec3 d, double conf, string text) => new Evidence { Kind = kind, Conf = conf, HasAxis = true, P = p, D = d.Normalized(), Actuated = true, Text = text };
            var axial = withN.Where(c => c.Kind != CKind.Plane).ToList();
            if (axial.Count > 0)
            {
                // 取数量最多的一组平行轴线（法兰上的销、螺钉孔、中心孔）
                var d = axial.GroupBy(c => axial.Count(o => Math.Abs(o.D.Normalized().Dot(c.D.Normalized())) > 0.999)).OrderByDescending(g => g.Key).First().First().D.Normalized();
                var par = axial.Where(c => Math.Abs(c.D.Normalized().Dot(d)) > 0.999).ToList();
                // 同一根轴线上的接触算一个
                var lines = new List<Contact>();
                foreach (var c in par.OrderByDescending(c => Math.Max(c.Ca.R, c.Cb.R)))
                    if (!lines.Any(o => DistToLine(c.P, o.P, d) < 3e-4)) lines.Add(c);
                Vec3 Proj(Vec3 p) => p - d * (p - lines[0].P).Dot(d);
                var cen = Vec3.Zero; foreach (var c in lines) cen = cen + Proj(c.P); cen = cen / lines.Count;
                var best = lines.OrderBy(c => (Proj(c.P) - cen).Length).First();
                var spread = lines.Max(c => (Proj(c.P) - cen).Length);
                bool isShaft = m.ShaftAxis != null && DistToLine(m.ShaftAxis.P, best.P, d) < 3e-4 && Math.Abs(m.ShaftAxis.D.Dot(d)) > 0.999;
                var dia = Mm(Math.Min(best.Ca.R, best.Cb.R) * 2);
                if (lines.Count == 1)
                    return Ev(best.P, d, isShaft ? 0.95 : 0.85, isShaft ? $"{other.Name} 装在电机 {m.Name} 的输出轴（Φ{dia}）上" : $"{other.Name} 与电机 {m.Name} 的输出端同轴连接（Φ{dia}）");
                if ((Proj(best.P) - cen).Length <= Math.Max(1e-3, 0.2 * spread))
                    return Ev(best.P, d, isShaft ? 0.95 : 0.9, $"{other.Name} 固定在电机 {m.Name} 的输出端（中心 Φ{dia}，周围 {lines.Count - 1} 个孔或销）");
                if (lines.Count == 2 && m.ShaftAxis != null && Math.Abs(m.ShaftAxis.D.Dot(d)) > 0.999)
                    return Ev(m.ShaftAxis.P, d, 0.7, $"{other.Name} 用 2 个孔固定在电机 {m.Name} 的输出端（转轴取电机输出轴）");
                return Ev(cen, d, lines.Count >= 3 ? 0.8 : 0.6, $"{other.Name} 用 {lines.Count} 个孔固定在电机 {m.Name} 的输出法兰上（转轴取孔的中心）");
            }
            if (m.ShaftAxis != null) return Ev(m.ShaftAxis.P, m.ShaftAxis.D, 0.7, $"{other.Name} 贴在电机 {m.Name} 的输出端（转轴取电机输出轴）");
            var pl = withN.OrderByDescending(c => c.Area).First();
            return Ev(pl.P, pl.D, 0.5, $"{other.Name} 贴在电机 {m.Name} 的输出面上（没有找到输出轴，转轴取贴合面的法向）");
        }

        static bool Rotating(Contact c) =>
            (c.B.Role == Role.Bearing || c.B.Role == Role.LinearBearing) && OnAxis(c.Cb, c.B.ShaftAxis, false) || c.A.Role == Role.Motor || c.B.Role == Role.Motor;

        /// <summary>接触在电机输出轴上（零件套在轴上、D 形平面、轴端面）。</summary>
        static bool RotorSide(Contact c, PartRef m)
        {
            var ax = m.ShaftAxis;
            if (ax == null) return false;
            if (c.Kind == CKind.Coax)
            {
                var mine = c.A == m ? c.Ca : c.Cb;
                return Coaxial(mine, ax, out _) && mine.R <= ax.R * 1.6 + 1e-4;
            }
            if (c.Kind == CKind.Aligned) return false;
            // 平面：所有接触点都在轴附近
            if (c.Samples == null || c.Samples.Count == 0) return false;
            return c.Samples.All(s => DistToAxis(s, ax) <= ax.R * 1.6 + 2e-4);
        }

        static bool OnAxis(CylFeat c, CylFeat axis, bool sameRadius)
        {
            if (c == null || axis == null) return false;
            if (!Coaxial(c, axis, out _)) return false;
            return !sameRadius || Math.Abs(c.R - axis.R) < 1e-6;
        }

        static PartRef Other(Contact c, PartRef p) => c.A == p ? c.B : c.A;

        // ================= 角色 =================

        static readonly string[] FastenerWords = { "螺钉", "螺丝", "螺栓", "螺母", "螺帽", "垫片", "垫圈", "弹垫", "平垫", "铜柱", "铝柱", "尼龙柱", "支撑柱", "螺柱", "隔离柱", "六角柱", "紧定", "顶丝", "沉头", "盘头", "内六角", "外六角", "热熔", "铆钉", "螺纹", "screw", "bolt", "nut", "washer", "standoff", "insert", "rivet", "iso 4762", "iso4762", "iso 7380", "iso 7045", "din 912", "din912", "din 934", "gb_", "gb-t", "gb／t", "socket head", "countersunk", "set screw", "cap screw", "hex nut", "lock nut" };
        static readonly string[] MotorWords = { "电机", "马达", "舵机", "motor", "servo", "stepper", "步进", "gearmotor", "dynamixel", "xl330", "xl430", "xm430", "xc330", "xh430", "xm540", "xw540", "mx-28", "mx-64", "mx-106", "ax-12", "sg90", "mg90", "mg996", "ds3218", "nema", "无刷", "brushless", "outrunner",
                                                       "sts3", "sts2", "scs0", "scs1", "scs2", "st3215", "飞特", "feetech", "lx-16", "lx-224", "hiwonder", "幻尔", "rs00", "rs01", "rs02", "rs03", "rs04", "rs05", "rs06", "robstride", "灵足", "ak10", "ak60", "ak70", "ak80", "gm6020", "m3508", "m2006", "关节模组", "执行器", "actuator", "一体化关节", "cybergear", "小米电机" };
        /// <summary>电机的安装件、转接件不是电机（例如「电机转接件」「舵机固定件」「电机座」）。</summary>
        static readonly string[] NotMotorWords = { "转接", "支架", "安装", "固定", "座", "架", "板", "夹", "罩", "盖", "壳体", "法兰", "连接", "mount", "bracket", "holder", "adapter", "plate", "seat", "clamp", "cover", "horn", "舵盘", "摆臂", "垫片", "垫板", "垫圈", "垫块", "spacer", "washer", "shim" };
        static readonly string[] BearingWords ={ "轴承", "bearing", "深沟球", "铜套", "轴套", "石墨套", "sleeve" };
        static readonly string[] LinearBearingWords = { "直线轴承", "linear bearing", "直线衬套", "直线轴套", "lmk", "lmf", "滑套", "linear bushing" };
        static readonly string[] RodWords = { "光轴", "导杆", "导柱", "光杆", "linear rod", "linear shaft", "guide rod", "rod" };
        static readonly string[] RailWords = { "导轨", "滑轨", "rail", "mgn", "hgr", "hiwin", "直线模组" };
        static readonly string[] SliderWords = { "滑块", "滑台", "滑座", "carriage", "slider", "slide block" };
        static readonly string[] LeadScrewWords = { "丝杆", "丝杠", "leadscrew", "lead screw", "ballscrew", "ball screw", "滚珠丝", "梯形丝", "t8丝", "螺杆" };
        static readonly Regex MetricSize = new Regex(@"(^|[^a-z])m\d(\.\d)?\s*[x×*]\s*\d", RegexOptions.IgnoreCase);
        static readonly Regex LinearBearingCode = new Regex(@"(^|[^a-z])lm\d+(uu|luu|auu)", RegexOptions.IgnoreCase);
        static readonly Regex BearingCode = new Regex(@"^(smr|mr|f|sf)?(6\d{2,3}|69\d{2}|68\d{2}|60\d{2}|62\d{2}|63\d{2})(zz|2rs|rs|z)?$", RegexOptions.IgnoreCase);

        static string NameOf(PartInst p)
        {
            var s = JointSolver.CleanName(p.Short);
            var file = Path.GetFileNameWithoutExtension(p.File ?? "");
            return string.Equals(s, file, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(file) ? s : s;
        }

        static string Lower(PartRef r) => (r.Name + " " + Path.GetFileNameWithoutExtension(r.P.File ?? "")).ToLowerInvariant();

        static bool Has(string s, string[] words) => words.Any(w => s.Contains(w));

        internal static bool NameIsBearing(string name)
        {
            var s = name.ToLowerInvariant();
            return Has(s, BearingWords) || BearingCode.IsMatch(s.Trim());
        }

        static Role Classify(PartRef r)
        {
            var s = Lower(r);
            var file = (r.P.File ?? "").ToLowerInvariant();
            if (Has(s, LeadScrewWords)) return Role.LeadScrew;
            if (Has(s, LinearBearingWords) || LinearBearingCode.IsMatch(s)) return Role.LinearBearing;
            if (Has(s, MotorWords) && !Has(s, NotMotorWords)) return Role.Motor;
            if (NameIsBearing(r.Name) || Has(s, BearingWords)) return Role.Bearing;
            if (Has(s, RailWords)) return Role.Rail;
            if (Has(s, SliderWords)) return Role.Slider;
            if ((Has(s, FastenerWords) || MetricSize.IsMatch(s) || file.Contains("\\toolbox") || file.Contains("solidworks data")) && SmallAndSimple(r)) return Role.Fastener;
            if (Has(s, RodWords) || LooksLikeRod(r)) return Role.Rod;
            if (LooksLikeBearing(r)) return Role.Bearing;
            return Role.Normal;
        }

        /// <summary>
        /// 名字像紧固件的零件还要外形简单（整圈的圆柱面不超过 6 个、尺寸不超过 8 cm），
        /// 排除「中心上板_压铆螺母」「电机_垫片（带一圈孔的垫板）」这类名字里带紧固件字样的板子。
        /// </summary>
        static bool SmallAndSimple(PartRef r) =>
            r.P.Geom.Cyls.Count(c => c.Coverage >= MinCoverage) <= 6 && (r.Max - r.Min).Length <= 0.08;

        /// <summary>细长的整圆柱（长度 ≥ 8 倍直径），像光轴。</summary>
        static bool LooksLikeRod(PartRef r)
        {
            var c = r.P.Geom.Cyls.Where(x => x.Convex && x.Coverage >= MinCoverage).OrderByDescending(x => x.Length).FirstOrDefault();
            if (c == null || c.Length < 8 * 2 * c.R) return false;
            var ext = Extent(r, c.D);
            return c.Length >= 0.85 * ext && Width(r, c.D) <= 2 * c.R * 1.3;
        }

        /// <summary>外形是圆环（同轴的外圆 + 内孔，宽度和壁厚相当），像轴承。</summary>
        static bool LooksLikeBearing(PartRef r)
        {
            var cyl = r.P.Geom.Cyls.Where(x => x.Coverage >= MinCoverage).ToList();
            var outer = cyl.Where(x => x.Convex).OrderByDescending(x => x.R).FirstOrDefault();
            var inner = cyl.Where(x => !x.Convex).OrderBy(x => x.R).FirstOrDefault();
            if (outer == null || inner == null || outer.R > 0.08 || outer.R < inner.R * 1.25 || !Coaxial(outer, inner, out _)) return false;
            var wall = outer.R - inner.R;
            var width = Extent(r, outer.D);
            if (width < 0.5 * wall || width > 2.2 * wall) return false;
            if (Math.Abs(outer.Length - width) > 0.3 * width || Math.Abs(inner.Length - width) > 0.3 * width) return false;
            return Width(r, outer.D) <= 2 * outer.R * 1.08;
        }

        /// <summary>零件的主体是一根沿 c 的圆柱（销、轴、螺钉）。</summary>
        static bool PinLike(PartRef r, CylFeat c)
        {
            if (!c.Convex) return false;
            var ext = Extent(r, c.D);
            return c.Length >= 0.5 * ext && Width(r, c.D) <= 2 * c.R * 2.6;
        }

        static double Extent(PartRef r, Vec3 d)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = new Vec3((i & 1) == 0 ? r.Min.X : r.Max.X, (i & 2) == 0 ? r.Min.Y : r.Max.Y, (i & 4) == 0 ? r.Min.Z : r.Max.Z);
                var t = p.Dot(d); lo = Math.Min(lo, t); hi = Math.Max(hi, t);
            }
            return hi - lo;
        }

        /// <summary>垂直于 d 方向上包围盒的最大宽度（只对接近坐标轴的方向准确）。</summary>
        static double Width(PartRef r, Vec3 d)
        {
            d.Perpendiculars(out var t1, out var t2);
            return Math.Max(Extent(r, t1), Extent(r, t2));
        }

        static string RoleName(Role r)
        {
            switch (r)
            {
                case Role.Fastener: return "紧固件";
                case Role.Motor: return "电机";
                case Role.Bearing: return "轴承";
                case Role.LinearBearing: return "直线轴承";
                case Role.Rod: return "光轴";
                case Role.Rail: return "导轨";
                case Role.Slider: return "滑块";
                case Role.LeadScrew: return "丝杆";
                default: return "";
            }
        }

        // ================= 接触检测 =================

        static void BoxOf(PartInst p, out Vec3 mn, out Vec3 mx)
        {
            mn = p.BoxMin; mx = p.BoxMax;
            if (mx.X >= mn.X && (mx - mn).Length > 0) return;
            // SolidWorks 没给包围盒：用几何特征估算
            mn = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue); mx = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (var pl in p.Geom.Planes) { mn = Min(mn, pl.Min); mx = Max(mx, pl.Max); }
            foreach (var c in p.Geom.Cyls) { var a = c.P + c.D * c.T0; var b = c.P + c.D * c.T1; var r = new Vec3(c.R, c.R, c.R); mn = Min(mn, Min(a, b) - r); mx = Max(mx, Max(a, b) + r); }
        }

        static Vec3 Min(Vec3 a, Vec3 b) => new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        static Vec3 Max(Vec3 a, Vec3 b) => new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

        static bool Overlap(PartRef a, PartRef b, double tol) =>
            a.Min.X - tol <= b.Max.X && b.Min.X - tol <= a.Max.X && a.Min.Y - tol <= b.Max.Y && b.Min.Y - tol <= a.Max.Y && a.Min.Z - tol <= b.Max.Z && b.Min.Z - tol <= a.Max.Z;

        static double OverlapVolume(PartRef a, PartRef b)
        {
            double dx = Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X), dy = Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y), dz = Math.Min(a.Max.Z, b.Max.Z) - Math.Max(a.Min.Z, b.Min.Z);
            return Math.Max(0, dx + 1e-3) * Math.Max(0, dy + 1e-3) * Math.Max(0, dz + 1e-3);
        }

        static bool BoxOverlap(Vec3 amin, Vec3 amax, Vec3 bmin, Vec3 bmax, double tol) =>
            amin.X - tol <= bmax.X && bmin.X - tol <= amax.X && amin.Y - tol <= bmax.Y && bmin.Y - tol <= amax.Y && amin.Z - tol <= bmax.Z && bmin.Z - tol <= amax.Z;

        internal static bool Coaxial(CylFeat a, CylFeat b, out double dist)
        {
            dist = double.MaxValue;
            if (Math.Abs(a.D.Dot(b.D)) < AxisCos) return false;
            dist = DistToAxis(b.P, a);
            var tol = Math.Max(AxisTol, 0.03 * Math.Min(a.R, b.R));
            if (dist > tol) return false;
            // 另一端也要在轴线上（方向有微小偏差时）
            return DistToAxis(b.P + b.D * (b.T1 - b.T0 + 1e-3), a) <= tol * 2;
        }

        static double DistToLine(Vec3 p, Vec3 o, Vec3 d)
        {
            var v = p - o;
            return (v - d * v.Dot(d)).Length;
        }

        static double DistToAxis(Vec3 p, CylFeat a)
        {
            var v = p - a.P;
            return (v - a.D * v.Dot(a.D)).Length;
        }

        /// <summary>b 在 a 的轴向上与 a 重叠的长度（负数为间隔）。</summary>
        static double AxialOverlap(CylFeat a, CylFeat b)
        {
            double s0 = (b.P + b.D * b.T0 - a.P).Dot(a.D), s1 = (b.P + b.D * b.T1 - a.P).Dot(a.D);
            double lo = Math.Max(a.T0, Math.Min(s0, s1)), hi = Math.Min(a.T1, Math.Max(s0, s1));
            return hi - lo;
        }

        static Vec3 AxisMid(CylFeat a, CylFeat b)
        {
            double s0 = (b.P + b.D * b.T0 - a.P).Dot(a.D), s1 = (b.P + b.D * b.T1 - a.P).Dot(a.D);
            double lo = Math.Max(a.T0, Math.Min(s0, s1)), hi = Math.Min(a.T1, Math.Max(s0, s1));
            return a.P + a.D * ((lo + hi) / 2);
        }

        static void FindContacts(PartRef a, PartRef b, List<Contact> outList)
        {
            var ga = a.P.Geom; var gb = b.P.Geom;
            // 轴 / 孔
            foreach (var ca in ga.Cyls)
            {
                if (ca.Coverage < MinCoverage) continue;
                foreach (var cb in gb.Cyls)
                {
                    if (cb.Coverage < MinCoverage) continue;
                    if (Math.Abs(ca.R - cb.R) > Math.Max(1e-3, 0.25 * Math.Min(ca.R, cb.R))) continue;
                    if (!Coaxial(ca, cb, out _)) continue;
                    var ov = AxialOverlap(ca, cb);
                    if (ca.Convex != cb.Convex)
                    {
                        var (sa, sb, cs, ch) = ca.Convex ? (a, b, ca, cb) : (b, a, cb, ca);
                        var dr = ch.R - cs.R;
                        if (dr < -3e-4 || dr > Math.Max(5e-4, 0.1 * cs.R) || ov < 2e-4) continue;
                        outList.Add(new Contact { Kind = CKind.Coax, A = sa, B = sb, Ca = cs, Cb = ch, Dr = dr, Overlap = ov, P = AxisMid(cs, ch), D = cs.D });
                    }
                    else if (!ca.Convex && !cb.Convex)
                    {
                        if (Math.Abs(ca.R - cb.R) > Math.Max(6e-4, 0.2 * Math.Min(ca.R, cb.R)) || ov < -1e-3) continue;
                        outList.Add(new Contact { Kind = CKind.Aligned, A = a, B = b, Ca = ca, Cb = cb, Dr = cb.R - ca.R, Overlap = ov, P = ca.P + ca.D * ((ca.T0 + ca.T1) / 2), D = ca.D });
                    }
                }
            }
            // 平面贴合
            foreach (var pa in ga.Planes)
                foreach (var pb in gb.Planes)
                {
                    if (pa.N.Dot(pb.N) > -0.9995) continue;
                    if (Math.Abs((pb.P - pa.P).Dot(pa.N)) > PlaneTol) continue;
                    if (!BoxOverlap(pa.Min, pa.Max, pb.Min, pb.Max, 1e-4)) continue;
                    var samples = PlaneOverlap(pa, pb, out var area);
                    if (samples.Count == 0) continue;
                    var c = Vec3.Zero; foreach (var s in samples) c = c + s; c = c / samples.Count;
                    outList.Add(new Contact { Kind = CKind.Plane, A = a, B = b, Pa = pa, Pb = pb, P = c, D = pa.N, Samples = samples, Area = area });
                }
        }

        /// <summary>两个共面、相对的平面的重叠部分：取三角形少的一面上的采样点，落在另一面里的点。返回采样点和估计的重叠面积。</summary>
        static List<Vec3> PlaneOverlap(PlaneFeat a, PlaneFeat b, out double area)
        {
            var src = a.Tris.Length <= b.Tris.Length ? a : b;
            var dst = src == a ? b : a;
            a.N.Perpendiculars(out var t1, out var t2);
            var dst2 = new List<(double x0, double y0, double x1, double y1, double x2, double y2)>();
            for (int i = 0; i + 8 < dst.Tris.Length; i += 9)
            {
                var p0 = new Vec3(dst.Tris[i], dst.Tris[i + 1], dst.Tris[i + 2]);
                var p1 = new Vec3(dst.Tris[i + 3], dst.Tris[i + 4], dst.Tris[i + 5]);
                var p2 = new Vec3(dst.Tris[i + 6], dst.Tris[i + 7], dst.Tris[i + 8]);
                dst2.Add((p0.Dot(t1), p0.Dot(t2), p1.Dot(t1), p1.Dot(t2), p2.Dot(t1), p2.Dot(t2)));
            }
            var result = new List<Vec3>();
            area = 0;
            int total = 0;
            double srcArea = 0;
            for (int i = 0; i + 8 < src.Tris.Length; i += 9)
            {
                var p0 = new Vec3(src.Tris[i], src.Tris[i + 1], src.Tris[i + 2]);
                var p1 = new Vec3(src.Tris[i + 3], src.Tris[i + 4], src.Tris[i + 5]);
                var p2 = new Vec3(src.Tris[i + 6], src.Tris[i + 7], src.Tris[i + 8]);
                srcArea += (p1 - p0).Cross(p2 - p0).Length / 2;
                var cen = (p0 + p1 + p2) / 3;
                var pts = new[] { cen, cen + (p0 - cen) * 0.8, cen + (p1 - cen) * 0.8, cen + (p2 - cen) * 0.8, cen + ((p0 + p1) / 2 - cen) * 0.8, cen + ((p1 + p2) / 2 - cen) * 0.8, cen + ((p2 + p0) / 2 - cen) * 0.8 };
                foreach (var p in pts)
                {
                    total++;
                    double x = p.Dot(t1), y = p.Dot(t2);
                    foreach (var t in dst2)
                        if (InTri(x, y, t)) { result.Add(p); break; }
                }
            }
            area = total > 0 ? srcArea * result.Count / total : 0;
            return result;
        }

        static bool InTri(double x, double y, (double x0, double y0, double x1, double y1, double x2, double y2) t)
        {
            double d1 = (x - t.x1) * (t.y0 - t.y1) - (t.x0 - t.x1) * (y - t.y1);
            double d2 = (x - t.x2) * (t.y1 - t.y2) - (t.x1 - t.x2) * (y - t.y2);
            double d3 = (x - t.x0) * (t.y2 - t.y0) - (t.x2 - t.x0) * (y - t.y0);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        static List<(Vec3 P, Vec3 D)> DistinctAxes(List<Contact> coax)
        {
            var axes = new List<(Vec3 P, Vec3 D)>();
            foreach (var c in coax)
            {
                var probe = new CylFeat { P = c.P, D = c.D, R = Math.Min(c.Ca.R, c.Cb.R), T0 = 0, T1 = 0 };
                if (axes.Any(x => Coaxial(new CylFeat { P = x.P, D = x.D, R = probe.R, T0 = 0, T1 = 0 }, probe, out _))) continue;
                axes.Add((c.P, c.D));
            }
            return axes;
        }

        static int DistinctNormals(List<Contact> planes)
        {
            var ns = new List<Vec3>();
            foreach (var c in planes) if (!ns.Any(n => Math.Abs(n.Dot(c.D)) > 0.999)) ns.Add(c.D);
            return ns.Count;
        }

        // ================= 约束行（与配合相同的形式）=================

        static List<double[]> Concentric(Vec3 p, Vec3 d)
        {
            d.Perpendiculars(out var t1, out var t2);
            return new List<double[]> { JointSolver.NoRot(t1), JointSolver.NoRot(t2), JointSolver.PointAlong(p, t1), JointSolver.PointAlong(p, t2) };
        }

        static List<double[]> Revolute(Vec3 p, Vec3 d)
        {
            var r = Concentric(p, d);
            r.Add(JointSolver.PointAlong(p, d));
            return r;
        }

        static List<double[]> PlaneRows(Vec3 p, Vec3 n)
        {
            n.Perpendiculars(out var t1, out var t2);
            return new List<double[]> { JointSolver.NoRot(t1), JointSolver.NoRot(t2), JointSolver.PointAlong(p, n) };
        }

        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
        static string Mm(double m) => (m * 1000).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
