using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SwKit.Core.Urdf
{
    /// <summary>
    /// 零件几何特征（圆柱面、平面）的磁盘缓存：%LOCALAPPDATA%\SwKit\cache\geom。
    /// 键 = 算法版本 + 文件路径 + 大小 + 修改时间 + 配置；零件改过就自动失效。总大小超过 300 MB 时删掉最旧的一半。
    /// </summary>
    public static class FeatureCache
    {
        const string Version = "geom-v1";
        public static string Dir => Environment.GetEnvironmentVariable("SWKIT_CACHE_DIR") is string d && d.Length > 0
            ? Path.Combine(d, "geom")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwKit", "cache", "geom");

        static string KeyFile(string file, string config)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            var fi = new FileInfo(file);
            var key = $"{Version}|{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{config}";
            using (var sha = SHA1.Create())
                return Path.Combine(Dir, string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(key)).Select(b => b.ToString("x2"))) + ".bin");
        }

        public static bool TryLoad(string file, string config, out PartFeatures f)
        {
            f = null;
            try
            {
                var k = KeyFile(file, config);
                if (k == null || !File.Exists(k)) return false;
                using (var fs = File.OpenRead(k)) f = (PartFeatures)new BinaryFormatter().Deserialize(fs);
                return f != null;
            }
            catch { f = null; return false; }
        }

        public static void Save(string file, string config, PartFeatures f)
        {
            try
            {
                var k = KeyFile(file, config);
                if (k == null) return;
                Directory.CreateDirectory(Dir);
                var tmp = k + ".tmp";
                using (var fs = File.Create(tmp)) new BinaryFormatter().Serialize(fs, f);
                if (File.Exists(k)) File.Delete(k);
                File.Move(tmp, k);
                Trim();
            }
            catch { }
        }

        static DateTime _lastTrim;
        static void Trim()
        {
            if ((DateTime.Now - _lastTrim).TotalMinutes < 5) return;
            _lastTrim = DateTime.Now;
            var files = new DirectoryInfo(Dir).GetFiles("*.bin").OrderBy(x => x.LastWriteTimeUtc).ToList();
            long total = files.Sum(x => x.Length);
            if (total < 300L << 20) return;
            foreach (var x in files.Take(files.Count / 2)) try { x.Delete(); } catch { }
        }
    }
}
