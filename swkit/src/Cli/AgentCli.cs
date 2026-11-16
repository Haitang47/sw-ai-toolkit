using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace SwKit.Cli
{
    /// <summary>
    /// swkit-cli agent：用任何兼容 OpenAI 接口（chat/completions + tools）的大模型操控 SW 工具箱。
    /// GPT、DeepSeek、通义千问、Kimi、智谱 GLM、豆包、SiliconFlow、本机的 Ollama / LM Studio 都可以。
    /// 密钥只从命令行参数或环境变量读，不保存；对话内容只发给你指定的模型服务。
    /// </summary>
    static class AgentCli
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        // 常见服务的接口地址、默认模型（null = 必须用 --model 指定）、密钥环境变量
        static readonly Dictionary<string, (string url, string model, string env)> Providers = new Dictionary<string, (string, string, string)>
        {
            ["openai"] = ("https://api.openai.com/v1", null, "OPENAI_API_KEY"),
            ["deepseek"] = ("https://api.deepseek.com/v1", "deepseek-chat", "DEEPSEEK_API_KEY"),
            ["qwen"] = ("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", "DASHSCOPE_API_KEY"),
            ["kimi"] = ("https://api.moonshot.cn/v1", null, "MOONSHOT_API_KEY"),
            ["zhipu"] = ("https://open.bigmodel.cn/api/paas/v4", null, "ZHIPU_API_KEY"),
            ["doubao"] = ("https://ark.cn-beijing.volces.com/api/v3", null, "ARK_API_KEY"),
            ["siliconflow"] = ("https://api.siliconflow.cn/v1", null, "SILICONFLOW_API_KEY"),
            ["ollama"] = ("http://localhost:11434/v1", null, null),
        };

        const string Usage =
            "用法：swkit-cli agent --provider deepseek|qwen|openai|kimi|zhipu|doubao|siliconflow|ollama [--model 模型名] [--key 密钥] [\"第一句话\"]\n" +
            "      swkit-cli agent --base-url <兼容 OpenAI 的接口地址> --model <模型名> [--key 密钥 | --key-env 环境变量名] [\"第一句话\"]\n" +
            "密钥默认从环境变量读：OPENAI_API_KEY、DEEPSEEK_API_KEY、DASHSCOPE_API_KEY、MOONSHOT_API_KEY、ZHIPU_API_KEY、ARK_API_KEY、SILICONFLOW_API_KEY。\n" +
            "例如：swkit-cli agent --provider deepseek \"把 D:\\零件\\支架.sldprt 做应力分析，底面固定，顶面向下 500 N\"";

        public static int Run(string[] args)
        {
            string provider = null, baseUrl = null, model = null, key = null, keyEnv = null; var first = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--provider": provider = args[++i].ToLowerInvariant(); break;
                    case "--base-url": baseUrl = args[++i]; break;
                    case "--model": model = args[++i]; break;
                    case "--key": key = args[++i]; break;
                    case "--key-env": keyEnv = args[++i]; break;
                    case "-h": case "--help": Console.WriteLine(Usage); return 0;
                    default: first.Add(args[i]); break;
                }
            }
            if (provider != null)
            {
                if (!Providers.TryGetValue(provider, out var p)) { Console.Error.WriteLine("不认识的 --provider：" + provider + "\n" + Usage); return 2; }
                baseUrl = baseUrl ?? p.url; model = model ?? p.model; keyEnv = keyEnv ?? p.env;
            }
            if (baseUrl == null || model == null) { Console.Error.WriteLine(Usage); return 2; }
            key = key ?? (keyEnv != null ? Environment.GetEnvironmentVariable(keyEnv) : null);
            if (string.IsNullOrEmpty(key) && provider != "ollama" && !baseUrl.Contains("localhost") && !baseUrl.Contains("127.0.0.1"))
            {
                Console.Error.WriteLine($"没有密钥：用 --key 传入，或者设置环境变量 {keyEnv ?? "（用 --key-env 指定名字）"}");
                return 2;
            }
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; } catch { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

            // 控制台用 UTF-8 显示中文；输入被重定向（管道）时不能改输入编码
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            if (!Console.IsInputRedirected) try { Console.InputEncoding = new UTF8Encoding(false); } catch { }
            Console.WriteLine("正在连接 SW 工具箱……");
            var info = McpServer.EnsureApp();
            var tools = ToolsFrom(info);
            Console.WriteLine($"已连接（{tools.Count} 个工具）。模型：{model}（{baseUrl}）。输入问题，空行或 exit 退出。\n");

            var messages = new List<object>
            {
                Msg("system",
                    "你在操控用户电脑上的「SW 工具箱」（SolidWorks 辅助软件），通过工具调用完成用户的要求。工具名是「页面_操作」：export_*（批量导出）、urdf_*（装配体生成 URDF）、parts_*（标准件和装配生成）、" +
                    "fea_*（应力分析、自适应加密、拓扑优化）、workspace_*（同名文件、版本历史）、dfm_*（可制造性检查，如果有）、prompts_*、app_*。" +
                    "先用查询类工具（例如 fea_faces、parts_types）弄清楚情况再操作；参数按工具说明里的格式给；路径用 Windows 绝对路径。" +
                    "每一步都会在用户的屏幕上实时显示。完成后用中文简要汇报结果和关键数字。"),
            };
            string line = first.Count > 0 ? string.Join(" ", first) : null;
            while (true)
            {
                if (line == null)
                {
                    Console.Write("你> ");
                    try { line = Console.ReadLine(); } catch (Exception ex) { Console.Error.WriteLine("读取输入失败：" + ex.GetType().Name + " " + ex.Message); line = null; }
                }
                if (line == null || line.Trim().Length == 0 || line.Trim() == "exit") break;
                messages.Add(Msg("user", line));
                line = null;
                for (int round = 0; round < 40; round++)
                {
                    var resp = Chat(baseUrl, key, model, messages, tools);
                    var choice = (Dictionary<string, object>)((IList)resp["choices"])[0];
                    var msg = (Dictionary<string, object>)choice["message"];
                    var calls = msg.TryGetValue("tool_calls", out var tc) ? tc as IList : null;
                    var content = msg.TryGetValue("content", out var c) ? c as string : null;
                    var keep = new Dictionary<string, object> { ["role"] = "assistant", ["content"] = content ?? "" };
                    if (calls != null && calls.Count > 0) keep["tool_calls"] = calls;
                    messages.Add(keep);
                    if (calls == null || calls.Count == 0) { Console.WriteLine("\nAI> " + content + "\n"); break; }
                    if (!string.IsNullOrWhiteSpace(content)) Console.WriteLine("AI> " + content);
                    foreach (Dictionary<string, object> call in calls)
                    {
                        var fn = (Dictionary<string, object>)call["function"];
                        var name = (string)fn["name"];
                        var argText = fn.TryGetValue("arguments", out var at) ? at as string : "{}";
                        Console.WriteLine($"  → {name} {Short(argText)}");
                        string result;
                        try
                        {
                            var a = string.IsNullOrWhiteSpace(argText) ? new Dictionary<string, object>() : Json.Deserialize<Dictionary<string, object>>(argText);
                            int k = name.IndexOf('_');
                            if (k < 0) throw new ArgumentException("工具名不对：" + name);
                            Dictionary<string, object> r;
                            try { r = McpServer.Post(info, name.Substring(0, k), name.Substring(k + 1), a); }
                            catch (WebException ex) when (ex.Response is HttpWebResponse hr)
                            {
                                using (var rd = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) r = Json.Deserialize<Dictionary<string, object>>(rd.ReadToEnd());
                            }
                            result = Json.Serialize(r);
                            Console.WriteLine(r.TryGetValue("ok", out var ok) && ok is bool bo && bo ? "    ✓ 完成" : "    ! " + (r.TryGetValue("error", out var er) ? er : "出错"));
                        }
                        catch (Exception ex) { result = Json.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message }); Console.WriteLine("    ! " + ex.Message); }
                        if (result.Length > 20000) result = result.Substring(0, 20000) + "…（已截断）";
                        messages.Add(new Dictionary<string, object> { ["role"] = "tool", ["tool_call_id"] = call["id"], ["content"] = result });
                    }
                }
            }
            return 0;
        }

        static Dictionary<string, object> Msg(string role, string content) => new Dictionary<string, object> { ["role"] = role, ["content"] = content };
        static string Short(string s) { s = (s ?? "").Replace("\n", " "); return s.Length > 120 ? s.Substring(0, 120) + "…" : s; }

        static List<object> ToolsFrom(Dictionary<string, object> info)
        {
            var req = (HttpWebRequest)WebRequest.Create(((string)info["url"]) + "/openai-tools");
            req.Headers["X-SwKit-Token"] = (string)info["token"]; req.Proxy = null; req.Timeout = 15000;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return ((IEnumerable)Json.DeserializeObject(r.ReadToEnd())).Cast<object>().ToList();
        }

        static Dictionary<string, object> Chat(string baseUrl, string key, string model, List<object> messages, List<object> tools)
        {
            var req = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/chat/completions");
            req.Method = "POST"; req.ContentType = "application/json; charset=utf-8"; req.Timeout = 300000; req.ReadWriteTimeout = 300000;
            if (!string.IsNullOrEmpty(key)) req.Headers["Authorization"] = "Bearer " + key;
            var body = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["model"] = model, ["messages"] = messages, ["tools"] = tools, ["tool_choice"] = "auto", ["temperature"] = 0.2 }));
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            try
            {
                using (var resp = req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return Json.Deserialize<Dictionary<string, object>>(r.ReadToEnd());
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse hr)
            {
                using (var r = new StreamReader(hr.GetResponseStream(), Encoding.UTF8))
                    throw new InvalidOperationException($"模型服务返回 {(int)hr.StatusCode}：{r.ReadToEnd()}");
            }
        }
    }
}
