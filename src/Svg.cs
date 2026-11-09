// SVG 그림을 PNG로 그린다. 파일 하나로 끝나는 그림만 다룬다.
// 스크립트와 바깥 파일·인터넷 주소는 읽지 않는다(그림 안에 넣은 data: 그림만 쓴다).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;

namespace Bakkum
{
    public static class SvgImage
    {
        const string Ns = "http://www.w3.org/2000/svg";
        const int LongSide = 2048, MaxSide = 6000, MaxElements = 200000, MaxDepth = 64;
        const long MaxPixels = 40000000;
        const long MaxFile = 32L * 1024 * 1024;

        public static byte[] Png(string path)
        {
            // 그리는 부품은 STA 스레드에서만 돈다.
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return Draw(path);
            byte[] result = null;
            Exception error = null;
            var t = new Thread(delegate ()
            {
                try { result = Draw(path); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (error is UserFacingException) throw error;
            if (error != null) throw new UserFacingException("SVG 그림을 그리지 못했어요.");
            return result;
        }

        static byte[] Draw(string path)
        {
            Util.EnsureReadable(path);
            if (new FileInfo(path).Length > MaxFile) throw new UserFacingException("SVG 파일이 너무 커요.");
            XmlDocument doc;
            try
            {
                doc = new XmlDocument { XmlResolver = null };
                // 일러스트레이터는 문서 안 약어(ENTITY)를 쓴다. 바깥 파일은 XmlResolver가 없어 읽지 않고, 부풀리기는 글자 수 한도로 막는다.
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null, MaxCharactersFromEntities = 65536 };
                using (var r = XmlReader.Create(path, settings)) doc.Load(r);
            }
            catch (XmlException)
            {
                throw new UserFacingException("SVG 파일을 읽지 못했어요. 형식이 맞지 않는 파일일 수 있어요.");
            }
            var root = doc.DocumentElement;
            if (root == null || root.LocalName != "svg") throw new UserFacingException("SVG 그림이 아니에요.");
            return new Painter(root).Render();
        }

        static bool IsSvg(XmlElement e)
        {
            return e.NamespaceURI == Ns || e.NamespaceURI.Length == 0;
        }

        static double Num(string s, double fallback)
        {
            double v;
            return double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : fallback;
        }

        sealed class Paint
        {
            public bool None;
            public bool Current;
            public Color Color = Colors.Black;
            public string Ref;
            public Paint Fallback;
        }

        sealed class Style
        {
            public Paint Fill = new Paint();
            public Paint Stroke = new Paint { None = true };
            public double FillOpacity = 1, StrokeOpacity = 1, StrokeWidth = 1, Miter = 4, DashOffset, FontSize = 16;
            public string Cap = "butt", Join = "miter", Family = "", Anchor = "start", FillRule = "nonzero", ClipRule = "nonzero";
            public double[] Dash;
            public bool Bold, Italic, Hidden;
            public Color CurrentColor = Colors.Black;

            public Style Copy()
            {
                return (Style)MemberwiseClone();
            }
        }

        sealed class Rule
        {
            public string Selector;
            public int Weight;
            public int Order;
            public Dictionary<string, string> Props;
        }

        sealed class Painter
        {
            readonly XmlElement root;
            readonly Dictionary<string, XmlElement> ids = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            readonly List<Rule> rules = new List<Rule>();
            readonly HashSet<string> using_ = new HashSet<string>(StringComparer.Ordinal);
            DrawingContext dc;
            double vw = 300, vh = 150;
            int count;

            public Painter(XmlElement r)
            {
                root = r;
                Index(r, 0);
            }

            void Index(XmlElement e, int depth)
            {
                if (depth > MaxDepth * 4) throw new UserFacingException("SVG 그림이 너무 복잡해요.");
                string id = e.GetAttribute("id");
                if (id.Length > 0 && !ids.ContainsKey(id)) ids[id] = e;
                if (e.LocalName == "style" && IsSvg(e)) Sheet(e.InnerText);
                foreach (XmlNode n in e.ChildNodes)
                {
                    var c = n as XmlElement;
                    if (c != null) Index(c, depth + 1);
                }
            }

            void Sheet(string css)
            {
                css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
                foreach (Match m in Regex.Matches(css, @"([^{}@]+)\{([^{}]*)\}"))
                {
                    var props = Declarations(m.Groups[2].Value);
                    foreach (string sel in m.Groups[1].Value.Split(','))
                    {
                        string s = sel.Trim();
                        if (s.Length == 0) continue;
                        // 뒤쪽 조각만 본다(자손 선택은 맨 끝 조각으로 어림한다).
                        int sp = s.LastIndexOfAny(new[] { ' ', '>' });
                        if (sp >= 0) s = s.Substring(sp + 1);
                        int weight = s.StartsWith("#", StringComparison.Ordinal) ? 3 : s.Contains(".") ? 2 : s == "*" ? 0 : 1;
                        rules.Add(new Rule { Selector = s, Weight = weight, Order = rules.Count, Props = props });
                    }
                }
            }

            static Dictionary<string, string> Declarations(string text)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in text.Split(';'))
                {
                    int c = part.IndexOf(':');
                    if (c <= 0) continue;
                    string value = part.Substring(c + 1).Replace("!important", "").Trim();
                    map[part.Substring(0, c).Trim()] = value;
                }
                return map;
            }

            static bool Matches(XmlElement e, string sel)
            {
                if (sel == "*") return true;
                if (sel.StartsWith("#", StringComparison.Ordinal)) return e.GetAttribute("id") == sel.Substring(1);
                int dot = sel.IndexOf('.');
                string tag = dot >= 0 ? sel.Substring(0, dot) : sel;
                if (tag.Length > 0 && !string.Equals(tag, e.LocalName, StringComparison.OrdinalIgnoreCase)) return false;
                if (dot < 0) return true;
                var want = sel.Substring(dot + 1).Split('.');
                var have = e.GetAttribute("class").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                return want.All(w => have.Contains(w));
            }

            /// 표현 속성 < 스타일시트 < style 속성 순으로 덮어쓴 값
            Dictionary<string, string> PropsOf(XmlElement e)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlAttribute a in e.Attributes) map[a.LocalName] = a.Value;
                foreach (var r in rules.Where(x => Matches(e, x.Selector)).OrderBy(x => x.Weight).ThenBy(x => x.Order))
                {
                    foreach (var kv in r.Props) map[kv.Key] = kv.Value;
                }
                string inline = e.GetAttribute("style");
                if (inline.Length > 0)
                {
                    foreach (var kv in Declarations(inline)) map[kv.Key] = kv.Value;
                }
                return map;
            }

            // ── 값 읽기 ──

            double Len(string s, double fallback, double percentOf, double em)
            {
                if (string.IsNullOrWhiteSpace(s)) return fallback;
                var m = Regex.Match(s.Trim(), @"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([a-zA-Z%]*)$");
                if (!m.Success) return fallback;
                double v = Num(m.Groups[1].Value, fallback);
                switch (m.Groups[2].Value.ToLowerInvariant())
                {
                    case "pt": return v * 96 / 72;
                    case "pc": return v * 16;
                    case "mm": return v * 96 / 25.4;
                    case "cm": return v * 96 / 2.54;
                    case "in": return v * 96;
                    case "em": return v * em;
                    case "ex": return v * em * 0.5;
                    case "%": return v * percentOf / 100;
                }
                return v;
            }

            static byte Byte255(string s)
            {
                string t = s.Trim();
                double v = t.EndsWith("%", StringComparison.Ordinal) ? Num(t.TrimEnd('%'), 0) * 2.55 : Num(t, 0);
                return (byte)Math.Max(0, Math.Min(255, Math.Round(v)));
            }

            static bool TryColor(string text, out Color c)
            {
                c = Colors.Black;
                string s = text.Trim();
                if (s.StartsWith("#", StringComparison.Ordinal))
                {
                    string h = s.Substring(1);
                    if (!h.All(Uri.IsHexDigit)) return false;
                    if (h.Length == 3 || h.Length == 4) h = string.Concat(h.Select(x => new string(x, 2)));
                    if (h.Length != 6 && h.Length != 8) return false;
                    byte a = h.Length == 8 ? Convert.ToByte(h.Substring(6, 2), 16) : (byte)255;
                    c = Color.FromArgb(a, Convert.ToByte(h.Substring(0, 2), 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16));
                    return true;
                }
                var m = Regex.Match(s, @"^(rgba?|hsla?)\(([^)]*)\)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var p = m.Groups[2].Value.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 3) return false;
                    byte a = 255;
                    if (p.Length > 3)
                    {
                        double al = p[3].EndsWith("%", StringComparison.Ordinal) ? Num(p[3].TrimEnd('%'), 100) / 100 : Num(p[3], 1);
                        a = (byte)Math.Max(0, Math.Min(255, Math.Round(al * 255)));
                    }
                    if (m.Groups[1].Value.StartsWith("r", StringComparison.OrdinalIgnoreCase))
                    {
                        c = Color.FromArgb(a, Byte255(p[0]), Byte255(p[1]), Byte255(p[2]));
                        return true;
                    }
                    double hue = ((Num(p[0].Replace("deg", ""), 0) % 360) + 360) % 360, sat = Num(p[1].TrimEnd('%'), 0) / 100, li = Num(p[2].TrimEnd('%'), 0) / 100;
                    double q = (1 - Math.Abs(2 * li - 1)) * sat, x2 = q * (1 - Math.Abs((hue / 60) % 2 - 1)), mm = li - q / 2;
                    double r = 0, g = 0, b = 0;
                    switch ((int)(hue / 60))
                    {
                        case 0: r = q; g = x2; break;
                        case 1: r = x2; g = q; break;
                        case 2: g = q; b = x2; break;
                        case 3: g = x2; b = q; break;
                        case 4: r = x2; b = q; break;
                        default: r = q; b = x2; break;
                    }
                    c = Color.FromArgb(a, (byte)Math.Round((r + mm) * 255), (byte)Math.Round((g + mm) * 255), (byte)Math.Round((b + mm) * 255));
                    return true;
                }
                try
                {
                    var o = ColorConverter.ConvertFromString(s);
                    if (o == null) return false;
                    c = (Color)o;
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            }

            static Paint ParsePaint(string text, Paint keep)
            {
                string s = text.Trim();
                if (s.Length == 0 || s == "inherit") return keep;
                if (s == "none" || s == "transparent") return new Paint { None = true };
                if (s.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return new Paint { Current = true };
                var m = Regex.Match(s, @"^url\(\s*['""]?#([^'"")\s]+)['""]?\s*\)\s*(.*)$");
                if (m.Success) return new Paint { Ref = m.Groups[1].Value, Fallback = m.Groups[2].Length > 0 ? ParsePaint(m.Groups[2].Value, new Paint { None = true }) : new Paint { None = true } };
                Color c;
                if (TryColor(s, out c)) return new Paint { Color = c };
                return keep;
            }

            Style Apply(Style parent, Dictionary<string, string> p)
            {
                var s = parent.Copy();
                string v;
                if (p.TryGetValue("color", out v))
                {
                    Color c;
                    if (TryColor(v, out c)) s.CurrentColor = c;
                }
                if (p.TryGetValue("fill", out v)) s.Fill = ParsePaint(v, s.Fill);
                if (p.TryGetValue("stroke", out v)) s.Stroke = ParsePaint(v, s.Stroke);
                if (p.TryGetValue("fill-opacity", out v)) s.FillOpacity = Clamp01(v, s.FillOpacity);
                if (p.TryGetValue("stroke-opacity", out v)) s.StrokeOpacity = Clamp01(v, s.StrokeOpacity);
                if (p.TryGetValue("font-size", out v)) s.FontSize = Math.Max(0.1, Len(v, s.FontSize, parent.FontSize, parent.FontSize));
                if (p.TryGetValue("stroke-width", out v)) s.StrokeWidth = Math.Max(0, Len(v, s.StrokeWidth, Math.Sqrt((vw * vw + vh * vh) / 2), s.FontSize));
                if (p.TryGetValue("stroke-miterlimit", out v)) s.Miter = Math.Max(1, Num(v, s.Miter));
                if (p.TryGetValue("stroke-linecap", out v)) s.Cap = v.Trim().ToLowerInvariant();
                if (p.TryGetValue("stroke-linejoin", out v)) s.Join = v.Trim().ToLowerInvariant();
                if (p.TryGetValue("fill-rule", out v)) s.FillRule = v.Trim().ToLowerInvariant();
                if (p.TryGetValue("clip-rule", out v)) s.ClipRule = v.Trim().ToLowerInvariant();
                if (p.TryGetValue("stroke-dashoffset", out v)) s.DashOffset = Len(v, s.DashOffset, vw, s.FontSize);
                if (p.TryGetValue("stroke-dasharray", out v))
                {
                    if (v.Trim() == "none") s.Dash = null;
                    else if (v.Trim() != "inherit")
                    {
                        var a = v.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Math.Max(0, Len(x, 0, vw, s.FontSize))).ToArray();
                        s.Dash = a.Length == 0 || a.All(x => x == 0) ? null : a;
                    }
                }
                if (p.TryGetValue("font-family", out v) && v != "inherit") s.Family = v;
                if (p.TryGetValue("font-weight", out v))
                {
                    string w = v.Trim().ToLowerInvariant();
                    s.Bold = w == "bold" || w == "bolder" || Num(w, 400) >= 600;
                }
                if (p.TryGetValue("font-style", out v)) s.Italic = v.Trim().ToLowerInvariant() != "normal";
                if (p.TryGetValue("text-anchor", out v)) s.Anchor = v.Trim().ToLowerInvariant();
                if (p.TryGetValue("visibility", out v)) s.Hidden = v.Trim() == "hidden" || v.Trim() == "collapse";
                return s;
            }

            static double Clamp01(string v, double fallback)
            {
                string t = v.Trim();
                double d = t.EndsWith("%", StringComparison.Ordinal) ? Num(t.TrimEnd('%'), fallback * 100) / 100 : Num(t, fallback);
                return Math.Max(0, Math.Min(1, d));
            }

            static Matrix ParseTransform(string text)
            {
                var result = Matrix.Identity;
                if (string.IsNullOrWhiteSpace(text)) return result;
                foreach (Match m in Regex.Matches(text, @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)"))
                {
                    var a = m.Groups[2].Value.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Num(x, 0)).ToArray();
                    var t = Matrix.Identity;
                    switch (m.Groups[1].Value)
                    {
                        case "matrix":
                            if (a.Length >= 6) t = new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]);
                            break;
                        case "translate":
                            if (a.Length >= 1) t.Translate(a[0], a.Length > 1 ? a[1] : 0);
                            break;
                        case "scale":
                            if (a.Length >= 1) t.Scale(a[0], a.Length > 1 ? a[1] : a[0]);
                            break;
                        case "rotate":
                            if (a.Length >= 3) t.RotateAt(a[0], a[1], a[2]);
                            else if (a.Length >= 1) t.Rotate(a[0]);
                            break;
                        case "skewX":
                            if (a.Length >= 1) t = new Matrix(1, 0, Math.Tan(a[0] * Math.PI / 180), 1, 0, 0);
                            break;
                        case "skewY":
                            if (a.Length >= 1) t = new Matrix(1, Math.Tan(a[0] * Math.PI / 180), 0, 1, 0, 0);
                            break;
                    }
                    result = t * result;
                }
                return result;
            }

            // ── 그리기 시작 ──

            void ViewBoxFit(string vb, string par, double x, double y, double w, double h, out Matrix m, out Rect clip)
            {
                m = Matrix.Identity;
                clip = new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
                m.Translate(x, y);
                var a = (vb ?? "").Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(s => Num(s, 0)).ToArray();
                if (a.Length != 4 || a[2] <= 0 || a[3] <= 0) return;
                double sx = w / a[2], sy = h / a[3];
                string mode = (par ?? "xMidYMid meet").Trim();
                if (mode.StartsWith("defer", StringComparison.Ordinal)) mode = mode.Substring(5).Trim();
                if (mode.StartsWith("none", StringComparison.Ordinal))
                {
                    m = Matrix.Identity;
                    m.Translate(-a[0], -a[1]);
                    m.Scale(sx, sy);
                    m.Translate(x, y);
                    return;
                }
                bool slice = mode.Contains("slice");
                double s2 = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
                double ox = w - a[2] * s2, oy = h - a[3] * s2;
                ox *= mode.Contains("xMin") ? 0 : mode.Contains("xMax") ? 1 : 0.5;
                oy *= mode.Contains("YMin") ? 0 : mode.Contains("YMax") ? 1 : 0.5;
                m = Matrix.Identity;
                m.Translate(-a[0], -a[1]);
                m.Scale(s2, s2);
                m.Translate(x + ox, y + oy);
            }

            public byte[] Render()
            {
                string vb = root.GetAttribute("viewBox");
                var a = vb.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(s => Num(s, 0)).ToArray();
                bool hasBox = a.Length == 4 && a[2] > 0 && a[3] > 0;
                string ws = root.GetAttribute("width"), hs = root.GetAttribute("height");
                bool pctW = ws.Trim().EndsWith("%", StringComparison.Ordinal), pctH = hs.Trim().EndsWith("%", StringComparison.Ordinal);
                double w = !pctW ? Len(ws, -1, 0, 16) : -1, h = !pctH ? Len(hs, -1, 0, 16) : -1;
                if (w <= 0 && h > 0 && hasBox) w = h * a[2] / a[3];
                if (h <= 0 && w > 0 && hasBox) h = w * a[3] / a[2];
                if (w <= 0) w = hasBox ? a[2] : 300;
                if (h <= 0) h = hasBox ? a[3] : 150;
                vw = hasBox ? a[2] : w;
                vh = hasBox ? a[3] : h;
                double big = Math.Max(w, h);
                double target = Math.Min(MaxSide, Math.Max(big, LongSide));
                double scale = target / big;
                if (w * scale * h * scale > MaxPixels) scale = Math.Sqrt(MaxPixels / (w * h));
                int pw = Math.Max(1, (int)Math.Round(w * scale)), ph = Math.Max(1, (int)Math.Round(h * scale));
                var visual = new DrawingVisual();
                using (dc = visual.RenderOpen())
                {
                    dc.PushClip(new RectangleGeometry(new Rect(0, 0, pw, ph)));
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    Matrix fit;
                    Rect clip;
                    ViewBoxFit(vb, root.GetAttribute("preserveAspectRatio").Length > 0 ? root.GetAttribute("preserveAspectRatio") : null, 0, 0, w, h, out fit, out clip);
                    dc.PushTransform(new MatrixTransform(fit));
                    var style = Apply(new Style(), PropsOf(root));
                    Children(root, style, 0);
                    dc.Pop();
                    dc.Pop();
                    dc.Pop();
                }
                var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(visual);
                bmp.Freeze();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var m = new MemoryStream())
                {
                    enc.Save(m);
                    return m.ToArray();
                }
            }

            void Children(XmlElement parent, Style style, int depth)
            {
                foreach (XmlNode n in parent.ChildNodes)
                {
                    var c = n as XmlElement;
                    if (c != null) Element(c, style, depth + 1);
                }
            }

            void Element(XmlElement e, Style parent, int depth)
            {
                if (!IsSvg(e)) return;
                if (depth > MaxDepth || ++count > MaxElements) throw new UserFacingException("SVG 그림이 너무 복잡해요.");
                switch (e.LocalName)
                {
                    case "defs":
                    case "symbol":
                    case "clipPath":
                    case "mask":
                    case "marker":
                    case "pattern":
                    case "linearGradient":
                    case "radialGradient":
                    case "filter":
                    case "style":
                    case "script":
                    case "title":
                    case "desc":
                    case "metadata":
                    case "foreignObject":
                    case "animate":
                    case "animateTransform":
                    case "animateMotion":
                    case "set":
                        return;
                }
                var p = PropsOf(e);
                string v;
                if (p.TryGetValue("display", out v) && v.Trim() == "none") return;
                var style = Apply(parent, p);
                int pushed = 0;
                if (p.TryGetValue("transform", out v))
                {
                    var t = ParseTransform(v);
                    if (!t.IsIdentity)
                    {
                        if (!t.HasInverse) return;
                        dc.PushTransform(new MatrixTransform(t));
                        pushed++;
                    }
                }
                if (p.TryGetValue("opacity", out v))
                {
                    double o = Clamp01(v, 1);
                    if (o < 1)
                    {
                        dc.PushOpacity(o);
                        pushed++;
                    }
                }
                Geometry clip = null;
                if (p.TryGetValue("clip-path", out v)) clip = ClipOf(v, depth);
                if (clip != null)
                {
                    dc.PushClip(clip);
                    pushed++;
                }
                try
                {
                    Draw(e, style, p, depth);
                }
                finally
                {
                    for (int i = 0; i < pushed; i++) dc.Pop();
                }
            }

            void Draw(XmlElement e, Style s, Dictionary<string, string> p, int depth)
            {
                switch (e.LocalName)
                {
                    case "svg":
                        NestedSvg(e, s, p, depth);
                        break;
                    case "g":
                    case "a":
                        Children(e, s, depth);
                        break;
                    case "switch":
                        foreach (XmlNode n in e.ChildNodes)
                        {
                            var c = n as XmlElement;
                            if (c == null || !IsSvg(c)) continue;
                            if (c.HasAttribute("requiredFeatures") || c.HasAttribute("requiredExtensions") || c.LocalName == "foreignObject") continue;
                            Element(c, s, depth + 1);
                            break;
                        }
                        break;
                    case "use":
                        Use(e, s, p, depth);
                        break;
                    case "text":
                        Text(e, s, p);
                        break;
                    case "image":
                        Image(e, p);
                        break;
                    default:
                        Shape(e, s, p);
                        break;
                }
            }

            void NestedSvg(XmlElement e, Style s, Dictionary<string, string> p, int depth)
            {
                double x = Attr(p, "x", 0, vw, s), y = Attr(p, "y", 0, vh, s), w = Attr(p, "width", vw, vw, s), h = Attr(p, "height", vh, vh, s);
                Matrix fit;
                Rect clip;
                string vb, par;
                p.TryGetValue("viewBox", out vb);
                p.TryGetValue("preserveAspectRatio", out par);
                ViewBoxFit(vb, par, x, y, w, h, out fit, out clip);
                dc.PushClip(new RectangleGeometry(clip));
                dc.PushTransform(new MatrixTransform(fit));
                Children(e, s, depth);
                dc.Pop();
                dc.Pop();
            }

            double Attr(Dictionary<string, string> p, string name, double fallback, double percentOf, Style s)
            {
                string v;
                return p.TryGetValue(name, out v) ? Len(v, fallback, percentOf, s.FontSize) : fallback;
            }

            static string Href(Dictionary<string, string> p)
            {
                string v;
                return p.TryGetValue("href", out v) ? v.Trim() : "";
            }

            void Use(XmlElement e, Style s, Dictionary<string, string> p, int depth)
            {
                string h = Href(p);
                if (!h.StartsWith("#", StringComparison.Ordinal)) return;
                string id = h.Substring(1);
                XmlElement target;
                if (!ids.TryGetValue(id, out target) || !using_.Add(id)) return;
                try
                {
                    double x = Attr(p, "x", 0, vw, s), y = Attr(p, "y", 0, vh, s);
                    dc.PushTransform(new TranslateTransform(x, y));
                    try
                    {
                        if (target.LocalName == "symbol")
                        {
                            var tp = PropsOf(target);
                            var st = Apply(s, tp);
                            double w = Attr(p, "width", vw, vw, s), h2 = Attr(p, "height", vh, vh, s);
                            Matrix fit;
                            Rect clip;
                            string vb, par;
                            tp.TryGetValue("viewBox", out vb);
                            tp.TryGetValue("preserveAspectRatio", out par);
                            ViewBoxFit(vb, par, 0, 0, w, h2, out fit, out clip);
                            dc.PushTransform(new MatrixTransform(fit));
                            Children(target, st, depth);
                            dc.Pop();
                        }
                        else Element(target, s, depth + 1);
                    }
                    finally
                    {
                        dc.Pop();
                    }
                }
                finally
                {
                    using_.Remove(id);
                }
            }

            // ── 모양 ──

            Geometry GeometryOf(XmlElement e, Style s, Dictionary<string, string> p)
            {
                switch (e.LocalName)
                {
                    case "rect":
                        {
                            double w = Attr(p, "width", 0, vw, s), h = Attr(p, "height", 0, vh, s);
                            if (w <= 0 || h <= 0) return null;
                            double x = Attr(p, "x", 0, vw, s), y = Attr(p, "y", 0, vh, s);
                            double rx = Attr(p, "rx", -1, vw, s), ry = Attr(p, "ry", -1, vh, s);
                            if (rx < 0) rx = Math.Max(0, ry);
                            if (ry < 0) ry = Math.Max(0, rx);
                            return new RectangleGeometry(new Rect(x, y, w, h), Math.Min(rx, w / 2), Math.Min(ry, h / 2));
                        }
                    case "circle":
                        {
                            double r = Attr(p, "r", 0, Math.Sqrt((vw * vw + vh * vh) / 2), s);
                            if (r <= 0) return null;
                            return new EllipseGeometry(new Point(Attr(p, "cx", 0, vw, s), Attr(p, "cy", 0, vh, s)), r, r);
                        }
                    case "ellipse":
                        {
                            double rx = Attr(p, "rx", 0, vw, s), ry = Attr(p, "ry", 0, vh, s);
                            if (rx <= 0 || ry <= 0) return null;
                            return new EllipseGeometry(new Point(Attr(p, "cx", 0, vw, s), Attr(p, "cy", 0, vh, s)), rx, ry);
                        }
                    case "line":
                        return new LineGeometry(new Point(Attr(p, "x1", 0, vw, s), Attr(p, "y1", 0, vh, s)), new Point(Attr(p, "x2", 0, vw, s), Attr(p, "y2", 0, vh, s)));
                    case "polyline":
                    case "polygon":
                        {
                            string v;
                            if (!p.TryGetValue("points", out v)) return null;
                            var n = Regex.Matches(v, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").Cast<Match>().Select(m => Num(m.Value, 0)).ToList();
                            if (n.Count < 4) return null;
                            var g = new StreamGeometry();
                            g.FillRule = s.FillRule == "evenodd" ? FillRule.EvenOdd : FillRule.Nonzero;
                            using (var c = g.Open())
                            {
                                c.BeginFigure(new Point(n[0], n[1]), true, e.LocalName == "polygon");
                                for (int i = 2; i + 1 < n.Count; i += 2) c.LineTo(new Point(n[i], n[i + 1]), true, true);
                            }
                            return g;
                        }
                    case "path":
                        {
                            string d;
                            if (!p.TryGetValue("d", out d)) return null;
                            return PathData.Parse(d, s.FillRule == "evenodd");
                        }
                }
                return null;
            }

            void Shape(XmlElement e, Style s, Dictionary<string, string> p)
            {
                if (s.Hidden) return;
                var g = GeometryOf(e, s, p);
                if (g == null) return;
                bool line = e.LocalName == "line";
                Brush fill = line ? null : BrushFor(s.Fill, s.FillOpacity, s, g);
                Pen pen = PenFor(s, g);
                if (fill == null && pen == null) return;
                dc.DrawGeometry(fill, pen, g);
            }

            Pen PenFor(Style s, Geometry g)
            {
                if (s.StrokeWidth <= 0) return null;
                var b = BrushFor(s.Stroke, s.StrokeOpacity, s, g);
                if (b == null) return null;
                var cap = s.Cap == "round" ? PenLineCap.Round : s.Cap == "square" ? PenLineCap.Square : PenLineCap.Flat;
                var pen = new Pen(b, s.StrokeWidth)
                {
                    StartLineCap = cap,
                    EndLineCap = cap,
                    DashCap = cap,
                    LineJoin = s.Join == "round" ? PenLineJoin.Round : s.Join == "bevel" ? PenLineJoin.Bevel : PenLineJoin.Miter,
                    MiterLimit = s.Miter
                };
                if (s.Dash != null)
                {
                    var a = s.Dash.Length % 2 == 1 ? s.Dash.Concat(s.Dash).ToArray() : s.Dash;
                    pen.DashStyle = new DashStyle(a.Select(x => x / s.StrokeWidth), s.DashOffset / s.StrokeWidth);
                }
                return pen;
            }

            Brush BrushFor(Paint p, double opacity, Style s, Geometry shape)
            {
                if (p.None || opacity <= 0) return null;
                if (p.Ref != null)
                {
                    XmlElement g;
                    if (ids.TryGetValue(p.Ref, out g) && (g.LocalName == "linearGradient" || g.LocalName == "radialGradient"))
                    {
                        var b = Gradient(g, s);
                        if (b != null)
                        {
                            b.Opacity = opacity;
                            return b;
                        }
                    }
                    return BrushFor(p.Fallback ?? new Paint { None = true }, opacity, s, shape);
                }
                var c = p.Current ? s.CurrentColor : p.Color;
                return new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * opacity), c.R, c.G, c.B));
            }

            string GradAttr(XmlElement g, string name, int guard = 0)
            {
                if (g.HasAttribute(name)) return g.GetAttribute(name);
                var linked = Linked(g);
                return linked != null && guard < 16 ? GradAttr(linked, name, guard + 1) : "";
            }

            XmlElement Linked(XmlElement g)
            {
                string h = g.GetAttribute("href");
                if (h.Length == 0) h = g.GetAttribute("href", "http://www.w3.org/1999/xlink");
                if (!h.StartsWith("#", StringComparison.Ordinal)) return null;
                XmlElement t;
                return ids.TryGetValue(h.Substring(1), out t) ? t : null;
            }

            List<GradientStop> Stops(XmlElement g, Style s)
            {
                XmlElement at = g;
                for (int i = 0; i < 16 && at != null; i++)
                {
                    var list = new List<GradientStop>();
                    foreach (var c in at.ChildNodes.OfType<XmlElement>().Where(x => x.LocalName == "stop"))
                    {
                        var p = PropsOf(c);
                        string v;
                        Color col = Colors.Black;
                        if (p.TryGetValue("stop-color", out v))
                        {
                            if (v.Trim().Equals("currentColor", StringComparison.OrdinalIgnoreCase)) col = s.CurrentColor;
                            else TryColor(v, out col);
                        }
                        double a = p.TryGetValue("stop-opacity", out v) ? Clamp01(v, 1) : 1;
                        double off = p.TryGetValue("offset", out v) ? Clamp01(v, 0) : 0;
                        if (list.Count > 0) off = Math.Max(off, list[list.Count - 1].Offset);
                        list.Add(new GradientStop(Color.FromArgb((byte)Math.Round(col.A * a), col.R, col.G, col.B), off));
                    }
                    if (list.Count > 0) return list;
                    at = Linked(at);
                }
                return new List<GradientStop>();
            }

            Brush Gradient(XmlElement g, Style s)
            {
                var stops = Stops(g, s);
                if (stops.Count == 0) return null;
                if (stops.Count == 1) return new SolidColorBrush(stops[0].Color);
                bool user = GradAttr(g, "gradientUnits") == "userSpaceOnUse";
                GradientBrush b;
                Func<string, double, double, double> coord = delegate (string name, double fallback, double size)
                {
                    string v = GradAttr(g, name);
                    if (v.Length == 0) return fallback;
                    return user ? Len(v, fallback, size, s.FontSize) : (v.Trim().EndsWith("%", StringComparison.Ordinal) ? Num(v.Trim().TrimEnd('%'), fallback * 100) / 100 : Num(v, fallback));
                };
                if (g.LocalName == "linearGradient")
                {
                    b = new LinearGradientBrush
                    {
                        StartPoint = new Point(coord("x1", 0, vw), coord("y1", 0, vh)),
                        EndPoint = new Point(coord("x2", user ? vw : 1, vw), coord("y2", 0, vh))
                    };
                }
                else
                {
                    double cx = coord("cx", user ? vw / 2 : 0.5, vw), cy = coord("cy", user ? vh / 2 : 0.5, vh), r = coord("r", user ? Math.Sqrt((vw * vw + vh * vh) / 2) / 2 : 0.5, Math.Sqrt((vw * vw + vh * vh) / 2));
                    b = new RadialGradientBrush
                    {
                        Center = new Point(cx, cy),
                        GradientOrigin = new Point(coord("fx", cx, vw), coord("fy", cy, vh)),
                        RadiusX = r,
                        RadiusY = r
                    };
                }
                b.MappingMode = user ? BrushMappingMode.Absolute : BrushMappingMode.RelativeToBoundingBox;
                string spread = GradAttr(g, "spreadMethod");
                b.SpreadMethod = spread == "reflect" ? GradientSpreadMethod.Reflect : spread == "repeat" ? GradientSpreadMethod.Repeat : GradientSpreadMethod.Pad;
                foreach (var st in stops) b.GradientStops.Add(st);
                string tf = GradAttr(g, "gradientTransform");
                if (tf.Length > 0)
                {
                    var t = new MatrixTransform(ParseTransform(tf));
                    if (user) b.Transform = t;
                    else b.RelativeTransform = t;
                }
                return b;
            }

            Geometry ClipOf(string value, int depth)
            {
                var m = Regex.Match(value, @"url\(\s*['""]?#([^'"")\s]+)['""]?\s*\)");
                XmlElement c;
                if (!m.Success || !ids.TryGetValue(m.Groups[1].Value, out c) || c.LocalName != "clipPath") return null;
                var group = new GeometryGroup { FillRule = FillRule.Nonzero };
                var baseStyle = new Style();
                foreach (var e in c.ChildNodes.OfType<XmlElement>())
                {
                    var p = PropsOf(e);
                    var st = Apply(baseStyle, p);
                    st.FillRule = st.ClipRule;
                    Geometry g = null;
                    if (e.LocalName == "use")
                    {
                        string h = Href(p);
                        XmlElement t;
                        if (h.StartsWith("#", StringComparison.Ordinal) && ids.TryGetValue(h.Substring(1), out t))
                        {
                            var tp = PropsOf(t);
                            var ts = Apply(st, tp);
                            ts.FillRule = ts.ClipRule;
                            g = GeometryOf(t, ts, tp);
                            if (g != null)
                            {
                                string tv;
                                var mt = Matrix.Identity;
                                if (tp.TryGetValue("transform", out tv)) mt = ParseTransform(tv);
                                mt.Translate(Attr(p, "x", 0, vw, st), Attr(p, "y", 0, vh, st));
                                g = Moved(g, mt);
                            }
                        }
                    }
                    else g = GeometryOf(e, st, p);
                    if (g == null) continue;
                    string tr;
                    if (e.LocalName != "use" && p.TryGetValue("transform", out tr)) g = Moved(g, ParseTransform(tr));
                    group.Children.Add(g);
                }
                if (group.Children.Count == 0) return Geometry.Empty;
                string own = c.GetAttribute("transform");
                return own.Length > 0 ? Moved(group, ParseTransform(own)) : group;
            }

            static Geometry Moved(Geometry g, Matrix m)
            {
                if (m.IsIdentity) return g;
                var c = g.Clone();
                c.Transform = new MatrixTransform(m);
                return c;
            }

            // ── 글자 ──

            static string FamilyList(string css)
            {
                var names = new List<string>();
                foreach (string part in (css ?? "").Split(','))
                {
                    string n = part.Trim().Trim('\'', '"').Trim();
                    switch (n.ToLowerInvariant())
                    {
                        case "":
                            continue;
                        case "sans-serif":
                        case "system-ui":
                        case "ui-sans-serif":
                            names.Add("Malgun Gothic");
                            break;
                        case "serif":
                        case "ui-serif":
                            names.Add("Batang");
                            break;
                        case "monospace":
                        case "ui-monospace":
                            names.Add("Consolas");
                            break;
                        default:
                            names.Add(n);
                            break;
                    }
                }
                names.Add("Malgun Gothic");
                return string.Join(", ", names.Distinct());
            }

            sealed class Cursor
            {
                public double X, Y;
                public List<Run> Line = new List<Run>();
            }

            sealed class Run
            {
                public Geometry Geo;
                public Style Style;
                public double Width;
                public double Start;
            }

            void Text(XmlElement e, Style s, Dictionary<string, string> p)
            {
                var cur = new Cursor { X = FirstLen(p, "x", 0, vw, s), Y = FirstLen(p, "y", 0, vh, s) };
                cur.X += FirstLen(p, "dx", 0, vw, s);
                cur.Y += FirstLen(p, "dy", 0, vh, s);
                TextNodes(e, s, cur);
                Flush(cur);
            }

            double FirstLen(Dictionary<string, string> p, string name, double fallback, double percentOf, Style s)
            {
                string v;
                if (!p.TryGetValue(name, out v)) return fallback;
                var parts = v.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 0 ? fallback : Len(parts[0], fallback, percentOf, s.FontSize);
            }

            void TextNodes(XmlElement parent, Style s, Cursor cur)
            {
                var kids = parent.ChildNodes.Cast<XmlNode>().ToList();
                for (int i = 0; i < kids.Count; i++)
                {
                    var n = kids[i];
                    if (n.NodeType == XmlNodeType.Text || n.NodeType == XmlNodeType.CDATA || n.NodeType == XmlNodeType.SignificantWhitespace || n.NodeType == XmlNodeType.Whitespace)
                    {
                        string t = Regex.Replace(n.Value, @"\s+", " ");
                        if (i == 0) t = t.TrimStart();
                        if (i == kids.Count - 1) t = t.TrimEnd();
                        if (t.Length > 0) AddText(t, s, cur);
                        continue;
                    }
                    var c = n as XmlElement;
                    if (c == null || !IsSvg(c) || c.LocalName != "tspan") continue;
                    if (++count > MaxElements) throw new UserFacingException("SVG 그림이 너무 복잡해요.");
                    var p = PropsOf(c);
                    string v;
                    if (p.TryGetValue("display", out v) && v.Trim() == "none") continue;
                    var st = Apply(s, p);
                    bool moved = p.ContainsKey("x") || p.ContainsKey("y");
                    if (moved) Flush(cur);
                    if (p.ContainsKey("x")) cur.X = FirstLen(p, "x", cur.X, vw, st);
                    if (p.ContainsKey("y")) cur.Y = FirstLen(p, "y", cur.Y, vh, st);
                    cur.X += FirstLen(p, "dx", 0, vw, st);
                    cur.Y += FirstLen(p, "dy", 0, vh, st);
                    TextNodes(c, st, cur);
                }
            }

            void AddText(string text, Style s, Cursor cur)
            {
                var face = new Typeface(new FontFamily(FamilyList(s.Family)), s.Italic ? FontStyles.Italic : FontStyles.Normal, s.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, face, s.FontSize, Brushes.Black, 1.0);
                var geo = ft.BuildGeometry(new Point(cur.X, cur.Y - ft.Baseline));
                cur.Line.Add(new Run { Geo = geo, Style = s, Width = ft.WidthIncludingTrailingWhitespace, Start = cur.X });
                cur.X += ft.WidthIncludingTrailingWhitespace;
            }

            void Flush(Cursor cur)
            {
                if (cur.Line.Count == 0) return;
                double total = cur.Line.Sum(r => r.Width);
                string anchor = cur.Line[0].Style.Anchor;
                double shift = anchor == "middle" ? -total / 2 : anchor == "end" ? -total : 0;
                foreach (var r in cur.Line)
                {
                    if (r.Style.Hidden) continue;
                    var g = shift == 0 ? r.Geo : Moved(r.Geo, new Matrix(1, 0, 0, 1, shift, 0));
                    var fill = BrushFor(r.Style.Fill, r.Style.FillOpacity, r.Style, g);
                    var pen = PenFor(r.Style, g);
                    if (fill != null || pen != null) dc.DrawGeometry(fill, pen, g);
                }
                cur.X += shift;
                cur.Line = new List<Run>();
            }

            // ── 그림 ──

            void Image(XmlElement e, Dictionary<string, string> p)
            {
                string h = Href(p);
                var m = Regex.Match(h, @"^data:image/(png|jpe?g|gif|bmp|webp|tiff?)\s*;\s*base64\s*,(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!m.Success) return;
                BitmapSource img;
                try
                {
                    img = Imaging.Upright(Imaging.Decoder(Convert.FromBase64String(Regex.Replace(m.Groups[2].Value, @"\s+", ""))).Frames[0]);
                }
                catch (Exception ex)
                {
                    Log.Write("SVG 속 그림 건너뜀: " + ex.GetType().Name);
                    return;
                }
                var s = new Style();
                double x = Attr(p, "x", 0, vw, s), y = Attr(p, "y", 0, vh, s), w = Attr(p, "width", img.PixelWidth, vw, s), h2 = Attr(p, "height", img.PixelHeight, vh, s);
                if (w <= 0 || h2 <= 0) return;
                string par;
                p.TryGetValue("preserveAspectRatio", out par);
                Matrix fit;
                Rect clip;
                ViewBoxFit("0 0 " + img.PixelWidth + " " + img.PixelHeight, par, x, y, w, h2, out fit, out clip);
                dc.PushClip(new RectangleGeometry(clip));
                dc.PushTransform(new MatrixTransform(fit));
                dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
                dc.Pop();
                dc.Pop();
            }
        }

        /// path의 d 값을 읽는다. 호 깃발(0·1)은 띄어쓰기 없이 붙어 있을 수 있어 숫자와 따로 읽는다.
        static class PathData
        {
            sealed class Seg
            {
                public char Op;
                public Point A, B, C;
                public Size R;
                public double Rot;
                public bool Large, Sweep;
            }

            sealed class Fig
            {
                public Point Start;
                public bool Closed;
                public List<Seg> Segs = new List<Seg>();
            }

            sealed class Reader
            {
                readonly string s;
                public int P;

                public Reader(string t)
                {
                    s = t;
                }

                public void Skip()
                {
                    while (P < s.Length && (char.IsWhiteSpace(s[P]) || s[P] == ',')) P++;
                }

                public bool More
                {
                    get
                    {
                        Skip();
                        return P < s.Length;
                    }
                }

                public bool NextIsNumber
                {
                    get
                    {
                        Skip();
                        return P < s.Length && (char.IsDigit(s[P]) || s[P] == '-' || s[P] == '+' || s[P] == '.');
                    }
                }

                public char Letter()
                {
                    Skip();
                    return s[P++];
                }

                public bool TryNum(out double v)
                {
                    v = 0;
                    Skip();
                    int start = P;
                    if (P < s.Length && (s[P] == '-' || s[P] == '+')) P++;
                    int digits = 0;
                    while (P < s.Length && char.IsDigit(s[P]))
                    {
                        P++;
                        digits++;
                    }
                    if (P < s.Length && s[P] == '.')
                    {
                        P++;
                        while (P < s.Length && char.IsDigit(s[P]))
                        {
                            P++;
                            digits++;
                        }
                    }
                    if (digits == 0)
                    {
                        P = start;
                        return false;
                    }
                    if (P < s.Length && (s[P] == 'e' || s[P] == 'E'))
                    {
                        int mark = P++;
                        if (P < s.Length && (s[P] == '-' || s[P] == '+')) P++;
                        int ed = 0;
                        while (P < s.Length && char.IsDigit(s[P]))
                        {
                            P++;
                            ed++;
                        }
                        if (ed == 0) P = mark;
                    }
                    return double.TryParse(s.Substring(start, P - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
                }

                public bool TryFlag(out bool f)
                {
                    f = false;
                    Skip();
                    if (P >= s.Length || (s[P] != '0' && s[P] != '1')) return false;
                    f = s[P] == '1';
                    P++;
                    return true;
                }
            }

            public static Geometry Parse(string d, bool evenOdd)
            {
                var r = new Reader(d ?? "");
                var figs = new List<Fig>();
                Fig fig = null;
                Point cur = new Point(), start = new Point(), lastC = new Point(), lastQ = new Point();
                char prev = ' ';
                char op = ' ';
                bool stop = false;
                Func<Point, bool, Point> at = delegate (Point v, bool rel) { return rel ? new Point(cur.X + v.X, cur.Y + v.Y) : v; };
                Action ensure = delegate
                {
                    if (fig != null) return;
                    fig = new Fig { Start = cur };
                    figs.Add(fig);
                };
                while (!stop && r.More)
                {
                    if (!r.NextIsNumber)
                    {
                        op = r.Letter();
                        if ("MmLlHhVvCcSsQqTtAaZz".IndexOf(op) < 0) break;
                    }
                    else if (op == ' ' || op == 'Z' || op == 'z') break;
                    else if (op == 'M') op = 'L';
                    else if (op == 'm') op = 'l';
                    bool rel = char.IsLower(op);
                    double a, b, c, e, f, g;
                    bool la, sw;
                    switch (char.ToUpperInvariant(op))
                    {
                        case 'M':
                            if (!r.TryNum(out a) || !r.TryNum(out b))
                            {
                                stop = true;
                                break;
                            }
                            cur = at(new Point(a, b), rel);
                            start = cur;
                            fig = new Fig { Start = cur };
                            figs.Add(fig);
                            break;
                        case 'L':
                            if (!r.TryNum(out a) || !r.TryNum(out b))
                            {
                                stop = true;
                                break;
                            }
                            ensure();
                            cur = at(new Point(a, b), rel);
                            fig.Segs.Add(new Seg { Op = 'L', A = cur });
                            break;
                        case 'H':
                            if (!r.TryNum(out a))
                            {
                                stop = true;
                                break;
                            }
                            ensure();
                            cur = new Point(rel ? cur.X + a : a, cur.Y);
                            fig.Segs.Add(new Seg { Op = 'L', A = cur });
                            break;
                        case 'V':
                            if (!r.TryNum(out a))
                            {
                                stop = true;
                                break;
                            }
                            ensure();
                            cur = new Point(cur.X, rel ? cur.Y + a : a);
                            fig.Segs.Add(new Seg { Op = 'L', A = cur });
                            break;
                        case 'C':
                            if (!r.TryNum(out a) || !r.TryNum(out b) || !r.TryNum(out c) || !r.TryNum(out e) || !r.TryNum(out f) || !r.TryNum(out g))
                            {
                                stop = true;
                                break;
                            }
                            {
                                ensure();
                                var p1 = at(new Point(a, b), rel);
                                var p2 = at(new Point(c, e), rel);
                                var p3 = at(new Point(f, g), rel);
                                fig.Segs.Add(new Seg { Op = 'C', A = p1, B = p2, C = p3 });
                                lastC = p2;
                                cur = p3;
                            }
                            break;
                        case 'S':
                            if (!r.TryNum(out a) || !r.TryNum(out b) || !r.TryNum(out c) || !r.TryNum(out e))
                            {
                                stop = true;
                                break;
                            }
                            {
                                ensure();
                                char pu = char.ToUpperInvariant(prev);
                                var p1 = pu == 'C' || pu == 'S' ? new Point(2 * cur.X - lastC.X, 2 * cur.Y - lastC.Y) : cur;
                                var p2 = at(new Point(a, b), rel);
                                var p3 = at(new Point(c, e), rel);
                                fig.Segs.Add(new Seg { Op = 'C', A = p1, B = p2, C = p3 });
                                lastC = p2;
                                cur = p3;
                            }
                            break;
                        case 'Q':
                            if (!r.TryNum(out a) || !r.TryNum(out b) || !r.TryNum(out c) || !r.TryNum(out e))
                            {
                                stop = true;
                                break;
                            }
                            {
                                ensure();
                                var p1 = at(new Point(a, b), rel);
                                var p2 = at(new Point(c, e), rel);
                                fig.Segs.Add(new Seg { Op = 'Q', A = p1, B = p2 });
                                lastQ = p1;
                                cur = p2;
                            }
                            break;
                        case 'T':
                            if (!r.TryNum(out a) || !r.TryNum(out b))
                            {
                                stop = true;
                                break;
                            }
                            {
                                ensure();
                                char pu = char.ToUpperInvariant(prev);
                                var p1 = pu == 'Q' || pu == 'T' ? new Point(2 * cur.X - lastQ.X, 2 * cur.Y - lastQ.Y) : cur;
                                var p2 = at(new Point(a, b), rel);
                                fig.Segs.Add(new Seg { Op = 'Q', A = p1, B = p2 });
                                lastQ = p1;
                                cur = p2;
                            }
                            break;
                        case 'A':
                            if (!r.TryNum(out a) || !r.TryNum(out b) || !r.TryNum(out c) || !r.TryFlag(out la) || !r.TryFlag(out sw) || !r.TryNum(out e) || !r.TryNum(out f))
                            {
                                stop = true;
                                break;
                            }
                            {
                                ensure();
                                var p = at(new Point(e, f), rel);
                                if (a == 0 || b == 0) fig.Segs.Add(new Seg { Op = 'L', A = p });
                                else fig.Segs.Add(new Seg { Op = 'A', A = p, R = new Size(Math.Abs(a), Math.Abs(b)), Rot = c, Large = la, Sweep = sw });
                                cur = p;
                            }
                            break;
                        case 'Z':
                            if (fig != null) fig.Closed = true;
                            cur = start;
                            fig = null;
                            break;
                    }
                    prev = op;
                }
                var geo = new StreamGeometry();
                geo.FillRule = evenOdd ? FillRule.EvenOdd : FillRule.Nonzero;
                using (var c = geo.Open())
                {
                    foreach (var x in figs)
                    {
                        c.BeginFigure(x.Start, true, x.Closed);
                        foreach (var s in x.Segs)
                        {
                            switch (s.Op)
                            {
                                case 'L': c.LineTo(s.A, true, true); break;
                                case 'C': c.BezierTo(s.A, s.B, s.C, true, true); break;
                                case 'Q': c.QuadraticBezierTo(s.A, s.B, true, true); break;
                                case 'A': c.ArcTo(s.A, s.R, s.Rot, s.Large, s.Sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true); break;
                            }
                        }
                    }
                }
                return geo;
            }
        }
    }
}
