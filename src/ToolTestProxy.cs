// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 미리 보기 사본 시험. 통째 그림이 10초에 한 번뿐인 1080p 가짜 영상을 만들어(줌·OBS 녹화와 비슷하게),
// 넘기기 빠르기를 원본과 사본에서 재고, 같은 시각에 같은 장이 나오는지 본다.
// 장마다 왼쪽 위 네모의 회색 밝기에 장 번호를 적어 두어 어긋나면 바로 드러난다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Bakkum
{
    static partial class ToolTest
    {
        const double ProxyFps = 30;

        /// 장 번호 k의 네모 밝기(이웃 장끼리 7씩 다르다)
        static int ProxyLevel(int k)
        {
            return 16 + (k * 7) % 220;
        }

        /// 통째 그림이 드문 1080p 가짜 영상. 하드웨어 인코더를 썼는지 돌려준다.
        static bool MakeLongGop(string path, double seconds, out bool taken)
        {
            return MakeLongGop(path, seconds, ProxyFps, out taken);
        }

        static bool MakeLongGop(string path, double seconds, double fps, out bool taken)
        {
            const int W = 1920, H = 1080;
            if (File.Exists(path)) File.Delete(path);
            var bg = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    bg[o] = bg[o + 1] = bg[o + 2] = (byte)(40 + (x + y) % 120);
                    bg[o + 3] = 255;
                }
            }
            var enc = new VEncoder(path, W, H, fps, 6000000, 0, null, 0, seconds, () => false) { KeyEvery = 300 };
            enc.Start();
            var buffers = new Queue<byte[]>();
            int n = (int)Math.Round(seconds * fps);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    byte[] px;
                    lock (buffers) px = buffers.Count > 0 ? buffers.Dequeue() : new byte[W * H * 4];
                    Buffer.BlockCopy(bg, 0, px, 0, bg.Length);
                    // 움직이는 막대(먼저) → 장 번호 네모(위에)
                    int bar = (i * 37) % W, barEnd = Math.Min(W, bar + 40);
                    for (int y = 0; y < H; y++)
                    {
                        for (int x = bar, o = (y * W + bar) * 4; x < barEnd; x++, o += 4) px[o] = px[o + 1] = px[o + 2] = 235;
                    }
                    byte level = (byte)ProxyLevel(i);
                    for (int y = 0; y < 256; y++)
                    {
                        for (int x = 0, o = y * W * 4; x < 256; x++, o += 4) px[o] = px[o + 1] = px[o + 2] = level;
                    }
                    enc.AddVideo(px, i, delegate (byte[] done) { lock (buffers) buffers.Enqueue(done); });
                }
            }
            finally
            {
                enc.Finish();
            }
            taken = enc.KeyEveryTaken;
            return enc.Hardware;
        }

        static readonly string[] BoxParents = { "moov", "trak", "mdia", "minf", "stbl" };

        /// mp4 영상의 통째 그림 번호(1부터, stss 상자). 상자가 없으면(모든 장이 통째) null.
        static List<int> KeyFrames(string mp4)
        {
            using (var fs = File.OpenRead(mp4))
            {
                return FindStss(fs, new BinaryReader(fs), 0, fs.Length);
            }
        }

        static long BigEndian(BinaryReader br, int bytes)
        {
            long v = 0;
            foreach (var b in br.ReadBytes(bytes)) v = (v << 8) | b;
            return v;
        }

        static List<int> FindStss(FileStream fs, BinaryReader br, long from, long to)
        {
            long pos = from;
            while (pos + 8 <= to)
            {
                fs.Position = pos;
                long size = BigEndian(br, 4), head = 8;
                string type = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (size == 1)
                {
                    size = BigEndian(br, 8);
                    head = 16;
                }
                else if (size == 0) size = to - pos;
                if (size < head) return null;
                if (type == "stss")
                {
                    fs.Position = pos + head + 4;
                    int n = (int)BigEndian(br, 4);
                    var list = new List<int>(n);
                    for (int i = 0; i < n; i++) list.Add((int)BigEndian(br, 4));
                    return list;
                }
                if (Array.IndexOf(BoxParents, type) >= 0)
                {
                    var r = FindStss(fs, br, pos + head, pos + size);
                    if (r != null) return r;
                }
                pos += size;
            }
            return null;
        }

        /// 통째 그림 사이 간격(장 수): 가장 짧은 것~가장 긴 것
        static string Spacing(List<int> keys, out int min, out int max)
        {
            min = int.MaxValue;
            max = 0;
            if (keys == null) return "모든 장이 통째 그림";
            for (int i = 1; i < keys.Count; i++)
            {
                int d = keys[i] - keys[i - 1];
                min = Math.Min(min, d);
                max = Math.Max(max, d);
            }
            if (keys.Count < 2) return "통째 그림 " + keys.Count + "개";
            return "통째 그림 " + keys.Count + "개, 간격 " + (min == max ? min + "장" : min + "~" + max + "장");
        }

        /// 그림 가운데 근처(왼쪽 위 네모 안)의 밝기
        static int BlockLevel(byte[] px, int w, int h)
        {
            int cx = Math.Max(1, w * 64 / 1920), cy = Math.Max(1, h * 64 / 1080), sum = 0, cnt = 0;
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                for (int x = cx - 1; x <= cx + 1; x++)
                {
                    int o = (y * w + x) * 4;
                    sum += (px[o] + px[o + 1] + px[o + 2]) / 3;
                    cnt++;
                }
            }
            return sum / cnt;
        }

        /// 어느 장이 나왔는지(밝기로 거꾸로 찾기, 기대한 장 둘레 ±3장 안에서). 못 찾으면 int.MinValue.
        static int WhichFrame(int level, int expect)
        {
            int best = int.MinValue, gap = 4;
            for (int k = Math.Max(0, expect - 3); k <= expect + 3; k++)
            {
                int d = Math.Abs(ProxyLevel(k) - level);
                if (d < gap)
                {
                    gap = d;
                    best = k;
                }
            }
            return best;
        }

        /// 시각마다 넘겨 보는 데 걸린 시간(ms). 처음 여는 값은 빼려고 먼저 한 번 읽어 둔다.
        static List<double> SeekCosts(string file, List<double> times, out int right, out bool gpu)
        {
            var cost = new List<double>();
            int ok = 0;
            bool g = false;
            WinRtCall.Mta<bool>(delegate
            {
                using (var r = new VideoReader(file, VProxy.LongSide, VProxy.LongSide, true))
                {
                    g = r.Gpu;
                    double ft;
                    r.At(0, out ft);
                    foreach (var t in times)
                    {
                        var w = Stopwatch.StartNew();
                        var px = r.At(t, out ft);
                        cost.Add(w.Elapsed.TotalMilliseconds);
                        int k = (int)Math.Floor(t * ProxyFps + 1e-6);
                        if (px != null && WhichFrame(BlockLevel(px, r.Width, r.Height), k) == k) ok++;
                    }
                }
                return true;
            });
            right = ok;
            gpu = g;
            cost.Sort();
            return cost;
        }

        static int LevelAt(byte[] px, int w, int x, int y)
        {
            int o = (y * w + x) * 4;
            return (px[o] + px[o + 1] + px[o + 2]) / 3;
        }

        static void ProxyTests(bool heavy)
        {
            Cli.Print(heavy ? "[미리 보기 사본 · 2분 영상으로 재기]" : "  · 미리 보기 사본");
            double seconds = heavy ? 120 : 12;
            int frames = (int)Math.Round(seconds * ProxyFps);
            string src = P(heavy ? "proxy_long.mp4" : "proxy_short.mp4");
            var sw = Stopwatch.StartNew();
            bool taken;
            bool hw = MakeLongGop(src, seconds, out taken);
            double made = sw.Elapsed.TotalSeconds;
            var ps = Probe(src);
            int kmin, kmax;
            string srcKeys = Spacing(KeyFrames(src), out kmin, out kmax);
            Check("사본 시험: 통째 그림이 드문 1080p 원본", ps != null && ps.Width == 1920 && Near(ps.Seconds, seconds, 0.2), Say(ps) + " · 만드는 데 " + made.ToString("0.0") + "초 · " + (hw ? "하드웨어" : "소프트웨어") + " 인코더");
            Check("인코더가 통째 그림 간격(300장)을 지킨다", kmin == 300 && kmax == 300, srcKeys + (taken ? " · 설정 받음" : " · 설정 안 받음"));
            if (ps == null) return;
            string old = VProxy.PathFor(src);
            if (File.Exists(old)) File.Delete(old);
            VProxy.Drop(src);

            var rnd = new Random(7);
            var times = Enumerable.Range(0, heavy ? 25 : 12).Select(i => Math.Round(rnd.NextDouble() * (seconds - 0.5) * ProxyFps) / ProxyFps).ToList();
            int srcRight;
            bool srcGpu;
            var srcCost = SeekCosts(src, times, out srcRight, out srcGpu);
            Check("사본 시험: 원본에서 찾은 시각의 장이 나온다", srcRight == times.Count, srcRight + "/" + times.Count + (srcGpu ? " · 그래픽 장치로 풂" : " · CPU로 풂"));

            if (heavy)
            {
                // (참고) 사본 만들기에서 시간이 어디에 드는지: 원본 차례로 읽기만 / 960 크기 쓰기만
                double readFps = 0;
                WinRtCall.Mta<bool>(delegate
                {
                    using (var r = new VideoReader(src, VProxy.LongSide, VProxy.LongSide, true))
                    {
                        var w = Stopwatch.StartNew();
                        double ft;
                        for (int i = 0; i < frames; i++) r.At(i / ProxyFps, out ft);
                        readFps = frames / w.Elapsed.TotalSeconds;
                    }
                    return true;
                });
                string wtmp = P("proxy_writeonly.mp4");
                var gray = new byte[960 * 540 * 4];
                for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(i % 4 == 3 ? 255 : 128);
                var ww = Stopwatch.StartNew();
                var e2 = new VEncoder(wtmp, 960, 540, ProxyFps, 2000000, 0, null, 0, seconds, () => false) { KeyEvery = 30 };
                e2.Start();
                for (int i = 0; i < frames; i++) e2.AddVideo(gray, i, null);
                e2.Finish();
                double writeFps = frames / ww.Elapsed.TotalSeconds;
                if (File.Exists(wtmp)) File.Delete(wtmp);
                Check("(참고) 원본 차례로 읽기만 / 960 크기 쓰기만", true, "읽기 1초에 " + readFps.ToString("0") + "장 · 쓰기 1초에 " + writeFps.ToString("0") + "장");
            }

            sw.Restart();
            string proxy = null, err = null;
            var steps = new List<double>();
            try
            {
                proxy = WinRtCall.Mta(() => VProxy.Make(src, steps.Add, () => false));
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
            }
            double makeSec = sw.Elapsed.TotalSeconds;
            var pp = proxy == null ? null : Probe(proxy);
            // 사본은 크고 다시 만들 수 있어서 설정 폴더(로밍)가 아니라 이 PC 안쪽 캐시 폴더에 둔다(2026-11-12 옮김)
            Check("사본 만들기: 긴 쪽 960·같은 길이·소리 없음, 바꿈 캐시 폴더 안에", err == null && pp != null && Math.Max(pp.Width, pp.Height) == VProxy.LongSide && Near(pp.Seconds, seconds, 0.2) && !pp.HasAudio
                && proxy.StartsWith(Path.Combine(AppInfo.CacheDir, "proxy"), StringComparison.OrdinalIgnoreCase),
                err ?? (Say(pp) + " · " + makeSec.ToString("0.0") + "초 걸림(원본 길이의 " + (seconds / Math.Max(0.01, makeSec)).ToString("0.0") + "배 빠르기) · " + Conversions.Size(new FileInfo(proxy).Length)));
            if (proxy == null) return;
            bool rising = steps.Count > 2 && steps.Last() == 1;
            for (int i = 1; i < steps.Count; i++) if (steps[i] < steps[i - 1]) rising = false;
            Check("사본 만들기: 진행 막대가 0에서 1까지 차오른다", rising, steps.Count + "번 알림, 마지막 " + (steps.Count > 0 ? steps.Last().ToString("0.00") : "없음"));

            int proxyRight;
            bool proxyGpu;
            var proxyCost = SeekCosts(proxy, times, out proxyRight, out proxyGpu);
            Check("사본: 같은 시각에 원본과 같은 장", proxyRight == times.Count, proxyRight + "/" + times.Count);
            string proxyKeys = Spacing(KeyFrames(proxy), out kmin, out kmax);
            Check("사본: 1초마다 통째 그림(넘기기가 빠른 까닭)", kmin == 30 && kmax == 30, proxyKeys);
            double srcMid = srcCost[srcCost.Count / 2], proxyMid = proxyCost[proxyCost.Count / 2];
            Check("사본: 넘기기가 빨라진다", proxyMid < srcMid && proxyCost.Last() < srcCost.Last(),
                "가운데값 원본 " + srcMid.ToString("0") + "ms → 사본 " + proxyMid.ToString("0") + "ms · 가장 느림 원본 " + srcCost.Last().ToString("0") + "ms → 사본 " + proxyCost.Last().ToString("0") + "ms");

            // 원본 이름을 바꿔도 같은 사본(내용으로 정한 이름)
            string renamed = P(heavy ? "proxy_long 이름바꿈.mp4" : "proxy_short 이름바꿈.mp4");
            if (File.Exists(renamed)) File.Delete(renamed);
            File.Copy(src, renamed);
            Check("사본: 원본을 옮기거나 이름을 바꿔도 다시 쓴다", VProxy.Find(renamed) == proxy, VProxy.Find(renamed));
            File.Delete(renamed);
            if (heavy)
            {
                if (File.Exists(src)) File.Delete(src);
                return;
            }

            // 언제 사본을 권하나: 10분 넘게 길거나, 1080p보다 크거나, 1080p를 1초에 50장 넘게 찍은 영상
            Func<int, int, double, double, MediaProbe> fake1 = (w, h, fps, sec) => new MediaProbe { HasVideo = true, Width = w, Height = h, FpsN = (uint)Math.Round(fps * 1000), FpsD = 1000, Seconds = sec };
            bool rule = !VProxy.Helps(fake1(1920, 1080, 30, 12)) && VProxy.Helps(fake1(1920, 1080, 30, 700)) && VProxy.Helps(fake1(3840, 2160, 30, 10))
                && VProxy.Helps(fake1(1920, 1080, 60, 10)) && !VProxy.Helps(fake1(1280, 720, 60, 10)) && !VProxy.Helps(fake1(1080, 1920, 30, 60)) && !VProxy.Helps(null);
            double es;
            long eb;
            VProxy.Estimate(fake1(1920, 1080, 30, 3000), out es, out eb);
            Check("사본 권하기: 긴·큰·60장 영상만, 짧은 1080p는 그대로", rule, "50분 수업 영상 어림값 " + Math.Round(es / 60) + "분 · " + Conversions.Size(eb));
            string sixty = P("proxy_60장.mp4");
            bool t60;
            MakeLongGop(sixty, 2, 60, out t60);
            var p60 = VMedia.Probe(sixty);
            Check("1초에 60장 1080p 파일을 읽으면 사본을 권한다", p60 != null && VProxy.Helps(p60) && VideoEditor.ProxyAdvice(new[] { new VClip { Path = sixty } }) != null, Say(p60) + " · " + VideoEditor.ProxyAdvice(new[] { new VClip { Path = sixty } }));
            Check("짧은 1080p 30장 파일은 권하지 않는다", VideoEditor.ProxyAdvice(new[] { new VClip { Path = src } }) == null, Say(ps));

            // 멈추기: 만들던 임시 파일이 남지 않는다
            VProxy.Drop(src);
            int calls = 0;
            string cancelErr = null;
            try
            {
                WinRtCall.Mta(() => VProxy.Make(src, null, () => ++calls > 40));
            }
            catch (Exception ex)
            {
                cancelErr = ex.GetType().Name;
            }
            var leftovers = Directory.GetFiles(VProxy.Folder, "*.tmp.mp4");
            Check("사본 만들기를 멈추면 임시 파일 없이 끝난다", cancelErr == "OperationCanceledException" && leftovers.Length == 0 && !File.Exists(VProxy.PathFor(src)),
                (cancelErr ?? "멈추지 않음") + " · 남은 임시 파일 " + leftovers.Length + "개");

            // 미리 보기만 사본을 읽고, 내보내기는 원본. 사본 자리에 일부러 다른 영상(한 가지 밝기)을 두고 확인한다.
            string fake = P("proxy_fake.mp4");
            {
                var enc = new VEncoder(fake, 960, 540, ProxyFps, 1000000, 0, null, 0, seconds, () => false);
                enc.Start();
                var flat = new byte[960 * 540 * 4];
                for (int i = 0; i < flat.Length; i += 4)
                {
                    flat[i] = flat[i + 1] = flat[i + 2] = 250;
                    flat[i + 3] = 255;
                }
                for (int i = 0; i < frames; i++) enc.AddVideo(flat, i, null);
                enc.Finish();
            }
            Check("(참고) 간격을 정하지 않으면 인코더 기본값", true, Spacing(KeyFrames(fake), out kmin, out kmax));
            // 사본 자리에 놓인 파일은 Make가 그대로 받아 쓴다(새로 만들지 않음)
            File.Copy(fake, VProxy.PathFor(src), true);
            VProxy.Make(src, null, () => false);
            var proj = new VProject { Aspect = "16:9", SourceW = 1920, SourceH = 1080, Fps = 30 };
            proj.Clips.Add(new VClip { Path = src, In = 0, Out = seconds });
            var zoomed = proj.Clone();
            zoomed.Clips[0].Zoom = 2.5;
            int previewLevel = -1, exportLevel = -1, zoomLevel = -1, brokenLevel = -1;
            bool dropped = false;
            WinRtCall.Mta<bool>(delegate
            {
                using (var r = new VRenderer(960, 540, false) { UseProxy = true })
                {
                    r.Use(proj, 1);
                    var px = r.Frame(2.0);
                    if (px != null) previewLevel = BlockLevel(px, 960, 540);
                    r.Use(zoomed, 2);
                    px = r.Frame(2.0);
                    if (px != null) zoomLevel = LevelAt(px, 960, 480, 270);
                }
                using (var r = new VRenderer(960, 540, true))
                {
                    r.Use(proj, 1);
                    var px = r.Frame(2.0);
                    if (px != null) exportLevel = BlockLevel(px, 960, 540);
                }
                // 깨진 사본: 지우고 원본으로 그린다
                VProxy.Drop(src);
                File.WriteAllBytes(VProxy.PathFor(src), new byte[4096]);
                VProxy.Make(src, null, () => false);
                using (var r = new VRenderer(960, 540, false) { UseProxy = true })
                {
                    r.Use(proj, 1);
                    var px = r.Frame(2.0);
                    if (px != null) brokenLevel = BlockLevel(px, 960, 540);
                }
                dropped = !File.Exists(VProxy.PathFor(src));
                return true;
            });
            int want = ProxyLevel(60);
            Check("미리 보기는 사본을 읽는다", Math.Abs(previewLevel - 250) <= 6, previewLevel.ToString());
            Check("크게 확대해 보면 원본으로 그린다(흐려지지 않게)", zoomLevel >= 0 && Math.Abs(zoomLevel - 250) > 20, zoomLevel.ToString());
            Check("내보내기는 늘 원본", Math.Abs(exportLevel - want) <= 3, exportLevel + " (원본 " + want + ")");
            Check("깨진 사본은 지우고 원본으로 그린다", Math.Abs(brokenLevel - want) <= 3 && dropped, brokenLevel + (dropped ? " · 지움" : " · 남음"));
            foreach (var f in new[] { fake, src }) if (File.Exists(f)) File.Delete(f);
        }
    }
}
