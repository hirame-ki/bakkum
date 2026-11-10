// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 화면 녹화 시험. 대부분 가짜 화면·가짜 소리로 확인한다.
// 진짜 화면은 빠르기만 재고 파일로 남기지 않는다. 마이크는 열지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
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

        static void RecordTests()
        {
            Cli.Print("[화면 녹화]");

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
