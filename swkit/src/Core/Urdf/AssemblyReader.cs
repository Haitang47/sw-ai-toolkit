using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;

namespace SwKit.Core.Urdf
{
    /// <summary>装配体里的一个零件实例（叶子零部件）。坐标都是装配体坐标系，单位米 / 千克。</summary>
    [Serializable]
    public sealed class PartInst
    {
        public string Name2;          // 例如 "子装配-1/零件-1"
        public string File;
        public string Config;
        public bool Hidden;
        public bool HasMaterial;
        public string Material;
        public Vec3 BoxMin, BoxMax;   // SolidWorks 给出的包围盒
        public double Mass;
        public Vec3 Com;
        public Mat3 Inertia;          // 关于质心、与装配体坐标轴对齐
        /// <summary>零部件在装配体里的位姿（SolidWorks 坐标，米）。</summary>
        public Pose Pose = Pose.Identity;
        /// <summary>B-rep 几何特征（圆柱面、平面），装配体坐标；没读时为 null。</summary>
        public PartFeatures Geom;
        public TopComp Top;
        public string Short => Name2.Contains("/") ? Name2.Substring(Name2.LastIndexOf('/') + 1) : Name2;
    }

    /// <summary>顶层零部件（零件或子装配体）。子装配体按刚体处理。</summary>
    [Serializable]
    public sealed class TopComp
    {
        public int Index;
        public string Name2;
        public string File;
        public bool IsAssembly;
        public bool Fixed;
        public bool Flexible;
        /// <summary>如果它在某个柔性子装配体里被固定，这里是那个子装配体的名字（它随子装配体整体运动）。</summary>
        public string FixedInFlex;
        /// <summary>展开的子装配体里的零部件：所在子装配体的名字（直接上级）。</summary>
        public string InSub;
        public List<PartInst> Parts = new List<PartInst>();
        public override string ToString() => Name2;
    }

    public enum GeoKind { Unknown, Plane, Axis, Point, Component }

    /// <summary>配合引用的几何：平面（点 + 法向）、轴线（点 + 方向，可带半径）、点或整个零部件。</summary>
    [Serializable]
    public sealed class MateGeom
    {
        public GeoKind Kind;
        public Vec3 P, D;
        public double Radius;
        public int RefType;
        public TopComp Comp;
        public string CompName;
        public override string ToString() => $"{Kind} {CompName} P={P} D={D} R={Radius * 1000:0.###}mm";
    }

    [Serializable]
    public sealed class MateInfo
    {
        public string Name;
        public int Type;
        public int Alignment;
        public bool Flipped;
        public double Min, Max;
        public double? Value;     // 距离（米）或角度（弧度）
        public bool IsLimit;
        public List<MateGeom> Ents = new List<MateGeom>();
        public string TypeName => MateTypes.Name(Type);
    }

    public static class MateTypes
    {
        public const int Coincident = 0, Concentric = 1, Perpendicular = 2, Parallel = 3, Tangent = 4, Distance = 5, Angle = 6,
            Symmetric = 8, Gear = 10, Width = 11, RackPinion = 13, Lock = 16, Screw = 17, LinearCoupler = 18, Slot = 21, Hinge = 22, Slider = 23;

        public static string Name(int t)
        {
            switch (t)
            {
                case Coincident: return "重合";
                case Concentric: return "同心";
                case Perpendicular: return "垂直";
                case Parallel: return "平行";
                case Tangent: return "相切";
                case Distance: return "距离";
                case Angle: return "角度";
                case Symmetric: return "对称";
                case Gear: return "齿轮";
                case Width: return "宽度";
                case RackPinion: return "齿条小齿轮";
                case Lock: return "锁定";
                case Screw: return "螺旋";
                case LinearCoupler: return "线性耦合";
                case Slot: return "槽口";
                case Hinge: return "铰链";
                case Slider: return "滑块";
                default: return "类型 " + t;
            }
        }
    }

    [Serializable]
    public sealed class AsmData
    {
        public string File;
        public string Name;
        public List<TopComp> Comps = new List<TopComp>();
        public List<MateInfo> Mates = new List<MateInfo>();
        /// <summary>必然刚性连接的单元对（例如柔性子装配体里都被固定的零部件）。</summary>
        public List<(TopComp, TopComp)> RigidPairs = new List<(TopComp, TopComp)>();
        /// <summary>子装配体里没有任何配合的零部件随锚（子装配体）一起动。不推断关节时按刚性连接处理；推断时如果几何上有接触，改按几何分析。</summary>
        public List<(TopComp, TopComp)> UnmatedPairs = new List<(TopComp, TopComp)>();
        /// <summary>是否读了 B-rep 几何特征。</summary>
        public bool HasGeometry;
        public double TotalMass;
        public List<string> Notes = new List<string>();
        /// <summary>说明性的信息（不需要处理）。</summary>
        public List<string> InfoNotes = new List<string>();
        public IEnumerable<PartInst> AllParts => Comps.SelectMany(c => c.Parts);
    }

    /// <summary>从 SolidWorks 装配体中读出零部件、配合与质量属性。在 SolidWorks 线程上调用。</summary>
    public static class AssemblyReader
    {
        public static AsmData Read(ISldWorks app, ModelDoc2 doc, Action<string> log, bool expandRigid = true, bool scanGeometry = true)
        {
            var asm = (AssemblyDoc)doc;
            try { asm.ResolveAllLightWeightComponents(false); } catch { }
            _allComps.Clear();
            _geomCache.Clear();
            _scanGeometry = scanGeometry;
            _geomTime = System.Diagnostics.Stopwatch.StartNew(); _geomTime.Stop();
            var data = new AsmData { File = doc.GetPathName(), Name = Path.GetFileNameWithoutExtension(doc.GetPathName()) };

            var byName = new Dictionary<string, TopComp>(StringComparer.Ordinal);
            // 展开的子装配体：组件、上级展开子装配体、它在上级里是否固定
            var subs = new List<(Component2 comp, Component2 parent, bool fixedInParent)>();
            var flexNames = new List<string>();
            var rigidNames = new List<string>();
            var suppressed = new List<string>();
            var tops = (asm.GetComponents(true) as object[]) ?? new object[0];

            // 分析单元：顶层零部件；子装配体展开成它的子零部件，按里面的配合分析它们之间的相对运动。
            // 柔性子装配体在 SolidWorks 里本来就能动，总是展开；刚性子装配体在 SolidWorks 里整体不能动，
            // 但做机器人时经常把夹爪、舵机模块这类有关节的部件放在刚性子装配体里，所以默认也展开（expandRigid）。
            void AddUnit(Component2 c, bool grounded, Component2 parentSub)
            {
                if (c.IsSuppressed()) { suppressed.Add(c.Name2); return; }
                var isAsm = string.Equals(Path.GetExtension(c.GetPathName()), ".sldasm", StringComparison.OrdinalIgnoreCase);
                var isFixed = SafeBool(() => c.IsFixed());
                var flexible = isAsm && SafeBool(() => c.Solving == 1);
                if (isAsm && (flexible || expandRigid) && ((c.GetChildren() as object[])?.Length ?? 0) > 0)
                {
                    var subGrounded = parentSub == null ? isFixed : grounded && isFixed;
                    subs.Add((c, parentSub, isFixed && parentSub != null));
                    (flexible ? flexNames : rigidNames).Add(c.Name2);
                    foreach (Component2 ch in (c.GetChildren() as object[]) ?? new object[0]) AddUnit(ch, subGrounded, c);
                    return;
                }
                var t = new TopComp
                {
                    Index = data.Comps.Count,
                    Name2 = c.Name2,
                    File = c.GetPathName(),
                    IsAssembly = isAsm,
                    // 顶层固定 = 固定在地面；子装配体里固定 = 只相对该子装配体固定（子装配体本身也接地时才算接地）
                    Fixed = isFixed && (parentSub == null || grounded),
                    InSub = parentSub?.Name2,
                };
                if (parentSub != null && isFixed) t.FixedInFlex = parentSub.Name2;
                data.Comps.Add(t);
                byName[t.Name2] = t;
                CollectParts(app, doc, c, t, data);
            }
            foreach (Component2 c in tops) AddUnit(c, true, null);
            data.HasGeometry = scanGeometry;
            if (scanGeometry) log?.Invoke($"读取了 {_geomCache.Count} 种零件的几何特征（{_geomTime.Elapsed.TotalSeconds:0.0} 秒）");
            if (suppressed.Count > 0)
                data.Notes.Add($"{suppressed.Count} 个零部件处于压缩状态或文件找不到，没有导出：" + string.Join("、", suppressed.Take(10)) + (suppressed.Count > 10 ? " 等" : ""));
            if (flexNames.Count > 0)
                data.InfoNotes.Add($"{flexNames.Count} 个柔性子装配体已展开，里面的零部件按各自的配合分析：" + string.Join("、", flexNames.Take(8)) + (flexNames.Count > 8 ? " 等" : ""));
            if (rigidNames.Count > 0)
                data.InfoNotes.Add($"{rigidNames.Count} 个子装配体在 SolidWorks 里是刚性的（整体不能动），也按里面的配合分析了关节：" + string.Join("、", rigidNames.Take(8)) + (rigidNames.Count > 8 ? " 等" : "") + "。如果不需要，关掉「分析子装配体内部」重新分析");
            log?.Invoke($"读取到 {data.Comps.Count} 个分析单元、{data.AllParts.Count()} 个零件实例");

            // 每个展开的子装配体选一个"锚"：在子装配体里固定的零部件（没有就取最重的）。
            // 子装配体自己的基准面、原点随锚一起动，所以引用子装配体基准面的配合落到锚上。
            // 由内向外处理（subs 是先父后子加入的），只含下级子装配体的，用下级的锚。
            var anchors = new Dictionary<string, TopComp>();
            for (int i = subs.Count - 1; i >= 0; i--)
            {
                var name = subs[i].comp.Name2;
                var units = data.Comps.Where(c => c.InSub == name).ToList();
                var anchor = units.FirstOrDefault(c => c.FixedInFlex != null)
                             ?? units.OrderByDescending(c => c.Parts.Sum(p => p.Mass)).FirstOrDefault()
                             ?? subs.Where(s => s.parent == subs[i].comp && anchors.ContainsKey(s.comp.Name2)).Select(s => anchors[s.comp.Name2]).FirstOrDefault();
                if (anchor == null) continue;
                anchors[name] = anchor;
                if (!byName.ContainsKey(name)) byName[name] = anchor;
            }
            TopComp AnchorOf(string sub) => sub != null && anchors.TryGetValue(sub, out var a) ? a : null;

            ReadMates(doc, data, byName, null, null, null);
            foreach (var (sub, _, _) in subs)
            {
                if (!(sub.GetModelDoc2() is ModelDoc2 subDoc)) continue;
                ReadMates(subDoc, data, byName, sub, AnchorOf(sub.Name2), app);
            }
            log?.Invoke($"读取到 {data.Mates.Count} 个配合");

            // 刚性连接：
            //  · 同一个子装配体里被固定的零部件和锚刚性连接；
            //  · 子装配体里没有任何配合的零部件随该子装配体（锚）一起动，否则会被当成散件挂到基座上；
            //  · 在上级子装配体里被固定的子装配体，它的锚和上级的锚刚性连接。
            var mated = new HashSet<TopComp>(data.Mates.SelectMany(m => m.Ents).Select(e => e.Comp).Where(c => c != null));
            foreach (var (sub, parent, fixedInParent) in subs)
            {
                var anchor = AnchorOf(sub.Name2);
                if (anchor == null) continue;
                foreach (var u in data.Comps.Where(c => c.InSub == sub.Name2 && (c.FixedInFlex != null || !mated.Contains(c))))
                    if (u != anchor) (u.FixedInFlex != null ? data.RigidPairs : data.UnmatedPairs).Add((anchor, u));
                if (fixedInParent && AnchorOf(parent?.Name2) is TopComp pa && pa != anchor) data.RigidPairs.Add((pa, anchor));
            }

            try
            {
                var mp = (MassProperty)doc.Extension.CreateMassProperty();
                mp.UseSystemUnits = true;
                data.TotalMass = mp.Mass;
            }
            catch { }

            // 自检：多个零件的质量完全相同且都等于总质量，说明按零部件取质量失败了
            var parts = data.AllParts.Where(x => !x.Hidden).ToList();
            if (parts.Count > 1 && parts.All(x => Math.Abs(x.Mass - data.TotalMass) < 1e-9))
                data.Notes.Add("按零部件读取质量属性失败（每个零件都得到了整个装配体的质量），导出的惯性参数不可信");
            return data;
        }

        static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

        static void CollectParts(ISldWorks app, ModelDoc2 asmDoc, Component2 c, TopComp top, AsmData data)
        {
            if (c.IsSuppressed()) return;
            _allComps[c.Name2] = c;
            var children = c.GetChildren() as object[];
            if (children != null && children.Length > 0)
            {
                foreach (Component2 ch in children) CollectParts(app, asmDoc, ch, top, data);
                return;
            }
            if (!string.Equals(Path.GetExtension(c.GetPathName()), ".sldprt", StringComparison.OrdinalIgnoreCase)) return;

            var p = new PartInst
            {
                Name2 = c.Name2,
                File = c.GetPathName(),
                Config = c.ReferencedConfiguration,
                Hidden = SafeBool(() => c.IsHidden(true)),
                Top = top,
            };
            try { p.Pose = PoseOf(app, c); } catch { }
            if (c.GetBox(false, false) is double[] b && b.Length >= 6)
            {
                p.BoxMin = new Vec3(b[0], b[1], b[2]);
                p.BoxMax = new Vec3(b[3], b[4], b[5]);
            }
            try
            {
                if (c.GetModelDoc2() is PartDoc part)
                {
                    string db = "";
                    p.Material = part.GetMaterialPropertyName2(p.Config ?? "", out db);
                    p.HasMaterial = !string.IsNullOrWhiteSpace(p.Material);
                }
            }
            catch { }

            // 质量属性：在零件自己的坐标系里算，再用零部件在装配体里的变换换到装配体坐标。
            // 实测：装配体的 MassProperty.AddBodies 加零部件实体不生效（返回整个装配体的质量），
            //       MassProperty2.SelectedItems 会让 SolidWorks 内部出错，所以都不用。
            p.Inertia = Mat3.Zero;
            try
            {
                if (c.GetModelDoc2() is ModelDoc2 partDoc)
                {
                    var active = partDoc.ConfigurationManager?.ActiveConfiguration?.Name;
                    if (!string.IsNullOrEmpty(p.Config) && active != null && active != p.Config) partDoc.ShowConfiguration2(p.Config);
                    var mp = partDoc.Extension.CreateMassProperty();
                    mp.UseSystemUnits = true;
                    var mass = mp.Mass;
                    var comLocal = (object)mp.CenterOfMass is double[] cm ? Vec3.From(cm) : Vec3.Zero;
                    var iLocal = (object)mp.GetMomentOfInertia(0) is double[] I && I.Length >= 9 ? new Mat3((double[])I.Clone()) : Mat3.Zero;

                    var xf = p.Pose;
                    p.Mass = mass;
                    p.Com = xf.Apply(comLocal);
                    p.Inertia = xf.R * iLocal * xf.R.T();
                }
            }
            catch (COMException ex)
            {
                data.Notes.Add($"{c.Name2}：读取质量属性失败（{ex.Message}）");
                p.Inertia = Mat3.Zero;
            }
            // B-rep 几何特征：在零件文档里读（零件坐标），每种零件（文件 + 配置）只读一次，再按位姿换到装配体坐标
            if (_scanGeometry && !p.Hidden)
            {
                var key = (p.File ?? "").ToLowerInvariant() + "|" + p.Config;
                if (!_geomCache.TryGetValue(key, out var local))
                {
                    _geomTime.Start();
                    // 磁盘缓存：零件文件没改过（路径 + 大小 + 修改时间 + 配置）就直接读上次的结果，大装配体第二次分析快很多
                    if (!FeatureCache.TryLoad(p.File, p.Config, out local))
                    {
                        try
                        {
                            if (c.GetModelDoc2() is ModelDoc2 partDoc)
                            {
                                var active = partDoc.ConfigurationManager?.ActiveConfiguration?.Name;
                                if (!string.IsNullOrEmpty(p.Config) && active != null && active != p.Config) partDoc.ShowConfiguration2(p.Config);
                                local = FeatureScanner.Scan(partDoc);
                                if (local != null) FeatureCache.Save(p.File, p.Config, local);
                            }
                        }
                        catch { local = null; }
                    }
                    _geomTime.Stop();
                    _geomCache[key] = local;
                }
                if (local != null) p.Geom = local.Transformed(p.Pose);
            }
            top.Parts.Add(p);
        }

        static readonly Dictionary<string, PartFeatures> _geomCache = new Dictionary<string, PartFeatures>();
        static bool _scanGeometry;
        static System.Diagnostics.Stopwatch _geomTime = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// 零部件在装配体中的位姿。用 SolidWorks 自己的 MathPoint / MathVector 做变换来取得旋转和平移，
        /// 不依赖对 ArrayData 存储顺序（行优先 / 列优先）的猜测。
        /// </summary>
        internal static Pose PoseOf(ISldWorks app, Component2 c)
        {
            var xf = c.Transform2;
            if (xf == null) return Pose.Identity;
            var mu = (MathUtility)(object)app.GetMathUtility();
            Vec3 Pt(double x, double y, double z)
            {
                var mp = (MathPoint)(object)mu.CreatePoint(new[] { x, y, z });
                var r = (MathPoint)(object)mp.MultiplyTransform(xf);
                return Vec3.From((double[])(object)r.ArrayData);
            }
            var o = Pt(0, 0, 0);
            var ex = Pt(1, 0, 0) - o;
            var ey = Pt(0, 1, 0) - o;
            var ez = Pt(0, 0, 1) - o;
            var R = new Mat3(new[] { ex.X, ey.X, ez.X, ex.Y, ey.Y, ez.Y, ex.Z, ey.Z, ez.Z });   // 列向量是变换后的基向量
            return new Pose(R, o);
        }

        /// <summary>读配合时的上下文。读柔性子装配体内部的配合时，Sub 是该子装配体（顶层里的零部件）。</summary>
        sealed class MateCtx
        {
            public Dictionary<string, TopComp> ByName;
            public Dictionary<string, Component2> AllComps;
            public Component2 Sub;
            public TopComp Anchor;     // 子装配体里被固定的单元：子装配体自身的基准面属于它
            public ISldWorks App;
            public Pose SubPose;
        }

        static readonly Dictionary<string, Component2> _allComps = new Dictionary<string, Component2>(StringComparer.Ordinal);

        static void ReadMates(ModelDoc2 doc, AsmData data, Dictionary<string, TopComp> byName, Component2 sub, TopComp anchor, ISldWorks app)
        {
            var ctx = new MateCtx { ByName = byName, AllComps = _allComps, Sub = sub, Anchor = anchor, App = app };
            if (sub != null) ctx.SubPose = PoseOf(app, sub);
            var f = doc.FirstFeature() as Feature;
            while (f != null)
            {
                if (f.GetTypeName2() == "MateGroup") ReadMateFolder(f, data, ctx);
                f = f.GetNextFeature() as Feature;
            }
        }

        static void ReadMateFolder(Feature folder, AsmData data, MateCtx ctx)
        {
            var sub = folder.GetFirstSubFeature() as Feature;
            while (sub != null)
            {
                var suppressed = SafeBool(() => sub.IsSuppressed());
                if (sub.GetSpecificFeature2() is Mate2 mate)
                {
                    if (!suppressed) data.Mates.Add(ReadMate((ctx.Sub != null ? ctx.Sub.Name2 + "/" : "") + sub.Name, mate, ctx));
                }
                else ReadMateFolder(sub, data, ctx);   // 配合文件夹
                sub = sub.GetNextSubFeature() as Feature;
            }
        }

        /// <summary>按名字逐级往上找所属的分析单元。</summary>
        static TopComp UnitOf(string fullName, Dictionary<string, TopComp> byName)
        {
            var n = fullName;
            while (!string.IsNullOrEmpty(n))
            {
                if (byName.TryGetValue(n, out var u)) return u;
                var i = n.LastIndexOf('/');
                if (i < 0) return null;
                n = n.Substring(0, i);
            }
            return null;
        }

        static MateInfo ReadMate(string name, Mate2 mate, MateCtx ctx)
        {
            var mi = new MateInfo { Name = name, Type = mate.Type, Alignment = mate.Alignment };
            try { mi.Flipped = mate.Flipped; } catch { }
            try { mi.Min = mate.MinimumVariation; mi.Max = mate.MaximumVariation; } catch { }
            try
            {
                var dd = mate.DisplayDimension;
                if (dd != null) mi.Value = dd.GetDimension2(0).SystemValue;
            }
            catch { }
            mi.IsLimit = Math.Abs(mi.Max - mi.Min) > 1e-12;

            for (int i = 0; i < 6; i++)
            {
                MateEntity2 e;
                try { e = mate.MateEntity(i); } catch { break; }
                if (e == null) break;
                var g = new MateGeom { RefType = e.ReferenceType2 };
                Pose? conv = null;   // 子装配体坐标 → 顶层坐标
                try
                {
                    var comp = e.ReferenceComponent;
                    if (ctx.Sub == null)
                    {
                        if (comp != null) { g.Comp = UnitOf(comp.Name2, ctx.ByName); g.CompName = comp.Name2; }
                    }
                    else if (comp == null)
                    {
                        // 引用的是子装配体自己的基准面 / 原点：属于在子装配体里被固定的那部分
                        g.Comp = ctx.Anchor; g.CompName = ctx.Sub.Name2;
                        conv = ctx.SubPose;
                    }
                    else
                    {
                        var full = ctx.Sub.Name2 + "/" + comp.Name2;
                        g.Comp = UnitOf(full, ctx.ByName); g.CompName = full;
                        // 柔性子装配体里零部件在顶层的实际位置和它在子装配体文件里的位置可能不同，
                        // 用"顶层位姿 × 子装配体内位姿的逆"换算配合几何
                        conv = ctx.AllComps.TryGetValue(full, out var topComp)
                            ? PoseOf(ctx.App, topComp) * PoseOf(ctx.App, comp).Inverse()
                            : ctx.SubPose;
                    }
                }
                catch { }
                double[] prm = null;
                try { prm = e.EntityParams as double[]; } catch { }
                Classify(g, prm);
                if (conv.HasValue && g.Kind != GeoKind.Unknown && g.Kind != GeoKind.Component)
                {
                    g.P = conv.Value.Apply(g.P);
                    g.D = conv.Value.ApplyDir(g.D);
                }
                mi.Ents.Add(g);
            }
            return mi;
        }

        /// <summary>按引用类型和 EntityParams（点、方向、半径）判断几何类型。</summary>
        static void Classify(MateGeom g, double[] prm)
        {
            const int Edges = 1, Faces = 2, Vertices = 3, Planes = 4, Axes = 5, Points = 6, SketchSegs = 10, SketchPoints = 11, Components = 20, ExtSketchSegs = 24, ExtSketchPoints = 25, CoordSys = 61;
            if (g.RefType == Components || g.RefType == CoordSys) { g.Kind = GeoKind.Component; return; }
            if (prm == null || prm.Length < 6) { g.Kind = GeoKind.Unknown; return; }
            g.P = Vec3.From(prm, 0);
            g.D = Vec3.From(prm, 3);
            g.Radius = prm.Length > 6 ? prm[6] : 0;
            var hasDir = g.D.Length > 1e-9;
            switch (g.RefType)
            {
                case Faces:
                    g.Kind = !hasDir ? GeoKind.Point : g.Radius > 1e-9 ? GeoKind.Axis : GeoKind.Plane;
                    break;
                case Planes:
                    g.Kind = GeoKind.Plane;
                    break;
                case Edges: case Axes: case SketchSegs: case ExtSketchSegs:
                    g.Kind = hasDir ? GeoKind.Axis : GeoKind.Point;
                    break;
                case Vertices: case Points: case SketchPoints: case ExtSketchPoints:
                    g.Kind = GeoKind.Point;
                    break;
                default:
                    g.Kind = GeoKind.Unknown;
                    break;
            }
            if (hasDir) g.D = g.D.Normalized();
        }

        /// <summary>开发用：把读到的原始数据打印出来，用于核对坐标约定。</summary>
        public static string Dump(AsmData d)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"装配体 {d.Name}  总质量 {d.TotalMass:0.######} kg");
            foreach (var c in d.Comps)
            {
                sb.AppendLine($"[{c.Index}] {c.Name2}{(c.IsAssembly ? "（子装配体）" : "")}{(c.Fixed ? " 固定" : "")}{(c.Flexible ? " 柔性" : "")}");
                foreach (var p in c.Parts)
                    sb.AppendLine($"    {p.Name2}  质量 {p.Mass:0.######} kg  质心 {p.Com}  包围盒 {p.BoxMin} ~ {p.BoxMax}  材料 {(p.HasMaterial ? p.Material : "（无）")}{(p.Hidden ? "  隐藏" : "")}");
            }
            foreach (var m in d.Mates)
            {
                sb.AppendLine($"配合 {m.Name}：{m.TypeName} 对齐 {m.Alignment}{(m.IsLimit ? $"  限位 {m.Min:0.######}~{m.Max:0.######}" : "")}{(m.Value.HasValue ? $"  当前值 {m.Value:0.######}" : "")}");
                foreach (var g in m.Ents) sb.AppendLine("    " + g);
            }
            foreach (var n in d.Notes) sb.AppendLine("注意：" + n);
            return sb.ToString();
        }
    }
}
