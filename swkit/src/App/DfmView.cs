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
using SwKit.Core.Dfm;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>
    /// 可制造性检查的 3D 视图：零件按问题上色（悬垂红、薄壁橙、倒扣紫、内角黄、孔浅蓝、折弯青、拔模不足粉、厚壁深红、空腔靛蓝），
    /// 3D 打印时把零件按摆放方向放到打印平台上，板件平放，CNC 时画出装夹方向的箭头，注塑时画出开模方向。写法和应力分析视图一样：一个网格 + 颜色贴图、相机每帧更新一次、模型大时拖动中关抗锯齿。
    /// </summary>
    sealed class DfmView
    {
        public FrameworkElement Element => _host;
        readonly Border _host;
        readonly Viewport3D _vp = new Viewport3D { ClipToBounds = true };
        readonly PerspectiveCamera _cam = new PerspectiveCamera { FieldOfView = 35, UpDirection = new Vector3D(0, 0, 1) };
        readonly ModelVisual3D _content = new ModelVisual3D();
        DfmMesh _mesh;
        Matrix3D _toDisp = Matrix3D.Identity;     // 零件坐标（mm）→ 显示坐标（Z 向上）
        GeometryModel3D _part;
        TriFlag[] _flags; HashSet<int> _selected = new HashSet<int>();

        double _yaw = -55, _pitch = 25, _dist = 100, _fitDist = 100;
        Point3D _target, _fitTarget;
        Point _last, _down;
        bool _rotating, _panning, _camDirty, _aliased;

        // 颜色：0 普通、1 悬垂、2 薄壁、3 倒扣、4 内角、5 孔、6 选中、7 贴平台、8 折弯、9 拔模不足、10 厚壁、11 封闭空腔
        public static readonly Color[] Colors =
        {
            Color.FromRgb(0xC6, 0xCA, 0xD1), Color.FromRgb(0xE8, 0x4A, 0x3F), Color.FromRgb(0xF0, 0x8A, 0x24), Color.FromRgb(0x9B, 0x59, 0xD6),
            Color.FromRgb(0xF2, 0xC2, 0x1B), Color.FromRgb(0x8C, 0xC4, 0xF2), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x7C, 0xC7, 0x8E),
            Color.FromRgb(0x3E, 0xB8, 0xC4), Color.FromRgb(0xE8, 0x6A, 0xB0), Color.FromRgb(0xA8, 0x3A, 0x2A), Color.FromRgb(0x5E, 0x5C, 0xE6),
        };
        static ImageBrush _palette, _paletteGlass;
        ModelVisual3D _overlay;
        Material _partMat, _glassMat;

        public DfmView()
        {
            _vp.Camera = _cam;
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(0x62, 0x64, 0x6A)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0xD8, 0xD8, 0xD8), new Vector3D(-0.5, 0.7, -1)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x66, 0x68, 0x6E), new Vector3D(0.7, -0.4, -0.2)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x3C, 0x3C, 0x40), new Vector3D(0, 0, 1)));
            lights.Freeze();
            _vp.Children.Add(new ModelVisual3D { Content = lights });
            _vp.Children.Add(_content);
            _host = new Border { Background = new LinearGradientBrush(Color.FromRgb(0xFB, 0xFB, 0xFD), Color.FromRgb(0xEC, 0xED, 0xF1), 90), Child = _vp, Cursor = Cursors.Hand };
            _host.MouseLeftButtonDown += (s, e) => { _rotating = true; _down = _last = e.GetPosition(_host); _host.CaptureMouse(); };
            _host.MouseRightButtonDown += (s, e) => { _panning = true; _last = e.GetPosition(_host); _host.CaptureMouse(); };
            _host.MouseLeftButtonUp += (s, e) => { _rotating = false; if (!_panning) End(); };
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
            if (!_aliased && (p - _down).Length > 3 && _mesh != null && _mesh.Tris > 150000) { _aliased = true; RenderOptions.SetEdgeMode(_vp, EdgeMode.Aliased); }
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

        public void ResetView() { _yaw = -55; _pitch = 25; _target = _fitTarget; _dist = _fitDist; UpdateCamera(); }

        /// <summary>设置视角（出报告图用）：yaw / pitch 单位度。</summary>
        public void SetAngles(double yaw, double pitch) { _yaw = yaw; _pitch = pitch; _target = _fitTarget; _dist = _fitDist; UpdateCamera(); }

        static Material Mat(Color c) { var m = new DiffuseMaterial(new SolidColorBrush(c)); m.Freeze(); return m; }

        static ImageBrush Palette(bool glass = false)
        {
            if (!glass && _palette != null) return _palette;
            if (glass && _paletteGlass != null) return _paletteGlass;
            int w = Colors.Length;
            var px = new byte[w * 4];
            byte al = glass ? (byte)0x48 : (byte)255;
            // 半透明时颜色要预乘 alpha（Pbgra）；这里用 Bgra32，WPF 会自己处理
            for (int i = 0; i < w; i++) { px[4 * i] = Colors[i].B; px[4 * i + 1] = Colors[i].G; px[4 * i + 2] = Colors[i].R; px[4 * i + 3] = al; }
            var bmp = BitmapSource.Create(w, 1, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            var brush = new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            brush.Freeze();
            if (glass) _paletteGlass = brush; else _palette = brush;
            return brush;
        }

        /// <summary>
        /// 显示零件。up：朝上的方向（零件坐标；3D 打印的摆放方向、板件的板面法向、注塑的开模方向；null = 按 SolidWorks 的 Y 向上显示）；
        /// printer：画打印平台；setups：箭头（CNC 装夹方向：刀从箭头那一侧下来；arrowsOut = true 时箭头朝外，表示注塑开模时两半模具分开的方向）。
        /// keepView：只换了颜色或参数时保持视角。
        /// </summary>
        public void Show(DfmMesh m, TriFlag[] flags, Vec3? up, Printer printer, IList<Vec3> setups, bool keepView, bool arrowsOut = false)
        {
            bool newMesh = _mesh != m;
            _mesh = m; _flags = flags; _selected.Clear();
            _content.Children.Clear();
            // 显示坐标：打印时把摆放方向转到 +Z、放在平台上；CNC 时 SolidWorks 的 Y 向上 → 显示的 Z 向上
            Vec3 d = up ?? new Vec3(0, 1, 0);
            var rot = RotationTo(d);
            m.Extent(d, out var hMin, out _);
            var c = (m.Min + m.Max) / 2;
            var cr = rot.Transform(new Point3D(c.X, c.Y, c.Z));
            var mtx = rot; mtx.Translate(new Vector3D(-cr.X, -cr.Y, -hMin));
            _toDisp = mtx;
            var pos = new Point3DCollection(m.T.Length); var idx = new Int32Collection(m.T.Length);
            for (int t = 0; t < m.Tris; t++)
                for (int k = 0; k < 3; k++)
                {
                    int vi = m.T[3 * t + k];
                    idx.Add(pos.Count);
                    pos.Add(_toDisp.Transform(new Point3D(m.V[3 * vi], m.V[3 * vi + 1], m.V[3 * vi + 2])));
                }
            pos.Freeze(); idx.Freeze();
            var mat = new DiffuseMaterial(Palette()); mat.Freeze();
            _partMat = mat;
            _glassMat = new DiffuseMaterial(Palette(true)); _glassMat.Freeze();
            _overlay = null;
            _part = new GeometryModel3D(new MeshGeometry3D { Positions = pos, TriangleIndices = idx, TextureCoordinates = Uv() }, mat) { BackMaterial = mat };
            _content.Children.Add(new ModelVisual3D { Content = _part });
            double size = m.Diag;
            if (printer != null && up != null)
            {
                // 打印平台：机器的大小，半透明
                double px = printer.X / 2, py = printer.Y / 2;
                var plate = new MeshGeometry3D
                {
                    Positions = new Point3DCollection { new Point3D(-px, -py, -0.05), new Point3D(px, -py, -0.05), new Point3D(px, py, -0.05), new Point3D(-px, py, -0.05) },
                    TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
                };
                var pm = Mat(Color.FromArgb(0x50, 0x80, 0x88, 0x96));
                _content.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(plate, pm) { BackMaterial = pm } });
                // 网格线
                var lines = new MeshGeometry3D(); var lp = new Point3DCollection(); var li = new Int32Collection();
                double step = printer.X > 200 ? 50 : 25, w = Math.Max(0.15, size * 0.002);
                for (double x = -px; x <= px + 1e-6; x += step) Quad(lp, li, new Point3D(x - w, -py, 0), new Point3D(x + w, -py, 0), new Point3D(x + w, py, 0), new Point3D(x - w, py, 0));
                for (double y = -py; y <= py + 1e-6; y += step) Quad(lp, li, new Point3D(-px, y - w, 0), new Point3D(px, y - w, 0), new Point3D(px, y + w, 0), new Point3D(-px, y + w, 0));
                lines.Positions = lp; lines.TriangleIndices = li;
                var lm = Mat(Color.FromArgb(0x60, 0x60, 0x66, 0x70));
                _content.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(lines, lm) { BackMaterial = lm } });
            }
            if (setups != null)
            {
                // CNC：装夹方向（刀从箭头那一侧下来）
                var dispMin = new Point3D(double.MaxValue, double.MaxValue, double.MaxValue);
                double R = size * 0.75;
                int i = 0;
                foreach (var a in setups)
                {
                    var dir = _toDisp.Transform(new Vector3D(a.X, a.Y, a.Z)); dir.Normalize();
                    var ctr = _toDisp.Transform(new Point3D(c.X, c.Y, c.Z));
                    var near = ctr + dir * (R * 0.55);
                    var far = ctr + dir * (R * 0.95);
                    if (arrowsOut) AddArrow(near, far, size * 0.018, i == 0 ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromRgb(0x34, 0xC7, 0x59));
                    else AddArrow(far, near, size * 0.018, i == 0 ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromRgb(0x34, 0xC7, 0x59));
                    i++;
                }
            }
            if (newMesh || !keepView)
            {
                var b = Bounds(pos);
                _fitTarget = new Point3D((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2, (b.Min.Z + b.Max.Z) / 2);
                _fitDist = Math.Max(size, 1) * 2.2;
                ResetView();
            }
        }

        static (Point3D Min, Point3D Max) Bounds(Point3DCollection pts)
        {
            double x0 = double.MaxValue, y0 = x0, z0 = x0, x1 = double.MinValue, y1 = x1, z1 = x1;
            foreach (var p in pts) { if (p.X < x0) x0 = p.X; if (p.Y < y0) y0 = p.Y; if (p.Z < z0) z0 = p.Z; if (p.X > x1) x1 = p.X; if (p.Y > y1) y1 = p.Y; if (p.Z > z1) z1 = p.Z; }
            return (new Point3D(x0, y0, z0), new Point3D(x1, y1, z1));
        }

        static void Quad(Point3DCollection p, Int32Collection i, Point3D a, Point3D b, Point3D c, Point3D d)
        {
            int o = p.Count; p.Add(a); p.Add(b); p.Add(c); p.Add(d);
            i.Add(o); i.Add(o + 1); i.Add(o + 2); i.Add(o); i.Add(o + 2); i.Add(o + 3);
        }

        void AddArrow(Point3D from, Point3D to, double r, Color color)
        {
            var axis = to - from; double len = axis.Length; axis.Normalize();
            var helper = Math.Abs(axis.Z) < 0.9 ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0);
            var u = Vector3D.CrossProduct(axis, helper); u.Normalize(); var v = Vector3D.CrossProduct(axis, u);
            var pos = new Point3DCollection(); var idx = new Int32Collection();
            int n = 16; double headLen = len * 0.3;
            var neck = to - axis * headLen;
            for (int i = 0; i < n; i++)
            {
                double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n;
                var d0 = u * Math.Cos(a0) + v * Math.Sin(a0); var d1 = u * Math.Cos(a1) + v * Math.Sin(a1);
                Quad(pos, idx, from + d0 * r, from + d1 * r, neck + d1 * r, neck + d0 * r);
                int o = pos.Count; pos.Add(neck + d0 * r * 2.6); pos.Add(neck + d1 * r * 2.6); pos.Add(to);
                idx.Add(o); idx.Add(o + 1); idx.Add(o + 2);
            }
            var m = Mat(color);
            _content.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(new MeshGeometry3D { Positions = pos, TriangleIndices = idx }, m) { BackMaterial = m } });
        }

        /// <summary>把方向 d 转到 +Z 的旋转。</summary>
        static Matrix3D RotationTo(Vec3 d)
        {
            var from = new Vector3D(d.X, d.Y, d.Z); from.Normalize();
            var to = new Vector3D(0, 0, 1);
            var m = Matrix3D.Identity;
            var axis = Vector3D.CrossProduct(from, to);
            double dot = Vector3D.DotProduct(from, to);
            if (axis.Length < 1e-9) { if (dot < 0) m.Rotate(new Quaternion(new Vector3D(1, 0, 0), 180)); return m; }
            m.Rotate(new Quaternion(axis, Math.Acos(Math.Max(-1, Math.Min(1, dot))) * 180 / Math.PI));
            return m;
        }

        int ColorIndex(int t)
        {
            if (_selected.Contains(t)) return 6;
            var f = _flags == null ? TriFlag.None : _flags[t];
            if ((f & TriFlag.Undercut) != 0) return 3;
            if ((f & TriFlag.Cavity) != 0) return 11;
            if ((f & TriFlag.Thick) != 0) return 10;
            if ((f & TriFlag.Thin) != 0) return 2;
            if ((f & TriFlag.Overhang) != 0) return 1;
            if ((f & TriFlag.Draft) != 0) return 9;
            if ((f & TriFlag.Corner) != 0) return 4;
            if ((f & TriFlag.Bend) != 0) return 8;
            if ((f & TriFlag.Hole) != 0) return 5;
            if ((f & TriFlag.OnPlate) != 0) return 7;
            return 0;
        }

        PointCollection Uv()
        {
            int n = Colors.Length;
            var uv = new PointCollection(_mesh.T.Length);
            for (int t = 0; t < _mesh.Tris; t++)
            {
                var p = new Point((ColorIndex(t) + 0.5) / n, 0.5);
                uv.Add(p); uv.Add(p); uv.Add(p);
            }
            uv.Freeze();
            return uv;
        }

        /// <summary>高亮一组三角形并把镜头移过去（点问题列表里的一条）。</summary>
        public void Locate(IEnumerable<int> tris, Vec3 loc)
        {
            if (_part == null) return;
            _selected = new HashSet<int>(tris);
            ((MeshGeometry3D)_part.Geometry).TextureCoordinates = Uv();
            // 问题可能在零件里面（例如封闭的 T 型槽）：零件变半透明，问题面单独用不透明的颜色画出来
            if (_overlay != null) _content.Children.Remove(_overlay);
            var op = new Point3DCollection(); var oi = new Int32Collection();
            foreach (var t in _selected)
            {
                var off = _mesh.N[t] * Math.Max(0.002, _mesh.Diag * 2e-4);
                for (int k = 0; k < 3; k++)
                {
                    int vi = _mesh.T[3 * t + k];
                    oi.Add(op.Count);
                    op.Add(_toDisp.Transform(new Point3D(_mesh.V[3 * vi] + off.X, _mesh.V[3 * vi + 1] + off.Y, _mesh.V[3 * vi + 2] + off.Z)));
                }
            }
            var sm = Mat(Colors[6]);
            _overlay = new ModelVisual3D { Content = new GeometryModel3D(new MeshGeometry3D { Positions = op, TriangleIndices = oi }, sm) { BackMaterial = sm } };
            _content.Children.Insert(0, _overlay);
            _part.Material = _glassMat; _part.BackMaterial = _glassMat;
            var p = _toDisp.Transform(new Point3D(loc.X, loc.Y, loc.Z));
            double r = 0; var n = Vec3.Zero;
            foreach (var t in _selected) { r = Math.Max(r, (_mesh.C[t] - loc).Length); n = n + _mesh.N[t] * _mesh.A[t]; }
            // 从问题面朝向的那一侧看过去（朝下的悬垂面就从下面看）
            if (n.Length > 1e-9)
            {
                var dn = _toDisp.Transform(new Vector3D(n.X, n.Y, n.Z)); dn.Normalize();
                _pitch = Math.Max(-75, Math.Min(75, Math.Asin(Math.Max(-1, Math.Min(1, dn.Z))) * 180 / Math.PI));
                if (Math.Abs(dn.Z) < 0.97) _yaw = Math.Atan2(dn.Y, dn.X) * 180 / Math.PI;
                // 正对着看太平，稍微偏一点角度更有立体感
                _yaw += 20; _pitch = Math.Max(-75, Math.Min(75, _pitch + (dn.Z >= 0 ? -10 : 10)));
            }
            _target = p;
            _dist = Math.Max(_fitDist * 0.35, Math.Min(_fitDist, r * 4 + _mesh.Diag * 0.35));
            UpdateCamera();
        }

        public void ClearSelection()
        {
            if (_part == null || _selected.Count == 0) return;
            _selected.Clear();
            ((MeshGeometry3D)_part.Geometry).TextureCoordinates = Uv();
            if (_overlay != null) { _content.Children.Remove(_overlay); _overlay = null; }
            _part.Material = _partMat; _part.BackMaterial = _partMat;
        }

        /// <summary>按 dpi 把当前视图存成 PNG（报告用 300 dpi）。</summary>
        public void SaveImage(string path, double dpi = 300)
        {
            var el = _host;
            double w = Math.Max(1, el.ActualWidth), h = Math.Max(1, el.ActualHeight);
            double scale = dpi / 96;
            var rtb = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), dpi, dpi, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var fs = File.Create(path)) enc.Save(fs);
        }
    }
}
