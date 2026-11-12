// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 쪽 편집 창. 쪽을 끌어 순서를 바꾸고, 골라서 돌리거나 복제·삭제한다.
// 화면에 보이는 칸만 만들고 작은 그림도 보이는 쪽만 그려서, 쪽이 많아도 메모리를 적게 쓴다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RectShape = System.Windows.Shapes.Rectangle;

namespace Bakkum
{
    public class PdfPagesWindow : Window
    {
        static readonly Hue H = Theme.Pink;
        const double CellW = 150, CellH = 212, ThumbW = 128, ThumbH = 170, Pad = 10;
        const int MaxPages = 5000;
        const int KeepThumbs = 320;

        sealed class PageItem
        {
            public string Path;
            public int Index;
            public int Rotate;
            public bool Selected;
            public PageItem Copy()
            {
                return new PageItem { Path = Path, Index = Index, Rotate = Rotate, Selected = Selected };
            }
            public string Key
            {
                get { return Path + "|" + Index; }
            }
        }

        sealed class Cell
        {
            public Border Box;
            public Image Img;
            public Border Tag;
            public TextBlock Number;
        }

        readonly MenuAction action;
        readonly List<string> files;
        readonly bool preview;
        List<PageItem> pages = new List<PageItem>();
        readonly List<List<PageItem>> undo = new List<List<PageItem>>();
        bool changed;
        bool saved;

        // 화면
        readonly ScrollViewer scroll;
        readonly Canvas canvas;
        readonly RectShape marker;
        readonly TextBlock status;
        readonly TextBox rangeBox;
        readonly TextBlock emptyText;
        readonly Dictionary<int, Cell> shown = new Dictionary<int, Cell>();
        readonly Stack<Cell> pool = new Stack<Cell>();

        // 작은 그림
        readonly object gate = new object();
        readonly Dictionary<string, BitmapSource> thumbs = new Dictionary<string, BitmapSource>();
        readonly List<PageItem> wanted = new List<PageItem>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool stop;

        // 마우스
        int pressIndex = -1;
        Point pressPoint;
        bool pressedSelected;
        bool dragging;
        int anchor = -1;
        int insertAt = -1;
        readonly DispatcherTimer autoScroll;
        double scrollSpeed;

        public PdfPagesWindow(MenuAction action, List<string> files, bool preview)
        {
            this.action = action;
            this.files = files;
            this.preview = preview;
            Title = "쪽 편집 · " + AppInfo.Name;
            var wa = SystemParameters.WorkArea;
            // 작은 화면에서도 창이 화면 밖으로 나가지 않게 가장 작은 크기를 작업 영역에 맞춘다
            MinWidth = Math.Min(760, wa.Width);
            MinHeight = Math.Min(560, wa.Height);
            Width = Math.Max(MinWidth, Math.Min(1120, wa.Width * 0.92));
            Height = Math.Max(MinHeight, Math.Min(780, wa.Height * 0.92));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Polish(this);
            try { Icon = App.WindowIcon; } catch { }

            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 14) };
            var head = Theme.DialogHead("쪽 편집", "끌어서 순서를 바꿔요. 쪽을 누르면 골라지고, Ctrl이나 Shift를 함께 누르면 여러 쪽을 고를 수 있어요.", H, "pdftool");
            head.Margin = new Thickness(2, 0, 2, 12);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            rangeBox = new TextBox { Style = Theme.GetStyle("TextField"), Width = 150, Height = 42, Tag = "예: 1,3-5" };
            rangeBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    SelectRange();
                    e.Handled = true;
                }
            };
            right.Children.Add(rangeBox);
            var pick = Theme.MakeButton("이 쪽들 고르기", "Line");
            pick.Margin = new Thickness(6, 0, 0, 0);
            pick.Click += delegate { SelectRange(); };
            right.Children.Add(pick);
            var all = Theme.MakeButton("모두 고르기", "Line");
            all.Margin = new Thickness(6, 0, 0, 0);
            all.Click += delegate { SelectAll(); };
            right.Children.Add(all);
            DockPanel.SetDock(right, Dock.Right);
            tools.Children.Add(right);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(ToolButton("rotate", "왼쪽으로", delegate { Rotate(-90); }, true));
            left.Children.Add(ToolButton("rotate", "오른쪽으로", delegate { Rotate(90); }, false));
            left.Children.Add(ToolButton("copy", "복제", Duplicate, false));
            left.Children.Add(ToolButton("trash", "삭제", DeleteSelected, false));
            left.Children.Add(ToolButton("undo", "되돌리기", Undo, false));
            tools.Children.Add(left);
            DockPanel.SetDock(tools, Dock.Top);
            root.Children.Add(tools);

            var foot = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var close = Theme.MakeButton("닫기", "Line");
            close.Click += delegate { Close(); };
            buttons.Children.Add(close);
            var selected = Theme.MakeButton("고른 쪽만 저장", "Line");
            selected.Margin = new Thickness(8, 0, 0, 0);
            selected.Click += delegate { Save(true, "one"); };
            buttons.Children.Add(selected);
            var split = Theme.MakeButton("쪽마다 나누기", "Line");
            split.Margin = new Thickness(8, 0, 0, 0);
            split.Click += delegate { Save(false, "split"); };
            buttons.Children.Add(split);
            var save = Theme.MakeButton("저장", "Primary");
            save.Margin = new Thickness(8, 0, 0, 0);
            save.Click += delegate { Save(false, "one"); };
            buttons.Children.Add(save);
            DockPanel.SetDock(buttons, Dock.Right);
            foot.Children.Add(buttons);
            status = Theme.Text("", 13.5, FontWeights.Normal, Theme.Ink2);
            status.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(status);
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);

            canvas = new Canvas { Background = Brushes.Transparent };
            marker = new RectShape { Width = 5, Height = CellH - 22, RadiusX = 2.5, RadiusY = 2.5, Fill = H.DeepBrush, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            Panel.SetZIndex(marker, 10);
            canvas.Children.Add(marker);
            canvas.MouseLeftButtonDown += Down;
            canvas.MouseMove += Move;
            canvas.MouseLeftButtonUp += Up;
            canvas.LostMouseCapture += delegate
            {
                dragging = false;
                pressIndex = -1;
                marker.Visibility = Visibility.Collapsed;
                autoScroll.Stop();
            };
            scroll = new ScrollViewer { Content = canvas, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            scroll.ScrollChanged += delegate { UpdateVisible(); };
            scroll.SizeChanged += delegate { Relayout(); };
            emptyText = Theme.Text("쪽을 읽는 중이에요", 15, FontWeights.Bold, Theme.Ink2);
            emptyText.HorizontalAlignment = HorizontalAlignment.Center;
            emptyText.VerticalAlignment = VerticalAlignment.Center;
            emptyText.TextAlignment = TextAlignment.Center;
            emptyText.Margin = new Thickness(30);
            emptyText.IsHitTestVisible = false;
            var stage = new Grid();
            stage.Children.Add(scroll);
            stage.Children.Add(emptyText);
            root.Children.Add(new Border { Background = Theme.Frozen(Theme.Hex(0xE9E4DB)), CornerRadius = new CornerRadius(20), Child = stage, ClipToBounds = true });
            Content = root;

            autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            autoScroll.Tick += delegate { scroll.ScrollToVerticalOffset(scroll.VerticalOffset + scrollSpeed); };
            PreviewKeyDown += OnKey;
            Closing += OnClosing;
            Closed += delegate
            {
                stop = true;
                wake.Set();
            };
            Loaded += delegate { Begin(); };
        }

        static FrameworkElement ToolButton(string glyph, string text, Action click, bool flip)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var g = Theme.Glyph(glyph, 18, Theme.Ink2, 2);
            g.VerticalAlignment = VerticalAlignment.Center;
            if (flip)
            {
                g.RenderTransformOrigin = new Point(0.5, 0.5);
                g.RenderTransform = new ScaleTransform(-1, 1);
            }
            row.Children.Add(g);
            var t = Theme.Text(text, 14, FontWeights.Bold, Theme.Ink);
            t.Margin = new Thickness(6, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(t);
            var b = new Button { Style = Theme.GetStyle("Quiet"), Content = row, Height = 42, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 4, 0) };
            b.Click += delegate { click(); };
            return b;
        }

        // ── 읽기 ──

        void Begin()
        {
            if (preview)
            {
                foreach (var f in files)
                {
                    using (var d = new WinPdfDoc(f))
                    {
                        for (int i = 0; i < d.PageCount; i++)
                        {
                            pages.Add(new PageItem { Path = f, Index = i });
                            thumbs[f + "|" + i] = Bgra.From(Imaging.Decoder(d.RenderPng(i, 40, 250)).Frames[0]).ToBitmap();
                        }
                    }
                }
                emptyText.Text = "";
                pages[0].Selected = true;
                if (pages.Count > 2)
                {
                    pages[2].Selected = true;
                    pages[2].Rotate = 90;
                }
                Relayout();
                return;
            }
            var list = files.ToList();
            var t = new Thread(delegate ()
            {
                var found = new List<PageItem>();
                string error = null;
                foreach (var f in list)
                {
                    try
                    {
                        using (var d = new WinPdfDoc(f))
                        {
                            for (int i = 0; i < d.PageCount; i++) found.Add(new PageItem { Path = f, Index = i });
                        }
                    }
                    catch (UserFacingException ex)
                    {
                        error = Util.ShortName(f) + ": " + ex.Message;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("쪽 편집 열기 실패: " + ex.GetType().Name);
                        error = Util.ShortName(f) + ": 이 PDF를 열지 못했어요.";
                    }
                    if (error != null) break;
                }
                Dispatcher.BeginInvoke(new Action(delegate { Opened(found, error); }));
                ThumbLoop(list);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void Opened(List<PageItem> found, string error)
        {
            if (error != null)
            {
                emptyText.Text = Theme.Keep(error);
                return;
            }
            if (found.Count > MaxPages)
            {
                emptyText.Text = Theme.Keep("한 번에 " + MaxPages + "쪽까지 편집할 수 있어요. 지금은 " + found.Count + "쪽이에요.");
                return;
            }
            pages = found;
            emptyText.Text = "";
            Relayout();
        }

        void ThumbLoop(List<string> paths)
        {
            var docs = new Dictionary<string, WinPdfDoc>(StringComparer.OrdinalIgnoreCase);
            try
            {
                while (!stop)
                {
                    PageItem next = null;
                    lock (gate)
                    {
                        if (wanted.Count > 0)
                        {
                            next = wanted[0];
                            wanted.RemoveAt(0);
                        }
                    }
                    if (next == null)
                    {
                        wake.WaitOne(600);
                        continue;
                    }
                    try
                    {
                        WinPdfDoc doc;
                        if (!docs.TryGetValue(next.Path, out doc))
                        {
                            doc = new WinPdfDoc(next.Path);
                            docs[next.Path] = doc;
                        }
                        var bmp = Bgra.From(Imaging.Decoder(doc.RenderPng(next.Index, 40, 250)).Frames[0]).ToBitmap();
                        string key = next.Key;
                        Dispatcher.BeginInvoke(new Action(delegate { ThumbReady(key, bmp); }));
                    }
                    catch (Exception ex)
                    {
                        Log.Write("쪽 작은 그림 실패: " + ex.GetType().Name);
                    }
                }
            }
            finally
            {
                foreach (var d in docs.Values) d.Dispose();
            }
        }

        void ThumbReady(string key, BitmapSource bmp)
        {
            thumbs[key] = bmp;
            foreach (var kv in shown)
            {
                if (kv.Key < pages.Count && pages[kv.Key].Key == key) Bind(kv.Value, kv.Key);
            }
            if (thumbs.Count > KeepThumbs)
            {
                var visible = new HashSet<string>(shown.Keys.Where(i => i < pages.Count).Select(i => pages[i].Key));
                foreach (var k in thumbs.Keys.Where(k => !visible.Contains(k)).Take(thumbs.Count - KeepThumbs / 2).ToList()) thumbs.Remove(k);
            }
        }

        // ── 칸 배치 ──

        int Columns
        {
            get { return Math.Max(1, (int)Math.Floor((scroll.ViewportWidth > 0 ? scroll.ViewportWidth - 2 * Pad : ActualWidth - 80) / CellW)); }
        }

        double SideGap
        {
            get { return Math.Max(Pad, (scroll.ViewportWidth - Columns * CellW) / 2); }
        }

        void Relayout()
        {
            int rows = (pages.Count + Columns - 1) / Columns;
            canvas.Height = rows * CellH + 2 * Pad;
            UpdateVisible(true);
            UpdateStatus();
        }

        void UpdateVisible()
        {
            UpdateVisible(false);
        }

        void UpdateVisible(bool rebind)
        {
            if (pages.Count == 0)
            {
                foreach (var kv in shown.ToList()) Recycle(kv.Key);
                return;
            }
            int cols = Columns;
            int firstRow = Math.Max(0, (int)Math.Floor((scroll.VerticalOffset - Pad) / CellH));
            int lastRow = (int)Math.Ceiling((scroll.VerticalOffset + Math.Max(600, scroll.ViewportHeight) - Pad) / CellH);
            int first = firstRow * cols, last = Math.Min(pages.Count - 1, (lastRow + 1) * cols - 1);
            foreach (var i in shown.Keys.Where(k => k < first || k > last).ToList()) Recycle(i);
            for (int i = first; i <= last; i++)
            {
                Cell c;
                if (!shown.TryGetValue(i, out c))
                {
                    c = pool.Count > 0 ? pool.Pop() : MakeCell();
                    c.Box.Visibility = Visibility.Visible;
                    shown[i] = c;
                    Bind(c, i);
                }
                else if (rebind) Bind(c, i);
                Canvas.SetLeft(c.Box, SideGap + (i % cols) * CellW + 6);
                Canvas.SetTop(c.Box, Pad + (i / cols) * CellH + 6);
            }
            if (!preview)
            {
                lock (gate)
                {
                    wanted.Clear();
                    for (int i = first; i <= last; i++)
                    {
                        if (!thumbs.ContainsKey(pages[i].Key)) wanted.Add(pages[i]);
                    }
                }
                wake.Set();
            }
        }

        void Recycle(int i)
        {
            var c = shown[i];
            shown.Remove(i);
            c.Img.Source = null;
            c.Box.Visibility = Visibility.Collapsed;
            pool.Push(c);
        }

        Cell MakeCell()
        {
            var img = new Image { Stretch = Stretch.Uniform, Width = ThumbW, Height = ThumbH, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var number = Theme.Text("", 12.5, FontWeights.Bold, Theme.Ink);
            var tag = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 1), Child = number, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
            var g = new Grid { IsHitTestVisible = false };
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ThumbH + 4) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(img);
            Grid.SetRow(tag, 1);
            g.Children.Add(tag);
            var box = new Border { Width = CellW - 12, Height = CellH - 12, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(3), Padding = new Thickness(3), Child = g, IsHitTestVisible = false };
            canvas.Children.Add(box);
            return new Cell { Box = box, Img = img, Tag = tag, Number = number };
        }

        void Bind(Cell c, int i)
        {
            var p = pages[i];
            BitmapSource bmp;
            thumbs.TryGetValue(p.Key, out bmp);
            c.Img.Source = bmp;
            bool sideways = p.Rotate % 180 != 0;
            c.Img.Width = sideways ? ThumbH : ThumbW;
            c.Img.Height = sideways ? ThumbW : ThumbH;
            c.Img.LayoutTransform = p.Rotate == 0 ? Transform.Identity : new RotateTransform(p.Rotate);
            c.Number.Text = (i + 1).ToString();
            c.Box.Background = p.Selected ? H.TintBrush : Theme.Sheet;
            c.Box.BorderBrush = p.Selected ? H.VividBrush : Brushes.Transparent;
            c.Tag.Background = p.Selected ? H.VividBrush : Theme.Sheet2;
        }

        void Rebind()
        {
            foreach (var kv in shown)
            {
                if (kv.Key < pages.Count) Bind(kv.Value, kv.Key);
            }
            UpdateStatus();
        }

        void UpdateStatus()
        {
            int sel = pages.Count(p => p.Selected);
            status.Text = pages.Count + "쪽" + (sel > 0 ? " · " + sel + "쪽 고름" : "");
        }

        // ── 마우스 ──

        int IndexAt(Point p)
        {
            int cols = Columns;
            double x = p.X - SideGap, y = p.Y - Pad;
            if (x < 0 || y < 0) return -1;
            int col = (int)(x / CellW), row = (int)(y / CellH);
            if (col >= cols) return -1;
            int i = row * cols + col;
            return i < pages.Count ? i : -1;
        }

        int InsertIndexAt(Point p)
        {
            int cols = Columns;
            double x = p.X - SideGap, y = p.Y - Pad;
            int row = Math.Max(0, (int)(y / CellH));
            int col = Math.Max(0, Math.Min(cols, (int)Math.Round(x / CellW)));
            return Math.Max(0, Math.Min(pages.Count, row * cols + col));
        }

        void Down(object s, MouseButtonEventArgs e)
        {
            canvas.Focus();
            var p = e.GetPosition(canvas);
            int i = IndexAt(p);
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (i < 0)
            {
                if (!ctrl && !shift) ClearSelection();
                return;
            }
            pressIndex = i;
            pressPoint = p;
            pressedSelected = pages[i].Selected;
            if (shift && anchor >= 0)
            {
                int a = Math.Min(anchor, i), b = Math.Max(anchor, i);
                if (!ctrl) foreach (var x in pages) x.Selected = false;
                for (int k = a; k <= b; k++) pages[k].Selected = true;
            }
            else if (ctrl)
            {
                pages[i].Selected = !pages[i].Selected;
                anchor = i;
            }
            else
            {
                if (!pages[i].Selected)
                {
                    foreach (var x in pages) x.Selected = false;
                    pages[i].Selected = true;
                }
                anchor = i;
            }
            canvas.CaptureMouse();
            Rebind();
        }

        void Move(object s, MouseEventArgs e)
        {
            if (pressIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(canvas);
            if (!dragging)
            {
                if ((p - pressPoint).Length < 7) return;
                if (!pages[pressIndex].Selected) return;
                dragging = true;
            }
            insertAt = InsertIndexAt(p);
            int cols = Columns;
            int row = insertAt / cols, col = insertAt % cols;
            if (insertAt == pages.Count && col == 0 && pages.Count > 0)
            {
                row = (insertAt - 1) / cols;
                col = cols;
            }
            Canvas.SetLeft(marker, SideGap + col * CellW + 2);
            Canvas.SetTop(marker, Pad + row * CellH + 12);
            marker.Visibility = Visibility.Visible;
            var view = e.GetPosition(scroll);
            scrollSpeed = view.Y < 50 ? -18 : view.Y > scroll.ViewportHeight - 50 ? 18 : 0;
            if (scrollSpeed != 0 && !autoScroll.IsEnabled) autoScroll.Start();
            if (scrollSpeed == 0) autoScroll.Stop();
        }

        void Up(object s, MouseButtonEventArgs e)
        {
            int pressed = pressIndex;
            bool wasDrag = dragging;
            int target = insertAt;
            bool plain = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0;
            canvas.ReleaseMouseCapture();
            if (wasDrag) MoveSelected(target);
            else if (pressed >= 0 && plain && pressedSelected)
            {
                foreach (var x in pages) x.Selected = false;
                pages[pressed].Selected = true;
                Rebind();
            }
        }

        // ── 고치기 ──

        void Remember()
        {
            undo.Add(pages.Select(p => p.Copy()).ToList());
            if (undo.Count > 40) undo.RemoveAt(0);
            changed = true;
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            pages = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            Relayout();
        }

        bool NeedSelection()
        {
            if (pages.Any(p => p.Selected)) return true;
            status.Text = Theme.Keep("먼저 쪽을 눌러 골라 주세요.");
            return false;
        }

        void Rotate(int by)
        {
            if (!NeedSelection()) return;
            Remember();
            foreach (var p in pages.Where(x => x.Selected)) p.Rotate = ((p.Rotate + by) % 360 + 360) % 360;
            Rebind();
        }

        void Duplicate()
        {
            if (!NeedSelection()) return;
            Remember();
            var result = new List<PageItem>();
            foreach (var p in pages)
            {
                result.Add(p);
                if (p.Selected)
                {
                    var c = p.Copy();
                    c.Selected = false;
                    result.Add(c);
                }
            }
            pages = result;
            Relayout();
        }

        void DeleteSelected()
        {
            if (!NeedSelection()) return;
            if (pages.All(p => p.Selected))
            {
                status.Text = Theme.Keep("모든 쪽을 지울 수는 없어요.");
                return;
            }
            Remember();
            pages = pages.Where(p => !p.Selected).ToList();
            anchor = -1;
            Relayout();
        }

        void MoveSelected(int target)
        {
            if (target < 0) return;
            var moving = pages.Where(p => p.Selected).ToList();
            if (moving.Count == 0) return;
            int before = pages.Take(target).Count(p => p.Selected);
            var rest = pages.Where(p => !p.Selected).ToList();
            int at = Math.Max(0, Math.Min(rest.Count, target - before));
            var next = new List<PageItem>(rest);
            next.InsertRange(at, moving);
            if (next.SequenceEqual(pages)) return;
            Remember();
            pages = next;
            Relayout();
        }

        void ClearSelection()
        {
            foreach (var x in pages) x.Selected = false;
            Rebind();
        }

        void SelectAll()
        {
            foreach (var x in pages) x.Selected = true;
            Rebind();
        }

        void SelectRange()
        {
            string error;
            var wantedPages = PdfTools.ParseRanges(rangeBox.Text, pages.Count, out error);
            if (wantedPages == null)
            {
                status.Text = Theme.Keep(error);
                return;
            }
            foreach (var x in pages) x.Selected = false;
            foreach (int i in wantedPages) pages[i].Selected = true;
            Rebind();
        }

        void OnKey(object s, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key == Key.Delete)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.A)
            {
                SelectAll();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.Z)
            {
                Undo();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.S)
            {
                Save(false, "one");
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        // ── 저장 ──

        void Save(bool onlySelected, string mode)
        {
            var list = onlySelected ? pages.Where(p => p.Selected).ToList() : pages;
            if (list.Count == 0)
            {
                status.Text = Theme.Keep(onlySelected ? "먼저 저장할 쪽을 골라 주세요." : "저장할 쪽이 없어요.");
                return;
            }
            var edit = new PdfPagesEdit { Mode = mode };
            foreach (var p in list) edit.Items.Add(new PdfPagesEdit.Entry { Path = p.Path, Index = p.Index, Rotate = p.Rotate });
            // 순서를 그대로 두고 돌리기만 했다면 책갈피·양식을 살리는 길로 저장한다.
            bool sameOrder = files.Count == 1 && !onlySelected && mode == "one" && list.Select((p, i) => p.Index == i).All(x => x);
            edit.RotateOnly = sameOrder;
            var job = new Job(action, new ActionOption("pagesave", "쪽 편집 저장", ""), list.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList()) { Payload = edit };
            saved = true;
            Close();
            Jobs.Start(job);
        }

        void OnClosing(object s, System.ComponentModel.CancelEventArgs e)
        {
            if (saved || preview || !changed) return;
            int n = Dialog.Ask(this, H, "pdftool", "저장하지 않고 닫을까요?", "바꾼 내용은 사라져요. 원본 PDF는 그대로예요.", null, "저장 안 하고 닫기", "계속 고치기");
            if (n != 0) e.Cancel = true;
        }
    }
}
