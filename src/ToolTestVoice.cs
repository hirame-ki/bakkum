// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 자연스러운 목소리 시험(인터넷 안 씀).
// · NaturalVoiceLogic: 재료 없이(video 묶음에서 부른다) 받을 목록·목소리 이름·읽을 조각·AI 목소리 표시·내보내기 점검,
//   자연스러운 목소리가 안 될 때 윈도우 목소리로 이어서 만들기
// · VoiceTests('voice' 묶음): 시험 폴더의 supertonic(11-11에 받아 둔 Supertonic 3)을 이 PC 안 작은 서버·복사로 내주어
//   받기·멈추기·이어 받기·확인을 보고, 진짜로 목소리를 만들어 길이·크기·빠르기·되풀이·다시 만들기·놓기를 재고,
//   whisper_onnx\base가 있으면 Whisper가 알아듣는지(글자 틀린 비율, CER)를 윈도우 목소리와 견준다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bakkum
{
    static partial class ToolTest
    {
        const string VoiceLesson = "오늘은 물의 순환에 대해 배워요. 바닷물이 햇볕을 받으면 수증기가 되어 하늘로 올라가요. 수증기가 모여 구름이 되고, 구름이 무거워지면 비가 되어 내려요.";

        /// 재료 없이: 받을 목록·이름·조각·표시·점검(video 묶음에서 부른다)
        static void NaturalVoiceLogic()
        {
            Cli.Print("[자연스러운 목소리 · 재료 없이]");
            var parts = NaturalVoices.Parts;
            Check("받을 목록: 커밋 고정·파일 16개·모두 401,283,498바이트(약 383MB)",
                parts.Length == 16 && NaturalVoices.Commit.Length == 40 && parts.All(p => p.Sha256.Length == 64 && p.Size > 0)
                && parts.Select(p => p.Path).Distinct().Count() == 16 && NaturalVoices.DownloadBytes == 401283498 && WhisperModels.Words(NaturalVoices.DownloadBytes) == "383MB",
                NaturalVoices.DownloadBytes + "바이트 · " + WhisperModels.Words(NaturalVoices.DownloadBytes));
            string url = NaturalVoices.UrlOf(parts[3]);
            Check("받는 주소: huggingface.co의 고정 커밋", url == "https://huggingface.co/supertone-oss-archive/supertonic-3/resolve/aafc6e32416a594460b32413efc49d7fe4ce6d46/onnx/vector_estimator.onnx", url);
            Check("목소리 id: st:F1~F5·M1~M5만, 이름은 '여자 1'·'남자 5'",
                NaturalVoices.IsNatural("st:F1") && NaturalVoices.IsNatural("st:M5") && !NaturalVoices.IsNatural("st:F6") && !NaturalVoices.IsNatural("st:X1")
                && !NaturalVoices.IsNatural(null) && !NaturalVoices.IsNatural("ko1") && NaturalVoices.NameOf("st:F1") == "여자 1" && NaturalVoices.NameOf("st:M5") == "남자 5"
                && NaturalVoices.DefaultVoice == "st:F1", NaturalVoices.NameOf("st:M3"));
            Check("큰 파일은 이 PC 안쪽(캐시 폴더)에 둔다", NaturalVoices.Folder.StartsWith(AppInfo.CacheDir, StringComparison.OrdinalIgnoreCase), NaturalVoices.Folder);
            List<VTts.Voice> here = null;
            try { here = VTts.Voices(); }
            catch (Exception ex) { Cli.Print("       참고: 윈도우 목소리 목록 실패 " + ex.GetType().Name); }
            Check("받기 전에는 목소리 목록에 자연스러운 목소리가 없다", !NaturalVoices.Ready() && (here == null || !here.Any(v => v.Natural)), here == null ? "목록 없음" : here.Count + "개");
            string blocked = WhisperModels.BlockedFor(NaturalVoices.Words);
            Check("받기가 막히면 '자연스러운 목소리'라고 알리고 허용할 주소를", blocked.Contains("자연스러운 목소리 받기가 막힌") && blocked.Contains("huggingface.co") && blocked.Contains("hf.co")
                && WhisperModels.Blocked.Contains("자동 자막 모델 받기가 막힌"), blocked);

            // ── 영어 자막 ──
            var fake = new List<VTts.Voice>
            {
                new VTts.Voice { Id = "ko1", Lang = "ko-KR", Korean = true },
                new VTts.Voice { Id = "en1", Lang = "en-US" },
                new VTts.Voice { Id = "st:F1", Lang = "ko-KR", Korean = true, Natural = true }
            };
            Check("영어 자막: 자연스러운 목소리는 같은 목소리로(영어로 읽는다), 윈도우 목소리는 영어 목소리로",
                VLecture.VoiceFor("See you tomorrow.", "st:F1", fake) == "st:F1" && VLecture.VoiceFor("See you tomorrow.", "ko1", fake) == "en1" && VLecture.VoiceFor("내일 봐요", "st:M2", fake) == "st:M2", null);

            // ── 자연스러운 목소리가 안 될 때(여기서는 받지 않은 채로 불러 본다: 메모리가 모자라거나 열리지 않을 때와 같은 길) ──
            var winKo = here == null ? null : here.FirstOrDefault(v => v.Korean && !v.Natural);
            if (winKo != null)
            {
                var winEn = here.FirstOrDefault(v => !v.Natural && (v.Lang ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase));
                var batch = new VTts.Batch(here, "없는 목소리");
                string v1 = "st:F1", v2 = "st:M2", v3 = "st:F3";
                string w1 = null, w2 = null, w3 = null, err = null;
                var swb = Stopwatch.StartNew();
                try
                {
                    w1 = batch.Speak("자연스러운 목소리를 쓸 수 없을 때예요.", ref v1, 1.0);
                    w2 = batch.Speak("다음 줄은 바로 윈도우 목소리로 만들어요.", ref v2, 1.0);
                    w3 = batch.Speak("See you tomorrow.", ref v3, 1.0);
                }
                catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
                Check("자연스러운 목소리가 안 되면 윈도우 목소리로 이어서 만들고 까닭을 적어 둔다(영어 줄은 영어 목소리로)",
                    err == null && File.Exists(w1) && File.Exists(w2) && File.Exists(w3) && v1 == winKo.Id && v2 == winKo.Id && v3 == (winEn != null ? winEn.Id : winKo.Id)
                    && batch.Switched == 3 && batch.Unreadable == 0 && batch.Because == "자연스러운 목소리 파일이 없어서",
                    err ?? batch.Because + " · " + batch.Switched + "줄 · " + swb.ElapsedMilliseconds + "ms");
            }
            else Cli.Print("       참고: 이 PC에 윈도우 한국어 목소리가 없어 '윈도우 목소리로 이어서' 시험을 건너뜀");
            string noWin = null;
            try
            {
                string vn = "st:F1";
                new VTts.Batch(fake.Where(v => v.Natural).ToList(), null).Speak("윈도우 목소리도 없을 때", ref vn, 1.0);
            }
            catch (NaturalVoiceException ex) { noWin = ex.Message; }
            Check("윈도우 한국어 목소리도 없으면 바꾸지 않고 까닭을 그대로 알린다", noWin != null && noWin.Contains("자연스러운 목소리 받기"), noWin);

            // ── 읽을 조각 ──
            var ch = NaturalVoiceEngine.Chunks("오늘은 물의 순환을 배워요. Let's start the lesson! 수증기가 모여 구름이 돼요.\n\n두 번째 문단이에요.");
            Check("읽을 조각: 문장 끝에서 나누고, 한국어·영어·문단은 따로",
                ch.Count == 4 && ch[0].Lang == "ko" && ch[1].Lang == "en" && ch[1].Text == "Let's start the lesson!" && ch[2].Lang == "ko" && ch[3].Text == "두 번째 문단이에요.",
                string.Join(" | ", ch.Select(c => c.Lang + ":" + c.Text)));
            var same = NaturalVoiceEngine.Chunks("하나예요. 둘이에요. 셋이에요.");
            Check("짧은 같은 말 문장은 한 조각으로 붙인다(사이가 자연스럽게)", same.Count == 1 && same[0].Text == "하나예요. 둘이에요. 셋이에요.", same.Count + "조각");
            string longKo = string.Join(" ", Enumerable.Repeat("수증기가 모여 구름이 되고 구름이 무거워지면 비가 되어 내려요", 6));
            var lc = NaturalVoiceEngine.Chunks(longKo);
            Check("마침표 없는 긴 글은 120자 안으로 띄어쓰기에서 나눈다(글자는 그대로)", lc.Count >= 2 && lc.All(c => c.Text.Length <= 120) && string.Join(" ", lc.Select(c => c.Text)) == longKo,
                string.Join(" · ", lc.Select(c => c.Text.Length + "자")));
            string longEn = string.Join(", ", Enumerable.Repeat("the water goes up into the sky and becomes a cloud", 8)) + ".";
            var le = NaturalVoiceEngine.Chunks(longEn);
            Check("긴 영어 글은 300자 안으로 쉼표에서 나눈다", le.Count >= 2 && le.All(c => c.Text.Length <= 300 && c.Lang == "en"), string.Join(" · ", le.Select(c => c.Text.Length + "자")));
            Check("빈 글·띄어쓰기뿐인 글은 조각이 없다", NaturalVoiceEngine.Chunks("").Count == 0 && NaturalVoiceEngine.Chunks("  \n\n ").Count == 0 && NaturalVoiceEngine.Chunks(null).Count == 0, null);

            // ── AI 목소리 표시·점검 ──
            foreach (var aspect in new[] { "16:9", "9:16", "1:1", "4:5" })
            {
                var p = new VProject { Aspect = aspect };
                p.Clips.Add(new VClip { Kind = "color", Dur = 10 });
                var sub = new VItem { Kind = "text", Subtitle = true, Text = "오늘은 물의 순환", Start = 1, End = 3 };
                p.Items.Add(sub);
                p.Sounds.Add(new VSound { Kind = "voice", Path = "없음.wav", Out = 2, Start = 1, Voice = "st:F1", ItemId = sub.Id, Text = sub.Text });
                var warn = VideoExport.AiVoice(p, false);
                var audio = VideoExport.AiVoice(p, true);
                bool added = VAiLabel.Ensure(p);
                var label = p.Items.Last();
                bool again = VAiLabel.Ensure(p);
                int w, h;
                p.CanvasSize(1080, out w, out h);
                var box = VOverlay.Box(label, VAnim.Item(label, 1), w, h);
                bool inside = box.Left >= w * 0.02 && box.Top >= h * 0.02 && box.Right <= w * 0.6 && box.Bottom <= h * 0.3;
                bool safe = true;
                if (p.AspectRatio < 0.8)
                {
                    // 내보내기 점검(HiddenZones)과 같은 곳: 위 12%, 아래 22%, 오른쪽 14%(30~80%)
                    var vb = VOverlay.Box(label, VAnim.Item(label, 1), 1080, 1920);
                    safe = !vb.IntersectsWith(new System.Windows.Rect(0, 0, 1080, 1920 * 0.12)) && !vb.IntersectsWith(new System.Windows.Rect(0, 1920 * 0.78, 1080, 1920 * 0.22))
                        && !vb.IntersectsWith(new System.Windows.Rect(1080 * 0.86, 1920 * 0.3, 1080 * 0.14, 1920 * 0.5));
                }
                Check("AI 목소리 표시(" + aspect + "): 없으면 내보내기에서 알리고 넣어 주며, 왼쪽 위 안전한 곳에 영상 끝까지",
                    warn != null && warn.Level == "warn" && warn.Fix == "ai_label" && audio != null && audio.Level == "info" && audio.Text.Contains("밝혀")
                    && added && !again && label.Text == "AI 목소리" && label.Start == 0 && Math.Abs(label.End - p.Duration) < 1e-9 && !label.Subtitle
                    && VideoExport.AiVoice(p, false) == null && inside && safe,
                    "칸 " + Math.Round(box.Left) + "," + Math.Round(box.Top) + " " + Math.Round(box.Width) + "×" + Math.Round(box.Height) + " / " + w + "×" + h);
            }
            var own = new VProject();
            own.Clips.Add(new VClip { Kind = "color", Dur = 5 });
            own.Sounds.Add(new VSound { Kind = "voice", Path = "없음.wav", Out = 2, Voice = "st:M2" });
            own.Items.Add(new VItem { Kind = "text", Text = "이 영상은 AI 음성으로 읽었어요", Start = 0, End = 2 });
            bool ownOk = VAiLabel.Has(own) && VideoExport.AiVoice(own, false) == null && !VAiLabel.Ensure(own);
            own.Items[0].Text = "AI가 뭘까요?";
            bool notSaid = !VAiLabel.Has(own);
            var win = new VProject();
            win.Clips.Add(new VClip { Kind = "color", Dur = 5 });
            win.Sounds.Add(new VSound { Kind = "voice", Path = "없음.wav", Out = 2, Voice = "ko1" });
            Check("직접 쓴 'AI 음성' 글도 밝힌 것으로 본다(겹쳐 넣지 않음). 'AI가 뭘까요?'는 아니다. 윈도우 목소리만 쓰면 알리지 않는다",
                ownOk && notSaid && VideoExport.AiVoice(win, false) == null && VideoExport.AiVoice(win, true) == null && !VAiLabel.Uses(win), null);
            var undo = own.Clone();
            Check("표시는 편집에 저장되고 되돌리기 사본에도 남는다", VProject.FromJson(own.ToJson()).Items.Count == own.Items.Count && undo.Items.Count == own.Items.Count, null);
        }

        // ── 'voice' 묶음 ──

        static string Sha256Hex(byte[] b)
        {
            using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(b).Select(x => x.ToString("x2")));
        }

        static NaturalVoices.Part FakePart(string path, byte[] body)
        {
            return new NaturalVoices.Part { Path = path, Size = body.Length, Sha256 = Sha256Hex(body) };
        }

        static void WavInfo(string path, out int channels, out int rate)
        {
            var b = new byte[44];
            using (var f = File.OpenRead(path)) f.Read(b, 0, 44);
            channels = BitConverter.ToInt16(b, 22);
            rate = BitConverter.ToInt32(b, 24);
        }

        static double Rms(float[] x)
        {
            double s = 0;
            foreach (var v in x) s += v * v;
            return x.Length == 0 ? 0 : Math.Sqrt(s / x.Length);
        }

        static void VoiceTests()
        {
            Cli.Print("[자연스러운 목소리]");
            string src = P("supertonic");
            if (!Directory.Exists(src))
            {
                Check("시험 재료(supertonic)가 시험 폴더에 있다", false, src);
                return;
            }
            Func<NaturalVoices.Part, string> local = p => Path.Combine(src, p.Path.Replace('/', '\\'));
            var sw = Stopwatch.StartNew();
            bool localSame = NaturalVoices.Parts.All(p => File.Exists(local(p)) && new FileInfo(local(p)).Length == p.Size && WhisperModels.Sha256Of(local(p)) == p.Sha256);
            Check("11-11에 받아 둔 Supertonic 3 파일이 받을 목록의 크기·SHA-256과 같다", localSame, sw.ElapsedMilliseconds + "ms");

            // ── 받기(이 PC 안 서버, 작은 가짜 파일) ──
            var rnd = new Random(11);
            Func<int, byte[]> bytes = n =>
            {
                var b = new byte[n];
                rnd.NextBytes(b);
                return b;
            };
            byte[] a = bytes(300000), big = bytes(4000000), c = Encoding.UTF8.GetBytes("{\"style_ttl\":1}");
            var fakeParts = new[] { FakePart("onnx/a.onnx", a), FakePart("onnx/big.onnx", big), FakePart("voice_styles/F1.json", c) };
            const string Commit = "1111111111111111111111111111111111111111";
            string fakeFolder = Path.Combine(AppInfo.CacheDir, "voicefake");
            using (var server = new RangeServer())
            {
                string root = "/test/voice/resolve/" + Commit + "/";
                server.Files[root + "onnx/a.onnx"] = a;
                server.Files[root + "onnx/big.onnx"] = big;
                server.Files[root + "voice_styles/F1.json"] = c;
                NaturalVoices.UrlBase = "http://127.0.0.1:" + server.Port + "/";
                try
                {
                    string bigPart = Path.Combine(fakeFolder + ".받는중", @"onnx\big.onnx");
                    bool cancelled = false;
                    try
                    {
                        NaturalVoices.Prepare("test/voice", Commit, fakeParts, fakeFolder, delegate { }, () => File.Exists(bigPart) && new FileInfo(bigPart).Length > 200000);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                    long half = File.Exists(bigPart) ? new FileInfo(bigPart).Length : 0;
                    Check("받다가 멈추면 받던 파일을 남겨 둔다(다 받지 않았으면 쓸 수 없다)", cancelled && half > 0 && half < big.Length && !NaturalVoices.ReadyAt(fakeFolder, Commit, fakeParts), half + "/" + big.Length + "바이트");

                    lock (server.Asked) server.Asked.Clear();
                    double lastFrac = 0;
                    var steps = new List<string>();
                    NaturalVoices.Prepare("test/voice", Commit, fakeParts, fakeFolder, delegate (string what, double f)
                    {
                        lastFrac = f;
                        lock (steps) if (!steps.Contains(what)) steps.Add(what);
                    }, () => false);
                    List<string> asked;
                    lock (server.Asked) asked = server.Asked.ToList();
                    bool resumed = asked.Any(x => x.Contains("big.onnx") && !x.EndsWith(" 0", StringComparison.Ordinal));
                    bool noRefetch = !asked.Any(x => x.Contains("/a.onnx"));
                    bool placed = NaturalVoices.ReadyAt(fakeFolder, Commit, fakeParts) && !Directory.Exists(fakeFolder + ".받는중") && SameBytes(Path.Combine(fakeFolder, @"onnx\big.onnx"), big);
                    Check("다시 누르면 끊긴 곳부터 이어 받고, 다 받은 파일은 다시 받지 않는다", resumed && noRefetch && placed, string.Join(" | ", asked.Select(x => x.Substring(x.LastIndexOf('/') + 1))));
                    Check("진행 글과 막대가 끝까지 간다", Math.Abs(lastFrac - 1) < 1e-9 && steps.Contains("자연스러운 목소리를 받는 중") && steps.Contains("다 받았어요"), string.Join(" → ", steps));

                    lock (server.Asked) server.Asked.Clear();
                    NaturalVoices.Prepare("test/voice", Commit, fakeParts, fakeFolder, delegate { }, () => false);
                    Check("이미 받았으면 인터넷에 묻지 않는다", server.Asked.Count == 0, null);

                    // 받은 파일이 원래와 다르면(학교 인터넷이 바꾼 파일) 지우고 멈춘다
                    var bad = (byte[])a.Clone();
                    bad[bad.Length / 2] ^= 0x20;
                    server.Files["/test/bad/resolve/" + Commit + "/onnx/a.onnx"] = bad;
                    string badFolder = Path.Combine(AppInfo.CacheDir, "voicebad");
                    string msg = null;
                    try { NaturalVoices.Prepare("test/bad", Commit, fakeParts.Take(1).ToArray(), badFolder, delegate { }, () => false); }
                    catch (UserFacingException ex) { msg = ex.Message; }
                    Check("받은 파일이 원래와 다르면 지우고 멈춘다", msg != null && msg.Contains("원래 파일과 달라요") && !File.Exists(Path.Combine(badFolder + ".받는중", @"onnx\a.onnx")) && !NaturalVoices.ReadyAt(badFolder, Commit, fakeParts.Take(1).ToArray()), msg);

                    // 학교 인터넷이 막으면(403·차단 안내 페이지) 누구에게 무엇을 부탁할지
                    server.Raw["/test/blocked/resolve/" + Commit + "/onnx/a.onnx"] = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    const string Html = "<html><body>blocked</body></html>";
                    server.Raw["/test/html/resolve/" + Commit + "/onnx/a.onnx"] = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + Html.Length + "\r\nConnection: close\r\n\r\n" + Html;
                    string forbid = null, html = null;
                    try { NaturalVoices.Prepare("test/blocked", Commit, fakeParts.Take(1).ToArray(), Path.Combine(AppInfo.CacheDir, "voiceblocked"), delegate { }, () => false); }
                    catch (UserFacingException ex) { forbid = ex.Message; }
                    try { NaturalVoices.Prepare("test/html", Commit, fakeParts.Take(1).ToArray(), Path.Combine(AppInfo.CacheDir, "voicehtml"), delegate { }, () => false); }
                    catch (UserFacingException ex) { html = ex.Message; }
                    Check("학교 인터넷이 막으면(403·차단 안내 페이지) '자연스러운 목소리'와 허용할 주소를 알린다", forbid == WhisperModels.BlockedFor(NaturalVoices.Words) && html == forbid, forbid + " / " + html);
                    string missing = null;
                    try { NaturalVoices.Prepare("test/missing", Commit, fakeParts.Take(1).ToArray(), Path.Combine(AppInfo.CacheDir, "voicemissing"), delegate { }, () => false); }
                    catch (UserFacingException ex) { missing = ex.Message; }
                    Check("그 밖의 실패는 다시 눌러 보게 한다(이어 받는다)", missing != null && missing.StartsWith("자연스러운 목소리를 받지 못했어요", StringComparison.Ordinal) && missing.Contains("이어서"), missing);
                }
                finally
                {
                    NaturalVoices.UrlBase = "https://huggingface.co/";
                    foreach (var d in new[] { "voicefake", "voicebad", "voiceblocked", "voicehtml", "voicemissing" })
                        foreach (var x in new[] { Path.Combine(AppInfo.CacheDir, d), Path.Combine(AppInfo.CacheDir, d) + ".받는중" })
                            try { if (Directory.Exists(x)) Directory.Delete(x, true); } catch { }
                }
            }

            // ── 진짜 목소리 넣기(시험 폴더에서 복사) ──
            sw.Restart();
            NaturalVoices.LocalSource = p => local(p);
            try
            {
                NaturalVoices.Prepare(delegate { }, () => false);
            }
            finally
            {
                NaturalVoices.LocalSource = null;
            }
            long used = NaturalVoices.UsedBytes();
            Check("받은 목소리: 캐시 폴더에 두고, 모두 확인한 뒤에 쓸 수 있다", NaturalVoices.Ready() && !Directory.Exists(NaturalVoices.Folder + ".받는중") && used >= NaturalVoices.DownloadBytes && used < NaturalVoices.DownloadBytes + 1024 * 1024,
                WhisperModels.Words(used) + " · " + sw.ElapsedMilliseconds + "ms");
            Check("조건 전문(LICENSE)도 함께 받는다", File.Exists(Path.Combine(NaturalVoices.Folder, "LICENSE")) && File.ReadAllText(Path.Combine(NaturalVoices.Folder, "LICENSE")).Contains("RAIL"), null);
            List<VTts.Voice> voices = null;
            try { voices = VTts.Voices(); }
            catch (Exception ex) { Cli.Print("       참고: 목소리 목록 실패 " + ex.GetType().Name); }
            var nat = voices == null ? new List<VTts.Voice>() : voices.Where(v => v.Natural).ToList();
            Check("받으면 목소리 목록 끝에 여자·남자 5개씩(윈도우 목소리 뒤)", nat.Count == 10 && nat.All(v => v.Korean && NaturalVoices.IsNatural(v.Id)) && nat[0].Name == "여자 1" && nat[9].Name == "남자 5"
                && (voices.Count == 10 || !voices[0].Natural), voices == null ? "목록 없음" : string.Join(", ", voices.Select(v => v.Name)));

            // ── 목소리 만들기 ──
            long memBefore = PeakMb();
            sw.Restart();
            var first = NaturalVoiceEngine.Speak(VoiceLesson, "st:F1", 1.0);
            long firstMs = sw.ElapsedMilliseconds;
            double sec = first.Length / (double)NaturalVoiceEngine.SampleRate;
            float peak = first.Length == 0 ? 0 : first.Max(v => Math.Abs(v));
            Check("처음 읽기(여는 시간 포함): 길이가 글에 맞고 소리가 난다", sec > 5 && sec < 20 && Rms(first) > 0.02 && peak <= 0.981f && NaturalVoiceEngine.Loaded,
                "소리 " + sec.ToString("0.0") + "초 · 걸린 시간 " + firstMs + "ms · RMS " + Rms(first).ToString("0.000") + " · 꼭대기 " + peak.ToString("0.000"));
            sw.Restart();
            var again = NaturalVoiceEngine.Speak(VoiceLesson, "st:F1", 1.0);
            long againMs = sw.ElapsedMilliseconds;
            Check("같은 글·같은 목소리는 늘 같은 소리(다시 만들어도 바뀌지 않는다)", again.Length == first.Length && again.SequenceEqual(first), null);
            double rtf = againMs / 1000.0 / sec;
            Check("열어 둔 뒤에는 실제 길이보다 빨리 만든다(배터리면 2배쯤 느림)", rtf < 1.0, "소리 1초에 " + rtf.ToString("0.00") + "초 · " + againMs + "ms");
            var fast = NaturalVoiceEngine.Speak(VoiceLesson, "st:F1", 1.5);
            double ratio = fast.Length / (double)first.Length;
            Check("음성 빠르기 1.5배는 그만큼 짧다", ratio > 0.55 && ratio < 0.8, "1.5배 길이 비율 " + ratio.ToString("0.00"));
            var m3 = NaturalVoiceEngine.Speak(VoiceLesson, "st:M3", 1.0);
            Check("목소리를 바꾸면 소리가 바뀐다", m3.Length > 0 && (m3.Length != first.Length || !m3.SequenceEqual(first)) && Rms(m3) > 0.02, "남자 3 " + (m3.Length / 44100.0).ToString("0.0") + "초");
            var en = NaturalVoiceEngine.Speak("Let's review the water cycle together. Clouds are made of tiny drops of water.", "st:M1", 1.0);
            double enSec = en.Length / 44100.0;
            Check("영어 문장도 읽는다", enSec > 2 && enSec < 12 && Rms(en) > 0.02, enSec.ToString("0.0") + "초");
            string odd = null;
            float[] oddWav = null;
            try { oddWav = NaturalVoiceEngine.Speak("오늘 날씨 😀 맑음 漢字 ♥ #해시", "st:F2", 1.0); }
            catch (Exception ex) { odd = ex.GetType().Name + ": " + ex.Message; }
            string none = null;
            try { NaturalVoiceEngine.Speak("😀😀 ♥", "st:F2", 1.0); }
            catch (UserFacingException ex) { none = ex.Message; }
            Check("이모지·한자 같은 모르는 글자는 건너뛰고, 읽을 글자가 없으면 쉬운 말로 알린다", odd == null && oddWav != null && oddWav.Length > 44100 / 2 && none == "읽을 글이 없어요.", odd ?? none);
            string longText = string.Join(" ", Enumerable.Repeat("수증기가 모여 구름이 되고 구름이 무거워지면 비가 되어 내려요.", 5));
            sw.Restart();
            var longWav = NaturalVoiceEngine.Speak(longText, "st:F4", 1.0);
            Check("긴 글은 나눠 읽고 사이를 띄운다", longWav.Length / 44100.0 > 10 && Rms(longWav) > 0.02, (longWav.Length / 44100.0).ToString("0.0") + "초 · " + sw.ElapsedMilliseconds + "ms");
            long memPeak = PeakMb();
            Check("쓰는 메모리가 안내한 만큼(약 " + WhisperModels.Words(NaturalVoices.Memory) + ") 안", (memPeak - memBefore) * 1024L * 1024 < NaturalVoices.Memory, "시험 프로그램 최대 " + memPeak + "MB(전 " + memBefore + "MB)");

            // ── 자막 음성 파일 ──
            sw.Restart();
            string wav = VTts.Speak("물이 하늘로 올라가요.", "st:F1", 1.0);
            long wavMs = sw.ElapsedMilliseconds;
            int chn, rate;
            WavInfo(wav, out chn, out rate);
            sw.Restart();
            string wav2 = VTts.Speak("물이 하늘로 올라가요.", "st:F1", 1.0);
            long hitMs = sw.ElapsedMilliseconds;
            string wav3 = VTts.Speak("물이 하늘로 올라가요.", "st:M1", 1.0);
            int rateIn;
            var head = ReadWav(wav, out rateIn);
            int lead = 0;
            while (lead < head.Length && Math.Abs(head[lead]) < 0.01f) lead++;
            Check("자막 음성 파일: 48kHz 두 줄, 앞 빈 소리 다듬고, 같은 글은 다시 만들지 않는다", chn == 2 && rate == 48000 && wav2 == wav && wav3 != wav && hitMs < 50 && lead < 48000 * 0.1 && wav.StartsWith(VTts.Folder, StringComparison.OrdinalIgnoreCase),
                "처음 " + wavMs + "ms · 다시 " + hitMs + "ms · 앞 빈 소리 " + (lead / 48.0).ToString("0") + "ms");
            var vb = new VTts.Batch(VTts.Voices(), null);
            string vSkip = "st:F2", vNext = "st:F2";
            string skipped1 = vb.Speak("😀😀 ♥", ref vSkip, 1.0);
            string next = vb.Speak("물이 하늘로 올라가요.", ref vNext, 1.0);
            Check("여러 자막을 만들 때 읽을 글자가 없는 자막만 건너뛰고, 다음 자막은 자연스러운 목소리 그대로", skipped1 == null && vb.Unreadable == 1 && vb.Because == null && vb.Switched == 0
                && vSkip == "st:F2" && vNext == "st:F2" && next != null && File.Exists(next), vb.Unreadable + "개 건너뜀");

            // ── 다시 만들기(다른 PC에서 열 때) ──
            var rp = new VProject();
            var lost = new VSound { Kind = "voice", Path = Path.Combine(VTts.Folder, "다른피시_자연.wav"), Text = "물이 하늘로\n올라가요.", Voice = "st:F2", Rate = 1.0, Out = 99 };
            rp.Sounds.Add(lost);
            int remade = VTts.Remake(rp);
            Check("소리 파일이 없는 자연스러운 목소리 음성은 저장된 글·목소리로 다시 만든다", remade == 1 && File.Exists(lost.Path) && lost.Out < 5 && lost.Voice == "st:F2", remade + "개 · " + lost.Out.ToString("0.00") + "초");
            double keep = VTts.NaturalRemakeBudget;
            var slow = new VProject();
            var late = new VSound { Kind = "voice", Path = Path.Combine(VTts.Folder, "다른피시_늦음.wav"), Text = "구름이 돼요.", Voice = "st:M4", Rate = 1.0, Out = 99 };
            slow.Sounds.Add(late);
            VTts.NaturalRemakeBudget = -1;
            int skipped;
            try { skipped = VTts.Remake(slow); }
            finally { VTts.NaturalRemakeBudget = keep; }
            Check("여는 창을 오래 붙잡지 않게, 정한 시간이 지나면 자연스러운 목소리는 더 만들지 않는다(여는 쪽이 한 번에 알림)", skipped == 0 && !File.Exists(late.Path), null);

            // ── 놓기 ──
            var th = new Thread(delegate ()
            {
                try { NaturalVoiceEngine.Speak(longText, "st:F3", 1.0); }
                catch { }
            });
            th.Start();
            Thread.Sleep(500);
            sw.Restart();
            NaturalVoiceEngine.Release();
            long relMs = sw.ElapsedMilliseconds;
            bool stillLoaded = NaturalVoiceEngine.Loaded;
            th.Join();
            Check("읽는 중에 놓으라고 하면 기다리지 않고, 다 읽은 뒤에 놓는다(편집 창을 닫을 때)", relMs < 400 && stillLoaded && !NaturalVoiceEngine.Loaded, "놓기 " + relMs + "ms");
            sw.Restart();
            var reopened = NaturalVoiceEngine.Speak("다시 열었어요.", "st:F5", 1.0);
            Check("놓은 뒤 다시 읽으면 다시 연다", reopened.Length > 0 && NaturalVoiceEngine.Loaded, sw.ElapsedMilliseconds + "ms");

            // ── Whisper가 알아듣는지(글자 틀린 비율) ──
            var wb = WhisperModels.Base;
            if (wb.Parts.All(p => File.Exists(WFile(wb, p))))
            {
                WhisperModels.LocalSource = p => WFile(wb, p);
                try { WhisperModels.Prepare(wb, delegate { }, () => false); }
                finally { WhisperModels.LocalSource = null; }
                var results = new List<string>();
                double worst = 0;
                using (var eng = new WhisperEngine(WhisperModels.Folder(wb)))
                {
                    foreach (var id in new[] { "st:F1", "st:M1" })
                    {
                        var x = NaturalVoiceEngine.Speak(VoiceLesson, id, 1.0);
                        string heard = SegText(eng.Transcribe(WhisperAudio.Resample(x, NaturalVoiceEngine.SampleRate), "ko", delegate { }, () => false));
                        double cer = Cer(heard, VoiceLesson);
                        worst = Math.Max(worst, cer);
                        results.Add(NaturalVoices.NameOf(id) + " " + Pct(cer));
                        if (cer > 0) Cli.Print("       " + NaturalVoices.NameOf(id) + " 받아쓰기: " + heard);
                    }
                    var ko = voices == null ? null : voices.FirstOrDefault(v => v.Korean && !v.Natural);
                    if (ko != null)
                    {
                        int wr;
                        var y = ReadWav(VTts.Speak(VoiceLesson, ko.Id, 1.0), out wr);
                        string heardWin = SegText(eng.Transcribe(WhisperAudio.Resample(y, wr), "ko", delegate { }, () => false));
                        results.Add("윈도우 목소리 " + Pct(Cer(heardWin, VoiceLesson)));
                    }
                }
                Check("Whisper(빠르게 모델)가 자연스러운 목소리를 알아듣는다(글자 틀린 비율 15% 이하)", worst <= 0.15, string.Join(" · ", results));
            }
            else Cli.Print("       참고: whisper_onnx\\base가 없어 받아쓰기 시험은 건너뜀");

            // ── 지우기 ──
            NaturalVoices.DeleteAll();
            List<VTts.Voice> after = null;
            try { after = VTts.Voices(); }
            catch { }
            Check("받은 목소리를 지우면 엔진도 놓고, 목록에서 빠진다", !NaturalVoices.Ready() && !NaturalVoiceEngine.Loaded && !Directory.Exists(NaturalVoices.Folder) && NaturalVoices.UsedBytes() == 0 && (after == null || !after.Any(v => v.Natural)), null);
            string gone = null;
            try { NaturalVoiceEngine.Speak("없어요.", "st:F1", 1.0); }
            catch (UserFacingException ex) { gone = ex.Message; }
            Check("지운 뒤에 쓰려 하면 다시 받으라고 알린다", gone != null && gone.Contains("자연스러운 목소리 받기"), gone);
        }

        static bool SameBytes(string path, byte[] want)
        {
            return File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(want);
        }
    }
}
