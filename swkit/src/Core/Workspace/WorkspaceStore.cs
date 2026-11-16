using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace SwKit.Core.Workspace
{
    /// <summary>工作区设置：工作目录列表和版本历史的参数，保存在 %APPDATA%\SwKit\workspaces.json。</summary>
    [DataContract]
    public sealed class WorkspaceSettings
    {
        [DataMember(Order = 1)] public List<string> Workspaces = new List<string>();
        [DataMember(Order = 2)] public string Selected;
        [DataMember(Order = 3)] public bool AutoHistory = true;      // 软件开着时，保存后自动存一份
        [DataMember(Order = 4)] public int KeepPerFile = 20;         // 每个文件保留的版本数
        [DataMember(Order = 5)] public double MaxHistoryGb = 2;      // 每个工作区历史库的总大小上限

        // 反序列化不会执行字段初始值，旧文件里缺的字段在这里补默认值
        [OnDeserializing] void Defaults(StreamingContext c) { Workspaces = new List<string>(); AutoHistory = true; KeepPerFile = 20; MaxHistoryGb = 2; }

        /// <summary>开发自测时把设置和历史库都放到别处，不碰用户的真实设置。</summary>
        public static string OverrideDir;

        public static string BaseDir => OverrideDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwKit");
        public static string FilePath => Path.Combine(BaseDir, "workspaces.json");

        public static WorkspaceSettings Load()
        {
            WorkspaceSettings s = null;
            try
            {
                if (File.Exists(FilePath))
                    using (var fs = File.OpenRead(FilePath))
                        s = (WorkspaceSettings)new DataContractJsonSerializer(typeof(WorkspaceSettings)).ReadObject(fs);
            }
            catch { s = null; }
            if (s == null) s = new WorkspaceSettings();
            if (s.Workspaces == null) s.Workspaces = new List<string>();
            s.Workspaces = s.Workspaces.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (s.KeepPerFile <= 0) s.KeepPerFile = 20;
            if (s.MaxHistoryGb <= 0) s.MaxHistoryGb = 2;
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(BaseDir);
            var tmp = FilePath + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, true, true, "  "))
                new DataContractJsonSerializer(typeof(WorkspaceSettings)).WriteObject(w, this);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

        public bool Add(string path)
        {
            path = Normalize(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("文件夹不存在：" + path);
            if (Workspaces.Any(w => string.Equals(w, path, StringComparison.OrdinalIgnoreCase))) return false;
            var parent = Workspaces.FirstOrDefault(w => IsUnder(path, w));
            if (parent != null) throw new ArgumentException($"它已经在工作区「{parent}」里面了");
            Workspaces.Add(path);
            return true;
        }

        public bool Remove(string path)
        {
            var n = Workspaces.RemoveAll(w => string.Equals(w, Normalize(path), StringComparison.OrdinalIgnoreCase));
            if (Selected != null && string.Equals(Selected, Normalize(path), StringComparison.OrdinalIgnoreCase)) Selected = Workspaces.FirstOrDefault();
            return n > 0;
        }

        /// <summary>file 是否在 dir 里面（含 dir 本身）。</summary>
        public static bool IsUnder(string file, string dir)
        {
            dir = dir.TrimEnd('\\');
            return file.Equals(dir, StringComparison.OrdinalIgnoreCase) || file.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public string WorkspaceOf(string file) =>
            Workspaces.Where(w => IsUnder(file, w)).OrderByDescending(w => w.Length).FirstOrDefault();

        public static string Hash(string text, int hexChars)
        {
            using (var sha = SHA1.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToLowerInvariant()));
                return string.Concat(b.Select(x => x.ToString("x2"))).Substring(0, hexChars);
            }
        }
    }
}
