// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 윈도우 Media Foundation을 직접 부른다. 영상의 길이·크기 읽기, 프레임 꺼내기(GIF), 화면 녹화 저장에 쓴다.
// 근거: Microsoft Learn의 mfobjects.h · mfreadwrite.h 문서. 아래 인터페이스는 C 헤더의 메서드 순서를 그대로 따른다.
// 쓰지 않는 메서드는 U로 시작하는 빈 자리로 두었다. 순서가 곧 약속이라 지우면 안 된다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Bakkum
{
    [StructLayout(LayoutKind.Explicit)]
    struct MfPropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
        [FieldOffset(16)] public IntPtr Reserved;
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void U0(); void U1(); void U2(); void U3();
        [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint value);
        [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
        void U6();
        [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid value);
        void U8(); void U9(); void U10(); void U11();
        [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int size, out int written);
        void U13(); void U14(); void U15(); void U16(); void U17();
        void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, uint value);
        void SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
        void U20();
        void SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
        void U22(); void U23();
        void SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        void U25(); void U26(); void U27(); void U28(); void U29();
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        void U0(); void U1(); void U2(); void U3();
        [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint value);
        [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
        void U6();
        [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid value);
        void U8(); void U9(); void U10(); void U11();
        [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int size, out int written);
        void U13(); void U14(); void U15(); void U16(); void U17();
        void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, uint value);
        void SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
        void U20();
        void SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
        void U22(); void U23(); void U24(); void U25(); void U26(); void U27(); void U28(); void U29();
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        void U0(); void U1(); void U2(); void U3(); void U4(); void U5(); void U6(); void U7(); void U8(); void U9();
        void U10(); void U11(); void U12(); void U13(); void U14(); void U15(); void U16(); void U17(); void U18(); void U19();
        void U20(); void U21(); void U22(); void U23(); void U24(); void U25(); void U26(); void U27(); void U28(); void U29();
        void U30(); void U31();
        void GetSampleTime(out long time);
        void SetSampleTime(long time);
        void U34();
        void SetSampleDuration(long duration);
        void U36(); void U37();
        void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        void AddBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        void Lock(out IntPtr data, out uint max, out uint current);
        void Unlock();
        void U2();
        void SetCurrentLength(uint length);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReader
    {
        void U0();
        void SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
        void GetCurrentMediaType(uint stream, out IMFMediaType type);
        void SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        void SetCurrentPosition([In, MarshalAs(UnmanagedType.LPStruct)] Guid format, [In] ref MfPropVariant position);
        void ReadSample(uint stream, uint control, out uint actual, out uint flags, out long time, out IMFSample sample);
        void U7(); void U8();
        [PreserveSig] int GetPresentationAttribute(uint stream, [In, MarshalAs(UnmanagedType.LPStruct)] Guid attribute, out MfPropVariant value);
    }

    /// 원본 읽기 부품이 끼워 둔 변환 부품(풀기, 영상 처리기)에 닿기
    [ComImport, Guid("7b981cf0-560e-4116-9875-b099895f23d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReaderEx
    {
        // IMFSourceReader의 10개
        void U0(); void U1(); void U2(); void U3(); void U4(); void U5(); void U6(); void U7(); void U8(); void U9();
        // SetNativeMediaType, AddTransformForStream, RemoveAllTransformsForStream
        void U10(); void U11(); void U12();
        [PreserveSig] int GetTransformForStream(uint stream, uint index, out Guid category, [MarshalAs(UnmanagedType.IUnknown)] out object transform);
    }

    /// 영상 처리기 설정. SetRotation(0)은 적힌 돌림대로 돌리지 않게 한다(MF_VIDEO_PROCESSOR_ROTATION의 ROTATION_NONE).
    [ComImport, Guid("a3f675d5-6119-4f7f-a100-1d8b280f0efb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFVideoProcessorControl
    {
        // SetBorderColor, SetSourceRectangle, SetDestinationRectangle, SetMirror
        void U0(); void U1(); void U2(); void U3();
        [PreserveSig] int SetRotation(int rotation);
    }

    /// 변환 부품(영상 처리기 등)의 설정 칸에 닿기
    [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFTransform
    {
        // GetStreamLimits, GetStreamCount, GetStreamIDs, GetInputStreamInfo, GetOutputStreamInfo
        void U0(); void U1(); void U2(); void U3(); void U4();
        [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSinkWriter
    {
        void AddStream(IMFMediaType target, out uint stream);
        void SetInputMediaType(uint stream, IMFMediaType input, IMFAttributes parameters);
        void BeginWriting();
        void WriteSample(uint stream, IMFSample sample);
        void U4(); void U5(); void U6(); void U7();
        void Finish();
        /// 스트림 뒤의 인코더 같은 부품에 닿기(service가 빈 값이면 인코더)
        [PreserveSig] int GetServiceForStream(uint stream, [In] ref Guid service, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    /// 인코더 세부 설정(통째 그림 간격 등)
    [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICodecAPI
    {
        [PreserveSig] int IsSupported([In] ref Guid api);
        [PreserveSig] int IsModifiable([In] ref Guid api);
        void U3(); void U4(); void U5();
        [PreserveSig] int GetValue([In] ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int SetValue([In] ref Guid api, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    static class Mf
    {
        public const uint FirstVideo = 0xFFFFFFFC, FirstAudio = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
        public const uint FlagEnd = 2, FlagTypeChanged = 0x20;

        public static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        public static readonly Guid Audio = new Guid("73647561-0000-0010-8000-00aa00389b71");
        public static readonly Guid Rgb32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        public static readonly Guid H264 = new Guid("34363248-0000-0010-8000-00aa00389b71");
        public static readonly Guid Aac = new Guid("00001610-0000-0010-8000-00aa00389b71");
        public static readonly Guid Pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        public static readonly Guid Mp3 = new Guid("00000055-0000-0010-8000-00aa00389b71");
        public static readonly Guid Flac = new Guid("0000f1ac-0000-0010-8000-00aa00389b71");
        public static readonly Guid Container = new Guid("150ff23f-4abc-478b-ac4f-e1916fba1cca");
        public static readonly Guid ContainerMp4 = new Guid("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
        public static readonly Guid ContainerMp3 = new Guid("e438b912-83f1-4de6-9e3a-9ffbc6dd24d1");
        public static readonly Guid ContainerFlac = new Guid("31344aa3-05a9-42b5-901b-8e9d4257f75e");
        public static readonly Guid ContainerWave = new Guid("64c3453c-0f26-4741-be63-87bdf8bb935b");
        public static readonly Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid PixelAspect = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid Interlace = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid AvgBitrate = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid Stride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid Profile = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static readonly Guid Aperture = new Guid("d7388766-18fe-48c6-a177-ee894867c8c4");
        public static readonly Guid Rotation = new Guid("c380465d-2271-428c-9b83-ecea3b4a85c1");
        public static readonly Guid Channels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid SampleRate = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid BytesPerSecond = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid BlockAlign = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid BitsPerSample = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid EnableProcessing = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        public static readonly Guid HardwareTransforms = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
        public static readonly Guid DisableThrottling = new Guid("18b3d7a4-e8e2-4f1f-aaaf-8ce6ddd2e0b1");
        public static readonly Guid Duration = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

        [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, uint size);
        [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll")] static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint length, out IMFMediaBuffer buffer);
        [DllImport("mfplat.dll")] static extern int MFCreateFile(int access, int open, int flags, [MarshalAs(UnmanagedType.LPWStr)] string path, out IntPtr stream);
        [DllImport("mfreadwrite.dll")] static extern int MFCreateSourceReaderFromByteStream(IntPtr stream, IMFAttributes attributes, out IMFSourceReader reader);
        [DllImport("mfreadwrite.dll")] static extern int MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr stream, IMFAttributes attributes, out IMFSinkWriter writer);

        static readonly object Gate = new object();
        static bool started;

        static void Hr(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        public static void Start()
        {
            lock (Gate)
            {
                if (started) return;
                try
                {
                    Hr(MFStartup(0x00020070, 1));
                }
                catch (DllNotFoundException)
                {
                    throw new UserFacingException("이 윈도우에는 영상·소리 기능이 없어요. N 에디션이면 윈도우 설정의 선택적 기능에서 '미디어 기능 팩'을 설치해 주세요.");
                }
                started = true;
            }
        }

        public static void Release(object com)
        {
            if (com == null) return;
            try { Marshal.ReleaseComObject(com); }
            catch (Exception ex) { Log.Write("미디어 부품 놓기 실패: " + ex.GetType().Name); }
        }

        public static IMFAttributes Attributes(int size)
        {
            Start();
            IMFAttributes a;
            Hr(MFCreateAttributes(out a, (uint)size));
            return a;
        }

        public static IMFMediaType MediaType()
        {
            Start();
            IMFMediaType t;
            Hr(MFCreateMediaType(out t));
            return t;
        }

        public static IMFSample Sample(IMFMediaBuffer buffer, long time, long duration)
        {
            IMFSample s;
            Hr(MFCreateSample(out s));
            s.AddBuffer(buffer);
            s.SetSampleTime(time);
            s.SetSampleDuration(duration);
            return s;
        }

        public static IMFMediaBuffer Buffer(int length)
        {
            IMFMediaBuffer b;
            Hr(MFCreateMemoryBuffer((uint)length, out b));
            return b;
        }

        static readonly Guid CodecApiId = new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        static readonly Guid BPictureCount = new Guid("8d390aac-dc5c-4200-b57f-814d04babab2");
        /// 통째 그림(키프레임) 간격(CODECAPI_AVEncMPVGOPSize)
        public static readonly Guid GopSize = new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1");

        /// 영상 줄의 입력 형식을 정한다. H.264면 B프레임(앞뒤 장을 보고 만드는 장)을 끈다: CPU 인코더는 B프레임을 쓰면
        /// MP4 장 시각표(ctts)에 음수를 '판 0'으로 적거나(규격 밖이라 오래된 재생기에서 장 순서가 엉킬 수 있다)
        /// 들쭉날쭉한 시각을 받으면 화면 전체를 한 장(33ms) 늦게 적는다(2026-11-12 실측). 이 PC의 그래픽 인코더는 원래 B프레임이 없다.
        /// 형식을 정한 뒤에는 CPU 인코더가 이 값을 받지 않아서 형식을 정할 때 같이 넘긴다. 받지 않는 인코더면 설정 없이 다시 한다.
        /// keyEvery(통째 그림 간격)도 같은 까닭으로 같이 넘긴다. NVIDIA 그래픽 인코더와 CPU 인코더는 형식을 정한 뒤에 넣은 간격을
        /// '받았다'고만 하고 지키지 않았다(2026-11-12 RTX 5060 Ti 실측: 300을 넣어도 30장마다, 다시 읽어도 30).
        public static void SetVideoInput(IMFSinkWriter w, uint stream, IMFMediaType input, bool h264, uint keyEvery = 0)
        {
            if (!h264 && keyEvery == 0)
            {
                w.SetInputMediaType(stream, input, null);
                return;
            }
            IMFAttributes p = Attributes(2);
            try
            {
                if (h264) p.SetUINT32(BPictureCount, 0);
                if (keyEvery > 0) p.SetUINT32(GopSize, keyEvery);
                try
                {
                    w.SetInputMediaType(stream, input, p);
                }
                catch (Exception ex)
                {
                    Log.Write("인코더가 B프레임 끄기·통째 그림 간격을 받지 않음 " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                    w.SetInputMediaType(stream, input, null);
                }
            }
            finally
            {
                Release(p);
            }
        }

        /// 쓰기 스트림 뒤 인코더에 값 하나 넣기. 인코더가 받지 않으면 false(그대로 진행).
        public static bool SetCodecValue(IMFSinkWriter w, uint stream, Guid api, uint value)
        {
            object o = null;
            try
            {
                Guid service = Guid.Empty, iid = CodecApiId;
                if (w.GetServiceForStream(stream, ref service, ref iid, out o) < 0 || o == null) return false;
                var codec = o as ICodecAPI;
                if (codec == null || codec.IsSupported(ref api) < 0) return false;
                object v = value;
                return codec.SetValue(ref api, ref v) >= 0;
            }
            catch (Exception ex)
            {
                Log.Write("인코더 설정 실패: " + ex.GetType().Name);
                return false;
            }
            finally
            {
                if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
            }
        }

        public static IMFSinkWriter SinkWriter(string path, IMFAttributes attributes)
        {
            Start();
            IMFSinkWriter w;
            Hr(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out w));
            return w;
        }

        /// 파일 경로를 주소로 해석하지 않게(이름에 #·%가 있어도 되게) 파일을 먼저 열어 넘긴다.
        public static IMFSourceReader Reader(string path, bool convert)
        {
            Start();
            IMFAttributes a = null;
            IntPtr stream = IntPtr.Zero;
            try
            {
                Hr(MFCreateAttributes(out a, 1));
                if (convert) a.SetUINT32(EnableProcessing, 1);
                int hr = MFCreateFile(1, 0, 0, Path.GetFullPath(path), out stream);
                if (hr < 0) throw Unreadable(hr);
                IMFSourceReader r;
                hr = MFCreateSourceReaderFromByteStream(stream, a, out r);
                if (hr < 0) throw Unreadable(hr);
                return r;
            }
            finally
            {
                if (stream != IntPtr.Zero) Marshal.Release(stream);
                Release(a);
            }
        }

        /// 속성을 직접 정해서 연다(그래픽 장치로 풀기 등)
        public static IMFSourceReader Reader(string path, IMFAttributes attributes)
        {
            Start();
            IntPtr stream = IntPtr.Zero;
            try
            {
                int hr = MFCreateFile(1, 0, 0, Path.GetFullPath(path), out stream);
                if (hr < 0) throw Unreadable(hr);
                IMFSourceReader r;
                hr = MFCreateSourceReaderFromByteStream(stream, attributes, out r);
                if (hr < 0) throw Unreadable(hr);
                return r;
            }
            finally
            {
                if (stream != IntPtr.Zero) Marshal.Release(stream);
            }
        }

        public static UserFacingException Unreadable(int hr)
        {
            Log.Write("미디어 파일 열기 실패: 0x" + hr.ToString("X8"));
            if (hr == unchecked((int)0x80070020) || hr == unchecked((int)0x80070005)) return new UserFacingException("파일을 열 수 없어요. 다른 프로그램이 쓰고 있는지 확인해 주세요.");
            return new UserFacingException("이 PC의 윈도우가 이 파일을 읽지 못해요. 필요한 코덱이 없거나 파일이 손상됐을 수 있어요.");
        }
    }

    /// 파일에 무엇이 들었는지
    public sealed class MediaProbe
    {
        public bool HasVideo, HasAudio;
        public int Width, Height, Rotation, AudioRate, Channels;
        public uint FpsN, FpsD;
        public double Seconds;
        public long Bytes;

        public double Fps
        {
            get { return FpsD == 0 ? 0 : (double)FpsN / FpsD; }
        }

        /// 영상과 소리를 합친 초당 비트 수. 모르면 0.
        public long Bitrate
        {
            get { return Seconds <= 0 ? 0 : (long)(Bytes * 8 / Seconds); }
        }

        public static MediaProbe Read(string path)
        {
            return WinRtCall.Mta(() => ReadCore(path));
        }

        static MediaProbe ReadCore(string path)
        {
            var p = new MediaProbe { Bytes = new FileInfo(path).Length };
            IMFSourceReader r = Mf.Reader(path, false);
            try
            {
                for (uint i = 0; i < 32; i++)
                {
                    IMFMediaType t;
                    if (r.GetNativeMediaType(i, 0, out t) < 0) break;
                    try
                    {
                        Guid major;
                        if (t.GetGUID(Mf.MajorType, out major) < 0) continue;
                        uint v;
                        ulong big;
                        if (major == Mf.Video && !p.HasVideo)
                        {
                            p.HasVideo = true;
                            if (t.GetUINT64(Mf.FrameSize, out big) >= 0)
                            {
                                p.Width = (int)(big >> 32);
                                p.Height = (int)(big & 0xFFFFFFFF);
                            }
                            if (t.GetUINT64(Mf.FrameRate, out big) >= 0)
                            {
                                p.FpsN = (uint)(big >> 32);
                                p.FpsD = (uint)(big & 0xFFFFFFFF);
                            }
                            if (t.GetUINT32(Mf.Rotation, out v) >= 0) p.Rotation = (int)v;
                            int w, h;
                            if (Aperture(t, out w, out h))
                            {
                                p.Width = w;
                                p.Height = h;
                            }
                        }
                        else if (major == Mf.Audio && !p.HasAudio)
                        {
                            p.HasAudio = true;
                            if (t.GetUINT32(Mf.SampleRate, out v) >= 0) p.AudioRate = (int)v;
                            if (t.GetUINT32(Mf.Channels, out v) >= 0) p.Channels = (int)v;
                        }
                    }
                    finally
                    {
                        Mf.Release(t);
                    }
                }
                MfPropVariant d;
                if (r.GetPresentationAttribute(Mf.MediaSource, Mf.Duration, out d) >= 0) p.Seconds = d.Value / 1e7;
                if (p.HasVideo) MeasureFps(r, p);
            }
            finally
            {
                Mf.Release(r);
            }
            if (!p.HasVideo && !p.HasAudio) throw new UserFacingException("이 파일에서 영상이나 소리를 찾지 못했어요.");
            return p;
        }

        /// 파일에 적힌 초당 장수는 틀릴 때가 있어(MKV·WebM에서 절반으로 나왔다) 앞쪽 프레임 시각으로 잰다.
        static void MeasureFps(IMFSourceReader r, MediaProbe p)
        {
            r.SetStreamSelection(Mf.AllStreams, false);
            r.SetStreamSelection(Mf.FirstVideo, true);
            var times = new List<long>();
            for (int i = 0; i < 48; i++)
            {
                uint actual, flags;
                long time;
                IMFSample sample;
                r.ReadSample(Mf.FirstVideo, 0, out actual, out flags, out time, out sample);
                bool end = (flags & Mf.FlagEnd) != 0;
                if (sample != null)
                {
                    times.Add(time);
                    Mf.Release(sample);
                }
                if (end) break;
            }
            times.Sort();
            var steps = new List<long>();
            for (int i = 1; i < times.Count; i++)
            {
                if (times[i] > times[i - 1]) steps.Add(times[i] - times[i - 1]);
            }
            if (steps.Count == 0) return;
            steps.Sort();
            double fps = 1e7 / steps[steps.Count / 2];
            if (fps < 1 || fps > 240) return;
            // MPEG 파일은 프레임 몇 장에 한 번만 시각이 적혀 있어 재면 오히려 낮게 나온다. 큰 쪽을 믿는다.
            if (p.Fps > fps * 1.2 && p.Fps <= 120) return;
            foreach (double common in new[] { 24000.0 / 1001, 30000.0 / 1001, 60000.0 / 1001 })
            {
                if (Math.Abs(fps - common) / common < 0.002)
                {
                    p.FpsN = (uint)Math.Round(common * 1001);
                    p.FpsD = 1001;
                    return;
                }
            }
            if (Math.Abs(fps - Math.Round(fps)) / fps < 0.01)
            {
                p.FpsN = (uint)Math.Round(fps);
                p.FpsD = 1;
                return;
            }
            p.FpsN = (uint)Math.Round(fps * 1000);
            p.FpsD = 1000;
        }

        internal static bool Aperture(IMFMediaType t, out int width, out int height)
        {
            int unused;
            return AreaOf(t, out unused, out unused, out width, out height);
        }

        /// 프레임 안에서 실제로 보이는 부분(1080줄 영상은 1088줄로 저장된다)
        internal static bool AreaOf(IMFMediaType t, out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            var area = new byte[16];
            int n;
            if (t.GetBlob(Mf.Aperture, area, 16, out n) < 0 || n < 16) return false;
            x = BitConverter.ToInt16(area, 2);
            y = BitConverter.ToInt16(area, 6);
            width = BitConverter.ToInt32(area, 8);
            height = BitConverter.ToInt32(area, 12);
            return width > 0 && height > 0;
        }
    }

    /// 영상의 프레임을 처음부터 차례로 꺼낸다(BGRA, 위쪽 줄부터).
    sealed class VideoFrames : IDisposable
    {
        IMFSourceReader reader;
        public int Width, Height;
        int fullWidth, fullHeight, stride, offsetX, offsetY;

        public VideoFrames(string path)
        {
            reader = Mf.Reader(path, true);
            reader.SetStreamSelection(Mf.AllStreams, false);
            reader.SetStreamSelection(Mf.FirstVideo, true);
            var want = Mf.MediaType();
            try
            {
                want.SetGUID(Mf.MajorType, Mf.Video);
                want.SetGUID(Mf.Subtype, Mf.Rgb32);
                reader.SetCurrentMediaType(Mf.FirstVideo, IntPtr.Zero, want);
            }
            finally
            {
                Mf.Release(want);
            }
            Load();
        }

        void Load()
        {
            IMFMediaType t;
            reader.GetCurrentMediaType(Mf.FirstVideo, out t);
            try
            {
                ulong size;
                if (t.GetUINT64(Mf.FrameSize, out size) < 0) throw new UserFacingException("영상 크기를 읽지 못했어요.");
                fullWidth = (int)(size >> 32);
                fullHeight = (int)(size & 0xFFFFFFFF);
                uint s;
                stride = t.GetUINT32(Mf.Stride, out s) >= 0 ? (int)s : fullWidth * 4;
                int x, y, w, h;
                if (MediaProbe.AreaOf(t, out x, out y, out w, out h) && x + w <= fullWidth && y + h <= fullHeight)
                {
                    offsetX = x;
                    offsetY = y;
                    Width = w;
                    Height = h;
                }
                else
                {
                    offsetX = offsetY = 0;
                    Width = fullWidth;
                    Height = fullHeight;
                }
            }
            finally
            {
                Mf.Release(t);
            }
        }

        public void Seek(double seconds)
        {
            var at = new MfPropVariant { Type = 20, Value = (long)(seconds * 1e7) };
            reader.SetCurrentPosition(Guid.Empty, ref at);
        }

        /// 다음 프레임을 pixels에 담는다(크기 Width×Height×4). 끝이면 false. 크기가 바뀌면 grow가 새 배열을 준다.
        public bool Next(out double seconds, ref byte[] pixels)
        {
            seconds = 0;
            while (true)
            {
                uint actual, flags;
                long time;
                IMFSample sample;
                reader.ReadSample(Mf.FirstVideo, 0, out actual, out flags, out time, out sample);
                try
                {
                    if ((flags & Mf.FlagEnd) != 0) return false;
                    if ((flags & Mf.FlagTypeChanged) != 0) Load();
                    if (sample == null) continue;
                    seconds = time / 1e7;
                    if (pixels == null || pixels.Length != Width * Height * 4) pixels = new byte[Width * Height * 4];
                    Copy(sample, pixels);
                    return true;
                }
                finally
                {
                    Mf.Release(sample);
                }
            }
        }

        void Copy(IMFSample sample, byte[] pixels)
        {
            IMFMediaBuffer b;
            sample.ConvertToContiguousBuffer(out b);
            try
            {
                IntPtr p;
                uint max, cur;
                b.Lock(out p, out max, out cur);
                try
                {
                    long first = stride >= 0 ? 0 : (fullHeight - 1) * (long)(-stride);
                    int row = Width * 4;
                    long need = first + (stride >= 0 ? (fullHeight - 1) * (long)stride : 0) + row;
                    if (need > cur) throw new InvalidOperationException("프레임이 예상보다 작음");
                    for (int y = 0; y < Height; y++)
                    {
                        long at = first + (y + offsetY) * (long)stride + offsetX * 4L;
                        Marshal.Copy(new IntPtr(p.ToInt64() + at), pixels, y * row, row);
                    }
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

        public void Dispose()
        {
            var r = reader;
            reader = null;
            Mf.Release(r);
        }
    }
}
