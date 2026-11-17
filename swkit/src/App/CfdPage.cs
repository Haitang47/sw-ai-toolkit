using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SolidWorks.Interop.sldworks;
using SwKit.Core;
using SwKit.Core.Cfd;
using SwKit.Core.Dfm;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>
    /// 「CFD 流场」页面：读整机模型 → 放转子 / 涵道（作动盘）→ 内置格子玻尔兹曼求解 → 流线、切面、受力。
    /// </summary>
    sealed class CfdPage
    {
        public FrameworkElement Root { get; }
        public bool IsBusy { get; private set; }

        readonly Window _owner;
        readonly SwWorker _worker;
        readonly CfdView _view = new CfdView();
        readonly FrameworkElement _partBar, _empty, _busy, _work, _seedBar, _legend;
        readonly TextBlock _modelName, _modelPath, _status, _busyText, _busyDetail, _runText, _runIcon, _legendMin, _legendMid, _legendMax;
        readonly StackPanel _side;
        readonly Button _runBtn, _vtkBtn, _shotBtn;

        string _source, _loadError;
        float[] _tris; int[] _triPart; string[] _partNames;
        Vec3 _up = new Vec3(0, 1, 0);
        Vec3 _bmin, _bmax;                                   // 模型包围盒（m）
        readonly List<CfdDisk> _disks = new List<CfdDisk>();
        int _selDisk = -1;
        string _detail = "标准";
        string _device;
        double _wind; string _windDir = "+X";
        bool _ground; double _groundMm = 300;
        double _weightG = 600;
        CfdResult _result;
        List<CfdStreamline> _lines;
        double _vmax;
        CfdProgress _live;
        BitmapSource _livePreview;
        CancellationTokenSource _cts;
        bool _picking, _refitPending;

        static readonly Brush Gray = Ui.Brush("#6E6E73"), Green = Ui.Brush("#248A3D"), Orange = Ui.Brush("#B25000"), Red = Ui.Brush("#D70015"), Text3 = Ui.Brush("#A1A1A6"), Accent = Ui.Brush("#0071E3");
        static readonly string[] Axes = { "+X", "−X", "+Y", "−Y", "+Z", "−Z" };

        public CfdPage(Window owner, SwWorker worker)
        {
            _owner = owner; _worker = worker;
            Root = (FrameworkElement)Ui.Load("CfdPage.xaml");
            _partBar = Root.Find<FrameworkElement>("PartBar");
            _empty = Root.Find<FrameworkElement>("EmptyState");
            _busy = Root.Find<FrameworkElement>("BusyState");
            _work = Root.Find<FrameworkElement>("WorkState");
            _seedBar = Root.Find<FrameworkElement>("SeedBar");
            _legend = Root.Find<FrameworkElement>("Legend");
            _modelName = Root.Find<TextBlock>("ModelName");
            _modelPath = Root.Find<TextBlock>("ModelPath");
            _status = Root.Find<TextBlock>("StatusText");
            _busyText = Root.Find<TextBlock>("BusyText");
            _busyDetail = Root.Find<TextBlock>("BusyDetail");
            _runText = Root.Find<TextBlock>("RunText");
            _runIcon = Root.Find<TextBlock>("RunIcon");
            _legendMin = Root.Find<TextBlock>("LegendMin"); _legendMid = Root.Find<TextBlock>("LegendMid"); _legendMax = Root.Find<TextBlock>("LegendMax");
            Root.Find<System.Windows.Shapes.Rectangle>("LegendBar").Fill = CfdView.TurboGradient();
            _side = Root.Find<StackPanel>("SidePanel");
            _runBtn = Root.Find<Button>("RunBtn");
            _vtkBtn = Root.Find<Button>("VtkBtn");
            _shotBtn = Root.Find<Button>("ShotBtn");
            Root.Find<Grid>("ViewHost").Children.Add(_view.Element);
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            ((RotateTransform)Root.Find<FrameworkElement>("Spinner").RenderTransform).BeginAnimation(RotateTransform.AngleProperty, spin);
            BuildHowSteps();

            Root.Find<Button>("PickBtn").Click += (s, e) => Pick();
            Root.Find<Button>("ChangeBtn").Click += (s, e) => Pick();
            Root.Find<Button>("SampleBtn").Click += async (s, e) => await LoadSampleAsync();
            Root.Find<Button>("SampleLink").Click += async (s, e) => await LoadSampleAsync();
            Root.Find<Button>("ResetViewBtn").Click += (s, e) => _view.ResetView();
            Root.Find<RadioButton>("UpY").Checked += (s, e) => SetUp(new Vec3(0, 1, 0));
            Root.Find<RadioButton>("UpZ").Checked += (s, e) => SetUp(new Vec3(0, 0, 1));
            foreach (var n in new[] { "SeedInlet", "SeedTip", "SeedAbove", "SliceToggle" })
            {
                var tb = Root.Find<ToggleButton>(n);
                tb.Checked += async (s, e) => await RefreshResultView();
                tb.Unchecked += async (s, e) => await RefreshResultView();
            }
            _runBtn.Click += async (s, e) => { if (IsBusy && _cts != null) _cts.Cancel(); else await RunAsync(); };
            _vtkBtn.Click += (s, e) => ExportVtk();
            _shotBtn.Click += (s, e) => SaveShot();
            _view.Picked += OnPicked;
            Root.DragOver += (s, e) => { e.Effects = !IsBusy && FileFrom(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Root.Drop += async (s, e) => { var f = FileFrom(e.Data); if (f != null && !IsBusy) await LoadAsync(f); };
            try { _device = CfdSolver.Devices().FirstOrDefault(); } catch { _device = null; }
        }

        static string FileFrom(IDataObject d) =>
            (d.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(f => new[] { ".sldasm", ".sldprt", ".step", ".stp", ".stl" }.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()));

        void BuildHowSteps()
        {
            var host = Root.Find<UniformGrid>("HowSteps");
            var steps = new[]
            {
                ("1", "拖入整机", "装配体、零件、STEP 或 STL。桨叶不用建模。"),
                ("2", "放桨盘", "每个转子 / 涵道一个作动盘：位置、方向、直径、推力。可以在模型上点选位置，或者用四旋翼模板。"),
                ("3", "计算、看流场", "流线按速度着色；给出机身受到的下洗力、各零件受力；可以导出 VTK 给 ParaView。"),
            };
            for (int i = 0; i < steps.Length; i++)
            {
                var (n, t, d) = steps[i];
                var sp = new StackPanel();
                var head = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                head.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Accent, Child = new TextBlock { Text = n, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
                head.Children.Add(new TextBlock { Text = t, FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(head);
                sp.Children.Add(new TextBlock { Text = d, FontSize = 12, Foreground = Gray, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(30, 4, 0, 0) });
                host.Children.Add(new Border { Background = Brushes.White, BorderBrush = Ui.Brush("#E5E5EA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 12), Child = sp, Margin = new Thickness(i == 0 ? 0 : 8, 0, i == 2 ? 0 : 8, 0) });
            }
        }

        // ================= 读入模型 =================

        void Pick()
        {
            if (IsBusy) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择整机模型", Filter = "模型|*.sldasm;*.sldprt;*.step;*.stp;*.stl|SolidWorks|*.sldasm;*.sldprt|STEP|*.step;*.stp|STL|*.stl" };
            if (dlg.ShowDialog(_owner) != true) return;
            _ = LoadAsync(dlg.FileName);
        }

        internal static string SamplePath()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[] { System.IO.Path.Combine(dir, "示例", "四旋翼示例.step"), System.IO.Path.Combine(dir, "..", "tools", "samples", "四旋翼示例.step") })
                if (File.Exists(c)) return System.IO.Path.GetFullPath(c);
            return null;
        }

        async Task LoadSampleAsync()
        {
            var p = SamplePath();
            if (p == null) { _status.Text = "找不到示例文件（应该在程序旁边的「示例」文件夹里）"; _status.Foreground = Red; return; }
            await LoadAsync(p);
            if (_tris == null) return;
            // 示例：5 寸四旋翼，桨盘在电机上方，每个 4 N，相邻反转
            if (_disks.Count == 0)
            {
                Root.Find<RadioButton>("UpY").IsChecked = true;
                Quad(226, 5 * 25.4, 26, 4, true);
            }
        }

        void Phase(string text) => Root.Dispatcher.BeginInvoke(new Action(() => { _busyDetail.Text = text; _status.Text = text; _status.Foreground = Gray; }));

        public async Task LoadAsync(string path)
        {
            if (IsBusy) return;
            path = System.IO.Path.GetFullPath(path);
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            _source = path;
            _modelName.Text = System.IO.Path.GetFileName(path);
            _modelPath.Text = System.IO.Path.GetDirectoryName(path);
            Ui.Fade(_partBar, true);
            Ui.Fade(_empty, false); Ui.Fade(_work, false); Ui.Fade(_busy, true);
            IsBusy = true; _busyText.Text = "正在读取模型";
            try
            {
                string input = path;
                if (ext == ".sldasm" || ext == ".sldprt")
                {
                    input = await _worker.Run(() =>
                    {
                        var s = _worker.Acquire(m => Phase(m.Contains("启动") ? "正在启动后台 SolidWorks（第一次约 20–60 秒）" : m), CancellationToken.None);
                        try { Phase("正在把模型转成 STEP（只读打开）"); return ToStep(s.App, path, ext == ".sldasm"); }
                        finally { _worker.Release(); }
                    });
                }
                DfmMesh mesh;
                using (HeavyWork.Begin()) mesh = await Task.Run(() => DfmMesh.Load(input, Phase));
                ConvertMesh(mesh);
                _result = null; _lines = null;
                _disks.Clear(); _selDisk = -1;
                RestoreSetup();
                IsBusy = false; _loadError = null;
                Ui.Fade(_busy, false); Ui.Fade(_work, true);
                _view.ShowModel(_tris, _up, false);
                _view.ClearResults();
                _view.ShowDisks(_disks, _selDisk);
                Ui.SetShown(_seedBar, false); Ui.SetShown(_legend, false);
                BuildSide();
                UpdateButtons();
                _status.Text = $"模型：{_partNames.Length} 个实体，{_tris.Length / 9:N0} 个三角形；" + (_disks.Count == 0 ? "在右侧放桨盘（转子 / 涵道）" : $"已恢复上次的 {_disks.Count} 个桨盘");
                _status.Foreground = Gray;
            }
            catch (Exception ex)
            {
                IsBusy = false;
                Ui.Fade(_busy, false); Ui.Fade(_tris == null ? _empty : _work, true);
                _status.Text = "读取失败：" + ex.Message; _status.Foreground = Red;
                _loadError = _status.Text;
            }
        }

        /// <summary>在后台 SolidWorks 里只读打开，导出 STEP（装配体会带上所有零件）。</summary>
        static string ToStep(ISldWorks app, string path, bool asm)
        {
            int e = 0, w = 0;
            var already = app.GetOpenDocumentByName(path) as ModelDoc2;
            var doc = already ?? app.OpenDoc6(path, asm ? 2 : 1, 1 | 2, "", ref e, ref w);
            if (doc == null) throw new InvalidOperationException("打开失败：" + ExportRunner.LoadErrorText(e));
            try
            {
                if (asm) try { ((AssemblyDoc)doc).ResolveAllLightWeightComponents(false); } catch { }
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swkit_cfd_models");
                Directory.CreateDirectory(dir);
                var step = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ".step");
                app.ActivateDoc3(doc.GetTitle(), false, 1, ref e);
                doc.Extension.SaveAs(step, 0, 1, null, ref e, ref w);
                if (!File.Exists(step)) throw new InvalidOperationException("导出 STEP 失败：" + ExportRunner.SaveErrorText(e));
                return step;
            }
            finally { if (already == null) try { app.CloseDoc(doc.GetTitle()); } catch { } }
        }

        void ConvertMesh(DfmMesh m)
        {
            int n = m.Tris;
            _tris = new float[9 * n];
            _triPart = new int[n];
            for (int t = 0; t < n; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = m.T[3 * t + k];
                    _tris[9 * t + 3 * k] = (float)(m.V[3 * v] / 1000); _tris[9 * t + 3 * k + 1] = (float)(m.V[3 * v + 1] / 1000); _tris[9 * t + 3 * k + 2] = (float)(m.V[3 * v + 2] / 1000);
                }
                _triPart[t] = m.ShellOf != null && t < m.ShellOf.Length ? Math.Max(0, m.ShellOf[t]) : 0;
            }
            int parts = _triPart.Length == 0 ? 0 : _triPart.Max() + 1;
            var mins = Enumerable.Repeat(new Vec3(double.MaxValue, double.MaxValue, double.MaxValue), parts).ToArray();
            var maxs = Enumerable.Repeat(new Vec3(double.MinValue, double.MinValue, double.MinValue), parts).ToArray();
            _bmin = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue); _bmax = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            for (int t = 0; t < n; t++)
                for (int k = 0; k < 3; k++)
                {
                    var p = new Vec3(_tris[9 * t + 3 * k], _tris[9 * t + 3 * k + 1], _tris[9 * t + 3 * k + 2]);
                    int s = _triPart[t];
                    mins[s] = Min(mins[s], p); maxs[s] = Max(maxs[s], p);
                    _bmin = Min(_bmin, p); _bmax = Max(_bmax, p);
                }
            _partNames = Enumerable.Range(0, parts).Select(i =>
            {
                var c = (mins[i] + maxs[i]) * 500;
                return parts == 1 ? System.IO.Path.GetFileNameWithoutExtension(_source) : $"实体 {i + 1}（{c.X:0}, {c.Y:0}, {c.Z:0} mm 附近）";
            }).ToArray();
        }

        static Vec3 Min(Vec3 a, Vec3 b) => new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        static Vec3 Max(Vec3 a, Vec3 b) => new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

        void SetUp(Vec3 up)
        {
            if ((up - _up).Length < 1e-9) return;
            _up = up;
            if (_tris == null) return;
            foreach (var d in _disks) if (Math.Abs(Math.Abs(d.Axis.Dot(up)) - 1) > 1e-6 && !d.Ducted) d.Axis = up;
            _view.ShowModel(_tris, _up, false);
            _view.ShowDisks(_disks, _selDisk);
            Invalidate();
        }

        // ================= 桨盘 =================

        static Vec3 AxisOf(string s)
        {
            switch (s) { case "+X": return new Vec3(1, 0, 0); case "−X": return new Vec3(-1, 0, 0); case "+Y": return new Vec3(0, 1, 0); case "−Y": return new Vec3(0, -1, 0); case "+Z": return new Vec3(0, 0, 1); default: return new Vec3(0, 0, -1); }
        }
        static string AxisName(Vec3 a)
        {
            var n = a.Normalized();
            foreach (var s in Axes) if ((AxisOf(s) - n).Length < 1e-6) return s;
            return $"({n.X:0.##}, {n.Y:0.##}, {n.Z:0.##})";
        }

        /// <summary>水平面的两个方向（与 Up 垂直的坐标轴）。</summary>
        void Horizontal(out Vec3 h1, out Vec3 h2)
        {
            if (Math.Abs(_up.Y) > 0.5) { h1 = new Vec3(1, 0, 0); h2 = new Vec3(0, 0, 1); }
            else if (Math.Abs(_up.Z) > 0.5) { h1 = new Vec3(1, 0, 0); h2 = new Vec3(0, 1, 0); }
            else { h1 = new Vec3(0, 1, 0); h2 = new Vec3(0, 0, 1); }
        }

        /// <summary>四旋翼模板：轴距（对角电机距离，mm）、桨径（mm）、桨盘高度（沿 Up 的坐标，mm）、单桨推力（N）；X 布局时桨在两条对角线上。</summary>
        void Quad(double wheelbaseMm, double propMm, double heightMm, double thrust, bool xLayout)
        {
            Horizontal(out var h1, out var h2);
            var c = (_bmin + _bmax) / 2;
            var center = c - _up * c.Dot(_up) + _up * (heightMm / 1000);
            double r = wheelbaseMm / 2000;
            _disks.RemoveAll(d => !d.Ducted);
            for (int i = 0; i < 4; i++)
            {
                double a = (xLayout ? Math.PI / 4 : 0) + i * Math.PI / 2;
                var p = center + (h1 * Math.Cos(a) + h2 * Math.Sin(a)) * r;
                _disks.Add(new CfdDisk
                {
                    Name = $"桨 {i + 1}", Center = p, Axis = _up, Radius = propMm / 2000, HubRadius = Math.Max(0.006, propMm / 2000 * 0.15), Thrust = thrust,
                    Torque = (i % 2 == 0 ? 1 : -1) * EstimateTorque(thrust, propMm / 1000), Loading = CfdLoading.Prop,
                });
            }
            _selDisk = -1;
            DisksChanged();
        }

        /// <summary>反扭矩粗估：Q ≈ T · D · 0.06（常见多旋翼桨 Cq/Ct·D 的量级），只用来产生旋流。</summary>
        static double EstimateTorque(double thrust, double diameter) => thrust * diameter * 0.06;

        void DisksChanged()
        {
            _view.ShowDisks(_disks, _selDisk);
            SaveSetup();
            Invalidate();
        }

        /// <summary>设置改了：旧结果作废。</summary>
        void Invalidate()
        {
            if (_result != null)
            {
                _result = null; _lines = null;
                _view.ClearResults();
                Ui.SetShown(_seedBar, false); Ui.SetShown(_legend, false);
                _status.Text = "设置改了，之前的结果已清除，重新计算"; _status.Foreground = Gray;
            }
            BuildSide();
            UpdateButtons();
        }

        void OnPicked(Vec3 p)
        {
            if (!_picking || _selDisk < 0 || _selDisk >= _disks.Count) return;
            _picking = false; _view.PickMode = false;
            var d = _disks[_selDisk];
            // 点在电机顶上：盘心放在点上方一点（3 mm）
            d.Center = p + d.Axis.Normalized() * 0.003;
            _status.Text = $"{d.Name} 放在 ({d.Center.X * 1000:0.#}, {d.Center.Y * 1000:0.#}, {d.Center.Z * 1000:0.#}) mm，可以在右侧微调";
            _status.Foreground = Gray;
            DisksChanged();
        }

        // ================= 右侧面板 =================

        void Section(string t) => _side.Children.Add(new TextBlock { Text = t, Style = (Style)Application.Current.FindResource("T.Section") });
        Border Group(UIElement child) => new Border { Style = (Style)Application.Current.FindResource("Group"), Child = child };
        static TextBlock Caption(string text, Brush color, Thickness margin) => new TextBlock { Text = text, FontSize = 12, Foreground = color, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = margin };
        static string N(double v, string f = "0.##") => v.ToString(f, CultureInfo.InvariantCulture);

        TextBox Num(double v, string fmt, Action<double> set, double width = 64)
        {
            var tb = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Width = width, Text = N(v, fmt), TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
            void Commit()
            {
                if (double.TryParse(tb.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) { if (Math.Abs(x - v) > 1e-12) { v = x; set(x); } }
                else tb.Text = N(v, fmt);
            }
            tb.LostFocus += (s, e) => Commit();
            tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) Commit(); };
            return tb;
        }

        StackPanel Row(string label, params UIElement[] items)
        {
            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(new TextBlock { Text = label, Width = 56, FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
            foreach (var i in items) sp.Children.Add(i);
            return sp;
        }

        static TextBlock Unit(string u) => new TextBlock { Text = u, FontSize = 12, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };

        WrapPanel Pills(IEnumerable<string> items, string cur, Action<string> set)
        {
            var w = new WrapPanel();
            var g = "p" + Guid.NewGuid().ToString("N");
            foreach (var it in items)
            {
                var rb = new RadioButton { Style = (Style)Application.Current.FindResource("Pill"), GroupName = g, Content = it, IsChecked = it == cur, FontSize = 11.5 };
                var x = it;
                rb.Checked += (s, e) => { if (x != cur) Root.Dispatcher.BeginInvoke(new Action(() => set(x))); };
                w.Children.Add(rb);
            }
            return w;
        }

        void BuildSide()
        {
            _side.Children.Clear();
            if (_tris == null) return;
            if (IsBusy && _live != null) AddProgressCard();
            else if (_result != null) AddResultCards();
            AddDiskCards();
            AddSettingsCard();
        }

        void AddDiskCards()
        {
            Section($"转子 / 涵道（{_disks.Count}）");
            if (_disks.Count == 0)
                _side.Children.Add(Group(Caption("还没有桨盘。用下面的「四旋翼模板」一次放 4 个，或者「+ 加一个」后在模型上点选位置。涵道风扇也是一个桨盘，方向设成它的推力方向。", Gray, new Thickness(0))));
            for (int i = 0; i < _disks.Count; i++) _side.Children.Add(DiskCard(i));

            var actions = new WrapPanel { Margin = new Thickness(2, 0, 0, 6) };
            var add = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "+ 加一个", Margin = new Thickness(0, 0, 14, 4) };
            add.Click += (s, e) =>
            {
                var c = (_bmin + _bmax) / 2;
                var d = new CfdDisk { Name = $"桨 {_disks.Count + 1}", Center = c + _up * ((_bmax - _bmin).Dot(_up) / 2 + 0.01), Axis = _up };
                _disks.Add(d); _selDisk = _disks.Count - 1;
                DisksChanged();
            };
            actions.Children.Add(add);
            var addDuct = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "+ 加涵道", Margin = new Thickness(0, 0, 14, 4), ToolTip = "涵道风扇：推力方向一般是水平的，盘面放在涵道里面" };
            addDuct.Click += (s, e) =>
            {
                Horizontal(out var h1, out _);
                var c = (_bmin + _bmax) / 2;
                _disks.Add(new CfdDisk { Name = $"涵道 {_disks.Count(x => x.Ducted) + 1}", Center = c, Axis = h1, Radius = 0.03, HubRadius = 0.008, Thrust = 1, Ducted = true });
                _selDisk = _disks.Count - 1;
                DisksChanged();
            };
            actions.Children.Add(addDuct);
            _side.Children.Add(actions);

            // 模板和按重量分配推力
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = "四旋翼模板", FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) });
            double wb = 226, prop = 127, h = (_bmax.Dot(_up) * 1000) + 10, th = 4; bool x = true;
            t.Children.Add(Row("轴距", Num(wb, "0", v => wb = v), Unit("mm"), Num(prop, "0", v => prop = v, 52), Unit("mm 桨径")));
            t.Children.Add(Row("盘面高", Num(h, "0.#", v => h = v), Unit("mm"), Num(th, "0.##", v => th = v, 52), Unit("N / 桨")));
            var layout = Pills(new[] { "X 布局", "+ 布局" }, "X 布局", v => x = v == "X 布局");
            layout.Margin = new Thickness(56, 2, 0, 4);
            t.Children.Add(layout);
            var gen = new Button { Style = (Style)Application.Current.FindResource("Btn.Secondary"), Content = "放 4 个桨盘（替换现有转子）", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            gen.Click += (s, e) => Quad(wb, prop, h, th, x);
            t.Children.Add(gen);
            t.Children.Add(Caption("轴距是对角两个电机的距离；盘面高是桨盘在「向上」方向的坐标（模型坐标）。以模型包围盒中心为中心，相邻两个桨反转。", Text3, new Thickness(0, 4, 0, 0)));
            t.Children.Add(new Border { Height = 1, Background = Ui.Brush("#EDEDF0"), Margin = new Thickness(0, 10, 0, 8) });
            t.Children.Add(new TextBlock { Text = "按起飞重量分配推力（悬停）", FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) });
            var wrow = Row("起飞重", Num(_weightG, "0", v => _weightG = v), Unit("g"));
            var split = new Button { Style = (Style)Application.Current.FindResource("Btn.Secondary"), Content = "平分给转子", VerticalAlignment = VerticalAlignment.Center };
            split.Click += (s, e) =>
            {
                var rotors = _disks.Where(d => !d.Ducted).ToList();
                if (rotors.Count == 0) { _status.Text = "还没有转子（非涵道的桨盘）"; _status.Foreground = Orange; return; }
                // 涵道推力的竖直分量先扣掉
                double ductUp = _disks.Where(d => d.Ducted).Sum(d => d.Thrust * Math.Max(0, d.Axis.Normalized().Dot(_up)));
                double each = Math.Max(0.1, (_weightG / 1000 * 9.80665 - ductUp) / rotors.Count);
                foreach (var d in rotors) { d.Thrust = each; d.Torque = Math.Sign(d.Torque == 0 ? 1 : d.Torque) * EstimateTorque(each, d.Radius * 2); }
                DisksChanged();
                _status.Text = $"每个转子 {N(each)} N（总重 {N(_weightG, "0")} g × g ÷ {rotors.Count}" + (ductUp > 0 ? $"，已扣掉涵道竖直推力 {N(ductUp)} N" : "") + "）";
                _status.Foreground = Gray;
            };
            wrow.Children.Add(split);
            t.Children.Add(wrow);
            _side.Children.Add(Group(t));
        }

        Border DiskCard(int i)
        {
            var d = _disks[i];
            bool sel = i == _selDisk;
            var sp = new StackPanel();
            var head = new DockPanel();
            var del = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "删除", FontSize = 12 };
            del.Click += (s, e) => { _disks.RemoveAt(i); _selDisk = -1; DisksChanged(); };
            DockPanel.SetDock(del, Dock.Right); head.Children.Add(del);
            var name = new TextBlock { Text = (d.Ducted ? "涵道 · " : "转子 · ") + d.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(name);
            sp.Children.Add(head);
            sp.Children.Add(Caption($"Φ{N(d.Radius * 2000, "0")} mm · 推力 {N(d.Thrust)} N · 朝 {AxisName(d.Axis)} · 中心 ({N(d.Center.X * 1000, "0.#")}, {N(d.Center.Y * 1000, "0.#")}, {N(d.Center.Z * 1000, "0.#")}) mm", Gray, new Thickness(0, 2, 0, 0)));
            if (sel)
            {
                var c = d.Center * 1000;
                sp.Children.Add(Row("中心", Num(c.X, "0.#", v => { d.Center = new Vec3(v / 1000, d.Center.Y, d.Center.Z); DisksChanged(); }, 58), Num(c.Y, "0.#", v => { d.Center = new Vec3(d.Center.X, v / 1000, d.Center.Z); DisksChanged(); }, 58), Num(c.Z, "0.#", v => { d.Center = new Vec3(d.Center.X, d.Center.Y, v / 1000); DisksChanged(); }, 58), Unit("mm")));
                var pickBtn = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = _picking ? "在模型上点一下……（Esc 取消）" : "在模型上点选位置", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(56, 0, 0, 2) };
                pickBtn.Click += (s, e) => { _picking = !_picking; _view.PickMode = _picking; _status.Text = _picking ? "点模型上电机的顶面（或涵道里桨所在的位置），桨盘中心放在那里" : ""; _status.Foreground = Gray; BuildSide(); };
                sp.Children.Add(pickBtn);
                var ax = Pills(Axes, AxisName(d.Axis), v => { d.Axis = AxisOf(v); DisksChanged(); });
                sp.Children.Add(Row("推力朝", ax));
                sp.Children.Add(Row("直径", Num(d.Radius * 2000, "0.#", v => { d.Radius = Math.Max(0.002, v / 2000); d.HubRadius = Math.Min(d.HubRadius, d.Radius * 0.6); DisksChanged(); }), Unit("mm"), Num(d.HubRadius * 2000, "0.#", v => { d.HubRadius = Math.Max(0, Math.Min(d.Radius * 0.8, v / 2000)); DisksChanged(); }, 52), Unit("mm 桨毂")));
                sp.Children.Add(Row("推力", Num(d.Thrust, "0.###", v => { d.Thrust = Math.Max(0, v); DisksChanged(); }), Unit("N"), Num(Math.Abs(d.Torque), "0.####", v => { d.Torque = Math.Sign(d.Torque == 0 ? 1 : d.Torque) * Math.Abs(v); DisksChanged(); }, 60), Unit("N·m 扭矩")));
                var spin = Pills(new[] { "右手方向转", "反方向转", "不加旋流" }, d.Torque > 0 ? "右手方向转" : d.Torque < 0 ? "反方向转" : "不加旋流", v =>
                {
                    double mag = Math.Abs(d.Torque) > 0 ? Math.Abs(d.Torque) : EstimateTorque(d.Thrust, d.Radius * 2);
                    d.Torque = v == "右手方向转" ? mag : v == "反方向转" ? -mag : 0; DisksChanged();
                });
                sp.Children.Add(Row("旋向", spin));
                var load = Pills(new[] { "均匀", "接近真实桨" }, d.Loading == CfdLoading.Prop ? "接近真实桨" : "均匀", v => { d.Loading = v == "均匀" ? CfdLoading.Uniform : CfdLoading.Prop; DisksChanged(); });
                sp.Children.Add(Row("分布", load));
                var duct = new CheckBox { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = d.Ducted, Content = new TextBlock { Text = "涵道风扇（盘面在涵道里）", FontSize = 12.5, Foreground = Gray } };
                duct.Checked += (s, e) => { d.Ducted = true; DisksChanged(); }; duct.Unchecked += (s, e) => { d.Ducted = false; DisksChanged(); };
                sp.Children.Add(duct);
                sp.Children.Add(Caption("推力朝：推力作用在飞行器上的方向（空气被推向反方向）。旋向按推力方向的右手定则；反扭矩只用来产生旋流，不影响推力。", Text3, new Thickness(0, 4, 0, 0)));
            }
            var b = Group(sp);
            b.Cursor = Cursors.Hand;
            if (sel) { b.BorderBrush = Accent; b.BorderThickness = new Thickness(1.5); }
            b.MouseLeftButtonUp += (s, e) =>
            {
                if (e.OriginalSource is FrameworkElement fe && (fe is TextBox || FindParent<TextBox>(fe) != null || FindParent<ButtonBase>(fe) != null)) return;
                if (_selDisk == i) return;
                _selDisk = i; _picking = false; _view.PickMode = false;
                _view.ShowDisks(_disks, _selDisk); BuildSide();
            };
            return b;
        }

        static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            for (var p = d; p != null; p = VisualTreeHelper.GetParent(p)) if (p is T t) return t;
            return null;
        }

        void AddSettingsCard()
        {
            Section("计算设置");
            var sp = new StackPanel();
            IList<string> devs; try { devs = CfdSolver.Devices(); } catch { devs = new[] { "CPU" }; }
            var combo = new ComboBox { FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var d in devs) combo.Items.Add(new ComboBoxItem { Content = d, IsSelected = d == _device });
            if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
            combo.SelectionChanged += (s, e) => { _device = (combo.SelectedItem as ComboBoxItem)?.Content as string; BuildSide(); };
            sp.Children.Add(new TextBlock { Text = "计算设备", FontSize = 12.5, Foreground = Gray, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(combo);
            sp.Children.Add(new TextBlock { Text = "网格", FontSize = 12.5, Foreground = Gray, Margin = new Thickness(0, 4, 0, 4) });
            sp.Children.Add(Pills(new[] { "粗", "标准", "细" }, _detail, v => { _detail = v; SaveSetup(); Invalidate(); }));
            try
            {
                var setup = MakeSetup();
                long cells = CfdSolver.EstimateCells(setup, out var dx, out _, out int nx, out int ny, out int nz);
                sp.Children.Add(Caption($"{nx}×{ny}×{nz} = {cells / 1e6:0.#} M 格，格子 {dx * 1000:0.#} mm；" + (IsGpu ? $"显存约 {cells * 190 / 1e9:0.#} GB" : $"内存约 {cells * 190 / 1e9:0.#} GB"), Text3, new Thickness(0, 4, 0, 0)));
                var thin = _disks.Count > 0 ? _disks.Min(d => d.Radius) * 2 / dx : 99;
                if (thin < 10) sp.Children.Add(Caption($"最小的桨盘直径只有 {thin:0} 个格子，桨盘附近的流动会比较粗糙；可以选「细」。", Orange, new Thickness(0, 2, 0, 0)));
            }
            catch { }
            sp.Children.Add(new TextBlock { Text = "来流（风速）", FontSize = 12.5, Foreground = Gray, Margin = new Thickness(0, 10, 0, 4) });
            Horizontal(out var h1, out var h2);
            var dirs = new[] { "+" + AxisName(h1).TrimStart('+'), "−" + AxisName(h1).TrimStart('+'), "+" + AxisName(h2).TrimStart('+'), "−" + AxisName(h2).TrimStart('+') };
            var wr = Row("风速", Num(_wind, "0.#", v => { _wind = Math.Max(0, v); SaveSetup(); Invalidate(); }), Unit("m/s"));
            sp.Children.Add(wr);
            if (_wind > 0) { var p = Pills(dirs, dirs.Contains(_windDir) ? _windDir : dirs[0], v => { _windDir = v; SaveSetup(); Invalidate(); }); sp.Children.Add(Row("吹向", p)); }
            sp.Children.Add(Caption("0 = 悬停（静止空气）。前飞时填飞行速度，方向选「空气相对飞机吹向哪里」（向 +X 飞就是风吹向 −X）。", Text3, new Thickness(0, 0, 0, 0)));
            var g = new CheckBox { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = _ground, Margin = new Thickness(0, 8, 0, 0), Content = new TextBlock { Text = "地面效应", FontSize = 12.5, Foreground = Gray } };
            g.Checked += (s, e) => { _ground = true; SaveSetup(); Invalidate(); }; g.Unchecked += (s, e) => { _ground = false; SaveSetup(); Invalidate(); };
            sp.Children.Add(g);
            if (_ground) sp.Children.Add(Row("离地", Num(_groundMm, "0", v => { _groundMm = Math.Max(5, v); SaveSetup(); Invalidate(); }), Unit("mm（模型最低点到地面）")));
            _side.Children.Add(Group(sp));
        }

        bool IsGpu => _device != null && !_device.StartsWith("CPU", StringComparison.OrdinalIgnoreCase);

        CfdSetup MakeSetup()
        {
            Horizontal(out var h1, out var h2);
            Vec3 wind = Vec3.Zero;
            if (_wind > 0)
            {
                var d = _windDir.Replace("−", "-");
                var ax = d.Contains("X") ? new Vec3(1, 0, 0) : d.Contains("Y") ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);
                wind = ax * (_wind * (d.StartsWith("-") ? -1 : 1));
            }
            long target = IsGpu ? (_detail == "粗" ? 1_500_000 : _detail == "细" ? 12_000_000 : 5_000_000) : (_detail == "粗" ? 500_000 : _detail == "细" ? 3_000_000 : 1_500_000);
            return new CfdSetup
            {
                Triangles = _tris, TriPart = _triPart, PartNames = _partNames,
                Disks = _disks.Select(d => d.Clone()).ToList(),
                Wind = wind, Up = _up, Ground = _ground, GroundDistance = _groundMm / 1000,
                TargetCells = target, Device = IsGpu ? _device : "cpu",
            };
        }

        // ================= 计算 =================

        void UpdateButtons()
        {
            _runBtn.IsEnabled = _tris != null && (_disks.Count > 0 || _wind > 0) && (!IsBusy || _cts != null);
            _runText.Text = IsBusy && _cts != null ? "停止" : "开始计算";
            _runIcon.Text = IsBusy && _cts != null ? "" : "";
            _vtkBtn.IsEnabled = _shotBtn.IsEnabled = _result != null && !IsBusy;
        }

        public async Task RunAsync()
        {
            if (IsBusy || _tris == null) return;
            if (_disks.Count == 0 && _wind <= 0) { _status.Text = "先放桨盘（或者设来流风速）"; _status.Foreground = Orange; return; }
            var setup = MakeSetup();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            IsBusy = true; _live = new CfdProgress { Phase = "准备" }; _livePreview = null;
            _result = null; _lines = null; _view.ClearResults();
            Ui.SetShown(_seedBar, false); Ui.SetShown(_legend, false);
            UpdateButtons(); BuildSide();
            var last = DateTime.MinValue;
            try
            {
                CfdResult r;
                using (HeavyWork.Begin())
                    r = await Task.Run(() => CfdSolver.Run(setup, msg => Phase(msg), p =>
                    {
                        Root.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _live = p;
                            if (p.Slice != null) _livePreview = SliceImage(p.Slice, p.SliceW, p.SliceH, Math.Max(1, p.MaxSpeed));
                            if ((DateTime.Now - last).TotalMilliseconds > 400) { last = DateTime.Now; BuildSide(); }
                            _status.Text = $"{p.Phase}：第 {p.Step:N0} / {p.Steps:N0} 步，物理时间 {p.Time * 1000:0.#} ms，最大速度 {p.MaxSpeed:0.#} m/s，{p.MLups:0} 百万格/秒";
                            _status.Foreground = Gray;
                        }));
                    }, ct), ct);
                _result = r;
                IsBusy = false; _cts = null; _live = null;
                _refitPending = true;
                await RefreshResultView();
                var dn = Download(r);
                _status.Text = $"计算完成（{r.Seconds:0} 秒，{r.Device}）：机身受到的下洗力 {N(dn)} N" + (r.Oscillating ? "；流场有明显脉动，结果是时间平均值" : "");
                _status.Foreground = Green;
            }
            catch (OperationCanceledException) { IsBusy = false; _cts = null; _live = null; _status.Text = "已停止"; _status.Foreground = Gray; }
            catch (Exception ex) { IsBusy = false; _cts = null; _live = null; _status.Text = "计算失败：" + ex.Message; _status.Foreground = Red; }
            finally { UpdateButtons(); BuildSide(); }
        }

        /// <summary>机身受到的、和推力相反的竖直力（N，向下为正 = 下洗载荷）。</summary>
        double Download(CfdResult r) => -r.BodyForce.Dot(_up);

        static BitmapSource SliceImage(float[] s, int w, int h, double vmax)
        {
            var px = new byte[w * h * 4];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    float v = s[i + w * j];
                    var c = float.IsNaN(v) || v < 0 ? Color.FromRgb(0x9A, 0x9C, 0xA2) : CfdView.Turbo(v / vmax);
                    int o = 4 * (i + w * (h - 1 - j));
                    px[o] = c.B; px[o + 1] = c.G; px[o + 2] = c.R; px[o + 3] = 255;
                }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }

        void AddProgressCard()
        {
            Section("正在计算");
            var sp = new StackPanel();
            var p = _live;
            double frac = p.Steps > 0 ? (double)p.Step / p.Steps : 0;
            var bar = new ProgressBar { Style = (Style)Application.Current.FindResource("ThinProgress"), Value = frac * 100, Maximum = 100, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(bar);
            sp.Children.Add(Caption($"{p.Phase}　{p.Step:N0} / {p.Steps:N0} 步　物理时间 {p.Time * 1000:0.#} ms", Gray, new Thickness(0)));
            if (p.MLups > 0)
            {
                double eta = frac > 0.01 ? p.Seconds / frac - p.Seconds : double.NaN;
                sp.Children.Add(Caption($"{p.MLups:0} 百万格/秒　已用 {p.Seconds:0} 秒" + (double.IsNaN(eta) ? "" : $"　还要约 {eta:0} 秒"), Gray, new Thickness(0, 2, 0, 0)));
            }
            sp.Children.Add(Caption($"最大速度 {p.MaxSpeed:0.#} m/s　机身受力 ({p.BodyForce.X:0.##}, {p.BodyForce.Y:0.##}, {p.BodyForce.Z:0.##}) N", Gray, new Thickness(0, 2, 0, 0)));
            if (_livePreview != null)
            {
                sp.Children.Add(new TextBlock { Text = "过桨盘中心的竖直切面（实时）", FontSize = 11.5, Foreground = Text3, Margin = new Thickness(0, 8, 0, 4) });
                sp.Children.Add(new Image { Source = _livePreview, Stretch = Stretch.Uniform, MaxHeight = 260, HorizontalAlignment = HorizontalAlignment.Left });
            }
            sp.Children.Add(Caption("前面一段时间是流场在建立（桨盘开始推气，尾流向下发展），后面一段做时间平均；流线和受力都用平均后的结果。", Text3, new Thickness(0, 6, 0, 0)));
            _side.Children.Add(Group(sp));
        }

        void AddResultCards()
        {
            var r = _result;
            Section("结果");
            var sp = new StackPanel();
            double thrustUp = r.Setup.Disks.Sum(d => d.Thrust * d.Axis.Normalized().Dot(_up));
            double dn = Download(r);
            var grid = new UniformGrid { Columns = 3 };
            grid.Children.Add(Metric("总推力（竖直）", N(thrustUp, "0.##"), "N", null));
            grid.Children.Add(Metric("机身下洗力", N(dn, "0.##"), thrustUp > 0 ? $"占推力 {dn / thrustUp:P1}" : "N", dn > 0.08 * thrustUp ? Orange : Accent));
            grid.Children.Add(Metric("净升力", N(thrustUp - dn, "0.##"), "N", null));
            sp.Children.Add(grid);
            var horiz = r.BodyForce - _up * r.BodyForce.Dot(_up);
            sp.Children.Add(Caption($"机身受到的合力 ({N(r.BodyForce.X)}, {N(r.BodyForce.Y)}, {N(r.BodyForce.Z)}) N；水平分量 {N(horiz.Length)} N。下洗力是桨的下洗气流打在机臂、机身上的力，会抵消一部分推力。", Gray, new Thickness(0, 8, 0, 0)));
            sp.Children.Add(Caption($"{r.Nx}×{r.Ny}×{r.Nz} 格（{r.Cells / 1e6:0.#} M，格子 {r.Dx * 1000:0.#} mm），{r.Steps:N0} 步，物理时间 {r.SimTime * 1000:0} ms，{r.Device}，用时 {r.Seconds:0} 秒" + (r.Oscillating ? "；流场脉动较大，看的是时间平均值" : ""), Text3, new Thickness(0, 4, 0, 0)));
            foreach (var n in r.Notes) sp.Children.Add(Caption("· " + n, Orange, new Thickness(0, 2, 0, 0)));
            _side.Children.Add(Group(sp));

            if (r.DiskStats.Count > 0)
            {
                Section("桨盘入流");
                var ds = new StackPanel();
                foreach (var st in r.DiskStats)
                    ds.Children.Add(Caption($"{st.Disk.Name}：平均入流 {N(st.InflowSpeed, "0.#")} m/s（孤立桨的动量理论 {N(st.IdealInflow, "0.#")} m/s）", Gray, new Thickness(0, 1, 0, 1)));
                ds.Children.Add(Caption("入流比动量理论大，说明受到别的桨或机身的干扰、需要更多功率才能产生同样的推力；小于理论值一般是盘面被遮挡或者有地面效应。", Text3, new Thickness(0, 4, 0, 0)));
                _side.Children.Add(Group(ds));
            }

            var parts = r.PartForces.Where(p => p.Force.Length > 1e-4).OrderByDescending(p => p.Force.Length).Take(8).ToList();
            if (parts.Count > 1)
            {
                Section("各实体受力（前 8）");
                var ps = new StackPanel();
                foreach (var p in parts)
                    ps.Children.Add(Caption($"{p.Part}：竖直 {N(p.Force.Dot(_up), "0.###")} N，合力 {N(p.Force.Length, "0.###")} N", Gray, new Thickness(0, 1, 0, 1)));
                ps.Children.Add(Caption("竖直力为正表示向上（例如涵道唇口的附加推力），为负表示被下洗气流往下压。", Text3, new Thickness(0, 4, 0, 0)));
                _side.Children.Add(Group(ps));
            }
        }

        FrameworkElement Metric(string title, string value, string unit, Brush color)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            sp.Children.Add(new TextBlock { Text = title, FontSize = 11.5, Foreground = Gray });
            sp.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = color ?? Brushes.Black, Margin = new Thickness(0, 2, 0, 0) });
            sp.Children.Add(new TextBlock { Text = unit, FontSize = 11, Foreground = Text3 });
            return sp;
        }

        /// <summary>按当前的种子开关重新画流线、切面、色标。</summary>
        async Task RefreshResultView()
        {
            if (_result == null) return;
            var r = _result;
            bool inlet = Root.Find<ToggleButton>("SeedInlet").IsChecked == true, tip = Root.Find<ToggleButton>("SeedTip").IsChecked == true, above = Root.Find<ToggleButton>("SeedAbove").IsChecked == true;
            bool slice = Root.Find<ToggleButton>("SliceToggle").IsChecked == true;
            var lines = await Task.Run(() => CfdStreamlines.Trace(r, CfdStreamlines.Seeds(r, inlet, tip, above)));
            if (r != _result) return;
            _lines = lines;
            // 色标上限：流线速度的 99 百分位，取整到 5 m/s
            var all = lines.SelectMany(l => l.Speeds).OrderBy(x => x).ToList();
            double p99 = all.Count > 0 ? all[(int)(0.99 * (all.Count - 1))] : 1;
            _vmax = Math.Max(5, Math.Ceiling(p99 / 5) * 5);
            double span = (_bmax - _bmin).Length;
            bool first = _refitPending;
            _refitPending = false;
            _view.ShowStreamlines(lines, _vmax, Math.Max(r.Dx * 0.12, span * 0.0012), first);
            if (slice) ShowSlice(r); else _view.ShowSlice(null, 0, 0, Vec3.Zero, Vec3.Zero, Vec3.Zero);
            _legendMin.Text = "0"; _legendMid.Text = N(_vmax / 2, "0.#"); _legendMax.Text = N(_vmax, "0.#");
            Ui.SetShown(_seedBar, true); Ui.SetShown(_legend, true);
            UpdateButtons();
            BuildSide();
        }

        /// <summary>竖直切面：过所有桨盘的平均中心，沿水平跨度较大的方向。</summary>
        void ShowSlice(CfdResult r)
        {
            Horizontal(out var h1, out var h2);
            var c = r.Setup.Disks.Count > 0 ? r.Setup.Disks.Aggregate(Vec3.Zero, (s, d) => s + d.Center) / r.Setup.Disks.Count : (_bmin + _bmax) / 2;
            var lo = r.Origin; var hi = r.Origin + new Vec3(r.Nx, r.Ny, r.Nz) * r.Dx;
            var ext = hi - lo;
            var along = Math.Abs(ext.Dot(h1)) >= Math.Abs(ext.Dot(h2)) ? h1 : h2;
            // 平面：along × up，过 c
            double a0 = lo.Dot(along), a1 = hi.Dot(along), u0 = lo.Dot(_up), u1 = hi.Dot(_up);
            if (a0 > a1) { var t = a0; a0 = a1; a1 = t; }
            if (u0 > u1) { var t = u0; u0 = u1; u1 = t; }
            var normal = along.Cross(_up);
            var basePt = normal * c.Dot(normal);
            int w = 360, h = (int)Math.Max(60, Math.Min(600, w * (u1 - u0) / (a1 - a0)));
            var px = new byte[w * h * 4];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    var p = basePt + along * (a0 + (a1 - a0) * (i + 0.5) / w) + _up * (u0 + (u1 - u0) * (j + 0.5) / h);
                    var col = r.IsSolid(p) ? Color.FromRgb(0x9A, 0x9C, 0xA2) : CfdView.Turbo(r.Sample(p).Length / _vmax);
                    int o = 4 * (i + w * (h - 1 - j));
                    px[o] = col.B; px[o + 1] = col.G; px[o + 2] = col.R; px[o + 3] = 255;
                }
            var o0 = basePt + along * a0 + _up * u0;
            _view.ShowSlice(px, w, h, o0, along * (a1 - a0), _up * (u1 - u0));
        }

        // ================= 导出 =================

        void ExportVtk()
        {
            if (_result == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = System.IO.Path.GetFileNameWithoutExtension(_source) + "_流场.vtk", Filter = "VTK（ParaView）|*.vtk", InitialDirectory = System.IO.Path.GetDirectoryName(_source) };
            if (dlg.ShowDialog(_owner) != true) return;
            try { CfdVtk.Write(_result, dlg.FileName); _status.Text = "已导出：" + dlg.FileName; _status.Foreground = Green; }
            catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; _status.Foreground = Red; }
        }

        void SaveShot()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = System.IO.Path.GetFileNameWithoutExtension(_source) + "_流线.png", Filter = "PNG|*.png", InitialDirectory = System.IO.Path.GetDirectoryName(_source) };
            if (dlg.ShowDialog(_owner) != true) return;
            try { _view.SaveImage(dlg.FileName, 2); _status.Text = "已保存：" + dlg.FileName; _status.Foreground = Green; }
            catch (Exception ex) { _status.Text = "保存失败：" + ex.Message; _status.Foreground = Red; }
        }

        // ================= 记住每个模型的桨盘和设置 =================

        static string StoreFile => System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SwKit", "cfd-setups.json");
        internal static string StoreOverride;

        Dictionary<string, object> LoadStore()
        {
            try
            {
                var f = StoreOverride ?? StoreFile;
                if (!File.Exists(f)) return new Dictionary<string, object>();
                return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
            catch { return new Dictionary<string, object>(); }
        }

        void SaveSetup()
        {
            if (_source == null || _source == SamplePath()) return;
            try
            {
                var all = LoadStore();
                all[_source.ToLowerInvariant()] = new Dictionary<string, object>
                {
                    ["up"] = new[] { _up.X, _up.Y, _up.Z }, ["detail"] = _detail, ["wind"] = _wind, ["windDir"] = _windDir, ["ground"] = _ground, ["groundMm"] = _groundMm, ["weightG"] = _weightG,
                    ["disks"] = _disks.Select(d => new Dictionary<string, object>
                    {
                        ["name"] = d.Name, ["c"] = new[] { d.Center.X, d.Center.Y, d.Center.Z }, ["a"] = new[] { d.Axis.X, d.Axis.Y, d.Axis.Z }, ["r"] = d.Radius, ["hub"] = d.HubRadius,
                        ["t"] = d.Thrust, ["q"] = d.Torque, ["duct"] = d.Ducted, ["prop"] = d.Loading == CfdLoading.Prop,
                    }).ToList(),
                };
                var f = StoreOverride ?? StoreFile;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
                File.WriteAllText(f, new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(all), Encoding.UTF8);
            }
            catch { }
        }

        void RestoreSetup()
        {
            if (!(LoadStore().TryGetValue(_source.ToLowerInvariant(), out var o) && o is Dictionary<string, object> s)) return;
            double D(object x) => Convert.ToDouble(x, CultureInfo.InvariantCulture);
            Vec3 V(object x) { var a = ((object[])x).Select(D).ToArray(); return new Vec3(a[0], a[1], a[2]); }
            try
            {
                if (s.TryGetValue("up", out var up)) { _up = V(up); Root.Find<RadioButton>(Math.Abs(_up.Z) > 0.5 ? "UpZ" : "UpY").IsChecked = true; _up = V(up); }
                if (s.TryGetValue("detail", out var de)) _detail = (string)de;
                if (s.TryGetValue("wind", out var wi)) _wind = D(wi);
                if (s.TryGetValue("windDir", out var wd)) _windDir = (string)wd;
                if (s.TryGetValue("ground", out var gr)) _ground = (bool)gr;
                if (s.TryGetValue("groundMm", out var gm)) _groundMm = D(gm);
                if (s.TryGetValue("weightG", out var wg)) _weightG = D(wg);
                if (s.TryGetValue("disks", out var ds) && ds is object[] arr)
                    foreach (Dictionary<string, object> d in arr)
                        _disks.Add(new CfdDisk
                        {
                            Name = (string)d["name"], Center = V(d["c"]), Axis = V(d["a"]), Radius = D(d["r"]), HubRadius = D(d["hub"]), Thrust = D(d["t"]), Torque = D(d["q"]),
                            Ducted = (bool)d["duct"], Loading = (bool)d["prop"] ? CfdLoading.Prop : CfdLoading.Uniform,
                        });
            }
            catch { }
        }

        // ================= AI 接口 =================

        internal static IEnumerable<ApiAction> ApiActions => new[]
        {
            new ApiAction("load", "读入整机模型（.sldasm / .sldprt / .step / .stl）", "{path, up?: y | z}"),
            new ApiAction("sample", "读入自带的四旋翼示例（已放好 4 个桨盘）", ""),
            new ApiAction("disks", "列出桨盘", ""),
            new ApiAction("add_disk", "加一个桨盘（转子或涵道）", "{center_mm: [x,y,z], axis: [x,y,z] 推力方向, diameter_mm, hub_mm?, thrust_N, torque_Nm?, ducted?, loading?: uniform | prop, name?}"),
            new ApiAction("set_disk", "改桨盘（只改给出的字段）", "{index, ...同 add_disk}"),
            new ApiAction("remove_disk", "删除桨盘", "{index}"),
            new ApiAction("quad", "四旋翼模板：放 4 个桨盘", "{wheelbase_mm, prop_mm, height_mm, thrust_N, layout?: x | plus}"),
            new ApiAction("settings", "计算设置", "{detail?: 粗 | 标准 | 细, device?: 名称或 cpu, wind_ms?, wind_dir?: +X / -X / +Z ..., ground?: bool, ground_mm?}"),
            new ApiAction("run", "开始计算（等算完再返回）", ""),
            new ApiAction("result", "结果：推力、机身下洗力、各实体受力、桨盘入流", ""),
            new ApiAction("streamlines", "切换流线种子和切面", "{inlet?: bool, tip?: bool, above?: bool, slice?: bool}"),
            new ApiAction("export_vtk", "导出时间平均速度场（VTK，ParaView 可以打开）", "{path}"),
            new ApiAction("screenshot", "保存 3D 视图图片", "{path}"),
        };

        internal async Task<object> ApiAsync(string action, Dictionary<string, object> a)
        {
            if (IsBusy) throw new InvalidOperationException("正在计算，稍后再试");
            Vec3 V(string k) { var d = a.List(k).Select(o => Convert.ToDouble(o, CultureInfo.InvariantCulture)).ToArray(); if (d.Length != 3) throw new ArgumentException(k + " 要 3 个数"); return new Vec3(d[0], d[1], d[2]); }
            switch (action)
            {
                case "load":
                    {
                        var p = a.ReqStr("path");
                        if (!File.Exists(p)) throw new ArgumentException("文件不存在：" + p);
                        if (a.Has("up")) Root.Find<RadioButton>(a.Str("up").ToLowerInvariant() == "z" ? "UpZ" : "UpY").IsChecked = true;
                        await LoadAsync(p);
                        if (_loadError != null) throw new InvalidOperationException(_loadError);
                        return Info();
                    }
                case "sample":
                    await LoadSampleAsync();
                    if (_loadError != null) throw new InvalidOperationException(_loadError);
                    return Info();
                case "disks": Require(); return DiskList();
                case "add_disk":
                case "set_disk":
                    {
                        Require();
                        CfdDisk d;
                        if (action == "add_disk") { d = new CfdDisk { Name = $"桨 {_disks.Count + 1}", Axis = _up }; _disks.Add(d); }
                        else { int i = Convert.ToInt32(a["index"], CultureInfo.InvariantCulture); if (i < 0 || i >= _disks.Count) throw new ArgumentException("没有这个桨盘"); d = _disks[i]; }
                        if (a.Has("center_mm")) d.Center = V("center_mm") / 1000;
                        if (a.Has("axis")) d.Axis = V("axis").Normalized();
                        if (a.Has("diameter_mm")) d.Radius = a.Num("diameter_mm") / 2000;
                        if (a.Has("hub_mm")) d.HubRadius = a.Num("hub_mm") / 2000;
                        if (a.Has("thrust_N")) d.Thrust = a.Num("thrust_N");
                        if (a.Has("torque_Nm")) d.Torque = a.Num("torque_Nm");
                        if (a.Has("ducted")) d.Ducted = a.Bool("ducted");
                        if (a.Has("loading")) d.Loading = a.Str("loading") == "prop" ? CfdLoading.Prop : CfdLoading.Uniform;
                        if (a.Has("name")) d.Name = a.Str("name");
                        DisksChanged();
                        return DiskList();
                    }
                case "remove_disk":
                    {
                        Require();
                        int i = Convert.ToInt32(a["index"], CultureInfo.InvariantCulture);
                        if (i < 0 || i >= _disks.Count) throw new ArgumentException("没有这个桨盘");
                        _disks.RemoveAt(i); _selDisk = -1; DisksChanged();
                        return DiskList();
                    }
                case "quad":
                    Require();
                    Quad(a.Num("wheelbase_mm"), a.Num("prop_mm"), a.Num("height_mm"), a.Num("thrust_N"), a.Str("layout", "x") != "plus");
                    return DiskList();
                case "settings":
                    if (a.Has("detail")) _detail = a.Str("detail");
                    if (a.Has("device")) { var dv = a.Str("device"); _device = dv.ToLowerInvariant() == "cpu" ? CfdSolver.Devices().FirstOrDefault(x => x.StartsWith("CPU")) ?? "CPU" : CfdSolver.Devices().FirstOrDefault(x => x.IndexOf(dv, StringComparison.OrdinalIgnoreCase) >= 0) ?? _device; }
                    if (a.Has("wind_ms")) _wind = a.Num("wind_ms");
                    if (a.Has("wind_dir")) _windDir = a.Str("wind_dir").Replace("-", "−");
                    if (a.Has("ground")) _ground = a.Bool("ground");
                    if (a.Has("ground_mm")) _groundMm = a.Num("ground_mm");
                    if (_tris != null) { SaveSetup(); Invalidate(); }
                    return ApiArgs.Ret("detail", _detail, "device", _device, "wind_ms", _wind, "wind_dir", _windDir, "ground", _ground, "ground_mm", _groundMm);
                case "run":
                    Require();
                    await RunAsync();
                    if (_result == null) throw new InvalidOperationException(_status.Text);
                    return ResultInfo();
                case "result":
                    if (_result == null) throw new InvalidOperationException("还没有结果：先 run");
                    return ResultInfo();
                case "streamlines":
                    if (a.Has("inlet")) Root.Find<ToggleButton>("SeedInlet").IsChecked = a.Bool("inlet");
                    if (a.Has("tip")) Root.Find<ToggleButton>("SeedTip").IsChecked = a.Bool("tip");
                    if (a.Has("above")) Root.Find<ToggleButton>("SeedAbove").IsChecked = a.Bool("above");
                    if (a.Has("slice")) Root.Find<ToggleButton>("SliceToggle").IsChecked = a.Bool("slice");
                    await RefreshResultView();
                    return ApiArgs.Ret("lines", _lines?.Count ?? 0, "vmax_ms", _vmax);
                case "export_vtk":
                    {
                        if (_result == null) throw new InvalidOperationException("还没有结果：先 run");
                        var p = System.IO.Path.GetFullPath(a.ReqStr("path"));
                        CfdVtk.Write(_result, p);
                        return ApiArgs.Ret("path", p);
                    }
                case "screenshot":
                    {
                        var p = System.IO.Path.GetFullPath(a.ReqStr("path"));
                        _view.SaveImage(p, 2);
                        return ApiArgs.Ret("path", p);
                    }
                default: throw new ArgumentException("未知操作：" + action);
            }
        }

        void Require() { if (_tris == null) throw new InvalidOperationException("先读入模型（load 或 sample）"); }

        object Info() => ApiArgs.Ret("model", _source, "parts", _partNames?.Length ?? 0, "triangles", (_tris?.Length ?? 0) / 9, "up", new[] { _up.X, _up.Y, _up.Z },
            "bbox_mm", new[] { _bmin.X * 1000, _bmin.Y * 1000, _bmin.Z * 1000, _bmax.X * 1000, _bmax.Y * 1000, _bmax.Z * 1000 }, "disks", DiskList());

        object DiskList() => _disks.Select((d, i) => ApiArgs.Ret("index", i, "name", d.Name, "center_mm", new[] { Math.Round(d.Center.X * 1000, 2), Math.Round(d.Center.Y * 1000, 2), Math.Round(d.Center.Z * 1000, 2) },
            "axis", new[] { d.Axis.X, d.Axis.Y, d.Axis.Z }, "diameter_mm", Math.Round(d.Radius * 2000, 2), "hub_mm", Math.Round(d.HubRadius * 2000, 2), "thrust_N", d.Thrust, "torque_Nm", d.Torque,
            "ducted", d.Ducted, "loading", d.Loading == CfdLoading.Prop ? "prop" : "uniform")).ToList();

        object ResultInfo()
        {
            var r = _result;
            double thrustUp = r.Setup.Disks.Sum(d => d.Thrust * d.Axis.Normalized().Dot(_up));
            return ApiArgs.Ret("total_vertical_thrust_N", thrustUp, "download_N", Download(r), "body_force_N", new[] { r.BodyForce.X, r.BodyForce.Y, r.BodyForce.Z },
                "parts", r.PartForces.OrderByDescending(p => p.Force.Length).Take(20).Select(p => ApiArgs.Ret("part", p.Part, "force_N", new[] { p.Force.X, p.Force.Y, p.Force.Z })).ToList(),
                "disks", r.DiskStats.Select(s => ApiArgs.Ret("name", s.Disk.Name, "inflow_ms", s.InflowSpeed, "ideal_inflow_ms", s.IdealInflow)).ToList(),
                "grid", new[] { r.Nx, r.Ny, r.Nz }, "cell_mm", r.Dx * 1000, "steps", r.Steps, "sim_time_s", r.SimTime, "seconds", r.Seconds, "device", r.Device, "oscillating", r.Oscillating, "notes", r.Notes);
        }

        // ================= 自测 =================

        internal async Task<string> SelfTest(string file, string detail, Func<string, Task> shot)
        {
            StoreOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swkit-cfd-selftest.json");
            var sb = new StringBuilder();
            if (file == "sample") await LoadSampleAsync(); else await LoadAsync(file);
            if (_tris == null) return "读入失败：" + _status.Text;
            sb.AppendLine($"读入：{_status.Text}");
            if (detail != null) _detail = detail;
            _selDisk = 0; BuildSide();
            if (shot != null) await shot("setup");
            await RunAsync();
            sb.AppendLine("计算：" + _status.Text);
            if (_result != null)
            {
                sb.AppendLine($"格子 {_result.Nx}×{_result.Ny}×{_result.Nz}，{_result.Steps} 步，{_result.Seconds:0} 秒，{_result.Device}；流线 {_lines?.Count ?? 0} 条，色标上限 {_vmax} m/s");
                foreach (var s in _result.DiskStats) sb.AppendLine($"  {s.Disk.Name}：入流 {s.InflowSpeed:0.##} m/s，动量理论 {s.IdealInflow:0.##} m/s");
                sb.AppendLine($"  机身受力 ({_result.BodyForce.X:0.###}, {_result.BodyForce.Y:0.###}, {_result.BodyForce.Z:0.###}) N，下洗力 {Download(_result):0.###} N");
                if (shot != null) await shot("result");
                Root.Find<ToggleButton>("SliceToggle").IsChecked = true;
                await Task.Delay(300);
                if (shot != null) await shot("slice");
            }
            return sb.ToString();
        }
    }
}
