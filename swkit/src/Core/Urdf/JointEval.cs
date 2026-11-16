using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SwKit.Core.Urdf
{
    /// <summary>
    /// 评估几何推断：把按配合识别出的运动关节当标准答案，和只按几何推断的结果比较。
    /// 匹配条件：轴线方向夹角 ≤ 3°；转动关节的轴线距离 ≤ 2 mm；关节两侧的零件划分相近（较小一侧的零件集合 Jaccard ≥ 0.5）。
    /// </summary>
    public static class JointEval
    {
        public sealed class Match { public Joint Gt, Inf; public double AngleDeg, OffsetMm, Jaccard; }

        public static string Compare(string name, RobotModel gt, RobotModel inf)
        {
            var gtMoving = gt.Joints.Where(j => j.Type != JointType.Fixed).ToList();
            var infMoving = inf.Joints.Where(j => j.Type != JointType.Fixed).ToList();
            var infSuggest = inf.Joints.Where(j => j.Type == JointType.Fixed && j.Inferred && j.Detected != JointType.Fixed).ToList();
            var allParts = gt.Links.SelectMany(l => l.Parts).Select(p => p.Name2).ToList();

            var matches = new List<Match>();
            var usedInf = new HashSet<Joint>();
            foreach (var g in gtMoving)
            {
                Match best = null;
                foreach (var c in infMoving.Concat(infSuggest))
                {
                    if (usedInf.Contains(c)) continue;
                    var m = Score(g, gt, c, inf, allParts.Count);
                    if (m == null) continue;
                    if (best == null || m.Jaccard > best.Jaccard || m.Jaccard == best.Jaccard && m.AngleDeg < best.AngleDeg) best = m;
                }
                if (best != null) { matches.Add(best); usedInf.Add(best.Inf); }
            }
            var found = matches.Where(m => m.Inf.Type != JointType.Fixed).ToList();
            var foundAsSuggestion = matches.Where(m => m.Inf.Type == JointType.Fixed).ToList();
            var fp = infMoving.Where(j => !usedInf.Contains(j)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"## {name}");
            sb.AppendLine();
            sb.AppendLine($"- 标准答案（配合）：{gtMoving.Count} 个运动关节；几何推断：{infMoving.Count} 个启用 + {infSuggest.Count} 个建议（默认不启用）");
            sb.AppendLine($"- 召回：{found.Count}/{gtMoving.Count} 直接启用" + (foundAsSuggestion.Count > 0 ? $"，另有 {foundAsSuggestion.Count} 个在建议里" : "") +
                          (gtMoving.Count > 0 ? $"（{(double)found.Count / gtMoving.Count:P0}；含建议 {(double)matches.Count / gtMoving.Count:P0}）" : ""));
            sb.AppendLine($"- 误报（启用但标准答案里没有）：{fp.Count}");
            if (found.Count > 0)
                sb.AppendLine($"- 轴线误差：方向最大 {found.Max(m => m.AngleDeg):0.##}°，位置最大 {found.Max(m => m.OffsetMm):0.##} mm");
            sb.AppendLine();
            sb.AppendLine("| 标准答案 | 类型 | 轴 | 推断 | 类型 | 置信度 | 方向误差 | 位置误差 | 依据 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var g in gtMoving)
            {
                var m = matches.FirstOrDefault(x => x.Gt == g);
                sb.AppendLine(m == null
                    ? $"| {g.Name} {g.Child.Label} | {Joint.TypeText(g.Type)} | {g.Axis} | 未找到 | | | | | |"
                    : $"| {g.Name} {g.Child.Label} | {Joint.TypeText(g.Type)} | {g.Axis} | {m.Inf.Name} {m.Inf.Child.Label} | {(m.Inf.Type == JointType.Fixed ? "建议·" + Joint.TypeText(m.Inf.Detected) : Joint.TypeText(m.Inf.Type))} | {m.Inf.Confidence:0.00} | {m.AngleDeg:0.##}° | {m.OffsetMm:0.##} mm | {Esc(m.Inf.Evidence)} |");
            }
            foreach (var j in fp)
                sb.AppendLine($"| （误报） | | | {j.Name} {j.Parent.Label}→{j.Child.Label} | {Joint.TypeText(j.Type)} | {j.Confidence:0.00} | | | {Esc(j.Evidence)} |");
            sb.AppendLine();
            return sb.ToString();
        }

        static string Esc(string s) => (s ?? "").Replace("|", "/");

        static Match Score(Joint g, RobotModel gm, Joint c, RobotModel cm, int total)
        {
            bool gRot = g.Type != JointType.Prismatic, cRot = (c.Type == JointType.Fixed ? c.Detected : c.Type) != JointType.Prismatic;
            if (gRot != cRot) return null;
            var cos = Math.Abs(g.Axis.Normalized().Dot(c.Axis.Normalized()));
            var ang = Math.Acos(Math.Min(1, cos)) * 180 / Math.PI;
            if (ang > 3) return null;
            double off = 0;
            if (gRot)
            {
                var d = g.Axis.Normalized();
                var v = c.AxisPoint - g.AxisPoint;
                off = (v - d * v.Dot(d)).Length * 1000;
                if (off > 2) return null;
            }
            var sg = Side(g, gm, total); var sc = Side(c, cm, total);
            var inter = sg.Count(sc.Contains);
            var union = sg.Count + sc.Count - inter;
            // 两侧可能互换（基座选得不同）
            var scc = new HashSet<string>(cm.Links.SelectMany(l => l.Parts).Select(p => p.Name2).Where(n => !sc.Contains(n)));
            var inter2 = sg.Count(scc.Contains);
            var union2 = sg.Count + scc.Count - inter2;
            var jac = Math.Max(union > 0 ? (double)inter / union : 0, union2 > 0 ? (double)inter2 / union2 : 0);
            if (jac < 0.5) return null;
            return new Match { Gt = g, Inf = c, AngleDeg = ang, OffsetMm = off, Jaccard = jac };
        }

        /// <summary>关节带动的零件（子连杆及其后代）；如果超过一半，取另一侧。</summary>
        static HashSet<string> Side(Joint j, RobotModel m, int total)
        {
            var links = new HashSet<Link> { j.Child };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var x in m.Joints) if (links.Contains(x.Parent) && links.Add(x.Child)) grew = true;
            }
            var set = new HashSet<string>(links.SelectMany(l => l.Parts).Select(p => p.Name2));
            if (set.Count > total / 2)
                set = new HashSet<string>(m.Links.SelectMany(l => l.Parts).Select(p => p.Name2).Where(n => !set.Contains(n)));
            return set;
        }
    }
}
