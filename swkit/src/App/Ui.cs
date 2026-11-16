using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SwKit.App
{
    /// <summary>加载嵌入在 exe 里的 XAML（界面布局和样式），以及常用的动画。</summary>
    static class Ui
    {
        public static object Load(string name)
        {
            var asm = typeof(Ui).Assembly;
            using (var s = asm.GetManifestResourceStream("SwKit." + name))
            {
                if (s == null) throw new InvalidOperationException("缺少界面资源：" + name);
                return XamlReader.Load(s);
            }
        }

        public static T Find<T>(this FrameworkElement root, string name) where T : class =>
            root.FindName(name) as T ?? throw new InvalidOperationException($"界面里找不到 {name}");

        public static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        public static DoubleAnimation To(double to, int ms) =>
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };

        // 每个元素"最终应该显示还是隐藏"。淡出还没结束又要求淡入时，以最后一次的要求为准。
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, object> _fadeTarget =
            new System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, object>();
        static readonly object Shown = new object(), Hidden = new object();

        /// <summary>淡入淡出地显示 / 隐藏。</summary>
        public static void Fade(UIElement el, bool visible, int ms = 180)
        {
            var target = visible ? Shown : Hidden;
            var hasPrev = _fadeTarget.TryGetValue(el, out var prev);
            if (hasPrev && prev == target) return;                                   // 已经在往这个方向走了
            if (!hasPrev && visible == (el.Visibility == Visibility.Visible)) { _fadeTarget.Add(el, target); return; }   // 初始状态就对
            _fadeTarget.Remove(el);
            _fadeTarget.Add(el, target);

            if (visible)
            {
                el.Visibility = Visibility.Visible;
                el.BeginAnimation(UIElement.OpacityProperty, To(1, ms));
            }
            else
            {
                var a = To(0, ms);
                a.Completed += (s, e) =>
                {
                    if (_fadeTarget.TryGetValue(el, out var now) && now == Hidden) el.Visibility = Visibility.Collapsed;
                };
                el.BeginAnimation(UIElement.OpacityProperty, a);
            }
        }

        /// <summary>立即显示 / 隐藏（无动画），同时更新 Fade 记录的目标状态。凡是用过 Fade 的元素，都要用这个代替直接改 Visibility。</summary>
        public static void SetShown(UIElement el, bool visible)
        {
            _fadeTarget.Remove(el);
            _fadeTarget.Add(el, visible ? Shown : Hidden);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>页面进入：淡入并从下方轻轻上移。</summary>
        public static void Enter(FrameworkElement page, double offset = 14, int ms = 300)
        {
            var tt = new TranslateTransform(0, offset);
            page.RenderTransform = tt;
            page.Opacity = 0;
            page.BeginAnimation(UIElement.OpacityProperty, To(1, ms));
            tt.BeginAnimation(TranslateTransform.YProperty, To(0, ms + 60));
        }

        public static SolidColorBrush Brush(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
