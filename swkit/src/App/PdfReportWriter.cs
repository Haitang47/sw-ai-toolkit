using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SwKit.Core.Report;

namespace SwKit.App
{
    /// <summary>
    /// 报告导出成 PDF：用 WPF 的 FlowDocument 按 A4 排版（标题、段落、列表、表格、图、参考文献、页码），
    /// 每页渲染成 JPEG，写进一个最简 PDF（每页一张图）。不依赖 Word、打印机或第三方库，任何电脑都能生成；
    /// 文字不能选中复制——需要编辑时用同一文件夹里的 Word / Markdown。
    /// </summary>
    static class PdfReportWriter
    {
        const double PageW = 793.7, PageH = 1122.5;     // A4，WPF 单位（1/96 英寸）
        const double Margin = 72;                       // 约 1.9 cm
        const double Dpi = 160;                         // 渲染分辨率：打印清楚、文件不太大（每页约 200–400 KB）
        static readonly FontFamily Font = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, SimSun");

        /// <summary>在独立的 STA 线程里排版、渲染（WPF 要求），从任何线程调用都可以。</summary>
        public static void Save(ReportDoc d, string path)
        {
            Exception err = null;
            var th = new Thread(() => { try { SaveSta(d, path); } catch (Exception ex) { err = ex; } });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            th.Join();
            if (err != null) throw new InvalidOperationException("生成 PDF 失败：" + err.Message, err);
        }

        static void SaveSta(ReportDoc d, string path)
        {
            var doc = Build(d);
            var pag = ((IDocumentPaginatorSource)doc).DocumentPaginator;
            pag.PageSize = new Size(PageW, PageH);
            pag.ComputePageCount();
            int n = pag.PageCount;
            var pages = new List<(byte[] jpg, int w, int h)>();
            for (int i = 0; i < n; i++)
            {
                var page = pag.GetPage(i);
                var bg = new DrawingVisual();
                using (var dc = bg.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageW, PageH));
                var hf = new DrawingVisual();
                using (var dc = hf.RenderOpen())
                {
                    // 页眉（标题，首页不要）、页脚（页码）
                    var head = Text(d.Title, 9, Gray);
                    if (i > 0) dc.DrawText(head, new Point(Margin, Margin / 2 - head.Height / 2));
                    var foot = Text($"{i + 1} / {n}", 9, Gray);
                    dc.DrawText(foot, new Point(PageW / 2 - foot.Width / 2, PageH - Margin / 2 - foot.Height / 2));
                }
                int w = (int)Math.Round(PageW * Dpi / 96), h = (int)Math.Round(PageH * Dpi / 96);
                var rtb = new RenderTargetBitmap(w, h, Dpi, Dpi, PixelFormats.Pbgra32);
                rtb.Render(bg);            // 白底
                rtb.Render(page.Visual);   // 正文（Render 是叠加的）
                rtb.Render(hf);            // 页眉页脚在最上面（正文页面自带白底，会盖住先画的东西）
                var enc = new JpegBitmapEncoder { QualityLevel = 88 };
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var ms = new MemoryStream()) { enc.Save(ms); pages.Add((ms.ToArray(), w, h)); }
            }
            WritePdf(path, pages, d.Title);
        }

        // 冻结：每次导出在新的 STA 线程里排版，没冻结的画刷只能在创建它的线程用
        static readonly Brush Gray = Frozen(0x6E, 0x6E, 0x73), Line = Frozen(0xC8, 0xC8, 0xCC), HeadBg = Frozen(0xF2, 0xF2, 0xF5);
        static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        /// <summary>段落文字：**加粗** 标记换成粗体。</summary>
        static Paragraph Rich(string text)
        {
            var p = new Paragraph();
            var parts = (text ?? "").Split(new[] { "**" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0) p.Inlines.Add(i % 2 == 1 ? (Inline)new Bold(new Run(parts[i])) : new Run(parts[i]));
            return p;
        }

        static FormattedText Text(string s, double pt, Brush b) =>
            new FormattedText(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), pt * 96 / 72, b, 1.0);

        static double Pt(double pt) => pt * 96 / 72;

        static FlowDocument Build(ReportDoc d)
        {
            var doc = new FlowDocument
            {
                PageWidth = PageW, PageHeight = PageH, PagePadding = new Thickness(Margin), ColumnWidth = PageW, ColumnGap = 0,
                FontFamily = Font, FontSize = Pt(10.5), Foreground = Brushes.Black, Background = Brushes.White, TextAlignment = TextAlignment.Left, LineHeight = Pt(17),
            };
            doc.Blocks.Add(new Paragraph(new Run(d.Title)) { FontSize = Pt(20), FontWeight = FontWeights.Bold, Margin = new Thickness(0, Pt(40), 0, Pt(6)), LineHeight = Pt(28) });
            if (!string.IsNullOrEmpty(d.Subtitle)) doc.Blocks.Add(new Paragraph(new Run(d.Subtitle)) { FontSize = Pt(12), Foreground = Gray, Margin = new Thickness(0, 0, 0, Pt(4)) });
            doc.Blocks.Add(new Paragraph(new Run($"{d.Author}{(d.Author != null ? " · " : "")}{d.Date:yyyy-MM-dd}")) { FontSize = Pt(10), Foreground = Gray, Margin = new Thickness(0, 0, 0, Pt(18)) });
            int fig = 0, tab = 0;
            foreach (var b in d.Blocks)
            {
                switch (b)
                {
                    case ReportDoc.Heading h:
                        doc.Blocks.Add(new Paragraph(new Run(h.Text))
                        {
                            FontSize = Pt(h.Level == 1 ? 15 : h.Level == 2 ? 12.5 : 11), FontWeight = FontWeights.Bold,
                            Margin = new Thickness(0, Pt(h.Level == 1 ? 16 : 10), 0, Pt(6)), KeepWithNext = true, LineHeight = Pt(h.Level == 1 ? 22 : 18),
                        });
                        break;
                    case ReportDoc.Para p:
                        {
                            var para = Rich(p.Text);
                            para.FontSize = Pt(p.Note ? 9 : 10.5); para.Foreground = p.Note ? Gray : Brushes.Black; para.Margin = new Thickness(0, 0, 0, Pt(6)); para.LineHeight = Pt(p.Note ? 14 : 17);
                            doc.Blocks.Add(para);
                            break;
                        }
                    case ReportDoc.Bullets l:
                        {
                            var list = new List { MarkerStyle = l.Numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(Pt(4), 0, 0, Pt(6)), Padding = new Thickness(Pt(14), 0, 0, 0) };
                            foreach (var it in l.Items) { var ip = Rich(it); ip.Margin = new Thickness(0, 0, 0, Pt(2)); list.ListItems.Add(new ListItem(ip)); }
                            doc.Blocks.Add(list);
                            break;
                        }
                    case ReportDoc.KeyValues kv:
                        foreach (var (k, v) in kv.Items)
                        {
                            var para = new Paragraph { Margin = new Thickness(0, 0, 0, Pt(2)) };
                            para.Inlines.Add(new Run(k + "：") { FontWeight = FontWeights.SemiBold });
                            para.Inlines.Add(new Run(v));
                            doc.Blocks.Add(para);
                        }
                        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, Pt(4)) });
                        break;
                    case ReportDoc.TableBlock t:
                        {
                            tab++;
                            doc.Blocks.Add(new Paragraph(new Run($"表 {tab}　{t.Caption}")) { FontSize = Pt(9.5), FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, Pt(8), 0, Pt(4)), KeepWithNext = true });
                            int nc = t.Header.Length;
                            var table = new Table { CellSpacing = 0, FontSize = Pt(9), BorderBrush = Line, BorderThickness = new Thickness(0.8), Margin = new Thickness(0, 0, 0, Pt(10)) };
                            // 列宽按内容长度分配（中文字符算 2）
                            double Len(string s) => (s ?? "").Sum(c => c > 255 ? 2.0 : 1.0);
                            var widths = Enumerable.Range(0, nc).Select(c => Math.Min(40, Math.Max(4, Math.Max(Len(t.Header[c]), t.Rows.Take(200).Select(r => c < r.Length ? Len(r[c]) : 0).DefaultIfEmpty(0).Max())))).ToArray();
                            foreach (var w in widths) table.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
                            var group = new TableRowGroup();
                            var hr = new TableRow { Background = HeadBg, FontWeight = FontWeights.SemiBold };
                            foreach (var hdr in t.Header) hr.Cells.Add(Cell(hdr));
                            group.Rows.Add(hr);
                            foreach (var r in t.Rows)
                            {
                                var row = new TableRow();
                                for (int c = 0; c < nc; c++) row.Cells.Add(Cell(c < r.Length ? r[c] : ""));
                                group.Rows.Add(row);
                            }
                            table.RowGroups.Add(group);
                            doc.Blocks.Add(table);
                            break;
                        }
                    case ReportDoc.Image im:
                        {
                            fig++;
                            if (!File.Exists(im.Path)) break;
                            var bmp = new BitmapImage();
                            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(Path.GetFullPath(im.Path)); bmp.EndInit(); bmp.Freeze();
                            double wmax = PageW - 2 * Margin;
                            double w = Math.Min(wmax, im.WidthCm / 2.54 * 96);
                            double h = w * bmp.PixelHeight / Math.Max(1, bmp.PixelWidth);
                            double hmax = PageH - 2 * Margin - 60;
                            if (h > hmax) { w *= hmax / h; h = hmax; }
                            doc.Blocks.Add(new BlockUIContainer(new Image { Source = bmp, Width = w, Height = h, Stretch = Stretch.Uniform }) { Margin = new Thickness(0, Pt(6), 0, Pt(2)) });
                            doc.Blocks.Add(new Paragraph(new Run($"图 {fig}　{im.Caption}")) { FontSize = Pt(9.5), TextAlignment = TextAlignment.Center, Foreground = Gray, Margin = new Thickness(0, 0, 0, Pt(10)) });
                            break;
                        }
                }
            }
            if (d.Refs.Count > 0)
            {
                doc.Blocks.Add(new Paragraph(new Run("参考文献")) { FontSize = Pt(15), FontWeight = FontWeights.Bold, Margin = new Thickness(0, Pt(16), 0, Pt(6)), KeepWithNext = true });
                for (int i = 0; i < d.Refs.Count; i++)
                    doc.Blocks.Add(new Paragraph(new Run($"[{i + 1}] {d.Refs[i]}")) { FontSize = Pt(9), Margin = new Thickness(0, 0, 0, Pt(3)), LineHeight = Pt(14) });
            }
            return doc;
        }

        static TableCell Cell(string s) => new TableCell(new Paragraph(new Run(s ?? "")) { Margin = new Thickness(0), LineHeight = Pt(13) })
        { BorderBrush = Line, BorderThickness = new Thickness(0.5), Padding = new Thickness(Pt(4), Pt(2), Pt(4), Pt(2)) };

        // ---------------- 最简 PDF：每页一张 JPEG ----------------

        static void WritePdf(string path, List<(byte[] jpg, int w, int h)> pages, string title)
        {
            const double W = 595.28, H = 841.89;    // A4，pt
            var offsets = new List<long>();
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                void Raw(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }
                void Begin(int id) { while (offsets.Count < id) offsets.Add(0); offsets[id - 1] = fs.Position; Raw($"{id} 0 obj\n"); }
                Raw("%PDF-1.4\n");
                fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);   // 二进制注释：告诉传输工具这是二进制文件
                int n = pages.Count;
                // 对象编号：1 目录，2 页树，3 信息；第 i 页：页 4+3i、内容 5+3i、图 6+3i
                Begin(1); Raw("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                Begin(2); Raw("<< /Type /Pages /Count " + n + " /Kids [" + string.Join(" ", Enumerable.Range(0, n).Select(i => $"{4 + 3 * i} 0 R")) + "] >>\nendobj\n");
                Begin(3); Raw("<< /Title " + PdfText(title) + " /Producer (SwKit) /CreationDate (D:" + DateTime.Now.ToString("yyyyMMddHHmmss") + ") >>\nendobj\n");
                string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
                for (int i = 0; i < n; i++)
                {
                    int pg = 4 + 3 * i, ct = 5 + 3 * i, im = 6 + 3 * i;
                    Begin(pg); Raw($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(W)} {F(H)}] /Resources << /XObject << /Im0 {im} 0 R >> >> /Contents {ct} 0 R >>\nendobj\n");
                    var content = $"q {F(W)} 0 0 {F(H)} 0 0 cm /Im0 Do Q\n";
                    Begin(ct); Raw($"<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
                    var (jpg, w, h) = pages[i];
                    Begin(im); Raw($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpg.Length} >>\nstream\n");
                    fs.Write(jpg, 0, jpg.Length);
                    Raw("\nendstream\nendobj\n");
                }
                long xref = fs.Position;
                Raw($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
                foreach (var o in offsets) Raw(o.ToString("D10") + " 00000 n \n");
                Raw($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            }
        }

        /// <summary>PDF 文本字符串：UTF-16BE（带 BOM）的十六进制形式，中文标题也能正确显示。</summary>
        static string PdfText(string s)
        {
            var b = Encoding.BigEndianUnicode.GetBytes(s ?? "");
            return "<FEFF" + string.Concat(b.Select(x => x.ToString("X2"))) + ">";
        }
    }
}
