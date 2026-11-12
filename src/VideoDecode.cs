// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집용으로 원본을 푼다: 그림(그래픽 장치로 빠르게 풀고 원하는 크기로 줄여 받기), 소리(48kHz 두 줄로 풀어 임시 파일에),
// 시간 막대에 늘어놓을 작은 그림 띠.
// 2026-11-12 이 PC 실측(1080p, 열쇠 장면 간격 5초): 그래픽 장치로 640×360을 받으면 1초에 338장, 아무 데로 넘기기 0.17~0.37초.
// 그래픽 장치가 없으면 윈도우의 소프트웨어 해독(NV12)을 받아 필요한 장만 바꾼다(넘기기 0.4~0.8초).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bakkum
{
    /// 그래픽 장치(D3D11)를 여러 해독기가 같이 쓴다
    static class VGpu
    {
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint nLevels, uint sdk, out IntPtr device, out int level, out IntPtr context);

        [DllImport("mfplat.dll")]
        static extern int MFCreateDXGIDeviceManager(out uint token, [MarshalAs(UnmanagedType.IUnknown)] out object manager);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResetDeviceFn(IntPtr self, IntPtr device, uint token);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetMtFn(IntPtr self, int on);

        static readonly object Gate = new object();
        static bool tried;
        static object manager;
        /// 시험용: 그래픽 장치를 쓰지 않는다
        public static bool Off;
        public static string Note = "";

        static T Fn<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fp = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fp, typeof(T));
        }

        public static object Manager
        {
            get
            {
                if (Off) return null;
                lock (Gate)
                {
                    if (tried) return manager;
                    tried = true;
                    try
                    {
                        Mf.Start();
                        IntPtr dev, ctx;
                        int level;
                        int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20 | 0x800, IntPtr.Zero, 0, 7, out dev, out level, out ctx);
                        if (hr < 0)
                        {
                            Note = "D3D11 0x" + hr.ToString("X8");
                            return null;
                        }
                        try
                        {
                            Guid iidMt = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
                            IntPtr mt;
                            if (Marshal.QueryInterface(dev, ref iidMt, out mt) == 0)
                            {
                                Fn<SetMtFn>(mt, 5)(mt, 1);
                                Marshal.Release(mt);
                            }
                            uint token;
                            object m;
                            hr = MFCreateDXGIDeviceManager(out token, out m);
                            if (hr < 0)
                            {
                                Note = "DXGI manager 0x" + hr.ToString("X8");
                                return null;
                            }
                            IntPtr pm = Marshal.GetIUnknownForObject(m);
                            Guid iidMgr = new Guid("eb533d5d-2db6-40f8-97a9-494692014f07");
                            IntPtr mgr;
                            hr = Marshal.QueryInterface(pm, ref iidMgr, out mgr);
                            Marshal.Release(pm);
                            if (hr < 0) return null;
                            hr = Fn<ResetDeviceFn>(mgr, 7)(mgr, dev, token);
                            Marshal.Release(mgr);
                            if (hr < 0)
                            {
                                Note = "ResetDevice 0x" + hr.ToString("X8");
                                return null;
                            }
                            manager = m;
                            Note = "gpu";
                            return manager;
                        }
                        finally
                        {
                            Marshal.Release(ctx);
                            Marshal.Release(dev);
                        }
                    }
                    catch (Exception ex)
                    {
                        Note = ex.GetType().Name;
                        Log.Write("영상 편집: 그래픽 장치 준비 실패 " + ex.GetType().Name);
                        return null;
                    }
                }
            }
        }
    }

    /// 원본 영상 하나에서 원하는 시각의 그림을 꺼낸다(BGRA, 위쪽 줄부터, 돌리기 전 모습).
    /// 한 스레드에서만 쓴다.
    sealed class VideoReader : IDisposable
    {
        static readonly Guid EnableAdvanced = new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");
        static readonly Guid D3DManager = new Guid("ec822da2-e1e9-4b29-a0d8-563c719f5269");
        /// MF_XVP_DISABLE_FRC: 영상 처리기가 초당 장수를 바꾸지 않게
        static readonly Guid DisableFrc = new Guid("2c0afa19-7a97-4d5a-9ee8-16d4fc518d8c");
        static readonly Guid Nv12 = new Guid("3231564E-0000-0010-8000-00aa00389b71");
        static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        static readonly Guid YuvMatrix = new Guid("3e23d450-2c75-4d25-a00e-b91670d12327");

        public readonly string Source;
        /// 내주는 그림 크기
        public int Width, Height;
        /// 원본에서 보이는 크기(돌리기 전)
        public int CodedW, CodedH;
        public bool Gpu;
        public double Step = 1 / 30.0;
        public double Duration;

        IMFSourceReader reader;
        // 받는 모양: 0 = 그래픽 장치 RGB32(줄여서), 1 = NV12(우리가 바꿈), 2 = RGB32(윈도우가 바꿈, 우리가 줄임)
        int mode;
        int bufW, bufH, stride, offX, offY;
        bool bt709;

        IMFSample held;
        double heldTime = -1;
        byte[] cur;
        double curTime = double.NaN;
        double lastRead = double.NegativeInfinity;
        bool ended;
        // 최근에 꺼낸 그림 몇 장(한 장씩 뒤로 갈 때 바로)
        readonly List<KeyValuePair<double, byte[]>> recent = new List<KeyValuePair<double, byte[]>>();
        byte[] rgbScratch;

        public VideoReader(string path, int boxW, int boxH, bool allowGpu)
        {
            Source = path;
            Mf.Start();
            ReadNative(path);
            double scale = 1;
            if (boxW > 0 && boxH > 0) scale = Math.Min(1, Math.Min(boxW / (double)CodedW, boxH / (double)CodedH));
            Width = Math.Max(2, (int)Math.Round(CodedW * scale) & ~1);
            Height = Math.Max(2, (int)Math.Round(CodedH * scale) & ~1);
            object gpu = allowGpu ? VGpu.Manager : null;
            if (gpu != null)
            {
                try
                {
                    OpenGpu(path, gpu);
                    Gpu = true;
                    return;
                }
                catch (Exception ex)
                {
                    Log.Write("영상 편집: 그래픽 장치로 열지 못함 " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                    Close();
                }
            }
            if (!TryOpenSoftware(path, false)) TryOpenSoftware(path, true);
        }

        void ReadNative(string path)
        {
            var r = Mf.Reader(path, false);
            try
            {
                IMFMediaType t;
                if (r.GetNativeMediaType(Mf.FirstVideo, 0, out t) < 0) throw new UserFacingException("이 파일에서 영상을 찾지 못했어요.");
                try
                {
                    ulong size;
                    if (t.GetUINT64(Mf.FrameSize, out size) < 0) throw new UserFacingException("영상 크기를 읽지 못했어요.");
                    CodedW = (int)(size >> 32);
                    CodedH = (int)(size & 0xFFFFFFFF);
                    int w, h;
                    if (MediaProbe.Aperture(t, out w, out h) && w <= CodedW && h <= CodedH)
                    {
                        CodedW = w;
                        CodedH = h;
                    }
                    ulong rate;
                    if (t.GetUINT64(Mf.FrameRate, out rate) >= 0)
                    {
                        double n = rate >> 32, d = rate & 0xFFFFFFFF;
                        if (n > 0 && d > 0 && n / d > 1 && n / d < 241) Step = d / n;
                    }
                    uint m;
                    bt709 = t.GetUINT32(YuvMatrix, out m) >= 0 ? m == 1 : CodedH >= 600;
                    Guid sub;
                    if (t.GetGUID(Mf.Subtype, out sub) >= 0) codec = sub;
                    uint rot;
                    if (t.GetUINT32(Mf.Rotation, out rot) >= 0) turn = rot;
                }
                finally
                {
                    Mf.Release(t);
                }
                MfPropVariant d2;
                if (r.GetPresentationAttribute(Mf.MediaSource, Mf.Duration, out d2) >= 0) Duration = d2.Value / 1e7;
            }
            finally
            {
                Mf.Release(r);
            }
        }

        void OpenGpu(string path, object manager)
        {
            var a = Mf.Attributes(4);
            try
            {
                a.SetUnknown(D3DManager, manager);
                a.SetUINT32(Mf.HardwareTransforms, 1);
                a.SetUINT32(EnableAdvanced, 1);
                reader = Mf.Reader(path, a);
            }
            finally
            {
                Mf.Release(a);
            }
            reader.SetStreamSelection(Mf.AllStreams, false);
            reader.SetStreamSelection(Mf.FirstVideo, true);
            var want = Mf.MediaType();
            try
            {
                want.SetGUID(Mf.MajorType, Mf.Video);
                want.SetGUID(Mf.Subtype, Mf.Rgb32);
                want.SetUINT64(Mf.FrameSize, ((ulong)(uint)Width << 32) | (uint)Height);
                reader.SetCurrentMediaType(Mf.FirstVideo, IntPtr.Zero, want);
            }
            finally
            {
                Mf.Release(want);
            }
            // 그래픽 장치의 영상 처리기 손보기(2026-11-12 실측 두 가지).
            // 1) 적힌 돌림대로 그림을 미리 세워 가로 틀(Width×Height) 가운데에 작게 넣는다: 휴대폰 세로 영상이 편집에서 한 번 더 돌고
            //    작아졌다. 바꿈은 그리는 쪽에서 돌리므로 처리기의 돌리기를 끈다. 끄지 못하면 CPU로 푼다(생성자가 그쪽으로 넘어간다).
            // 2) 장 간격이 들쭉날쭉한 휴대폰 영상을 평균 장수의 고른 간격으로 바꿔, 어떤 곳에서는 한 장 늦은 그림이 나왔다. 장수 바꾸기를 끈다.
            if (!TuneProcessor()) throw new InvalidOperationException("영상 처리기의 돌리기를 끄지 못함");
            mode = 0;
            LoadFormat();
        }

        /// 영상 처리기의 자동 돌리기와 장수 바꾸기를 끈다. 돌리기를 껐거나 끌 필요가 없으면 true.
        bool TuneProcessor()
        {
            bool unturned = turn == 0;
            var ex = reader as IMFSourceReaderEx;
            if (ex == null) return unturned;
            for (uint i = 0; i < 8; i++)
            {
                Guid category;
                object t;
                if (ex.GetTransformForStream(Mf.FirstVideo, i, out category, out t) < 0) break;
                try
                {
                    var vp = t as IMFVideoProcessorControl;
                    if (vp == null) continue;
                    if (turn != 0 && vp.SetRotation(0) >= 0) unturned = true;
                    var mft = t as IMFTransform;
                    IMFAttributes a;
                    if (mft != null && mft.GetAttributes(out a) >= 0 && a != null)
                    {
                        try
                        {
                            a.SetUINT32(DisableFrc, 1);
                        }
                        catch (Exception e)
                        {
                            Log.Write("영상 처리기 장수 바꾸기 끄기 실패: " + e.GetType().Name);
                        }
                        finally
                        {
                            Mf.Release(a);
                        }
                    }
                }
                finally
                {
                    if (t != null && Marshal.IsComObject(t)) Marshal.ReleaseComObject(t);
                }
            }
            return unturned;
        }

        bool TryOpenSoftware(string path, bool rgb)
        {
            try
            {
                reader = Mf.Reader(path, rgb);
                reader.SetStreamSelection(Mf.AllStreams, false);
                reader.SetStreamSelection(Mf.FirstVideo, true);
                var want = Mf.MediaType();
                try
                {
                    want.SetGUID(Mf.MajorType, Mf.Video);
                    want.SetGUID(Mf.Subtype, rgb ? Mf.Rgb32 : Nv12);
                    reader.SetCurrentMediaType(Mf.FirstVideo, IntPtr.Zero, want);
                }
                finally
                {
                    Mf.Release(want);
                }
                mode = rgb ? 2 : 1;
                LoadFormat();
                return true;
            }
            catch (Exception ex)
            {
                if (ex is UserFacingException) throw;
                Close();
                if (rgb)
                {
                    Log.Write("영상 편집: 영상 열기 실패 0x" + ex.HResult.ToString("X8"));
                    throw new UserFacingException(Unsupported(path, codec));
                }
                return false;
            }
        }

        // 윈도우에 따로 깔아야 하는 압축 방식(파일 안에 적힌 영상 형식)
        internal static readonly Guid Hevc = new Guid("43564548-0000-0010-8000-00aa00389b71");
        internal static readonly Guid Av1 = new Guid("31305641-0000-0010-8000-00aa00389b71");
        internal static readonly Guid Vp9 = new Guid("30395056-0000-0010-8000-00aa00389b71");
        /// 파일 안에 적힌 영상 형식(못 읽었으면 빈 값)
        Guid codec;
        /// 파일에 적힌 돌림(휴대폰 세로 영상은 90). 그림은 돌리지 않은 채로 내주고, 돌리기는 그리는 쪽이 한다.
        uint turn;

        public static string Unsupported(string path)
        {
            return Unsupported(path, Guid.Empty);
        }

        /// 풀지 못했을 때의 안내. 갤럭시도 HEVC를 .mp4에 담으므로 확장자보다 적힌 형식을 먼저 본다.
        public static string Unsupported(string path, Guid codec)
        {
            const string Hevc265 = "이 PC의 윈도우가 이 영상을 풀지 못해요. 고효율(HEVC) 방식 영상이라 Microsoft Store의 'HEVC 비디오 확장'이 있어야 열려요. 휴대폰으로 찍는다면 카메라 설정에서 고효율 동영상을 끄고 찍어도 돼요(아이폰: 설정 → 카메라 → 포맷 → 높은 호환성).";
            if (codec == Hevc) return Hevc265;
            if (codec == Av1) return "이 PC의 윈도우가 이 영상을 풀지 못해요. AV1 영상이에요. Microsoft Store의 'AV1 비디오 확장'을 설치하면 열 수 있어요.";
            if (codec == Vp9) return "이 PC의 윈도우가 이 영상을 풀지 못해요. VP9 영상이에요. Microsoft Store의 'VP9 비디오 확장'을 설치하면 열 수 있어요.";
            string e = Path.GetExtension(path).ToLowerInvariant();
            if (codec == Guid.Empty && (e == ".mov" || e == ".heic" || e == ".hevc")) return "이 PC의 윈도우가 이 영상을 풀지 못해요. 아이폰 영상(HEVC)이면 Microsoft Store의 'HEVC 비디오 확장'이 필요해요.";
            return "이 PC의 윈도우가 이 영상을 풀지 못해요. 필요한 코덱이 없거나 파일이 손상됐을 수 있어요.";
        }

        void LoadFormat()
        {
            IMFMediaType t;
            reader.GetCurrentMediaType(Mf.FirstVideo, out t);
            try
            {
                ulong size;
                t.GetUINT64(Mf.FrameSize, out size);
                bufW = (int)(size >> 32);
                bufH = (int)(size & 0xFFFFFFFF);
                uint s;
                stride = t.GetUINT32(DefaultStride, out s) >= 0 ? (int)s : 0;
                int x, y, w, h;
                if (mode != 0 && MediaProbe.AreaOf(t, out x, out y, out w, out h) && x + w <= bufW && y + h <= bufH)
                {
                    offX = x;
                    offY = y;
                }
                else
                {
                    offX = offY = 0;
                }
            }
            finally
            {
                Mf.Release(t);
            }
        }

        public double Last
        {
            get { return curTime; }
        }

        /// t에 보이는 그림(그 시각이나 그 앞의 가장 가까운 장). 그림이 없으면 null.
        /// 돌려준 배열은 이 읽개가 다시 쓰므로, 일곱 장을 더 꺼내기 전에 다 써야 한다.
        public byte[] At(double t, out double frameTime)
        {
            frameTime = curTime;
            if (cur != null && !double.IsNaN(curTime) && t >= curTime - 1e-4 && (held == null ? ended || t < curTime + Step * 0.5 : t < heldTime - 1e-4))
                return cur;
            // 조금 뒤로(한 장 뒤로 가기, 부드러운 느린 화면): 최근에 꺼낸 장에서
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                double next = i + 1 < recent.Count ? recent[i + 1].Key : curTime;
                if (t >= recent[i].Key - 1e-4 && t < next - 1e-4)
                {
                    frameTime = recent[i].Key;
                    return recent[i].Value;
                }
            }
            bool seek = cur == null || double.IsNaN(curTime) || t < curTime - 1e-4 || t > Math.Max(lastRead, curTime) + 2.5;
            if (seek) SeekTo(t);
            IMFSample cand = null;
            double candT = 0;
            try
            {
                while (true)
                {
                    if (held == null)
                    {
                        if (ended || !ReadOne()) break;
                    }
                    if (heldTime <= t + 1e-4)
                    {
                        Mf.Release(cand);
                        cand = held;
                        candT = heldTime;
                        held = null;
                        continue;
                    }
                    break;
                }
                if (cand != null)
                {
                    Keep(cand, candT);
                }
                else if ((seek || cur == null) && held != null)
                {
                    // 찾는 시각이 첫 장보다 앞: 첫 장
                    Keep(held, heldTime);
                    Mf.Release(held);
                    held = null;
                }
            }
            finally
            {
                Mf.Release(cand);
            }
            frameTime = curTime;
            return cur;
        }

        // 다 쓴 그림 배열을 다시 쓴다(1080p 한 장이 8MB라 장마다 새로 만들면 메모리 정리가 잦아 끊긴다)
        readonly Stack<byte[]> spare = new Stack<byte[]>();

        byte[] NewBuffer()
        {
            int n = Width * Height * 4;
            while (spare.Count > 0)
            {
                var b = spare.Pop();
                if (b.Length == n) return b;
            }
            return new byte[n];
        }

        void Keep(IMFSample s, double time)
        {
            var px = NewBuffer();
            Convert(s, px);
            if (cur != null && !double.IsNaN(curTime))
            {
                recent.Add(new KeyValuePair<double, byte[]>(curTime, cur));
                if (recent.Count > 6)
                {
                    spare.Push(recent[0].Value);
                    recent.RemoveAt(0);
                }
            }
            cur = px;
            curTime = time;
        }

        void SeekTo(double t)
        {
            Mf.Release(held);
            held = null;
            ended = false;
            foreach (var kv in recent) spare.Push(kv.Value);
            recent.Clear();
            if (cur != null) spare.Push(cur);
            cur = null;
            curTime = double.NaN;
            var at = new MfPropVariant { Type = 20, Value = (long)(Math.Max(0, t - 0.001) * 1e7) };
            reader.SetCurrentPosition(Guid.Empty, ref at);
            lastRead = double.NegativeInfinity;
        }

        bool ReadOne()
        {
            for (int guard = 0; guard < 100000; guard++)
            {
                uint actual, flags;
                long time;
                IMFSample s;
                reader.ReadSample(Mf.FirstVideo, 0, out actual, out flags, out time, out s);
                if ((flags & Mf.FlagTypeChanged) != 0) LoadFormat();
                if ((flags & (Mf.FlagEnd | 1)) != 0)
                {
                    Mf.Release(s);
                    ended = true;
                    return false;
                }
                if (s == null) continue;
                held = s;
                heldTime = time / 1e7;
                lastRead = heldTime;
                return true;
            }
            ended = true;
            return false;
        }

        /// a~b 사이의 장을 모두(복사본으로). 거꾸로 보여 줄 때 덩어리로 쓴다. 다음 At은 새로 찾아간다.
        public List<KeyValuePair<double, byte[]>> Range(double a, double b)
        {
            var list = new List<KeyValuePair<double, byte[]>>();
            SeekTo(a);
            while (true)
            {
                if (held == null)
                {
                    if (ended || !ReadOne()) break;
                }
                if (heldTime > b + 1e-4) break;
                if (heldTime >= a - Step * 1.01)
                {
                    var px = new byte[Width * Height * 4];
                    Convert(held, px);
                    list.Add(new KeyValuePair<double, byte[]>(heldTime, px));
                }
                Mf.Release(held);
                held = null;
            }
            // 덩어리 앞쪽에 장이 없으면(구간이 장 사이) 앞 장 하나를 넣는다
            if (list.Count == 0 && cur != null) list.Add(new KeyValuePair<double, byte[]>(curTime, (byte[])cur.Clone()));
            SeekTo(b);
            return list;
        }

        void Convert(IMFSample sample, byte[] dst)
        {
            IMFMediaBuffer b;
            sample.ConvertToContiguousBuffer(out b);
            try
            {
                IntPtr p;
                uint max, len;
                b.Lock(out p, out max, out len);
                try
                {
                    if (mode == 0) CopyRgb(p, (int)len, dst);
                    else if (mode == 1) FromNv12(p, (int)len, dst);
                    else FromRgbFull(p, (int)len, dst);
                }
                finally
                {
                    b.Unlock();
                }
            }
            finally
            {
                Mf.Release(b);
            }
        }

        static IntPtr At(IntPtr p, long offset)
        {
            return new IntPtr(p.ToInt64() + offset);
        }

        void CopyRgb(IntPtr p, int len, byte[] dst)
        {
            int row = Width * 4;
            int pitch = stride != 0 ? Math.Abs(stride) : (bufH > 0 ? len / bufH : row);
            if (pitch < row) pitch = row;
            bool flip = stride < 0;
            int rows = Math.Min(Height, bufH);
            for (int y = 0; y < rows; y++)
            {
                long at = (long)(flip ? bufH - 1 - y : y) * pitch;
                if (at + row > len) break;
                Marshal.Copy(At(p, at), dst, y * row, row);
            }
            // 그래픽 장치가 알파를 비워 줄 때가 있다
            for (int i = 3; i < dst.Length; i += 4) dst[i] = 255;
        }

        static readonly int[] Ytab = new int[256], Rv601 = new int[256], Gu601 = new int[256], Gv601 = new int[256], Bu601 = new int[256];
        static readonly int[] Rv709 = new int[256], Gu709 = new int[256], Gv709 = new int[256], Bu709 = new int[256];

        static VideoReader()
        {
            for (int i = 0; i < 256; i++)
            {
                Ytab[i] = (int)Math.Round(1.164 * (i - 16) * 1024);
                int c = i - 128;
                Rv601[i] = (int)Math.Round(1.596 * c * 1024);
                Gu601[i] = (int)Math.Round(-0.392 * c * 1024);
                Gv601[i] = (int)Math.Round(-0.813 * c * 1024);
                Bu601[i] = (int)Math.Round(2.017 * c * 1024);
                Rv709[i] = (int)Math.Round(1.793 * c * 1024);
                Gu709[i] = (int)Math.Round(-0.213 * c * 1024);
                Gv709[i] = (int)Math.Round(-0.533 * c * 1024);
                Bu709[i] = (int)Math.Round(2.112 * c * 1024);
            }
        }

        static byte Clip(int v)
        {
            v >>= 10;
            return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
        }

        /// NV12 → BGRA, 보이는 부분만, 필요하면 줄이면서(가까운 칸)
        void FromNv12(IntPtr p, int len, byte[] dst)
        {
            int pitch = stride != 0 ? Math.Abs(stride) : len * 2 / (bufH * 3);
            if (pitch < bufW) pitch = bufW;
            if (rgbScratch == null || rgbScratch.Length < len) rgbScratch = new byte[len];
            Marshal.Copy(p, rgbScratch, 0, len);
            byte[] src = rgbScratch;
            int w = Width, h = Height, cw = CodedW, ch = CodedH, oy = offY;
            int[] rv = bt709 ? Rv709 : Rv601, gu = bt709 ? Gu709 : Gu601, gv = bt709 ? Gv709 : Gv601, bu = bt709 ? Bu709 : Bu601;
            var xs = new int[w];
            for (int x = 0; x < w; x++) xs[x] = offX + Math.Min(cw - 1, (int)((x + 0.5) * cw / w));
            int uvBase = pitch * bufH;
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                int sy = oy + Math.Min(ch - 1, (int)((y + 0.5) * ch / h));
                int yr = sy * pitch;
                int uvr = uvBase + (sy >> 1) * pitch;
                if (uvr + pitch > len || yr + pitch > len) return;
                int d = y * w * 4;
                for (int x = 0; x < w; x++, d += 4)
                {
                    int sx = xs[x];
                    int yy = Ytab[src[yr + sx]];
                    int uo = uvr + (sx & ~1);
                    int u = src[uo], v = src[uo + 1];
                    dst[d] = Clip(yy + bu[u]);
                    dst[d + 1] = Clip(yy + gu[u] + gv[v]);
                    dst[d + 2] = Clip(yy + rv[v]);
                    dst[d + 3] = 255;
                }
            });
        }

        /// 윈도우가 바꿔 준 원본 크기 RGB32 → 보이는 부분만, 필요하면 줄여서
        void FromRgbFull(IntPtr p, int len, byte[] dst)
        {
            int pitch = stride != 0 ? Math.Abs(stride) : bufW * 4;
            bool flip = stride < 0;
            int need = CodedW * CodedH * 4;
            bool same = Width == CodedW && Height == CodedH;
            byte[] into = dst;
            if (!same)
            {
                if (rgbScratch == null || rgbScratch.Length < need) rgbScratch = new byte[need];
                into = rgbScratch;
            }
            int row = CodedW * 4;
            for (int y = 0; y < CodedH; y++)
            {
                long at = (long)(flip ? bufH - 1 - (y + offY) : y + offY) * pitch + offX * 4L;
                if (at + row > len) break;
                Marshal.Copy(At(p, at), into, y * row, row);
            }
            if (!same) VPx.Shrink(into, CodedW, CodedH, dst, Width, Height);
            for (int i = 3; i < dst.Length; i += 4) dst[i] = 255;
        }

        void Close()
        {
            Mf.Release(held);
            held = null;
            Mf.Release(reader);
            reader = null;
        }

        public void Dispose()
        {
            Close();
            cur = null;
            recent.Clear();
            spare.Clear();
        }
    }

    /// 원본 소리를 48kHz 두 줄(16비트)로 풀어 임시 파일에 둔다. 소리 크기 띠(10ms마다)도 함께.
    sealed class AudioTrack
    {
        public const int Rate = 48000;
        public const int EnvStep = 480;
        public string Source;
        public string File;
        public long Frames;
        public volatile bool Ready;
        public volatile bool Failed;
        public volatile float Progress;
        public string Error;
        /// 10ms마다 가장 큰 값(0~1)과 평균 세기(RMS)
        public float[] Peak = new float[0], Rms = new float[0];
        FileStream stream;
        readonly object gate = new object();
        byte[] raw = new byte[0];

        public double Seconds
        {
            get { return Frames / (double)Rate; }
        }

        /// frame0부터 count개 칸을 dst(두 줄 섞어 놓은 float, dstFrame 칸부터)에 gain을 곱해 더한다.
        public void Mix(long frame0, int count, float[] dst, int dstFrame, float gain)
        {
            if (!Ready || count <= 0) return;
            long a = Math.Max(0, frame0), b = Math.Min(Frames, frame0 + count);
            if (b <= a) return;
            int n = (int)(b - a);
            int skip = (int)(a - frame0);
            lock (gate)
            {
                if (stream == null) stream = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                if (raw.Length < n * 4) raw = new byte[n * 4];
                stream.Position = a * 4;
                int got = 0;
                while (got < n * 4)
                {
                    int r = stream.Read(raw, got, n * 4 - got);
                    if (r <= 0) break;
                    got += r;
                }
                int frames = got / 4;
                const float k = 1f / 32768f;
                int o = (dstFrame + skip) * 2;
                for (int i = 0; i < frames; i++)
                {
                    short l = (short)(raw[i * 4] | (raw[i * 4 + 1] << 8));
                    short r = (short)(raw[i * 4 + 2] | (raw[i * 4 + 3] << 8));
                    dst[o++] += l * k * gain;
                    dst[o++] += r * k * gain;
                }
            }
        }

        /// 소리 칸 하나를 바로 읽는다(느린 길: 처리용)
        public float[] ReadAll(long frame0, int count)
        {
            var buf = new float[count * 2];
            Mix(frame0, count, buf, 0, 1f);
            return buf;
        }

        public void Close()
        {
            lock (gate)
            {
                if (stream != null) stream.Dispose();
                stream = null;
            }
        }
    }

    static class AudioCache
    {
        static readonly Dictionary<string, AudioTrack> Tracks = new Dictionary<string, AudioTrack>(StringComparer.OrdinalIgnoreCase);
        static readonly object Gate = new object();

        /// 다시 만들 수 있어서 이 PC 안쪽 캐시 폴더에 둔다(임시 폴더는 정리되지 않고, 로밍 폴더는 로그인이 느려진다). 정리는 VAutoSave.Tidy.
        public static string Folder
        {
            get
            {
                string d = Path.Combine(AppInfo.CacheDir, "vaudio");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        /// 이 프로그램이 지금 쓰는 소리 파일(정리할 때 남긴다)
        public static List<string> InUse()
        {
            lock (Gate) return Tracks.Values.Select(t => t.File).Where(f => f != null).ToList();
        }

        static string KeyOf(string path)
        {
            var fi = new FileInfo(path);
            string s = Path.GetFullPath(path).ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)), 0, 10).Replace("-", "").ToLowerInvariant();
        }

        /// 이 파일의 소리(없으면 뒤에서 풀기 시작). 소리가 없는 파일이면 Failed.
        public static AudioTrack Get(string path)
        {
            lock (Gate)
            {
                AudioTrack t;
                if (Tracks.TryGetValue(path, out t)) return t;
                t = new AudioTrack { Source = path };
                Tracks[path] = t;
                var th = new Thread(delegate () { Decode(t); }) { IsBackground = true, Name = "소리 풀기", Priority = ThreadPriority.BelowNormal };
                th.SetApartmentState(ApartmentState.MTA);
                th.Start();
                return t;
            }
        }

        /// 다 풀릴 때까지 기다린다(시험·내보내기용)
        public static AudioTrack Wait(string path, Func<bool> stop)
        {
            var t = Get(path);
            while (!t.Ready && !t.Failed)
            {
                if (stop != null && stop()) break;
                Thread.Sleep(20);
            }
            return t;
        }

        public static void Forget(string path)
        {
            lock (Gate)
            {
                AudioTrack t;
                if (Tracks.TryGetValue(path, out t))
                {
                    t.Close();
                    Tracks.Remove(path);
                }
            }
        }

        static void Decode(AudioTrack t)
        {
            try
            {
                string key = KeyOf(t.Source);
                string pcm = Path.Combine(Folder, key + ".pcm"), env = Path.Combine(Folder, key + ".env");
                if (File.Exists(pcm) && File.Exists(env) && LoadEnv(t, env))
                {
                    // 쓰는 것은 오래된 것 정리에서 빠지게
                    try
                    {
                        File.SetLastWriteTime(pcm, DateTime.Now);
                        File.SetLastWriteTime(env, DateTime.Now);
                    }
                    catch { }
                    t.File = pcm;
                    t.Frames = new FileInfo(pcm).Length / 4;
                    t.Ready = true;
                    return;
                }
                string tmp = pcm + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
                var peaks = new List<float>();
                var rmss = new List<float>();
                long frames = 0;
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    frames = Pcm48(t.Source, f, peaks, rmss, t);
                }
                if (frames <= 0)
                {
                    try { File.Delete(tmp); } catch { }
                    t.Failed = true;
                    t.Error = "소리 없음";
                    return;
                }
                try { if (File.Exists(pcm)) File.Delete(pcm); File.Move(tmp, pcm); }
                catch { pcm = tmp; }
                t.Peak = peaks.ToArray();
                t.Rms = rmss.ToArray();
                SaveEnv(t, env);
                t.File = pcm;
                t.Frames = frames;
                t.Ready = true;
            }
            catch (Exception ex)
            {
                t.Error = ex.Message;
                t.Failed = true;
                Log.Write("영상 편집: 소리 풀기 실패 " + ex.GetType().Name);
            }
        }

        static bool LoadEnv(AudioTrack t, string env)
        {
            try
            {
                var b = File.ReadAllBytes(env);
                int n = b.Length / 8;
                var p = new float[n];
                var r = new float[n];
                Buffer.BlockCopy(b, 0, p, 0, n * 4);
                Buffer.BlockCopy(b, n * 4, r, 0, n * 4);
                t.Peak = p;
                t.Rms = r;
                return true;
            }
            catch { return false; }
        }

        static void SaveEnv(AudioTrack t, string env)
        {
            try
            {
                int n = t.Peak.Length;
                var b = new byte[n * 8];
                Buffer.BlockCopy(t.Peak, 0, b, 0, n * 4);
                Buffer.BlockCopy(t.Rms, 0, b, n * 4, n * 4);
                File.WriteAllBytes(env, b);
            }
            catch { }
        }

        /// 소리를 48kHz 두 줄 16비트로 풀어 쓴다. 윈도우가 바꿔 주지 않으면 우리가 바꾼다.
        static long Pcm48(string path, Stream outStream, List<float> peaks, List<float> rmss, AudioTrack t)
        {
            IMFSourceReader r;
            try { r = Mf.Reader(path, false); }
            catch (UserFacingException) { return 0; }
            IMFMediaType want = null, got = null;
            try
            {
                IMFMediaType native;
                if (r.GetNativeMediaType(Mf.FirstAudio, 0, out native) < 0) return 0;
                Mf.Release(native);
                r.SetStreamSelection(Mf.AllStreams, false);
                r.SetStreamSelection(Mf.FirstAudio, true);
                want = Mf.MediaType();
                want.SetGUID(Mf.MajorType, Mf.Audio);
                want.SetGUID(Mf.Subtype, Mf.Pcm);
                want.SetUINT32(Mf.SampleRate, AudioTrack.Rate);
                want.SetUINT32(Mf.Channels, 2);
                want.SetUINT32(Mf.BitsPerSample, 16);
                want.SetUINT32(Mf.BlockAlign, 4);
                want.SetUINT32(Mf.BytesPerSecond, AudioTrack.Rate * 4);
                try
                {
                    r.SetCurrentMediaType(Mf.FirstAudio, IntPtr.Zero, want);
                }
                catch
                {
                    Mf.Release(want);
                    want = Mf.MediaType();
                    want.SetGUID(Mf.MajorType, Mf.Audio);
                    want.SetGUID(Mf.Subtype, Mf.Pcm);
                    want.SetUINT32(Mf.BitsPerSample, 16);
                    r.SetCurrentMediaType(Mf.FirstAudio, IntPtr.Zero, want);
                }
                r.GetCurrentMediaType(Mf.FirstAudio, out got);
                uint rate, ch, bits;
                got.GetUINT32(Mf.SampleRate, out rate);
                got.GetUINT32(Mf.Channels, out ch);
                if (got.GetUINT32(Mf.BitsPerSample, out bits) < 0) bits = 16;
                if (bits != 16 || ch == 0 || rate == 0) return 0;
                MfPropVariant dur;
                double total = r.GetPresentationAttribute(Mf.MediaSource, Mf.Duration, out dur) >= 0 ? dur.Value / 1e7 : 0;
                var env = new EnvBuilder(peaks, rmss);
                var res = new Resampler((int)rate, (int)ch);
                long written = 0;
                byte[] outBytes = new byte[0];
                long expectFrame = -1;
                while (true)
                {
                    uint actual, flags;
                    long time;
                    IMFSample s;
                    r.ReadSample(Mf.FirstAudio, 0, out actual, out flags, out time, out s);
                    try
                    {
                        if (s != null)
                        {
                            IMFMediaBuffer b;
                            s.ConvertToContiguousBuffer(out b);
                            short[] pcm;
                            try
                            {
                                IntPtr p;
                                uint max, len;
                                b.Lock(out p, out max, out len);
                                pcm = new short[len / 2];
                                try { Marshal.Copy(p, pcm, 0, pcm.Length); }
                                finally { b.Unlock(); }
                            }
                            finally
                            {
                                Mf.Release(b);
                            }
                            // 앞쪽이 비어 있으면(시작 시각이 0이 아님) 조용한 칸으로 채운다
                            long at = (long)Math.Round(time / 1e7 * AudioTrack.Rate);
                            if (expectFrame < 0 && at > 0 && at < AudioTrack.Rate * 5)
                            {
                                var silence = new short[at * 2];
                                written += Write(outStream, silence, (int)at, ref outBytes, env);
                            }
                            expectFrame = at;
                            short[] st = res.Push(pcm);
                            written += Write(outStream, st, st.Length / 2, ref outBytes, env);
                            if (total > 0) t.Progress = (float)Math.Min(1, time / 1e7 / total);
                        }
                        if ((flags & Mf.FlagEnd) != 0) break;
                    }
                    finally
                    {
                        Mf.Release(s);
                    }
                }
                env.Finish();
                return written;
            }
            finally
            {
                Mf.Release(want);
                Mf.Release(got);
                Mf.Release(r);
            }
        }

        static long Write(Stream s, short[] st, int frames, ref byte[] buf, EnvBuilder env)
        {
            if (frames <= 0) return 0;
            int bytes = frames * 4;
            if (buf.Length < bytes) buf = new byte[bytes];
            Buffer.BlockCopy(st, 0, buf, 0, bytes);
            s.Write(buf, 0, bytes);
            env.Add(st, frames);
            return frames;
        }

        sealed class EnvBuilder
        {
            readonly List<float> peaks, rmss;
            int n;
            float peak;
            double sum;

            public EnvBuilder(List<float> p, List<float> r)
            {
                peaks = p;
                rmss = r;
            }

            public void Add(short[] st, int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    float l = st[i * 2] / 32768f, r = st[i * 2 + 1] / 32768f;
                    float m = Math.Max(Math.Abs(l), Math.Abs(r));
                    if (m > peak) peak = m;
                    sum += (l * l + r * r) * 0.5;
                    if (++n == AudioTrack.EnvStep) Flush();
                }
            }

            void Flush()
            {
                peaks.Add(peak);
                rmss.Add((float)Math.Sqrt(sum / Math.Max(1, n)));
                n = 0;
                peak = 0;
                sum = 0;
            }

            public void Finish()
            {
                if (n > 0) Flush();
            }
        }

        /// 여러 줄·다른 빠르기의 16비트 소리를 48kHz 두 줄로(직선 보간)
        sealed class Resampler
        {
            readonly int rate, ch;
            double pos;
            short lastL, lastR;
            bool started;

            public Resampler(int rate, int ch)
            {
                this.rate = rate;
                this.ch = ch;
            }

            public short[] Push(short[] pcm)
            {
                int frames = pcm.Length / ch;
                if (rate == AudioTrack.Rate)
                {
                    if (ch == 2) return pcm;
                    var o = new short[frames * 2];
                    for (int i = 0; i < frames; i++)
                    {
                        short l, r;
                        Stereo(pcm, i, out l, out r);
                        o[i * 2] = l;
                        o[i * 2 + 1] = r;
                    }
                    return o;
                }
                double step = rate / (double)AudioTrack.Rate;
                var list = new List<short>(frames * 2 * AudioTrack.Rate / rate + 4);
                // pos는 이번 덩어리 앞 칸(-1 = 지난 덩어리의 마지막) 기준
                while (pos < frames - 1 + 1e-9)
                {
                    int i = (int)Math.Floor(pos);
                    double f = pos - i;
                    short l0, r0, l1, r1;
                    if (i < 0)
                    {
                        if (!started) { l0 = r0 = 0; }
                        else { l0 = lastL; r0 = lastR; }
                        Stereo(pcm, 0, out l1, out r1);
                    }
                    else
                    {
                        Stereo(pcm, i, out l0, out r0);
                        Stereo(pcm, Math.Min(frames - 1, i + 1), out l1, out r1);
                    }
                    list.Add((short)Math.Round(l0 + (l1 - l0) * f));
                    list.Add((short)Math.Round(r0 + (r1 - r0) * f));
                    pos += step;
                }
                if (frames > 0)
                {
                    Stereo(pcm, frames - 1, out lastL, out lastR);
                    started = true;
                }
                pos -= frames;
                return list.ToArray();
            }

            void Stereo(short[] pcm, int i, out short l, out short r)
            {
                if (ch == 1)
                {
                    l = r = pcm[i];
                    return;
                }
                if (ch == 2)
                {
                    l = pcm[i * 2];
                    r = pcm[i * 2 + 1];
                    return;
                }
                // 여러 줄: 앞 두 줄 + 가운데를 섞는다
                int a = pcm[i * ch], b = pcm[i * ch + 1], c = ch > 2 ? pcm[i * ch + 2] : 0;
                l = (short)Math.Max(-32768, Math.Min(32767, a + c * 0.7));
                r = (short)Math.Max(-32768, Math.Min(32767, b + c * 0.7));
            }
        }
    }

    /// 시간 막대에 늘어놓을 작은 그림 띠(돌리기 전 모습)
    sealed class Filmstrip
    {
        public string Source;
        public int W, H;
        public double Step;
        public byte[][] Frames = new byte[0][];
        public volatile int Made;
        public volatile bool Done;
        public volatile bool Failed;

        static readonly Dictionary<string, Filmstrip> Cache = new Dictionary<string, Filmstrip>(StringComparer.OrdinalIgnoreCase);
        static readonly object Gate = new object();
        static readonly Queue<Filmstrip> Waiting = new Queue<Filmstrip>();
        static Thread worker;

        public static Filmstrip Get(string path)
        {
            lock (Gate)
            {
                Filmstrip f;
                if (Cache.TryGetValue(path, out f)) return f;
                f = new Filmstrip { Source = path };
                Cache[path] = f;
                Waiting.Enqueue(f);
                if (worker == null)
                {
                    worker = new Thread(Work) { IsBackground = true, Name = "그림 띠", Priority = ThreadPriority.BelowNormal };
                    worker.SetApartmentState(ApartmentState.MTA);
                    worker.Start();
                }
                return f;
            }
        }

        /// t에 가장 가까운 작은 그림(아직 없으면 가까운 것, 하나도 없으면 null)
        public byte[] Near(double t)
        {
            int n = Made;
            if (n <= 0 || Step <= 0) return null;
            int i = (int)Math.Round(t / Step);
            if (i < 0) i = 0;
            if (i >= n) i = n - 1;
            return Frames[i];
        }

        static void Work()
        {
            while (true)
            {
                Filmstrip f;
                lock (Gate)
                {
                    if (Waiting.Count == 0)
                    {
                        worker = null;
                        return;
                    }
                    f = Waiting.Dequeue();
                }
                try
                {
                    f.Make();
                }
                catch (Exception ex)
                {
                    f.Failed = true;
                    Log.Write("영상 편집: 그림 띠 실패 " + ex.GetType().Name);
                }
                f.Done = true;
            }
        }

        void Make()
        {
            using (var r = new VideoReader(Source, 176, 176, true))
            {
                double dur = r.Duration > 0 ? r.Duration : 1;
                W = r.Width;
                H = r.Height;
                Step = Math.Max(0.5, dur / 480);
                int count = Math.Max(1, (int)Math.Ceiling(dur / Step));
                Frames = new byte[count][];
                bool seekEach = dur > 900;
                for (int i = 0; i < count; i++)
                {
                    double t = i * Step, ft;
                    byte[] px;
                    if (seekEach)
                    {
                        // 긴 영상: 그 자리로 넘겨서 한 장씩
                        using (var one = new VideoReader(Source, 176, 176, true)) px = one.At(t, out ft);
                    }
                    else px = r.At(t, out ft);
                    if (px == null) break;
                    Frames[i] = (byte[])px.Clone();
                    Made = i + 1;
                }
            }
        }
    }
}
