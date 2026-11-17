using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SwKit.Core.Dfm;
using SwKit.Core.Urdf;

namespace SwKit.Core.Mass
{
    /// <summary>读出装配体里每种零件的体积、表面积、SolidWorks 材料和每个实例的质心位置。</summary>
    public static class MassReader
    {
        /// <summary>在 SolidWorks 线程上调用。doc 是只读打开的零件或装配体。</summary>
        public static MassAssembly ReadSw(ISldWorks app, ModelDoc2 doc, Action<string> log)
        {
            var path = doc.GetPathName();
            var r = new MassAssembly { File = path, Name = Path.GetFileNameWithoutExtension(path) };
            var byKey = new Dictionary<string, MassPart>();
            if (doc is PartDoc)
            {
                var p = NewPart(path, doc.ConfigurationManager?.ActiveConfiguration?.Name, byKey, r);
                ReadPartProps(doc, p, r);
                p.Quantity = 1;
                if (p.Note == null) p.Coms.Add(_lastCom);
                return r;
            }

            r.IsAssembly = true;
            var asm = (AssemblyDoc)doc;
            try { asm.ResolveAllLightWeightComponents(false); } catch { }
            int suppressed = 0, envelopes = 0, missing = 0, n = 0;
            void Walk(Component2 c)
            {
                if (c == null) return;
                if (Safe(() => c.IsSuppressed())) { suppressed++; return; }
                if (Safe(() => c.IsEnvelope())) { envelopes++; return; }
                var children = c.GetChildren() as object[];
                if (children != null && children.Length > 0) { foreach (Component2 ch in children) Walk(ch); return; }
                var file = c.GetPathName();
                if (!string.Equals(Path.GetExtension(file), ".sldprt", StringComparison.OrdinalIgnoreCase)) return;
                var cfg = c.ReferencedConfiguration;
                var key = Key(file, cfg);
                if (!byKey.TryGetValue(key, out var p))
                {
                    p = NewPart(file, cfg, byKey, r);
                    if (c.GetModelDoc2() is ModelDoc2 pd)
                    {
                        var active = pd.ConfigurationManager?.ActiveConfiguration?.Name;
                        if (!string.IsNullOrEmpty(cfg) && active != null && active != cfg) try { pd.ShowConfiguration2(cfg); } catch { }
                        ReadPartProps(pd, p, r);
                        p.LocalCom = _lastCom;
                    }
                    else { p.Note = "零件文件没有加载（找不到文件或是轻化状态），读不到体积"; missing++; }
                    if (++n % 20 == 0) log?.Invoke($"已读取 {n} 种零件……");
                }
                p.Quantity++;
                if (p.LocalCom.HasValue)
                {
                    try { p.Coms.Add(AssemblyReader.PoseOf(app, c).Apply(p.LocalCom.Value)); } catch { }
                }
            }
            foreach (Component2 c in (asm.GetComponents(true) as object[]) ?? new object[0]) Walk(c);
            // 有实例的位置没读到时不算重心（Summarize 里按实例数核对）
            if (suppressed > 0) r.Notes.Add($"{suppressed} 个零部件是压缩状态，没有计入");
            if (envelopes > 0) r.Notes.Add($"{envelopes} 个封套零部件（参考用）没有计入");
            if (missing > 0) r.Notes.Add($"{missing} 种零件读不到几何（文件缺失或没加载），体积为 0，可以填实测质量");
            log?.Invoke($"读取到 {r.Parts.Count} 种零件、{r.Instances} 个实例");
            return r;
        }

        static Vec3 _lastCom;

        static string Key(string file, string cfg) => (file ?? "").ToLowerInvariant() + "|" + (cfg ?? "");

        static MassPart NewPart(string file, string cfg, Dictionary<string, MassPart> byKey, MassAssembly r)
        {
            var p = new MassPart { Index = r.Parts.Count, Key = Key(file, cfg), File = file, Config = cfg, Name = Path.GetFileNameWithoutExtension(file) };
            // 默认配置不显示，其他配置写在名字后面
            if (!string.IsNullOrEmpty(cfg) && !new[] { "默认", "Default", "默认<按加工>", "Default<As Machined>" }.Contains(cfg)) p.Name += $"（{cfg}）";
            byKey[p.Key] = p; r.Parts.Add(p);
            return p;
        }

        static void ReadPartProps(ModelDoc2 pd, MassPart p, MassAssembly r)
        {
            _lastCom = Vec3.Zero;
            try
            {
                var mp = pd.Extension.CreateMassProperty();
                mp.UseSystemUnits = true;
                p.Volume = mp.Volume;
                p.Area = mp.SurfaceArea;
                if ((object)mp.CenterOfMass is double[] cm && cm.Length >= 3) _lastCom = Vec3.From(cm);
                try
                {
                    string db;
                    var mat = ((PartDoc)pd).GetMaterialPropertyName2(p.Config ?? "", out db);
                    if (!string.IsNullOrWhiteSpace(mat)) { p.SwMaterial = mat; p.SwDensity = mp.Density; }
                }
                catch { }
                if (((PartDoc)pd).GetPartBox(true) is double[] b && b.Length >= 6) p.Size = new Vec3(Math.Abs(b[3] - b[0]), Math.Abs(b[4] - b[1]), Math.Abs(b[5] - b[2]));
                if (p.Volume <= 0) p.Note = "没有实体（只有曲面或草图），体积为 0";
            }
            catch (COMException ex) { p.Note = "读取质量属性失败：" + ex.Message; r.Notes.Add($"{p.Name}：读取质量属性失败"); }
        }

        static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }

        /// <summary>
        /// STEP / STL：按表面网格的连通块拆成实体（STEP 装配体导出后每个零件是一个或几个实体），
        /// 内部空腔并到包着它的实体里。体积、形状完全相同的实体合并成一种、数量累加。
        /// STEP 里的零件名读不出来，按「实体 1、2……」编号。
        /// </summary>
        public static MassAssembly ReadMesh(string path, Action<string> log)
        {
            // STEP 用二阶表面网格：每个三角形按贴合曲面的二次曲边三角形积分，圆孔、圆柱、球面的体积基本是精确的
            var m = DfmMesh.Load(path, log, curved: true);
            var r = new MassAssembly { File = path, Name = Path.GetFileNameWithoutExtension(path) };
            var solids = new List<(double vol, double area, Vec3 min, Vec3 max, Vec3 com)>();
            var shells = m.Shells.Where(s => s.Tris.Count > 0).ToList();
            var cavities = shells.Where(s => s.Cavity).ToList();
            // 先算每个壳的体积、面积、包围盒、一阶矩（散度定理：体积 = ∮x·n/3，∫x dV = ∮(x²/2)·n）
            var info = new Dictionary<DfmShell, (double vol, double area, Vec3 min, Vec3 max, Vec3 moment)>();
            var x6 = new Vec3[6];
            foreach (var sh in shells)
            {
                double v = 0, a = 0; Vec3 mo = Vec3.Zero;
                var mn = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue); var mx = new Vec3(double.MinValue, double.MinValue, double.MinValue);
                foreach (var t in sh.Tris)
                {
                    m.Tri6(t, x6);
                    DfmMesh.CurvedTri(x6, out var dv, out var dm, out var da);
                    v += dv; mo = mo + dm; a += da;
                    foreach (var q in x6) { mn = new Vec3(Math.Min(mn.X, q.X), Math.Min(mn.Y, q.Y), Math.Min(mn.Z, q.Z)); mx = new Vec3(Math.Max(mx.X, q.X), Math.Max(mx.Y, q.Y), Math.Max(mx.Z, q.Z)); }
                }
                info[sh] = (v, a, mn, mx, mo);
            }
            if (m.EdgeMid != null) log?.Invoke("体积按二次曲边三角形积分（曲面不会被弦切掉）");
            var outer = shells.Where(s => !s.Cavity).ToList();
            var acc = outer.ToDictionary(s => s, s => (vol: Math.Abs(info[s].vol), area: info[s].area, moment: info[s].vol < 0 ? -info[s].moment : info[s].moment));
            foreach (var cv in cavities)
            {
                var ci = info[cv];
                var c = (ci.min + ci.max) / 2;
                // 包着它的、包围盒最小的实体
                var host = outer.Where(o => Inside(c, info[o].min, info[o].max)).OrderBy(o => Math.Abs(info[o].vol)).FirstOrDefault();
                if (host == null) continue;
                var h = acc[host];
                acc[host] = (h.vol - Math.Abs(ci.vol), h.area + ci.area, h.moment - (ci.vol < 0 ? -ci.moment : ci.moment));
            }
            foreach (var o in outer)
            {
                var a = acc[o];
                if (a.vol < 1e-6) continue;   // mm³：碎片
                solids.Add((a.vol, a.area, info[o].min, info[o].max, a.moment / a.vol));
            }
            if (solids.Count == 0) throw new InvalidOperationException("没有找到封闭的实体（STL 可能有破面）");
            // 合并相同的实体：体积、面积、包围盒尺寸都在 0.2% 以内
            var groups = new List<List<int>>();
            for (int i = 0; i < solids.Count; i++)
            {
                var si = solids[i];
                var g = groups.FirstOrDefault(gr =>
                {
                    var sj = solids[gr[0]];
                    bool Close(double x, double y) => Math.Abs(x - y) <= 0.002 * Math.Max(Math.Abs(x), Math.Abs(y)) + 1e-6;
                    var di = (si.max - si.min); var dj = (sj.max - sj.min);
                    var a = new[] { di.X, di.Y, di.Z }.OrderBy(x => x).ToArray(); var b = new[] { dj.X, dj.Y, dj.Z }.OrderBy(x => x).ToArray();
                    return Close(si.vol, sj.vol) && Close(si.area, sj.area) && Close(a[0], b[0]) && Close(a[1], b[1]) && Close(a[2], b[2]);
                });
                if (g == null) groups.Add(new List<int> { i }); else g.Add(i);
            }
            var baseName = Path.GetFileNameWithoutExtension(path);
            foreach (var g in groups.OrderByDescending(gr => solids[gr[0]].vol))
            {
                var s0 = solids[g[0]];
                var p = new MassPart
                {
                    Index = r.Parts.Count,
                    Key = Key(path, "实体" + (r.Parts.Count + 1)),
                    File = path,
                    Name = groups.Count == 1 ? baseName : $"实体 {r.Parts.Count + 1}",
                    Quantity = g.Count,
                    Volume = s0.vol * 1e-9, Area = s0.area * 1e-6,
                    Size = (s0.max - s0.min) * 1e-3,
                };
                foreach (var i in g) p.Coms.Add(solids[i].com * 1e-3);
                r.Parts.Add(p);
            }
            r.IsAssembly = solids.Count > 1;
            if (r.IsAssembly) r.Notes.Add("STEP / STL 里读不出零件名，按体积从大到小编号为「实体 1、2……」；形状相同的实体已合并计数。要零件名和 SolidWorks 材料，直接拖 .sldasm 进来");
            if (!m.Closed) r.Notes.Add("网格不是完全封闭的（有破面），体积可能不准");
            log?.Invoke($"找到 {solids.Count} 个实体，{r.Parts.Count} 种");
            return r;
        }

        static bool Inside(Vec3 p, Vec3 min, Vec3 max) => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;
    }
}
