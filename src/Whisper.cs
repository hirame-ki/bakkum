// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 자동 자막: OpenAI Whisper(Hugging Face onnx-community의 8비트 ONNX 파일)를 윈도우에 들어 있는 Windows ML로 이 PC 안에서 돌린다.
// exe를 따로 켜지 않고, 처음 쓸 때 한 번만 받는다(받는 곳·크기를 먼저 보여 준다). 받은 뒤에는 인터넷 없이 돌아가고 소리는 PC 밖으로 나가지 않는다.
// 로그멜·타임스탬프 규칙·구간 나누기는 OpenAI whisper(MIT)를, 바이트 BPE 풀기는 GPT-2를 따른다(2026-11-11 시험판을 옮김).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WinML = Windows.AI.MachineLearning;

namespace Bakkum
{
    /// 받아 쓰는 Whisper 모델 하나(받는 곳은 커밋으로 고정하고, 파일마다 SHA-256으로 확인한다)
    sealed class WhisperModel
    {
        public sealed class Part
        {
            public string Path, Sha256;
            public long Size;
        }

        public string Id, Name, Repo, Commit, License, Note;
        /// 이 PC에 맞게 고친 뒤 디스크에 남는 크기(어림)
        public long Disk;
        /// 돌릴 때 메모리(어림, 11-11 실측)
        public long Memory;
        public Part[] Parts;

        public long DownloadBytes
        {
            get { return Parts.Sum(p => p.Size); }
        }
    }

    static class WhisperModels
    {
        const long MB = 1024 * 1024;
        /// 고친 방법이 바뀌면 올린다(받아 둔 모델을 다시 고치게)
        const int PatchVersion = 1;
        /// 시험용: 받는 주소 앞부분
        internal static string UrlBase = "https://huggingface.co/";
        /// 시험용: 받지 않고 이 PC의 파일을 복사한다(경로를 돌려주면)
        internal static Func<WhisperModel.Part, string> LocalSource;

        static WhisperModel.Part P(string path, long size, string sha)
        {
            return new WhisperModel.Part { Path = path, Size = size, Sha256 = sha };
        }

        public static readonly WhisperModel Turbo = new WhisperModel
        {
            Id = "turbo",
            Name = "정확하게",
            Repo = "onnx-community/whisper-large-v3-turbo",
            Commit = "360ebcde2559d60bb474678be3c1de9ef347d01a",
            License = "MIT",
            Note = "글자를 잘 맞혀요(시험 수업 녹음에서 글자 100개 중 10개쯤 틀림). 이 PC에서 10분 영상에 3분쯤 걸렸고, 메모리를 3GB 가까이 써요.",
            Disk = 936 * MB,
            Memory = 2800 * MB,
            Parts = new[]
            {
                P("config.json", 1332, "35cd83669f75bc2867f3b3a4461850392d5e308cd6ea951c3700539883c28df1"),
                P("generation_config.json", 3897, "16f95291d2f47c944d3c2b19390bba7965666555c1ea2a0bdc850d1fab45612f"),
                P("vocab.json", 1036558, "e2aa043ef015641d363d8288e7c241c85e36a5c761fb303598e0710233344387"),
                P("onnx/encoder_model_uint8.onnx", 644822195, "d2f853dc3254fdc0079f55dd4433ea716ac98ec5574d3b475f288f2a77cebba9"),
                P("onnx/decoder_model_merged_int8.onnx", 439936716, "61481bd3be3a445d5a4b9070e8f8b2c6cc4fbbbbdc9f0e7ed048a132b8b84e0d"),
            }
        };

        public static readonly WhisperModel Base = new WhisperModel
        {
            Id = "base",
            Name = "빠르게",
            Repo = "onnx-community/whisper-base",
            Commit = "27a07b4b52b3a4c5886134bb4320e93e1a4bbc7c",
            License = "Apache-2.0",
            Note = "빨라요(이 PC에서 10분 영상에 1분 안쪽). 대신 더 많이 틀려요(시험 수업 녹음에서 글자 100개 중 15개쯤).",
            Disk = 122 * MB,
            Memory = 800 * MB,
            Parts = new[]
            {
                P("config.json", 2243, "f4d0608f7d918166da7edb3e188de5ef1bfe70d9802e785d271fd88111e9cf4b"),
                P("generation_config.json", 3832, "61070cf8de25b1e9256e8e102ded49d8d24a8369ed36ef84fdf21549e68125a0"),
                P("vocab.json", 1036584, "50d6a919f0a0601d56a04eb583c780d18553aa388254ba3158eb6a00f13e2c1a"),
                P("onnx/encoder_model_uint8.onnx", 23201314, "5862993336bf33acd23736071aae2b32261d3b1b2f37780194460d4ef974dd46"),
                P("onnx/decoder_model_merged_int8.onnx", 53693315, "fa3ef9902734ce5ae6f9ef2bdb2ba9a6c4b5785b09f4f420ce036573dc9d090b"),
            }
        };

        public static readonly WhisperModel[] All = { Turbo, Base };

        public static WhisperModel Find(string id)
        {
            return All.FirstOrDefault(m => m.Id == id) ?? Turbo;
        }

        public static string Folder(WhisperModel m)
        {
            return Path.Combine(AppInfo.CacheDir, "whisper", m.Id);
        }

        static string PartialFolder(WhisperModel m)
        {
            return Folder(m) + ".받는중";
        }

        public static string UrlOf(WhisperModel m, WhisperModel.Part p)
        {
            return UrlBase + m.Repo + "/resolve/" + m.Commit + "/" + p.Path;
        }

        static readonly string[] Needed = { "config.json", "generation_config.json", "vocab.json", "encoder.onnx", "decoder_first.onnx", "decoder_next.onnx" };

        /// 받아서 고쳐 둔 모델이 있는지
        public static bool Ready(WhisperModel m)
        {
            try
            {
                string d = Folder(m);
                string mark = Path.Combine(d, "ready.json");
                if (!File.Exists(mark)) return false;
                string text = File.ReadAllText(mark);
                if (text.IndexOf(m.Commit, StringComparison.Ordinal) < 0 || text.IndexOf("\"patch\":" + PatchVersion, StringComparison.Ordinal) < 0) return false;
                return Needed.All(n => File.Exists(Path.Combine(d, n)));
            }
            catch
            {
                return false;
            }
        }

        /// 받은 모델이 디스크에 쓰는 크기(받다 만 것 포함)
        public static long UsedBytes()
        {
            long sum = 0;
            try
            {
                string root = Path.Combine(AppInfo.CacheDir, "whisper");
                if (Directory.Exists(root))
                    foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) sum += new FileInfo(f).Length;
            }
            catch { }
            return sum;
        }

        /// 받은 모델과 받다 만 것을 모두 지운다
        public static void DeleteAll()
        {
            string root = Path.Combine(AppInfo.CacheDir, "whisper");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        /// 사람에게 보여 줄 크기(1GB 넘으면 GB로)
        public static string Words(long bytes)
        {
            if (bytes >= 1000L * MB) return (bytes / (1024.0 * MB)).ToString("0.0", CultureInfo.InvariantCulture) + "GB";
            return Math.Max(1, Math.Round(bytes / (double)MB)) + "MB";
        }

        public static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(path))
            {
                var h = sha.ComputeHash(s);
                var sb = new StringBuilder(64);
                foreach (byte x in h) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        /// 받기 → 확인 → 이 PC에 맞게 고치기. report(할 일, 0~1). 멈추면 OperationCanceledException(받던 파일은 남겨 두어 다음에 이어 받는다).
        public static void Prepare(WhisperModel m, Action<string, double> report, Func<bool> stop)
        {
            if (Ready(m)) return;
            string dir = Folder(m), part = PartialFolder(m);
            Directory.CreateDirectory(part);
            long have = 0;
            foreach (var p in m.Parts)
            {
                string f = Path.Combine(part, p.Path.Replace('/', '\\'));
                if (File.Exists(f)) have += Math.Min(p.Size, new FileInfo(f).Length);
            }
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(part)));
                long need = m.DownloadBytes - have + m.Disk + 300 * MB;
                if (drive.IsReady && drive.AvailableFreeSpace < need)
                    throw new UserFacingException("디스크 공간이 모자라요. 자동 자막 모델을 받고 고치려면 약 " + Words(need) + "가 비어 있어야 해요(지금 " + Words(drive.AvailableFreeSpace) + ").");
            }
            catch (UserFacingException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Write("자동 자막: 빈 공간 확인 실패 " + ex.GetType().Name);
            }
            long total = m.DownloadBytes, before = 0;
            foreach (var p in m.Parts)
            {
                if (stop()) throw new OperationCanceledException();
                string dst = Path.Combine(part, p.Path.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                long start = before;
                Action<long> got = delegate (long n) { report("자동 자막 모델을 받는 중", 0.9 * (start + n) / total); };
                bool done = File.Exists(dst) && new FileInfo(dst).Length == p.Size && Sha256Of(dst) == p.Sha256;
                if (!done)
                {
                    if (File.Exists(dst) && new FileInfo(dst).Length >= p.Size) File.Delete(dst);
                    string local = LocalSource != null ? LocalSource(p) : null;
                    if (local != null) File.Copy(local, dst, true);
                    else Fetch(UrlOf(m, p), dst, p.Size, got, stop);
                    report("받은 파일을 확인하는 중", 0.9 * (before + p.Size) / total);
                    if (Sha256Of(dst) != p.Sha256)
                    {
                        File.Delete(dst);
                        throw new UserFacingException("받은 파일이 원래 파일과 달라요(" + Path.GetFileName(p.Path) + "). 다시 눌러 주세요. 계속 그러면 학교 인터넷이 파일을 바꾸는지 확인해 주세요.");
                    }
                }
                before += p.Size;
            }
            report("모델을 이 PC에 맞게 고치는 중", 0.92);
            string merged = Path.Combine(part, "onnx", "decoder_model_merged_int8.onnx");
            string enc = Path.Combine(part, "onnx", "encoder_model_uint8.onnx");
            OnnxPatch.SplitDecoder(merged, Path.Combine(part, "decoder_first.onnx"), Path.Combine(part, "decoder_next.onnx"));
            if (stop()) throw new OperationCanceledException();
            report("모델을 이 PC에 맞게 고치는 중", 0.98);
            string encDst = Path.Combine(part, "encoder.onnx");
            if (File.Exists(encDst)) File.Delete(encDst);
            File.Move(enc, encDst);
            OnnxPatch.LowerIr(encDst, OnnxPatch.MaxIr);
            File.Delete(merged);
            try { Directory.Delete(Path.Combine(part, "onnx")); } catch { }
            // 나누기에 쓴 큰 배열(turbo는 440MB)을 바로 비운다. 곧 모델을 열 때 메모리가 겹치지 않게(11-12 실측: 안 비우면 최대 3.8GB).
            GC.Collect();
            GC.WaitForPendingFinalizers();
            File.WriteAllText(Path.Combine(part, "ready.json"), "{\"repo\":\"" + m.Repo + "\",\"commit\":\"" + m.Commit + "\",\"patch\":" + PatchVersion + "}");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(part, dir);
            report("다 받았어요", 1);
        }

        // 받기가 막혔을 때 할 일. 모델 파일은 huggingface.co에서 hf.co(CDN)로 넘겨 받는다(11-12 확인: us.aws.cdn.hf.co).
        const string AskAllow = "정보 담당 선생님께 huggingface.co와 hf.co 주소를 열어 달라고 부탁해 주세요.";
        internal const string Blocked = "학교 인터넷에서 자동 자막 모델 받기가 막힌 것 같아요. " + AskAllow;

        /// 받기 실패를 쉬운 말로(학교 인터넷에서 막혔으면 누구에게 무엇을 부탁할지까지)
        internal static string Explain(WebException ex)
        {
            var hr = ex.Response as HttpWebResponse;
            int code = hr != null ? (int)hr.StatusCode : 0;
            if (code == 403) return Blocked;
            if (code == 407) return "학교 인터넷이 따로 로그인(프록시 인증)을 요구해서 받지 못했어요. 정보 담당 선생님께 huggingface.co와 hf.co 주소를 로그인 없이 쓸 수 있게 해 달라고 부탁해 주세요.";
            // 보안 장비가 연결을 가로채면 인증서를 못 믿거나(TrustFailure) 보안 연결 자체가 끊긴다(SecureChannelFailure)
            if (ex.Status == WebExceptionStatus.TrustFailure || ex.Status == WebExceptionStatus.SecureChannelFailure)
                return "보안 연결을 확인하지 못했어요(인증서 문제). 먼저 이 PC의 날짜·시간이 맞는지 확인해 주세요. 맞는데도 안 되면 학교 인터넷의 보안 장비가 연결을 검사하는 것 같아요. 정보 담당 선생님께 huggingface.co와 hf.co 주소를 검사에서 빼 달라고 부탁해 주세요.";
            if (ex.Status == WebExceptionStatus.NameResolutionFailure) return "huggingface.co 주소를 찾지 못했어요. 인터넷이 연결되어 있는지 확인해 주세요. 다른 사이트는 열린다면 학교 인터넷에서 막은 것이니, " + AskAllow;
            return "자동 자막 모델을 받지 못했어요. 인터넷 연결을 확인하고 다시 눌러 주세요(받던 것은 이어서 받아요). 계속 안 되면 " + AskAllow;
        }

        /// 파일 하나 받기. 받다 만 파일이 있으면 이어서 받는다(서버가 이어 주지 않으면 처음부터).
        static void Fetch(string url, string dst, long size, Action<long> got, Func<bool> stop)
        {
            Web.EnsureTls();
            long have = File.Exists(dst) ? new FileInfo(dst).Length : 0;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = AppInfo.Id + "/" + AppInfo.Version;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            // 멈출 때 남은 답을 끝까지 읽어 치우지 않게(연결을 다시 쓰지 않는다)
            req.KeepAlive = false;
            if (have > 0) req.AddRange(have);
            try
            {
                using (var r = (HttpWebResponse)req.GetResponse())
                {
                    bool append = have > 0 && r.StatusCode == HttpStatusCode.PartialContent;
                    long done = append ? have : 0;
                    // 차단 안내 페이지(200 HTML)나 길이가 다른 답이면 받지 않는다(받던 파일도 그대로 둔다)
                    if ((r.ContentType ?? "").StartsWith("text/html", StringComparison.OrdinalIgnoreCase) || r.ContentLength >= 0 && r.ContentLength != size - done)
                    {
                        Log.Write("자동 자막 모델 받기: 다른 답 " + r.ContentType + " " + r.ContentLength + "/" + (size - done));
                        throw new UserFacingException(Blocked);
                    }
                    using (var s = r.GetResponseStream())
                    using (var f = new FileStream(dst, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
                    {
                        var buf = new byte[1 << 16];
                        int n;
                        int tick = Environment.TickCount;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (stop())
                            {
                                try { req.Abort(); }
                                catch { }
                                throw new OperationCanceledException();
                            }
                            done += n;
                            if (done > size) throw new UserFacingException(Blocked);
                            f.Write(buf, 0, n);
                            if (Environment.TickCount - tick > 250)
                            {
                                tick = Environment.TickCount;
                                got(done);
                            }
                        }
                    }
                    got(done);
                    if (done != size) throw new UserFacingException("받기가 중간에 끊겼어요. [자동 자막 만들기]를 다시 누르면 이어서 받아요.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UserFacingException)
            {
                throw;
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.RequestCanceled && stop()) throw new OperationCanceledException();
                var hr = ex.Response as HttpWebResponse;
                Log.Write("자동 자막 모델 받기 실패: " + ex.Status + (hr != null ? " " + (int)hr.StatusCode : ""));
                throw new UserFacingException(Explain(ex));
            }
            catch (IOException ex)
            {
                Log.Write("자동 자막 모델 받기 끊김: " + ex.GetType().Name);
                throw new UserFacingException("받기가 중간에 끊겼어요. [자동 자막 만들기]를 다시 누르면 이어서 받아요.");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatus
        {
            public uint Length, Load;
            public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatus s);

        /// 이 PC의 메모리(바이트). 모르면 0.
        public static void MemoryInfo(out long total, out long available)
        {
            total = available = 0;
            try
            {
                var s = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
                if (!GlobalMemoryStatusEx(ref s)) return;
                total = (long)s.TotalPhys;
                available = (long)s.AvailPhys;
            }
            catch { }
        }

        /// 처음 고를 모델: 받아 둔 것이 있으면 그것, 없으면 메모리가 넉넉한 PC(8GB 이상)는 '정확하게'
        public static WhisperModel Default()
        {
            foreach (var m in All) if (Ready(m)) return m;
            long total, avail;
            MemoryInfo(out total, out avail);
            return total > 0 && total < 7L * 1024 * MB ? Base : Turbo;
        }
    }

    sealed class WSegment
    {
        public double Start, End;
        public string Text;
    }

    /// Whisper 하나를 열어 두고 소리를 글자로 바꾼다(뒤 스레드, MTA에서)
    sealed class WhisperEngine : IDisposable
    {
        public const int SampleRate = 16000;
        const int NFft = 400, Hop = 160, ChunkFrames = 3000;
        const double TimePrecision = 0.02;
        /// 25ms 칸이 이보다 크면 소리가 있다고 본다(-45dBFS)
        public const float LoudRms = 0.0056f;

        int nMels, vocabSize, sot, eot, tsBegin, taskTranscribe, maxInitial;
        readonly Dictionary<string, int> langs = new Dictionary<string, int>();
        readonly List<int> suppress = new List<int>(), beginSuppress = new List<int>();
        string[] idToToken;
        Dictionary<char, byte> byteDecoder;
        float[] melFilters;
        WinML.LearningModelSession encoder, decFirst, decNext;
        string encIn, encOut, logitsName = "logits", firstIds, firstHidden, nextIds;
        readonly List<string> pastNames = new List<string>();

        /// 지난 바꾸기에서 소리가 없어 건너뛴 30초 칸 수(시험용)
        public int SkippedWindows;
        public int Windows;

        public WhisperEngine(string dir)
        {
            LoadSettings(dir);
            List<string> encIns, encOuts, firstIns, firstOuts, nextIns, nextOuts;
            try
            {
                encoder = Load(Path.Combine(dir, "encoder.onnx"), out encIns, out encOuts);
                decFirst = Load(Path.Combine(dir, "decoder_first.onnx"), out firstIns, out firstOuts);
                decNext = Load(Path.Combine(dir, "decoder_next.onnx"), out nextIns, out nextOuts);
            }
            catch (Exception ex)
            {
                Dispose();
                Log.Write("자동 자막: 모델 열기 실패 " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                if (ex is OutOfMemoryException || ex.HResult == unchecked((int)0x8007000E)) throw new UserFacingException("메모리가 모자라 자동 자막 모델을 열지 못했어요. 다른 프로그램을 닫거나 '빠르게' 모델을 골라 주세요.");
                throw new UserFacingException("자동 자막 모델을 열지 못했어요. [받은 모델 지우기]를 누른 뒤 다시 받아 주세요. (" + ex.GetType().Name + ")");
            }
            encIn = encIns.Count > 0 ? encIns[0] : null;
            encOut = encOuts.Count > 0 ? encOuts[0] : null;
            foreach (string f in firstIns)
            {
                if (f.Contains("input_ids")) firstIds = f;
                else if (f.Contains("encoder_hidden_states")) firstHidden = f;
            }
            foreach (string f in nextIns)
            {
                if (f.StartsWith("past_key_values.", StringComparison.Ordinal)) pastNames.Add(f);
                else if (f.Contains("input_ids")) nextIds = f;
            }
            if (encIn == null || encOut == null || firstIds == null || firstHidden == null || nextIds == null || pastNames.Count == 0 || !firstOuts.Contains(logitsName) || !nextOuts.Contains(logitsName))
            {
                Dispose();
                throw new UserFacingException("자동 자막 모델 모양이 예상과 달라요. [받은 모델 지우기]를 누른 뒤 다시 받아 주세요.");
            }
        }

        /// 모델 하나를 연다. 들어가고 나오는 이름은 세션이 모델을 넘겨받기 전에 읽어 둔다.
        static WinML.LearningModelSession Load(string path, out List<string> inputs, out List<string> outputs)
        {
            var model = WinML.LearningModel.LoadFromFilePath(path);
            inputs = model.InputFeatures.Select(f => f.Name).ToList();
            outputs = model.OutputFeatures.Select(f => f.Name).ToList();
            var options = new WinML.LearningModelSessionOptions();
            try
            {
                // 세션이 모델을 복사하지 않고 넘겨받아 같은 내용을 메모리에 두 번 두지 않게
                options.CloseModelOnSessionCreation = true;
            }
            catch { }
            return new WinML.LearningModelSession(model, new WinML.LearningModelDevice(WinML.LearningModelDeviceKind.Cpu), options);
        }

        public void Dispose()
        {
            foreach (var s in new[] { encoder, decFirst, decNext })
            {
                try
                {
                    if (s != null) s.Dispose();
                }
                catch { }
            }
            encoder = decFirst = decNext = null;
            GC.Collect();
        }

        // ── 설정과 단어표 ──

        static Dictionary<string, object> Json(string path)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
        }

        static int Int(object o)
        {
            return Convert.ToInt32(o, CultureInfo.InvariantCulture);
        }

        void LoadSettings(string dir)
        {
            var cfg = Json(Path.Combine(dir, "config.json"));
            nMels = Int(cfg["num_mel_bins"]);
            vocabSize = Int(cfg["vocab_size"]);
            var gen = Json(Path.Combine(dir, "generation_config.json"));
            sot = Int(gen["decoder_start_token_id"]);
            eot = Int(gen["eos_token_id"]);
            tsBegin = Int(gen["no_timestamps_token_id"]) + 1;
            foreach (var kv in (Dictionary<string, object>)gen["lang_to_id"]) langs[kv.Key] = Int(kv.Value);
            taskTranscribe = Int(((Dictionary<string, object>)gen["task_to_id"])["transcribe"]);
            maxInitial = gen.ContainsKey("max_initial_timestamp_index") ? Int(gen["max_initial_timestamp_index"]) : 50;
            foreach (object o in (object[])gen["suppress_tokens"]) suppress.Add(Int(o));
            foreach (object o in (object[])gen["begin_suppress_tokens"]) beginSuppress.Add(Int(o));
            idToToken = new string[vocabSize];
            foreach (var kv in Json(Path.Combine(dir, "vocab.json")))
            {
                int id = Int(kv.Value);
                if (id >= 0 && id < vocabSize) idToToken[id] = kv.Key;
            }
            // GPT-2의 바이트 ↔ 글자 표
            var bs = new List<int>();
            for (int b = '!'; b <= '~'; b++) bs.Add(b);
            for (int b = 0xA1; b <= 0xAC; b++) bs.Add(b);
            for (int b = 0xAE; b <= 0xFF; b++) bs.Add(b);
            var cs = new List<int>(bs);
            int extra = 0;
            for (int b = 0; b < 256; b++)
            {
                if (bs.Contains(b)) continue;
                bs.Add(b);
                cs.Add(256 + extra);
                extra++;
            }
            byteDecoder = new Dictionary<char, byte>();
            for (int i = 0; i < bs.Count; i++) byteDecoder[(char)cs[i]] = (byte)bs[i];
            melFilters = MelFilters(nMels);
        }

        string Text(List<int> tokens, int from, int to)
        {
            var bytes = new List<byte>();
            for (int i = from; i < to; i++)
            {
                int t = tokens[i];
                if (t >= eot || t < 0 || idToToken[t] == null) continue;
                foreach (char c in idToToken[t])
                {
                    byte b;
                    if (byteDecoder.TryGetValue(c, out b)) bytes.Add(b);
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
        }

        // ── 로그멜(OpenAI whisper/audio.py) ──

        static double HzToMel(double f)
        {
            const double fsp = 200.0 / 3, minLogHz = 1000;
            double minLogMel = minLogHz / fsp, logstep = Math.Log(6.4) / 27.0;
            return f >= minLogHz ? minLogMel + Math.Log(f / minLogHz) / logstep : f / fsp;
        }

        static double MelToHz(double m)
        {
            const double fsp = 200.0 / 3, minLogHz = 1000;
            double minLogMel = minLogHz / fsp, logstep = Math.Log(6.4) / 27.0;
            return m >= minLogMel ? minLogHz * Math.Exp(logstep * (m - minLogMel)) : fsp * m;
        }

        /// librosa.filters.mel(sr=16000, n_fft=400, n_mels), Slaney 눈금과 넓이 맞춤
        static float[] MelFilters(int nm)
        {
            int bins = NFft / 2 + 1;
            double lo = HzToMel(0), hi = HzToMel(SampleRate / 2.0);
            var melF = new double[nm + 2];
            for (int i = 0; i < nm + 2; i++) melF[i] = MelToHz(lo + (hi - lo) * i / (nm + 1));
            var w = new float[nm * bins];
            for (int m = 0; m < nm; m++)
            {
                double fd0 = melF[m + 1] - melF[m], fd1 = melF[m + 2] - melF[m + 1], enorm = 2.0 / (melF[m + 2] - melF[m]);
                for (int k = 0; k < bins; k++)
                {
                    double f = k * (double)SampleRate / NFft;
                    double lower = (f - melF[m]) / fd0, upper = (melF[m + 2] - f) / fd1;
                    w[m * bins + k] = (float)(Math.Max(0, Math.Min(lower, upper)) * enorm);
                }
            }
            return w;
        }

        /// 소리 뒤에 30초 고요를 붙인 로그멜. mel[m * frames + f].
        float[] LogMel(float[] audio, out int frames)
        {
            int len = audio.Length + ChunkFrames * Hop;
            int nf = len / Hop;
            frames = nf;
            int bins = NFft / 2 + 1, nm = nMels;
            var win = new float[NFft];
            for (int i = 0; i < NFft; i++) win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / NFft));
            var cosT = new float[bins * NFft];
            var sinT = new float[bins * NFft];
            for (int k = 0; k < bins; k++)
                for (int n = 0; n < NFft; n++)
                {
                    double a = 2 * Math.PI * ((long)k * n % NFft) / NFft;
                    cosT[k * NFft + n] = (float)Math.Cos(a);
                    sinT[k * NFft + n] = (float)Math.Sin(a);
                }
            var mel = new float[(long)nm * nf];
            float[] filters = melFilters;
            int alen = audio.Length;
            Parallel.For(0, nf, () => new float[NFft + bins], (f, state, buf) =>
            {
                bool any = false;
                for (int n = 0; n < NFft; n++)
                {
                    int idx = f * Hop - NFft / 2 + n;
                    if (idx < 0) idx = -idx;
                    if (idx >= len) idx = 2 * (len - 1) - idx;
                    float v = idx < alen ? audio[idx] * win[n] : 0f;
                    buf[n] = v;
                    if (v != 0) any = true;
                }
                if (!any)
                {
                    float floor = (float)Math.Log10(1e-10);
                    for (int m = 0; m < nm; m++) mel[(long)m * nf + f] = floor;
                    return buf;
                }
                for (int k = 0; k < bins; k++)
                {
                    float re = 0, im = 0;
                    int row = k * NFft;
                    for (int n = 0; n < NFft; n++)
                    {
                        re += buf[n] * cosT[row + n];
                        im += buf[n] * sinT[row + n];
                    }
                    buf[NFft + k] = re * re + im * im;
                }
                for (int m = 0; m < nm; m++)
                {
                    float s = 0;
                    int row = m * bins;
                    for (int k = 0; k < bins; k++) s += filters[row + k] * buf[NFft + k];
                    mel[(long)m * nf + f] = (float)Math.Log10(Math.Max(s, 1e-10));
                }
                return buf;
            }, buf => { });
            float top = float.NegativeInfinity;
            for (long i = 0; i < mel.LongLength; i++) if (mel[i] > top) top = mel[i];
            float low = top - 8f;
            for (long i = 0; i < mel.LongLength; i++) mel[i] = (Math.Max(mel[i], low) + 4f) / 4f;
            return mel;
        }

        /// from부터 count칸에 소리(25ms 칸이 LoudRms보다 큰 것)가 몇 칸인지
        public static int LoudFrames(float[] audio, int from, int count)
        {
            const int Frame = 400;
            int loud = 0;
            int end = Math.Min(audio.Length, from + count);
            for (int s = Math.Max(0, from); s + Frame <= end; s += Frame)
            {
                double sum = 0;
                for (int i = s; i < s + Frame; i++) sum += audio[i] * audio[i];
                if (Math.Sqrt(sum / Frame) > LoudRms) loud++;
            }
            return loud;
        }

        // ── 글자로 바꾸기 ──

        /// 16kHz 한 줄 소리를 바꾼다. lang은 "ko"·"en" 등. progress는 0~1.
        public List<WSegment> Transcribe(float[] audio, string lang, Action<double> progress, Func<bool> stop)
        {
            int langId;
            if (!langs.TryGetValue("<|" + (lang ?? "ko") + "|>", out langId)) langId = langs["<|ko|>"];
            SkippedWindows = Windows = 0;
            int frames;
            float[] mel = LogMel(audio, out frames);
            int contentFrames = frames - ChunkFrames;
            var segs = new List<WSegment>();
            int seek = 0;
            var input = new float[nMels * ChunkFrames];
            while (seek < contentFrames)
            {
                if (stop()) throw new OperationCanceledException();
                if (progress != null) progress(seek / (double)Math.Max(1, contentFrames));
                int segmentSize = Math.Min(ChunkFrames, contentFrames - seek);
                double offset = seek * (double)Hop / SampleRate;
                // 말소리가 없는 30초는 건너뛴다(고요에서 지어낸 말이 나오지 않게, 더 빠르게)
                if (LoudFrames(audio, seek * Hop, segmentSize * Hop) < 8)
                {
                    SkippedWindows++;
                    seek += segmentSize;
                    continue;
                }
                for (int m = 0; m < nMels; m++) Array.Copy(mel, (long)m * frames + seek, input, (long)m * ChunkFrames, ChunkFrames);
                var eb = new WinML.LearningModelBinding(encoder);
                var features = WinML.TensorFloat.CreateFromArray(new long[] { 1, nMels, ChunkFrames }, input);
                eb.Bind(encIn, features);
                var er = encoder.Evaluate(eb, "encoder");
                if (!er.Succeeded) throw new InvalidOperationException("encoder 0x" + er.ErrorStatus.ToString("X8"));
                var hidden = (WinML.TensorFloat)er.Outputs[encOut];
                eb.Clear();
                features.Dispose();
                Windows++;
                List<int> tokens;
                try
                {
                    tokens = Decode(hidden, langId, stop);
                }
                finally
                {
                    hidden.Dispose();
                }
                if (tokens.Count > 0 && tokens[tokens.Count - 1] == eot) tokens.RemoveAt(tokens.Count - 1);
                int n = tokens.Count;
                var isTs = new bool[n];
                for (int i = 0; i < n; i++) isTs[i] = tokens[i] >= tsBegin;
                bool singleEnding = n >= 2 && !isTs[n - 2] && isTs[n - 1];
                var consecutive = new List<int>();
                for (int i = 1; i < n; i++) if (isTs[i - 1] && isTs[i]) consecutive.Add(i);
                int advance;
                if (consecutive.Count > 0)
                {
                    var slices = new List<int>(consecutive);
                    if (singleEnding) slices.Add(n);
                    int last = 0;
                    foreach (int cur in slices)
                    {
                        int startPos = tokens[last] - tsBegin, endPos = tokens[cur - 1] - tsBegin;
                        Add(segs, audio, offset + startPos * TimePrecision, offset + endPos * TimePrecision, Text(tokens, last, cur));
                        last = cur;
                    }
                    advance = singleEnding ? segmentSize : (tokens[last - 1] - tsBegin) * 2;
                }
                else
                {
                    double duration = segmentSize * (double)Hop / SampleRate;
                    for (int i = n - 1; i >= 0; i--)
                    {
                        if (!isTs[i]) continue;
                        if (tokens[i] != tsBegin) duration = (tokens[i] - tsBegin) * TimePrecision;
                        break;
                    }
                    Add(segs, audio, offset, offset + duration, Text(tokens, 0, n));
                    advance = segmentSize;
                }
                if (advance <= 0) advance = segmentSize;
                seek += advance;
            }
            if (progress != null) progress(1);
            return segs;
        }

        /// 구간 하나 더하기. 소리가 거의 없는 구간(고요에서 지어낸 말)은 버리고, 바로 앞과 같은 글은 앞 구간에 합친다.
        static void Add(List<WSegment> segs, float[] audio, double start, double end, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || end <= start) return;
            int from = (int)(start * SampleRate), count = (int)((end - start) * SampleRate);
            if (LoudFrames(audio, from, count) < 4) return;
            Tighten(audio, ref start, ref end);
            var prev = segs.Count > 0 ? segs[segs.Count - 1] : null;
            if (prev != null && prev.Text == text && start - prev.End < 1.0)
            {
                prev.End = Math.Max(prev.End, end);
                return;
            }
            segs.Add(new WSegment { Start = start, End = end, Text = text });
        }

        /// Whisper는 30초 칸 앞의 고요를 건너뛰지 않고 첫 말을 칸 처음에 찍곤 한다(11-12 시험: 5초 고요 뒤의 말이 0초로).
        /// 구간 안에서 가장 큰 소리보다 20dB 넘게 작지 않은 첫·끝 25ms 칸으로 시작과 끝을 당긴다(앞 0.15초·뒤 0.25초 여유).
        internal static void Tighten(float[] audio, ref double start, ref double end)
        {
            const int Frame = 400;
            int from = Math.Max(0, (int)(start * SampleRate)), to = Math.Min(audio.Length, (int)(end * SampleRate));
            int n = (to - from) / Frame;
            if (n < 2) return;
            var rms = new double[n];
            double peak = 0;
            for (int f = 0; f < n; f++)
            {
                double sum = 0;
                int s0 = from + f * Frame;
                for (int i = s0; i < s0 + Frame; i++) sum += audio[i] * audio[i];
                rms[f] = Math.Sqrt(sum / Frame);
                if (rms[f] > peak) peak = rms[f];
            }
            double thr = Math.Max(LoudRms, peak * 0.1);
            int first = -1, last = -1;
            for (int f = 0; f < n && first < 0; f++) if (rms[f] >= thr) first = f;
            for (int f = n - 1; f >= 0 && last < 0; f--) if (rms[f] >= thr) last = f;
            if (first < 0) return;
            double s = (from + first * Frame) / (double)SampleRate - 0.15;
            double e = (from + (last + 1) * Frame) / (double)SampleRate + 0.25;
            if (s > start) start = s;
            if (e < end) end = e;
        }

        static void CopyTail(WinML.TensorFloat t, float[] dst)
        {
            var reference = t.CreateReference();
            try
            {
                IntPtr p;
                uint capacity;
                ((IMemoryBufferByteAccess)(object)reference).GetBuffer(out p, out capacity);
                long count = capacity / 4;
                Marshal.Copy(new IntPtr(p.ToInt64() + (count - dst.Length) * 4), dst, 0, dst.Length);
            }
            finally
            {
                reference.Dispose();
                Marshal.FinalReleaseComObject(reference);
            }
        }

        /// 30초 한 칸을 욕심껏(greedy) 풀기, OpenAI의 타임스탬프 규칙과 함께. 고른 토큰(앞머리 빼고)을 돌려준다.
        List<int> Decode(WinML.TensorFloat hidden, int langId, Func<bool> stop)
        {
            var prompt = new long[] { sot, langId, taskTranscribe };
            var tokens = new List<int>();
            var logits = new float[vocabSize];
            var pastDec = new WinML.TensorFloat[pastNames.Count];
            var pastEnc = new WinML.TensorFloat[pastNames.Count];
            const int MaxNew = 224;
            long[] ids = prompt;
            try
            {
                for (int step = 0; step <= MaxNew; step++)
                {
                    if (stop()) throw new OperationCanceledException();
                    bool first = step == 0;
                    var s = first ? decFirst : decNext;
                    var b = new WinML.LearningModelBinding(s);
                    b.Bind(first ? firstIds : nextIds, WinML.TensorInt64Bit.CreateFromArray(new long[] { 1, ids.Length }, ids));
                    if (first) b.Bind(firstHidden, hidden);
                    else
                        for (int i = 0; i < pastNames.Count; i++)
                            b.Bind(pastNames[i], pastNames[i].Contains(".encoder.") ? pastEnc[i] : pastDec[i]);
                    var r = s.Evaluate(b, "decoder");
                    if (!r.Succeeded) throw new InvalidOperationException("decoder 0x" + r.ErrorStatus.ToString("X8"));
                    b.Clear();
                    for (int i = 0; i < pastNames.Count; i++)
                    {
                        string present = "present." + pastNames[i].Substring("past_key_values.".Length);
                        bool enc = pastNames[i].Contains(".encoder.");
                        object o;
                        if (!r.Outputs.TryGetValue(present, out o)) continue;
                        var t = (WinML.TensorFloat)o;
                        if (enc && !first)
                        {
                            t.Dispose();
                            continue;
                        }
                        if (enc) pastEnc[i] = t;
                        else
                        {
                            if (pastDec[i] != null) pastDec[i].Dispose();
                            pastDec[i] = t;
                        }
                    }
                    var logitsTensor = (WinML.TensorFloat)r.Outputs[logitsName];
                    CopyTail(logitsTensor, logits);
                    logitsTensor.Dispose();
                    if (step == MaxNew) break;
                    ApplyRules(logits, tokens);
                    int next = 0;
                    float best = float.NegativeInfinity;
                    for (int i = 0; i < vocabSize; i++)
                    {
                        if (logits[i] > best)
                        {
                            best = logits[i];
                            next = i;
                        }
                    }
                    tokens.Add(next);
                    if (next == eot) break;
                    ids = new long[] { next };
                }
            }
            finally
            {
                for (int i = 0; i < pastDec.Length; i++)
                {
                    if (pastDec[i] != null) pastDec[i].Dispose();
                    if (pastEnc[i] != null) pastEnc[i].Dispose();
                }
            }
            return tokens;
        }

        /// OpenAI의 SuppressBlank, SuppressTokens, ApplyTimestampRules
        void ApplyRules(float[] logits, List<int> tokens)
        {
            float no = float.NegativeInfinity;
            int n = tokens.Count, v = vocabSize;
            foreach (int t in suppress) if (t < v) logits[t] = no;
            for (int t = eot + 1; t < tsBegin; t++) logits[t] = no;
            if (n == 0) foreach (int t in beginSuppress) if (t < v) logits[t] = no;
            bool lastTs = n >= 1 && tokens[n - 1] >= tsBegin;
            bool penultimateTs = n < 2 || tokens[n - 2] >= tsBegin;
            if (lastTs)
            {
                if (penultimateTs) for (int t = tsBegin; t < v; t++) logits[t] = no;
                else for (int t = 0; t < eot; t++) logits[t] = no;
            }
            int lastStamp = -1;
            for (int i = n - 1; i >= 0; i--)
            {
                if (tokens[i] < tsBegin) continue;
                lastStamp = tokens[i];
                break;
            }
            if (lastStamp >= 0)
            {
                int limit = lastTs && !penultimateTs ? lastStamp : lastStamp + 1;
                for (int t = tsBegin; t < limit && t < v; t++) logits[t] = no;
            }
            if (n == 0)
            {
                for (int t = 0; t < tsBegin; t++) logits[t] = no;
                for (int t = tsBegin + maxInitial + 1; t < v; t++) logits[t] = no;
            }
            double top = double.NegativeInfinity;
            for (int t = 0; t < v; t++) if (logits[t] > top) top = logits[t];
            if (double.IsNegativeInfinity(top)) return;
            double all = 0, stamps = 0, textTop = double.NegativeInfinity;
            for (int t = 0; t < v; t++)
            {
                double e = Math.Exp(logits[t] - top);
                all += e;
                if (t >= tsBegin) stamps += e;
                else if (logits[t] > textTop) textTop = logits[t];
            }
            double logAll = top + Math.Log(all);
            double stampLog = stamps > 0 ? top + Math.Log(stamps) - logAll : double.NegativeInfinity;
            double textLog = textTop - logAll;
            if (stampLog > textLog) for (int t = 0; t < tsBegin; t++) logits[t] = no;
        }
    }

    /// 편집 중인 영상의 소리(들리는 그대로 섞은 것)를 Whisper가 듣는 16kHz 한 줄로
    static class WhisperAudio
    {
        /// 48kHz → 16kHz(창 함수를 씌운 sinc 거르개로 높은 소리를 걸러 낸 뒤 세 칸에 하나)
        sealed class Down3
        {
            const int Half = 48;
            static readonly float[] H = Kernel();
            float[] x = new float[1 << 16];
            int len;
            long baseIdx;
            long next;
            readonly float[] output;
            int outN;

            public Down3(int capacity)
            {
                output = new float[capacity];
                // 앞에 Half칸 고요를 둔 것처럼
                len = Half;
                baseIdx = -Half;
            }

            static float[] Kernel()
            {
                var h = new float[2 * Half + 1];
                double fc = 0.95 * 0.5 / 3, sum = 0;
                for (int i = 0; i < h.Length; i++)
                {
                    int d = i - Half;
                    double s = d == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * d) / (Math.PI * d);
                    double w = 0.5 + 0.5 * Math.Cos(Math.PI * d / (Half + 1));
                    h[i] = (float)(s * w);
                    sum += h[i];
                }
                for (int i = 0; i < h.Length; i++) h[i] = (float)(h[i] / sum);
                return h;
            }

            public void Add(float[] mono, int n)
            {
                if (len + n > x.Length)
                {
                    var nx = new float[Math.Max(x.Length * 2, len + n)];
                    Array.Copy(x, nx, len);
                    x = nx;
                }
                Array.Copy(mono, 0, x, len, n);
                len += n;
                while (next + Half < baseIdx + len && outN < output.Length)
                {
                    int c = (int)(next - Half - baseIdx);
                    float s = 0;
                    for (int k = 0; k < H.Length; k++) s += H[k] * x[c + k];
                    output[outN++] = s;
                    next += 3;
                }
                int drop = (int)(next - Half - baseIdx);
                if (drop > 0)
                {
                    Array.Copy(x, drop, x, 0, len - drop);
                    len -= drop;
                    baseIdx += drop;
                }
            }

            public float[] Finish(long frames48)
            {
                Add(new float[Half + 3], Half + 3);
                int want = (int)Math.Min(output.Length, (frames48 + 2) / 3);
                if (outN >= want && want == output.Length) return output;
                var r = new float[Math.Min(outN, want)];
                Array.Copy(output, r, r.Length);
                return r;
            }
        }

        /// 소리를 끝까지 섞어 16kHz 한 줄로. 소리 파일이 아직 준비 중이면 기다린다.
        public static float[] FromProject(VProject p, Action<double> progress, Func<bool> stop)
        {
            const int Rate = AudioTrack.Rate;
            var mixer = new VMixer { Wait = true, Stop = stop };
            mixer.Use(p);
            long frames48 = (long)Math.Ceiling(p.Duration * Rate);
            var down = new Down3((int)(frames48 / 3) + 8);
            const int Chunk = Rate;
            var buf = new float[Chunk * 2];
            var mono = new float[Chunk];
            for (long done = 0; done < frames48; done += Chunk)
            {
                if (stop()) throw new OperationCanceledException();
                int n = (int)Math.Min(Chunk, frames48 - done);
                mixer.Mix(done / (double)Rate, n, buf);
                for (int i = 0; i < n; i++) mono[i] = 0.5f * (buf[2 * i] + buf[2 * i + 1]);
                down.Add(mono, n);
                if (progress != null) progress(done / (double)Math.Max(1, frames48));
            }
            return down.Finish(frames48);
        }

        /// 다른 빠르기의 한 줄 소리를 16kHz로(시험·파일용)
        public static float[] Resample(float[] x, int rate)
        {
            if (rate == WhisperEngine.SampleRate) return x;
            double ratio = (double)rate / WhisperEngine.SampleRate;
            int outLen = (int)Math.Floor(x.Length / ratio);
            var y = new float[outLen];
            double cutoff = 0.95 * 0.5 / Math.Max(1.0, ratio);
            int half = (int)Math.Ceiling(16 * Math.Max(1.0, ratio));
            Parallel.For(0, outLen, i =>
            {
                double c = i * ratio;
                int center = (int)Math.Floor(c);
                double sum = 0, wsum = 0;
                for (int k = center - half + 1; k <= center + half; k++)
                {
                    if (k < 0 || k >= x.Length) continue;
                    double d = k - c;
                    double s = Math.Abs(d) < 1e-9 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * d) / (Math.PI * d);
                    double w = 0.5 + 0.5 * Math.Cos(Math.PI * d / (half + 1));
                    double h = s * w;
                    sum += x[k] * h;
                    wsum += h;
                }
                y[i] = (float)(wsum != 0 ? sum / wsum : 0);
            });
            return y;
        }
    }

    /// Whisper 구간을 화면 자막 크기로 나눈다
    static class WhisperText
    {
        /// 자막 한 줄에 둘 글자 수(빈칸 포함)
        public const int MaxLine = 24;

        public sealed class Piece
        {
            public double Start, End;
            public string Text;
        }

        static readonly Regex SentenceEnd = new Regex(@"(?<=[.?!。？！…])\s+");

        /// 문장 끝에서 먼저 나누고, 그래도 길면 가운데에 가까운 빈칸에서 나눈다
        public static List<string> Split(string text)
        {
            var list = new List<string>();
            foreach (var s0 in SentenceEnd.Split(text.Trim()))
            {
                string s = Regex.Replace(s0, @"\s+", " ").Trim();
                if (s.Length > 0) SplitLong(s, list);
            }
            return list;
        }

        static void SplitLong(string s, List<string> list)
        {
            if (s.Length <= MaxLine)
            {
                list.Add(s);
                return;
            }
            int mid = s.Length / 2, best = -1;
            for (int d = 0; d < s.Length / 2; d++)
            {
                if (mid - d > 3 && s[mid - d] == ' ')
                {
                    best = mid - d;
                    break;
                }
                if (mid + d < s.Length - 3 && s[mid + d] == ' ')
                {
                    best = mid + d;
                    break;
                }
            }
            if (best < 0)
            {
                // 빈칸이 없으면 반씩
                best = mid;
                list.Add(s.Substring(0, best));
                SplitLong(s.Substring(best), list);
                return;
            }
            SplitLong(s.Substring(0, best).Trim(), list);
            SplitLong(s.Substring(best + 1).Trim(), list);
        }

        static int Weight(string s)
        {
            return Math.Max(1, s.Replace(" ", "").Length);
        }

        /// 구간마다 나눈 조각에 글자 수만큼 시간을 나눠 준다
        public static List<Piece> Pieces(List<WSegment> segs)
        {
            var list = new List<Piece>();
            foreach (var seg in segs)
            {
                var parts = Split(seg.Text);
                if (parts.Count == 0) continue;
                double total = parts.Sum(p => (double)Weight(p));
                double t = seg.Start, span = Math.Max(0.3, seg.End - seg.Start);
                foreach (var p in parts)
                {
                    double len = span * Weight(p) / total;
                    list.Add(new Piece { Start = t, End = t + len, Text = p });
                    t += len;
                }
            }
            // 너무 짧게 보이는 자막은 다음 자막 앞까지 조금 늘린다
            for (int i = 0; i < list.Count; i++)
            {
                double limit = i + 1 < list.Count ? list[i + 1].Start : list[i].End + 1;
                if (list[i].End - list[i].Start < 0.8) list[i].End = Math.Min(limit, list[i].Start + 0.8);
            }
            return list;
        }
    }
}
