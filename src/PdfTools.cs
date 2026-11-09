// PDF를 쪽 단위로 다루는 일: 합치기·나누기·쪽 고르기·돌리기, 워터마크·쪽 번호 넣기, 정보 지우기.
// 쪽을 새로 짜는 일(Compose)은 쪽 객체만 옮겨 담으므로 글자·그림은 그대로이고, 책갈피와 양식은 새 문서로 옮기지 않는다.
// 워터마크는 글자를 그림(투명도 포함)으로 넣는다. 어떤 글꼴이든 다른 PC에서 똑같이 보이게 하기 위해서다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    public sealed class PagePick
    {
        public PdfDocument Doc;
        public int Num;
        public int Rotate;
    }

    public sealed class PdfMark
    {
        public string Title;
        public int PickIndex;
    }

    public sealed class WatermarkSpec
    {
        public string Text = "";
        /// diagonal(대각선) · center(가운데) · tile(바둑판)
        public string Mode = "diagonal";
        public double Opacity = 0.2;
        public uint Rgb = 0x5A5F6E;
    }

    public sealed class PageNumberSpec
    {
        /// bottom-center · bottom-right · top-right
        public string Place = "bottom-center";
        /// plain(1) · dash(- 1 -) · total(1 / 10)
        public string Style = "plain";
        public int Start = 1;
    }

    public static class PdfTools
    {
        public static PdfDocument OpenPlain(string path)
        {
            var doc = PdfDocument.Load(path);
            if (doc.Encrypted) throw new UserFacingException("암호가 걸린 PDF예요. 'PDF 편집 → 암호 풀기'로 먼저 풀어 주세요.");
            return doc;
        }

        public static List<int> Pages(PdfDocument doc)
        {
            var list = new List<int>();
            PdfPageSize.CollectPages(doc, doc.Resolve(doc.Trailer["Root"]) as PdfDict, list);
            if (list.Count == 0) throw new UserFacingException("이 PDF에서 쪽을 찾지 못했어요.");
            return list;
        }

        public static bool IsSigned(PdfDocument doc)
        {
            var root = doc.Resolve(doc.Trailer["Root"]) as PdfDict;
            var form = root == null ? null : doc.Resolve(root["AcroForm"]) as PdfDict;
            return form != null && (doc.IntOf(form, "SigFlags", 0) & 1) != 0;
        }

        public static bool HasOutlines(PdfDocument doc)
        {
            var root = doc.Resolve(doc.Trailer["Root"]) as PdfDict;
            return root != null && doc.Resolve(root["Outlines"]) is PdfDict;
        }

        // ── 쪽 범위 ──

        /// "1,3-5,8-" 를 쪽 번호(0부터) 목록으로. 맞지 않으면 null과 이유.
        public static List<int> ParseRanges(string text, int total, out string error)
        {
            error = null;
            var result = new List<int>();
            string s = (text ?? "").Replace('，', ',').Replace('、', ',').Replace('~', '-').Replace('–', '-').Replace('∼', '-').Replace(" ", ",");
            var parts = s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                error = "쪽 번호를 적어 주세요. 예: 1,3-5";
                return null;
            }
            foreach (var part in parts)
            {
                int dash = part.IndexOf('-');
                int from, to;
                if (dash < 0)
                {
                    if (!int.TryParse(part, out from))
                    {
                        error = "'" + part + "'는 쪽 번호가 아니에요. 예: 1,3-5";
                        return null;
                    }
                    to = from;
                }
                else
                {
                    string a = part.Substring(0, dash), b = part.Substring(dash + 1);
                    if (a.Length == 0) from = 1;
                    else if (!int.TryParse(a, out from))
                    {
                        error = "'" + part + "'는 쪽 번호가 아니에요. 예: 1,3-5";
                        return null;
                    }
                    if (b.Length == 0) to = total;
                    else if (!int.TryParse(b, out to))
                    {
                        error = "'" + part + "'는 쪽 번호가 아니에요. 예: 1,3-5";
                        return null;
                    }
                }
                if (from < 1 || to < 1 || from > total || to > total)
                {
                    error = "이 문서는 " + total + "쪽까지 있어요.";
                    return null;
                }
                int step = from <= to ? 1 : -1;
                for (int p = from; ; p += step)
                {
                    result.Add(p - 1);
                    if (p == to) break;
                }
            }
            return result;
        }

        // ── 쪽 새로 짜기 ──

        sealed class Source
        {
            public PdfDocument Doc;
            public HashSet<int> PageSet;
            public readonly Dictionary<int, int> Map = new Dictionary<int, int>();
            public readonly Dictionary<int, int> FirstPage = new Dictionary<int, int>();
        }

        sealed class Composer
        {
            public int Next = 4;
            public readonly Dictionary<int, PdfObj> Objects = new Dictionary<int, PdfObj>();
            readonly Queue<KeyValuePair<Source, KeyValuePair<int, int>>> todo = new Queue<KeyValuePair<Source, KeyValuePair<int, int>>>();

            int Renumber(Source s, int old)
            {
                int n;
                // 다른 쪽을 가리키는 것(링크·주석): 새 문서에 그 쪽이 있으면 그리로, 없으면 비운다(안 고른 쪽이 딸려 오지 않게).
                if (s.PageSet.Contains(old)) return s.FirstPage.TryGetValue(old, out n) ? n : 0;
                if (s.Map.TryGetValue(old, out n)) return n;
                var o = s.Doc.Get(old);
                if (o is PdfNull) return 0;
                var d = o as PdfDict;
                if (d != null)
                {
                    string t = d.NameOf("Type");
                    if (t == "Pages" || t == "Catalog") return 0;
                }
                n = Next++;
                s.Map[old] = n;
                todo.Enqueue(new KeyValuePair<Source, KeyValuePair<int, int>>(s, new KeyValuePair<int, int>(old, n)));
                return n;
            }

            public PdfObj Clone(Source s, PdfObj o)
            {
                var r = o as PdfRef;
                if (r != null)
                {
                    int n = Renumber(s, r.Num);
                    return n > 0 ? (PdfObj)new PdfRef(n, 0) : PdfNull.Value;
                }
                var a = o as PdfArray;
                if (a != null)
                {
                    var na = new PdfArray();
                    foreach (var x in a.Items) na.Items.Add(Clone(s, x));
                    return na;
                }
                var d = o as PdfDict;
                if (d != null)
                {
                    var nd = new PdfDict();
                    foreach (var k in d.Keys) nd[k] = Clone(s, d[k]);
                    return nd;
                }
                return o;
            }

            public void Drain()
            {
                while (todo.Count > 0)
                {
                    var item = todo.Dequeue();
                    var o = item.Key.Doc.Get(item.Value.Key);
                    var st = o as PdfStream;
                    if (st != null)
                    {
                        var nd = new PdfDict();
                        foreach (var k in st.Dict.Keys)
                        {
                            if (k != "Length") nd[k] = Clone(item.Key, st.Dict[k]);
                        }
                        Objects[item.Value.Value] = new PdfStream(nd, st.Data);
                    }
                    else Objects[item.Value.Value] = Clone(item.Key, o);
                }
            }
        }

        public static void Compose(IList<PagePick> picks, string path, IList<PdfMark> marks, Job job)
        {
            if (picks.Count == 0) throw new UserFacingException("남길 쪽이 없어요.");
            var sources = new Dictionary<PdfDocument, Source>();
            var c = new Composer();
            var newNum = new int[picks.Count];
            for (int i = 0; i < picks.Count; i++)
            {
                Source s;
                if (!sources.TryGetValue(picks[i].Doc, out s))
                {
                    s = new Source { Doc = picks[i].Doc, PageSet = new HashSet<int>(Pages(picks[i].Doc)) };
                    sources[picks[i].Doc] = s;
                }
                newNum[i] = c.Next++;
                if (!s.FirstPage.ContainsKey(picks[i].Num)) s.FirstPage[picks[i].Num] = newNum[i];
            }
            for (int i = 0; i < picks.Count; i++)
            {
                job.Check();
                if (i % 16 == 0 && picks.Count > 32) job.Report("쪽 " + (i + 1) + " / " + picks.Count + " 옮기는 중", (double)i / picks.Count);
                var p = picks[i];
                var src = sources[p.Doc];
                var page = p.Doc.Get(p.Num) as PdfDict;
                if (page == null) throw new UserFacingException("쪽을 읽지 못했어요.");
                var np = new PdfDict();
                foreach (var k in page.Keys)
                {
                    if (k == "Parent" || k == "B") continue;
                    np[k] = c.Clone(src, page[k]);
                }
                np.SetName("Type", "Page");
                np["Parent"] = new PdfRef(2, 0);
                foreach (var key in new[] { "MediaBox", "CropBox", "Resources" })
                {
                    if (page[key] != null) continue;
                    var inh = PdfPageSize.Inherited(p.Doc, page, key);
                    if (inh != null && !(inh is PdfNull)) np[key] = c.Clone(src, inh);
                }
                if (np["MediaBox"] == null) np["MediaBox"] = PdfArray.Nums(0, 0, 595.276, 841.89);
                var rot = PdfPageSize.Inherited(p.Doc, page, "Rotate") as PdfNum;
                int r = (((rot == null ? 0 : rot.I) + p.Rotate) % 360 + 360) % 360;
                if (r != 0) np.SetNum("Rotate", r);
                else np.Remove("Rotate");
                c.Objects[newNum[i]] = np;
            }
            c.Drain();
            var kids = new PdfArray();
            foreach (int n in newNum) kids.Items.Add(new PdfRef(n, 0));
            var pages = new PdfDict();
            pages.SetName("Type", "Pages");
            pages["Kids"] = kids;
            pages.SetNum("Count", picks.Count);
            c.Objects[2] = pages;
            var catalog = new PdfDict();
            catalog.SetName("Type", "Catalog");
            catalog["Pages"] = new PdfRef(2, 0);
            if (marks != null && marks.Count > 0)
            {
                int rootNum = c.Next++;
                int first = c.Next;
                c.Next += marks.Count;
                for (int i = 0; i < marks.Count; i++)
                {
                    var item = new PdfDict();
                    item["Title"] = PdfString.Text(marks[i].Title);
                    item["Parent"] = new PdfRef(rootNum, 0);
                    if (i > 0) item["Prev"] = new PdfRef(first + i - 1, 0);
                    if (i < marks.Count - 1) item["Next"] = new PdfRef(first + i + 1, 0);
                    item["Dest"] = new PdfArray(new PdfRef(newNum[marks[i].PickIndex], 0), new PdfName("Fit"));
                    c.Objects[first + i] = item;
                }
                var outlines = new PdfDict();
                outlines.SetName("Type", "Outlines");
                outlines["First"] = new PdfRef(first, 0);
                outlines["Last"] = new PdfRef(first + marks.Count - 1, 0);
                outlines.SetNum("Count", marks.Count);
                c.Objects[rootNum] = outlines;
                catalog["Outlines"] = new PdfRef(rootNum, 0);
            }
            c.Objects[1] = catalog;
            var info = new PdfDict();
            info["Producer"] = PdfString.Text(AppInfo.Name + " " + AppInfo.Version);
            info["CreationDate"] = PdfWriter.Date(DateTime.Now);
            c.Objects[3] = info;
            var order = c.Objects.Keys.OrderBy(x => x).ToList();
            var map = new Dictionary<int, int>();
            for (int i = 0; i < order.Count; i++) map[order[i]] = i + 1;
            Func<PdfRef, int> mapRef = delegate (PdfRef r)
            {
                int n;
                return map.TryGetValue(r.Num, out n) ? n : 0;
            };
            var trailer = new PdfDict();
            trailer["Root"] = new PdfRef(1, 0);
            trailer["Info"] = new PdfRef(3, 0);
            trailer["ID"] = PdfWriter.NewId(path);
            string version = "1.5";
            foreach (var d in sources.Keys)
            {
                if (string.CompareOrdinal(d.Version, version) > 0) version = d.Version;
            }
            job.Report("새 PDF로 쓰는 중", -1);
            var objects = c.Objects;
            PdfRewrite.WritePacked(order, n => objects[n], map, mapRef, version, trailer, path, true);
        }

        // ── 쪽에 얹기 ──

        /// 쪽의 보이는 영역(회전 반영). fromView는 보이는 좌표(왼쪽 아래 0,0)를 쪽 좌표로 바꾸는 행렬.
        static void ViewOf(PdfDocument doc, PdfDict page, out double vw, out double vh, out string fromView)
        {
            var media = PdfPageSize.Box(doc, PdfPageSize.Inherited(doc, page, "MediaBox")) ?? new[] { 0, 0, 595.276, 841.89 };
            var crop = PdfPageSize.Box(doc, PdfPageSize.Inherited(doc, page, "CropBox"));
            double x0 = media[0], y0 = media[1], x1 = media[2], y1 = media[3];
            if (crop != null)
            {
                double a = Math.Max(x0, crop[0]), b = Math.Max(y0, crop[1]), c = Math.Min(x1, crop[2]), d = Math.Min(y1, crop[3]);
                if (c > a && d > b)
                {
                    x0 = a;
                    y0 = b;
                    x1 = c;
                    y1 = d;
                }
            }
            var rotNum = PdfPageSize.Inherited(doc, page, "Rotate") as PdfNum;
            int rot = rotNum == null ? 0 : ((rotNum.I % 360) + 360) % 360;
            Func<double, string> f = PdfNum.Fmt;
            switch (rot)
            {
                case 90:
                    vw = y1 - y0;
                    vh = x1 - x0;
                    fromView = "0 1 -1 0 " + f(x1) + " " + f(y0);
                    break;
                case 180:
                    vw = x1 - x0;
                    vh = y1 - y0;
                    fromView = "-1 0 0 -1 " + f(x1) + " " + f(y1);
                    break;
                case 270:
                    vw = y1 - y0;
                    vh = x1 - x0;
                    fromView = "0 -1 1 0 " + f(x0) + " " + f(y1);
                    break;
                default:
                    vw = x1 - x0;
                    vh = y1 - y0;
                    fromView = "1 0 0 1 " + f(x0) + " " + f(y0);
                    break;
            }
        }

        delegate string OverlayContent(int index, int total, double vw, double vh);

        /// 모든 쪽 위에 내용을 덧그려 새 파일로 쓴다. 나머지(책갈피·양식)는 그대로 둔다.
        static void ApplyOverlay(PdfDocument doc, string path, Job job, IList<int> added, Dictionary<int, PdfObj> replace, OverlayContent content, Action<PdfDict> addResources)
        {
            var order = doc.Reachable();
            var pages = Pages(doc);
            int next = order.Concat(added).Max() + 1;
            int qNum = next++, closeNum = next++;
            replace[qNum] = new PdfStream(new PdfDict(), Encoding.ASCII.GetBytes("q\n"));
            replace[closeNum] = new PdfStream(new PdfDict(), Encoding.ASCII.GetBytes("\nQ\n"));
            var all = new List<int>(order);
            all.AddRange(added);
            all.Add(qNum);
            all.Add(closeNum);
            var byText = new Dictionary<string, int>();
            for (int i = 0; i < pages.Count; i++)
            {
                job.Check();
                if (i % 16 == 0 && pages.Count > 32) job.Report("쪽 " + (i + 1) + " / " + pages.Count + " 넣는 중", (double)i / pages.Count);
                var page = doc.Get(pages[i]) as PdfDict;
                if (page == null) continue;
                double vw, vh;
                string fromView;
                ViewOf(doc, page, out vw, out vh, out fromView);
                string body = "q " + fromView + " cm\n" + content(i, pages.Count, vw, vh) + "\nQ\n";
                int bodyNum;
                if (!byText.TryGetValue(body, out bodyNum))
                {
                    bodyNum = next++;
                    byText[body] = bodyNum;
                    replace[bodyNum] = new PdfStream(new PdfDict(), Encoding.ASCII.GetBytes(body));
                    all.Add(bodyNum);
                }
                var np = page.Copy();
                var contents = new PdfArray();
                contents.Items.Add(new PdfRef(qNum, 0));
                var old = page["Contents"];
                var oldArray = doc.Resolve(old) as PdfArray;
                if (oldArray != null) contents.Items.AddRange(oldArray.Items);
                else if (old is PdfRef) contents.Items.Add(old);
                contents.Items.Add(new PdfRef(closeNum, 0));
                contents.Items.Add(new PdfRef(bodyNum, 0));
                np["Contents"] = contents;
                var inherited = PdfPageSize.Inherited(doc, page, "Resources") as PdfDict;
                var res = inherited == null ? new PdfDict() : inherited.Copy();
                addResources(res);
                np["Resources"] = res;
                replace[pages[i]] = np;
            }
            job.Report("새 PDF로 쓰는 중", -1);
            PdfRewrite.Write(doc, all, replace, path, false);
        }

        static void PutResource(PdfDocument doc, PdfDict res, string category, string name, PdfObj value)
        {
            var old = doc.Resolve(res[category]) as PdfDict;
            var nd = old == null ? new PdfDict() : old.Copy();
            nd[name] = value;
            res[category] = nd;
        }

        // ── 워터마크 ──

#pragma warning disable 618
        static PdfStream WatermarkImage(WatermarkSpec spec, int maskNum, out double aspect)
        {
            var typeface = new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft = new FormattedText(spec.Text, new CultureInfo("ko-KR"), FlowDirection.LeftToRight, typeface, 180, Brushes.Black);
            int w = Math.Max(4, (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 40), h = Math.Max(4, (int)Math.Ceiling(ft.Height) + 24);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawText(ft, new Point(20, 12));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            var alpha = new byte[w * h];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = (byte)(px[i * 4 + 3] * spec.Opacity + 0.5);
            var rgb = new byte[w * h * 3];
            byte r = (byte)(spec.Rgb >> 16), g = (byte)(spec.Rgb >> 8), b = (byte)spec.Rgb;
            for (int i = 0; i < w * h; i++)
            {
                rgb[i * 3] = r;
                rgb[i * 3 + 1] = g;
                rgb[i * 3 + 2] = b;
            }
            aspect = (double)w / h;
            var d = ImageDict(w, h, "DeviceRGB");
            d["SMask"] = new PdfRef(maskNum, 0);
            lastMask = new PdfStream(ImageDict(w, h, "DeviceGray"), Zlib.Deflate(alpha));
            return new PdfStream(d, Zlib.Deflate(rgb));
        }
#pragma warning restore 618

        [ThreadStatic]
        static PdfStream lastMask;

        static PdfDict ImageDict(int w, int h, string cs)
        {
            var d = new PdfDict();
            d.SetName("Type", "XObject");
            d.SetName("Subtype", "Image");
            d.SetNum("Width", w);
            d.SetNum("Height", h);
            d.SetName("ColorSpace", cs);
            d.SetNum("BitsPerComponent", 8);
            d.SetName("Filter", "FlateDecode");
            return d;
        }

        static string Place(double angle, double cx, double cy, double w, double h)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            Func<double, string> f = PdfNum.Fmt;
            return "q " + f(c) + " " + f(s) + " " + f(-s) + " " + f(c) + " " + f(cx) + " " + f(cy) + " cm " + f(w) + " 0 0 " + f(h) + " " + f(-w / 2) + " " + f(-h / 2) + " cm /BkMark Do Q";
        }

        static string WatermarkContent(WatermarkSpec spec, double aspect, double vw, double vh)
        {
            double shortSide = Math.Min(vw, vh);
            if (spec.Mode == "center")
            {
                double w = Math.Min(0.72 * vw, 0.22 * shortSide * aspect);
                return Place(0, vw / 2, vh / 2, w, w / aspect);
            }
            if (spec.Mode == "tile")
            {
                double angle = 30 * Math.PI / 180;
                double w = Math.Min(0.34 * vw, 0.075 * shortSide * aspect);
                double h = w / aspect;
                double stepX = w * Math.Cos(angle) + Math.Max(40, 0.1 * vw), stepY = w * Math.Sin(angle) + h + Math.Max(26, 0.05 * vh);
                var sb = new StringBuilder();
                int row = 0;
                for (double y = stepY * 0.4; y < vh; y += stepY, row++)
                {
                    for (double x = (row % 2 == 0 ? stepX * 0.4 : stepX * 0.9); x < vw; x += stepX) sb.Append(Place(angle, x, y, w, h)).Append('\n');
                }
                return sb.ToString();
            }
            {
                double angle = Math.Atan2(vh, vw);
                double c = Math.Cos(angle), s = Math.Sin(angle);
                double w = 0.92 * Math.Min(vw / (c + s / aspect), vh / (s + c / aspect));
                w = Math.Min(w, 0.28 * shortSide * aspect);
                return Place(angle, vw / 2, vh / 2, w, w / aspect);
            }
        }

        public static void Watermark(PdfDocument doc, string path, WatermarkSpec spec, Job job)
        {
            int next = doc.Reachable().Max() + 1;
            int maskNum = next++, imageNum = next++;
            double aspect;
            var replace = new Dictionary<int, PdfObj>();
            replace[imageNum] = WatermarkImage(spec, maskNum, out aspect);
            replace[maskNum] = lastMask;
            ApplyOverlay(doc, path, job, new List<int> { maskNum, imageNum }, replace,
                (i, total, vw, vh) => WatermarkContent(spec, aspect, vw, vh),
                res => PutResource(doc, res, "XObject", "BkMark", new PdfRef(imageNum, 0)));
        }

        // ── 쪽 번호 ──

        static double TextWidth(string s, double size)
        {
            double sum = 0;
            foreach (char c in s)
            {
                if (c >= '0' && c <= '9') sum += 556;
                else if (c == '-') sum += 333;
                else sum += 278;
            }
            return sum * size / 1000;
        }

        public static void PageNumbers(PdfDocument doc, string path, PageNumberSpec spec, Job job)
        {
            int next = doc.Reachable().Max() + 1;
            int fontNum = next++;
            var font = new PdfDict();
            font.SetName("Type", "Font");
            font.SetName("Subtype", "Type1");
            font.SetName("BaseFont", "Helvetica");
            font.SetName("Encoding", "WinAnsiEncoding");
            var replace = new Dictionary<int, PdfObj>();
            replace[fontNum] = font;
            const double Size = 11;
            ApplyOverlay(doc, path, job, new List<int> { fontNum }, replace, (i, total, vw, vh) =>
            {
                int n = spec.Start + i;
                string s = spec.Style == "dash" ? "- " + n + " -" : spec.Style == "total" ? n + " / " + (spec.Start + total - 1) : n.ToString();
                double tw = TextWidth(s, Size);
                double x, y = 24;
                if (spec.Place == "bottom-right") x = vw - 40 - tw;
                else if (spec.Place == "top-right")
                {
                    x = vw - 40 - tw;
                    y = vh - 34;
                }
                else x = (vw - tw) / 2;
                return "BT /BkPage " + PdfNum.Fmt(Size) + " Tf 0.2 0.2 0.25 rg 1 0 0 1 " + PdfNum.Fmt(x) + " " + PdfNum.Fmt(y) + " Tm (" + s + ") Tj ET";
            }, res => PutResource(doc, res, "Font", "BkPage", new PdfRef(fontNum, 0)));
        }

        // ── 정보 지우기 ──

        public static void StripInfo(PdfDocument doc, string path)
        {
            doc.Trailer.Remove("Info");
            var root = doc.Resolve(doc.Trailer["Root"]) as PdfDict;
            if (root != null) root.Remove("Metadata");
            foreach (int p in Pages(doc))
            {
                var page = doc.Get(p) as PdfDict;
                if (page == null) continue;
                page.Remove("Metadata");
                page.Remove("PieceInfo");
            }
            var order = doc.Reachable();
            var id = doc.Resolve(doc.Trailer["ID"]) as PdfArray;
            if (id != null) doc.Trailer.Remove("ID");
            PdfRewrite.Write(doc, order, new Dictionary<int, PdfObj>(), path, false);
        }

        /// 쪽을 돌리기만 할 때: 책갈피·양식을 그대로 두고 /Rotate만 바꾼다.
        public static void RotateOnly(PdfDocument doc, IDictionary<int, int> delta, string path)
        {
            var replace = new Dictionary<int, PdfObj>();
            foreach (var kv in delta)
            {
                var page = doc.Get(kv.Key) as PdfDict;
                if (page == null) continue;
                var rot = PdfPageSize.Inherited(doc, page, "Rotate") as PdfNum;
                int r = (((rot == null ? 0 : rot.I) + kv.Value) % 360 + 360) % 360;
                var np = page.Copy();
                np.SetNum("Rotate", r);
                replace[kv.Key] = np;
            }
            PdfRewrite.Write(doc, doc.Reachable(), replace, path, false);
        }
    }
}
