using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using SwKit.Core.Fea;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>
    /// 应力分析的 3D 视图：按面显示零件（可点选），算完后显示应力云图和放大的变形，拓扑优化时显示优化后的形状。
    /// 网格坐标是零件坐标（Y 向上），显示时换成 Z 向上：(x, y, z) → (x, −z, y)。
    /// 左键拖动旋转、右键拖动平移、滚轮缩放；左键单击选面（按住 Ctrl 多选）。
    ///
    /// 性能：所有面合成一个网格（一次绘制），每个面的颜色放在一张小贴图里（改颜色只改贴图像素）；
    /// 不用高光材质（多一遍绘制）；相机每帧最多更新一次；网格很大时拖动中临时关闭抗锯齿。
    /// </summary>
    sealed class FeaView
    {
        public FrameworkElement Element => _host;
        public event Action<int, bool> FacePicked;     // 面编号（-1 = 点在空白处），是否按住 Ctrl

        readonly Border _host;
        readonly Viewport3D _vp = new Viewport3D { ClipToBounds = true };
        readonly PerspectiveCamera _cam = new PerspectiveCamera { FieldOfView = 35, UpDirection = new Vector3D(0, 0, 1) };
        readonly ModelVisual3D _content = new ModelVisual3D();
        FeMesh _mesh;

        // 按面显示时：一个网格 + 面颜色贴图
        GeometryModel3D _faceModel;
        int[] _faceOfTri;                 // 显示三角形 → 面编号
        int[] _faceIds;                   // 贴图里第 k 个像素对应的面编号
        Dictionary<int, int> _texIndex;   // 面编号 → 像素序号
        WriteableBitmap _faceTex;
        const int TexW = 256;
        int _displayTris;

        public static readonly Color CNormal = Color.FromRgb(0xC6, 0xCA, 0xD1), CSelected = Color.FromRgb(0x7F, 0xB8, 0xF7),
                                     CFixed = Color.FromRgb(0x34, 0x7D, 0xE0), CLoad = Color.FromRgb(0xF0, 0x8A, 0x24), CHover = Color.FromRgb(0xA8, 0xCF, 0xFA),
                                     CTopo = Color.FromRgb(0xB9, 0xBE, 0xC8);

        double _yaw = -50, _pitch = 25, _dist = 1, _fitDist = 1;
        Point3D _target, _fitTarget;
        Point _last, _down;
        bool _rotating, _panning, _moved, _camDirty, _aliasedForDrag;

        public FeaView()
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
            _host.MouseLeftButtonDown += (s, e) => { _rotating = true; _moved = false; _down = _last = e.GetPosition(_host); _host.CaptureMouse(); };
            _host.MouseRightButtonDown += (s, e) => { _panning = true; _last = e.GetPosition(_host); _host.CaptureMouse(); };
            _host.MouseLeftButtonUp += (s, e) =>
            {
                _rotating = false; if (!_panning) EndDrag();
                if (!_moved) Pick(e.GetPosition(_vp), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            };
            _host.MouseRightButtonUp += (s, e) => { _panning = false; if (!_rotating) EndDrag(); };
            _host.LostMouseCapture += (s, e) => { _rotating = _panning = false; RestoreQuality(); };
            _host.MouseMove += OnMove;
            _host.MouseWheel += (s, e) => { _dist *= Math.Pow(0.88, e.Delta / 120.0); _dist = Math.Max(_fitDist * 0.03, Math.Min(_fitDist * 8, _dist)); RequestCamera(); };
            // 相机每帧最多更新一次（高回报率鼠标一帧会来好几次移动事件）
            _host.Loaded += (s, e) => CompositionTarget.Rendering += OnRendering;
            _host.Unloaded += (s, e) => CompositionTarget.Rendering -= OnRendering;
        }

        void EndDrag() { _host.ReleaseMouseCapture(); RestoreQuality(); }

        void RestoreQuality()
        {
            if (!_aliasedForDrag) return;
            _aliasedForDrag = false;
            RenderOptions.SetEdgeMode(_vp, EdgeMode.Unspecified);
        }

        void OnRendering(object sender, EventArgs e)
        {
            if (!_camDirty) return;
            _camDirty = false;
            UpdateCamera();
        }

        void RequestCamera() => _camDirty = true;

        static Point3D Disp(Vec3 p) => new Point3D(p.X, -p.Z, p.Y);

        static readonly Dictionary<Color, Material> _matCache = new Dictionary<Color, Material>();
        static Material Mat(Color c)
        {
            if (_matCache.TryGetValue(c, out var m)) return m;
            var d = new DiffuseMaterial(new SolidColorBrush(c));
            d.Freeze();
            return _matCache[c] = d;
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (!_rotating && !_panning) return;
            var p = e.GetPosition(_host);
            if ((p - _down).Length > 3)
            {
                _moved = true;
                // 网格很大时，拖动过程中关闭抗锯齿（松开后恢复），4K 屏上更跟手
                if (!_aliasedForDrag && _displayTris > 150000) { _aliasedForDrag = true; RenderOptions.SetEdgeMode(_vp, EdgeMode.Aliased); }
            }
            var dx = p.X - _last.X; var dy = p.Y - _last.Y;
            _last = p;
            if (_rotating) { _yaw -= dx * 0.4; _pitch = Math.Max(-85, Math.Min(85, _pitch + dy * 0.4)); }
            else
            {
                var look = _cam.LookDirection; look.Normalize();
                var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1)); right.Normalize();
                var up = Vector3D.CrossProduct(right, look); up.Normalize();
                var k = _dist * 0.0018;
                _target = _target - right * dx * k + up * dy * k;
            }
            RequestCamera();
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

        public void ResetView() { _yaw = -50; _pitch = 25; _target = _fitTarget; _dist = _fitDist; UpdateCamera(); }

        /// <summary>按指定方位看整个零件（出报告截图用）。yaw：绕竖直轴的角度；pitch：仰角。</summary>
        public void SetAngles(double yaw, double pitch) { _yaw = yaw; _pitch = pitch; _target = _fitTarget; _dist = _fitDist; UpdateCamera(); }
        public (double yaw, double pitch, double dist, Point3D target) SaveCamera() => (_yaw, _pitch, _dist, _target);
        public void RestoreCamera((double yaw, double pitch, double dist, Point3D target) c) { (_yaw, _pitch, _dist, _target) = c; UpdateCamera(); }

        void Pick(Point p, bool ctrl)
        {
            int face = -1;
            if (_faceModel != null && _faceOfTri != null)
            {
                VisualTreeHelper.HitTest(_vp, null, r =>
                {
                    if (r is RayMeshGeometry3DHitTestResult hit && hit.ModelHit == _faceModel)
                    {
                        var tri = hit.VertexIndex1 / 3;
                        if (tri >= 0 && tri < _faceOfTri.Length) face = _faceOfTri[tri];
                        return HitTestResultBehavior.Stop;
                    }
                    return HitTestResultBehavior.Continue;
                }, new PointHitTestParameters(p));
            }
            FacePicked?.Invoke(face, ctrl);
        }

        // ================= 显示零件（按面） =================

        public void LoadMesh(FeMesh mesh)
        {
            _mesh = mesh;
            BuildFaceModel();
            mesh.Bounds(out var mn, out var mx);
            _fitTarget = Disp((mn + mx) * 0.5);
            _fitDist = Math.Max((mx - mn).Length, 1e-4) * 1.9;
            ResetView();
        }

        void BuildFaceModel()
        {
            _content.Children.Clear();
            var mesh = _mesh;
            _faceIds = mesh.Faces.Select(f => f.Id).ToArray();
            _texIndex = new Dictionary<int, int>();
            for (int k = 0; k < _faceIds.Length; k++) _texIndex[_faceIds[k]] = k;
            int texH = Math.Max(1, (_faceIds.Length + TexW - 1) / TexW);
            _faceTex = new WriteableBitmap(TexW, texH, 96, 96, PixelFormats.Bgra32, null);

            var pos = new Point3DCollection(); var idx = new Int32Collection(); var tex = new PointCollection();
            var faceOfTri = new List<int>();
            foreach (var f in mesh.Faces)
            {
                int k = _texIndex[f.Id];
                var uv = new Point((k % TexW + 0.5) / TexW, (k / TexW + 0.5) / texH);
                foreach (var t in f.Tris)
                {
                    int before = pos.Count;
                    AddTri6(mesh, t, pos, idx, null, 0, null, null);
                    for (int i = before; i < pos.Count; i++) tex.Add(uv);
                    for (int i = 0; i < (pos.Count - before) / 3; i++) faceOfTri.Add(f.Id);
                }
            }
            pos.Freeze(); idx.Freeze(); tex.Freeze();
            _faceOfTri = faceOfTri.ToArray();
            _displayTris = _faceOfTri.Length;
            var brush = new ImageBrush(_faceTex) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetCachingHint(brush, CachingHint.Cache);
            var mat = new DiffuseMaterial(brush);
            _faceModel = new GeometryModel3D(new MeshGeometry3D { Positions = pos, TriangleIndices = idx, TextureCoordinates = tex }, mat) { BackMaterial = mat };
            _content.Children.Add(new ModelVisual3D { Content = _faceModel });
            SetFaceColors(_ => CNormal);
            AddAxes();
        }

        /// <summary>二次三角形拆成 4 个小三角形显示（曲面更圆）。u 为 null 时不变形。</summary>
        static void AddTri6(FeMesh m, int t, Point3DCollection pos, Int32Collection idx, double[] u, double scale, PointCollection tex, Func<int, double> value)
        {
            int[] sub = { 0, 3, 5, 3, 1, 4, 5, 4, 2, 3, 4, 5 };
            foreach (var k in sub)
            {
                int n = m.Tris[6 * t + k];
                var p = m.P(n);
                if (u != null) p = p + new Vec3(u[3 * n], u[3 * n + 1], u[3 * n + 2]) * scale;
                idx.Add(pos.Count);
                pos.Add(Disp(p));
                tex?.Add(new Point(value(n), 0.5));
            }
        }

        void AddAxes()
        {
            _mesh.Bounds(out var mn, out var mx);
            var L = (mx - mn).Length * 0.18; var w = L * 0.025;
            void Arrow(Vec3 d, Color c)
            {
                var pos = new Point3DCollection(); var idx = new Int32Collection();
                // 细长的方棱柱，从原点沿 d
                Vec3 a = Math.Abs(d.X) > 0.5 ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
                var u = d.Cross(a).Normalized() * w; var v = d.Cross(u).Normalized() * w;
                var corners = new[] { u + v, u - v, -u - v, -u + v };
                for (int i = 0; i < 4; i++)
                {
                    var p0 = corners[i]; var p1 = corners[(i + 1) % 4]; var q0 = p0 + d * L; var q1 = p1 + d * L;
                    foreach (var p in new[] { p0, p1, q1, p0, q1, q0 }) { idx.Add(pos.Count); pos.Add(Disp(p)); }
                }
                var m = Mat(c);
                _content.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(new MeshGeometry3D { Positions = pos, TriangleIndices = idx }, m) { BackMaterial = m } });
            }
            Arrow(new Vec3(1, 0, 0), Color.FromRgb(0xE5, 0x48, 0x4D));
            Arrow(new Vec3(0, 1, 0), Color.FromRgb(0x30, 0xA4, 0x6C));
            Arrow(new Vec3(0, 0, 1), Color.FromRgb(0x2F, 0x6F, 0xE4));
        }

        /// <summary>给每个面上色（选中、固定、受载）：只改颜色贴图的像素，不重建网格。</summary>
        public void SetFaceColors(Func<int, Color> colorOf)
        {
            if (_faceTex == null || _faceIds == null) return;
            int w = _faceTex.PixelWidth, h = _faceTex.PixelHeight;
            var px = new byte[w * h * 4];
            for (int k = 0; k < _faceIds.Length; k++)
            {
                var c = colorOf(_faceIds[k]);
                px[4 * k] = c.B; px[4 * k + 1] = c.G; px[4 * k + 2] = c.R; px[4 * k + 3] = 255;
            }
            _faceTex.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        }

        // ================= 结果：应力云图 =================

        public static readonly Color[] Palette =
        {
            Color.FromRgb(0x2B, 0x5C, 0xD6), Color.FromRgb(0x2E, 0xA8, 0xE8), Color.FromRgb(0x3C, 0xC8, 0x9B),
            Color.FromRgb(0xC4, 0xD8, 0x3A), Color.FromRgb(0xF5, 0xB0, 0x2E), Color.FromRgb(0xE8, 0x5A, 0x2A), Color.FromRgb(0xC8, 0x1E, 0x2B),
        };

        public static LinearGradientBrush PaletteBrush(bool vertical = false)
        {
            var b = new LinearGradientBrush { StartPoint = vertical ? new Point(0, 1) : new Point(0, 0.5), EndPoint = vertical ? new Point(0, 0) : new Point(1, 0.5) };
            for (int i = 0; i < Palette.Length; i++) b.GradientStops.Add(new GradientStop(Palette[i], (double)i / (Palette.Length - 1)));
            b.Freeze();
            return b;
        }

        /// <summary>
        /// 3D 云图用的颜色条贴图。纹理坐标按绝对值映射（ViewportUnits = Absolute）：
        /// 默认的「相对包围盒」映射会把颜色条拉伸到实际出现的数值范围上，应力比较均匀时颜色会错。
        /// </summary>
        static ImageBrush PaletteBrush3D()
        {
            const int W = 256;
            var px = new byte[W * 4];
            for (int i = 0; i < W; i++)
            {
                double t = (double)i / (W - 1) * (Palette.Length - 1);
                int a = Math.Min(Palette.Length - 2, (int)t); double f = t - a;
                Color c0 = Palette[a], c1 = Palette[a + 1];
                px[4 * i] = (byte)(c0.B + (c1.B - c0.B) * f); px[4 * i + 1] = (byte)(c0.G + (c1.G - c0.G) * f); px[4 * i + 2] = (byte)(c0.R + (c1.R - c0.R) * f); px[4 * i + 3] = 255;
            }
            var bmp = BitmapSource.Create(W, 1, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
            var b = new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill };
            b.Freeze();
            return b;
        }

        /// <summary>显示云图。values：每个节点的值；max：颜色条上限；deformScale：变形放大倍数（0 = 不显示变形）。</summary>
        public void ShowResult(double[] values, double max, double[] u, double deformScale, int markNode)
        {
            _content.Children.Clear();
            _faceModel = null; _faceOfTri = null;
            var pos = new Point3DCollection(); var idx = new Int32Collection(); var tex = new PointCollection();
            for (int t = 0; t < _mesh.TriFace.Length; t++)
                AddTri6(_mesh, t, pos, idx, deformScale > 0 ? u : null, deformScale, tex, n => Math.Max(0.002, Math.Min(0.998, values[n] / max)));
            pos.Freeze(); idx.Freeze(); tex.Freeze();
            _displayTris = idx.Count / 3;
            var mat = new DiffuseMaterial(PaletteBrush3D()); mat.Freeze();
            var gm = new GeometryModel3D(new MeshGeometry3D { Positions = pos, TriangleIndices = idx, TextureCoordinates = tex }, mat) { BackMaterial = mat };
            _content.Children.Add(new ModelVisual3D { Content = gm });
            // 未变形的外形，半透明，便于对比
            if (deformScale > 0) AddGhost(0x30);
            // 最大值位置：小球标记
            if (markNode >= 0)
            {
                var c = _mesh.P(markNode);
                if (deformScale > 0) c = c + new Vec3(u[3 * markNode], u[3 * markNode + 1], u[3 * markNode + 2]) * deformScale;
                _mesh.Bounds(out var mn, out var mx);
                AddSphere(Disp(c), (mx - mn).Length * 0.012, Color.FromRgb(0x1D, 0x1D, 0x1F));
            }
            AddAxes();
        }

        void AddGhost(byte alpha)
        {
            var p0 = new Point3DCollection(); var i0 = new Int32Collection();
            for (int t = 0; t < _mesh.TriFace.Length; t++) AddTri6(_mesh, t, p0, i0, null, 0, null, null);
            p0.Freeze(); i0.Freeze();
            var ghost = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(alpha, 0x80, 0x84, 0x8C))); ghost.Freeze();
            _content.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(new MeshGeometry3D { Positions = p0, TriangleIndices = i0 }, ghost) { BackMaterial = ghost } });
        }

        /// <summary>
        /// 显示拓扑优化结果：tris 是零件坐标下的三角形（每 9 个数一个三角形，米），values 可选（每个顶点一个 0–1 的值，用于上色）。
        /// 原零件外形半透明显示在后面。
        /// </summary>
        public void ShowSurface(float[] tris, bool ghost)
        {
            _content.Children.Clear();
            _faceModel = null; _faceOfTri = null;
            // 相同位置的顶点合并成一个：WPF 会按共享顶点算平滑法向，曲面看起来是光滑的而不是一块块的
            var pos = new Point3DCollection(tris.Length / 9); var idx = new Int32Collection(tris.Length / 3);
            var map = new Dictionary<(float, float, float), int>(tris.Length / 9);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                var key = (tris[i], tris[i + 1], tris[i + 2]);
                if (!map.TryGetValue(key, out var k))
                {
                    k = pos.Count;
                    pos.Add(new Point3D(tris[i], -tris[i + 2], tris[i + 1]));
                    map[key] = k;
                }
                idx.Add(k);
            }
            pos.Freeze(); idx.Freeze();
            _displayTris = idx.Count / 3;
            var mat = Mat(CTopo);
            _content.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(new MeshGeometry3D { Positions = pos, TriangleIndices = idx }, mat) { BackMaterial = mat } });
            if (ghost && _mesh != null) AddGhost(0x22);
            if (_mesh != null) AddAxes();
        }

        void AddSphere(Point3D c, double r, Color color)
        {
            var pos = new Point3DCollection(); var idx = new Int32Collection();
            int nu = 16, nv = 10;
            for (int i = 0; i <= nv; i++)
                for (int j = 0; j <= nu; j++)
                {
                    double th = Math.PI * i / nv, ph = 2 * Math.PI * j / nu;
                    pos.Add(new Point3D(c.X + r * Math.Sin(th) * Math.Cos(ph), c.Y + r * Math.Sin(th) * Math.Sin(ph), c.Z + r * Math.Cos(th)));
                }
            for (int i = 0; i < nv; i++)
                for (int j = 0; j < nu; j++)
                {
                    int a = i * (nu + 1) + j, b = a + nu + 1;
                    idx.Add(a); idx.Add(b); idx.Add(a + 1); idx.Add(a + 1); idx.Add(b); idx.Add(b + 1);
                }
            var m = Mat(color);
            _content.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(new MeshGeometry3D { Positions = pos, TriangleIndices = idx }, m) { BackMaterial = m } });
        }

        /// <summary>回到按面显示（编辑约束和载荷时）。</summary>
        public void ShowFaces(Func<int, Color> colorOf)
        {
            if (_mesh == null) return;
            BuildFaceModel();
            SetFaceColors(colorOf);
        }
    }
}
