using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SwKit.Core.Urdf;

namespace SwKit.Core.Fea
{
    /// <summary>
    /// 凸边 / 加强筋布局优化的设计区域：在板的上（下）表面外面各加一层（凸边高度），
    /// 原来的板保持不变，优化器决定这一层里哪些地方留下来当筋。
    /// 做法：Gmsh（OpenCASCADE）把板的两个大面沿法向拉伸出凸边层，和原零件合并，
    /// 再按板面切成「下层 / 板 / 上层」三块并切分（共享分界面），网格贴着分界面划分，每个单元都完整地属于某一层。
    /// </summary>
    public static class TopoRibs
    {
        public sealed class Domain
        {
            public string Step;            // 设计区域（三层）：给 Gmsh.Mesh 用的 .geo（读入 STEP 后 Coherence）
            public Vec3 Normal;            // 板的法向（坐标轴方向）
            public double KeepMin, KeepMax;// 原来的板：沿 Normal 的坐标范围（米）
            public double RibHeight;       // 凸边高度（米）
            public int Sides;              // 1 = 只在上面（+Normal 一侧），−1 = 只在下面，2 = 两面
        }

        /// <summary>sides：1 上面、−1 下面、2 两面（「上」= 板法向 +Normal 那一侧）。</summary>
        public static Domain Build(string step, FeMesh m, double ribHeight, int sides, Action<string> log)
        {
            var plate = TopoOpt.DetectPlate(m);
            if (!plate.IsPlate) throw new InvalidOperationException("凸边布局只适用于板件（板厚远小于长宽）");
            var n = plate.Normal;
            int axis = Math.Abs(n.X) > 0.999 ? 0 : Math.Abs(n.Y) > 0.999 ? 1 : Math.Abs(n.Z) > 0.999 ? 2 : -1;
            if (axis < 0) throw new InvalidOperationException("板面要和坐标平面平行（板的法向是 X、Y 或 Z 方向）");
            // 统一成坐标轴正方向
            if ((axis == 0 ? n.X : axis == 1 ? n.Y : n.Z) < 0) n = -n;
            var planes = m.Faces.Where(f => f.IsPlane && f.Area > 0).ToList();
            var pos = planes.Where(f => f.Normal.Dot(n) > 0.995).OrderByDescending(f => f.Area).FirstOrDefault();
            var neg = planes.Where(f => f.Normal.Dot(n) < -0.995).OrderByDescending(f => f.Area).FirstOrDefault();
            if (pos == null || neg == null) throw new InvalidOperationException("找不到板的上下两个大平面");
            double top = pos.Centroid.Dot(n), bot = neg.Centroid.Dot(n);
            if (top <= bot) throw new InvalidOperationException("板的上下表面识别有误");
            m.Bounds(out var bmin, out var bmax);

            // Gmsh 处理中文路径不可靠：在临时文件夹里用英文文件名
            var dir = Path.Combine(Path.GetTempPath(), "swkit_ribs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.Copy(step, Path.Combine(dir, "in.step"), true);
            string F(double v) => (v * 1000).ToString("0.######", CultureInfo.InvariantCulture);   // 米 → mm（Gmsh 读 STEP 用 mm）
            string Vec(double s) => axis == 0 ? $"{F(s)}, 0, 0" : axis == 1 ? $"0, {F(s)}, 0" : $"0, 0, {F(s)}";
            // 沿板法向 [lo, hi]、其他方向盖住整个零件的盒子
            string Box(int tag, double lo, double hi)
            {
                double pad = 0.05;
                var o = new[] { bmin.X - pad, bmin.Y - pad, bmin.Z - pad };
                var d = new[] { bmax.X - bmin.X + 2 * pad, bmax.Y - bmin.Y + 2 * pad, bmax.Z - bmin.Z + 2 * pad };
                o[axis] = lo; d[axis] = hi - lo;
                return $"Box({tag}) = {{{F(o[0])}, {F(o[1])}, {F(o[2])}, {F(d[0])}, {F(d[1])}, {F(d[2])}}};";
            }
            bool up = sides == 1 || sides == 2, down = sides == -1 || sides == 2;
            double h = ribHeight;
            var g = new StringBuilder();
            g.AppendLine("SetFactory(\"OpenCASCADE\");");
            g.AppendLine("Merge \"in.step\";");
            g.AppendLine("v() = Volume{:};");
            var ext = new List<string>();
            if (up) { g.AppendLine($"a[] = Extrude {{{Vec(h)}}} {{ Surface{{{pos.Id}}}; }};"); ext.Add("a[1]"); }
            if (down) { g.AppendLine($"b[] = Extrude {{{Vec(-h)}}} {{ Surface{{{neg.Id}}}; }};"); ext.Add("b[1]"); }
            g.AppendLine($"s() = BooleanUnion{{ Volume{{v()}}; Delete; }}{{ Volume{{{string.Join(", ", ext)}}}; Delete; }};");
            // 切成三层（只切有凸边的一侧）。原来超出凸边高度的部分（例如更高的凸台）会被切掉
            var parts = new List<string>();
            int tag = 9001;
            if (up) { g.AppendLine(Box(tag, top, top + h)); g.AppendLine($"lt() = BooleanIntersection{{ Volume{{s()}}; }}{{ Volume{{{tag}}}; Delete; }};"); parts.Add("lt()"); tag++; }
            g.AppendLine(Box(tag, bot, top)); g.AppendLine($"lc() = BooleanIntersection{{ Volume{{s()}}; {(down ? "" : "Delete; ")}}}{{ Volume{{{tag}}}; Delete; }};"); parts.Add("lc()"); tag++;
            if (down) { g.AppendLine(Box(tag, bot - h, bot)); g.AppendLine($"lb() = BooleanIntersection{{ Volume{{s()}}; Delete; }}{{ Volume{{{tag}}}; Delete; }};"); parts.Add("lb()"); tag++; }
            g.AppendLine($"f() = BooleanFragments{{ Volume{{{string.Join(", ", parts)}}}; Delete; }}{{}};");
            g.AppendLine("Save \"domain.step\";");
            File.WriteAllText(Path.Combine(dir, "domain.geo"), g.ToString(), new UTF8Encoding(false));
            log?.Invoke("正在生成凸边设计区域（Gmsh）");
            var exe = Gmsh.FindExe() ?? throw new InvalidOperationException("找不到 gmsh.exe");
            var psi = new ProcessStartInfo(exe, "domain.geo -0 -v 2") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            string output;
            using (var p = Process.Start(psi))
            {
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(5 * 60 * 1000)) { try { p.Kill(); } catch { } throw new TimeoutException("生成凸边设计区域超时"); }
                output = so.Result + se.Result;
            }
            var outStep = Path.Combine(dir, "domain.step");
            if (!File.Exists(outStep)) throw new InvalidOperationException("生成凸边设计区域失败：" + string.Join(" ", output.Split('\n').Where(l => l.Contains("Error")).Take(3)));
            // STEP 不保存「共享面」：三层读回来是三个互不相连的实体（上面的层等于悬空）。
            // 划网格时用一个 .geo 读入后 Coherence，重合的面重新合并，网格在分界面上共享节点
            var meshGeo = Path.Combine(dir, "mesh.geo");
            File.WriteAllText(meshGeo, "SetFactory(\"OpenCASCADE\");\nMerge \"domain.step\";\nCoherence;\n", new UTF8Encoding(false));
            return new Domain
            {
                Step = meshGeo, Normal = n, KeepMin = bot, KeepMax = top, RibHeight = h, Sides = sides,
            };
        }

        /// <summary>
        /// 把原零件上选的约束 / 受载面对应到设计区域的网格上：孔的圆柱面、侧面会被分层切开，
        /// 按「同类（平面 / 曲面）、法向相同、在板面内的位置相同」找回来（一个原面可能对应几层的几个面）。
        /// 平行于板面的面（板的上下表面）在设计区域里变成了内部面，对应不上，返回在 unmapped 里。
        /// </summary>
        public static List<int> MapFaces(FeMesh from, IEnumerable<int> faces, FeMesh to, Vec3 n, out List<int> unmapped)
        {
            var result = new HashSet<int>();
            unmapped = new List<int>();
            foreach (var id in faces)
            {
                var f = from.Faces.FirstOrDefault(x => x.Id == id);
                if (f == null) { unmapped.Add(id); continue; }
                if (f.IsPlane && Math.Abs(f.Normal.Dot(n)) > 0.9) { unmapped.Add(id); continue; }
                // 原面在板平面内的位置（去掉法向分量）
                var c0 = f.Centroid - n * f.Centroid.Dot(n);
                var nodes = from.FaceNodes(new[] { id });
                double r = nodes.Count == 0 ? 0.002 : nodes.Max(k => { var p = from.P(k); var q = p - n * p.Dot(n); return (q - c0).Length; });
                bool found = false;
                foreach (var g in to.Faces)
                {
                    if (g.IsPlane != f.IsPlane) continue;
                    if (g.IsPlane && g.Normal.Dot(f.Normal) < 0.99) continue;
                    if (!g.IsPlane && Math.Abs(g.Normal.Dot(n)) > 0.5) continue;
                    var c1 = g.Centroid - n * g.Centroid.Dot(n);
                    // 同一个孔的同一半：板面内的形心几乎重合（曲面的形心不随层高变）
                    if ((c1 - c0).Length < Math.Max(2e-4, 0.08 * r))
                    {
                        if (!g.IsPlane && Math.Abs(g.Normal.Normalized().Dot(f.Normal.Normalized())) < 0.95) continue;
                        result.Add(g.Id); found = true;
                    }
                }
                if (!found) unmapped.Add(id);
            }
            return result.ToList();
        }
    }
}
