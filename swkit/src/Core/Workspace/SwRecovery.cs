using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SwKit.Core.Workspace
{
    /// <summary>
    /// SolidWorks 自己的自动恢复和备份文件。SolidWorks 崩溃后，没来得及保存的修改在自动恢复文件夹里；
    /// 打开了「备份」选项时，每次保存前的旧版本在备份文件夹里。位置在 工具 → 选项 → 系统选项 → 备份/恢复，
    /// 注册表 HKCU\Software\SolidWorks\SOLIDWORKS 20xx\General 下的 AutoRecover Directory / Backup directory，为空时用默认位置。
    /// </summary>
    public static class SwRecovery
    {
        public sealed class Folder
        {
            public string Version, Kind, Path;   // Kind：自动恢复 / 备份
            public bool Exists;
            public bool IsDefault;
        }

        public sealed class Settings
        {
            public string Version;
            public bool AutoRecoverOn;           // 自动恢复
            public int AutoRecoverInterval;      // 间隔（分钟或改动次数，SolidWorks 的单位设置在 AutoRecover Mode）
            public bool BackupsOn;               // 保存时保留旧版本备份
            public int BackupCopies;
        }

        public static string DefaultBase => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TempSWBackupDirectory");

        /// <summary>本机各版本 SolidWorks 的恢复设置。</summary>
        public static List<Settings> ReadSettings()
        {
            var list = new List<Settings>();
            foreach (var (ver, key) in GeneralKeys())
                using (key)
                {
                    list.Add(new Settings
                    {
                        Version = ver,
                        AutoRecoverOn = Int(key, "AutoRecover Mode", 1) != 0 && Int(key, "Interval for AutoSave", 1) > 0,
                        AutoRecoverInterval = Int(key, "Interval for AutoSave", 0),
                        BackupsOn = Int(key, "EnableBackups", 0) != 0,
                        BackupCopies = Int(key, "Backup Copies", 0),
                    });
                }
            return list;
        }

        /// <summary>所有可能放恢复 / 备份文件的文件夹（含默认位置）。</summary>
        public static List<Folder> Folders()
        {
            var list = new List<Folder>();
            void Add(string ver, string kind, string path, bool isDefault)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                path = Environment.ExpandEnvironmentVariables(path.Trim()).TrimEnd('\\');
                if (list.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
                list.Add(new Folder { Version = ver, Kind = kind, Path = path, Exists = Directory.Exists(path), IsDefault = isDefault });
            }
            foreach (var (ver, key) in GeneralKeys())
                using (key)
                {
                    var ar = key.GetValue("AutoRecover Directory") as string;
                    var bk = key.GetValue("Backup directory") as string;
                    Add(ver, "自动恢复", string.IsNullOrWhiteSpace(ar) ? Path.Combine(DefaultBase, "swxauto") : ar, string.IsNullOrWhiteSpace(ar));
                    Add(ver, "备份", string.IsNullOrWhiteSpace(bk) ? Path.Combine(DefaultBase, "swbackupdirectory") : bk, string.IsNullOrWhiteSpace(bk));
                }
            // 默认根目录也看一下（不同版本的子文件夹名可能不同）
            Add(null, "恢复 / 备份", DefaultBase, true);
            return list;
        }

        /// <summary>恢复 / 备份文件夹里的文件，最新的在前。</summary>
        public static List<(FileEntry file, string kind)> Files(int max = 300)
        {
            var result = new List<(FileEntry, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Folders().Where(x => x.Exists))
            {
                try
                {
                    foreach (var fi in new DirectoryInfo(f.Path).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        if (!seen.Add(fi.FullName)) continue;
                        var kind = fi.FullName.IndexOf("swxauto", StringComparison.OrdinalIgnoreCase) >= 0 || fi.Name.IndexOf("autorecover", StringComparison.OrdinalIgnoreCase) >= 0 ? "自动恢复"
                                 : fi.Name.StartsWith("Backup", StringComparison.OrdinalIgnoreCase) || fi.FullName.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0 ? "备份" : f.Kind;
                        result.Add((new FileEntry { Path = fi.FullName, Size = fi.Length, Modified = fi.LastWriteTime }, kind));
                    }
                }
                catch { }
            }
            return result.OrderByDescending(x => x.Item1.Modified).Take(max).ToList();
        }

        /// <summary>另存时建议的文件名：去掉 SolidWorks 加的前缀 / 后缀（Backup of、Backup (2) of、swxauto、AutoRecover 等）。</summary>
        public static string SuggestName(string file)
        {
            var name = Path.GetFileName(file);
            name = Regex.Replace(name, @"^Backup(\s*\(\d+\))?\s+of\s+", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"^(swxauto|autorecover(y)?)[ _\-]*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"[ _\-]*(swxauto|autorecover)(?=\.[^.]+$)", "", RegexOptions.IgnoreCase);
            // 自动恢复文件有时是 名字.SLDPRT.swar 这样的双扩展名
            var m = Regex.Match(name, @"^(.+\.(sldprt|sldasm|slddrw))\.[^.]+$", RegexOptions.IgnoreCase);
            if (m.Success) name = m.Groups[1].Value;
            return name.Length > 0 ? name : Path.GetFileName(file);
        }

        static IEnumerable<(string ver, RegistryKey key)> GeneralKeys()
        {
            RegistryKey root = null;
            try { root = Registry.CurrentUser.OpenSubKey(@"Software\SolidWorks"); } catch { }
            if (root == null) yield break;
            using (root)
                foreach (var name in root.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^SOLIDWORKS \d{4}$", RegexOptions.IgnoreCase)).OrderByDescending(n => n))
                {
                    RegistryKey k = null;
                    try { k = root.OpenSubKey(name + @"\General"); } catch { }
                    if (k != null) yield return (name, k);
                }
        }

        static int Int(RegistryKey k, string name, int def)
        {
            try
            {
                var v = k.GetValue(name);
                if (v is int i) return i;
                if (v != null && int.TryParse(v.ToString(), out var j)) return j;
            }
            catch { }
            return def;
        }
    }
}
