using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SwKit.App
{
    /// <summary>
    /// 本地 API（给 AI / 脚本操控软件）的参数读取工具。
    /// 约定：每个页面实现
    ///     internal async Task&lt;object&gt; ApiAsync(string action, Dictionary&lt;string, object&gt; args)
    ///     internal static IEnumerable&lt;ApiAction&gt; ApiActions
    /// ApiServer 在界面线程上调用 ApiAsync；返回值（Dictionary / List / 字符串 / 数字 / bool）会被序列化成 JSON。
    /// 参数错误时抛 ArgumentException（消息用中文，直接返回给调用方）。
    /// args 来自 JavaScriptSerializer：数字可能是 int / long / decimal / double，数组是 object[] 或 ArrayList，对象是 Dictionary&lt;string, object&gt;。
    /// </summary>
    sealed class ApiAction
    {
        public string Name, Description, Args;
        public ApiAction(string name, string description, string args = "") { Name = name; Description = description; Args = args; }
    }

    static class ApiArgs
    {
        public static bool Has(this Dictionary<string, object> a, string key) => a != null && a.TryGetValue(key, out var v) && v != null;

        public static string Str(this Dictionary<string, object> a, string key, string def = null)
        {
            if (!a.Has(key)) return def;
            var v = a[key];
            return v is string s ? s : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static string ReqStr(this Dictionary<string, object> a, string key) =>
            a.Str(key) ?? throw new ArgumentException($"缺少参数 {key}");

        public static double Num(this Dictionary<string, object> a, string key, double def = double.NaN)
        {
            if (!a.Has(key)) return def;
            var v = a[key];
            if (v is string s) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new ArgumentException($"参数 {key} 应该是数字：{s}");
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { throw new ArgumentException($"参数 {key} 应该是数字"); }
        }

        public static double ReqNum(this Dictionary<string, object> a, string key)
        {
            var v = a.Num(key);
            if (double.IsNaN(v)) throw new ArgumentException($"缺少参数 {key}");
            return v;
        }

        public static int Int(this Dictionary<string, object> a, string key, int def = 0)
        {
            var v = a.Num(key);
            return double.IsNaN(v) ? def : (int)Math.Round(v);
        }

        public static bool Bool(this Dictionary<string, object> a, string key, bool def = false)
        {
            if (!a.Has(key)) return def;
            var v = a[key];
            if (v is bool b) return b;
            if (v is string s) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "是";
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0; } catch { return def; }
        }

        public static List<object> List(this Dictionary<string, object> a, string key)
        {
            if (!a.Has(key)) return new List<object>();
            var v = a[key];
            if (v is string) return new List<object> { v };
            if (v is IEnumerable e) return e.Cast<object>().ToList();
            return new List<object> { v };
        }

        public static Dictionary<string, object> Obj(this Dictionary<string, object> a, string key) =>
            a.Has(key) ? a[key] as Dictionary<string, object> ?? throw new ArgumentException($"参数 {key} 应该是对象") : new Dictionary<string, object>();

        /// <summary>方便构造返回值：Ret("a", 1, "b", "x")。</summary>
        public static Dictionary<string, object> Ret(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }
}
