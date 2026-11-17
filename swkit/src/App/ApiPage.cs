using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SwKit.App
{
    /// <summary>「AI 控制接口」页：开关本地接口、复制连接配置（Claude Desktop / Claude Code / Cursor / HTTP）、查看所有操作和调用记录。</summary>
    sealed class ApiPage
    {
        public FrameworkElement Root { get; }
        readonly MainWindow _win;
        readonly TextBlock _state, _url;
        readonly CheckBox _enable;
        readonly StackPanel _log;

        static Brush B(string k) => (Brush)Application.Current.FindResource(k);
        static Style S(string k) => (Style)Application.Current.FindResource(k);

        public ApiPage(MainWindow win)
        {
            _win = win;
            var grid = new Grid { Background = Brushes.White };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            Root = grid;

            var head = new StackPanel { Margin = new Thickness(36, 40, 150, 18) };
            head.Children.Add(new TextBlock { Text = "AI 控制接口", Style = S("T.LargeTitle") });
            head.Children.Add(new TextBlock
            {
                Style = S("T.Secondary"), Margin = new Thickness(0, 6, 0, 0), MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left,
                Text = "让你的 AI（Claude、Cursor 等支持 MCP 的工具，或者你自己的脚本）直接操控这个软件：导出、生成 URDF、做标准件、做应力分析和拓扑优化、检查工作区。AI 的每一步都会在界面上实时显示。接口只在本机可用，需要令牌。",
            });
            Grid.SetColumnSpan(head, 2);
            grid.Children.Add(head);

            // ---- 左列：状态、连接方法、调用记录 ----
            var left = new StackPanel { Margin = new Thickness(36, 0, 14, 24) };
            var leftScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = left, CanContentScroll = false };
            Grid.SetRow(leftScroll, 1);
            grid.Children.Add(leftScroll);

            left.Children.Add(new TextBlock { Text = "状态", Style = S("T.Section") });
            var st = new StackPanel();
            _enable = new CheckBox { Style = S("Switch"), IsChecked = ApiServer.EnabledSetting, Content = new TextBlock { Text = "允许 AI 操控本软件", FontSize = 13 } };
            _enable.Checked += (s, e) => { ApiServer.EnabledSetting = true; _win.StartApi(); Refresh(); };
            _enable.Unchecked += (s, e) => { ApiServer.EnabledSetting = false; _win.StopApi(); Refresh(); };
            st.Children.Add(_enable);
            _state = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            st.Children.Add(_state);
            _url = new TextBlock { FontSize = 12, Foreground = B("B.Text2"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            st.Children.Add(_url);
            left.Children.Add(new Border { Style = S("Group"), Child = st });

            left.Children.Add(new TextBlock { Text = "连接你的 AI", Style = S("T.Section") });
            var cli = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "swkit-cli.exe");
            var esc = cli.Replace("\\", "\\\\");
            var port = _win.ApiServer?.Port ?? ApiServer.DefaultPort;
            var token = _win.ApiServer?.Token ?? "（打开开关后显示）";
            left.Children.Add(Connect("Claude Code（推荐）", "在终端里运行这一行。以后在 Claude Code 里直接说「用 SW 工具箱把 D:\\项目 里的零件导出 STEP」：",
                $"claude mcp add swkit -- \"{cli}\" mcp"));
            left.Children.Add(Connect("Claude Desktop、Cursor、VS Code（Copilot / Cline）、Cherry Studio、Trae、Windsurf、Gemini CLI",
                "这些都支持 MCP。在客户端的 MCP 配置里加上这一段，然后重启客户端（Claude Desktop：设置 → 开发者 → 编辑配置）：",
                "{\n  \"mcpServers\": {\n    \"swkit\": {\n      \"command\": \"" + esc + "\",\n      \"args\": [\"mcp\"]\n    }\n  }\n}"));
            left.Children.Add(Connect("GPT（OpenAI Codex CLI）",
                "ChatGPT 网页版和桌面版只能连公网上的服务，连不到你电脑上的程序。要用 GPT 操控这个软件，用 OpenAI 的 Codex CLI：把下面这段加进 ~/.codex/config.toml；或者用下面的「任何大模型」方式。",
                "[mcp_servers.swkit]\ncommand = '" + cli + "'\nargs = [\"mcp\"]"));
            left.Children.Add(Connect("只能填网址的 MCP 客户端", "填这个地址（HTTP 版 MCP，令牌在网址里；也可以用请求头 Authorization: Bearer 令牌）：",
                $"http://127.0.0.1:{port}/mcp?token={token}"));
            left.Children.Add(Connect("任何大模型：GPT、DeepSeek、通义、Kimi、智谱、豆包、本机 Ollama",
                "用你自己的 API 密钥，在终端里和模型对话，它直接操控这个软件。密钥只从环境变量或参数读取，不保存；对话只发给你指定的模型服务。例如：",
                $"set DEEPSEEK_API_KEY=你的密钥\n\"{cli}\" agent --provider deepseek\n\n\"{cli}\" agent --provider qwen          （DASHSCOPE_API_KEY）\n\"{cli}\" agent --provider openai --model 模型名   （OPENAI_API_KEY）\n\"{cli}\" agent --provider ollama --model qwen2.5:14b\n\"{cli}\" agent --base-url 兼容OpenAI的地址 --model 模型名 --key 密钥"));
            left.Children.Add(Connect("自己写程序 / Dify / Coze / LangChain",
                "本地 HTTP 接口：POST /api/<页面>/<操作>，请求体是 JSON 参数，请求头 X-SwKit-Token。OpenAPI 描述和 OpenAI 函数调用格式的工具定义可以直接导入：",
                $"OpenAPI：http://127.0.0.1:{port}/api/openapi.json\n函数定义：http://127.0.0.1:{port}/api/openai-tools\n令牌：{token}\n（端口和令牌也保存在 %APPDATA%\\SwKit\\api.json）"));
            var regen = new Button { Style = S("Btn.Text"), Content = "重新生成令牌", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 14), ToolTip = "令牌泄露时用；之前配置的「只能填网址」客户端和自己写的程序要更新令牌（MCP 和 agent 方式会自动读取，不受影响）" };
            regen.Click += (s, e) =>
            {
                if (MessageBox.Show(_win, "重新生成令牌后，填了旧令牌的客户端会连不上。继续吗？", "SW 工具箱", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                _win.ApiServer?.RegenerateToken();
                MessageBox.Show(_win, "已重新生成。切换一下页面再回来可以看到新的令牌。", "SW 工具箱");
            };
            left.Children.Add(regen);
            left.Children.Add(new TextBlock { Text = "最近的调用", Style = S("T.Section") });
            _log = new StackPanel();
            left.Children.Add(new Border { Style = S("Group"), Child = _log });

            // ---- 右列：操作说明 ----
            var reader = new FlowDocumentScrollViewer { IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Document = Markdown.Render(Reference()) };
            var card = new Border { Style = S("Card"), Margin = new Thickness(0, 0, 30, 24), Child = reader, ClipToBounds = true };
            Grid.SetRow(card, 1); Grid.SetColumn(card, 1);
            grid.Children.Add(card);

            Refresh();
            if (_win.ApiServer != null)
            {
                lock (_win.ApiServer.Log) foreach (var l in _win.ApiServer.Log.Skip(Math.Max(0, _win.ApiServer.Log.Count - 30))) AddLog(l);
                _win.ApiServer.Logged += line => _win.Dispatcher.BeginInvoke(new Action(() => AddLog(line)));
            }
        }

        FrameworkElement Connect(string title, string text, string code)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, Style = S("T.Headline") });
            sp.Children.Add(new TextBlock { Text = text, Style = S("T.Caption"), FontSize = 12, Margin = new Thickness(0, 3, 0, 6) });
            var doc = new FlowDocument { PagePadding = new Thickness(0) };
            doc.Blocks.Add(Markdown.CodeBlock(code, null));
            sp.Children.Add(new FlowDocumentScrollViewer { Document = doc, IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 200 });
            return new Border { Style = S("Group"), Child = sp, Margin = new Thickness(0, 0, 0, 10) };
        }

        void AddLog(string line)
        {
            _log.Children.Add(new TextBlock { Text = line, FontSize = 11.5, FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"), Foreground = B("B.Text2"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = line });
            while (_log.Children.Count > 40) _log.Children.RemoveAt(0);
        }

        void Refresh()
        {
            var srv = _win.ApiServer;
            if (srv != null && srv.Running)
            {
                _state.Text = "● 正在运行，AI 可以连接";
                _state.Foreground = B("B.Green");
                _url.Text = $"地址 http://127.0.0.1:{srv.Port}/api · 令牌保存在 %APPDATA%\\SwKit\\api.json（每次启动都会换新的）";
            }
            else
            {
                _state.Text = "○ 已关闭";
                _state.Foreground = B("B.Text2");
                _url.Text = "打开上面的开关后，AI 才能连接。";
            }
        }

        /// <summary>所有页面的操作说明（从各页面的 ApiActions 生成，和实际代码保持一致）。</summary>
        static string Reference()
        {
            var sb = new StringBuilder();
            sb.Append("# 可用的操作\n\n");
            sb.Append("MCP 里每个操作是一个工具，名字是「页面_操作」，例如 `fea_solve`。HTTP 是 `POST /api/<页面>/<操作>`，请求体是 JSON 参数。\n\n");
            sb.Append("**示例：让 AI 做一次应力分析**\n\n");
            sb.Append("1. `fea_load {\"path\": \"D:\\\\零件\\\\支架.sldprt\"}`\n2. `fea_faces` 查看面编号和位置\n3. `fea_material {\"name\": \"6061-T6\"}`\n4. `fea_fix {\"faces\": [3, 7]}`\n5. `fea_add_load {\"kind\": \"force\", \"faces\": [12], \"value\": 500, \"dir\": [0, -1, 0]}`\n6. `fea_solve {\"adaptive\": true}`\n\n");
            var pages = new (string key, string title, Type type)[]
            {
                ("app", "软件", null), ("export", "一键导出", typeof(ExportPage)), ("urdf", "URDF 导出", typeof(UrdfPage)), ("parts", "标准件库", typeof(PartsPage)),
                ("fea", "应力分析", typeof(FeaPage)), ("dfm", "可制造性", typeof(DfmPage)), ("mass", "质量估算", typeof(MassPage)), ("cfd", "CFD 流场", typeof(CfdPage)), ("workspace", "工作区", typeof(WorkspacePage)), ("prompts", "AI 建模提示词", typeof(PromptKitPage)),
            };
            foreach (var (key, title, type) in pages)
            {
                IEnumerable<ApiAction> acts;
                if (type == null)
                    acts = new[]
                    {
                        new ApiAction("status", "软件状态", ""), new ApiAction("navigate", "切换页面", "{page}"),
                        new ApiAction("screenshot", "窗口截图（PNG 路径）", "{path?}"), new ApiAction("zoom", "界面缩放", "{scale}"),
                        new ApiAction("check_update", "检查 GitHub 上有没有新版本（只查不装）", ""),
                    };
                else
                {
                    var p = type.GetProperty("ApiActions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    acts = p?.GetValue(null) as IEnumerable<ApiAction> ?? Enumerable.Empty<ApiAction>();
                }
                var list = acts.ToList();
                sb.Append($"## {title}（{key}）\n\n");
                if (list.Count == 0) { sb.Append("（这个页面还没有开放接口）\n\n"); continue; }
                sb.Append("| 操作 | 说明 | 参数 |\n|---|---|---|\n");
                foreach (var a in list) sb.Append($"| `{key}_{a.Name}` | {a.Description.Replace("|", "/")} | {(string.IsNullOrEmpty(a.Args) ? "—" : a.Args.Replace("|", "/"))} |\n");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        internal static IEnumerable<ApiAction> ApiActions => Enumerable.Empty<ApiAction>();
        internal Task<object> ApiAsync(string action, Dictionary<string, object> args) => throw new ArgumentException("这个页面没有操作");
    }
}
