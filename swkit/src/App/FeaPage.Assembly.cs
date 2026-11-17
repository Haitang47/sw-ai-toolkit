using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SolidWorks.Interop.sldworks;
using SwKit.Core;
using SwKit.Core.Cfd;
using SwKit.Core.Dfm;
using SwKit.Core.Fea;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>
    /// 装配体应力分析：STEP（或 .sldasm 转出来的 STEP）里有多个实体时，每个实体是一个零件，接触面粘接，各用各的材料。
    /// · 零件列表：名字（.sldasm 时从 SolidWorks 读零部件名）、体积、质量、材料、是否参与计算；
    /// · 「识别桨叶，加推力」：桨叶不参与结构计算，在桨毂处加远程推力（沿桨的轴线，倾转也对）和反扭矩，加到桨下面的电机上；
    /// · 没接触上、也不受力的零件自动固定住（不影响结果）；受力但没接触上的零件会提示。
    /// </summary>
    sealed partial class FeaPage
    {
        List<FeBody> _bodies;          // 装配体的全部实体；单个零件时为 null
        List<FeBody> _included;        // 参与计算的（顺序 = FeMesh.TetBody 的编号）
        string _asmBrep;               // 装配体几何（读入时从 STEP 转成的 BREP，划网格读它快很多）
        List<(string name, Vec3 min, Vec3 max)> _swComps;
        int _autoExcluded, _autoComplex;   // 读入时自动去掉的小零件、复杂零件个数

        bool IsAssembly => _bodies != null;

        /// <summary>读到 STEP 以后：看是不是多个实体，是的话进入装配体模式。</summary>
        void PrepareAssembly()
        {
            _bodies = null; _included = null; _asmBrep = null;
            List<FeBody> list;
            var dir = Path.Combine(Path.GetTempPath(), "swkit_fea_asm");
            string brep = null;
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var old in Directory.GetFiles(dir)) try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old); } catch { }
                brep = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".brep");
                list = FeAssembly.Read(_step, brep);
            }
            catch { return; }
            if (list.Count <= 1) { try { File.Delete(brep); } catch { } return; }
            _asmBrep = brep;
            _bmin = new Vec3(list.Min(b => b.Min.X), list.Min(b => b.Min.Y), list.Min(b => b.Min.Z));
            _bmax = new Vec3(list.Max(b => b.Max.X), list.Max(b => b.Max.Y), list.Max(b => b.Max.Z));
            FeAssembly.NameFrom(list, _swComps);
            foreach (var b in list)
                b.Material = GuessFromName(b.Name, out _) ?? _mat;
            // 零件很多（整机带螺丝、电子件）：默认去掉很小的零件（包围盒对角线不到整机的 4%），网格才划得动；之后可以在列表里勾回来
            _autoExcluded = 0; _autoComplex = 0;
            if (list.Count > 60)
            {
                double diag = (_bmax - _bmin).Length;
                foreach (var b in list) if (b.Size.Length < 0.04 * diag) { b.Included = false; _autoExcluded++; }
                // 形状很复杂又不是板状的（厂家的电机、舵机、电子件模型）：布尔粘接和划网格都很慢，先不算；推力会加在它下面的电机座 / 支架上
                foreach (var b in list) if (b.Included && b.Faces > 400 && !b.PlateLike) { b.Included = false; _autoComplex++; }
                if (list.All(b => !b.Included)) foreach (var b in list) b.Included = true;
            }
            _bodies = list;
            RebuildAssemblyInput();
        }

        void RebuildAssemblyInput()
        {
            _included = _bodies.Where(b => b.Included).ToList();
            if (_included.Count == 0) throw new InvalidOperationException("至少要留一个零件参与计算");
        }

        /// <summary>
        /// 装配体划网格（后台线程调用）：零件分组并行划，划不出来的零件自动去掉（_included 跟着更新）。
        /// 返回的提示放在状态栏：去掉了哪些零件、网格超过内存上限时最重的几个零件。
        /// </summary>
        FeMesh MeshAssembly(double size, string backgroundPos, double bgMin, out string note)
        {
            int threads = Math.Max(1, System.Environment.ProcessorCount - 2);
            var mesh = FeAssembly.Mesh(_asmBrep, _included, size, threads, Phase, out var failed, backgroundPos, bgMin);
            note = null;
            if (failed.Count > 0)
            {
                foreach (var b in failed) b.Included = false;
                RebuildAssemblyInput();
                note = $"{failed.Count} 个零件划不出网格（形状太复杂或有破面），已去掉：" + string.Join("、", failed.Take(4).Select(b => b.Name)) + (failed.Count > 4 ? " 等" : "") + "。";
            }
            return mesh;
        }

        /// <summary>网格超过内存上限时：节点最多的几个零件（建议去掉或换简化模型）。</summary>
        string HeavyBodiesNote(FeMesh m)
        {
            if (m.TetBody == null || 3L * m.Nodes <= FeSolver.AmgDofLimit()) return null;
            var count = new int[_included.Count];
            var seen = new HashSet<long>();
            for (int e = 0; e < m.Elements; e++)
                for (int k = 0; k < 10; k++) if (seen.Add(((long)m.TetBody[e] << 32) | (uint)m.Tets[10 * e + k])) count[m.TetBody[e]]++;
            var top = Enumerable.Range(0, count.Length).OrderByDescending(i => count[i]).Take(3)
                .Select(i => $"{_included[i].Name}（{count[i] / 1000.0:0.#} 千节点）");
            return $"网格有 {m.Nodes:N0} 个节点，超过本机内存能快速求解的规模，求解会慢很多。节点最多的零件：{string.Join("、", top)}——不承力的（电路板、电池、电机）可以在列表里去掉。";
        }

        /// <summary>新网格：标出每个单元属于哪个零件。</summary>
        void AfterMesh(FeMesh mesh)
        {
            if (!IsAssembly) return;
            FeAssembly.AssignBodies(mesh, _included);
            int n = FeAssembly.Tie(mesh, _included);
            Phase($"零件之间的面绑定：{n:N0} 个节点");
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("SWKIT_FEA_LOG")) && mesh.Ties != null)
            {
                var cnt = new int[_included.Count];
                foreach (var t in mesh.Ties) { cnt[t.SlaveBody]++; cnt[t.MasterBody]++; }
                var comp = FeAssembly.Components(mesh, _included, out int main);
                Phase("绑定节点数：" + string.Join("，", Enumerable.Range(0, _included.Count).Select(i => $"{_included[i].Name}={cnt[i]}{(comp[i] == main ? "" : "(不连主体)")}")));
                if (mesh.WeakLinks != null && mesh.WeakLinks.Count > 0)
                    Phase("点 / 线接触（不粘接）：" + string.Join("，", mesh.WeakLinks.Select(w => $"{_included[w.a].Name}–{_included[w.b].Name}({w.nodes})")));
                var piece = FeAssembly.FacePieces(mesh, out int np);
                var perBody = Enumerable.Range(0, mesh.Elements).GroupBy(e => mesh.TetBody[e]).Select(g => (b: g.Key, n: g.Select(e => piece[e]).Distinct().Count(), sizes: g.GroupBy(e => piece[e]).Select(x => x.Count()).OrderByDescending(x => x).ToList())).Where(x => x.n > 1).ToList();
                Phase($"面连通的块：共 {np} 块；多于一块的零件：" + string.Join("，", perBody.Select(x => $"{_included[x.b].Name}={x.n}块({string.Join("/", x.sizes.Take(5))})")));
            }
        }

        /// <summary>装配体：模型带上每个零件的材料、换网格时怎么标零件、没接触也不受力的零件（固定住）。</summary>
        FeModel WithAssembly(FeModel md)
        {
            if (!IsAssembly || md.Mesh?.TetBody == null) return md;
            md.BodyMaterials = _included.Select(b => b.Material).ToArray();
            md.BodyNames = _included.Select(b => b.Name).ToArray();
            var inc = _included;
            md.BodyMap = m => { FeAssembly.AssignBodies(m, inc); FeAssembly.Tie(m, inc); };
            md.ExtraFixedNodes = FeAssembly.FloatingNodes(md, out var floating);
            if (floating.Count == 0)
            {
                // 机构检查：只靠一个点 / 一条线连着、能整体转动的零件。不受力的：断开它的绑定，当作悬空零件固定住（它本来也传不了力）；
                // 受力的：提示（这种情况迭代不会收敛，与其让用户等几十分钟，不如先说清楚）
                var mech = FeAssembly.Mechanisms(md.Mesh, _included.Count, md.FixedFaces, md.ExtraFixedNodes);
                if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("SWKIT_FEA_LOG")) && FeAssembly.LastModes != null)
                    Phase("诊断：刚体模态（最小几个）：" + string.Join("；", FeAssembly.LastModes.Select(x => $"{x.ev:0.0e0} " + string.Join("+", x.bodies.Take(6).Select(i => _included[i].Name)))));
                if (mech.Count > 0)
                {
                    Phase($"机构检查：{mech.Count} 个能自由运动的模式：" + string.Join("；", mech.Take(8).Select(g => string.Join("+", g.Select(i => _included[i].Name)))));
                    var loaded = new HashSet<int>();
                    foreach (var l in md.Loads)
                    {
                        if (l.Kind == LoadKind.Gravity) { for (int i = 0; i < _included.Count; i++) loaded.Add(i); break; }
                        foreach (var f in md.Mesh.Faces.Where(f => l.Faces.Contains(f.Id))) loaded.Add(md.Mesh.BodyOfFace(f));
                    }
                    var bad = mech.Where(g => g.Any(loaded.Contains)).SelectMany(g => g).Distinct().ToList();
                    if (bad.Count > 0)
                        throw new InvalidOperationException("这些零件没有完全约束住，受力后会整体转动或移动（只在一个点 / 一条线上和别的零件接触，或者整串零件都没固定）：" +
                            string.Join("、", bad.Take(6).Select(i => _included[i].Name)) + "。装配体里靠螺丝连接的，把螺丝勾回来参与计算；或者给它们加约束");
                    var free = new HashSet<int>(mech.SelectMany(g => g));
                    md.Mesh.Ties?.RemoveAll(t => free.Contains(t.SlaveBody) || free.Contains(t.MasterBody));
                    md.ExtraFixedNodes = FeAssembly.FloatingNodes(md, out floating);
                    md.Notes.Add($"{free.Count} 个不受力的零件只在一个点 / 一条线上和别的零件接触（能转动，传不了力），按没接触处理：" + string.Join("、", free.Take(5).Select(i => _included[i].Name)) + (free.Count > 5 ? " 等" : ""));
                }
            }
            if (floating.Count > 0)
                throw new InvalidOperationException("这些零件受了力，但和固定的零件没有面接触（有间隙、没贴上，或者只在一个点 / 一条线上碰着——能转动），会整体飞走：" +
                    string.Join("、", floating.Distinct().Take(6).Select(i => _included[i].Name)) + "。装配体里靠螺丝连接的零件，把螺丝勾回来参与计算；或者把力加在和机架面贴面的零件上，或者给它们也加约束");
            return md;
        }

        double AssemblyMassG() => IsAssembly ? _included.Sum(b => b.Material.Density * b.Volume) * 1000 : _mat.Density * _mesh.Volume() * 1000;

        // ================= 右侧：零件列表 =================

        static readonly Color[] BodyColors =
        {
            Color.FromRgb(0x5B, 0x8D, 0xEF), Color.FromRgb(0x4C, 0xB3, 0x8A), Color.FromRgb(0xE0, 0x9A, 0x3E), Color.FromRgb(0xB0, 0x6A, 0xD9),
            Color.FromRgb(0xD9, 0x6A, 0x7A), Color.FromRgb(0x3E, 0xA8, 0xC4), Color.FromRgb(0x9A, 0xA5, 0x3E), Color.FromRgb(0x8A, 0x7A, 0x6A),
        };

        void AddBodiesCard()
        {
            Section($"零件（{_included.Count} / {_bodies.Count} 个参与计算）");
            var sp = new StackPanel();
            sp.Children.Add(Caption($"装配体：零件之间接触的面粘在一起计算，每个零件用自己的材料。整机质量约 {AssemblyMassG():0.#} g（参与计算的零件）。", Gray, new Thickness(0, 0, 0, 6)));
            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            var thrust = SmallButton("识别桨叶，自动加推力", async () => await AutoThrustAsync());
            thrust.ToolTip = "找出桨叶：桨叶不参与结构计算，在桨毂处加沿桨轴线的远程推力（倾转、斜装也对）和反扭矩，传到桨下面的电机上";
            thrust.Margin = new Thickness(0, 0, 14, 0);
            tools.Children.Add(thrust);
            var small = SmallButton("去掉很小的零件", async () =>
            {
                double total = _bodies.Sum(b => b.MassVolume);
                foreach (var b in _bodies) if (b.MassVolume > 0 && b.MassVolume < 0.002 * total) b.Included = false;
                await ApplyBodySelectionAsync();
            });
            small.ToolTip = "体积不到整机 0.2% 的零件（螺丝、垫片、小电子件）不参与计算，网格会小很多";
            tools.Children.Add(small);
            sp.Children.Add(tools);
            var names = FeMaterial.All.Select(x => x.Name).ToList();
            int shown = 0;
            foreach (var b in _bodies.OrderByDescending(x => x.Included).ThenByDescending(x => x.MassVolume))
            {
                if (shown++ >= 40) { sp.Children.Add(Caption($"还有 {_bodies.Count - 40} 个零件没列出（多半是很小的零件）", Text3, new Thickness(0, 4, 0, 0))); break; }
                int idx = _included.IndexOf(b);
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var cb = new CheckBox { IsChecked = b.Included, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "参与计算" };
                var bb = b;
                cb.Checked += async (s, e) => { bb.Included = true; await ApplyBodySelectionAsync(); };
                cb.Unchecked += async (s, e) => { bb.Included = false; await ApplyBodySelectionAsync(); };
                DockPanel.SetDock(cb, Dock.Left); row.Children.Add(cb);
                var swatch = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(idx >= 0 ? BodyColors[idx % BodyColors.Length] : Color.FromRgb(0xD0, 0xD0, 0xD5)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(swatch, Dock.Left); row.Children.Add(swatch);
                var combo = new ComboBox { Width = 150, FontSize = 11.5, IsEnabled = b.Included, ItemsSource = names, SelectedItem = b.Material?.Name, VerticalAlignment = VerticalAlignment.Center };
                combo.SelectionChanged += (s, e) =>
                {
                    var m = FeMaterial.All.FirstOrDefault(x => x.Name == combo.SelectedItem as string);
                    if (m != null && m != bb.Material) { bb.Material = m; Changed(); }
                };
                DockPanel.SetDock(combo, Dock.Right); row.Children.Add(combo);
                var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = b.Included ? Ui.Brush("#1D1D1F") : Text3 };
                text.Inlines.Add(new System.Windows.Documents.Run(b.Name));
                if (b.MassVolume > 0) text.Inlines.Add(new System.Windows.Documents.Run($"  {b.Material.Density * b.MassVolume * 1000:0.#} g") { Foreground = Gray, FontSize = 11 });
                text.ToolTip = $"{b.Name}：{b.Size.X * 1000:0.#} × {b.Size.Y * 1000:0.#} × {b.Size.Z * 1000:0.#} mm" + (b.MassVolume > 0 ? $"，体积 {b.MassVolume * 1e6:0.###} cm³" : "");
                row.Children.Add(text);
                // 鼠标移上去：在 3D 视图里高亮这个零件
                if (idx >= 0)
                {
                    row.MouseEnter += (s, e) => { if (_result == null && _topo == null && _mesh?.TetBody != null) _view.SetFaceColors(id => { var f = _mesh.Faces.FirstOrDefault(x => x.Id == id); return f != null && _mesh.BodyOfFace(f) == idx ? FeaView.CHover : FaceColor(id); }); };
                    row.MouseLeave += (s, e) => { if (_result == null && _topo == null) _view.SetFaceColors(FaceColor); };
                }
                sp.Children.Add(row);
            }
            _side.Children.Add(Group(sp));
        }

        /// <summary>改了参与计算的零件：重新生成几何、重新划网格（面编号会变，约束和载荷要重新选）。</summary>
        async Task ApplyBodySelectionAsync()
        {
            if (IsBusy) return;
            try { RebuildAssemblyInput(); }
            catch (Exception ex) { _status.Text = ex.Message; _status.Foreground = Orange; BuildSide(); return; }
            _fixed.Clear(); _loads.Clear(); _result = null; _topo = null;
            Ui.Fade(_work, false); Ui.Fade(_busy, true);
            SetBusy(true, "正在重新划分网格");
            await RemeshAsync();
            _status.Text = "参与计算的零件改了，约束和载荷已清空，请重新设置。" + _status.Text;
        }

        // ================= 识别桨叶，自动加推力 =================

        /// <summary>
        /// 1. 用表面网格按零件统计形状，找出桨叶（CfdProps：扁平、扭转、同样大小成组）；
        /// 2. 桨叶不参与计算（重划网格）；
        /// 3. 每个桨：桨下面的零件（包围盒碰到桨毂、最小的那个，一般是电机）的所有面上加远程推力（作用点 = 桨毂中心，方向 = 桨轴，朝上）
        ///    和反扭矩（相邻的桨反向）；推力默认 = 整机重量 ÷ 桨数（悬停），之后可以改、或者生成多旋翼常用工况。
        /// </summary>
        async Task AutoThrustAsync()
        {
            if (!IsAssembly || IsBusy) return;
            // 用表面网格识别桨叶（和 CFD 页同一套方法），不参与计算的零件里的桨也能找到。
            // 只划「可能是桨」的零件：比整机对角线的 4% 大、扁（最薄方向不到最长的 40%）、面不太多（厂家电机模型几千个面，很慢）
            Phase("正在识别桨叶");
            DfmMesh dm;
            double diag = (_bmax - _bmin).Length;
            var cands = _bodies.Where(b =>
            {
                var d = new[] { b.Size.X, b.Size.Y, b.Size.Z }.OrderBy(x => x).ToArray();
                return d[2] > 0.04 * diag && d[0] < 0.4 * d[2] && b.Faces <= 800;
            }).Select(b => b.Tag).ToList();
            try
            {
                var brep = _asmBrep;
                if (brep != null && File.Exists(brep) && cands.Count == 0) { _status.Text = "没有找到桨叶（装配体里没有扁平的大零件）。可以手动选电机座的孔，点「加远程力」，作用点填桨毂中心"; _status.Foreground = Orange; return; }
                dm = await Task.Run(() => brep != null && File.Exists(brep) ? DfmMesh.LoadBrepCoarse(brep, diag, 4, Phase, cands) : DfmMesh.Load(_step, Phase));
            }
            catch (Exception ex) { _status.Text = "读取表面失败：" + ex.Message; _status.Foreground = Red; return; }
            int nt = dm.Tris; var tris = new float[9 * nt]; var part = new int[nt];
            for (int t = 0; t < nt; t++)
            {
                for (int k = 0; k < 3; k++) { int v = dm.T[3 * t + k]; tris[9 * t + 3 * k] = (float)(dm.V[3 * v] / 1000); tris[9 * t + 3 * k + 1] = (float)(dm.V[3 * v + 1] / 1000); tris[9 * t + 3 * k + 2] = (float)(dm.V[3 * v + 2] / 1000); }
                part[t] = dm.ShellOf != null && t < dm.ShellOf.Length ? Math.Max(0, dm.ShellOf[t]) : 0;
            }
            var shapes = CfdProps.Shapes(tris, part);
            var found = CfdProps.Detect(shapes, (_bmax - _bmin).Length);
            if (found.Count == 0) { _status.Text = "没有找到桨叶（桨叶和电机可能是同一个实体，或者装配体里没有桨）。可以手动选电机座的孔，点「加远程力」，作用点填桨毂中心"; _status.Foreground = Orange; return; }
            var up = new Vec3(0, 1, 0);
            // 桨叶 → 对应的零件（包围盒包含桨叶中心、尺寸和桨径接近）
            FeBody BodyOf(CfdProps.PartShape s) => _bodies.Where(b => s.Center.X > b.Min.X - 1e-3 && s.Center.X < b.Max.X + 1e-3 && s.Center.Y > b.Min.Y - 1e-3 && s.Center.Y < b.Max.Y + 1e-3 && s.Center.Z > b.Min.Z - 1e-3 && s.Center.Z < b.Max.Z + 1e-3)
                                                          .OrderBy(b => Math.Abs(Math.Max(b.Size.X, Math.Max(b.Size.Y, b.Size.Z)) - 2 * s.Radius)).FirstOrDefault();
            var props = found.Select(i => (shape: shapes[i], body: BodyOf(shapes[i]))).ToList();
            // 整机重量：所有实体（包括不参与计算的电机、电池……）的体积 × 对应零件的材料密度
            double totalG = 0;
            if (_bodies.All(b => b.CadVolume > 0 || b.Volume > 0))
                totalG = _bodies.Sum(b => b.MassVolume * (b.Material ?? _mat).Density * 1000);
            else
                foreach (var sh in dm.Shells.Where(x => !x.Cavity))
                {
                    var c = Vec3.Zero; int cnt = 0;
                    foreach (var t in sh.Tris.Take(30)) { c = c + dm.P(dm.T[3 * t]) * 0.001; cnt++; }
                    c = c * (1.0 / Math.Max(1, cnt));
                    var b = _bodies.FirstOrDefault(x => c.X > x.Min.X - 1e-3 && c.X < x.Max.X + 1e-3 && c.Y > x.Min.Y - 1e-3 && c.Y < x.Max.Y + 1e-3 && c.Z > x.Min.Z - 1e-3 && c.Z < x.Max.Z + 1e-3);
                    totalG += Math.Abs(sh.Volume) * 1e-9 * (b?.Material ?? _mat).Density * 1000;
                }
            bool changed = props.Any(p => p.body != null && p.body.Included);
            foreach (var p in props) if (p.body != null) p.body.Included = false;
            _loads.Clear(); _result = null; _topo = null;
            if (changed || _mesh?.TetBody == null)
            {
                // 桨叶原来参与计算：去掉后重新划网格（面编号会变，固定面也要重选）
                RebuildAssemblyInput();
                Ui.Fade(_work, false); Ui.Fade(_busy, true);
                SetBusy(true, "桨叶不参与计算，重新划分网格");
                _fixed.Clear();
                await RemeshAsync();
            }
            if (_mesh?.TetBody == null) return;
            // 每个桨下面的零件：包围盒包含桨毂中心附近（沿桨轴往下一点）的最小零件
            double thrust = Math.Max(0.1, totalG / 1000 * 9.80665 / props.Count);
            var mid = (_bmin + _bmax) * 0.5;
            var ordered = props.OrderBy(p => Math.Atan2(p.shape.Center.Z - mid.Z, p.shape.Center.X - mid.X)).ToList();
            int added = 0, viaMount = 0; var missing = new List<string>();
            // 零件之间的连通：电机常常只靠螺丝（很小，默认不参与计算）装在电机座上，和机架没粘上——推力加在它上面会「飞走」。
            // 所以沿桨轴往下找时，优先找和机身主体连在一起的零件（电机座 / 机臂 / 倾转支架）
            var comp = FeAssembly.Components(_mesh, _included, out int mainComp);
            // 只靠一小片接触挂在机身上的零件（实际靠螺丝，例如电机座）：相对机身主体几乎能转，推力加在它上面迭代收敛不了，
            // 跳过它，加在下面牢固连着机身的零件上（作用点仍在桨毂，力和力矩等效）
            int mainBody = Enumerable.Range(0, _included.Count).Where(b => comp[b] == mainComp).OrderByDescending(b => _included[b].Volume).FirstOrDefault();
            var weak = new HashSet<int>(FeAssembly.Mechanisms(_mesh, _included.Count, new int[0], null, mainBody).SelectMany(g => g));
            weak.Remove(mainBody);
            for (int b = 0; b < comp.Length; b++) if (weak.Contains(b)) comp[b] = -1;
            for (int i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i].shape;
                var axis = s.Axis.Dot(up) < 0 ? -s.Axis : s.Axis;
                // 沿桨轴往下找：第一个碰到的参与计算的零件（一般是电机；电机没参与计算时是电机座 / 倾转支架），取包围盒最小的
                int first = -1, best = -1;
                for (double dz = Math.Max(0.004, s.Thickness); dz < 0.12 && best < 0; dz += 0.004)
                {
                    var probe = s.Center - axis * dz;
                    double vAny = double.MaxValue, vMain = double.MaxValue; int hitAny = -1, hitMain = -1;
                    for (int b = 0; b < _included.Count; b++)
                    {
                        var bb = _included[b]; const double tol = 0.003;
                        bool hit = probe.X > bb.Min.X - tol && probe.X < bb.Max.X + tol && probe.Y > bb.Min.Y - tol && probe.Y < bb.Max.Y + tol && probe.Z > bb.Min.Z - tol && probe.Z < bb.Max.Z + tol;
                        if (!hit) continue;
                        double v = Math.Max(bb.Size.X, 1e-4) * Math.Max(bb.Size.Y, 1e-4) * Math.Max(bb.Size.Z, 1e-4);
                        if (v < vAny) { vAny = v; hitAny = b; }
                        if (comp[b] == mainComp && v < vMain) { vMain = v; hitMain = b; }
                    }
                    if (first < 0 && dz < 0.08) first = hitAny;
                    best = hitMain;
                }
                if (best < 0) best = first;
                else if (first >= 0 && first != best) viaMount++;
                if (best < 0) { missing.Add($"桨 {i + 1}"); continue; }
                var faces = _mesh.Faces.Where(f => _mesh.BodyOfFace(f) == best).Select(f => f.Id).ToList();
                if (faces.Count == 0) { missing.Add($"桨 {i + 1}"); continue; }
                _loads.Add(new FeLoad { Kind = LoadKind.Remote, Faces = faces.ToList(), Value = thrust, Dir = axis, Point = s.Center });
                double q = thrust * s.Radius * 2 * 0.06;   // 反扭矩粗估（和 CFD 页一样），相邻的桨反向
                _loads.Add(new FeLoad { Kind = LoadKind.Torque, Faces = faces.ToList(), Value = q, Dir = i % 2 == 0 ? axis : -axis });
                added++;
            }
            BuildSide(); _view.SetFaceColors(FaceColor);
            _status.Text = $"找到 {props.Count} 个桨叶（Φ{props.Average(p => p.shape.Radius) * 2000:0} mm），已不参与计算；在 {added} 个电机{(viaMount > 0 ? " / 电机座" : "")}上加了远程推力（每个 {thrust:0.##} N = 整机 {totalG:0} g ÷ {props.Count}，作用在桨毂、沿桨轴）和反扭矩。" +
                           (viaMount > 0 ? $"其中 {viaMount} 个电机 / 电机座和机架没有牢固的面接触（实际靠螺丝固定，螺丝没参与计算），推力加在它下面牢固连着机架的零件上（作用点仍在桨毂）。" : "") +
                           (missing.Count > 0 ? $"{string.Join("、", missing)} 没找到桨下面的零件，请手动加。" : "") + "再选机身上固定的位置（例如中心板的安装孔），点「固定」；要算满油门、着陆等，点「生成多旋翼常用工况」";
            _status.Foreground = missing.Count > 0 ? Orange : Gray;
        }

        // ================= AI 接口 =================

        object BodiesInfo() => _bodies == null ? (object)ApiArgs.Ret("assembly", false) : _bodies.Select((b, i) => ApiArgs.Ret(
            "body", i, "name", b.Name, "included", b.Included, "material", b.Material?.Name, "volume_cm3", Math.Round(b.MassVolume * 1e6, 4),
            "mass_g", Math.Round((b.Material?.Density ?? 0) * b.MassVolume * 1000, 3), "size_mm", new[] { b.Size.X * 1000, b.Size.Y * 1000, b.Size.Z * 1000 })).ToList();

        async Task<object> AssemblyApiAsync(string action, Dictionary<string, object> a)
        {
            if (!IsAssembly) { RequireMesh(); throw new InvalidOperationException("当前不是装配体（只有一个实体）"); }
            switch (action)
            {
                case "bodies": return BodiesInfo();
                case "set_body":
                    {
                        var ids = a.Has("bodies") ? a.List("bodies").Select(o => Convert.ToInt32(o)).ToList() : new List<int> { (int)a.Num("body") };
                        bool remesh = false;
                        foreach (var i in ids)
                        {
                            if (i < 0 || i >= _bodies.Count) throw new ArgumentException($"零件编号 0–{_bodies.Count - 1}");
                            if (a.Has("material"))
                            {
                                var n = a.Str("material");
                                _bodies[i].Material = FeMaterial.All.FirstOrDefault(m => m.Name == n) ?? FeMaterial.All.FirstOrDefault(m => m.Name.Contains(n)) ?? throw new ArgumentException("没有这个材料：" + n);
                            }
                            if (a.Has("included") && _bodies[i].Included != a.Bool("included")) { _bodies[i].Included = a.Bool("included"); remesh = true; }
                        }
                        if (remesh) await ApplyBodySelectionAsync(); else Changed();
                        return BodiesInfo();
                    }
                case "auto_thrust":
                    await AutoThrustAsync();
                    return ApiArgs.Ret("status", _status.Text, "loads", _loads.Count, "bodies", BodiesInfo());
                default: throw new ArgumentException("未知操作：" + action);
            }
        }

        // ================= SolidWorks 装配体 → STEP（带零部件名和包围盒） =================

        /// <summary>在后台 SolidWorks 里只读打开装配体，导出 STEP；同时读每个零部件的名字和包围盒（米，装配体坐标），用来给 STEP 里的实体起名。</summary>
        static (string step, List<(string, Vec3, Vec3)> comps) AsmToStep(ISldWorks app, string path)
        {
            int e = 0, w = 0;
            var already = app.GetOpenDocumentByName(path) as ModelDoc2;
            var doc = already ?? app.OpenDoc6(path, 2, 1 | 2, "", ref e, ref w);
            if (doc == null) throw new InvalidOperationException("打开装配体失败：" + ExportRunner.LoadErrorText(e));
            try
            {
                var asm = (AssemblyDoc)doc;
                try { asm.ResolveAllLightWeightComponents(false); } catch { }
                var comps = new List<(string, Vec3, Vec3)>();
                if (asm.GetComponents(false) is object[] arr)
                    foreach (Component2 c in arr)
                    {
                        try
                        {
                            if (c.IsSuppressed()) continue;
                            if (c.GetModelDoc2() is ModelDoc2 md && md.GetType() != (int)SolidWorks.Interop.swconst.swDocumentTypes_e.swDocPART) continue;
                            if (c.GetBox(false, false) is double[] box && box.Length >= 6)
                                comps.Add((c.Name2, new Vec3(box[0], box[1], box[2]), new Vec3(box[3], box[4], box[5])));
                        }
                        catch { }
                    }
                var dir = Path.Combine(Path.GetTempPath(), "swkit_fea_parts");
                Directory.CreateDirectory(dir);
                var step = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".step");
                app.ActivateDoc3(doc.GetTitle(), false, 1, ref e);
                doc.Extension.SaveAs(step, 0, 1, null, ref e, ref w);
                if (!File.Exists(step)) throw new InvalidOperationException("导出 STEP 失败：" + ExportRunner.SaveErrorText(e));
                return (step, comps);
            }
            finally { if (already == null) try { app.CloseDoc(doc.GetTitle()); } catch { } }
        }
    }
}
