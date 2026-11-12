// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 자동 자막 시험(인터넷 안 씀). 시험 폴더에 미리 받아 둔 Whisper 파일(whisper_onnx\base·turbo)을 이 PC 안의 작은 서버로 내주어
// 받기·멈추기·이어 받기·확인·고치기를 보고, 사람이 만든 수업 녹음(whisper_test\lesson16k.wav와 정답 글 lesson.txt)을 글로 바꿔
// 글자 틀린 비율(CER, 띄어쓰기·문장부호 빼고)을 잰다. 'whisperturbo'는 1.1GB 모델이라 따로 부를 때만.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bakkum
{
    static partial class ToolTest
    {
        /// 이어 받기(Range)를 받아 주는 작은 서버. 받은 요청을 "경로 시작바이트"로 적어 둔다.
        sealed class RangeServer : IDisposable
        {
            readonly TcpListener door = new TcpListener(IPAddress.Loopback, 0);
            public readonly int Port;
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public readonly List<string> Asked = new List<string>();

            public RangeServer()
            {
                door.Start();
                Port = ((IPEndPoint)door.LocalEndpoint).Port;
                new Thread(Loop) { IsBackground = true }.Start();
            }

            void Loop()
            {
                while (true)
                {
                    TcpClient c;
                    try { c = door.AcceptTcpClient(); }
                    catch { return; }
                    var client = c;
                    new Thread(delegate () { Serve(client); }) { IsBackground = true }.Start();
                }
            }

            void Serve(TcpClient c)
            {
                try
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var head = new StringBuilder();
                        var one = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        {
                            if (s.Read(one, 0, 1) <= 0 || head.Length > 16384) return;
                            head.Append((char)one[0]);
                        }
                        var lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                        string path = lines[0].Split(' ')[1];
                        long from = 0;
                        foreach (var l in lines)
                        {
                            var m = Regex.Match(l, @"^Range:\s*bytes=(\d+)-", RegexOptions.IgnoreCase);
                            if (m.Success) from = long.Parse(m.Groups[1].Value);
                        }
                        lock (Asked) Asked.Add(path + " " + from);
                        byte[] body;
                        string answer;
                        if (!Files.TryGetValue(path, out body))
                        {
                            body = new byte[0];
                            from = 0;
                            answer = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                        }
                        else if (from > 0 && from < body.Length)
                            answer = "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + from + "-" + (body.Length - 1) + "/" + body.Length + "\r\nContent-Length: " + (body.Length - from) + "\r\nConnection: close\r\n\r\n";
                        else
                        {
                            from = 0;
                            answer = "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
                        }
                        var hb = Encoding.ASCII.GetBytes(answer);
                        s.Write(hb, 0, hb.Length);
                        for (long at = from; at < body.Length; at += 1 << 16) s.Write(body, (int)at, (int)Math.Min(1 << 16, body.Length - at));
                    }
                }
                catch { }
            }

            public void Dispose()
            {
                door.Stop();
            }
        }

        /// 16비트 PCM WAV를 한 줄 소리로(여러 줄이면 섞는다)
        static float[] ReadWav(string path, out int rate)
        {
            var b = File.ReadAllBytes(path);
            rate = 0;
            int channels = 1, bits = 16, pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                }
                else if (id == "data")
                {
                    if (bits != 16) throw new InvalidDataException("16비트 WAV만 읽어요.");
                    len = Math.Min(len, b.Length - body);
                    int frames = len / (2 * channels);
                    var x = new float[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        float sum = 0;
                        for (int ch = 0; ch < channels; ch++) sum += BitConverter.ToInt16(b, body + (i * channels + ch) * 2) / 32768f;
                        x[i] = sum / channels;
                    }
                    return x;
                }
                pos = body + len + (len & 1);
            }
            throw new InvalidDataException("WAV에서 소리를 찾지 못했어요.");
        }

        static string NormText(string s)
        {
            return Regex.Replace(s ?? "", "[^0-9A-Za-z가-힣]", "");
        }

        /// 글자 틀린 비율(띄어쓰기·문장부호 빼고, 레벤슈타인 거리 ÷ 정답 글자 수)
        static double Cer(string hyp, string truth)
        {
            string h = NormText(hyp), r = NormText(truth);
            if (r.Length == 0) return 1;
            var prev = new int[h.Length + 1];
            var cur = new int[h.Length + 1];
            for (int j = 0; j <= h.Length; j++) prev[j] = j;
            for (int i = 1; i <= r.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= h.Length; j++) cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
                var t = prev;
                prev = cur;
                cur = t;
            }
            return prev[h.Length] / (double)r.Length;
        }

        static bool SameFile(string a, string b)
        {
            if (!File.Exists(a) || !File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using (var x = File.OpenRead(a))
            using (var y = File.OpenRead(b))
            {
                var ba = new byte[1 << 20];
                var bb = new byte[1 << 20];
                while (true)
                {
                    int n = x.Read(ba, 0, ba.Length);
                    if (n == 0) return true;
                    int m = 0;
                    while (m < n)
                    {
                        int k = y.Read(bb, m, n - m);
                        if (k <= 0) return false;
                        m += k;
                    }
                    for (int i = 0; i < n; i++) if (ba[i] != bb[i]) return false;
                }
            }
        }

        static string Pct(double v)
        {
            return (v * 100).ToString("0.0") + "%";
        }

        static long PeakMb()
        {
            var p = Process.GetCurrentProcess();
            p.Refresh();
            return p.PeakWorkingSet64 / (1024 * 1024);
        }

        static string SegText(List<WSegment> segs)
        {
            return string.Join(" ", segs.Select(s => s.Text));
        }

        static bool InOrder(List<WSegment> segs, double length)
        {
            double last = -1;
            foreach (var s in segs)
            {
                if (s.Start < last - 0.01 || s.End < s.Start || s.Start < 0 || s.End > length + 1.5) return false;
                last = s.Start;
            }
            return true;
        }

        static string WFile(WhisperModel m, WhisperModel.Part p)
        {
            return Path.Combine(P("whisper_onnx"), m.Id, p.Path.Replace('/', '\\'));
        }

        static void WhisperTests(bool turbo)
        {
            Cli.Print(turbo ? "[자동 자막 · 정확하게(turbo)]" : "[자동 자막]");
            var b = WhisperModels.Base;
            var t = WhisperModels.Turbo;
            if (!Directory.Exists(P("whisper_onnx")) || !File.Exists(P(@"whisper_test\lesson16k.wav")))
            {
                Check("시험 재료(whisper_onnx·whisper_test)가 시험 폴더에 있다", false, P("whisper_onnx"));
                return;
            }
            string work = P("whispertest");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            int rate;
            float[] lesson = ReadWav(P(@"whisper_test\lesson16k.wav"), out rate);
            string lessonText = File.ReadAllText(P(@"whisper_test\lesson.txt"), Encoding.UTF8);
            double lessonSec = lesson.Length / (double)WhisperEngine.SampleRate;
            if (turbo)
            {
                TurboTests(t, lesson, lessonText);
                return;
            }

            // ── 모델 목록 ──
            bool listOk = WhisperModels.All.All(m => m.Parts.Length == 5 && m.Commit.Length == 40 && m.Parts.All(p => p.Sha256.Length == 64 && p.Size > 0));
            string url = WhisperModels.UrlOf(b, b.Parts[3]);
            Check("모델 목록: 받는 곳을 커밋으로 고정하고 파일마다 크기·SHA-256", listOk && url == "https://huggingface.co/onnx-community/whisper-base/resolve/27a07b4b52b3a4c5886134bb4320e93e1a4bbc7c/onnx/encoder_model_uint8.onnx", url);
            Check("크기 말: 정확하게 1.0GB · 빠르게 74MB", WhisperModels.Words(t.DownloadBytes) == "1.0GB" && WhisperModels.Words(b.DownloadBytes) == "74MB", WhisperModels.Words(t.DownloadBytes) + " · " + WhisperModels.Words(b.DownloadBytes));
            bool localSame = b.Parts.All(p => File.Exists(WFile(b, p)) && new FileInfo(WFile(b, p)).Length == p.Size && WhisperModels.Sha256Of(WFile(b, p)) == p.Sha256);
            Check("11-11에 받아 둔 base 파일이 목록의 크기·SHA-256과 같다", localSame, null);

            // ── 디코더 나누기·형식 번호 ──
            string merged = WFile(b, b.Parts[4]);
            string first = Path.Combine(work, "first.onnx"), next = Path.Combine(work, "next.onnx");
            var sw = Stopwatch.StartNew();
            OnnxPatch.SplitDecoder(merged, first, next);
            long splitMs = sw.ElapsedMilliseconds;
            bool same = SameFile(first, P(@"whisper_split\base_q\decoder_first_int8.onnx")) && SameFile(next, P(@"whisper_split\base_q\decoder_next_int8.onnx"));
            Check("디코더 나누기(C#)가 11-11 파이썬 결과와 바이트까지 같다", same, new FileInfo(first).Length + " · " + new FileInfo(next).Length + "바이트 · " + splitMs + "ms");
            Check("나눈 두 파일의 형식 번호가 9", OnnxPatch.IrVersion(first) == 9 && OnnxPatch.IrVersion(next) == 9, "원본 " + OnnxPatch.IrVersion(merged));

            string fake = Path.Combine(work, "fake.onnx");
            File.WriteAllBytes(fake, new byte[] { 0x12, 0x03, (byte)'a', (byte)'b', (byte)'c', 0x08, 0x0A, 0x3A, 0x02, 0x01, 0x02 });
            bool lowered = OnnxPatch.LowerIr(fake, 9);
            var after = File.ReadAllBytes(fake);
            bool rest = after.SequenceEqual(new byte[] { 0x12, 0x03, (byte)'a', (byte)'b', (byte)'c', 0x08, 0x09, 0x3A, 0x02, 0x01, 0x02 });
            bool again = OnnxPatch.LowerIr(fake, 9);
            string enc = WFile(b, b.Parts[3]);
            Check("형식 번호 낮추기: 그 자리만 바꾸고 나머지는 그대로, 낮으면 손대지 않는다", lowered && rest && !again && OnnxPatch.IrVersion(fake) == 9 && OnnxPatch.IrVersion(enc) == 7, "base 인코더 " + OnnxPatch.IrVersion(enc));

            // ── 받기(이 PC 안 서버) ──
            using (var server = new RangeServer())
            {
                foreach (var p in b.Parts) server.Files["/" + b.Repo + "/resolve/" + b.Commit + "/" + p.Path] = File.ReadAllBytes(WFile(b, p));
                WhisperModels.UrlBase = "http://127.0.0.1:" + server.Port + "/";
                try
                {
                    var none = new WhisperModel { Id = "none", Name = "없음", Repo = "test/none", Commit = b.Commit, License = "MIT", Parts = b.Parts.Take(1).ToArray(), Disk = 1, Memory = 1 };
                    string msg = null;
                    try { WhisperModels.Prepare(none, delegate { }, () => false); }
                    catch (UserFacingException ex) { msg = ex.Message; }
                    Check("받을 파일이 없으면 쉬운 말로 멈춘다", msg != null && msg.Contains("받지 못했어요"), msg);

                    var bad = new WhisperModel { Id = "bad", Name = "바뀐 파일", Repo = "test/bad", Commit = b.Commit, License = "MIT", Parts = b.Parts.Take(3).ToArray(), Disk = 1, Memory = 1 };
                    for (int i = 0; i < 3; i++)
                    {
                        var bytes = (byte[])File.ReadAllBytes(WFile(b, b.Parts[i])).Clone();
                        if (i == 2) bytes[bytes.Length / 2] ^= 0x20;
                        server.Files["/test/bad/resolve/" + b.Commit + "/" + b.Parts[i].Path] = bytes;
                    }
                    msg = null;
                    try { WhisperModels.Prepare(bad, delegate { }, () => false); }
                    catch (UserFacingException ex) { msg = ex.Message; }
                    string badPart = WhisperModels.Folder(bad) + ".받는중";
                    Check("받은 파일이 원래와 다르면 지우고 멈춘다(학교망이 바꾼 파일 막기)", msg != null && msg.Contains("원래 파일과 달라요") && !File.Exists(Path.Combine(badPart, "vocab.json")) && File.Exists(Path.Combine(badPart, "config.json")) && !WhisperModels.Ready(bad), msg);

                    int calls = 0;
                    bool cancelled = false;
                    try { WhisperModels.Prepare(b, delegate { }, () => ++calls > 150); }
                    catch (OperationCanceledException) { cancelled = true; }
                    string part = WhisperModels.Folder(b) + ".받는중";
                    string partEnc = Path.Combine(part, @"onnx\encoder_model_uint8.onnx");
                    long half = File.Exists(partEnc) ? new FileInfo(partEnc).Length : 0;
                    Check("받다가 멈추면 받던 파일을 남겨 둔다", cancelled && half > 0 && half < b.Parts[3].Size && !WhisperModels.Ready(b), "인코더 " + half + "/" + b.Parts[3].Size + "바이트");

                    lock (server.Asked) server.Asked.Clear();
                    double lastFrac = 0;
                    var steps = new List<string>();
                    sw.Restart();
                    WhisperModels.Prepare(b, delegate (string what, double f)
                    {
                        lastFrac = f;
                        lock (steps) if (!steps.Contains(what)) steps.Add(what);
                    }, () => false);
                    long prepMs = sw.ElapsedMilliseconds;
                    List<string> asked;
                    lock (server.Asked) asked = server.Asked.ToList();
                    bool resumed = asked.Any(a => a.Contains("encoder_model_uint8.onnx") && !a.EndsWith(" 0", StringComparison.Ordinal));
                    bool noRefetch = !asked.Any(a => a.Contains("/config.json") || a.Contains("/vocab.json"));
                    Check("다시 누르면 끊긴 곳부터 이어 받고, 다 받은 파일은 다시 받지 않는다", resumed && noRefetch && WhisperModels.Ready(b), string.Join(" | ", asked.Select(a => a.Substring(a.LastIndexOf('/') + 1))) + " · " + prepMs + "ms");
                    string folder = WhisperModels.Folder(b);
                    bool shaped = SameFile(Path.Combine(folder, "decoder_first.onnx"), P(@"whisper_split\base_q\decoder_first_int8.onnx"))
                        && SameFile(Path.Combine(folder, "decoder_next.onnx"), P(@"whisper_split\base_q\decoder_next_int8.onnx"))
                        && SameFile(Path.Combine(folder, "encoder.onnx"), enc) && !Directory.Exists(Path.Combine(folder, "onnx")) && !Directory.Exists(part);
                    Check("받은 모델: 디코더를 나누고 원래 큰 파일은 지웠다", shaped, string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName)));
                    Check("큰 파일은 설정 폴더가 아니라 이 PC 안쪽(캐시 폴더)에 둔다", folder.StartsWith(AppInfo.CacheDir, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(AppInfo.CacheDir) == "cache", folder.Substring(AppInfo.DataDir.Length));
                    Check("진행 글과 막대가 끝까지 간다", Math.Abs(lastFrac - 1) < 1e-9 && steps.Contains("자동 자막 모델을 받는 중") && steps.Contains("모델을 이 PC에 맞게 고치는 중"), string.Join(" → ", steps));

                    lock (server.Asked) server.Asked.Clear();
                    WhisperModels.Prepare(b, delegate { }, () => false);
                    Check("이미 받았으면 인터넷에 묻지 않는다", server.Asked.Count == 0, null);
                }
                finally
                {
                    WhisperModels.UrlBase = "https://huggingface.co/";
                }
            }
            Check("받아 둔 모델을 먼저 고른다", WhisperModels.Default() == b, WhisperModels.Default().Id);
            long used = WhisperModels.UsedBytes();
            Check("받은 모델 크기(지우기 단추에 보이는 값)", used > 100L * 1024 * 1024 && used < b.Disk + 20L * 1024 * 1024, WhisperModels.Words(used));

            // ── 글로 바꾸기 ──
            using (var eng = new WhisperEngine(WhisperModels.Folder(b)))
            {
                sw.Restart();
                var segs = eng.Transcribe(lesson, "ko", null, () => false);
                double sec = sw.Elapsed.TotalSeconds;
                double cer = Cer(SegText(segs), lessonText);
                Check("수업 녹음 " + Math.Round(lessonSec) + "초를 '빠르게'로: 글자 틀린 비율 20% 아래", cer <= 0.20, "CER " + Pct(cer) + " · " + sec.ToString("0.0") + "초 · 구간 " + segs.Count + " · 메모리 최대 " + PeakMb() + "MB");
                Check("구간 시각이 차례대로, 소리 길이 안에", segs.Count > 3 && InOrder(segs, lessonSec), segs.Count > 0 ? segs[0].Start.ToString("0.00") + "~" + segs[segs.Count - 1].End.ToString("0.00") + "초" : "");

                var quiet = new float[WhisperEngine.SampleRate * 40];
                var sq = eng.Transcribe(quiet, "ko", null, () => false);
                Check("고요한 40초: 자막을 지어내지 않고 30초 칸 둘을 건너뛴다", sq.Count == 0 && eng.SkippedWindows == 2 && eng.Windows == 0, sq.Count + "구간 · 건너뜀 " + eng.SkippedWindows);
                var rnd = new Random(7);
                var hiss = new float[WhisperEngine.SampleRate * 20];
                for (int i = 0; i < hiss.Length; i++) hiss[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.001);
                var sn = eng.Transcribe(hiss, "ko", null, () => false);
                Check("아주 작은 잡음(-60dB)에서도 자막이 생기지 않는다", sn.Count == 0, sn.Count + "구간");
                // 고요 20초 + 수업 → 앞 20초는 건너뛰고 시각은 20초 뒤로 밀린다
                var padded = new float[WhisperEngine.SampleRate * 31 + lesson.Length];
                Array.Copy(lesson, 0, padded, WhisperEngine.SampleRate * 31, lesson.Length);
                var sp = eng.Transcribe(padded, "ko", null, () => false);
                double cerP = Cer(SegText(sp), lessonText);
                Check("앞에 31초 고요가 있어도 첫 자막은 31초쯤에서 시작하고 내용은 같다", eng.SkippedWindows >= 1 && sp.Count > 0 && sp[0].Start > 30.6 && sp[0].Start < 32 && cerP <= 0.22, "첫 자막 " + (sp.Count > 0 ? sp[0].Start.ToString("0.00") : "-") + "초 · CER " + Pct(cerP) + " · 건너뜀 " + eng.SkippedWindows);

                var tone = new float[WhisperEngine.SampleRate * 5];
                for (int i = WhisperEngine.SampleRate * 2; i < WhisperEngine.SampleRate * 3; i++) tone[i] = (float)(0.3 * Math.Sin(i * 0.2));
                double ts = 0, te = 5;
                WhisperEngine.Tighten(tone, ref ts, ref te);
                Check("구간 앞뒤의 고요는 잘라 낸다(앞 0.15초·뒤 0.25초 여유)", Math.Abs(ts - 1.85) < 0.03 && Math.Abs(te - 3.25) < 0.03, ts.ToString("0.00") + "~" + te.ToString("0.00") + "초");

                int k = 0;
                bool stopped = false;
                try { eng.Transcribe(lesson, "ko", null, () => ++k > 20); }
                catch (OperationCanceledException) { stopped = true; }
                Check("바꾸는 중에 멈출 수 있다", stopped, null);

                // ── 편집 중인 영상의 소리(시간 막대 그대로) ──
                var proj = new VProject();
                proj.Clips.Add(new VClip { Kind = "color", Dur = 5 + lessonSec + 3 });
                proj.Sounds.Add(new VSound { Kind = "record", Path = P(@"whisper_test\lesson16k.wav"), Start = 5, In = 0, Out = lessonSec, Name = "수업 녹음" });
                proj.Sounds.Add(new VSound { Kind = "music", Path = P(@"whisper_test\lesson16k.wav"), Start = 0, In = 0, Out = lessonSec, Name = "배경 음악(시험)" });
                proj.Sounds.Add(new VSound { Kind = "effect", Path = P(@"whisper_test\lesson16k.wav"), Start = 1, In = 0, Out = 1, Name = "효과음(시험)" });
                proj.EndFade = true;
                var listen = VideoEditor.ListenCopy(proj);
                Check("자동 자막은 배경 음악·효과음을 빼고 듣는다(편집은 그대로)", listen.Sounds.Count == 1 && listen.Sounds[0].Kind == "record" && !listen.EndFade && proj.Sounds.Count == 3 && proj.EndFade, listen.Sounds.Count + "개");
                sw.Restart();
                var mixed = WhisperAudio.FromProject(listen, null, () => false);
                double mixSec = sw.Elapsed.TotalSeconds;
                // 5초 뒤에 놓인 수업 녹음과 얼마나 같은지(소리 크기 맞춤과 상관없게 상관계수로)
                int off = 5 * WhisperEngine.SampleRate, bestLag = 0;
                double best = -1;
                for (int lag = -40; lag <= 40; lag += 2)
                {
                    double sxy = 0, sxx = 0, syy = 0;
                    for (int i = WhisperEngine.SampleRate; i < lesson.Length - WhisperEngine.SampleRate; i += 3)
                    {
                        int j = off + i + lag;
                        if (j < 0 || j >= mixed.Length) continue;
                        double x = lesson[i], y = mixed[j];
                        sxy += x * y;
                        sxx += x * x;
                        syy += y * y;
                    }
                    double r = sxy / Math.Sqrt(Math.Max(1e-12, sxx * syy));
                    if (r > best)
                    {
                        best = r;
                        bestLag = lag;
                    }
                }
                double expectLen = proj.Duration * WhisperEngine.SampleRate;
                Check("시간 막대의 소리를 16kHz로 모으면 원래 녹음과 같다(어긋남 2ms 안)", best > 0.95 && Math.Abs(bestLag) <= 32 && Math.Abs(mixed.Length - expectLen) < 400, "상관 " + best.ToString("0.000") + " · 어긋남 " + bestLag + "칸 · 길이 " + mixed.Length + "/" + Math.Round(expectLen) + " · " + mixSec.ToString("0.0") + "초");
                var sm = eng.Transcribe(mixed, "ko", null, () => false);
                double cerM = Cer(SegText(sm), lessonText);
                Check("편집 소리로 만든 자막: 시각이 시간 막대 기준(5초 뒤)이고 내용도 맞다", sm.Count > 0 && sm[0].Start > 4.6 && sm[0].Start < 5.6 && cerM <= 0.20, "첫 자막 " + (sm.Count > 0 ? sm[0].Start.ToString("0.00") : "-") + "초 · CER " + Pct(cerM));

                var pieces = WhisperText.Pieces(sm);
                var made = VideoEditor.AutoSubItems(proj, pieces, 0);
                Check("자동 자막을 화면 자막으로: 한 줄 24자 안, 시간이 겹치지 않는다", made.Count >= sm.Count && made.All(i => i.Text.Length <= WhisperText.MaxLine && i.End > i.Start)
                    && made.Zip(made.Skip(1), (x, y) => y.Start >= x.End - 0.01).All(v => v), made.Count + "개 · 가장 긴 줄 " + made.Max(i => i.Text.Length) + "자");
            }

            // ── 자막 조각 나누기·넣기(모델 없이) ──
            var split = WhisperText.Split("오늘은 식물이 양분을 만드는 광합성에 대해 알아보겠습니다. 잎에 있는 엽록체가 햇빛을 받아요!");
            string joined = string.Concat(split).Replace(" ", "");
            Check("긴 말은 문장 끝에서 먼저, 그다음 가운데 빈칸에서 나눈다", split.Count == 3 && split.All(s => s.Length <= WhisperText.MaxLine) && joined == "오늘은식물이양분을만드는광합성에대해알아보겠습니다.잎에있는엽록체가햇빛을받아요!", string.Join(" / ", split));
            var nospace = WhisperText.Split(new string('가', 30));
            Check("빈칸이 없는 긴 말도 24자 안으로", nospace.Count == 2 && nospace.All(s => s.Length <= WhisperText.MaxLine), string.Join(" / ", nospace.Select(s => s.Length + "자")));
            var ps = WhisperText.Pieces(new List<WSegment>
            {
                new WSegment { Start = 1, End = 7, Text = "오늘은 식물이 양분을 만드는 광합성에 대해 알아보겠습니다. 잎을 봐요." },
                new WSegment { Start = 7.2, End = 7.4, Text = "네." }
            });
            Check("나눈 조각은 글자 수만큼 시간을 나누고, 너무 짧은 자막은 조금 늘린다", ps.Count == 4 && Math.Abs(ps[0].Start - 1) < 1e-9 && Math.Abs(ps[2].End - 7) < 1e-6 && ps[1].Start >= ps[0].End - 1e-9 && ps[3].End - ps[3].Start >= 0.79,
                string.Join(" | ", ps.Select(x => x.Start.ToString("0.00") + "~" + x.End.ToString("0.00") + " " + x.Text)));

            var vp = new VProject();
            vp.Clips.Add(new VClip { Kind = "color", Dur = 10 });
            var have = new VItem { Kind = "text", Subtitle = true, Text = "있던 자막", Start = 2, End = 4, X = 0.5, Y = 0.14 };
            vp.Items.Add(have);
            var pcs = new List<WhisperText.Piece>
            {
                new WhisperText.Piece { Start = 1, End = 3, Text = "겹치는 말" },
                new WhisperText.Piece { Start = 5, End = 6, Text = "빈 곳" },
                new WhisperText.Piece { Start = 6.5, End = 6.6, Text = "너무 짧음" },
                new WhisperText.Piece { Start = 9.5, End = 11, Text = "끝을 넘음" },
                new WhisperText.Piece { Start = 10.2, End = 11, Text = "영상 밖" }
            };
            var gap = VideoEditor.AutoSubItems(vp, pcs, 2);
            var all = VideoEditor.AutoSubItems(vp, pcs, 1);
            Check("빈 곳에만 넣기: 있는 자막과 겹치는 말은 빼고, 영상 끝에 맞추고, 모양은 있던 자막을 따른다", gap.Count == 2 && gap[0].Text == "빈 곳" && Math.Abs(gap[1].End - 10) < 1e-9 && gap.All(i => i.Subtitle && Math.Abs(i.Y - 0.14) < 1e-9),
                string.Join(", ", gap.Select(i => i.Text + " " + i.Start + "~" + i.End)));
            Check("모두 바꾸기: 겹치는 말도 넣는다", all.Count == 3 && all[0].Text == "겹치는 말", all.Count + "개");

            WhisperModels.DeleteAll();
            Check("받은 모델 지우기", WhisperModels.UsedBytes() == 0 && !WhisperModels.Ready(b) && WhisperModels.Default() == t, WhisperModels.Default().Id);
        }

        /// 정확하게(turbo): 시험 폴더 파일을 복사해 고치고(인터넷 안 씀) 두 녹음의 CER을 잰다
        static void TurboTests(WhisperModel t, float[] lesson, string lessonText)
        {
            WhisperModels.LocalSource = p => WFile(t, p);
            try
            {
                var sw = Stopwatch.StartNew();
                WhisperModels.Prepare(t, delegate { }, () => false);
                long prepMs = sw.ElapsedMilliseconds;
                string folder = WhisperModels.Folder(t);
                bool shaped = SameFile(Path.Combine(folder, "decoder_first.onnx"), P(@"whisper_split\turbo_q2\decoder_first_int8.onnx"))
                    && SameFile(Path.Combine(folder, "decoder_next.onnx"), P(@"whisper_split\turbo_q2\decoder_next_int8.onnx"))
                    && SameFile(Path.Combine(folder, "encoder.onnx"), P(@"whisper_split\turbo_q2\encoder_model_uint8.onnx"));
                Check("turbo 준비(복사·확인·나누기·형식 번호 10→9)가 11-11 파이썬 결과와 같다", WhisperModels.Ready(t) && shaped && OnnxPatch.IrVersion(Path.Combine(folder, "encoder.onnx")) == 9, (prepMs / 1000.0).ToString("0.0") + "초 · " + WhisperModels.Words(WhisperModels.UsedBytes()));
                long afterPrep = PeakMb();
                using (var eng = new WhisperEngine(folder))
                {
                    var me = Process.GetCurrentProcess();
                    me.Refresh();
                    Cli.Print("       메모리: 준비까지 최대 " + afterPrep + "MB · 모델 연 뒤 " + me.WorkingSet64 / (1024 * 1024) + "MB");
                    sw.Restart();
                    var segs = eng.Transcribe(lesson, "ko", null, () => false);
                    double sec = sw.Elapsed.TotalSeconds;
                    double cer = Cer(SegText(segs), lessonText);
                    double len = lesson.Length / (double)WhisperEngine.SampleRate;
                    Check("수업 녹음 " + Math.Round(len) + "초를 '정확하게'로: 글자 틀린 비율 15% 아래", cer <= 0.15 && InOrder(segs, len), "CER " + Pct(cer) + " · " + sec.ToString("0.0") + "초(영상 길이의 " + (sec / len).ToString("0.00") + "배) · 메모리 최대 " + PeakMb() + "MB");
                    string reelWav = P(@"whisper_test\reel16k.wav"), reelTxt = P(@"whisper_test\reel.txt");
                    if (File.Exists(reelWav) && File.Exists(reelTxt))
                    {
                        int rate;
                        var reel = ReadWav(reelWav, out rate);
                        sw.Restart();
                        var rs = eng.Transcribe(reel, "ko", null, () => false);
                        double rcer = Cer(SegText(rs), File.ReadAllText(reelTxt, Encoding.UTF8));
                        Check("빠른 말 영상(배경 음악 섞임)을 '정확하게'로: 글자 틀린 비율 15% 아래", rcer <= 0.15, "CER " + Pct(rcer) + " · " + sw.Elapsed.TotalSeconds.ToString("0.0") + "초");
                    }
                }
            }
            finally
            {
                WhisperModels.LocalSource = null;
                try { WhisperModels.DeleteAll(); } catch { }
            }
            Check("받은 모델 지우기(1GB 비우기)", WhisperModels.UsedBytes() == 0, null);
        }
    }
}
