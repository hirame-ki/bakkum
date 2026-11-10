// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// PDF 안에 들어 있는 글자를 그대로 읽는다(글자 인식을 거치지 않아 틀리지 않는다).
// 글꼴에 글자 대응표가 없어 믿을 수 없는 PDF나 스캔본은 Usable이 false다. 그때는 글자 인식이나 AI로 넘어간다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Bakkum
{
    public sealed class PdfTextRun
    {
        public double X, Y, End, Size;
        public string S;
        // 글자를 PPT로 자리 그대로 옮길 때 쓴다.
        public string Font;
        public bool Bold, Italic, Serif;
        public int Rgb;
    }

    public sealed class PdfTextLine
    {
        public double Y, Size;
        public readonly List<PdfTextRun> Runs = new List<PdfTextRun>();

        public string Text()
        {
            var sb = new StringBuilder();
            PdfTextRun prev = null;
            foreach (var r in Runs)
            {
                if (prev != null)
                {
                    double gap = r.X - prev.End;
                    bool spaced = sb.Length > 0 && sb[sb.Length - 1] == ' ' || r.S.StartsWith(" ");
                    if (gap > Math.Max(r.Size, prev.Size) * 0.22 && !spaced) sb.Append(' ');
                }
                sb.Append(r.S);
                prev = r;
            }
            return sb.ToString().Trim();
        }

        /// 넓게 띄어진 곳에서 나눈 칸(왼쪽 위치, 글자)
        public List<KeyValuePair<double, string>> Cells()
        {
            var list = new List<KeyValuePair<double, string>>();
            var sb = new StringBuilder();
            double start = 0;
            PdfTextRun prev = null;
            foreach (var r in Runs)
            {
                if (prev != null && r.X - prev.End > Math.Max(r.Size, prev.Size) * 1.4)
                {
                    list.Add(new KeyValuePair<double, string>(start, sb.ToString().Trim()));
                    sb.Clear();
                    prev = null;
                }
                if (prev == null) start = r.X;
                else if (r.X - prev.End > Math.Max(r.Size, prev.Size) * 0.22) sb.Append(' ');
                sb.Append(r.S);
                prev = r;
            }
            if (sb.ToString().Trim().Length > 0) list.Add(new KeyValuePair<double, string>(start, sb.ToString().Trim()));
            return list;
        }
    }

    public sealed class PdfPageText
    {
        public readonly List<PdfTextLine> Lines = new List<PdfTextLine>();
        public int Chars, Bad, Images;
        /// 보이는 쪽의 크기(pt). 돌려 놓은 쪽은 돌린 뒤의 크기.
        public double Width, Height;
        /// 그릴 때 보이지 않게 넣은 글자(스캔본 위에 얹힌 글자 층)
        public readonly List<PdfTextRun> HiddenRuns = new List<PdfTextRun>();
        /// 글자 빼기 결과(글자를 뺀 내용 흐름). 뺄 것이 없으면 null
        internal byte[] Stripped;

        public string Plain()
        {
            var sb = new StringBuilder();
            var gaps = new List<double>();
            for (int i = 1; i < Lines.Count; i++) gaps.Add(Lines[i - 1].Y - Lines[i].Y);
            gaps.Sort();
            double usual = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0;
            for (int i = 0; i < Lines.Count; i++)
            {
                if (i > 0 && usual > 0 && Lines[i - 1].Y - Lines[i].Y > usual * 1.7) sb.Append('\n');
                sb.Append(Lines[i].Text()).Append('\n');
            }
            return sb.ToString();
        }
    }

    public sealed class PdfTextResult
    {
        public readonly List<PdfPageText> Pages = new List<PdfPageText>();

        public int Chars
        {
            get { return Pages.Sum(p => p.Chars); }
        }

        /// 글자 대응이 믿을 만하고, 쪽 대부분에 글자가 있는가
        public bool Usable
        {
            get
            {
                int chars = Chars, bad = Pages.Sum(p => p.Bad);
                if (chars < 16 || Pages.Count == 0) return false;
                if (bad > chars * 0.03) return false;
                return Pages.Count(p => p.Chars >= 12) >= Math.Ceiling(Pages.Count * 0.8);
            }
        }

        public string Plain()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Pages.Count; i++)
            {
                if (Pages.Count > 1)
                {
                    if (sb.Length > 0) sb.Append("\r\n");
                    sb.Append("--- ").Append(i + 1).Append("쪽 ---\r\n\r\n");
                }
                sb.Append(Pages[i].Plain().Replace("\n", "\r\n"));
            }
            return sb.ToString();
        }

        /// 글자 위치로 표를 추측한다. 두 칸 이상인 줄이 세 줄 넘게 이어지면 표로 본다.
        public List<XlsxSheet> GuessTables()
        {
            var sheets = new List<XlsxSheet>();
            foreach (var page in Pages)
            {
                var run = new List<List<KeyValuePair<double, string>>>();
                Action flush = delegate
                {
                    if (run.Count >= 3) sheets.Add(MakeSheet(run, sheets.Count + 1));
                    run = new List<List<KeyValuePair<double, string>>>();
                };
                foreach (var l in page.Lines)
                {
                    var cells = l.Cells();
                    if (cells.Count >= 2) run.Add(cells);
                    else flush();
                }
                flush();
            }
            return sheets;
        }

        static XlsxSheet MakeSheet(List<List<KeyValuePair<double, string>>> lines, int number)
        {
            var xs = lines.SelectMany(l => l.Select(c => c.Key)).OrderBy(x => x).ToList();
            var cols = new List<double>();
            foreach (var x in xs)
            {
                if (cols.Count == 0 || x - cols[cols.Count - 1] > 12) cols.Add(x);
            }
            var sheet = new XlsxSheet { Name = "표" + number, Grid = true };
            foreach (var l in lines)
            {
                var row = new List<string>();
                foreach (var c in cols) row.Add("");
                foreach (var c in l)
                {
                    int best = 0;
                    for (int i = 1; i < cols.Count; i++)
                    {
                        if (Math.Abs(cols[i] - c.Key) < Math.Abs(cols[best] - c.Key)) best = i;
                    }
                    row[best] = row[best].Length == 0 ? c.Value : row[best] + " " + c.Value;
                }
                sheet.Rows.Add(row);
            }
            return sheet;
        }
    }

    public static class PdfText
    {
        /// 자리·글꼴·색까지 읽고, 읽어 낸 글자를 뺀 PDF를 strippedPath에 쓴다(PPT 배경을 그릴 때 쓴다).
        /// 기울어진 글자, 대응표가 없는 글자는 빼지 않고 배경에 남겨 둔다. 읽지 못하면 null.
        public static PdfTextResult ReadLayout(string path, string strippedPath, Job job)
        {
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Load(path);
            }
            catch (Exception ex)
            {
                Log.Write("PDF 글자 자리 읽기 실패: " + ex.GetType().Name);
                return null;
            }
            if (doc.Encrypted) return null;
            try
            {
                var result = new PdfTextResult();
                var pages = PdfTools.Pages(doc);
                var fonts = new Dictionary<PdfDict, TextFont>();
                var forms = new Dictionary<int, byte[]>();
                var order = doc.Reachable();
                var replace = new Dictionary<int, PdfObj>();
                var added = new List<int>();
                int next = order.Max() + 1;
                for (int i = 0; i < pages.Count; i++)
                {
                    if (job != null)
                    {
                        job.Check();
                        if (pages.Count > 2) job.Report("글자 자리 읽는 중 " + (i + 1) + " / " + pages.Count + "쪽", (double)i / pages.Count * 0.3);
                    }
                    var dict = doc.Get(pages[i]) as PdfDict;
                    if (dict == null) continue;
                    var page = new Walker(doc, fonts, forms).Page(dict);
                    result.Pages.Add(page);
                    if (page.Stripped == null) continue;
                    int n = next++;
                    var d = new PdfDict();
                    d.SetName("Filter", "FlateDecode");
                    replace[n] = new PdfStream(d, Zlib.Deflate(page.Stripped));
                    added.Add(n);
                    page.Stripped = null;
                    var np = dict.Copy();
                    np["Contents"] = new PdfRef(n, 0);
                    replace[pages[i]] = np;
                }
                foreach (var kv in forms)
                {
                    var st = doc.Get(kv.Key) as PdfStream;
                    if (st == null) continue;
                    var d = st.Dict.Copy();
                    d.Remove("DecodeParms");
                    d.Remove("DP");
                    d.Remove("F");
                    d.SetName("Filter", "FlateDecode");
                    replace[kv.Key] = new PdfStream(d, Zlib.Deflate(kv.Value));
                }
                var all = new List<int>(order);
                all.AddRange(added);
                PdfRewrite.Write(doc, all, replace, strippedPath, false);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Write("PDF 글자 자리 읽기 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                return null;
            }
        }

        /// 읽지 못하면 null(암호·손상 등). 스캔본이면 Usable이 false인 결과.
        public static PdfTextResult Read(string path, Job job)
        {
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Load(path);
            }
            catch (Exception ex)
            {
                Log.Write("PDF 글자 읽기 실패: " + ex.GetType().Name);
                return null;
            }
            if (doc.Encrypted) return null;
            try
            {
                var result = new PdfTextResult();
                var pages = PdfTools.Pages(doc);
                var fonts = new Dictionary<PdfDict, TextFont>();
                for (int i = 0; i < pages.Count; i++)
                {
                    if (job != null)
                    {
                        job.Check();
                        if (pages.Count > 4 && i % 4 == 0) job.Report("글자 읽는 중 " + (i + 1) + " / " + pages.Count + "쪽", (double)i / pages.Count);
                    }
                    var dict = doc.Get(pages[i]) as PdfDict;
                    if (dict == null) continue;
                    result.Pages.Add(new Walker(doc, fonts).Page(dict));
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Write("PDF 글자 읽기 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                return null;
            }
        }

        // ── 글꼴 ──

        enum CodeMode { Single, Identity, Ucs2, Cp949 }

        sealed class TextFont
        {
            public CodeMode Mode = CodeMode.Single;
            public readonly Dictionary<int, string> ToUni = new Dictionary<int, string>();
            public readonly Dictionary<int, double> Widths = new Dictionary<int, double>();
            public double Default = 1000;
            public string[] Simple;
            public bool Unknown;
            public string Name;
            public bool Bold, Italic, Serif;
        }

        static readonly Encoding Win1252 = Encoding.GetEncoding(1252);
        static readonly Encoding Mac = Encoding.GetEncoding(10000);
        static readonly Encoding Korean = Encoding.GetEncoding(949);

        static readonly Dictionary<string, string> GlyphNames = new Dictionary<string, string>
        {
            {"space"," "},{"exclam","!"},{"quotedbl","\""},{"numbersign","#"},{"dollar","$"},{"percent","%"},{"ampersand","&"},{"quotesingle","'"},{"quoteright","'"},{"parenleft","("},{"parenright",")"},{"asterisk","*"},{"plus","+"},{"comma",","},{"hyphen","-"},{"minus","-"},{"period","."},{"slash","/"},
            {"zero","0"},{"one","1"},{"two","2"},{"three","3"},{"four","4"},{"five","5"},{"six","6"},{"seven","7"},{"eight","8"},{"nine","9"},{"colon",":"},{"semicolon",";"},{"less","<"},{"equal","="},{"greater",">"},{"question","?"},{"at","@"},
            {"bracketleft","["},{"backslash","\\"},{"bracketright","]"},{"asciicircum","^"},{"underscore","_"},{"grave","`"},{"quoteleft","`"},{"braceleft","{"},{"bar","|"},{"braceright","}"},{"asciitilde","~"},
            {"bullet","•"},{"endash","–"},{"emdash","—"},{"quotedblleft","“"},{"quotedblright","”"},{"middot","·"},{"periodcentered","·"},{"ellipsis","…"}
        };

        static string FromGlyphName(string name)
        {
            string s;
            if (GlyphNames.TryGetValue(name, out s)) return s;
            if (name.Length == 1) return name;
            if (name.StartsWith("uni") && name.Length >= 7)
            {
                int v;
                if (int.TryParse(name.Substring(3, 4), System.Globalization.NumberStyles.HexNumber, null, out v)) return ((char)v).ToString();
            }
            return null;
        }

        static string Utf16(byte[] b)
        {
            return Encoding.BigEndianUnicode.GetString(b, 0, b.Length & ~1);
        }

        static int CodeOf(byte[] b)
        {
            int v = 0;
            foreach (byte x in b) v = (v << 8) | x;
            return v;
        }

        static void ParseCMap(byte[] data, TextFont f)
        {
            var lx = new PdfLexer(data, 0, data.Length);
            var args = new List<PdfObj>();
            while (true)
            {
                lx.SkipWs();
                int c = lx.Peek();
                if (c < 0) break;
                if (c == '<' || c == '[' || c == '(' || c == '/' || c == '+' || c == '-' || c == '.' || (c >= '0' && c <= '9'))
                {
                    try
                    {
                        args.Add(lx.Read());
                    }
                    catch (PdfException)
                    {
                        lx.Pos++;
                    }
                    continue;
                }
                string t = lx.Token();
                if (t.Length == 0)
                {
                    lx.Pos++;
                    continue;
                }
                if (t == "endbfchar")
                {
                    for (int i = 0; i + 1 < args.Count; i += 2)
                    {
                        var a = args[i] as PdfString;
                        var b = args[i + 1] as PdfString;
                        if (a != null && b != null) f.ToUni[CodeOf(a.V)] = Utf16(b.V);
                    }
                }
                else if (t == "endbfrange")
                {
                    for (int i = 0; i + 2 < args.Count; i += 3)
                    {
                        var lo = args[i] as PdfString;
                        var hi = args[i + 1] as PdfString;
                        if (lo == null || hi == null) continue;
                        int from = CodeOf(lo.V), to = CodeOf(hi.V);
                        if (to - from > 65535) continue;
                        var list = args[i + 2] as PdfArray;
                        var start = args[i + 2] as PdfString;
                        if (list != null)
                        {
                            for (int k = 0; k < list.Items.Count && from + k <= to; k++)
                            {
                                var s = list.Items[k] as PdfString;
                                if (s != null) f.ToUni[from + k] = Utf16(s.V);
                            }
                        }
                        else if (start != null && start.V.Length >= 2)
                        {
                            var u = (byte[])start.V.Clone();
                            for (int code = from; code <= to; code++)
                            {
                                f.ToUni[code] = Utf16(u);
                                for (int k = u.Length - 1; k >= 0; k--)
                                {
                                    if (++u[k] != 0) break;
                                }
                            }
                        }
                    }
                }
                args.Clear();
            }
        }

        /// 글꼴 이름에서 'ABCDEF+' 꼬리표를 떼고, 한글 이름(#B8#BC… 로 적힌 EUC-KR·UTF-8)을 되살린다.
        static string CleanFontName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw;
            int plus = s.IndexOf('+');
            if (plus == 6) s = s.Substring(7);
            if (s.All(c => c < 0x80)) return s;
            if (s.Any(c => c > 0xFF)) return s;
            byte[] b = s.Select(c => (byte)c).ToArray();
            try
            {
                return new UTF8Encoding(false, true).GetString(b);
            }
            catch (DecoderFallbackException)
            {
                return Korean.GetString(b);
            }
        }

        static void ReadFace(PdfDocument doc, PdfDict font, PdfDict desc, TextFont f)
        {
            f.Name = CleanFontName(font.NameOf("BaseFont"));
            string low = f.Name.ToLowerInvariant();
            int flags = desc == null ? 0 : doc.IntOf(desc, "Flags", 0);
            int weight = desc == null ? 0 : doc.IntOf(desc, "FontWeight", 0);
            f.Serif = (flags & 2) != 0 || low.Contains("batang") || low.Contains("myeongjo") || low.Contains("gungsuh") || low.Contains("times") || low.Contains("serif") && !low.Contains("sans");
            f.Italic = (flags & 64) != 0 || low.Contains("italic") || low.Contains("oblique");
            f.Bold = (flags & 262144) != 0 || weight >= 700 || low.Contains("bold") || low.Contains("black") || low.Contains("heavy");
        }

        static TextFont LoadFont(PdfDocument doc, PdfDict font)
        {
            var f = new TextFont();
            var cmap = doc.Resolve(font["ToUnicode"]) as PdfStream;
            if (cmap != null)
            {
                try { ParseCMap(doc.Decode(cmap), f); }
                catch (PdfException) { }
            }
            string sub = font.NameOf("Subtype");
            var enc = doc.Resolve(font["Encoding"]);
            if (sub == "Type0")
            {
                string name = enc is PdfName ? ((PdfName)enc).V : "";
                if (name.Contains("UCS2") || name.Contains("UTF16")) f.Mode = CodeMode.Ucs2;
                else if (name.StartsWith("KSC")) f.Mode = CodeMode.Cp949;
                else f.Mode = CodeMode.Identity;
                var kids = doc.Resolve(font["DescendantFonts"]) as PdfArray;
                var cid = kids != null && kids.Items.Count > 0 ? doc.Resolve(kids.Items[0]) as PdfDict : null;
                ReadFace(doc, font, cid == null ? null : doc.Resolve(cid["FontDescriptor"]) as PdfDict, f);
                if (cid != null)
                {
                    f.Default = doc.IntOf(cid, "DW", 1000);
                    var w = doc.Resolve(cid["W"]) as PdfArray;
                    if (w != null && f.Mode == CodeMode.Identity)
                    {
                        int i = 0;
                        while (i < w.Items.Count)
                        {
                            var first = doc.Resolve(w.Items[i]) as PdfNum;
                            if (first == null || i + 1 >= w.Items.Count) break;
                            var next = doc.Resolve(w.Items[i + 1]);
                            var arr = next as PdfArray;
                            if (arr != null)
                            {
                                for (int k = 0; k < arr.Items.Count; k++)
                                {
                                    var n = doc.Resolve(arr.Items[k]) as PdfNum;
                                    if (n != null) f.Widths[first.I + k] = n.V;
                                }
                                i += 2;
                            }
                            else
                            {
                                if (i + 2 >= w.Items.Count) break;
                                var lastNum = next as PdfNum;
                                var width = doc.Resolve(w.Items[i + 2]) as PdfNum;
                                if (lastNum != null && width != null && lastNum.I - first.I < 70000)
                                {
                                    for (int k = first.I; k <= lastNum.I; k++) f.Widths[k] = width.V;
                                }
                                i += 3;
                            }
                        }
                    }
                }
                if (f.Mode == CodeMode.Identity && f.ToUni.Count == 0) f.Unknown = true;
                return f;
            }
            // 한 글자 1바이트 글꼴
            f.Default = 556;
            var desc = doc.Resolve(font["FontDescriptor"]) as PdfDict;
            ReadFace(doc, font, desc, f);
            if (desc != null)
            {
                int missing = doc.IntOf(desc, "MissingWidth", 0);
                if (missing > 0) f.Default = missing;
            }
            int firstChar = doc.IntOf(font, "FirstChar", 0);
            var widths = doc.Resolve(font["Widths"]) as PdfArray;
            if (widths != null)
            {
                for (int i = 0; i < widths.Items.Count; i++)
                {
                    var n = doc.Resolve(widths.Items[i]) as PdfNum;
                    if (n != null) f.Widths[firstChar + i] = n.V;
                }
            }
            f.Simple = new string[256];
            string baseName = enc is PdfName ? ((PdfName)enc).V : null;
            var encDict = enc as PdfDict;
            if (encDict != null) baseName = encDict.NameOf("BaseEncoding");
            Encoding table = baseName == "MacRomanEncoding" ? Mac : Win1252;
            var one = new byte[1];
            for (int i = 32; i < 256; i++)
            {
                one[0] = (byte)i;
                f.Simple[i] = table.GetString(one);
            }
            bool embedded = desc != null && (desc["FontFile"] != null || desc["FontFile2"] != null || desc["FontFile3"] != null);
            if (embedded && baseName == null && f.ToUni.Count == 0 && encDict == null) f.Unknown = true;
            if (encDict != null)
            {
                var diff = doc.Resolve(encDict["Differences"]) as PdfArray;
                if (diff != null)
                {
                    int code = 0;
                    foreach (var item in diff.Items)
                    {
                        var r = doc.Resolve(item);
                        if (r is PdfNum) code = ((PdfNum)r).I;
                        else if (r is PdfName)
                        {
                            if (code >= 0 && code < 256) f.Simple[code] = FromGlyphName(((PdfName)r).V);
                            code++;
                        }
                    }
                }
            }
            return f;
        }

        // ── 내용 흐름 ──

        sealed class Walker
        {
            readonly PdfDocument doc;
            readonly Dictionary<PdfDict, TextFont> fonts;
            /// null이 아니면 글자를 PPT로 옮기는 중: 뺀 글자의 자리를 기억해 글자 없는 내용 흐름을 만든다.
            readonly Dictionary<int, byte[]> forms;
            readonly List<PdfTextRun> runs = new List<PdfTextRun>();
            PdfPageText page;
            int rotate;
            double x0, y0, x1, y1;

            double[] ctm = { 1, 0, 0, 1, 0, 0 };
            double[] tm = { 1, 0, 0, 1, 0, 0 };
            double[] tlm = { 1, 0, 0, 1, 0, 0 };
            TextFont font;
            double size, tc, tw, th = 1, tl, rise;
            int fillRgb, textMode;
            bool inkColor;

            sealed class Saved
            {
                public double[] Ctm;
                public TextFont Font;
                public double Size, Tc, Tw, Th, Tl, Rise;
                public int Rgb, Mode;
                public bool Ink;
            }

            sealed class Cut
            {
                public int From, To;
                public string With;
            }

            readonly Stack<Saved> stack = new Stack<Saved>();

            public Walker(PdfDocument doc, Dictionary<PdfDict, TextFont> fonts) : this(doc, fonts, null) { }

            public Walker(PdfDocument doc, Dictionary<PdfDict, TextFont> fonts, Dictionary<int, byte[]> forms)
            {
                this.doc = doc;
                this.fonts = fonts;
                this.forms = forms;
            }

            public PdfPageText Page(PdfDict dict)
            {
                page = new PdfPageText();
                var media = PdfPageSize.Box(doc, PdfPageSize.Inherited(doc, dict, "MediaBox")) ?? new[] { 0, 0, 595.276, 841.89 };
                var crop = PdfPageSize.Box(doc, PdfPageSize.Inherited(doc, dict, "CropBox"));
                if (crop != null && crop[2] > crop[0] && crop[3] > crop[1]) media = crop;
                x0 = media[0];
                y0 = media[1];
                x1 = media[2];
                y1 = media[3];
                var rot = PdfPageSize.Inherited(doc, dict, "Rotate") as PdfNum;
                rotate = rot == null ? 0 : ((rot.I % 360) + 360) % 360;
                page.Width = rotate == 90 || rotate == 270 ? y1 - y0 : x1 - x0;
                page.Height = rotate == 90 || rotate == 270 ? x1 - x0 : y1 - y0;
                var res = doc.Resolve(PdfPageSize.Inherited(doc, dict, "Resources")) as PdfDict ?? new PdfDict();
                var content = new List<byte>();
                var c = doc.Resolve(dict["Contents"]);
                var list = c as PdfArray;
                if (list != null)
                {
                    foreach (var item in list.Items) AddStream(doc.Resolve(item) as PdfStream, content);
                }
                else AddStream(c as PdfStream, content);
                var whole = content.ToArray();
                var cuts = forms == null ? null : new List<Cut>();
                Run(whole, res, 0, cuts);
                if (cuts != null && cuts.Count > 0) page.Stripped = Apply(whole, cuts);
                Assemble();
                return page;
            }

            void AddStream(PdfStream s, List<byte> into)
            {
                if (s == null) return;
                try
                {
                    into.AddRange(doc.Decode(s));
                    into.Add(10);
                }
                catch (PdfException) { }
            }

            static double[] Mul(double[] a, double[] b)
            {
                return new[]
                {
                    a[0] * b[0] + a[1] * b[2], a[0] * b[1] + a[1] * b[3],
                    a[2] * b[0] + a[3] * b[2], a[2] * b[1] + a[3] * b[3],
                    a[4] * b[0] + a[5] * b[2] + b[4], a[4] * b[1] + a[5] * b[3] + b[5]
                };
            }

            static double[] Move(double x, double y)
            {
                return new double[] { 1, 0, 0, 1, x, y };
            }

            static double N(List<PdfObj> a, int i)
            {
                var n = i >= 0 && i < a.Count ? a[i] as PdfNum : null;
                return n == null ? 0 : n.V;
            }

            void NextLine(double x, double y)
            {
                tlm = Mul(Move(x, y), tlm);
                tm = tlm;
            }

            static byte[] Apply(byte[] data, List<Cut> cuts)
            {
                using (var o = new System.IO.MemoryStream(data.Length))
                {
                    int at = 0;
                    foreach (var c in cuts.OrderBy(x => x.From))
                    {
                        if (c.From < at) continue;
                        o.Write(data, at, c.From - at);
                        if (c.With != null)
                        {
                            byte[] w = Encoding.ASCII.GetBytes(c.With);
                            o.Write(w, 0, w.Length);
                        }
                        o.WriteByte(10);
                        at = c.To;
                    }
                    o.Write(data, at, data.Length - at);
                    return o.ToArray();
                }
            }

            static int Channel(double v)
            {
                return Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));
            }

            static int Color(double r, double g, double b)
            {
                return (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
            }

            static int Cmyk(double c, double m, double y, double k)
            {
                return Color((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
            }

            void SetFill(List<PdfObj> a)
            {
                // 무늬 색은 이름으로 끝난다. 그대로 두면 앞 색을 쓴다.
                if (a.Count == 0 || !(a[a.Count - 1] is PdfNum)) return;
                if (a.Count == 1) fillRgb = inkColor ? Color(1 - N(a, 0), 1 - N(a, 0), 1 - N(a, 0)) : Color(N(a, 0), N(a, 0), N(a, 0));
                else if (a.Count == 3) fillRgb = Color(N(a, 0), N(a, 1), N(a, 2));
                else if (a.Count == 4) fillRgb = Cmyk(N(a, 0), N(a, 1), N(a, 2), N(a, 3));
            }

            void SetSpace(PdfDict res, PdfName name)
            {
                inkColor = false;
                fillRgb = 0;
                if (name == null) return;
                var spaces = doc.Resolve(res["ColorSpace"]) as PdfDict;
                var cs = spaces == null ? null : doc.Resolve(spaces[name.V]) as PdfArray;
                var family = cs != null && cs.Items.Count > 0 ? doc.Resolve(cs.Items[0]) as PdfName : null;
                if (family != null && (family.V == "Separation" || family.V == "DeviceN")) inkColor = true;
            }

            void Run(byte[] data, PdfDict res, int depth, List<Cut> cuts)
            {
                var lx = new PdfLexer(data, 0, data.Length);
                var a = new List<PdfObj>();
                int argStart = 0;
                while (true)
                {
                    lx.SkipWs();
                    int c = lx.Peek();
                    if (c < 0) break;
                    if (c == '/' || c == '(' || c == '<' || c == '[' || c == '+' || c == '-' || c == '.' || (c >= '0' && c <= '9'))
                    {
                        if (a.Count == 0) argStart = lx.Pos;
                        try
                        {
                            a.Add(lx.Read());
                        }
                        catch (PdfException)
                        {
                            lx.Pos++;
                        }
                        continue;
                    }
                    int opAt = lx.Pos;
                    string op = lx.Token();
                    if (op.Length == 0)
                    {
                        lx.Pos++;
                        continue;
                    }
                    if (a.Count == 0) argStart = opAt;
                    switch (op)
                    {
                        case "q":
                            stack.Push(new Saved { Ctm = ctm, Font = font, Size = size, Tc = tc, Tw = tw, Th = th, Tl = tl, Rise = rise, Rgb = fillRgb, Mode = textMode, Ink = inkColor });
                            break;
                        case "Q":
                            if (stack.Count > 0)
                            {
                                var s = stack.Pop();
                                ctm = s.Ctm;
                                font = s.Font;
                                size = s.Size;
                                tc = s.Tc;
                                tw = s.Tw;
                                th = s.Th;
                                tl = s.Tl;
                                rise = s.Rise;
                                fillRgb = s.Rgb;
                                textMode = s.Mode;
                                inkColor = s.Ink;
                            }
                            break;
                        case "g":
                            inkColor = false;
                            fillRgb = Color(N(a, 0), N(a, 0), N(a, 0));
                            break;
                        case "rg":
                            inkColor = false;
                            fillRgb = Color(N(a, 0), N(a, 1), N(a, 2));
                            break;
                        case "k":
                            inkColor = false;
                            fillRgb = Cmyk(N(a, 0), N(a, 1), N(a, 2), N(a, 3));
                            break;
                        case "cs":
                            SetSpace(res, a.Count > 0 ? a[a.Count - 1] as PdfName : null);
                            break;
                        case "sc":
                        case "scn":
                            SetFill(a);
                            break;
                        case "Tr":
                            textMode = (int)N(a, 0);
                            break;
                        case "cm":
                            if (a.Count >= 6) ctm = Mul(new[] { N(a, 0), N(a, 1), N(a, 2), N(a, 3), N(a, 4), N(a, 5) }, ctm);
                            break;
                        case "BT":
                            tm = new double[] { 1, 0, 0, 1, 0, 0 };
                            tlm = tm;
                            break;
                        case "Tf":
                            if (a.Count >= 2)
                            {
                                size = N(a, 1);
                                font = FontOf(res, a[0] as PdfName);
                            }
                            break;
                        case "Tc": tc = N(a, 0); break;
                        case "Tw": tw = N(a, 0); break;
                        case "Tz": th = N(a, 0) / 100; break;
                        case "TL": tl = N(a, 0); break;
                        case "Ts": rise = N(a, 0); break;
                        case "Td": NextLine(N(a, 0), N(a, 1)); break;
                        case "TD":
                            tl = -N(a, 1);
                            NextLine(N(a, 0), N(a, 1));
                            break;
                        case "Tm":
                            if (a.Count >= 6)
                            {
                                tm = new[] { N(a, 0), N(a, 1), N(a, 2), N(a, 3), N(a, 4), N(a, 5) };
                                tlm = tm;
                            }
                            break;
                        case "T*": NextLine(0, -tl); break;
                        case "Tj":
                            if (a.Count > 0 && a[a.Count - 1] is PdfString && Show(((PdfString)a[a.Count - 1]).V) && cuts != null) cuts.Add(new Cut { From = argStart, To = lx.Pos });
                            break;
                        case "'":
                            NextLine(0, -tl);
                            if (a.Count > 0 && a[a.Count - 1] is PdfString && Show(((PdfString)a[a.Count - 1]).V) && cuts != null) cuts.Add(new Cut { From = argStart, To = lx.Pos, With = "T*" });
                            break;
                        case "\"":
                            if (a.Count >= 3)
                            {
                                tw = N(a, 0);
                                tc = N(a, 1);
                            }
                            NextLine(0, -tl);
                            if (a.Count > 0 && a[a.Count - 1] is PdfString && Show(((PdfString)a[a.Count - 1]).V) && cuts != null)
                            {
                                cuts.Add(new Cut { From = argStart, To = lx.Pos, With = PdfNum.Fmt(tw) + " Tw " + PdfNum.Fmt(tc) + " Tc T*" });
                            }
                            break;
                        case "TJ":
                            {
                                var arr = a.Count > 0 ? a[a.Count - 1] as PdfArray : null;
                                if (arr == null) break;
                                int before = runs.Count, chars = page.Chars, bad = page.Bad;
                                bool all = true;
                                foreach (var item in arr.Items)
                                {
                                    var str = item as PdfString;
                                    if (str != null) all &= Show(str.V);
                                    else if (item is PdfNum) tm = Mul(Move(-((PdfNum)item).V / 1000 * size * th, 0), tm);
                                }
                                if (cuts != null)
                                {
                                    if (all) cuts.Add(new Cut { From = argStart, To = lx.Pos });
                                    else if (runs.Count > before) runs.RemoveRange(before, runs.Count - before);
                                }
                                break;
                            }
                        case "Do":
                            if (a.Count > 0 && depth < 6) Form(res, a[a.Count - 1] as PdfName, depth);
                            break;
                        case "ID":
                            SkipInlineImage(lx, data);
                            page.Images++;
                            break;
                    }
                    a.Clear();
                }
            }

            static void SkipInlineImage(PdfLexer lx, byte[] d)
            {
                int p = lx.Pos + 1;
                for (; p + 2 < d.Length; p++)
                {
                    if (d[p] == 'E' && d[p + 1] == 'I' && PdfLexer.IsWs(d[p - 1]) && (p + 2 >= d.Length || PdfLexer.IsWs(d[p + 2])))
                    {
                        lx.Pos = p + 2;
                        return;
                    }
                }
                lx.Pos = d.Length;
            }

            void Form(PdfDict res, PdfName name, int depth)
            {
                if (name == null) return;
                var xo = doc.Resolve(res["XObject"]) as PdfDict;
                var st = xo == null ? null : doc.Resolve(xo[name.V]) as PdfStream;
                if (st == null) return;
                var formRef = xo[name.V] as PdfRef;
                string sub = st.Dict.NameOf("Subtype");
                if (sub == "Image")
                {
                    page.Images++;
                    return;
                }
                if (sub != "Form") return;
                byte[] data;
                try
                {
                    data = doc.Decode(st);
                }
                catch (PdfException)
                {
                    return;
                }
                var savedCtm = ctm;
                var savedFont = font;
                double savedSize = size, savedTc = tc, savedTw = tw, savedTh = th, savedTl = tl, savedRise = rise;
                var savedTm = tm;
                var savedTlm = tlm;
                var m = doc.Resolve(st.Dict["Matrix"]) as PdfArray;
                if (m != null && m.Items.Count >= 6)
                {
                    var v = m.Items.Select(x => doc.Resolve(x) as PdfNum).Select(x => x == null ? 0 : x.V).ToArray();
                    ctm = Mul(v, ctm);
                }
                var own = doc.Resolve(st.Dict["Resources"]) as PdfDict;
                var inner = forms == null ? null : new List<Cut>();
                Run(data, own ?? res, depth + 1, inner);
                if (inner != null && inner.Count > 0 && formRef != null && !forms.ContainsKey(formRef.Num)) forms[formRef.Num] = Apply(data, inner);
                ctm = savedCtm;
                font = savedFont;
                size = savedSize;
                tc = savedTc;
                tw = savedTw;
                th = savedTh;
                tl = savedTl;
                rise = savedRise;
                tm = savedTm;
                tlm = savedTlm;
            }

            TextFont FontOf(PdfDict res, PdfName name)
            {
                if (name == null) return null;
                var dict = doc.Resolve(res["Font"]) as PdfDict;
                var f = dict == null ? null : doc.Resolve(dict[name.V]) as PdfDict;
                if (f == null) return null;
                TextFont tf;
                if (!fonts.TryGetValue(f, out tf))
                {
                    tf = LoadFont(doc, f);
                    fonts[f] = tf;
                }
                return tf;
            }

            bool Good(string s)
            {
                if (string.IsNullOrEmpty(s)) return false;
                foreach (char c in s)
                {
                    if (c == 0xFFFD || c == 0 || (c >= 0xE000 && c <= 0xF8FF) || (c < 0x20 && c != '\t' && c != '\n')) return false;
                }
                return true;
            }

            /// 글자 한 토막을 읽는다. 글자를 PPT로 옮기는 중이면, 이 토막을 글상자로 옮겨도 되는지(배경에서 빼도 되는지) 돌려준다.
            bool Show(byte[] b)
            {
                if (font == null) return false;
                var sb = new StringBuilder();
                double adv = 0;
                int i = 0;
                bool clean = true;
                while (i < b.Length)
                {
                    int code, len;
                    string u = null;
                    switch (font.Mode)
                    {
                        case CodeMode.Identity:
                        case CodeMode.Ucs2:
                            code = i + 1 < b.Length ? (b[i] << 8) | b[i + 1] : b[i];
                            len = 2;
                            break;
                        case CodeMode.Cp949:
                            if (b[i] >= 0x81 && i + 1 < b.Length)
                            {
                                code = (b[i] << 8) | b[i + 1];
                                len = 2;
                                if (!font.ToUni.ContainsKey(code)) u = Korean.GetString(b, i, 2);
                            }
                            else
                            {
                                code = b[i];
                                len = 1;
                                if (!font.ToUni.ContainsKey(code)) u = ((char)code).ToString();
                            }
                            break;
                        default:
                            code = b[i];
                            len = 1;
                            break;
                    }
                    string mapped;
                    if (font.ToUni.TryGetValue(code, out mapped)) u = mapped;
                    else if (font.Mode == CodeMode.Ucs2) u = ((char)code).ToString();
                    else if (font.Mode == CodeMode.Single && font.Simple != null && !font.Unknown) u = font.Simple[code & 255];
                    bool space = u == " " || (font.Mode == CodeMode.Single && code == 32);
                    if (!space)
                    {
                        page.Chars++;
                        if (!Good(u))
                        {
                            page.Bad++;
                            clean = false;
                        }
                    }
                    if (Good(u)) sb.Append(u);
                    double w;
                    if (!font.Widths.TryGetValue(code, out w))
                    {
                        w = font.Default;
                        if (font.Mode != CodeMode.Single && font.Mode != CodeMode.Identity && code < 0x80) w = 500;
                    }
                    adv += (w / 1000 * size + tc + (len == 1 && code == 32 ? tw : 0)) * th;
                    i += len;
                }
                var trm = Mul(tm, ctm);
                var endM = Mul(Mul(Move(adv, 0), tm), ctm);
                string text = sb.ToString();
                bool layout = forms != null;
                bool hidden = layout && textMode == 3;
                bool level = !layout || Level(trm);
                // 윤곽선만 그린 글자(1), 오려 내기에 쓰는 글자(4~7)는 그림의 일부로 남겨 둔다.
                bool usable = text.Trim().Length > 0 && clean && level && !hidden && textMode != 1 && textMode < 4;
                if (text.Trim().Length > 0 && (!layout || usable || hidden))
                {
                    double vx, vy, ex, ey;
                    View(trm[4], trm[5], out vx, out vy);
                    View(endM[4], endM[5], out ex, out ey);
                    double fs = Math.Abs(size) * Math.Sqrt(trm[2] * trm[2] + trm[3] * trm[3]);
                    if (fs < 0.5) fs = Math.Abs(size);
                    var run = new PdfTextRun { X = vx, Y = vy, End = Math.Max(vx, ex), Size = fs, S = text, Font = font.Name, Bold = font.Bold || textMode == 2, Italic = font.Italic, Serif = font.Serif, Rgb = fillRgb };
                    if (hidden) page.HiddenRuns.Add(run);
                    else runs.Add(run);
                }
                tm = Mul(Move(adv, 0), tm);
                return usable;
            }

            /// 보이는 쪽에서 수평인 글자인가(세로·비스듬한 글자는 배경에 남긴다)
            bool Level(double[] trm)
            {
                double angle = Math.Atan2(trm[1], trm[0]) * 180 / Math.PI + rotate;
                while (angle > 180) angle -= 360;
                while (angle <= -180) angle += 360;
                return Math.Abs(angle) < 1.5 && trm[0] * trm[3] - trm[1] * trm[2] > 0;
            }

            void View(double x, double y, out double vx, out double vy)
            {
                switch (rotate)
                {
                    case 90:
                        vx = y - y0;
                        vy = x1 - x;
                        break;
                    case 180:
                        vx = x1 - x;
                        vy = y1 - y;
                        break;
                    case 270:
                        vx = y1 - y;
                        vy = x - x0;
                        break;
                    default:
                        vx = x - x0;
                        vy = y - y0;
                        break;
                }
            }

            void Assemble()
            {
                foreach (var r in runs.OrderByDescending(r => r.Y).ThenBy(r => r.X))
                {
                    PdfTextLine line = null;
                    for (int i = page.Lines.Count - 1; i >= 0 && i >= page.Lines.Count - 4; i--)
                    {
                        var l = page.Lines[i];
                        if (Math.Abs(l.Y - r.Y) <= Math.Max(l.Size, r.Size) * 0.45)
                        {
                            line = l;
                            break;
                        }
                    }
                    if (line == null)
                    {
                        line = new PdfTextLine { Y = r.Y, Size = r.Size };
                        page.Lines.Add(line);
                    }
                    line.Runs.Add(r);
                }
                foreach (var l in page.Lines) l.Runs.Sort((a, b) => a.X.CompareTo(b.X));
            }
        }
    }
}
