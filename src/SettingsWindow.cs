// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 설정 창: 메뉴 모양 · 메뉴를 띄우는 키 · AI 연결 · 그 밖에. 바꾸는 즉시 저장된다.
// 디자인(DESIGN.md): 책상색 바탕 위 흰 카드 한 장씩. 칸 제목마다 색 칩.
// AI 연결은 고르면 펼쳐지는 카드: 고른 카드는 그 연결의 색 테두리와 색 그림자로 떠오른다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Bakkum
{
    public class SettingsWindow : Window
    {
        /// 그림으로 확인할 때: 네트워크·Claude Code를 부르지 않고 보기용 상태를 쓴다. 설정도 저장하지 않는다.
        public static bool Demo;

        readonly AppSettings s = AppSettings.Current;
        bool loading = true;
        readonly List<ProviderRow> rows = new List<ProviderRow>();
        TextBlock headSub;

        StatusPill claudePill;
        Button claudeLogin, claudeRecheck, claudeInstall;

        StatusPill gptPill;
        Button gptSignIn, gptCancel, gptUsage, gptSignOut;
        volatile bool gptCancelled;
        bool gptBusy;

        StatusPill geminiPill;
        PasswordBox geminiBox;
        FrameworkElement geminiEntry, geminiSaved, geminiPaste;
        Button geminiGet, geminiStop, geminiKeep;
        bool geminiEntryOpen;
        int geminiBoxLength, geminiTicket;
        DispatcherTimer geminiWatch;
        uint geminiClipSeq;
        DateTime geminiWatchUntil;

        [DllImport("user32.dll")]
        static extern uint GetClipboardSequenceNumber();

        StatusPill orPill;
        Button orConnect, orCancel, orDisconnect, orPasteToggle;
        FrameworkElement orPasteArea;
        PasswordBox orBox;
        volatile bool orCancelled;
        bool orBusy;
        int quotaTicket;

        public SettingsWindow()
        {
            Title = "설정 · " + AppInfo.Name;
            Width = 664;
            Height = 880;
            MinWidth = 560;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Polish(this);
            try { Icon = App.WindowIcon; } catch { }

            var body = new StackPanel { Margin = new Thickness(26, 24, 26, 8) };
            var head = Theme.Head(AppInfo.Name, "");
            headSub = FindSub(head);
            head.Margin = new Thickness(4, 0, 0, 22);
            body.Children.Add(head);
            UpdateHeadSub();

            body.Children.Add(Section("메뉴 모양", Theme.Purple, "shape", ShapeSection()));
            body.Children.Add(Section("메뉴 칸", Theme.Yellow, "star", SlotSection()));
            body.Children.Add(Section("메뉴를 띄우는 키", Theme.Orange, "key", KeySection()));
            body.Children.Add(Section("AI 연결", Theme.Blue, "spark", AiSection()));
            body.Children.Add(Section("그 밖에", Theme.Green, "check", OtherSection()));

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
            var root = new DockPanel();
            var footer = Footer();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(scroll);
            Content = root;

            loading = false;
            Loaded += delegate { RefreshAi(); };
            Closed += delegate
            {
                gptCancelled = true;
                orCancelled = true;
                StopGeminiWatch();
            };
        }

        /// 그림 확인용: 스크롤 없이 펼친 내용(머리 + 카드 + 아래 단추 줄). 창에서 떼어 낸다.
        public FrameworkElement PreviewContent()
        {
            var root = (DockPanel)Content;
            Content = null;
            var scroll = (ScrollViewer)root.Children[1];
            var body = (FrameworkElement)scroll.Content;
            var footer = (FrameworkElement)root.Children[0];
            scroll.Content = null;
            root.Children.Clear();
            var stack = new StackPanel();
            stack.Children.Add(body);
            stack.Children.Add(footer);
            RefreshAi();
            return stack;
        }

        void Save()
        {
            if (loading || Demo) return;
            s.Save();
        }

        static TextBlock FindSub(FrameworkElement head)
        {
            var dock = head as DockPanel;
            if (dock == null) return null;
            foreach (var c in dock.Children)
            {
                var words = c as StackPanel;
                if (words == null) continue;
                if (words.Children.Count > 1) return words.Children[1] as TextBlock;
                var t = Theme.MetaLabel("");
                t.FontSize = 13.5;
                t.Margin = new Thickness(0, 3, 0, 0);
                words.Children.Add(t);
                return t;
            }
            return null;
        }

        void UpdateHeadSub()
        {
            if (headSub == null) return;
            string k = s.TriggerKey;
            string open = "파일을 오른쪽 클릭 → 보내기 → '" + AppInfo.Name + "'으로도 열 수 있어요.";
            if (k == "Off") headSub.Text = Theme.Keep("파일을 오른쪽 클릭 → 보내기 → '" + AppInfo.Name + "'으로 메뉴를 열어요.");
            else headSub.Text = Theme.Keep("탐색기에서 파일을 끌다가 " + (k == "Ctrl" ? "Ctrl을" : k == "Q" ? "Q를" : "Shift를") + " 누르면 메뉴가 떠요. " + open);
        }

        static FrameworkElement Section(string title, Hue hue, string glyph, UIElement content)
        {
            var stack = new StackPanel();
            stack.Children.Add(Theme.SectionHead(title, hue, glyph));
            stack.Children.Add(content);
            var card = Theme.Card(stack, 24);
            card.Margin = new Thickness(0, 0, 0, 16);
            return card;
        }

        // ── 메뉴 모양 ──

        UIElement ShapeSection()
        {
            var stack = new StackPanel();
            var grid = new UniformGrid { Columns = 4, Margin = new Thickness(-5, 0, -5, 0) };
            var shapes = new[]
            {
                new[] { "index", "색인" },
                new[] { "fan", "부채" },
                new[] { "wave", "물결" },
                new[] { "honeycomb", "벌집" }
            };
            foreach (var pair in shapes)
            {
                string id = pair[0];
                var tile = new StackPanel();
                var frame = new Border { Background = Theme.Desk, CornerRadius = new CornerRadius(12), Height = 96, Padding = new Thickness(6) };
                var img = new Image { Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                try { img.Source = MenuWindow.Preview(id, 1, new List<string>(), null, 0.6); }
                catch (Exception ex) { Log.Write("미리보기 실패: " + ex.GetType().Name); }
                frame.Child = img;
                tile.Children.Add(frame);
                var label = new DockPanel { Margin = new Thickness(4, 9, 2, 1) };
                if (id == "index")
                {
                    // 고른 칸은 바탕이 옅은 보라라서, 꼬리표는 흰 바탕 + 옅은 보라 테두리로 어느 쪽에서도 보이게
                    var tag = Theme.Tag("기본", Theme.Purple);
                    tag.Background = Theme.Sheet;
                    tag.BorderBrush = Theme.Purple.TintBrush;
                    tag.BorderThickness = new Thickness(1);
                    tag.Padding = new Thickness(7, 1, 7, 2);
                    DockPanel.SetDock(tag, Dock.Right);
                    label.Children.Add(tag);
                }
                var name = new TextBlock { Text = pair[1], VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(name);
                tile.Children.Add(label);
                var rb = new RadioButton { Style = Theme.GetStyle("Tile"), GroupName = "shape", Content = tile, Tag = Theme.Purple, Margin = new Thickness(5, 0, 5, 0) };
                rb.IsChecked = s.MenuShape == id;
                rb.Checked += delegate
                {
                    s.MenuShape = id;
                    Save();
                };
                grid.Children.Add(rb);
            }
            stack.Children.Add(grid);
            var tip = Theme.MetaLabel("메뉴를 띄운 채 숫자 키(1~9)로도 고를 수 있어요. Esc를 누르면 닫혀요.");
            tip.Margin = new Thickness(2, 14, 0, 0);
            stack.Children.Add(tip);
            return stack;
        }

        // ── 메뉴 칸: 보이기·차례 ──

        StackPanel slotList;

        UIElement SlotSection()
        {
            var stack = new StackPanel();
            stack.Children.Add(Theme.Body("끈 파일에 맞는 칸만 메뉴에 나와요. 안 쓰는 칸은 끄고, 화살표로 차례를 바꿀 수 있어요."));
            slotList = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            stack.Children.Add(slotList);
            var auto = Theme.Switch("자주 쓰는 칸을 저절로 앞으로", "켜 두면 화살표로 정한 차례 대신 많이 쓴 칸부터 나와요.", s.AutoOrder, delegate (bool on)
            {
                s.AutoOrder = on;
                Save();
                RefreshSlots();
            });
            stack.Children.Add(Spaced(auto, 16));
            var tip = Theme.MetaLabel("메뉴에서 고를 거리를 오른쪽 단추로 누르면 즐겨찾기에 들어가요(3개까지).");
            tip.Margin = new Thickness(2, 12, 0, 0);
            stack.Children.Add(tip);
            RefreshSlots();
            return stack;
        }

        List<MenuAction> SlotsInOrder()
        {
            var order = s.SlotOrder ?? new List<string>();
            return System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(System.Linq.Enumerable.Where(MenuAction.All, a => !a.IsSettings), a =>
            {
                int i = order.IndexOf(a.Id);
                return i >= 0 ? i : 1000 + MenuAction.All.IndexOf(a);
            }));
        }

        // 시험(/selftest-tools update)에서만 쓴다
        internal List<string> SlotIdsForTest()
        {
            var ids = new List<string>();
            foreach (var a in SlotsInOrder()) ids.Add(a.Id);
            return ids;
        }

        internal List<CheckBox> SlotSwitchesForTest()
        {
            var boxes = new List<CheckBox>();
            foreach (UIElement row in slotList.Children) boxes.Add((CheckBox)((DockPanel)row).Children[0]);
            return boxes;
        }

        internal void MoveSlot(int index, int delta)
        {
            var ids = new List<string>();
            foreach (var a in SlotsInOrder()) ids.Add(a.Id);
            int to = index + delta;
            if (to < 0 || to >= ids.Count) return;
            string id = ids[index];
            ids[index] = ids[to];
            ids[to] = id;
            s.SlotOrder = ids;
            Save();
            RefreshSlots();
        }

        Button SlotArrow(string glyph, string tip, bool enabled, Action click)
        {
            var b = new Button { Style = Theme.GetStyle("Quiet"), Width = 34, Height = 34, Padding = new Thickness(0), ToolTip = tip, IsEnabled = enabled, Content = Theme.Glyph(glyph, 15, enabled ? Theme.Ink2 : Theme.Line2, 2) };
            b.Click += delegate { click(); };
            return b;
        }

        void RefreshSlots()
        {
            slotList.Children.Clear();
            var list = SlotsInOrder();
            if (s.HiddenSlots == null) s.HiddenSlots = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                int at = i;
                var row = new DockPanel { Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0) };
                bool shown = !s.HiddenSlots.Contains(a.Id);
                var toggle = Theme.Switch("", null, shown, delegate (bool on)
                {
                    if (on) s.HiddenSlots.Remove(a.Id);
                    else if (s.HiddenSlots.Count < list.Count - 1) s.HiddenSlots.Add(a.Id);
                    else
                    {
                        // 칸이 하나도 안 남게 할 수는 없다
                        Dispatcher.BeginInvoke(new Action(RefreshSlots));
                        return;
                    }
                    Save();
                });
                toggle.Margin = new Thickness(8, 0, 0, 0);
                toggle.VerticalAlignment = VerticalAlignment.Center;
                toggle.ToolTip = "메뉴에 보이기";
                DockPanel.SetDock(toggle, Dock.Right);
                row.Children.Add(toggle);
                bool manual = !s.AutoOrder;
                var down = SlotArrow("chevdown", "뒤로", manual && i < list.Count - 1, delegate { MoveSlot(at, 1); });
                DockPanel.SetDock(down, Dock.Right);
                row.Children.Add(down);
                var up = SlotArrow("up", "앞으로", manual && i > 0, delegate { MoveSlot(at, -1); });
                DockPanel.SetDock(up, Dock.Right);
                row.Children.Add(up);
                var chip = Theme.GlyphChip(a.Id, Theme.HueOf(a.Id), 34);
                chip.Margin = new Thickness(0, 0, 12, 0);
                DockPanel.SetDock(chip, Dock.Left);
                row.Children.Add(chip);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(Theme.Text(a.Label, 14.5, FontWeights.Bold, Theme.Ink));
                words.Children.Add(Theme.MetaLabel(a.Hint));
                row.Children.Add(words);
                slotList.Children.Add(row);
            }
        }

        // ── 메뉴를 띄우는 키 ──

        UIElement KeySection()
        {
            var stack = new StackPanel();
            stack.Children.Add(Theme.Body("탐색기에서 파일을 끄는 동안 이 키를 누르면 커서 자리에 메뉴가 떠요."));
            var grid = new UniformGrid { Columns = 4, Margin = new Thickness(-5, 14, -5, 0) };
            var keys = new[] { new[] { "Shift", "Shift" }, new[] { "Ctrl", "Ctrl" }, new[] { "Q", "Q" }, new[] { "Off", "끄기" } };
            foreach (var pair in keys)
            {
                string id = pair[0];
                var rb = new RadioButton { Style = Theme.GetStyle("Chip"), GroupName = "key", Content = pair[1], Tag = Theme.Orange, Margin = new Thickness(5, 0, 5, 0) };
                rb.IsChecked = s.TriggerKey == id;
                rb.Checked += delegate
                {
                    s.TriggerKey = id;
                    UpdateHeadSub();
                    Save();
                };
                grid.Children.Add(rb);
            }
            stack.Children.Add(grid);
            var any = Theme.Switch("탐색기 말고 다른 프로그램에서 끌 때도 띄우기", "메일·메신저에서 파일을 끌 때도 메뉴가 떠요.", s.AnySource, delegate (bool on)
            {
                s.AnySource = on;
                Save();
            });
            any.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(any);
            return stack;
        }

        // ── AI 연결 ──

        UIElement AiSection()
        {
            var stack = new StackPanel();
            stack.Children.Add(Theme.Body("'AI로' 칸의 요약·번역·문제 만들기와 글자 뽑기의 'AI로'에 써요. 선생님 계정을 이어 쓰고, 보낼 때마다 먼저 물어봐요."));
            var list = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            stack.Children.Add(list);

            var none = AddRow(list, "none", "쓰지 않음", "문서를 밖으로 보내지 않아요. 글자 뽑기도 이 PC 안에서만 해요.", null, Theme.Slate);
            none.Details.Children.Clear();

            var claude = AddRow(list, Ai.ClaudeId, "Claude", "Claude Pro·Max 구독 · 이 PC의 Claude Code로", "구독", Theme.Orange);
            ClaudeDetails(claude.Details);

            var gpt = AddRow(list, Ai.ChatGptId, "ChatGPT", "ChatGPT Plus·Pro 요금제 · OpenAI 로그인", "구독", Theme.Green);
            ChatGptDetails(gpt.Details);

            var gemini = AddRow(list, Ai.GeminiId, "Gemini", "Google AI Studio 키 · 무료 키도 돼요", "키", Theme.Blue);
            GeminiDetails(gemini.Details);

            var or = AddRow(list, Ai.OpenRouterId, "OpenRouter", "무료 AI 모델 · OpenRouter 로그인", "무료", Theme.Purple);
            OpenRouterDetails(or.Details);

            foreach (var r in rows) r.Refresh(false);
            return stack;
        }

        ProviderRow AddRow(StackPanel list, string id, string name, string sub, string tag, Hue hue)
        {
            var row = new ProviderRow(id, name, sub, tag, hue);
            row.Margin = new Thickness(0, 0, 0, 10);
            row.Radio.IsChecked = (s.AiProvider ?? "none") == id;
            row.Radio.Checked += delegate
            {
                if (loading) return;
                s.AiProvider = id;
                Save();
                if (id != Ai.GeminiId) StopGeminiWatch();
                foreach (var r in rows) r.Refresh(true);
                RefreshAi();
            };
            row.Radio.Unchecked += delegate { row.Refresh(true); };
            rows.Add(row);
            list.Children.Add(row);
            return row;
        }

        static TextBlock DetailText(string text)
        {
            var t = Theme.Body(text);
            t.FontSize = 14;
            t.Margin = new Thickness(0, 10, 0, 0);
            return t;
        }

        static FrameworkElement Spaced(FrameworkElement e, double top)
        {
            e.Margin = new Thickness(0, top, 0, 0);
            return e;
        }

        void ClaudeDetails(StackPanel d)
        {
            claudePill = new StatusPill();
            d.Children.Add(claudePill);
            d.Children.Add(DetailText("이 PC에 설치된 Claude Code에 선생님의 Claude 구독으로 로그인해 두면 이어 써요. 로그인은 Anthropic 화면에서 하고, 이 프로그램은 로그인 정보를 보거나 저장하지 않아요."));
            claudeLogin = Theme.MakeButton("로그인 창 열기", "Line");
            claudeLogin.Click += delegate
            {
                if (!ClaudeCode.OpenLogin()) claudePill.Set("로그인 창을 열지 못했어요", Theme.Coral);
                else claudePill.Set("로그인 창에서 마친 뒤 [다시 확인]을 눌러 주세요", Theme.Orange);
            };
            claudeRecheck = Theme.MakeButton("다시 확인", "Quiet");
            claudeRecheck.Click += delegate
            {
                ClaudeCode.Forget();
                RefreshAi();
            };
            claudeInstall = Theme.MakeButton("설치 방법", "Line");
            claudeInstall.Click += delegate { ShowInstallHelp(); };
            d.Children.Add(Spaced(Theme.ButtonRow(claudeInstall, claudeLogin, claudeRecheck), 14));
            ClaudeButtons(true, true);   // 확인이 끝날 때까지는 [다시 확인]만
            var metered = Theme.Switch("구독이 아닌 계정(쓴 만큼 요금)이어도 쓰기", "켜면 Anthropic 요금이 나갈 수 있어요.", s.ClaudeAllowMetered, delegate (bool on)
            {
                s.ClaudeAllowMetered = on;
                Save();
            });
            d.Children.Add(Spaced(metered, 16));
        }

        void ChatGptDetails(StackPanel d)
        {
            gptPill = new StatusPill();
            d.Children.Add(gptPill);
            d.Children.Add(DetailText("브라우저에서 OpenAI 로그인 화면이 열려요. Plus·Pro 요금제가 있어야 하고, 쓴 만큼 ChatGPT 사용량에서 빠져요. 이 프로그램은 비밀번호를 보지 않아요."));
            gptSignIn = Theme.MakeButton("ChatGPT로 계속하기", "Primary");
            gptSignIn.Click += delegate { StartChatGptSignIn(); };
            gptCancel = Theme.MakeButton("취소", "Line");
            gptCancel.Click += delegate { gptCancelled = true; };
            gptUsage = Theme.MakeButton("사용량 보기", "Line");
            gptUsage.Click += delegate { Open(ChatGpt.UsagePage); };
            gptSignOut = Theme.MakeButton("로그아웃", "Quiet");
            gptSignOut.Click += delegate
            {
                gptSignOut.IsEnabled = false;
                gptPill.Set("로그아웃하는 중", Theme.Slate);
                Task.Run(() => ChatGpt.SignOut()).ContinueWith(t => Dispatcher.BeginInvoke(new Action(delegate
                {
                    gptSignOut.IsEnabled = true;
                    if (!IsLoaded) return;
                    ShowChatGpt();
                    // OpenAI 쪽 연결 해제를 확인하지 못했으면(인터넷이 끊겼을 때 등) 그렇게 알린다. 이 PC의 로그인은 이미 지웠다.
                    string notice = t.IsFaulted ? ChatGpt.SignOutUnconfirmed : t.Result;
                    if (notice != null) Dialog.Ask(this, Theme.Orange, "box", "이 PC에서는 로그아웃했어요", notice, null, "확인");
                })));
            };
            d.Children.Add(Spaced(Theme.ButtonRow(gptSignIn, gptCancel, gptUsage, gptSignOut), 14));
        }

        void StartChatGptSignIn()
        {
            if (gptBusy) return;
            gptBusy = true;
            gptCancelled = false;
            ShowChatGpt();
            gptPill.Set("브라우저에서 로그인을 마쳐 주세요", Theme.Orange);
            Task.Run(() =>
            {
                string failure;
                bool ok = ChatGpt.SignIn(() => gptCancelled, out failure);
                return new KeyValuePair<bool, string>(ok, failure);
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(delegate
            {
                gptBusy = false;
                if (!IsLoaded) return;
                bool ok = !t.IsFaulted && t.Result.Key;
                string failure = t.IsFaulted ? "로그인하지 못했어요." : t.Result.Value;
                ShowChatGpt();
                if (ok)
                {
                    Activate();
                    if (ChatGpt.TakeFirstNotice())
                    {
                        int pick = Dialog.Ask(this, Theme.Green, "check", "ChatGPT 요금제를 사용하고 있어요",
                            "AI로 글자를 뽑으면 선생님의 ChatGPT 요금제 사용량에서 빠져요. 남은 사용량은 chatgpt.com의 설정 → 사용량에서 볼 수 있어요.",
                            null, "사용량 보기", "확인");
                        if (pick == 0) Open(ChatGpt.UsagePage);
                    }
                }
                else if (failure != null) gptPill.Set(failure, Theme.Coral);
            })));
        }

        void ShowChatGpt()
        {
            bool signedIn = Demo ? DemoSignedIn("chatgpt") : ChatGpt.SignedIn;
            gptSignIn.Visibility = !gptBusy && !signedIn ? Visibility.Visible : Visibility.Collapsed;
            gptCancel.Visibility = gptBusy ? Visibility.Visible : Visibility.Collapsed;
            gptUsage.Visibility = !gptBusy && signedIn ? Visibility.Visible : Visibility.Collapsed;
            gptSignOut.Visibility = !gptBusy && signedIn ? Visibility.Visible : Visibility.Collapsed;
            if (gptBusy) return;
            if (signedIn)
            {
                string who = Demo ? null : ChatGpt.Account;
                gptPill.Set(who == null ? "연결됨 · ChatGPT 요금제를 쓰고 있어요" : "연결됨 · " + who, Theme.Green);
            }
            else gptPill.Set("로그인이 필요해요", Theme.Orange);
        }

        void GeminiDetails(StackPanel d)
        {
            geminiPill = new StatusPill();
            d.Children.Add(geminiPill);

            // 키 받기: 키가 없을 때, 또는 [다른 키 넣기]를 눌렀을 때만
            var entry = new StackPanel();
            entry.Children.Add(DetailText("1. [키 받으러 가기]를 누르고 구글에 로그인해요.\n2. 구글 화면에서 키를 만들고 복사 단추를 눌러요.\n3. 복사하면 바꿈이 알아서 넣고 확인해요."));
            geminiGet = Theme.MakeButton("키 받으러 가기", "Primary");
            geminiGet.Click += delegate { StartGeminiWatch(); };
            geminiStop = Theme.MakeButton("기다리기 멈추기", "Line");
            geminiStop.Click += delegate
            {
                StopGeminiWatch();
                ShowGemini();
            };
            var pasteToggle = Theme.MakeButton("키를 직접 넣을게요", "Quiet");
            pasteToggle.Click += delegate
            {
                geminiPaste.Visibility = geminiPaste.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                if (geminiPaste.Visibility == Visibility.Visible) geminiBox.Focus();
            };
            geminiKeep = Theme.MakeButton("취소", "Quiet");
            geminiKeep.Click += delegate
            {
                geminiPaste.Visibility = Visibility.Collapsed;
                geminiBox.Password = "";
                geminiEntryOpen = false;
                ShowGemini();
            };
            entry.Children.Add(Spaced(Theme.ButtonRow(geminiGet, geminiStop, pasteToggle, geminiKeep), 14));

            // 직접 넣는 곳. 키 칸에는 오른쪽 클릭 메뉴가 없어서 [붙여넣기]를 따로 둔다.
            var paste = new StackPanel { Visibility = Demo && DemoSignedIn("pastekey") ? Visibility.Visible : Visibility.Collapsed };
            var label = Theme.Text("Gemini API 키", 13, FontWeights.Bold, Theme.Ink2);
            label.Margin = new Thickness(0, 14, 0, 7);
            paste.Children.Add(label);
            var line = new DockPanel();
            var save = Theme.MakeButton("저장", "Quiet");
            save.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(save, Dock.Right);
            line.Children.Add(save);
            var pasteNow = Theme.MakeButton("붙여넣기", "Line");
            pasteNow.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(pasteNow, Dock.Right);
            line.Children.Add(pasteNow);
            geminiBox = new PasswordBox { Style = Theme.GetStyle("Field") };
            line.Children.Add(geminiBox);
            paste.Children.Add(line);
            paste.Children.Add(Spaced(LinkLine("키 만들기: ", "aistudio.google.com/apikey", Gemini.KeyPage), 8));
            pasteNow.Click += delegate
            {
                string copied;
                TryReadCopied(out copied);
                if (!GeminiCopied(copied)) geminiPill.Set("복사해 둔 글이 Gemini 키 모양이 아니에요. 구글 화면에서 키를 다시 복사해 주세요", Theme.Coral);
            };
            save.Click += delegate
            {
                string k = Web.CleanSecret(geminiBox.Password);
                if (k == null)
                {
                    geminiPill.Set("키 모양이 맞지 않아요. AI Studio에서 복사한 키를 그대로 붙여 넣어 주세요", Theme.Coral);
                    return;
                }
                TakeGeminiKey(k);
            };
            geminiBox.PasswordChanged += delegate
            {
                // 한 번에 들어온 글(붙여넣기)이 키 모양이면 [저장]을 기다리지 않는다
                int before = geminiBoxLength;
                string now = geminiBox.Password;
                geminiBoxLength = now.Length;
                if (now.Length - before >= 20) Dispatcher.BeginInvoke(new Action(delegate { GeminiCopied(now); }));
            };
            geminiPaste = paste;
            entry.Children.Add(paste);
            entry.Children.Add(Spaced(Theme.Note("구글 로그인 정보는 바꿈에 들어오지 않아요. 구글이 앱에 쓰라고 안내한 API 키만 써요. 기다리는 동안 복사한 글은 키 모양인지만 보고, 저장하거나 보내지 않아요.", Theme.Blue), 14));
            geminiEntry = entry;
            d.Children.Add(entry);

            // 키가 저장돼 있을 때의 단추들
            var check = Theme.MakeButton("키 확인", "Line");
            check.Click += delegate { CheckGeminiKey(); };
            var change = Theme.MakeButton("다른 키 넣기", "Quiet");
            change.Click += delegate
            {
                geminiEntryOpen = true;
                ShowGemini();
            };
            var clear = Theme.MakeButton("지우기", "Quiet");
            clear.Click += delegate
            {
                s.GeminiKey = "";
                Save();
                geminiEntryOpen = false;
                ShowGemini();
            };
            geminiSaved = Spaced(Theme.ButtonRow(check, change, clear), 14);
            d.Children.Add(geminiSaved);
            geminiEntryOpen = Demo && DemoSignedIn("changekey");
            d.Children.Add(Spaced(Theme.Warning("무료 키로 보낸 내용은 Google 서비스 개선에 쓰이고 사람이 볼 수도 있어요. 만 18세 이상만 쓸 수 있고, 결제를 연결한 프로젝트의 키는 요금이 나갈 수 있어요. 키는 다른 사람과 나눠 쓰지 마세요."), 14));
        }

        void ShowGemini()
        {
            geminiTicket++;   // 진행 중인 키 확인의 결과는 이제 보이지 않게
            bool has = Demo ? DemoSignedIn("gemini") : !string.IsNullOrEmpty(s.GeminiKey);
            bool waiting = Demo ? DemoSignedIn("waitkey") : geminiWatch != null;
            bool open = !has || geminiEntryOpen;
            geminiEntry.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            geminiSaved.Visibility = has && !open ? Visibility.Visible : Visibility.Collapsed;
            geminiGet.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
            geminiStop.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
            geminiKeep.Visibility = has && !waiting ? Visibility.Visible : Visibility.Collapsed;
            if (waiting) geminiPill.Set("구글 화면에서 키를 복사하면 바로 넣어요", Theme.Orange);
            else if (has) geminiPill.Set("키 저장됨 · 이 PC의 내 계정에서만 풀리게 암호화", Theme.Green);
            else geminiPill.Set("저장된 키가 없어요", Theme.Orange);
        }

        /// [키 받으러 가기]: 구글의 키 화면을 열고, 키가 복사되기를 최대 10분 기다린다.
        /// 이 단추를 누른 뒤에만, 복사한 글이 바뀔 때만 읽고, 키 모양이 아니면 바로 버린다.
        void StartGeminiWatch()
        {
            if (geminiWatch != null) return;
            string copied;
            TryReadCopied(out copied);
            string ready = Gemini.FindKey(copied);
            if (ready != null && ready != s.GeminiKey)
            {
                TakeGeminiKey(ready);   // 이미 복사해 둔 키면 브라우저를 열 것 없이 바로
                return;
            }
            geminiClipSeq = GetClipboardSequenceNumber();
            geminiWatchUntil = DateTime.UtcNow.AddMinutes(10);
            geminiWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            geminiWatch.Tick += delegate { GeminiWatchTick(); };
            geminiWatch.Start();
            ShowGemini();
            Open(Gemini.KeyPage);
        }

        void StopGeminiWatch()
        {
            if (geminiWatch == null) return;
            geminiWatch.Stop();
            geminiWatch = null;
        }

        void GeminiWatchTick()
        {
            if (DateTime.UtcNow > geminiWatchUntil)
            {
                StopGeminiWatch();
                ShowGemini();
                geminiPill.Set("10분 동안 복사한 키가 없어서 기다리기를 멈췄어요. [키 받으러 가기]를 다시 눌러 주세요", Theme.Orange);
                return;
            }
            uint now = GetClipboardSequenceNumber();
            if (now == geminiClipSeq) return;
            string copied;
            if (!TryReadCopied(out copied)) return;   // 다른 프로그램이 쓰는 중이면 다음 차례에 다시 본다
            geminiClipSeq = now;
            if (GeminiCopied(copied)) BringForward();
        }

        /// 복사한 글이 키 모양이면 넣고 true. 아니면 아무것도 남기지 않고 false.
        public bool GeminiCopied(string copied)
        {
            string k = Gemini.FindKey(copied);
            if (k == null) return false;
            TakeGeminiKey(k);
            return true;
        }

        /// 키를 저장하고, 복사해 둔 키를 지우고, 구글에 쓸 수 있는 키인지 바로 확인한다.
        void TakeGeminiKey(string k)
        {
            StopGeminiWatch();
            s.GeminiKey = k;
            Save();
            ForgetCopiedKey(k);
            geminiBox.Password = "";
            geminiPaste.Visibility = Visibility.Collapsed;
            geminiEntryOpen = false;
            ShowGemini();
            CheckGeminiKey();
        }

        void CheckGeminiKey()
        {
            string k = s.GeminiKey;
            if (string.IsNullOrEmpty(k))
            {
                geminiPill.Set("먼저 키를 저장해 주세요", Theme.Orange);
                return;
            }
            if (Demo)
            {
                geminiPill.Set("키가 맞아요 · 쓸 수 있어요", Theme.Green);
                return;
            }
            int ticket = ++geminiTicket;
            geminiPill.Set("확인 중", Theme.Slate);
            Task.Run(() => Gemini.CheckKey(k)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(delegate
            {
                if (ticket != geminiTicket) return;
                string problem = t.IsFaulted ? "확인하지 못했어요" : t.Result;
                if (problem == null) geminiPill.Set("키가 맞아요 · 쓸 수 있어요", Theme.Green);
                else geminiPill.Set(problem, Theme.Coral);
            })));
        }

        /// 복사해 둔 글을 읽는다. 다른 프로그램이 쓰고 있어 못 읽으면 false.
        static bool TryReadCopied(out string text)
        {
            text = null;
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
                return true;
            }
            catch { return false; }
        }

        /// 넣은 키가 복사해 둔 글로 남아 있으면 지운다. 그 사이 다른 글을 복사했으면 건드리지 않는다.
        static void ForgetCopiedKey(string k)
        {
            if (Demo) return;
            string copied;
            if (!TryReadCopied(out copied) || Gemini.FindKey(copied) != k) return;
            try { Clipboard.Clear(); }
            catch { }
        }

        /// 브라우저 뒤에 가려진 설정 창을 앞으로 꺼내 결과가 보이게 한다.
        void BringForward()
        {
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Topmost = true;
                Topmost = false;
                Activate();
            }
            catch { }
        }

        void OpenRouterDetails(StackPanel d)
        {
            orPill = new StatusPill();
            d.Children.Add(orPill);
            d.Children.Add(DetailText("OpenRouter 계정으로 무료 AI 모델을 써요. 하루 50번까지 무료이고, 붐비는 모델은 건너뛰어 다른 무료 모델로 읽어요."));
            orConnect = Theme.MakeButton("OpenRouter로 연결", "Primary");
            orConnect.Click += delegate { StartOpenRouterSignIn(); };
            orCancel = Theme.MakeButton("취소", "Line");
            orCancel.Click += delegate { orCancelled = true; };
            orDisconnect = Theme.MakeButton("연결 끊기", "Line");
            orDisconnect.Click += delegate
            {
                s.OpenRouterKey = "";
                Save();
                ShowOpenRouter();
            };
            orPasteToggle = Theme.MakeButton("키를 직접 넣을게요", "Quiet");
            orPasteToggle.Click += delegate
            {
                orPasteArea.Visibility = orPasteArea.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                if (orPasteArea.Visibility == Visibility.Visible) orBox.Focus();
            };
            d.Children.Add(Spaced(Theme.ButtonRow(orConnect, orCancel, orDisconnect, orPasteToggle), 14));

            var paste = new StackPanel { Visibility = Visibility.Collapsed };
            var label = Theme.Text("OpenRouter API 키", 13, FontWeights.Bold, Theme.Ink2);
            label.Margin = new Thickness(0, 14, 0, 7);
            paste.Children.Add(label);
            var line = new DockPanel();
            var save = Theme.MakeButton("저장", "Line");
            save.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(save, Dock.Right);
            line.Children.Add(save);
            orBox = new PasswordBox { Style = Theme.GetStyle("Field") };
            line.Children.Add(orBox);
            paste.Children.Add(line);
            paste.Children.Add(Spaced(LinkLine("키 만들기: ", "openrouter.ai/settings/keys", OpenRouter.KeysPage), 8));
            save.Click += delegate
            {
                string k = OpenRouter.CleanKey(orBox.Password);
                if (k == null)
                {
                    orPill.Set("OpenRouter 키 모양이 아니에요. Keys 화면에서 만든 키(sk-or-로 시작)를 그대로 붙여 넣어 주세요", Theme.Coral);
                    return;
                }
                orPill.Set("키 확인 중", Theme.Slate);
                Task.Run(() => OpenRouter.CheckKey(k)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(delegate
                {
                    string problem = t.IsFaulted ? "확인하지 못했어요" : t.Result;
                    if (problem != null)
                    {
                        orPill.Set(problem, Theme.Coral);
                        return;
                    }
                    s.OpenRouterKey = k;
                    Save();
                    orBox.Password = "";
                    orPasteArea.Visibility = Visibility.Collapsed;
                    ShowOpenRouter();
                })));
            };
            orPasteArea = paste;
            d.Children.Add(paste);

            var privacy = Theme.Switch("기록·학습하지 않는 곳으로만 보내기", "켜면 쓸 수 있는 무료 모델이 줄어요.", s.OpenRouterNoTraining, delegate (bool on)
            {
                s.OpenRouterNoTraining = on;
                Save();
            });
            d.Children.Add(Spaced(privacy, 16));
            d.Children.Add(Spaced(Theme.Warning("무료 모델은 그 모델을 운영하는 회사가 보낸 내용을 기록하거나 학습에 쓸 수 있어요. 학생 개인정보가 담긴 문서는 보내지 마세요."), 14));
        }

        void StartOpenRouterSignIn()
        {
            if (orBusy) return;
            orBusy = true;
            orCancelled = false;
            ShowOpenRouter();
            orPill.Set("브라우저에서 로그인을 마쳐 주세요", Theme.Orange);
            Task.Run(() =>
            {
                string failure;
                string key = OpenRouter.SignIn(() => orCancelled, out failure);
                return new KeyValuePair<string, string>(key, failure);
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(delegate
            {
                orBusy = false;
                if (!IsLoaded) return;
                string key = t.IsFaulted ? null : t.Result.Key;
                string failure = t.IsFaulted ? "연결하지 못했어요." : t.Result.Value;
                if (key != null)
                {
                    s.OpenRouterKey = key;
                    Save();
                    Activate();
                }
                ShowOpenRouter();
                if (key == null && failure != null) orPill.Set(failure, Theme.Coral);
            })));
        }

        void ShowOpenRouter()
        {
            bool has = Demo ? DemoSignedIn("openrouter") : !string.IsNullOrEmpty(s.OpenRouterKey);
            orConnect.Visibility = !orBusy && !has ? Visibility.Visible : Visibility.Collapsed;
            orCancel.Visibility = orBusy ? Visibility.Visible : Visibility.Collapsed;
            orDisconnect.Visibility = !orBusy && has ? Visibility.Visible : Visibility.Collapsed;
            orPasteToggle.Visibility = !orBusy && !has ? Visibility.Visible : Visibility.Collapsed;
            if (has) orPasteArea.Visibility = Visibility.Collapsed;
            if (orBusy) return;
            if (!has)
            {
                orPill.Set("연결이 필요해요", Theme.Orange);
                return;
            }
            orPill.Set("연결됨", Theme.Green);
            if (Demo)
            {
                orPill.Set("연결됨 · 오늘 무료 사용 50번 중 38번 남음", Theme.Green);
                return;
            }
            int ticket = ++quotaTicket;
            string key = s.OpenRouterKey;
            Task.Run(() => OpenRouter.TodayQuota(key)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(delegate
            {
                if (ticket != quotaTicket || t.IsFaulted || t.Result == null) return;
                if (string.IsNullOrEmpty(s.OpenRouterKey)) return;
                var q = t.Result;
                orPill.Set("연결됨 · 오늘 무료 사용 " + q.Limit + "번 중 " + q.Remaining + "번 남음", q.Remaining > 0 ? Theme.Green : Theme.Orange);
            })));
        }

        static bool DemoSignedIn(string what)
        {
            return DemoState.Contains(what);
        }

        /// 그림 확인용 상태(예: "chatgpt,openrouter" = 그 둘은 연결된 것으로 보이기)
        public static string DemoState = "";

        void RefreshAi()
        {
            string p = s.AiProvider ?? "none";
            if (p == Ai.ClaudeId) RefreshClaude();
            if (p == Ai.ChatGptId) ShowChatGpt();
            if (p == Ai.GeminiId) ShowGemini();
            if (p == Ai.OpenRouterId) ShowOpenRouter();
        }

        void RefreshClaude()
        {
            if (Demo)
            {
                bool on = DemoSignedIn("claude");
                ClaudeButtons(on, on);
                if (on) claudePill.Set("연결됨 · Claude 구독 계정으로 로그인돼 있어요", Theme.Green);
                else claudePill.Set("이 PC에서 Claude Code를 찾지 못했어요 · [설치 방법]을 눌러 보세요", Theme.Orange);
                return;
            }
            claudePill.Set("확인 중", Theme.Slate);
            Task.Run(() => ClaudeCode.Status()).ContinueWith(t =>
            {
                var st = t.IsFaulted ? null : t.Result;
                Dispatcher.BeginInvoke(new Action(delegate { ShowClaude(st); }));
            });
        }

        /// 상황에 맞는 단추만: 없으면 [설치 방법], 로그인 전이면 [로그인 창 열기], 연결되면 [다시 확인]만 테두리 단추로
        void ClaudeButtons(bool found, bool loggedIn)
        {
            claudeInstall.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
            claudeLogin.Visibility = found && !loggedIn ? Visibility.Visible : Visibility.Collapsed;
            claudeRecheck.Style = Theme.GetStyle(found && loggedIn ? "Line" : "Quiet");
        }

        void ShowClaude(ClaudeCode.StatusInfo st)
        {
            if (st == null)
            {
                claudePill.Set("상태를 확인하지 못했어요", Theme.Coral);
                return;
            }
            ClaudeButtons(st.Found, st.LoggedIn);
            if (!st.Found) claudePill.Set("이 PC에서 Claude Code를 찾지 못했어요 · [설치 방법]을 눌러 보세요", Theme.Orange);
            else if (!st.LoggedIn) claudePill.Set(st.Problem ?? "로그인이 필요해요 · [로그인 창 열기]를 눌러 주세요", Theme.Orange);
            else if (st.Subscription) claudePill.Set("연결됨 · Claude 구독 계정으로 로그인돼 있어요", Theme.Green);
            else claudePill.Set("'쓴 만큼 요금' 계정이에요 · 아래 스위치를 켜야 써요", Theme.Orange);
        }

        void ShowInstallHelp()
        {
            const string cmd = "irm https://claude.ai/install.ps1 | iex";
            var steps = new StackPanel();
            steps.Children.Add(Theme.CodeBox(cmd));
            var after = Theme.Body("시작 메뉴에서 PowerShell을 열고, 위 명령을 붙여 넣은 뒤 Enter를 눌러요. 설치가 끝나면 설정 창에서 [로그인 창 열기]를 눌러요.");
            after.Margin = new Thickness(0, 12, 0, 0);
            steps.Children.Add(after);
            int pick = Dialog.Ask(this, Theme.Orange, "box", "Claude Code 설치 방법",
                "Claude Code는 Anthropic이 만든 프로그램이에요. 한 번만 설치하면 돼요.", steps, "닫기", "명령 복사");
            if (pick == 1)
            {
                try { Clipboard.SetText(cmd); } catch { }
            }
        }

        static FrameworkElement LinkLine(string lead, string text, string url)
        {
            var t = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Ink3 };
            t.Inlines.Add(new Run(lead));
            var link = new Hyperlink(new Run(text));
            link.Click += delegate { Open(url); };
            t.Inlines.Add(link);
            return t;
        }

        // ── 그 밖에 ──

        UIElement OtherSection()
        {
            var stack = new StackPanel();
            var startup = Theme.Switch("윈도우를 켤 때 같이 켜기", "꺼 두면 윈도우를 켤 때마다 바꿈을 직접 열어야 메뉴가 떠요.", s.StartWithWindows, delegate (bool on)
            {
                s.StartWithWindows = on;
                Save();
            });
            stack.Children.Add(startup);
            var reveal = Theme.Switch("다 되면 결과가 있는 폴더 열기", null, s.RevealResult, delegate (bool on)
            {
                s.RevealResult = on;
                Save();
            });
            stack.Children.Add(Spaced(reveal, 14));
            stack.Children.Add(Spaced(UpdateArea(), 14));
            stack.Children.Add(Spaced(Theme.Note("결과 파일은 원본 옆에 만들어요. 원본 폴더에 쓸 수 없으면 문서 폴더의 '변환 결과'에 둬요. 원본은 바꾸지 않아요.", Theme.Green), 18));
            return stack;
        }

        StatusPill updatePill;
        Button updateCheck, updateApply;

        void ShowAvailable()
        {
            var info = Updater.Available;
            if (info == null) return;
            updatePill.Set("새 버전 " + info.Version + "이 있어요", Theme.Blue);
            updateApply.Visibility = AppInfo.RunningInstalled ? Visibility.Visible : Visibility.Collapsed;
        }

        FrameworkElement UpdateArea()
        {
            var stack = new StackPanel();
            stack.Children.Add(Theme.Switch("새 버전이 나오면 알려 주기", "하루 한 번 버전 번호만 확인해요. 문서 내용은 보내지 않아요.", s.CheckUpdates, delegate (bool on)
            {
                s.CheckUpdates = on;
                Save();
            }));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            updateCheck = Theme.MakeButton("지금 확인", "Line");
            updateApply = Theme.MakeButton("새 버전으로 바꾸기", "Primary");
            updateApply.Margin = new Thickness(8, 0, 0, 0);
            updateApply.Visibility = Visibility.Collapsed;
            var guide = Theme.MakeButton("사용법 다시 보기", "Line");
            guide.Margin = new Thickness(8, 0, 0, 0);
            guide.Click += delegate { GuideWindow.Open(); };
            updatePill = new StatusPill { Margin = new Thickness(0, 10, 0, 0) };
            updatePill.Set("", Theme.Slate);
            row.Children.Add(updateCheck);
            row.Children.Add(updateApply);
            row.Children.Add(guide);
            stack.Children.Add(row);
            stack.Children.Add(updatePill);
            ShowAvailable();
            updateApply.Click += delegate
            {
                var info = Updater.Available;
                if (info != null) Updater.Apply(info);
            };
            updateCheck.Click += delegate
            {
                updateCheck.IsEnabled = false;
                updatePill.Set("확인하는 중", Theme.Slate);
                var t = new System.Threading.Thread(delegate ()
                {
                    string problem = null;
                    UpdateInfo found = null;
                    try { found = Updater.Fetch(); }
                    catch (UserFacingException ex) { problem = ex.Message; }
                    catch (Exception ex)
                    {
                        Log.Write("새 버전 확인 실패: " + ex.GetType().Name);
                        problem = "확인하지 못했어요. 잠시 뒤에 다시 해 보세요.";
                    }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        updateCheck.IsEnabled = true;
                        if (problem != null) updatePill.Set(problem, Theme.Slate);
                        else if (found != null) ShowAvailable();
                        else updatePill.Set("지금이 최신 버전이에요", Theme.Green);
                    }));
                });
                t.IsBackground = true;
                t.Start();
            };
            return stack;
        }

        FrameworkElement Footer()
        {
            var grid = new Grid { Margin = new Thickness(26, 10, 26, 20) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var version = Theme.MetaLabel(AppInfo.Name + " " + AppInfo.Version);
            version.VerticalAlignment = VerticalAlignment.Center;
            version.Margin = new Thickness(2, 0, 10, 0);
            left.Children.Add(version);
            var notice = Theme.MakeButton("라이선스·고지", "Quiet");
            notice.Click += delegate { ShowNotice(this); };
            left.Children.Add(notice);
            if (AppInfo.RunningInstalled || Demo)
            {
                var remove = Theme.MakeButton("프로그램 지우기", "Quiet");
                remove.Margin = new Thickness(2, 0, 0, 0);
                remove.Click += delegate
                {
                    try { Process.Start(AppInfo.InstalledExe, "/uninstall"); }
                    catch (Exception ex) { Log.Write("지우기 실행 실패: " + ex.GetType().Name); }
                };
                left.Children.Add(remove);
            }
            grid.Children.Add(left);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            // 2026-11-08 사용자 시험: 설정 창을 닫으려고 [끝내기]를 눌러 바꿈이 꺼졌고, 그 뒤로 Shift를 눌러도 메뉴가 뜨지 않았다.
            // 같은 날 사용자 의견: [바꿈 끄기]와 [닫기]도 둘 다 닫는 말처럼 들려 차이를 모르겠다고 했다.
            // 그래서 하는 일이 다르게 들리는 [프로그램 종료]와 [설정 완료]로 나누고, 끄기 전에는 한 번 묻는다.
            var quit = Theme.MakeButton("프로그램 종료", "Line");
            quit.ToolTip = "바꿈을 완전히 꺼요. 파일을 끌 때 메뉴도 뜨지 않아요.";
            quit.Click += delegate
            {
                int r = Dialog.Ask(this, Theme.Coral, "close", "프로그램을 종료할까요?",
                    "종료하면 파일을 끌다가 Shift를 눌러도 메뉴가 뜨지 않아요. 설정만 마치려면 [설정 완료]를 누르세요. 다시 켜려면 바탕 화면이나 시작 메뉴에서 " + AppInfo.Name + "을 열면 돼요.",
                    null, "취소", "종료");
                if (r == 1) App.Quit();
            };
            var close = Theme.MakeButton("설정 완료", "Primary");
            close.ToolTip = "이 창만 닫아요. 바꿈은 계속 켜져 있어요.";
            close.Margin = new Thickness(10, 0, 0, 0);
            close.Click += delegate { Close(); };
            right.Children.Add(quit);
            right.Children.Add(close);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }

        static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("링크 열기 실패: " + ex.GetType().Name); }
        }

        public static string NoticeText()
        {
            try
            {
                using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Bakkum.NOTICE.txt"))
                {
                    if (st != null)
                    {
                        using (var r = new StreamReader(st)) return r.ReadToEnd();
                    }
                }
            }
            catch { }
            return "고지 파일을 찾지 못했어요.";
        }

        public static void ShowNotice(Window owner)
        {
            NoticeWindow.Show(owner);
        }
    }

    /// AI 연결 하나: 고르는 줄 + 고르면 펼쳐지는 자세한 칸.
    /// 고른 카드는 그 연결의 진한 색 테두리와 색이 비치는 그림자로 떠오른다(그림자는 글자 없는 뒤판에만).
    class ProviderRow : Grid
    {
        public readonly RadioButton Radio;
        public readonly StackPanel Details;
        readonly Border face, shade;
        readonly Hue hue;
        readonly DropShadowEffect glow;
        static readonly Brush RestLine = Theme.Frozen(Theme.Hex(0xE7E1D7));

        public ProviderRow(string id, string name, string sub, string tag, Hue hue)
        {
            this.hue = hue;
            glow = Theme.Shadow(hue.Deep, 22, 5, 0);
            shade = new Border { CornerRadius = new CornerRadius(17), Background = Theme.Sheet, Effect = glow };
            face = new Border
            {
                CornerRadius = new CornerRadius(17),
                Background = Theme.Sheet,
                BorderThickness = new Thickness(1.5),
                BorderBrush = RestLine
            };
            var header = new DockPanel();
            if (tag != null)
            {
                var t = Theme.Tag(tag, hue);
                t.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(t, Dock.Right);
                header.Children.Add(t);
            }
            var words = new StackPanel();
            words.Children.Add(Theme.Text(name, 15.5, FontWeights.Bold, Theme.Ink));
            var subText = Theme.MetaLabel(sub);
            subText.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(subText);
            header.Children.Add(words);
            Radio = new RadioButton { Style = Theme.GetStyle("Bare"), GroupName = "ai", Tag = hue, Content = header };
            Details = new StackPanel { Margin = new Thickness(52, 0, 20, 20), Visibility = Visibility.Collapsed };
            var stack = new StackPanel();
            stack.Children.Add(Radio);
            stack.Children.Add(Details);
            face.Child = stack;
            Children.Add(shade);
            Children.Add(face);
            MouseEnter += delegate { Refresh(true); };
            MouseLeave += delegate { Refresh(true); };
        }

        public void Refresh(bool animate)
        {
            bool on = Radio.IsChecked == true;
            face.BorderBrush = on || IsMouseOver ? hue.VividBrush : RestLine;
            Details.Visibility = on && Details.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            double target = on ? 0.22 : 0;
            if (!animate || Math.Abs(glow.Opacity - target) < 0.001)
            {
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                glow.Opacity = target;
                return;
            }
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }
}
