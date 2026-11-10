// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 글자 문서(마크다운·HTML·전자책·글자 파일, 프로그램이 없을 때의 워드·표)를 PDF로 만든다.
// PC에 있는 글꼴에서 실제로 쓴 글자만 떼어 넣어, 파일이 작고 글자를 고르거나 찾을 수 있는 진짜 PDF가 된다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Bakkum
{
    public static class TextPdf
    {
        const double PageW = 595.276, PageH = 841.89, MarginX = 62, MarginY = 60;
        const double TextW = PageW - 2 * MarginX, Body = PageH - 2 * MarginY;
        const uint Ink = 0x22252F, Rule = 0xB9BDC7, Shade = 0xEEF0F4, DelColor = 0xC0392B, AddColor = 0x1F6FEB;

        public static void Write(string path, IList<DocBlock> blocks, string title, Job job)
        {
            var l = new Layout(job);
            l.Flow(blocks);
            if (l.Empty) throw new UserFacingException("옮길 글자를 찾지 못했어요.");
            l.Save(path, title);
            using (var check = new WinPdfDoc(path))
            {
                if (check.PageCount != l.PageCount) throw new UserFacingException("만든 PDF를 확인해 보니 쪽 수가 맞지 않아 저장하지 않았어요.");
                check.RenderPng(0, 24, 300);
            }
        }

        sealed class Face
        {
            public GlyphTypeface G;
            public string Name;
            public int Index;
            public int Obj;
            public readonly SortedDictionary<ushort, int> Used = new SortedDictionary<ushort, int>();
        }

        struct Cell
        {
            public Face F;
            public ushort Gid;
            public double W;
            public uint Rgb;
            public byte Deco;
            public bool Space;
        }

        sealed class Fonts
        {
            // 앞에서부터 그 글자가 있는 글꼴을 쓴다.
            static readonly string[] Chain = { "Malgun Gothic", "Segoe UI", "Microsoft YaHei", "Yu Gothic", "Segoe UI Symbol", "Segoe UI Emoji", "Batang" };
            public readonly List<Face> All = new List<Face>();
            readonly Dictionary<string, Face> byKey = new Dictionary<string, Face>();

            Face Get(string family, bool bold)
            {
                string key = family + (bold ? "|b" : "");
                Face f;
                if (byKey.TryGetValue(key, out f)) return f;
                GlyphTypeface g;
                var tf = new Typeface(new FontFamily(family), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                if (tf.TryGetGlyphTypeface(out g) && CanEmbed(g))
                {
                    f = new Face { G = g, Name = family.Replace(" ", "") + (bold ? "-Bold" : ""), Index = All.Count };
                    All.Add(f);
                }
                byKey[key] = f;
                return f;
            }

            static bool CanEmbed(GlyphTypeface g)
            {
                string r = g.EmbeddingRights.ToString();
                return r != "RestrictedLicense" && r.IndexOf("NoSubsetting", StringComparison.Ordinal) < 0 && r.IndexOf("BitmapsOnly", StringComparison.Ordinal) < 0;
            }

            public Face For(int cp, bool bold, out ushort gid)
            {
                Face first = null;
                foreach (string family in Chain)
                {
                    var f = Get(family, bold);
                    if (f == null) continue;
                    if (first == null) first = f;
                    if (f.G.CharacterToGlyphMap.TryGetValue(cp, out gid)) return f;
                }
                if (first == null) throw new UserFacingException("PDF에 넣을 글꼴을 찾지 못했어요.");
                gid = 0;
                return first;
            }
        }

        sealed class Layout
        {
            readonly Job job;
            readonly Fonts fonts = new Fonts();
            readonly List<StringBuilder> pages = new List<StringBuilder>();
            readonly List<HashSet<int>> pageImages = new List<HashSet<int>>();
            readonly List<PdfStream> images = new List<PdfStream>();
            StringBuilder C;
            double y;
            bool fresh;
            public bool Empty = true;

            public Layout(Job j)
            {
                job = j;
            }

            public int PageCount
            {
                get { return pages.Count; }
            }

            void NewPage()
            {
                C = new StringBuilder();
                pages.Add(C);
                pageImages.Add(new HashSet<int>());
                y = PageH - MarginY;
                fresh = true;
            }

            static string F(double v)
            {
                return PdfNum.Fmt(v);
            }

            static string Rg(uint c, string op)
            {
                return F(((c >> 16) & 255) / 255.0) + " " + F(((c >> 8) & 255) / 255.0) + " " + F((c & 255) / 255.0) + " " + op + " ";
            }

            // ── 글자 잇기와 줄 나누기 ──

            Cell Make(int cp, double size, bool bold, uint rgb, byte deco)
            {
                ushort gid;
                var f = fonts.For(cp, bold, out gid);
                int have;
                if (!f.Used.TryGetValue(gid, out have)) f.Used[gid] = cp;
                return new Cell { F = f, Gid = gid, W = f.G.AdvanceWidths[gid] * size, Rgb = rgb, Deco = deco, Space = cp == ' ' };
            }

            /// 한자·가나는 글자 사이에서 줄을 바꿔도 된다. 한글·영문은 띄어쓰기에서 바꾼다.
            static bool BreakAnywhere(int cp)
            {
                return (cp >= 0x3040 && cp <= 0x30FF) || (cp >= 0x3400 && cp <= 0x9FFF) || (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0x20000 && cp <= 0x2FFFF);
            }

            static string Compose(string s)
            {
                try { return s.Normalize(NormalizationForm.FormC); }
                catch (ArgumentException) { return s; }
            }

            List<List<Cell>> Wrap(string text, double size, bool bold, double width, uint color)
            {
                var lines = new List<List<Cell>>();
                var line = new List<Cell>();
                var word = new List<Cell>();
                double lw = 0, ww = 0;
                Action push = delegate
                {
                    while (line.Count > 0 && line[line.Count - 1].Space)
                    {
                        lw -= line[line.Count - 1].W;
                        line.RemoveAt(line.Count - 1);
                    }
                    lines.Add(line);
                    line = new List<Cell>();
                    lw = 0;
                };
                Action commit = delegate
                {
                    if (word.Count == 0) return;
                    if (lw + ww > width && line.Count > 0) push();
                    if (ww > width)
                    {
                        foreach (var c in word)
                        {
                            if (lw + c.W > width && line.Count > 0) push();
                            line.Add(c);
                            lw += c.W;
                        }
                    }
                    else
                    {
                        line.AddRange(word);
                        lw += ww;
                    }
                    word.Clear();
                    ww = 0;
                };
                foreach (var run in Inline.Split(text))
                {
                    string s = Compose(run.Text);
                    uint rgb = run.Del ? DelColor : run.Add ? AddColor : color;
                    byte deco = (byte)(run.Del ? 1 : run.Add ? 2 : 0);
                    bool b = bold || run.Bold;
                    for (int i = 0; i < s.Length; i++)
                    {
                        int cp = s[i];
                        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            cp = char.ConvertToUtf32(s[i], s[i + 1]);
                            i++;
                        }
                        else if (char.IsSurrogate(s[i])) continue;
                        if (cp == '\n')
                        {
                            commit();
                            push();
                            continue;
                        }
                        if (cp == ' ' || cp == '\t' || cp == 0xA0 || cp == 0x3000)
                        {
                            commit();
                            if (line.Count == 0) continue;
                            int n = cp == '\t' ? 4 : 1;
                            for (int k = 0; k < n; k++)
                            {
                                var sp = Make(' ', size, b, rgb, deco);
                                line.Add(sp);
                                lw += sp.W;
                            }
                            continue;
                        }
                        if (cp < 0x20 || (cp >= 0x7F && cp < 0xA0) || cp == 0xFEFF || cp == 0x200B) continue;
                        var c = Make(cp, size, b, rgb, deco);
                        word.Add(c);
                        ww += c.W;
                        if (BreakAnywhere(cp)) commit();
                    }
                }
                commit();
                if (line.Count > 0 || lines.Count == 0) push();
                return lines;
            }

            static double WidthOf(List<Cell> line)
            {
                double w = 0;
                foreach (var c in line) w += c.W;
                return w;
            }

            void Draw(List<Cell> line, double x, double baseline, double size)
            {
                int i = 0;
                double cx = x;
                while (i < line.Count)
                {
                    int j = i;
                    double w = 0;
                    var sb = new StringBuilder();
                    while (j < line.Count && line[j].F == line[i].F && line[j].Rgb == line[i].Rgb && line[j].Deco == line[i].Deco)
                    {
                        sb.Append(line[j].Gid.ToString("X4"));
                        w += line[j].W;
                        j++;
                    }
                    C.Append("BT ").Append(Rg(line[i].Rgb, "rg")).Append("/F").Append(line[i].F.Index).Append(' ').Append(F(size)).Append(" Tf ")
                     .Append(F(cx)).Append(' ').Append(F(baseline)).Append(" Td <").Append(sb).Append("> Tj ET\n");
                    if (line[i].Deco != 0)
                    {
                        double ly = line[i].Deco == 1 ? baseline + size * 0.3 : baseline - size * 0.12;
                        C.Append(Rg(line[i].Rgb, "rg")).Append(F(cx)).Append(' ').Append(F(ly)).Append(' ').Append(F(w)).Append(' ').Append(F(Math.Max(0.5, size * 0.05))).Append(" re f\n");
                    }
                    cx += w;
                    i = j;
                }
                if (line.Count > 0)
                {
                    fresh = false;
                    Empty = false;
                }
            }

            // ── 덩어리마다 ──

            public void Flow(IList<DocBlock> blocks)
            {
                NewPage();
                foreach (var b in blocks)
                {
                    job.Check();
                    switch (b.Kind)
                    {
                        case BlockKind.Heading:
                            Para(b.Text, b.Level <= 1 ? 21 : b.Level == 2 ? 16 : 13, true, 0, b.Level <= 1 ? 18 : 14, b.Level <= 1 ? 9 : 6, null, 1.35, true);
                            break;
                        case BlockKind.Para:
                            Para(b.Text, 11, false, b.Level > 1 ? 18 : 0, 0, 7, null, 1.65, false);
                            break;
                        case BlockKind.Bullet:
                            Para(b.Text, 11, false, 16 * b.Level, 0, 4, "•", 1.6, false);
                            break;
                        case BlockKind.Number:
                            Para(b.Text, 11, false, 22 * b.Level, 0, 4, b.Number + ".", 1.6, false);
                            break;
                        case BlockKind.Table:
                            TableBlock(b.Rows);
                            break;
                        case BlockKind.Picture:
                            PictureBlock(b);
                            break;
                        case BlockKind.PageBreak:
                            if (!fresh) NewPage();
                            break;
                    }
                }
                if (fresh && pages.Count > 1)
                {
                    pages.RemoveAt(pages.Count - 1);
                    pageImages.RemoveAt(pageImages.Count - 1);
                }
            }

            void Para(string text, double size, bool bold, double indent, double before, double after, string marker, double lead, bool keepNext)
            {
                var lines = Wrap(text, size, bold, TextW - indent, Ink);
                double lh = size * lead;
                if (!fresh) y -= before;
                if (keepNext && y - lh * (lines.Count + 2) < MarginY && !fresh) NewPage();
                bool first = true;
                foreach (var line in lines)
                {
                    if (y - lh < MarginY) NewPage();
                    double baseline = y - lh * 0.5 - size * 0.35;
                    if (first && marker != null)
                    {
                        var m = Wrap(marker, size, false, 1e6, Ink)[0];
                        Draw(m, MarginX + indent - WidthOf(m) - 5, baseline, size);
                    }
                    first = false;
                    Draw(line, MarginX + indent, baseline, size);
                    y -= lh;
                }
                y -= after;
            }

            void TableBlock(List<List<string>> rows)
            {
                int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
                if (cols == 0) return;
                const double size = 10, pad = 5, lh = size * 1.45;
                var natural = new double[cols];
                for (int r = 0; r < rows.Count; r++)
                {
                    job.Check();
                    for (int c = 0; c < rows[r].Count; c++)
                    {
                        foreach (var l in Wrap(rows[r][c], size, r == 0, 1e6, Ink)) natural[c] = Math.Max(natural[c], WidthOf(l) + 2 * pad);
                    }
                }
                for (int c = 0; c < cols; c++) natural[c] = Math.Max(natural[c], 2 * pad + size * 2);
                var w = new double[cols];
                double sum = natural.Sum();
                if (sum <= TextW)
                {
                    for (int c = 0; c < cols; c++) w[c] = natural[c] * TextW / sum;
                }
                else
                {
                    // 넘치면 좁은 칸은 그대로 두고 넓은 칸끼리 남은 폭을 나눈다.
                    var open = Enumerable.Range(0, cols).ToList();
                    double rest = TextW;
                    bool changed = true;
                    while (changed && open.Count > 0)
                    {
                        changed = false;
                        double fair = rest / open.Count;
                        foreach (int c in open.ToList())
                        {
                            if (natural[c] > fair) continue;
                            w[c] = natural[c];
                            rest -= natural[c];
                            open.Remove(c);
                            changed = true;
                        }
                    }
                    foreach (int c in open) w[c] = rest / open.Count;
                }
                var cells = new List<List<List<Cell>>[]>();
                for (int r = 0; r < rows.Count; r++)
                {
                    job.Check();
                    var row = new List<List<Cell>>[cols];
                    for (int c = 0; c < cols; c++) row[c] = Wrap(c < rows[r].Count ? rows[r][c] : "", size, r == 0, w[c] - 2 * pad, Ink);
                    cells.Add(row);
                }
                Action<int, int, int> drawRow = delegate (int r, int from, int take)
                {
                    double h = take * lh + 2 * pad;
                    double x = MarginX;
                    for (int c = 0; c < cols; c++)
                    {
                        if (r == 0) C.Append(Rg(Shade, "rg")).Append(F(x)).Append(' ').Append(F(y - h)).Append(' ').Append(F(w[c])).Append(' ').Append(F(h)).Append(" re f\n");
                        var part = cells[r][c];
                        for (int i = from; i < Math.Min(from + take, part.Count); i++)
                        {
                            double top = y - pad - (i - from) * lh;
                            Draw(part[i], x + pad, top - lh * 0.5 - size * 0.35, size);
                        }
                        C.Append(Rg(Rule, "RG")).Append("0.5 w ").Append(F(x)).Append(' ').Append(F(y - h)).Append(' ').Append(F(w[c])).Append(' ').Append(F(h)).Append(" re S\n");
                        x += w[c];
                    }
                    y -= h;
                    fresh = false;
                };
                double wholePage = Body - 2 * pad;
                for (int r = 0; r < rows.Count; r++)
                {
                    int total = cells[r].Max(c => c.Count);
                    int done = 0;
                    while (done < total)
                    {
                        int left = total - done;
                        int fit = (int)Math.Floor((y - MarginY - 2 * pad) / lh);
                        // 다음 쪽에 통째로 들어가는 줄은 나누지 않는다. 쪽보다 긴 줄만 나눈다.
                        if (!fresh && fit < left && (left * lh <= wholePage || fit < 2))
                        {
                            NewPage();
                            if (r > 0) drawRow(0, 0, cells[0].Max(c => c.Count));
                            continue;
                        }
                        int take = Math.Min(Math.Max(fit, 1), left);
                        drawRow(r, done, take);
                        done += take;
                    }
                }
                y -= 10;
            }

            void PictureBlock(DocBlock b)
            {
                if (b.Image == null || b.W <= 0 || b.H <= 0) return;
                double scale = Math.Min(0.75, Math.Min(TextW / b.W, Body / b.H));
                double dw = b.W * scale, dh = b.H * scale;
                if (y - dh < MarginY && !fresh) NewPage();
                int id = images.Count;
                images.Add(ImageOf(b));
                pageImages[pageImages.Count - 1].Add(id);
                C.Append("q ").Append(F(dw)).Append(" 0 0 ").Append(F(dh)).Append(' ').Append(F(MarginX + (TextW - dw) / 2)).Append(' ').Append(F(y - dh)).Append(" cm /Im").Append(id).Append(" Do Q\n");
                y -= dh + 8;
                fresh = false;
                Empty = false;
            }

            static PdfStream ImageOf(DocBlock b)
            {
                byte[] data = b.Image;
                int w = b.W, h = b.H;
                bool gray = false;
                var ji = Imaging.ReadJpeg(data);
                if (ji != null && ji.Orientation == 1 && (ji.Components == 1 || ji.Components == 3) && ji.Precision == 8 && !ji.Arithmetic)
                {
                    gray = ji.Components == 1;
                    w = ji.Width;
                    h = ji.Height;
                }
                else
                {
                    var bmp = Imaging.Flatten(Imaging.Upright(Imaging.Decoder(data).Frames[0]));
                    data = Imaging.Jpeg(bmp, 88);
                    w = bmp.PixelWidth;
                    h = bmp.PixelHeight;
                }
                var d = new PdfDict();
                d.SetName("Type", "XObject");
                d.SetName("Subtype", "Image");
                d.SetNum("Width", w);
                d.SetNum("Height", h);
                d.SetName("ColorSpace", gray ? "DeviceGray" : "DeviceRGB");
                d.SetNum("BitsPerComponent", 8);
                d.SetName("Filter", "DCTDecode");
                return new PdfStream(d, data);
            }

            // ── 파일로 ──

            static PdfStream Packed(byte[] data, PdfDict d)
            {
                d.SetName("Filter", "FlateDecode");
                return new PdfStream(d, Zlib.Deflate(data));
            }

            static byte[] ToUnicode(Face f)
            {
                var sb = new StringBuilder();
                sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
                var all = f.Used.Where(kv => kv.Value > 0).ToList();
                for (int i = 0; i < all.Count; i += 100)
                {
                    var part = all.Skip(i).Take(100).ToList();
                    sb.Append(part.Count).Append(" beginbfchar\n");
                    foreach (var kv in part)
                    {
                        sb.Append('<').Append(kv.Key.ToString("X4")).Append("> <");
                        foreach (char c in char.ConvertFromUtf32(kv.Value)) sb.Append(((int)c).ToString("X4"));
                        sb.Append(">\n");
                    }
                    sb.Append("endbfchar\n");
                }
                sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
                return Encoding.ASCII.GetBytes(sb.ToString());
            }

            public void Save(string path, string title)
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var w = new PdfWriter(fs, r => r.Num))
                {
                    w.Header("1.5");
                    int next = 4;
                    int tag = 0;
                    foreach (var f in fonts.All.Where(x => x.Used.Count > 0))
                    {
                        job.Check();
                        int fileNum = next++, descNum = next++, cidNum = next++, mapNum = next++, typeNum = next++;
                        var glyphs = f.Used.Keys.ToList();
                        if (!glyphs.Contains(0)) glyphs.Add(0);
                        byte[] subset = f.G.ComputeSubset(glyphs);
                        var fd = new PdfDict();
                        fd.SetNum("Length1", subset.Length);
                        w.Object(fileNum, Packed(subset, fd));
                        string baseName = "BK" + (char)('A' + (tag / 676) % 26) + (char)('A' + (tag / 26) % 26) + (char)('A' + tag % 26) + "A+" + f.Name;
                        tag++;
                        var desc = new PdfDict();
                        desc.SetName("Type", "FontDescriptor");
                        desc.SetName("FontName", baseName);
                        desc.SetNum("Flags", 4);
                        desc["FontBBox"] = PdfArray.Nums(-1000, -1000, 3000, 2000);
                        desc.SetNum("ItalicAngle", 0);
                        desc.SetNum("Ascent", Math.Round(f.G.Baseline * 1000));
                        desc.SetNum("Descent", -Math.Round((f.G.Height - f.G.Baseline) * 1000));
                        desc.SetNum("CapHeight", Math.Round(f.G.CapsHeight * 1000));
                        desc.SetNum("StemV", 80);
                        desc["FontFile2"] = new PdfRef(fileNum, 0);
                        w.Object(descNum, desc);
                        var widths = new PdfArray();
                        var run = new PdfArray();
                        int prev = -2;
                        foreach (ushort g in f.Used.Keys)
                        {
                            if (g != prev + 1)
                            {
                                if (run.Items.Count > 0) widths.Items.Add(run);
                                run = new PdfArray();
                                widths.Items.Add(new PdfNum(g));
                            }
                            run.Items.Add(new PdfNum(Math.Round(f.G.AdvanceWidths[g] * 1000, 1)));
                            prev = g;
                        }
                        if (run.Items.Count > 0) widths.Items.Add(run);
                        var info = new PdfDict();
                        info["Registry"] = PdfString.Text("Adobe");
                        info["Ordering"] = PdfString.Text("Identity");
                        info.SetNum("Supplement", 0);
                        var cid = new PdfDict();
                        cid.SetName("Type", "Font");
                        cid.SetName("Subtype", "CIDFontType2");
                        cid.SetName("BaseFont", baseName);
                        cid["CIDSystemInfo"] = info;
                        cid["FontDescriptor"] = new PdfRef(descNum, 0);
                        cid.SetName("CIDToGIDMap", "Identity");
                        cid.SetNum("DW", 1000);
                        cid["W"] = widths;
                        w.Object(cidNum, cid);
                        w.Object(mapNum, Packed(ToUnicode(f), new PdfDict()));
                        var type0 = new PdfDict();
                        type0.SetName("Type", "Font");
                        type0.SetName("Subtype", "Type0");
                        type0.SetName("BaseFont", baseName);
                        type0.SetName("Encoding", "Identity-H");
                        var kids0 = new PdfArray();
                        kids0.Items.Add(new PdfRef(cidNum, 0));
                        type0["DescendantFonts"] = kids0;
                        type0["ToUnicode"] = new PdfRef(mapNum, 0);
                        w.Object(typeNum, type0);
                        f.Obj = typeNum;
                    }
                    var imageNums = new int[images.Count];
                    for (int i = 0; i < images.Count; i++)
                    {
                        imageNums[i] = next++;
                        w.Object(imageNums[i], images[i]);
                    }
                    var fontDict = new PdfDict();
                    foreach (var f in fonts.All.Where(x => x.Used.Count > 0)) fontDict["F" + f.Index] = new PdfRef(f.Obj, 0);
                    var kids = new PdfArray();
                    for (int p = 0; p < pages.Count; p++)
                    {
                        job.Check();
                        int contNum = next++, pageNum = next++;
                        w.Object(contNum, Packed(Encoding.ASCII.GetBytes(pages[p].ToString()), new PdfDict()));
                        var res = new PdfDict();
                        res["Font"] = fontDict;
                        if (pageImages[p].Count > 0)
                        {
                            var xo = new PdfDict();
                            foreach (int id in pageImages[p]) xo["Im" + id] = new PdfRef(imageNums[id], 0);
                            res["XObject"] = xo;
                        }
                        var page = new PdfDict();
                        page.SetName("Type", "Page");
                        page["Parent"] = new PdfRef(2, 0);
                        page["MediaBox"] = PdfArray.Nums(0, 0, PageW, PageH);
                        page["Resources"] = res;
                        page["Contents"] = new PdfRef(contNum, 0);
                        w.Object(pageNum, page);
                        kids.Items.Add(new PdfRef(pageNum, 0));
                    }
                    var tree = new PdfDict();
                    tree.SetName("Type", "Pages");
                    tree["Kids"] = kids;
                    tree.SetNum("Count", pages.Count);
                    w.Object(2, tree);
                    var cat = new PdfDict();
                    cat.SetName("Type", "Catalog");
                    cat["Pages"] = new PdfRef(2, 0);
                    w.Object(1, cat);
                    var meta = new PdfDict();
                    meta["Title"] = PdfString.Text(title ?? "");
                    meta["Producer"] = PdfString.Text(AppInfo.Name + " " + AppInfo.Version);
                    meta["CreationDate"] = PdfWriter.Date(DateTime.Now);
                    w.Object(3, meta);
                    var trailer = new PdfDict();
                    trailer["Root"] = new PdfRef(1, 0);
                    trailer["Info"] = new PdfRef(3, 0);
                    trailer["ID"] = PdfWriter.NewId(path);
                    w.ClassicXref(next, trailer);
                }
            }
        }
    }
}
