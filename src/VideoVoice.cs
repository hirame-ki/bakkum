// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 창의 목소리 고르기: 자연스러운 목소리(AI, 처음 쓸 때 받음)와 윈도우 목소리.
// 자막 칸의 '자막을 음성으로'와 선생님 도구의 '발표자 노트를 음성으로'가 같이 쓴다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Bakkum
{
    public sealed partial class VideoEditor
    {
        /// 목소리 종류: 자연스러운 목소리(AI)가 기본. 받지 않았으면 쓸 때 받을지 묻는다(윈도우 목소리로 바꿀 수도 있다).
        bool ttsNatural = true;
        string ttsWinVoice, ttsNatVoice = NaturalVoices.DefaultVoice;

        /// 지금 고른 목소리 id(자연스러운 목소리면 "st:F1" 꼴, 윈도우 목소리를 아직 안 골랐으면 null = 한국어 목소리)
        string ttsVoice
        {
            get { return ttsNatural ? ttsNatVoice : ttsWinVoice; }
        }

        const string VoiceSample = "안녕하세요. 이 목소리로 자막을 읽어 드릴게요.";

        void TtsControls()
        {
            Chips(new[] { "natural", "windows" }, new[] { "자연스러운 목소리 (AI)", "윈도우 목소리" }, ttsNatural ? "natural" : "windows", v =>
            {
                ttsNatural = v == "natural";
                RefreshPanel();
            });
            if (ttsNatural)
            {
                if (!NaturalVoices.Ready())
                {
                    long part = NaturalVoices.UsedBytes();
                    Hint("Supertone이 공개한 AI 목소리예요. 여자·남자 목소리가 5개씩이고, 영어 문장도 영어로 읽어요. 학생 이름 같은 개인정보가 인터넷으로 나가지 않게, 목소리 파일을 처음 한 번 받아(약 "
                        + WhisperModels.Words(NaturalVoices.DownloadBytes) + ") 이 PC 안에서만 만들어요.");
                    Act(part > 0 ? "자연스러운 목소리 이어 받기" : "자연스러운 목소리 받기", "Line", delegate { DownloadNaturalVoice(null); });
                    if (part > 0) Act("받다 만 파일 지우기 (" + WhisperModels.Words(part) + ")", "Quiet", DeleteNaturalVoice);
                    return;
                }
                NaturalVoiceGrid();
                Hint("AI 목소리로 만든 음성을 넣으면 화면 왼쪽 위에 'AI 목소리' 표시도 넣어요(목소리 모델의 사용 조건). 끌어서 옮기거나 글을 바꿀 수 있어요.");
            }
            else
            {
                var korean = WindowsKoreanVoices();
                if (korean.Count == 0)
                {
                    Hint("이 PC에 한국어 윈도우 목소리가 없어요. 윈도우 설정 › 시간 및 언어 › 음성에서 더하거나, [자연스러운 목소리 (AI)]를 골라 주세요.");
                    return;
                }
                if (ttsWinVoice == null || !korean.Any(v => v.Id == ttsWinVoice)) ttsWinVoice = korean[0].Id;
                if (korean.Count > 1) Chips(korean.Select(v => v.Id).ToArray(), korean.Select(v => ShortVoice(v.Name)).ToArray(), ttsWinVoice, v => ttsWinVoice = v);
                else Hint("목소리: " + ShortVoice(korean[0].Name));
            }
            SliderRow("음성 빠르기", 0.6, 1.6, ttsRate, 0.1, Times, v => ttsRate = v);
            Act("목소리 들어 보기", "Quiet", PreviewVoice);
            if (ttsNatural) Act("받은 목소리 지우기 (" + WhisperModels.Words(NaturalVoices.UsedBytes()) + ")", "Quiet", DeleteNaturalVoice);
        }

        static List<VTts.Voice> WindowsKoreanVoices()
        {
            try
            {
                return VTts.Voices().Where(v => v.Korean && !v.Natural).ToList();
            }
            catch (Exception ex)
            {
                Log.Write("목소리 목록 실패: " + ex.GetType().Name);
                return new List<VTts.Voice>();
            }
        }

        /// 여자 1~5 · 남자 1~5를 두 줄로(한 묶음이라 하나만 골라진다). 칩이 좁아 읽는 이름은 '여자 1'처럼 따로 단다.
        void NaturalVoiceGrid()
        {
            string group = "vg" + (++groupCounter);
            foreach (var row in new[] { new[] { "F", "여자" }, new[] { "M", "남자" } })
            {
                var line = new WrapPanel();
                var name = Theme.Text(row[1], 13, FontWeights.Bold, Theme.Ink2);
                name.Width = 38;
                name.VerticalAlignment = VerticalAlignment.Center;
                name.Margin = new Thickness(0, 0, 2, 6);
                line.Children.Add(name);
                for (int n = 1; n <= 5; n++)
                {
                    string id = NaturalVoices.Prefix + row[0] + n;
                    var r = new RadioButton
                    {
                        Style = Theme.GetStyle("Chip"),
                        Tag = H,
                        GroupName = group,
                        Content = n.ToString(),
                        MinHeight = 36,
                        MinWidth = 40,
                        FontSize = 13.5,
                        Margin = new Thickness(0, 0, 6, 6),
                        IsChecked = id == ttsNatVoice
                    };
                    AutomationProperties.SetName(r, NaturalVoices.NameOf(id));
                    r.Checked += delegate
                    {
                        if (!building) ttsNatVoice = id;
                    };
                    line.Children.Add(r);
                }
                panel.Children.Add(line);
            }
        }

        /// 처음 받기 전에 묻는 글: 받는 까닭(개인정보)·받는 곳·만든 곳·크기·메모리·이어 받기·사용 조건(요약)
        internal static string NaturalDownloadBody()
        {
            return "목소리 파일을 받는 까닭은 개인정보 때문이에요. 인터넷으로 목소리를 만드는 서비스는 자막 글을 그 회사 서버로 보내야 해요. "
                + "바꿈은 목소리 파일을 이 PC에 받아 두고 PC 안에서만 만들어서, 학생 이름 같은 자막 글이 밖으로 나가지 않아요. "
                + "처음 한 번만 받고, 받은 뒤에는 인터넷 없이 써요.\n\n"
                + "• 받는 곳: Hugging Face의 " + NaturalVoices.Repo + "\n"
                + "• 만든 곳: Supertone (Supertonic 3, " + NaturalVoices.License + " 라이선스)\n"
                + "• 크기: 약 " + WhisperModels.Words(NaturalVoices.DownloadBytes) + " · 쓸 때 메모리 약 " + WhisperModels.Words(NaturalVoices.Memory) + "\n"
                + "• 받다가 [멈추기]를 누르면 다음에 이어서 받아요. 받은 목소리는 자막 칸에서 지울 수 있어요.\n\n"
                + "사용 조건(줄임): 이 목소리로 만든 영상·소리를 나눌 때는 AI 목소리라고 밝혀야 해요. 바꿈이 화면에 'AI 목소리' 표시를 넣어 드려요. "
                + "다른 사람인 것처럼 꾸미거나, 속이거나 남을 해치는 데 쓰면 안 돼요. 조건 전문은 받은 폴더의 LICENSE 파일에 있어요.";
        }

        void DownloadNaturalVoice(Action then)
        {
            if (busy)
            {
                SetStatus("하던 일(" + busyWhat + ")이 끝난 뒤에 다시 눌러 주세요.");
                return;
            }
            if (Dialog.Ask(this, H, "wave", "자연스러운 목소리를 받을까요?", NaturalDownloadBody(), null, "그만두기", "동의하고 받기") != 1) return;
            StartNaturalDownload(then);
        }

        /// 받기(이어 받기). 다 받으면 then(받으려고 누른 일을 이어서 한다).
        void StartNaturalDownload(Action then)
        {
            RunBusy("자연스러운 목소리 받기", delegate (Action<string, double> report)
            {
                try
                {
                    NaturalVoices.Prepare(report, () => busyCancel);
                }
                catch (UserFacingException ex)
                {
                    // 학교 인터넷에서 막혔을 때 할 일(허용할 주소)은 상태 줄에 다 안 보여서 창으로도 알린다. [멈추기]를 눌렀으면 띄우지 않는다.
                    if (busyCancel) throw;
                    string msg = ex.Message;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (windowOpen) Dialog.Ask(this, H, "wave", "자연스러운 목소리를 받지 못했어요", msg, null, "확인");
                    }));
                    throw;
                }
            }, delegate
            {
                ttsNatural = true;
                SetStatus("자연스러운 목소리를 받았어요. 목소리를 고르고 [목소리 들어 보기]로 들어 보세요.");
                RefreshPanel();
                if (then != null) then();
            });
        }

        /// 자연스러운 목소리가 필요한데(needed) 아직 받지 않았으면 받을지 묻는다. 지금 이어서 해도 되면 true.
        /// [윈도우 목소리로]를 고르면 윈도우 목소리로 바꾸고 toWindows = true(부른 쪽이 고른 목소리를 바꾼다).
        /// 받기를 고르면 받은 뒤 again을 다시 부르고 false. 메모리가 모자라 보이면 한 번 더 묻는다.
        bool NaturalReady(bool needed, Action again, out bool toWindows)
        {
            toWindows = false;
            if (!needed) return true;
            if (!NaturalVoices.Ready())
            {
                if (busy)
                {
                    SetStatus("하던 일(" + busyWhat + ")이 끝난 뒤에 다시 눌러 주세요.");
                    return false;
                }
                // 단추 셋이 창 너비(500)에 들어가게 짧게(길면 왼쪽 단추가 잘린다)
                int a = Dialog.Ask(this, H, "wave", "자연스러운 목소리를 받을까요?", NaturalDownloadBody(), null, "그만두기", "윈도우 목소리로", "동의하고 받기");
                if (a == 1)
                {
                    ttsNatural = false;
                    toWindows = true;
                    RefreshPanel();
                    return true;
                }
                if (a == 2) StartNaturalDownload(again);
                return false;
            }
            if (NaturalVoiceEngine.Loaded) return true;
            long total, avail;
            WhisperModels.MemoryInfo(out total, out avail);
            if (avail > 0 && avail < NaturalVoices.Memory + 400L * 1024 * 1024)
            {
                int a = Dialog.Ask(this, H, "wave", "메모리가 모자랄 수 있어요", NaturalMemoryBody(avail), null, "그만두기", "윈도우 목소리로", "그래도 하기");
                if (a == 1)
                {
                    ttsNatural = false;
                    toWindows = true;
                    RefreshPanel();
                    return true;
                }
                if (a != 2) return false;
            }
            return true;
        }

        /// 메모리가 모자라 보일 때 묻는 글(avail: 지금 남은 메모리)
        internal static string NaturalMemoryBody(long avail)
        {
            return "자연스러운 목소리는 메모리를 " + WhisperModels.Words(NaturalVoices.Memory) + "쯤 써요. 지금 남은 메모리는 " + WhisperModels.Words(avail) + "예요. 다른 프로그램을 닫은 뒤에 하거나 윈도우 목소리로 읽으면 안전해요. "
                + "그래도 해 보다가 메모리가 모자라면 윈도우 목소리로 이어서 만들어요.";
        }

        void PreviewVoice()
        {
            bool toWindows;
            if (!NaturalReady(NaturalVoices.IsNatural(ttsVoice), PreviewVoice, out toWindows)) return;
            string voice = ttsVoice;
            double rate = ttsRate;
            string wav = null;
            RunBusy("목소리를 만드는 중", delegate (Action<string, double> report)
            {
                if (NaturalVoices.IsNatural(voice) && !NaturalVoiceEngine.Loaded) report("자연스러운 목소리를 여는 중", -1);
                wav = VTts.Speak(VoiceSample, voice, rate);
            }, delegate
            {
                try
                {
                    new System.Media.SoundPlayer(wav).Play();
                    SetStatus((NaturalVoices.IsNatural(voice) ? "'" + NaturalVoices.NameOf(voice) + "' 목소리예요." : "윈도우 목소리예요.") + " 마음에 들면 [이 자막을 음성으로]나 [모든 자막을 음성으로]를 눌러 주세요.");
                }
                catch (Exception ex)
                {
                    Log.Write("목소리 들어 보기 실패: " + ex.GetType().Name);
                    SetStatus("목소리를 들려주지 못했어요.");
                }
            });
        }

        void DeleteNaturalVoice()
        {
            if (busy)
            {
                SetStatus("하던 일(" + busyWhat + ")이 끝난 뒤에 다시 눌러 주세요.");
                return;
            }
            bool ready = NaturalVoices.Ready();
            long used = NaturalVoices.UsedBytes();
            string title = ready ? "받은 자연스러운 목소리를 지울까요?" : "받다 만 목소리 파일을 지울까요?";
            string body = "디스크에서 " + WhisperModels.Words(used) + "를 비워요. 이미 넣은 음성은 그대로 있어요. 다음에 자연스러운 목소리를 쓸 때 다시 받아요.";
            if (Dialog.Ask(this, H, "trash", title, body, null, "그만두기", "지우기") != 1) return;
            try
            {
                NaturalVoices.DeleteAll();
                SetStatus(ready ? "받은 자연스러운 목소리를 지웠어요." : "받다 만 목소리 파일을 지웠어요.");
            }
            catch (Exception ex)
            {
                Log.Write("자연스러운 목소리 지우기 실패: " + ex.GetType().Name);
                SetStatus("목소리 파일을 다 지우지 못했어요. 바꿈을 다시 켠 뒤 한 번 더 해 주세요.");
            }
            RefreshPanel();
        }

        /// .pptx에 읽을 발표자 노트가 있는지(자연스러운 목소리를 받을지 물을 때만 본다. 못 읽으면 없다고 본다)
        static bool HasNotes(string file)
        {
            try
            {
                return OoxmlRead.PptxNotes(file).Any(n => !string.IsNullOrWhiteSpace(n.Text));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
