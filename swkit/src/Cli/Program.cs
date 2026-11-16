using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SwKit.Core;

namespace SwKit.Cli
{
    /// <summary>
    /// 命令行版：swkit-cli export --to step,stl 文件或文件夹……
    /// 适合写批处理脚本，也用于自动化测试。
    /// </summary>
    static class Program
    {
        const string Usage = @"SW 工具箱 命令行版

用法：
  swkit-cli status
      查看 SolidWorks 是否在运行、是什么版本

  swkit-cli export [选项] <文件或文件夹>...
      批量导出。文件夹会递归查找 .sldprt / .sldasm / .slddrw

  swkit-cli urdf <装配体.sldasm> [--out 文件夹] [--name 机器人名] [--up y|z] [--rigid-subasm] [--keep-fixed] [--ignore-mates] [--no-infer] [--analyze]
      从配合识别关节，导出同时支持 ROS 1 和 ROS 2 的功能包（URDF + 网格 + launch），并写出校验报告。
      --up           SolidWorks 模型的上方向（默认 y，导出时转成 ROS 的 z 向上）
      --rigid-subasm 刚性子装配体整体当作一个零件（默认会分析子装配体里的配合）
      --keep-fixed   固定连接不并入上级连杆（默认合并，每个螺钉、垫片不再各占一个连杆）
      --ignore-mates 忽略配合和固定状态，只按零件几何（同轴的轴和孔、轴承、电机输出轴、导轨）推断关节
      --no-infer     不做几何推断，只看配合（旧版行为）
      --analyze      只分析、打印识别结果，不写文件

  swkit-cli urdf-eval [--up y|z] [--out 报告.md] <装配体>...
      评估几何推断：按配合识别的关节当标准答案，统计只按几何推断的召回率、误报和轴线误差

  swkit-cli parts list | make <类型> [参数=值 ...] [--out 文件夹] [--overwrite]
      生成标准件（紧固件、轴承、齿轮、步进电机、铝型材）。list 列出类型和参数，例如：
      swkit-cli parts make socket_cap size=M4 len=16
      默认保存到 文档\SW工具箱\标准件

  swkit-cli fea bench | materials | faces <零件.step> | run <零件.step> --fix … --force … | sample bracket|cantilever [--adaptive 0.03] [--topo 0.3 --stl 输出.stl]
      应力分析：理论解验证、材料表、列出面、命令行计算；示例零件上试自适应加密和拓扑优化。

  swkit-cli dfm <零件.step|.stp|.stl|.sldprt> [--process fdm|sla|cnc] [--up x,y,z] [--overhang 45] [--min-wall mm] [--tilts] [--report 报告.docx]
      可制造性检查：3D 打印（悬垂、支撑、薄壁、最佳摆放方向、用料和时间）或 CNC（可达性、装夹次数、内角、深腔、深孔、薄壁、费用估算），带修改建议，可出 Word 报告。

  swkit-cli ws scan|dup|refs|snapshot|history|restore|watch|recovery <文件夹或文件> …
      工作区：统计 SolidWorks 文件、查同名文件（--global 再查全电脑）、检查装配体引用（后台 SolidWorks）、
      版本存档和恢复、列出 SolidWorks 的自动恢复 / 备份文件。例如：
      swkit-cli ws dup D:\项目 --global
      swkit-cli ws refs D:\项目
      swkit-cli ws snapshot D:\项目 --note 改电机座之前
      swkit-cli ws history D:\项目\支架.sldprt --ws D:\项目
      swkit-cli ws restore D:\项目\支架.sldprt 20261117-005930-805 --ws D:\项目

  swkit-cli api [--port 端口] | mcp
      给 AI 用的接口：api 显示正在运行的 SW 工具箱的本地接口地址和用法；mcp 作为 MCP 服务器运行（Claude Desktop / Claude Code / Cursor 等直接调用）。

  swkit-cli agent --provider deepseek|qwen|openai|kimi|zhipu|doubao|siliconflow|ollama [--model 模型] [--key 密钥] [「要做的事」]
  swkit-cli agent --base-url <兼容 OpenAI 的接口地址> --model <模型> [--key 密钥]
      用任何兼容 OpenAI 接口的大模型（GPT、DeepSeek、通义、Kimi、智谱、豆包、本机 Ollama……）直接操控 SW 工具箱，对话式完成任务。

export 的选项：
  --to <格式>         逗号分隔，可选：step,stl,3mf,iges,x_t,dxf_flat,pdf,dwg,dxf（默认 step,stl）
  --out <文件夹>      输出到指定文件夹（默认与源文件同目录）
  --subfolders        每种格式放进单独的子文件夹
  --step-ap <n>       STEP 协议：214（默认，兼容性最好）或 203
  --stl-quality <q>   coarse 或 fine（默认 fine）
  --stl-ascii         STL 用文本格式（默认二进制）
  --stl-translate     允许 SolidWorks 把 STL 平移到正空间（默认保持原坐标）
  --stl-split         装配体每个零部件单独一个 STL（默认合并成一个文件）
  --units <u>         STL 单位：mm（默认）或 m
  --bend-lines        钣金展开 DXF 包含折弯线
  --all-configs       导出所有配置（文件名加 _配置名）
  --no-overwrite      已存在时不覆盖，另起名字
  --no-recurse        添加文件夹时不进入子文件夹
  --show-sw           后台 SolidWorks 显示窗口（排查问题用）
  --keep-sw           导出后不关闭后台 SolidWorks

说明：导出总是在另外启动的隐藏 SolidWorks 里进行，不会碰你正在用的 SolidWorks。
     导出的是磁盘上已保存的版本，未保存的修改不会包含在内。

退出码：0 全部成功；1 有失败；2 参数错误；3 无法启动 SolidWorks";

        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }

            ComMessageFilter.Register();
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "status": return Status();
                    case "export": return Export(args.Skip(1).ToArray());
                    case "urdf": return Urdf(args.Skip(1).ToArray());
                    case "urdf-eval": return UrdfCli.Eval(args.Skip(1).ToArray());
                    case "dev-geom": return UrdfCli.DevGeom(args.Skip(1).ToArray());
                    case "dev-geom-cache": return UrdfCli.DevGeomCache(args.Skip(1).ToArray());
                    case "dev-near": return UrdfCli.DevNear(args.Skip(1).ToArray());
                    case "parts": return PartsCli.Run(args.Skip(1).ToArray());
                    case "fea": return FeaCli.Run(args.Skip(1).ToArray());
                    case "ws": return WorkspaceCli.Run(args.Skip(1).ToArray());
                    case "dfm": return DfmCli.Run(args.Skip(1).ToArray());
                    case "mcp": return McpServer.Run(args.Skip(1).ToArray());
                    case "api": return McpServer.Info(args.Skip(1).ToArray());
                    case "agent": return AgentCli.Run(args.Skip(1).ToArray());
                    case "dev-make-robot": return DevRobot.Run(args[1]);
                    case "dev-dump": return DevRobot.Dump(args[1]);
                    case "dev-decimate": return DevDecimate(args[1], int.Parse(args[2]), args[3]);
                    default:
                        Console.Error.WriteLine("未知命令：" + args[0]);
                        Console.WriteLine(Usage);
                        return 2;
                }
            }
            finally { ComMessageFilter.Revoke(); }
        }

        /// <summary>开发用：简化一个 STL，打印前后的三角形数、包围盒和体积。</summary>
        static int DevDecimate(string stl, int max, string outFile)
        {
            var m = Core.Urdf.Mesh.ReadStl(stl);
            var t0 = DateTime.Now;
            var d = Core.Urdf.MeshBuilder.Decimate(m, max);
            var ms = (DateTime.Now - t0).TotalMilliseconds;
            string Info(Core.Urdf.Mesh x)
            {
                x.Bounds(out var mn, out var mx);
                double v = 0;
                for (int i = 0; i < x.Data.Count; i += 9)
                {
                    var a = new Core.Urdf.Vec3(x.Data[i], x.Data[i + 1], x.Data[i + 2]);
                    var b = new Core.Urdf.Vec3(x.Data[i + 3], x.Data[i + 4], x.Data[i + 5]);
                    var c = new Core.Urdf.Vec3(x.Data[i + 6], x.Data[i + 7], x.Data[i + 8]);
                    v += a.Dot(b.Cross(c)) / 6;
                }
                return $"{x.Triangles,8} 个三角形  包围盒 {(mx - mn) * 1000} mm  体积 {v * 1e9:0} mm³";
            }
            Console.WriteLine("原始  " + Info(m));
            Console.WriteLine("简化  " + Info(d) + $"  （{ms:0} ms）");
            d.WriteBinary(outFile, "decimated");
            return 0;
        }

        static int Urdf(string[] args)
        {
            string asm = null, outDir = null, name = null, up = "y";
            bool analyzeOnly = false, expandSubs = true, mergeFixed = true;
            var infer = Core.Urdf.InferMode.Auto;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--out": outDir = args[++i]; break;
                    case "--name": name = UrdfWriterName(args[++i]); break;
                    case "--up": up = args[++i].ToLowerInvariant() == "z" ? "z" : "y"; break;
                    case "--analyze": analyzeOnly = true; break;
                    case "--rigid-subasm": expandSubs = false; break;
                    case "--keep-fixed": mergeFixed = false; break;
                    case "--ignore-mates": infer = Core.Urdf.InferMode.GeometryOnly; break;
                    case "--no-infer": infer = Core.Urdf.InferMode.Off; break;
                    default: asm = args[i]; break;
                }
            }
            if (asm == null || !File.Exists(asm)) { Console.Error.WriteLine("用法：swkit-cli urdf <装配体.sldasm> [--out 文件夹] [--name 机器人名] [--up y|z] [--rigid-subasm] [--keep-fixed] [--ignore-mates] [--no-infer] [--analyze]"); return 2; }
            outDir = outDir ?? Path.GetDirectoryName(Path.GetFullPath(asm));

            try
            {
                using (var s = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
                {
                    var doc = Core.Urdf.UrdfExporter.OpenAssembly(s.App, asm);
                    var model = Core.Urdf.UrdfExporter.Analyze(s.App, doc, up, out var raw, Console.WriteLine, expandSubs, infer);
                    if (name != null) model.RobotName = name;
                    Console.WriteLine();
                    Console.WriteLine(Core.Urdf.UrdfExporter.Describe(model));
                    if (analyzeOnly) { s.App.CloseAllDocuments(true); return 0; }

                    var result = Core.Urdf.UrdfExporter.Export(s.App, doc, raw, model, outDir, Console.WriteLine, null, mergeFixed);
                    s.App.CloseAllDocuments(true);
                    Console.WriteLine();
                    Console.WriteLine($"功能包：{result.PackageDir}");
                    Console.WriteLine($"URDF：{result.Model.Links.Count} 个连杆、{result.Model.Joints.Count} 个关节" + (mergeFixed ? "（固定连接已并入上级连杆）" : ""));
                    Console.WriteLine($"校验：{result.Report.Summary}");
                    foreach (var item in result.Report.Items)
                        Console.WriteLine($"  [{Core.Urdf.CheckItem.LevelText(item.Level)}] {item.Category}：{item.Text}");
                    return result.Report.Errors > 0 ? 1 : 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("URDF 导出失败：" + ex.Message);
                return 3;
            }
        }

        static string UrdfWriterName(string s) => Core.Urdf.UrdfWriter.SanitizeName(s);

        static int Status()
        {
            if (!SwSession.IsInstalled()) { Console.WriteLine("本机没有安装 SolidWorks"); return 3; }
            var rev = SwSession.ProbeRunningRevision();
            Console.WriteLine(rev == null ? "SolidWorks 已安装，当前没有运行" : "正在运行：" + SwSession.Describe(rev));
            Console.WriteLine("SolidWorks 程序：" + (SwSession.FindExecutable() ?? "（注册表里没找到）"));
            return 0;
        }

        static int Export(string[] args)
        {
            var opt = new ExportOptions();
            var inputs = new List<string>();
            var recurse = true;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    var a = args[i];
                    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} 后面缺少参数");
                    switch (a.ToLowerInvariant())
                    {
                        case "--to":
                            opt.Formats = Next().Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()).ToList();
                            var bad = opt.Formats.Where(f => ExportFormats.ByKey(f) == null).ToList();
                            if (bad.Count > 0) throw new ArgumentException("不认识的格式：" + string.Join(",", bad));
                            break;
                        case "--out": opt.OutputToSourceFolder = false; opt.OutputFolder = Next(); break;
                        case "--subfolders": opt.SubfolderPerFormat = true; break;
                        case "--step-ap":
                            opt.StepAP = int.Parse(Next());
                            // 实测：这个设置项填 242 时 SolidWorks 实际输出 AP203，所以只提供 203 和 214
                            if (opt.StepAP != 203 && opt.StepAP != 214) throw new ArgumentException("--step-ap 只能是 203 或 214");
                            break;
                        case "--stl-quality":
                            opt.StlQuality = Next().ToLowerInvariant();
                            if (opt.StlQuality != "coarse" && opt.StlQuality != "fine") throw new ArgumentException("--stl-quality 只能是 coarse 或 fine");
                            break;
                        case "--stl-ascii": opt.StlBinary = false; break;
                        case "--stl-translate": opt.StlKeepCoordinates = false; break;
                        case "--stl-split": opt.StlAssemblyOneFile = false; break;
                        case "--units":
                            opt.MeshUnits = Next().ToLowerInvariant();
                            if (opt.MeshUnits != "mm" && opt.MeshUnits != "m") throw new ArgumentException("--units 只能是 mm 或 m");
                            break;
                        case "--bend-lines": opt.FlatIncludeBendLines = true; break;
                        case "--all-configs": opt.AllConfigurations = true; break;
                        case "--no-overwrite": opt.Overwrite = false; break;
                        case "--no-recurse": recurse = false; break;
                        case "--show-sw": opt.ShowLaunchedSw = true; break;
                        case "--keep-sw": opt.CloseLaunchedSw = false; break;
                        default:
                            if (a.StartsWith("--")) throw new ArgumentException("未知选项：" + a);
                            inputs.Add(a);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            var files = ExportFormats.ExpandInputs(inputs, recurse);
            if (files.Count == 0) { Console.Error.WriteLine("没有找到 SolidWorks 文件"); return 2; }

            var items = files.Select(f => new ExportItem(f)).ToList();
            var runner = new ExportRunner();
            runner.Log += (level, text) =>
            {
                var prefix = level == LogLevel.Error ? "[失败] " : level == LogLevel.Warning ? "[注意] " : level == LogLevel.Success ? "[完成] " : "";
                (level == LogLevel.Error ? Console.Error : Console.Out).WriteLine(prefix + text);
            };

            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("正在取消，当前文件导出完后停止……"); };
                ExportSummary summary;
                try { summary = runner.Run(items, opt, cts.Token); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("无法完成导出：" + ex.Message);
                    return 3;
                }

                Console.WriteLine();
                foreach (var it in items) Console.WriteLine($"{it.StatusText,-6} {it.FileName}  {it.Message}");
                return summary.Failed > 0 || summary.Warning > 0 ? 1 : 0;
            }
        }
    }
}
