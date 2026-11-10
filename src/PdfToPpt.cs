// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// PDF를 글자 고칠 수 있는 PPT로 바꾼다.
// - 글자가 들어 있는 쪽: 글자를 글상자로 옮기고, 글자를 뺀 쪽을 그림으로 떠서 슬라이드 배경에 깐다.
// - 스캔한 쪽: 글자 인식으로 읽고, 읽은 자리를 주변 색으로 지운 뒤 글상자로 얹는다.
// 줄바꿈은 PDF의 줄 그대로 두고(줄마다 문단), 글꼴이 달라져도 폭이 맞도록 글자 사이를 조절한다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    public sealed class PdfToPptResult
    {
        public Deck Deck;
        public int TextPages, OcrPages, LayerPages, PicturePages;
    }

    public static class PdfToPpt
    {
        const double Dpi = 200;
        const int MaxSide = 3200;
        const string PlainFont = "맑은 고딕";

        // 글자 인식의 낱말 상자 높이는 글자 크기보다 조금 작고, 한글 아랫부분은 기준선 밑으로 조금 내려간다.
        internal const double OcrSizeFactor = 1.03;
        internal const double OcrBaselineLift = 0.1;

        sealed class Piece
        {
            public string Text, Font;
            public double X, End, Size;
            public bool Bold, Italic, Wide;
            public int Rgb;
        }

        sealed class Seg
        {
            public double X, End, Y, Size;
            public readonly List<Piece> Pieces = new List<Piece>();
        }

        sealed class Block
        {
            public readonly List<Seg> Segs = new List<Seg>();
        }

        sealed class Map
        {
            public double S, Ox, Oy;
            public long X(double pt) { return (long)Math.Round(Ox + pt * S * 12700); }
            public long Y(double pt) { return (long)Math.Round(Oy + pt * S * 12700); }
            public long L(double pt) { return (long)Math.Round(pt * S * 12700); }
        }

        /// 쪽 그림을 픽셀로 다룬다(BGR 3바이트).
        sealed class Raster
        {
            public int W, H, Stride;
            public byte[] Px;

            public static Raster From(BitmapSource s)
            {
                var c = new FormatConvertedBitmap(s, PixelFormats.Bgr24, null, 0);
                var r = new Raster { W = c.PixelWidth, H = c.PixelHeight };
                r.Stride = r.W * 3;
                r.Px = new byte[r.Stride * r.H];
                c.CopyPixels(r.Px, r.Stride, 0);
                return r;
            }

            public BitmapSource ToBitmap()
            {
                var b = BitmapSource.Create(W, H, Dpi, Dpi, PixelFormats.Bgr24, null, Px, Stride);
                b.Freeze();
                return b;
            }

            /// 거의 흰 종이뿐인가
            public bool Blank()
            {
                int bad = 0, limit = W * H / 20000;
                for (int i = 0; i + 2 < Px.Length; i += 3)
                {
                    if (Px[i] < 250 || Px[i + 1] < 250 || Px[i + 2] < 250)
                    {
                        if (++bad > limit) return false;
                    }
                }
                return true;
            }

            double Luma(int x, int y)
            {
                int i = y * Stride + x * 3;
                return 0.114 * Px[i] + 0.587 * Px[i + 1] + 0.299 * Px[i + 2];
            }

            /// 상자 둘레가 고른 색인가. 사진 속 글자는 지우면 사진이 망가지므로 그대로 둔다.
            public bool FlatAround(int[] b)
            {
                var v = new List<double>();
                int x0 = Math.Max(0, b[0] - 1), y0 = Math.Max(0, b[1] - 1), x1 = Math.Min(W - 1, b[2] + 1), y1 = Math.Min(H - 1, b[3] + 1);
                for (int x = x0; x <= x1; x += 2)
                {
                    v.Add(Luma(x, y0));
                    v.Add(Luma(x, y1));
                }
                for (int y = y0; y <= y1; y += 2)
                {
                    v.Add(Luma(x0, y));
                    v.Add(Luma(x1, y));
                }
                if (v.Count < 8) return true;
                v.Sort();
                return v[(int)(v.Count * 0.9)] - v[(int)(v.Count * 0.1)] <= 48;
            }

            bool Ink(int xa, int ya, int xb, int yb, double paper)
            {
                int ink = 0, all = 0;
                for (int y = ya; y <= yb; y++)
                {
                    for (int x = xa; x <= xb; x++)
                    {
                        all++;
                        if (Math.Abs(Luma(x, y) - paper) > 38) ink++;
                    }
                }
                return ink >= 1 && ink < all * 0.85;
            }

            /// 글자 인식의 낱말 상자는 글자보다 좁을 때가 있다. 바깥쪽에 글자 먹이 남아 있으면 그만큼 넓힌다.
            /// 줄 전체가 먹이면 표의 선이므로 넘어가지 않는다.
            public int[] Grow(int[] b, int most)
            {
                int x0 = Math.Max(0, b[0]), y0 = Math.Max(0, b[1]), x1 = Math.Min(W - 1, b[2]), y1 = Math.Min(H - 1, b[3]);
                var ring = new List<double>();
                for (int x = x0; x <= x1; x += 2)
                {
                    ring.Add(Luma(x, y0));
                    ring.Add(Luma(x, y1));
                }
                for (int y = y0; y <= y1; y += 2)
                {
                    ring.Add(Luma(x0, y));
                    ring.Add(Luma(x1, y));
                }
                ring.Sort();
                double paper = ring[ring.Count / 2];
                for (int n = 0; n < most && x1 + 1 < W && Ink(x1 + 1, y0, x1 + 1, y1, paper); n++) x1++;
                for (int n = 0; n < most && x0 > 0 && Ink(x0 - 1, y0, x0 - 1, y1, paper); n++) x0--;
                for (int n = 0; n < most && y1 + 1 < H && Ink(x0, y1 + 1, x1, y1 + 1, paper); n++) y1++;
                for (int n = 0; n < most && y0 > 0 && Ink(x0, y0 - 1, x1, y0 - 1, paper); n++) y0--;
                return new[] { x0, y0, x1, y1 };
            }

            /// 상자 안을 줄마다 왼쪽·오른쪽 바깥 색으로 이어 칠한다.
            public void Erase(int[] b)
            {
                int x0 = Math.Max(0, b[0]), y0 = Math.Max(0, b[1]), x1 = Math.Min(W - 1, b[2]), y1 = Math.Min(H - 1, b[3]);
                if (x1 < x0 || y1 < y0) return;
                var left = new double[3];
                var right = new double[3];
                for (int y = y0; y <= y1; y++)
                {
                    bool hasL = Sample(y, x0 - 3, x0 - 1, left), hasR = Sample(y, x1 + 1, x1 + 3, right);
                    if (!hasL && !hasR) continue;
                    if (!hasL) Array.Copy(right, left, 3);
                    if (!hasR) Array.Copy(left, right, 3);
                    int span = x1 - x0 + 2;
                    for (int x = x0; x <= x1; x++)
                    {
                        double t = (x - x0 + 1) / (double)span;
                        int i = y * Stride + x * 3;
                        for (int c = 0; c < 3; c++) Px[i + c] = (byte)Math.Round(left[c] * (1 - t) + right[c] * t);
                    }
                }
            }

            bool Sample(int y, int from, int to, double[] into)
            {
                int n = 0;
                double b = 0, g = 0, r = 0;
                for (int x = Math.Max(0, from); x <= Math.Min(W - 1, to); x++)
                {
                    int i = y * Stride + x * 3;
                    b += Px[i];
                    g += Px[i + 1];
                    r += Px[i + 2];
                    n++;
                }
                if (n == 0) return false;
                into[0] = b / n;
                into[1] = g / n;
                into[2] = r / n;
                return true;
            }

            /// 상자들 안에서 글자 색을 고른다. 종이와 가장 다른 픽셀들의 평균.
            public int TextColor(IEnumerable<int[]> boxes)
            {
                var all = new List<int>();
                var luma = new List<double>();
                foreach (var b in boxes)
                {
                    int x1 = Math.Min(W - 1, b[2]), y1 = Math.Min(H - 1, b[3]);
                    for (int y = Math.Max(0, b[1]); y <= y1; y++)
                    {
                        for (int x = Math.Max(0, b[0]); x <= x1; x++)
                        {
                            all.Add(y * Stride + x * 3);
                            luma.Add(Luma(x, y));
                        }
                    }
                }
                if (all.Count == 0) return 0;
                var order = Enumerable.Range(0, all.Count).OrderBy(i => luma[i]).ToList();
                double low = luma[order[(int)(order.Count * 0.05)]], mid = luma[order[order.Count / 2]], high = luma[order[(int)(order.Count * 0.95)]];
                bool dark = mid - low >= high - mid;
                int take = Math.Max(1, order.Count * 12 / 100);
                var picked = dark ? order.Take(take) : order.Skip(order.Count - take);
                long b1 = 0, g1 = 0, r1 = 0;
                foreach (int k in picked)
                {
                    int i = all[k];
                    b1 += Px[i];
                    g1 += Px[i + 1];
                    r1 += Px[i + 2];
                }
                int bb = (int)(b1 / take), gg = (int)(g1 / take), rr = (int)(r1 / take);
                if (Math.Min(rr, Math.Min(gg, bb)) >= 232) return 0xFFFFFF;
                // 스캔한 검은 글자는 회색으로 읽히므로 검정으로 되돌린다.
                if (dark && Math.Max(rr, Math.Max(gg, bb)) - Math.Min(rr, Math.Min(gg, bb)) < 40 && 0.114 * bb + 0.587 * gg + 0.299 * rr < 125) return 0;
                return (rr << 16) | (gg << 8) | bb;
            }
        }

        // ── 글자를 토막과 덩어리로 ──

        static readonly Dictionary<string, string> FontMemo = new Dictionary<string, string>();

        static string FontOf(PdfTextRun r)
        {
            if (r.Font == null) return PlainFont;
            string key = (r.Serif ? "s:" : "g:") + r.Font;
            lock (FontMemo)
            {
                string f;
                if (!FontMemo.TryGetValue(key, out f))
                {
                    f = DeckFonts.Resolve(r.Font, r.Serif);
                    FontMemo[key] = f;
                }
                return f;
            }
        }

        static Piece Make(PdfTextRun r)
        {
            return new Piece { Text = r.S, Font = FontOf(r), X = r.X, End = r.End, Size = r.Size, Bold = r.Bold, Italic = r.Italic, Rgb = r.Rgb };
        }

        static bool Same(Piece a, PdfTextRun r)
        {
            return a.Bold == r.Bold && a.Italic == r.Italic && a.Rgb == r.Rgb && a.Font == FontOf(r) && Math.Abs(a.Size - r.Size) <= a.Size * 0.03;
        }

        /// 한 줄을 토막으로 나눈다. 넓은 틈은 표의 칸이나 단이 갈린 것으로 본다.
        static List<Seg> SegmentsOfLine(PdfTextLine line)
        {
            var segs = new List<Seg>();
            Seg cur = null;
            PdfTextRun prev = null;
            foreach (var r in line.Runs)
            {
                // 같은 글자를 살짝 비켜 겹쳐 찍어 진하게 만든 경우
                if (prev != null && prev.S == r.S && Math.Abs(prev.X - r.X) < 0.3 * r.Size && Math.Abs(prev.Y - r.Y) < 0.3 * r.Size)
                {
                    if (prev.Rgb == r.Rgb && cur != null) cur.Pieces[cur.Pieces.Count - 1].Bold = true;
                    continue;
                }
                prev = r;
                double big = cur == null ? r.Size : Math.Max(cur.Size, r.Size);
                double gap = cur == null ? 0 : r.X - cur.End;
                if (cur == null || gap > 1.4 * big)
                {
                    // 줄의 높이가 아니라 이 글자의 높이를 쓴다. 옆의 큰 글자와 한 줄로 묶여도 제자리를 지킨다.
                    cur = new Seg { X = r.X, End = r.End, Y = r.Y, Size = r.Size };
                    cur.Pieces.Add(Make(r));
                    segs.Add(cur);
                    continue;
                }
                var last = cur.Pieces[cur.Pieces.Count - 1];
                bool space = gap > 0.22 * big && !last.Text.EndsWith(" ", StringComparison.Ordinal) && !r.S.StartsWith(" ", StringComparison.Ordinal);
                if (Same(last, r))
                {
                    last.Text += (space ? " " : "") + r.S;
                    last.End = Math.Max(last.End, r.End);
                }
                else
                {
                    if (space) last.Text += " ";
                    cur.Pieces.Add(Make(r));
                }
                cur.End = Math.Max(cur.End, r.End);
                cur.Size = big;
            }
            MergeSpread(segs);
            foreach (var s in segs) TrimEnds(s);
            return segs;
        }

        static bool Short(Seg s)
        {
            return s.Pieces.Sum(p => p.Text.Trim().Length) <= 2;
        }

        /// 글자를 띄엄띄엄 벌려 쓴 제목: 한두 글자짜리 토막이 같은 간격으로 이어지면 한 토막으로 합친다.
        static void MergeSpread(List<Seg> segs)
        {
            for (int i = 0; i + 2 < segs.Count; i++)
            {
                double first = segs[i + 1].X - segs[i].End;
                // 틈이 글자 세 개보다 넓으면 표의 칸이다(모둠·주제·장소처럼 두 글자 제목이 줄지어 있어도 합치지 않는다).
                if (first > 3 * segs[i].Size) continue;
                int j = i;
                while (j + 1 < segs.Count && Short(segs[j]) && Short(segs[j + 1]) && Math.Abs(segs[j + 1].X - segs[j].End - first) <= 0.25 * first + 0.5) j++;
                if (j - i < 2) continue;
                var p = segs[i].Pieces[0];
                var merged = new Seg { X = segs[i].X, End = segs[j].End, Y = segs[i].Y, Size = segs[i].Size };
                string text = string.Concat(segs.Skip(i).Take(j - i + 1).SelectMany(s => s.Pieces).Select(x => x.Text.Trim()));
                merged.Pieces.Add(new Piece { Text = text, Font = p.Font, X = segs[i].X, End = segs[j].End, Size = p.Size, Bold = p.Bold, Italic = p.Italic, Rgb = p.Rgb, Wide = true });
                segs[i] = merged;
                segs.RemoveRange(i + 1, j - i);
            }
        }

        /// 토막 앞뒤의 빈칸을 떼고, 그만큼 자리도 줄인다.
        static void TrimEnds(Seg s)
        {
            var first = s.Pieces[0];
            int lead = first.Text.Length - first.Text.TrimStart().Length;
            if (lead > 0 && lead < first.Text.Length)
            {
                double shift = (first.End - first.X) * lead / first.Text.Length;
                first.Text = first.Text.Substring(lead);
                first.X += shift;
                s.X = first.X;
            }
            var last = s.Pieces[s.Pieces.Count - 1];
            int tail = last.Text.Length - last.Text.TrimEnd().Length;
            if (tail > 0 && tail < last.Text.Length)
            {
                double cut = (last.End - last.X) * tail / last.Text.Length;
                last.Text = last.Text.Substring(0, last.Text.Length - tail);
                last.End -= cut;
                s.End = Math.Min(s.End, last.End);
            }
        }

        /// 위에서 아래로 이어지는 줄을 한 글상자로 묶는다.
        static List<Block> Cluster(List<Seg> segs)
        {
            var blocks = new List<Block>();
            foreach (var s in segs.OrderByDescending(x => x.Y).ThenBy(x => x.X))
            {
                Block best = null;
                double bestGap = double.MaxValue;
                foreach (var b in blocks)
                {
                    if (b.Segs.Count >= 40) continue;
                    var last = b.Segs[b.Segs.Count - 1];
                    double size = Math.Max(last.Size, s.Size);
                    double gap = last.Y - s.Y;
                    if (gap < 0.9 * size || gap > 1.75 * size) continue;
                    if (Math.Abs(last.Size - s.Size) > 0.08 * size) continue;
                    double overlap = Math.Min(last.End, s.End) - Math.Max(last.X, s.X);
                    if (overlap < 0.3 * Math.Min(last.End - last.X, s.End - s.X)) continue;
                    bool left = Math.Abs(last.X - s.X) <= 3.2 * size;
                    bool mid = Math.Abs((last.X + last.End) / 2 - (s.X + s.End) / 2) <= 0.6 * size;
                    bool right = Math.Abs(last.End - s.End) <= 0.6 * size;
                    if (!left && !mid && !right) continue;
                    if (gap < bestGap)
                    {
                        best = b;
                        bestGap = gap;
                    }
                }
                if (best == null)
                {
                    best = new Block();
                    blocks.Add(best);
                }
                best.Segs.Add(s);
            }
            return blocks;
        }

        static string AlignOf(Block b, double pageW)
        {
            var s = b.Segs;
            double size = s.Max(x => x.Size);
            if (s.Count == 1)
            {
                // 왼쪽 여백에서 멀리 떨어져 쪽 한가운데에 있는 한 줄은 가운데 정렬로 본다.
                double centre = (s[0].X + s[0].End) / 2;
                return Math.Abs(centre - pageW / 2) <= pageW * 0.02 && s[0].X > pageW * 0.15 ? "ctr" : null;
            }
            if (s.Max(x => x.X) - s.Min(x => x.X) <= 0.4 * size) return null;
            if (s.Max(x => (x.X + x.End) / 2) - s.Min(x => (x.X + x.End) / 2) <= 0.5 * size) return "ctr";
            if (s.Max(x => x.End) - s.Min(x => x.End) <= 0.5 * size) return "r";
            return null;
        }

        static Piece Biggest(Seg s)
        {
            return s.Pieces.OrderByDescending(p => p.Size).First();
        }

        /// PDF에서의 폭에 맞도록 글자 사이를 조절한다(1/100pt).
        static int SpacingOf(Piece p)
        {
            int n = p.Text.Length;
            if (n < 2) return 0;
            double natural = DeckFonts.Width(p.Text, p.Font, p.Size, p.Bold, p.Italic), want = p.End - p.X;
            if (natural <= 0 || want <= 0) return 0;
            double per = (want - natural) / (n - 1);
            double up = p.Size * (p.Wide ? 4 : 0.4), down = p.Size * 0.24;
            per = Math.Max(-down, Math.Min(up, per));
            int spc = (int)Math.Round(per * 100);
            return Math.Abs(spc) < 3 ? 0 : spc;
        }

        static DeckShape ToShape(Block b, PdfPageText pg, Map m, int index)
        {
            var segs = b.Segs;
            double left = segs.Min(s => s.X), right = segs.Max(s => s.End);
            string align = AlignOf(b, pg.Width);
            var big = Biggest(segs[0]);
            // 파워포인트는 맑은 고딕의 한 줄 높이를 글꼴 파일의 1.33배가 아니라 1.2배쯤으로 그린다.
            double top = pg.Height - segs[0].Y - Math.Min(DeckFonts.Baseline(big.Font), 0.97) * big.Size;
            double height = 1.2 * big.Size;
            var shape = new DeckShape { IsTextBox = true, Wrap = false, Fit = "shape", Name = "글상자 " + index, X = m.X(left), Y = m.Y(top), W = m.L(right - left) };
            for (int i = 0; i < segs.Count; i++)
            {
                var seg = segs[i];
                var para = new DeckPara { Align = align };
                if (i > 0)
                {
                    double gap = segs[i - 1].Y - seg.Y;
                    para.LinePts = Math.Max(1, gap * m.S);
                    height += gap;
                }
                if (align == null && seg.X - left > 0.15 * seg.Size) para.MarL = m.L(seg.X - left);
                foreach (var piece in seg.Pieces)
                {
                    para.Runs.Add(new DeckRun
                    {
                        Text = piece.Text,
                        Size = piece.Size * m.S,
                        Font = piece.Font,
                        Color = piece.Rgb.ToString("X6", CultureInfo.InvariantCulture),
                        Bold = piece.Bold,
                        Italic = piece.Italic,
                        Spacing = (int)Math.Round(SpacingOf(piece) * m.S)
                    });
                }
                shape.Paras.Add(para);
            }
            shape.H = m.L(height);
            return shape;
        }

        // ── 스캔한 쪽 ──

        sealed class Zone
        {
            public PdfTextLine Line;
            public List<int[]> Boxes;
        }

        static double Median(IEnumerable<double> values)
        {
            var v = values.OrderBy(x => x).ToList();
            return v.Count == 0 ? 0 : v[v.Count / 2];
        }

        /// 글자 인식 결과에서 글상자로 옮길 줄을 고른다. 고른 줄의 상자는 뒤에서 지운다.
        static List<Zone> ZonesFromOcr(List<OcrLine> lines, Raster r, PdfPageText pg)
        {
            double ptPerPx = pg.Width / r.W;
            var zones = new List<Zone>();
            foreach (var l in lines)
            {
                double h = Median(l.Words.Select(w => w.H));
                if (h < 3) continue;
                // 세로로 쓴 글자를 가로로 잘못 읽으면 글자 수에 비해 폭이 터무니없이 좁다.
                if (l.Words.Sum(w => w.W) < 0.3 * l.Words.Sum(w => w.Text.Length) * h) continue;
                // 짧은 줄에서 한글 바로 뒤에 한자가 붙으면 잘못 읽은 것이다(동그라미 안의 굵은 글자 등). 그림에 그대로 둔다.
                if (l.Text.Replace(" ", "").Length <= 6 && LooksMisread(l.Text)) continue;
                int margin = (int)Math.Ceiling(Math.Max(2, h * 0.14));
                var boxes = l.Words.Select(w => r.Grow(new[] { (int)Math.Floor(w.X) - margin, (int)Math.Floor(w.Y) - margin, (int)Math.Ceiling(w.X + w.W) + margin, (int)Math.Ceiling(w.Y + w.H) + margin }, (int)Math.Ceiling(h * 0.6))).ToList();
                if (boxes.Count(r.FlatAround) * 2 < boxes.Count) continue;
                double size = h * ptPerPx * OcrSizeFactor;
                double y = pg.Height - Median(l.Words.Select(w => w.Y + w.H)) * ptPerPx + size * OcrBaselineLift;
                int rgb = r.TextColor(l.Words.Select(w => new[] { (int)Math.Floor(w.X), (int)Math.Floor(w.Y), (int)Math.Ceiling(w.X + w.W), (int)Math.Ceiling(w.Y + w.H) }));
                var line = new PdfTextLine { Y = y, Size = size };
                for (int i = 0; i < l.Words.Count; i++)
                {
                    var w = l.Words[i];
                    // 낱말 사이가 실제로 벌어져 있을 때만 띄어 쓴다(인식 엔진은 '목요 일'처럼 나눌 때가 있다).
                    bool space = i + 1 < l.Words.Count && (l.Words[i + 1].X - (w.X + w.W)) * ptPerPx >= 0.25 * size;
                    line.Runs.Add(new PdfTextRun { X = w.X * ptPerPx, End = (w.X + w.W) * ptPerPx, Y = y, Size = size, S = w.Text + (space ? " " : ""), Rgb = rgb });
                }
                zones.Add(new Zone { Line = line, Boxes = boxes });
            }
            SnapSizes(zones);
            return zones;
        }

        static bool LooksMisread(string s)
        {
            for (int i = 0; i + 1 < s.Length; i++)
            {
                if (s[i] >= 0xAC00 && s[i] <= 0xD7A3 && s[i + 1] >= 0x4E00 && s[i + 1] <= 0x9FFF) return true;
            }
            return false;
        }

        /// 인식한 글자 크기는 줄마다 조금씩 다르게 나온다. 비슷한 크기는 하나로 맞춘다.
        static void SnapSizes(List<Zone> zones)
        {
            var sorted = zones.OrderBy(z => z.Line.Size).ToList();
            int i = 0;
            while (i < sorted.Count)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1].Line.Size <= sorted[i].Line.Size * 1.07) j++;
                double size = Math.Round(sorted[(i + j) / 2].Line.Size * 2) / 2;
                for (int k = i; k <= j; k++)
                {
                    sorted[k].Line.Size = size;
                    foreach (var run in sorted[k].Line.Runs) run.Size = size;
                }
                i = j + 1;
            }
        }

        /// 스캔본 위에 얹혀 있던 보이지 않는 글자 층을 쓴다.
        static List<Zone> ZonesFromHidden(PdfPageText pg, Raster r)
        {
            double pxPerPt = r.W / pg.Width;
            var lines = new List<PdfTextLine>();
            foreach (var run in pg.HiddenRuns.OrderByDescending(x => x.Y).ThenBy(x => x.X))
            {
                var line = lines.LastOrDefault(l => Math.Abs(l.Y - run.Y) <= Math.Max(l.Size, run.Size) * 0.45);
                if (line == null)
                {
                    line = new PdfTextLine { Y = run.Y, Size = run.Size };
                    lines.Add(line);
                }
                line.Runs.Add(run);
            }
            var zones = new List<Zone>();
            foreach (var line in lines)
            {
                line.Runs.Sort((a, b) => a.X.CompareTo(b.X));
                var boxes = line.Runs.Select(run => r.Grow(new[]
                {
                    (int)Math.Floor(run.X * pxPerPt) - 2,
                    (int)Math.Floor((pg.Height - run.Y - run.Size * 0.92) * pxPerPt) - 2,
                    (int)Math.Ceiling(run.End * pxPerPt) + 2,
                    (int)Math.Ceiling((pg.Height - run.Y + run.Size * 0.24) * pxPerPt) + 2
                }, (int)Math.Ceiling(run.Size * pxPerPt * 0.4))).ToList();
                if (boxes.Count(r.FlatAround) * 2 < boxes.Count) continue;
                int rgb = r.TextColor(boxes);
                foreach (var run in line.Runs)
                {
                    run.Font = null;
                    run.Bold = false;
                    run.Italic = false;
                    run.Rgb = rgb;
                }
                zones.Add(new Zone { Line = line, Boxes = boxes });
            }
            return zones;
        }

        // ── 쪽 하나 ──

        /// PNG가 그리 크지 않으면(선·면 위주) 또렷한 PNG, 사진이 많으면 JPEG
        static DeckPicture PictureOf(Raster r, byte[] png)
        {
            var bmp = r.ToBitmap();
            byte[] jpeg = Imaging.Jpeg(bmp, 86);
            byte[] lossless = png ?? Imaging.Encode(bmp, "png", 0);
            bool usePng = lossless.Length <= jpeg.Length * 1.3;
            return new DeckPicture { Data = usePng ? lossless : jpeg, Type = usePng ? "png" : "jpeg", Name = "배경" };
        }

        static void Attach(DeckPage page, Raster r, byte[] png, Deck deck, PdfPageText pg, Map m)
        {
            if (r.Blank()) return;
            var pic = PictureOf(r, png);
            long w = m.L(pg.Width), h = m.L(pg.Height);
            // 슬라이드를 꽉 채우면 슬라이드 배경으로 깐다. 글상자를 고를 때 그림이 끌려 다니지 않는다.
            if (Math.Abs(w - deck.Cx) <= deck.Cx / 200 && Math.Abs(h - deck.Cy) <= deck.Cy / 200)
            {
                page.BackgroundPicture = pic;
                return;
            }
            pic.X = m.X(0);
            pic.Y = m.Y(0);
            pic.W = w;
            pic.H = h;
            page.Items.Add(pic);
        }

        static WinOcr TryOcr(Job job)
        {
            try
            {
                var ocr = job.Use(() => new WinOcr());
                if (!ocr.Korean && !job.Notes.Any(n => n.StartsWith("한국어 글자 인식", StringComparison.Ordinal)))
                {
                    job.Note("한국어 글자 인식이 이 PC에 없어 '" + ocr.LanguageTag + "' 인식으로 읽었어요.");
                }
                return ocr;
            }
            catch (UserFacingException)
            {
                return null;
            }
        }

        static long Slide(double pt)
        {
            return Math.Max(914400, Math.Min(51206400, (long)Math.Round(pt * 12700)));
        }

        static void AddBoxes(DeckPage page, IEnumerable<PdfTextLine> lines, PdfPageText pg, Map m)
        {
            var segs = new List<Seg>();
            foreach (var line in lines) segs.AddRange(SegmentsOfLine(line));
            // 쪽 바깥에 놓인 글자는 보이지 않던 글자다.
            segs.RemoveAll(s => s.End < 0 || s.X > pg.Width || s.Y < -s.Size || s.Y > pg.Height + s.Size);
            int n = 0;
            foreach (var b in Cluster(segs)) page.Items.Add(ToShape(b, pg, m, ++n));
        }

        /// 만들지 못하면 null(암호·손상 등). 부르는 쪽이 쪽을 그림으로 넣는 길로 돌아간다.
        public static PdfToPptResult Build(Job job, string pdf)
        {
            string dir = Util.TempDir("ppt");
            try
            {
                string stripped = Path.Combine(dir, "s.pdf");
                var layout = PdfText.ReadLayout(pdf, stripped, job);
                if (layout == null || layout.Pages.Count == 0 || !File.Exists(stripped)) return null;
                var firstPage = layout.Pages[0];
                if (firstPage.Width <= 0 || firstPage.Height <= 0) return null;
                var deck = new Deck { Cx = Slide(firstPage.Width), Cy = Slide(firstPage.Height) };
                deck.Palette.Dk1 = "000000";
                var result = new PdfToPptResult { Deck = deck };
                WinOcr ocr = null;
                bool triedOcr = false;
                using (var plain = new WinPdfDoc(stripped))
                using (var orig = new WinPdfDoc(pdf))
                {
                    int total = layout.Pages.Count;
                    if (plain.PageCount != total || orig.PageCount != total) return null;
                    for (int i = 0; i < total; i++)
                    {
                        job.Check();
                        if (total > 1) job.Report(Util.ShortName(pdf) + " · " + (i + 1) + "/" + total + "쪽", 0.3 + 0.7 * i / total);
                        var pg = layout.Pages[i];
                        if (pg.Width <= 0 || pg.Height <= 0)
                        {
                            pg.Width = firstPage.Width;
                            pg.Height = firstPage.Height;
                        }
                        var page = new DeckPage();
                        deck.Pages.Add(page);
                        double s = Math.Min(deck.Cx / (pg.Width * 12700), deck.Cy / (pg.Height * 12700));
                        var m = new Map { S = s, Ox = (deck.Cx - pg.Width * 12700 * s) / 2, Oy = (deck.Cy - pg.Height * 12700 * s) / 2 };
                        int visible = pg.Lines.Sum(l => l.Runs.Sum(r => r.S.Trim().Length));
                        int hidden = pg.HiddenRuns.Sum(r => r.S.Trim().Length);
                        bool layer = hidden >= 10 && hidden > visible * 2;
                        bool textual = !layer && visible >= 1 && pg.Bad <= pg.Chars * 0.03;
                        if (textual)
                        {
                            byte[] png = plain.RenderPng(i, Dpi, MaxSide);
                            Attach(page, Raster.From(Imaging.Decoder(png).Frames[0]), png, deck, pg, m);
                            AddBoxes(page, pg.Lines, pg, m);
                            result.TextPages++;
                            continue;
                        }
                        // 스캔한 쪽, 글자 대응표가 깨진 쪽
                        byte[] shot = orig.RenderPng(i, Dpi, MaxSide);
                        var raster = Raster.From(Imaging.Decoder(shot).Frames[0]);
                        var zones = new List<Zone>();
                        if (layer) zones = ZonesFromHidden(pg, raster);
                        else
                        {
                            if (!triedOcr)
                            {
                                ocr = TryOcr(job);
                                triedOcr = true;
                            }
                            if (ocr != null) zones = ZonesFromOcr(ocr.ReadLines(shot), raster, pg);
                        }
                        if (zones.Count == 0)
                        {
                            Attach(page, raster, shot, deck, pg, m);
                            result.PicturePages++;
                            continue;
                        }
                        foreach (var z in zones)
                        {
                            foreach (var b in z.Boxes) raster.Erase(b);
                        }
                        Attach(page, raster, null, deck, pg, m);
                        AddBoxes(page, zones.Select(z => z.Line), pg, m);
                        if (layer) result.LayerPages++;
                        else result.OcrPages++;
                    }
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UserFacingException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Write("PDF→PPT 글자 옮기기 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                return null;
            }
            finally
            {
                Util.TryDeleteDir(dir);
            }
        }
    }
}
