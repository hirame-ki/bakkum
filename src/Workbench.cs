// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 메뉴에서 고른 일 가운데 창이 필요한 일(사진 손질·쪽 편집·콜라주)을 여는 곳과, 시작 전에 몇 가지 물어보는 곳.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Bakkum
{
    public static class Workbench
    {
        /// 창을 열었으면 true(작업은 창에서 '저장'을 누를 때 시작된다).
        public static bool Open(MenuAction a, ActionOption o, List<string> files)
        {
            var usable = a.Usable(files);
            if (usable.Count == 0) return false;
            if (a.Id == "photo")
            {
                switch (o.Id)
                {
                    case "edit":
                    case "face":
                    case "scan":
                        Show(new PhotoEditor(a, usable, o.Id));
                        return true;
                    case "collage":
                        Show(new CollageWindow(a, usable, false));
                        return true;
                }
            }
            if (a.Id == "pdftool" && o.Id == "pages")
            {
                Show(new PdfPagesWindow(a, usable, false));
                return true;
            }
            return false;
        }

        static void Show(Window w)
        {
            w.Show();
            w.Activate();
        }
    }

    /// 작업을 시작하기 전에 더 물어볼 것(암호, 목표 용량 등). 취소하면 false.
    public static class Prompts
    {
        static readonly Hue PdfHue = Theme.Pink;

        public static bool Ask(Job job)
        {
            switch (job.Action.Id + "/" + job.Option.Id)
            {
                case "shrink/target": return AskTarget(job);
                case "pdftool/lock": return AskLock(job);
                case "pdftool/unlock": return AskUnlock(job);
                case "pdftool/pick": return AskPick(job);
                case "pdf/merge":
                case "pdftool/merge":
                    return AskOrder(job);
                case "pdftool/split": return AskSplit(job);
                case "media/trim": return AskTrim(job);
                case "media/gif": return AskGif(job);
                case "media/compress": return AskMediaShrink(job);
                case "pdftool/watermark": return AskWatermark(job);
                case "pdftool/number": return AskNumber(job);
                case "ai/mc":
                case "ai/ox":
                case "ai/essay":
                case "ai/mix":
                    return AskQuiz(job);
                case "ai/worksheet":
                case "ai/levels":
                case "ai/rubric":
                case "ai/similar":
                    return AskOutput(job);
                case "ai/lesson":
                    return AskLesson(job);
                case "ai/reply":
                case "ai/planning":
                case "ai/report":
                case "ai/letter":
                    return AskDraft(job);
                case "ai/slides":
                    return AskDeck(job);
            }
            return true;
        }

        /// 보기용(그림 확인): 물어보는 창을 띄우지 않고 만들어 돌려준다.
        public static Window Preview(Job job)
        {
            previewing = true;
            preview = null;
            try { Ask(job); }
            finally { previewing = false; }
            return preview;
        }

        static bool previewing;
        static Window preview;

        internal static TextBlock Caption(string s)
        {
            var t = Theme.Text(s, 13, FontWeights.Bold, Theme.Ink2);
            t.Margin = new Thickness(0, 12, 0, 6);
            return t;
        }

        static TextBox Field(string text, string hint)
        {
            return new TextBox { Style = Theme.GetStyle("TextField"), Text = text, Height = 44, Tag = hint };
        }

        static PasswordBox Password()
        {
            return new PasswordBox { Style = Theme.GetStyle("Field"), Height = 44 };
        }

        static TextBlock ErrorLine()
        {
            var t = Theme.Text("", 13.5, FontWeights.Bold, Theme.Coral.DeepBrush);
            t.Margin = new Thickness(0, 8, 0, 0);
            return t;
        }

        /// 칩 묶음. 고른 값은 store에 넣는다.
        internal static WrapPanel Chips(Hue hue, string[] ids, string[] labels, string current, Action<string> store)
        {
            var wrap = new WrapPanel();
            string group = "p" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                var r = new RadioButton { Style = Theme.GetStyle("Chip"), Tag = hue, GroupName = group, Content = labels[i], MinHeight = 38, FontSize = 13.5, Margin = new Thickness(0, 0, 6, 6), IsChecked = id == current };
                r.Checked += delegate { store(id); };
                wrap.Children.Add(r);
            }
            return wrap;
        }

        /// 대화 창을 띄우고 '맞는 값이 될 때까지' 묻는다. check가 오류 문장을 돌려주면 다시 묻는다.
        static bool Loop(Func<FrameworkElement> build, Func<string> check, TextBlock error, Hue hue, string glyph, string title, string body, string ok)
        {
            var extra = build();
            if (previewing)
            {
                preview = Dialog.Build(null, hue, glyph, title, body, extra, "취소", ok);
                return false;
            }
            while (true)
            {
                var host = extra.Parent as Panel;
                if (host != null) host.Children.Remove(extra);
                var border = extra.Parent as Border;
                if (border != null) border.Child = null;
                if (Dialog.Ask(null, hue, glyph, title, body, extra, "취소", ok) != 1) return false;
                string e = check();
                if (e == null) return true;
                error.Text = Theme.Keep(e);
            }
        }

        static bool AskTarget(Job job)
        {
            var box = Field("10", "숫자");
            var error = ErrorLine();
            var stack = new StackPanel();
            stack.Children.Add(Caption("목표 용량(MB)"));
            stack.Children.Add(box);
            stack.Children.Add(error);
            var note = Theme.MetaLabel("사진을 조금씩 더 줄여서 맞춰요. 글자가 많은 PDF는 목표까지 줄지 않을 수 있어요.");
            note.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(note);
            return Loop(() => stack, () =>
            {
                double v;
                if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v <= 0) return "숫자로 적어 주세요. 예: 10";
                job.Params["mb"] = v.ToString(CultureInfo.InvariantCulture);
                return null;
            }, error, Theme.HueOf("shrink"), "shrink", "몇 MB 이하로 줄일까요?", null, "줄이기");
        }

        static bool AskLock(Job job)
        {
            var a = Password();
            var b = Password();
            var error = ErrorLine();
            var stack = new StackPanel();
            stack.Children.Add(Caption("암호"));
            stack.Children.Add(a);
            stack.Children.Add(Caption("한 번 더"));
            stack.Children.Add(b);
            stack.Children.Add(error);
            var warn = Theme.Warning("암호를 잊으면 아무도 열 수 없어요. 암호는 어디에도 저장하지 않아요.");
            warn.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(warn);
            return Loop(() => stack, () =>
            {
                if (a.Password.Length == 0) return "암호를 넣어 주세요.";
                if (a.Password != b.Password) return "두 칸의 암호가 달라요.";
                job.Params["password"] = a.Password;
                return null;
            }, error, PdfHue, "lock", "열 때 쓸 암호를 정해 주세요", null, "암호 걸기");
        }

        static bool AskUnlock(Job job)
        {
            var a = Password();
            var stack = new StackPanel();
            stack.Children.Add(Caption("지금 걸려 있는 암호"));
            stack.Children.Add(a);
            var error = ErrorLine();
            stack.Children.Add(error);
            return Loop(() => stack, () =>
            {
                job.Params["password"] = a.Password;
                return null;
            }, error, PdfHue, "lock", "암호를 넣어 주세요", "암호를 아는 PDF만 풀 수 있어요.", "암호 풀기");
        }

        static bool AskPick(Job job)
        {
            var box = Field("", "예: 1,3-5");
            var error = ErrorLine();
            var stack = new StackPanel();
            stack.Children.Add(Caption("남길 쪽"));
            stack.Children.Add(box);
            stack.Children.Add(error);
            var note = Theme.MetaLabel("쉼표로 나누고, 이어진 쪽은 -로 적어요. '8-'은 8쪽부터 끝까지, '5-1'은 거꾸로예요.");
            note.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(note);
            stack.Children.Add(Caption("저장 형식"));
            job.Params["as"] = "pdf";
            stack.Children.Add(Chips(PdfHue, new[] { "pdf", "png", "jpg", "docx", "txt", "pptx", "xlsx" }, new[] { "PDF", "PNG", "JPG", "워드", "글자", "PPT", "엑셀" }, "pdf", v => job.Params["as"] = v));
            return Loop(() => stack, () =>
            {
                if (box.Text.Trim().Length == 0) return "쪽 번호를 적어 주세요. 예: 1,3-5";
                job.Params["pages"] = box.Text.Trim();
                return null;
            }, error, PdfHue, "pdftool", "어느 쪽을 남길까요?", null, "만들기");
        }

        static double LengthOf(Job job)
        {
            var files = job.Action.Usable(job.Files);
            if (files.Count != 1) return 0;
            try
            {
                return MediaProbe.Read(files[0]).Seconds;
            }
            catch (Exception ex)
            {
                Log.Write("길이를 읽지 못함: " + ex.GetType().Name);
                return 0;
            }
        }

        /// 시작·끝 칸 둘. 넓이를 나눠 나란히 둔다.
        static UIElement TimeFields(TextBox from, TextBox to)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel();
            left.Children.Add(Caption("시작"));
            left.Children.Add(from);
            var right = new StackPanel();
            right.Children.Add(Caption("끝"));
            right.Children.Add(to);
            Grid.SetColumn(right, 2);
            grid.Children.Add(left);
            grid.Children.Add(right);
            return grid;
        }

        /// 맞으면 null, 아니면 오류 문장. 맞으면 job에 적어 둔다.
        static string StoreRange(Job job, TextBox from, TextBox to, double length, bool needOne)
        {
            string a = from.Text.Trim(), b = to.Text.Trim();
            double start = 0, end = length;
            if (a.Length > 0 && !MediaJobs.ParseTime(a, out start)) return "시작 시간을 알아보지 못했어요. 90, 1:30, 01:02:03처럼 적어 주세요.";
            if (b.Length > 0 && !MediaJobs.ParseTime(b, out end)) return "끝 시간을 알아보지 못했어요. 90, 1:30, 01:02:03처럼 적어 주세요.";
            if (needOne && a.Length == 0 && b.Length == 0) return "시작이나 끝 가운데 하나는 적어 주세요.";
            if (b.Length > 0 && end <= start) return "끝이 시작보다 뒤여야 해요.";
            if (length > 0 && start >= length) return "시작이 전체 길이(" + MediaJobs.TimeText(length) + ")보다 뒤예요.";
            job.Params["start"] = a;
            job.Params["end"] = b;
            return null;
        }

        static bool AskTrim(Job job)
        {
            var hue = Theme.HueOf("media");
            var from = Field("", "처음부터");
            var to = Field("", "끝까지");
            var error = ErrorLine();
            double length = LengthOf(job);
            var stack = new StackPanel();
            stack.Children.Add(TimeFields(from, to));
            stack.Children.Add(error);
            var note = Theme.MetaLabel("90, 1:30, 01:02:03처럼 적어요. 비워 두면 처음이나 끝까지예요.");
            note.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(note);
            return Loop(() => stack, () => StoreRange(job, from, to, length, true), error, hue, "media", "어디부터 어디까지 남길까요?", length > 0 ? "전체 길이 " + MediaJobs.TimeText(length) : null, "자르기");
        }

        static bool AskGif(Job job)
        {
            var hue = Theme.HueOf("media");
            var from = Field("", "처음부터");
            var to = Field("", "끝까지");
            var error = ErrorLine();
            double length = LengthOf(job);
            var stack = new StackPanel();
            stack.Children.Add(TimeFields(from, to));
            stack.Children.Add(error);
            var note = Theme.MetaLabel("GIF는 " + (int)MediaJobs.MaxGifSeconds + "초까지 만들어요. 짧을수록 파일이 작아요.");
            note.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(note);
            job.Params["width"] = "480";
            job.Params["fps"] = "12";
            stack.Children.Add(Caption("크기"));
            stack.Children.Add(Chips(hue, new[] { "320", "480", "640" }, new[] { "작게", "보통", "크게" }, "480", v => job.Params["width"] = v));
            stack.Children.Add(Caption("움직임"));
            stack.Children.Add(Chips(hue, new[] { "8", "12", "15" }, new[] { "가볍게", "보통", "부드럽게" }, "12", v => job.Params["fps"] = v));
            return Loop(() => stack, () => StoreRange(job, from, to, length, false), error, hue, "media", "GIF로 만들까요?", length > 0 ? "전체 길이 " + MediaJobs.TimeText(length) : null, "만들기");
        }

        static bool AskMediaShrink(Job job)
        {
            var hue = Theme.HueOf("media");
            var error = ErrorLine();
            job.Params["level"] = "mid";
            var stack = new StackPanel();
            stack.Children.Add(Chips(hue, new[] { "soft", "mid", "hard" }, new[] { "조금", "보통", "많이" }, "mid", v => job.Params["level"] = v));
            var note = Theme.MetaLabel("많이 줄일수록 글자가 흐려질 수 있어요. 원본은 그대로 남아요.");
            note.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(note);
            return Loop(() => stack, () => null, error, hue, "media", "얼마나 줄일까요?", null, "줄이기");
        }

        /// 합치기 전에 순서를 정한다. 놓은 차례(이름 순서)로 먼저 보여 준다.
        static bool AskOrder(Job job)
        {
            var usable = job.Action.Usable(job.Files);
            if (usable.Count < 2) return true;
            var hue = Theme.HueOf(job.Action.Id);
            var order = usable.ToList();
            var list = new StackPanel();
            Action redraw = null;
            Func<string, bool, Action, Button> arrow = delegate (string glyph, bool on, Action act)
            {
                var b = new Button { Style = Theme.GetStyle("Quiet"), Content = Theme.Glyph(glyph, 15, Theme.Ink2, 2), Width = 34, Height = 34, Padding = new Thickness(0), IsEnabled = on, Opacity = on ? 1 : 0.3 };
                b.Click += delegate { act(); };
                return b;
            };
            redraw = delegate
            {
                list.Children.Clear();
                for (int i = 0; i < order.Count; i++)
                {
                    int at = i;
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), LastChildFill = true };
                    var number = Theme.Text((i + 1).ToString(), 13, FontWeights.Bold, hue.DeepBrush);
                    number.Width = 26;
                    number.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(number, Dock.Left);
                    row.Children.Add(number);
                    var down = arrow("chevdown", i < order.Count - 1, delegate { string t = order[at]; order[at] = order[at + 1]; order[at + 1] = t; redraw(); });
                    DockPanel.SetDock(down, Dock.Right);
                    row.Children.Add(down);
                    var up = arrow("up", i > 0, delegate { string t = order[at]; order[at] = order[at - 1]; order[at - 1] = t; redraw(); });
                    DockPanel.SetDock(up, Dock.Right);
                    row.Children.Add(up);
                    var name = Theme.Text(Util.ShortName(order[i]), 13.5, FontWeights.Normal, Theme.Ink);
                    name.VerticalAlignment = VerticalAlignment.Center;
                    name.TextTrimming = TextTrimming.CharacterEllipsis;
                    row.Children.Add(name);
                    list.Children.Add(row);
                }
            };
            redraw();
            var scroller = new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var error = ErrorLine();
            return Loop(() => scroller, () =>
            {
                var rest = job.Files.Where(f => !usable.Contains(f)).ToList();
                job.Files.Clear();
                job.Files.AddRange(order);
                job.Files.AddRange(rest);
                return null;
            }, error, hue, job.Action.Id, "이 순서로 합칠까요?", "화살표로 옮겨 순서를 정해요.", "합치기");
        }

        static bool AskSplit(Job job)
        {
            var box = Field("1", "숫자");
            var error = ErrorLine();
            var stack = new StackPanel();
            stack.Children.Add(Caption("한 파일에 몇 쪽씩"));
            stack.Children.Add(box);
            stack.Children.Add(error);
            var note = Theme.MetaLabel("1이면 한 쪽씩, 5면 다섯 쪽씩 나눠요. 마지막 파일은 쪽이 더 적을 수 있어요.");
            note.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(note);
            return Loop(() => stack, () =>
            {
                int v;
                if (!int.TryParse(box.Text.Trim(), out v) || v < 1) return "1 이상의 숫자로 적어 주세요.";
                job.Params["every"] = v.ToString();
                return null;
            }, error, PdfHue, "pdftool", "몇 쪽씩 나눌까요?", null, "나누기");
        }

        static bool AskWatermark(Job job)
        {
            var box = Field("", "예: 복사 금지");
            var error = ErrorLine();
            string mode = "diagonal", opacity = "0.2";
            var stack = new StackPanel();
            stack.Children.Add(Caption("넣을 글자"));
            stack.Children.Add(box);
            stack.Children.Add(error);
            stack.Children.Add(Caption("놓는 자리"));
            stack.Children.Add(Chips(PdfHue, new[] { "diagonal", "center", "tile" }, new[] { "대각선", "가운데", "가득" }, mode, v => mode = v));
            stack.Children.Add(Caption("진하기"));
            stack.Children.Add(Chips(PdfHue, new[] { "0.12", "0.2", "0.35" }, new[] { "연하게", "보통", "진하게" }, opacity, v => opacity = v));
            return Loop(() => stack, () =>
            {
                if (box.Text.Trim().Length == 0) return "넣을 글자를 적어 주세요.";
                job.Params["text"] = box.Text.Trim();
                job.Params["mode"] = mode;
                job.Params["opacity"] = opacity;
                return null;
            }, error, PdfHue, "pdftool", "워터마크를 넣어요", null, "넣기");
        }

        static readonly Hue AiHue = Theme.Sky;

        static void AddOutputChoice(StackPanel stack, Action<string> store, string current)
        {
            stack.Children.Add(Caption("저장 형식"));
            stack.Children.Add(Chips(AiHue, new[] { "hwp", "docx" }, new[] { "한글", "워드" }, current, store));
        }

        static bool AskQuiz(Job job)
        {
            string kind = job.Option.Id;
            string count = kind == "essay" ? "3" : kind == "mc" ? "5" : "10";
            string output = Hangul.Installed ? "hwp" : "docx";
            var stack = new StackPanel();
            stack.Children.Add(Caption("몇 문제"));
            stack.Children.Add(Chips(AiHue, new[] { "3", "5", "10", "15", "20" }, new[] { "3", "5", "10", "15", "20" }, count, v => count = v));
            if (output == "hwp") AddOutputChoice(stack, v => output = v, output);
            return Loop(() => stack, () =>
            {
                job.Params["count"] = count;
                job.Params["output"] = output;
                return null;
            }, new TextBlock(), AiHue, "ai", "몇 문제를 만들까요?", "정답과 해설은 뒤쪽에 따로 적어요.", "만들기");
        }

        /// 디자인 고르기: 슬라이드 모양을 작게 그려 보여 준다.
        static FrameworkElement DeckTile(DeckStyle st)
        {
            Func<string, System.Windows.Media.Brush> b = hex => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#" + hex));
            var canvas = new Canvas { Width = 96, Height = 54, Background = b(st.Palette.Lt1), ClipToBounds = true };
            Action<UIElement, double, double> put = (e, x, y) =>
            {
                Canvas.SetLeft(e, x);
                Canvas.SetTop(e, y);
                canvas.Children.Add(e);
            };
            put(new System.Windows.Shapes.Rectangle { Width = 44, Height = 5, RadiusX = 2, RadiusY = 2, Fill = b(st.Id == "board" ? st.Palette.Accents[0] : st.Palette.Dk1) }, 9, 10);
            for (int i = 0; i < 3; i++)
            {
                put(new System.Windows.Shapes.Rectangle { Width = 23, Height = 24, RadiusX = 3, RadiusY = 3, Fill = b(st.Palette.Lt2) }, 9 + i * 27, 21);
                put(new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = b(st.Palette.Accents[i]) }, 12 + i * 27, 24);
            }
            var frame = new Border { CornerRadius = new CornerRadius(6), BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Child = canvas, ClipToBounds = true };
            var label = Theme.Text(st.Label, 13, FontWeights.Bold, Theme.Ink);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 5, 0, 0);
            var stack = new StackPanel { Margin = new Thickness(2) };
            stack.Children.Add(frame);
            stack.Children.Add(label);
            return stack;
        }

        static bool AskDeck(Job job)
        {
            string style = DeckDesign.Styles[0].Id, count = "auto";
            var stack = new StackPanel();
            stack.Children.Add(Caption("디자인"));
            var wrap = new WrapPanel();
            string group = "p" + Guid.NewGuid().ToString("N");
            foreach (var st in DeckDesign.Styles)
            {
                string id = st.Id;
                var r = new RadioButton { Style = Theme.GetStyle("Chip"), Tag = AiHue, GroupName = group, Content = DeckTile(st), Margin = new Thickness(0, 0, 6, 6), IsChecked = id == style };
                r.Checked += delegate { style = id; };
                wrap.Children.Add(r);
            }
            stack.Children.Add(wrap);
            stack.Children.Add(Caption("슬라이드 수"));
            stack.Children.Add(Chips(AiHue, new[] { "auto", "6", "10", "15" }, new[] { "알맞게", "6장", "10장", "15장" }, count, v => count = v));
            return Loop(() => stack, () =>
            {
                job.Params["deck"] = style;
                job.Params["count"] = count;
                return null;
            }, new TextBlock(), AiHue, "ai", "어떤 모양으로 만들까요?", "AI가 쪽마다 알맞은 구성을 골라요. 글자와 색은 PPT에서 모두 고칠 수 있어요.", "만들기");
        }

        /// 여러 줄 입력 칸(글자가 위에서 시작한다).
        static TextBox Memo(string hint)
        {
            var box = new TextBox { Style = Theme.GetStyle("TextField"), Height = 92, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Tag = hint };
            box.Loaded += delegate
            {
                foreach (string part in new[] { "PART_ContentHost", "Hint" })
                {
                    var el = box.Template.FindName(part, box) as FrameworkElement;
                    if (el == null) continue;
                    el.VerticalAlignment = VerticalAlignment.Top;
                    el.Margin = new Thickness(el.Margin.Left, 10, el.Margin.Right, 10);
                }
            };
            return box;
        }

        /// 문서 초안: 덧붙일 말(비워 둬도 된다)과 저장 형식.
        static bool AskDraft(Job job)
        {
            var box = Memo("예: 3명 참가, 12월 5일까지 제출");
            string output = Hangul.Installed ? "hwp" : "docx";
            var stack = new StackPanel();
            stack.Children.Add(Caption("덧붙일 내용 (없으면 비워 두세요)"));
            stack.Children.Add(box);
            if (output == "hwp") AddOutputChoice(stack, v => output = v, output);
            return Loop(() => stack, () =>
            {
                job.Params["note"] = box.Text;
                job.Params["output"] = output;
                return null;
            }, new TextBlock(), AiHue, "ai", job.Option.Label + " 초안을 쓸게요", "문서에 없는 내용은 [일시]처럼 빈칸으로 남겨요.", "만들기");
        }

        static bool AskLesson(Job job)
        {
            string minutes = "45";
            string output = Hangul.Installed ? "hwp" : "docx";
            var stack = new StackPanel();
            stack.Children.Add(Caption("수업 시간"));
            stack.Children.Add(Chips(AiHue, new[] { "40", "45", "50" }, new[] { "40분", "45분", "50분" }, minutes, v => minutes = v));
            if (output == "hwp") AddOutputChoice(stack, v => output = v, output);
            return Loop(() => stack, () =>
            {
                job.Params["minutes"] = minutes;
                job.Params["output"] = output;
                return null;
            }, new TextBlock(), AiHue, "ai", "몇 분 수업인가요?", "도입·전개·정리로 나눠 시간까지 적어요.", "만들기");
        }

        static bool AskOutput(Job job)
        {
            if (!Hangul.Installed) return true;
            string output = "hwp";
            var stack = new StackPanel();
            AddOutputChoice(stack, v => output = v, output);
            return Loop(() => stack, () =>
            {
                job.Params["output"] = output;
                return null;
            }, new TextBlock(), AiHue, "ai", "어떤 형식으로 저장할까요?", null, "만들기");
        }

        static bool AskNumber(Job job)
        {
            var box = Field("1", "숫자");
            var error = ErrorLine();
            string place = "bottom-center", style = "plain";
            var stack = new StackPanel();
            stack.Children.Add(Caption("자리"));
            stack.Children.Add(Chips(PdfHue, new[] { "bottom-center", "bottom-right", "top-right" }, new[] { "아래 가운데", "아래 오른쪽", "위 오른쪽" }, place, v => place = v));
            stack.Children.Add(Caption("모양"));
            stack.Children.Add(Chips(PdfHue, new[] { "plain", "dash", "total" }, new[] { "1", "- 1 -", "1 / 10" }, style, v => style = v));
            stack.Children.Add(Caption("시작 번호"));
            stack.Children.Add(box);
            stack.Children.Add(error);
            return Loop(() => stack, () =>
            {
                int v;
                if (!int.TryParse(box.Text.Trim(), out v) || v < 0) return "시작 번호를 숫자로 적어 주세요.";
                job.Params["start"] = v.ToString();
                job.Params["place"] = place;
                job.Params["style"] = style;
                return null;
            }, error, PdfHue, "pdftool", "쪽 번호를 넣어요", null, "넣기");
        }
    }
}
