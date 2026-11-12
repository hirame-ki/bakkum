// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 화면 녹화와 함께 웹캠 찍기. 윈도우 카메라 기능(MediaCapture + MediaFrameReader)으로 가장 최근 장을 받아
// 화면 녹화와 같은 시계로 따로 MP4에 담는다(ScreenRecorder를 하나 더 쓴다). 녹화하는 동안 화면 구석에 내 얼굴이
// 작게 보이지만 녹화본에는 찍히지 않는다. 영상 편집에 녹화 파일을 놓으면 얼굴 영상이 오른쪽 아래에 동그랗게 얹힌다.
// 얼굴 영상은 이 PC 안(녹화 파일 옆)에만 저장하고 어디로도 보내지 않는다.
// 근거: Microsoft Learn의 'Process media frames with MediaFrameReader', MediaFrameReference.SystemRelativeTime,
// IMemoryBufferByteAccess 문서.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinCapture = Windows.Media.Capture;
using WinFrames = Windows.Media.Capture.Frames;
using WinImaging = Windows.Graphics.Imaging;
using WinProps = Windows.Media.MediaProperties;

namespace Bakkum
{
    [ComImport, Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    /// 웹캠의 가장 최근 장을 녹화 프레임으로 내준다. 따로 도는 스레드가 카메라에서 새 장을 받아 둔다.
    sealed class CamGrabber : IFrameGrabber
    {
        const int MaxW = 1280, MaxH = 720;

        WinCapture.MediaCapture capture;
        WinFrames.MediaFrameReader reader;
        readonly object gate = new object();
        readonly int w, h;
        readonly byte[] latest;
        byte[] row;
        Thread poller;
        volatile bool closed;
        bool seen;
        long lagSum;
        int lagCount, frames, pullErrors;
        long lastNewTicks, lastFrameTicks = -1;
        double stalled;

        /// 카메라 이름(윈도우가 알려 주는 대로)
        public readonly string Name;

        public int Width { get { return w; } }
        public int Height { get { return h; } }

        readonly double nominalFps;
        /// 고른 카메라 형식의 1초 장 수(카메라가 알려 준 값)
        public double NominalFps { get { return nominalFps; } }

        /// 받은 장 수
        public int Frames { get { lock (gate) return frames; } }

        /// 카메라가 장을 찍은 때와 우리가 받은 때의 평균 차이(초). 영상 편집에서 그만큼 앞당겨 입 모양과 소리를 맞춘다.
        public double Lag
        {
            get
            {
                lock (gate) return lagCount == 0 ? 0 : Math.Max(0, Math.Min(0.5, lagSum / (double)lagCount / 1e7));
            }
        }

        /// 녹화 중 새 장이 2초 넘게 오지 않은 시간(초)
        public double Stalled { get { lock (gate) return stalled; } }

        /// 평균 밝기(0~255)가 이보다 낮으면 까만 화면으로 본다. 렌즈 덮개를 닫으면 3쯤, 불 끈 방에서 모니터 빛만 받아도 20은 넘는다.
        const double DarkLevel = 10;
        int checks, darkChecks, darkRun;

        /// 지금 화면이 계속 까만지(렌즈 덮개가 닫혔거나 아주 어두움). 15장마다 재고 세 번 잇달아 까말 때만(켠 직후 밝기를 맞추는 1초 남짓은 넘긴다).
        public bool LooksDark { get { lock (gate) return darkRun >= 3; } }

        /// 녹화하는 동안 까맣던 몫(0~1)
        public double DarkShare { get { lock (gate) return checks < 4 ? 0 : darkChecks / (double)checks; } }

        /// 녹화를 시작할 때 부른다(켜지는 동안 기다린 시간과 까맣던 때는 세지 않는다)
        public void ResetCounters()
        {
            lock (gate)
            {
                stalled = 0;
                checks = 0;
                darkChecks = 0;
            }
            Interlocked.Exchange(ref lastNewTicks, Stopwatch.GetTimestamp());
        }

        /// 997칸마다 한 칸씩 본 평균 밝기
        static double SampleBright(byte[] b)
        {
            long sum = 0;
            int n = 0;
            for (int i = 0; i + 2 < b.Length; i += 4 * 997)
            {
                sum += b[i] + b[i + 1] + b[i + 2];
                n++;
            }
            return n == 0 ? 0 : sum / (3.0 * n);
        }

        /// 이 PC에 웹캠이 있는지(카메라를 켜지는 않는다)
        public static bool Exists()
        {
            try
            {
                return WinRtCall.Mta(() => Pick(WinRtCall.Await(WinFrames.MediaFrameSourceGroup.FindAllAsync())) != null);
            }
            catch (Exception ex)
            {
                Log.Write("웹캠 찾기 실패: " + ex.GetType().Name);
                return false;
            }
        }

        /// 웹캠을 켠다(불이 들어온다). 실패하면 사용자에게 보일 문장으로 UserFacingException.
        public static CamGrabber Open()
        {
            return WinRtCall.Mta(() => new CamGrabber());
        }

        /// 시험·그림용: 카메라 없이 크기만 정한다. FeedForTest로 장을 넣는다.
        internal CamGrabber(int width, int height)
        {
            w = width;
            h = height;
            latest = new byte[w * h * 4];
            Name = "시험";
            closed = true;
        }

        /// 시험·그림용: 카메라에서 받은 것처럼 장을 넣는다(같은 복사·밝기 재기 길을 탄다)
        internal void FeedForTest(WinImaging.SoftwareBitmap bmp)
        {
            Take(bmp);
        }

        /// 시험·그림용: BGRA 바이트로 윈도우 그림(SoftwareBitmap)을 만든다
        internal static WinImaging.SoftwareBitmap BitmapForTest(byte[] bgra, int width, int height)
        {
            var dw = new Windows.Storage.Streams.DataWriter();
            dw.WriteBytes(bgra);
            return WinImaging.SoftwareBitmap.CreateCopyFromBuffer(dw.DetachBuffer(), WinImaging.BitmapPixelFormat.Bgra8, width, height);
        }

        static WinFrames.MediaFrameSourceGroup Pick(IReadOnlyList<WinFrames.MediaFrameSourceGroup> groups)
        {
            // 색 영상만 있는 묶음(보통 웹캠)을 먼저, 없으면 색 영상이 있는 아무 묶음(윈도우 헬로 적외선 카메라는 뒤로)
            WinFrames.MediaFrameSourceGroup any = null;
            foreach (var g in groups)
            {
                bool color = false, other = false;
                foreach (var i in g.SourceInfos)
                {
                    if (i.SourceKind == WinFrames.MediaFrameSourceKind.Color) color = true;
                    else other = true;
                }
                if (!color) continue;
                if (!other) return g;
                if (any == null) any = g;
            }
            return any;
        }

        CamGrabber()
        {
            var group = Pick(WinRtCall.Await(WinFrames.MediaFrameSourceGroup.FindAllAsync()));
            if (group == null) throw new UserFacingException("이 PC에서 웹캠을 찾지 못했어요. 웹캠을 꽂았는지 확인해 주세요.");
            Name = group.DisplayName ?? "웹캠";
            capture = new WinCapture.MediaCapture();
            try
            {
                var settings = new WinCapture.MediaCaptureInitializationSettings
                {
                    SourceGroup = group,
                    SharingMode = WinCapture.MediaCaptureSharingMode.ExclusiveControl,
                    StreamingCaptureMode = WinCapture.StreamingCaptureMode.Video,
                    MemoryPreference = WinCapture.MediaCaptureMemoryPreference.Cpu
                };
                WinRtCall.Await(capture.InitializeAsync(settings));
                WinFrames.MediaFrameSource source = null;
                foreach (var s in capture.FrameSources.Values)
                {
                    if (s.Info.SourceKind != WinFrames.MediaFrameSourceKind.Color) continue;
                    if (source == null || (s.Info.MediaStreamType == WinCapture.MediaStreamType.VideoRecord && source.Info.MediaStreamType != WinCapture.MediaStreamType.VideoRecord)) source = s;
                }
                if (source == null) throw new UserFacingException("웹캠에서 색 영상을 찾지 못했어요.");
                var format = Choose(source.SupportedFormats);
                if (format != null)
                {
                    try
                    {
                        WinRtCall.Await(source.SetFormatAsync(format));
                    }
                    catch (Exception ex)
                    {
                        Log.Write("웹캠 형식 바꾸기 실패(그대로 씀): " + ex.GetType().Name);
                    }
                }
                var cur = source.CurrentFormat;
                if (cur != null && cur.FrameRate != null && cur.FrameRate.Denominator > 0) nominalFps = cur.FrameRate.Numerator / (double)cur.FrameRate.Denominator;
                int fw = cur != null && cur.VideoFormat != null ? (int)cur.VideoFormat.Width : 640;
                int fh = cur != null && cur.VideoFormat != null ? (int)cur.VideoFormat.Height : 480;
                double k = Math.Min(1, Math.Min(MaxW / (double)Math.Max(1, fw), MaxH / (double)Math.Max(1, fh)));
                w = Math.Max(16, (int)Math.Round(fw * k) & ~1);
                h = Math.Max(16, (int)Math.Round(fh * k) & ~1);
                latest = new byte[w * h * 4];
                for (int i = 0; i < latest.Length; i += 4)
                {
                    latest[i] = 0x2A;
                    latest[i + 1] = 0x26;
                    latest[i + 2] = 0x22;
                    latest[i + 3] = 255;
                }
                reader = WinRtCall.Await(capture.CreateFrameReaderAsync(source, WinProps.MediaEncodingSubtypes.Bgra8, new WinImaging.BitmapSize { Width = (uint)w, Height = (uint)h }));
                reader.AcquisitionMode = WinFrames.MediaFrameReaderAcquisitionMode.Realtime;
                var status = WinRtCall.Await(reader.StartAsync());
                if (status != WinFrames.MediaFrameReaderStartStatus.Success)
                {
                    Log.Write("웹캠 시작 실패: " + status);
                    if (status == WinFrames.MediaFrameReaderStartStatus.ExclusiveControlNotAvailable || status == WinFrames.MediaFrameReaderStartStatus.DeviceNotAvailable)
                        throw new UserFacingException(InUse);
                    throw new UserFacingException("웹캠을 켜지 못했어요.");
                }
            }
            catch (Exception ex)
            {
                CloseDevice();
                if (ex is UserFacingException) throw;
                Log.Write("웹캠 열기 실패: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                throw new UserFacingException(Explain(ex));
            }
            lastNewTicks = Stopwatch.GetTimestamp();
            poller = new Thread(PollLoop) { IsBackground = true, Name = "웹캠" };
            poller.SetApartmentState(ApartmentState.MTA);
            poller.Start();
        }

        const string InUse = "다른 프로그램(줌·팀즈·카메라 앱 등)이 웹캠을 쓰고 있어요. 그 프로그램을 끄고 다시 해 주세요.";

        internal static string Explain(Exception ex)
        {
            uint hr = (uint)ex.HResult;
            if (ex is UnauthorizedAccessException || hr == 0x80070005)
                return "윈도우가 카메라 사용을 막고 있어요. 설정 → 개인 정보 및 보안 → 카메라에서 '카메라 액세스'와 '데스크톱 앱이 카메라에 액세스하도록 허용'을 켜 주세요.";
            // 0xC00D3704: 장치를 다른 곳에서 쓰는 중(스트리밍 시작 실패), 0xC00DABE4·0xC00DABE5: 장치를 빼앗김·끊김
            if (hr == 0xC00D3704 || hr == 0xC00DABE4 || hr == 0xC00DABE5 || hr == 0x800700AA) return InUse;
            if (hr == 0xC00D36D5 || hr == 0x80070490) return "웹캠을 찾지 못했어요. 웹캠을 꽂았는지 확인해 주세요.";
            return "웹캠을 켜지 못했어요. (" + ex.GetType().Name + ")";
        }

        /// 1280×720 안에서 가장 큰 것, 같으면 30장에 가까운 것
        static WinFrames.MediaFrameFormat Choose(IReadOnlyList<WinFrames.MediaFrameFormat> formats)
        {
            WinFrames.MediaFrameFormat best = null;
            double bestScore = double.MinValue;
            foreach (var f in formats)
            {
                var v = f.VideoFormat;
                if (v == null || v.Width == 0 || v.Height == 0) continue;
                double fps = f.FrameRate != null && f.FrameRate.Denominator > 0 ? f.FrameRate.Numerator / (double)f.FrameRate.Denominator : 30;
                bool fits = v.Width <= MaxW && v.Height <= MaxH;
                double score = (fits ? 1e7 : -1e7) + (fits ? v.Width * (double)v.Height : -(v.Width * (double)v.Height));
                if (fps < 14) score -= 2e7;
                score -= Math.Abs(Math.Min(fps, 60) - 30) * 1000;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = f;
                }
            }
            return best;
        }

        void PollLoop()
        {
            while (!closed)
            {
                try
                {
                    Pull();
                }
                catch (Exception ex)
                {
                    if (pullErrors++ < 3) Log.Write("웹캠 장 받기 실패: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                }
                Thread.Sleep(8);
            }
        }

        static long QpcTicks()
        {
            return (long)(Stopwatch.GetTimestamp() * (1e7 / Stopwatch.Frequency));
        }

        void Pull()
        {
            var r = reader;
            if (r == null) return;
            using (var fr = r.TryAcquireLatestFrame())
            {
                long now = Stopwatch.GetTimestamp();
                var rel = fr == null ? null : fr.SystemRelativeTime;
                // 지난번과 같은 장이면 새 장이 아니다(멈춘 카메라가 마지막 장을 되풀이해 주는 때도 끊긴 것으로 센다)
                bool fresh = fr != null && (!rel.HasValue || rel.Value.Ticks != lastFrameTicks);
                if (!fresh)
                {
                    double quiet = (now - Interlocked.Read(ref lastNewTicks)) / (double)Stopwatch.Frequency;
                    if (quiet > 2)
                    {
                        lock (gate) stalled += quiet;
                        Interlocked.Exchange(ref lastNewTicks, now);
                    }
                    return;
                }
                if (rel.HasValue) lastFrameTicks = rel.Value.Ticks;
                Interlocked.Exchange(ref lastNewTicks, now);
                var vf = fr.VideoMediaFrame;
                var sb = vf == null ? null : vf.SoftwareBitmap;
                if (sb == null) return;
                if (sb.BitmapPixelFormat == WinImaging.BitmapPixelFormat.Bgra8) Take(sb);
                else
                {
                    using (var conv = WinImaging.SoftwareBitmap.Convert(sb, WinImaging.BitmapPixelFormat.Bgra8)) Take(conv);
                }
                if (rel.HasValue)
                {
                    long lag = QpcTicks() - rel.Value.Ticks;
                    if (lag >= 0 && lag < 5000000)
                    {
                        lock (gate)
                        {
                            lagSum += lag;
                            lagCount++;
                        }
                    }
                }
            }
        }

        /// 시험용: 받은 장을 다른 길(윈도우 버퍼 복사)로도 읽어 우리 복사와 견주게 한다
        internal static Action<WinImaging.SoftwareBitmap> TestTap;

        void Take(WinImaging.SoftwareBitmap bmp)
        {
            CopyFrom(bmp);
            var tap = TestTap;
            if (tap != null) tap(bmp);
        }

        void CopyFrom(WinImaging.SoftwareBitmap bmp)
        {
            using (var buf = bmp.LockBuffer(WinImaging.BitmapBufferAccessMode.Read))
            {
                var desc = buf.GetPlaneDescription(0);
                using (var mref = buf.CreateReference())
                {
                    IntPtr p;
                    uint cap;
                    ((IMemoryBufferByteAccess)(object)mref).GetBuffer(out p, out cap);
                    if (p == IntPtr.Zero) return;
                    int sw = desc.Width, sh = desc.Height, stride = desc.Stride;
                    long start = p.ToInt64() + desc.StartIndex;
                    if (sw <= 0 || sh <= 0 || stride < sw * 4 || (long)desc.StartIndex + (long)stride * (sh - 1) + sw * 4 > cap) return;
                    lock (gate)
                    {
                        if (sw == w && sh == h)
                        {
                            for (int y = 0; y < h; y++) Marshal.Copy(new IntPtr(start + (long)y * stride), latest, y * w * 4, w * 4);
                        }
                        else
                        {
                            // 크기가 다르면 가까운 칸으로 맞춘다(보통은 같은 크기로 받는다)
                            if (row == null || row.Length < sw * 4) row = new byte[sw * 4];
                            for (int y = 0; y < h; y++)
                            {
                                int sy = Math.Min(sh - 1, y * sh / h);
                                Marshal.Copy(new IntPtr(start + (long)sy * stride), row, 0, sw * 4);
                                int d = y * w * 4;
                                for (int x = 0; x < w; x++)
                                {
                                    int s = Math.Min(sw - 1, x * sw / w) * 4;
                                    latest[d] = row[s];
                                    latest[d + 1] = row[s + 1];
                                    latest[d + 2] = row[s + 2];
                                    latest[d + 3] = 255;
                                    d += 4;
                                }
                            }
                        }
                        frames++;
                        seen = true;
                        if (frames % 15 == 1)
                        {
                            bool dark = SampleBright(latest) < DarkLevel;
                            checks++;
                            if (dark) darkChecks++;
                            darkRun = dark ? darkRun + 1 : 0;
                        }
                    }
                }
            }
        }

        public bool Grab(IntPtr target)
        {
            lock (gate) Marshal.Copy(latest, 0, target, latest.Length);
            return true;
        }

        /// 미리 보기 창용: 가장 최근 장을 dst(Width×Height×4)에. 아직 한 장도 없으면 false.
        public bool CopyLatest(byte[] dst)
        {
            lock (gate)
            {
                if (!seen) return false;
                Buffer.BlockCopy(latest, 0, dst, 0, Math.Min(dst.Length, latest.Length));
                return true;
            }
        }

        void CloseDevice()
        {
            var r = reader;
            var c = capture;
            reader = null;
            capture = null;
            try
            {
                if (r != null)
                {
                    WinRtCall.Await(r.StopAsync());
                    r.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Write("웹캠 멈추기 실패: " + ex.GetType().Name);
            }
            try
            {
                if (c != null) c.Dispose();
            }
            catch (Exception ex)
            {
                Log.Write("웹캠 닫기 실패: " + ex.GetType().Name);
            }
        }

        public void Dispose()
        {
            if (closed) return;
            closed = true;
            var t = poller;
            if (t != null && t != Thread.CurrentThread) t.Join(2000);
            WinRtCall.Mta<bool>(delegate
            {
                CloseDevice();
                return true;
            });
        }
    }

    /// 녹화 파일과 같이 찍은 웹캠 파일을 잇는 기록. 바꿈 자료 폴더의 cam 안에 녹화 파일 내용으로 정한 이름으로 둔다.
    static class CamLog
    {
        const string Head = "bakkum-cam 1";

        static string Folder
        {
            get
            {
                string d = Path.Combine(VAutoSave.Folder, "cam");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        /// 녹화 파일 옆에 둘 웹캠 파일 이름
        public static string NameFor(string video)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(video));
            return Util.UniquePath(Path.Combine(dir, Path.GetFileNameWithoutExtension(video) + " 웹캠.mp4"));
        }

        public static void Save(string video, string cam, double lag)
        {
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string body = Head + "\n" + Path.GetFileName(cam) + "\n" + Path.GetFullPath(cam) + "\n" + lag.ToString("0.000", inv) + "\n";
                File.WriteAllText(Path.Combine(Folder, CursorLog.Fingerprint(video) + ".txt"), body, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Write("웹캠 기록 저장 실패: " + ex.GetType().Name);
            }
        }

        /// 이 녹화 파일과 같이 찍은 웹캠 파일(녹화 파일 옆 → 처음 저장한 자리 순). 없으면 false.
        public static bool Find(string video, out string cam, out double lag)
        {
            cam = null;
            lag = 0;
            try
            {
                if (!File.Exists(video)) return false;
                string log = Path.Combine(Folder, CursorLog.Fingerprint(video) + ".txt");
                if (!File.Exists(log)) return false;
                var lines = File.ReadAllText(log, Encoding.UTF8).Replace("\r", "").Split('\n');
                if (lines.Length < 4 || lines[0] != Head) return false;
                double.TryParse(lines[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lag);
                lag = Math.Max(0, Math.Min(0.5, lag));
                string near = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video)), lines[1]);
                if (lines[1].Length > 0 && File.Exists(near)) cam = near;
                else if (lines[2].Length > 0 && File.Exists(lines[2])) cam = lines[2];
                return cam != null;
            }
            catch (Exception ex)
            {
                Log.Write("웹캠 기록 읽기 실패: " + ex.GetType().Name);
                return false;
            }
        }
    }

    /// 녹화하는 동안 화면 구석에 보이는 내 얼굴(거울처럼). 녹화본에는 찍히지 않는다(윈도우 10 2004 이상).
    /// 찍히지 않게 할 수 없는 PC에서는 띄우지 않는다.
    sealed class CamBubble : Window
    {
        const double Size = 148;
        readonly CamGrabber cam;
        readonly WriteableBitmap bmp;
        readonly byte[] px;
        readonly TextBlock hint;
        readonly System.Windows.Shapes.Ellipse face;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };

        public CamBubble(CamGrabber grabber, Rect place)
        {
            cam = grabber;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Width = Size + 8;
            Height = Size + 8;
            Title = "내 얼굴 · " + AppInfo.Name;
            px = new byte[cam.Width * cam.Height * 4];
            bmp = new WriteableBitmap(cam.Width, cam.Height, 96, 96, PixelFormats.Bgr32, null);
            // 동그라미를 카메라 그림으로 칠한다(가운데를 꽉 차게 자르고, 거울처럼 좌우를 뒤집는다).
            // 그림 요소를 뒤집으면 넘치는 부분 때문에 얼굴이 옆으로 밀려나므로 붓(ImageBrush)으로 칠한다.
            face = new System.Windows.Shapes.Ellipse
            {
                Width = Size,
                Height = Size,
                Visibility = Visibility.Hidden,
                Fill = new ImageBrush(bmp)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                    RelativeTransform = new ScaleTransform(-1, 1, 0.5, 0.5)
                }
            };
            var ring = new System.Windows.Shapes.Ellipse { Width = Size, Height = Size, Stroke = Brushes.White, StrokeThickness = 3 };
            var g = new Grid { Width = Size, Height = Size, Margin = new Thickness(4) };
            g.Children.Add(new System.Windows.Shapes.Ellipse { Width = Size, Height = Size, Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2A)) });
            g.Children.Add(face);
            g.Children.Add(ring);
            hint = new TextBlock
            {
                Text = "까맣게 나와요\n렌즈 덮개를\n확인해 주세요",
                Foreground = Brushes.White,
                FontFamily = Theme.Font,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                LineHeight = 17,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            g.Children.Add(hint);
            g.ToolTip = "녹화에는 찍히지 않아요. 끌어서 옮길 수 있어요.";
            Content = g;
            Left = -30000;
            Top = -30000;
            MouseLeftButtonDown += delegate
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
            };
            SourceInitialized += delegate
            {
                if (!ScreenArea.Float(this, false))
                {
                    Log.Write("웹캠 미리 보기: 녹화에서 숨길 수 없어 띄우지 않음");
                    Dispatcher.BeginInvoke(new Action(Close));
                }
            };
            Loaded += delegate
            {
                Left = place.X + place.Width - Width - 24;
                Top = place.Y + place.Height - Height - 96;
                timer.Start();
            };
            timer.Tick += delegate { Update(); };
            Closed += delegate { timer.Stop(); };
        }

        /// 그림 확인용: 얼굴 대신 가운데가 밝은 둥근 빛(또는 까만 화면)을 받은 작은 얼굴 창
        internal static CamBubble ForPreview(bool dark)
        {
            int w = 320, h = 180;
            var g = new CamGrabber(w, h);
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double dx = (x - w / 2.0) / (h / 2.0), dy = (y - h * 0.45) / (h / 2.0);
                    double v = Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy));
                    int i = (y * w + x) * 4;
                    px[i] = (byte)(dark ? 3 : 70 + 90 * v);
                    px[i + 1] = (byte)(dark ? 3 : 90 + 110 * v);
                    px[i + 2] = (byte)(dark ? 3 : 110 + 130 * v);
                    px[i + 3] = 255;
                }
            }
            int n = dark ? 46 : 1;
            WinRtCall.Mta<bool>(delegate
            {
                for (int k = 0; k < n; k++)
                {
                    using (var sb = CamGrabber.BitmapForTest(px, w, h)) g.FeedForTest(sb);
                }
                return true;
            });
            var b = new CamBubble(g, SystemParameters.WorkArea);
            b.Update();
            return b;
        }

        internal void Update()
        {
            var show = cam.LooksDark ? Visibility.Visible : Visibility.Collapsed;
            if (hint.Visibility != show) hint.Visibility = show;
            if (!cam.CopyLatest(px)) return;
            // 그리는 동안 기다리지 않는다(WPF가 화면을 드물게 갱신할 때 WritePixels가 100ms씩 멈출 수 있다)
            if (!bmp.TryLock(new Duration(TimeSpan.Zero))) return;
            try
            {
                Marshal.Copy(px, 0, bmp.BackBuffer, Math.Min(px.Length, bmp.BackBufferStride * bmp.PixelHeight));
                bmp.AddDirtyRect(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
            }
            finally
            {
                bmp.Unlock();
            }
            // 첫 장이 오기 전에는 까만 그림 대신 차분한 바탕만 보인다
            if (face.Visibility != Visibility.Visible) face.Visibility = Visibility.Visible;
        }
    }
}
