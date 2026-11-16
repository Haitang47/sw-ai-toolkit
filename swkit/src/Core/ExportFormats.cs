using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwKit.Core
{
    public enum DocKind { Unknown = 0, Part = 1, Assembly = 2, Drawing = 3 }

    public sealed class ExportFormat
    {
        public string Key { get; }
        public string Label { get; }
        public string Extension { get; }
        public bool ForPart { get; }
        public bool ForAssembly { get; }
        public bool ForDrawing { get; }
        /// <summary>只对钣金零件有效（展开图）。</summary>
        public bool SheetMetalOnly { get; }
        public string Hint { get; }

        public ExportFormat(string key, string label, string ext, bool part, bool asm, bool drw, string hint, bool sheetMetalOnly = false)
        {
            Key = key; Label = label; Extension = ext;
            ForPart = part; ForAssembly = asm; ForDrawing = drw;
            Hint = hint; SheetMetalOnly = sheetMetalOnly;
        }

        public bool AppliesTo(DocKind kind) =>
            (kind == DocKind.Part && ForPart) || (kind == DocKind.Assembly && ForAssembly) || (kind == DocKind.Drawing && ForDrawing);

        public override string ToString() => Label;
    }

    public static class ExportFormats
    {
        public static readonly ExportFormat Step = new ExportFormat("step", "STEP", ".step", true, true, false, "通用三维交换格式，几乎所有 CAD 都能打开");
        public static readonly ExportFormat Stl = new ExportFormat("stl", "STL", ".stl", true, true, false, "三角网格，3D 打印、URDF 网格常用");
        public static readonly ExportFormat ThreeMf = new ExportFormat("3mf", "3MF", ".3mf", true, true, false, "带颜色和单位的网格，新版切片软件推荐");
        public static readonly ExportFormat Iges = new ExportFormat("iges", "IGES", ".igs", true, true, false, "老式曲面交换格式");
        public static readonly ExportFormat Parasolid = new ExportFormat("x_t", "Parasolid", ".x_t", true, true, false, "SolidWorks 内核格式，和 NX、Solid Edge 等交换时几乎无损");
        public static readonly ExportFormat FlatDxf = new ExportFormat("dxf_flat", "钣金展开 DXF", ".dxf", true, false, false, "钣金件的展开下料图，非钣金零件会自动跳过", sheetMetalOnly: true);
        public static readonly ExportFormat Pdf = new ExportFormat("pdf", "PDF", ".pdf", false, false, true, "工程图全部图纸导出为一个 PDF");
        public static readonly ExportFormat Dwg = new ExportFormat("dwg", "DWG", ".dwg", false, false, true, "工程图导出给 AutoCAD");
        public static readonly ExportFormat Dxf = new ExportFormat("dxf", "DXF", ".dxf", false, false, true, "工程图导出为 DXF");

        public static IReadOnlyList<ExportFormat> All { get; } = new[] { Step, Stl, ThreeMf, Iges, Parasolid, FlatDxf, Pdf, Dwg, Dxf };

        public static ExportFormat ByKey(string key) =>
            All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

        public static DocKind KindFromPath(string path)
        {
            switch (Path.GetExtension(path)?.ToLowerInvariant())
            {
                case ".sldprt": return DocKind.Part;
                case ".sldasm": return DocKind.Assembly;
                case ".slddrw": return DocKind.Drawing;
                default: return DocKind.Unknown;
            }
        }

        public static string KindText(DocKind kind)
        {
            switch (kind)
            {
                case DocKind.Part: return "零件";
                case DocKind.Assembly: return "装配体";
                case DocKind.Drawing: return "工程图";
                default: return "不支持";
            }
        }

        /// <summary>展开文件和文件夹，返回所有 SolidWorks 文件（跳过 ~$ 开头的临时文件），去重并保持顺序。</summary>
        public static List<string> ExpandInputs(IEnumerable<string> inputs, bool recurse)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in inputs)
            {
                var p = raw.Trim().Trim('"');
                if (p.Length == 0) continue;
                IEnumerable<string> files;
                if (Directory.Exists(p))
                {
                    var opt = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    files = Directory.EnumerateFiles(p, "*.*", opt).Where(f => KindFromPath(f) != DocKind.Unknown).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                }
                else if (File.Exists(p)) files = new[] { p };
                else continue;

                foreach (var f in files)
                {
                    var full = Path.GetFullPath(f);
                    if (Path.GetFileName(full).StartsWith("~$")) continue;
                    if (KindFromPath(full) == DocKind.Unknown) continue;
                    if (seen.Add(full)) result.Add(full);
                }
            }
            return result;
        }
    }
}
