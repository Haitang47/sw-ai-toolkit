using System;
using System.IO;
using System.Linq;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SwKit.Core;
using SwKit.Core.Urdf;

namespace SwKit.Cli
{
    /// <summary>
    /// 开发用：在后台独立的 SolidWorks 里生成一个已知答案的测试机械臂（Y 轴向上）：
    ///   底座（固定）─J1 转动（绕 Y，同心+重合）→ 转台 + 支架（锁定）─J2 转动（绕 X，同心+重合）→ 大臂 ─J3 移动（两组面重合 + 距离限位 10~80 mm）→ 滑块
    /// </summary>
    static class DevRobot
    {
        const double MM = 0.001;

        public static int Run(string dir)
        {
            Directory.CreateDirectory(dir);
            dir = Path.GetFullPath(dir);
            using (var s = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
            {
                var app = s.App;
                var tplDir = app.GetUserPreferenceStringValue(6).Split(';')[0];
                var prt = Directory.GetFiles(tplDir, "*.prtdot").First();
                var asmTpl = Directory.GetFiles(tplDir, "*.asmdot").First();

                // ---- 零件（毫米，Y 向上）----
                var basePath = MakePart(app, prt, Path.Combine(dir, "底座.sldprt"), b =>
                {
                    b.Sketch(b.Plane(2)); b.Sm.CreateCenterRectangle(0, 0, 0, 60 * MM, 60 * MM, 0); b.End(); b.Boss(20 * MM);
                    b.Sketch(b.Offset(b.Plane(2), 20 * MM)); b.Sm.CreateCircleByRadius(0, 0, 0, 15 * MM); b.End(); b.Boss(20 * MM);
                });
                var turnPath = MakePart(app, prt, Path.Combine(dir, "转台.sldprt"), b =>
                {
                    b.Sketch(b.Plane(2)); b.Sm.CreateCircleByRadius(0, 0, 0, 40 * MM); b.End(); b.Boss(10 * MM);
                    b.Sketch(b.Plane(2)); b.Sm.CreateCircleByRadius(0, 0, 0, 15 * MM); b.End(); b.CutAll();
                    b.Sketch(b.Offset(b.Plane(2), 10 * MM)); b.Sm.CreateCornerRectangle(30 * MM, -10 * MM, 0, 50 * MM, 10 * MM, 0); b.End(); b.Boss(80 * MM);
                    b.Sketch(b.Plane(3)); b.Sm.CreateCircleByRadius(0, 70 * MM, 0, 5 * MM); b.End(); b.CutAll();
                });
                var armPath = MakePart(app, prt, Path.Combine(dir, "大臂.sldprt"), b =>
                {
                    b.Sketch(b.Plane(3)); b.Sm.CreateCornerRectangle(-10 * MM, -10 * MM, 0, 10 * MM, 110 * MM, 0); b.End(); b.Boss(10 * MM);
                    b.Sketch(b.Plane(3)); b.Sm.CreateCircleByRadius(0, 0, 0, 5 * MM); b.End(); b.CutAll();
                });
                var sliderPath = MakePart(app, prt, Path.Combine(dir, "滑块.sldprt"), b =>
                {
                    b.Sketch(b.Plane(1)); b.Sm.CreateCornerRectangle(0, 0, 0, 10 * MM, 20 * MM, 0); b.End(); b.Boss(10 * MM);
                });
                var bracketPath = MakePart(app, prt, Path.Combine(dir, "支架.sldprt"), b =>
                {
                    b.Sketch(b.Plane(1)); b.Sm.CreateCornerRectangle(0, 0, 0, 20 * MM, 20 * MM, 0); b.End(); b.Boss(20 * MM);
                });

                // ---- 装配体 ----
                var asmDoc = (ModelDoc2)app.NewDocument(asmTpl, 0, 0, 0);
                var asm = (AssemblyDoc)asmDoc;
                var cBase = asm.AddComponent5(basePath, 0, "", false, "", 0, 0, 0);
                var cTurn = asm.AddComponent5(turnPath, 0, "", false, "", 0, 0.03, 0);
                var cArm = asm.AddComponent5(armPath, 0, "", false, "", 0.06, 0.12, 0);
                var cSlider = asm.AddComponent5(sliderPath, 0, "", false, "", 0.06, 0.15, 0.02);
                var cBracket = asm.AddComponent5(bracketPath, 0, "", false, "", -0.03, 0.04, 0.03);

                // J1：转台孔 ⟂ 底座凸台同心 + 转台底面与底座上表面重合
                Mate(asmDoc, MateTypes.Concentric, (cTurn, Cyl(cTurn, 15)), (cBase, Cyl(cBase, 15)));
                Mate(asmDoc, MateTypes.Coincident, (cTurn, Plane(cTurn, new Vec3(0, -1, 0), 0)), (cBase, Plane(cBase, new Vec3(0, 1, 0), 20 * MM)));
                // 支架锁定在转台上
                Lock(asmDoc, cBracket, cTurn);
                // J2：大臂孔与转台臂孔同心 + 大臂侧面与转台臂外侧面重合
                Mate(asmDoc, MateTypes.Concentric, (cArm, Cyl(cArm, 5)), (cTurn, Cyl(cTurn, 5)));
                Mate(asmDoc, MateTypes.Coincident, (cArm, Plane(cArm, new Vec3(-1, 0, 0), 0)), (cTurn, Plane(cTurn, new Vec3(1, 0, 0), 50 * MM)));
                // J3：滑块背面贴大臂正面，滑块侧面与大臂侧面齐平，滑块底面到大臂下端面距离 10~80 mm
                Mate(asmDoc, MateTypes.Coincident, (cSlider, Plane(cSlider, new Vec3(0, 0, -1), 0)), (cArm, Plane(cArm, new Vec3(0, 0, 1), 10 * MM)));
                Mate(asmDoc, MateTypes.Coincident, (cSlider, Plane(cSlider, new Vec3(-1, 0, 0), 0)), (cArm, Plane(cArm, new Vec3(-1, 0, 0), 0)));
                Mate(asmDoc, MateTypes.Distance, (cSlider, Plane(cSlider, new Vec3(0, -1, 0), 0)), (cArm, Plane(cArm, new Vec3(0, -1, 0), -10 * MM)),
                     dist: 30 * MM, upper: 80 * MM, lower: 10 * MM);

                asmDoc.EditRebuild3();
                var asmPath = Path.Combine(dir, "测试机械臂.sldasm");
                var err = asmDoc.SaveAs3(asmPath, 0, 1);
                if (err != 0) throw new InvalidOperationException("保存装配体失败，错误码 " + err);
                Console.WriteLine("已生成 " + asmPath);
                app.CloseAllDocuments(true);
            }
            return 0;
        }

        /// <summary>开发用：在后台 SolidWorks 里只读打开装配体，打印读到的原始数据。</summary>
        public static int Dump(string asmPath)
        {
            using (var s = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
            {
                int e = 0, w = 0;
                var doc = s.App.OpenDoc6(Path.GetFullPath(asmPath), 2, 1 | 2, "", ref e, ref w);
                if (doc == null) { Console.Error.WriteLine("打开失败：" + ExportRunner.LoadErrorText(e)); return 3; }
                var data = AssemblyReader.Read(s.App, doc, Console.WriteLine);
                Console.WriteLine(AssemblyReader.Dump(data));
                s.App.CloseAllDocuments(true);
            }
            return 0;
        }

        // ================= 零件建模 =================

        sealed class PartBuilder
        {
            public ModelDoc2 M; public SketchManager Sm; public FeatureManager Fm;
            public Feature Plane(int idx)
            {
                var f = M.FirstFeature() as Feature; int n = 0;
                while (f != null) { if (f.GetTypeName2() == "RefPlane" && ++n == idx) return f; f = f.GetNextFeature() as Feature; }
                throw new InvalidOperationException("找不到基准面 " + idx);
            }
            public Feature Offset(Feature basePlane, double d)
            {
                M.ClearSelection2(true); basePlane.Select2(false, 0);
                Fm.InsertRefPlane(8, d, 0, 0, 0, 0);
                return (Feature)M.FeatureByPositionReverse(0);
            }
            public void Sketch(Feature plane) { M.ClearSelection2(true); plane.Select2(false, 0); Sm.AddToDB = true; Sm.InsertSketch(true); }
            public void End() { Sm.AddToDB = false; Sm.InsertSketch(true); M.ClearSelection2(true); ((Feature)M.FeatureByPositionReverse(0)).Select2(false, 0); }
            public void Boss(double d)
            {
                if (Fm.FeatureExtrusion3(true, false, false, 0, 0, d, d, false, false, false, false, 0, 0, false, false, false, false, true, true, true, 0, 0, false) == null)
                    throw new InvalidOperationException("拉伸失败");
            }
            public void CutAll()
            {
                if (Fm.FeatureCut4(false, false, false, 1, 1, 0.01, 0.01, false, false, false, false, 0, 0, false, false, false, false, false, true, true, true, true, false, 0, 0, false, false) == null)
                    throw new InvalidOperationException("切除失败");
            }
        }

        static string MakePart(ISldWorks app, string tpl, string path, Action<PartBuilder> build)
        {
            var m = (ModelDoc2)app.NewDocument(tpl, 0, 0, 0);
            var b = new PartBuilder { M = m, Sm = m.SketchManager, Fm = m.FeatureManager };
            build(b);
            m.EditRebuild3();
            var err = m.SaveAs3(path, 0, 1);
            if (err != 0) throw new InvalidOperationException($"保存 {path} 失败，错误码 {err}");
            Console.WriteLine("已生成 " + path);
            return path;   // 保持打开，装配体插入时需要
        }

        // ================= 在零件坐标系里按几何找面，再换成装配体里的对应面 =================

        static Face2[] Faces(Component2 c) =>
            ((object[])((PartDoc)c.GetModelDoc2()).GetBodies2(0, false)).Cast<Body2>().SelectMany(b => ((object[])b.GetFaces()).Cast<Face2>()).ToArray();

        static Face2 Cyl(Component2 c, double radiusMm)
        {
            foreach (var f in Faces(c))
            {
                var s = (Surface)f.GetSurface();
                if (s.IsCylinder() && Math.Abs(((double[])s.CylinderParams)[6] - radiusMm * MM) < 1e-6) return f;
            }
            throw new InvalidOperationException($"{c.Name2} 上找不到半径 {radiusMm} mm 的圆柱面");
        }

        /// <summary>法向为 n（外法向）、并且沿法向的位置为 coord 的平面。</summary>
        static Face2 Plane(Component2 c, Vec3 n, double coord)
        {
            foreach (var f in Faces(c))
            {
                var s = (Surface)f.GetSurface();
                if (!s.IsPlane()) continue;
                var fn = Vec3.From((double[])f.Normal);
                if (fn.Dot(n) < 0.999) continue;
                var box = (double[])f.GetBox();
                var center = new Vec3((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2);
                if (Math.Abs(center.Dot(n) - coord * (n.X + n.Y + n.Z)) < 1e-6) return f;
            }
            throw new InvalidOperationException($"{c.Name2} 上找不到法向 {n}、位置 {coord / MM} mm 的平面");
        }

        static void Mate(ModelDoc2 asmDoc, int type, (Component2 c, Face2 f) a, (Component2 c, Face2 f) b, double dist = 0, double upper = 0, double lower = 0)
        {
            asmDoc.ClearSelection2(true);
            var sel = (SelectionMgr)asmDoc.SelectionManager;
            foreach (var (c, f) in new[] { a, b })
            {
                var sd = sel.CreateSelectData(); sd.Mark = 1;
                var ent = (Entity)c.GetCorrespondingEntity(f);
                if (ent == null || !ent.Select4(true, sd)) throw new InvalidOperationException($"无法选中 {c.Name2} 上的面");
            }
            var mate = ((AssemblyDoc)asmDoc).AddMate5(type, 2, false, dist, upper, lower, 0, 0, 0, 0, 0, false, false, 0, out int err);
            if (mate == null || err != 1) throw new InvalidOperationException($"添加{MateTypes.Name(type)}配合失败，错误码 {err}");
            Console.WriteLine($"已添加配合：{MateTypes.Name(type)}  {a.c.Name2} ↔ {b.c.Name2}");
        }

        static void Lock(ModelDoc2 asmDoc, Component2 a, Component2 b)
        {
            asmDoc.ClearSelection2(true);
            var sel = (SelectionMgr)asmDoc.SelectionManager;
            foreach (var c in new[] { a, b }) { var sd = sel.CreateSelectData(); sd.Mark = 1; c.Select4(true, sd, false); }
            var mate = ((AssemblyDoc)asmDoc).AddMate5(MateTypes.Lock, 0, false, 0, 0, 0, 0, 0, 0, 0, 0, false, false, 0, out int err);
            if (mate == null || err != 1) throw new InvalidOperationException("添加锁定配合失败，错误码 " + err);
            Console.WriteLine($"已添加配合：锁定  {a.Name2} ↔ {b.Name2}");
        }
    }
}
