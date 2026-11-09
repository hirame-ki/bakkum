// 전후 비교: 결과를 열어 보기 전에 원본과 나란히 본다. 휠로 같이 확대하고, 끌어서 같이 옮긴다.
// 디자인(DESIGN.md): 책상색 바탕 위 흰 칸 두 개, 꼬리표로 원본과 결과를 나눈다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    public sealed class ComparePair
    {
        public string Before, After;
    }

    public class CompareWindow : Window
    {
        const int LongSide = 2400;
        const int PdfDpi = 150;
        const double MostZoom = 12;

        sealed class Side
        {
            public FrameworkElement Root;
            public Grid Host;
            public Image Picture;
            public TextBlock Info, Message;
        }

        internal sealed class Shot
        {
            public BitmapSource Picture;
            public int Pages = 1;
            public string Problem;
        }

        readonly List<ComparePair> pairs;
        readonly bool demo;
        readonly ScaleTransform zoom = new ScaleTransform(1, 1);
        readonly TranslateTransform pan = new TranslateTransform();
        readonly TransformGroup moves = new TransformGroup();
        readonly Side left, right;
        readonly TextBlock fileText;
        TextBlock pairText, pageText;
        readonly StackPanel pairNav, pageNav;
        int pairIndex, page, pages = 1, ticket;
        bool dragging;
        Point dragFrom;

        /// 끝난 일에서 나란히 볼 만한 원본·결과 짝. 볼 수 없거나 비교할 뜻이 없는 일이면 null.
        public static List<ComparePair> PairsFor(Job j)
        {
            if (j == null || j.Cancelled || j.Problems.Count > 0 || j.Outputs.Count == 0) return null;
            bool useful = j.Action.Id == "shrink" || j.Action.Id == "photo"
                || (j.Action.Id == "pdftool" && (j.Option.Id == "watermark" || j.Option.Id == "number"));
            if (!useful) return null;
            var sources = j.Action.Usable(j.Files);
            var list = new List<ComparePair>();
            for (int i = 0; i < j.Outputs.Count; i++)
            {
                // 더 줄일 게 없어 결과를 안 만든 파일이 있으면 수가 달라진다. 그때는 결과 이름으로 원본을 찾는다.
                string source = sources.Count == j.Outputs.Count ? sources[i] : SourceOf(j.Outputs[i], sources);
                if (source != null && CanShow(source) && CanShow(j.Outputs[i])) list.Add(new ComparePair { Before = source, After = j.Outputs[i] });
            }
            return list.Count > 0 ? list : null;
        }

        /// 결과 이름이 원본 이름으로 시작하는 파일(가장 길게 맞는 것). 둘 이상이 똑같이 맞으면 모른다고 한다.
        static string SourceOf(string output, List<string> sources)
        {
            string name = Path.GetFileNameWithoutExtension(output);
            string best = null;
            int bestLength = -1;
            bool tie = false;
            foreach (var s in sources)
            {
                string b = Path.GetFileNameWithoutExtension(s);
                if (!name.StartsWith(b, StringComparison.OrdinalIgnoreCase)) continue;
                if (b.Length > bestLength)
                {
                    best = s;
                    bestLength = b.Length;
                    tie = false;
                }
                else if (b.Length == bestLength) tie = true;
            }
            return tie ? null : best;
        }

        static bool CanShow(string f)
        {
            Kind k = Formats.KindOf(f);
            return k == Kind.Image || k == Kind.Pdf;
        }

        public static void Open(List<ComparePair> pairs, Hue hue)
        {
            var w = new CompareWindow(pairs, hue, false);
            w.Show();
            w.Activate();
        }

        public static Window ForPreview(string before, string after, Hue hue)
        {
            return new CompareWindow(new List<ComparePair> { new ComparePair { Before = before, After = after } }, hue, true);
        }

        // 시험(/selftest-tools update)에서만 쓴다
        internal static CompareWindow ForTest(List<ComparePair> pairs, Hue hue)
        {
            return new CompareWindow(pairs, hue, false);
        }

        internal bool PicturesReady
        {
            get { return left.Picture.Source != null && right.Picture.Source != null; }
        }

        internal bool MovesTogether
        {
            get { return left.Picture.RenderTransform == right.Picture.RenderTransform; }
        }

        internal double CurrentZoom
        {
            get { return zoom.ScaleX; }
        }

        internal Point CurrentPan
        {
            get { return new Point(pan.X, pan.Y); }
        }

        internal Grid LeftHost
        {
            get { return left.Host; }
        }

        CompareWindow(List<ComparePair> pairs, Hue hue, bool demo)
        {
            this.pairs = pairs;
            this.demo = demo;
            moves.Children.Add(zoom);
            moves.Children.Add(pan);
            Title = "전후 비교 · " + AppInfo.Name;
            Width = 980;
            Height = 700;
            MinWidth = 640;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Polish(this);
            try { Icon = App.WindowIcon; } catch { }

            var root = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };

            var head = new DockPanel { Margin = new Thickness(2, 0, 2, 16) };
            pairNav = Nav(delegate { GoPair(-1); }, delegate { GoPair(1); }, "앞 파일", "다음 파일", ref pairText);
            DockPanel.SetDock(pairNav, Dock.Right);
            head.Children.Add(pairNav);
            var chip = Theme.GlyphChip("compare", hue, 44);
            chip.Margin = new Thickness(0, 0, 14, 0);
            DockPanel.SetDock(chip, Dock.Left);
            head.Children.Add(chip);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text("전후 비교", 19, FontWeights.Bold, Theme.Ink));
            fileText = Theme.MetaLabel("");
            fileText.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(fileText);
            head.Children.Add(words);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var foot = new DockPanel { Margin = new Thickness(2, 14, 2, 0) };
            var close = Theme.MakeButton("닫기", "Primary");
            close.IsCancel = true;
            close.Click += delegate { Close(); };
            DockPanel.SetDock(close, Dock.Right);
            foot.Children.Add(close);
            pageNav = Nav(delegate { GoPage(-1); }, delegate { GoPage(1); }, "앞쪽", "다음 쪽", ref pageText);
            pageNav.Margin = new Thickness(0, 0, 14, 0);
            DockPanel.SetDock(pageNav, Dock.Right);
            foot.Children.Add(pageNav);
            var tip = Theme.MetaLabel("휠로 확대 · 끌어서 이동 · 두 번 누르면 처음 크기");
            tip.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(tip);
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            left = MakeSide("원본", Theme.Slate);
            right = MakeSide("결과", hue);
            grid.Children.Add(left.Root);
            Grid.SetColumn(right.Root, 2);
            grid.Children.Add(right.Root);
            root.Children.Add(grid);
            Content = root;

            ShowPair();
        }

        static StackPanel Nav(Action back, Action next, string backTip, string nextTip, ref TextBlock label)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var t = Theme.Text("", 13.5, FontWeights.SemiBold, Theme.Ink2);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(8, 0, 8, 0);
            row.Children.Add(Arrow("left", backTip, back));
            row.Children.Add(t);
            row.Children.Add(Arrow("right", nextTip, next));
            label = t;
            return row;
        }

        static Button Arrow(string glyph, string tip, Action click)
        {
            var b = new Button { Style = Theme.GetStyle("Quiet"), Width = 36, Height = 36, Padding = new Thickness(0), ToolTip = tip, Content = Theme.Glyph(glyph, 16, Theme.Ink2, 2) };
            b.Click += delegate { click(); };
            return b;
        }

        Side MakeSide(string caption, Hue hue)
        {
            var side = new Side();
            side.Picture = new Image { Stretch = Stretch.Uniform, RenderTransform = moves };
            RenderOptions.SetBitmapScalingMode(side.Picture, BitmapScalingMode.HighQuality);
            side.Message = Theme.MetaLabel("");
            side.Message.HorizontalAlignment = HorizontalAlignment.Center;
            side.Message.VerticalAlignment = VerticalAlignment.Center;
            side.Host = new Grid { Background = Theme.Sheet };
            side.Host.Children.Add(side.Picture);
            side.Host.Children.Add(side.Message);
            var host = side.Host;
            host.SizeChanged += delegate { host.Clip = new RectangleGeometry(new Rect(0, 0, host.ActualWidth, host.ActualHeight), 16, 16); };
            host.MouseWheel += OnWheel;
            host.MouseLeftButtonDown += OnDown;
            host.MouseMove += OnMove;
            host.MouseLeftButtonUp += OnUp;

            var top = new DockPanel { Margin = new Thickness(2, 0, 2, 8) };
            var tag = Theme.Tag(caption, hue);
            tag.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(tag, Dock.Left);
            top.Children.Add(tag);
            side.Info = Theme.MetaLabel("");
            side.Info.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(side.Info);
            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);
            dock.Children.Add(host);
            side.Root = dock;
            return side;
        }

        // ── 확대·이동 ──

        void Reset()
        {
            zoom.ScaleX = 1;
            zoom.ScaleY = 1;
            pan.X = 0;
            pan.Y = 0;
        }

        void Limit()
        {
            double w = Math.Max(left.Picture.ActualWidth, right.Picture.ActualWidth);
            double h = Math.Max(left.Picture.ActualHeight, right.Picture.ActualHeight);
            pan.X = Math.Min(0, Math.Max(w - w * zoom.ScaleX, pan.X));
            pan.Y = Math.Min(0, Math.Max(h - h * zoom.ScaleY, pan.Y));
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            var side = sender == left.Host ? left : right;
            if (side.Picture.Source == null) return;
            // 커서 아래의 그림 점이 그 자리에 머물게 옮긴다
            Point q = e.GetPosition(side.Picture);
            double old = zoom.ScaleX;
            double now = Math.Max(1, Math.Min(MostZoom, old * (e.Delta > 0 ? 1.25 : 0.8)));
            pan.X += q.X * (old - now);
            pan.Y += q.Y * (old - now);
            zoom.ScaleX = now;
            zoom.ScaleY = now;
            Limit();
            e.Handled = true;
        }

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                Reset();
                return;
            }
            dragging = true;
            dragFrom = e.GetPosition(this);
            ((UIElement)sender).CaptureMouse();
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            Point p = e.GetPosition(this);
            pan.X += p.X - dragFrom.X;
            pan.Y += p.Y - dragFrom.Y;
            dragFrom = p;
            Limit();
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            dragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }

        // ── 파일 넘기기·불러오기 ──

        void GoPair(int d)
        {
            pairIndex = (pairIndex + d + pairs.Count) % pairs.Count;
            page = 0;
            ShowPair();
        }

        void GoPage(int d)
        {
            if (pages <= 1) return;
            page = (page + d + pages) % pages;
            Load();
        }

        static long Length(string f)
        {
            try { return new FileInfo(f).Length; }
            catch { return 0; }
        }

        static string Change(long a, long b)
        {
            if (a <= 0 || b <= 0 || a == b) return "";
            double p = Math.Abs(1 - (double)b / a) * 100;
            if (p < 1) return "";
            return " · " + Math.Round(p) + (b < a ? "% 줄었어요" : "% 늘었어요");
        }

        void ShowPair()
        {
            var pair = pairs[pairIndex];
            fileText.Text = Theme.Keep(Path.GetFileName(pair.Before));
            pairText.Text = (pairIndex + 1) + " / " + pairs.Count;
            pairNav.Visibility = pairs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            long a = Length(pair.Before), b = Length(pair.After);
            left.Info.Text = Conversions.Size(a);
            right.Info.Text = Conversions.Size(b) + Change(a, b);
            Load();
        }

        void Load()
        {
            var pair = pairs[pairIndex];
            int at = page;
            int mine = ++ticket;
            Reset();
            foreach (var s in new[] { left, right })
            {
                s.Picture.Source = null;
                s.Message.Text = "불러오는 중";
            }
            if (demo)
            {
                Apply(Read(pair.Before, at), Read(pair.After, at));
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                var x = Read(pair.Before, at);
                var y = Read(pair.After, at);
                Dispatcher.BeginInvoke(new Action(delegate { if (mine == ticket) Apply(x, y); }));
            });
        }

        internal static Shot Read(string path, int at)
        {
            var shot = new Shot();
            try
            {
                if (Formats.KindOf(path) == Kind.Pdf)
                {
                    using (var d = new WinPdfDoc(path))
                    {
                        shot.Pages = d.PageCount;
                        shot.Picture = Imaging.Decoder(d.RenderPng(Math.Min(at, d.PageCount - 1), PdfDpi, LongSide)).Frames[0];
                    }
                }
                else
                {
                    shot.Picture = Imaging.Fit(Imaging.Load(path), LongSide);
                }
                if (shot.Picture.CanFreeze) shot.Picture.Freeze();
            }
            catch (Exception ex)
            {
                Log.Write("비교 불러오기 실패: " + ex.GetType().Name);
                shot.Picture = null;
                shot.Problem = "미리 볼 수 없어요";
            }
            return shot;
        }

        void Apply(Shot a, Shot b)
        {
            Put(left, a);
            Put(right, b);
            pages = Math.Min(a.Pages, b.Pages);
            pageText.Text = (page + 1) + " / " + pages + "쪽";
            pageNav.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        static void Put(Side s, Shot shot)
        {
            s.Picture.Source = shot.Picture;
            s.Message.Text = shot.Problem ?? "";
        }
    }
}
