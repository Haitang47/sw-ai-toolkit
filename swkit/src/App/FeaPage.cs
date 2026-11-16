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
using System.Windows.Threading;
using SolidWorks.Interop.sldworks;
using SwKit.Core;
using SwKit.Core.Fea;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>「应力分析」页面：静力分析（含自适应加密）和拓扑优化。</summary>
    sealed partial class FeaPage
    {
        public FrameworkElement Root { get; }
        public bool IsBusy { get; private set; }

        readonly Window _owner;
        readonly SwWorker _worker;
        readonly FeaView _view = new FeaView();
        readonly FrameworkElement _partBar, _empty, _busy, _work, _selBar, _legend, _resultBar, _guideOverlay;
        readonly TextBlock _partName, _partPath, _status, _busyText, _busyDetail, _selText, _legendMax, _legendMid, _legendMin, _legendTitle, _guideTip;
        readonly StackPanel _side, _guideSteps;
        readonly Button _solve, _refine, _topoBtn, _reportBtn;
        readonly CheckBox _showDeform;
        readonly DispatcherTimer _tick;
        DateTime _busyStart; string _busyPhase;

        string _source, _step;              // 用户给的文件、实际划网格用的 STEP
        Vec3 _bmin, _bmax;
        FeMesh _mesh;
        string _density = "标准";
        FeMaterial _mat = FeMaterial.All.First(m => m.Name == "6061-T6");
        string _matCategory = "铝合金";
        readonly HashSet<int> _fixed = new HashSet<int>();
        readonly List<FeLoad> _loads = new List<FeLoad>();
        readonly HashSet<int> _sel = new HashSet<int>();
        FeResult _result;
        string _refineNote;
        string _matNote;     // 读入零件时关于材料的说明
        string _showMode = "vm";
        CancellationTokenSource _cts;
        FeSamples.Sample _sample;
        bool _capturing;      // 正在为报告截图：不显示视图上的按钮

        // 拓扑优化
        bool _topoMode;
        TopoResult _topo;
        double _topoVf = 0.3;
        string _topoDetail = "标准";
        bool _topoGhost = true;
        int _topoLiveIter;

        static readonly Brush Gray = Ui.Brush("#6E6E73"), Green = Ui.Brush("#248A3D"), Orange = Ui.Brush("#B25000"), Red = Ui.Brush("#D70015"), Text3 = Ui.Brush("#A1A1A6"), Accent = Ui.Brush("#0071E3");

        public FeaPage(Window owner, SwWorker worker)
        {
            _owner = owner;
            _worker = worker;
            Root = (FrameworkElement)Ui.Load("FeaPage.xaml");
            _partBar = Root.Find<FrameworkElement>("PartBar");
            _empty = Root.Find<FrameworkElement>("EmptyState");
            _busy = Root.Find<FrameworkElement>("BusyState");
            _work = Root.Find<FrameworkElement>("WorkState");
            _selBar = Root.Find<FrameworkElement>("SelBar");
            _legend = Root.Find<FrameworkElement>("Legend");
            _resultBar = Root.Find<FrameworkElement>("ResultBar");
            _guideOverlay = Root.Find<FrameworkElement>("GuideOverlay");
            _partName = Root.Find<TextBlock>("PartName");
            _partPath = Root.Find<TextBlock>("PartPath");
            _status = Root.Find<TextBlock>("StatusText");
            _busyText = Root.Find<TextBlock>("BusyText");
            _busyDetail = Root.Find<TextBlock>("BusyDetail");
            _selText = Root.Find<TextBlock>("SelText");
            _legendMax = Root.Find<TextBlock>("LegendMax");
            _legendMid = Root.Find<TextBlock>("LegendMid");
            _legendMin = Root.Find<TextBlock>("LegendMin");
            _legendTitle = Root.Find<TextBlock>("LegendTitle");
            _guideTip = Root.Find<TextBlock>("GuideTip");
            _guideSteps = Root.Find<StackPanel>("GuideSteps");
            _side = Root.Find<StackPanel>("SidePanel");
            _solve = Root.Find<Button>("SolveBtn");
            _refine = Root.Find<Button>("RefineBtn");
            _topoBtn = Root.Find<Button>("TopoBtn");
            _reportBtn = Root.Find<Button>("ReportBtn");
            _reportBtn.Click += async (s, e) => await ReportAsync(null);
            _showDeform = Root.Find<CheckBox>("ShowDeform");
            Root.Find<Border>("LegendBar").Background = FeaView.PaletteBrush(vertical: true);
            Root.Find<Grid>("ViewHost").Children.Add(_view.Element);
            BuildHowSteps();

            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (s, e) => _busyDetail.Text = $"{_busyPhase} · 已用时 {(int)(DateTime.Now - _busyStart).TotalSeconds} 秒";
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            ((RotateTransform)Root.Find<FrameworkElement>("Spinner").RenderTransform).BeginAnimation(RotateTransform.AngleProperty, spin);

            Root.Find<Button>("PickPartBtn").Click += (s, e) => PickPart();
            Root.Find<Button>("ChangePartBtn").Click += (s, e) => PickPart();
            Root.Find<Button>("ResetViewBtn").Click += (s, e) => _view.ResetView();
            Root.Find<Button>("BackToEditBtn").Click += (s, e) => { _topoMode = false; ShowEdit(); };
            Root.Find<Button>("GuideBtn").Click += (s, e) => ShowGuide(true);
            Root.Find<Button>("GuideCloseBtn").Click += (s, e) => ShowGuide(false);
            _guideOverlay.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == _guideOverlay) ShowGuide(false); };
            foreach (var n in new[] { "SampleBracketBtn", "SampleBracketLink" }) Root.Find<Button>(n).Click += async (s, e) => await LoadSampleAsync("bracket");
            foreach (var n in new[] { "SampleTopoBtn", "SampleTopoLink" }) Root.Find<Button>(n).Click += async (s, e) => await LoadSampleAsync("cantilever");
            _showDeform.Checked += (s, e) => ShowResultView();
            _showDeform.Unchecked += (s, e) => ShowResultView();
            foreach (var (name, mode) in new[] { ("ShowVm", "vm"), ("ShowP1", "p1"), ("ShowDisp", "disp"), ("ShowUtil", "util") })
            {
                var rb = Root.Find<RadioButton>(name);
                var md = mode;
                rb.Checked += (s, e) => { _showMode = md; ShowResultView(); };
            }
            _solve.Click += async (s, e) => { if (IsBusy) _cts?.Cancel(); else if (_topoMode) await RunTopoAsync(); else await SolveAsync(); };
            _refine.Click += async (s, e) => await AdaptiveAsync();
            _topoBtn.Click += (s, e) => EnterTopoMode();

            Root.Find<Button>("SelFixBtn").Click += (s, e) => { foreach (var f in _sel) { _fixed.Add(f); foreach (var l in _loads) l.Faces.Remove(f); } _loads.RemoveAll(l => l.Kind != LoadKind.Gravity && l.Faces.Count == 0); ClearSelection(); Changed(); };
            Root.Find<Button>("SelForceBtn").Click += (s, e) => AddLoad(LoadKind.Force);
            Root.Find<Button>("SelPressureBtn").Click += (s, e) => AddLoad(LoadKind.Pressure);
            Root.Find<Button>("SelTorqueBtn").Click += (s, e) => AddLoad(LoadKind.Torque);
            Root.Find<Button>("SelClearBtn").Click += (s, e) => ClearSelection();

            var coarse = Root.Find<RadioButton>("MeshCoarse"); var normal = Root.Find<RadioButton>("MeshNormal"); var fine = Root.Find<RadioButton>("MeshFine");
            normal.IsChecked = true;
            coarse.Checked += async (s, e) => await SetDensity("粗");
            normal.Checked += async (s, e) => await SetDensity("标准");
            fine.Checked += async (s, e) => await SetDensity("细");

            _view.FacePicked += OnFacePicked;
            Root.DragOver += (s, e) => { e.Effects = !IsBusy && PartFrom(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Root.Drop += async (s, e) => { var p = PartFrom(e.Data); if (p != null && !IsBusy) { _sample = null; await LoadPartAsync(p); } };
        }

        static string PartFrom(IDataObject d) =>
            (d.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(f => new[] { ".sldprt", ".step", ".stp" }.Contains(Path.GetExtension(f).ToLowerInvariant()));

        async void PickPart()
        {
            if (IsBusy) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择零件", Filter = "零件|*.sldprt;*.step;*.stp|SolidWorks 零件|*.sldprt|STEP|*.step;*.stp" };
            if (dlg.ShowDialog(_owner) != true) return;
            _sample = null;
            await LoadPartAsync(dlg.FileName);
        }

        void SetBusy(bool busy, string title = null, bool overlay = true)
        {
            IsBusy = busy;
            if (busy)
            {
                _busyStart = DateTime.Now; _busyText.Text = title; _busyPhase = "准备中";
                if (overlay) _tick.Start();
            }
            else _tick.Stop();
            UpdateButtons();
        }

        void Phase(string text) => Root.Dispatcher.BeginInvoke(new Action(() => { _busyPhase = text; _status.Text = text; _status.Foreground = Gray; }));

        void UpdateButtons()
        {
            bool ready = _mesh != null && _fixed.Count > 0 && _loads.Count > 0;
            _solve.IsEnabled = _mesh != null && (IsBusy && _cts != null || !IsBusy && ready);
            Root.Find<TextBlock>("SolveText").Text = IsBusy && _cts != null ? "停止" : _topoMode ? "开始优化" : "开始计算";
            Ui.SetShown(_refine, _result != null && !IsBusy && !_topoMode);
            Ui.SetShown(_topoBtn, ready && !IsBusy && !_topoMode);
            if (_reportBtn != null) Ui.SetShown(_reportBtn, !IsBusy && (_result != null && !_topoMode || _topo != null && _topoMode));
            UpdateGuide();
        }

        // ================= 引导 =================

        void BuildHowSteps()
        {
            var host = Root.Find<UniformGrid>("HowSteps");
            var steps = new[]
            {
                ("1", "拖入零件", "SolidWorks 零件或 STEP。网格先用「标准」。"),
                ("2", "点选面：固定 + 加载荷", "安装面、螺栓孔点「固定」；受力面点「加力 / 压力 / 扭矩」。"),
                ("3", "计算，看安全系数", "安全系数 ≥ 2 一般足够；想更准点「自适应加密」，想减重点「拓扑优化」。"),
            };
            for (int i = 0; i < steps.Length; i++)
            {
                var (n, t, d) = steps[i];
                var sp = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 8, 0, i == steps.Length - 1 ? 0 : 8, 0) };
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Accent, Child = new TextBlock { Text = n, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
                head.Children.Add(new TextBlock { Text = t, FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(head);
                sp.Children.Add(new TextBlock { Text = d, FontSize = 12, Foreground = Gray, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(30, 4, 0, 0) });
                host.Children.Add(new Border { Background = Brushes.White, BorderBrush = Ui.Brush("#E5E5EA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 12), Child = sp, Margin = sp.Margin });
                sp.Margin = new Thickness(0);
            }
        }

        /// <summary>顶部的分步引导：哪一步完成了、现在该做什么。</summary>
        void UpdateGuide()
        {
            if (_guideSteps == null) return;
            _guideSteps.Children.Clear();
            bool hasFix = _fixed.Count > 0, hasLoad = _loads.Count > 0, hasRes = _result != null;
            var labels = _topoMode ? new[] { "材料", "固定", "载荷", "设置保留比例", "优化" } : new[] { "材料", "固定", "载荷", "计算", "看结果" };
            bool[] done = _topoMode
                ? new[] { true, hasFix, hasLoad, _topo != null || IsBusy, _topo != null }
                : new[] { true, hasFix, hasLoad, hasRes, false };
            int current = Array.IndexOf(done, false);
            if (current < 0) current = labels.Length - 1;
            for (int i = 0; i < labels.Length; i++)
            {
                bool isDone = done[i] && i != current, isCur = i == current;
                var circle = new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                    Background = isDone ? Ui.Brush("#34C759") : isCur ? Accent : Ui.Brush("#D8D8DE"),
                    Child = new TextBlock { Text = isDone ? "✓" : (i + 1).ToString(), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0) };
                sp.Children.Add(circle);
                sp.Children.Add(new TextBlock { Text = labels[i], FontSize = 12.5, FontWeight = isCur ? FontWeights.Bold : FontWeights.Normal, Foreground = isCur ? Ui.Brush("#1D1D1F") : Gray, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                if (i < labels.Length - 1) sp.Children.Add(new TextBlock { Text = "›", Foreground = Text3, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                _guideSteps.Children.Add(sp);
            }
            string tip;
            if (_topoMode)
                tip = !hasFix ? "先点选固定面（安装面、螺栓孔），再点「固定」"
                    : !hasLoad ? "点选受力面，加力 / 压力 / 扭矩"
                    : IsBusy ? "正在优化，可以实时看到材料一点点被去掉；可以随时停止"
                    : _topo == null ? "在右侧选保留的材料比例和细节程度，点「开始优化」"
                    : "结果是概念形状：导出 STL，在 SolidWorks 里参考它重新建模，再回来做应力校核";
            else
                tip = !hasFix ? "第 2 步：在左边点选零件被固定的面（安装面、螺栓孔），再点下方「固定」。按住 Ctrl 可以多选"
                    : !hasLoad ? "第 3 步：点选受力的面，点「加力」「加压力」或「加扭矩」，在右侧填大小和方向；重力在右侧添加"
                    : !hasRes ? "第 4 步：检查右侧的材料、约束和载荷，点右下角「开始计算」"
                    : "看安全系数（≥ 2 一般足够）。最大应力在尖角或固定面边缘时点「自适应加密」看是否收敛；想减重点「拓扑优化」";
            _guideTip.Text = tip;
            _guideTip.ToolTip = tip;
        }

        void ShowGuide(bool show)
        {
            if (show)
            {
                var reader = Root.Find<FlowDocumentScrollViewer>("GuideReader");
                if (reader.Document == null) reader.Document = Markdown.Render(GuideText);
            }
            Ui.Fade(_guideOverlay, show, 160);
        }

        const string GuideText =
"## 三步上手\n\n" +
"1. **拖入零件**：SolidWorks 零件（.sldprt，在后台 SolidWorks 里转成 STEP）或 STEP 文件。网格先用「标准」。\n" +
"2. **固定 + 载荷**：在 3D 视图里单击选面（Ctrl 多选），点「固定」；再选受力面，点「加力」「加压力」或「加扭矩」，在右侧填大小和方向。\n" +
"3. **计算**：点「开始计算」，看颜色图、最大应力和安全系数。\n\n" +
"没有零件也可以先点「示例：L 形支架」或「示例：悬臂块拓扑优化」体验一遍。\n\n" +
"## 约束和载荷怎么设才对\n\n" +
"- **固定**选零件真正被固定的面：螺栓孔的孔壁、压紧的安装底面、焊接面。不要把受力面也固定。\n" +
"- **力**是所选面上的**合力**（均匀分布）。「垂直压向面」适合压头、按压；方向按零件坐标（和 SolidWorks 里一样）。\n" +
"- **压力**是单位面积上的力（MPa = N/mm²），适合液压、气压。\n" +
"- **扭矩**加在轴的圆柱面或法兰端面，绕所选面的中心轴。\n" +
"- **重力 / 加速度**作用在整个零件上，加速运动时按加速度是 g 的几倍填。\n" +
"- 约束不够（零件还能整体移动或转动）时求解不收敛，界面会提示。\n\n" +
"## 怎么看结果\n\n" +
"| 显示 | 什么时候看 |\n|---|---|\n" +
"| 等效应力（von Mises） | 钢、铝、钛、铜、多数塑料等**塑性材料**，和屈服强度比 |\n" +
"| 最大主应力 | 铸铁、碳纤维、玻纤、亚克力、光敏树脂等**脆性材料**，拉应力和抗拉强度比 |\n" +
"| 位移 | 刚度够不够、会不会碰到别的零件 |\n" +
"| 强度利用率 | 应力 ÷ 强度（= 1 ÷ 安全系数），超过 100% 的地方会失效 |\n\n" +
"**安全系数参考**：静载、载荷明确取 1.5–2；有冲击、振动或载荷不确定取 2.5–3；3D 打印件取 3 以上（层间强度低）；疲劳问题需要单独做疲劳校核。\n\n" +
"## 结果准不准\n\n" +
"- 求解器是二次四面体单元（和 SolidWorks Simulation 的「高品质」网格同一类），已用拉杆、悬臂梁、带孔板、扭转轴、自重悬臂梁的理论解验证，误差在 0.1%–2% 以内。\n" +
"- 每次计算都给出**估计误差**（Zienkiewicz–Zhu 误差估计，能量范数）：3% 以内很好，8% 以内可以接受，再大建议点「自适应加密」。\n" +
"- **自适应加密**只在误差大的地方加密网格并重算，直到估计误差低于 3%、离开约束面的最大应力变化小于 3%。\n" +
"- **尖角**（没有圆角的内角）和**固定面边缘**的应力在数学上是无穷大，会随网格加密一直升高，这不是真实应力：看「离开约束面后的最大应力」，或者给尖角加上真实的圆角再算。\n\n" +
"## 拓扑优化\n\n" +
"在当前的约束和载荷下，只保留一部分材料（例如 30%），让零件尽量刚。方法是 SIMP 密度法加密度过滤和 Heaviside 投影（学术界和商业软件最常用的方法，参考 Sigmund 的 99 行 / 88 行程序）。\n\n" +
"1. 先设好固定面和载荷（和应力分析一样）。\n" +
"2. 点「拓扑优化…」，选保留比例（30%–50% 常用）和细节程度。\n" +
"3. 点「开始优化」，可以实时看到材料一点点被去掉，大约 1 分钟。\n" +
"4. 结果是**概念形状**：导出 STL，在 SolidWorks 里作为参考重新建模（保留这些梁和筋、去掉空的地方），再回来做应力校核。\n\n" +
"固定面和受载面附近的材料会保持实体，不会被去掉。\n\n" +
"## 适用范围\n\n" +
"线弹性、小变形、静力；单个零件。不包括接触、螺栓预紧、塑性、大变形、屈曲、振动和疲劳。复合材料按各向同性等效，只能粗略估计。\n";

        // ================= 读入零件、划网格 =================

        async Task LoadSampleAsync(string id)
        {
            if (IsBusy) return;
            var s = FeSamples.All.First(x => x.Id == id);
            var path = FeSamples.PathOf(s);
            if (path == null || !File.Exists(path)) { _status.Text = "找不到示例文件（应该在程序旁边的「示例」文件夹里）"; _status.Foreground = Red; return; }
            _sample = s;
            _mat = FeMaterial.All.First(m => m.Name == s.Material); _matCategory = _mat.Category;
            await LoadPartAsync(path);
            if (_mesh == null) return;
            FeSamples.Setup(s, _mesh, _tmpFixed, _loads);
            _fixed.Clear(); foreach (var f in _tmpFixed) _fixed.Add(f);
            _matNote = null;
            Changed();
            _status.Text = "示例：" + s.Description;
            if (s.ForTopology) EnterTopoMode();
        }
        readonly List<int> _tmpFixed = new List<int>();

        public async Task LoadPartAsync(string path)
        {
            _source = Path.GetFullPath(path);
            _partName.Text = _sample != null ? "示例：" + _sample.Name : Path.GetFileName(_source);
            _partPath.Text = _sample != null ? _sample.Description : Path.GetDirectoryName(_source);
            Ui.Fade(_partBar, true);
            _fixed.Clear(); _loads.Clear(); _sel.Clear(); _result = null; _refineNote = null; _mesh = null; _topo = null; _topoMode = false;
            _matNote = null;
            Ui.Fade(_empty, false); Ui.Fade(_work, false); Ui.Fade(_busy, true);
            SetBusy(true, "正在准备零件");
            try
            {
                if (Path.GetExtension(_source).ToLowerInvariant() == ".sldprt")
                {
                    string step, material;
                    if (StepCache.TryGet(_source, out var cached, out var cachedMat))
                    {
                        // 这个零件转换过、之后没改过：直接用缓存的 STEP，不用启动后台 SolidWorks
                        step = cached; material = cachedMat;
                    }
                    else
                    {
                        (step, material) = await _worker.Run(() =>
                        {
                            var s = _worker.Acquire(m => Phase(m.Contains("启动") ? "正在启动后台 SolidWorks（第一次约 20–60 秒）" : m), CancellationToken.None);
                            try { Phase("正在把零件转成 STEP"); return ToStep(s.App, _source); }
                            finally { _worker.Release(); }
                        });
                        step = StepCache.Put(_source, step, material);
                    }
                    _step = step;
                    var guess = GuessMaterial(material);
                    if (guess != null) { _mat = guess; _matCategory = guess.Category; _matNote = $"零件材料是「{material}」，按 {guess.Name} 计算。"; }
                    else _matNote = string.IsNullOrWhiteSpace(material) ? $"零件没有设置材料，先按 {_mat.Name} 计算，可以在右侧更改。" : $"零件材料「{material}」不在材料表里，先按 {_mat.Name} 计算，可以在右侧更改或用自定义材料。";
                }
                else if (_source.Any(c => c > 127))
                {
                    // 路径里有中文时复制到临时文件夹，避免外部程序处理非 ASCII 路径出问题
                    var dir = Path.Combine(Path.GetTempPath(), "swkit_fea_parts");
                    Directory.CreateDirectory(dir);
                    _step = Path.Combine(dir, Guid.NewGuid().ToString("N") + Path.GetExtension(_source));
                    File.Copy(_source, _step, true);
                }
                else _step = _source;

                Phase("正在读取几何");
                await Task.Run(() => Gmsh.Bounds(_step, out _bmin, out _bmax));
                await RemeshAsync();
            }
            catch (Exception ex)
            {
                SetBusy(false);
                Ui.Fade(_busy, false); Ui.Fade(_mesh == null ? _empty : _work, true);
                _status.Text = "读取零件失败：" + ex.Message; _status.Foreground = Red;
            }
        }

        /// <summary>在后台 SolidWorks 里只读打开零件，导出 STEP，读材料名。</summary>
        static (string step, string material) ToStep(ISldWorks app, string path)
        {
            int e = 0, w = 0;
            var doc = app.GetOpenDocumentByName(path) as ModelDoc2 ?? app.OpenDoc6(path, 1, 1 | 2, "", ref e, ref w);
            if (doc == null) throw new InvalidOperationException("打开零件失败：" + ExportRunner.LoadErrorText(e));
            try
            {
                string mat = null;
                try { mat = ((PartDoc)doc).GetMaterialPropertyName2("", out _); } catch { }
                var dir = Path.Combine(Path.GetTempPath(), "swkit_fea_parts");
                Directory.CreateDirectory(dir);
                var step = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".step");
                app.ActivateDoc3(doc.GetTitle(), false, 1, ref e);
                doc.Extension.SaveAs(step, 0, 1, null, ref e, ref w);
                if (!File.Exists(step)) throw new InvalidOperationException("导出 STEP 失败：" + ExportRunner.SaveErrorText(e));
                return (step, mat);
            }
            finally { try { app.CloseDoc(doc.GetTitle()); } catch { } }
        }

        static FeMaterial GuessMaterial(string swName)
        {
            if (string.IsNullOrWhiteSpace(swName)) return null;
            var s = swName.ToUpperInvariant();
            string pick =
                s.Contains("7075") ? "7075-T6" : s.Contains("6063") ? "6063-T5" : s.Contains("5052") ? "5052-H32" : s.Contains("2024") || s.Contains("2A12") ? "2A12-T4" :
                s.Contains("6061") || s.Contains("铝") || s.Contains("ALUMIN") ? "6061-T6" :
                s.Contains("TI-6") || s.Contains("TC4") || s.Contains("TI6") ? "TC4 (Ti-6Al-4V)" : s.Contains("钛") || s.Contains("TITAN") ? "TA2 纯钛" :
                s.Contains("316") ? "316 不锈钢" : s.Contains("304") || s.Contains("不锈") || s.Contains("STAINLESS") ? "304 不锈钢" :
                s.Contains("黄铜") || s.Contains("BRASS") ? "黄铜 H62" : s.Contains("青铜") || s.Contains("BRONZE") ? "锡青铜 QSn6.5-0.1" :
                s.Contains("铸铁") || s.Contains("CAST IRON") || s.Contains("GRAY") || s.Contains("HT") && s.Contains("200") ? "灰铸铁 HT200" :
                s.Contains("40CR") ? "40Cr 调质" : s.Contains("65MN") ? "65Mn 弹簧钢" :
                s.Contains("45") || s.Contains("1045") ? "45 钢" : s.Contains("Q345") || s.Contains("Q355") ? "Q355" :
                s.Contains("钢") || s.Contains("STEEL") || s.Contains("1020") || s.Contains("Q235") ? "Q235" :
                s.Contains("碳纤") || s.Contains("CARBON") ? "碳纤维板（3K 斜纹）" : s.Contains("FR4") || s.Contains("G10") || s.Contains("玻纤") || s.Contains("GLASS") ? "玻璃纤维板 FR4/G10" :
                s.Contains("POM") || s.Contains("ACETAL") || s.Contains("DELRIN") ? "POM 赛钢" : s.Contains("PEEK") ? "PEEK" :
                s.Contains("PMMA") || s.Contains("ACRYLIC") || s.Contains("亚克力") ? "亚克力 PMMA" : s.Contains("PC") && !s.Contains("PCB") ? "PC 聚碳酸酯" :
                s.Contains("PLA") ? "PLA" : s.Contains("PETG") ? "PETG" : s.Contains("ABS") ? "ABS" : s.Contains("树脂") || s.Contains("RESIN") ? "光敏树脂（标准）" :
                s.Contains("尼龙") || s.Contains("NYLON") || s.Contains("PA") ? "MC 尼龙 PA6" : null;
            return pick == null ? null : FeMaterial.All.FirstOrDefault(m => m.Name == pick);
        }

        double MeshSize()
        {
            var diag = (_bmax - _bmin).Length;
            return diag / (_density == "粗" ? 14 : _density == "细" ? 38 : 24);
        }

        async Task SetDensity(string d)
        {
            if (_density == d) return;
            _density = d;
            if (_mesh != null && !IsBusy)
            {
                Ui.Fade(_work, false); Ui.Fade(_busy, true);
                SetBusy(true, "正在重新划分网格");
                _result = null; _topo = null;
                await RemeshAsync();
            }
        }

        async Task RemeshAsync()
        {
            try
            {
                Phase("正在划分网格（Gmsh）");
                var size = MeshSize();
                var mesh = await Task.Run(() => Gmsh.Mesh(_step, size, Phase));
                // 约束和载荷按面编号保留（同一个 STEP，面编号不变）
                var ids = new HashSet<int>(mesh.Faces.Select(f => f.Id));
                _fixed.RemoveWhere(f => !ids.Contains(f));
                foreach (var l in _loads) l.Faces.RemoveAll(f => !ids.Contains(f));
                _mesh = mesh;
                _view.LoadMesh(mesh);
                SetBusy(false);
                Ui.Fade(_busy, false); Ui.Fade(_work, true);
                ShowEdit();
                _status.Text = $"网格：{mesh.Elements:N0} 个二次四面体单元、{mesh.Nodes:N0} 个节点、{mesh.Faces.Count} 个面。" + (_matNote ?? "");
                _status.Foreground = Gray;
            }
            catch (Exception ex)
            {
                SetBusy(false);
                Ui.Fade(_busy, false); Ui.Fade(_mesh == null ? _empty : _work, true);
                _status.Text = "网格划分失败：" + ex.Message; _status.Foreground = Red;
            }
        }

        // ================= 选面、约束、载荷 =================

        void OnFacePicked(int face, bool ctrl)
        {
            if (_legend.Visibility == Visibility.Visible || _topo != null && _topoMode || IsBusy) return;   // 看结果时不选面
            if (face < 0) { if (!ctrl) ClearSelection(); return; }
            if (ctrl) { if (!_sel.Add(face)) _sel.Remove(face); }
            else if (_sel.Count == 1 && _sel.Contains(face)) _sel.Clear();
            else { _sel.Clear(); _sel.Add(face); }
            RefreshSelection();
        }

        void ClearSelection() { _sel.Clear(); RefreshSelection(); }

        void RefreshSelection()
        {
            if (_mesh == null) return;
            _view.SetFaceColors(FaceColor);
            if (_sel.Count == 0) { Ui.Fade(_selBar, false); return; }
            var area = _mesh.Faces.Where(f => _sel.Contains(f.Id)).Sum(f => f.Area);
            _selText.Text = $"已选 {_sel.Count} 个面（{area * 1e6:0.#} mm²）";
            Ui.Fade(_selBar, true);
        }

        Color FaceColor(int id) =>
            _sel.Contains(id) ? FeaView.CSelected : _fixed.Contains(id) ? FeaView.CFixed : _loads.Any(l => l.Faces.Contains(id)) ? FeaView.CLoad : FeaView.CNormal;

        void AddLoad(LoadKind kind) => AddLoad(kind, _sel.ToList(), null, null, false);

        FeLoad AddLoad(LoadKind kind, List<int> faces, double? value, Vec3? dir, bool alongNormal)
        {
            var load = new FeLoad { Kind = kind, Faces = faces };
            switch (kind)
            {
                case LoadKind.Force: load.Value = 100; load.Dir = new Vec3(0, -1, 0); break;
                case LoadKind.Pressure: load.Value = 1e6; break;
                case LoadKind.Torque: load.Value = 1; load.Dir = GuessAxis(faces); break;
                case LoadKind.Gravity: load.Value = 1; load.Dir = new Vec3(0, -1, 0); break;
            }
            if (value != null) load.Value = value.Value;
            if (dir != null) load.Dir = dir.Value;
            load.AlongNormal = alongNormal;
            foreach (var f in faces) _fixed.Remove(f);
            _loads.Add(load);
            ClearSelection();
            Changed();
            return load;
        }

        /// <summary>扭矩的轴：平面取法向；曲面（例如轴的圆柱面）取与各处法向最垂直的坐标轴。</summary>
        Vec3 GuessAxis(List<int> faces)
        {
            var fs = _mesh.Faces.Where(f => faces.Contains(f.Id)).ToList();
            if (fs.Count == 0) return new Vec3(0, 1, 0);
            if (fs.Count == 1 && fs[0].IsPlane) return Axis(fs[0].Normal);
            var axes = new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) };
            return axes.OrderBy(a => fs.SelectMany(f => f.Tris).Sum(t => { var n = _mesh.TriNormal(t, out var ar); return n.Dot(a) * n.Dot(a) * ar; })).First();
        }

        static Vec3 Axis(Vec3 n)
        {
            var a = new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) }.OrderByDescending(v => Math.Abs(v.Dot(n))).First();
            return Math.Abs(a.Dot(n)) > 0.999 ? a * Math.Sign(a.Dot(n)) : n;
        }

        void Changed()
        {
            if (_result != null || _topo != null) { _result = null; _topo = null; _refineNote = null; ShowEdit(); }
            else { if (_mesh != null) _view.SetFaceColors(FaceColor); BuildSide(); }
            UpdateButtons();
            if (_fixed.Count == 0) _status.Text = "点选要固定的面（例如螺栓孔、安装面），再点「固定」";
            else if (_loads.Count == 0) _status.Text = "点选受力的面，再点「加力」「加压力」或「加扭矩」；整体的重力在右侧添加";
            else _status.Text = _topoMode ? "设置好后点「开始优化」" : "设置好后点「开始计算」";
            _status.Foreground = Gray;
        }

        void ShowEdit()
        {
            _legend.Visibility = Visibility.Collapsed;
            Ui.SetShown(_resultBar, false);
            Root.Find<TextBlock>("ViewHint").Text = "单击选面，按住 Ctrl 多选 · 左键拖动旋转 · 滚轮缩放 · 右键拖动平移";
            if (_mesh != null) _view.ShowFaces(FaceColor);
            BuildSide();
            UpdateButtons();
        }

        // ================= 右侧面板 =================

        void Section(string t) => _side.Children.Add(new TextBlock { Text = t, Style = (Style)Application.Current.FindResource("T.Section") });
        static TextBlock Caption(string text, Brush color, Thickness margin) => new TextBlock { Text = text, FontSize = 12, Foreground = color, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = margin };
        Border Group(UIElement child) => new Border { Style = (Style)Application.Current.FindResource("Group"), Child = child };
        static RadioButton Pill(string text, string group, bool on) => new RadioButton { Style = (Style)Application.Current.FindResource("Pill"), GroupName = group, Content = text, IsChecked = on };

        void BuildSide()
        {
            _side.Children.Clear();
            if (_mesh == null) return;
            if (_topoMode) AddTopoCard();
            else if (_result != null) AddResultCards();
            AddMaterialCard();

            // 约束
            Section("固定");
            var fx = new StackPanel();
            if (_fixed.Count == 0) fx.Children.Add(Caption("还没有固定面。在左边点选螺栓孔、安装面等，再点「固定」。", Text3, new Thickness(0)));
            else
            {
                var row = new DockPanel();
                var del = SmallButton("清除", () => { _fixed.Clear(); Changed(); });
                DockPanel.SetDock(del, Dock.Right); row.Children.Add(del);
                row.Children.Add(new TextBlock { Text = $"{_fixed.Count} 个面（{_mesh.Faces.Where(f => _fixed.Contains(f.Id)).Sum(f => f.Area) * 1e6:0.#} mm²）", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(FeaView.CFixed) });
                fx.Children.Add(row);
                fx.Children.Add(Caption("固定面上各方向位移都为 0。真实的螺栓连接没有这么刚硬，固定面边缘的应力通常偏高。", Text3, new Thickness(0, 4, 0, 0)));
            }
            _side.Children.Add(Group(fx));

            // 载荷
            Section("载荷");
            foreach (var l in _loads) _side.Children.Add(LoadCard(l));
            var addG = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "+ 加重力 / 加速度", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 14) };
            addG.Click += (s, e) => AddLoad(LoadKind.Gravity, new List<int>(), null, null, false);
            if (_loads.Count == 0) _side.Children.Add(Caption("在左边点选受力的面，再点「加力」「加压力」或「加扭矩」。", Text3, new Thickness(4, 0, 0, 8)));
            _side.Children.Add(addG);
        }

        void AddMaterialCard()
        {
            Section("材料");
            var ms = new StackPanel();
            var cats = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            var gc = "cat" + Guid.NewGuid().ToString("N");
            foreach (var c in FeMaterial.Categories.Concat(new[] { "自定义" }))
            {
                var rb = Pill(c, gc, c == _matCategory);
                rb.FontSize = 11.5;
                var cc = c;
                rb.Checked += (s, e) => { _matCategory = cc; if (cc != "自定义" && _mat.Category != cc) _mat = FeMaterial.All.First(m => m.Category == cc); Root.Dispatcher.BeginInvoke(new Action(() => { if (_result != null || _topo != null) Changed(); else BuildSide(); })); };
                cats.Children.Add(rb);
            }
            ms.Children.Add(cats);
            ms.Children.Add(new Border { Height = 1, Background = Ui.Brush("#EDEDF0"), Margin = new Thickness(0, 2, 0, 8) });
            if (_matCategory == "自定义")
            {
                ms.Children.Add(CustomMaterialEditor());
            }
            else
            {
                var wrap = new WrapPanel();
                var g = "mat" + Guid.NewGuid().ToString("N");
                foreach (var m in FeMaterial.All.Where(m => m.Category == _matCategory))
                {
                    var rb = Pill(m.Name, g, m == _mat);
                    var mm = m;
                    rb.Checked += (s, e) => { if (_mat == mm) return; _mat = mm; Root.Dispatcher.BeginInvoke(new Action(Changed)); };
                    wrap.Children.Add(rb);
                }
                ms.Children.Add(wrap);
            }
            ms.Children.Add(Caption(MaterialLine(_mat), Gray, new Thickness(0, 6, 0, 0)));
            _side.Children.Add(Group(ms));
        }

        static string MaterialLine(FeMaterial m) =>
            $"{m.Name}：弹性模量 {m.E / 1e9:0.#} GPa · 泊松比 {m.Nu:0.00} · {(m.Brittle ? "抗拉强度" : "屈服强度")} {m.Strength / 1e6:0} MPa · 密度 {m.Density:0} kg/m³" +
            (m.Brittle ? " · 脆性，按主应力判断" : "") + (m.Note != null ? " · " + m.Note : "");

        FrameworkElement CustomMaterialEditor()
        {
            var cur = _mat.Custom ? _mat : FeMaterial.CreateCustom(_mat.E / 1e9, _mat.Nu, _mat.Strength / 1e6, _mat.Density, _mat.Brittle);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var fields = new List<(string label, string unit, TextBox box)>();
            void Row(string label, double v, string unit)
            {
                int r = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var tb = new TextBlock { Text = label, FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
                Grid.SetRow(tb, r); grid.Children.Add(tb);
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                var box = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Width = 90, Text = v.ToString("0.###", CultureInfo.InvariantCulture), TextAlignment = TextAlignment.Right };
                sp.Children.Add(box);
                sp.Children.Add(new TextBlock { Text = unit, FontSize = 12, Foreground = Gray, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                Grid.SetRow(sp, r); Grid.SetColumn(sp, 1); grid.Children.Add(sp);
                fields.Add((label, unit, box));
            }
            Row("弹性模量", cur.E / 1e9, "GPa");
            Row("泊松比", cur.Nu, "");
            Row("强度", cur.Strength / 1e6, "MPa（塑性填屈服，脆性填抗拉）");
            Row("密度", cur.Density, "kg/m³");
            var sp2 = new StackPanel();
            sp2.Children.Add(grid);
            var brittle = new CheckBox { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = cur.Brittle, Margin = new Thickness(0, 6, 0, 0), Content = new TextBlock { Text = "脆性材料（按主应力判断）", FontSize = 12.5, Foreground = Gray } };
            sp2.Children.Add(brittle);
            var apply = new Button { Style = (Style)Application.Current.FindResource("Btn.Secondary"), Content = "使用这个材料", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            apply.Click += (s, e) =>
            {
                var v = new double[4];
                for (int i = 0; i < 4; i++)
                    if (!double.TryParse(fields[i].box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || v[i] <= 0) { _status.Text = $"「{fields[i].label}」要填正数"; _status.Foreground = Red; return; }
                if (v[1] >= 0.5) { _status.Text = "泊松比要小于 0.5"; _status.Foreground = Red; return; }
                _mat = FeMaterial.CreateCustom(v[0], v[1], v[2], v[3], brittle.IsChecked == true);
                Changed();
            };
            sp2.Children.Add(apply);
            return sp2;
        }

        Button SmallButton(string text, Action onClick)
        {
            var b = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            b.Click += (s, e) => onClick();
            return b;
        }

        FrameworkElement LoadCard(FeLoad l)
        {
            var sp = new StackPanel();
            var head = new DockPanel();
            var del = SmallButton("删除", () => { _loads.Remove(l); Changed(); });
            DockPanel.SetDock(del, Dock.Right); head.Children.Add(del);
            var title = l.Kind == LoadKind.Force ? "力" : l.Kind == LoadKind.Pressure ? "压力" : l.Kind == LoadKind.Torque ? "扭矩" : "重力 / 加速度";
            var where = l.Kind == LoadKind.Gravity ? "整个零件" : $"{l.Faces.Count} 个面";
            var tb = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            tb.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(l.Kind == LoadKind.Gravity ? Color.FromRgb(0x6E, 0x6E, 0x73) : FeaView.CLoad) });
            tb.Inlines.Add(new System.Windows.Documents.Run("   " + where) { Foreground = Gray, FontSize = 12 });
            head.Children.Add(tb);
            sp.Children.Add(head);

            string unit; double toUi;
            switch (l.Kind)
            {
                case LoadKind.Force: unit = "N"; toUi = 1; break;
                case LoadKind.Pressure: unit = "MPa"; toUi = 1e-6; break;
                case LoadKind.Torque: unit = "N·m"; toUi = 1; break;
                default: unit = "× g"; toUi = 1; break;
            }
            var box = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Width = 90, Text = (l.Value * toUi).ToString("0.###", CultureInfo.InvariantCulture), TextAlignment = TextAlignment.Right };
            void Commit()
            {
                if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                {
                    if (Math.Abs(v / toUi - l.Value) > 1e-12) { l.Value = v / toUi; Changed(); }
                }
                else box.Text = (l.Value * toUi).ToString("0.###", CultureInfo.InvariantCulture);
            }
            box.LostFocus += (s, e) => Commit();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) Commit(); };
            var valRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            valRow.Children.Add(new TextBlock { Text = l.Kind == LoadKind.Pressure ? "压强" : l.Kind == LoadKind.Torque ? "大小" : l.Kind == LoadKind.Gravity ? "倍数" : "合力", Width = 44, FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
            valRow.Children.Add(box);
            valRow.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(8, 0, 0, 0), FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(valRow);

            if (l.Kind != LoadKind.Pressure)
            {
                var options = new List<(string label, Vec3 dir, bool normal)>
                {
                    ("+X", new Vec3(1, 0, 0), false), ("−X", new Vec3(-1, 0, 0), false), ("+Y", new Vec3(0, 1, 0), false),
                    ("−Y", new Vec3(0, -1, 0), false), ("+Z", new Vec3(0, 0, 1), false), ("−Z", new Vec3(0, 0, -1), false),
                };
                if (l.Kind == LoadKind.Force) options.Add(("垂直压向面", Vec3.Zero, true));
                var w = new WrapPanel { Margin = new Thickness(44, 8, 0, 0) };
                var g = "dir" + Guid.NewGuid().ToString("N");
                foreach (var (label, dir, normal) in options)
                {
                    var selected = normal ? l.AlongNormal : !l.AlongNormal && (l.Dir - dir).Length < 1e-6;
                    var rb = Pill(label, g, selected);
                    rb.Checked += (s, e) => { l.AlongNormal = normal; if (!normal) l.Dir = dir; Root.Dispatcher.BeginInvoke(new Action(Changed)); };
                    w.Children.Add(rb);
                }
                if (!l.AlongNormal && !options.Any(o => !o.normal && (o.dir - l.Dir).Length < 1e-6))
                    w.Children.Add(Pill(l.Kind == LoadKind.Torque ? "面的法向" : $"({l.Dir.X:0.##}, {l.Dir.Y:0.##}, {l.Dir.Z:0.##})", g, true));
                var dirRow = new Grid();
                dirRow.Children.Add(new TextBlock { Text = l.Kind == LoadKind.Torque ? "轴向" : "方向", FontSize = 12.5, Foreground = Gray, Margin = new Thickness(0, 12, 0, 0) });
                dirRow.Children.Add(w);
                sp.Children.Add(dirRow);
            }
            if (l.Kind == LoadKind.Torque) sp.Children.Add(Caption("绕所选面形心、沿上面方向的轴（右手定则），按切向分布到面上。", Text3, new Thickness(0, 2, 0, 0)));
            if (l.Kind == LoadKind.Gravity) sp.Children.Add(Caption($"零件自重约 {_mat.Density * 9.80665 * _mesh.Volume() * l.Value:0.###} N。加速运动时，惯性力可以按加速度的倍数填。", Text3, new Thickness(0, 2, 0, 0)));
            var card = Group(sp);
            card.Margin = new Thickness(0, 0, 0, 10);
            card.MouseEnter += (s, e) => { if (_result == null && _topo == null && l.Faces.Count > 0) _view.SetFaceColors(id => l.Faces.Contains(id) ? FeaView.CHover : FaceColor(id)); };
            card.MouseLeave += (s, e) => { if (_result == null && _topo == null) _view.SetFaceColors(FaceColor); };
            return card;
        }

        // ================= 计算 =================

        FeModel Model(FeMesh mesh) => new FeModel { Mesh = mesh, Material = _mat, FixedFaces = _fixed.ToList(), Loads = _loads.Select(l => l.Clone()).ToList() };

        public async Task SolveAsync()
        {
            if (IsBusy || _mesh == null || _fixed.Count == 0 || _loads.Count == 0) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetBusy(true, "正在计算");
            Ui.Fade(_work, false); Ui.Fade(_busy, true);
            using (HeavyWork.Begin())
            try
            {
                var model = Model(_mesh);
                var r = await Task.Run(() => FeSolver.Solve(model, Phase, ct), ct);
                _result = r; _refineNote = null;
                FinishSolve();
            }
            catch (OperationCanceledException) { Stopped(); }
            catch (Exception ex) { Failed("计算失败：" + ex.Message); }
            finally { _cts = null; UpdateButtons(); }
        }

        /// <summary>自适应加密：按误差分布加密网格重算，直到收敛（最多 3 轮）。</summary>
        public async Task AdaptiveAsync()
        {
            if (IsBusy || _mesh == null || _result == null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetBusy(true, "正在自适应加密");
            Ui.Fade(_work, false); Ui.Fade(_busy, true);
            using (HeavyWork.Begin())
            try
            {
                var model = Model(_mesh);
                var first = _result;
                var step = _step; var size = MeshSize();
                var (mesh, r) = await Task.Run(() => FeAdaptive.Run(model, first, step, size, 0.03, 3, 150000, Phase,
                    (m, res, k) => Phase($"第 {k} 轮完成：{m.Elements:N0} 个单元，最大应力 {res.MaxVm / 1e6:0.##} MPa，估计误差 {res.ErrorEstimate:P1}"), ct), ct);
                var p = r.Passes;
                var a = p[0]; var b = p[p.Count - 1];
                double chAway = a.MaxVmAway > 0 ? (b.MaxVmAway - a.MaxVmAway) / a.MaxVmAway : 0;
                double chMax = a.MaxVm > 0 ? (b.MaxVm - a.MaxVm) / a.MaxVm : 0;
                double lastStep = p.Count >= 2 && p[p.Count - 2].MaxVmAway > 0 ? Math.Abs(b.MaxVmAway - p[p.Count - 2].MaxVmAway) / p[p.Count - 2].MaxVmAway : 1;
                _refineNote = b.Eta <= 0.03 && lastStep < 0.03
                    ? $"已收敛：估计误差 {b.Eta:P1}，最后一轮离开约束面的最大应力只变化 {lastStep:P1}。"
                    : $"估计误差 {b.Eta:P1}，最后一轮离开约束面的最大应力变化 {lastStep:P1}，还没有完全收敛。";
                if (Math.Abs(chMax) > 0.1 && Math.Abs(chMax) > 2 * Math.Abs(chAway))
                    _refineNote += $" 全局最大应力随加密变化 {chMax:+0%;-0%}，而离开约束面后只变化 {chAway:+0%;-0%}：最大值出现在尖角或固定面边缘的奇异点，参考离开约束面后的数值，或给尖角加上真实的圆角。";
                _mesh = mesh;
                _view.LoadMesh(mesh);
                _result = r;
                FinishSolve();
            }
            catch (OperationCanceledException) { Stopped(); }
            catch (Exception ex) { Failed("自适应加密失败：" + ex.Message); }
            finally { _cts = null; UpdateButtons(); }
        }

        void FinishSolve()
        {
            var r = _result;
            SetBusy(false);
            Ui.Fade(_busy, false); Ui.Fade(_work, true);
            _showDeform.IsChecked = false;
            var defMode = _mat.Brittle ? "ShowP1" : "ShowVm";
            var rb = Root.Find<RadioButton>(defMode);
            if (rb.IsChecked == true) ShowResultView(); else rb.IsChecked = true;
            BuildSide();
            var sf = r.SafetyFactor;
            _status.Text = (_mat.Brittle ? $"最大主应力 {r.MaxP1 / 1e6:0.##} MPa" : $"最大等效应力 {r.MaxVm / 1e6:0.##} MPa") +
                           $"，安全系数 {Sf(sf)}，最大位移 {r.MaxDisp * 1000:0.###} mm，估计误差 {r.ErrorEstimate:P1}（{r.Seconds:0.0} 秒）";
            _status.Foreground = sf >= 2 ? Green : sf >= 1 ? Orange : Red;
        }

        void Stopped()
        {
            SetBusy(false);
            Ui.Fade(_busy, false); Ui.Fade(_work, true);
            _status.Text = "已停止"; _status.Foreground = Gray;
        }

        void Failed(string text)
        {
            SetBusy(false);
            Ui.Fade(_busy, false); Ui.Fade(_work, true);
            _status.Text = text; _status.Foreground = Red;
        }

        static string Sf(double sf) => double.IsInfinity(sf) ? "∞" : sf.ToString(sf < 10 ? "0.00" : "0.#", CultureInfo.InvariantCulture);

        void ShowResultView()
        {
            if (_result == null || _topoMode) return;
            ClearSelection();
            var r = _result;
            int nn = _mesh.Nodes;
            double[] values; double max; int mark; string title; Func<double, string> fmt;
            switch (_showMode)
            {
                case "p1":
                    values = r.Principal1.Select(v => Math.Max(0, v)).ToArray(); max = NiceMax(Math.Max(r.MaxP1, 1)); mark = r.MaxP1Node;
                    title = "最大主应力（拉）MPa"; fmt = v => (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture); break;
                case "disp":
                    values = new double[nn];
                    for (int n = 0; n < nn; n++) values[n] = Math.Sqrt(r.U[3 * n] * r.U[3 * n] + r.U[3 * n + 1] * r.U[3 * n + 1] + r.U[3 * n + 2] * r.U[3 * n + 2]);
                    max = NiceMax(Math.Max(r.MaxDisp, 1e-12)); mark = r.MaxDispNode;
                    title = "位移 mm"; fmt = v => (v * 1000).ToString("0.####", CultureInfo.InvariantCulture); break;
                case "util":
                    values = new double[nn];
                    double ut = _mat.Strength, uc = _mat.CompStrength;
                    for (int n = 0; n < nn; n++)
                        values[n] = _mat.Brittle ? Math.Max(Math.Max(0, r.Principal1[n]) / ut, Math.Max(0, -r.Principal3[n]) / uc) : r.VonMises[n] / ut;
                    max = 1; mark = r.SafetyNode;
                    title = "强度利用率（≥100% 失效）"; fmt = v => (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%"; break;
                default:
                    values = r.VonMises; max = NiceMax(r.MaxVm); mark = r.MaxVmNode;
                    title = "等效应力 MPa"; fmt = v => (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture); break;
            }
            var scale = 0.0;
            if (_showDeform.IsChecked == true && r.MaxDisp > 0)
            {
                scale = 0.06 * (_bmax - _bmin).Length / r.MaxDisp;   // 最大位移放大到零件尺寸的 6%
                Root.Find<TextBlock>("DeformText").Text = $"显示变形（放大 {scale:0} 倍）";
            }
            else Root.Find<TextBlock>("DeformText").Text = "显示变形";
            _view.ShowResult(values, max, r.U, scale, mark);
            _legendTitle.Text = title;
            _legendMax.Text = _showMode == "util" ? "≥100%" : fmt(max);
            _legendMid.Text = fmt(max / 2);
            _legendMin.Text = _showMode == "util" ? "0%" : "0";
            _legend.Visibility = Visibility.Visible;
            if (!_capturing) Ui.SetShown(_resultBar, true);
            Root.Find<TextBlock>("ViewHint").Text = "黑点是最大值的位置 · 左键拖动旋转 · 滚轮缩放";
        }

        static double NiceMax(double v)
        {
            if (v <= 0) return 1;
            var e = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (var k in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }) if (k * e >= v) return k * e;
            return 10 * e;
        }

        void AddResultCards()
        {
            var r = _result;
            Section("结果");
            var grid = new UniformGrid { Columns = 3 };
            if (_mat.Brittle) grid.Children.Add(Metric("最大主应力", $"{r.MaxP1 / 1e6:0.##}", "MPa（拉）", null));
            else grid.Children.Add(Metric("最大等效应力", $"{r.MaxVm / 1e6:0.##}", "MPa", null));
            var sf = r.SafetyFactor;
            grid.Children.Add(Metric("安全系数", Sf(sf), $"{(_mat.Brittle ? "抗拉" : "屈服")} {_mat.Strength / 1e6:0} MPa", sf >= 2 ? Green : sf >= 1 ? Orange : Red));
            grid.Children.Add(Metric("最大位移", $"{r.MaxDisp * 1000:0.###}", "mm", null));
            var sp = new StackPanel();
            sp.Children.Add(grid);
            string verdict = sf >= 2 ? "强度足够。" : sf >= 1.2 ? "不会失效，但余量不大，受冲击、疲劳或 3D 打印件建议加强。" : sf >= 1 ? "接近失效，建议加强。" :
                (_mat.Brittle ? "最大拉应力超过抗拉强度，零件会断裂，需要加强。" : "最大应力超过屈服强度，零件会发生永久变形，需要加强（实际最大应力会比线弹性结果略低，但结论不变）。");
            sp.Children.Add(Caption(verdict, sf >= 2 ? Green : sf >= 1 ? Orange : Red, new Thickness(0, 10, 0, 0)));
            if (!_mat.Brittle && r.MaxVmAwayNode >= 0 && r.MaxVmAway < r.MaxVm * 0.77)
                sp.Children.Add(Caption($"离开固定面后的最大应力 {r.MaxVmAway / 1e6:0.##} MPa（安全系数 {Sf(_mat.Strength / r.MaxVmAway)}）", Gray, new Thickness(0, 4, 0, 0)));

            // 可信度检查
            var checks = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            void Check(bool ok, string text) => checks.Children.Add(Caption((ok ? "✓ " : "! ") + text, ok ? Green : Orange, new Thickness(0, 2, 0, 0)));
            Check(r.Converged, r.Converged ? $"求解收敛（{r.Iterations} 次迭代）" : "求解没有完全收敛");
            if (r.Applied.Length > 0) Check(r.EquilibriumError < 1e-3, $"外载 {r.Applied.Length:0.##} N 与支撑反力平衡（误差 {r.EquilibriumError:P3}）");
            Check(r.ErrorEstimate < 0.08, $"估计误差 {r.ErrorEstimate:P1}（{(r.ErrorEstimate < 0.03 ? "很好" : r.ErrorEstimate < 0.08 ? "可以接受" : "偏大，建议自适应加密")}）· 网格 {_mesh.Elements:N0} 个二次四面体单元");
            if (r.Passes.Count > 1)
            {
                checks.Children.Add(new TextBlock { Text = "自适应加密过程", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
                foreach (var p in r.Passes)
                    checks.Children.Add(Caption($"{p.Elements,7:N0} 单元 · 最大 {p.MaxVm / 1e6:0.#} MPa · 离开约束 {p.MaxVmAway / 1e6:0.#} MPa · 误差 {p.Eta:P1}", Gray, new Thickness(0, 1, 0, 0)));
            }
            if (_refineNote != null) checks.Children.Add(Caption(_refineNote, Gray, new Thickness(0, 4, 0, 0)));
            else checks.Children.Add(Caption("点底部「自适应加密」，在应力变化剧烈的地方自动加密网格重算，看结果是否收敛。", Text3, new Thickness(0, 4, 0, 0)));
            foreach (var n in r.Notes) checks.Children.Add(Caption("· " + n, Gray, new Thickness(0, 4, 0, 0)));
            sp.Children.Add(checks);
            _side.Children.Add(Group(sp));
        }

        static FrameworkElement Metric(string label, string value, string unit, Brush color)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = Gray });
            sp.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = color ?? Ui.Brush("#1D1D1F"), Margin = new Thickness(0, 2, 0, 0) });
            sp.Children.Add(new TextBlock { Text = unit, FontSize = 11, Foreground = Gray, TextTrimming = TextTrimming.CharacterEllipsis });
            return sp;
        }

        // ================= 拓扑优化 =================

        void EnterTopoMode()
        {
            if (_mesh == null) return;
            _topoMode = true;
            _topo = null;
            _legend.Visibility = Visibility.Collapsed;
            Ui.SetShown(_resultBar, false);
            _view.ShowFaces(FaceColor);
            BuildSide();
            UpdateButtons();
            _status.Text = _fixed.Count > 0 && _loads.Count > 0 ? "拓扑优化：在右侧设置保留比例，点「开始优化」" : "拓扑优化：先设置固定面和载荷";
            _status.Foreground = Gray;
        }

        void AddTopoCard()
        {
            Section("拓扑优化");
            var sp = new StackPanel();
            if (_topo != null && !IsBusy)
            {
                var grid = new UniformGrid { Columns = 3 };
                grid.Children.Add(Metric("保留材料", $"{_topo.VolumeFraction:P0}", "体积", null));
                grid.Children.Add(Metric("保留刚度", $"{_topo.StiffnessRatio:P0}", "相对实心零件", Accent));
                grid.Children.Add(Metric("减重", $"{_mat.Density * _mesh.Volume() * (1 - _topo.VolumeFraction) * 1000:0.#}", "g", null));
                sp.Children.Add(grid);
                sp.Children.Add(Caption($"{_topo.Iterations} 轮迭代，用时 {_topo.Seconds:0} 秒。灰色是优化后的形状，浅色是原来的外形。", Gray, new Thickness(0, 10, 0, 0)));
                sp.Children.Add(Caption("这是概念形状：参考它在 SolidWorks 里重新建模（保留这些梁和筋、去掉空的地方），再回到这里用应力分析校核。", Gray, new Thickness(0, 4, 0, 0)));
                var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                var exp = new Button { Style = (Style)Application.Current.FindResource("Btn.Primary"), Content = "导出 STL…", Margin = new Thickness(0, 0, 8, 6) };
                exp.Click += (s, e) => ExportTopoStl(null);
                row.Children.Add(exp);
                var ghost = new Button { Style = (Style)Application.Current.FindResource("Btn.Secondary"), Content = _topoGhost ? "隐藏原外形" : "显示原外形", Margin = new Thickness(0, 0, 8, 6) };
                ghost.Click += (s, e) => { _topoGhost = !_topoGhost; _view.ShowSurface(_topo.Surface, _topoGhost); BuildSide(); };
                row.Children.Add(ghost);
                var again = new Button { Style = (Style)Application.Current.FindResource("Btn.Secondary"), Content = "换个参数再算", Margin = new Thickness(0, 0, 8, 6) };
                again.Click += (s, e) => { _topo = null; _view.ShowFaces(FaceColor); BuildSide(); UpdateButtons(); };
                row.Children.Add(again);
                sp.Children.Add(row);
            }
            else
            {
                sp.Children.Add(Caption("在当前的约束和载荷下，只保留一部分材料，让零件尽量刚。固定面和受载面附近的材料会保留。", Gray, new Thickness(0, 0, 0, 8)));
                sp.Children.Add(new TextBlock { Text = "保留的材料比例", FontSize = 12.5, Foreground = Gray, Margin = new Thickness(0, 0, 0, 4) });
                var w = new WrapPanel();
                var g = "vf" + Guid.NewGuid().ToString("N");
                foreach (var v in new[] { 0.2, 0.3, 0.4, 0.5, 0.6 })
                {
                    var rb = Pill($"{v:P0}", g, Math.Abs(v - _topoVf) < 1e-6);
                    var vv = v;
                    rb.Checked += (s, e) => _topoVf = vv;
                    w.Children.Add(rb);
                }
                sp.Children.Add(w);
                sp.Children.Add(new TextBlock { Text = "细节程度", FontSize = 12.5, Foreground = Gray, Margin = new Thickness(0, 8, 0, 4) });
                var w2 = new WrapPanel();
                var g2 = "det" + Guid.NewGuid().ToString("N");
                foreach (var (d, tip) in new[] { ("粗", "约 2.5 万单元，半分钟左右，结构粗壮"), ("标准", "约 6 万单元，1 分钟左右"), ("细", "约 12 万单元，几分钟，细节更多") })
                {
                    var rb = Pill(d, g2, d == _topoDetail);
                    rb.ToolTip = tip;
                    var dd = d;
                    rb.Checked += (s, e) => _topoDetail = dd;
                    w2.Children.Add(rb);
                }
                sp.Children.Add(w2);
                if (IsBusy) sp.Children.Add(Caption($"正在优化：第 {_topoLiveIter} 轮……", Accent, new Thickness(0, 10, 0, 0)));
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                var start = new Button { Style = (Style)Application.Current.FindResource("Btn.Primary"), Content = "开始优化", IsEnabled = !IsBusy && _fixed.Count > 0 && _loads.Count > 0 };
                start.Click += async (s, e) => await RunTopoAsync();
                row.Children.Add(start);
                var back = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "回到应力分析", Margin = new Thickness(12, 0, 0, 0), IsEnabled = !IsBusy };
                back.Click += (s, e) => { _topoMode = false; _topo = null; ShowEdit(); Changed(); };
                row.Children.Add(back);
                sp.Children.Add(row);
            }
            _side.Children.Add(Group(sp));
        }

        public async Task RunTopoAsync()
        {
            if (IsBusy || _mesh == null || _fixed.Count == 0 || _loads.Count == 0) return;
            _topoMode = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _topo = null; _topoLiveIter = 0;
            SetBusy(true, "拓扑优化", overlay: false);
            BuildSide();
            using (HeavyWork.Begin())
            try
            {
                int target = _topoDetail == "粗" ? 25000 : _topoDetail == "细" ? 120000 : 60000;
                var size = TopoOpt.SuggestMeshSize(_mesh.Volume(), target);
                Phase($"拓扑优化：正在划分网格（尺寸 {size * 1000:0.#} mm）");
                var step = _step;
                var mesh = await Task.Run(() => Gmsh.Mesh(step, size, Phase), ct);
                var model = Model(mesh);
                var settings = new TopoSettings { VolumeFraction = _topoVf };
                DateTime lastShow = DateTime.MinValue;
                var r = await Task.Run(() => TopoOpt.Run(model, settings, Phase, (it, tr) =>
                {
                    // 实时显示：每隔一会儿提取一次中间形状
                    if ((DateTime.Now - lastShow).TotalMilliseconds < 700) return;
                    lastShow = DateTime.Now;
                    var surf = TopoOpt.PreviewSurface(mesh, tr.Density);
                    Root.Dispatcher.BeginInvoke(new Action(() => { _topoLiveIter = it; if (IsBusy) { _view.ShowSurface(surf, true); BuildSide(); } }));
                }, ct), ct);
                _topo = r;
                SetBusy(false);
                _view.ShowSurface(r.Surface, _topoGhost);
                BuildSide();
                _status.Text = $"拓扑优化完成：保留 {r.VolumeFraction:P0} 的材料，刚度是实心零件的 {r.StiffnessRatio:P0}（{r.Iterations} 轮，{r.Seconds:0} 秒）";
                _status.Foreground = Green;
                Root.Find<TextBlock>("ViewHint").Text = "灰色：优化后的形状 · 浅色：原外形 · 左键拖动旋转 · 滚轮缩放";
            }
            catch (OperationCanceledException) { SetBusy(false); _view.ShowFaces(FaceColor); BuildSide(); _status.Text = "已停止"; _status.Foreground = Gray; }
            catch (Exception ex) { SetBusy(false); _view.ShowFaces(FaceColor); BuildSide(); _status.Text = "拓扑优化失败：" + ex.Message; _status.Foreground = Red; }
            finally { _cts = null; UpdateButtons(); }
        }

        string ExportTopoStl(string path)
        {
            if (_topo == null) return null;
            if (path == null)
            {
                var name = Path.GetFileNameWithoutExtension(_source) + $"_拓扑优化_{_topoVf * 100:0}%.stl";
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = name, Filter = "STL|*.stl", InitialDirectory = _sample != null ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory) : Path.GetDirectoryName(_source) };
                if (dlg.ShowDialog(_owner) != true) return null;
                path = dlg.FileName;
            }
            TopoSurface.WriteStl(_topo.Surface, path);
            _status.Text = "已导出 " + path + "（单位毫米，可以在 SolidWorks 里作为参考几何插入）";
            _status.Foreground = Green;
            return path;
        }

        // ================= 给 AI 用的接口 =================

        internal static IEnumerable<ApiAction> ApiActions => new[]
        {
            new ApiAction("load", "读入零件（.sldprt / .step）", "{path}"),
            new ApiAction("sample", "读入自带示例并设好约束和载荷", "{id: bracket | cantilever}"),
            new ApiAction("faces", "列出所有面（编号、平面/曲面、面积 mm²、形心 mm、法向）", ""),
            new ApiAction("materials", "列出材料表", ""),
            new ApiAction("material", "选材料；或给自定义材料", "{name} 或 {E_GPa, nu, strength_MPa, density, brittle}"),
            new ApiAction("mesh", "网格密度", "{density: 粗 | 标准 | 细}"),
            new ApiAction("fix", "固定面", "{faces: [面编号...]}"),
            new ApiAction("clear_fixed", "清除所有固定面", ""),
            new ApiAction("add_load", "加载荷", "{kind: force | pressure | torque | gravity, faces?: [...], value: N / MPa / N·m / 倍g, dir?: [x,y,z], normal?: bool}"),
            new ApiAction("clear_loads", "清除所有载荷", ""),
            new ApiAction("solve", "计算（可选自适应加密）", "{adaptive?: bool}"),
            new ApiAction("result", "当前结果", ""),
            new ApiAction("view", "结果显示方式", "{mode: vm | p1 | disp | util, deform?: bool}"),
            new ApiAction("topo", "拓扑优化", "{volume?: 0.3, detail?: 粗 | 标准 | 细, stl?: 输出路径}"),
            new ApiAction("report", "生成实验 / 论文报告（Word + 300 dpi 图 + CSV 原始数据 + 方法描述和参考文献）。当前是拓扑优化结果时出拓扑优化报告", "{dir?: 输出文件夹（不给则放在 桌面\\SW工具箱报告）}"),
        };

        internal async Task<object> ApiAsync(string action, Dictionary<string, object> a)
        {
            if (IsBusy) throw new InvalidOperationException("正在计算，稍后再试");
            switch (action)
            {
                case "load":
                    {
                        var p = a.ReqStr("path");
                        if (!File.Exists(p)) throw new ArgumentException("文件不存在：" + p);
                        _sample = null;
                        await LoadPartAsync(p);
                        if (_mesh == null) throw new InvalidOperationException(_status.Text);
                        return Summary();
                    }
                case "sample":
                    await LoadSampleAsync(a.Str("id", "bracket"));
                    if (_mesh == null) throw new InvalidOperationException(_status.Text);
                    return Summary();
                case "faces":
                    RequireMesh();
                    return _mesh.Faces.Select(f => ApiArgs.Ret("id", f.Id, "plane", f.IsPlane, "area_mm2", Math.Round(f.Area * 1e6, 3),
                        "centroid_mm", new[] { Math.Round(f.Centroid.X * 1000, 3), Math.Round(f.Centroid.Y * 1000, 3), Math.Round(f.Centroid.Z * 1000, 3) },
                        "normal", new[] { Math.Round(f.Normal.X, 4), Math.Round(f.Normal.Y, 4), Math.Round(f.Normal.Z, 4) },
                        "fixed", _fixed.Contains(f.Id))).ToList();
                case "materials":
                    return FeMaterial.All.Select(m => ApiArgs.Ret("name", m.Name, "category", m.Category, "E_GPa", m.E / 1e9, "nu", m.Nu, "strength_MPa", m.Strength / 1e6, "brittle", m.Brittle, "density", m.Density, "note", m.Note)).ToList();
                case "material":
                    if (a.Has("name"))
                    {
                        var n = a.Str("name");
                        _mat = FeMaterial.All.FirstOrDefault(m => m.Name == n) ?? FeMaterial.All.FirstOrDefault(m => m.Name.Contains(n)) ?? throw new ArgumentException("没有这个材料：" + n);
                    }
                    else _mat = FeMaterial.CreateCustom(a.ReqNum("E_GPa"), a.Num("nu", 0.3), a.ReqNum("strength_MPa"), a.Num("density", 7850), a.Bool("brittle"));
                    _matCategory = _mat.Category;
                    Changed();
                    return ApiArgs.Ret("material", MaterialLine(_mat));
                case "mesh":
                    {
                        RequireMesh();
                        var d = a.ReqStr("density");
                        var rb = Root.Find<RadioButton>(d == "粗" ? "MeshCoarse" : d == "细" ? "MeshFine" : "MeshNormal");
                        if (rb.IsChecked == true) return Summary();
                        rb.IsChecked = true;
                        while (IsBusy) await Task.Delay(100);
                        return Summary();
                    }
                case "fix":
                    RequireMesh();
                    foreach (var f in FaceList(a)) { _fixed.Add(f); foreach (var l in _loads) l.Faces.Remove(f); }
                    _loads.RemoveAll(l => l.Kind != LoadKind.Gravity && l.Faces.Count == 0);
                    Changed();
                    return Summary();
                case "clear_fixed": _fixed.Clear(); Changed(); return Summary();
                case "clear_loads": _loads.Clear(); Changed(); return Summary();
                case "add_load":
                    {
                        RequireMesh();
                        var k = a.ReqStr("kind").ToLowerInvariant();
                        var kind = k == "force" || k == "力" ? LoadKind.Force : k == "pressure" || k == "压力" ? LoadKind.Pressure : k == "torque" || k == "扭矩" ? LoadKind.Torque :
                                   k == "gravity" || k == "重力" ? LoadKind.Gravity : throw new ArgumentException("kind 应该是 force / pressure / torque / gravity");
                        var faces = kind == LoadKind.Gravity ? new List<int>() : FaceList(a);
                        double v = a.ReqNum("value");
                        if (kind == LoadKind.Pressure) v *= 1e6;
                        Vec3? dir = null;
                        if (a.Has("dir")) { var d = a.List("dir").Select(o => Convert.ToDouble(o, CultureInfo.InvariantCulture)).ToArray(); if (d.Length != 3) throw new ArgumentException("dir 要 3 个数"); dir = new Vec3(d[0], d[1], d[2]).Normalized(); }
                        AddLoad(kind, faces, v, dir, a.Bool("normal"));
                        return Summary();
                    }
                case "solve":
                    if (_fixed.Count == 0 || _loads.Count == 0) throw new InvalidOperationException("先设置固定面和载荷");
                    _topoMode = false;
                    await SolveAsync();
                    if (a.Bool("adaptive") && _result != null) await AdaptiveAsync();
                    if (_result == null) throw new InvalidOperationException(_status.Text);
                    return ResultInfo();
                case "result":
                    if (_result == null) throw new InvalidOperationException("还没有计算结果");
                    return ResultInfo();
                case "view":
                    {
                        if (_result == null) throw new InvalidOperationException("还没有计算结果");
                        var m = a.Str("mode", _showMode);
                        var name = m == "p1" ? "ShowP1" : m == "disp" ? "ShowDisp" : m == "util" ? "ShowUtil" : "ShowVm";
                        if (a.Has("deform")) _showDeform.IsChecked = a.Bool("deform");
                        Root.Find<RadioButton>(name).IsChecked = true;
                        return ApiArgs.Ret("mode", _showMode);
                    }
                case "topo":
                    {
                        if (_fixed.Count == 0 || _loads.Count == 0) throw new InvalidOperationException("先设置固定面和载荷");
                        if (a.Has("volume")) { var v = a.Num("volume"); _topoVf = v > 1 ? v / 100 : v; }
                        if (a.Has("detail")) _topoDetail = a.Str("detail");
                        EnterTopoMode();
                        await RunTopoAsync();
                        if (_topo == null) throw new InvalidOperationException(_status.Text);
                        string stl = a.Has("stl") ? ExportTopoStl(a.Str("stl")) : null;
                        return ApiArgs.Ret("volume_fraction", _topo.VolumeFraction, "stiffness_ratio", _topo.StiffnessRatio, "iterations", _topo.Iterations, "seconds", Math.Round(_topo.Seconds, 1), "triangles", _topo.Surface.Length / 9, "stl", stl);
                    }
                case "report":
                    {
                        if (_result == null && _topo == null) throw new InvalidOperationException("还没有结果：先 solve 或 topo");
                        var dir = a.Str("dir") ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory), "SW工具箱报告",
                            (_sample != null ? _sample.Name.Replace(" ", "") : Path.GetFileNameWithoutExtension(_source)) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        var docx = await ReportAsync(dir);
                        if (docx == null) throw new InvalidOperationException(_status.Text);
                        return ApiArgs.Ret("folder", dir, "docx", docx, "markdown", Path.Combine(dir, "报告.md"), "figures", Path.Combine(dir, "图"), "data", Path.Combine(dir, "数据"));
                    }
                default: throw new ArgumentException("未知操作：" + action);
            }
        }

        void RequireMesh() { if (_mesh == null) throw new InvalidOperationException("先读入零件（load 或 sample）"); }

        List<int> FaceList(Dictionary<string, object> a)
        {
            var ids = a.List("faces").Select(o => Convert.ToInt32(o, CultureInfo.InvariantCulture)).ToList();
            if (ids.Count == 0) throw new ArgumentException("缺少 faces（面编号列表，先用 faces 查看）");
            var valid = new HashSet<int>(_mesh.Faces.Select(f => f.Id));
            var bad = ids.Where(i => !valid.Contains(i)).ToList();
            if (bad.Count > 0) throw new ArgumentException("没有这些面：" + string.Join(",", bad));
            return ids;
        }

        object Summary() => ApiArgs.Ret(
            "part", _source, "material", _mat?.Name, "mesh", _mesh == null ? null : $"{_mesh.Elements} 单元 / {_mesh.Nodes} 节点 / {_mesh.Faces.Count} 面",
            "fixed", _fixed.ToList(), "loads", _loads.Select(l => ApiArgs.Ret("kind", l.Kind.ToString(), "faces", l.Faces.ToList(), "value", l.Kind == LoadKind.Pressure ? l.Value / 1e6 : l.Value,
                "dir", l.AlongNormal ? "normal" : (object)new[] { l.Dir.X, l.Dir.Y, l.Dir.Z })).ToList(), "status", _status.Text);

        object ResultInfo()
        {
            var r = _result;
            return ApiArgs.Ret("max_von_mises_MPa", r.MaxVm / 1e6, "max_von_mises_away_from_fixed_MPa", r.MaxVmAway / 1e6, "max_principal_MPa", r.MaxP1 / 1e6, "min_principal_MPa", r.MinP3 / 1e6,
                "safety_factor", double.IsInfinity(r.SafetyFactor) ? -1 : r.SafetyFactor, "criterion", _mat.Brittle ? "max principal (brittle)" : "von Mises (ductile)",
                "max_displacement_mm", r.MaxDisp * 1000, "error_estimate", r.ErrorEstimate, "elements", _mesh.Elements, "equilibrium_error", r.EquilibriumError,
                "passes", r.Passes.Select(p => ApiArgs.Ret("elements", p.Elements, "max_vm_MPa", p.MaxVm / 1e6, "max_vm_away_MPa", p.MaxVmAway / 1e6, "eta", p.Eta)).ToList(),
                "notes", r.Notes, "refine_note", _refineNote, "seconds", r.Seconds);
        }

        // ================= 自测（开发用） =================

        /// <summary>
        /// part 是零件路径：固定最大的平面，在离它最远的平面上加 1000 N，计算并自适应加密。
        /// part = "sample:bracket" / "sample:cantilever"：用示例（cantilever 做拓扑优化）。
        /// </summary>
        internal async Task<string> SelfTest(string part)
        {
            var sb = new StringBuilder();
            if (part.StartsWith("sample:"))
            {
                var id = part.Substring(7);
                await LoadSampleAsync(id);
                if (_mesh == null) return "读入失败：" + _status.Text;
                sb.AppendLine($"示例 {_partName.Text}，材料 {_mat.Name}，固定 {string.Join(",", _fixed)}，载荷 {_loads.Count} 个");
                var rep = Path.Combine(Path.GetTempPath(), "swkit-report-test", id);
                if (id == "cantilever")
                {
                    await RunTopoAsync();
                    sb.AppendLine("拓扑优化：" + _status.Text);
                    sb.AppendLine("报告：" + await ReportAsync(rep));
                    return sb.ToString();
                }
                await SolveAsync();
                sb.AppendLine("计算：" + _status.Text);
                await AdaptiveAsync();
                sb.AppendLine("自适应加密：" + _status.Text);
                sb.AppendLine(_refineNote);
                sb.AppendLine("报告：" + await ReportAsync(rep));
                return sb.ToString();
            }
            await LoadPartAsync(part);
            if (_mesh == null) return "读入失败：" + _status.Text;
            var planes = _mesh.Faces.Where(f => f.IsPlane).OrderByDescending(f => f.Area).ToList();
            var fix = planes[0];
            var load = planes.OrderByDescending(f => (f.Centroid - fix.Centroid).Length).First();
            _sel.Clear(); _sel.Add(fix.Id); Root.Find<Button>("SelFixBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AddLoad(LoadKind.Force, new List<int> { load.Id }, 1000, null, true);
            await SolveAsync();
            sb.AppendLine($"零件 {_partName.Text}，材料 {_mat.Name}，固定 {fix}，在 {load} 上加 1000 N（垂直压向面）");
            sb.AppendLine("计算：" + _status.Text);
            await AdaptiveAsync();
            sb.AppendLine("自适应加密：" + _status.Text);
            sb.AppendLine(_refineNote);
            _showDeform.IsChecked = true;
            return sb.ToString();
        }
    }
}
