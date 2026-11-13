// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
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
        // 웹캠도 같이 찍을 때: 화면 녹화와 같은 시계를 쓰는 두 번째 녹화
        static ScreenRecorder camRecorder;
        static IFrameGrabber camGrabber;
        static CamBubble bubble;

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

        static StackPanel Choices(string area, string sound, string cam, Action<string> setArea, Action<string> setSound, Action<string> setCam, bool severalScreens, bool hasCam)
        {
            var stack = new StackPanel();
            var first = Prompts.Caption("어디를 녹화할까요?");
            first.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(first);
            stack.Children.Add(Prompts.Chips(Paper, new[] { "full", "area" }, new[] { "전체 화면", "영역 고르기" }, area, setArea));
            stack.Children.Add(Prompts.Caption("소리"));
            stack.Children.Add(Prompts.Chips(Paper, new[] { "none", "mic", "computer", "both" }, new[] { "소리 없음", "마이크", "컴퓨터 소리", "둘 다" }, sound, setSound));
            if (hasCam)
            {
                stack.Children.Add(Prompts.Caption("웹캠"));
                stack.Children.Add(Prompts.Chips(Paper, new[] { "none", "cam" }, new[] { "안 찍기", "같이 찍기" }, cam, setCam));
            }
            string text = "비디오 폴더에 MP4로 저장돼요. 녹화 중에는 화면 위 작은 막대에서 잠시 멈추거나 끝낼 수 있어요.";
            if (hasCam) text += " 웹캠을 같이 찍으면 얼굴 영상이 녹화 파일 옆에 따로 저장되고, [영상 편집]에 녹화 파일을 놓으면 오른쪽 아래에 동그랗게 얹혀요. 녹화하는 동안 보이는 작은 얼굴 창은 녹화에 찍히지 않아요.";
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
            string cam = Known(settings.RecordCam, "none", "cam");
            bool hasCam = CamGrabber.Exists();
            if (!hasCam) cam = "none";
            var panel = Choices(area, sound, cam, v => area = v, v => sound = v, v => cam = v, ScreenArea.MonitorCount > 1, hasCam);
            if (Dialog.Ask(null, Paper, "record", "화면을 녹화할까요?", null, panel, "취소", "녹화 시작") != 1) return;
            settings.RecordArea = area;
            settings.RecordSound = sound;
            if (hasCam) settings.RecordCam = cam;
            settings.SaveQuietly();
            busy = true;
            try
            {
                Box real = ScreenArea.MonitorUnderCursor();
                Rect place = ScreenArea.MonitorDip();
                if (area != "area")
                {
                    Arm(real, place, null, sound, cam);
                    return;
                }
                var picker = new AreaPicker(real, place, AreaRatio.Known(settings.RecordRatio), null);
                picker.Done = delegate (Rect? part)
                {
                    // 고른 비율은 취소해도 다음에 그대로 쓴다
                    settings.RecordRatio = picker.Ratio;
                    settings.SaveQuietly();
                    if (part == null)
                    {
                        busy = false;
                        return;
                    }
                    Rect p = part.Value;
                    Arm(BoxFor(real, p), place, new Rect(place.X + p.X * place.Width, place.Y + p.Y * place.Height, p.Width * place.Width, p.Height * place.Height), sound, cam);
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

        /// 화면 전체를 1로 본 비율 → 그 모니터의 실제 픽셀 영역
        internal static Box BoxFor(Box real, Rect p)
        {
            return new Box(real.X + (int)Math.Round(p.X * real.Width), real.Y + (int)Math.Round(p.Y * real.Height), (int)Math.Round(p.Width * real.Width), (int)Math.Round(p.Height * real.Height));
        }

        /// 시험에서만: 진짜 비디오 폴더 대신 쓸 곳
        internal static string TestFolder;

        /// 시험에서만: 마지막으로 저장한 파일
        internal static string LastFile;

        /// 시험에서만: 진짜 웹캠 대신 쓸 가짜 화면(카메라를 켜지 않는다)
        internal static IFrameGrabber TestCam;

        /// 시험에서만: 마지막으로 저장한 웹캠 파일
        internal static string LastCamFile;

        internal static void ArmForTest(Box box, Rect place, Rect? part)
        {
            busy = true;
            Arm(box, place, part, "none", TestCam != null ? "cam" : "none");
        }

        /// 웹캠 녹화를 준비한다(카메라가 켜진다). 실패하면 사용자에게 보일 문장으로 예외.
        static ScreenRecorder BuildCam(ScreenRecorder main, string path)
        {
            IFrameGrabber g = TestCam ?? (IFrameGrabber)CamGrabber.Open();
            ScreenRecorder r = null;
            try
            {
                r = new ScreenRecorder(CamLog.NameFor(path), g, new List<ISoundSource>(), Fps, main.Clock);
                r.Prepare();
                camGrabber = g;
                return r;
            }
            catch
            {
                if (r != null) r.Discard();
                else g.Dispose();
                throw;
            }
        }

        static void DiscardCam()
        {
            var c = camRecorder;
            camRecorder = null;
            camGrabber = null;
            if (c != null) c.Discard();
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
                made.Cursor = new CursorLog(ScreenArea.Even(box));
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

        static void Arm(Box box, Rect place, Rect? part, string sound, string cam)
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
            camRecorder = null;
            camGrabber = null;
            if (cam == "cam")
            {
                string why = null;
                try
                {
                    camRecorder = BuildCam(made, path);
                }
                catch (UserFacingException ex)
                {
                    why = ex.Message;
                }
                catch (Exception ex)
                {
                    Log.Write("웹캠 녹화 준비 실패: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                    why = CamGrabber.Explain(ex);
                }
                if (why != null && Dialog.Ask(null, Paper, "record", "웹캠을 켜지 못했어요", why + " 화면만 녹화할까요?", null, "취소", "화면만 녹화") != 1)
                {
                    made.Discard();
                    busy = false;
                    return;
                }
            }
            var count = new Countdown(place, false);
            count.Done = delegate (bool go)
            {
                if (!go)
                {
                    if (bubble != null) bubble.Close();
                    bubble = null;
                    DiscardCam();
                    made.Discard();
                    busy = false;
                    return;
                }
                Launch(made, path, place, part);
            };
            count.Show();
            var real = camGrabber as CamGrabber;
            if (real != null)
            {
                // 3·2·1 동안에도 얼굴이 보이게(웹캠이 켜지는 데 1초쯤 걸린다)
                bubble = new CamBubble(real, place);
                bubble.Show();
            }
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
                DiscardCam();
                Stop();
                return;
            }
            var c = camRecorder;
            if (c != null)
            {
                var real = camGrabber as CamGrabber;
                if (real != null) real.ResetCounters();
                try
                {
                    // 같은 시계라 화면 녹화가 시작한 때를 0으로 같이 적는다
                    c.Start();
                }
                catch (Exception ex)
                {
                    Log.Write("웹캠 녹화 시작 실패: " + ex.GetType().Name);
                    DiscardCam();
                }
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
            if (bubble != null) bubble.Close();
            bubble = null;
        }

        /// 웹캠 녹화 마무리를 시작한다(화면 녹화 마무리와 같이 돈다). 끝나면 done이 켜진다.
        static ManualResetEvent FinishCam(ScreenRecorder c, string[] error)
        {
            if (c == null) return null;
            var done = new ManualResetEvent(false);
            c.Finish(delegate (string e)
            {
                error[0] = e;
                done.Set();
            });
            return done;
        }

        /// 웹캠 녹화가 끝난 뒤: 녹화 파일과 잇는 기록을 남기고 알림에 쓸 말을 돌려준다.
        static string AfterCam(string path, ScreenRecorder c, IFrameGrabber g, ManualResetEvent done, string camError, bool screenOk)
        {
            if (c == null) return null;
            if (!done.WaitOne(30000)) Log.Write("웹캠 녹화 마무리가 늦음");
            done.Close();
            string camFile = c.OutputPath;
            if (!screenOk) return null;
            if (camError != null || !File.Exists(camFile)) return "웹캠 영상은 저장하지 못했어요." + (camError != null ? " (" + camError + ")" : "");
            var real = g as CamGrabber;
            CamLog.Save(path, camFile, real != null ? real.Lag : 0);
            LastCamFile = camFile;
            string note = "얼굴 영상도 따로 저장했어요(" + Path.GetFileName(camFile) + "). [영상 편집]에 녹화 파일을 놓으면 얼굴이 오른쪽 아래에 동그랗게 얹혀요.";
            if (real != null && real.Frames == 0) note = "웹캠에서 영상이 오지 않아 얼굴 영상이 비어 있어요. 다른 프로그램이 웹캠을 쓰고 있지 않은지 확인해 주세요.";
            else
            {
                if (real != null && real.DarkShare >= 0.9) note += " 그런데 웹캠 영상이 거의 까맣게 찍혔어요. 렌즈 덮개가 닫혀 있거나 너무 어두웠는지 확인해 주세요. 얼굴이 필요 없으면 [영상 편집]에서 동그라미를 골라 지우면 돼요.";
                if (real != null && real.Stalled >= 3) note += " 녹화 중 웹캠 영상이 " + (int)Math.Round(real.Stalled) + "초쯤 멈춰 있었어요.";
            }
            return note;
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
            var c = camRecorder;
            var g = camGrabber;
            var camError = new string[1];
            var camDone = FinishCam(c, camError);
            r.Finish(delegate (string error)
            {
                string camNote = AfterCam(path, c, g, camDone, camError[0], error == null);
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
                    if (camNote != null) job.Note(camNote);
                    recorder = null;
                    camRecorder = null;
                    camGrabber = null;
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
            var c = camRecorder;
            var g = camGrabber;
            var camError = new string[1];
            var camDone = FinishCam(c, camError);
            using (var done = new ManualResetEvent(false))
            {
                string error = null;
                r.Finish(delegate (string e)
                {
                    error = e;
                    done.Set();
                });
                if (!done.WaitOne(20000)) Log.Write("끝낼 때 녹화 저장이 늦음");
                AfterCam(outputPath, c, g, camDone, camError[0], error == null);
            }
            recorder = null;
            camRecorder = null;
            camGrabber = null;
            busy = false;
        }

        // ── 보기용(그림 확인): 띄우지 않은 창 ──

        public static Window PreviewOptions()
        {
            return Dialog.Build(null, Paper, "record", "화면을 녹화할까요?", null, Choices("full", "mic", "cam", delegate { }, delegate { }, delegate { }, true, true), "취소", "녹화 시작");
        }

        public static Window PreviewBar(bool paused)
        {
            return new RecordBar(null, new Rect(), null, paused);
        }

        public static Window PreviewCountdown()
        {
            return new Countdown(new Rect(), true);
        }

        /// state: start(고르기 전) · small(너무 작게 그림) · 비율 이름(그 비율로 고른 모습). 150% 배율 화면으로 그린다.
        public static Window PreviewPicker(string state)
        {
            string s = string.IsNullOrEmpty(state) ? "free" : state;
            string ratio = s == "start" || s == "small" ? "free" : AreaRatio.Known(s);
            return new AreaPicker(new Box(0, 0, 1920, 1080), new Rect(0, 0, 1280, 720), ratio, s);
        }
    }

    /// 어디를 녹화할지 고르는 창: 끌어서 그리고, 손잡이로 다듬고, [녹화 시작]으로 정한다.
    /// 비율 칩으로 비율을 정하면 꼭짓점 손잡이만(비율 그대로), 자유면 변 손잡이도 있다.
    /// 계산은 AreaSelect가 실제 픽셀로 하고, 결과는 화면 전체를 1로 본 비율이라 배율이 달라도 맞는다.
    sealed class AreaPicker : Window
    {
        /// 손잡이를 잡는 거리, 새로 그리기를 시작하는 거리, 막대와 영역·화면 끝 사이, 옆에 둘 때 막대의 가장 작은 너비(화면 점)
        const double GripDip = 12, SlopDip = 4, Gap = 12, SideMin = 340;
        static readonly AreaGrip[] GripOrder = { AreaGrip.TopLeft, AreaGrip.Top, AreaGrip.TopRight, AreaGrip.Right, AreaGrip.BottomRight, AreaGrip.Bottom, AreaGrip.BottomLeft, AreaGrip.Left };

        readonly Canvas canvas = new Canvas();
        readonly RectangleGeometry hole = new RectangleGeometry();
        readonly Border edge;
        readonly Border sizeTag;
        readonly TextBlock sizeText;
        readonly FrameworkElement[] grips = new FrameworkElement[8];
        readonly FrameworkElement bar;
        readonly TextBlock barTitle;
        readonly TextBlock barHelp;
        readonly FrameworkElement barButtons;
        readonly AreaSelect select;
        readonly double sx, sy, viewW, viewH;
        bool finished, tooSmall;

        public Action<Rect?> Done;

        /// ratio: 처음 비율(지난번에 고른 것). preview: null이면 진짜로 고른다.
        /// 그림 확인용은 "start"(고르기 전), "small"(너무 작게 그렸을 때), 비율 이름(그 비율로 고른 모습).
        public AreaPicker(Box real, Rect place, string ratio, string preview)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            // 아주 옅게라도 칠해 둔다: 다 투명한 곳(고른 곳 안)을 누르면 아래 프로그램이 눌리기 때문
            Background = Theme.Frozen(Color.FromArgb(1, 0, 0, 0));
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
            viewW = place.Width;
            viewH = place.Height;
            sx = real.Width / place.Width;
            sy = real.Height / place.Height;
            select = new AreaSelect(real.Width, real.Height, ratio);
            var hue = Theme.HueOf("record");

            var whole = new GeometryGroup { FillRule = FillRule.EvenOdd };
            whole.Children.Add(new RectangleGeometry(new Rect(0, 0, place.Width, place.Height)));
            whole.Children.Add(hole);
            canvas.Children.Add(new ShapePath { Data = whole, Fill = Theme.Frozen(Color.FromArgb(120, 0x22, 0x25, 0x2F)) });

            edge = new Border { BorderBrush = hue.VividBrush, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            canvas.Children.Add(edge);

            // 손잡이: 꼭짓점은 동그라미, 변은 짧은 알약(자유 비율에서만 보인다)
            for (int i = 0; i < grips.Length; i++)
            {
                AreaGrip g = GripOrder[i];
                FrameworkElement shape;
                if (AreaMath.IsCorner(g))
                {
                    shape = new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Fill = Theme.Sheet, Stroke = hue.DeepBrush, StrokeThickness = 2 };
                }
                else
                {
                    bool across = g == AreaGrip.Top || g == AreaGrip.Bottom;
                    shape = new Border { Width = across ? 26 : 8, Height = across ? 8 : 26, CornerRadius = new CornerRadius(4), Background = Theme.Sheet, BorderBrush = hue.DeepBrush, BorderThickness = new Thickness(2) };
                }
                shape.IsHitTestVisible = false;
                shape.Visibility = Visibility.Collapsed;
                grips[i] = shape;
                canvas.Children.Add(shape);
            }

            sizeText = Theme.Text("", 13, FontWeights.Bold, Theme.Ink);
            sizeTag = new Border { Background = hue.VividBrush, CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 3), Child = sizeText, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            canvas.Children.Add(sizeTag);

            // 막대: 안내 한 줄 → 비율 칩 → 짧은 도움말 + [취소] [녹화 시작]
            var stack = new StackPanel();
            barTitle = Theme.Text("", 15.5, FontWeights.Bold, Theme.Ink);
            barTitle.Margin = new Thickness(0, 0, 0, 10);
            stack.Children.Add(barTitle);
            var chips = Prompts.Chips(hue, AreaRatio.Ids, AreaRatio.Labels, select.Ratio, delegate (string id)
            {
                select.SetRatio(id);
                tooSmall = false;
                Redraw();
            });
            // 칩·단추는 마우스로만 고른다(화살표 키는 고른 곳을 옮기는 데 쓴다)
            foreach (UIElement c in chips.Children) c.Focusable = false;
            stack.Children.Add(chips);
            var foot = new DockPanel { LastChildFill = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
            var cancel = BarButton("취소", "Quiet");
            var go = BarButton("녹화 시작", "Primary");
            go.Margin = new Thickness(6, 0, 0, 0);
            buttons.Children.Add(cancel);
            buttons.Children.Add(go);
            DockPanel.SetDock(buttons, Dock.Right);
            foot.Children.Add(buttons);
            barButtons = buttons;
            barHelp = Theme.MetaLabel("");
            barHelp.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(barHelp);
            stack.Children.Add(foot);
            bar = Theme.Card(stack, 16);
            bar.Cursor = Cursors.Arrow;
            // 막대를 눌러도 영역을 새로 그리거나 취소하지 않는다
            bar.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };
            bar.MouseRightButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };
            canvas.Children.Add(bar);
            Content = canvas;
            Loaded += delegate { Redraw(); };

            if (preview != null)
            {
                if (preview == "small") tooSmall = true;
                else if (preview != "start") select.Choose(new Rect(real.Width * 0.25, real.Height * 0.28, real.Width * 0.5, real.Height * 0.5));
                Redraw();
                return;
            }

            cancel.Click += delegate { End(null); };
            go.Click += delegate { Confirm(); };
            MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                Point p = Real(e.GetPosition(canvas));
                if (e.ClickCount == 2 && select.Hit(p, GripDip * sx) == AreaGrip.Inside)
                {
                    // 고른 곳 안을 두 번 누르면 시작
                    Confirm();
                    return;
                }
                select.Press(p, GripDip * sx);
                Cursor = CursorFor(select.Held);
                CaptureMouse();
            };
            MouseMove += delegate (object s, MouseEventArgs e)
            {
                Point p = Real(e.GetPosition(canvas));
                if (!select.Pressed)
                {
                    Cursor = CursorFor(select.Hit(p, GripDip * sx));
                    return;
                }
                if (select.Move(p, SlopDip * sx))
                {
                    tooSmall = false;
                    Redraw();
                }
            };
            MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                if (!select.Pressed) return;
                var result = select.Release(Real(e.GetPosition(canvas)), SlopDip * sx);
                ReleaseMouseCapture();
                tooSmall = result == AreaRelease.TooSmall;
                Redraw();
            };
            // 다른 창이 마우스를 가져가면 끄던 것만 그만둔다(고른 곳은 그대로)
            LostMouseCapture += delegate
            {
                if (!select.Pressed) return;
                select.Abort();
                Redraw();
            };
            MouseRightButtonDown += delegate { End(null); };
            PreviewKeyDown += delegate (object s, KeyEventArgs e)
            {
                int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                switch (e.Key)
                {
                    case Key.Escape: End(null); break;
                    case Key.Enter: Confirm(); break;
                    case Key.Left: select.Nudge(-step, 0); Redraw(); break;
                    case Key.Right: select.Nudge(step, 0); Redraw(); break;
                    case Key.Up: select.Nudge(0, -step); Redraw(); break;
                    case Key.Down: select.Nudge(0, step); Redraw(); break;
                    default: return;
                }
                e.Handled = true;
            };
            Closed += delegate { End(null); };
        }

        /// 고른 비율(다음에 열 때 쓴다)
        public string Ratio
        {
            get { return select.Ratio; }
        }

        /// 시험용: 보이는 손잡이 수
        internal int GripsShown
        {
            get { return grips.Count(g => g.Visibility == Visibility.Visible); }
        }

        /// 시험용: 막대 자리(화면 점)
        internal Rect BarArea
        {
            get { return new Rect(Canvas.GetLeft(bar), Canvas.GetTop(bar), bar.DesiredSize.Width, bar.DesiredSize.Height); }
        }

        /// 시험용: 고른 곳(화면 점). 아직 없으면 null.
        internal Rect? ChosenDip
        {
            get { return select.Chosen == null ? (Rect?)null : Dip(select.Chosen.Value); }
        }

        static Button BarButton(string text, string style)
        {
            var b = Theme.MakeButton(text, style);
            b.Height = 38;
            b.FontSize = 14;
            b.Padding = new Thickness(16, 0, 16, 0);
            b.Focusable = false;
            return b;
        }

        static Cursor CursorFor(AreaGrip g)
        {
            switch (g)
            {
                case AreaGrip.TopLeft:
                case AreaGrip.BottomRight:
                    return Cursors.SizeNWSE;
                case AreaGrip.TopRight:
                case AreaGrip.BottomLeft:
                    return Cursors.SizeNESW;
                case AreaGrip.Left:
                case AreaGrip.Right:
                    return Cursors.SizeWE;
                case AreaGrip.Top:
                case AreaGrip.Bottom:
                    return Cursors.SizeNS;
                case AreaGrip.Inside:
                    return Cursors.SizeAll;
                default:
                    return Cursors.Cross;
            }
        }

        /// 화면 점 → 실제 픽셀
        Point Real(Point p)
        {
            return new Point(p.X * sx, p.Y * sy);
        }

        /// 실제 픽셀 → 화면 점
        Rect Dip(Rect r)
        {
            return new Rect(r.X / sx, r.Y / sy, r.Width / sx, r.Height / sy);
        }

        void Redraw()
        {
            Rect? shown = select.Shown;
            bool any = shown != null;
            Rect d = any ? Dip(shown.Value) : Rect.Empty;
            hole.Rect = d;
            edge.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            sizeTag.Visibility = edge.Visibility;
            if (any)
            {
                Canvas.SetLeft(edge, d.X - 2);
                Canvas.SetTop(edge, d.Y - 2);
                edge.Width = d.Width + 4;
                edge.Height = d.Height + 4;
                // 크기 글은 손을 떼면 정해질 실제 크기(짝수, 정한 비율이면 딱 그 비율)
                Rect plan = select.Planned.Value;
                sizeText.Text = Theme.Keep((int)plan.Width + " × " + (int)plan.Height + (select.Free ? "" : " · " + select.Ratio));
            }
            // 손잡이: 새로 그리는 동안은 감춘다. 정한 비율이면 꼭짓점만.
            for (int i = 0; i < grips.Length; i++)
            {
                bool show = any && !select.Drawing && (AreaMath.IsCorner(GripOrder[i]) || select.Free);
                grips[i].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show) continue;
                Point c = AreaMath.GripPoint(d, GripOrder[i]);
                Canvas.SetLeft(grips[i], c.X - grips[i].Width / 2);
                Canvas.SetTop(grips[i], c.Y - grips[i].Height / 2);
            }
            bool chosen = select.Chosen != null;
            string title = "녹화할 곳을 끌어서 고르세요";
            if (tooSmall) title = "조금 더 크게 끌어 주세요";
            else if (chosen) title = select.Free ? "꼭짓점이나 변을 끌어 크기를 바꿔요" : "꼭짓점을 끌면 비율 그대로 크기가 바뀌어요";
            barTitle.Text = Theme.Keep(title);
            barHelp.Text = Theme.Keep(chosen ? "안을 끌면 옮겨져요" : "Esc를 누르면 취소돼요");
            barButtons.Visibility = chosen ? Visibility.Visible : Visibility.Collapsed;
            // 끄는 동안은 막대를 감춘다(고르는 곳을 가리지 않게)
            bar.Visibility = select.Drawing || select.Held != AreaGrip.None ? Visibility.Hidden : Visibility.Visible;
            bool barAbove = PlaceBar(chosen ? (Rect?)Dip(select.Chosen.Value) : null);
            if (any)
            {
                Canvas.SetLeft(sizeTag, d.X);
                // 막대가 위에 붙었거나 화면 위 끝이면 영역 안쪽 위에
                Canvas.SetTop(sizeTag, !barAbove && d.Y >= 34 ? d.Y - 32 : d.Y + 8);
            }
        }

        /// 막대를 고른 곳 아래에 둔다. 자리가 없으면 위, 그다음 넓은 옆(칩을 접어서), 그래도 없으면 안쪽 아래.
        /// 고르기 전이면 화면 위 가운데. 위에 두었으면 true(크기 글을 영역 안쪽에 쓰게).
        bool PlaceBar(Rect? area)
        {
            double full = Math.Max(SideMin, viewW - Gap * 2);
            Size bs = MeasureBar(full);
            if (area == null)
            {
                Canvas.SetLeft(bar, (viewW - bs.Width) / 2);
                Canvas.SetTop(bar, Math.Max(28, viewH * 0.08));
                return false;
            }
            Rect r = area.Value;
            double x = Math.Max(Gap, Math.Min(viewW - Gap - bs.Width, r.X + (r.Width - bs.Width) / 2));
            Canvas.SetLeft(bar, x);
            if (r.Bottom + Gap + bs.Height <= viewH - Gap)
            {
                Canvas.SetTop(bar, r.Bottom + Gap);
                return false;
            }
            if (r.Top - Gap - bs.Height >= Gap)
            {
                Canvas.SetTop(bar, r.Top - Gap - bs.Height);
                return true;
            }
            // 세로로 긴 영역: 위아래에 자리가 없으면 넓은 옆에(아래 끝에 맞춤). 한 줄이 안 들어가면 좁게 접는다(칩 4·3).
            double right = viewW - r.Right - Gap * 2, left = r.Left - Gap * 2;
            double room = Math.Max(right, left);
            if (room >= SideMin)
            {
                Size side = room >= bs.Width ? bs : MeasureBar(SideMin);
                if (side.Height <= viewH - Gap * 2)
                {
                    Canvas.SetLeft(bar, right >= left ? r.Right + Gap : r.Left - Gap - side.Width);
                    Canvas.SetTop(bar, Math.Max(Gap, Math.Min(viewH - Gap - side.Height, r.Bottom - side.Height)));
                    return false;
                }
                bs = MeasureBar(full);
            }
            Canvas.SetTop(bar, Math.Max(Gap, r.Bottom - Gap - bs.Height));
            return false;
        }

        /// 막대 너비를 max까지로 두고 잰 크기(칩은 넘치면 다음 줄로)
        Size MeasureBar(double max)
        {
            bar.MaxWidth = max;
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return bar.DesiredSize;
        }

        void Confirm()
        {
            if (select.Pressed || select.Chosen == null) return;
            End(select.Fraction);
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
