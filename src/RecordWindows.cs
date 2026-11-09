// 화면 녹화의 창들: 고르기 → (영역 끌기) → 3·2·1 → 떠 있는 막대 → 저장 알림.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Box = System.Drawing.Rectangle;
using ShapePath = System.Windows.Shapes.Path;

namespace Bakkum
{
    public static class RecordUi
    {
        const int Fps = 24;
        const long FewestFreeBytes = 300L * 1024 * 1024;

        static Hue Paper
        {
            get { return Theme.HueOf("record"); }
        }

        static ScreenRecorder recorder;
        static RecordBar bar;
        static AreaFrame frame;
        static string outputPath;
        static bool busy, ending;

        /// 녹화를 준비하거나 하는 중인지(새 버전 바꾸기를 미룬다)
        public static bool Active
        {
            get { return busy; }
        }

        /// 지금 찍고 있는지
        public static bool Recording
        {
            get { return recorder != null && !ending; }
        }

        static string Known(string value, params string[] allowed)
        {
            return allowed.Contains(value) ? value : allowed[0];
        }

        static StackPanel Choices(string area, string sound, Action<string> setArea, Action<string> setSound, bool severalScreens)
        {
            var stack = new StackPanel();
            var first = Prompts.Caption("어디를 녹화할까요?");
            first.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(first);
            stack.Children.Add(Prompts.Chips(Paper, new[] { "full", "area" }, new[] { "전체 화면", "영역 고르기" }, area, setArea));
            stack.Children.Add(Prompts.Caption("소리"));
            stack.Children.Add(Prompts.Chips(Paper, new[] { "none", "mic", "computer", "both" }, new[] { "소리 없음", "마이크", "컴퓨터 소리", "둘 다" }, sound, setSound));
            string text = "비디오 폴더에 MP4로 저장돼요. 녹화 중에는 화면 위 작은 막대에서 잠시 멈추거나 끝낼 수 있어요.";
            if (severalScreens) text += " 화면이 여러 개면 마우스가 있는 화면을 녹화해요.";
            var note = Theme.MetaLabel(text);
            note.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(note);
            return stack;
        }

        public static void Open()
        {
            if (busy)
            {
                App.Notify("화면 녹화", "이미 녹화하고 있어요. 화면 위 작은 막대에서 끝낼 수 있어요.", false);
                return;
            }
            var settings = AppSettings.Current;
            string area = Known(settings.RecordArea, "full", "area");
            string sound = Known(settings.RecordSound, "none", "mic", "computer", "both");
            var panel = Choices(area, sound, v => area = v, v => sound = v, ScreenArea.MonitorCount > 1);
            if (Dialog.Ask(null, Paper, "record", "화면을 녹화할까요?", null, panel, "취소", "녹화 시작") != 1) return;
            settings.RecordArea = area;
            settings.RecordSound = sound;
            settings.SaveQuietly();
            busy = true;
            try
            {
                Box real = ScreenArea.MonitorUnderCursor();
                Rect place = ScreenArea.MonitorDip();
                if (area != "area")
                {
                    Arm(real, place, null, sound);
                    return;
                }
                var picker = new AreaPicker(real, place, false);
                picker.Done = delegate (Rect? part)
                {
                    if (part == null)
                    {
                        busy = false;
                        return;
                    }
                    Rect p = part.Value;
                    var box = new Box(real.X + (int)Math.Round(p.X * real.Width), real.Y + (int)Math.Round(p.Y * real.Height), (int)Math.Round(p.Width * real.Width), (int)Math.Round(p.Height * real.Height));
                    Arm(box, place, new Rect(place.X + p.X * place.Width, place.Y + p.Y * place.Height, p.Width * place.Width, p.Height * place.Height), sound);
                };
                picker.Show();
                picker.Activate();
            }
            catch (UserFacingException ex)
            {
                busy = false;
                App.Notify("화면 녹화", ex.Message, true);
            }
        }

        /// 시험에서만: 진짜 비디오 폴더 대신 쓸 곳
        internal static string TestFolder;

        /// 시험에서만: 마지막으로 저장한 파일
        internal static string LastFile;

        internal static void ArmForTest(Box box, Rect place, Rect? part)
        {
            busy = true;
            Arm(box, place, part, "none");
        }

        static string NewFile()
        {
            string dir = TestFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            try
            {
                if (new DriveInfo(Path.GetPathRoot(dir)).AvailableFreeSpace < FewestFreeBytes) throw new UserFacingException("디스크 공간이 모자라요. 파일을 조금 지운 뒤에 다시 해 주세요.");
            }
            catch (IOException)
            {
                // 네트워크 폴더 등은 남은 공간을 알 수 없다. 그대로 시도한다.
            }
            catch (ArgumentException)
            {
            }
            return Util.UniquePath(Path.Combine(dir, "화면녹화_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4"));
        }

        static ScreenRecorder Build(Box box, string sound, string path)
        {
            var sources = new List<ISoundSource>();
            ScreenRecorder made = null;
            try
            {
                WinRtCall.Mta<bool>(delegate
                {
                    if (sound == "mic" || sound == "both") sources.Add(new WasapiSource(false));
                    if (sound == "computer" || sound == "both") sources.Add(new WasapiSource(true));
                    return true;
                });
                // CAPTUREBLT는 쓰지 않는다. 없어도 투명 창까지 찍혔고(2026-11-08 실측) 쓰면 마우스 포인터가 깜빡일 수 있다.
                made = new ScreenRecorder(path, new GdiGrabber(box, true, false), sources, Fps);
                made.Prepare();
                return made;
            }
            catch
            {
                if (made != null)
                {
                    made.Discard();
                }
                else
                {
                    WinRtCall.Mta<bool>(delegate
                    {
                        foreach (var s in sources) s.Dispose();
                        return true;
                    });
                }
                throw;
            }
        }

        static void Arm(Box box, Rect place, Rect? part, string sound)
        {
            ScreenRecorder made;
            string path;
            try
            {
                path = NewFile();
                made = Build(box, sound, path);
            }
            catch (UserFacingException ex)
            {
                busy = false;
                App.Notify("화면 녹화", ex.Message, true);
                return;
            }
            catch (Exception ex)
            {
                busy = false;
                Log.Write("녹화 준비 실패: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                App.Notify("화면 녹화", "녹화를 시작하지 못했어요. (" + ex.GetType().Name + ")", true);
                return;
            }
            var count = new Countdown(place, false);
            count.Done = delegate (bool go)
            {
                if (!go)
                {
                    made.Discard();
                    busy = false;
                    return;
                }
                Launch(made, path, place, part);
            };
            count.Show();
        }

        static void Launch(ScreenRecorder made, string path, Rect place, Rect? part)
        {
            recorder = made;
            outputPath = path;
            ending = false;
            made.Died = delegate
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(Stop));
            };
            try
            {
                made.Start();
            }
            catch (Exception ex)
            {
                Log.Write("녹화 시작 실패: " + ex.GetType().Name);
                Stop();
                return;
            }
            bar = new RecordBar(made, place, part, false);
            bar.Show();
            if (part != null)
            {
                frame = new AreaFrame(part.Value);
                frame.Show();
            }
        }

        static void CloseFloating()
        {
            if (bar != null) bar.Close();
            bar = null;
            if (frame != null) frame.Close();
            frame = null;
        }

        /// 녹화를 끝내고 저장을 마무리한다. 알림에 진행과 결과가 나온다.
        public static void Stop()
        {
            var r = recorder;
            if (r == null || ending) return;
            ending = true;
            CloseFloating();
            var job = new Job(MenuAction.All.First(a => a.Id == "record"), new ActionOption("go", "화면 녹화", ""), new List<string>());
            var toast = Toast.ForJob(job);
            toast.NoCancel();
            toast.SetProgress("영상을 저장하는 중", -1);
            string path = outputPath;
            r.Finish(delegate (string error)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (error == null)
                    {
                        job.Output(path);
                        Jobs.AddRecent(path);
                        LastFile = path;
                    }
                    else job.Problem(null, error);
                    if (r.Note != null) job.Note(r.Note);
                    recorder = null;
                    ending = false;
                    busy = false;
                    toast.Done(job);
                }));
            });
        }

        /// 프로그램을 끌 때: 찍던 것을 저장하고 나서 끝낸다.
        public static void StopForQuit()
        {
            var r = recorder;
            if (r == null || ending) return;
            ending = true;
            CloseFloating();
            using (var done = new ManualResetEvent(false))
            {
                r.Finish(delegate { done.Set(); });
                if (!done.WaitOne(20000)) Log.Write("끝낼 때 녹화 저장이 늦음");
            }
            recorder = null;
            busy = false;
        }

        // ── 보기용(그림 확인): 띄우지 않은 창 ──

        public static Window PreviewOptions()
        {
            return Dialog.Build(null, Paper, "record", "화면을 녹화할까요?", null, Choices("full", "mic", delegate { }, delegate { }, true), "취소", "녹화 시작");
        }

        public static Window PreviewBar(bool paused)
        {
            return new RecordBar(null, new Rect(), null, paused);
        }

        public static Window PreviewCountdown()
        {
            return new Countdown(new Rect(), true);
        }

        public static Window PreviewPicker()
        {
            return new AreaPicker(new Box(0, 0, 1920, 1080), new Rect(0, 0, 1280, 720), true);
        }
    }

    /// 어디를 녹화할지 끌어서 고르는 창. 결과는 화면 전체를 1로 본 비율이라 배율이 달라도 맞는다.
    sealed class AreaPicker : Window
    {
        const int Smallest = 64;

        readonly Canvas canvas = new Canvas();
        readonly RectangleGeometry hole = new RectangleGeometry();
        readonly Border edge;
        readonly Border sizeTag;
        readonly TextBlock sizeText;
        readonly FrameworkElement hint;
        readonly TextBlock hintText;
        readonly Box real;
        Point start;
        bool dragging, finished;

        public Action<Rect?> Done;

        public AreaPicker(Box real, Rect place, bool preview)
        {
            this.real = real;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Left = place.X;
            Top = place.Y;
            Width = place.Width;
            Height = place.Height;
            Title = "녹화할 곳 고르기 · " + AppInfo.Name;
            FontFamily = Theme.Font;
            Cursor = Cursors.Cross;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            var whole = new GeometryGroup { FillRule = FillRule.EvenOdd };
            whole.Children.Add(new RectangleGeometry(new Rect(0, 0, place.Width, place.Height)));
            whole.Children.Add(hole);
            canvas.Children.Add(new ShapePath { Data = whole, Fill = Theme.Frozen(Color.FromArgb(120, 0x22, 0x25, 0x2F)) });

            edge = new Border { BorderBrush = Theme.HueOf("record").VividBrush, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2), Visibility = Visibility.Collapsed };
            canvas.Children.Add(edge);

            sizeText = Theme.Text("", 13, FontWeights.Bold, Theme.Ink);
            sizeTag = new Border { Background = Theme.HueOf("record").VividBrush, CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 3), Child = sizeText, Visibility = Visibility.Collapsed };
            canvas.Children.Add(sizeTag);

            var words = new StackPanel();
            hintText = Theme.Text("녹화할 곳을 끌어서 고르세요", 16, FontWeights.Bold, Theme.Ink);
            hintText.HorizontalAlignment = HorizontalAlignment.Center;
            words.Children.Add(hintText);
            var sub = Theme.MetaLabel("Esc를 누르면 취소돼요");
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(sub);
            hint = Theme.Card(words, 16);
            hint.IsHitTestVisible = false;
            canvas.Children.Add(hint);
            hint.Loaded += delegate
            {
                Canvas.SetLeft(hint, (place.Width - hint.ActualWidth) / 2);
                Canvas.SetTop(hint, Math.Max(28, place.Height * 0.08));
            };
            Content = canvas;
            if (preview)
            {
                Update(new Point(320, 200), new Point(980, 560));
                hint.Visibility = Visibility.Visible;
                return;
            }

            MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                start = e.GetPosition(canvas);
                dragging = true;
                hint.Visibility = Visibility.Collapsed;
                CaptureMouse();
            };
            MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (dragging) Update(start, e.GetPosition(canvas));
            };
            MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                if (!dragging) return;
                dragging = false;
                ReleaseMouseCapture();
                Rect r = Span(start, e.GetPosition(canvas));
                if (RealWidth(r) < Smallest || RealHeight(r) < Smallest)
                {
                    // 너무 작게 고르면(그냥 누른 경우 포함) 다시 고르게 한다.
                    edge.Visibility = Visibility.Collapsed;
                    sizeTag.Visibility = Visibility.Collapsed;
                    hole.Rect = Rect.Empty;
                    hintText.Text = Theme.Keep("조금 더 크게 끌어 주세요");
                    hint.Visibility = Visibility.Visible;
                    return;
                }
                End(new Rect(r.X / ActualWidth, r.Y / ActualHeight, r.Width / ActualWidth, r.Height / ActualHeight));
            };
            MouseRightButtonDown += delegate { End(null); };
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) End(null);
            };
            Closed += delegate { End(null); };
        }

        static Rect Span(Point a, Point b)
        {
            return new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        int RealWidth(Rect r)
        {
            return (int)Math.Round(r.Width / Width * real.Width) & ~1;
        }

        int RealHeight(Rect r)
        {
            return (int)Math.Round(r.Height / Height * real.Height) & ~1;
        }

        void Update(Point a, Point b)
        {
            Rect r = Span(a, b);
            hole.Rect = r;
            edge.Visibility = Visibility.Visible;
            Canvas.SetLeft(edge, r.X - 2);
            Canvas.SetTop(edge, r.Y - 2);
            edge.Width = r.Width + 4;
            edge.Height = r.Height + 4;
            sizeText.Text = RealWidth(r) + " × " + RealHeight(r);
            sizeTag.Visibility = Visibility.Visible;
            Canvas.SetLeft(sizeTag, r.X);
            Canvas.SetTop(sizeTag, r.Y >= 34 ? r.Y - 32 : r.Y + 8);
        }

        void End(Rect? result)
        {
            if (finished) return;
            finished = true;
            var h = Done;
            Close();
            if (h != null) h(result);
        }
    }

    /// 3·2·1. 누르면 취소한다.
    sealed class Countdown : Window
    {
        readonly TextBlock digit;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int left = 3;
        bool finished;

        public Action<bool> Done;

        public Countdown(Rect place, bool preview)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Width = 208;
            Height = 232;
            Left = place.X + (place.Width - Width) / 2;
            Top = place.Y + (place.Height - Height) / 2;
            Title = "녹화 준비 · " + AppInfo.Name;
            FontFamily = Theme.Font;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Cursor = Cursors.Hand;

            var hue = Theme.HueOf("record");
            digit = Theme.Text("3", 58, FontWeights.Bold, Theme.Ink);
            digit.HorizontalAlignment = HorizontalAlignment.Center;
            digit.VerticalAlignment = VerticalAlignment.Center;
            var circle = new Grid { Width = 112, Height = 112, HorizontalAlignment = HorizontalAlignment.Center };
            circle.Children.Add(new Border { Background = hue.VividBrush, CornerRadius = new CornerRadius(56) });
            circle.Children.Add(digit);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(circle);
            var sub = Theme.MetaLabel("누르면 취소돼요");
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.Margin = new Thickness(0, 14, 0, 0);
            stack.Children.Add(sub);
            var card = Theme.Card(stack, 26);
            card.Margin = new Thickness(18);
            Content = card;
            if (preview) return;

            MouseLeftButtonDown += delegate { End(false); };
            timer.Tick += delegate
            {
                left--;
                if (left <= 0)
                {
                    End(true);
                    return;
                }
                digit.Text = left.ToString();
                digit.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(180)));
            };
            SourceInitialized += delegate { ScreenArea.Float(this, false); };
            Loaded += delegate { timer.Start(); };
        }

        void End(bool go)
        {
            if (finished) return;
            finished = true;
            timer.Stop();
            var h = Done;
            Close();
            if (h == null) return;
            if (!go)
            {
                h(false);
                return;
            }
            // 이 창이 화면에서 사라진 뒤에 찍기 시작한다.
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            wait.Tick += delegate
            {
                wait.Stop();
                h(true);
            };
            wait.Start();
        }
    }

    /// 녹화 중 떠 있는 막대. 녹화본에는 찍히지 않고, 눌러도 다른 프로그램의 포커스를 빼앗지 않는다.
    sealed class RecordBar : Window
    {
        readonly ScreenRecorder recorder;
        readonly TextBlock timeText;
        readonly TextBlock stateText;
        readonly Border dot;
        readonly Button pauseButton;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly bool preview;

        public RecordBar(ScreenRecorder r, Rect place, Rect? part, bool previewPaused)
        {
            recorder = r;
            preview = r == null;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = "화면 녹화 · " + AppInfo.Name;
            FontFamily = Theme.Font;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Theme.Coral.VividBrush, VerticalAlignment = VerticalAlignment.Center };
            timeText = Theme.Text("00:00", 17, FontWeights.Bold, Theme.Ink);
            timeText.FontFamily = Theme.MonoFont;
            timeText.VerticalAlignment = VerticalAlignment.Center;
            timeText.Margin = new Thickness(9, 0, 0, 0);
            stateText = Theme.Text("녹화 중", 13, FontWeights.Normal, Theme.Ink3);
            stateText.VerticalAlignment = VerticalAlignment.Center;
            stateText.Margin = new Thickness(9, 0, 16, 0);
            pauseButton = Small("잠시 멈춤", "Line");
            pauseButton.Margin = new Thickness(0, 0, 8, 0);
            var stop = Small("끝내기", "Primary");

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(timeText);
            row.Children.Add(stateText);
            row.Children.Add(pauseButton);
            row.Children.Add(stop);

            var g = new Grid { Margin = new Thickness(18, 14, 18, 24) };
            g.Children.Add(new Border { Background = Theme.Sheet, CornerRadius = new CornerRadius(20), Effect = Theme.Shadow(Theme.InkColor, 22, 6, 0.2) });
            g.Children.Add(new Border { Background = Theme.Sheet, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Padding = new Thickness(16, 8, 9, 8), Child = row, Cursor = Cursors.SizeAll });
            Content = g;

            if (preview)
            {
                if (previewPaused)
                {
                    timeText.Text = "12:34";
                    SetPaused(true);
                }
                return;
            }

            Left = -30000;
            Top = -30000;
            MouseLeftButtonDown += delegate
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
            };
            pauseButton.Click += delegate
            {
                if (recorder.Paused) recorder.Resume();
                else recorder.Pause();
                SetPaused(recorder.Paused);
            };
            stop.Click += delegate { RecordUi.Stop(); };
            timer.Tick += delegate { timeText.Text = TimeOf(recorder.Seconds); };
            SourceInitialized += delegate { ScreenArea.Float(this, false); };
            Loaded += delegate
            {
                Place(place, part);
                timer.Start();
                Blink(true);
            };
            Closed += delegate { timer.Stop(); };
        }

        static Button Small(string text, string style)
        {
            var b = Theme.MakeButton(text, style);
            b.Height = 36;
            b.FontSize = 14;
            b.Padding = new Thickness(14, 0, 14, 0);
            b.Focusable = false;
            return b;
        }

        static string TimeOf(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") : t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }

        void Place(Rect place, Rect? part)
        {
            double w = ActualWidth, h = ActualHeight;
            Left = place.X + (place.Width - w) / 2;
            Top = place.Y;
            if (part == null) return;
            // 영역을 골랐으면 그 바로 아래(자리가 없으면 위)에 둔다.
            Rect r = part.Value;
            Left = Math.Max(place.X, Math.Min(place.Right - w, r.X + (r.Width - w) / 2));
            if (r.Bottom + h <= place.Bottom - 40) Top = r.Bottom + 2;
            else if (r.Top - h >= place.Top) Top = r.Top - h - 2;
            else Top = r.Top;
        }

        void Blink(bool on)
        {
            if (!on)
            {
                dot.BeginAnimation(OpacityProperty, null);
                dot.Opacity = 1;
                return;
            }
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }

        void SetPaused(bool paused)
        {
            dot.Background = paused ? Theme.Slate.VividBrush : Theme.Coral.VividBrush;
            stateText.Text = Theme.Keep(paused ? "멈춤" : "녹화 중");
            pauseButton.Content = paused ? "이어서" : "잠시 멈춤";
            if (!preview) Blink(!paused);
        }
    }

    /// 고른 영역의 테두리. 클릭은 그대로 아래 창으로 간다.
    sealed class AreaFrame : Window
    {
        const double Gap = 3;

        public AreaFrame(Rect part)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            Left = part.X - Gap;
            Top = part.Y - Gap;
            Width = part.Width + Gap * 2;
            Height = part.Height + Gap * 2;
            Title = "녹화 영역 · " + AppInfo.Name;
            Content = new Border { BorderBrush = Theme.HueOf("record").VividBrush, BorderThickness = new Thickness(2.5), CornerRadius = new CornerRadius(3) };
            SourceInitialized += delegate { ScreenArea.Float(this, true); };
        }
    }
}
