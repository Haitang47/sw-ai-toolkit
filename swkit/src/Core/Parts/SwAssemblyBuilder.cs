using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SwKit.Core.Urdf;

namespace SwKit.Core.Parts
{
    public sealed class AsmBuildResult
    {
        public string Path;
        public List<string> PartFiles = new List<string>();
        public int Components, NewParts;
        public bool MatesAdded;
        public string MateNote;
        public double MaxPositionError;     // 重新读回的零部件位置和方案的最大偏差（米）
        public int Interferences;           // 干涉检查发现的干涉数（-1 = 没做）
        public double InterferenceVolume;   // 干涉总体积（立方米）
        public double Seconds;
        public List<string> Log = new List<string>();
    }

    /// <summary>
    /// 在（后台独立的）SolidWorks 里按 AsmPlan 生成装配体：先用 SwPartBuilder 生成（或复用）各零件，
    /// 再新建装配体、插入零部件、按方案设置位姿，最后加配合（齿轮副）或固定全部零部件，保存为 .SLDASM。
    /// 位姿用 MathTransform 的数组设置：前 9 个数是零件 X、Y、Z 轴在装配体里的方向，接着 3 个是原点位置，然后是比例 1。
    /// 保存前把每个零部件的位姿读回来和方案比较，并做一次干涉检查。
    /// </summary>
    public static class SwAssemblyBuilder
    {
        public static string FilePath(AsmPlan plan, string libraryRoot) =>
            System.IO.Path.Combine(libraryRoot, "装配", Safe(plan.Title) + ".SLDASM");

        static string Safe(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace('×', 'x');
        }

        public static AsmBuildResult Build(ISldWorks app, AsmPlan plan, string libraryRoot, bool tryMates, Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            var r = new AsmBuildResult { Path = FilePath(plan, libraryRoot) };
            void Log(string s) { r.Log.Add(s); log?.Invoke(s); }

            // 1. 零件（同规格只生成一次；已有文件直接用）
            var files = new Dictionary<string, string>();
            foreach (var part in plan.Parts)
            {
                var key = part.Type.Id + "|" + part.Type.Title(part.P);
                if (files.ContainsKey(key)) continue;
                Log("零件：" + part.Type.Title(part.P));
                var pr = SwPartBuilder.Build(app, part.Type, part.P, libraryRoot, false, null);
                if (!pr.Reused) r.NewParts++;
                files[key] = pr.Path;
                r.PartFiles.Add(pr.Path);
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(r.Path));
            var mates = tryMates && plan.Mates != null;
            try
            {
                BuildOnce(app, plan, files, r, mates, Log);
            }
            catch (Exception ex) when (mates)
            {
                Log("加配合失败（" + ex.Message + "），改为固定位置重新生成");
                r.MateNote = "没能加上齿轮配合（" + ex.Message + "），零部件按计算好的位置固定";
                BuildOnce(app, plan, files, r, false, Log);
            }
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        static void BuildOnce(ISldWorks app, AsmPlan plan, Dictionary<string, string> files, AsmBuildResult r, bool mates, Action<string> Log)
        {
            int e = 0, w = 0;
            var asm = NewAssembly(app) ?? throw new InvalidOperationException("新建装配体失败：找不到装配体模板（工具 → 选项 → 默认模板）");
            var opened = new List<string>();
            try
            {
                var ad = (AssemblyDoc)asm;
                // AddComponent5 要求零件已经在会话里打开
                foreach (var f in files.Values.Distinct())
                {
                    if (app.GetOpenDocumentByName(f) == null)
                    {
                        var d = app.OpenDoc6(f, 1, 1, "", ref e, ref w) as ModelDoc2;
                        if (d == null) throw new InvalidOperationException("打开零件失败：" + f);
                        opened.Add(d.GetTitle());
                    }
                }
                app.ActivateDoc3(asm.GetTitle(), false, 0, ref e);

                var comps = new List<(AsmPart part, Component2 comp)>();
                foreach (var part in plan.Parts)
                {
                    var f = files[part.Type.Id + "|" + part.Type.Title(part.P)];
                    var c = ad.AddComponent5(f, 0, "", false, "", part.T.X, part.T.Y, part.T.Z) ?? throw new InvalidOperationException("插入零部件失败：" + System.IO.Path.GetFileName(f));
                    comps.Add((part, c));
                }
                r.Components = comps.Count;
                Log($"已插入 {comps.Count} 个零部件");

                // 第一个插入的零部件默认是固定的，先全部解除固定再摆位置
                asm.ClearSelection2(true);
                foreach (var (_, c) in comps) c.Select4(true, null, false);
                ad.UnfixComponent();
                asm.ClearSelection2(true);

                var mu = (MathUtility)app.GetMathUtility();
                foreach (var (part, c) in comps)
                {
                    var data = new[] { part.Ex.X, part.Ex.Y, part.Ex.Z, part.Ey.X, part.Ey.Y, part.Ey.Z, part.Ez.X, part.Ez.Y, part.Ez.Z, part.T.X, part.T.Y, part.T.Z, 1.0, 0, 0, 0 };
                    c.Transform2 = (MathTransform)mu.CreateTransform(data);
                }
                asm.EditRebuild3();

                // 读回位姿核对（用 MathPoint 变换，不依赖数组的存储顺序）
                r.MaxPositionError = 0;
                foreach (var (part, c) in comps)
                {
                    var xf = c.Transform2;
                    foreach (var v in new[] { new Vec3(0, 0, 0), new Vec3(0.01, 0, 0), new Vec3(0, 0.01, 0), new Vec3(0, 0, 0.01) })
                    {
                        var mp = (MathPoint)mu.CreatePoint(new[] { v.X, v.Y, v.Z });
                        var got = Vec3.From((double[])((MathPoint)mp.MultiplyTransform(xf)).ArrayData);
                        r.MaxPositionError = Math.Max(r.MaxPositionError, (got - part.Apply(v)).Length);
                    }
                }
                Log($"位姿核对：最大偏差 {r.MaxPositionError * 1000:0.####} mm");
                if (r.MaxPositionError > 1e-6) throw new InvalidOperationException($"零部件位置和计算值不一致（偏差 {r.MaxPositionError * 1000:0.###} mm）");

                r.MatesAdded = false;
                if (mates)
                {
                    AddGearMates(asm, ad, comps, plan, Log);
                    r.MatesAdded = true;
                    r.MateNote = $"已加齿轮配合（{plan.Z1} : {plan.Z2}）：拖动一个齿轮，另一个会按传动比反向转动";
                    // 配合求解后位置不能变
                    asm.EditRebuild3();
                    foreach (var (part, c) in comps.Where(x => x.part.Role != null && x.part.Role.StartsWith("gear")))
                    {
                        var mp = (MathPoint)mu.CreatePoint(new[] { 0.0, 0, 0 });
                        var got = Vec3.From((double[])((MathPoint)mp.MultiplyTransform(c.Transform2)).ArrayData);
                        if ((got - part.T).Length > 1e-6) throw new InvalidOperationException("加配合后齿轮位置变了");
                    }
                    // 其余零部件（安装板）固定
                    asm.ClearSelection2(true);
                    var others = comps.Where(x => x.part.Role == null || !x.part.Role.StartsWith("gear")).ToList();
                    foreach (var (_, c) in others) c.Select4(true, null, false);
                    if (others.Count > 0) ad.FixComponent();
                }
                else
                {
                    asm.ClearSelection2(true);
                    foreach (var (_, c) in comps) c.Select4(true, null, false);
                    ad.FixComponent();
                }
                asm.ClearSelection2(true);
                asm.EditRebuild3();

                // 干涉检查（接触不算干涉）
                r.Interferences = -1;
                try
                {
                    var idm = ad.InterferenceDetectionManager;
                    idm.TreatCoincidenceAsInterference = false;
                    idm.TreatSubAssembliesAsComponents = false;
                    idm.IncludeMultibodyPartInterferences = false;
                    var list = idm.GetInterferences() as object[];
                    r.Interferences = 0; r.InterferenceVolume = 0;
                    foreach (IInterference it in list ?? new object[0])
                    {
                        // 极小的数值干涉（样条逼近渐开线的误差）忽略
                        if (it.Volume < 1e-12) continue;
                        r.Interferences++; r.InterferenceVolume += it.Volume;
                    }
                    idm.Done();
                    Log($"干涉检查：{r.Interferences} 处" + (r.Interferences > 0 ? $"，总体积 {r.InterferenceVolume * 1e9:0.###} mm³" : ""));
                }
                catch (Exception ex) { Log("干涉检查没有完成：" + ex.Message); }

                var cpm = asm.Extension.CustomPropertyManager[""];
                cpm.Add3("名称", 30, plan.Title, 2);
                cpm.Add3("来源", 30, "SW 工具箱自动生成", 2);
                if (File.Exists(r.Path)) File.Delete(r.Path);
                asm.Extension.SaveAs(r.Path, 0, 1, null, ref e, ref w);
                if (!File.Exists(r.Path)) throw new InvalidOperationException($"保存装配体失败（错误码 {e}）：{r.Path}");
                Log("已保存 " + r.Path);
            }
            finally
            {
                try { app.CloseDoc(asm.GetTitle()); } catch { }
                foreach (var t in opened) try { app.CloseDoc(t); } catch { }
            }
        }

        /// <summary>
        /// 齿轮副的配合：每个齿轮的「旋转轴」在装配体前视基准面内（z = 0），齿轮 1 的轴在右视基准面内（x = 0），
        /// 齿轮 2 的轴到右视基准面距离 a；两个齿轮的上视基准面和装配体上视基准面重合；最后加齿轮配合 z1 : z2。
        /// 任何一步失败都抛异常，由调用方改为固定位置重新生成。
        /// </summary>
        static void AddGearMates(ModelDoc2 asm, AssemblyDoc ad, List<(AsmPart part, Component2 comp)> comps, AsmPlan plan, Action<string> Log)
        {
            var g1 = comps.First(x => x.part.Role == "gear1"); var g2 = comps.First(x => x.part.Role == "gear2");
            Feature Axis(Component2 c) => c.FeatureByName(SwPartBuilder.AxisName) as Feature ?? throw new InvalidOperationException("齿轮零件里没有「旋转轴」（旧版本生成的零件），请删除后重新生成");
            Feature AsmPlane(int idx)
            {
                var f = asm.FirstFeature() as Feature; int n = 0;
                while (f != null) { if (f.GetTypeName2() == "RefPlane" && ++n == idx) return f; f = f.GetNextFeature() as Feature; }
                throw new InvalidOperationException("找不到装配体基准面 " + idx);
            }
            Feature CompPlane(Component2 c, int idx)
            {
                var f = c.FirstFeature() as Feature; int n = 0;
                while (f != null) { if (f.GetTypeName2() == "RefPlane" && ++n == idx) return f; f = f.GetNextFeature() as Feature; }
                throw new InvalidOperationException("找不到零件基准面 " + idx);
            }
            void Mate(string what, int type, Feature a, Feature b, double dist = 0, double num = 0, double den = 0)
            {
                asm.ClearSelection2(true);
                if (!a.Select2(false, 1) || !b.Select2(true, 1)) throw new InvalidOperationException($"选不中「{what}」的配合对象");
                int err;
                var m = ad.AddMate5(type, 2, false, dist, dist, dist, num, den, 0, 0, 0, false, false, 0, out err);
                if (m == null || err != 1) throw new InvalidOperationException($"「{what}」配合失败（错误码 {err}）");
                Log("配合：" + what);
            }
            var front = AsmPlane(1); var top = AsmPlane(2); var right = AsmPlane(3);
            var a1 = Axis(g1.comp); var a2 = Axis(g2.comp);
            const int Coincident = 0, Distance = 5, GearMate = 10;
            Mate("齿轮 1 轴在前视面", Coincident, a1, front);
            Mate("齿轮 1 轴在右视面", Coincident, a1, right);
            Mate("齿轮 1 底面", Coincident, CompPlane(g1.comp, 2), top);
            Mate("齿轮 2 轴在前视面", Coincident, a2, front);
            Mate("齿轮 2 轴到右视面的距离", Distance, a2, right, g2.part.T.X);
            Mate("齿轮 2 底面", Coincident, CompPlane(g2.comp, 2), top);
            Mate("齿轮配合", GearMate, a1, a2, 0, plan.Z1, plan.Z2);
            asm.ClearSelection2(true);
        }

        static ModelDoc2 NewAssembly(ISldWorks app)
        {
            var tpl = app.GetUserPreferenceStringValue(9);
            if (!string.IsNullOrEmpty(tpl) && !tpl.StartsWith("~") && File.Exists(tpl) && app.NewDocument(tpl, 0, 0, 0) is ModelDoc2 d0) return d0;
            foreach (var dir in (app.GetUserPreferenceStringValue(6) ?? "").Split(';').Where(Directory.Exists))
            {
                var f = Directory.GetFiles(dir, "*.asmdot").FirstOrDefault();
                if (f != null && app.NewDocument(f, 0, 0, 0) is ModelDoc2 d) return d;
            }
            return null;
        }

        /// <summary>重新打开已生成的装配体，读回各零部件的位置（米，零件原点在装配体里的位置），用于验证。</summary>
        public static List<(string name, Vec3 origin)> ReadBack(ISldWorks app, string path)
        {
            int e = 0, w = 0;
            var doc = app.OpenDoc6(path, 2, 1 | 2, "", ref e, ref w) as ModelDoc2 ?? throw new InvalidOperationException("打开装配体失败：" + path);
            var res = new List<(string, Vec3)>();
            try
            {
                var mu = (MathUtility)app.GetMathUtility();
                foreach (Component2 c in (object[])((AssemblyDoc)doc).GetComponents(true))
                {
                    var mp = (MathPoint)mu.CreatePoint(new[] { 0.0, 0, 0 });
                    res.Add((c.Name2, Vec3.From((double[])((MathPoint)mp.MultiplyTransform(c.Transform2)).ArrayData)));
                }
            }
            finally { try { app.CloseDoc(doc.GetTitle()); } catch { } }
            return res;
        }
    }
}
