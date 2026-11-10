// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 작은 대화 창(안내·확인)과 라이선스·고지 창. 색종이 디자인: 책상색 바탕 위 흰 카드, 색 칩 머리.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bakkum
{
    public class Dialog : Window
    {
        int answer = -1;

        /// 단추를 왼쪽부터 늘어놓고, 맨 오른쪽(마지막) 단추를 주 단추로 한다.
        /// 누른 단추 번호(0부터), 창을 그냥 닫으면 -1.
        public static int Ask(Window owner, Hue hue, string glyph, string title, string body, FrameworkElement extra, params string[] buttons)
        {
            var d = new Dialog(owner, hue, glyph, title, body, extra, buttons);
            d.ShowDialog();
            return d.answer;
        }

        /// 보기용(그림 확인): 띄우지 않은 창
        public static Window Build(Window owner, Hue hue, string glyph, string title, string body, FrameworkElement extra, params string[] buttons)
        {
            return new Dialog(owner, hue, glyph, title, body, extra, buttons);
        }

        Dialog(Window owner, Hue hue, string glyph, string title, string body, FrameworkElement extra, string[] buttons)
        {
            Title = title + " · " + AppInfo.Name;
            Width = 500;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            if (owner != null && owner.IsLoaded)
            {
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ShowInTaskbar = false;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Polish(this);
            try { Icon = App.WindowIcon; } catch { }

            var stack = new StackPanel();
            stack.Children.Add(Theme.DialogHead(title, null, hue, glyph));
            if (!string.IsNullOrEmpty(body))
            {
                var b = Theme.Body(body);
                b.Margin = new Thickness(58, 6, 0, 0);
                stack.Children.Add(b);
            }
            if (extra != null)
            {
                extra.Margin = new Thickness(58, 14, 0, 0);
                stack.Children.Add(extra);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            Button main = null;
            for (int i = 0; i < buttons.Length; i++)
            {
                int n = i;
                bool last = i == buttons.Length - 1;
                var btn = Theme.MakeButton(buttons[i], last ? "Primary" : "Line");
                if (i > 0) btn.Margin = new Thickness(10, 0, 0, 0);
                btn.Click += delegate
                {
                    answer = n;
                    Close();
                };
                if (last)
                {
                    btn.IsDefault = true;
                    main = btn;
                }
                row.Children.Add(btn);
            }
            stack.Children.Add(row);
            var card = Theme.Card(stack, 26);
            card.Margin = new Thickness(18);
            Content = card;
            Loaded += delegate
            {
                if (main != null) main.Focus();
            };
        }
    }

    public static class NoticeWindow
    {
        public static void Show(Window owner)
        {
            Build(owner).ShowDialog();
        }

        public static Window Build(Window owner)
        {
            var w = new Window
            {
                Title = "라이선스·고지 · " + AppInfo.Name,
                Width = 680,
                Height = 600,
                MinWidth = 420,
                MinHeight = 320
            };
            if (owner != null && owner.IsLoaded)
            {
                w.Owner = owner;
                w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Polish(w);
            try { w.Icon = App.WindowIcon; } catch { }

            var dock = new DockPanel();
            var head = Theme.DialogHead("라이선스·고지", AppInfo.Name + "의 라이선스(GPL-3.0)와 추가 조건, 함께 쓰는 프로그램·상표 안내예요.", Theme.Slate, "box");
            head.Margin = new Thickness(0, 0, 0, 16);
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);
            var close = Theme.MakeButton("닫기", "Primary");
            close.IsCancel = true;
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 16, 0, 0);
            close.Click += delegate { w.Close(); };
            DockPanel.SetDock(close, Dock.Bottom);
            dock.Children.Add(close);
            var box = new TextBox
            {
                Text = SettingsWindow.NoticeText(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = Theme.MonoFont,
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 12, 6, 12),
                Background = Brushes.Transparent,
                Foreground = Theme.Ink2
            };
            dock.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Theme.Sheet2, Padding = new Thickness(2, 4, 2, 4), Child = box });
            var card = Theme.Card(dock, 24);
            card.Margin = new Thickness(18);
            w.Content = card;
            return w;
        }
    }
}
