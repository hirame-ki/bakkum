// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 변환 작업 줄 세우기와 화면 오른쪽 아래 진행 알림.
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

namespace Bakkum
{
    public class Job
    {
        public readonly MenuAction Action;
        public readonly ActionOption Option;
        public readonly List<string> Files;
        public readonly List<string> Outputs = new List<string>();
        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public bool Headless;
        public bool AutoYes;
        public volatile bool Cancelled;
        public Toast Toast;
        /// 시작 전에 물어본 값(암호, 목표 용량, 번역 언어 등)
        public readonly Dictionary<string, string> Params = new Dictionary<string, string>();
        /// 창에서 만든 작업 내용(사진 손질, 쪽 편집 등)
        public object Payload;
        readonly List<IDisposable> resources = new List<IDisposable>();

        public Job(MenuAction a, ActionOption o, List<string> files)
        {
            Action = a;
            Option = o;
            Files = files;
        }

        public string Param(string key, string fallback)
        {
            string v;
            return Params.TryGetValue(key, out v) && v != null ? v : fallback;
        }

        public string Title
        {
            get { return Option == null || Option.Id == "go" ? Action.Label : Action.Label + " · " + Option.Label; }
        }

        /// 진행 상황(0~1, 모르면 음수)
        public void Report(string text, double fraction)
        {
            if (Headless)
            {
                Cli.Print("  … " + text);
                return;
            }
            var t = Toast;
            if (t == null) return;
            t.Dispatcher.BeginInvoke(new Action(delegate { t.SetProgress(text, fraction); }));
        }

        public void Problem(string file, string message)
        {
            lock (Problems) Problems.Add((file == null ? "" : Util.ShortName(file) + " — ") + message);
        }

        public void Output(string path)
        {
            lock (Outputs) Outputs.Add(path);
        }

        public void Note(string message)
        {
            lock (Notes) Notes.Add(message);
        }

        public void Check()
        {
            if (Cancelled) throw new OperationCanceledException();
        }

        /// 작업 동안 한 번 띄워 여러 파일에 쓰는 엔진(한글·한셀 등). 작업이 끝나면 닫는다.
        public T Use<T>(Func<T> make) where T : class, IDisposable
        {
            foreach (var r in resources)
            {
                var t = r as T;
                if (t != null) return t;
            }
            var x = make();
            resources.Add(x);
            return x;
        }

        public void ReleaseResources()
        {
            for (int i = resources.Count - 1; i >= 0; i--)
            {
                try { resources[i].Dispose(); } catch (Exception ex) { Log.Write("정리 실패: " + ex.GetType().Name); }
            }
            resources.Clear();
        }

        /// 화면에서 사용자에게 묻기(AI 보내기 동의 등). 시험 모드는 정해 둔 답을 쓴다.
        public T Ask<T>(Func<T> ask, T headlessAnswer)
        {
            if (Headless) return headlessAnswer;
            T answer = headlessAnswer;
            Application.Current.Dispatcher.Invoke(new Action(delegate { answer = ask(); }));
            return answer;
        }
    }

    public static class Jobs
    {
        static readonly object Gate = new object();
        static readonly Queue<Job> Waiting = new Queue<Job>();
        static readonly AutoResetEvent Signal = new AutoResetEvent(false);
        // 이번에 켜 있는 동안 만든 결과(새것이 앞). 파일에 남기지 않는다.
        static readonly List<string> Recent = new List<string>();
        static Thread worker;
        static Job running;

        public static void Start(MenuAction a, ActionOption o, List<string> files)
        {
            Start(new Job(a, o, files));
        }

        public static List<string> RecentOutputs()
        {
            lock (Gate) return Recent.ToList();
        }

        /// 작업 줄 밖에서 만든 결과(화면 녹화)도 최근 결과에 넣는다.
        public static void AddRecent(string path)
        {
            lock (Gate)
            {
                Recent.Remove(path);
                Recent.Insert(0, path);
                if (Recent.Count > 10) Recent.RemoveRange(10, Recent.Count - 10);
            }
        }

        /// 하고 있거나 기다리는 일이 있는지
        public static bool Busy
        {
            get { lock (Gate) return running != null || Waiting.Count > 0; }
        }

        public static void Start(Job job)
        {
            // 끝내는 중이면 새 일은 받지 않는다(끝내기가 기다려 주지 않으니 반쯤 쓴 결과가 남는다)
            if (App.Quitting) return;
            job.Toast = Toast.ForJob(job);
            bool queued;
            lock (Gate)
            {
                queued = running != null || Waiting.Count > 0;
                Waiting.Enqueue(job);
                if (worker == null)
                {
                    worker = new Thread(Loop) { IsBackground = true, Name = "jobs" };
                    worker.SetApartmentState(ApartmentState.STA);
                    worker.Start();
                }
            }
            if (queued) job.Report("앞의 일이 끝나면 시작해요", -1);
            Signal.Set();
        }

        static void Loop()
        {
            while (true)
            {
                Job job = null;
                lock (Gate)
                {
                    if (Waiting.Count > 0)
                    {
                        job = Waiting.Dequeue();
                        running = job;
                    }
                }
                if (job == null)
                {
                    Signal.WaitOne();
                    continue;
                }
                RunNow(job);
                lock (Gate)
                {
                    running = null;
                    lock (job.Outputs)
                    {
                        foreach (string o in job.Outputs)
                        {
                            Recent.Remove(o);
                            Recent.Insert(0, o);
                        }
                    }
                    if (Recent.Count > 10) Recent.RemoveRange(10, Recent.Count - 10);
                }
                var t = job.Toast;
                if (t != null) t.Dispatcher.BeginInvoke(new Action(delegate { t.Done(job); }));
            }
        }

        public static void RunNow(Job job)
        {
            Util.UsedFallbackDir = false;
            try
            {
                job.Check();
                Conversions.Execute(job);
            }
            catch (OperationCanceledException)
            {
                job.Cancelled = true;
            }
            catch (UserFacingException ex)
            {
                job.Problem(null, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Write("작업 실패 " + job.Action.Id + "/" + job.Option.Id + ": " + ex.GetType().Name + " @ " + ex.TargetSite);
                job.Problem(null, "바꾸지 못했어요. (" + ex.GetType().Name + ")");
            }
            finally
            {
                job.ReleaseResources();
                // 결과가 원본 옆이 아닌 곳에 있으면 어디에 두었는지 맨 먼저 알린다.
                if (Util.UsedFallbackDir && job.Outputs.Count > 0)
                {
                    lock (job.Notes) job.Notes.Insert(0, "원본 폴더에는 저장할 수 없어서 '문서\\변환 결과' 폴더에 두었어요.");
                }
            }
        }

        public static void CancelAll()
        {
            lock (Gate)
            {
                foreach (var j in Waiting) j.Cancelled = true;
                if (running != null) running.Cancelled = true;
            }
        }
    }

    /// 오른쪽 아래 알림 카드: 할 일 색 아이콘 칩 + 할 일 색 진행 막대(얼마나 남았는지 모를 때는 무지개 막대가 흐른다).
    /// 카드 둘레의 투명한 틈은 그림자 자리. 포커스를 빼앗지 않는다.
    public class Toast : Window
    {
        static readonly Thickness Room = new Thickness(20, 14, 20, 26);
        static readonly List<Toast> Shown = new List<Toast>();
        readonly TextBlock titleText;
        readonly TextBlock statusText;
        readonly Grid track;
        readonly Border fill;
        readonly TranslateTransform fillMove = new TranslateTransform();
        readonly StackPanel body;
        readonly StackPanel buttons;
        readonly Button closeButton;
        readonly Grid chipHost;
        readonly TranslateTransform enter = new TranslateTransform();
        readonly Job job;
        readonly Hue hue;
        DispatcherTimer closer;
        double fraction = -1;
        bool finished;
        bool flowing;

        Toast(string title, Job job, Hue hue, FrameworkElement chip)
        {
            this.job = job;
            this.hue = hue;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Width = 384 + Room.Left + Room.Right;
            SizeToContent = SizeToContent.Height;
            Title = AppInfo.Name;
            try { Icon = App.WindowIcon; } catch { }
            FontFamily = Theme.Font;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            var stack = new StackPanel();
            var head = new DockPanel();
            closeButton = new Button
            {
                Style = Theme.GetStyle("Quiet"),
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Focusable = false,
                ToolTip = "닫기",
                Content = Theme.Glyph("close", 14, Theme.Ink3, 2),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, -3, -5, 0)
            };
            closeButton.Click += delegate { OnClose(); };
            DockPanel.SetDock(closeButton, Dock.Right);
            head.Children.Add(closeButton);
            chipHost = new Grid { Width = 42, Height = 42, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 13, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
            chipHost.Children.Add(chip);
            DockPanel.SetDock(chipHost, Dock.Left);
            head.Children.Add(chipHost);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleText = Theme.Text(title, 15.5, FontWeights.Bold, Theme.Ink);
            titleText.LineHeight = 21;
            words.Children.Add(titleText);
            statusText = Theme.Text("", 13.5, FontWeights.Normal, Theme.Ink2);
            statusText.LineHeight = 20;
            statusText.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(statusText);
            head.Children.Add(words);
            stack.Children.Add(head);

            track = new Grid { Height = 6, Margin = new Thickness(55, 13, 4, 2) };
            track.Children.Add(new Border { Background = Theme.Frozen(Theme.Hex(0xEFEAE2)), CornerRadius = new CornerRadius(3) });
            fill = new Border { CornerRadius = new CornerRadius(3), Background = hue.VividBrush, HorizontalAlignment = HorizontalAlignment.Left, Width = 0, RenderTransform = fillMove };
            track.Children.Add(fill);
            track.SizeChanged += delegate
            {
                track.Clip = new RectangleGeometry(new Rect(0, 0, track.ActualWidth, track.ActualHeight), 3, 3);
                UpdateFill(false);
            };
            stack.Children.Add(track);

            body = new StackPanel { Margin = new Thickness(55, 0, 4, 0) };
            stack.Children.Add(body);
            buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(55, 14, 0, 0), Visibility = Visibility.Collapsed };
            stack.Children.Add(buttons);

            var g = new Grid { Margin = Room, RenderTransform = enter };
            g.Children.Add(new Border { Background = Theme.Sheet, CornerRadius = new CornerRadius(20), Effect = Theme.Shadow(Theme.InkColor, 26, 7, 0.17) });
            g.Children.Add(new Border
            {
                Background = Theme.Sheet,
                BorderBrush = Theme.Frozen(Theme.Hex(0xEFEAE2)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(18, 17, 16, 17),
                Child = stack
            });
            Content = g;

            SizeChanged += delegate { Reflow(); };
            MouseEnter += delegate { if (closer != null) closer.Stop(); };
            MouseLeave += delegate { if (closer != null) closer.Start(); };
            Closed += delegate
            {
                Shown.Remove(this);
                Reflow();
            };
        }

        public static Toast ForJob(Job job)
        {
            var hue = Theme.HueOf(job.Action.Id);
            var t = new Toast(job.Title, job, hue, Theme.GlyphChip(job.Action.Id, hue, 42));
            t.SetProgress("준비하는 중", -1);
            t.Present();
            return t;
        }

        /// 보기용(그림 확인): 화면에 띄우지 않은 진행 알림 하나
        public static Toast ForPreview(MenuAction a, string title, string status, double f)
        {
            var hue = Theme.HueOf(a.Id);
            var t = new Toast(title, null, hue, Theme.GlyphChip(a.Id, hue, 42));
            t.SetProgress(status, f);
            return t;
        }

        /// 보기용(그림 확인): 끝난 알림 하나
        public static Toast DoneForPreview(Job j)
        {
            var hue = Theme.HueOf(j.Action.Id);
            var t = new Toast(j.Title, null, hue, Theme.GlyphChip(j.Action.Id, hue, 42));
            t.Done(j);
            if (t.closer != null) t.closer.Stop();
            return t;
        }

        public static void ShowMessage(string title, string message, bool isError)
        {
            var t = Message(title, message, isError);
            t.Present();
            t.CloseLater(isError ? 12 : 6);
        }

        /// 단추가 있는 알림(예: 새 버전이 있어요 [지금 바꾸기] [나중에])
        public static void ShowOffer(string title, string message, string action, Action run)
        {
            var t = Offer(title, message, action, run);
            t.Present();
            t.CloseLater(30);
        }

        public static Toast Offer(string title, string message, string action, Action run)
        {
            var t = Message(title, message, false);
            var go = SmallButton(action, "Primary");
            go.Click += delegate
            {
                t.FadeClose();
                run();
            };
            var later = SmallButton("나중에", "Line");
            later.Click += delegate { t.FadeClose(); };
            t.buttons.Children.Add(go);
            t.buttons.Children.Add(later);
            t.buttons.Visibility = Visibility.Visible;
            return t;
        }

        /// 작업이 아닌 일의 진행 알림(새 버전 받기). 끝나면 Dismiss.
        public static Toast Progress(string title, Hue hue, string glyph)
        {
            var t = new Toast(title, null, hue, Theme.GlyphChip(glyph, hue, 42));
            t.SetProgress("준비하는 중", -1);
            t.Present();
            return t;
        }

        public void Dismiss()
        {
            finished = true;
            FadeClose();
        }

        /// 도중에 멈출 수 없는 일(녹화 저장)은 닫기 단추를 숨긴다.
        public void NoCancel()
        {
            closeButton.Visibility = Visibility.Hidden;
        }

        public static Toast Message(string title, string message, bool isError)
        {
            var chip = isError ? Theme.GlyphChip("close", Theme.Coral, 42) : Theme.Logo(42);
            var t = new Toast(title, null, isError ? Theme.Coral : Theme.Slate, chip);
            t.finished = true;
            t.track.Visibility = Visibility.Collapsed;
            // 오류는 산호색 칩이 이미 알려 주므로, 설명은 경고 상자 없이 본문 글로 담백하게
            t.statusText.Text = Theme.Keep(message);
            return t;
        }

        void Present()
        {
            Shown.Add(this);
            Opacity = 0;
            Show();
            Reflow();
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            enter.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        static void Reflow()
        {
            var wa = SystemParameters.WorkArea;
            double y = wa.Bottom;
            foreach (var t in Shown)
            {
                double h = t.ActualHeight > 0 ? t.ActualHeight : 140;
                y -= h;
                t.Left = wa.Right - t.Width;
                t.Top = Math.Max(wa.Top, y);
                // 카드 사이 12: 아래 틈(26)과 위 틈(14)이 겹치는 만큼 올린다
                y += Room.Top + Room.Bottom - 12;
            }
        }

        public void SetProgress(string text, double f)
        {
            if (finished) return;
            statusText.Text = Theme.Keep(text);
            fraction = f;
            UpdateFill(true);
        }

        static Brush rainbow;

        static Brush Rainbow
        {
            get
            {
                if (rainbow == null)
                {
                    var stops = new GradientStopCollection();
                    var hues = Theme.Rainbow;
                    for (int i = 0; i < hues.Length; i++) stops.Add(new GradientStop(hues[i].Vivid, i / (double)(hues.Length - 1)));
                    var b = new LinearGradientBrush(stops, 0);
                    b.Freeze();
                    rainbow = b;
                }
                return rainbow;
            }
        }

        void UpdateFill(bool animate)
        {
            double w = track.ActualWidth;
            if (w <= 0) return;
            if (fraction < 0)
            {
                // 얼마나 남았는지 모를 때: 무지개 막대가 왼쪽에서 오른쪽으로 흐른다
                double seg = w * 0.36;
                fill.BeginAnimation(WidthProperty, null);
                fill.Width = seg;
                fill.Background = Rainbow;
                if (!flowing)
                {
                    flowing = true;
                    var a = new DoubleAnimation(-seg, w, TimeSpan.FromMilliseconds(1500)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                    fillMove.BeginAnimation(TranslateTransform.XProperty, a);
                }
                return;
            }
            if (flowing)
            {
                flowing = false;
                fillMove.BeginAnimation(TranslateTransform.XProperty, null);
                fillMove.X = 0;
                fill.Width = 0;
            }
            fill.Background = hue.VividBrush;
            double target = w * Math.Max(0, Math.Min(1, fraction));
            if (!animate)
            {
                fill.BeginAnimation(WidthProperty, null);
                fill.Width = target;
                return;
            }
            fill.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(240)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        void OnClose()
        {
            if (!finished && job != null)
            {
                job.Cancelled = true;
                statusText.Text = Theme.Keep("멈추는 중 · 지금 하던 파일까지만 해요");
                closeButton.IsEnabled = false;
                return;
            }
            FadeClose();
        }

        void FadeClose()
        {
            if (closer != null) closer.Stop();
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            a.Completed += delegate { Close(); };
            BeginAnimation(OpacityProperty, a);
            enter.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, TimeSpan.FromMilliseconds(160)));
        }

        void CloseLater(int seconds)
        {
            closer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            closer.Tick += delegate { FadeClose(); };
            if (!IsMouseOver) closer.Start();
        }

        /// 칩을 바꾸며 톡 튀게(다 됐을 때 체크, 못 했을 때 X)
        void SwapChip(FrameworkElement chip)
        {
            chipHost.Children.Clear();
            chipHost.Children.Add(chip);
            var scale = new ScaleTransform(1, 1);
            chipHost.RenderTransform = scale;
            if (!IsVisible) return;
            var a = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        /// 알림 안의 작은 단추(높이 36). 알림 창이 포커스를 빼앗지 않게 Focusable은 끈다.
        static Button SmallButton(string text, string style)
        {
            var b = Theme.MakeButton(text, style);
            b.Height = 36;
            b.FontSize = 14;
            b.Padding = new Thickness(16, 0, 16, 0);
            b.Margin = new Thickness(0, 0, 8, 0);
            b.Focusable = false;
            return b;
        }

        public void Done(Job j)
        {
            finished = true;
            closeButton.IsEnabled = true;
            closeButton.Visibility = Visibility.Visible;
            fillMove.BeginAnimation(TranslateTransform.XProperty, null);
            track.Visibility = Visibility.Collapsed;
            int made = j.Outputs.Count;
            int bad = j.Problems.Count;

            if (j.Cancelled && bad == 0)
            {
                titleText.Text = Theme.Keep("멈췄어요 · " + j.Title);
                statusText.Text = Theme.Keep(made > 0 ? "멈추기 전에 파일 " + made + "개를 만들었어요." : "남은 일은 하지 않았어요.");
                SwapChip(Theme.GlyphChip("close", Theme.Slate, 42));
            }
            else if (bad == 0 && made > 0)
            {
                titleText.Text = Theme.Keep("다 됐어요 · " + j.Title);
                statusText.Text = Theme.Keep(made == 1 ? Path.GetFileName(j.Outputs[0]) : "파일 " + made + "개를 만들었어요.");
                SwapChip(Theme.GlyphChip("check", hue, 42));
            }
            else if (made > 0)
            {
                titleText.Text = Theme.Keep("일부만 됐어요 · " + j.Title);
                statusText.Text = Theme.Keep("파일 " + made + "개를 만들고, " + bad + "개는 못 했어요.");
                SwapChip(Theme.GlyphChip("check", Theme.Orange, 42));
            }
            else if (bad > 0)
            {
                titleText.Text = Theme.Keep("바꾸지 못했어요 · " + j.Title);
                statusText.Visibility = Visibility.Collapsed;
                SwapChip(Theme.GlyphChip("close", Theme.Coral, 42));
            }
            else
            {
                titleText.Text = Theme.Keep(j.Title);
                statusText.Text = Theme.Keep("만든 파일이 없어요.");
            }

            if (made > 1)
            {
                var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var o in j.Outputs.Take(3))
                {
                    var t = Theme.Text(Util.ShortName(o), 13, FontWeights.Normal, Theme.Ink3);
                    t.Margin = new Thickness(0, 1, 0, 1);
                    list.Children.Add(t);
                }
                if (made > 3) list.Children.Add(Theme.Text("외 " + (made - 3) + "개", 13, FontWeights.Normal, Theme.Ink3));
                body.Children.Add(list);
            }
            foreach (var n in j.Notes.Take(3))
            {
                var t = Theme.Note(n, hue);
                t.Margin = new Thickness(0, 10, 0, 0);
                body.Children.Add(t);
            }
            if (bad > 0)
            {
                string msg = string.Join("\n", j.Problems.Take(4));
                if (bad > 4) msg += "\n외 " + (bad - 4) + "개";
                var w = Theme.Warning(msg);
                w.Margin = new Thickness(0, 10, 0, 0);
                body.Children.Add(w);
            }

            if (made > 0)
            {
                string first = j.Outputs[0];
                // 파일이 하나면 바로 열어 볼 수 있게 [열기]를 앞에 둔다
                if (made == 1)
                {
                    var view = SmallButton("열기", "Primary");
                    view.Click += delegate { Util.OpenFile(first); FadeClose(); };
                    buttons.Children.Add(view);
                }
                var open = SmallButton("폴더 열기", "Line");
                open.Click += delegate { Util.Reveal(first); FadeClose(); };
                buttons.Children.Add(open);
                var pairs = CompareWindow.PairsFor(j);
                if (pairs != null)
                {
                    var look = SmallButton("전후 비교", "Line");
                    look.Click += delegate { CompareWindow.Open(pairs, hue); FadeClose(); };
                    buttons.Children.Add(look);
                }
                buttons.Visibility = Visibility.Visible;
                if (job != null && AppSettings.Current.RevealResult && !j.Cancelled) Util.Reveal(first);
            }
            if (bad == 0 && job != null) CloseLater(made > 0 ? 9 : 5);
        }
    }
}
