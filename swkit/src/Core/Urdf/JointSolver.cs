using System;
using System.Collections.Generic;
using System.Linq;

namespace SwKit.Core.Urdf
{
    /// <summary>
    /// 从配合推断关节。
    /// 思路：每个配合都是对两个零部件之间相对运动旋量 ξ = [ω; v]（v 是原点处的速度）的线性约束 r·ξ = 0。
    /// 两组零部件之间所有约束矩阵的零空间就是它们之间剩下的自由度：
    ///   0 维 → 刚性连接（合并成同一个连杆）
    ///   1 维、纯转动 → 转动关节（轴线由零空间直接给出）
    ///   1 维、纯平移 → 移动关节
    ///   2 维且为同轴的转动 + 平移 → 只有同心没有端面约束，按转动处理并提醒
    ///   其他 → 无法识别，按固定处理并提醒
    /// </summary>
    public static class JointSolver
    {
        /// <summary>SolidWorks Y 向上 → ROS Z 向上：(x, y, z) → (x, -z, y)。</summary>
        /// <summary>开发用：最近一次几何推断的明细。</summary>
        public static JointInference.Result LastInference;

        public static readonly Mat3 YUpToZUp = new Mat3(new double[] { 1, 0, 0, 0, 0, -1, 0, 1, 0 });

        /// <summary>
        /// 识别关节。infer 见 <see cref="InferMode"/>；装配体没有读几何特征时（raw.HasGeometry = false）只看配合。
        /// </summary>
        public static RobotModel Solve(AsmData raw, string upAxis = "y", InferMode infer = InferMode.Auto)
        {
            var g = upAxis == "y" ? YUpToZUp : Mat3.Identity;
            var data = Transform(raw, g);
            if (infer == InferMode.Off || !raw.HasGeometry) return SolveCore(raw, data, upAxis, false, null);

            var units = data.Comps.Where(c => c.Parts.Any(p => !p.Hidden)).ToList();
            var geo = JointInference.Analyze(units);
            LastInference = geo;
            if (infer == InferMode.GeometryOnly)
            {
                var m = SolveCore(raw, data, upAxis, true, geo);
                m.InferUsed = InferMode.GeometryOnly;
                m.Infos.Insert(0, $"已忽略配合，只按零件几何推断关节（找到 {geo.Contacts} 处接触）");
                return m;
            }

            var mates = SolveCore(raw, data, upAxis, false, geo);
            mates.InferUsed = InferMode.Auto;
            var mateMoving = mates.Joints.Count(j => j.Type != JointType.Fixed && !j.Inferred);
            if (mateMoving == 0 && units.Count >= 2)
            {
                var only = SolveCore(raw, data, upAxis, true, geo);
                if (only.Joints.Any(j => j.Inferred && j.Detected != JointType.Fixed))
                {
                    only.InferUsed = InferMode.GeometryOnly;
                    only.Infos.Insert(0, data.Mates.Count == 0
                        ? "装配体里没有配合，已按零件几何（同轴的轴和孔、轴承、电机输出轴、贴合面）推断关节，请在右侧逐个确认"
                        : "配合把所有零件都锁在了一起（没有任何运动关节），已改为按零件几何推断关节，请在右侧逐个确认");
                    return only;
                }
                return mates;
            }
            // 有配合时：几何上很像关节、却被配合锁死的位置，列为建议
            var geoOnly = SolveCore(raw, data, upAxis, true, geo);
            AddSuggestions(mates, geoOnly);
            return mates;
        }

        /// <summary>几何推断出的高置信度关节，如果在按配合的结果里两侧在同一个连杆里（被锁死），列为建议。</summary>
        static void AddSuggestions(RobotModel mates, RobotModel geo)
        {
            var linkOfPart = new Dictionary<string, Link>();
            foreach (var l in mates.Links) foreach (var p in l.Parts) linkOfPart[p.Name2] = l;
            foreach (var j in geo.Joints.Where(x => x.Inferred && x.Detected != JointType.Fixed && x.Confidence >= 0.7))
            {
                var a = j.Parent.Parts.Select(p => linkOfPart.TryGetValue(p.Name2, out var l) ? l : null).Where(l => l != null).Distinct().ToList();
                var b = j.Child.Parts.Select(p => linkOfPart.TryGetValue(p.Name2, out var l) ? l : null).Where(l => l != null).Distinct().ToList();
                var locked = a.Intersect(b).FirstOrDefault();
                if (locked == null) continue;
                // 已经有配合给出的同轴关节就不提示
                if (mates.Joints.Any(m => m.Type != JointType.Fixed && Math.Abs(m.Axis.Normalized().Dot(j.Axis.Normalized())) > 0.999 && ((m.AxisPoint - j.AxisPoint) - m.Axis.Normalized() * (m.AxisPoint - j.AxisPoint).Dot(m.Axis.Normalized())).Length < 2e-3)) continue;
                mates.Suggestions.Add($"{j.Child.Label} 相对 {j.Parent.Label} 看起来能{(j.Detected == JointType.Prismatic ? "移动" : "转动")}（{j.Evidence}），但配合把它们锁在了同一个连杆（{locked.Label}）里。如果它应该是关节，请在 SolidWorks 里改配合，或打开「忽略配合」重新分析");
                if (mates.Suggestions.Count >= 12) break;
            }
        }

        static RobotModel SolveCore(AsmData raw, AsmData data, string upAxis, bool geometryOnly, JointInference.Result geo)
        {
            var model = new RobotModel { AssemblyName = raw.Name, UpAxis = upAxis };
            model.Warnings.AddRange(raw.Notes);
            model.Infos.AddRange(raw.InfoNotes);

            var units = data.Comps.Where(c => c.Parts.Any(p => !p.Hidden)).ToList();
            if (units.Count == 0) { model.Warnings.Add("装配体里没有可见的零件"); return model; }
            var unitSet = new HashSet<int>(units.Select(u => u.Index));
            var hiddenOnly = data.Comps.Where(c => c.Parts.Count > 0 && !unitSet.Contains(c.Index)).Select(c => c.Name2).ToList();
            if (hiddenOnly.Count > 0) model.Infos.Add($"{hiddenOnly.Count} 个零部件全部是隐藏的，没有导出：" + string.Join("、", hiddenOnly.Take(8)) + (hiddenOnly.Count > 8 ? " 等" : ""));
            var uf = new UnionFind(data.Comps.Count);

            // 参与配合的单元（没有任何配合的单元在 Auto 模式下按几何接触分析）
            var matedUnits = new HashSet<int>(geometryOnly ? Enumerable.Empty<int>() : data.Mates.SelectMany(m => m.Ents).Where(e => e.Comp != null).Select(e => e.Comp.Index));
            bool Free(int u) => !matedUnits.Contains(u) && !data.Comps[u].Fixed;
            bool UseGeo(int a, int b) => geo != null && (geometryOnly || Free(a) || Free(b));

            // 固定的零部件相对世界不动，彼此也就刚性连接
            var fixedUnits = units.Where(u => u.Fixed).ToList();
            if (!geometryOnly)
            {
                for (int i = 1; i < fixedUnits.Count; i++) uf.Union(fixedUnits[0].Index, fixedUnits[i].Index);
                foreach (var (a, b) in data.RigidPairs) uf.Union(a.Index, b.Index);
                // 子装配体里没有配合的零部件：随子装配体（锚）一起动；推断时如果它和别的零件有几何接触，改按几何分析
                foreach (var (a, b) in data.UnmatedPairs)
                    if (geo == null || !geo.Touched.Contains(b.Index)) uf.Union(a.Index, b.Index);
            }
            if (geo != null)
                foreach (var (a, b, _) in geo.Unions)
                    if (unitSet.Contains(a) && unitSet.Contains(b) && UseGeo(a, b)) uf.Union(a, b);

            // ---- 每个配合转成约束行 ----
            var pairRows = new Dictionary<(int, int), List<double[]>>();
            var pairMates = new Dictionary<(int, int), List<string>>();
            var limits = new List<MateInfo>();
            var coupled = new HashSet<int>();   // 参与齿轮 / 齿条 / 螺旋 / 线性耦合配合的零部件：它们的转动是被驱动的，不是空转
            var matePairs = new HashSet<(int, int)>();
            foreach (var m in geometryOnly ? new List<MateInfo>() : data.Mates)
            {
                if (m.Ents.Count < 2 || m.Ents[0].Comp == null || m.Ents[1].Comp == null) continue;
                var a = m.Ents[0].Comp.Index; var b = m.Ents[1].Comp.Index;
                if (a == b || !unitSet.Contains(a) || !unitSet.Contains(b)) continue;
                if (m.IsLimit) { limits.Add(m); continue; }
                if (m.Type == MateTypes.Gear || m.Type == MateTypes.RackPinion || m.Type == MateTypes.Screw || m.Type == MateTypes.LinearCoupler) { coupled.Add(a); coupled.Add(b); }
                var rows = MateRows(m, out var note);
                if (note != null) model.Infos.Add($"配合「{m.Name}」（{m.TypeName}）：{note}");
                if (rows == null || rows.Count == 0) continue;
                var key = Key(a, b);
                if (!pairRows.TryGetValue(key, out var list)) { pairRows[key] = list = new List<double[]>(); pairMates[key] = new List<string>(); }
                list.AddRange(rows);
                pairMates[key].Add($"{m.Name}（{m.TypeName}）");
                matePairs.Add(key);
            }

            // ---- 几何接触转成约束行 ----
            var pairEvid = new Dictionary<(int, int), List<Evidence>>();
            if (geo != null)
            {
                foreach (var kv in geo.Rows)
                {
                    var (a, b) = kv.Key;
                    if (!unitSet.Contains(a) || !unitSet.Contains(b) || !UseGeo(a, b)) continue;
                    if (!geometryOnly && matePairs.Contains(kv.Key)) continue;   // 两者之间有配合时以配合为准
                    if (!pairRows.TryGetValue(kv.Key, out var list)) { pairRows[kv.Key] = list = new List<double[]>(); pairMates[kv.Key] = new List<string>(); }
                    list.AddRange(kv.Value);
                    pairMates[kv.Key].AddRange(geo.Texts[kv.Key]);
                    pairEvid[kv.Key] = geo.Evid[kv.Key];
                }
            }

            // ---- 反复合并完全约束的组，直到没有变化 ----
            void MergePairwise()
            {
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var kv in GroupPairs(uf, pairRows))
                    {
                        if (LinAlg.NullSpace(kv.Value.rows).Count == 0)
                        {
                            uf.Union(kv.Key.Item1, kv.Key.Item2);
                            changed = true;
                            break;
                        }
                    }
                }
            }
            MergePairwise();
            // 闭链里两两看还能动、整个机构一起看却被锁死的组，也要合并（合并后可能又出现两两完全约束的组）
            while (MergeRigidInLoops(uf, pairRows, model.Infos)) MergePairwise();

            // ---- 关节候选（组与组之间） ----
            var edges = new List<Edge>();
            var gp = GroupPairs(uf, pairRows);
            foreach (var kv in gp)
            {
                var e = Classify(kv.Value.rows);
                e.A = kv.Key.Item1; e.B = kv.Key.Item2;
                e.Source = string.Join(" + ", kv.Value.unitPairs.SelectMany(p => pairMates[p]).Distinct());
                e.Inferred = kv.Value.unitPairs.All(p => !matePairs.Contains(p));
                if (e.Inferred) e.Evid = kv.Value.unitPairs.SelectMany(p => pairEvid.TryGetValue(p, out var l) ? l : new List<Evidence>()).ToList();
                if (e.Inferred) Grade(e);
                edges.Add(e);
            }
            // 连杆机构：只靠一个小孔铰接（孔里没画销）的边单独看把握不大；但如果一个零件两端各有一个这样的铰接孔、
            // 连着两个不同的零件，并且两根轴平行（平面连杆，例如夹爪的连杆、四连杆），它多半就是连杆，提高置信度
            var pivots = edges.Where(e => e.Inferred && (e.Type == JointType.Revolute) && e.Conf < 0.5 && e.Evid.Count > 0 && e.Evid.All(x => x.Kind == EvKind.AlignedHoles || x.Kind == EvKind.FastenerPin || x.Kind == EvKind.Planar)).ToList();
            foreach (var g in pivots.SelectMany(e => new[] { e.A, e.B }).Distinct().ToList())
            {
                var mine = pivots.Where(e => e.A == g || e.B == g).ToList();
                var lever = mine.Where(e => mine.Any(o => o != e && (o.A == g ? o.B : o.A) != (e.A == g ? e.B : e.A)
                                                     && Math.Abs(o.Axis.Dot(e.Axis)) > 0.999
                                                     && ((o.Point - e.Point) - e.Axis * (o.Point - e.Point).Dot(e.Axis)).Length > 2e-3)).ToList();
                foreach (var e in lever.Where(e => e.Conf < 0.55))
                {
                    e.Conf = 0.55;
                    e.EvidenceText += "；所在零件两端都是平行的铰接孔，像连杆机构";
                }
            }

            // 只是贴在一起（一个贴合面）的单元：不约束运动，但建树时可以经过它挂上去（优先级最低）
            if (geo != null)
            {
                var have = new HashSet<(int, int)>(edges.Select(x => Key(x.A, x.B)));
                foreach (var kv in geo.Touch.OrderByDescending(x => x.Value))
                {
                    var (a, b) = kv.Key;
                    if (!unitSet.Contains(a) || !unitSet.Contains(b) || !UseGeo(a, b)) continue;
                    int ga = uf.Find(a), gb = uf.Find(b);
                    if (ga == gb || !have.Add(Key(ga, gb))) continue;
                    edges.Add(new Edge { A = ga, B = gb, Type = JointType.Fixed, TouchOnly = true, Source = "几何：贴合在一起" });
                }
            }

            // ---- 连杆 ----
            var groups = units.GroupBy(u => uf.Find(u.Index)).ToDictionary(gr => gr.Key, gr => gr.ToList());
            double GroupMass(int key) => groups[key].SelectMany(u => u.Parts).Where(p => !p.Hidden).Sum(p => p.Mass);
            int rootKey;
            if (fixedUnits.Count > 0) rootKey = uf.Find(fixedUnits.OrderByDescending(u => u.Parts.Sum(p => p.Mass)).First().Index);
            else if (geometryOnly && groups.Keys.Any(k => LooksLikeBase(groups[k])))
                rootKey = groups.Keys.Where(k => LooksLikeBase(groups[k])).OrderByDescending(GroupMass).First();
            else
            {
                rootKey = groups.Keys.OrderByDescending(GroupMass).First();
                model.Warnings.Add($"装配体里没有固定的零部件，以最重的部分（{CleanName(groups[rootKey].SelectMany(u => u.Parts).OrderByDescending(p => p.Mass).First().Short)}）作为基座；如果不对，请在 SolidWorks 里把基座固定");
            }

            // ---- 从基座开始广度优先建树 ----
            var linkOf = new Dictionary<int, Link>();
            Link MakeLink(int key)
            {
                var members = groups[key];
                var l = new Link { Members = members };
                var main = members.SelectMany(u => u.Parts).OrderByDescending(p => p.Mass).FirstOrDefault();
                l.Label = CleanName(main != null ? main.Short : members[0].Name2);
                linkOf[key] = l;
                model.Links.Add(l);
                return l;
            }
            var root = MakeLink(rootKey);
            root.Name = "base_link";
            var underconstrained = new List<string>();
            var loose0 = new List<string>();   // 只按几何：和谁都不挨着、挂到基座的
            var gapped = new List<string>();   // 只按几何：没挨着、挂到最近连杆上的
            var usedEdges = new HashSet<Edge>();
            var edgeOf = new Dictionary<Joint, Edge>();
            void Attach(Edge e, int cur, int other)
            {
                usedEdges.Add(e);
                var child = MakeLink(other);
                var j = new Joint
                {
                    Parent = linkOf[cur], Child = child, Type = e.Type, Detected = e.Type, Axis = e.Axis, AxisPoint = e.Point,
                    Source = e.Source, NeedsReview = e.NeedsReview, Inferred = e.Inferred && e.Type != JointType.Fixed,
                    Confidence = e.Inferred ? e.Conf : 1, Evidence = e.Inferred ? e.EvidenceText : null,
                };
                if (e.Note != null) j.Notes.Add(e.Note);
                if (e.Underconstrained) underconstrained.Add(child.Label);
                model.Joints.Add(j);
                edgeOf[j] = e;
            }
            if (!geometryOnly)
            {
                // 从基座开始广度优先建树
                var queue = new Queue<int>();
                queue.Enqueue(rootKey);
                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    foreach (var e in edges.Where(x => !usedEdges.Contains(x) && (x.A == cur || x.B == cur)))
                    {
                        var other = e.A == cur ? e.B : e.A;
                        if (linkOf.ContainsKey(other)) continue;
                        Attach(e, cur, other);
                        queue.Enqueue(other);
                    }
                }
            }
            else
            {
                // 只按几何时接触很多（偶然贴在一起的面也算），优先用把握大的关节边建树：
                // 每次从已在树里的连杆出发，取优先级最高的边（单自由度的运动副按置信度 > 固定 > 欠约束）
                double Priority(Edge e) => e.TouchOnly ? -0.5 : e.Underconstrained ? 0 : e.Type == JointType.Fixed ? 0.5 : 1 + e.Conf;
                while (true)
                {
                    Edge best = null; int bc = 0, bo = 0;
                    foreach (var e in edges)
                    {
                        if (usedEdges.Contains(e)) continue;
                        bool a = linkOf.ContainsKey(e.A), b = linkOf.ContainsKey(e.B);
                        if (a == b) continue;
                        if (best == null || Priority(e) > Priority(best)) { best = e; bc = a ? e.A : e.B; bo = a ? e.B : e.A; }
                    }
                    if (best != null) { Attach(best, bc, bo); continue; }
                    // 剩下的部分和树之间没有接触（装配时留了间隙）：把离树最近的一组挂到最近的连杆上，再从它继续建树，
                    // 这样一整串没挨着的零件（例如整个夹爪）内部的关节还能识别出来
                    int pick = -1; Link near = null; double bestD = double.MaxValue;
                    foreach (var k in groups.Keys.Where(k => !linkOf.ContainsKey(k)))
                    {
                        var mine = groups[k].SelectMany(u => u.Parts).Where(p => !p.Hidden).ToList();
                        foreach (var l2 in model.Links)
                            foreach (var p in l2.Parts)
                                foreach (var q in mine)
                                {
                                    var d = BoxDistance(p, q);
                                    if (d < bestD) { bestD = d; pick = k; near = l2; }
                                }
                    }
                    if (pick < 0) break;
                    var size = groups[pick].SelectMany(u => u.Parts).Where(p => !p.Hidden).Select(q => (q.BoxMax - q.BoxMin).Length).DefaultIfEmpty(0).Max();
                    var parent = bestD <= Math.Max(3e-3, 0.3 * size) ? near : root;
                    var nl = MakeLink(pick);
                    model.Joints.Add(new Joint { Parent = parent, Child = nl, Type = JointType.Fixed, Detected = JointType.Fixed, Source = parent == root ? "（和其他零件没有接触）" : $"（没有接触，挂到最近的 {parent.Label} 上）" });
                    if (parent == root) loose0.Add(nl.Label); else gapped.Add(nl.Label);
                }
            }
            if (underconstrained.Count > 0)
                model.Infos.Add(geometryOnly
                    ? $"{underconstrained.Count} 个零部件只是贴在别的零件上（没有轴、销或导向定位），按当前位置固定：" + string.Join("、", underconstrained.Distinct().Take(10)) + (underconstrained.Count > 10 ? " 等" : "")
                    : $"{underconstrained.Count} 个零部件没有被完全约束（剩下不止一个自由度，不对应任何关节），按当前位置固定：" + string.Join("、", underconstrained.Distinct().Take(10)) + (underconstrained.Count > 10 ? " 等" : ""));

            // 闭链：两端都已经在树里的剩余边（只按几何时，只提示运动副构成的闭链；固定接触构成的环没有影响）
            foreach (var e in edges.Where(x => !usedEdges.Contains(x) && !x.TouchOnly && linkOf.ContainsKey(x.A) && linkOf.ContainsKey(x.B) && (!geometryOnly || x.Type != JointType.Fixed && x.Conf >= 0.5)))
                model.Warnings.Add($"检测到闭链：{linkOf[e.A].Label} 与 {linkOf[e.B].Label} 之间还有一处连接（{e.Source}），URDF 无法表达闭链，这处连接没有导出");

            // 没连上的组固定到基座（只按几何时上面已经全部挂好了）
            var loose = new List<string>();
            foreach (var key in groups.Keys.Where(k => !linkOf.ContainsKey(k)).ToList())
            {
                var l = MakeLink(key);
                model.Joints.Add(new Joint { Parent = root, Child = l, Type = JointType.Fixed, Detected = JointType.Fixed, Source = "（没有配合）" });
                loose.Add(l.Label);
            }
            if (loose.Count > 0)
                model.Infos.Add($"{loose.Count} 个零部件和其他零部件之间没有配合，按当前位置固定在基座上：" + string.Join("、", loose.Take(10)) + (loose.Count > 10 ? " 等" : ""));
            if (gapped.Count > 0)
                model.Infos.Add($"{gapped.Count} 个零部件和其他零件之间有间隙（没有接触），按当前位置固定在离它最近的零件上：" + string.Join("、", gapped.Distinct().Take(10)) + (gapped.Count > 10 ? " 等" : "") + "。如果它们之间其实有关节，请在 SolidWorks 里让零件贴合，或加上配合");
            if (loose0.Count > 0)
                model.Infos.Add($"{loose0.Count} 个零部件离其他零件都很远，按当前位置固定在基座上：" + string.Join("、", loose0.Distinct().Take(10)) + (loose0.Count > 10 ? " 等" : ""));

            // ---- 命名、限位、默认值 ----
            for (int i = 0; i < model.Joints.Count; i++)
            {
                var j = model.Joints[i];
                j.Child.Name = "link" + (i + 1);
                j.Name = "joint" + (i + 1);
                ApplyDefaults(j);
            }
            model.RecomputeFrames();
            foreach (var j in model.Joints) ApplyLimitMates(j, limits, uf, model);
            // 几何推断的关节：行程估计、连续转动、置信度低的默认不启用
            var actuated = new HashSet<Joint>();
            foreach (var j in model.Joints.Where(x => x.Inferred))
            {
                var e = edgeOf[j];
                if (e.Evid.Any(x => x.Actuated && x.HasAxis && SameAxis(x, j))) actuated.Add(j);
                if (j.Type == JointType.Prismatic) EstimateTravel(j, e);
                if (j.Type == JointType.Revolute && e.Evid.Any(x => x.Kind == EvKind.Rotor && SameAxis(x, j)))
                {
                    j.Type = j.Detected = JointType.Continuous;
                    j.Notes.Add("名字像螺旋桨 / 轮子，按连续转动（不限角度）导出");
                }
                j.Notes.Insert(0, $"几何推断（置信度{Joint.ConfidenceText(j.Confidence)} {j.Confidence:0.00}）：{j.Evidence}");
                if (j.Confidence < 0.5)
                {
                    j.Type = JointType.Fixed;
                    j.NeedsReview = false;
                    j.Notes.Add("把握不大，默认不启用（按固定导出）；如果它确实是关节，在类型里改成转动 / 移动");
                }
            }
            MarkSpinners(model, coupled, actuated);
            model.RecomputeFrames();
            return model;
        }

        static double BoxDistance(PartInst a, PartInst b)
        {
            double dx = Math.Max(0, Math.Max(a.BoxMin.X - b.BoxMax.X, b.BoxMin.X - a.BoxMax.X));
            double dy = Math.Max(0, Math.Max(a.BoxMin.Y - b.BoxMax.Y, b.BoxMin.Y - a.BoxMax.Y));
            double dz = Math.Max(0, Math.Max(a.BoxMin.Z - b.BoxMax.Z, b.BoxMin.Z - a.BoxMax.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static bool SameAxis(Evidence e, Joint j)
        {
            var d = j.Axis.Normalized();
            if (Math.Abs(e.D.Normalized().Dot(d)) < 0.999) return false;
            if (j.Detected == JointType.Prismatic) return true;
            var v = e.P - j.AxisPoint;
            return (v - d * v.Dot(d)).Length < 1.5e-3;
        }

        static readonly string[] BaseWords = { "底座", "底板", "机架", "机身", "基座", "主体", "车架", "框架", "base", "frame", "chassis", "body", "mount", "bottom" };
        static bool LooksLikeBase(List<TopComp> group) =>
            group.SelectMany(u => u.Parts).Any(p => BaseWords.Any(w => (p.Short ?? "").ToLowerInvariant().Contains(w) && !(p.Short ?? "").Contains("电机")));

        /// <summary>移动关节的行程：运动件在导向件（光轴、导轨）上还能走多远。</summary>
        static void EstimateTravel(Joint j, Edge e)
        {
            var ev = e.Evid.FirstOrDefault(x => x.PreferPrismatic && x.Guide != null && x.Mover != null);
            if (ev == null) return;
            var d = j.Axis.Normalized();
            void Range(PartInst p, out double lo, out double hi)
            {
                lo = double.MaxValue; hi = double.MinValue;
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vec3((i & 1) == 0 ? p.BoxMin.X : p.BoxMax.X, (i & 2) == 0 ? p.BoxMin.Y : p.BoxMax.Y, (i & 4) == 0 ? p.BoxMin.Z : p.BoxMax.Z);
                    lo = Math.Min(lo, c.Dot(d)); hi = Math.Max(hi, c.Dot(d));
                }
            }
            Range(ev.Guide, out var g0, out var g1);
            Range(ev.Mover, out var m0, out var m1);
            // 关节正方向是 d；运动件在子连杆一侧时，向 +d 还能走 g1 − m1，向 −d 能走 m0 − g0
            var moverIsChild = j.Child.Parts.Contains(ev.Mover);
            double up = g1 - m1, down = m0 - g0;
            if (!moverIsChild) { var t = up; up = down; down = t; }
            if (up < 0 || down < 0 || up + down < 1e-3) return;
            j.Lower = -down; j.Upper = up;
            j.Notes.Add($"行程按 {CleanName(ev.Mover.Short)} 在 {CleanName(ev.Guide.Short)} 上的位置估计：{-down * 1000:0.#} ~ {up * 1000:0.#} mm");
        }

        /// <summary>几何推断边的置信度和说明；同轴两自由度（转动 + 滑动）时按依据决定转动还是移动。</summary>
        static void Grade(Edge e)
        {
            var ev = e.Evid;
            if (e.Type == JointType.Fixed) { e.Conf = 1; return; }
            List<Evidence> match;
            if (e.TwoDof)
            {
                match = ev.Where(x => x.HasAxis && Math.Abs(x.D.Normalized().Dot(e.Axis)) > 0.999 && Dist(x, e) < 1.5e-3).ToList();
                var prism = match.Where(x => x.PreferPrismatic).OrderByDescending(x => x.Conf).FirstOrDefault();
                var rot = match.Where(x => !x.PreferPrismatic).OrderByDescending(x => x.Conf).FirstOrDefault();
                if (prism != null && (rot == null || prism.Conf >= rot.Conf))
                {
                    e.Type = JointType.Prismatic;
                    e.Point = Vec3.Zero;
                    e.NeedsReview = false;
                    e.Note = "同轴的轴和孔既能转也能滑，按依据判断为移动";
                    e.Conf = prism.Conf;
                }
                else
                {
                    e.Conf = (rot?.Conf ?? 0.4) * 0.9;
                    e.Note = "同轴的轴和孔之间没有端面定位，零件还能沿轴向滑动；已按转动处理";
                }
            }
            else if (e.Type == JointType.Prismatic)
            {
                match = ev.Where(x => x.PreferPrismatic && (!x.HasAxis || Math.Abs(x.D.Normalized().Dot(e.Axis)) > 0.999)).ToList();
                e.Conf = match.Count > 0 ? match.Max(x => x.Conf) : 0.35;
                if (match.Count == 0 && ev.Count(x => x.HasAxis && Math.Abs(x.D.Normalized().Dot(e.Axis)) > 0.999) >= 2)
                {
                    // 两根平行的轴（双光轴、导杆）
                    match = ev.Where(x => x.HasAxis && Math.Abs(x.D.Normalized().Dot(e.Axis)) > 0.999).ToList();
                    e.Conf = 0.8;
                }
            }
            else
            {
                match = ev.Where(x => x.HasAxis && Math.Abs(x.D.Normalized().Dot(e.Axis)) > 0.999 && Dist(x, e) < 1.5e-3).ToList();
                e.Conf = match.Count > 0 ? match.Max(x => x.Conf) : 0.3;
                if (match.Count == 0) e.Note = "运动是由几个贴合面推出来的，没有轴或孔支撑，可能只是零件贴在一起";
            }
            if (match.Count == 0) match = ev;
            e.EvidenceText = string.Join("；", match.OrderByDescending(x => x.Conf).Select(x => x.Text).Distinct().Take(3));
            if (string.IsNullOrEmpty(e.EvidenceText)) e.EvidenceText = "贴合面";
        }

        static double Dist(Evidence x, Edge e)
        {
            var d = e.Axis.Normalized();
            var v = x.P - e.Point;
            return (v - d * v.Dot(d)).Length;
        }

        /// <summary>
        /// 只能绕自身对称轴空转的零件（螺钉、铝柱、轴承、齿轮……）在配合上确实留下了一个转动自由度，
        /// 但通常不是机器人的关节。判断条件：转动关节的子连杆是末端（不再带别的关节）、质心在轴上、
        /// 并且绕该轴的惯量分布是轴对称的。这类关节默认改为固定，界面里可以改回转动。
        /// 例外：名字像螺旋桨、轮子、风扇的零件按连续转动导出；参与齿轮等耦合配合的零件是被驱动的，保持转动。
        /// </summary>
        static void MarkSpinners(RobotModel model, HashSet<int> coupled, HashSet<Joint> actuated)
        {
            var spun = new List<string>();
            var rotors = new List<string>();
            foreach (var j in model.Joints)
            {
                if (j.Type != JointType.Revolute || j.LimitsFromMate || actuated.Contains(j)) continue;
                var l = j.Child;
                if (l.Members.Any(m => coupled.Contains(m.Index))) continue;
                if (l.Parts.Any(p => LooksLikeRotor(p.Short)) || LooksLikeRotor(l.Label))
                {
                    j.Type = JointType.Continuous;
                    j.NeedsReview = false;
                    j.Notes.RemoveAll(n => n.StartsWith("只有同心"));
                    j.Notes.Add("名字像螺旋桨 / 轮子，按连续转动（不限角度）导出");
                    rotors.Add(l.Label);
                    continue;
                }
                if (model.Joints.Any(x => x.Parent == j.Child)) continue;
                if (l.Mass <= 0) continue;
                var d = j.Axis.Normalized();
                var off = (l.Com - j.AxisPoint) - d * (l.Com - j.AxisPoint).Dot(d);
                var size = l.Parts.Select(p => (p.BoxMax - p.BoxMin).Length).DefaultIfEmpty(0).Max();
                if (off.Length > Math.Max(0.0005, 0.02 * size)) continue;
                d.Perpendiculars(out var t1, out var t2);
                var I = l.Inertia;
                double a11 = t1.Dot(I * t1), a22 = t2.Dot(I * t2), a12 = t1.Dot(I * t2), ia = d.Dot(I * d);
                double c1 = t1.Dot(I * d), c2 = t2.Dot(I * d);
                var scale = Math.Max(Math.Max(a11, a22), ia);
                if (scale <= 0) continue;
                var mean = (a11 + a22) / 2;
                var aniso = Math.Sqrt((a11 - a22) * (a11 - a22) / 4 + a12 * a12);
                if (aniso > 0.02 * mean || Math.Sqrt(c1 * c1 + c2 * c2) > 0.02 * scale) continue;
                j.Type = JointType.Fixed;
                j.SpinCandidate = true;
                j.NeedsReview = false;
                j.Notes.Clear();
                j.Notes.Add("这个零件绕关节轴对称（像螺钉、铝柱、齿轮），配合上只是能空转，已默认设为固定；如果它确实是关节，请改回转动");
                spun.Add(l.Label);
            }
            if (rotors.Count > 0)
                model.Infos.Add($"{rotors.Count} 个螺旋桨 / 轮子按连续转动导出：" + string.Join("、", rotors.Distinct().Take(10)));
            if (spun.Count > 0)
                model.Infos.Add($"{spun.Count} 个绕自身轴对称、只能空转的零件已默认固定：" + string.Join("、", spun.Distinct().Take(10)) + (spun.Count > 10 ? " 等" : ""));
        }

        // ================= 关节分类 =================

        sealed class Edge
        {
            public int A, B;
            public JointType Type;
            public Vec3 Axis = new Vec3(0, 0, 1), Point;
            public bool NeedsReview, Underconstrained;
            public string Note, Source;
            public bool TwoDof;                       // 同轴的转动 + 滑动
            public bool TouchOnly;                    // 只是贴在一起（没有约束行）
            public bool Inferred;                     // 全部来自几何推断
            public List<Evidence> Evid = new List<Evidence>();
            public double Conf = 1;
            public string EvidenceText;
        }

        static Edge Classify(List<double[]> rows)
        {
            var ns = LinAlg.NullSpace(rows);
            var e = new Edge();
            if (ns.Count == 0) { e.Type = JointType.Fixed; return e; }
            if (ns.Count == 1) { FromTwist(ns[0], e); return e; }

            // 2 维：检查是否是同轴的转动 + 平移（只有同心、缺少端面约束）
            if (ns.Count == 2)
            {
                // 在零空间里找 ω 分量最小的组合 → 纯平移方向
                var w1 = Vec3.From(ns[0]); var w2 = Vec3.From(ns[1]);
                var m = new double[2, 2] { { w1.Dot(w1), w1.Dot(w2) }, { w2.Dot(w1), w2.Dot(w2) } };
                var vals = LinAlg.SymEigen(m, out var vecs);
                int kMin = vals[0] < vals[1] ? 0 : 1, kMax = 1 - kMin;
                var trans = Combine(ns, vecs[0, kMin], vecs[1, kMin]);
                var rot = Combine(ns, vecs[0, kMax], vecs[1, kMax]);
                var tw = Vec3.From(trans); var tv = Vec3.From(trans, 3);
                var rw = Vec3.From(rot);
                if (tw.Length < 1e-6 && tv.Length > 1e-9 && rw.Length > 1e-9 && Math.Abs(rw.Normalized().Dot(tv.Normalized())) > 0.999)
                {
                    // 去掉旋转旋量里沿轴向的平移分量，得到纯转动
                    var d = tv.Normalized();
                    var rv = Vec3.From(rot, 3);
                    var scale = rv.Dot(d);
                    var pure = new[] { rot[0], rot[1], rot[2], rv.X - d.X * scale, rv.Y - d.Y * scale, rv.Z - d.Z * scale };
                    FromTwist(pure, e);
                    e.TwoDof = true;
                    e.NeedsReview = true;
                    e.Note = "只有同心配合、缺少端面重合，零件还能沿轴向滑动；已按转动关节处理，建议在 SolidWorks 里补一个端面配合";
                    return e;
                }
            }
            // 剩下多个自由度：通常是只配了一部分的零件（例如只有一个面重合的板子），不是关节
            e.Type = JointType.Fixed;
            e.Underconstrained = true;
            return e;
        }

        static double[] Combine(List<double[]> ns, double a, double b)
        {
            var r = new double[6];
            for (int i = 0; i < 6; i++) r[i] = ns[0][i] * a + ns[1][i] * b;
            return r;
        }

        static void FromTwist(double[] s, Edge e)
        {
            var w = Vec3.From(s); var v = Vec3.From(s, 3);
            if (w.Length < 1e-6 * Math.Max(1, v.Length))
            {
                e.Type = JointType.Prismatic;
                e.Axis = v.Normalized().Canonical().Clean();
                return;
            }
            var d = w / w.Length;
            var vp = v / w.Length;
            var pitch = d.Dot(vp);
            e.Type = JointType.Revolute;
            e.Axis = d.Canonical().Clean();
            e.Point = d.Cross(vp).Clean(1e-9);   // 轴线上离原点最近的点
            if (Math.Abs(pitch) > 1e-4)
            {
                e.NeedsReview = true;
                e.Note = $"运动是螺旋运动（导程 {pitch * 2 * Math.PI * 1000:0.##} mm/圈），URDF 不支持，已按转动处理";
            }
        }

        // ================= 配合 → 约束行 =================

        internal static double[] Row(Vec3 w, Vec3 v) => new[] { w.X, w.Y, w.Z, v.X, v.Y, v.Z };
        /// <summary>点 p 不能沿 n 方向运动：n·(v + ω×p) = 0。</summary>
        internal static double[] PointAlong(Vec3 p, Vec3 n) => Row(p.Cross(n), n);
        /// <summary>不能绕 u 转动：ω·u = 0。</summary>
        internal static double[] NoRot(Vec3 u) => Row(u, Vec3.Zero);

        static List<double[]> Lock() => new List<double[]>
        {
            NoRot(new Vec3(1, 0, 0)), NoRot(new Vec3(0, 1, 0)), NoRot(new Vec3(0, 0, 1)),
            Row(Vec3.Zero, new Vec3(1, 0, 0)), Row(Vec3.Zero, new Vec3(0, 1, 0)), Row(Vec3.Zero, new Vec3(0, 0, 1)),
        };

        static List<double[]> MateRows(MateInfo m, out string note)
        {
            note = null;
            var a = m.Ents[0]; var b = m.Ents[1];
            switch (m.Type)
            {
                case MateTypes.Lock:
                case 20: // 坐标配合
                    return Lock();
                case MateTypes.Coincident:
                    return Coincident(a, b, ref note);
                case MateTypes.Distance:
                    if (a.Kind == GeoKind.Plane && b.Kind == GeoKind.Plane) return Coincident(a, b, ref note);
                    if (a.Kind == GeoKind.Point && b.Kind == GeoKind.Plane) return new List<double[]> { PointAlong(a.P, b.D) };
                    if (a.Kind == GeoKind.Plane && b.Kind == GeoKind.Point) return new List<double[]> { PointAlong(b.P, a.D) };
                    if (a.Kind == GeoKind.Point && b.Kind == GeoKind.Point) return new List<double[]> { PointAlong(b.P, b.P - a.P) };
                    note = "这种几何组合的距离配合暂不支持，未作为约束";
                    return null;
                case MateTypes.Concentric:
                    return Concentric(a, b, ref note);
                case MateTypes.Hinge:
                    {
                        var rows = Concentric(a, b, ref note) ?? new List<double[]>();
                        if (m.Ents.Count >= 4) rows.AddRange(Coincident(m.Ents[2], m.Ents[3], ref note) ?? new List<double[]>());
                        else note = "铰链配合缺少端面信息，只按同心处理";
                        return rows;
                    }
                case MateTypes.Parallel:
                    {
                        var u = Dir(a); var w = Dir(b);
                        if (u == null || w == null) { note = "无法识别平行配合的几何"; return null; }
                        if ((a.Kind == GeoKind.Plane) != (b.Kind == GeoKind.Plane))
                        {
                            var n = a.Kind == GeoKind.Plane ? a.D : b.D; var d = a.Kind == GeoKind.Plane ? b.D : a.D;
                            return new List<double[]> { NoRot(d.Cross(n)) };
                        }
                        u.Value.Perpendiculars(out var t1, out var t2);
                        return new List<double[]> { NoRot(t1), NoRot(t2) };
                    }
                case MateTypes.Perpendicular:
                case MateTypes.Angle:
                    {
                        var u = Dir(a); var w = Dir(b);
                        if (u == null || w == null) { note = "无法识别垂直/角度配合的几何"; return null; }
                        var c = w.Value.Cross(u.Value);
                        if (c.Length > 1e-6) return new List<double[]> { NoRot(c.Normalized()) };
                        u.Value.Perpendiculars(out var t1, out var t2);
                        return new List<double[]> { NoRot(t1), NoRot(t2) };
                    }
                case MateTypes.Gear:
                case MateTypes.RackPinion:
                case MateTypes.Screw:
                case MateTypes.LinearCoupler:
                    note = "这是两个关节之间的联动关系，URDF 里可以用 mimic 表达，目前未导出";
                    return null;
                default:
                    note = "暂不支持这种配合，未作为约束（如果它决定了关节，请检查结果）";
                    return null;
            }
        }

        static Vec3? Dir(MateGeom g) => g.Kind == GeoKind.Plane || g.Kind == GeoKind.Axis ? g.D : (Vec3?)null;

        static List<double[]> Coincident(MateGeom a, MateGeom b, ref string note)
        {
            // 让 a 是"更高维"的几何：平面 > 轴线 > 点
            if (Rank(b) > Rank(a)) { var t = a; a = b; b = t; }
            if (a.Kind == GeoKind.Plane && b.Kind == GeoKind.Plane)
            {
                a.D.Perpendiculars(out var t1, out var t2);
                return new List<double[]> { NoRot(t1), NoRot(t2), PointAlong(a.P, a.D) };
            }
            if (a.Kind == GeoKind.Plane && b.Kind == GeoKind.Axis)
            {
                var rows = new List<double[]> { PointAlong(b.P, a.D) };
                var c = b.D.Cross(a.D);
                if (c.Length > 1e-6) rows.Add(NoRot(c.Normalized()));
                return rows;
            }
            if (a.Kind == GeoKind.Plane && b.Kind == GeoKind.Point) return new List<double[]> { PointAlong(b.P, a.D) };
            if (a.Kind == GeoKind.Axis && b.Kind == GeoKind.Axis) return Concentric(a, b, ref note);
            if (a.Kind == GeoKind.Axis && b.Kind == GeoKind.Point)
            {
                a.D.Perpendiculars(out var t1, out var t2);
                return new List<double[]> { PointAlong(b.P, t1), PointAlong(b.P, t2) };
            }
            if (a.Kind == GeoKind.Point && b.Kind == GeoKind.Point)
                return new List<double[]> { PointAlong(b.P, new Vec3(1, 0, 0)), PointAlong(b.P, new Vec3(0, 1, 0)), PointAlong(b.P, new Vec3(0, 0, 1)) };
            note = "无法识别重合配合的几何，未作为约束";
            return null;
        }

        static List<double[]> Concentric(MateGeom a, MateGeom b, ref string note)
        {
            if (Rank(b) > Rank(a)) { var t = a; a = b; b = t; }
            if (a.Kind == GeoKind.Axis && (b.Kind == GeoKind.Axis || b.Kind == GeoKind.Plane))
            {
                a.D.Perpendiculars(out var t1, out var t2);
                return new List<double[]> { NoRot(t1), NoRot(t2), PointAlong(a.P, t1), PointAlong(a.P, t2) };
            }
            if (a.Kind == GeoKind.Axis && b.Kind == GeoKind.Point)
            {
                a.D.Perpendiculars(out var t1, out var t2);
                return new List<double[]> { PointAlong(b.P, t1), PointAlong(b.P, t2) };
            }
            if (a.Kind == GeoKind.Point && b.Kind == GeoKind.Point)
                return new List<double[]> { PointAlong(b.P, new Vec3(1, 0, 0)), PointAlong(b.P, new Vec3(0, 1, 0)), PointAlong(b.P, new Vec3(0, 0, 1)) };
            note = "无法识别同心配合的几何，未作为约束";
            return null;
        }

        static int Rank(MateGeom g) => g.Kind == GeoKind.Plane ? 3 : g.Kind == GeoKind.Axis ? 2 : g.Kind == GeoKind.Point ? 1 : 0;

        // ================= 限位与默认值 =================

        static void ApplyDefaults(Joint j)
        {
            switch (j.Type)
            {
                case JointType.Revolute:
                    j.Lower = -Math.PI; j.Upper = Math.PI; j.Effort = 10; j.Velocity = 3.14;
                    break;
                case JointType.Prismatic:
                    j.Lower = -0.05; j.Upper = 0.05; j.Effort = 100; j.Velocity = 0.5;
                    break;
                default:
                    j.Lower = j.Upper = 0; j.Effort = j.Velocity = 0;
                    break;
            }
        }

        /// <summary>
        /// 限位配合给出的是相对当前位置的变化量（实测：MinimumVariation = 下限 − 当前值）。
        /// 根据几何算出关节正向运动时配合值是增大还是减小，换算成关节的上下限。
        /// </summary>
        static void ApplyLimitMates(Joint j, List<MateInfo> limits, UnionFind uf, RobotModel model)
        {
            if (j.Type != JointType.Revolute && j.Type != JointType.Prismatic) return;
            var childKeys = new HashSet<int>(j.Child.Members.Select(m => uf.Find(m.Index)));
            var parentKeys = new HashSet<int>(j.Parent.Members.Select(m => uf.Find(m.Index)));
            foreach (var m in limits)
            {
                var ka = uf.Find(m.Ents[0].Comp.Index); var kb = uf.Find(m.Ents[1].Comp.Index);
                bool aChild = childKeys.Contains(ka) && parentKeys.Contains(kb);
                bool bChild = childKeys.Contains(kb) && parentKeys.Contains(ka);
                if (!aChild && !bChild) continue;
                var moving = aChild ? m.Ents[0] : m.Ents[1];
                var still = aChild ? m.Ents[1] : m.Ents[0];
                double rate;   // 关节正向单位运动引起的配合值变化
                if (j.Type == JointType.Prismatic && m.Type == MateTypes.Distance && moving.Kind == GeoKind.Plane && still.Kind == GeoKind.Plane)
                {
                    var s = (moving.P - still.P).Dot(still.D);
                    rate = Math.Sign(s == 0 ? 1 : s) * j.Axis.Dot(still.D);
                }
                else if (j.Type == JointType.Revolute && m.Type == MateTypes.Angle && moving.Kind == GeoKind.Plane && still.Kind == GeoKind.Plane)
                {
                    var c = still.D.Dot(moving.D);
                    var theta = Math.Acos(Math.Max(-1, Math.Min(1, c)));
                    var sin = Math.Sin(theta);
                    if (Math.Abs(sin) < 1e-6) { model.Infos.Add($"{j.Name}：角度限位配合「{m.Name}」当前位于 0° 或 180°，无法判断方向，未使用"); continue; }
                    var dTheta = -still.D.Dot(j.Axis.Cross(moving.D)) / sin;
                    // 配合显示的可能是 θ，也可能是 180° − θ
                    var measured = m.Value ?? theta;
                    rate = Math.Abs(measured - theta) <= Math.Abs(measured - (Math.PI - theta)) ? dTheta : -dTheta;
                }
                else continue;

                if (Math.Abs(rate) < 1e-6) { model.Infos.Add($"{j.Name}：限位配合「{m.Name}」与关节运动方向垂直，未使用"); continue; }
                var lo = m.Min / rate; var hi = m.Max / rate;
                j.Lower = Math.Min(lo, hi);
                j.Upper = Math.Max(lo, hi);
                j.LimitsFromMate = true;
                j.Source += $" + {m.Name}（限位）";
            }
        }

        // ================= 工具 =================

        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

        static readonly string[] RotorWords = { "桨", "旋翼", "轮", "风扇", "prop", "rotor", "wheel", "fan", "impeller" };
        static readonly string[] NotRotorWords = { "齿轮", "轮廓", "gear", "property", "proper", "fang" };

        /// <summary>名字像螺旋桨、轮子、风扇。（齿轮不算：齿轮由齿轮配合判断。）</summary>
        internal static bool LooksLikeRotor(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var s = name.ToLowerInvariant();
            foreach (var w in NotRotorWords) s = s.Replace(w, "");
            return RotorWords.Any(w => s.Contains(w));
        }

        /// <summary>
        /// 两两分析只看直接相连的两组。有闭链时，整个机构一起看，有些组其实不能相对运动
        /// （例如一块板既和基座的孔同心、又和电机输出轴同心，两根轴不重合，这块板就被锁在基座上）。
        /// 做法：反复去掉只连着一条边的组（树枝部分两两分析就是准确的），剩下的闭链部分一起列约束，
        /// 求整个机构的运动空间；在所有可能的运动里相对旋量都为零的两组合并。返回是否合并了。
        /// </summary>
        static bool MergeRigidInLoops(UnionFind uf, Dictionary<(int, int), List<double[]>> pairRows, List<string> infos)
        {
            var gp = GroupPairs(uf, pairRows);
            var adj = new Dictionary<int, HashSet<int>>();
            foreach (var k in gp.Keys)
            {
                if (!adj.TryGetValue(k.Item1, out var s1)) adj[k.Item1] = s1 = new HashSet<int>();
                if (!adj.TryGetValue(k.Item2, out var s2)) adj[k.Item2] = s2 = new HashSet<int>();
                s1.Add(k.Item2); s2.Add(k.Item1);
            }
            bool pruned = true;
            while (pruned)
            {
                pruned = false;
                foreach (var node in adj.Keys.ToList())
                {
                    if (adj[node].Count > 1) continue;
                    foreach (var other in adj[node]) adj[other].Remove(node);
                    adj.Remove(node);
                    pruned = true;
                }
            }
            if (adj.Count < 3) return false;
            const int maxGroups = 60;
            if (adj.Count > maxGroups)
            {
                infos.Add($"闭链部分有 {adj.Count} 组零部件，超过 {maxGroups} 组，没有做整体运动分析；闭链里的关节请人工确认");
                return false;
            }

            // 第一组固定作参考，其余每组 6 个未知数（旋量 [ω; v]）
            var nodes = adj.Keys.ToList();
            var idx = new Dictionary<int, int>();
            for (int i = 1; i < nodes.Count; i++) idx[nodes[i]] = i - 1;
            int n = 6 * (nodes.Count - 1);
            var ctc = new double[n, n];
            var entries = new List<(int i, double v)>(12);
            foreach (var kv in gp)
            {
                if (!adj.ContainsKey(kv.Key.Item1) || !adj.ContainsKey(kv.Key.Item2)) continue;
                int ia = idx.TryGetValue(kv.Key.Item1, out var xa) ? xa : -1;
                int ib = idx.TryGetValue(kv.Key.Item2, out var xb) ? xb : -1;
                foreach (var r0 in kv.Value.rows)
                {
                    double norm = Math.Sqrt(r0.Sum(v => v * v));
                    if (norm < 1e-12) continue;
                    // 约束作用在相对旋量上：r·(ξb − ξa) = 0（整体取反不影响）
                    entries.Clear();
                    for (int c = 0; c < 6; c++)
                    {
                        if (ib >= 0) entries.Add((ib * 6 + c, r0[c] / norm));
                        if (ia >= 0) entries.Add((ia * 6 + c, -r0[c] / norm));
                    }
                    foreach (var (i, vi) in entries) foreach (var (j, vj) in entries) ctc[i, j] += vi * vj;
                }
            }
            var vals = LinAlg.SymEigen(ctc, out var vecs);
            var basis = Enumerable.Range(0, n).Where(k => vals[k] < 1e-8).ToList();
            double Rel(int a, int b)
            {
                double d = 0;
                foreach (var k in basis)
                    for (int c = 0; c < 6; c++)
                    {
                        var ta = idx.TryGetValue(a, out var i) ? vecs[i * 6 + c, k] : 0;
                        var tb = idx.TryGetValue(b, out var j) ? vecs[j * 6 + c, k] : 0;
                        d += (ta - tb) * (ta - tb);
                    }
                return d;
            }
            var merged = false;
            for (int x = 0; x < nodes.Count; x++)
                for (int y = x + 1; y < nodes.Count; y++)
                    if (uf.Find(nodes[x]) != uf.Find(nodes[y]) && Rel(nodes[x], nodes[y]) < 1e-10)
                    {
                        uf.Union(nodes[x], nodes[y]);
                        merged = true;
                    }
            return merged;
        }

        /// <summary>把零部件对之间的约束按当前分组汇总成组对之间的约束。</summary>
        static Dictionary<(int, int), (List<double[]> rows, List<(int, int)> unitPairs)> GroupPairs(UnionFind uf, Dictionary<(int, int), List<double[]>> pairRows)
        {
            var result = new Dictionary<(int, int), (List<double[]>, List<(int, int)>)>();
            foreach (var kv in pairRows)
            {
                var ga = uf.Find(kv.Key.Item1); var gb = uf.Find(kv.Key.Item2);
                if (ga == gb) continue;
                var k = Key(ga, gb);
                if (!result.TryGetValue(k, out var v)) { v = (new List<double[]>(), new List<(int, int)>()); result[k] = v; }
                v.Item1.AddRange(kv.Value);
                v.Item2.Add(kv.Key);
            }
            return result;
        }

        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var i = name.LastIndexOf('-');
            return i > 0 && name.Substring(i + 1).All(char.IsDigit) ? name.Substring(0, i) : name;
        }

        /// <summary>把整个装配体数据旋转 G（用于 Y 向上 → Z 向上）。</summary>
        static AsmData Transform(AsmData d, Mat3 G)
        {
            var gt = G.T();
            var r = new AsmData { File = d.File, Name = d.Name, TotalMass = d.TotalMass, HasGeometry = d.HasGeometry };
            r.Notes.AddRange(d.Notes);
            var map = new Dictionary<TopComp, TopComp>();
            foreach (var c in d.Comps)
            {
                var nc = new TopComp { Index = c.Index, Name2 = c.Name2, File = c.File, IsAssembly = c.IsAssembly, Fixed = c.Fixed, Flexible = c.Flexible, FixedInFlex = c.FixedInFlex, InSub = c.InSub };
                foreach (var p in c.Parts)
                {
                    RotateBox(G, p.BoxMin, p.BoxMax, out var mn, out var mx);
                    nc.Parts.Add(new PartInst
                    {
                        Name2 = p.Name2, File = p.File, Config = p.Config, Hidden = p.Hidden, HasMaterial = p.HasMaterial, Material = p.Material,
                        BoxMin = mn, BoxMax = mx, Mass = p.Mass, Com = G * p.Com, Inertia = G * p.Inertia * gt, Top = nc,
                        Pose = new Pose(G * p.Pose.R, G * p.Pose.T), Geom = p.Geom?.Transformed(G, Vec3.Zero),
                    });
                }
                map[c] = nc;
                r.Comps.Add(nc);
            }
            foreach (var (a, b) in d.RigidPairs) r.RigidPairs.Add((map[a], map[b]));
            foreach (var (a, b) in d.UnmatedPairs) r.UnmatedPairs.Add((map[a], map[b]));
            foreach (var m in d.Mates)
            {
                var nm = new MateInfo { Name = m.Name, Type = m.Type, Alignment = m.Alignment, Flipped = m.Flipped, Min = m.Min, Max = m.Max, Value = m.Value, IsLimit = m.IsLimit };
                foreach (var e in m.Ents)
                    nm.Ents.Add(new MateGeom { Kind = e.Kind, P = G * e.P, D = G * e.D, Radius = e.Radius, RefType = e.RefType, Comp = e.Comp != null && map.TryGetValue(e.Comp, out var nc) ? nc : null, CompName = e.CompName });
                r.Mates.Add(nm);
            }
            return r;
        }

        public static void RotateBox(Mat3 G, Vec3 mn, Vec3 mx, out Vec3 omin, out Vec3 omax)
        {
            omin = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            omax = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var p = G * new Vec3((i & 1) == 0 ? mn.X : mx.X, (i & 2) == 0 ? mn.Y : mx.Y, (i & 4) == 0 ? mn.Z : mx.Z);
                omin = new Vec3(Math.Min(omin.X, p.X), Math.Min(omin.Y, p.Y), Math.Min(omin.Z, p.Z));
                omax = new Vec3(Math.Max(omax.X, p.X), Math.Max(omax.Y, p.Y), Math.Max(omax.Z, p.Z));
            }
        }

        sealed class UnionFind
        {
            readonly int[] _p;
            public UnionFind(int n) { _p = Enumerable.Range(0, n).ToArray(); }
            public int Find(int x) { while (_p[x] != x) x = _p[x] = _p[_p[x]]; return x; }
            public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _p[Math.Max(a, b)] = Math.Min(a, b); }
        }
    }
}
