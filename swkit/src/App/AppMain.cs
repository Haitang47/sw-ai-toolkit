using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 声明目标框架为 4.8：WPF 才会启用按屏幕缩放（PerMonitorV2）等新行为（用 csc 直接编译时默认没有这个声明）
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace SwKit.App
{
    static class AppMain
    {
        [STAThread]
        static int Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            // 动画默认 60 帧；高刷新率屏幕（120–240 Hz）上提高到 120 帧，切换页面、展开面板更顺
            System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata { DefaultValue = 120 });

            // 开发用的两个模式都在屏幕外运行，出错只写文件，绝不弹窗：
            //   --snapshot <输出文件夹>                       把各页面渲染成 PNG
            //   --selftest <数据文件夹> <输出文件夹> <结果文件> 通过界面流程连续导出两轮
            var snap = Array.IndexOf(args, "--snapshot");
            var self = Array.IndexOf(args, "--selftest");
            var selfUrdf = Array.IndexOf(args, "--selftest-urdf");   // --selftest-urdf <装配体> <输出文件夹> <结果文件>
            var selfParts = Array.IndexOf(args, "--selftest-parts");  // --selftest-parts <零件库文件夹> <结果文件>
            var selfFea = Array.IndexOf(args, "--selftest-fea");      // --selftest-fea <零件 | sample:bracket | sample:cantilever> <结果文件>
            var selfDfm = Array.IndexOf(args, "--selftest-dfm");      // --selftest-dfm <零件> <fdm|sla|cnc|sls|slm|sheet|cut|mold[:材料]> <结果文件>
            var selfWs = Array.IndexOf(args, "--selftest-ws");        // --selftest-ws <设置和历史库文件夹> <工作区> <结果文件> [要改动的文件]
            var selfMass = Array.IndexOf(args, "--selftest-mass");    // --selftest-mass <装配体 | sample> <结果文件>（材料记忆写到临时文件）
            var selfCfd = Array.IndexOf(args, "--selftest-cfd");      // --selftest-cfd <模型 | sample> <粗|标准|细> <结果文件>（桨盘设置写到临时文件）
            string devLog = snap >= 0 && snap + 1 < args.Length ? Path.Combine(args[snap + 1], "error.txt")
                          : self >= 0 && self + 3 < args.Length ? args[self + 3]
                          : selfUrdf >= 0 && selfUrdf + 3 < args.Length ? args[selfUrdf + 3]
                          : selfParts >= 0 && selfParts + 2 < args.Length ? args[selfParts + 2]
                          : selfFea >= 0 && selfFea + 2 < args.Length ? args[selfFea + 2]
                          : selfWs >= 0 && selfWs + 3 < args.Length ? args[selfWs + 3]
                          : selfMass >= 0 && selfMass + 2 < args.Length ? args[selfMass + 2]
                          : selfCfd >= 0 && selfCfd + 3 < args.Length ? args[selfCfd + 3]
                          : selfDfm >= 0 && selfDfm + 3 < args.Length ? args[selfDfm + 3] : null;
            // 开发模式下工作区设置和历史库放到别处，不读写用户的真实设置
            if (selfWs >= 0 && selfWs + 3 < args.Length) SwKit.Core.Workspace.WorkspaceSettings.OverrideDir = Path.GetFullPath(args[selfWs + 1]);
            else if (snap >= 0 && snap + 1 < args.Length) SwKit.Core.Workspace.WorkspaceSettings.OverrideDir = Path.Combine(Path.GetFullPath(args[snap + 1]), "ws-data");
            else if (devLog != null) SwKit.Core.Workspace.WorkspaceSettings.OverrideDir = Path.Combine(Path.GetTempPath(), "swkit-dev-ws");
            MainWindow.DevMode = devLog != null;

            void Report(string text)
            {
                if (devLog != null)
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(devLog))); File.AppendAllText(devLog, text + Environment.NewLine, Encoding.UTF8); } catch { }
                }
                else MessageBox.Show(text, "SW 工具箱", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            try
            {
                app.Resources.MergedDictionaries.Add((ResourceDictionary)Ui.Load("Theme.xaml"));
            }
            catch (Exception ex)
            {
                Report("界面资源加载失败：" + ex.Message);
                return 1;
            }
            app.DispatcherUnhandledException += (s, e) =>
            {
                Report("出现了未处理的错误：\n\n" + (devLog != null ? e.Exception.ToString() : e.Exception.Message));
                e.Handled = true;
                if (devLog != null) app.Shutdown(1);
            };

            MainWindow win;
            try { win = new MainWindow(); }
            catch (Exception ex) { Report("窗口创建失败：" + ex); return 1; }

            // --api-test <接口信息文件>：窗口在屏幕外运行、启动本地接口（开发时测试 AI 接口 / MCP 用，不碰用户的设置）
            var apiTest = Array.IndexOf(args, "--api-test");
            if (apiTest >= 0 && apiTest + 1 < args.Length)
            {
                Environment.SetEnvironmentVariable("SWKIT_API_INFO", Path.GetFullPath(args[apiTest + 1]));
                SwKit.Core.Workspace.WorkspaceSettings.OverrideDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[apiTest + 1])), "ws-data");
                SwKit.Core.Mass.MassStore.OverridePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[apiTest + 1])), "mass-choices.json");
                OffScreen(win);
                win.Width = 1400; win.Height = 900;
                return app.Run(win);
            }

            if (snap >= 0 && snap + 1 < args.Length)
            {
                try { return Snapshot(win, args[snap + 1]); }
                catch (Exception ex) { Report("截图失败：" + ex); return 1; }
            }
            if (self >= 0 && self + 3 < args.Length)
                return SelfTest(app, win, args[self + 1], args[self + 2], args[self + 3]);
            if (selfUrdf >= 0 && selfUrdf + 3 < args.Length)
                return SelfTestUrdf(app, win, args[selfUrdf + 1], args[selfUrdf + 2], args[selfUrdf + 3]);
            if (selfParts >= 0 && selfParts + 2 < args.Length)
                return SelfTestParts(app, win, args[selfParts + 1], args[selfParts + 2]);
            if (selfFea >= 0 && selfFea + 2 < args.Length)
                return SelfTestFea(app, win, args[selfFea + 1], args[selfFea + 2]);
            if (selfDfm >= 0 && selfDfm + 3 < args.Length)
                return SelfTestDfm(app, win, args[selfDfm + 1], args[selfDfm + 2], args[selfDfm + 3]);
            if (selfMass >= 0 && selfMass + 2 < args.Length)
                return SelfTestMass(app, win, args[selfMass + 1], args[selfMass + 2]);
            if (selfCfd >= 0 && selfCfd + 3 < args.Length)
                return SelfTestCfd(app, win, args[selfCfd + 1], args[selfCfd + 2], args[selfCfd + 3]);
            if (selfWs >= 0 && selfWs + 3 < args.Length)
                return SelfTestWs(app, win, args[selfWs + 2], args[selfWs + 3], selfWs + 4 < args.Length && !args[selfWs + 4].StartsWith("--") ? args[selfWs + 4] : null);

            // 在线更新的开发测试（环境变量 SWKIT_TEST_DATA / SWKIT_UPDATE_SNAPSHOT，说明见 Updater.cs 开头）
            var upd = UpdatePanel.DevHooks(app, win, OffScreen);
            if (upd.HasValue) return upd.Value;

            // 支持把文件直接拖到 exe 图标上打开
            var files = Array.FindAll(args, a => File.Exists(a) || Directory.Exists(a));
            if (files.Length > 0) win.Loaded += (s, e) => win.Dispatcher.BeginInvoke(new Action(() => win.Export?.AddPaths(files)), DispatcherPriority.Background);
            return app.Run(win);
        }

        static void OffScreen(Window win)
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -30000; win.Top = 0;
            win.ShowActivated = false;
            win.ShowInTaskbar = false;
        }

        static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
        }

        /// <summary>把窗口内容渲染成 PNG（1.5 倍清晰度）。</summary>
        static void SaveShot(Window win, string file)
        {
            var root = (FrameworkElement)win.Content;
            const double scale = 1.5;
            var rtb = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawRectangle(new VisualBrush(root), null, rect);
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        static int Snapshot(MainWindow win, string dir)
        {
            Directory.CreateDirectory(dir);
            OffScreen(win);
            // SWKIT_SNAP_SIZE=宽x高：按指定窗口大小截图（检查小窗口、大窗口下的布局）
            var size = Environment.GetEnvironmentVariable("SWKIT_SNAP_SIZE");
            if (size != null && size.Contains("x")) { var wh = size.Split('x'); win.MinWidth = 0; win.MinHeight = 0; win.Width = double.Parse(wh[0]); win.Height = double.Parse(wh[1]); }
            win.Show();
            void Shot(string name) { Pump(900); SaveShot(win, Path.Combine(dir, name + ".png")); }

            Shot("1-导出-空");
            win.Export.LoadDemo(@"D:\项目\机械臂");
            Shot("2-导出-进行中");
            win.Export.LoadDemoDone(@"D:\项目\机械臂");
            Shot("3-导出-完成");
            win.Navigate("prompts");
            Shot("4-提示词");
            win.Prompts.ApiAsync("open", new System.Collections.Generic.Dictionary<string, object> { ["id"] = "claude/zip" });
            Shot("4b-提示词-安装包");
            win.Prompts.ApiAsync("open", new System.Collections.Generic.Dictionary<string, object> { ["id"] = "plain/2-L2-机械件.md" });
            Shot("4c-提示词-通用");
            win.Navigate("urdf");
            Shot("5-URDF-空");
            win.Navigate("parts");
            Shot("6-标准件");
            win.Navigate("fea");
            Shot("7-应力分析-空");
            win.Navigate("dfm");
            Shot("7b-可制造性-空");
            win.Navigate("workspace");
            Shot("8-工作区-空");
            win.Navigate("api");
            Shot("9-AI控制接口");
            win.Close();
            return 0;
        }

        static int SelfTestDfm(Application app, MainWindow win, string part, string process, string resultFile)
        {
            OffScreen(win);
            win.Width = 1400; win.Height = 900;
            // SWKIT_SNAP_SIZE=宽x高：按指定窗口大小截图（检查窄窗口下的布局）
            var size = Environment.GetEnvironmentVariable("SWKIT_SNAP_SIZE");
            if (size != null && size.Contains("x")) { var wh = size.Split('x'); win.MinWidth = 0; win.MinHeight = 0; win.Width = double.Parse(wh[0]); win.Height = double.Parse(wh[1]); }
            var code = 0;
            win.Loaded += async (s, e) =>
            {
                try
                {
                    await Task.Delay(400);
                    win.Navigate("dfm");
                    var docx = Path.ChangeExtension(Path.GetFullPath(resultFile), ".docx");
                    var text = await win.Dfm.SelfTest(part, process, docx);
                    File.WriteAllText(resultFile, text, Encoding.UTF8);
                    await Task.Delay(1200);
                    SaveShot(win, Path.ChangeExtension(resultFile, ".png"));
                }
                catch (Exception ex)
                {
                    File.AppendAllText(resultFile, "自测出错：" + ex + Environment.NewLine, Encoding.UTF8);
                    code = 1;
                }
                finally { win.Close(); }
            };
            app.Run(win);
            return code;
        }

        static int SelfTestWs(Application app, MainWindow win, string ws, string resultFile, string touch)
        {
            OffScreen(win);
            win.Width = 1400; win.Height = 900;
            var code = 0;
            win.Loaded += async (s, e) =>
            {
                try
                {
                    await Task.Delay(400);
                    win.Navigate("workspace");
                    var dir = Path.GetDirectoryName(Path.GetFullPath(resultFile));
                    var text = await win.Workspace.SelfTest(ws, touch, async name => { await Task.Delay(700); SaveShot(win, Path.Combine(dir, "ws-" + name + ".png")); });
                    File.WriteAllText(resultFile, text, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(resultFile, "自测出错：" + ex + Environment.NewLine, Encoding.UTF8);
                    code = 1;
                }
                finally { win.Close(); }
            };
            app.Run(win);
            return code;
        }

        static int SelfTestFea(Application app, MainWindow win, string part, string resultFile)
        {
            OffScreen(win);
            win.Width = 1400; win.Height = 900;
            var code = 0;
            win.Loaded += async (s, e) =>
            {
                try
                {
                    await Task.Delay(400);
                    win.Navigate("fea");
                    var text = await win.Fea.SelfTest(part, async name => { await Task.Delay(900); SaveShot(win, Path.ChangeExtension(resultFile, null) + "-" + name + ".png"); });
                    File.WriteAllText(resultFile, text, Encoding.UTF8);
                    await Task.Delay(1200);
                    SaveShot(win, Path.ChangeExtension(resultFile, ".png"));
                }
                catch (Exception ex)
                {
                    File.AppendAllText(resultFile, "自测出错：" + ex + Environment.NewLine, Encoding.UTF8);
                    code = 1;
                }
                finally { win.Close(); }
            };
            app.Run(win);
            return code;
        }

        static int SelfTestCfd(Application app, MainWindow win, string file, string detail, string resultFile)
        {
            OffScreen(win);
            var size = Environment.GetEnvironmentVariable("SWKIT_SNAP_SIZE")?.Split('x');
            win.Width = size?.Length == 2 ? double.Parse(size[0]) : 1400; win.Height = size?.Length == 2 ? double.Parse(size[1]) : 900;
            var code = 0;
            win.Loaded += async (s, e) =>
            {
                try
                {
                    await Task.Delay(400);
                    win.Navigate("cfd");
                    var text = await win.Cfd.SelfTest(file, detail, async name => { await Task.Delay(900); SaveShot(win, Path.ChangeExtension(resultFile, null) + "-" + name + ".png"); });
                    File.WriteAllText(resultFile, text, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(resultFile, "自测出错：" + ex + Environment.NewLine, Encoding.UTF8);
                    code = 1;
                }
                finally { win.Close(); }
            };
            app.Run(win);
            return code;
        }

        static int SelfTestMass(Application app, MainWindow win, string file, string resultFile)
        {
            OffScreen(win);
            var size = Environment.GetEnvironmentVariable("SWKIT_SNAP_SIZE")?.Split('x');
            win.Width = size?.Length == 2 ? double.Parse(size[0]) : 1400; win.Height = size?.Length == 2 ? double.Parse(size[1]) : 900;
            var code = 0;
            win.Loaded += async (s, e) =>
            {
                try
                {
                    await Task.Delay(400);
                    win.Navigate("mass");
                    var path = file == "sample" ? MassPage.SamplePath() : file;
                    var text = await win.Mass.SelfTest(path, async name => { await Task.Delay(900); SaveShot(win, Path.ChangeExtension(resultFile, null) + "-" + name + ".png"); }, Path.ChangeExtension(resultFile, null) + "-popup.png");
                    File.WriteAllText(resultFile, text, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(resultFile, "自测出错：" + ex + Environment.NewLine, Encoding.UTF8);
                    code = 1;
                }
                finally { win.Close(); }
            };
            app.Run(win);
            return code;
        }

        static int SelfTestParts(Application app, MainWindow win, string lib, string resultFile)
        {
            OffScreen(win);
            win.Width = 1400; win.Height = 900;
            var code = 0;
            win.Loaded += async (s, e) =>
            {
                try
                {
                    await Task.Delay(400);
                    win.Navigate("parts");
                    var text = await win.Parts.SelfTest(lib);
                    File.WriteAllText(resultFile, text, Encoding.UTF8);
                    await Task.Delay(900);
                    SaveShot(win, Path.ChangeExtension(resultFile, ".png"));
                }
                catch (Exception ex)
                {
                    File.AppendAllText(resultFile, "自测出错：" + ex + Environment.NewLine, Encoding.UTF8);
                    code = 1;
                }
                finally { win.Close(); }
            };
            app.Run(win);
            return code;
        }

        static int SelfTestUrdf(Application app, MainWindow win, string asm, string outDir, string resultFile)
        {
            OffScreen(win);
            win.Width = 1400; win.Height = 900;
            var code = 0;
            win.Loaded += async (s, e) =>
            {
                try
                {
                    await Task.Delay(400);
                    win.Navigate("urdf");
                    var text = await win.Urdf.SelfTest(asm, outDir);
                    File.WriteAllText(resultFile, text, Encoding.UTF8);
                    await Task.Delay(1200);
                    SaveShot(win, Path.ChangeExtension(resultFile, ".png"));
                }
                catch (Exception ex)
                {
                    File.AppendAllText(resultFile, "自测出错：" + ex + Environment.NewLine, Encoding.UTF8);
                    code = 1;
                }
                finally { win.Close(); }
            };
            app.Run(win);
            return code;
        }

        static int SelfTest(Application app, MainWindow win, string dataDir, string outDir, string resultFile)
        {
            OffScreen(win);
            var code = 0;
            win.Loaded += async (s, e) =>
            {
                try
                {
                    await Task.Delay(400);
                    var text = await win.Export.SelfTest(dataDir, outDir);
                    File.WriteAllText(resultFile, text, Encoding.UTF8);
                    await Task.Delay(900);
                    SaveShot(win, Path.ChangeExtension(resultFile, ".png"));
                }
                catch (Exception ex)
                {
                    File.AppendAllText(resultFile, "自测出错：" + ex + Environment.NewLine, Encoding.UTF8);
                    code = 1;
                }
                finally { win.Close(); }
            };
            app.Run(win);
            return code;
        }
    }
}
