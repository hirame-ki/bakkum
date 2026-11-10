// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 워드·엑셀·파워포인트 파일을 프로그램 없이 직접 쓰고 읽는다. 밖에서 쓰는 라이브러리는 없다.
// 글 덩어리(DocBlock)를 중간 형식으로 삼아 마크다운·HTML·글자 파일과 오간다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Bakkum
{
    static class XmlText
    {
        public static string E(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default:
                        if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') break;
                        if (c == 0xFFFE || c == 0xFFFF) break;
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

        public static void Put(ZipArchive z, string name, string text)
        {
            Put(z, name, new UTF8Encoding(false).GetBytes(text));
        }

        public static void Put(ZipArchive z, string name, byte[] data)
        {
            var e = z.CreateEntry(name, CompressionLevel.Optimal);
            using (var s = e.Open()) s.Write(data, 0, data.Length);
        }

        public static XmlReaderSettings Safe()
        {
            return new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        }
    }

    public struct InlineRun
    {
        public string Text;
        public bool Bold, Del, Add;
    }

    /// 글 안의 표시: **굵게**, [-지운 글-], {+넣은 글+}
    public static class Inline
    {
        public static List<InlineRun> Split(string s)
        {
            var list = new List<InlineRun>();
            if (string.IsNullOrEmpty(s)) return list;
            var sb = new StringBuilder();
            bool bold = false, del = false, add = false;
            Action flush = delegate
            {
                if (sb.Length == 0) return;
                list.Add(new InlineRun { Text = sb.ToString(), Bold = bold, Del = del, Add = add });
                sb.Clear();
            };
            for (int i = 0; i < s.Length; i++)
            {
                if (i + 1 < s.Length)
                {
                    string two = s.Substring(i, 2);
                    if (two == "**")
                    {
                        flush();
                        bold = !bold;
                        i++;
                        continue;
                    }
                    if (two == "[-" && !del)
                    {
                        flush();
                        del = true;
                        i++;
                        continue;
                    }
                    if (two == "-]" && del)
                    {
                        flush();
                        del = false;
                        i++;
                        continue;
                    }
                    if (two == "{+" && !add)
                    {
                        flush();
                        add = true;
                        i++;
                        continue;
                    }
                    if (two == "+}" && add)
                    {
                        flush();
                        add = false;
                        i++;
                        continue;
                    }
                }
                sb.Append(s[i]);
            }
            flush();
            return list;
        }
    }

    public enum BlockKind { Heading, Para, Bullet, Number, Table, Picture, PageBreak }

    public sealed class DocBlock
    {
        public BlockKind Kind = BlockKind.Para;
        public int Level = 1;
        public string Text = "";
        public int Number;
        public List<List<string>> Rows;
        /// 표 칸 너비(비율). 없으면 똑같이 나눈다.
        public int[] Widths;
        public byte[] Image;
        public string ImageType = "jpeg";
        public int W, H;

        public static DocBlock Para(string s)
        {
            return new DocBlock { Text = s };
        }

        public static DocBlock Head(string s, int level)
        {
            return new DocBlock { Kind = BlockKind.Heading, Text = s, Level = Math.Max(1, Math.Min(3, level)) };
        }

        public static DocBlock Break()
        {
            return new DocBlock { Kind = BlockKind.PageBreak };
        }
    }

    public static class MarkdownDoc
    {
        static readonly Regex HeadRe = new Regex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$");
        static readonly Regex BulletRe = new Regex(@"^(\s*)[-*+•]\s+(.*)$");
        static readonly Regex NumberRe = new Regex(@"^(\s*)(\d{1,4})[.)]\s+(.*)$");
        static readonly Regex RuleRe = new Regex(@"^\s*([-*_])(\s*\1){2,}\s*$");
        static readonly Regex TableSepRe = new Regex(@"^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?\s*$");
        static readonly Regex BrRe = new Regex(@"<br\s*/?>", RegexOptions.IgnoreCase);

        static List<string> SplitRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|") && !t.EndsWith("\\|")) t = t.Substring(0, t.Length - 1);
            var cells = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|')
                {
                    sb.Append('|');
                    i++;
                }
                else if (t[i] == '|')
                {
                    cells.Add(BrRe.Replace(sb.ToString().Trim(), "\n"));
                    sb.Clear();
                }
                else sb.Append(t[i]);
            }
            cells.Add(BrRe.Replace(sb.ToString().Trim(), "\n"));
            return cells;
        }

        public static List<DocBlock> Parse(string md)
        {
            var blocks = new List<DocBlock>();
            var lines = (md ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var para = new StringBuilder();
            Action flush = delegate
            {
                if (para.Length == 0) return;
                blocks.Add(DocBlock.Para(para.ToString()));
                para.Clear();
            };
            bool fence = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string t = line.Trim();
                if (t.StartsWith("```"))
                {
                    flush();
                    fence = !fence;
                    continue;
                }
                if (fence)
                {
                    if (t.Length > 0) blocks.Add(DocBlock.Para(line.TrimEnd()));
                    continue;
                }
                if (t.Length == 0)
                {
                    flush();
                    continue;
                }
                if (t.StartsWith("<!--") && t.EndsWith("-->"))
                {
                    flush();
                    // AI가 읽은 글의 쪽 표시: 둘째 쪽부터 쪽을 나눈다.
                    if (t.StartsWith("<!-- p.") && blocks.Count > 0) blocks.Add(DocBlock.Break());
                    continue;
                }
                if (t.StartsWith("--- ") && t.EndsWith("쪽 ---"))
                {
                    flush();
                    if (blocks.Count > 0) blocks.Add(DocBlock.Break());
                    continue;
                }
                // 객관식 보기(①~⑳)는 한 줄씩 들여 쓴다.
                if (t[0] >= '\u2460' && t[0] <= '\u2473')
                {
                    flush();
                    blocks.Add(new DocBlock { Kind = BlockKind.Para, Text = t, Level = 2 });
                    continue;
                }
                if (t.StartsWith("|") && i + 1 < lines.Length && TableSepRe.IsMatch(lines[i + 1]) && lines[i + 1].Contains("-"))
                {
                    flush();
                    var rows = new List<List<string>> { SplitRow(t) };
                    i += 2;
                    while (i < lines.Length && lines[i].Trim().StartsWith("|"))
                    {
                        rows.Add(SplitRow(lines[i]));
                        i++;
                    }
                    i--;
                    int cols = rows.Max(r => r.Count);
                    foreach (var r in rows)
                    {
                        while (r.Count < cols) r.Add("");
                    }
                    blocks.Add(new DocBlock { Kind = BlockKind.Table, Rows = rows });
                    continue;
                }
                var m = HeadRe.Match(line);
                if (m.Success)
                {
                    flush();
                    blocks.Add(DocBlock.Head(m.Groups[2].Value, m.Groups[1].Length));
                    continue;
                }
                if (RuleRe.IsMatch(line))
                {
                    flush();
                    continue;
                }
                m = NumberRe.Match(line);
                if (m.Success)
                {
                    flush();
                    blocks.Add(new DocBlock { Kind = BlockKind.Number, Number = int.Parse(m.Groups[2].Value), Text = m.Groups[3].Value, Level = 1 + m.Groups[1].Length / 2 });
                    continue;
                }
                m = BulletRe.Match(line);
                if (m.Success)
                {
                    flush();
                    blocks.Add(new DocBlock { Kind = BlockKind.Bullet, Text = m.Groups[2].Value, Level = 1 + m.Groups[1].Length / 2 });
                    continue;
                }
                if (t.StartsWith(">")) t = t.TrimStart('>').Trim();
                if (para.Length > 0) para.Append(' ');
                para.Append(BrRe.Replace(t, "\n"));
            }
            flush();
            return blocks;
        }

        /// 줄마다 문단. '--- n쪽 ---' 줄은 쪽 나눔으로 바꾼다.
        public static List<DocBlock> FromPlain(string text)
        {
            var blocks = new List<DocBlock>();
            bool any = false;
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string t = raw.TrimEnd();
                if (t.Trim().Length == 0) continue;
                if (t.StartsWith("--- ") && t.EndsWith("쪽 ---"))
                {
                    if (any) blocks.Add(DocBlock.Break());
                    continue;
                }
                any = true;
                blocks.Add(DocBlock.Para(t));
            }
            return blocks;
        }

        /// 그림 파일을 덩어리로. 읽지 못하면 null(문서는 계속 옮긴다).
        public static DocBlock Picture(byte[] data)
        {
            try
            {
                var frame = Imaging.Decoder(data).Frames[0];
                var ji = Imaging.ReadJpeg(data);
                if (ji != null && ji.Orientation == 1 && !ji.Progressive && !ji.Arithmetic && ji.Precision == 8 && (ji.Components == 1 || ji.Components == 3) && Math.Max(ji.Width, ji.Height) <= 2400)
                {
                    return new DocBlock { Kind = BlockKind.Picture, Image = data, ImageType = "jpeg", W = ji.Width, H = ji.Height };
                }
                var fit = Imaging.Fit(Imaging.Upright(frame), 2400);
                bool clear = Imaging.MayHaveAlpha(fit);
                return new DocBlock { Kind = BlockKind.Picture, Image = clear ? Imaging.Encode(fit, "png", 0) : Imaging.Jpeg(fit, 88), ImageType = clear ? "png" : "jpeg", W = fit.PixelWidth, H = fit.PixelHeight };
            }
            catch (Exception ex)
            {
                Log.Write("문서 속 그림 건너뜀: " + ex.GetType().Name);
                return null;
            }
        }

        static DocBlock DataPicture(string uri)
        {
            var m = Regex.Match(uri, @"^data:image/(png|jpe?g|gif|bmp|webp)\s*;\s*base64\s*,(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            try
            {
                return Picture(Convert.FromBase64String(Regex.Replace(m.Groups[2].Value, @"\s+", "")));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// 표시를 뗀 글자만
        public static string ToPlain(IList<DocBlock> blocks)
        {
            Func<string, string> clean = s => string.Concat(Inline.Split(s).Select(r => r.Text));
            var sb = new StringBuilder();
            bool inList = false;
            foreach (var b in blocks)
            {
                bool isList = b.Kind == BlockKind.Bullet || b.Kind == BlockKind.Number;
                if (inList && !isList) sb.Append('\n');
                inList = isList;
                switch (b.Kind)
                {
                    case BlockKind.Heading:
                    case BlockKind.Para:
                        sb.Append(clean(b.Text)).Append("\n\n");
                        break;
                    case BlockKind.Bullet:
                        sb.Append(' ', (b.Level - 1) * 2).Append("- ").Append(clean(b.Text)).Append('\n');
                        break;
                    case BlockKind.Number:
                        sb.Append(' ', (b.Level - 1) * 2).Append(b.Number).Append(". ").Append(clean(b.Text)).Append('\n');
                        break;
                    case BlockKind.Table:
                        foreach (var row in b.Rows) sb.Append(string.Join("\t", row.Select(c => clean(c).Replace('\n', ' ')))).Append('\n');
                        sb.Append('\n');
                        break;
                    case BlockKind.PageBreak:
                        sb.Append('\n');
                        break;
                }
            }
            return sb.ToString().TrimEnd() + "\n";
        }

        public static string ToMarkdown(IList<DocBlock> blocks)
        {
            var sb = new StringBuilder();
            bool inList = false;
            foreach (var b in blocks)
            {
                // 목록 뒤에는 빈 줄이 있어야 다음 덩어리가 목록에 빨려 들어가지 않는다.
                bool isList = b.Kind == BlockKind.Bullet || b.Kind == BlockKind.Number;
                if (inList && !isList) sb.Append('\n');
                inList = isList;
                switch (b.Kind)
                {
                    case BlockKind.Heading:
                        sb.Append('#', b.Level).Append(' ').Append(b.Text).Append("\n\n");
                        break;
                    case BlockKind.Para:
                        sb.Append(b.Text.Replace("\n", "  \n")).Append("\n\n");
                        break;
                    case BlockKind.Bullet:
                        sb.Append(' ', (b.Level - 1) * 2).Append("- ").Append(b.Text).Append('\n');
                        break;
                    case BlockKind.Number:
                        sb.Append(' ', (b.Level - 1) * 2).Append(b.Number).Append(". ").Append(b.Text).Append('\n');
                        break;
                    case BlockKind.Table:
                        for (int i = 0; i < b.Rows.Count; i++)
                        {
                            sb.Append("| ").Append(string.Join(" | ", b.Rows[i].Select(c => c.Replace("|", "\\|").Replace("\n", "<br>")))).Append(" |\n");
                            if (i == 0) sb.Append('|').Append(string.Join("|", b.Rows[0].Select(c => " --- "))).Append("|\n");
                        }
                        sb.Append('\n');
                        break;
                    case BlockKind.PageBreak:
                        sb.Append("---\n\n");
                        break;
                }
            }
            return sb.ToString().TrimEnd() + "\n";
        }

        static string HtmlInline(string s)
        {
            var sb = new StringBuilder();
            foreach (var r in Inline.Split(s))
            {
                string t = WebUtility.HtmlEncode(r.Text).Replace("\n", "<br>");
                if (r.Bold) t = "<strong>" + t + "</strong>";
                if (r.Del) t = "<del>" + t + "</del>";
                if (r.Add) t = "<ins>" + t + "</ins>";
                sb.Append(t);
            }
            return sb.ToString();
        }

        public static string ToHtml(IList<DocBlock> blocks, string title)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html>\n<html lang=\"ko\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>")
              .Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n")
              .Append("body{font-family:'Malgun Gothic',sans-serif;line-height:1.7;max-width:46rem;margin:2.5rem auto;padding:0 1rem;color:#222}\n")
              .Append("table{border-collapse:collapse;margin:1rem 0}th,td{border:1px solid #bbb;padding:.35rem .6rem;vertical-align:top}th{background:#f0f0f0}\n")
              .Append("del{color:#c0392b}ins{color:#1f6feb}\n</style>\n</head>\n<body>\n");
            string open = null;
            Action<string> list = delegate (string want)
            {
                if (open == want) return;
                if (open != null) sb.Append("</").Append(open).Append(">\n");
                if (want != null) sb.Append('<').Append(want).Append(">\n");
                open = want;
            };
            foreach (var b in blocks)
            {
                switch (b.Kind)
                {
                    case BlockKind.Bullet:
                        list("ul");
                        sb.Append("<li>").Append(HtmlInline(b.Text)).Append("</li>\n");
                        break;
                    case BlockKind.Number:
                        list("ol");
                        sb.Append("<li>").Append(HtmlInline(b.Text)).Append("</li>\n");
                        break;
                    case BlockKind.Heading:
                        list(null);
                        sb.Append("<h").Append(b.Level).Append('>').Append(HtmlInline(b.Text)).Append("</h").Append(b.Level).Append(">\n");
                        break;
                    case BlockKind.Para:
                        list(null);
                        if (b.Level > 1) sb.Append("<p style=\"margin-left:1.5em\">");
                        else sb.Append("<p>");
                        sb.Append(HtmlInline(b.Text)).Append("</p>\n");
                        break;
                    case BlockKind.Table:
                        list(null);
                        sb.Append(b.Widths == null ? "<table border=\"1\">\n" : "<table border=\"1\" width=\"100%\">\n");
                        for (int i = 0; i < b.Rows.Count; i++)
                        {
                            string tag = i == 0 ? "th" : "td";
                            sb.Append("<tr>");
                            for (int c = 0; c < b.Rows[i].Count; c++)
                            {
                                sb.Append('<').Append(tag);
                                if (b.Widths != null) sb.Append(" style=\"padding:4px 6px\"");
                                if (i == 0 && b.Widths != null && c < b.Widths.Length) sb.Append(" width=\"").Append(b.Widths[c] * 100 / b.Widths.Sum()).Append("%\"");
                                sb.Append('>').Append(HtmlInline(b.Rows[i][c])).Append("</").Append(tag).Append('>');
                            }
                            sb.Append("</tr>\n");
                        }
                        sb.Append("</table>\n");
                        break;
                    case BlockKind.Picture:
                        list(null);
                        sb.Append("<p style=\"text-align:center\"><img style=\"max-width:100%\" alt=\"\" src=\"data:image/").Append(b.ImageType == "png" ? "png" : "jpeg").Append(";base64,").Append(Convert.ToBase64String(b.Image)).Append("\"></p>\n");
                        break;
                    case BlockKind.PageBreak:
                        list(null);
                        sb.Append("<hr>\n");
                        break;
                }
            }
            list(null);
            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }

        static readonly Regex TagRe = new Regex(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)([^<>]*)>", RegexOptions.Compiled);

        /// image가 있으면 src를 그림으로 바꿔 준다(전자책 안의 그림 등). 없으면 문서 안에 든 data: 그림만 쓴다.
        public static List<DocBlock> FromHtml(string html, Func<string, DocBlock> image = null)
        {
            string s = Regex.Replace(html ?? "", "<!--.*?-->", "", RegexOptions.Singleline);
            s = Regex.Replace(s, @"<[!?][^>]*>", "");
            s = Regex.Replace(s, @"<(script|style|head|noscript|template)\b.*?</\1\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var blocks = new List<DocBlock>();
            var text = new StringBuilder();
            BlockKind kind = BlockKind.Para;
            int level = 1, number = 0;
            var lists = new Stack<int[]>();
            List<List<string>> rows = null;
            List<string> row = null;
            bool pre = false;
            Action flush = delegate
            {
                string t = text.ToString();
                text.Clear();
                t = pre ? t.Trim('\n') : Regex.Replace(t.Replace("\n ", "\n").Replace(" \n", "\n"), @"[ \t]+", " ").Trim();
                if (row != null)
                {
                    return;
                }
                if (t.Length == 0) return;
                blocks.Add(new DocBlock { Kind = kind, Level = level, Number = number, Text = t });
            };
            Action<string> addText = delegate (string raw)
            {
                string d = WebUtility.HtmlDecode(raw);
                if (pre)
                {
                    text.Append(d);
                    return;
                }
                text.Append(Regex.Replace(d, @"[\r\n\t ]+", " "));
            };
            int pos = 0;
            foreach (Match m in TagRe.Matches(s))
            {
                if (m.Index > pos) addText(s.Substring(pos, m.Index - pos));
                pos = m.Index + m.Length;
                bool close = m.Groups[1].Length > 0;
                string tag = m.Groups[2].Value.ToLowerInvariant();
                switch (tag)
                {
                    case "img":
                        if (!close && row == null)
                        {
                            var at = Regex.Match(m.Groups[3].Value, "\\bsrc\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase);
                            if (at.Success)
                            {
                                string src = WebUtility.HtmlDecode(at.Groups[1].Success ? at.Groups[1].Value : at.Groups[2].Success ? at.Groups[2].Value : at.Groups[3].Value).Trim();
                                DocBlock pic = src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? DataPicture(src) : image != null ? image(src) : null;
                                if (pic != null)
                                {
                                    flush();
                                    blocks.Add(pic);
                                }
                            }
                        }
                        break;
                    case "br":
                        text.Append('\n');
                        break;
                    case "b":
                    case "strong":
                        text.Append("**");
                        break;
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                        flush();
                        kind = close ? BlockKind.Para : BlockKind.Heading;
                        level = close ? 1 : Math.Min(3, tag[1] - '0');
                        break;
                    case "pre":
                        flush();
                        pre = !close;
                        break;
                    case "p": case "div": case "section": case "article": case "header": case "footer": case "main": case "blockquote": case "aside": case "nav":
                        if (rows == null || row == null) flush();
                        else text.Append(close ? "\n" : "");
                        break;
                    case "ul":
                    case "ol":
                        flush();
                        if (close)
                        {
                            if (lists.Count > 0) lists.Pop();
                            kind = BlockKind.Para;
                            level = 1;
                        }
                        else lists.Push(new[] { tag == "ol" ? 1 : 0, 0 });
                        break;
                    case "li":
                        flush();
                        if (close)
                        {
                            kind = BlockKind.Para;
                            level = 1;
                        }
                        else if (lists.Count > 0)
                        {
                            var l = lists.Peek();
                            kind = l[0] == 1 ? BlockKind.Number : BlockKind.Bullet;
                            l[1]++;
                            number = l[1];
                            level = lists.Count;
                        }
                        else kind = BlockKind.Bullet;
                        break;
                    case "table":
                        if (!close)
                        {
                            flush();
                            rows = new List<List<string>>();
                        }
                        else if (rows != null)
                        {
                            row = null;
                            text.Clear();
                            if (rows.Count > 0)
                            {
                                int cols = rows.Max(r => r.Count);
                                foreach (var r in rows)
                                {
                                    while (r.Count < cols) r.Add("");
                                }
                                blocks.Add(new DocBlock { Kind = BlockKind.Table, Rows = rows });
                            }
                            rows = null;
                        }
                        break;
                    case "tr":
                        if (!close && rows != null)
                        {
                            row = new List<string>();
                            rows.Add(row);
                        }
                        break;
                    case "td":
                    case "th":
                        if (close && row != null)
                        {
                            string cell = Regex.Replace(text.ToString().Replace("\n ", "\n").Replace(" \n", "\n"), @"[ \t]+", " ").Trim();
                            row.Add(cell);
                            text.Clear();
                        }
                        else if (!close) text.Clear();
                        break;
                }
            }
            if (pos < s.Length) addText(s.Substring(pos));
            row = null;
            flush();
            return blocks;
        }
    }

    // ── 워드 ──

    public static class DocxWriter
    {
        const long TextW = 6120000, TextH = 8820000;
        const int TextWTwips = 9638;
        const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

        static void Runs(StringBuilder sb, string text, bool forceBold)
        {
            foreach (var r in Inline.Split(text))
            {
                var rpr = new StringBuilder();
                if (r.Bold || forceBold) rpr.Append("<w:b/>");
                if (r.Del) rpr.Append("<w:strike/><w:color w:val=\"C0392B\"/>");
                if (r.Add) rpr.Append("<w:color w:val=\"1F6FEB\"/><w:u w:val=\"single\"/>");
                sb.Append("<w:r>");
                if (rpr.Length > 0) sb.Append("<w:rPr>").Append(rpr).Append("</w:rPr>");
                var parts = r.Text.Split('\n');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) sb.Append("<w:br/>");
                    var cells = parts[i].Split('\t');
                    for (int k = 0; k < cells.Length; k++)
                    {
                        if (k > 0) sb.Append("<w:tab/>");
                        if (cells[k].Length > 0) sb.Append("<w:t xml:space=\"preserve\">").Append(XmlText.E(cells[k])).Append("</w:t>");
                    }
                }
                sb.Append("</w:r>");
            }
        }

        static void Table(StringBuilder sb, List<List<string>> rows, int[] widths)
        {
            int cols = rows.Max(r => r.Count);
            if (cols == 0) return;
            bool fitted = widths != null && widths.Length == cols && widths.Sum() > 0;
            var cw = new int[cols];
            for (int i = 0; i < cols; i++) cw[i] = fitted ? (int)((long)TextWTwips * widths[i] / widths.Sum()) : TextWTwips / cols;
            sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"").Append(TextWTwips).Append("\" w:type=\"dxa\"/><w:tblBorders>");
            foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            {
                sb.Append("<w:").Append(side).Append(" w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"999999\"/>");
            }
            sb.Append("</w:tblBorders></w:tblPr><w:tblGrid>");
            for (int i = 0; i < cols; i++) sb.Append("<w:gridCol w:w=\"").Append(cw[i]).Append("\"/>");
            sb.Append("</w:tblGrid>");
            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append("<w:tr>");
                for (int c = 0; c < cols; c++)
                {
                    sb.Append("<w:tc><w:tcPr><w:tcW w:w=\"").Append(cw[c]).Append("\" w:type=\"dxa\"/>");
                    if (r == 0) sb.Append("<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"EFEFEF\"/>");
                    sb.Append("</w:tcPr><w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr>");
                    Runs(sb, c < rows[r].Count ? rows[r][c] : "", r == 0);
                    sb.Append("</w:p></w:tc>");
                }
                sb.Append("</w:tr>");
            }
            sb.Append("</w:tbl>");
        }

        public static void Write(string path, IList<DocBlock> blocks)
        {
            var body = new StringBuilder();
            var rels = new StringBuilder();
            var media = new List<KeyValuePair<string, byte[]>>();
            bool png = false, jpeg = false;
            rels.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            int pic = 0;
            foreach (var b in blocks)
            {
                switch (b.Kind)
                {
                    case BlockKind.Heading:
                        body.Append("<w:p><w:pPr><w:pStyle w:val=\"Heading").Append(b.Level).Append("\"/></w:pPr>");
                        Runs(body, b.Text, false);
                        body.Append("</w:p>");
                        break;
                    case BlockKind.Para:
                        body.Append("<w:p>");
                        if (b.Level > 1) body.Append("<w:pPr><w:spacing w:after=\"40\"/><w:ind w:left=\"").Append(360 * (b.Level - 1)).Append("\"/></w:pPr>");
                        Runs(body, b.Text, false);
                        body.Append("</w:p>");
                        break;
                    case BlockKind.Bullet:
                    case BlockKind.Number:
                        {
                            int left = 360 * b.Level;
                            body.Append("<w:p><w:pPr><w:spacing w:after=\"60\"/><w:ind w:left=\"").Append(left).Append("\" w:hanging=\"360\"/></w:pPr>");
                            Runs(body, (b.Kind == BlockKind.Bullet ? "•" : b.Number + ".") + "\u00A0" + b.Text, false);
                            body.Append("</w:p>");
                            break;
                        }
                    case BlockKind.Table:
                        Table(body, b.Rows, b.Widths);
                        // 표 바로 뒤에는 문단이 있어야 한다(문서 끝이 표면 워드가 파일을 고치려 든다).
                        body.Append("<w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr></w:p>");
                        break;
                    case BlockKind.PageBreak:
                        body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                        break;
                    case BlockKind.Picture:
                        {
                            pic++;
                            string ext = b.ImageType == "png" ? "png" : "jpeg";
                            if (ext == "png") png = true;
                            else jpeg = true;
                            string target = "media/image" + pic + "." + ext;
                            media.Add(new KeyValuePair<string, byte[]>("word/" + target, b.Image));
                            string rid = "rId" + (pic + 1);
                            rels.Append("<Relationship Id=\"").Append(rid).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"").Append(target).Append("\"/>");
                            double s = Math.Min(TextW / (double)b.W, TextH / (double)b.H);
                            long cx = (long)(b.W * s), cy = (long)(b.H * s);
                            body.Append("<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"").Append(cx).Append("\" cy=\"").Append(cy)
                                .Append("\"/><wp:docPr id=\"").Append(pic).Append("\" name=\"그림 ").Append(pic).Append("\"/><a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:nvPicPr><pic:cNvPr id=\"")
                                .Append(pic).Append("\" name=\"image").Append(pic).Append("\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"").Append(rid).Append("\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"")
                                .Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
                            break;
                        }
                }
            }
            if (body.Length == 0) body.Append("<w:p/>");
            string doc = XmlText.Head + "<w:document " + W + " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"><w:body>" + body
                + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"567\" w:footer=\"567\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";
            var ct = new StringBuilder(XmlText.Head);
            ct.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            if (png) ct.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
            if (jpeg) ct.Append("<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>");
            ct.Append("<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                XmlText.Put(z, "[Content_Types].xml", ct.ToString());
                XmlText.Put(z, "_rels/.rels", XmlText.Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
                XmlText.Put(z, "word/document.xml", doc);
                XmlText.Put(z, "word/_rels/document.xml.rels", XmlText.Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + rels + "</Relationships>");
                XmlText.Put(z, "word/styles.xml", Styles());
                foreach (var m in media) XmlText.Put(z, m.Key, m.Value);
            }
        }

        static string Styles()
        {
            const string F = "맑은 고딕";
            var sb = new StringBuilder(XmlText.Head);
            sb.Append("<w:styles ").Append(W).Append("><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"").Append(F).Append("\" w:hAnsi=\"").Append(F).Append("\" w:eastAsia=\"").Append(F).Append("\" w:cs=\"").Append(F)
              .Append("\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\" w:eastAsia=\"ko-KR\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"300\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>")
              .Append("<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>");
            int[] sizes = { 36, 30, 26 };
            for (int i = 0; i < 3; i++)
            {
                sb.Append("<w:style w:type=\"paragraph\" w:styleId=\"Heading").Append(i + 1).Append("\"><w:name w:val=\"heading ").Append(i + 1).Append("\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"")
                  .Append(280 - i * 40).Append("\" w:after=\"120\"/><w:outlineLvl w:val=\"").Append(i).Append("\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"").Append(sizes[i]).Append("\"/><w:szCs w:val=\"").Append(sizes[i]).Append("\"/></w:rPr></w:style>");
            }
            sb.Append("<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"40\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"40\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style></w:styles>");
            return sb.ToString();
        }
    }

    // ── 엑셀 ──

    public sealed class XlsxSheet
    {
        public string Name = "시트1";
        public List<List<string>> Rows = new List<List<string>>();
        /// 행1, 열1, 행2, 열2 (0부터)
        public List<int[]> Merges = new List<int[]>();
        public bool Header;
        public bool Grid;
    }

    public static class XlsxWriter
    {
        static readonly Regex Numeric = new Regex(@"^-?(0|[1-9]\d{0,14})(\.\d{1,12})?$");

        /// 앞이 0인 값(학번·전화번호)과 15자리를 넘는 값은 글자로 둔다.
        static bool IsNumber(string s)
        {
            return Numeric.IsMatch(s);
        }

        public static string ColName(int c)
        {
            var sb = new StringBuilder();
            for (c++; c > 0; c = (c - 1) / 26) sb.Insert(0, (char)('A' + (c - 1) % 26));
            return sb.ToString();
        }

        static string SafeSheetName(string name, HashSet<string> used)
        {
            string n = Regex.Replace(name ?? "", @"[\[\]:*?/\\']", " ").Trim();
            if (n.Length == 0) n = "시트";
            if (n.Length > 28) n = n.Substring(0, 28);
            string candidate = n;
            for (int i = 2; !used.Add(candidate.ToLowerInvariant()); i++) candidate = n + " " + i;
            return candidate;
        }

        static double WidthOf(string s)
        {
            double best = 0;
            foreach (var line in s.Split('\n'))
            {
                double w = 0;
                foreach (char c in line) w += c < 0x80 ? 1 : 2;
                if (w > best) best = w;
            }
            return best;
        }

        public static void Write(string path, IList<XlsxSheet> sheets)
        {
            var shared = new Dictionary<string, int>(StringComparer.Ordinal);
            var strings = new List<string>();
            var used = new HashSet<string>();
            var book = new StringBuilder(XmlText.Head);
            book.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var rels = new StringBuilder(XmlText.Head);
            rels.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            var ct = new StringBuilder(XmlText.Head);
            ct.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                for (int si = 0; si < sheets.Count; si++)
                {
                    var sh = sheets[si];
                    int cols = sh.Rows.Count == 0 ? 0 : sh.Rows.Max(r => r.Count);
                    var widths = new double[cols];
                    var data = new StringBuilder();
                    for (int r = 0; r < sh.Rows.Count; r++)
                    {
                        data.Append("<row r=\"").Append(r + 1).Append("\">");
                        for (int c = 0; c < cols; c++)
                        {
                            string v = c < sh.Rows[r].Count ? (sh.Rows[r][c] ?? "") : "";
                            int style = sh.Grid ? ((sh.Header && r == 0) ? 1 : 2) : ((sh.Header && r == 0) ? 1 : 0);
                            if (v.Length == 0)
                            {
                                if (style != 0) data.Append("<c r=\"").Append(ColName(c)).Append(r + 1).Append("\" s=\"").Append(style).Append("\"/>");
                                continue;
                            }
                            widths[c] = Math.Max(widths[c], WidthOf(v));
                            data.Append("<c r=\"").Append(ColName(c)).Append(r + 1).Append('"');
                            if (style != 0) data.Append(" s=\"").Append(style).Append('"');
                            if (IsNumber(v)) data.Append("><v>").Append(v).Append("</v></c>");
                            else
                            {
                                int id;
                                if (!shared.TryGetValue(v, out id))
                                {
                                    id = strings.Count;
                                    strings.Add(v);
                                    shared[v] = id;
                                }
                                data.Append(" t=\"s\"><v>").Append(id).Append("</v></c>");
                            }
                        }
                        data.Append("</row>");
                    }
                    var xml = new StringBuilder(XmlText.Head);
                    xml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                    if (sh.Header) xml.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                    if (cols > 0)
                    {
                        xml.Append("<cols>");
                        for (int c = 0; c < cols; c++)
                        {
                            double w = Math.Max(6, Math.Min(60, widths[c] + 2));
                            xml.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1).Append("\" width=\"").Append(w.ToString("0.#", CultureInfo.InvariantCulture)).Append("\" customWidth=\"1\"/>");
                        }
                        xml.Append("</cols>");
                    }
                    xml.Append("<sheetData>").Append(data).Append("</sheetData>");
                    if (sh.Merges.Count > 0)
                    {
                        xml.Append("<mergeCells count=\"").Append(sh.Merges.Count).Append("\">");
                        foreach (var m in sh.Merges) xml.Append("<mergeCell ref=\"").Append(ColName(m[1])).Append(m[0] + 1).Append(':').Append(ColName(m[3])).Append(m[2] + 1).Append("\"/>");
                        xml.Append("</mergeCells>");
                    }
                    xml.Append("</worksheet>");
                    XmlText.Put(z, "xl/worksheets/sheet" + (si + 1) + ".xml", xml.ToString());
                    book.Append("<sheet name=\"").Append(XmlText.E(SafeSheetName(sh.Name, used))).Append("\" sheetId=\"").Append(si + 1).Append("\" r:id=\"rId").Append(si + 1).Append("\"/>");
                    rels.Append("<Relationship Id=\"rId").Append(si + 1).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet").Append(si + 1).Append(".xml\"/>");
                    ct.Append("<Override PartName=\"/xl/worksheets/sheet").Append(si + 1).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                }
                book.Append("</sheets></workbook>");
                int n = sheets.Count;
                rels.Append("<Relationship Id=\"rId").Append(n + 1).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"rId").Append(n + 2).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>");
                ct.Append("</Types>");
                var sst = new StringBuilder(XmlText.Head);
                sst.Append("<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"").Append(strings.Count).Append("\" uniqueCount=\"").Append(strings.Count).Append("\">");
                foreach (var s in strings) sst.Append("<si><t xml:space=\"preserve\">").Append(XmlText.E(s)).Append("</t></si>");
                sst.Append("</sst>");
                XmlText.Put(z, "[Content_Types].xml", ct.ToString());
                XmlText.Put(z, "_rels/.rels", XmlText.Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                XmlText.Put(z, "xl/workbook.xml", book.ToString());
                XmlText.Put(z, "xl/_rels/workbook.xml.rels", rels.ToString());
                XmlText.Put(z, "xl/styles.xml", XlStyles);
                XmlText.Put(z, "xl/sharedStrings.xml", sst.ToString());
            }
        }

        const string XlStyles = XmlText.Head + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"맑은 고딕\"/><family val=\"2\"/></font><font><b/><sz val=\"11\"/><name val=\"맑은 고딕\"/><family val=\"2\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEFEFEF\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style=\"thin\"><color rgb=\"FFBBBBBB\"/></left><right style=\"thin\"><color rgb=\"FFBBBBBB\"/></right><top style=\"thin\"><color rgb=\"FFBBBBBB\"/></top><bottom style=\"thin\"><color rgb=\"FFBBBBBB\"/></bottom><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
    }

    // ── 파워포인트 ──

    public sealed class PptSlide
    {
        public byte[] Image;
        public string ImageType = "jpeg";
        public int W, H;
        public string Title;
        public List<string> Bullets;
    }

    public static class PptxWriter
    {
        const string Ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
        const string Group = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";
        const string RelNs = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">";
        const string RelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

        /// 슬라이드 크기(EMU). 긴 변을 PowerPoint 기본 크기에 맞춘다.
        public static void SizeFor(int w, int h, out long cx, out long cy)
        {
            if (w >= h)
            {
                cx = 12192000;
                cy = Math.Max(914400, (long)(cx * (double)h / w));
            }
            else
            {
                cy = 9144000;
                cx = Math.Max(914400, (long)(cy * (double)w / h));
            }
        }

        static string Placeholder(int id, string name, string ph, long x, long y, long w, long h, string body)
        {
            return "<p:sp><p:nvSpPr><p:cNvPr id=\"" + id + "\" name=\"" + name + "\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph " + ph + "/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"" + x + "\" y=\"" + y + "\"/><a:ext cx=\"" + w + "\" cy=\"" + h + "\"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/>" + body + "</p:txBody></p:sp>";
        }

        static string Para(string text)
        {
            return "<a:p><a:r><a:rPr lang=\"ko-KR\"/><a:t>" + XmlText.E(text) + "</a:t></a:r></a:p>";
        }

        public static void Write(string path, IList<PptSlide> slides, long cx, long cy)
        {
            long mx = cx / 16, ty = cy / 18, th = cy * 3 / 16, by = ty + th + cy / 40, bw = cx - 2 * mx, bh = cy - by - cy / 14;
            string master = XmlText.Head + "<p:sldMaster " + Ns + "><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>" + Group
                + Placeholder(2, "제목", "type=\"title\"", mx, ty, bw, th, Para("제목")) + Placeholder(3, "내용", "type=\"body\" idx=\"1\"", mx, by, bw, bh, Para("내용"))
                + "</p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"3200\" b=\"1\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr marL=\"342900\" indent=\"-342900\"><a:spcBef><a:spcPts val=\"1000\"/></a:spcBef><a:buChar char=\"&#8226;\"/><a:defRPr sz=\"2200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:bodyStyle><p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1800\"/></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>";
            string layout = XmlText.Head + "<p:sldLayout " + Ns + " type=\"obj\" preserve=\"1\"><p:cSld name=\"제목 및 내용\"><p:spTree>" + Group
                + "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"제목\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>" + Para("제목") + "</p:txBody></p:sp>"
                + "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"내용\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>" + Para("내용") + "</p:txBody></p:sp>"
                + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";
            var ids = new StringBuilder();
            var presRels = new StringBuilder(XmlText.Head + RelNs);
            presRels.Append("<Relationship Id=\"rId1\" Type=\"").Append(RelType).Append("slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/><Relationship Id=\"rId2\" Type=\"").Append(RelType).Append("theme\" Target=\"theme/theme1.xml\"/>");
            var ct = new StringBuilder(XmlText.Head);
            ct.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/><Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/><Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/><Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/><Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                for (int i = 0; i < slides.Count; i++)
                {
                    var s = slides[i];
                    int n = i + 1;
                    string rels = XmlText.Head + RelNs + "<Relationship Id=\"rId1\" Type=\"" + RelType + "slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>";
                    string shapes;
                    if (s.Image != null)
                    {
                        string ext = s.ImageType == "png" ? "png" : "jpeg";
                        XmlText.Put(z, "ppt/media/image" + n + "." + ext, s.Image);
                        rels += "<Relationship Id=\"rId2\" Type=\"" + RelType + "image\" Target=\"../media/image" + n + "." + ext + "\"/>";
                        double scale = Math.Min((double)cx / s.W, (double)cy / s.H);
                        long w = (long)(s.W * scale), h = (long)(s.H * scale);
                        shapes = "<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"그림 " + n + "\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x=\"" + (cx - w) / 2 + "\" y=\"" + (cy - h) / 2 + "\"/><a:ext cx=\"" + w + "\" cy=\"" + h + "\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>";
                    }
                    else
                    {
                        string title = "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"제목\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>" + Para(s.Title ?? "") + "</p:txBody></p:sp>";
                        var body = new StringBuilder();
                        foreach (var b in s.Bullets ?? new List<string>()) body.Append(Para(b));
                        if (body.Length == 0) body.Append("<a:p><a:endParaRPr lang=\"ko-KR\"/></a:p>");
                        shapes = title + "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"내용\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr><a:normAutofit/></a:bodyPr><a:lstStyle/>" + body + "</p:txBody></p:sp>";
                    }
                    XmlText.Put(z, "ppt/slides/slide" + n + ".xml", XmlText.Head + "<p:sld " + Ns + "><p:cSld><p:spTree>" + Group + shapes + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
                    XmlText.Put(z, "ppt/slides/_rels/slide" + n + ".xml.rels", rels + "</Relationships>");
                    ids.Append("<p:sldId id=\"").Append(255 + n).Append("\" r:id=\"rId").Append(n + 2).Append("\"/>");
                    presRels.Append("<Relationship Id=\"rId").Append(n + 2).Append("\" Type=\"").Append(RelType).Append("slide\" Target=\"slides/slide").Append(n).Append(".xml\"/>");
                    ct.Append("<Override PartName=\"/ppt/slides/slide").Append(n).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
                }
                presRels.Append("</Relationships>");
                ct.Append("</Types>");
                XmlText.Put(z, "[Content_Types].xml", ct.ToString());
                XmlText.Put(z, "_rels/.rels", XmlText.Head + RelNs + "<Relationship Id=\"rId1\" Type=\"" + RelType + "officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>");
                XmlText.Put(z, "ppt/presentation.xml", XmlText.Head + "<p:presentation " + Ns + " saveSubsetFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>" + ids + "</p:sldIdLst><p:sldSz cx=\"" + cx + "\" cy=\"" + cy + "\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
                XmlText.Put(z, "ppt/_rels/presentation.xml.rels", presRels.ToString());
                XmlText.Put(z, "ppt/slideMasters/slideMaster1.xml", master);
                XmlText.Put(z, "ppt/slideMasters/_rels/slideMaster1.xml.rels", XmlText.Head + RelNs + "<Relationship Id=\"rId1\" Type=\"" + RelType + "slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"" + RelType + "theme\" Target=\"../theme/theme1.xml\"/></Relationships>");
                XmlText.Put(z, "ppt/slideLayouts/slideLayout1.xml", layout);
                XmlText.Put(z, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", XmlText.Head + RelNs + "<Relationship Id=\"rId1\" Type=\"" + RelType + "slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
                XmlText.Put(z, "ppt/theme/theme1.xml", Theme1());
            }
        }

        static string Theme1()
        {
            const string F = "맑은 고딕";
            string solid = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
            var sb = new StringBuilder(XmlText.Head);
            sb.Append("<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Office\"><a:themeElements><a:clrScheme name=\"Office\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>");
            sb.Append("<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"").Append(F).Append("\"/><a:ea typeface=\"").Append(F).Append("\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"").Append(F).Append("\"/><a:ea typeface=\"").Append(F).Append("\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>");
            sb.Append("<a:fmtScheme name=\"Office\"><a:fillStyleLst>").Append(solid).Append(solid).Append(solid).Append("</a:fillStyleLst><a:lnStyleLst>");
            foreach (int w in new[] { 6350, 12700, 19050 }) sb.Append("<a:ln w=\"").Append(w).Append("\">").Append(solid).Append("</a:ln>");
            sb.Append("</a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst>").Append(solid).Append(solid).Append(solid).Append("</a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>");
            return sb.ToString();
        }
    }

    // ── 읽기 ──

    public static class OoxmlRead
    {
        static ZipArchive OpenZip(string path, out FileStream fs)
        {
            Util.EnsureReadable(path);
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                return new ZipArchive(fs, ZipArchiveMode.Read);
            }
            catch (InvalidDataException)
            {
                fs.Dispose();
                throw new UserFacingException("파일을 읽지 못했어요. 암호가 걸렸거나 손상된 파일일 수 있어요.");
            }
        }

        static XmlReader Reader(ZipArchiveEntry e)
        {
            return XmlReader.Create(e.Open(), XmlText.Safe());
        }

        static ZipArchiveEntry Need(ZipArchive z, string name)
        {
            var e = z.GetEntry(name);
            if (e == null) throw new UserFacingException("파일 안의 내용을 찾지 못했어요. 형식이 맞지 않는 파일일 수 있어요.");
            return e;
        }

        static void EndCell(StringBuilder sb)
        {
            while (sb.Length > 0 && (sb[sb.Length - 1] == '\n' || sb[sb.Length - 1] == '\r')) sb.Length--;
            sb.Append('\t');
        }

        static void EndRow(StringBuilder sb)
        {
            if (sb.Length > 0 && sb[sb.Length - 1] == '\t') sb.Length--;
            sb.Append('\n');
        }

        public static string DocxText(string path)
        {
            FileStream fs;
            using (var z = OpenZip(path, out fs))
            using (fs)
            using (var x = Reader(Need(z, "word/document.xml")))
            {
                var sb = new StringBuilder();
                bool inT = false, inRun = false;
                while (x.Read())
                {
                    switch (x.NodeType)
                    {
                        case XmlNodeType.Element:
                            if (x.LocalName == "r") inRun = !x.IsEmptyElement;
                            else if (x.LocalName == "t") inT = !x.IsEmptyElement;
                            else if (inRun && x.LocalName == "tab") sb.Append('\t');
                            else if (inRun && (x.LocalName == "br" || x.LocalName == "cr")) sb.Append('\n');
                            break;
                        case XmlNodeType.Text:
                        case XmlNodeType.SignificantWhitespace:
                        case XmlNodeType.Whitespace:
                        case XmlNodeType.CDATA:
                            if (inT) sb.Append(x.Value);
                            break;
                        case XmlNodeType.EndElement:
                            if (x.LocalName == "t") inT = false;
                            else if (x.LocalName == "r") inRun = false;
                            else if (x.LocalName == "p") sb.Append('\n');
                            else if (x.LocalName == "tc") EndCell(sb);
                            else if (x.LocalName == "tr") EndRow(sb);
                            break;
                    }
                }
                return sb.ToString();
            }
        }

        static Dictionary<string, string> Rels(ZipArchive z, string name)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var e = z.GetEntry(name);
            if (e == null) return map;
            using (var x = Reader(e))
            {
                while (x.Read())
                {
                    if (x.NodeType == XmlNodeType.Element && x.LocalName == "Relationship") map[x.GetAttribute("Id") ?? ""] = x.GetAttribute("Target") ?? "";
                }
            }
            return map;
        }

        const string RelsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        public static string PptxText(string path)
        {
            FileStream fs;
            using (var z = OpenZip(path, out fs))
            using (fs)
            {
                var rels = Rels(z, "ppt/_rels/presentation.xml.rels");
                var order = new List<string>();
                using (var x = Reader(Need(z, "ppt/presentation.xml")))
                {
                    while (x.Read())
                    {
                        if (x.NodeType != XmlNodeType.Element || x.LocalName != "sldId") continue;
                        string t;
                        if (rels.TryGetValue(x.GetAttribute("id", RelsNs) ?? "", out t)) order.Add("ppt/" + t.TrimStart('/').Replace("ppt/", ""));
                    }
                }
                var sb = new StringBuilder();
                for (int i = 0; i < order.Count; i++)
                {
                    var e = z.GetEntry(order[i]);
                    if (e == null) continue;
                    if (order.Count > 1) sb.Append(i > 0 ? "\n" : "").Append("--- ").Append(i + 1).Append("쪽 ---\n");
                    using (var x = Reader(e))
                    {
                        bool inT = false;
                        while (x.Read())
                        {
                            if (x.NodeType == XmlNodeType.Element && x.LocalName == "t") inT = !x.IsEmptyElement;
                            else if (x.NodeType == XmlNodeType.Element && x.LocalName == "br") sb.Append('\n');
                            else if ((x.NodeType == XmlNodeType.Text || x.NodeType == XmlNodeType.SignificantWhitespace) && inT) sb.Append(x.Value);
                            else if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "t") inT = false;
                            else if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "p") sb.Append('\n');
                        }
                    }
                }
                return sb.ToString();
            }
        }

        static bool IsDateFormat(string code)
        {
            string c = Regex.Replace(code ?? "", "\"[^\"]*\"|\\[[^\\]]*\\]", "").ToLowerInvariant();
            return c.IndexOfAny(new[] { 'y', 'd' }) >= 0 || c.Contains("m/") || c.Contains("/m") || c.Contains("h:") || c.Contains("yy");
        }

        public static List<XlsxSheet> XlsxRead(string path)
        {
            FileStream fs;
            using (var z = OpenZip(path, out fs))
            using (fs)
            {
                var strings = new List<string>();
                var sst = z.GetEntry("xl/sharedStrings.xml");
                if (sst != null)
                {
                    using (var x = Reader(sst))
                    {
                        StringBuilder cur = null;
                        bool inT = false, phonetic = false;
                        while (x.Read())
                        {
                            if (x.NodeType == XmlNodeType.Element)
                            {
                                if (x.LocalName == "si")
                                {
                                    if (x.IsEmptyElement) strings.Add("");
                                    else cur = new StringBuilder();
                                }
                                else if (x.LocalName == "rPh") phonetic = !x.IsEmptyElement;
                                else if (x.LocalName == "t") inT = !x.IsEmptyElement;
                            }
                            else if ((x.NodeType == XmlNodeType.Text || x.NodeType == XmlNodeType.SignificantWhitespace) && inT && !phonetic && cur != null) cur.Append(x.Value);
                            else if (x.NodeType == XmlNodeType.EndElement)
                            {
                                if (x.LocalName == "t") inT = false;
                                else if (x.LocalName == "rPh") phonetic = false;
                                else if (x.LocalName == "si" && cur != null)
                                {
                                    strings.Add(cur.ToString());
                                    cur = null;
                                }
                            }
                        }
                    }
                }
                var dateStyles = new HashSet<int>();
                var styles = z.GetEntry("xl/styles.xml");
                if (styles != null)
                {
                    var custom = new Dictionary<int, string>();
                    using (var x = Reader(styles))
                    {
                        bool inXfs = false;
                        int index = 0;
                        while (x.Read())
                        {
                            if (x.NodeType == XmlNodeType.Element)
                            {
                                if (x.LocalName == "numFmt")
                                {
                                    int id;
                                    if (int.TryParse(x.GetAttribute("numFmtId"), out id)) custom[id] = x.GetAttribute("formatCode");
                                }
                                else if (x.LocalName == "cellXfs") inXfs = !x.IsEmptyElement;
                                else if (x.LocalName == "xf" && inXfs)
                                {
                                    int id;
                                    int.TryParse(x.GetAttribute("numFmtId"), out id);
                                    string code;
                                    if ((id >= 14 && id <= 22) || (id >= 27 && id <= 36) || (id >= 45 && id <= 47) || (id >= 50 && id <= 58) || (custom.TryGetValue(id, out code) && IsDateFormat(code))) dateStyles.Add(index);
                                    index++;
                                }
                            }
                            else if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "cellXfs") inXfs = false;
                        }
                    }
                }
                var rels = Rels(z, "xl/_rels/workbook.xml.rels");
                var list = new List<XlsxSheet>();
                var names = new List<KeyValuePair<string, string>>();
                using (var x = Reader(Need(z, "xl/workbook.xml")))
                {
                    while (x.Read())
                    {
                        if (x.NodeType != XmlNodeType.Element || x.LocalName != "sheet") continue;
                        string t;
                        if (!rels.TryGetValue(x.GetAttribute("id", RelsNs) ?? "", out t)) continue;
                        names.Add(new KeyValuePair<string, string>(x.GetAttribute("name") ?? "", t.StartsWith("/") ? t.TrimStart('/') : "xl/" + t));
                    }
                }
                foreach (var kv in names)
                {
                    var e = z.GetEntry(kv.Value);
                    if (e == null) continue;
                    var sheet = new XlsxSheet { Name = kv.Key };
                    using (var x = Reader(e))
                    {
                        int rowIndex = -1, col = 0, style = 0;
                        string type = null;
                        var value = new StringBuilder();
                        bool inValue = false;
                        while (x.Read())
                        {
                            if (x.NodeType == XmlNodeType.Element)
                            {
                                switch (x.LocalName)
                                {
                                    case "row":
                                        {
                                            int r;
                                            rowIndex = int.TryParse(x.GetAttribute("r"), out r) ? r - 1 : rowIndex + 1;
                                            while (sheet.Rows.Count <= rowIndex) sheet.Rows.Add(new List<string>());
                                            col = 0;
                                            break;
                                        }
                                    case "c":
                                        {
                                            string rf = x.GetAttribute("r");
                                            if (rf != null)
                                            {
                                                col = 0;
                                                foreach (char ch in rf)
                                                {
                                                    if (ch < 'A' || ch > 'Z') break;
                                                    col = col * 26 + (ch - 'A' + 1);
                                                }
                                                col--;
                                            }
                                            type = x.GetAttribute("t");
                                            int.TryParse(x.GetAttribute("s"), out style);
                                            value.Clear();
                                            if (x.IsEmptyElement) col++;
                                            break;
                                        }
                                    case "v":
                                    case "t":
                                        inValue = !x.IsEmptyElement;
                                        break;
                                }
                            }
                            else if ((x.NodeType == XmlNodeType.Text || x.NodeType == XmlNodeType.SignificantWhitespace) && inValue) value.Append(x.Value);
                            else if (x.NodeType == XmlNodeType.EndElement)
                            {
                                if (x.LocalName == "v" || x.LocalName == "t") inValue = false;
                                else if (x.LocalName == "c" && rowIndex >= 0)
                                {
                                    var row = sheet.Rows[rowIndex];
                                    while (row.Count <= col) row.Add("");
                                    row[col] = CellValue(value.ToString(), type, style, strings, dateStyles);
                                    col++;
                                }
                            }
                        }
                    }
                    list.Add(sheet);
                }
                return list;
            }
        }

        static string CellValue(string v, string type, int style, List<string> strings, HashSet<int> dates)
        {
            if (type == "s")
            {
                int i;
                return int.TryParse(v, out i) && i >= 0 && i < strings.Count ? strings[i] : "";
            }
            if (type == "b") return v == "1" ? "TRUE" : "FALSE";
            if (type == null || type == "n")
            {
                double d;
                if (dates.Contains(style) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0 && d < 2958465)
                {
                    DateTime t = DateTime.FromOADate(d);
                    return t.TimeOfDay == TimeSpan.Zero ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
            }
            return v;
        }

        public static string RowsText(IList<XlsxSheet> sheets)
        {
            var sb = new StringBuilder();
            foreach (var s in sheets)
            {
                if (sheets.Count > 1) sb.Append("[").Append(s.Name).Append("]\n");
                foreach (var r in s.Rows) sb.Append(string.Join("\t", r)).Append('\n');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // ── HWPX ──

        static List<XmlDocument> HwpxSections(string path)
        {
            FileStream fs;
            using (var z = OpenZip(path, out fs))
            using (fs)
            {
                var entries = z.Entries.Where(e => Regex.IsMatch(e.FullName, @"^Contents/section\d+\.xml$", RegexOptions.IgnoreCase))
                    .OrderBy(e => int.Parse(Regex.Match(e.Name, @"\d+").Value)).ToList();
                if (entries.Count == 0) throw new UserFacingException("문서 내용을 찾지 못했어요. 암호가 걸렸거나 손상된 파일일 수 있어요.");
                var docs = new List<XmlDocument>();
                foreach (var e in entries)
                {
                    var d = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
                    try
                    {
                        using (var x = Reader(e)) d.Load(x);
                    }
                    catch (XmlException)
                    {
                        throw new UserFacingException("문서 내용을 읽지 못했어요. 암호가 걸렸거나 손상된 파일일 수 있어요.");
                    }
                    docs.Add(d);
                }
                return docs;
            }
        }

        static void HwpxWalk(XmlNode n, StringBuilder sb)
        {
            foreach (XmlNode c in n.ChildNodes)
            {
                if (c.NodeType != XmlNodeType.Element) continue;
                switch (c.LocalName)
                {
                    case "t":
                        foreach (XmlNode t in c.ChildNodes)
                        {
                            if (t.NodeType == XmlNodeType.Text || t.NodeType == XmlNodeType.SignificantWhitespace || t.NodeType == XmlNodeType.Whitespace) sb.Append(t.Value);
                            else if (t.LocalName == "lineBreak") sb.Append('\n');
                            else if (t.LocalName == "tab") sb.Append('\t');
                            else if (t.LocalName == "nbSpace" || t.LocalName == "fwSpace") sb.Append(' ');
                            else if (t.LocalName == "hyphen") sb.Append('-');
                        }
                        break;
                    case "p":
                        HwpxWalk(c, sb);
                        sb.Append('\n');
                        break;
                    case "tc":
                        HwpxWalk(c, sb);
                        EndCell(sb);
                        break;
                    case "tr":
                        HwpxWalk(c, sb);
                        EndRow(sb);
                        break;
                    case "memo":
                    case "hiddenComment":
                        break;
                    default:
                        HwpxWalk(c, sb);
                        break;
                }
            }
        }

        public static string HwpxText(string path)
        {
            var sb = new StringBuilder();
            foreach (var d in HwpxSections(path)) HwpxWalk(d.DocumentElement, sb);
            return sb.ToString();
        }

        static void FindTables(XmlNode n, List<XmlNode> found)
        {
            foreach (XmlNode c in n.ChildNodes)
            {
                if (c.NodeType != XmlNodeType.Element) continue;
                if (c.LocalName == "tbl") found.Add(c);
                else FindTables(c, found);
            }
        }

        static XmlNode Child(XmlNode n, string name)
        {
            foreach (XmlNode c in n.ChildNodes)
            {
                if (c.NodeType == XmlNodeType.Element && c.LocalName == name) return c;
            }
            return null;
        }

        static int Attr(XmlNode n, string name, int fallback)
        {
            if (n == null || n.Attributes == null) return fallback;
            var a = n.Attributes[name];
            int v;
            return a != null && int.TryParse(a.Value, out v) ? v : fallback;
        }

        public static List<XlsxSheet> HwpxTables(string path)
        {
            var sheets = new List<XlsxSheet>();
            foreach (var d in HwpxSections(path))
            {
                var tables = new List<XmlNode>();
                FindTables(d.DocumentElement, tables);
                foreach (var t in tables)
                {
                    var sheet = new XlsxSheet { Grid = true };
                    int rowNo = 0, maxCol = 0;
                    var cells = new List<KeyValuePair<int[], string>>();
                    foreach (XmlNode tr in t.ChildNodes)
                    {
                        if (tr.NodeType != XmlNodeType.Element || tr.LocalName != "tr") continue;
                        int colNo = 0;
                        foreach (XmlNode tc in tr.ChildNodes)
                        {
                            if (tc.NodeType != XmlNodeType.Element || tc.LocalName != "tc") continue;
                            var addr = Child(tc, "cellAddr");
                            var span = Child(tc, "cellSpan");
                            int r = Attr(addr, "rowAddr", rowNo), c = Attr(addr, "colAddr", colNo), rs = Math.Max(1, Attr(span, "rowSpan", 1)), cs = Math.Max(1, Attr(span, "colSpan", 1));
                            var sb = new StringBuilder();
                            HwpxWalk(tc, sb);
                            cells.Add(new KeyValuePair<int[], string>(new[] { r, c, rs, cs }, sb.ToString().Trim('\n', '\r', ' ')));
                            colNo = c + cs;
                            maxCol = Math.Max(maxCol, colNo);
                        }
                        rowNo++;
                    }
                    if (cells.Count == 0 || cells.All(x => x.Value.Length == 0)) continue;
                    int rows = cells.Max(x => x.Key[0] + x.Key[2]);
                    for (int r = 0; r < rows; r++)
                    {
                        var row = new List<string>();
                        for (int c = 0; c < maxCol; c++) row.Add("");
                        sheet.Rows.Add(row);
                    }
                    foreach (var x in cells)
                    {
                        var k = x.Key;
                        sheet.Rows[k[0]][k[1]] = x.Value;
                        if (k[2] > 1 || k[3] > 1) sheet.Merges.Add(new[] { k[0], k[1], k[0] + k[2] - 1, k[1] + k[3] - 1 });
                    }
                    sheet.Name = "표" + (sheets.Count + 1);
                    sheets.Add(sheet);
                }
            }
            return sheets;
        }
    }
}
