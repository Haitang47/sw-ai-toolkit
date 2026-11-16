using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SwKit.App
{
    /// <summary>
    /// 「AI 建模提示词」页：在软件里直接阅读提示词和安装说明、一键复制；Claude Skill 安装包可以复制到剪贴板（在 Claude 的上传框里 Ctrl+V）、
    /// 另存，或一键装到 Claude Code；ChatGPT 的知识库文件可以一次全部复制。不再跳转到资源管理器。
    /// </summary>
    sealed class PromptKitPage
    {
        public FrameworkElement Root { get; }
        readonly Window _owner;
        readonly string _kit;
        readonly StackPanel _list;
        readonly TextBlock _title, _sub;
        readonly FlowDocumentScrollViewer _reader;
        readonly List<Kit> _kits = new List<Kit>();
        Kit _currentKit; Doc _current;

        sealed class Doc
        {
            public string Id, Title, Sub, Icon = "";
            public bool Child;
            public Func<string> Markdown;                        // 正文
            public List<(string label, Action<Button> run)> Actions = new List<(string, Action<Button>)>();
            public Func<string> PlainText;                       // 「复制全文」复制的内容（null 时用 Markdown）
        }
        sealed class Kit { public string Id, Name, Desc, Icon, IconBg, IconFg; public List<Doc> Docs = new List<Doc>(); public Border Card; }

        public PromptKitPage(Window owner)
        {
            _owner = owner;
            Root = (FrameworkElement)Ui.Load("PromptKitPage.xaml");
            _list = Root.Find<StackPanel>("FileList");
            _actionsPanel = Root.Find<WrapPanel>("DocActions");
            _tabsGrid = Root.Find<System.Windows.Controls.Primitives.UniformGrid>("KitTabs");
            _title = Root.Find<TextBlock>("DocTitle");
            _sub = Root.Find<TextBlock>("DocSub");
            _reader = Root.Find<FlowDocumentScrollViewer>("Reader");
            _kit = FindPromptKit();
            BuildKits();
            foreach (var k in _kits) _tabsGrid.Children.Add(KitCard(k));
            SelectKit(_kits[0]);
        }

        readonly WrapPanel _actionsPanel;
        readonly System.Windows.Controls.Primitives.UniformGrid _tabsGrid;

        /// <summary>提示词包的位置：发布时放在 exe 旁边的「提示词包」文件夹；开发时在 ..\..\prompt-kit\dist。</summary>
        internal static string FindPromptKit()
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(exeDir, "提示词包"),
                Path.GetFullPath(Path.Combine(exeDir, "..", "..", "prompt-kit", "dist")),
            };
            return candidates.FirstOrDefault(Directory.Exists);
        }

        static string Read(string path) { try { return File.ReadAllText(path, Encoding.UTF8); } catch (Exception ex) { return "读取失败：" + ex.Message; } }

        // ================= 内容 =================

        const string HowTo =
            "## 一次完整的流程\n\n" +
            "1. **拍照**：正对着拍，离远一点再放大；旁边放一枚 1 元硬币（直径 25 mm），或者用卡尺量一个关键尺寸。\n" +
            "2. **发给 AI**：AI 先判断复杂度等级（L1 简单件到 L4 装配体），给出尺寸表、特征树和逐步建模步骤。\n" +
            "3. **回答待确认问题**：AI 会列出照片里看不清的地方，回答后它会修正尺寸。\n" +
            "4. **生成宏并运行**：说「生成 SolidWorks 宏」，在 SolidWorks 里 工具 → 宏 → 新建，粘贴后按 F5，几秒钟建出初版模型。\n\n" +
            "## 小技巧\n\n" +
            "- 一张正视图 + 一张侧视图 + 一张 45° 斜视图，比十张随手拍更有用。\n" +
            "- 关键尺寸（孔距、配合直径）最好直接告诉 AI，例如「两个孔中心距 31 mm，能穿 M3 螺丝」。\n" +
            "- 宏运行出错时，把 SolidWorks 的报错原样发回给 AI，它会改。\n";

        void BuildKits()
        {
            var claude = new Kit { Id = "claude", Name = "Claude Skill", Desc = "Claude Pro 及以上。装一次，以后直接发照片。", Icon = "", IconBg = "#FCEEE7", IconFg = "#C4572F" };
            var gpt = new Kit { Id = "chatgpt", Name = "ChatGPT", Desc = "Plus 及以上。做成专用 GPT 或项目，可分享给同事。", Icon = "", IconBg = "#E8F6EC", IconFg = "#248A3D" };
            var plain = new Kit { Id = "plain", Name = "通用提示词", Desc = "豆包、Kimi、通义、DeepSeek 等任何能看图的 AI。", Icon = "", IconBg = "#EAF3FE", IconFg = "#0071E3" };
            _kits.AddRange(new[] { claude, gpt, plain });
            foreach (var k in _kits) k.Docs.Add(new Doc { Id = k.Id + "/howto", Title = "怎么用", Sub = "拍照 → 发给 AI → 回答问题 → 生成宏", Icon = "", Markdown = () => "# 怎么用\n\n" + HowTo });
            if (_kit == null)
            {
                foreach (var k in _kits) k.Docs.Add(new Doc { Id = k.Id + "/missing", Title = "没有找到提示词包", Markdown = () => "# 没有找到提示词包\n\n请把「提示词包」文件夹放在 SwKit.exe 旁边（发布版里本来就有）。" });
                return;
            }

            // ---- Claude ----
            var cdir = Path.Combine(_kit, "1-Claude-Skill");
            var zip = Directory.Exists(cdir) ? Directory.GetFiles(cdir, "*.zip").FirstOrDefault() : null;
            AddFile(claude, Path.Combine(cdir, "安装说明.md"), "安装说明", "");
            if (zip != null)
            {
                var zipDoc = new Doc
                {
                    Id = "claude/zip", Title = "安装包（zip）", Sub = $"{Path.GetFileName(zip)} · {new FileInfo(zip).Length / 1024.0:0.#} KB · 上传到 Claude 时不要解压", Icon = "",
                    Markdown = () => ZipOverview(zip),
                };
                zipDoc.Actions.Add(("复制安装包", b => { if (Clip.SetFiles(new[] { zip })) Markdown.Flash(b, "已复制，去 Claude 的上传框里 Ctrl+V"); }));
                zipDoc.Actions.Add(("另存为…", b => SaveCopy(zip)));
                zipDoc.Actions.Add(("装到 Claude Code", b => InstallToClaudeCode(zip, b)));
                claude.Docs.Add(zipDoc);
                try
                {
                    using (var za = ZipFile.OpenRead(zip))
                        foreach (var e in za.Entries.Where(e => e.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName.Contains("/references/") ? 1 : 0).ThenBy(e => e.FullName))
                        {
                            var full = e.FullName;
                            claude.Docs.Add(new Doc { Id = "claude/zip/" + full, Title = e.Name, Sub = "安装包内 · " + full, Child = true, Markdown = () => ReadZipEntry(zip, full) });
                        }
                }
                catch { }
            }

            // ---- ChatGPT ----
            var gdir = Path.Combine(_kit, "2-ChatGPT");
            AddFile(gpt, Path.Combine(gdir, "安装说明.md"), "安装说明", "");
            var instr = Path.Combine(gdir, "GPT指令.txt");
            if (File.Exists(instr))
            {
                var d = new Doc
                {
                    Id = "chatgpt/instructions", Title = "GPT 指令", Sub = "粘贴到 GPT 的「指令」或项目的「指令」里", Icon = "",
                    Markdown = () => "# GPT 指令\n\n全选复制，粘贴到 GPT 配置页的「指令」（Instructions）里。\n\n```\n" + Read(instr) + "\n```\n",
                    PlainText = () => Read(instr),
                };
                gpt.Docs.Add(d);
            }
            var kb = Path.Combine(gdir, "知识库");
            if (Directory.Exists(kb))
            {
                var files = Directory.GetFiles(kb, "*.md").OrderBy(f => f).ToArray();
                var d = new Doc
                {
                    Id = "chatgpt/kb", Title = $"知识库（{files.Length} 个文件）", Sub = "全部上传到 GPT 的「知识」或项目的「文件」", Icon = "",
                    Markdown = () => "# 知识库\n\n点上方「复制全部文件」，然后在 ChatGPT 的「知识」上传框里按 Ctrl+V，一次传完。\n\n| 文件 | 大小 |\n|---|---|\n" +
                                     string.Join("\n", files.Select(f => $"| {Path.GetFileName(f)} | {new FileInfo(f).Length / 1024.0:0.#} KB |")) + "\n",
                };
                d.Actions.Add(("复制全部文件", b => { if (Clip.SetFiles(files)) Markdown.Flash(b, "已复制，去上传框里 Ctrl+V"); }));
                d.Actions.Add(("另存到文件夹…", b => SaveFolderCopy(files)));
                gpt.Docs.Add(d);
                foreach (var f in files) AddFile(gpt, f, Path.GetFileNameWithoutExtension(f), null, child: true);
            }

            // ---- 通用 ----
            var pdir = Path.Combine(_kit, "3-通用提示词");
            if (Directory.Exists(pdir))
            {
                var use = Path.Combine(pdir, "使用说明.md");
                AddFile(plain, use, "使用说明", "");
                foreach (var f in Directory.GetFiles(pdir, "*.md").Where(f => !f.Equals(use, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
                    AddFile(plain, f, Path.GetFileNameWithoutExtension(f), "");
            }
        }

        void AddFile(Kit k, string path, string title, string icon, bool child = false)
        {
            if (!File.Exists(path)) return;
            var d = new Doc { Id = k.Id + "/" + Path.GetFileName(path), Title = title, Sub = $"{Path.GetFileName(path)} · {new FileInfo(path).Length / 1024.0:0.#} KB", Child = child, Markdown = () => Read(path) };
            if (icon != null) d.Icon = icon;
            k.Docs.Add(d);
        }

        static string ReadZipEntry(string zip, string entry)
        {
            try
            {
                using (var za = ZipFile.OpenRead(zip))
                using (var r = new StreamReader(za.GetEntry(entry).Open(), Encoding.UTF8)) return r.ReadToEnd();
            }
            catch (Exception ex) { return "读取失败：" + ex.Message; }
        }

        static string ZipOverview(string zip)
        {
            var sb = new StringBuilder();
            sb.Append("# Claude Skill 安装包\n\n");
            sb.Append("**Claude 桌面端 / 网页版**：点上方「复制安装包」，打开 Claude 的 设置 → 功能（Capabilities）→ Skills → 上传 skill，在上传框里按 Ctrl+V（或用「另存为…」存到桌面再选）。不要解压。\n\n");
            sb.Append("**Claude Code**：点上方「装到 Claude Code」，会解压到 `%USERPROFILE%\\.claude\\skills\\`，重启 Claude Code 后生效。\n\n");
            sb.Append("## 包里有什么\n\n| 文件 | 大小 |\n|---|---|\n");
            try
            {
                using (var za = ZipFile.OpenRead(zip))
                    foreach (var e in za.Entries.Where(e => e.Length > 0).OrderBy(e => e.FullName))
                        sb.Append($"| {e.FullName} | {e.Length / 1024.0:0.#} KB |\n");
            }
            catch (Exception ex) { sb.Append($"| 读取失败 | {ex.Message} |\n"); }
            sb.Append("\n左侧可以直接阅读包里的每个文件。\n");
            return sb.ToString();
        }

        void SaveCopy(string file)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(file), Filter = "压缩包|*.zip|所有文件|*.*", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
            if (dlg.ShowDialog(_owner) != true) return;
            try { File.Copy(file, dlg.FileName, true); Toast("已保存到 " + dlg.FileName); }
            catch (Exception ex) { MessageBox.Show(_owner, "保存失败：" + ex.Message, "SW 工具箱"); }
        }

        void SaveFolderCopy(string[] files)
        {
            var dir = FolderPicker.Pick(_owner, "选择保存知识库文件的文件夹");
            if (dir == null) return;
            try { foreach (var f in files) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true); Toast($"已保存 {files.Length} 个文件到 {dir}"); }
            catch (Exception ex) { MessageBox.Show(_owner, "保存失败：" + ex.Message, "SW 工具箱"); }
        }

        void InstallToClaudeCode(string zip, Button b)
        {
            var skills = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");
            try
            {
                string name;
                using (var za = ZipFile.OpenRead(zip)) name = za.Entries.Select(e => e.FullName.Split('/')[0]).FirstOrDefault(n => n.Length > 0) ?? "sw-image-to-model";
                var target = Path.Combine(skills, name);
                if (Directory.Exists(target))
                {
                    var r = MessageBox.Show(_owner, $"已经装过（{target}）。要用这个版本覆盖吗？", "SW 工具箱", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                    Directory.Delete(target, true);
                }
                Directory.CreateDirectory(skills);
                ZipFile.ExtractToDirectory(zip, skills);
                Markdown.Flash(b, "已安装");
                Toast("已装到 " + target + "，重启 Claude Code 后生效");
            }
            catch (Exception ex) { MessageBox.Show(_owner, "安装失败：" + ex.Message, "SW 工具箱"); }
        }

        void Toast(string text) { _sub.Text = text; _sub.Foreground = (Brush)Application.Current.FindResource("B.Green"); }

        // ================= 界面 =================

        FrameworkElement KitCard(Kit k)
        {
            var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Ui.Brush(k.IconBg), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = k.Icon, Style = (Style)Application.Current.FindResource("T.Icon"), FontSize = 16, Foreground = Ui.Brush(k.IconFg) } };
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = k.Name, Style = (Style)Application.Current.FindResource("T.Headline"), FontSize = 14 });
            text.Children.Add(new TextBlock { Text = k.Desc, Style = (Style)Application.Current.FindResource("T.Caption"), Margin = new Thickness(0, 3, 0, 0) });
            var dock = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon); dock.Children.Add(text);
            var card = new Border
            {
                CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), Padding = new Thickness(16, 13, 16, 13), Cursor = Cursors.Hand, Child = dock,
                Margin = new Thickness(k == _kits[0] ? 0 : 7, 0, k == _kits[_kits.Count - 1] ? 0 : 7, 0),
            };
            card.MouseLeftButtonUp += (s, e) => SelectKit(k);
            card.MouseEnter += (s, e) => { if (_currentKit != k) card.Background = Ui.Brush("#FAFAFC"); };
            card.MouseLeave += (s, e) => { if (_currentKit != k) card.Background = Brushes.White; };
            k.Card = card;
            return card;
        }

        void SelectKit(Kit k)
        {
            _currentKit = k;
            foreach (var x in _kits)
            {
                if (x.Card == null) continue;
                x.Card.BorderBrush = x == k ? (Brush)Application.Current.FindResource("B.Accent") : Ui.Brush("#E0E0E6");
                x.Card.Background = x == k ? (Brush)Application.Current.FindResource("B.AccentTint") : Brushes.White;
            }
            _list.Children.Clear();
            RadioButton first = null;
            var group = "doc" + k.Id;
            foreach (var d in k.Docs)
            {
                var rb = new RadioButton { Style = (Style)Application.Current.FindResource("Nav"), GroupName = group, Tag = d.Icon, Content = d.Title, ToolTip = d.Sub };
                if (d.Child) { rb.Margin = new Thickness(16, 0, 0, 0); rb.FontSize = 12; rb.Tag = ""; }
                var dd = d;
                rb.Checked += (s, e) => ShowDoc(dd);
                _list.Children.Add(rb);
                if (first == null) first = rb;
            }
            if (first != null) first.IsChecked = true;
        }

        void ShowDoc(Doc d)
        {
            _current = d;
            _title.Text = d.Title;
            _sub.Text = d.Sub ?? "";
            _sub.Foreground = (Brush)Application.Current.FindResource("B.Text3");
            _actionsPanel.Children.Clear();
            foreach (var (label, run) in d.Actions)
            {
                var b = new Button { Style = (Style)Application.Current.FindResource(_actionsPanel.Children.Count == 0 ? "Btn.Primary" : "Btn.Secondary"), Content = label, Margin = new Thickness(8, 0, 0, 0) };
                b.Click += (s, e) => run(b);
                _actionsPanel.Children.Add(b);
            }
            var copy = new Button { Style = (Style)Application.Current.FindResource(d.Actions.Count == 0 ? "Btn.Primary" : "Btn.Secondary"), Content = "复制全文", Margin = new Thickness(8, 0, 0, 0) };
            copy.Click += (s, e) => { if (Clip.SetText(d.PlainText != null ? d.PlainText() : d.Markdown())) Markdown.Flash(copy, "已复制"); };
            if (d.Id.EndsWith("/zip") == false && d.Id != "chatgpt/kb") _actionsPanel.Children.Add(copy);
            _reader.Document = Markdown.Render(d.Markdown());
        }

        // ================= 给 AI 用的接口 =================

        internal static IEnumerable<ApiAction> ApiActions => new[]
        {
            new ApiAction("docs", "列出提示词包里的所有文档（按 AI 分组）", "{kit?: claude | chatgpt | plain}"),
            new ApiAction("read", "读取某个文档的全文（Markdown）", "{id}"),
            new ApiAction("open", "在界面上打开某个文档", "{id}"),
        };

        internal Task<object> ApiAsync(string action, Dictionary<string, object> args)
        {
            switch (action)
            {
                case "docs":
                    var kit = args.Str("kit");
                    return Task.FromResult<object>(_kits.Where(k => kit == null || k.Id == kit).Select(k => ApiArgs.Ret("kit", k.Id, "name", k.Name,
                        "docs", k.Docs.Select(d => ApiArgs.Ret("id", d.Id, "title", d.Title, "sub", d.Sub)).ToList())).ToList());
                case "read":
                    {
                        var d = FindDoc(args.ReqStr("id"));
                        return Task.FromResult<object>(ApiArgs.Ret("id", d.Id, "title", d.Title, "markdown", d.PlainText != null ? d.PlainText() : d.Markdown()));
                    }
                case "open":
                    {
                        var d = FindDoc(args.ReqStr("id"));
                        var k = _kits.First(x => x.Docs.Contains(d));
                        SelectKit(k);
                        foreach (RadioButton rb in _list.Children) if ((string)rb.Content == d.Title) rb.IsChecked = true;
                        return Task.FromResult<object>(ApiArgs.Ret("opened", d.Id));
                    }
                default: throw new ArgumentException("未知操作：" + action);
            }
        }

        Doc FindDoc(string id) => _kits.SelectMany(k => k.Docs).FirstOrDefault(d => d.Id == id) ?? throw new ArgumentException("没有这个文档：" + id + "（先用 docs 列出）");
    }
}
