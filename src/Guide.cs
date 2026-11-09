// 처음 켰을 때 한 번 보여 주는 사용법. 알림 영역 메뉴와 설정에서 다시 볼 수 있다.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bakkum
{
    public class GuideWindow : Window
    {
        static GuideWindow shown;

        public static void Open()
        {
            if (shown != null)
            {
                shown.Activate();
                return;
            }
            shown = new GuideWindow();
            shown.Closed += delegate { shown = null; };
            shown.Show();
            shown.Activate();
            var s = AppSettings.Current;
            if (!s.GuideShown)
            {
                s.GuideShown = true;
                s.SaveQuietly();
            }
        }

        public static Window ForPreview()
        {
            return new GuideWindow();
        }

        GuideWindow()
        {
            var s = AppSettings.Current;
            string key = s.TriggerKey == "Off" ? "Shift" : s.TriggerKey;
            Title = "사용법 · " + AppInfo.Name;
            Width = 800;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Polish(this);
            try { Icon = App.WindowIcon; } catch { }

            var body = new StackPanel();
            var head = Theme.Head("이렇게 써요", "파일을 끌다가 키만 누르면 메뉴가 떠요.");
            head.Margin = new Thickness(0, 0, 0, 22);
            body.Children.Add(head);

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            var steps = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            steps.Children.Add(Step(Theme.Purple, "shape", "파일을 끌다가 " + key, "탐색기에서 파일을 잡고 끄는 동안 " + key + " 키를 누르면 메뉴가 떠요."));
            steps.Children.Add(Step(Theme.Orange, "check", "메뉴에 놓고 고르기", "칸 위에 파일을 놓고 할 일을 고르면 끝이에요. 숫자 키로 골라도 돼요."));
            steps.Children.Add(Step(Theme.Blue, "link", "오른쪽 클릭도 돼요", "파일을 오른쪽 클릭 → 보내기 → " + AppInfo.Name + "을 눌러도 같은 메뉴가 떠요."));
            steps.Children.Add(Step(Theme.Indigo, "record", "화면 녹화는 시계 옆에서", "시계 옆 " + AppInfo.Name + " 아이콘을 오른쪽 클릭 → 화면 녹화를 누르면 돼요."));
            steps.Children.Add(Step(Theme.Green, "shield", "원본은 그대로예요", "결과는 원본 옆에 새 파일로 만들어요."));
            steps.Children.Add(Step(Theme.Sky, "ai", "AI는 고를 때만 써요", "요약·번역·문제 만들기는 설정에서 AI를 연결하면 쓸 수 있고, 보내기 전에 꼭 물어봐요."));
            columns.Children.Add(steps);

            var frame = new Border { Background = Theme.Desk, CornerRadius = new CornerRadius(16), Padding = new Thickness(14), VerticalAlignment = VerticalAlignment.Top };
            var img = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            try { img.Source = MenuWindow.Preview(s.MenuShape, 1, new List<string>(), null, 1); }
            catch (Exception ex) { Log.Write("사용법 그림 실패: " + ex.GetType().Name); }
            frame.Child = img;
            Grid.SetColumn(frame, 1);
            columns.Children.Add(frame);
            body.Children.Add(columns);

            var go = Theme.MakeButton("시작하기", "Primary");
            go.IsDefault = true;
            go.Click += delegate { Close(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            if (!Ai.Configured)
            {
                var ai = Theme.MakeButton("AI 연결하기", "Line");
                ai.Margin = new Thickness(0, 0, 10, 0);
                ai.Click += delegate
                {
                    Close();
                    App.OpenSettings();
                };
                buttons.Children.Add(ai);
            }
            buttons.Children.Add(go);
            body.Children.Add(buttons);

            var card = Theme.Card(body, 28);
            card.Margin = new Thickness(20);
            Content = card;
        }

        static FrameworkElement Step(Hue hue, string glyph, string title, string text)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 15) };
            var chip = Theme.GlyphChip(glyph, hue, 36);
            chip.VerticalAlignment = VerticalAlignment.Top;
            chip.Margin = new Thickness(0, 1, 14, 0);
            DockPanel.SetDock(chip, Dock.Left);
            row.Children.Add(chip);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text(title, 14.5, FontWeights.Bold, Theme.Ink));
            var t = Theme.MetaLabel(text);
            t.FontSize = 13.5;
            t.LineHeight = 20;
            t.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(t);
            row.Children.Add(words);
            return row;
        }
    }
}
