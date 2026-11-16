using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

// 单文件安装程序：把发布压缩包（嵌在 exe 里的 payload.zip）解压到 %LOCALAPPDATA%\Programs\SW工具箱（不需要管理员权限），
// 建开始菜单 / 桌面快捷方式，在「设置 → 应用」里登记，可以卸载。用户设置在 %APPDATA%\SwKit，升级和卸载都不动它。
//
// 命令行：/S 静默安装；/D=<文件夹> 安装位置；/noshortcut 不建快捷方式；/noreg 不登记到「应用」（开发测试用）；/uninstall 卸载
[assembly: AssemblyTitle("SW 工具箱 安装程序")]
[assembly: AssemblyProduct("SW 工具箱")]

namespace SwKit.Setup
{
    static class Program
    {
        public const string Version = BuildInfo.Version;   // release.ps1 生成 BuildInfo.Version
        public const string AppName = "SW 工具箱";
        const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SwKit";

        public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SW工具箱");

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
            bool noShortcut = args.Any(a => a.Equals("/noshortcut", StringComparison.OrdinalIgnoreCase));
            bool noReg = args.Any(a => a.Equals("/noreg", StringComparison.OrdinalIgnoreCase));
            string dir = args.Where(a => a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)).Select(a => a.Substring(3).Trim('"')).FirstOrDefault() ?? DefaultDir;

            if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
                return Uninstall(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), silent, noReg, noShortcut);

            // /preview <图片>：在屏幕外画出安装窗口（开发时检查界面用）
            int pv = Array.FindIndex(args, a => a.Equals("/preview", StringComparison.OrdinalIgnoreCase));
            if (pv >= 0 && pv + 1 < args.Length)
            {
                Application.EnableVisualStyles();
                using (var f = new SetupForm(dir) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, 0), ShowInTaskbar = false })
                {
                    f.Show();
                    Application.DoEvents();
                    f.ActiveControl = null;
                    using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(args[pv + 1]); }
                }
                return 0;
            }

            if (silent)
            {
                try { Install(dir, !noShortcut, !noShortcut, !noReg, null); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(dir));
            return 0;
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
                var entries = zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith("/")).ToList();
                long total = entries.Sum(e => e.Length), done = 0;
                foreach (var e in entries)
                {
                    // 压缩包里第一层是「SW工具箱/」，去掉它
                    var rel = e.FullName.Replace('/', '\\');
                    int i = rel.IndexOf('\\');
                    if (i >= 0) rel = rel.Substring(i + 1);
                    if (rel.Length == 0 || rel.EndsWith("\\")) continue;
                    var target = Path.GetFullPath(Path.Combine(dir, rel));
                    if (!target.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;   // 防止路径穿越
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                    done += e.Length;
                    progress?.Invoke(total > 0 ? (double)done / total : 1, rel);
                }
            }
            // 卸载程序：把自己复制一份进去
            var self = Assembly.GetExecutingAssembly().Location;
            var uninst = Path.Combine(dir, "卸载.exe");
            try { File.Copy(self, uninst, true); } catch { }
            var exe = Path.Combine(dir, "SwKit.exe");
            if (startMenu)
            {
                var sm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
                Directory.CreateDirectory(sm);
                Shortcut(Path.Combine(sm, AppName + ".lnk"), exe, dir, "AI × SolidWorks 工具集");
                Shortcut(Path.Combine(sm, "使用说明.lnk"), Path.Combine(dir, "使用说明.md"), dir, "使用说明");
            }
            if (desktop) Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"), exe, dir, "AI × SolidWorks 工具集");
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
            if (!silent && MessageBox.Show($"要卸载 {AppName} 吗？\n\n会删除 {dir}。\n你的设置和工作区历史（%APPDATA%\\SwKit、%LOCALAPPDATA%\\SwKit）会保留。", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
            if (Process.GetProcessesByName("SwKit").Any())
            {
                if (!silent) MessageBox.Show("SW 工具箱正在运行，请先关闭它。", AppName);
                return 1;
            }
            if (!noShortcut)
            {
                try { var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); if (File.Exists(lnk)) File.Delete(lnk); } catch { }
                try { var sm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName); if (Directory.Exists(sm)) Directory.Delete(sm, true); } catch { }
            }
            if (!noReg) try { Registry.CurrentUser.DeleteSubKeyTree(RegKey, false); } catch { }
            // 卸载程序自己在这个文件夹里，退出后再删
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (!silent) MessageBox.Show("已卸载。", AppName);
            return 0;
        }
    }

    /// <summary>安装界面：选位置、选快捷方式、进度条，完成后可以直接启动。</summary>
    sealed class SetupForm : Form
    {
        readonly TextBox _dir;
        readonly CheckBox _desktop;
        readonly ProgressBar _bar;
        readonly Label _status;
        readonly Button _install, _cancel, _browse;
        bool _done;

        public SetupForm(string dir)
        {
            Text = $"安装 {Program.AppName} v{Program.Version}";
            // 按屏幕缩放比例换算所有尺寸（字号用「磅」，系统会自动按 DPI 放大；位置和大小是像素，要自己乘）
            AutoScaleMode = AutoScaleMode.None;
            float k;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            int S(int v) => (int)Math.Round(v * k);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            ClientSize = new Size(S(580), S(310));

            var logo = new PictureBox { Location = new Point(S(28), S(26)), Size = new Size(S(64), S(64)), SizeMode = PictureBoxSizeMode.Zoom };
            try
            {
                using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")) Icon = new Icon(st);
                using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")) logo.Image = new Bitmap(Image.FromStream(st));
            }
            catch { }
            var title = new Label { Text = $"{Program.AppName}  v{Program.Version}", Location = new Point(S(108), S(28)), AutoSize = true, Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold) };
            var sub = new Label { Text = "AI × SolidWorks：一键导出、URDF、标准件、应力分析、可制造性", Location = new Point(S(110), S(66)), Size = new Size(S(450), S(22)), ForeColor = Color.FromArgb(110, 110, 115), AutoEllipsis = true };
            var lbl = new Label { Text = "安装到", Location = new Point(S(28), S(112)), AutoSize = true };
            _dir = new TextBox { Text = dir, Location = new Point(S(28), S(136)), Width = S(432) };
            _browse = new Button { Text = "更改…", Location = new Point(S(468), S(134)), Size = new Size(S(84), S(30)), FlatStyle = FlatStyle.System };
            _browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "选择安装位置", SelectedPath = _dir.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) _dir.Text = Path.Combine(d.SelectedPath, "SW工具箱");
            };
            _desktop = new CheckBox { Text = "创建桌面快捷方式", Checked = true, Location = new Point(S(28), S(174)), AutoSize = true };
            _bar = new ProgressBar { Location = new Point(S(28), S(212)), Size = new Size(S(524), S(8)), Visible = false };
            _status = new Label { Text = "不需要管理员权限；你的设置保存在别处，升级不会丢。", Location = new Point(S(28), S(228)), Size = new Size(S(524), S(24)), ForeColor = Color.FromArgb(110, 110, 115), AutoEllipsis = true };
            _install = new Button { Text = "安装", Location = new Point(S(368), S(264)), Size = new Size(S(88), S(32)), FlatStyle = FlatStyle.System };
            _cancel = new Button { Text = "取消", Location = new Point(S(464), S(264)), Size = new Size(S(88), S(32)), FlatStyle = FlatStyle.System };
            _install.Click += async (s, e) => await OnInstall();
            _cancel.Click += (s, e) => Close();
            AcceptButton = _install; CancelButton = _cancel;
            Controls.AddRange(new Control[] { logo, title, sub, lbl, _dir, _browse, _desktop, _bar, _status, _install, _cancel });
            Shown += (s, e) => { _dir.SelectionStart = _dir.Text.Length; _dir.SelectionLength = 0; _install.Focus(); };
        }
        async Task OnInstall()
        {
            if (_done)
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(_dir.Text, "SwKit.exe")) { WorkingDirectory = _dir.Text, UseShellExecute = true }); } catch { }
                Close();
                return;
            }
            var dir = _dir.Text.Trim();
            bool desktop = _desktop.Checked;
            _install.Enabled = _browse.Enabled = _cancel.Enabled = _dir.Enabled = _desktop.Enabled = false;
            _bar.Visible = true; _status.ForeColor = Color.FromArgb(110, 110, 115);
            try
            {
                await Task.Run(() => Program.Install(dir, true, desktop, true, (p, f) => BeginInvoke(new Action(() => { _bar.Value = (int)(p * 100); _status.Text = "正在安装：" + f; }))));
                _done = true;
                _bar.Value = 100;
                _status.Text = "安装完成。开始菜单里有「" + Program.AppName + "」，卸载在「设置 → 应用」里。";
                _status.ForeColor = Color.FromArgb(36, 138, 61);
                _install.Text = "启动"; _cancel.Text = "完成";
                _install.Enabled = _cancel.Enabled = true;
            }
            catch (Exception ex)
            {
                _status.Text = "安装失败：" + ex.Message;
                _status.ForeColor = Color.FromArgb(215, 0, 21);
                _install.Enabled = _browse.Enabled = _cancel.Enabled = _dir.Enabled = _desktop.Enabled = true;
            }
        }
    }
}
