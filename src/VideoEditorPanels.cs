// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 창의 오른쪽 손질 칸: 편집 · 자막 · 글자·그림 · 화면 비율 · 소리 · 색·효과 · 가리기 · 내보내기.
// 칸은 고른 것이나 손질 칸이 바뀔 때만 다시 그린다(슬라이더를 끄는 동안에는 다시 그리지 않는다).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bakkum
{
    public sealed partial class VideoEditor
    {
        bool busy;
        volatile bool busyCancel;
        int groupCounter;
        string panelShownTab;
        TextBox focusBox;

        // ── 칸 다시 그리기 ──

        void RefreshPanel()
        {
            RefreshPanel(false);
        }

        void RefreshPanel(bool focus)
        {
            if (panel == null || project == null) return;
            double offset = panelScroll.VerticalOffset;
            bool sameTab = panelShownTab == tab;
            panelShownTab = tab;
            building = true;
            groupCounter = 0;
            focusBox = null;
            liveSliders.Clear();
            try
            {
                panel.Children.Clear();
                proxyHint = null;
                switch (tab)
                {
                    case "edit": PanelEdit(); break;
                    case "subtitle": PanelSubtitle(); break;
                    case "deco": PanelDeco(); break;
                    case "ratio": PanelRatio(); break;
                    case "sound": PanelSound(); break;
                    case "look": PanelLook(); break;
                    case "redact": PanelRedact(); break;
                    case "teacher": PanelTeacher(); break;
                    case "export": PanelExport(); break;
                }
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집 칸 그리기 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                panel.Children.Add(Theme.Note("이 칸을 그리지 못했어요. (" + ex.GetType().Name + ")", Theme.Coral));
            }
            finally
            {
                building = false;
            }
            RefreshBar();
            if (sameTab) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate { panelScroll.ScrollToVerticalOffset(offset); }));
            else panelScroll.ScrollToTop();
            if (focus && focusBox != null)
            {
                var box = focusBox;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
                {
                    box.Focus();
                    box.CaretIndex = box.Text.Length;
                }));
            }
        }

        // ── 칸 만들기 도우미 ──

        void Head(string title, string hint)
        {
            var t = Theme.Text(title, 17, FontWeights.Bold, Theme.Ink);
            t.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(t);
            if (hint != null) Hint(hint);
        }

        void Hint(string s)
        {
            var t = Theme.MetaLabel(s);
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, 2, 0, 2);
            panel.Children.Add(t);
        }

        void Label(string s)
        {
            var t = Theme.Text(s, 13, FontWeights.Bold, Theme.Ink2);
            t.Margin = new Thickness(0, 18, 0, 7);
            panel.Children.Add(t);
        }

        void Chips(string[] ids, string[] labels, string current, Action<string> pick)
        {
            var wrap = new WrapPanel();
            string group = "vg" + (++groupCounter);
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                var r = new RadioButton
                {
                    Style = Theme.GetStyle("Chip"),
                    Tag = H,
                    GroupName = group,
                    Content = labels[i],
                    MinHeight = 36,
                    FontSize = 13.5,
                    Margin = new Thickness(0, 0, 6, 6),
                    IsChecked = id == current
                };
                r.Checked += delegate
                {
                    if (!building) pick(id);
                };
                wrap.Children.Add(r);
            }
            panel.Children.Add(wrap);
        }

        Slider SliderRow(string label, double min, double max, double value, double step, Func<double, string> format, Action<double> set)
        {
            var dock = new DockPanel { Margin = new Thickness(0, 12, 0, 2) };
            value = Math.Max(min, Math.Min(max, value));
            var val = Theme.Text(format(value), 13, FontWeights.Bold, H.DeepBrush);
            val.TextWrapping = TextWrapping.NoWrap;
            DockPanel.SetDock(val, Dock.Right);
            dock.Children.Add(val);
            dock.Children.Add(Theme.Text(label, 13, FontWeights.Bold, Theme.Ink2));
            panel.Children.Add(dock);
            var s = new Slider { Minimum = min, Maximum = max, Value = value, Tag = H };
            if (step > 0)
            {
                s.TickFrequency = step;
                s.IsSnapToTickEnabled = true;
                s.SmallChange = step;
                s.LargeChange = step * 5;
            }
            s.ValueChanged += delegate
            {
                // 재생 위치를 옮겨 값만 맞출 때(building)도 숫자는 따라 바꾼다
                val.Text = format(s.Value);
                if (building) return;
                set(s.Value);
            };
            panel.Children.Add(s);
            return s;
        }

        void Swatches(uint[] colors, uint current, Action<uint> pick)
        {
            var wrap = new WrapPanel();
            string group = "vg" + (++groupCounter);
            foreach (uint c in colors)
            {
                uint color = c;
                var r = new RadioButton
                {
                    Style = Theme.GetStyle("Swatch"),
                    GroupName = group,
                    Background = Theme.Frozen(Theme.Hex(color)),
                    IsChecked = color == current
                };
                r.Checked += delegate
                {
                    if (!building) pick(color);
                };
                wrap.Children.Add(r);
            }
            panel.Children.Add(wrap);
        }

        Button Act(string text, string style, Action click)
        {
            var b = Theme.MakeButton(text, style);
            b.Height = 40;
            b.FontSize = 14;
            b.Margin = new Thickness(0, 8, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Click += delegate { click(); };
            panel.Children.Add(b);
            return b;
        }

        void Buttons(string[] labels, Action[] clicks)
        {
            var g = new UniformGrid { Columns = labels.Length, Margin = new Thickness(0, 8, 0, 0) };
            for (int i = 0; i < labels.Length; i++)
            {
                var click = clicks[i];
                var b = Theme.MakeButton(labels[i], "Line");
                b.Height = 40;
                b.FontSize = 13.5;
                b.Padding = new Thickness(6, 0, 6, 0);
                b.Margin = new Thickness(i == 0 ? 0 : 3, 0, i == labels.Length - 1 ? 0 : 3, 0);
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.Click += delegate { click(); };
                g.Children.Add(b);
            }
            panel.Children.Add(g);
        }

        static Button SmallButton(string text, Action click)
        {
            var b = Theme.MakeButton(text, "Line");
            b.Height = 32;
            b.FontSize = 12.5;
            b.Padding = new Thickness(12, 0, 12, 0);
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Click += delegate { click(); };
            return b;
        }

        CheckBox Toggle(string title, string sub, bool value, Action<bool> set)
        {
            var sw = Theme.Switch(title, sub, value, v =>
            {
                if (!building) set(v);
            });
            sw.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(sw);
            return sw;
        }

        TextBox Field(string text, string hint, bool multi, Action<string> changed)
        {
            var box = new TextBox { Style = Theme.GetStyle("TextField"), Text = text ?? "", Tag = hint, Margin = new Thickness(0, 4, 0, 0) };
            if (multi)
            {
                box.AcceptsReturn = true;
                box.TextWrapping = TextWrapping.Wrap;
                box.MinHeight = 70;
                box.VerticalContentAlignment = VerticalAlignment.Top;
            }
            else box.Height = 42;
            box.TextChanged += delegate
            {
                if (!building) changed(box.Text);
            };
            panel.Children.Add(box);
            return box;
        }

        void NoteBox(string s)
        {
            var n = Theme.Note(s, H);
            n.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(n);
        }

        void ListRow(string left, string text, bool on, Action click)
        {
            var dock = new DockPanel();
            var l = Theme.Text(left, 12.5, FontWeights.Bold, on ? H.DeepBrush : Theme.Ink3);
            l.FontFamily = Theme.MonoFont;
            l.Width = 50;
            DockPanel.SetDock(l, Dock.Left);
            dock.Children.Add(l);
            var t = Theme.Text(string.IsNullOrWhiteSpace(text) ? "(비어 있음)" : text.Replace("\r", "").Replace("\n", " "), 13.5, FontWeights.Normal, string.IsNullOrWhiteSpace(text) ? Theme.Ink3 : Theme.Ink);
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            dock.Children.Add(t);
            var idle = on ? H.TintBrush : Brushes.Transparent;
            var b = new Border { Child = dock, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 2), Background = idle, Cursor = Cursors.Hand };
            b.MouseEnter += delegate { if (!on) b.Background = Theme.Frozen(Theme.Hex(0xF3EFE8)); };
            b.MouseLeave += delegate { b.Background = idle; };
            b.MouseLeftButtonUp += delegate { click(); };
            panel.Children.Add(b);
        }

        static string Sec(double v)
        {
            return v.ToString("0.0") + "초";
        }

        static string Times(double v)
        {
            return v.ToString("0.0#") + "배";
        }

        static string Pct(double v)
        {
            return Math.Round(v * 100) + "%";
        }

        static string Signed(double v)
        {
            int n = (int)Math.Round(v * 100);
            return (n > 0 ? "+" : "") + n;
        }

        static string Clock(double t)
        {
            return VProject.Clock(t, false);
        }

        // ── 바꾸기 도우미 ──

        /// 고른 조각(없으면 재생 위치의 조각)을 바꾼다
        void SetClip(string key, Action<VClip> act)
        {
            var c = CurrentClip;
            if (c == null) return;
            string id = c.Id;
            Change(key == null ? null : key + id, delegate
            {
                var x = project.FindClip(id);
                if (x != null) act(x);
            });
        }

        /// 길이가 바뀌는 바꿈: 얹은 것·소리·표시가 제자리를 따라가게
        void SetClipTimed(string key, Action<VClip> act)
        {
            SetClip(key, delegate (VClip x)
            {
                var f = project.Follow();
                act(x);
                f();
            });
        }

        void SetItem(string key, Action<VItem> act)
        {
            var it = SelectedItem;
            if (it == null) return;
            string id = it.Id;
            Change(key == null ? null : key + id, delegate
            {
                var x = project.FindItem(id);
                if (x != null) act(x);
            });
        }

        void SetSound(string key, Action<VSound> act)
        {
            var s = SelectedSound;
            if (s == null) return;
            string id = s.Id;
            Change(key == null ? null : key + id, delegate
            {
                var x = project.Sounds.FirstOrDefault(v => v.Id == id);
                if (x != null) act(x);
            });
        }

        string ClipName(VClip c)
        {
            int n = project.Clips.IndexOf(c) + 1;
            string kind = c.Kind == "photo" ? "사진" : c.Kind == "color" ? "색 화면" : c.Kind == "freeze" ? "멈춘 화면" : "영상";
            string name = string.IsNullOrEmpty(c.Path) ? "" : " · " + Path.GetFileName(c.Path);
            return n + "번 조각 (" + kind + name + ")";
        }

        bool HasRange
        {
            get { return timeline.RangeA >= 0 && timeline.RangeB > timeline.RangeA + 0.05; }
        }

        // ══ 편집 ══

        void PanelEdit()
        {
            var mk = SelectedMarker;
            if (mk != null)
            {
                PanelMarker(mk);
                return;
            }
            Head("편집", "조각을 고르고 나누거나 지워요. 시간 막대에서 조각을 끌면 차례가, 끝을 끌면 길이가 바뀌어요.");
            Buttons(new[] { "여기서 나누기", "지우기" }, new Action[] { SplitHere, DeleteSelected });
            Buttons(new[] { "복제", "멈춘 화면 넣기" }, new Action[] { Duplicate, InsertFreeze });

            Label("구간");
            if (HasRange)
            {
                Hint(Clock(timeline.RangeA) + " ~ " + Clock(timeline.RangeB) + " (" + Sec(timeline.RangeB - timeline.RangeA) + ")을 골랐어요. 재생하면 이 구간을 되풀이해요.");
                Buttons(new[] { "구간 잘라 내기", "구간만 남기기" }, new Action[] { CutRange, KeepRange });
                Buttons(new[] { "구간 풀기" }, new Action[] { delegate
                {
                    timeline.RangeA = timeline.RangeB = -1;
                    timeline.InvalidateVisual();
                    RefreshPanel();
                } });
            }
            else Hint("Shift를 누른 채 시간 막대 위 눈금을 끌거나, I·O 키로 구간의 시작·끝을 고르세요. 고른 구간만 잘라 내거나 남길 수 있어요.");

            var c = CurrentClip;
            if (c != null)
            {
                Label(ClipName(c));
                if (c.Kind == "video")
                {
                    Hint("원본 " + Clock(c.In) + " ~ " + Clock(c.Out) + " · 지금 길이 " + Sec(c.Length));
                    Chips(new[] { "0.25", "0.5", "1", "1.5", "2" }, new[] { "0.25배", "0.5배", "보통", "1.5배", "2배" }, SpeedChip(c.Speed), v =>
                    {
                        double sp = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                        SetClipTimed(null, x => x.Speed = sp);
                        RefreshPanel();
                    });
                    SliderRow("빠르기", 0.1, 2.0, c.Speed, 0.1, Times, v => SetClipTimed("speed", x => x.Speed = Math.Round(v * 10) / 10));
                    Toggle("부드럽게 느리게", "느리게 할 때 앞뒤 장면을 섞어서 덜 끊겨 보여요", c.Smooth, v => SetClip(null, x => x.Smooth = v));
                    Toggle("거꾸로 재생", null, c.Reverse, v => SetClipTimed(null, x => x.Reverse = v));
                }
                else
                {
                    SliderRow("보여 줄 시간", 0.5, 30, c.Dur, 0.5, Sec, v => SetClipTimed("dur", x => x.Dur = v));
                    if (c.Kind == "photo")
                    {
                        Label("사진 움직임");
                        Chips(new[] { "none", "in", "out", "left", "right" }, new[] { "그대로", "다가가기", "멀어지기", "왼쪽으로", "오른쪽으로" }, c.Ken ?? "none", v => SetClip(null, x => x.Ken = v));
                    }
                    if (c.Kind == "color")
                    {
                        Label("색");
                        Swatches(Colors, c.Rgb, v => SetClip(null, x => x.Rgb = v));
                    }
                }
                Label("앞 조각에서 넘어가기");
                Chips(new[] { "none", "cross", "black", "white", "slide", "wipe", "zoom" }, new[] { "바로", "겹치기", "검게", "하얗게", "밀기", "닦기", "다가가기" }, c.Trans ?? "none", v =>
                {
                    SetClip(null, x => x.Trans = v);
                    RefreshPanel();
                });
                if ((c.Trans ?? "none") != "none")
                {
                    SliderRow("넘어가는 시간", 0.2, 2, c.TransSec, 0.1, Sec, v => SetClip("trans", x => x.TransSec = v));
                    if (project.Clips.IndexOf(c) == 0 && c.Trans != "black" && c.Trans != "white") Hint("첫 조각에는 '검게'·'하얗게'만 보여요.");
                }
                ProxySection(c);
            }
            else proxyHint = null;

            Label("더하기");
            Buttons(new[] { "영상·사진", "색 화면" }, new Action[] { AddMediaDialog, AddColorClip });
            Hint("파일을 이 창에 끌어 놓아도 재생 위치 뒤에 들어가요.");

            Label("표시");
            Act("재생 위치에 표시 넣기 (M)", "Line", AddMarker);
            foreach (var m in project.Markers.OrderBy(x => x.T))
            {
                var mm = m;
                ListRow(Clock(m.T), m.Label, false, delegate
                {
                    SeekTo(mm.T);
                    Select(mm.Id, false);
                });
            }
        }

        static string SpeedChip(double s)
        {
            foreach (var v in new[] { 0.25, 0.5, 1, 1.5, 2 }) if (Math.Abs(s - v) < 0.001) return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "";
        }

        void PanelMarker(VMarker mk)
        {
            Head("표시", "중요한 곳에 표시를 해 두면 Ctrl+←·→로 바로 갈 수 있고, '선생님 도구'에서 유튜브 챕터로 만들 수 있어요.");
            Label("이름");
            string id = mk.Id;
            focusBox = Field(mk.Label, "예: 실험 준비", false, v => Change("mark" + id, delegate
            {
                var x = project.Markers.FirstOrDefault(m => m.Id == id);
                if (x != null) x.Label = v;
            }));
            Hint(Clock(mk.T) + "에 있어요.");
            Buttons(new[] { "재생 위치로 옮기기", "지우기" }, new Action[] { delegate
            {
                double t = CurrentTime;
                Change(null, delegate
                {
                    var x = project.Markers.FirstOrDefault(m => m.Id == id);
                    if (x != null) x.T = t;
                });
                RefreshPanel();
            }, DeleteSelected });
            Act("편집 칸으로", "Quiet", delegate { Select(null, false); });
        }

        // ── 편집 동작 ──

        void SplitHere()
        {
            if (preview.Playing)
            {
                preview.Pause();
                seekT = preview.Now;
                Unhook();
                UpdatePlayButton();
            }
            double t = CurrentTime;
            var it = SelectedItem;
            if (it != null && t > it.Start + 0.05 && t < it.End - 0.05)
            {
                string id = it.Id;
                Change(null, delegate
                {
                    var a = project.FindItem(id);
                    var b = a.Copy();
                    b.Id = VProject.NewId();
                    double off = t - a.Start;
                    b.Start = t;
                    if (a.Keys.Count > 0)
                    {
                        var at = VAnim.At(a.Keys, off).Copy();
                        var startKey = at.Copy();
                        startKey.T = 0;
                        b.Keys = new List<VKey> { startKey };
                        foreach (var k in a.Keys)
                        {
                            if (k.T <= off + 1e-3) continue;
                            var k2 = k.Copy();
                            k2.T -= off;
                            b.Keys.Add(k2);
                        }
                        a.Keys.RemoveAll(k => k.T >= off - 1e-3);
                        at.T = off;
                        a.Keys.Add(at);
                    }
                    a.End = t;
                    a.Out = "none";
                    b.In = "none";
                    project.Items.Insert(project.Items.IndexOf(a) + 1, b);
                });
                SetStatus("고른 글자·그림을 둘로 나눴어요.");
                return;
            }
            double local;
            int i = project.ClipAt(t, out local);
            if (i < 0) return;
            var c = project.Clips[i];
            if (local < 0.05 || local > c.Length - 0.05)
            {
                SetStatus("조각 끝에 너무 가까워서 나눌 수 없어요.");
                return;
            }
            VClip back = null;
            Change(null, delegate { back = project.Split(t); });
            if (back != null)
            {
                selected = back.Id;
                timeline.Selected = selected;
                SetStatus("둘로 나눴어요. 필요 없는 쪽을 골라 Delete를 누르면 지워져요.");
            }
            RefreshPanel();
        }

        void DeleteSelected()
        {
            if (tab == "redact" && selectedRedact != null)
            {
                var c = CurrentClip;
                string rid = selectedRedact;
                if (c != null)
                {
                    string cid = c.Id;
                    Change(null, delegate
                    {
                        var x = project.FindClip(cid);
                        if (x != null) x.Redacts.RemoveAll(r => r.Id == rid);
                    });
                    selectedRedact = null;
                    RefreshPanel();
                    return;
                }
            }
            var it = SelectedItem;
            if (it != null)
            {
                string id = it.Id;
                Change(null, delegate
                {
                    project.Items.RemoveAll(x => x.Id == id);
                    project.Sounds.RemoveAll(s => s.ItemId == id);
                });
                Select(null, false);
                SetStatus("지웠어요. Ctrl+Z로 되돌릴 수 있어요.");
                return;
            }
            var snd = SelectedSound;
            if (snd != null)
            {
                string id = snd.Id;
                Change(null, delegate { project.Sounds.RemoveAll(x => x.Id == id); });
                Select(null, false);
                return;
            }
            var mk = SelectedMarker;
            if (mk != null)
            {
                string id = mk.Id;
                Change(null, delegate { project.Markers.RemoveAll(x => x.Id == id); });
                Select(null, false);
                return;
            }
            var clip = SelectedClip;
            if (clip != null)
            {
                if (project.Clips.Count <= 1)
                {
                    SetStatus("조각이 하나뿐이라 지울 수 없어요. 필요 없는 곳은 나눈 뒤 지우거나 구간을 잘라 내세요.");
                    return;
                }
                string id = clip.Id;
                Change(null, delegate
                {
                    var f = project.Follow();
                    var x = project.FindClip(id);
                    if (x != null) project.RemoveClip(x);
                    f();
                });
                Select(null, false);
                SetStatus("조각을 지웠어요. 뒤 조각이 앞으로 붙었어요.");
                return;
            }
            if (HasRange)
            {
                CutRange();
                return;
            }
            SetStatus("지울 것을 먼저 골라 주세요(시간 막대나 화면에서 누르기).");
        }

        void CutRange()
        {
            if (!HasRange) return;
            double a = timeline.RangeA, b = timeline.RangeB;
            if (a <= 0.02 && b >= project.Duration - 0.02)
            {
                SetStatus("영상 전체를 잘라 낼 수는 없어요.");
                return;
            }
            Change(null, delegate
            {
                var f = project.Follow();
                project.Cut(a, b);
                f();
            });
            timeline.RangeA = timeline.RangeB = -1;
            SeekTo(a);
            SetStatus(Sec(b - a) + "을 잘라 냈어요.");
            RefreshPanel();
        }

        void KeepRange()
        {
            if (!HasRange) return;
            double a = timeline.RangeA, b = timeline.RangeB;
            Change(null, delegate
            {
                var f = project.Follow();
                project.Cut(b, project.Duration + 1);
                project.Cut(0, a);
                f();
            });
            timeline.RangeA = timeline.RangeB = -1;
            SeekTo(0);
            timeline.FitAll();
            SetStatus("고른 구간만 남겼어요.");
            RefreshPanel();
        }

        void SetRange(bool start)
        {
            double t = CurrentTime;
            if (start)
            {
                timeline.RangeA = t;
                if (timeline.RangeB >= 0 && timeline.RangeB <= t + 0.05) timeline.RangeB = Math.Min(project.Duration, t + 2);
                if (timeline.RangeB < 0) timeline.RangeB = project.Duration;
            }
            else
            {
                timeline.RangeB = t;
                if (timeline.RangeA < 0 || timeline.RangeA >= t - 0.05) timeline.RangeA = Math.Max(0, Math.Min(timeline.RangeA < 0 ? 0 : timeline.RangeA, t - 0.1));
            }
            timeline.InvalidateVisual();
            if (tab == "edit" || tab == "export") RefreshPanel();
        }

        void Duplicate()
        {
            var it = SelectedItem;
            if (it != null)
            {
                string id = it.Id;
                string newId = VProject.NewId();
                Change(null, delegate
                {
                    var a = project.FindItem(id);
                    var b = a.Copy();
                    b.Id = newId;
                    if (a.Subtitle)
                    {
                        double len = a.Length;
                        b.Start = a.End;
                        b.End = Math.Min(project.Duration, a.End + len);
                        if (b.End - b.Start < 0.3) b.Start = Math.Max(0, b.End - len);
                    }
                    else
                    {
                        b.X = Math.Min(0.95, a.X + 0.03);
                        b.Y = Math.Min(0.95, a.Y + 0.03);
                        foreach (var k in b.Keys)
                        {
                            k.X += 0.03;
                            k.Y += 0.03;
                        }
                    }
                    project.Items.Add(b);
                });
                Select(newId, true);
                return;
            }
            var snd = SelectedSound;
            if (snd != null)
            {
                string id = snd.Id;
                string newId = VProject.NewId();
                Change(null, delegate
                {
                    var a = project.Sounds.First(x => x.Id == id);
                    var b = a.Copy();
                    b.Id = newId;
                    b.ItemId = null;
                    b.Start = a.Start + a.Length;
                    project.Sounds.Add(b);
                });
                Select(newId, true);
                return;
            }
            var c = CurrentClip;
            if (c == null) return;
            string cid = c.Id;
            string cNew = VProject.NewId();
            Change(null, delegate
            {
                var f = project.Follow();
                var a = project.FindClip(cid);
                var b = a.Copy();
                b.Id = cNew;
                b.Line = cNew;
                b.Trans = "none";
                project.Clips.Insert(project.Clips.IndexOf(a) + 1, b);
                f();
            });
            Select(cNew, false);
            SetStatus("조각을 복제해 바로 뒤에 붙였어요.");
        }

        void InsertFreeze()
        {
            var c = CurrentClip;
            double local, source;
            if (c == null || c.Kind != "video" || !LocalIn(c, out local, out source))
            {
                SetStatus("멈춘 화면은 영상 조각 위에서 넣을 수 있어요.");
                return;
            }
            double t = CurrentTime;
            string cid = c.Id;
            string fid = VProject.NewId();
            Change(null, delegate
            {
                var f = project.Follow();
                var a = project.FindClip(cid);
                double z, px, py;
                VAnim.ClipMotion(a, local, source, out z, out px, out py);
                var back = project.Split(t);
                var fz = a.Copy();
                fz.Id = fid;
                fz.Line = fid;
                fz.Kind = "freeze";
                fz.FreezeAt = source;
                fz.Dur = 2;
                fz.Trans = "none";
                fz.Keys = new List<VKey>();
                fz.Zoom = z;
                fz.PanX = px;
                fz.PanY = py;
                int at = back != null ? project.Clips.IndexOf(back) : project.Clips.IndexOf(a) + (local > a.Length / 2 ? 1 : 0);
                project.Clips.Insert(Math.Max(0, at), fz);
                f();
            });
            Select(fid, false);
            SetStatus("2초 동안 멈춘 화면을 넣었어요. 길이는 왼쪽 칸에서 바꿀 수 있어요.");
        }

        void AddMediaDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "더할 영상·사진·소리 고르기",
                Multiselect = true,
                Filter = "영상·사진·소리|*.mp4;*.mov;*.m4v;*.avi;*.wmv;*.3gp;*.mkv;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.heic;*.mp3;*.m4a;*.wav;*.aac;*.wma|모든 파일|*.*"
            };
            if (dlg.ShowDialog(this) == true) InsertFiles(dlg.FileNames.ToList());
        }

        /// 영상·사진은 재생 위치의 조각 뒤(앞 반이면 앞)에, 소리는 재생 위치에 넣는다
        void InsertFiles(List<string> files)
        {
            var clips = new List<VClip>();
            var sounds = new List<VSound>();
            var skipped = new List<string>();
            double t = CurrentTime;
            foreach (var f0 in files)
            {
                string f = Path.GetFullPath(f0);
                if (VMedia.IsImage(f))
                {
                    clips.Add(new VClip { Kind = "photo", Path = f, Dur = 4, Ken = "in" });
                    continue;
                }
                var pr = VMedia.Probe(f);
                if (pr == null)
                {
                    skipped.Add(Path.GetFileName(f));
                    continue;
                }
                if (pr.HasVideo) clips.Add(new VClip { Kind = "video", Path = f, In = 0, Out = pr.Seconds });
                else if (pr.HasAudio) sounds.Add(new VSound { Kind = "file", Path = f, Start = t, In = 0, Out = pr.Seconds, Volume = 0.8, Name = Path.GetFileNameWithoutExtension(f) });
                else skipped.Add(Path.GetFileName(f));
            }
            if (clips.Count == 0 && sounds.Count == 0)
            {
                SetStatus(skipped.Count > 0 ? "넣지 못했어요: " + string.Join(", ", skipped) : "넣을 파일이 없어요.");
                return;
            }
            Change(null, delegate
            {
                var f = project.Follow();
                double local;
                int i = project.ClipAt(t, out local);
                int at = i < 0 ? project.Clips.Count : (local < project.Clips[i].Length / 2 ? i : i + 1);
                foreach (var c in clips) project.Clips.Insert(at++, c);
                f();
                project.Sounds.AddRange(sounds);
            });
            string msg = (clips.Count > 0 ? clips.Count + "개 조각" : "") + (clips.Count > 0 && sounds.Count > 0 ? "과 " : "") + (sounds.Count > 0 ? "소리 " + sounds.Count + "개" : "") + "를 넣었어요.";
            if (skipped.Count > 0) msg += " 읽지 못한 파일: " + string.Join(", ", skipped);
            string advice = ProxyAdvice(clips);
            if (advice != null) msg += " " + advice;
            SetStatus(msg);
            fileText.Text = Theme.Keep(FilesLine());
            RefreshPanel();
        }

        void AddColorClip()
        {
            double t = CurrentTime;
            string id = VProject.NewId();
            Change(null, delegate
            {
                var f = project.Follow();
                double local;
                int i = project.ClipAt(t, out local);
                int at = i < 0 ? project.Clips.Count : (local < project.Clips[i].Length / 2 ? i : i + 1);
                project.Clips.Insert(at, new VClip { Id = id, Kind = "color", Rgb = 0x22252F, Dur = 3 });
                f();
            });
            Select(id, false);
            SetStatus("색 화면을 넣었어요. '글자·그림'에서 제목을 얹어 보세요.");
        }

        void AddMarker()
        {
            double t = CurrentTime;
            string id = VProject.NewId();
            int n = project.Markers.Count + 1;
            Change(null, delegate { project.Markers.Add(new VMarker { Id = id, T = t, Label = "표시 " + n }); });
            selected = id;
            timeline.Selected = id;
            SetTab("edit");
            timeline.InvalidateVisual();
            RefreshPanel(true);
        }

        // ══ 자막 ══

        string findText = "", replaceText = "";
        string ttsVoice;
        double ttsRate = 1;

        void AddSubtitle()
        {
            double t = CurrentTime, d = project.Duration;
            var style = SelectedItem != null && SelectedItem.Subtitle ? SelectedItem : project.Items.LastOrDefault(i => i.Subtitle);
            // 줄 너비는 화면의 86%: 예전 기본(30%)은 한 줄에 몇 글자 못 들어가 자막이 여러 줄로 쌓였다
            var it = new VItem { Kind = "text", Subtitle = true, Text = "", X = 0.5, Y = 0.86, W = 0.86, Start = t, End = Math.Min(d, t + 2.5), Box = false };
            if (style != null)
            {
                it.TakeStyleFrom(style);
                it.X = style.X;
                it.Y = style.Y;
                it.W = style.W;
            }
            if (it.End - it.Start < 0.4)
            {
                it.Start = Math.Max(0, d - 2.5);
                it.End = d;
            }
            // 바로 뒤 자막과 겹치지 않게
            var next = project.Items.Where(i => i.Subtitle && i.Start > it.Start + 0.01).OrderBy(i => i.Start).FirstOrDefault();
            if (next != null && next.Start < it.End) it.End = Math.Max(it.Start + 0.4, next.Start);
            Change(null, delegate { project.Items.Add(it); });
            selected = it.Id;
            timeline.Selected = it.Id;
            SetTab("subtitle");
            timeline.InvalidateVisual();
            RefreshPanel(true);
        }

        void PanelSubtitle()
        {
            var it = SelectedItem;
            if (it != null && !it.Subtitle) it = null;
            Head("자막", "재생 위치에 자막을 넣고 글을 써요. 시간 막대에서 자막의 끝을 끌면 길이가 바뀌어요.");
            Buttons(new[] { "자막 넣기 (T)" }, new Action[] { AddSubtitle });
            // 'SRT'라는 말은 선생님들이 모를 수 있어서 '자막 파일'로 쓴다(2026-11-13 관리자)
            Buttons(new[] { "자막 파일 불러오기", "자막 파일로 저장" }, new Action[] { ImportSrt, ExportSrt });
            if (it != null)
            {
                string id = it.Id;
                Label("글");
                // 음성으로 바꾼 자막의 글을 고치면 음성은 고치기 전 글을 읽는다: 쓰는 동안 바로 알린다
                var voiced = project.Sounds.FirstOrDefault(s => s.Kind == "voice" && s.ItemId == id);
                TextBlock staleNote = null;
                focusBox = Field(it.Text, "자막을 써 주세요", true, v =>
                {
                    SetItem("text", x => x.Text = v);
                    if (staleNote != null) staleNote.Visibility = VProject.SameText(voiced.Text, v) ? Visibility.Collapsed : Visibility.Visible;
                });
                if (voiced != null)
                {
                    staleNote = Theme.Text("글을 고쳐서 음성과 달라요. 아래 [이 자막을 음성으로]를 누르면 새 글로 다시 만들어요.", 13, FontWeights.Bold, Theme.Yellow.DeepBrush);
                    staleNote.TextWrapping = TextWrapping.Wrap;
                    staleNote.Margin = new Thickness(0, 4, 0, 2);
                    staleNote.Visibility = VProject.SameText(voiced.Text, it.Text) ? Visibility.Collapsed : Visibility.Visible;
                    panel.Children.Add(staleNote);
                }
                Hint(Clock(it.Start) + " ~ " + Clock(it.End) + " (" + Sec(it.Length) + ") · 1초에 " + Math.Round((it.Text ?? "").Replace(" ", "").Length / Math.Max(0.1, it.Length), 1) + "자");
                Buttons(new[] { "복사 (Ctrl+D)", "지우기" }, new Action[] { Duplicate, DeleteSelected });
                Buttons(new[] { "여기서 시작", "여기서 끝", "다음 자막" }, new Action[] { delegate
                {
                    double t = CurrentTime;
                    SetItem(null, x =>
                    {
                        // 음성도 같은 만큼 따라 옮긴다
                        var keep = project.KeepVoices(new[] { x.Id });
                        x.Start = Math.Min(t, x.End - 0.2);
                        keep();
                    });
                    RefreshPanel();
                }, delegate
                {
                    double t = CurrentTime;
                    SetItem(null, x =>
                    {
                        x.End = Math.Max(t, x.Start + 0.2);
                    });
                    RefreshPanel();
                }, delegate
                {
                    var nx = project.Items.Where(i => i.Subtitle && i.Start > it.Start + 0.01).OrderBy(i => i.Start).FirstOrDefault();
                    if (nx == null)
                    {
                        SeekTo(it.End);
                        AddSubtitle();
                        return;
                    }
                    SeekTo(nx.Start + 0.01);
                    Select(nx.Id, true);
                    RefreshPanel(true);
                } });

                Label("자리");
                Chips(new[] { "0.86", "0.5", "0.14" }, new[] { "아래", "가운데", "위" }, PosChip(it.Y), v =>
                {
                    double y = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                    SetItem(null, x =>
                    {
                        x.Y = y;
                        x.X = 0.5;
                        x.Keys.Clear();
                    });
                });
                Buttons(new[] { "모든 자막을 이 자리로" }, new Action[] { AllSubtitlesHere });
                Hint("화면에서 자막 상자 양옆의 세로 손잡이를 끌면 한 줄 너비가, 모서리를 끌면 글자 크기가 바뀌어요.");
                StyleControls(it);
                Buttons(new[] { "이 모양을 모든 자막에" }, new Action[] { delegate
                {
                    Change(null, delegate
                    {
                        var src = project.FindItem(id);
                        foreach (var x in project.Items.Where(i => i.Subtitle && i.Id != id))
                        {
                            x.TakeStyleFrom(src);
                            x.X = src.X;
                            x.Y = src.Y;
                            x.W = src.W;
                        }
                    });
                    SetStatus("모든 자막을 같은 모양으로 맞췄어요.");
                } });
            }
            else
            {
                Hint("시간 막대의 자막을 누르거나 T를 눌러 새 자막을 넣으면, 글과 모양을 여기서 바꿀 수 있어요.");
                AutoSubSection();
            }

            VoiceSection(it);

            Label("찾아 바꾸기");
            Field(findText, "찾을 말", false, v => findText = v);
            Field(replaceText, "바꿀 말", false, v => replaceText = v);
            Act("자막에서 모두 바꾸기", "Line", ReplaceAll);

            Label("모든 자막 옮기기");
            Hint("소리보다 자막이 늦거나 빠를 때 한꺼번에 맞춰요.");
            Buttons(new[] { "-1초", "-0.1초", "+0.1초", "+1초" }, new Action[]
            {
                delegate { ShiftSubtitles(-1); }, delegate { ShiftSubtitles(-0.1); }, delegate { ShiftSubtitles(0.1); }, delegate { ShiftSubtitles(1); }
            });

            var subs = project.Items.Where(i => i.Subtitle).OrderBy(i => i.Start).ToList();
            Label("자막 " + subs.Count + "개");
            foreach (var s in subs.Take(400))
            {
                var ss = s;
                ListRow(Clock(s.Start), s.Text, it != null && s.Id == it.Id, delegate
                {
                    SeekTo(ss.Start + 0.01);
                    Select(ss.Id, true);
                    RefreshPanel(true);
                });
            }
            if (subs.Count > 400) Hint("앞의 400개만 보여요.");
        }

        static string PosChip(double y)
        {
            foreach (var v in new[] { 0.86, 0.5, 0.14 }) if (Math.Abs(y - v) < 0.005) return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "";
        }

        List<string> fontsCache;
        bool allFonts;

        /// 글자 모양(자막·제목 같이 쓴다)
        void StyleControls(VItem it)
        {
            Label("글꼴");
            if (fontsCache == null)
            {
                try
                {
                    fontsCache = VFonts.Korean();
                }
                catch (Exception ex)
                {
                    Log.Write("글꼴 목록 실패: " + ex.GetType().Name);
                    fontsCache = new List<string> { "맑은 고딕" };
                }
            }
            var list = fontsCache.ToList();
            if (!list.Contains(it.Font)) list.Insert(0, it.Font);
            var show = allFonts ? list : list.Take(10).ToList();
            if (!show.Contains(it.Font)) show.Insert(0, it.Font);
            var wrap = new WrapPanel();
            string group = "vg" + (++groupCounter);
            foreach (var f in show)
            {
                string font = f;
                var r = new RadioButton
                {
                    Style = Theme.GetStyle("Chip"),
                    Tag = H,
                    GroupName = group,
                    Content = new TextBlock { Text = font, FontFamily = new FontFamily(font) },
                    MinHeight = 36,
                    FontSize = 14,
                    Margin = new Thickness(0, 0, 6, 6),
                    IsChecked = font == it.Font
                };
                r.Checked += delegate
                {
                    if (!building) SetItem(null, x => x.Font = font);
                };
                wrap.Children.Add(r);
            }
            panel.Children.Add(wrap);
            if (list.Count > 10)
            {
                var more = SmallButton(allFonts ? "글꼴 줄여 보기" : "글꼴 " + list.Count + "개 모두 보기", delegate
                {
                    allFonts = !allFonts;
                    RefreshPanel();
                });
                more.HorizontalAlignment = HorizontalAlignment.Left;
                panel.Children.Add(more);
            }
            SliderRow("크기", 0.025, 0.16, it.Size, 0.005, v => Math.Round(v * 1000) / 10 + "", v => SetItem("size", x => x.Size = v));
            Toggle("굵게", null, it.Bold, v => SetItem(null, x => x.Bold = v));
            Label("글자 색");
            Swatches(Colors, it.Rgb, v => SetItem(null, x => x.Rgb = v));
            SliderRow("테두리", 0, 0.3, it.Outline, 0.01, Pct, v => SetItem("outline", x => x.Outline = v));
            if (it.Outline > 0.005)
            {
                Swatches(new uint[] { 0x15161A, 0xFFFFFF, 0x0D6F66, 0xB53A28, 0x2259B8 }, it.OutlineRgb, v => SetItem(null, x => x.OutlineRgb = v));
            }
            Toggle("그림자", null, it.Shadow, v => SetItem(null, x => x.Shadow = v));
            Toggle("바탕 상자", "글자 뒤에 반투명 상자를 깔아 어떤 장면에서도 잘 읽혀요", it.Box, v =>
            {
                SetItem(null, x => x.Box = v);
                RefreshPanel();
            });
            if (it.Box)
            {
                Swatches(new uint[] { 0x15161A, 0xFFFFFF, 0xFFD447, 0x38C7B8, 0xFF8473 }, it.BoxRgb, v => SetItem(null, x => x.BoxRgb = v));
                SliderRow("상자 진하기", 0.1, 1, it.BoxAlpha, 0.05, Pct, v => SetItem("boxa", x => x.BoxAlpha = v));
            }
            Label("줄 맞춤");
            Chips(new[] { "left", "center", "right" }, new[] { "왼쪽", "가운데", "오른쪽" }, it.Align, v => SetItem(null, x => x.Align = v));
            Label("강조할 낱말");
            Field(it.Emph, "예: 구름, 비 (쉼표로 여럿)", false, v => SetItem("emph", x => x.Emph = v));
            if (!string.IsNullOrWhiteSpace(it.Emph)) Swatches(new uint[] { 0xFFD447, 0xFF8473, 0x52D3A0, 0x6CA8FF, 0xFF8FC2 }, it.EmphRgb, v => SetItem(null, x => x.EmphRgb = v));
            Label("나타날 때");
            Chips(new[] { "none", "fade", "pop", "rise", "slide", "type" }, new[] { "바로", "서서히", "톡", "올라오기", "옆에서", "한 자씩" }, it.In, v => SetItem(null, x => x.In = v));
            Label("사라질 때");
            Chips(new[] { "none", "fade" }, new[] { "바로", "서서히" }, it.Out, v => SetItem(null, x => x.Out = v));
        }

        void TtsControls()
        {
            List<VTts.Voice> voices;
            try
            {
                voices = VTts.Voices();
            }
            catch (Exception ex)
            {
                Log.Write("목소리 목록 실패: " + ex.GetType().Name);
                voices = new List<VTts.Voice>();
            }
            var korean = voices.Where(v => v.Korean).ToList();
            if (korean.Count == 0)
            {
                Hint("이 PC에 한국어 목소리가 없어요. 윈도우 설정 › 시간 및 언어 › 음성에서 한국어 목소리를 더할 수 있어요.");
                return;
            }
            if (ttsVoice == null || !korean.Any(v => v.Id == ttsVoice)) ttsVoice = korean[0].Id;
            if (korean.Count > 1) Chips(korean.Select(v => v.Id).ToArray(), korean.Select(v => ShortVoice(v.Name)).ToArray(), ttsVoice, v => ttsVoice = v);
            else Hint("목소리: " + ShortVoice(korean[0].Name));
            SliderRow("음성 빠르기", 0.6, 1.6, ttsRate, 0.1, Times, v => ttsRate = v);
        }

        /// 자막 칸의 '자막을 음성으로'. 자막을 고르지 않아도 보인다([모든 자막을 음성으로]).
        void VoiceSection(VItem it)
        {
            Label("자막을 음성으로");
            if (!project.Items.Any(i => i.Subtitle && !string.IsNullOrWhiteSpace(i.Text)))
            {
                Hint("자막을 넣으면 그 글을 목소리로 읽어 음성으로 넣을 수 있어요.");
                return;
            }
            Hint("자막 글을 목소리로 읽어 음성으로 넣어요. 자막을 옮기면 음성도 따라가요. 글은 이 PC 밖으로 나가지 않아요.");
            var stale = VideoExport.StaleVoices(project);
            if (stale != null) CheckRow(stale);
            TtsControls();
            if (it != null)
            {
                string id = it.Id;
                Act("이 자막을 음성으로", "Line", delegate { SubtitlesToVoice(new List<string> { id }, false); });
            }
            Act("모든 자막을 음성으로", "Line", delegate
            {
                SubtitlesToVoice(project.Items.Where(i => i.Subtitle && !string.IsNullOrWhiteSpace(i.Text)).OrderBy(i => i.Start).Select(i => i.Id).ToList(), false);
            });
        }

        static string ShortVoice(string name)
        {
            if (string.IsNullOrEmpty(name)) return "목소리";
            int i = name.IndexOf(" - ", StringComparison.Ordinal);
            string s = i > 0 ? name.Substring(0, i) : name;
            return s.Replace("Microsoft ", "");
        }

        sealed class VoiceJob
        {
            public string ItemId, Text, Voice, Path;
            public double Rate, Len;
        }

        /// 자막을 음성으로 바꿔 넣는다. 영어 자막은 영어 목소리로 읽는다(이 PC에 영어 목소리가 있을 때).
        /// 음성이 자막보다 길면 자막을 늘린다(다음 자막과 겹치지 않는 만큼).
        /// again이면 '바뀐 자막만 다시 만들기': 음성마다 전에 쓴 목소리·빠르기로 만들고, 글이 빈 자막의 음성은 뺀다.
        void SubtitlesToVoice(List<string> ids, bool again)
        {
            var items = ids.Select(id => project.FindItem(id)).Where(i => i != null).ToList();
            var clear = again ? items.Where(i => string.IsNullOrWhiteSpace(i.Text)).Select(i => i.Id).ToList() : new List<string>();
            var jobs = new List<VoiceJob>();
            foreach (var i in items.Where(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                var old = again ? project.Sounds.FirstOrDefault(s => s.Kind == "voice" && s.ItemId == i.Id) : null;
                jobs.Add(new VoiceJob { ItemId = i.Id, Text = i.Text, Voice = old != null ? old.Voice : ttsVoice, Rate = old != null && old.Rate > 0 ? old.Rate : ttsRate });
            }
            if (jobs.Count == 0 && clear.Count == 0)
            {
                SetStatus(items.Count == 0 ? "음성으로 바꿀 자막이 없어요." : "자막에 글을 먼저 써 주세요.");
                return;
            }
            if (jobs.Count == 0)
            {
                Change(null, delegate { project.Sounds.RemoveAll(s => s.ItemId != null && clear.Contains(s.ItemId)); });
                SetStatus("글이 빈 자막 " + clear.Count + "개의 음성을 뺐어요.");
                RefreshPanel();
                return;
            }
            string chosen = ttsVoice;
            int english = 0, noEnglish = 0;
            RunBusy("자막을 음성으로 바꾸는 중", delegate (Action<string, double> report)
            {
                var voices = VTts.Voices();
                var korean = voices.Where(v => v.Korean).ToList();
                if (korean.Count == 0) throw new UserFacingException("이 PC에 한국어 목소리가 없어요. 윈도우 설정 › 시간 및 언어 › 음성에서 한국어 목소리를 더할 수 있어요.");
                string fallback = chosen != null && korean.Any(v => v.Id == chosen) ? chosen : korean[0].Id;
                for (int i = 0; i < jobs.Count; i++)
                {
                    if (busyCancel) throw new OperationCanceledException();
                    report("자막을 음성으로 바꾸는 중 (" + (i + 1) + "/" + jobs.Count + ")", i / (double)jobs.Count);
                    var j = jobs[i];
                    string speak = j.Text.Replace("\r", " ").Replace("\n", " ");
                    // 바탕은 한국어 목소리(전에 영어 자막을 읽은 목소리였으면 고른 한국어 목소리로), 영어 문장이면 영어 목소리
                    string baseVoice = j.Voice != null && korean.Any(v => v.Id == j.Voice) ? j.Voice : fallback;
                    j.Voice = VLecture.VoiceFor(speak, baseVoice, voices);
                    if (VLecture.LooksEnglish(speak))
                    {
                        if (j.Voice != baseVoice) english++;
                        else noEnglish++;
                    }
                    j.Path = VTts.Speak(speak, j.Voice, j.Rate);
                    j.Len = SoundSeconds(j.Path);
                }
            }, delegate
            {
                int made = 0, longer = 0;
                Change(null, delegate
                {
                    if (clear.Count > 0) project.Sounds.RemoveAll(s => s.ItemId != null && clear.Contains(s.ItemId));
                    foreach (var j in jobs)
                    {
                        // 만드는 동안 지운 자막은 건너뛴다. 그동안 글을 고쳤으면 '바뀐 자막'으로 잡힌다(읽은 글을 적어 두므로).
                        var it = project.FindItem(j.ItemId);
                        if (it == null) continue;
                        if (project.PutVoice(it, j.Text, j.Path, j.Len, j.Voice, j.Rate)) longer++;
                        made++;
                    }
                });
                var sb = new StringBuilder(again ? "바뀐 자막 " + made + "개의 음성을 새 글로 다시 만들었어요." : "자막 " + made + "개를 음성으로 바꿨어요.");
                if (clear.Count > 0) sb.Append(" 글이 빈 자막 " + clear.Count + "개의 음성은 뺐어요.");
                if (english > 0) sb.Append(" 영어 자막 " + english + "개는 영어 목소리로 읽었어요.");
                if (noEnglish > 0) sb.Append(" 이 PC에 영어 목소리가 없어서 영어 자막 " + noEnglish + "개는 한국어 목소리로 읽었어요(윈도우 설정 › 시간 및 언어 › 음성에서 영어 목소리를 더할 수 있어요).");
                if (longer > 0) sb.Append(" 음성이 다음 자막과 겹치는 곳이 " + longer + "군데 있어요. 자막을 나누거나 음성 빠르기를 올려 보세요.");
                SetStatus(sb.ToString());
                RefreshPanel();
            });
        }

        static double SoundSeconds(string path)
        {
            var pr = VMedia.Probe(path);
            if (pr != null && pr.Seconds > 0) return pr.Seconds;
            try
            {
                return Math.Max(0.1, (new FileInfo(path).Length - 44) / (4.0 * AudioTrack.Rate));
            }
            catch (Exception)
            {
                return 1;
            }
        }

        void ReplaceAll()
        {
            string a = findText ?? "", b = replaceText ?? "";
            if (a.Length == 0)
            {
                SetStatus("찾을 말을 써 주세요.");
                return;
            }
            int n = 0;
            foreach (var it in project.Items.Where(i => i.Subtitle)) n += Count(it.Text, a);
            if (n == 0)
            {
                SetStatus("'" + a + "'을(를) 찾지 못했어요.");
                return;
            }
            Change(null, delegate
            {
                foreach (var it in project.Items.Where(i => i.Subtitle)) it.Text = (it.Text ?? "").Replace(a, b);
            });
            SetStatus(n + "곳을 바꿨어요.");
            RefreshPanel();
        }

        static int Count(string s, string part)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0, i = 0;
            while ((i = s.IndexOf(part, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += part.Length;
            }
            return n;
        }

        void ShiftSubtitles(double dt)
        {
            if (!project.Items.Any(i => i.Subtitle)) return;
            Change("shiftsub", delegate
            {
                double d = project.Duration;
                var subs = project.Items.Where(i => i.Subtitle).ToList();
                // 음성은 자막이 옮겨진 만큼 따라간다(따로 옮겨 둔 간격은 그대로)
                var keep = project.KeepVoices(subs.Select(i => i.Id));
                foreach (var it in subs)
                {
                    double len = it.Length;
                    it.Start = Math.Max(0, Math.Min(d - 0.1, it.Start + dt));
                    it.End = Math.Min(d, it.Start + len);
                }
                keep();
            });
            SetStatus("모든 자막을 " + (dt > 0 ? "뒤로 " : "앞으로 ") + Math.Abs(dt) + "초 옮겼어요.");
        }

        // ── 자막 파일(SRT·VTT) ──

        void ImportSrt()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "자막 파일 고르기", Filter = "자막 파일|*.srt;*.vtt|모든 파일|*.*" };
            if (dlg.ShowDialog(this) != true) return;
            List<VItem> subs;
            try
            {
                subs = ParseSrt(File.ReadAllBytes(dlg.FileName));
            }
            catch (Exception ex)
            {
                Log.Write("SRT 읽기 실패: " + ex.GetType().Name);
                SetStatus("자막 파일을 읽지 못했어요.");
                return;
            }
            if (subs.Count == 0)
            {
                SetStatus("자막 파일에서 자막을 찾지 못했어요.");
                return;
            }
            var style = project.Items.LastOrDefault(i => i.Subtitle);
            double d = project.Duration;
            int outside = 0;
            Change(null, delegate
            {
                foreach (var s in subs)
                {
                    if (s.Start >= d)
                    {
                        outside++;
                        continue;
                    }
                    s.End = Math.Min(d, s.End);
                    if (style != null)
                    {
                        s.TakeStyleFrom(style);
                        s.X = style.X;
                        s.Y = style.Y;
                        s.W = style.W;
                    }
                    project.Items.Add(s);
                }
            });
            SetStatus((subs.Count - outside) + "개 자막을 불러왔어요." + (outside > 0 ? " 영상보다 뒤에 있는 " + outside + "개는 뺐어요." : ""));
            RefreshPanel();
        }

        internal static List<VItem> ParseSrt(byte[] bytes)
        {
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.GetEncoding(949).GetString(bytes);
            }
            text = text.TrimStart('﻿').Replace("\r\n", "\n").Replace("\r", "\n");
            var list = new List<VItem>();
            foreach (var block in text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                int ti = lines.FindIndex(l => l.Contains("-->"));
                if (ti < 0) continue;
                var parts = lines[ti].Split(new[] { "-->" }, StringSplitOptions.None);
                double a, b;
                if (parts.Length < 2 || !SrtTime(parts[0], out a) || !SrtTime(parts[1], out b) || b <= a) continue;
                // VTT는 &amp; 같은 글자 표시를 쓴다
                string body = System.Net.WebUtility.HtmlDecode(string.Join("\n", lines.Skip(ti + 1).Select(StripTags)));
                list.Add(new VItem { Kind = "text", Subtitle = true, Text = body, Start = a, End = b, X = 0.5, Y = 0.86, W = 0.86 });
            }
            return list;
        }

        static string StripTags(string s)
        {
            return System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>|\\{\\\\[^}]*\\}", "");
        }

        static bool SrtTime(string s, out double t)
        {
            t = 0;
            s = s.Trim();
            int sp = s.IndexOf(' ');
            if (sp > 0) s = s.Substring(0, sp);
            // SRT는 시:분:초,천분의 일. 유튜브에서 받은 VTT는 시가 없을 수 있다(분:초.천분의 일).
            var m = System.Text.RegularExpressions.Regex.Match(s, "^(?:(\\d+):)?(\\d{1,2}):(\\d{1,2})[,.](\\d{1,3})$");
            if (!m.Success) return false;
            int hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            t = hours * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) + int.Parse(m.Groups[4].Value.PadRight(3, '0')) / 1000.0;
            return true;
        }

        internal static string SrtClock(double t)
        {
            var ts = TimeSpan.FromMilliseconds(Math.Max(0, Math.Round(t * 1000)));
            return ((int)ts.TotalHours).ToString("00") + ":" + ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00") + "," + ts.Milliseconds.ToString("000");
        }

        void ExportSrt()
        {
            var subs = project.Items.Where(i => i.Subtitle && !string.IsNullOrWhiteSpace(i.Text)).OrderBy(i => i.Start).ToList();
            if (subs.Count == 0)
            {
                SetStatus("저장할 자막이 없어요.");
                return;
            }
            string first = project.Opened.FirstOrDefault(File.Exists);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "자막 파일로 저장",
                Filter = "자막 파일|*.srt",
                FileName = (first != null ? Path.GetFileNameWithoutExtension(first) : "자막") + ".srt"
            };
            if (first != null) dlg.InitialDirectory = Path.GetDirectoryName(first);
            if (dlg.ShowDialog(this) != true) return;
            var sb = new StringBuilder();
            for (int i = 0; i < subs.Count; i++)
            {
                sb.Append(i + 1).Append("\r\n");
                sb.Append(SrtClock(subs[i].Start)).Append(" --> ").Append(SrtClock(subs[i].End)).Append("\r\n");
                sb.Append(subs[i].Text.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
            }
            try
            {
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                SetStatus("자막 " + subs.Count + "개를 '" + Path.GetFileName(dlg.FileName) + "'로 저장했어요.");
            }
            catch (Exception ex)
            {
                Log.Write("SRT 저장 실패: " + ex.GetType().Name);
                SetStatus("자막 파일을 저장하지 못했어요.");
            }
        }

        // ══ 글자·그림 ══

        static readonly string[][] Shapes =
        {
            new[] { "arrow", "화살표" }, new[] { "box", "네모" }, new[] { "circle", "동그라미" },
            new[] { "marker", "형광펜" }, new[] { "line", "선" }, new[] { "bar", "이름 막대" }
        };

        void PanelDeco()
        {
            var it = SelectedItem;
            if (it != null && it.Subtitle) it = null;
            Head("글자·그림", "제목·그림·스티커·도형·작은 화면을 얹어요. 화면에서 끌어 옮기고, 모서리로 크기를, 위 동그라미로 돌려요.");
            Label("넣기");
            Buttons(new[] { "제목 글자", "그림 파일", "작은 화면" }, new Action[] { AddTitle, AddImageDialog, AddPipDialog });
            var shapeRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var s in Shapes)
            {
                string id = s[0];
                var b = SmallButton(s[1], delegate { AddShape(id); });
                b.Margin = new Thickness(0, 0, 6, 6);
                shapeRow.Children.Add(b);
            }
            panel.Children.Add(shapeRow);
            Label("스티커");
            var stickers = new WrapPanel();
            foreach (var s in VOverlay.Stickers)
            {
                string id = s[0];
                var img = new Image { Source = StickerImage(id), Width = 34, Height = 34 };
                var b = new Button { Style = Theme.GetStyle("Quiet"), Width = 46, Height = 46, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4), Content = img, ToolTip = s[1] };
                b.Click += delegate { AddSticker(id); };
                stickers.Children.Add(b);
            }
            panel.Children.Add(stickers);

            if (it == null)
            {
                NoteBox("화면이나 시간 막대에서 얹은 것을 누르면 여기서 모양·나타나기·움직임을 바꿀 수 있어요.");
                return;
            }
            Label(ItemTitle(it) + " · " + Clock(it.Start) + " ~ " + Clock(it.End));
            switch (it.Kind)
            {
                case "text":
                    focusBox = Field(it.Text, "글을 써 주세요", true, v => SetItem("text", x => x.Text = v));
                    StyleControls(it);
                    break;
                case "image":
                    SliderRow("크기", 0.03, 1.2, it.W, 0.01, Pct, v => SetItem("w", x => x.W = v));
                    Label("자리");
                    Chips(new[] { "tl", "tr", "bl", "br", "c" }, new[] { "왼쪽 위", "오른쪽 위", "왼쪽 아래", "오른쪽 아래", "가운데" }, "", v => SetItem(null, x => Corner(x, v)));
                    Buttons(new[] { "처음부터 끝까지 보이기" }, new Action[] { delegate
                    {
                        double d = project.Duration;
                        SetItem(null, x =>
                        {
                            x.Start = 0;
                            x.End = d;
                            x.In = x.Out = "none";
                        });
                        RefreshPanel();
                    } });
                    break;
                case "sticker":
                    SliderRow("크기", 0.03, 0.8, it.W, 0.01, Pct, v => SetItem("w", x => x.W = v));
                    if (!(it.Sticker ?? "").StartsWith("t_") && !(it.Sticker ?? "").StartsWith("n"))
                    {
                        Label("색");
                        Swatches(Colors, it.Rgb, v => SetItem(null, x => x.Rgb = v));
                    }
                    break;
                case "shape":
                    Label("색");
                    Swatches(Colors, it.Rgb, v => SetItem(null, x => x.Rgb = v));
                    if (it.Shape != "marker" && it.Shape != "bar" && it.Shape != "spot") SliderRow("굵기", 0.003, 0.04, it.Thick, 0.001, v => Math.Round(v * 1000) / 10 + "", v => SetItem("thick", x => x.Thick = v));
                    if (it.Shape == "box") SliderRow("모서리 둥글게", 0, 0.5, it.Corner, 0.02, Pct, v => SetItem("corner", x => x.Corner = v));
                    break;
                case "pip":
                    PipControls(it);
                    break;
            }
            if (it.Kind != "text")
            {
                Label("나타날 때");
                Chips(new[] { "none", "fade", "pop", "rise", "slide" }, new[] { "바로", "서서히", "톡", "올라오기", "옆에서" }, it.In, v => SetItem(null, x => x.In = v));
                Label("사라질 때");
                Chips(new[] { "none", "fade" }, new[] { "바로", "서서히" }, it.Out, v => SetItem(null, x => x.Out = v));
            }
            ItemLookSliders(it);
            ItemMotionSection(it);

            Label("시간·순서");
            Buttons(new[] { "여기서 시작", "여기서 끝" }, new Action[] { delegate
            {
                double t = CurrentTime;
                SetItem(null, x => x.Start = Math.Min(t, x.End - 0.1));
                RefreshPanel();
            }, delegate
            {
                double t = CurrentTime;
                SetItem(null, x => x.End = Math.Max(t, x.Start + 0.1));
                RefreshPanel();
            } });
            Buttons(new[] { "맨 앞으로", "맨 뒤로" }, new Action[] { delegate
            {
                string id = it.Id;
                Change(null, delegate
                {
                    var x = project.FindItem(id);
                    project.Items.Remove(x);
                    project.Items.Add(x);
                });
            }, delegate
            {
                string id = it.Id;
                Change(null, delegate
                {
                    var x = project.FindItem(id);
                    project.Items.Remove(x);
                    project.Items.Insert(0, x);
                });
            } });
            Buttons(new[] { "복제 (Ctrl+D)", "지우기" }, new Action[] { Duplicate, DeleteSelected });
        }

        static void Corner(VItem x, string where)
        {
            x.Keys.Clear();
            double m = 0.04 + x.W / 2;
            switch (where)
            {
                case "tl": x.X = m; x.Y = 0.11; break;
                case "tr": x.X = 1 - m; x.Y = 0.11; break;
                case "bl": x.X = m; x.Y = 0.86; break;
                case "br": x.X = 1 - m; x.Y = 0.86; break;
                default: x.X = 0.5; x.Y = 0.5; break;
            }
        }

        static string ItemTitle(VItem it)
        {
            switch (it.Kind)
            {
                case "text": return it.Subtitle ? "자막" : "글자";
                case "image": return "그림";
                case "sticker": return "스티커 '" + VOverlay.StickerLabel(it.Sticker) + "'";
                case "pip": return "작은 화면";
                case "shape":
                    foreach (var s in Shapes) if (s[0] == it.Shape) return s[1];
                    return it.Shape == "spot" ? "마우스 강조" : it.Shape == "ripple" ? "누른 자리" : "도형";
            }
            return "얹은 것";
        }

        static ImageSource StickerImage(string id)
        {
            var dg = new DrawingGroup();
            using (var dc = dg.Open())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 40, 40));
                double a = VOverlay.StickerAspect(id);
                var r = a >= 1 ? new Rect(0, (40 - 40 / a) / 2, 40, 40 / a) : new Rect((40 - 40 * a) / 2, 0, 40 * a, 40);
                VOverlay.DrawSticker(dc, id, r, StickerRgb(id));
            }
            dg.Freeze();
            var img = new DrawingImage(dg);
            img.Freeze();
            return img;
        }

        static uint StickerRgb(string id)
        {
            switch (id)
            {
                case "heart":
                case "cross": return 0xFF8473;
                case "check": return 0x52D3A0;
                case "bubble": return 0xFFFFFF;
                case "question": return 0x6CA8FF;
                default: return 0xFFD447;
            }
        }

        /// 넣을 때의 시간: 재생 위치부터 secs(끝에 가까우면 앞으로 당긴다)
        void SpanAt(VItem it, double secs)
        {
            double d = project.Duration, t = CurrentTime;
            it.Start = t;
            it.End = Math.Min(d, t + secs);
            if (it.End - it.Start < Math.Min(secs, 1))
            {
                it.Start = Math.Max(0, d - secs);
                it.End = d;
            }
        }

        void AddItem(VItem it)
        {
            Change(null, delegate { project.Items.Add(it); });
            selected = it.Id;
            timeline.Selected = it.Id;
            SetTab("deco");
            timeline.InvalidateVisual();
            RefreshPanel(it.Kind == "text");
        }

        void AddTitle()
        {
            var it = new VItem { Kind = "text", Text = "제목을 써 주세요", Size = 0.085, X = 0.5, Y = 0.45, W = 0.8, Rgb = 0xFFFFFF, Outline = 0.12, Shadow = true, In = "pop" };
            SpanAt(it, 3);
            AddItem(it);
        }

        void AddImageDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "얹을 그림 고르기 (로고·사진)", Filter = "그림|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.heic|모든 파일|*.*" };
            if (dlg.ShowDialog(this) != true) return;
            if (VOverlay.ImageOf(dlg.FileName) == null)
            {
                SetStatus("그림을 읽지 못했어요.");
                return;
            }
            var it = new VItem { Kind = "image", Path = dlg.FileName, W = 0.28, X = 0.5, Y = 0.5 };
            SpanAt(it, 4);
            AddItem(it);
        }

        void AddPipDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "작은 화면으로 얹을 영상 고르기", Filter = "영상|*.mp4;*.mov;*.m4v;*.avi;*.wmv;*.3gp|모든 파일|*.*" };
            if (dlg.ShowDialog(this) != true) return;
            AddPip(dlg.FileName, false);
        }

        /// 작은 화면(영상 위 영상). face면 선생님 얼굴용(동그랗게, 오른쪽 아래, 소리는 켬).
        void AddPip(string path, bool face)
        {
            var pr = VMedia.Probe(path);
            if (pr == null || !pr.HasVideo)
            {
                SetStatus("영상을 읽지 못했어요: " + Path.GetFileName(path) + (VMedia.Problem(path) != null ? " (" + VMedia.Problem(path) + ")" : ""));
                return;
            }
            var it = new VItem { Kind = "pip", Path = path, SrcIn = 0, SrcOut = pr.Seconds, W = face ? 0.22 : 0.32, X = face ? 0.85 : 0.78, Y = face ? 0.78 : 0.76, Mute = !face, Circle = face, Border = true, In = "fade", Out = "fade" };
            double t = face ? 0 : CurrentTime, d = project.Duration;
            it.Start = t;
            it.End = Math.Min(d, t + pr.Seconds);
            if (it.End - it.Start < 0.5)
            {
                it.Start = Math.Max(0, d - pr.Seconds);
                it.End = d;
            }
            AddItem(it);
            if (face) SetStatus("선생님 얼굴 작은 화면을 넣었어요. 화면에서 끌어 자리를 옮길 수 있어요.");
        }

        void PipControls(VItem it)
        {
            SliderRow("크기", 0.08, 1, it.W, 0.01, Pct, v => SetItem("w", x => x.W = v));
            Hint("원본 " + Clock(it.SrcIn) + " ~ " + Clock(it.SrcOut) + "을 보여 줘요.");
            Toggle("동그랗게", null, it.Circle, v => SetItem(null, x => x.Circle = v));
            Toggle("테두리", null, it.Border, v =>
            {
                SetItem(null, x => x.Border = v);
                RefreshPanel();
            });
            if (it.Border && !it.Chroma) Swatches(new uint[] { 0xFFFFFF, 0x22252F, 0x38C7B8, 0xFFD447, 0xFF8473 }, it.BorderRgb, v => SetItem(null, x => x.BorderRgb = v));
            Toggle("소리 켜기", "작은 화면의 소리도 들려요", !it.Mute, v =>
            {
                SetItem(null, x => x.Mute = !v);
                RefreshPanel();
            });
            if (!it.Mute) SliderRow("소리 크기", 0, 2, it.Volume, 0.05, Pct, v => SetItem("vol", x => x.Volume = v));
            Toggle("초록 화면 지우기", "초록(또는 파랑) 바탕 앞에서 찍은 영상의 바탕을 지워요", it.Chroma, v =>
            {
                SetItem(null, x => x.Chroma = v);
                RefreshPanel();
            });
            if (it.Chroma)
            {
                Chips(new[] { "00B140", "0047BB" }, new[] { "초록 바탕", "파랑 바탕" }, it.ChromaRgb == 0x0047BB ? "0047BB" : "00B140", v => SetItem(null, x => x.ChromaRgb = Convert.ToUInt32(v, 16)));
                SliderRow("지우는 범위", 0.1, 0.7, it.ChromaTol, 0.01, Pct, v => SetItem("tol", x => x.ChromaTol = v));
            }
        }

        void AddShape(string shape)
        {
            var it = new VItem { Kind = "shape", Shape = shape, Rgb = 0xFF8473, Thick = 0.012, In = "fade" };
            switch (shape)
            {
                case "arrow":
                    it.X = 0.42;
                    it.Y = 0.42;
                    it.W = 0.16;
                    it.H = 0.14;
                    break;
                case "line":
                    it.X = 0.5;
                    it.Y = 0.6;
                    it.W = 0.3;
                    it.H = 0;
                    break;
                case "circle":
                    it.W = 0.18;
                    it.H = 0.18 * project.AspectRatio;
                    break;
                case "marker":
                    it.Rgb = 0xFFD447;
                    it.W = 0.3;
                    it.H = 0.07;
                    break;
                case "bar":
                    it.Rgb = 0x38C7B8;
                    it.X = 0.3;
                    it.Y = 0.82;
                    it.W = 0.44;
                    it.H = 0.11;
                    break;
                default:
                    it.W = 0.3;
                    it.H = 0.2;
                    break;
            }
            SpanAt(it, 4);
            if (shape == "bar")
            {
                // 이름 막대: 막대 + 그 위의 글자
                var name = new VItem { Kind = "text", Text = "이름 · 소개", Size = 0.045, X = 0.32, Y = 0.82, Align = "left", Outline = 0, Rgb = 0xFFFFFF, Start = it.Start, End = it.End, In = "slide" };
                it.In = "slide";
                Change(null, delegate
                {
                    project.Items.Add(it);
                    project.Items.Add(name);
                });
                selected = name.Id;
                timeline.Selected = name.Id;
                SetTab("deco");
                timeline.InvalidateVisual();
                RefreshPanel(true);
                return;
            }
            AddItem(it);
        }

        void AddSticker(string id)
        {
            var it = new VItem { Kind = "sticker", Sticker = id, W = id.StartsWith("t_") ? 0.2 : 0.12, X = 0.8, Y = 0.2, Rgb = StickerRgb(id), In = "pop" };
            SpanAt(it, 3);
            AddItem(it);
        }

        // ══ 화면 비율 ══

        static readonly string[] AspectIds = { "source", "16:9", "9:16", "1:1", "4:5", "4:3", "3:4" };
        static readonly string[] AspectLabels = { "원본", "16:9 가로", "9:16 세로", "1:1", "4:5", "4:3", "3:4" };

        void PanelRatio()
        {
            Head("화면 비율", "릴스·쇼츠는 9:16, 유튜브는 16:9가 알맞아요. 화면을 끌면 자리가, 휠을 굴리면 크기가 바뀌어요.");
            Chips(AspectIds, AspectLabels, project.Aspect, v =>
            {
                Change(null, delegate { project.Aspect = v; });
                RefreshPanel();
            });
            Label("빈 곳");
            Chips(new[] { "blur", "black", "color" }, new[] { "흐린 배경", "검정", "색" }, project.Back, v =>
            {
                Change(null, delegate { project.Back = v; });
                RefreshPanel();
            });
            if (project.Back == "color") Swatches(Colors, project.BackRgb, v => Change(null, delegate { project.BackRgb = v; }));

            var c = CurrentClip;
            if (c == null) return;
            Label(ClipName(c));
            Chips(new[] { "fit", "fill" }, new[] { "전체 보이기", "꽉 채우기" }, c.Fit, v =>
            {
                SetClip(null, x => x.Fit = v);
                RefreshPanel();
            });
            double local, source, z = c.Zoom, px = c.PanX, py = c.PanY;
            if (LocalIn(c, out local, out source)) VAnim.ClipMotion(c, local, source, out z, out px, out py);
            var zs = SliderRow("크기", 0.2, 4, z, 0.01, Pct, v => SetClip("zoom", x =>
            {
                double l2, s2, z2, px2, py2;
                LocalIn(x, out l2, out s2);
                VAnim.ClipMotion(x, l2, s2, out z2, out px2, out py2);
                SetClipMotion(x, v, px2, py2);
            }));
            var xs = SliderRow("가로 자리", -1, 1, px, 0.01, Signed, v => SetClip("panx", x =>
            {
                double l2, s2, z2, px2, py2;
                LocalIn(x, out l2, out s2);
                VAnim.ClipMotion(x, l2, s2, out z2, out px2, out py2);
                SetClipMotion(x, z2, v, py2);
            }));
            var ys = SliderRow("세로 자리", -1, 1, py, 0.01, Signed, v => SetClip("pany", x =>
            {
                double l2, s2, z2, px2, py2;
                LocalIn(x, out l2, out s2);
                VAnim.ClipMotion(x, l2, s2, out z2, out px2, out py2);
                SetClipMotion(x, z2, px2, v);
            }));
            // 움직임 표시가 있으면 재생 위치를 옮길 때 값이 따라 바뀐다
            string motionId = c.Id;
            liveSliders.Add(delegate
            {
                var x = project.FindClip(motionId);
                double l2, s2, z2, px2, py2;
                if (x == null || x.Keys.Count == 0 || !LocalIn(x, out l2, out s2)) return;
                VAnim.ClipMotion(x, l2, s2, out z2, out px2, out py2);
                SetLive(zs, z2);
                SetLive(xs, px2);
                SetLive(ys, py2);
            });
            Buttons(new[] { "가운데로", "처음 크기" }, new Action[] { delegate
            {
                SetClip(null, x =>
                {
                    x.Keys.Clear();
                    x.PanX = x.PanY = 0;
                });
                RefreshPanel();
            }, delegate
            {
                SetClip(null, x =>
                {
                    x.Keys.Clear();
                    x.Zoom = 1;
                    x.PanX = x.PanY = 0;
                });
                RefreshPanel();
            } });
            ClipMotionSection(c);

            Label("돌리기·뒤집기");
            Buttons(new[] { "왼쪽 90°", "오른쪽 90°", "좌우", "상하" }, new Action[]
            {
                delegate { SetClip(null, x => x.Rotate = ((x.Rotate - 90) % 360 + 360) % 360); },
                delegate { SetClip(null, x => x.Rotate = (x.Rotate + 90) % 360); },
                delegate { SetClip(null, x => x.FlipH = !x.FlipH); },
                delegate { SetClip(null, x => x.FlipV = !x.FlipV); }
            });

            Label("가장자리 잘라 내기");
            SliderRow("왼쪽", 0, 0.45, c.CropX, 0.01, Pct, v => SetClip("cl", x =>
            {
                double right = 1 - x.CropX - x.CropW;
                x.CropX = v;
                x.CropW = Math.Max(0.05, 1 - v - right);
            }));
            SliderRow("오른쪽", 0, 0.45, 1 - c.CropX - c.CropW, 0.01, Pct, v => SetClip("cr", x => x.CropW = Math.Max(0.05, 1 - x.CropX - v)));
            SliderRow("위", 0, 0.45, c.CropY, 0.01, Pct, v => SetClip("ct", x =>
            {
                double bottom = 1 - x.CropY - x.CropH;
                x.CropY = v;
                x.CropH = Math.Max(0.05, 1 - v - bottom);
            }));
            SliderRow("아래", 0, 0.45, 1 - c.CropY - c.CropH, 0.01, Pct, v => SetClip("cb", x => x.CropH = Math.Max(0.05, 1 - x.CropY - v)));

            Act("이 놓기를 모든 조각에", "Line", delegate
            {
                string id = c.Id;
                Change(null, delegate
                {
                    var src = project.FindClip(id);
                    foreach (var x in project.Clips)
                    {
                        if (x == src) continue;
                        x.Fit = src.Fit;
                        x.Zoom = src.Zoom;
                        x.PanX = src.PanX;
                        x.PanY = src.PanY;
                        x.Keys.Clear();
                        x.Rotate = src.Rotate;
                        x.FlipH = src.FlipH;
                        x.FlipV = src.FlipV;
                        x.CropX = src.CropX;
                        x.CropY = src.CropY;
                        x.CropW = src.CropW;
                        x.CropH = src.CropH;
                    }
                });
                SetStatus("모든 조각을 같은 크기·자리로 맞췄어요.");
            });
        }

        // ══ 소리 ══

        VMicRecorder recorder;
        DispatcherTimer recTimer;
        double recAt;
        TextBlock recText;
        string sfxPick = "ding";

        void PanelSound()
        {
            Head("소리", "원본 소리를 다듬고, 배경 음악·내 목소리·효과음을 넣어요.");
            var s = SelectedSound;
            if (s != null)
            {
                Label(SoundTitle(s) + " · " + Clock(s.Start) + "부터 " + Sec(s.Length));
                MusicCreditNote(s);
                SliderRow("크기", 0, 2, s.Volume, 0.05, Pct, v => SetSound("vol", x => x.Volume = v));
                SliderRow("서서히 커지기", 0, 5, s.FadeIn, 0.1, Sec, v => SetSound("fi", x => x.FadeIn = v));
                SliderRow("서서히 작아지기", 0, 5, s.FadeOut, 0.1, Sec, v => SetSound("fo", x => x.FadeOut = v));
                if (s.IsMusic) Toggle("영상 끝까지 되풀이", null, s.Loop, v => SetSound(null, x => x.Loop = v));
                Buttons(new[] { "여기서 시작", "지우기" }, new Action[] { delegate
                {
                    double t = CurrentTime;
                    SetSound(null, x => x.Start = t);
                    RefreshPanel();
                }, DeleteSelected });
                Act("원본 소리 다듬기로", "Quiet", delegate { Select(null, false); });
                return;
            }

            var c = CurrentClip;
            if (c != null && c.Kind == "video")
            {
                Label(ClipName(c) + "의 소리");
                Toggle("소리 끄기", null, c.Mute, v =>
                {
                    SetClip(null, x => x.Mute = v);
                    RefreshPanel();
                });
                if (!c.Mute)
                {
                    SliderRow("크기", 0, 2, c.Volume, 0.05, Pct, v => SetClip("vol", x => x.Volume = v));
                    SliderRow("서서히 커지기", 0, 3, c.FadeIn, 0.1, Sec, v => SetClip("fi", x => x.FadeIn = v));
                    SliderRow("서서히 작아지기", 0, 3, c.FadeOut, 0.1, Sec, v => SetClip("fo", x => x.FadeOut = v));
                    SliderRow("잡음 줄이기", 0, 1, c.Denoise, 0.05, Pct, v => SetClip("dn", x => x.Denoise = v));
                    SliderRow("목소리 높낮이", -6, 6, c.Pitch, 1, v => (v > 0 ? "+" : "") + Math.Round(v), v => SetClip("pitch", x => x.Pitch = Math.Round(v)));
                    Hint("목소리 높낮이를 바꾸면 학생 목소리를 알아보기 어렵게 할 수 있어요. 잡음 줄이기·높낮이는 처음 한 번 계산하느라 잠시 걸려요.");
                }
            }

            Label("전체 소리");
            Toggle("말할 때 음악 줄이기", "목소리·자막 음성이 나올 때 배경 음악을 낮춰요", project.Duck, v =>
            {
                Change(null, delegate { project.Duck = v; });
                RefreshPanel();
            });
            if (project.Duck) SliderRow("줄일 때 음악 크기", 0.05, 0.8, project.DuckTo, 0.05, Pct, v => Change("duck", delegate { project.DuckTo = v; }));
            Toggle("소리 크기 맞추기", "조각마다 다른 소리 크기를 고르게 맞춰요", project.Level, v => Change(null, delegate { project.Level = v; }));
            Toggle("끝에서 서서히 끝내기", "마지막 1초 남짓 동안 화면은 어두워지고 소리는 작아져요", project.EndFade, v => Change(null, delegate { project.EndFade = v; }));

            Label("넣기");
            Buttons(new[] { "배경 음악", "소리 파일" }, new Action[] { delegate { AddSoundDialog(true); }, delegate { AddSoundDialog(false); } });
            PanelFreeMusic();

            Label("보면서 녹음하기");
            if (recorder == null)
            {
                Hint("누르면 그때 마이크를 켜요. 재생 위치부터 영상이 소리 없이 흐르고, 말한 목소리가 그 자리에 들어가요.");
                Act("녹음 시작", "Line", StartRecording);
            }
            else
            {
                recText = Theme.Text("녹음 중…", 14, FontWeights.Bold, Theme.Coral.DeepBrush);
                recText.Margin = new Thickness(0, 4, 0, 0);
                panel.Children.Add(recText);
                Act("녹음 끝내기", "Primary", delegate { StopRecording(true); });
                Act("녹음 버리기", "Quiet", delegate { StopRecording(false); });
            }

            Label("효과음");
            Chips(VSfx.All.Select(x => x[0]).ToArray(), VSfx.All.Select(x => x[1]).ToArray(), sfxPick, v => sfxPick = v);
            Buttons(new[] { "미리 듣기", "재생 위치에 넣기" }, new Action[] { PreviewSfx, delegate
            {
                string path;
                try
                {
                    path = VSfx.PathOf(sfxPick);
                }
                catch (Exception ex)
                {
                    Log.Write("효과음 만들기 실패: " + ex.GetType().Name);
                    SetStatus("효과음을 만들지 못했어요.");
                    return;
                }
                double t = CurrentTime;
                string label = VSfx.Label(sfxPick);
                double len = SoundSeconds(path);
                Change(null, delegate { project.Sounds.Add(new VSound { Kind = "effect", Path = path, Start = t, In = 0, Out = len, Volume = 0.8, Name = label }); });
                SetStatus("효과음 '" + label + "'을 넣었어요.");
                RefreshPanel();
            } });

            var list = project.Sounds.OrderBy(x => x.Start).ToList();
            if (list.Count > 0)
            {
                Label("넣은 소리 " + list.Count + "개");
                foreach (var x in list.Take(200))
                {
                    var xx = x;
                    ListRow(Clock(x.Start), SoundTitle(x), false, delegate
                    {
                        SeekTo(xx.Start);
                        Select(xx.Id, true);
                    });
                }
            }
        }

        static string SoundTitle(VSound s)
        {
            switch (s.Kind)
            {
                case "music": return "배경 음악" + (string.IsNullOrEmpty(s.Path) ? "" : " · " + Path.GetFileNameWithoutExtension(s.Path));
                case "record": return "내 목소리";
                case "voice": return "음성 · " + (s.Text ?? "");
                case "effect": return "효과음 · " + (s.Name ?? "");
            }
            return s.Name ?? (string.IsNullOrEmpty(s.Path) ? "소리" : Path.GetFileNameWithoutExtension(s.Path));
        }

        void AddSoundDialog(bool music)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = music ? "배경 음악 고르기" : "넣을 소리 고르기", Filter = "소리|*.mp3;*.m4a;*.wav;*.aac;*.wma;*.flac|모든 파일|*.*" };
            if (dlg.ShowDialog(this) != true) return;
            AddSoundFile(dlg.FileName, music);
        }

        /// 소리 파일 넣기: 배경 음악은 처음부터 끝까지 되풀이, 소리 파일은 재생 위치에
        void AddSoundFile(string file, bool music)
        {
            var pr = string.IsNullOrEmpty(file) || !File.Exists(file) ? null : VMedia.Probe(file);
            if (pr == null || !pr.HasAudio)
            {
                SetStatus("소리를 읽지 못했어요: " + Path.GetFileName(file ?? ""));
                return;
            }
            var s = music
                ? new VSound { Kind = "music", Path = file, Start = 0, In = 0, Out = pr.Seconds, Loop = true, Volume = 0.55, FadeOut = 1.5 }
                : new VSound { Kind = "file", Path = file, Start = CurrentTime, In = 0, Out = pr.Seconds, Volume = 0.9, Name = Path.GetFileNameWithoutExtension(file) };
            Change(null, delegate { project.Sounds.Add(s); });
            selected = s.Id;
            timeline.Selected = s.Id;
            var site = music ? VMusic.SourceOf(file) : null;
            SetStatus(music ? "배경 음악을 넣었어요. 말할 때는 음악이 저절로 줄어요." + (site != null && !site.NoCredit ? " 출처 밝히기를 잊지 마세요." : "") : "소리를 넣었어요.");
            RefreshPanel();
        }

        void PreviewSfx()
        {
            try
            {
                var player = new System.Media.SoundPlayer(VSfx.PathOf(sfxPick));
                player.Play();
            }
            catch (Exception ex)
            {
                Log.Write("효과음 미리 듣기 실패: " + ex.GetType().Name);
                SetStatus("효과음을 들려주지 못했어요.");
            }
        }

        void StartRecording()
        {
            if (recorder != null) return;
            var r = new VMicRecorder();
            try
            {
                r.Start();
            }
            catch (UserFacingException ex)
            {
                SetStatus(ex.Message);
                return;
            }
            recorder = r;
            recAt = CurrentTime;
            preview.MuteAudio = true;
            preview.Play(recAt, -1, false);
            Hook();
            UpdatePlayButton();
            recTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            recTimer.Tick += delegate
            {
                if (recorder == null || recText == null) return;
                int bars = (int)Math.Round(Math.Min(1, recorder.Level * 3) * 12);
                recText.Text = "녹음 중 " + Clock(CurrentTime - recAt) + "  " + new string('|', Math.Max(1, bars));
            };
            recTimer.Start();
            SetStatus("녹음 중이에요. 다 말했으면 [녹음 끝내기]를 누르세요.");
            RefreshPanel();
        }

        void StopRecording(bool keep)
        {
            var r = recorder;
            if (r == null) return;
            recorder = null;
            if (recTimer != null) recTimer.Stop();
            recTimer = null;
            r.Stop();
            preview.MuteAudio = muteBtn.IsChecked == true;
            if (preview.Playing)
            {
                preview.Pause();
                seekT = preview.Now;
                Unhook();
                ShowFrame();
                UpdatePlayButton();
            }
            if (!keep || r.Error != null || r.Seconds < 0.3 || r.Path == null || !File.Exists(r.Path))
            {
                SetStatus(r.Error ?? (keep ? "녹음된 소리가 너무 짧아요." : "녹음을 버렸어요."));
                if (!keep && r.Path != null) try { File.Delete(r.Path); } catch (Exception) { }
                RefreshPanel();
                return;
            }
            double at = recAt, secs = r.Seconds;
            string path = r.Path;
            Change(null, delegate { project.Sounds.Add(new VSound { Kind = "record", Path = path, Start = at, In = 0, Out = secs, Name = "내 목소리" }); });
            SetStatus("녹음한 목소리 " + Sec(secs) + "를 넣었어요.");
            RefreshPanel();
        }

        // ══ 색·효과 ══

        TextBlock stabText;

        void PanelLook()
        {
            Head("색·효과", "고른 조각의 색을 바꿔요. 위 '나란히 보기'를 켜면 왼쪽 반이 손대기 전 모습이에요.");
            var c = CurrentClip;
            if (c == null) return;
            Label(ClipName(c));
            Chips(new[] { "", "warm", "cool", "vivid", "soft", "gray", "old" }, new[] { "없음", "따뜻하게", "차갑게", "선명하게", "부드럽게", "흑백", "옛날 느낌" }, c.Filter ?? "", v => SetClip(null, x => x.Filter = v));
            SliderRow("밝기", -1, 1, c.Bright, 0.01, Signed, v => SetClip("br", x => x.Bright = v));
            SliderRow("대비", -1, 1, c.Contrast, 0.01, Signed, v => SetClip("ct", x => x.Contrast = v));
            SliderRow("채도", -1, 1, c.Sat, 0.01, Signed, v => SetClip("sa", x => x.Sat = v));
            SliderRow("색온도", -1, 1, c.Warm, 0.01, Signed, v => SetClip("wa", x => x.Warm = v));
            SliderRow("또렷하게", 0, 1, c.Sharp, 0.01, Pct, v => SetClip("sh", x => x.Sharp = v));
            Toggle("손대기 전과 나란히 보기", "화면 왼쪽 반이 손대기 전이에요", compareBtn.IsChecked == true, v => compareBtn.IsChecked = v);
            Buttons(new[] { "처음대로", "모든 조각에" }, new Action[] { delegate
            {
                SetClip(null, x =>
                {
                    x.Bright = x.Contrast = x.Sat = x.Warm = x.Sharp = 0;
                    x.Filter = "";
                });
                RefreshPanel();
            }, delegate
            {
                string id = c.Id;
                Change(null, delegate
                {
                    var src = project.FindClip(id);
                    foreach (var x in project.Clips)
                    {
                        x.Bright = src.Bright;
                        x.Contrast = src.Contrast;
                        x.Sat = src.Sat;
                        x.Warm = src.Warm;
                        x.Sharp = src.Sharp;
                        x.Filter = src.Filter;
                    }
                });
                SetStatus("모든 조각을 같은 색으로 맞췄어요.");
            } });
            if (c.Kind == "video")
            {
                Label("흔들림");
                Toggle("흔들림 줄이기", "손에 들고 찍어 흔들린 영상을 차분하게 해요. 가장자리가 조금 잘려요", c.Stabilize, v =>
                {
                    SetClip(null, x => x.Stabilize = v);
                    if (v) VStab.Ensure(c.Path, false, null);
                    RefreshPanel();
                });
                if (c.Stabilize)
                {
                    stabText = Theme.MetaLabel(StabLine(c.Path));
                    stabText.Margin = new Thickness(0, 4, 0, 0);
                    panel.Children.Add(stabText);
                    VStab.Ensure(c.Path, false, null);
                }
            }
        }

        static string StabLine(string path)
        {
            if (VStab.Ready(path)) return "흔들림을 다 쟀어요.";
            return "흔들림을 재는 중 " + Math.Round(VStab.Progress(path) * 100) + "% · 다 재면 미리 보기에 바로 나타나요.";
        }

        // ══ 가리기 ══

        void SetRedact(string key, Action<VRedact> act)
        {
            act(redactProto);
            var c = CurrentClip;
            if (c == null || selectedRedact == null) return;
            string cid = c.Id, rid = selectedRedact;
            Change(key == null ? null : key + rid, delegate
            {
                var cc = project.FindClip(cid);
                var r = cc == null ? null : cc.Redacts.FirstOrDefault(x => x.Id == rid);
                if (r != null) act(r);
            });
        }

        void PanelRedact()
        {
            Head("가리기", "화면에서 끌어 가릴 곳을 그려요. 얼굴은 [얼굴 찾아 가리기]로 한 번에 가릴 수 있어요.");
            var c = CurrentClip;
            if (c == null || c.Kind != "video")
            {
                NoteBox("가리기는 영상 조각에서 쓸 수 있어요. 재생 위치를 영상 조각으로 옮겨 주세요.");
                return;
            }
            var rd = c.Redacts.FirstOrDefault(x => x.Id == selectedRedact);
            var look = rd ?? redactProto;
            Label("가리는 방법");
            Chips(new[] { "blur", "mosaic", "fill" }, new[] { "흐리게", "모자이크", "색으로 덮기" }, look.Mode, v =>
            {
                SetRedact(null, x => x.Mode = v);
                RefreshPanel();
            });
            if (look.Mode == "fill") Swatches(new uint[] { 0x22252F, 0xFFFFFF, 0xFFD447, 0x38C7B8, 0xFF8473 }, look.Rgb, v => SetRedact(null, x => x.Rgb = v));
            else SliderRow("세기", 0.2, 1, look.Strength, 0.05, Pct, v => SetRedact("str", x => x.Strength = v));
            Toggle("동그랗게", null, look.Oval, v => SetRedact(null, x => x.Oval = v));

            Label("얼굴");
            Act("얼굴 찾아 가리기", "Primary", ScanFaces);
            Hint("이 조각에서 얼굴을 찾아 움직임을 따라 가려요. 이 PC 안에서만 찾고 어디로도 보내지 않아요. 가리지 않을 얼굴은 화면에서 두 번 누르거나 아래 목록에서 끄세요.");
            var faces = c.Redacts.Where(x => x.Face).OrderBy(x => x.Start).ToList();
            for (int i = 0; i < faces.Count; i++)
            {
                var f = faces[i];
                string rid = f.Id;
                ListRow(Clock(Math.Max(0, ToTimeline(c, f.Start))), "얼굴 " + (i + 1) + (f.Off ? " · 보이게 둠" : " · 가림") + " (" + Sec(f.End - f.Start) + ")", f.Id == selectedRedact, delegate
                {
                    double t = ToTimeline(c, f.Start + Math.Min(0.3, (f.End - f.Start) / 2));
                    SeekTo(t);
                    selectedRedact = rid;
                    RefreshPanel();
                    stage.InvalidateVisual();
                });
            }

            if (rd != null)
            {
                Label(rd.Face ? "고른 얼굴" : "고른 상자");
                if (rd.Face) Toggle("가리기", "끄면 이 얼굴은 보이게 둬요", !rd.Off, v =>
                {
                    SetRedact(null, x => x.Off = !v);
                    RefreshPanel();
                });
                Hint("원본 " + Clock(rd.Start) + " ~ " + Clock(rd.End) + " 동안 가려요." + (rd.Keys.Count > 0 ? " 움직임 표시 " + rd.Keys.Count + "개." : ""));
                Buttons(new[] { "여기부터", "여기까지", "처음부터 끝까지" }, new Action[] { delegate
                {
                    double l, s;
                    if (!LocalIn(c, out l, out s)) return;
                    SetRedact(null, x => x.Start = Math.Min(s, x.End - 0.1));
                    RefreshPanel();
                }, delegate
                {
                    double l, s;
                    if (!LocalIn(c, out l, out s)) return;
                    SetRedact(null, x => x.End = Math.Max(s, x.Start + 0.1));
                    RefreshPanel();
                }, delegate
                {
                    double a = c.In, b = c.Out;
                    SetRedact(null, x =>
                    {
                        x.Start = a;
                        x.End = b;
                    });
                    RefreshPanel();
                } });
                Buttons(new[] { "따라가기", "움직임 지우기", "지우기" }, new Action[] { TrackSelected, delegate
                {
                    SetRedact(null, x =>
                    {
                        if (x.Keys.Count == 0) return;
                        var r = VAnim.Redact(x, x.Start);
                        x.X = r.X;
                        x.Y = r.Y;
                        x.W = r.Width;
                        x.H = r.Height;
                        x.Keys.Clear();
                    });
                    RefreshPanel();
                }, DeleteSelected });
                Hint("[따라가기]는 지금 장면의 상자 안 모양을 앞으로 따라가며 상자를 옮겨요. 움직이는 사람·물건을 가릴 때 쓰세요.");
            }
            else Hint("화면을 끌어 상자를 그리면 이 조각 처음부터 끝까지 가려요. 상자를 누르면 고를 수 있어요.");

            Label("화면 전체");
            Act("이 조각 전체 흐리게", "Line", delegate
            {
                string cid = c.Id;
                var nr = new VRedact { Start = c.In, End = c.Out, X = 0, Y = 0, W = 1, H = 1, Mode = "blur", Strength = 0.85 };
                Change(null, delegate { project.FindClip(cid).Redacts.Add(nr); });
                selectedRedact = nr.Id;
                RefreshPanel();
            });
        }

        /// 조각의 원본 시각 → 전체 시간
        double ToTimeline(VClip c, double source)
        {
            double local;
            if (!c.LocalOf(source, out local)) local = source < c.In ? 0 : c.Length;
            return project.StartOf(c) + local;
        }

        // ══ 내보내기 ══

        List<VCheck> loudChecks;
        int loudFor = -1;
        bool loudRunning;

        void PanelExport()
        {
            var e = project.Export;
            Head("내보내기", "어디에 올릴지 고르면 크기·화질을 알맞게 맞춰요. 내보내는 동안에도 편집을 이어 갈 수 있어요.");
            Chips(VideoExport.Presets.Select(x => x[0]).ToArray(), VideoExport.Presets.Select(x => x[1]).ToArray(), e.Preset, v =>
            {
                Change(null, delegate { project.Export.Preset = v; });
                RefreshPanel();
            });
            switch (e.Preset)
            {
                case "same":
                    Label("크기");
                    Chips(new[] { "0", "1080", "720", "480" }, new[] { "원본", "1080p", "720p", "480p" }, e.Height.ToString(), v => Change(null, delegate { project.Export.Height = int.Parse(v); }));
                    Label("초당 장 수");
                    Chips(new[] { "0", "30", "60" }, new[] { "원본", "30", "60" }, ((int)e.Fps).ToString(), v => Change(null, delegate { project.Export.Fps = int.Parse(v); }));
                    break;
                case "youtube":
                    Label("크기");
                    Chips(new[] { "720", "1080", "1440", "2160" }, new[] { "720p", "1080p", "1440p", "4K" }, (e.Height > 0 ? e.Height : 1080).ToString(), v => Change(null, delegate { project.Export.Height = int.Parse(v); }));
                    break;
                case "size":
                    Label("목표 용량");
                    Chips(new[] { "10", "25", "50", "100", "300" }, new[] { "10MB", "25MB", "50MB", "100MB", "300MB" }, Math.Round(e.TargetMb).ToString(), v =>
                    {
                        Change(null, delegate { project.Export.TargetMb = int.Parse(v); });
                        RefreshPanel();
                    });
                    Hint("메일·학교 게시판처럼 올릴 수 있는 크기가 정해진 곳에 맞춰요.");
                    break;
                case "kakao":
                    Hint("카카오톡에서 잘 열리게 720p·초당 30장 이하로 가볍게 만들어요.");
                    break;
                case "reels":
                case "shorts":
                    Hint("세로 1080×1920으로 만들어요. 화면의 빨간 곳은 앱 단추·글에 가려지는 곳이에요.");
                    break;
                case "audio":
                    Label("소리 형식");
                    Chips(new[] { "m4a", "mp3", "wav" }, new[] { "M4A", "MP3", "WAV" }, e.AudioFormat, v => Change(null, delegate { project.Export.AudioFormat = v; }));
                    break;
                case "cover":
                    Hint("지금 보이는 장면(얹은 글자·그림까지)을 그림으로 저장해요. 유튜브 표지·학급 게시물에 쓸 수 있어요.");
                    Act("지금 장면을 표지로", "Line", delegate
                    {
                        double t = CurrentTime;
                        Change(null, delegate { project.Export.CoverAt = t; });
                        RefreshPanel();
                    });
                    Hint("표지 장면: " + VProject.Clock(e.CoverAt, true));
                    break;
            }

            if (VideoExport.HasQuality(e.Preset))
            {
                Label("화질");
                string cur = VideoExport.QualityId(e.Quality);
                Chips(VideoExport.Qualities.Select(x => x[0]).ToArray(), VideoExport.Qualities.Select(x => x[1]).ToArray(), cur, v =>
                {
                    Change(null, delegate { project.Export.Quality = v; });
                    RefreshPanel();
                });
                QualityHints(cur);
            }

            if (e.Preset != "cover")
            {
                Label("구간");
                Toggle("고른 구간만 내보내기", HasRange ? Clock(timeline.RangeA) + " ~ " + Clock(timeline.RangeB) : "시간 막대에서 Shift를 누른 채 끌어 구간을 고르세요", e.RangeOnly && HasRange, v =>
                {
                    double a = timeline.RangeA, b = timeline.RangeB;
                    bool ok = HasRange;
                    Change(null, delegate
                    {
                        project.Export.RangeOnly = v && ok;
                        if (ok)
                        {
                            project.Export.RangeStart = a;
                            project.Export.RangeEnd = b;
                        }
                    });
                    RefreshPanel();
                });
                if (e.RangeOnly && HasRange && (Math.Abs(e.RangeStart - timeline.RangeA) > 0.01 || Math.Abs(e.RangeEnd - timeline.RangeB) > 0.01))
                {
                    project.Export.RangeStart = timeline.RangeA;
                    project.Export.RangeEnd = timeline.RangeB;
                }
                if (e.RangeOnly && !HasRange) project.Export.RangeOnly = false;
            }

            VExportPlan plan;
            List<VCheck> checks;
            try
            {
                plan = VideoExport.Plan(project);
                checks = VideoExport.Checks(project, plan);
            }
            catch (Exception ex)
            {
                Log.Write("내보내기 계획 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                NoteBox("내보낼 준비를 하지 못했어요. (" + ex.GetType().Name + ")");
                return;
            }
            Label("점검");
            if (plan.Kind != "cover")
            {
                if (loudFor == version && loudChecks != null) checks.AddRange(loudChecks);
                else
                {
                    checks.Add(new VCheck { Level = "info", Text = "소리 크기를 재는 중이에요…" });
                    StartLoudness();
                }
            }
            var order = new Dictionary<string, int> { { "stop", 0 }, { "warn", 1 }, { "info", 2 } };
            foreach (var ck in checks.OrderBy(x => order.ContainsKey(x.Level) ? order[x.Level] : 3)) CheckRow(ck);
            var go = Act("이대로 내보내기", "Primary", StartExport);
            go.IsEnabled = !checks.Any(x => x.Level == "stop");
            Hint("만들 파일: " + Path.GetFileName(plan.Output) + " (원본과 같은 폴더. 원본은 그대로예요)");
        }

        /// 화질 칩 아래: 고른 화질이 어떤지 쉬운 말 한 줄 + 화질마다 예상 크기
        void QualityHints(string cur)
        {
            switch (cur)
            {
                case "max": Hint("최고: 원본에 가장 가깝게 만들어요(원본보다 좋아지지는 않아요). 파일이 가장 커요."); break;
                case "high": Hint("높음: 칠판 글씨·작은 무늬까지 또렷해요. 파일이 중간보다 커요."); break;
                case "low": Hint("낮음: 파일이 가장 작아요. 빠르게 움직이는 장면은 조금 뭉개질 수 있어요."); break;
                default: Hint("중간: 보통은 이걸로 충분해요."); break;
            }
            try
            {
                // '최고 약 1.8MB'가 줄 끝에서 쪼개지지 않게 그 안의 빈칸은 붙는 빈칸(U+00A0)
                var sizes = VideoExport.Qualities.Select(q => (q[1] + " 약 " + Conversions.Size(VideoExport.Plan(project, q[0]).EstimatedBytes)).Replace(' ', ' ')).ToList();
                Hint("예상 크기: " + string.Join(" · ", sizes));
            }
            catch (Exception ex)
            {
                Log.Write("화질별 예상 크기 실패: " + ex.GetType().Name);
            }
        }

        void CheckRow(VCheck ck)
        {
            Hue hue = ck.Level == "stop" ? Theme.Coral : ck.Level == "warn" ? Theme.Yellow : Theme.Teal;
            var stack = new StackPanel();
            var t = Theme.Text(ck.Text, 13.5, ck.Level == "info" ? FontWeights.Normal : FontWeights.Bold, Theme.Ink);
            t.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(t);
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (ck.T >= 0)
            {
                double at = ck.T;
                row.Children.Add(SmallButton("그 자리로", delegate { SeekTo(at + 0.01); }));
            }
            if (ck.Fix != null)
            {
                string fix = ck.Fix;
                row.Children.Add(SmallButton(FixLabel(fix), delegate { ApplyFix(fix); }));
            }
            if (row.Children.Count > 0) stack.Children.Add(row);
            panel.Children.Add(new Border { Background = hue.TintBrush, CornerRadius = new CornerRadius(13), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 6), Child = stack });
        }

        static string FixLabel(string fix)
        {
            switch (fix)
            {
                case "relink": return "파일 찾기";
                case "level": return "크기 맞추기 켜기";
                case "aspect916": return "9:16으로 바꾸기";
                case "subtitle_slow": return "자막 늘리기";
                case "voice_stale": return "바뀐 자막만 다시 만들기";
            }
            return "고치기";
        }

        void ApplyFix(string fix)
        {
            switch (fix)
            {
                case "relink":
                    {
                        var copy = project.Clone();
                        var fixedP = Relink(copy);
                        if (fixedP == null)
                        {
                            SetStatus("원본을 찾지 못해 그대로 두었어요.");
                            return;
                        }
                        string json = fixedP.ToJson();
                        Change(null, delegate { project = VProject.FromJson(json); });
                        foreach (var f in project.Files()) VMedia.Forget(f);
                        break;
                    }
                case "level":
                    Change(null, delegate { project.Level = true; });
                    break;
                case "aspect916":
                    Change(null, delegate { project.Aspect = "9:16"; });
                    break;
                case "subtitle_slow":
                    {
                        int n = 0;
                        Change(null, delegate
                        {
                            double d = project.Duration;
                            var subs = project.Items.Where(i => i.Subtitle).OrderBy(i => i.Start).ToList();
                            for (int i = 0; i < subs.Count; i++)
                            {
                                var s = subs[i];
                                int chars = (s.Text ?? "").Replace(" ", "").Replace("\n", "").Length;
                                double need = Math.Max(0.8, chars / 12.0);
                                if (s.Length >= need - 0.01) continue;
                                double limit = i + 1 < subs.Count ? subs[i + 1].Start : d;
                                double end = Math.Min(limit, s.Start + need);
                                if (end > s.End + 0.01)
                                {
                                    s.End = end;
                                    n++;
                                }
                            }
                        });
                        SetStatus(n > 0 ? n + "개 자막을 읽을 만큼 늘렸어요. 다음 자막에 막혀 못 늘린 것은 자막을 나눠 주세요." : "다음 자막에 막혀 늘리지 못했어요. 자막을 둘로 나누거나 글을 줄여 주세요.");
                        break;
                    }
                case "voice_stale":
                    // 음성으로 바꾼 뒤 글을 고친 자막만, 전에 쓴 목소리·빠르기로 다시 만든다(끝나면 칸을 다시 그린다)
                    SubtitlesToVoice(project.StaleVoiceItems().Select(i => i.Id).ToList(), true);
                    break;
            }
            RefreshPanel();
        }

        /// 소리 크기 점검은 시간이 걸려서 뒤에서 한다(내용이 바뀌면 결과를 버린다)
        void StartLoudness()
        {
            if (loudRunning) return;
            loudRunning = true;
            int ver = version;
            var snap = project.Clone();
            var th = new Thread(delegate ()
            {
                List<VCheck> got = null;
                try
                {
                    got = VideoExport.LoudnessChecks(snap, () => version != ver || !IsVisibleSafe());
                }
                catch (Exception ex)
                {
                    Log.Write("소리 크기 점검 실패: " + ex.GetType().Name);
                    got = new List<VCheck>();
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    loudRunning = false;
                    if (version != ver) return;
                    loudChecks = got;
                    loudFor = ver;
                    if (tab == "export") RefreshPanel();
                }));
            }) { IsBackground = true, Name = "소리 크기 점검", Priority = ThreadPriority.BelowNormal };
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
        }

        volatile bool windowOpen = true;

        bool IsVisibleSafe()
        {
            return windowOpen;
        }

        void StartExport()
        {
            VExportPlan plan;
            List<VCheck> checks;
            try
            {
                plan = VideoExport.Plan(project);
                checks = VideoExport.Checks(project, plan);
            }
            catch (Exception ex)
            {
                Log.Write("내보내기 계획 실패: " + ex.GetType().Name);
                SetStatus("내보낼 준비를 하지 못했어요.");
                return;
            }
            var stop = checks.FirstOrDefault(x => x.Level == "stop");
            if (stop != null)
            {
                Dialog.Ask(this, H, "export", "아직 내보낼 수 없어요", stop.Text, null, "확인");
                return;
            }
            if (File.Exists(plan.Output)) plan.Output = Util.UniquePath(plan.Output);
            if (demo) return;
            var files = project.Opened.Where(File.Exists).ToList();
            if (files.Count == 0) files = project.Files().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(1).ToList();
            var job = new Job(action, new ActionOption("vexport", "영상 편집 내보내기", ""), files) { Payload = new VExportPayload { Project = project.Clone(), Plan = plan } };
            Jobs.Start(job);
            changedSinceExport = false;
            AutoSave();
            SetStatus("'" + Path.GetFileName(plan.Output) + "' 만들기를 시작했어요. 알림 창에서 진행을 볼 수 있고, 그동안 편집을 이어 가도 돼요.");
        }

        // ══ 다른 영상에도 똑같이 ══

        void ApplyToOtherFiles()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "똑같이 손질할 다른 영상 고르기", Multiselect = true, Filter = "영상|*.mp4;*.mov;*.m4v;*.avi;*.wmv;*.3gp|모든 파일|*.*" };
            string first = project.Opened.FirstOrDefault(File.Exists);
            if (first != null) dlg.InitialDirectory = Path.GetDirectoryName(first);
            if (dlg.ShowDialog(this) != true) return;
            var others = dlg.FileNames.Where(f => !project.Opened.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            if (others.Count == 0) return;
            var body = "고른 영상 " + others.Count + "개에 지금의 화면 비율·색·소리 다듬기와, 처음부터 끝까지 얹은 로고·글자, 배경 음악, 내보내기 설정(" + VideoExport.PresetLabel(project.Export.Preset) + ")을 똑같이 넣어 따로따로 만들어요. 자막·자르기·가리기는 영상마다 달라서 옮기지 않아요.";
            if (Dialog.Ask(this, H, "copy", "다른 영상에도 똑같이", body, null, "그만두기", "만들기") != 1) return;
            var tmpl = project.Clone();
            var look = tmpl.Clips.FirstOrDefault(c => c.Kind == "video") ?? tmpl.Clips[0];
            double dur0 = tmpl.Duration;
            int started = 0;
            var failed = new List<string>();
            var tooBig = new List<string>();
            foreach (var f in others)
            {
                VProject p;
                try
                {
                    p = Build(new List<string> { Path.GetFullPath(f) });
                }
                catch (UserFacingException)
                {
                    failed.Add(Path.GetFileName(f));
                    continue;
                }
                p.Aspect = tmpl.Aspect;
                p.Back = tmpl.Back;
                p.BackRgb = tmpl.BackRgb;
                p.Duck = tmpl.Duck;
                p.DuckTo = tmpl.DuckTo;
                p.Level = tmpl.Level;
                p.EndFade = tmpl.EndFade;
                var e = VProject.FromJson(tmpl.ToJson()).Export;
                e.RangeOnly = false;
                p.Export = e;
                foreach (var c in p.Clips)
                {
                    c.TakeLookFrom(look);
                    c.Keys.Clear();
                    c.CropX = look.CropX;
                    c.CropY = look.CropY;
                    c.CropW = look.CropW;
                    c.CropH = look.CropH;
                }
                double dur = p.Duration;
                foreach (var it in tmpl.Items)
                {
                    if (it.Subtitle || it.Start > 0.05 || it.End < dur0 - 0.05) continue;
                    var x = it.Copy();
                    x.Id = VProject.NewId();
                    x.Start = 0;
                    x.End = dur;
                    if (x.Keys.Count > 0) x.Keys.Clear();
                    p.Items.Add(x);
                }
                foreach (var s in tmpl.Sounds.Where(s => s.IsMusic))
                {
                    var x = s.Copy();
                    x.Id = VProject.NewId();
                    p.Sounds.Add(x);
                }
                var plan = VideoExport.Plan(p);
                var stops = VideoExport.Checks(p, plan).Where(x => x.Level == "stop").ToList();
                if (stops.Count > 0)
                {
                    // 길어서 목표 용량에 맞지 않는 영상은 읽지 못한 영상과 나눠 알린다
                    (stops.Count == 1 && VideoExport.TooBig(p, plan) ? tooBig : failed).Add(Path.GetFileName(f));
                    continue;
                }
                var job = new Job(action, new ActionOption("vexport", "영상 편집 내보내기", ""), new List<string> { f }) { Payload = new VExportPayload { Project = p, Plan = plan } };
                Jobs.Start(job);
                started++;
            }
            SetStatus(started + "개 영상 만들기를 시작했어요." + (failed.Count > 0 ? " 읽지 못한 영상: " + string.Join(", ", failed) + "." : "")
                + (tooBig.Count > 0 ? " 용량에 맞지 않아 만들지 않은 영상: " + string.Join(", ", tooBig) + "." : ""));
        }
    }
}
