// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// AI가 고른 슬라이드 구성에 디자인을 입혀 PPT를 만든다.
// AI는 내용과 쪽 구성(JSON)만 정하고, 모양은 이 파일이 그린다. 그래서 답이 조금 어긋나도 슬라이드가 깨지지 않고,
// 글자·도형·표·색이 모두 PPT 안에서 그대로 고쳐진다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace Bakkum
{
    public sealed class DeckStyle
    {
        public string Id, Label;
        public DeckPalette Palette;
    }

    public static class DeckDesign
    {
        const string F = "맑은 고딕";
        const double W = 960, H = 540, M = 48;
        const string Bg = "@bg1", Card = "@bg2", Ink = "@tx1", Sub = "@tx2";

        public static readonly DeckStyle[] Styles =
        {
            new DeckStyle { Id = "paper", Label = "색종이", Palette = new DeckPalette { Name = "색종이", Dk1 = "2B2A33", Lt1 = "FFFBF4", Dk2 = "6B6675", Lt2 = "FFFFFF", Accents = new[] { "FF8473", "6CA8FF", "52D3A0", "FFD447", "B596FF", "FFAA4D" } } },
            new DeckStyle { Id = "board", Label = "칠판", Palette = new DeckPalette { Name = "칠판", Dk1 = "F5F3E7", Lt1 = "24423A", Dk2 = "B9CBBE", Lt2 = "2F5449", Accents = new[] { "F7D774", "F4A6A0", "9BD5E8", "B6E2A1", "C99A62", "D4B5F0" } } },
            new DeckStyle { Id = "neat", Label = "단정", Palette = new DeckPalette { Name = "단정", Dk1 = "1E2A3A", Lt1 = "FFFFFF", Dk2 = "5B6B7D", Lt2 = "F1F4F8", Accents = new[] { "1F4E8C", "2A9D8F", "E9A23B", "C8553D", "6C7A89", "7B5EA7" } } },
            new DeckStyle { Id = "pastel", Label = "파스텔", Palette = new DeckPalette { Name = "파스텔", Dk1 = "3A3550", Lt1 = "FDFBFF", Dk2 = "7A7592", Lt2 = "F2EEFB", Accents = new[] { "B79CED", "8ED1C2", "F6B8C8", "F9D784", "9CC9F5", "F3B391" } } }
        };

        public static DeckStyle StyleOf(string id)
        {
            return Styles.FirstOrDefault(s => s.Id == id) ?? Styles[0];
        }

        static readonly string[] Icons = { "bulb", "people", "clock", "flag", "target", "pencil", "chat", "question", "book", "star", "check", "link", "shield", "spark", "info", "face", "image" };

        /// AI에게 보낼 지시문
        public static string Instruction(string count)
        {
            string how = count == "auto" || string.IsNullOrEmpty(count) ? "Use 8 to 12 slides after the cover." : "Use exactly " + count + " slides after the cover.";
            return "Create a slide outline in Korean for a class, based on this document, and choose a layout for each slide. Reply with JSON only, in this shape: " +
                "{\"title\":\"...\",\"subtitle\":\"...\",\"note\":\"...\",\"slides\":[{\"layout\":\"...\",\"title\":\"...\",\"note\":\"...\"}]}. " +
                how + " Each slide object also has the fields of its layout:\n" +
                "goals: items (2 to 4 learning goals, strings)\n" +
                "section: subtitle (opens a new part; use only when the deck has clear parts)\n" +
                "bullets: items (3 to 5 strings), icon\n" +
                "cards: cards (2 to 4 objects with head, body, icon), for parallel ideas\n" +
                "steps: items (3 to 5 objects with head, body), for an ordered procedure\n" +
                "timeline: items (3 to 5 objects with when, head, body), for dates or a sequence over time\n" +
                "compare: left and right (objects with head, items), for two things side by side\n" +
                "big: value, label, body, for one striking number or fact stated in the document\n" +
                "quote: text, by, for a sentence quoted from the document\n" +
                "table: rows (array of arrays of short strings; the first row is the header; 2 to 5 columns; at most 6 rows)\n" +
                "bars: items (objects with label, value as a number), unit; only when the document gives the numbers\n" +
                "activity: minutes, mode (one of 개인, 짝, 모둠, 전체), items (2 to 4 instructions for students)\n" +
                "question: text, hint, for a question students discuss\n" +
                "summary: items (3 or 4 key points to remember)\n" +
                "icon is one of: " + string.Join(", ", Icons) + ".\n" +
                "Pick the layout that fits what each slide says and vary them: no more than a third of the slides may use bullets, and do not use the same layout three times in a row. " +
                "When the document is teaching material, begin with goals and end with summary or question. Use activity or question where students should do something. " +
                "note is one or two sentences the teacher can say while showing the slide. " +
                "Keep text short: slide title under 24 Korean characters, head under 14, list item under 40, body under 70, table cell under 16.";
        }

        // ── 색 ──

        static double Luminance(string hex)
        {
            Func<int, double> f = i =>
            {
                double c = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            };
            return 0.2126 * f(0) + 0.7152 * f(2) + 0.0722 * f(4);
        }

        static double Contrast(string a, string b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        sealed class Kit
        {
            public DeckStyle St;
            public DeckPage P;

            string Acc(int i)
            {
                return St.Palette.Accents[Math.Abs(i) % 4];
            }

            public string A(int i)
            {
                return "@accent" + (Math.Abs(i) % 4 + 1);
            }

            /// 이 색 면 위에 쓸 글자 색
            public string On(int i)
            {
                string c = Acc(i);
                return Contrast(c, St.Palette.Dk1) >= Contrast(c, St.Palette.Lt1) ? Ink : Bg;
            }

            public string Tint(int i)
            {
                return St.Id == "board" ? Card : A(i) + ":25";
            }

            public static long E(double pt)
            {
                return DeckWriter.Emu(pt);
            }

            public DeckShape Shape(string geom, double x, double y, double w, double h, string fill)
            {
                var s = new DeckShape { Geometry = geom, X = E(x), Y = E(y), W = E(w), H = E(h), Fill = fill };
                P.Items.Add(s);
                return s;
            }

            public DeckShape Round(double x, double y, double w, double h, string fill, double radius)
            {
                var s = Shape("roundRect", x, y, w, h, fill);
                s.Adjust = new Dictionary<string, long> { { "adj", (long)Math.Min(50000, radius / Math.Min(w, h) * 100000) } };
                return s;
            }

            public DeckPara Para(string text, double size, string color, bool bold, string align)
            {
                var p = DeckPara.Of(text, size, color, bold);
                p.Align = align;
                p.LinePct = 100;
                return p;
            }

            public DeckShape Text(double x, double y, double w, double h, string text, double size, string color, bool bold, string align, string anchor)
            {
                var s = new DeckShape { IsTextBox = true, X = E(x), Y = E(y), W = E(w), H = E(h), Anchor = anchor, Fit = "norm" };
                s.Paras.Add(Para(text ?? "", size, color, bold, align));
                P.Items.Add(s);
                return s;
            }

            /// 상자에 들어가는 크기로 글을 넣는다.
            public DeckShape Fitted(double x, double y, double w, double h, string text, double max, double min, string color, bool bold, string align, string anchor)
            {
                return Text(x, y, w, h, text, DeckFonts.Fit(text ?? "", F, bold, w, h, max, min, 100), color, bold, align, anchor);
            }

            /// 굵은 머리글 바로 아래에 설명을 잇는다. 머리글이 한 줄이면 설명이 그만큼 올라온다.
            public void HeadBody(double x, double y, double w, double h, string head, string body, double headMax, double bodyMax, string align, string bodyColor, double shift = 0)
            {
                double hs = DeckFonts.Fit(head ?? "", F, true, w, Math.Min(h * 0.4, headMax * 2.8), headMax, 15, 100);
                double hh = Math.Max(hs * 1.4, DeckFonts.Height(head ?? "", F, hs, true, w, 100));
                Text(x, y + shift, w, hh, head, hs, Ink, true, align, "t");
                if (string.IsNullOrWhiteSpace(body)) return;
                double size = DeckFonts.Fit(body, F, false, w, h - hh - 8, bodyMax, 13, 100);
                Text(x, y + shift + hh + 8, w, h - hh - 8 - shift, body, size, bodyColor, false, align, "t");
            }

            /// 머리글과 설명이 실제로 차지하는 높이. 짧은 글을 카드 가운데로 모을 때 쓴다.
            public double StackHeight(double w, double h, string head, string body, double headMax, double bodyMax)
            {
                double hs = DeckFonts.Fit(head ?? "", F, true, w, Math.Min(h * 0.4, headMax * 2.8), headMax, 15, 100);
                double hh = Math.Max(hs * 1.4, DeckFonts.Height(head ?? "", F, hs, true, w, 100));
                if (string.IsNullOrWhiteSpace(body)) return hh;
                double room = h - hh - 8;
                double size = DeckFonts.Fit(body, F, false, w, room, bodyMax, 13, 100);
                return hh + 8 + Math.Min(room, DeckFonts.Height(body, F, size, false, w, 100));
            }

            public void Icon(string name, double x, double y, double size, string color, double weight)
            {
                var s = new DeckShape { X = E(x), Y = E(y), W = E(size), H = E(size), PathXml = IconPath(name), Line = color, LineWidth = weight, RoundCap = true, Name = "아이콘" };
                P.Items.Add(s);
            }

            /// 색 동그라미 안의 아이콘
            public void Badge(string icon, double x, double y, double size, int accent)
            {
                Shape("ellipse", x, y, size, size, A(accent));
                double pad = size * 0.24;
                Icon(icon, x + pad, y + pad, size - 2 * pad, On(accent), Math.Max(1.5, size / 26));
            }

            public void Number(int n, double x, double y, double size, int accent)
            {
                var s = Shape("ellipse", x, y, size, size, A(accent));
                s.Anchor = "ctr";
                s.Paras.Add(Para(n.ToString(CultureInfo.InvariantCulture), size * 0.45, On(accent), true, "ctr"));
            }
        }

        static string IconPath(string name)
        {
            var g = Theme.GlyphGeometry(name).GetFlattenedPathGeometry(0.08, ToleranceType.Absolute);
            var sb = new StringBuilder("<a:path w=\"2400\" h=\"2400\" fill=\"none\">");
            Action<string, System.Windows.Point> pt = (tag, p) =>
            {
                sb.Append("<a:").Append(tag).Append("><a:pt x=\"").Append((int)Math.Round(p.X * 100)).Append("\" y=\"").Append((int)Math.Round(p.Y * 100)).Append("\"/></a:").Append(tag).Append('>');
            };
            foreach (var fig in g.Figures)
            {
                pt("moveTo", fig.StartPoint);
                foreach (var seg in fig.Segments)
                {
                    var poly = seg as PolyLineSegment;
                    var line = seg as LineSegment;
                    if (poly != null)
                    {
                        foreach (var p in poly.Points) pt("lnTo", p);
                    }
                    else if (line != null) pt("lnTo", line.Point);
                }
                if (fig.IsClosed) sb.Append("<a:close/>");
            }
            return sb.Append("</a:path>").ToString();
        }

        // ── AI 답 읽기 ──

        static string Str(Dictionary<string, object> d, string k)
        {
            if (d == null) return "";
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return "";
            return Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
        }

        static List<string> Strs(Dictionary<string, object> d, string k)
        {
            var list = new List<string>();
            var a = Json.A(d, k);
            if (a == null) return list;
            foreach (object o in a)
            {
                string s = o as string;
                var obj = o as Dictionary<string, object>;
                if (obj != null) s = string.Join(" ", new[] { Str(obj, "head"), Str(obj, "text"), Str(obj, "body") }.Where(x => x.Length > 0));
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }
            return list;
        }

        static List<Dictionary<string, object>> Objs(Dictionary<string, object> d, string k)
        {
            var list = new List<Dictionary<string, object>>();
            var a = Json.A(d, k);
            if (a == null) return list;
            foreach (object o in a)
            {
                var obj = o as Dictionary<string, object>;
                if (obj != null) list.Add(obj);
                else if (o is string && ((string)o).Trim().Length > 0) list.Add(new Dictionary<string, object> { { "head", ((string)o).Trim() } });
            }
            return list;
        }

        static string IconOf(Dictionary<string, object> d, string fallback)
        {
            string s = Str(d, "icon").ToLowerInvariant();
            return Array.IndexOf(Icons, s) >= 0 ? s : fallback;
        }

        static double Number(object o)
        {
            try
            {
                string s = o as string;
                if (s != null) return double.Parse(s.Replace(",", "").Trim(), CultureInfo.InvariantCulture);
                return Convert.ToDouble(o, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return double.NaN;
            }
            catch (InvalidCastException)
            {
                return double.NaN;
            }
        }

        // ── 틀 ──

        static void Title(Kit k, string text)
        {
            double x = M, w = W - 2 * M;
            string color = Ink;
            switch (k.St.Id)
            {
                case "paper":
                    // 제목 앞에 비스듬히 붙인 색종이 한 조각
                    k.Round(M, 42, 22, 22, k.A(1), 4).Rotation = -9;
                    k.Round(M + 8, 36, 22, 22, k.A(0), 4).Rotation = 7;
                    x = M + 46;
                    w -= 46;
                    break;
                case "board":
                    k.Shape("rect", 0, 526, W, 14, "@accent5");
                    color = k.A(0);
                    break;
                case "neat":
                    k.Shape("rect", 0, 0, 14, H, k.A(0));
                    break;
                default:
                    k.Shape("ellipse", 852, 18, 76, 76, k.A(0) + ":40");
                    k.Shape("ellipse", 806, 50, 40, 40, k.A(1) + ":40");
                    w -= 130;
                    break;
            }
            double size = DeckFonts.Fit(text, F, true, w, 56, 32, 22, 100);
            var s = new DeckShape { IsTitle = true, X = Kit.E(x), Y = Kit.E(30), W = Kit.E(w), H = Kit.E(62), Anchor = "ctr", Fit = "norm", Name = "제목" };
            s.Paras.Add(k.Para(text, size, color, true, "l"));
            k.P.Items.Add(s);
        }

        static void PageNumber(Kit k, int n)
        {
            var s = new DeckShape { IsTextBox = true, X = Kit.E(W - M - 60), Y = Kit.E(504), W = Kit.E(60), H = Kit.E(18), Anchor = "ctr", Name = "쪽 번호" };
            var p = new DeckPara { Align = "r" };
            p.Runs.Add(new DeckRun { Text = n.ToString(CultureInfo.InvariantCulture), Size = 11, Color = Sub, SlideNumber = true });
            s.Paras.Add(p);
            k.P.Items.Add(s);
        }

        static void Cover(Kit k, string title, string subtitle)
        {
            double textX = M, textW = 540, top = 150;
            string align = "l";
            switch (k.St.Id)
            {
                case "paper":
                    k.Round(628, 74, 262, 360, k.A(3), 22).Rotation = -9;
                    k.Round(644, 92, 262, 360, k.A(1), 22).Rotation = 6;
                    k.Round(642, 82, 262, 360, k.A(0), 22).Rotation = -2;
                    k.Icon("book", 716, 202, 116, k.On(0), 4);
                    break;
                case "board":
                    k.Shape("rect", 0, 516, W, 24, "@accent5");
                    k.Round(690, 504, 74, 12, k.A(0), 5);
                    k.Round(776, 504, 54, 12, k.A(1), 5);
                    var frame = k.Round(36, 36, W - 72, 456, null, 20);
                    frame.Line = Sub;
                    frame.LineWidth = 2;
                    frame.Dash = "dash";
                    textX = 110;
                    textW = W - 220;
                    align = "ctr";
                    break;
                case "neat":
                    k.Shape("rect", 0, 0, 330, H, k.A(0));
                    k.Icon("book", 113, 198, 104, k.On(0), 3.5);
                    textX = 382;
                    textW = 530;
                    break;
                default:
                    k.Shape("ellipse", 610, 110, 320, 320, k.A(0) + ":40");
                    k.Shape("ellipse", 770, 30, 160, 160, k.A(2) + ":40");
                    k.Shape("ellipse", 560, 370, 130, 130, k.A(3) + ":40");
                    k.Icon("book", 716, 216, 108, k.A(0), 3.5);
                    break;
            }
            double size = DeckFonts.Fit(title, F, true, textW, 170, 48, 30, 100);
            string color = k.St.Id == "board" ? k.A(0) : Ink;
            var s = new DeckShape { IsTitle = true, X = Kit.E(textX), Y = Kit.E(top), W = Kit.E(textW), H = Kit.E(170), Anchor = "b", Fit = "norm", Name = "제목" };
            s.Paras.Add(k.Para(title, size, color, true, align));
            k.P.Items.Add(s);
            if (subtitle.Length > 0) k.Fitted(textX, top + 186, textW, 90, subtitle, 22, 15, Sub, false, align, "t");
        }

        // ── 쪽 모양들 ──

        static void Section(Kit k, string title, string subtitle, int number)
        {
            bool filled = k.St.Id != "board";
            string ink = filled ? k.On(0) : k.A(0);
            if (filled) k.P.Background = k.St.Id == "pastel" ? k.A(0) + ":40" : k.A(0);
            else k.Shape("rect", 0, 526, W, 14, "@accent5");
            if (k.St.Id == "pastel") ink = Ink;
            k.Text(M, 96, 300, 150, number.ToString("00", CultureInfo.InvariantCulture), 110, ink, true, "l", "b");
            var s = new DeckShape { IsTitle = true, X = Kit.E(M), Y = Kit.E(262), W = Kit.E(W - 2 * M), H = Kit.E(90), Anchor = "t", Fit = "norm", Name = "제목" };
            s.Paras.Add(k.Para(title, DeckFonts.Fit(title, F, true, W - 2 * M, 84, 46, 30, 100), ink, true, "l"));
            k.P.Items.Add(s);
            if (subtitle.Length > 0) k.Fitted(M, 356, W - 2 * M, 70, subtitle, 22, 15, ink, false, "l", "t");
        }

        static bool Rows(Kit k, List<string> items, Func<int, string> icon, bool numbered)
        {
            if (items.Count == 0) return false;
            int n = Math.Min(items.Count, 5);
            double top = 112, gap = 12, h = Math.Min(92, (374 - gap * (n - 1)) / n);
            double total = n * h + (n - 1) * gap;
            top += (374 - total) / 2;
            for (int i = 0; i < n; i++)
            {
                double y = top + i * (h + gap);
                k.Round(M, y, W - 2 * M, h, Card, 14);
                double size = h - 24;
                if (numbered) k.Number(i + 1, M + 16, y + 12, size, i);
                else k.Badge(icon(i), M + 16, y + 12, size, i);
                k.Fitted(M + 32 + size, y + 8, W - 2 * M - 56 - size, h - 16, items[i], 26, 15, Ink, false, "l", "ctr");
            }
            return true;
        }

        static bool Bullets(Kit k, List<string> items, string icon)
        {
            if (items.Count == 0) return false;
            var list = items.Take(6).ToList();
            double bw = 576, boxH = 372;
            string all = string.Join("\n", list);
            double size = 28;
            while (size > 15 && DeckFonts.Height(all, F, size, false, bw - 30, 100) + (list.Count - 1) * size * 0.7 > boxH) size -= 1;
            var s = new DeckShape { IsTextBox = true, X = Kit.E(M), Y = Kit.E(116), W = Kit.E(bw), H = Kit.E(boxH), Anchor = "ctr", Fit = "norm" };
            for (int i = 0; i < list.Count; i++)
            {
                var p = k.Para(list[i], size, Ink, false, "l");
                p.Bullet = "•";
                p.BulletColor = k.A(0);
                p.MarL = Kit.E(size * 1.1);
                p.Indent = -Kit.E(size * 1.1);
                if (i > 0) p.Before = size * 0.7;
                s.Paras.Add(p);
            }
            k.P.Items.Add(s);
            k.Shape("ellipse", 662, 140, 250, 250, k.Tint(0));
            k.Icon(icon, 725, 203, 124, k.St.Id == "board" ? k.A(0) : k.A(0) + "*75", 4);
            return true;
        }

        static bool Cards(Kit k, List<Dictionary<string, object>> cards)
        {
            if (cards.Count < 2) return false;
            int n = Math.Min(cards.Count, 4);
            double gap = 18, w = (W - 2 * M - gap * (n - 1)) / n, top = 118, h = 364;
            double headMax = n <= 3 ? 30 : 26, bodyMax = n <= 3 ? 25 : 21, room = h - 128, used = 0;
            for (int i = 0; i < n; i++) used = Math.Max(used, k.StackHeight(w - 44, room, Str(cards[i], "head"), Str(cards[i], "body"), headMax, bodyMax));
            double shift = (room - used) / 2;
            string[] fallback = { "bulb", "star", "target", "flag" };
            for (int i = 0; i < n; i++)
            {
                double x = M + i * (w + gap);
                k.Round(x, top, w, h, Card, 18);
                k.Badge(IconOf(cards[i], fallback[i]), x + 22, top + 22 + shift, 72, i);
                k.HeadBody(x + 22, top + 110, w - 44, room, Str(cards[i], "head"), Str(cards[i], "body"), headMax, bodyMax, "l", Sub, shift);
            }
            return true;
        }

        static bool Steps(Kit k, List<Dictionary<string, object>> items)
        {
            if (items.Count < 2) return false;
            int n = Math.Min(items.Count, 5);
            double gap = 16, w = (W - 2 * M - gap * (n - 1)) / n, circle = 62, cy = 132;
            double headMax = n <= 3 ? 30 : 26, bodyMax = n <= 3 ? 24 : 21, used = 0;
            for (int i = 0; i < n; i++) used = Math.Max(used, k.StackHeight(w - 28, 240, Str(items[i], "head"), Str(items[i], "body"), headMax, bodyMax));
            double shift = (240 - used) / 2;
            k.Shape("rect", M + w / 2, cy + circle / 2 - 2, (n - 1) * (w + gap), 4, k.Tint(0));
            for (int i = 0; i < n; i++)
            {
                double x = M + i * (w + gap);
                k.Number(i + 1, x + (w - circle) / 2, cy, circle, i);
                k.Round(x, cy + 84, w, 268, Card, 16);
                k.HeadBody(x + 14, cy + 100, w - 28, 240, Str(items[i], "head"), Str(items[i], "body"), headMax, bodyMax, "ctr", Sub, shift);
            }
            return true;
        }

        static bool Timeline(Kit k, List<Dictionary<string, object>> items)
        {
            if (items.Count < 2) return false;
            int n = Math.Min(items.Count, 5);
            double gap = 16, w = (W - 2 * M - gap * (n - 1)) / n, line = 182;
            double headMax = n <= 3 ? 30 : 26, bodyMax = n <= 3 ? 24 : 21, used = 0;
            for (int i = 0; i < n; i++) used = Math.Max(used, k.StackHeight(w - 28, 248, Str(items[i], "head"), Str(items[i], "body"), headMax, bodyMax));
            double shift = (248 - used) / 2;
            k.Shape("rect", M, line - 3, W - 2 * M, 6, k.Tint(0));
            for (int i = 0; i < n; i++)
            {
                double x = M + i * (w + gap);
                var when = k.Round(x + 6, 112, w - 12, 44, k.A(i), 22);
                when.Anchor = "ctr";
                string text = Str(items[i], "when");
                when.Paras.Add(k.Para(text, DeckFonts.Fit(text, F, true, w - 28, 36, 22, 12, 100), k.On(i), true, "ctr"));
                k.Shape("ellipse", x + w / 2 - 12, line - 12, 24, 24, k.A(i));
                k.Round(x, line + 26, w, 276, Card, 16);
                k.HeadBody(x + 14, line + 42, w - 28, 248, Str(items[i], "head"), Str(items[i], "body"), headMax, bodyMax, "ctr", Sub, shift);
            }
            return true;
        }

        static bool Compare(Kit k, Dictionary<string, object> s)
        {
            var left = Json.D(s, "left");
            var right = Json.D(s, "right");
            if (left == null || right == null) return false;
            double gap = 24, w = (W - 2 * M - gap) / 2, top = 118, h = 366;
            for (int side = 0; side < 2; side++)
            {
                var d = side == 0 ? left : right;
                double x = M + side * (w + gap);
                k.Round(x, top, w, h, Card, 18);
                var band = k.Round(x + 16, top + 16, w - 32, 56, k.A(side), 14);
                band.Anchor = "ctr";
                string head = Str(d, "head");
                band.Paras.Add(k.Para(head, DeckFonts.Fit(head, F, true, w - 60, 44, 26, 15, 100), k.On(side), true, "ctr"));
                var items = Strs(d, "items").Take(6).ToList();
                string all = string.Join("\n", items);
                double size = 27;
                while (size > 13 && DeckFonts.Height(all, F, size, false, w - 76, 100) + (items.Count - 1) * size * 0.55 > h - 112) size -= 1;
                var box = new DeckShape { IsTextBox = true, X = Kit.E(x + 26), Y = Kit.E(top + 88), W = Kit.E(w - 52), H = Kit.E(h - 104), Anchor = "ctr", Fit = "norm" };
                for (int i = 0; i < items.Count; i++)
                {
                    var p = k.Para(items[i], size, Ink, false, "l");
                    p.Bullet = "•";
                    p.BulletColor = k.A(side);
                    p.MarL = Kit.E(size);
                    p.Indent = -Kit.E(size);
                    if (i > 0) p.Before = size * 0.55;
                    box.Paras.Add(p);
                }
                k.P.Items.Add(box);
            }
            return true;
        }

        static bool Big(Kit k, Dictionary<string, object> s)
        {
            string value = Str(s, "value");
            if (value.Length == 0) return false;
            k.Round(M, 124, 440, 352, k.A(0), 22);
            // 글꼴 폭은 프로그램마다 조금씩 달라서, 좁혀 잰 크기로 쓴다.
            k.Text(M + 24, 148, 392, 304, value, DeckFonts.Fit(value, F, true, 392 * 0.92, 304, 120, 40, 100), k.On(0), true, "ctr", "ctr");
            k.Fitted(520, 150, W - M - 520, 120, Str(s, "label"), 34, 20, Ink, true, "l", "b");
            k.Fitted(520, 286, W - M - 520, 180, Str(s, "body"), 22, 14, Sub, false, "l", "t");
            return true;
        }

        static bool Quote(Kit k, Dictionary<string, object> s)
        {
            string text = Str(s, "text");
            if (text.Length == 0) return false;
            k.Round(M, 118, W - 2 * M, 366, Card, 22);
            k.Text(M + 30, 122, 120, 120, "“", 130, k.A(0), true, "l", "t");
            k.Fitted(M + 90, 196, W - 2 * M - 180, 190, text, 34, 18, Ink, true, "ctr", "ctr");
            k.Fitted(M + 90, 404, W - 2 * M - 180, 50, Str(s, "by"), 20, 14, Sub, false, "ctr", "ctr");
            return true;
        }

        static bool Table(Kit k, Dictionary<string, object> s)
        {
            var a = Json.A(s, "rows");
            if (a == null || a.Length < 2) return false;
            var rows = new List<List<string>>();
            foreach (object o in a.Take(7))
            {
                var r = o as object[];
                if (r == null) continue;
                rows.Add(r.Take(6).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? "").ToList());
            }
            if (rows.Count < 2) return false;
            int cols = rows.Max(r => r.Count);
            if (cols < 1) return false;
            double tw = W - 2 * M, rh = Math.Min(84, 366.0 / rows.Count);
            double cw = tw / cols - 18;
            double size = 26;
            Func<double, bool> fits = sz => rows.All(r => r.All(c => DeckFonts.Height(c, F, sz, false, cw, 100) <= rh - 12));
            while (size > 12 && !fits(size)) size -= 1;
            var t = new DeckTable { X = Kit.E(M), Y = Kit.E(118), W = Kit.E(tw), H = Kit.E(rh * rows.Count), LineColor = k.St.Id == "board" ? Sub : "@tx2:40" };
            t.Cols = Enumerable.Repeat(Kit.E(tw / cols), cols).ToArray();
            t.Rows = Enumerable.Repeat(Kit.E(rh), rows.Count).ToArray();
            for (int r = 0; r < rows.Count; r++)
            {
                var line = new List<DeckCell>();
                for (int c = 0; c < cols; c++)
                {
                    var cell = new DeckCell { Fill = r == 0 ? k.A(0) : r % 2 == 0 ? Card : null };
                    string text = c < rows[r].Count ? rows[r][c] : "";
                    cell.Paras.Add(k.Para(text, size, r == 0 ? k.On(0) : Ink, r == 0, cols > 2 && c > 0 ? "ctr" : "l"));
                    line.Add(cell);
                }
                t.Cells.Add(line);
            }
            k.P.Items.Add(t);
            return true;
        }

        static bool Bars(Kit k, Dictionary<string, object> s)
        {
            var items = Objs(s, "items").Select(o => new { Label = Str(o, "label"), Value = o.ContainsKey("value") ? Number(o["value"]) : double.NaN }).Where(x => x.Label.Length > 0 && !double.IsNaN(x.Value) && x.Value >= 0).Take(7).ToList();
            if (items.Count < 2) return false;
            string unit = Str(s, "unit");
            double most = Math.Max(1e-9, items.Max(x => x.Value));
            int n = items.Count;
            double gap = 14, h = Math.Min(72, (358 - gap * (n - 1)) / n), labelW = 210, barMax = W - 2 * M - labelW - 140;
            double top = 122 + (358 - (n * h + (n - 1) * gap)) / 2;
            for (int i = 0; i < n; i++)
            {
                double y = top + i * (h + gap);
                k.Fitted(M, y, labelW - 14, h, items[i].Label, 26, 13, Ink, false, "r", "ctr");
                k.Round(M + labelW, y + 6, barMax, h - 12, k.Tint(0), 8);
                double bar = Math.Max(10, barMax * items[i].Value / most);
                k.Round(M + labelW, y + 6, bar, h - 12, k.A(0), 8);
                string text = items[i].Value.ToString("0.##", CultureInfo.InvariantCulture) + unit;
                k.Fitted(M + labelW + barMax + 10, y, 130, h, text, 26, 13, Ink, true, "l", "ctr");
            }
            return true;
        }

        static bool Activity(Kit k, Dictionary<string, object> s)
        {
            var items = Strs(s, "items");
            if (items.Count == 0) return false;
            double panel = 270;
            k.Round(M, 118, panel, 366, k.A(0), 22);
            k.Icon(Str(s, "mode") == "모둠" || Str(s, "mode") == "짝" ? "people" : "clock", M + (panel - 84) / 2, 150, 84, k.On(0), 3.5);
            string minutes = Str(s, "minutes");
            if (minutes.Length > 0 && char.IsDigit(minutes[minutes.Length - 1])) minutes += "분";
            k.Fitted(M + 16, 248, panel - 32, 90, minutes, 56, 26, k.On(0), true, "ctr", "ctr");
            string mode = Str(s, "mode");
            if (mode.Length > 0 && !mode.EndsWith("활동", StringComparison.Ordinal)) mode += " 활동";
            k.Fitted(M + 16, 348, panel - 32, 60, mode, 26, 15, k.On(0), false, "ctr", "ctr");
            int n = Math.Min(items.Count, 5);
            double x = M + panel + 20, w = W - M - x, gap = 12, h = (366 - gap * (n - 1)) / n;
            if (n <= 3) h = Math.Min(h, 110);
            double top = 118 + (366 - (n * h + (n - 1) * gap)) / 2;
            for (int i = 0; i < n; i++)
            {
                double y = top + i * (h + gap);
                k.Round(x, y, w, h, Card, 14);
                double size = Math.Min(54, h - 22);
                k.Number(i + 1, x + 14, y + (h - size) / 2, size, 1);
                k.Fitted(x + 28 + size, y + 8, w - 44 - size, h - 16, items[i], 24, 14, Ink, false, "l", "ctr");
            }
            return true;
        }

        static bool Question(Kit k, Dictionary<string, object> s)
        {
            string text = Str(s, "text");
            if (text.Length == 0) return false;
            k.Round(M, 118, W - 2 * M, 366, k.Tint(1), 22);
            k.Badge("question", (W - 84) / 2, 140, 84, 1);
            k.Fitted(M + 60, 240, W - 2 * M - 120, 150, text, 38, 20, Ink, true, "ctr", "ctr");
            string hint = Str(s, "hint");
            if (hint.Length > 0) k.Fitted(M + 60, 400, W - 2 * M - 120, 66, hint, 20, 13, Sub, false, "ctr", "ctr");
            return true;
        }

        static bool Summary(Kit k, List<string> items)
        {
            if (items.Count == 0) return false;
            if (items.Count == 1 || items.Count > 4) return Rows(k, items, i => "check", false);
            int cols = 2, rows = (items.Count + 1) / 2;
            double gap = 18, w = (W - 2 * M - gap) / cols, h = (366 - gap * (rows - 1)) / rows;
            for (int i = 0; i < items.Count; i++)
            {
                double x = M + (i % cols) * (w + gap), y = 118 + (i / cols) * (h + gap);
                k.Round(x, y, w, h, Card, 18);
                double size = Math.Min(62, h - 40);
                k.Badge("check", x + 20, y + (h - size) / 2, size, i);
                k.Fitted(x + 38 + size, y + 14, w - 58 - size, h - 28, items[i], 28, 14, Ink, true, "l", "ctr");
            }
            return true;
        }

        static List<string> AnyText(Dictionary<string, object> s)
        {
            var list = new List<string>();
            foreach (string key in new[] { "items", "bullets", "cards" }) list.AddRange(Strs(s, key));
            foreach (string key in new[] { "text", "body", "label", "value", "hint", "subtitle" })
            {
                string v = Str(s, key);
                if (v.Length > 0) list.Add(v);
            }
            foreach (string key in new[] { "left", "right" })
            {
                var d = Json.D(s, key);
                if (d == null) continue;
                string head = Str(d, "head");
                foreach (string item in Strs(d, "items")) list.Add(head.Length > 0 ? head + ": " + item : item);
            }
            var rows = Json.A(s, "rows");
            if (rows != null)
            {
                foreach (object r in rows)
                {
                    var cells = r as object[];
                    if (cells != null) list.Add(string.Join(" · ", cells.Select(c => Convert.ToString(c, CultureInfo.InvariantCulture))));
                }
            }
            return list;
        }

        /// AI 답(JSON)을 슬라이드로. 쪽 모양에 맞지 않는 답은 글머리표 쪽으로 돌린다.
        public static Deck Build(Dictionary<string, object> d, string fallbackTitle, DeckStyle style)
        {
            var deck = new Deck { Cx = 12192000, Cy = 6858000, Palette = style.Palette, Font = F };
            var cover = new DeckPage { Notes = Str(d, "note") };
            deck.Pages.Add(cover);
            string title = Str(d, "title");
            Cover(new Kit { St = style, P = cover }, title.Length > 0 ? title : fallbackTitle, Str(d, "subtitle"));
            int section = 0;
            foreach (object o in Json.A(d, "slides") ?? new object[0])
            {
                var s = Json.Obj(o);
                if (s == null) continue;
                var page = new DeckPage { Notes = Str(s, "note") };
                deck.Pages.Add(page);
                var k = new Kit { St = style, P = page };
                string layout = Str(s, "layout").ToLowerInvariant();
                string head = Str(s, "title");
                if (layout == "section")
                {
                    Section(k, head, Str(s, "subtitle"), ++section);
                    continue;
                }
                Title(k, head);
                bool done = false;
                switch (layout)
                {
                    case "goals": done = Rows(k, Strs(s, "items").Take(4).ToList(), i => "target", false); break;
                    case "cards": done = Cards(k, Objs(s, "cards")); break;
                    case "steps": done = Steps(k, Objs(s, "items")); break;
                    case "timeline": done = Timeline(k, Objs(s, "items")); break;
                    case "compare": done = Compare(k, s); break;
                    case "big": done = Big(k, s); break;
                    case "quote": done = Quote(k, s); break;
                    case "table": done = Table(k, s); break;
                    case "bars": done = Bars(k, s); break;
                    case "activity": done = Activity(k, s); break;
                    case "question": done = Question(k, s); break;
                    case "summary": done = Summary(k, Strs(s, "items")); break;
                }
                if (!done) Bullets(k, AnyText(s), IconOf(s, "bulb"));
                PageNumber(k, deck.Pages.Count);
            }
            return deck;
        }
    }
}
