// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 시간 막대: 눈금(표시·구간), 본 영상(그림 띠·소리 모양), 자막, 글자·그림, 작은 화면, 소리 줄.
// 조각은 끌어서 순서를 바꾸고 앞뒤 끝을 끌어 자른다(놓을 때 뒤가 당겨 붙는다).
// 얹은 것·소리는 끌어서 옮기거나 길이를 바꾼다. 자석을 켜면 재생 위치·경계·표시에 붙는다. Ctrl+휠은 확대.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    sealed class VTimeline : FrameworkElement
    {
        public const double HeadW = 74;
        const double RulerH = 26, VideoH = 64, WaveH = 15, LaneH = 24, SoundLaneH = 30, RowGap = 5, Edge = 7, SnapPx = 9;
        static readonly Hue ClipHue = Theme.Teal, SubHue = Theme.Blue, DecoHue = Theme.Orange, PipHue = Theme.Purple, MusicHue = Theme.Green, VoiceHue = Theme.Pink, MarkHue = Theme.Yellow;
        static readonly Typeface Face = new Typeface(Theme.Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        static readonly Typeface FaceBold = new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

        public VProject Project;
        public double PxPerSec = 24;
        public double Scroll;
        public bool Magnet = true;
        public string Selected;
        public double RangeA = -1, RangeB = -1;
        /// 표시해 둘 구간들(시작, 끝). 조용한 곳 찾기 결과 등.
        public List<double[]> Marks;
        double playhead;

        /// 눈금·빈 곳을 눌러 넘기기
        public Action<double> Seek;
        /// 막대를 끄는 중(true)·놓음(false)
        public Action<bool> Scrubbing;
        /// 고른 것(id, 없으면 null)
        public Action<string> Select;
        /// 두 번 누름
        public Action<string> Open;
        /// 바꾸기 시작(본 영상 줄이 바뀌면 true)
        public Action<bool> Begin;
        /// 바꾸는 중(미리 보기 다시 그리기)
        public Action Moved;
        /// 바꾸기 끝
        public Action End;
        /// 확대·스크롤이 바뀜
        public Action ViewChanged;
        /// 구간(I~O)이 바뀜
        public Action RangeChanged;

        enum RowKind { Video, Sub, Deco, Pip, Sound }

        sealed class Block
        {
            public RowKind Row;
            public int Lane;
            public double A, B;
            public string Id;
            public object Obj;
            public Rect R;
        }

        enum DragMode { None, Scrub, Range, Marker, ClipPress, ClipMove, ClipTrimL, ClipTrimR, Body, LeftEdge, RightEdge }

        readonly List<Block> blocks = new List<Block>();
        readonly Dictionary<RowKind, double> rowTop = new Dictionary<RowKind, double>();
        readonly Dictionary<RowKind, double> rowH = new Dictionary<RowKind, double>();
        readonly Dictionary<RowKind, double> laneH = new Dictionary<RowKind, double>();
        readonly List<RowKind> rows = new List<RowKind>();
        readonly DrawingVisual head = new DrawingVisual();

        DragMode mode;
        Block dragBlock;
        VMarker dragMarker;
        Point downAt;
        double downT, origA, origB, origIn, origOut, origSrcIn, dragDelta;
        int insertAt = -1;
        bool begun;
        double snapLine = -1;
        string dragLabel;

        public VTimeline()
        {
            ClipToBounds = true;
            Focusable = true;
            FocusVisualStyle = null;
            AddVisualChild(head);
            SnapsToDevicePixels = true;
        }

        protected override int VisualChildrenCount
        {
            get { return 1; }
        }

        protected override Visual GetVisualChild(int index)
        {
            return head;
        }

        // ── 좌표 ──

        public double X(double t)
        {
            return HeadW + (t - Scroll) * PxPerSec;
        }

        public double TimeAt(double x)
        {
            return Scroll + (x - HeadW) / PxPerSec;
        }

        public double VisibleSeconds
        {
            get { return Math.Max(0.5, (ActualWidth - HeadW) / PxPerSec); }
        }

        double Duration
        {
            get { return Project == null ? 0 : Project.Duration; }
        }

        /// 전체가 한눈에 보이게
        public void FitAll()
        {
            double d = Math.Max(1, Duration);
            double w = Math.Max(100, ActualWidth - HeadW - 24);
            PxPerSec = Math.Max(0.5, Math.Min(400, w / d));
            Scroll = 0;
            InvalidateVisual();
            DrawHead();
            if (ViewChanged != null) ViewChanged();
        }

        public void ZoomBy(double factor, double aroundT)
        {
            double x = X(aroundT);
            PxPerSec = Math.Max(0.5, Math.Min(400, PxPerSec * factor));
            Scroll = aroundT - (x - HeadW) / PxPerSec;
            ClampScroll();
            InvalidateVisual();
            DrawHead();
            if (ViewChanged != null) ViewChanged();
        }

        void ClampScroll()
        {
            double max = Math.Max(0, Duration - VisibleSeconds * 0.5);
            Scroll = Math.Max(0, Math.Min(max, Scroll));
        }

        /// 재생 위치(follow면 화면 밖으로 나갈 때 따라 넘긴다)
        public void SetPlayhead(double t, bool follow)
        {
            playhead = t;
            if (follow && ActualWidth > HeadW + 40)
            {
                double x = X(t);
                if (x > ActualWidth - 30 || x < HeadW)
                {
                    Scroll = Math.Max(0, t - VisibleSeconds * 0.08);
                    InvalidateVisual();
                    if (ViewChanged != null) ViewChanged();
                }
            }
            DrawHead();
        }

        public double Playhead
        {
            get { return playhead; }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            DrawHead();
        }

        void DrawHead()
        {
            using (var dc = head.RenderOpen())
            {
                double x = X(playhead);
                if (x < HeadW - 1 || x > ActualWidth + 1) return;
                var pen = new Pen(Theme.Ink, 2);
                pen.Freeze();
                dc.DrawLine(pen, new Point(x, 4), new Point(x, ActualHeight));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x - 7, 2), true, true);
                    c.LineTo(new Point(x + 7, 2), true, true);
                    c.LineTo(new Point(x + 7, 10), true, true);
                    c.LineTo(new Point(x, 17), true, true);
                    c.LineTo(new Point(x - 7, 10), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(Theme.Ink, null, g);
            }
        }

        // ── 칸 나누기 ──

        void AddLaned(RowKind row, IEnumerable<KeyValuePair<object, string>> things, Func<object, double> a, Func<object, double> b)
        {
            var ends = new List<double>();
            foreach (var kv in things.OrderBy(x => a(x.Key)))
            {
                double s = a(kv.Key), e = Math.Max(s + 0.05, b(kv.Key));
                int lane = ends.FindIndex(x => x <= s + 1e-6);
                if (lane < 0)
                {
                    lane = ends.Count;
                    ends.Add(e);
                }
                else ends[lane] = e;
                blocks.Add(new Block { Row = row, Lane = lane, A = s, B = e, Id = kv.Value, Obj = kv.Key });
            }
        }

        static string RowName(RowKind r)
        {
            switch (r)
            {
                case RowKind.Video: return "영상";
                case RowKind.Sub: return "자막";
                case RowKind.Deco: return "글자·그림";
                case RowKind.Pip: return "작은 화면";
                default: return "소리";
            }
        }

        static Hue RowHue(RowKind r)
        {
            switch (r)
            {
                case RowKind.Video: return ClipHue;
                case RowKind.Sub: return SubHue;
                case RowKind.Deco: return DecoHue;
                case RowKind.Pip: return PipHue;
                default: return MusicHue;
            }
        }

        void Layout()
        {
            blocks.Clear();
            rows.Clear();
            var p = Project;
            if (p == null) return;
            double dur = p.Duration;
            double s = 0;
            foreach (var c in p.Clips)
            {
                blocks.Add(new Block { Row = RowKind.Video, A = s, B = s + c.Length, Id = c.Id, Obj = c });
                s += c.Length;
            }
            AddLaned(RowKind.Sub, p.Items.Where(i => i.Kind == "text" && i.Subtitle).Select(i => new KeyValuePair<object, string>(i, i.Id)), o => ((VItem)o).Start, o => ((VItem)o).End);
            AddLaned(RowKind.Deco, p.Items.Where(i => !(i.Kind == "text" && i.Subtitle) && i.Kind != "pip").Select(i => new KeyValuePair<object, string>(i, i.Id)), o => ((VItem)o).Start, o => ((VItem)o).End);
            AddLaned(RowKind.Pip, p.Items.Where(i => i.Kind == "pip").Select(i => new KeyValuePair<object, string>(i, i.Id)), o => ((VItem)o).Start, o => ((VItem)o).End);
            AddLaned(RowKind.Sound, p.Sounds.Select(x => new KeyValuePair<object, string>(x, x.Id)), o => ((VSound)o).Start, o =>
            {
                var x = (VSound)o;
                double e = x.Start + x.Length;
                if (x.Loop) e = Math.Max(e, dur);
                else if (x.IsMusic) e = Math.Min(e, Math.Max(dur, x.Start + 0.1));
                return e;
            });

            rows.Add(RowKind.Video);
            rows.Add(RowKind.Sub);
            if (blocks.Any(b => b.Row == RowKind.Deco)) rows.Add(RowKind.Deco);
            if (blocks.Any(b => b.Row == RowKind.Pip)) rows.Add(RowKind.Pip);
            rows.Add(RowKind.Sound);

            double avail = Math.Max(120, ActualHeight - RulerH - 4);
            double want = 0;
            var laneCount = new Dictionary<RowKind, int>();
            foreach (var r in rows)
            {
                int n = Math.Max(1, blocks.Where(b => b.Row == r).Select(b => b.Lane + 1).DefaultIfEmpty(1).Max());
                laneCount[r] = n;
                want += r == RowKind.Video ? VideoH : n * (r == RowKind.Sound ? SoundLaneH : LaneH);
                want += RowGap;
            }
            double k = want > avail ? Math.Max(0.45, avail / want) : 1;
            double y = RulerH + 4;
            foreach (var r in rows)
            {
                double lh = r == RowKind.Video ? VideoH * Math.Max(0.75, k) : (r == RowKind.Sound ? SoundLaneH : LaneH) * k;
                double h = r == RowKind.Video ? lh : lh * laneCount[r];
                rowTop[r] = y;
                rowH[r] = h;
                laneH[r] = r == RowKind.Video ? h : lh;
                y += h + RowGap * k;
            }
            foreach (var b in blocks)
            {
                double x0 = X(b.A), x1 = X(b.B);
                double top = rowTop[b.Row] + (b.Row == RowKind.Video ? 0 : b.Lane * laneH[b.Row]);
                double h = b.Row == RowKind.Video ? rowH[b.Row] : laneH[b.Row] - 3;
                b.R = new Rect(x0, top, Math.Max(3, x1 - x0 - (b.Row == RowKind.Video ? 2 : 1)), Math.Max(6, h));
            }
        }

        // ── 그리기 ──

        static FormattedText Txt(string s, double size, Brush b, bool bold)
        {
            return new FormattedText(s ?? "", Ko, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, b, 1.0);
        }

        static Pen MakePen(Brush b, double w)
        {
            var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return p;
        }

        static double NiceStep(double pxPerSec)
        {
            double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200 };
            foreach (var s in steps) if (s * pxPerSec >= 74) return s;
            return 1800;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            Layout();
            var p = Project;
            if (p == null) return;
            double dur = p.Duration;
            double xEnd = X(dur);

            // 영상 끝 뒤는 옅게
            if (xEnd < W) dc.DrawRectangle(Theme.Frozen(Theme.Hex(0xF4F1EB)), null, new Rect(Math.Max(HeadW, xEnd), RulerH, W - Math.Max(HeadW, xEnd), H - RulerH));

            // 구간
            if (RangeA >= 0 && RangeB > RangeA)
            {
                double a = Math.Max(HeadW, X(RangeA)), b = Math.Min(W, X(RangeB));
                if (b > a)
                {
                    dc.DrawRectangle(Theme.Frozen(Color.FromArgb(70, 0xFF, 0xD4, 0x47)), null, new Rect(a, 0, b - a, H));
                    var edge = MakePen(Theme.Frozen(Theme.Hex(0xD9A300)), 2);
                    dc.DrawLine(edge, new Point(X(RangeA), 0), new Point(X(RangeA), H));
                    dc.DrawLine(edge, new Point(X(RangeB), 0), new Point(X(RangeB), H));
                }
            }

            DrawRuler(dc, W);

            dc.PushClip(new RectangleGeometry(new Rect(HeadW, RulerH, Math.Max(0, W - HeadW), Math.Max(0, H - RulerH))));
            foreach (var b in blocks)
            {
                if (b.R.Right < HeadW - 2 || b.R.Left > W + 2) continue;
                if (mode == DragMode.ClipMove && dragBlock != null && b.Id == dragBlock.Id) continue;
                switch (b.Row)
                {
                    case RowKind.Video: DrawClip(dc, b, (VClip)b.Obj); break;
                    case RowKind.Sound: DrawSound(dc, b, (VSound)b.Obj); break;
                    default: DrawItem(dc, b, (VItem)b.Obj); break;
                }
            }
            DrawDragExtras(dc, W, H);
            dc.Pop();

            // 찾아 둔 구간(조용한 곳 찾기): 빨갛게
            var marks = Marks;
            if (marks != null)
            {
                var fill = Theme.Frozen(Color.FromArgb(60, 0xE5, 0x48, 0x3B));
                var top = Theme.Frozen(Theme.Hex(0xE5483B));
                foreach (var m in marks)
                {
                    double a = Math.Max(HeadW, X(m[0])), b = Math.Min(W, X(m[1]));
                    if (b <= a) continue;
                    dc.DrawRectangle(fill, null, new Rect(a, RulerH, Math.Max(1, b - a), Math.Max(0, H - RulerH)));
                    dc.DrawRectangle(top, null, new Rect(a, RulerH - 4, Math.Max(1, b - a), 4));
                }
            }

            // 줄 이름
            dc.DrawRectangle(Theme.Sheet, null, new Rect(0, 0, HeadW - 4, H));
            foreach (var r in rows)
            {
                var t = Txt(RowName(r), 12, RowHue(r).DeepBrush, true);
                double y = rowTop[r] + Math.Min(rowH[r], r == RowKind.Video ? rowH[r] : laneH[r]) / 2 - t.Height / 2;
                dc.DrawRoundedRectangle(RowHue(r).TintBrush, null, new Rect(4, y - 3, Math.Min(HeadW - 12, t.Width + 14), t.Height + 6), 8, 8);
                dc.DrawText(t, new Point(11, y));
            }
        }

        void DrawRuler(DrawingContext dc, double W)
        {
            double step = NiceStep(PxPerSec);
            double minor = step >= 10 ? step / 5 : step / 2;
            var tick = MakePen(Theme.Line2, 1);
            var major = MakePen(Theme.Ink3, 1.2);
            double t0 = Math.Floor(Scroll / minor) * minor;
            double tEnd = TimeAt(W);
            for (double t = t0; t <= tEnd + minor; t += minor)
            {
                double x = X(t);
                if (x < HeadW) continue;
                bool isMajor = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
                dc.DrawLine(isMajor ? major : tick, new Point(x, isMajor ? RulerH - 11 : RulerH - 6), new Point(x, RulerH - 1));
                if (isMajor)
                {
                    var label = Txt(VProject.Clock(t, step < 1), 11, Theme.Ink3, false);
                    dc.DrawText(label, new Point(x + 3, 3));
                }
            }
            dc.DrawLine(MakePen(Theme.Line, 1), new Point(HeadW, RulerH), new Point(W, RulerH));
            // 표시
            if (Project == null) return;
            foreach (var m in Project.Markers)
            {
                double x = X(m.T);
                if (x < HeadW - 6 || x > W + 6) continue;
                bool sel = m.Id == Selected;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x, RulerH - 2), true, true);
                    c.LineTo(new Point(x - 6, RulerH - 10), true, true);
                    c.LineTo(new Point(x - 6, RulerH - 20), true, true);
                    c.LineTo(new Point(x + 6, RulerH - 20), true, true);
                    c.LineTo(new Point(x + 6, RulerH - 10), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(MarkHue.VividBrush, sel ? MakePen(Theme.Ink, 1.6) : null, g);
                if (!string.IsNullOrEmpty(m.Label) && PxPerSec * 3 > 30)
                {
                    var t = Txt(m.Label, 10.5, Theme.Ink, true);
                    if (t.Width < 140) dc.DrawText(t, new Point(x + 8, RulerH - 22));
                }
            }
        }

        void Label(DrawingContext dc, Rect r, string text, Brush brush, double size)
        {
            if (r.Width < 22 || string.IsNullOrEmpty(text)) return;
            var t = Txt(text.Replace("\r", " ").Replace("\n", " "), size, brush, true);
            t.MaxTextWidth = Math.Max(8, r.Width - 12);
            t.MaxLineCount = 1;
            t.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(t, new Point(Math.Max(r.Left, HeadW) + 6, r.Top + (Math.Min(r.Height, 24) - t.Height) / 2));
        }

        void DrawClip(DrawingContext dc, Block b, VClip c)
        {
            var r = b.R;
            bool sel = b.Id == Selected;
            var geo = new RectangleGeometry(r, 9, 9);
            dc.DrawGeometry(ClipHue.TintBrush, null, geo);
            dc.PushClip(geo);
            double stripH = r.Height - WaveH;
            if (c.Kind == "color")
            {
                dc.DrawRectangle(Theme.Frozen(Theme.Hex(c.Rgb)), null, new Rect(r.Left, r.Top, r.Width, stripH));
            }
            else if (c.Kind == "photo")
            {
                var img = PhotoThumb(c.Path);
                if (img != null)
                {
                    double tw = stripH * img.PixelWidth / Math.Max(1.0, img.PixelHeight);
                    for (double x = r.Left; x < r.Right && x < ActualWidth; x += tw)
                    {
                        if (x + tw < HeadW) continue;
                        dc.DrawImage(img, new Rect(x, r.Top, tw, stripH));
                    }
                }
            }
            else
            {
                DrawStrip(dc, c, b, new Rect(r.Left, r.Top, r.Width, stripH));
            }
            // 소리 모양
            var waveRect = new Rect(r.Left, r.Top + stripH, r.Width, WaveH);
            dc.DrawRectangle(Theme.Frozen(Color.FromArgb(235, 0xFF, 0xFF, 0xFF)), null, waveRect);
            if (c.Kind == "video" && !c.Mute) DrawWave(dc, c.Path, waveRect, b.A, t => c.SourceAt(t - b.A), ClipHue.DeepBrush, c.Volume);
            else if (c.Kind == "video")
            {
                var m = Txt("소리 끔", 9.5, Theme.Ink3, true);
                dc.DrawText(m, new Point(Math.Max(r.Left, HeadW) + 6, waveRect.Top + 1));
            }
            // 이름·빠르기
            string name = c.Kind == "photo" ? Path.GetFileName(c.Path) : c.Kind == "color" ? "색 화면" : c.Kind == "freeze" ? "멈춘 장면" : Path.GetFileName(c.Path);
            var tags = new List<string>();
            if (Math.Abs(c.Speed - 1) > 0.001) tags.Add(c.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "배");
            if (c.Reverse) tags.Add("거꾸로");
            if (c.Kind == "video" && (c.Bright != 0 || c.Contrast != 0 || c.Sat != 0 || c.Warm != 0 || c.Sharp != 0 || !string.IsNullOrEmpty(c.Filter))) tags.Add("색");
            if (c.Redacts.Count > 0) tags.Add("가림 " + c.Redacts.Count(x => !x.Off));
            if (r.Width > 60)
            {
                string text = tags.Count > 0 ? string.Join(" · ", tags) + "  " + name : name;
                var t = Txt(text, 10.5, Theme.Ink, true);
                t.MaxTextWidth = Math.Max(8, r.Width - 14);
                t.MaxLineCount = 1;
                t.Trimming = TextTrimming.CharacterEllipsis;
                double lx = Math.Max(r.Left, HeadW) + 5;
                dc.DrawRoundedRectangle(Theme.Frozen(Color.FromArgb(225, 0xFF, 0xFF, 0xFF)), null, new Rect(lx - 2, r.Top + 3, Math.Min(t.Width + 8, r.Width - 6), t.Height + 2), 6, 6);
                dc.DrawText(t, new Point(lx + 2, r.Top + 4));
            }
            // 확대·옮기기 움직임 표시(키프레임): 영상은 원본 시각, 그 밖은 조각 안 시각
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
                KeyMark(dc, new Point(X(b.A + local), r.Top + stripH - 7), sel ? Theme.Ink : ClipHue.DeepBrush);
            }
            dc.Pop();
            // 전환 표시(앞 조각과 만나는 곳)
            if (c.Trans != "none" && c.TransSec > 0.01)
            {
                double x = r.Left;
                var g = new StreamGeometry();
                using (var cx = g.Open())
                {
                    double cy = r.Top + stripH / 2;
                    cx.BeginFigure(new Point(x, cy - 9), true, true);
                    cx.LineTo(new Point(x + 9, cy), true, true);
                    cx.LineTo(new Point(x, cy + 9), true, true);
                    cx.LineTo(new Point(x - 9, cy), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(Theme.Sheet, MakePen(ClipHue.DeepBrush, 1.6), g);
            }
            dc.DrawGeometry(null, sel ? MakePen(Theme.Ink, 2.6) : MakePen(Theme.Frozen(Color.FromArgb(60, 0x0D, 0x6F, 0x66)), 1), geo);
            if (sel && r.Width > 24)
            {
                // 끝 손잡이
                var hb = Theme.Ink;
                dc.DrawRoundedRectangle(hb, null, new Rect(r.Left + 1, r.Top + r.Height / 2 - 11, 5, 22), 2.5, 2.5);
                dc.DrawRoundedRectangle(hb, null, new Rect(r.Right - 6, r.Top + r.Height / 2 - 11, 5, 22), 2.5, 2.5);
            }
        }

        // 그림 띠 그림 기억
        readonly Dictionary<string, BitmapSource[]> stripCache = new Dictionary<string, BitmapSource[]>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, BitmapSource> photoCache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        BitmapSource PhotoThumb(string path)
        {
            BitmapSource b;
            if (photoCache.TryGetValue(path ?? "", out b)) return b;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelHeight = 96;
                bi.UriSource = new Uri(Path.GetFullPath(path));
                bi.EndInit();
                bi.Freeze();
                b = bi;
            }
            catch (Exception)
            {
                b = null;
            }
            photoCache[path ?? ""] = b;
            return b;
        }

        BitmapSource StripFrame(Filmstrip f, int i)
        {
            BitmapSource[] arr;
            if (!stripCache.TryGetValue(f.Source, out arr) || arr.Length != f.Frames.Length)
            {
                arr = new BitmapSource[f.Frames.Length];
                stripCache[f.Source] = arr;
            }
            if (i < 0 || i >= arr.Length) return null;
            if (arr[i] == null)
            {
                var px = f.Frames[i];
                if (px == null || f.W <= 0 || f.H <= 0 || px.Length < f.W * f.H * 4) return null;
                var bmp = BitmapSource.Create(f.W, f.H, 96, 96, PixelFormats.Bgr32, null, px, f.W * 4);
                bmp.Freeze();
                arr[i] = bmp;
            }
            return arr[i];
        }

        void DrawStrip(DrawingContext dc, VClip c, Block b, Rect r)
        {
            if (string.IsNullOrEmpty(c.Path) || !File.Exists(c.Path)) return;
            var f = Filmstrip.Get(c.Path);
            int n = f.Made;
            if (n <= 0 || f.W <= 0) return;
            var probe = VMedia.Probe(c.Path);
            int rot = probe == null ? 0 : ((probe.Rotation % 360) + 360) % 360;
            bool side = rot == 90 || rot == 270;
            double aspect = side ? f.H / (double)f.W : f.W / (double)f.H;
            double th = r.Height, tw = Math.Max(16, th * aspect);
            double first = Math.Max(r.Left, HeadW - tw);
            first = r.Left + Math.Floor((first - r.Left) / tw) * tw;
            for (double x = first; x < r.Right && x < ActualWidth; x += tw)
            {
                double mid = TimeAt(Math.Min(r.Right - 1, x + tw / 2));
                double src = c.Kind == "freeze" ? c.FreezeAt : c.SourceAt(mid - b.A);
                int i = (int)Math.Round(src / Math.Max(0.01, f.Step));
                if (i >= n) i = n - 1;
                if (i < 0) i = 0;
                var img = StripFrame(f, i);
                if (img == null) continue;
                var cell = new Rect(x, r.Top, tw, th);
                if (rot == 0)
                {
                    dc.DrawImage(img, cell);
                    continue;
                }
                dc.PushTransform(new RotateTransform(rot, cell.Left + cell.Width / 2, cell.Top + cell.Height / 2));
                double dw = side ? th : tw, dh = side ? tw : th;
                dc.DrawImage(img, new Rect(cell.Left + cell.Width / 2 - dw / 2, cell.Top + cell.Height / 2 - dh / 2, dw, dh));
                dc.Pop();
            }
        }

        /// 소리 모양: 원본 시간(map)을 따라 10ms마다의 가장 큰 값을 그린다
        void DrawWave(DrawingContext dc, string path, Rect r, double a, Func<double, double> map, Brush brush, double gain)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var tr = AudioCache.Get(path);
            if (!tr.Ready)
            {
                if (!tr.Failed && r.Width > 80)
                {
                    var m = Txt("소리 읽는 중 " + Math.Round(tr.Progress * 100) + "%", 9.5, Theme.Ink3, false);
                    dc.DrawText(m, new Point(Math.Max(r.Left, HeadW) + 6, r.Top + 1));
                }
                return;
            }
            var peak = tr.Peak;
            if (peak == null || peak.Length == 0) return;
            double perSec = AudioTrack.Rate / (double)AudioTrack.EnvStep;
            double x0 = Math.Max(r.Left, HeadW), x1 = Math.Min(r.Right, ActualWidth);
            if (x1 - x0 < 1) return;
            double mid = r.Top + r.Height / 2, half = r.Height / 2 - 1;
            var up = new List<Point>();
            var down = new List<Point>();
            double g = Math.Min(2.5, Math.Max(0.2, gain));
            for (double x = x0; x <= x1; x += 2)
            {
                double ta = TimeAt(x), tb = TimeAt(x + 2);
                double sa = map(ta), sb = map(tb);
                if (sb < sa)
                {
                    double tmp = sa;
                    sa = sb;
                    sb = tmp;
                }
                int ia = Math.Max(0, (int)(sa * perSec)), ib = Math.Min(peak.Length - 1, (int)Math.Ceiling(sb * perSec));
                float v = 0;
                for (int i = ia; i <= ib; i++) if (peak[i] > v) v = peak[i];
                double h = Math.Min(half, Math.Sqrt(v * g) * half);
                up.Add(new Point(x, mid - h));
                down.Add(new Point(x, mid + h));
            }
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(up[0], true, true);
                for (int i = 1; i < up.Count; i++) c.LineTo(up[i], false, false);
                for (int i = down.Count - 1; i >= 0; i--) c.LineTo(down[i], false, false);
            }
            geo.Freeze();
            dc.DrawGeometry(brush, null, geo);
        }

        static string ItemText(VItem it)
        {
            switch (it.Kind)
            {
                case "text": return string.IsNullOrWhiteSpace(it.Text) ? "(빈 글자)" : it.Text;
                case "image": return "그림 · " + Path.GetFileName(it.Path ?? "");
                case "sticker": return "스티커 · " + VOverlay.StickerLabel(it.Sticker);
                case "pip": return "작은 화면 · " + Path.GetFileName(it.Path ?? "");
                case "shape":
                    switch (it.Shape)
                    {
                        case "arrow": return "화살표";
                        case "box": return "네모";
                        case "circle": return "동그라미";
                        case "marker": return "형광펜";
                        case "line": return "선";
                        case "spot": return "마우스 강조";
                        case "ripple": return "누름 표시";
                        case "bar": return "이름 띠";
                        default: return "도형";
                    }
                default: return it.Kind;
            }
        }

        void DrawItem(DrawingContext dc, Block b, VItem it)
        {
            var hue = RowHue(b.Row);
            var r = b.R;
            bool sel = b.Id == Selected;
            var geo = new RectangleGeometry(r, 7, 7);
            dc.DrawGeometry(sel ? hue.VividBrush : hue.TintBrush, sel ? MakePen(Theme.Ink, 2) : MakePen(Theme.Frozen(Color.FromArgb(70, hue.Deep.R, hue.Deep.G, hue.Deep.B)), 1), geo);
            dc.PushClip(geo);
            Label(dc, r, ItemText(it), sel ? Theme.Ink : hue.DeepBrush, 11);
            dc.Pop();
            // 움직임 표시(키프레임): 그 시각에 ◆
            foreach (var k in it.Keys)
            {
                if (k.T < -1e-6 || k.T > it.Length + 1e-6) continue;
                KeyMark(dc, new Point(X(b.A + k.T), r.Bottom - 6), sel ? Theme.Ink : hue.DeepBrush);
            }
        }

        /// 움직임 표시 하나(◆). 흰 테두리를 둘러 띠 색 위에서도 보이게.
        void KeyMark(DrawingContext dc, Point at, Brush fill)
        {
            if (at.X < HeadW - 1 || at.X > ActualWidth + 6) return;
            const double s = 4.6;
            var g = new StreamGeometry();
            using (var x = g.Open())
            {
                x.BeginFigure(new Point(at.X, at.Y - s), true, true);
                x.LineTo(new Point(at.X + s, at.Y), true, false);
                x.LineTo(new Point(at.X, at.Y + s), true, false);
                x.LineTo(new Point(at.X - s, at.Y), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(fill, MakePen(Theme.Sheet, 1.4), g);
        }

        void DrawSound(DrawingContext dc, Block b, VSound s)
        {
            var hue = s.Kind == "voice" || s.Kind == "record" ? VoiceHue : MusicHue;
            var r = b.R;
            bool sel = b.Id == Selected;
            var geo = new RectangleGeometry(r, 8, 8);
            dc.DrawGeometry(sel ? hue.VividBrush : hue.TintBrush, null, geo);
            dc.PushClip(geo);
            double loopLen = Math.Max(0.05, s.Length);
            DrawWave(dc, s.Path, new Rect(r.Left, r.Top + 4, r.Width, r.Height - 6), b.A, t =>
            {
                double local = t - s.Start;
                if (s.Loop && local > loopLen) local %= loopLen;
                return s.In + Math.Max(0, local);
            }, Theme.Frozen(Color.FromArgb(120, hue.Deep.R, hue.Deep.G, hue.Deep.B)), s.Volume);
            string kind = s.Kind == "music" ? "배경 음악" : s.Kind == "record" ? "내 목소리" : s.Kind == "voice" ? "읽어 주기" : s.Kind == "effect" ? "효과음" : "소리";
            string name = !string.IsNullOrEmpty(s.Name) ? s.Name : Path.GetFileNameWithoutExtension(s.Path ?? "");
            Label(dc, r, kind + " · " + name, sel ? Theme.Ink : hue.DeepBrush, 10.5);
            dc.Pop();
            dc.DrawGeometry(null, sel ? MakePen(Theme.Ink, 2) : MakePen(Theme.Frozen(Color.FromArgb(70, hue.Deep.R, hue.Deep.G, hue.Deep.B)), 1), geo);
        }

        void DrawDragExtras(DrawingContext dc, double W, double H)
        {
            if (snapLine >= 0)
            {
                double x = X(snapLine);
                var pen = new Pen(Theme.Frozen(Theme.Hex(0xE5A100)), 1.6) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
                pen.Freeze();
                dc.DrawLine(pen, new Point(x, RulerH), new Point(x, H));
            }
            if (mode == DragMode.ClipMove && dragBlock != null)
            {
                // 놓을 자리
                if (insertAt >= 0 && Project != null)
                {
                    double t = Project.StartOf(insertAt);
                    if (insertAt >= Project.Clips.Count) t = Project.Duration;
                    double x = X(t);
                    dc.DrawRoundedRectangle(Theme.Ink, null, new Rect(x - 2, rowTop[RowKind.Video] - 3, 4, rowH[RowKind.Video] + 6), 2, 2);
                }
                var mouse = Mouse.GetPosition(this);
                var r = dragBlock.R;
                var ghost = new Rect(mouse.X - (downAt.X - r.Left), r.Top + 6, Math.Min(r.Width, 220), r.Height);
                dc.PushOpacity(0.78);
                dc.DrawRoundedRectangle(ClipHue.VividBrush, MakePen(Theme.Ink, 2), ghost, 9, 9);
                var c = (VClip)dragBlock.Obj;
                Label(dc, ghost, c.Kind == "video" || c.Kind == "photo" ? Path.GetFileName(c.Path) : "조각", Theme.Ink, 11);
                dc.Pop();
            }
            if ((mode == DragMode.ClipTrimL || mode == DragMode.ClipTrimR) && dragBlock != null && Math.Abs(dragDelta) > 1e-4)
            {
                var r = dragBlock.R;
                double cut;
                Rect shade;
                if (mode == DragMode.ClipTrimL)
                {
                    cut = r.Left + dragDelta * PxPerSec;
                    shade = dragDelta > 0 ? new Rect(r.Left, r.Top, cut - r.Left, r.Height) : new Rect(cut, r.Top, r.Left - cut, r.Height);
                }
                else
                {
                    cut = r.Right + dragDelta * PxPerSec;
                    shade = dragDelta < 0 ? new Rect(cut, r.Top, r.Right - cut, r.Height) : new Rect(r.Right, r.Top, cut - r.Right, r.Height);
                }
                bool cutting = mode == DragMode.ClipTrimL ? dragDelta > 0 : dragDelta < 0;
                var fill = cutting ? Theme.Frozen(Color.FromArgb(150, 0x22, 0x25, 0x2F)) : Theme.Frozen(Color.FromArgb(120, 0x38, 0xC7, 0xB8));
                dc.DrawRoundedRectangle(fill, null, shade, 6, 6);
                dc.DrawRoundedRectangle(Theme.Ink, null, new Rect(cut - 2.5, r.Top - 2, 5, r.Height + 4), 2.5, 2.5);
            }
            if (!string.IsNullOrEmpty(dragLabel))
            {
                var mouse = Mouse.GetPosition(this);
                var t = Txt(dragLabel, 11.5, Theme.Sheet, true);
                double x = Math.Min(W - t.Width - 14, Math.Max(HeadW + 2, mouse.X + 12));
                double y = Math.Max(RulerH + 2, mouse.Y - 30);
                dc.DrawRoundedRectangle(Theme.Ink, null, new Rect(x, y, t.Width + 12, t.Height + 6), 8, 8);
                dc.DrawText(t, new Point(x + 6, y + 3));
            }
        }

        // ── 고르기·끌기 ──

        Block HitBlock(Point p)
        {
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                var b = blocks[i];
                var r = b.R;
                r.Inflate(b.R.Width < 14 ? 4 : 0, 0);
                if (r.Contains(p)) return b;
            }
            return null;
        }

        VMarker HitMarker(Point p)
        {
            if (Project == null || p.Y > RulerH + 2) return null;
            VMarker best = null;
            double bd = 8;
            foreach (var m in Project.Markers)
            {
                double d = Math.Abs(X(m.T) - p.X);
                if (d < bd)
                {
                    bd = d;
                    best = m;
                }
            }
            return best;
        }

        /// 자석: 가까운 경계·재생 위치·표시에 붙인다
        double Snap(double t, string ignoreId, out double line)
        {
            line = -1;
            if (!Magnet || Project == null || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return t;
            double best = SnapPx / PxPerSec, got = t;
            Action<double> consider = delegate (double c)
            {
                double d = Math.Abs(c - t);
                if (d < best)
                {
                    best = d;
                    got = c;
                }
            };
            consider(0);
            consider(playhead);
            double s = 0;
            foreach (var c in Project.Clips)
            {
                consider(s);
                s += c.Length;
            }
            consider(s);
            foreach (var it in Project.Items)
            {
                if (it.Id == ignoreId) continue;
                consider(it.Start);
                consider(it.End);
            }
            foreach (var x in Project.Sounds)
            {
                if (x.Id == ignoreId) continue;
                consider(x.Start);
            }
            foreach (var m in Project.Markers) if (m.Id != ignoreId) consider(m.T);
            if (got != t) line = got;
            return got;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            var p = e.GetPosition(this);
            if (p.X < HeadW - 4 || Project == null) return;
            Layout();
            downAt = p;
            downT = Math.Max(0, TimeAt(p.X));
            begun = false;
            dragDelta = 0;
            snapLine = -1;
            dragLabel = null;
            dragBlock = null;
            dragMarker = null;
            insertAt = -1;
            CaptureMouse();
            e.Handled = true;
            if (p.Y <= RulerH)
            {
                var m = HitMarker(p);
                if (m != null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    dragMarker = m;
                    origA = m.T;
                    mode = DragMode.Marker;
                    if (Select != null) Select(m.Id);
                    if (e.ClickCount == 2 && Open != null) Open(m.Id);
                    return;
                }
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    mode = DragMode.Range;
                    RangeA = RangeB = Math.Min(Duration, downT);
                    InvalidateVisual();
                    return;
                }
                mode = DragMode.Scrub;
                if (Scrubbing != null) Scrubbing(true);
                if (Seek != null) Seek(Math.Min(Duration, downT));
                return;
            }
            var b = HitBlock(p);
            if (b == null)
            {
                if (Select != null) Select(null);
                mode = DragMode.Scrub;
                if (Scrubbing != null) Scrubbing(true);
                if (Seek != null) Seek(Math.Min(Duration, downT));
                return;
            }
            dragBlock = b;
            if (Select != null) Select(b.Id);
            if (e.ClickCount == 2)
            {
                ReleaseMouseCapture();
                mode = DragMode.None;
                if (Open != null) Open(b.Id);
                return;
            }
            bool left = p.X - b.R.Left < Edge && b.R.Width > Edge * 2.2;
            bool right = b.R.Right - p.X < Edge && b.R.Width > Edge * 2.2;
            origA = b.A;
            origB = b.B;
            if (b.Row == RowKind.Video)
            {
                mode = left ? DragMode.ClipTrimL : right ? DragMode.ClipTrimR : DragMode.ClipPress;
                return;
            }
            mode = left ? DragMode.LeftEdge : right ? DragMode.RightEdge : DragMode.Body;
            var it = b.Obj as VItem;
            var snd = b.Obj as VSound;
            if (it != null)
            {
                origA = it.Start;
                origB = it.End;
                origSrcIn = it.SrcIn;
                origOut = it.SrcOut;
            }
            if (snd != null)
            {
                origA = snd.Start;
                origIn = snd.In;
                origOut = snd.Out;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            if (mode == DragMode.None || !IsMouseCaptured)
            {
                Cursor = CursorAt(p);
                return;
            }
            double t = TimeAt(p.X);
            double dt = t - downT;
            switch (mode)
            {
                case DragMode.Scrub:
                    if (Seek != null) Seek(Math.Max(0, Math.Min(Duration, t)));
                    break;
                case DragMode.Range:
                    RangeB = Math.Max(0, Math.Min(Duration, t));
                    dragLabel = VProject.Clock(Math.Min(RangeA, RangeB), true) + " ~ " + VProject.Clock(Math.Max(RangeA, RangeB), true);
                    InvalidateVisual();
                    break;
                case DragMode.Marker:
                {
                    if (!StartChange(p, false)) break;
                    double line;
                    dragMarker.T = Math.Max(0, Math.Min(Duration, Snap(origA + dt, dragMarker.Id, out line)));
                    snapLine = line;
                    dragLabel = VProject.Clock(dragMarker.T, true);
                    if (Moved != null) Moved();
                    InvalidateVisual();
                    break;
                }
                case DragMode.ClipPress:
                    if (Math.Abs(p.X - downAt.X) > 5)
                    {
                        mode = DragMode.ClipMove;
                        goto case DragMode.ClipMove;
                    }
                    break;
                case DragMode.ClipMove:
                    insertAt = InsertIndex(p.X);
                    dragLabel = null;
                    InvalidateVisual();
                    break;
                case DragMode.ClipTrimL:
                case DragMode.ClipTrimR:
                    TrimMove(t);
                    break;
                case DragMode.Body:
                case DragMode.LeftEdge:
                case DragMode.RightEdge:
                    if (!StartChange(p, false)) break;
                    MoveThing(dt);
                    if (Moved != null) Moved();
                    InvalidateVisual();
                    break;
            }
        }

        bool StartChange(Point p, bool clips)
        {
            if (begun) return true;
            if (Math.Abs(p.X - downAt.X) < 3) return false;
            begun = true;
            if (Begin != null) Begin(clips);
            return true;
        }

        int InsertIndex(double x)
        {
            var p = Project;
            var c = (VClip)dragBlock.Obj;
            double t = TimeAt(x);
            double s = 0;
            int idx = 0;
            for (int i = 0; i < p.Clips.Count; i++)
            {
                double len = p.Clips[i].Length;
                if (t > s + len / 2) idx = i + 1;
                s += len;
            }
            return idx;
        }

        double SourceSeconds(string path)
        {
            var pr = VMedia.Probe(path);
            return pr != null && pr.Seconds > 0 ? pr.Seconds : 1e9;
        }

        void TrimMove(double t)
        {
            var c = (VClip)dragBlock.Obj;
            double a = origA, b = origB, len = b - a;
            double d, line;
            if (mode == DragMode.ClipTrimL)
            {
                double edge = Snap(a + (t - downT), c.Id, out line);
                d = edge - a;
                double maxCut = len - 0.1, maxGrow;
                if (c.Kind == "video") maxGrow = (c.Reverse ? SourceSeconds(c.Path) - c.Out : c.In) / Math.Max(0.05, c.Speed);
                else maxGrow = 600;
                d = Math.Max(-maxGrow, Math.Min(maxCut, d));
                snapLine = Math.Abs(a + d - line) < 1e-6 ? line : -1;
                dragDelta = d;
                if (Seek != null) Seek(d >= 0 ? a + d : a);
                dragLabel = (d >= 0 ? "앞 " + d.ToString("0.0") + "초 자르기" : "앞으로 " + (-d).ToString("0.0") + "초 늘리기") + " · 길이 " + VProject.Clock(len - d, true);
            }
            else
            {
                double edge = Snap(b + (t - downT), c.Id, out line);
                d = edge - b;
                double maxCut = len - 0.1, maxGrow;
                if (c.Kind == "video") maxGrow = (c.Reverse ? c.In : SourceSeconds(c.Path) - c.Out) / Math.Max(0.05, c.Speed);
                else maxGrow = 600;
                d = Math.Max(-maxCut, Math.Min(maxGrow, d));
                snapLine = Math.Abs(b + d - line) < 1e-6 ? line : -1;
                dragDelta = d;
                if (Seek != null) Seek(d <= 0 ? Math.Max(a, b + d - 0.04) : Math.Max(a, b - 0.04));
                dragLabel = (d <= 0 ? "뒤 " + (-d).ToString("0.0") + "초 자르기" : "뒤로 " + d.ToString("0.0") + "초 늘리기") + " · 길이 " + VProject.Clock(len + d, true);
            }
            InvalidateVisual();
        }

        void ApplyTrim()
        {
            var c = (VClip)dragBlock.Obj;
            double d = dragDelta;
            if (Math.Abs(d) < 0.01) return;
            if (Begin != null) Begin(true);
            double sp = Math.Max(0.05, c.Speed);
            if (mode == DragMode.ClipTrimL)
            {
                if (c.Kind == "video")
                {
                    if (!c.Reverse) c.In = Math.Max(0, c.In + d * sp);
                    else c.Out = Math.Min(SourceSeconds(c.Path), c.Out - d * sp);
                }
                else c.Dur = Math.Max(0.1, c.Dur - d);
            }
            else
            {
                if (c.Kind == "video")
                {
                    if (!c.Reverse) c.Out = Math.Min(SourceSeconds(c.Path), c.Out + d * sp);
                    else c.In = Math.Max(0, c.In - d * sp);
                }
                else c.Dur = Math.Max(0.1, c.Dur + d);
            }
            if (End != null) End();
        }

        void MoveThing(double dt)
        {
            var it = dragBlock.Obj as VItem;
            var snd = dragBlock.Obj as VSound;
            double dur = Duration;
            double line = -1, l2;
            if (it != null)
            {
                double len = origB - origA;
                if (mode == DragMode.Body)
                {
                    double a = origA + dt;
                    double sa = Snap(a, it.Id, out line), sb = Snap(a + len, it.Id, out l2);
                    if (line >= 0 && (l2 < 0 || Math.Abs(sa - a) <= Math.Abs(sb - a - len))) a = sa;
                    else if (l2 >= 0)
                    {
                        a = sb - len;
                        line = l2;
                    }
                    a = Math.Max(0, Math.Min(Math.Max(0, dur - 0.1), a));
                    it.Start = a;
                    it.End = a + len;
                    dragLabel = VProject.Clock(it.Start, true) + " ~ " + VProject.Clock(it.End, true);
                }
                else if (mode == DragMode.LeftEdge)
                {
                    double a = Math.Max(0, Math.Min(origB - 0.1, Snap(origA + dt, it.Id, out line)));
                    it.Start = a;
                    if (it.Kind == "pip") it.SrcIn = Math.Max(0, origSrcIn + (a - origA));
                    dragLabel = "시작 " + VProject.Clock(it.Start, true) + " · " + it.Length.ToString("0.0") + "초";
                }
                else
                {
                    double b = Math.Max(origA + 0.1, Snap(origB + dt, it.Id, out line));
                    if (it.Kind == "pip" && origOut > it.SrcIn) b = Math.Min(b, it.Start + Math.Max(0.1, SourceSeconds(it.Path) - it.SrcIn));
                    it.End = b;
                    if (it.Kind == "pip") it.SrcOut = it.SrcIn + it.Length;
                    dragLabel = "끝 " + VProject.Clock(it.End, true) + " · " + it.Length.ToString("0.0") + "초";
                }
            }
            else if (snd != null)
            {
                if (mode == DragMode.Body)
                {
                    snd.Start = Math.Max(0, Snap(origA + dt, snd.Id, out line));
                    dragLabel = "시작 " + VProject.Clock(snd.Start, true);
                }
                else if (mode == DragMode.LeftEdge)
                {
                    double ns = Snap(origA + dt, snd.Id, out line);
                    double d = ns - origA;
                    double nin = Math.Max(0, Math.Min(origOut - 0.1, origIn + d));
                    snd.In = nin;
                    snd.Start = Math.Max(0, origA + (nin - origIn));
                    dragLabel = "앞 " + (nin - origIn).ToString("0.0") + "초 자름";
                }
                else
                {
                    double end0 = origA + (origOut - origIn);
                    double ne = Snap(end0 + dt, snd.Id, out line);
                    double nout = Math.Max(snd.In + 0.1, Math.Min(SourceSeconds(snd.Path), origOut + (ne - end0)));
                    snd.Out = nout;
                    dragLabel = "길이 " + snd.Length.ToString("0.0") + "초";
                }
            }
            snapLine = line;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            var m = mode;
            mode = DragMode.None;
            try
            {
                switch (m)
                {
                    case DragMode.Scrub:
                        if (Scrubbing != null) Scrubbing(false);
                        break;
                    case DragMode.Range:
                        if (RangeB < RangeA)
                        {
                            double x = RangeA;
                            RangeA = RangeB;
                            RangeB = x;
                        }
                        if (RangeB - RangeA < 0.1) RangeA = RangeB = -1;
                        if (RangeChanged != null) RangeChanged();
                        break;
                    case DragMode.ClipPress:
                        if (Seek != null) Seek(Math.Min(Duration, downT));
                        break;
                    case DragMode.ClipMove:
                    {
                        var c = (VClip)dragBlock.Obj;
                        int from = Project.Clips.IndexOf(c);
                        int to = insertAt;
                        if (to > from) to--;
                        if (from >= 0 && to >= 0 && to != from)
                        {
                            if (Begin != null) Begin(true);
                            Project.Clips.RemoveAt(from);
                            Project.Clips.Insert(Math.Min(to, Project.Clips.Count), c);
                            if (Project.Clips[0].Trans != "none" && Project.Clips[0].Trans != "black" && Project.Clips[0].Trans != "white") Project.Clips[0].Trans = "none";
                            if (End != null) End();
                        }
                        break;
                    }
                    case DragMode.ClipTrimL:
                    case DragMode.ClipTrimR:
                        mode = m;
                        ApplyTrim();
                        mode = DragMode.None;
                        break;
                    case DragMode.Body:
                    case DragMode.LeftEdge:
                    case DragMode.RightEdge:
                    case DragMode.Marker:
                        if (begun && End != null) End();
                        break;
                }
            }
            finally
            {
                begun = false;
                snapLine = -1;
                dragLabel = null;
                insertAt = -1;
                dragDelta = 0;
                if (IsMouseCaptured) ReleaseMouseCapture();
                InvalidateVisual();
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (mode == DragMode.None) return;
            // 끌다가 놓쳤으면(다른 창이 뜨는 등) 지금까지 한 대로 끝낸다
            var m = mode;
            mode = DragMode.None;
            if (m == DragMode.Scrub && Scrubbing != null) Scrubbing(false);
            if ((m == DragMode.Body || m == DragMode.LeftEdge || m == DragMode.RightEdge || m == DragMode.Marker) && begun && End != null) End();
            begun = false;
            snapLine = -1;
            dragLabel = null;
            InvalidateVisual();
        }

        Cursor CursorAt(Point p)
        {
            if (p.X < HeadW - 4) return Cursors.Arrow;
            if (p.Y <= RulerH) return HitMarker(p) != null ? Cursors.SizeWE : Cursors.Hand;
            Layout();
            var b = HitBlock(p);
            if (b == null) return Cursors.Arrow;
            bool edge = (p.X - b.R.Left < Edge || b.R.Right - p.X < Edge) && b.R.Width > Edge * 2.2;
            return edge ? Cursors.SizeWE : Cursors.Hand;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            var p = e.GetPosition(this);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                ZoomBy(e.Delta > 0 ? 1.25 : 0.8, TimeAt(Math.Max(HeadW, p.X)));
            }
            else
            {
                Scroll -= e.Delta / 120.0 * 70 / PxPerSec;
                ClampScroll();
                InvalidateVisual();
                DrawHead();
                if (ViewChanged != null) ViewChanged();
            }
            e.Handled = true;
        }
    }
}
