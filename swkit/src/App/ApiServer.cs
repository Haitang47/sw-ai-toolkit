using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwKit.App
{
    /// <summary>
    /// 本地 API：让 AI（或脚本）直接操控正在运行的 SW 工具箱，界面会同步显示每一步。
    ///
    /// · 只监听 127.0.0.1（本机），端口默认 47821（被占用时换一个）。
    /// · 每个请求都要带令牌：请求头 X-SwKit-Token: &lt;令牌&gt;（或 Authorization: Bearer &lt;令牌&gt;）。
    ///   端口和令牌写在 %APPDATA%\SwKit\api.json，本机的 AI 工具（swkit-cli mcp）从这里读取；网页无法读这个文件，也无法跨域带自定义请求头，所以浏览器里的网页调用不了。
    /// · GET  /api                     → 所有页面和操作的列表
    /// · POST /api/&lt;页面&gt;/&lt;操作&gt;      → 请求体是 JSON 参数，返回 {"ok":true,"result":…} 或 {"ok":false,"error":"…"}
    ///   页面：app、export、urdf、parts、fea、workspace、prompts。操作在界面线程上执行（和用户点按钮一样）。
    /// </summary>
    sealed class ApiServer : IDisposable
    {
        public const int DefaultPort = 47821;
        public int Port { get; private set; }
        /// <summary>令牌：第一次启动时生成并保存（%APPDATA%\SwKit\api-token.txt），之后不变，这样只能填网址的 MCP 客户端配一次就行；可以在界面上重新生成。</summary>
        public string Token { get; private set; } = LoadToken();
        public static string TokenFile => Path.Combine(Path.GetDirectoryName(InfoFile), "api-token.txt");

        static string LoadToken()
        {
            try
            {
                if (File.Exists(TokenFile)) { var t = File.ReadAllText(TokenFile).Trim(); if (t.Length >= 24) return t; }
                var n = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N").Substring(0, 8);
                Directory.CreateDirectory(Path.GetDirectoryName(TokenFile));
                File.WriteAllText(TokenFile, n);
                return n;
            }
            catch { return Guid.NewGuid().ToString("N"); }
        }

        public void RegenerateToken()
        {
            try { File.Delete(TokenFile); } catch { }
            Token = LoadToken();
            if (Running) WriteInfo();
            Add("令牌已重新生成，之前配置的 AI 客户端需要更新");
        }
        public bool Running => _listener != null;
        public event Action<string> Logged;            // 每次调用的一行记录（界面上显示）
        public readonly List<string> Log = new List<string>();

        readonly MainWindow _win;
        TcpListener _listener;
        CancellationTokenSource _cts;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        public static string InfoFile => Environment.GetEnvironmentVariable("SWKIT_API_INFO") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwKit", "api.json");
        public static string ActionsFile => Path.Combine(Path.GetDirectoryName(InfoFile), "api-actions.json");
        public static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwKit", "api-enabled.txt");

        /// <summary>用户是否开启了接口（默认开启）。</summary>
        public static bool EnabledSetting
        {
            get { try { return !File.Exists(SettingsFile) || File.ReadAllText(SettingsFile).Trim() != "0"; } catch { return true; } }
            set { try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)); File.WriteAllText(SettingsFile, value ? "1" : "0"); } catch { } }
        }

        public ApiServer(MainWindow win) { _win = win; }

        public void Start()
        {
            if (_listener != null) return;
            foreach (var p in new[] { DefaultPort, DefaultPort + 1, DefaultPort + 2, 0 })
            {
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    _listener = l;
                    Port = ((IPEndPoint)l.LocalEndpoint).Port;
                    break;
                }
                catch (SocketException) { }
            }
            if (_listener == null) return;
            _cts = new CancellationTokenSource();
            WriteInfo();
            var ct = _cts.Token;
            Task.Run(() => AcceptLoop(ct));
            Add($"接口已启动：http://127.0.0.1:{Port}/api");
        }

        public void Stop()
        {
            try { _cts?.Cancel(); _listener?.Stop(); } catch { }
            _listener = null;
            try { if (File.Exists(InfoFile) && File.ReadAllText(InfoFile).Contains(Token)) File.Delete(InfoFile); } catch { }
            Add("接口已关闭");
        }

        public void Dispose() => Stop();

        void WriteInfo()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(InfoFile));
                var info = new Dictionary<string, object>
                {
                    ["url"] = $"http://127.0.0.1:{Port}/api", ["port"] = Port, ["token"] = Token,
                    ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id, ["exe"] = System.Reflection.Assembly.GetExecutingAssembly().Location,
                    ["started"] = DateTime.Now.ToString("s"),
                };
                File.WriteAllText(InfoFile, Json.Serialize(info), new UTF8Encoding(false));
                // 操作列表也存一份：MCP 客户端启动时不用先把软件打开就能列出工具
                File.WriteAllText(ActionsFile, Json.Serialize(Describe()), new UTF8Encoding(false));
            }
            catch { }
        }

        void Add(string line)
        {
            var text = $"{DateTime.Now:HH:mm:ss}  {line}";
            lock (Log) { Log.Add(text); if (Log.Count > 300) Log.RemoveAt(0); }
            Logged?.Invoke(text);
        }

        async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { if (ct.IsCancellationRequested) return; continue; }
                _ = Task.Run(() => Handle(client));
            }
        }

        // ================= HTTP（极简：HTTP/1.1，Content-Length，一次请求一个连接）=================

        async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    var stream = client.GetStream();
                    var (method, path, headers, body) = await ReadRequest(stream);
                    if (method == null) return;
                    string token = headers.TryGetValue("x-swkit-token", out var t) ? t : headers.TryGetValue("authorization", out var au) && au.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? au.Substring(7).Trim() : null;
                    var qi = path.IndexOf('?');
                    if (token == null && qi >= 0)
                        foreach (var kv in path.Substring(qi + 1).Split('&'))
                            if (kv.StartsWith("token=")) token = Uri.UnescapeDataString(kv.Substring(6));
                    var pathOnly = (qi >= 0 ? path.Substring(0, qi) : path).TrimEnd('/');
                    if (method == "OPTIONS") { await Write(stream, 403, "{\"ok\":false,\"error\":\"不支持跨域\"}"); return; }
                    if (token != Token) { await Write(stream, 401, Json.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = "令牌不对：请求头 X-SwKit-Token 要等于 %APPDATA%\\SwKit\\api.json 里的 token" })); return; }
                    if (pathOnly == "/mcp") { await HandleMcp(stream, method, body); return; }
                    if (pathOnly == "/api/openai-tools") { await Write(stream, 200, Json.Serialize(OpenAiTools())); return; }
                    if (pathOnly == "/api/openapi.json") { await Write(stream, 200, Json.Serialize(OpenApi())); return; }
                    var segs = pathOnly.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (segs.Length == 0 || segs[0] != "api") { await Write(stream, 404, "{\"ok\":false,\"error\":\"路径应该是 /api/<页面>/<操作>\"}"); return; }
                    object result;
                    if (segs.Length == 1) result = Describe();
                    else
                    {
                        if (segs.Length < 3) throw new ArgumentException("路径应该是 /api/<页面>/<操作>");
                        var args = string.IsNullOrWhiteSpace(body) ? new Dictionary<string, object>() : Json.Deserialize<Dictionary<string, object>>(body) ?? new Dictionary<string, object>();
                        var page = Uri.UnescapeDataString(segs[1]); var action = Uri.UnescapeDataString(segs[2]);
                        Add($"{page}.{action} {Shorten(body)}");
                        result = await Call(page, action, args);
                        Add($"  → 完成");
                    }
                    await Write(stream, 200, Json.Serialize(new Dictionary<string, object> { ["ok"] = true, ["result"] = Clean(result) }));
                }
                catch (Exception ex)
                {
                    var e = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    while (e is AggregateException ae && ae.InnerException != null) e = ae.InnerException;
                    Add("  → 出错：" + e.Message);
                    try { await Write(client.GetStream(), e is ArgumentException ? 400 : 500, Json.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = e.Message })); } catch { }
                }
            }
        }

        static string Shorten(string s) { s = (s ?? "").Replace("\r", "").Replace("\n", " "); return s.Length > 160 ? s.Substring(0, 160) + "…" : s; }

        static async Task<(string method, string path, Dictionary<string, string> headers, string body)> ReadRequest(NetworkStream s)
        {
            var buf = new byte[65536];
            var ms = new MemoryStream();
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = await s.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) return (null, null, null, null);
                ms.Write(buf, 0, n);
                var arr = ms.GetBuffer(); int len = (int)ms.Length;
                for (int i = 3; i < len; i++) if (arr[i - 3] == '\r' && arr[i - 2] == '\n' && arr[i - 1] == '\r' && arr[i] == '\n') { headerEnd = i + 1; break; }
                if (ms.Length > 1 << 20 && headerEnd < 0) return (null, null, null, null);
            }
            var all = ms.ToArray();
            var head = Encoding.ASCII.GetString(all, 0, headerEnd);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var first = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1)) { int c = l.IndexOf(':'); if (c > 0) headers[l.Substring(0, c).Trim().ToLowerInvariant()] = l.Substring(c + 1).Trim(); }
            int contentLength = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var x) ? x : 0;
            if (contentLength > 64 << 20) throw new ArgumentException("请求太大");
            var bodyBytes = new MemoryStream();
            bodyBytes.Write(all, headerEnd, all.Length - headerEnd);
            while (bodyBytes.Length < contentLength)
            {
                int n = await s.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                bodyBytes.Write(buf, 0, n);
            }
            return (first[0].ToUpperInvariant(), first.Length > 1 ? first[1] : "/", headers, Encoding.UTF8.GetString(bodyBytes.ToArray()));
        }

        static async Task Write(NetworkStream s, int code, string json)
        {
            var body = Encoding.UTF8.GetBytes(json ?? "");
            var reason = code == 200 ? "OK" : code == 202 ? "Accepted" : code == 400 ? "Bad Request" : code == 401 ? "Unauthorized" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 405 ? "Method Not Allowed" : "Error";
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(head, 0, head.Length);
            await s.WriteAsync(body, 0, body.Length);
            await s.FlushAsync();
        }

        // ================= HTTP 版 MCP（Streamable HTTP，只认网址的客户端用）=================

        IEnumerable<(string name, string page, string action, string desc)> ToolList()
        {
            var d = (Dictionary<string, object>)Describe();
            foreach (Dictionary<string, object> p in (IEnumerable)d["pages"])
                foreach (Dictionary<string, object> a in (IEnumerable)p["actions"])
                {
                    var args = a["args"] as string;
                    yield return ($"{p["page"]}_{a["name"]}", (string)p["page"], (string)a["name"],
                                  $"[{p["title"]}] {a["description"]}" + (string.IsNullOrEmpty(args) ? "（无参数）" : "。参数：" + args));
                }
        }

        static Dictionary<string, object> FreeSchema() => new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>(), ["additionalProperties"] = true };

        async Task HandleMcp(NetworkStream stream, string method, string body)
        {
            if (method != "POST") { await Write(stream, 405, "{\"error\":\"只支持 POST（不提供 SSE 流）\"}"); return; }
            object parsed = Json.DeserializeObject(body);
            var msgs = parsed is object[] arr ? arr.OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>> { parsed as Dictionary<string, object> };
            var replies = new List<object>();
            foreach (var msg in msgs.Where(m => m != null))
            {
                msg.TryGetValue("id", out var id);
                var m = msg.TryGetValue("method", out var mm) ? mm as string : null;
                var prm = msg.TryGetValue("params", out var pp) ? pp as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                if (id == null) continue;
                var reply = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id };
                try
                {
                    switch (m)
                    {
                        case "initialize":
                            reply["result"] = new Dictionary<string, object>
                            {
                                ["protocolVersion"] = prm.TryGetValue("protocolVersion", out var pv) && pv is string sv ? sv : "2025-03-26",
                                ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object>() },
                                ["serverInfo"] = new Dictionary<string, object> { ["name"] = "swkit", ["version"] = AppInfo.Version },
                                ["instructions"] = "SW 工具箱（SolidWorks 辅助工具）。工具名是「页面_操作」。所有操作在用户看得见的界面上执行；SolidWorks 相关操作在独立的后台 SolidWorks 里进行。路径用 Windows 绝对路径。",
                            };
                            break;
                        case "ping": reply["result"] = new Dictionary<string, object>(); break;
                        case "tools/list":
                            reply["result"] = new Dictionary<string, object> { ["tools"] = ToolList().Select(t => new Dictionary<string, object> { ["name"] = t.name, ["description"] = t.desc, ["inputSchema"] = FreeSchema() }).ToList() };
                            break;
                        case "tools/call":
                            {
                                var name = prm.TryGetValue("name", out var n) ? n as string : null;
                                var args = prm.TryGetValue("arguments", out var a) ? a as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                                var tool = ToolList().FirstOrDefault(t => t.name == name);
                                var content = new List<object>();
                                if (tool.name == null) { content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = "没有这个工具：" + name }); reply["result"] = new Dictionary<string, object> { ["content"] = content, ["isError"] = true }; break; }
                                Add($"[MCP] {tool.page}.{tool.action} {Shorten(Json.Serialize(args))}");
                                try
                                {
                                    var r = Clean(await Call(tool.page, tool.action, args));
                                    content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = Json.Serialize(r) });
                                    if (tool.page == "app" && tool.action == "screenshot" && r is Dictionary<string, object> rd && rd.TryGetValue("path", out var png) && png is string ps && File.Exists(ps))
                                        content.Add(new Dictionary<string, object> { ["type"] = "image", ["data"] = Convert.ToBase64String(File.ReadAllBytes(ps)), ["mimeType"] = "image/png" });
                                    reply["result"] = new Dictionary<string, object> { ["content"] = content };
                                    Add("  → 完成");
                                }
                                catch (Exception ex)
                                {
                                    var e = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                                    content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = "出错：" + e.Message });
                                    reply["result"] = new Dictionary<string, object> { ["content"] = content, ["isError"] = true };
                                    Add("  → 出错：" + e.Message);
                                }
                                break;
                            }
                        default:
                            reply["error"] = new Dictionary<string, object> { ["code"] = -32601, ["message"] = "不支持的方法：" + m };
                            break;
                    }
                }
                catch (Exception ex) { reply["error"] = new Dictionary<string, object> { ["code"] = -32603, ["message"] = ex.Message }; }
                replies.Add(reply);
            }
            if (replies.Count == 0) { await Write(stream, 202, ""); return; }
            await Write(stream, 200, Json.Serialize(parsed is object[] ? (object)replies : replies[0]));
        }

        /// <summary>OpenAI 函数调用格式的工具定义（GPT、DeepSeek、通义、Kimi、智谱、豆包、Ollama 等兼容 OpenAI 接口的模型都能用）。</summary>
        List<object> OpenAiTools() => ToolList().Select(t => (object)new Dictionary<string, object>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object> { ["name"] = t.name, ["description"] = t.desc, ["parameters"] = FreeSchema() },
        }).ToList();

        /// <summary>OpenAPI 3 描述（Dify、Coze 本地版、LangChain 等可以直接导入成工具）。</summary>
        Dictionary<string, object> OpenApi()
        {
            var paths = new Dictionary<string, object>();
            foreach (var t in ToolList())
                paths[$"/api/{t.page}/{t.action}"] = new Dictionary<string, object>
                {
                    ["post"] = new Dictionary<string, object>
                    {
                        ["operationId"] = t.name, ["summary"] = t.desc,
                        ["requestBody"] = new Dictionary<string, object> { ["required"] = false, ["content"] = new Dictionary<string, object> { ["application/json"] = new Dictionary<string, object> { ["schema"] = FreeSchema() } } },
                        ["responses"] = new Dictionary<string, object> { ["200"] = new Dictionary<string, object> { ["description"] = "{ok: true, result: …} 或 {ok: false, error: …}" } },
                    },
                };
            return new Dictionary<string, object>
            {
                ["openapi"] = "3.0.3",
                ["info"] = new Dictionary<string, object> { ["title"] = "SW 工具箱本地接口", ["version"] = AppInfo.Version, ["description"] = "只在本机可用。请求头 X-SwKit-Token 带令牌（%APPDATA%\\SwKit\\api.json）。" },
                ["servers"] = new[] { new Dictionary<string, object> { ["url"] = $"http://127.0.0.1:{Port}" } },
                ["components"] = new Dictionary<string, object> { ["securitySchemes"] = new Dictionary<string, object> { ["token"] = new Dictionary<string, object> { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-SwKit-Token" } } },
                ["security"] = new[] { new Dictionary<string, object> { ["token"] = new string[0] } },
                ["paths"] = paths,
            };
        }

        // ================= 分发 =================

        static readonly (string key, string nav, string title)[] Pages =
        {
            ("export", "NavExport", "一键导出"), ("urdf", "NavUrdf", "URDF 导出"), ("parts", "NavParts", "标准件库"),
            ("fea", "NavFea", "应力分析"), ("dfm", "NavDfm", "可制造性"), ("mass", "NavMass", "质量估算"), ("workspace", "NavWorkspace", "工作区"), ("prompts", "NavPrompts", "AI 建模提示词"),
        };

        static readonly ApiAction[] AppActions =
        {
            new ApiAction("status", "软件状态：版本、当前页面、SolidWorks 是否安装、后台实例状态", ""),
            new ApiAction("navigate", "切换页面", "{page: export | urdf | parts | fea | dfm | mass | workspace | prompts}"),
            new ApiAction("screenshot", "把软件窗口截图存成 PNG，返回路径（AI 可以看图确认界面状态）", "{path?: 输出路径}"),
            new ApiAction("zoom", "界面缩放", "{scale: 0.7–2}"),
            new ApiAction("check_update", "检查 GitHub 上有没有新版本：返回当前版本、最新版本、是否有更新、更新内容和网址（只检查不安装；要更新请用户点软件左下角的「更新」）", ""),
        };

        static Type PageType(string key) =>
            key == "export" ? typeof(ExportPage) : key == "urdf" ? typeof(UrdfPage) : key == "parts" ? typeof(PartsPage) : key == "fea" ? typeof(FeaPage) : key == "dfm" ? typeof(DfmPage) : key == "mass" ? typeof(MassPage) :
            key == "workspace" ? typeof(WorkspacePage) : key == "prompts" ? typeof(PromptKitPage) : null;

        static IEnumerable<ApiAction> ActionsOf(Type t)
        {
            var p = t?.GetProperty("ApiActions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return p?.GetValue(null) as IEnumerable<ApiAction> ?? Enumerable.Empty<ApiAction>();
        }

        object Describe() => new Dictionary<string, object>
        {
            ["name"] = "SW 工具箱", ["version"] = AppInfo.Version,
            ["usage"] = "POST /api/<page>/<action>，请求体是 JSON 参数；请求头 X-SwKit-Token。操作在界面上执行，用户能看到。",
            ["pages"] = new[] { ("app", "软件") }.Concat(Pages.Select(p => (p.key, p.title))).Select(p => new Dictionary<string, object>
            {
                ["page"] = p.Item1, ["title"] = p.Item2,
                ["actions"] = (p.Item1 == "app" ? AppActions : ActionsOf(PageType(p.Item1))).Select(a => new Dictionary<string, object> { ["name"] = a.Name, ["description"] = a.Description, ["args"] = a.Args }).ToList(),
            }).ToList(),
        };

        async Task<object> Call(string page, string action, Dictionary<string, object> args)
        {
            // 所有操作都在界面线程上执行
            var op = _win.Dispatcher.InvokeAsync(() => CallOnUi(page, action, args));
            var inner = await op.Task;
            return await inner;
        }

        async Task<object> CallOnUi(string page, string action, Dictionary<string, object> args)
        {
            if (page == "app") return await AppCall(action, args);
            var p = Pages.FirstOrDefault(x => x.key == page);
            if (p.key == null) throw new ArgumentException($"没有页面 {page}（可选：app、{string.Join("、", Pages.Select(x => x.key))}）");
            // 切到这个页面（用户能看到 AI 在操作哪里）
            var nav = _win.FindNav(p.nav);
            if (nav != null && nav.IsChecked != true) nav.IsChecked = true; else _win.Navigate(page);
            await Task.Delay(50);
            var obj = _win.PageObject(page) ?? throw new InvalidOperationException("页面还没准备好");
            var m = obj.GetType().GetMethod("ApiAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"页面 {page} 还没有开放接口");
            if (!ActionsOf(obj.GetType()).Any(a => a.Name == action))
                throw new ArgumentException($"页面 {page} 没有操作 {action}（可选：{string.Join("、", ActionsOf(obj.GetType()).Select(a => a.Name))}）");
            var task = (Task<object>)m.Invoke(obj, new object[] { action, args });
            return await task;
        }

        async Task<object> AppCall(string action, Dictionary<string, object> args)
        {
            switch (action)
            {
                case "status": return _win.ApiStatus();
                case "navigate":
                    {
                        var key = args.ReqStr("page");
                        var p = Pages.FirstOrDefault(x => x.key == key);
                        if (p.key == null) throw new ArgumentException("没有这个页面：" + key);
                        var nav = _win.FindNav(p.nav);
                        if (nav != null) nav.IsChecked = true;
                        await Task.Delay(100);
                        return new Dictionary<string, object> { ["page"] = key };
                    }
                case "screenshot":
                    {
                        var path = args.Str("path") ?? Path.Combine(Path.GetTempPath(), "swkit-screenshot-" + DateTime.Now.ToString("HHmmss") + ".png");
                        await Task.Delay(200);
                        SaveShot(_win, path);
                        return new Dictionary<string, object> { ["path"] = path };
                    }
                case "zoom":
                    _win.SetZoom(args.ReqNum("scale"));
                    return new Dictionary<string, object> { ["scale"] = args.ReqNum("scale") };
                case "check_update": return await _win.Update.ApiCheckAsync();
                default: throw new ArgumentException("未知操作：" + action);
            }
        }

        /// <summary>按窗口的实际缩放比例截图（4K 屏上也清晰）。</summary>
        public static void SaveShot(Window win, string file)
        {
            var root = (FrameworkElement)win.Content;
            var dpi = VisualTreeHelper.GetDpi(win);
            double scale = Math.Max(1, dpi.DpiScaleX);
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
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        /// <summary>把结果转成 JavaScriptSerializer 能序列化的东西（Vec3、元组等转成数组 / 字典，NaN / ∞ 转成 null）。</summary>
        static object Clean(object o, int depth = 0)
        {
            if (o == null || depth > 20) return null;
            switch (o)
            {
                case string s: return s;
                case bool b: return b;
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? null : (object)Math.Round(d, 6);
                case float f: return float.IsNaN(f) || float.IsInfinity(f) ? null : (object)Math.Round(f, 5);
                case int _: case long _: case decimal _: case short _: case byte _: return o;
                case Enum e: return e.ToString();
                case DateTime dt: return dt.ToString("s");
                case SwKit.Core.Urdf.Vec3 v: return new[] { Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6) };
                case IDictionary<string, object> dict: return dict.ToDictionary(k => k.Key, k => Clean(k.Value, depth + 1));
                case IDictionary dict2: { var r = new Dictionary<string, object>(); foreach (DictionaryEntry kv in dict2) r[kv.Key.ToString()] = Clean(kv.Value, depth + 1); return r; }
                case IEnumerable en: return en.Cast<object>().Select(x => Clean(x, depth + 1)).ToList();
            }
            var t = o.GetType();
            if (t.FullName != null && t.FullName.StartsWith("System.ValueTuple"))
                return t.GetFields().ToDictionary(fi => fi.Name, fi => Clean(fi.GetValue(o), depth + 1));
            return o.ToString();
        }
    }

    static class AppInfo
    {
        public const string Version = "0.7";
    }
}
