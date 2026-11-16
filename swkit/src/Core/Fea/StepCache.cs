using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SwKit.Core.Fea
{
    /// <summary>
    /// SolidWorks 零件转出的 STEP 的缓存（%LOCALAPPDATA%\SwKit\cache\step）：同一个零件（路径 + 大小 + 修改时间不变）第二次分析时
    /// 直接用上次的 STEP，不用再启动后台 SolidWorks。零件改过就自动重新转换。总大小超过 500 MB 时删掉最旧的一半。
    /// </summary>
    public static class StepCache
    {
        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwKit", "cache", "step");

        static string Key(string part)
        {
            var fi = new FileInfo(part);
            var key = $"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            using (var sha = SHA1.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(key)).Select(b => b.ToString("x2")));
        }

        public static bool TryGet(string part, out string step, out string material)
        {
            step = null; material = null;
            try
            {
                var k = Key(part);
                var s = Path.Combine(Dir, k + ".step");
                if (!File.Exists(s)) return false;
                var m = Path.Combine(Dir, k + ".material.txt");
                material = File.Exists(m) ? File.ReadAllText(m, Encoding.UTF8) : null;
                step = s;
                File.SetLastWriteTimeUtc(s, DateTime.UtcNow);   // 最近用过的不会被清理掉
                return true;
            }
            catch { return false; }
        }

        public static string Put(string part, string step, string material)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var k = Key(part);
                var s = Path.Combine(Dir, k + ".step");
                File.Copy(step, s, true);
                File.WriteAllText(Path.Combine(Dir, k + ".material.txt"), material ?? "", Encoding.UTF8);
                Trim();
                return s;
            }
            catch { return step; }
        }

        static void Trim()
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.step").OrderBy(f => f.LastWriteTimeUtc).ToList();
            if (files.Sum(f => f.Length) < 500L << 20) return;
            foreach (var f in files.Take(files.Count / 2))
                try { f.Delete(); File.Delete(Path.Combine(Dir, Path.GetFileNameWithoutExtension(f.Name) + ".material.txt")); } catch { }
        }
    }
}