// 도형·글상자·그림·표를 자리대로 놓아 PPT를 쓴다. PDF를 글자 고칠 수 있는 PPT로 옮길 때와 AI 수업 자료가 함께 쓴다.
// 근거: ECMA-376(PresentationML·DrawingML)의 요소 순서. PowerPoint는 순서가 틀리면 '복구'를 묻는다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Bakkum
{
    public sealed class DeckRun
    {
        public string Text = "";
        public double Size;
        public string Font;
        public string Color;
        public bool Bold, Italic, SlideNumber;
        /// 글자 사이 조절(1/100pt)
        public int Spacing;
    }

    public sealed class DeckPara
    {
        public readonly List<DeckRun> Runs = new List<DeckRun>();
        public string Align;
        public long MarL, Indent;
        /// 줄 간격(%). 0이면 글꼴 기본
        public double LinePct;
        /// 줄 간격을 pt로 못 박는다(프로그램마다 글꼴의 기본 줄 높이를 다르게 봐도 줄이 밀리지 않는다)
        public double LinePts;
        public double Before, After;
        public string Bullet, BulletColor;

        public static DeckPara Of(string text, double size, string color, bool bold)
        {
            var p = new DeckPara();
            p.Runs.Add(new DeckRun { Text = text ?? "", Size = size, Color = color, Bold = bold });
            return p;
        }

        public DeckPara Aligned(string align)
        {
            Align = align;
            return this;
        }
    }

    public abstract class DeckItem
    {
        public long X, Y, W, H;
        public string Name;
    }

    public sealed class DeckShape : DeckItem
    {
        public string Geometry = "rect";
        /// 모양 조절값(이름 → 값). 예: roundRect의 adj
        public Dictionary<string, long> Adjust;
        /// 있으면 직접 그린 모양(a:path 묶음)
        public string PathXml;
        public int PathW, PathH;
        public string Fill, Line;
        public int FillAlpha = 100;
        public double LineWidth;
        public bool RoundCap;
        public string Dash;
        public double Rotation;
        public readonly List<DeckPara> Paras = new List<DeckPara>();
        public string Anchor = "t";
        public long InsetL, InsetT, InsetR, InsetB;
        public bool Wrap = true;
        /// none: 그대로, norm: 넘치면 글자를 줄임, shape: 글에 맞춰 상자 크기가 바뀜
        public string Fit = "none";
        public bool IsTitle, IsTextBox;
    }

    public sealed class DeckPicture : DeckItem
    {
        public byte[] Data;
        public string Type = "png";
    }

    public sealed class DeckCell
    {
        public readonly List<DeckPara> Paras = new List<DeckPara>();
        public string Fill;
        public string Anchor = "ctr";
    }

    public sealed class DeckTable : DeckItem
    {
        public long[] Cols, Rows;
        public List<List<DeckCell>> Cells = new List<List<DeckCell>>();
        public string LineColor;
    }

    public sealed class DeckPage
    {
        public string Background;
        /// 슬라이드 배경으로 까는 그림(눌러도 선택되지 않는다)
        public DeckPicture BackgroundPicture;
        public readonly List<DeckItem> Items = new List<DeckItem>();
        public string Notes;
    }

    /// 파워포인트 테마 색(디자인 탭에서 색을 바꾸면 슬라이드도 따라간다).
    public sealed class DeckPalette
    {
        public string Name = "바꿈";
        public string Dk1 = "1F2430", Lt1 = "FFFFFF", Dk2 = "44546A", Lt2 = "F4F1EA";
        public string[] Accents = { "4472C4", "ED7D31", "A5A5A5", "FFC000", "5B9BD5", "70AD47" };
    }

    public sealed class Deck
    {
        public long Cx = 12192000, Cy = 6858000;
        public readonly List<DeckPage> Pages = new List<DeckPage>();
        public DeckPalette Palette = new DeckPalette();
        public string Font = "맑은 고딕";
        public string Title;
    }

    /// 글꼴 재기. 슬라이드에 맞는 글자 크기를 정하거나 PDF 글자 폭에 맞출 때 쓴다.
    public static class DeckFonts
    {
        static Dictionary<string, string> installed;
        static readonly object Gate = new object();
        static readonly Dictionary<string, FontFamily> Families = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

        // PDF 안의 글꼴 이름과 설치된 글꼴 이름이 다른 흔한 경우
        static readonly Dictionary<string, string> Known = new Dictionary<string, string>
        {
            { "hcrbatang", "함초롬바탕" }, { "hcrdotum", "함초롬돋움" }, { "malgungothic", "맑은 고딕" }, { "malgun", "맑은 고딕" },
            { "gulim", "굴림" }, { "gulimche", "굴림체" }, { "dotum", "돋움" }, { "dotumche", "돋움체" }, { "batang", "바탕" }, { "batangche", "바탕체" }, { "gungsuh", "궁서" }, { "gungsuhche", "궁서체" },
            { "hyheadline", "HY헤드라인M" }, { "hygothic", "HY중고딕" }, { "hygothicextra", "HY견고딕" }, { "hymyeongjo", "HY신명조" }, { "hymyeongjoextra", "HY견명조" }, { "hysinmyeongjo", "HY신명조" }, { "hygraphic", "HY그래픽" }, { "hygungso", "HY궁서" }, { "hypost", "HY엽서M" },
            { "nanumgothic", "나눔고딕" }, { "nanummyeongjo", "나눔명조" }, { "nanumbarungothic", "나눔바른고딕" }, { "nanumsquare", "나눔스퀘어" }
        };

        static string Normal(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        static Dictionary<string, string> Installed()
        {
            lock (Gate)
            {
                if (installed != null) return installed;
                var d = new Dictionary<string, string>();
                foreach (var f in Fonts.SystemFontFamilies)
                {
                    string first = f.FamilyNames.Values.FirstOrDefault() ?? f.Source;
                    string shown;
                    if (!f.FamilyNames.TryGetValue(System.Windows.Markup.XmlLanguage.GetLanguage("ko-kr"), out shown)) shown = first;
                    foreach (string n in f.FamilyNames.Values)
                    {
                        string key = Normal(n);
                        if (key.Length > 0 && !d.ContainsKey(key)) d[key] = shown;
                    }
                }
                installed = d;
                return d;
            }
        }

        static readonly string[] StyleWords = { "extrabold", "semibold", "demibold", "ultrabold", "extralight", "ultralight", "bolditalic", "boldoblique", "bold", "italic", "oblique", "regular", "normal", "medium", "light", "black", "heavy", "thin", "book", "roman" };

        /// PDF 글꼴 이름을 이 PC에 있는 글꼴 이름으로. 없으면 비슷한 기본 글꼴.
        public static string Resolve(string pdfName, bool serif)
        {
            string fallback = serif ? "바탕" : "맑은 고딕";
            if (string.IsNullOrWhiteSpace(pdfName)) return fallback;
            var map = Installed();
            string key = Normal(pdfName);
            string hit;
            if (map.TryGetValue(key, out hit)) return hit;
            string stem = key;
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var w in StyleWords)
                {
                    if (stem.Length > w.Length && stem.EndsWith(w, StringComparison.Ordinal))
                    {
                        stem = stem.Substring(0, stem.Length - w.Length);
                        changed = true;
                    }
                }
            }
            if (map.TryGetValue(stem, out hit)) return hit;
            string known;
            if (Known.TryGetValue(key, out known) || Known.TryGetValue(stem, out known)) return map.ContainsKey(Normal(known)) ? map[Normal(known)] : fallback;
            if (stem.Length >= 5)
            {
                foreach (var kv in map)
                {
                    if (kv.Key.StartsWith(stem, StringComparison.Ordinal)) return kv.Value;
                }
            }
            return fallback;
        }

        static FontFamily Family(string name)
        {
            lock (Gate)
            {
                FontFamily f;
                if (!Families.TryGetValue(name, out f))
                {
                    f = new FontFamily(name);
                    Families[name] = f;
                }
                return f;
            }
        }

        static FormattedText Format(string text, string font, double size, bool bold, bool italic)
        {
            var face = new Typeface(Family(font), italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            return new FormattedText(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0);
        }

        public static double Width(string text, string font, double size, bool bold, bool italic)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return Format(text, font, size, bold, italic).WidthIncludingTrailingWhitespace;
        }

        /// 폭 안에서 줄을 바꿔 쓸 때 몇 pt 높이가 되는가
        public static double Height(string text, string font, double size, bool bold, double width, double linePct)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double total = 0;
            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                var ft = Format(line.Length == 0 ? " " : line, font, size, bold, false);
                ft.MaxTextWidth = Math.Max(1, width);
                ft.Trimming = TextTrimming.None;
                total += ft.Height * (linePct <= 0 ? 1 : linePct / 100);
            }
            return total;
        }

        public static double Baseline(string font)
        {
            return Family(font).Baseline;
        }

        public static double LineSpacing(string font)
        {
            return Family(font).LineSpacing;
        }

        /// 상자에 들어가는 가장 큰 글자 크기(step씩 줄이며 찾는다)
        public static double Fit(string text, string font, bool bold, double width, double height, double max, double min, double linePct)
        {
            for (double s = max; s > min; s -= 1)
            {
                if (Height(text, font, s, bold, width, linePct) <= height) return s;
            }
            return min;
        }
    }

    public static class DeckWriter
    {
        const string Ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
        const string Group = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";
        const string RelNs = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">";
        const string RelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        const string ClrMap = "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";

        public static long Emu(double pt)
        {
            return (long)Math.Round(pt * 12700);
        }

        static string Rel(string id, string type, string target)
        {
            return "<Relationship Id=\"" + id + "\" Type=\"" + RelType + type + "\" Target=\"" + target + "\"/>";
        }

        /// 색: "RRGGBB", "@accent1"(테마 색), "@accent1:20"(20%만 남기고 밝게), "@accent1*75"(75%로 어둡게)
        static void Color(StringBuilder sb, string c, int alpha)
        {
            string tail = alpha < 100 ? "<a:alpha val=\"" + alpha * 1000 + "\"/>" : "";
            if (c.StartsWith("@", StringComparison.Ordinal))
            {
                string name = c.Substring(1), mod = "";
                int at = name.IndexOf(':');
                if (at > 0)
                {
                    int keep = int.Parse(name.Substring(at + 1), CultureInfo.InvariantCulture);
                    name = name.Substring(0, at);
                    mod = "<a:lumMod val=\"" + keep * 1000 + "\"/><a:lumOff val=\"" + (100 - keep) * 1000 + "\"/>";
                }
                at = name.IndexOf('*');
                if (at > 0)
                {
                    int keep = int.Parse(name.Substring(at + 1), CultureInfo.InvariantCulture);
                    name = name.Substring(0, at);
                    mod = "<a:lumMod val=\"" + keep * 1000 + "\"/>";
                }
                sb.Append("<a:schemeClr val=\"").Append(name).Append("\">").Append(mod).Append(tail).Append("</a:schemeClr>");
                return;
            }
            sb.Append("<a:srgbClr val=\"").Append(c).Append("\">").Append(tail).Append("</a:srgbClr>");
        }

        static void Solid(StringBuilder sb, string c, int alpha)
        {
            sb.Append("<a:solidFill>");
            Color(sb, c, alpha);
            sb.Append("</a:solidFill>");
        }

        static void Run(StringBuilder sb, DeckRun r)
        {
            bool field = r.SlideNumber;
            sb.Append(field ? "<a:fld id=\"{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}\" type=\"slidenum\">" : "<a:r>");
            sb.Append("<a:rPr lang=\"ko-KR\" altLang=\"en-US\"");
            if (r.Size > 0) sb.Append(" sz=\"").Append(Math.Max(100, (int)Math.Round(r.Size * 100))).Append('"');
            if (r.Bold) sb.Append(" b=\"1\"");
            if (r.Italic) sb.Append(" i=\"1\"");
            if (r.Spacing != 0) sb.Append(" spc=\"").Append(r.Spacing).Append('"');
            sb.Append(" dirty=\"0\">");
            if (r.Color != null) Solid(sb, r.Color, 100);
            if (r.Font != null)
            {
                string f = XmlText.E(r.Font);
                sb.Append("<a:latin typeface=\"").Append(f).Append("\"/><a:ea typeface=\"").Append(f).Append("\"/><a:cs typeface=\"").Append(f).Append("\"/>");
            }
            sb.Append("</a:rPr><a:t>").Append(XmlText.E(r.Text)).Append("</a:t>").Append(field ? "</a:fld>" : "</a:r>");
        }

        static void Para(StringBuilder sb, DeckPara p)
        {
            sb.Append("<a:p><a:pPr");
            if (p.MarL != 0) sb.Append(" marL=\"").Append(p.MarL).Append('"');
            if (p.Indent != 0) sb.Append(" indent=\"").Append(p.Indent).Append('"');
            if (p.Align != null) sb.Append(" algn=\"").Append(p.Align).Append('"');
            sb.Append('>');
            if (p.LinePts > 0) sb.Append("<a:lnSpc><a:spcPts val=\"").Append((int)Math.Round(p.LinePts * 100)).Append("\"/></a:lnSpc>");
            else if (p.LinePct > 0) sb.Append("<a:lnSpc><a:spcPct val=\"").Append((int)Math.Round(p.LinePct * 1000)).Append("\"/></a:lnSpc>");
            if (p.Before > 0) sb.Append("<a:spcBef><a:spcPts val=\"").Append((int)Math.Round(p.Before * 100)).Append("\"/></a:spcBef>");
            if (p.After > 0) sb.Append("<a:spcAft><a:spcPts val=\"").Append((int)Math.Round(p.After * 100)).Append("\"/></a:spcAft>");
            if (p.Bullet != null)
            {
                if (p.BulletColor != null)
                {
                    sb.Append("<a:buClr>");
                    Color(sb, p.BulletColor, 100);
                    sb.Append("</a:buClr>");
                }
                sb.Append("<a:buFont typeface=\"Arial\"/><a:buChar char=\"").Append(XmlText.E(p.Bullet)).Append("\"/>");
            }
            else sb.Append("<a:buNone/>");
            sb.Append("</a:pPr>");
            foreach (var r in p.Runs) Run(sb, r);
            if (p.Runs.Count == 0) sb.Append("<a:endParaRPr lang=\"ko-KR\" dirty=\"0\"/>");
            sb.Append("</a:p>");
        }

        static void Xfrm(StringBuilder sb, DeckItem it, double rotation, string prefix)
        {
            sb.Append('<').Append(prefix).Append(":xfrm");
            if (rotation != 0) sb.Append(" rot=\"").Append((long)Math.Round(rotation * 60000)).Append('"');
            sb.Append("><a:off x=\"").Append(it.X).Append("\" y=\"").Append(it.Y).Append("\"/><a:ext cx=\"").Append(Math.Max(1, it.W)).Append("\" cy=\"").Append(Math.Max(1, it.H)).Append("\"/></").Append(prefix).Append(":xfrm>");
        }

        static void Shape(StringBuilder sb, DeckShape s, int id)
        {
            string name = XmlText.E(s.Name ?? (s.IsTitle ? "제목" : s.IsTextBox ? "글상자 " + id : "도형 " + id));
            sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id).Append("\" name=\"").Append(name).Append("\"/>");
            if (s.IsTitle) sb.Append("<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr>");
            else sb.Append(s.IsTextBox ? "<p:cNvSpPr txBox=\"1\"/><p:nvPr/>" : "<p:cNvSpPr/><p:nvPr/>");
            sb.Append("</p:nvSpPr><p:spPr>");
            Xfrm(sb, s, s.Rotation, "a");
            if (s.PathXml != null)
            {
                sb.Append("<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"0\" t=\"0\" r=\"r\" b=\"b\"/><a:pathLst>").Append(s.PathXml).Append("</a:pathLst></a:custGeom>");
            }
            else
            {
                sb.Append("<a:prstGeom prst=\"").Append(s.Geometry).Append("\"><a:avLst>");
                if (s.Adjust != null)
                {
                    foreach (var kv in s.Adjust) sb.Append("<a:gd name=\"").Append(kv.Key).Append("\" fmla=\"val ").Append(kv.Value).Append("\"/>");
                }
                sb.Append("</a:avLst></a:prstGeom>");
            }
            if (s.Fill != null) Solid(sb, s.Fill, s.FillAlpha);
            else sb.Append("<a:noFill/>");
            sb.Append("<a:ln");
            if (s.Line != null) sb.Append(" w=\"").Append(Emu(s.LineWidth > 0 ? s.LineWidth : 1)).Append('"');
            if (s.RoundCap) sb.Append(" cap=\"rnd\"");
            sb.Append('>');
            if (s.Line != null)
            {
                Solid(sb, s.Line, 100);
                if (s.Dash != null) sb.Append("<a:prstDash val=\"").Append(s.Dash).Append("\"/>");
                if (s.RoundCap) sb.Append("<a:round/>");
            }
            else sb.Append("<a:noFill/>");
            sb.Append("</a:ln></p:spPr>");
            if (s.Paras.Count > 0 || s.IsTitle)
            {
                sb.Append("<p:txBody><a:bodyPr wrap=\"").Append(s.Wrap ? "square" : "none").Append("\" lIns=\"").Append(s.InsetL).Append("\" tIns=\"").Append(s.InsetT).Append("\" rIns=\"").Append(s.InsetR).Append("\" bIns=\"").Append(s.InsetB).Append("\" rtlCol=\"0\" anchor=\"").Append(s.Anchor).Append("\">");
                sb.Append(s.Fit == "norm" ? "<a:normAutofit/>" : s.Fit == "shape" ? "<a:spAutoFit/>" : "<a:noAutofit/>");
                sb.Append("</a:bodyPr><a:lstStyle/>");
                foreach (var p in s.Paras) Para(sb, p);
                if (s.Paras.Count == 0) sb.Append("<a:p><a:endParaRPr lang=\"ko-KR\" dirty=\"0\"/></a:p>");
                sb.Append("</p:txBody>");
            }
            sb.Append("</p:sp>");
        }

        static void Picture(StringBuilder sb, DeckPicture p, int id, string rel)
        {
            sb.Append("<p:pic><p:nvPicPr><p:cNvPr id=\"").Append(id).Append("\" name=\"").Append(XmlText.E(p.Name ?? "그림 " + id)).Append("\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"").Append(rel).Append("\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr>");
            Xfrm(sb, p, 0, "a");
            sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
        }

        static void Border(StringBuilder sb, string tag, string color)
        {
            sb.Append("<a:").Append(tag).Append(" w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">");
            if (color != null)
            {
                Solid(sb, color, 100);
                sb.Append("<a:prstDash val=\"solid\"/>");
            }
            else sb.Append("<a:noFill/>");
            sb.Append("</a:").Append(tag).Append('>');
        }

        static void Table(StringBuilder sb, DeckTable t, int id)
        {
            sb.Append("<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"").Append(id).Append("\" name=\"").Append(XmlText.E(t.Name ?? "표 " + id)).Append("\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>");
            Xfrm(sb, t, 0, "p");
            sb.Append("<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr firstRow=\"1\" bandRow=\"1\"/><a:tblGrid>");
            foreach (long c in t.Cols) sb.Append("<a:gridCol w=\"").Append(c).Append("\"/>");
            sb.Append("</a:tblGrid>");
            for (int r = 0; r < t.Cells.Count; r++)
            {
                sb.Append("<a:tr h=\"").Append(t.Rows[r]).Append("\">");
                foreach (var c in t.Cells[r])
                {
                    sb.Append("<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>");
                    foreach (var p in c.Paras) Para(sb, p);
                    if (c.Paras.Count == 0) sb.Append("<a:p><a:endParaRPr lang=\"ko-KR\" dirty=\"0\"/></a:p>");
                    sb.Append("</a:txBody><a:tcPr marL=\"114300\" marR=\"114300\" marT=\"57150\" marB=\"57150\" anchor=\"").Append(c.Anchor).Append("\">");
                    Border(sb, "lnL", t.LineColor);
                    Border(sb, "lnR", t.LineColor);
                    Border(sb, "lnT", t.LineColor);
                    Border(sb, "lnB", t.LineColor);
                    if (c.Fill != null) Solid(sb, c.Fill, 100);
                    else sb.Append("<a:noFill/>");
                    sb.Append("</a:tcPr></a:tc>");
                }
                sb.Append("</a:tr>");
            }
            sb.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
        }

        // 한쇼는 글꼴을 적지 않고 테마를 따르는 글자를 바탕체로 그린다(2026-11-08 실측). 글자마다 이름을 적어 둔다.
        static void NameFonts(Deck deck)
        {
            var paras = new List<DeckPara>();
            foreach (var page in deck.Pages)
            {
                foreach (var item in page.Items)
                {
                    var shape = item as DeckShape;
                    if (shape != null) paras.AddRange(shape.Paras);
                    var table = item as DeckTable;
                    if (table == null) continue;
                    foreach (var row in table.Cells)
                    {
                        foreach (var cell in row) paras.AddRange(cell.Paras);
                    }
                }
            }
            foreach (var r in paras.SelectMany(p => p.Runs))
            {
                if (r.Font == null) r.Font = deck.Font;
            }
        }

        public static void Write(string path, Deck deck)
        {
            NameFonts(deck);
            bool anyNotes = deck.Pages.Any(p => !string.IsNullOrWhiteSpace(p.Notes));
            var ids = new StringBuilder();
            var presRels = new StringBuilder(XmlText.Head + RelNs);
            presRels.Append(Rel("rId1", "slideMaster", "slideMasters/slideMaster1.xml")).Append(Rel("rId2", "theme", "theme/theme1.xml"));
            if (anyNotes) presRels.Append(Rel("rId3", "notesMaster", "notesMasters/notesMaster1.xml"));
            int relBase = anyNotes ? 3 : 2;
            var ct = new StringBuilder(XmlText.Head);
            ct.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>");
            ct.Append("<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/><Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/><Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/><Override PartName=\"/ppt/slideLayouts/slideLayout2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/><Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
            if (anyNotes) ct.Append("<Override PartName=\"/ppt/notesMasters/notesMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.notesMaster+xml\"/><Override PartName=\"/ppt/theme/theme2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                int media = 0;
                for (int i = 0; i < deck.Pages.Count; i++)
                {
                    var page = deck.Pages[i];
                    int n = i + 1;
                    bool titled = page.Items.OfType<DeckShape>().Any(s => s.IsTitle);
                    var rels = new StringBuilder(XmlText.Head + RelNs);
                    rels.Append(Rel("rId1", "slideLayout", "../slideLayouts/slideLayout" + (titled ? 2 : 1) + ".xml"));
                    int nextRel = 2;
                    var shapes = new StringBuilder();
                    int id = 2;
                    string bgRel = null;
                    if (page.BackgroundPicture != null)
                    {
                        media++;
                        string bgExt = page.BackgroundPicture.Type == "png" ? "png" : "jpeg";
                        XmlText.Put(z, "ppt/media/image" + media + "." + bgExt, page.BackgroundPicture.Data);
                        bgRel = "rId" + nextRel++;
                        rels.Append(Rel(bgRel, "image", "../media/image" + media + "." + bgExt));
                    }
                    foreach (var item in page.Items)
                    {
                        var pic = item as DeckPicture;
                        var sh = item as DeckShape;
                        var tb = item as DeckTable;
                        if (pic != null)
                        {
                            media++;
                            string ext = pic.Type == "png" ? "png" : "jpeg";
                            XmlText.Put(z, "ppt/media/image" + media + "." + ext, pic.Data);
                            string rel = "rId" + nextRel++;
                            rels.Append(Rel(rel, "image", "../media/image" + media + "." + ext));
                            Picture(shapes, pic, id++, rel);
                        }
                        else if (sh != null) Shape(shapes, sh, id++);
                        else if (tb != null) Table(shapes, tb, id++);
                    }
                    var slide = new StringBuilder(XmlText.Head);
                    slide.Append("<p:sld ").Append(Ns).Append("><p:cSld>");
                    if (bgRel != null)
                    {
                        slide.Append("<p:bg><p:bgPr><a:blipFill dpi=\"0\" rotWithShape=\"1\"><a:blip r:embed=\"").Append(bgRel).Append("\"/><a:srcRect/><a:stretch><a:fillRect/></a:stretch></a:blipFill><a:effectLst/></p:bgPr></p:bg>");
                    }
                    else if (page.Background != null)
                    {
                        slide.Append("<p:bg><p:bgPr>");
                        Solid(slide, page.Background, 100);
                        slide.Append("<a:effectLst/></p:bgPr></p:bg>");
                    }
                    slide.Append("<p:spTree>").Append(Group).Append(shapes).Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
                    if (!string.IsNullOrWhiteSpace(page.Notes))
                    {
                        rels.Append(Rel("rId" + nextRel++, "notesSlide", "../notesSlides/notesSlide" + n + ".xml"));
                        WriteNotes(z, page.Notes, n);
                        ct.Append("<Override PartName=\"/ppt/notesSlides/notesSlide").Append(n).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml\"/>");
                    }
                    XmlText.Put(z, "ppt/slides/slide" + n + ".xml", slide.ToString());
                    XmlText.Put(z, "ppt/slides/_rels/slide" + n + ".xml.rels", rels + "</Relationships>");
                    ids.Append("<p:sldId id=\"").Append(255 + n).Append("\" r:id=\"rId").Append(n + relBase).Append("\"/>");
                    presRels.Append(Rel("rId" + (n + relBase), "slide", "slides/slide" + n + ".xml"));
                    ct.Append("<Override PartName=\"/ppt/slides/slide").Append(n).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
                }
                presRels.Append("</Relationships>");
                ct.Append("</Types>");
                XmlText.Put(z, "[Content_Types].xml", ct.ToString());
                XmlText.Put(z, "_rels/.rels", XmlText.Head + RelNs + Rel("rId1", "officeDocument", "ppt/presentation.xml") + "</Relationships>");
                string notesList = anyNotes ? "<p:notesMasterIdLst><p:notesMasterId r:id=\"rId3\"/></p:notesMasterIdLst>" : "";
                XmlText.Put(z, "ppt/presentation.xml", XmlText.Head + "<p:presentation " + Ns + " saveSubsetFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" + notesList + "<p:sldIdLst>" + ids + "</p:sldIdLst><p:sldSz cx=\"" + deck.Cx + "\" cy=\"" + deck.Cy + "\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
                XmlText.Put(z, "ppt/_rels/presentation.xml.rels", presRels.ToString());
                WriteMasters(z, deck);
                XmlText.Put(z, "ppt/theme/theme1.xml", ThemeXml(deck));
                if (anyNotes)
                {
                    XmlText.Put(z, "ppt/theme/theme2.xml", ThemeXml(deck));
                    WriteNotesMaster(z);
                }
            }
        }

        static string Placeholder(int id, string name, string ph, long x, long y, long w, long h, string text)
        {
            return "<p:sp><p:nvSpPr><p:cNvPr id=\"" + id + "\" name=\"" + name + "\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph " + ph + "/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"" + x + "\" y=\"" + y + "\"/><a:ext cx=\"" + w + "\" cy=\"" + h + "\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"ko-KR\"/><a:t>" + text + "</a:t></a:r></a:p></p:txBody></p:sp>";
        }

        static void WriteMasters(ZipArchive z, Deck deck)
        {
            long mx = deck.Cx / 16, ty = deck.Cy / 18, th = deck.Cy * 3 / 16, bw = deck.Cx - 2 * mx;
            string master = XmlText.Head + "<p:sldMaster " + Ns + "><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>" + Group
                + Placeholder(2, "제목", "type=\"title\"", mx, ty, bw, th, "제목")
                + "</p:spTree></p:cSld>" + ClrMap + "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/><p:sldLayoutId id=\"2147483650\" r:id=\"rId2\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"3200\" b=\"1\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr marL=\"0\" indent=\"0\"><a:buNone/><a:defRPr sz=\"2000\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:bodyStyle><p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1800\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>";
            XmlText.Put(z, "ppt/slideMasters/slideMaster1.xml", master);
            XmlText.Put(z, "ppt/slideMasters/_rels/slideMaster1.xml.rels", XmlText.Head + RelNs + Rel("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml") + Rel("rId2", "slideLayout", "../slideLayouts/slideLayout2.xml") + Rel("rId3", "theme", "../theme/theme1.xml") + "</Relationships>");
            string layoutRels = XmlText.Head + RelNs + Rel("rId1", "slideMaster", "../slideMasters/slideMaster1.xml") + "</Relationships>";
            XmlText.Put(z, "ppt/slideLayouts/slideLayout1.xml", XmlText.Head + "<p:sldLayout " + Ns + " type=\"blank\" preserve=\"1\"><p:cSld name=\"빈 화면\"><p:spTree>" + Group + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
            XmlText.Put(z, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", layoutRels);
            XmlText.Put(z, "ppt/slideLayouts/slideLayout2.xml", XmlText.Head + "<p:sldLayout " + Ns + " type=\"titleOnly\" preserve=\"1\"><p:cSld name=\"제목만\"><p:spTree>" + Group + "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"제목\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"ko-KR\"/><a:t>제목</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
            XmlText.Put(z, "ppt/slideLayouts/_rels/slideLayout2.xml.rels", layoutRels);
        }

        static void WriteNotes(ZipArchive z, string notes, int n)
        {
            var sb = new StringBuilder(XmlText.Head);
            sb.Append("<p:notes ").Append(Ns).Append("><p:cSld><p:spTree>").Append(Group);
            sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"슬라이드 이미지\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"sldImg\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>");
            sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"슬라이드 노트\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>");
            foreach (string line in notes.Replace("\r", "").Split('\n'))
            {
                sb.Append("<a:p><a:r><a:rPr lang=\"ko-KR\" dirty=\"0\"/><a:t>").Append(XmlText.E(line)).Append("</a:t></a:r></a:p>");
            }
            sb.Append("</p:txBody></p:sp></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>");
            XmlText.Put(z, "ppt/notesSlides/notesSlide" + n + ".xml", sb.ToString());
            XmlText.Put(z, "ppt/notesSlides/_rels/notesSlide" + n + ".xml.rels", XmlText.Head + RelNs + Rel("rId1", "notesMaster", "../notesMasters/notesMaster1.xml") + Rel("rId2", "slide", "../slides/slide" + n + ".xml") + "</Relationships>");
        }

        static void WriteNotesMaster(ZipArchive z)
        {
            string body = "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"슬라이드 이미지\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"sldImg\" idx=\"2\"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"685800\" y=\"1143000\"/><a:ext cx=\"5486400\" cy=\"3086100\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln w=\"12700\"><a:solidFill><a:prstClr val=\"black\"/></a:solidFill></a:ln></p:spPr></p:sp>"
                + "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"슬라이드 노트\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" sz=\"quarter\" idx=\"3\"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"685800\" y=\"4400550\"/><a:ext cx=\"5486400\" cy=\"3600450\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"ko-KR\"/><a:t>노트</a:t></a:r></a:p></p:txBody></p:sp>";
            XmlText.Put(z, "ppt/notesMasters/notesMaster1.xml", XmlText.Head + "<p:notesMaster " + Ns + "><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>" + Group + body + "</p:spTree></p:cSld>" + ClrMap + "</p:notesMaster>");
            XmlText.Put(z, "ppt/notesMasters/_rels/notesMaster1.xml.rels", XmlText.Head + RelNs + Rel("rId1", "theme", "../theme/theme2.xml") + "</Relationships>");
        }

        static string ThemeXml(Deck deck)
        {
            var pal = deck.Palette;
            string f = XmlText.E(deck.Font);
            string solid = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
            var sb = new StringBuilder(XmlText.Head);
            sb.Append("<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"").Append(XmlText.E(pal.Name)).Append("\"><a:themeElements><a:clrScheme name=\"").Append(XmlText.E(pal.Name)).Append("\">");
            sb.Append("<a:dk1><a:srgbClr val=\"").Append(pal.Dk1).Append("\"/></a:dk1><a:lt1><a:srgbClr val=\"").Append(pal.Lt1).Append("\"/></a:lt1><a:dk2><a:srgbClr val=\"").Append(pal.Dk2).Append("\"/></a:dk2><a:lt2><a:srgbClr val=\"").Append(pal.Lt2).Append("\"/></a:lt2>");
            for (int i = 0; i < 6; i++) sb.Append("<a:accent").Append(i + 1).Append("><a:srgbClr val=\"").Append(pal.Accents[i]).Append("\"/></a:accent").Append(i + 1).Append('>');
            sb.Append("<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>");
            sb.Append("<a:fontScheme name=\"바꿈\"><a:majorFont><a:latin typeface=\"").Append(f).Append("\"/><a:ea typeface=\"").Append(f).Append("\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"").Append(f).Append("\"/><a:ea typeface=\"").Append(f).Append("\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>");
            sb.Append("<a:fmtScheme name=\"바꿈\"><a:fillStyleLst>").Append(solid).Append(solid).Append(solid).Append("</a:fillStyleLst><a:lnStyleLst>");
            foreach (int w in new[] { 6350, 12700, 19050 }) sb.Append("<a:ln w=\"").Append(w).Append("\">").Append(solid).Append("</a:ln>");
            sb.Append("</a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst>").Append(solid).Append(solid).Append(solid).Append("</a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>");
            return sb.ToString();
        }
    }
}
