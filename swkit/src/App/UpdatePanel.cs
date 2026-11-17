using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SwKit.App
{
    /// <summary>
    /// 侧栏底部的版本号和在线更新（检查 → 有新版本 → 下载 → 校验 → 关闭软件、安装、自动重新打开），以及「看更新内容」浮层。
    /// 逻辑在 Updater.cs；这里只管界面状态。
    /// </summary>
    sealed class UpdatePanel
    {
        readonly MainWindow _win;
        readonly Grid _root;
        readonly Border _card;
        readonly TextBlock _icon, _title, _detail, _version;
        readonly ProgressBar _bar;
        readonly FrameworkElement _line, _buttons;
        readonly Button _primary, _link1, _link2, _dismiss, _check;
        Action _onPrimary, _onLink1, _onLink2, _onDismiss;

        ReleaseInfo _latest;                 // 比当前版本新的发布（没有为 null）
        bool _busy;                          // 正在检查 / 下载 / 安装
        CancellationTokenSource _download;
        ProcessStartInfo _pendingInstall;    // 窗口关闭后启动的安装程序
        bool _closed;
        DispatcherTimer _revert;
        Grid _notes;                         // 更新内容浮层
        TextBlock _notesTitle, _notesCaption;
        FlowDocumentScrollViewer _notesReader;
        Button _notesUpdate;
        ReleaseInfo _notesRelease;
        Action<string> _trace;               // 自测时记录每一步
        Action<double> _onProgress;          // 自测时下载到一半截图

        static Brush R(string key) => (Brush)Application.Current.FindResource(key);
        static readonly Brush TintBlue = Ui.Brush("#EAF3FE"), TintGray = Ui.Brush("#EBEBEF"), Blue = Ui.Brush("#0071E3"), Orange = Ui.Brush("#C25E00"), Red = Ui.Brush("#D70015");

        public UpdatePanel(MainWindow win, FrameworkElement root)
        {
            _win = win;
            _root = (Grid)root;
            _card = root.Find<Border>("UpdCard");
            _icon = root.Find<TextBlock>("UpdIcon");
            _title = root.Find<TextBlock>("UpdTitle");
            _detail = root.Find<TextBlock>("UpdDetail");
            _bar = root.Find<ProgressBar>("UpdProgress");
            _buttons = root.Find<FrameworkElement>("UpdButtons");
            _primary = root.Find<Button>("UpdPrimary");
            _link1 = root.Find<Button>("UpdLink1");
            _link2 = root.Find<Button>("UpdLink2");
            _dismiss = root.Find<Button>("UpdDismiss");
            _line = root.Find<FrameworkElement>("UpdLine");
            _version = root.Find<TextBlock>("UpdVersion");
            _check = root.Find<Button>("UpdCheckBtn");

            _primary.Click += (s, e) => _onPrimary?.Invoke();
            _link1.Click += (s, e) => _onLink1?.Invoke();
            _link2.Click += (s, e) => _onLink2?.Invoke();
            _dismiss.Click += (s, e) => _onDismiss?.Invoke();
            _check.Click += async (s, e) =>
            {
                if (_busy) return;
                if (_latest != null) { ShowAvailable(_latest); return; }   // 收起过的「有新版本」
                try { await CheckAsync(true); } catch (UpdateException) { }
            };
            _win.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && _notes != null && _notes.Visibility == Visibility.Visible) { HideNotes(); e.Handled = true; } };
            ShowIdle();
        }

        /// <summary>窗口加载完调用：显示上次查到的新版本；超过一天没查过就在后台查一次（失败不打扰）。开发模式不联网。</summary>
        internal void Start()
        {
            var selfTest = Environment.GetEnvironmentVariable("SWKIT_UPDATE_SELFTEST");
            if (!string.IsNullOrWhiteSpace(selfTest)) { _ = SelfTestAsync(selfTest.Trim()); return; }
            if (MainWindow.DevMode) return;
            var state = Updater.LoadState();
            if (state.Latest != null && Updater.IsNewer(state.Latest.Version, Updater.CurrentVersion)) { _latest = state.Latest; ShowAvailable(_latest); }
            Updater.CleanupDownloads();
            if (state.LastCheck == null || DateTime.UtcNow - state.LastCheck.Value > TimeSpan.FromHours(24) || state.LastCheck.Value > DateTime.UtcNow.AddMinutes(5))
                _ = AutoCheckLater();
        }

        async Task AutoCheckLater()
        {
            await Task.Delay(4000);   // 等软件启动完再查，不和打开页面抢
            try { await CheckAsync(false); } catch { }
        }

        // ================= 检查 =================

        /// <summary>查最新版本并更新界面。manual：手动检查，结果（已是最新 / 连不上）都要明确告诉用户；自动检查失败不打扰。</summary>
        async Task<ReleaseInfo> CheckAsync(bool manual)
        {
            if (_busy) throw new UpdateException("正在检查或下载更新，请稍等");
            _busy = true;
            if (manual) ShowChecking();
            try
            {
                Trace($"检查更新：{Updater.ApiUrl}");
                ReleaseInfo r;
                try { r = await Updater.FetchLatestAsync(CancellationToken.None); }
                catch { Updater.SaveState(DateTime.UtcNow, Updater.LoadState().Latest); throw; }   // 没查成也记下时间（自动检查每天最多一次），保留上次查到的新版本
                bool newer = Updater.IsNewer(r.Version, Updater.CurrentVersion);
                Updater.SaveState(DateTime.UtcNow, newer ? r : null);
                Trace($"最新 {r.Tag}（{r.Published}），当前 v{Updater.CurrentVersion} → {(newer ? "有新版本" : "已是最新")}；安装程序 {r.AssetName ?? "无"} {r.AssetSize:N0} 字节 {r.AssetDigest ?? "无 digest"}");
                _busy = false;
                if (newer) { _latest = r; ShowAvailable(r); }
                else { _latest = null; if (manual) ShowUpToDate(); else ShowIdle(); }
                return r;
            }
            catch (Exception ex)
            {
                var ue = ex as UpdateException ?? new UpdateException("检查更新失败：" + ex.Message, UpdateException.Kinds.Other, ex);
                Trace("检查失败：" + ue.Message + (ue.InnerException != null ? "（" + ue.InnerException.GetBaseException().Message + "）" : ""));
                _busy = false;
                if (manual) ShowCheckFailed(ue);
                else if (_latest != null) ShowAvailable(_latest); else ShowIdle();
                throw ue;
            }
            finally { _busy = false; }
        }

        /// <summary>AI 接口 app.check_update：查一次，返回版本信息（不安装；安装要用户在软件里点「更新」）。</summary>
        internal async Task<object> ApiCheckAsync()
        {
            ReleaseInfo r;
            if (_busy)
            {
                if (_latest == null) throw new UpdateException("正在检查更新，请稍后再试");
                r = _latest;
            }
            else r = await CheckAsync(true);
            bool newer = Updater.IsNewer(r.Version, Updater.CurrentVersion);
            return new Dictionary<string, object>
            {
                ["current"] = Updater.CurrentVersion, ["latest"] = r.Version, ["update_available"] = newer,
                ["notes_url"] = r.PageUrl, ["published"] = r.Published, ["notes"] = Updater.NotesForApp(r.Notes),
                ["installer"] = r.AssetName, ["installer_size"] = r.AssetSize,
                ["how_to_update"] = newer ? "请用户点软件左下角的「更新」：自动下载、校验、关闭软件、安装并重新打开" : "已经是最新版本",
            };
        }

        // ================= 下载和安装 =================

        async Task UpdateAsync()
        {
            var r = _latest;
            if (r == null || _busy) return;
            if (r.AssetUrl == null) { Updater.OpenUrl(r.PageUrl); return; }
            var dir = Updater.AppDir;
            if (!Updater.CanWrite(dir))
            {
                Trace("没有写入权限：" + dir);
                ShowAvailable(r, $"没有权限写入软件所在的文件夹（{dir}），没法自动更新。请打开下载页，手动下载安装程序。", false);
                return;
            }
            _busy = true;
            _download = new CancellationTokenSource();
            var started = DateTime.Now;
            try
            {
                Trace($"下载 {r.AssetUrl}");
                ShowDownloading(r, 0, r.AssetSize);
                var last = DateTime.MinValue;
                var file = await Updater.DownloadAsync(r,
                    (done, total) =>
                    {
                        if ((DateTime.Now - last).TotalMilliseconds < 100 && done < total) return;   // 界面每秒最多刷新 10 次
                        last = DateTime.Now;
                        _win.Dispatcher.BeginInvoke(new Action(() => { if (_busy && _download != null && !_download.IsCancellationRequested) ShowDownloading(r, done, total); }));
                    },
                    () => _win.Dispatcher.BeginInvoke(new Action(() => ShowVerifying(r))),
                    _download.Token);
                Trace($"下载完成并校验通过（{(DateTime.Now - started).TotalSeconds:0.0} 秒）：{file}，SHA-256 {Updater.Sha256(file)}");

                bool installed = Updater.IsInstalledCopy(dir);
                var log = Path.Combine(Updater.DownloadDir, "install.log");
                var psi = Updater.InstallerCommand(file, dir, installed, Process.GetCurrentProcess().Id, log);
                Trace($"{(installed ? "安装版" : "绿色版")}，启动安装程序：\"{psi.FileName}\" {psi.Arguments}");
                ShowInstalling(r);
                await Task.Delay(600);   // 让用户看到「正在更新」

                // 关窗口：沿用「还有任务在进行中，确定要关闭吗？」的确认；用户取消就不更新
                _pendingInstall = psi;
                _closed = false;
                _win.Close();
                if (!_closed)
                {
                    _pendingInstall = null;
                    Trace("用户取消了关闭，不更新");
                    ShowAvailable(r, "已取消更新。安装程序已经下载好，下次点「更新」会直接安装。");
                }
            }
            catch (OperationCanceledException) when (_download?.IsCancellationRequested == true)
            {
                Trace("已取消下载");
                ShowAvailable(r, "已取消下载。");
            }
            catch (Exception ex)
            {
                var ue = ex as UpdateException ?? new UpdateException("更新失败：" + ex.Message, UpdateException.Kinds.Other, ex);
                Trace("更新失败：" + ue.Kind + " " + ue.Message + (ue.InnerException != null ? "（" + ue.InnerException.GetBaseException().Message + "）" : ""));
                ShowUpdateFailed(r, ue);
            }
            finally
            {
                _busy = false;
                _download = null;
            }
        }

        /// <summary>主窗口关闭后（MainWindow.Closed）调用：要更新的话现在启动安装程序，它等本进程退出后再覆盖文件。</summary>
        internal void OnClosed()
        {
            _closed = true;
            try { _download?.Cancel(); } catch { }
            var psi = _pendingInstall;
            if (psi == null) return;
            _pendingInstall = null;
            try
            {
                Updater.ClearSelfTestEnvironment();
                using (var p = Process.Start(psi)) Trace($"安装程序已启动（进程 {p?.Id}），本进程 {Process.GetCurrentProcess().Id} 即将退出");
            }
            catch (Exception ex)
            {
                Trace("安装程序启动失败：" + ex.Message);
                if (_trace == null)
                    MessageBox.Show("没能启动安装程序：" + ex.Message + "\n\n可以到 GitHub 下载页手动下载安装：\n" + Updater.ReleasesPage, "SW 工具箱", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ================= 界面状态 =================

        void Buttons(string primary, Action onPrimary, string link1 = null, Action onLink1 = null, string link2 = null, Action onLink2 = null)
        {
            _primary.Content = primary; _onPrimary = onPrimary; _primary.Visibility = primary != null ? Visibility.Visible : Visibility.Collapsed;
            _link1.Content = link1; _onLink1 = onLink1; _link1.Visibility = link1 != null ? Visibility.Visible : Visibility.Collapsed;
            _link2.Content = link2; _onLink2 = onLink2; _link2.Visibility = link2 != null ? Visibility.Visible : Visibility.Collapsed;
            _buttons.Visibility = primary != null || link1 != null || link2 != null ? Visibility.Visible : Visibility.Collapsed;
        }

        void Card(Brush bg, string glyph, Brush glyphColor, string title, string detail, Action onDismiss)
        {
            _revert?.Stop();
            _card.Background = bg;
            _icon.Text = glyph; _icon.Foreground = glyphColor;
            _title.Text = title; _title.ToolTip = title;
            _detail.Text = detail ?? ""; _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            _bar.Visibility = Visibility.Collapsed;
            _onDismiss = onDismiss; _dismiss.Visibility = onDismiss != null ? Visibility.Visible : Visibility.Collapsed;
            _line.Visibility = Visibility.Collapsed;
            if (_card.Visibility != Visibility.Visible) { Ui.SetShown(_card, false); Ui.Fade(_card, true, 180); }
        }

        internal void ShowIdle()
        {
            _revert?.Stop();
            Ui.SetShown(_card, false);
            _line.Visibility = Visibility.Visible;
            _version.Text = "v" + Updater.CurrentVersion;
            _version.Foreground = R("B.Text3");
            _check.Visibility = Visibility.Visible;
            _check.IsHitTestVisible = true;
            _check.Foreground = R("B.Accent");
            _check.Content = _latest != null ? $"有新版本 v{_latest.Version}" : "检查更新";
            _check.ToolTip = _latest != null ? "点开看看" : "到 GitHub 查有没有新版本";
        }

        internal void ShowChecking()
        {
            ShowIdle();
            _check.Content = "正在检查…";
            _check.Foreground = R("B.Text2");
            _check.IsHitTestVisible = false;
        }

        internal void ShowUpToDate()
        {
            ShowIdle();
            _check.Content = "已经是最新版本";
            _check.Foreground = R("B.Green");
            _check.IsHitTestVisible = false;
            _check.ToolTip = null;
            _revert = _revert ?? new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _revert.Tick -= RevertTick; _revert.Tick += RevertTick;
            _revert.Start();
        }

        void RevertTick(object s, EventArgs e) { _revert.Stop(); if (!_busy && _card.Visibility != Visibility.Visible) ShowIdle(); }

        internal void ShowCheckFailed(UpdateException ex)
        {
            Card(TintGray, "", Orange, ex.Network ? "连不上 GitHub" : "检查更新失败",
                ex.Network ? (ex.Message.Contains("限制") ? ex.Message + "。" : "可能是网络问题，或者 GitHub 被墙了。可以稍后重试，或用浏览器打开下载页手动下载。")
                           : ex.Message + "。可以重试，或打开下载页手动下载。",
                ShowIdle);
            Buttons(null, null, "重试", async () => { try { await CheckAsync(true); } catch (UpdateException) { } }, "打开下载页", () => Updater.OpenUrl(Updater.ReleasesPage));
        }

        internal void ShowAvailable(ReleaseInfo r, string note = null, bool canUpdate = true)
        {
            var size = r.AssetSize > 0 ? $" · 安装包 {Updater.Mb(r.AssetSize)} MB" : "";
            Card(TintBlue, "", Blue, $"有新版本 v{r.Version}", note ?? $"当前 v{Updater.CurrentVersion}{size}", () => ShowIdle());
            if (r.AssetUrl != null && canUpdate)
                Buttons("更新", () => _ = UpdateAsync(), "看更新内容", () => ShowNotes(r));
            else
                Buttons(null, null, "看更新内容", () => ShowNotes(r), "打开下载页", () => Updater.OpenUrl(r.PageUrl ?? Updater.ReleasesPage));
        }

        internal void ShowDownloading(ReleaseInfo r, long done, long total)
        {
            double p = total > 0 ? Math.Min(1, (double)done / total) : 0;
            var amount = total > 0 ? $"{Updater.Mb(done)} / {Updater.Mb(total)} MB · {p:P0}" : $"{Updater.Mb(done)} MB";
            if (_title.Text != $"正在下载 v{r.Version}" || _card.Visibility != Visibility.Visible)
            {
                Card(TintBlue, "", Blue, $"正在下载 v{r.Version}", amount, null);
                Buttons(null, null, "取消", () => { try { _download?.Cancel(); } catch { } });
            }
            _detail.Text = amount + "\n下载完会自动关闭软件、安装并重新打开";
            _bar.Visibility = Visibility.Visible;
            _bar.Value = p;
            _onProgress?.Invoke(p);
        }

        internal void ShowVerifying(ReleaseInfo r)
        {
            Card(TintBlue, "", Blue, "正在校验安装程序…", "核对大小和 SHA-256，和 GitHub 上公布的一致才会安装", null);
            _bar.Visibility = Visibility.Visible; _bar.Value = 1;
            Buttons(null, null);
        }

        internal void ShowInstalling(ReleaseInfo r)
        {
            Card(TintBlue, "", Blue, $"正在更新到 v{r.Version}", "软件马上关闭，装好后会自动重新打开（大约十几秒）", null);
            _bar.Visibility = Visibility.Visible; _bar.Value = 1;
            Buttons(null, null);
        }

        internal void ShowUpdateFailed(ReleaseInfo r, UpdateException ex)
        {
            string title = ex.Kind == UpdateException.Kinds.Integrity ? "安装程序校验没通过" : ex.Network ? "下载失败" : "更新失败";
            string detail = ex.Kind == UpdateException.Kinds.Integrity ? ex.Message + "。文件已删除，没有安装。" : ex.Message + "。";
            Card(TintGray, "", ex.Kind == UpdateException.Kinds.Integrity ? Red : Orange, title, detail, () => { if (_latest != null) ShowAvailable(_latest); else ShowIdle(); });
            Buttons(null, null, "重试", () => _ = UpdateAsync(), "打开下载页", () => Updater.OpenUrl(r?.PageUrl ?? Updater.ReleasesPage));
        }

        // ================= 更新内容浮层 =================

        internal void ShowNotes(ReleaseInfo r)
        {
            if (_notes == null) BuildNotes();
            _notesRelease = r;
            _notesTitle.Text = $"v{r.Version} 更新内容";
            bool newer = Updater.IsNewer(r.Version, Updater.CurrentVersion);
            _notesCaption.Text = (r.Published != null ? "发布于 " + r.Published + " · " : "") + "当前 v" + Updater.CurrentVersion + (newer && r.AssetSize > 0 ? $" · 安装包 {Updater.Mb(r.AssetSize)} MB" : "");
            var md = Updater.NotesForApp(r.Notes);
            _notesReader.Document = Markdown.Render(md.Length > 0 ? md : "这个版本没有写更新说明。");
            _notesUpdate.Content = $"更新到 v{r.Version}";
            _notesUpdate.Visibility = newer && r.AssetUrl != null && !_busy ? Visibility.Visible : Visibility.Collapsed;
            Ui.Fade(_notes, true, 160);
        }

        void HideNotes() { if (_notes != null) Ui.Fade(_notes, false, 140); }

        void BuildNotes()
        {
            _notes = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), Visibility = Visibility.Collapsed };
            Grid.SetColumnSpan(_notes, 2);
            Panel.SetZIndex(_notes, 90);
            _notes.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == _notes) HideNotes(); };

            var card = new Border { Style = (Style)Application.Current.FindResource("Card"), Background = Brushes.White, MaxWidth = 760, MaxHeight = 900, Margin = new Thickness(40, 36, 40, 36) };
            var dock = new DockPanel();

            var header = new Border { BorderBrush = R("B.Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(24, 16, 12, 14) };
            DockPanel.SetDock(header, Dock.Top);
            var hd = new DockPanel();
            var x = new Button { Style = (Style)Application.Current.FindResource("Btn.Icon"), Content = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 11, ToolTip = "关闭（Esc）", VerticalAlignment = VerticalAlignment.Top };
            x.Click += (s, e) => HideNotes();
            DockPanel.SetDock(x, Dock.Right);
            hd.Children.Add(x);
            var ht = new StackPanel();
            _notesTitle = new TextBlock { Style = (Style)Application.Current.FindResource("T.Title"), TextTrimming = TextTrimming.CharacterEllipsis };
            _notesCaption = new TextBlock { FontSize = 12, Foreground = R("B.Text2"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            ht.Children.Add(_notesTitle);
            ht.Children.Add(_notesCaption);
            hd.Children.Add(ht);
            header.Child = hd;
            dock.Children.Add(header);

            var footer = new Border { BorderBrush = R("B.Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12, 16, 12) };
            DockPanel.SetDock(footer, Dock.Bottom);
            var fd = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            var close = new Button { Style = (Style)Application.Current.FindResource("Btn.Secondary"), Content = "关闭" };
            close.Click += (s, e) => HideNotes();
            _notesUpdate = new Button { Style = (Style)Application.Current.FindResource("Btn.Primary"), Height = 32, Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(8, 0, 0, 0) };
            _notesUpdate.Click += (s, e) => { HideNotes(); if (_notesRelease != null && _latest != null) _ = UpdateAsync(); };
            right.Children.Add(close);
            right.Children.Add(_notesUpdate);
            fd.Children.Add(right);
            var gh = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "在 GitHub 上查看", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            gh.Click += (s, e) => Updater.OpenUrl(_notesRelease?.PageUrl ?? Updater.ReleasesPage);
            fd.Children.Add(gh);
            footer.Child = fd;
            dock.Children.Add(footer);

            _notesReader = new FlowDocumentScrollViewer { IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            dock.Children.Add(_notesReader);
            card.Child = dock;
            _notes.Children.Add(card);
            _root.Children.Add(_notes);
        }

        // ================= 开发测试 =================

        void Trace(string s) => _trace?.Invoke(s);

        static string Env(string n) { var v = Environment.GetEnvironmentVariable(n); return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }

        /// <summary>
        /// AppMain 调用：SWKIT_TEST_DATA（隔离数据、窗口在屏幕外）、SWKIT_UPDATE_SNAPSHOT（渲染各状态）。返回 null 表示照常运行。
        /// </summary>
        internal static int? DevHooks(Application app, MainWindow win, Action<Window> offScreen)
        {
            var data = Updater.TestData;
            var snap = Env("SWKIT_UPDATE_SNAPSHOT");
            if (data == null && snap == null) return null;
            if (data != null) SwKit.Core.Workspace.WorkspaceSettings.OverrideDir = Path.Combine(data, "ws-data");
            offScreen(win);
            win.Width = 1100; win.Height = 760;
            var size = Env("SWKIT_SNAP_SIZE");   // 宽x高（检查小窗口下的布局）
            if (size != null && size.Contains("x")) { var wh = size.Split('x'); win.MinWidth = 0; win.MinHeight = 0; win.Width = double.Parse(wh[0]); win.Height = double.Parse(wh[1]); }
            if (snap == null) return null;
            MainWindow.DevMode = true;
            if (data == null) SwKit.Core.Workspace.WorkspaceSettings.OverrideDir = Path.Combine(Path.GetFullPath(snap), "ws-data");
            return Snapshot(app, win, Path.GetFullPath(snap));
        }

        /// <summary>把侧栏更新区的各个状态（和更新内容浮层）渲染成 PNG，不联网。</summary>
        static int Snapshot(Application app, MainWindow win, string dir)
        {
            Directory.CreateDirectory(dir);
            int code = 0;
            var scale = double.TryParse(Env("SWKIT_SNAP_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 1.5;
            win.Loaded += async (s, e) =>
            {
                try
                {
                    var u = win.Update;
                    var r = new ReleaseInfo
                    {
                        Tag = "v0.7", Version = "0.7", Title = "SW 工具箱 v0.7", Published = "2026-12-01", PageUrl = Updater.ReleasesPage,
                        AssetName = "SWKit-v0.7-Setup.exe", AssetUrl = "https://github.com/" + Updater.Repo + "/releases/download/v0.7/SWKit-v0.7-Setup.exe", AssetSize = 35654656,
                        Notes = SampleNotes,
                    };
                    async Task Shot(string name) { await Task.Delay(450); win.UpdateLayout(); SaveShot(win, Path.Combine(dir, name + ".png"), scale); }
                    await Task.Delay(500);
                    u.ShowIdle(); await Shot("u1-平时");
                    u.ShowChecking(); await Shot("u2-检查中");
                    u.ShowUpToDate(); await Shot("u3-已是最新");
                    u.ShowCheckFailed(new UpdateException("连不上 GitHub（网络或被墙）", UpdateException.Kinds.Network)); await Shot("u4-连不上");
                    u._latest = r; u.ShowAvailable(r); await Shot("u5-有新版本");
                    u.ShowDownloading(r, (long)(r.AssetSize * 0.42), r.AssetSize); await Shot("u6-下载中");
                    u.ShowVerifying(r); await Shot("u7-校验");
                    u.ShowInstalling(r); await Shot("u8-安装");
                    u.ShowUpdateFailed(r, new UpdateException("安装程序的 SHA-256 和 GitHub 上公布的不一致（可能下载损坏或被篡改）", UpdateException.Kinds.Integrity)); await Shot("u9-校验失败");
                    u.ShowIdle(); await Shot("u10-收起后");
                    u.ShowAvailable(r); u.ShowNotes(r); await Task.Delay(300); await Shot("u11-更新内容");
                }
                catch (Exception ex) { File.AppendAllText(Path.Combine(dir, "error.txt"), ex + Environment.NewLine, Encoding.UTF8); code = 1; }
                finally { win.Close(); }
            };
            app.Run(win);
            return code;
        }

        static void SaveShot(Window win, string file, double scale)
        {
            var root = (FrameworkElement)win.Content;
            var rtb = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect }, null, rect);
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        /// <summary>SWKIT_UPDATE_SELFTEST=&lt;结果文件&gt;：启动后自动 检查 → 看更新内容 → 下载 → 校验 → 关闭并启动安装程序，每一步写进结果文件、截图。</summary>
        async Task SelfTestAsync(string log)
        {
            log = Path.GetFullPath(log);
            var dir = Path.GetDirectoryName(log);
            Directory.CreateDirectory(dir);
            _trace = s => { try { File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}", Encoding.UTF8); } catch { } };
            void Shot(string name) { try { _win.UpdateLayout(); SaveShot(_win, Path.Combine(dir, name + ".png"), 1.5); } catch (Exception ex) { Trace("截图失败：" + ex.Message); } }
            try
            {
                Trace($"自测开始：当前版本 v{Updater.CurrentVersion}（程序里写的 v{AppInfo.Version}），程序 {Updater.AppDir}，进程 {Process.GetCurrentProcess().Id}");
                Trace("安装方式：" + (Updater.IsInstalledCopy(Updater.AppDir) ? "安装版（/update，更新登记的版本号）" : "绿色版（/update /noreg /noshortcut）"));
                await Task.Delay(800);
                Shot("st-0-启动");
                ReleaseInfo r;
                try { r = await CheckAsync(true); }
                catch (UpdateException) { await Task.Delay(400); Shot("st-1-检查失败"); Trace("结束"); _win.Close(); return; }
                await Task.Delay(500);
                if (!Updater.IsNewer(r.Version, Updater.CurrentVersion)) { Shot("st-1-已是最新"); Trace("结束"); _win.Close(); return; }
                Shot("st-1-有新版本");
                ShowNotes(r); await Task.Delay(600); Shot("st-2-更新内容"); Ui.SetShown(_notes, false);
                bool shot = false;
                _onProgress = p => { if (!shot && p >= 0.3 && p < 0.95) { shot = true; Shot("st-3-下载中"); } };
                await UpdateAsync();
                if (!_closed) { await Task.Delay(400); Shot("st-4-结果"); Trace($"没有安装：{_title.Text} / {_detail.Text}"); Trace("结束"); _win.Close(); }
            }
            catch (Exception ex) { Trace("自测出错：" + ex); _win.Close(); }
        }

        const string SampleNotes =
"## 下载\n\n- **SW工具箱-v0.7-安装程序.exe（推荐）**：双击安装。\n\n" +
"## 更新内容\n\n" +
"**在线更新**\n" +
"- 新增：左下角「检查更新」，有新版本时一键下载、校验、安装并自动重启，不用再到 GitHub 上下载。\n" +
"- 每天启动时自动查一次，连不上 GitHub 时不打扰。\n\n" +
"**质量估算**\n" +
"- 新增质量估算页面：按材料密度估算零件和装配体的质量、重心。\n\n" +
"**修正**\n" +
"- 修正：应力分析在小窗口下右侧面板被遮住的问题。\n" +
"- 修正：`swkit-cli mcp` 在路径带空格时启动失败。\n";
    }
}
