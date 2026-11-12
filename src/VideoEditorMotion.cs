// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집의 움직임 표시(키프레임): 재생 위치에 넣기·빼기, 앞뒤 표시로 가기, 모두 지우기, 오른쪽 칸의 쉬운 설명.
// 표시가 둘 이상이면 표시와 표시 사이를 저절로 부드럽게 잇는다(얹은 것은 자리·크기·기울기·투명도, 조각은 확대·자리).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Bakkum
{
    public sealed partial class VideoEditor
    {
        /// 재생 위치가 바뀔 때 오른쪽 칸의 슬라이더를 표시 사이 값으로 맞추는 일들. 칸을 다시 그릴 때 비운다.
        readonly List<Action> liveSliders = new List<Action>();
        DateTime lastLive;

        // ── 무엇의 표시를 다루나 ──

        /// 표시를 다룰 조각: 얹은 것·소리·표시를 고르지 않았을 때 고른 조각(화면 비율 칸에서는 재생 위치의 조각)
        VClip KeyClip
        {
            get
            {
                if (SelectedItem != null || SelectedSound != null || SelectedMarker != null) return null;
                var c = SelectedClip;
                if (c != null) return c;
                return tab == "ratio" ? CurrentClip : null;
            }
        }

        /// 재생 위치의 표시 상태: -1 지금은 다룰 수 없음(보이는 시간 밖), 0 표시 없음, 1 표시 있음
        int KeyStateNow()
        {
            double t = CurrentTime;
            var it = SelectedItem;
            if (it != null)
            {
                if (t < it.Start - 1e-6 || t >= it.End) return -1;
                return VKeys.Find(it.Keys, t - it.Start) != null ? 1 : 0;
            }
            var c = KeyClip;
            double local, source;
            if (c == null || !LocalIn(c, out local, out source)) return -1;
            return VKeys.Find(c.Keys, VKeys.ClipTime(c, local, source)) != null ? 1 : 0;
        }

        /// 고른 것의 표시가 시간 막대 위 어느 시각에 있나(작은 것부터, 보이는 시간 밖의 표시는 뺀다)
        List<double> KeyTimes()
        {
            var list = new List<double>();
            var it = SelectedItem;
            if (it != null)
            {
                foreach (var k in it.Keys)
                    if (k.T >= -1e-6 && k.T <= it.Length + 1e-6) list.Add(it.Start + k.T);
            }
            else
            {
                var c = KeyClip;
                if (c == null) return list;
                double start = project.StartOf(c);
                foreach (var k in c.Keys)
                {
                    double local;
                    if (c.Kind == "video")
                    {
                        if (!c.LocalOf(k.T, out local)) continue;
                    }
                    else
                    {
                        if (k.T < -1e-6 || k.T > c.Length + 1e-6) continue;
                        local = k.T;
                    }
                    list.Add(start + local);
                }
            }
            list.Sort();
            return list;
        }

        int KeyCount()
        {
            var it = SelectedItem;
            if (it != null) return it.Keys.Count;
            var c = KeyClip;
            return c == null ? 0 : c.Keys.Count;
        }

        // ── 단추가 하는 일 ──

        /// 재생 위치에 움직임 표시를 넣거나 뺀다(◆ 단추·K 키)
        void ToggleKey()
        {
            if (preview.Playing) TogglePlay();
            double t = CurrentTime;
            var it = SelectedItem;
            if (it != null)
            {
                if (t < it.Start - 1e-6 || t >= it.End)
                {
                    SetStatus("고른 것이 화면에 보이는 시간 안으로 재생 위치를 옮긴 뒤 눌러 주세요.");
                    return;
                }
                string id = it.Id;
                bool added = false;
                Change(null, delegate
                {
                    var x = project.FindItem(id);
                    added = VKeys.Toggle(x, t - x.Start);
                });
                var now = project.FindItem(id);
                SetStatus(KeyMessage(added, now == null ? 0 : now.Keys.Count, false));
                RefreshPanel();
                return;
            }
            var c = KeyClip;
            if (c == null)
            {
                SetStatus("움직임 표시를 넣을 것(자막·글자·그림·조각)을 먼저 골라 주세요.");
                return;
            }
            double local, source;
            if (!LocalIn(c, out local, out source))
            {
                SetStatus("재생 위치를 고른 조각 안으로 옮긴 뒤 눌러 주세요.");
                return;
            }
            string cid = c.Id;
            bool clipAdded = false;
            Change(null, delegate
            {
                var x = project.FindClip(cid);
                clipAdded = VKeys.ToggleClip(x, local, source);
            });
            var cc = project.FindClip(cid);
            int n = cc == null ? 0 : cc.Keys.Count;
            // 조각의 첫 표시면 크기·자리를 바꾸는 '화면 비율' 칸을 연다
            if (clipAdded && n == 1 && tab != "ratio") SetTab("ratio");
            SetStatus(KeyMessage(clipAdded, n, true));
            RefreshPanel();
        }

        static string KeyMessage(bool added, int count, bool clip)
        {
            string what = clip ? "크기·자리를" : "자리·크기·기울기·투명도를";
            if (added && count == 1) return "첫 움직임 표시(◆)를 넣었어요. 재생 위치를 옮긴 뒤 " + what + " 바꾸면 두 번째 표시가 생기고, 그 사이를 부드럽게 움직여요.";
            if (added) return "움직임 표시를 넣었어요(모두 " + count + "개). 재생하면 표시 사이를 부드럽게 이어요.";
            if (count == 0) return "마지막 표시를 빼서 움직임을 멈췄어요. 지금 모습 그대로 남아요.";
            return "이 시각의 움직임 표시를 뺐어요(남은 표시 " + count + "개).";
        }

        /// 앞(dir<0)·뒤(dir>0) 움직임 표시로 재생 위치를 옮긴다
        void JumpKey(int dir)
        {
            var times = KeyTimes();
            if (times.Count == 0)
            {
                SetStatus("아직 움직임 표시가 없어요. ◆를 눌러 넣어 보세요.");
                return;
            }
            double t = CurrentTime, best = double.NaN;
            foreach (double x in times)
            {
                if (dir < 0 && x < t - VKeys.Near / 2 && (double.IsNaN(best) || x > best)) best = x;
                if (dir > 0 && x > t + VKeys.Near / 2 && (double.IsNaN(best) || x < best)) best = x;
            }
            if (double.IsNaN(best))
            {
                SetStatus(dir < 0 ? "앞에는 움직임 표시가 없어요." : "뒤에는 움직임 표시가 없어요.");
                return;
            }
            if (preview.Playing) TogglePlay();
            SeekTo(best);
        }

        /// 움직임 표시를 모두 지우고 재생 위치의 모습으로 굳힌다
        void ClearKeys()
        {
            double t = CurrentTime;
            var it = SelectedItem;
            if (it != null)
            {
                if (it.Keys.Count == 0) return;
                string id = it.Id;
                Change(null, delegate
                {
                    var x = project.FindItem(id);
                    VKeys.Bake(x, VKeys.LocalOf(x, t));
                });
                SetStatus("움직임 표시를 모두 지웠어요. 재생 위치의 모습 그대로 멈춰 있어요.");
                RefreshPanel();
                return;
            }
            var c = KeyClip;
            if (c == null || c.Keys.Count == 0) return;
            string cid = c.Id;
            double local, source;
            if (!LocalIn(c, out local, out source))
            {
                local = 0;
                source = c.SourceAt(0);
            }
            Change(null, delegate
            {
                var x = project.FindClip(cid);
                VKeys.BakeClip(x, local, source);
            });
            SetStatus("확대·옮기기 움직임을 지웠어요. 재생 위치의 크기·자리 그대로 남아요.");
            RefreshPanel();
        }

        /// 다른 자막을 모두 고른 자막과 같은 자리·줄 너비로(글 모양은 그대로)
        void AllSubtitlesHere()
        {
            var it = SelectedItem;
            if (it == null || !it.Subtitle) return;
            string id = it.Id;
            int n = project.Items.Count(x => x.Subtitle && x.Id != id);
            if (n == 0)
            {
                SetStatus("맞출 다른 자막이 없어요.");
                return;
            }
            var st = VAnim.Item(it, Math.Max(it.Start, Math.Min(it.End - 0.01, CurrentTime)));
            double x0 = st.X, y0 = st.Y, w0 = it.W;
            bool hadKeys = project.Items.Any(x => x.Subtitle && x.Id != id && x.Keys.Count > 0);
            Change(null, delegate
            {
                foreach (var x in project.Items.Where(i => i.Subtitle && i.Id != id))
                {
                    x.X = x0;
                    x.Y = y0;
                    x.W = w0;
                    x.Keys.Clear();
                }
            });
            SetStatus("다른 자막 " + n + "개를 이 자막과 같은 자리·너비로 옮겼어요." + (hadKeys ? " 움직임 표시는 지웠어요." : "") + " Ctrl+Z로 되돌릴 수 있어요.");
        }

        // ── 오른쪽 칸: 움직임(키프레임) ──

        /// 글자·그림 칸의 투명도·기울기. 움직임 표시가 있으면 재생 위치의 표시를 바꾼다.
        void ItemLookSliders(VItem it)
        {
            string id = it.Id;
            double op, rot;
            ItemLookAt(it, out op, out rot);
            var ops = SliderRow("투명도", 0.1, 1, op, 0.05, Pct, v => SetItem("op", x =>
            {
                if (x.Keys.Count > 0) VKeys.Ensure(x, VKeys.LocalOf(x, CurrentTime)).Opacity = v;
                else x.Opacity = v;
            }));
            var rots = SliderRow("기울기", -180, 180, rot, 1, v => Math.Round(v) + "°", v => SetItem("rot", x =>
            {
                if (x.Keys.Count > 0)
                {
                    var k = VKeys.Ensure(x, VKeys.LocalOf(x, CurrentTime));
                    k.Rot = v;
                    k.HasRot = true;
                }
                else x.Rot = v;
            }));
            liveSliders.Add(delegate
            {
                var x = project.FindItem(id);
                if (x == null || x.Keys.Count == 0) return;
                double o2, r2;
                ItemLookAt(x, out o2, out r2);
                SetLive(ops, o2);
                SetLive(rots, r2);
            });
        }

        /// 재생 위치에서의 투명도(표시까지 곱한 값)·기울기
        void ItemLookAt(VItem it, out double op, out double rot)
        {
            op = it.Opacity;
            rot = it.Rot;
            var k = VAnim.At(it.Keys, VKeys.LocalOf(it, CurrentTime));
            if (k == null) return;
            op = Math.Min(1, it.Opacity * k.Opacity);
            if (k.HasRot) rot = k.Rot;
        }

        /// 글자·그림 칸의 '움직임' 설명과 단추
        void ItemMotionSection(VItem it)
        {
            Label("움직임 (키프레임)");
            MotionExplainer(false);
            MotionButtons(it.Keys.Count);
        }

        /// 화면 비율 칸의 '움직임' 설명과 단추(조각의 확대·자리)
        void ClipMotionSection(VClip c)
        {
            Label("움직임 (키프레임)");
            MotionExplainer(true);
            MotionButtons(c.Keys.Count);
        }

        /// 쉬운 설명: 무엇을 하는 기능인지, 어떻게 쓰는지
        void MotionExplainer(bool clip)
        {
            Hint(clip
                ? "화면이 시간에 따라 저절로 다가가거나(확대) 옆으로 옮겨 가게 해요. 예) 칠판 글씨 쪽으로 천천히 다가가기."
                : "얹은 것이 시간에 따라 저절로 움직이게 해요. 예) 화살표가 왼쪽에서 오른쪽으로 날아가기, 제목이 점점 커지기, 스티커가 빙글 돌기.");
            string steps = clip
                ? "① 움직임이 시작할 곳에 재생 위치를 두고 ◆를 눌러요.\n② 재생 위치를 옮긴 뒤 화면을 끌거나 휠을 굴려 크기·자리를 바꿔요. ◆가 하나 더 생겨요.\n③ 재생하면 두 ◆ 사이를 부드럽게 이어 가요."
                : "① 움직임이 시작할 곳에 재생 위치를 두고 ◆를 눌러요.\n② 재생 위치를 옮긴 뒤 화면에서 끌어 자리·크기·기울기를 바꾸거나 투명도를 바꿔요. ◆가 하나 더 생겨요.\n③ 재생하면 두 ◆ 사이를 부드럽게 이어 가요.";
            var n = Theme.Note(steps, H);
            n.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(n);
        }

        void MotionButtons(int count)
        {
            var g = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 8, 0, 0) };
            var prev = MotionButton("◀ 앞 ◆", "앞 움직임 표시로 재생 위치를 옮겨요", delegate { JumpKey(-1); });
            var key = MotionButton("◆ 넣기", "재생 위치에 움직임 표시를 넣거나 빼요 (K)", ToggleKey);
            var next = MotionButton("뒤 ◆ ▶", "뒤 움직임 표시로 재생 위치를 옮겨요", delegate { JumpKey(1); });
            prev.Margin = new Thickness(0, 0, 3, 0);
            key.Margin = new Thickness(3, 0, 3, 0);
            next.Margin = new Thickness(3, 0, 0, 0);
            g.Children.Add(prev);
            g.Children.Add(key);
            g.Children.Add(next);
            panel.Children.Add(g);
            Action look = delegate
            {
                int state = KeyStateNow();
                key.Content = state == 1 ? "◆ 빼기" : "◆ 넣기";
                // 흐리게만 하고 누를 수는 있게 둔다: 누르면 왜 안 되는지 아래 알림으로 알려 준다
                key.Opacity = state >= 0 ? 1 : 0.5;
                key.ToolTip = state < 0 ? "고른 것이 보이는 시간 안으로 재생 위치를 옮기면 쓸 수 있어요" : "재생 위치에 움직임 표시를 넣거나 빼요 (K)";
            };
            look();
            liveSliders.Add(look);
            if (count == 0)
            {
                Hint("◆ 단추는 재생 막대 가운데에도 있어요. 단축키는 K예요.");
                return;
            }
            Hint("움직임 표시 " + count + "개 · 시간 막대의 ◆가 표시예요. 표시가 있을 때 화면에서 바꾸면 그 시각의 표시에 담겨요.");
            Buttons(new[] { "움직임 모두 지우기" }, new Action[] { ClearKeys });
        }

        static Button MotionButton(string text, string tip, Action click)
        {
            var b = Theme.MakeButton(text, "Line");
            b.Height = 40;
            b.FontSize = 13.5;
            b.Padding = new Thickness(6, 0, 6, 0);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.ToolTip = tip;
            b.Click += delegate { click(); };
            return b;
        }

        /// 재생 위치가 바뀌었을 때 슬라이더 값을 맞춘다(끄는 중인 슬라이더는 그대로)
        void SetLive(System.Windows.Controls.Slider s, double v)
        {
            if (s == null || s.IsMouseCaptureWithin) return;
            v = Math.Max(s.Minimum, Math.Min(s.Maximum, v));
            if (Math.Abs(s.Value - v) < 1e-4) return;
            bool was = building;
            building = true;
            try
            {
                s.Value = v;
            }
            finally
            {
                building = was;
            }
        }

        /// UpdateTime에서 부른다: 고른 것의 표시 상태와 칸의 값을 재생 위치에 맞춘다(재생 중에는 0.15초에 한 번)
        void UpdateLive()
        {
            if (preview.Playing && (DateTime.Now - lastLive).TotalMilliseconds < 150) return;
            lastLive = DateTime.Now;
            UpdateBarKeys();
            foreach (var a in liveSliders.ToList())
            {
                try
                {
                    a();
                }
                catch (Exception ex)
                {
                    Log.Write("영상 편집 값 맞추기 실패: " + ex.GetType().Name);
                }
            }
        }
    }
}
