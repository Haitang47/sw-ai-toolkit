using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using SwKit.Core.Cfd;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>
    /// CFD 的 3D 视图：模型半透明灰色，桨盘（转子 / 涵道）画成半透明圆盘，流线画成按速度着色的细管（turbo 色标）。
    /// 显示坐标 Z 向上（模型的 Up 方向转到 Z）。相机写法和其他 3D 视图一样：每帧最多更新一次相机。
    /// 点选模式下单击模型，返回点到的位置（模型坐标，米）。
    /// </summary>
    sealed class CfdView
    {
        public FrameworkElement Element => _host;
        public event Action<Vec3> Picked;
        public bool PickMode { get => _pickMode; set { _pickMode = value; _host.Cursor = value ? Cursors.Cross : Cursors.Hand; } }

        readonly Border _host;
        readonly Viewport3D _vp = new Viewport3D { ClipToBounds = true };
        readonly PerspectiveCamera _cam = new PerspectiveCamera { FieldOfView = 35, UpDirection = new Vector3D(0, 0, 1) };
        readonly ModelVisual3D _model = new ModelVisual3D(), _disks = new ModelVisual3D(), _lines = new ModelVisual3D(), _slice = new ModelVisual3D();
        Matrix3D _toDisp = Matrix3D.Identity, _toModel = Matrix3D.Identity;
        double _yaw = -60, _pitch = 28, _dist = 1, _fitDist = 1;
        Point3D _target, _fitTarget;
        Point _last, _down;
        bool _rotating, _panning, _camDirty, _aliased, _pickMode;
        int _tris;

        public CfdView()
        {
            _vp.Camera = _cam;
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(0x70, 0x72, 0x78)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0xD0, 0xD0, 0xD0), new Vector3D(-0.5, 0.7, -1)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x5A, 0x5C, 0x62), new Vector3D(0.7, -0.4, -0.2)));
            lights.Freeze();
            _vp.Children.Add(new ModelVisual3D { Content = lights });
            // 顺序：先画不透明的流线，再画半透明的模型和桨盘（WPF 按顺序混合）
            _vp.Children.Add(_lines);
            _vp.Children.Add(_slice);
            _vp.Children.Add(_model);
            _vp.Children.Add(_disks);
            _host = new Border { Background = Brushes.White, Child = _vp, Cursor = Cursors.Hand };
            _host.MouseLeftButtonDown += (s, e) => { _rotating = true; _down = _last = e.GetPosition(_host); _host.CaptureMouse(); };
            _host.MouseRightButtonDown += (s, e) => { _panning = true; _last = e.GetPosition(_host); _host.CaptureMouse(); };
            _host.MouseLeftButtonUp += (s, e) =>
            {
                var p = e.GetPosition(_host);
                _rotating = false; if (!_panning) End();
                if (_pickMode && (p - _down).Length < 4) TryPick(p);
            };
            _host.MouseRightButtonUp += (s, e) => { _panning = false; if (!_rotating) End(); };
            _host.LostMouseCapture += (s, e) => { _rotating = _panning = false; Restore(); };
            _host.MouseMove += OnMove;
            _host.MouseWheel += (s, e) => { _dist *= Math.Pow(0.88, e.Delta / 120.0); _dist = Math.Max(_fitDist * 0.02, Math.Min(_fitDist * 8, _dist)); _camDirty = true; };
            _host.Loaded += (s, e) => CompositionTarget.Rendering += OnRendering;
            _host.Unloaded += (s, e) => CompositionTarget.Rendering -= OnRendering;
        }

        void End() { _host.ReleaseMouseCapture(); Restore(); }
        void Restore() { if (_aliased) { _aliased = false; RenderOptions.SetEdgeMode(_vp, EdgeMode.Unspecified); } }
        void OnRendering(object s, EventArgs e) { if (_camDirty) { _camDirty = false; UpdateCamera(); } }

        void OnMove(object s, MouseEventArgs e)
        {
            if (!_rotating && !_panning) return;
            var p = e.GetPosition(_host);
            if (!_aliased && (p - _down).Length > 3 && _tris > 120000) { _aliased = true; RenderOptions.SetEdgeMode(_vp, EdgeMode.Aliased); }
            double dx = p.X - _last.X, dy = p.Y - _last.Y; _last = p;
            if (_rotating) { _yaw -= dx * 0.4; _pitch = Math.Max(-85, Math.Min(85, _pitch + dy * 0.4)); }
            else
            {
                var look = _cam.LookDirection; look.Normalize();
                var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1)); right.Normalize();
                var up = Vector3D.CrossProduct(right, look); up.Normalize();
                var k = _dist * 0.0018;
                _target = _target - right * dx * k + up * dy * k;
            }
            _camDirty = true;
        }

        void UpdateCamera()
        {
            double yr = _yaw * Math.PI / 180, pr = _pitch * Math.PI / 180;
            var dir = new Vector3D(Math.Cos(pr) * Math.Cos(yr), Math.Cos(pr) * Math.Sin(yr), Math.Sin(pr));
            _cam.Position = _target + dir * _dist;
            _cam.LookDirection = -dir * _dist;
            _cam.NearPlaneDistance = _dist * 0.01;
            _cam.FarPlaneDistance = _dist * 50;
        }

        public void ResetView() { _yaw = -60; _pitch = 28; _target = _fitTarget; _dist = _fitDist; UpdateCamera(); }
        public void SetAngles(double yaw, double pitch) { _yaw = yaw; _pitch = pitch; UpdateCamera(); }

        /// <summary>模型的 Up 方向转到显示的 Z（只支持坐标轴方向，SolidWorks 默认 +Y）。</summary>
        void SetUp(Vec3 up)
        {
            var u = up.Normalized();
            var m = Matrix3D.Identity;
            if (Math.Abs(u.Y - 1) < 1e-6) m = new Matrix3D(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);        // (x, y, z) → (x, −z, y)
            else if (Math.Abs(u.Y + 1) < 1e-6) m = new Matrix3D(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);   // (x, y, z) → (x, z, −y)
            else if (Math.Abs(u.X - 1) < 1e-6) m = new Matrix3D(0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1);   // x → z
            else if (Math.Abs(u.X + 1) < 1e-6) m = new Matrix3D(0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1);
            else if (Math.Abs(u.Z + 1) < 1e-6) m = new Matrix3D(1, 0, 0, 0, 0, -1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1);
            _toDisp = m;
            _toModel = m; _toModel.Invert();
        }

        Point3D D(Vec3 p) => _toDisp.Transform(new Point3D(p.X, p.Y, p.Z));

        static Material Mat(Color c, byte alpha = 255)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); b.Freeze();
            var m = new DiffuseMaterial(b); m.Freeze(); return m;
        }

        /// <summary>显示模型（三角形，米）。keepView：换了桨盘、流线时不重置视角。</summary>
        public void ShowModel(float[] tris, Vec3 up, bool keepView)
        {
            SetUp(up);
            int n = tris.Length / 9;
            _tris = n;
            var mesh = new MeshGeometry3D();
            var pos = new Point3DCollection(n * 3); var idx = new Int32Collection(n * 3);
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < n * 3; i++)
            {
                var p = D(new Vec3(tris[3 * i], tris[3 * i + 1], tris[3 * i + 2]));
                pos.Add(p); idx.Add(i);
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
            }
            mesh.Positions = pos; mesh.TriangleIndices = idx; mesh.Freeze();
            var mat = Mat(Color.FromRgb(0x8E, 0x92, 0x9A), 0x70);
            var gm = new GeometryModel3D(mesh, mat) { BackMaterial = mat }; gm.Freeze();
            _model.Content = gm;
            if (!keepView && n > 0)
            {
                _fitTarget = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
                var diag = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ));
                _fitDist = Math.Max(diag, 1e-3) * 1.9;
                ResetView();
            }
        }

        /// <summary>桨盘：半透明圆盘 + 外圈；selected 高亮。涵道风扇用青色，转子用橙色。</summary>
        public void ShowDisks(IList<CfdDisk> disks, int selected = -1)
        {
            var g = new Model3DGroup();
            for (int d = 0; d < disks.Count; d++)
            {
                var k = disks[d];
                var a = k.Axis.Normalized(); a.Perpendiculars(out var e1, out var e2);
                var mesh = new MeshGeometry3D();
                int seg = 48;
                for (int i = 0; i <= seg; i++)
                {
                    double t = 2 * Math.PI * i / seg;
                    var dir = e1 * Math.Cos(t) + e2 * Math.Sin(t);
                    mesh.Positions.Add(D(k.Center + dir * k.HubRadius));
                    mesh.Positions.Add(D(k.Center + dir * k.Radius));
                }
                for (int i = 0; i < seg; i++)
                {
                    int b = 2 * i;
                    mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(b + 3);
                    mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 3); mesh.TriangleIndices.Add(b + 2);
                }
                mesh.Freeze();
                var col = d == selected ? Color.FromRgb(0x0A, 0x84, 0xFF) : k.Ducted ? Color.FromRgb(0x2B, 0xB5, 0xC8) : Color.FromRgb(0xFF, 0x9F, 0x0A);
                var mat = Mat(col, d == selected ? (byte)0x90 : (byte)0x55);
                var gm = new GeometryModel3D(mesh, mat) { BackMaterial = mat }; gm.Freeze();
                g.Children.Add(gm);
                // 推力方向的小箭头（轴线，细而短，不挡流线）
                g.Children.Add(Tube(new[] { k.Center, k.Center + a * (k.Radius * 0.35) }, k.Radius * 0.012, Mat(col), null, 0, 1, D));
            }
            g.Freeze();
            _disks.Content = g;
        }

        /// <summary>沿折线的细管（6 边）。speeds 不为 null 时按速度取色标纹理坐标。</summary>
        static GeometryModel3D Tube(IList<Vec3> pts, double r, Material m, IList<float> speeds, double vmin, double vmax, Func<Vec3, Point3D> tf = null)
        {
            var mesh = new MeshGeometry3D();
            AddTube(mesh, pts, r, speeds, vmin, vmax, tf);
            mesh.Freeze();
            var gm = new GeometryModel3D(mesh, m); gm.Freeze();
            return gm;
        }

        static void AddTube(MeshGeometry3D mesh, IList<Vec3> pts, double r, IList<float> speeds, double vmin, double vmax, Func<Vec3, Point3D> tf, int sides = 5)
        {
            if (pts.Count < 2) return;
            tf = tf ?? (p => new Point3D(p.X, p.Y, p.Z));
            int b0 = mesh.Positions.Count;
            Vec3 prevN = Vec3.Zero;
            for (int i = 0; i < pts.Count; i++)
            {
                var t = (i == 0 ? pts[1] - pts[0] : i == pts.Count - 1 ? pts[i] - pts[i - 1] : pts[i + 1] - pts[i - 1]);
                if (t.Length < 1e-12) t = new Vec3(0, 0, 1);
                t = t.Normalized();
                // 平行移动标架，避免管子扭转
                Vec3 nrm;
                if (i == 0) { t.Perpendiculars(out nrm, out _); }
                else { nrm = prevN - t * prevN.Dot(t); if (nrm.Length < 1e-9) t.Perpendiculars(out nrm, out _); nrm = nrm.Normalized(); }
                prevN = nrm;
                var bin = t.Cross(nrm);
                double u = speeds == null ? 0.5 : Math.Max(0.002, Math.Min(0.998, (speeds[i] - vmin) / Math.Max(vmax - vmin, 1e-9)));
                for (int s = 0; s < sides; s++)
                {
                    double a = 2 * Math.PI * s / sides;
                    mesh.Positions.Add(tf(pts[i] + (nrm * Math.Cos(a) + bin * Math.Sin(a)) * r));
                    mesh.TextureCoordinates.Add(new Point(u, 0.5));
                }
            }
            for (int i = 0; i < pts.Count - 1; i++)
                for (int s = 0; s < sides; s++)
                {
                    int a0 = b0 + i * sides + s, a1 = b0 + i * sides + (s + 1) % sides, c0 = a0 + sides, c1 = a1 + sides;
                    mesh.TriangleIndices.Add(a0); mesh.TriangleIndices.Add(c0); mesh.TriangleIndices.Add(c1);
                    mesh.TriangleIndices.Add(a0); mesh.TriangleIndices.Add(c1); mesh.TriangleIndices.Add(a1);
                }
        }

        // ================= 色标 =================

        /// <summary>Turbo 色标（Google 2019，多项式近似），t ∈ [0, 1]。</summary>
        public static Color Turbo(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            double r = 0.13572138 + t * (4.61539260 + t * (-42.66032258 + t * (132.13108234 + t * (-152.94239396 + t * 59.28637943))));
            double g = 0.09140261 + t * (2.19418839 + t * (4.84296658 + t * (-14.18503333 + t * (4.27729857 + t * 2.82956604))));
            double b = 0.10667330 + t * (12.64194608 + t * (-60.58204836 + t * (110.36276771 + t * (-89.90310912 + t * 27.34824973))));
            byte C(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v * 255)));
            return Color.FromRgb(C(r), C(g), C(b));
        }

        static ImageBrush _turbo;
        public static ImageBrush TurboBrush()
        {
            if (_turbo != null) return _turbo;
            int w = 256; var px = new byte[w * 4];
            for (int i = 0; i < w; i++) { var c = Turbo(i / (w - 1.0)); px[4 * i] = c.B; px[4 * i + 1] = c.G; px[4 * i + 2] = c.R; px[4 * i + 3] = 255; }
            var bmp = BitmapSource.Create(w, 1, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            _turbo = new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill };
            _turbo.Freeze();
            return _turbo;
        }

        /// <summary>水平色标条（界面上用）。</summary>
        public static LinearGradientBrush TurboGradient()
        {
            var g = new GradientStopCollection();
            for (int i = 0; i <= 16; i++) g.Add(new GradientStop(Turbo(i / 16.0), i / 16.0));
            var b = new LinearGradientBrush(g, 0); b.Freeze(); return b;
        }

        // ================= 流线 =================

        /// <summary>显示流线（vmax：色标上限 m/s）。radius：管子半径（米）。refit：按模型 + 流线的范围重新取景（第一次显示结果时）。</summary>
        public void ShowStreamlines(IList<CfdStreamline> lines, double vmax, double radius, bool refit = false)
        {
            if (lines == null || lines.Count == 0) { _lines.Content = null; return; }
            if (refit)
            {
                // 下洗流线一直延伸到模型下面很远：取景要把流线也包进来（取 2%–98% 分位，避免个别跑很远的线把画面拉得太小）
                var pts = lines.SelectMany(l => l.Points.Where((p, i) => i % 4 == 0)).Select(D).ToList();
                double zFloor = double.NegativeInfinity;
                if (_model.Content is GeometryModel3D mg3 && mg3.Geometry is MeshGeometry3D mm3)
                {
                    // 模型本身（桨盘在模型上方，流线从那里出发）也要完整地在画面里
                    var b = mm3.Bounds; pts.Add(new Point3D(b.X, b.Y, b.Z)); pts.Add(new Point3D(b.X + b.SizeX, b.Y + b.SizeY, b.Z + b.SizeZ));
                    // 下洗尾迹可以拖到模型下面好几倍远：只取模型下方约一个机身宽度，让机身在画面里够大（下面的流线自然延伸出画面）
                    zFloor = b.Z - 1.1 * Math.Max(b.SizeX, b.SizeY);
                    pts = pts.Where(p => p.Z >= zFloor).ToList();
                }
                if (pts.Count > 10)
                {
                    double Q(IEnumerable<double> v, double q) { var a = v.OrderBy(x => x).ToList(); return a[(int)(q * (a.Count - 1))]; }
                    double x0 = Q(pts.Select(p => p.X), 0.01), x1 = Q(pts.Select(p => p.X), 0.99), y0 = Q(pts.Select(p => p.Y), 0.01), y1 = Q(pts.Select(p => p.Y), 0.99), z0 = Q(pts.Select(p => p.Z), 0.01), z1 = pts.Max(p => p.Z) + 0.05 * (pts.Max(p => p.Z) - Q(pts.Select(p => p.Z), 0.01));
                    _fitTarget = new Point3D((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2);
                    // WPF 的视场角是水平方向的；视图偏高时竖直方向看得更少，距离给足
                    double aspect = Math.Max(0.6, _host.ActualHeight > 0 ? _host.ActualHeight / Math.Max(1, _host.ActualWidth) : 1);
                    _fitDist = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0)) * 1.9 * Math.Max(1, aspect * 1.25);
                    _yaw = -60; _pitch = 24; _target = _fitTarget; _dist = _fitDist; UpdateCamera();
                }
            }
            // 控制总点数（WPF 3D 三角形太多会卡）：每条线最多 ~220 个点
            int budget = 160000, total = lines.Sum(l => l.Points.Count);
            int stride = Math.Max(1, (int)Math.Ceiling(total / (double)budget));
            var mat = new DiffuseMaterial(TurboBrush());
            var mg = new MaterialGroup(); mg.Children.Add(mat); mg.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))));
            mg.Freeze();
            var mesh = new MeshGeometry3D();
            foreach (var l in lines)
            {
                var pts = new List<Vec3>(); var sp = new List<float>();
                for (int i = 0; i < l.Points.Count; i += stride) { pts.Add(l.Points[i]); sp.Add(l.Speeds[i]); }
                if (pts.Count < 2) continue;
                AddTube(mesh, pts, radius, sp, 0, vmax, D, 5);
            }
            mesh.Freeze();
            var gm = new GeometryModel3D(mesh, mg); gm.Freeze();
            _lines.Content = gm;
        }

        /// <summary>
        /// 切面：平面上的速度大小图（img：RGBA 像素，w×h），平面由原点 o 和两个边向量 eu、ev（米，模型坐标）给出。null = 不显示。
        /// </summary>
        public void ShowSlice(byte[] img, int w, int h, Vec3 o, Vec3 eu, Vec3 ev)
        {
            if (img == null) { _slice.Content = null; return; }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, img, w * 4); bmp.Freeze();
            var brush = new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill, Opacity = 0.85 };
            brush.Freeze();
            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(D(o)); mesh.Positions.Add(D(o + eu)); mesh.Positions.Add(D(o + eu + ev)); mesh.Positions.Add(D(o + ev));
            mesh.TextureCoordinates.Add(new Point(0, 1)); mesh.TextureCoordinates.Add(new Point(1, 1)); mesh.TextureCoordinates.Add(new Point(1, 0)); mesh.TextureCoordinates.Add(new Point(0, 0));
            foreach (var i in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(i);
            mesh.Freeze();
            var mat = new EmissiveMaterial(brush); mat.Freeze();
            var mg = new MaterialGroup(); mg.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20)))); mg.Children.Add(mat); mg.Freeze();
            var gm = new GeometryModel3D(mesh, mg) { BackMaterial = mg }; gm.Freeze();
            _slice.Content = gm;
        }

        public void ClearResults() { _lines.Content = null; _slice.Content = null; }

        // ================= 点选 =================

        void TryPick(Point p)
        {
            Vec3? hit = null;
            VisualTreeHelper.HitTest(_vp, null, r =>
            {
                if (r is RayMeshGeometry3DHitTestResult rm && rm.VisualHit == _model)
                {
                    var m = _toModel.Transform(rm.PointHit);
                    hit = new Vec3(m.X, m.Y, m.Z);
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(p));
            if (hit.HasValue) Picked?.Invoke(hit.Value);
        }

        // ================= 截图 =================

        public void SaveImage(string path, double scale = 2)
        {
            _vp.UpdateLayout();
            int w = (int)Math.Max(1, _host.ActualWidth * scale), h = (int)Math.Max(1, _host.ActualHeight * scale);
            var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, _host.ActualWidth, _host.ActualHeight));
                dc.DrawRectangle(new VisualBrush(_vp), null, new Rect(0, 0, _host.ActualWidth, _host.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }
    }
}
