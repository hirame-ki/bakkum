// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 도구 시험 중 영상 편집 창의 '줄 이름 옆 + 로 넣기'와 '아래 빈 곳 단추'(이슈 #69) 부분: /selftest-tools 시험폴더 video
// 실제 창을 화면 밖에 띄워 누른다. 파일 고르기 창·마이크·음성 만들기는 부르지 않고, 효과음 미리 듣기는 소리를 내지 않는다(VWaveOut.Silent).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Bakkum
{
    static partial class ToolTest
    {
        static string Box(Rect r)
        {
            return r.IsEmpty ? "없음" : r.X.ToString("0") + "," + r.Y.ToString("0") + " " + r.Width.ToString("0") + "×" + r.Height.ToString("0");
        }

        /// 편집 창을 화면 밖에 띄우고 시간 막대가 자리를 잡을 때까지 기다린다
        static VideoEditor OpenEditorOffscreen(string video, double width, double height)
        {
            var a = MenuAction.All.First(x => x.Id == "media");
            var w = VideoEditor.ForPreview(a, new List<string> { video }, "edit", false);
            // 작은 화면에서는 가장 작은 크기가 작업 영역에 맞춰 내려간다(VideoEditor 생성자). 그 크기를 흉내 낸다.
            w.MinWidth = Math.Min(w.MinWidth, width);
            w.MinHeight = Math.Min(w.MinHeight, height);
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -30000;
            w.Top = -30000;
            w.Width = width;
            w.Height = height;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            w.Show();
            var tl = w.TimelineForTest;
            Pump(delegate { return tl.ActualWidth > 0 && !tl.PlusRect("sound").IsEmpty; }, 20000);
            // 처음 자리 잡기(시간 막대 맞추기·재생 위치)를 마저 처리한다
            Pump(delegate { return false; }, 400);
            return w;
        }

        /// 화면 스레드의 할 일을 낮은 차례(ContextIdle: 칸을 다시 그린 뒤 스크롤 등)까지 처리한다.
        /// Pump는 Background 차례에서 멈춰 그보다 낮은 일을 남긴다. 한가해지지 않아도 0.5초면 빠져나온다.
        static void Settle()
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 150)
            {
                var frame = new DispatcherFrame();
                var guard = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(500) };
                guard.Tick += delegate
                {
                    guard.Stop();
                    frame.Continue = false;
                };
                guard.Start();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
                Dispatcher.PushFrame(frame);
                guard.Stop();
                Thread.Sleep(15);
            }
        }

        static void EditorPlus(string moving)
        {
            Cli.Print("[영상 편집 창: 줄 이름 옆 + ·아래 빈 곳 단추]");
            VideoEditor w = null;
            try
            {
                w = OpenEditorOffscreen(moving, 1280, 760);
                PlusButtons(w);
                PlusInserts(w);
                FootButtonsWork(w);
                w.Close();
                w = null;
                // 작은 화면(1366×768에 125%면 창이 약 1060×580): 고르기 창·아래 줄이 잘리거나 겹치지 않는다
                w = OpenEditorOffscreen(moving, 1060, 580);
                SmallEditor(w);
            }
            catch (Exception ex)
            {
                Check("영상 편집 창 시험이 끝까지 돈다", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (w != null) w.Close();
            }
        }

        static void PlusButtons(VideoEditor w)
        {
            var tl = w.TimelineForTest;
            var kinds = new[] { "sub", "deco", "sound" };
            var rects = kinds.Select(k => tl.PlusRect(k)).ToList();
            bool inHead = rects.All(r => !r.IsEmpty && r.Left >= 0 && r.Right <= VTimeline.HeadW && r.Top >= 0 && r.Bottom <= tl.ActualHeight + 0.5);
            bool apart = !rects[0].IntersectsWith(rects[1]) && !rects[1].IntersectsWith(rects[2]) && !rects[0].IntersectsWith(rects[2]);
            Check("+ 단추: 자막·글자·그림·소리 줄 이름 옆에 겹치지 않고(글자·그림이 없어도 그 줄이 있다)", inHead && apart && w.ProjectForTest.Items.Count == 0, string.Join(" / ", rects.Select(r => Box(r))));

            bool hits = true;
            foreach (var k in kinds)
            {
                var r = tl.PlusRect(k);
                if (tl.PlusAt(new Point(r.X + r.Width / 2, r.Y + r.Height / 2)) != k) hits = false;
                // 그린 것보다 사방 3씩 넓게 받는다
                if (tl.PlusAt(new Point(r.Right + 2, r.Y + r.Height / 2)) != k) hits = false;
            }
            var sr = rects[0];
            bool elsewhere = tl.PlusAt(new Point(VTimeline.HeadW + 30, sr.Y + sr.Height / 2)) == null && tl.PlusAt(new Point(8, sr.Y + sr.Height / 2)) == null;
            Check("+ 를 누른 자리는 그 줄의 + 로 알아듣고, 줄 이름·시간 막대는 + 가 아니다", hits && elsewhere && tl.Plus != null, null);

            // + 에 마우스: 넣을 자리를 옅게, 말풍선은 그 자리를 가리지 않는다
            w.SeekForTest(1.0);
            tl.ShowPlusHover("deco");
            var g = tl.PlusGhostRect();
            var pr = tl.PlusRect("deco");
            bool place = !g.IsEmpty && Near(g.Left, tl.X(1.0), 0.6) && Near(g.Right, tl.X(4.0) - 1, 0.6) && g.Top < pr.Top;
            Check("+ 에 마우스: 넣을 자리(재생 위치부터 3초, 첫 칸)를 옅게 보인다", place, Box(g) + " · 1초 자리 " + tl.X(1.0).ToString("0"));
            bool clear = true;
            string where = "";
            foreach (double t in new[] { 0.0, 0.1, 1.0, 4.9 })
            {
                w.SeekForTest(t);
                foreach (var k in kinds)
                {
                    tl.ShowPlusHover(k);
                    var gr = tl.PlusGhostRect();
                    var tip = tl.PlusTipRect();
                    var seen = gr.IsEmpty ? Rect.Empty : new Rect(Math.Max(VTimeline.HeadW, gr.Left), gr.Top, Math.Max(0, gr.Right - Math.Max(VTimeline.HeadW, gr.Left)), gr.Height);
                    bool ok = !tip.IsEmpty && tip.Left >= 0 && tip.Right <= tl.ActualWidth + 0.5 && (seen.IsEmpty || seen.Width <= 0 || !seen.IntersectsWith(tip));
                    if (!ok)
                    {
                        clear = false;
                        where += " " + k + "@" + t + " 자리 " + Box(gr) + " 말풍선 " + Box(tip);
                    }
                }
            }
            tl.ShowPlusHover(null);
            Check("+ 말풍선은 넣을 자리를 가리지 않고 시간 막대 안에", clear, where.Trim());
        }

        static void PlusInserts(VideoEditor w)
        {
            var tl = w.TimelineForTest;
            // ── 자막 + : 바로 넣고 고른다 ──
            w.SeekForTest(1.0);
            tl.Plus("sub");
            Settle();
            var sub = w.ProjectForTest.Items.FirstOrDefault(i => i.Subtitle);
            Check("자막 + : 재생 위치(1초)에 자막을 바로 넣고 고른다", sub != null && Near(sub.Start, 1.0, 0.01) && Near(sub.End, 3.5, 0.01) && w.SelectedId == sub.Id && w.TabId == "subtitle" && !w.PickerOpen,
                sub == null ? "안 들어감" : sub.Start.ToString("0.00") + "~" + sub.End.ToString("0.00") + " · 칸 " + w.TabId);
            var foot = w.FootButtons();
            Check("자막을 고르면 아래 빈 곳에 [여기서 시작][여기서 끝][다음 자막][음성으로]", foot.SequenceEqual(new[] { "여기서 시작", "여기서 끝", "다음 자막", "음성으로" }), string.Join(",", foot));
            w.UndoForTest();
            Settle();
            Check("자막 + 는 되돌리기로 빠지고 아래 단추도 사라진다", !w.ProjectForTest.Items.Any(i => i.Subtitle) && w.FootButtons().Count == 0, w.FootButtons().Count + "개");

            // ── 글자·그림 + : 고르기 창 ──
            w.SeekForTest(2.0);
            tl.Plus("deco");
            Settle();
            var card = w.PickerRectForTest();
            var plus = w.PlusInWindowForTest("deco");
            var shell = w.ShellSizeForTest;
            bool inside = !card.IsEmpty && card.Left >= 7.5 && card.Top >= 7.5 && card.Right <= shell.Width - 7.5 && card.Bottom <= shell.Height - 7.5;
            Check("글자·그림 + : 고르기 창이 창 안에, + 를 가리지 않고 뜬다", w.PickerOpen && w.PickerKind == "deco" && inside && !card.IntersectsWith(plus), "창 " + Box(card) + " · + " + Box(plus) + " · 바탕 " + shell.Width.ToString("0") + "×" + shell.Height.ToString("0"));
            w.KeyForTest(Key.T);
            w.KeyForTest(Key.Delete);
            Settle();
            Check("고르기 창이 떠 있는 동안 단축키(T·Delete)는 듣지 않는다", w.PickerOpen && w.ProjectForTest.Items.Count == 0, null);
            w.KeyForTest(Key.Escape);
            Settle();
            Check("Esc 로 고르기 창이 닫히고 아무것도 넣지 않는다", !w.PickerOpen && w.ProjectForTest.Items.Count == 0, null);
            tl.Plus("deco");
            Settle();
            w.ClickOutsidePickerForTest();
            Settle();
            Check("고르기 창 바깥을 누르면 닫힌다", !w.PickerOpen && w.ProjectForTest.Items.Count == 0, null);

            tl.Plus("deco");
            Settle();
            bool clicked = w.ClickPicker("별");
            Settle();
            Settle();
            var star = w.ProjectForTest.Items.FirstOrDefault(i => i.Kind == "sticker");
            Check("스티커를 고르면 재생 위치(2초)에 넣고 창을 닫고 고른다", clicked && star != null && star.Sticker == "star" && Near(star.Start, 2.0, 0.01) && star.End > star.Start && !w.PickerOpen && w.SelectedId == star.Id && w.TabId == "deco",
                star == null ? "안 들어감" : star.Sticker + " " + star.Start.ToString("0.00") + "~" + star.End.ToString("0.00"));
            Check("+ 로 넣으면 오른쪽 칸을 넣은 것의 설정까지 내려 보인다", w.PanelOffsetForTest > 50, w.PanelOffsetForTest.ToString("0"));
            foot = w.FootButtons();
            Check("글자·그림을 고르면 아래 빈 곳에 [여기서 시작][여기서 끝][맨 앞으로][맨 뒤로]", foot.SequenceEqual(new[] { "여기서 시작", "여기서 끝", "맨 앞으로", "맨 뒤로" }), string.Join(",", foot));

            // 넣을 자리 미리 보기는 실제로 들어갈 칸을 셈한다: 2초에는 첫 칸(별)이 차 있어 새 칸, 0.5초에는 첫 칸
            int lanes;
            w.SeekForTest(2.0);
            int at2 = tl.PlusLaneForTest("deco", out lanes);
            tl.ShowPlusHover("deco");
            var g2 = tl.PlusGhostRect();
            tl.ShowPlusHover(null);
            w.SeekForTest(0.5);
            int at05 = tl.PlusLaneForTest("deco", out lanes);
            var dp = tl.PlusRect("deco");
            var sp = tl.PlusRect("sound");
            Check("넣을 자리 미리 보기: 칸이 차 있으면 새 칸, 비어 있으면 그 칸(넣은 뒤와 같게)", lanes == 1 && at2 == 1 && at05 == 0, "2초 " + at2 + "번 칸 · 0.5초 " + at05 + "번 칸 · 칸 " + lanes);
            Check("새 칸이 생길 때도 넣을 자리는 그 줄 안에(아랫줄을 침범하지 않는다)", !g2.IsEmpty && g2.Top < dp.Top && g2.Bottom < sp.Top, Box(g2) + " · 소리 + " + Box(sp));

            w.SeekForTest(2.0);
            tl.Plus("deco");
            Settle();
            clicked = w.ClickPicker("화살표");
            Settle();
            Check("도형도 고르기 창에서 넣는다", clicked && w.ProjectForTest.Items.Count(i => i.Kind == "shape") == 1 && !w.PickerOpen, null);
            w.UndoForTest();
            Settle();
            Check("고르기 창으로 넣은 것도 되돌리기로 빠진다", w.ProjectForTest.Items.Count(i => i.Kind == "shape") == 0 && w.ProjectForTest.Items.Count(i => i.Kind == "sticker") == 1, null);

            // ── 소리 + : 효과음 ──
            w.SeekForTest(1.0);
            tl.Plus("sound");
            Settle();
            card = w.PickerRectForTest();
            plus = w.PlusInWindowForTest("sound");
            inside = !card.IsEmpty && card.Left >= 7.5 && card.Top >= 7.5 && card.Right <= shell.Width - 7.5 && card.Bottom <= shell.Height - 7.5;
            Check("소리 + : 고르기 창이 창 안에, + 를 가리지 않고 뜬다", w.PickerOpen && w.PickerKind == "sound" && inside && !card.IntersectsWith(plus), "창 " + Box(card) + " · + " + Box(plus));
            int sounds = w.ProjectForTest.Sounds.Count;
            bool heard = w.ClickPicker("'정답' 미리 듣기");
            Settle();
            Check("효과음 ▶ 미리 듣기는 넣지 않고 창도 그대로", heard && w.PickerOpen && w.ProjectForTest.Sounds.Count == sounds, null);
            clicked = w.ClickPicker("딩동");
            Settle();
            var ding = w.ProjectForTest.Sounds.LastOrDefault();
            Check("효과음을 고르면 재생 위치(1초)에 넣고 창을 닫고 고른다", clicked && w.ProjectForTest.Sounds.Count == sounds + 1 && ding != null && ding.Kind == "effect" && Near(ding.Start, 1.0, 0.01) && !w.PickerOpen && w.SelectedId == ding.Id && w.TabId == "sound",
                ding == null ? "안 들어감" : ding.Name + " " + ding.Start.ToString("0.00"));
            foot = w.FootButtons();
            Check("소리를 고르면 아래 빈 곳에 [여기서 시작][여기서 끝]", foot.SequenceEqual(new[] { "여기서 시작", "여기서 끝" }), string.Join(",", foot));
        }

        static void FootButtonsWork(VideoEditor w)
        {
            var tl = w.TimelineForTest;
            // ── 소리: 여기서 끝(재생 위치까지만 들리게) ──
            var ding = w.ProjectForTest.Sounds.Last();
            string id = ding.Id;
            double outBefore = ding.Out;
            w.SelectForTest(id);
            w.SeekForTest(0.5);
            w.ClickFoot("여기서 끝");
            Settle();
            var s = w.ProjectForTest.Sounds.First(x => x.Id == id);
            Check("소리 [여기서 끝]: 재생 위치가 소리 앞이면 바꾸지 않는다", Near(s.Out, outBefore, 1e-6), s.Out.ToString("0.000"));
            double keep = Math.Min(0.3, (s.Out - s.In) / 2);
            w.SeekForTest(1.0 + keep);
            w.ClickFoot("여기서 끝");
            Settle();
            s = w.ProjectForTest.Sounds.First(x => x.Id == id);
            Check("소리 [여기서 끝]: 재생 위치까지만 들리게 뒤를 자른다", Near(s.Out - s.In, keep, 0.01) && !s.Loop, (s.Out - s.In).ToString("0.000") + "초");
            w.UndoForTest();
            Settle();
            s = w.ProjectForTest.Sounds.First(x => x.Id == id);
            Check("소리 [여기서 끝]도 되돌리기로 돌아온다", Near(s.Out, outBefore, 1e-6), s.Out.ToString("0.000"));
            w.SelectForTest(id);
            w.SeekForTest(0.7);
            w.ClickFoot("여기서 시작");
            Settle();
            s = w.ProjectForTest.Sounds.First(x => x.Id == id);
            Check("소리 [여기서 시작]: 재생 위치에서 시작", Near(s.Start, 0.7, 0.01), s.Start.ToString("0.00"));

            // 소리 끝을 정할 수 없는 까닭
            var probe = new VSound { Start = 1, In = 0.5, Out = 2 };
            Check("소리 끝 까닭: 시작 앞·파일보다 길게는 안 되고, 그 사이는 된다",
                VideoEditor.SoundEndProblem(probe, 1.05, 3) != null && VideoEditor.SoundEndProblem(probe, 4, 3) != null && VideoEditor.SoundEndProblem(probe, 2.5, 3) == null, null);
            string loopWhy = VideoEditor.SoundEndProblem(new VSound { Start = 1, In = 0.5, Out = 2, Loop = true }, 4, 3) ?? "";
            Check("소리 끝 까닭: 되풀이하는 음악은 그 까닭을 따로 알린다", loopWhy.Contains("되풀이"), loopWhy);

            // ── 자막: 여기서 시작·끝, 다음 자막 ──
            w.SeekForTest(0.5);
            tl.Plus("sub");
            Settle();
            string a = w.SelectedId;
            w.SeekForTest(3.2);
            tl.Plus("sub");
            Settle();
            string b = w.SelectedId;
            w.SelectForTest(a);
            w.SeekForTest(0.8);
            w.ClickFoot("여기서 시작");
            Settle();
            w.SeekForTest(2.0);
            w.ClickFoot("여기서 끝");
            Settle();
            var sa = w.ProjectForTest.FindItem(a);
            Check("자막 [여기서 시작]·[여기서 끝]: 재생 위치로", sa != null && Near(sa.Start, 0.8, 0.01) && Near(sa.End, 2.0, 0.01), sa == null ? "없음" : sa.Start.ToString("0.00") + "~" + sa.End.ToString("0.00"));
            w.ClickFoot("다음 자막");
            Settle();
            Check("자막 [다음 자막]: 다음 자막을 고르고 그리로 간다", w.SelectedId == b && w.ProjectForTest.Items.Count(i => i.Subtitle) == 2, null);

            // ── 글자·그림: 맨 앞으로·맨 뒤로 ──
            w.SeekForTest(1.0);
            tl.Plus("deco");
            Settle();
            w.ClickPicker("하트");
            Settle();
            var star = w.ProjectForTest.Items.First(i => i.Kind == "sticker" && i.Sticker == "star");
            string starId = star.Id;
            w.SelectForTest(starId);
            w.ClickFoot("맨 앞으로");
            Settle();
            bool front = w.ProjectForTest.Items.Last().Id == starId;
            w.ClickFoot("맨 뒤로");
            Settle();
            bool back = w.ProjectForTest.Items.First().Id == starId;
            Check("글자·그림 [맨 앞으로]·[맨 뒤로]: 그리는 차례가 바뀐다", front && back, front + "/" + back);

            // 고른 것이 없으면 아래 단추도 없다
            w.SelectForTest(null);
            Settle();
            Check("고른 것이 없으면 아래 빈 곳 단추도 없다", w.FootButtons().Count == 0, string.Join(",", w.FootButtons()));

            // 긴 상태 글이 와도 아래 줄이 잘리지 않는다
            w.SelectForTest(a);
            Settle();
            w.StatusForTest(string.Concat(Enumerable.Repeat("상태 글이 아주 길게 이어져도 아래 단추를 밀어내지 않아요. ", 6)));
            Settle();
            string clip = w.FootClippedForTest();
            Check("긴 상태 글이 와도 아래 단추·글이 잘리지 않는다(넘치면 … 으로)", clip == null && w.FootButtons().Count == 4, clip);
        }

        static void SmallEditor(VideoEditor w)
        {
            var tl = w.TimelineForTest;
            var shell = w.ShellSizeForTest;
            var kinds = new[] { "sub", "deco", "sound" };
            var rects = kinds.Select(k => tl.PlusRect(k)).ToList();
            bool inHead = rects.All(r => !r.IsEmpty && r.Right <= VTimeline.HeadW && r.Top >= 0 && r.Bottom <= tl.ActualHeight + 0.5);
            bool apart = !rects[0].IntersectsWith(rects[1]) && !rects[1].IntersectsWith(rects[2]) && !rects[0].IntersectsWith(rects[2]);
            Check("작은 창(" + shell.Width.ToString("0") + "×" + shell.Height.ToString("0") + "): + 단추가 겹치지 않고 시간 막대 안에", inHead && apart, string.Join(" / ", rects.Select(r => Box(r))));
            foreach (var k in new[] { "deco", "sound" })
            {
                w.SeekForTest(1.0);
                tl.Plus(k);
                Settle();
                var card = w.PickerRectForTest();
                var plus = w.PlusInWindowForTest(k);
                bool inside = !card.IsEmpty && card.Left >= 7.5 && card.Top >= 7.5 && card.Right <= shell.Width - 7.5 && card.Bottom <= shell.Height - 7.5;
                Check("작은 창: " + (k == "deco" ? "글자·그림" : "소리") + " 고르기 창이 잘리지 않고 + 를 가리지 않는다", w.PickerOpen && inside && !card.IntersectsWith(plus), "창 " + Box(card) + " · + " + Box(plus));
                w.KeyForTest(Key.Escape);
                Settle();
            }
            w.SeekForTest(1.0);
            tl.Plus("sub");
            Settle();
            w.StatusForTest(string.Concat(Enumerable.Repeat("작은 창에서도 상태 글은 남은 자리까지만 보여요. ", 6)));
            Settle();
            string clip = w.FootClippedForTest();
            Check("작은 창: 아래 빈 곳 단추 넷이 남고(자리가 좁으면 그림만) 잘리지 않는다", clip == null && w.FootButtons().Count == 4, (clip ?? "") + (w.FootIconsOnly ? " 그림만" : " 글과 함께"));
        }
    }
}
