using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace SwKit.Core.Parts
{
    public sealed class PartBuildResult
    {
        public string Path;
        public bool Reused;              // 文件已存在，直接用
        public double Volume, Expected;  // SolidWorks 算的体积和理论体积（立方米）
        public string Material;          // 实际设置上的材料名，没设置时为 null
        public string MaterialNote;
        public double Seconds;
        public Urdf.Vec3 BoxMin, BoxMax;  // SolidWorks 实体的精确包围盒（极值点）
        public int Bodies;               // 实体个数（应为 1；多于 1 说明几何没有连成一体）
        public double VolumeError => Expected > 0 ? Math.Abs(Volume - Expected) / Expected : 0;
    }

    /// <summary>
    /// 在（后台独立的）SolidWorks 里按 PartGeom 建模，设置材料和自定义属性，保存为 .SLDPRT。
    /// 坐标约定见 Op：零件轴线沿 +Y；拉伸和切除画在上视基准面（或其等距面）上，旋转画在前视基准面上。
    /// 所用 API 都在提示词包的自检脚本里实测过（见 prompt-kit/src/macro/sw-api-cheatsheet.md）。
    /// </summary>
    public static class SwPartBuilder
    {
        // 材料库里的候选名称（英文版和中文版 SolidWorks 名称不同，逐个尝试，以读回的结果为准）
        static readonly Dictionary<string, string[]> MaterialNames = new Dictionary<string, string[]>
        {
            ["steel"] = new[] { "Plain Carbon Steel", "普通碳钢", "AISI 1020", "1020" },
            ["stainless"] = new[] { "AISI 304", "304" },
            ["aluminum"] = new[] { "6061 Alloy", "6061 合金", "6061-T6 (SS)", "1060 Alloy", "1060 合金" },
            ["nylon"] = new[] { "Nylon 6/10", "尼龙 6/10", "Nylon 101", "尼龙 101" },
            ["pla"] = new[] { "PLA" },
            ["brass"] = new[] { "Brass", "黄铜" },
            ["rubber"] = new[] { "Natural Rubber", "天然橡胶", "Rubber", "橡胶", "Butyl Rubber", "丁基橡胶" },
            ["abs"] = new[] { "ABS", "ABS PC" },
            ["zinc"] = new[] { "Zinc", "锌" },
            ["castiron"] = new[] { "Gray Cast Iron", "灰铸铁", "Malleable Cast Iron", "可锻铸铁" },
        };
        public static string MaterialText(string key)
        {
            switch (key)
            {
                case "steel": return "钢"; case "stainless": return "不锈钢"; case "aluminum": return "铝合金";
                case "nylon": return "尼龙"; case "pla": return "PLA"; case "brass": return "黄铜"; case "rubber": return "橡胶";
                case "abs": return "ABS"; case "zinc": return "锌合金"; case "castiron": return "铸铁";
                default: return "";
            }
        }

        /// <summary>材料密度（kg/m³），用于界面上估算质量。</summary>
        public static double Density(string key)
        {
            switch (key)
            {
                case "steel": return 7850; case "stainless": return 8000; case "aluminum": return 2700; case "nylon": return 1140;
                case "pla": return 1240; case "brass": return 8500; case "rubber": return 1200; case "abs": return 1050;
                case "zinc": return 6600; case "castiron": return 7200; default: return 0;
            }
        }

        /// <summary>齿轮、带轮等零件里沿 Y 的基准轴的名字（装配配合用）。</summary>
        public const string AxisName = "旋转轴";

        public static string FilePath(PartType type, Dictionary<string, string> p, string libraryRoot) =>
            System.IO.Path.Combine(libraryRoot, type.Category, type.FileName(p) + ".SLDPRT");

        /// <summary>生成零件。文件已存在且 overwrite = false 时直接返回（不需要 SolidWorks）。</summary>
        public static PartBuildResult Build(ISldWorks app, PartType type, Dictionary<string, string> p, string libraryRoot, bool overwrite, Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            var geom = type.Build(p);
            var path = FilePath(type, p, libraryRoot);
            var result = new PartBuildResult { Path = path, Expected = geom.Volume() };
            if (File.Exists(path) && !overwrite) { result.Reused = true; return result; }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

            var m = NewPart(app) ?? throw new InvalidOperationException("新建零件失败：找不到零件模板（工具 → 选项 → 默认模板）");
            try
            {
                var b = new Ctx { M = m, Sm = m.SketchManager, Fm = m.FeatureManager };
                foreach (var op in geom.Ops)
                {
                    log?.Invoke($"建模：{op.Name}");
                    switch (op.Kind)
                    {
                        case OpKind.Extrude:
                            b.Sketch(b.OffsetPlane(op.Dir, op.Y0));
                            foreach (var l in op.Loops) b.Draw(l);
                            b.End();
                            if (b.Fm.FeatureExtrusion3(true, false, false, 0, 0, op.Height, op.Height, false, false, false, false, 0, 0,
                                                       false, false, false, false, true, true, true, 0, 0, false) == null)
                                throw new InvalidOperationException($"「{op.Name}」拉伸失败");
                            break;
                        case OpKind.Cut:
                            b.Sketch(b.OffsetPlane(op.Dir, op.Y0));
                            foreach (var l in op.Loops) b.Draw(l);
                            b.End();
                            if (b.Fm.FeatureCut4(true, false, false, 0, 0, op.Height, op.Height, false, false, false, false, 0, 0,
                                                 false, false, false, false, false, true, true, true, true, false, 0, 0, false, false) == null)
                                throw new InvalidOperationException($"「{op.Name}」切除失败");
                            break;
                        case OpKind.Revolve:
                            b.Sketch(b.Plane(1));
                            var pts = op.Loops[0].Tessellate();
                            b.Draw(op.Loops[0]);
                            b.Sm.CreateCenterLine(0, pts.Min(v => v.Y) - 0.001, 0, 0, pts.Max(v => v.Y) + 0.001, 0);
                            b.End();
                            if (b.Fm.FeatureRevolve2(true, true, false, false, false, false, 0, 0, 2 * Math.PI, 0, false, false, 0, 0, 0, 0, 0, true, true, true) == null)
                                throw new InvalidOperationException($"「{op.Name}」旋转失败");
                            break;
                    }
                }
                m.ClearSelection2(true);

                // 旋转轴：前视和右视基准面的交线（沿 Y），装配时用来加同轴 / 齿轮配合
                if (type.AddAxis)
                {
                    try
                    {
                        b.Plane(1).Select2(false, 0);
                        b.Plane(3).Select2(true, 0);
                        if (m.InsertAxis2(true) && m.FeatureByPositionReverse(0) is Feature ax && ax.GetTypeName2() == "RefAxis") ax.Name = AxisName;
                    }
                    catch { }
                    m.ClearSelection2(true);
                }

                // 材料
                var matKey = type.Material(p);
                if (matKey != null && m is PartDoc part)
                {
                    foreach (var name in MaterialNames[matKey])
                    {
                        try
                        {
                            part.SetMaterialPropertyName2("", "SOLIDWORKS Materials", name);
                            var got = part.GetMaterialPropertyName2("", out _);
                            if (!string.IsNullOrWhiteSpace(got)) { result.Material = got; break; }
                        }
                        catch { }
                    }
                    if (result.Material == null) result.MaterialNote = $"材料库里没找到「{MaterialText(matKey)}」，没有设置材料";
                }

                // 自定义属性（BOM、明细表用）
                var cpm = m.Extension.CustomPropertyManager[""];
                void Prop(string k, string v) { if (!string.IsNullOrEmpty(v)) cpm.Add3(k, 30, v, 2); }   // 30 = 文字，2 = 已存在就替换
                Prop("名称", type.Name);
                Prop("标准", type.Standard);
                Prop("规格", type.Size(p));
                Prop("Description", type.Title(p));
                Prop("材料说明", MaterialText(matKey));
                Prop("来源", "SW 工具箱自动生成");

                m.EditRebuild3();
                var mp = m.Extension.CreateMassProperty();
                mp.UseSystemUnits = true;
                result.Volume = mp.Volume;
                Box(m, out result.BoxMin, out result.BoxMax);
                result.Bodies = ((object[])((PartDoc)m).GetBodies2(0, true))?.Length ?? 0;

                var err = m.SaveAs3(path, 0, 1);
                if (err != 0 || !File.Exists(path)) throw new InvalidOperationException($"保存失败（错误码 {err}）：{path}");
            }
            finally
            {
                try { app.CloseDoc(m.GetTitle()); } catch { }
            }
            result.Seconds = sw.Elapsed.TotalSeconds;
            return result;
        }

        /// <summary>实体的精确包围盒：用各实体沿 ±X、±Y、±Z 的极值点（GetBodyBox 只是近似值）。</summary>
        public static void Box(ModelDoc2 m, out Urdf.Vec3 min, out Urdf.Vec3 max)
        {
            min = new Urdf.Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            max = new Urdf.Vec3(double.MinValue, double.MinValue, double.MinValue);
            if (!(m is PartDoc pd) || !(pd.GetBodies2(0, true) is object[] bodies)) return;
            foreach (Body2 b in bodies)
            {
                double Ext(double dx, double dy, double dz, int k)
                {
                    b.GetExtremePoint(dx, dy, dz, out var x, out var y, out var z);
                    return k == 0 ? x : k == 1 ? y : z;
                }
                min = new Urdf.Vec3(Math.Min(min.X, Ext(-1, 0, 0, 0)), Math.Min(min.Y, Ext(0, -1, 0, 1)), Math.Min(min.Z, Ext(0, 0, -1, 2)));
                max = new Urdf.Vec3(Math.Max(max.X, Ext(1, 0, 0, 0)), Math.Max(max.Y, Ext(0, 1, 0, 1)), Math.Max(max.Z, Ext(0, 0, 1, 2)));
            }
        }

        /// <summary>新建零件：默认模板可能是 "~BLANK..." 占位名，用起来时好时坏，这时到模板文件夹里找 .prtdot。</summary>
        static ModelDoc2 NewPart(ISldWorks app)
        {
            var tpl = app.GetUserPreferenceStringValue(8);
            if (!string.IsNullOrEmpty(tpl) && !tpl.StartsWith("~") && File.Exists(tpl))
            {
                if (app.NewDocument(tpl, 0, 0, 0) is ModelDoc2 d) return d;
            }
            foreach (var dir in (app.GetUserPreferenceStringValue(6) ?? "").Split(';').Where(Directory.Exists))
            {
                var f = Directory.GetFiles(dir, "*.prtdot").FirstOrDefault();
                if (f != null && app.NewDocument(f, 0, 0, 0) is ModelDoc2 d) return d;
            }
            return null;
        }

        sealed class Ctx
        {
            public ModelDoc2 M; public SketchManager Sm; public FeatureManager Fm;
            readonly Dictionary<(Axis, double), Feature> _planes = new Dictionary<(Axis, double), Feature>();

            /// <summary>按特征树顺序取默认基准面：1 = 前视，2 = 上视，3 = 右视（与界面语言无关）。</summary>
            public Feature Plane(int idx)
            {
                var f = M.FirstFeature() as Feature; int n = 0;
                while (f != null) { if (f.GetTypeName2() == "RefPlane" && ++n == idx) return f; f = f.GetNextFeature() as Feature; }
                throw new InvalidOperationException("找不到基准面 " + idx);
            }

            /// <summary>
            /// 法向为 axis、等距 h 的平面：上视（Y）/ 前视（Z）/ 右视（X）基准面或它的等距面
            /// （向负方向等距时坐标映射和拉伸方向不变）。
            /// </summary>
            public Feature OffsetPlane(Axis axis, double h)
            {
                h = Math.Round(h, 9);
                int idx = axis == Axis.Y ? 2 : axis == Axis.Z ? 1 : 3;
                if (Math.Abs(h) < 1e-12) return Plane(idx);
                if (_planes.TryGetValue((axis, h), out var cached)) return cached;
                M.ClearSelection2(true);
                Plane(idx).Select2(false, 0);
                Fm.InsertRefPlane(h > 0 ? 8 : 8 + 256, Math.Abs(h), 0, 0, 0, 0);
                var f = (Feature)M.FeatureByPositionReverse(0);
                if (f == null || f.GetTypeName2() != "RefPlane") throw new InvalidOperationException($"建等距基准面失败（{axis} = {h * 1000:0.###} mm）");
                _planes[(axis, h)] = f;
                return f;
            }

            public void Sketch(Feature plane) { M.ClearSelection2(true); plane.Select2(false, 0); Sm.AddToDB = true; Sm.InsertSketch(true); }
            public void End() { Sm.AddToDB = false; Sm.InsertSketch(true); M.ClearSelection2(true); ((Feature)M.FeatureByPositionReverse(0)).Select2(false, 0); }

            public void Draw(Loop l)
            {
                if (l.IsCircle) { Sm.CreateCircleByRadius(l.Center.X, l.Center.Y, 0, l.Radius); return; }
                foreach (var s in l.Segs)
                {
                    switch (s)
                    {
                        case LineSeg ln: Sm.CreateLine(ln.A.X, ln.A.Y, 0, ln.B.X, ln.B.Y, 0); break;
                        case ArcSeg a:
                            var p0 = a.P0; var p1 = a.P1;   // 草图几何总是从 A0 逆时针画到 A1（顺时针走向只影响轮廓顺序）
                            Sm.CreateArc(a.C.X, a.C.Y, 0, p0.X, p0.Y, 0, p1.X, p1.Y, 0, 1);   // 1 = 逆时针
                            break;
                        case SplineSeg sp:
                            var data = new double[sp.Pts.Count * 3];
                            for (int i = 0; i < sp.Pts.Count; i++) { data[i * 3] = sp.Pts[i].X; data[i * 3 + 1] = sp.Pts[i].Y; }
                            Sm.CreateSpline2(data, true);
                            break;
                    }
                }
            }
        }
    }
}
