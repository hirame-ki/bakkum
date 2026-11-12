// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 긴 영상·큰 영상의 미리 보기용 가벼운 사본. 원본을 작은 영상(긴 쪽 960, 1초에 30장까지, 1초마다 통째 그림)으로
// 한 번 바꿔 두고, 편집 창의 미리 보기만 이 사본을 읽는다. 내보내기는 늘 원본으로 한다.
// 줌·OBS 녹화처럼 통째 그림이 드문 영상, 4K 휴대폰 영상, 1시간 수업 영상에서 넘기기가 빨라진다.
// 사본은 이 PC 안쪽 캐시 폴더(AppInfo.CacheDir\proxy, 로밍 프로필에 실리지 않게)에 원본 내용으로 정한 이름으로 두므로,
// 원본을 옮기거나 이름을 바꿔도 다시 쓴다.
using System;
using System.Collections.Generic;
using System.IO;

namespace Bakkum
{
    static class VProxy
    {
        /// 사본의 긴 쪽(화소)
        public const int LongSide = 960;
        /// 사본으로 그려도 되는 한계(풀 크기의 긴 변). 미리 보기 칸이 1280이면 여유를 둬 1600으로 풀므로 거기까지는 사본,
        /// 더 크게 확대해 볼 때만 원본(사본을 늘리면 흐려 보여서).
        public const int MaxDrawLong = 1600;
        const double MaxFps = 30;

        static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly object Gate = new object();
        static int version;

        /// 사본이 새로 생기거나 지워질 때마다 늘어난다(미리 보기가 열어 둔 영상을 바꿔 열게)
        public static int Version
        {
            get { lock (Gate) return version; }
        }

        public static string Folder
        {
            get
            {
                string d = Path.Combine(AppInfo.CacheDir, "proxy");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string Key(string src)
        {
            var fi = new FileInfo(src);
            return Path.GetFullPath(src) + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        }

        /// 사본 파일 자리(원본 내용으로 정한 이름)
        public static string PathFor(string src)
        {
            return Path.Combine(Folder, CursorLog.Fingerprint(src) + ".mp4");
        }

        /// 이미 만든 사본(없으면 null). 원본마다 한 번만 디스크를 본다.
        public static string Find(string src)
        {
            try
            {
                if (string.IsNullOrEmpty(src) || !File.Exists(src)) return null;
                string k = Key(src);
                lock (Gate)
                {
                    string v;
                    if (Known.TryGetValue(k, out v)) return v.Length > 0 ? v : null;
                }
                string p = PathFor(src);
                string found = File.Exists(p) && new FileInfo(p).Length > 1000 ? p : "";
                if (found.Length > 0)
                {
                    // 쓰는 사본은 오래된 것 정리에서 빠지게
                    try { File.SetLastWriteTimeUtc(found, DateTime.UtcNow); } catch { }
                }
                lock (Gate) Known[k] = found;
                return found.Length > 0 ? found : null;
            }
            catch (Exception ex)
            {
                Log.Write("미리 보기 사본 찾기 실패: " + ex.GetType().Name);
                return null;
            }
        }

        static string Remember(string src, string proxy)
        {
            lock (Gate)
            {
                try { Known[Key(src)] = proxy ?? ""; } catch { }
                version++;
            }
            return proxy;
        }

        /// 사본이 깨졌을 때: 지우고 원본으로 돌아간다
        public static void Drop(string src)
        {
            try
            {
                string p = PathFor(src);
                if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception ex)
            {
                Log.Write("미리 보기 사본 지우기 실패: " + ex.GetType().Name);
            }
            Remember(src, null);
        }

        /// 사본이 도움이 되는 영상인지: 10분 넘게 길거나, 1080p보다 크거나, 1080p 이상을 1초에 50장 넘게 찍었거나
        public static bool Helps(MediaProbe pr)
        {
            if (pr == null || !pr.HasVideo) return false;
            int big = Math.Max(pr.Width, pr.Height);
            return pr.Seconds >= 600 || big > 1920 || (pr.Fps > 50 && big >= 1920);
        }

        /// 대략 걸릴 시간(초)과 사본 크기(바이트). 이 PC 기준이 아니라 어림값.
        public static void Estimate(MediaProbe pr, out double seconds, out long bytes)
        {
            double len = pr == null ? 0 : pr.Seconds;
            double fps = pr == null || pr.Fps <= 0 ? 30 : pr.Fps;
            int big = pr == null ? 1920 : Math.Max(pr.Width, pr.Height);
            // 풀기가 무거울수록(크고 장이 많을수록) 느리다. 1080p 30장은 영상 길이의 3.6배 빠르기로 바꿨다(11-12, i5-1135G7 실측).
            double weight = Math.Max(1, Math.Pow(big / 1920.0, 1.5)) * Math.Max(1, fps / 30.0);
            seconds = len / 3.5 * weight;
            bytes = (long)(len * Bitrate(LongSide, LongSide * 9 / 16, Math.Min(MaxFps, fps)) / 8);
        }

        static uint Bitrate(int w, int h, double fps)
        {
            return (uint)Math.Max(600000, Math.Min(2000000, w * (double)h * fps * 0.08));
        }

        /// 사본을 만든다(오래 걸리므로 뒤 스레드에서). progress는 0~1. 멈추면 OperationCanceledException.
        public static string Make(string src, Action<double> progress, Func<bool> stop)
        {
            try
            {
                return MakeOnce(src, progress, stop);
            }
            catch (HardwareEncoderFailed)
            {
                // 그래픽 인코더가 앞쪽에서 멈추면 VEncoder.CpuOnly가 켜져서 다시 할 때는 CPU 인코더로 만든다(임시 파일 이름도 새로)
                Log.Write("미리 보기 사본: 그래픽 인코더가 멈춰서 CPU 인코더로 다시 만듦");
                return MakeOnce(src, progress, stop);
            }
        }

        static string MakeOnce(string src, Action<double> progress, Func<bool> stop)
        {
            string dst = PathFor(src);
            if (File.Exists(dst) && new FileInfo(dst).Length > 1000) return Remember(src, dst);
            string tmp = Path.Combine(Folder, Path.GetFileNameWithoutExtension(dst) + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp.mp4");
            try
            {
                using (var r = new VideoReader(src, LongSide, LongSide, true))
                {
                    double srcFps = r.Step > 0 ? 1 / r.Step : MaxFps;
                    double fps = srcFps > MaxFps + 1 ? MaxFps : Math.Max(10, Math.Round(srcFps * 1000) / 1000);
                    double dur = r.Duration;
                    if (dur <= 0)
                    {
                        var pr = VMedia.Probe(src);
                        dur = pr != null ? pr.Seconds : 0;
                    }
                    if (dur <= 0) throw new UserFacingException("영상 길이를 알 수 없어 사본을 만들지 못했어요.");
                    int n = Math.Max(1, (int)Math.Ceiling(dur * fps - 1e-6));
                    var enc = new VEncoder(tmp, r.Width, r.Height, fps, Bitrate(r.Width, r.Height, fps), 0, null, 0, dur, stop) { KeyEvery = (uint)Math.Max(1, Math.Round(fps)) };
                    enc.Start();
                    bool ok = false;
                    try
                    {
                        for (int i = 0; i < n; i++)
                        {
                            if (stop()) throw new OperationCanceledException();
                            double ft;
                            var px = r.At(i / fps, out ft);
                            if (px == null) break;
                            enc.AddVideo(px, i, null);
                            if (i % 15 == 0 && progress != null) progress(i / (double)n);
                        }
                        ok = true;
                    }
                    finally
                    {
                        try { enc.Finish(); }
                        catch (Exception) { if (ok) throw; }
                    }
                }
                if (File.Exists(dst)) File.Delete(tmp);
                else File.Move(tmp, dst);
                if (progress != null) progress(1);
                return Remember(src, dst);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
