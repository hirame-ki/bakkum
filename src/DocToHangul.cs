// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki, moonsune.zip - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 워드·PDF → 한글(HWP·HWPX). 한글 프로그램이 읽고 저장한다.
// - 워드 계열은 빈 문서에 끼워 넣는다(Hangul.InsertDoc). 열기(Open)로 주면 한컴오피스 2022는 한워드로 넘기고 실패한다.
// - 워드에서 들여온 표는 칸 안 위·아래 여백이 0(워드 기본값)이라 한글에서는 글자가 칸 선에 붙는다. HWPX로 한 번 저장해 고친다.
// - PDF는 한글이 글·표를 되살려 연다(Hangul.OpenPdf). 스캔한 PDF는 글자 인식으로 글자를 옮긴다.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bakkum
{
    public static partial class DocConvert
    {
        /// 워드에서 들여온 표의 칸 안 위·아래 여백(0)을 바꿀 값. 한글이 새 표에 쓰는 기본값(0.5mm)과 같다.
        internal const int TableMarginTopBottom = 141;

        static readonly Regex ZeroTopBottom = new Regex("\\b(top|bottom)=\"0\"");

        /// f(한글·워드·PDF)를 한글 문서 dst로 만든다. format은 "HWP" 또는 "HWPX".
        public static void ToHangul(Job job, string f, string dst, string format)
        {
            switch (Formats.KindOf(f))
            {
                case Kind.Word:
                    WordToHangul(job, f, dst, format);
                    break;
                case Kind.Pdf:
                    PdfToHangul(job, f, dst, format);
                    break;
                default:
                    job.Use(() => new Hangul(job)).Convert(f, dst, format);
                    break;
            }
        }

        static void WordToHangul(Job job, string f, string dst, string format)
        {
            string ext = Formats.Ext(f);
            // 암호 걸린 워드 파일(.docx)은 zip이 아니다. 한글에 넘기기 전에 알린다.
            List<string> parts = ext == ".docx" || ext == ".odt" ? ZipParts(f) : null;
            var h = job.Use(() => new Hangul(job));
            string dir = Util.TempDir("hwp");
            try
            {
                bool read = false, empty = false;
                if (ext != ".odt" || OdtHangulReads(parts)) read = h.InsertDoc(f, ext == ".docx" ? "OOXML" : StartsWithRtf(f) ? "RTF" : "");
                if (!read && ext == ".odt")
                {
                    // 한글이 못 읽는 오픈 문서는 바꿈이 읽어 워드 파일로 만든 뒤 넣는다(글·표·사진만 옮겨진다).
                    var blocks = OdtFile.Read(f);
                    empty = blocks.Count == 0;
                    if (!empty)
                    {
                        string docx = Path.Combine(dir, "odt.docx");
                        DocxWriter.Write(docx, blocks);
                        read = h.InsertDoc(docx, "OOXML");
                    }
                }
                if (!read && ext == ".docx") empty = !parts.Any(p => p.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)) && OoxmlRead.DocxText(f).Trim().Length == 0;
                if (!read) throw new UserFacingException(empty ? "빈 파일이에요." : "한글에서 이 파일을 읽지 못했어요. 암호가 걸렸거나 손상된 파일일 수 있어요.");
                SaveFixingTables(h, dir, dst, format);
            }
            finally
            {
                h.CloseDoc();
                Util.TryDeleteDir(dir);
            }
        }

        /// PDF → HWP·HWPX. 한글이 PDF를 읽어 글·표를 되살린다(PDF → 워드와 같은 길).
        /// 스캔한 PDF는 한글이 쪽 그림만 넣는다(글자 0자, 2026-11-15 실측). 그때는 글자 인식으로 글자를 옮기고,
        /// 글자를 못 찾으면 쪽 그림을 넣은 문서를 그대로 둔다.
        static void PdfToHangul(Job job, string f, string dst, string format)
        {
            var h = job.Use(() => new Hangul(job));
            bool hasText;
            try
            {
                h.OpenPdf(f);
                try
                {
                    hasText = h.Text().Trim().Length > 0;
                    h.SaveAs(dst, format);
                }
                finally { h.CloseDoc(); }
            }
            catch (UserFacingException ex)
            {
                // 한글이 PDF를 열지 못하면(PDF 변환이 없는 판, 망가진 PDF 등) 바꿈이 읽은 글자만이라도 옮긴다.
                Log.Write("PDF를 한글로 열기 실패: " + ex.Message);
                Conversions.TryDelete(dst);
                string text = LocalPlain(job, f);
                if (text.Trim().Length == 0) throw;
                PlainToHangul(job, text, dst, format);
                job.Note(Util.ShortName(f) + ": 한글에서 이 PDF를 열지 못해 글자만 옮겼어요. 표와 배치는 빠졌어요.");
                return;
            }
            if (hasText) return;
            string scanned;
            try
            {
                scanned = LocalPlain(job, f);
            }
            catch (UserFacingException ex)
            {
                job.Note(Util.ShortName(f) + ": 스캔한 PDF라 쪽 그림만 옮겼어요. " + ex.Message);
                return;
            }
            if (scanned.Trim().Length == 0)
            {
                job.Note(Util.ShortName(f) + ": 글자를 찾지 못해 쪽 그림만 옮겼어요.");
                return;
            }
            PlainToHangul(job, scanned, dst, format);
            job.Note(Util.ShortName(f) + ": 스캔한 PDF라 글자 인식으로 글자만 옮겼어요. 틀린 글자가 있을 수 있어요.");
        }

        /// 글자만 있는 내용을 워드 파일로 만든 뒤 한글에 넣어 dst로 저장한다(dst가 있으면 바꾼다).
        static void PlainToHangul(Job job, string text, string dst, string format)
        {
            string dir = Util.TempDir("hwptext");
            try
            {
                string docx = Path.Combine(dir, "text.docx");
                DocxWriter.Write(docx, MarkdownDoc.FromPlain(text));
                string made = Path.Combine(dir, "text." + format.ToLowerInvariant());
                WordToHangul(job, docx, made, format);
                File.Copy(made, dst, true);
            }
            finally { Util.TryDeleteDir(dir); }
        }

        /// 한글에 열린 문서를 dst로 저장한다. 표 여백을 고치려고 HWPX로 한 번 저장해 고치고,
        /// 고친 곳이 있으면 HWPX는 고친 파일을 그대로 쓰고 HWP는 고친 파일을 한글로 다시 열어 저장한다.
        static void SaveFixingTables(Hangul h, string dir, string dst, string format)
        {
            string raw = Path.Combine(dir, "raw.hwpx"), fixedHwpx = Path.Combine(dir, "fixed.hwpx");
            h.SaveAs(raw, "HWPX");
            if (FixTableMargins(raw, fixedHwpx) == 0)
            {
                if (format == "HWPX") File.Copy(raw, dst, true);
                else h.SaveAs(dst, format);
                return;
            }
            h.CloseDoc();
            if (format == "HWPX") File.Copy(fixedHwpx, dst, true);
            else h.Convert(fixedHwpx, dst, format);
        }

        /// HWPX 안 표의 칸 여백 가운데 위·아래가 0인 것을 TableMarginTopBottom으로 바꿔 dst에 쓴다. 바꾼 곳 수를 돌려준다.
        internal static int FixTableMargins(string src, string dst)
        {
            int changed = 0;
            using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
            using (var os = new FileStream(dst, FileMode.Create, FileAccess.Write))
            using (var o = new ZipArchive(os, ZipArchiveMode.Create))
            {
                foreach (var e in z.Entries.OrderBy(x => x.FullName == "mimetype" ? 0 : 1))
                {
                    string name = e.FullName;
                    byte[] data;
                    using (var s = e.Open())
                    using (var m = new MemoryStream())
                    {
                        s.CopyTo(m);
                        data = m.ToArray();
                    }
                    if (name.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        int n = 0;
                        string xml = FixMarginsXml(new UTF8Encoding(false).GetString(data), ref n);
                        if (n > 0)
                        {
                            data = new UTF8Encoding(false).GetBytes(xml);
                            changed += n;
                        }
                    }
                    // HWPX의 맨 앞 mimetype은 압축하지 않아야 한다.
                    var entry = o.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using (var s = entry.Open()) s.Write(data, 0, data.Length);
                }
            }
            return changed;
        }

        /// 표(tbl)의 inMargin과 칸(tc)의 cellMargin에서 top·bottom="0"을 고친다. 그림(pic)에도 inMargin이 있어서
        /// 이름만 보고 고치면 그림 둘레가 벌어진다. 그래서 바로 위 요소를 따라가며 고르고, 나머지 글은 한 글자도 바꾸지 않는다.
        internal static string FixMarginsXml(string xml, ref int changed)
        {
            var sb = new StringBuilder(xml.Length + 64);
            var open = new List<string>();
            int i = 0;
            while (i < xml.Length)
            {
                int lt = xml.IndexOf('<', i);
                if (lt < 0) break;
                int end = TagEnd(xml, lt);
                if (end < 0) break;
                sb.Append(xml, i, lt - i);
                string tag = xml.Substring(lt, end - lt + 1);
                i = end + 1;
                if (tag.StartsWith("<?", StringComparison.Ordinal) || tag.StartsWith("<!", StringComparison.Ordinal))
                {
                    sb.Append(tag);
                    continue;
                }
                if (tag.StartsWith("</", StringComparison.Ordinal))
                {
                    if (open.Count > 0) open.RemoveAt(open.Count - 1);
                    sb.Append(tag);
                    continue;
                }
                string name = LocalName(tag);
                string parent = open.Count > 0 ? open[open.Count - 1] : "";
                if ((name == "inMargin" && parent == "tbl") || (name == "cellMargin" && parent == "tc"))
                {
                    string before = tag;
                    tag = ZeroTopBottom.Replace(tag, m => m.Groups[1].Value + "=\"" + TableMarginTopBottom + "\"");
                    if (tag != before) changed++;
                }
                sb.Append(tag);
                if (!tag.EndsWith("/>", StringComparison.Ordinal)) open.Add(name);
            }
            sb.Append(xml, i, xml.Length - i);
            return sb.ToString();
        }

        /// lt에서 시작하는 태그의 끝('>') 자리. 따옴표 안의 '>'와 주석·CDATA는 건너뛴다.
        static int TagEnd(string xml, int lt)
        {
            if (string.CompareOrdinal(xml, lt, "<!--", 0, 4) == 0)
            {
                int c = xml.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                return c < 0 ? -1 : c + 2;
            }
            if (string.CompareOrdinal(xml, lt, "<![CDATA[", 0, 9) == 0)
            {
                int c = xml.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                return c < 0 ? -1 : c + 2;
            }
            char quote = '\0';
            for (int k = lt + 1; k < xml.Length; k++)
            {
                char ch = xml[k];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                }
                else if (ch == '"' || ch == '\'') quote = ch;
                else if (ch == '>') return k;
            }
            return -1;
        }

        static string LocalName(string tag)
        {
            int k = 1;
            while (k < tag.Length && !char.IsWhiteSpace(tag[k]) && tag[k] != '/' && tag[k] != '>') k++;
            string q = tag.Substring(1, k - 1);
            int colon = q.IndexOf(':');
            return colon < 0 ? q : q.Substring(colon + 1);
        }

        static List<string> ZipParts(string f)
        {
            Util.EnsureReadable(f);
            try
            {
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
                    return z.Entries.Select(e => e.FullName).ToList();
            }
            catch (InvalidDataException)
            {
                throw new UserFacingException("파일을 읽지 못했어요. 암호가 걸렸거나 손상된 파일일 수 있어요.");
            }
        }

        /// 한글이 바로 읽는 오픈 문서인지. 한글은 meta.xml·settings.xml이 없으면 글자 파일로 알고 '텍스트 문서 종류' 창을 띄우고,
        /// styles.xml이 없으면 꺼진다(2026-11-15 한컴오피스 2022로 시험). 보통 프로그램이 만든 .odt에는 다 들어 있다.
        static bool OdtHangulReads(List<string> parts)
        {
            return new[] { "content.xml", "styles.xml", "meta.xml", "settings.xml" }.All(p => parts.Contains(p));
        }

        /// 이름만 .doc인 RTF(학교 시스템에서 받은 파일 등)는 RTF로 읽게 한다.
        static bool StartsWithRtf(string f)
        {
            var head = new byte[5];
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int n = fs.Read(head, 0, head.Length);
                return n == head.Length && Encoding.ASCII.GetString(head) == "{\\rtf";
            }
        }
    }
}
