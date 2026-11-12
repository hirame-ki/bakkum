// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 위에 얹는 것(글자·자막·그림·스티커·도형·작은 화면 테두리)을 WPF로 그린다.
// 크기와 자리는 모두 화면에 대한 비율이라, 미리 보기(화면 크기)와 내보내기(영상 크기)가 같은 모습으로 나온다.
// 스티커는 이 파일에서 직접 그린 단순한 도형이라 따로 허락받을 것이 없다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    static class VOverlay
    {
        static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

        public sealed class TextLayout
        {
            public List<string> Lines = new List<string>();
            public double[] Widths = new double[0];
            public double FontPx, LineH, BlockW, BlockH;
            public Typeface Face;
        }

        static readonly Dictionary<string, TextLayout> Layouts = new Dictionary<string, TextLayout>();
        static readonly Dictionary<string, double> Widths = new Dictionary<string, double>();

        public static Typeface FaceOf(VItem it)
        {
            return new Typeface(new FontFamily(string.IsNullOrEmpty(it.Font) ? "맑은 고딕" : it.Font), FontStyles.Normal, it.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        }

        static double Measure(string s, Typeface face, double px)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            string key = face.FontFamily.Source + "|" + face.Weight + "|" + px.ToString("F1") + "|" + s;
            lock (Widths)
            {
                double w;
                if (Widths.TryGetValue(key, out w)) return w;
                w = new FormattedText(s, Ko, FlowDirection.LeftToRight, face, px, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
                if (Widths.Count > 4000) Widths.Clear();
                Widths[key] = w;
                return w;
            }
        }

        /// 줄 바꿈은 띄어쓰기에서(낱말을 자르지 않는다). 한 낱말이 너무 길 때만 글자 단위로 자른다.
        public static List<string> Wrap(string text, Typeface face, double px, double maxW)
        {
            var result = new List<string>();
            foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var words = para.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string line = "";
                foreach (var w in words)
                {
                    string cand = line.Length == 0 ? w : line + " " + w;
                    if (Measure(cand, face, px) <= maxW || line.Length == 0 && Measure(w, face, px) <= maxW)
                    {
                        line = cand;
                        continue;
                    }
                    if (line.Length > 0) result.Add(line);
                    line = "";
                    if (Measure(w, face, px) <= maxW)
                    {
                        line = w;
                        continue;
                    }
                    foreach (char ch in w)
                    {
                        if (line.Length > 0 && Measure(line + ch, face, px) > maxW)
                        {
                            result.Add(line);
                            line = "";
                        }
                        line += ch;
                    }
                }
                result.Add(line);
            }
            if (result.Count == 0) result.Add("");
            return result;
        }

        public static TextLayout Layout(VItem it, double W, double H)
        {
            double px = Math.Max(4, it.Size * Math.Min(W, H));
            double maxW = Math.Max(px * 1.5, it.W * W);
            string key = it.Text + "\u0001" + it.Font + "|" + it.Bold + "|" + px.ToString("F2") + "|" + maxW.ToString("F1");
            lock (Layouts)
            {
                TextLayout l;
                if (Layouts.TryGetValue(key, out l)) return l;
            }
            var face = FaceOf(it);
            var lay = new TextLayout { Face = face, FontPx = px, LineH = px * 1.3 };
            lay.Lines = Wrap(it.Text, face, px, maxW);
            lay.Widths = lay.Lines.Select(s => Measure(s.TrimEnd(), face, px)).ToArray();
            lay.BlockW = Math.Max(px * 0.6, lay.Widths.Length == 0 ? 0 : lay.Widths.Max());
            lay.BlockH = lay.LineH * lay.Lines.Count;
            lock (Layouts)
            {
                if (Layouts.Count > 600) Layouts.Clear();
                Layouts[key] = lay;
            }
            return lay;
        }

        static Brush BrushOf(uint rgb, double alpha)
        {
            var c = Theme.Hex(rgb);
            if (alpha < 1) c.A = (byte)Math.Round(Math.Max(0, Math.Min(1, alpha)) * 255);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        static Pen PenOf(uint rgb, double alpha, double thick)
        {
            var p = new Pen(BrushOf(rgb, alpha), Math.Max(0.5, thick)) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return p;
        }

        // ── 그리기 ──

        /// 한 시각의 얹은 것 전체. hideId는 끌어서 옮기는 중인 것(그 자리를 따로 그린다).
        public static void Draw(DrawingContext dc, VProject p, double t, double W, double H, string hideId)
        {
            if (p == null) return;
            foreach (var it in p.Items)
            {
                if (it.Id == hideId) continue;
                if (t < it.Start - 1e-6 || t >= it.End) continue;
                var st = VAnim.Item(it, t);
                if (!st.Visible) continue;
                DrawItem(dc, it, st, t, W, H);
            }
        }

        public static void DrawItem(DrawingContext dc, VItem it, VAnim.State st, double t, double W, double H)
        {
            if (st.Opacity <= 0.003 && it.In != "type") return;
            var box = Box(it, st, W, H);
            double cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
            // 작은 화면도 영상(VRenderer.Pip)과 같이 돌린 테두리를 그린다
            bool rot = Math.Abs(st.Rot) > 0.01;
            if (rot) dc.PushTransform(new RotateTransform(st.Rot, cx, cy));
            dc.PushOpacity(Math.Max(0, Math.Min(1, st.Opacity)));
            try
            {
                switch (it.Kind)
                {
                    case "text": DrawText(dc, it, st, W, H); break;
                    case "image": DrawImage(dc, it, box); break;
                    case "sticker": DrawSticker(dc, it.Sticker, box, it.Rgb); break;
                    case "shape": DrawShape(dc, it, st, t, W, H); break;
                    case "pip": DrawPipBorder(dc, it, box, W, H); break;
                }
            }
            finally
            {
                dc.Pop();
                if (rot) dc.Pop();
            }
        }

        static void DrawText(DrawingContext dc, VItem it, VAnim.State st, double W, double H)
        {
            var lay = Layout(it, W, H);
            if (lay.Lines.Count == 0) return;
            double px = lay.FontPx;
            double cx = (st.X + st.Dx) * W, cy = (st.Y + st.Dy) * H;
            bool scaled = Math.Abs(st.Scale - 1) > 0.001;
            if (scaled) dc.PushTransform(new ScaleTransform(st.Scale, st.Scale, cx, cy));
            try
            {
                double left = cx - lay.BlockW / 2, top = cy - lay.BlockH / 2;
                int total = lay.Lines.Sum(l => l.Length);
                int show = it.In == "type" ? (int)Math.Round(st.Reveal * total) : int.MaxValue;
                var fill = BrushOf(it.Rgb, 1);
                var emphFill = BrushOf(it.EmphRgb, 1);
                var emph = (it.Emph ?? "").Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                double padX = px * 0.42, padY = px * 0.16;
                if (it.Box)
                {
                    var boxBrush = BrushOf(it.BoxRgb, it.BoxAlpha);
                    if (it.Subtitle)
                    {
                        for (int i = 0; i < lay.Lines.Count; i++)
                        {
                            double lw = lay.Widths[i];
                            if (lw <= 0.5) continue;
                            double lx = LineX(it.Align, left, lay.BlockW, lw);
                            dc.DrawRoundedRectangle(boxBrush, null, new Rect(lx - padX, top + i * lay.LineH - padY + px * 0.02, lw + padX * 2, lay.LineH + padY * 2 - px * 0.12), px * 0.22, px * 0.22);
                        }
                    }
                    else
                    {
                        dc.DrawRoundedRectangle(boxBrush, null, new Rect(left - padX * 1.3, top - padY * 2, lay.BlockW + padX * 2.6, lay.BlockH + padY * 4), px * 0.3, px * 0.3);
                    }
                }
                int used = 0;
                for (int i = 0; i < lay.Lines.Count; i++)
                {
                    string line = lay.Lines[i];
                    double lx = LineX(it.Align, left, lay.BlockW, lay.Widths[i]);
                    string part = line;
                    if (show < used + line.Length) part = line.Substring(0, Math.Max(0, show - used));
                    used += line.Length;
                    if (part.Length == 0) continue;
                    var ft = new FormattedText(part, Ko, FlowDirection.LeftToRight, lay.Face, px, fill, 1.0);
                    foreach (var word in emph)
                    {
                        int at = 0;
                        while ((at = part.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
                        {
                            ft.SetForegroundBrush(emphFill, at, word.Length);
                            at += word.Length;
                        }
                    }
                    var origin = new Point(lx, top + i * lay.LineH + (lay.LineH - ft.Height) / 2);
                    var geo = ft.BuildGeometry(origin);
                    if (it.Shadow)
                    {
                        dc.PushTransform(new TranslateTransform(px * 0.05, px * 0.07));
                        dc.DrawGeometry(BrushOf(0x000000, 0.45), it.Outline > 0 ? PenOf(0x000000, 0.45, it.Outline * px * 2) : null, geo);
                        dc.Pop();
                    }
                    if (it.Outline > 0) dc.DrawGeometry(null, PenOf(it.OutlineRgb, 1, it.Outline * px * 2), geo);
                    dc.DrawText(ft, origin);
                }
            }
            finally
            {
                if (scaled) dc.Pop();
            }
        }

        static double LineX(string align, double left, double blockW, double lineW)
        {
            if (align == "left") return left;
            if (align == "right") return left + blockW - lineW;
            return left + (blockW - lineW) / 2;
        }

        // ── 그림 ──

        static readonly Dictionary<string, BitmapSource> Images = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        public static BitmapSource ImageOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (Images)
            {
                BitmapSource b;
                if (Images.TryGetValue(path, out b)) return b;
            }
            BitmapSource img = null;
            try
            {
                if (File.Exists(path))
                {
                    img = Imaging.Fit(Imaging.Load(path), 2048);
                    if (img.CanFreeze) img.Freeze();
                }
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집: 그림 읽기 실패 " + ex.GetType().Name);
                img = null;
            }
            lock (Images)
            {
                if (Images.Count > 60) Images.Clear();
                Images[path] = img;
            }
            return img;
        }

        static void DrawImage(DrawingContext dc, VItem it, Rect box)
        {
            var img = ImageOf(it.Path);
            if (img == null)
            {
                dc.DrawRoundedRectangle(BrushOf(0x22252F, 0.5), PenOf(0xFFFFFF, 0.8, Math.Max(1, box.Width * 0.01)), box, box.Width * 0.04, box.Width * 0.04);
                return;
            }
            if (it.Corner > 0)
            {
                double r = Math.Min(box.Width, box.Height) * Math.Min(0.5, it.Corner);
                dc.PushClip(new RectangleGeometry(box, r, r));
                dc.DrawImage(img, box);
                dc.Pop();
            }
            else dc.DrawImage(img, box);
        }

        static double ImageAspect(VItem it)
        {
            var img = ImageOf(it.Path);
            if (img == null || img.PixelHeight == 0) return 1;
            return img.PixelWidth / (double)img.PixelHeight;
        }

        // ── 스티커 ──

        public static readonly string[][] Stickers =
        {
            new[] { "star", "별" }, new[] { "heart", "하트" }, new[] { "check", "맞아요" }, new[] { "cross", "아니에요" },
            new[] { "question", "물음표" }, new[] { "exclaim", "느낌표" }, new[] { "spark", "반짝" }, new[] { "bulb", "생각" },
            new[] { "bubble", "말풍선" }, new[] { "pin", "여기" },
            new[] { "n1", "①" }, new[] { "n2", "②" }, new[] { "n3", "③" }, new[] { "n4", "④" }, new[] { "n5", "⑤" },
            new[] { "t_important", "중요" }, new[] { "t_answer", "정답" }, new[] { "t_wrong", "오답" }, new[] { "t_tip", "꿀팁" },
            new[] { "t_review", "복습" }, new[] { "t_quiz", "퀴즈" }, new[] { "t_check", "확인" }, new[] { "t_point", "핵심" }
        };

        static readonly Dictionary<string, uint> PillColors = new Dictionary<string, uint>
        {
            { "t_important", 0xFF8473 }, { "t_answer", 0x52D3A0 }, { "t_wrong", 0xFF8473 }, { "t_tip", 0xFFD447 },
            { "t_review", 0x6CA8FF }, { "t_quiz", 0xB596FF }, { "t_check", 0x4FC3E8 }, { "t_point", 0xFFAA4D }
        };

        public static string StickerLabel(string id)
        {
            foreach (var s in Stickers) if (s[0] == id) return s[1];
            return "스티커";
        }

        /// 스티커의 가로:세로
        public static double StickerAspect(string id)
        {
            if (id != null && id.StartsWith("t_")) return 2.3;
            if (id == "bubble") return 1.25;
            return 1;
        }

        public static void DrawSticker(DrawingContext dc, string id, Rect r, uint rgb)
        {
            double s = Math.Min(r.Width, r.Height);
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            var ink = PenOf(0x22252F, 1, s * 0.055);
            var inkFill = BrushOf(0x22252F, 1);
            switch (id ?? "star")
            {
                case "star":
                    dc.DrawGeometry(BrushOf(0xFFD447, 1), ink, Star(cx, cy + s * 0.03, s * 0.47, s * 0.2, 5));
                    break;
                case "spark":
                    dc.DrawGeometry(BrushOf(0xFFD447, 1), ink, Star(cx, cy, s * 0.46, s * 0.13, 4));
                    dc.DrawGeometry(BrushOf(0xFFF6D6, 1), PenOf(0x22252F, 1, s * 0.04), Star(cx + s * 0.3, cy - s * 0.3, s * 0.13, s * 0.04, 4));
                    break;
                case "heart":
                    {
                        var g = Geometry.Parse("M0.5,0.88 C0.2,0.66 0.04,0.48 0.06,0.3 C0.08,0.12 0.3,0.04 0.5,0.24 C0.7,0.04 0.92,0.12 0.94,0.3 C0.96,0.48 0.8,0.66 0.5,0.88 Z").Clone();
                        g.Transform = new MatrixTransform(s, 0, 0, s, cx - s / 2, cy - s / 2);
                        dc.DrawGeometry(BrushOf(0xFF8473, 1), ink, g);
                        break;
                    }
                case "check":
                    dc.DrawEllipse(BrushOf(0x52D3A0, 1), ink, new Point(cx, cy), s * 0.46, s * 0.46);
                    dc.DrawGeometry(null, PenOf(0x22252F, 1, s * 0.1), Poly(new Point(cx - s * 0.22, cy), new Point(cx - s * 0.05, cy + s * 0.17), new Point(cx + s * 0.24, cy - s * 0.17)));
                    break;
                case "cross":
                    dc.DrawEllipse(BrushOf(0xFF8473, 1), ink, new Point(cx, cy), s * 0.46, s * 0.46);
                    dc.DrawLine(PenOf(0x22252F, 1, s * 0.1), new Point(cx - s * 0.17, cy - s * 0.17), new Point(cx + s * 0.17, cy + s * 0.17));
                    dc.DrawLine(PenOf(0x22252F, 1, s * 0.1), new Point(cx + s * 0.17, cy - s * 0.17), new Point(cx - s * 0.17, cy + s * 0.17));
                    break;
                case "question":
                case "exclaim":
                    dc.DrawEllipse(BrushOf(id == "question" ? 0x6CA8FFu : 0xFFAA4Du, 1), ink, new Point(cx, cy), s * 0.46, s * 0.46);
                    CenterText(dc, id == "question" ? "?" : "!", cx, cy, s * 0.62, inkFill);
                    break;
                case "bulb":
                    dc.DrawEllipse(BrushOf(0xFFD447, 1), ink, new Point(cx, cy - s * 0.1), s * 0.3, s * 0.3);
                    dc.DrawRoundedRectangle(BrushOf(0xAEB6C5, 1), ink, new Rect(cx - s * 0.13, cy + s * 0.2, s * 0.26, s * 0.2), s * 0.05, s * 0.05);
                    foreach (var a in new[] { -60.0, -20, 20, 60 })
                    {
                        double rad = (a - 90) * Math.PI / 180;
                        dc.DrawLine(PenOf(0x22252F, 1, s * 0.045), new Point(cx + Math.Cos(rad) * s * 0.38, cy - s * 0.1 + Math.Sin(rad) * s * 0.38), new Point(cx + Math.Cos(rad) * s * 0.47, cy - s * 0.1 + Math.Sin(rad) * s * 0.47));
                    }
                    break;
                case "bubble":
                    {
                        double w = r.Width, h = r.Height;
                        var g = Geometry.Parse("M0.12,0.06 H0.88 A0.1,0.1 0 0 1 0.98,0.16 V0.62 A0.1,0.1 0 0 1 0.88,0.72 H0.42 L0.22,0.94 L0.26,0.72 H0.12 A0.1,0.1 0 0 1 0.02,0.62 V0.16 A0.1,0.1 0 0 1 0.12,0.06 Z").Clone();
                        g.Transform = new MatrixTransform(w, 0, 0, h, r.X, r.Y);
                        dc.DrawGeometry(BrushOf(0xFFFFFF, 1), PenOf(0x22252F, 1, s * 0.045), g);
                        break;
                    }
                case "pin":
                    {
                        var g = Geometry.Parse("M0.5,0.96 C0.36,0.74 0.16,0.56 0.16,0.38 A0.34,0.34 0 0 1 0.84,0.38 C0.84,0.56 0.64,0.74 0.5,0.96 Z").Clone();
                        g.Transform = new MatrixTransform(s, 0, 0, s, cx - s / 2, cy - s / 2);
                        dc.DrawGeometry(BrushOf(0xFF8473, 1), ink, g);
                        dc.DrawEllipse(BrushOf(0xFFFFFF, 1), PenOf(0x22252F, 1, s * 0.04), new Point(cx, cy - s * 0.12), s * 0.11, s * 0.11);
                        break;
                    }
                default:
                    if (id != null && id.Length == 2 && id[0] == 'n')
                    {
                        dc.DrawEllipse(BrushOf(0xFFD447, 1), ink, new Point(cx, cy), s * 0.46, s * 0.46);
                        CenterText(dc, id.Substring(1), cx, cy, s * 0.56, inkFill);
                    }
                    else if (id != null && id.StartsWith("t_"))
                    {
                        uint fillRgb;
                        if (!PillColors.TryGetValue(id, out fillRgb)) fillRgb = 0xFFD447;
                        var pill = new Rect(r.X + s * 0.03, r.Y + s * 0.03, r.Width - s * 0.06, r.Height - s * 0.06);
                        dc.DrawRoundedRectangle(BrushOf(fillRgb, 1), PenOf(0x22252F, 1, s * 0.06), pill, pill.Height / 2, pill.Height / 2);
                        CenterText(dc, StickerLabel(id), cx, cy, s * 0.5, inkFill);
                    }
                    break;
            }
        }

        static void CenterText(DrawingContext dc, string text, double cx, double cy, double px, Brush fill)
        {
            var ft = new FormattedText(text, Ko, FlowDirection.LeftToRight, new Typeface(new FontFamily("맑은 고딕"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), Math.Max(2, px), fill, 1.0);
            dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
        }

        static Geometry Star(double cx, double cy, double outer, double inner, int points)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                for (int i = 0; i < points * 2; i++)
                {
                    double rr = i % 2 == 0 ? outer : inner;
                    double a = -Math.PI / 2 + i * Math.PI / points;
                    var pt = new Point(cx + Math.Cos(a) * rr, cy + Math.Sin(a) * rr);
                    if (i == 0) c.BeginFigure(pt, true, true);
                    else c.LineTo(pt, true, true);
                }
            }
            g.Freeze();
            return g;
        }

        static Geometry Poly(params Point[] pts)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(pts[0], false, false);
                for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, true);
            }
            g.Freeze();
            return g;
        }

        // ── 도형 ──

        static void DrawShape(DrawingContext dc, VItem it, VAnim.State st, double t, double W, double H)
        {
            double s = Math.Min(W, H);
            double thick = Math.Max(1, it.Thick * s * st.Scale);
            double cx = (st.X + st.Dx) * W, cy = (st.Y + st.Dy) * H;
            double w = it.W * W * st.Scale, h = it.H * H * st.Scale;
            var pen = PenOf(it.Rgb, 1, thick);
            switch (it.Shape ?? "box")
            {
                case "arrow":
                case "line":
                    {
                        var a = new Point(cx - w / 2, cy - h / 2);
                        var b = new Point(cx + w / 2, cy + h / 2);
                        if (it.Shape == "line")
                        {
                            dc.DrawLine(pen, a, b);
                            break;
                        }
                        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                        if (len < 1) break;
                        double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len;
                        double head = Math.Min(len * 0.45, thick * 4.2);
                        var tip = b;
                        var baseP = new Point(b.X - ux * head, b.Y - uy * head);
                        var l = new Point(baseP.X - uy * head * 0.62, baseP.Y + ux * head * 0.62);
                        var rr = new Point(baseP.X + uy * head * 0.62, baseP.Y - ux * head * 0.62);
                        dc.DrawLine(pen, a, new Point(b.X - ux * head * 0.7, b.Y - uy * head * 0.7));
                        var g = new StreamGeometry();
                        using (var c = g.Open())
                        {
                            c.BeginFigure(tip, true, true);
                            c.LineTo(l, true, true);
                            c.LineTo(rr, true, true);
                        }
                        g.Freeze();
                        dc.DrawGeometry(BrushOf(it.Rgb, 1), PenOf(it.Rgb, 1, thick * 0.5), g);
                        break;
                    }
                case "circle":
                    dc.DrawEllipse(null, pen, new Point(cx, cy), Math.Abs(w) / 2, Math.Abs(h) / 2);
                    break;
                case "marker":
                    dc.DrawRoundedRectangle(BrushOf(it.Rgb, 0.38), null, new Rect(cx - Math.Abs(w) / 2, cy - Math.Abs(h) / 2, Math.Abs(w), Math.Abs(h)), Math.Abs(h) * 0.18, Math.Abs(h) * 0.18);
                    break;
                case "spot":
                    // 마우스 자리 강조: 옅은 노란 원
                    dc.DrawEllipse(BrushOf(it.Rgb, 0.36), PenOf(it.Rgb, 0.85, s * 0.004), new Point(cx, cy), Math.Abs(w) / 2, Math.Abs(w) / 2);
                    break;
                case "ripple":
                    {
                        // 누른 자리: 퍼지는 고리
                        double u = VAnim.Clamp01((t - it.Start) / Math.Max(0.05, it.Length));
                        double rad = Math.Abs(w) / 2 * (0.35 + 0.9 * VAnim.EaseOut(u));
                        dc.DrawEllipse(null, PenOf(it.Rgb, 1 - u, Math.Max(1.5, s * 0.008)), new Point(cx, cy), rad, rad);
                        break;
                    }
                case "bar":
                    {
                        // 이름 막대: 왼쪽 색 띠 + 어두운 판(글자는 따로 얹는다)
                        var rect = new Rect(cx - Math.Abs(w) / 2, cy - Math.Abs(h) / 2, Math.Abs(w), Math.Abs(h));
                        dc.DrawRoundedRectangle(BrushOf(0x22252F, 0.82), null, rect, rect.Height * 0.16, rect.Height * 0.16);
                        dc.DrawRoundedRectangle(BrushOf(it.Rgb, 1), null, new Rect(rect.X, rect.Y, Math.Max(2, rect.Height * 0.14), rect.Height), rect.Height * 0.07, rect.Height * 0.07);
                        break;
                    }
                default:
                    {
                        double corner = Math.Min(Math.Abs(w), Math.Abs(h)) * Math.Min(0.5, it.Corner);
                        dc.DrawRoundedRectangle(null, pen, new Rect(cx - Math.Abs(w) / 2, cy - Math.Abs(h) / 2, Math.Abs(w), Math.Abs(h)), corner, corner);
                        break;
                    }
            }
        }

        static void DrawPipBorder(DrawingContext dc, VItem it, Rect box, double W, double H)
        {
            if (!it.Border || it.Chroma) return;
            double thick = Math.Max(1, Math.Min(W, H) * 0.006);
            var pen = PenOf(it.BorderRgb, 1, thick);
            if (it.Circle) dc.DrawEllipse(null, pen, new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2);
            else dc.DrawRectangle(null, pen, box);
        }

        // ── 자리 ──

        /// 원본 영상의 가로:세로(돌림 반영). 모르면 16:9.
        public static double SourceAspect(string path)
        {
            if (VMedia.IsImage(path))
            {
                var img = ImageOf(path);
                return img == null || img.PixelHeight == 0 ? 1 : img.PixelWidth / (double)img.PixelHeight;
            }
            var pr = VMedia.Probe(path);
            if (pr == null || pr.Width <= 0 || pr.Height <= 0) return 16.0 / 9;
            bool side = pr.Rotation % 180 != 0;
            return side ? pr.Height / (double)pr.Width : pr.Width / (double)pr.Height;
        }

        /// 돌리기 전, 화면 위의 상자(가운데·너비·높이). 손잡이와 누르기 판정에 쓴다.
        public static Rect Box(VItem it, VAnim.State st, double W, double H)
        {
            double cx = (st.X + st.Dx) * W, cy = (st.Y + st.Dy) * H;
            double w, h;
            switch (it.Kind)
            {
                case "text":
                    {
                        var lay = Layout(it, W, H);
                        double padX = it.Box ? lay.FontPx * 0.55 : lay.FontPx * 0.1, padY = it.Box ? lay.FontPx * 0.32 : lay.FontPx * 0.05;
                        w = (lay.BlockW + padX * 2) * st.Scale;
                        h = (lay.BlockH + padY * 2) * st.Scale;
                        break;
                    }
                case "image":
                    w = it.W * W * st.Scale;
                    h = w / Math.Max(0.05, ImageAspect(it));
                    break;
                case "sticker":
                    w = it.W * W * st.Scale;
                    h = w / StickerAspect(it.Sticker);
                    break;
                case "pip":
                    {
                        Rect r;
                        VRenderer.PipRect(it, st, SourceAspect(it.Path), (int)Math.Round(W), (int)Math.Round(H), out r);
                        return r;
                    }
                default:
                    if (it.Shape == "arrow" || it.Shape == "line")
                    {
                        double ww = it.W * W * st.Scale, hh = it.H * H * st.Scale;
                        double pad = Math.Max(4, it.Thick * Math.Min(W, H) * 2.5);
                        var r = new Rect(new Point(cx - ww / 2, cy - hh / 2), new Point(cx + ww / 2, cy + hh / 2));
                        r.Inflate(pad, pad);
                        return r;
                    }
                    w = Math.Abs(it.W * W * st.Scale);
                    h = it.Shape == "spot" || it.Shape == "ripple" ? w : Math.Abs(it.H * H * st.Scale);
                    break;
            }
            return new Rect(cx - w / 2, cy - h / 2, Math.Max(1, w), Math.Max(1, h));
        }

        /// 이 점(화면 좌표)에 있는 맨 위의 것
        public static VItem HitTest(VProject p, double t, double W, double H, Point pt)
        {
            for (int i = p.Items.Count - 1; i >= 0; i--)
            {
                var it = p.Items[i];
                if (t < it.Start - 1e-6 || t >= it.End) continue;
                var st = VAnim.Item(it, t);
                if (!st.Visible) continue;
                var box = Box(it, st, W, H);
                var q = pt;
                if (Math.Abs(st.Rot) > 0.01)
                {
                    var m = Matrix.Identity;
                    m.RotateAt(-st.Rot, box.X + box.Width / 2, box.Y + box.Height / 2);
                    q = m.Transform(pt);
                }
                var hit = box;
                hit.Inflate(6, 6);
                if (hit.Contains(q)) return it;
            }
            return null;
        }
    }

    /// 내보내기용: 얹은 것을 영상 크기 그림(미리 곱한 BGRA)으로. STA 스레드에서만 쓴다.
    sealed class VOverlayRenderer
    {
        readonly int W, H;
        readonly RenderTargetBitmap target;
        readonly byte[] pixels;
        string lastKey;
        bool lastEmpty = true;

        public VOverlayRenderer(int w, int h)
        {
            W = w;
            H = h;
            target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            pixels = new byte[w * h * 4];
        }

        /// 이 시각에 보일 것이 없으면 null. 바로 앞 장과 모습이 같으면 다시 그리지 않는다.
        public byte[] Render(VProject p, int version, double t)
        {
            var sb = new StringBuilder();
            sb.Append(version).Append('|');
            bool any = false;
            foreach (var it in p.Items)
            {
                if (t < it.Start - 1e-6 || t >= it.End) continue;
                if (it.Kind == "pip" && (!it.Border || it.Chroma)) continue;
                var st = VAnim.Item(it, t);
                if (!st.Visible) continue;
                any = true;
                sb.Append(it.Id).Append(':').Append(st.X.ToString("F4")).Append(',').Append(st.Y.ToString("F4")).Append(',')
                  .Append(st.Scale.ToString("F4")).Append(',').Append(st.Opacity.ToString("F3")).Append(',').Append(st.Dx.ToString("F4")).Append(',')
                  .Append(st.Dy.ToString("F4")).Append(',').Append(st.Reveal.ToString("F3")).Append(',').Append(st.Rot.ToString("F2"));
                if (it.Kind == "shape" && it.Shape == "ripple") sb.Append(',').Append(t.ToString("F3"));
                sb.Append(';');
            }
            if (!any)
            {
                lastEmpty = true;
                lastKey = null;
                return null;
            }
            string key = sb.ToString();
            if (key == lastKey && !lastEmpty) return pixels;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) VOverlay.Draw(dc, p, t, W, H, null);
            target.Clear();
            target.Render(dv);
            target.CopyPixels(pixels, W * 4, 0);
            lastKey = key;
            lastEmpty = false;
            return pixels;
        }
    }

    /// 글꼴 목록(한국어 이름이 있는 글꼴을 앞에)
    static class VFonts
    {
        static List<string> cached;

        public static List<string> Korean()
        {
            if (cached != null) return cached;
            var list = new List<string>();
            try
            {
                var ko = System.Windows.Markup.XmlLanguage.GetLanguage("ko-kr");
                foreach (var f in Fonts.SystemFontFamilies)
                {
                    string name;
                    if (f.FamilyNames.TryGetValue(ko, out name) && !string.IsNullOrWhiteSpace(name)) list.Add(name);
                }
            }
            catch (Exception ex)
            {
                Log.Write("글꼴 목록 읽기 실패: " + ex.GetType().Name);
            }
            list = list.Distinct().OrderBy(n => n, StringComparer.CurrentCulture).ToList();
            list.Remove("맑은 고딕");
            list.Insert(0, "맑은 고딕");
            cached = list;
            return list;
        }
    }
}
