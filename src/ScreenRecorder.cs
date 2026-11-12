// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 화면 녹화 엔진. 화면을 GDI로 찍고 소리는 WASAPI로 받아 Media Foundation의 Sink Writer로 MP4(H.264 + AAC)에 저장한다.
// 창이 없어서 시험에서는 가짜 화면·가짜 소리를 넣어 확인한다.
// 근거: Microsoft Learn의 Capturing an Image(GDI), Loopback Recording(WASAPI), Tutorial: Using the Sink Writer 문서.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Box = System.Drawing.Rectangle;

namespace Bakkum
{
    /// 화면의 한 부분을 프레임 버퍼에 담는다.
    interface IFrameGrabber : IDisposable
    {
        int Width { get; }
        int Height { get; }
        /// target(Width×Height×4, 위쪽 줄부터 BGRA)에 지금 화면을 담는다. 못 찍었으면 false.
        bool Grab(IntPtr target);
    }

    interface ISoundSource : IDisposable
    {
        void Start();
        /// 쌓인 소리를 모두 꺼내 fifo에 넣는다.
        void Pull(SoundFifo fifo);
        /// 녹음 도중 장치가 끊겼는지
        bool Lost { get; }
    }

    /// 화면 위치와 관련된 윈도우 기능
    static class ScreenArea
    {
        [StructLayout(LayoutKind.Sequential)] struct PointXY { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct Edges { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MonitorData { public int Size; public Edges Monitor, Work; public int Flags; }

        [DllImport("user32.dll")] static extern bool GetCursorPos(out PointXY p);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(PointXY p, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorData info);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        /// 이 스레드가 배율을 거치지 않은 실제 픽셀을 쓰게 한다(배율이 다른 모니터에서도 맞도록). 이전 값을 돌려준다.
        public static IntPtr UseRealPixels()
        {
            try { return SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
        }

        public static void Back(IntPtr old)
        {
            if (old == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(old); }
            catch (EntryPointNotFoundException) { }
        }

        /// 마우스가 있는 모니터의 실제 픽셀 범위
        public static Box MonitorUnderCursor()
        {
            IntPtr old = UseRealPixels();
            try
            {
                PointXY p;
                GetCursorPos(out p);
                var info = new MonitorData { Size = Marshal.SizeOf(typeof(MonitorData)) };
                if (!GetMonitorInfo(MonitorFromPoint(p, 2), ref info)) throw new UserFacingException("화면 크기를 알아내지 못했어요.");
                return new Box(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
            }
            finally
            {
                Back(old);
            }
        }

        /// 마우스가 있는 모니터의 범위(WPF 창 좌표)
        public static System.Windows.Rect MonitorDip()
        {
            var b = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).Bounds;
            double s = MenuWindow.DpiScale();
            return new System.Windows.Rect(b.Left / s, b.Top / s, b.Width / s, b.Height / s);
        }

        public static int MonitorCount
        {
            get { return System.Windows.Forms.Screen.AllScreens.Length; }
        }

        /// H.264는 가로·세로가 짝수여야 한다.
        public static Box Even(Box b)
        {
            return new Box(b.X, b.Y, b.Width & ~1, b.Height & ~1);
        }

        /// 녹화 중에 떠 있는 창: 눌러도 다른 창의 포커스를 빼앗지 않고, 녹화본에는 찍히지 않는다(윈도우 10 2004 이상).
        public static bool Float(System.Windows.Window w, bool clickThrough)
        {
            IntPtr h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            int ex = GetWindowLong(h, -20);
            ex |= 0x08000000 | 0x00000080;
            if (clickThrough) ex |= 0x00000020;
            SetWindowLong(h, -20, ex);
            return HideFromCapture(h);
        }

        public static bool HideFromCapture(IntPtr hwnd)
        {
            return SetWindowDisplayAffinity(hwnd, 0x11);
        }
    }

    /// 진짜 화면을 찍는다. 첫 Grab이 도는 스레드에서 GDI 자원을 만든다.
    sealed class GdiGrabber : IFrameGrabber
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BitmapHeader
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CursorData
        {
            public int Size, Flags;
            public IntPtr Cursor;
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IconData
        {
            [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
            public int HotX, HotY;
            public IntPtr Mask, Color;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint op);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CursorData info);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out IconData info);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void CopyMemory(IntPtr dest, IntPtr source, UIntPtr length);

        const uint Copy = 0x00CC0020, Layered = 0x40000000;

        readonly Box box;
        readonly bool drawCursor;
        readonly uint operation;
        IntPtr screen, memory, bitmap, oldBitmap, bits;
        IntPtr lastCursor;
        int hotX, hotY;

        public GdiGrabber(Box area, bool cursor, bool layered)
        {
            box = ScreenArea.Even(area);
            drawCursor = cursor;
            operation = layered ? Copy | Layered : Copy;
        }

        public int Width { get { return box.Width; } }
        public int Height { get { return box.Height; } }

        void Open()
        {
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new UserFacingException("화면을 읽을 수 없어요.");
            memory = CreateCompatibleDC(screen);
            var head = new BitmapHeader { Size = Marshal.SizeOf(typeof(BitmapHeader)), Width = box.Width, Height = -box.Height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref head, 0, out bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) throw new UserFacingException("녹화할 메모리가 모자라요. 다른 프로그램을 닫고 다시 해 주세요.");
            oldBitmap = SelectObject(memory, bitmap);
        }

        public bool Grab(IntPtr target)
        {
            if (memory == IntPtr.Zero) Open();
            // 잠금 화면·UAC 창처럼 읽을 수 없는 화면이면 false. 다음 차례에 다시 해 본다.
            if (!BitBlt(memory, 0, 0, box.Width, box.Height, screen, box.X, box.Y, operation)) return false;
            if (drawCursor) DrawCursor();
            GdiFlush();
            CopyMemory(target, bits, new UIntPtr((uint)(box.Width * box.Height * 4)));
            return true;
        }

        void DrawCursor()
        {
            var info = new CursorData { Size = Marshal.SizeOf(typeof(CursorData)) };
            if (!GetCursorInfo(ref info) || (info.Flags & 1) == 0 || info.Cursor == IntPtr.Zero) return;
            if (info.Cursor != lastCursor)
            {
                IconData icon;
                if (!GetIconInfo(info.Cursor, out icon)) return;
                if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
                if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
                lastCursor = info.Cursor;
                hotX = icon.HotX;
                hotY = icon.HotY;
            }
            DrawIconEx(memory, info.X - hotX - box.X, info.Y - hotY - box.Y, info.Cursor, 0, 0, 0, IntPtr.Zero, 3);
        }

        public void Dispose()
        {
            if (memory != IntPtr.Zero)
            {
                SelectObject(memory, oldBitmap);
                DeleteObject(bitmap);
                DeleteDC(memory);
                memory = IntPtr.Zero;
            }
            if (screen != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screen);
                screen = IntPtr.Zero;
            }
        }
    }

    /// 48kHz 스테레오 소리를 잠깐 담아 두는 곳. 넘치면 오래된 것부터 버린다.
    sealed class SoundFifo
    {
        readonly float[] data;
        readonly int capacity;
        int head, frames;

        public SoundFifo(int seconds)
        {
            capacity = ScreenRecorder.Rate * seconds;
            data = new float[capacity * 2];
        }

        public int Frames
        {
            get { return frames; }
        }

        public void Add(float left, float right)
        {
            int tail = (head + frames) % capacity;
            data[tail * 2] = left;
            data[tail * 2 + 1] = right;
            if (frames < capacity) frames++;
            else head = (head + 1) % capacity;
        }

        /// 앞에서 n프레임을 꺼내 mix에 더한다. 모자라면 있는 만큼만(나머지는 무음으로 남는다).
        public void MixInto(float[] mix, int n)
        {
            int take = Math.Min(n, frames);
            for (int i = 0; i < take; i++)
            {
                int at = (head + i) % capacity;
                mix[i * 2] += data[at * 2];
                mix[i * 2 + 1] += data[at * 2 + 1];
            }
            Skip(take);
        }

        public void Skip(int n)
        {
            n = Math.Min(n, frames);
            head = (head + n) % capacity;
            frames -= n;
        }

        public void Clear()
        {
            head = 0;
            frames = 0;
        }
    }

    /// 장치의 표본 빈도를 48kHz로 바꾼다(앞뒤 표본을 잇는 직선 보간).
    sealed class ToStereo48k
    {
        readonly double step;
        double phase = -1;
        float lastLeft, lastRight;

        public ToStereo48k(int rate)
        {
            step = rate / (double)ScreenRecorder.Rate;
        }

        public void Push(float[] s, int frames, SoundFifo fifo)
        {
            if (frames <= 0) return;
            double p = phase;
            while (true)
            {
                int i = (int)Math.Floor(p);
                if (i + 1 >= frames) break;
                double t = p - i;
                float l0 = i < 0 ? lastLeft : s[i * 2], r0 = i < 0 ? lastRight : s[i * 2 + 1];
                float l1 = s[(i + 1) * 2], r1 = s[(i + 1) * 2 + 1];
                fifo.Add((float)(l0 + (l1 - l0) * t), (float)(r0 + (r1 - r0) * t));
                p += step;
            }
            phase = p - frames;
            lastLeft = s[(frames - 1) * 2];
            lastRight = s[(frames - 1) * 2 + 1];
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void U0();
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate([In, MarshalAs(UnmanagedType.LPStruct)] Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        void U1(); void U2(); void U3(); void U4();
        [PreserveSig] int GetMixFormat(out IntPtr format);
        void U6();
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        void U9(); void U10();
        [PreserveSig] int GetService([In, MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong counter);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    /// 마이크 또는 컴퓨터에서 나는 소리(loopback). MTA 스레드에서 만들고 쓴다.
    sealed class WasapiSource : ISoundSource
    {
        const int LoopbackFlag = 0x00020000;
        static readonly Guid ClientId = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static readonly Guid CaptureId = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        static readonly Guid FloatKind = new Guid("00000003-0000-0010-8000-00aa00389b71");

        readonly bool loopback;
        IMMDeviceEnumerator enumerator;
        IMMDevice device;
        IAudioClient client;
        IAudioCaptureClient capture;
        ToStereo48k convert;
        int channels, frameBytes, sampleBytes, bits;
        bool isFloat, lost, started;
        byte[] raw = new byte[0];
        float[] stereo = new float[0];

        public WasapiSource(bool loopback)
        {
            this.loopback = loopback;
            try
            {
                Open();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public bool Lost
        {
            get { return lost; }
        }

        string Name
        {
            get { return loopback ? "컴퓨터 소리" : "마이크"; }
        }

        void Open()
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            int hr = enumerator.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out device);
            if (hr < 0 || device == null)
            {
                Log.Write("소리 장치 없음: 0x" + hr.ToString("X8"));
                throw new UserFacingException(loopback ? "컴퓨터 소리를 내는 장치를 찾지 못했어요. 스피커나 이어폰이 연결돼 있는지 확인해 주세요." : "마이크를 찾지 못했어요. 마이크가 연결돼 있는지 확인해 주세요.");
            }
            object service;
            hr = device.Activate(ClientId, 0x17, IntPtr.Zero, out service);
            Explain(hr);
            client = (IAudioClient)service;
            IntPtr format;
            Explain(client.GetMixFormat(out format));
            try
            {
                int tag = (ushort)Marshal.ReadInt16(format, 0);
                channels = (ushort)Marshal.ReadInt16(format, 2);
                int rate = Marshal.ReadInt32(format, 4);
                frameBytes = (ushort)Marshal.ReadInt16(format, 12);
                bits = (ushort)Marshal.ReadInt16(format, 14);
                sampleBytes = channels > 0 ? frameBytes / channels : 0;
                isFloat = tag == 3;
                if (tag == 0xFFFE)
                {
                    var kind = new byte[16];
                    Marshal.Copy(new IntPtr(format.ToInt64() + 24), kind, 0, 16);
                    isFloat = new Guid(kind) == FloatKind;
                }
                convert = new ToStereo48k(rate);
                Explain(client.Initialize(0, loopback ? LoopbackFlag : 0, 2000000, 0, format, IntPtr.Zero));
            }
            finally
            {
                Marshal.FreeCoTaskMem(format);
            }
            Explain(client.GetService(CaptureId, out service));
            capture = (IAudioCaptureClient)service;
        }

        void Explain(int hr)
        {
            if (hr >= 0) return;
            Log.Write(Name + " 열기 실패: 0x" + hr.ToString("X8"));
            if (hr == unchecked((int)0x80070005))
            {
                throw new UserFacingException("윈도우가 " + Name + " 사용을 막고 있어요. 윈도우 설정의 '개인 정보 및 보안 → 마이크'에서 데스크톱 앱의 접근을 허용해 주세요.");
            }
            throw new UserFacingException(Name + "를 열지 못했어요. 다른 프로그램이 독점해서 쓰고 있는지 확인해 주세요.");
        }

        public void Start()
        {
            Explain(client.Start());
            started = true;
        }

        public void Pull(SoundFifo fifo)
        {
            if (lost) return;
            uint packet;
            int hr = capture.GetNextPacketSize(out packet);
            while (hr >= 0 && packet > 0)
            {
                IntPtr data;
                uint frames, flags;
                ulong position, counter;
                hr = capture.GetBuffer(out data, out frames, out flags, out position, out counter);
                if (hr < 0 || frames == 0) break;
                Convert(data, (int)frames, (flags & 2) != 0, fifo);
                hr = capture.ReleaseBuffer(frames);
                if (hr < 0) break;
                hr = capture.GetNextPacketSize(out packet);
            }
            if (hr < 0)
            {
                // 이어폰을 뽑는 등. 영상은 계속 찍고 소리만 무음으로 남긴다.
                lost = true;
                Log.Write(Name + " 끊김: 0x" + hr.ToString("X8"));
            }
        }

        void Convert(IntPtr data, int frames, bool silent, SoundFifo fifo)
        {
            if (stereo.Length < frames * 2) stereo = new float[frames * 2];
            if (silent)
            {
                Array.Clear(stereo, 0, frames * 2);
            }
            else
            {
                int bytes = frames * frameBytes;
                if (raw.Length < bytes) raw = new byte[bytes];
                Marshal.Copy(data, raw, 0, bytes);
                for (int i = 0; i < frames; i++)
                {
                    int at = i * frameBytes;
                    float left = Sample(at);
                    stereo[i * 2] = left;
                    stereo[i * 2 + 1] = channels > 1 ? Sample(at + sampleBytes) : left;
                }
            }
            convert.Push(stereo, frames, fifo);
        }

        float Sample(int at)
        {
            if (isFloat) return bits == 32 ? BitConverter.ToSingle(raw, at) : 0f;
            switch (bits)
            {
                case 16: return BitConverter.ToInt16(raw, at) / 32768f;
                case 24: return (raw[at] | raw[at + 1] << 8 | (sbyte)raw[at + 2] << 16) / 8388608f;
                case 32: return BitConverter.ToInt32(raw, at) / 2147483648f;
            }
            return 0f;
        }

        public void Dispose()
        {
            if (client != null && started)
            {
                try { client.Stop(); }
                catch (COMException) { }
            }
            started = false;
            Mf.Release(capture);
            Mf.Release(client);
            Mf.Release(device);
            Mf.Release(enumerator);
            capture = null;
            client = null;
            device = null;
            enumerator = null;
        }
    }

    /// 멈춘 시간을 뺀 녹화 시각(100ns 단위)
    sealed class RecordClock
    {
        readonly object gate = new object();
        long startedAt, total;
        bool running;

        public bool Running
        {
            get { lock (gate) return running; }
        }

        public void Start()
        {
            lock (gate)
            {
                if (running) return;
                startedAt = Stopwatch.GetTimestamp();
                running = true;
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                if (!running) return;
                total += Stopwatch.GetTimestamp() - startedAt;
                running = false;
            }
        }

        public long Now()
        {
            lock (gate)
            {
                long t = total;
                if (running) t += Stopwatch.GetTimestamp() - startedAt;
                return (long)(t * (10000000.0 / Stopwatch.Frequency));
            }
        }
    }

    /// 녹화 한 번. Prepare → Start → (Pause·Resume) → Finish 차례로 부른다.
    sealed class ScreenRecorder
    {
        public const int Rate = 48000;
        // 인코더가 밀리면 이보다 많이 쌓지 않고 프레임을 건너뛴다(1080p 한 장이 8MB).
        const int MostFramesWaiting = 3;
        // 늦게 도착하는 소리를 기다리는 시간. 컴퓨터 소리는 조용할 때 아무것도 오지 않아 시계에 맞춰 무음을 채운다.
        const long SoundDelay = 600000;
        const long LowDisk = 200L * 1024 * 1024;

        sealed class Item
        {
            public bool Audio;
            public IMFMediaBuffer Buffer;
            public long Time, Duration;
        }

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        readonly string path;
        readonly IFrameGrabber grabber;
        readonly List<ISoundSource> sounds;
        readonly int fps;
        readonly long period;
        readonly RecordClock clock;
        readonly Queue<Item> queue = new Queue<Item>();
        readonly ManualResetEvent soundReady = new ManualResetEvent(false);
        IMFSinkWriter sink;
        uint videoStream, audioStream;
        Thread captureThread, soundThread, writerThread;
        int framesWaiting;
        bool closing;
        volatile bool stopping;
        string failure, note;
        long framesWritten, framesDropped;
        bool ticking;

        /// 녹화를 더 잇지 못하게 됐을 때(다른 스레드에서 부른다)
        public Action Died;
        /// 마우스 자리 기록(영상 편집의 '화면 녹화 다듬기'가 쓴다). 없으면 적지 않는다.
        public CursorLog Cursor;

        public ScreenRecorder(string path, IFrameGrabber grabber, List<ISoundSource> sounds, int fps) : this(path, grabber, sounds, fps, null)
        {
        }

        /// shared를 주면 그 시계를 같이 쓴다(웹캠 녹화가 화면 녹화와 같은 시각·같은 멈춤을 따르게).
        public ScreenRecorder(string path, IFrameGrabber grabber, List<ISoundSource> sounds, int fps, RecordClock shared)
        {
            this.path = path;
            this.grabber = grabber;
            this.sounds = sounds;
            this.fps = fps;
            period = 10000000L / fps;
            clock = shared ?? new RecordClock();
        }

        /// 이 녹화의 시계(웹캠 녹화가 같이 쓴다)
        public RecordClock Clock { get { return clock; } }
        public string OutputPath { get { return path; } }

        public string Failure { get { return failure; } }
        public string Note { get { return note; } }
        public long FramesWritten { get { return Interlocked.Read(ref framesWritten); } }
        public long FramesDropped { get { return Interlocked.Read(ref framesDropped); } }
        public bool Paused { get { return !clock.Running; } }
        public double Seconds { get { return clock.Now() / 1e7; } }

        /// 저장 파일과 인코더를 연다. 실패하면 사용자에게 보일 문장으로 예외를 던진다.
        public void Prepare()
        {
            WinRtCall.Mta<bool>(delegate
            {
                try
                {
                    sink = Open(true);
                }
                catch (Exception ex)
                {
                    if (ex is UserFacingException) throw;
                    Log.Write("녹화: 그래픽 인코더로 열지 못함 " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                    try
                    {
                        sink = Open(false);
                    }
                    catch (Exception second)
                    {
                        Log.Write("녹화: 인코더 열기 실패 " + second.GetType().Name + " 0x" + second.HResult.ToString("X8"));
                        if (second is IOException || second is UnauthorizedAccessException) throw new UserFacingException("저장할 폴더에 파일을 만들 수 없어요. 폴더가 있는지, 보안 프로그램이 막고 있지 않은지 확인해 주세요.");
                        throw new UserFacingException("이 화면 크기로는 녹화를 시작하지 못했어요. 영역을 골라 더 작게 녹화해 보세요.");
                    }
                }
                sink.BeginWriting();
                return true;
            });
        }

        IMFSinkWriter Open(bool hardware)
        {
            IMFAttributes a = Mf.Attributes(3);
            IMFSinkWriter w = null;
            bool ok = false;
            try
            {
                a.SetGUID(Mf.Container, Mf.ContainerMp4);
                a.SetUINT32(Mf.HardwareTransforms, hardware ? 1u : 0u);
                w = Mf.SinkWriter(path, a);
                AddVideo(w);
                if (sounds.Count > 0) AddAudio(w);
                ok = true;
                return w;
            }
            finally
            {
                Mf.Release(a);
                if (!ok) Mf.Release(w);
            }
        }

        void AddVideo(IMFSinkWriter w)
        {
            int width = grabber.Width, height = grabber.Height;
            IMFMediaType target = Mf.MediaType(), input = Mf.MediaType();
            try
            {
                target.SetGUID(Mf.MajorType, Mf.Video);
                target.SetGUID(Mf.Subtype, Mf.H264);
                target.SetUINT32(Mf.AvgBitrate, Bitrate(width, height));
                target.SetUINT32(Mf.Profile, 100);
                Describe(target, width, height);
                w.AddStream(target, out videoStream);
                input.SetGUID(Mf.MajorType, Mf.Video);
                input.SetGUID(Mf.Subtype, Mf.Rgb32);
                Describe(input, width, height);
                input.SetUINT32(Mf.Stride, (uint)(width * 4));
                Mf.SetVideoInput(w, videoStream, input, true);
            }
            finally
            {
                Mf.Release(target);
                Mf.Release(input);
            }
        }

        /// 글자가 뭉개지지 않을 만큼. 1080p 24장이면 약 4Mbps.
        uint Bitrate(int width, int height)
        {
            double bps = width * (double)height * fps * 0.08;
            return (uint)Math.Max(1000000, Math.Min(12000000, bps));
        }

        void Describe(IMFMediaType t, int width, int height)
        {
            t.SetUINT64(Mf.FrameSize, ((ulong)(uint)width << 32) | (uint)height);
            t.SetUINT64(Mf.FrameRate, ((ulong)(uint)fps << 32) | 1u);
            t.SetUINT64(Mf.PixelAspect, (1UL << 32) | 1u);
            t.SetUINT32(Mf.Interlace, 2);
        }

        void AddAudio(IMFSinkWriter w)
        {
            IMFMediaType target = Mf.MediaType(), input = Mf.MediaType();
            try
            {
                target.SetGUID(Mf.MajorType, Mf.Audio);
                target.SetGUID(Mf.Subtype, Mf.Aac);
                target.SetUINT32(Mf.SampleRate, Rate);
                target.SetUINT32(Mf.Channels, 2);
                target.SetUINT32(Mf.BitsPerSample, 16);
                target.SetUINT32(Mf.BytesPerSecond, 20000);
                w.AddStream(target, out audioStream);
                input.SetGUID(Mf.MajorType, Mf.Audio);
                input.SetGUID(Mf.Subtype, Mf.Pcm);
                input.SetUINT32(Mf.SampleRate, Rate);
                input.SetUINT32(Mf.Channels, 2);
                input.SetUINT32(Mf.BitsPerSample, 16);
                input.SetUINT32(Mf.BlockAlign, 4);
                input.SetUINT32(Mf.BytesPerSecond, Rate * 4);
                w.SetInputMediaType(audioStream, input, null);
            }
            finally
            {
                Mf.Release(target);
                Mf.Release(input);
            }
        }

        public void Start()
        {
            timeBeginPeriod(1);
            ticking = true;
            writerThread = Spawn(WriterLoop, "녹화 저장", ThreadPriority.Normal);
            if (sounds.Count > 0)
            {
                soundThread = Spawn(SoundLoop, "녹화 소리", ThreadPriority.AboveNormal);
                soundReady.WaitOne(3000);
            }
            clock.Start();
            captureThread = Spawn(CaptureLoop, "녹화 화면", ThreadPriority.AboveNormal);
        }

        static Thread Spawn(ThreadStart work, string name, ThreadPriority priority)
        {
            var t = new Thread(work) { IsBackground = true, Name = name, Priority = priority };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            return t;
        }

        public void Pause()
        {
            clock.Stop();
        }

        public void Resume()
        {
            clock.Start();
        }

        /// 시작하기 전에 취소
        public void Discard()
        {
            WinRtCall.Mta<bool>(delegate
            {
                foreach (var s in sounds) s.Dispose();
                grabber.Dispose();
                Mf.Release(sink);
                sink = null;
                return true;
            });
            DeleteFile();
        }

        /// 녹화를 끝내고 저장을 마무리한다. 끝나면 done(오류 문장 또는 null)을 다른 스레드에서 부른다.
        public void Finish(Action<string> done)
        {
            stopping = true;
            var t = new Thread(delegate ()
            {
                try
                {
                    if (captureThread != null) captureThread.Join();
                    if (soundThread != null) soundThread.Join();
                    clock.Stop();
                    lock (queue)
                    {
                        closing = true;
                        Monitor.PulseAll(queue);
                    }
                    if (writerThread != null) writerThread.Join();
                    var cursor = Cursor;
                    if (cursor != null && failure == null) cursor.Save(path);
                }
                catch (Exception ex)
                {
                    Log.Write("녹화 마무리 실패: " + ex.GetType().Name);
                    if (failure == null) failure = "녹화를 마무리하지 못했어요.";
                }
                if (ticking) timeEndPeriod(1);
                ticking = false;
                done(failure);
            }) { IsBackground = true, Name = "녹화 마무리" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        void Fail(string message)
        {
            bool first;
            lock (queue)
            {
                first = failure == null;
                if (first) failure = message;
            }
            stopping = true;
            var h = Died;
            if (first && h != null) h();
        }

        void EndEarly(string why)
        {
            note = why;
            stopping = true;
            var h = Died;
            if (h != null) h();
        }

        void CaptureLoop()
        {
            ScreenArea.UseRealPixels();
            int bytes = grabber.Width * grabber.Height * 4;
            long next = 0;
            int sinceCheck = 0;
            try
            {
                while (!stopping)
                {
                    if (!clock.Running)
                    {
                        Thread.Sleep(15);
                        continue;
                    }
                    long now = clock.Now();
                    if (now < next)
                    {
                        Thread.Sleep((int)Math.Max(1, (next - now) / 10000));
                        continue;
                    }
                    // 늦었으면 놓친 차례는 건너뛴다. 시각은 늘 정해진 칸에 맞춘다.
                    long time = now / period * period;
                    next = time + period;
                    bool room;
                    lock (queue) room = framesWaiting < MostFramesWaiting;
                    if (!room)
                    {
                        Interlocked.Increment(ref framesDropped);
                        continue;
                    }
                    IMFMediaBuffer buffer = Mf.Buffer(bytes);
                    bool taken;
                    IntPtr p;
                    uint max, current;
                    buffer.Lock(out p, out max, out current);
                    try
                    {
                        taken = grabber.Grab(p);
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                    if (!taken)
                    {
                        Mf.Release(buffer);
                        Interlocked.Increment(ref framesDropped);
                        continue;
                    }
                    buffer.SetCurrentLength((uint)bytes);
                    var cursor = Cursor;
                    if (cursor != null) cursor.Sample(time / 1e7);
                    Enqueue(new Item { Buffer = buffer, Time = time, Duration = period });
                    if (++sinceCheck >= fps * 5)
                    {
                        sinceCheck = 0;
                        if (DiskIsLow())
                        {
                            EndEarly("디스크 공간이 거의 없어서 녹화를 여기서 끝냈어요.");
                            break;
                        }
                    }
                }
            }
            catch (UserFacingException ex)
            {
                Fail(ex.Message);
            }
            catch (Exception ex)
            {
                Log.Write("녹화 화면 찍기 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                Fail("화면을 찍지 못했어요. (" + ex.GetType().Name + ")");
            }
            finally
            {
                grabber.Dispose();
            }
        }

        bool DiskIsLow()
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(path)).AvailableFreeSpace < LowDisk;
            }
            catch (Exception)
            {
                return false;
            }
        }

        void SoundLoop()
        {
            var mix = new float[Rate * 2];
            var pcm = new short[Rate * 2];
            var fifos = new List<SoundFifo>();
            foreach (var s in sounds) fifos.Add(new SoundFifo(4));
            long written = 0;
            try
            {
                foreach (var s in sounds) s.Start();
                soundReady.Set();
                while (!stopping)
                {
                    PullAll(fifos);
                    bool running = clock.Running;
                    long now = clock.Now();
                    MixUpTo(fifos, running ? now - SoundDelay : now, running ? 480 : 1, ref written, mix, pcm);
                    if (!running)
                    {
                        // 멈춘 동안의 소리는 녹화본에 넣지 않는다.
                        foreach (var f in fifos) f.Clear();
                    }
                    Thread.Sleep(10);
                }
                PullAll(fifos);
                MixUpTo(fifos, clock.Now(), 1, ref written, mix, pcm);
                foreach (var s in sounds)
                {
                    if (s.Lost) note = "녹화 도중 소리 장치가 끊겨서 그 뒤로는 소리가 없어요.";
                }
            }
            catch (UserFacingException ex)
            {
                Fail(ex.Message);
            }
            catch (Exception ex)
            {
                Log.Write("녹화 소리 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                Fail("소리를 녹음하지 못했어요. (" + ex.GetType().Name + ")");
            }
            finally
            {
                soundReady.Set();
                foreach (var s in sounds)
                {
                    try { s.Dispose(); }
                    catch (Exception ex) { Log.Write("소리 장치 닫기 실패: " + ex.GetType().Name); }
                }
            }
        }

        void PullAll(List<SoundFifo> fifos)
        {
            for (int i = 0; i < sounds.Count; i++) sounds[i].Pull(fifos[i]);
        }

        void MixUpTo(List<SoundFifo> fifos, long time, int atLeast, ref long written, float[] mix, short[] pcm)
        {
            long due = time <= 0 ? 0 : (long)(time * (Rate / 1e7));
            while (due - written >= atLeast)
            {
                int n = (int)Math.Min(due - written, Rate);
                Array.Clear(mix, 0, n * 2);
                foreach (var f in fifos) f.MixInto(mix, n);
                EnqueueSound(mix, pcm, n, written);
                written += n;
            }
            // 장치의 시계가 조금씩 어긋나 쌓이면 잘라 내서 영상과 어긋나지 않게 한다.
            foreach (var f in fifos)
            {
                if (f.Frames > Rate / 4) f.Skip(f.Frames - Rate / 10);
            }
        }

        void EnqueueSound(float[] mix, short[] pcm, int n, long at)
        {
            for (int i = 0; i < n * 2; i++)
            {
                float v = mix[i];
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                pcm[i] = (short)(v * 32767f);
            }
            IMFMediaBuffer buffer = Mf.Buffer(n * 4);
            IntPtr p;
            uint max, current;
            buffer.Lock(out p, out max, out current);
            try
            {
                Marshal.Copy(pcm, 0, p, n * 2);
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.SetCurrentLength((uint)(n * 4));
            Enqueue(new Item { Audio = true, Buffer = buffer, Time = (long)(at * (1e7 / Rate)), Duration = (long)(n * (1e7 / Rate)) });
        }

        void Enqueue(Item it)
        {
            lock (queue)
            {
                queue.Enqueue(it);
                if (!it.Audio) framesWaiting++;
                Monitor.PulseAll(queue);
            }
        }

        void WriterLoop()
        {
            bool broken = false;
            while (true)
            {
                Item it;
                lock (queue)
                {
                    while (queue.Count == 0 && !closing) Monitor.Wait(queue);
                    if (queue.Count == 0) break;
                    it = queue.Dequeue();
                }
                try
                {
                    if (!broken) WriteOne(it);
                }
                catch (Exception ex)
                {
                    broken = true;
                    Log.Write("녹화 저장 실패: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                    Fail(ex.HResult == unchecked((int)0x80070070) ? "디스크 공간이 모자라 저장하지 못했어요." : "영상을 저장하지 못했어요.");
                }
                finally
                {
                    Mf.Release(it.Buffer);
                    if (!it.Audio)
                    {
                        lock (queue) framesWaiting--;
                    }
                }
            }
            try
            {
                if (failure == null)
                {
                    if (FramesWritten == 0) failure = "화면을 한 장도 찍지 못했어요.";
                    else sink.Finish();
                }
            }
            catch (Exception ex)
            {
                Log.Write("녹화 파일 닫기 실패: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                if (failure == null) failure = "영상을 마무리하지 못했어요.";
            }
            Mf.Release(sink);
            sink = null;
            if (failure != null) DeleteFile();
        }

        void WriteOne(Item it)
        {
            IMFSample sample = Mf.Sample(it.Buffer, it.Time, it.Duration);
            try
            {
                sink.WriteSample(it.Audio ? audioStream : videoStream, sample);
                if (!it.Audio) Interlocked.Increment(ref framesWritten);
            }
            finally
            {
                Mf.Release(sample);
            }
        }

        void DeleteFile()
        {
            // 인코더가 파일을 놓기까지 잠깐 걸릴 수 있다.
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(50);
                }
            }
            Log.Write("녹화 임시 파일을 지우지 못함");
        }
    }

    /// 시험용 가짜 화면: 네 귀퉁이 색이 정해져 있고 가운데 막대가 움직인다.
    sealed class TestFrames : IFrameGrabber
    {
        readonly int w, h;
        readonly byte[] pixels;
        int n;

        public TestFrames(int width, int height)
        {
            w = width & ~1;
            h = height & ~1;
            pixels = new byte[w * h * 4];
        }

        public int Width { get { return w; } }
        public int Height { get { return h; } }

        public bool Grab(IntPtr target)
        {
            int barLeft = n * 6 % w, barWidth = w / 10;
            for (int y = 0; y < h; y++)
            {
                bool middle = y > h * 45 / 100 && y < h * 55 / 100;
                for (int x = 0; x < w; x++)
                {
                    int at = (y * w + x) * 4;
                    byte b, g, r;
                    if (middle)
                    {
                        bool bar = x >= barLeft && x < barLeft + barWidth;
                        b = 0;
                        g = bar ? (byte)255 : (byte)0;
                        r = bar ? (byte)255 : (byte)0;
                    }
                    else if (y < h / 2)
                    {
                        // 왼쪽 위 빨강, 오른쪽 위 초록
                        b = 0;
                        g = x < w / 2 ? (byte)0 : (byte)255;
                        r = x < w / 2 ? (byte)255 : (byte)0;
                    }
                    else
                    {
                        // 왼쪽 아래 파랑, 오른쪽 아래 흰색
                        b = 255;
                        g = x < w / 2 ? (byte)0 : (byte)255;
                        r = x < w / 2 ? (byte)0 : (byte)255;
                    }
                    pixels[at] = b;
                    pixels[at + 1] = g;
                    pixels[at + 2] = r;
                    pixels[at + 3] = 255;
                }
            }
            Marshal.Copy(pixels, 0, target, pixels.Length);
            n++;
            return true;
        }

        public void Dispose()
        {
        }
    }

    /// 시험용 가짜 소리(시계에 맞춰 440Hz가 들어온다)
    sealed class ToneSource : ISoundSource
    {
        readonly Stopwatch watch = new Stopwatch();
        long pushed;

        public bool Lost { get { return false; } }

        public void Start()
        {
            watch.Start();
        }

        public void Pull(SoundFifo fifo)
        {
            long due = (long)(watch.Elapsed.TotalSeconds * ScreenRecorder.Rate);
            for (; pushed < due; pushed++)
            {
                float v = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * pushed / ScreenRecorder.Rate));
                fifo.Add(v, v);
            }
        }

        public void Dispose()
        {
        }
    }

    /// 녹화하는 동안 마우스 자리와 왼쪽 단추를 적어 둔다(영상 편집의 '화면 녹화 다듬기'가 쓴다).
    /// 녹화 파일 옆에 '<녹화 이름>.cursor.txt'로 저장한다. 화면 내용이나 누른 글쇠는 적지 않는다.
    sealed class CursorLog
    {
        [StructLayout(LayoutKind.Sequential)] struct PointXY { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out PointXY p);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

        readonly Box box;
        readonly System.Text.StringBuilder lines = new System.Text.StringBuilder();
        readonly object gate = new object();
        int lastX = int.MinValue, lastY = int.MinValue;
        bool lastDown;
        int count;

        public CursorLog(Box area)
        {
            box = area;
        }

        /// 녹화 시각 t(초)의 마우스. 녹화 화면 스레드에서 부른다(실제 픽셀 좌표).
        public void Sample(double t)
        {
            PointXY p;
            if (!GetCursorPos(out p)) return;
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (p.X == lastX && p.Y == lastY && down == lastDown) return;
            lastX = p.X;
            lastY = p.Y;
            lastDown = down;
            if (count > 400000) return;
            count++;
            double x = (p.X - box.X) / (double)Math.Max(1, box.Width), y = (p.Y - box.Y) / (double)Math.Max(1, box.Height);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            lock (gate) lines.Append(t.ToString("0.000", inv)).Append(' ').Append(x.ToString("0.0000", inv)).Append(' ').Append(y.ToString("0.0000", inv)).Append(' ').Append(down ? '1' : '0').Append('\n');
        }

        /// 기록 파일 자리. 선생님 폴더에 파일을 늘어놓지 않게 바꿈 자료 폴더에 두고,
        /// 이름은 영상 내용(크기·앞뒤 조각)으로 정해서 영상을 옮기거나 이름을 바꿔도 찾는다.
        public static string PathFor(string video)
        {
            string dir = Path.Combine(VAutoSave.Folder, "cursor");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, Fingerprint(video) + ".txt");
        }

        internal static string Fingerprint(string video)
        {
            using (var fs = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                long len = fs.Length;
                int part = (int)Math.Min(len, 256 * 1024);
                var buf = new byte[part * 2 + 8];
                int got = ReadFull(fs, buf, 0, part);
                fs.Position = Math.Max(0, len - part);
                got += ReadFull(fs, buf, got, part);
                BitConverter.GetBytes(len).CopyTo(buf, got);
                var h = sha.ComputeHash(buf, 0, got + 8);
                return BitConverter.ToString(h, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }

        static int ReadFull(Stream s, byte[] buf, int at, int count)
        {
            int got = 0;
            while (got < count)
            {
                int r = s.Read(buf, at + got, count - got);
                if (r <= 0) break;
                got += r;
            }
            return got;
        }

        public void Save(string video)
        {
            try
            {
                string body;
                lock (gate) body = "bakkum-cursor 1 " + box.Width + " " + box.Height + "\n" + lines;
                File.WriteAllText(PathFor(video), body);
            }
            catch (Exception ex)
            {
                Log.Write("마우스 기록 저장 실패: " + ex.GetType().Name);
            }
        }
    }
}
