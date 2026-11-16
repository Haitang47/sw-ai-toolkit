using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SwKit.Core.Report
{
    /// <summary>
    /// 报告内容（与输出格式无关）：标题、段落、列表、表格、图片、参考文献。
    /// 由 DocxWriter 写成 Word，MarkdownWriter 写成 Markdown（方便贴进 LaTeX / 笔记），表格可以另存 CSV。
    /// 图、表自动编号（图 1、表 1），文中引用参考文献用 [1] 这样的编号，References() 列表的顺序就是编号。
    /// </summary>
    public sealed class ReportDoc
    {
        public string Title = "报告", Subtitle, Author;
        public DateTime Date = DateTime.Now;
        public readonly List<Block> Blocks = new List<Block>();
        public readonly List<string> Refs = new List<string>();

        public abstract class Block { }
        public sealed class Heading : Block { public int Level; public string Text; }
        public sealed class Para : Block { public string Text; public bool Note; }
        public sealed class Bullets : Block { public List<string> Items; public bool Numbered; }
        public sealed class TableBlock : Block { public string Caption; public string[] Header; public List<string[]> Rows; public string CsvName; }
        public sealed class Image : Block { public string Path; public string Caption; public double WidthCm = 14; }
        public sealed class KeyValues : Block { public List<(string key, string value)> Items; }

        public ReportDoc H1(string t) { Blocks.Add(new Heading { Level = 1, Text = t }); return this; }
        public ReportDoc H2(string t) { Blocks.Add(new Heading { Level = 2, Text = t }); return this; }
        public ReportDoc H3(string t) { Blocks.Add(new Heading { Level = 3, Text = t }); return this; }
        public ReportDoc P(string t) { Blocks.Add(new Para { Text = t }); return this; }
        /// <summary>灰色小字的说明（适用条件、注意事项）。</summary>
        public ReportDoc Note(string t) { Blocks.Add(new Para { Text = t, Note = true }); return this; }
        public ReportDoc List(IEnumerable<string> items, bool numbered = false) { Blocks.Add(new Bullets { Items = items.ToList(), Numbered = numbered }); return this; }
        /// <summary>表格；csvName 不为空时，导出时另存一份同名 CSV（论文附原始数据用）。</summary>
        public ReportDoc Table(string caption, string[] header, IEnumerable<string[]> rows, string csvName = null) { Blocks.Add(new TableBlock { Caption = caption, Header = header, Rows = rows.ToList(), CsvName = csvName }); return this; }
        public ReportDoc Img(string path, string caption, double widthCm = 14) { Blocks.Add(new Image { Path = path, Caption = caption, WidthCm = widthCm }); return this; }
        public ReportDoc KV(IEnumerable<(string, string)> items) { Blocks.Add(new KeyValues { Items = items.ToList() }); return this; }
        /// <summary>登记一条参考文献，返回它的编号（同一条只登记一次）。</summary>
        public int Ref(string citation) { int i = Refs.IndexOf(citation); if (i < 0) { Refs.Add(citation); i = Refs.Count - 1; } return i + 1; }

        // ---------------- 常用参考文献（GB/T 7714 格式）----------------
        public static class Cite
        {
            public const string Gmsh = "Geuzaine C, Remacle J F. Gmsh: A 3-D finite element mesh generator with built-in pre- and post-processing facilities[J]. International Journal for Numerical Methods in Engineering, 2009, 79(11): 1309-1331.";
            public const string ZZ = "Zienkiewicz O C, Zhu J Z. A simple error estimator and adaptive procedure for practical engineering analysis[J]. International Journal for Numerical Methods in Engineering, 1987, 24(2): 337-357.";
            public const string Spr = "Zienkiewicz O C, Taylor R L, Zhu J Z. The Finite Element Method: Its Basis and Fundamentals[M]. 7th ed. Oxford: Butterworth-Heinemann, 2013.";
            public const string Simp = "Bendsøe M P. Optimal shape design as a material distribution problem[J]. Structural Optimization, 1989, 1(4): 193-202.";
            public const string Top99 = "Sigmund O. A 99 line topology optimization code written in Matlab[J]. Structural and Multidisciplinary Optimization, 2001, 21(2): 120-127.";
            public const string Top88 = "Andreassen E, Clausen A, Schevenels M, et al. Efficient topology optimization in MATLAB using 88 lines of code[J]. Structural and Multidisciplinary Optimization, 2011, 43(1): 1-16.";
            public const string DensityFilter = "Bruns T E, Tortorelli D A. Topology optimization of non-linear elastic structures and compliant mechanisms[J]. Computer Methods in Applied Mechanics and Engineering, 2001, 190(26-27): 3443-3459.";
            public const string Heaviside = "Wang F, Lazarov B S, Sigmund O. On projection methods, convergence and robust formulations in topology optimization[J]. Structural and Multidisciplinary Optimization, 2011, 43(6): 767-784.";
            public const string SaAmg = "Vaněk P, Mandel J, Brezina M. Algebraic multigrid by smoothed aggregation for second and fourth order elliptic problems[J]. Computing, 1996, 56(3): 179-196.";
            public const string Hxt = "Marot C, Pellerin J, Remacle J F. One machine, one minute, three billion tetrahedra[J]. International Journal for Numerical Methods in Engineering, 2019, 117(9): 967-990.";
        }

        public static string N(double v, string fmt = "0.###") => v.ToString(fmt, CultureInfo.InvariantCulture);
    }

    /// <summary>Markdown 输出（图片用相对路径；表格用管道表）。</summary>
    public static class MarkdownWriter
    {
        public static void Save(ReportDoc d, string path)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(d.Title).Append("\n\n");
            if (d.Subtitle != null) sb.Append(d.Subtitle).Append("\n\n");
            sb.Append($"{d.Author}{(d.Author != null ? " · " : "")}{d.Date:yyyy-MM-dd}\n\n");
            int fig = 0, tab = 0;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            foreach (var b in d.Blocks)
            {
                switch (b)
                {
                    case ReportDoc.Heading h: sb.Append(new string('#', h.Level + 1)).Append(' ').Append(h.Text).Append("\n\n"); break;
                    case ReportDoc.Para p: sb.Append(p.Note ? "> " : "").Append(p.Text).Append("\n\n"); break;
                    case ReportDoc.Bullets l: for (int i = 0; i < l.Items.Count; i++) sb.Append(l.Numbered ? $"{i + 1}. " : "- ").Append(l.Items[i]).Append('\n'); sb.Append('\n'); break;
                    case ReportDoc.KeyValues kv: foreach (var (k, v) in kv.Items) sb.Append($"- **{k}**：{v}\n"); sb.Append('\n'); break;
                    case ReportDoc.TableBlock t:
                        tab++;
                        sb.Append($"**表 {tab}　{t.Caption}**\n\n| ").Append(string.Join(" | ", t.Header)).Append(" |\n|").Append(string.Concat(t.Header.Select(_ => "---|"))).Append('\n');
                        foreach (var r in t.Rows) sb.Append("| ").Append(string.Join(" | ", r.Select(c => (c ?? "").Replace("|", "/")))).Append(" |\n");
                        sb.Append('\n');
                        break;
                    case ReportDoc.Image im:
                        fig++;
                        var rel = MakeRelative(dir, im.Path);
                        sb.Append($"![图 {fig}]({rel.Replace('\\', '/')})\n\n*图 {fig}　{im.Caption}*\n\n");
                        break;
                }
            }
            if (d.Refs.Count > 0)
            {
                sb.Append("## 参考文献\n\n");
                for (int i = 0; i < d.Refs.Count; i++) sb.Append($"[{i + 1}] {d.Refs[i]}\n\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string MakeRelative(string dir, string file)
        {
            try
            {
                var u1 = new Uri(dir.TrimEnd('\\') + "\\"); var u2 = new Uri(Path.GetFullPath(file));
                return Uri.UnescapeDataString(u1.MakeRelativeUri(u2).ToString());
            }
            catch { return file; }
        }
    }

    public static class CsvWriter
    {
        /// <summary>写 CSV（带 BOM，Excel 直接打开中文不乱码）。</summary>
        public static void Save(string path, string[] header, IEnumerable<string[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", header.Select(Esc))).Append("\r\n");
            foreach (var r in rows) sb.Append(string.Join(",", r.Select(Esc))).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static string Esc(string s) => s == null ? "" : s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
