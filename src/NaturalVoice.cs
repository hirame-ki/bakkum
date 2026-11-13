// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 자연스러운 목소리: Supertone이 공개한 Supertonic 3(한국어·영어를 읽는 AI 목소리, ONNX 파일 넷과 목소리 10개)를
// 윈도우에 들어 있는 Windows ML로 이 PC 안에서 돌린다. exe를 따로 켜지 않고, 처음 쓸 때 한 번만 받는다
// (받는 곳·크기·사용 조건을 먼저 보여 주고 묻는다). 받은 뒤에는 인터넷 없이 돌고, 글은 PC 밖으로 나가지 않는다.
// 글자 다듬기(Preprocess)와 처리 순서(길이 예측 → 글자 인코더 → 잡음에서 여러 단계 다듬기 → 보코더)는
// Supertone의 공개 예제(supertonic py/helper.py, Copyright (c) 2025 Supertone Inc., MIT License)를
// C#으로 옮겼다(2026-11-11 시험판을 옮김). MIT 허가 문구는 NOTICE.txt 3장에 있다.
// 모델 사용 조건(BigScience Open RAIL-M)에 따라, 이 목소리로 만든 영상에는 AI 목소리라는 표시를 넣는다(VAiLabel).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using WinML = Windows.AI.MachineLearning;

namespace Bakkum
{
    /// 받아 쓰는 자연스러운 목소리(Supertonic 3). 받는 곳은 커밋으로 고정하고, 파일마다 SHA-256으로 확인한다.
    static class NaturalVoices
    {
        public sealed class Part
        {
            public string Path, Sha256;
            public long Size;
        }

        const long MB = 1024 * 1024;
        public const string Repo = "supertone-oss-archive/supertonic-3";
        public const string Commit = "aafc6e32416a594460b32413efc49d7fe4ce6d46";
        public const string License = "BigScience Open RAIL-M";
        /// 돌릴 때 메모리(어림). 11-11 실측 약 1GB, 11-13 시험(세션이 모델을 넘겨받게 함)에서는 약 0.6GB 늘었다.
        /// 넘겨받기를 못 하는 옛 윈도우도 있어서 넉넉하게 잡는다.
        public const long Memory = 1000 * MB;
        /// 목소리 id 앞에 붙인다(윈도우 목소리 id와 섞이지 않게)
        public const string Prefix = "st:";
        public static readonly string[] Styles = { "F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5" };
        public const string DefaultVoice = Prefix + "F1";

        /// 시험용: 받는 주소 앞부분
        internal static string UrlBase = "https://huggingface.co/";
        /// 시험용: 받지 않고 이 PC의 파일을 복사한다(경로를 돌려주면)
        internal static Func<Part, string> LocalSource;

        internal static readonly ModelWords Words = new ModelWords { Name = "자연스러운 목소리", Object = "자연스러운 목소리를", Again = "[자연스러운 목소리 받기]를" };

        static Part P(string path, long size, string sha)
        {
            return new Part { Path = path, Size = size, Sha256 = sha };
        }

        /// 2026-11-11에 받아 시험한 판(모두 401,283,498바이트)
        public static readonly Part[] Parts =
        {
            P("LICENSE", 15007, "0d944a9110fed9a9602d60e0423a272903e7bd21ab060490774efc77c2275e9f"),
            P("onnx/duration_predictor.onnx", 3700147, "c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db"),
            P("onnx/text_encoder.onnx", 36416150, "c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff"),
            P("onnx/vector_estimator.onnx", 256534781, "883ac868ea0275ef0e991524dc64f16b3c0376efd7c320af6b53f5b780d7c61c"),
            P("onnx/vocoder.onnx", 101424195, "085de76dd8e8d5836d6ca66826601f615939218f90e519f70ee8a36ed2a4c4ba"),
            P("onnx/unicode_indexer.json", 277676, "9bf7346e43883a81f8645c81224f786d43c5b57f3641f6e7671a7d6c493cb24f"),
            P("voice_styles/F1.json", 292046, "bbdec6ee00231c2c742ad05483df5334cab3b52fda3ba38e6a07059c4563dbc2"),
            P("voice_styles/F2.json", 292423, "7c722c6a72707b1a77f035d67f0d1351ba187738e06f7683e8c72b1df3477fc6"),
            P("voice_styles/F3.json", 290794, "12f6ef2573baa2defa1128069cb59f203e3ab67c92af77b42df8a0e3a2f7c6ab"),
            P("voice_styles/F4.json", 291808, "c2fa764c1225a76dfc3e2c73e8aa4f70d9ee48793860eb34c295fff01c2e032b"),
            P("voice_styles/F5.json", 291479, "45966e73316415626cf41a7d1c6f3b4c70dbc1ba2bee5c1978ef0ce33244fc8d"),
            P("voice_styles/M1.json", 291748, "e35604687f5d23694b8e91593a93eec0e4eca6c0b02bb8ed69139ab2ea6b0a5b"),
            P("voice_styles/M2.json", 292055, "b76cbf62bac707c710cf0ae5aba5e31eea1a6339a9734bfae33ab98499534a50"),
            P("voice_styles/M3.json", 290198, "ea1ac35ccb91b0d7ecad533a2fbd0eec10c91513d8951e3b25fbba99954e159b"),
            P("voice_styles/M4.json", 291522, "ca8eefad4fcd989c9379032ff3e50738adc547eeb5e221b82593a6d7b3bac303"),
            P("voice_styles/M5.json", 291469, "dd22b92740314321f8ae11c5e87f8dd60d060f15dd3a632b5adf77f471f77af2"),
        };

        public static long DownloadBytes
        {
            get { return Parts.Sum(p => p.Size); }
        }

        /// 크고 다시 받을 수 있어서 캐시 폴더(이 PC 안쪽)에 둔다. 프로그램을 지울 때 같이 지운다.
        public static string Folder
        {
            get { return Path.Combine(AppInfo.CacheDir, "supertonic3"); }
        }

        public static string UrlOf(Part p)
        {
            return UrlOf(Repo, Commit, p);
        }

        internal static string UrlOf(string repo, string commit, Part p)
        {
            return UrlBase + repo + "/resolve/" + commit + "/" + p.Path;
        }

        public static bool IsNatural(string voiceId)
        {
            return voiceId != null && voiceId.StartsWith(Prefix, StringComparison.Ordinal) && Styles.Contains(voiceId.Substring(Prefix.Length));
        }

        public static string StyleOf(string voiceId)
        {
            return IsNatural(voiceId) ? voiceId.Substring(Prefix.Length) : null;
        }

        /// "여자 1" · "남자 3"
        public static string NameOf(string voiceId)
        {
            string s = StyleOf(voiceId);
            if (s == null) return "자연스러운 목소리";
            return (s[0] == 'F' ? "여자 " : "남자 ") + s.Substring(1);
        }

        /// 다 받아 둔 목소리가 있는지(파일마다 크기까지 본다. SHA-256은 받을 때 확인했다)
        public static bool Ready()
        {
            return ReadyAt(Folder, Commit, Parts);
        }

        internal static bool ReadyAt(string folder, string commit, Part[] parts)
        {
            try
            {
                string mark = Path.Combine(folder, "ready.json");
                if (!File.Exists(mark) || File.ReadAllText(mark).IndexOf(commit, StringComparison.Ordinal) < 0) return false;
                foreach (var p in parts)
                {
                    var f = new FileInfo(Path.Combine(folder, p.Path.Replace('/', '\\')));
                    if (!f.Exists || f.Length != p.Size) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// 받은 목소리와 받다 만 것이 디스크에 쓰는 크기
        public static long UsedBytes()
        {
            long sum = 0;
            foreach (var d in new[] { Folder, Folder + ".받는중" })
            {
                try
                {
                    if (Directory.Exists(d))
                        foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) sum += new FileInfo(f).Length;
                }
                catch { }
            }
            return sum;
        }

        /// 받은 목소리와 받다 만 것을 모두 지운다(열어 둔 엔진도 놓는다)
        public static void DeleteAll()
        {
            NaturalVoiceEngine.Release();
            foreach (var d in new[] { Folder, Folder + ".받는중" })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }

        public static void Prepare(Action<string, double> report, Func<bool> stop)
        {
            Prepare(Repo, Commit, Parts, Folder, report, stop);
        }

        /// 받기 → 확인. report(할 일, 0~1). 멈추면 OperationCanceledException(받던 파일은 남겨 두어 다음에 이어 받는다).
        internal static void Prepare(string repo, string commit, Part[] parts, string folder, Action<string, double> report, Func<bool> stop)
        {
            if (ReadyAt(folder, commit, parts)) return;
            string part = folder + ".받는중";
            Directory.CreateDirectory(part);
            long total = Math.Max(1, parts.Sum(p => p.Size)), have = 0;
            foreach (var p in parts)
            {
                string f = Path.Combine(part, p.Path.Replace('/', '\\'));
                if (File.Exists(f)) have += Math.Min(p.Size, new FileInfo(f).Length);
            }
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(part)));
                long need = total - have + 100 * MB;
                if (drive.IsReady && drive.AvailableFreeSpace < need)
                    throw new UserFacingException("디스크 공간이 모자라요. 자연스러운 목소리를 받으려면 약 " + WhisperModels.Words(need) + "가 비어 있어야 해요(지금 " + WhisperModels.Words(drive.AvailableFreeSpace) + ").");
            }
            catch (UserFacingException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Write("자연스러운 목소리: 빈 공간 확인 실패 " + ex.GetType().Name);
            }
            long before = 0;
            foreach (var p in parts)
            {
                if (stop()) throw new OperationCanceledException();
                string dst = Path.Combine(part, p.Path.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                long start = before;
                Action<long> got = delegate (long n) { report("자연스러운 목소리를 받는 중", Math.Min(0.99, (start + n) / (double)total)); };
                bool done = File.Exists(dst) && new FileInfo(dst).Length == p.Size && WhisperModels.Sha256Of(dst) == p.Sha256;
                if (!done)
                {
                    if (File.Exists(dst) && new FileInfo(dst).Length >= p.Size) File.Delete(dst);
                    string local = LocalSource != null ? LocalSource(p) : null;
                    if (local != null) File.Copy(local, dst, true);
                    else WhisperModels.Fetch(UrlOf(repo, commit, p), dst, p.Size, got, stop, Words);
                    report("받은 파일을 확인하는 중", Math.Min(0.99, (before + p.Size) / (double)total));
                    if (WhisperModels.Sha256Of(dst) != p.Sha256)
                    {
                        File.Delete(dst);
                        throw new UserFacingException("받은 파일이 원래 파일과 달라요(" + Path.GetFileName(p.Path) + "). 다시 눌러 주세요. 계속 그러면 학교 인터넷이 파일을 바꾸는지 확인해 주세요.");
                    }
                }
                before += p.Size;
            }
            File.WriteAllText(Path.Combine(part, "ready.json"), "{\"repo\":\"" + repo + "\",\"commit\":\"" + commit + "\"}");
            NaturalVoiceEngine.Release();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(part, folder);
            report("다 받았어요", 1);
        }
    }

    /// 자연스러운 목소리를 쓸 수 없을 때(메모리가 모자람·열리지 않음·파일 없음). Because는 다른 목소리로 바꿔 만들 때
    /// 알림에 붙일 짧은 까닭('…해서'로 끝난다).
    sealed class NaturalVoiceException : UserFacingException
    {
        public readonly string Because;

        public NaturalVoiceException(string message, string because) : base(message)
        {
            Because = because;
        }
    }

    /// 읽을 글자가 하나도 없는 글(이모지·한자·기호만). 엔진은 멀쩡하다.
    sealed class NothingToReadException : UserFacingException
    {
        public NothingToReadException() : base("읽을 글이 없어요.") { }
    }

    /// Supertonic 3 엔진. 모델 넷(메모리 약 1GB)은 처음 읽을 때 열어 두고, 영상 편집 창을 닫거나 목소리를 지울 때 놓는다.
    /// 한 번에 한 스레드만 쓴다(Speak가 잠근다).
    sealed class NaturalVoiceEngine : IDisposable
    {
        public const int SampleRate = 44100;
        const int BaseChunk = 512, Compress = 6, LatentDim = 24;
        /// 잡음에서 다듬는 단계 수(11-11 시험: 5단계보다 8단계가 또렷했다)
        public const int Steps = 8;
        /// 공개 예제의 기본 빠르기. 바꿈의 '음성 빠르기 1배'가 이 빠르기다.
        public const double BaseSpeed = 1.05;
        /// 긴 글을 나눠 읽을 때 사이 쉼(초, 공개 예제와 같다)
        const double ChunkGap = 0.3;
        /// 같은 글은 늘 같은 소리가 나게 잡음의 씨앗을 정해 둔다
        const int Seed = 7;
        /// 만드는 방법이 바뀌면 올린다(자막 음성 파일 이름에 들어가 옛 파일을 다시 쓰지 않게)
        public const int Version = 1;

        static readonly object gate = new object();
        static NaturalVoiceEngine shared;
        static volatile bool releaseLater;

        readonly string folder;
        int[] indexer;
        WinML.LearningModelSession dp, enc, est, voc;
        string dpOut, encOut, estOut, vocOut;
        readonly Dictionary<string, Style> styles = new Dictionary<string, Style>();

        sealed class Style
        {
            public float[] Ttl, Dp;
            public long[] TtlShape, DpShape;
        }

        const string NoMemory = "메모리가 모자라 자연스러운 목소리를 만들지 못했어요. 다른 프로그램을 닫고 다시 해 보거나, [윈도우 목소리]를 골라 주세요.";
        const string NoMemoryBecause = "메모리가 모자라서";

        public NaturalVoiceEngine(string folder)
        {
            this.folder = folder;
            try
            {
                string onnx = Path.Combine(folder, "onnx");
                indexer = LoadIndexer(Path.Combine(onnx, "unicode_indexer.json"));
                dp = Load(Path.Combine(onnx, "duration_predictor.onnx"), out dpOut);
                enc = Load(Path.Combine(onnx, "text_encoder.onnx"), out encOut);
                est = Load(Path.Combine(onnx, "vector_estimator.onnx"), out estOut);
                voc = Load(Path.Combine(onnx, "vocoder.onnx"), out vocOut);
            }
            catch (Exception ex)
            {
                Dispose();
                Log.Write("자연스러운 목소리: 열기 실패 " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                if (IsOutOfMemory(ex)) throw new NaturalVoiceException(NoMemory, NoMemoryBecause);
                throw new NaturalVoiceException("자연스러운 목소리를 열지 못했어요. [받은 목소리 지우기]를 누른 뒤 다시 받아 주세요. (" + ex.GetType().Name + ")",
                    "이 PC에서 자연스러운 목소리를 열지 못해서");
            }
        }

        static bool IsOutOfMemory(Exception ex)
        {
            return ex is OutOfMemoryException || ex.HResult == unchecked((int)0x8007000E);
        }

        // ── 함께 쓰는 엔진 ──

        /// 열어 둔 엔진으로 글을 읽는다(없으면 연다). 44.1kHz 한 줄 소리. rate는 음성 빠르기(1이 보통).
        public static float[] Speak(string text, string voiceId, double rate)
        {
            string style = NaturalVoices.StyleOf(voiceId);
            if (style == null) throw new ArgumentException("자연스러운 목소리가 아니에요: " + voiceId);
            if (!NaturalVoices.Ready()) throw new NaturalVoiceException("자연스러운 목소리 파일이 없어요. 자막 칸에서 [자연스러운 목소리 받기]를 눌러 주세요.", "자연스러운 목소리 파일이 없어서");
            double speed = BaseSpeed * Math.Max(0.5, Math.Min(2.0, rate));
            return WinRtCall.Mta(() =>
            {
                lock (gate)
                {
                    try
                    {
                        if (shared == null) shared = new NaturalVoiceEngine(NaturalVoices.Folder);
                        return shared.Read(text, style, speed);
                    }
                    catch (UserFacingException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("자연스러운 목소리: 만들기 실패 " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                        if (IsOutOfMemory(ex))
                        {
                            DisposeShared();
                            throw new NaturalVoiceException(NoMemory, NoMemoryBecause);
                        }
                        throw new NaturalVoiceException("자연스러운 목소리를 만들지 못했어요. 다시 해 보고, 계속 안 되면 [받은 목소리 지우기]를 누른 뒤 다시 받아 주세요. (" + ex.GetType().Name + ")",
                            "자연스러운 목소리를 만들지 못해서");
                    }
                    finally
                    {
                        if (releaseLater)
                        {
                            releaseLater = false;
                            DisposeShared();
                        }
                    }
                }
            });
        }

        /// 열어 둔 엔진이 있는지(처음 열 때는 몇 초 걸린다)
        public static bool Loaded
        {
            get { return shared != null; }
        }

        /// 열어 둔 엔진을 놓는다(메모리 약 1GB를 돌려준다). 읽는 중이면 다 읽은 뒤에 놓는다.
        public static void Release()
        {
            if (!Monitor.TryEnter(gate, 100))
            {
                releaseLater = true;
                return;
            }
            try
            {
                DisposeShared();
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }

        static void DisposeShared()
        {
            if (shared == null) return;
            shared.Dispose();
            shared = null;
            GC.Collect();
        }

        public void Dispose()
        {
            foreach (var s in new[] { dp, enc, est, voc })
            {
                try
                {
                    if (s != null) s.Dispose();
                }
                catch { }
            }
            dp = enc = est = voc = null;
        }

        // ── 파일 읽기 ──

        static object Json(string path)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
            return js.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
        }

        /// 글자(UTF-16 한 칸) → 모델의 글자 번호. 모르는 글자는 -1.
        static int[] LoadIndexer(string path)
        {
            var idx = (object[])Json(path);
            var a = new int[idx.Length];
            for (int i = 0; i < idx.Length; i++) a[i] = idx[i] == null ? -1 : Convert.ToInt32(idx[i]);
            return a;
        }

        static WinML.LearningModelSession Load(string path, out string output)
        {
            var model = WinML.LearningModel.LoadFromFilePath(path);
            output = model.OutputFeatures.First().Name;
            var options = new WinML.LearningModelSessionOptions();
            try
            {
                // 세션이 모델을 복사하지 않고 넘겨받아 같은 내용을 메모리에 두 번 두지 않게
                options.CloseModelOnSessionCreation = true;
            }
            catch { }
            return new WinML.LearningModelSession(model, new WinML.LearningModelDevice(WinML.LearningModelDeviceKind.Cpu), options);
        }

        Style StyleFor(string name)
        {
            Style s;
            if (styles.TryGetValue(name, out s)) return s;
            var root = (Dictionary<string, object>)Json(Path.Combine(folder, "voice_styles", name + ".json"));
            s = new Style();
            s.Ttl = Tensor(root, "style_ttl", out s.TtlShape);
            s.Dp = Tensor(root, "style_dp", out s.DpShape);
            styles[name] = s;
            return s;
        }

        static float[] Tensor(Dictionary<string, object> root, string key, out long[] shape)
        {
            var part = (Dictionary<string, object>)root[key];
            var dims = (object[])part["dims"];
            shape = new long[dims.Length];
            long n = 1;
            for (int i = 0; i < dims.Length; i++)
            {
                shape[i] = Convert.ToInt64(dims[i]);
                n *= shape[i];
            }
            var data = new List<float>((int)n);
            Flatten(part["data"], data);
            if (data.Count != n) throw new InvalidDataException(key + " 크기가 맞지 않아요: " + data.Count + " / " + n);
            return data.ToArray();
        }

        static void Flatten(object o, List<float> into)
        {
            var arr = o as object[];
            if (arr != null)
            {
                foreach (var x in arr) Flatten(x, into);
                return;
            }
            var list = o as System.Collections.ArrayList;
            if (list != null)
            {
                foreach (var x in list) Flatten(x, into);
                return;
            }
            into.Add(Convert.ToSingle(o, System.Globalization.CultureInfo.InvariantCulture));
        }

        // ── 글자 ──

        /// 읽을 조각(말과 글)
        internal sealed class Chunk
        {
            public string Lang, Text;
        }

        static readonly Regex SentenceEnd = new Regex(@"(?<=[.!?。？！…])\s+");

        /// 글을 읽을 조각으로: 문장 끝에서 나누고, 같은 말(한국어·영어)인 문장끼리 공개 예제의 길이(한국어 120자, 영어 300자) 안에서 붙인다.
        /// 한 문장이 그보다 길면 쉼표나 띄어쓰기에서 나눈다.
        internal static List<Chunk> Chunks(string text)
        {
            var list = new List<Chunk>();
            foreach (var para in Regex.Split((text ?? "").Replace("\r", ""), @"\n\s*\n+"))
            {
                Chunk cur = null;
                foreach (var raw in SentenceEnd.Split(para.Replace('\n', ' ')))
                {
                    string s = Regex.Replace(raw, @"\s+", " ").Trim();
                    if (s.Length == 0) continue;
                    string lang = VLecture.LooksEnglish(s) ? "en" : "ko";
                    int max = lang == "en" ? 300 : 120;
                    foreach (var piece in SplitLong(s, max))
                    {
                        if (cur != null && (cur.Lang != lang || cur.Text.Length + 1 + piece.Length > max))
                        {
                            list.Add(cur);
                            cur = null;
                        }
                        if (cur == null) cur = new Chunk { Lang = lang, Text = piece };
                        else cur.Text += " " + piece;
                    }
                }
                if (cur != null) list.Add(cur);
            }
            return list;
        }

        static IEnumerable<string> SplitLong(string s, int max)
        {
            while (s.Length > max)
            {
                int cut = s.LastIndexOfAny(new[] { ',', '，', '、', ';' }, max - 1);
                if (cut < max / 3) cut = s.LastIndexOf(' ', max - 1);
                if (cut < max / 3) cut = max - 1;
                string head = s.Substring(0, cut + 1).Trim();
                if (head.Length > 0) yield return head;
                s = s.Substring(cut + 1).Trim();
            }
            if (s.Length > 0) yield return s;
        }

        bool Known(char c)
        {
            return c < indexer.Length && indexer[c] >= 0;
        }

        static readonly string[] From = { "–", "‑", "—", "_", "“", "”", "‘", "’", "´", "`", "[", "]", "|", "/", "#", "→", "←" };
        static readonly string[] To = { "-", "-", "-", " ", "\"", "\"", "'", "'", "'", "'", " ", " ", " ", " ", " ", " ", " " };

        /// 공개 예제의 글자 다듬기: NFKD로 풀고, 따옴표·대시를 맞추고, 끝에 문장부호가 없으면 마침표를 붙여 &lt;ko&gt;…&lt;/ko&gt;로 감싼다.
        /// 모델이 모르는 글자(이모지·한자 등)는 띄어쓰기로 바꾼다. 읽을 글자가 없으면 null.
        internal string Preprocess(string text, string lang)
        {
            text = (text ?? "").Normalize(NormalizationForm.FormKD);
            for (int i = 0; i < From.Length; i++) text = text.Replace(From[i], To[i]);
            text = Regex.Replace(text, "[♥☆♡©\\\\]", "");
            text = text.Replace("@", " at ");
            var sb = new StringBuilder(text.Length);
            foreach (char c in text) sb.Append(Known(c) ? c : ' ');
            text = sb.ToString();
            text = Regex.Replace(text, " ([,.!?;:'])", "$1");
            while (text.Contains("\"\"")) text = text.Replace("\"\"", "\"");
            while (text.Contains("''")) text = text.Replace("''", "'");
            text = Regex.Replace(text, "\\s+", " ").Trim();
            if (!text.Any(char.IsLetterOrDigit)) return null;
            if (!Regex.IsMatch(text, "[.!?;:,'\"')\\]}…。」』】〉》›»]$")) text += ".";
            return "<" + lang + ">" + text + "</" + lang + ">";
        }

        // ── 목소리 만들기 ──

        /// 글 전체를 읽는다. 조각 사이는 0.3초 쉰다.
        float[] Read(string text, string style, double speed)
        {
            var st = StyleFor(style);
            var parts = new List<float[]>();
            foreach (var c in Chunks(text))
            {
                string t = Preprocess(c.Text, c.Lang);
                if (t != null) parts.Add(Synth(t, st, speed));
            }
            if (parts.Count == 0) throw new NothingToReadException();
            int gap = (int)(ChunkGap * SampleRate);
            var all = new float[parts.Sum(p => p.Length) + gap * (parts.Count - 1)];
            int at = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) at += gap;
                Array.Copy(parts[i], 0, all, at, parts[i].Length);
                at += parts[i].Length;
            }
            // 넘치는 소리는 깎지 않고 전체를 줄인다
            float peak = 0;
            foreach (var v in all) peak = Math.Max(peak, Math.Abs(v));
            if (peak > 0.98f)
            {
                float k = 0.98f / peak;
                for (int i = 0; i < all.Length; i++) all[i] *= k;
            }
            return all;
        }

        /// 다듬은 글 한 조각 → 44.1kHz 소리
        float[] Synth(string t, Style st, double speed)
        {
            var idList = new List<long>(t.Length);
            foreach (char c in t)
                if (Known(c)) idList.Add(indexer[c]);
            int L = idList.Count;
            var ids = idList.ToArray();
            var mask = new float[L];
            for (int i = 0; i < L; i++) mask[i] = 1f;
            long[] tshape = { 1, L }, mshape = { 1, 1, L }, one = { 1 };
            long[] sh, embShape;

            var dur = Eval(dp, dpOut, out sh,
                "text_ids", WinML.TensorInt64Bit.CreateFromArray(tshape, ids),
                "style_dp", F(st.DpShape, st.Dp),
                "text_mask", F(mshape, mask));
            double seconds = Math.Max(0.1, dur[0] / speed);

            var emb = Eval(enc, encOut, out embShape,
                "text_ids", WinML.TensorInt64Bit.CreateFromArray(tshape, ids),
                "style_ttl", F(st.TtlShape, st.Ttl),
                "text_mask", F(mshape, mask));

            long wavLen = (long)(seconds * SampleRate);
            int chunk = BaseChunk * Compress;
            long latLen = (wavLen + chunk - 1) / chunk;
            int C = LatentDim * Compress;
            var rnd = new Random(Seed);
            var x = new float[C * latLen];
            for (int i = 0; i < x.Length; i++) x[i] = (float)Gauss(rnd);
            var lmask = new float[latLen];
            for (int i = 0; i < lmask.Length; i++) lmask[i] = 1f;
            long[] xshape = { 1, C, latLen }, lmshape = { 1, 1, latLen };
            var total = new float[] { Steps };
            for (int step = 0; step < Steps; step++)
            {
                x = Eval(est, estOut, out sh,
                    "noisy_latent", F(xshape, x),
                    "text_emb", F(embShape, emb),
                    "style_ttl", F(st.TtlShape, st.Ttl),
                    "text_mask", F(mshape, mask),
                    "latent_mask", F(lmshape, lmask),
                    "current_step", F(one, new float[] { step }),
                    "total_step", F(one, total));
            }
            var wav = Eval(voc, vocOut, out sh, "latent", F(xshape, x));
            long keep = Math.Min(wav.Length, wavLen);
            var o = new float[keep];
            Array.Copy(wav, o, keep);
            return o;
        }

        static WinML.TensorFloat F(long[] shape, float[] data)
        {
            return WinML.TensorFloat.CreateFromArray(shape, data);
        }

        static double Gauss(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        /// 입력(이름, 텐서, 이름, 텐서…)을 묶어 한 번 돌리고 첫 출력을 float 배열로. 입력·출력 텐서는 바로 놓는다(한 번에 자막 수십 개를 읽어도 메모리가 쌓이지 않게).
        static float[] Eval(WinML.LearningModelSession s, string outName, out long[] shape, params object[] inputs)
        {
            var b = new WinML.LearningModelBinding(s);
            try
            {
                for (int i = 0; i + 1 < inputs.Length; i += 2) b.Bind((string)inputs[i], inputs[i + 1]);
                var r = s.Evaluate(b, "");
                if (!r.Succeeded) throw new InvalidOperationException("평가 실패 0x" + r.ErrorStatus.ToString("X8"));
                var t = (WinML.TensorFloat)r.Outputs[outName];
                try
                {
                    var sh = t.Shape;
                    shape = new long[sh.Count];
                    long n = 1;
                    for (int i = 0; i < sh.Count; i++)
                    {
                        shape[i] = sh[i];
                        n *= sh[i];
                    }
                    return Floats(t, n);
                }
                finally
                {
                    t.Dispose();
                }
            }
            finally
            {
                b.Clear();
                for (int i = 1; i < inputs.Length; i += 2)
                {
                    var d = inputs[i] as IDisposable;
                    if (d != null) d.Dispose();
                }
            }
        }

        /// 텐서를 한 번에 복사한다(한 칸씩 읽으면 보코더 출력처럼 큰 것은 느리다)
        static float[] Floats(WinML.TensorFloat t, long count)
        {
            var reference = t.CreateReference();
            try
            {
                IntPtr p;
                uint capacity;
                ((IMemoryBufferByteAccess)(object)reference).GetBuffer(out p, out capacity);
                var a = new float[Math.Min(count, capacity / 4)];
                Marshal.Copy(p, a, 0, a.Length);
                return a;
            }
            finally
            {
                reference.Dispose();
                Marshal.FinalReleaseComObject(reference);
            }
        }
    }

    /// AI 목소리 표시. Supertonic 사용 조건(Open RAIL-M 부록 A의 e: 기계가 만든 것을 알리지 않고 퍼뜨리지 않기)에 따라
    /// 자연스러운 목소리를 넣을 때 화면 왼쪽 위에 작게 넣는다. 사용자가 옮기거나 고칠 수 있고, 지우면 내보낼 때 알린다.
    static class VAiLabel
    {
        public const string Text = "AI 목소리";
        static readonly Regex Says = new Regex(@"AI\s*(목소리|음성|보이스|더빙|성우|내레이션|낭독)|인공\s*지능\s*(목소리|음성)|AI[\s-]*(voice|generated|narrat)", RegexOptions.IgnoreCase);

        /// 자연스러운 목소리로 만든 음성이 있는지
        public static bool Uses(VProject p)
        {
            return p.Sounds.Any(s => s.Kind == "voice" && NaturalVoices.IsNatural(s.Voice));
        }

        /// 'AI 목소리'라고 밝힌 글자가 화면에 있는지(사용자가 고쳐 쓴 'AI 음성으로 읽었어요' 같은 말도 본다)
        public static bool Has(VProject p)
        {
            return p.Items.Any(i => i.Kind == "text" && Says.IsMatch(i.Text ?? ""));
        }

        /// 표시가 없으면 넣는다(영상 처음부터 끝까지, 세로 영상은 앱 단추에 가리지 않는 곳). 넣었으면 true.
        public static bool Ensure(VProject p)
        {
            if (Has(p)) return false;
            bool vertical = p.AspectRatio < 0.8, square = !vertical && p.AspectRatio < 1.3;
            p.Items.Add(new VItem
            {
                Kind = "text", Text = Text, Name = "AI 목소리 표시",
                Start = 0, End = Math.Max(0.5, p.Duration),
                X = vertical ? 0.2 : square ? 0.15 : 0.1, Y = vertical ? 0.16 : 0.075, W = 0.3,
                Size = 0.032, Bold = true, Rgb = 0xFFFFFF, Outline = 0, Shadow = false,
                Box = true, BoxRgb = 0x15161A, BoxAlpha = 0.5, In = "none", Out = "none"
            });
            return true;
        }
    }
}
