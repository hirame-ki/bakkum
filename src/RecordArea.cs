// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 화면 녹화: 녹화할 곳 고르기의 계산(비율, 꼭짓점·변 손잡이, 옮기기, 화면 안에 두기, 짝수 크기).
// 창(AreaPicker)과 떼어 두어 창 없이 시험한다. 좌표는 모니터 왼쪽 위를 0으로 본 실제 픽셀(배율을 거치지 않음).
using System;
using System.Windows;

namespace Bakkum
{
    /// 고른 곳에서 마우스가 잡은 것
    enum AreaGrip { None, Inside, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    /// 손을 뗐을 때 일어난 일
    enum AreaRelease { Nothing, Chosen, TooSmall }

    /// 고를 수 있는 비율: 자유 + 영상 편집의 화면 비율과 같은 것
    static class AreaRatio
    {
        public static readonly string[] Ids = { "free", "16:9", "9:16", "1:1", "4:5", "4:3", "3:4" };
        public static readonly string[] Labels = { "자유", "16:9 가로", "9:16 세로", "1:1", "4:5", "4:3", "3:4" };

        /// 모르는 값은 자유로
        public static string Known(string id)
        {
            return Array.IndexOf(Ids, id) >= 0 ? id : "free";
        }

        /// "16:9" → 16, 9. 자유이거나 모르는 값이면 false.
        public static bool Parse(string id, out int a, out int b)
        {
            a = 0;
            b = 0;
            if (string.IsNullOrEmpty(id)) return false;
            int i = id.IndexOf(':');
            if (i <= 0) return false;
            int x, y;
            if (!int.TryParse(id.Substring(0, i), out x) || !int.TryParse(id.Substring(i + 1), out y) || x <= 0 || y <= 0) return false;
            a = x;
            b = y;
            return true;
        }

        /// 가로÷세로. 자유면 0.
        public static double Of(string id)
        {
            int a, b;
            return Parse(id, out a, out b) ? (double)a / b : 0;
        }
    }

    static class AreaMath
    {
        /// anchor를 제자리에 두고 pointer 쪽으로 편 사각형. k(가로÷세로)가 0보다 크면 그 비율을 지킨다.
        /// 화면(w×h) 밖으로 나가지 않고, 가로·세로가 min보다 작아지지 않는다(화면 끝이라 자리가 모자라면 자리만큼).
        public static Rect Drag(Point anchor, Point pointer, double k, double w, double h, double min)
        {
            double ax = Clamp(anchor.X, 0, w), ay = Clamp(anchor.Y, 0, h);
            double dx = pointer.X - ax, dy = pointer.Y - ay;
            bool left = dx < 0, up = dy < 0;
            double roomX = left ? ax : w - ax, roomY = up ? ay : h - ay;
            double rw, rh;
            if (k > 0)
            {
                // 마우스가 있는 쪽으로 더 많이 간 축을 따른다(마우스가 늘 사각형 끝에 닿게)
                rw = Math.Max(Math.Abs(dx), Math.Abs(dy) * k);
                rw = Math.Max(rw, Math.Max(min, min * k));
                rw = Math.Min(rw, Math.Min(roomX, roomY * k));
                rh = rw / k;
            }
            else
            {
                rw = Math.Min(Math.Max(Math.Abs(dx), min), roomX);
                rh = Math.Min(Math.Max(Math.Abs(dy), min), roomY);
            }
            return new Rect(left ? ax - rw : ax, up ? ay - rh : ay, rw, rh);
        }

        /// 변 하나를 p로 옮긴다(자유 비율). 맞은편 변은 그대로이고, 맞은편을 넘어가면 뒤집힌다.
        public static Rect Edge(Rect r, AreaGrip side, Point p, double w, double h, double min)
        {
            double s, len;
            switch (side)
            {
                case AreaGrip.Left:
                    Stretch(r.Right, p.X, w, min, true, out s, out len);
                    return new Rect(s, r.Top, len, r.Height);
                case AreaGrip.Right:
                    Stretch(r.Left, p.X, w, min, false, out s, out len);
                    return new Rect(s, r.Top, len, r.Height);
                case AreaGrip.Top:
                    Stretch(r.Bottom, p.Y, h, min, true, out s, out len);
                    return new Rect(r.Left, s, r.Width, len);
                case AreaGrip.Bottom:
                    Stretch(r.Top, p.Y, h, min, false, out s, out len);
                    return new Rect(r.Left, s, r.Width, len);
            }
            return r;
        }

        /// 한 축: fixedV는 그대로 두고 moving 쪽으로 편다. 길이는 min 이상, [0, limit] 안.
        /// moving이 fixedV와 같으면 원래 쪽(before: 앞쪽이었는지)을 지킨다.
        static void Stretch(double fixedV, double moving, double limit, double min, bool before, out double start, out double length)
        {
            double m = Clamp(moving, 0, limit);
            bool back = m < fixedV || (m == fixedV && before);
            double len = Math.Max(Math.Abs(m - fixedV), min);
            len = Math.Min(len, back ? fixedV : limit - fixedV);
            start = back ? fixedV - len : fixedV;
            length = len;
        }

        /// 크기는 그대로 dx·dy만큼 옮긴다. 화면 밖으로는 나가지 않는다.
        public static Rect Move(Rect r, double dx, double dy, double w, double h)
        {
            return new Rect(Clamp(r.X + dx, 0, Math.Max(0, w - r.Width)), Clamp(r.Y + dy, 0, Math.Max(0, h - r.Height)), r.Width, r.Height);
        }

        /// 비율을 바꿀 때: 가운데와 넓이를 지키며 k(가로÷세로)로 바꾸고, 화면 안에 넣는다.
        public static Rect Refit(Rect r, double k, double w, double h)
        {
            if (k <= 0) return r;
            double rw = Math.Sqrt(Math.Max(1, r.Width * r.Height) * k), rh = rw / k;
            double fit = Math.Min(1, Math.Min(w / rw, h / rh));
            rw *= fit;
            rh *= fit;
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            return new Rect(Clamp(cx - rw / 2, 0, w - rw), Clamp(cy - rh / 2, 0, h - rh), rw, rh);
        }

        /// 정수 픽셀로 맞춘다: 가로·세로는 짝수(H.264), 비율 a:b가 있으면 딱 그 비율, min 이상, 화면(w×h) 안.
        /// (fx, fy)는 r 안에서 제자리에 둘 점(0 = 왼쪽·위, 0.5 = 가운데, 1 = 오른쪽·아래).
        public static Rect Settle(Rect r, int a, int b, double fx, double fy, int w, int h, int min)
        {
            int rw, rh;
            if (a > 0 && b > 0)
            {
                // 가로 a·n, 세로 b·n이 둘 다 짝수여야 한다. a·b 중 하나라도 홀수면 n을 짝수로(2씩) 고른다.
                int g = a % 2 == 0 && b % 2 == 0 ? 1 : 2;
                int ua = a * g, ub = b * g;
                int n = (int)Math.Round((r.Width / ua + r.Height / ub) / 2);
                int nMin = Math.Max((min + ua - 1) / ua, (min + ub - 1) / ub);
                int nMax = Math.Min(w / ua, h / ub);
                n = Math.Max(1, Math.Min(Math.Max(n, nMin), nMax));
                rw = ua * n;
                rh = ub * n;
            }
            else
            {
                int lo = (min + 1) & ~1;
                rw = Math.Min(Math.Max(Even(r.Width), lo), w & ~1);
                rh = Math.Min(Math.Max(Even(r.Height), lo), h & ~1);
            }
            int x = (int)Math.Round(r.X + fx * (r.Width - rw));
            int y = (int)Math.Round(r.Y + fy * (r.Height - rh));
            return new Rect(Math.Max(0, Math.Min(x, w - rw)), Math.Max(0, Math.Min(y, h - rh)), rw, rh);
        }

        /// p가 r의 어디를 잡았는지. grip: 손잡이를 잡는 거리. edges가 false면(정한 비율) 변 손잡이는 없다.
        public static AreaGrip Hit(Rect r, Point p, double grip, bool edges)
        {
            double dl = Math.Abs(p.X - r.Left), dr = Math.Abs(p.X - r.Right), dt = Math.Abs(p.Y - r.Top), db = Math.Abs(p.Y - r.Bottom);
            bool nearL = dl <= grip, nearR = dr <= grip, nearT = dt <= grip, nearB = db <= grip;
            // 아주 작은 영역이면 가까운 쪽 하나만
            if (nearL && nearR)
            {
                if (dl <= dr) nearR = false;
                else nearL = false;
            }
            if (nearT && nearB)
            {
                if (dt <= db) nearB = false;
                else nearT = false;
            }
            if (nearT && nearL) return AreaGrip.TopLeft;
            if (nearT && nearR) return AreaGrip.TopRight;
            if (nearB && nearL) return AreaGrip.BottomLeft;
            if (nearB && nearR) return AreaGrip.BottomRight;
            bool inX = p.X >= r.Left && p.X <= r.Right, inY = p.Y >= r.Top && p.Y <= r.Bottom;
            if (edges)
            {
                if (nearL && inY) return AreaGrip.Left;
                if (nearR && inY) return AreaGrip.Right;
                if (nearT && inX) return AreaGrip.Top;
                if (nearB && inX) return AreaGrip.Bottom;
            }
            return inX && inY ? AreaGrip.Inside : AreaGrip.None;
        }

        public static bool IsCorner(AreaGrip g)
        {
            return g == AreaGrip.TopLeft || g == AreaGrip.TopRight || g == AreaGrip.BottomLeft || g == AreaGrip.BottomRight;
        }

        /// 손잡이가 잡는 점(꼭짓점, 변의 가운데)
        public static Point GripPoint(Rect r, AreaGrip g)
        {
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            switch (g)
            {
                case AreaGrip.TopLeft: return r.TopLeft;
                case AreaGrip.Top: return new Point(cx, r.Top);
                case AreaGrip.TopRight: return r.TopRight;
                case AreaGrip.Right: return new Point(r.Right, cy);
                case AreaGrip.BottomRight: return r.BottomRight;
                case AreaGrip.Bottom: return new Point(cx, r.Bottom);
                case AreaGrip.BottomLeft: return r.BottomLeft;
                default: return new Point(r.Left, cy);
            }
        }

        /// 손잡이를 끄는 동안 제자리에 있는 점(꼭짓점은 맞은편 꼭짓점, 변은 맞은편 변)
        public static Point Fixed(Rect r, AreaGrip g)
        {
            switch (g)
            {
                case AreaGrip.TopLeft: return r.BottomRight;
                case AreaGrip.TopRight: return r.BottomLeft;
                case AreaGrip.BottomLeft: return r.TopRight;
                case AreaGrip.Left: return r.TopRight;
                case AreaGrip.Top: return r.BottomLeft;
                default: return r.TopLeft;
            }
        }

        /// v가 [lo, hi]의 어느 끝에 붙어 있는지(0 = 앞, 1 = 뒤)
        public static double Side(double lo, double hi, double v)
        {
            return Math.Abs(v - lo) <= Math.Abs(v - hi) ? 0 : 1;
        }

        static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        static int Even(double v)
        {
            return 2 * (int)Math.Round(v / 2);
        }
    }

    /// 녹화할 곳 고르기: 끌어서 그리고 → 손잡이로 다듬고 → 정한다. 계산만 하고 그리기는 AreaPicker가 한다.
    /// 정한 비율이면 꼭짓점 손잡이만(비율 그대로), 자유면 변 손잡이도 쓴다. 안을 끌면 옮기고, 밖을 끌면 새로 그린다.
    sealed class AreaSelect
    {
        /// 가장 작은 녹화 크기(실제 픽셀)
        public const int Smallest = 64;

        readonly int w, h;
        string ratio;
        bool pressed, drawing;
        AreaGrip held = AreaGrip.None;
        Point from, grabbed, still;
        Rect start, live;
        Rect? chosen;

        public AreaSelect(int width, int height, string ratio)
        {
            w = width;
            h = height;
            this.ratio = AreaRatio.Known(ratio);
        }

        public string Ratio
        {
            get { return ratio; }
        }

        /// 자유 비율이면 변 손잡이도 쓴다
        public bool Free
        {
            get { return ratio == "free"; }
        }

        /// 정한 영역(짝수 정수 픽셀). 아직 없으면 null.
        public Rect? Chosen
        {
            get { return chosen; }
        }

        /// 누른 채 있는지
        public bool Pressed
        {
            get { return pressed; }
        }

        /// 새로 그리는 중인지(그리는 동안은 손잡이를 감춘다)
        public bool Drawing
        {
            get { return pressed && drawing; }
        }

        /// 끄는 손잡이(새로 그리거나 누르지 않았으면 None)
        public AreaGrip Held
        {
            get { return pressed ? held : AreaGrip.None; }
        }

        /// 화면에 보일 영역: 끄는 동안은 끄는 대로, 아니면 정한 영역
        public Rect? Shown
        {
            get { return pressed && (drawing || held != AreaGrip.None) ? live : chosen; }
        }

        /// 지금 손을 떼면 정해질 크기(끄는 동안 크기 글에 쓴다)
        public Rect? Planned
        {
            get
            {
                if (!(pressed && (drawing || held != AreaGrip.None))) return chosen;
                return SettleLive();
            }
        }

        /// 정한 영역을 화면 전체를 1로 본 비율로
        public Rect? Fraction
        {
            get
            {
                if (chosen == null) return null;
                var c = chosen.Value;
                return new Rect(c.X / w, c.Y / h, c.Width / w, c.Height / h);
            }
        }

        public AreaGrip Hit(Point p, double grip)
        {
            return chosen == null ? AreaGrip.None : AreaMath.Hit(chosen.Value, p, grip, Free);
        }

        /// 눌렀다. 손잡이·안쪽이면 그것을 끌고, 밖이면 (조금 끈 뒤부터) 새로 그린다.
        public void Press(Point p, double grip)
        {
            pressed = true;
            drawing = false;
            from = p;
            held = Hit(p, grip);
            if (held == AreaGrip.None) return;
            start = chosen.Value;
            live = start;
            still = AreaMath.Fixed(start, held);
            grabbed = AreaMath.GripPoint(start, held);
        }

        /// 누른 채 움직였다. 보이는 영역이 바뀌었으면 true. slop: 새로 그리기를 시작하는 거리.
        public bool Move(Point p, double slop)
        {
            if (!pressed) return false;
            double k = AreaRatio.Of(ratio);
            if (held == AreaGrip.None)
            {
                if (!drawing)
                {
                    if (Math.Abs(p.X - from.X) < slop && Math.Abs(p.Y - from.Y) < slop) return false;
                    drawing = true;
                }
                live = AreaMath.Drag(from, p, k, w, h, 0);
                return true;
            }
            if (held == AreaGrip.Inside)
            {
                live = AreaMath.Move(start, p.X - from.X, p.Y - from.Y, w, h);
                return true;
            }
            // 손잡이는 누른 곳과의 차이만큼 움직인다(누르자마자 튀지 않게)
            var q = new Point(grabbed.X + p.X - from.X, grabbed.Y + p.Y - from.Y);
            live = AreaMath.IsCorner(held) ? AreaMath.Drag(still, q, k, w, h, Smallest) : AreaMath.Edge(start, held, q, w, h, Smallest);
            return true;
        }

        /// 손을 뗐다. 그냥 누른 것이면 고른 곳은 그대로(Nothing), 너무 작게 그렸으면 TooSmall.
        public AreaRelease Release(Point p, double slop)
        {
            if (!pressed) return AreaRelease.Nothing;
            Move(p, slop);
            var settled = SettleLive();
            bool wasDrawing = drawing;
            var g = held;
            Abort();
            if (g == AreaGrip.None)
            {
                if (!wasDrawing) return AreaRelease.Nothing;
                if (live.Width < Smallest || live.Height < Smallest) return AreaRelease.TooSmall;
            }
            chosen = settled;
            return AreaRelease.Chosen;
        }

        /// 끄던 것을 그만둔다(고른 곳은 그대로)
        public void Abort()
        {
            pressed = false;
            drawing = false;
            held = AreaGrip.None;
        }

        /// 비율을 바꾼다. 고른 곳이 있으면 가운데·넓이를 지키며 새 비율로 맞춘다(자유로 바꾸면 그대로).
        public void SetRatio(string id)
        {
            ratio = AreaRatio.Known(id);
            if (chosen == null || pressed) return;
            double k = AreaRatio.Of(ratio);
            if (k > 0) chosen = SettleFrac(AreaMath.Refit(chosen.Value, k, w, h), 0.5, 0.5);
        }

        /// 화살표 키: 고른 곳을 dx·dy 픽셀 옮긴다
        public void Nudge(int dx, int dy)
        {
            if (chosen == null || pressed) return;
            chosen = AreaMath.Move(chosen.Value, dx, dy, w, h);
        }

        /// 이 영역을 고른 것으로(정한 비율이면 가운데를 지키며 그 비율로). 그림 확인·시험에 쓴다.
        public void Choose(Rect r)
        {
            double k = AreaRatio.Of(ratio);
            if (k > 0) r = AreaMath.Refit(r, k, w, h);
            chosen = SettleFrac(r, 0.5, 0.5);
        }

        Rect SettleLive()
        {
            if (held == AreaGrip.Inside) return SettleFrac(live, 0, 0);
            Point fix = held == AreaGrip.None ? from : still;
            return SettleFrac(live, AreaMath.Side(live.Left, live.Right, fix.X), AreaMath.Side(live.Top, live.Bottom, fix.Y));
        }

        Rect SettleFrac(Rect r, double fx, double fy)
        {
            int a, b;
            AreaRatio.Parse(ratio, out a, out b);
            return AreaMath.Settle(r, a, b, fx, fy, w, h, Smallest);
        }
    }
}
