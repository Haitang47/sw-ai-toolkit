using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace SwKit.App
{
    /// <summary>GitHub 上的一个发布版本（只取更新要用的字段）。</summary>
    sealed class ReleaseInfo
    {
        public string Tag;          // "v0.7"
        public string Version;      // "0.7"
        public string Title;        // "SW 工具箱 v0.7"
        public string Notes;        // 更新说明（Markdown）
        public string PageUrl;      // 网页上的发布页
        public string Published;    // "2026-11-17"
        public string AssetName;    // 安装程序，例如 SWKit-v0.7-Setup.exe；这个版本没有安装程序时为 null
        public string AssetUrl;
        public string AssetDigest;  // "sha256:…"（GitHub 自动算的；老的发布可能没有）
        public long AssetSize;
    }

    sealed class UpdateException : Exception
    {
        public enum Kinds { Other, Network, Integrity }
        public readonly Kinds Kind;
        public bool Network => Kind == Kinds.Network;
        public UpdateException(string message, Kinds kind = Kinds.Other, Exception inner = null) : base(message, inner) { Kind = kind; }
    }

    /// <summary>
    /// 在线更新：查 GitHub 上的最新发布 → 下载安装程序 → 校验大小和 SHA-256 → 让安装程序等本程序退出后覆盖安装并重新打开。
    ///
    /// · 安装版（「设置 → 应用」里登记的位置就是本程序所在文件夹）：安装程序带 /update，只换文件、更新登记的版本号，不新建快捷方式。
    /// · 绿色版（解压出来的文件夹）：带 /noreg /noshortcut，只替换那个文件夹里的文件。
    /// · 上次检查的时间和结果记在 %APPDATA%\SwKit\update.json，启动时最多每天自动查一次。
    ///
    /// 开发测试用的环境变量（都不碰用户的真实数据）：
    ///   SWKIT_FAKE_VERSION=0.5        假装当前是这个版本
    ///   SWKIT_UPDATE_URL=<网址或文件>  代替 GitHub 接口（可以是本地 JSON 文件，里面的下载地址也可以是本地文件）
    ///   SWKIT_TEST_DATA=<文件夹>       更新记录、下载的安装程序、工作区设置和历史库放这里，窗口在屏幕外（重新打开的新版本也继承）
    ///   SWKIT_TEST_REGKEY=<HKCU 下的键> 代替「应用」登记的注册表键（测试安装版的更新）
    ///   SWKIT_UPDATE_SELFTEST=<结果文件> 启动后自动走一遍 检查 → 下载 → 校验 → 安装并重启，每一步写进结果文件并截图
    ///   SWKIT_UPDATE_SNAPSHOT=<文件夹>  把侧栏更新区的各个状态渲染成 PNG（不联网）
    /// </summary>
    static class Updater
    {
        public const string Repo = "Haitang47/sw-ai-toolkit";
        public static string ReleasesPage => "https://github.com/" + Repo + "/releases";
        const string DefaultApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
        const string DefaultRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SwKit";
        public const int CheckTimeoutMs = 8000;

        static string Env(string name) { var v = Environment.GetEnvironmentVariable(name); return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }

        public static string CurrentVersion => Env("SWKIT_FAKE_VERSION") ?? AppInfo.Version;
        public static string ApiUrl => Env("SWKIT_UPDATE_URL") ?? DefaultApi;
        static bool Overridden => Env("SWKIT_UPDATE_URL") != null;
        public static string TestData => Env("SWKIT_TEST_DATA") is string d ? Path.GetFullPath(d) : null;
        static string RegKey => Env("SWKIT_TEST_REGKEY") ?? DefaultRegKey;
        public static string StateFile => TestData != null ? Path.Combine(TestData, "update.json")
                                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwKit", "update.json");
        public static string DownloadDir => TestData != null ? Path.Combine(TestData, "SwKit-update") : Path.Combine(Path.GetTempPath(), "SwKit-update");
        public static string AppDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // ================= 版本号 =================

        /// <summary>按数字比较版本号：v0.10 &gt; v0.9，0.6.1 &gt; 0.6。</summary>
        public static int Compare(string a, string b)
        {
            var x = Parts(a); var y = Parts(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
                if (p != q) return p.CompareTo(q);
            }
            return 0;
        }

        public static bool IsNewer(string latest, string current) => Compare(latest, current) > 0;

        static int[] Parts(string v) =>
            Regex.Match(v ?? "", @"\d+(\.\d+)*").Value.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(s => int.TryParse(s, out var n) ? n : int.MaxValue).ToArray();

        static string Normalize(string tag) { var m = Regex.Match(tag ?? "", @"\d+(\.\d+)*"); return m.Success ? m.Value : (tag ?? "").TrimStart('v', 'V'); }

        // ================= 查最新版本 =================

        static HttpClient _http;
        static HttpClient Http
        {
            get
            {
                if (_http != null) return _http;
                // .NET 4.8 在老系统上默认可能不开 TLS 1.2，GitHub 只接受 TLS 1.2 及以上
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                // 走系统代理（公司网络、VPN），用当前登录用户的凭据
                try { if (WebRequest.DefaultWebProxy != null) WebRequest.DefaultWebProxy.Credentials = CredentialCache.DefaultNetworkCredentials; } catch { }
                var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseProxy = true, AllowAutoRedirect = true };
                return _http = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
            }
        }

        static HttpRequestMessage Request(string url, string accept)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "SwKit/" + CurrentVersion);
            req.Headers.TryAddWithoutValidation("Accept", accept);
            return req;
        }

        static bool IsLocal(string url) => url != null && (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(url, @"^[a-zA-Z]:\\|^\\\\"));
        static string LocalPath(string url) => url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;

        /// <summary>问 GitHub 最新的正式发布是哪个版本。出错时抛 UpdateException（消息可以直接给用户看）。</summary>
        public static async Task<ReleaseInfo> FetchLatestAsync(CancellationToken ct)
        {
            var url = ApiUrl;
            string json;
            if (IsLocal(url))
            {
                try { json = File.ReadAllText(LocalPath(url), Encoding.UTF8); }
                catch (Exception ex) { throw new UpdateException("连不上 GitHub（网络或被墙）", UpdateException.Kinds.Network, ex); }
            }
            else
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(CheckTimeoutMs);
                    try
                    {
                        using (var req = Request(url, "application/vnd.github+json"))
                        using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                        {
                            if (!resp.IsSuccessStatusCode) throw HttpError(resp);
                            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new UpdateException($"连不上 GitHub（网络或被墙）：等了 {CheckTimeoutMs / 1000} 秒没有回应", UpdateException.Kinds.Network);
                    }
                    catch (HttpRequestException ex) { throw new UpdateException("连不上 GitHub（网络或被墙）", UpdateException.Kinds.Network, ex); }
                }
            }
            return Parse(json);
        }

        static UpdateException HttpError(HttpResponseMessage resp, bool download = false)
        {
            int code = (int)resp.StatusCode;
            string Header(string n) => resp.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
            if ((code == 403 || code == 429) && (Header("X-RateLimit-Remaining") == "0" || code == 429))
                return new UpdateException("GitHub 暂时限制了访问次数（每小时 60 次），过一会儿再试", UpdateException.Kinds.Network);
            if (code == 404) return new UpdateException(download ? "GitHub 上找不到这个安装程序（可能刚被删除或替换）" : "GitHub 上还没有正式发布的版本");
            return new UpdateException($"GitHub 返回了错误（HTTP {code}）", code >= 500 ? UpdateException.Kinds.Network : UpdateException.Kinds.Other);
        }

        /// <summary>解析 GitHub 接口 /releases/latest 返回的 JSON，挑出安装程序。</summary>
        public static ReleaseInfo Parse(string json)
        {
            Dictionary<string, object> d;
            try { d = Json.DeserializeObject(json) as Dictionary<string, object>; }
            catch (Exception ex) { throw new UpdateException("GitHub 返回的内容不对（可能被网络拦截了）", UpdateException.Kinds.Network, ex); }
            var tag = d == null ? null : Str(d, "tag_name");
            if (string.IsNullOrEmpty(tag)) throw new UpdateException("GitHub 返回的内容不对（可能被网络拦截了）", UpdateException.Kinds.Network);
            var r = new ReleaseInfo
            {
                Tag = tag, Version = Normalize(tag), Title = Str(d, "name") ?? tag,
                Notes = (Str(d, "body") ?? "").TrimStart('﻿').Trim(),
                PageUrl = Str(d, "html_url") ?? ReleasesPage,
                Published = (Str(d, "published_at") ?? "").Length >= 10 ? Str(d, "published_at").Substring(0, 10) : null,
            };
            if (!Allowed(r.PageUrl)) r.PageUrl = ReleasesPage;
            // 发布里有两个文件：SWKit-v版本-Setup.exe（安装程序）和 SWKit-v版本-portable.zip（绿色版）；更新用安装程序
            var exes = (d.TryGetValue("assets", out var a) ? a as System.Collections.IEnumerable : null)?.OfType<Dictionary<string, object>>()
                       .Where(x => (Str(x, "name") ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<Dictionary<string, object>>();
            var setup = exes.FirstOrDefault(x => Regex.IsMatch(Str(x, "name") ?? "", "setup", RegexOptions.IgnoreCase) || (Str(x, "label") ?? "").Contains("安装程序"))
                        ?? (exes.Count == 1 ? exes[0] : null);
            if (setup != null && Allowed(Str(setup, "browser_download_url")))
            {
                r.AssetName = Str(setup, "name");
                r.AssetUrl = Str(setup, "browser_download_url");
                r.AssetDigest = Str(setup, "digest");
                r.AssetSize = setup.TryGetValue("size", out var sz) && sz != null ? Convert.ToInt64(sz) : 0;
            }
            return r;
        }

        /// <summary>只从 GitHub 下载（https）。测试时用 SWKIT_UPDATE_URL 换了接口的，不限制。</summary>
        static bool Allowed(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (Overridden) return true;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
            var h = u.Host.ToLowerInvariant();
            return h == "github.com" || h.EndsWith(".github.com") || h.EndsWith(".githubusercontent.com");
        }

        static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as string : null;

        // ================= 上次检查的记录 =================

        public sealed class State { public DateTime? LastCheck; public ReleaseInfo Latest; }

        public static State LoadState()
        {
            var s = new State();
            try
            {
                if (!File.Exists(StateFile)) return s;
                var d = Json.DeserializeObject(File.ReadAllText(StateFile, Encoding.UTF8)) as Dictionary<string, object>;
                if (d == null) return s;
                if (DateTime.TryParse(Str(d, "lastCheck"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) s.LastCheck = t.ToUniversalTime();
                if (d.TryGetValue("latest", out var l) && l is Dictionary<string, object> ld && Str(ld, "tag") != null)
                    s.Latest = new ReleaseInfo
                    {
                        Tag = Str(ld, "tag"), Version = Normalize(Str(ld, "tag")), Title = Str(ld, "title"), Notes = Str(ld, "notes") ?? "", PageUrl = Str(ld, "url") ?? ReleasesPage,
                        Published = Str(ld, "published"), AssetName = Str(ld, "assetName"), AssetUrl = Str(ld, "assetUrl"), AssetDigest = Str(ld, "assetDigest"),
                        AssetSize = ld.TryGetValue("assetSize", out var z) && z != null ? Convert.ToInt64(z) : 0,
                    };
            }
            catch { }
            return s;
        }

        public static void SaveState(DateTime lastCheckUtc, ReleaseInfo latest)
        {
            try
            {
                var d = new Dictionary<string, object> { ["lastCheck"] = lastCheckUtc.ToUniversalTime().ToString("o") };
                if (latest != null)
                    d["latest"] = new Dictionary<string, object>
                    {
                        ["tag"] = latest.Tag, ["title"] = latest.Title, ["notes"] = latest.Notes, ["url"] = latest.PageUrl, ["published"] = latest.Published,
                        ["assetName"] = latest.AssetName, ["assetUrl"] = latest.AssetUrl, ["assetDigest"] = latest.AssetDigest, ["assetSize"] = latest.AssetSize,
                    };
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
                File.WriteAllText(StateFile, Json.Serialize(d), new UTF8Encoding(false));
            }
            catch { }
        }

        // ================= 下载和校验 =================

        /// <summary>
        /// 下载安装程序到 %TEMP%\SwKit-update\，校验大小和 SHA-256（GitHub 公布的 digest），通过后返回文件路径。
        /// 之前已经下载好、校验也对的直接用。progress(已下载, 总大小) 在后台线程调用。
        /// </summary>
        public static async Task<string> DownloadAsync(ReleaseInfo r, Action<long, long> progress, Action verifying, CancellationToken ct)
        {
            if (r?.AssetUrl == null) throw new UpdateException("这个版本没有安装程序，请打开下载页手动下载");
            Directory.CreateDirectory(DownloadDir);
            var name = string.Concat((Path.GetFileName(r.AssetName ?? "") is string n && n.Length > 0 ? n : $"SWKit-v{r.Version}-Setup.exe").Where(c => Path.GetInvalidFileNameChars().All(x => x != c)));
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
            var file = Path.Combine(DownloadDir, name);
            if (File.Exists(file) && DigestHex(r) != null)
            {
                verifying?.Invoke();
                if (await Task.Run(() => Problem(file, r)).ConfigureAwait(false) == null) { progress?.Invoke(r.AssetSize, r.AssetSize); return file; }
            }
            var part = file + ".part";
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            try
            {
                if (IsLocal(r.AssetUrl)) await CopyLocal(LocalPath(r.AssetUrl), part, r.AssetSize, progress, ct).ConfigureAwait(false);
                else await DownloadHttp(r.AssetUrl, part, r.AssetSize, progress, ct).ConfigureAwait(false);
                verifying?.Invoke();
                var problem = await Task.Run(() => Problem(part, r)).ConfigureAwait(false);
                if (problem != null) throw new UpdateException(problem, UpdateException.Kinds.Integrity);
                if (File.Exists(file)) File.Delete(file);
                File.Move(part, file);
                return file;
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }   // 下坏的、被取消的、校验不对的都不留
                throw;
            }
        }

        static async Task DownloadHttp(string url, string part, long expected, Action<long, long> progress, CancellationToken ct)
        {
            const int StallMs = 30000;   // 30 秒一点数据都没收到就算断了
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(StallMs);
                try
                {
                    using (var req = Request(url, "application/octet-stream"))
                    using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) throw HttpError(resp, true);
                        long total = resp.Content.Headers.ContentLength ?? expected;
                        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (cts.Token.Register(() => { try { resp.Dispose(); } catch { } }))   // .NET Framework 的网络流读的时候不认取消，直接关掉连接
                        using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                        {
                            var buf = new byte[1 << 16];
                            long done = 0;
                            while (true)
                            {
                                int n = await src.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                                if (n <= 0) break;
                                await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                                done += n;
                                if (expected > 0 && done > expected + (1 << 20)) throw new UpdateException("下载的文件比 GitHub 上公布的大，已停止", UpdateException.Kinds.Integrity);
                                progress?.Invoke(done, total);
                                cts.CancelAfter(StallMs);
                            }
                        }
                    }
                }
                catch (Exception ex) when (!(ex is UpdateException))
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (cts.IsCancellationRequested) throw new UpdateException("下载中断了：30 秒没有收到数据（网络不稳定或被墙）", UpdateException.Kinds.Network, ex);
                    if (ex is HttpRequestException || ex is IOException || ex is WebException || ex is ObjectDisposedException)
                        throw new UpdateException("下载中断了：连不上 GitHub（网络或被墙）", UpdateException.Kinds.Network, ex);
                    throw;
                }
            }
        }

        static async Task CopyLocal(string src, string part, long expected, Action<long, long> progress, CancellationToken ct)
        {
            try
            {
                using (var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
                using (var d = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    var buf = new byte[1 << 16]; long done = 0; int n;
                    while ((n = await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await d.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                        done += n;
                        progress?.Invoke(done, s.Length);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { throw new UpdateException("下载失败：" + ex.Message, UpdateException.Kinds.Network, ex); }
        }

        static string DigestHex(ReleaseInfo r)
        {
            var d = r.AssetDigest?.Trim();
            if (d == null || !d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
            var hex = d.Substring(7).Trim();
            return Regex.IsMatch(hex, "^[0-9a-fA-F]{64}$") ? hex.ToLowerInvariant() : null;
        }

        /// <summary>检查下载的文件：大小要和 GitHub 公布的一样；有 SHA-256 的还要一致。没问题返回 null。</summary>
        static string Problem(string file, ReleaseInfo r)
        {
            long len = new FileInfo(file).Length;
            if (r.AssetSize > 0 && len != r.AssetSize)
                return $"下载的文件大小不对（{len:N0} 字节，GitHub 上是 {r.AssetSize:N0} 字节），可能没下完或被网络改动了";
            var want = DigestHex(r);
            if (want == null) return len > 0 ? null : "下载的文件是空的";   // 老的发布没有 digest：靠 HTTPS 和大小
            var got = Sha256(file);
            return got == want ? null : "安装程序的 SHA-256 和 GitHub 上公布的不一致（可能下载损坏或被篡改）";
        }

        public static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>删掉两天前留下的下载文件（安装后的安装程序、没下完的 .part）。</summary>
        public static void CleanupDownloads()
        {
            try
            {
                if (!Directory.Exists(DownloadDir)) return;
                foreach (var f in Directory.GetFiles(DownloadDir))
                    try { if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromDays(2)) File.Delete(f); } catch { }
            }
            catch { }
        }

        // ================= 安装 =================

        /// <summary>安装版：「设置 → 应用」里登记的安装位置就是这个文件夹（只读注册表）。</summary>
        public static bool IsInstalledCopy(string dir)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegKey, false))
                    return k?.GetValue("InstallLocation") is string loc && SamePath(loc, dir);
            }
            catch { return false; }
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a.Trim().Trim('"')).TrimEnd('\\'), Path.GetFullPath(b.Trim().Trim('"')).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>这个文件夹能不能写（装在 Program Files 之类需要管理员权限的地方就不能自动更新）。</summary>
        public static bool CanWrite(string dir)
        {
            try
            {
                var f = Path.Combine(dir, ".swkit-write-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(f, "");
                File.Delete(f);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 启动安装程序的命令：/S 静默、/update 只换文件、/D 装到本程序所在文件夹、/wait 等本进程退出、/run 装完重新打开、/log 记录。
        /// 绿色版再加 /noreg /noshortcut（不登记、不建快捷方式）。
        /// </summary>
        public static ProcessStartInfo InstallerCommand(string setup, string dir, bool installed, int pid, string log)
        {
            string Q(string s) => "\"" + s + "\"";
            dir = Path.GetFullPath(dir).TrimEnd('\\');
            if (dir.EndsWith(":")) dir += "\\.";   // 盘符根目录：不能以 \ 结尾（会把后面的引号转义掉）
            var a = new List<string> { "/S", "/update", Q("/D=" + dir), "/wait=" + pid, "/run", Q("/log=" + log) };
            if (!installed) { a.Add("/noreg"); a.Add("/noshortcut"); }
            if (Env("SWKIT_TEST_REGKEY") is string rk) { a.Add(Q("/regkey=" + rk)); if (installed) a.Add("/noshortcut"); }   // 测试：登记写到测试键，快捷方式绝不碰
            // UseShellExecute：不把本进程的句柄（例如本地接口监听的端口）继承给安装程序
            return new ProcessStartInfo(setup, string.Join(" ", a)) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setup) };
        }

        /// <summary>只在测试时用到的环境变量：启动安装程序前去掉，新打开的版本不会再假装旧版本、不会再自测。</summary>
        public static void ClearSelfTestEnvironment()
        {
            Environment.SetEnvironmentVariable("SWKIT_UPDATE_SELFTEST", null);
            Environment.SetEnvironmentVariable("SWKIT_FAKE_VERSION", null);
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        public static string Mb(long bytes) => (bytes / 1048576.0).ToString(bytes >= 10 * 1048576 ? "0" : "0.0");

        /// <summary>软件里看更新说明时去掉「## 下载」那一节（在软件里更新用不到），以及开头和浮层标题重复的「## 更新内容」。</summary>
        public static string NotesForApp(string md)
        {
            md = (md ?? "").Replace("\r\n", "\n");
            md = Regex.Replace(md, @"(?ms)^##[ \t]*下载[^\n]*\n.*?(?=^##[ \t]|\z)", "").Trim();
            md = Regex.Replace(md, @"^#{1,3}[ \t]*更新内容[ \t]*\n", "");
            return md.Trim();
        }
    }
}
