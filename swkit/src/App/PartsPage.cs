using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwKit.Core;
using SwKit.Core.Parts;

namespace SwKit.App
{
    /// <summary>"标准件库"页面：零件目录（可搜索）和装配生成（齿轮副、型材框架、同步带）。</summary>
    sealed class PartsPage
    {
        public FrameworkElement Root { get; }
        public bool IsBusy { get; private set; }

        readonly Window _owner;
        readonly SwWorker _worker;
        readonly RobotView _view = new RobotView();
        readonly Dictionary<string, Dictionary<string, string>> _paramsByType = new Dictionary<string, Dictionary<string, string>>();
        readonly StackPanel _typeList, _paramPanel, _recentList;
        readonly TextBlock _title, _info, _note, _status, _libDirText, _recentEmpty, _searchHint, _generateText;
        readonly TextBox _search;
        readonly Button _generate;
        readonly CheckBox _alsoStep, _alsoStl;
        PartType _type;
        Dictionary<string, string> _p;
        AsmPlan _plan;          // 当前装配生成方案（选的是装配生成器时）
        string _libRoot;
        bool _devMode;          // 自测时不写用户设置

        static readonly Brush Gray = Ui.Brush("#6E6E73"), Green = Ui.Brush("#248A3D"), Orange = Ui.Brush("#B25000"), Red = Ui.Brush("#D70015");
        static readonly Dictionary<string, string> CategoryIcon = new Dictionary<string, string>
        {
            ["装配生成"] = "", ["紧固件"] = "", ["轴承"] = "", ["直线运动"] = "", ["传动"] = "",
            ["型材"] = "", ["结构件"] = "", ["电机"] = "", ["电子"] = "",
        };
        static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwKit", "parts-settings.txt");
        static IEnumerable<PartType> AllTypes => Assemblies.All.Cast<PartType>().Concat(Catalog.All);

        public PartsPage(Window owner, SwWorker worker)
        {
            _owner = owner;
            _worker = worker;
            Root = (FrameworkElement)Ui.Load("PartsPage.xaml");
            _typeList = Root.Find<StackPanel>("TypeList");
            _paramPanel = Root.Find<StackPanel>("ParamPanel");
            _recentList = Root.Find<StackPanel>("RecentList");
            _recentEmpty = Root.Find<TextBlock>("RecentEmpty");
            _title = Root.Find<TextBlock>("PartTitle");
            _info = Root.Find<TextBlock>("PartInfo");
            _note = Root.Find<TextBlock>("NoteText");
            _status = Root.Find<TextBlock>("StatusText");
            _libDirText = Root.Find<TextBlock>("LibDirText");
            _generate = Root.Find<Button>("GenerateBtn");
            _generateText = Root.Find<TextBlock>("GenerateText");
            _alsoStep = Root.Find<CheckBox>("AlsoStep");
            _alsoStl = Root.Find<CheckBox>("AlsoStl");
            _search = Root.Find<TextBox>("SearchBox");
            _searchHint = Root.Find<TextBlock>("SearchHint");
            Root.Find<Grid>("ViewHost").Children.Add(_view.Element);
            Root.Find<Button>("ResetViewBtn").Click += (s, e) => _view.ResetView();
            _status.Text = "选择零件和规格，中间会实时预览";

            LoadSettings();
            _libDirText.Text = _libRoot;
            _alsoStep.Checked += (s, e) => SaveSettings(); _alsoStep.Unchecked += (s, e) => SaveSettings();
            _alsoStl.Checked += (s, e) => SaveSettings(); _alsoStl.Unchecked += (s, e) => SaveSettings();
            Root.Find<Button>("ChooseLibBtn").Click += (s, e) =>
            {
                var p = FolderPicker.Pick(_owner, "选择标准件库的保存位置", _libRoot);
                if (p != null) { _libRoot = p; _libDirText.Text = p; SaveSettings(); }
            };
            Root.Find<Button>("OpenFolderBtn").Click += (s, e) =>
            {
                Directory.CreateDirectory(_libRoot);
                Process.Start("explorer.exe", $"\"{_libRoot}\"");
            };
            _generate.Click += async (s, e) => await GenerateAsync();
            _search.TextChanged += (s, e) => { _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; BuildTypeList(); };
            _search.KeyDown += (s, e) =>
            {
                // 回车：选中搜索结果的第一个
                if (e.Key == Key.Enter) { var first = _typeList.Children.OfType<RadioButton>().FirstOrDefault(); if (first != null) first.IsChecked = true; }
                else if (e.Key == Key.Escape) _search.Text = "";
            };

            BuildTypeList();
        }

        // ================= 设置 =================

        void LoadSettings()
        {
            _libRoot = PartsLibrary.DefaultRoot;
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    var i = line.IndexOf('=');
                    if (i < 0) continue;
                    var k = line.Substring(0, i); var v = line.Substring(i + 1);
                    if (k == "lib" && v.Length > 0) _libRoot = v;
                    else if (k == "step") _alsoStep.IsChecked = v == "1";
                    else if (k == "stl") _alsoStl.IsChecked = v == "1";
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            if (_devMode) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[] { "lib=" + _libRoot, "step=" + (_alsoStep.IsChecked == true ? "1" : "0"), "stl=" + (_alsoStl.IsChecked == true ? "1" : "0") }, Encoding.UTF8);
            }
            catch { }
        }

        // ================= 零件类型（带搜索） =================

        void BuildTypeList()
        {
            var text = _search.Text.Trim();
            var types = text.Length == 0 ? AllTypes.ToList()
                      : Assemblies.All.Where(a => Matches(a, text)).Cast<PartType>().Concat(Catalog.Search(text)).ToList();
            _typeList.Children.Clear();
            RadioButton current = null, first = null;
            foreach (var cat in types.Select(t => t.Category).Distinct())
            {
                _typeList.Children.Add(new TextBlock { Text = cat, Style = (Style)Application.Current.FindResource("T.Section"), Margin = new Thickness(10, _typeList.Children.Count == 0 ? 0 : 14, 0, 6) });
                foreach (var t in types.Where(x => x.Category == cat))
                {
                    var rb = new RadioButton
                    {
                        Style = (Style)Application.Current.FindResource("Nav"), GroupName = "partType",
                        Tag = CategoryIcon.TryGetValue(cat, out var ic) ? ic : "",
                        Content = t.Name, ToolTip = t.Standard != null ? t.Name + "  " + t.Standard : t.Name,
                    };
                    var tt = t;
                    if (t == _type) { rb.IsChecked = true; current = rb; }
                    rb.Checked += (s, e) => SelectType(tt);
                    _typeList.Children.Add(rb);
                    if (first == null) first = rb;
                }
            }
            if (types.Count == 0)
                _typeList.Children.Add(new TextBlock { Text = "没有找到。可以搜型号（M3、608、GT2）、名称（轴承、舵机）或标准号（ISO 4762）。", FontSize = 12, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 4, 0) });
            if (_type == null && first != null)
            {
                // 默认选第一个零件（而不是装配生成器），装配生成在列表顶部
                var firstPart = _typeList.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Content == Catalog.All[0].Name) ?? first;
                firstPart.IsChecked = true;
            }
        }

        static bool Matches(PartType t, string text)
        {
            var hay = (t.Name + " " + t.Category + " " + t.Id + " " + t.Keywords + " " + t.Note).ToUpperInvariant();
            return text.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries).All(w => hay.Contains(w.ToUpperInvariant()));
        }

        void SelectType(PartType t)
        {
            _type = t;
            if (!_paramsByType.TryGetValue(t.Id, out _p)) _paramsByType[t.Id] = _p = t.Defaults();
            _note.Text = t.Note;
            _generateText.Text = t is AsmType ? "生成装配体" : "生成零件";
            BuildParams();
            UpdatePreview(keepView: false);
        }

        // ================= 参数 =================

        void BuildParams()
        {
            _paramPanel.Children.Clear();
            if (_type.Standard != null)
                _paramPanel.Children.Add(new TextBlock { Text = _type.Standard, Style = (Style)Application.Current.FindResource("T.Section") });
            var group = new StackPanel();
            _paramPanel.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Group"), Child = group, Margin = new Thickness(0, 0, 0, 6) });

            foreach (var pd in _type.Params)
            {
                var head = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, group.Children.Count == 0 ? 0 : 12, 0, 6), TextWrapping = TextWrapping.Wrap };
                head.Inlines.Add(new System.Windows.Documents.Run(pd.Label) { Foreground = Ui.Brush("#1D1D1F") });
                var extra = string.Join("，", new[] { pd.Unit, pd.Hint }.Where(x => !string.IsNullOrEmpty(x)));
                if (extra.Length > 0) head.Inlines.Add(new System.Windows.Documents.Run("  " + extra) { Foreground = Gray, FontSize = 11.5 });
                group.Children.Add(head);

                if (pd.Kind == ParamKind.Choice)
                {
                    var wrap = new WrapPanel();
                    var gname = "p_" + pd.Key + "_" + Guid.NewGuid().ToString("N");
                    foreach (var c in pd.Choices(_p))
                    {
                        var rb = new RadioButton { Style = (Style)Application.Current.FindResource("Pill"), GroupName = gname, Content = c, IsChecked = _p[pd.Key] == c };
                        var key = pd.Key; var val = c;
                        rb.Checked += (s, e) =>
                        {
                            if (_p[key] == val) return;
                            _p[key] = val;
                            _type.Normalize(_p);
                            // 其他参数的可选项可能变了（例如换规格后长度范围不同），重建参数面板
                            Root.Dispatcher.BeginInvoke(new Action(() => { BuildParams(); UpdatePreview(keepView: true); }));
                        };
                        wrap.Children.Add(rb);
                    }
                    group.Children.Add(wrap);
                }
                else
                {
                    var box = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Width = 96, Text = _p[pd.Key], HorizontalAlignment = HorizontalAlignment.Left, TextAlignment = TextAlignment.Right };
                    var key = pd.Key; var def = pd;
                    void Commit()
                    {
                        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= def.Min && v <= def.Max)
                        {
                            var s = v.ToString("0.###", CultureInfo.InvariantCulture);
                            if (_p[key] == s) { box.Text = s; return; }
                            _p[key] = s;
                            box.Text = s;
                            if (_type is AsmType)
                            {
                                // 装配生成器的可选方案随数值变化（例如中心距变了，齿轮方案跟着变）
                                _type.Normalize(_p);
                                Root.Dispatcher.BeginInvoke(new Action(() => { BuildParams(); UpdatePreview(keepView: true); }));
                            }
                            else UpdatePreview(keepView: true);
                        }
                        else
                        {
                            box.Text = _p[key];
                            _status.Text = $"{def.Label}要在 {def.Min.ToString(CultureInfo.InvariantCulture)} 到 {def.Max.ToString(CultureInfo.InvariantCulture)} 之间";
                            _status.Foreground = Orange;
                        }
                    }
                    box.LostFocus += (s, e) => Commit();
                    box.KeyDown += (s, e) => { if (e.Key == Key.Enter) Commit(); };
                    group.Children.Add(box);
                }
            }
            // 装配生成方案的说明（中心距、切割清单等），在 UpdatePreview 里填
            _planBox = new StackPanel();
            _paramPanel.Children.Add(_planBox);
        }

        StackPanel _planBox;

        // ================= 预览 =================

        bool _previewOk;

        void UpdatePreview(bool keepView)
        {
            _plan = null;
            _planBox?.Children.Clear();
            try
            {
                if (_type is AsmType at)
                {
                    _plan = at.Plan(_p);
                    _title.Text = _plan.Title;
                    var mesh = _plan.Preview();
                    _view.LoadMeshes(mesh.Body, mesh.Cuts, keepView);
                    var parts = _plan.Parts.GroupBy(x => x.Type.Title(x.P)).Count();
                    _info.Text = $"{_plan.Parts.Count} 个零部件（{parts} 种零件）";
                    _info.Foreground = Gray;
                    if (_planBox != null)
                    {
                        _planBox.Children.Add(new TextBlock { Text = "方案", Style = (Style)Application.Current.FindResource("T.Section"), Margin = new Thickness(0, 10, 0, 6) });
                        var sp = new StackPanel();
                        foreach (var line in _plan.Info) sp.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = Ui.Brush("#1D1D1F"), TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 0, 0, 4) });
                        foreach (var line in _plan.Warnings) sp.Children.Add(new TextBlock { Text = "注意：" + line, FontSize = 12, Foreground = Orange, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 0, 0, 4) });
                        _planBox.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Group"), Child = sp });
                    }
                    _previewOk = true;
                    if (!IsBusy) { _status.Text = File.Exists(SwAssemblyBuilder.FilePath(_plan, _libRoot)) ? "这个装配体已经生成过，再点会重新生成（零件已有的直接用）" : "确认方案后点「生成装配体」"; _status.Foreground = Gray; }
                }
                else
                {
                    _title.Text = _type.Title(_p);
                    var g = _type.Build(_p);
                    var mesh = PartMesher.Build(g);
                    _view.LoadMeshes(mesh.Body, mesh.Cuts, keepView);
                    var v = g.Volume();
                    var mat = _type.Material(_p);
                    var info = new StringBuilder();
                    if (mesh.Body.Bounds(out var mn, out var mx))
                        info.Append($"外形 {Mm(mx.X - mn.X)} × {Mm(mx.Z - mn.Z)} × {Mm(mx.Y - mn.Y)} mm（宽 × 深 × 高）");
                    info.Append($" · 体积 {v * 1e9:0.#} mm³");
                    var rho = mat != null ? SwPartBuilder.Density(mat) : 0;
                    if (rho > 0) info.Append($" · 约 {v * rho * 1000:0.##} g（{SwPartBuilder.MaterialText(mat)}）");
                    _info.Text = info.ToString();
                    _info.Foreground = Gray;
                    _previewOk = true;
                    if (!IsBusy) { _status.Text = File.Exists(SwPartBuilder.FilePath(_type, _p, _libRoot)) ? "这个规格已经生成过，点「生成零件」会直接使用已有文件" : "确认无误后点「生成零件」"; _status.Foreground = Gray; }
                }
            }
            catch (Exception ex)
            {
                _view.LoadMeshes(null, null, true);
                _title.Text = _type.Name;
                _info.Text = ex.Message;
                _info.Foreground = Red;
                _previewOk = false;
            }
            _generate.IsEnabled = _previewOk && !IsBusy;
        }

        static string Mm(double m) => (m * 1000).ToString("0.##", CultureInfo.InvariantCulture);

        // ================= 生成 =================

        Task<object> GenerateAsync() => _type is AsmType ? GenerateAssemblyAsync().ContinueWith(t => (object)t.Result, TaskScheduler.FromCurrentSynchronizationContext())
                                                         : GeneratePartAsync().ContinueWith(t => (object)t.Result, TaskScheduler.FromCurrentSynchronizationContext());

        /// <param name="formatsOverride">null = 按界面上的「同时导出」开关；否则按给定格式（"step"、"stl"）。</param>
        public async Task<PartBuildResult> GeneratePartAsync(IEnumerable<string> formatsOverride = null)
        {
            if (IsBusy || !_previewOk || _type is AsmType) return null;
            IsBusy = true;
            _generate.IsEnabled = false;
            var type = _type; var p = new Dictionary<string, string>(_p); var lib = _libRoot;
            var formats = formatsOverride?.ToList() ?? new List<string>();
            if (formatsOverride == null)
            {
                if (_alsoStep.IsChecked == true) formats.Add("step");
                if (_alsoStl.IsChecked == true) formats.Add("stl");
            }
            _status.Text = "正在生成……"; _status.Foreground = Gray;
            try
            {
                var r = await _worker.Run(() =>
                {
                    var path = SwPartBuilder.FilePath(type, p, lib);
                    var needSw = !File.Exists(path) || formats.Any(f => !File.Exists(Path.ChangeExtension(path, f == "step" ? ".step" : ".stl")));
                    if (!needSw) return SwPartBuilder.Build(null, type, p, lib, false, null);
                    var s = _worker.Acquire(m => Status(m.Contains("启动") ? "正在启动后台 SolidWorks（第一次约 20–60 秒）" : m), CancellationToken.None);
                    try
                    {
                        var res = SwPartBuilder.Build(s.App, type, p, lib, false, m => Status("正在" + m));
                        if (formats.Count > 0)
                        {
                            Status("正在导出 " + string.Join(" / ", formats.Select(f => f.ToUpperInvariant())));
                            var opt = new ExportOptions { Formats = formats, OutputToSourceFolder = true, Overwrite = true, CloseLaunchedSw = false };
                            new ExportRunner().Run(s, new List<ExportItem> { new ExportItem(res.Path) }, opt, CancellationToken.None);
                        }
                        return res;
                    }
                    finally { _worker.Release(); }
                });
                AddRecent(r.Path);
                if (r.Reused) { _status.Text = $"已存在，直接使用：{r.Path}"; _status.Foreground = Green; }
                else if (r.VolumeError >= 0.01) { _status.Text = $"已生成，但体积和理论值差 {r.VolumeError:P1}，请检查：{r.Path}"; _status.Foreground = Orange; }
                else
                {
                    _status.Text = $"已生成（{r.Seconds:0.0} 秒，体积核对一致）" + (r.MaterialNote != null ? "，" + r.MaterialNote : "") + "。可以把右侧的文件直接拖进 SolidWorks";
                    _status.Foreground = r.MaterialNote != null ? Orange : Green;
                }
                return r;
            }
            catch (Exception ex)
            {
                _status.Text = "生成失败：" + ex.Message;
                _status.Foreground = Red;
                return null;
            }
            finally
            {
                IsBusy = false;
                _generate.IsEnabled = _previewOk;
            }
        }

        public async Task<AsmBuildResult> GenerateAssemblyAsync()
        {
            if (IsBusy || !_previewOk || _plan == null) return null;
            IsBusy = true;
            _generate.IsEnabled = false;
            var plan = _plan; var lib = _libRoot;
            _status.Text = "正在生成装配体……"; _status.Foreground = Gray;
            try
            {
                var r = await _worker.Run(() =>
                {
                    var s = _worker.Acquire(m => Status(m.Contains("启动") ? "正在启动后台 SolidWorks（第一次约 20–60 秒）" : m), CancellationToken.None);
                    try { return SwAssemblyBuilder.Build(s.App, plan, lib, true, m => Status(m)); }
                    finally { _worker.Release(); }
                });
                AddRecent(r.Path);
                foreach (var f in r.PartFiles.Take(6)) AddRecent(f, top: false);
                var bad = r.Interferences > 0;
                _status.Text = $"已生成装配体（{r.Components} 个零部件，{r.Seconds:0.0} 秒）" + (r.MateNote != null ? "，" + r.MateNote : "，零部件已固定") +
                               (bad ? $"。干涉检查发现 {r.Interferences} 处干涉，请检查" : r.Interferences == 0 ? "，干涉检查通过" : "");
                _status.Foreground = bad ? Orange : Green;
                return r;
            }
            catch (Exception ex)
            {
                _status.Text = "生成失败：" + ex.Message;
                _status.Foreground = Red;
                return null;
            }
            finally
            {
                IsBusy = false;
                _generate.IsEnabled = _previewOk;
            }
        }

        void Status(string text) => Root.Dispatcher.BeginInvoke(new Action(() => { _status.Text = text; _status.Foreground = Gray; }));

        // ================= 已生成（可拖进 SolidWorks） =================

        void AddRecent(string path, bool top = true)
        {
            foreach (var old in _recentList.Children.OfType<FrameworkElement>().Where(x => (string)x.Tag == path).ToList()) _recentList.Children.Remove(old);
            _recentEmpty.Visibility = Visibility.Collapsed;
            var isAsm = path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase);

            var row = new Border { Tag = path, Padding = new Thickness(6, 6, 6, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = path + "\n按住拖进 SolidWorks 的装配体或零件窗口" };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Ui.Brush(isAsm ? "#EEF7EE" : "#EAF3FE"), Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Text = isAsm ? "" : "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 14, Foreground = Ui.Brush(isAsm ? "#248A3D" : "#0071E3"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = isAsm ? "装配体 · 拖进 SolidWorks 打开或插入" : "拖进 SolidWorks 即可插入", FontSize = 11, Foreground = Gray });
            Grid.SetColumn(text, 1); g.Children.Add(text);
            var show = new Button { Style = (Style)Application.Current.FindResource("Btn.Text"), Content = "显示", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            show.Click += (s, e) => Process.Start("explorer.exe", $"/select,\"{path}\"");
            Grid.SetColumn(show, 2); g.Children.Add(show);
            row.Child = g;

            row.MouseEnter += (s, e) => row.Background = Ui.Brush("#F2F2F7");
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            Point down = default; bool pressed = false;
            row.PreviewMouseLeftButtonDown += (s, e) => { down = e.GetPosition(row); pressed = !(e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null); };
            row.PreviewMouseLeftButtonUp += (s, e) => pressed = false;
            row.MouseMove += (s, e) =>
            {
                if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
                var v = e.GetPosition(row) - down;
                if (Math.Abs(v.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(v.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = false;
                if (File.Exists(path)) DragDrop.DoDragDrop(row, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
            };
            if (top) _recentList.Children.Insert(0, row); else _recentList.Children.Add(row);
            while (_recentList.Children.Count > 14) _recentList.Children.RemoveAt(_recentList.Children.Count - 1);
        }

        static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T)) d = VisualTreeHelper.GetParent(d);
            return d as T;
        }

        // ================= 给 AI / 脚本用的接口 =================

        internal static IEnumerable<ApiAction> ApiActions => new[]
        {
            new ApiAction("types", "列出零件类型和装配生成器（可按分类或关键字筛选）", "category?: 分类名, search?: 关键字"),
            new ApiAction("params", "某个类型的参数定义（可选项、范围、默认值）；给 params 时按这些值计算联动的可选项", "type: 类型编号, params?: {参数: 值}"),
            new ApiAction("preview", "在界面上选中类型并填参数，返回预览信息（标题、外形、体积、方案说明），不生成文件", "type, params?"),
            new ApiAction("make", "生成零件（.SLDPRT，可同时导出 STEP / STL），返回文件路径和体积核对结果", "type, params?, formats?: [\"step\", \"stl\"]"),
            new ApiAction("gear_pair_plan", "已知中心距和传动比，列出齿轮副方案（不生成文件）", "a: 中心距 mm, ratio: 传动比 z2/z1, z1?: 小齿轮齿数, m?: 模数"),
            new ApiAction("gear_pair", "按中心距生成齿轮副装配体（自动选方案或用指定的齿数 / 模数）", "a, ratio 或 z1 + z2, m?, b?: 齿宽, d1?, d2?: 孔径, bl?: 齿侧间隙, mat?, plate?: 安装板孔径, plan?: 方案序号（从 0 开始）"),
            new ApiAction("frame", "生成铝型材立方框架装配体，返回切割清单", "L, W, H: 外形 mm, series?: 2020/3030/4040, brackets?: true/false"),
            new ApiAction("belt_drive", "生成同步带传动装配体（自动选标准带长并反算中心距）", "z1, z2, c: 大致中心距 mm, profile?: GT2/HTD3M/HTD5M, w?: 带宽, d1?, d2?"),
            new ApiAction("library", "查看或更改标准件库文件夹", "path?: 新文件夹"),
        };

        internal async Task<object> ApiAsync(string action, Dictionary<string, object> args)
        {
            args = args ?? new Dictionary<string, object>();
            switch (action)
            {
                case "types":
                    {
                        var cat = args.Str("category"); var q = args.Str("search");
                        var list = (q == null ? AllTypes : Assemblies.All.Where(a => Matches(a, q)).Cast<PartType>().Concat(Catalog.Search(q)))
                                   .Where(t => cat == null || t.Category == cat);
                        return ApiArgs.Ret("types", list.Select(t => ApiArgs.Ret("id", t.Id, "name", t.Name, "category", t.Category, "standard", t.Standard, "assembly", t is AsmType)).ToList(),
                                           "categories", AllTypes.Select(t => t.Category).Distinct().ToList());
                    }
                case "params":
                    {
                        var t = TypeArg(args);
                        var p = ParamsArg(t, args);
                        return ApiArgs.Ret("type", t.Id, "name", t.Name, "note", t.Note, "params", t.Params.Select(pd => ApiArgs.Ret(
                            "key", pd.Key, "label", pd.Label, "kind", pd.Kind == ParamKind.Choice ? "choice" : "number", "unit", pd.Unit, "hint", pd.Hint,
                            "choices", pd.Kind == ParamKind.Choice ? pd.Choices(p).ToList() : null, "min", pd.Kind == ParamKind.Number ? (object)pd.Min : null,
                            "max", pd.Kind == ParamKind.Number ? (object)pd.Max : null, "value", p[pd.Key])).ToList());
                    }
                case "preview":
                    {
                        var t = TypeArg(args);
                        Show(t, ParamsArg(t, args));
                        return PreviewInfo();
                    }
                case "make":
                    {
                        var t = TypeArg(args);
                        if (t is AsmType) throw new ArgumentException("装配生成器请用 gear_pair / frame / belt_drive");
                        Show(t, ParamsArg(t, args));
                        if (!_previewOk) throw new ArgumentException(_info.Text);
                        var formats = args.Has("formats") ? args.List("formats").Select(f => f.ToString().ToLowerInvariant()).Where(f => f == "step" || f == "stl").ToList()
                                    : args.Has("format") ? new List<string> { args.Str("format").ToLowerInvariant() }.Where(f => f == "step" || f == "stl").ToList() : new List<string>();
                        var r = await GeneratePartAsync(formats) ?? throw new InvalidOperationException(_status.Text);
                        var files = new List<string> { r.Path };
                        foreach (var f in formats) { var x = Path.ChangeExtension(r.Path, f == "step" ? ".step" : ".stl"); if (File.Exists(x)) files.Add(x); }
                        return ApiArgs.Ret("path", r.Path, "files", files, "reused", r.Reused, "volume_mm3", r.Reused ? (object)null : r.Volume * 1e9,
                                           "expected_mm3", r.Expected * 1e9, "volume_error", r.Reused ? (object)null : r.VolumeError, "material", r.Material, "status", _status.Text);
                    }
                case "gear_pair_plan":
                    {
                        var list = Assemblies.PlanGearPair(args.ReqNum("a") * 0.001, args.Num("ratio", 1), args.Int("z1"), args.Num("m", 0));
                        return ApiArgs.Ret("options", list.Select((o, i) => ApiArgs.Ret("index", i, "label", o.Label, "module", o.M, "z1", o.Z1, "z2", o.Z2, "x1", o.X1, "x2", o.X2,
                            "ratio", o.Ratio, "ratio_error", o.RatioError, "standard_center_distance_mm", o.A0 * 1000, "shifted", o.Shifted, "undercut", o.Undercut)).ToList());
                    }
                case "gear_pair":
                    {
                        // 参数名对照：ratio → i；z1 + z2 → 传动比 z2/z1 并固定小齿轮齿数；plan 是方案序号（不是方案文字）
                        var p = new Dictionary<string, object>();
                        foreach (var k in new[] { "a", "z1", "m", "b", "d1", "d2", "bl", "mat", "plate" }) if (args.Has(k)) p[k] = args[k];
                        if (args.Has("ratio")) p["i"] = args["ratio"];
                        else if (args.Has("i")) p["i"] = args["i"];
                        else if (args.Has("z1") && args.Has("z2")) p["i"] = args.Num("z2") / args.Num("z1");
                        if (!p.ContainsKey("a")) throw new ArgumentException("缺少参数 a（中心距，mm）");
                        var t = Assemblies.GearPair;
                        var values = ParamsArg(t, ApiArgs.Ret("params", p));
                        if (args.Has("plan"))
                        {
                            var choices = t.Params.First(x => x.Key == "plan").Choices(values);
                            var idx = args.Int("plan");
                            if (idx < 0 || idx >= choices.Length) throw new ArgumentException($"plan 要在 0–{choices.Length - 1} 之间");
                            values["plan"] = choices[idx];
                        }
                        return await GenerateAssemblyApi(t, values);
                    }
                case "frame":
                    {
                        var p = new Dictionary<string, object>();
                        foreach (var k in new[] { "l", "w", "h", "series" }) if (args.Has(k)) p[k] = args[k];
                        if (args.Has("L")) p["l"] = args["L"]; if (args.Has("W")) p["w"] = args["W"]; if (args.Has("H")) p["h"] = args["H"];
                        if (args.Has("brackets")) p["brackets"] = args.Bool("brackets") ? "每个连接处 1 个" : "不要";
                        return await GenerateAssemblyApi(Assemblies.Frame, ParamsArg(Assemblies.Frame, ApiArgs.Ret("params", p)));
                    }
                case "belt_drive":
                    return await GenerateAssemblyApi(Assemblies.BeltDrive, ParamsArg(Assemblies.BeltDrive, ApiArgs.Ret("params", args)));
                case "library":
                    if (args.Has("path"))
                    {
                        var dir = Path.GetFullPath(args.Str("path"));
                        Directory.CreateDirectory(dir);
                        _libRoot = dir; _libDirText.Text = dir; SaveSettings();
                        UpdatePreview(keepView: true);
                    }
                    return ApiArgs.Ret("path", _libRoot);
                default:
                    throw new ArgumentException("标准件库没有这个操作：" + action);
            }
        }

        static PartType TypeArg(Dictionary<string, object> args)
        {
            var id = args.ReqStr("type");
            return (PartType)Assemblies.Find(id) ?? Catalog.Find(id) ?? AllTypes.FirstOrDefault(t => t.Name == id) ?? throw new ArgumentException("没有这个类型：" + id + "（用 types 查看全部）");
        }

        /// <summary>默认值 + 调用方给的值（按参数顺序依次设置，再修正到有效范围）。</summary>
        static Dictionary<string, string> ParamsArg(PartType t, Dictionary<string, object> args)
        {
            var given = args.Obj("params");
            var p = t.Defaults();
            foreach (var pd in t.Params)
            {
                if (!given.TryGetValue(pd.Key, out var v) || v == null) continue;
                var s = v is string str ? str : Convert.ToString(v, CultureInfo.InvariantCulture);
                if (pd.Kind == ParamKind.Number)
                {
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw new ArgumentException($"参数 {pd.Key} 应该是数字");
                    if (d < pd.Min || d > pd.Max) throw new ArgumentException($"参数 {pd.Key}（{pd.Label}）要在 {pd.Min.ToString(CultureInfo.InvariantCulture)} 到 {pd.Max.ToString(CultureInfo.InvariantCulture)} 之间");
                    s = d.ToString("0.###", CultureInfo.InvariantCulture);
                }
                else
                {
                    var choices = pd.Choices(p);
                    if (!choices.Contains(s)) throw new ArgumentException($"参数 {pd.Key}（{pd.Label}）只能是：{string.Join(" / ", choices)}");
                }
                p[pd.Key] = s;
                t.Normalize(p);
            }
            foreach (var k in given.Keys.Where(k => t.Params.All(pd => pd.Key != k)))
                throw new ArgumentException($"{t.Name}没有参数 {k}，可用的参数：{string.Join("、", t.Params.Select(pd => pd.Key))}");
            return p;
        }

        /// <summary>在界面上选中类型、填好参数（清空搜索框，让用户看得到）。</summary>
        void Show(PartType t, Dictionary<string, string> p)
        {
            if (IsBusy) throw new InvalidOperationException("标准件库正在生成，请稍后再试");
            if (_search.Text.Length > 0) _search.Text = "";
            _paramsByType[t.Id] = p;
            var rb = _typeList.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Content == t.Name);
            if (rb != null && rb.IsChecked != true) rb.IsChecked = true;   // 触发 SelectType
            else { _type = t; _p = p; _note.Text = t.Note; _generateText.Text = t is AsmType ? "生成装配体" : "生成零件"; BuildParams(); UpdatePreview(keepView: false); }
            if (_p != p) { _p = p; BuildParams(); UpdatePreview(keepView: false); }
        }

        Dictionary<string, object> PreviewInfo() => ApiArgs.Ret("ok", _previewOk, "title", _title.Text, "info", _info.Text,
            "plan", _plan?.Info, "warnings", _plan?.Warnings, "data", _plan?.Data, "file", _plan != null ? SwAssemblyBuilder.FilePath(_plan, _libRoot) : SwPartBuilder.FilePath(_type, _p, _libRoot));

        async Task<object> GenerateAssemblyApi(AsmType t, Dictionary<string, string> p)
        {
            Show(t, p);
            if (!_previewOk) throw new ArgumentException(_info.Text);
            var info = PreviewInfo();
            var r = await GenerateAssemblyAsync() ?? throw new InvalidOperationException(_status.Text);
            info["path"] = r.Path; info["part_files"] = r.PartFiles; info["components"] = r.Components; info["mates"] = r.MatesAdded;
            info["mate_note"] = r.MateNote; info["interferences"] = r.Interferences; info["status"] = _status.Text;
            return info;
        }

        // ================= 自测（开发用） =================

        internal async Task<string> SelfTest(string libRoot)
        {
            _devMode = true;
            _libRoot = libRoot; _libDirText.Text = libRoot;
            var sb = new StringBuilder();
            // 搜索
            _search.Text = "GT2";
            sb.AppendLine($"搜索「GT2」：{string.Join("、", _typeList.Children.OfType<RadioButton>().Select(r => r.Content))}");
            _search.Text = "";
            foreach (var (id, set) in new[] { ("socket_cap", "len=16"), ("timing_pulley", "z=20"), ("linear_rail", "len=200") })
            {
                var r = await ApiAsync("make", ApiArgs.Ret("type", id, "params", set.Split(',').Select(x => x.Split('=')).ToDictionary(x => x[0], x => (object)x[1]))) as Dictionary<string, object>;
                sb.AppendLine($"{_title.Text}：{_status.Text}" + (r?["volume_error"] is double ve ? $"（体积差 {ve:P3}）" : ""));
            }
            // 已生成的零件再导出 STEP 和 STL（文件已存在，只做导出）
            var again = await ApiAsync("make", ApiArgs.Ret("type", "linear_rail", "params", ApiArgs.Ret("len", "200"), "formats", new object[] { "step", "stl" })) as Dictionary<string, object>;
            sb.AppendLine("导出：" + string.Join("、", ((IEnumerable)again["files"]).Cast<string>().Select(Path.GetFileName)));
            // 装配生成：按中心距的齿轮副（界面上显示的最后一步）
            var plan = await ApiAsync("gear_pair_plan", ApiArgs.Ret("a", 41.3, "ratio", 3)) as Dictionary<string, object>;
            sb.AppendLine("齿轮方案：" + string.Join("；", ((IEnumerable)plan["options"]).Cast<Dictionary<string, object>>().Take(3).Select(o => o["label"])));
            var gp = await ApiAsync("gear_pair", ApiArgs.Ret("a", 41.3, "ratio", 3, "plate", 22)) as Dictionary<string, object>;
            sb.AppendLine($"齿轮副：{gp["title"]}，{gp["status"]}");
            return sb.ToString();
        }
    }
}
