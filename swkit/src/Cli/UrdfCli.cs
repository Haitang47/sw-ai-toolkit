using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SwKit.Core;
using SwKit.Core.Urdf;

namespace SwKit.Cli
{
    /// <summary>
    /// URDF 相关的开发 / 评估命令：
    ///   swkit-cli urdf-eval [--up y|z] [--rigid-subasm] [--out 报告.md] 装配体…
    ///       每个装配体读一次，分别按配合（当作标准答案）和只按几何（忽略配合）识别关节，统计几何推断的召回率、误报、轴线误差。
    ///   swkit-cli dev-geom 装配体 [零件名关键字]
    ///       打印读到的 B-rep 几何特征（圆柱面、平面），以及 Component2.GetBodies3 的坐标系实测结果。
    /// </summary>
    static class UrdfCli
    {
        public static int DevGeom(string[] args)
        {
            try { return DevGeomCore(args); }
            catch (Exception ex)
            {
                var msg = "出错：" + ex.GetType().FullName + "：" + ex.Message + "\n" + ex.StackTrace;
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "swkit_devgeom_error.txt"), msg, Encoding.UTF8); } catch { }
                Console.Error.WriteLine(msg);
                return 1;
            }
        }

        static int DevGeomCore(string[] args)
        {
            var asmPath = args[0];
            var filter = args.Length > 1 ? args[1] : null;
            using (var s = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
            {
                var doc = UrdfExporter.OpenAssembly(s.App, asmPath);
                var t0 = DateTime.Now;
                var data = AssemblyReader.Read(s.App, doc, Console.WriteLine);
                Console.WriteLine($"读取用时 {(DateTime.Now - t0).TotalSeconds:0.0} 秒；平面法向与 Face2.Normal 相反 {FeatureScanner.NormalFlips} 次");
                foreach (var p in data.AllParts)
                {
                    if (filter != null && p.Name2.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var g = p.Geom;
                    Console.WriteLine($"{p.Name2}  面 {g?.Faces}  圆柱 {g?.Cyls.Count}  平面 {g?.Planes.Count}{(g?.Truncated == true ? "（截断）" : "")}  包围盒 {p.BoxMin}~{p.BoxMax}");
                    if (g == null) continue;
                    foreach (var c in g.Cyls.Where(c => c.Coverage > 3)) Console.WriteLine("    " + c);
                }

                // GetBodies3 的坐标系：取前几个零件，比较组件实体上的圆柱参数与零件文档里的
                Console.WriteLine();
                Console.WriteLine("== Component2.GetBodies3 坐标系实测 ==");
                var comps = ((AssemblyDoc)doc).GetComponents(false) as object[];
                int shown = 0;
                foreach (Component2 c in comps ?? new object[0])
                {
                    if (shown >= 4) break;
                    if (!c.GetPathName().EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)) continue;
                    var bodies = c.GetBodies3(0, out object _) as object[];
                    if (bodies == null) continue;
                    foreach (Body2 b in bodies)
                    {
                        foreach (Face2 f in (b.GetFaces() as object[]) ?? new object[0])
                        {
                            var sf = f.GetSurface() as Surface;
                            if (sf == null || !sf.IsCylinder()) continue;
                            var prm = (double[])sf.CylinderParams;
                            var inst = data.AllParts.FirstOrDefault(x => x.Name2 == c.Name2);
                            var local = inst?.Pose.Inverse().Apply(Vec3.From(prm, 0));
                            Console.WriteLine($"{c.Name2}: GetBodies3 圆柱原点 {Vec3.From(prm, 0)} 半径 {prm[6] * 1000:0.###} mm；按组件位姿逆变换后 {local}；组件包围盒 {inst?.BoxMin}~{inst?.BoxMax}");
                            shown++;
                            goto next;
                        }
                    }
                    next:;
                }
                s.App.CloseAllDocuments(true);
            }
            return 0;
        }

        /// <summary>开发用：打印缓存的装配体数据里每个零件的几何特征统计（不需要 SolidWorks）。</summary>
        public static int DevGeomCache(string[] args)
        {
            AsmData raw;
            using (var fs = File.OpenRead(args[0])) raw = (AsmData)new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(fs);
            var filter = args.Length > 1 ? args[1] : null;
            foreach (var p in raw.AllParts)
            {
                if (filter != null && p.Name2.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var g = p.Geom;
                Console.WriteLine($"{p.Name2}{(p.Hidden ? "（隐藏）" : "")}  面 {g?.Faces}  圆柱 {g?.Cyls.Count}（整圈 {g?.Cyls.Count(c => c.Coverage > 3.4)}）  平面 {g?.Planes.Count}{(g?.Truncated == true ? "（截断）" : "")}  盒 {p.BoxMin}~{p.BoxMax}");
                if (filter != null && g != null) foreach (var c in g.Cyls.Where(c => c.Coverage > 3.4)) Console.WriteLine("    " + c);
            }
            return 0;
        }

        /// <summary>开发用：两个零件之间最近的相对平面和同轴圆柱（不需要 SolidWorks）。</summary>
        public static int DevNear(string[] args)
        {
            AsmData raw;
            using (var fs = File.OpenRead(args[0])) raw = (AsmData)new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(fs);
            var a = raw.AllParts.First(p => p.Name2.Contains(args[1]));
            var b = raw.AllParts.First(p => p.Name2.Contains(args[2]) && p != a);
            Console.WriteLine($"{a.Name2} ↔ {b.Name2}");
            var list = new List<(double d, string s)>();
            foreach (var pa in a.Geom.Planes)
                foreach (var pb in b.Geom.Planes)
                {
                    if (pa.N.Dot(pb.N) > -0.999) continue;
                    var dist = (pb.P - pa.P).Dot(pa.N);
                    if (Math.Abs(dist) > 0.005) continue;
                    bool box = pa.Min.X - 1e-3 <= pb.Max.X && pb.Min.X - 1e-3 <= pa.Max.X && pa.Min.Y - 1e-3 <= pb.Max.Y && pb.Min.Y - 1e-3 <= pa.Max.Y && pa.Min.Z - 1e-3 <= pb.Max.Z && pb.Min.Z - 1e-3 <= pa.Max.Z;
                    if (!box) continue;
                    list.Add((Math.Abs(dist), $"平面 间距 {dist * 1000:0.###} mm 法向 {pa.N} 面积 {pa.Area * 1e6:0.#}/{pb.Area * 1e6:0.#} mm²"));
                }
            foreach (var ca in a.Geom.Cyls.Where(c => c.Coverage > 3))
                foreach (var cb in b.Geom.Cyls.Where(c => c.Coverage > 3))
                {
                    if (Math.Abs(ca.D.Dot(cb.D)) < 0.999) continue;
                    var v = cb.P - ca.P; var off = (v - ca.D * v.Dot(ca.D)).Length;
                    if (off > 0.002) continue;
                    list.Add((off, $"圆柱 偏心 {off * 1000:0.###} mm  {ca} ↔ {cb}"));
                }
            foreach (var x in list.OrderBy(x => x.d).Take(25)) Console.WriteLine("  " + x.s);
            return 0;
        }

        public static int Eval(string[] args)
        {
            string up = "y", report = null, cache = null; bool expand = true, debug = false, auto = false;
            var files = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--up": up = args[++i].ToLowerInvariant() == "z" ? "z" : "y"; break;
                    case "--rigid-subasm": expand = false; break;
                    case "--out": report = args[++i]; break;
                    case "--cache": cache = args[++i]; break;      // 开发用：读到的装配体数据存成文件，下次直接用（调参时不用再开 SolidWorks）
                    case "--debug": debug = true; break;
                    case "--auto": auto = true; break;             // 同时打印 Auto 模式（默认）的结果
                    default: files.Add(args[i]); break;
                }
            }
            if (files.Count == 0) { Console.Error.WriteLine("用法：swkit-cli urdf-eval [--up y|z] [--out 报告.md] 装配体…"); return 2; }
            JointInference.Verbose = debug;
            var sb = new StringBuilder();
            sb.AppendLine("# 几何推断关节评估（标准答案 = 按配合识别的运动关节）");
            sb.AppendLine();
            SwSession s = null;
            try
            {
                foreach (var f in files)
                {
                    try
                    {
                        AsmData raw = null;
                        var cacheFile = cache == null ? null : Path.Combine(cache, Path.GetFileNameWithoutExtension(f) + (expand ? "" : "_rigid") + ".bin");
                        if (cacheFile != null && File.Exists(cacheFile))
                            using (var fs = File.OpenRead(cacheFile)) raw = (AsmData)new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(fs);
                        if (raw == null)
                        {
                            if (s == null) s = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None);
                            var t0 = DateTime.Now;
                            var doc = UrdfExporter.OpenAssembly(s.App, f);
                            raw = AssemblyReader.Read(s.App, doc, Console.WriteLine, expand);
                            s.App.CloseAllDocuments(true);
                            Console.WriteLine($"读取用时 {(DateTime.Now - t0).TotalSeconds:0.0} 秒");
                            if (cacheFile != null)
                            {
                                Directory.CreateDirectory(cache);
                                using (var fs = File.Create(cacheFile)) new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(fs, raw);
                            }
                        }
                        var gt = JointSolver.Solve(raw, up, InferMode.Off);
                        var t1 = DateTime.Now;
                        var inf = JointSolver.Solve(raw, up, InferMode.GeometryOnly);
                        Console.WriteLine($"推断用时 {(DateTime.Now - t1).TotalSeconds:0.00} 秒");
                        var text = JointEval.Compare(Path.GetFileNameWithoutExtension(f), gt, inf);
                        Console.WriteLine(text);
                        Console.WriteLine("---- 几何推断结果 ----");
                        Console.WriteLine(UrdfExporter.Describe(inf));
                        if (debug) foreach (var d in JointSolver.LastInference.Debug) Console.WriteLine("  · " + d);
                        if (debug) { Console.WriteLine("---- 标准答案（配合）----"); Console.WriteLine(UrdfExporter.Describe(gt)); }
                        if (auto)
                        {
                            Console.WriteLine("---- 默认（Auto）结果 ----");
                            Console.WriteLine(UrdfExporter.Describe(JointSolver.Solve(raw, up, InferMode.Auto)));
                        }
                        sb.AppendLine(text);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"{f}：{ex}");
                        sb.AppendLine($"## {Path.GetFileName(f)}\n\n失败：{ex.Message}\n");
                    }
                }
            }
            finally { s?.Dispose(); }
            if (report != null) File.WriteAllText(report, sb.ToString(), new UTF8Encoding(false));
            return 0;
        }
    }
}
