// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 창. 왼쪽 손질 칸 고르기, 가운데 화면(미리 보기)과 재생 단추, 오른쪽 손질 칸, 아래 시간 막대.
// 모든 바꿈은 Change()를 거쳐 되돌리기 칸에 쌓이고 2초 뒤 자동 저장된다(꺼져도 같은 영상을 열면 이어서 한다).
// 미리 보기는 VPreview가 다른 스레드에서 그리고, 이 창은 받은 장면을 보여 주기만 한다(창이 멈추지 않게).
// 어떤 영상·사진·소리도 이 PC 밖으로 나가지 않는다. 원본은 건드리지 않고 내보낼 때 새 파일을 만든다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shape = System.Windows.Shapes.Shape;

namespace Bakkum
{
    public sealed partial class VideoEditor : Window
    {
        static readonly Hue H = Theme.Teal;
        static readonly uint[] Colors = { 0xFFFFFF, 0x22252F, 0xFFD447, 0xFF8473, 0xFFAA4D, 0x52D3A0, 0x6CA8FF, 0xB596FF, 0xFF8FC2, 0xE5483B, 0x2F7BF0, 0x27B37D };

        static readonly string[][] Tabs =
        {
            new[] { "edit", "편집", "cut" },
            new[] { "subtitle", "자막", "subtitle" },
            new[] { "deco", "글자·그림", "sticker" },
            new[] { "ratio", "화면 비율", "ratio" },
            new[] { "sound", "소리", "music" },
            new[] { "look", "색·효과", "adjust" },
            new[] { "redact", "가리기", "blur" },
            new[] { "teacher", "선생님 도구", "teacher" },
            new[] { "export", "내보내기", "export" }
        };

        readonly MenuAction action;
        VProject project;
        readonly VHistory history = new VHistory();
        int version;
        readonly VPreview preview;
        readonly bool demo;
        string tab = "edit";
        string selected;
        string selectedRedact;
        bool building;
        Action follow;
        string lastKey;
        DateTime lastAt;
        bool changedSinceExport;
        bool scrubbing, playAfterScrub;
        double seekT;
        int shownSerial = -1;
        bool renderingHooked;
        bool showSafe;
        double lastDrawnT = -1;
        bool wasPlaying;
        string frameProblem;

        readonly DispatcherTimer autosaveTimer, pollTimer;

        // 화면
        Grid stageGrid;
        Image frameImage;
        WriteableBitmap bitmap;
        VStage stage;
        TextBlock stageMessage, timeText, statusText, fileText, saveText;
        Button playBtn, undoBtn, redoBtn, cancelBusyBtn;
        ToggleButton magnetBtn, safeBtn, compareBtn, lightBtn, muteBtn;
        VTimeline timeline;
        StackPanel panel;
        ScrollViewer panelScroll;
        readonly Dictionary<string, RadioButton> tabButtons = new Dictionary<string, RadioButton>();
        Rect canvasRect;

        VideoEditor(MenuAction action, VProject p, bool demo)
        {
            this.action = action;
            this.demo = demo;
            project = p;
            Title = "영상 편집 · " + AppInfo.Name;
            var wa = SystemParameters.WorkArea;
            MinWidth = 1060;
            MinHeight = 700;
            Width = Math.Max(MinWidth, Math.Min(1480, wa.Width * 0.96));
            Height = Math.Max(MinHeight, Math.Min(930, wa.Height * 0.95));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Polish(this);
            try { Icon = App.WindowIcon; } catch { }
            preview = new VPreview();
            Content = BuildRoot();
            AllowDrop = true;
            Drop += OnDrop;
            autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            autosaveTimer.Tick += delegate
            {
                autosaveTimer.Stop();
                AutoSave();
            };
            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            pollTimer.Tick += delegate { Poll(); };
            preview.FrameReady = delegate
            {
                if (!preview.Playing) Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(ShowFrame));
            };
            preview.Ended = delegate { Dispatcher.BeginInvoke(new Action(OnEnded)); };
            PreviewKeyDown += OnKey;
            Closing += OnClosing;
            Closed += delegate
            {
                windowOpen = false;
                busyCancel = true;
                proxyStop = true;
                Unhook();
                pollTimer.Stop();
                autosaveTimer.Stop();
                preview.Dispose();
            };
            Loaded += delegate
            {
                timeline.Project = project;
                Touch(false);
                RefreshPanel();
                fileText.Text = Theme.Keep(FilesLine());
                pollTimer.Start();
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    timeline.FitAll();
                    SeekTo(demo ? Math.Min(2, project.Duration * 0.3) : 0);
                }));
                if (demo) DemoReady();
            };
        }

        // ── 열기 ──

        /// 메뉴의 [영상 편집]: 하던 편집이 있으면 이어서 할지 묻고, 없으면 놓은 파일로 새로 만든다.
        public static void OpenFor(MenuAction a, List<string> files)
        {
            VAutoSave.Tidy();
            var full = files.Select(f => Path.GetFullPath(f)).ToList();
            VProject p = null;
            var saved = VAutoSave.Load(full);
            if (saved != null)
            {
                int n = Dialog.Ask(null, H, "media", "하던 편집이 있어요",
                    "이 영상으로 " + saved.Saved.ToString("M월 d일 H:mm") + "까지 편집한 내용이 남아 있어요. 이어서 할까요?", null, "새로 시작", "이어서 하기");
                if (n == 1) p = Relink(saved);
                else if (n == -1) return;
            }
            int faces = 0;
            if (p == null)
            {
                try
                {
                    p = Build(full, out faces);
                }
                catch (UserFacingException ex)
                {
                    Dialog.Ask(null, H, "media", "영상을 열지 못했어요", ex.Message, null, "확인");
                    return;
                }
            }
            var w = new VideoEditor(a, p, false);
            w.Show();
            w.Activate();
            string advice = ProxyAdvice(p.Clips);
            if (faces > 0 || advice != null)
            {
                w.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (faces > 0) w.SetStatus("녹화할 때 같이 찍은 얼굴 영상을 오른쪽 아래에 얹었어요. 화면에서 끌어 옮기거나 골라서 지울 수 있어요." + (advice != null ? " " + advice : ""));
                    else w.SetStatus(advice);
                }));
            }
        }

        /// 그림 확인용(/render-window video): 화면 밖에 띄워 찍는다
        public static VideoEditor ForPreview(MenuAction a, List<string> files, string startTab, bool demoItems)
        {
            var p = Build(files.Select(f => Path.GetFullPath(f)).ToList());
            if (demoItems) AddDemo(p);
            var w = new VideoEditor(a, p, true);
            // SetTab으로 바꿔야 왼쪽 칸 메뉴의 불도 같이 옮겨 간다
            if (!string.IsNullOrEmpty(startTab) && Tabs.Any(x => x[0] == startTab)) w.SetTab(startTab);
            if (demoItems)
            {
                var sub = p.Items.FirstOrDefault(i => i.Subtitle);
                var deco = p.Items.FirstOrDefault(i => !i.Subtitle);
                if (w.tab == "subtitle" && sub != null) w.selected = sub.Id;
                else if (w.tab == "deco" && deco != null) w.selected = deco.Id;
            }
            return w;
        }

        /// 그림 확인용(/render-window video ... panel): 창보다 긴 오른쪽 칸을 끝까지 찍으려고 칸 내용을 창에서 떼어 낸다
        internal FrameworkElement DetachPanelForPreview()
        {
            panelScroll.Content = null;
            return panel;
        }

        static void AddDemo(VProject p)
        {
            double d = p.Duration;
            p.Items.Add(new VItem { Kind = "text", Subtitle = true, Start = 0.5, End = Math.Min(d, 3.4), Text = "오늘은 물의 순환을 알아봐요", X = 0.5, Y = 0.86 });
            p.Items.Add(new VItem { Kind = "text", Subtitle = true, Start = 3.5, End = Math.Min(d, 6.5), Text = "구름은 어떻게 만들어질까요?", X = 0.5, Y = 0.86, Emph = "구름", EmphRgb = 0xFFD447 });
            p.Items.Add(new VItem { Kind = "sticker", Sticker = "t_point", Start = 0.8, End = Math.Min(d, 5), X = 0.84, Y = 0.16, W = 0.16, In = "pop" });
            p.Items.Add(new VItem { Kind = "shape", Shape = "arrow", Start = 1, End = Math.Min(d, 5), X = 0.3, Y = 0.36, W = 0.16, H = -0.12, Rgb = 0xFF8473, Thick = 0.012 });
            p.Markers.Add(new VMarker { T = Math.Min(d * 0.3, 20), Label = "도입" });
            p.Markers.Add(new VMarker { T = Math.Min(d * 0.6, 60), Label = "실험" });
        }

        static VProject Build(List<string> files)
        {
            int faces;
            return Build(files, out faces);
        }

        /// 놓은 파일로 새 편집 만들기: 영상·사진은 차례로 본 영상 줄에, 소리 파일은 배경 음악으로.
        /// 화면 녹화 때 웹캠도 같이 찍었으면 그 얼굴 영상을 같은 때에 동그랗게 얹는다(faces = 얹은 수).
        internal static VProject Build(List<string> files, out int faces)
        {
            var p = new VProject();
            p.Opened = files.ToList();
            var skipped = new List<string>();
            MediaProbe first = null;
            double at = 0;
            faces = 0;
            foreach (var f in files)
            {
                if (VMedia.IsImage(f))
                {
                    p.Clips.Add(new VClip { Kind = "photo", Path = f, Dur = 4, Ken = "in" });
                    at += 4;
                    continue;
                }
                MediaProbe pr;
                try
                {
                    pr = VMedia.Probe(f);
                }
                catch (UserFacingException)
                {
                    skipped.Add(Path.GetFileName(f));
                    continue;
                }
                if (pr == null)
                {
                    skipped.Add(Path.GetFileName(f));
                    continue;
                }
                if (pr.HasVideo)
                {
                    p.Clips.Add(new VClip { Kind = "video", Path = f, In = 0, Out = pr.Seconds });
                    if (first == null) first = pr;
                    if (AttachCam(p, f, at, pr.Seconds)) faces++;
                    at += pr.Seconds;
                }
                else if (pr.HasAudio)
                {
                    p.Sounds.Add(new VSound { Kind = "music", Path = f, Start = 0, In = 0, Out = pr.Seconds, Loop = true, Volume = 0.55, FadeOut = 1.5 });
                }
            }
            if (p.Clips.Count == 0) throw new UserFacingException(skipped.Count > 0 ? "영상을 읽지 못했어요: " + string.Join(", ", skipped) : "편집할 영상이나 사진이 없어요.");
            if (first != null)
            {
                bool side = first.Rotation % 180 != 0;
                p.SourceW = side ? first.Height : first.Width;
                p.SourceH = side ? first.Width : first.Height;
                p.Fps = first.Fps > 1 ? Math.Min(60, first.Fps) : 30;
            }
            else
            {
                p.SourceW = 1920;
                p.SourceH = 1080;
                p.Fps = 30;
            }
            return p;
        }

        /// 화면 녹화 f와 같이 찍은 웹캠 영상이 있으면 그 조각(시간 막대 at부터 len초)에 맞춰 얼굴 작은 화면으로 얹는다.
        /// 웹캠은 찍힌 뒤 조금 늦게 받으므로(Lag) 그만큼 앞에서부터 보여 입 모양을 소리에 맞춘다.
        internal static bool AttachCam(VProject p, string f, double at, double len)
        {
            string cam;
            double lag;
            if (!CamLog.Find(f, out cam, out lag)) return false;
            MediaProbe cp = null;
            try
            {
                cp = VMedia.Probe(cam);
            }
            catch (UserFacingException)
            {
            }
            if (cp == null || !cp.HasVideo || cp.Seconds < lag + 0.5) return false;
            double show = Math.Min(len, cp.Seconds - lag);
            p.Items.Add(new VItem
            {
                Kind = "pip", Path = cam, SrcIn = lag, SrcOut = lag + show, Start = at, End = at + show,
                W = 0.22, X = 0.85, Y = 0.78, Mute = true, Circle = true, Border = true, In = "fade", Out = "fade"
            });
            return true;
        }

        /// 이어서 할 때 원본이 옮겨졌으면 새 자리를 묻는다
        static VProject Relink(VProject p)
        {
            // 읽어 주기 소리는 캐시 폴더에 있어서 다른 PC(로밍 프로필)에는 없다. 묻지 않고 저장된 글로 다시 만든다.
            VTts.Remake(p);
            foreach (var f in p.Files().Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (File.Exists(f)) continue;
                bool voice = p.Sounds.Any(s => s.Kind == "voice" && string.Equals(s.Path, f, StringComparison.OrdinalIgnoreCase));
                int n = voice
                    ? Dialog.Ask(null, H, "media", "읽어 주기 소리를 다시 만들어야 해요", "이 PC에서 읽어 주기 소리를 다시 만들지 못했어요(윈도우 목소리가 없을 수 있어요). 그 소리만 빼고 열어요. 자막은 그대로 남고, 자막 칸의 '모든 자막 읽기'로 다시 넣을 수 있어요.", null, "빼고 열기")
                    : Dialog.Ask(null, H, "media", "원본 파일을 찾지 못했어요", "'" + Path.GetFileName(f) + "' 파일이 옮겨졌거나 이름이 바뀌었어요. 새 자리를 알려 주면 이어서 편집할 수 있어요.", null, "빼고 열기", "찾아보기");
                string found = null;
                if (n == 1)
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Title = Path.GetFileName(f) + " 찾기", FileName = Path.GetFileName(f), CheckFileExists = true };
                    if (dlg.ShowDialog() == true) found = dlg.FileName;
                }
                if (found != null) p.Relink(f, found);
                else
                {
                    var follow = p.Follow();
                    p.Clips.RemoveAll(c => string.Equals(c.Path, f, StringComparison.OrdinalIgnoreCase));
                    p.Items.RemoveAll(i => string.Equals(i.Path, f, StringComparison.OrdinalIgnoreCase));
                    p.Sounds.RemoveAll(s => string.Equals(s.Path, f, StringComparison.OrdinalIgnoreCase));
                    follow();
                }
            }
            return p.Clips.Count > 0 ? p : null;
        }

        string FilesLine()
        {
            var names = project.Opened.Select(Path.GetFileName).ToList();
            if (names.Count == 0) return "";
            string line = names[0];
            if (names.Count > 1) line += " 외 " + (names.Count - 1) + "개";
            return line + " · " + VProject.Clock(project.Duration, false);
        }

        // ── 틀 ──

        FrameworkElement BuildRoot()
        {
            var root = new Grid { Margin = new Thickness(14, 12, 14, 12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(250) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // 왼쪽 칸 메뉴는 시간 막대 옆까지 길게 둔다. 칸이 아홉 개라 화면 위쪽에만 두면
            // 보통 노트북(창 높이 700~780)에서 가리기·선생님 도구·내보내기가 아래로 숨었다(2026-11-12 그림 확인).
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var head = BuildHead();
            Grid.SetRow(head, 0);
            Grid.SetColumnSpan(head, 2);
            root.Children.Add(head);

            var tools = BuildTabs();
            Grid.SetRow(tools, 1);
            Grid.SetRowSpan(tools, 2);
            Grid.SetColumn(tools, 0);
            root.Children.Add(tools);

            var body = new Grid { Margin = new Thickness(12, 0, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            Grid.SetRow(body, 1);
            Grid.SetColumn(body, 1);
            root.Children.Add(body);

            var center = BuildCenter();
            center.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(center, 0);
            body.Children.Add(center);

            panel = new StackPanel();
            panelScroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var side = Theme.Card(panelScroll, 18);
            Grid.SetColumn(side, 1);
            body.Children.Add(side);

            timeline = new VTimeline();
            WireTimeline();
            var tcard = Theme.Card(timeline, 8);
            tcard.Margin = new Thickness(12, 12, 0, 0);
            Grid.SetRow(tcard, 2);
            Grid.SetColumn(tcard, 1);
            root.Children.Add(tcard);

            var foot = BuildFoot();
            Grid.SetRow(foot, 3);
            Grid.SetColumnSpan(foot, 2);
            root.Children.Add(foot);
            return root;
        }

        FrameworkElement BuildHead()
        {
            var dock = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            undoBtn = IconButton("undo", "되돌리기 (Ctrl+Z)", DoUndo);
            redoBtn = IconButton("redo", "다시 하기 (Ctrl+Y)", DoRedo);
            right.Children.Add(undoBtn);
            right.Children.Add(redoBtn);
            right.Children.Add(new Border { Width = 1, Height = 26, Background = Theme.Line2, Margin = new Thickness(8, 0, 4, 0) });
            magnetBtn = IconToggle("magnet", "자석: 끌 때 재생 위치·경계에 붙어요 (Alt를 누르면 잠시 끔)", true, on =>
            {
                timeline.Magnet = on;
            });
            safeBtn = IconToggle("safe", "안전 구역: 휴대폰 앱의 단추·글에 가려지는 곳을 보여요", false, on =>
            {
                showSafe = on;
                stage.InvalidateVisual();
            });
            compareBtn = IconToggle("compare", "보정 전과 나란히 보기(왼쪽 반이 보정 전)", false, on => preview.Compare(on ? project : null));
            lightBtn = IconToggle("speed", "가볍게 보기: 미리 보기를 작게 그려 느린 PC에서도 부드럽게", false, on =>
            {
                preview.Light = on;
                preview.SetBox((int)canvasRect.Width, (int)canvasRect.Height);
            });
            muteBtn = IconToggle("mute", "미리 볼 때 소리 끄기", false, on => preview.MuteAudio = on);
            right.Children.Add(magnetBtn);
            right.Children.Add(safeBtn);
            right.Children.Add(compareBtn);
            right.Children.Add(lightBtn);
            right.Children.Add(muteBtn);
            right.Children.Add(IconButton("info", "단축키 (F1)", ShowKeys));
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);
            var chip = Theme.GlyphChip("media", H, 40);
            chip.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(chip, Dock.Left);
            dock.Children.Add(chip);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text("영상 편집", 18, FontWeights.Bold, Theme.Ink));
            fileText = Theme.MetaLabel("");
            fileText.TextWrapping = TextWrapping.NoWrap;
            fileText.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(fileText);
            dock.Children.Add(words);
            return dock;
        }

        static Button IconButton(string glyph, string tip, Action click)
        {
            var b = new Button
            {
                Style = Theme.GetStyle("Quiet"),
                Width = 40,
                Height = 40,
                Padding = new Thickness(0),
                ToolTip = tip,
                Content = Theme.Glyph(glyph, 20, Theme.Ink2, 2),
                Margin = new Thickness(3, 0, 0, 0)
            };
            b.Click += delegate { click(); };
            return b;
        }

        static ToggleButton IconToggle(string glyph, string tip, bool on, Action<bool> changed)
        {
            var icon = Theme.Glyph(glyph, 20, Theme.Ink2, 2);
            var face = new Border { CornerRadius = new CornerRadius(12), Width = 40, Height = 40, Child = icon };
            var b = new ToggleButton
            {
                Width = 40,
                Height = 40,
                Margin = new Thickness(3, 0, 0, 0),
                ToolTip = tip,
                IsChecked = on,
                Cursor = Cursors.Hand,
                Focusable = false,
                Template = BareToggleTemplate()
            };
            b.Content = face;
            Action look = delegate
            {
                bool c = b.IsChecked == true;
                face.Background = c ? H.TintBrush : (b.IsMouseOver ? Theme.Frozen(Theme.Hex(0xEFEAE2)) : Brushes.Transparent);
                var shape = icon as Shape;
                if (shape != null) shape.Stroke = c ? H.DeepBrush : Theme.Ink2;
                else SetStroke(icon, c ? H.DeepBrush : Theme.Ink2);
            };
            b.Checked += delegate { look(); changed(true); };
            b.Unchecked += delegate { look(); changed(false); };
            b.MouseEnter += delegate { look(); };
            b.MouseLeave += delegate { look(); };
            look();
            return b;
        }

        static void SetStroke(DependencyObject o, Brush b)
        {
            var s = o as Shape;
            if (s != null)
            {
                s.Stroke = b;
                return;
            }
            int n = VisualTreeHelper.GetChildrenCount(o);
            if (n == 0)
            {
                var panel = o as Panel;
                if (panel != null) foreach (UIElement c in panel.Children) SetStroke(c, b);
                var dec = o as Decorator;
                if (dec != null && dec.Child != null) SetStroke(dec.Child, b);
                return;
            }
            for (int i = 0; i < n; i++) SetStroke(VisualTreeHelper.GetChild(o, i), b);
        }

        static ControlTemplate BareToggleTemplate()
        {
            var t = new ControlTemplate(typeof(ToggleButton));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            t.VisualTree = cp;
            return t;
        }

        FrameworkElement BuildTabs()
        {
            var stack = new StackPanel();
            foreach (var t in Tabs)
            {
                string id = t[0];
                var words = new TextBlock
                {
                    Text = Theme.Keep(t[1]),
                    FontSize = 12.5,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                // 아홉 칸이 가장 작은 창(높이 700)에서도 다 보이는 크기(그림 21→20, 칸 사이 3→1)
                var icon = Theme.Glyph(t[2], 20, Theme.Ink2, 1.9);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                var bind = new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) };
                var shape = icon as Shape;
                if (shape != null) BindingOperations.SetBinding(shape, Shape.StrokeProperty, bind);
                var content = new StackPanel();
                content.Children.Add(icon);
                content.Children.Add(words);
                var r = new RadioButton
                {
                    Style = Theme.GetStyle("ToolTab"),
                    Tag = H,
                    GroupName = "vtab",
                    Content = content,
                    Margin = new Thickness(0, 0, 0, 1),
                    IsChecked = id == tab
                };
                r.Checked += delegate
                {
                    if (building) return;
                    tab = id;
                    RefreshPanel();
                    stage.InvalidateVisual();
                };
                tabButtons[id] = r;
                stack.Children.Add(r);
            }
            var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            return Theme.Card(scroll, 7);
        }

        void SetTab(string id)
        {
            if (tab == id) return;
            tab = id;
            building = true;
            try
            {
                RadioButton r;
                if (tabButtons.TryGetValue(id, out r))
                {
                    r.IsChecked = true;
                    // 아주 작은 창에서 칸 메뉴가 넘쳐도, 바뀐 칸은 보이게
                    r.BringIntoView();
                }
            }
            finally
            {
                building = false;
            }
        }

        FrameworkElement BuildCenter()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var outer = new Border { Background = Theme.Frozen(Theme.Hex(0xE9E4DB)), CornerRadius = new CornerRadius(20) };
            stageGrid = new Grid { ClipToBounds = true };
            frameImage = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            RenderOptions.SetBitmapScalingMode(frameImage, BitmapScalingMode.Linear);
            stageGrid.Children.Add(new Border { Background = Brushes.Transparent });
            stageGrid.Children.Add(frameImage);
            stage = new VStage(this);
            stageGrid.Children.Add(stage);
            stageMessage = Theme.Text("", 14.5, FontWeights.Bold, Theme.Ink2);
            stageMessage.HorizontalAlignment = HorizontalAlignment.Center;
            stageMessage.VerticalAlignment = VerticalAlignment.Bottom;
            stageMessage.TextAlignment = TextAlignment.Center;
            stageMessage.Margin = new Thickness(30, 0, 30, 14);
            stageMessage.IsHitTestVisible = false;
            stageGrid.Children.Add(stageMessage);
            stageGrid.SizeChanged += delegate { LayoutCanvas(); };
            outer.Child = stageGrid;
            g.Children.Add(outer);

            var bar = BuildTransport();
            Grid.SetRow(bar, 1);
            g.Children.Add(bar);
            return g;
        }

        FrameworkElement BuildTransport()
        {
            var dock = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
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
            return dock;
        }

        FrameworkElement BuildFoot()
        {
            var dock = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var close = Theme.MakeButton("닫기", "Line");
            close.Click += delegate { Close(); };
            right.Children.Add(close);
            var same = Theme.MakeButton("다른 영상에도 똑같이", "Line");
            same.Margin = new Thickness(8, 0, 0, 0);
            same.ToolTip = "화면 비율·색·로고·음악·내보내기 설정을 다른 영상에도 똑같이 넣어 따로따로 만들어요";
            same.Click += delegate { ApplyToOtherFiles(); };
            right.Children.Add(same);
            var export = Theme.MakeButton("내보내기", "Primary");
            export.Margin = new Thickness(8, 0, 0, 0);
            export.Click += delegate
            {
                if (tab != "export")
                {
                    SetTab("export");
                    RefreshPanel();
                }
                else StartExport();
            };
            right.Children.Add(export);
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            saveText = Theme.Text("", 13, FontWeights.Normal, Theme.Ink3);
            saveText.VerticalAlignment = VerticalAlignment.Center;
            saveText.Margin = new Thickness(2, 0, 14, 0);
            left.Children.Add(saveText);
            statusText = Theme.Text("", 13.5, FontWeights.Bold, H.DeepBrush);
            statusText.VerticalAlignment = VerticalAlignment.Center;
            statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            statusText.MaxWidth = 560;
            left.Children.Add(statusText);
            cancelBusyBtn = Theme.MakeButton("멈추기", "Line");
            cancelBusyBtn.Height = 34;
            cancelBusyBtn.Margin = new Thickness(10, 0, 0, 0);
            cancelBusyBtn.Visibility = Visibility.Collapsed;
            cancelBusyBtn.Click += delegate { busyCancel = true; };
            left.Children.Add(cancelBusyBtn);
            dock.Children.Add(left);
            return dock;
        }

        void SetStatus(string s)
        {
            statusText.Text = Theme.Keep(s ?? "");
        }

        // ── 화면 자리 ──

        void LayoutCanvas()
        {
            double w = stageGrid.ActualWidth, h = stageGrid.ActualHeight;
            if (w < 10 || h < 10 || project == null) return;
            double pad = 16;
            double a = project.AspectRatio;
            double cw = w - pad * 2, ch = cw / a;
            if (ch > h - pad * 2)
            {
                ch = h - pad * 2;
                cw = ch * a;
            }
            canvasRect = new Rect((w - cw) / 2, (h - ch) / 2, Math.Max(1, cw), Math.Max(1, ch));
            frameImage.Margin = new Thickness(canvasRect.X, canvasRect.Y, 0, 0);
            frameImage.Width = canvasRect.Width;
            frameImage.Height = canvasRect.Height;
            var src = PresentationSource.FromVisual(this);
            double dpi = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
            preview.SetBox((int)(canvasRect.Width * dpi), (int)(canvasRect.Height * dpi));
            stage.InvalidateVisual();
        }

        // ── 시간 ──

        double FrameSec
        {
            get { return 1.0 / Math.Max(10, Math.Min(60, project.Fps)); }
        }

        public double CurrentTime
        {
            get { return preview.Playing ? preview.Now : seekT; }
        }

        void SeekTo(double t)
        {
            double d = project.Duration;
            t = Math.Max(0, Math.Min(Math.Max(0, d - 1e-3), t));
            seekT = t;
            preview.Seek(t);
            timeline.SetPlayhead(t, true);
            UpdateTime();
            stage.InvalidateVisual();
        }

        void Step(double dt)
        {
            if (preview.Playing) preview.Pause();
            SeekTo(CurrentTime + dt);
        }

        void UpdateTime()
        {
            double t = CurrentTime;
            timeText.Text = VProject.Clock(t, true) + " / " + VProject.Clock(project.Duration, true);
        }

        void TogglePlay()
        {
            if (preview.Playing)
            {
                preview.Pause();
                seekT = preview.Now;
                Unhook();
                ShowFrame();
                UpdatePlayButton();
                return;
            }
            double from = seekT;
            if (from >= project.Duration - 0.05) from = 0;
            double end = -1;
            bool loop = false;
            if (timeline.RangeA >= 0 && timeline.RangeB > timeline.RangeA && from >= timeline.RangeA - 0.01 && from < timeline.RangeB)
            {
                end = timeline.RangeB;
                loop = true;
            }
            preview.Play(from, end, loop);
            Hook();
            UpdatePlayButton();
        }

        void UpdatePlayButton()
        {
            bool p = preview.Playing;
            if (p == wasPlaying && playBtn.Content != null) return;
            wasPlaying = p;
            playBtn.Content = Theme.Glyph(p ? "pause" : "play", 20, Theme.Sheet, 2.2);
        }

        void OnEnded()
        {
            seekT = preview.Now;
            Unhook();
            ShowFrame();
            UpdatePlayButton();
            UpdateTime();
        }

        void Hook()
        {
            if (renderingHooked) return;
            CompositionTarget.Rendering += OnRendering;
            renderingHooked = true;
        }

        void Unhook()
        {
            if (!renderingHooked) return;
            CompositionTarget.Rendering -= OnRendering;
            renderingHooked = false;
        }

        void OnRendering(object s, EventArgs e)
        {
            ShowFrame();
            if (!preview.Playing && !scrubbing)
            {
                Unhook();
                UpdatePlayButton();
            }
        }

        /// 미리 보기 일꾼이 내놓은 장면을 화면에(잠겨 있으면 다음 차례에)
        void ShowFrame()
        {
            bool playing = preview.Playing;
            double t = playing ? preview.Now : seekT;
            if (Monitor.TryEnter(preview.FrameLock, 0))
            {
                try
                {
                    if (preview.FrameSerial != shownSerial)
                    {
                        shownSerial = preview.FrameSerial;
                        int fw = preview.FrameW, fh = preview.FrameH;
                        if (preview.Frame != null && fw > 0 && fh > 0 && preview.Frame.Length >= fw * fh * 4)
                        {
                            if (bitmap == null || bitmap.PixelWidth != fw || bitmap.PixelHeight != fh)
                            {
                                bitmap = new WriteableBitmap(fw, fh, 96, 96, PixelFormats.Bgr32, null);
                                frameImage.Source = bitmap;
                            }
                            bitmap.WritePixels(new Int32Rect(0, 0, fw, fh), preview.Frame, fw * 4, 0);
                        }
                        frameProblem = preview.Problem;
                    }
                }
                finally
                {
                    Monitor.Exit(preview.FrameLock);
                }
            }
            if (playing || Math.Abs(t - lastDrawnT) > 1e-6)
            {
                lastDrawnT = t;
                timeline.SetPlayhead(t, playing);
                UpdateTime();
                stage.InvalidateVisual();
            }
            string msg = frameProblem;
            if (msg == null && preview.AudioPending && playing) msg = "소리를 읽는 중이에요. 잠시 뒤부터 소리가 나요.";
            stageMessage.Text = Theme.Keep(msg ?? "");
            if (playing != wasPlaying) UpdatePlayButton();
        }

        /// 0.6초마다: 그림 띠·소리 모양이 만들어지는 동안 시간 막대를 다시 그린다
        void Poll()
        {
            bool loading = false;
            foreach (var c in project.Clips)
            {
                if (c.Kind != "video" || string.IsNullOrEmpty(c.Path) || !File.Exists(c.Path)) continue;
                var f = Filmstrip.Get(c.Path);
                if (!f.Done) loading = true;
                var a = AudioCache.Get(c.Path);
                if (!a.Ready && !a.Failed) loading = true;
            }
            foreach (var s in project.Sounds)
            {
                if (string.IsNullOrEmpty(s.Path) || !File.Exists(s.Path)) continue;
                var a = AudioCache.Get(s.Path);
                if (!a.Ready && !a.Failed) loading = true;
            }
            if (loading || pollAgain)
            {
                timeline.InvalidateVisual();
                pollAgain = loading;
            }
            PollTeacher();
        }

        bool pollAgain = true;

        // ── 바꾸기·되돌리기·자동 저장 ──

        /// 되돌리기 한 칸으로 바꾼다. 같은 key로 0.65초 안에 또 바꾸면(슬라이더 끌기) 앞 칸에 합친다.
        void Change(string key, Action act)
        {
            bool merge = key != null && key == lastKey && (DateTime.Now - lastAt).TotalMilliseconds < 650;
            string before = project.ToJson();
            try
            {
                act();
            }
            catch (UserFacingException ex)
            {
                project = VProject.FromJson(before);
                timeline.Project = project;
                SetStatus(ex.Message);
                return;
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집 바꾸기 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                project = VProject.FromJson(before);
                timeline.Project = project;
                SetStatus("바꾸지 못했어요. (" + ex.GetType().Name + ")");
                return;
            }
            if (!merge) history.Push(before);
            lastKey = key;
            lastAt = DateTime.Now;
            Touch(true);
        }

        /// 바뀐 내용을 미리 보기·시간 막대·화면에 알리고 자동 저장을 맞춘다
        void Touch(bool changed)
        {
            version++;
            preview.SetProject(project.Clone(), version);
            if (compareBtn != null && compareBtn.IsChecked == true) preview.Compare(project);
            timeline.Project = project;
            timeline.InvalidateVisual();
            stage.InvalidateVisual();
            LayoutCanvas();
            UpdateButtons();
            UpdateTime();
            if (changed)
            {
                changedSinceExport = true;
                autosaveTimer.Stop();
                autosaveTimer.Start();
                if (seekT > project.Duration) SeekTo(project.Duration);
            }
        }

        void UpdateButtons()
        {
            if (undoBtn == null) return;
            undoBtn.IsEnabled = history.CanUndo;
            redoBtn.IsEnabled = history.CanRedo;
            undoBtn.Opacity = history.CanUndo ? 1 : 0.4;
            redoBtn.Opacity = history.CanRedo ? 1 : 0.4;
        }

        void DoUndo()
        {
            var s = history.Undo(project.ToJson());
            if (s == null) return;
            Restore(s, "되돌렸어요.");
        }

        void DoRedo()
        {
            var s = history.Redo(project.ToJson());
            if (s == null) return;
            Restore(s, "다시 했어요.");
        }

        void Restore(string json, string msg)
        {
            project = VProject.FromJson(json);
            lastKey = null;
            if (selected != null && project.FindClip(selected) == null && project.FindItem(selected) == null && !project.Sounds.Any(x => x.Id == selected) && !project.Markers.Any(m => m.Id == selected)) selected = null;
            timeline.Selected = selected;
            Touch(true);
            RefreshPanel();
            SetStatus(msg);
        }

        void AutoSave()
        {
            if (demo) return;
            try
            {
                VAutoSave.Save(project);
                saveText.Text = Theme.Keep("자동 저장 " + DateTime.Now.ToString("H:mm:ss"));
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집 자동 저장 실패: " + ex.GetType().Name);
                saveText.Text = Theme.Keep("자동 저장하지 못했어요");
            }
        }

        void OnClosing(object s, System.ComponentModel.CancelEventArgs e)
        {
            if (demo) return;
            if (busy)
            {
                int b = Dialog.Ask(this, H, "media", "아직 하는 일이 있어요", "하던 일을 멈추고 닫을까요?", null, "계속하기", "멈추고 닫기");
                if (b != 1)
                {
                    e.Cancel = true;
                    return;
                }
                busyCancel = true;
            }
            if (recorder != null) StopRecording(false);
            if (changedSinceExport && history.CanUndo)
            {
                int n = Dialog.Ask(this, H, "media", "편집을 닫을까요?", "편집한 내용은 자동 저장돼 있어요. 같은 영상을 다시 열면 이어서 할 수 있어요.", null, "계속 편집하기", "닫기");
                if (n != 1)
                {
                    e.Cancel = true;
                    return;
                }
            }
            if (changedSinceExport) AutoSave();
            if (preview.Playing) preview.Pause();
        }

        // ── 고르기 ──

        VClip SelectedClip
        {
            get { return selected == null ? null : project.FindClip(selected); }
        }

        VItem SelectedItem
        {
            get { return selected == null ? null : project.FindItem(selected); }
        }

        VSound SelectedSound
        {
            get { return selected == null ? null : project.Sounds.FirstOrDefault(x => x.Id == selected); }
        }

        VMarker SelectedMarker
        {
            get { return selected == null ? null : project.Markers.FirstOrDefault(x => x.Id == selected); }
        }

        /// 고른 조각, 없으면 재생 위치의 조각
        VClip CurrentClip
        {
            get
            {
                var c = SelectedClip;
                if (c != null) return c;
                double local;
                int i = project.ClipAt(CurrentTime, out local);
                return i >= 0 ? project.Clips[i] : null;
            }
        }

        /// 재생 위치가 이 조각 안이면 조각 안 시간·원본 시간
        bool LocalIn(VClip c, out double local, out double source)
        {
            local = source = 0;
            if (c == null) return false;
            double start = project.StartOf(c);
            double t = CurrentTime;
            if (t < start - 1e-6 || t > start + c.Length + 1e-6) return false;
            local = Math.Max(0, Math.Min(c.Length, t - start));
            source = c.SourceAt(local);
            return true;
        }

        static readonly HashSet<string> ClipTabs = new HashSet<string> { "edit", "ratio", "sound", "look", "redact", "teacher", "export" };

        void Select(string id, bool switchTab)
        {
            selected = id;
            selectedRedact = null;
            timeline.Selected = id;
            if (id != null && switchTab)
            {
                var it = project.FindItem(id);
                if (it != null) SetTab(it.Kind == "text" && it.Subtitle ? "subtitle" : "deco");
                else if (project.Sounds.Any(x => x.Id == id)) SetTab("sound");
                else if (project.Markers.Any(m => m.Id == id)) SetTab("edit");
                else if (project.FindClip(id) != null && !ClipTabs.Contains(tab)) SetTab("edit");
            }
            timeline.InvalidateVisual();
            stage.InvalidateVisual();
            RefreshPanel();
        }

        // ── 시간 막대 ──

        void WireTimeline()
        {
            timeline.Seek = delegate (double t)
            {
                if (preview.Playing && !scrubbing) preview.Pause();
                SeekTo(t);
            };
            timeline.Scrubbing = delegate (bool on)
            {
                scrubbing = on;
                if (on)
                {
                    playAfterScrub = preview.Playing;
                    if (playAfterScrub) preview.Pause();
                }
                else if (playAfterScrub)
                {
                    playAfterScrub = false;
                    TogglePlay();
                }
            };
            timeline.Select = delegate (string id) { Select(id, true); };
            timeline.Open = delegate (string id)
            {
                Select(id, true);
                RefreshPanel(true);
            };
            timeline.Begin = delegate (bool clips)
            {
                history.Push(project.ToJson());
                lastKey = null;
                follow = clips ? project.Follow() : null;
            };
            timeline.Moved = delegate
            {
                var it = SelectedItem;
                if (it != null && it.Kind == "pip")
                {
                    version++;
                    preview.SetProject(project.Clone(), version);
                }
                stage.InvalidateVisual();
            };
            timeline.End = delegate
            {
                if (follow != null)
                {
                    follow();
                    follow = null;
                }
                Touch(true);
                RefreshPanel();
            };
            timeline.RangeChanged = delegate
            {
                if (tab == "edit" || tab == "export") RefreshPanel();
            };
        }

        // ── 단축키 ──

        void OnKey(object s, KeyEventArgs e)
        {
            var focus = Keyboard.FocusedElement;
            if (focus is TextBox)
            {
                if (e.Key == Key.Escape)
                {
                    stage.Focus();
                    e.Handled = true;
                }
                return;
            }
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (focus is Slider && (e.Key == Key.Left || e.Key == Key.Right)) return;
            switch (e.Key)
            {
                case Key.Space:
                    TogglePlay();
                    break;
                case Key.Left:
                    if (ctrl) JumpEdge(-1);
                    else Step(shift ? -1.0 : -FrameSec);
                    break;
                case Key.Right:
                    if (ctrl) JumpEdge(1);
                    else Step(shift ? 1.0 : FrameSec);
                    break;
                case Key.Home:
                    SeekTo(0);
                    break;
                case Key.End:
                    SeekTo(project.Duration);
                    break;
                case Key.S:
                    if (ctrl) return;
                    SplitHere();
                    break;
                case Key.B:
                    if (!ctrl) return;
                    SplitHere();
                    break;
                case Key.Delete:
                case Key.Back:
                    DeleteSelected();
                    break;
                case Key.Z:
                    if (!ctrl) return;
                    if (shift) DoRedo();
                    else DoUndo();
                    break;
                case Key.Y:
                    if (!ctrl) return;
                    DoRedo();
                    break;
                case Key.T:
                    if (ctrl) return;
                    AddSubtitle();
                    break;
                case Key.M:
                    if (ctrl) return;
                    AddMarker();
                    break;
                case Key.I:
                    if (ctrl) return;
                    SetRange(true);
                    break;
                case Key.O:
                    if (ctrl) return;
                    SetRange(false);
                    break;
                case Key.D:
                    if (!ctrl) return;
                    Duplicate();
                    break;
                case Key.Escape:
                    if (timeline.RangeA >= 0)
                    {
                        timeline.RangeA = timeline.RangeB = -1;
                        timeline.InvalidateVisual();
                    }
                    else Select(null, false);
                    break;
                case Key.OemPlus:
                case Key.Add:
                    timeline.ZoomBy(1.33, CurrentTime);
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    timeline.ZoomBy(0.75, CurrentTime);
                    break;
                case Key.D0:
                case Key.NumPad0:
                    timeline.FitAll();
                    break;
                case Key.F1:
                    ShowKeys();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        void JumpEdge(int dir)
        {
            double t = CurrentTime, s = 0;
            var edges = new List<double> { 0 };
            foreach (var c in project.Clips)
            {
                s += c.Length;
                edges.Add(s);
            }
            foreach (var m in project.Markers) edges.Add(m.T);
            edges.Sort();
            double target = dir > 0 ? edges.Where(x => x > t + 0.02).DefaultIfEmpty(project.Duration).First() : edges.Where(x => x < t - 0.02).DefaultIfEmpty(0).Last();
            Step(target - t);
        }

        static readonly string[][] Keys =
        {
            new[] { "Space", "재생 · 멈춤" },
            new[] { "← →", "한 장씩 (Shift를 같이 누르면 1초씩)" },
            new[] { "Ctrl + ← →", "앞뒤 조각 경계·표시로" },
            new[] { "Home  End", "처음 · 끝으로" },
            new[] { "S  또는 Ctrl + B", "재생 위치에서 나누기" },
            new[] { "Delete", "고른 것 지우기" },
            new[] { "Ctrl + Z  /  Ctrl + Y", "되돌리기 / 다시 하기" },
            new[] { "Ctrl + D", "고른 것 복제" },
            new[] { "T", "재생 위치에 자막 넣기" },
            new[] { "M", "재생 위치에 표시 넣기" },
            new[] { "I  /  O", "구간 시작 / 끝 (구간은 되풀이 재생)" },
            new[] { "Shift + 눈금 끌기", "구간 고르기" },
            new[] { "+  -  0", "시간 막대 늘리기 · 줄이기 · 한눈에" },
            new[] { "Ctrl + 휠", "시간 막대 확대" },
            new[] { "Alt + 끌기", "자석 없이 옮기기" },
            new[] { "Esc", "구간·고르기 풀기, 글 칸에서 나오기" }
        };

        void ShowKeys()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < Keys.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var k = new Border
                {
                    Background = H.TintBrush,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 10, 6),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = Theme.Text(Keys[i][0], 12.5, FontWeights.Bold, H.DeepBrush)
                };
                Grid.SetRow(k, i);
                grid.Children.Add(k);
                var d = Theme.Text(Keys[i][1], 13.5, FontWeights.Normal, Theme.Ink2);
                d.VerticalAlignment = VerticalAlignment.Center;
                d.Margin = new Thickness(0, 0, 0, 6);
                Grid.SetRow(d, i);
                Grid.SetColumn(d, 1);
                grid.Children.Add(d);
            }
            Dialog.Ask(this, H, "info", "단축키", null, grid, "확인");
        }

        // ── 끌어 놓기 ──

        void OnDrop(object s, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;
            InsertFiles(files.ToList());
        }

        // ── 화면(얹은 것·손잡이·가리기) ──

        sealed class VStage : FrameworkElement
        {
            readonly VideoEditor ed;

            public VStage(VideoEditor ed)
            {
                this.ed = ed;
                Focusable = true;
                FocusVisualStyle = null;
                ClipToBounds = true;
            }

            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
                ed.DrawStage(dc);
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonDown(e);
                Focus();
                ed.StageDown(e.GetPosition(this), e.ClickCount);
                if (ed.stageDrag != StageDrag.None) CaptureMouse();
                e.Handled = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                var p = e.GetPosition(this);
                if (ed.stageDrag != StageDrag.None && IsMouseCaptured) ed.StageMove(p);
                else Cursor = ed.StageCursor(p);
            }

            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonUp(e);
                if (IsMouseCaptured) ReleaseMouseCapture();
                ed.StageUp(e.GetPosition(this));
            }

            protected override void OnLostMouseCapture(MouseEventArgs e)
            {
                base.OnLostMouseCapture(e);
                if (ed.stageDrag != StageDrag.None) ed.StageUp(Mouse.GetPosition(this));
            }

            protected override void OnMouseWheel(MouseWheelEventArgs e)
            {
                base.OnMouseWheel(e);
                if (ed.StageWheel(e.GetPosition(this), e.Delta)) e.Handled = true;
            }
        }

        enum StageDrag { None, Move, Resize, Rotate, End1, End2, Pan, RedactNew, RedactMove, RedactResize }

        StageDrag stageDrag;
        Point dragFrom;
        bool stageBegun;
        double origX, origY, origW, origH, origSize, origRot, origZoom, origPanX, origPanY;
        Rect origBox, redactDraft;
        bool guideV, guideH;
        int resizeCorner;

        /// 화면 좌표 → 영상 화면 안 좌표(0~W, 0~H)
        Point ToCanvas(Point p)
        {
            return new Point(p.X - canvasRect.X, p.Y - canvasRect.Y);
        }

        void DrawStage(DrawingContext dc)
        {
            var r = canvasRect;
            if (project == null || r.Width < 4) return;
            double t = CurrentTime;
            dc.PushTransform(new TranslateTransform(r.X, r.Y));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, r.Width, r.Height)));
            VOverlay.Draw(dc, project, t, r.Width, r.Height, null);
            if (tab == "redact") DrawRedacts(dc, r.Width, r.Height);
            if (showSafe || (tab == "export" && (project.Export.Preset == "reels" || project.Export.Preset == "shorts"))) DrawSafe(dc, r.Width, r.Height);
            if (stageDrag != StageDrag.None)
            {
                var gp = new Pen(Theme.Frozen(Theme.Hex(0xFF8FC2)), 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                gp.Freeze();
                if (guideV) dc.DrawLine(gp, new Point(r.Width / 2, 0), new Point(r.Width / 2, r.Height));
                if (guideH) dc.DrawLine(gp, new Point(0, r.Height / 2), new Point(r.Width, r.Height / 2));
            }
            dc.Pop();
            DrawSelection(dc, r.Width, r.Height, t);
            dc.Pop();
        }

        static Pen DashPen(Brush b, double w)
        {
            var p = new Pen(b, w) { DashStyle = new DashStyle(new double[] { 3, 2.5 }, 0) };
            p.Freeze();
            return p;
        }

        void Handle(DrawingContext dc, Point at)
        {
            dc.DrawEllipse(Theme.Sheet, new Pen(Theme.Ink, 1.8), at, 6, 6);
        }

        void DrawSelection(DrawingContext dc, double W, double Hh, double t)
        {
            var it = SelectedItem;
            if (it == null || tab == "redact") return;
            var st = VAnim.Item(it, t);
            if (t < it.Start - 1e-6 || t >= it.End) return;
            if (it.Kind == "shape" && (it.Shape == "arrow" || it.Shape == "line"))
            {
                Point a, b;
                Ends(it, st, W, Hh, out a, out b);
                dc.DrawLine(DashPen(Theme.Ink, 1.2), a, b);
                Handle(dc, a);
                Handle(dc, b);
                return;
            }
            var box = VOverlay.Box(it, st, W, Hh);
            var c = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            if (Math.Abs(it.Rot) > 0.01) dc.PushTransform(new RotateTransform(it.Rot, c.X, c.Y));
            dc.DrawRectangle(null, DashPen(Theme.Ink, 1.4), box);
            dc.DrawRectangle(null, DashPen(Theme.Sheet, 1.4), new Rect(box.X - 1, box.Y - 1, box.Width + 2, box.Height + 2));
            foreach (var h in Corners(box)) Handle(dc, h);
            var rot = new Point(c.X, box.Top - 26);
            dc.DrawLine(new Pen(Theme.Ink, 1.2), new Point(c.X, box.Top), rot);
            dc.DrawEllipse(H.VividBrush, new Pen(Theme.Ink, 1.8), rot, 7, 7);
            if (Math.Abs(it.Rot) > 0.01) dc.Pop();
        }

        static Point[] Corners(Rect b)
        {
            return new[] { b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft };
        }

        static void Ends(VItem it, VAnim.State st, double W, double Hh, out Point a, out Point b)
        {
            double cx = (st.X + st.Dx) * W, cy = (st.Y + st.Dy) * Hh;
            double w = it.W * W * st.Scale, h = it.H * Hh * st.Scale;
            a = new Point(cx - w / 2, cy - h / 2);
            b = new Point(cx + w / 2, cy + h / 2);
        }

        void DrawSafe(DrawingContext dc, double W, double Hh)
        {
            var fill = Theme.Frozen(Color.FromArgb(70, 0xFF, 0x84, 0x73));
            var pen = DashPen(Theme.Frozen(Theme.Hex(0xB53A28)), 1);
            var zones = new[]
            {
                new Rect(0, 0, W, Hh * 0.12),
                new Rect(0, Hh * 0.78, W, Hh * 0.22),
                new Rect(W * 0.86, Hh * 0.3, W * 0.14, Hh * 0.48)
            };
            foreach (var z in zones) dc.DrawRectangle(fill, pen, z);
            var t = new FormattedText("앱 단추·글에 가려지는 곳", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
                new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), Math.Max(10, Math.Min(13, W / 30)), Theme.Frozen(Theme.Hex(0x7A2214)), 1.0);
            dc.DrawText(t, new Point(8, Hh * 0.78 + 6));
        }

        // 가리기 상자: 원본 화면(돌리기 전 0~1) ↔ 화면
        bool RedactMatrix(VClip c, out Matrix m)
        {
            m = Matrix.Identity;
            double local, source;
            if (c == null || c.Kind != "video" || !LocalIn(c, out local, out source)) return false;
            var pr = VMedia.Probe(c.Path);
            if (pr == null || pr.Width <= 0) return false;
            Rect clip;
            bool covers;
            m = VRenderer.PlaceOn(c, local, source, pr.Width, pr.Height, pr.Rotation, false, (int)Math.Round(canvasRect.Width), (int)Math.Round(canvasRect.Height), out clip, out covers);
            var s = Matrix.Identity;
            s.Scale(pr.Width, pr.Height);
            m = s * m;
            return true;
        }

        Point[] RedactCorners(Rect n, Matrix m)
        {
            return new[] { m.Transform(n.TopLeft), m.Transform(n.TopRight), m.Transform(n.BottomRight), m.Transform(n.BottomLeft) };
        }

        void DrawRedacts(DrawingContext dc, double W, double Hh)
        {
            var c = CurrentClip;
            Matrix m;
            if (!RedactMatrix(c, out m)) return;
            double local, source;
            LocalIn(c, out local, out source);
            foreach (var rd in c.Redacts)
            {
                if (source < rd.Start - 1e-3 || source > rd.End + 1e-3) continue;
                var n = VAnim.Redact(rd, source);
                var pts = RedactCorners(n, m);
                var g = new StreamGeometry();
                using (var x = g.Open())
                {
                    x.BeginFigure(pts[0], true, true);
                    x.LineTo(pts[1], true, true);
                    x.LineTo(pts[2], true, true);
                    x.LineTo(pts[3], true, true);
                }
                g.Freeze();
                bool sel = rd.Id == selectedRedact;
                var pen = rd.Off ? DashPen(Theme.Frozen(Theme.Hex(0x8A8F9C)), 1.4) : sel ? new Pen(Theme.Ink, 2.2) : DashPen(Theme.Sheet, 1.6);
                dc.DrawGeometry(rd.Off ? Theme.Frozen(Color.FromArgb(40, 0xFF, 0xFF, 0xFF)) : null, pen, g);
                if (rd.Face)
                {
                    var label = new FormattedText(rd.Off ? "보이게 둠" : "얼굴", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
                        new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 11, rd.Off ? Theme.Ink2 : Theme.Ink, 1.0);
                    var at = new Point(Math.Min(pts.Min(p => p.X), W - label.Width - 8), Math.Max(0, pts.Min(p => p.Y) - label.Height - 4));
                    dc.DrawRoundedRectangle(rd.Off ? Theme.Frozen(Theme.Hex(0xEEF0F4)) : H.VividBrush, null, new Rect(at.X - 4, at.Y - 1, label.Width + 8, label.Height + 2), 6, 6);
                    dc.DrawText(label, at);
                }
                if (sel) foreach (var p in pts) Handle(dc, p);
            }
            if (stageDrag == StageDrag.RedactNew && redactDraft.Width > 0) dc.DrawRectangle(Theme.Frozen(Color.FromArgb(60, 0x38, 0xC7, 0xB8)), new Pen(Theme.Ink, 1.6), redactDraft);
        }

        VRedact HitRedact(Point q, out int corner)
        {
            corner = -1;
            var c = CurrentClip;
            Matrix m;
            if (!RedactMatrix(c, out m)) return null;
            double local, source;
            LocalIn(c, out local, out source);
            for (int i = c.Redacts.Count - 1; i >= 0; i--)
            {
                var rd = c.Redacts[i];
                if (source < rd.Start - 1e-3 || source > rd.End + 1e-3) continue;
                var pts = RedactCorners(VAnim.Redact(rd, source), m);
                if (rd.Id == selectedRedact)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        if ((pts[k] - q).Length < 9)
                        {
                            corner = k;
                            return rd;
                        }
                    }
                }
                var bounds = new Rect(pts[0], pts[2]);
                bounds.Union(pts[1]);
                bounds.Union(pts[3]);
                bounds.Inflate(3, 3);
                if (bounds.Contains(q)) return rd;
            }
            return null;
        }

        Cursor StageCursor(Point p)
        {
            var q = ToCanvas(p);
            if (tab == "redact")
            {
                int corner;
                var rd = HitRedact(q, out corner);
                return rd != null ? (corner >= 0 ? Cursors.SizeNWSE : Cursors.SizeAll) : Cursors.Cross;
            }
            var it = SelectedItem;
            double t = CurrentTime;
            if (it != null && HandleAt(it, q, t) != StageDrag.None) return Cursors.SizeNWSE;
            var hit = VOverlay.HitTest(project, t, canvasRect.Width, canvasRect.Height, q);
            if (hit != null) return Cursors.SizeAll;
            if (tab == "ratio") return Cursors.Hand;
            return Cursors.Arrow;
        }

        StageDrag HandleAt(VItem it, Point q, double t)
        {
            if (t < it.Start - 1e-6 || t >= it.End) return StageDrag.None;
            var st = VAnim.Item(it, t);
            double W = canvasRect.Width, Hh = canvasRect.Height;
            if (it.Kind == "shape" && (it.Shape == "arrow" || it.Shape == "line"))
            {
                Point a, b;
                Ends(it, st, W, Hh, out a, out b);
                if ((a - q).Length < 10) return StageDrag.End1;
                if ((b - q).Length < 10) return StageDrag.End2;
                return StageDrag.None;
            }
            var box = VOverlay.Box(it, st, W, Hh);
            var c = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            var p = q;
            if (Math.Abs(it.Rot) > 0.01)
            {
                var m = Matrix.Identity;
                m.RotateAt(-it.Rot, c.X, c.Y);
                p = m.Transform(q);
            }
            if ((new Point(c.X, box.Top - 26) - p).Length < 11) return StageDrag.Rotate;
            var cs = Corners(box);
            for (int k = 0; k < 4; k++)
            {
                if ((cs[k] - p).Length < 10)
                {
                    resizeCorner = k;
                    return StageDrag.Resize;
                }
            }
            return StageDrag.None;
        }

        void StageDown(Point p, int clicks)
        {
            var q = ToCanvas(p);
            dragFrom = q;
            stageBegun = false;
            guideV = guideH = false;
            stageDrag = StageDrag.None;
            double t = CurrentTime;
            if (preview.Playing && tab != "redact") preview.Pause();
            if (tab == "redact")
            {
                RedactDown(q, clicks);
                return;
            }
            var it = SelectedItem;
            if (it != null)
            {
                var h = HandleAt(it, q, t);
                if (h != StageDrag.None)
                {
                    BeginItemDrag(it, h, t);
                    return;
                }
            }
            var hit = VOverlay.HitTest(project, t, canvasRect.Width, canvasRect.Height, q);
            if (hit != null)
            {
                if (hit.Id != selected) Select(hit.Id, true);
                if (clicks == 2)
                {
                    RefreshPanel(true);
                    return;
                }
                BeginItemDrag(hit, StageDrag.Move, t);
                return;
            }
            if (tab == "ratio")
            {
                var c = CurrentClip;
                if (c == null) return;
                double local, source;
                LocalIn(c, out local, out source);
                double z, px, py;
                VAnim.ClipMotion(c, local, source, out z, out px, out py);
                origZoom = z;
                origPanX = px;
                origPanY = py;
                stageDrag = StageDrag.Pan;
                return;
            }
            if (selected != null && project.FindItem(selected) != null) Select(null, false);
        }

        void BeginItemDrag(VItem it, StageDrag kind, double t)
        {
            var st = VAnim.Item(it, t);
            origX = st.X;
            origY = st.Y;
            origW = it.W;
            origH = it.H;
            origSize = it.Size;
            origRot = it.Rot;
            origZoom = st.Scale;
            origBox = VOverlay.Box(it, st, canvasRect.Width, canvasRect.Height);
            stageDrag = kind;
        }

        void StageBegin()
        {
            if (stageBegun) return;
            stageBegun = true;
            history.Push(project.ToJson());
            lastKey = null;
        }

        /// 움직임 표시가 있으면 지금 시각의 표시를 고치고(없으면 새로), 없으면 기본값을 고친다
        void SetItemPlace(VItem it, double x, double y, double scale, double t)
        {
            if (it.Keys.Count > 0)
            {
                double local = Math.Max(0, t - it.Start);
                var k = it.Keys.FirstOrDefault(v => Math.Abs(v.T - local) < 0.04);
                if (k == null)
                {
                    var cur = VAnim.At(it.Keys, local);
                    k = cur == null ? new VKey { T = local } : cur.Copy();
                    k.T = local;
                    it.Keys.Add(k);
                    it.Keys.Sort((a, b) => a.T.CompareTo(b.T));
                }
                k.X = x;
                k.Y = y;
                k.Zoom = scale;
            }
            else
            {
                it.X = x;
                it.Y = y;
            }
        }

        void StageMove(Point p)
        {
            var q = ToCanvas(p);
            double W = canvasRect.Width, Hh = canvasRect.Height;
            double dx = q.X - dragFrom.X, dy = q.Y - dragFrom.Y;
            if (!stageBegun && Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
            double t = CurrentTime;
            switch (stageDrag)
            {
                case StageDrag.Move:
                {
                    var it = SelectedItem;
                    if (it == null) return;
                    StageBegin();
                    double x = origX + dx / W, y = origY + dy / Hh;
                    guideV = Math.Abs(x - 0.5) * W < 7;
                    guideH = Math.Abs(y - 0.5) * Hh < 7;
                    if (guideV && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) x = 0.5;
                    else guideV = false;
                    if (guideH && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) y = 0.5;
                    else guideH = false;
                    SetItemPlace(it, Math.Max(-0.3, Math.Min(1.3, x)), Math.Max(-0.3, Math.Min(1.3, y)), it.Keys.Count > 0 ? origZoom : 1, t);
                    StageChanged(it);
                    break;
                }
                case StageDrag.Resize:
                {
                    var it = SelectedItem;
                    if (it == null) return;
                    StageBegin();
                    var c = new Point(origBox.X + origBox.Width / 2, origBox.Y + origBox.Height / 2);
                    double d0 = Math.Max(4, (dragFrom - c).Length), d1 = Math.Max(2, (q - c).Length);
                    double k = Math.Max(0.1, Math.Min(10, d1 / d0));
                    if (it.Kind == "text") it.Size = Math.Max(0.015, Math.Min(0.3, origSize * k));
                    else if (it.Kind == "shape" && (it.Shape == "box" || it.Shape == "circle" || it.Shape == "marker" || it.Shape == "bar"))
                    {
                        double kx = Math.Abs(q.X - c.X) / Math.Max(2, Math.Abs(dragFrom.X - c.X));
                        double ky = Math.Abs(q.Y - c.Y) / Math.Max(2, Math.Abs(dragFrom.Y - c.Y));
                        it.W = Math.Max(0.02, Math.Min(1.5, origW * Math.Max(0.05, Math.Min(20, kx))));
                        it.H = Math.Max(0.02, Math.Min(1.5, origH * Math.Max(0.05, Math.Min(20, ky))));
                    }
                    else it.W = Math.Max(0.02, Math.Min(1.5, origW * k));
                    StageChanged(it);
                    break;
                }
                case StageDrag.Rotate:
                {
                    var it = SelectedItem;
                    if (it == null) return;
                    StageBegin();
                    var c = new Point(origBox.X + origBox.Width / 2, origBox.Y + origBox.Height / 2);
                    double a0 = Math.Atan2(dragFrom.Y - c.Y, dragFrom.X - c.X), a1 = Math.Atan2(q.Y - c.Y, q.X - c.X);
                    double rot = origRot + (a1 - a0) * 180 / Math.PI;
                    rot = ((rot + 180) % 360 + 360) % 360 - 180;
                    foreach (double snap in new double[] { -180, -90, 0, 90, 180 }) if (Math.Abs(rot - snap) < 4) rot = snap;
                    it.Rot = rot;
                    StageChanged(it);
                    break;
                }
                case StageDrag.End1:
                case StageDrag.End2:
                {
                    var it = SelectedItem;
                    if (it == null) return;
                    StageBegin();
                    var st = VAnim.Item(it, t);
                    Point a, b;
                    Ends(it, st, W, Hh, out a, out b);
                    if (stageDrag == StageDrag.End1) a = q;
                    else b = q;
                    it.X = (a.X + b.X) / 2 / W - st.Dx;
                    it.Y = (a.Y + b.Y) / 2 / Hh - st.Dy;
                    it.W = (b.X - a.X) / W / Math.Max(0.05, st.Scale);
                    it.H = (b.Y - a.Y) / Hh / Math.Max(0.05, st.Scale);
                    it.Keys.Clear();
                    StageChanged(it);
                    break;
                }
                case StageDrag.Pan:
                {
                    var c = CurrentClip;
                    if (c == null) return;
                    StageBegin();
                    double px = origPanX + dx / W, py = origPanY + dy / Hh;
                    SetClipMotion(c, origZoom, px, py);
                    version++;
                    preview.SetProject(project.Clone(), version);
                    break;
                }
                case StageDrag.RedactNew:
                    redactDraft = new Rect(dragFrom, q);
                    stage.InvalidateVisual();
                    break;
                case StageDrag.RedactMove:
                case StageDrag.RedactResize:
                    RedactDragMove(q);
                    break;
            }
        }

        void StageChanged(VItem it)
        {
            if (it.Kind == "pip")
            {
                version++;
                preview.SetProject(project.Clone(), version);
            }
            stage.InvalidateVisual();
        }

        /// 조각의 확대·자리: 움직임 표시가 있으면 지금 원본 시각의 표시를 고친다
        void SetClipMotion(VClip c, double zoom, double panX, double panY)
        {
            panX = Math.Max(-1.5, Math.Min(1.5, panX));
            panY = Math.Max(-1.5, Math.Min(1.5, panY));
            zoom = Math.Max(0.2, Math.Min(6, zoom));
            if (c.Keys.Count > 0)
            {
                double local, source;
                LocalIn(c, out local, out source);
                double kt = c.Kind == "video" ? source : local;
                var k = c.Keys.FirstOrDefault(v => Math.Abs(v.T - kt) < 0.04);
                if (k == null)
                {
                    k = new VKey { T = kt };
                    c.Keys.Add(k);
                    c.Keys.Sort((a, b) => a.T.CompareTo(b.T));
                }
                k.Zoom = zoom;
                k.PanX = panX;
                k.PanY = panY;
            }
            else
            {
                c.Zoom = zoom;
                c.PanX = panX;
                c.PanY = panY;
            }
        }

        void StageUp(Point p)
        {
            var d = stageDrag;
            stageDrag = StageDrag.None;
            guideV = guideH = false;
            if (d == StageDrag.RedactNew)
            {
                FinishRedactDraft();
                return;
            }
            if (stageBegun)
            {
                stageBegun = false;
                Touch(true);
                if (d == StageDrag.Pan || tab == "deco" || tab == "subtitle" || tab == "ratio" || tab == "redact") RefreshPanel();
            }
            stage.InvalidateVisual();
        }

        bool StageWheel(Point p, int delta)
        {
            if (tab != "ratio") return false;
            var c = CurrentClip;
            if (c == null) return false;
            double local, source;
            LocalIn(c, out local, out source);
            double z, px, py;
            VAnim.ClipMotion(c, local, source, out z, out px, out py);
            double nz = Math.Max(0.2, Math.Min(6, z * Math.Pow(1.1, delta / 120.0)));
            Change("zoom" + c.Id, delegate { SetClipMotion(c, nz, px, py); });
            RefreshPanel();
            return true;
        }

        // ── 가리기 상자 그리기·옮기기 ──

        int redactCorner;
        Rect redactOrig;

        void RedactDown(Point q, int clicks)
        {
            var c = CurrentClip;
            if (c == null || c.Kind != "video")
            {
                SetStatus("가리기는 영상 조각에서 쓸 수 있어요.");
                return;
            }
            int corner;
            var rd = HitRedact(q, out corner);
            if (rd != null)
            {
                if (clicks == 2 && rd.Face)
                {
                    Change(null, delegate { rd.Off = !rd.Off; });
                    selectedRedact = rd.Id;
                    RefreshPanel();
                    return;
                }
                selectedRedact = rd.Id;
                double local, source;
                LocalIn(c, out local, out source);
                redactOrig = VAnim.Redact(rd, source);
                redactCorner = corner;
                stageDrag = corner >= 0 ? StageDrag.RedactResize : StageDrag.RedactMove;
                RefreshPanel();
                stage.InvalidateVisual();
                return;
            }
            selectedRedact = null;
            redactDraft = new Rect(q, q);
            stageDrag = StageDrag.RedactNew;
            RefreshPanel();
        }

        bool StageToSource(VClip c, Point q, out Point n)
        {
            n = new Point();
            Matrix m;
            if (!RedactMatrix(c, out m) || !m.HasInverse) return false;
            m.Invert();
            n = m.Transform(q);
            return true;
        }

        void RedactDragMove(Point q)
        {
            var c = CurrentClip;
            var rd = c == null ? null : c.Redacts.FirstOrDefault(x => x.Id == selectedRedact);
            if (rd == null) return;
            Point a, b;
            if (!StageToSource(c, dragFrom, out a) || !StageToSource(c, q, out b)) return;
            StageBegin();
            Rect r = redactOrig;
            if (stageDrag == StageDrag.RedactMove)
            {
                r.X += b.X - a.X;
                r.Y += b.Y - a.Y;
            }
            else
            {
                // 고른 귀퉁이를 원본 좌표로 옮긴다(맞은편 귀퉁이는 그대로)
                var corners = new[] { redactOrig.TopLeft, redactOrig.TopRight, redactOrig.BottomRight, redactOrig.BottomLeft };
                var fixedPt = corners[(redactCorner + 2) % 4];
                Point moved;
                StageToSource(c, q, out moved);
                r = new Rect(fixedPt, moved);
            }
            r.X = Math.Max(-0.2, Math.Min(1.1, r.X));
            r.Y = Math.Max(-0.2, Math.Min(1.1, r.Y));
            r.Width = Math.Max(0.01, Math.Min(1.2, r.Width));
            r.Height = Math.Max(0.01, Math.Min(1.2, r.Height));
            double local, source;
            LocalIn(c, out local, out source);
            SetRedactRect(rd, r, source);
            version++;
            preview.SetProject(project.Clone(), version);
            stage.InvalidateVisual();
        }

        static void SetRedactRect(VRedact rd, Rect r, double source)
        {
            if (rd.Keys.Count > 0)
            {
                var k = rd.Keys.FirstOrDefault(v => Math.Abs(v.T - source) < 0.04);
                if (k == null)
                {
                    k = new VKey { T = source };
                    rd.Keys.Add(k);
                    rd.Keys.Sort((a, b) => a.T.CompareTo(b.T));
                }
                k.X = r.X;
                k.Y = r.Y;
                k.W = r.Width;
                k.H = r.Height;
            }
            else
            {
                rd.X = r.X;
                rd.Y = r.Y;
                rd.W = r.Width;
                rd.H = r.Height;
            }
        }

        void FinishRedactDraft()
        {
            var c = CurrentClip;
            var draft = redactDraft;
            redactDraft = Rect.Empty;
            stage.InvalidateVisual();
            if (c == null || draft.IsEmpty || draft.Width < 6 || draft.Height < 6) return;
            Point a, b;
            if (!StageToSource(c, draft.TopLeft, out a) || !StageToSource(c, draft.BottomRight, out b)) return;
            Point a2, b2;
            StageToSource(c, draft.TopRight, out a2);
            StageToSource(c, draft.BottomLeft, out b2);
            var r = new Rect(a, b);
            r.Union(a2);
            r.Union(b2);
            var rd = new VRedact
            {
                Start = c.In,
                End = c.Out,
                X = r.X,
                Y = r.Y,
                W = r.Width,
                H = r.Height,
                Mode = redactProto.Mode,
                Strength = redactProto.Strength,
                Oval = redactProto.Oval,
                Rgb = redactProto.Rgb
            };
            Change(null, delegate { c.Redacts.Add(rd); });
            selectedRedact = rd.Id;
            RefreshPanel();
            SetStatus("가렸어요. 움직이는 것이면 [따라가기]를 눌러 보세요.");
        }

        readonly VRedact redactProto = new VRedact { Mode = "blur", Strength = 0.6 };

        // ── 그림 확인용 ──

        void DemoReady()
        {
            // 그림 띠·소리 모양을 기다렸다가(길어야 20초) 장면을 바로 그려 둔다
            var until = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < until)
            {
                bool ready = true;
                foreach (var c in project.Clips)
                {
                    if (c.Kind != "video") continue;
                    if (!Filmstrip.Get(c.Path).Done) ready = false;
                    var a = AudioCache.Get(c.Path);
                    if (!a.Ready && !a.Failed) ready = false;
                }
                if (ready) break;
                Thread.Sleep(150);
            }
            LayoutCanvas();
            double t = Math.Min(2, project.Duration * 0.3);
            seekT = t;
            int w = Math.Max(16, (int)canvasRect.Width & ~1), h = Math.Max(16, (int)canvasRect.Height & ~1);
            try
            {
                using (var r = new VRenderer(w, h, false))
                {
                    r.Use(project, version);
                    var px = r.Frame(t);
                    bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                    bitmap.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
                    frameImage.Source = bitmap;
                }
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집 그림 확인: 장면 그리기 실패 " + ex.GetType().Name);
            }
            timeline.SetPlayhead(t, false);
            timeline.InvalidateVisual();
            UpdateTime();
        }
    }
}
