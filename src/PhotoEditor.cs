// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 사진 손질 창. 왼쪽에서 도구를 고르고, 가운데 사진 위에서 드래그하고, 오른쪽에서 값을 맞춘다.
// 화면에서는 줄인 사진으로 바로바로 보여 주고, 저장할 때 원본 크기로 똑같이 다시 계산한다.
// 어떤 사진도 이 PC 밖으로 나가지 않는다. 원본은 건드리지 않고 '이름_손질' 파일을 새로 만든다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using LineShape = System.Windows.Shapes.Line;
using Polygon = System.Windows.Shapes.Polygon;
using RectShape = System.Windows.Shapes.Rectangle;
using Shape = System.Windows.Shapes.Shape;
using ShapePath = System.Windows.Shapes.Path;

namespace Bakkum
{
    public class PhotoSaveItem
    {
        public string Path;
        public PhotoEdit Edit;
    }

    public class PhotoSave
    {
        /// same · png · jpg · webp
        public string Format = "same";
        public List<PhotoSaveItem> Items = new List<PhotoSaveItem>();
    }

    public class PhotoEditor : Window
    {
        static readonly Hue H = Theme.Orange;
        static readonly uint[] Colors = { 0x22252F, 0xFFFFFF, 0xE5483B, 0xFF8A1F, 0xFFC928, 0x27B37D, 0x2F7BF0, 0x8B5CF6, 0xEC4899 };
        static readonly Rect Full = new Rect(0, 0, 1, 1);

        static readonly string[][] Tools =
        {
            new[] { "redact", "가리기", "blur" },
            new[] { "crop", "자르기", "crop" },
            new[] { "rotate", "돌리기", "rotate" },
            new[] { "size", "크기", "resize" },
            new[] { "adjust", "보정", "adjust" },
            new[] { "mark", "글자·표시", "letter" },
            new[] { "frame", "꾸미기", "frame" },
            new[] { "scan", "문서 펴기", "scan" },
            new[] { "info", "정보", "info" }
        };

        sealed class Item
        {
            public string Path;
            public PhotoEdit Edit = new PhotoEdit();
            public readonly List<PhotoEdit> Undo = new List<PhotoEdit>();
            public readonly List<PhotoEdit> Redo = new List<PhotoEdit>();
            public bool Changed;
            public BitmapSource Proxy;
            public int FullW, FullH;
            public string Error;
            public Point[] Quad;
            public Image Thumb;
            public Border ThumbBox;
        }

        enum Drag { None, Create, Move, Handle, CropNew, CropMove, CropHandle, QuadHandle }

        readonly MenuAction action;
        readonly List<Item> items;
        int index = -1;
        string tool;
        readonly bool autoFace;
        bool preview;
        bool building;

        // 새로 그릴 때 쓰는 값
        readonly PhotoMark redactProto = new PhotoMark { Kind = MarkKind.Redact, Mode = "blur", Strength = 0.6, Rgb = 0x22252F };
        string markKind = "text";
        string markText = "글자";
        bool markBold = true;
        readonly Dictionary<string, double> markSize = new Dictionary<string, double> { { "text", 0.06 }, { "arrow", 0.012 }, { "box", 0.01 }, { "marker", 0.05 } };
        readonly Dictionary<string, uint> markColor = new Dictionary<string, uint> { { "text", 0xE5483B }, { "arrow", 0xE5483B }, { "box", 0xE5483B }, { "marker", 0xFFC928 } };

        Rect cropRect = Full;
        double cropAspect;
        string saveFormat = "same";
        bool compare;
        bool faceBusy;
        bool saved;

        PhotoMark selected;
        BitmapSource geo;
        string geoKey;
        PhotoEdit shown;
        Rect content;
        string lastKey;
        DateTime lastAt;

        Drag drag;
        int dragHandle;
        Point dragStart;
        PhotoMark dragOrig;
        PhotoEdit dragBefore;
        Rect dragRect;
        bool dragChanged;

        // 화면
        readonly DispatcherTimer renderTimer;
        Grid stage;
        Canvas overlay;
        RectShape checker;
        Image picture;
        Grid comparePane;
        Image beforeImg, afterImg;
        TextBlock stageMessage;
        StackPanel panel;
        TextBlock fileText, statusText;
        Button undoBtn, redoBtn, compareBtn, applyAllBtn;
        StackPanel strip;
        readonly Dictionary<string, RadioButton> toolTabs = new Dictionary<string, RadioButton>();

        public PhotoEditor(MenuAction action, List<string> files, string start)
        {
            this.action = action;
            items = files.Select(f => new Item { Path = f }).ToList();
            tool = start == "scan" ? "scan" : "redact";
            autoFace = start == "face";
            Title = "사진 손질 · " + AppInfo.Name;
            var wa = SystemParameters.WorkArea;
            // 작은 화면(작업 영역이 620보다 낮음)에서도 창이 화면 밖으로 나가지 않게
            MinWidth = Math.Min(880, wa.Width);
            MinHeight = Math.Min(620, wa.Height);
            Width = Math.Max(MinWidth, Math.Min(1140, wa.Width * 0.94));
            Height = Math.Max(MinHeight, Math.Min(780, wa.Height * 0.94));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Polish(this);
            try { Icon = App.WindowIcon; } catch { }
            Content = BuildRoot();
            renderTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(36) };
            renderTimer.Tick += delegate
            {
                renderTimer.Stop();
                Render();
            };
            PreviewKeyDown += OnKey;
            Closing += OnClosing;
            Loaded += delegate
            {
                LoadThumbs();
                Go(0);
            };
        }

        /// 보기용(그림 확인): 사진을 바로 읽어 두고, 도구와 예시 표시를 넣은 창
        public static PhotoEditor ForPreview(MenuAction a, List<string> files, string start, string useTool)
        {
            var w = new PhotoEditor(a, files, start);
            w.preview = true;
            if (!string.IsNullOrEmpty(useTool)) w.tool = useTool;
            return w;
        }

        Item Current
        {
            get { return index >= 0 && index < items.Count ? items[index] : null; }
        }

        // ── 틀 만들기 ──

        FrameworkElement BuildRoot()
        {
            var root = new Grid { Margin = new Thickness(14, 12, 14, 14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = BuildHead();
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(304) });
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var tools = BuildTools();
            Grid.SetColumn(tools, 0);
            body.Children.Add(tools);

            var center = BuildStage();
            center.Margin = new Thickness(12, 0, 12, 0);
            Grid.SetColumn(center, 1);
            body.Children.Add(center);

            panel = new StackPanel();
            var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var side = Theme.Card(scroll, 18);
            Grid.SetColumn(side, 2);
            body.Children.Add(side);

            var foot = BuildFoot();
            Grid.SetRow(foot, 2);
            root.Children.Add(foot);
            return root;
        }

        FrameworkElement BuildHead()
        {
            var dock = new DockPanel { Margin = new Thickness(2, 0, 2, 12) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            undoBtn = IconButton("undo", "되돌리기 (Ctrl+Z)", DoUndo);
            redoBtn = IconButton("redo", "다시 하기 (Ctrl+Y)", DoRedo);
            compareBtn = IconButton("compare", "원본과 나란히 보기", ToggleCompare);
            right.Children.Add(undoBtn);
            right.Children.Add(redoBtn);
            right.Children.Add(compareBtn);
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);
            var chip = Theme.GlyphChip("photo", H, 40);
            chip.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(chip, Dock.Left);
            dock.Children.Add(chip);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text("사진 손질", 18, FontWeights.Bold, Theme.Ink));
            fileText = Theme.MetaLabel("");
            fileText.TextWrapping = TextWrapping.NoWrap;
            fileText.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(fileText);
            dock.Children.Add(words);
            return dock;
        }

        static Button IconButton(string glyph, string tip, Action click)
        {
            var b = new Button
            {
                Style = Theme.GetStyle("Quiet"),
                Width = 42,
                Height = 42,
                Padding = new Thickness(0),
                ToolTip = tip,
                Content = Theme.Glyph(glyph, 20, Theme.Ink2, 2),
                Margin = new Thickness(4, 0, 0, 0)
            };
            b.Click += delegate { click(); };
            return b;
        }

        FrameworkElement BuildTools()
        {
            var stack = new StackPanel();
            foreach (var t in Tools)
            {
                string id = t[0];
                var words = new TextBlock
                {
                    Text = Theme.Keep(t[1]),
                    FontSize = 12.5,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0)
                };
                var icon = Theme.Glyph(t[2], 21, Theme.Ink2, 1.9);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                var bind = new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) };
                BindingOperations.SetBinding(icon, Shape.StrokeProperty, bind);
                var content2 = new StackPanel();
                content2.Children.Add(icon);
                content2.Children.Add(words);
                var r = new RadioButton
                {
                    Style = Theme.GetStyle("ToolTab"),
                    Tag = H,
                    GroupName = "tool",
                    Content = content2,
                    Margin = new Thickness(0, 0, 0, 3),
                    IsChecked = id == tool
                };
                r.Checked += delegate
                {
                    if (!building) UseTool(id);
                };
                toolTabs[id] = r;
                stack.Children.Add(r);
            }
            var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            return Theme.Card(scroll, 7);
        }

        FrameworkElement BuildStage()
        {
            var outer = new Border
            {
                Background = Theme.Frozen(Theme.Hex(0xE9E4DB)),
                CornerRadius = new CornerRadius(20)
            };
            var g = new Grid();
            stage = new Grid { Background = Brushes.Transparent, ClipToBounds = true, Focusable = true, FocusVisualStyle = null, Cursor = Cursors.Cross };
            overlay = new Canvas { IsHitTestVisible = false };
            checker = new RectShape { Fill = CheckerBrush(), Visibility = Visibility.Collapsed };
            picture = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            overlay.Children.Add(checker);
            overlay.Children.Add(picture);
            stage.Children.Add(overlay);
            stage.MouseLeftButtonDown += StageDown;
            stage.MouseMove += StageMove;
            stage.MouseLeftButtonUp += StageUp;
            stage.LostMouseCapture += delegate { if (drag != Drag.None) EndDrag(); };
            stage.SizeChanged += delegate { LayoutPicture(); };
            g.Children.Add(stage);

            comparePane = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(14) };
            comparePane.ColumnDefinitions.Add(new ColumnDefinition());
            comparePane.ColumnDefinitions.Add(new ColumnDefinition());
            beforeImg = new Image();
            afterImg = new Image();
            RenderOptions.SetBitmapScalingMode(beforeImg, BitmapScalingMode.HighQuality);
            RenderOptions.SetBitmapScalingMode(afterImg, BitmapScalingMode.HighQuality);
            var left = CompareSide("원본", beforeImg);
            var right = CompareSide("손질 후", afterImg);
            left.Margin = new Thickness(0, 0, 7, 0);
            right.Margin = new Thickness(7, 0, 0, 0);
            Grid.SetColumn(right, 1);
            comparePane.Children.Add(left);
            comparePane.Children.Add(right);
            g.Children.Add(comparePane);

            stageMessage = Theme.Text("", 15, FontWeights.Bold, Theme.Ink2);
            stageMessage.HorizontalAlignment = HorizontalAlignment.Center;
            stageMessage.VerticalAlignment = VerticalAlignment.Center;
            stageMessage.TextAlignment = TextAlignment.Center;
            stageMessage.Margin = new Thickness(30);
            stageMessage.IsHitTestVisible = false;
            g.Children.Add(stageMessage);

            outer.Child = g;
            return outer;
        }

        static FrameworkElement CompareSide(string caption, Image img)
        {
            var dock = new DockPanel();
            var tag = Theme.Tag(caption, H);
            tag.HorizontalAlignment = HorizontalAlignment.Center;
            tag.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(tag, Dock.Top);
            dock.Children.Add(tag);
            dock.Children.Add(img);
            return dock;
        }

        static Brush CheckerBrush()
        {
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            var dark = new GeometryGroup();
            dark.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
            dark.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
            g.Children.Add(new GeometryDrawing(Theme.Frozen(Theme.Hex(0xE3DED5)), null, dark));
            var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
            b.Freeze();
            return b;
        }

        FrameworkElement BuildFoot()
        {
            var dock = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(Theme.MetaLabel("저장 형식"));
            ((TextBlock)right.Children[0]).VerticalAlignment = VerticalAlignment.Center;
            ((TextBlock)right.Children[0]).Margin = new Thickness(0, 0, 8, 0);
            var ids = new[] { "same", "png", "jpg", "webp" };
            var labels = new[] { "그대로", "PNG", "JPG", "WEBP" };
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                var r = new RadioButton
                {
                    Style = Theme.GetStyle("Chip"),
                    Tag = H,
                    GroupName = "format",
                    Content = labels[i],
                    MinHeight = 38,
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 5, 0),
                    IsChecked = id == saveFormat
                };
                if (id == "webp") r.ToolTip = "손실 없이 저장해요. 사진은 JPG보다 커질 수 있어요.";
                r.Checked += delegate { saveFormat = id; };
                right.Children.Add(r);
            }
            var close = Theme.MakeButton("닫기", "Line");
            close.Margin = new Thickness(10, 0, 0, 0);
            close.Click += delegate { Close(); };
            right.Children.Add(close);
            if (items.Count > 1)
            {
                applyAllBtn = Theme.MakeButton("다른 사진에도 똑같이", "Line");
                applyAllBtn.Margin = new Thickness(8, 0, 0, 0);
                applyAllBtn.Click += delegate { ApplyToOthers(); };
                right.Children.Add(applyAllBtn);
            }
            var save = Theme.MakeButton(items.Count > 1 ? "모두 저장" : "저장", "Primary");
            save.Margin = new Thickness(8, 0, 0, 0);
            save.Click += delegate { Save(); };
            right.Children.Add(save);
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            statusText = Theme.Text("", 13.5, FontWeights.Normal, Theme.Ink2);
            statusText.Margin = new Thickness(2, 0, 10, 0);
            statusText.VerticalAlignment = VerticalAlignment.Center;
            if (items.Count > 1)
            {
                strip = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var it in items)
                {
                    var item = it;
                    item.Thumb = new Image { Stretch = Stretch.UniformToFill };
                    item.ThumbBox = new Border
                    {
                        Width = 52,
                        Height = 42,
                        Margin = new Thickness(0, 0, 6, 0),
                        CornerRadius = new CornerRadius(9),
                        BorderThickness = new Thickness(2),
                        BorderBrush = Theme.Line2,
                        Background = Theme.Sheet,
                        Cursor = Cursors.Hand,
                        Child = item.Thumb,
                        ToolTip = Path.GetFileName(item.Path)
                    };
                    item.ThumbBox.MouseLeftButtonUp += delegate { Go(items.IndexOf(item)); };
                    strip.Children.Add(item.ThumbBox);
                }
                var hs = new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxWidth = 340, Height = 44 };
                left.Children.Add(hs);
                statusText.Margin = new Thickness(2, 4, 10, 0);
            }
            left.Children.Add(statusText);
            dock.Children.Add(left);
            return dock;
        }

        void LoadThumbs()
        {
            if (items.Count < 2 || preview) return;
            var todo = items.Select(i => new KeyValuePair<Item, string>(i, i.Path)).ToList();
            var t = new Thread(delegate ()
            {
                foreach (var pair in todo)
                {
                    try
                    {
                        var small = Bgra.From(Imaging.Fit(Imaging.Load(pair.Value), 120)).ToBitmap();
                        var item = pair.Key;
                        Dispatcher.BeginInvoke(new Action(delegate { item.Thumb.Source = small; }));
                    }
                    catch { }
                }
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        // ── 사진 옮겨 다니기 ──

        void Go(int i)
        {
            if (i < 0 || i >= items.Count) return;
            CommitCrop();
            index = i;
            selected = null;
            cropRect = Full;
            geoKey = null;
            var it = items[i];
            fileText.Text = Path.GetFileName(it.Path) + (items.Count > 1 ? "  ·  " + (i + 1) + "/" + items.Count : "");
            foreach (var x in items)
            {
                if (x.ThumbBox != null) x.ThumbBox.BorderBrush = x == it ? H.VividBrush : Theme.Line2;
            }
            Ensure(it, delegate
            {
                if (Current != it) return;
                EnterTool();
                Render();
                if (autoFace && it.Edit.Marks.Count == 0 && tool == "redact") FindFaces();
            });
        }

        void Ensure(Item it, Action then)
        {
            if (it.Proxy != null || it.Error != null)
            {
                then();
                return;
            }
            if (preview)
            {
                it.Proxy = PhotoRender.Proxy(it.Path, out it.FullW, out it.FullH);
                then();
                return;
            }
            picture.Source = null;
            stageMessage.Text = Theme.Keep("사진을 여는 중이에요");
            string path = it.Path;
            var t = new Thread(delegate ()
            {
                BitmapSource p = null;
                int w = 0, h = 0;
                string err = null;
                try
                {
                    p = PhotoRender.Proxy(path, out w, out h);
                }
                catch (UserFacingException ex)
                {
                    err = ex.Message;
                }
                catch (Exception ex)
                {
                    Log.Write("사진 열기 실패: " + ex.GetType().Name);
                    err = "이 사진을 열지 못했어요.";
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    it.Proxy = p;
                    it.FullW = w;
                    it.FullH = h;
                    it.Error = err;
                    TrimCache();
                    then();
                }));
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        /// 앞뒤 두 장만 메모리에 둔다(학교 PC는 메모리가 빠듯하다).
        void TrimCache()
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (Math.Abs(i - index) > 2) items[i].Proxy = null;
            }
        }

        // ── 도구 ──

        void UseTool(string id)
        {
            if (id == tool) return;
            CommitCrop();
            tool = id;
            selected = null;
            EnterTool();
            Render();
        }

        void EnterTool()
        {
            var it = Current;
            if (it == null) return;
            building = true;
            RadioButton tab;
            if (toolTabs.TryGetValue(tool, out tab)) tab.IsChecked = true;
            building = false;
            if (tool == "crop") cropRect = Full;
            if (tool == "scan" && it.Quad == null && it.Proxy != null)
            {
                it.Quad = PaperFinder.Find(it.Proxy) ?? new[] { new Point(0.06, 0.06), new Point(0.94, 0.06), new Point(0.94, 0.94), new Point(0.06, 0.94) };
            }
            geoKey = null;
            RefreshPanel();
            UpdateButtons();
            stage.Cursor = tool == "redact" || tool == "mark" || tool == "crop" ? Cursors.Cross : Cursors.Arrow;
        }

        void PushUndo(string key)
        {
            var it = Current;
            if (it == null) return;
            if (key != null && key == lastKey && (DateTime.Now - lastAt).TotalMilliseconds < 1200)
            {
                lastAt = DateTime.Now;
                it.Changed = true;
                return;
            }
            lastKey = key;
            lastAt = DateTime.Now;
            AddUndo(it, it.Edit.Copy());
        }

        void AddUndo(Item it, PhotoEdit before)
        {
            it.Undo.Add(before);
            if (it.Undo.Count > 40) it.Undo.RemoveAt(0);
            it.Redo.Clear();
            it.Changed = true;
            UpdateButtons();
        }

        void DoUndo()
        {
            var it = Current;
            if (it == null || it.Undo.Count == 0) return;
            cropRect = Full;
            it.Redo.Add(it.Edit);
            it.Edit = it.Undo[it.Undo.Count - 1];
            it.Undo.RemoveAt(it.Undo.Count - 1);
            AfterHistory();
        }

        void DoRedo()
        {
            var it = Current;
            if (it == null || it.Redo.Count == 0) return;
            cropRect = Full;
            it.Undo.Add(it.Edit);
            it.Edit = it.Redo[it.Redo.Count - 1];
            it.Redo.RemoveAt(it.Redo.Count - 1);
            AfterHistory();
        }

        void AfterHistory()
        {
            selected = null;
            lastKey = null;
            geoKey = null;
            RefreshPanel();
            UpdateButtons();
            Render();
        }

        void UpdateButtons()
        {
            var it = Current;
            undoBtn.IsEnabled = it != null && it.Undo.Count > 0;
            redoBtn.IsEnabled = it != null && it.Redo.Count > 0;
            compareBtn.Background = compare ? H.VividBrush : Brushes.Transparent;
        }

        void ToggleCompare()
        {
            compare = !compare;
            comparePane.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
            stage.Visibility = compare ? Visibility.Hidden : Visibility.Visible;
            UpdateButtons();
            Render();
        }

        void SetStatus(string s)
        {
            statusText.Text = Theme.Keep(s ?? "");
        }

        void RequestRender()
        {
            if (!renderTimer.IsEnabled) renderTimer.Start();
        }

        void Later(Action a)
        {
            Dispatcher.BeginInvoke(a, DispatcherPriority.Background);
        }

        // ── 그리기 ──

        PhotoEdit ViewEdit(Item it)
        {
            var e = it.Edit;
            if (tool == "crop")
            {
                var v = e.Copy();
                v.Margin = 0;
                v.Corner = 0;
                v.CanvasRatio = 0;
                return v;
            }
            if (tool == "scan" && e.Quad == null)
            {
                return new PhotoEdit { Filter = e.Filter };
            }
            return e;
        }

        void Render()
        {
            var it = Current;
            if (it == null) return;
            if (it.Proxy == null)
            {
                picture.Source = null;
                checker.Visibility = Visibility.Collapsed;
                stageMessage.Text = Theme.Keep(it.Error ?? "사진을 여는 중이에요");
                DrawOverlay();
                return;
            }
            stageMessage.Text = "";
            PhotoEdit view = ViewEdit(it);
            string key = it.Path + "|" + view.GeometryKey();
            try
            {
                if (key != geoKey)
                {
                    geo = PhotoRender.Geometry(it.Proxy, view);
                    geoKey = key;
                }
                var bmp = PhotoRender.Finish(geo, view, true);
                picture.Source = bmp;
                shown = view;
                if (compare)
                {
                    beforeImg.Source = it.Proxy;
                    afterImg.Source = bmp;
                }
            }
            catch (Exception ex)
            {
                Log.Write("사진 그리기 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                stageMessage.Text = Theme.Keep("이 손질을 보여 주지 못했어요. 되돌려 주세요.");
            }
            LayoutPicture();
            UpdateSizeLine();
        }

        void LayoutPicture()
        {
            var src = picture.Source as BitmapSource;
            if (src == null || geo == null || shown == null) return;
            double W = stage.ActualWidth - 36, H2 = stage.ActualHeight - 36;
            if (W <= 0 || H2 <= 0) return;
            double pw = src.PixelWidth, ph = src.PixelHeight;
            double s = Math.Min(3, Math.Min(W / pw, H2 / ph));
            double dw = Math.Round(pw * s), dh = Math.Round(ph * s);
            double x = Math.Round((stage.ActualWidth - dw) / 2), y = Math.Round((stage.ActualHeight - dh) / 2);
            picture.Width = dw;
            picture.Height = dh;
            Canvas.SetLeft(picture, x);
            Canvas.SetTop(picture, y);
            checker.Width = dw;
            checker.Height = dh;
            Canvas.SetLeft(checker, x);
            Canvas.SetTop(checker, y);
            checker.Visibility = Visibility.Visible;
            Rect box = shown.HasFrame ? PhotoRender.ContentBox(geo.PixelWidth, geo.PixelHeight, shown) : Full;
            content = new Rect(x + box.X * dw, y + box.Y * dh, box.Width * dw, box.Height * dh);
            DrawOverlay();
        }

        Point Norm(Point p)
        {
            return new Point((p.X - content.X) / content.Width, (p.Y - content.Y) / content.Height);
        }

        Point Scr(Point n)
        {
            return new Point(content.X + n.X * content.Width, content.Y + n.Y * content.Height);
        }

        static Point Clamp01(Point p)
        {
            return new Point(Math.Max(0, Math.Min(1, p.X)), Math.Max(0, Math.Min(1, p.Y)));
        }

        static Point[] RectHandles(Rect r)
        {
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            return new[]
            {
                new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right, r.Top), new Point(r.Right, cy),
                new Point(r.Right, r.Bottom), new Point(cx, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, cy)
            };
        }

        Point[] HandlesOf(PhotoMark m)
        {
            if (m == null || m.Whole) return new Point[0];
            switch (m.Kind)
            {
                case MarkKind.Arrow: return new[] { new Point(m.X, m.Y), new Point(m.X + m.W, m.Y + m.H) };
                case MarkKind.Redact:
                case MarkKind.Box:
                    return RectHandles(m.Bounds);
            }
            return new Point[0];
        }

        void DrawOverlay()
        {
            while (overlay.Children.Count > 2) overlay.Children.RemoveAt(overlay.Children.Count - 1);
            if (compare || Current == null || Current.Proxy == null || picture.Source == null) return;
            switch (tool)
            {
                case "redact":
                    foreach (var m in Current.Edit.Marks)
                    {
                        if (m.Kind == MarkKind.Redact && !m.Whole) Outline(m.Bounds, m.Oval, m == selected);
                    }
                    DrawHandles();
                    break;
                case "mark":
                    if (selected != null)
                    {
                        if (selected.Kind != MarkKind.Arrow) Outline(selected.Kind == MarkKind.Highlight ? PointsBox(selected) : selected.Bounds, false, true);
                        DrawHandles();
                    }
                    break;
                case "crop":
                    DrawCrop();
                    break;
                case "scan":
                    if (Current.Edit.Quad == null) DrawQuad();
                    break;
            }
        }

        static Rect PointsBox(PhotoMark m)
        {
            if (m.Points == null || m.Points.Count == 0) return m.Bounds;
            double x0 = m.Points.Min(p => p.X), y0 = m.Points.Min(p => p.Y), x1 = m.Points.Max(p => p.X), y1 = m.Points.Max(p => p.Y);
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }

        Rect ScrRect(Rect n)
        {
            return new Rect(Scr(n.TopLeft), Scr(n.BottomRight));
        }

        void Outline(Rect n, bool oval, bool strong)
        {
            var r = ScrRect(n);
            overlay.Children.Add(MakeOutline(r, oval, Brushes.White, strong ? 3 : 2, null, strong ? 1 : 0.75));
            overlay.Children.Add(MakeOutline(r, oval, Theme.Ink, strong ? 1.6 : 1.2, new DoubleCollection { 4, 3 }, 1));
        }

        static Shape MakeOutline(Rect r, bool oval, Brush stroke, double thick, DoubleCollection dash, double opacity)
        {
            Shape s = oval ? (Shape)new Ellipse() : new RectShape();
            s.Width = Math.Max(1, r.Width);
            s.Height = Math.Max(1, r.Height);
            s.Stroke = stroke;
            s.StrokeThickness = thick;
            s.Opacity = opacity;
            if (dash != null) s.StrokeDashArray = dash;
            Canvas.SetLeft(s, r.X);
            Canvas.SetTop(s, r.Y);
            return s;
        }

        void DrawHandles()
        {
            foreach (var n in HandlesOf(selected)) AddHandle(Scr(n));
        }

        void AddHandle(Point at)
        {
            var e = new Ellipse { Width = 13, Height = 13, Fill = Brushes.White, Stroke = Theme.Ink, StrokeThickness = 2 };
            Canvas.SetLeft(e, at.X - 6.5);
            Canvas.SetTop(e, at.Y - 6.5);
            overlay.Children.Add(e);
        }

        void DrawCrop()
        {
            var r = ScrRect(cropRect);
            var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
            dim.Children.Add(new RectangleGeometry(content));
            dim.Children.Add(new RectangleGeometry(r));
            overlay.Children.Add(new ShapePath { Data = dim, Fill = Theme.Frozen(Color.FromArgb(0x96, 0x22, 0x25, 0x2F)) });
            overlay.Children.Add(MakeOutline(r, false, Brushes.White, 2, null, 1));
            Brush thin = Theme.Frozen(Color.FromArgb(0x9A, 255, 255, 255));
            for (int i = 1; i <= 2; i++)
            {
                double x = r.X + r.Width * i / 3, y = r.Y + r.Height * i / 3;
                overlay.Children.Add(new LineShape { X1 = x, X2 = x, Y1 = r.Top, Y2 = r.Bottom, Stroke = thin, StrokeThickness = 1 });
                overlay.Children.Add(new LineShape { X1 = r.Left, X2 = r.Right, Y1 = y, Y2 = y, Stroke = thin, StrokeThickness = 1 });
            }
            var hs = RectHandles(cropRect);
            for (int i = 0; i < hs.Length; i++)
            {
                if (cropAspect > 0 && i % 2 == 1) continue;
                AddHandle(Scr(hs[i]));
            }
        }

        void DrawQuad()
        {
            var q = Current.Quad;
            if (q == null) return;
            var points = new PointCollection(q.Select(Scr));
            overlay.Children.Add(new Polygon { Points = points, Stroke = Brushes.White, StrokeThickness = 3, Fill = Theme.Frozen(Color.FromArgb(0x26, 0xFF, 0xAA, 0x4D)) });
            overlay.Children.Add(new Polygon { Points = points, Stroke = H.DeepBrush, StrokeThickness = 1.6 });
            foreach (var p in q) AddHandle(Scr(p));
        }

        // ── 마우스 ──

        int HitHandle(Point[] handles, Point p)
        {
            for (int i = 0; i < handles.Length; i++)
            {
                if ((Scr(handles[i]) - p).Length <= 11) return i;
            }
            return -1;
        }

        PhotoMark HitMark(Point n)
        {
            var marks = Current.Edit.Marks;
            double tol = 7 / Math.Max(1, Math.Min(content.Width, content.Height));
            bool wantRedact = tool == "redact";
            for (int i = marks.Count - 1; i >= 0; i--)
            {
                var m = marks[i];
                if (m.Whole || (m.Kind == MarkKind.Redact) != wantRedact) continue;
                if (m.Hit(n, tol)) return m;
            }
            return null;
        }

        void StageDown(object s, MouseButtonEventArgs e)
        {
            var it = Current;
            if (compare || it == null || it.Proxy == null || content.Width <= 0) return;
            Point p = e.GetPosition(overlay);
            stage.Focus();
            dragChanged = false;
            switch (tool)
            {
                case "redact":
                case "mark":
                    MarkDown(p);
                    break;
                case "crop":
                    CropDown(p);
                    break;
                case "scan":
                    if (it.Edit.Quad == null && it.Quad != null)
                    {
                        int h = HitHandle(it.Quad, p);
                        if (h >= 0)
                        {
                            drag = Drag.QuadHandle;
                            dragHandle = h;
                        }
                    }
                    break;
            }
            if (drag != Drag.None) stage.CaptureMouse();
        }

        void MarkDown(Point p)
        {
            var it = Current;
            var n = Norm(p);
            dragStart = n;
            dragBefore = it.Edit.Copy();
            var before = selected;
            if (selected != null)
            {
                int h = HitHandle(HandlesOf(selected), p);
                if (h >= 0)
                {
                    drag = Drag.Handle;
                    dragHandle = h;
                    dragOrig = selected.Copy();
                    return;
                }
            }
            var hit = HitMark(n);
            if (hit != null)
            {
                selected = hit;
                drag = Drag.Move;
                dragOrig = hit.Copy();
                if (before != hit) RefreshPanel();
                DrawOverlay();
                return;
            }
            bool had = selected != null;
            selected = null;
            if (n.X < 0 || n.Y < 0 || n.X > 1 || n.Y > 1)
            {
                if (had)
                {
                    RefreshPanel();
                    DrawOverlay();
                }
                return;
            }
            var m = NewMark(n);
            if (m.Kind == MarkKind.Text)
            {
                AddUndo(it, dragBefore);
                it.Edit.Marks.Add(m);
                selected = m;
                RefreshPanel(true);
                Render();
                return;
            }
            it.Edit.Marks.Add(m);
            selected = m;
            drag = Drag.Create;
            if (had) RefreshPanel();
        }

        MarkKind KindOfMark(string id)
        {
            switch (id)
            {
                case "arrow": return MarkKind.Arrow;
                case "box": return MarkKind.Box;
                case "marker": return MarkKind.Highlight;
            }
            return MarkKind.Text;
        }

        string IdOfKind(MarkKind k)
        {
            switch (k)
            {
                case MarkKind.Arrow: return "arrow";
                case MarkKind.Box: return "box";
                case MarkKind.Highlight: return "marker";
            }
            return "text";
        }

        PhotoMark NewMark(Point n)
        {
            PhotoMark m;
            if (tool == "redact")
            {
                m = redactProto.Copy();
                m.Whole = false;
            }
            else
            {
                m = new PhotoMark { Kind = KindOfMark(markKind), Size = markSize[markKind], Rgb = markColor[markKind], Bold = markBold, Text = string.IsNullOrEmpty(markText) ? "글자" : markText };
            }
            m.X = n.X;
            m.Y = n.Y;
            if (m.Kind == MarkKind.Highlight) m.Points = new List<Point> { n };
            return m;
        }

        void CropDown(Point p)
        {
            var n = Norm(p);
            dragStart = n;
            dragRect = cropRect;
            int h = HitHandle(RectHandles(cropRect), p);
            if (h >= 0 && !(cropAspect > 0 && h % 2 == 1))
            {
                drag = Drag.CropHandle;
                dragHandle = h;
                return;
            }
            bool isFull = IsFull(cropRect);
            if (!isFull && cropRect.Contains(n))
            {
                drag = Drag.CropMove;
                return;
            }
            if (n.X < 0 || n.Y < 0 || n.X > 1 || n.Y > 1) return;
            drag = Drag.CropNew;
        }

        static bool IsFull(Rect r)
        {
            return r.X <= 0.0005 && r.Y <= 0.0005 && r.Width >= 0.999 && r.Height >= 0.999;
        }

        void StageMove(object s, MouseEventArgs e)
        {
            var it = Current;
            if (it == null || it.Proxy == null || content.Width <= 0) return;
            Point p = e.GetPosition(overlay);
            if (drag == Drag.None)
            {
                stage.Cursor = CursorAt(p);
                return;
            }
            Point n = Norm(p);
            Point c = Clamp01(n);
            dragChanged = true;
            switch (drag)
            {
                case Drag.Create:
                    UpdateCreate(c);
                    RequestRender();
                    break;
                case Drag.Move:
                    selected.X = dragOrig.X;
                    selected.Y = dragOrig.Y;
                    selected.Points = dragOrig.Points == null ? null : new List<Point>(dragOrig.Points);
                    selected.Offset(n.X - dragStart.X, n.Y - dragStart.Y);
                    KeepInside(selected);
                    RequestRender();
                    break;
                case Drag.Handle:
                    UpdateHandle(c);
                    RequestRender();
                    break;
                case Drag.CropNew:
                    cropRect = cropAspect > 0 ? LockedRect(dragStart, c) : FloorSize(new Rect(dragStart, c));
                    DrawOverlay();
                    UpdateSizeLine();
                    break;
                case Drag.CropMove:
                    {
                        double dx = Math.Max(-dragRect.X, Math.Min(1 - dragRect.Right, n.X - dragStart.X));
                        double dy = Math.Max(-dragRect.Y, Math.Min(1 - dragRect.Bottom, n.Y - dragStart.Y));
                        cropRect = new Rect(dragRect.X + dx, dragRect.Y + dy, dragRect.Width, dragRect.Height);
                        DrawOverlay();
                        break;
                    }
                case Drag.CropHandle:
                    cropRect = cropAspect > 0 ? LockedHandle(c) : FloorSize(ResizeRect(dragRect, dragHandle, c));
                    DrawOverlay();
                    UpdateSizeLine();
                    break;
                case Drag.QuadHandle:
                    it.Quad[dragHandle] = c;
                    DrawOverlay();
                    break;
            }
        }

        static void KeepInside(PhotoMark m)
        {
            var b = m.Bounds;
            double dx = 0, dy = 0;
            if (b.Right < 0.02) dx = 0.02 - b.Right;
            if (b.Left > 0.98) dx = 0.98 - b.Left;
            if (b.Bottom < 0.02) dy = 0.02 - b.Bottom;
            if (b.Top > 0.98) dy = 0.98 - b.Top;
            if (dx != 0 || dy != 0) m.Offset(dx, dy);
        }

        void UpdateCreate(Point c)
        {
            var m = selected;
            switch (m.Kind)
            {
                case MarkKind.Arrow:
                    m.W = c.X - m.X;
                    m.H = c.Y - m.Y;
                    break;
                case MarkKind.Highlight:
                    {
                        var last = m.Points[m.Points.Count - 1];
                        if ((Scr(c) - Scr(last)).Length >= 3) m.Points.Add(c);
                        var box = PointsBox(m);
                        m.X = box.X;
                        m.Y = box.Y;
                        m.W = box.Width;
                        m.H = box.Height;
                        break;
                    }
                default:
                    {
                        var r = new Rect(dragStart, c);
                        m.X = r.X;
                        m.Y = r.Y;
                        m.W = r.Width;
                        m.H = r.Height;
                        break;
                    }
            }
        }

        void UpdateHandle(Point c)
        {
            var m = selected;
            if (m.Kind == MarkKind.Arrow)
            {
                if (dragHandle == 0)
                {
                    double ex = dragOrig.X + dragOrig.W, ey = dragOrig.Y + dragOrig.H;
                    m.X = c.X;
                    m.Y = c.Y;
                    m.W = ex - c.X;
                    m.H = ey - c.Y;
                }
                else
                {
                    m.W = c.X - m.X;
                    m.H = c.Y - m.Y;
                }
                return;
            }
            var r = FloorSize(ResizeRect(dragOrig.Bounds, dragHandle, c));
            m.X = r.X;
            m.Y = r.Y;
            m.W = r.Width;
            m.H = r.Height;
        }

        /// 손잡이 h를 c로 옮겼을 때의 네모. 반대쪽을 넘어가면 뒤집힌다.
        static Rect ResizeRect(Rect r, int h, Point c)
        {
            double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
            if (h == 0 || h == 6 || h == 7) l = c.X;
            if (h == 2 || h == 3 || h == 4) rr = c.X;
            if (h == 0 || h == 1 || h == 2) t = c.Y;
            if (h == 4 || h == 5 || h == 6) b = c.Y;
            return new Rect(new Point(l, t), new Point(rr, b));
        }

        Rect FloorSize(Rect r)
        {
            double mw = 8 / content.Width, mh = 8 / content.Height;
            double w = Math.Max(mw, r.Width), h = Math.Max(mh, r.Height);
            double x = Math.Min(r.X, 1 - w), y = Math.Min(r.Y, 1 - h);
            return new Rect(Math.Max(0, x), Math.Max(0, y), Math.Min(1, w), Math.Min(1, h));
        }

        double ImageAspect
        {
            get { return geo == null ? 1 : (double)geo.PixelWidth / geo.PixelHeight; }
        }

        /// 고정 비율: anchor에서 p 쪽으로 가장 크게 그릴 수 있는 네모
        Rect LockedRect(Point anchor, Point p)
        {
            double ia = ImageAspect, a = cropAspect;
            double sx = p.X >= anchor.X ? 1 : -1, sy = p.Y >= anchor.Y ? 1 : -1;
            double w = Math.Abs(p.X - anchor.X), h = Math.Abs(p.Y - anchor.Y);
            w = Math.Max(w, h * a / ia);
            double maxW = sx > 0 ? 1 - anchor.X : anchor.X, maxH = sy > 0 ? 1 - anchor.Y : anchor.Y;
            w = Math.Max(0.01, Math.Min(w, Math.Min(maxW, maxH * a / ia)));
            h = w * ia / a;
            return new Rect(new Point(anchor.X, anchor.Y), new Point(anchor.X + sx * w, anchor.Y + sy * h));
        }

        Rect LockedHandle(Point c)
        {
            var hs = RectHandles(dragRect);
            return LockedRect(hs[(dragHandle + 4) % 8], c);
        }

        void StageUp(object s, MouseButtonEventArgs e)
        {
            if (drag != Drag.None) stage.ReleaseMouseCapture();
        }

        void EndDrag()
        {
            var it = Current;
            var kind = drag;
            drag = Drag.None;
            if (it == null) return;
            switch (kind)
            {
                case Drag.Create:
                    {
                        var m = selected;
                        bool tooSmall;
                        if (m.Kind == MarkKind.Arrow)
                        {
                            tooSmall = (Scr(new Point(m.X + m.W, m.Y + m.H)) - Scr(new Point(m.X, m.Y))).Length < 10;
                        }
                        else if (m.Kind == MarkKind.Highlight) tooSmall = m.Points.Count < 2;
                        else tooSmall = m.W * content.Width < 6 || m.H * content.Height < 6;
                        if (tooSmall)
                        {
                            it.Edit.Marks.Remove(m);
                            selected = null;
                        }
                        else AddUndo(it, dragBefore);
                        RefreshPanel();
                        Render();
                        break;
                    }
                case Drag.Move:
                case Drag.Handle:
                    if (dragChanged) AddUndo(it, dragBefore);
                    Render();
                    break;
                case Drag.CropNew:
                case Drag.CropMove:
                case Drag.CropHandle:
                    DrawOverlay();
                    UpdateSizeLine();
                    break;
            }
        }

        Cursor CursorAt(Point p)
        {
            var it = Current;
            if (compare) return Cursors.Arrow;
            var n = Norm(p);
            switch (tool)
            {
                case "redact":
                case "mark":
                    if (selected != null && HitHandle(HandlesOf(selected), p) >= 0) return Cursors.Hand;
                    if (HitMark(n) != null) return Cursors.SizeAll;
                    return Cursors.Cross;
                case "crop":
                    if (HitHandle(RectHandles(cropRect), p) >= 0) return Cursors.Hand;
                    if (!IsFull(cropRect) && cropRect.Contains(n)) return Cursors.SizeAll;
                    return Cursors.Cross;
                case "scan":
                    if (it.Edit.Quad == null && it.Quad != null && HitHandle(it.Quad, p) >= 0) return Cursors.Hand;
                    return Cursors.Arrow;
            }
            return Cursors.Arrow;
        }

        // ── 키보드 ──

        void OnKey(object s, KeyEventArgs e)
        {
            bool inBox = Keyboard.FocusedElement is TextBox;
            bool inSlider = Keyboard.FocusedElement is Slider;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.S)
            {
                Save();
                e.Handled = true;
                return;
            }
            if (inBox) return;
            if (ctrl && e.Key == Key.Z)
            {
                DoUndo();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.Y)
            {
                DoRedo();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                if (selected != null)
                {
                    DeleteSelected();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (selected != null)
                {
                    selected = null;
                    RefreshPanel();
                    DrawOverlay();
                }
                else if (tool == "crop" && !IsFull(cropRect))
                {
                    cropRect = Full;
                    DrawOverlay();
                    UpdateSizeLine();
                }
                else Close();
            }
            else if (!inSlider && e.Key == Key.Left && items.Count > 1)
            {
                Go(index - 1);
                e.Handled = true;
            }
            else if (!inSlider && e.Key == Key.Right && items.Count > 1)
            {
                Go(index + 1);
                e.Handled = true;
            }
        }

        void DeleteSelected()
        {
            var it = Current;
            if (it == null || selected == null) return;
            PushUndo(null);
            it.Edit.Marks.Remove(selected);
            selected = null;
            RefreshPanel();
            Render();
        }

        // ── 얼굴 ──

        void FindFaces()
        {
            var it = Current;
            if (it == null || faceBusy) return;
            faceBusy = true;
            SetStatus("얼굴을 찾는 중이에요");
            var edit = it.Edit.Copy();
            string path = it.Path;
            var proto = redactProto.Copy();
            var t = new Thread(delegate ()
            {
                List<Rect> faces = null;
                string error = null;
                try
                {
                    var src = Imaging.Load(path);
                    faces = FaceFinder.Find(PhotoRender.Geometry(src, edit));
                }
                catch (UserFacingException ex)
                {
                    error = ex.Message;
                }
                catch (Exception ex)
                {
                    Log.Write("얼굴 찾기 실패: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                    error = "얼굴을 찾지 못했어요. 드래그해서 직접 가려 주세요.";
                }
                Dispatcher.BeginInvoke(new Action(delegate { FacesFound(it, faces, error, proto); }));
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void FacesFound(Item it, List<Rect> faces, string error, PhotoMark proto)
        {
            faceBusy = false;
            if (error != null)
            {
                SetStatus(error);
                return;
            }
            if (faces.Count == 0)
            {
                SetStatus("얼굴을 찾지 못했어요. 드래그해서 직접 가려 주세요.");
                return;
            }
            if (Current == it) PushUndo(null);
            else AddUndo(it, it.Edit.Copy());
            int added = 0;
            foreach (var f in faces)
            {
                bool dup = it.Edit.Marks.Any(m => m.Kind == MarkKind.Redact && !m.Whole && Overlap(m.Bounds, f) > 0.5);
                if (dup) continue;
                var m2 = proto.Copy();
                m2.Whole = false;
                m2.Oval = true;
                m2.X = f.X;
                m2.Y = f.Y;
                m2.W = f.Width;
                m2.H = f.Height;
                it.Edit.Marks.Add(m2);
                added++;
            }
            SetStatus("얼굴 " + added + "개를 찾았어요. 빠진 얼굴이 없는지 꼭 확인해 주세요.");
            RefreshPanel();
            RequestRender();
        }

        static double Overlap(Rect a, Rect b)
        {
            var i = Rect.Intersect(a, b);
            if (i.IsEmpty) return 0;
            double area = i.Width * i.Height;
            return area / Math.Max(1e-9, Math.Min(a.Width * a.Height, b.Width * b.Height));
        }

        // ── 오른쪽 칸 ──

        void RefreshPanel()
        {
            RefreshPanel(false);
        }

        void RefreshPanel(bool focusText)
        {
            panel.Children.Clear();
            var it = Current;
            if (it == null || it.Proxy == null) return;
            building = true;
            try
            {
                switch (tool)
                {
                    case "redact": PanelRedact(); break;
                    case "crop": PanelCrop(); break;
                    case "rotate": PanelRotate(); break;
                    case "size": PanelSize(); break;
                    case "adjust": PanelAdjust(); break;
                    case "mark": PanelMark(focusText); break;
                    case "frame": PanelFrame(); break;
                    case "scan": PanelScan(); break;
                    case "info": PanelInfo(); break;
                }
            }
            finally
            {
                building = false;
            }
        }

        void Head(string title, string hint)
        {
            var t = Theme.Text(title, 17, FontWeights.Bold, Theme.Ink);
            t.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(t);
            if (hint != null) panel.Children.Add(Theme.MetaLabel(hint));
        }

        void Label(string s)
        {
            var t = Theme.Text(s, 13, FontWeights.Bold, Theme.Ink2);
            t.Margin = new Thickness(0, 16, 0, 7);
            panel.Children.Add(t);
        }

        int groupCounter;

        void Chips(string[] ids, string[] labels, string current, Action<string> pick)
        {
            var wrap = new WrapPanel();
            string group = "g" + (++groupCounter);
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                var r = new RadioButton
                {
                    Style = Theme.GetStyle("Chip"),
                    Tag = H,
                    GroupName = group,
                    Content = labels[i],
                    MinHeight = 38,
                    FontSize = 13.5,
                    Margin = new Thickness(0, 0, 6, 6),
                    IsChecked = id == current
                };
                r.Checked += delegate
                {
                    if (!building) pick(id);
                };
                wrap.Children.Add(r);
            }
            panel.Children.Add(wrap);
        }

        void SliderRow(string label, double min, double max, double value, Func<double, string> format, Action<double> set)
        {
            var dock = new DockPanel { Margin = new Thickness(0, 14, 0, 2) };
            var val = Theme.Text(format(value), 13, FontWeights.Bold, H.DeepBrush);
            val.TextWrapping = TextWrapping.NoWrap;
            DockPanel.SetDock(val, Dock.Right);
            dock.Children.Add(val);
            dock.Children.Add(Theme.Text(label, 13, FontWeights.Bold, Theme.Ink2));
            panel.Children.Add(dock);
            var s = new Slider { Minimum = min, Maximum = max, Value = value, Tag = H };
            s.ValueChanged += delegate
            {
                if (building) return;
                val.Text = format(s.Value);
                set(s.Value);
            };
            panel.Children.Add(s);
        }

        void Swatches(uint[] colors, uint current, Action<uint> pick)
        {
            var wrap = new WrapPanel();
            string group = "g" + (++groupCounter);
            foreach (uint c in colors)
            {
                uint color = c;
                var r = new RadioButton
                {
                    Style = Theme.GetStyle("Swatch"),
                    GroupName = group,
                    Background = Theme.Frozen(Theme.Hex(color)),
                    IsChecked = color == current
                };
                r.Checked += delegate
                {
                    if (!building) pick(color);
                };
                wrap.Children.Add(r);
            }
            panel.Children.Add(wrap);
        }

        Button Act(string text, string style, Action click)
        {
            var b = Theme.MakeButton(text, style);
            b.Height = 40;
            b.FontSize = 14;
            b.Margin = new Thickness(0, 8, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Click += delegate { click(); };
            panel.Children.Add(b);
            return b;
        }

        void NoteBox(string s)
        {
            var n = Theme.Note(s, H);
            n.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(n);
        }

        // 가리기

        PhotoMark WholeMark()
        {
            return Current.Edit.Marks.FirstOrDefault(m => m.Kind == MarkKind.Redact && m.Whole);
        }

        PhotoMark RedactTarget()
        {
            if (selected != null && selected.Kind == MarkKind.Redact) return selected;
            return WholeMark();
        }

        void SetRedact(string key, Action<PhotoMark> change)
        {
            var t = RedactTarget();
            if (t != null)
            {
                PushUndo(key);
                change(t);
                RequestRender();
            }
            change(redactProto);
        }

        void PanelRedact()
        {
            var e = Current.Edit;
            Head("가리기", "사진 위를 드래그하면 그곳만 가려져요. 가린 곳을 누르면 옮기거나 크기를 바꿀 수 있어요.");
            var target = RedactTarget() ?? redactProto;
            Label("가리는 방법");
            Chips(new[] { "blur", "mosaic", "fill" }, new[] { "흐리게", "모자이크", "색 칸" }, target.Mode, id =>
            {
                SetRedact(null, m => m.Mode = id);
                Later(RefreshPanel);
            });
            if (target.Mode == "fill")
            {
                Label("칸 색");
                Swatches(Colors, target.Rgb, c => SetRedact(null, m => m.Rgb = c));
            }
            else SliderRow("세기", 0.15, 1, target.Strength, v => Math.Round(v * 100) + "%", v => SetRedact("strength", m => m.Strength = v));
            if (!target.Whole)
            {
                Label("모양");
                Chips(new[] { "rect", "oval" }, new[] { "네모", "동그라미" }, target.Oval ? "oval" : "rect", id => SetRedact(null, m => m.Oval = id == "oval"));
            }
            var whole = WholeMark();
            Label("한 번에");
            Act(whole == null ? "사진 전체 가리기" : "사진 전체 가리기 풀기", "Line", delegate
            {
                PushUndo(null);
                var w = WholeMark();
                if (w != null) e.Marks.Remove(w);
                else
                {
                    var m = redactProto.Copy();
                    m.Whole = true;
                    m.Oval = false;
                    e.Marks.Insert(0, m);
                }
                selected = null;
                Later(RefreshPanel);
                RequestRender();
            });
            var face = Act("얼굴 찾아서 가리기", "Line", FindFaces);
            face.ToolTip = "이 PC 안에서 찾아요. 사진은 밖으로 나가지 않아요.";
            if (selected != null) Act("이 부분 지우기", "Line", DeleteSelected);
            else if (e.Marks.Any(m => m.Kind == MarkKind.Redact))
            {
                Act("가린 곳 모두 지우기", "Line", delegate
                {
                    PushUndo(null);
                    e.Marks.RemoveAll(m => m.Kind == MarkKind.Redact);
                    Later(RefreshPanel);
                    RequestRender();
                });
            }
            NoteBox("이름·번호처럼 꼭 숨길 글자는 '색 칸'이 가장 안전해요. 흐림과 모자이크는 세기가 약하면 알아볼 수 있어요.");
        }

        // 자르기

        TextBlock sizeLine;

        void PanelCrop()
        {
            Head("자르기", "사진 위를 드래그해 남길 곳을 고르세요.");
            Label("비율");
            string cur = cropAspect <= 0 ? "free" : Math.Abs(cropAspect - 1) < 0.001 ? "1:1" : Math.Abs(cropAspect - 4.0 / 3) < 0.001 ? "4:3" : Math.Abs(cropAspect - 0.75) < 0.001 ? "3:4" : Math.Abs(cropAspect - 16.0 / 9) < 0.001 ? "16:9" : "9:16";
            Chips(new[] { "free", "1:1", "4:3", "3:4", "16:9", "9:16" }, new[] { "자유", "1:1", "4:3", "3:4", "16:9", "9:16" }, cur, id => SetAspect(id));
            sizeLine = Theme.Text("", 13.5, FontWeights.Bold, H.DeepBrush);
            sizeLine.Margin = new Thickness(0, 14, 0, 4);
            panel.Children.Add(sizeLine);
            Act("여기만 남기기", "Primary", delegate
            {
                CommitCrop();
                geoKey = null;
                Render();
            });
            Act("고른 곳 풀기", "Line", delegate
            {
                cropRect = Full;
                DrawOverlay();
                UpdateSizeLine();
            });
            NoteBox("다른 도구로 넘어가면 고른 곳만 남겨요. 잘못 잘랐으면 되돌리기를 누르세요.");
        }

        void SetAspect(string id)
        {
            switch (id)
            {
                case "1:1": cropAspect = 1; break;
                case "4:3": cropAspect = 4.0 / 3; break;
                case "3:4": cropAspect = 0.75; break;
                case "16:9": cropAspect = 16.0 / 9; break;
                case "9:16": cropAspect = 9.0 / 16; break;
                default: cropAspect = 0; break;
            }
            if (cropAspect > 0)
            {
                double ia = ImageAspect;
                double w = 1, h = 1;
                if (cropAspect > ia) h = ia / cropAspect;
                else w = cropAspect / ia;
                w *= 0.92;
                h *= 0.92;
                cropRect = new Rect((1 - w) / 2, (1 - h) / 2, w, h);
            }
            DrawOverlay();
            UpdateSizeLine();
        }

        void CommitCrop()
        {
            var it = Current;
            if (it == null || tool != "crop" || IsFull(cropRect) || geo == null) return;
            PushUndo(null);
            it.Edit.CropTo(cropRect, ImageAspect);
            cropRect = Full;
            selected = null;
            geoKey = null;
        }

        void UpdateSizeLine()
        {
            var it = Current;
            if (it == null || geo == null || it.Proxy == null) return;
            double scale = (double)it.FullW / it.Proxy.PixelWidth;
            int gw = (int)Math.Round(geo.PixelWidth * scale), gh = (int)Math.Round(geo.PixelHeight * scale);
            if (tool == "crop" && sizeLine != null)
            {
                sizeLine.Text = Math.Round(gw * cropRect.Width) + " × " + Math.Round(gh * cropRect.Height);
            }
            if (tool == "size" && sizeLine != null)
            {
                int rw, rh;
                PhotoRender.ResultSize(gw, gh, it.Edit, out rw, out rh);
                sizeLine.Text = "저장하면 " + rw + " × " + rh;
            }
        }

        // 돌리기

        void Transform(Action<PhotoEdit> f)
        {
            PushUndo(null);
            f(Current.Edit);
            selected = null;
            geoKey = null;
            Later(RefreshPanel);
            Render();
        }

        void PanelRotate()
        {
            var e = Current.Edit;
            Head("돌리기", null);
            Act("왼쪽으로 90°", "Line", delegate { Transform(x => x.RotateLeft()); });
            Act("오른쪽으로 90°", "Line", delegate { Transform(x => x.RotateRight()); });
            Act("좌우 뒤집기", "Line", delegate { Transform(x => x.FlipH()); });
            Act("위아래 뒤집기", "Line", delegate { Transform(x => x.FlipV()); });
            SliderRow("기울기 바로잡기", -15, 15, e.Angle, v => v.ToString("+0.0;-0.0;0") + "°", v =>
            {
                PushUndo("angle");
                Current.Edit.Angle = Math.Abs(v) < 0.05 ? 0 : v;
                RequestRender();
            });
            NoteBox("기울기를 바로잡으면 귀퉁이가 비지 않게 사진이 조금 커져요.");
        }

        // 크기

        void PanelSize()
        {
            var it = Current;
            var e = it.Edit;
            Head("크기 바꾸기", null);
            Label("비율로");
            string cur = e.ResizeW > 0 || e.ResizeH > 0 ? "custom" : e.ResizePercent > 0 ? ((int)e.ResizePercent).ToString() : "0";
            Chips(new[] { "0", "75", "50", "25" }, new[] { "원본", "75%", "50%", "25%" }, cur, id =>
            {
                PushUndo(null);
                e.ResizeW = 0;
                e.ResizeH = 0;
                e.ResizePercent = int.Parse(id);
                Later(RefreshPanel);
                Later(UpdateSizeLine);
            });
            Label("칸 수로");
            double scale = (double)it.FullW / it.Proxy.PixelWidth;
            int gw = geo == null ? it.FullW : (int)Math.Round(geo.PixelWidth * scale), gh = geo == null ? it.FullH : (int)Math.Round(geo.PixelHeight * scale);
            int rw, rh;
            PhotoRender.ResultSize(gw, gh, e, out rw, out rh);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var wb = new TextBox { Style = Theme.GetStyle("TextField"), Text = rw.ToString(), Height = 42, Tag = "가로" };
            var hb = new TextBox { Style = Theme.GetStyle("TextField"), Text = rh.ToString(), Height = 42, Tag = "세로" };
            var x2 = Theme.Text("×", 15, FontWeights.Bold, Theme.Ink3);
            x2.HorizontalAlignment = HorizontalAlignment.Center;
            x2.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(x2, 1);
            Grid.SetColumn(hb, 2);
            row.Children.Add(wb);
            row.Children.Add(x2);
            row.Children.Add(hb);
            panel.Children.Add(row);
            bool syncing = false;
            bool locked = lockRatio;
            Action<TextBox, TextBox, double> onChange = (me, other, perUnit) =>
            {
                if (building || syncing) return;
                int v;
                if (!int.TryParse(me.Text.Trim(), out v) || v <= 0 || v > 20000) return;
                PushUndo("resize");
                syncing = true;
                if (lockRatio)
                {
                    other.Text = Math.Max(1, (int)Math.Round(v * perUnit)).ToString();
                }
                int w3, h3;
                if (int.TryParse(wb.Text.Trim(), out w3) && int.TryParse(hb.Text.Trim(), out h3))
                {
                    e.ResizePercent = 0;
                    e.ResizeW = w3;
                    e.ResizeH = h3;
                }
                syncing = false;
                UpdateSizeLine();
            };
            wb.TextChanged += delegate { onChange(wb, hb, (double)gh / gw); };
            hb.TextChanged += delegate { onChange(hb, wb, (double)gw / gh); };
            var sw = Theme.Switch("비율 유지", null, locked, v => lockRatio = v);
            sw.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(sw);
            sizeLine = Theme.Text("", 13.5, FontWeights.Bold, H.DeepBrush);
            sizeLine.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(sizeLine);
            NoteBox("크기는 저장할 때 적용돼요. 화면에서는 같은 크기로 보여요.");
            Later(UpdateSizeLine);
        }

        bool lockRatio = true;

        // 보정

        void PanelAdjust()
        {
            var e = Current.Edit;
            Head("보정", null);
            Func<double, string> pct = v => (v > 0 ? "+" : "") + Math.Round(v * 100);
            SliderRow("밝기", -1, 1, e.Brightness, pct, v => Adj("b", x => x.Brightness = v));
            SliderRow("대비", -1, 1, e.Contrast, pct, v => Adj("c", x => x.Contrast = v));
            SliderRow("채도", -1, 1, e.Saturation, pct, v => Adj("s", x => x.Saturation = v));
            SliderRow("선명도", 0, 1, e.Sharpness, pct, v => Adj("h", x => x.Sharpness = v));
            Act("처음대로", "Line", delegate
            {
                PushUndo(null);
                var x = Current.Edit;
                x.Brightness = 0;
                x.Contrast = 0;
                x.Saturation = 0;
                x.Sharpness = 0;
                Later(RefreshPanel);
                RequestRender();
            });
        }

        void Adj(string key, Action<PhotoEdit> f)
        {
            PushUndo("adj-" + key);
            f(Current.Edit);
            RequestRender();
        }

        // 글자·표시

        void SetMark(string key, Action<PhotoMark> change)
        {
            if (selected != null && selected.Kind != MarkKind.Redact)
            {
                PushUndo(key);
                change(selected);
                RequestRender();
            }
        }

        void PanelMark(bool focusText)
        {
            var sel = selected != null && selected.Kind != MarkKind.Redact ? selected : null;
            if (sel != null) markKind = IdOfKind(sel.Kind);
            Head("글자·표시", "종류를 고르고 사진 위에서 누르거나 드래그하세요.");
            Label("종류");
            Chips(new[] { "text", "arrow", "marker", "box" }, new[] { "글자", "화살표", "형광펜", "네모" }, markKind, id =>
            {
                markKind = id;
                selected = null;
                DrawOverlay();
                Later(RefreshPanel);
            });
            double size = sel != null ? sel.Size : markSize[markKind];
            uint color = sel != null ? sel.Rgb : markColor[markKind];
            if (markKind == "text")
            {
                Label("글자");
                var box = new TextBox { Style = Theme.GetStyle("TextField"), Text = sel != null ? sel.Text : markText, Height = 44, Tag = "여기에 써 주세요" };
                box.TextChanged += delegate
                {
                    if (building) return;
                    markText = box.Text;
                    if (selected != null && selected.Kind == MarkKind.Text)
                    {
                        PushUndo("text");
                        selected.Text = box.Text;
                        RequestRender();
                    }
                };
                panel.Children.Add(box);
                if (focusText)
                {
                    Later(delegate
                    {
                        box.Focus();
                        box.SelectAll();
                    });
                }
                SliderRow("글자 크기", 0.02, 0.2, size, v => Math.Round(v * 100) + "", v =>
                {
                    markSize["text"] = v;
                    SetMark("size", m => m.Size = v);
                });
                var bold = Theme.Switch("굵게", null, sel != null ? sel.Bold : markBold, v =>
                {
                    markBold = v;
                    SetMark(null, m => m.Bold = v);
                });
                bold.Margin = new Thickness(0, 14, 0, 0);
                panel.Children.Add(bold);
            }
            else
            {
                string kind = markKind;
                double min = kind == "marker" ? 0.015 : 0.004, max = kind == "marker" ? 0.14 : 0.04;
                SliderRow("굵기", min, max, size, v => Math.Round(v * 1000) + "", v =>
                {
                    markSize[kind] = v;
                    SetMark("size", m => m.Size = v);
                });
            }
            Label("색");
            string k2 = markKind;
            Swatches(Colors, color, c =>
            {
                markColor[k2] = c;
                SetMark(null, m => m.Rgb = c);
            });
            if (sel != null) Act("이것 지우기", "Line", DeleteSelected);
        }

        // 꾸미기

        void PanelFrame()
        {
            var e = Current.Edit;
            Head("꾸미기", null);
            Label("바탕");
            string cur = e.BackTransparent ? "clear" : e.BackBlur ? "blur" : "color";
            Chips(new[] { "color", "blur", "clear" }, new[] { "색", "사진 흐리게", "투명" }, cur, id =>
            {
                PushUndo(null);
                e.BackTransparent = id == "clear";
                e.BackBlur = id == "blur";
                if (!e.HasFrame) e.Margin = 0.06;
                Later(RefreshPanel);
                RequestRender();
            });
            if (cur == "color")
            {
                Swatches(new uint[] { 0xFFFFFF, 0xF6F3EE, 0x22252F, 0xFFD447, 0xFF8473, 0x52D3A0, 0x6CA8FF, 0xB596FF, 0xFF8FC2 }, e.BackRgb, c =>
                {
                    PushUndo("back");
                    e.BackRgb = c;
                    if (!e.HasFrame) e.Margin = 0.06;
                    RequestRender();
                });
            }
            SliderRow("여백", 0, 0.3, e.Margin, v => Math.Round(v * 100) + "%", v =>
            {
                PushUndo("margin");
                e.Margin = v < 0.003 ? 0 : v;
                RequestRender();
            });
            SliderRow("둥근 모서리", 0, 0.25, e.Corner, v => Math.Round(v * 100) + "%", v =>
            {
                PushUndo("corner");
                e.Corner = v < 0.002 ? 0 : v;
                RequestRender();
            });
            Label("바탕 비율");
            string ratio = e.CanvasRatio <= 0 ? "0" : Math.Abs(e.CanvasRatio - 1) < 0.01 ? "1" : Math.Abs(e.CanvasRatio - 4.0 / 3) < 0.01 ? "1.3333" : Math.Abs(e.CanvasRatio - 16.0 / 9) < 0.01 ? "1.7777" : "0.5625";
            Chips(new[] { "0", "1", "1.3333", "1.7777", "0.5625" }, new[] { "그대로", "1:1", "4:3", "16:9", "9:16" }, ratio, id =>
            {
                PushUndo(null);
                double v = double.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
                e.CanvasRatio = v == 1.3333 ? 4.0 / 3 : v == 1.7777 ? 16.0 / 9 : v;
                RequestRender();
            });
            NoteBox("바탕이 투명하면 PNG나 WEBP로 저장돼요.");
        }

        // 문서 펴기

        void PanelScan()
        {
            var it = Current;
            var e = it.Edit;
            Head("문서 펴기", "비스듬히 찍은 종이를 반듯하게 펴고, 그늘을 지워요.");
            if (e.Quad == null)
            {
                NoteBox("네 점을 종이 모서리에 맞춘 뒤 '반듯하게 펴기'를 누르세요.");
                Act("종이 찾기", "Line", delegate
                {
                    var q = PaperFinder.Find(it.Proxy);
                    if (q == null) SetStatus("종이 모서리를 찾지 못했어요. 점을 직접 옮겨 주세요.");
                    else
                    {
                        it.Quad = q;
                        SetStatus("");
                    }
                    DrawOverlay();
                });
                Act("반듯하게 펴기", "Primary", delegate
                {
                    Transform(x =>
                    {
                        x.Quad = (Point[])it.Quad.Clone();
                        x.Turns = 0;
                        x.Mirror = false;
                        x.Angle = 0;
                        x.Crop = Full;
                        x.Marks.Clear();
                        x.ResizePercent = 0;
                        x.ResizeW = 0;
                        x.ResizeH = 0;
                    });
                });
            }
            else
            {
                Act("모서리 다시 맞추기", "Line", delegate { Transform(x => x.Quad = null); });
            }
            Label("문서 색");
            Chips(new[] { "", "clean", "gray", "bw" }, new[] { "그대로", "깨끗하게", "회색", "검정·흰색" }, e.Filter ?? "", id =>
            {
                PushUndo(null);
                e.Filter = id;
                RequestRender();
            });
            if (e.Quad != null)
            {
                SliderRow("기울기 바로잡기", -15, 15, e.Angle, v => v.ToString("+0.0;-0.0;0") + "°", v =>
                {
                    PushUndo("angle");
                    e.Angle = Math.Abs(v) < 0.05 ? 0 : v;
                    RequestRender();
                });
                Act("글줄에 맞춰 바로잡기", "Line", delegate
                {
                    double skew = PaperFinder.Skew(geo);
                    if (Math.Abs(skew) < 0.05)
                    {
                        SetStatus("글줄이 이미 반듯해요.");
                        return;
                    }
                    Transform(x => x.Angle = Math.Max(-15, Math.Min(15, x.Angle + skew)));
                });
            }
        }

        // 정보

        void PanelInfo()
        {
            var it = Current;
            Head("사진 정보", "사진 파일 안에 함께 저장된 정보예요.");
            List<PhotoInfo.Row> rows;
            try
            {
                rows = PhotoInfo.Read(it.Path);
            }
            catch (Exception ex)
            {
                Log.Write("사진 정보 읽기 실패: " + ex.GetType().Name);
                rows = new List<PhotoInfo.Row>();
            }
            var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var r in rows)
            {
                var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var name = Theme.Text(r.Label, 13, FontWeights.Bold, r.Sensitive ? H.DeepBrush : Theme.Ink2);
                name.Width = 86;
                DockPanel.SetDock(name, Dock.Left);
                dock.Children.Add(name);
                var value = Theme.Text(r.Value, 13.5, FontWeights.Normal, Theme.Ink);
                dock.Children.Add(value);
                list.Children.Add(dock);
            }
            panel.Children.Add(list);
            if (PhotoInfo.HasSensitive(rows))
            {
                NoteBox("찍은 때·기기·장소가 들어 있어요. 사진을 나눠 주기 전에 지우는 게 좋아요.");
                Act("정보만 지운 사본 만들기", "Primary", delegate
                {
                    Jobs.Start(new Job(action, new ActionOption("strip", "정보 지우기", ""), new List<string> { it.Path }));
                    SetStatus("정보를 지운 사본을 만들고 있어요.");
                });
            }
            else NoteBox("찍은 때나 장소 정보는 없어요.");
            var pale = Theme.MetaLabel("여기서 손질해 저장한 파일에는 이런 정보가 들어가지 않아요.");
            pale.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(pale);
        }

        // ── 저장 ──

        void ApplyToOthers()
        {
            var it = Current;
            if (it == null) return;
            CommitCrop();
            var src = it.Edit.Copy();
            src.Quad = null;
            int n = 0;
            foreach (var x in items)
            {
                if (x == it) continue;
                x.Undo.Add(x.Edit);
                x.Redo.Clear();
                x.Edit = src.Copy();
                x.Changed = true;
                n++;
            }
            SetStatus("다른 사진 " + n + "장에도 똑같이 넣었어요. 사진마다 확인해 보세요.");
        }

        void Save()
        {
            CommitCrop();
            var list = items.Where(i => !i.Edit.IsUntouched).ToList();
            if (list.Count == 0)
            {
                SetStatus("아직 손질한 것이 없어요.");
                return;
            }
            var payload = new PhotoSave { Format = saveFormat };
            foreach (var i in list) payload.Items.Add(new PhotoSaveItem { Path = i.Path, Edit = i.Edit.Copy() });
            var job = new Job(action, new ActionOption("save", "저장", ""), list.Select(i => i.Path).ToList()) { Payload = payload };
            saved = true;
            Close();
            Jobs.Start(job);
        }

        void OnClosing(object s, System.ComponentModel.CancelEventArgs e)
        {
            renderTimer.Stop();
            if (saved || preview) return;
            CommitCrop();
            if (!items.Any(i => i.Changed && !i.Edit.IsUntouched)) return;
            int n = Dialog.Ask(this, H, "photo", "저장하지 않고 닫을까요?", "손질한 내용은 사라져요. 원본 사진은 그대로예요.", null, "저장 안 하고 닫기", "계속 손질하기");
            if (n != 0) e.Cancel = true;
        }

        /// 보기용: 예시 표시를 넣는다(그림 확인에만 쓴다).
        public void AddDemoMarks()
        {
            Loaded += delegate
            {
                var it = Current;
                if (it == null) return;
                it.Edit.Marks.Add(new PhotoMark { Kind = MarkKind.Redact, Mode = "blur", Strength = 0.7, X = 0.08, Y = 0.1, W = 0.26, H = 0.2 });
                it.Edit.Marks.Add(new PhotoMark { Kind = MarkKind.Redact, Mode = "mosaic", Strength = 0.6, X = 0.42, Y = 0.1, W = 0.24, H = 0.2 });
                var sel = new PhotoMark { Kind = MarkKind.Redact, Mode = "fill", Rgb = 0x22252F, X = 0.1, Y = 0.55, W = 0.3, H = 0.14 };
                it.Edit.Marks.Add(sel);
                it.Edit.Marks.Add(new PhotoMark { Kind = MarkKind.Text, Text = "글자 넣기", Size = 0.07, Rgb = 0xE5483B, Bold = true, X = 0.5, Y = 0.5 });
                it.Edit.Marks.Add(new PhotoMark { Kind = MarkKind.Arrow, Size = 0.012, Rgb = 0x2F7BF0, X = 0.55, Y = 0.85, W = 0.2, H = -0.15 });
                selected = sel;
                RefreshPanel();
                Render();
            };
        }
    }

    /// 여러 장을 한 장으로 모으는 창
    public class CollageSave
    {
        public List<string> Order;
        public CollageSpec Spec;
        public string Format = "jpg";
    }

    public class CollageWindow : Window
    {
        static readonly Hue H = Theme.Orange;
        readonly MenuAction action;
        readonly List<string> order;
        readonly Dictionary<string, BitmapSource> proxies = new Dictionary<string, BitmapSource>();
        readonly CollageSpec spec = new CollageSpec();
        string layout = "auto";
        string format = "jpg";
        int picked = -1;
        readonly Image picture;
        readonly StackPanel panel;
        readonly WrapPanel strip;
        readonly TextBlock status;
        readonly DispatcherTimer timer;
        readonly bool preview;
        int groupCounter;

        public CollageWindow(MenuAction action, List<string> files, bool preview)
        {
            this.action = action;
            this.preview = preview;
            order = files.ToList();
            Title = "콜라주 · " + AppInfo.Name;
            var wa = SystemParameters.WorkArea;
            MinWidth = Math.Min(820, wa.Width);
            MinHeight = Math.Min(600, wa.Height);
            Width = Math.Max(MinWidth, Math.Min(1060, wa.Width * 0.92));
            Height = Math.Max(MinHeight, Math.Min(740, wa.Height * 0.92));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Polish(this);
            try { Icon = App.WindowIcon; } catch { }

            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 14) };
            var head = Theme.DialogHead("콜라주", "여러 사진을 한 장에 모아요. 위쪽 사진 두 장을 차례로 누르면 자리가 바뀌어요.", H, "collage");
            head.Margin = new Thickness(2, 0, 2, 12);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var foot = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            var close = Theme.MakeButton("닫기", "Line");
            close.Click += delegate { Close(); };
            var save = Theme.MakeButton("저장", "Primary");
            save.Margin = new Thickness(8, 0, 0, 0);
            save.Click += delegate { Save(); };
            right.Children.Add(close);
            right.Children.Add(save);
            DockPanel.SetDock(right, Dock.Right);
            foot.Children.Add(right);
            status = Theme.Text("", 13.5, FontWeights.Normal, Theme.Ink2);
            status.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(status);
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(304) });
            var center = new DockPanel();
            strip = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(strip, Dock.Top);
            center.Children.Add(strip);
            picture = new Image { Margin = new Thickness(16) };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            center.Children.Add(new Border { Background = Theme.Frozen(Theme.Hex(0xE9E4DB)), CornerRadius = new CornerRadius(20), Child = picture });
            center.Margin = new Thickness(0, 0, 12, 0);
            body.Children.Add(center);
            panel = new StackPanel();
            var side = Theme.Card(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 18);
            Grid.SetColumn(side, 1);
            body.Children.Add(side);
            root.Children.Add(body);
            Content = root;

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            timer.Tick += delegate
            {
                timer.Stop();
                Render();
            };
            BuildPanel();
            Loaded += delegate { LoadProxies(); };
        }

        void LoadProxies()
        {
            if (preview)
            {
                foreach (var f in order)
                {
                    int w, h;
                    proxies[f] = PhotoRender.Proxy(f, out w, out h);
                }
                FillStrip();
                Render();
                return;
            }
            status.Text = Theme.Keep("사진을 여는 중이에요");
            var list = order.ToList();
            var t = new Thread(delegate ()
            {
                foreach (var f in list)
                {
                    BitmapSource p = null;
                    try
                    {
                        p = Bgra.From(Imaging.Fit(Imaging.Load(f), 700)).ToBitmap();
                    }
                    catch (Exception ex)
                    {
                        Log.Write("콜라주 사진 열기 실패: " + ex.GetType().Name);
                    }
                    string path = f;
                    var bmp = p;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (bmp != null) proxies[path] = bmp;
                        else order.Remove(path);
                    }));
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    status.Text = "";
                    FillStrip();
                    Render();
                }));
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void FillStrip()
        {
            strip.Children.Clear();
            for (int i = 0; i < order.Count; i++)
            {
                int at = i;
                BitmapSource bmp;
                if (!proxies.TryGetValue(order[i], out bmp)) continue;
                var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                var b = new Border
                {
                    Width = 58,
                    Height = 46,
                    Margin = new Thickness(0, 0, 6, 6),
                    CornerRadius = new CornerRadius(9),
                    BorderThickness = new Thickness(2.5),
                    BorderBrush = at == picked ? H.VividBrush : Theme.Line2,
                    Background = Theme.Sheet,
                    Cursor = Cursors.Hand,
                    Child = img,
                    ToolTip = System.IO.Path.GetFileName(order[i])
                };
                b.MouseLeftButtonUp += delegate
                {
                    if (picked < 0) picked = at;
                    else if (picked == at) picked = -1;
                    else
                    {
                        string t = order[picked];
                        order[picked] = order[at];
                        order[at] = t;
                        picked = -1;
                        Render();
                    }
                    FillStrip();
                };
                strip.Children.Add(b);
            }
        }

        void Request()
        {
            if (!timer.IsEnabled) timer.Start();
        }

        void Render()
        {
            var list = order.Where(proxies.ContainsKey).Select(f => proxies[f]).ToList();
            if (list.Count == 0) return;
            try
            {
                picture.Source = Collage.Render(list, spec, 1200);
            }
            catch (Exception ex)
            {
                Log.Write("콜라주 그리기 실패: " + ex.GetType().Name);
            }
        }

        void Label(string s)
        {
            var t = Theme.Text(s, 13, FontWeights.Bold, Theme.Ink2);
            t.Margin = new Thickness(0, 14, 0, 7);
            panel.Children.Add(t);
        }

        void Chips(string[] ids, string[] labels, string current, Action<string> pick)
        {
            var wrap = new WrapPanel();
            string group = "c" + (++groupCounter);
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                var r = new RadioButton { Style = Theme.GetStyle("Chip"), Tag = H, GroupName = group, Content = labels[i], MinHeight = 38, FontSize = 13.5, Margin = new Thickness(0, 0, 6, 6), IsChecked = id == current };
                r.Checked += delegate { pick(id); };
                wrap.Children.Add(r);
            }
            panel.Children.Add(wrap);
        }

        void SliderRow(string label, double min, double max, double value, Action<double> set)
        {
            var dock = new DockPanel { Margin = new Thickness(0, 12, 0, 2) };
            dock.Children.Add(Theme.Text(label, 13, FontWeights.Bold, Theme.Ink2));
            panel.Children.Add(dock);
            var s = new Slider { Minimum = min, Maximum = max, Value = value, Tag = H };
            s.ValueChanged += delegate
            {
                set(s.Value);
                Request();
            };
            panel.Children.Add(s);
        }

        void BuildPanel()
        {
            var t = Theme.Text("모양", 17, FontWeights.Bold, Theme.Ink);
            panel.Children.Add(t);
            Label("줄과 칸");
            Chips(new[] { "auto", "row", "col", "2", "3" }, new[] { "저절로", "가로 한 줄", "세로 한 줄", "2칸씩", "3칸씩" }, layout, id =>
            {
                layout = id;
                spec.Columns = id == "auto" ? 0 : id == "row" ? 1000 : id == "col" ? 1 : int.Parse(id);
                Request();
            });
            Label("칸 모양");
            Chips(new[] { "1", "1.3333", "0.75", "1.7777" }, new[] { "정사각형", "4:3", "3:4", "16:9" }, "1.3333", id =>
            {
                spec.CellRatio = id == "1" ? 1 : id == "1.3333" ? 4.0 / 3 : id == "0.75" ? 0.75 : 16.0 / 9;
                Request();
            });
            Label("사진 넣는 법");
            Chips(new[] { "fill", "fit" }, new[] { "칸 꽉 채우기", "사진 전체 보이기" }, "fill", id =>
            {
                spec.Fill = id == "fill";
                Request();
            });
            SliderRow("사이 간격", 0, 0.06, spec.Gap, v => spec.Gap = v);
            SliderRow("둥근 모서리", 0, 0.25, spec.Corner, v => spec.Corner = v);
            Label("바탕 색");
            var wrap = new WrapPanel();
            foreach (uint c in new uint[] { 0xFFFFFF, 0xF6F3EE, 0x22252F, 0xFFD447, 0xFF8473, 0x52D3A0, 0x6CA8FF, 0xB596FF })
            {
                uint color = c;
                var r = new RadioButton { Style = Theme.GetStyle("Swatch"), GroupName = "cback", Background = Theme.Frozen(Theme.Hex(color)), IsChecked = color == spec.BackRgb };
                r.Checked += delegate
                {
                    spec.BackRgb = color;
                    Request();
                };
                wrap.Children.Add(r);
            }
            panel.Children.Add(wrap);
            Label("저장 크기(긴 변)");
            Chips(new[] { "1600", "2400", "3600" }, new[] { "1600", "2400", "3600" }, "2400", id => spec.LongSide = int.Parse(id));
            Label("저장 형식");
            Chips(new[] { "jpg", "png" }, new[] { "JPG", "PNG" }, format, id => format = id);
        }

        void Save()
        {
            if (order.Count < 2)
            {
                status.Text = Theme.Keep("콜라주는 사진이 두 장 이상 있어야 해요.");
                return;
            }
            var job = new Job(action, new ActionOption("collage", "콜라주", ""), order.ToList())
            {
                Payload = new CollageSave { Order = order.ToList(), Spec = spec, Format = format }
            };
            Close();
            Jobs.Start(job);
        }
    }
}
