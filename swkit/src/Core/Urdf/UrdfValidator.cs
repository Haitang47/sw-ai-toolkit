using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SwKit.Core.Urdf
{
    public enum CheckLevel { Pass, Info, Warn, Error }

    public sealed class CheckItem
    {
        public CheckLevel Level;
        public string Category;
        public string Text;
        public static string LevelText(CheckLevel l) => l == CheckLevel.Pass ? "通过" : l == CheckLevel.Info ? "提示" : l == CheckLevel.Warn ? "警告" : "错误";
    }

    public sealed class ValidationReport
    {
        public List<CheckItem> Items = new List<CheckItem>();
        public int Errors => Items.Count(i => i.Level == CheckLevel.Error);
        public int Warnings => Items.Count(i => i.Level == CheckLevel.Warn);
        public void Add(CheckLevel l, string cat, string text) => Items.Add(new CheckItem { Level = l, Category = cat, Text = text });
        public string Summary => Errors > 0 ? $"有 {Errors} 个错误、{Warnings} 个警告" : Warnings > 0 ? $"通过，有 {Warnings} 个警告需要确认" : "全部通过";
    }

    /// <summary>
    /// 导出后的校验。关键是"独立复核"：重新读入写出的 URDF 文件（而不是用内存里的模型），
    /// 做零位正运动学，把每个零件的网格位置和 SolidWorks 里的实际位置逐一比较。
    /// </summary>
    public static class UrdfValidator
    {
        sealed class ULink { public string Name; public double Mass; public double[] I; public string Mesh, CollisionMesh; public bool HasInertial; }
        sealed class UJoint { public string Name, Type, Parent, Child; public Vec3 Xyz, Rpy, Axis = new Vec3(1, 0, 0); public double? Lower, Upper, Effort, Velocity; }

        static double D(XAttribute a, double def = 0) => a == null ? def : double.Parse(a.Value, CultureInfo.InvariantCulture);
        static Vec3 V3(XAttribute a, Vec3 def)
        {
            if (a == null) return def;
            var p = a.Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            return new Vec3(p[0], p[1], p[2]);
        }

        public static ValidationReport Validate(string urdfPath, string pkgDir, RobotModel model, MeshResult meshes, AsmData raw)
        {
            var r = new ValidationReport();
            XElement robot;
            try { robot = XDocument.Load(urdfPath).Root; }
            catch (Exception ex) { r.Add(CheckLevel.Error, "文件", "URDF 无法解析：" + ex.Message); return r; }

            // ---------- 解析 ----------
            var links = robot.Elements("link").Select(l =>
            {
                var inertial = l.Element("inertial");
                var I = inertial?.Element("inertia");
                return new ULink
                {
                    Name = (string)l.Attribute("name"),
                    HasInertial = inertial != null,
                    Mass = D(inertial?.Element("mass")?.Attribute("value")),
                    I = I == null ? null : new[] { D(I.Attribute("ixx")), D(I.Attribute("ixy")), D(I.Attribute("ixz")), D(I.Attribute("iyy")), D(I.Attribute("iyz")), D(I.Attribute("izz")) },
                    Mesh = (string)l.Element("visual")?.Element("geometry")?.Element("mesh")?.Attribute("filename"),
                    CollisionMesh = (string)l.Element("collision")?.Element("geometry")?.Element("mesh")?.Attribute("filename"),
                };
            }).ToList();
            var joints = robot.Elements("joint").Select(j => new UJoint
            {
                Name = (string)j.Attribute("name"),
                Type = (string)j.Attribute("type"),
                Parent = (string)j.Element("parent")?.Attribute("link"),
                Child = (string)j.Element("child")?.Attribute("link"),
                Xyz = V3(j.Element("origin")?.Attribute("xyz"), Vec3.Zero),
                Rpy = V3(j.Element("origin")?.Attribute("rpy"), Vec3.Zero),
                Axis = V3(j.Element("axis")?.Attribute("xyz"), new Vec3(1, 0, 0)),
                Lower = j.Element("limit")?.Attribute("lower") is XAttribute lo ? D(lo) : (double?)null,
                Upper = j.Element("limit")?.Attribute("upper") is XAttribute hi ? D(hi) : (double?)null,
                Effort = j.Element("limit")?.Attribute("effort") is XAttribute ef ? D(ef) : (double?)null,
                Velocity = j.Element("limit")?.Attribute("velocity") is XAttribute ve ? D(ve) : (double?)null,
            }).ToList();

            // ---------- 结构 ----------
            var ok = true;
            var linkNames = new HashSet<string>();
            foreach (var l in links) if (!linkNames.Add(l.Name)) { r.Add(CheckLevel.Error, "结构", $"连杆名重复：{l.Name}"); ok = false; }
            var jointNames = new HashSet<string>();
            foreach (var j in joints) if (!jointNames.Add(j.Name)) { r.Add(CheckLevel.Error, "结构", $"关节名重复：{j.Name}"); ok = false; }
            foreach (var j in joints)
                if (!linkNames.Contains(j.Parent) || !linkNames.Contains(j.Child)) { r.Add(CheckLevel.Error, "结构", $"关节 {j.Name} 引用了不存在的连杆"); ok = false; }
            foreach (var g in joints.GroupBy(j => j.Child).Where(g => g.Count() > 1)) { r.Add(CheckLevel.Error, "结构", $"连杆 {g.Key} 有多个父关节"); ok = false; }
            var roots = links.Where(l => !joints.Any(j => j.Child == l.Name)).ToList();
            if (roots.Count != 1) { r.Add(CheckLevel.Error, "结构", $"应当只有一个根连杆，实际有 {roots.Count} 个"); ok = false; }

            var poses = new Dictionary<string, Pose>();
            if (roots.Count == 1)
            {
                poses[roots[0].Name] = Pose.Identity;
                var queue = new Queue<string>(); queue.Enqueue(roots[0].Name);
                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    foreach (var j in joints.Where(x => x.Parent == cur && !poses.ContainsKey(x.Child)))
                    {
                        poses[j.Child] = poses[cur] * new Pose(Rpy(j.Rpy), j.Xyz);
                        queue.Enqueue(j.Child);
                    }
                }
                var unreached = links.Where(l => !poses.ContainsKey(l.Name)).ToList();
                if (unreached.Count > 0) { r.Add(CheckLevel.Error, "结构", "以下连杆没有连到根上：" + string.Join("、", unreached.Select(l => l.Name))); ok = false; }
            }
            var badNames = links.Select(l => l.Name).Concat(joints.Select(j => j.Name)).Where(n => !Regex.IsMatch(n ?? "", "^[A-Za-z_][A-Za-z0-9_]*$")).ToList();
            if (badNames.Count > 0) r.Add(CheckLevel.Warn, "结构", "这些名字含有字母、数字、下划线以外的字符，部分 ROS 工具不支持：" + string.Join("、", badNames));
            if (ok) r.Add(CheckLevel.Pass, "结构", $"{links.Count} 个连杆、{joints.Count} 个关节，组成一棵以 {roots[0].Name} 为根的树");

            // ---------- 惯性 ----------
            var inertiaOk = true;
            foreach (var l in links)
            {
                if (!l.HasInertial)
                {
                    if (l.Mesh != null) { r.Add(CheckLevel.Warn, "惯性", $"{l.Name} 有几何但没有质量（零件可能没有实体，或质量读取失败）"); inertiaOk = false; }
                    continue;
                }
                if (l.Mass <= 0) { r.Add(CheckLevel.Error, "惯性", $"{l.Name} 的质量不是正数：{l.Mass}"); inertiaOk = false; continue; }
                var I = l.I;
                var m = new double[3, 3] { { I[0], I[1], I[2] }, { I[1], I[3], I[4] }, { I[2], I[4], I[5] } };
                var ev = LinAlg.SymEigen(m, out _).OrderBy(x => x).ToArray();
                if (ev[0] <= 0) { r.Add(CheckLevel.Error, "惯性", $"{l.Name} 的惯性矩阵不是正定的（最小主惯量 {ev[0]:G4}）"); inertiaOk = false; }
                else if (ev[0] + ev[1] < ev[2] * (1 - 1e-6)) { r.Add(CheckLevel.Error, "惯性", $"{l.Name} 的主惯量不满足三角不等式，物理上不可能"); inertiaOk = false; }
            }
            if (inertiaOk) r.Add(CheckLevel.Pass, "惯性", "所有连杆质量为正，惯性矩阵正定且满足三角不等式");

            var allParts = model.Links.SelectMany(l => l.Members).SelectMany(m => m.Parts).ToList();
            // 读取是否正确：所有读到的零件（含隐藏的）合计应等于 SolidWorks 的装配体总质量（SolidWorks 也计入隐藏零件，不计压缩的）
            var readSum = raw.Comps.SelectMany(c => c.Parts).Sum(p => p.Mass);
            var exported = allParts.Where(p => !p.Hidden).Sum(p => p.Mass);
            if (raw.TotalMass > 0)
            {
                var rel = Math.Abs(readSum - raw.TotalMass) / raw.TotalMass;
                if (rel > 0.01) r.Add(CheckLevel.Warn, "惯性", $"读到的零件质量合计 {readSum:0.####} kg 与 SolidWorks 装配体总质量 {raw.TotalMass:0.####} kg 相差 {rel:P1}，可能有零件的配置或质量没有读对");
                else r.Add(CheckLevel.Pass, "惯性", $"零件质量合计 {readSum:0.####} kg，与 SolidWorks 装配体总质量一致");
            }
            // （model 里的零件是坐标变换后的副本，按名字对应）
            var exportedNames = new HashSet<string>(allParts.Where(p => !p.Hidden).Select(p => p.Name2));
            var hiddenParts = raw.Comps.SelectMany(c => c.Parts).Where(p => !exportedNames.Contains(p.Name2)).ToList();
            if (hiddenParts.Count > 0)
                r.Add(CheckLevel.Info, "惯性", $"URDF 总质量 {exported:0.####} kg；{hiddenParts.Count} 个隐藏的零件（共 {hiddenParts.Sum(p => p.Mass):0.####} kg）没有计入质量和网格：" + string.Join("、", hiddenParts.Select(p => p.Name2).Take(12)) + (hiddenParts.Count > 12 ? " 等" : ""));
            var noMat = allParts.Where(p => !p.Hidden && !p.HasMaterial).Select(p => JointSolver.CleanName(p.Short)).Distinct().ToList();
            if (noMat.Count > 0) r.Add(CheckLevel.Warn, "惯性", $"{noMat.Count} 种零件没有指定材料，质量按默认密度 1000 kg/m³ 计算，可能和实物差很多：" + string.Join("、", noMat.Take(12)) + (noMat.Count > 12 ? " 等" : ""));

            // ---------- 网格 ----------
            var meshOk = true;
            string Resolve(string m) => m.StartsWith("package://") ? Path.Combine(pkgDir, m.Substring(m.IndexOf('/', "package://".Length) + 1).Replace('/', Path.DirectorySeparatorChar)) : Path.Combine(Path.GetDirectoryName(urdfPath), m);
            foreach (var l in links.Where(l => l.CollisionMesh != null && !File.Exists(Resolve(l.CollisionMesh))))
            {
                r.Add(CheckLevel.Error, "网格", $"{l.Name} 的碰撞网格文件不存在：{l.CollisionMesh}");
                meshOk = false;
            }
            foreach (var l in links.Where(l => l.Mesh != null))
            {
                var path = Resolve(l.Mesh);
                if (!File.Exists(path)) { r.Add(CheckLevel.Error, "网格", $"{l.Name} 的网格文件不存在：{l.Mesh}"); meshOk = false; continue; }
                var mesh = Mesh.ReadStl(path);
                if (mesh.Triangles == 0 || !mesh.Bounds(out var mn, out var mx)) { r.Add(CheckLevel.Error, "网格", $"{l.Name} 的网格是空的"); meshOk = false; continue; }
                var diag = (mx - mn).Length;
                if (diag > 10) { r.Add(CheckLevel.Error, "网格", $"{l.Name} 的网格尺寸 {diag:0.#} m，单位很可能错了（毫米当成了米）"); meshOk = false; }
            }
            if (meshes.Missing.Count > 0)
            {
                meshOk = false;
                r.Add(CheckLevel.Warn, "网格", $"{meshes.Missing.Count} 个零件没有找到对应的网格，它们不会显示（质量和惯性仍然计入）：" + string.Join("、", meshes.Missing.Take(8)) + (meshes.Missing.Count > 8 ? " 等" : ""));
                if (meshes.Unmatched.Count > 0)
                    r.Add(CheckLevel.Info, "网格", $"SolidWorks 导出了但没对应上零件的网格文件 {meshes.Unmatched.Count} 个：" + string.Join("、", meshes.Unmatched.Take(8)) + (meshes.Unmatched.Count > 8 ? " 等" : ""));
            }
            if (meshOk) r.Add(CheckLevel.Pass, "网格", $"{links.Count(l => l.Mesh != null)} 个连杆网格和碰撞网格都已生成，单位为米");
            var simplified = meshes.TriangleCounts.Where(kv => meshes.SourceTriangles.TryGetValue(kv.Key, out var s) && s > kv.Value).Select(kv => kv.Key.Name).ToList();
            r.Add(CheckLevel.Info, "网格", $"外观网格共 {meshes.TriangleCounts.Values.Sum():N0} 个三角形" +
                (simplified.Count > 0 ? $"（{string.Join("、", simplified)} 细节太多，已简化到每个连杆不超过 {MeshBuilder.MaxVisualTriangles:N0} 个）" : "") +
                $"；碰撞网格单独简化到每个连杆不超过 {MeshBuilder.MaxCollisionTriangles:N0} 个，共 {meshes.CollisionTriangles.Values.Sum():N0} 个");

            // ---------- 零位一致性 ----------
            // ① 坐标系：按读回的 URDF 做零位正运动学，把每个零件的网格放回去，应与 SolidWorks 导出时的位置完全一致。
            //    这检验关节原点、连杆坐标系和 URDF 的写出 / 读回。
            // ② 质心：网格的体积质心与 SolidWorks 算出的零件质心比较，独立检验网格的坐标和单位。
            //    个别零件对不上通常是零件自己的原因（质量覆盖、隐藏实体、多种材料）；大多数都对不上才说明坐标或单位错了。
            //    （不用 SolidWorks 的包围盒：零件斜放时它会偏大。）
            double frameWorst = 0, comWorst = 0; int framed = 0, comChecked = 0, densityOdd = 0;
            var frameBad = new List<string>();
            var comBad = new List<(PartInst part, double dev)>();
            foreach (var link in model.Links)
            {
                if (!poses.TryGetValue(link.Name, out var pose)) continue;
                foreach (var part in link.Parts)
                {
                    if (!meshes.PartCentroids.TryGetValue(part, out var cv)) continue;
                    var expected = cv.centroid + link.Origin;   // 网格在 SolidWorks 里的位置（连杆坐标系与世界坐标轴平行）
                    var fd = (pose.Apply(cv.centroid) - expected).Length;
                    framed++;
                    frameWorst = Math.Max(frameWorst, fd);
                    if (fd > 1e-4) frameBad.Add($"{part.Name2}（{fd * 1000:0.###} mm）");

                    if (part.Mass <= 0) continue;
                    var dev = (expected - part.Com).Length;
                    var size = (part.BoxMax - part.BoxMin).Length;
                    comChecked++;
                    if (dev > Math.Max(0.001, 0.01 * size)) comBad.Add((part, dev));
                    else comWorst = Math.Max(comWorst, dev);
                    var density = part.Mass / cv.volume;
                    if (density < 30 || density > 30000) densityOdd++;
                }
            }
            if (frameBad.Count > 0)
                r.Add(CheckLevel.Error, "零位", $"按 URDF 计算零位时，{frameBad.Count} 个零件没有回到 SolidWorks 里的位置：" + string.Join("、", frameBad.Take(8)) + (frameBad.Count > 8 ? " 等" : ""));
            else if (framed > 0)
                r.Add(CheckLevel.Pass, "零位", $"重新读入 URDF 计算零位姿态：{framed} 个零件都回到 SolidWorks 里的位置（最大偏差 {frameWorst * 1000:0.####} mm）");

            if (comChecked > 0)
            {
                var list = string.Join("、", comBad.OrderByDescending(b => b.dev).Take(8).Select(b => $"{JointSolver.CleanName(b.part.Short)}（{b.dev * 1000:0.#} mm）")) + (comBad.Count > 8 ? " 等" : "");
                if (comBad.Count == 0)
                    r.Add(CheckLevel.Pass, "质心", $"{comChecked} 个零件网格的质心都与 SolidWorks 算的质心一致（最大偏差 {comWorst * 1000:0.###} mm），网格的坐标和单位正确");
                else if (comChecked >= 2 && comBad.Count * 2 > comChecked)
                    r.Add(CheckLevel.Error, "质心", $"大多数零件（{comBad.Count}/{comChecked}）网格的质心与 SolidWorks 算的质心对不上，网格的坐标或单位可能错了：{list}");
                else
                    r.Add(CheckLevel.Warn, "质心", $"{comBad.Count} 个零件网格的质心与 SolidWorks 算的质心对不上：{list}。"
                        + (comChecked > comBad.Count ? $"其余 {comChecked - comBad.Count} 个零件都一致，坐标系没有问题。" : "")
                        + "差异通常来自零件设置了质量覆盖、含有隐藏的实体或用了多种材料；URDF 里的质量和质心用的是 SolidWorks 的数值");
            }
            if (densityOdd > 0)
                r.Add(CheckLevel.Warn, "网格", $"{densityOdd} 个零件的网格体积和质量对不上（密度不在常见范围内），可能是网格不封闭或质量被覆盖");

            // ---------- 关节 ----------
            var jointOk = true;
            foreach (var j in joints)
            {
                if (j.Type == "fixed") continue;
                if (Math.Abs(j.Axis.Length - 1) > 1e-6) { r.Add(CheckLevel.Error, "关节", $"{j.Name} 的轴不是单位向量"); jointOk = false; }
                if ((j.Type == "revolute" || j.Type == "prismatic") && (j.Lower == null || j.Upper == null || j.Lower >= j.Upper)) { r.Add(CheckLevel.Error, "关节", $"{j.Name} 的上下限无效"); jointOk = false; }
                if (j.Effort == null || j.Effort <= 0 || j.Velocity == null || j.Velocity <= 0) { r.Add(CheckLevel.Error, "关节", $"{j.Name} 缺少力/速度限制"); jointOk = false; }
            }
            if (jointOk) r.Add(CheckLevel.Pass, "关节", "所有运动关节的轴为单位向量，上下限和力/速度限制有效");

            // 运动是否和装配体一致：URDF 里每个运动关节的轴（换到世界坐标）应与配合给出的轴线重合；
            // 转动关节绕的是过子连杆原点的直线，所以子连杆原点必须在这条轴线上。
            var axisBad = new List<string>(); int axisChecked = 0;
            foreach (var mj in model.Joints.Where(x => x.Type != JointType.Fixed))
            {
                var pj = joints.FirstOrDefault(x => x.Name == mj.Name);
                if (pj == null || !poses.TryGetValue(mj.Child.Name, out var cp)) continue;
                axisChecked++;
                var d = mj.Axis.Normalized();
                var worldAxis = (cp.R * pj.Axis).Normalized();
                var rel = cp.T - mj.AxisPoint;
                var dist = mj.Type == JointType.Prismatic ? 0 : (rel - d * rel.Dot(d)).Length;
                if (worldAxis.Dot(d) < 1 - 1e-6) axisBad.Add($"{mj.Name} 的轴方向与配合不一致");
                else if (dist > 1e-4) axisBad.Add($"{mj.Name} 的转轴偏离配合给出的轴线 {dist * 1000:0.###} mm");
            }
            if (axisBad.Count > 0) foreach (var t in axisBad) r.Add(CheckLevel.Error, "关节", t);
            else if (axisChecked > 0) r.Add(CheckLevel.Pass, "关节", model.Joints.Any(x => x.Inferred && x.Type != JointType.Fixed)
                ? $"{axisChecked} 个运动关节的轴线都与识别出的轴线（配合 / 零件几何）重合，转动时零件会绕正确的轴运动"
                : $"{axisChecked} 个运动关节的轴线都与 SolidWorks 配合给出的轴线重合，转动时零件会绕正确的轴运动");
            var inferredJoints = model.Joints.Where(x => x.Inferred && x.Type != JointType.Fixed).Select(x => x.Name).ToList();
            if (inferredJoints.Count > 0)
                r.Add(CheckLevel.Warn, "关节", $"{inferredJoints.Count} 个关节是按零件几何推断的（装配体里没有对应的配合）：" + string.Join("、", inferredJoints.Take(12)) + "，请在预览里确认转轴和方向");
            foreach (var j in model.Joints)
            {
                foreach (var n in j.Notes) r.Add(j.NeedsReview ? CheckLevel.Warn : CheckLevel.Info, "关节", $"{j.Name}：{n}");
                if (j.NeedsReview && j.Notes.Count == 0) r.Add(CheckLevel.Warn, "关节", $"{j.Name}：自动识别结果需要人工确认");
                if (j.Type == JointType.Revolute && !j.LimitsFromMate) r.Add(CheckLevel.Info, "关节", $"{j.Name}：没有找到角度限位配合，使用默认范围 ±180°");
                if (j.Type == JointType.Prismatic && !j.LimitsFromMate && !j.Notes.Any(n => n.StartsWith("行程按"))) r.Add(CheckLevel.Warn, "关节", $"{j.Name}：没有找到距离限位配合，使用默认行程 ±50 mm，请按实际修改");
            }
            if (model.Joints.Any(j => j.Type != JointType.Fixed))
                r.Add(CheckLevel.Info, "关节", "力矩和速度上限是默认值（转动 10 N·m、3.14 rad/s；移动 100 N、0.5 m/s），做仿真或控制前请按电机参数修改");

            foreach (var w in model.Warnings) r.Add(CheckLevel.Warn, "配合", w);
            foreach (var i in model.Infos) r.Add(CheckLevel.Info, "配合", i);
            return r;
        }

        /// <summary>URDF 的 rpy：R = Rz(yaw)·Ry(pitch)·Rx(roll)。</summary>
        static Mat3 Rpy(Vec3 rpy) =>
            Mat3.Rotation(new Vec3(0, 0, 1), rpy.Z) * Mat3.Rotation(new Vec3(0, 1, 0), rpy.Y) * Mat3.Rotation(new Vec3(1, 0, 0), rpy.X);

        public static string ToMarkdown(ValidationReport r, RobotModel m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# URDF 校验报告");
            sb.AppendLine();
            sb.AppendLine($"- 装配体：{m.AssemblyName}");
            sb.AppendLine($"- 机器人名：{m.RobotName}");
            sb.AppendLine($"- 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"- 结果：**{r.Summary}**");
            sb.AppendLine();
            sb.AppendLine("## 检查结果");
            sb.AppendLine();
            sb.AppendLine("| 结果 | 类别 | 内容 |");
            sb.AppendLine("|---|---|---|");
            foreach (var i in r.Items.OrderByDescending(i => i.Level)) sb.AppendLine($"| {CheckItem.LevelText(i.Level)} | {i.Category} | {i.Text.Replace("|", "/")} |");
            sb.AppendLine();
            sb.AppendLine("## 连杆");
            sb.AppendLine();
            sb.AppendLine("| 连杆 | 说明 | 包含的零部件 | 质量 (kg) |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var l in m.Links) sb.AppendLine($"| {l.Name} | {l.Label} | {string.Join("、", l.Members.Select(x => x.Name2))} | {l.Mass:0.####} |");
            sb.AppendLine();
            sb.AppendLine("## 关节");
            sb.AppendLine();
            sb.AppendLine("| 关节 | 类型 | 父 → 子 | 轴 | 下限 | 上限 | 来源 |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var j in m.Joints)
            {
                string Lim(double v) => j.Type == JointType.Revolute ? $"{v * 180 / Math.PI:0.#}°" : j.Type == JointType.Prismatic ? $"{v * 1000:0.#} mm" : "—";
                sb.AppendLine($"| {j.Name} | {Joint.TypeText(j.Type)} | {j.Parent.Name} → {j.Child.Name}（{j.Child.Label}） | {(j.Type == JointType.Fixed ? "—" : j.Axis.Fmt("0.###"))} | {Lim(j.Lower)} | {Lim(j.Upper)} | {j.Source} |");
            }
            return sb.ToString();
        }
    }
}
