using System;
using System.IO;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;

namespace SwKit.Core.Urdf
{
    public sealed class UrdfExportResult
    {
        public string PackageDir, UrdfPath, ReportPath;
        public ValidationReport Report;
        public MeshResult Meshes;
        /// <summary>实际写进 URDF 的模型（合并固定连接之后）。</summary>
        public RobotModel Model;
    }

    /// <summary>URDF 导出的流程：打开 → 分析 →（用户确认 / 修改）→ 导出网格和文件 → 校验。在 SolidWorks 线程上调用。</summary>
    public static class UrdfExporter
    {
        public static ModelDoc2 OpenAssembly(ISldWorks app, string path)
        {
            path = Path.GetFullPath(path);
            var doc = app.GetOpenDocumentByName(path) as ModelDoc2;
            if (doc != null) return doc;
            int e = 0, w = 0;
            doc = app.OpenDoc6(path, 2, 1 | 2, "", ref e, ref w);   // 静默、只读
            if (doc == null) throw new InvalidOperationException("打开装配体失败：" + ExportRunner.LoadErrorText(e));
            return doc;
        }

        public static RobotModel Analyze(ISldWorks app, ModelDoc2 doc, string upAxis, out AsmData raw, Action<string> log, bool expandSubs = true, InferMode infer = InferMode.Auto)
        {
            log?.Invoke("正在读取装配体的零部件、配合、质量和几何……");
            raw = AssemblyReader.Read(app, doc, log, expandSubs, infer != InferMode.Off);
            log?.Invoke(infer == InferMode.GeometryOnly ? "正在根据零件几何推断关节……" : "正在根据配合推断关节……");
            return Solve(raw, upAxis, infer, log);
        }

        /// <summary>用已经读出的装配体数据重新识别关节（切换「按几何推断 / 忽略配合」时不用再打开 SolidWorks）。</summary>
        public static RobotModel Solve(AsmData raw, string upAxis, InferMode infer, Action<string> log = null)
        {
            var model = JointSolver.Solve(raw, upAxis, infer);
            var name = UrdfWriter.SanitizeName(raw.Name);
            model.RobotName = name.StartsWith("robot") && !raw.Name.Any(c => c < 128 && char.IsLetter(c)) ? "my_robot" : name;
            log?.Invoke($"识别出 {model.Links.Count} 个连杆、{model.Joints.Count(j => j.Type != JointType.Fixed)} 个运动关节" + (model.InferredCount > 0 ? $"（其中 {model.InferredCount} 个由几何推断）" : ""));
            return model;
        }

        /// <summary>
        /// 导出。partMeshes 可以传入分析时已经导出的零件网格（界面版的预览就用它），为空时现场导出。
        /// mergeFixed：固定关节连着的连杆并入上级连杆（螺钉、垫片、没约束全的零件不再各占一个连杆），URDF 更干净。
        /// </summary>
        public static UrdfExportResult Export(ISldWorks app, ModelDoc2 doc, AsmData raw, RobotModel model, string outDir, Action<string> log,
                                              System.Collections.Generic.Dictionary<string, Mesh> partMeshes = null, bool mergeFixed = true)
        {
            model.RecomputeFrames();
            if (mergeFixed) model = MergeFixed(model);
            var pkgDir = Path.Combine(outDir, UrdfWriter.PackageName(model));
            var meshDir = Path.Combine(pkgDir, "meshes");
            if (Directory.Exists(meshDir))
                foreach (var f in Directory.GetFiles(meshDir, "*.stl")) File.Delete(f);   // 只清掉上次导出的网格
            var colDir = Path.Combine(meshDir, "collision");
            if (Directory.Exists(colDir))
                foreach (var f in Directory.GetFiles(colDir, "*.stl")) File.Delete(f);
            if (partMeshes == null)
            {
                log?.Invoke("正在导出各零件的网格……");
                partMeshes = MeshBuilder.ExportParts(app, doc, model.UpAxis, log, raw?.AllParts);
            }
            var meshes = MeshBuilder.Assemble(model, partMeshes, meshDir);
            log?.Invoke("正在写出 URDF 功能包……");
            var urdf = UrdfWriter.Write(model, meshes, pkgDir);
            log?.Invoke("正在校验导出结果……");
            var report = UrdfValidator.Validate(urdf, pkgDir, model, meshes, raw);
            var reportPath = Path.Combine(pkgDir, "校验报告.md");
            File.WriteAllText(reportPath, UrdfValidator.ToMarkdown(report, model), new UTF8Encoding(false));
            return new UrdfExportResult { PackageDir = pkgDir, UrdfPath = urdf, ReportPath = reportPath, Report = report, Meshes = meshes, Model = model };
        }

        /// <summary>
        /// 生成一个新模型：每个通过固定关节挂着的连杆并入最近的、通过运动关节连接的上级连杆（或基座）。
        /// 连杆和关节保留原来的名字（和界面里一致，编号可能不连续）。不修改传入的模型。
        /// </summary>
        public static RobotModel MergeFixed(RobotModel m)
        {
            var parentJoint = m.Joints.ToDictionary(j => j.Child);
            Link Owner(Link l)
            {
                while (parentJoint.TryGetValue(l, out var pj) && pj.Type == JointType.Fixed) l = pj.Parent;
                return l;
            }
            var r = new RobotModel { AssemblyName = m.AssemblyName, RobotName = m.RobotName, UpAxis = m.UpAxis, InferUsed = m.InferUsed };
            r.Warnings.AddRange(m.Warnings);
            r.Infos.AddRange(m.Infos);
            r.Suggestions.AddRange(m.Suggestions);
            var map = new System.Collections.Generic.Dictionary<Link, Link>();
            foreach (var l in m.Links)
            {
                var owner = Owner(l);
                if (!map.TryGetValue(owner, out var nl))
                {
                    nl = new Link { Name = owner.Name, Label = owner.Label };
                    map[owner] = nl;
                    r.Links.Add(nl);
                }
                nl.Members.AddRange(l.Members);
            }
            var merged = 0;
            foreach (var j in m.Joints)
            {
                if (j.Type == JointType.Fixed) { merged++; continue; }
                var nj = new Joint
                {
                    Name = j.Name, Type = j.Type, Detected = j.Detected, SpinCandidate = j.SpinCandidate,
                    Parent = map[Owner(j.Parent)], Child = map[j.Child], AxisPoint = j.AxisPoint, Axis = j.Axis,
                    Lower = j.Lower, Upper = j.Upper, Effort = j.Effort, Velocity = j.Velocity,
                    LimitsFromMate = j.LimitsFromMate, Source = j.Source, NeedsReview = j.NeedsReview,
                    Inferred = j.Inferred, Confidence = j.Confidence, Evidence = j.Evidence,
                };
                nj.Notes.AddRange(j.Notes);
                r.Joints.Add(nj);
            }
            if (merged > 0) r.Infos.Add($"{merged} 个固定连接已并入上级连杆，URDF 里共 {r.Links.Count} 个连杆、{r.Joints.Count} 个运动关节");
            r.RecomputeFrames();
            return r;
        }

        /// <summary>分析结果的文字摘要（命令行和日志用）。</summary>
        public static string Describe(RobotModel m)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"连杆（{m.Links.Count}）：");
            foreach (var l in m.Links)
                sb.AppendLine($"  {l.Name,-10} {l.Label}  质量 {l.Mass:0.####} kg  原点 {l.Origin}  ← {string.Join("、", l.Members.Select(x => x.Name2))}");
            sb.AppendLine($"关节（{m.Joints.Count}）：");
            foreach (var j in m.Joints)
            {
                var lim = j.Type == JointType.Revolute ? $"{j.Lower * 180 / Math.PI:0.#}° ~ {j.Upper * 180 / Math.PI:0.#}°" : j.Type == JointType.Prismatic ? $"{j.Lower * 1000:0.#} ~ {j.Upper * 1000:0.#} mm" : "";
                var inferred = j.Inferred || j.Evidence != null ? $"  [几何推断 {j.Confidence:0.00}{(j.Type == JointType.Fixed ? "，未启用：" + Joint.TypeText(j.Detected) : "")}]" : "";
                sb.AppendLine($"  {j.Name,-8} {Joint.TypeText(j.Type),-4} {j.Parent.Name} → {j.Child.Name}  轴 {j.Axis}  {lim}{(j.LimitsFromMate ? "（来自限位配合）" : "")}{inferred}");
                sb.AppendLine($"           来源：{j.Source}");
                foreach (var n in j.Notes) sb.AppendLine($"           注意：{n}");
            }
            foreach (var w in m.Warnings) sb.AppendLine("警告：" + w);
            foreach (var i in m.Infos) sb.AppendLine("提示：" + i);
            foreach (var s in m.Suggestions) sb.AppendLine("建议：" + s);
            return sb.ToString();
        }
    }
}
