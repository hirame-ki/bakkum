// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집: 자동 자막(말소리를 듣고 자막 만들기). Whisper 모델은 처음 쓸 때 한 번만 받는다(받는 곳·크기를 먼저 묻는다).
// 받은 뒤에는 인터넷 없이 이 PC 안에서 돌고, 영상의 소리는 PC 밖으로 나가지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Bakkum
{
    public sealed partial class VideoEditor
    {
        /// 고른 모델(null이면 이 PC에 맞는 것)과 말
        string autoSubModel;
        string autoSubLang = "ko";

        WhisperModel AutoSubModel
        {
            get
            {
                if (autoSubModel == null) autoSubModel = WhisperModels.Default().Id;
                return WhisperModels.Find(autoSubModel);
            }
        }

        static string ModelChip(WhisperModel m)
        {
            return m.Name + " · " + (WhisperModels.Ready(m) ? "받아 둠" : "약 " + WhisperModels.Words(m.DownloadBytes));
        }

        /// 자막 칸에서 고른 자막이 없을 때: 자동 자막
        void AutoSubSection()
        {
            Label("자동 자막");
            Hint("영상의 말소리를 듣고 자막을 만들어요. 이 PC 안에서 바꾸고, 소리는 PC 밖으로 나가지 않아요. 배경 음악과 효과음은 빼고 들어요.");
            var models = WhisperModels.All;
            Chips(models.Select(m => m.Id).ToArray(), models.Select(ModelChip).ToArray(), AutoSubModel.Id, v =>
            {
                autoSubModel = v;
                RefreshPanel();
            });
            var cur = AutoSubModel;
            Hint(cur.Note + (WhisperModels.Ready(cur) ? "" : " 처음 한 번 받아요(약 " + WhisperModels.Words(cur.DownloadBytes) + "). 받은 뒤에는 인터넷 없이 돌아가요."));
            Chips(new[] { "ko", "en" }, new[] { "한국어", "영어" }, autoSubLang, v => autoSubLang = v);
            Act("자동 자막 만들기", "Primary", StartAutoSub);
            long used = WhisperModels.UsedBytes();
            if (used > 0) Act("받은 모델 지우기 (" + WhisperModels.Words(used) + ")", "Quiet", DeleteAutoSubModels);
        }

        /// 처음 받기 전에 묻는 글: 받는 곳·원래 모델·라이선스·크기·이어 받기
        internal static string DownloadBody(WhisperModel m)
        {
            return "처음 한 번만 받아요. 받은 뒤에는 인터넷 없이 이 PC 안에서 돌아가고, 영상의 소리는 PC 밖으로 나가지 않아요.\n\n"
                + "• 받는 곳: Hugging Face의 " + m.Repo + "\n"
                + "• 원래 모델: OpenAI Whisper (" + m.License + " 라이선스)\n"
                + "• 크기: 약 " + WhisperModels.Words(m.DownloadBytes) + " (이 PC에 맞게 고친 뒤 " + WhisperModels.Words(m.Disk) + "쯤 남아요)\n"
                + "• 받다가 [멈추기]를 누르면 다음에 이어서 받아요. 받은 모델은 자막 칸에서 지울 수 있어요.";
        }

        void StartAutoSub()
        {
            if (busy)
            {
                SetStatus("하던 일(" + busyWhat + ")이 끝난 뒤에 다시 눌러 주세요.");
                return;
            }
            if (project.Clips.Count == 0 || project.Duration < 0.5)
            {
                SetStatus("자막을 만들 영상이 없어요.");
                return;
            }
            var m = AutoSubModel;
            if (!WhisperModels.Ready(m) && Dialog.Ask(this, H, "subtitle", "자동 자막 모델을 받을까요?", DownloadBody(m), null, "그만두기", "받고 만들기") != 1) return;
            long total, avail;
            WhisperModels.MemoryInfo(out total, out avail);
            if (avail > 0 && avail < m.Memory + 400L * 1024 * 1024)
            {
                string body = "'" + m.Name + "' 모델은 메모리를 " + WhisperModels.Words(m.Memory) + "쯤 써요. 지금 남은 메모리는 " + WhisperModels.Words(avail) + "예요. "
                    + (m == WhisperModels.Turbo ? "다른 프로그램을 닫거나 '빠르게' 모델을 고르면 안전해요." : "다른 프로그램을 닫은 뒤에 하면 안전해요.");
                if (Dialog.Ask(this, H, "subtitle", "메모리가 모자랄 수 있어요", body, null, "그만두기", "그래도 하기") != 1) return;
            }
            int existing = project.Items.Count(i => i.Subtitle);
            int mode = 0;
            if (existing > 0)
            {
                bool voiced = project.Sounds.Any(s => s.ItemId != null && s.Kind == "voice");
                int a = Dialog.Ask(this, H, "subtitle", "이미 자막이 " + existing + "개 있어요", "새로 만든 자막으로 모두 바꾸거나, 자막이 없는 곳에만 넣을 수 있어요." + (voiced ? " 모두 바꾸면 있던 자막의 음성도 빠져요." : "") + " 어느 쪽이든 되돌리기(Ctrl+Z)로 넣기 전으로 돌아갈 수 있어요.", null, "그만두기", "모두 바꾸기", "빈 곳에만 넣기");
                if (a <= 0) return;
                mode = a;
            }
            var snap = ListenCopy(project);
            string lang = autoSubLang;
            List<WhisperText.Piece> pieces = null;
            RunBusy("자동 자막 만들기", delegate (Action<string, double> report)
            {
                Func<bool> stop = () => busyCancel;
                var sw = Stopwatch.StartNew();
                try
                {
                    WhisperModels.Prepare(m, report, stop);
                }
                catch (UserFacingException ex)
                {
                    // 받기가 막혔을 때 할 일(허용할 주소 등)은 아래 상태 줄에 다 안 보여서 창으로도 알린다. [멈추기]를 눌렀으면 띄우지 않는다.
                    if (busyCancel) throw;
                    string msg = ex.Message;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (windowOpen) Dialog.Ask(this, H, "subtitle", "자동 자막 모델을 받지 못했어요", msg, null, "확인");
                    }));
                    throw;
                }
                report("영상의 소리를 모으는 중", 0);
                var audio = WhisperAudio.FromProject(snap, f => report("영상의 소리를 모으는 중", f), stop);
                if (WhisperEngine.LoudFrames(audio, 0, audio.Length) < 8) throw new UserFacingException("영상에서 말소리를 찾지 못했어요. 영상의 소리가 꺼져 있거나 아주 작은지 확인해 주세요.");
                report("자동 자막 모델을 여는 중", -1);
                double listen = sw.Elapsed.TotalSeconds;
                using (var eng = new WhisperEngine(WhisperModels.Folder(m)))
                {
                    report("말소리를 살펴보는 중", -1);
                    var segs = eng.Transcribe(audio, lang, f => report("말을 글로 바꾸는 중", f), stop);
                    pieces = WhisperText.Pieces(segs);
                    Log.Write("자동 자막: " + m.Id + " · 소리 " + Math.Round(audio.Length / (double)WhisperEngine.SampleRate) + "초 · 준비 " + Math.Round(listen) + "초 · 모두 " + Math.Round(sw.Elapsed.TotalSeconds) + "초 · 구간 " + segs.Count + " · 자막 " + pieces.Count + " · 고요해 건너뛴 30초 " + eng.SkippedWindows + "/" + (eng.SkippedWindows + eng.Windows));
                }
            }, delegate { ApplyAutoSub(pieces, mode); });
        }

        /// 자동 자막이 들을 편집: 들리는 그대로 듣되, 배경 음악·효과음은 빼고(음악에서 없는 말을 지어내지 않게) 끝을 줄이지 않는다
        internal static VProject ListenCopy(VProject p)
        {
            var snap = p.Clone();
            snap.Sounds.RemoveAll(s => s.IsMusic || s.Kind == "effect");
            snap.EndFade = false;
            return snap;
        }

        /// 자동 자막 조각을 자막으로. mode 2는 있는 자막과 겹치지 않는 것만.
        internal static List<VItem> AutoSubItems(VProject p, List<WhisperText.Piece> pieces, int mode)
        {
            var list = new List<VItem>();
            double d = p.Duration;
            var style = p.Items.LastOrDefault(i => i.Subtitle);
            var keep = mode == 2 ? p.Items.Where(i => i.Subtitle).ToList() : new List<VItem>();
            foreach (var pc in pieces)
            {
                if (string.IsNullOrWhiteSpace(pc.Text) || pc.Start >= d - 0.05) continue;
                double start = Math.Max(0, pc.Start), end = Math.Min(d, pc.End);
                if (end - start < 0.2) continue;
                if (keep.Any(k => k.Start < end - 0.05 && start + 0.05 < k.End)) continue;
                var it = new VItem { Kind = "text", Subtitle = true, Text = pc.Text, Start = start, End = end, X = 0.5, Y = 0.86, W = 0.86, Box = false };
                if (style != null)
                {
                    it.TakeStyleFrom(style);
                    it.X = style.X;
                    it.Y = style.Y;
                    it.W = style.W;
                }
                list.Add(it);
            }
            return list;
        }

        void ApplyAutoSub(List<WhisperText.Piece> pieces, int mode)
        {
            if (pieces == null || pieces.Count == 0)
            {
                SetStatus("말소리를 글로 바꾸지 못했어요. 말소리가 작거나 음악만 있는 영상일 수 있어요.");
                return;
            }
            var items = AutoSubItems(project, pieces, mode);
            if (items.Count == 0)
            {
                SetStatus(mode == 2 ? "자막이 없는 곳에서 새로 넣을 말을 찾지 못했어요." : "영상 안에서 넣을 자막을 찾지 못했어요.");
                return;
            }
            int removed = 0, unvoiced = 0;
            Change(null, delegate { removed = PutAutoSubs(project, items, mode, out unvoiced); });
            SetTab("subtitle");
            timeline.InvalidateVisual();
            RefreshPanel();
            SetStatus("자동 자막 " + items.Count + "개를 넣었어요" + (removed > 0 ? "(있던 자막 " + removed + "개" + (unvoiced > 0 ? "와 그 음성 " + unvoiced + "개" : "") + "는 뺐어요)" : "") + ". 틀린 글자가 있을 수 있으니 재생하며 훑어보세요. 자주 틀린 말은 '찾아 바꾸기'로 한 번에 고칠 수 있고, 되돌리기(Ctrl+Z)로 넣기 전으로 돌아갈 수 있어요.");
        }

        /// 자동 자막을 넣는다. mode 1(모두 바꾸기)이면 있던 자막을 빼고, 그 자막의 음성도 뺀다(자막을 지울 때처럼.
        /// 남기면 없는 자막의 옛 글을 읽는다). 뺀 자막 수, unvoiced는 뺀 음성 수.
        internal static int PutAutoSubs(VProject p, List<VItem> items, int mode, out int unvoiced)
        {
            int removed = 0;
            unvoiced = 0;
            if (mode == 1)
            {
                removed = p.Items.RemoveAll(i => i.Subtitle);
                unvoiced = p.Sounds.RemoveAll(s => s.ItemId != null && p.FindItem(s.ItemId) == null);
            }
            p.Items.AddRange(items);
            return removed;
        }

        void DeleteAutoSubModels()
        {
            if (busy)
            {
                SetStatus("하던 일(" + busyWhat + ")이 끝난 뒤에 다시 눌러 주세요.");
                return;
            }
            long used = WhisperModels.UsedBytes();
            if (Dialog.Ask(this, H, "trash", "받은 자동 자막 모델을 지울까요?", "디스크에서 " + WhisperModels.Words(used) + "를 비워요. 만든 자막은 그대로 있어요. 다음에 자동 자막을 만들 때 다시 받아요.", null, "그만두기", "지우기") != 1) return;
            try
            {
                WhisperModels.DeleteAll();
                SetStatus("받은 자동 자막 모델을 지웠어요.");
            }
            catch (Exception ex)
            {
                Log.Write("자동 자막 모델 지우기 실패: " + ex.GetType().Name);
                SetStatus("모델을 다 지우지 못했어요. 바꿈을 다시 켠 뒤 한 번 더 해 주세요.");
            }
            RefreshPanel();
        }
    }
}
