using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;

namespace SwKit.Core.Urdf
{
    /// <summary>三角网格（每个三角形 9 个 float，单位米）。</summary>
    public sealed class Mesh
    {
        public List<float> Data = new List<float>();
        public int Triangles => Data.Count / 9;

        public static Mesh ReadStl(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var m = new Mesh();
            if (bytes.Length >= 84)
            {
                var n = BitConverter.ToUInt32(bytes, 80);
                if (84 + (long)n * 50 == bytes.Length)
                {
                    for (int t = 0; t < n; t++)
                    {
                        int o = 84 + t * 50 + 12;
                        for (int k = 0; k < 9; k++) m.Data.Add(BitConverter.ToSingle(bytes, o + k * 4));
                    }
                    return m;
                }
            }
            // 文本格式
            foreach (var line in Encoding.ASCII.GetString(bytes).Split('\n'))
            {
                var s = line.Trim();
                if (!s.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                for (int k = 1; k <= 3; k++) m.Data.Add(float.Parse(parts[k], System.Globalization.CultureInfo.InvariantCulture));
            }
            return m;
        }

        public void WriteBinary(string path, string header)
        {
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                var h = new byte[80];
                var hb = Encoding.ASCII.GetBytes(header);
                Array.Copy(hb, h, Math.Min(hb.Length, 80));
                bw.Write(h);
                bw.Write((uint)Triangles);
                for (int t = 0; t < Triangles; t++)
                {
                    int o = t * 9;
                    var a = new Vec3(Data[o], Data[o + 1], Data[o + 2]);
                    var b = new Vec3(Data[o + 3], Data[o + 4], Data[o + 5]);
                    var c = new Vec3(Data[o + 6], Data[o + 7], Data[o + 8]);
                    var n = (b - a).Cross(c - a).Normalized();
                    bw.Write((float)n.X); bw.Write((float)n.Y); bw.Write((float)n.Z);
                    for (int k = 0; k < 9; k++) bw.Write(Data[o + k]);
                    bw.Write((ushort)0);
                }
            }
        }

        public bool Bounds(out Vec3 min, out Vec3 max)
        {
            min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            if (Data.Count == 0) return false;
            for (int i = 0; i < Data.Count; i += 3)
            {
                min = new Vec3(Math.Min(min.X, Data[i]), Math.Min(min.Y, Data[i + 1]), Math.Min(min.Z, Data[i + 2]));
                max = new Vec3(Math.Max(max.X, Data[i]), Math.Max(max.Y, Data[i + 1]), Math.Max(max.Z, Data[i + 2]));
            }
            return true;
        }
    }

    public sealed class MeshResult
    {
        /// <summary>每个零件的网格包围盒，在其所属连杆的坐标系里（用于校验）。</summary>
        public Dictionary<PartInst, (Vec3 min, Vec3 max)> PartBoxes = new Dictionary<PartInst, (Vec3, Vec3)>();
        /// <summary>每个零件网格的体积质心（连杆坐标系）和体积（立方米），用来和 SolidWorks 的质心对比。</summary>
        public Dictionary<PartInst, (Vec3 centroid, double volume)> PartCentroids = new Dictionary<PartInst, (Vec3, double)>();
        public Dictionary<Link, string> Files = new Dictionary<Link, string>();
        public Dictionary<Link, int> TriangleCounts = new Dictionary<Link, int>();
        /// <summary>简化前的三角形数。</summary>
        public Dictionary<Link, int> SourceTriangles = new Dictionary<Link, int>();
        /// <summary>碰撞网格（简化过的）。</summary>
        public Dictionary<Link, string> CollisionFiles = new Dictionary<Link, string>();
        public Dictionary<Link, int> CollisionTriangles = new Dictionary<Link, int>();
        public List<string> Missing = new List<string>();
        /// <summary>SolidWorks 导出了、但没有对应到任何零件的网格文件（用于排查）。</summary>
        public List<string> Unmatched = new List<string>();
    }

    /// <summary>
    /// 用 SolidWorks 把装配体按零部件拆分导出 STL（保持装配体坐标、单位米），
    /// 再按连杆合并，并换算到连杆坐标系。
    /// </summary>
    public static class MeshBuilder
    {
        /// <summary>
        /// 让 SolidWorks 把装配体按零部件拆分导出 STL，读回来并转到导出坐标系（世界坐标，米）。
        /// 返回：零件 Name2 → 网格。
        /// </summary>
        /// <param name="want">需要网格的零件（装配体读出来的原始零件）。传入时，拆分导出漏掉的零件会单独从零件文档导出补上。</param>
        public static Dictionary<string, Mesh> ExportParts(ISldWorks app, ModelDoc2 asmDoc, string upAxis, Action<string> log, IEnumerable<PartInst> want = null)
        {
            var result = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
            var tmp = Path.Combine(Path.GetTempPath(), "swkit_stl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var G = upAxis == "y" ? JointSolver.YUpToZUp : Mat3.Identity;

            // 读 STL，先用零部件位姿换到装配体坐标（拆分导出的已经是装配体坐标，传 Identity），再转到导出坐标系
            Mesh Read(string file, Pose pose)
            {
                var m = Mesh.ReadStl(file);
                for (int i = 0; i < m.Data.Count; i += 3)
                {
                    var v = G * pose.Apply(new Vec3(m.Data[i], m.Data[i + 1], m.Data[i + 2]));
                    m.Data[i] = (float)v.X; m.Data[i + 1] = (float)v.Y; m.Data[i + 2] = (float)v.Z;
                }
                return m;
            }

            try
            {
                var prefs = new PrefSnapshot(app);
                try
                {
                    prefs.SetToggle(70, false);  // 保存时不弹信息框
                    prefs.SetToggle(191, false); // 不预览
                    prefs.SetToggle(69, true);   // 二进制
                    prefs.SetToggle(71, true);   // 不平移到正空间（保持模型坐标）
                    prefs.SetToggle(72, false);  // 每个零部件单独一个文件
                    prefs.SetInt(78, 2);         // 精细
                    prefs.SetInt(211, 2);        // 单位：米
                    int e = 0, w = 0;
                    app.ActivateDoc3(asmDoc.GetTitle(), false, 1, ref e);
                    asmDoc.Extension.SaveAs(Path.Combine(tmp, "asm.stl"), 0, 1, null, ref e, ref w);

                    var files = Directory.GetFiles(tmp).Where(f => f.EndsWith(".stl", StringComparison.OrdinalIgnoreCase)).ToList();
                    log?.Invoke($"SolidWorks 导出了 {files.Count} 个零件网格");
                    const string prefix = "asm - ";
                    foreach (var f in files)
                    {
                        var stem = Path.GetFileNameWithoutExtension(f);
                        if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            result[stem.Substring(prefix.Length)] = Read(f, Pose.Identity);   // 键是 Name2 把 / 换成空格后的样子
                    }
                    // 只有一个零件时 SolidWorks 不拆分，直接写成 asm.stl
                    if (result.Count == 0 && files.Count == 1) result[SingleKey] = Read(files[0], Pose.Identity);

                    // 实测拆分导出偶尔会漏掉个别零件（嵌套子装配体里的）：从零件文档单独导出，再按零部件位姿放回去
                    if (want != null && !result.ContainsKey(SingleKey))
                    {
                        var list = want.Where(p => !p.Hidden).ToList();
                        var matched = Match(list, result, out _);
                        var done = 0;
                        foreach (var p in list.Where(p => !matched.ContainsKey(p)))
                        {
                            try
                            {
                                if (!(app.GetOpenDocumentByName(p.File) is ModelDoc2 pd)) continue;
                                if (!string.IsNullOrEmpty(p.Config)) pd.ShowConfiguration2(p.Config);
                                app.ActivateDoc3(pd.GetTitle(), false, 1, ref e);
                                var f = Path.Combine(tmp, "part_" + Guid.NewGuid().ToString("N") + ".stl");
                                pd.Extension.SaveAs(f, 0, 1, null, ref e, ref w);
                                if (File.Exists(f)) { result[p.Name2.Replace('/', ' ')] = Read(f, p.Pose); done++; }
                            }
                            catch { }
                        }
                        if (done > 0)
                        {
                            app.ActivateDoc3(asmDoc.GetTitle(), false, 1, ref e);
                            log?.Invoke($"另外从零件文件单独导出了 {done} 个零件的网格");
                        }
                    }
                }
                finally { prefs.Restore(); }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
            return result;
        }

        const string SingleKey = "\u0001single";

        /// <summary>
        /// 把零件对应到网格文件。SolidWorks 拆分导出的文件名一般是「Name2 把 / 换成空格」；
        /// 对不上的，再在没被别的零件占用的文件里，用名字的最后几段做后缀匹配（只接受唯一匹配）。
        /// 返回：零件 → 网格键；unused 是没有对应到任何零件的文件。
        /// </summary>
        public static Dictionary<PartInst, string> Match(IEnumerable<PartInst> partList, Dictionary<string, Mesh> parts, out List<string> unused)
        {
            var result = new Dictionary<PartInst, string>();
            var list = partList.ToList();
            if (parts.Count == 1 && parts.ContainsKey(SingleKey))
            {
                foreach (var p in list) result[p] = SingleKey;
                unused = new List<string>();
                return result;
            }
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<PartInst>();
            foreach (var p in list)
            {
                var key = p.Name2.Replace('/', ' ');
                if (parts.ContainsKey(key)) { result[p] = key; claimed.Add(key); }
                else pending.Add(p);
            }
            foreach (var p in pending)
            {
                var segs = p.Name2.Split('/');
                for (int n = segs.Length - 1; n >= 1; n--)
                {
                    var tail = string.Join(" ", segs.Skip(segs.Length - n));
                    var hits = parts.Keys.Where(k => !claimed.Contains(k) && (k.Equals(tail, StringComparison.OrdinalIgnoreCase) || k.EndsWith(" " + tail, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (hits.Count == 1) { result[p] = hits[0]; claimed.Add(hits[0]); break; }
                    if (hits.Count > 1) break;
                }
            }
            unused = parts.Keys.Where(k => !claimed.Contains(k)).ToList();
            return result;
        }

        /// <summary>按连杆合并零件网格，换到连杆坐标系（世界坐标减去连杆原点），写出 STL。</summary>
        public static MeshResult Assemble(RobotModel model, Dictionary<string, Mesh> parts, string meshDir)
        {
            var result = new MeshResult();
            Directory.CreateDirectory(meshDir);
            var match = Match(model.Links.SelectMany(l => l.Parts), parts, out var unused);
            result.Unmatched = unused.Where(k => k != SingleKey).ToList();
            foreach (var link in model.Links)
            {
                var merged = new Mesh();
                foreach (var part in link.Parts)
                {
                    if (!match.TryGetValue(part, out var key)) { result.Missing.Add(part.Name2); continue; }
                    var m = parts[key];
                    var pmin = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
                    var pmax = new Vec3(double.MinValue, double.MinValue, double.MinValue);
                    double vol = 0; var moment = Vec3.Zero;
                    for (int i = 0; i < m.Data.Count; i += 9)
                    {
                        var a = new Vec3(m.Data[i], m.Data[i + 1], m.Data[i + 2]) - link.Origin;
                        var b = new Vec3(m.Data[i + 3], m.Data[i + 4], m.Data[i + 5]) - link.Origin;
                        var c = new Vec3(m.Data[i + 6], m.Data[i + 7], m.Data[i + 8]) - link.Origin;
                        foreach (var v in new[] { a, b, c })
                        {
                            merged.Data.Add((float)v.X); merged.Data.Add((float)v.Y); merged.Data.Add((float)v.Z);
                            pmin = new Vec3(Math.Min(pmin.X, v.X), Math.Min(pmin.Y, v.Y), Math.Min(pmin.Z, v.Z));
                            pmax = new Vec3(Math.Max(pmax.X, v.X), Math.Max(pmax.Y, v.Y), Math.Max(pmax.Z, v.Z));
                        }
                        // 以连杆原点为顶点的四面体：带符号体积及其质心（散度定理）
                        var tv = a.Dot(b.Cross(c)) / 6;
                        vol += tv;
                        moment = moment + (a + b + c) * (tv / 4);
                    }
                    if (m.Triangles > 0)
                    {
                        result.PartBoxes[part] = (pmin, pmax);
                        if (Math.Abs(vol) > 1e-15) result.PartCentroids[part] = (moment / vol, Math.Abs(vol));
                    }
                }
                if (merged.Triangles == 0) continue;
                // 外观网格：超过上限才简化（细节很多的零件，例如舵机，单个连杆能到几十万个三角形）
                var visual = Decimate(merged, MaxVisualTriangles);
                var outFile = Path.Combine(meshDir, link.Name + ".stl");
                visual.WriteBinary(outFile, "SW toolkit: " + link.Name);
                result.Files[link] = outFile;
                result.TriangleCounts[link] = visual.Triangles;
                result.SourceTriangles[link] = merged.Triangles;
                // 碰撞网格：单独一份更粗的，碰撞检测（MoveIt、Gazebo）快很多
                var collision = Decimate(merged, MaxCollisionTriangles);
                var colDir = Path.Combine(meshDir, "collision");
                Directory.CreateDirectory(colDir);
                var colFile = Path.Combine(colDir, link.Name + ".stl");
                collision.WriteBinary(colFile, "SW toolkit collision: " + link.Name);
                result.CollisionFiles[link] = colFile;
                result.CollisionTriangles[link] = collision.Triangles;
            }
            return result;
        }

        public const int MaxVisualTriangles = 200000, MaxCollisionTriangles = 20000;

        /// <summary>
        /// 顶点聚类简化：把顶点按网格单元合并（取平均位置），去掉退化和重复的三角形。
        /// 单元尺寸二分查找，使三角形数不超过 maxTriangles。形状误差不超过一个单元的大小。
        /// </summary>
        public static Mesh Decimate(Mesh m, int maxTriangles)
        {
            if (m.Triangles <= maxTriangles || !m.Bounds(out var mn, out var mx)) return m;
            var diag = (mx - mn).Length;
            double lo = diag * 1e-5, hi = diag * 0.25;
            Mesh best = null;
            for (int iter = 0; iter < 14; iter++)
            {
                var cell = Math.Sqrt(lo * hi);
                var r = Cluster(m, mn, cell);
                if (r.Triangles <= maxTriangles) { best = r; hi = cell; } else lo = cell;
                if (best != null && best.Triangles > maxTriangles * 0.85) break;
            }
            return best ?? Cluster(m, mn, diag * 0.25);
        }

        static Mesh Cluster(Mesh m, Vec3 origin, double cell)
        {
            var ids = new Dictionary<long, int>();
            var sums = new List<double>();
            var counts = new List<int>();
            var vid = new int[m.Data.Count / 3];
            for (int i = 0, v = 0; i < m.Data.Count; i += 3, v++)
            {
                long ix = (long)Math.Floor((m.Data[i] - origin.X) / cell), iy = (long)Math.Floor((m.Data[i + 1] - origin.Y) / cell), iz = (long)Math.Floor((m.Data[i + 2] - origin.Z) / cell);
                var key = (ix * 2097152L + iy) * 2097152L + iz;
                if (!ids.TryGetValue(key, out var id))
                {
                    id = counts.Count; ids[key] = id;
                    sums.Add(0); sums.Add(0); sums.Add(0); counts.Add(0);
                }
                sums[id * 3] += m.Data[i]; sums[id * 3 + 1] += m.Data[i + 1]; sums[id * 3 + 2] += m.Data[i + 2]; counts[id]++;
                vid[v] = id;
            }
            var seen = new HashSet<(int, int, int)>();
            var r = new Mesh();
            for (int t = 0; t < vid.Length / 3; t++)
            {
                int a = vid[t * 3], b = vid[t * 3 + 1], c = vid[t * 3 + 2];
                if (a == b || b == c || a == c) continue;
                var k = a < b ? (a < c ? (b < c ? (a, b, c) : (a, c, b)) : (c, a, b)) : (b < c ? (a < c ? (b, a, c) : (b, c, a)) : (c, b, a));
                if (!seen.Add(k)) continue;
                foreach (var id in new[] { a, b, c })
                {
                    r.Data.Add((float)(sums[id * 3] / counts[id])); r.Data.Add((float)(sums[id * 3 + 1] / counts[id])); r.Data.Add((float)(sums[id * 3 + 2] / counts[id]));
                }
            }
            return r;
        }
    }
}
