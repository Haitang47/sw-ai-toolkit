using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>
    /// 机器人 3D 预览（WPF 自带的 3D，Z 向上）。网格保持世界坐标（零位），
    /// 显示时用"正运动学位姿 × 平移(−连杆原点)"摆放，所以修改关节后只需要更新变换。
    /// 流畅度：显示用的网格先简化（总数不超过 MaxTriangles）；拖动时相机每帧只更新一次，并临时关掉抗锯齿；材质只用漫反射。
    /// </summary>
    sealed class RobotView
    {
        public FrameworkElement Element => _host;

        /// <summary>预览里显示的三角形总数上限（4K 屏 + 集成显卡也能流畅拖动）。</summary>
        public const int MaxTriangles = 300000;

        readonly Border _host;
        readonly Viewport3D _vp = new Viewport3D { ClipToBounds = true };
        readonly PerspectiveCamera _cam = new PerspectiveCamera { FieldOfView = 35, NearPlaneDistance = 0.001, FarPlaneDistance = 100, UpDirection = new Vector3D(0, 0, 1) };
        readonly ModelVisual3D _axesVisual = new ModelVisual3D();
        readonly Dictionary<Link, GeometryModel3D> _models = new Dictionary<Link, GeometryModel3D>();
        readonly Dictionary<Link, MatrixTransform3D> _xf = new Dictionary<Link, MatrixTransform3D>();
        readonly Dictionary<Joint, GeometryModel3D> _arrows = new Dictionary<Joint, GeometryModel3D>();
        RobotModel _model;
        double _size = 1;
        bool _showAxes = true;

        static readonly Material Normal = MakeMaterial(Color.FromRgb(0xC9, 0xCD, 0xD4));
        static readonly Material Accent = MakeMaterial(Color.FromRgb(0x5A, 0x9C, 0xF0));
        static readonly Material BaseMat = MakeMaterial(Color.FromRgb(0x9E, 0xA3, 0xAC));
        static readonly Material Dark = MakeMaterial(Color.FromRgb(0x3A, 0x3C, 0x42));
        static readonly Material AxisMate = MakeMaterial(Color.FromRgb(0x0A, 0x84, 0xFF));
        static readonly Material AxisInferred = MakeMaterial(Color.FromRgb(0xAF, 0x52, 0xDE));
        static readonly Material AxisOff = MakeMaterial(Color.FromRgb(0xB0, 0xB0, 0xB6));
        static readonly Material AxisHot = MakeMaterial(Color.FromRgb(0xFF, 0x9F, 0x0A));

        double _yaw = -50, _pitch = 25, _dist = 1, _fitDist = 1;
        Point3D _target, _fitTarget;
        Point _last;
        bool _rotating, _panning, _dirty, _hooked;

        public RobotView()
        {
            _vp.Camera = _cam;
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(0x62, 0x64, 0x6A)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0xDC, 0xDC, 0xDC), new Vector3D(-0.5, 0.7, -1)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x66, 0x68, 0x6E), new Vector3D(0.7, -0.4, -0.2)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x3A, 0x3A, 0x3E), new Vector3D(0, 0, 1)));
            _vp.Children.Add(new ModelVisual3D { Content = lights });

            _host = new Border
            {
                Background = new LinearGradientBrush(Color.FromRgb(0xFB, 0xFB, 0xFD), Color.FromRgb(0xEC, 0xED, 0xF1), 90),
                Child = _vp,
                Cursor = Cursors.Hand,
            };
            _host.MouseLeftButtonDown += (s, e) => { _rotating = true; BeginDrag(e); };
            _host.MouseRightButtonDown += (s, e) => { _panning = true; BeginDrag(e); };
            _host.MouseLeftButtonUp += (s, e) => { _rotating = false; EndDrag(); };
            _host.MouseRightButtonUp += (s, e) => { _panning = false; EndDrag(); };
            _host.LostMouseCapture += (s, e) => { _rotating = _panning = false; EndDrag(); };
            _host.MouseMove += OnMove;
            _host.MouseWheel += (s, e) => { _dist *= Math.Pow(0.88, e.Delta / 120.0); _dist = Math.Max(_fitDist * 0.05, Math.Min(_fitDist * 8, _dist)); UpdateCamera(); };
        }

        /// <summary>只用漫反射（高光要多一遍渲染，4K 下拖动明显变慢）。</summary>
        static Material MakeMaterial(Color c)
        {
            var m = new DiffuseMaterial(new SolidColorBrush(c));
            m.Freeze();
            return m;
        }

        void BeginDrag(MouseButtonEventArgs e)
        {
            _last = e.GetPosition(_host);
            _host.CaptureMouse();
            if (!_hooked)
            {
                CompositionTarget.Rendering += OnRendering;
                _hooked = true;
                RenderOptions.SetEdgeMode(_vp, EdgeMode.Aliased);   // 拖动时关掉抗锯齿，松开后恢复
            }
        }

        void EndDrag()
        {
            if (_rotating || _panning) return;
            if (_host.IsMouseCaptured) _host.ReleaseMouseCapture();
            if (_hooked)
            {
                CompositionTarget.Rendering -= OnRendering;
                _hooked = false;
                if (_dirty) UpdateCamera();
                RenderOptions.SetEdgeMode(_vp, EdgeMode.Unspecified);
            }
        }

        void OnRendering(object s, EventArgs e)
        {
            if (!_dirty) return;
            UpdateCamera();
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (!_rotating && !_panning) return;
            var p = e.GetPosition(_host);
            var dx = p.X - _last.X; var dy = p.Y - _last.Y;
            _last = p;
            if (_rotating)
            {
                _yaw -= dx * 0.4;
                _pitch = Math.Max(-85, Math.Min(85, _pitch + dy * 0.4));
            }
            else
            {
                // 在相机平面内平移目标点
                var look = _cam.LookDirection; look.Normalize();
                var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1)); right.Normalize();
                var up = Vector3D.CrossProduct(right, look); up.Normalize();
                var k = _dist * 0.0018;
                _target = _target - right * dx * k + up * dy * k;
            }
            _dirty = true;   // 每帧最多更新一次相机（高回报率鼠标一帧会来好几个移动事件）
        }

        void UpdateCamera()
        {
            _dirty = false;
            double yr = _yaw * Math.PI / 180, pr = _pitch * Math.PI / 180;
            var dir = new Vector3D(Math.Cos(pr) * Math.Cos(yr), Math.Cos(pr) * Math.Sin(yr), Math.Sin(pr));
            _cam.Position = _target + dir * _dist;
            _cam.LookDirection = -dir * _dist;
            _cam.NearPlaneDistance = _dist * 0.01;
            _cam.FarPlaneDistance = _dist * 50;
        }

        public void ResetView()
        {
            _yaw = -50; _pitch = 25;
            _target = _fitTarget; _dist = _fitDist;
            UpdateCamera();
        }

        void ClearScene()
        {
            foreach (var v in _vp.Children.OfType<ModelVisual3D>().Skip(1).ToList()) _vp.Children.Remove(v);
            _axesVisual.Children.Clear();
            _models.Clear(); _xf.Clear(); _arrows.Clear();
        }

        static MeshGeometry3D ToGeometry(Mesh m, Func<float, float, float, Point3D> map, ref Vec3 mn, ref Vec3 mx)
        {
            var n = m.Data.Count / 3;
            var pos = new Point3DCollection(n);
            var idx = new Int32Collection(n);
            for (int i = 0; i < m.Data.Count; i += 3)
            {
                var p = map(m.Data[i], m.Data[i + 1], m.Data[i + 2]);
                idx.Add(pos.Count);
                pos.Add(p);
                if (p.X < mn.X) mn.X = p.X; if (p.Y < mn.Y) mn.Y = p.Y; if (p.Z < mn.Z) mn.Z = p.Z;
                if (p.X > mx.X) mx.X = p.X; if (p.Y > mx.Y) mx.Y = p.Y; if (p.Z > mx.Z) mx.Z = p.Z;
            }
            pos.Freeze(); idx.Freeze();
            var mesh = new MeshGeometry3D { Positions = pos, TriangleIndices = idx };
            mesh.Freeze();
            return mesh;
        }

        /// <summary>
        /// 显示单个零件（标准件预览）：body 是实体网格，cuts 是切除处的深色贴片。网格是 Y 向上（SolidWorks 零件坐标），
        /// 显示时换成本视图的 Z 向上：(x, y, z) → (x, −z, y)。keepView = true 时保留当前视角（只换了参数时不跳动）。
        /// </summary>
        public void LoadMeshes(Mesh body, Mesh cuts, bool keepView)
        {
            _model = null;
            ClearScene();
            var mn = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var mx = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (var (m0, mat) in new[] { (body, Normal), (cuts, Dark) })
            {
                if (m0 == null || m0.Triangles == 0) continue;
                var m = MeshBuilder.Decimate(m0, MaxTriangles);
                var geom = ToGeometry(m, (x, y, z) => new Point3D(x, -z, y), ref mn, ref mx);
                _vp.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(geom, mat) { BackMaterial = mat } });
            }
            if (mn.X > mx.X) return;
            var newTarget = new Point3D((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2);
            var diag = (mx - mn).Length;
            var newFit = Math.Max(diag, 1e-4) * 1.9;
            if (keepView)
            {
                // 尺寸变了时按比例缩放距离，保持零件在画面里的大小
                _dist *= newFit / _fitDist;
                _target = newTarget;
                _fitTarget = newTarget; _fitDist = newFit;
                UpdateCamera();
            }
            else
            {
                _fitTarget = newTarget; _fitDist = newFit;
                ResetView();
            }
        }

        /// <summary>装入模型：每个连杆一个网格（世界坐标，零位）。网格总数超过 MaxTriangles 时按比例简化。</summary>
        public void Load(RobotModel model, Dictionary<string, Mesh> partMeshes)
        {
            _model = model;
            ClearScene();

            var match = MeshBuilder.Match(model.Links.SelectMany(l => l.Parts), partMeshes, out _);
            var perLink = new Dictionary<Link, Mesh>();
            long total = 0;
            foreach (var link in model.Links)
            {
                var merged = new Mesh();
                foreach (var part in link.Parts)
                    if (match.TryGetValue(part, out var key)) merged.Data.AddRange(partMeshes[key].Data);
                if (merged.Triangles == 0) continue;
                perLink[link] = merged;
                total += merged.Triangles;
            }
            var mn = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var mx = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (var kv in perLink)
            {
                var m = kv.Value;
                if (total > MaxTriangles)
                {
                    var budget = (int)Math.Max(1500, (double)MaxTriangles * m.Triangles / total);
                    m = MeshBuilder.Decimate(m, budget);
                }
                var geom = ToGeometry(m, (x, y, z) => new Point3D(x, y, z), ref mn, ref mx);
                var mat = kv.Key == model.Root ? BaseMat : Normal;
                var gm = new GeometryModel3D(geom, mat) { BackMaterial = mat };
                var xf = new MatrixTransform3D();
                _vp.Children.Add(new ModelVisual3D { Content = gm, Transform = xf });
                _models[kv.Key] = gm;
                _xf[kv.Key] = xf;
            }
            if (mn.X <= mx.X)
            {
                _fitTarget = new Point3D((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2);
                var diag = (mx - mn).Length;
                _size = Math.Max(diag, 1e-3);
                _fitDist = _size * 1.9;
            }
            _vp.Children.Add(_axesVisual);
            RefreshAxes();
            ResetView();
        }

        // ================= 关节轴箭头 =================

        /// <summary>是否显示关节轴（运动关节：配合识别的蓝色、几何推断的紫色；没启用的推断关节：灰色）。</summary>
        public bool ShowAxes
        {
            get => _showAxes;
            set { _showAxes = value; RefreshAxes(); }
        }

        /// <summary>关节类型或启用状态改了之后调用，重画关节轴。</summary>
        public void RefreshAxes()
        {
            _axesVisual.Children.Clear();
            _arrows.Clear();
            if (_model == null || !_showAxes) return;
            foreach (var j in _model.Joints)
            {
                bool moving = j.Type != JointType.Fixed;
                bool suggested = !moving && j.Inferred && j.Detected != JointType.Fixed;
                if (!moving && !suggested) continue;
                var mat = !moving ? AxisOff : j.Inferred ? AxisInferred : AxisMate;
                var d = j.Axis.Normalized();
                var origin = j.Type == JointType.Prismatic || j.Detected == JointType.Prismatic ? j.Child.Origin : j.AxisPoint + d * (j.Child.Origin - j.AxisPoint).Dot(d);
                var len = Math.Max(0.02, Math.Min(_size * 0.14, 0.25));
                var geom = Arrow(origin - d * (len * 0.35), d, len, len * (moving ? 0.022 : 0.014));
                var gm = new GeometryModel3D(geom, mat) { BackMaterial = mat };
                _arrows[j] = gm;
                var v = new ModelVisual3D { Content = gm };
                if (_xf.TryGetValue(j.Child, out var xf)) v.Transform = xf;   // 跟着子连杆动（转动时箭头在轴上不变）
                _axesVisual.Children.Add(v);
            }
        }

        /// <summary>从 p 沿 d 的箭头（圆柱杆 + 圆锥头），长 len，杆半径 r。</summary>
        static MeshGeometry3D Arrow(Vec3 p, Vec3 d, double len, double r)
        {
            d.Perpendiculars(out var t1, out var t2);
            var pos = new Point3DCollection(); var idx = new Int32Collection();
            const int n = 14;
            double shaft = len * 0.78, head = r * 2.6;
            Point3D P(Vec3 v) => new Point3D(v.X, v.Y, v.Z);
            for (int i = 0; i < n; i++)
            {
                double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n;
                var u0 = t1 * Math.Cos(a0) + t2 * Math.Sin(a0); var u1 = t1 * Math.Cos(a1) + t2 * Math.Sin(a1);
                var b0 = p + u0 * r; var b1 = p + u1 * r; var c0 = p + d * shaft + u0 * r; var c1 = p + d * shaft + u1 * r;
                var h0 = p + d * shaft + u0 * head; var h1 = p + d * shaft + u1 * head; var tip = p + d * len;
                foreach (var v in new[] { b0, b1, c1, b0, c1, c0, h0, h1, tip, c0, c1, h1, c0, h1, h0, p, b1, b0 })
                {
                    idx.Add(pos.Count); pos.Add(P(v));
                }
            }
            pos.Freeze(); idx.Freeze();
            var m = new MeshGeometry3D { Positions = pos, TriangleIndices = idx };
            m.Freeze();
            return m;
        }

        // ================= 位姿、高亮 =================

        /// <summary>按关节值摆放各连杆。q：关节 → 角度（弧度）或位移（米）。</summary>
        public void SetPose(Func<Joint, double> q)
        {
            if (_model == null) return;
            var world = new Dictionary<Link, Matrix3D> { [_model.Root] = Matrix3D.Identity };
            foreach (var j in _model.Joints)
            {
                if (!world.TryGetValue(j.Parent, out var parentT)) continue;
                var m = Matrix3D.Identity;
                var v = q(j);
                var axis = new Vector3D(j.Axis.X, j.Axis.Y, j.Axis.Z);
                if (j.Type == JointType.Revolute || j.Type == JointType.Continuous) m.Rotate(new Quaternion(axis, v * 180 / Math.PI));
                else if (j.Type == JointType.Prismatic) m.Translate(axis * v);
                var o = j.Child.Origin - j.Parent.Origin;
                m.Translate(new Vector3D(o.X, o.Y, o.Z));
                m.Append(parentT);
                world[j.Child] = m;
            }
            foreach (var kv in _xf)
            {
                if (!world.TryGetValue(kv.Key, out var w)) continue;
                var t = Matrix3D.Identity;
                t.Translate(new Vector3D(-kv.Key.Origin.X, -kv.Key.Origin.Y, -kv.Key.Origin.Z));
                t.Append(w);
                kv.Value.Matrix = t;
            }
        }

        /// <summary>高亮某个关节带动的所有连杆和它的轴（null 取消高亮）。</summary>
        public void Highlight(Joint joint)
        {
            if (_model == null) return;
            var moving = new HashSet<Link>();
            if (joint != null)
            {
                moving.Add(joint.Child);
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var j in _model.Joints)
                        if (moving.Contains(j.Parent) && moving.Add(j.Child)) grew = true;
                }
            }
            foreach (var kv in _models)
            {
                var mat = moving.Contains(kv.Key) ? Accent : kv.Key == _model.Root ? BaseMat : Normal;
                kv.Value.Material = mat;
                kv.Value.BackMaterial = mat;
            }
            foreach (var kv in _arrows)
            {
                var j = kv.Key;
                var mat = j == joint ? AxisHot : j.Type == JointType.Fixed ? AxisOff : j.Inferred ? AxisInferred : AxisMate;
                kv.Value.Material = mat; kv.Value.BackMaterial = mat;
            }
        }
    }
}
