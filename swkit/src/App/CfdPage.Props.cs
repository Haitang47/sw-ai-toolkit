using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SwKit.Core.Cfd;
using SwKit.Core.Urdf;

namespace SwKit.App
{
    /// <summary>
    /// 用模型里建好的桨叶生成桨盘：点选桨叶，或者读入时自动识别（见 CfdProps）。
    /// 换成桨盘的桨叶实体计算时不当固体（真实的桨要转起来才有意义，静止的桨叶只会挡风），界面上用橙色标出。
    /// </summary>
    sealed partial class CfdPage
    {
        bool _pickProp;

        double DefaultThrust(int rotors) => _weightG > 0 && rotors > 0 ? Math.Max(0.1, _weightG / 1000 * 9.80665 / rotors) : 4;

        /// <summary>点选的桨叶实体 → 桨盘（已经有这个实体的桨盘就按几何重新算位置、方向、直径）。</summary>
        void AddPropFromPart(int part) => AddPropFromParts(new[] { part }, _disks.FirstOrDefault(d => d.Parts.Contains(part)));

        /// <summary>几个实体（桨叶被拆开时：桨毂 + 叶片）→ 一个桨盘；target 不为空时更新它。</summary>
        void AddPropFromParts(IList<int> parts, CfdDisk target)
        {
            if (_tris == null || _triPart == null || parts.Count == 0) return;
            var s = CfdProps.Combined(_tris, _triPart, parts);
            if (s == null || s.Radius < 0.008)
            {
                _status.Text = "这个实体太小，不像桨叶：点桨叶本身（桨叶和电机是分开的实体时，点在桨叶上）"; _status.Foreground = Orange; return;
            }
            var existing = target;
            var nd = CfdProps.DiskFrom(s, _up, existing?.Name ?? $"桨 {_disks.Count(d => !d.Ducted) + 1}");
            nd.SourcePart = parts[0]; nd.MoreParts = parts.Count > 1 ? parts.Skip(1).ToArray() : null;
            if (existing != null)
            {
                // 保持原来推力方向的那一侧（用户可能已经点过「反向」）
                var ax = nd.Axis.Dot(existing.Axis) < 0 ? -nd.Axis : nd.Axis;
                existing.Center = nd.Center; existing.Axis = ax; existing.Radius = nd.Radius; existing.HubRadius = nd.HubRadius;
                existing.SourcePart = nd.SourcePart; existing.MoreParts = nd.MoreParts;
                nd = existing;
                _selDisk = _disks.IndexOf(existing);
            }
            else
            {
                int rotors = _disks.Count(d => !d.Ducted) + 1;
                nd.Thrust = _disks.FirstOrDefault(d => !d.Ducted)?.Thrust ?? DefaultThrust(4);
                // 相邻的桨反转：和已有的转子交替（之后可以在卡片里改旋向）
                nd.Torque = (rotors % 2 == 1 ? 1 : -1) * EstimateTorque(nd.Thrust, nd.Radius * 2);
                _disks.Add(nd);
                _selDisk = _disks.Count - 1;
            }
            double tilt = Math.Acos(Math.Max(-1, Math.Min(1, nd.Axis.Dot(_up.Normalized())))) * 180 / Math.PI;
            _status.Text = $"{nd.Name}：Φ{nd.Radius * 2000:0} mm，推力方向 ({nd.Axis.X:0.###}, {nd.Axis.Y:0.###}, {nd.Axis.Z:0.###})" + (tilt > 0.5 ? $"，倾斜 {tilt:0.#}°" : "") +
                           "。桨叶实体计算时不当固体（橙色）。继续点下一个桨叶，Esc 结束";
            _status.Foreground = Gray;
            UpdatePropHighlight();
            DisksChanged();
        }

        /// <summary>自动找模型里的桨叶并换成桨盘（替换手动放的转子，涵道保留）。返回找到的个数。</summary>
        int AutoProps(bool quiet)
        {
            if (_tris == null || _triPart == null) return 0;
            var shapes = CfdProps.Shapes(_tris, _triPart);
            var found = CfdProps.Detect(shapes, (_bmax - _bmin).Length);
            if (found.Count == 0)
            {
                if (!quiet) { _status.Text = "没有自动找到桨叶（桨叶可能和电机是同一个实体，或者模型里没有桨）：点「点选模型里的桨叶」手动点，或者用模板 / 手动放桨盘"; _status.Foreground = Orange; }
                return 0;
            }
            // 按绕模型中心的方位角排序，相邻的反转
            var mid = (_bmin + _bmax) / 2;
            Horizontal(out var h1, out var h2);
            var ordered = found.Select(p => shapes[p]).OrderBy(s => Math.Atan2((s.Center - mid).Dot(h2), (s.Center - mid).Dot(h1))).ToList();
            _disks.RemoveAll(d => !d.Ducted);
            double thrust = DefaultThrust(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                var d = CfdProps.DiskFrom(ordered[i], _up, $"桨 {i + 1}");
                d.Thrust = thrust;
                d.Torque = (i % 2 == 0 ? 1 : -1) * EstimateTorque(thrust, d.Radius * 2);
                _disks.Add(d);
            }
            _selDisk = -1;
            UpdatePropHighlight();
            DisksChanged();
            var dia = ordered.Select(s => s.Radius * 2000).ToList();
            _status.Text = $"找到 {ordered.Count} 个桨叶（Φ{dia.Min():0}" + (dia.Max() - dia.Min() > 1 ? $"–{dia.Max():0}" : "") + $" mm），已换成桨盘（橙色），每个推力 {thrust:0.##} N（按起飞重 {_weightG:0} g 平分，可以改）";
            _status.Foreground = Gray;
            return ordered.Count;
        }

        /// <summary>桨叶实体高亮（橙色）。</summary>
        void UpdatePropHighlight()
        {
            if (_tris == null || _triPart == null) { _view.ShowHighlight(null); return; }
            var parts = new HashSet<int>(_disks.SelectMany(d => d.Parts));
            if (parts.Count == 0) { _view.ShowHighlight(null); return; }
            var list = new List<float>();
            for (int t = 0; t < _triPart.Length; t++)
                if (parts.Contains(_triPart[t])) for (int k = 0; k < 9; k++) list.Add(_tris[9 * t + k]);
            _view.ShowHighlight(list.ToArray());
        }

        /// <summary>计算用的几何：去掉已经换成桨盘的桨叶实体。</summary>
        void SolidGeometry(out float[] tris, out int[] triPart)
        {
            var parts = new HashSet<int>(_disks.SelectMany(d => d.Parts));
            if (parts.Count == 0 || _triPart == null) { tris = _tris; triPart = _triPart; return; }
            var tl = new List<float>(_tris.Length); var pl = new List<int>(_triPart.Length);
            for (int t = 0; t < _triPart.Length; t++)
            {
                if (parts.Contains(_triPart[t])) continue;
                for (int k = 0; k < 9; k++) tl.Add(_tris[9 * t + k]);
                pl.Add(_triPart[t]);
            }
            tris = tl.ToArray(); triPart = pl.ToArray();
        }

        void OnPickedTri(int tri, Vec3 p, Vec3 n)
        {
            if (!_pickProp || _triPart == null || tri < 0 || tri >= _triPart.Length) return;
            int part = _triPart[tri];
            // 按住 Ctrl：把这个实体并到当前选中的桨盘（桨叶被拆成桨毂、叶片几个实体时）；再点一次已并入的实体就去掉它
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 && _selDisk >= 0 && _selDisk < _disks.Count && _disks[_selDisk].SourcePart >= 0)
            {
                var d = _disks[_selDisk];
                var list = d.Parts.ToList();
                if (list.Contains(part)) { if (list.Count > 1) list.Remove(part); } else list.Add(part);
                AddPropFromParts(list, d);
                return;
            }
            AddPropFromPart(part);   // 保持点选状态，可以连续点几个桨叶
        }

        /// <summary>桨盘区上方：点选桨叶 / 自动识别。</summary>
        FrameworkElement PropActions()
        {
            var w = new WrapPanel { Margin = new Thickness(2, 0, 0, 6) };
            var pick = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = _pickProp ? "点桨叶……（Ctrl 点：并入同一个桨；Esc 结束）" : "+ 点选模型里的桨叶", Margin = new Thickness(0, 0, 14, 4),
                ToolTip = "在 3D 视图里点桨叶：自动算出盘心、直径和推力方向（斜装 / 倾转的桨也对），这个桨叶实体计算时用桨盘代替" };
            pick.Click += (s, e) =>
            {
                _pickProp = !_pickProp; _picking = false; _pickAxis = false;
                _view.PickMode = _pickProp;
                _status.Text = _pickProp ? "点模型里的桨叶（一个一个点），Esc 结束" : ""; _status.Foreground = Gray;
                BuildSide();
            };
            w.Children.Add(pick);
            var auto = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "自动找桨叶", Margin = new Thickness(0, 0, 14, 4),
                ToolTip = "找扁平、扭转、同样大小的几个实体当作桨叶，换成桨盘（会替换手动放的转子）" };
            auto.Click += (s, e) => AutoProps(false);
            w.Children.Add(auto);
            return w;
        }
    }
}
