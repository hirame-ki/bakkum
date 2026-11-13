// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 화면 스타일과 색감(설정 → 메뉴 모양). 켤 때 한 번 정하고 Theme가 읽는다. 바꾸면 다시 켜야 보인다.
// 스타일은 바탕·글자·선 색, 모서리, 테두리, 그림자를 바꾸고, 색감은 바탕과 열두 색에 고른 색을 조금 섞는다.
// 기본 스타일(색종이)에 기본 색감이면 아무것도 바꾸지 않는다(DESIGN.md 그대로).
// 어느 스타일·색감이든 진한 칠 위 잉크 글자는 6:1, 옅은 칠·흰 면 위 깊은 색과 도움말 글자는 4.5:1 이상이 되게 다시 맞춘다.
// 색감 열 가지의 이름과 값은 쌤탕화면(moonsugugu/ssamtang)과 같다.
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Bakkum
{
    public sealed class UiStyle
    {
        public string Id, Name, Desc;
        public uint Desk, Sheet, Sheet2, Ink, Ink2, Ink3, Line, Line2, Muted;
        public double Radius = 1;                        // 모서리 배율
        public double Outline;                           // 칸·카드·단추의 잉크색 테두리 두께(0이면 지금처럼)
        public bool Hard;                                // 흐림 없이 오른쪽 아래로 미는 그림자
        public double Blur = 1, Depth = 1, Opacity = 1;  // 부드러운 그림자 배율
        public double Saturation = 1;                    // 열두 색의 채도 배율
        public double Pastel;                            // 열두 색을 흰색 쪽으로(0~1)
        public uint Warm;                                // 열두 색에 조금 섞을 색
        public double WarmBy;
        public uint Dots;                                // 책상에 찍는 점 색(0이면 없음)
    }

    public sealed class UiMood
    {
        public string Id, Name;
        public double H, S;   // 색상 각도, 채도(0~100)

        public Color Color
        {
            get { return Look.FromHsl(H, S / 100, 0.55); }
        }
    }

    /// 한 스타일·색감에서 쓰는 바탕색 묶음
    public sealed class Palette
    {
        public UiStyle Style;
        public UiMood Mood;
        public Color Desk, Sheet, Sheet2, Ink, Ink2, Ink3, Line, Line2, Muted, Focus;

        public bool Plain
        {
            get { return Style == Look.Styles[0] && Mood == null; }
        }
    }

    public static class Look
    {
        // 색종이 원래 값(DESIGN.md). 다른 스타일의 회색은 이 값에서 책상과 잉크 사이 어디쯤인지로 옮긴다.
        const uint BaseDesk = 0xF6F3EE, BaseInk = 0x22252F;

        public static readonly UiStyle[] Styles =
        {
            new UiStyle
            {
                Id = "paper", Name = "색종이", Desc = "책상 위 알록달록한 색종이. 처음 모양 그대로예요.",
                Desk = 0xF6F3EE, Sheet = 0xFFFFFF, Sheet2 = 0xFAF7F2, Ink = 0x22252F, Ink2 = 0x4B5160, Ink3 = 0x696F7D, Line = 0xECE7DF, Line2 = 0xDDD7CD, Muted = 0xEFECE6
            },
            new UiStyle
            {
                Id = "cartoon", Name = "카툰풍", Desc = "굵은 테두리와 또렷한 그림자, 점무늬 하늘 바탕. 만화 말풍선 느낌이에요.",
                Desk = 0xE8F4FD, Sheet = 0xFFFFFF, Sheet2 = 0xF3F9FE, Ink = 0x1E2330, Ink2 = 0x3D4456, Ink3 = 0x58607A, Line = 0xD5E5F2, Line2 = 0xBCD2E4, Muted = 0xDFEAF3,
                Radius = 1.3, Outline = 2, Hard = true, Saturation = 1.12, Dots = 0xCDE3F6
            },
            new UiStyle
            {
                Id = "clay", Name = "클레이모피즘", Desc = "말랑한 점토처럼 두툼하고 둥근 칸, 부드러운 파스텔 색이에요.",
                Desk = 0xF2ECFA, Sheet = 0xFFFFFF, Sheet2 = 0xF8F4FD, Ink = 0x2A2440, Ink2 = 0x4B4463, Ink3 = 0x655E80, Line = 0xE7DFF3, Line2 = 0xD6CBE9, Muted = 0xEAE3F5,
                Radius = 1.45, Blur = 1.5, Depth = 1.3, Opacity = 1.25, Pastel = 0.22
            },
            new UiStyle
            {
                Id = "clean", Name = "깔끔", Desc = "흰 면과 얇은 선, 옅은 그림자. 군더더기 없이 단정해요.",
                Desk = 0xF5F5F7, Sheet = 0xFFFFFF, Sheet2 = 0xFAFAFC, Ink = 0x1D1D1F, Ink2 = 0x424245, Ink3 = 0x6E6E73, Line = 0xE8E8ED, Line2 = 0xD2D2D7, Muted = 0xEDEDF0,
                Radius = 0.8, Blur = 0.7, Depth = 0.6, Opacity = 0.7, Saturation = 0.85
            },
            new UiStyle
            {
                Id = "hanji", Name = "한지", Desc = "한지 빛 바탕에 먹색 글씨, 차분한 전통 색이에요.",
                Desk = 0xF2EADA, Sheet = 0xFFFDF7, Sheet2 = 0xFAF4E8, Ink = 0x2B241C, Ink2 = 0x4F4537, Ink3 = 0x6B5F4E, Line = 0xE6DBC6, Line2 = 0xD6C8AE, Muted = 0xEBE2D0,
                Radius = 0.9, Saturation = 0.78, Warm = 0xC9A36B, WarmBy = 0.12
            },
            new UiStyle
            {
                Id = "retro", Name = "레트로", Desc = "크림색 바탕에 갈색 테두리, 주황빛이 도는 복고풍이에요.",
                Desk = 0xFBEFD8, Sheet = 0xFFF9EE, Sheet2 = 0xFBF2E0, Ink = 0x3B2A1F, Ink2 = 0x5B4535, Ink3 = 0x765F4C, Line = 0xEFDDBF, Line2 = 0xE2C9A2, Muted = 0xF1E3C8,
                Radius = 1.15, Outline = 1.5, Warm = 0xF0A050, WarmBy = 0.15
            }
        };

        public static readonly UiMood[] Moods =
        {
            new UiMood { Id = "sky", Name = "맑은 하늘", H = 210, S = 72 },
            new UiMood { Id = "mint", Name = "산뜻 민트", H = 165, S = 55 },
            new UiMood { Id = "forest", Name = "초록 숲", H = 140, S = 45 },
            new UiMood { Id = "lavender", Name = "라벤더", H = 270, S = 60 },
            new UiMood { Id = "rose", Name = "장미 분홍", H = 335, S = 62 },
            new UiMood { Id = "peach", Name = "복숭아", H = 18, S = 72 },
            new UiMood { Id = "sunshine", Name = "햇살 노랑", H = 44, S = 78 },
            new UiMood { Id = "ocean", Name = "청록 바다", H = 190, S = 66 },
            new UiMood { Id = "mocha", Name = "따뜻 모카", H = 28, S = 32 },
            new UiMood { Id = "mono", Name = "차분 회색", H = 220, S = 0 }
        };

        public static UiStyle StyleOf(string id)
        {
            foreach (var s in Styles) if (s.Id == id) return s;
            return Styles[0];
        }

        /// 없거나 모르는 이름이면 null(기본 색감)
        public static UiMood MoodOf(string id)
        {
            foreach (var m in Moods) if (m.Id == id) return m;
            return null;
        }

        /// 이번에 켤 때 정한 묶음. 설정에서 바꿔도 다시 켤 때까지 그대로다.
        public static readonly Palette Current = Make(StyleOf(AppSettings.Current.UiStyle), MoodOf(AppSettings.Current.UiMood));

        public static UiStyle Style
        {
            get { return Current.Style; }
        }

        /// 설정에 고른 것이 지금 화면과 다른지(다시 켜야 보이는지)
        public static bool Pending(AppSettings s)
        {
            return StyleOf(s.UiStyle) != Current.Style || MoodOf(s.UiMood) != Current.Mood;
        }

        public static Palette Make(UiStyle st, UiMood mood)
        {
            var p = new Palette
            {
                Style = st,
                Mood = mood,
                Desk = Theme.Hex(st.Desk),
                Sheet = Theme.Hex(st.Sheet),
                Sheet2 = Theme.Hex(st.Sheet2),
                Ink = Theme.Hex(st.Ink),
                Ink2 = Theme.Hex(st.Ink2),
                Ink3 = Theme.Hex(st.Ink3),
                Line = Theme.Hex(st.Line),
                Line2 = Theme.Hex(st.Line2),
                Muted = Theme.Hex(st.Muted),
                Focus = Theme.Hex(0x3D7BFF)
            };
            if (p.Plain) return p;
            if (mood != null)
            {
                Color m = mood.Color;
                p.Desk = Mix(p.Desk, m, 0.10);
                p.Sheet2 = Mix(p.Sheet2, m, 0.05);
                p.Line = Mix(p.Line, m, 0.12);
                p.Line2 = Mix(p.Line2, m, 0.14);
                p.Muted = Mix(p.Muted, m, 0.10);
            }
            // 도움말 글자는 흰 면과 책상 어디에 놓여도 읽히게
            p.Ink2 = Toward(p.Ink2, p.Ink, 7, p.Sheet, p.Desk);
            p.Ink3 = Toward(p.Ink3, p.Ink, 4.6, p.Sheet, p.Sheet2, p.Desk);
            return p;
        }

        // ── 열두 색 ──

        /// 색종이 한 장(진한 칠·옅은 칠·깊은 색)을 이 스타일·색감에 맞게 옮긴다.
        public static Hue HueFor(Palette p, string name, uint vivid, uint tint, uint deep)
        {
            Color v = Theme.Hex(vivid), t = Theme.Hex(tint), d = Theme.Hex(deep);
            if (p.Plain) return new Hue(name, v, t, d);
            var st = p.Style;
            if (st.Saturation != 1)
            {
                v = Saturate(v, st.Saturation);
                t = Saturate(t, st.Saturation);
                d = Saturate(d, st.Saturation);
            }
            if (st.WarmBy > 0)
            {
                Color w = Theme.Hex(st.Warm);
                v = Mix(v, w, st.WarmBy);
                t = Mix(t, w, st.WarmBy * 0.5);
                d = Mix(d, w, st.WarmBy * 0.4);
            }
            if (st.Pastel > 0)
            {
                v = Mix(v, Colors.White, st.Pastel);
                t = Mix(t, Colors.White, st.Pastel * 0.5);
            }
            if (p.Mood != null)
            {
                Color m = p.Mood.Color;
                v = Mix(v, m, 0.2);
                t = Mix(t, Mix(m, Colors.White, 0.85), 0.35);
                d = Mix(d, Mix(m, Colors.Black, 0.45), 0.2);
            }
            v = Toward(v, Colors.White, 6, p.Ink);
            d = Toward(d, p.Ink, 4.8, t, p.Sheet, p.Sheet2);
            return new Hue(name, v, t, d);
        }

        // ── 그림자·모서리·테두리 ──

        public static double ShadowDirection
        {
            get { return Style.Hard ? 315 : 270; }
        }

        public static void Shade(ref double blur, ref double depth, ref double opacity)
        {
            Shade(Style, ref blur, ref depth, ref opacity);
        }

        /// 그림자 값(색종이 기준)을 이 스타일에 맞게 바꾼다. 딱딱한 그림자는 흐림 없이 짧게 민다.
        public static void Shade(UiStyle st, ref double blur, ref double depth, ref double opacity)
        {
            if (st.Hard)
            {
                blur = 0;
                depth = Math.Max(2, Math.Min(6, Math.Round(depth * 0.6 + 1)));
                opacity = Math.Min(0.9, opacity * 6);
                return;
            }
            blur *= st.Blur;
            depth *= st.Depth;
            opacity = Math.Min(1, opacity * st.Opacity);
        }

        public static DropShadowEffect Shadow(Color color, double blur, double depth, double opacity)
        {
            Shade(ref blur, ref depth, ref opacity);
            return new DropShadowEffect { Color = color, BlurRadius = blur, ShadowDepth = depth, Direction = ShadowDirection, Opacity = opacity };
        }

        public static double R(double r)
        {
            return R(Style, r);
        }

        static double R(UiStyle st, double r)
        {
            return Math.Round(r * st.Radius, 1);
        }

        public static double Outline
        {
            get { return Style.Outline; }
        }

        /// 색종이의 회색을 이 스타일의 회색으로(책상에서 잉크 쪽으로 같은 만큼)
        public static Color Neutral(Color c)
        {
            return Neutral(Current, c);
        }

        static Color Neutral(Palette p, Color c)
        {
            if (p.Plain) return c;
            double desk = Avg(Theme.Hex(BaseDesk)), ink = Avg(Theme.Hex(BaseInk));
            double at = Math.Max(0, Math.Min(1, (desk - Avg(c)) / (desk - ink)));
            return Mix(p.Desk, p.Ink, at);
        }

        public static Brush DeskBrush()
        {
            var p = Current;
            if (p.Style.Dots == 0) return Theme.Frozen(p.Desk);
            Color dot = p.Mood == null ? Theme.Hex(p.Style.Dots) : Mix(Theme.Hex(p.Style.Dots), p.Mood.Color, 0.15);
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Theme.Frozen(p.Desk), null, new RectangleGeometry(new Rect(0, 0, 18, 18))));
            g.Children.Add(new GeometryDrawing(Theme.Frozen(dot), null, new EllipseGeometry(new Point(5, 5), 1.8, 1.8)));
            g.Children.Add(new GeometryDrawing(Theme.Frozen(dot), null, new EllipseGeometry(new Point(14, 14), 1.8, 1.8)));
            var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 18, 18), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
            b.Freeze();
            return b;
        }

        // ── 앱 전체 꾸밈(XAML)을 이 스타일로 ──

        public static string Restyle(string xaml, Palette p)
        {
            if (p.Plain) return xaml;
            var st = p.Style;
            xaml = Regex.Replace(xaml, "#([0-9a-fA-F]{6})\\b", m => "#" + ToHex(XamlColor(p, Theme.Hex(uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber)))));
            xaml = Regex.Replace(xaml, "CornerRadius='([0-9.]+)'", m => "CornerRadius='" + Num(R(st, Parse(m.Groups[1].Value))) + "'");
            if (st.Outline > 0) xaml = xaml.Replace("BorderThickness='1.5'", "BorderThickness='" + Num(st.Outline) + "'");
            xaml = Regex.Replace(xaml, "BlurRadius='([0-9.]+)' ShadowDepth='([0-9.]+)' Direction='270' Opacity='([0-9.]+)'", m =>
            {
                double blur = Parse(m.Groups[1].Value), depth = Parse(m.Groups[2].Value), op = Parse(m.Groups[3].Value);
                Shade(st, ref blur, ref depth, ref op);
                return "BlurRadius='" + Num(blur) + "' ShadowDepth='" + Num(depth) + "' Direction='" + (st.Hard ? "315" : "270") + "' Opacity='" + Num(op) + "'";
            });
            return xaml;
        }

        static Color XamlColor(Palette p, Color c)
        {
            uint rgb = (uint)(c.R << 16 | c.G << 8 | c.B);
            switch (rgb)
            {
                case 0x22252F: return p.Ink;
                case 0x4B5160: return p.Ink2;
                case 0xFFFFFF: return p.Sheet;
                case 0xFAF7F2: return p.Sheet2;
                case 0x3D7BFF: return p.Focus;
                case 0x52D3A0: return HueFor(p, "green", 0x52D3A0, 0xDDF7EC, 0x13795A).Vivid;   // 켠 스위치
                case 0x2259B8: return HueFor(p, "blue", 0x6CA8FF, 0xE3EEFF, 0x2259B8).Deep;     // 링크
                case 0x3A3E4C: return Mix(p.Ink, p.Sheet, 0.12);   // 진한 단추에 마우스
                case 0xDDD7CD:
                case 0xE3DDD3:
                case 0xCFC8BC:
                case 0xBFB8AC:
                case 0xD4CDC1:
                    // 입력칸·선택지 테두리: 테두리를 굵게 쓰는 스타일에서는 잉크색
                    if (p.Style.Outline > 0) return p.Ink;
                    break;
            }
            return Neutral(p, c);
        }

        // ── 색 계산 ──

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb(Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));
        }

        static byte Lerp(byte a, byte b, double t)
        {
            return (byte)Math.Round(a + (b - a) * t);
        }

        static double Avg(Color c)
        {
            return (c.R + c.G + c.B) / 3.0;
        }

        static double Channel(byte v)
        {
            double c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        public static double Luminance(Color c)
        {
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        /// 두 색의 명도 대비(WCAG, 1~21)
        public static double Contrast(Color a, Color b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        /// c를 target 쪽으로 조금씩 옮겨 모든 바탕과 대비가 min 이상이 되게 한다.
        static Color Toward(Color c, Color target, double min, params Color[] against)
        {
            for (int i = 0; i < 40; i++)
            {
                bool ok = true;
                foreach (var b in against) if (Contrast(c, b) < min) ok = false;
                if (ok) return c;
                c = Mix(c, target, 0.08);
            }
            return c;
        }

        static Color Saturate(Color c, double k)
        {
            double h, s, l;
            ToHsl(c, out h, out s, out l);
            return FromHsl(h, Math.Max(0, Math.Min(1, s * k)), l);
        }

        static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;
            h = 0;
            s = 0;
            if (max == min) return;
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }

        public static Color FromHsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double hp = ((h % 360) + 360) % 360 / 60;
            double x = c * (1 - Math.Abs(hp % 2 - 1));
            double r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }
            double m = l - c / 2;
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        static string ToHex(Color c)
        {
            return c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
        }

        static double Parse(string s)
        {
            return double.Parse(s, CultureInfo.InvariantCulture);
        }

        static string Num(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // ── 설정 창 미리 보기 ──

        /// 스타일 하나를 작은 창 그림으로(지금 켠 스타일과 상관없이 그 스타일 값으로 그린다)
        public static FrameworkElement Preview(UiStyle st, UiMood mood)
        {
            var p = Make(st, mood);
            Func<double, double> r = x => Math.Round(x * st.Radius, 1);
            Brush desk = Theme.Frozen(p.Desk);
            if (st.Dots != 0)
            {
                var g = new DrawingGroup();
                g.Children.Add(new GeometryDrawing(Theme.Frozen(p.Desk), null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
                g.Children.Add(new GeometryDrawing(Theme.Frozen(Theme.Hex(st.Dots)), null, new EllipseGeometry(new Point(4, 4), 1.2, 1.2)));
                desk = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 12, 12), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
            }
            var hues = new[]
            {
                HueFor(p, "coral", 0xFF8473, 0xFFE8E3, 0xB53A28),
                HueFor(p, "green", 0x52D3A0, 0xDDF7EC, 0x13795A),
                HueFor(p, "sky", 0x4FC3E8, 0xDFF5FC, 0x0E6F8C)
            };
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var h in hues)
            {
                chips.Children.Add(new Border
                {
                    Width = 26,
                    Height = 18,
                    Margin = new Thickness(0, 0, 5, 0),
                    CornerRadius = new CornerRadius(r(6)),
                    Background = Theme.Frozen(h.Vivid),
                    BorderBrush = Theme.Frozen(p.Ink),
                    BorderThickness = new Thickness(st.Outline > 0 ? 1.4 : 0)
                });
            }
            var lines = new StackPanel();
            lines.Children.Add(new Border { Height = 6, Width = 64, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Background = Theme.Frozen(p.Ink) });
            lines.Children.Add(new Border { Height = 5, Width = 44, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 5, 0, 0), Background = Theme.Frozen(p.Ink3) });
            lines.Children.Add(chips);
            double blur = 16, depth = 4, op = 0.12;
            if (st.Hard)
            {
                blur = 0;
                depth = 3;
                op = 0.85;
            }
            else
            {
                blur *= st.Blur;
                depth *= st.Depth;
                op = Math.Min(1, op * st.Opacity);
            }
            var card = new Border
            {
                Background = Theme.Frozen(p.Sheet),
                CornerRadius = new CornerRadius(r(12)),
                BorderBrush = st.Outline > 0 ? Theme.Frozen(p.Ink) : Theme.Frozen(p.Line),
                BorderThickness = new Thickness(st.Outline > 0 ? 1.6 : 1),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(12, 12, 14, 14),
                Effect = new DropShadowEffect { Color = p.Ink, BlurRadius = blur, ShadowDepth = depth, Direction = st.Hard ? 315 : 270, Opacity = op },
                Child = lines
            };
            return new Border { Background = desk, CornerRadius = new CornerRadius(12), Height = 96, Child = card, ClipToBounds = true };
        }
    }
}
