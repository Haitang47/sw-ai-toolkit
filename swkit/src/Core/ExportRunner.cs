using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorks.Interop.sldworks;

namespace SwKit.Core
{
    public enum LogLevel { Info, Success, Warning, Error }

    public sealed class ExportSummary
    {
        public int Done, Warning, Failed, Skipped;
        public TimeSpan Elapsed;
        public bool Cancelled;
        public List<string> Outputs = new List<string>();
        public int Total => Done + Warning + Failed + Skipped;
    }

    /// <summary>
    /// 批量导出引擎。必须在 STA 线程上调用 Run（命令行版的 Main 和界面版的后台线程都是 STA）。
    /// </summary>
    public sealed class ExportRunner
    {
        public event Action<LogLevel, string> Log;
        public event Action<int, int> Progress;

        // SolidWorks 用户设置编号（swUserPreferenceToggle_e / swUserPreferenceIntegerValue_e）
        const int TogStlBinary = 69, TogStlShowInfo = 70, TogStlDontTranslate = 71, TogStlOneFile = 72;
        const int TogStlPreview = 191, TogPdfViewOnSave = 617, Tog3mfShowInfo = 643;
        const int IntStepAP = 75, IntStlQuality = 78, IntStlUnits = 211;

        const int OpenSilent = 1, OpenReadOnly = 2;
        const int SaveAsSilent = 1;

        readonly HashSet<string> _usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Emit(LogLevel level, string text) => Log?.Invoke(level, text);

        /// <summary>连接（或启动）SolidWorks，导出，然后按设置关闭后台启动的 SolidWorks。</summary>
        public ExportSummary Run(IList<ExportItem> items, ExportOptions opt, CancellationToken ct)
        {
            ValidateOptions(opt);
            using (var session = SwSession.Connect((SwMode)opt.Mode, opt.ShowLaunchedSw, m => Emit(LogLevel.Info, m), ct))
            {
                session.ExitOnDispose = opt.CloseLaunchedSw;
                var summary = Run(session, items, opt, ct);
                if (session.LaunchedByUs)
                    Emit(LogLevel.Info, opt.CloseLaunchedSw ? "关闭后台 SolidWorks" : "后台 SolidWorks 保持运行");
                return summary;
            }
        }

        static List<ExportFormat> ValidateOptions(ExportOptions opt)
        {
            var formats = opt.Formats.Select(ExportFormats.ByKey).Where(f => f != null).Distinct().ToList();
            if (formats.Count == 0) throw new InvalidOperationException("没有选择任何导出格式。");
            if (!opt.OutputToSourceFolder && string.IsNullOrWhiteSpace(opt.OutputFolder))
                throw new InvalidOperationException("选择了导出到指定文件夹，但没有设置文件夹。");
            return formats;
        }

        /// <summary>用已有的连接导出（界面版会让后台 SolidWorks 在两次导出之间保持运行）。</summary>
        public ExportSummary Run(SwSession session, IList<ExportItem> items, ExportOptions opt, CancellationToken ct)
        {
            var summary = new ExportSummary();
            var timer = Stopwatch.StartNew();
            _usedOutputs.Clear();
            var formats = ValidateOptions(opt);

            Emit(LogLevel.Info, $"共 {items.Count} 个文件，格式：{string.Join("、", formats.Select(f => f.Label))}");
            {
                if (session.MajorVersion > 0 && session.MajorVersion < SwSession.MinSupportedMajor)
                    Emit(LogLevel.Warning, $"当前是 {session.DisplayName}，本工具按 SolidWorks 2023 及以上版本开发，旧版本可能有功能不可用。");

                var app = session.App;
                var prefs = new PrefSnapshot(app);
                // 记下用户原来的活动文档，导出结束后切回去
                string userActiveTitle = null;
                if (!session.LaunchedByUs)
                {
                    try { userActiveTitle = (app.ActiveDoc as ModelDoc2)?.GetTitle(); } catch { }
                }
                try
                {
                    ApplyPreferences(prefs, opt, formats);

                    for (int i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        if (ct.IsCancellationRequested)
                        {
                            summary.Cancelled = true;
                            item.Status = ItemStatus.Skipped;
                            item.Message = "已取消";
                        }
                        else
                        {
                            try { ProcessItem(app, item, opt, formats, ct); }
                            catch (OperationCanceledException)
                            {
                                summary.Cancelled = true;
                                item.Status = ItemStatus.Skipped;
                                item.Message = "已取消";
                            }
                            catch (Exception ex)
                            {
                                item.Status = ItemStatus.Failed;
                                item.Message = "出错：" + ex.Message;
                                Emit(LogLevel.Error, $"{item.FileName}：{ex.Message}");
                            }
                        }

                        switch (item.Status)
                        {
                            case ItemStatus.Done: summary.Done++; break;
                            case ItemStatus.Warning: summary.Warning++; break;
                            case ItemStatus.Failed: summary.Failed++; break;
                            default: summary.Skipped++; break;
                        }
                        summary.Outputs.AddRange(item.Outputs);
                        Progress?.Invoke(i + 1, items.Count);
                    }
                }
                finally
                {
                    prefs.Restore();
                    if (userActiveTitle != null)
                    {
                        int e = 0;
                        try { app.ActivateDoc3(userActiveTitle, false, 1, ref e); } catch { }
                    }
                }
            }

            summary.Elapsed = timer.Elapsed;
            Emit(summary.Failed > 0 ? LogLevel.Warning : LogLevel.Success,
                $"结束：成功 {summary.Done}，部分成功 {summary.Warning}，失败 {summary.Failed}，跳过 {summary.Skipped}，" +
                $"共生成 {summary.Outputs.Count} 个文件，用时 {summary.Elapsed.TotalSeconds:0.0} 秒" + (summary.Cancelled ? "（已取消）" : ""));
            return summary;
        }

        static void ApplyPreferences(PrefSnapshot p, ExportOptions o, List<ExportFormat> formats)
        {
            // 关掉保存时的信息框和预览，避免批量导出被对话框卡住
            p.SetToggle(TogStlShowInfo, false);
            p.SetToggle(TogStlPreview, false);
            p.SetToggle(Tog3mfShowInfo, false);
            p.SetToggle(TogPdfViewOnSave, false);

            if (formats.Contains(ExportFormats.Step))
                p.SetInt(IntStepAP, o.StepAP);

            if (formats.Contains(ExportFormats.Stl))
            {
                p.SetToggle(TogStlBinary, o.StlBinary);
                p.SetToggle(TogStlDontTranslate, o.StlKeepCoordinates);
                p.SetToggle(TogStlOneFile, o.StlAssemblyOneFile);
                p.SetInt(IntStlQuality, o.StlQuality == "coarse" ? 1 : 2);
                p.SetInt(IntStlUnits, o.MeshUnits == "m" ? 2 : 0);   // swLengthUnit_e：0 = 毫米，2 = 米
            }
        }

        void ProcessItem(ISldWorks app, ExportItem item, ExportOptions opt, List<ExportFormat> formats, CancellationToken ct)
        {
            item.Reset();
            item.Status = ItemStatus.Running;

            if (item.Kind == DocKind.Unknown)
            {
                item.Status = ItemStatus.Skipped;
                item.Message = "不是 SolidWorks 文件";
                return;
            }
            if (!File.Exists(item.Path))
            {
                item.Status = ItemStatus.Failed;
                item.Message = "文件不存在";
                Emit(LogLevel.Error, $"{item.FileName}：文件不存在");
                return;
            }

            var applicable = formats.Where(f => f.AppliesTo(item.Kind)).ToList();
            if (applicable.Count == 0)
            {
                item.Status = ItemStatus.Skipped;
                item.Message = $"所选格式都不适用于{item.KindText}";
                Emit(LogLevel.Info, $"{item.FileName}：{item.Message}，跳过");
                return;
            }

            Emit(LogLevel.Info, $"打开 {item.FileName}");
            var doc = app.GetOpenDocumentByName(item.Path) as ModelDoc2;
            var openedByUs = false;
            if (doc == null)
            {
                int err = 0, warn = 0;
                doc = app.OpenDoc6(item.Path, (int)item.Kind, OpenSilent | OpenReadOnly, "", ref err, ref warn);
                if (doc == null)
                {
                    item.Status = ItemStatus.Failed;
                    item.Message = LoadErrorText(err);
                    Emit(LogLevel.Error, $"{item.FileName}：{item.Message}");
                    return;
                }
                openedByUs = true;
            }
            else
            {
                Emit(LogLevel.Info, $"{item.FileName} 已在 SolidWorks 中打开，直接使用，导出后不关闭");
            }

            var problems = new List<string>();
            var notes = new List<string>();
            var exported = 0;
            string originalConfig = null;
            try
            {
                // STL、IGES、工程图、钣金展开等转换器导出的是"活动文档"，不是调用 SaveAs 的那个文档，
                // 所以每个文件导出前都要先激活（实测：不激活时会把用户正在看的装配体导出去）
                int activateErr = 0;
                app.ActivateDoc3(doc.GetTitle(), false, 1, ref activateErr);   // 1 = 激活时不重建
                if (!(app.ActiveDoc is ModelDoc2 active) || active.GetPathName() != doc.GetPathName())
                    throw new InvalidOperationException("无法把文件切换为活动文档，为避免导出错误的模型，已停止导出该文件");

                originalConfig = ActiveConfigName(doc);
                var configs = ConfigsToExport(doc, item.Kind, opt.AllConfigurations);
                var multiConfig = configs.Count > 1;
                var isSheetMetal = item.Kind == DocKind.Part && applicable.Any(f => f.SheetMetalOnly) && IsSheetMetal(doc);

                foreach (var cfg in configs)
                {
                    if (cfg != null && cfg != ActiveConfigName(doc) && !doc.ShowConfiguration2(cfg))
                    {
                        problems.Add($"无法切换到配置「{cfg}」");
                        continue;
                    }
                    foreach (var fmt in applicable)
                    {
                        ct.ThrowIfCancellationRequested();
                        var cfgText = multiConfig ? $"（配置 {cfg}）" : "";
                        if (fmt.SheetMetalOnly && !isSheetMetal)
                        {
                            if (!notes.Contains("不是钣金件，未导出展开图")) notes.Add("不是钣金件，未导出展开图");
                            continue;
                        }
                        var outPath = BuildOutputPath(item, fmt, multiConfig ? cfg : null, opt);
                        if (ExportOne(app, doc, item.Kind, fmt, outPath, opt, out var produced, out var error))
                        {
                            exported += produced.Count;
                            item.Outputs.AddRange(produced);
                            foreach (var f in produced) Emit(LogLevel.Success, $"  {fmt.Label}{cfgText} → {f}");
                        }
                        else
                        {
                            problems.Add($"{fmt.Label}{cfgText}：{error}");
                            Emit(LogLevel.Error, $"  {fmt.Label}{cfgText} 失败：{error}");
                        }
                    }
                }
            }
            finally
            {
                if (openedByUs)
                {
                    try { app.CloseDoc(doc.GetTitle()); } catch { }
                }
                else if (originalConfig != null && ActiveConfigName(doc) != originalConfig)
                {
                    // 用户自己打开的文件：切回原来的配置
                    try { doc.ShowConfiguration2(originalConfig); } catch { }
                }
            }

            var all = problems.Concat(notes).ToList();
            if (problems.Count == 0)
            {
                item.Status = exported > 0 ? ItemStatus.Done : ItemStatus.Skipped;
                item.Message = exported > 0 ? $"已导出 {exported} 个文件" + (notes.Count > 0 ? "；" + string.Join("；", notes) : "") : string.Join("；", notes);
            }
            else
            {
                item.Status = exported > 0 ? ItemStatus.Warning : ItemStatus.Failed;
                item.Message = (exported > 0 ? $"已导出 {exported} 个文件；" : "") + string.Join("；", all);
            }
        }

        bool ExportOne(ISldWorks app, ModelDoc2 doc, DocKind kind, ExportFormat fmt, string outPath, ExportOptions opt,
                       out List<string> produced, out string error)
        {
            produced = new List<string>();
            error = null;
            var start = DateTime.Now;
            var splitAssemblyStl = fmt == ExportFormats.Stl && kind == DocKind.Assembly && !opt.StlAssemblyOneFile;

            if (File.Exists(outPath))
            {
                try { File.Delete(outPath); }
                catch (Exception ex) { error = "目标文件被占用，无法覆盖（" + ex.Message + "）"; return false; }
            }

            int err = 0, warn = 0;
            bool ok;
            try
            {
                if (fmt == ExportFormats.FlatDxf)
                {
                    var options = 1 | (opt.FlatIncludeBendLines ? 4 : 0);   // 1 = 几何，4 = 折弯线
                    ok = ((PartDoc)doc).ExportToDWG2(outPath, doc.GetPathName(), 1, true, null, false, false, options, null);
                }
                else if (fmt == ExportFormats.Pdf)
                {
                    var pdf = app.GetExportFileData(1) as ExportPdfData;   // 1 = swExportPdfData
                    pdf?.SetSheets(1, null);                                 // 1 = 全部图纸
                    ok = doc.Extension.SaveAs(outPath, 0, SaveAsSilent, pdf, ref err, ref warn);
                }
                else
                {
                    ok = doc.Extension.SaveAs(outPath, 0, SaveAsSilent, null, ref err, ref warn);
                }
            }
            catch (COMException ex)
            {
                error = "SolidWorks 调用出错：" + ex.Message;
                return false;
            }

            // 以"是否生成了新文件"为准：导出前已删除旧文件，所以存在即是这次生成的。
            // 实测：工程图导出 PDF 时 SolidWorks 会返回错误码 1，但 PDF 其实已经正常生成。
            if (splitAssemblyStl)
            {
                var dir = Path.GetDirectoryName(outPath);
                var stem = Path.GetFileNameWithoutExtension(outPath);
                produced.AddRange(Directory.GetFiles(dir, stem + "*.stl")
                    .Where(f => File.GetLastWriteTime(f) >= start.AddSeconds(-2)).OrderBy(f => f)
                    .Select(NormalizeExtensionCase));
            }
            else if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
            {
                produced.Add(NormalizeExtensionCase(outPath));
            }

            if (produced.Count == 0)
            {
                error = !ok || err != 0 ? SaveErrorText(err) : "SolidWorks 报告成功，但没有生成输出文件";
                return false;
            }
            if (!ok || err != 0)
                Emit(LogLevel.Info, $"  （SolidWorks 返回了错误码 {err}，但文件已正常生成）");
            return true;
        }

        /// <summary>
        /// SolidWorks 会把扩展名写成大写（例如 .STL）。Linux 下文件名区分大小写，URDF 里引用 .stl 会找不到，
        /// 所以统一改成小写扩展名。返回改名后的路径。
        /// </summary>
        static string NormalizeExtensionCase(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                var actual = Directory.GetFiles(dir, Path.GetFileName(path)).FirstOrDefault();
                if (actual == null) return path;
                var ext = Path.GetExtension(actual);
                if (ext == ext.ToLowerInvariant()) return actual;
                var target = Path.Combine(dir, Path.GetFileNameWithoutExtension(actual) + ext.ToLowerInvariant());
                var temp = target + ".tmp-rename";
                File.Move(actual, temp);
                File.Move(temp, target);
                return target;
            }
            catch { return path; }
        }

        string BuildOutputPath(ExportItem item, ExportFormat fmt, string config, ExportOptions opt)
        {
            var dir = opt.OutputToSourceFolder ? item.Folder : opt.OutputFolder;
            if (opt.SubfolderPerFormat) dir = Path.Combine(dir, fmt == ExportFormats.FlatDxf ? "DXF展开图" : fmt.Label);
            Directory.CreateDirectory(dir);

            var name = Path.GetFileNameWithoutExtension(item.Path);
            if (config != null) name += "_" + Sanitize(config);
            if (fmt == ExportFormats.FlatDxf) name += "_展开";

            var path = Path.Combine(dir, name + fmt.Extension);
            // 同一批里不同文件导出到同一个名字（例如 A.sldprt 和 A.sldasm 都导出 A.step）时加上类型后缀
            if (_usedOutputs.Contains(path))
                path = Path.Combine(dir, $"{name}_{item.KindText}{fmt.Extension}");
            if (!opt.Overwrite || _usedOutputs.Contains(path)) path = UniquePath(path);
            _usedOutputs.Add(path);
            return path;
        }

        static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                var p = Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!File.Exists(p)) return p;
            }
        }

        static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        static string ActiveConfigName(ModelDoc2 doc)
        {
            try { return doc.ConfigurationManager?.ActiveConfiguration?.Name; }
            catch { return null; }
        }

        static List<string> ConfigsToExport(ModelDoc2 doc, DocKind kind, bool all)
        {
            var current = new List<string> { null };
            if (!all || kind == DocKind.Drawing) return current;
            var raw = doc.GetConfigurationNames();
            var names = (raw as Array)?.Cast<object>().Select(o => o?.ToString()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            return names != null && names.Count > 1 ? names : current;
        }

        static bool IsSheetMetal(ModelDoc2 doc)
        {
            var f = doc.FirstFeature() as Feature;
            while (f != null)
            {
                if (f.GetTypeName2() == "SheetMetal") return true;
                f = f.GetNextFeature() as Feature;
            }
            return false;
        }

        public static string LoadErrorText(int e)
        {
            if ((e & 2) != 0) return "文件不存在";
            if ((e & 8192) != 0) return "文件是用更高版本的 SolidWorks 保存的，当前版本打不开";
            if ((e & 1024) != 0) return "不支持的文件类型";
            if ((e & 65536) != 0) return "SolidWorks 里已经打开了另一个同名文件（不同文件夹），请先关闭它";
            if ((e & (2097152 | 4194304)) != 0) return "文件已损坏，需要在 SolidWorks 里修复";
            if ((e & 8388608) != 0) return "SolidWorks 正忙，请稍后重试";
            if ((e & 262144) != 0) return "系统资源不足";
            return $"打开失败（SolidWorks 错误码 {e}）";
        }

        public static string SaveErrorText(int e)
        {
            if (e == 0) return "导出失败（SolidWorks 没有给出原因）";
            if ((e & (32 | 4096)) != 0) return "这个文件不支持导出为该格式";
            if ((e & 16) != 0) return "目标文件被其他程序占用";
            if ((e & 2048) != 0) return "输出路径太长";
            if ((e & 256) != 0) return "文件扩展名无效";
            if ((e & 2) != 0) return "目标位置是只读的";
            return $"导出失败（SolidWorks 错误码 {e}）";
        }
    }
}
