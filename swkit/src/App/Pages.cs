using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SwKit.App
{
    /// <summary>"AI 建模提示词"页面和"即将推出"页面。</summary>
    static class Pages
    {
        public static FrameworkElement ComingSoon(string icon, string title, string phase, string summary, IEnumerable<(string title, string desc)> bullets)
        {
            var root = (FrameworkElement)Ui.Load("ComingSoonPage.xaml");
            root.Find<TextBlock>("Icon").Text = icon;
            root.Find<TextBlock>("Title").Text = title;
            root.Find<TextBlock>("Phase").Text = phase;
            root.Find<TextBlock>("Summary").Text = summary;
            var panel = root.Find<StackPanel>("Bullets");
            var list = bullets.ToList();
            for (int i = 0; i < list.Count; i++)
                panel.Children.Add(Row("", list[i].title, list[i].desc, i < list.Count - 1));
            return root;
        }

        static FrameworkElement Row(string mark, string title, string desc, bool divider, bool numbered = false)
        {
            var g = new Grid { Margin = new Thickness(0, 12, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                Background = (Brush)Application.Current.FindResource("B.AccentTint"),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 14, 0),
                Child = new TextBlock
                {
                    Text = mark,
                    FontFamily = numbered ? (FontFamily)Application.Current.FindResource("UiFont") : (FontFamily)Application.Current.FindResource("IconFont"),
                    FontSize = numbered ? 12 : 11, FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.FindResource("B.Accent"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            g.Children.Add(badge);

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("T.Headline") });
            text.Children.Add(new TextBlock { Text = desc, Style = (Style)Application.Current.FindResource("T.Caption"), FontSize = 12.5, LineHeight = 19, Margin = new Thickness(0, 3, 0, 0) });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            if (!divider) return g;
            var sp = new StackPanel();
            sp.Children.Add(g);
            sp.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.FindResource("B.Line"), Margin = new Thickness(38, 0, 0, 0) });
            return sp;
        }
    }
}
