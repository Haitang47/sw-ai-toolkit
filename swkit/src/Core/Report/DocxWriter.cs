using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace SwKit.Core.Report
{
    /// <summary>
    /// 不依赖任何第三方库，直接按 Office Open XML 写 Word 文档（.docx）。
    /// 排版按中文科技论文习惯：正文宋体 + Times New Roman 五号、1.25 倍行距、首行缩进 2 字；标题黑体；
    /// 图题在图下、表题在表上，自动编号；三线表；参考文献按 GB/T 7714。
    /// 同时把带 CsvName 的表格另存为 CSV（放在 docx 旁边的「数据」文件夹）。
    /// </summary>
    public static class DocxWriter
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        public static void Save(ReportDoc d, string path)
        {
            var images = new List<(string id, string target, byte[] data)>();
            var body = new StringBuilder();
            int fig = 0, tab = 0, docPr = 1;
            var csvDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "数据");

            // 标题区
            body.Append(Par(d.Title, "Title"));
            if (d.Subtitle != null) body.Append(Par(d.Subtitle, "Subtitle"));
            body.Append(Par($"{(d.Author != null ? d.Author + "　" : "")}{d.Date:yyyy 年 M 月 d 日}", "Subtitle"));

            foreach (var b in d.Blocks)
            {
                switch (b)
                {
                    case ReportDoc.Heading h:
                        body.Append(Par(h.Text, "Heading" + Math.Min(3, Math.Max(1, h.Level))));
                        break;
                    case ReportDoc.Para p:
                        body.Append(Par(p.Text, p.Note ? "Note" : "BodyText"));
                        break;
                    case ReportDoc.Bullets l:
                        for (int i = 0; i < l.Items.Count; i++)
                            body.Append(Par((l.Numbered ? $"{i + 1}. " : "• ") + l.Items[i], "ListText"));
                        break;
                    case ReportDoc.KeyValues kv:
                        body.Append(Table(new[] { "项目", "内容" }, kv.Items.Select(x => new[] { x.key, x.value }).ToList(), new[] { 30, 70 }));
                        body.Append(Par("", "BodyText"));
                        break;
                    case ReportDoc.TableBlock t:
                        tab++;
                        body.Append(Par($"表 {tab}　{t.Caption}", "TableCaption"));
                        body.Append(Table(t.Header, t.Rows, null));
                        if (t.CsvName != null)
                        {
                            Directory.CreateDirectory(csvDir);
                            CsvWriter.Save(Path.Combine(csvDir, t.CsvName.EndsWith(".csv") ? t.CsvName : t.CsvName + ".csv"), t.Header, t.Rows);
                            body.Append(Par($"（原始数据：数据\\{(t.CsvName.EndsWith(".csv") ? t.CsvName : t.CsvName + ".csv")}）", "Note"));
                        }
                        else body.Append(Par("", "BodyText"));
                        break;
                    case ReportDoc.Image im:
                        if (!File.Exists(im.Path)) break;
                        fig++;
                        var data = File.ReadAllBytes(im.Path);
                        var (pw, ph) = PngSize(data);
                        long cx = (long)(im.WidthCm * 360000), cy = pw > 0 ? (long)(cx * (double)ph / pw) : cx * 3 / 4;
                        string rid = "rIdImg" + fig, target = $"media/image{fig}.png";
                        images.Add((rid, target, data));
                        body.Append($"<w:p><w:pPr><w:pStyle w:val=\"Figure\"/></w:pPr><w:r>{Drawing(rid, cx, cy, docPr++, "image" + fig)}</w:r></w:p>");
                        body.Append(Par($"图 {fig}　{im.Caption}", "FigureCaption"));
                        break;
                }
            }
            if (d.Refs.Count > 0)
            {
                body.Append(Par("参考文献", "Heading1"));
                for (int i = 0; i < d.Refs.Count; i++) body.Append(Par($"[{i + 1}] {d.Refs[i]}", "Reference"));
            }
            // A4，页边距 2.5 cm
            body.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1417\" w:right=\"1417\" w:bottom=\"1417\" w:left=\"1417\" w:header=\"851\" w:footer=\"992\" w:gutter=\"0\"/></w:sectPr>");

            var document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                $"<w:document xmlns:w=\"{W}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" + body + "</w:body></w:document>";

            var docRels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            docRels.Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            foreach (var (id, target, _) in images) docRels.Append($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"{target}\"/>");
            docRels.Append("</Relationships>");

            var tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                Entry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
                    "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                    "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                    "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>");
                Entry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
                Entry(zip, "docProps/core.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
                    "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                    $"<dc:title>{X(d.Title)}</dc:title><dc:creator>{X(d.Author ?? "SW 工具箱")}</dc:creator>" +
                    $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{d.Date.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}</dcterms:created></cp:coreProperties>");
                Entry(zip, "word/document.xml", document);
                Entry(zip, "word/styles.xml", Styles());
                Entry(zip, "word/_rels/document.xml.rels", docRels.ToString());
                foreach (var (_, target, data) in images)
                {
                    var e = zip.CreateEntry("word/" + target, CompressionLevel.NoCompression);
                    using (var s = e.Open()) s.Write(data, 0, data.Length);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static void Entry(ZipArchive zip, string name, string text)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(text);
        }

        static string X(string s) => SecurityElement.Escape(s ?? "");

        static string Par(string text, string style) =>
            $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>{Runs(text)}</w:p>";

        /// <summary>支持 **粗体** 的简单行内格式。</summary>
        static string Runs(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder();
            var parts = text.Split(new[] { "**" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                sb.Append("<w:r>");
                if (i % 2 == 1) sb.Append("<w:rPr><w:b/></w:rPr>");
                sb.Append($"<w:t xml:space=\"preserve\">{X(parts[i])}</w:t></w:r>");
            }
            return sb.ToString();
        }

        /// <summary>三线表：表头上下粗线、表尾细线，无竖线。widths：各列宽度百分比（null = 平均）。</summary>
        static string Table(string[] header, List<string[]> rows, int[] widths)
        {
            int n = header.Length;
            const int total = 9070;   // A4 去掉页边距的宽度（twip）
            var w = Enumerable.Range(0, n).Select(i => widths != null ? total * widths[i] / 100 : total / n).ToArray();
            var sb = new StringBuilder();
            sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:jc w:val=\"center\"/>");
            sb.Append("<w:tblBorders><w:top w:val=\"single\" w:sz=\"12\" w:color=\"000000\"/><w:bottom w:val=\"single\" w:sz=\"12\" w:color=\"000000\"/></w:tblBorders>");
            sb.Append("<w:tblCellMar><w:left w:w=\"80\" w:type=\"dxa\"/><w:right w:w=\"80\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>");
            foreach (var x in w) sb.Append($"<w:gridCol w:w=\"{x}\"/>");
            sb.Append("</w:tblGrid>");
            void Row(string[] cells, bool head)
            {
                sb.Append("<w:tr>");
                if (head) sb.Append("<w:trPr><w:tblHeader/></w:trPr>");
                for (int i = 0; i < n; i++)
                {
                    sb.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{w[i]}\" w:type=\"dxa\"/>");
                    if (head) sb.Append("<w:tcBorders><w:bottom w:val=\"single\" w:sz=\"6\" w:color=\"000000\"/></w:tcBorders>");
                    sb.Append("<w:vAlign w:val=\"center\"/></w:tcPr>");
                    var t = i < cells.Length ? cells[i] : "";
                    sb.Append($"<w:p><w:pPr><w:pStyle w:val=\"TableText\"/></w:pPr>{(head ? $"<w:r><w:rPr><w:b/></w:rPr><w:t xml:space=\"preserve\">{X(t)}</w:t></w:r>" : Runs(t))}</w:p></w:tc>");
                }
                sb.Append("</w:tr>");
            }
            Row(header, true);
            foreach (var r in rows) Row(r, false);
            sb.Append("</w:tbl>");
            return sb.ToString();
        }

        static string Drawing(string rid, long cx, long cy, int id, string name) =>
            $"<w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{id}\" name=\"{name}\"/>" +
            "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
            "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
            $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"{name}.png\"/><pic:cNvPicPr/></pic:nvPicPr>" +
            $"<pic:blipFill><a:blip r:embed=\"{rid}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
            $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
            "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>";

        static (int w, int h) PngSize(byte[] d)
        {
            if (d.Length < 24 || d[1] != 'P' || d[2] != 'N' || d[3] != 'G') return (0, 0);
            int Be(int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
            return (Be(16), Be(20));
        }

        static string Styles()
        {
            string Font(string east, string ascii) => $"<w:rFonts w:ascii=\"{ascii}\" w:hAnsi=\"{ascii}\" w:eastAsia=\"{east}\" w:cs=\"{ascii}\"/>";
            string Style(string id, string name, string ppr, string rpr, bool para = true) =>
                $"<w:style w:type=\"{(para ? "paragraph" : "character")}\" w:customStyle=\"1\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr>{ppr}</w:pPr><w:rPr>{rpr}</w:rPr></w:style>";
            var body = Font("宋体", "Times New Roman");
            var hei = Font("黑体", "Times New Roman");
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                $"<w:styles xmlns:w=\"{W}\">" +
                $"<w:docDefaults><w:rPrDefault><w:rPr>{body}<w:sz w:val=\"21\"/><w:szCs w:val=\"21\"/><w:lang w:val=\"en-US\" w:eastAsia=\"zh-CN\"/></w:rPr></w:rPrDefault>" +
                "<w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"300\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
                Style("Title", "Title", "<w:jc w:val=\"center\"/><w:spacing w:before=\"240\" w:after=\"120\"/>", hei + "<w:b/><w:sz w:val=\"36\"/><w:szCs w:val=\"36\"/>") +
                Style("Subtitle", "Subtitle", "<w:jc w:val=\"center\"/><w:spacing w:after=\"120\"/>", "<w:color w:val=\"555555\"/><w:sz w:val=\"21\"/>") +
                Style("Heading1", "heading 1", "<w:keepNext/><w:spacing w:before=\"360\" w:after=\"120\"/><w:outlineLvl w:val=\"0\"/>", hei + "<w:b/><w:sz w:val=\"30\"/><w:szCs w:val=\"30\"/>") +
                Style("Heading2", "heading 2", "<w:keepNext/><w:spacing w:before=\"240\" w:after=\"120\"/><w:outlineLvl w:val=\"1\"/>", hei + "<w:b/><w:sz w:val=\"26\"/><w:szCs w:val=\"26\"/>") +
                Style("Heading3", "heading 3", "<w:keepNext/><w:spacing w:before=\"180\" w:after=\"60\"/><w:outlineLvl w:val=\"2\"/>", hei + "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/>") +
                Style("BodyText", "Body Text", "<w:ind w:firstLineChars=\"200\" w:firstLine=\"420\"/><w:jc w:val=\"both\"/><w:spacing w:after=\"60\"/>", "") +
                Style("ListText", "List Text", "<w:ind w:left=\"420\" w:hanging=\"210\"/><w:spacing w:after=\"40\"/>", "") +
                Style("Note", "Note", "<w:spacing w:after=\"60\"/>", "<w:color w:val=\"666666\"/><w:sz w:val=\"18\"/><w:szCs w:val=\"18\"/>") +
                Style("Figure", "Figure", "<w:keepNext/><w:jc w:val=\"center\"/><w:spacing w:before=\"120\"/>", "") +
                Style("FigureCaption", "Figure Caption", "<w:jc w:val=\"center\"/><w:spacing w:after=\"200\"/>", hei + "<w:sz w:val=\"18\"/><w:szCs w:val=\"18\"/>") +
                Style("TableCaption", "Table Caption", "<w:keepNext/><w:jc w:val=\"center\"/><w:spacing w:before=\"120\" w:after=\"60\"/>", hei + "<w:sz w:val=\"18\"/><w:szCs w:val=\"18\"/>") +
                Style("TableText", "Table Text", "<w:jc w:val=\"center\"/><w:spacing w:before=\"20\" w:after=\"20\" w:line=\"240\" w:lineRule=\"auto\"/>", "<w:sz w:val=\"18\"/><w:szCs w:val=\"18\"/>") +
                Style("Reference", "Reference", "<w:ind w:left=\"420\" w:hanging=\"420\"/><w:jc w:val=\"both\"/><w:spacing w:after=\"40\"/>", "<w:sz w:val=\"18\"/><w:szCs w:val=\"18\"/>") +
                "</w:styles>";
        }
    }
}
