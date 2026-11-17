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
using SolidWorks.Interop.sldworks;
using SwKit.Core;
using SwKit.Core.Dfm;
using SwKit.Core.Fea;
using SwKit.Core.Report;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>「可制造性检查」页面：3D 打印（FDM / 光固化 / 尼龙粉末烧结 / 金属打印）、CNC、板材切割、钣金、注塑。</summary>
    sealed class DfmPage
    {
        public FrameworkElement Root { get; }
        public bool IsBusy { get; private set; }

        readonly Window _owner;
        readonly SwWorker _worker;
        readonly DfmView _view = new DfmView();
        readonly FrameworkElement _partBar, _empty, _busy, _work;
        readonly TextBlock _partName, _partPath, _status, _busyText, _busyDetail;
        readonly StackPanel _side;
        readonly WrapPanel _legend, _procPanel;
        readonly Button _reportBtn;
        double _procWidth;          // 工艺按钮排成一行时的宽度
        bool? _procOneRow;

        string _source;
        DfmMesh _mesh;
        DfmAnalyzer _an;
        DfmResult _result;
        readonly DfmSettings _s = new DfmSettings();
        Vec3? _up;                 // 用户选的摆放方向 / 开模方向（null = 用推荐的第一个）
        int _selIssue = -1;
        CancellationTokenSource _cts;
        int _runId;

        static readonly Brush Gray = Ui.Brush("#6E6E73"), Green = Ui.Brush("#248A3D"), Orange = Ui.Brush("#B25000"), Red = Ui.Brush("#D70015"), Text3 = Ui.Brush("#A1A1A6"), Accent = Ui.Brush("#0071E3");

        // 工艺按钮（XAML 里的名字）
        static readonly (string name, DfmProcess p)[] ProcButtons =
        {
            ("ProcFdm", DfmProcess.Fdm), ("ProcSla", DfmProcess.Sla), ("ProcSls", DfmProcess.Sls), ("ProcSlm", DfmProcess.Slm),
            ("ProcCnc", DfmProcess.Cnc), ("ProcCut", DfmProcess.Cut), ("ProcSheet", DfmProcess.Sheet), ("ProcMold", DfmProcess.Mold),
        };

        // 自带示例：编号、文件名、工艺
        static readonly (string id, string file, DfmProcess p)[] Samples =
        {
            ("悬垂", "悬垂", DfmProcess.Fdm), ("薄壁", "薄壁", DfmProcess.Fdm), ("CNC", "CNC", DfmProcess.Cnc),
            ("钣金", "钣金支架", DfmProcess.Sheet), ("碳板", "碳板机臂", DfmProcess.Cut),
        };

        public DfmPage(Window owner, SwWorker worker)
        {
            _owner = owner; _worker = worker;
            Root = (FrameworkElement)Ui.Load("DfmPage.xaml");
            _partBar = Root.Find<FrameworkElement>("PartBar");
            _empty = Root.Find<FrameworkElement>("EmptyState");
            _busy = Root.Find<FrameworkElement>("BusyState");
            _work = Root.Find<FrameworkElement>("WorkState");
            _partName = Root.Find<TextBlock>("PartName");
            _partPath = Root.Find<TextBlock>("PartPath");
            _status = Root.Find<TextBlock>("StatusText");
            _busyText = Root.Find<TextBlock>("BusyText");
            _busyDetail = Root.Find<TextBlock>("BusyDetail");
            _side = Root.Find<StackPanel>("SidePanel");
            _legend = Root.Find<WrapPanel>("Legend");
            _procPanel = Root.Find<WrapPanel>("ProcPanel");
            _reportBtn = Root.Find<Button>("ReportBtn");
            Root.Find<Grid>("ViewHost").Children.Add(_view.Element);
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            ((RotateTransform)Root.Find<FrameworkElement>("Spinner").RenderTransform).BeginAnimation(RotateTransform.AngleProperty, spin);
            BuildHowSteps();

            Root.Find<Button>("PickPartBtn").Click += (s, e) => PickPart();
            Root.Find<Button>("ChangePartBtn").Click += (s, e) => PickPart();
            Root.Find<Button>("ResetViewBtn").Click += (s, e) => _view.ResetView();
            foreach (var (n, id) in new[] { ("SampleOverhangLink", "悬垂"), ("SampleOverhangBtn", "悬垂"), ("SampleThinLink", "薄壁"), ("SampleCncLink", "CNC"), ("SampleCncBtn", "CNC"),
                                            ("SampleSheetLink", "钣金"), ("SampleSheetBtn", "钣金"), ("SamplePlateLink", "碳板"), ("SamplePlateBtn", "碳板") })
            {
                var sample = Samples.First(x => x.id == id);
                Root.Find<Button>(n).Click += async (s, e) => await LoadSampleAsync(sample.file, sample.p);
            }
            _reportBtn.Click += (s, e) => SaveReportDialog();
            foreach (var (n, p) in ProcButtons)
            {
                var rb = Root.Find<RadioButton>(n); var pp = p;
                rb.Checked += async (s, e) => { if (_s.Process == pp) return; SetProcess(pp); await AnalyzeAsync(); };
            }
            Root.Find<RadioButton>("ProcFdm").IsChecked = true;
            _partBar.SizeChanged += (s, e) => LayoutPartBar();
            Root.DragOver += (s, e) => { e.Effects = !IsBusy && PartFrom(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Root.Drop += async (s, e) => { var p = PartFrom(e.Data); if (p != null && !IsBusy) await LoadPartAsync(p); };
        }

        /// <summary>零件栏：放得下时工艺按钮和零件名在同一行，否则换到第二行（WrapPanel 再按组折行）。</summary>
        void LayoutPartBar()
        {
            var grid = (Grid)_procPanel.Parent;
            if (grid.ActualWidth <= 0) return;
            if (_procWidth <= 0)
            {
                _procPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _procWidth = _procPanel.DesiredSize.Width;
            }
            double button = Root.Find<Button>("ChangePartBtn").ActualWidth;
            bool oneRow = grid.ActualWidth >= 36 + 12 + 200 + 16 + _procWidth + 14 + Math.Max(button, 60);
            if (_procOneRow == oneRow) return;
            _procOneRow = oneRow;
            Grid.SetRow(_procPanel, oneRow ? 0 : 1);
            Grid.SetColumn(_procPanel, oneRow ? 2 : 0);
            Grid.SetColumnSpan(_procPanel, oneRow ? 1 : 4);
            _procPanel.Margin = oneRow ? new Thickness(0, 0, 14, 0) : new Thickness(0, 10, 0, 0);
        }

        static string PartFrom(IDataObject d) =>
            (d.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(f => new[] { ".sldprt", ".step", ".stp", ".stl" }.Contains(Path.GetExtension(f).ToLowerInvariant()));

        void SetProcess(DfmProcess p)
        {
            if (_s.Process != p)
            {
                // 最小壁厚、最小孔径按各工艺的默认值重新来
                _s.MinWallPrint = double.NaN; _s.MinHoleUser = double.NaN;
            }
            _s.Process = p;
            if (p == DfmProcess.Sla && !_s.PrintMaterial.Resin) _s.PrintMaterial = PrintMaterial.All.First(x => x.Resin);
            if (p == DfmProcess.Fdm && _s.PrintMaterial.Resin) _s.PrintMaterial = PrintMaterial.All[0];
            if (p == DfmProcess.Sla && !_s.Printer.Resin) _s.Printer = Printer.All.First(x => x.Resin);
            if (p == DfmProcess.Fdm && _s.Printer.Resin) _s.Printer = Printer.All[0];
            _up = null;
            var rb = Root.Find<RadioButton>(ProcButtons.First(x => x.p == p).name);
            if (rb.IsChecked != true) rb.IsChecked = true;
        }

        void BuildHowSteps()
        {
            var host = Root.Find<UniformGrid>("HowSteps");
            var steps = new[]
            {
                ("1", "拖入零件", "SolidWorks 零件、STEP 或 STL。"),
                ("2", "选工艺", "3D 打印、CNC、板材切割、钣金或注塑；材料和参数在右侧调。"),
                ("3", "看问题、改设计", "点每条问题会在模型上定位，并给出怎么改；可以生成报告。"),
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

        // ================= 读入 =================

        async void PickPart()
        {
            if (IsBusy) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择零件", Filter = "零件|*.sldprt;*.step;*.stp;*.stl|SolidWorks 零件|*.sldprt|STEP|*.step;*.stp|STL|*.stl" };
            if (dlg.ShowDialog(_owner) != true) return;
            await LoadPartAsync(dlg.FileName);
        }

        static string SampleFolder()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[] { Path.Combine(dir, "示例", "可制造性"), Path.Combine(dir, "..", "tools", "dfm-tests") })
                if (Directory.Exists(c)) return Path.GetFullPath(c);
            return null;
        }

        async Task LoadSampleAsync(string name, DfmProcess p)
        {
            var dir = SampleFolder();
            var path = dir == null ? null : Path.Combine(dir, name + ".step");
            if (path == null || !File.Exists(path)) { _status.Text = "找不到示例文件（应该在程序旁边的「示例\\可制造性」文件夹里）"; _status.Foreground = Red; return; }
            SetProcess(p);
            if (p == DfmProcess.Cut) { _s.PlateMaterial = PlateMaterial.All[0]; _s.CutMethod = CutMethod.Router; }   // 碳板机臂：3 mm 碳板，CNC 雕刻
            await LoadPartAsync(path);
        }

        public async Task LoadPartAsync(string path)
        {
            if (IsBusy) return;
            _source = Path.GetFullPath(path);
            _partName.Text = Path.GetFileName(_source);
            _partPath.Text = Path.GetDirectoryName(_source);
            Ui.Fade(_partBar, true);
            _mesh = null; _an = null; _result = null; _up = null; _selIssue = -1;
            Ui.Fade(_empty, false); Ui.Fade(_work, false); Ui.Fade(_busy, true);
            IsBusy = true; _busyText.Text = "正在读取零件"; _reportBtn.IsEnabled = false;
            try
            {
                string input = _source;
                if (Path.GetExtension(_source).ToLowerInvariant() == ".sldprt")
                {
                    if (!StepCache.TryGet(_source, out input, out _))
                    {
                        var step = await _worker.Run(() =>
                        {
                            var s = _worker.Acquire(m => Phase(m.Contains("启动") ? "正在启动后台 SolidWorks（第一次约 20–60 秒）" : m), CancellationToken.None);
                            try { Phase("正在把零件转成 STEP"); return ToStep(s.App, _source); }
                            finally { _worker.Release(); }
                        });
                        input = StepCache.Put(_source, step, null);
                    }
                }
                var src = _source;
                using (HeavyWork.Begin())
                {
                    var (mesh, an) = await Task.Run(() =>
                    {
                        var m = DfmMesh.Load(input, Phase);
                        m.Source = src;
                        Phase("正在建立加速结构");
                        return (m, new DfmAnalyzer(m));
                    });
                    _mesh = mesh; _an = an;
                }
                IsBusy = false;
                Ui.Fade(_busy, false); Ui.Fade(_work, true);
                await AnalyzeAsync();
            }
            catch (Exception ex)
            {
                IsBusy = false;
                Ui.Fade(_busy, false); Ui.Fade(_mesh == null ? _empty : _work, true);
                _status.Text = "读取零件失败：" + ex.Message; _status.Foreground = Red;
            }
        }

        void Phase(string text) => Root.Dispatcher.BeginInvoke(new Action(() => { _busyDetail.Text = text; _status.Text = text; _status.Foreground = Gray; }));

        static string ToStep(ISldWorks app, string path)
        {
            int e = 0, w = 0;
            var doc = app.GetOpenDocumentByName(path) as ModelDoc2 ?? app.OpenDoc6(path, 1, 1 | 2, "", ref e, ref w);
            if (doc == null) throw new InvalidOperationException("打开零件失败：" + ExportRunner.LoadErrorText(e));
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "swkit_dfm_parts");
                Directory.CreateDirectory(dir);
                var step = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".step");
                app.ActivateDoc3(doc.GetTitle(), false, 1, ref e);
                doc.Extension.SaveAs(step, 0, 1, null, ref e, ref w);
                if (!File.Exists(step)) throw new InvalidOperationException("导出 STEP 失败：" + ExportRunner.SaveErrorText(e));
                return step;
            }
            finally { try { app.CloseDoc(doc.GetTitle()); } catch { } }
        }

        // ================= 分析 =================

        public async Task AnalyzeAsync()
        {
            if (_an == null) return;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            int id = ++_runId;
            var settings = _s; var up = _up; var an = _an;
            _status.Text = "正在检查…"; _status.Foreground = Gray;
            try
            {
                DfmResult r;
                using (HeavyWork.Begin())
                    r = await Task.Run(() => an.Analyze(settings, up, Phase, cts.Token), cts.Token);
                if (id != _runId) return;
                _result = r; _selIssue = -1;
                _view.Show(_mesh, r.Flags, r.ViewUp, r.Bed, r.Arrows.Count > 0 ? r.Arrows : null, keepView: false, arrowsOut: r.Process == DfmProcess.Mold);
                BuildLegend();
                BuildSide();
                int ne = r.Issues.Count(i => i.Severity == Severity.Error), nw = r.Issues.Count(i => i.Severity == Severity.Warning);
                _status.Text = (ne + nw == 0 ? $"{DfmReport.ShortName(r.Process)}：没有发现问题（{r.Seconds:0.0} 秒）" : $"{DfmReport.ShortName(r.Process)}：{ne} 个必须修改、{nw} 个建议修改（{r.Seconds:0.0} 秒）。点右侧的问题可以在模型上定位");
                _status.Foreground = ne > 0 ? Red : nw > 0 ? Orange : Green;
                _reportBtn.IsEnabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (id == _runId) { _status.Text = "检查失败：" + ex.Message; _status.Foreground = Red; } }
        }

        void BuildLegend()
        {
            _legend.Children.Clear();
            void Chip(int c, string text)
            {
                var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 4) };
                sp.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(DfmView.Colors[c]), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Gray, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                _legend.Children.Add(new Border { Background = Ui.Brush("#F2FFFFFF"), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 3, 0, 3), Child = sp, Margin = new Thickness(0, 0, 6, 4) });
            }
            switch (_result.Process)
            {
                case DfmProcess.Cnc: Chip(3, "倒扣"); Chip(4, "内角 / 深腔"); Chip(2, "薄壁"); Chip(5, "孔"); break;
                case DfmProcess.Sls: Chip(2, "薄壁 / 翘曲"); Chip(4, "缝隙"); Chip(11, "封闭空腔"); Chip(5, "孔"); break;
                case DfmProcess.Slm: Chip(1, "悬垂（要支撑）"); Chip(7, "贴基板"); Chip(2, "薄壁"); Chip(4, "缝隙"); Chip(11, "封闭空腔"); Chip(5, "孔"); break;
                case DfmProcess.Sheet: Chip(8, "折弯"); Chip(3, "不等厚 / 斜边"); Chip(2, "边距太小"); Chip(5, "孔"); break;
                case DfmProcess.Cut: Chip(3, "要另外铣"); Chip(4, "内角 / 窄槽"); Chip(2, "窄筋 / 孔边距"); Chip(5, "孔"); break;
                case DfmProcess.Mold: Chip(3, "倒扣"); Chip(9, "拔模不足"); Chip(10, "厚壁"); Chip(2, "薄壁"); Chip(4, "尖内角"); Chip(5, "孔"); break;
                default:
                    Chip(1, "悬垂（要支撑）"); Chip(7, "贴平台"); Chip(2, "薄壁"); Chip(5, "孔");
                    if (_result.Process == DfmProcess.Sla && _mesh.Shells.Any(x => x.Cavity)) Chip(11, "封闭空腔");
                    break;
            }
            Chip(6, "选中的问题");
        }

        // ================= 右侧面板 =================

        void Section(string t) => _side.Children.Add(new TextBlock { Text = t, Style = (Style)Application.Current.FindResource("T.Section") });
        static TextBlock Caption(string text, Brush color, Thickness margin) => new TextBlock { Text = text, FontSize = 12, Foreground = color, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = margin };
        Border Group(UIElement child) => new Border { Style = (Style)Application.Current.FindResource("Group"), Child = child };
        static RadioButton Pill(string text, string group, bool on) => new RadioButton { Style = (Style)Application.Current.FindResource("Pill"), GroupName = group, Content = text, IsChecked = on };
        static string F(double v, string f = "0.##") => v.ToString(f, CultureInfo.InvariantCulture);

        void BuildSide()
        {
            _side.Children.Clear();
            if (_result == null) return;
            var r = _result;
            // ---- 问题 ----
            int ne = r.Issues.Count(i => i.Severity == Severity.Error), nw = r.Issues.Count(i => i.Severity == Severity.Warning), ni = r.Issues.Count - ne - nw;
            Section($"问题（必须修改 {ne}、建议修改 {nw}、提示 {ni}）");
            if (r.Issues.Count == 0) _side.Children.Add(Group(Caption("没有发现问题，可以直接加工。", Green, new Thickness(0))));
            for (int i = 0; i < r.Issues.Count; i++) _side.Children.Add(IssueCard(r.Issues[i], i));

            // ---- 摆放方向 / 开模方向 ----
            if (DfmProcesses.Printing(r.Process) && r.Orientation != null)
            {
                bool sls = r.Process == DfmProcess.Sls;
                Section("摆放方向");
                var sp = new StackPanel();
                string cur = sls ? $"当前：{r.Orientation.Label}（高 {F(r.Orientation.Height, "0.#")} mm）"
                                 : $"当前：{r.Orientation.Label}（支撑 {F(r.Orientation.SupportVolume / 1000, "0.##")} cm³、高 {F(r.Orientation.Height, "0.#")} mm）";
                sp.Children.Add(Caption(cur + (r.Orientation.Fits ? "" : " · 放不下"), r.Orientation.Fits ? Gray : Red, new Thickness(0, 0, 0, 6)));
                sp.Children.Add(CandidateButtons(r, c => sls ? $"高 {F(c.Height, "0.#")} mm" + (c.Fits ? "" : "，放不下") : $"支撑 {F(c.SupportVolume / 1000, "0.##")} cm³、高 {F(c.Height, "0.#")} mm、底面接触 {F(c.BaseArea, "0")} mm²"));
                sp.Children.Add(Caption(sls ? "粉末烧结不需要支撑，摆放主要影响打印高度（时间）和台阶纹的方向；受力方向尽量平行于打印层。"
                                      : r.Process == DfmProcess.Slm ? "推荐按「支撑少、高度低、底面稳」打分。金属打印的支撑还要把热量导到基板，截面积变化平缓的方向更不容易变形。"
                                      : "推荐按「支撑少、高度低、底面贴得稳」打分。实际打印时也要考虑受力方向：层间强度最低，受拉方向尽量平行于打印层。", Text3, new Thickness(0, 2, 0, 0)));
                _side.Children.Add(Group(sp));
            }
            else if (r.Process == DfmProcess.Mold && r.Orientation != null)
            {
                Section("开模方向");
                var sp = new StackPanel();
                sp.Children.Add(Caption($"当前：{r.Orientation.Label}（箭头是两半模具分开的方向）", Gray, new Thickness(0, 0, 0, 6)));
                sp.Children.Add(CandidateButtons(r, c => $"倒扣约 {F(c.SupportArea, "0")} mm²、深 {F(c.Height, "0.#")} mm"));
                sp.Children.Add(Caption("推荐按「倒扣最少、深度最浅」选。可以点其他方向比较；侧面的倒扣会提示用滑块还是斜顶。", Text3, new Thickness(0, 2, 0, 0)));
                _side.Children.Add(Group(sp));
            }

            // ---- 指标 ----
            Section(DfmReport.MetricsTitle(r.Process));
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var mtr in r.Metrics)
            {
                int row = g.RowDefinitions.Count;
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var k = new TextBlock { Text = mtr.Name, FontSize = 12.5, Foreground = Gray, Margin = new Thickness(0, 3, 8, 3), TextWrapping = TextWrapping.Wrap };
                var v = new TextBlock { Text = mtr.Value, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap, ToolTip = mtr.Note };
                Grid.SetRow(k, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
                g.Children.Add(k); g.Children.Add(v);
            }
            var ms = new StackPanel(); ms.Children.Add(g);
            ms.Children.Add(Caption("时间和费用都是按经验参数的估算（国内常见报价），用来比较方案；准确的数字以切片软件 / 加工厂报价为准。鼠标停在数值上可以看算法。", Text3, new Thickness(0, 6, 0, 0)));
            _side.Children.Add(Group(ms));

            // ---- 工艺提示 ----
            if (r.Notes.Count > 0)
            {
                Section("工艺提示");
                var np = new StackPanel();
                foreach (var n in r.Notes) np.Children.Add(Caption("· " + n, Ui.Brush("#1D1D1F"), new Thickness(0, np.Children.Count == 0 ? 0 : 4, 0, 0)));
                _side.Children.Add(Group(np));
            }

            // ---- 参数 ----
            Section("参数");
            _side.Children.Add(Group(SettingsPanel()));
        }

        WrapPanel CandidateButtons(DfmResult r, Func<SwKit.Core.Dfm.Orientation, string> tip)
        {
            var w = new WrapPanel();
            for (int i = 0; i < r.Candidates.Count; i++)
            {
                var c = r.Candidates[i];
                var b = new Button { Style = (Style)Application.Current.FindResource(i == 0 ? "Btn.Primary" : "Btn.Secondary"), Margin = new Thickness(0, 0, 8, 6), ToolTip = tip(c) };
                b.Content = $"推荐 {i + 1}：{c.Label}";
                var cc = c;
                b.Click += async (s, e) => { _up = cc.Up; await AnalyzeAsync(); };
                w.Children.Add(b);
            }
            return w;
        }

        FrameworkElement IssueCard(DfmIssue x, int index)
        {
            var sp = new StackPanel();
            var head = new DockPanel();
            var sev = new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top,
                Background = x.Severity == Severity.Error ? Ui.Brush("#FFECEB") : x.Severity == Severity.Warning ? Ui.Brush("#FFF2E0") : Ui.Brush("#F2F2F4"),
                Child = new TextBlock { Text = DfmReport.Sev(x.Severity), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = x.Severity == Severity.Error ? Red : x.Severity == Severity.Warning ? Orange : Gray },
            };
            DockPanel.SetDock(sev, Dock.Left);
            head.Children.Add(sev);
            head.Children.Add(new TextBlock { Text = x.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(head);
            sp.Children.Add(Caption(x.Value, Gray, new Thickness(0, 4, 0, 0)));
            sp.Children.Add(Caption("建议：" + x.Suggestion, Ui.Brush("#1D1D1F"), new Thickness(0, 4, 0, 0)));
            var card = Group(sp);
            card.Margin = new Thickness(0, 0, 0, 8);
            card.Cursor = Cursors.Hand;
            if (index == _selIssue) card.BorderBrush = Accent;
            card.MouseLeftButtonUp += (s, e) => Locate(index);
            return card;
        }

        void Locate(int index)
        {
            if (_result == null || index < 0 || index >= _result.Issues.Count) return;
            _selIssue = index;
            var x = _result.Issues[index];
            _view.Locate(x.Tris, x.Location);
            BuildSide();
        }

        FrameworkElement SettingsPanel()
        {
            var sp = new StackPanel();
            void Label(string t) => sp.Children.Add(new TextBlock { Text = t, FontSize = 12.5, Foreground = Gray, Margin = new Thickness(0, sp.Children.Count == 0 ? 0 : 10, 0, 4), TextWrapping = TextWrapping.Wrap });
            void Note(string t) => sp.Children.Add(Caption(t, Text3, new Thickness(0, 6, 0, 0)));
            WrapPanel Pills<T>(IEnumerable<T> items, Func<T, string> text, Func<T, bool> on, Action<T> set, Func<T, string> tip = null)
            {
                var w = new WrapPanel(); var g = "g" + Guid.NewGuid().ToString("N");
                foreach (var it in items)
                {
                    var rb = Pill(text(it), g, on(it)); var ii = it;
                    if (tip != null) rb.ToolTip = tip(it);
                    rb.Checked += async (s, e) => { set(ii); await AnalyzeAsync(); };
                    w.Children.Add(rb);
                }
                sp.Children.Add(w);
                return w;
            }
            TextBox Number(string label, double value, string unit, Action<double> set)
            {
                var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                row.Children.Add(new TextBlock { Text = label, Width = 108, FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
                var box = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Width = 70, Text = F(value, "0.###"), TextAlignment = TextAlignment.Right };
                async void Commit()
                {
                    if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) { set(v); await AnalyzeAsync(); }
                }
                box.LostFocus += (s, e) => Commit();
                box.KeyDown += (s, e) => { if (e.Key == Key.Enter) Commit(); };
                row.Children.Add(box);
                row.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(8, 0, 0, 0), FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(row);
                return box;
            }
            void Machines(List<Printer> list, Printer cur, Action<Printer> set)
            {
                var combo = new ComboBox { FontSize = 12.5 };
                foreach (var p in list) combo.Items.Add(new ComboBoxItem { Content = $"{p.Name}（{F(p.X, "0")}×{F(p.Y, "0")}×{F(p.Z, "0")}）", Tag = p, IsSelected = p == cur });
                combo.SelectionChanged += async (s, e) => { if (combo.SelectedItem is ComboBoxItem ci) { set((Printer)ci.Tag); await AnalyzeAsync(); } };
                sp.Children.Add(combo);
            }
            int Qty(double v) => Math.Max(1, (int)Math.Round(v));
            double thick = _result != null && !double.IsNaN(_result.Thickness) ? _result.Thickness : 2;
            switch (_s.Process)
            {
                case DfmProcess.Cnc:
                    {
                        Label("材料");
                        Pills(CncMaterial.All, m => m.Name, m => m == _s.CncMaterial, m => { _s.CncMaterial = m; _s.MinWallCnc = double.NaN; });
                        var tilt = new CheckBox { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = _s.FiveAxisTilts, Margin = new Thickness(0, 10, 0, 0), Content = new TextBlock { Text = "允许 3+2 斜向装夹（45°）", FontSize = 12.5, Foreground = Gray } };
                        tilt.Checked += async (s, e) => { _s.FiveAxisTilts = true; await AnalyzeAsync(); };
                        tilt.Unchecked += async (s, e) => { _s.FiveAxisTilts = false; await AnalyzeAsync(); };
                        sp.Children.Add(tilt);
                        Number("最小壁厚", _s.MinWall, "mm", v => _s.MinWallCnc = v);
                        Number("最小刀具直径", _s.MinToolDia, "mm", v => _s.MinToolDia = v);
                        Number("机时费率", _s.HourRate, "元/小时", v => _s.HourRate = v);
                        Number("每次装夹", _s.SetupMinutes, "分钟", v => _s.SetupMinutes = v);
                        break;
                    }
                case DfmProcess.Sls:
                    Label("设备");
                    Machines(Printer.Sls, _s.SlsMachine, p => _s.SlsMachine = p);
                    Label("材料");
                    Pills(PowderMaterial.Sls, m => m.Name, m => m == _s.SlsMaterial, m => _s.SlsMaterial = m, m => $"{m.Note}；{F(m.Price)} 元/cm³（估算）");
                    Number("最小壁厚", _s.MinWall, "mm", v => _s.MinWallPrint = v);
                    Number("最小孔径", _s.MinHoleDia, "mm", v => _s.MinHoleUser = v);
                    Note($"缝隙小于 {F(_s.MinGap)} mm 会被烧结粘住；封闭空腔要开排粉孔。");
                    break;
                case DfmProcess.Slm:
                    Label("设备");
                    Machines(Printer.Slm, _s.SlmMachine, p => _s.SlmMachine = p);
                    Label("材料");
                    Pills(PowderMaterial.Slm, m => m.Name, m => m == _s.SlmMaterial, m => _s.SlmMaterial = m, m => $"{F(m.Density)} g/cm³，{F(m.Price)} 元/g（估算）；{m.Note}");
                    Label("悬垂角（与竖直方向，超过就要支撑）");
                    Pills(new[] { 35.0, 40, 45, 50 }, v => F(v, "0") + "°", v => Math.Abs(v - _s.OverhangDeg) < 1e-6, v => _s.OverhangDeg = v);
                    Number("最小壁厚", _s.MinWall, "mm", v => _s.MinWallPrint = v);
                    Number("最小孔径", _s.MinHoleDia, "mm", v => _s.MinHoleUser = v);
                    break;
                case DfmProcess.Sheet:
                    {
                        var mat = _s.SheetMaterial;
                        Label("材料");
                        Pills(SheetMaterial.All, m => m.Name, m => m == _s.SheetMaterial, m => { _s.SheetMaterial = m; _s.SheetCutPrice = double.NaN; },
                              m => $"推荐内折弯半径 ≥ {F(m.RecR, "0.#")}t；{F(m.PricePerKg, "0")} 元/kg" + (m.Note != null ? "；" + m.Note : ""));
                        Note($"{mat.Name}：内折弯半径建议 ≥ {F(mat.RecR, "0.#")}t（小于 {F(mat.MinR, "0.#")}t 外侧容易开裂），折边外尺寸 ≥ max(4t, 3t + R)，孔离折弯 ≥ 2t + R、离板边 ≥ 1.5t。" + (mat.Note != null ? mat.Note + "。" : ""));
                        Number("激光切割单价", double.IsNaN(_s.SheetCutPrice) ? mat.LaserPrice(thick) : _s.SheetCutPrice, "元/m", v => _s.SheetCutPrice = v);
                        Number("折弯单价", _s.BendPrice, "元/刀", v => _s.BendPrice = v);
                        Number("数量", _s.Quantity, "件", v => _s.Quantity = Qty(v));
                        break;
                    }
                case DfmProcess.Cut:
                    {
                        Label("加工方式");
                        Pills(new[] { CutMethod.Router, CutMethod.Laser, CutMethod.Waterjet }, m => m == CutMethod.Router ? "CNC 雕刻" : m == CutMethod.Laser ? "激光切割" : "水刀",
                              m => m == _s.CutMethod, m => { _s.CutMethod = m; _s.CutHourRate = double.NaN; },
                              m => m == CutMethod.Router ? "碳板、玻纤板、铝板、亚克力；内角至少留刀具半径" : m == CutMethod.Laser ? "钢、不锈钢、铝（光纤激光）、亚克力（CO₂）；不适合碳板" : "几乎什么都能切、能切很厚；速度慢、价格高");
                        Label("材料");
                        Pills(PlateMaterial.All, m => m.Name, m => m == _s.PlateMaterial, m => _s.PlateMaterial = m,
                              m => $"最小筋宽 ≥ max({F(m.WebK, "0.#")}t, {F(m.WebAbs, "0.#")} mm)；{F(m.Density)} g/cm³");
                        if (_s.CutMethod == CutMethod.Router) Number("铣刀直径", _s.CutterDia, "mm", v => _s.CutterDia = v);
                        if (_s.CutMethod != CutMethod.Laser) Number("机时费率", _s.CutRate, "元/小时", v => _s.CutHourRate = v);
                        Number("数量", _s.Quantity, "件", v => _s.Quantity = Qty(v));
                        Note("板材切割只能切出垂直贯穿的外形和孔；凸起、沉槽、沉头孔、倒角都要另外铣。");
                        break;
                    }
                case DfmProcess.Mold:
                    {
                        var mat = _s.MoldMaterial;
                        Label("材料");
                        Pills(MoldMaterial.All, m => m.Name, m => m == _s.MoldMaterial, m => _s.MoldMaterial = m,
                              m => $"推荐壁厚 {F(m.MinWall)}–{F(m.MaxWall)} mm，收缩率约 {F(m.Shrink, "0.#")}%，{F(m.PricePerKg, "0")} 元/kg");
                        Note($"{mat.Name}：推荐壁厚 {F(mat.MinWall)}–{F(mat.MaxWall)} mm，流长比 ≤ {F(mat.FlowRatio, "0")}，收缩率约 {F(mat.Shrink, "0.#")}%。");
                        Label("最小拔模角");
                        Pills(new[] { 0.5, 1, 1.5, 2, 3 }, v => F(v, "0.#") + "°", v => Math.Abs(v - _s.MinDraft) < 1e-6, v => _s.MinDraft = v);
                        Number("最小壁厚", _s.MinWall, "mm", v => _s.MinWallPrint = v);
                        Number("生产数量", _s.MoldQuantity, "件", v => _s.MoldQuantity = Qty(v));
                        break;
                    }
                default:
                    {
                        bool resin = _s.Process == DfmProcess.Sla;
                        Label("打印机");
                        Machines(Printer.All.Where(p => p.Resin == resin).ToList(), _s.Printer, p => _s.Printer = p);
                        Label("材料");
                        Pills(PrintMaterial.All.Where(m => m.Resin == resin), m => m.Name, m => m == _s.PrintMaterial, m => _s.PrintMaterial = m);
                        Label("悬垂角（与竖直方向，超过就要支撑）");
                        Pills(new[] { 35.0, 45, 50, 55, 60 }, v => F(v, "0") + "°", v => Math.Abs(v - _s.OverhangDeg) < 1e-6, v => _s.OverhangDeg = v);
                        if (!resin)
                        {
                            Label("填充率");
                            Pills(new[] { 0.1, 0.15, 0.2, 0.4, 1.0 }, v => F(v * 100, "0") + "%", v => Math.Abs(v - _s.Infill) < 1e-6, v => _s.Infill = v);
                        }
                        Number("最小壁厚", _s.MinWall, "mm", v => { _s.MinWallPrint = v; });
                        Number("最小孔径", _s.MinHoleDia, "mm", v => _s.MinHoleUser = v);
                        break;
                    }
            }
            return sp;
        }

        // ================= 报告 =================

        /// <summary>生成报告内容：把当前视图按 300 dpi 渲染成两张图（斜视、另一个角度），放在 figDir。</summary>
        internal ReportDoc BuildReport(string figDir)
        {
            if (_result == null) throw new InvalidOperationException("还没有检查结果");
            Directory.CreateDirectory(figDir);
            var figs = new List<(string, string)>();
            var p = _result.Process;
            string cap1, cap2; double pitch2;
            switch (p)
            {
                case DfmProcess.Cnc: cap1 = "零件和装夹方向（箭头：刀具从这一侧下来；紫色倒扣，黄色内角 / 深腔，橙色薄壁）"; cap2 = "从另一侧看"; pitch2 = 35; break;
                case DfmProcess.Sls: cap1 = $"摆放方向「{_result.Orientation?.Label}」下的零件（橙色薄壁 / 翘曲，黄色缝隙，靛蓝封闭空腔）"; cap2 = "从另一侧看"; pitch2 = 35; break;
                case DfmProcess.Sheet: cap1 = "钣金件（青色折弯，紫色不等厚 / 斜边，橙色边距太小，浅蓝孔）"; cap2 = "从另一侧看"; pitch2 = 35; break;
                case DfmProcess.Cut: cap1 = "板件（紫色要另外铣削，黄色内角 / 窄槽，橙色窄筋 / 孔边距，浅蓝孔）"; cap2 = "从背面看"; pitch2 = -35; break;
                case DfmProcess.Mold: cap1 = "零件和开模方向（箭头；紫色倒扣，粉色拔模不足，深红厚壁，黄色尖内角）"; cap2 = "从上方看"; pitch2 = 55; break;
                default: cap1 = $"摆放方向「{_result.Orientation?.Label}」下的零件（红色要支撑，绿色贴平台，橙色薄壁）"; cap2 = "从下方看（悬垂面在下面）"; pitch2 = -20; break;
            }
            _view.ClearSelection();
            var a = Path.Combine(figDir, "可制造性_斜视.png");
            _view.SetAngles(-55, 25); Root.UpdateLayout(); _view.SaveImage(a, 300);
            figs.Add((a, cap1));
            var b = Path.Combine(figDir, "可制造性_另一侧.png");
            _view.SetAngles(125, pitch2); Root.UpdateLayout(); _view.SaveImage(b, 300);
            figs.Add((b, cap2));
            _view.ResetView();
            return DfmReport.Build(_mesh, _result, _s, figs);
        }

        string SaveReport(string docx)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(docx));
            var doc = BuildReport(Path.Combine(dir, Path.GetFileNameWithoutExtension(docx) + "_图"));
            DocxWriter.Save(doc, docx);
            MarkdownWriter.Save(doc, Path.ChangeExtension(docx, ".md"));
            _status.Text = "报告已保存：" + docx + "（同一文件夹里还有 300 dpi 的图、Markdown 和 CSV 数据）";
            _status.Foreground = Green;
            return docx;
        }

        void SaveReportDialog()
        {
            if (_result == null) return;
            var name = Path.GetFileNameWithoutExtension(_source) + "_可制造性报告.docx";
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = name, Filter = "Word 文档|*.docx", InitialDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory) };
            if (dlg.ShowDialog(_owner) != true) return;
            try
            {
                // 每份报告放进自己的文件夹（图、数据都在一起）
                var folder = Path.Combine(Path.GetDirectoryName(dlg.FileName), Path.GetFileNameWithoutExtension(dlg.FileName));
                Directory.CreateDirectory(folder);
                SaveReport(Path.Combine(folder, Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex) { _status.Text = "生成报告失败：" + ex.Message; _status.Foreground = Red; }
        }

        // ================= 给 AI 用的接口 =================

        internal static IEnumerable<ApiAction> ApiActions => new[]
        {
            new ApiAction("load", "读入零件（.sldprt / .step / .stp / .stl）并按当前工艺检查。工艺：fdm FDM 打印、sla 光固化、sls 尼龙粉末烧结（SLS / MJF）、slm 金属打印（SLM / DMLS）、cnc CNC 铣削、cut 板材切割（CNC 雕刻 / 激光 / 水刀切 2D 板，如碳板）、sheet 钣金（激光切割 + 折弯）、mold 注塑",
                          "{path, process?: " + DfmProcesses.Ids + "}"),
            new ApiAction("sample", "读入自带的测试件（悬垂、薄壁：FDM；CNC：CNC 铣削；钣金：钣金支架；碳板：3 mm 碳板机臂，CNC 雕刻）", "{id: 悬垂 | 薄壁 | CNC | 钣金 | 碳板}"),
            new ApiAction("check", "换工艺或参数重新检查（material / machine 按名称包含匹配，可选值见报错信息）",
                          "{process?: " + DfmProcesses.Ids + ", material?: 名称, machine?: 设备名称（打印机 / 烧结设备 / 金属打印设备）, overhang?: 度, min_wall?: mm, min_hole?: mm, infill?: 0–1, tilts?: bool, min_tool?: mm, hour_rate?: 元/小时（CNC，或板材切割的雕刻 / 水刀）, " +
                          "method?: router | laser | waterjet（板材切割）, cutter?: mm（雕刻机铣刀直径）, qty?: 数量（钣金 / 板材切割 / 注塑）, bend_price?: 元/刀, cut_price?: 元/m（钣金激光切割）, draft?: 度（注塑最小拔模角）}"),
            new ApiAction("orient", "3D 打印 / 尼龙烧结 / 金属打印：列出推荐的摆放方向；注塑：列出候选开模方向。给 index 就换成第几个（从 0 开始），或给 up 指定方向", "{index?, up?: [x,y,z]}"),
            new ApiAction("issues", "问题清单（严重程度、位置、数值、修改建议）", ""),
            new ApiAction("metrics", "估算的用料、时间、费用、装夹次数、折弯数、模具费等，以及工艺提示", ""),
            new ApiAction("locate", "在 3D 视图里定位第几个问题", "{index}"),
            new ApiAction("report", "生成 Word 报告（附 300 dpi 图、Markdown、CSV）", "{path: 输出的 .docx}"),
        };

        internal async Task<object> ApiAsync(string action, Dictionary<string, object> a)
        {
            switch (action)
            {
                case "load":
                    {
                        var p = a.ReqStr("path");
                        if (!File.Exists(p)) throw new ArgumentException("文件不存在：" + p);
                        if (a.Has("process")) SetProcess(ParseProcess(a.Str("process")));
                        await LoadPartAsync(p);
                        if (_result == null) throw new InvalidOperationException(_status.Text);
                        return Summary();
                    }
                case "sample":
                    {
                        var id = a.Str("id", "悬垂");
                        var sample = Samples.FirstOrDefault(x => id.IndexOf(x.id, StringComparison.OrdinalIgnoreCase) >= 0 || x.file.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (sample.file == null) throw new ArgumentException("没有这个示例：" + id + "（可选：" + string.Join("、", Samples.Select(x => x.id)) + "）");
                        await LoadSampleAsync(sample.file, sample.p);
                        if (_result == null) throw new InvalidOperationException(_status.Text);
                        return Summary();
                    }
                case "check":
                    {
                        RequirePart();
                        if (a.Has("process")) SetProcess(ParseProcess(a.Str("process")));
                        if (a.Has("overhang")) _s.OverhangDeg = a.ReqNum("overhang");
                        if (a.Has("min_wall")) { if (_s.Process == DfmProcess.Cnc) _s.MinWallCnc = a.ReqNum("min_wall"); else _s.MinWallPrint = a.ReqNum("min_wall"); }
                        if (a.Has("min_hole")) _s.MinHoleUser = a.ReqNum("min_hole");
                        if (a.Has("infill")) { var v = a.ReqNum("infill"); _s.Infill = v > 1 ? v / 100 : v; }
                        if (a.Has("tilts")) _s.FiveAxisTilts = a.Bool("tilts");
                        if (a.Has("min_tool")) _s.MinToolDia = a.ReqNum("min_tool");
                        if (a.Has("hour_rate")) { if (_s.Process == DfmProcess.Cut) _s.CutHourRate = a.ReqNum("hour_rate"); else _s.HourRate = a.ReqNum("hour_rate"); }
                        if (a.Has("method"))
                        {
                            var m = a.Str("method").ToLowerInvariant();
                            _s.CutMethod = m.StartsWith("laser") || m.Contains("激光") ? CutMethod.Laser : m.StartsWith("water") || m.Contains("水刀") ? CutMethod.Waterjet : CutMethod.Router;
                            _s.CutHourRate = double.NaN;
                        }
                        if (a.Has("cutter")) _s.CutterDia = a.ReqNum("cutter");
                        if (a.Has("qty")) { int q = Math.Max(1, a.Int("qty")); if (_s.Process == DfmProcess.Mold) _s.MoldQuantity = q; else _s.Quantity = q; }
                        if (a.Has("bend_price")) _s.BendPrice = a.ReqNum("bend_price");
                        if (a.Has("cut_price")) _s.SheetCutPrice = a.ReqNum("cut_price");
                        if (a.Has("draft")) _s.MinDraft = a.ReqNum("draft");
                        if (a.Has("material")) _s.SetMaterial(a.Str("material"));
                        if (a.Has("machine")) _s.SetMachine(a.Str("machine"));
                        if (a.Has("printer")) _s.SetMachine(a.Str("printer"));
                        await AnalyzeAsync();
                        return Summary();
                    }
                case "orient":
                    {
                        RequirePart();
                        if (!DfmProcesses.Printing(_s.Process) && _s.Process != DfmProcess.Mold) throw new InvalidOperationException(DfmReport.ShortName(_s.Process) + "没有摆放方向（CNC 的装夹方向看 metrics）");
                        if (a.Has("up")) { var v = a.List("up").Select(o => Convert.ToDouble(o, CultureInfo.InvariantCulture)).ToArray(); _up = new Vec3(v[0], v[1], v[2]).Normalized(); await AnalyzeAsync(); }
                        else if (a.Has("index"))
                        {
                            int i = a.Int("index");
                            if (i < 0 || i >= _result.Candidates.Count) throw new ArgumentException("index 超出范围（0–" + (_result.Candidates.Count - 1) + "）");
                            _up = _result.Candidates[i].Up; await AnalyzeAsync();
                        }
                        bool mold = _result.Process == DfmProcess.Mold;
                        return ApiArgs.Ret("current", _result.Orientation.Label, mold ? "pull" : "up", _result.Orientation.Up,
                            "candidates", _result.Candidates.Select((c, i) => mold
                                ? ApiArgs.Ret("index", i, "label", c.Label, "pull", c.Up, "undercut_mm2", c.SupportArea, "depth_mm", c.Height)
                                : ApiArgs.Ret("index", i, "label", c.Label, "up", c.Up, "support_cm3", c.SupportVolume / 1000, "height_mm", c.Height, "base_mm2", c.BaseArea, "score", c.Score)).ToList());
                    }
                case "issues":
                    RequirePart();
                    return _result.Issues.Select((x, i) => ApiArgs.Ret("index", i, "severity", DfmReport.Sev(x.Severity), "kind", x.Kind, "title", x.Title, "value", x.Value, "suggestion", x.Suggestion,
                        "location_mm", x.Location)).ToList();
                case "metrics":
                    RequirePart();
                    return _result.Metrics.Select(x => ApiArgs.Ret("name", x.Name, "value", x.Value, "note", x.Note))
                        .Concat(_result.Notes.Select(n => ApiArgs.Ret("name", "工艺提示", "value", n, "note", null))).ToList();
                case "locate":
                    RequirePart();
                    Locate(a.Int("index"));
                    return ApiArgs.Ret("located", a.Int("index"));
                case "report":
                    {
                        RequirePart();
                        var p = Path.GetFullPath(a.ReqStr("path"));
                        if (!p.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) p += ".docx";
                        Directory.CreateDirectory(Path.GetDirectoryName(p));
                        await Task.Delay(100);
                        return ApiArgs.Ret("docx", SaveReport(p), "markdown", Path.ChangeExtension(p, ".md"));
                    }
                default: throw new ArgumentException("未知操作：" + action);
            }
        }

        static DfmProcess ParseProcess(string s) => DfmProcesses.Parse(s) ?? throw new ArgumentException("不认识的工艺：" + s + "（可选：" + DfmProcesses.Ids + "）");

        void RequirePart() { if (_result == null) throw new InvalidOperationException("先读入零件（load 或 sample）"); }

        object Summary()
        {
            var r = _result;
            return ApiArgs.Ret("part", _source, "process", DfmReport.ProcessName(r.Process), "process_id", DfmProcesses.Id(r.Process), "material", _s.MaterialName,
                "errors", r.Issues.Count(i => i.Severity == Severity.Error), "warnings", r.Issues.Count(i => i.Severity == Severity.Warning), "infos", r.Issues.Count(i => i.Severity == Severity.Info),
                "orientation", DfmProcesses.Printing(r.Process) ? r.Orientation?.Label : null,
                "pull_direction", r.Process == DfmProcess.Mold ? r.Orientation?.Label : null,
                "thickness_mm", double.IsNaN(r.Thickness) ? (object)null : r.Thickness,
                "metrics", r.Metrics.Select(x => ApiArgs.Ret("name", x.Name, "value", x.Value)).ToList(),
                "notes", r.Notes,
                "issues", r.Issues.Take(30).Select((x, i) => ApiArgs.Ret("index", i, "severity", DfmReport.Sev(x.Severity), "title", x.Title, "suggestion", x.Suggestion)).ToList());
        }

        // ================= 自测（开发用） =================

        /// <summary>读入零件、按工艺检查、点第一个问题、生成报告。返回文字结果；截图由调用方保存。process 可以带材料：「cut:铝板 6061」。</summary>
        internal async Task<string> SelfTest(string part, string process, string reportDocx)
        {
            var parts = (process ?? "").Split(new[] { ':' }, 2);
            SetProcess(ParseProcess(parts[0]));
            if (parts.Length > 1) _s.SetMaterial(parts[1]);
            await LoadPartAsync(part);
            if (_result == null) return "失败：" + _status.Text;
            var sb = new StringBuilder();
            sb.AppendLine($"{Path.GetFileName(part)} · {DfmReport.ProcessName(_result.Process)} · {_s.MaterialName} · {_status.Text}");
            foreach (var m in _result.Metrics) sb.AppendLine($"  {m.Name}：{m.Value}");
            foreach (var n in _result.Notes) sb.AppendLine($"  提示：{n}");
            foreach (var x in _result.Issues) sb.AppendLine($"  [{DfmReport.Sev(x.Severity)}] {x.Title}｜{x.Value}");
            if (reportDocx != null) { SaveReport(reportDocx); sb.AppendLine("报告：" + reportDocx); }
            if (_result.Issues.Count > 0) Locate(0);
            // SWKIT_DFM_SCROLL=end：右侧面板滚到最下面再截图（检查估算和参数卡片的布局）
            if (System.Environment.GetEnvironmentVariable("SWKIT_DFM_SCROLL") == "end" && _side.Parent is ScrollViewer sv) { Root.UpdateLayout(); sv.ScrollToEnd(); }
            return sb.ToString();
        }
    }
}
