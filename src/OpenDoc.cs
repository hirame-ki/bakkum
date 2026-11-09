// 오픈 문서 형식(ODS 표, ODT 글)과 워드 파일을 글·표·사진 덩어리로 읽는다. 프로그램이 없어도 된다.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Bakkum
{
    public static class OdsFile
    {
        const string Off = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        const string Tab = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        const string Txt = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        const string Mime = "application/vnd.oasis.opendocument.spreadsheet";
        const int MaxCells = 3000000, MaxRows = 1100000;
        static readonly Regex Numeric = new Regex(@"^-?(0|[1-9]\d{0,14})(\.\d{1,12})?$");

        static int Repeat(string v)
        {
            int n;
            return int.TryParse(v, out n) && n > 1 ? n : 1;
        }

        static UserFacingException TooBig()
        {
            return new UserFacingException("표가 너무 커서 읽지 않았어요.");
        }

        public static List<XlsxSheet> Read(string path)
        {
            Util.EnsureReadable(path);
            var sheets = new List<XlsxSheet>();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var e = z.GetEntry("content.xml");
                    if (e == null) throw new UserFacingException("파일 안의 내용을 찾지 못했어요. 형식이 맞지 않는 파일일 수 있어요.");
                    using (var x = XmlReader.Create(e.Open(), XmlText.Safe()))
                    {
                        XlsxSheet sheet = null;
                        int cells = 0;
                        long pending = 0;
                        while (x.Read())
                        {
                            if (x.NodeType != XmlNodeType.Element || x.NamespaceURI != Tab) continue;
                            if (x.LocalName == "table")
                            {
                                sheet = new XlsxSheet { Name = x.GetAttribute("name", Tab) ?? ("시트" + (sheets.Count + 1)) };
                                sheets.Add(sheet);
                                pending = 0;
                            }
                            else if (x.LocalName == "table-row" && sheet != null)
                            {
                                int repeat = Repeat(x.GetAttribute("number-rows-repeated", Tab));
                                var row = ReadRow(x, ref cells);
                                if (row.Count == 0)
                                {
                                    pending += repeat;
                                    continue;
                                }
                                if (sheet.Rows.Count + pending + repeat > MaxRows) throw TooBig();
                                for (long i = 0; i < pending; i++) sheet.Rows.Add(new List<string>());
                                pending = 0;
                                sheet.Rows.Add(row);
                                for (int i = 1; i < repeat; i++)
                                {
                                    cells += row.Count;
                                    if (cells > MaxCells) throw TooBig();
                                    sheet.Rows.Add(new List<string>(row));
                                }
                            }
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                throw new UserFacingException("파일을 읽지 못했어요. 암호가 걸렸거나 손상된 파일일 수 있어요.");
            }
            catch (XmlException)
            {
                throw new UserFacingException("파일을 읽지 못했어요. 형식이 맞지 않는 파일일 수 있어요.");
            }
            return sheets;
        }

        static List<string> ReadRow(XmlReader x, ref int cells)
        {
            var row = new List<string>();
            if (x.IsEmptyElement) return row;
            int depth = x.Depth, pending = 0;
            while (x.Read())
            {
                if (x.NodeType == XmlNodeType.EndElement && x.Depth == depth) break;
                if (x.NodeType != XmlNodeType.Element || x.NamespaceURI != Tab) continue;
                if (x.LocalName != "table-cell" && x.LocalName != "covered-table-cell") continue;
                int repeat = Repeat(x.GetAttribute("number-columns-repeated", Tab));
                string v = ReadCell(x);
                if (v.Length == 0)
                {
                    pending += repeat;
                    continue;
                }
                if (row.Count + pending + repeat > 20000) throw TooBig();
                cells += pending + repeat;
                if (cells > MaxCells) throw TooBig();
                for (int i = 0; i < pending; i++) row.Add("");
                pending = 0;
                for (int i = 0; i < repeat; i++) row.Add(v);
            }
            return row;
        }

        static string ReadCell(XmlReader x)
        {
            string type = x.GetAttribute("value-type", Off);
            string value = x.GetAttribute("value", Off);
            string date = x.GetAttribute("date-value", Off);
            string flag = x.GetAttribute("boolean-value", Off);
            var sb = new StringBuilder();
            if (!x.IsEmptyElement)
            {
                int depth = x.Depth, paragraphs = 0, hide = -1;
                while (x.Read())
                {
                    if (x.NodeType == XmlNodeType.EndElement && x.Depth == depth) break;
                    if (hide >= 0)
                    {
                        if (x.NodeType == XmlNodeType.EndElement && x.Depth == hide) hide = -1;
                        continue;
                    }
                    if (x.NodeType == XmlNodeType.Element)
                    {
                        if (x.NamespaceURI == Off && x.LocalName == "annotation")
                        {
                            if (!x.IsEmptyElement) hide = x.Depth;
                            continue;
                        }
                        if (x.NamespaceURI != Txt) continue;
                        switch (x.LocalName)
                        {
                            case "p":
                                if (paragraphs++ > 0) sb.Append('\n');
                                break;
                            case "s":
                                sb.Append(' ', Repeat(x.GetAttribute("c", Txt)));
                                break;
                            case "tab":
                                sb.Append('\t');
                                break;
                            case "line-break":
                                sb.Append('\n');
                                break;
                        }
                    }
                    else if (x.NodeType == XmlNodeType.Text || x.NodeType == XmlNodeType.CDATA || x.NodeType == XmlNodeType.SignificantWhitespace)
                    {
                        sb.Append(x.Value);
                    }
                }
            }
            switch (type)
            {
                case "float":
                    if (!string.IsNullOrEmpty(value)) return value;
                    break;
                case "date":
                    if (!string.IsNullOrEmpty(date)) return date.Replace("T00:00:00", "").Replace('T', ' ');
                    break;
                case "boolean":
                    if (!string.IsNullOrEmpty(flag)) return flag == "true" ? "TRUE" : "FALSE";
                    break;
            }
            return sb.ToString();
        }

        static void PutText(StringBuilder sb, string line)
        {
            sb.Append("<text:p>");
            int spaces = 0;
            bool any = false;
            Action flush = delegate
            {
                if (spaces == 0) return;
                bool collapse = !any || spaces > 1;
                if (!collapse) sb.Append(' ');
                else if (any)
                {
                    sb.Append(' ');
                    if (spaces > 1) sb.Append("<text:s text:c=\"").Append(spaces - 1).Append("\"/>");
                }
                else sb.Append("<text:s text:c=\"").Append(spaces).Append("\"/>");
                spaces = 0;
            };
            foreach (char c in line)
            {
                if (c == ' ')
                {
                    spaces++;
                    continue;
                }
                flush();
                any = true;
                if (c == '\t') sb.Append("<text:tab/>");
                else sb.Append(XmlText.E(c.ToString()));
            }
            flush();
            sb.Append("</text:p>");
        }

        public static void Write(string path, IList<XlsxSheet> sheets)
        {
            var sb = new StringBuilder(XmlText.Head);
            sb.Append("<office:document-content xmlns:office=\"").Append(Off).Append("\" xmlns:table=\"").Append(Tab).Append("\" xmlns:text=\"").Append(Txt).Append("\" office:version=\"1.2\"><office:body><office:spreadsheet>");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sheets)
            {
                string name = (s.Name ?? "").Trim();
                if (name.Length == 0) name = "시트";
                string candidate = name;
                for (int i = 2; !used.Add(candidate); i++) candidate = name + " " + i;
                int cols = Math.Max(1, s.Rows.Count == 0 ? 1 : s.Rows.Max(r => r.Count));
                sb.Append("<table:table table:name=\"").Append(XmlText.E(candidate)).Append("\"><table:table-column table:number-columns-repeated=\"").Append(cols).Append("\"/>");
                foreach (var row in s.Rows)
                {
                    sb.Append("<table:table-row>");
                    foreach (var c in row)
                    {
                        string v = c ?? "";
                        if (v.Length == 0) sb.Append("<table:table-cell/>");
                        else if (Numeric.IsMatch(v)) sb.Append("<table:table-cell office:value-type=\"float\" office:value=\"").Append(v).Append("\"><text:p>").Append(v).Append("</text:p></table:table-cell>");
                        else
                        {
                            sb.Append("<table:table-cell office:value-type=\"string\">");
                            foreach (var line in v.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) PutText(sb, line);
                            sb.Append("</table:table-cell>");
                        }
                    }
                    if (row.Count == 0) sb.Append("<table:table-cell/>");
                    sb.Append("</table:table-row>");
                }
                if (s.Rows.Count == 0) sb.Append("<table:table-row><table:table-cell/></table:table-row>");
                sb.Append("</table:table>");
            }
            sb.Append("</office:spreadsheet></office:body></office:document-content>");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var z = new StoredFirstZip(fs))
            {
                z.Add("mimetype", Encoding.ASCII.GetBytes(Mime), true);
                z.AddText("content.xml", sb.ToString());
                z.AddText("styles.xml", XmlText.Head + "<office:document-styles xmlns:office=\"" + Off + "\" office:version=\"1.2\"/>");
                z.AddText("meta.xml", XmlText.Head + "<office:document-meta xmlns:office=\"" + Off + "\" xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" office:version=\"1.2\"><office:meta><meta:generator>" + XmlText.E(AppInfo.Name) + "</meta:generator></office:meta></office:document-meta>");
                z.AddText("META-INF/manifest.xml", XmlText.Head + "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\"><manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.2\" manifest:media-type=\"" + Mime + "\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/><manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/><manifest:file-entry manifest:full-path=\"meta.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>");
            }
        }
    }

    /// 압축 문서 안의 XML을 읽는 공통 부분
    static class PackedXml
    {
        public static XmlDocument Load(ZipArchiveEntry e)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(e.Open(), XmlText.Safe())) doc.Load(r);
            return doc;
        }

        public static byte[] Bytes(ZipArchiveEntry e)
        {
            using (var s = e.Open())
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        public static IEnumerable<XmlElement> Kids(XmlNode n)
        {
            return n.ChildNodes.OfType<XmlElement>();
        }

        public static XmlElement Kid(XmlNode n, string local)
        {
            return Kids(n).FirstOrDefault(k => k.LocalName == local);
        }

        public static string Attr(XmlElement e, string local)
        {
            foreach (XmlAttribute a in e.Attributes)
            {
                if (a.LocalName == local) return a.Value;
            }
            return null;
        }
    }

    public static class OdtFile
    {
        public static List<DocBlock> Read(string path)
        {
            Util.EnsureReadable(path);
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var e = z.GetEntry("content.xml");
                    if (e == null) throw new UserFacingException("파일 안의 내용을 찾지 못했어요. 형식이 맞지 않는 파일일 수 있어요.");
                    var doc = PackedXml.Load(e);
                    var body = doc.SelectSingleNode("//*[local-name()='body']/*[local-name()='text']");
                    var blocks = new List<DocBlock>();
                    if (body != null) Walk(z, body, blocks, 0);
                    return blocks;
                }
            }
            catch (InvalidDataException)
            {
                throw new UserFacingException("파일을 읽지 못했어요. 암호가 걸렸거나 손상된 파일일 수 있어요.");
            }
            catch (XmlException)
            {
                throw new UserFacingException("파일을 읽지 못했어요. 형식이 맞지 않는 파일일 수 있어요.");
            }
        }

        static void Walk(ZipArchive z, XmlNode parent, List<DocBlock> blocks, int listLevel)
        {
            foreach (var n in PackedXml.Kids(parent))
            {
                switch (n.LocalName)
                {
                    case "h":
                        {
                            int level;
                            if (!int.TryParse(PackedXml.Attr(n, "outline-level"), out level)) level = 1;
                            string t = Text(n);
                            if (t.Trim().Length > 0) blocks.Add(DocBlock.Head(t, level));
                            Pictures(z, n, blocks);
                            break;
                        }
                    case "p":
                        {
                            string t = Text(n);
                            if (t.Trim().Length > 0)
                            {
                                if (listLevel > 0) blocks.Add(new DocBlock { Kind = BlockKind.Bullet, Text = t, Level = listLevel });
                                else blocks.Add(DocBlock.Para(t));
                            }
                            Pictures(z, n, blocks);
                            break;
                        }
                    case "list":
                        foreach (var item in PackedXml.Kids(n).Where(k => k.LocalName == "list-item")) Walk(z, item, blocks, listLevel + 1);
                        break;
                    case "section":
                        Walk(z, n, blocks, listLevel);
                        break;
                    case "table":
                        Table(n, blocks);
                        break;
                }
            }
        }

        static void Pictures(ZipArchive z, XmlElement p, List<DocBlock> blocks)
        {
            foreach (XmlElement img in p.SelectNodes(".//*[local-name()='image']"))
            {
                string href = PackedXml.Attr(img, "href");
                if (string.IsNullOrEmpty(href) || href.Contains(":")) continue;
                var e = z.GetEntry(href.TrimStart('.', '/'));
                if (e == null || e.Length > 64L * 1024 * 1024) continue;
                var b = MarkdownDoc.Picture(PackedXml.Bytes(e));
                if (b != null) blocks.Add(b);
            }
        }

        static void Table(XmlElement t, List<DocBlock> blocks)
        {
            var rows = new List<List<string>>();
            foreach (XmlElement r in t.SelectNodes(".//*[local-name()='table-row']"))
            {
                var row = new List<string>();
                foreach (var c in PackedXml.Kids(r).Where(k => k.LocalName == "table-cell" || k.LocalName == "covered-table-cell"))
                {
                    int repeat;
                    if (!int.TryParse(PackedXml.Attr(c, "number-columns-repeated"), out repeat) || repeat < 1) repeat = 1;
                    string v = string.Join("\n", PackedXml.Kids(c).Where(k => k.LocalName == "p").Select(Text));
                    for (int i = 0; i < Math.Min(repeat, 64); i++) row.Add(v);
                }
                rows.Add(row);
            }
            if (rows.Count == 0) return;
            int cols = rows.Max(r => r.Count);
            foreach (var r in rows)
            {
                while (r.Count < cols) r.Add("");
            }
            blocks.Add(new DocBlock { Kind = BlockKind.Table, Rows = rows });
        }

        static string Text(XmlNode n)
        {
            var sb = new StringBuilder();
            Collect(n, sb);
            return sb.ToString();
        }

        static void Collect(XmlNode n, StringBuilder sb)
        {
            foreach (XmlNode c in n.ChildNodes)
            {
                if (c.NodeType == XmlNodeType.Text || c.NodeType == XmlNodeType.CDATA || c.NodeType == XmlNodeType.SignificantWhitespace)
                {
                    sb.Append(c.Value);
                    continue;
                }
                var e = c as XmlElement;
                if (e == null) continue;
                switch (e.LocalName)
                {
                    case "s":
                        {
                            int k;
                            if (!int.TryParse(PackedXml.Attr(e, "c"), out k) || k < 1) k = 1;
                            sb.Append(' ', Math.Min(k, 200));
                            break;
                        }
                    case "tab":
                        sb.Append('\t');
                        break;
                    case "line-break":
                        sb.Append('\n');
                        break;
                    case "annotation":
                    case "note":
                    case "frame":
                        break;
                    default:
                        Collect(e, sb);
                        break;
                }
            }
        }
    }

    public static class DocxBlocks
    {
        sealed class Reading
        {
            public ZipArchive Z;
            public Dictionary<string, string> Rels = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, int> Headings = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, bool[]> Bullets = new Dictionary<string, bool[]>(StringComparer.Ordinal);
            public Dictionary<string, int> Counters = new Dictionary<string, int>(StringComparer.Ordinal);
            public List<DocBlock> Blocks = new List<DocBlock>();
        }

        public static List<DocBlock> Read(string path)
        {
            Util.EnsureReadable(path);
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var main = z.GetEntry("word/document.xml");
                    if (main == null) throw new UserFacingException("파일 안의 내용을 찾지 못했어요. 형식이 맞지 않는 파일일 수 있어요.");
                    var r = new Reading { Z = z };
                    LoadRels(r);
                    LoadStyles(r);
                    LoadNumbering(r);
                    var doc = PackedXml.Load(main);
                    var body = doc.SelectSingleNode("//*[local-name()='body']");
                    if (body != null) Container(r, body);
                    return r.Blocks;
                }
            }
            catch (InvalidDataException)
            {
                throw new UserFacingException("파일을 읽지 못했어요. 암호가 걸렸거나 손상된 파일일 수 있어요.");
            }
            catch (XmlException)
            {
                throw new UserFacingException("파일을 읽지 못했어요. 형식이 맞지 않는 파일일 수 있어요.");
            }
        }

        static void LoadRels(Reading r)
        {
            var e = r.Z.GetEntry("word/_rels/document.xml.rels");
            if (e == null) return;
            foreach (var x in PackedXml.Kids(PackedXml.Load(e).DocumentElement)) r.Rels[x.GetAttribute("Id")] = x.GetAttribute("Target");
        }

        static void LoadStyles(Reading r)
        {
            var e = r.Z.GetEntry("word/styles.xml");
            if (e == null) return;
            foreach (var s in PackedXml.Kids(PackedXml.Load(e).DocumentElement).Where(k => k.LocalName == "style"))
            {
                var name = PackedXml.Kid(s, "name");
                if (name == null) continue;
                string n = (PackedXml.Attr(name, "val") ?? "").Trim().ToLowerInvariant();
                var m = Regex.Match(n, @"^(heading|제목)\s*(\d)$");
                int level = 0;
                if (m.Success) level = m.Groups[2].Value[0] - '0';
                else if (n == "title" || n == "제목") level = 1;
                if (level > 0) r.Headings[PackedXml.Attr(s, "styleId") ?? ""] = level;
            }
        }

        static void LoadNumbering(Reading r)
        {
            var e = r.Z.GetEntry("word/numbering.xml");
            if (e == null) return;
            var root = PackedXml.Load(e).DocumentElement;
            var abs = new Dictionary<string, bool[]>(StringComparer.Ordinal);
            foreach (var a in PackedXml.Kids(root).Where(k => k.LocalName == "abstractNum"))
            {
                var levels = new bool[9];
                foreach (var l in PackedXml.Kids(a).Where(k => k.LocalName == "lvl"))
                {
                    int i;
                    var fmt = PackedXml.Kid(l, "numFmt");
                    if (int.TryParse(PackedXml.Attr(l, "ilvl"), out i) && i >= 0 && i < 9 && fmt != null) levels[i] = PackedXml.Attr(fmt, "val") == "bullet";
                }
                abs[PackedXml.Attr(a, "abstractNumId") ?? ""] = levels;
            }
            foreach (var n in PackedXml.Kids(root).Where(k => k.LocalName == "num"))
            {
                var link = PackedXml.Kid(n, "abstractNumId");
                bool[] levels;
                if (link != null && abs.TryGetValue(PackedXml.Attr(link, "val") ?? "", out levels)) r.Bullets[PackedXml.Attr(n, "numId") ?? ""] = levels;
            }
        }

        static void Container(Reading r, XmlNode parent)
        {
            foreach (var n in PackedXml.Kids(parent))
            {
                switch (n.LocalName)
                {
                    case "p":
                        Paragraph(r, n);
                        break;
                    case "tbl":
                        Table(r, n);
                        break;
                    case "sdt":
                        {
                            var c = PackedXml.Kid(n, "sdtContent");
                            if (c != null) Container(r, c);
                            break;
                        }
                }
            }
        }

        static bool On(XmlElement toggle)
        {
            if (toggle == null) return false;
            string v = PackedXml.Attr(toggle, "val");
            return v == null || (v != "0" && v != "false" && v != "off");
        }

        sealed class Line
        {
            public readonly StringBuilder Text = new StringBuilder();
            public readonly List<string> Pictures = new List<string>();
            public bool PageBreak;
        }

        static void Runs(XmlNode n, Line line, bool marks)
        {
            foreach (var e in PackedXml.Kids(n))
            {
                switch (e.LocalName)
                {
                    case "r":
                        Run(e, line, marks);
                        break;
                    case "hyperlink":
                    case "ins":
                    case "smartTag":
                    case "fldSimple":
                        Runs(e, line, marks);
                        break;
                    case "sdt":
                        {
                            var c = PackedXml.Kid(e, "sdtContent");
                            if (c != null) Runs(c, line, marks);
                            break;
                        }
                }
            }
        }

        static void Run(XmlElement r, Line line, bool marks)
        {
            var pr = PackedXml.Kid(r, "rPr");
            bool bold = marks && pr != null && On(PackedXml.Kid(pr, "b"));
            var sb = new StringBuilder();
            foreach (var c in PackedXml.Kids(r))
            {
                switch (c.LocalName)
                {
                    case "t":
                        sb.Append(c.InnerText);
                        break;
                    case "tab":
                        sb.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        if (PackedXml.Attr(c, "type") == "page") line.PageBreak = true;
                        else sb.Append('\n');
                        break;
                    case "drawing":
                    case "pict":
                    case "object":
                        foreach (XmlElement b in c.SelectNodes(".//*[local-name()='blip' or local-name()='imagedata']"))
                        {
                            string id = PackedXml.Attr(b, "embed") ?? PackedXml.Attr(b, "id");
                            if (!string.IsNullOrEmpty(id)) line.Pictures.Add(id);
                        }
                        break;
                    case "AlternateContent":
                        {
                            var choice = PackedXml.Kid(c, "Choice");
                            if (choice != null)
                            {
                                foreach (XmlElement b in choice.SelectNodes(".//*[local-name()='blip']"))
                                {
                                    string id = PackedXml.Attr(b, "embed");
                                    if (!string.IsNullOrEmpty(id)) line.Pictures.Add(id);
                                }
                            }
                            break;
                        }
                }
            }
            if (sb.Length == 0) return;
            string t = sb.ToString();
            if (bold && t.Trim().Length > 0) line.Text.Append("**").Append(t).Append("**");
            else line.Text.Append(t);
        }

        static void Paragraph(Reading r, XmlElement p)
        {
            var line = new Line();
            Runs(p, line, true);
            string text = line.Text.ToString().Replace("****", "");
            var pr = PackedXml.Kid(p, "pPr");
            int heading = 0;
            string numId = null;
            int ilvl = 0;
            if (pr != null)
            {
                var st = PackedXml.Kid(pr, "pStyle");
                if (st != null) r.Headings.TryGetValue(PackedXml.Attr(st, "val") ?? "", out heading);
                var num = PackedXml.Kid(pr, "numPr");
                if (num != null)
                {
                    var a = PackedXml.Kid(num, "numId");
                    var b = PackedXml.Kid(num, "ilvl");
                    if (a != null) numId = PackedXml.Attr(a, "val");
                    if (b != null) int.TryParse(PackedXml.Attr(b, "val"), out ilvl);
                }
            }
            if (text.Trim().Length > 0)
            {
                if (heading > 0) r.Blocks.Add(DocBlock.Head(text.Replace("**", ""), heading));
                else if (numId != null && numId != "0")
                {
                    bool[] levels;
                    bool bullet = !r.Bullets.TryGetValue(numId, out levels) || (ilvl >= 0 && ilvl < 9 && levels[ilvl]);
                    if (bullet) r.Blocks.Add(new DocBlock { Kind = BlockKind.Bullet, Text = text, Level = Math.Min(ilvl + 1, 4) });
                    else
                    {
                        string key = numId + "/" + ilvl;
                        int n;
                        r.Counters.TryGetValue(key, out n);
                        r.Counters[key] = ++n;
                        r.Blocks.Add(new DocBlock { Kind = BlockKind.Number, Number = n, Text = text, Level = Math.Min(ilvl + 1, 4) });
                    }
                }
                else r.Blocks.Add(DocBlock.Para(text));
            }
            foreach (string id in line.Pictures)
            {
                string target;
                if (!r.Rels.TryGetValue(id, out target)) continue;
                var e = r.Z.GetEntry("word/" + target.TrimStart('/').Replace("word/", ""));
                if (e == null || e.Length > 64L * 1024 * 1024) continue;
                var b = MarkdownDoc.Picture(PackedXml.Bytes(e));
                if (b != null) r.Blocks.Add(b);
            }
            if (line.PageBreak) r.Blocks.Add(DocBlock.Break());
        }

        static void Table(Reading r, XmlElement t)
        {
            var rows = new List<List<string>>();
            foreach (var tr in PackedXml.Kids(t).Where(k => k.LocalName == "tr"))
            {
                var row = new List<string>();
                foreach (var tc in PackedXml.Kids(tr).Where(k => k.LocalName == "tc"))
                {
                    var pr = PackedXml.Kid(tc, "tcPr");
                    int span = 1;
                    bool continued = false;
                    if (pr != null)
                    {
                        var g = PackedXml.Kid(pr, "gridSpan");
                        if (g != null) int.TryParse(PackedXml.Attr(g, "val"), out span);
                        var v = PackedXml.Kid(pr, "vMerge");
                        continued = v != null && PackedXml.Attr(v, "val") != "restart";
                    }
                    var lines = new List<string>();
                    foreach (XmlElement p in tc.SelectNodes(".//*[local-name()='p']"))
                    {
                        var line = new Line();
                        Runs(p, line, false);
                        if (line.Text.Length > 0) lines.Add(line.Text.ToString());
                    }
                    row.Add(continued ? "" : string.Join("\n", lines));
                    for (int i = 1; i < Math.Min(span, 64); i++) row.Add("");
                }
                rows.Add(row);
            }
            if (rows.Count == 0) return;
            int cols = rows.Max(x => x.Count);
            foreach (var x in rows)
            {
                while (x.Count < cols) x.Add("");
            }
            r.Blocks.Add(new DocBlock { Kind = BlockKind.Table, Rows = rows });
        }
    }
}
