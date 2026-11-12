// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 창의 '고른 것 단추 줄': 재생 막대 가운데 빈 곳에, 고른 것(자막·글자·그림·조각·소리 등)마다 바로 쓸 단추를 보인다.
// 오른쪽 칸을 내려 찾지 않아도 움직임 표시(키프레임)·복사·지우기를 할 수 있게(2026-11-12 사용자 요청).
// 자리가 좁으면 그림만 보이고, 그래도 넘치면 재생 막대 아래 줄로 내린다.
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Bakkum
{
    public sealed partial class VideoEditor
    {
        ContentControl barHost, barRow;
        Button barKeyBtn, barPrevBtn, barNextBtn;
        Shape barKeyIcon;

        /// 재생 막대(왼쪽 재생 단추들 · 가운데 고른 것 단추 줄 · 오른쪽 나누기·시간 막대 크기)
        FrameworkElement BuildTransport()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var dock = new DockPanel { LastChildFill = true };
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(IconButton("first", "처음으로 (Home)", delegate { SeekTo(0); }));
            left.Children.Add(IconButton("stepback", "한 장 뒤로 (←, Shift+←는 1초)", delegate { Step(-FrameSec); }));
            playBtn = new Button
            {
                Style = Theme.GetStyle("Primary"),
                Width = 50,
                Height = 44,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 3, 0),
                ToolTip = "재생·멈춤 (Space)",
                Content = Theme.Glyph("play", 20, Theme.Sheet, 2.2)
            };
            playBtn.Click += delegate { TogglePlay(); };
            left.Children.Add(playBtn);
            left.Children.Add(IconButton("stepfwd", "한 장 앞으로 (→, Shift+→는 1초)", delegate { Step(FrameSec); }));
            left.Children.Add(IconButton("last", "끝으로 (End)", delegate { SeekTo(project.Duration); }));
            timeText = Theme.Text("0:00.0 / 0:00.0", 14.5, FontWeights.Bold, Theme.Ink);
            timeText.FontFamily = Theme.MonoFont;
            timeText.VerticalAlignment = VerticalAlignment.Center;
            timeText.Margin = new Thickness(12, 0, 0, 0);
            left.Children.Add(timeText);
            DockPanel.SetDock(left, Dock.Left);
            dock.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var split = Theme.MakeButton("여기서 나누기", "Line");
            split.ToolTip = "재생 위치에서 조각을 둘로 나눠요 (S)";
            split.Height = 40;
            split.Click += delegate { SplitHere(); };
            right.Children.Add(split);
            right.Children.Add(IconButton("zoomout", "시간 막대 줄이기 (-)", delegate { timeline.ZoomBy(0.75, CurrentTime); }));
            right.Children.Add(IconButton("zoomin", "시간 막대 늘리기 (+)", delegate { timeline.ZoomBy(1.33, CurrentTime); }));
            right.Children.Add(IconButton("range", "전체가 한눈에 보이게 (0)", delegate { timeline.FitAll(); }));
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);

            // 가운데 빈 곳: 고른 것 단추 줄(마지막 아이라 남은 너비를 다 받는다)
            barHost = new ContentControl { Focusable = false, IsTabStop = false, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
            barHost.SizeChanged += (s, e) =>
            {
                if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) RefreshBar();
            };
            dock.Children.Add(barHost);
            outer.Children.Add(dock);

            // 자리가 모자랄 때 내려오는 둘째 줄
            barRow = new ContentControl { Focusable = false, IsTabStop = false, Margin = new Thickness(0, 6, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
            outer.Children.Add(barRow);
            return outer;
        }

        /// 고른 것이 바뀌면(칸을 다시 그릴 때마다) 단추 줄을 다시 만든다
        void RefreshBar()
        {
            if (barHost == null || project == null) return;
            double room = barHost.ActualWidth;
            // 다 보이게(꼬리표 + 모든 글) → 복사·지우기만 글 → 그림만 → 그래도 모자라면 다 보이게 둘째 줄로
            FrameworkElement content = null;
            bool below = false;
            for (int level = 0; level <= 2; level++)
            {
                bool hint;
                content = BuildBar(level, out hint);
                if (hint || content == null || room <= 0 || Desired(content) <= room) break;
                if (level == 2)
                {
                    content = BuildBar(0, out hint);
                    below = true;
                }
            }
            barHost.Content = below ? null : content;
            barRow.Content = below ? content : null;
            barRow.Visibility = below ? Visibility.Visible : Visibility.Collapsed;
            UpdateBarKeys();
        }

        static double Desired(FrameworkElement e)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return e.DesiredSize.Width;
        }

        /// level 0: 꼬리표 + 모든 단추에 글, 1: 꼬리표 없이 복사·지우기만 글, 2: 그림만(이름은 풍선 도움말).
        /// hint: 아무것도 고르지 않아 안내 글만 있을 때
        FrameworkElement BuildBar(int level, out bool hint)
        {
            hint = false;
            barKeyBtn = barPrevBtn = barNextBtn = null;
            barKeyIcon = null;
            bool noTag = level >= 1, iconsOnly = level >= 2, extraIcon = level >= 1;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var it = SelectedItem;
            if (tab == "redact" && selectedRedact != null)
            {
                AddTag(row, "가리기 상자", noTag);
                row.Children.Add(BarButton("trash", "지우기", "고른 가리기 상자 지우기 (Delete)", iconsOnly, DeleteSelected));
                return row;
            }
            if (it != null)
            {
                AddTag(row, ItemTitle(it), noTag);
                AddKeyButtons(row, extraIcon);
                AddSep(row);
                row.Children.Add(BarButton("copy", "복사", it.Subtitle ? "자막을 복사해 바로 뒤에 붙여요 (Ctrl+D)" : "복사해 살짝 옆에 하나 더 놓아요 (Ctrl+D)", iconsOnly, Duplicate));
                row.Children.Add(BarButton("trash", "지우기", "고른 것 지우기 (Delete)", iconsOnly, DeleteSelected));
                if (it.Subtitle) row.Children.Add(BarButton("subtitle", "모든 자막 이 자리로", "모든 자막 이 자리로: 다른 자막을 모두 이 자막과 같은 자리·너비로 옮겨요(글 모양은 그대로)", extraIcon, AllSubtitlesHere));
                return row;
            }
            var snd = SelectedSound;
            if (snd != null)
            {
                AddTag(row, SoundTitle(snd), noTag);
                row.Children.Add(BarButton("copy", "복사", "소리를 복사해 바로 뒤에 붙여요 (Ctrl+D)", iconsOnly, Duplicate));
                row.Children.Add(BarButton("trash", "지우기", "고른 소리 지우기 (Delete)", iconsOnly, DeleteSelected));
                return row;
            }
            if (SelectedMarker != null)
            {
                AddTag(row, "표시", noTag);
                row.Children.Add(BarButton("trash", "지우기", "고른 표시 지우기 (Delete)", iconsOnly, DeleteSelected));
                return row;
            }
            var clip = KeyClip;
            if (clip != null)
            {
                AddTag(row, SelectedClip != null ? "조각" : "재생 위치의 조각", noTag);
                AddKeyButtons(row, extraIcon);
                if (SelectedClip != null)
                {
                    AddSep(row);
                    row.Children.Add(BarButton("copy", "복사", "조각을 복사해 바로 뒤에 붙여요 (Ctrl+D)", iconsOnly, Duplicate));
                    row.Children.Add(BarButton("trash", "지우기", "고른 조각 지우기 (Delete)", iconsOnly, DeleteSelected));
                }
                return row;
            }
            hint = true;
            var t = Theme.Text("화면이나 시간 막대에서 고르면 여기에 쓸 단추가 나와요", 13, FontWeights.Normal, Theme.Ink3);
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            return t;
        }

        void AddKeyButtons(StackPanel row, bool iconOnly)
        {
            Shape icon;
            barPrevBtn = BarButton("left", null, "앞 움직임 표시(◆)로 가기", true, delegate { JumpKey(-1); }, out icon);
            barKeyBtn = BarButton("keyframe", "움직임 표시", "움직임 표시(◆, 키프레임): 재생 위치에 넣거나 빼요 (K). 표시가 둘 이상이면 그 사이를 저절로 움직여요.", iconOnly, ToggleKey, out barKeyIcon);
            barNextBtn = BarButton("right", null, "뒤 움직임 표시(◆)로 가기", true, delegate { JumpKey(1); }, out icon);
            foreach (var b in new[] { barPrevBtn, barKeyBtn, barNextBtn }) ToolTipService.SetShowOnDisabled(b, true);
            row.Children.Add(barPrevBtn);
            row.Children.Add(barKeyBtn);
            row.Children.Add(barNextBtn);
        }

        void AddTag(StackPanel row, string text, bool skip)
        {
            if (skip || string.IsNullOrEmpty(text)) return;
            var tag = Theme.Tag(text, H);
            tag.VerticalAlignment = VerticalAlignment.Center;
            tag.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(tag);
        }

        static void AddSep(StackPanel row)
        {
            row.Children.Add(new Border { Width = 1, Height = 24, Background = Theme.Line2, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        }

        Button BarButton(string glyph, string label, string tip, bool iconOnly, Action click)
        {
            Shape icon;
            return BarButton(glyph, label, tip, iconOnly, click, out icon);
        }

        /// 단추 줄의 단추: 그림(+글). 그림만일 때는 40×40.
        Button BarButton(string glyph, string label, string tip, bool iconOnly, Action click, out Shape icon)
        {
            var g = Theme.Glyph(glyph, 19, Theme.Ink2, 2);
            icon = g as Shape;
            g.VerticalAlignment = VerticalAlignment.Center;
            object content = g;
            if (!iconOnly && !string.IsNullOrEmpty(label))
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(g);
                var t = Theme.Text(label, 13.5, FontWeights.Bold, Theme.Ink2);
                t.TextWrapping = TextWrapping.NoWrap;
                t.VerticalAlignment = VerticalAlignment.Center;
                t.Margin = new Thickness(6, 0, 0, 0);
                sp.Children.Add(t);
                content = sp;
            }
            var b = new Button
            {
                Style = Theme.GetStyle("Quiet"),
                Height = 40,
                MinWidth = 40,
                Padding = iconOnly ? new Thickness(0) : new Thickness(8, 0, 10, 0),
                ToolTip = tip,
                Content = content,
                Margin = new Thickness(2, 0, 0, 0)
            };
            if (iconOnly) b.Width = 40;
            b.Click += delegate { click(); };
            return b;
        }

        /// 재생 위치에 따라 ◆(채움=이 시각에 표시 있음)와 앞뒤 단추를 맞춘다
        void UpdateBarKeys()
        {
            if (barKeyBtn == null) return;
            int state = KeyStateNow();
            // 흐리게만 하고 누를 수는 있게 둔다: 누르면 왜 안 되는지(재생 위치를 옮기라고) 알림으로 알려 준다
            barKeyBtn.Opacity = state >= 0 ? 1 : 0.45;
            barKeyBtn.ToolTip = state < 0
                ? "움직임 표시(◆): 고른 것이 화면에 보이는 시간 안으로 재생 위치를 옮기면 쓸 수 있어요 (K)"
                : state == 1
                    ? "움직임 표시(◆): 재생 위치에 있는 표시를 빼요 (K)"
                    : "움직임 표시(◆, 키프레임): 재생 위치에 넣어요 (K). 표시가 둘 이상이면 그 사이를 저절로 움직여요.";
            if (barKeyIcon != null)
            {
                barKeyIcon.Fill = state == 1 ? H.VividBrush : null;
                barKeyIcon.Stroke = state == 1 ? H.DeepBrush : Theme.Ink2;
            }
            var times = KeyTimes();
            double t = CurrentTime;
            bool prev = times.Any(x => x < t - VKeys.Near / 2), next = times.Any(x => x > t + VKeys.Near / 2);
            if (barPrevBtn != null)
            {
                barPrevBtn.IsEnabled = prev;
                barPrevBtn.Opacity = prev ? 1 : 0.45;
            }
            if (barNextBtn != null)
            {
                barNextBtn.IsEnabled = next;
                barNextBtn.Opacity = next ? 1 : 0.45;
            }
        }
    }
}
