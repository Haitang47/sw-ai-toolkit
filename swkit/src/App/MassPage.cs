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
using System.Windows.Shapes;
using SolidWorks.Interop.sldworks;
using SwKit.Core;
using SwKit.Core.Mass;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>
    /// 「质量估算」页面：读装配体里每种零件的体积和数量，逐个 / 批量选材料（或填实测质量），算总质量、重心、按材料的小计。
    /// </summary>
    sealed class MassPage
    {
        public FrameworkElement Root { get; }
        public bool IsBusy { get; private set; }

        readonly Window _owner;
        readonly SwWorker _worker;
        readonly FrameworkElement _partBar, _empty, _busy, _work;
        readonly TextBlock _asmName, _asmPath, _status, _busyText, _busyDetail, _searchHint;
        readonly StackPanel _rowsPanel, _side;
        readonly Grid _header;
        readonly TextBox _search;
        readonly Button _bulkBtn, _csvBtn, _copyBtn;

        string _source, _loadError;
        MassAssembly _asm;
        readonly MassSettings _s = new MassSettings();
        readonly List<Row> _rows = new List<Row>();
        readonly HashSet<MassPart> _checked = new HashSet<MassPart>();
        readonly List<string> _recent = new List<string>();   // 这次用过的材料，放在「常用」最前面
        string _sort = "mass";
        Popup _popup;

        static readonly Brush Gray = Ui.Brush("#6E6E73"), Green = Ui.Brush("#248A3D"), Orange = Ui.Brush("#B25000"), Red = Ui.Brush("#D70015"),
            Text3 = Ui.Brush("#A1A1A6"), Accent = Ui.Brush("#0071E3"), LineBrush = Ui.Brush("#EDEDF0");
        static readonly string[] Common =
        {
            "6061-T6 铝", "7075-T6 铝", "5052 铝（钣金）", "6063 铝（型材）", "碳纤维板 3K（预浸料压制）", "碳纤维管（卷制）", "玻纤板 FR4", "Q235 低碳钢", "304 不锈钢",
            "TC4 钛合金（Ti-6Al-4V）", "PLA", "PETG", "PA-CF 尼龙碳纤维", "TPU 95A", "PA12（MJF）", "AlSi10Mg", "标准树脂", "POM 赛钢", "亚克力 PMMA", "H62 黄铜", "巴沙木",
        };
        const double ColCheck = 30;
        double _colMat = 190, _colMass = 120;   // 窗口窄时收窄（见 Relayout）

        sealed class Row
        {
            public MassPart P;
            public Grid Cols;
            public Border Root;
            public Border Check;
            public TextBlock Name, Sub, MatText, Mass, MassSub;
            public Button MatBtn;
            public Rectangle Bar;
        }

        public MassPage(Window owner, SwWorker worker)
        {
            _owner = owner; _worker = worker;
            Root = (FrameworkElement)Ui.Load("MassPage.xaml");
            _partBar = Root.Find<FrameworkElement>("PartBar");
            _empty = Root.Find<FrameworkElement>("EmptyState");
            _busy = Root.Find<FrameworkElement>("BusyState");
            _work = Root.Find<FrameworkElement>("WorkState");
            _asmName = Root.Find<TextBlock>("AsmName");
            _asmPath = Root.Find<TextBlock>("AsmPath");
            _status = Root.Find<TextBlock>("StatusText");
            _busyText = Root.Find<TextBlock>("BusyText");
            _busyDetail = Root.Find<TextBlock>("BusyDetail");
            _rowsPanel = Root.Find<StackPanel>("Rows");
            _side = Root.Find<StackPanel>("SidePanel");
            _header = Root.Find<Grid>("HeaderRow");
            _search = Root.Find<TextBox>("SearchBox");
            _searchHint = Root.Find<TextBlock>("SearchHint");
            _bulkBtn = Root.Find<Button>("BulkBtn");
            _csvBtn = Root.Find<Button>("CsvBtn");
            _copyBtn = Root.Find<Button>("CopyBtn");
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            ((RotateTransform)Root.Find<FrameworkElement>("Spinner").RenderTransform).BeginAnimation(RotateTransform.AngleProperty, spin);
            BuildHowSteps();
            BuildHeader();

            Root.Find<Button>("PickBtn").Click += (s, e) => Pick();
            Root.Find<Button>("ChangeBtn").Click += (s, e) => Pick();
            Root.Find<Button>("ReloadBtn").Click += async (s, e) => { if (_source != null) await LoadAsync(_source); };
            Root.Find<Button>("SampleBtn").Click += async (s, e) => await LoadSampleAsync();
            Root.Find<Button>("SampleLink").Click += async (s, e) => await LoadSampleAsync();
            _bulkBtn.Click += (s, e) => { if (_checked.Count > 0) OpenPicker(_bulkBtn, _checked.ToList()); };
            Root.Find<Button>("UnsetBtn").Click += (s, e) =>
            {
                if (_asm == null) return;
                _checked.Clear();
                foreach (var p in _asm.Parts.Where(p => !p.Excluded && double.IsNaN(MassCalc.UnitGrams(p, _s)))) _checked.Add(p);
                RefreshAll();
                _status.Text = _checked.Count > 0 ? $"已勾选 {_checked.Count} 种没选材料的零件，点「给勾选的零件设材料」一次设好" : "所有零件都有材料了";
                _status.Foreground = Gray;
            };
            _search.TextChanged += (s, e) => { _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Reorder(); };
            foreach (var (n, k) in new[] { ("SortMass", "mass"), ("SortName", "name"), ("SortQty", "qty") })
            {
                var kk = k;
                Root.Find<RadioButton>(n).Checked += (s, e) => { _sort = kk; Reorder(); };
            }
            _csvBtn.Click += (s, e) => ExportCsvDialog();
            _copyBtn.Click += (s, e) => CopyList();
            _work.SizeChanged += (s, e) => Relayout();
            Root.DragOver += (s, e) => { e.Effects = !IsBusy && FileFrom(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Root.Drop += async (s, e) => { var f = FileFrom(e.Data); if (f != null && !IsBusy) await LoadAsync(f); };
        }

        static string FileFrom(IDataObject d) =>
            (d.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(f => new[] { ".sldasm", ".sldprt", ".step", ".stp", ".stl" }.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()));

        void BuildHowSteps()
        {
            var host = Root.Find<UniformGrid>("HowSteps");
            var steps = new[]
            {
                ("1", "拖入装配体", "列出每一种零件、数量和体积；同一个零件用了几次就算几个。"),
                ("2", "选材料", "SolidWorks 里设过材料的自动对应；没设的按文件名猜。可以勾选多个零件一次设好，外购件直接填实测质量。"),
                ("3", "看总质量和重心", "按材料分的小计、重心位置；可以导出 CSV 或复制到表格。"),
            };
            for (int i = 0; i < steps.Length; i++)
            {
                var (n, t, d) = steps[i];
                var sp = new StackPanel();
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Accent, Child = new TextBlock { Text = n, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
                head.Children.Add(new TextBlock { Text = t, FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(head);
                sp.Children.Add(new TextBlock { Text = d, FontSize = 12, Foreground = Gray, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(30, 4, 0, 0) });
                host.Children.Add(new Border { Background = Brushes.White, BorderBrush = Ui.Brush("#E5E5EA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 12), Child = sp, Margin = new Thickness(i == 0 ? 0 : 8, 0, i == 2 ? 0 : 8, 0) });
            }
        }

        Grid Columns()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColCheck) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 90 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_colMat) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_colMass) });
            return g;
        }

        /// <summary>按可用宽度调整：窄窗口时右侧栏 270、材料列 150、质量列 96（否则质量列会被挤出去）。</summary>
        void Relayout()
        {
            var w = _work.ActualWidth;
            if (w <= 0) return;
            var grid = (Grid)_work;
            double side = w < 900 ? 270 : 330;
            if (Math.Abs(grid.ColumnDefinitions[1].Width.Value - side) > 0.5) grid.ColumnDefinitions[1].Width = new GridLength(side);
            double table = w - side - 18;
            double mat = table < 600 ? 150 : 190, mass = table < 600 ? 96 : 120;
            if (Math.Abs(mat - _colMat) < 0.5 && Math.Abs(mass - _colMass) < 0.5) return;
            _colMat = mat; _colMass = mass;
            void Apply(Grid g) { g.ColumnDefinitions[2].Width = new GridLength(mat); g.ColumnDefinitions[3].Width = new GridLength(mass); }
            Apply(_header);
            foreach (var r in _rows) Apply(r.Cols);
        }

        void BuildHeader()
        {
            var g = Columns();
            foreach (var cd in g.ColumnDefinitions) _header.ColumnDefinitions.Add(new ColumnDefinition { Width = cd.Width, MinWidth = cd.MinWidth });
            var all = CheckBox(false, () =>
            {
                var vis = VisibleRows().Select(r => r.P).ToList();
                bool allOn = vis.Count > 0 && vis.All(p => _checked.Contains(p));
                foreach (var p in vis) { if (allOn) _checked.Remove(p); else _checked.Add(p); }
                RefreshAll();
            });
            all.ToolTip = "全选 / 全不选（只对当前搜索出来的零件）";
            _header.Children.Add(all);
            void H(string t, int col, HorizontalAlignment ha)
            {
                var tb = new TextBlock { Text = t, FontSize = 11.5, Foreground = Text3, HorizontalAlignment = ha, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(col == 1 ? 6 : 0, 0, col == 3 ? 4 : 0, 0) };
                Grid.SetColumn(tb, col); _header.Children.Add(tb);
            }
            H("零件 × 数量", 1, HorizontalAlignment.Left); H("材料", 2, HorizontalAlignment.Left); H("质量（小计）", 3, HorizontalAlignment.Right);
        }

        /// <summary>小复选框（和整体风格一致的圆角方块）。</summary>
        static Border CheckBox(bool on, Action click)
        {
            var b = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.4), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            b.Child = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            SetCheck(b, on);
            b.MouseLeftButtonUp += (s, e) => { click(); e.Handled = true; };
            return b;
        }

        static void SetCheck(Border b, bool on)
        {
            b.Background = on ? Accent : Brushes.White;
            b.BorderBrush = on ? Accent : Ui.Brush("#C7C7CC");
            b.Child.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }

        // ================= 读入 =================

        void Pick()
        {
            if (IsBusy) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择装配体或零件", Filter = "装配体 / 零件|*.sldasm;*.sldprt;*.step;*.stp;*.stl|SolidWorks 装配体|*.sldasm|SolidWorks 零件|*.sldprt|STEP|*.step;*.stp|STL|*.stl" };
            if (dlg.ShowDialog(_owner) != true) return;
            _ = LoadAsync(dlg.FileName);
        }

        internal static string SamplePath()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[] { System.IO.Path.Combine(dir, "示例", "质量估算示例.step"), System.IO.Path.Combine(dir, "..", "tools", "samples", "质量估算示例.step") })
                if (File.Exists(c)) return System.IO.Path.GetFullPath(c);
            return null;
        }

        async Task LoadSampleAsync()
        {
            var p = SamplePath();
            if (p == null) { _status.Text = "找不到示例文件（应该在程序旁边的「示例」文件夹里）"; _status.Foreground = Red; return; }
            await LoadAsync(p);
        }

        void Phase(string text) => Root.Dispatcher.BeginInvoke(new Action(() => { _busyDetail.Text = text; _status.Text = text; _status.Foreground = Gray; }));

        public async Task LoadAsync(string path)
        {
            if (IsBusy) return;
            path = System.IO.Path.GetFullPath(path);
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            bool reload = _asm != null && string.Equals(_source, path, StringComparison.OrdinalIgnoreCase);
            var old = reload ? _asm.Parts.ToDictionary(p => p.Key) : null;
            _source = path;
            _asmName.Text = System.IO.Path.GetFileName(path);
            _asmPath.Text = System.IO.Path.GetDirectoryName(path);
            Ui.Fade(_partBar, true);
            Ui.Fade(_empty, false); Ui.Fade(_work, false); Ui.Fade(_busy, true);
            IsBusy = true; _busyText.Text = ext == ".sldasm" ? "正在读取装配体" : "正在读取";
            _csvBtn.IsEnabled = _copyBtn.IsEnabled = false;
            try
            {
                MassAssembly asm;
                if (ext == ".sldasm" || ext == ".sldprt")
                {
                    asm = await _worker.Run(() =>
                    {
                        var s = _worker.Acquire(m => Phase(m.Contains("启动") ? "正在启动后台 SolidWorks（第一次约 20–60 秒）" : m), CancellationToken.None);
                        try
                        {
                            Phase(ext == ".sldasm" ? "正在打开装配体（只读，大装配体需要一些时间）" : "正在打开零件（只读）");
                            int e = 0, w = 0;
                            var already = s.App.GetOpenDocumentByName(path) as ModelDoc2;
                            var doc = already ?? s.App.OpenDoc6(path, ext == ".sldasm" ? 2 : 1, 1 | 2, "", ref e, ref w);
                            if (doc == null) throw new InvalidOperationException("打开失败：" + ExportRunner.LoadErrorText(e));
                            try { Phase("正在读取每个零件的体积和材料"); return MassReader.ReadSw(s.App, doc, Phase); }
                            finally { if (already == null) try { s.App.CloseDoc(doc.GetTitle()); } catch { } }
                        }
                        finally { _worker.Release(); }
                    });
                }
                else
                {
                    using (HeavyWork.Begin())
                        asm = await Task.Run(() => MassReader.ReadMesh(path, Phase));
                }
                // 选择：重新读取时沿用这次的；否则用上次存下的，再自动猜
                if (old != null)
                    foreach (var p in asm.Parts)
                        if (old.TryGetValue(p.Key, out var o)) { p.Material = o.Material; p.MaterialWhy = o.MaterialWhy; p.MeasuredGrams = o.MeasuredGrams; p.Excluded = o.Excluded; }
                MassStore.Apply(asm.Parts.Where(p => p.Material == null && !p.MeasuredGrams.HasValue));
                MassStore.AutoAssign(asm.Parts);
                _asm = asm; _loadError = null;
                _checked.Clear();
                IsBusy = false;
                BuildRows();
                Ui.Fade(_busy, false); Ui.Fade(_work, true);
                _csvBtn.IsEnabled = _copyBtn.IsEnabled = true;
                var sum = MassCalc.Summarize(asm.Parts, _s);
                _status.Text = $"读取了 {asm.Parts.Count} 种零件、{asm.Instances} 个" + (sum.Unassigned > 0 ? $"；还有 {sum.Unassigned} 种没选材料，点材料按钮选，或者勾选几个一起设" : "；材料都有了");
                _status.Foreground = sum.Unassigned > 0 ? Orange : Green;
            }
            catch (Exception ex)
            {
                IsBusy = false;
                Ui.Fade(_busy, false); Ui.Fade(_asm == null ? _empty : _work, true);
                _status.Text = "读取失败：" + ex.Message; _status.Foreground = Red;
                _loadError = _status.Text;
            }
        }

        // ================= 零件表 =================

        void BuildRows()
        {
            _rows.Clear();
            foreach (var p in _asm.Parts) _rows.Add(MakeRow(p));
            Reorder();
        }

        Row MakeRow(MassPart p)
        {
            var r = new Row { P = p };
            var g = Columns();
            r.Cols = g;
            g.Margin = new Thickness(14, 0, 14, 0);
            r.Check = CheckBox(false, () => { if (!_checked.Remove(p)) _checked.Add(p); RefreshChecks(); });
            g.Children.Add(r.Check);

            var nameSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) };
            r.Name = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            r.Sub = new TextBlock { FontSize = 11.5, Foreground = Text3, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            nameSp.Children.Add(r.Name); nameSp.Children.Add(r.Sub);
            Grid.SetColumn(nameSp, 1); g.Children.Add(nameSp);

            r.MatText = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var chevron = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 9, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            var dp = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(chevron, Dock.Right); dp.Children.Add(chevron); dp.Children.Add(r.MatText);
            r.MatBtn = new Button { Style = (Style)Application.Current.FindResource("Btn.Secondary"), Content = dp, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
            r.MatBtn.Click += (s, e) => OpenPicker(r.MatBtn, new List<MassPart> { p });
            Grid.SetColumn(r.MatBtn, 2); g.Children.Add(r.MatBtn);

            var massSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            r.Mass = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            r.MassSub = new TextBlock { FontSize = 11, Foreground = Text3, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
            massSp.Children.Add(r.Mass); massSp.Children.Add(r.MassSub);
            Grid.SetColumn(massSp, 3); g.Children.Add(massSp);

            // 占比条（贴在行底部）
            var host = new Grid();
            host.Children.Add(g);
            r.Bar = new Rectangle { Height = 2, Fill = Ui.Brush("#FF9F0A"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 0, 0), RadiusX = 1, RadiusY = 1, Opacity = 0.75 };
            host.Children.Add(r.Bar);
            r.Root = new Border { Child = host, MinHeight = 50, Padding = new Thickness(0, 6, 0, 6), BorderBrush = LineBrush, BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
            r.Root.MouseEnter += (s, e) => { if (!_checked.Contains(p)) r.Root.Background = Ui.Brush("#FAFAFC"); };
            r.Root.MouseLeave += (s, e) => { if (!_checked.Contains(p)) r.Root.Background = Brushes.Transparent; };
            UpdateRow(r);
            return r;
        }

        static string G(double g) => double.IsNaN(g) ? "—" : g >= 1000 ? (g / 1000).ToString("0.###", CultureInfo.InvariantCulture) + " kg" : g.ToString(g >= 100 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + " g";
        static string N(double v, string f) => v.ToString(f, CultureInfo.InvariantCulture);

        void UpdateRow(Row r, double total = double.NaN)
        {
            var p = r.P;
            r.Name.Text = p.Quantity > 1 ? $"{p.Name}  × {p.Quantity}" : p.Name;
            r.Name.Foreground = p.Excluded ? Text3 : Brushes.Black;
            r.Name.TextDecorations = p.Excluded ? TextDecorations.Strikethrough : null;
            var sz = p.Size * 1000;
            var bits = new List<string> { $"{N(p.Volume * 1e6, p.Volume * 1e6 >= 100 ? "0.#" : "0.###")} cm³" };
            if (sz.X > 0 || sz.Y > 0) bits.Add($"{N(sz.X, "0.#")}×{N(sz.Y, "0.#")}×{N(sz.Z, "0.#")} mm");
            if (p.Note != null) bits.Add("⚠ " + p.Note);
            else if (p.MaterialWhy != null && p.MaterialWhy != "手动选择") bits.Add(p.MaterialWhy);
            r.Sub.Text = string.Join(" · ", bits);
            r.Sub.ToolTip = r.Sub.Text;
            r.Name.ToolTip = p.File + (string.IsNullOrEmpty(p.Config) ? "" : "\n配置：" + p.Config);

            double unit = MassCalc.UnitGrams(p, _s);
            r.MatText.Text = p.MeasuredGrams.HasValue && p.Material == null ? "（按实测质量）" : MassCalc.MaterialLabel(p);
            r.MatText.Foreground = p.Material == null && !p.MeasuredGrams.HasValue ? Orange : Brushes.Black;
            r.MatBtn.ToolTip = p.Material == null ? "还没选材料，点这里选" : MassCalc.IsFdm(p) ? $"FDM 打印：外壳 {N(_s.ShellMm, "0.#")} mm + 填充 {N(_s.Infill * 100, "0")}%" : $"密度 {N(MassCalc.Density(p), "0.###")} g/cm³";
            if (p.Excluded) { r.Mass.Text = "不计入"; r.Mass.Foreground = Text3; r.MassSub.Text = double.IsNaN(unit) ? "" : "单件 " + G(unit); }
            else if (double.IsNaN(unit)) { r.Mass.Text = "未选材料"; r.Mass.Foreground = Orange; r.MassSub.Text = ""; }
            else
            {
                r.Mass.Text = G(unit * p.Quantity); r.Mass.Foreground = Brushes.Black;
                r.MassSub.Text = p.MeasuredGrams.HasValue ? (p.Quantity > 1 ? "实测，单件 " + G(unit) : "实测")
                               : p.Quantity > 1 ? "单件 " + G(unit)
                               : MassCalc.IsFdm(p) ? $"填充 {N(_s.Infill * 100, "0")}%" : "";
            }
            bool on = _checked.Contains(p);
            SetCheck(r.Check, on);
            r.Root.Background = on ? Ui.Brush("#EAF3FE") : Brushes.Transparent;
            if (double.IsNaN(total)) total = MassCalc.Summarize(_asm.Parts, _s).TotalGrams;
            double frac = total > 0 && !double.IsNaN(unit) && !p.Excluded ? unit * p.Quantity / total : 0;
            r.Bar.Width = Math.Max(0, frac * Math.Max(0, _rowsPanel.ActualWidth - 28));
            r.Bar.ToolTip = $"占总质量 {frac:P1}";
        }

        IEnumerable<Row> VisibleRows()
        {
            var q = _search.Text.Trim();
            return _rows.Where(r => q.Length == 0 || r.P.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || MassCalc.MaterialLabel(r.P).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void Reorder()
        {
            if (_asm == null) return;
            IEnumerable<Row> rows = VisibleRows();
            rows = _sort == "name" ? rows.OrderBy(r => r.P.Name, StringComparer.CurrentCulture)
                 : _sort == "qty" ? rows.OrderByDescending(r => r.P.Quantity).ThenBy(r => r.P.Name)
                 : rows.OrderByDescending(r => { var g = MassCalc.SubtotalGrams(r.P, _s); return double.IsNaN(g) ? double.MaxValue : g; });   // 没选材料的放最前面
            _rowsPanel.Children.Clear();
            foreach (var r in rows) _rowsPanel.Children.Add(r.Root);
            if (_rowsPanel.Children.Count == 0) _rowsPanel.Children.Add(new TextBlock { Text = "没有匹配的零件", Foreground = Text3, Margin = new Thickness(16, 16, 0, 0) });
            RefreshAll();
        }

        void RefreshChecks()
        {
            foreach (var r in _rows) { bool on = _checked.Contains(r.P); SetCheck(r.Check, on); r.Root.Background = on ? Ui.Brush("#EAF3FE") : Brushes.Transparent; }
            var vis = VisibleRows().Select(r => r.P).ToList();
            SetCheck((Border)_header.Children[0], vis.Count > 0 && vis.All(p => _checked.Contains(p)));
            _bulkBtn.IsEnabled = _checked.Count > 0;
            _bulkBtn.Content = _checked.Count > 0 ? $"给勾选的 {_checked.Count} 种零件设材料…" : "给勾选的零件设材料…";
        }

        void RefreshAll()
        {
            if (_asm == null) return;
            double total = MassCalc.Summarize(_asm.Parts, _s).TotalGrams;
            Root.Dispatcher.BeginInvoke(new Action(() => { foreach (var r in _rows) UpdateRow(r, total); }), System.Windows.Threading.DispatcherPriority.Loaded);
            foreach (var r in _rows) UpdateRow(r, total);
            RefreshChecks();
            BuildSide();
        }

        /// <summary>改了零件的选择：保存、刷新。</summary>
        void Changed()
        {
            MassStore.Save(_asm.Parts);
            RefreshAll();
            var sum = MassCalc.Summarize(_asm.Parts, _s);
            _status.Text = $"总质量 {G(sum.TotalGrams)}" + (sum.Unassigned > 0 ? $"；还有 {sum.Unassigned} 种零件没选材料（没有计入）" : "");
            _status.Foreground = sum.Unassigned > 0 ? Orange : Gray;
        }

        // ================= 右侧：总质量、重心、按材料、FDM 设置 =================

        void Section(string t) => _side.Children.Add(new TextBlock { Text = t, Style = (Style)Application.Current.FindResource("T.Section") });
        Border Group(UIElement child) => new Border { Style = (Style)Application.Current.FindResource("Group"), Child = child };
        static TextBlock Caption(string text, Brush color, Thickness margin) => new TextBlock { Text = text, FontSize = 12, Foreground = color, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = margin };

        void BuildSide()
        {
            _side.Children.Clear();
            if (_asm == null) return;
            var sum = MassCalc.Summarize(_asm.Parts, _s);

            Section("总质量");
            var t = new StackPanel();
            var big = new TextBlock { FontSize = 30, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont") };
            big.Text = sum.TotalGrams >= 1000 ? N(sum.TotalGrams / 1000, "0.###") + " kg" : N(sum.TotalGrams, "0.#") + " g";
            t.Children.Add(big);
            if (sum.TotalGrams >= 1000) t.Children.Add(Caption(N(sum.TotalGrams, "#,0.#") + " g", Gray, new Thickness(0, 0, 0, 0)));
            t.Children.Add(Caption($"{_asm.Parts.Count} 种零件、{_asm.Instances} 个" + (sum.Measured > 0 ? $"，其中 {sum.Measured} 种按实测质量" : "") + (sum.Excluded > 0 ? $"，{sum.Excluded} 种不计入" : ""), Gray, new Thickness(0, 4, 0, 0)));
            if (sum.Unassigned > 0)
            {
                var warn = new Border { Background = Ui.Brush("#FFF4E5"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 10, 0, 0) };
                var ws = new StackPanel();
                ws.Children.Add(new TextBlock { Text = $"还有 {sum.Unassigned} 种零件没选材料，没有计入总质量", FontSize = 12, Foreground = Orange, TextWrapping = TextWrapping.Wrap });
                var pick = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "勾选它们，一起设材料", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
                pick.Click += (s, e) => Root.Find<Button>("UnsetBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ws.Children.Add(pick);
                warn.Child = ws;
                t.Children.Add(warn);
            }
            _side.Children.Add(Group(t));

            Section("重心");
            var c = new StackPanel();
            if (sum.Com is Vec3 com)
            {
                c.Children.Add(new TextBlock { Text = $"X {N(com.X * 1000, "0.#")}   Y {N(com.Y * 1000, "0.#")}   Z {N(com.Z * 1000, "0.#")}  mm", FontSize = 14, FontWeight = FontWeights.SemiBold });
                c.Children.Add(Caption((_asm.File.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? "装配体坐标系（和 SolidWorks 里一致）" : "零件 / 文件的坐标系") + "。只算已经有质量的零件；FDM 打印件按实心形状的质心近似。", Gray, new Thickness(0, 4, 0, 0)));
            }
            else c.Children.Add(Caption(sum.Counted == 0 ? "选了材料以后显示。" : "有零件读不到位置（或填了实测质量的外购件位置未知），算不出重心。", Gray, new Thickness(0)));
            _side.Children.Add(Group(c));

            if (sum.ByMaterial.Count > 0)
            {
                Section("按材料");
                var b = new StackPanel();
                foreach (var (mat, grams, n) in sum.ByMaterial.Take(12))
                {
                    var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var name = new TextBlock { Text = mat, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = $"{mat}：{n} 种零件" };
                    var val = new TextBlock { Text = $"{G(grams)}  {grams / Math.Max(sum.TotalGrams, 1e-9):P0}", FontSize = 12.5, Foreground = Gray, Margin = new Thickness(8, 0, 0, 0) };
                    Grid.SetColumn(val, 1);
                    row.Children.Add(name); row.Children.Add(val);
                    b.Children.Add(row);
                    var track = new Grid { Height = 4, Margin = new Thickness(0, 0, 0, 4) };
                    track.Children.Add(new Border { Background = Ui.Brush("#EEEEF1"), CornerRadius = new CornerRadius(2) });
                    var fill = new Border { Background = Ui.Brush("#FF9F0A"), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
                    double frac = grams / Math.Max(sum.TotalGrams, 1e-9);
                    track.SizeChanged += (s, e) => fill.Width = Math.Max(2, frac * track.ActualWidth);
                    track.Children.Add(fill);
                    b.Children.Add(track);
                }
                if (sum.ByMaterial.Count > 12) b.Children.Add(Caption($"还有 {sum.ByMaterial.Count - 12} 种材料……", Text3, new Thickness(0, 2, 0, 0)));
                _side.Children.Add(Group(b));
            }

            Section("FDM 打印件");
            var f = new StackPanel();
            f.Children.Add(Caption("只影响选了「FDM 打印」类材料的零件：质量 = 外壳（表面积 × 外壳厚度）+ 内部 × 填充率。", Gray, new Thickness(0, 0, 0, 8)));
            f.Children.Add(new TextBlock { Text = "填充率", FontSize = 12.5, Foreground = Gray, Margin = new Thickness(0, 0, 0, 4) });
            f.Children.Add(Pills(new[] { 0.1, 0.15, 0.2, 0.3, 0.5, 1.0 }, v => $"{v * 100:0}%", _s.Infill, v => { _s.Infill = v; Changed(); }));
            f.Children.Add(new TextBlock { Text = "外壳厚度（墙 + 顶底）", FontSize = 12.5, Foreground = Gray, Margin = new Thickness(0, 8, 0, 4) });
            f.Children.Add(Pills(new[] { 0.4, 0.8, 1.2, 1.6, 2.0 }, v => $"{v:0.0} mm", _s.ShellMm, v => { _s.ShellMm = v; Changed(); }));
            _side.Children.Add(Group(f));

            if (_asm.Notes.Count > 0)
            {
                Section("说明");
                var n = new StackPanel();
                foreach (var note in _asm.Notes) n.Children.Add(Caption("· " + note, Gray, new Thickness(0, 1, 0, 1)));
                _side.Children.Add(Group(n));
            }
        }

        WrapPanel Pills(double[] values, Func<double, string> label, double cur, Action<double> set)
        {
            var w = new WrapPanel();
            var g = "p" + Guid.NewGuid().ToString("N");
            foreach (var v in values)
            {
                var rb = new RadioButton { Style = (Style)Application.Current.FindResource("Pill"), GroupName = g, Content = label(v), IsChecked = Math.Abs(v - cur) < 1e-9 };
                var vv = v;
                rb.Checked += (s, e) => { if (Math.Abs(vv - cur) > 1e-9) Root.Dispatcher.BeginInvoke(new Action(() => set(vv))); };
                w.Children.Add(rb);
            }
            return w;
        }

        // ================= 材料选择弹窗 =================

        void OpenPicker(FrameworkElement anchor, List<MassPart> parts)
        {
            if (_popup != null) _popup.IsOpen = false;
            var single = parts.Count == 1 ? parts[0] : null;
            var card = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), BorderBrush = Ui.Brush("#E3E3E8"), BorderThickness = new Thickness(1), Padding = new Thickness(14), Width = 560, Margin = new Thickness(10), Effect = (System.Windows.Media.Effects.Effect)Application.Current.FindResource("Shadow.Card") };
            var root = new DockPanel();
            card.Child = root;

            var title = new TextBlock { Text = single != null ? single.Name : $"给 {parts.Count} 种零件设材料", FontSize = 14, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);

            var search = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Margin = new Thickness(0, 0, 0, 10) };
            var hint = new TextBlock { Text = "搜索材料（例如 碳纤维、5052、PLA、钛）", FontSize = 12.5, Foreground = Text3, Margin = new Thickness(11, 0, 0, 10), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            var sg = new Grid(); sg.Children.Add(search); sg.Children.Add(hint);
            DockPanel.SetDock(sg, Dock.Top); root.Children.Add(sg);

            // 单个零件：实测质量、不计入
            if (single != null)
            {
                var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                bottom.Children.Add(new Border { Height = 1, Background = LineBrush, Margin = new Thickness(0, 0, 0, 10) });
                var mrow = new StackPanel { Orientation = Orientation.Horizontal };
                mrow.Children.Add(new TextBlock { Text = "实测单件质量", FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                var mbox = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Width = 90, TextAlignment = TextAlignment.Right, Text = single.MeasuredGrams.HasValue ? N(single.MeasuredGrams.Value, "0.###") : "" };
                mrow.Children.Add(mbox);
                mrow.Children.Add(new TextBlock { Text = "g", FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) });
                var useM = new Button { Style = (Style)Application.Current.FindResource("Btn.Secondary"), Content = "用这个质量", VerticalAlignment = VerticalAlignment.Center };
                useM.Click += (s, e) =>
                {
                    var txt = mbox.Text.Trim();
                    if (txt.Length == 0) single.MeasuredGrams = null;
                    else if (double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g >= 0) single.MeasuredGrams = g;
                    else { mbox.BorderBrush = Red; return; }
                    _popup.IsOpen = false; Changed();
                };
                mbox.KeyDown += (s, e) => { if (e.Key == Key.Enter) useM.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                mrow.Children.Add(useM);
                bottom.Children.Add(mrow);
                bottom.Children.Add(Caption("外购件（电机、电池、电调、螺丝包……）或建模简化过的零件，直接填称出来的质量，就不按材料算了；清空再点按钮恢复按材料算。", Text3, new Thickness(0, 4, 0, 0)));
                var ex = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = single.Excluded ? "恢复计入总质量" : "不计入总质量（参考件、示意件）", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), FontSize = 12.5 };
                ex.Click += (s, e) => { single.Excluded = !single.Excluded; _popup.IsOpen = false; Changed(); };
                bottom.Children.Add(ex);
                DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
            }

            var body = new Grid { Height = 300 };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cats = new StackPanel();
            var catScroll = new ScrollViewer { Content = cats, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 10, 0) };
            body.Children.Add(catScroll);
            var list = new StackPanel();
            var listScroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(listScroll, 1); body.Children.Add(listScroll);
            root.Children.Add(body);

            string cat = "常用";
            var current = single?.Material;
            var catButtons = new List<(string name, Border b)>();
            void Choose(string material)
            {
                foreach (var p in parts)
                {
                    p.Material = material; p.MaterialWhy = "手动选择";
                    if (material != null) p.MeasuredGrams = null;   // 选了材料就按材料算（否则实测值还盖着，看起来像没变）
                }
                if (material != null && material != MassCalc.SwChoice) { _recent.Remove(material); _recent.Insert(0, material); if (_recent.Count > 6) _recent.RemoveAt(6); }
                _popup.IsOpen = false;
                Changed();
            }
            Border Item(string name, string right, string note, bool on, Action click)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = on ? Accent : Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis });
                if (note != null) sp.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Text3, TextTrimming = TextTrimming.CharacterEllipsis });
                g.Children.Add(sp);
                var rt = new TextBlock { Text = right, FontSize = 12, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                Grid.SetColumn(rt, 1); g.Children.Add(rt);
                var b = new Border { Child = g, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Background = on ? Ui.Brush("#EAF3FE") : Brushes.Transparent };
                b.MouseEnter += (s, e) => { if (!on) b.Background = Ui.Brush("#F2F2F5"); };
                b.MouseLeave += (s, e) => { if (!on) b.Background = Brushes.Transparent; };
                b.MouseLeftButtonUp += (s, e) => click();
                return b;
            }
            void Fill()
            {
                list.Children.Clear();
                var q = search.Text.Trim();
                IEnumerable<MassMaterial> mats;
                if (q.Length > 0) mats = MassMaterials.All.Where(m => m.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || m.Category.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (m.Note ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                else if (cat == "常用") mats = _recent.Concat(Common).Distinct().Select(MassMaterials.Find).Where(m => m != null);
                else mats = MassMaterials.All.Where(m => m.Category == cat);
                // SolidWorks 里设的材料（单个零件、或批量里都有 SolidWorks 材料时）
                if (q.Length == 0 && cat == "常用" && parts.All(p => p.SwMaterial != null && p.SwDensity > 0))
                    list.Children.Add(Item(single != null ? $"SolidWorks：{single.SwMaterial}" : "按各自在 SolidWorks 里设的材料", single != null ? $"{N(single.SwDensity / 1000, "0.###")} g/cm³" : "", "用 SolidWorks 里的密度", current == MassCalc.SwChoice, () => Choose(MassCalc.SwChoice)));
                foreach (var m in mats)
                    list.Children.Add(Item(m.Name, $"{N(m.Density, "0.###")} g/cm³", (m.Fdm ? "按外壳 + 填充率算" : null) ?? (q.Length > 0 || cat == "常用" ? m.Category + (m.Note != null ? " · " + m.Note : "") : m.Note), m.Name == current, () => Choose(m.Name)));
                if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = "没有找到。可以先选密度接近的材料，或者在下面填实测质量。", Foreground = Text3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) });
                if (q.Length == 0 && parts.Any(p => p.Material != null))
                    list.Children.Add(Item("清除材料（不计入，直到选好）", "", null, false, () => Choose(null)));
                foreach (var (name, b) in catButtons)
                {
                    bool on = q.Length == 0 && name == cat;
                    b.Background = on ? Ui.Brush("#EAF3FE") : Brushes.Transparent;
                    ((TextBlock)b.Child).Foreground = on ? Accent : Brushes.Black;
                }
            }
            foreach (var c in new[] { "常用" }.Concat(MassMaterials.Categories))
            {
                var cc = c;
                var b = new Border { Child = new TextBlock { Text = c, FontSize = 12.5 }, Padding = new Thickness(10, 6, 8, 6), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand };
                b.MouseLeftButtonUp += (s, e) => { cat = cc; search.Text = ""; Fill(); };
                catButtons.Add((c, b));
                cats.Children.Add(b);
            }
            // 当前材料所在的类别
            if (current != null && current != MassCalc.SwChoice && MassMaterials.Find(current) is MassMaterial cm && !Common.Contains(cm.Name) && !_recent.Contains(cm.Name)) cat = cm.Category;
            search.TextChanged += (s, e) => { hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); };
            Fill();

            _popup = new Popup { Child = card, StaysOpen = false, AllowsTransparency = true, PlacementTarget = anchor, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.Fade };
            _popup.Opened += (s, e) => search.Focus();
            _popup.IsOpen = true;
        }

        // ================= 导出 =================

        void ExportCsvDialog()
        {
            if (_asm == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = _asm.Name + "_质量清单.csv", Filter = "CSV（Excel 可以直接打开）|*.csv", InitialDirectory = System.IO.Path.GetDirectoryName(_source) };
            if (dlg.ShowDialog(_owner) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, MassCalc.ToCsv(_asm, _s), new UTF8Encoding(true));
                _status.Text = "已导出：" + dlg.FileName; _status.Foreground = Green;
            }
            catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; _status.Foreground = Red; }
        }

        string ListText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("零件\t数量\t材料\t单件/g\t小计/g");
            foreach (var r in _rows.OrderByDescending(r => MassCalc.SubtotalGrams(r.P, _s)))
            {
                var p = r.P; var u = MassCalc.UnitGrams(p, _s);
                sb.AppendLine($"{p.Name}\t{p.Quantity}\t{(p.MeasuredGrams.HasValue ? "实测" : MassCalc.MaterialLabel(p))}\t{(double.IsNaN(u) ? "" : N(u, "0.##"))}\t{(p.Excluded ? "不计入" : double.IsNaN(u) ? "" : N(u * p.Quantity, "0.##"))}");
            }
            var sum = MassCalc.Summarize(_asm.Parts, _s);
            sb.AppendLine($"合计\t{_asm.Instances}\t\t\t{N(sum.TotalGrams, "0.##")}");
            return sb.ToString();
        }

        void CopyList()
        {
            if (_asm == null) return;
            try { Clipboard.SetText(ListText()); _status.Text = "已复制，可以直接粘贴到 Excel / 飞书 / 微信"; _status.Foreground = Green; }
            catch (Exception ex) { _status.Text = "复制失败：" + ex.Message; _status.Foreground = Red; }
        }

        // ================= AI 接口 =================

        internal static IEnumerable<ApiAction> ApiActions => new[]
        {
            new ApiAction("load", "读入装配体或零件（.sldasm / .sldprt / .step / .stl），列出每种零件", "{path}"),
            new ApiAction("sample", "读入自带的示例装配体（STEP）", ""),
            new ApiAction("parts", "零件清单：编号、名称、数量、体积、材料、单件和小计质量", ""),
            new ApiAction("materials", "可选的材料和密度", "{category?: 类别}"),
            new ApiAction("set_material", "给零件设材料", "{parts: [编号…] | \"all\" | \"unassigned\", material: 材料名（\"sw\" = 用 SolidWorks 里的材料；null = 清除）}"),
            new ApiAction("set_measured", "填实测单件质量（外购件），grams 为 null 时清除", "{part: 编号, grams}"),
            new ApiAction("exclude", "不计入 / 恢复计入总质量", "{parts: [编号…], excluded: bool}"),
            new ApiAction("settings", "FDM 打印件的填充率和外壳厚度", "{infill?: 0–1 或百分数, shell_mm?: 0.8}"),
            new ApiAction("summary", "总质量、重心、按材料的小计、没选材料的零件", ""),
            new ApiAction("export_csv", "导出零件清单 CSV", "{path}"),
        };

        internal async Task<object> ApiAsync(string action, Dictionary<string, object> a)
        {
            if (IsBusy) throw new InvalidOperationException("正在读取，稍后再试");
            switch (action)
            {
                case "load":
                    {
                        var p = a.ReqStr("path");
                        if (!File.Exists(p)) throw new ArgumentException("文件不存在：" + p);
                        await LoadAsync(p);
                        if (_asm == null || _loadError != null) throw new InvalidOperationException(_loadError ?? _status.Text);
                        return SummaryInfo();
                    }
                case "sample":
                    await LoadSampleAsync();
                    if (_asm == null) throw new InvalidOperationException(_status.Text);
                    return SummaryInfo();
                case "parts":
                    Require();
                    return _asm.Parts.Select(p => PartInfo(p)).ToList();
                case "materials":
                    {
                        var c = a.Str("category");
                        return MassMaterials.All.Where(m => c == null || m.Category == c).Select(m => ApiArgs.Ret("name", m.Name, "category", m.Category, "density_g_cm3", m.Density, "fdm_infill", m.Fdm, "note", m.Note)).ToList();
                    }
                case "set_material":
                    {
                        Require();
                        var targets = Targets(a);
                        string mat = a.Has("material") ? a.Str("material") : throw new ArgumentException("缺少 material");
                        if (mat == "sw") mat = MassCalc.SwChoice;
                        else if (mat != null) mat = (MassMaterials.Find(mat) ?? throw new ArgumentException("没有这个材料：" + mat + "（先用 materials 查看）")).Name;
                        foreach (var p in targets)
                        {
                            if (mat == MassCalc.SwChoice && (p.SwMaterial == null || p.SwDensity <= 0)) continue;
                            p.Material = mat; p.MaterialWhy = "手动选择";
                        }
                        Changed();
                        return SummaryInfo();
                    }
                case "set_measured":
                    {
                        Require();
                        var p = PartAt(Convert.ToInt32(a["part"], CultureInfo.InvariantCulture));
                        p.MeasuredGrams = a.Has("grams") && a["grams"] != null ? a.Num("grams") : (double?)null;
                        Changed();
                        return PartInfo(p);
                    }
                case "exclude":
                    {
                        Require();
                        bool ex = !a.Has("excluded") || a.Bool("excluded");
                        foreach (var p in Targets(a)) p.Excluded = ex;
                        Changed();
                        return SummaryInfo();
                    }
                case "settings":
                    if (a.Has("infill")) { var v = a.Num("infill"); _s.Infill = v > 1 ? v / 100 : v; }
                    if (a.Has("shell_mm")) _s.ShellMm = a.Num("shell_mm");
                    if (_asm != null) Changed();
                    return ApiArgs.Ret("infill", _s.Infill, "shell_mm", _s.ShellMm);
                case "summary":
                    Require();
                    return SummaryInfo();
                case "export_csv":
                    {
                        Require();
                        var path = System.IO.Path.GetFullPath(a.ReqStr("path"));
                        File.WriteAllText(path, MassCalc.ToCsv(_asm, _s), new UTF8Encoding(true));
                        return ApiArgs.Ret("path", path);
                    }
                default: throw new ArgumentException("未知操作：" + action);
            }
        }

        void Require() { if (_asm == null) throw new InvalidOperationException("先读入装配体（load）"); }
        MassPart PartAt(int i) => i >= 0 && i < _asm.Parts.Count ? _asm.Parts[i] : throw new ArgumentException("没有编号为 " + i + " 的零件（parts 查看编号）");

        List<MassPart> Targets(Dictionary<string, object> a)
        {
            if (!a.Has("parts")) throw new ArgumentException("缺少 parts");
            if (a["parts"] is string s)
            {
                if (s == "all") return _asm.Parts.ToList();
                if (s == "unassigned") return _asm.Parts.Where(p => !p.Excluded && double.IsNaN(MassCalc.UnitGrams(p, _s))).ToList();
                return _asm.Parts.Where(p => p.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }
            return a.List("parts").Select(o => PartAt(Convert.ToInt32(o, CultureInfo.InvariantCulture))).ToList();
        }

        object PartInfo(MassPart p)
        {
            double u = MassCalc.UnitGrams(p, _s);
            return ApiArgs.Ret("index", p.Index, "name", p.Name, "config", p.Config, "quantity", p.Quantity, "volume_cm3", Math.Round(p.Volume * 1e6, 4), "area_cm2", Math.Round(p.Area * 1e4, 3),
                "material", p.Material == MassCalc.SwChoice ? "sw:" + p.SwMaterial : p.Material, "why", p.MaterialWhy, "sw_material", p.SwMaterial, "measured_g", p.MeasuredGrams,
                "unit_g", double.IsNaN(u) ? (double?)null : Math.Round(u, 3), "subtotal_g", double.IsNaN(u) || p.Excluded ? (double?)null : Math.Round(u * p.Quantity, 3), "excluded", p.Excluded, "note", p.Note);
        }

        object SummaryInfo()
        {
            var sum = MassCalc.Summarize(_asm.Parts, _s);
            return ApiArgs.Ret("file", _asm.File, "parts", _asm.Parts.Count, "instances", _asm.Instances, "total_g", Math.Round(sum.TotalGrams, 3),
                "center_of_mass_mm", sum.Com is Vec3 c ? new[] { Math.Round(c.X * 1000, 3), Math.Round(c.Y * 1000, 3), Math.Round(c.Z * 1000, 3) } : null,
                "unassigned", sum.UnassignedParts.Select(p => ApiArgs.Ret("index", p.Index, "name", p.Name)).ToList(),
                "by_material", sum.ByMaterial.Select(x => ApiArgs.Ret("material", x.material, "grams", Math.Round(x.grams, 3), "parts", x.parts)).ToList(),
                "fdm", ApiArgs.Ret("infill", _s.Infill, "shell_mm", _s.ShellMm), "notes", _asm.Notes, "status", _status.Text);
        }

        // ================= 自测（开发用） =================

        /// <summary>把弹窗之类的元素渲染成 PNG（弹窗是单独的窗口，主窗口截图拍不到）。</summary>
        static void SaveVisual(FrameworkElement el, string path)
        {
            el.UpdateLayout();
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * 2), (int)Math.Ceiling(el.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight)); dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight)); }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        internal async Task<string> SelfTest(string file, Func<string, Task> shot, string popupPng = null)
        {
            var sb = new StringBuilder();
            // 自测不碰用户真实的材料记忆
            MassStore.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swkit-mass-selftest.json");
            try { File.Delete(MassStore.OverridePath); } catch { }
            await LoadAsync(file);
            if (_asm == null) return "读入失败：" + _status.Text;
            sb.AppendLine($"读入 {_asm.Name}：{_asm.Parts.Count} 种零件、{_asm.Instances} 个；{_status.Text}");
            if (shot != null) await shot("loaded");
            // 勾选全部 → 批量设材料（打开弹窗截图，再选材料）
            foreach (var p in _asm.Parts) _checked.Add(p);
            RefreshAll();
            OpenPicker(_bulkBtn, _checked.ToList());
            if (shot != null) await shot("picker");
            if (popupPng != null) SaveVisual((FrameworkElement)_popup.Child, popupPng);
            // 单个零件的弹窗（带实测质量、不计入）
            _popup.IsOpen = false;
            OpenPicker(_rows[0].MatBtn, new List<MassPart> { _asm.Parts[0] });
            await Task.Delay(600);
            if (popupPng != null) SaveVisual((FrameworkElement)_popup.Child, System.IO.Path.ChangeExtension(popupPng, null) + "-single.png");
            _popup.IsOpen = false;
            foreach (var p in _asm.Parts) { p.Material = "6061-T6 铝"; p.MaterialWhy = "手动选择"; }
            _checked.Clear();
            Changed();
            var s1 = MassCalc.Summarize(_asm.Parts, _s);
            sb.AppendLine($"全部 6061-T6：{s1.TotalGrams:0.###} g，重心 {(s1.Com is Vec3 c1 ? $"({c1.X * 1000:0.##}, {c1.Y * 1000:0.##}, {c1.Z * 1000:0.##}) mm" : "无")}");
            // 第一种零件改 PLA、第二种填实测质量、第三种不计入
            if (_asm.Parts.Count >= 3)
            {
                _asm.Parts[0].Material = "PLA"; _asm.Parts[0].MaterialWhy = "手动选择";
                _asm.Parts[1].MeasuredGrams = 12.5;
                _asm.Parts[2].Excluded = true;
                Changed();
                foreach (var p in _asm.Parts) sb.AppendLine($"  {p.Name} ×{p.Quantity}：{MassCalc.MaterialLabel(p)}，单件 {MassCalc.UnitGrams(p, _s):0.###} g{(p.Excluded ? "（不计入）" : "")}");
                var s2 = MassCalc.Summarize(_asm.Parts, _s);
                sb.AppendLine($"改完：{s2.TotalGrams:0.###} g；状态栏：{_status.Text}");
                // 截图用的状态：一种 PLA、一种碳纤维、一种还没选（显示提示）
                foreach (var p in _asm.Parts) { p.Material = null; p.MaterialWhy = null; p.MeasuredGrams = null; p.Excluded = false; }
                _asm.Parts[0].Material = "PLA"; _asm.Parts[0].MaterialWhy = "手动选择";
                _asm.Parts[1].Material = "碳纤维板 3K（预浸料压制）"; _asm.Parts[1].MaterialWhy = "手动选择";
                Changed();
            }
            if (shot != null) await shot("result");
            return sb.ToString();
        }
    }
}
