using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Threading;
using SwKit.Core;
using SwKit.Core.Workspace;

namespace SwKit.App
{
    /// <summary>
    /// 「工作区」页面：项目文件夹列表、同名文件检查（工作区内 / 全电脑 / 装配体引用）、版本历史（自动 + 手动存档、恢复）、SolidWorks 崩溃找回。
    /// 自动存档在软件启动时就开始（StartWatcher），不需要打开这个页面。
    /// </summary>
    sealed class WorkspacePage
    {
        public FrameworkElement Root { get; }
        public bool IsBusy { get; private set; }

        // ================= 自动存档（全程序共用） =================

        static HistoryWatcher _watcher;
        static WorkspaceSettings _settings;
        static WorkspaceSettings _published;     // 给后台线程看的副本（页面改设置时整个替换，后台线程不会读到改了一半的列表）
        internal static event Action<HistoryVersion> AutoSaved;

        static WorkspaceSettings Settings
        {
            get
            {
                if (_settings == null) { _settings = WorkspaceSettings.Load(); Publish(); }
                return _settings;
            }
        }

        static void Publish()
        {
            var s = _settings;
            _published = new WorkspaceSettings { Workspaces = s.Workspaces.ToList(), Selected = s.Selected, AutoHistory = s.AutoHistory, KeepPerFile = s.KeepPerFile, MaxHistoryGb = s.MaxHistoryGb };
            _watcher?.Sync(_published.Workspaces, _published.AutoHistory);
        }

        /// <summary>软件启动时调用：按设置开始监视各工作区，保存后自动存档。</summary>
        internal static void StartWatcher()
        {
            if (_watcher != null) return;
            var s = Settings;
            _watcher = new HistoryWatcher(() => _published);
            _watcher.Saved += v => AutoSaved?.Invoke(v);
            _watcher.Sync(_published.Workspaces, _published.AutoHistory);
        }

        internal static void StopWatcher()
        {
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
        }

        static void SaveSettings()
        {
            try { Settings.Save(); } catch { }
            Publish();
        }

        // ================= 页面 =================

        readonly Window _owner;
        readonly SwWorker _worker;
        readonly StackPanel _wsList, _content;
        readonly FrameworkElement _empty, _detail;
        readonly TextBlock _status;
        readonly Button _scanBtn, _cancelBtn;
        readonly ProgressBar _progressBar;
        readonly ScrollViewer _tabScroll;
        readonly Dictionary<string, RadioButton> _tabs = new Dictionary<string, RadioButton>();
        readonly DispatcherTimer _progressTimer;
        Func<string> _progressText;

        sealed class WsState
        {
            public ScanResult Scan;
            public List<DupGroup> Internal, Global;
            public double GlobalSeconds; public long GlobalFiles, GlobalDirs; public List<string> GlobalRoots;
            public List<RefReport> Refs; public double RefSeconds;
            public string HistoryFile;      // 版本历史里选中的文件
            public string Filter = "";
        }

        readonly Dictionary<string, WsState> _state = new Dictionary<string, WsState>(StringComparer.OrdinalIgnoreCase);
        string _current;
        string _tab = "overview";
        CancellationTokenSource _cts;

        static readonly Brush Gray = Ui.Brush("#6E6E73"), Green = Ui.Brush("#248A3D"), Orange = Ui.Brush("#B25000"), Red = Ui.Brush("#D70015"),
                              Text3 = Ui.Brush("#A1A1A6"), Ink = Ui.Brush("#1D1D1F"), Accent = Ui.Brush("#0071E3");

        public WorkspacePage(Window owner, SwWorker worker)
        {
            _owner = owner;
            _worker = worker;
            Root = (FrameworkElement)Ui.Load("WorkspacePage.xaml");
            _wsList = Root.Find<StackPanel>("WsList");
            _content = Root.Find<StackPanel>("TabContent");
            _empty = Root.Find<FrameworkElement>("EmptyState");
            _detail = Root.Find<FrameworkElement>("Detail");
            _status = Root.Find<TextBlock>("StatusText");
            _scanBtn = Root.Find<Button>("ScanBtn");
            _cancelBtn = Root.Find<Button>("CancelBtn");
            _progressBar = Root.Find<ProgressBar>("Progress");
            _tabScroll = Root.Find<ScrollViewer>("TabScroll");

            Root.Find<Button>("AddWsBtn").Click += (s, e) => PickWorkspace();
            Root.Find<Button>("PickWsBtn").Click += (s, e) => PickWorkspace();
            Root.Find<Button>("OpenWsBtn").Click += (s, e) => { if (_current != null) OpenFolder(_current); };
            Root.Find<Button>("RemoveWsBtn").Click += (s, e) => { if (_current != null) RemoveWorkspace(_current, confirm: true); };
            _scanBtn.Click += async (s, e) => { if (_current != null) await ScanAsync(_current); };
            _cancelBtn.Click += (s, e) => _cts?.Cancel();
            foreach (var (name, key) in new[] { ("TabOverview", "overview"), ("TabDup", "dup"), ("TabHistory", "history"), ("TabRecover", "recover") })
            {
                var rb = Root.Find<RadioButton>(name);
                _tabs[key] = rb;
                var k = key;
                rb.Checked += (s, e) => { _tab = k; Render(scrollTop: true); };
            }
            _tabs["overview"].IsChecked = true;

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _progressTimer.Tick += (s, e) => { var t = _progressText?.Invoke(); if (t != null) { _status.Text = t; _status.Foreground = Gray; } };

            Root.DragOver += (s, e) => { e.Effects = !IsBusy && FolderFrom(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Root.Drop += (s, e) => { var f = FolderFrom(e.Data); if (f != null && !IsBusy) AddWorkspace(f); };

            AutoSaved += v => Root.Dispatcher.BeginInvoke(new Action(() => OnAutoSaved(v)));

            BuildWsList();
            var first = Settings.Selected != null && Settings.Workspaces.Contains(Settings.Selected, StringComparer.OrdinalIgnoreCase) ? Settings.Selected : Settings.Workspaces.FirstOrDefault();
            if (first != null) Root.Dispatcher.BeginInvoke(new Action(() => Select(first)), DispatcherPriority.Background);
            else ShowEmpty();
        }

        static string FolderFrom(IDataObject d) => (d.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(Directory.Exists);

        WsState State(string ws)
        {
            if (!_state.TryGetValue(ws, out var st)) _state[ws] = st = new WsState();
            return st;
        }

        // ================= 状态栏 / 忙碌 =================

        void SetStatus(string text, Brush color = null) { _status.Text = text; _status.Foreground = color ?? Gray; }

        CancellationToken BeginBusy(Func<string> progress, bool cancellable)
        {
            IsBusy = true;
            _cts = cancellable ? new CancellationTokenSource() : null;
            _progressText = progress;
            if (progress != null) _progressTimer.Start();
            Ui.SetShown(_cancelBtn, cancellable);
            _scanBtn.IsEnabled = false;
            return _cts?.Token ?? CancellationToken.None;
        }

        void EndBusy()
        {
            IsBusy = false;
            _progressTimer.Stop();
            _progressText = null;
            _cts = null;
            Ui.SetShown(_cancelBtn, false);
            _progressBar.Visibility = Visibility.Collapsed;
            _scanBtn.IsEnabled = _current != null;
        }

        // ================= 工作区列表 =================

        void PickWorkspace()
        {
            if (IsBusy) return;
            var p = FolderPicker.Pick(_owner, "选择项目文件夹（作为一个工作区）", _current);
            if (p != null) AddWorkspace(p);
        }

        /// <summary>添加工作区并开始扫描。返回规范化后的路径；失败时返回 null，并在状态栏显示原因。</summary>
        string AddWorkspace(string path)
        {
            try
            {
                var norm = WorkspaceSettings.Normalize(path);
                if (norm.Length <= 3) throw new ArgumentException("不能把整个磁盘作为工作区，请选择具体的项目文件夹");
                Settings.Add(norm);
                SaveSettings();
                BuildWsList();
                Select(norm);
                return norm;
            }
            catch (Exception ex)
            {
                SetStatus("添加失败：" + ex.Message, Red);
                return null;
            }
        }

        void RemoveWorkspace(string ws, bool confirm)
        {
            if (IsBusy) return;
            if (confirm && MessageBox.Show(_owner, $"从列表里移除「{Path.GetFileName(ws)}」？\n\n只是不再管理这个文件夹，不会删除任何文件；已经存下的历史版本也会保留，重新添加后还能看到。",
                    "SW 工具箱", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Settings.Remove(ws);
            SaveSettings();
            _state.Remove(ws);
            BuildWsList();
            var next = Settings.Workspaces.FirstOrDefault();
            if (next != null) Select(next); else { _current = null; ShowEmpty(); }
            SetStatus($"已移除「{Path.GetFileName(ws)}」（文件没有任何改动）");
        }

        void ShowEmpty()
        {
            Ui.Fade(_detail, false);
            Ui.Fade(_empty, true);
            _scanBtn.IsEnabled = false;
            SetStatus("添加一个项目文件夹开始");
        }

        void BuildWsList()
        {
            _wsList.Children.Clear();
            if (Settings.Workspaces.Count == 0) _wsList.Children.Add(Caption("还没有。点下面「添加工作区」，或者把文件夹拖进来。", Text3, 12, new Thickness(10, 2, 0, 0)));
            foreach (var ws in Settings.Workspaces)
            {
                var selected = string.Equals(ws, _current, StringComparison.OrdinalIgnoreCase);
                var exists = Directory.Exists(ws);
                var row = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand, Background = selected ? Ui.Brush("#EAF3FE") : Brushes.Transparent, ToolTip = ws };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(new TextBlock { Text = exists ? "" : "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 15, Foreground = exists ? (selected ? Accent : Gray) : Orange, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top });
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = Path.GetFileName(ws), FontSize = 13, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis });
                var st = _state.TryGetValue(ws, out var s) ? s : null;
                var sub = !exists ? "文件夹不存在（可能被移动或删除）"
                        : st?.Scan != null ? $"零件 {st.Scan.Parts} · 装配体 {st.Scan.Assemblies} · 工程图 {st.Scan.Drawings}" + (st.Internal != null && st.Internal.Any(x => !x.Identical) ? $" · {st.Internal.Count(x => !x.Identical)} 组重名" : "")
                        : Path.GetDirectoryName(ws);
                text.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = !exists ? Orange : st?.Internal != null && st.Internal.Any(x => !x.Identical) ? Orange : Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(text, 1); g.Children.Add(text);
                row.Child = g;
                var w = ws;
                row.MouseEnter += (a, b) => { if (!string.Equals(w, _current, StringComparison.OrdinalIgnoreCase)) row.Background = Ui.Brush("#F2F2F7"); };
                row.MouseLeave += (a, b) => { if (!string.Equals(w, _current, StringComparison.OrdinalIgnoreCase)) row.Background = Brushes.Transparent; };
                row.MouseLeftButtonUp += (a, b) => { if (!IsBusy) Select(w); };
                _wsList.Children.Add(row);
            }
        }

        void Select(string ws)
        {
            _current = ws;
            if (!string.Equals(Settings.Selected, ws, StringComparison.OrdinalIgnoreCase)) { Settings.Selected = ws; SaveSettings(); }
            BuildWsList();
            Ui.Fade(_empty, false);
            Ui.Fade(_detail, true);
            _scanBtn.IsEnabled = !IsBusy;
            Render(scrollTop: true);
            if (!Directory.Exists(ws)) { SetStatus("这个文件夹不存在了。可以点右上角「移除」，或者把它放回原处后点「重新扫描」", Orange); return; }
            if (State(ws).Scan == null && !IsBusy) _ = ScanAsync(ws);
        }

        void SwitchTab(string key) { if (_tabs.TryGetValue(key, out var rb)) { if (rb.IsChecked == true) Render(scrollTop: true); else rb.IsChecked = true; } }

        // ================= 扫描 / 检查 =================

        public async Task<ScanResult> ScanAsync(string ws)
        {
            if (IsBusy) return null;
            var p = new FastScan.Progress();
            var ct = BeginBusy(() => $"正在扫描「{Path.GetFileName(ws)}」：{p.Dirs:N0} 个文件夹、{p.Files:N0} 个文件……", true);
            try
            {
                var (scan, dups) = await Task.Run(() =>
                {
                    var r = WorkspaceScanner.Scan(ws, p, ct);
                    return (r, WorkspaceScanner.InternalDuplicates(r, ct));
                });
                var st = State(ws);
                st.Scan = scan; st.Internal = dups;
                var danger = dups.Count(d => !d.Identical);
                SetStatus($"扫描完成：{scan.SwFiles.Count()} 个 SolidWorks 文件（{WorkspaceScanner.SizeText(scan.TotalBytes)}），{scan.Seconds:0.0} 秒。" +
                          (dups.Count == 0 ? "工作区里没有同名文件。" : $"发现 {dups.Count} 组同名文件，其中 {danger} 组内容不同。"), danger > 0 ? Orange : Green);
                return scan;
            }
            catch (OperationCanceledException) { SetStatus("已停止扫描"); return null; }
            catch (Exception ex) { SetStatus("扫描失败：" + ex.Message, Red); return null; }
            finally
            {
                EndBusy();
                BuildWsList();
                if (string.Equals(ws, _current, StringComparison.OrdinalIgnoreCase)) Render(scrollTop: false);
            }
        }

        public async Task<List<DupGroup>> GlobalAsync(string ws, List<string> roots = null)
        {
            if (IsBusy) return null;
            var st = State(ws);
            if (st.Scan == null) { await ScanAsync(ws); if (st.Scan == null) return null; }
            roots = roots ?? FastScan.FixedDrives();
            var p = new FastScan.Progress();
            var sw = Stopwatch.StartNew();
            var ct = BeginBusy(() => $"正在检查全电脑（{string.Join("、", roots)}）：{p.Dirs:N0} 个文件夹、{p.Files:N0} 个文件，{sw.Elapsed.TotalSeconds:0} 秒……", true);
            try
            {
                var groups = await Task.Run(() => WorkspaceScanner.GlobalDuplicates(st.Scan, roots, p, ct));
                st.Global = groups; st.GlobalSeconds = sw.Elapsed.TotalSeconds; st.GlobalFiles = p.Files; st.GlobalDirs = p.Dirs; st.GlobalRoots = roots;
                var danger = groups.Count(g => !g.Identical);
                SetStatus($"全电脑检查完成：扫描了 {p.Files:N0} 个文件（{st.GlobalSeconds:0.0} 秒），工作区外有 {groups.Count} 组同名文件，其中 {danger} 组内容不同。", danger > 0 ? Orange : Green);
                return groups;
            }
            catch (OperationCanceledException) { SetStatus("已停止全电脑检查"); return null; }
            catch (Exception ex) { SetStatus("全电脑检查失败：" + ex.Message, Red); return null; }
            finally { EndBusy(); if (string.Equals(ws, _current, StringComparison.OrdinalIgnoreCase)) Render(scrollTop: false); }
        }

        /// <summary>在后台 SolidWorks 里检查工作区所有装配体 / 工程图的引用。</summary>
        public async Task<List<RefReport>> RefsAsync(string ws, IEnumerable<string> only = null)
        {
            if (IsBusy) return null;
            var st = State(ws);
            if (st.Scan == null) { await ScanAsync(ws); if (st.Scan == null) return null; }
            var docs = (only ?? st.Scan.Files.Where(f => f.Ext == ".sldasm" || f.Ext == ".slddrw").Select(f => f.Path)).ToList();
            if (docs.Count == 0) { SetStatus("这个工作区里没有装配体或工程图，不需要检查引用"); return new List<RefReport>(); }
            string phase = "准备中";
            var sw = Stopwatch.StartNew();
            var ct = BeginBusy(() => $"{phase} · {sw.Elapsed.TotalSeconds:0} 秒", true);
            try
            {
                var reports = await _worker.Run(() =>
                {
                    var s = _worker.Acquire(m => phase = m.Contains("启动") ? "正在启动后台 SolidWorks（第一次约 20–60 秒）" : m, ct);
                    try
                    {
                        var list = new List<RefReport>();
                        for (int i = 0; i < docs.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            phase = $"正在检查引用 {i + 1}/{docs.Count}：{Path.GetFileName(docs[i])}";
                            list.Add(RefCheck.Check(s.App, docs[i], ws));
                        }
                        return list;
                    }
                    finally { _worker.Release(); }
                });
                st.Refs = reports; st.RefSeconds = sw.Elapsed.TotalSeconds;
                var issues = reports.SelectMany(r => r.Issues).ToList();
                var bad = issues.Count(i => i.Kind == "缺失" || i.Kind == "会用错文件");
                SetStatus($"引用检查完成：{reports.Count} 个文档、{reports.Sum(r => r.Count)} 个引用，" + (issues.Count == 0 ? "没有问题。" : $"{issues.Count} 个提示，其中 {bad} 个需要处理。"), bad > 0 ? Orange : Green);
                return reports;
            }
            catch (OperationCanceledException) { SetStatus("已停止引用检查"); return null; }
            catch (Exception ex) { SetStatus("引用检查失败：" + ex.Message, Red); return null; }
            finally { EndBusy(); if (string.Equals(ws, _current, StringComparison.OrdinalIgnoreCase)) Render(scrollTop: false); }
        }

        // ================= 版本历史 =================

        public async Task<(int saved, int unchanged, List<string> failed)> SnapshotAllAsync(string ws, string note)
        {
            if (IsBusy) return (0, 0, new List<string> { "正忙" });
            var st = State(ws);
            if (st.Scan == null) { await ScanAsync(ws); if (st.Scan == null) return (0, 0, new List<string> { "扫描失败" }); }
            int done = 0, total = 0;
            var ct = BeginBusy(() => $"正在存档 {done}/{total}……", true);
            _progressBar.Value = 0; _progressBar.Visibility = Visibility.Visible;
            try
            {
                var files = st.Scan.SwFiles.Select(f => f.Path).Where(File.Exists).ToList();
                total = files.Count;
                var store = new HistoryStore(ws);
                var r = await Task.Run(() =>
                {
                    var res = store.SaveAll(files, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), (i, n) => { done = i; Root.Dispatcher.BeginInvoke(new Action(() => _progressBar.Value = n == 0 ? 1 : (double)i / n)); }, ct);
                    store.Prune(Settings.KeepPerFile, (long)(Settings.MaxHistoryGb * (1L << 30)));
                    return res;
                });
                SetStatus($"存档完成：新存了 {r.saved} 个文件，{r.unchanged} 个和上次一样没有重复存" + (r.failed.Count > 0 ? $"，{r.failed.Count} 个失败（{r.failed[0]}）" : "。"), r.failed.Count > 0 ? Orange : Green);
                return r;
            }
            catch (OperationCanceledException) { SetStatus("已停止存档（已经存好的保留）"); return (0, 0, new List<string>()); }
            catch (Exception ex) { SetStatus("存档失败：" + ex.Message, Red); return (0, 0, new List<string> { ex.Message }); }
            finally { EndBusy(); if (string.Equals(ws, _current, StringComparison.OrdinalIgnoreCase)) Render(scrollTop: false); }
        }

        /// <summary>恢复某个版本（confirm 为 false 时不弹确认框，给 API 用）。</summary>
        public async Task<HistoryVersion> RestoreAsync(string ws, string file, HistoryVersion v, bool confirm)
        {
            if (IsBusy) throw new InvalidOperationException("正在进行别的操作，请稍后再试");
            if (confirm && MessageBox.Show(_owner, $"把「{Path.GetFileName(file)}」恢复到 {v.Time:yyyy-MM-dd HH:mm:ss} 的版本？\n\n恢复前会先把当前文件自动存一份，随时可以再换回来。\n如果这个文件正在 SolidWorks 里打开，请先关闭它。",
                    "恢复历史版本", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return null;
            BeginBusy(null, false);
            SetStatus("正在恢复……");
            try
            {
                var r = await Task.Run(() => new HistoryStore(ws).Restore(file, v.Id));
                SetStatus($"已恢复「{Path.GetFileName(file)}」到 {v.Time:MM-dd HH:mm:ss} 的版本。恢复前的文件已自动存了一份。", Green);
                return r;
            }
            catch (Exception ex)
            {
                SetStatus("恢复失败：" + ex.Message, Red);
                if (confirm) MessageBox.Show(_owner, ex.Message, "没有恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
                if (!confirm) throw;
                return null;
            }
            finally { EndBusy(); if (string.Equals(ws, _current, StringComparison.OrdinalIgnoreCase)) Render(scrollTop: false); }
        }

        void SaveCopyAs(string source, string suggestedName)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "另存为", FileName = suggestedName,
                Filter = "SolidWorks 文件|*" + Path.GetExtension(suggestedName) + "|所有文件|*.*",
            };
            if (dlg.ShowDialog(_owner) != true) return;
            try
            {
                if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) return;
                using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = File.Create(dlg.FileName)) src.CopyTo(dst);
                SetStatus("已另存为：" + dlg.FileName, Green);
            }
            catch (Exception ex) { SetStatus("另存失败：" + ex.Message, Red); }
        }

        void OnAutoSaved(HistoryVersion v)
        {
            if (!IsBusy) SetStatus($"已自动存档：{v.Rel}（{v.Time:HH:mm:ss}）", Green);
            if (_tab == "history" && _current != null && v.Source != null && WorkspaceSettings.IsUnder(v.Source, _current)) Render(scrollTop: false);
        }

        static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"");
            }
            catch { }
        }

        static void OpenFolder(string dir) { try { if (Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\""); } catch { } }

        // ================= 界面构件 =================

        static Style S(string key) => (Style)Application.Current.FindResource(key);
        void Section(string t, Thickness? margin = null) => _content.Children.Add(new TextBlock { Text = t, Style = S("T.Section"), Margin = margin ?? new Thickness(4, 6, 0, 8) });
        static TextBlock Caption(string text, Brush color = null, double size = 12, Thickness? margin = null) =>
            new TextBlock { Text = text, FontSize = size, Foreground = color ?? Gray, TextWrapping = TextWrapping.Wrap, LineHeight = size + 6, Margin = margin ?? new Thickness(0) };
        static Border Group(UIElement child, Thickness? margin = null) { var b = new Border { Style = S("Group"), Child = child }; if (margin != null) b.Margin = margin.Value; return b; }

        static Button TextButton(string text, Action click, string tip = null)
        {
            var b = new Button { Style = S("Btn.Text"), Content = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
            b.Click += (s, e) => click();
            return b;
        }

        static Button SecondaryButton(string icon, string text, Action click)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon != null) sp.Children.Add(new TextBlock { Text = icon, Style = S("T.Icon"), FontSize = 13, Margin = new Thickness(0, 0, 7, 0) });
            sp.Children.Add(new TextBlock { Text = text });
            var b = new Button { Style = S("Btn.Secondary"), Content = sp, Margin = new Thickness(0, 0, 10, 0) };
            b.Click += (s, e) => click();
            return b;
        }

        static Border Badge(string text, Brush fg, string bg)
        {
            return new Border { Style = S("Badge"), Background = Ui.Brush(bg), Margin = new Thickness(8, 0, 0, 0), Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = fg } };
        }

        static string Rel(string path, string root) =>
            root != null && WorkspaceSettings.IsUnder(path, root) && path.Length > root.Length ? path.Substring(root.Length + 1) : path;

        static string When(DateTime t) =>
            t.Date == DateTime.Today ? "今天 " + t.ToString("HH:mm") : t.Date == DateTime.Today.AddDays(-1) ? "昨天 " + t.ToString("HH:mm") : t.Year == DateTime.Now.Year ? t.ToString("MM-dd HH:mm") : t.ToString("yyyy-MM-dd");

        /// <summary>一行文件：[字母] 路径   时间   大小   定位。</summary>
        static FrameworkElement FileRow(FileEntry f, string root, string where, string letter, bool letterWarn)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            int c = 0;
            void Add(UIElement e) { Grid.SetColumn(e, c++); g.Children.Add(e); }
            Add(letter == null ? new FrameworkElement() : new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0), Background = Ui.Brush(letterWarn ? "#FFF2E0" : "#ECECF0"),
                ToolTip = "内容相同的文件字母相同",
                Child = new TextBlock { Text = letter, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = letterWarn ? Orange : Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            Add(where == null ? new FrameworkElement() : new TextBlock { Text = where, FontSize = 11.5, Foreground = where == "外" ? Orange : Gray, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            Add(new TextBlock { Text = Rel(f.Path, root), FontSize = 12.5, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f.Path, VerticalAlignment = VerticalAlignment.Center });
            Add(new TextBlock { Text = When(f.Modified), FontSize = 11.5, Foreground = Gray, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Add(new TextBlock { Text = WorkspaceScanner.SizeText(f.Size), FontSize = 11.5, Foreground = Gray, Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            var p = f.Path;
            Add(TextButton("定位", () => Reveal(p), "在资源管理器里显示"));
            return g;
        }

        // ================= 各个标签页 =================

        void Render(bool scrollTop)
        {
            _content.Children.Clear();
            if (_current == null) return;
            switch (_tab)
            {
                case "dup": BuildDup(); break;
                case "history": BuildHistory(); break;
                case "recover": BuildRecover(); break;
                default: BuildOverview(); break;
            }
            if (scrollTop) _tabScroll.ScrollToTop();
        }

        void BuildOverview()
        {
            var ws = _current; var st = State(ws);
            if (st.Scan == null)
            {
                _content.Children.Add(Caption(IsBusy ? "正在扫描……" : Directory.Exists(ws) ? "还没有扫描。点右下角「重新扫描」。" : "文件夹不存在。", Text3, 12.5, new Thickness(4, 6, 0, 0)));
                return;
            }
            var r = st.Scan;
            var tiles = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, 0, 8) };
            foreach (var (label, n) in new[] { ("零件", r.Parts), ("装配体", r.Assemblies), ("工程图", r.Drawings), ("STEP", r.Steps), ("STL", r.Stls) })
            {
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = n.ToString("N0"), FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Ink });
                sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Gray, Margin = new Thickness(0, 2, 0, 0) });
                tiles.Children.Add(Group(sp, new Thickness(0, 0, 10, 0)));
            }
            _content.Children.Add(tiles);
            _content.Children.Add(Caption($"{ws}  ·  共 {WorkspaceScanner.SizeText(r.TotalBytes)}  ·  {r.DirsScanned:N0} 个文件夹  ·  扫描用时 {r.Seconds:0.00} 秒" + (r.Newest > DateTime.MinValue ? $"  ·  最近修改 {When(r.Newest)}" : ""), Text3, 11.5, new Thickness(4, 0, 0, 16)));

            // 检查结果
            Section("检查");
            var checks = new StackPanel();
            void CheckRow(string title, string text, Brush color, string btn, Action click, bool last = false)
            {
                var d = new DockPanel { Margin = new Thickness(0, 0, 0, last ? 0 : 10) };
                if (btn != null) { var b = TextButton(btn, click); DockPanel.SetDock(b, Dock.Right); d.Children.Add(b); }
                var t = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                t.Inlines.Add(new System.Windows.Documents.Run(title + "  ") { Foreground = Ink, FontWeight = FontWeights.SemiBold });
                t.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = color });
                d.Children.Add(t);
                checks.Children.Add(d);
            }
            var di = st.Internal ?? new List<DupGroup>();
            CheckRow("工作区内同名", di.Count == 0 ? "没有" : $"危险 {di.Count(x => !x.Identical)} 组 · 副本 {di.Count(x => x.Identical)} 组", di.Any(x => !x.Identical) ? Orange : di.Count == 0 ? Green : Gray, di.Count == 0 ? null : "查看", () => SwitchTab("dup"));
            CheckRow("全电脑同名", st.Global == null ? "还没有检查（只读，通常几秒到一分钟）" : st.Global.Count == 0 ? "没有" : $"危险 {st.Global.Count(x => !x.Identical)} 组 · 副本 {st.Global.Count(x => x.Identical)} 组",
                     st.Global == null ? Text3 : st.Global.Any(x => !x.Identical) ? Orange : Green, st.Global == null ? "检查全电脑" : "查看", async () => { if (st.Global == null) await GlobalAsync(ws); SwitchTab("dup"); });
            var refIssues = st.Refs?.SelectMany(x => x.Issues).ToList();
            CheckRow("装配体引用", st.Refs == null ? "还没有检查（需要后台 SolidWorks，约 1 分钟）" : refIssues.Count == 0 ? "没有问题" : $"{refIssues.Count} 个提示",
                     st.Refs == null ? Text3 : refIssues.Any(i => i.Kind == "缺失" || i.Kind == "会用错文件") ? Orange : Green, st.Refs == null ? "检查引用" : "查看", async () => { if (st.Refs == null) await RefsAsync(ws); SwitchTab("dup"); }, last: true);
            _content.Children.Add(Group(checks));

            // 自动存档
            Section("版本历史");
            var hs = new StackPanel();
            hs.Children.Add(AutoSwitch());
            var store = new HistoryStore(ws);
            var hf = store.Files();
            hs.Children.Add(Caption(hf.Count == 0 ? "还没有存档。打开自动存档后，你每次在 SolidWorks 里保存，这里都会留一份；也可以在「版本历史」里手动「全部存档」。"
                                                  : $"已有 {hf.Count} 个文件的 {hf.Sum(x => x.Count)} 个历史版本，占用 {WorkspaceScanner.SizeText(store.TotalBytes())}，最近一次 {When(hf.Max(x => x.Latest))}。", Gray, 12, new Thickness(0, 8, 0, 0)));
            _content.Children.Add(Group(hs));

            // 最近修改
            Section("最近修改");
            var recent = new StackPanel();
            foreach (var f in r.SwFiles.OrderByDescending(f => f.Modified).Take(8)) recent.Children.Add(FileRow(f, ws, null, null, false));
            if (recent.Children.Count == 0) recent.Children.Add(Caption("这个文件夹里没有 SolidWorks 文件。", Text3));
            _content.Children.Add(Group(recent));
        }

        FrameworkElement AutoSwitch()
        {
            var cb = new CheckBox { Style = S("Switch"), IsChecked = Settings.AutoHistory, VerticalAlignment = VerticalAlignment.Center };
            cb.Content = new TextBlock { Text = "保存后自动存一份（软件开着时有效）", FontSize = 12.5, Foreground = Ink };
            cb.Checked += (s, e) => { Settings.AutoHistory = true; SaveSettings(); SetStatus("已打开自动存档：以后在 SolidWorks 里保存，会自动留一份历史版本", Green); };
            cb.Unchecked += (s, e) => { Settings.AutoHistory = false; SaveSettings(); SetStatus("已关闭自动存档"); };
            return cb;
        }

        // ---------------- 重名检查 ----------------

        void BuildDup()
        {
            var ws = _current; var st = State(ws);
            var top = new StackPanel();
            top.Children.Add(Caption("SolidWorks 是按文件名认文档的：内存里已经有一个叫「支架.SLDPRT」的文件时，再打开的装配体会直接用它，不管装配体当初引用的是哪个文件夹里的「支架」。所以不同文件夹里内容不同的同名文件，是装配体「莫名其妙变了」最常见的原因。", Gray, 12));
            var btns = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            btns.Children.Add(SecondaryButton("", st.Global == null ? "检查全电脑" : "重新检查全电脑", async () => await GlobalAsync(ws)));
            btns.Children.Add(SecondaryButton("", st.Refs == null ? "检查装配体引用" : "重新检查引用", async () => await RefsAsync(ws)));
            top.Children.Add(btns);
            top.Children.Add(Caption("全电脑检查只读文件名和大小，跳过 Windows、Program Files、回收站和各种缓存文件夹。引用检查在后台 SolidWorks 里进行，不打开文档，也不会碰你正在用的 SolidWorks。", Text3, 11.5, new Thickness(0, 8, 0, 0)));
            _content.Children.Add(Group(top));

            if (st.Scan == null) { _content.Children.Add(Caption("还没有扫描。", Text3)); return; }

            // 引用检查
            if (st.Refs != null)
            {
                var issues = st.Refs.SelectMany(r => r.Issues).ToList();
                Section($"装配体引用（{st.Refs.Count} 个文档、{st.Refs.Sum(r => r.Count)} 个引用，{st.RefSeconds:0} 秒）");
                if (issues.Count == 0) _content.Children.Add(Group(Caption("✓ 所有引用都能找到，而且 SolidWorks 实际会打开的就是装配体里记录的那个文件。", Green, 12.5)));
                var order = new[] { "缺失", "会用错文件", "位置变了", "工作区外" };
                foreach (var kind in order)
                {
                    var list = issues.Where(i => i.Kind == kind).ToList();
                    if (list.Count == 0) continue;
                    var sp = new StackPanel();
                    var head = new StackPanel { Orientation = Orientation.Horizontal };
                    head.Children.Add(new TextBlock { Text = kind, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ink });
                    var severe = kind == "缺失" || kind == "会用错文件";
                    head.Children.Add(Badge($"{list.Count} 处", severe ? Red : Gray, severe ? "#FFECEB" : "#ECECF0"));
                    sp.Children.Add(head);
                    sp.Children.Add(Caption(list[0].Detail, Gray, 12, new Thickness(0, 4, 0, 6)));
                    foreach (var i in list.Take(40))
                    {
                        var t = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), LineHeight = 18 };
                        t.Inlines.Add(new System.Windows.Documents.Run(i.Name) { Foreground = Ink, FontWeight = FontWeights.SemiBold });
                        t.Inlines.Add(new System.Windows.Documents.Run($"   在 {Rel(i.Document, ws)} 里") { Foreground = Gray });
                        if (kind != "工作区外") t.Inlines.Add(new System.Windows.Documents.Run("\n记录：" + i.Stored) { Foreground = Gray });
                        t.Inlines.Add(new System.Windows.Documents.Run("\n" + (kind == "缺失" ? "找不到" : "实际：") + (kind == "缺失" ? "" : i.Resolved)) { Foreground = severe ? Orange : Gray });
                        var row = new DockPanel();
                        var target = File.Exists(i.Resolved) ? i.Resolved : i.Document;
                        var b = TextButton("定位", () => Reveal(target)); DockPanel.SetDock(b, Dock.Right); row.Children.Add(b);
                        row.Children.Add(t);
                        sp.Children.Add(row);
                    }
                    if (list.Count > 40) sp.Children.Add(Caption($"…还有 {list.Count - 40} 处", Text3));
                    _content.Children.Add(Group(sp, new Thickness(0, 0, 0, 10)));
                }
                foreach (var r in st.Refs.Where(r => r.Error != null)) _content.Children.Add(Caption($"{Path.GetFileName(r.Document)}：{r.Error}", Red, 12, new Thickness(4, 0, 0, 8)));
            }

            // 全电脑
            if (st.Global != null)
            {
                Section($"全电脑（{string.Join("、", st.GlobalRoots)}：{st.GlobalFiles:N0} 个文件，{st.GlobalSeconds:0.0} 秒）—— 工作区外的同名文件", new Thickness(4, 10, 0, 8));
                if (st.Global.Count == 0) _content.Children.Add(Group(Caption("✓ 工作区外没有同名的 SolidWorks 文件。", Green, 12.5)));
                RenderGroups(st.Global, ws);
            }

            // 工作区内
            Section($"工作区内（{st.Scan.SwFiles.Count()} 个 SolidWorks 文件）", new Thickness(4, 10, 0, 8));
            if (st.Internal.Count == 0) _content.Children.Add(Group(Caption("✓ 工作区里没有同名文件。", Green, 12.5)));
            RenderGroups(st.Internal, ws);
        }

        void RenderGroups(List<DupGroup> groups, string ws, int max = 60)
        {
            foreach (var g in groups.Take(max))
            {
                var sp = new StackPanel();
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = g.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ink });
                head.Children.Add(g.Identical ? Badge("副本", Gray, "#ECECF0") : Badge("危险", Red, "#FFECEB"));
                head.Children.Add(new TextBlock { Text = $"  {g.All.Count()} 个文件" + (g.Identical ? "，内容完全相同" : $"，{g.Content.Values.Distinct().Count()} 种不同内容"), FontSize = 11.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(head);
                sp.Children.Add(Caption(g.Advice, Gray, 11.5, new Thickness(0, 4, 0, 6)));
                var multi = g.Content.Values.Distinct().Count() > 1;
                foreach (var f in g.Inside) sp.Children.Add(FileRow(f, ws, g.Global ? "内" : null, g.Content[f.Path], multi && g.Content[f.Path] != "A"));
                foreach (var f in g.Outside.Take(12)) sp.Children.Add(FileRow(f, ws, "外", g.Content[f.Path], multi && g.Content[f.Path] != "A"));
                if (g.Outside.Count > 12) sp.Children.Add(Caption($"…还有 {g.Outside.Count - 12} 个", Text3));
                _content.Children.Add(Group(sp, new Thickness(0, 0, 0, 10)));
            }
            if (groups.Count > max) _content.Children.Add(Caption($"…还有 {groups.Count - max} 组没有列出（可以用命令行 swkit-cli ws dup 看全部）", Text3, 12, new Thickness(4, 0, 0, 10)));
        }

        // ---------------- 版本历史 ----------------

        void BuildHistory()
        {
            var ws = _current; var st = State(ws);
            var store = new HistoryStore(ws);

            // 手动存档
            var top = new StackPanel();
            top.Children.Add(Caption("在改大版本、做有风险的修改之前，先「全部存档」一次。只存和上一版不一样的文件，不会重复占空间。", Gray, 12));
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var note = new TextBox { Style = S("Field"), Width = 300, ToolTip = "备注（可选），例如「改电机座之前」" };
            var saveAll = SecondaryButton("", "全部存档", async () => await SnapshotAllAsync(ws, note.Text));
            saveAll.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(saveAll, Dock.Right);
            var lbl = new TextBlock { Text = "备注", FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(lbl, Dock.Left);
            row.Children.Add(lbl); row.Children.Add(saveAll); row.Children.Add(note);
            note.HorizontalAlignment = HorizontalAlignment.Left;
            top.Children.Add(row);
            _content.Children.Add(Group(top));

            // 自动存档设置
            Section("自动存档");
            var set = new StackPanel();
            set.Children.Add(AutoSwitch());
            var nums = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            TextBox Num(string value, Action<double> commit, double min, double max)
            {
                var tb = new TextBox { Style = S("Field"), Width = 56, Text = value, TextAlignment = TextAlignment.Right, Margin = new Thickness(6, 0, 6, 0) };
                void Commit()
                {
                    if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max) commit(v);
                    else { tb.Text = value; SetStatus($"请输入 {min}–{max} 之间的数", Orange); }
                }
                tb.LostFocus += (s, e) => Commit();
                tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) Commit(); };
                return tb;
            }
            nums.Children.Add(new TextBlock { Text = "每个文件保留", FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
            nums.Children.Add(Num(Settings.KeepPerFile.ToString(), v => { Settings.KeepPerFile = (int)Math.Round(v); SaveSettings(); }, 1, 1000));
            nums.Children.Add(new TextBlock { Text = "个版本，历史库最多", FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
            nums.Children.Add(Num(Settings.MaxHistoryGb.ToString("0.##", CultureInfo.InvariantCulture), v => { Settings.MaxHistoryGb = v; SaveSettings(); }, 0.1, 1000));
            nums.Children.Add(new TextBlock { Text = "GB（超出时从最旧的删起，每个文件的最新版本总会保留）", FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
            set.Children.Add(nums);
            var used = store.TotalBytes();
            var usedRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var prune = TextButton("立即清理", () =>
            {
                var n = store.Prune(Settings.KeepPerFile, (long)(Settings.MaxHistoryGb * (1L << 30)));
                SetStatus(n == 0 ? "没有需要清理的旧版本" : $"已删除 {n} 个超出保留数量的旧版本", Green);
                Render(scrollTop: false);
            });
            DockPanel.SetDock(prune, Dock.Right); usedRow.Children.Add(prune);
            var openLib = TextButton("打开历史库", () => { Directory.CreateDirectory(store.Dir); OpenFolder(store.Dir); });
            DockPanel.SetDock(openLib, Dock.Right); usedRow.Children.Add(openLib);
            usedRow.Children.Add(Caption($"历史库占用 {WorkspaceScanner.SizeText(used)}，放在 {HistoryStore.Root}（不往项目文件夹里写任何东西）", Text3, 11.5));
            set.Children.Add(usedRow);
            _content.Children.Add(Group(set));

            // 文件和版本
            var files = store.Files();
            Section($"有历史版本的文件（{files.Count}）");
            if (files.Count == 0)
            {
                _content.Children.Add(Group(Caption("还没有历史版本。点上面的「全部存档」，或者打开自动存档后在 SolidWorks 里保存一次。", Text3, 12.5)));
                return;
            }
            var filter = new TextBox { Style = S("Field"), Width = 240, Text = st.Filter, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10), ToolTip = "按文件名筛选" };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            var left = new StackPanel();
            var right = new StackPanel();
            if (st.HistoryFile == null || !files.Any(f => string.Equals(f.Path, st.HistoryFile, StringComparison.OrdinalIgnoreCase))) st.HistoryFile = files[0].Path;

            void FillLeft()
            {
                left.Children.Clear();
                var shown = files.Where(f => st.Filter.Length == 0 || f.Rel.IndexOf(st.Filter, StringComparison.OrdinalIgnoreCase) >= 0).Take(200).ToList();
                foreach (var f in shown)
                {
                    var sel = string.Equals(f.Path, st.HistoryFile, StringComparison.OrdinalIgnoreCase);
                    var b = new Border { Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Background = sel ? Ui.Brush("#EAF3FE") : Brushes.Transparent, ToolTip = f.Path };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = Path.GetFileName(f.Rel), FontSize = 12.5, Foreground = f.Exists ? Ink : Orange, FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
                    sp.Children.Add(new TextBlock { Text = $"{f.Count} 个版本 · 最近 {When(f.Latest)}" + (f.Exists ? "" : " · 文件已不在原处") + (Path.GetDirectoryName(f.Rel).Length > 0 ? " · " + Path.GetDirectoryName(f.Rel) : ""), FontSize = 11, Foreground = Gray, TextTrimming = TextTrimming.CharacterEllipsis });
                    b.Child = sp;
                    var path = f.Path;
                    b.MouseEnter += (s, e) => { if (!string.Equals(path, st.HistoryFile, StringComparison.OrdinalIgnoreCase)) b.Background = Ui.Brush("#F2F2F7"); };
                    b.MouseLeave += (s, e) => { if (!string.Equals(path, st.HistoryFile, StringComparison.OrdinalIgnoreCase)) b.Background = Brushes.Transparent; };
                    b.MouseLeftButtonUp += (s, e) => { st.HistoryFile = path; FillLeft(); FillRight(); };
                    left.Children.Add(b);
                }
                if (shown.Count == 0) left.Children.Add(Caption("没有匹配的文件", Text3));
            }

            void FillRight()
            {
                right.Children.Clear();
                var file = st.HistoryFile;
                List<HistoryVersion> vs;
                try { vs = store.Versions(file); } catch { vs = new List<HistoryVersion>(); }
                right.Children.Add(new TextBlock { Text = Path.GetFileName(file), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis });
                var cur = File.Exists(file) ? WorkspaceScanner.FileHash(file) : null;
                right.Children.Add(Caption(File.Exists(file) ? $"当前文件：{When(File.GetLastWriteTime(file))} 修改，{WorkspaceScanner.SizeText(new FileInfo(file).Length)}" : "当前文件已经不在原处，可以把某个版本恢复回去或另存出来。", Gray, 11.5, new Thickness(0, 2, 0, 10)));
                foreach (var v in vs)
                {
                    var r = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var t = new StackPanel();
                    var l1 = new StackPanel { Orientation = Orientation.Horizontal };
                    l1.Children.Add(new TextBlock { Text = v.Time.ToString("yyyy-MM-dd HH:mm:ss"), FontSize = 12.5, Foreground = Ink });
                    var reasonColor = v.Reason == "手动" ? Accent : v.Reason == "恢复" || v.Reason == "恢复前" ? Orange : Gray;
                    l1.Children.Add(Badge(v.Reason, reasonColor, v.Reason == "手动" ? "#EAF3FE" : v.Reason == "恢复" || v.Reason == "恢复前" ? "#FFF2E0" : "#ECECF0"));
                    if (cur != null && v.Sha1 == cur) l1.Children.Add(Badge("和当前相同", Green, "#E8F6EC"));
                    t.Children.Add(l1);
                    t.Children.Add(new TextBlock { Text = WorkspaceScanner.SizeText(v.Size) + (string.IsNullOrEmpty(v.Note) ? "" : " · " + v.Note), FontSize = 11.5, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                    r.Children.Add(t);
                    var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    var vv = v;
                    if (!(cur != null && v.Sha1 == cur)) acts.Children.Add(TextButton("恢复", async () => await RestoreAsync(ws, file, vv, confirm: true), "用这个版本替换当前文件（当前文件会先自动存一份）"));
                    acts.Children.Add(TextButton("另存为", () => SaveCopyAs(vv.StoredPath, Path.GetFileNameWithoutExtension(file) + "_" + vv.Time.ToString("MMdd-HHmm") + Path.GetExtension(file)), "另存一份，不影响当前文件"));
                    Grid.SetColumn(acts, 1); r.Children.Add(acts);
                    right.Children.Add(new Border { Child = r, Padding = new Thickness(0, 6, 0, 6), BorderBrush = Ui.Brush("#EFEFF2"), BorderThickness = new Thickness(0, 0, 0, 1) });
                }
            }

            filter.TextChanged += (s, e) => { st.Filter = filter.Text.Trim(); FillLeft(); };
            FillLeft(); FillRight();
            var leftHost = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            var filterRow = new StackPanel { Orientation = Orientation.Horizontal };
            filterRow.Children.Add(new TextBlock { Text = "", Style = S("T.Icon"), FontSize = 13, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 10) });
            filterRow.Children.Add(filter);
            leftHost.Children.Add(filterRow); leftHost.Children.Add(left);
            grid.Children.Add(leftHost);
            var rightCard = Group(right); Grid.SetColumn(rightCard, 1); rightCard.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(rightCard);
            _content.Children.Add(grid);
        }

        // ---------------- 崩溃找回 ----------------

        void BuildRecover()
        {
            var intro = new StackPanel();
            intro.Children.Add(Caption("两种情况分开找：", Ink, 12.5));
            intro.Children.Add(Caption("· SolidWorks 崩溃、没来得及保存：看下面 SolidWorks 自己的「自动恢复」文件。重新打开 SolidWorks 时它通常会在右侧「文档恢复」里提示；如果错过了，可以在这里把文件另存出来。", Gray, 12, new Thickness(0, 4, 0, 0)));
            intro.Children.Add(Caption("· 已经保存了，但改坏了、存错了：用「版本历史」退回去（需要事先打开自动存档或手动存过）。", Gray, 12, new Thickness(0, 4, 0, 0)));
            var go = TextButton("去版本历史", () => SwitchTab("history"));
            go.HorizontalAlignment = HorizontalAlignment.Left; go.Margin = new Thickness(-6, 6, 0, 0);
            intro.Children.Add(go);
            _content.Children.Add(Group(intro));

            Section("SolidWorks 的恢复设置");
            var set = new StackPanel();
            var settings = SwRecovery.ReadSettings();
            if (settings.Count == 0) set.Children.Add(Caption("没有在注册表里找到 SolidWorks 的设置。", Text3));
            foreach (var s in settings)
            {
                var t = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
                t.Inlines.Add(new System.Windows.Documents.Run(s.Version + "  ") { FontWeight = FontWeights.SemiBold, Foreground = Ink });
                t.Inlines.Add(new System.Windows.Documents.Run("自动恢复 " + (s.AutoRecoverOn ? "已开启" : "已关闭")) { Foreground = s.AutoRecoverOn ? Green : Red });
                t.Inlines.Add(new System.Windows.Documents.Run("  ·  保存时保留备份 " + (s.BackupsOn ? $"已开启（{s.BackupCopies} 份）" : "已关闭")) { Foreground = s.BackupsOn ? Green : Gray });
                set.Children.Add(t);
            }
            var off = new List<string>();
            if (settings.Any(s => !s.AutoRecoverOn)) off.Add("「自动恢复」");
            if (settings.Any(s => !s.BackupsOn)) off.Add("「每个文档的备份副本数」");
            if (off.Count > 0)
                set.Children.Add(Caption($"建议在 SolidWorks 的 工具 → 选项 → 系统选项 → 备份/恢复 里打开{string.Join("和", off)}。本工具的版本历史和它们互不影响，可以同时用。", Gray, 12, new Thickness(0, 6, 0, 0)));
            set.Children.Add(new Border { Height = 1, Background = Ui.Brush("#EFEFF2"), Margin = new Thickness(0, 10, 0, 8) });
            foreach (var f in SwRecovery.Folders())
            {
                var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                if (f.Exists) { var path = f.Path; var b = TextButton("打开", () => OpenFolder(path)); DockPanel.SetDock(b, Dock.Right); d.Children.Add(b); }
                var t = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = f.Path };
                t.Inlines.Add(new System.Windows.Documents.Run(f.Kind + (f.IsDefault ? "（默认位置）" : "") + "  ") { Foreground = Ink });
                t.Inlines.Add(new System.Windows.Documents.Run(f.Path + (f.Exists ? "" : "  · 还没有生成")) { Foreground = f.Exists ? Gray : Text3 });
                d.Children.Add(t);
                set.Children.Add(d);
            }
            _content.Children.Add(Group(set));

            Section("恢复 / 备份文件");
            var list = new StackPanel();
            var files = SwRecovery.Files();
            if (files.Count == 0)
                list.Children.Add(Caption("现在没有恢复文件。SolidWorks 正常关闭时会自动删掉它们，这是好事。万一崩溃了，在重新打开 SolidWorks 之前先来这里看看，把需要的文件另存出来。", Text3, 12.5));
            foreach (var (f, kind) in files.Take(100))
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                int c = 0;
                void Add(UIElement e) { Grid.SetColumn(e, c++); g.Children.Add(e); }
                var badge = Badge(kind, kind == "自动恢复" ? Orange : Gray, kind == "自动恢复" ? "#FFF2E0" : "#ECECF0"); badge.Margin = new Thickness(0, 0, 10, 0);
                Add(badge);
                Add(new TextBlock { Text = f.Name, FontSize = 12.5, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f.Path, VerticalAlignment = VerticalAlignment.Center });
                Add(new TextBlock { Text = When(f.Modified), FontSize = 11.5, Foreground = Gray, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                Add(new TextBlock { Text = WorkspaceScanner.SizeText(f.Size), FontSize = 11.5, Foreground = Gray, Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                var acts = new StackPanel { Orientation = Orientation.Horizontal };
                var p = f.Path;
                acts.Children.Add(TextButton("另存为", () => SaveCopyAs(p, SwRecovery.SuggestName(p))));
                acts.Children.Add(TextButton("定位", () => Reveal(p)));
                Add(acts);
                list.Children.Add(g);
            }
            _content.Children.Add(Group(list));
        }

        // ================= 给 AI / 脚本用的接口 =================

        internal static IEnumerable<ApiAction> ApiActions => new[]
        {
            new ApiAction("list", "列出所有工作区（含已扫描的统计）"),
            new ApiAction("add", "添加工作区并扫描", "path: 文件夹"),
            new ApiAction("remove", "从列表移除工作区（不删除文件）", "path"),
            new ApiAction("select", "在界面上切换到某个工作区和标签页", "path?, tab?: overview | dup | history | recover"),
            new ApiAction("scan", "重新扫描，返回文件统计和工作区内的同名文件数", "path?（默认当前工作区）"),
            new ApiAction("duplicates", "同名文件检查；global=true 时再查全电脑（只读，几秒到一分钟）", "path?, global?: bool, roots?: [磁盘或文件夹]"),
            new ApiAction("refs", "在后台 SolidWorks 里检查装配体 / 工程图引用（缺失、会用错文件、位置变了、工作区外）", "path?, files?: [装配体]"),
            new ApiAction("snapshot", "全部存档（只存有变化的文件）", "path?, note?"),
            new ApiAction("history", "某个文件的历史版本；不给 file 时列出有历史的文件", "file?, path?"),
            new ApiAction("restore", "把文件恢复成某个版本（当前文件会先自动存一份；文件在 SolidWorks 里打开着时会失败）", "file, version: 版本 id"),
            new ApiAction("settings", "读取 / 修改自动存档设置", "auto?: bool, keep?: 每个文件保留的版本数, maxGb?: 历史库上限"),
            new ApiAction("recovery_files", "SolidWorks 的自动恢复 / 备份设置、文件夹和文件"),
        };

        static object Group2Json(DupGroup g) => ApiArgs.Ret(
            "name", g.Name, "level", g.Level, "identical", g.Identical, "advice", g.Advice,
            "files", g.Inside.Select(f => (object)ApiArgs.Ret("path", f.Path, "where", "inside", "content", g.Content[f.Path], "size", f.Size, "modified", f.Modified.ToString("s")))
                     .Concat(g.Outside.Select(f => (object)ApiArgs.Ret("path", f.Path, "where", "outside", "content", g.Content[f.Path], "size", f.Size, "modified", f.Modified.ToString("s")))).ToList());

        static object Version2Json(HistoryVersion v) => v == null ? null : ApiArgs.Ret("id", v.Id, "time", v.Time.ToString("s"), "reason", v.Reason, "size", v.Size, "sha1", v.Sha1, "note", v.Note, "file", v.Source);

        static object Scan2Json(string ws, ScanResult r, List<DupGroup> dups) => r == null ? (object)ApiArgs.Ret("path", ws, "scanned", false, "exists", Directory.Exists(ws)) : ApiArgs.Ret(
            "path", ws, "scanned", true, "parts", r.Parts, "assemblies", r.Assemblies, "drawings", r.Drawings, "step", r.Steps, "stl", r.Stls,
            "bytes", r.TotalBytes, "newest", r.Newest.ToString("s"), "seconds", Math.Round(r.Seconds, 2),
            "duplicates", dups == null ? null : ApiArgs.Ret("danger", dups.Count(d => !d.Identical), "copies", dups.Count(d => d.Identical)));

        string WsArg(Dictionary<string, object> args, string fileHint = null)
        {
            var p = args.Str("path");
            if (p != null)
            {
                var n = WorkspaceSettings.Normalize(p);
                var ws = Settings.Workspaces.FirstOrDefault(w => string.Equals(w, n, StringComparison.OrdinalIgnoreCase)) ?? Settings.WorkspaceOf(n);
                return ws ?? throw new ArgumentException("不是已添加的工作区：" + p + "（先用 add 添加）");
            }
            if (fileHint != null) return Settings.WorkspaceOf(Path.GetFullPath(fileHint)) ?? throw new ArgumentException("文件不在任何工作区里：" + fileHint);
            return _current ?? throw new ArgumentException("还没有工作区，先用 add 添加一个");
        }

        void Show(string ws, string tab)
        {
            if (!string.Equals(ws, _current, StringComparison.OrdinalIgnoreCase)) Select(ws);
            if (tab != null) SwitchTab(tab);
        }

        internal async Task<object> ApiAsync(string action, Dictionary<string, object> args)
        {
            args = args ?? new Dictionary<string, object>();
            switch (action)
            {
                case "list":
                    return ApiArgs.Ret("current", _current, "workspaces", Settings.Workspaces.Select(w => Scan2Json(w, _state.TryGetValue(w, out var s) ? s.Scan : null, s?.Internal)).ToList());

                case "add":
                {
                    if (IsBusy) throw new InvalidOperationException("正在进行别的操作，请稍后再试");
                    var p = args.ReqStr("path");
                    var norm = WorkspaceSettings.Normalize(p);
                    if (!Settings.Workspaces.Contains(norm, StringComparer.OrdinalIgnoreCase))
                    {
                        if (norm.Length <= 3) throw new ArgumentException("不能把整个磁盘作为工作区");
                        Settings.Add(norm);
                        SaveSettings();
                        BuildWsList();
                    }
                    Show(norm, "overview");
                    while (IsBusy) await Task.Delay(100);
                    if (State(norm).Scan == null) await ScanAsync(norm);
                    return Scan2Json(norm, State(norm).Scan, State(norm).Internal);
                }

                case "remove":
                {
                    var ws = WsArg(args);
                    RemoveWorkspace(ws, confirm: false);
                    return ApiArgs.Ret("removed", ws);
                }

                case "select":
                {
                    var ws = WsArg(args);
                    var tab = args.Str("tab");
                    if (tab != null && !_tabs.ContainsKey(tab)) throw new ArgumentException("tab 只能是 overview / dup / history / recover");
                    Show(ws, tab);
                    return ApiArgs.Ret("current", ws, "tab", _tab);
                }

                case "scan":
                {
                    var ws = WsArg(args);
                    Show(ws, "overview");
                    while (IsBusy) await Task.Delay(100);
                    var r = await ScanAsync(ws) ?? throw new InvalidOperationException(_status.Text);
                    return Scan2Json(ws, r, State(ws).Internal);
                }

                case "duplicates":
                {
                    var ws = WsArg(args);
                    Show(ws, "dup");
                    while (IsBusy) await Task.Delay(100);
                    var st = State(ws);
                    if (st.Scan == null && await ScanAsync(ws) == null) throw new InvalidOperationException(_status.Text);
                    object global = null;
                    if (args.Bool("global"))
                    {
                        var roots = args.List("roots").Select(x => x.ToString()).Where(Directory.Exists).ToList();
                        var g = await GlobalAsync(ws, roots.Count > 0 ? roots : null) ?? throw new InvalidOperationException(_status.Text);
                        global = ApiArgs.Ret("roots", st.GlobalRoots, "filesScanned", st.GlobalFiles, "seconds", Math.Round(st.GlobalSeconds, 1), "groups", g.Select(Group2Json).ToList());
                    }
                    return ApiArgs.Ret("workspace", ws, "inside", st.Internal.Select(Group2Json).ToList(), "global", global);
                }

                case "refs":
                {
                    var ws = WsArg(args);
                    Show(ws, "dup");
                    while (IsBusy) await Task.Delay(100);
                    var only = args.List("files").Select(x => Path.GetFullPath(x.ToString())).ToList();
                    var reps = await RefsAsync(ws, only.Count > 0 ? only : null) ?? throw new InvalidOperationException(_status.Text);
                    return ApiArgs.Ret("workspace", ws, "documents", reps.Select(r => (object)ApiArgs.Ret("document", r.Document, "references", r.Count, "error", r.Error,
                        "issues", r.Issues.Select(i => (object)ApiArgs.Ret("kind", i.Kind, "name", i.Name, "in", i.Document, "stored", i.Stored, "resolved", i.Resolved, "detail", i.Detail)).ToList())).ToList());
                }

                case "snapshot":
                {
                    var ws = WsArg(args);
                    Show(ws, "history");
                    while (IsBusy) await Task.Delay(100);
                    var (saved, unchanged, failed) = await SnapshotAllAsync(ws, args.Str("note"));
                    return ApiArgs.Ret("workspace", ws, "saved", saved, "unchanged", unchanged, "failed", failed);
                }

                case "history":
                {
                    var file = args.Str("file");
                    var ws = WsArg(args, file);
                    var store = new HistoryStore(ws);
                    if (file == null)
                    {
                        Show(ws, "history");
                        return ApiArgs.Ret("workspace", ws, "bytes", store.TotalBytes(), "files", store.Files().Select(f => (object)ApiArgs.Ret("file", f.Path, "versions", f.Count, "latest", f.Latest.ToString("s"), "exists", f.Exists)).ToList());
                    }
                    file = Path.GetFullPath(file);
                    State(ws).HistoryFile = file;
                    Show(ws, "history");
                    if (_tab == "history") Render(scrollTop: false);
                    return ApiArgs.Ret("workspace", ws, "file", file, "currentSha1", File.Exists(file) ? WorkspaceScanner.FileHash(file) : null, "versions", store.Versions(file).Select(Version2Json).ToList());
                }

                case "restore":
                {
                    var file = Path.GetFullPath(args.ReqStr("file"));
                    var ws = WsArg(args, file);
                    var id = args.ReqStr("version");
                    var v = new HistoryStore(ws).Versions(file).FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("找不到这个版本：" + id + "（先用 history 查看版本 id）");
                    State(ws).HistoryFile = file;
                    Show(ws, "history");
                    while (IsBusy) await Task.Delay(100);
                    var r = await RestoreAsync(ws, file, v, confirm: false);
                    return ApiArgs.Ret("file", file, "restoredFrom", id, "record", Version2Json(r));
                }

                case "settings":
                {
                    bool changed = false;
                    if (args.Has("auto")) { Settings.AutoHistory = args.Bool("auto"); changed = true; }
                    if (args.Has("keep")) { var k = args.Int("keep"); if (k < 1 || k > 1000) throw new ArgumentException("keep 要在 1–1000 之间"); Settings.KeepPerFile = k; changed = true; }
                    if (args.Has("maxGb")) { var g = args.Num("maxGb"); if (g < 0.1 || g > 1000) throw new ArgumentException("maxGb 要在 0.1–1000 之间"); Settings.MaxHistoryGb = g; changed = true; }
                    if (changed) { SaveSettings(); if (_tab == "history" || _tab == "overview") Render(scrollTop: false); }
                    return ApiArgs.Ret("auto", Settings.AutoHistory, "keep", Settings.KeepPerFile, "maxGb", Settings.MaxHistoryGb, "historyRoot", HistoryStore.Root, "watching", _watcher?.Watched.ToList());
                }

                case "recovery_files":
                {
                    if (_current != null) SwitchTab("recover");
                    return ApiArgs.Ret(
                        "settings", SwRecovery.ReadSettings().Select(s => (object)ApiArgs.Ret("version", s.Version, "autoRecover", s.AutoRecoverOn, "backups", s.BackupsOn, "backupCopies", s.BackupCopies)).ToList(),
                        "folders", SwRecovery.Folders().Select(f => (object)ApiArgs.Ret("kind", f.Kind, "path", f.Path, "exists", f.Exists, "default", f.IsDefault)).ToList(),
                        "files", SwRecovery.Files().Select(x => (object)ApiArgs.Ret("kind", x.kind, "path", x.file.Path, "size", x.file.Size, "modified", x.file.Modified.ToString("s"), "suggestedName", SwRecovery.SuggestName(x.file.Path))).ToList());
                }

                default:
                    throw new ArgumentException("未知操作：" + action + "。可用：" + string.Join("、", ApiActions.Select(a => a.Name)));
            }
        }

        // ================= 自测（开发用） =================

        /// <summary>添加工作区 → 扫描 → 全电脑检查 → 引用检查 → 全部存档 → 改一个文件 → 自动存档 → 恢复，每一步截图。</summary>
        internal async Task<string> SelfTest(string ws, string fileToTouch, Func<string, Task> shot)
        {
            var sb = new StringBuilder();
            async Task<object> Call(string action, Dictionary<string, object> a)
            {
                try { var r = await ApiAsync(action, a); sb.AppendLine($"{action}：{_status.Text}"); return r; }
                catch (Exception ex) { sb.AppendLine($"{action} 出错：{ex.Message}"); return null; }
            }
            await Call("add", ApiArgs.Ret("path", ws));
            await shot("1-概览");
            await Call("duplicates", ApiArgs.Ret("global", true));
            await Call("refs", new Dictionary<string, object>());
            await Task.Delay(300);
            await shot("2-重名检查");
            await Call("snapshot", ApiArgs.Ret("note", "自测：初始"));
            if (fileToTouch != null && File.Exists(fileToTouch))
            {
                File.AppendAllText(fileToTouch, "SWKIT-SELFTEST");
                var waited = 0;
                var before = new HistoryStore(ws).Versions(fileToTouch).Count;
                while (waited < 15000 && new HistoryStore(ws).Versions(fileToTouch).Count == before) { await Task.Delay(500); waited += 500; }
                sb.AppendLine($"自动存档：改动后 {waited / 1000.0:0.0} 秒内版本数 {before} → {new HistoryStore(ws).Versions(fileToTouch).Count}");
                var vs = new HistoryStore(ws).Versions(fileToTouch);
                var oldest = vs.Last();
                await Call("restore", ApiArgs.Ret("file", fileToTouch, "version", oldest.Id));
                sb.AppendLine($"恢复后内容和最早版本相同：{WorkspaceScanner.FileHash(fileToTouch) == oldest.Sha1}");
                await Call("history", ApiArgs.Ret("file", fileToTouch));
            }
            await Task.Delay(300);
            await shot("3-版本历史");
            SwitchTab("recover");
            await Task.Delay(300);
            await shot("4-崩溃找回");
            return sb.ToString();
        }
    }
}
