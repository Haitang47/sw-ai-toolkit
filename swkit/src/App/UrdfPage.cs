using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SwKit.Core;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>"URDF 导出"页面。</summary>
    sealed class UrdfPage
    {
        public FrameworkElement Root { get; }
        public bool IsBusy { get; private set; }

        readonly Window _owner;
        readonly SwWorker _worker;
        readonly RobotView _view = new RobotView();
        readonly Dictionary<Joint, double> _q = new Dictionary<Joint, double>();
        readonly DispatcherTimer _tick;

        string _asmPath, _outDir, _up = "y";
        bool _expandSubs = true;
        InferMode _infer = InferMode.Auto;
        RobotModel _model;
        AsmData _raw;
        Dictionary<string, Mesh> _parts;
        UrdfExportResult _last;
        DateTime _busyStart;
        string _busyPhase;

        readonly FrameworkElement _asmBar, _empty, _busy, _result;
        readonly TextBlock _asmName, _asmDirText, _status, _busyText, _busyDetail, _outDirText;
        readonly StackPanel _side;
        readonly Button _analyze, _export, _openPkg, _openReport;
        readonly TextBox _robotName;

        static readonly Brush Gray = Ui.Brush("#6E6E73"), Green = Ui.Brush("#248A3D"), Orange = Ui.Brush("#B25000"), Red = Ui.Brush("#D70015"), Text3 = Ui.Brush("#A1A1A6");

        public UrdfPage(Window owner, SwWorker worker)
        {
            _owner = owner;
            _worker = worker;
            Root = (FrameworkElement)Ui.Load("UrdfPage.xaml");
            _asmBar = Root.Find<FrameworkElement>("AsmBar");
            _empty = Root.Find<FrameworkElement>("EmptyState");
            _busy = Root.Find<FrameworkElement>("BusyState");
            _result = Root.Find<FrameworkElement>("ResultState");
            _asmName = Root.Find<TextBlock>("AsmName");
            _asmDirText = Root.Find<TextBlock>("AsmPath");
            _status = Root.Find<TextBlock>("StatusText");
            _busyText = Root.Find<TextBlock>("BusyText");
            _busyDetail = Root.Find<TextBlock>("BusyDetail");
            _outDirText = Root.Find<TextBlock>("OutDirText");
            _side = Root.Find<StackPanel>("SidePanel");
            _analyze = Root.Find<Button>("AnalyzeBtn");
            _export = Root.Find<Button>("ExportBtn");
            _openPkg = Root.Find<Button>("OpenPkgBtn");
            _openReport = Root.Find<Button>("OpenReportBtn");
            _robotName = Root.Find<TextBox>("RobotName");
            Root.Find<Grid>("ViewHost").Children.Add(_view.Element);

            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (s, e) => _busyDetail.Text = $"{_busyPhase} · 已用时 {(int)(DateTime.Now - _busyStart).TotalSeconds} 秒";

            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            ((RotateTransform)Root.Find<FrameworkElement>("Spinner").RenderTransform).BeginAnimation(RotateTransform.AngleProperty, spin);

            Root.Find<Button>("PickAsmBtn").Click += (s, e) => PickAssembly();
            Root.Find<Button>("ChangeAsmBtn").Click += (s, e) => PickAssembly();
            Root.Find<Button>("ResetViewBtn").Click += (s, e) => _view.ResetView();
            var axesBtn = Root.Find<Button>("AxesBtn");
            axesBtn.Click += (s, e) => { _view.ShowAxes = !_view.ShowAxes; axesBtn.Content = _view.ShowAxes ? "隐藏关节轴" : "显示关节轴"; };
            _analyze.Click += async (s, e) => await AnalyzeAsync();
            _export.Click += async (s, e) => await ExportAsync();
            _openPkg.Click += (s, e) => { if (_last != null) Process.Start("explorer.exe", $"\"{_last.PackageDir}\""); };
            _openReport.Click += (s, e) => { if (_last != null) try { Process.Start(_last.ReportPath); } catch { Process.Start("notepad.exe", $"\"{_last.ReportPath}\""); } };
            Root.Find<Button>("ChooseOutBtn").Click += (s, e) =>
            {
                var p = FolderPicker.Pick(_owner, "选择 URDF 功能包的保存位置", _outDir);
                if (p != null) { _outDir = p; _outDirText.Text = p; }
            };
            var upY = Root.Find<RadioButton>("UpY"); var upZ = Root.Find<RadioButton>("UpZ");
            upY.IsChecked = true;
            upY.Checked += async (s, e) => { _up = "y"; if (_model != null) await AnalyzeAsync(); };
            upZ.Checked += async (s, e) => { _up = "z"; if (_model != null) await AnalyzeAsync(); };
            var expand = Root.Find<CheckBox>("ExpandSubs");
            expand.Checked += async (s, e) => { _expandSubs = true; if (_model != null) await AnalyzeAsync(); };
            expand.Unchecked += async (s, e) => { _expandSubs = false; if (_model != null) await AnalyzeAsync(); };
            _robotName.LostFocus += (s, e) => { _robotName.Text = UrdfWriter.SanitizeName(_robotName.Text); if (_model != null) _model.RobotName = _robotName.Text; };

            Root.DragOver += (s, e) =>
            {
                e.Effects = !IsBusy && AsmFrom(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            Root.Drop += async (s, e) =>
            {
                var p = AsmFrom(e.Data);
                if (p != null && !IsBusy) { SetAssembly(p); await AnalyzeAsync(); }
            };
        }

        static string AsmFrom(IDataObject d) =>
            (d.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(f => f.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase));

        async void PickAssembly()
        {
            if (IsBusy) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择装配体", Filter = "SolidWorks 装配体|*.sldasm" };
            if (dlg.ShowDialog(_owner) != true) return;
            SetAssembly(dlg.FileName);
            await AnalyzeAsync();
        }

        void SetAssembly(string path)
        {
            _asmPath = Path.GetFullPath(path);
            _asmName.Text = Path.GetFileName(_asmPath);
            _asmDirText.Text = Path.GetDirectoryName(_asmPath);
            _outDir = Path.GetDirectoryName(_asmPath);
            _outDirText.Text = _outDir;
            Ui.Fade(_asmBar, true);
        }

        void SetBusy(bool busy, string title = null)
        {
            IsBusy = busy;
            _analyze.IsEnabled = !busy;
            _export.IsEnabled = !busy && _model != null;
            if (busy)
            {
                _busyStart = DateTime.Now;
                _busyText.Text = title;
                _busyPhase = "准备中";
                Ui.Fade(_empty, false); Ui.Fade(_result, false); Ui.Fade(_busy, true);
                _tick.Start();
            }
            else _tick.Stop();
        }

        void Phase(string text)
        {
            Root.Dispatcher.BeginInvoke(new Action(() => { _busyPhase = text; _status.Text = text; }));
        }

        // ================= 分析 =================

        public async Task AnalyzeAsync()
        {
            if (_asmPath == null || IsBusy) return;
            SetBusy(true, "正在分析装配体");
            _status.Foreground = Gray;
            Ui.SetShown(_openPkg, false); Ui.SetShown(_openReport, false);
            var path = _asmPath; var up = _up; var expandSubs = _expandSubs; var infer = _infer;
            try
            {
                var r = await _worker.Run(() =>
                {
                    var s = _worker.Acquire(m => Phase(m.Contains("启动") ? "正在启动后台 SolidWorks（第一次约 20–60 秒）" : m), CancellationToken.None);
                    try
                    {
                        Phase("正在打开装配体（大装配体需要一些时间）");
                        var doc = UrdfExporter.OpenAssembly(s.App, path);
                        var model = UrdfExporter.Analyze(s.App, doc, up, out var raw, Phase, expandSubs, infer);
                        Phase("正在导出零件网格用于预览");
                        var parts = MeshBuilder.ExportParts(s.App, doc, up, Phase, raw.AllParts);
                        try { s.App.CloseDoc(doc.GetTitle()); } catch { }
                        return (model, raw, parts);
                    }
                    finally { _worker.Release(); }
                });
                _raw = r.raw; _parts = r.parts;
                ShowModel(r.model);
                SetBusy(false);
                Ui.Fade(_busy, false); Ui.Fade(_result, true);
            }
            catch (Exception ex)
            {
                SetBusy(false);
                Ui.Fade(_busy, false);
                Ui.Fade(_model == null ? _empty : _result, true);
                _status.Text = "分析失败：" + ex.Message;
                _status.Foreground = Red;
            }
        }

        /// <summary>显示一个识别结果（分析完成、切换识别方式后）。</summary>
        void ShowModel(RobotModel model)
        {
            var keepName = _model != null && _robotName.Text != _model.RobotName ? _robotName.Text : null;
            _model = model;
            if (keepName != null) _model.RobotName = keepName;
            _robotName.Text = _model.RobotName;
            _last = null;
            _q.Clear();
            foreach (var j in _model.Joints) _q[j] = 0;
            _view.Load(_model, _parts);
            _view.SetPose(Q);
            BuildSide();
            UpdateStatus();
        }

        void UpdateStatus()
        {
            var moving = _model.Joints.Count(j => j.Type != JointType.Fixed);
            var inferred = _model.InferredCount;
            var suggested = _model.Joints.Count(j => j.Inferred && j.Type == JointType.Fixed && j.Detected != JointType.Fixed);
            var review = _model.Joints.Count(j => j.NeedsReview) + _model.Warnings.Count;
            _status.Text = $"识别出 {_model.Links.Count} 个连杆、{moving} 个运动关节"
                           + (inferred > 0 ? $"（其中 {inferred} 个由零件几何推断）" : "")
                           + (suggested > 0 ? $"，另有 {suggested} 个把握不大的建议默认没启用" : "")
                           + (_model.Suggestions.Count > 0 ? $"，{_model.Suggestions.Count} 处可能被配合锁死" : "")
                           + (review > 0 ? $"，{review} 处需要确认" : "") + "。拖动右侧滑条检查运动，确认后导出";
            _status.Foreground = review > 0 ? Orange : Gray;
        }

        /// <summary>切换识别方式：用已经读出的装配体数据重新识别，不用再开 SolidWorks（之前没读几何时才重新分析）。</summary>
        async Task SetInferAsync(InferMode mode)
        {
            if (_infer == mode) return;
            _infer = mode;
            if (_raw == null || IsBusy) return;
            if (mode != InferMode.Off && !_raw.HasGeometry) { await AnalyzeAsync(); return; }
            ShowModel(UrdfExporter.Solve(_raw, _up, mode));
        }

        static string InferText(InferMode m) => m == InferMode.Auto ? "配合 + 几何" : m == InferMode.GeometryOnly ? "只按几何" : "只看配合";

        double Q(Joint j) => _q.TryGetValue(j, out var v) ? v : 0;

        // ================= 导出 =================

        public async Task ExportAsync(bool mergeFixed = true)
        {
            if (_model == null || IsBusy) return;
            _model.RobotName = UrdfWriter.SanitizeName(_robotName.Text);
            _robotName.Text = _model.RobotName;
            IsBusy = true;
            _export.IsEnabled = false; _analyze.IsEnabled = false;
            _status.Text = "正在导出并校验……"; _status.Foreground = Gray;
            try
            {
                var model = _model; var raw = _raw; var parts = _parts; var outDir = _outDir;
                _last = await Task.Run(() => UrdfExporter.Export(null, null, raw, model, outDir, null, parts, mergeFixed));   // 网格已在分析时取得，这一步不需要 SolidWorks
                var rep = _last.Report;
                _status.Text = $"已导出到 {_last.PackageDir} · {_last.Model.Links.Count} 个连杆、{_last.Model.Joints.Count(j => j.Type != JointType.Fixed)} 个运动关节" + (mergeFixed ? "（固定连接已并入上级连杆）" : "") + $" · 校验{rep.Summary}";
                _status.Foreground = rep.Errors > 0 ? Red : rep.Warnings > 0 ? Orange : Green;
                Ui.Fade(_openPkg, true); Ui.Fade(_openReport, true);
                BuildSide();
            }
            catch (Exception ex)
            {
                _status.Text = "导出失败：" + ex.Message;
                _status.Foreground = Red;
            }
            finally
            {
                IsBusy = false;
                _export.IsEnabled = true; _analyze.IsEnabled = true;
            }
        }

        // ================= 右侧面板 =================

        void BuildSide()
        {
            _side.Children.Clear();
            if (_last != null && _last.Report != null) AddReport(_last.Report);

            // 识别方式
            Section("关节识别方式");
            var modeBox = new StackPanel();
            var seg = new Border { Style = (Style)Application.Current.FindResource("SegmentHost"), HorizontalAlignment = HorizontalAlignment.Left };
            var segSp = new StackPanel { Orientation = Orientation.Horizontal };
            seg.Child = segSp;
            var group = "infer" + Guid.NewGuid().ToString("N");
            foreach (var m in new[] { InferMode.Auto, InferMode.GeometryOnly, InferMode.Off })
            {
                var rb = new RadioButton { Style = (Style)Application.Current.FindResource("Segment"), GroupName = group, Content = InferText(m), IsChecked = m == _infer };
                var mm = m;
                rb.Checked += async (s, e) => await SetInferAsync(mm);
                segSp.Children.Add(rb);
            }
            modeBox.Children.Add(seg);
            var modeText = _infer == InferMode.Off ? "只用 SolidWorks 里的配合识别关节（旧版行为）。"
                         : _infer == InferMode.GeometryOnly ? "忽略配合和固定状态，只看零件几何：同轴的轴和孔、轴承、电机 / 舵机输出端、导轨和光轴。适合没有配合、或者配合全锁死的装配体。"
                         : _model.InferUsed == InferMode.GeometryOnly ? "这个装配体的配合没有留下运动关节，已自动改为按零件几何推断。"
                         : "以配合为准；没有配合的零件按几何接触推断。紫色箭头是几何推断的关节轴。";
            modeBox.Children.Add(Caption(modeText, Gray, new Thickness(0, 8, 0, 0)));
            _side.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Group"), Child = modeBox });

            Section($"关节（{_model.Joints.Count}）");
            if (_model.Joints.Count == 0)
                _side.Children.Add(Caption("没有识别出关节：所有零部件都是刚性连接在一起的。请检查装配体里的配合，或换成「只按几何」。", Gray, new Thickness(4, 0, 0, 18)));
            // 运动关节（以及需要确认的、几何推断的建议）放前面；识别为固定、可空转的折叠起来，避免螺钉、铝柱刷屏
            var shown = _model.Joints.Where(j => j.Detected != JointType.Fixed && !j.SpinCandidate || j.NeedsReview || j.Type != JointType.Fixed)
                                     .OrderBy(j => j.Type != JointType.Fixed ? 0 : 1).ThenByDescending(j => j.Confidence).ToList();
            foreach (var j in shown) _side.Children.Add(JointCard(j));
            var folded = _model.Joints.Except(shown).ToList();
            if (folded.Count > 0)
            {
                var more = new StackPanel { Visibility = Visibility.Collapsed };
                var spin = folded.Count(j => j.SpinCandidate);
                var toggle = new Button
                {
                    Style = (Style)Application.Current.FindResource("Btn.Text"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12),
                    Content = $"显示另外 {folded.Count} 个固定连接" + (spin > 0 ? $"（其中 {spin} 个是能空转的螺钉、铝柱等）" : ""),
                };
                toggle.Click += (s, e) =>
                {
                    if (more.Children.Count == 0) foreach (var j in folded) more.Children.Add(JointCard(j));
                    more.Visibility = more.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                };
                _side.Children.Add(toggle);
                _side.Children.Add(more);
            }

            Section($"连杆（{_model.Links.Count}）");
            var links = new StackPanel();
            foreach (var l in _model.Links)
            {
                var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = l.Name, FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
                var lbl = new TextBlock { Text = l.Label + (l.Members.Count > 1 ? $"（{l.Members.Count} 个零部件）" : ""), FontSize = 12.5, Foreground = Gray, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = string.Join("\n", l.Members.Select(m => m.Name2)) };
                Grid.SetColumn(lbl, 1); g.Children.Add(lbl);
                var mass = new TextBlock { Text = $"{l.Mass:0.###} kg", FontSize = 12.5, Foreground = Gray };
                Grid.SetColumn(mass, 2); g.Children.Add(mass);
                links.Children.Add(g);
            }
            _side.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Group"), Child = links });

            if (_model.Suggestions.Count > 0)
            {
                Section("可能被配合锁死的关节");
                var sp = new StackPanel();
                foreach (var t in _model.Suggestions) sp.Children.Add(Caption("· " + t, Orange, new Thickness(0, 3, 0, 3)));
                _side.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Group"), Child = sp });
            }

            var notes = _model.Warnings.Select(w => (w, Orange)).Concat(_model.Infos.Select(i => (i, Gray))).ToList();
            if (notes.Count > 0)
            {
                Section("提示");
                var sp = new StackPanel();
                foreach (var (text, color) in notes) sp.Children.Add(Caption("· " + text, color, new Thickness(0, 3, 0, 3)));
                _side.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Group"), Child = sp });
            }
        }

        void Section(string text) => _side.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("T.Section") });

        static TextBlock Caption(string text, Brush color, Thickness margin) =>
            new TextBlock { Text = text, FontSize = 12, Foreground = color, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = margin };

        void AddReport(ValidationReport rep)
        {
            Section("校验结果");
            var sp = new StackPanel();
            foreach (var item in rep.Items.Where(i => i.Level != CheckLevel.Info).OrderByDescending(i => i.Level))
            {
                var (icon, color) = item.Level == CheckLevel.Pass ? ("", Green) : item.Level == CheckLevel.Warn ? ("", Orange) : ("", Red);
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 12, Foreground = color, Margin = new Thickness(0, 3, 0, 0) });
                var t = new TextBlock { Text = item.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
                Grid.SetColumn(t, 1); g.Children.Add(t);
                sp.Children.Add(g);
            }
            var infos = rep.Items.Count(i => i.Level == CheckLevel.Info);
            if (infos > 0) sp.Children.Add(Caption($"另有 {infos} 条提示，见校验报告", Gray, new Thickness(22, 4, 0, 0)));
            _side.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Group"), Child = sp });
        }

        FrameworkElement JointCard(Joint j)
        {
            var card = new Border { Style = (Style)Application.Current.FindResource("Group") };
            var sp = new StackPanel();
            card.Child = sp;

            // 标题行
            var head = new DockPanel();
            bool suggestion = j.Inferred && j.Type == JointType.Fixed && j.Detected != JointType.Fixed;
            if (suggestion)
                head.Children.Add(Badge("建议 · 未启用", "#F2F2F7", "#6E6E73"));
            else if (j.Inferred)
                head.Children.Add(Badge($"几何推断 · {Joint.ConfidenceText(j.Confidence)}", "#F3EBF8", "#8944AB"));
            else if (j.NeedsReview)
                head.Children.Add(Badge("需确认", "#FFF2E0", "#B25000"));
            else if (j.Type != JointType.Fixed)
                head.Children.Add(Badge("自动识别", "#E8F6EC", "#248A3D"));
            else if (j.SpinCandidate)
                head.Children.Add(Badge("可空转", "#F2F2F7", "#6E6E73"));
            DockPanel.SetDock(head.Children.Count > 0 ? (UIElement)head.Children[0] : new Border(), Dock.Right);
            var title = new TextBlock { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            title.Inlines.Add(new System.Windows.Documents.Run(j.Name) { FontWeight = FontWeights.Bold });
            title.Inlines.Add(new System.Windows.Documents.Run($"   {j.Parent.Label} → {j.Child.Label}") { Foreground = Gray, FontSize = 12.5 });
            head.Children.Add(title);
            sp.Children.Add(head);

            var body = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            sp.Children.Add(body);
            var detected = j.Detected;
            void Rebuild()
            {
                body.Children.Clear();
                // 类型
                var types = detected == JointType.Revolute || detected == JointType.Continuous ? new[] { JointType.Revolute, JointType.Continuous, JointType.Fixed }
                          : detected == JointType.Prismatic ? new[] { JointType.Prismatic, JointType.Fixed } : new[] { JointType.Fixed };
                body.Children.Add(Row("类型", SegmentFor(types, j.Type, t =>
                {
                    j.Type = t;
                    if (t == JointType.Fixed) _q[j] = 0;
                    _model.RecomputeFrames();
                    _view.SetPose(Q);
                    _view.RefreshAxes();
                    _view.SetPose(Q);
                    Rebuild();
                    UpdateStatus();
                })));
                if (j.Type == JointType.Fixed)
                {
                    if (detected == JointType.Fixed) body.Children.Add(Caption("这两部分之间没有剩余自由度（或无法识别），按固定连接导出。", Gray, new Thickness(0, 6, 0, 0)));
                    else if (j.Inferred) body.Children.Add(Caption($"几何上像{Joint.TypeText(detected)}关节，但把握不大（{j.Confidence:0.00}），默认按固定导出。确认是关节的话，在上面改成{Joint.TypeText(detected)}。", Gray, new Thickness(0, 6, 0, 0)));
                }
                else
                {
                    // 轴
                    var axisRow = new DockPanel();
                    var flip = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "反向", Padding = new Thickness(6, 2, 6, 2) };
                    DockPanel.SetDock(flip, Dock.Right);
                    axisRow.Children.Add(flip);
                    axisRow.Children.Add(new TextBlock { Text = AxisText(j.Axis), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
                    flip.Click += (s, e) =>
                    {
                        j.Axis = -j.Axis;
                        var lo = j.Lower; j.Lower = -j.Upper; j.Upper = -lo;
                        _q[j] = -Q(j);
                        _view.SetPose(Q);
                        _view.RefreshAxes();
                        Rebuild();
                    };
                    body.Children.Add(Row("轴", axisRow));

                    var prismatic = j.Type == JointType.Prismatic;
                    double toUi = prismatic ? 1000 : 180 / Math.PI;
                    var unit = prismatic ? "mm" : "°";
                    if (j.Type != JointType.Continuous)
                    {
                        var lo = Field(j.Lower * toUi); var hi = Field(j.Upper * toUi);
                        var limRow = new StackPanel { Orientation = Orientation.Horizontal };
                        limRow.Children.Add(lo);
                        limRow.Children.Add(new TextBlock { Text = "~", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Gray });
                        limRow.Children.Add(hi);
                        limRow.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Gray });
                        if (j.LimitsFromMate) limRow.Children.Add(new TextBlock { Text = "来自限位配合", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Green, FontSize = 11.5 });
                        void Commit()
                        {
                            if (TryNum(lo.Text, out var a) && TryNum(hi.Text, out var b) && a < b)
                            {
                                j.Lower = a / toUi; j.Upper = b / toUi;
                                _q[j] = Math.Max(j.Lower, Math.Min(j.Upper, Q(j)));
                                _view.SetPose(Q);
                                Rebuild();
                            }
                            else { lo.Text = Fmt(j.Lower * toUi); hi.Text = Fmt(j.Upper * toUi); }
                        }
                        foreach (var f in new[] { lo, hi })
                        {
                            f.LostFocus += (s, e) => Commit();
                            f.KeyDown += (s, e) => { if (e.Key == Key.Enter) Commit(); };
                        }
                        body.Children.Add(Row("范围", limRow));
                    }

                    // 预览滑条
                    double min = j.Type == JointType.Continuous ? -Math.PI : j.Lower, max = j.Type == JointType.Continuous ? Math.PI : j.Upper;
                    var slider = new Slider { Style = (Style)Application.Current.FindResource("Slim"), Minimum = min * toUi, Maximum = max * toUi, Value = Q(j) * toUi };
                    var val = new TextBlock { Width = 64, TextAlignment = TextAlignment.Right, FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Text = Fmt(Q(j) * toUi) + unit };
                    slider.ValueChanged += (s, e) => { _q[j] = slider.Value / toUi; val.Text = Fmt(slider.Value) + unit; _view.SetPose(Q); };
                    slider.MouseEnter += (s, e) => _view.Highlight(j);
                    slider.MouseLeave += (s, e) => _view.Highlight(null);
                    slider.MouseDoubleClick += (s, e) => slider.Value = 0;
                    var sliderRow = new DockPanel();
                    DockPanel.SetDock(val, Dock.Right);
                    sliderRow.Children.Add(val);
                    sliderRow.Children.Add(slider);
                    body.Children.Add(Row("预览", sliderRow));
                }

                if (j.Inferred && j.Evidence != null)
                    body.Children.Add(Caption("依据：" + j.Evidence, Ui.Brush("#8944AB"), new Thickness(0, 8, 0, 0)));
                body.Children.Add(Caption("来源：" + j.Source, Text3, new Thickness(0, j.Inferred ? 4 : 8, 0, 0)));
                foreach (var n in j.Notes.Where(n => !(j.Inferred && n.StartsWith("几何推断（"))))
                    body.Children.Add(Caption(n, j.NeedsReview ? Orange : Gray, new Thickness(0, 4, 0, 0)));
            }
            Rebuild();
            card.MouseEnter += (s, e) => { if (j.Type != JointType.Fixed) _view.Highlight(j); };
            card.MouseLeave += (s, e) => _view.Highlight(null);
            return card;
        }

        static string AxisText(Vec3 a) => $"({Vec3.Num(a.X, "0.###")}, {Vec3.Num(a.Y, "0.###")}, {Vec3.Num(a.Z, "0.###")})";
        static string Fmt(double v) => Vec3.Num(Math.Round(v, 2), "0.##");
        static bool TryNum(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        TextBox Field(double v) => new TextBox { Style = (Style)Application.Current.FindResource("Field"), Width = 74, Text = Fmt(v), TextAlignment = TextAlignment.Right };

        static FrameworkElement Badge(string text, string bg, string fg) => new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 2), Background = Ui.Brush(bg), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(fg) },
        };

        static FrameworkElement Row(string label, FrameworkElement content)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(content, 1);
            g.Children.Add(content);
            return g;
        }

        static FrameworkElement SegmentFor(JointType[] types, JointType current, Action<JointType> onChange)
        {
            var host = new Border { Style = (Style)Application.Current.FindResource("SegmentHost"), HorizontalAlignment = HorizontalAlignment.Left };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            host.Child = sp;
            var group = "jt" + Guid.NewGuid().ToString("N");
            foreach (var t in types)
            {
                var rb = new RadioButton { Style = (Style)Application.Current.FindResource("Segment"), GroupName = group, Content = Joint.TypeText(t), IsChecked = t == current };
                var tt = t;
                rb.Checked += (s, e) => { if (tt != current) onChange(tt); };
                sp.Children.Add(rb);
            }
            return host;
        }

        // ================= 给 AI / 脚本用的接口 =================

        internal static IEnumerable<ApiAction> ApiActions => new[]
        {
            new ApiAction("load", "打开装配体并识别关节（在后台 SolidWorks 里只读打开）。返回连杆、关节摘要。",
                "{path: 装配体路径, expandSubs?: 是否分析子装配体内部（默认 true）, infer?: \"auto\"（配合 + 几何，默认）| \"geometry\"（只按几何，忽略配合）| \"mates\"（只看配合）, up?: \"y\"|\"z\"}"),
            new ApiAction("set_infer", "切换关节识别方式（用已读出的数据重新识别，不需要 SolidWorks）。", "{infer: \"auto\"|\"geometry\"|\"mates\"}"),
            new ApiAction("joints", "列出关节：名字、类型、父子连杆、轴、来源（配合 / 几何推断）、置信度、是否启用、依据、范围。", "{}"),
            new ApiAction("set_joint", "修改一个关节。enabled=false 等于改成固定；type 可选 revolute / continuous / prismatic / fixed（只能在识别出的类型里选）。",
                "{name: 关节名（如 joint3）, enabled?: bool, type?: string, lower?: 下限（度或毫米）, upper?: 上限, flip?: bool 反转轴方向}"),
            new ApiAction("export", "导出 URDF 功能包并校验。", "{outDir?: 输出文件夹（默认装配体所在文件夹）, robotName?: 机器人名, keepFixed?: 固定连接不并入上级（默认 false）}"),
            new ApiAction("status", "当前状态：装配体、识别方式、连杆 / 关节数、状态栏文字、上次导出结果。", "{}"),
        };

        internal async Task<object> ApiAsync(string action, Dictionary<string, object> args)
        {
            switch (action)
            {
                case "load":
                    {
                        var path = args.ReqStr("path");
                        if (!File.Exists(path)) throw new ArgumentException("找不到装配体：" + path);
                        if (IsBusy) throw new InvalidOperationException("URDF 页面正忙，稍后再试");
                        _model = null; _raw = null;   // 先清掉，下面改开关时不会触发重新分析
                        if (args.Has("expandSubs")) { _expandSubs = args.Bool("expandSubs", true); Root.Find<CheckBox>("ExpandSubs").IsChecked = _expandSubs; }
                        if (args.Has("infer")) _infer = ParseInfer(args.Str("infer"));
                        if (args.Has("up")) { _up = args.Str("up").ToLowerInvariant() == "z" ? "z" : "y"; (_up == "z" ? Root.Find<RadioButton>("UpZ") : Root.Find<RadioButton>("UpY")).IsChecked = true; }
                        SetAssembly(path);
                        await AnalyzeAsync();
                        if (_model == null) throw new InvalidOperationException(_status.Text);
                        return Summary();
                    }
                case "set_infer":
                    {
                        if (_model == null) throw new InvalidOperationException("还没有打开装配体，先调用 load");
                        await SetInferAsync(ParseInfer(args.ReqStr("infer")));
                        return Summary();
                    }
                case "joints":
                    if (_model == null) throw new InvalidOperationException("还没有打开装配体，先调用 load");
                    return _model.Joints.Select(JointInfo).ToList();
                case "set_joint":
                    {
                        if (_model == null) throw new InvalidOperationException("还没有打开装配体，先调用 load");
                        var name = args.ReqStr("name");
                        var j = _model.Joints.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("没有这个关节：" + name);
                        var allowed = j.Detected == JointType.Revolute || j.Detected == JointType.Continuous ? new[] { JointType.Revolute, JointType.Continuous, JointType.Fixed }
                                    : j.Detected == JointType.Prismatic ? new[] { JointType.Prismatic, JointType.Fixed } : new[] { JointType.Fixed };
                        if (args.Has("type"))
                        {
                            var t = ParseType(args.Str("type"));
                            if (!allowed.Contains(t)) throw new ArgumentException($"{j.Name} 只能设为：" + string.Join(" / ", allowed.Select(Joint.UrdfType)));
                            j.Type = t;
                        }
                        if (args.Has("enabled")) j.Type = args.Bool("enabled") ? (j.Detected == JointType.Fixed ? JointType.Fixed : j.Detected) : JointType.Fixed;
                        if (args.Bool("flip")) { j.Axis = -j.Axis; var lo0 = j.Lower; j.Lower = -j.Upper; j.Upper = -lo0; }
                        double toSi = j.Type == JointType.Prismatic ? 0.001 : Math.PI / 180;
                        if (args.Has("lower")) j.Lower = args.Num("lower") * toSi;
                        if (args.Has("upper")) j.Upper = args.Num("upper") * toSi;
                        if (j.Lower > j.Upper) { var t2 = j.Lower; j.Lower = j.Upper; j.Upper = t2; }
                        if (j.Type == JointType.Fixed) _q[j] = 0;
                        _model.RecomputeFrames();
                        _view.RefreshAxes();
                        _view.SetPose(Q);
                        BuildSide();
                        UpdateStatus();
                        return JointInfo(j);
                    }
                case "export":
                    {
                        if (_model == null) throw new InvalidOperationException("还没有打开装配体，先调用 load");
                        if (args.Has("outDir")) { _outDir = Path.GetFullPath(args.Str("outDir")); Directory.CreateDirectory(_outDir); _outDirText.Text = _outDir; }
                        if (args.Has("robotName")) _robotName.Text = UrdfWriter.SanitizeName(args.Str("robotName"));
                        _last = null;
                        await ExportAsync(!args.Bool("keepFixed"));
                        if (_last == null) throw new InvalidOperationException(_status.Text);
                        var rep = _last.Report;
                        return ApiArgs.Ret("packageDir", _last.PackageDir, "urdf", _last.UrdfPath, "report", _last.ReportPath,
                            "links", _last.Model.Links.Count, "movingJoints", _last.Model.Joints.Count(j => j.Type != JointType.Fixed),
                            "errors", rep.Errors, "warnings", rep.Warnings, "summary", rep.Summary,
                            "items", rep.Items.Where(i => i.Level != CheckLevel.Info).Select(i => ApiArgs.Ret("level", CheckItem.LevelText(i.Level), "category", i.Category, "text", i.Text)).ToList());
                    }
                case "status":
                    return _model == null ? ApiArgs.Ret("assembly", _asmPath, "busy", IsBusy, "status", _status.Text) : Summary();
                default:
                    throw new ArgumentException("URDF 页面没有这个操作：" + action);
            }
        }

        static InferMode ParseInfer(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "geometry": case "geometry_only": case "ignore_mates": case "几何": case "只按几何": return InferMode.GeometryOnly;
                case "mates": case "off": case "配合": case "只看配合": return InferMode.Off;
                case "auto": case "": case "配合+几何": return InferMode.Auto;
                default: throw new ArgumentException("infer 只能是 auto / geometry / mates");
            }
        }

        static JointType ParseType(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "revolute": case "转动": return JointType.Revolute;
                case "continuous": case "连续转动": return JointType.Continuous;
                case "prismatic": case "移动": return JointType.Prismatic;
                case "fixed": case "固定": return JointType.Fixed;
                default: throw new ArgumentException("type 只能是 revolute / continuous / prismatic / fixed");
            }
        }

        Dictionary<string, object> Summary() => ApiArgs.Ret(
            "assembly", _asmPath, "infer", _infer == InferMode.Auto ? "auto" : _infer == InferMode.GeometryOnly ? "geometry" : "mates",
            "inferUsed", _model.InferUsed == InferMode.GeometryOnly ? "geometry" : _model.InferUsed == InferMode.Auto ? "auto" : "mates",
            "robotName", _model.RobotName, "links", _model.Links.Count,
            "movingJoints", _model.Joints.Count(j => j.Type != JointType.Fixed),
            "inferredJoints", _model.InferredCount,
            "suggestedJoints", _model.Joints.Count(j => j.Inferred && j.Type == JointType.Fixed && j.Detected != JointType.Fixed),
            "joints", _model.Joints.Where(j => j.Type != JointType.Fixed || j.Inferred && j.Detected != JointType.Fixed).Select(JointInfo).ToList(),
            "lockedByMates", _model.Suggestions.ToList(),
            "warnings", _model.Warnings.ToList(), "infos", _model.Infos.ToList(),
            "status", _status.Text,
            "lastExport", _last?.PackageDir);

        static Dictionary<string, object> JointInfo(Joint j)
        {
            double toUi = j.Type == JointType.Prismatic || j.Detected == JointType.Prismatic ? 1000 : 180 / Math.PI;
            return ApiArgs.Ret(
                "name", j.Name, "type", Joint.UrdfType(j.Type), "detected", Joint.UrdfType(j.Detected), "enabled", j.Type != JointType.Fixed,
                "parent", j.Parent.Name, "parentLabel", j.Parent.Label, "child", j.Child.Name, "childLabel", j.Child.Label,
                "axis", new[] { Math.Round(j.Axis.X, 6), Math.Round(j.Axis.Y, 6), Math.Round(j.Axis.Z, 6) },
                "axisPoint", new[] { Math.Round(j.AxisPoint.X, 6), Math.Round(j.AxisPoint.Y, 6), Math.Round(j.AxisPoint.Z, 6) },
                "source", j.Inferred ? "geometry" : "mates", "confidence", Math.Round(j.Confidence, 2), "evidence", j.Evidence,
                "lower", Math.Round(j.Lower * toUi, 3), "upper", Math.Round(j.Upper * toUi, 3), "unit", toUi > 100 ? "mm" : "deg",
                "needsReview", j.NeedsReview, "notes", j.Notes.ToList());
        }

        // ================= 自测（开发用） =================

        internal async Task<string> SelfTest(string asm, string outDir)
        {
            // 开发用：环境变量 SWKIT_SELFTEST_INFER=geometry 时按「只按几何」测试
            var env = Environment.GetEnvironmentVariable("SWKIT_SELFTEST_INFER");
            if (!string.IsNullOrEmpty(env)) _infer = ParseInfer(env);
            SetAssembly(asm);
            _outDir = outDir; _outDirText.Text = outDir;
            await AnalyzeAsync();
            if (_model == null) return "分析失败：" + _status.Text;
            // 把第一个运动关节转到上限的一半，检查预览能动
            var moving = _model.Joints.FirstOrDefault(j => j.Type == JointType.Revolute || j.Type == JointType.Prismatic);
            if (moving != null) { _q[moving] = moving.Upper / 2; _view.SetPose(Q); }
            await ExportAsync();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("状态栏：" + _status.Text);
            sb.AppendLine(UrdfExporter.Describe(_model));
            if (_last != null) foreach (var i in _last.Report.Items) sb.AppendLine($"[{CheckItem.LevelText(i.Level)}] {i.Category}：{i.Text}");
            return sb.ToString();
        }
    }
}
