// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 창의 '+로 넣기'와 '아래 빈 곳 단추'(2026-11-13 관리자 요청, 이슈 #69).
// 오른쪽 칸이 보이는 높이보다 3~5배 길어서 넣기·시간 맞추기 단추를 찾으려면 한참 내려야 했다.
// - 시간 막대 줄 이름 옆 연한 + : 자막은 바로 넣고, 글자·그림·소리는 + 위로 작은 고르기 창을 띄워 재생 위치에 넣는다.
//   고르기 창은 창 안 맨 위 층에 그린다(그림 확인에도 찍히고, Esc·바깥 누르기로 닫는다).
// - 창 맨 아래 왼쪽 빈 곳: 고른 것의 [여기서 시작]·[여기서 끝], 자막은 [다음 자막]·[음성으로], 글자·그림은 [맨 앞으로]·[맨 뒤로].
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Bakkum
{
    public sealed partial class VideoEditor
    {
        Grid pickShell;
        Canvas pickLayer;
        FrameworkElement pickCard;
        string pickKind;
        /// 아래 빈 곳 단추 줄과 그 왼쪽 묶음(상태 글이 남은 자리를 쓴다)
        ContentControl footHost;
        StackPanel footLeft;
        /// 오른쪽 칸에서 고른 얹은 것의 설정이 시작되는 곳(+로 넣은 뒤 그곳까지 내려 보인다)
        FrameworkElement decoSelAnchor;
        /// 그림 확인용(/render-window video ... plus:deco|sound hover:sub|deco|sound)
        internal string PreviewPlus, PreviewHover;

        static Hue PlusHue(string kind)
        {
            return kind == "sub" ? Theme.Blue : kind == "deco" ? Theme.Orange : Theme.Green;
        }

        // ══ 고르기 창 층 ══

        /// 창 내용 위에 고르기 창 층을 얹는다. 층은 평소에는 접혀 있어 아무것도 막지 않는다.
        FrameworkElement WithPickLayer(FrameworkElement root)
        {
            pickShell = new Grid();
            pickShell.Children.Add(root);
            pickLayer = new Canvas { Background = Brushes.Transparent, Visibility = Visibility.Collapsed };
            // 고르기 창 바깥을 누르면(왼쪽·오른쪽 모두) 그냥 닫는다. 버릇처럼 두 번 누른 두 번째는 넘긴다.
            pickLayer.PreviewMouseDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (InPicker(e.OriginalSource)) return;
                e.Handled = true;
                if (e.ClickCount < 2) ClosePicker();
            };
            pickLayer.PreviewMouseWheel += delegate (object s, MouseWheelEventArgs e)
            {
                if (InPicker(e.OriginalSource)) return;
                e.Handled = true;
                ClosePicker();
            };
            pickShell.Children.Add(pickLayer);
            return pickShell;
        }

        bool InPicker(object source)
        {
            var d = source as DependencyObject;
            while (d != null)
            {
                if (d == pickCard) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        internal bool PickerOpen
        {
            get { return pickCard != null; }
        }

        internal string PickerKind
        {
            get { return pickKind; }
        }

        /// 시간 막대 줄 이름 옆 + 를 눌렀을 때
        internal void PlusClicked(string kind)
        {
            ClosePicker();
            if (kind == "sub")
            {
                AddSubtitle();
                return;
            }
            OpenPicker(kind);
        }

        /// 글자·그림 또는 소리 고르기 창을 + 위에 연다
        internal void OpenPicker(string kind)
        {
            ClosePicker();
            if (pickLayer == null || (kind != "deco" && kind != "sound")) return;
            // 재생 중이면 멈춘다: 고르는 동안 재생 위치가 흘러가면 누른 때와 다른 곳에 들어간다
            if (preview.Playing) TogglePlay();
            var card = kind == "deco" ? DecoPicker() : SoundPicker();
            pickKind = kind;
            pickCard = card;
            pickLayer.Children.Add(card);
            pickLayer.Visibility = Visibility.Visible;
            PlacePicker(kind);
            // 살짝 톡 커지며 나타난다(DESIGN: 메뉴가 뜰 때 0.6→1이지만, 작은 창이라 0.94→1로 짧게)
            var scale = new ScaleTransform(0.94, 0.94);
            card.RenderTransformOrigin = new Point(0, 1);
            card.RenderTransform = scale;
            card.Opacity = 0;
            var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
            var grow = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }

        internal void ClosePicker()
        {
            if (pickLayer == null || pickCard == null) return;
            pickLayer.Children.Clear();
            pickLayer.Visibility = Visibility.Collapsed;
            pickCard = null;
            pickKind = null;
        }

        /// + 바로 위에 띄운다(+ 왼쪽 끝에 맞춤). 위에 자리가 없으면 + 오른쪽 옆에.
        void PlacePicker(string kind)
        {
            if (pickCard == null || pickShell == null) return;
            pickCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = pickCard.DesiredSize;
            double W = pickShell.ActualWidth, H = pickShell.ActualHeight;
            // 창이 고르기 창보다 낮으면(아주 작은 화면) 잘리지 않게 통째로 줄인다
            double k = H > 0 && W > 0 ? Math.Min(1, Math.Min((H - 16) / size.Height, (W - 16) / size.Width)) : 1;
            if (k < 1)
            {
                k = Math.Max(0.6, k);
                pickCard.LayoutTransform = new ScaleTransform(k, k);
                pickCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                size = pickCard.DesiredSize;
            }
            var r = timeline.PlusRect(kind);
            double x, y;
            if (r.IsEmpty || W <= 0 || H <= 0)
            {
                x = (W - size.Width) / 2;
                y = (H - size.Height) / 2;
            }
            else
            {
                var tl = timeline.TranslatePoint(new Point(r.Left, r.Top), pickShell);
                var br = timeline.TranslatePoint(new Point(r.Right, r.Bottom), pickShell);
                x = tl.X - 10;
                y = tl.Y - 8 - size.Height;
                if (y < 8)
                {
                    x = br.X + 12;
                    y = br.Y - size.Height;
                }
            }
            x = Math.Max(8, Math.Min(W - size.Width - 8, x));
            y = Math.Max(8, Math.Min(H - size.Height - 8, y));
            Canvas.SetLeft(pickCard, x);
            Canvas.SetTop(pickCard, y);
        }

        /// 고르면 창을 닫고 넣는다. deco면 오른쪽 칸을 넣은 것의 설정까지 내려 보인다.
        Action Pick(Action act, bool showSettings)
        {
            return delegate
            {
                ClosePicker();
                act();
                if (showSettings) ShowSelectedSettings();
            };
        }

        /// 오른쪽 칸을 고른 것의 설정이 시작되는 곳까지 내린다(칸을 다시 그린 뒤에)
        void ShowSelectedSettings()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate
            {
                var anchor = decoSelAnchor;
                if (anchor == null || panelScroll == null || !panel.IsAncestorOf(anchor)) return;
                var at = anchor.TranslatePoint(new Point(0, 0), panel);
                panelScroll.ScrollToVerticalOffset(Math.Max(0, at.Y - 6));
            }));
        }

        FrameworkElement PickerFrame(string kind, string title, UIElement body)
        {
            var hue = PlusHue(kind);
            var stack = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var close = new Button
            {
                Style = Theme.GetStyle("Quiet"),
                Width = 34,
                Height = 34,
                Padding = new Thickness(0),
                ToolTip = "닫기 (Esc)",
                Content = Theme.Glyph("close", 16, Theme.Ink2, 2),
                VerticalAlignment = VerticalAlignment.Top
            };
            close.Click += delegate { ClosePicker(); };
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            var chip = Theme.GlyphChip(kind == "deco" ? "sticker" : "music", hue, 32);
            chip.Margin = new Thickness(0, 0, 10, 0);
            chip.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(chip, Dock.Left);
            head.Children.Add(chip);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text(title, 15.5, FontWeights.Bold, Theme.Ink));
            var when = Theme.Text("재생 위치 " + VProject.Clock(CurrentTime, true) + "에 넣어요", 12.5, FontWeights.Normal, Theme.Ink3);
            when.TextWrapping = TextWrapping.NoWrap;
            words.Children.Add(when);
            head.Children.Add(words);
            stack.Children.Add(head);
            stack.Children.Add(body);
            // 흰 카드 + 그 줄 색 옅은 테두리. 아래 화면·시간 막대 위에 떠 있으니 그림자는 카드보다 조금 진하게(한 겹).
            var card = new Grid();
            card.Children.Add(new Border { Background = Theme.Sheet, CornerRadius = new CornerRadius(Look.R(20)), Effect = Theme.Shadow(Theme.InkColor, 26, 8, 0.2) });
            card.Children.Add(new Border
            {
                Background = Theme.Sheet,
                BorderBrush = Look.Outline > 0 ? Theme.Ink : hue.TintBrush,
                BorderThickness = new Thickness(Look.Outline > 0 ? Look.Outline : 1.5),
                CornerRadius = new CornerRadius(Look.R(20)),
                Padding = new Thickness(14),
                Child = stack
            });
            return card;
        }

        static TextBlock PickLabel(string s)
        {
            var t = Theme.Text(s, 12.5, FontWeights.Bold, Theme.Ink2);
            t.Margin = new Thickness(0, 12, 0, 6);
            return t;
        }

        /// 넣기 칸: 쉴 때 옅은 칠, 마우스를 올리면 진한 칠(글·그림은 잉크색)
        static Button PickTile(string glyph, string label, string tip, Hue hue, Action click)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            FrameworkElement g = null;
            if (glyph != null)
            {
                g = Theme.Glyph(glyph, 19, hue.DeepBrush, 2);
                g.VerticalAlignment = VerticalAlignment.Center;
                g.Margin = new Thickness(0, 0, 6, 0);
                sp.Children.Add(g);
            }
            var t = Theme.Text(label, 13.5, FontWeights.Bold, hue.DeepBrush);
            t.TextWrapping = TextWrapping.NoWrap;
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            var b = new Button
            {
                Style = Theme.GetStyle("Quiet"),
                Height = 40,
                Padding = new Thickness(8, 0, 8, 0),
                Content = sp,
                ToolTip = tip,
                Background = hue.TintBrush,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            b.MouseEnter += delegate
            {
                b.Background = hue.VividBrush;
                t.Foreground = Theme.Ink;
                if (g != null) SetStroke(g, Theme.Ink);
            };
            b.MouseLeave += delegate
            {
                b.Background = hue.TintBrush;
                t.Foreground = hue.DeepBrush;
                if (g != null) SetStroke(g, hue.DeepBrush);
            };
            b.Click += delegate { click(); };
            return b;
        }

        static UniformGrid TileRow(params Button[] tiles)
        {
            var g = new UniformGrid { Columns = tiles.Length };
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i].Margin = new Thickness(i == 0 ? 0 : 3, 0, i == tiles.Length - 1 ? 0 : 3, 0);
                g.Children.Add(tiles[i]);
            }
            return g;
        }

        FrameworkElement DecoPicker()
        {
            var hue = PlusHue("deco");
            var body = new StackPanel { Width = 356 };
            body.Children.Add(TileRow(
                PickTile("letter", "제목 글자", "재생 위치에 제목 글자를 넣어요", hue, Pick(AddTitle, true)),
                PickTile("image", "그림 파일", "로고·사진 같은 그림 파일을 골라 얹어요", hue, Pick(AddImageDialog, true)),
                PickTile("frame", "작은 화면", "다른 영상을 작은 화면으로 얹어요", hue, Pick(AddPipDialog, true))));
            body.Children.Add(PickLabel("도형"));
            var shapes = new WrapPanel();
            foreach (var s in Shapes)
            {
                string id = s[0];
                var b = PickTile(null, s[1], s[1] + " 넣기", hue, Pick(delegate { AddShape(id); }, true));
                b.Height = 34;
                b.Padding = new Thickness(11, 0, 11, 0);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(0, 0, 6, 6);
                shapes.Children.Add(b);
            }
            body.Children.Add(shapes);
            var stickerHead = PickLabel("스티커");
            stickerHead.Margin = new Thickness(0, 6, 0, 6);
            body.Children.Add(stickerHead);
            var stickers = new WrapPanel();
            foreach (var s in VOverlay.Stickers)
            {
                string id = s[0];
                // 정답·오답 같은 글자 알약은 네모 칸에 넣으면 글자가 안 보여 두 칸 너비로 크게 보인다
                double aspect = VOverlay.StickerAspect(id);
                bool pill = aspect >= 1.6;
                var img = new Image { Source = pill ? PillImage(id, aspect) : StickerImage(id), Width = pill ? 72 : 30, Height = 30 };
                var b = new Button { Style = Theme.GetStyle("Quiet"), Width = pill ? 86 : 42, Height = 42, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 2), Content = img, ToolTip = s[1] };
                var put = Pick(delegate { AddSticker(id); }, true);
                b.Click += delegate { put(); };
                stickers.Children.Add(b);
            }
            body.Children.Add(stickers);
            return PickerFrame("deco", "글자·그림 넣기", body);
        }

        /// 가로로 긴 스티커를 여백 없이 그린 그림(StickerImage는 정사각형 안에 그려 작아진다)
        static ImageSource PillImage(string id, double aspect)
        {
            var dg = new DrawingGroup();
            using (var dc = dg.Open())
            {
                VOverlay.DrawSticker(dc, id, new Rect(0, 0, 80, 80 / aspect), StickerRgb(id));
            }
            dg.Freeze();
            var img = new DrawingImage(dg);
            img.Freeze();
            return img;
        }

        FrameworkElement SoundPicker()
        {
            var hue = PlusHue("sound");
            var body = new StackPanel { Width = 356 };
            var rec = recorder == null
                ? PickTile("mic", "녹음", "재생 위치부터 보면서 내 목소리를 녹음해요(누르면 마이크를 켜요)", hue, Pick(delegate
                {
                    // 녹음을 끝내는 단추가 소리 칸에 있으니 그 칸을 보인다
                    if (tab != "sound")
                    {
                        Select(null, false);
                        SetTab("sound");
                    }
                    StartRecording();
                }, false))
                : PickTile("mic", "녹음 끝내기", "녹음한 목소리를 넣어요", hue, Pick(delegate { StopRecording(true); }, false));
            body.Children.Add(TileRow(
                PickTile("music", "배경 음악", "음악 파일을 골라 처음부터 끝까지 깔아요", hue, Pick(delegate { AddSoundDialog(true); }, false)),
                PickTile("volume", "소리 파일", "소리 파일을 골라 재생 위치에 넣어요", hue, Pick(delegate { AddSoundDialog(false); }, false)),
                rec));
            body.Children.Add(PickLabel("효과음 · ▶로 미리 들어요"));
            var grid = new UniformGrid { Columns = 3 };
            foreach (var e in VSfx.All)
            {
                string id = e[0];
                var cell = new DockPanel { Margin = new Thickness(0, 0, 6, 6) };
                var play = new Button
                {
                    Style = Theme.GetStyle("Quiet"),
                    Width = 30,
                    Height = 36,
                    Padding = new Thickness(0),
                    ToolTip = "'" + e[1] + "' 미리 듣기",
                    Content = Theme.Glyph("play", 13, hue.DeepBrush, 2)
                };
                play.Click += delegate { PlaySfx(id); };
                DockPanel.SetDock(play, Dock.Left);
                cell.Children.Add(play);
                var add = PickTile(null, e[1], "'" + e[1] + "'을 재생 위치에 넣어요", hue, Pick(delegate { InsertSfx(id, true); }, false));
                add.Height = 36;
                add.Margin = new Thickness(2, 0, 0, 0);
                cell.Children.Add(add);
                grid.Children.Add(cell);
            }
            body.Children.Add(grid);
            return PickerFrame("sound", "소리 넣기", body);
        }

        // ══ 넣기·시간·순서(오른쪽 칸 단추와 아래 빈 곳 단추가 같이 쓴다) ══

        /// 고른 것이 재생 위치에서 시작한다. 자막은 그 음성도 같은 만큼 옮긴다.
        void StartHere()
        {
            double t = CurrentTime;
            var it = SelectedItem;
            if (it != null)
            {
                double least = it.Subtitle ? 0.2 : 0.1;
                SetItem(null, x =>
                {
                    var keep = x.Subtitle ? project.KeepVoices(new[] { x.Id }) : null;
                    x.Start = Math.Min(t, x.End - least);
                    if (keep != null) keep();
                });
                RefreshPanel();
                return;
            }
            if (SelectedSound != null)
            {
                SetSound(null, x => x.Start = t);
                RefreshPanel();
            }
        }

        /// 고른 것이 재생 위치에서 끝난다. 소리는 재생 위치까지만 들리게 뒤를 자른다(되풀이도 끈다).
        void EndHere()
        {
            double t = CurrentTime;
            var it = SelectedItem;
            if (it != null)
            {
                double least = it.Subtitle ? 0.2 : 0.1;
                SetItem(null, x => x.End = Math.Max(t, x.Start + least));
                RefreshPanel();
                return;
            }
            var s = SelectedSound;
            if (s == null) return;
            string why = SoundEndProblem(s, t);
            if (why != null)
            {
                SetStatus(why);
                return;
            }
            double keepLen = t - s.Start;
            SetSound(null, x =>
            {
                x.Out = x.In + keepLen;
                x.Loop = false;
            });
            SetStatus("소리가 " + Clock(t) + "에서 끝나요.");
            RefreshPanel();
        }

        /// 소리의 끝을 재생 위치로 정할 수 없으면 그 까닭(정할 수 있으면 null)
        internal static string SoundEndProblem(VSound s, double t, double sourceSeconds)
        {
            double keep = t - s.Start;
            if (keep < 0.1) return "재생 위치를 이 소리가 시작한 뒤로 옮기면 끝을 정할 수 있어요.";
            // 파일보다 길게는 시간 막대에서 끝을 끌어도 늘어나지 않는다. 되풀이(영상 끝까지)는 '어디까지 되풀이'를 따로 적지 않는다.
            if (keep > sourceSeconds - s.In + 0.01)
                return s.Loop ? "되풀이하는 음악은 파일 한 번 길이 안에서만 끝을 정할 수 있어요." : "소리 파일이 재생 위치 전에 끝나요.";
            return null;
        }

        string SoundEndProblem(VSound s, double t)
        {
            return SoundEndProblem(s, t, SoundSeconds(s.Path));
        }

        /// 다음 자막으로 간다(없으면 이 자막 끝에 새 자막을 넣는다)
        void NextSubtitle()
        {
            var it = SelectedItem;
            if (it == null || !it.Subtitle) return;
            double from = it.Start;
            var nx = project.Items.Where(i => i.Subtitle && i.Start > from + 0.01).OrderBy(i => i.Start).FirstOrDefault();
            if (nx == null)
            {
                SeekTo(it.End);
                AddSubtitle();
                return;
            }
            SeekTo(nx.Start + 0.01);
            Select(nx.Id, true);
            RefreshPanel(true);
        }

        void VoiceSelected()
        {
            var it = SelectedItem;
            if (it == null || !it.Subtitle) return;
            SubtitlesToVoice(new List<string> { it.Id }, false);
        }

        /// 고른 얹은 것을 맨 앞(다른 것 위)·맨 뒤로
        void ItemOrder(bool front)
        {
            var it = SelectedItem;
            if (it == null) return;
            string id = it.Id;
            Change(null, delegate
            {
                var x = project.FindItem(id);
                if (x == null) return;
                project.Items.Remove(x);
                if (front) project.Items.Add(x);
                else project.Items.Insert(0, x);
            });
            SetStatus(front ? "맨 앞으로 옮겼어요. 다른 것 위에 보여요." : "맨 뒤로 옮겼어요. 다른 것 아래에 보여요.");
        }

        /// 효과음을 재생 위치에 넣는다. select면 넣은 것을 고르고 소리 칸을 보인다(+로 넣을 때).
        void InsertSfx(string id, bool select)
        {
            string path;
            try
            {
                path = VSfx.PathOf(id);
            }
            catch (Exception ex)
            {
                Log.Write("효과음 만들기 실패: " + ex.GetType().Name);
                SetStatus("효과음을 만들지 못했어요.");
                return;
            }
            double t = CurrentTime;
            string label = VSfx.Label(id);
            double len = SoundSeconds(path);
            var s = new VSound { Kind = "effect", Path = path, Start = t, In = 0, Out = len, Volume = 0.8, Name = label };
            Change(null, delegate { project.Sounds.Add(s); });
            if (select)
            {
                selected = s.Id;
                selectedRedact = null;
                timeline.Selected = s.Id;
                SetTab("sound");
                timeline.InvalidateVisual();
            }
            SetStatus("효과음 '" + label + "'을 넣었어요.");
            RefreshPanel();
        }

        void PlaySfx(string id)
        {
            // 시험 때는 소리를 내지 않는다
            if (VWaveOut.Silent) return;
            try
            {
                var player = new System.Media.SoundPlayer(VSfx.PathOf(id));
                player.Play();
            }
            catch (Exception ex)
            {
                Log.Write("효과음 미리 듣기 실패: " + ex.GetType().Name);
                SetStatus("효과음을 들려주지 못했어요.");
            }
        }

        // ══ 아래 빈 곳 단추 ══

        /// 창 맨 아래 왼쪽 묶음: [고른 것 단추] [자동 저장 글] [상태 글] [멈추기]
        FrameworkElement BuildFootLeft()
        {
            footLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            footHost = new ContentControl { Focusable = false, IsTabStop = false, VerticalAlignment = VerticalAlignment.Center };
            footLeft.Children.Add(footHost);
            footLeft.SizeChanged += (s, e) =>
            {
                if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) RefreshFoot();
            };
            return footLeft;
        }

        /// 고른 것이 바뀌면(칸을 다시 그릴 때마다) 아래 단추를 다시 만든다. 자리가 좁으면 그림만.
        void RefreshFoot()
        {
            if (footHost == null || project == null) return;
            double room = footLeft.ActualWidth;
            var strip = BuildFootStrip(false);
            // 상태 글(예: '효과음을 넣었어요')이 읽힐 자리(260)는 남긴다
            if (strip != null && room > 0 && Desired(strip) + SaveTextWidth() + 260 > room) strip = BuildFootStrip(true);
            footHost.Content = strip;
            footHost.Margin = new Thickness(0, 0, strip == null ? 0 : 14, 0);
            FitStatus();
        }

        /// 상태 글은 남은 자리까지만(넘치면 … 으로 줄인다)
        void FitStatus()
        {
            if (footLeft == null || statusText == null || footLeft.ActualWidth <= 0) return;
            double strip = footHost.Content == null ? 0 : Desired((FrameworkElement)footHost.Content) + 14;
            // [멈추기]는 방금 보이게 됐으면 아직 너비가 0이라 바라는 너비로 센다
            double cancel = cancelBusyBtn.Visibility == Visibility.Visible ? (cancelBusyBtn.ActualWidth > 0 ? cancelBusyBtn.ActualWidth : Desired(cancelBusyBtn)) + 10 : 0;
            statusText.MaxWidth = Math.Max(80, Math.Min(560, footLeft.ActualWidth - strip - SaveTextWidth() - cancel - 4));
        }

        /// 자동 저장 글이 차지하는 너비(글이 없어도 양옆 여백은 자리를 차지한다)
        double SaveTextWidth()
        {
            if (saveText == null) return 0;
            double text = string.IsNullOrEmpty(saveText.Text) ? 0 : (saveText.ActualWidth > 0 ? saveText.ActualWidth : Desired(saveText));
            return text + saveText.Margin.Left + saveText.Margin.Right;
        }

        /// 고른 것에 맞는 시간·순서 단추(고른 것이 없거나 조각·표시면 null)
        FrameworkElement BuildFootStrip(bool iconsOnly)
        {
            if (tab == "redact" && selectedRedact != null) return null;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var it = SelectedItem;
            if (it != null)
            {
                string what = it.Subtitle ? "자막이" : "고른 것이";
                row.Children.Add(FootButton("setstart", "여기서 시작", what + " 재생 위치에서 시작해요", iconsOnly, StartHere));
                row.Children.Add(FootButton("setend", "여기서 끝", what + " 재생 위치에서 끝나요", iconsOnly, EndHere));
                if (it.Subtitle)
                {
                    row.Children.Add(FootButton("right", "다음 자막", "다음 자막으로 가요(없으면 새 자막을 넣어요)", iconsOnly, NextSubtitle));
                    row.Children.Add(FootButton("volume", "음성으로", "이 자막을 목소리로 읽어 넣어요", iconsOnly, VoiceSelected));
                }
                else
                {
                    row.Children.Add(FootButton("up", "맨 앞으로", "다른 것 위에 보이게 해요", iconsOnly, delegate { ItemOrder(true); }));
                    row.Children.Add(FootButton("chevdown", "맨 뒤로", "다른 것 아래에 보이게 해요", iconsOnly, delegate { ItemOrder(false); }));
                }
                return row;
            }
            if (SelectedSound != null)
            {
                row.Children.Add(FootButton("setstart", "여기서 시작", "소리가 재생 위치에서 시작해요", iconsOnly, StartHere));
                row.Children.Add(FootButton("setend", "여기서 끝", "소리가 재생 위치까지만 들려요", iconsOnly, EndHere));
                return row;
            }
            return null;
        }

        /// 아래 빈 곳 단추: 그림만 보일 때도 이름을 알 수 있게 풍선 도움말 앞에 이름을 붙인다(단추 줄의 '모든 자막 이 자리로'와 같은 꼴)
        Button FootButton(string glyph, string label, string tip, bool iconOnly, Action click)
        {
            var b = BarButton(glyph, label, label + ": " + tip, iconOnly, click);
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            return b;
        }

        List<Button> FootRow()
        {
            var row = footHost == null ? null : footHost.Content as Panel;
            return row == null ? new List<Button>() : row.Children.OfType<Button>().ToList();
        }

        /// 시험용: 아래 빈 곳 단추 이름들
        internal List<string> FootButtons()
        {
            return FootRow().Select(b => System.Windows.Automation.AutomationProperties.GetName(b)).ToList();
        }

        /// 시험용: 아래 빈 곳 단추가 그림만으로 줄었는지
        internal bool FootIconsOnly
        {
            get { return FootRow().Any(b => !(b.Content is StackPanel)); }
        }

        /// 시험용: 아래 빈 곳 단추를 이름으로 누른다
        internal bool ClickFoot(string name)
        {
            var b = FootRow().FirstOrDefault(x => System.Windows.Automation.AutomationProperties.GetName(x) == name);
            if (b == null) return false;
            b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, b));
            return true;
        }

        /// 시험용: 아래 왼쪽 묶음(단추·자동 저장 글·상태 글)이 제 자리 밖으로 잘려 나가는지. 괜찮으면 null.
        internal string FootClippedForTest()
        {
            if (footLeft == null || footLeft.ActualWidth <= 0) return "아래 줄이 아직 자리를 잡지 않았어요";
            double room = footLeft.ActualWidth + 0.5;
            foreach (var e in new FrameworkElement[] { footHost, saveText, statusText })
            {
                if (e == null || e.Visibility != Visibility.Visible || e.ActualWidth <= 0) continue;
                double right = e.TranslatePoint(new Point(e.ActualWidth, 0), footLeft).X;
                if (right > room) return (e == footHost ? "단추" : e == saveText ? "자동 저장 글" : "상태 글") + " 오른쪽 끝 " + right.ToString("0") + " > 자리 " + footLeft.ActualWidth.ToString("0");
            }
            return null;
        }

        internal void StatusForTest(string s)
        {
            SetStatus(s);
        }

        /// 시험용: 고르기 창 자리(창 안 좌표). 닫혀 있으면 Rect.Empty
        internal Rect PickerRectForTest()
        {
            if (pickCard == null) return Rect.Empty;
            var sz = pickCard.ActualWidth > 0 ? new Size(pickCard.ActualWidth, pickCard.ActualHeight) : pickCard.DesiredSize;
            var tr = pickCard.LayoutTransform as ScaleTransform;
            if (pickCard.ActualWidth > 0 && tr != null) sz = new Size(sz.Width * tr.ScaleX, sz.Height * tr.ScaleY);
            return new Rect(Canvas.GetLeft(pickCard), Canvas.GetTop(pickCard), sz.Width, sz.Height);
        }

        /// 시험용: 그 줄 + 단추 자리(창 안 좌표)
        internal Rect PlusInWindowForTest(string kind)
        {
            var r = timeline.PlusRect(kind);
            if (r.IsEmpty) return r;
            var tl = timeline.TranslatePoint(new Point(r.Left, r.Top), pickShell);
            return new Rect(tl, r.Size);
        }

        internal Size ShellSizeForTest
        {
            get { return new Size(pickShell.ActualWidth, pickShell.ActualHeight); }
        }

        internal double PanelOffsetForTest
        {
            get { return panelScroll.VerticalOffset; }
        }

        /// 시험용: 고르기 창 바깥(창 안 아무 곳)을 누른 것처럼
        internal void ClickOutsidePickerForTest()
        {
            if (pickLayer == null) return;
            var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };
            pickLayer.RaiseEvent(e);
        }

        /// 시험용: 고르기 창의 단추를 풍선 도움말 첫머리나 글로 찾아 누른다
        internal bool ClickPicker(string text)
        {
            if (pickCard == null) return false;
            foreach (var b in FindAll<Button>(pickCard))
            {
                string tip = b.ToolTip as string ?? "";
                string label = string.Join("", FindAll<TextBlock>(b).Select(x => x.Text)).Replace("⁠", "");
                if (label != text && !tip.StartsWith(text, StringComparison.Ordinal)) continue;
                b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, b));
                return true;
            }
            return false;
        }

        static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            var t = root as T;
            if (t != null) yield return t;
            var cc = root as ContentControl;
            var content = cc != null ? cc.Content as DependencyObject : null;
            if (content != null)
            {
                foreach (var x in FindAll<T>(content)) yield return x;
                yield break;
            }
            var panelRoot = root as Panel;
            if (panelRoot != null)
            {
                foreach (UIElement c in panelRoot.Children) foreach (var x in FindAll<T>(c)) yield return x;
                yield break;
            }
            var dec = root as Decorator;
            if (dec != null && dec.Child != null) foreach (var x in FindAll<T>(dec.Child)) yield return x;
        }

        // ══ 시험·그림 확인용 다리 ══

        internal VProject ProjectForTest
        {
            get { return project; }
        }

        internal string SelectedId
        {
            get { return selected; }
        }

        internal string TabId
        {
            get { return tab; }
        }

        internal VTimeline TimelineForTest
        {
            get { return timeline; }
        }

        internal void SelectForTest(string id)
        {
            Select(id, true);
        }

        internal void SeekForTest(double t)
        {
            SeekTo(t);
        }

        internal void UndoForTest()
        {
            DoUndo();
        }

        /// 시험용: 창 단축키(Esc 등)를 누른 것처럼
        internal void KeyForTest(Key key)
        {
            var src = PresentationSource.FromVisual(this);
            if (src == null) return;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            RaiseEvent(args);
        }
    }
}
