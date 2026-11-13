// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집의 소리: 원본 소리 다듬기(빠르기·높낮이·거꾸로·잡음 줄이기), 여러 소리 섞기(배경 음악 줄이기·크기 맞추기),
// 미리 듣기(waveOut), 자막 음성(윈도우 목소리), 녹음(마이크), 조용한 곳 찾기, 효과음 만들기.
// 효과음은 이 파일에서 소리 물결을 직접 계산해 만든다(따로 받은 소리 파일이 없다).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinSpeech = Windows.Media.SpeechSynthesis;

namespace Bakkum
{
    /// 소리 계산
    static class VDsp
    {
        public const int Rate = AudioTrack.Rate;

        /// WSOLA: 높낮이는 그대로 두고 빠르기만 바꾼다(두 줄 섞어 놓은 float).
        public static float[] Stretch(float[] x, int frames, double speed)
        {
            if (Math.Abs(speed - 1) < 1e-4 || frames < 4) return (float[])x.Clone();
            const int N = 1920, Hs = N / 2, Tol = 576;
            double Ha = Hs * speed;
            int outFrames = (int)Math.Ceiling(frames / speed) + N;
            var y = new float[outFrames * 2];
            var norm = new float[outFrames];
            var win = new float[N];
            for (int i = 0; i < N; i++) win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
            int last = 0;
            double posA = 0;
            int posS = 0;
            bool first = true;
            while (posS + N <= outFrames)
            {
                int target = (int)Math.Round(posA);
                if (target >= frames) break;
                int best = target;
                if (!first)
                {
                    int natural = last + Hs;
                    best = BestMatch(x, frames, natural, target, Tol, N / 2);
                }
                for (int i = 0; i < N; i++)
                {
                    int s = best + i;
                    if (s < 0 || s >= frames) continue;
                    float w = win[i];
                    y[(posS + i) * 2] += x[s * 2] * w;
                    y[(posS + i) * 2 + 1] += x[s * 2 + 1] * w;
                    norm[posS + i] += w;
                }
                last = best;
                first = false;
                posA += Ha;
                posS += Hs;
            }
            int keep = Math.Min(outFrames, (int)Math.Round(frames / speed));
            var o = new float[keep * 2];
            for (int i = 0; i < keep; i++)
            {
                float n = norm[i];
                if (n < 1e-3f) n = 1;
                else if (n < 0.5f) n = 0.5f;
                o[i * 2] = y[i * 2] / n;
                o[i * 2 + 1] = y[i * 2 + 1] / n;
            }
            return o;
        }

        /// natural(이어질 자리)와 가장 닮은 target 근처 자리
        static int BestMatch(float[] x, int frames, int natural, int target, int tol, int len)
        {
            int best = target;
            double bestScore = double.MinValue;
            if (natural < 0 || natural + len >= frames) return Math.Max(0, Math.Min(frames - 1, target));
            for (int d = -tol; d <= tol; d += 4)
            {
                int c = target + d;
                if (c < 0 || c + len >= frames) continue;
                double s = 0;
                for (int i = 0; i < len; i += 8)
                {
                    float a = x[(natural + i) * 2] + x[(natural + i) * 2 + 1];
                    float b = x[(c + i) * 2] + x[(c + i) * 2 + 1];
                    s += a * b;
                }
                if (s > bestScore)
                {
                    bestScore = s;
                    best = c;
                }
            }
            // 4칸 간격으로 찾은 자리 주변을 한 칸씩 다듬는다
            int coarse = best;
            for (int d = -3; d <= 3; d++)
            {
                int c = coarse + d;
                if (d == 0 || c < 0 || c + len >= frames) continue;
                double s = 0;
                for (int i = 0; i < len; i += 8)
                {
                    float a = x[(natural + i) * 2] + x[(natural + i) * 2 + 1];
                    float b = x[(c + i) * 2] + x[(c + i) * 2 + 1];
                    s += a * b;
                }
                if (s > bestScore)
                {
                    bestScore = s;
                    best = c;
                }
            }
            return best;
        }

        /// 빠르기를 바꿔 다시 뽑기(높낮이도 함께 바뀐다). 직선으로 잇는다.
        public static float[] Resample(float[] x, int frames, double factor)
        {
            int outFrames = Math.Max(1, (int)Math.Round(frames / factor));
            var o = new float[outFrames * 2];
            for (int i = 0; i < outFrames; i++)
            {
                double p = i * factor;
                int a = (int)p;
                double k = p - a;
                int b = Math.Min(frames - 1, a + 1);
                if (a >= frames) a = frames - 1;
                o[i * 2] = (float)(x[a * 2] + (x[b * 2] - x[a * 2]) * k);
                o[i * 2 + 1] = (float)(x[a * 2 + 1] + (x[b * 2 + 1] - x[a * 2 + 1]) * k);
            }
            return o;
        }

        /// 빠르기와 목소리 높낮이(반음)를 함께
        public static float[] SpeedPitch(float[] x, int frames, double speed, double semitones)
        {
            double pf = Math.Pow(2, semitones / 12.0);
            if (Math.Abs(semitones) < 0.01) return Stretch(x, frames, speed);
            var stretched = Stretch(x, frames, speed / pf);
            return Resample(stretched, stretched.Length / 2, pf);
        }

        public static void Reverse(float[] x)
        {
            int n = x.Length / 2;
            for (int i = 0, j = n - 1; i < j; i++, j--)
            {
                float l = x[i * 2], r = x[i * 2 + 1];
                x[i * 2] = x[j * 2];
                x[i * 2 + 1] = x[j * 2 + 1];
                x[j * 2] = l;
                x[j * 2 + 1] = r;
            }
        }

        // ── 잡음 줄이기: 조용한 곳에서 잡음의 모양을 재고, 그만큼 덜어 낸다 ──

        const int FftN = 1024, Hop = 256;

        public sealed class NoiseProfile
        {
            public float[] Mag = new float[FftN / 2 + 1];
        }

        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int j = 0; j < len / 2; j++)
                    {
                        int a = i + j, b = i + j + len / 2;
                        double ur = re[a], ui = im[a];
                        double vr = re[b] * cr - im[b] * ci, vi = re[b] * ci + im[b] * cr;
                        re[a] = ur + vr; im[a] = ui + vi;
                        re[b] = ur - vr; im[b] = ui - vi;
                        double nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }

        static double[] Hann()
        {
            var w = new double[FftN];
            for (int i = 0; i < FftN; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftN);
            return w;
        }

        /// 가장 조용한 15% 구간으로 잡음 모양을 잰다(모노).
        public static NoiseProfile Measure(float[] x, int frames)
        {
            var p = new NoiseProfile();
            var w = Hann();
            var energies = new List<KeyValuePair<double, int>>();
            for (int s = 0; s + FftN <= frames; s += FftN)
            {
                double e = 0;
                for (int i = 0; i < FftN; i++)
                {
                    double v = (x[(s + i) * 2] + x[(s + i) * 2 + 1]) * 0.5;
                    e += v * v;
                }
                energies.Add(new KeyValuePair<double, int>(e, s));
            }
            if (energies.Count == 0) return p;
            energies.Sort((a, b) => a.Key.CompareTo(b.Key));
            int take = Math.Max(1, energies.Count * 15 / 100);
            var re = new double[FftN];
            var im = new double[FftN];
            var sum = new double[FftN / 2 + 1];
            for (int k = 0; k < take; k++)
            {
                int s = energies[k].Value;
                for (int i = 0; i < FftN; i++)
                {
                    re[i] = (x[(s + i) * 2] + x[(s + i) * 2 + 1]) * 0.5 * w[i];
                    im[i] = 0;
                }
                Fft(re, im);
                for (int b = 0; b <= FftN / 2; b++) sum[b] += Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
            }
            for (int b = 0; b <= FftN / 2; b++) p.Mag[b] = (float)(sum[b] / take);
            return p;
        }

        /// 잡음 덜어 내기. strength 0~1. 두 줄을 따로 처리한다.
        public static float[] Denoise(float[] x, int frames, NoiseProfile noise, double strength)
        {
            var y = new float[frames * 2];
            if (strength <= 0.01 || frames < FftN)
            {
                Array.Copy(x, y, Math.Min(x.Length, y.Length));
                return y;
            }
            var w = Hann();
            double over = 1.2 + 1.8 * strength, floor = Math.Max(0.06, 0.35 - 0.3 * strength);
            Parallel.For(0, 2, ch =>
            {
                var re = new double[FftN];
                var im = new double[FftN];
                var gainPrev = new double[FftN / 2 + 1];
                for (int b = 0; b < gainPrev.Length; b++) gainPrev[b] = 1;
                var acc = new double[frames + FftN];
                var wsum = new double[frames + FftN];
                for (int s = -FftN + Hop; s < frames; s += Hop)
                {
                    for (int i = 0; i < FftN; i++)
                    {
                        int at = s + i;
                        re[i] = at >= 0 && at < frames ? x[at * 2 + ch] * w[i] : 0;
                        im[i] = 0;
                    }
                    Fft(re, im);
                    for (int b = 0; b <= FftN / 2; b++)
                    {
                        double mag = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
                        double g = mag <= 1e-9 ? floor : Math.Max(floor, 1 - over * noise.Mag[b] / mag);
                        // 시간 방향으로 부드럽게(뽀글거림 줄이기)
                        g = g > gainPrev[b] ? g * 0.6 + gainPrev[b] * 0.4 : g * 0.3 + gainPrev[b] * 0.7;
                        gainPrev[b] = g;
                        re[b] *= g;
                        im[b] *= g;
                        if (b > 0 && b < FftN / 2)
                        {
                            re[FftN - b] = re[b];
                            im[FftN - b] = -im[b];
                        }
                    }
                    // 역변환: 켤레를 이용
                    for (int i = 0; i < FftN; i++) im[i] = -im[i];
                    Fft(re, im);
                    for (int i = 0; i < FftN; i++)
                    {
                        int at = s + i;
                        if (at < 0 || at >= frames) continue;
                        acc[at] += re[i] / FftN * w[i];
                        wsum[at] += w[i] * w[i];
                    }
                }
                for (int i = 0; i < frames; i++) y[i * 2 + ch] = (float)(wsum[i] > 1e-6 ? acc[i] / wsum[i] : 0);
            });
            return y;
        }

        public static short[] ToShorts(float[] x, int count)
        {
            var o = new short[count];
            for (int i = 0; i < count; i++)
            {
                float v = x[i];
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                o[i] = (short)Math.Round(v * 32767f);
            }
            return o;
        }

        /// 넘치는 소리를 부드럽게 눌러 찢어지지 않게
        public static void Limit(float[] x, int count)
        {
            const float knee = 0.82f;
            for (int i = 0; i < count; i++)
            {
                float v = x[i], a = v < 0 ? -v : v;
                if (a <= knee) continue;
                float o = knee + (1 - knee) * (float)Math.Tanh((a - knee) / (1 - knee));
                x[i] = v < 0 ? -o : o;
            }
        }
    }

    /// 다듬은 원본 소리(빠르기·높낮이·거꾸로·잡음 줄이기). 임시 파일(16비트 두 줄)에 둔다.
    sealed class VProcessed
    {
        public string Key, File;
        public long Frames;
        public volatile bool Ready, Failed;
        public volatile float Progress;
        FileStream stream;
        readonly object gate = new object();
        byte[] raw = new byte[0];

        public void Mix(long frame0, int count, float[] dst, int dstFrame, float gain)
        {
            if (!Ready || count <= 0) return;
            long a = Math.Max(0, frame0), b = Math.Min(Frames, frame0 + count);
            if (b <= a) return;
            int n = (int)(b - a), skip = (int)(a - frame0);
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
                const float k = 1f / 32768f;
                int o = (dstFrame + skip) * 2;
                for (int i = 0; i < got / 4; i++)
                {
                    dst[o++] += (short)(raw[i * 4] | (raw[i * 4 + 1] << 8)) * k * gain;
                    dst[o++] += (short)(raw[i * 4 + 2] | (raw[i * 4 + 3] << 8)) * k * gain;
                }
            }
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

    static class VAudioProc
    {
        static readonly Dictionary<string, VProcessed> Cache = new Dictionary<string, VProcessed>();

        public static bool Needs(VClip c)
        {
            return c.Kind == "video" && (Math.Abs(c.Speed - 1) > 1e-3 || c.Reverse || Math.Abs(c.Pitch) > 0.01 || c.Denoise > 0.01);
        }

        public static string KeyOf(VClip c)
        {
            return c.Path + "|" + c.In.ToString("F3") + "|" + c.Out.ToString("F3") + "|" + c.Speed.ToString("F3") + "|" + c.Reverse + "|" + c.Pitch.ToString("F1") + "|" + c.Denoise.ToString("F2");
        }

        /// 다듬은 소리(없으면 뒤에서 만들기 시작)
        public static VProcessed Get(VClip c)
        {
            string key = KeyOf(c);
            lock (Cache)
            {
                VProcessed p;
                if (Cache.TryGetValue(key, out p)) return p;
                p = new VProcessed { Key = key };
                Cache[key] = p;
                var clip = c.Copy();
                var th = new Thread(delegate () { Make(p, clip); }) { IsBackground = true, Name = "소리 다듬기", Priority = ThreadPriority.BelowNormal };
                th.SetApartmentState(ApartmentState.MTA);
                th.Start();
                if (Cache.Count > 40)
                {
                    foreach (var old in Cache.Where(kv => kv.Value.Ready && kv.Key != key).Take(10).ToList())
                    {
                        old.Value.Close();
                        Cache.Remove(old.Key);
                    }
                }
                return p;
            }
        }

        /// 이 프로그램이 지금 쓰는 다듬은 소리 파일(정리할 때 남긴다)
        public static List<string> InUse()
        {
            lock (Cache) return Cache.Values.Select(p => p.File).Where(f => f != null).ToList();
        }

        static string Folder
        {
            get
            {
                string d = Path.Combine(AudioCache.Folder, "proc");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static void Make(VProcessed p, VClip c)
        {
            try
            {
                var track = AudioCache.Wait(c.Path, null);
                if (!track.Ready)
                {
                    p.Failed = true;
                    return;
                }
                string name;
                using (var sha = SHA1.Create()) name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(track.File + "|" + p.Key)), 0, 10).Replace("-", "").ToLowerInvariant();
                string file = Path.Combine(Folder, name + ".pcm");
                if (System.IO.File.Exists(file))
                {
                    try { System.IO.File.SetLastWriteTime(file, DateTime.Now); } catch { }
                    p.File = file;
                    p.Frames = new FileInfo(file).Length / 4;
                    p.Ready = true;
                    return;
                }
                long a = (long)Math.Round(c.In * VDsp.Rate), b = Math.Min(track.Frames, (long)Math.Round(c.Out * VDsp.Rate));
                if (b <= a)
                {
                    p.Failed = true;
                    return;
                }
                VDsp.NoiseProfile noise = null;
                if (c.Denoise > 0.01)
                {
                    // 잡음 모양: 조각 전체에서 고루 뽑은 30초 안쪽
                    int sample = (int)Math.Min(b - a, VDsp.Rate * 30L);
                    var probe = track.ReadAll(a + (b - a - sample) / 2, sample);
                    noise = VDsp.Measure(probe, sample);
                }
                const int Seg = VDsp.Rate * 20, Ctx = VDsp.Rate / 2, Fade = 480;
                string tmp = file + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
                long total = b - a;
                long written = 0;
                float[] tail = null;
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    // 거꾸로면 끝에서부터 덩어리를 꺼낸다
                    for (long done = 0; done < total; done += Seg)
                    {
                        long segLen = Math.Min(Seg, total - done);
                        long s0 = c.Reverse ? b - done - segLen : a + done;
                        long c0 = Math.Max(a, s0 - Ctx), c1 = Math.Min(b, s0 + segLen + Ctx);
                        var x = track.ReadAll(c0, (int)(c1 - c0));
                        int frames = (int)(c1 - c0);
                        long pre = s0 - c0, post = c1 - (s0 + segLen);
                        if (c.Reverse)
                        {
                            VDsp.Reverse(x);
                            long t = pre;
                            pre = post;
                            post = t;
                        }
                        if (noise != null) x = VDsp.Denoise(x, frames, noise, c.Denoise);
                        double speed = Math.Max(0.05, c.Speed);
                        var y = VDsp.SpeedPitch(x, frames, speed, c.Pitch);
                        int yFrames = y.Length / 2;
                        int from = (int)Math.Round(pre / speed), len = (int)Math.Round(segLen / speed);
                        from = Math.Max(0, Math.Min(yFrames, from));
                        len = Math.Max(0, Math.Min(yFrames - from, len));
                        // 덩어리 사이는 짧게 섞어 잇는다
                        var part = new float[len * 2];
                        Array.Copy(y, from * 2, part, 0, len * 2);
                        if (tail != null)
                        {
                            int m = Math.Min(Fade, Math.Min(len, tail.Length / 2));
                            for (int i = 0; i < m; i++)
                            {
                                float k = (i + 0.5f) / m;
                                part[i * 2] = part[i * 2] * k + tail[i * 2] * (1 - k);
                                part[i * 2 + 1] = part[i * 2 + 1] * k + tail[i * 2 + 1] * (1 - k);
                            }
                        }
                        int extra = Math.Min(Fade, yFrames - (from + len));
                        tail = extra > 0 ? new float[extra * 2] : null;
                        if (tail != null) Array.Copy(y, (from + len) * 2, tail, 0, extra * 2);
                        var shorts = VDsp.ToShorts(part, part.Length);
                        var bytes = new byte[shorts.Length * 2];
                        Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
                        f.Write(bytes, 0, bytes.Length);
                        written += len;
                        p.Progress = (float)Math.Min(0.99, (done + segLen) / (double)total);
                    }
                }
                try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); System.IO.File.Move(tmp, file); }
                catch { file = tmp; }
                p.File = file;
                p.Frames = written;
                p.Ready = true;
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집: 소리 다듬기 실패 " + ex.GetType().Name);
                p.Failed = true;
            }
        }
    }

    /// 여러 소리를 섞는다(미리 듣기와 내보내기가 같이 쓴다). 한 스레드에서만 쓴다.
    sealed class VMixer
    {
        const int Rate = VDsp.Rate;
        VProject p;
        float[] tmp = new float[0];
        readonly Dictionary<string, double> levels = new Dictionary<string, double>();
        /// 아직 준비되지 않은 소리가 있었는지(미리 듣기에서 알려 준다)
        public bool Pending;
        /// 내보내기: 준비될 때까지 기다린다
        public bool Wait;
        public Func<bool> Stop;

        public void Use(VProject snapshot)
        {
            if (snapshot == p) return;
            p = snapshot;
            levels.Clear();
        }

        AudioTrack Track(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var t = Wait ? AudioCache.Wait(path, Stop) : AudioCache.Get(path);
            if (!t.Ready)
            {
                if (!t.Failed) Pending = true;
                return null;
            }
            return t;
        }

        float[] Tmp(int n)
        {
            if (tmp.Length < n * 2) tmp = new float[n * 2];
            Array.Clear(tmp, 0, n * 2);
            return tmp;
        }

        /// t0부터 n칸(48kHz 두 줄)을 dst에 만든다
        public void Mix(double t0, int n, float[] dst)
        {
            Array.Clear(dst, 0, n * 2);
            Pending = false;
            if (p == null || p.Clips.Count == 0) return;
            double t1 = t0 + n / (double)Rate;
            double dur = p.Duration;
            bool[] speech = null;
            double speechFrom = t0 - 0.45;
            bool hasMusic = p.Sounds.Any(snd => snd.IsMusic);
            if (p.Duck && hasMusic) speech = SpeechMask(speechFrom, t1 + 0.15);

            // 본 영상 줄
            double s = 0;
            foreach (var c in p.Clips)
            {
                double len = c.Length;
                double a = Math.Max(t0, s), b = Math.Min(t1, s + len);
                if (b > a && c.Kind == "video" && !c.Mute && c.Volume > 0.001) MixClip(c, s, a, b, t0, n, dst);
                s += len;
                if (s >= t1) break;
            }
            // 작은 화면의 소리
            foreach (var it in p.Items)
            {
                if (it.Kind != "pip" || it.Mute || it.Volume <= 0.001 || !VMedia.IsVideo(it.Path)) continue;
                double a = Math.Max(t0, it.Start), b = Math.Min(t1, it.End);
                if (b <= a) continue;
                var tr = Track(it.Path);
                if (tr == null) continue;
                int f0 = (int)Math.Round((a - t0) * Rate), cnt = Math.Min(n - f0, (int)Math.Round((b - a) * Rate));
                if (cnt <= 0) continue;
                var buf = Tmp(cnt);
                tr.Mix((long)Math.Round((it.SrcIn + (a - it.Start)) * Rate), cnt, buf, 0, 1f);
                double lv = p.Level ? LevelOf(it.Path, it.SrcIn, it.SrcIn + it.Length, 0.1) : 1;
                Add(dst, buf, f0, cnt, a, delegate (double t) { return it.Volume * lv * Edge(t - it.Start, it.End - t, 0.02, 0.02); });
            }
            // 따로 넣은 소리
            foreach (var snd in p.Sounds)
            {
                if (string.IsNullOrEmpty(snd.Path)) continue;
                double end = snd.Loop ? dur : snd.Start + snd.Length;
                if (snd.IsMusic && !snd.Loop) end = Math.Min(end, dur);
                double a = Math.Max(t0, snd.Start), b = Math.Min(t1, end);
                if (b <= a) continue;
                var tr = Track(snd.Path);
                if (tr == null) continue;
                double srcOut = snd.Out > snd.In ? Math.Min(snd.Out, tr.Seconds) : tr.Seconds;
                double loopLen = Math.Max(0.05, srcOut - snd.In);
                int f0 = (int)Math.Round((a - t0) * Rate), cnt = Math.Min(n - f0, (int)Math.Round((b - a) * Rate));
                if (cnt <= 0) continue;
                var buf = Tmp(cnt);
                // 되풀이: 덩어리 끝에서 다시 처음으로
                int done = 0;
                while (done < cnt)
                {
                    double t = a + done / (double)Rate;
                    double pos = (t - snd.Start);
                    if (snd.Loop) pos %= loopLen;
                    double left = loopLen - pos;
                    int take = Math.Min(cnt - done, Math.Max(1, (int)Math.Floor(left * Rate)));
                    tr.Mix((long)Math.Round((snd.In + pos) * Rate), take, buf, done, 1f);
                    done += take;
                }
                double target = snd.IsMusic ? 0.05 : 0.1;
                double lv = p.Level ? LevelOf(snd.Path, snd.In, srcOut, target) : 1;
                var sp = speech;
                double duckTo = p.DuckTo;
                bool music = snd.IsMusic;
                double fadeOut = snd.FadeOut, fadeIn = snd.FadeIn, start = snd.Start, vol = snd.Volume;
                Add(dst, buf, f0, cnt, a, delegate (double t)
                {
                    double g = vol * lv * Edge(t - start, end - t, Math.Max(0.01, fadeIn), Math.Max(0.01, fadeOut));
                    if (music && sp != null) g *= Duck(sp, speechFrom, t, duckTo);
                    return g;
                });
            }
            // 끝에서 서서히 조용히
            if (p.EndFade && t1 > dur - 1.2)
            {
                for (int i = 0; i < n; i++)
                {
                    double t = t0 + i / (double)Rate;
                    if (t < dur - 1.2) continue;
                    float g = (float)VAnim.Ease((dur - t) / 1.2);
                    dst[i * 2] *= g;
                    dst[i * 2 + 1] *= g;
                }
            }
            VDsp.Limit(dst, n * 2);
        }

        void MixClip(VClip c, double clipStart, double a, double b, double t0, int n, float[] dst)
        {
            int f0 = (int)Math.Round((a - t0) * Rate), cnt = Math.Min(n - f0, (int)Math.Round((b - a) * Rate));
            if (cnt <= 0) return;
            var buf = Tmp(cnt);
            if (VAudioProc.Needs(c))
            {
                var pr = VAudioProc.Get(c);
                while (Wait && !pr.Ready && !pr.Failed)
                {
                    if (Stop != null && Stop()) return;
                    Thread.Sleep(20);
                }
                if (!pr.Ready)
                {
                    if (!pr.Failed) Pending = true;
                    return;
                }
                pr.Mix((long)Math.Round((a - clipStart) * Rate), cnt, buf, 0, 1f);
            }
            else
            {
                var tr = Track(c.Path);
                if (tr == null) return;
                tr.Mix((long)Math.Round(c.SourceAt(a - clipStart) * Rate), cnt, buf, 0, 1f);
            }
            double lv = p.Level ? LevelOf(c.Path, c.In, c.Out, 0.1) : 1;
            double len = c.Length;
            double fin = Math.Max(0.006, c.FadeIn), fout = Math.Max(0.006, c.FadeOut);
            if (c.Trans != "none" && c.TransSec > 0.01) fin = Math.Max(fin, Math.Min(0.3, c.TransSec));
            double vol = c.Volume;
            Add(dst, buf, f0, cnt, a, delegate (double t) { return vol * lv * Edge(t - clipStart, clipStart + len - t, fin, fout); });
        }

        static double Edge(double sinceStart, double untilEnd, double fadeIn, double fadeOut)
        {
            double g = 1;
            if (sinceStart < fadeIn) g *= Math.Max(0, sinceStart / fadeIn);
            if (untilEnd < fadeOut) g *= Math.Max(0, untilEnd / fadeOut);
            return g;
        }

        /// 64칸마다 크기를 정하고 그 사이는 직선으로 잇는다
        static void Add(float[] dst, float[] buf, int f0, int cnt, double tStart, Func<double, double> gainAt)
        {
            const int Step = 64;
            double g0 = gainAt(tStart);
            for (int i = 0; i < cnt; i += Step)
            {
                int m = Math.Min(Step, cnt - i);
                double g1 = gainAt(tStart + (i + m) / (double)Rate);
                for (int k = 0; k < m; k++)
                {
                    float g = (float)(g0 + (g1 - g0) * k / m);
                    int at = (f0 + i + k) * 2, src = (i + k) * 2;
                    dst[at] += buf[src] * g;
                    dst[at + 1] += buf[src + 1] * g;
                }
                g0 = g1;
            }
        }

        // ── 말소리가 날 때 배경 음악 줄이기 ──

        bool[] SpeechMask(double from, double to)
        {
            int blocks = Math.Max(1, (int)Math.Ceiling((to - from) * 100));
            var mask = new bool[blocks];
            for (int j = 0; j < blocks; j++)
            {
                double t = from + j / 100.0;
                if (t < 0) continue;
                mask[j] = SpeechAt(t);
            }
            return mask;
        }

        bool SpeechAt(double t)
        {
            double local;
            int i = p.ClipAt(t, out local);
            if (i >= 0)
            {
                var c = p.Clips[i];
                if (c.Kind == "video" && !c.Mute && c.Volume > 0.05 && t < p.Duration)
                {
                    var tr = Track(c.Path);
                    if (tr != null && tr.Rms.Length > 0)
                    {
                        int k = (int)(c.SourceAt(local) * 100);
                        if (k >= 0 && k < tr.Rms.Length && tr.Rms[k] * c.Volume > 0.025) return true;
                    }
                }
            }
            foreach (var s in p.Sounds)
            {
                if (s.IsMusic || s.Kind == "effect") continue;
                if (t < s.Start || t > s.Start + s.Length) continue;
                var tr = Track(s.Path);
                if (tr == null || tr.Rms.Length == 0) return true;
                int k = (int)((s.In + t - s.Start) * 100);
                if (k >= 0 && k < tr.Rms.Length && tr.Rms[k] * s.Volume > 0.02) return true;
            }
            return false;
        }

        static double Duck(bool[] mask, double from, double t, double duckTo)
        {
            int j = (int)Math.Round((t - from) * 100);
            double d = 1;
            // 0.45초 전부터 0.15초 뒤까지 말소리가 있으면 줄인다(가까울수록 더)
            for (int k = Math.Max(0, j - 45); k <= Math.Min(mask.Length - 1, j + 15); k++)
            {
                if (!mask[k]) continue;
                double dist = Math.Abs(k - j) / 100.0;
                if (k > j) dist *= 3;
                if (dist < d) d = dist;
            }
            double u = VAnim.Clamp01((d - 0.05) / 0.4);
            return duckTo + (1 - duckTo) * u;
        }

        // ── 크기 맞추기 ──

        double LevelOf(string path, double from, double to, double target)
        {
            string key = path + "|" + from.ToString("F2") + "|" + to.ToString("F2") + "|" + target;
            double g;
            if (levels.TryGetValue(key, out g)) return g;
            var tr = Track(path);
            if (tr == null || tr.Rms.Length == 0) return 1;
            int a = Math.Max(0, (int)(from * 100)), b = Math.Min(tr.Rms.Length, (int)Math.Ceiling(Math.Max(from, to) * 100));
            double sum = 0;
            int cnt = 0;
            for (int i = a; i < b; i++)
            {
                double v = tr.Rms[i];
                if (v < 0.003) continue;
                sum += v * v;
                cnt++;
            }
            if (cnt < 10) g = 1;
            else
            {
                double rms = Math.Sqrt(sum / cnt);
                g = Math.Max(0.25, Math.Min(4, target / Math.Max(1e-4, rms)));
                double peak = 0;
                for (int i = a; i < b; i++) peak = Math.Max(peak, tr.Peak[i]);
                if (peak * g > 1.2) g = Math.Max(0.25, 1.2 / Math.Max(1e-4, peak));
            }
            levels[key] = g;
            return g;
        }

        /// 소리 크기 재기(내보내기 전 확인): 말한 곳의 평균(dBFS)과 꽉 찬 칸의 비율
        public static void Loudness(VProject p, Func<bool> stop, out double rmsDb, out double clipRatio, out bool silent)
        {
            var m = new VMixer { Wait = true, Stop = stop };
            m.Use(p);
            double dur = p.Duration;
            const int Chunk = Rate;
            var buf = new float[Chunk * 2];
            double sum = 0;
            long cnt = 0, clipped = 0, total = 0;
            for (double t = 0; t < dur; t += 1)
            {
                if (stop != null && stop()) break;
                int n = (int)Math.Min(Chunk, Math.Round((dur - t) * Rate));
                if (n <= 0) break;
                m.Mix(t, n, buf);
                for (int i = 0; i < n * 2; i += 480)
                {
                    double e = 0;
                    int k = 0;
                    for (int j = i; j < Math.Min(n * 2, i + 480); j++)
                    {
                        double v = buf[j];
                        e += v * v;
                        k++;
                        if (Math.Abs(v) > 0.985) clipped++;
                    }
                    total += k;
                    double r = Math.Sqrt(e / Math.Max(1, k));
                    if (r < 0.003) continue;
                    sum += r * r;
                    cnt++;
                }
            }
            silent = cnt == 0;
            rmsDb = cnt == 0 ? -100 : 10 * Math.Log10(sum / cnt);
            clipRatio = total == 0 ? 0 : clipped / (double)total;
        }
    }

    /// 미리 듣기: 윈도우 기본 소리 장치(waveOut)로 내보낸다. 재생 시각은 장치가 실제로 낸 칸 수로 잰다.
    sealed class VWaveOut : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormatEx
        {
            public ushort Tag, Channels;
            public uint Rate, BytesPerSec;
            public ushort Align, Bits, Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr
        {
            public IntPtr Data;
            public uint Length, Recorded;
            public IntPtr User;
            public uint Flags, Loops;
            public IntPtr Next, Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MmTime
        {
            public uint Type, Value, Pad;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint device, ref WaveFormatEx fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr h, ref MmTime t, int size);

        /// 시험용: 소리를 내지 않고 시계만 돈다
        public static bool Silent;

        const int Buffers = 4, Frames = 2048;
        IntPtr handle;
        readonly IntPtr[] headers = new IntPtr[Buffers];
        readonly IntPtr[] data = new IntPtr[Buffers];
        readonly bool[] queued = new bool[Buffers];
        readonly int hdrSize = Marshal.SizeOf(typeof(WaveHdr));
        readonly int flagsAt = (int)Marshal.OffsetOf(typeof(WaveHdr), "Flags");
        readonly short[] pcm = new short[Frames * 2];
        readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        long fedFrames;
        public bool Opened;

        public VWaveOut()
        {
            if (Silent) return;
            var fmt = new WaveFormatEx { Tag = 1, Channels = 2, Rate = AudioTrack.Rate, Bits = 16, Align = 4, BytesPerSec = AudioTrack.Rate * 4 };
            if (waveOutOpen(out handle, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
            {
                handle = IntPtr.Zero;
                Log.Write("영상 편집: 소리 장치를 열지 못함(소리 없이 재생)");
                return;
            }
            for (int i = 0; i < Buffers; i++)
            {
                data[i] = Marshal.AllocHGlobal(Frames * 4);
                headers[i] = Marshal.AllocHGlobal(hdrSize);
                var h = new WaveHdr { Data = data[i], Length = Frames * 4 };
                Marshal.StructureToPtr(h, headers[i], false);
                waveOutPrepareHeader(handle, headers[i], hdrSize);
            }
            Opened = true;
        }

        /// 비어 있는 칸을 채운다. fill(dst, frames)이 소리를 만든다.
        public void Feed(Action<float[], int> fill, float[] mix)
        {
            if (!Opened)
            {
                if (!watch.IsRunning) watch.Start();
                return;
            }
            for (int i = 0; i < Buffers; i++)
            {
                if (queued[i] && (Marshal.ReadInt32(headers[i], flagsAt) & 1) == 0) continue;
                fill(mix, Frames);
                for (int k = 0; k < Frames * 2; k++)
                {
                    float v = mix[k];
                    if (v > 1f) v = 1f;
                    else if (v < -1f) v = -1f;
                    pcm[k] = (short)(v * 32767f);
                }
                Marshal.Copy(pcm, 0, data[i], Frames * 2);
                // 다시 쓰기 전에 '다 냄' 표시를 지운다
                int flags = Marshal.ReadInt32(headers[i], flagsAt);
                Marshal.WriteInt32(headers[i], flagsAt, flags & ~1);
                waveOutWrite(handle, headers[i], hdrSize);
                queued[i] = true;
                fedFrames += Frames;
            }
        }

        public int ChunkFrames
        {
            get { return Frames; }
        }

        /// 지금까지 실제로 낸 칸 수(소리 장치가 없으면 흐른 시간)
        public long Played
        {
            get
            {
                if (!Opened) return (long)(watch.Elapsed.TotalSeconds * AudioTrack.Rate);
                var t = new MmTime { Type = 2 };
                if (waveOutGetPosition(handle, ref t, Marshal.SizeOf(typeof(MmTime))) != 0 || t.Type != 2) return 0;
                return t.Value;
            }
        }

        /// 멈추고 처음부터
        public void Reset()
        {
            watch.Reset();
            fedFrames = 0;
            if (!Opened) return;
            waveOutReset(handle);
            for (int i = 0; i < Buffers; i++) queued[i] = false;
        }

        public void Dispose()
        {
            if (Opened)
            {
                waveOutReset(handle);
                for (int i = 0; i < Buffers; i++) waveOutUnprepareHeader(handle, headers[i], hdrSize);
                waveOutClose(handle);
                Opened = false;
            }
            for (int i = 0; i < Buffers; i++)
            {
                if (headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(headers[i]);
                if (data[i] != IntPtr.Zero) Marshal.FreeHGlobal(data[i]);
                headers[i] = data[i] = IntPtr.Zero;
            }
        }
    }

    /// WAV 파일 쓰기·읽기(48kHz 두 줄 16비트)
    static class VWav
    {
        public static void Write(string path, short[] pcm, int channels, int rate)
        {
            using (var f = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(f))
            {
                int bytes = pcm.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + bytes);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(bytes);
                var b = new byte[bytes];
                Buffer.BlockCopy(pcm, 0, b, 0, bytes);
                w.Write(b);
            }
        }

        /// 크기를 나중에 적는 WAV(녹음·긴 소리용)
        public sealed class Writer : IDisposable
        {
            readonly FileStream f;
            long bytes;

            public Writer(string path)
            {
                f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                f.Write(new byte[44], 0, 44);
            }

            public void Add(short[] pcm, int count)
            {
                var b = new byte[count * 2];
                Buffer.BlockCopy(pcm, 0, b, 0, b.Length);
                f.Write(b, 0, b.Length);
                bytes += b.Length;
            }

            public long Frames
            {
                get { return bytes / 4; }
            }

            public void Dispose()
            {
                f.Position = 0;
                var w = new BinaryWriter(f);
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write((int)(36 + bytes));
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write((short)2);
                w.Write(AudioTrack.Rate);
                w.Write(AudioTrack.Rate * 4);
                w.Write((short)4);
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write((int)bytes);
                w.Flush();
                f.Dispose();
            }
        }

        /// 16비트 PCM WAV의 표본(다른 형식이면 null)
        public static short[] ReadPcm16(byte[] wav, out int channels, out int rate)
        {
            channels = 0;
            rate = 0;
            int pos = 12;
            short[] result = null;
            int bits = 0;
            while (pos + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(wav, body + 2);
                    rate = BitConverter.ToInt32(wav, body + 4);
                    bits = BitConverter.ToInt16(wav, body + 14);
                }
                else if (id == "data" && bits == 16)
                {
                    int n = Math.Min(size, wav.Length - body) / 2;
                    result = new short[n];
                    Buffer.BlockCopy(wav, body, result, 0, n * 2);
                }
                pos = body + size + (size & 1);
            }
            return result;
        }
    }

    /// 자막 음성: 윈도우에 들어 있는 목소리(인터넷으로 보내지 않는다)
    static class VTts
    {
        public sealed class Voice
        {
            public string Id, Name, Lang;
            public bool Korean;
        }

        public static List<Voice> Voices()
        {
            return WinRtCall.Mta(() =>
            {
                var list = new List<Voice>();
                foreach (var v in WinSpeech.SpeechSynthesizer.AllVoices)
                    list.Add(new Voice { Id = v.Id, Name = v.DisplayName, Lang = v.Language ?? "", Korean = (v.Language ?? "").StartsWith("ko", StringComparison.OrdinalIgnoreCase) });
                return list.OrderByDescending(v => v.Korean).ThenBy(v => v.Name).ToList();
            });
        }

        /// 다시 만들 수 있어서 캐시 폴더에 둔다. 자동 저장한 편집이 쓰는 것은 정리에서 남긴다(VAutoSave.Tidy).
        public static string Folder
        {
            get
            {
                string d = Path.Combine(AppInfo.CacheDir, "voice");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        /// 글을 읽은 WAV 파일(48kHz 두 줄). 앞뒤의 빈 소리는 다듬는다. rate는 0.5~2.
        public static string Speak(string text, string voiceId, double rate)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new UserFacingException("읽을 글이 없어요.");
            string key;
            using (var sha = SHA1.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text + "|" + voiceId + "|" + rate.ToString("F2"))), 0, 10).Replace("-", "").ToLowerInvariant();
            string path = Path.Combine(Folder, key + ".wav");
            if (File.Exists(path))
            {
                try { File.SetLastWriteTime(path, DateTime.Now); } catch { }
                return path;
            }
            byte[] wav = WinRtCall.Mta(() =>
            {
                using (var synth = new WinSpeech.SpeechSynthesizer())
                {
                    if (!string.IsNullOrEmpty(voiceId))
                    {
                        foreach (var v in WinSpeech.SpeechSynthesizer.AllVoices)
                            if (v.Id == voiceId) synth.Voice = v;
                    }
                    else
                    {
                        foreach (var v in WinSpeech.SpeechSynthesizer.AllVoices)
                            if ((v.Language ?? "").StartsWith("ko", StringComparison.OrdinalIgnoreCase)) { synth.Voice = v; break; }
                    }
                    try { synth.Options.SpeakingRate = Math.Max(0.5, Math.Min(2.0, rate)); }
                    catch (Exception ex) { Log.Write("읽기 빠르기 설정 실패: " + ex.GetType().Name); }
                    var stream = WinRtCall.Await(synth.SynthesizeTextToStreamAsync(text));
                    using (stream) return WinRtCall.ReadAll(stream);
                }
            });
            int ch, sr;
            var pcm = VWav.ReadPcm16(wav, out ch, out sr);
            if (pcm == null || ch <= 0 || sr <= 0) throw new UserFacingException("윈도우 목소리가 소리를 만들지 못했어요.");
            var stereo = To48Stereo(pcm, ch, sr);
            stereo = Trim(stereo);
            VWav.Write(path, stereo, 2, AudioTrack.Rate);
            return path;
        }

        /// 소리 파일이 없는 자막 음성을 저장된 글·목소리·빠르기로 다시 만든다(로밍 프로필로 다른 PC에서 열 때 등). 다시 만든 수.
        /// 그 목소리가 이 PC에 없으면 한국어 목소리로 읽는다.
        public static int Remake(VProject p)
        {
            var missing = p.Sounds.Where(s => s.Kind == "voice" && !string.IsNullOrWhiteSpace(s.Text) && !string.IsNullOrEmpty(s.Path) && !File.Exists(s.Path)).ToList();
            if (missing.Count == 0) return 0;
            int n = 0;
            try
            {
                var here = new HashSet<string>(Voices().Select(v => v.Id));
                foreach (var s in missing)
                {
                    string path = Speak(s.Text.Replace("\r", " ").Replace("\n", " "), s.Voice != null && here.Contains(s.Voice) ? s.Voice : null, s.Rate > 0 ? s.Rate : 1);
                    s.Path = path;
                    s.Out = Math.Min(s.Out, Math.Max(0.1, (new FileInfo(path).Length - 44) / (4.0 * AudioTrack.Rate)));
                    n++;
                }
            }
            catch (Exception ex)
            {
                Log.Write("자막 음성 다시 만들기 실패: " + ex.GetType().Name);
            }
            return n;
        }

        static short[] To48Stereo(short[] pcm, int ch, int sr)
        {
            int frames = pcm.Length / ch;
            double step = sr / (double)AudioTrack.Rate;
            int outFrames = (int)(frames / step);
            var o = new short[outFrames * 2];
            for (int i = 0; i < outFrames; i++)
            {
                double p = i * step;
                int a = (int)p;
                int b = Math.Min(frames - 1, a + 1);
                double k = p - a;
                double l = pcm[a * ch] + (pcm[b * ch] - pcm[a * ch]) * k;
                double r = ch > 1 ? pcm[a * ch + 1] + (pcm[b * ch + 1] - pcm[a * ch + 1]) * k : l;
                o[i * 2] = (short)l;
                o[i * 2 + 1] = (short)r;
            }
            return o;
        }

        /// 앞뒤 빈 소리 다듬기(윈도우 목소리는 앞에 0.45~0.6초를 비워 둔다)
        static short[] Trim(short[] s)
        {
            int frames = s.Length / 2;
            int first = 0, last = frames - 1;
            while (first < frames && Math.Abs((int)s[first * 2]) < 300 && Math.Abs((int)s[first * 2 + 1]) < 300) first++;
            while (last > first && Math.Abs((int)s[last * 2]) < 300 && Math.Abs((int)s[last * 2 + 1]) < 300) last--;
            first = Math.Max(0, first - AudioTrack.Rate * 3 / 100);
            last = Math.Min(frames - 1, last + AudioTrack.Rate * 12 / 100);
            if (last <= first) return s;
            var o = new short[(last - first + 1) * 2];
            Array.Copy(s, first * 2, o, 0, o.Length);
            return o;
        }
    }

    /// 녹음: 마이크 소리를 WAV로. 버튼을 누를 때만 마이크를 연다.
    sealed class VMicRecorder
    {
        Thread thread;
        volatile bool stop;
        public volatile float Level;
        public string Error;
        public string Path;
        public double Seconds;
        readonly ManualResetEvent started = new ManualResetEvent(false);

        /// 녹음은 다시 만들 수 없는 사용자 자료라 자동 저장과 같은 곳에 둔다(정리할 때 편집이 쓰는 것은 남긴다)
        public static string Folder
        {
            get
            {
                string d = System.IO.Path.Combine(VAutoSave.Folder, "rec");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        /// 녹음을 시작한다. 마이크를 열지 못하면 사용자에게 보일 문장으로 예외.
        public void Start()
        {
            Path = System.IO.Path.Combine(Folder, "내목소리_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");
            thread = new Thread(Loop) { IsBackground = true, Name = "녹음" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            started.WaitOne(4000);
            if (Error != null) throw new UserFacingException(Error);
        }

        void Loop()
        {
            WasapiSource src = null;
            VWav.Writer w = null;
            try
            {
                src = new WasapiSource(false);
                w = new VWav.Writer(Path);
                var fifo = new SoundFifo(4);
                src.Start();
                started.Set();
                var mix = new float[ScreenRecorder.Rate * 2];
                var pcm = new short[ScreenRecorder.Rate * 2];
                while (!stop)
                {
                    src.Pull(fifo);
                    int n = fifo.Frames;
                    if (n > 0)
                    {
                        Array.Clear(mix, 0, n * 2);
                        fifo.MixInto(mix, n);
                        float peak = 0;
                        for (int i = 0; i < n * 2; i++)
                        {
                            float v = mix[i];
                            if (v > 1f) v = 1f;
                            else if (v < -1f) v = -1f;
                            pcm[i] = (short)(v * 32767f);
                            if (Math.Abs(v) > peak) peak = Math.Abs(v);
                        }
                        Level = peak;
                        w.Add(pcm, n * 2);
                    }
                    Thread.Sleep(10);
                }
                src.Pull(fifo);
                Seconds = w.Frames / (double)AudioTrack.Rate;
            }
            catch (UserFacingException ex)
            {
                Error = ex.Message;
            }
            catch (Exception ex)
            {
                Log.Write("녹음 실패: " + ex.GetType().Name);
                Error = "녹음하지 못했어요. (" + ex.GetType().Name + ")";
            }
            finally
            {
                started.Set();
                if (w != null)
                {
                    Seconds = w.Frames / (double)AudioTrack.Rate;
                    w.Dispose();
                }
                if (src != null) src.Dispose();
            }
        }

        public void Stop()
        {
            stop = true;
            if (thread != null) thread.Join(3000);
        }
    }

    /// 조용한 곳 찾기(10ms 소리 세기 띠로)
    static class VSilence
    {
        public sealed class Gap
        {
            public double Start, End;
        }

        /// 소리가 거의 없는 곳(원본 시간). threshold가 0이면 말소리와 바탕 소리 크기로 알아서 정한다.
        public static List<Gap> Find(AudioTrack t, double from, double to, double thresholdDb, double minSec, double keep)
        {
            var gaps = new List<Gap>();
            if (t == null || !t.Ready || t.Rms.Length == 0) return gaps;
            int a = Math.Max(0, (int)(from * 100)), b = Math.Min(t.Rms.Length, (int)Math.Ceiling(to * 100));
            if (b - a < 10) return gaps;
            double th = thresholdDb == 0 ? AutoThreshold(t, a, b) : Math.Pow(10, thresholdDb / 20);
            int run = -1;
            for (int i = a; i <= b; i++)
            {
                bool quiet = i < b && t.Rms[i] < th;
                if (quiet && run < 0) run = i;
                if (!quiet && run >= 0)
                {
                    double s = run / 100.0, e = i / 100.0;
                    if (e - s >= minSec)
                    {
                        double ks = run == a ? 0 : keep, ke = i >= b ? 0 : keep;
                        if (e - ke - (s + ks) >= 0.1) gaps.Add(new Gap { Start = s + ks, End = e - ke });
                    }
                    run = -1;
                }
            }
            return gaps;
        }

        public static double AutoThreshold(AudioTrack t, int a, int b)
        {
            var v = new List<float>();
            for (int i = a; i < b; i++) v.Add(t.Rms[i]);
            v.Sort();
            double floor = Math.Max(1e-4, v[v.Count / 10]);
            double speech = Math.Max(floor * 2, v[v.Count * 85 / 100]);
            // 바탕 소리와 말소리 사이(로그 크기로 30% 지점)
            double th = Math.Exp(Math.Log(floor) + (Math.Log(speech) - Math.Log(floor)) * 0.3);
            return Math.Max(0.004, Math.Min(0.06, th));
        }
    }

    /// 효과음: 소리 물결을 계산해서 만든다
    static class VSfx
    {
        public static readonly string[][] All =
        {
            new[] { "ding", "딩동" }, new[] { "correct", "정답" }, new[] { "wrong", "땡" }, new[] { "pop", "뿅" },
            new[] { "whoosh", "휙" }, new[] { "tick", "똑딱" }, new[] { "chime", "반짝" }, new[] { "drum", "두둥" }, new[] { "click", "딸깍" }
        };

        public static string Label(string id)
        {
            foreach (var s in All) if (s[0] == id) return s[1];
            return "효과음";
        }

        public static string PathOf(string id)
        {
            string d = Path.Combine(VAutoSave.Folder, "sfx");
            Directory.CreateDirectory(d);
            string path = Path.Combine(d, id + "_v1.wav");
            if (!File.Exists(path)) VWav.Write(path, Make(id), 2, AudioTrack.Rate);
            return path;
        }

        static short[] Make(string id)
        {
            const int R = AudioTrack.Rate;
            var rnd = new Random(7);
            Func<double, double, double, double> tone = (f, t, decay) => Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * decay);
            double len;
            Func<double, double> wave;
            switch (id)
            {
                case "ding":
                    len = 1.4;
                    wave = t => 0.5 * (t < 0.45 ? tone(1318.5, t, 4) + 0.4 * tone(2637, t, 7) : 0) + (t >= 0.45 ? 0.5 * (tone(1046.5, t - 0.45, 3.5) + 0.4 * tone(2093, t - 0.45, 6)) : 0);
                    break;
                case "correct":
                    len = 0.9;
                    wave = t => 0.42 * (Note(t, 0, 0.12, 784) + Note(t, 0.11, 0.12, 988) + Note(t, 0.22, 0.6, 1175));
                    break;
                case "wrong":
                    len = 0.7;
                    wave = t => 0.32 * Math.Sign(Math.Sin(2 * Math.PI * (t < 0.3 ? 220 : 185) * t)) * Env(t, 0.005, 0.62) * (t < 0.3 || t > 0.34 ? 1 : 0);
                    break;
                case "pop":
                    len = 0.25;
                    wave = t => 0.6 * Math.Sin(2 * Math.PI * (380 + 1600 * t) * t) * Math.Exp(-t * 22);
                    break;
                case "whoosh":
                    {
                        len = 0.7;
                        double lp = 0, lp2 = 0;
                        wave = t =>
                        {
                            double cut = 0.02 + 0.25 * Math.Sin(Math.PI * Math.Min(1, t / 0.7));
                            lp += (rnd.NextDouble() * 2 - 1 - lp) * cut;
                            lp2 += (lp - lp2) * cut;
                            return 1.6 * lp2 * Math.Sin(Math.PI * Math.Min(1, t / 0.7));
                        };
                        break;
                    }
                case "tick":
                    len = 1.0;
                    wave = t => 0.55 * (Click(t, 0, 2600) + Click(t, 0.5, 2000));
                    break;
                case "chime":
                    len = 1.6;
                    wave = t => 0.3 * (Note(t, 0, 1.2, 1568) + Note(t, 0.08, 1.2, 2093) + Note(t, 0.16, 1.3, 2637) + Note(t, 0.24, 1.3, 3136));
                    break;
                case "drum":
                    len = 1.1;
                    wave = t => 0.8 * (Kick(t, 0) + Kick(t, 0.32) * 1.1);
                    break;
                default:
                    len = 0.12;
                    wave = t => 0.6 * Click(t, 0, 3200);
                    break;
            }
            int n = (int)(len * R);
            var o = new short[n * 2];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)R;
                double v = wave(t);
                double edge = Math.Min(1, (len - t) / 0.02);
                v *= Math.Max(0, edge);
                short s = (short)Math.Max(-32767, Math.Min(32767, v * 32767));
                o[i * 2] = s;
                o[i * 2 + 1] = s;
            }
            return o;
        }

        static double Env(double t, double attack, double len)
        {
            if (t < 0 || t > len) return 0;
            return Math.Min(1, t / attack) * Math.Max(0, 1 - t / len);
        }

        static double Note(double t, double at, double len, double f)
        {
            double u = t - at;
            if (u < 0 || u > len + 0.6) return 0;
            return (Math.Sin(2 * Math.PI * f * u) + 0.3 * Math.Sin(4 * Math.PI * f * u)) * Math.Min(1, u / 0.004) * Math.Exp(-u * 5);
        }

        static double Click(double t, double at, double f)
        {
            double u = t - at;
            if (u < 0 || u > 0.06) return 0;
            return Math.Sin(2 * Math.PI * f * u) * Math.Exp(-u * 90);
        }

        static double Kick(double t, double at)
        {
            double u = t - at;
            if (u < 0 || u > 0.7) return 0;
            double f = 55 + 120 * Math.Exp(-u * 30);
            return Math.Sin(2 * Math.PI * f * u) * Math.Exp(-u * 6);
        }
    }
}
