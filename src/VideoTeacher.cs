// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 창의 '선생님 도구': 학생 얼굴 가리기(찾기·따라가기), 조용한 곳 한 번에 자르기,
// 화면 녹화 다듬기(누른 곳 확대·마우스 강조·누른 자리 표시), 내 얼굴 작은 화면, 수업 자료(PDF·PPT·한글)로 영상 만들기,
// 제목 화면, 내 꾸밈, 유튜브 챕터.
// 오래 걸리는 일은 뒤에서 하고, 다 되면 되돌리기 한 칸으로 한 번에 바꾼다.
// 얼굴·목소리·화면은 이 PC 안에서만 다루고 어디로도 보내지 않는다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bakkum
{
    public sealed partial class VideoEditor
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ══ 오래 걸리는 일 ══

        string busyWhat;

        /// 뒤 스레드(MTA)에서 work를 하고, 끝나면 화면 스레드에서 done. [멈추기]를 누르면 busyCancel이 켜진다.
        void RunBusy(string what, Action<Action<string, double>> work, Action done)
        {
            RunBusy(what, ApartmentState.MTA, work, done);
        }

        void RunBusy(string what, ApartmentState apartment, Action<Action<string, double>> work, Action done)
        {
            if (busy)
            {
                SetStatus("하던 일(" + busyWhat + ")이 끝난 뒤에 다시 눌러 주세요.");
                return;
            }
            busy = true;
            busyCancel = false;
            busyWhat = what;
            SetStatus(what + "…");
            cancelBusyBtn.Visibility = Visibility.Visible;
            Action<string, double> report = delegate (string text, double frac)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (busy) SetStatus(text + (frac >= 0 ? " " + Math.Round(Math.Min(1, frac) * 100) + "%" : "") + "…");
                }));
            };
            var th = new Thread(delegate ()
            {
                Exception error = null;
                try
                {
                    work(report);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool cancelled = busyCancel || error is OperationCanceledException;
                    busy = false;
                    busyCancel = false;
                    cancelBusyBtn.Visibility = Visibility.Collapsed;
                    if (!windowOpen) return;
                    if (cancelled)
                    {
                        SetStatus("하던 일을 멈췄어요. 바뀐 것은 없어요.");
                        return;
                    }
                    if (error != null)
                    {
                        if (error is UserFacingException) SetStatus(error.Message);
                        else
                        {
                            Log.Write("영상 편집 '" + what + "' 실패: " + error.GetType().Name + " @ " + error.TargetSite);
                            SetStatus("하지 못했어요. (" + error.GetType().Name + ")");
                        }
                        return;
                    }
                    try
                    {
                        done();
                    }
                    catch (Exception ex)
                    {
                        Log.Write("영상 편집 '" + what + "' 마무리 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                        SetStatus("마무리하지 못했어요. (" + ex.GetType().Name + ")");
                    }
                }));
            }) { IsBackground = true, Name = "영상 편집 · " + what, Priority = ThreadPriority.BelowNormal };
            th.SetApartmentState(apartment);
            th.Start();
        }

        /// 0.6초마다(Poll에서): 흔들림 재기 진행을 보여 주고, 편집이 바뀌어 낡은 '조용한 곳' 표시는 지운다
        void PollTeacher()
        {
            if (timeline != null && timeline.Marks != null && silFor != version)
            {
                timeline.Marks = null;
                timeline.InvalidateVisual();
            }
            if (stabText != null && tab == "look")
            {
                var c = CurrentClip;
                if (c != null && c.Kind == "video" && c.Stabilize) stabText.Text = Theme.Keep(StabLine(c.Path));
            }
        }

        // ══ 선생님 도구 칸 ══

        double lectureSecs = 5;
        bool lectureNotes = true, lectureSubs = true;
        string styleName = "";

        void PanelTeacher()
        {
            Head("선생님 도구", "수업 영상을 빨리 다듬는 도구예요. 얼굴·목소리·화면은 이 PC 안에서만 다루고 어디로도 보내지 않아요.");

            // 학생 얼굴
            Label("학생 얼굴 가리기");
            Hint("영상에서 얼굴을 찾아 움직임을 따라 흐리게 가려요. 보여도 되는 얼굴(선생님 등)은 [가리기] 칸 목록에서 끄면 돼요.");
            Buttons(new[] { "이 조각에서", "모든 영상 조각에서" }, new Action[] { ScanFaces, ScanAllFaces });
            int faces = project.Clips.Sum(c => c.Redacts.Count(r => r.Face));
            int shown = project.Clips.Sum(c => c.Redacts.Count(r => r.Face && r.Off));
            if (faces > 0)
            {
                NoteBox("얼굴 " + faces + "개를 가리고 있어요" + (shown > 0 ? " (" + shown + "개는 보이게 둠)" : "") + ". 재생해 보며 놓친 얼굴이 없는지 꼭 확인해 주세요.");
                Act("[가리기] 칸에서 하나씩 보기", "Line", delegate
                {
                    SetTab("redact");
                    RefreshPanel();
                });
            }

            // 조용한 곳
            Label("조용한 곳 한 번에 자르기");
            Hint("말이 멈춘 곳을 찾아 한꺼번에 잘라 내요. 찾은 곳은 시간 막대에 빨갛게 보이고, 목록에서 하나씩 빼거나 들어 볼 수 있어요.");
            Chips(new[] { "0.4", "0.7", "1", "1.5" }, new[] { "0.4초 넘게", "0.7초 넘게", "1초 넘게", "1.5초 넘게" }, silMin.ToString(Inv), v =>
            {
                silMin = double.Parse(v, Inv);
                DropGaps();
            });
            Chips(new[] { "0.05", "0.15", "0.3" }, new[] { "말 앞뒤 바짝", "알맞게", "넉넉히" }, silKeep.ToString(Inv), v =>
            {
                silKeep = double.Parse(v, Inv);
                DropGaps();
            });
            Act("조용한 곳 찾기", "Primary", FindSilence);
            if (silGaps != null && silFor == version)
            {
                double sum = silGaps.Sum(g => g[1] - g[0]);
                NoteBox(silGaps.Count + "군데 · 모두 " + Sec(sum) + " · 자르면 길이 " + Clock(project.Duration) + " → " + Clock(Math.Max(0, project.Duration - sum)));
                for (int i = 0; i < silGaps.Count && i < 40; i++) GapRow(silGaps[i]);
                if (silGaps.Count > 40) Hint("그 밖에 " + (silGaps.Count - 40) + "군데가 더 있어요.");
                if (silGaps.Count > 0) Buttons(new[] { "모두 잘라 내기", "표시 지우기" }, new Action[] { CutSilence, DropGaps });
            }

            // 화면 녹화
            Label("화면 녹화 다듬기");
            var cc = CurrentClip;
            var cur = cc != null && cc.Kind == "video" ? VCursor.Of(cc.Path) : null;
            if (cur == null)
            {
                Hint("바꿈 [화면 녹화]로 녹화한 영상은 마우스 자리가 함께 기록돼서, 누른 곳으로 다가가고 마우스를 강조할 수 있어요. 지금 조각에는 마우스 기록이 없어요.");
            }
            else
            {
                Hint("이 조각에서 마우스를 누른 곳 " + cur.ClicksIn(Math.Min(cc.In, cc.Out), Math.Max(cc.In, cc.Out)) + "군데를 찾았어요.");
                Toggle("누른 곳 확대", "누를 때 그 자리로 다가갔다가 잠시 뒤 돌아와요", polishZoom, v =>
                {
                    polishZoom = v;
                    RefreshPanel();
                });
                if (polishZoom) Chips(new[] { "1.5", "1.8", "2.2" }, new[] { "조금 (1.5배)", "보통 (1.8배)", "크게 (2.2배)" }, polishLevel.ToString(Inv), v => polishLevel = double.Parse(v, Inv));
                Toggle("마우스 강조", "마우스 둘레에 옅은 원이 따라다녀요", polishSpot, v =>
                {
                    polishSpot = v;
                    RefreshPanel();
                });
                Toggle("누른 자리 표시", "누를 때마다 고리가 퍼져요", polishRipple, v =>
                {
                    polishRipple = v;
                    RefreshPanel();
                });
                if (polishSpot || polishRipple) Swatches(new uint[] { 0xFFD447, 0xFF8473, 0x38C7B8, 0x6CA8FF }, polishRgb, v => polishRgb = v);
                Buttons(new[] { "이대로 넣기", "넣은 것 지우기" }, new Action[] { ApplyPolish, ClearPolish });
                if (cc.Keys.Count > 0) Hint("[이대로 넣기]를 누르면 이 조각에 있던 확대 움직임은 새로 바뀌어요.");
            }

            // 내 얼굴
            Label("내 얼굴 작은 화면");
            Hint("웹캠으로 따로 찍은 영상을 오른쪽 아래에 동그랗게 얹어요. 화면 녹화와 함께 쓰면 강의 영상이 돼요.");
            Act("웹캠 영상 고르기", "Line", delegate
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "내 얼굴이 나온 영상 고르기", Filter = "영상|*.mp4;*.mov;*.m4v;*.avi;*.wmv;*.3gp|모든 파일|*.*" };
                if (dlg.ShowDialog(this) == true) AddPip(dlg.FileName, true);
            });

            // 수업 자료
            Label("수업 자료로 영상 만들기");
            Hint("PDF·PPT·한글 파일의 쪽을 그림으로 바꿔 재생 위치에 차례로 넣어요. PPT(.pptx)에 발표자 노트가 있으면 윈도우 목소리로 읽어 주고, 그 쪽은 읽는 길이만큼 보여 줘요.");
            Toggle("발표자 노트 읽어 주기", "노트가 없는 쪽은 아래에서 고른 초만큼 보여 줘요", lectureNotes, v =>
            {
                lectureNotes = v;
                RefreshPanel();
            });
            if (lectureNotes)
            {
                Toggle("노트를 자막으로도", "읽는 문장이 그때그때 자막으로 나와요", lectureSubs, v => lectureSubs = v);
                TtsControls();
            }
            Chips(new[] { "3", "5", "8", "12" }, new[] { "쪽마다 3초", "5초", "8초", "12초" }, lectureSecs.ToString(Inv), v => lectureSecs = double.Parse(v, Inv));
            Act("자료 파일 고르기", "Line", AddLectureDialog);

            // 제목 화면
            Label("제목 화면 · 이름 띠");
            Hint("영상 조각마다 앞에 제목 화면(2.5초)을 넣거나, 말하는 사람 이름 띠를 얹어요.");
            Buttons(new[] { "조각마다 제목 화면", "이름 띠 넣기" }, new Action[] { AddTitleCards, delegate { AddShape("bar"); } });

            // 내 꾸밈
            Label("내 꾸밈");
            var it = SelectedItem;
            if (it != null && it.Kind != "pip")
            {
                Hint("지금 고른 " + ItemTitle(it) + "의 모양을 저장해 두면 다른 영상에서도 한 번에 꺼내 쓸 수 있어요.");
                Field(styleName, "이름 (예: 우리 반 자막)", false, v => styleName = v);
                Act("내 꾸밈으로 저장", "Line", SaveStyle);
            }
            else Hint("글자·자막·스티커·도형을 고르면 그 모양을 저장해 두고 다른 영상에서도 꺼내 쓸 수 있어요.");
            foreach (var s in LoadStyles()) StyleRow(s);

            // 유튜브 챕터
            Label("유튜브 챕터");
            var marks = project.Markers.OrderBy(m => m.T).ToList();
            if (marks.Count == 0)
            {
                Hint("장면이 바뀌는 곳마다 표시(M)를 넣으면, 유튜브 설명에 붙여 넣을 챕터 글을 만들어 줘요.");
                Act("지금 자리에 표시 넣기", "Line", AddMarker);
            }
            else
            {
                var warns = new List<string>();
                string text = ChapterText(marks, project.Duration, warns);
                var box = new TextBox { Style = Theme.GetStyle("TextField"), Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, VerticalContentAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
                panel.Children.Add(box);
                foreach (var w in warns) Hint("· " + w);
                Buttons(new[] { "복사하기", "표시 넣기" }, new Action[] { delegate
                {
                    try
                    {
                        Clipboard.SetText(text);
                        SetStatus("챕터 글을 복사했어요. 유튜브 영상 설명에 붙여 넣으세요.");
                    }
                    catch (Exception)
                    {
                        SetStatus("복사하지 못했어요. 글을 직접 골라 복사해 주세요.");
                    }
                }, AddMarker });
                Hint("표시 이름이 챕터 이름이 돼요. 시간 막대 위 표시를 누르면 이름을 고칠 수 있어요.");
            }
        }

        // ══ 얼굴 가리기 ══

        sealed class FaceJob
        {
            public string ClipId, Path;
            public double In, Out;
            public int Rot;
            public readonly List<VRedact> Made = new List<VRedact>();
        }

        void ScanFaces()
        {
            var c = CurrentClip;
            if (c == null || c.Kind != "video")
            {
                SetStatus("얼굴 찾기는 영상 조각에서 쓸 수 있어요. 재생 위치를 영상 조각으로 옮겨 주세요.");
                return;
            }
            ScanFaces(new List<VClip> { c });
        }

        void ScanAllFaces()
        {
            ScanFaces(project.Clips.Where(x => x.Kind == "video").ToList());
        }

        void ScanFaces(List<VClip> clips)
        {
            var jobs = new List<FaceJob>();
            foreach (var c in clips)
            {
                var pr = VMedia.Probe(c.Path);
                if (pr == null || !pr.HasVideo) continue;
                jobs.Add(new FaceJob { ClipId = c.Id, Path = c.Path, In = Math.Min(c.In, c.Out), Out = Math.Max(c.In, c.Out), Rot = ((pr.Rotation + c.Rotate) % 360 + 360) % 360 });
            }
            if (jobs.Count == 0)
            {
                SetStatus("얼굴을 찾을 영상이 없어요.");
                return;
            }
            var proto = redactProto.Copy();
            double total = jobs.Sum(j => j.Out - j.In);
            RunBusy("얼굴을 찾는 중", delegate (Action<string, double> report)
            {
                var session = new FaceFinder.Session();
                double before = 0;
                foreach (var j in jobs)
                {
                    double at = before;
                    VFaces.Scan(j.Path, j.In, j.Out, j.Rot, session, proto, j.Made, delegate (double sec)
                    {
                        report("얼굴을 찾는 중", total > 0 ? (at + sec) / total : -1);
                    }, () => busyCancel);
                    before += j.Out - j.In;
                }
            }, delegate
            {
                int found = 0, kept = 0;
                Change(null, delegate
                {
                    foreach (var j in jobs)
                    {
                        var c = project.FindClip(j.ClipId);
                        if (c == null) continue;
                        // 다시 찾아도 '보이게 둠'으로 끈 얼굴은 그대로 둔다
                        var old = c.Redacts.Where(r => r.Face && r.Off).ToList();
                        foreach (var made in j.Made)
                        {
                            if (!old.Any(o => VFaces.Same(o, made))) continue;
                            made.Off = true;
                            kept++;
                        }
                        c.Redacts.RemoveAll(r => r.Face);
                        c.Redacts.AddRange(j.Made);
                        found += j.Made.Count;
                    }
                });
                selectedRedact = null;
                if (found == 0) SetStatus("얼굴을 찾지 못했어요. 옆모습·아주 작은 얼굴은 놓칠 수 있어요. 화면을 끌어 직접 가려 주세요.");
                else SetStatus("얼굴 " + found + "개를 찾아 가렸어요." + (kept > 0 ? " 전에 '보이게 둠'으로 끈 얼굴 " + kept + "개는 그대로 보이게 뒀어요." : "") + " 재생해 보며 놓친 얼굴이 없는지 꼭 확인해 주세요.");
                RefreshPanel();
                stage.InvalidateVisual();
            });
        }

        /// 고른 가리기 상자를 지금 장면부터 앞으로 따라가게 한다
        void TrackSelected()
        {
            var c = CurrentClip;
            var rd = c == null || selectedRedact == null ? null : c.Redacts.FirstOrDefault(x => x.Id == selectedRedact);
            if (rd == null || c.Kind != "video")
            {
                SetStatus("따라갈 가리기 상자를 먼저 골라 주세요.");
                return;
            }
            double local, s;
            if (!LocalIn(c, out local, out s) || s < rd.Start - 0.05 || s > rd.End + 0.05)
            {
                SetStatus("재생 위치를 이 상자가 보이는 장면으로 옮긴 뒤 눌러 주세요.");
                return;
            }
            double end = Math.Min(rd.End, Math.Max(c.In, c.Out));
            if (end - s < 0.1)
            {
                SetStatus("이 상자는 여기서 끝나요. 더 앞 장면에서 눌러 주세요.");
                return;
            }
            var box = VAnim.Redact(rd, s);
            string cid = c.Id, rid = rd.Id, path = c.Path;
            var keys = new List<VKey>();
            double lost = -1;
            RunBusy("움직임을 따라가는 중", delegate (Action<string, double> report)
            {
                lost = VTrack.Follow(path, s, end, box, keys, delegate (double t) { report("움직임을 따라가는 중", (t - s) / (end - s)); }, () => busyCancel);
            }, delegate
            {
                if (keys.Count < 2)
                {
                    SetStatus("상자 안 모양을 따라가지 못했어요. 움직이는 것에 딱 맞게 상자를 다시 그려 보세요.");
                    return;
                }
                Change(null, delegate
                {
                    var cc = project.FindClip(cid);
                    var r = cc == null ? null : cc.Redacts.FirstOrDefault(x => x.Id == rid);
                    if (r == null) return;
                    r.Keys.RemoveAll(k => k.T >= s - 1e-6);
                    r.Keys.AddRange(keys);
                    r.Keys.Sort((p, q) => p.T.CompareTo(q.T));
                });
                double last = keys[keys.Count - 1].T;
                if (lost >= 0) SetStatus("원본 " + Clock(last) + "까지 따라갔어요. 그 뒤로는 모양을 놓쳐서 마지막 자리에 뒀어요. 그 장면에서 상자를 옮기고 다시 [따라가기]를 눌러 주세요.");
                else SetStatus("원본 " + Clock(last) + "까지 따라갔어요. 재생해 보며 잘 가려지는지 확인해 주세요.");
                RefreshPanel();
                stage.InvalidateVisual();
            });
        }

        // ══ 조용한 곳 자르기 ══

        double silMin = 0.7, silKeep = 0.15;
        List<double[]> silGaps;
        int silFor = -1;

        sealed class SilenceJob
        {
            public string Path, Name;
            public double In, Out, Speed, Start;
        }

        void FindSilence()
        {
            var jobs = new List<SilenceJob>();
            int skipped = 0;
            foreach (var c in project.Clips)
            {
                if (c.Kind != "video") continue;
                if (c.Mute || c.Reverse)
                {
                    skipped++;
                    continue;
                }
                jobs.Add(new SilenceJob { Path = c.Path, Name = Path.GetFileName(c.Path), In = c.In, Out = c.Out, Speed = Math.Max(0.05, c.Speed), Start = project.StartOf(c) });
            }
            if (jobs.Count == 0)
            {
                SetStatus("소리가 있는 영상 조각이 없어요.");
                return;
            }
            int ver = version;
            double min = silMin, keep = silKeep;
            var found = new List<double[]>();
            var noSound = new List<string>();
            RunBusy("조용한 곳을 찾는 중", delegate (Action<string, double> report)
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    var j = jobs[i];
                    report("소리를 살피는 중 (" + (i + 1) + "/" + jobs.Count + ")", i / (double)jobs.Count);
                    var track = AudioCache.Wait(j.Path, () => busyCancel);
                    if (busyCancel) throw new OperationCanceledException();
                    if (!track.Ready)
                    {
                        if (!noSound.Contains(j.Name)) noSound.Add(j.Name);
                        continue;
                    }
                    foreach (var g in VSilence.Find(track, j.In, j.Out, 0, min, keep))
                    {
                        double a = j.Start + (g.Start - j.In) / j.Speed, b = j.Start + (g.End - j.In) / j.Speed;
                        if (b - a >= 0.1) found.Add(new[] { a, b });
                    }
                }
            }, delegate
            {
                if (version != ver)
                {
                    SetStatus("찾는 동안 편집이 바뀌었어요. 다시 찾아 주세요.");
                    return;
                }
                silGaps = found;
                silFor = version;
                timeline.Marks = found.Count > 0 ? found : null;
                timeline.InvalidateVisual();
                double sum = found.Sum(g => g[1] - g[0]);
                string note = (noSound.Count > 0 ? " 소리가 없는 영상: " + string.Join(", ", noSound) + "." : "") + (skipped > 0 ? " 소리를 끈 조각·거꾸로 재생하는 조각 " + skipped + "개는 빼고 찾았어요." : "");
                SetStatus((found.Count == 0 ? "잘라 낼 만큼 조용한 곳이 없어요." : "조용한 곳 " + found.Count + "군데(모두 " + Sec(sum) + ")를 찾아 시간 막대에 빨갛게 표시했어요.") + note);
                RefreshPanel();
            });
        }

        void DropGaps()
        {
            bool had = silGaps != null;
            silGaps = null;
            if (timeline.Marks != null)
            {
                timeline.Marks = null;
                timeline.InvalidateVisual();
            }
            if (had) RefreshPanel();
        }

        void GapRow(double[] g)
        {
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var drop = SmallButton("빼기", delegate
            {
                if (silGaps == null) return;
                silGaps.Remove(g);
                timeline.Marks = silGaps.Count > 0 ? silGaps : null;
                timeline.InvalidateVisual();
                RefreshPanel();
            });
            drop.Height = 28;
            drop.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(drop, Dock.Right);
            dock.Children.Add(drop);
            var row = new DockPanel();
            var at = Theme.Text(Clock(g[0]), 12.5, FontWeights.Bold, H.DeepBrush);
            at.FontFamily = Theme.MonoFont;
            at.Width = 50;
            DockPanel.SetDock(at, Dock.Left);
            row.Children.Add(at);
            row.Children.Add(Theme.Text(Sec(g[1] - g[0]) + " 조용함", 13.5, FontWeights.Normal, Theme.Ink));
            var b = new Border { Child = row, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.MouseEnter += delegate { b.Background = Theme.Frozen(Theme.Hex(0xF3EFE8)); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate
            {
                SeekTo(Math.Max(0, g[0] - 0.4));
                SetStatus("조용한 곳 바로 앞으로 왔어요. 스페이스를 누르면 들어 볼 수 있어요.");
            };
            dock.Children.Add(b);
            panel.Children.Add(dock);
        }

        void CutSilence()
        {
            if (silGaps == null || silFor != version)
            {
                SetStatus("편집이 바뀌었어요. 조용한 곳을 다시 찾아 주세요.");
                DropGaps();
                return;
            }
            var gaps = silGaps.OrderByDescending(g => g[0]).ToList();
            double before = project.Duration;
            Change(null, delegate
            {
                var f = project.Follow();
                foreach (var g in gaps) project.Cut(g[0], g[1]);
                f();
            });
            silGaps = null;
            timeline.Marks = null;
            timeline.InvalidateVisual();
            SetStatus("조용한 곳 " + gaps.Count + "군데를 잘라 냈어요. 길이 " + Clock(before) + " → " + Clock(project.Duration) + ". 마음에 안 들면 되돌리기(Ctrl+Z) 한 번이면 돌아와요.");
            RefreshPanel();
        }

        // ══ 화면 녹화 다듬기 ══

        bool polishZoom = true, polishSpot = true, polishRipple = true;
        double polishLevel = 1.8;
        uint polishRgb = 0xFFD447;

        void ApplyPolish()
        {
            var c = CurrentClip;
            var cur = c != null && c.Kind == "video" ? VCursor.Of(c.Path) : null;
            var pr = cur == null ? null : VMedia.Probe(c.Path);
            if (cur == null || pr == null || pr.Width <= 0)
            {
                SetStatus("이 조각에는 마우스 기록이 없어요.");
                return;
            }
            if (!polishZoom && !polishSpot && !polishRipple)
            {
                SetStatus("넣을 것을 하나 이상 켜 주세요.");
                return;
            }
            string cid = c.Id;
            bool zoom = polishZoom, spot = polishSpot, ripple = polishRipple;
            double level = polishLevel;
            uint rgb = polishRgb;
            int clicks = 0, items = 0;
            Change(null, delegate
            {
                var x = project.FindClip(cid);
                if (x == null) return;
                int W, Hh;
                project.CanvasSizeLong(1920, out W, out Hh);
                VPolish.Apply(project, x, cur, pr, zoom, level, spot, ripple, rgb, W, Hh, out clicks, out items);
            });
            var parts = new List<string>();
            if (zoom) parts.Add("누른 곳 " + clicks + "군데 확대");
            if (spot) parts.Add("마우스 강조");
            if (ripple) parts.Add("누른 자리 표시");
            SetStatus(string.Join(" · ", parts) + "를 넣었어요. 재생해 보고 마음에 안 들면 되돌리기(Ctrl+Z)를 누르세요.");
            RefreshPanel();
        }

        void ClearPolish()
        {
            var c = CurrentClip;
            if (c == null) return;
            string cid = c.Id;
            int n = 0;
            bool keys = c.Keys.Count > 0;
            Change(null, delegate
            {
                var x = project.FindClip(cid);
                if (x != null) n = VPolish.Clear(project, x, true);
            });
            SetStatus(n == 0 && !keys ? "지울 것이 없어요." : "넣은 확대 움직임과 마우스 표시를 지웠어요.");
            RefreshPanel();
        }

        // ══ 수업 자료로 영상 만들기 ══

        void AddLectureDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "영상으로 만들 수업 자료 고르기",
                Filter = "수업 자료|*.pdf;*.ppt;*.pptx;*.pps;*.ppsx;*.show;*.hwp;*.hwpx;*.doc;*.docx|모든 파일|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            AddLecture(dlg.FileName);
        }

        void AddLecture(string file)
        {
            var kind = Formats.KindOf(file);
            bool pdf = kind == Kind.Pdf;
            if (!pdf && kind != Kind.Ppt && kind != Kind.Hwp && kind != Kind.Word)
            {
                SetStatus("PDF·PPT·한글·워드 파일을 골라 주세요.");
                return;
            }
            string folder;
            try
            {
                folder = LectureFolder(file);
            }
            catch (Exception ex)
            {
                Log.Write("수업 자료 폴더 만들기 실패: " + ex.GetType().Name);
                SetStatus("자료를 읽지 못했어요.");
                return;
            }
            double secs = lectureSecs;
            double t = CurrentTime;
            var pages = new List<string>();
            int total = 0;
            bool wantNotes = lectureNotes && kind == Kind.Ppt, subs = lectureSubs;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            bool pptx = ext == ".pptx" || ext == ".ppsx" || ext == ".pptm" || ext == ".ppsm";
            string voice = ttsVoice;
            double rate = ttsRate;
            List<List<VLecture.Spoken>> spoken = null;
            string notesWhy = wantNotes && !pptx ? "발표자 노트는 .pptx 파일에서만 읽을 수 있어요(PPT에서 .pptx로 저장해 다시 넣어 보세요)" : null;
            int noted = 0;
            RunBusy("수업 자료를 그림으로 바꾸는 중", pdf ? ApartmentState.MTA : ApartmentState.STA, delegate (Action<string, double> report)
            {
                Func<string, int> render = delegate (string pdfPath)
                {
                    return LecturePages(pdfPath, folder, pages, report, () => busyCancel);
                };
                if (pdf) total = render(file);
                else
                {
                    report("자료를 PDF로 바꾸는 중", -1);
                    var job = new Job(action, new ActionOption("lecture", "수업 자료", ""), new List<string> { file });
                    try
                    {
                        total = Conversions.WithPdf(job, file, render);
                    }
                    finally
                    {
                        job.ReleaseResources();
                    }
                }
                if (!wantNotes || !pptx || pages.Count == 0) return;
                report("발표자 노트를 읽는 중", -1);
                List<OoxmlRead.SlideNote> notes = null;
                try
                {
                    notes = OoxmlRead.PptxNotes(file);
                }
                catch (Exception ex)
                {
                    Log.Write("발표자 노트 읽기 실패: " + ex.GetType().Name);
                    notesWhy = "발표자 노트를 읽지 못했어요";
                    return;
                }
                var mapped = VLecture.MapNotes(notes, total, pages.Count);
                if (mapped == null)
                {
                    notesWhy = "슬라이드 수(" + notes.Count + ")와 PDF 쪽 수(" + total + ")가 달라 노트를 쪽에 잇지 못했어요";
                    return;
                }
                var lists = mapped.Select(VLecture.Sentences).ToList();
                int all = lists.Sum(l => l.Count);
                if (all == 0)
                {
                    notesWhy = "이 자료에는 발표자 노트가 없어요";
                    return;
                }
                var voices = VTts.Voices();
                if (!voices.Any(v => v.Korean))
                {
                    notesWhy = "이 PC에 한국어 목소리가 없어 노트를 읽지 못했어요(윈도우 설정 › 시간 및 언어 › 음성에서 더할 수 있어요)";
                    return;
                }
                if (string.IsNullOrEmpty(voice) || !voices.Any(v => v.Id == voice)) voice = voices.First(v => v.Korean).Id;
                int made = 0;
                var result = new List<List<VLecture.Spoken>>();
                foreach (var l in lists)
                {
                    var parts = new List<VLecture.Spoken>();
                    foreach (var s in l)
                    {
                        if (busyCancel) throw new OperationCanceledException();
                        report("노트를 읽는 소리를 만드는 중 (" + (made + 1) + "/" + all + ")", made / (double)all);
                        string wav = VTts.Speak(s, VLecture.VoiceFor(s, voice, voices), rate);
                        parts.Add(new VLecture.Spoken { Text = s, Wav = wav, Len = SoundSeconds(wav) });
                        made++;
                    }
                    result.Add(parts);
                }
                spoken = result;
                noted = result.Count(x => x.Count > 0);
            }, delegate
            {
                if (pages.Count == 0)
                {
                    SetStatus("자료에 넣을 쪽이 없어요.");
                    return;
                }
                int read = 0;
                Change(null, delegate
                {
                    double local;
                    int i = project.ClipAt(t, out local);
                    int at = i < 0 ? project.Clips.Count : (local < project.Clips[i].Length / 2 ? i : i + 1);
                    read = VLecture.Insert(project, at, pages, spoken, secs, subs, project.Items.LastOrDefault(x => x.Subtitle));
                });
                string head = "'" + Path.GetFileName(file) + "'의 " + pages.Count + "쪽을 넣었어요." + (total > pages.Count ? " (앞 " + pages.Count + "쪽만 넣었어요)" : "");
                if (read > 0)
                    SetStatus(head + " 노트가 있는 " + noted + "쪽은 노트 " + read + "문장을 읽어 주고" + (subs ? " 자막으로도 보여 주며" : "") + ", 노트가 없는 쪽은 " + Sec(secs) + "씩 보여 줘요. 읽는 소리는 [소리] 칸에서 바꾸거나 지울 수 있어요.");
                else
                    SetStatus(head + " 쪽마다 " + Sec(secs) + "씩 보여 줘요." + (notesWhy != null ? " " + notesWhy + "." : "") + " [소리] 칸의 녹음으로 설명을 입혀 보세요.");
                fileText.Text = Theme.Keep(FilesLine());
                RefreshPanel();
            });
        }

        const int LectureMostPages = 300;

        /// PDF 쪽들을 그림으로(이미 만든 쪽은 다시 쓰기). 전체 쪽 수를 돌려준다.
        internal static int LecturePages(string pdfPath, string folder, List<string> pages, Action<string, double> report, Func<bool> stop)
        {
            using (var doc = new WinPdfDoc(pdfPath))
            {
                int n = Math.Min(doc.PageCount, LectureMostPages);
                for (int i = 0; i < n; i++)
                {
                    if (stop()) throw new OperationCanceledException();
                    report("쪽 그림을 만드는 중 (" + (i + 1) + "/" + n + ")", i / (double)n);
                    string png = Path.Combine(folder, (i + 1).ToString("000") + ".png");
                    if (!File.Exists(png))
                    {
                        string tmp = png + ".tmp";
                        double pw, ph;
                        doc.PageSizeDip(i, out pw, out ph);
                        File.WriteAllBytes(tmp, doc.RenderPngExact(i, LectureDpi(pw, ph), 2560));
                        File.Move(tmp, png);
                    }
                    pages.Add(png);
                }
                return doc.PageCount;
            }
        }

        /// 쪽 그림의 dpi. 보통은 200(A4 세로 1654×2339, 16:9 슬라이드는 긴 변 2560으로 줄어듦).
        /// 쪽이 작게 적힌 자료(한쇼가 옛 .ppt·.show를 PDF로 바꾸면 쪽이 42%로 적힌다)는 200으로 그리면
        /// 긴 변이 1120쯤이라 1080p 영상에서 흐려서, 긴 변이 1920 이상 되게 올린다(글자·도형은 벡터라 다시 그려도 또렷하다).
        internal static double LectureDpi(double widthDip, double heightDip)
        {
            double longInch = Math.Max(widthDip, heightDip) / 96.0;
            if (longInch <= 0.01) return 200;
            return Math.Max(200, Math.Min(1200, 1920 / longInch));
        }

        /// 자료마다 쪽 그림을 두는 곳(바꿈 자료 폴더 안). 같은 자료를 또 넣으면 다시 쓴다.
        internal static string LectureFolder(string file)
        {
            var fi = new FileInfo(file);
            string key = Path.GetFullPath(file).ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            string hash;
            using (var sha = System.Security.Cryptography.SHA1.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key)), 0, 10).Replace("-", "").ToLowerInvariant();
            string d = Path.Combine(VAutoSave.Folder, "lecture", hash);
            Directory.CreateDirectory(d);
            try { Directory.SetLastWriteTime(d, DateTime.Now); } catch { }
            return d;
        }

        // ══ 제목 화면 ══

        /// 영상 조각마다(나눈 조각은 처음 것만) 앞에 색 화면 + 제목 글자
        void AddTitleCards()
        {
            if (VPolish.TitleTargets(project).Count == 0)
            {
                SetStatus("제목 화면을 넣을 영상 조각이 없어요. (앞에 이미 색 화면이 있는 조각은 건너뛰어요)");
                return;
            }
            int made = 0;
            Change(null, delegate { made = VPolish.AddTitleCards(project); });
            SetStatus("제목 화면 " + made + "개를 넣었어요. 시간 막대에서 제목 글자를 눌러 고쳐 주세요.");
            RefreshPanel();
        }

        // ══ 내 꾸밈 ══

        static string StylesPath
        {
            get { return Path.Combine(VAutoSave.Folder, "styles.json"); }
        }

        static JavaScriptSerializer StyleJson()
        {
            return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 32 };
        }

        internal static List<VItem> LoadStyles()
        {
            try
            {
                if (!File.Exists(StylesPath)) return new List<VItem>();
                var list = StyleJson().Deserialize<List<VItem>>(File.ReadAllText(StylesPath, Encoding.UTF8)) ?? new List<VItem>();
                list.RemoveAll(s => s == null);
                foreach (var s in list) if (s.Keys == null) s.Keys = new List<VKey>();
                return list;
            }
            catch (Exception ex)
            {
                Log.Write("내 꾸밈 읽기 실패: " + ex.GetType().Name);
                return new List<VItem>();
            }
        }

        internal static void SaveStyles(List<VItem> list)
        {
            string path = StylesPath, tmp = path + ".tmp";
            File.WriteAllText(tmp, StyleJson().Serialize(list), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        void SaveStyle()
        {
            var it = SelectedItem;
            if (it == null || it.Kind == "pip")
            {
                SetStatus("저장할 글자·스티커·도형을 먼저 골라 주세요.");
                return;
            }
            var list = LoadStyles();
            var s = it.Copy();
            s.Id = VProject.NewId();
            s.Start = 0;
            s.End = it.Length;
            s.Name = string.IsNullOrWhiteSpace(styleName) ? ItemTitle(it) + " " + (list.Count + 1) : styleName.Trim();
            list.RemoveAll(x => x.Name == s.Name);
            list.Insert(0, s);
            if (list.Count > 40) list.RemoveRange(40, list.Count - 40);
            try
            {
                SaveStyles(list);
            }
            catch (Exception ex)
            {
                Log.Write("내 꾸밈 저장 실패: " + ex.GetType().Name);
                SetStatus("저장하지 못했어요.");
                return;
            }
            styleName = "";
            SetStatus("내 꾸밈에 저장했어요: " + s.Name + ". 다른 영상에서도 [선생님 도구] 칸에서 꺼내 쓸 수 있어요.");
            RefreshPanel();
        }

        void StyleRow(VItem s)
        {
            var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var del = SmallButton("지우기", delegate
            {
                var list = LoadStyles();
                list.RemoveAll(x => x.Name == s.Name);
                try
                {
                    SaveStyles(list);
                }
                catch (Exception ex)
                {
                    Log.Write("내 꾸밈 지우기 실패: " + ex.GetType().Name);
                }
                RefreshPanel();
            });
            del.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(del, Dock.Right);
            dock.Children.Add(del);
            if (s.Kind == "text")
            {
                var all = SmallButton("자막에 입히기", delegate { ApplyStyleToSubtitles(s); });
                all.Margin = new Thickness(4, 0, 0, 0);
                DockPanel.SetDock(all, Dock.Right);
                dock.Children.Add(all);
            }
            var add = SmallButton("넣기", delegate { InsertStyle(s); });
            add.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(add, Dock.Right);
            dock.Children.Add(add);
            var name = Theme.Text(string.IsNullOrEmpty(s.Name) ? ItemTitle(s) : s.Name, 13.5, FontWeights.Bold, Theme.Ink);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            name.VerticalAlignment = VerticalAlignment.Center;
            dock.Children.Add(name);
            panel.Children.Add(dock);
        }

        void InsertStyle(VItem s)
        {
            var it = s.Copy();
            it.Id = VProject.NewId();
            it.Name = null;
            SpanAt(it, Math.Max(1, s.Length));
            Change(null, delegate { project.Items.Add(it); });
            Select(it.Id, true);
            SetStatus("내 꾸밈 '" + s.Name + "'을 재생 위치에 넣었어요.");
        }

        void ApplyStyleToSubtitles(VItem s)
        {
            int n = project.Items.Count(x => x.Subtitle);
            if (n == 0)
            {
                SetStatus("입힐 자막이 없어요.");
                return;
            }
            Change(null, delegate
            {
                foreach (var sub in project.Items)
                {
                    if (!sub.Subtitle) continue;
                    sub.TakeStyleFrom(s);
                    sub.X = s.X;
                    sub.Y = s.Y;
                }
            });
            SetStatus("자막 " + n + "개에 '" + s.Name + "' 모양을 입혔어요.");
            RefreshPanel();
        }

        // ══ 유튜브 챕터 ══

        /// 표시들로 유튜브 설명에 붙일 챕터 글. 유튜브 규칙(0:00부터, 3개 이상, 10초 넘게)에 어긋나면 warns에 적는다.
        internal static string ChapterText(List<VMarker> marks, double duration, List<string> warns)
        {
            var sorted = marks.OrderBy(m => m.T).ToList();
            var list = new List<KeyValuePair<double, string>>();
            if (sorted.Count == 0 || sorted[0].T >= 1) list.Add(new KeyValuePair<double, string>(0, "시작"));
            for (int i = 0; i < sorted.Count; i++)
            {
                string label = string.IsNullOrWhiteSpace(sorted[i].Label) ? "구간 " + (i + 1) : sorted[i].Label.Replace("\r", " ").Replace("\n", " ").Trim();
                double t = list.Count == 0 ? 0 : sorted[i].T;
                list.Add(new KeyValuePair<double, string>(t, label));
            }
            var sb = new StringBuilder();
            foreach (var x in list) sb.Append(VProject.Clock(Math.Floor(x.Key), false)).Append(' ').Append(x.Value).Append("\r\n");
            if (list.Count < 3) warns.Add("유튜브는 챕터가 3개 이상일 때만 보여 줘요. 지금 " + list.Count + "개예요.");
            var tooShort = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                double next = i + 1 < list.Count ? list[i + 1].Key : duration;
                if (next - list[i].Key < 10) tooShort.Add(VProject.Clock(Math.Floor(list[i].Key), false));
            }
            if (tooShort.Count > 0) warns.Add("10초보다 짧은 챕터가 있어요(" + string.Join(", ", tooShort) + "). 유튜브는 챕터마다 10초 넘게 있어야 보여 줘요.");
            return sb.ToString().TrimEnd();
        }
    }

    /// 화면 녹화 때 남긴 마우스 기록(ScreenRecorder의 CursorLog가 쓴 파일)
    sealed class VCursor
    {
        public int W, H;
        public double[] T, X, Y;
        public bool[] Down;
        /// 누른 순간들: 시각, x, y(녹화 화면에 대한 비율)
        public readonly List<double[]> Clicks = new List<double[]>();

        static readonly Dictionary<string, KeyValuePair<string, VCursor>> Cache = new Dictionary<string, KeyValuePair<string, VCursor>>(StringComparer.OrdinalIgnoreCase);

        /// 이 영상의 마우스 기록. 없거나 읽지 못하면 null.
        public static VCursor Of(string video)
        {
            if (string.IsNullOrEmpty(video) || !File.Exists(video)) return null;
            try
            {
                var fi = new FileInfo(video);
                string stamp = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                lock (Cache)
                {
                    KeyValuePair<string, VCursor> hit;
                    if (Cache.TryGetValue(video, out hit) && hit.Key == stamp) return hit.Value;
                }
                string log = CursorLog.PathFor(video);
                var cur = File.Exists(log) ? Parse(File.ReadAllText(log, Encoding.UTF8)) : null;
                lock (Cache) Cache[video] = new KeyValuePair<string, VCursor>(stamp, cur);
                return cur;
            }
            catch (Exception ex)
            {
                Log.Write("마우스 기록 읽기 실패: " + ex.GetType().Name);
                return null;
            }
        }

        public static VCursor Parse(string text)
        {
            var lines = text.Split('\n');
            if (lines.Length < 2) return null;
            var head = lines[0].Trim().Split(' ');
            int w, h;
            if (head.Length < 4 || head[0] != "bakkum-cursor" || !int.TryParse(head[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out w) || !int.TryParse(head[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return null;
            var inv = CultureInfo.InvariantCulture;
            var ts = new List<double>();
            var xs = new List<double>();
            var ys = new List<double>();
            var ds = new List<bool>();
            for (int i = 1; i < lines.Length; i++)
            {
                var p = lines[i].Trim().Split(' ');
                if (p.Length < 4) continue;
                double t, x, y;
                if (!double.TryParse(p[0], NumberStyles.Float, inv, out t) || !double.TryParse(p[1], NumberStyles.Float, inv, out x) || !double.TryParse(p[2], NumberStyles.Float, inv, out y)) continue;
                if (ts.Count > 0 && t < ts[ts.Count - 1]) continue;
                ts.Add(t);
                xs.Add(x);
                ys.Add(y);
                ds.Add(p[3] == "1");
            }
            if (ts.Count == 0) return null;
            var c = new VCursor { W = w, H = h, T = ts.ToArray(), X = xs.ToArray(), Y = ys.ToArray(), Down = ds.ToArray() };
            for (int i = 0; i < c.T.Length; i++)
            {
                if (!c.Down[i] || (i > 0 && c.Down[i - 1])) continue;
                if (c.X[i] < 0 || c.X[i] > 1 || c.Y[i] < 0 || c.Y[i] > 1) continue;
                c.Clicks.Add(new[] { c.T[i], c.X[i], c.Y[i] });
            }
            return c;
        }

        /// 시각 t의 마우스 자리(그때까지의 마지막 기록). 기록 앞이면 false.
        public bool At(double t, out double x, out double y)
        {
            x = y = 0;
            int i = Array.BinarySearch(T, t);
            if (i < 0) i = ~i - 1;
            if (i < 0) return false;
            x = X[i];
            y = Y[i];
            return true;
        }

        public int ClicksIn(double a, double b)
        {
            return Clicks.Count(k => k[0] >= a && k[0] <= b);
        }
    }

    /// 화면 녹화 다듬기(누른 곳 확대·마우스 강조·누른 자리)와 제목 화면 넣기
    static class VPolish
    {
        /// 조각 x에 넣는다. 확대는 조각의 움직임 표시(원본 시간), 마우스 강조·누른 자리는 얹은 도형.
        public static void Apply(VProject p, VClip x, VCursor cur, MediaProbe pr, bool zoom, double level, bool spot, bool ripple, uint rgb, int W, int H, out int clicks, out int items)
        {
            double a = Math.Min(x.In, x.Out), b = Math.Max(x.In, x.Out);
            Clear(p, x, false);
            double start = p.StartOf(x), len = x.Length;
            var cl = cur.Clicks.Where(k => k[0] >= a && k[0] <= b).ToList();
            clicks = cl.Count;
            items = 0;
            if (zoom) x.Keys = ZoomKeys(x, cl, level, pr, W, H);
            if (spot)
            {
                var it = new VItem { Kind = "shape", Shape = "spot", Start = start, End = start + len, W = 0.05, H = 0.05, Rgb = rgb, In = "none", Out = "none" };
                it.Keys = SpotKeys(p, x, cur, pr, W, H, start);
                if (it.Keys.Count > 0)
                {
                    it.X = it.Keys[0].X;
                    it.Y = it.Keys[0].Y;
                    p.Items.Add(it);
                    items++;
                }
            }
            if (ripple)
            {
                foreach (var k in cl)
                {
                    var q = CanvasPoint(x, k[0], k[1], k[2], pr, W, H);
                    double tl = TimelineOf(p, x, k[0]);
                    double end = Math.Min(start + len, tl + 0.6);
                    if (end - tl < 0.1) continue;
                    p.Items.Add(new VItem { Kind = "shape", Shape = "ripple", Start = tl, End = end, X = q.X, Y = q.Y, W = 0.07, H = 0.07, Rgb = rgb, In = "none", Out = "none" });
                    items++;
                }
            }
        }

        /// 이 조각 위의 마우스 강조·누른 자리를 지운다(keys면 확대 움직임도)
        public static int Clear(VProject p, VClip x, bool keys)
        {
            double start = p.StartOf(x), end = start + x.Length;
            int n = p.Items.RemoveAll(i => i.Kind == "shape" && (i.Shape == "spot" || i.Shape == "ripple") && i.Start >= start - 0.02 && i.Start < end - 0.01);
            if (keys) x.Keys.Clear();
            return n;
        }

        static double TimelineOf(VProject p, VClip c, double source)
        {
            double local;
            if (!c.LocalOf(source, out local)) local = source < Math.Min(c.In, c.Out) ? 0 : c.Length;
            return p.StartOf(c) + local;
        }

        /// 원본 자리(0~1) → 그 순간 화면 자리(0~1)
        static Point CanvasPoint(VClip x, double s, double ux, double uy, MediaProbe pr, int W, int H)
        {
            double local;
            if (!x.LocalOf(s, out local)) local = s < Math.Min(x.In, x.Out) ? 0 : x.Length;
            Rect clip;
            bool covers;
            var m = VRenderer.PlaceOn(x, local, s, pr.Width, pr.Height, pr.Rotation, false, W, H, out clip, out covers);
            var q = m.Transform(new Point(ux * pr.Width, uy * pr.Height));
            return new Point(q.X / W, q.Y / H);
        }

        /// 원본 자리(0~1)가 화면 가운데에 오는 옮김. 그림이 화면을 덮던 쪽은 계속 덮게(빈 곳이 보이지 않게) 붙잡는다.
        static Point PanFor(VClip x, double s, double ux, double uy, double z, MediaProbe pr, int W, int H)
        {
            var tmp = x.Copy();
            tmp.Keys = new List<VKey>();
            tmp.Zoom = z;
            tmp.PanX = 0;
            tmp.PanY = 0;
            tmp.Stabilize = false;
            double local;
            if (!tmp.LocalOf(s, out local)) local = 0;
            Rect clip;
            bool covers;
            var m = VRenderer.PlaceOn(tmp, local, s, pr.Width, pr.Height, pr.Rotation, false, W, H, out clip, out covers);
            var q = m.Transform(new Point(ux * pr.Width, uy * pr.Height));
            double px = (W / 2.0 - q.X) / W, py = (H / 2.0 - q.Y) / H;
            if (!clip.IsEmpty)
            {
                var c1 = m.Transform(clip.TopLeft);
                var c2 = m.Transform(clip.TopRight);
                var c3 = m.Transform(clip.BottomLeft);
                var c4 = m.Transform(clip.BottomRight);
                double L = Math.Min(Math.Min(c1.X, c2.X), Math.Min(c3.X, c4.X)), R = Math.Max(Math.Max(c1.X, c2.X), Math.Max(c3.X, c4.X));
                double T = Math.Min(Math.Min(c1.Y, c2.Y), Math.Min(c3.Y, c4.Y)), B = Math.Max(Math.Max(c1.Y, c2.Y), Math.Max(c3.Y, c4.Y));
                px = Between(px, (W - R) / W, -L / W);
                py = Between(py, (H - B) / H, -T / H);
            }
            return new Point(px, py);
        }

        static double Between(double v, double p, double q)
        {
            return Math.Max(Math.Min(p, q), Math.Min(Math.Max(p, q), v));
        }

        /// 누르기 0.7초 전부터 다가가 누른 곳을 가운데에 두고, 마지막으로 누르고 1.4초 뒤 0.6초 동안 돌아온다.
        /// 3초 안에 이어 누르면 다가간 채로 옮겨 간다.
        internal static List<VKey> ZoomKeys(VClip x, List<double[]> clicks, double level, MediaProbe pr, int W, int H)
        {
            var keys = new List<VKey>();
            if (clicks.Count == 0) return keys;
            double a = Math.Min(x.In, x.Out), b = Math.Max(x.In, x.Out);
            double z = Math.Max(1.05, level) * Math.Max(0.1, x.Zoom);
            double bz = x.Zoom, bx = x.PanX, by = x.PanY;
            var groups = new List<List<double[]>>();
            foreach (var k in clicks.OrderBy(v => v[0]))
            {
                if (groups.Count == 0 || k[0] - groups[groups.Count - 1][groups[groups.Count - 1].Count - 1][0] > 3.0) groups.Add(new List<double[]>());
                groups[groups.Count - 1].Add(k);
            }
            Action<double, double, double, double> add = delegate (double t, double zoom, double panX, double panY)
            {
                t = Math.Max(a, Math.Min(b, t));
                if (keys.Count > 0 && t <= keys[keys.Count - 1].T + 0.02)
                {
                    // 같은 때면 나중 것으로
                    keys[keys.Count - 1] = new VKey { T = keys[keys.Count - 1].T, Zoom = zoom, PanX = panX, PanY = panY };
                    return;
                }
                keys.Add(new VKey { T = t, Zoom = zoom, PanX = panX, PanY = panY });
            };
            add(a, bz, bx, by);
            foreach (var g in groups)
            {
                var first = g[0];
                var prev = PanFor(x, first[0], first[1], first[2], z, pr, W, H);
                add(first[0] - 0.7, bz, bx, by);
                add(first[0] - 0.05, z, prev.X, prev.Y);
                double prevT = first[0];
                for (int i = 1; i < g.Count; i++)
                {
                    var k = g[i];
                    var pk = PanFor(x, k[0], k[1], k[2], z, pr, W, H);
                    if (Math.Abs(pk.X - prev.X) + Math.Abs(pk.Y - prev.Y) < 0.02) continue;
                    add(Math.Max(prevT, k[0] - 0.45), z, prev.X, prev.Y);
                    add(k[0], z, pk.X, pk.Y);
                    prev = pk;
                    prevT = k[0];
                }
                double hold = g[g.Count - 1][0] + 1.4;
                add(hold, z, prev.X, prev.Y);
                add(hold + 0.6, bz, bx, by);
            }
            add(b, bz, bx, by);
            return keys;
        }

        /// 마우스 강조가 마우스를 따라가는 움직임 표시(얹은 것의 시간). 멈춰 있던 마우스가 갑자기 움직이면
        /// 그 바로 앞에도 표시를 둬서 미끄러지지 않게 하고, 확대가 바뀌는 동안은 촘촘히 둔다.
        static List<VKey> SpotKeys(VProject p, VClip x, VCursor cur, MediaProbe pr, int W, int H, double itemStart)
        {
            double a = Math.Min(x.In, x.Out), b = Math.Max(x.In, x.Out);
            var times = new List<double> { a, b };
            for (int i = 0; i < cur.T.Length; i++)
            {
                double t = cur.T[i];
                if (t < a || t > b) continue;
                if (i > 0 && t - cur.T[i - 1] > 0.12) times.Add(Math.Max(a, t - 0.04));
                times.Add(t);
            }
            for (int i = 0; i + 1 < x.Keys.Count; i++)
            {
                var k0 = x.Keys[i];
                var k1 = x.Keys[i + 1];
                if (Math.Abs(k0.Zoom - k1.Zoom) < 1e-6 && Math.Abs(k0.PanX - k1.PanX) < 1e-6 && Math.Abs(k0.PanY - k1.PanY) < 1e-6) continue;
                for (double t = k0.T; t < k1.T; t += 0.08) times.Add(t);
                times.Add(k1.T);
            }
            times = times.Where(t => t >= a - 1e-6 && t <= b + 1e-6).OrderBy(t => t).ToList();
            var keys = new List<VKey>();
            double lastT = double.NegativeInfinity;
            foreach (double t in times)
            {
                if (t - lastT < 0.03) continue;
                lastT = t;
                double ux, uy;
                if (!cur.At(t, out ux, out uy))
                {
                    ux = cur.X[0];
                    uy = cur.Y[0];
                }
                bool inside = ux >= 0 && ux <= 1 && uy >= 0 && uy <= 1;
                var q = CanvasPoint(x, t, Math.Max(0, Math.Min(1, ux)), Math.Max(0, Math.Min(1, uy)), pr, W, H);
                keys.Add(new VKey { T = TimelineOf(p, x, t) - itemStart, X = q.X, Y = q.Y, Zoom = 1, Opacity = inside ? 1 : 0 });
            }
            keys.Sort((k1, k2) => k1.T.CompareTo(k2.T));
            // 멈춰 있는 동안의 가운데 표시는 빼도 같다
            var thin = new List<VKey>();
            for (int i = 0; i < keys.Count; i++)
            {
                bool still = i > 0 && i + 1 < keys.Count && SamePlace(keys[i - 1], keys[i]) && SamePlace(keys[i], keys[i + 1]);
                if (!still) thin.Add(keys[i]);
            }
            return thin;
        }

        static bool SamePlace(VKey a, VKey b)
        {
            return Math.Abs(a.X - b.X) < 0.0008 && Math.Abs(a.Y - b.Y) < 0.0008 && Math.Abs(a.Opacity - b.Opacity) < 0.01;
        }

        // ── 제목 화면 ──

        /// 제목 화면을 넣을 영상 조각의 자리(앞에 색 화면이 있거나, 나눈 조각의 뒤쪽이면 뺀다)
        internal static List<int> TitleTargets(VProject p)
        {
            var list = new List<int>();
            for (int i = 0; i < p.Clips.Count; i++)
            {
                var c = p.Clips[i];
                if (c.Kind != "video") continue;
                if (i > 0)
                {
                    var prev = p.Clips[i - 1];
                    if (prev.Kind == "color") continue;
                    if (c.Line != null && (prev.Line ?? prev.Id) == c.Line) continue;
                }
                list.Add(i);
            }
            return list;
        }

        internal static int AddTitleCards(VProject p)
        {
            var targets = TitleTargets(p);
            if (targets.Count == 0) return 0;
            var f = p.Follow();
            var made = new List<KeyValuePair<string, string>>();
            for (int k = targets.Count - 1; k >= 0; k--)
            {
                int i = targets[k];
                var card = new VClip { Kind = "color", Rgb = 0x22252F, Dur = 2.5 };
                made.Insert(0, new KeyValuePair<string, string>(card.Id, TitleFor(p.Clips[i].Path, k + 1)));
                p.Clips.Insert(i, card);
            }
            f();
            foreach (var m in made)
            {
                var card = p.FindClip(m.Key);
                if (card == null) continue;
                double s = p.StartOf(card);
                p.Items.Add(new VItem { Kind = "text", Text = m.Value, Size = 0.08, X = 0.5, Y = 0.47, Start = s, End = s + card.Length, Rgb = 0xFFFFFF, Outline = 0, Shadow = true, In = "pop", Out = "fade" });
            }
            return made.Count;
        }

        /// 파일 이름에 한글이 있으면 그것을 제목으로(학생이 붙인 이름일 때가 많다), 아니면 빈칸 안내
        static string TitleFor(string path, int n)
        {
            string name = string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim();
            bool hangul = name.Any(ch => ch >= 0xAC00 && ch <= 0xD7A3);
            return hangul && name.Length <= 30 ? name : n + ". 제목을 써 주세요";
        }
    }

    /// 영상에서 얼굴을 찾아 움직임을 따라 가리는 상자로 만든다
    static class VFaces
    {
        sealed class Track
        {
            public readonly List<double> T = new List<double>();
            public readonly List<Rect> R = new List<Rect>();
        }

        /// 원본 a~b에서 얼굴을 찾아 made에 가리기 상자로 더한다. rot은 세워 보는 각도(원본 돌림 + 사용자가 돌린 것).
        public static void Scan(string path, double a, double b, int rot, FaceFinder.Session session, VRedact proto, List<VRedact> made, Action<double> progress, Func<bool> stop)
        {
            double len = Math.Max(0, b - a);
            double step = StepFor(len);
            var frames = new List<KeyValuePair<double, List<Rect>>>();
            using (var reader = new VideoReader(path, 1280, 1280, true))
            {
                int w = reader.Width, h = reader.Height;
                for (double t = a; t <= b + 1e-6; t += step)
                {
                    if (stop()) throw new OperationCanceledException();
                    double ft;
                    var px = reader.At(Math.Max(a, Math.Min(t, b - 0.02)), out ft);
                    progress(t - a);
                    if (px == null) continue;
                    int gw, gh;
                    var gray = Upright(VPx.Gray(px, w, h), w, h, rot, out gw, out gh);
                    frames.Add(new KeyValuePair<double, List<Rect>>(t, session.Find(gray, gw, gh).Select(r => FromUpright(r, rot)).ToList()));
                }
            }
            made.AddRange(FromFrames(frames, a, b, step, proto));
        }

        /// 장면마다 찾은 얼굴(원본 0~1)을 이어서 가리기 상자로. 같은 얼굴은 상자 하나가 움직임을 따라간다.
        internal static List<VRedact> FromFrames(List<KeyValuePair<double, List<Rect>>> frames, double a, double b, double step, VRedact proto)
        {
            var tracks = new List<Track>();
            foreach (var f in frames.OrderBy(x => x.Key)) Link(tracks, f.Value, f.Key, step);
            return tracks.Select(tr => ToRedact(tr, a, b, step, proto)).ToList();
        }

        /// 얼마마다 볼지: 5분까지는 0.5초, 더 길면 늘린다(1.5초까지)
        public static double StepFor(double len)
        {
            return len <= 300 ? 0.5 : Math.Min(1.5, len / 600);
        }

        /// 원본(돌리기 전) 회색 그림을 세운 그림으로. 화면에 보이는 대로(시계 방향 rot도).
        public static byte[] Upright(byte[] g, int w, int h, int rot, out int ow, out int oh)
        {
            ow = w;
            oh = h;
            if (rot != 90 && rot != 180 && rot != 270) return g;
            var d = new byte[g.Length];
            if (rot == 180)
            {
                for (int v = 0; v < h; v++)
                    for (int u = 0; u < w; u++) d[(h - 1 - v) * w + (w - 1 - u)] = g[v * w + u];
                return d;
            }
            ow = h;
            oh = w;
            if (rot == 90)
            {
                for (int v = 0; v < h; v++)
                    for (int u = 0; u < w; u++) d[u * h + (h - 1 - v)] = g[v * w + u];
            }
            else
            {
                for (int v = 0; v < h; v++)
                    for (int u = 0; u < w; u++) d[(w - 1 - u) * h + v] = g[v * w + u];
            }
            return d;
        }

        /// 세운 그림의 자리(0~1) → 원본(돌리기 전) 자리(0~1)
        public static Rect FromUpright(Rect r, int rot)
        {
            return new Rect(Map(r.Left, r.Top, rot), Map(r.Right, r.Bottom, rot));
        }

        static Point Map(double dx, double dy, int rot)
        {
            switch (rot)
            {
                case 90: return new Point(dy, 1 - dx);
                case 180: return new Point(1 - dx, 1 - dy);
                case 270: return new Point(1 - dy, dx);
                default: return new Point(dx, dy);
            }
        }

        static double IoU(Rect a, Rect b)
        {
            var i = Rect.Intersect(a, b);
            if (i.IsEmpty) return 0;
            double inter = i.Width * i.Height;
            double union = a.Width * a.Height + b.Width * b.Height - inter;
            return union <= 0 ? 0 : inter / union;
        }

        /// 같은 얼굴일 만한 정도(0이면 아님): 많이 겹치면 높고, 겹치지 않아도 가깝고 크기가 비슷하면 낮게
        static double Match(Rect a, Rect b)
        {
            double iou = IoU(a, b);
            if (iou > 0.15) return 1 + iou;
            double ax = a.X + a.Width / 2, ay = a.Y + a.Height / 2, bx = b.X + b.Width / 2, by = b.Y + b.Height / 2;
            double d = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
            double size = Math.Max(Math.Max(a.Width, a.Height), Math.Max(b.Width, b.Height));
            double ratio = Math.Min(a.Width, b.Width) / Math.Max(1e-6, Math.Max(a.Width, b.Width));
            if (d < size * 0.9 && ratio > 0.5) return 1 - d / (size * 0.9);
            return 0;
        }

        /// 이번 장면에서 찾은 얼굴을 이어지는 길에 붙인다(2초까지 안 보여도 같은 길)
        static void Link(List<Track> tracks, List<Rect> rects, double t, double step)
        {
            double gap = Math.Max(2.0, step * 2.5);
            var open = tracks.Where(x => t - x.T[x.T.Count - 1] <= gap + 1e-6).ToList();
            var pairs = new List<Tuple<double, Track, int>>();
            for (int i = 0; i < rects.Count; i++)
                foreach (var tr in open)
                {
                    double s = Match(tr.R[tr.R.Count - 1], rects[i]);
                    if (s > 0) pairs.Add(Tuple.Create(s, tr, i));
                }
            pairs.Sort((p, q) => q.Item1.CompareTo(p.Item1));
            var usedTrack = new HashSet<Track>();
            var usedRect = new HashSet<int>();
            foreach (var pair in pairs)
            {
                if (usedTrack.Contains(pair.Item2) || usedRect.Contains(pair.Item3)) continue;
                usedTrack.Add(pair.Item2);
                usedRect.Add(pair.Item3);
                pair.Item2.T.Add(t);
                pair.Item2.R.Add(rects[pair.Item3]);
            }
            for (int i = 0; i < rects.Count; i++)
            {
                if (usedRect.Contains(i)) continue;
                var tr = new Track();
                tr.T.Add(t);
                tr.R.Add(rects[i]);
                tracks.Add(tr);
            }
        }

        /// 길 하나를 가리기 상자로: 흔들리지 않게 고르고 조금 키운다. 앞뒤로 한 번 볼 만큼 더 가린다.
        static VRedact ToRedact(Track tr, double clipIn, double clipOut, double step, VRedact proto)
        {
            int n = tr.T.Count;
            var rects = new List<Rect>();
            for (int i = 0; i < n; i++)
            {
                double cx = 0, cy = 0, w = 0, h = 0, k = 0;
                for (int d = -1; d <= 1; d++)
                {
                    int q = i + d;
                    if (q < 0 || q >= n || Math.Abs(tr.T[q] - tr.T[i]) > step * 1.5) continue;
                    double wt = d == 0 ? 2 : 1;
                    var r = tr.R[q];
                    cx += (r.X + r.Width / 2) * wt;
                    cy += (r.Y + r.Height / 2) * wt;
                    w += r.Width * wt;
                    h += r.Height * wt;
                    k += wt;
                }
                cx /= k;
                cy /= k;
                w = w / k * 1.12;
                h = h / k * 1.12;
                rects.Add(new Rect(cx - w / 2, cy - h / 2, w, h));
            }
            var rd = new VRedact
            {
                Face = true,
                Mode = proto.Mode == "fill" || proto.Mode == "mosaic" ? proto.Mode : "blur",
                Strength = Math.Max(0.6, proto.Strength),
                Oval = true,
                Rgb = proto.Rgb,
                Start = Math.Max(clipIn, tr.T[0] - step - 0.1),
                End = Math.Min(clipOut, tr.T[n - 1] + step + 0.1)
            };
            rd.X = rects[0].X;
            rd.Y = rects[0].Y;
            rd.W = rects[0].Width;
            rd.H = rects[0].Height;
            if (n > 1)
                for (int i = 0; i < n; i++) rd.Keys.Add(new VKey { T = tr.T[i], X = rects[i].X, Y = rects[i].Y, W = rects[i].Width, H = rects[i].Height });
            return rd;
        }

        /// 다시 찾았을 때 전의 상자와 같은 얼굴인지(시간이 겹치고 그때 자리가 겹치면)
        public static bool Same(VRedact a, VRedact b)
        {
            double s = Math.Max(a.Start, b.Start), e = Math.Min(a.End, b.End);
            if (e - s < 0.3) return false;
            double m = (s + e) / 2;
            return IoU(VAnim.Redact(a, m), VAnim.Redact(b, m)) > 0.25;
        }
    }

    /// 가리기 상자 안 모양을 다음 장면들에서 찾아 따라간다(밝기 모양 맞추기, 평균 밝기는 빼고 견준다)
    static class VTrack
    {
        /// 픽셀마다 밝기 차이 평균이 이보다 크면 놓친 것으로 본다
        const double LostDiff = 26;

        /// t0의 box(원본 0~1)를 t1까지 따라가 keys에 더한다. 끝까지 따라가면 -1, 놓치면 놓친 시각.
        public static double Follow(string path, double t0, double t1, Rect box, List<VKey> keys, Action<double> progress, Func<bool> stop)
        {
            using (var reader = new VideoReader(path, 640, 640, true))
            {
                int w = reader.Width, h = reader.Height;
                double bw = Math.Max(2, box.Width * w), bh = Math.Max(2, box.Height * h);
                double f = Math.Min(1, 40.0 / Math.Max(bw, bh));
                int sw = Math.Max(16, (int)Math.Round(w * f)), sh = Math.Max(16, (int)Math.Round(h * f));
                int tw = Math.Max(4, (int)Math.Round(box.Width * sw)), th = Math.Max(4, (int)Math.Round(box.Height * sh));
                if (tw >= sw - 2 || th >= sh - 2) return t0;
                double ft;
                var px = reader.At(t0, out ft);
                if (px == null) return t0;
                var g = Shrink(VPx.Gray(px, w, h), w, h, sw, sh);
                double cx = (box.X + box.Width / 2) * sw, cy = (box.Y + box.Height / 2) * sh;
                int x0 = Math.Max(0, Math.Min(sw - tw, (int)Math.Round(cx - tw / 2.0)));
                int y0 = Math.Max(0, Math.Min(sh - th, (int)Math.Round(cy - th / 2.0)));
                // 처음 상자 가운데와 맞추는 데 쓴 칸 사이의 어긋남(비율)
                double offX = box.X + box.Width / 2 - (x0 + tw / 2.0) / sw, offY = box.Y + box.Height / 2 - (y0 + th / 2.0) / sh;
                var tmpl = Patch(g, sw, x0, y0, tw, th);
                keys.Add(new VKey { T = t0, X = box.X, Y = box.Y, W = box.Width, H = box.Height });
                double step = Math.Max(1 / 30.0, reader.Step);
                int radius = Math.Max(6, (int)Math.Round(Math.Max(tw, th) * 0.5));
                double lastKey = t0, lastT = t0;
                int lx = x0, ly = y0;
                for (double t = t0 + step; t <= t1 + 1e-6; t += step)
                {
                    if (stop()) throw new OperationCanceledException();
                    px = reader.At(t, out ft);
                    if (px == null) break;
                    g = Shrink(VPx.Gray(px, w, h), w, h, sw, sh);
                    int bx, by;
                    double diff = Search(g, sw, sh, tmpl, tw, th, lx, ly, radius, out bx, out by);
                    if (diff > LostDiff)
                    {
                        if (lastT > lastKey + 1e-6) keys.Add(Key(lastT, lx, ly, sw, sh, tw, th, offX, offY, box));
                        return t;
                    }
                    lx = bx;
                    ly = by;
                    lastT = t;
                    Blend(tmpl, g, sw, bx, by, tw, th, 0.15);
                    if (t - lastKey >= 0.2 - 1e-6)
                    {
                        keys.Add(Key(t, bx, by, sw, sh, tw, th, offX, offY, box));
                        lastKey = t;
                    }
                    progress(t);
                }
                if (lastT > lastKey + 1e-6) keys.Add(Key(lastT, lx, ly, sw, sh, tw, th, offX, offY, box));
                return -1;
            }
        }

        static VKey Key(double t, int bx, int by, int sw, int sh, int tw, int th, double offX, double offY, Rect box)
        {
            double cx = (bx + tw / 2.0) / sw + offX, cy = (by + th / 2.0) / sh + offY;
            return new VKey { T = t, X = cx - box.Width / 2, Y = cy - box.Height / 2, W = box.Width, H = box.Height };
        }

        /// 넓이 평균으로 줄이기
        internal static byte[] Shrink(byte[] g, int w, int h, int sw, int sh)
        {
            if (sw == w && sh == h) return g;
            var d = new byte[sw * sh];
            for (int y = 0; y < sh; y++)
            {
                int ya = y * h / sh, yb = Math.Max(ya + 1, (y + 1) * h / sh);
                for (int x = 0; x < sw; x++)
                {
                    int xa = x * w / sw, xb = Math.Max(xa + 1, (x + 1) * w / sw);
                    int s = 0, n = 0;
                    for (int yy = ya; yy < yb; yy++)
                    {
                        int row = yy * w;
                        for (int xx = xa; xx < xb; xx++)
                        {
                            s += g[row + xx];
                            n++;
                        }
                    }
                    d[y * sw + x] = (byte)(s / n);
                }
            }
            return d;
        }

        /// 상자 안 밝기에서 평균을 뺀 모양
        static float[] Patch(byte[] g, int sw, int x0, int y0, int tw, int th)
        {
            var t = new float[tw * th];
            double sum = 0;
            for (int j = 0; j < th; j++)
                for (int i = 0; i < tw; i++)
                {
                    float v = g[(y0 + j) * sw + x0 + i];
                    t[j * tw + i] = v;
                    sum += v;
                }
            float mean = (float)(sum / t.Length);
            for (int k = 0; k < t.Length; k++) t[k] -= mean;
            return t;
        }

        static void Blend(float[] tmpl, byte[] g, int sw, int x0, int y0, int tw, int th, double k)
        {
            var now = Patch(g, sw, x0, y0, tw, th);
            float keep = (float)(1 - k), take = (float)k;
            for (int i = 0; i < tmpl.Length; i++) tmpl[i] = tmpl[i] * keep + now[i] * take;
        }

        /// 지난 자리 둘레 radius 안에서 모양이 가장 비슷한 자리. 픽셀마다 밝기 차이 평균을 돌려준다.
        internal static double Search(byte[] g, int sw, int sh, float[] tmpl, int tw, int th, int px, int py, int radius, out int bx, out int by)
        {
            int n = tw * th;
            bx = px;
            by = py;
            int xa = Math.Max(0, px - radius), xb = Math.Min(sw - tw, px + radius);
            int ya = Math.Max(0, py - radius), yb = Math.Min(sh - th, py + radius);
            if (xb < xa || yb < ya) return double.MaxValue;
            // 창 평균을 빨리 구하려고 쌓은 합
            var sum = new long[(sw + 1) * (sh + 1)];
            for (int y = 0; y < sh; y++)
            {
                long row = 0;
                for (int x = 0; x < sw; x++)
                {
                    row += g[y * sw + x];
                    sum[(y + 1) * (sw + 1) + x + 1] = sum[y * (sw + 1) + x + 1] + row;
                }
            }
            double best = double.MaxValue;
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                {
                    long s = sum[(y + th) * (sw + 1) + x + tw] - sum[y * (sw + 1) + x + tw] - sum[(y + th) * (sw + 1) + x] + sum[y * (sw + 1) + x];
                    float mean = (float)s / n;
                    double limit = best * n;
                    double cost = 0;
                    for (int j = 0; j < th && cost < limit; j++)
                    {
                        int row = (y + j) * sw + x, trow = j * tw;
                        for (int i = 0; i < tw; i++)
                        {
                            float d = g[row + i] - mean - tmpl[trow + i];
                            cost += d < 0 ? -d : d;
                        }
                    }
                    if (cost >= limit) continue;
                    double c = cost / n + 0.02 * Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                    if (c < best)
                    {
                        best = c;
                        bx = x;
                        by = y;
                    }
                }
            return best;
        }
    }

    /// 수업 자료로 영상 만들기: 발표자 노트를 문장으로 나누고, 쪽 그림·읽는 소리·자막을 시간 막대에 놓는다.
    static class VLecture
    {
        /// 쪽이 나온 뒤 읽기 시작까지, 문장 사이, 마지막 문장 뒤 쉬는 시간(초)
        internal const double Lead = 0.5, Gap = 0.3, Tail = 0.9;

        /// 읽은 문장 하나
        internal sealed class Spoken
        {
            public string Text, Wav;
            public double Len;
        }

        static readonly System.Text.RegularExpressions.Regex Bullet = new System.Text.RegularExpressions.Regex(
            @"^(?:[•·▪■□◦●○※▶▷☞✔✓►▸]+\s*|[-–—*>]\s+|\(?\d{1,2}[.)]\s+)");
        static readonly System.Text.RegularExpressions.Regex SentenceEnd = new System.Text.RegularExpressions.Regex(@"(?<=[.?!。？！…])\s+");

        /// 노트를 읽을 문장들로. 줄마다 글머리표·번호는 떼고, 문장 끝(. ? !) 뒤 띄어쓰기에서 나눈다(3.14처럼 붙은 점은 그대로).
        /// 아주 짧은 조각은 앞 문장에 붙이고, 너무 긴 문장은 가운데 가까운 쉼표에서 한 번 나눈다(자막이 두 줄을 넘지 않게).
        internal static List<string> Sentences(string notes)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(notes)) return list;
            foreach (var raw in notes.Replace("\r", "").Split('\n'))
            {
                string line = Bullet.Replace(raw.Trim(), "").Trim();
                if (line.Length == 0) continue;
                foreach (var piece in SentenceEnd.Split(line))
                {
                    string s = piece.Trim();
                    if (s.Length == 0) continue;
                    if (s.Length < 4 && list.Count > 0 && !EndsSentence(list[list.Count - 1])) list[list.Count - 1] += " " + s;
                    else if (s.Length > 60) list.AddRange(SplitLong(s));
                    else list.Add(s);
                }
            }
            return list;
        }

        static bool EndsSentence(string s)
        {
            return s.Length > 0 && ".?!。？！…".IndexOf(s[s.Length - 1]) >= 0;
        }

        static IEnumerable<string> SplitLong(string s)
        {
            int mid = s.Length / 2, best = -1;
            for (int i = 10; i < s.Length - 10; i++)
            {
                if ((s[i] == ',' || s[i] == '，' || s[i] == '、') && (best < 0 || Math.Abs(i - mid) < Math.Abs(best - mid))) best = i;
            }
            if (best < 0) return new[] { s };
            return new[] { s.Substring(0, best + 1).Trim(), s.Substring(best + 1).Trim() };
        }

        /// 노트를 PDF 쪽에 맞춘다. 숨긴 슬라이드를 뺀 수나 전체 수가 PDF 쪽 수와 같아야 한다(아니면 null: 쪽과 노트를 잇지 못함).
        internal static List<string> MapNotes(List<OoxmlRead.SlideNote> slides, int pdfPages, int used)
        {
            if (slides == null || pdfPages <= 0) return null;
            var visible = slides.Where(x => !x.Hidden).ToList();
            List<OoxmlRead.SlideNote> pick = visible.Count == pdfPages ? visible : slides.Count == pdfPages ? slides : null;
            if (pick == null) return null;
            return pick.Take(Math.Min(used, pick.Count)).Select(x => x.Text ?? "").ToList();
        }

        /// 이 글을 읽을 목소리: 한글이 없고 영어 글자가 대부분이면 영어 목소리(있을 때), 아니면 고른 목소리
        internal static string VoiceFor(string text, string chosen, List<VTts.Voice> voices)
        {
            int hangul = 0, latin = 0, letters = 0;
            foreach (char c in text)
            {
                if (c >= '가' && c <= '힣') hangul++;
                else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin++;
                if (char.IsLetter(c)) letters++;
            }
            if (hangul > 0 || letters == 0 || latin < letters * 0.8 || voices == null) return chosen;
            var en = voices.FirstOrDefault(v => (v.Lang ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase));
            return en != null ? en.Id : chosen;
        }

        /// 쪽 하나의 길이: 노트를 읽으면 읽는 길이에 맞추고, 없으면 정한 초
        internal static double PageLength(List<Spoken> parts, double fallback)
        {
            if (parts == null || parts.Count == 0) return fallback;
            return Lead + parts.Sum(p => p.Len) + Gap * (parts.Count - 1) + Tail;
        }

        /// 쪽 그림을 at 자리에 차례로 넣고(뒤의 얹은 것·목소리는 따라 밀린다), 읽는 소리와 자막을 그 쪽 위에 놓는다. 넣은 읽기 수.
        internal static int Insert(VProject p, int at, List<string> pages, List<List<Spoken>> spoken, double fallback, bool subs, VItem style)
        {
            var follow = p.Follow();
            int first = at;
            for (int i = 0; i < pages.Count; i++)
            {
                var parts = spoken != null && i < spoken.Count ? spoken[i] : null;
                p.Clips.Insert(at++, new VClip { Kind = "photo", Path = pages[i], Dur = PageLength(parts, fallback), Ken = "none", Fit = "fit" });
            }
            follow();
            if (spoken == null) return 0;
            int voices = 0;
            double start = p.StartOf(first);
            for (int i = 0; i < pages.Count; i++)
            {
                var parts = i < spoken.Count ? spoken[i] : null;
                double t = start + Lead;
                if (parts != null)
                {
                    foreach (var s in parts)
                    {
                        var sound = new VSound { Kind = "voice", Path = s.Wav, Start = t, In = 0, Out = s.Len, Text = s.Text, Name = "노트 읽기" };
                        if (subs)
                        {
                            var it = new VItem { Kind = "text", Subtitle = true, Text = s.Text, Start = t, End = t + s.Len + 0.2, X = 0.5, Y = 0.86 };
                            if (style != null)
                            {
                                it.TakeStyleFrom(style);
                                it.X = style.X;
                                it.Y = style.Y;
                            }
                            p.Items.Add(it);
                            sound.ItemId = it.Id;
                        }
                        p.Sounds.Add(sound);
                        voices++;
                        t += s.Len + Gap;
                    }
                }
                start += p.Clips[first + i].Length;
            }
            return voices;
        }
    }
}
