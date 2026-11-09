// 전자책(EPUB) 읽고 쓰기. 글과 표, 사진을 옮긴다. 복제 방지가 걸린 책은 열지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml;

namespace Bakkum
{
    public static class Epub
    {
        const long MaxEntry = 64L * 1024 * 1024, MaxTotal = 512L * 1024 * 1024;
        const string Css = "body{font-family:'Malgun Gothic',sans-serif;line-height:1.7;margin:1em}\nh1{font-size:1.6em}h2{font-size:1.3em}h3{font-size:1.1em}\ntable{border-collapse:collapse;margin:1em 0}th,td{border:1px solid #999;padding:.3em .5em;vertical-align:top}th{background:#eee}\nimg{max-width:100%}\ndel{color:#c0392b}ins{color:#1f6feb}\n.pic{text-align:center}\n";

        sealed class Chapter
        {
            public string Title;
            public readonly List<DocBlock> Blocks = new List<DocBlock>();
        }

        static List<Chapter> Split(IList<DocBlock> blocks, string title)
        {
            bool byHeading = blocks.Count(b => b.Kind == BlockKind.Heading && b.Level == 1) >= 2;
            var list = new List<Chapter>();
            var cur = new Chapter { Title = title };
            foreach (var b in blocks)
            {
                if (byHeading && b.Kind == BlockKind.Heading && b.Level == 1)
                {
                    if (cur.Blocks.Count > 0) list.Add(cur);
                    cur = new Chapter { Title = Plain(b.Text) };
                }
                cur.Blocks.Add(b);
            }
            if (cur.Blocks.Count > 0 || list.Count == 0) list.Add(cur);
            return list;
        }

        static string Plain(string s)
        {
            return string.Concat(Inline.Split(s).Select(r => r.Text)).Replace('\n', ' ').Trim();
        }

        static string InlineXhtml(string s)
        {
            var sb = new StringBuilder();
            foreach (var r in Inline.Split(s))
            {
                string t = XmlText.E(r.Text).Replace("\n", "<br/>");
                if (r.Bold) t = "<strong>" + t + "</strong>";
                if (r.Del) t = "<del>" + t + "</del>";
                if (r.Add) t = "<ins>" + t + "</ins>";
                sb.Append(t);
            }
            return sb.ToString();
        }

        static string Page(string title, string body, bool nav)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\"" + (nav ? " xmlns:epub=\"http://www.idpf.org/2007/ops\"" : "") + " xml:lang=\"ko\" lang=\"ko\">\n<head><meta charset=\"utf-8\"/><title>" + XmlText.E(title) + "</title><link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/></head>\n<body>\n" + body + "</body>\n</html>\n";
        }

        static string Body(Chapter c, List<KeyValuePair<string, byte[]>> images, ref int count)
        {
            var sb = new StringBuilder();
            string open = null;
            Action<string> list = delegate (string want)
            {
                if (open == want) return;
                if (open != null) sb.Append("</").Append(open).Append(">\n");
                if (want != null) sb.Append('<').Append(want).Append(">\n");
                open = want;
            };
            foreach (var b in c.Blocks)
            {
                switch (b.Kind)
                {
                    case BlockKind.Bullet:
                    case BlockKind.Number:
                        list(b.Kind == BlockKind.Bullet ? "ul" : "ol");
                        sb.Append("<li>").Append(InlineXhtml(b.Text)).Append("</li>\n");
                        break;
                    case BlockKind.Heading:
                        list(null);
                        sb.Append("<h").Append(b.Level).Append('>').Append(InlineXhtml(b.Text)).Append("</h").Append(b.Level).Append(">\n");
                        break;
                    case BlockKind.Para:
                        list(null);
                        sb.Append("<p>").Append(InlineXhtml(b.Text)).Append("</p>\n");
                        break;
                    case BlockKind.Table:
                        list(null);
                        sb.Append("<table>\n");
                        for (int i = 0; i < b.Rows.Count; i++)
                        {
                            string tag = i == 0 ? "th" : "td";
                            sb.Append("<tr>");
                            foreach (var cell in b.Rows[i]) sb.Append('<').Append(tag).Append('>').Append(InlineXhtml(cell)).Append("</").Append(tag).Append('>');
                            sb.Append("</tr>\n");
                        }
                        sb.Append("</table>\n");
                        break;
                    case BlockKind.Picture:
                        {
                            list(null);
                            count++;
                            string name = "images/img" + count + (b.ImageType == "png" ? ".png" : ".jpg");
                            images.Add(new KeyValuePair<string, byte[]>(name, b.Image));
                            sb.Append("<p class=\"pic\"><img src=\"").Append(name).Append("\" alt=\"\"/></p>\n");
                            break;
                        }
                    case BlockKind.PageBreak:
                        list(null);
                        sb.Append("<hr/>\n");
                        break;
                }
            }
            list(null);
            return sb.ToString();
        }

        public static void Write(string path, IList<DocBlock> blocks, string title)
        {
            var chapters = Split(blocks, title);
            var images = new List<KeyValuePair<string, byte[]>>();
            int pictures = 0;
            string id = "urn:uuid:" + Guid.NewGuid().ToString();
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var z = new StoredFirstZip(fs))
            {
                z.Add("mimetype", Encoding.ASCII.GetBytes("application/epub+zip"), true);
                z.AddText("META-INF/container.xml", XmlText.Head + "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
                z.AddText("OEBPS/style.css", Css);
                var manifest = new StringBuilder();
                var spine = new StringBuilder();
                var navList = new StringBuilder();
                var ncx = new StringBuilder();
                for (int i = 0; i < chapters.Count; i++)
                {
                    string file = "ch" + (i + 1).ToString("000") + ".xhtml";
                    z.AddText("OEBPS/" + file, Page(chapters[i].Title, Body(chapters[i], images, ref pictures), false));
                    manifest.Append("<item id=\"ch").Append(i + 1).Append("\" href=\"").Append(file).Append("\" media-type=\"application/xhtml+xml\"/>\n");
                    spine.Append("<itemref idref=\"ch").Append(i + 1).Append("\"/>\n");
                    navList.Append("<li><a href=\"").Append(file).Append("\">").Append(XmlText.E(chapters[i].Title)).Append("</a></li>\n");
                    ncx.Append("<navPoint id=\"p").Append(i + 1).Append("\" playOrder=\"").Append(i + 1).Append("\"><navLabel><text>").Append(XmlText.E(chapters[i].Title)).Append("</text></navLabel><content src=\"").Append(file).Append("\"/></navPoint>\n");
                }
                for (int i = 0; i < images.Count; i++)
                {
                    z.Add("OEBPS/" + images[i].Key, images[i].Value, true);
                    manifest.Append("<item id=\"img").Append(i + 1).Append("\" href=\"").Append(images[i].Key).Append("\" media-type=\"").Append(images[i].Key.EndsWith(".png") ? "image/png" : "image/jpeg").Append("\"/>\n");
                }
                z.AddText("OEBPS/nav.xhtml", Page("차례", "<nav epub:type=\"toc\" id=\"toc\"><h1>차례</h1><ol>\n" + navList + "</ol></nav>\n", true));
                z.AddText("OEBPS/toc.ncx", XmlText.Head + "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\"><head><meta name=\"dtb:uid\" content=\"" + id + "\"/></head><docTitle><text>" + XmlText.E(title) + "</text></docTitle><navMap>\n" + ncx + "</navMap></ncx>\n");
                z.AddText("OEBPS/content.opf", XmlText.Head + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" xml:lang=\"ko\">\n<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"bookid\">" + id + "</dc:identifier><dc:title>" + XmlText.E(title) + "</dc:title><dc:language>ko</dc:language><meta property=\"dcterms:modified\">" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "</meta></metadata>\n<manifest>\n<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>\n<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>\n" + manifest + "</manifest>\n<spine toc=\"ncx\">\n" + spine + "</spine>\n</package>\n");
            }
        }

        // ── 읽기 ──

        static byte[] Bytes(ZipArchiveEntry e, ref long total)
        {
            if (e.Length > MaxEntry || total + e.Length > MaxTotal) throw new UserFacingException("전자책 안의 파일이 너무 커서 열지 않았어요.");
            using (var s = e.Open())
            using (var m = new MemoryStream())
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    m.Write(buf, 0, n);
                    total += n;
                    if (m.Length > MaxEntry || total > MaxTotal) throw new UserFacingException("전자책 안의 파일이 너무 커서 열지 않았어요.");
                }
                return m.ToArray();
            }
        }

        static XmlDocument Xml(byte[] data)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            {
                doc.Load(r);
            }
            return doc;
        }

        static IEnumerable<XmlElement> Named(XmlNode n, string local)
        {
            return n.SelectNodes("//*[local-name()='" + local + "']").Cast<XmlElement>();
        }

        /// 책 안의 경로를 잇는다. 책 밖으로 나가는 경로는 null.
        static string Join(string baseDir, string href)
        {
            string h = href;
            int cut = h.IndexOfAny(new[] { '#', '?' });
            if (cut >= 0) h = h.Substring(0, cut);
            h = WebUtility.UrlDecode(h.Replace("+", "%2B"));
            if (h.Contains(":") || h.Length == 0) return null;
            var parts = new List<string>();
            foreach (var p in ((h.StartsWith("/") ? "" : baseDir) + h).Split('/'))
            {
                if (p.Length == 0 || p == ".") continue;
                if (p == "..")
                {
                    if (parts.Count == 0) return null;
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(p);
            }
            return string.Join("/", parts);
        }

        static ZipArchiveEntry Find(ZipArchive z, string name)
        {
            if (name == null) return null;
            return z.GetEntry(name) ?? z.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
        }

        public static List<DocBlock> Read(string path)
        {
            Util.EnsureReadable(path);
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    return ReadBook(z);
                }
            }
            catch (InvalidDataException)
            {
                throw new UserFacingException("전자책을 읽지 못했어요. 손상된 파일일 수 있어요.");
            }
            catch (XmlException)
            {
                throw new UserFacingException("전자책을 읽지 못했어요. 형식이 맞지 않는 파일일 수 있어요.");
            }
        }

        static List<DocBlock> ReadBook(ZipArchive z)
        {
            long total = 0;
            var enc = z.GetEntry("META-INF/encryption.xml");
            if (enc != null)
            {
                foreach (var m in Named(Xml(Bytes(enc, ref total)), "EncryptionMethod"))
                {
                    string a = m.GetAttribute("Algorithm");
                    if (a != "http://www.idpf.org/2008/embedding" && a != "http://ns.adobe.com/pdf/enc#RC") throw new UserFacingException("복제 방지가 걸린 전자책은 열 수 없어요.");
                }
            }
            var container = z.GetEntry("META-INF/container.xml");
            if (container == null) throw new UserFacingException("전자책이 아니거나 손상된 파일이에요.");
            var root = Named(Xml(Bytes(container, ref total)), "rootfile").FirstOrDefault();
            if (root == null) throw new UserFacingException("전자책이 아니거나 손상된 파일이에요.");
            string opfPath = root.GetAttribute("full-path");
            var opfEntry = Find(z, opfPath);
            if (opfEntry == null) throw new UserFacingException("전자책이 아니거나 손상된 파일이에요.");
            var opf = Xml(Bytes(opfEntry, ref total));
            int slash = opfPath.LastIndexOf('/');
            string baseDir = slash >= 0 ? opfPath.Substring(0, slash + 1) : "";
            var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in Named(opf, "item")) manifest[item.GetAttribute("id")] = item.GetAttribute("href");
            var blocks = new List<DocBlock>();
            int pages = 0;
            foreach (var r in Named(opf, "itemref"))
            {
                if (r.GetAttribute("linear") == "no") continue;
                string href;
                if (!manifest.TryGetValue(r.GetAttribute("idref"), out href)) continue;
                string name = Join(baseDir, href);
                var e = Find(z, name);
                if (e == null) continue;
                int at = name.LastIndexOf('/');
                string dir = at >= 0 ? name.Substring(0, at + 1) : "";
                long used = total;
                var part = MarkdownDoc.FromHtml(new UTF8Encoding(false).GetString(StripBom(Bytes(e, ref used))), delegate (string src)
                {
                    var pic = Find(z, Join(dir, src));
                    if (pic == null) return null;
                    long t = used;
                    byte[] data = Bytes(pic, ref t);
                    used = t;
                    return MarkdownDoc.Picture(data);
                });
                total = used;
                if (part.Count == 0) continue;
                if (pages++ > 0) blocks.Add(DocBlock.Break());
                blocks.AddRange(part);
            }
            if (blocks.Count == 0) throw new UserFacingException("전자책에서 글자를 찾지 못했어요.");
            return blocks;
        }

        static byte[] StripBom(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return b.Skip(3).ToArray();
            return b;
        }
    }
}
