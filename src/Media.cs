// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상·소리 바꾸기. 윈도우에 들어 있는 Media Foundation만 쓰고 따로 설치하는 것은 없다.
// 윈도우가 만들 수 있는 것: MP4(H.264+AAC)·MP3·M4A·WAV·FLAC. MOV·MKV는 MP4를 만든 뒤 상자만 바꿔 담는다.
// WebM·OGG는 윈도우에 저장 부품이 없어 만들지 못한다. 읽을 수 있는 형식은 그 PC에 깔린 코덱에 달렸다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WinFoundation = Windows.Foundation;
using WinProps = Windows.Media.MediaProperties;
using WinStorage = Windows.Storage;
using WinTranscode = Windows.Media.Transcoding;

namespace Bakkum
{
    static class MediaJobs
    {
        public const double MaxGifSeconds = 60;

        static bool IsVideo(string f)
        {
            return Formats.KindOf(f) == Kind.Video;
        }

        public static void Run(Job job, List<string> files)
        {
            string id = job.Option.Id;
            switch (id)
            {
                case "mp4":
                case "mov":
                case "mkv":
                    Conversions.EachFile(job, Only(job, files, IsVideo), "바꾸는 중", delegate (string f) { ToVideo(job, f, id); });
                    break;
                case "compress":
                    Conversions.EachFile(job, Only(job, files, IsVideo), "줄이는 중", delegate (string f) { Compress(job, f); });
                    break;
                case "gif":
                    Conversions.EachFile(job, Only(job, files, IsVideo), "GIF로 바꾸는 중", delegate (string f) { Gif(job, f); });
                    break;
                case "trim":
                    Conversions.EachFile(job, files, "자르는 중", delegate (string f) { Trim(job, f); });
                    break;
                case "vexport":
                    VideoExport.Run(job);
                    break;
                case "mp3":
                case "m4a":
                case "wav":
                case "flac":
                    Conversions.EachFile(job, files, "바꾸는 중", delegate (string f) { ToAudio(job, f, id); });
                    break;
                default:
                    throw new UserFacingException("알 수 없는 작업이에요.");
            }
        }

        static List<string> Only(Job job, List<string> files, Func<string, bool> ok)
        {
            var list = files.Where(ok).ToList();
            if (list.Count < files.Count) job.Note("영상이 아닌 파일 " + (files.Count - list.Count) + "개는 건너뛰었어요.");
            return list;
        }

        // ── 시간 ──

        /// 90 · 90.5 · 1:30 · 01:02:03 처럼 적은 시간을 초로. 비었거나 틀리면 false.
        public static bool ParseTime(string text, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Trim().Replace('：', ':').Split(':');
            if (parts.Length > 3) return false;
            double total = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                double v;
                if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out v)) return false;
                if (i > 0 && v >= 60) return false;
                if (i < parts.Length - 1 && parts[i].Contains(".")) return false;
                total = total * 60 + v;
            }
            seconds = total;
            return true;
        }

        public static string TimeText(double seconds)
        {
            int s = (int)Math.Round(seconds);
            return s >= 3600 ? (s / 3600) + ":" + (s / 60 % 60).ToString("00") + ":" + (s % 60).ToString("00") : (s / 60) + ":" + (s % 60).ToString("00");
        }

        static void Range(Job job, MediaProbe p, out double from, out double to)
        {
            from = 0;
            to = p.Seconds;
            double v;
            if (ParseTime(job.Param("start", ""), out v)) from = v;
            if (ParseTime(job.Param("end", ""), out v)) to = Math.Min(v, p.Seconds);
            if (from >= to) throw new UserFacingException("고른 구간이 전체 길이(" + TimeText(p.Seconds) + ") 안에 있어야 해요.");
        }

        // ── 하나씩 ──

        static int Even(double v)
        {
            return Math.Max(2, ((int)Math.Round(v)) & ~1);
        }

        /// maxShort: 짧은 변 상한(0이면 원본 그대로). density: 화소·프레임당 비트 상한. share: 원본 비트 대비 비율.
        static WinProps.MediaEncodingProfile VideoProfile(MediaProbe p, int maxShort, double density, double share, uint audioBits, int maxFps)
        {
            var profile = WinProps.MediaEncodingProfile.CreateMp4(WinProps.VideoEncodingQuality.HD720p);
            double scale = 1;
            int shortSide = Math.Min(p.Width, p.Height);
            if (maxShort > 0 && shortSide > maxShort) scale = (double)maxShort / shortSide;
            int w = Even(p.Width * scale), h = Even(p.Height * scale);
            profile.Video.Width = (uint)w;
            profile.Video.Height = (uint)h;
            double fps = p.Fps > 0 ? p.Fps : 30;
            if (fps > maxFps)
            {
                fps = maxFps;
                profile.Video.FrameRate.Numerator = (uint)maxFps;
                profile.Video.FrameRate.Denominator = 1;
            }
            else if (p.FpsN > 0 && p.FpsD > 0)
            {
                profile.Video.FrameRate.Numerator = p.FpsN;
                profile.Video.FrameRate.Denominator = p.FpsD;
            }
            double bits = Math.Min(w * h * fps * density, 16000000);
            if (p.Bitrate > 0) bits = Math.Min(bits, p.Bitrate * share);
            profile.Video.Bitrate = (uint)Math.Max(bits, 150000);
            if (p.HasAudio)
            {
                profile.Audio.Bitrate = audioBits;
                profile.Audio.SampleRate = p.AudioRate == 44100 || p.AudioRate == 48000 ? (uint)p.AudioRate : 48000;
                profile.Audio.ChannelCount = (uint)(p.Channels == 1 ? 1 : 2);
            }
            else
            {
                profile.Audio = null;
            }
            return profile;
        }

        static WinProps.MediaEncodingProfile KeepProfile(MediaProbe p)
        {
            return VideoProfile(p, 0, 0.14, 1.2, 160000, 120);
        }

        static WinProps.MediaEncodingProfile AudioProfile(string id, MediaProbe p)
        {
            WinProps.MediaEncodingProfile profile;
            switch (id)
            {
                case "mp3":
                    profile = WinProps.MediaEncodingProfile.CreateMp3(WinProps.AudioEncodingQuality.High);
                    KeepRate(profile, p);
                    break;
                case "m4a":
                    profile = WinProps.MediaEncodingProfile.CreateM4a(WinProps.AudioEncodingQuality.High);
                    KeepRate(profile, p);
                    break;
                case "wav":
                    profile = WinProps.MediaEncodingProfile.CreateWav(WinProps.AudioEncodingQuality.High);
                    KeepShape(profile, p);
                    break;
                case "flac":
                    profile = WinProps.MediaEncodingProfile.CreateFlac(WinProps.AudioEncodingQuality.High);
                    KeepShape(profile, p);
                    break;
                default:
                    throw new InvalidOperationException(id);
            }
            profile.Video = null;
            return profile;
        }

        static void KeepRate(WinProps.MediaEncodingProfile profile, MediaProbe p)
        {
            if (p.AudioRate == 44100 || p.AudioRate == 48000) profile.Audio.SampleRate = (uint)p.AudioRate;
        }

        static void KeepShape(WinProps.MediaEncodingProfile profile, MediaProbe p)
        {
            if (p.AudioRate > 0) profile.Audio.SampleRate = (uint)p.AudioRate;
            if (p.Channels > 0) profile.Audio.ChannelCount = (uint)Math.Min(p.Channels, 2);
            // WAV는 초당 바이트 수가 다른 값과 맞아야 받아 준다.
            profile.Audio.Bitrate = profile.Audio.SampleRate * profile.Audio.ChannelCount * profile.Audio.BitsPerSample;
        }

        static void Check(string f, string dst, double seconds)
        {
            var made = MediaProbe.Read(dst);
            if (seconds > 0.5 && Math.Abs(made.Seconds - seconds) > Math.Max(seconds * 0.3, 1.5))
            {
                Log.Write("만든 파일 길이 어긋남: 기대 " + seconds.ToString("0.00") + " 실제 " + made.Seconds.ToString("0.00"));
                throw new UserFacingException("만든 파일의 길이가 맞지 않아 저장하지 않았어요.");
            }
        }

        static void ToVideo(Job job, string f, string target)
        {
            if (Formats.Ext(f) == "." + target)
            {
                job.Note(Util.ShortName(f) + ": 이미 " + target.ToUpperInvariant() + " 파일이에요.");
                return;
            }
            var p = MediaProbe.Read(f);
            if (!p.HasVideo) throw new UserFacingException("영상이 들어 있지 않아요.");
            string dst = Util.OutputFile(f, "." + target);
            if (target == "mkv")
            {
                string temp = Util.TempDir("mkv");
                try
                {
                    string mp4 = Path.Combine(temp, "v.mp4");
                    Transcode(job, f, mp4, () => KeepProfile(p), 0, 0, p.Seconds, "바꾸는 중");
                    Conversions.Produce(job, dst, delegate (string d)
                    {
                        Mkv.Remux(mp4, d, job);
                        Check(f, d, p.Seconds);
                    });
                }
                finally
                {
                    Util.TryDeleteDir(temp);
                }
                return;
            }
            Conversions.Produce(job, dst, delegate (string d)
            {
                Transcode(job, f, d, () => KeepProfile(p), 0, 0, p.Seconds, "바꾸는 중");
                if (target == "mov") IsoFile.MakeMov(d);
                Check(f, d, p.Seconds);
            });
        }

        static void ToAudio(Job job, string f, string target)
        {
            if (Formats.Ext(f) == "." + target)
            {
                job.Note(Util.ShortName(f) + ": 이미 " + target.ToUpperInvariant() + " 파일이에요.");
                return;
            }
            var p = MediaProbe.Read(f);
            if (!p.HasAudio) throw new UserFacingException("이 영상에는 소리가 없어요.");
            Conversions.Produce(job, Util.OutputFile(f, "." + target), delegate (string d)
            {
                Transcode(job, f, d, () => AudioProfile(target, p), 0, 0, p.Seconds, "소리 바꾸는 중", delegate { AudioPipe(job, f, d, target, 0, p.Seconds); });
                Check(f, d, p.Seconds);
            });
        }

        static void Compress(Job job, string f)
        {
            var p = MediaProbe.Read(f);
            if (!p.HasVideo) throw new UserFacingException("영상이 들어 있지 않아요.");
            string level = job.Param("level", "mid");
            int maxShort = level == "soft" ? 1080 : level == "hard" ? 480 : 720;
            double density = level == "soft" ? 0.08 : level == "hard" ? 0.06 : 0.07;
            double share = level == "soft" ? 0.6 : level == "hard" ? 0.22 : 0.38;
            uint audio = level == "hard" ? 96000u : 128000u;
            int fps = level == "soft" ? 60 : 30;
            Conversions.Produce(job, Conversions.Sibling(f, "_줄임", ".mp4"), delegate (string d)
            {
                Transcode(job, f, d, () => VideoProfile(p, maxShort, density, share, audio, fps), 0, 0, p.Seconds, "줄이는 중");
                Check(f, d, p.Seconds);
                long before = new FileInfo(f).Length, after = new FileInfo(d).Length;
                if (after >= before * 0.97) throw new UserFacingException("이미 충분히 작아서 더 줄이지 못했어요. 원본을 그대로 쓰세요.");
                job.Note(Util.ShortName(f) + ": " + Conversions.Size(before) + " → " + Conversions.Size(after));
            });
        }

        static string TrimExt(string f)
        {
            if (IsVideo(f)) return ".mp4";
            string e = Formats.Ext(f);
            return e == ".mp3" || e == ".m4a" || e == ".wav" || e == ".flac" ? e : ".m4a";
        }

        static void Trim(Job job, string f)
        {
            var p = MediaProbe.Read(f);
            double from, to;
            Range(job, p, out from, out to);
            string ext = TrimExt(f);
            Conversions.Produce(job, Conversions.Sibling(f, "_잘라냄", ext), delegate (string d)
            {
                Action pipe = null;
                if (!IsVideo(f)) pipe = delegate { AudioPipe(job, f, d, ext.Substring(1), from, to); };
                Transcode(job, f, d, () => IsVideo(f) ? KeepProfile(p) : AudioProfile(ext.Substring(1), p), from, to, p.Seconds, "자르는 중", pipe);
                Check(f, d, to - from);
            });
        }

        static void Gif(Job job, string f)
        {
            var p = MediaProbe.Read(f);
            if (!p.HasVideo) throw new UserFacingException("영상이 들어 있지 않아요.");
            double from, to;
            Range(job, p, out from, out to);
            if (to - from > MaxGifSeconds)
            {
                to = from + MaxGifSeconds;
                job.Note(Util.ShortName(f) + ": GIF는 " + (int)MaxGifSeconds + "초까지만 만들어요. 앞부분만 담았어요.");
            }
            int width, fps;
            if (!int.TryParse(job.Param("width", "480"), out width) || width < 32) width = 480;
            if (!int.TryParse(job.Param("fps", "12"), out fps) || fps < 1 || fps > 30) fps = 12;
            Conversions.Produce(job, Util.OutputFile(f, ".gif"), delegate (string d) { GifMaker.Write(job, f, d, p, from, to, width, fps); });
        }

        // ── 윈도우 변환기 ──

        /// 변환기가 받지 못한 소리(OGG 등)는 직접 풀어서 저장 부품에 넘긴다.
        static void AudioPipe(Job job, string src, string dst, string target, double from, double to)
        {
            string name = Util.ShortName(src);
            IMFSourceReader reader = null;
            IMFSinkWriter writer = null;
            IMFMediaType want = null, pcm = null, output = null;
            IMFAttributes attributes = null;
            try
            {
                reader = Mf.Reader(src, false);
                reader.SetStreamSelection(Mf.AllStreams, false);
                reader.SetStreamSelection(Mf.FirstAudio, true);
                want = Mf.MediaType();
                want.SetGUID(Mf.MajorType, Mf.Audio);
                want.SetGUID(Mf.Subtype, Mf.Pcm);
                // 비트 수는 지정하지 않는다. OGG·Opus에 16비트를 지정하면 형식을 맞추지 못했다(2026-11-08 실측).
                reader.SetCurrentMediaType(Mf.FirstAudio, IntPtr.Zero, want);
                reader.GetCurrentMediaType(Mf.FirstAudio, out pcm);
                uint rate, channels, bits;
                pcm.GetUINT32(Mf.SampleRate, out rate);
                pcm.GetUINT32(Mf.Channels, out channels);
                if (pcm.GetUINT32(Mf.BitsPerSample, out bits) < 0) bits = 16;
                output = Mf.MediaType();
                output.SetGUID(Mf.MajorType, Mf.Audio);
                output.SetUINT32(Mf.SampleRate, rate);
                output.SetUINT32(Mf.Channels, channels);
                Guid container;
                switch (target)
                {
                    case "mp3":
                        output.SetGUID(Mf.Subtype, Mf.Mp3);
                        // MP3 인코더는 모노에서 192kbps를 받지 않는다(2026-11-08 실측). 모노는 128kbps로.
                        output.SetUINT32(Mf.BytesPerSecond, channels == 1 ? 16000u : 24000u);
                        container = Mf.ContainerMp3;
                        break;
                    case "m4a":
                        output.SetGUID(Mf.Subtype, Mf.Aac);
                        output.SetUINT32(Mf.BitsPerSample, 16);
                        output.SetUINT32(Mf.BytesPerSecond, 24000);
                        container = Mf.ContainerMp4;
                        break;
                    case "flac":
                        output.SetGUID(Mf.Subtype, Mf.Flac);
                        output.SetUINT32(Mf.BitsPerSample, bits);
                        container = Mf.ContainerFlac;
                        break;
                    default:
                        output.SetGUID(Mf.Subtype, Mf.Pcm);
                        output.SetUINT32(Mf.BitsPerSample, bits);
                        output.SetUINT32(Mf.BlockAlign, channels * bits / 8);
                        output.SetUINT32(Mf.BytesPerSecond, rate * channels * bits / 8);
                        container = Mf.ContainerWave;
                        break;
                }
                attributes = Mf.Attributes(1);
                attributes.SetGUID(Mf.Container, container);
                writer = Mf.SinkWriter(Path.GetFullPath(dst), attributes);
                uint stream;
                writer.AddStream(output, out stream);
                writer.SetInputMediaType(stream, pcm, null);
                writer.BeginWriting();
                if (from > 0)
                {
                    var at = new MfPropVariant { Type = 20, Value = (long)(from * 1e7) };
                    reader.SetCurrentPosition(Guid.Empty, ref at);
                }
                long start = (long)(from * 1e7), stop = (long)(to * 1e7);
                bool any = false;
                while (true)
                {
                    job.Check();
                    uint actual, flags;
                    long time;
                    IMFSample sample;
                    reader.ReadSample(Mf.FirstAudio, 0, out actual, out flags, out time, out sample);
                    try
                    {
                        if ((flags & Mf.FlagEnd) != 0) break;
                        if (sample == null || time < start) continue;
                        if (time >= stop) break;
                        sample.SetSampleTime(time - start);
                        writer.WriteSample(stream, sample);
                        any = true;
                        job.Report(name + " 소리 바꾸는 중", (double)(time - start) / Math.Max(1, stop - start));
                    }
                    finally
                    {
                        Mf.Release(sample);
                    }
                }
                if (!any) throw new UserFacingException("이 구간에서 소리를 찾지 못했어요.");
                writer.Finish();
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Log.Write("소리 직접 변환 실패: 0x" + ex.HResult.ToString("X8"));
                throw new UserFacingException("이 PC의 윈도우가 이 파일을 읽지 못해요. 필요한 코덱이 없거나 파일이 손상됐을 수 있어요.");
            }
            finally
            {
                Mf.Release(writer);
                Mf.Release(attributes);
                Mf.Release(output);
                Mf.Release(pcm);
                Mf.Release(want);
                Mf.Release(reader);
            }
        }

        static bool Stopped(Job job)
        {
            try
            {
                job.Check();
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }

        /// 윈도우 변환기는 휴대폰 영상의 촬영 위치를 결과 MP4·MOV·M4A에 그대로 옮겨 적는다(2026-11-12 실측).
        /// 사진의 위치 정보를 지우듯 결과에서 지운다. 못 지워도 변환 결과는 그대로 둔다(기록만).
        static void DropPlace(string dst)
        {
            string e = Formats.Ext(dst);
            if (e != ".mp4" && e != ".mov" && e != ".m4a" && e != ".m4v") return;
            for (int i = 0; ; i++)
            {
                try
                {
                    if (IsoFile.DropPlace(dst)) Log.Write("결과에서 촬영 위치 정보를 지움");
                    return;
                }
                catch (IOException ex)
                {
                    // 변환기가 파일을 막 놓는 중일 수 있다
                    if (i >= 4)
                    {
                        Log.Write("촬영 위치 지우기 실패: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                        return;
                    }
                    System.Threading.Thread.Sleep(150);
                }
                catch (Exception ex)
                {
                    Log.Write("촬영 위치 지우기 실패: " + ex.GetType().Name);
                    return;
                }
            }
        }

        /// 그래픽 부품을 먼저 쓰고, 안 되면 CPU로 한 번 더 해 본다.
        static void Transcode(Job job, string src, string dst, Func<WinProps.MediaEncodingProfile> profile, double from, double to, double total, string verb, Action fallback = null)
        {
            string name = Util.ShortName(src);
            WinRtCall.Mta<object>(delegate ()
            {
                var input = WinRtCall.Await(WinStorage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(src)));
                File.WriteAllBytes(dst, new byte[0]);
                var output = WinRtCall.Await(WinStorage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(dst)));
                Exception failure = null;
                foreach (bool hardware in new[] { true, false })
                {
                    try
                    {
                        var t = new WinTranscode.MediaTranscoder();
                        t.HardwareAccelerationEnabled = hardware;
                        t.AlwaysReencode = true;
                        if (from > 0) t.TrimStartTime = TimeSpan.FromSeconds(from);
                        // 문서에는 '끝에서 잘라낼 길이'라고 나오지만 실제로는 멈출 시각이다(2026-11-08 실측).
                        if (to > 0 && to < total) t.TrimStopTime = TimeSpan.FromSeconds(to);
                        var prepared = WinRtCall.Await(t.PrepareFileTranscodeAsync(input, output, profile()));
                        if (!prepared.CanTranscode)
                        {
                            Log.Write("미디어 변환 준비 실패: " + prepared.FailureReason);
                            if (prepared.FailureReason == WinTranscode.TranscodeFailureReason.CodecNotFound) throw new UserFacingException("이 PC에는 이 파일을 여는 코덱이 없어요.");
                            throw new InvalidOperationException(prepared.FailureReason.ToString());
                        }
                        WinRtCall.Await(prepared.TranscodeAsync(), delegate (double percent) { job.Report(name + " " + verb + " " + (int)percent + "%", percent / 100.0); }, () => Stopped(job));
                        DropPlace(dst);
                        return null;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (UserFacingException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("미디어 변환 실패(그래픽 부품 " + hardware + "): " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                        failure = ex;
                    }
                }
                if (fallback != null)
                {
                    fallback();
                    return null;
                }
                if (failure != null && failure.HResult == unchecked((int)0xC00D36C4)) throw new UserFacingException("이 PC의 윈도우가 이 파일을 읽지 못해요. 필요한 코덱이 없거나 파일이 손상됐을 수 있어요.");
                throw new UserFacingException("바꾸지 못했어요. 이 PC의 윈도우가 이 형식을 처리하지 못하는 것 같아요.");
            });
        }
    }
}
