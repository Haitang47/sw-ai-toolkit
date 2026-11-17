using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SwKit.Core.Fea;
using SwKit.Core.Report;

namespace SwKit.App
{
    /// <summary>
    /// 应力分析 / 拓扑优化的实验报告：Word（概述、材料、网格、边界条件、结果、收敛性、拓扑优化、方法、参考文献）、
    /// Markdown、300 dpi 图片（「图」文件夹）、CSV 原始数据（「数据」文件夹，含全部节点的位移和应力）。
    /// </summary>
    sealed partial class FeaPage
    {
        static string F(double v, string fmt = "0.###") => v.ToString(fmt, CultureInfo.InvariantCulture);

        /// <summary>生成报告。dir 为 null 时让用户选文件夹。返回 docx 路径。</summary>
        internal async Task<string> ReportAsync(string dir)
        {
            if (IsBusy || _mesh == null || _result == null && _topo == null) return null;
            if (dir == null)
            {
                var parent = FolderPicker.Pick(_owner, "选择报告保存位置", _sample != null ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory) : Path.GetDirectoryName(_source));
                if (parent == null) return null;
                var name = (_sample != null ? _sample.Name.Replace(" ", "") : Path.GetFileNameWithoutExtension(_source)) + (_topoMode && _topo != null ? "_拓扑优化报告_" : "_应力分析报告_") + DateTime.Now.ToString("yyyyMMdd_HHmm");
                dir = Path.Combine(parent, name);
            }
            Directory.CreateDirectory(dir);
            var figDir = Path.Combine(dir, "图"); var dataDir = Path.Combine(dir, "数据");
            Directory.CreateDirectory(figDir); Directory.CreateDirectory(dataDir);
            _status.Text = "正在生成报告：渲染 300 dpi 图片……"; _status.Foreground = Gray;
            var saveMode = _showMode; var saveDeform = _showDeform.IsChecked; var cam = _view.SaveCamera();
            var hide = new FrameworkElement[] { _resultBar, Root.Find<FrameworkElement>("ResetViewBtn"), Root.Find<FrameworkElement>("ViewHint"), _selBar };
            var vis = hide.Select(h => h.Visibility).ToArray();
            try
            {
                _capturing = true;
                foreach (var h in hide) h.Visibility = Visibility.Collapsed;
                var d = new ReportDoc { Title = (_sample != null ? "示例：" + _sample.Name : Path.GetFileNameWithoutExtension(_source)) + (_topoMode && _topo != null ? " 拓扑优化报告" : " 结构静力分析报告"), Subtitle = "由 SW 工具箱 " + AppInfo.Version + " 生成", Author = System.Environment.UserName };
                // 静力分析
                if (_result != null && !(_topoMode && _topo != null))
                {
                    var figs = new List<(string file, string caption)>();
                    foreach (var (mode, label, file) in new[] { (_mat.Brittle ? "p1" : "vm", _mat.Brittle ? "最大主应力云图" : "等效应力（von Mises）云图", "1_应力"), ("util", "强度利用率（应力 ÷ 强度）", "2_强度利用率"), ("disp", "位移云图", "3_位移") })
                    {
                        _showMode = mode; _showDeform.IsChecked = mode == "disp";
                        ShowResultView();
                        _view.SetAngles(-50, 25);
                        figs.Add((await Capture(Path.Combine(figDir, file + ".png")), label + (mode == "disp" ? $"（变形放大显示）" : "")));
                    }
                    _showMode = _mat.Brittle ? "p1" : "vm"; _showDeform.IsChecked = false; ShowResultView();
                    _view.SetAngles(-90, 0);
                    figs.Add((await Capture(Path.Combine(figDir, "4_应力_正视.png")), (_mat.Brittle ? "最大主应力" : "等效应力") + "云图（正视，沿 −Z 方向看）"));
                    BuildStaticReport(d, figs, dataDir);
                }
                // 拓扑优化
                if (_topo != null && _topoMode)
                {
                    var figs = new List<(string, string)>();
                    _view.ShowSurface(_topo.Surface, true);
                    _view.SetAngles(-50, 25);
                    figs.Add((await Capture(Path.Combine(figDir, "1_优化结果_轴测.png")), "拓扑优化结果（轴测图，浅色为原设计域）"));
                    _view.SetAngles(-90, 0);
                    figs.Add((await Capture(Path.Combine(figDir, "2_优化结果_正视.png")), "拓扑优化结果（正视，沿 −Z 方向看）"));
                    _view.SetAngles(0, 89);
                    figs.Add((await Capture(Path.Combine(figDir, "3_优化结果_俯视.png")), "拓扑优化结果（俯视）"));
                    _view.ShowSurface(_topo.Surface, _topoGhost);
                    BuildTopoReport(d, figs, dataDir);
                    TopoSurface.WriteStl(_topo.Surface, Path.Combine(dataDir, "优化结果.stl"));
                }
                AddMethods(d);
                var docx = Path.Combine(dir, "报告.docx");
                DocxWriter.Save(d, docx);
                MarkdownWriter.Save(d, Path.Combine(dir, "报告.md"));
                File.WriteAllText(Path.Combine(dir, "方法描述（可直接用于论文）.txt"), MethodsText(d), new UTF8Encoding(true));
                _status.Text = "报告已生成：" + dir; _status.Foreground = Green;
                if (!MainWindow.DevMode) try { System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\""); } catch { }
                return docx;
            }
            catch (Exception ex)
            {
                _status.Text = "生成报告失败：" + ex.Message; _status.Foreground = Red;
                return null;
            }
            finally
            {
                _capturing = false;
                for (int i = 0; i < hide.Length; i++) hide[i].Visibility = vis[i];
                _showMode = saveMode; _showDeform.IsChecked = saveDeform;
                if (_result != null && !_topoMode) ShowResultView();
                _view.RestoreCamera(cam);
            }
        }

        /// <summary>把 3D 视图（含颜色条）按 300 dpi 渲染成 PNG。</summary>
        async Task<string> Capture(string file)
        {
            await Root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(120);
            var host = Root.Find<FrameworkElement>("CaptureHost");
            host.UpdateLayout();
            double w = host.ActualWidth, h = host.ActualHeight, scale = 300.0 / 96;
            var rtb = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 300, 300, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(host), null, new Rect(0, 0, w, h));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
            return file;
        }

        void Overview(ReportDoc d, string kind)
        {
            d.H1("1 概述");
            d.KV(new (string, string)[]
            {
                ("分析对象", _sample != null ? "示例零件：" + _sample.Name : Path.GetFileName(_source)),
                ("文件", _source),
                ("分析类型", kind),
                ("材料", $"{_mat.Name}（{_mat.Category}）"),
                ("网格", $"{_mesh.Elements:N0} 个二次四面体单元（TET10），{_mesh.Nodes:N0} 个节点"),
                ("零件体积 / 质量", $"{F(_mesh.Volume() * 1e9, "N0")} mm³ / {F(_mesh.Volume() * _mat.Density * 1000, "0.##")} g"),
                ("日期", DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
            });
            d.H1("2 材料");
            d.Table("材料参数", new[] { "参数", "数值", "单位" }, new[]
            {
                new[] { "弹性模量 E", F(_mat.E / 1e9, "0.#"), "GPa" },
                new[] { "泊松比 ν", F(_mat.Nu, "0.00"), "—" },
                new[] { _mat.Brittle ? "抗拉强度" : "屈服强度", F(_mat.Strength / 1e6, "0"), "MPa" },
                new[] { "抗压强度", _mat.Brittle ? F(_mat.CompStrength / 1e6, "0") : "—", "MPa" },
                new[] { "密度", F(_mat.Density, "0"), "kg/m³" },
                new[] { "失效判据", _mat.Brittle ? "最大主应力（脆性材料）" : "von Mises 等效应力（塑性材料）", "—" },
            });
            if (_mat.Note != null) d.Note("材料说明：" + _mat.Note + "。数值为该牌号的典型值，重要零件请按供应商数据核对。");
            d.H1("3 边界条件与载荷");
            var fixedArea = _mesh.Faces.Where(f => _fixed.Contains(f.Id)).Sum(f => f.Area);
            d.Table("约束和载荷", new[] { "类型", "作用位置", "大小", "方向" },
                new[] { new[] { "固定（三个方向位移为 0）", $"{_fixed.Count} 个面（{F(fixedArea * 1e6, "0.#")} mm²），面编号 {string.Join("、", _fixed)}", "—", "—" } }
                .Concat(_loads.Select(l => new[]
                {
                    l.Kind == LoadKind.Force ? "力（面上均布，合力）" : l.Kind == LoadKind.Pressure ? "压力" : l.Kind == LoadKind.Torque ? "扭矩（切向分布）" : "重力 / 惯性力",
                    l.Kind == LoadKind.Gravity ? "整个零件" : $"{l.Faces.Count} 个面（{F(_mesh.Faces.Where(f => l.Faces.Contains(f.Id)).Sum(f => f.Area) * 1e6, "0.#")} mm²），面编号 {string.Join("、", l.Faces)}",
                    l.Kind == LoadKind.Force ? F(l.Value, "0.###") + " N" : l.Kind == LoadKind.Pressure ? F(l.Value / 1e6, "0.###") + " MPa" : l.Kind == LoadKind.Torque ? F(l.Value, "0.###") + " N·m" : F(l.Value, "0.##") + " g",
                    l.AlongNormal ? "垂直压向面" : $"({F(l.Dir.X, "0.##")}, {F(l.Dir.Y, "0.##")}, {F(l.Dir.Z, "0.##")})",
                })));
            d.Note("坐标为零件坐标系（与 SolidWorks 中一致，Y 向上），单位：长度 mm。");
        }

        void BuildStaticReport(ReportDoc d, List<(string file, string caption)> figs, string dataDir)
        {
            var r = _result;
            Overview(d, "线弹性静力分析（小变形）");
            d.H1("4 结果");
            var sf = r.SafetyFactor;
            d.Table("主要结果", new[] { "指标", "数值", "说明" }, new[]
            {
                new[] { "最大等效应力（von Mises）", F(r.MaxVm / 1e6, "0.##") + " MPa", "全零件" },
                new[] { "离开约束面后的最大等效应力", F(r.MaxVmAway / 1e6, "0.##") + " MPa", "排除固定面边缘一层单元" },
                new[] { "最大主应力（拉）", F(r.MaxP1 / 1e6, "0.##") + " MPa", "" },
                new[] { "最小主应力（压）", F(r.MinP3 / 1e6, "0.##") + " MPa", "" },
                new[] { "最大位移", F(r.MaxDisp * 1000, "0.####") + " mm", "" },
                new[] { "安全系数", double.IsInfinity(sf) ? "∞" : F(sf, "0.00"), _mat.Brittle ? "min(抗拉/最大主应力, 抗压/|最小主应力|)" : "屈服强度 / 最大等效应力" },
                new[] { "估计误差（能量范数）", F(r.ErrorEstimate * 100, "0.0") + "%", "Zienkiewicz–Zhu 恢复型误差估计" },
                new[] { "外载 / 反力平衡误差", F(r.EquilibriumError * 100, "0.000") + "%", $"外载合力 {F(r.Applied.Length, "0.###")} N" },
            }, "结果汇总");
            var verdict = sf >= 2 ? "强度足够。" : sf >= 1.2 ? "不会失效，但余量不大；受冲击、疲劳或 3D 打印件建议加强。" : sf >= 1 ? "接近失效，建议加强。" : "最大应力超过材料强度，需要加强。";
            d.P($"结论：最大{(_mat.Brittle ? "主应力" : "等效应力")}为 **{F((_mat.Brittle ? r.MaxP1 : r.MaxVm) / 1e6, "0.##")} MPa**，安全系数 **{(double.IsInfinity(sf) ? "∞" : F(sf, "0.00"))}**，{verdict}");
            foreach (var n in r.Notes) d.Note("注：" + n);
            foreach (var (file, caption) in figs) d.Img(file, caption, 14);
            if (r.Passes.Count > 1)
            {
                d.H1("5 网格收敛性");
                d.P("采用基于误差估计的 h 自适应加密：每轮按单元误差重新计算目标单元尺寸、重新划分网格并求解，直到估计误差低于 3% 且离开约束面的最大应力变化小于 3%。各轮结果如表所示。");
                d.Table("自适应加密过程", new[] { "轮次", "单元数", "节点数", "最大等效应力 / MPa", "离开约束面 / MPa", "估计误差" },
                    r.Passes.Select((p, i) => new[] { (i + 1).ToString(), p.Elements.ToString("N0"), p.Nodes.ToString("N0"), F(p.MaxVm / 1e6, "0.##"), F(p.MaxVmAway / 1e6, "0.##"), F(p.Eta * 100, "0.0") + "%" }), "收敛过程");
                if (_refineNote != null) d.P(_refineNote);
            }
            // 原始数据
            var nn = _mesh.Nodes;
            var rows = new List<string[]>(nn);
            for (int n = 0; n < nn; n++)
            {
                double ux = r.U[3 * n], uy = r.U[3 * n + 1], uz = r.U[3 * n + 2];
                rows.Add(new[] { n.ToString(), F(_mesh.X[3 * n] * 1000, "0.####"), F(_mesh.X[3 * n + 1] * 1000, "0.####"), F(_mesh.X[3 * n + 2] * 1000, "0.####"),
                    F(ux * 1000, "0.######"), F(uy * 1000, "0.######"), F(uz * 1000, "0.######"), F(Math.Sqrt(ux * ux + uy * uy + uz * uz) * 1000, "0.######"),
                    F(r.VonMises[n] / 1e6, "0.####"), F(r.Principal1[n] / 1e6, "0.####"), F(r.Principal3[n] / 1e6, "0.####") });
            }
            CsvWriter.Save(Path.Combine(dataDir, "节点结果.csv"), new[] { "节点", "x/mm", "y/mm", "z/mm", "ux/mm", "uy/mm", "uz/mm", "|u|/mm", "等效应力/MPa", "最大主应力/MPa", "最小主应力/MPa" }, rows);
            d.Note($"全部 {nn:N0} 个节点的坐标、位移和应力见 数据\\节点结果.csv。");
        }

        void BuildTopoReport(ReportDoc d, List<(string file, string caption)> figs, string dataDir)
        {
            var t = _topo;
            Overview(d, "拓扑优化（柔度最小化，体积约束）");
            d.H1("4 优化设置");
            d.Table("拓扑优化参数", new[] { "参数", "数值" }, new[]
            {
                new[] { "目标", "柔度最小（刚度最大）" },
                new[] { "约束", $"保留材料体积 ≤ {F(_topoVf * 100, "0")}%" },
                new[] { "材料插值", "SIMP，惩罚指数 p = 3，空单元相对刚度 10⁻³" },
                new[] { "过滤", "密度过滤（体积加权），半径 2 倍平均单元边长" },
                new[] { "投影", "Heaviside 投影，β 从 1 逐步加倍到 16，阈值 0.5" },
                new[] { "更新", "最优准则法（OC），移动限 0.2" },
                new[] { "设计变量", t.PlateMode ? "板件模式：沿板厚方向的一列单元共用一个密度（拉伸约束），只产生穿透孔" : "三维：每个单元一个密度" },
                new[] { "非设计域", $"安装孔、受载孔等约束 / 受载面相邻单元保持实体（很大的面除外），占体积 {F(t.PassiveFraction * 100, "0.0")}%" },
                new[] { "细节程度", _topoDetail },
            });
            d.H1("5 优化结果");
            d.Table("优化结果", new[] { "指标", "数值" }, new[]
            {
                new[] { "保留材料体积", F(t.VolumeFraction * 100, "0.0") + "%" },
                new[] { "刚度（相对实心零件）", F(t.StiffnessRatio * 100, "0.0") + "%" },
                new[] { "实心零件柔度", F(t.Compliance0, "0.######") + " J" },
                new[] { "优化后柔度", F(t.Compliance, "0.######") + " J" },
                new[] { "减重", F(_mat.Density * _mesh.Volume() * (1 - t.VolumeFraction) * 1000, "0.#") + " g" },
                new[] { "迭代次数 / 用时", $"{t.Iterations} 轮 / {F(t.Seconds, "0")} 秒" },
                new[] { "优化后外形", $"{t.Surface.Length / 9:N0} 个三角形，体积 {F(TopoSurface.Volume(t.Surface) * 1e9, "N0")} mm³（数据\\优化结果.stl）" },
            }, "优化结果汇总");
            if (t.Warnings.Count > 0) d.List(t.Warnings.Select(w => "注意：" + w).ToArray());
            foreach (var (file, caption) in figs) d.Img(file, caption, 13);
            CsvWriter.Save(Path.Combine(dataDir, "迭代历史.csv"), new[] { "轮次", "柔度/J", "刚度/实心" },
                t.History.Select((c, i) => new[] { (i + 1).ToString(), F(c, "0.##########"), F(t.Compliance0 / c, "0.####") }));
            d.P("优化结果是概念形状：建议参考它在 CAD 中重新建模（保留主要的梁和筋、去掉空的区域，并修整为可加工的外形），再进行静力分析校核。");
            d.Note("迭代历史见 数据\\迭代历史.csv。");
        }

        void AddMethods(ReportDoc d)
        {
            int n = d.Blocks.OfType<ReportDoc.Heading>().Count(h => h.Level == 1) + 1;
            d.H1($"{n} 方法");
            d.P(MethodsParagraph(d));
            d.H1($"{n + 1} 适用范围与局限");
            d.List(new[]
            {
                "线弹性、小变形、静力分析；不包括接触、螺栓预紧、塑性、大变形、屈曲、振动和疲劳。",
                "固定约束比真实支撑更刚，固定面边缘和无圆角尖角处的应力为奇异值，会随网格加密持续升高，应参考离开约束面后的数值或在尖角处建模真实圆角。",
                "复合材料按各向同性等效，只能粗略估计；3D 打印件层间强度显著低于标称值。",
                "求解器已用拉杆、悬臂梁（挠度与弯曲应力）、带孔板应力集中、圆轴扭转、自重悬臂梁的理论解验证，误差 0.04%–2%。",
            });
        }

        string MethodsParagraph(ReportDoc d)
        {
            var sb = new StringBuilder();
            sb.Append($"有限元网格由开源网格生成器 Gmsh [{d.Ref(ReportDoc.Cite.Gmsh)}] 采用 HXT 算法 [{d.Ref(ReportDoc.Cite.Hxt)}] 生成，单元为十节点二次四面体（TET10）[{d.Ref(ReportDoc.Cite.Spr)}]，单元刚度采用 4 点高斯积分。");
            sb.Append($"线性方程组采用以光滑聚集代数多重网格 [{d.Ref(ReportDoc.Cite.SaAmg)}]（近零空间取 6 个刚体模态，Chebyshev 多项式光滑）为预条件的共轭梯度法求解，相对残差收敛标准 10⁻⁸。");
            sb.Append($"节点应力由单元节点处的应力平均得到；离散误差采用 Zienkiewicz–Zhu 恢复型误差估计 [{d.Ref(ReportDoc.Cite.ZZ)}]，以恢复应力与单元应力之差的能量范数度量");
            if (_result != null && _result.Passes.Count > 1) sb.Append("，并据此进行 h 自适应网格加密直至收敛");
            sb.Append("。");
            if (_topo != null && _topoMode)
            {
                sb.Append($"拓扑优化以柔度最小为目标、体积分数为约束，采用 SIMP 材料插值 [{d.Ref(ReportDoc.Cite.Simp)}]（惩罚指数 3），体积加权的密度过滤 [{d.Ref(ReportDoc.Cite.DensityFilter)}] 与 Heaviside 投影 [{d.Ref(ReportDoc.Cite.Heaviside)}] 抑制棋盘格和灰度单元，");
                sb.Append($"设计变量按最优准则法更新 [{d.Ref(ReportDoc.Cite.Top99)}, {d.Ref(ReportDoc.Cite.Top88)}]；" + (_topo.PlateMode ? "零件为薄板，沿板厚方向施加拉伸约束（同一列单元共用设计变量，过滤在板平面内进行），使结果只含穿透孔、可直接切割加工；" : "") + $"为提高分辨率，优化阶段使用线性四面体单元，最终外形由节点密度 0.5 等值面提取并经 Taubin 平滑。");
            }
            return sb.ToString();
        }

        string MethodsText(ReportDoc d)
        {
            var sb = new StringBuilder();
            sb.AppendLine(MethodsParagraph(d));
            sb.AppendLine();
            sb.AppendLine("参考文献：");
            for (int i = 0; i < d.Refs.Count; i++) sb.AppendLine($"[{i + 1}] {d.Refs[i]}");
            return sb.ToString();
        }
    }
}
