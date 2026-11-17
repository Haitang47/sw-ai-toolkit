using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwKit.Core.Fea;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>
    /// 应力分析的多工况：一个零件在使用中会遇到几种不同的受力（例如无人机机架的满油门、偏航急转、硬着陆、侧面磕碰）。
    /// 约束所有工况共用；载荷属于各自的工况。计算时逐个工况求解，结果显示包络（每个点最危险的值），也可以切换看单个工况；
    /// 拓扑优化 / 凸边布局同时兼顾所有工况。只有一个工况时界面和以前完全一样。
    /// </summary>
    sealed partial class FeaPage
    {
        readonly List<FeLoadCase> _cases = new List<FeLoadCase>();   // 空 = 只有一个工况
        int _curCase;          // 正在编辑的工况
        int _viewCase = -1;    // 结果显示哪个工况（−1 = 包络）

        bool MultiCase => _cases.Count > 1;
        IEnumerable<FeLoad> CurLoads => MultiCase ? _loads.Where(l => l.Case == _curCase) : _loads;
        List<FeLoadCase> CasesCopy() => MultiCase ? _cases.Select(c => c.Clone()).ToList() : new List<FeLoadCase>();
        string CaseName(int c) => c < _cases.Count ? _cases[c].Name : $"工况 {c + 1}";

        /// <summary>正在显示的结果：包络，或者选中的某个工况。</summary>
        FeResult Shown => _result != null && _result.CaseResults != null && _viewCase >= 0 && _viewCase < _result.CaseResults.Count ? _result.CaseResults[_viewCase] : _result;

        void ResetCases() { _cases.Clear(); _curCase = 0; _viewCase = -1; }

        /// <summary>新工况：复制当前工况的载荷并乘以 scale（scale = 0 时是空工况）。</summary>
        int AddCase(string name, double scale, double weight = 1)
        {
            if (_cases.Count == 0) _cases.Add(new FeLoadCase { Name = "工况 1" });
            int from = _curCase;
            _cases.Add(new FeLoadCase { Name = string.IsNullOrWhiteSpace(name) ? $"工况 {_cases.Count + 1}" : name.Trim(), Weight = weight });
            int idx = _cases.Count - 1;
            if (scale > 0)
                foreach (var l in _loads.Where(x => x.Case == from).ToList())
                {
                    var c = l.Clone(); c.Case = idx; c.Value *= scale; _loads.Add(c);
                }
            _curCase = idx;
            Changed();
            return idx;
        }

        void RemoveCase(int idx)
        {
            if (!MultiCase || idx < 0 || idx >= _cases.Count) return;
            _loads.RemoveAll(l => l.Case == idx);
            foreach (var l in _loads) if (l.Case > idx) l.Case--;
            _cases.RemoveAt(idx);
            if (_cases.Count == 1) { _cases.Clear(); foreach (var l in _loads) l.Case = 0; }
            if (_curCase > idx) _curCase--;
            _curCase = Math.Max(0, Math.Min(_curCase, _cases.Count - 1));
            Changed();
        }

        void SelectCase(int idx)
        {
            if (idx == _curCase) return;
            _curCase = idx;
            if (_mesh != null && _result == null && _topo == null) _view.SetFaceColors(FaceColor);
            BuildSide();
        }

        /// <summary>
        /// 当前工况像不像「多旋翼悬停」：至少 3 个加在不同面上、方向相同的力（每个电机座一个），可以带反扭矩。
        /// 返回每个电机的（力，反扭矩或 null）。
        /// </summary>
        List<(FeLoad force, FeLoad torque)> MotorLoads()
        {
            var cur = CurLoads.ToList();
            var forces = cur.Where(l => l.Kind == LoadKind.Force && !l.AlongNormal && l.Faces.Count > 0).ToList();
            if (forces.Count < 3 || forces.Any(f => (f.Dir.Normalized() - forces[0].Dir.Normalized()).Length > 1e-3)) return null;
            if (forces.SelectMany(f => f.Faces).Distinct().Count() != forces.Sum(f => f.Faces.Count)) return null;
            var torques = cur.Where(l => l.Kind == LoadKind.Torque).ToList();
            return forces.Select(f => (f, torques.FirstOrDefault(t => t.Faces.Count == f.Faces.Count && !t.Faces.Except(f.Faces).Any()))).ToList();
        }

        /// <summary>
        /// 多旋翼常用工况：把当前工况当作悬停（每个电机的推力 T、反扭矩 Q），生成
        /// 满油门（×推重比）、偏航急转（一对电机 1.6 倍、另一对 0.4 倍）、侧面磕碰（一个电机座受 2T 水平侧向力）、硬着陆（每个电机座 3T 向下）。
        /// </summary>
        void AddMultirotorCases(double twr = 2)
        {
            var motors = MotorLoads();
            if (motors == null) throw new InvalidOperationException("当前工况看起来不是多旋翼悬停：需要每个电机座各加一个同方向的力（推力），最好再加反扭矩");
            if (_cases.Count == 0) _cases.Add(new FeLoadCase { Name = "悬停" });
            int hover = _curCase;
            if (_cases[hover].Name.StartsWith("工况")) _cases[hover].Name = "悬停";
            var up = motors[0].force.Dir.Normalized();
            // 零件中心（各电机座的平均位置）和每个电机座的位置
            Vec3 Center(FeLoad l) { var fs = _mesh.Faces.Where(f => l.Faces.Contains(f.Id)).ToList(); var a = fs.Sum(f => f.Area); return fs.Aggregate(Vec3.Zero, (s, f) => s + f.Centroid * f.Area) * (1 / Math.Max(a, 1e-30)); }
            var pos = motors.Select(m => Center(m.force)).ToList();
            var mid = pos.Aggregate(Vec3.Zero, (s, p) => s + p) * (1.0 / pos.Count);
            int NewCase(string name) { _cases.Add(new FeLoadCase { Name = name }); return _cases.Count - 1; }
            void Add(int c, FeLoad l, double k, Vec3? dir = null) { var x = l.Clone(); x.Case = c; x.Value *= k; if (dir != null) x.Dir = dir.Value; _loads.Add(x); }

            int full = NewCase($"满油门（推重比 {twr:0.#}）");
            foreach (var (f, t) in motors) { Add(full, f, twr); if (t != null) Add(full, t, twr); }

            if (motors.Any(m => m.torque != null))
            {
                // 偏航：反扭矩方向和推力同向的那组电机加速、另一组减速（推力、反扭矩都和转速平方成正比）
                int yaw = NewCase("偏航急转");
                foreach (var (f, t) in motors)
                {
                    bool fast = t != null && t.Dir.Dot(up) > 0;
                    Add(yaw, f, fast ? 1.6 : 0.4); if (t != null) Add(yaw, t, fast ? 1.6 : 0.4);
                }
            }
            {
                // 侧面磕碰：第一个电机座受 2T 的水平侧向力（垂直于机臂）
                int hit = NewCase("侧面磕碰");
                var arm = pos[0] - mid; arm = arm - up * arm.Dot(up);
                var side = up.Cross(arm.Length > 1e-9 ? arm.Normalized() : new Vec3(1, 0, 0)).Normalized();
                Add(hit, motors[0].force, 2, side);
            }
            {
                // 硬着陆：起落架 / 电机座着地，每个电机座受 3T 向下（≈ 3 g 冲击，中心的固定孔代表机身的惯性）
                int land = NewCase("硬着陆");
                foreach (var (f, _) in motors) Add(land, f, 3, -up);
            }
            _curCase = hover;
            Changed();
        }

        /// <summary>载荷区上方的工况条：切换、改名、权重、复制（可放大）、删除。</summary>
        void AddCaseBar()
        {
            var sp = new StackPanel();
            bool motorsLike = MotorLoads() != null && !_cases.Any(c => c.Name == "偏航急转" || c.Name == "硬着陆");
            Button MultirotorButton()
            {
                var b = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "+ 生成多旋翼常用工况（满油门 / 偏航 / 磕碰 / 着陆）", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12, Margin = new Thickness(0, 4, 0, 0),
                    ToolTip = "把这个工况当作悬停（每个电机的推力和反扭矩），按推重比 2 推算其他几种常见受力。数值可以在每个工况里再改" };
                b.Click += (s, e) => { try { AddMultirotorCases(); } catch (Exception ex) { _status.Text = ex.Message; _status.Foreground = Orange; } };
                return b;
            }
            if (!MultiCase)
            {
                var add = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "+ 加工况（多种受力情况一起算）", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12 };
                add.Click += (s, e) => AddCase(null, 1);
                sp.Children.Add(add);
                if (motorsLike) sp.Children.Add(MultirotorButton());
                sp.Children.Add(Caption("零件会遇到好几种受力时（例如机架的满油门、偏航急转、硬着陆、侧面磕碰），每种建一个工况：约束共用，载荷分开。计算给出最危险的包络；拓扑优化 / 凸边布局同时兼顾所有工况。", Text3, new Thickness(2, 2, 0, 0)));
                _side.Children.Add(Group(sp));
                return;
            }
            var pills = new WrapPanel();
            var g = "case" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < _cases.Count; i++)
            {
                int n = _loads.Count(l => l.Case == i);
                var rb = Pill($"{_cases[i].Name}（{n}）", g, i == _curCase);
                int ii = i;
                rb.Checked += (s, e) => Root.Dispatcher.BeginInvoke(new Action(() => SelectCase(ii)));
                pills.Children.Add(rb);
            }
            sp.Children.Add(pills);
            var cur = _cases[_curCase];

            // 名字和权重
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            nameRow.Children.Add(new TextBlock { Text = "名称", Width = 44, FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
            var nameBox = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Width = 130, Text = cur.Name };
            void CommitName() { var t = nameBox.Text.Trim(); if (t.Length > 0 && t != cur.Name) { cur.Name = t; Changed(); } else nameBox.Text = cur.Name; }
            nameBox.LostFocus += (s, e) => CommitName();
            nameBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) CommitName(); };
            nameRow.Children.Add(nameBox);
            nameRow.Children.Add(new TextBlock { Text = "权重", Margin = new Thickness(14, 0, 6, 0), FontSize = 12.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
            var wBox = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Width = 48, Text = cur.Weight.ToString("0.##", CultureInfo.InvariantCulture), TextAlignment = TextAlignment.Right, ToolTip = "拓扑优化 / 凸边布局时这个工况有多重要（默认 1）；静力分析不受影响" };
            void CommitW() { if (double.TryParse(wBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0) { if (Math.Abs(v - cur.Weight) > 1e-9) { cur.Weight = v; Changed(); } } else wBox.Text = cur.Weight.ToString("0.##", CultureInfo.InvariantCulture); }
            wBox.LostFocus += (s, e) => CommitW();
            wBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) CommitW(); };
            nameRow.Children.Add(wBox);
            sp.Children.Add(nameRow);

            // 复制 / 新建 / 删除
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var scaleBox = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Width = 44, Text = "1", TextAlignment = TextAlignment.Right, ToolTip = "复制时载荷乘以这个倍数，例如满油门 = 悬停 × 2" };
            var dup = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "复制为新工况 ×", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            dup.Click += (s, e) =>
            {
                double k = double.TryParse(scaleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1;
                AddCase($"{cur.Name}{(Math.Abs(k - 1) > 1e-9 ? $" ×{k:0.##}" : " 副本")}", k);
            };
            actions.Children.Add(dup);
            actions.Children.Add(scaleBox);
            var blank = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "+ 空工况", FontSize = 12, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            blank.Click += (s, e) => AddCase(null, 0);
            actions.Children.Add(blank);
            var del = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "删除这个工况", FontSize = 12, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            del.Click += (s, e) => RemoveCase(_curCase);
            actions.Children.Add(del);
            sp.Children.Add(actions);
            if (motorsLike) sp.Children.Add(MultirotorButton());
            sp.Children.Add(Caption("下面列出的是这个工况的载荷；在左边选面加的载荷也加到这个工况。约束所有工况共用。", Text3, new Thickness(0, 6, 0, 0)));
            _side.Children.Add(Group(sp));
        }

        /// <summary>结果区上方：切换看包络还是某个工况，并列出每个工况的关键数值。</summary>
        void AddResultCaseBar()
        {
            var cr = _result?.CaseResults;
            if (cr == null) return;
            var sp = new StackPanel();
            var pills = new WrapPanel();
            var g = "rcase" + Guid.NewGuid().ToString("N");
            var all = new[] { "包络（最危险）" }.Concat(cr.Select(r => r.CaseName)).ToList();
            for (int i = 0; i < all.Count; i++)
            {
                var rb = Pill(all[i], g, i - 1 == _viewCase);
                int ii = i - 1;
                rb.Checked += (s, e) => Root.Dispatcher.BeginInvoke(new Action(() => { _viewCase = ii; ShowResultView(); BuildSide(); }));
                pills.Children.Add(rb);
            }
            sp.Children.Add(pills);
            var worst = cr.Select((r, i) => (r, i)).OrderBy(t => t.r.SafetyFactor).First();
            foreach (var (r, i) in cr.Select((r, i) => (r, i)))
            {
                string v = _mat.Brittle ? $"拉 {Math.Max(0, r.MaxP1) / 1e6:0.#} MPa" : $"{r.MaxVm / 1e6:0.#} MPa（离开约束 {r.MaxVmAway / 1e6:0.#}）";
                sp.Children.Add(Caption($"{(i == worst.i ? "▲ " : "· ")}{r.CaseName}：{v} · 安全系数 {Sf(r.SafetyFactor)} · 位移 {r.MaxDisp * 1000:0.###} mm",
                    i == worst.i ? (r.SafetyFactor >= 2 ? Green : r.SafetyFactor >= 1 ? Orange : Red) : Gray, new Thickness(0, i == 0 ? 8 : 2, 0, 0)));
            }
            sp.Children.Add(Caption(_viewCase < 0 ? "现在显示的是包络：每个点取所有工况里最危险的值（位移取位移最大的工况）。▲ 是最危险的工况。" : $"现在显示的是「{cr[_viewCase].CaseName}」。", Text3, new Thickness(0, 6, 0, 0)));
            _side.Children.Add(Group(sp));
        }

        // ---- AI 接口 ----

        int CaseIndex(Dictionary<string, object> a, string key = "case")
        {
            if (!a.Has(key)) return _curCase;
            var o = a[key];
            if (o is string s)
            {
                int i = _cases.FindIndex(c => c.Name == s);
                if (i >= 0) return i;
                if (int.TryParse(s, out var n)) o = n; else throw new ArgumentException($"没有这个工况：{s}（现有：{string.Join("、", Enumerable.Range(0, Math.Max(1, _cases.Count)).Select(CaseName))}）");
            }
            int k = Convert.ToInt32(o, CultureInfo.InvariantCulture);
            if (k < 0 || k >= Math.Max(1, _cases.Count)) throw new ArgumentException($"工况编号 {k} 超出范围（0–{Math.Max(1, _cases.Count) - 1}）");
            return k;
        }

        object CasesInfo() => ApiArgs.Ret("current", _curCase, "cases", Enumerable.Range(0, Math.Max(1, _cases.Count)).Select(i => ApiArgs.Ret(
            "index", i, "name", CaseName(i), "weight", i < _cases.Count ? _cases[i].Weight : 1, "loads", _loads.Count(l => (MultiCase ? l.Case : 0) == i))).ToList());

        object CaseApi(string action, Dictionary<string, object> a)
        {
            RequireMesh();
            switch (action)
            {
                case "cases": return CasesInfo();
                case "add_case":
                    {
                        if (a.Has("from")) _curCase = CaseIndex(a, "from");
                        double scale = a.Has("scale") ? a.Num("scale") : a.Bool("copy") ? 1 : 0;
                        AddCase(a.Str("name"), scale, a.Num("weight", 1));
                        return CasesInfo();
                    }
                case "select_case": SelectCase(CaseIndex(a)); return CasesInfo();
                case "set_case":
                    {
                        int i = CaseIndex(a);
                        if (_cases.Count == 0) _cases.Add(new FeLoadCase());
                        if (a.Has("name")) _cases[i].Name = a.Str("name");
                        if (a.Has("weight")) _cases[i].Weight = a.Num("weight");
                        Changed();
                        return CasesInfo();
                    }
                case "remove_case": RemoveCase(CaseIndex(a)); return CasesInfo();
                case "multirotor_cases":
                    if (a.Has("case")) _curCase = CaseIndex(a);
                    AddMultirotorCases(a.Num("twr", 2));
                    return CasesInfo();
                default: throw new ArgumentException("未知操作：" + action);
            }
        }
    }
}
