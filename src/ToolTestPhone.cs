// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 휴대폰 영상 시험: /selftest-tools 시험폴더 phone
// 사람·학생 영상은 쓰지 않는다. 시험이 직접 만든 무늬 영상·삐 소리에 휴대폰이 적어 두는 정보(위치·돌림)를 넣어 본다.
//  1. 위치 정보: 휴대폰처럼 위치(©xyz 칸, 애플 위치 칸)를 넣은 영상을 바꿈의 영상 도구·편집 내보내기에 넣고, 결과에 위치가 남는지.
//  2. 세로 영상: 휴대폰을 세워 찍은 것처럼 90° 돌림을 적고 장 간격이 들쭉날쭉(VFR)한 영상에서, 화면이 바로 서고 소리가 어긋나지 않는지.
//  3. 진짜 HEVC: 이 PC의 HEVC 인코더로 만든 세로 영상을 열고, 편집해 내보내고, 줄이기(H.264로)까지.
// 한컴 수업 자료 시험: /selftest-tools 시험폴더 lecturehancom (한쇼·한글이 있어야 함)
//  지어낸 발표 자료(.pptx, 노트 있음)를 한쇼로 PDF → 쪽 그림 → 노트 읽기(윈도우 목소리) → 영상, 지어낸 한글 문서도 쪽 그림까지.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    static partial class ToolTest
    {
        static string Ph(string name)
        {
            return Path.Combine(P("phonejob"), name);
        }

        /// 시험 위치(서울 시청 앞 광장 근처, 사람과 상관없는 공공장소 좌표)
        const string TestPlace = "+37.5665+126.9780+012.000/";

        /// MF가 적어 주는 돌림 값을 '시계 방향 몇 도'로(세로 영상 시험에서 정한다)
        static Func<int, int> phoneCw = deg => deg;

        static void PhoneTests()
        {
            Cli.Print("[휴대폰 영상]");
            if (string.IsNullOrEmpty(AppInfo.DataDirOverride))
            {
                Check("자료 폴더를 임시로 바꾸고 시험한다", false, "선생님의 진짜 자료 폴더를 건드리지 않으려고 멈췄어요.");
                return;
            }
            string w = P("phonejob");
            if (Directory.Exists(w)) Directory.Delete(w, true);
            Directory.CreateDirectory(w);
            VWaveOut.Silent = true;
            var sw = Stopwatch.StartNew();
            PhoneLocation();
            Cli.Print("    (위치 정보 시험 " + sw.Elapsed.TotalSeconds.ToString("0") + "초)");
            sw.Restart();
            PhoneCpuEncoder();
            PhoneVertical();
            Cli.Print("    (세로 영상 시험 " + sw.Elapsed.TotalSeconds.ToString("0") + "초)");
            sw.Restart();
            PhoneHevc();
            Cli.Print("    (HEVC 시험 " + sw.Elapsed.TotalSeconds.ToString("0") + "초)");
        }

        // ── 시험 영상 만들기 ──

        /// 홀수 초부터 0.1초 동안(흰 화면·삐 소리)
        static bool OddBeat(double t)
        {
            int k = (int)Math.Floor(t + 1e-6);
            return k % 2 == 1 && t >= k - 1e-6 && t < k + 0.1 - 1e-6;
        }

        static List<double> EvenTimes(double seconds, double fps)
        {
            var list = new List<double>();
            int n = (int)Math.Round(seconds * fps);
            for (int i = 0; i < n; i++) list.Add(i / fps);
            return list;
        }

        /// 장마다 시작 시각(휴대폰처럼 들쭉날쭉). 0~4초 초당 30장, 4~8초 15장, 8~12초 30장·20장 간격 번갈아, 12~16초 24장, 16~20초 60장.
        /// 홀수 초는 모두 어느 장의 시작과 맞는다.
        static List<double> PhoneVfrTimes(double seconds)
        {
            var list = new List<double>();
            double t = 0;
            int k = 0;
            while (t < seconds - 1e-6)
            {
                list.Add(t);
                double step;
                if (t < 4 - 1e-6) step = 1 / 30.0;
                else if (t < 8 - 1e-6) step = 1 / 15.0;
                else if (t < 12 - 1e-6) step = k++ % 2 == 0 ? 1 / 30.0 : 1 / 20.0;
                else if (t < 16 - 1e-6) step = 1 / 24.0;
                else step = 1 / 60.0;
                t += step;
            }
            return list;
        }

        /// 어두운 바탕(아래 절반은 장마다 바뀌는 무늬), 움직이는 막대, 저장된 그림의 왼쪽 위에 빨간 네모. 홀수 초 0.1초는 흰 화면.
        static void PaintPhone(byte[] px, int W, int H, double t, Random rnd)
        {
            bool flash = OddBeat(t);
            int cell = 16, cw = W / cell + 1, ch = H / cell + 1;
            var noise = new byte[cw * ch];
            for (int i = 0; i < noise.Length; i++) noise[i] = (byte)rnd.Next(30, 170);
            int bar = (int)(t * 160) % W, barW = Math.Max(8, W / 16);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    byte v;
                    if (flash) v = 235;
                    else if (x >= bar && x < bar + barW) v = 150;
                    else if (y >= H / 2) v = noise[(y / cell) * cw + x / cell];
                    else v = 50;
                    px[o] = v;
                    px[o + 1] = v;
                    px[o + 2] = v;
                    px[o + 3] = 255;
                }
            }
            int m = H / 4, at = H / 24;
            for (int y = at; y < at + m; y++)
            {
                for (int x = at; x < at + m; x++)
                {
                    int o = (y * W + x) * 4;
                    px[o] = 30;
                    px[o + 1] = 30;
                    px[o + 2] = 220;
                }
            }
        }

        /// 시험용 휴대폰 영상(소리: 홀수 초마다 0.1초 1kHz 삐). 장 i에는 times[i] 시각의 그림을 그리고, 파일에는 장 번호 ÷ 30초로 적는다
        /// (들쭉날쭉한 영상은 그 뒤 MakeVfr로 시각표를 고친다). 소리 길이는 seconds. 만들지 못하면 그 까닭을, 만들면 null.
        static string MakePhoneVideo(string path, int W, int H, List<double> times, double seconds, Guid codec, out bool hardware)
        {
            return MakePhoneVideo(path, W, H, times, seconds, codec, false, out hardware);
        }

        static string MakePhoneVideo(string path, int W, int H, List<double> times, double seconds, Guid codec, bool cpu, out bool hardware)
        {
            hardware = false;
            string wav = path + ".wav";
            WriteWav(wav, seconds, t => OddBeat(t) ? 0.5 * Math.Sin(2 * Math.PI * 1000 * t) : 0);
            var snd = new VProject { Duck = false, EndFade = false };
            snd.Clips.Add(new VClip { Kind = "color", Dur = seconds });
            snd.Sounds.Add(new VSound { Kind = "file", Path = wav, Start = 0, In = 0, Out = seconds, Volume = 1 });
            var enc = new VEncoder(path, W, H, 30, (uint)Math.Min(8000000, W * H * 8), 24000, snd, 0, seconds, () => false)
            {
                Codec = codec,
                NoHardware = cpu
            };
            string err = null;
            try
            {
                enc.Start();
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
            }
            if (err == null)
            {
                var rnd = new Random(3);
                var free = new Queue<byte[]>();
                try
                {
                    for (int i = 0; i < times.Count; i++)
                    {
                        byte[] px;
                        lock (free) px = free.Count > 0 ? free.Dequeue() : new byte[W * H * 4];
                        PaintPhone(px, W, H, times[i], rnd);
                        enc.AddVideo(px, i, delegate (byte[] done) { lock (free) free.Enqueue(done); });
                    }
                }
                catch (Exception ex)
                {
                    err = ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    try
                    {
                        enc.Finish();
                    }
                    catch (Exception ex)
                    {
                        if (err == null) err = ex.GetType().Name + ": " + ex.Message;
                    }
                }
                hardware = enc.Hardware;
            }
            enc.Dispose();
            AudioCache.Forget(wav);
            try { File.Delete(wav); }
            catch { }
            if (err == null && (!File.Exists(path) || new FileInfo(path).Length < 1000)) err = "파일이 비었어요";
            return err;
        }

        // ── MP4 상자 ──

        sealed class IsoBox
        {
            public string Type;
            public int At, Size, Head;

            public int Body
            {
                get { return At + Head; }
            }

            public int End
            {
                get { return At + Size; }
            }
        }

        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        static uint Be32(byte[] d, int o)
        {
            return (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
        }

        static void PutBe32(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v >> 24);
            d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8);
            d[o + 3] = (byte)v;
        }

        static byte[] Be32Bytes(uint v)
        {
            var b = new byte[4];
            PutBe32(b, 0, v);
            return b;
        }

        static List<IsoBox> IsoKids(byte[] d, int from, int to)
        {
            var list = new List<IsoBox>();
            int pos = from;
            while (pos + 8 <= to)
            {
                long size = Be32(d, pos);
                int head = 8;
                if (size == 1)
                {
                    size = (long)Be32(d, pos + 8) << 32 | Be32(d, pos + 12);
                    head = 16;
                }
                else if (size == 0) size = to - pos;
                if (size < head || pos + size > to) break;
                list.Add(new IsoBox { Type = Latin1.GetString(d, pos + 4, 4), At = pos, Size = (int)size, Head = head });
                pos += (int)size;
            }
            return list;
        }

        static IsoBox IsoChild(byte[] d, IsoBox parent, string type)
        {
            return parent == null ? null : IsoKids(d, parent.Body, parent.End).FirstOrDefault(b => b.Type == type);
        }

        static IsoBox IsoPath(byte[] d, IsoBox from, params string[] types)
        {
            var b = from;
            foreach (var t in types) b = IsoChild(d, b, t);
            return b;
        }

        static IsoBox Moov(byte[] d)
        {
            return IsoKids(d, 0, d.Length).FirstOrDefault(b => b.Type == "moov");
        }

        /// 영상 줄(trak). hdlr의 종류가 vide인 것.
        static IsoBox VideoTrak(byte[] d)
        {
            var moov = Moov(d);
            if (moov == null) return null;
            foreach (var trak in IsoKids(d, moov.Body, moov.End).Where(b => b.Type == "trak"))
            {
                var hdlr = IsoPath(d, trak, "mdia", "hdlr");
                if (hdlr != null && Latin1.GetString(d, hdlr.Body + 8, 4) == "vide") return trak;
            }
            return null;
        }

        static byte[] IsoMake(byte[] type, params byte[][] parts)
        {
            int n = 8 + parts.Sum(p => p.Length);
            var b = new byte[n];
            PutBe32(b, 0, (uint)n);
            Buffer.BlockCopy(type, 0, b, 4, 4);
            int o = 8;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, b, o, p.Length);
                o += p.Length;
            }
            return b;
        }

        static byte[] IsoMake(string type, params byte[][] parts)
        {
            return IsoMake(Latin1.GetBytes(type), parts);
        }

        /// 휴대폰처럼 영상에 위치를 적는다: moov/udta/©xyz(안드로이드·갤럭시) + moov/meta 애플 위치 칸(아이폰).
        static void AddPlace(string path)
        {
            var d = File.ReadAllBytes(path);
            var top = IsoKids(d, 0, d.Length);
            var moov = top.First(b => b.Type == "moov");
            if (moov.Head != 8) throw new InvalidOperationException("moov 머리가 16바이트");
            var mdat = top.FirstOrDefault(b => b.Type == "mdat");
            var place = Encoding.ASCII.GetBytes(TestPlace);
            var xyz = IsoMake(new byte[] { 0xA9, (byte)'x', (byte)'y', (byte)'z' }, new byte[] { 0, (byte)place.Length, 0x15, 0xC7 }, place);
            const string Key = "com.apple.quicktime.location.ISO6709";
            var hdlr = IsoMake("hdlr", Be32Bytes(0), Be32Bytes(0), Encoding.ASCII.GetBytes("mdta"), new byte[13]);
            var keys = IsoMake("keys", Be32Bytes(0), Be32Bytes(1), Be32Bytes((uint)(8 + Key.Length)), Encoding.ASCII.GetBytes("mdta"), Encoding.ASCII.GetBytes(Key));
            var ilst = IsoMake("ilst", IsoMake(Be32Bytes(1), IsoMake("data", Be32Bytes(1), Be32Bytes(0), place)));
            var add = IsoMake("udta", xyz).Concat(IsoMake("meta", hdlr, keys, ilst)).ToArray();
            var nm = new byte[moov.Size + add.Length];
            Buffer.BlockCopy(d, moov.At, nm, 0, moov.Size);
            Buffer.BlockCopy(add, 0, nm, moov.Size, add.Length);
            PutBe32(nm, 0, (uint)nm.Length);
            // moov가 mdat 앞에 있으면 덩어리 위치(stco·co64)를 늘어난 만큼 민다
            if (mdat != null && moov.At < mdat.At)
            {
                var root = new IsoBox { Type = "moov", At = 0, Size = nm.Length, Head = 8 };
                foreach (var trak in IsoKids(nm, root.Body, root.End).Where(b => b.Type == "trak"))
                {
                    var stbl = IsoPath(nm, trak, "mdia", "minf", "stbl");
                    if (stbl == null) continue;
                    foreach (var b in IsoKids(nm, stbl.Body, stbl.End))
                    {
                        int n = b.Type == "stco" || b.Type == "co64" ? (int)Be32(nm, b.Body + 4) : 0;
                        for (int i = 0; i < n; i++)
                        {
                            if (b.Type == "stco")
                            {
                                int o = b.Body + 8 + i * 4;
                                PutBe32(nm, o, Be32(nm, o) + (uint)add.Length);
                            }
                            else
                            {
                                int o = b.Body + 8 + i * 8;
                                ulong v = ((ulong)Be32(nm, o) << 32 | Be32(nm, o + 4)) + (ulong)add.Length;
                                PutBe32(nm, o, (uint)(v >> 32));
                                PutBe32(nm, o + 4, (uint)v);
                            }
                        }
                    }
                }
            }
            using (var fs = File.Create(path))
            {
                fs.Write(d, 0, moov.At);
                fs.Write(nm, 0, nm.Length);
                fs.Write(d, moov.End, d.Length - moov.End);
            }
        }

        static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        /// 파일 어디에든 시험 위치가 적혀 있으면 참
        static bool HasPlace(string file)
        {
            var d = File.ReadAllBytes(file);
            return IndexOf(d, Encoding.ASCII.GetBytes("+37.5665")) >= 0 || IndexOf(d, Encoding.ASCII.GetBytes("ISO6709")) >= 0;
        }

        /// 영상 줄의 그림을 시계 방향 90°로 보이게 적는다(휴대폰을 세워 찍은 영상과 같은 행렬). 저장된 그림은 그대로.
        static void Turn90(string path)
        {
            var d = File.ReadAllBytes(path);
            var trak = VideoTrak(d);
            var tkhd = IsoChild(d, trak, "tkhd");
            int m = tkhd.At + (d[tkhd.Body] == 1 ? 60 : 48);
            uint height = Be32(d, m + 40);
            uint[] mat = { 0, 0x00010000, 0, 0xFFFF0000, 0, 0, height, 0, 0x40000000 };
            for (int i = 0; i < 9; i++) PutBe32(d, m + i * 4, mat[i]);
            File.WriteAllBytes(path, d);
        }

        /// 풀어 보면 나오는 장 사이 간격들(ms, 0.1ms로 반올림한 종류). MF의 MP4 저장은 장 시각을 고르게 적고
        /// 실제 보이는 시각은 ctts에 따로 적어서, 파일 상자보다 풀린 시각으로 본다.
        static List<double> ShownGaps(string file, double seconds)
        {
            var times = new List<double>();
            using (var r = new VideoReader(file, 320, 320, false))
            {
                double last = double.NaN;
                for (double t = 0; t < seconds; t += 1 / 120.0)
                {
                    double ft;
                    if (r.At(t, out ft) == null) continue;
                    if (!double.IsNaN(last) && Math.Abs(ft - last) < 1e-6) continue;
                    last = ft;
                    times.Add(ft);
                }
            }
            var gaps = new List<double>();
            for (int i = 1; i < times.Count; i++) gaps.Add(Math.Round((times[i] - times[i - 1]) * 1000, 1));
            return gaps.Distinct().OrderBy(x => x).ToList();
        }

        // ── 그림·소리 재기 ──

        /// 빨간 네모의 가운데(0~1)
        static bool RedSpot(byte[] bgra, int w, int h, out double nx, out double ny)
        {
            long sx = 0, sy = 0, n = 0;
            for (int y = 0; y < h; y += 2)
            {
                for (int x = 0; x < w; x += 2)
                {
                    int o = (y * w + x) * 4;
                    if (bgra[o + 2] > 150 && bgra[o + 1] < 100 && bgra[o] < 100)
                    {
                        sx += x;
                        sy += y;
                        n++;
                    }
                }
            }
            nx = ny = 0;
            if (n < 12) return false;
            nx = (sx / (double)n + 0.5) / w;
            ny = (sy / (double)n + 0.5) / h;
            return true;
        }

        /// 저장된 그림의 점(0~1)을 시계 방향으로 deg만큼 돌려 보이는 자리로
        static void TurnCw(int deg, double x, double y, out double dx, out double dy)
        {
            switch ((deg % 360 + 360) % 360)
            {
                case 90:
                    dx = 1 - y;
                    dy = x;
                    break;
                case 180:
                    dx = 1 - x;
                    dy = 1 - y;
                    break;
                case 270:
                    dx = y;
                    dy = 1 - x;
                    break;
                default:
                    dx = x;
                    dy = y;
                    break;
            }
        }

        /// 재생기가 보여 줄 모습(적힌 돌림 반영)으로 세로로 바로 섰는지: 세로가 더 길고 빨간 네모가 오른쪽 위
        static string Upright(string file, double t, Func<int, int> cwOf, out bool ok)
        {
            ok = false;
            var pr = Probe(file);
            if (pr == null) return "읽기 실패";
            byte[] px;
            int w, h;
            try
            {
                using (var r = new VideoReader(file, 640, 640, false))
                {
                    double ft;
                    px = r.At(t, out ft);
                    w = r.Width;
                    h = r.Height;
                }
            }
            catch (Exception ex)
            {
                return "풀기 실패: " + ex.Message;
            }
            double nx, ny;
            if (px == null) return "그림 없음";
            if (!RedSpot(px, w, h, out nx, out ny)) return "빨간 네모 없음 · 적힌 돌림 " + pr.Rotation + "° · " + w + "×" + h;
            int cw = cwOf(pr.Rotation);
            double dx, dy;
            TurnCw(cw, nx, ny, out dx, out dy);
            bool side = cw % 180 != 0;
            int dw = side ? h : w, dh = side ? w : h;
            ok = dh > dw && dx > 0.6 && dy < 0.35;
            return "적힌 돌림 " + pr.Rotation + "° · 보이는 모습 " + dw + "×" + dh + " · 빨간 네모 (" + dx.ToString("0.00") + ", " + dy.ToString("0.00") + ")";
        }

        static double MeanLevel(byte[] bgra)
        {
            long s = 0, n = 0;
            for (int o = 0; o + 3 < bgra.Length; o += 4 * 7)
            {
                s += bgra[o] + bgra[o + 1] + bgra[o + 2];
                n += 3;
            }
            return n == 0 ? 0 : s / (double)n;
        }

        static string Ms(double sec)
        {
            return double.IsNaN(sec) ? "?" : Math.Round(sec * 1000).ToString("0") + "ms";
        }

        /// 홀수 초마다 (흰 화면이 시작한 시각 − 삐 소리가 시작한 시각). 못 찾은 칸은 NaN.
        static List<double> SyncGaps(string file, double seconds, out string detail)
        {
            var gaps = new List<double>();
            var bright = new List<KeyValuePair<double, double>>();
            try
            {
                using (var r = new VideoReader(file, 320, 320, false))
                {
                    double last = double.NaN;
                    for (double t = 0; t < seconds; t += 1 / 120.0)
                    {
                        double ft;
                        var px = r.At(t, out ft);
                        if (px == null) continue;
                        if (!double.IsNaN(last) && Math.Abs(ft - last) < 1e-6) continue;
                        last = ft;
                        bright.Add(new KeyValuePair<double, double>(ft, MeanLevel(px)));
                    }
                }
            }
            catch (Exception ex)
            {
                detail = "화면 풀기 실패: " + ex.Message;
                return gaps;
            }
            var track = AudioCache.Wait(file, () => false);
            if (track == null || !track.Ready)
            {
                detail = "소리 풀기 실패";
                return gaps;
            }
            var pcm = track.ReadAll(0, (int)track.Frames);
            var parts = new List<string>();
            for (int k = 1; k < seconds - 0.15; k += 2)
            {
                double v = double.NaN, a = double.NaN;
                foreach (var b in bright)
                {
                    if (b.Key > k - 0.2 && b.Key < k + 0.3 && b.Value > 160)
                    {
                        v = b.Key;
                        break;
                    }
                }
                int from = Math.Max(0, (int)((k - 0.25) * AudioTrack.Rate)), to = (int)Math.Min(track.Frames, (long)((k + 0.35) * AudioTrack.Rate));
                for (int i = from; i < to; i++)
                {
                    if (Math.Abs(pcm[i * 2]) > 0.1f)
                    {
                        a = i / (double)AudioTrack.Rate;
                        break;
                    }
                }
                gaps.Add(v - a);
                parts.Add(k + "초 " + (double.IsNaN(v) ? "흰 화면 못 찾음" : double.IsNaN(a) ? "삐 소리 못 찾음" : Ms(v - a)));
            }
            AudioCache.Forget(file);
            detail = string.Join(", ", parts);
            return gaps;
        }

        /// 화면과 소리의 차이가 뒤로 갈수록 벌어지지 않고, 원본보다 크게 늘지 않는지
        static void SyncCheck(string name, string file, double seconds, List<double> baseGaps)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                Check(name + ": 화면과 소리가 어긋나지 않는다", false, "결과 없음");
                return;
            }
            string d;
            var g = SyncGaps(file, seconds, out d);
            bool all = g.Count > 0 && g.All(x => !double.IsNaN(x));
            double spread = all ? g.Max() - g.Min() : double.NaN;
            double worst = all ? g.Max(x => Math.Abs(x)) : double.NaN;
            bool withBase = all && baseGaps != null && baseGaps.Count == g.Count && baseGaps.All(x => !double.IsNaN(x));
            double added = withBase ? g.Zip(baseGaps, (x, y) => Math.Abs(x - y)).Max() : double.NaN;
            bool ok = all && spread <= 0.045 && worst <= 0.1 && (!withBase || added <= 0.05);
            Check(name + ": 화면과 소리가 어긋나지 않는다(뒤로 갈수록 벌어지지 않음)", ok, "화면−소리 " + d + " · 벌어짐 " + Ms(spread) + (withBase ? " · 원본보다 최대 " + Ms(added) : ""));
        }

        static string Problems(Job j)
        {
            return j.Problems.Count == 0 ? "" : " · 문제: " + string.Join("; ", j.Problems);
        }

        // ── 1. 위치 정보 ──

        static void PhoneLocation()
        {
            string src = Ph("위치.mp4");
            bool hw;
            string err = MakePhoneVideo(src, 640, 360, EvenTimes(4, 30), 4, Mf.H264, out hw);
            if (err != null)
            {
                Check("위치 정보: 시험 영상 만들기", false, err);
                return;
            }
            AddPlace(src);
            var pr = Probe(src);
            Check("위치 정보: 휴대폰처럼 위치를 적은 영상이 그대로 열린다", HasPlace(src) && pr != null && pr.HasVideo && pr.HasAudio && Near(pr.Seconds, 4, 0.2), Say(pr));
            string mov = Ph("위치_아이폰.mov");
            File.Copy(src, mov);
            var outs = new List<KeyValuePair<string, Job>>
            {
                new KeyValuePair<string, Job>("MP4로(아이폰 .mov에서)", RunJob("media", "mp4", new[] { mov }, null)),
                new KeyValuePair<string, Job>("MOV로", RunJob("media", "mov", new[] { src }, null)),
                new KeyValuePair<string, Job>("MKV로", RunJob("media", "mkv", new[] { src }, null)),
                new KeyValuePair<string, Job>("줄이기", RunJob("media", "compress", new[] { src }, null)),
                new KeyValuePair<string, Job>("자르기", RunJob("media", "trim", new[] { src }, null, "start=0.5", "end=3.5")),
                new KeyValuePair<string, Job>("소리만 MP3로", RunJob("media", "mp3", new[] { src }, null)),
                new KeyValuePair<string, Job>("소리만 M4A로", RunJob("media", "m4a", new[] { src }, null)),
                new KeyValuePair<string, Job>("소리만 WAV로", RunJob("media", "wav", new[] { src }, null)),
                new KeyValuePair<string, Job>("GIF로", RunJob("media", "gif", new[] { src }, null, "start=0", "end=2"))
            };
            var p = new VProject { Aspect = "16:9", SourceW = 640, SourceH = 360, Fps = 30, Duck = false, EndFade = false };
            p.Clips.Add(new VClip { Path = src, In = 0, Out = 4 });
            outs.Add(new KeyValuePair<string, Job>("영상 편집 내보내기", ExportNow(p, PlanFor(Ph("위치_편집.mp4"), 640, 360, 0, 4), null)));
            foreach (var kv in outs)
            {
                string o = kv.Key == "영상 편집 내보내기" ? (File.Exists(Ph("위치_편집.mp4")) ? Ph("위치_편집.mp4") : "") : Out1(kv.Value);
                bool made = o != "" && File.Exists(o);
                Check("위치 정보: " + kv.Key + " 결과에는 위치가 남지 않는다", made && !HasPlace(o), (made ? Path.GetFileName(o) + " " + Conversions.Size(new FileInfo(o).Length) : "결과 없음") + Problems(kv.Value));
            }
        }

        /// 그래픽 인코더가 없는 PC에서 쓰는 CPU 인코더로 저장해도 화면이 소리보다 늦지 않은지
        static void PhoneCpuEncoder()
        {
            string src = Ph("CPU인코더.mp4");
            bool hw;
            string err = MakePhoneVideo(src, 640, 360, EvenTimes(4, 30), 4, Mf.H264, true, out hw);
            if (err != null)
            {
                Check("CPU 인코더: 시험 영상 만들기", false, err);
                return;
            }
            var d = File.ReadAllBytes(src);
            bool bframes = IsoPath(d, VideoTrak(d), "mdia", "minf", "stbl", "ctts") != null;
            string sd;
            var g = SyncGaps(src, 4, out sd);
            bool ok = !hw && !bframes && g.Count == 2 && g.All(x => !double.IsNaN(x) && Math.Abs(x) <= 0.005);
            Check("CPU 인코더(그래픽 인코더가 없는 PC): B프레임 없이 저장해 화면이 소리보다 늦지 않다", ok,
                (hw ? "그래픽 인코더로 저장됨 · " : "") + (bframes ? "B프레임 있음(ctts 판 " + d[IsoPath(d, VideoTrak(d), "mdia", "minf", "stbl", "ctts").Body] + ")" : "B프레임 없음") + " · 화면−소리 " + sd);
        }

        /// target 상자를 replacement로 바꾸고, 그 위 상자들(ancestors)의 길이를 늘어난 만큼 고친다.
        static byte[] Splice(byte[] d, IsoBox target, byte[] replacement, params IsoBox[] ancestors)
        {
            int delta = replacement.Length - target.Size;
            var o = new byte[d.Length + delta];
            Buffer.BlockCopy(d, 0, o, 0, target.At);
            Buffer.BlockCopy(replacement, 0, o, target.At, replacement.Length);
            Buffer.BlockCopy(d, target.End, o, target.At + replacement.Length, d.Length - target.End);
            foreach (var a in ancestors)
            {
                if (a.Head != 8) throw new InvalidOperationException(a.Type + " 머리가 16바이트");
                PutBe32(o, a.At, (uint)(a.Size + delta));
            }
            return o;
        }

        /// 판 0(32비트)·판 1(64비트) 상자의 길이 칸. offset0·offset1은 판마다 상자 몸에서 떨어진 자리.
        static void PutDuration(byte[] d, IsoBox b, int offset0, int offset1, double seconds, uint scale)
        {
            ulong v = (ulong)Math.Round(seconds * scale);
            if (d[b.Body] == 1)
            {
                PutBe32(d, b.Body + offset1, (uint)(v >> 32));
                PutBe32(d, b.Body + offset1 + 4, (uint)v);
            }
            else PutBe32(d, b.Body + offset0, (uint)v);
        }

        static double GetDuration(byte[] d, IsoBox b, int offset0, int offset1, uint scale)
        {
            if (d[b.Body] == 1) return ((ulong)Be32(d, b.Body + offset1) << 32 | Be32(d, b.Body + offset1 + 4)) / (double)scale;
            return Be32(d, b.Body + offset0) / (double)scale;
        }

        /// 고른 간격으로 만든 영상(B프레임 없음)의 장 시각표(stts)를 times대로 고쳐 써서 휴대폰의 들쭉날쭉한 영상과 같은 모양으로 만든다.
        /// MF의 저장 부품은 들어온 장 시각을 초당 장수에 맞춰 고르게 바꿔 버려서(2026-11-12 실측) 이렇게 만든다. 못 하면 까닭을 돌려준다.
        static string MakeVfr(string path, List<double> times, double seconds)
        {
            var d = File.ReadAllBytes(path);
            var top = IsoKids(d, 0, d.Length);
            var moov = top.First(b => b.Type == "moov");
            var mdat = top.FirstOrDefault(b => b.Type == "mdat");
            if (mdat == null || moov.At < mdat.At) return "moov가 mdat 앞에 있음";
            var trak = VideoTrak(d);
            var mdia = IsoChild(d, trak, "mdia");
            var minf = IsoChild(d, mdia, "minf");
            var stbl = IsoChild(d, minf, "stbl");
            if (IsoChild(d, stbl, "ctts") != null) return "B프레임이 있어 시각표만 고칠 수 없음";
            if (IsoChild(d, trak, "edts") != null) return "편집 목록이 있음";
            var stsz = IsoChild(d, stbl, "stsz");
            int count = (int)Be32(d, stsz.Body + 8);
            if (count != times.Count) return "장 수가 다름(" + count + "/" + times.Count + ")";
            var mdhd = IsoChild(d, mdia, "mdhd");
            uint scale = Be32(d, mdhd.Body + (d[mdhd.Body] == 1 ? 20 : 12));
            var runs = new List<uint[]>();
            for (int i = 0; i < times.Count; i++)
            {
                double next = i + 1 < times.Count ? times[i + 1] : seconds;
                uint delta = (uint)(Math.Round(next * scale) - Math.Round(times[i] * scale));
                if (runs.Count > 0 && runs[runs.Count - 1][1] == delta) runs[runs.Count - 1][0]++;
                else runs.Add(new[] { 1u, delta });
            }
            var parts = new List<byte[]> { Be32Bytes(0), Be32Bytes((uint)runs.Count) };
            foreach (var r in runs)
            {
                parts.Add(Be32Bytes(r[0]));
                parts.Add(Be32Bytes(r[1]));
            }
            d = Splice(d, IsoChild(d, stbl, "stts"), IsoMake("stts", parts.ToArray()), moov, trak, mdia, minf, stbl);
            // 길이 칸: 영상 줄(mdhd·tkhd)과 전체(mvhd)
            moov = Moov(d);
            trak = VideoTrak(d);
            var mvhd = IsoChild(d, moov, "mvhd");
            uint movieScale = Be32(d, mvhd.Body + (d[mvhd.Body] == 1 ? 20 : 12));
            PutDuration(d, IsoPath(d, trak, "mdia", "mdhd"), 16, 24, seconds, scale);
            PutDuration(d, IsoChild(d, trak, "tkhd"), 20, 28, seconds, movieScale);
            double longest = 0;
            foreach (var t in IsoKids(d, moov.Body, moov.End).Where(b => b.Type == "trak"))
                longest = Math.Max(longest, GetDuration(d, IsoChild(d, t, "tkhd"), 20, 28, movieScale));
            PutDuration(d, mvhd, 16, 24, longest, movieScale);
            File.WriteAllBytes(path, d);
            return null;
        }

        /// 편집 내보내기처럼 1/30초마다 장을 꺼내, 홀수 초(흰 화면이 시작하는 장)에 제 장이 나오는지. 틀린 곳을 "k초→받은 장 시각"으로.
        static List<string> OddSecondFrames(string file, double seconds, bool gpu, out bool usedGpu, out double step)
        {
            var wrong = new List<string>();
            using (var r = new VideoReader(file, 320, 320, gpu))
            {
                usedGpu = r.Gpu;
                step = r.Step;
                int n = (int)Math.Round(seconds * 30);
                for (int i = 0; i < n; i++)
                {
                    double ft;
                    r.At(i / 30.0, out ft);
                    if (i % 60 == 30 && Math.Abs(ft - i / 30) > 1e-3) wrong.Add(i / 30 + "초→" + ft.ToString("0.000"));
                }
            }
            return wrong;
        }

        // ── 2. 세로·들쭉날쭉 영상 ──

        static void PhoneVertical()
        {
            string src = Ph("세로.mp4");
            var times = PhoneVfrTimes(20);
            bool hw;
            string err = MakePhoneVideo(src, 640, 360, times, 20, Mf.H264, out hw);
            if (err == null) err = MakeVfr(src, times, 20);
            Check("세로 영상: 시험 영상 만들기(20초, 장 " + times.Count + "개)", err == null, err ?? (hw ? "그래픽 인코더" : "CPU 인코더"));
            if (err != null) return;
            Turn90(src);
            var gaps = ShownGaps(src, 20);
            var pr = Probe(src);
            int rot = pr == null ? -1 : pr.Rotation;
            // 이 영상은 시계 방향 90°로 보여야 한다. MF가 그것을 몇으로 적어 주는지에 맞춰 다른 결과도 읽는다.
            phoneCw = deg => deg;
            if (rot == 270) phoneCw = deg => (360 - deg) % 360;
            Check("세로 영상: 휴대폰처럼 90° 돌림이 적히고 장 간격이 여러 가지", pr != null && (rot == 90 || rot == 270) && gaps.Count(x => x > 10) >= 4,
                "MF가 읽은 돌림 " + rot + "° · " + Say(pr) + " · 장 간격 " + string.Join("/", gaps.Select(x => x.ToString("0.#"))) + "ms");
            Check("세로 영상: 바꿈이 이 돌림을 '시계 방향'으로 읽는다(화면 그리기와 같은 뜻)", rot == 90, "MF 값 " + rot + "° — 270이면 바꿈의 편집 화면이 거꾸로 돌린다");
            string sd;
            var g0 = SyncGaps(src, 20, out sd);
            Cli.Print("    원본 화면−소리: " + sd);
            bool g1, g2;
            double s1, s2;
            var wc = OddSecondFrames(src, 20, false, out g1, out s1);
            var wg = OddSecondFrames(src, 20, true, out g2, out s2);
            Check("세로 영상: 1/30초마다 꺼내도 홀수 초에 제 장이 나온다(CPU로 풀기)", !g1 && wc.Count == 0,
                "장 길이 " + Math.Round(s1 * 1000, 1) + "ms · " + (wc.Count == 0 ? "10곳 모두 맞음" : string.Join(", ", wc)));
            Check("세로 영상: 1/30초마다 꺼내도 홀수 초에 제 장이 나온다(그래픽 장치로 풀기)", g2 && wg.Count == 0,
                (g2 ? "" : "그래픽 장치로 못 엶 · ") + "장 길이 " + Math.Round(s2 * 1000, 1) + "ms · " + (wg.Count == 0 ? "10곳 모두 맞음" : string.Join(", ", wg)));

            // 편집해서 9:16으로 내보내기
            var p = new VProject { Aspect = "9:16", SourceW = 360, SourceH = 640, Fps = 30, Duck = false, EndFade = false };
            p.Clips.Add(new VClip { Path = src, In = 0, Out = 20 });
            string ex = Ph("세로_편집.mp4");
            var job = ExportNow(p, PlanFor(ex, 360, 640, 0, 20), null);
            bool up = false;
            string ud = File.Exists(ex) ? Upright(ex, 0.5, deg => deg, out up) : "결과 없음";
            Check("세로 영상: 편집 내보내기에서 바로 선다(빨간 네모가 오른쪽 위)", job.Problems.Count == 0 && up, ud + Problems(job));
            SyncCheck("세로 영상: 편집 내보내기", ex, 20, g0);

            // 영상 도구
            string mov = Ph("세로_아이폰.mov");
            File.Copy(src, mov);
            var tools = new List<KeyValuePair<string, Job>>
            {
                new KeyValuePair<string, Job>("MP4로(아이폰 .mov에서)", RunJob("media", "mp4", new[] { mov }, null)),
                new KeyValuePair<string, Job>("MOV로", RunJob("media", "mov", new[] { src }, null)),
                new KeyValuePair<string, Job>("MKV로", RunJob("media", "mkv", new[] { src }, null)),
                new KeyValuePair<string, Job>("줄이기", RunJob("media", "compress", new[] { src }, null)),
                new KeyValuePair<string, Job>("자르기", RunJob("media", "trim", new[] { src }, null, "start=0.5", "end=3.5"))
            };
            foreach (var kv in tools)
            {
                string o = Out1(kv.Value);
                bool ok = false;
                string d = o == "" ? "결과 없음" : Upright(o, kv.Key == "자르기" ? 0.2 : 0.5, phoneCw, out ok);
                Check("세로 영상: " + kv.Key + " 결과가 바로 선다", o != "" && ok, d + Problems(kv.Value));
            }
            SyncCheck("세로 영상: MP4로(아이폰 .mov에서)", Out1(tools[0].Value), 20, g0);
            SyncCheck("세로 영상: 줄이기", Out1(tools[3].Value), 20, g0);

            // GIF
            var gj = RunJob("media", "gif", new[] { src }, null, "start=0", "end=2");
            string gif = Out1(gj);
            bool gok = false;
            string gd = "결과 없음" + Problems(gj);
            if (gif != "")
            {
                using (var fs = File.OpenRead(gif))
                {
                    var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var f0 = dec.Frames[0];
                    var px = Bgra32(f0);
                    double nx, ny;
                    bool spot = RedSpot(px, f0.PixelWidth, f0.PixelHeight, out nx, out ny);
                    gok = spot && f0.PixelHeight > f0.PixelWidth && nx > 0.6 && ny < 0.35;
                    gd = f0.PixelWidth + "×" + f0.PixelHeight + (spot ? " · 빨간 네모 (" + nx.ToString("0.00") + ", " + ny.ToString("0.00") + ")" : " · 빨간 네모 없음");
                }
            }
            Check("세로 영상: GIF가 바로 선다", gok, gd);
        }

        // ── 3. 진짜 HEVC ──

        static void PhoneHevc()
        {
            string src = Ph("세로_HEVC.mp4");
            bool hw;
            string err = MakePhoneVideo(src, 1280, 720, EvenTimes(4, 30), 4, VideoReader.Hevc, out hw);
            if (err != null)
            {
                Cli.Print("    이 PC에서 HEVC 영상을 만들지 못해 HEVC 시험은 건너뜀: " + err);
                return;
            }
            Turn90(src);
            var bytes = File.ReadAllBytes(src);
            bool hevc = IndexOf(bytes, Encoding.ASCII.GetBytes("hvc1")) >= 0 || IndexOf(bytes, Encoding.ASCII.GetBytes("hev1")) >= 0;
            var pr = Probe(src);
            Check("HEVC: 이 PC의 인코더로 진짜 HEVC 세로 영상을 만들었다", hevc && pr != null && pr.HasVideo && pr.HasAudio, (hw ? "그래픽 인코더" : "CPU 인코더") + " · " + Say(pr) + " · 돌림 " + (pr == null ? 0 : pr.Rotation) + "°");
            if (!hevc || pr == null) return;
            foreach (bool gpu in new[] { true, false })
            {
                bool ok = false;
                string d;
                try
                {
                    using (var r = new VideoReader(src, 640, 640, gpu))
                    {
                        double ft, nx, ny;
                        var px = r.At(0.5, out ft);
                        ok = px != null && RedSpot(px, r.Width, r.Height, out nx, out ny) && nx < 0.3 && ny < 0.4;
                        d = (r.Gpu ? "그래픽 장치로" : "CPU로") + " " + r.Width + "×" + r.Height;
                    }
                }
                catch (Exception ex)
                {
                    d = ex.GetType().Name + ": " + ex.Message;
                }
                Check("HEVC: 영상 편집이 푼다(" + (gpu ? "그래픽 장치 먼저" : "CPU만") + ")", ok, d);
            }
            var p = new VProject { Aspect = "9:16", SourceW = 720, SourceH = 1280, Fps = 30, Duck = false, EndFade = false };
            p.Clips.Add(new VClip { Path = src, In = 0, Out = 4 });
            string ex2 = Ph("세로_HEVC_편집.mp4");
            var job = ExportNow(p, PlanFor(ex2, 720, 1280, 0, 4), null);
            bool up = false;
            string ud = File.Exists(ex2) ? Upright(ex2, 0.5, deg => deg, out up) : "결과 없음";
            Check("HEVC: 편집해 내보낸 세로 영상이 바로 선다", job.Problems.Count == 0 && up, ud + Problems(job));
            SyncCheck("HEVC: 편집 내보내기", ex2, 4, null);
            var cj = RunJob("media", "compress", new[] { src }, null);
            string co = Out1(cj);
            bool avc = co != "" && IndexOf(File.ReadAllBytes(co), Encoding.ASCII.GetBytes("avc1")) >= 0;
            bool cup = false;
            string cd = co == "" ? "결과 없음" : Upright(co, 0.5, phoneCw, out cup);
            Check("HEVC: 줄이기는 H.264로 바꾸고 세로를 지킨다", avc && cup, cd + (co == "" ? "" : " · " + Conversions.Size(new FileInfo(src).Length) + " → " + Conversions.Size(new FileInfo(co).Length)) + Problems(cj));
        }

        // ── 한컴으로 수업 자료 영상 ──

        static string Lj(string name)
        {
            return Path.Combine(P("lecturejob"), name);
        }

        /// 지어낸 3쪽 발표 자료(1·2쪽에만 발표자 노트)
        static void MakeLectureDeck(string path)
        {
            var deck = new Deck { Title = "물의 순환" };
            string[] titles = { "물의 순환", "구름이 생기는 까닭", "정리해 봐요" };
            string[] bodies = { "바다·강·호수의 물은 어디로 갈까요?", "따뜻한 공기가 올라가며 식으면 수증기가 물방울이 돼요.", "증발 → 응결 → 비·눈 → 다시 바다로" };
            string[] notes = { "오늘은 물의 순환을 배워요. 물은 모양을 바꾸며 돌고 돌아요.", "수증기가 차가워지면 작은 물방울이 되어 구름이 돼요.", "" };
            for (int i = 0; i < 3; i++)
            {
                var page = new DeckPage { Background = "FFFFFF", Notes = notes[i] };
                var t = new DeckShape { X = 800000, Y = 700000, W = 10592000, H = 1300000, IsTitle = true };
                t.Paras.Add(DeckPara.Of(titles[i], 44, "1F2430", true));
                var bar = new DeckShape { X = 800000, Y = 2100000, W = 2400000, H = 90000, Fill = "4472C4" };
                var b = new DeckShape { X = 800000, Y = 2500000, W = 10592000, H = 2800000, IsTextBox = true };
                b.Paras.Add(DeckPara.Of(bodies[i], 30, "44546A", false));
                page.Items.Add(t);
                page.Items.Add(bar);
                page.Items.Add(b);
                deck.Pages.Add(page);
            }
            DeckWriter.Write(path, deck);
        }

        static Job LectureJob(string file)
        {
            return new Job(ActionOf("media"), new ActionOption("lecture", "수업 자료", ""), new List<string> { file }) { Headless = true, AutoYes = true };
        }

        /// 흰 바탕이 아닌 칸의 비율(쪽 그림이 비지 않았는지)
        static double InkShare(string png, out int w, out int h)
        {
            BitmapSource s;
            using (var fs = File.OpenRead(png)) s = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            w = s.PixelWidth;
            h = s.PixelHeight;
            var px = Bgra32(s);
            long ink = 0, all = 0;
            for (int o = 0; o + 3 < px.Length; o += 4 * 5)
            {
                all++;
                if (px[o] + px[o + 1] + px[o + 2] < 600) ink++;
            }
            return all == 0 ? 0 : ink / (double)all;
        }

        static void LectureHancomTests()
        {
            Cli.Print("[수업 자료: 한컴]");
            if (string.IsNullOrEmpty(AppInfo.DataDirOverride))
            {
                Check("자료 폴더를 임시로 바꾸고 시험한다", false, "선생님의 진짜 자료 폴더를 건드리지 않으려고 멈췄어요.");
                return;
            }
            string w = P("lecturejob");
            if (Directory.Exists(w)) Directory.Delete(w, true);
            Directory.CreateDirectory(w);
            VWaveOut.Silent = true;
            Cli.Print("    MS 파워포인트 " + (MsPowerPoint.Installed ? "있음" : "없음") + " · 한쇼 " + (Hanshow.Installed ? "있음" : "없음") + " · 한글 " + (Hangul.Installed ? "있음" : "없음"));

            // 1. 발표 자료 → PDF(한쇼) → 쪽 그림 → 노트 → 읽기 → 영상
            string pptx = Lj("물의순환_수업.pptx"), pdf = Lj("물의순환_수업.pdf");
            MakeLectureDeck(pptx);
            string program = null, err = null;
            var sw = Stopwatch.StartNew();
            var job = LectureJob(pptx);
            try
            {
                program = OfficeConvert.ToPdf(job, pptx, pdf);
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                job.ReleaseResources();
            }
            double pw = 0, ph = 0;
            if (err == null && File.Exists(pdf))
            {
                using (var d = new WinPdfDoc(pdf)) d.PageSizeDip(0, out pw, out ph);
            }
            Check("한컴 수업 자료: 발표 자료(.pptx)를 PDF로", err == null && File.Exists(pdf), err ?? (program + " · " + sw.ElapsedMilliseconds + "ms · 첫 쪽 " + (pw / 96).ToString("0.00") + "×" + (ph / 96).ToString("0.00") + "인치"));
            if (err == null && File.Exists(pdf))
            {
                var pages = new List<string>();
                int total = 0;
                try
                {
                    total = VideoEditor.LecturePages(pdf, VideoEditor.LectureFolder(pdf), pages, delegate (string s, double f) { }, () => false);
                }
                catch (Exception ex)
                {
                    err = ex.GetType().Name + ": " + ex.Message;
                }
                int iw = 0, ih = 0;
                double ink = pages.Count > 0 ? InkShare(pages[0], out iw, out ih) : 0;
                Check("한컴 수업 자료: 쪽 그림 3장, 긴 변 1920 이상, 글자가 그려짐", err == null && total == 3 && pages.Count == 3 && Math.Max(iw, ih) >= 1900 && ink > 0.003,
                    err ?? (total + "쪽 · 첫 쪽 " + iw + "×" + ih + " · 잉크 " + (ink * 100).ToString("0.0") + "%"));

                List<OoxmlRead.SlideNote> notes = null;
                try
                {
                    notes = OoxmlRead.PptxNotes(pptx);
                }
                catch (Exception ex)
                {
                    err = ex.GetType().Name + ": " + ex.Message;
                }
                var mapped = notes == null ? null : VLecture.MapNotes(notes, total, pages.Count);
                Check("한컴 수업 자료: 발표자 노트를 쪽에 잇는다", mapped != null && mapped.Count == 3 && mapped[0].StartsWith("오늘은") && mapped[1].StartsWith("수증기") && mapped[2] == "", err ?? (mapped == null ? "없음" : string.Join(" | ", mapped)));
                var voices = VTts.Voices();
                var ko = voices.FirstOrDefault(v => v.Korean);
                if (mapped != null && ko != null && pages.Count == 3)
                {
                    var spoken = new List<List<VLecture.Spoken>>();
                    int made = 0;
                    sw.Restart();
                    foreach (var m in mapped)
                    {
                        var list = new List<VLecture.Spoken>();
                        foreach (var s in VLecture.Sentences(m))
                        {
                            string wav = VTts.Speak(s, VLecture.VoiceFor(s, ko.Id, voices), 1.0);
                            var wp = Probe(wav);
                            list.Add(new VLecture.Spoken { Text = s, Wav = wav, Len = wp == null ? 0 : wp.Seconds });
                            made++;
                        }
                        spoken.Add(list);
                    }
                    long ttsMs = sw.ElapsedMilliseconds;
                    var p = new VProject { Aspect = "16:9", SourceW = 1920, SourceH = 1080, Fps = 30, Duck = false, EndFade = false };
                    int read = VLecture.Insert(p, 0, pages, spoken, 2, true, null);
                    string mp4 = Lj("물의순환_수업.mp4");
                    var ej = ExportNow(p, PlanFor(mp4, 1280, 720, 0, p.Duration), null);
                    var mp = Probe(mp4);
                    var talk = p.Sounds.Where(s => s.Name == "노트 읽기").OrderBy(s => s.Start).ToList();
                    bool placed = talk.Count == made && talk.Count > 0 && Near(talk[0].Start, VLecture.Lead, 0.01) && talk.All(s => s.Start < p.Clips[0].Length + p.Clips[1].Length);
                    Check("한컴 수업 자료: 노트를 윈도우 목소리로 읽어 쪽·자막과 함께 영상으로", ej.Problems.Count == 0 && mp != null && mp.HasVideo && mp.HasAudio && Near(mp.Seconds, p.Duration, 0.25) && read == made && placed,
                        "문장 " + made + "개(" + ttsMs + "ms) · 쪽 길이 " + string.Join("/", p.Clips.Select(c => c.Length.ToString("0.0"))) + "초 · " + Say(mp) + Problems(ej));
                }
                else
                {
                    Cli.Print("    한국어 목소리가 없거나 노트를 잇지 못해 읽기·영상은 건너뜀");
                }

                // 2. 쪽이 작게 적힌 PDF(한쇼가 옛 .ppt·.show를 바꾸면 42%로 적힌다)도 또렷하게
                string small = Lj("작은쪽.pdf");
                File.Copy(pdf, small);
                bool shrunk = false;
                try
                {
                    shrunk = PdfPageSize.FitTo(small, pw * 0.75 * 0.42, ph * 0.75 * 0.42);
                }
                catch (Exception ex)
                {
                    err = ex.GetType().Name + ": " + ex.Message;
                }
                double aw = 0, ah = 0;
                using (var d = new WinPdfDoc(small)) d.PageSizeDip(0, out aw, out ah);
                var sp = new List<string>();
                VideoEditor.LecturePages(small, VideoEditor.LectureFolder(small), sp, delegate (string s, double f) { }, () => false);
                int sw2 = 0, sh2 = 0;
                double sink = sp.Count > 0 ? InkShare(sp[0], out sw2, out sh2) : 0;
                Check("수업 자료: 작게 적힌 쪽도 긴 변 1920으로 또렷하게", shrunk && Math.Max(sw2, sh2) >= 1900 && sink > 0.003,
                    "쪽 " + (aw / 96).ToString("0.00") + "×" + (ah / 96).ToString("0.00") + "인치(200dpi면 " + Math.Round(aw / 96 * 200) + "×" + Math.Round(ah / 96 * 200) + ") → 그림 " + sw2 + "×" + sh2 + " · dpi " + VideoEditor.LectureDpi(aw, ah).ToString("0"));
            }

            // 3. 한글 문서 → PDF(한글) → 쪽 그림
            string html = Lj("가정통신문_예시.html"), hwp = Lj("가정통신문_예시.hwp"), hpdf = Lj("가정통신문_예시.pdf");
            File.WriteAllText(html, "<html><head><meta charset=\"utf-8\"></head><body><h1>봄 현장 체험 학습 안내</h1><p>학부모님께, 다음 주 금요일에 과학관 체험 학습을 갑니다.</p><p>준비물: 도시락, 물, 모자</p><table border=\"1\"><tr><td>시각</td><td>활동</td></tr><tr><td>9:00</td><td>학교 출발</td></tr><tr><td>10:00</td><td>과학관 관람</td></tr></table></body></html>", new UTF8Encoding(false));
            err = null;
            sw.Restart();
            var hj = LectureJob(html);
            try
            {
                using (var h = new Hangul(hj)) h.Convert(html, hwp, "HWP");
                program = OfficeConvert.ToPdf(hj, hwp, hpdf);
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                hj.ReleaseResources();
            }
            Check("한컴 수업 자료: 한글 문서를 PDF로", err == null && File.Exists(hpdf), err ?? (program + " · " + sw.ElapsedMilliseconds + "ms"));
            if (err == null && File.Exists(hpdf))
            {
                var hp = new List<string>();
                int ht = VideoEditor.LecturePages(hpdf, VideoEditor.LectureFolder(hpdf), hp, delegate (string s, double f) { }, () => false);
                int hw2 = 0, hh2 = 0;
                double hink = hp.Count > 0 ? InkShare(hp[0], out hw2, out hh2) : 0;
                Check("한컴 수업 자료: 한글 문서 쪽 그림(A4 세로)", ht >= 1 && hp.Count == ht && hh2 > hw2 && Math.Max(hw2, hh2) >= 1900 && hink > 0.002, ht + "쪽 · 첫 쪽 " + hw2 + "×" + hh2 + " · 잉크 " + (hink * 100).ToString("0.0") + "%");
            }
        }
    }
}
