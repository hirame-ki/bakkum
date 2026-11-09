// 파일을 끌다가 키를 누르거나 '보내기'로 열면 커서 자리에 뜨는 메뉴.
// 모양은 색인(index, 기본)·부채(fan)·물결(wave)·벌집(honeycomb). 칸 하나가 색종이 한 장이고(할 일마다 색이 정해져 있음),
// 마우스를 올리면 진한 색으로 바뀌며 톡 튀어나온다. 모양과 색은 DESIGN.md '색종이'를 따른다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Bakkum
{
    class MenuCell
    {
        public MenuAction Action;
        public ActionOption Option;
        public Hue Hue = Theme.Slate;
        public bool IsCenter;
        /// 즐겨찾기: 첫 화면에 바로 나오는 고를 거리(Action과 Option이 둘 다 있다)
        public bool IsFavorite;
        public int Number;
        public Canvas Root;
        public Point TopLeft;
        /// 쉬던 자리의 모양(메뉴 좌표). 마우스가 들어왔는지는 이것으로 본다.
        public Geometry Rest;
        /// 칸 안 좌표의 모양. 튀어나온 칸 위에 마우스가 있는지 볼 때 쓴다.
        public Geometry Local;
        public DropShadowEffect Shade;
        public SolidColorBrush Fill, TextBrush, SubBrush, ChipBrush, GlyphBrush, BadgeBrush, BadgeTextBrush, CapBrush, CapTextBrush;
        public TextBlock LabelText, SubText;
        public ScaleTransform Scale;
        public TranslateTransform Move;
        public Vector Bulge;
        public double HoverScale = 1.08;
        public double Spring = 0.9;
        public bool Enabled = true;
    }

    /// 칸 하나의 한 순간 색(쉬는 때·올라간 때·못 쓰는 때)
    class CellLook
    {
        public Color Fill, Text, Sub, Chip, Glyph, Badge, BadgeText, Cap, CapText, Shadow;
        public double Blur, Depth, Opacity;
    }

    public class MenuWindow : Window
    {
        public static MenuWindow Current;

        const double DisabledOpacity = 0.82;
        static readonly Color White = Colors.White;

        readonly Canvas canvas = new Canvas();
        readonly List<MenuCell> cells = new List<MenuCell>();
        readonly string shape;
        readonly Point cursorDip;
        readonly Rect work;
        bool dragMode;
        readonly bool animate;
        List<string> files;
        MenuAction chosen;
        bool choosing, closing, dropped, dialogOpen;
        MenuCell hot;
        MenuCell center;
        readonly DispatcherTimer poll;
        // 끌기로 열리면 놓을 파일을 창 위에 올 때에야 안다. 맞는 칸만 한 번에 나타나도록 그때까지 칸을 숨겨 둔다.
        bool waitingForFiles;
        readonly DateTime openedAt = DateTime.Now;
        DateTime releasedAt = DateTime.MinValue;
        Point origin;

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr hdc, int index);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT p);

        public static double DpiScale()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            int dpi = GetDeviceCaps(dc, 88);
            ReleaseDC(IntPtr.Zero, dc);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }

        public static System.Drawing.Point CursorPx()
        {
            POINT p;
            GetCursorPos(out p);
            return new System.Drawing.Point(p.X, p.Y);
        }

        public static void ShowAt(System.Drawing.Point px, bool dragMode, List<string> files)
        {
            if (Current != null) Current.CloseNow();
            var w = new MenuWindow(px, dragMode, files, AppSettings.Current.MenuShape, true);
            Current = w;
            w.Show();
        }

        /// 미리보기에서 커서가 있다고 치는 자리(화면 픽셀). 시험할 때 화면 끝 자리로 바꿔 뒤집힌 모양을 본다.
        public static System.Drawing.Point PreviewAt = new System.Drawing.Point(900, 600);

        /// 메뉴 모양 그림(설정 창 미리보기·시험용). chooseAction을 주면 '놓은 뒤 고르기' 화면을 그린다.
        public static BitmapSource Preview(string shapeName, int hotIndex, List<string> files, string chooseAction, double scale)
        {
            var w = new MenuWindow(PreviewAt, false, files, shapeName, false);
            if (!string.IsNullOrEmpty(chooseAction))
            {
                var parts = chooseAction.Split('/');
                var a = MenuAction.All.FirstOrDefault(x => x.Id == parts[0]);
                if (a != null)
                {
                    w.chosen = a;
                    w.choosing = true;
                    var opts = a.OptionsFor(w.files);
                    string caption = a.Label;
                    for (int k = 1; k < parts.Length; k++)
                    {
                        var o = opts.FirstOrDefault(x => x.Id == parts[k]);
                        if (o == null || !o.HasChildren) break;
                        caption = o.Label;
                        opts = o.Children;
                    }
                    var items = new List<MenuCell>();
                    int n = 1;
                    foreach (var o in opts) items.Add(new MenuCell { Option = o, Number = n++ });
                    w.Layout(items, caption, "닫기");
                }
            }
            var plain = w.cells.Where(c => !c.IsCenter).ToList();
            if (hotIndex >= 0 && plain.Count > 0) w.SetHotInstant(plain[Math.Min(hotIndex, plain.Count - 1)]);
            w.poll.Stop();
            var c0 = w.canvas;
            w.Content = null;
            c0.Measure(new Size(c0.Width, c0.Height));
            c0.Arrange(new Rect(0, 0, c0.Width, c0.Height));
            c0.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(c0.Width * scale), (int)Math.Ceiling(c0.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(c0);
            rtb.Freeze();
            w.Close();
            return rtb;
        }

        /// 시험용: 메뉴 모양을 PNG 파일로 그린다(바탕은 책상색).
        public static void RenderPreview(string shapeName, string outPng, int hotIndex, List<string> files, string chooseAction)
        {
            var img = Preview(shapeName, hotIndex, files, chooseAction, 2);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Theme.Desk, null, new Rect(0, 0, img.Width, img.Height));
                dc.DrawImage(img, new Rect(0, 0, img.Width, img.Height));
            }
            var rtb = new RenderTargetBitmap(img.PixelWidth, img.PixelHeight, 192, 192, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(outPng)) enc.Save(fs);
        }

        MenuWindow(System.Drawing.Point px, bool dragMode, List<string> files, string shapeName, bool animate)
        {
            this.dragMode = dragMode;
            this.files = files ?? new List<string>();
            this.animate = animate;
            shape = shapeName;
            double s = DpiScale();
            cursorDip = new Point(px.X / s, px.Y / s);
            var wa = System.Windows.Forms.Screen.FromPoint(px).WorkingArea;
            work = new Rect(wa.Left / s, wa.Top / s, wa.Width / s, wa.Height / s);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = !dragMode;
            AllowDrop = true;
            Title = AppInfo.Name;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Focusable = true;
            Content = canvas;

            waitingForFiles = dragMode && animate && this.files.Count == 0;
            BuildMain();

            DragEnter += OnDragEnter;
            DragOver += OnDragOver;
            DragLeave += OnDragLeave;
            Drop += OnDrop;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnClick;
            MouseRightButtonUp += OnRightClick;
            MouseLeave += delegate { if (!this.dragMode) SetHot(null); };
            KeyDown += OnKey;
            Deactivated += delegate { if (!this.dragMode && !dialogOpen && !closing) CloseNow(); };

            poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            poll.Tick += OnPoll;
            poll.Start();
        }

        // ── 칸 만들기 ──

        void BuildMain()
        {
            var items = new List<MenuCell>();
            // 놓을 파일을 알면 즐겨찾기를 맨 앞에, 그 파일에 맞는 칸만 보인다. 숫자는 칸마다 그대로라 손에 익는다.
            if (files.Count > 0)
            {
                foreach (var fav in Favorites.Applicable(files))
                {
                    items.Add(new MenuCell { Action = fav.Key, Option = fav.Value, IsFavorite = true });
                }
            }
            int n = 1;
            foreach (var a in MenuAction.Ordered())
            {
                int number = n++;
                if (files.Count > 0 && !a.CanTake(files)) continue;
                items.Add(new MenuCell { Action = a, Number = number });
            }
            Layout(items, CenterCaption(), CenterSub());
        }

        void ShowOptions(string caption, List<ActionOption> opts)
        {
            choosing = true;
            dragMode = false;
            currentCaption = caption;
            currentOptions = opts;
            var items = new List<MenuCell>();
            int n = 1;
            foreach (var o in opts) items.Add(new MenuCell { Option = o, Number = n++ });
            Layout(items, caption, "닫기");
            Activate();
            Focus();
        }

        string currentCaption;
        List<ActionOption> currentOptions;

        string CenterCaption()
        {
            if (choosing && chosen != null) return chosen.Label;
            if (files.Count > 0)
            {
                int plain = files.Count(f => !ArchiveJobs.IsFolder(f));
                return plain == 0 ? "폴더 " + files.Count + "개" : "파일 " + plain + "개";
            }
            return "닫기";
        }

        string CenterSub()
        {
            if (choosing) return "닫기";
            return files.Count > 0 ? "닫기" : "";
        }

        void Layout(List<MenuCell> items, string centerCaption, string centerSub)
        {
            canvas.Children.Clear();
            cells.Clear();
            hot = null;
            center = null;
            foreach (var c in items)
            {
                if (c.Action != null) c.Hue = Theme.HueOf(c.Action.Id);
                else if (chosen != null) c.Hue = Theme.HueOf(chosen.Id);
            }
            switch (shape)
            {
                case "fan": LayoutFan(items, centerCaption, centerSub); break;
                case "wave": LayoutWave(items, centerCaption, centerSub); break;
                case "honeycomb": LayoutHoney(items, centerCaption, centerSub); break;
                default: LayoutIndex(items, centerCaption, centerSub); break;
            }
            RefreshEnabled();
            Place();
            if (animate) AnimateIn();
        }

        static string LabelOf(MenuCell c)
        {
            if (c.Option != null) return c.Option.Label;
            return c.Action.Label;
        }

        static string SubOf(MenuCell c)
        {
            if (c.IsFavorite) return c.Action.Label;
            if (c.Option != null) return c.Option.Sub;
            return "";
        }

        /// 색인처럼 칸이 넓으면 할 일 칸에도 도움말(받는 파일)을 단다.
        static string HintOf(MenuCell c)
        {
            if (c.IsFavorite) return c.Action.Label + " · 즐겨찾기";
            if (c.Option != null) return c.Option.Sub;
            return c.Action.Hint;
        }

        bool Pinned(MenuCell c)
        {
            return chosen != null && c.Option != null && !c.IsFavorite && !c.Option.HasChildren && Favorites.Has(chosen, c.Option);
        }

        /// 고를 거리 칸 귀퉁이의 별(즐겨찾기로 고정됨)
        void PutStar(MenuCell c, Point chipAt)
        {
            if (!Pinned(c)) return;
            var g = new Grid { Width = 19, Height = 19, IsHitTestVisible = false };
            g.Children.Add(new Ellipse { Fill = c.BadgeBrush, Stroke = Brushes.White, StrokeThickness = 1.6 });
            var star = Theme.Glyph("star", 12, c.BadgeTextBrush, 1.7);
            star.HorizontalAlignment = HorizontalAlignment.Center;
            star.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(star);
            Put(c, g, chipAt + new Vector(12.5, -12.5), true, true);
        }

        /// 좁은 칸에서는 띄어쓰기에서 두 줄로 나눈다. 한 글자만 남는 쪽이 생기면 나누지 않는다("이 PC에서").
        static string TwoLines(string s)
        {
            int best = -1;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != ' ') continue;
                if (i < 2 || s.Length - i - 1 < 2) continue;
                if (best < 0 || Math.Abs(i - s.Length / 2.0) < Math.Abs(best - s.Length / 2.0)) best = i;
            }
            return best > 0 ? s.Substring(0, best) + "\n" + s.Substring(best + 1) : s;
        }

        static double TextWidth(string s, double size)
        {
            var t = new TextBlock { Text = s, FontFamily = Theme.Font, FontSize = size, FontWeight = FontWeights.Bold };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return t.DesiredSize.Width;
        }

        /// 한 줄에 다 들어가면 그대로, 넘치면 두 줄로.
        static string Fit(string s, double size, double width)
        {
            if (string.IsNullOrEmpty(s) || TextWidth(s, size) <= width) return s;
            return TwoLines(s);
        }

        static Point Polar(Point c, double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        static Vector Unit(double deg)
        {
            double a = deg * Math.PI / 180;
            return new Vector(Math.Cos(a), Math.Sin(a));
        }

        static List<Point> HexPoints(Point c, double r)
        {
            var pts = new List<Point>();
            for (int i = 0; i < 6; i++) pts.Add(Polar(c, r, 60 * i - 90));
            return pts;
        }

        /// 모서리를 둥글린 볼록 다각형. 꼭짓점마다 두 변에 닿는 원호로 깎는다.
        static Geometry RoundedPolygon(IList<Point> pts, double radius)
        {
            int n = pts.Count;
            double area = 0;
            for (int i = 0; i < n; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % n];
                area += a.X * b.Y - b.X * a.Y;
            }
            var sweep = area > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
            var t1 = new Point[n];
            var t2 = new Point[n];
            var rr = new double[n];
            for (int i = 0; i < n; i++)
            {
                Point p = pts[i], prev = pts[(i + n - 1) % n], next = pts[(i + 1) % n];
                Vector v1 = prev - p, v2 = next - p;
                double l1 = v1.Length, l2 = v2.Length;
                v1 /= l1;
                v2 /= l2;
                double cos = Math.Max(-1, Math.Min(1, v1.X * v2.X + v1.Y * v2.Y));
                double half = Math.Acos(cos) / 2;
                double d = Math.Min(radius / Math.Tan(half), Math.Min(l1, l2) / 2);
                t1[i] = p + v1 * d;
                t2[i] = p + v2 * d;
                rr[i] = d * Math.Tan(half);
            }
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(t2[0], true, true);
                for (int k = 1; k <= n; k++)
                {
                    int i = k % n;
                    ctx.LineTo(t1[i], true, true);
                    ctx.ArcTo(t2[i], new Size(rr[i], rr[i]), 0, false, sweep, true, true);
                }
            }
            g.Freeze();
            return g;
        }

        /// 둥근 부채살: 안쪽 반지름 r0~바깥 r1, 각도 a0~a1. 이웃과 gap만큼 떨어지고 네 모서리를 rad로 둥글린다.
        static Geometry RoundedSector(Point o, double r0, double r1, double a0, double a1, double rad, double gap)
        {
            if (a1 < a0)
            {
                double t = a0;
                a0 = a1;
                a1 = t;
            }
            double h = gap / 2;
            double A0 = a0 * Math.PI / 180, A1 = a1 * Math.PI / 180;
            // 모서리가 너무 크면 칸이 좁아 겹치므로 줄인다.
            for (int guard = 0; guard < 20; guard++)
            {
                double dIn0 = Math.Asin(Math.Min(1, (rad + h) / (r0 + rad)));
                if (A1 - A0 > 2 * dIn0 + 0.02 || rad < 2) break;
                rad *= 0.8;
            }
            double R1c = r1 - rad, R0c = r0 + rad;
            double dOut = Math.Asin(Math.Min(1, (rad + h) / R1c));
            double dIn = Math.Asin(Math.Min(1, (rad + h) / R0c));
            var u0 = new Vector(Math.Cos(A0), Math.Sin(A0));
            var n0 = new Vector(-Math.Sin(A0), Math.Cos(A0));
            var u1 = new Vector(Math.Cos(A1), Math.Sin(A1));
            var n1 = new Vector(-Math.Sin(A1), Math.Cos(A1));
            Point f0in = o + u0 * (R0c * Math.Cos(dIn)) + n0 * h;
            Point f0out = o + u0 * (R1c * Math.Cos(dOut)) + n0 * h;
            Point f1out = o + u1 * (R1c * Math.Cos(dOut)) - n1 * h;
            Point f1in = o + u1 * (R0c * Math.Cos(dIn)) - n1 * h;
            Point out0 = o + new Vector(Math.Cos(A0 + dOut), Math.Sin(A0 + dOut)) * r1;
            Point out1 = o + new Vector(Math.Cos(A1 - dOut), Math.Sin(A1 - dOut)) * r1;
            Point in1 = o + new Vector(Math.Cos(A1 - dIn), Math.Sin(A1 - dIn)) * r0;
            Point in0 = o + new Vector(Math.Cos(A0 + dIn), Math.Sin(A0 + dIn)) * r0;
            bool largeOut = (A1 - dOut) - (A0 + dOut) > Math.PI;
            bool largeIn = (A1 - dIn) - (A0 + dIn) > Math.PI;
            var cw = SweepDirection.Clockwise;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(f0in, true, true);
                ctx.LineTo(f0out, true, true);
                ctx.ArcTo(out0, new Size(rad, rad), 0, false, cw, true, true);
                ctx.ArcTo(out1, new Size(r1, r1), 0, largeOut, cw, true, true);
                ctx.ArcTo(f1out, new Size(rad, rad), 0, false, cw, true, true);
                ctx.LineTo(f1in, true, true);
                ctx.ArcTo(in1, new Size(rad, rad), 0, false, cw, true, true);
                ctx.ArcTo(in0, new Size(r0, r0), 0, largeIn, SweepDirection.Counterclockwise, true, true);
                ctx.ArcTo(f0in, new Size(rad, rad), 0, false, cw, true, true);
            }
            g.Freeze();
            return g;
        }

        MenuCell AddCell(MenuCell c, Geometry g)
        {
            Rect b = g.Bounds;
            var root = new Canvas { Width = b.Width, Height = b.Height, IsHitTestVisible = false };
            Canvas.SetLeft(root, b.Left);
            Canvas.SetTop(root, b.Top);
            var local = g.Clone();
            local.Transform = new TranslateTransform(-b.Left, -b.Top);
            local.Freeze();
            c.Rest = g;
            c.Local = local;
            c.TopLeft = b.TopLeft;

            c.Fill = new SolidColorBrush();
            c.TextBrush = new SolidColorBrush();
            c.SubBrush = new SolidColorBrush();
            c.ChipBrush = new SolidColorBrush();
            c.GlyphBrush = new SolidColorBrush();
            c.BadgeBrush = new SolidColorBrush();
            c.BadgeTextBrush = new SolidColorBrush();
            c.CapBrush = new SolidColorBrush();
            c.CapTextBrush = new SolidColorBrush();
            c.Shade = new DropShadowEffect { Direction = 270, RenderingBias = RenderingBias.Quality };
            root.Children.Add(new ShapePath { Data = local, Fill = c.Fill, Effect = c.Shade });

            c.Scale = new ScaleTransform(1, 1);
            c.Move = new TranslateTransform(0, 0);
            var tg = new TransformGroup();
            tg.Children.Add(c.Scale);
            tg.Children.Add(c.Move);
            root.RenderTransform = tg;
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            c.Root = root;
            canvas.Children.Add(root);
            cells.Add(c);
            return c;
        }

        /// 칸 안에 조각을 놓는다. at은 메뉴 좌표, 가운데 맞춤이면 조각의 가운데가 at에 온다.
        static void Put(MenuCell c, FrameworkElement e, Point at, bool centerX, bool centerY)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = !double.IsNaN(e.Width) ? e.Width : e.DesiredSize.Width;
            double h = !double.IsNaN(e.Height) ? e.Height : e.DesiredSize.Height;
            Canvas.SetLeft(e, at.X - c.TopLeft.X - (centerX ? w / 2 : 0));
            Canvas.SetTop(e, at.Y - c.TopLeft.Y - (centerY ? h / 2 : 0));
            c.Root.Children.Add(e);
        }

        /// 할 일 그림 동그라미(고를 거리 칸은 그림 대신 숫자)
        static FrameworkElement MakeChip(MenuCell c, double size)
        {
            var g = new Grid { Width = size, Height = size, IsHitTestVisible = false };
            g.Children.Add(new Ellipse { Fill = c.ChipBrush });
            if (c.IsFavorite)
            {
                var star = Theme.Glyph("star", size * 0.6, c.GlyphBrush, size >= 34 ? 2 : 1.85);
                star.HorizontalAlignment = HorizontalAlignment.Center;
                star.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(star);
            }
            else if (c.Option != null)
            {
                var num = new TextBlock
                {
                    Text = c.Number.ToString(),
                    FontFamily = Theme.Font,
                    FontSize = Math.Round(size * 0.45),
                    FontWeight = FontWeights.Bold,
                    Foreground = c.GlyphBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 1)
                };
                g.Children.Add(num);
            }
            else
            {
                var icon = Theme.Glyph(c.Action.Id, size * 0.58, c.GlyphBrush, size >= 34 ? 2 : 1.85);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                icon.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(icon);
            }
            return g;
        }

        /// 그림 동그라미 귀퉁이의 숫자 단추(숫자 키로 고를 수 있다는 표시)
        static FrameworkElement MakeBadge(MenuCell c)
        {
            var g = new Grid { Width = 19, Height = 19, IsHitTestVisible = false };
            g.Children.Add(new Ellipse { Fill = c.BadgeBrush, Stroke = Brushes.White, StrokeThickness = 1.6 });
            g.Children.Add(new TextBlock
            {
                Text = c.Number.ToString(),
                FontFamily = Theme.Font,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = c.BadgeTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1)
            });
            return g;
        }

        /// 색인 띠 오른쪽 끝의 숫자 키 알약
        static FrameworkElement MakeKeycap(MenuCell c)
        {
            var t = new TextBlock
            {
                Text = c.Number.ToString(),
                FontFamily = Theme.Font,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = c.CapTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1)
            };
            return new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(8), Background = c.CapBrush, Child = t, IsHitTestVisible = false };
        }

        static StackPanel MakeLabels(MenuCell c, string label, string sub, double size, double width, TextAlignment align)
        {
            var stack = new StackPanel { Width = width, IsHitTestVisible = false };
            c.LabelText = new TextBlock
            {
                Text = label,
                FontFamily = Theme.Font,
                FontSize = size,
                FontWeight = FontWeights.Bold,
                Foreground = c.TextBrush,
                TextAlignment = align,
                LineHeight = size + 3,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            stack.Children.Add(c.LabelText);
            if (!string.IsNullOrEmpty(sub))
            {
                c.SubText = new TextBlock
                {
                    Text = sub,
                    FontFamily = Theme.Font,
                    FontSize = 11.5,
                    Foreground = c.SubBrush,
                    TextAlignment = align,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                stack.Children.Add(c.SubText);
            }
            return stack;
        }

        MenuCell AddCenter(Geometry g, string caption, string sub, double width, double size)
        {
            var c = new MenuCell { IsCenter = true, HoverScale = 1.06, Spring = 0.5 };
            AddCell(c, g);
            var labels = MakeLabels(c, Fit(caption, size, width), sub, size, width, TextAlignment.Center);
            Put(c, labels, origin, true, true);
            center = c;
            return c;
        }

        // 색인(기본): 커서 옆 흰 배지 + 세로로 쌓인 색 띠. 올리면 바깥으로 쏙 빠져나온다.
        void LayoutIndex(List<MenuCell> items, string caption, string sub)
        {
            double W = 236, H = 54, gap = 7, badge = 72, gapX = 12, slide = 20, m = 40;
            int n = items.Count;
            // 화면이 낮으면 띠를 얇게 눌러 모두 보이게 한다.
            double avail = work.Height - 2 * m;
            if (n * H + Math.Max(0, n - 1) * gap > avail)
            {
                gap = 5;
                H = Math.Max(36, Math.Floor((avail - (n - 1) * gap) / Math.Max(1, n)));
            }
            bool compact = H < 48;
            double chipSize = compact ? 28 : 34;
            double total = n * H + Math.Max(0, n - 1) * gap;
            double width = m + badge + gapX + W + slide + 8 + m;
            double height = Math.Max(total, badge) + m * 2;
            canvas.Width = width;
            canvas.Height = height;
            bool left = cursorDip.X + width - m - badge / 2 > work.Right;
            origin = left ? new Point(width - m - badge / 2, height / 2) : new Point(m + badge / 2, height / 2);
            AddCenter(new RectangleGeometry(new Rect(origin.X - badge / 2, origin.Y - badge / 2, badge, badge), 22, 22), caption, sub, badge - 10, 13.5);
            double top = height / 2 - total / 2;
            for (int i = 0; i < n; i++)
            {
                var c = items[i];
                double y = top + i * (H + gap);
                double x0 = left ? origin.X - badge / 2 - gapX - W : origin.X + badge / 2 + gapX;
                AddCell(c, new RectangleGeometry(new Rect(x0, y, W, H), 16, 16));
                Point chipAt = new Point(x0 + 14 + chipSize / 2, y + H / 2);
                Put(c, MakeChip(c, chipSize), chipAt, true, true);
                PutStar(c, chipAt);
                double textX = x0 + 14 + chipSize + 12;
                // 고를 거리 칸은 동그라미 안 숫자로 충분하다(같은 숫자를 두 번 쓰지 않는다).
                bool showCap = c.Number > 0 && c.Option == null;
                double capW = showCap ? 24 : 0;
                double textW = x0 + W - 14 - capW - 8 - textX;
                Put(c, MakeLabels(c, LabelOf(c), compact ? "" : HintOf(c), compact ? 14 : 15, textW, TextAlignment.Left), new Point(textX, y + H / 2), false, true);
                if (showCap) Put(c, MakeKeycap(c), new Point(x0 + W - 14 - capW / 2, y + H / 2), true, true);
                c.Bulge = new Vector(left ? -slide : slide, 0);
                c.HoverScale = 1.02;
                c.Spring = 0.7;
            }
        }

        // 부채: 커서 위로 반원. 둥근 부채살이 틈을 두고 놓이고, 올리면 바깥쪽으로 툭 튀어 오른다. 위가 좁으면 아래로 편다.
        void LayoutFan(List<MenuCell> items, string caption, string sub)
        {
            double r0 = 66, r1 = 192, cr = 50, gap = 7, m = 40, pop = 24;
            int n = items.Count;
            // 일곱 칸부터는 반원을 넘겨 둘러 펴서 칸 폭을 지킨다.
            double totalAngle = n <= 6 ? 180 : Math.Min(300, n * 34.0);
            if (n > 6) r1 = 208;
            double labelR = n > 6 ? 124 : 115;
            double reach = r1 + pop + 16;
            bool down = cursorDip.Y - (reach + m) < work.Top;
            double under = Math.Max(cr, totalAngle > 180 ? reach * Math.Sin((totalAngle - 180) / 2 * Math.PI / 180) : 0);
            double width = 2 * (reach + m);
            double height = reach + m + under + m;
            canvas.Width = width;
            canvas.Height = height;
            origin = down ? new Point(width / 2, m + under) : new Point(width / 2, reach + m);
            double span = totalAngle / n;
            double firstAngle = down ? 90 + totalAngle / 2 : 270 - totalAngle / 2;
            for (int i = 0; i < n; i++)
            {
                var c = items[i];
                double a0 = down ? firstAngle - i * span : firstAngle + i * span;
                double a1 = down ? a0 - span : a0 + span;
                double mid = (a0 + a1) / 2;
                AddCell(c, RoundedSector(origin, r0, r1, a0, a1, 12, gap));
                var labels = MakeLabels(c, Fit(LabelOf(c), 14.5, span >= 45 ? 76 : 52), SubOf(c), 14.5, 84, TextAlignment.Center);
                Point chipAt;
                if (span >= 45)
                {
                    // 넓은 부채살(고를 거리 2~3개): 그림을 위, 글을 아래로 쌓아 부채살 한가운데에 둔다.
                    labels.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double stack = 34 + 8 + labels.DesiredSize.Height;
                    Point mid0 = Polar(origin, (r0 + r1) / 2 + 4, mid);
                    chipAt = new Point(mid0.X, mid0.Y - stack / 2 + 17);
                    Put(c, labels, new Point(mid0.X, mid0.Y + stack / 2 - labels.DesiredSize.Height / 2), true, true);
                }
                else
                {
                    // 좁은 부채살: 그림은 바깥쪽, 글은 안쪽에 반지름 방향으로 놓는다.
                    chipAt = Polar(origin, r1 - 34, mid);
                    Put(c, labels, Polar(origin, labelR, mid), true, true);
                }
                Put(c, MakeChip(c, 34), chipAt, true, true);
                PutStar(c, chipAt);
                // 숫자 단추는 글과 반대쪽 귀퉁이에(위로 펴면 오른쪽 위, 아래로 펴면 오른쪽 아래)
                if (c.Option == null && c.Number > 0) Put(c, MakeBadge(c), chipAt + new Vector(span < 45 && Math.Cos(mid * Math.PI / 180) < -0.2 ? -13 : 13, down && span < 45 ? 13 : -13), true, true);
                c.Bulge = Unit(mid) * pop;
                c.HoverScale = 1.09;
                c.Spring = 1.0;
            }
            AddCenter(new EllipseGeometry(origin, cr, cr), caption, sub, cr * 2 - 16, 13.5);
        }

        // 물결: 알약 모양 칸이 무지개 물결선을 따라 늘어선다. 올리면 칸이 위로 톡 솟는다.
        void LayoutWave(List<MenuCell> items, string caption, string sub)
        {
            double bw = 86, bh = 116, step = 98, amp = 22, rise = 134, cr = 32, m = 40, pop = 10;
            int n = items.Count;
            if (n >= 8)
            {
                bw = 78;
                step = 88;
            }
            if (n > 1) step = Math.Min(step, Math.Max(bw + 4, (work.Width - 2 * m - bw - bw * 0.16) / (n - 1)));
            double grow = bh * 0.07 + pop + 4;
            double width = Math.Max((n - 1) * step + bw + 2 * m + bw * 0.16, 2 * (cr + m));
            double height = m + grow + bh / 2 + amp + rise + cr + m;
            bool down = cursorDip.Y - (rise + amp + bh / 2 + grow + m) < work.Top;
            canvas.Width = width;
            canvas.Height = height;
            origin = down ? new Point(width / 2, m + cr) : new Point(width / 2, height - m - cr);
            double rowY = down ? origin.Y + rise : origin.Y - rise;

            // 칸을 꿰는 물결선(장식). 칸 색을 차례로 이어 무지개가 된다.
            var wave = new StreamGeometry();
            double k0 = -(n - 1) / 2.0 - 0.7, k1 = (n - 1) / 2.0 + 0.7;
            using (var ctx = wave.Open())
            {
                bool first = true;
                for (double k = k0; k <= k1 + 1e-9; k += 0.05)
                {
                    var p = new Point(width / 2 + k * step, rowY + amp * Math.Sin(k * 1.05));
                    if (first)
                    {
                        ctx.BeginFigure(p, false, false);
                        first = false;
                    }
                    else ctx.LineTo(p, true, true);
                }
            }
            wave.Freeze();
            Brush line;
            if (n <= 1) line = new SolidColorBrush(items.Count > 0 ? items[0].Hue.Vivid : Theme.Slate.Vivid);
            else
            {
                var grad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                for (int i = 0; i < n; i++)
                {
                    double k = i - (n - 1) / 2.0;
                    grad.GradientStops.Add(new GradientStop(items[i].Hue.Vivid, (k - k0) / (k1 - k0)));
                }
                line = grad;
            }
            canvas.Children.Add(new ShapePath
            {
                Data = wave,
                Stroke = line,
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.6,
                IsHitTestVisible = false
            });

            for (int i = 0; i < n; i++)
            {
                var c = items[i];
                double k = i - (n - 1) / 2.0;
                double x = width / 2 + k * step;
                double y = rowY + amp * Math.Sin(k * 1.05);
                AddCell(c, new RectangleGeometry(new Rect(x - bw / 2, y - bh / 2, bw, bh), bw / 2, bw / 2));
                Point chipAt = new Point(x, y - 27);
                Put(c, MakeChip(c, 32), chipAt, true, true);
                PutStar(c, chipAt);
                if (c.Option == null && c.Number > 0) Put(c, MakeBadge(c), chipAt + new Vector(12.5, -12.5), true, true);
                Put(c, MakeLabels(c, Fit(LabelOf(c), 14.5, bw - 14), SubOf(c), 14.5, bw - 8, TextAlignment.Center), new Point(x, y + 15), true, true);
                c.Bulge = new Vector(0, down ? pop : -pop);
                c.HoverScale = 1.14;
                c.Spring = 0.9;
            }
            AddCenter(new EllipseGeometry(origin, cr, cr), files.Count > 0 && !choosing ? files.Count + "개" : "닫기", "", cr * 2 - 8, 13.5);
        }

        // 벌집: 둥근 육각형 타일. 7칸이 넘으면 두 번째 둘레까지 놓는다.
        void LayoutHoney(List<MenuCell> items, string caption, string sub)
        {
            double R = 60, gap = 8, corner = 13, m = 40;
            double d = Math.Sqrt(3) * R + gap;
            var pos = HoneyPositions(d, items.Count);
            // 일곱 칸부터 놓이는 둘째 줄은 위쪽에 생긴다. 위가 좁으면 아래로 뒤집는다.
            double up = R + pos.Select(v => -v.Y).DefaultIfEmpty(0).Max();
            if (items.Count > 6 && cursorDip.Y - (up + m) < work.Top)
            {
                pos = pos.Select(v => new Vector(v.X, -v.Y)).ToList();
            }
            up = Math.Max(R, R + pos.Select(v => -v.Y).DefaultIfEmpty(0).Max());
            double below = Math.Max(R, R + pos.Select(v => v.Y).DefaultIfEmpty(0).Max());
            double side = Math.Max(R, R * 0.87 + pos.Select(v => Math.Abs(v.X)).DefaultIfEmpty(0).Max()) + m;
            canvas.Width = side * 2;
            canvas.Height = up + below + m * 2;
            origin = new Point(side, up + m);
            AddCenter(RoundedPolygon(HexPoints(origin, R), corner), caption, sub, 88, 14);
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                Point p = origin + pos[i];
                AddCell(c, RoundedPolygon(HexPoints(p, R), corner));
                Point chipAt = new Point(p.X, p.Y - 21);
                Put(c, MakeChip(c, 32), chipAt, true, true);
                PutStar(c, chipAt);
                if (c.Option == null && c.Number > 0) Put(c, MakeBadge(c), chipAt + new Vector(12.5, -12.5), true, true);
                Put(c, MakeLabels(c, Fit(LabelOf(c), 15, 90), SubOf(c), 15, 96, TextAlignment.Center), new Point(p.X, p.Y + 16), true, true);
                Vector v = pos[i];
                v.Normalize();
                c.Bulge = v * 8;
                c.HoverScale = 1.10;
                c.Spring = 0.9;
            }
        }

        static List<Vector> HoneyPositions(double d, int count)
        {
            var list = new List<Vector>();
            // 고를거리가 적으면 가운데를 둘러 고르게 놓는다(2개: 좌우, 3개: 바람개비, 4개: 위아래 두 쌍).
            double[] angles;
            switch (count)
            {
                case 1: angles = new double[] { 0 }; break;
                case 2: angles = new double[] { 180, 0 }; break;
                case 3: angles = new double[] { 240, 0, 120 }; break;
                case 4: angles = new double[] { 240, 300, 60, 120 }; break;
                default: angles = new double[] { 240, 300, 0, 60, 120, 180 }; break;
            }
            foreach (double a in angles)
            {
                double r = a * Math.PI / 180;
                list.Add(new Vector(d * Math.Cos(r), d * Math.Sin(r)));
            }
            if (count > 6)
            {
                int extra = count - 6;
                double rowY = -d * Math.Sqrt(3);
                // 일곱~아홉 칸은 맨 위에 좌우가 맞는 줄 하나로 놓는다.
                if (extra == 1) list.Add(new Vector(0, rowY));
                else if (extra == 2)
                {
                    list.Add(new Vector(-d / 2 - d / 2, rowY));
                    list.Add(new Vector(d / 2 + d / 2, rowY));
                }
                else if (extra == 3)
                {
                    list.Add(new Vector(-d, rowY));
                    list.Add(new Vector(0, rowY));
                    list.Add(new Vector(d, rowY));
                }
                else
                {
                    // 두 번째 둘레: 축 좌표 거리 2인 12자리, 왼쪽 위부터 시계 방향
                    var ring = new List<Vector>();
                    for (int q = -2; q <= 2; q++)
                    {
                        for (int r = -2; r <= 2; r++)
                        {
                            int s = -q - r;
                            if (Math.Max(Math.Abs(q), Math.Max(Math.Abs(r), Math.Abs(s))) != 2) continue;
                            ring.Add(new Vector(d * (q + r / 2.0), d * Math.Sqrt(3) / 2 * r));
                        }
                    }
                    ring.Sort(delegate (Vector x, Vector y)
                    {
                        double ax = (Math.Atan2(x.Y, x.X) * 180 / Math.PI - 240 + 720) % 360;
                        double ay = (Math.Atan2(y.Y, y.X) * 180 / Math.PI - 240 + 720) % 360;
                        return ax.CompareTo(ay);
                    });
                    list.AddRange(ring);
                }
            }
            return list.Take(count).ToList();
        }

        // ── 색과 움직임 ──

        static CellLook LookOf(MenuCell c, bool on)
        {
            var l = new CellLook();
            if (c.IsCenter)
            {
                l.Fill = on ? Theme.InkColor : White;
                l.Text = on ? White : Theme.InkColor;
                l.Sub = on ? Theme.Hex(0xC9CDD6) : Theme.Ink3Color;
                l.Shadow = Theme.InkColor;
                l.Blur = on ? 22 : 14;
                l.Depth = on ? 6 : 3;
                l.Opacity = on ? 0.26 : 0.14;
                return l;
            }
            if (!c.Enabled)
            {
                l.Fill = Theme.MutedFillColor;
                l.Text = Theme.Ink3Color;
                l.Sub = Theme.Hex(0xA3A7B1);
                l.Chip = Theme.Hex(0xDCD8D0);
                l.Glyph = Theme.Hex(0x8A8F9B);
                l.Badge = Theme.Hex(0xB9B5AD);
                l.BadgeText = White;
                l.Cap = Theme.Hex(0xE4E0D8);
                l.CapText = Theme.Ink3Color;
                l.Shadow = Theme.InkColor;
                l.Blur = 8;
                l.Depth = 2;
                l.Opacity = 0.06;
                return l;
            }
            var h = c.Hue;
            l.Fill = on ? h.Vivid : h.Tint;
            l.Text = Theme.InkColor;
            l.Sub = on ? Theme.WithAlpha(Theme.InkColor, 0xD8) : h.Deep;
            l.Chip = on ? White : h.Vivid;
            l.Glyph = on ? h.Deep : Theme.InkColor;
            l.Badge = Theme.InkColor;
            l.BadgeText = White;
            l.Cap = on ? White : Theme.WithAlpha(White, 0xC0);
            l.CapText = h.Deep;
            l.Shadow = on ? h.Deep : Theme.InkColor;
            l.Blur = on ? 26 : 12;
            l.Depth = on ? 9 : 3;
            l.Opacity = on ? 0.32 : 0.13;
            return l;
        }

        static void Tint(SolidColorBrush b, Color to, bool instant, Duration t)
        {
            if (b == null) return;
            if (instant)
            {
                b.BeginAnimation(SolidColorBrush.ColorProperty, null);
                b.Color = to;
            }
            else b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, t));
        }

        static void Tween(Animatable target, DependencyProperty p, double to, bool instant, Duration t, IEasingFunction ease)
        {
            if (instant)
            {
                target.BeginAnimation(p, null);
                target.SetValue(p, to);
            }
            else target.BeginAnimation(p, new DoubleAnimation(to, t) { EasingFunction = ease });
        }

        static void Paint(MenuCell c, bool on, bool instant)
        {
            var l = LookOf(c, on);
            var t = new Duration(TimeSpan.FromMilliseconds(140));
            Tint(c.Fill, l.Fill, instant, t);
            Tint(c.TextBrush, l.Text, instant, t);
            Tint(c.SubBrush, l.Sub, instant, t);
            Tint(c.ChipBrush, l.Chip, instant, t);
            Tint(c.GlyphBrush, l.Glyph, instant, t);
            Tint(c.BadgeBrush, l.Badge, instant, t);
            Tint(c.BadgeTextBrush, l.BadgeText, instant, t);
            Tint(c.CapBrush, l.Cap, instant, t);
            Tint(c.CapTextBrush, l.CapText, instant, t);
            var st = new Duration(TimeSpan.FromMilliseconds(on ? 240 : 180));
            if (instant)
            {
                c.Shade.BeginAnimation(DropShadowEffect.ColorProperty, null);
                c.Shade.Color = l.Shadow;
            }
            else c.Shade.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(l.Shadow, st));
            Tween(c.Shade, DropShadowEffect.BlurRadiusProperty, l.Blur, instant, st, null);
            Tween(c.Shade, DropShadowEffect.ShadowDepthProperty, l.Depth, instant, st, null);
            Tween(c.Shade, DropShadowEffect.OpacityProperty, l.Opacity, instant, st, null);
        }

        void RefreshEnabled()
        {
            foreach (var c in cells)
            {
                if (c.IsCenter || (c.Option != null && !c.IsFavorite)) c.Enabled = true;
                else if (c.Action == null) c.Enabled = true;
                else c.Enabled = c.Action.CanTake(files);
                c.Root.BeginAnimation(OpacityProperty, null);
                c.Root.Opacity = c.Enabled ? 1 : DisabledOpacity;
                if (c != hot) Paint(c, false, true);
            }
            if (hot != null && !hot.Enabled) SetHot(null);
        }

        void UpdateCenter()
        {
            if (center == null) return;
            if (shape == "wave") center.LabelText.Text = files.Count > 0 && !choosing ? files.Count + "개" : "닫기";
            else
            {
                center.LabelText.Text = CenterCaption();
                if (center.SubText != null) center.SubText.Text = CenterSub();
            }
        }

        void Place()
        {
            Width = canvas.Width;
            Height = canvas.Height;
            double left = cursorDip.X - origin.X, top = cursorDip.Y - origin.Y;
            left = Math.Max(work.Left, Math.Min(left, work.Right - Width));
            top = Math.Max(work.Top, Math.Min(top, work.Bottom - Height));
            Left = left;
            Top = top;
        }

        /// 칸이 차례로 톡톡 커지며 나타난다. 시작 전에는 숨겨 두어 깜빡이지 않게 한다.
        void AnimateIn()
        {
            if (waitingForFiles)
            {
                foreach (var c in cells) c.Root.Opacity = 0;
                return;
            }
            int i = 0;
            foreach (var c in cells)
            {
                double begin = c.IsCenter ? 0 : 30 + i * 18;
                double target = c.Enabled ? 1 : DisabledOpacity;
                var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(begin))));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(begin + 130))));
                c.Root.BeginAnimation(OpacityProperty, fade);
                var pop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.6, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(begin))));
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(begin + 230)), new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut }));
                c.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                c.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());
                if (!c.IsCenter) i++;
            }
        }

        /// 마우스 아래 칸. 올라가 있는 칸은 튀어나온 모양 그대로 보고, 나머지는 쉬던 자리로 본다(깜빡임 방지).
        MenuCell CellAt(Point p)
        {
            if (hot != null && PoppedContains(hot, p)) return hot;
            for (int i = cells.Count - 1; i >= 0; i--)
            {
                if (cells[i].Rest.FillContains(p)) return cells[i];
            }
            return null;
        }

        bool PoppedContains(MenuCell c, Point p)
        {
            try
            {
                var t = canvas.TransformToDescendant(c.Root);
                Point q;
                if (t == null || !t.TryTransform(p, out q)) return false;
                return c.Local.FillContains(q) || c.Rest.FillContains(p);
            }
            catch
            {
                return c.Rest.FillContains(p);
            }
        }

        void SetHot(MenuCell c)
        {
            if (c != null && !c.Enabled) c = null;
            if (c == hot) return;
            foreach (var x in cells) if (x != hot && x != c) Panel.SetZIndex(x.Root, 0);
            if (hot != null)
            {
                Panel.SetZIndex(hot.Root, 5);
                Animate(hot, false, false);
            }
            hot = c;
            if (hot != null)
            {
                Panel.SetZIndex(hot.Root, 10);
                Animate(hot, true, false);
            }
        }

        void SetHotInstant(MenuCell c)
        {
            hot = c;
            Panel.SetZIndex(c.Root, 10);
            Animate(c, true, true);
        }

        void Animate(MenuCell c, bool on, bool instant)
        {
            Paint(c, on, instant);
            double s = on ? c.HoverScale : 1.0;
            var t = new Duration(TimeSpan.FromMilliseconds(on ? 260 : 170));
            IEasingFunction ease = on
                ? (IEasingFunction)new BackEase { Amplitude = c.Spring, EasingMode = EasingMode.EaseOut }
                : new QuadraticEase { EasingMode = EasingMode.EaseOut };
            Tween(c.Scale, ScaleTransform.ScaleXProperty, s, instant, t, ease);
            Tween(c.Scale, ScaleTransform.ScaleYProperty, s, instant, t, ease);
            Tween(c.Move, TranslateTransform.XProperty, on ? c.Bulge.X : 0, instant, t, ease);
            Tween(c.Move, TranslateTransform.YProperty, on ? c.Bulge.Y : 0, instant, t, ease);
        }

        // ── 끌어 놓기 ──

        static List<string> FilesFrom(IDataObject data)
        {
            try
            {
                if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
                var raw = data.GetData(DataFormats.FileDrop) as string[];
                if (raw == null) return null;
                return Formats.Expand(raw);
            }
            catch
            {
                return null;
            }
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (!choosing)
            {
                var list = FilesFrom(e.Data);
                if (list != null)
                {
                    bool changed = waitingForFiles || !list.SequenceEqual(files, StringComparer.OrdinalIgnoreCase);
                    if (changed)
                    {
                        files = list;
                        waitingForFiles = false;
                        BuildMain();
                    }
                }
            }
            OnDragOver(sender, e);
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (choosing)
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            var c = CellAt(e.GetPosition(canvas));
            SetHot(c);
            bool ok = c != null && c.Enabled && !c.IsCenter && c.Action != null && !c.Action.IsSettings;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragLeave(object sender, DragEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight) SetHot(null);
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            dropped = true;
            var c = CellAt(e.GetPosition(canvas));
            var list = FilesFrom(e.Data);
            bool ok = c != null && c.Enabled && !c.IsCenter && c.Action != null && !c.Action.IsSettings && list != null && list.Count > 0;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (!ok)
            {
                Dispatcher.BeginInvoke(new Action(CloseNow), DispatcherPriority.Background);
                return;
            }
            files = list;
            // 탐색기가 끌기를 끝낼 수 있도록 고르기는 놓기가 끝난 뒤에 한다.
            Dispatcher.BeginInvoke(new Action(delegate { Choose(c); }), DispatcherPriority.Background);
        }

        // ── 누르기 ──

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (dragMode && !choosing) return;
            SetHot(CellAt(e.GetPosition(canvas)));
        }

        void OnClick(object sender, MouseButtonEventArgs e)
        {
            if (dragMode && !choosing) return;
            var c = CellAt(e.GetPosition(canvas));
            if (c == null)
            {
                CloseNow();
                return;
            }
            Choose(c);
        }

        /// 고를 거리 칸을 오른쪽 단추로 누르면 즐겨찾기에 넣거나 뺀다.
        void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            if (!choosing || chosen == null || currentOptions == null) return;
            var c = CellAt(e.GetPosition(canvas));
            if (c == null || c.IsCenter || c.Option == null || c.Option.HasChildren) return;
            string message = Favorites.Toggle(chosen, c.Option);
            ShowOptions(currentCaption, currentOptions);
            if (message != null) App.Notify("즐겨찾기", message, false);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseNow();
                return;
            }
            int num = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D9) num = e.Key - Key.D1 + 1;
            else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) num = e.Key - Key.NumPad1 + 1;
            if (num > 0)
            {
                var c = cells.FirstOrDefault(x => x.Number == num);
                if (c != null) Choose(c);
            }
        }

        void Choose(MenuCell c)
        {
            if (c == null || !c.Enabled || closing) return;
            if (c.IsCenter)
            {
                CloseNow();
                return;
            }
            if (c.IsFavorite)
            {
                chosen = c.Action;
                Run(c.Action, c.Option);
                return;
            }
            if (c.Option != null)
            {
                if (c.Option.HasChildren)
                {
                    ShowOptions(c.Option.Label, c.Option.Children);
                    return;
                }
                Run(chosen, c.Option);
                return;
            }
            var a = c.Action;
            if (a.IsSettings)
            {
                CloseNow();
                App.OpenSettings();
                return;
            }
            if (a.Id == "ai" && !Ai.Configured)
            {
                CloseNow();
                App.AskToSetUpAi();
                return;
            }
            if (a.Id == "record")
            {
                CloseNow();
                Favorites.CountUse(a);
                RecordUi.Open();
                return;
            }
            if (files.Count == 0)
            {
                var picked = PickFiles(a);
                if (picked == null || picked.Count == 0) return;
                files = picked;
                RefreshEnabled();
                UpdateCenter();
            }
            if (!a.CanTake(files) || a.Usable(files).Count == 0)
            {
                CloseNow();
                App.Notify(a.Label, "이 칸에서 바꿀 수 있는 파일이 아니에요. (" + a.Hint + ")", true);
                return;
            }
            var opts = a.OptionsFor(files);
            if (opts.Count == 1)
            {
                chosen = a;
                Run(a, opts[0]);
                return;
            }
            chosen = a;
            ShowOptions(a.Label, opts);
        }

        void Run(MenuAction a, ActionOption o)
        {
            // 놓은 파일을 모두 넘긴다. 이 칸이 못 받는 파일은 작업이 이름을 알려 주며 건너뛴다.
            var all = files.ToList();
            CloseNow();
            Favorites.CountUse(a);
            App.RunAction(a, o, all);
        }

        List<string> PickFiles(MenuAction a)
        {
            var exts = new List<string>();
            foreach (var k in a.Accepts)
            {
                switch (k)
                {
                    case Kind.Hwp: exts.AddRange(new[] { "*.hwp", "*.hwpx" }); break;
                    case Kind.Word: exts.AddRange(new[] { "*.doc", "*.docx", "*.rtf", "*.odt" }); break;
                    case Kind.Excel: exts.AddRange(new[] { "*.xls", "*.xlsx", "*.xlsm", "*.csv", "*.cell", "*.ods" }); break;
                    case Kind.Ppt: exts.AddRange(new[] { "*.ppt", "*.pptx", "*.show" }); break;
                    case Kind.Pdf: exts.Add("*.pdf"); break;
                    case Kind.Image: exts.AddRange(Formats.ImageExts.Select(x => "*" + x)); break;
                    case Kind.Text: exts.AddRange(new[] { "*.txt", "*.md", "*.markdown", "*.html", "*.htm", "*.srt", "*.vtt", "*.tsv", "*.epub" }); break;
                    case Kind.Archive: exts.AddRange(new[] { "*.zip", "*.tar", "*.gz", "*.tgz" }); break;
                    case Kind.Video: exts.AddRange(new[] { "*.mp4", "*.m4v", "*.mov", "*.mkv", "*.avi", "*.wmv", "*.webm", "*.mpg", "*.mpeg", "*.mts", "*.m2ts", "*.ts", "*.3gp" }); break;
                    case Kind.Audio: exts.AddRange(new[] { "*.mp3", "*.wav", "*.m4a", "*.aac", "*.flac", "*.ogg", "*.opus", "*.wma" }); break;
                }
            }
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = a.Label + " — 파일 고르기",
                Filter = Array.IndexOf(a.Accepts, Kind.Unknown) >= 0 ? "모든 파일|*.*" : a.Label + " (" + string.Join(", ", exts) + ")|" + string.Join(";", exts) + "|모든 파일|*.*"
            };
            dialogOpen = true;
            bool? ok = dlg.ShowDialog(this);
            dialogOpen = false;
            if (ok != true) return null;
            var list = dlg.FileNames.ToList();
            list.Sort(Util.NaturalCompare);
            return list;
        }

        void OnPoll(object sender, EventArgs e)
        {
            if (closing) return;
            if (waitingForFiles && (DateTime.Now - openedAt).TotalMilliseconds > 260)
            {
                // 파일을 알려 주지 않는 끌기(글자 끌기 등)도 메뉴는 보여 준다.
                waitingForFiles = false;
                RefreshEnabled();
                AnimateIn();
            }
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (dragMode && !choosing)
            {
                if (!down)
                {
                    if (releasedAt == DateTime.MinValue) releasedAt = DateTime.Now;
                    else if ((DateTime.Now - releasedAt).TotalMilliseconds > 420 && !dropped) CloseNow();
                }
                else releasedAt = DateTime.MinValue;
            }
            else if (!dialogOpen && down)
            {
                var px = CursorPx();
                double s = DpiScale();
                double x = px.X / s, y = px.Y / s;
                if (x < Left || y < Top || x > Left + ActualWidth || y > Top + ActualHeight) CloseNow();
            }
        }

        public void CloseNow()
        {
            if (closing) return;
            closing = true;
            poll.Stop();
            if (Current == this) Current = null;
            var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(110)));
            fade.Completed += delegate { Close(); };
            BeginAnimation(OpacityProperty, fade);
        }
    }
}
