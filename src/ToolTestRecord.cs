// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 화면 녹화 시험. 대부분 가짜 화면·가짜 소리로 확인한다.
// 진짜 화면은 빠르기만 재고 파일로 남기지 않는다. 마이크는 열지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Box = System.Drawing.Rectangle;

namespace Bakkum
{
    static partial class ToolTest
    {
        static ScreenRecorder Rec(string path, IFrameGrabber grabber, List<ISoundSource> sounds, Action<ScreenRecorder> during)
        {
            if (File.Exists(path)) File.Delete(path);
            var r = new ScreenRecorder(path, grabber, sounds, 24);
            r.Prepare();
            r.Start();
            during(r);
            using (var done = new ManualResetEvent(false))
            {
                r.Finish(delegate { done.Set(); });
                if (!done.WaitOne(40000)) Cli.Print("    마무리가 40초 안에 끝나지 않음");
            }
            return r;
        }

        static List<ISoundSource> NoSound()
        {
            return new List<ISoundSource>();
        }

        static bool Close(byte[] px, int at, int b, int g, int r)
        {
            return Math.Abs(px[at] - b) < 50 && Math.Abs(px[at + 1] - g) < 50 && Math.Abs(px[at + 2] - r) < 50;
        }

        static void PumpUi(int ms)
        {
            var end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new DispatcherOperationCallback(delegate (object f)
                {
                    ((DispatcherFrame)f).Continue = false;
                    return null;
                }), frame);
                Dispatcher.PushFrame(frame);
                Thread.Sleep(15);
            }
        }

        /// 진짜 화면 한 부분에서 자홍색 칸의 비율. 숫자만 쓰고 그림은 어디에도 남기지 않는다.
        static double MagentaShare(Box box, bool layered)
        {
            var g = new GdiGrabber(box, false, layered);
            int bytes = g.Width * g.Height * 4;
            IntPtr mem = Marshal.AllocHGlobal(bytes);
            try
            {
                if (!g.Grab(mem)) return -1;
                var px = new byte[bytes];
                Marshal.Copy(mem, px, 0, bytes);
                int hit = 0;
                for (int i = 0; i < bytes; i += 4)
                {
                    if (px[i] > 200 && px[i + 1] < 60 && px[i + 2] > 200) hit++;
                }
                return hit / (double)(bytes / 4);
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
                g.Dispose();
            }
        }

        static void CaptureWindowChecks()
        {
            var wa = SystemParameters.WorkArea;
            double s = MenuWindow.DpiScale();
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromRgb(255, 0, 255)),
                ResizeMode = ResizeMode.NoResize,
                Width = 80,
                Height = 80,
                Left = wa.Right - 96,
                Top = wa.Bottom - 96,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            w.Show();
            try
            {
                PumpUi(500);
                var box = new Box((int)((w.Left + 24) * s), (int)((w.Top + 24) * s), 32, 32);
                double withLayered = MagentaShare(box, true);
                double without = MagentaShare(box, false);
                Check("투명 창도 찍힌다", withLayered > 0.9, withLayered.ToString("0.00"));
                Cli.Print("    투명 창 찍기 옵션 없이: " + without.ToString("0.00"));
                bool hidden = ScreenArea.HideFromCapture(new System.Windows.Interop.WindowInteropHelper(w).Handle);
                PumpUi(400);
                double after = MagentaShare(box, true), afterPlain = MagentaShare(box, false);
                Check("막대는 녹화본에 안 찍힌다", hidden && after < 0.1 && afterPlain < 0.1, hidden + " · " + after.ToString("0.00") + " · " + afterPlain.ToString("0.00"));
            }
            finally
            {
                w.Close();
            }
        }

        static byte[] CamPattern(int w, int h, byte level)
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    px[i] = level > 0 ? level : (byte)(x & 255);
                    px[i + 1] = level > 0 ? level : (byte)(y & 255);
                    px[i + 2] = level > 0 ? level : (byte)((x * 3 + y) & 255);
                    px[i + 3] = 255;
                }
            }
            return px;
        }

        /// 웹캠에서 받은 장을 옮겨 담는 길과 까만 화면 알림(카메라 없이 윈도우 그림 객체를 넣는다)
        static void CamCopyTests()
        {
            const int W = 320, H = 180;
            var light = CamPattern(W, H, 0);
            var big = CamPattern(W * 2, H * 2, 0);
            var dark = CamPattern(W, H, 3);
            bool sameSize = false, scaled = false, darkSeen = false, darkGone = true, lightQuiet = false;
            double share = -1;
            WinRtCall.Mta<bool>(delegate
            {
                var got = new byte[W * H * 4];
                var g1 = new CamGrabber(W, H);
                using (var sb = CamGrabber.BitmapForTest(light, W, H)) g1.FeedForTest(sb);
                sameSize = g1.CopyLatest(got);
                for (int i = 0; sameSize && i < got.Length; i += 4)
                    if (got[i] != light[i] || got[i + 1] != light[i + 1] || got[i + 2] != light[i + 2]) sameSize = false;

                var g2 = new CamGrabber(W, H);
                using (var sb = CamGrabber.BitmapForTest(big, W * 2, H * 2)) g2.FeedForTest(sb);
                scaled = g2.CopyLatest(got);
                for (int y = 0; scaled && y < H; y++)
                {
                    for (int x = 0; scaled && x < W; x++)
                    {
                        int d = (y * W + x) * 4, s = ((y * 2) * W * 2 + x * 2) * 4;
                        if (got[d] != big[s] || got[d + 1] != big[s + 1] || got[d + 2] != big[s + 2]) scaled = false;
                    }
                }

                var g3 = new CamGrabber(W, H);
                using (var sb = CamGrabber.BitmapForTest(light, W, H)) g3.FeedForTest(sb);
                lightQuiet = !g3.LooksDark;
                for (int k = 0; k < 45; k++)
                {
                    using (var sb = CamGrabber.BitmapForTest(dark, W, H)) g3.FeedForTest(sb);
                }
                darkSeen = g3.LooksDark;
                g3.ResetCounters();
                for (int k = 0; k < 60; k++)
                {
                    using (var sb = CamGrabber.BitmapForTest(dark, W, H)) g3.FeedForTest(sb);
                }
                share = g3.DarkShare;
                for (int k = 0; k < 15; k++)
                {
                    using (var sb = CamGrabber.BitmapForTest(light, W, H)) g3.FeedForTest(sb);
                }
                darkGone = !g3.LooksDark;
                return true;
            });
            Check("웹캠 장 옮기기: 같은 크기는 한 칸도 다르지 않다", sameSize, null);
            Check("웹캠 장 옮기기: 큰 장은 줄여 담는다", scaled, null);
            Check("까만 웹캠 알림: 밝을 땐 조용하다", lightQuiet, null);
            Check("까만 웹캠 알림: 까만 장이 1.5초 이어지면 알린다", darkSeen, null);
            Check("까만 웹캠 알림: 녹화 내내 까맸으면 끝난 뒤에도 알린다", share >= 0.9, share.ToString("0.00"));
            Check("까만 웹캠 알림: 다시 밝아지면 사라진다", darkGone, null);
        }

        /// 웹캠 같이 찍기. 진짜 카메라는 켜지 않고 가짜 화면을 웹캠 자리에 넣는다.
        static void CamTests(string dir, Box corner, Rect place, double s)
        {
            CamCopyTests();

            // 같은 시계: 멈춘 시간이 둘 다에서 빠지고 길이가 같다
            string a = P("cam_screen.mp4"), b = P("cam_face.mp4");
            foreach (var f in new[] { a, b }) if (File.Exists(f)) File.Delete(f);
            var main = new ScreenRecorder(a, new TestFrames(320, 180), NoSound(), 24);
            var cam = new ScreenRecorder(b, new TestFrames(160, 120), NoSound(), 24, main.Clock);
            main.Prepare();
            cam.Prepare();
            main.Start();
            cam.Start();
            Thread.Sleep(1500);
            main.Pause();
            Thread.Sleep(800);
            main.Resume();
            Thread.Sleep(900);
            using (var d1 = new ManualResetEvent(false))
            using (var d2 = new ManualResetEvent(false))
            {
                cam.Finish(delegate { d2.Set(); });
                main.Finish(delegate { d1.Set(); });
                d1.WaitOne(40000);
                d2.WaitOne(40000);
            }
            var pa = Probe(a);
            var pb = Probe(b);
            Check("웹캠: 화면과 같은 시계로 따로 저장(멈춘 시간도 같이 빠짐)", main.Failure == null && cam.Failure == null && pa != null && pb != null && pb.HasVideo && !pb.HasAudio
                && pb.Width == 160 && Near(pa.Seconds, 2.4, 0.5) && Math.Abs(pa.Seconds - pb.Seconds) < 0.15, Say(pa) + " / " + Say(pb));

            // 녹화 파일과 웹캠 파일을 잇는 기록
            if (File.Exists(a) && File.Exists(b))
            {
                CamLog.Save(a, b, 0.08);
                string got;
                double lag;
                bool found = CamLog.Find(a, out got, out lag);
                Check("웹캠 기록: 녹화 파일로 웹캠 파일을 찾는다", found && string.Equals(got, Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase) && Near(lag, 0.08, 0.001), got + " · " + lag);
                string moved = P("cam_screen_다른이름.mp4");
                if (File.Exists(moved)) File.Delete(moved);
                File.Copy(a, moved);
                found = CamLog.Find(moved, out got, out lag);
                Check("웹캠 기록: 녹화 파일 이름을 바꿔도 찾는다", found && got != null && File.Exists(got), got);
                string other = P("rec_fake.mp4");
                Check("웹캠 기록: 다른 영상에는 붙지 않는다", !File.Exists(other) || !CamLog.Find(other, out got, out lag), got);

                int faces;
                var proj = VideoEditor.Build(new List<string> { moved }, out faces);
                var pip = proj.Items.FirstOrDefault(i => i.Kind == "pip");
                Check("영상 편집: 같이 찍은 얼굴이 오른쪽 아래 동그랗게 얹힌다", faces == 1 && pip != null && pip.Circle && pip.X > 0.7 && pip.Y > 0.6 && Near(pip.SrcIn, 0.08, 0.001)
                    && Near(pip.Start, 0, 0.001) && pip.End <= proj.Duration + 0.01 && pip.End > 1.5,
                    pip == null ? "없음" : pip.Start.ToString("0.00") + "~" + pip.End.ToString("0.00") + " · 원본 " + pip.SrcIn.ToString("0.00") + "부터");
                var two = VideoEditor.Build(new List<string> { other, moved }, out faces);
                var pip2 = two.Items.FirstOrDefault(i => i.Kind == "pip");
                double firstLen = two.Clips.Count > 0 ? two.Clips[0].Length : 0;
                Check("영상 편집: 두 번째 영상이면 그 조각이 시작하는 때에", faces == 1 && pip2 != null && Near(pip2.Start, firstLen, 0.05), pip2 == null ? "없음" : pip2.Start.ToString("0.00") + " / " + firstLen.ToString("0.00"));
                File.Delete(moved);
            }

            // 창까지 이어서(가짜 웹캠): 3·2·1 → 녹화 → 끝내기 → 녹화 파일 옆 웹캠 파일과 기록
            RecordUi.TestCam = new TestFrames(160, 120);
            RecordUi.LastFile = null;
            RecordUi.LastCamFile = null;
            try
            {
                RecordUi.ArmForTest(corner, place, new Rect(place.X, place.Y, 64 / s, 64 / s));
                PumpUi(3700);
                Check("웹캠 같이: 3·2·1 뒤 녹화 시작", RecordUi.Recording, null);
                PumpUi(1500);
                RecordUi.Stop();
                for (int i = 0; i < 150 && RecordUi.Active; i++) PumpUi(100);
            }
            finally
            {
                RecordUi.TestCam = null;
            }
            string lf = RecordUi.LastFile, lc = RecordUi.LastCamFile;
            bool sideBySide = lf != null && lc != null && File.Exists(lc) && string.Equals(Path.GetDirectoryName(lf), Path.GetDirectoryName(lc), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(lc).StartsWith(Path.GetFileNameWithoutExtension(lf)) && lc.EndsWith(" 웹캠.mp4");
            Check("웹캠 같이: 녹화 파일 옆에 '… 웹캠.mp4'", sideBySide, lc);
            if (lf != null && lc != null && File.Exists(lf) && File.Exists(lc))
            {
                var pl = Probe(lf);
                var pc = Probe(lc);
                string got;
                double lag;
                Check("웹캠 같이: 길이가 같고 기록으로 이어진다", pl != null && pc != null && Math.Abs(pl.Seconds - pc.Seconds) < 0.2 && CamLog.Find(lf, out got, out lag), Say(pl) + " / " + Say(pc));
                File.Delete(lf);
                File.Delete(lc);
            }
            PumpUi(400);
        }

        /// 진짜 웹캠: 켜서 3초 찍고 장 수·크기·지연만 본다. 찍힌 그림은 저장해 두거나 보여 주지 않고, 파일은 바로 지운다.
        static void CamRealTests()
        {
            if (!CamGrabber.Exists())
            {
                Cli.Print("    이 PC에 웹캠이 없어 건너뜀");
                return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            CamGrabber cam = null;
            string why = null;
            try
            {
                cam = CamGrabber.Open();
            }
            catch (UserFacingException ex)
            {
                why = ex.Message;
            }
            Check("진짜 웹캠: 켜진다", cam != null, cam == null ? why : cam.Width + "×" + cam.Height + " · 켜는 데 " + watch.Elapsed.TotalSeconds.ToString("0.0") + "초");
            if (cam == null) return;
            string f = P("cam_real.mp4");
            // 다른 길(윈도우 버퍼 복사)로 읽은 밝기. 부탁할 때 한 장만 잰다.
            bool wantTap = false;
            double tapMean = -1;
            CamGrabber.TestTap = delegate(Windows.Graphics.Imaging.SoftwareBitmap sb)
            {
                if (!wantTap || tapMean >= 0) return;
                var buf = new Windows.Storage.Streams.Buffer((uint)(sb.PixelWidth * sb.PixelHeight * 4));
                sb.CopyToBuffer(buf);
                var bytes = new byte[buf.Length];
                using (var dr = Windows.Storage.Streams.DataReader.FromBuffer(buf)) dr.ReadBytes(bytes);
                int d;
                tapMean = Brightness(bytes, out d);
            };
            try
            {
                watch.Restart();
                while (cam.Frames == 0 && watch.ElapsedMilliseconds < 4000) Thread.Sleep(50);
                Check("진짜 웹캠: 장이 들어온다", cam.Frames > 0, "첫 장까지 " + watch.Elapsed.TotalSeconds.ToString("0.00") + "초");
                int before = cam.Frames;
                var span = System.Diagnostics.Stopwatch.StartNew();
                cam.ResetCounters();
                double mean = -1, spanSec = 3;
                int differ = 0;
                int frames = 0;
                // 녹화가 끝나면 녹화기가 웹캠도 끄므로, 녹화하는 도중(켠 지 2초 뒤, 밝기 맞추기가 끝난 때)에 잰다.
                // 한 가지 색만이 아닌지, 윈도우 버퍼 복사와 밝기가 같은지. 숫자만 세고 그림은 남기거나 보여 주지 않는다.
                var r = Rec(f, cam, NoSound(), x =>
                {
                    Thread.Sleep(2000);
                    wantTap = true;
                    var w2 = System.Diagnostics.Stopwatch.StartNew();
                    while (tapMean < 0 && w2.ElapsedMilliseconds < 900) Thread.Sleep(20);
                    var px = new byte[cam.Width * cam.Height * 4];
                    cam.CopyLatest(px);
                    mean = Brightness(px, out differ);
                    Thread.Sleep(Math.Max(0, 1000 - (int)w2.ElapsedMilliseconds));
                    frames = cam.Frames;
                    spanSec = span.Elapsed.TotalSeconds;
                });
                // 녹화기를 준비하는 시간도 사이에 들므로 잰 시간으로 나눈다
                double perSec = (frames - before) / Math.Max(0.5, spanSec);
                var p = Probe(f);
                Check("진짜 웹캠: 3초 녹화", r.Failure == null && p != null && p.HasVideo && p.Width == cam.Width && p.Height == cam.Height && Near(p.Seconds, 3, 0.4),
                    Say(p) + " · 카메라에서 1초에 " + perSec.ToString("0.0") + "장(카메라 형식 " + cam.NominalFps.ToString("0.#") + "장)");
                Check("진짜 웹캠: 지연이 짧고 끊기지 않는다", cam.Lag < 0.3 && cam.Stalled == 0 && perSec >= 10, "지연 " + cam.Lag.ToString("0.000") + "초 · 끊긴 시간 " + cam.Stalled.ToString("0.0") + "초");
                Check("진짜 웹캠: 빈 화면이 아니다", differ > 3, "평균 밝기 " + mean.ToString("0") + " · 첫 칸과 다른 색 칸 " + differ + " (렌즈 덮개를 닫았거나 아주 어두우면 0)");
                Check("진짜 웹캠: 우리 복사와 윈도우 복사의 밝기가 같다", tapMean >= 0 && Math.Abs(tapMean - mean) < 12, "우리 " + mean.ToString("0.0") + " / 윈도우 " + tapMean.ToString("0.0"));
                Check("진짜 웹캠: 까만 화면 알림이 밝기와 맞다", (mean < 10) == (cam.DarkShare >= 0.9), "까맣던 몫 " + cam.DarkShare.ToString("0.00"));
            }
            finally
            {
                CamGrabber.TestTap = null;
                cam.Dispose();
                if (File.Exists(f)) File.Delete(f);
            }
            // 끄면 카메라를 놓아 준다(다음 녹화·다른 프로그램이 바로 쓸 수 있다)
            CamGrabber again = null;
            why = null;
            try
            {
                again = CamGrabber.Open();
            }
            catch (UserFacingException ex)
            {
                why = ex.Message;
            }
            if (again != null) again.Dispose();
            Check("진짜 웹캠: 끈 뒤 다시 켤 수 있다", again != null, why);
            Check("시험 파일을 남기지 않았다", !File.Exists(f), null);
        }

        /// 997칸마다 한 칸씩 본 평균 밝기(0~255)와, 첫 칸과 색이 뚜렷이 다른 칸 수
        static double Brightness(byte[] px, out int differ)
        {
            long sum = 0;
            int n = 0;
            differ = 0;
            for (int i = 0; i + 2 < px.Length; i += 4 * 997)
            {
                sum += px[i] + px[i + 1] + px[i + 2];
                n++;
                if (Math.Abs(px[i] - px[0]) + Math.Abs(px[i + 1] - px[1]) + Math.Abs(px[i + 2] - px[2]) > 24) differ++;
            }
            return n == 0 ? 0 : sum / (3.0 * n);
        }

        static bool AreaSame(Rect? r, double x, double y, double w, double h)
        {
            if (r == null) return false;
            Rect v = r.Value;
            return Math.Abs(v.X - x) < 0.01 && Math.Abs(v.Y - y) < 0.01 && Math.Abs(v.Width - w) < 0.01 && Math.Abs(v.Height - h) < 0.01;
        }

        static string AreaSay(Rect? r)
        {
            if (r == null) return "없음";
            Rect v = r.Value;
            return v.X.ToString("0.##") + "," + v.Y.ToString("0.##") + " " + v.Width.ToString("0.##") + "×" + v.Height.ToString("0.##");
        }

        /// 정수 자리, 짝수 크기, 딱 a:b
        static bool AreaExact(Rect r, int a, int b)
        {
            bool whole = r.X == Math.Floor(r.X) && r.Y == Math.Floor(r.Y) && r.Width == Math.Floor(r.Width) && r.Height == Math.Floor(r.Height);
            long w = (long)r.Width, h = (long)r.Height;
            return whole && w % 2 == 0 && h % 2 == 0 && w * b == h * a;
        }

        static bool AreaInside(Rect r, int w, int h)
        {
            return r.X >= 0 && r.Y >= 0 && r.Right <= w && r.Bottom <= h;
        }

        /// 녹화할 곳 고르기(창 없이 계산만): 비율, 꼭짓점·변 손잡이, 옮기기, 화면 안, 짝수 크기, 배율·둘째 모니터
        static void AreaTests()
        {
            double k169 = 16.0 / 9, k916 = 9.0 / 16;
            var r1 = AreaMath.Drag(new Point(100, 100), new Point(-50, 300), 0, 1920, 1080, 0);
            Check("영역: 자유로 끌면 화면 끝에서 멈춤", AreaSame(r1, 0, 100, 100, 200), AreaSay(r1));
            var r2 = AreaMath.Drag(new Point(100, 100), new Point(900, 300), k169, 1920, 1080, 0);
            Check("영역: 16:9로 끌기", AreaSame(r2, 100, 100, 800, 450), AreaSay(r2));
            var r3 = AreaMath.Drag(new Point(1000, 900), new Point(700, 100), k916, 1920, 1080, 0);
            Check("영역: 9:16 왼쪽 위로 끌기", AreaSame(r3, 550, 100, 450, 800), AreaSay(r3));
            var r4 = AreaMath.Drag(new Point(1500, 600), new Point(2500, 700), k169, 1920, 1080, 0);
            Check("영역: 화면 끝에서도 비율 그대로", AreaSame(r4, 1500, 600, 420, 236.25), AreaSay(r4));

            // 정한 비율: 여러 화면 크기·이상한 영역에서도 정수, 짝수, 딱 그 비율, 화면 안, 64 이상
            var bad = new List<string>();
            var screens = new[] { new Size(1920, 1080), new Size(1366, 768), new Size(2880, 1620), new Size(1281, 721) };
            var rects = new[] { new Rect(333.3, 222.7, 517.9, 301.1), new Rect(-10, -10, 5000, 5000), new Rect(10, 10, 5, 5), new Rect(1900, 1000, 300, 300) };
            foreach (string id in AreaRatio.Ids)
            {
                int a, b;
                if (!AreaRatio.Parse(id, out a, out b)) continue;
                foreach (var sc in screens)
                {
                    foreach (var r in rects)
                    {
                        var s = AreaMath.Settle(r, a, b, 0.5, 0.5, (int)sc.Width, (int)sc.Height, AreaSelect.Smallest);
                        if (!AreaExact(s, a, b) || !AreaInside(s, (int)sc.Width, (int)sc.Height) || s.Width < 64 || s.Height < 64)
                            bad.Add(id + " " + sc.Width + "×" + sc.Height + " → " + AreaSay(s));
                    }
                }
            }
            Check("영역: 정한 비율은 딱 그 비율·짝수·화면 안", bad.Count == 0, string.Join(" / ", bad.Take(3)));
            var s6 = AreaMath.Settle(new Rect(100.4, 200.6, 801.3, 449.2), 16, 9, 0, 0, 1920, 1080, 64);
            var s6b = AreaMath.Settle(new Rect(100.4, 200.6, 801.3, 449.2), 16, 9, 1, 1, 1920, 1080, 64);
            Check("영역: 맞출 때 잡은 쪽은 제자리(1픽셀 안)", Math.Abs(s6.X - 100.4) <= 1 && Math.Abs(s6.Y - 200.6) <= 1 && Math.Abs(s6b.Right - 901.7) <= 1 && Math.Abs(s6b.Bottom - 649.8) <= 1, AreaSay(s6) + " / " + AreaSay(s6b));
            var s7 = AreaMath.Settle(new Rect(10.2, 20.7, 301.4, 199.6), 0, 0, 0, 0, 1920, 1080, 64);
            Check("영역: 자유도 짝수 크기", AreaSame(s7, 10, 21, 302, 200), AreaSay(s7));
            var s8 = AreaMath.Settle(new Rect(500, 500, 10, 10), 16, 9, 0.5, 0.5, 1920, 1080, 64);
            Check("영역: 16:9 가장 작은 크기 128×72", s8.Width == 128 && s8.Height == 72, AreaSay(s8));
            var s9 = AreaMath.Settle(new Rect(0, 0, 1366, 768), 9, 16, 0.5, 0.5, 1366, 768, 64);
            Check("영역: 1366×768 화면의 9:16은 432×768", s9.Width == 432 && s9.Height == 768 && AreaInside(s9, 1366, 768), AreaSay(s9));

            // 그리기 → 꼭짓점으로 다듬기(16:9)
            var sel = new AreaSelect(1920, 1080, "16:9");
            sel.Press(new Point(100, 100), 18);
            sel.Move(new Point(900, 300), 6);
            var got = sel.Release(new Point(900, 300), 6);
            Check("영역: 16:9로 그리기", got == AreaRelease.Chosen && AreaSame(sel.Chosen, 100, 100, 800, 450), got + " " + AreaSay(sel.Chosen));
            sel.Press(new Point(900, 550), 18);
            bool corner = sel.Held == AreaGrip.BottomRight;
            sel.Move(new Point(1203.7, 640.2), 6);
            Rect? plan = sel.Planned;
            sel.Release(new Point(1203.7, 640.2), 6);
            var c1 = sel.Chosen.Value;
            Check("영역: 오른쪽 아래 꼭짓점 → 왼쪽 위 그대로, 딱 16:9", corner && c1.X == 100 && c1.Y == 100 && c1.Width > 800 && AreaExact(c1, 16, 9), AreaSay(c1));
            Check("영역: 끄는 동안 보인 크기 = 손 뗀 뒤 크기", AreaSame(plan, c1.X, c1.Y, c1.Width, c1.Height), AreaSay(plan) + " / " + AreaSay(c1));
            sel.Press(new Point(c1.Left, c1.Top), 18);
            bool corner2 = sel.Held == AreaGrip.TopLeft;
            sel.Move(new Point(300.4, 250.9), 6);
            sel.Release(new Point(300.4, 250.9), 6);
            var c2 = sel.Chosen.Value;
            Check("영역: 왼쪽 위 꼭짓점 → 오른쪽 아래 그대로", corner2 && Math.Abs(c2.Right - c1.Right) <= 1 && Math.Abs(c2.Bottom - c1.Bottom) <= 1 && AreaExact(c2, 16, 9), AreaSay(c1) + " → " + AreaSay(c2));
            var mid = new Point(c2.Right, c2.Y + c2.Height / 2);
            var fixedHit = sel.Hit(mid, 18);
            sel.SetRatio("free");
            var freeHit = sel.Hit(mid, 18);
            Check("영역: 변 손잡이는 자유 비율에서만", (fixedHit == AreaGrip.Inside || fixedHit == AreaGrip.None) && freeHit == AreaGrip.Right && AreaSame(sel.Chosen, c2.X, c2.Y, c2.Width, c2.Height), fixedHit + " / " + freeHit);

            // 자유 비율: 변 끌기, 뒤집기, 가장 작은 크기
            var fr = new AreaSelect(1920, 1080, "free");
            fr.Choose(new Rect(100, 100, 600, 400));
            fr.Press(new Point(700, 300), 18);
            bool rightEdge = fr.Held == AreaGrip.Right;
            fr.Move(new Point(1000, 310), 6);
            fr.Release(new Point(1000, 310), 6);
            Check("영역: 자유 비율 오른쪽 변 끌기", rightEdge && AreaSame(fr.Chosen, 100, 100, 900, 400), AreaSay(fr.Chosen));
            fr.Press(new Point(100, 300), 18);
            fr.Move(new Point(1200, 300), 6);
            fr.Release(new Point(1200, 300), 6);
            Check("영역: 왼쪽 변을 오른쪽 너머로 끌면 뒤집힘", AreaSame(fr.Chosen, 1000, 100, 200, 400), AreaSay(fr.Chosen));
            fr.Press(new Point(1200, 300), 18);
            fr.Move(new Point(1010, 300), 6);
            fr.Release(new Point(1010, 300), 6);
            fr.Press(new Point(1032, 500), 18);
            fr.Move(new Point(1032, 105), 6);
            fr.Release(new Point(1032, 105), 6);
            Check("영역: 가장 작은 크기 64×64", AreaSame(fr.Chosen, 1000, 100, 64, 64), AreaSay(fr.Chosen));

            // 안을 끌면 옮기기(화면 밖으로 안 나감). 그냥 누르기·아주 작게 그리기는 고른 곳을 지우지 않는다.
            var mv = new AreaSelect(1920, 1080, "free");
            mv.Choose(new Rect(100, 100, 600, 400));
            mv.Press(new Point(400, 300), 18);
            bool inside = mv.Held == AreaGrip.Inside;
            mv.Move(new Point(5000, 5000), 6);
            mv.Release(new Point(5000, 5000), 6);
            Check("영역: 안을 끌어 옮겨도 화면 안", inside && AreaSame(mv.Chosen, 1320, 680, 600, 400), AreaSay(mv.Chosen));
            mv.Press(new Point(100, 100), 18);
            var click = mv.Release(new Point(101, 101), 6);
            Check("영역: 그냥 누르면 고른 곳 그대로", click == AreaRelease.Nothing && AreaSame(mv.Chosen, 1320, 680, 600, 400), click + " " + AreaSay(mv.Chosen));
            mv.Press(new Point(100, 100), 18);
            mv.Move(new Point(140, 130), 6);
            var tiny = mv.Release(new Point(140, 130), 6);
            Check("영역: 너무 작게 그리면 알리고 고른 곳 그대로", tiny == AreaRelease.TooSmall && AreaSame(mv.Chosen, 1320, 680, 600, 400), tiny + " " + AreaSay(mv.Chosen));

            // 비율 바꾸기: 가운데·넓이 지키기, 화면 끝이면 안으로. 화살표로 옮기기.
            var rt = new AreaSelect(1920, 1080, "free");
            rt.Choose(new Rect(400, 300, 800, 450));
            rt.SetRatio("1:1");
            var q1 = rt.Chosen.Value;
            double area0 = 800 * 450, area1 = q1.Width * q1.Height;
            Check("영역: 비율을 바꾸면 가운데·넓이 그대로", AreaExact(q1, 1, 1) && Math.Abs(q1.X + q1.Width / 2 - 800) <= 2 && Math.Abs(q1.Y + q1.Height / 2 - 525) <= 2 && Math.Abs(area1 - area0) <= area0 * 0.1, AreaSay(q1));
            var rt2 = new AreaSelect(1920, 1080, "free");
            rt2.Choose(new Rect(1500, 100, 400, 900));
            rt2.SetRatio("16:9");
            var q2 = rt2.Chosen.Value;
            Check("영역: 화면 끝에서 비율을 바꿔도 화면 안", AreaExact(q2, 16, 9) && AreaInside(q2, 1920, 1080), AreaSay(q2));
            rt2.Nudge(1000, 0);
            bool rightEnd = rt2.Chosen.Value.Right == 1920;
            rt2.Nudge(-5000, -5000);
            Check("영역: 화살표로 옮겨도 화면 안", rightEnd && AreaSame(rt2.Chosen, 0, 0, q2.Width, q2.Height), AreaSay(rt2.Chosen));

            // 결과 비율 → 실제 픽셀: 150% 둘째 모니터, 125% 화면
            var big = new Box(1920, 0, 2880, 1620);
            var bsel = new AreaSelect(big.Width, big.Height, "16:9");
            bsel.Choose(new Rect(700.3, 400.6, 1000, 560));
            var bc = bsel.Chosen.Value;
            var bb = RecordUi.BoxFor(big, bsel.Fraction.Value);
            var small = new Box(0, 0, 2400, 1350);
            var ssel = new AreaSelect(small.Width, small.Height, "9:16");
            ssel.Choose(new Rect(900, 200, 500, 900));
            var sc2 = ssel.Chosen.Value;
            var sb = RecordUi.BoxFor(small, ssel.Fraction.Value);
            Check("영역: 배율·둘째 모니터에서도 고른 픽셀 그대로", bb.X == 1920 + (int)bc.X && bb.Y == (int)bc.Y && bb.Width == (int)bc.Width && bb.Height == (int)bc.Height && (long)bb.Width * 9 == (long)bb.Height * 16 && sb.X == (int)sc2.X && sb.Y == (int)sc2.Y && sb.Width == (int)sc2.Width && sb.Height == (int)sc2.Height && (long)sb.Width * 16 == (long)sb.Height * 9, bb + " / " + sb);

            int pa, pb;
            bool parsed = AreaRatio.Parse("16:9", out pa, out pb) && pa == 16 && pb == 9;
            Check("영역: 비율 이름", AreaRatio.Known("2:1") == "free" && AreaRatio.Known(null) == "free" && AreaRatio.Known("4:5") == "4:5" && parsed && !AreaRatio.Parse("free", out pa, out pb) && Math.Abs(AreaRatio.Of("9:16") - 0.5625) < 1e-9, null);

            // 고르기 창(띄우지 않고 150% 화면으로): 정한 비율은 꼭짓점 손잡이 4개, 자유는 8개, 막대는 고른 곳을 가리지 않고 화면 안
            var report = new List<string>();
            bool winOk = true;
            foreach (string st in new[] { "start", "free", "16:9", "9:16", "1:1", "3:4" })
            {
                var pw = (AreaPicker)RecordUi.PreviewPicker(st);
                int want = st == "start" ? 0 : (st == "free" ? 8 : 4);
                Rect bar = pw.BarArea;
                Rect? chosen = pw.ChosenDip;
                bool clear = chosen == null || !bar.IntersectsWith(chosen.Value);
                bool onScreen = bar.X >= 0 && bar.Y >= 0 && bar.Right <= 1280 && bar.Bottom <= 720;
                if (pw.GripsShown != want || !clear || !onScreen) winOk = false;
                report.Add(st + " 손잡이 " + pw.GripsShown + " 막대 " + AreaSay(bar));
                pw.Close();
            }
            Check("영역 창: 손잡이 수·막대 자리", winOk, string.Join(" / ", report));
        }

        static void RecordTests()
        {
            Cli.Print("[화면 녹화]");
            AreaTests();

            // 길이·크기·방향·색
            string a = P("rec_fake.mp4");
            var one = Rec(a, new TestFrames(640, 360), NoSound(), r => Thread.Sleep(3000));
            Check("가짜 화면 녹화", one.Failure == null && File.Exists(a), one.Failure);
            var pa = Probe(a);
            Check("길이·크기", pa != null && pa.HasVideo && !pa.HasAudio && pa.Width == 640 && pa.Height == 360 && Near(pa.Seconds, 3, 0.4), Say(pa));
            Check("초당 장수", pa != null && Near(pa.Fps, 24, 3), pa == null ? "" : pa.Fps.ToString("0.0"));
            Cli.Print("    프레임 " + one.FramesWritten + " · 건너뜀 " + one.FramesDropped);
            WinRtCall.Mta<bool>(delegate
            {
                using (var v = new VideoFrames(a))
                {
                    byte[] px = null;
                    double sec;
                    bool got = v.Next(out sec, ref px);
                    Check("첫 프레임을 읽는다", got, null);
                    if (got)
                    {
                        Func<int, int, int> at = (x, y) => (y * v.Height / 8 * v.Width + x * v.Width / 8) * 4;
                        Check("왼쪽 위 빨강(뒤집히지 않음)", Close(px, at(1, 1), 0, 0, 255), null);
                        Check("오른쪽 위 초록", Close(px, at(6, 1), 0, 255, 0), null);
                        Check("왼쪽 아래 파랑", Close(px, at(1, 6), 255, 0, 0), null);
                        Check("오른쪽 아래 흰색", Close(px, at(6, 6), 255, 255, 255), null);
                    }
                }
                return true;
            });

            // 홀수 크기는 짝수로
            string b = P("rec_odd.mp4");
            var odd = Rec(b, new TestFrames(641, 361), NoSound(), r => Thread.Sleep(600));
            var pb = Probe(b);
            Check("홀수 크기", odd.Failure == null && pb != null && pb.Width == 640 && pb.Height == 360, Say(pb));

            // 잠시 멈춤: 멈춘 시간은 영상에 들어가지 않는다
            string c = P("rec_pause.mp4");
            var paused = Rec(c, new TestFrames(320, 240), NoSound(), r =>
            {
                Thread.Sleep(1000);
                r.Pause();
                Thread.Sleep(1200);
                r.Resume();
                Thread.Sleep(1000);
            });
            var pc = Probe(c);
            Check("잠시 멈춘 시간은 빠진다", paused.Failure == null && pc != null && Near(pc.Seconds, 2, 0.5), Say(pc));

            // 가짜 소리(440Hz). 소리 내용은 파이썬에서 확인한다.
            string d = P("rec_tone.mp4");
            var tone = Rec(d, new TestFrames(320, 240), new List<ISoundSource> { new ToneSource() }, r => Thread.Sleep(3000));
            var pd = Probe(d);
            Check("영상과 소리", tone.Failure == null && pd != null && pd.HasVideo && pd.HasAudio && pd.Channels == 2 && pd.AudioRate == 48000 && Near(pd.Seconds, 3, 0.4), Say(pd));

            // 소리도 멈춘 시간이 빠진다
            string e = P("rec_tonepause.mp4");
            var tonep = Rec(e, new TestFrames(320, 240), new List<ISoundSource> { new ToneSource() }, r =>
            {
                Thread.Sleep(1000);
                r.Pause();
                Thread.Sleep(1000);
                r.Resume();
                Thread.Sleep(1000);
            });
            var pe = Probe(e);
            Check("소리도 멈춘 시간이 빠진다", tonep.Failure == null && pe != null && pe.HasAudio && Near(pe.Seconds, 2, 0.5), Say(pe));

            // 1080p가 이 PC에서 얼마나 따라오는지(가짜 화면이라 찍는 시간은 빠져 있다)
            string f = P("rec_1080.mp4");
            var big = Rec(f, new TestFrames(1920, 1080), NoSound(), r => Thread.Sleep(3000));
            var pf = Probe(f);
            Check("1080p 저장", big.Failure == null && pf != null && pf.Width == 1920 && pf.Height == 1080 && big.FramesWritten >= 24, Say(pf) + " · 프레임 " + big.FramesWritten + " · 건너뜀 " + big.FramesDropped);
            if (File.Exists(f)) File.Delete(f);

            // 저장할 수 없는 곳
            string bad = P("없는폴더\\rec.mp4");
            bool refused = false;
            try
            {
                var r = new ScreenRecorder(bad, new TestFrames(320, 240), NoSound(), 24);
                r.Prepare();
                r.Discard();
            }
            catch (UserFacingException ex)
            {
                refused = ex.Message.Contains("저장할 폴더");
            }
            Check("저장할 수 없는 곳이면 시작하지 않는다", refused, null);

            // 녹화 도중 그래픽 인코더가 멈추면: 찍은 화면은 다시 만들 수 없어 알리고, 다음 녹화부터 CPU 인코더로
            string gf = P("rec_gpu_fail.mp4"), gn = P("rec_gpu_next.mp4");
            ScreenRecorder broke, next;
            try
            {
                ScreenRecorder.FailHardwareAt = 5;
                broke = Rec(gf, new TestFrames(320, 240), NoSound(), r => Thread.Sleep(1500));
                ScreenRecorder.FailHardwareAt = -1;
                next = Rec(gn, new TestFrames(320, 240), NoSound(), r => Thread.Sleep(1500));
            }
            finally
            {
                ScreenRecorder.FailHardwareAt = -1;
                VEncoder.CpuOnly = false;
            }
            var pn = Probe(gn);
            Check("녹화 도중 그래픽 인코더가 멈추면 알리고, 다음 녹화는 CPU 인코더로 하고 끝에 알린다", broke.Hardware && broke.Failure != null && broke.Failure.Contains("다시 녹화하면") && !File.Exists(gf)
                && next.Failure == null && !next.Hardware && next.Note != null && next.Note.Contains("그래픽 인코더 없이") && pn != null && pn.HasVideo,
                (broke.Failure ?? "실패 없음") + " · 다음 녹화 " + (next.Hardware ? "그래픽 인코더" : "CPU") + " · " + next.Note + " " + Say(pn));
            if (File.Exists(gn)) File.Delete(gn);

            // 시작 전 취소하면 파일이 남지 않는다
            string g = P("rec_discard.mp4");
            var cancel = new ScreenRecorder(g, new TestFrames(320, 240), NoSound(), 24);
            cancel.Prepare();
            cancel.Discard();
            Check("취소하면 파일이 남지 않는다", !File.Exists(g), null);

            // 진짜 화면은 찍는 빠르기만 잰다. 파일로 남기지 않는다.
            Box mon = ScreenArea.Even(ScreenArea.MonitorUnderCursor());
            foreach (bool layered in new[] { true, false, true, false })
            {
                bool mode = layered;
                var speed = new Thread(delegate ()
                {
                    ScreenArea.UseRealPixels();
                    using (var gdi = new GdiGrabber(mon, true, mode))
                    {
                        IntPtr mem = Marshal.AllocHGlobal(gdi.Width * gdi.Height * 4);
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            int ok = 0;
                            for (int i = 0; i < 30; i++)
                            {
                                if (gdi.Grab(mem)) ok++;
                            }
                            sw.Stop();
                            Check("진짜 화면 찍기 " + (mode ? "(CAPTUREBLT)" : "(기본)"), ok == 30, ok + "장 · 한 장 " + (sw.ElapsedMilliseconds / 30.0).ToString("0.0") + "ms · " + gdi.Width + "×" + gdi.Height);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(mem);
                        }
                    }
                });
                speed.Start();
                speed.Join();
            }

            // 떠 있는 창이 찍히는지(화면 구석에 작은 자홍색 네모가 1초쯤 떴다 사라진다)
            CaptureWindowChecks();

            // 오래 찍어도 메모리가 늘지 않는지(1080p 가짜 화면 + 가짜 소리 30초)
            string soak = P("rec_soak.mp4");
            long at5 = 0, at30 = 0;
            var longRun = Rec(soak, new TestFrames(1920, 1080), new List<ISoundSource> { new ToneSource() }, r =>
            {
                Thread.Sleep(5000);
                at5 = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
                Thread.Sleep(25000);
                at30 = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
            });
            var ps = Probe(soak);
            Check("30초 녹화", longRun.Failure == null && ps != null && Near(ps.Seconds, 30, 1.5) && ps.HasAudio, Say(ps) + " · 프레임 " + longRun.FramesWritten + " · 건너뜀 " + longRun.FramesDropped + " · " + Conversions.Size(new FileInfo(soak).Length));
            Check("메모리가 늘지 않는다", at30 - at5 < 80L * 1024 * 1024, "5초 " + (at5 >> 20) + "MB → 30초 " + (at30 >> 20) + "MB");
            if (File.Exists(soak)) File.Delete(soak);

            // 창까지 이어서: 3·2·1 → 막대 → 끝내기 → 저장 알림(화면 왼쪽 위 작은 영역, 눌러 주는 입력 없음)
            RecordUi.TestFolder = dir;
            Box corner = new Box(mon.X, mon.Y, 64, 64);
            Rect place = ScreenArea.MonitorDip();
            double s = MenuWindow.DpiScale();
            RecordUi.ArmForTest(corner, place, new Rect(place.X, place.Y, 64 / s, 64 / s));
            Check("준비 뒤 3·2·1", RecordUi.Active && !RecordUi.Recording, null);
            PumpUi(3700);
            Check("3·2·1 뒤 녹화 시작", RecordUi.Recording, null);
            PumpUi(1500);
            RecordUi.Stop();
            for (int i = 0; i < 150 && RecordUi.Active; i++) PumpUi(100);
            Check("끝내면 저장까지 끝난다", !RecordUi.Active && RecordUi.LastFile != null && File.Exists(RecordUi.LastFile), null);
            if (RecordUi.LastFile != null)
            {
                var pz = Probe(RecordUi.LastFile);
                Check("창에서 찍은 영상", pz != null && pz.Width == 64 && pz.Height == 64 && pz.Seconds > 0.8, Say(pz));
                Check("최근 결과에 든다", Jobs.RecentOutputs().Contains(RecordUi.LastFile), null);
                File.Delete(RecordUi.LastFile);
            }
            PumpUi(400);

            CamTests(dir, corner, place, s);

            // 컴퓨터 소리: 파일 형식만 보고 바로 지운다. 마이크는 열지 않는다.
            string h = P("rec_loop.mp4");
            ScreenRecorder loop = null;
            string why = null;
            try
            {
                var src = new List<ISoundSource>();
                WinRtCall.Mta<bool>(delegate
                {
                    src.Add(new WasapiSource(true));
                    return true;
                });
                loop = Rec(h, new TestFrames(320, 240), src, r => Thread.Sleep(2500));
            }
            catch (UserFacingException ex)
            {
                why = ex.Message;
            }
            if (why != null)
            {
                Cli.Print("    컴퓨터 소리 장치 없음: " + why);
            }
            else
            {
                var ph = Probe(h);
                Check("컴퓨터 소리(조용해도 길이가 맞다)", loop.Failure == null && ph != null && ph.HasAudio && Near(ph.Seconds, 2.5, 0.5), Say(ph));
                if (File.Exists(h)) File.Delete(h);
            }
        }
    }
}
