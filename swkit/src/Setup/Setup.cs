using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using Microsoft.Win32;

// 单文件安装程序：把发布压缩包（嵌在 exe 里的 payload.zip）解压到 %LOCALAPPDATA%\Programs\SW工具箱（不需要管理员权限），
// 建开始菜单 / 桌面快捷方式，在「设置 → 应用」里登记，可以卸载。用户设置在 %APPDATA%\SwKit，升级和卸载都不动它。
//
// 命令行：/S 静默安装；/D=<文件夹> 安装位置；/noshortcut 不建快捷方式；/noreg 不登记到「应用」（开发测试用）；
//        /uninstall 卸载；/preview <文件夹> 在屏幕外把三个界面状态渲染成 PNG（开发时检查界面用）
[assembly: AssemblyTitle("SW 工具箱 安装程序")]
[assembly: AssemblyProduct("SW 工具箱")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace SwKit.Setup
{
    static class Program
    {
        public const string Version = BuildInfo.Version;   // release.ps1 生成 BuildInfo.Version
        public const string AppName = "SW 工具箱";
        const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SwKit";

        public static string DefaultDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SW工具箱");

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
            bool noShortcut = args.Any(a => a.Equals("/noshortcut", StringComparison.OrdinalIgnoreCase));
            bool noReg = args.Any(a => a.Equals("/noreg", StringComparison.OrdinalIgnoreCase));
            string dir = args.Where(a => a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)).Select(a => a.Substring(3).Trim('"')).FirstOrDefault() ?? DefaultDir;

            if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
                return Uninstall(System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), silent, noReg, noShortcut);

            if (silent)
            {
                try { Install(dir, !noShortcut, !noShortcut, !noReg, null); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            int pv = Array.FindIndex(args, a => a.Equals("/preview", StringComparison.OrdinalIgnoreCase));
            if (pv >= 0 && pv + 1 < args.Length) return SetupWindow.Preview(app, dir, args[pv + 1]);
            return app.Run(new SetupWindow(dir));
        }

        /// <summary>解压、建快捷方式、登记。progress：0–1。</summary>
        public static void Install(string dir, bool startMenu, bool desktop, bool register, Action<double, string> progress)
        {
            var running = Process.GetProcessesByName("SwKit").Where(p => { try { return p.MainModule.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase); } catch { return false; } }).ToList();
            if (running.Count > 0) throw new InvalidOperationException("SW 工具箱正在运行，请先关闭它再安装。");
            Directory.CreateDirectory(dir);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(s ?? throw new InvalidOperationException("安装程序里缺少安装包"), ZipArchiveMode.Read))
            {
                var entries = zip.Entries.Where(e => !e.FullName.EndsWith("/")).ToList();
                long total = entries.Sum(e => e.Length), done = 0;
                foreach (var e in entries)
                {
                    // 压缩包里第一层是「SW工具箱/」，去掉它
                    var rel = e.FullName.Replace('/', '\\');
                    int i = rel.IndexOf('\\');
                    if (i >= 0) rel = rel.Substring(i + 1);
                    if (rel.Length == 0 || rel.EndsWith("\\")) continue;
                    var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, rel));
                    if (!target.StartsWith(System.IO.Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;   // 防止路径穿越
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                    done += e.Length;
                    progress?.Invoke(total > 0 ? (double)done / total : 1, rel);
                }
            }
            // 卸载程序：把自己复制一份进去
            var uninst = System.IO.Path.Combine(dir, "卸载.exe");
            try { File.Copy(Assembly.GetExecutingAssembly().Location, uninst, true); } catch { }
            var exe = System.IO.Path.Combine(dir, "SwKit.exe");
            if (startMenu)
            {
                var sm = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
                Directory.CreateDirectory(sm);
                Shortcut(System.IO.Path.Combine(sm, AppName + ".lnk"), exe, dir, "AI × SolidWorks 工具集");
                Shortcut(System.IO.Path.Combine(sm, "使用说明.lnk"), System.IO.Path.Combine(dir, "使用说明.md"), dir, "使用说明");
            }
            if (desktop) Shortcut(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"), exe, dir, "AI × SolidWorks 工具集");
            if (register)
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", "SW 工具箱");
                    k.SetValue("DisplayIcon", exe);
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("UninstallString", $"\"{uninst}\" /uninstall");
                    k.SetValue("QuietUninstallString", $"\"{uninst}\" /uninstall /S");
                    k.SetValue("URLInfoAbout", "https://github.com/Haitang47/sw-ai-toolkit");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    long kb = new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
                    k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
                }
            }
        }

        static void Shortcut(string lnk, string target, string workDir, string desc)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(t);
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = target;
                sc.WorkingDirectory = workDir;
                sc.Description = desc;
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) sc.IconLocation = target + ",0";
                sc.Save();
            }
            catch { }
        }

        static int Uninstall(string dir, bool silent, bool noReg, bool noShortcut)
        {
            if (!silent && MessageBox.Show($"要卸载 {AppName} 吗？\n\n会删除 {dir}。\n你的设置和工作区历史（%APPDATA%\\SwKit、%LOCALAPPDATA%\\SwKit）会保留。", AppName, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return 1;
            if (Process.GetProcessesByName("SwKit").Any())
            {
                if (!silent) MessageBox.Show("SW 工具箱正在运行，请先关闭它。", AppName);
                return 1;
            }
            if (!noShortcut)
            {
                try { var lnk = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); if (File.Exists(lnk)) File.Delete(lnk); } catch { }
                try { var sm = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName); if (Directory.Exists(sm)) Directory.Delete(sm, true); } catch { }
            }
            if (!noReg) try { Registry.CurrentUser.DeleteSubKeyTree(RegKey, false); } catch { }
            // 卸载程序自己在这个文件夹里，退出后再删
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (!silent) MessageBox.Show("已卸载。", AppName);
            return 0;
        }
    }

    /// <summary>安装界面：和软件同一套风格（橙色渐变头部 + 白色主体、圆角控件、细进度条、完成页可直接启动）。</summary>
    sealed class SetupWindow : Window
    {
        static readonly Color Orange = Color.FromRgb(0xF2, 0x6A, 0x0A), OrangeLight = Color.FromRgb(0xFF, 0x9F, 0x38);
        static readonly Brush Text1 = B("#1D1D1F"), Text2 = B("#6E6E73"), Text3 = B("#A1A1A6"), Line = B("#E3E3E8"), Field = B("#FAFAFC"), Chip = B("#F4F4F6");
        static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
        static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        static SolidColorBrush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        readonly Grid _body;
        readonly TextBox _dir;
        readonly TextBlock _status;
        bool _desktop = true, _done, _busy;
        Border _barFill; Grid _barTrack; TextBlock _barText;
        FrameworkElement _primary, _secondary;

        public SetupWindow(string dir)
        {
            Title = $"安装 {Program.AppName}";
            Width = 640; Height = 452;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Ui; FontSize = 13;
            Background = Brushes.White;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0, 0, 0, 1), UseAeroCaptionButtons = false });
            SourceInitialized += (s, e) => { try { int v = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref v, 4); } catch { } };
            try { using (var st = Res("app.ico")) Icon = BitmapFrame.Create(st, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); } catch { }

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(168) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            // ---------- 头部：橙色渐变 + 大图标 + 标题 ----------
            var header = new Grid { Background = new LinearGradientBrush(OrangeLight, Orange, new Point(0, 0), new Point(1, 1)), ClipToBounds = true };
            header.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
            // 右侧淡淡的几何装饰（大号半透明立方体轮廓）
            var deco = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 120,0 L 240,40 L 240,180 L 120,220 L 0,180 L 0,40 Z M 0,40 L 120,80 L 240,40 M 120,80 L 120,220"),
                Stroke = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), StrokeThickness = 2, Width = 242, Height = 222, Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -18, -46, 0), IsHitTestVisible = false,
            };
            header.Children.Add(deco);
            var logo = new Image { Width = 104, Height = 104, Margin = new Thickness(34, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            try { using (var st = Res("logo.png")) { var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = st; bi.EndInit(); logo.Source = bi; } } catch { }
            logo.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.25, Color = Color.FromRgb(0x6A, 0x22, 0x00) };
            header.Children.Add(logo);
            var titles = new StackPanel { Margin = new Thickness(160, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var tRow = new StackPanel { Orientation = Orientation.Horizontal };
            tRow.Children.Add(new TextBlock { Text = Program.AppName, FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI Variable Display, Microsoft YaHei UI") });
            tRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3),
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "v" + Program.Version, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold },
            });
            titles.Children.Add(tRow);
            titles.Children.Add(new TextBlock { Text = "AI × SolidWorks 工具集", Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), FontSize = 15, Margin = new Thickness(2, 6, 0, 0) });
            header.Children.Add(titles);
            var close = CaptionButton("");
            close.MouseLeftButtonUp += (s, e) => { if (!_busy) Close(); };
            header.Children.Add(close);
            root.Children.Add(header);

            // ---------- 主体 ----------
            _body = new Grid { Margin = new Thickness(36, 22, 36, 0) };
            Grid.SetRow(_body, 1);
            root.Children.Add(_body);
            _dir = new TextBox { Text = dir, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Foreground = Text1, Padding = new Thickness(10, 0, 4, 0) };
            ShowForm();

            // ---------- 底栏 ----------
            var footer = new DockPanel { Margin = new Thickness(36, 0, 36, 26), LastChildFill = true };
            Grid.SetRow(footer, 2);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(buttons, Dock.Right);
            _secondary = TextButton("取消", () => { if (!_busy) Close(); });
            _primary = PrimaryButton("安装", async () => await OnPrimary());
            buttons.Children.Add(_secondary);
            buttons.Children.Add(_primary);
            footer.Children.Add(buttons);
            _status = new TextBlock { Text = "不需要管理员权限 · 约 120 MB · 你的设置保存在别处，升级不会丢", FontSize = 12, Foreground = Text3, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            footer.Children.Add(_status);
            root.Children.Add(footer);
            KeyDown += async (s, e) => { if (e.Key == Key.Enter && !_busy) await OnPrimary(); else if (e.Key == Key.Escape && !_busy) Close(); };
        }

        static Stream Res(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // ---------- 三个状态 ----------

        void ShowForm()
        {
            _body.Children.Clear();
            var sp = new StackPanel();
            // 功能标签
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var c in new[] { "一键导出", "URDF", "标准件库", "应力分析", "拓扑优化", "可制造性", "实验报告", "AI 接口" })
                chips.Children.Add(new Border { Background = Chip, CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6), Child = new TextBlock { Text = c, FontSize = 12, Foreground = Text2 } });
            sp.Children.Add(chips);
            sp.Children.Add(new TextBlock { Text = "安装位置", FontSize = 12.5, Foreground = Text2, Margin = new Thickness(2, 0, 0, 6) });
            var field = new Border { BorderBrush = Line, BorderThickness = new Thickness(1), Background = Field, CornerRadius = new CornerRadius(10), Height = 40 };
            var fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (_dir.Parent is Panel old) old.Children.Remove(_dir);
            fg.Children.Add(_dir);
            var change = TextButton("更改…", () =>
            {
                var picked = SwKit.App.FolderPicker.Pick(this, "选择安装位置", Directory.Exists(_dir.Text) ? _dir.Text : null);
                if (picked != null) _dir.Text = picked.EndsWith("SW工具箱", StringComparison.OrdinalIgnoreCase) ? picked : System.IO.Path.Combine(picked, "SW工具箱");
            });
            change.Margin = new Thickness(0, 0, 4, 0);
            Grid.SetColumn(change, 1);
            fg.Children.Add(change);
            field.Child = fg;
            _dir.GotKeyboardFocus += (s, e) => field.BorderBrush = new SolidColorBrush(Orange);
            _dir.LostKeyboardFocus += (s, e) => field.BorderBrush = Line;
            sp.Children.Add(field);
            sp.Children.Add(Toggle("创建桌面快捷方式", _desktop, v => _desktop = v));
            // 进度条（安装时显示）
            _barTrack = new Grid { Height = 6, Margin = new Thickness(0, 18, 0, 0), Visibility = Visibility.Collapsed };
            _barTrack.Children.Add(new Border { Background = B("#EEEEF1"), CornerRadius = new CornerRadius(3) });
            _barFill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = new LinearGradientBrush(OrangeLight, Orange, 0) };
            _barTrack.Children.Add(_barFill);
            sp.Children.Add(_barTrack);
            _barText = new TextBlock { FontSize = 12, Foreground = Text2, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, TextTrimming = TextTrimming.CharacterEllipsis };
            sp.Children.Add(_barText);
            _body.Children.Add(sp);
        }

        void ShowDone()
        {
            _body.Children.Clear();
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var check = new Grid { Width = 52, Height = 52 };
            check.Children.Add(new Ellipse { Fill = B("#34C759") });
            check.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 24, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(check);
            var tx = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            tx.Children.Add(new TextBlock { Text = "安装完成", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Text1 });
            tx.Children.Add(new TextBlock { Text = "开始菜单" + (_desktop ? "和桌面" : "") + "上都有「" + Program.AppName + "」。卸载在「设置 → 应用」里。", FontSize = 13, Foreground = Text2, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            row.Children.Add(tx);
            sp.Children.Add(row);
            sp.Children.Add(new TextBlock { Text = "第一次用可以先看软件里「应力分析」和「可制造性」页面上的示例。", FontSize = 12.5, Foreground = Text3, Margin = new Thickness(68, 18, 0, 0), TextWrapping = TextWrapping.Wrap });
            _body.Children.Add(sp);
            SetLabel(_primary, "立即启动");
            SetLabel(_secondary, "完成");
            _status.Text = "已安装到 " + _dir.Text; _status.Foreground = Text3;
        }

        void SetProgress(double p, string file)
        {
            _barTrack.Visibility = Visibility.Visible; _barText.Visibility = Visibility.Visible;
            var w = Math.Max(6, (_barTrack.ActualWidth > 0 ? _barTrack.ActualWidth : 568) * p);
            _barFill.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _barText.Text = $"正在安装 · {p:P0} · {file}";
        }

        async Task OnPrimary()
        {
            if (_done)
            {
                try { Process.Start(new ProcessStartInfo(System.IO.Path.Combine(_dir.Text, "SwKit.exe")) { WorkingDirectory = _dir.Text, UseShellExecute = true }); } catch { }
                Close();
                return;
            }
            if (_busy) return;
            var dir = _dir.Text.Trim();
            if (dir.Length == 0) return;
            _busy = true;
            _primary.IsEnabled = _secondary.IsEnabled = _dir.IsEnabled = false;
            _primary.Opacity = 0.6; _secondary.Opacity = 0.4;
            _status.Text = "正在安装，请稍候……"; _status.Foreground = Text2;
            SetProgress(0, "");
            try
            {
                bool desktop = _desktop;
                await Task.Run(() => Program.Install(dir, true, desktop, true, (p, f) => Dispatcher.BeginInvoke(new Action(() => SetProgress(p, f)))));
                _done = true;
                ShowDone();
            }
            catch (Exception ex)
            {
                _status.Text = "安装失败：" + ex.Message; _status.Foreground = B("#D70015");
                _barTrack.Visibility = _barText.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _busy = false;
                _primary.IsEnabled = _secondary.IsEnabled = _dir.IsEnabled = true;
                _primary.Opacity = 1; _secondary.Opacity = 1;
            }
        }

        // ---------- 控件 ----------

        FrameworkElement CaptionButton(string glyph)
        {
            var b = new Border { Width = 46, Height = 36, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            b.MouseEnter += (s, e) => b.Background = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            return b;
        }

        static void SetLabel(FrameworkElement b, string text) => ((TextBlock)((Border)b).Child).Text = text;

        FrameworkElement PrimaryButton(string text, Action onClick)
        {
            var normal = new LinearGradientBrush(OrangeLight, Orange, 90);
            var hover = new LinearGradientBrush(Color.FromRgb(0xFF, 0xAE, 0x52), Color.FromRgb(0xF7, 0x78, 0x1C), 90);
            var b = new Border { Background = normal, CornerRadius = new CornerRadius(19), Height = 38, MinWidth = 124, Padding = new Thickness(22, 0, 22, 0), Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand };
            b.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.22, Color = Orange };
            b.Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            b.MouseEnter += (s, e) => b.Background = hover;
            b.MouseLeave += (s, e) => { b.Background = normal; b.RenderTransform = null; };
            b.MouseLeftButtonDown += (s, e) => { b.RenderTransformOrigin = new Point(0.5, 0.5); b.RenderTransform = new ScaleTransform(0.97, 0.97); };
            b.MouseLeftButtonUp += (s, e) => { b.RenderTransform = null; if (b.IsEnabled) onClick(); };
            return b;
        }

        FrameworkElement TextButton(string text, Action onClick)
        {
            var b = new Border { CornerRadius = new CornerRadius(17), Height = 34, Padding = new Thickness(16, 0, 16, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = text, Foreground = new SolidColorBrush(Orange), FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            b.Child = t;
            b.MouseEnter += (s, e) => b.Background = B("#FFF3E8");
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => { if (b.IsEnabled) onClick(); };
            return b;
        }

        FrameworkElement Toggle(string text, bool on, Action<bool> changed)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 16, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left };
            var track = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11) };
            var knob = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
            knob.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.25 };
            var tt = new TranslateTransform(); knob.RenderTransform = tt;
            track.Child = knob;
            void Apply(bool v, bool anim)
            {
                track.Background = v ? new SolidColorBrush(Orange) : B("#D9D9DE");
                var to = v ? 18.0 : 0.0;
                if (anim) tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                else tt.X = to;
            }
            Apply(on, false);
            row.Children.Add(track);
            row.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Text1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            row.MouseLeftButtonUp += (s, e) => { if (_busy) return; on = !on; Apply(on, true); changed(on); };
            return row;
        }

        // ---------- 预览（开发用）：三个状态渲染成 PNG ----------

        public static int Preview(Application app, string dir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var w = new SetupWindow(dir) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            int code = 0;
            w.Loaded += async (s, e) =>
            {
                try
                {
                    await Task.Delay(300); Shot(w, System.IO.Path.Combine(outDir, "setup-1-准备.png"));
                    w._busy = true; w.SetProgress(0.46, @"gmsh\gmsh.exe"); w._status.Text = "正在安装，请稍候……"; w._primary.Opacity = 0.6; w._secondary.Opacity = 0.4;
                    await Task.Delay(400); Shot(w, System.IO.Path.Combine(outDir, "setup-2-安装中.png"));
                    w._busy = false; w._done = true; w._primary.Opacity = 1; w._secondary.Opacity = 1; w.ShowDone();
                    await Task.Delay(300); Shot(w, System.IO.Path.Combine(outDir, "setup-3-完成.png"));
                }
                catch (Exception ex) { File.WriteAllText(System.IO.Path.Combine(outDir, "error.txt"), ex.ToString()); code = 1; }
                finally { w.Close(); }
            };
            app.Run(w);
            return code;
        }

        static void Shot(Window w, string file)
        {
            var root = (FrameworkElement)w.Content;
            root.UpdateLayout();
            const double scale = 2;
            var rtb = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }
    }
}
