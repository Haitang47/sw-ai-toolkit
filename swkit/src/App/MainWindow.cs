using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using SwKit.Core;

namespace SwKit.App
{
    sealed class MainWindow : Window
    {
        readonly FrameworkElement _root;
        readonly Grid _host;
        readonly SwWorker _worker = new SwWorker();
        readonly Dictionary<string, FrameworkElement> _pages = new Dictionary<string, FrameworkElement>();
        internal ExportPage Export { get; private set; }
        internal UrdfPage Urdf { get; private set; }
        internal PartsPage Parts { get; private set; }
        internal FeaPage Fea { get; private set; }
        internal PromptKitPage Prompts { get; private set; }
        internal WorkspacePage Workspace { get; private set; }
        internal DfmPage Dfm { get; private set; }
        internal MassPage Mass { get; private set; }
        internal CfdPage Cfd { get; private set; }
        internal ApiPage Api { get; private set; }
        internal ApiServer ApiServer { get; private set; }
        internal static bool DevMode;   // 开发用的截图、自测模式：不启动本地接口

        readonly TextBlock _swTitle, _swDetail;
        readonly Ellipse _swDot, _swHalo;
        readonly Button _workerClose;
        internal readonly UpdatePanel Update;   // 侧栏底部的版本号和在线更新
        string _installed;      // 例如 "SolidWorks 2025"；没装时为 null
        string _workerStatus;    // 后台 SolidWorks 状态；没有时为 null

        public MainWindow()
        {
            Title = "SW 工具箱";
            // 窗口大小按屏幕可用区域来定（4K 200%、笔记本 150%、1080p 100% 都合适，不会超出屏幕）
            var wa = SystemParameters.WorkArea;
            Width = Math.Round(Math.Min(1320, wa.Width * 0.88));
            Height = Math.Round(Math.Min(860, wa.Height * 0.9));
            MinWidth = Math.Round(Math.Min(960, wa.Width * 0.95));
            MinHeight = Math.Round(Math.Min(620, wa.Height * 0.95));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // 窗口 / 任务栏图标：嵌在 exe 里的 app.ico（多尺寸，取最大的一帧，系统按缩放比例缩放）
            try
            {
                using (var s = typeof(MainWindow).Assembly.GetManifestResourceStream("SwKit.app.ico"))
                    if (s != null) Icon = new System.Windows.Media.Imaging.IconBitmapDecoder(s, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames.OrderByDescending(f => f.PixelWidth).First();
            }
            catch { }
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
            FontSize = 13;
            Background = Brushes.White;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 44,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0, 0, 0, 1),   // 保留系统阴影
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });

            _root = (FrameworkElement)Ui.Load("MainWindow.xaml");
            // 侧栏左上角的品牌标志：和程序图标同一张图
            try
            {
                using (var ls = typeof(MainWindow).Assembly.GetManifestResourceStream("SwKit.logo.png"))
                    if (ls != null)
                    {
                        var bi = new System.Windows.Media.Imaging.BitmapImage();
                        bi.BeginInit(); bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.StreamSource = ls; bi.EndInit(); bi.Freeze();
                        _root.Find<Image>("BrandLogo").Source = bi;
                    }
            }
            catch { }
            Content = _root;
            _host = _root.Find<Grid>("PageHost");
            _swTitle = _root.Find<TextBlock>("SwTitle");
            _swDetail = _root.Find<TextBlock>("SwDetail");
            _swDot = _root.Find<Ellipse>("SwDot");
            _swHalo = _root.Find<Ellipse>("SwDotHalo");
            _workerClose = _root.Find<Button>("WorkerCloseBtn");
            Update = new UpdatePanel(this, _root);

            foreach (var n in new[] { "MinBtn", "MaxBtn", "CloseBtn" })
                WindowChrome.SetIsHitTestVisibleInChrome(_root.Find<Button>(n), true);
            _root.Find<Button>("MinBtn").Click += (s, e) => WindowState = WindowState.Minimized;
            _root.Find<Button>("MaxBtn").Click += (s, e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            _root.Find<Button>("CloseBtn").Click += (s, e) => Close();
            StateChanged += (s, e) =>
            {
                // 最大化时 WindowChrome 会超出屏幕边缘，按实际像素补边距
                Dispatcher.BeginInvoke(new Action(FixMaximizedMargin), System.Windows.Threading.DispatcherPriority.Render);
                _root.Find<Button>("MaxBtn").Content = WindowState == WindowState.Maximized ? "" : "";
            };
            SizeChanged += (s, e) => { if (WindowState == WindowState.Maximized) FixMaximizedMargin(); };
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(FixMaximizedMargin), System.Windows.Threading.DispatcherPriority.Render);
            SourceInitialized += (s, e) => RoundCorners();

            // 界面缩放：Ctrl + 滚轮、Ctrl + = / − / 0（记住上次的设置）
            _zoom = LoadZoom();
            ApplyZoom(false);
            PreviewMouseWheel += (s, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Control) return;
                SetZoom(_zoom + (e.Delta > 0 ? 0.05 : -0.05));
                e.Handled = true;
            };
            PreviewKeyDown += (s, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Control) return;
                if (e.Key == Key.OemPlus || e.Key == Key.Add) { SetZoom(_zoom + 0.1); e.Handled = true; }
                else if (e.Key == Key.OemMinus || e.Key == Key.Subtract) { SetZoom(_zoom - 0.1); e.Handled = true; }
                else if (e.Key == Key.D0 || e.Key == Key.NumPad0) { SetZoom(1); e.Handled = true; }
            };

            Nav("NavExport", "export");
            Nav("NavUrdf", "urdf");
            Nav("NavParts", "parts");
            Nav("NavFea", "fea");
            Nav("NavDfm", "dfm");
            Nav("NavMass", "mass");
            Nav("NavCfd", "cfd");
            Nav("NavWorkspace", "workspace");
            Nav("NavPrompts", "prompts");
            Nav("NavApi", "api");

            _workerClose.Click += (s, e) => _worker.CloseSession();
            _worker.StatusChanged += text => Dispatcher.BeginInvoke(new Action(() => { _workerStatus = text; RefreshSwStatus(); }));

            Loaded += async (s, e) =>
            {
                _root.Find<RadioButton>("NavExport").IsChecked = true;
                _installed = await Task.Run(() => DetectSolidWorks());
                RefreshSwStatus();
                WorkspacePage.StartWatcher();   // 工作区自动存档：软件开着就生效，不需要打开工作区页面
                if (!DevMode && SwKit.App.ApiServer.EnabledSetting) StartApi();
                Update.Start();   // 每天最多自动检查一次更新（开发模式不联网）
            };
            Closing += (s, e) =>
            {
                if (Export?.IsRunning == true || Urdf?.IsBusy == true || Parts?.IsBusy == true || Fea?.IsBusy == true || Workspace?.IsBusy == true || Dfm?.IsBusy == true || Mass?.IsBusy == true || Cfd?.IsBusy == true)
                {
                    var r = MessageBox.Show(this, "还有任务在进行中。现在关闭会中断导出，确定要关闭吗？", "SW 工具箱", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
                }
                Export?.SaveOptions();
            };
            Closed += (s, e) => { Update.OnClosed(); ApiServer?.Stop(); WorkspacePage.StopWatcher(); _worker.Dispose(); };
        }

        readonly Dictionary<string, RadioButton> _navButtons = new Dictionary<string, RadioButton>();

        void Nav(string name, string key)
        {
            var rb = _root.Find<RadioButton>(name);
            _navButtons[key] = rb;
            rb.Checked += (s, e) => Navigate(key);
        }

        internal void Navigate(string key)
        {
            if (!_pages.TryGetValue(key, out var page))
            {
                page = CreatePage(key);
                _pages[key] = page;
            }
            // 从代码 / AI 接口切换页面时，左侧导航的高亮也要跟着变
            if (_navButtons.TryGetValue(key, out var nav) && nav.IsChecked != true) nav.IsChecked = true;
            if (_host.Children.Count == 1 && _host.Children[0] == page) return;
            _host.Children.Clear();
            _host.Children.Add(page);
            Ui.Enter(page);
        }

        FrameworkElement CreatePage(string key)
        {
            switch (key)
            {
                case "export":
                    Export = new ExportPage(this, _worker);
                    return Export.Root;
                case "prompts":
                    Prompts = new PromptKitPage(this);
                    return Prompts.Root;
                case "urdf":
                    Urdf = new UrdfPage(this, _worker);
                    return Urdf.Root;
                case "parts":
                    Parts = new PartsPage(this, _worker);
                    return Parts.Root;
                case "api":
                    Api = new ApiPage(this);
                    return Api.Root;
                case "dfm":
                    Dfm = new DfmPage(this, _worker);
                    return Dfm.Root;
                case "mass":
                    Mass = new MassPage(this, _worker);
                    return Mass.Root;
                case "cfd":
                    Cfd = new CfdPage(this, _worker);
                    return Cfd.Root;
                case "workspace":
                    Workspace = new WorkspacePage(this, _worker);
                    return Workspace.Root;
                case "fea":
                    Fea = new FeaPage(this, _worker);
                    return Fea.Root;
                default:
                    return new Grid();
            }
        }

        // ================= 本地接口（给 AI 用） =================

        internal void StartApi()
        {
            if (ApiServer == null) ApiServer = new ApiServer(this);
            ApiServer.Start();
        }

        internal void StopApi() => ApiServer?.Stop();

        internal RadioButton FindNav(string name) => _root.FindName(name) as RadioButton;

        internal object PageObject(string key)
        {
            switch (key)
            {
                case "export": return Export;
                case "urdf": return Urdf;
                case "parts": return Parts;
                case "fea": return Fea;
                case "dfm": return Dfm;
                case "mass": return Mass;
                case "cfd": return Cfd;
                case "workspace": return Workspace;
                case "prompts": return Prompts;
                case "api": return Api;
                default: return null;
            }
        }

        internal Dictionary<string, object> ApiStatus()
        {
            var current = _pages.FirstOrDefault(kv => _host.Children.Count == 1 && _host.Children[0] == kv.Value).Key;
            return new Dictionary<string, object>
            {
                ["app"] = "SW 工具箱", ["version"] = AppInfo.Version, ["page"] = current,
                ["solidworks"] = _installed ?? "没有找到 SolidWorks", ["background_solidworks"] = _workerStatus ?? "没有运行（需要时自动启动，独立于你自己的 SolidWorks）",
                ["busy"] = Export?.IsRunning == true || Urdf?.IsBusy == true || Parts?.IsBusy == true || Fea?.IsBusy == true || Workspace?.IsBusy == true || Dfm?.IsBusy == true,
                ["zoom"] = _zoom,
            };
        }

        // ================= SolidWorks 状态 =================

        static string DetectSolidWorks()
        {
            var exe = SwSession.FindExecutable();
            if (exe == null) return null;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                return v.FileMajorPart > 0 ? $"SolidWorks {v.FileMajorPart + 1992}" : "SolidWorks";
            }
            catch { return "SolidWorks"; }
        }

        static readonly Brush DotGray = Ui.Brush("#A1A1A6");
        static readonly Brush DotGreen = Ui.Brush("#34C759");
        static readonly Brush DotBlue = Ui.Brush("#0A84FF");
        static readonly Brush DotRed = Ui.Brush("#FF3B30");

        void RefreshSwStatus()
        {
            if (_installed == null)
            {
                _swDot.Fill = DotRed;
                _swTitle.Text = "没有找到 SolidWorks";
                _swDetail.Text = "需要安装 SolidWorks 2023 及以上版本";
                _workerClose.Visibility = Visibility.Collapsed;
                Pulse(false);
                return;
            }
            _swTitle.Text = _installed;
            if (_workerStatus == null)
            {
                _swDot.Fill = DotGreen;
                _swDetail.Text = "已就绪，导出时自动在后台启动";
                _workerClose.Visibility = Visibility.Collapsed;
                Pulse(false);
            }
            else
            {
                var working = _workerStatus.Contains("正在工作");
                _swDot.Fill = working ? DotBlue : DotGreen;
                _swDetail.Text = working ? "后台实例正在导出" : "后台实例待命中，空闲 10 分钟后自动关闭";
                _workerClose.Visibility = working ? Visibility.Collapsed : Visibility.Visible;
                Pulse(working);
            }
        }

        /// <summary>后台工作时，状态点外圈有一个柔和的呼吸光晕。</summary>
        void Pulse(bool on)
        {
            var st = (ScaleTransform)_swHalo.RenderTransform;
            if (!on)
            {
                _swHalo.BeginAnimation(OpacityProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _swHalo.Opacity = 0;
                return;
            }
            _swHalo.Fill = DotBlue;
            var dur = TimeSpan.FromSeconds(1.6);
            var grow = new DoubleAnimation(1, 2.6, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Ui.Ease };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _swHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0, dur) { RepeatBehavior = RepeatBehavior.Forever });
        }

        // ================= 界面缩放 =================

        double _zoom = 1;
        Border _toast;
        System.Windows.Threading.DispatcherTimer _toastTimer;
        static string ZoomFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwKit", "ui-scale.txt");

        static double LoadZoom()
        {
            try
            {
                if (File.Exists(ZoomFile) && double.TryParse(File.ReadAllText(ZoomFile).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
                    return Math.Max(0.7, Math.Min(2, z));
            }
            catch { }
            return 1;
        }

        internal void SetZoom(double z)
        {
            z = Math.Round(Math.Max(0.7, Math.Min(2, z)) * 20) / 20;
            if (Math.Abs(z - _zoom) < 1e-6) { ShowToast($"界面缩放 {z:P0}"); return; }
            _zoom = z;
            ApplyZoom(true);
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ZoomFile)); File.WriteAllText(ZoomFile, z.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
        }

        void ApplyZoom(bool toast)
        {
            _root.LayoutTransform = Math.Abs(_zoom - 1) < 1e-6 ? Transform.Identity : new ScaleTransform(_zoom, _zoom);
            if (toast) ShowToast($"界面缩放 {_zoom:P0}　（Ctrl + 0 还原）");
        }

        void ShowToast(string text)
        {
            if (_toast == null)
            {
                _toast = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1D, 0x1D, 0x1F)), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 9, 16, 9),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40),
                    IsHitTestVisible = false, Visibility = Visibility.Collapsed,
                    Child = new TextBlock { Foreground = Brushes.White, FontSize = 12.5 },
                };
                Grid.SetColumnSpan(_toast, 2);
                Panel.SetZIndex(_toast, 100);
                ((Grid)_root).Children.Add(_toast);
                _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
                _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); Ui.Fade(_toast, false, 250); };
            }
            ((TextBlock)_toast.Child).Text = text;
            Ui.Fade(_toast, true, 120);
            _toastTimer.Stop(); _toastTimer.Start();
        }

        // ================= 最大化时的边距 =================

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

        /// <summary>最大化时 WindowChrome 的窗口会超出屏幕可用区域一圈（宽度随缩放比例变化），按实际像素算出来补上边距。</summary>
        void FixMaximizedMargin()
        {
            if (WindowState != WindowState.Maximized) { _root.Margin = new Thickness(0); return; }
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var wr)) return;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return;
                var dpi = VisualTreeHelper.GetDpi(this);
                _root.Margin = new Thickness(
                    Math.Max(0, mi.rcWork.Left - wr.Left) / dpi.DpiScaleX, Math.Max(0, mi.rcWork.Top - wr.Top) / dpi.DpiScaleY,
                    Math.Max(0, wr.Right - mi.rcWork.Right) / dpi.DpiScaleX, Math.Max(0, wr.Bottom - mi.rcWork.Bottom) / dpi.DpiScaleY);
            }
            catch { _root.Margin = new Thickness(7); }
        }

        // ================= Windows 11 圆角 =================

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void RoundCorners()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int round = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE（Windows 10 上无效果）
            }
            catch { }
        }
    }
}
