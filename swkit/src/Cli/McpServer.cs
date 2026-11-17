using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SwKit.Cli
{
    /// <summary>
    /// MCP 服务器（Model Context Protocol，stdio 传输，每行一个 JSON-RPC 2.0 消息）。
    /// Claude Desktop、Claude Code、Cursor 等客户端启动 swkit-cli mcp，就能把 SW 工具箱的每个操作当作工具调用。
    /// 工具实际由正在运行的 SW 工具箱执行（经它的本地接口），界面会同步显示；软件没开时第一次调用会自动打开它。
    /// </summary>
    static class McpServer
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        static string InfoFile => Environment.GetEnvironmentVariable("SWKIT_API_INFO") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwKit", "api.json");
        static string ActionsFile => Path.Combine(Path.GetDirectoryName(InfoFile), "api-actions.json");
        static string AppExe => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SwKit.exe");
        static readonly string ProtocolDefault = "2024-11-05";

        // ================= swkit-cli api：显示连接方法 =================

        public static int Info(string[] args)
        {
            var info = ReadInfo();
            var cli = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "swkit-cli.exe");
            if (info != null && Alive(info)) Console.WriteLine($"SW 工具箱正在运行，本地接口：{info["url"]}（令牌在 {InfoFile}）");
            else Console.WriteLine("SW 工具箱没有运行（或者在「AI 控制接口」页关闭了接口）。MCP 客户端第一次调用工具时会自动打开它。");
            Console.WriteLine();
            Console.WriteLine("Claude Code：");
            Console.WriteLine($"  claude mcp add swkit -- \"{cli}\" mcp");
            Console.WriteLine();
            Console.WriteLine("Claude Desktop / Cursor 等（MCP 配置文件）：");
            Console.WriteLine("  {\"mcpServers\": {\"swkit\": {\"command\": \"" + cli.Replace("\\", "\\\\") + "\", \"args\": [\"mcp\"]}}}");
            Console.WriteLine();
            Console.WriteLine("HTTP：POST <url>/<页面>/<操作>，请求头 X-SwKit-Token: <token>，请求体是 JSON 参数；GET <url> 列出所有操作。");
            return 0;
        }

        // ================= swkit-cli mcp =================

        public static int Run(string[] args)
        {
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                Dictionary<string, object> msg;
                try { msg = Json.Deserialize<Dictionary<string, object>>(line); }
                catch { Send(stdout, null, null, Err(-32700, "JSON 解析失败")); continue; }
                msg.TryGetValue("id", out var id);
                var method = msg.TryGetValue("method", out var m) ? m as string : null;
                var prm = msg.TryGetValue("params", out var p) ? p as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                if (id == null) continue;   // 通知（notifications/initialized 等），不回复
                try
                {
                    object result;
                    switch (method)
                    {
                        case "initialize":
                            result = new Dictionary<string, object>
                            {
                                ["protocolVersion"] = prm.TryGetValue("protocolVersion", out var pv) && pv is string s ? s : ProtocolDefault,
                                ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object>() },
                                ["serverInfo"] = new Dictionary<string, object> { ["name"] = "swkit", ["version"] = "0.6" },
                                ["instructions"] = "SW 工具箱（SolidWorks 辅助工具）。工具名是「页面_操作」：export_*（批量导出 STEP/STL/PDF…）、urdf_*（装配体生成 URDF）、parts_*（标准件和装配生成）、" +
                                                   "fea_*（应力分析、自适应加密、拓扑优化）、workspace_*（同名文件检查、版本历史）、prompts_*（AI 建模提示词）、app_*（状态、切换页面、截图）。" +
                                                   "所有操作在用户看得见的界面上执行；SolidWorks 相关操作在独立的后台 SolidWorks 里进行，不影响用户自己打开的 SolidWorks。路径用 Windows 绝对路径。",
                            };
                            break;
                        case "ping": result = new Dictionary<string, object>(); break;
                        case "tools/list": result = new Dictionary<string, object> { ["tools"] = Tools() }; break;
                        case "tools/call":
                            result = Call(prm.TryGetValue("name", out var n) ? n as string : null,
                                          prm.TryGetValue("arguments", out var a) ? a as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>());
                            break;
                        case "resources/list": result = new Dictionary<string, object> { ["resources"] = new object[0] }; break;
                        case "prompts/list": result = new Dictionary<string, object> { ["prompts"] = new object[0] }; break;
                        default: Send(stdout, id, null, Err(-32601, "不支持的方法：" + method)); continue;
                    }
                    Send(stdout, id, result, null);
                }
                catch (Exception ex) { Send(stdout, id, null, Err(-32603, ex.Message)); }
            }
            return 0;
        }

        static Dictionary<string, object> Err(int code, string message) => new Dictionary<string, object> { ["code"] = code, ["message"] = message };

        static void Send(StreamWriter w, object id, object result, object error)
        {
            var r = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id };
            if (error != null) r["error"] = error; else r["result"] = result;
            lock (w) w.WriteLine(Json.Serialize(r));
        }

        // ---------------- 工具列表 ----------------

        static List<object> Tools()
        {
            Dictionary<string, object> desc = null;
            var info = ReadInfo();
            if (info != null && Alive(info)) { try { desc = Get(info); } catch { } }
            if (desc == null && File.Exists(ActionsFile)) { try { desc = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ActionsFile, Encoding.UTF8)); } catch { } }
            if (desc == null) { info = EnsureApp(); desc = Get(info); }
            var tools = new List<object>();
            foreach (Dictionary<string, object> page in (IEnumerable)desc["pages"])
            {
                var key = (string)page["page"]; var title = (string)page["title"];
                foreach (Dictionary<string, object> a in (IEnumerable)page["actions"])
                {
                    var args = a.TryGetValue("args", out var x) ? x as string : "";
                    tools.Add(new Dictionary<string, object>
                    {
                        ["name"] = key + "_" + (string)a["name"],
                        ["description"] = $"[{title}] {a["description"]}" + (string.IsNullOrEmpty(args) ? "（无参数）" : "。参数：" + args),
                        ["inputSchema"] = new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>(), ["additionalProperties"] = true },
                    });
                }
            }
            return tools;
        }

        // ---------------- 调用 ----------------

        static object Call(string name, Dictionary<string, object> args)
        {
            if (string.IsNullOrEmpty(name) || !name.Contains("_")) throw new ArgumentException("工具名不对：" + name);
            int i = name.IndexOf('_');
            string page = name.Substring(0, i), action = name.Substring(i + 1);
            var info = EnsureApp();
            Dictionary<string, object> resp;
            try { resp = Post(info, page, action, args); }
            catch (WebException ex) when (ex.Response is HttpWebResponse hr)
            {
                using (var r = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) resp = Json.Deserialize<Dictionary<string, object>>(r.ReadToEnd());
            }
            bool ok = resp.TryGetValue("ok", out var o) && o is bool b && b;
            var content = new List<object>();
            if (!ok)
            {
                content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = "出错：" + (resp.TryGetValue("error", out var e) ? e : "未知错误") });
                return new Dictionary<string, object> { ["content"] = content, ["isError"] = true };
            }
            var result = resp.TryGetValue("result", out var res) ? res : null;
            content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = Json.Serialize(result) });
            // 截图：把图片也直接给 AI 看
            if (page == "app" && action == "screenshot" && result is Dictionary<string, object> d && d.TryGetValue("path", out var p) && p is string png && File.Exists(png))
                content.Add(new Dictionary<string, object> { ["type"] = "image", ["data"] = Convert.ToBase64String(File.ReadAllBytes(png)), ["mimeType"] = "image/png" });
            return new Dictionary<string, object> { ["content"] = content };
        }

        // ---------------- 和 SW 工具箱的本地接口通信 ----------------

        static Dictionary<string, object> ReadInfo()
        {
            try { return File.Exists(InfoFile) ? Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(InfoFile, Encoding.UTF8)) : null; }
            catch { return null; }
        }

        static bool Alive(Dictionary<string, object> info)
        {
            try
            {
                var pid = Convert.ToInt32(info["pid"]);
                using (var p = Process.GetProcessById(pid)) if (p.HasExited) return false;
                Get(info, 3000);
                return true;
            }
            catch { return false; }
        }

        /// <summary>确保软件在运行并且接口可用；没运行就打开它，最多等 60 秒。</summary>
        internal static Dictionary<string, object> EnsureApp()
        {
            var info = ReadInfo();
            if (info != null && Alive(info)) return info;
            if (!File.Exists(AppExe)) throw new InvalidOperationException("找不到 SwKit.exe（应该和 swkit-cli.exe 在同一个文件夹）");
            var before = File.Exists(InfoFile) ? File.GetLastWriteTimeUtc(InfoFile) : DateTime.MinValue;
            Process.Start(new ProcessStartInfo(AppExe) { UseShellExecute = true });
            var t = Stopwatch.StartNew();
            while (t.Elapsed.TotalSeconds < 60)
            {
                Thread.Sleep(500);
                if (!File.Exists(InfoFile) || File.GetLastWriteTimeUtc(InfoFile) <= before) continue;
                info = ReadInfo();
                if (info != null && Alive(info)) return info;
            }
            throw new InvalidOperationException("打开了 SW 工具箱，但它的本地接口没有响应。请在软件的「AI 控制接口」页确认接口已开启");
        }

        internal static Dictionary<string, object> Get(Dictionary<string, object> info, int timeout = 15000)
        {
            var req = (HttpWebRequest)WebRequest.Create((string)info["url"]);
            req.Method = "GET"; req.Timeout = timeout; req.Proxy = null;
            req.Headers["X-SwKit-Token"] = (string)info["token"];
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var d = Json.Deserialize<Dictionary<string, object>>(r.ReadToEnd());
                return (Dictionary<string, object>)d["result"];
            }
        }

        internal static Dictionary<string, object> Post(Dictionary<string, object> info, string page, string action, Dictionary<string, object> args)
        {
            var url = (string)info["url"] + "/" + Uri.EscapeDataString(page) + "/" + Uri.EscapeDataString(action);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST"; req.Timeout = Timeout.Infinite; req.ReadWriteTimeout = Timeout.Infinite; req.Proxy = null;
            req.ContentType = "application/json; charset=utf-8";
            req.Headers["X-SwKit-Token"] = (string)info["token"];
            var body = Encoding.UTF8.GetBytes(Json.Serialize(args ?? new Dictionary<string, object>()));
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return Json.Deserialize<Dictionary<string, object>>(r.ReadToEnd());
        }
    }
}
