using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using SwKit.Core;

namespace SwKit.App
{
    /// <summary>"一键导出"页面。</summary>
    sealed class ExportPage
    {
        public FrameworkElement Root { get; }
        public bool IsRunning { get; private set; }

        readonly Window _owner;
        readonly SwWorker _worker;
        readonly ObservableCollection<ExportItem> _items = new ObservableCollection<ExportItem>();
        readonly ExportOptions _opt = ExportOptions.Load();
        readonly Dictionary<string, ToggleButton> _chips;
        readonly DispatcherTimer _dragHide, _tick;
        CancellationTokenSource _cts;
        DateTime _runStart;
        string _phase = "";
        string _lastOutput;
        bool _logOpen;

        // 控件
        readonly ListBox _list;
        readonly FrameworkElement _empty, _listCard, _overlay, _settings, _stepSection, _stlSection, _flatSection, _outFolderRow;
        readonly TextBlock _summary, _status, _outFolderText;
        readonly ProgressBar _progress;
        readonly Button _start, _cancel, _openOut, _logToggle;
        readonly Border _logPanel;
        readonly StackPanel _logLines;
        readonly ScrollViewer _logScroll;

        static readonly Brush StatusNormal = Ui.Brush("#6E6E73");
        static readonly Brush StatusGood = Ui.Brush("#248A3D");
        static readonly Brush StatusBad = Ui.Brush("#D70015");
        static readonly Brush StatusWarn = Ui.Brush("#B25000");

        public ExportPage(Window owner, SwWorker worker)
        {
            _owner = owner;
            _worker = worker;
            Root = (FrameworkElement)Ui.Load("ExportPage.xaml");

            _list = Root.Find<ListBox>("FileList");
            _empty = Root.Find<FrameworkElement>("EmptyState");
            _listCard = Root.Find<FrameworkElement>("ListCard");
            _overlay = Root.Find<FrameworkElement>("DropOverlay");
            _settings = Root.Find<FrameworkElement>("SettingsPanel");
            _stepSection = Root.Find<FrameworkElement>("StepSection");
            _stlSection = Root.Find<FrameworkElement>("StlSection");
            _flatSection = Root.Find<FrameworkElement>("FlatSection");
            _outFolderRow = Root.Find<FrameworkElement>("OutFolderRow");
            _summary = Root.Find<TextBlock>("FileSummary");
            _status = Root.Find<TextBlock>("StatusText");
            _outFolderText = Root.Find<TextBlock>("OutFolderText");
            _progress = Root.Find<ProgressBar>("Progress");
            _start = Root.Find<Button>("StartBtn");
            _cancel = Root.Find<Button>("CancelBtn");
            _openOut = Root.Find<Button>("OpenOutBtn");
            _logToggle = Root.Find<Button>("LogToggleBtn");
            _logPanel = Root.Find<Border>("LogPanel");
            _logLines = Root.Find<StackPanel>("LogLines");
            _logScroll = Root.Find<ScrollViewer>("LogScroll");

            _list.ItemsSource = _items;
            _items.CollectionChanged += (s, e) => UpdateFileState();

            _chips = new Dictionary<string, ToggleButton>
            {
                ["step"] = Root.Find<ToggleButton>("FmtStep"),
                ["stl"] = Root.Find<ToggleButton>("FmtStl"),
                ["3mf"] = Root.Find<ToggleButton>("Fmt3mf"),
                ["x_t"] = Root.Find<ToggleButton>("FmtXt"),
                ["iges"] = Root.Find<ToggleButton>("FmtIges"),
                ["dxf_flat"] = Root.Find<ToggleButton>("FmtFlat"),
                ["pdf"] = Root.Find<ToggleButton>("FmtPdf"),
                ["dwg"] = Root.Find<ToggleButton>("FmtDwg"),
                ["dxf"] = Root.Find<ToggleButton>("FmtDxf"),
            };

            _dragHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _dragHide.Tick += (s, e) => { _dragHide.Stop(); Ui.Fade(_overlay, false, 150); };
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (s, e) => RefreshRunningStatus();

            BindOptions();
            WireActions();
            WireDragDrop();
            UpdateFileState();
            UpdateSections(false);
        }

        // ================= 设置 =================

        void BindOptions()
        {
            foreach (var kv in _chips)
            {
                var key = kv.Key;
                kv.Value.IsChecked = _opt.Formats.Contains(key);
                kv.Value.ToolTip = ExportFormats.ByKey(key)?.Hint;
                kv.Value.Checked += (s, e) => FormatsChanged();
                kv.Value.Unchecked += (s, e) => FormatsChanged();
            }

            Segment("Ap214", "Ap203", _opt.StepAP != 203, a => _opt.StepAP = a ? 214 : 203);
            Segment("StlFine", "StlCoarse", _opt.StlQuality != "coarse", a => _opt.StlQuality = a ? "fine" : "coarse");
            Segment("UnitMm", "UnitM", _opt.MeshUnits != "m", a => _opt.MeshUnits = a ? "mm" : "m");
            Segment("OutSource", "OutCustom", _opt.OutputToSourceFolder, a => { _opt.OutputToSourceFolder = a; UpdateOutFolder(true); });

            Switch("KeepCoords", _opt.StlKeepCoordinates, v => _opt.StlKeepCoordinates = v);
            Switch("AsmOneFile", _opt.StlAssemblyOneFile, v => _opt.StlAssemblyOneFile = v);
            Switch("StlBinary", _opt.StlBinary, v => _opt.StlBinary = v);
            Switch("BendLines", _opt.FlatIncludeBendLines, v => _opt.FlatIncludeBendLines = v);
            Switch("Subfolders", _opt.SubfolderPerFormat, v => _opt.SubfolderPerFormat = v);
            Switch("AllConfigs", _opt.AllConfigurations, v => _opt.AllConfigurations = v);
            Switch("Overwrite", _opt.Overwrite, v => _opt.Overwrite = v);

            Root.Find<Button>("ChooseOutBtn").Click += (s, e) =>
            {
                var path = FolderPicker.Pick(_owner, "选择导出文件夹", _opt.OutputFolder);
                if (path == null) return;
                _opt.OutputFolder = path;
                _opt.Save();
                UpdateOutFolder(true);
            };
            UpdateOutFolder(false);
        }

        void Segment(string first, string second, bool firstSelected, Action<bool> set)
        {
            var a = Root.Find<RadioButton>(first);
            var b = Root.Find<RadioButton>(second);
            a.IsChecked = firstSelected;
            b.IsChecked = !firstSelected;
            a.Checked += (s, e) => { set(true); _opt.Save(); };
            b.Checked += (s, e) => { set(false); _opt.Save(); };
        }

        void Switch(string name, bool value, Action<bool> set)
        {
            var cb = Root.Find<CheckBox>(name);
            cb.IsChecked = value;
            cb.Checked += (s, e) => { set(true); _opt.Save(); };
            cb.Unchecked += (s, e) => { set(false); _opt.Save(); };
        }

        void FormatsChanged()
        {
            _opt.Formats = _chips.Where(k => k.Value.IsChecked == true).Select(k => k.Key).ToList();
            _opt.Save();
            UpdateSections(true);
            UpdateStartState();
        }

        void UpdateSections(bool animate)
        {
            void Show(FrameworkElement el, bool on)
            {
                if (animate) Ui.Fade(el, on);
                else Ui.SetShown(el, on);
            }
            Show(_stepSection, _opt.Formats.Contains("step"));
            Show(_stlSection, _opt.Formats.Contains("stl"));
            Show(_flatSection, _opt.Formats.Contains("dxf_flat"));
        }

        void UpdateOutFolder(bool animate)
        {
            var custom = !_opt.OutputToSourceFolder;
            if (animate) Ui.Fade(_outFolderRow, custom);
            else Ui.SetShown(_outFolderRow, custom);
            _outFolderText.Text = string.IsNullOrWhiteSpace(_opt.OutputFolder) ? "还没有选择文件夹" : _opt.OutputFolder;
            _outFolderText.ToolTip = _opt.OutputFolder;
            UpdateStartState();
        }

        /// <summary>自测时会临时改设置，不能写进用户的设置文件。</summary>
        bool _noSave;

        public void SaveOptions() { if (!_noSave) _opt.Save(); }

        // ================= 文件列表 =================

        void WireActions()
        {
            Root.Find<Button>("EmptyAddFiles").Click += (s, e) => PickFiles();
            Root.Find<Button>("AddFilesBtn").Click += (s, e) => PickFiles();
            Root.Find<Button>("EmptyAddFolder").Click += (s, e) => PickFolder();
            Root.Find<Button>("AddFolderBtn").Click += (s, e) => PickFolder();
            Root.Find<Button>("ClearBtn").Click += (s, e) =>
            {
                if (IsRunning) return;
                _items.Clear();
                SetStatus("添加文件后就可以开始导出", StatusNormal);
                Ui.Fade(_openOut, false);
            };
            _list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (IsRunning) return;
                if (e.OriginalSource is FrameworkElement fe && FindButton(fe) is Button b && b.Name == "RemoveBtn" && b.DataContext is ExportItem item)
                    _items.Remove(item);
            }));

            _start.Click += async (s, e) => await RunExportAsync();
            _cancel.Click += (s, e) =>
            {
                _cts?.Cancel();
                _cancel.IsEnabled = false;
                _phase = "正在取消，当前文件完成后停止";
                RefreshRunningStatus();
            };
            _openOut.Click += (s, e) => OpenOutput();
            _logToggle.Click += (s, e) => ToggleLog();
            Root.Find<Button>("CopyLogBtn").Click += (s, e) =>
            {
                var sb = new StringBuilder();
                foreach (var tb in _logLines.Children.OfType<TextBlock>()) sb.AppendLine(tb.Text);
                try { Clipboard.SetText(sb.ToString()); } catch { }
            };
        }

        static Button FindButton(DependencyObject d)
        {
            while (d != null && !(d is Button)) d = VisualTreeHelper.GetParent(d);
            return d as Button;
        }

        void PickFiles()
        {
            if (IsRunning) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 SolidWorks 文件",
                Multiselect = true,
                Filter = "SolidWorks 文件|*.sldprt;*.sldasm;*.slddrw|零件|*.sldprt|装配体|*.sldasm|工程图|*.slddrw",
            };
            if (dlg.ShowDialog(_owner) == true) AddPaths(dlg.FileNames);
        }

        void PickFolder()
        {
            if (IsRunning) return;
            var path = FolderPicker.Pick(_owner, "选择包含 SolidWorks 文件的文件夹");
            if (path != null) AddPaths(new[] { path });
        }

        public void AddPaths(IEnumerable<string> paths)
        {
            if (IsRunning) return;
            var files = ExportFormats.ExpandInputs(paths, true);
            var existing = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var f in files.Where(f => !existing.Contains(f)))
            {
                _items.Add(new ExportItem(f));
                added++;
            }
            // 用户还在选格式、选文件夹的时候，就先在后台把 SolidWorks 启动起来（开发用的截图 / 自测模式不预热）
            if (added > 0 && !MainWindow.DevMode) _worker.Prewarm();
            if (files.Count == 0) SetStatus("没有找到 SolidWorks 文件（.sldprt / .sldasm / .slddrw）", StatusWarn);
            else if (added == 0) SetStatus("这些文件已经在列表里了", StatusNormal);
            else SetStatus($"已添加 {added} 个文件", StatusNormal);
        }

        void UpdateFileState()
        {
            var has = _items.Count > 0;
            Ui.Fade(_empty, !has, 200);
            Ui.Fade(_listCard, has, 200);
            var parts = _items.Count(i => i.Kind == DocKind.Part);
            var asms = _items.Count(i => i.Kind == DocKind.Assembly);
            var drws = _items.Count(i => i.Kind == DocKind.Drawing);
            var detail = new List<string>();
            if (parts > 0) detail.Add($"零件 {parts}");
            if (asms > 0) detail.Add($"装配体 {asms}");
            if (drws > 0) detail.Add($"工程图 {drws}");
            _summary.Inlines.Clear();
            _summary.Inlines.Add(new Run($"{_items.Count} 个文件"));
            if (detail.Count > 0)
                _summary.Inlines.Add(new Run("    " + string.Join("  ·  ", detail)) { FontWeight = FontWeights.Normal, Foreground = StatusNormal });
            UpdateStartState();
        }

        void UpdateStartState()
        {
            if (IsRunning) return;
            var ok = _items.Count > 0 && _opt.Formats.Count > 0 && (_opt.OutputToSourceFolder || !string.IsNullOrWhiteSpace(_opt.OutputFolder));
            _start.IsEnabled = ok;
            _start.ToolTip = ok ? null
                : _items.Count == 0 ? "先添加要导出的文件"
                : _opt.Formats.Count == 0 ? "至少选择一种导出格式"
                : "先选择输出文件夹";
        }

        // ================= 拖放 =================

        void WireDragDrop()
        {
            Root.DragOver += (s, e) =>
            {
                var ok = !IsRunning && e.Data.GetDataPresent(DataFormats.FileDrop);
                e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
                if (!ok) return;
                Ui.Fade(_overlay, true, 150);
                _dragHide.Stop();
                _dragHide.Start();
            };
            Root.DragLeave += (s, e) => { _dragHide.Stop(); _dragHide.Start(); };
            Root.Drop += (s, e) =>
            {
                _dragHide.Stop();
                Ui.Fade(_overlay, false, 150);
                if (!IsRunning && e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
            };
        }

        // ================= 导出 =================

        async Task RunExportAsync()
        {
            if (IsRunning || _items.Count == 0) return;
            foreach (var it in _items) it.Reset();
            _logLines.Children.Clear();
            _lastOutput = null;
            SetRunning(true);

            var items = _items.ToList();
            var opt = _opt.Clone();
            opt.Mode = (int)SwMode.Isolated;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var runner = new ExportRunner();
            runner.Log += (level, text) => Root.Dispatcher.BeginInvoke(new Action(() => AppendLog(level, text)));
            runner.Progress += (done, total) => Root.Dispatcher.BeginInvoke(new Action(() => SetProgress(done, total)));
            PropertyChangedEventHandler onItem = (s, e) =>
            {
                if (e.PropertyName != nameof(ExportItem.Status)) return;
                var it = (ExportItem)s;
                if (it.Status != ItemStatus.Running) return;
                Root.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _phase = $"正在导出 {items.IndexOf(it) + 1}/{items.Count} · {it.FileName}";
                    RefreshRunningStatus();
                    _list.ScrollIntoView(it);
                }));
            };
            foreach (var it in items) it.PropertyChanged += onItem;

            _phase = "正在准备后台 SolidWorks";
            RefreshRunningStatus();

            ExportSummary summary = null;
            Exception error = null;
            try
            {
                summary = await _worker.Run(() =>
                {
                    var session = _worker.Acquire(m => Root.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppendLog(LogLevel.Info, m);
                        if (m.Contains("启动")) { _phase = "正在启动后台 SolidWorks（第一次约 20–60 秒）"; RefreshRunningStatus(); }
                    })), ct);
                    try { return runner.Run(session, items, opt, ct); }
                    finally { _worker.Release(); }
                });
            }
            catch (OperationCanceledException) { error = null; }
            catch (Exception ex) { error = ex; }
            finally
            {
                foreach (var it in items) it.PropertyChanged -= onItem;
            }

            SetRunning(false);
            var secs = (DateTime.Now - _runStart).TotalSeconds;
            if (error != null)
            {
                SetStatus("导出没有完成：" + error.Message, StatusBad);
                AppendLog(LogLevel.Error, error.Message);
                return;
            }
            if (summary == null || summary.Cancelled)
            {
                SetStatus($"已取消 · 已生成 {summary?.Outputs.Count ?? 0} 个文件", StatusWarn);
            }
            else
            {
                var problems = summary.Failed + summary.Warning;
                var text = $"完成 · 生成 {summary.Outputs.Count} 个文件 · 用时 {secs:0} 秒";
                if (problems > 0) text += $" · {problems} 个文件有问题，详见列表";
                SetStatus(text, problems > 0 ? StatusWarn : StatusGood);
            }
            _lastOutput = summary?.Outputs.LastOrDefault();
            if (_lastOutput != null) Ui.Fade(_openOut, true);
            _progress.BeginAnimation(RangeBase.ValueProperty, Ui.To(1, 300));
        }

        void SetRunning(bool running)
        {
            IsRunning = running;
            _settings.IsEnabled = !running;
            _start.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            _cancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            _cancel.IsEnabled = true;
            foreach (var n in new[] { "AddFilesBtn", "AddFolderBtn", "ClearBtn" }) Root.Find<Button>(n).IsEnabled = !running;
            if (running)
            {
                _runStart = DateTime.Now;
                Ui.SetShown(_openOut, false);
                _progress.BeginAnimation(RangeBase.ValueProperty, null);
                _progress.Value = 0;
                Ui.Fade(_progress, true);
                _tick.Start();
            }
            else
            {
                _tick.Stop();
                UpdateStartState();
            }
        }

        void RefreshRunningStatus()
        {
            if (!IsRunning) return;
            var secs = (int)(DateTime.Now - _runStart).TotalSeconds;
            SetStatus($"{_phase} · 已用时 {secs} 秒", StatusNormal);
        }

        void SetProgress(int done, int total)
        {
            if (total <= 0) return;
            _progress.BeginAnimation(RangeBase.ValueProperty, Ui.To(done / (double)total, 350));
        }

        void SetStatus(string text, Brush color)
        {
            _status.Text = text;
            _status.Foreground = color;
        }

        void OpenOutput()
        {
            if (_lastOutput == null) return;
            try
            {
                if (File.Exists(_lastOutput)) Process.Start("explorer.exe", $"/select,\"{_lastOutput}\"");
                else Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(_lastOutput)}\"");
            }
            catch { }
        }

        // ================= 日志 =================

        void ToggleLog()
        {
            _logOpen = !_logOpen;
            _logToggle.Content = _logOpen ? "隐藏日志" : "显示日志";
            _logPanel.BeginAnimation(FrameworkElement.HeightProperty, Ui.To(_logOpen ? 230 : 0, 260));
            if (_logOpen) _logScroll.ScrollToEnd();
        }

        static readonly Dictionary<LogLevel, Brush> LogColors = new Dictionary<LogLevel, Brush>
        {
            [LogLevel.Info] = Ui.Brush("#6E6E73"),
            [LogLevel.Success] = Ui.Brush("#248A3D"),
            [LogLevel.Warning] = Ui.Brush("#B25000"),
            [LogLevel.Error] = Ui.Brush("#D70015"),
        };

        void AppendLog(LogLevel level, string text)
        {
            var tb = new TextBlock
            {
                Text = $"{DateTime.Now:HH:mm:ss}  {text}",
                Foreground = LogColors[level],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1),
            };
            _logLines.Children.Add(tb);
            if (_logLines.Children.Count > 2000) _logLines.Children.RemoveAt(0);
            if (_logOpen) _logScroll.ScrollToEnd();
        }

        // ================= 给 AI 用的接口 =================

        internal static IEnumerable<ApiAction> ApiActions => new[]
        {
            new ApiAction("formats", "列出可导出的格式", ""),
            new ApiAction("add", "添加要导出的文件或文件夹（文件夹会递归查找 .sldprt / .sldasm / .slddrw）", "{paths: [...]}"),
            new ApiAction("clear", "清空列表", ""),
            new ApiAction("set", "设置导出选项", "{formats?: [step, stl, 3mf, iges, x_t, dxf_flat, pdf, dwg, dxf], out?: 输出文件夹（不给则输出到源文件旁边）, overwrite?: bool, all_configs?: bool}"),
            new ApiAction("list", "列出当前文件和状态", ""),
            new ApiAction("run", "开始导出（在后台 SolidWorks 里进行，完成后返回每个文件的结果）", ""),
        };

        internal async Task<object> ApiAsync(string action, Dictionary<string, object> a)
        {
            switch (action)
            {
                case "formats":
                    return ExportFormats.All.Select(f => ApiArgs.Ret("key", f.Key, "label", f.Label, "ext", f.Extension, "selected", _opt.Formats.Contains(f.Key))).ToList();
                case "add":
                    {
                        var paths = a.List("paths").Select(o => o.ToString()).ToList();
                        if (paths.Count == 0) throw new ArgumentException("缺少 paths");
                        var missing = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
                        if (missing.Count > 0) throw new ArgumentException("不存在：" + string.Join("，", missing));
                        AddPaths(paths);
                        return Items();
                    }
                case "clear":
                    if (IsRunning) throw new InvalidOperationException("正在导出");
                    _items.Clear();
                    return Items();
                case "set":
                    if (a.Has("formats"))
                    {
                        var keys = a.List("formats").Select(o => o.ToString().ToLowerInvariant()).ToList();
                        var bad = keys.Where(k => ExportFormats.All.All(f => f.Key != k)).ToList();
                        if (bad.Count > 0) throw new ArgumentException("不认识的格式：" + string.Join(",", bad));
                        foreach (var kv in _chips) kv.Value.IsChecked = keys.Contains(kv.Key);
                    }
                    if (a.Has("out"))
                    {
                        Root.Find<RadioButton>("OutCustom").IsChecked = true;
                        _opt.OutputToSourceFolder = false;
                        _opt.OutputFolder = a.Str("out");
                        UpdateOutFolder(true);
                    }
                    else if (a.Bool("to_source")) Root.Find<RadioButton>("OutSource").IsChecked = true;
                    if (a.Has("overwrite")) Root.Find<CheckBox>("Overwrite").IsChecked = a.Bool("overwrite");
                    if (a.Has("all_configs")) Root.Find<CheckBox>("AllConfigs").IsChecked = a.Bool("all_configs");
                    return ApiArgs.Ret("formats", _opt.Formats, "out", _opt.OutputToSourceFolder ? "（源文件旁边）" : _opt.OutputFolder, "overwrite", _opt.Overwrite);
                case "list": return Items();
                case "run":
                    if (IsRunning) throw new InvalidOperationException("已经在导出");
                    if (_items.Count == 0) throw new InvalidOperationException("列表是空的，先 add");
                    await RunExportAsync();
                    return ApiArgs.Ret("status", _status.Text, "items", Items());
                default: throw new ArgumentException("未知操作：" + action);
            }
        }

        List<Dictionary<string, object>> Items() => _items.Select(i => ApiArgs.Ret("file", i.Path, "kind", i.KindText, "status", i.StatusText, "message", i.Message, "outputs", i.Outputs.ToList())).ToList();

        // ================= 自测（开发用） =================

        /// <summary>通过真实界面流程连续导出两轮：第二轮应直接复用后台 SolidWorks，不再启动。</summary>
        internal async Task<string> SelfTest(string dataDir, string outDir)
        {
            _noSave = true;
            _opt.Formats = new List<string> { "step", "stl" };
            _opt.OutputToSourceFolder = false;
            _opt.OutputFolder = outDir;
            _opt.SubfolderPerFormat = false;
            _opt.Overwrite = true;
            _opt.AllConfigurations = false;
            AddPaths(new[] { dataDir });
            var sb = new StringBuilder();
            for (int round = 1; round <= 2; round++)
            {
                var t0 = DateTime.Now;
                await RunExportAsync();
                sb.AppendLine($"第 {round} 轮：用时 {(DateTime.Now - t0).TotalSeconds:0} 秒；状态栏：{_status.Text}");
                foreach (var it in _items) sb.AppendLine($"  [{it.StatusText}] {it.FileName}：{it.Message}");
            }
            sb.AppendLine("日志：");
            foreach (var tb in _logLines.Children.OfType<TextBlock>()) sb.AppendLine("  " + tb.Text);
            return sb.ToString();
        }

        // ================= 截图演示用 =================

        internal void LoadDemo(string dataFolder)
        {
            var names = new[] { "机械臂_底座.sldprt", "大臂_碳板.sldprt", "法兰.sldprt", "夹爪总成.sldasm", "钣金L板.sldprt", "大臂_碳板.slddrw" };
            var states = new[] { ItemStatus.Done, ItemStatus.Done, ItemStatus.Running, ItemStatus.Waiting, ItemStatus.Waiting, ItemStatus.Waiting };
            var msgs = new[] { "已导出 2 个文件", "已导出 2 个文件", "", "", "", "" };
            for (int i = 0; i < names.Length; i++)
            {
                var it = new ExportItem(Path.Combine(dataFolder, names[i]));
                _items.Add(it);
                it.Status = states[i];
                it.Message = msgs[i];
            }
            IsRunning = true;
            _start.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Visible;
            Ui.SetShown(_progress, true);
            _progress.Value = 0.36;
            SetStatus("正在导出 3/6 · 法兰.sldprt · 已用时 46 秒", StatusNormal);
        }

        internal void LoadDemoDone(string dataFolder)
        {
            _items.Clear();
            var rows = new (string name, ItemStatus st, string msg)[]
            {
                ("机械臂_底座.sldprt", ItemStatus.Done, "已导出 2 个文件"),
                ("大臂_碳板.sldprt", ItemStatus.Done, "已导出 2 个文件"),
                ("法兰.sldprt", ItemStatus.Done, "已导出 2 个文件"),
                ("夹爪总成.sldasm", ItemStatus.Warning, "已导出 1 个文件；STL：目标文件被其他程序占用"),
                ("钣金L板.sldprt", ItemStatus.Done, "已导出 3 个文件"),
                ("旧版零件.sldprt", ItemStatus.Failed, "文件是用更高版本的 SolidWorks 保存的，当前版本打不开"),
            };
            foreach (var r in rows)
            {
                var it = new ExportItem(Path.Combine(dataFolder, r.name));
                _items.Add(it);
                it.Status = r.st;
                it.Message = r.msg;
            }
            IsRunning = false;
            _start.Visibility = Visibility.Visible;
            _cancel.Visibility = Visibility.Collapsed;
            Ui.SetShown(_openOut, true);
            _progress.Value = 1;
            SetStatus("完成 · 生成 12 个文件 · 用时 58 秒 · 2 个文件有问题，详见列表", StatusWarn);
            AppendLog(LogLevel.Info, "后台 SolidWorks 2025（33.5.0）已就绪，用时 38 秒");
            AppendLog(LogLevel.Success, "  STEP → D:\\项目\\机械臂\\导出\\机械臂_底座.step");
            AppendLog(LogLevel.Success, "  STL → D:\\项目\\机械臂\\导出\\机械臂_底座.stl");
            AppendLog(LogLevel.Error, "  STL 失败：目标文件被其他程序占用");
            _logOpen = false;
            ToggleLog();
        }
    }
}
