using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SwKit.App
{
    /// <summary>
    /// 把 Markdown 渲染成 WPF 的 FlowDocument（软件里直接阅读提示词、说明文档）。
    /// 支持：标题、段落、有序 / 无序列表（可嵌套）、代码块（带「复制」按钮）、表格、引用、分隔线、行内粗体 / 斜体 / 代码 / 链接、YAML 头。
    /// </summary>
    static class Markdown
    {
        static Brush R(string key) => (Brush)Application.Current.FindResource(key);
        static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        static readonly Brush CodeBg = Ui.Brush("#F5F5F7"), CodeLine = Ui.Brush("#E6E6EB"), QuoteLine = Ui.Brush("#D2D2D7"), InlineCodeBg = Ui.Brush("#EFEFF2");

        public static FlowDocument Render(string md)
        {
            var doc = new FlowDocument
            {
                FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
                FontSize = 13.5, LineHeight = 22, PagePadding = new Thickness(28, 22, 28, 30),
                Foreground = R("B.Text"), TextAlignment = TextAlignment.Left, IsHyphenationEnabled = false,
            };
            var lines = (md ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;

            // YAML 头（Claude Skill 的 SKILL.md 开头的 --- name / description ---）
            if (lines.Length > 2 && lines[0].Trim() == "---")
            {
                int end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
                if (end > 0)
                {
                    doc.Blocks.Add(CodeBlock(string.Join("\n", lines.Skip(1).Take(end - 1)), "Skill 信息"));
                    i = end + 1;
                }
            }

            var para = new List<string>();
            void FlushPara()
            {
                if (para.Count == 0) return;
                // 中文文档常常一行一句（例如提示词里的【填空】模板），保留原来的换行，比按标准 Markdown 合并成一段好读
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                for (int k = 0; k < para.Count; k++)
                {
                    if (k > 0) p.Inlines.Add(new LineBreak());
                    AddInlines(p.Inlines, para[k].Trim());
                }
                doc.Blocks.Add(p);
                para.Clear();
            }

            while (i < lines.Length)
            {
                var line = lines[i];
                var t = line.Trim();
                if (t.Length == 0) { FlushPara(); i++; continue; }

                // 代码块
                if (t.StartsWith("```") || t.StartsWith("~~~"))
                {
                    FlushPara();
                    var fence = t.Substring(0, 3); var lang = t.Substring(3).Trim();
                    var sb = new StringBuilder();
                    i++;
                    while (i < lines.Length && !lines[i].Trim().StartsWith(fence)) { sb.Append(lines[i]).Append('\n'); i++; }
                    i++;
                    doc.Blocks.Add(CodeBlock(sb.ToString().TrimEnd('\n'), lang));
                    continue;
                }
                // 标题
                var h = Regex.Match(t, @"^(#{1,6})\s+(.*)$");
                if (h.Success)
                {
                    FlushPara();
                    int level = h.Groups[1].Length;
                    var p = new Paragraph
                    {
                        FontSize = level == 1 ? 22 : level == 2 ? 18 : level == 3 ? 15.5 : 14,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, level <= 2 ? 18 : 12, 0, level <= 2 ? 8 : 6),
                        LineHeight = level <= 2 ? 28 : 22,
                    };
                    if (level == 1) p.FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont");
                    AddInlines(p.Inlines, h.Groups[2].Value.TrimEnd('#', ' '));
                    doc.Blocks.Add(p);
                    i++; continue;
                }
                // 分隔线
                if (Regex.IsMatch(t, @"^(\*\s*){3,}$|^(-\s*){3,}$|^(_\s*){3,}$"))
                {
                    FlushPara();
                    doc.Blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = R("B.Line"), Margin = new Thickness(0, 8, 0, 8) }));
                    i++; continue;
                }
                // 表格
                if (t.StartsWith("|") && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}"))
                {
                    FlushPara();
                    var rows = new List<string>();
                    while (i < lines.Length && lines[i].Trim().StartsWith("|")) { rows.Add(lines[i].Trim()); i++; }
                    doc.Blocks.Add(Table(rows));
                    continue;
                }
                // 引用
                if (t.StartsWith(">"))
                {
                    FlushPara();
                    var q = new List<string>();
                    while (i < lines.Length && lines[i].Trim().StartsWith(">")) { q.Add(lines[i].Trim().TrimStart('>').Trim()); i++; }
                    var sec = new Section { BorderBrush = QuoteLine, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Margin = new Thickness(0, 0, 0, 10), Foreground = R("B.Text2") };
                    var p = new Paragraph { Margin = new Thickness(0) };
                    AddInlines(p.Inlines, string.Join(" ", q));
                    sec.Blocks.Add(p);
                    doc.Blocks.Add(sec);
                    continue;
                }
                // 列表
                if (IsListItem(line, out _, out _, out _))
                {
                    FlushPara();
                    doc.Blocks.Add(ParseList(lines, ref i, Indent(line)));
                    continue;
                }
                para.Add(line);
                i++;
            }
            FlushPara();
            return doc;
        }

        static int Indent(string line) { int n = 0; foreach (var c in line) { if (c == ' ') n++; else if (c == '\t') n += 4; else break; } return n; }

        static bool IsListItem(string line, out bool ordered, out string text, out int indent)
        {
            indent = Indent(line);
            var t = line.Trim();
            var m = Regex.Match(t, @"^([-*+])\s+(.*)$");
            if (m.Success) { ordered = false; text = m.Groups[2].Value; return true; }
            m = Regex.Match(t, @"^(\d+)[.)]\s+(.*)$");
            if (m.Success) { ordered = true; text = m.Groups[2].Value; return true; }
            ordered = false; text = null; return false;
        }

        static List ParseList(string[] lines, ref int i, int baseIndent)
        {
            IsListItem(lines[i], out var ordered, out _, out _);
            var list = new List { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(22, 0, 0, 0) };
            ListItem current = null;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (line.Trim().Length == 0)
                {
                    // 空行后面如果还是同级列表项就继续
                    if (i + 1 < lines.Length && IsListItem(lines[i + 1], out _, out _, out var ni) && ni >= baseIndent) { i++; continue; }
                    break;
                }
                if (IsListItem(line, out var ord, out var text, out var ind))
                {
                    if (ind < baseIndent) break;
                    if (ind > baseIndent + 1 && current != null) { current.Blocks.Add(ParseList(lines, ref i, ind)); continue; }
                    if (ord != ordered && current != null) break;
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
                    // 任务列表 [ ] / [x]
                    var tm = Regex.Match(text, @"^\[([ xX])\]\s+(.*)$");
                    if (tm.Success) { p.Inlines.Add(new Run(tm.Groups[1].Value == " " ? "☐ " : "☑ ")); text = tm.Groups[2].Value; }
                    AddInlines(p.Inlines, text);
                    current = new ListItem(p);
                    list.ListItems.Add(current);
                    i++;
                }
                else if (current != null && Indent(line) > baseIndent && !line.Trim().StartsWith("```"))
                {
                    // 续行
                    var last = current.Blocks.LastBlock as Paragraph;
                    if (last != null) { last.Inlines.Add(new Run(" ")); AddInlines(last.Inlines, line.Trim()); }
                    i++;
                }
                else break;
            }
            return list;
        }

        static Block Table(List<string> rows)
        {
            string[] Cells(string r) => r.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            var header = Cells(rows[0]);
            var body = rows.Skip(2).Select(Cells).ToList();
            int n = Math.Max(header.Length, body.Count == 0 ? 0 : body.Max(b => b.Length));
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 12), BorderBrush = CodeLine, BorderThickness = new Thickness(1, 1, 0, 0), FontSize = 12.5 };
            for (int c = 0; c < n; c++) table.Columns.Add(new TableColumn());
            var group = new TableRowGroup();
            void AddRow(string[] cells, bool head)
            {
                var row = new TableRow { Background = head ? CodeBg : Brushes.White };
                for (int c = 0; c < n; c++)
                {
                    var p = new Paragraph { Margin = new Thickness(0), LineHeight = 19 };
                    if (head) p.FontWeight = FontWeights.SemiBold;
                    AddInlines(p.Inlines, c < cells.Length ? cells[c] : "");
                    row.Cells.Add(new TableCell(p) { Padding = new Thickness(8, 5, 8, 5), BorderBrush = CodeLine, BorderThickness = new Thickness(0, 0, 1, 1) });
                }
                group.Rows.Add(row);
            }
            AddRow(header, true);
            foreach (var b in body) AddRow(b, false);
            table.RowGroups.Add(group);
            return table;
        }

        /// <summary>代码块：等宽字体、浅灰底、右上角「复制」。</summary>
        public static Block CodeBlock(string code, string title)
        {
            var grid = new Grid();
            var text = new TextBox
            {
                Text = code, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                FontFamily = Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0, string.IsNullOrEmpty(title) ? 0 : 18, 64, 0),
                Foreground = R("B.Text"),
            };
            grid.Children.Add(text);
            if (!string.IsNullOrEmpty(title))
                grid.Children.Add(new TextBlock { Text = title, FontSize = 11, Foreground = R("B.Text3"), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false });
            var copy = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "复制", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -6, 0) };
            copy.Click += (s, e) => { if (Clip.SetText(code)) Flash(copy, "已复制"); };
            grid.Children.Add(copy);
            var border = new Border { Background = CodeBg, BorderBrush = CodeLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Child = grid };
            return new BlockUIContainer(border) { Margin = new Thickness(0, 2, 0, 12) };
        }

        public static void Flash(Button b, string text)
        {
            var old = b.Content;
            b.Content = text;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            t.Tick += (s, e) => { t.Stop(); b.Content = old; };
            t.Start();
        }

        // ---------------- 行内 ----------------

        static readonly Regex InlineRx = new Regex(@"(`[^`]+`)|(\*\*[^*]+\*\*)|(__[^_]+__)|(\[[^\]]+\]\([^)]+\))|((?<![\w*])\*[^*\s][^*]*\*(?!\w))", RegexOptions.Compiled);

        static void AddInlines(InlineCollection target, string text)
        {
            int pos = 0;
            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > pos) target.Add(new Run(text.Substring(pos, m.Index - pos)));
                var v = m.Value;
                if (v.StartsWith("`"))
                    target.Add(new Run(v.Trim('`')) { FontFamily = Mono, FontSize = 12.5, Background = InlineCodeBg });
                else if (v.StartsWith("**") || v.StartsWith("__"))
                {
                    var b = new Bold();
                    AddInlines(b.Inlines, v.Substring(2, v.Length - 4));
                    target.Add(b);
                }
                else if (v.StartsWith("["))
                {
                    var lm = Regex.Match(v, @"^\[([^\]]+)\]\(([^)]+)\)$");
                    target.Add(new Run(lm.Groups[1].Value) { Foreground = R("B.Accent"), ToolTip = lm.Groups[2].Value });
                }
                else target.Add(new Italic(new Run(v.Trim('*'))));
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) target.Add(new Run(text.Substring(pos)));
        }
    }

    /// <summary>剪贴板：其他程序占用剪贴板时重试几次，不抛异常。</summary>
    static class Clip
    {
        public static bool SetText(string s)
        {
            for (int k = 0; k < 5; k++)
            {
                try { Clipboard.SetDataObject(s ?? "", true); return true; }
                catch { System.Threading.Thread.Sleep(60); }
            }
            return false;
        }

        /// <summary>把文件放到剪贴板（之后可以在网页上传框、聊天窗口、资源管理器里 Ctrl+V 粘贴）。</summary>
        public static bool SetFiles(IEnumerable<string> files)
        {
            var sc = new System.Collections.Specialized.StringCollection();
            foreach (var f in files) sc.Add(f);
            for (int k = 0; k < 5; k++)
            {
                try { Clipboard.SetFileDropList(sc); return true; }
                catch { System.Threading.Thread.Sleep(60); }
            }
            return false;
        }
    }
}
