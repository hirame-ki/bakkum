// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 문서 형식 바꾸기(워드·엑셀·PPT·자막·웹 문서)와 문서 속 사진 다루기.
// 한글·워드가 있어야 하는 길과 프로그램 없이 되는 길을 나눠, 없는 프로그램 때문에 멈추지 않게 한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    public static class TextFile
    {
        /// 글자 파일을 인코딩을 가려 읽는다(BOM → UTF-8 → 한국어 윈도우 인코딩).
        public static string Read(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            try
            {
                return new UTF8Encoding(false, true).GetString(b);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(949).GetString(b);
            }
        }

        public static List<List<string>> ParseDelimited(string text, char sep)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else quoted = false;
                    }
                    else cell.Append(c);
                    continue;
                }
                if (c == '"' && cell.Length == 0) quoted = true;
                else if (c == sep)
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = new List<string>();
                }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                rows.Add(row);
            }
            return rows;
        }

        public static string ToCsv(IEnumerable<IList<string>> rows)
        {
            return ToDelimited(rows, ',');
        }

        public static string ToDelimited(IEnumerable<IList<string>> rows, char sep)
        {
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                sb.Append(string.Join(sep.ToString(), r.Select(c => c.IndexOfAny(new[] { sep, '"', '\n', '\r' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c)));
                sb.Append("\r\n");
            }
            return sb.ToString();
        }
    }

    /// 문서에서 글자를 꺼낸다(글자 뽑기와 AI가 함께 쓴다).
    public static class DocRead
    {
        /// PDF·그림이 아닌 문서인가
        public static bool IsDocument(string f)
        {
            Kind k = Formats.KindOf(f);
            return k != Kind.Pdf && k != Kind.Image;
        }

        public static string Text(Job job, string f)
        {
            switch (Formats.Ext(f))
            {
                case ".hwpx":
                    return OoxmlRead.HwpxText(f);
                case ".hwp":
                    {
                        var h = job.Use(() => new Hangul(job));
                        h.Open(f);
                        try { return h.Text() ?? ""; }
                        finally { h.CloseDoc(); }
                    }
                case ".docx":
                    return OoxmlRead.DocxText(f);
                case ".pptx":
                    return OoxmlRead.PptxText(f);
                case ".xlsx":
                case ".xlsm":
                    return OoxmlRead.RowsText(OoxmlRead.XlsxRead(f));
                case ".txt":
                case ".md":
                case ".markdown":
                case ".srt":
                case ".vtt":
                case ".csv":
                case ".tsv":
                    return TextFile.Read(f);
                case ".html":
                case ".htm":
                    return MarkdownDoc.ToMarkdown(MarkdownDoc.FromHtml(TextFile.Read(f)));
                case ".epub":
                    return MarkdownDoc.ToPlain(Epub.Read(f));
                case ".odt":
                    return MarkdownDoc.ToPlain(OdtFile.Read(f));
                case ".ods":
                    return OoxmlRead.RowsText(OdsFile.Read(f));
            }
            // 옛 형식(.doc·.xls·.ppt·.rtf·.show·.cell)은 PDF로 바꿔서 읽는다.
            return Conversions.WithPdf(job, f, delegate (string pdf)
            {
                var t = PdfText.Read(pdf, job);
                if (t == null || !t.Usable) throw new UserFacingException("이 파일에서 글자를 읽지 못했어요.");
                return t.Plain();
            });
        }
    }

    public static partial class DocConvert
    {
        public static bool Handles(string target)
        {
            switch (target)
            {
                case "docx":
                case "xlsx":
                case "pptx":
                case "pptximg":
                case "vtt":
                case "srt":
                case "md":
                case "html":
                case "epub":
                case "txt":
                case "csv":
                case "tsv":
                case "ods":
                    return true;
            }
            return false;
        }

        public static void Run(Job job, List<string> files, string target)
        {
            switch (target)
            {
                case "docx": ToDocx(job, files); break;
                case "xlsx": ToXlsx(job, files); break;
                case "pptx": ToPptx(job, files, false); break;
                case "pptximg": ToPptx(job, files, true); break;
                case "vtt":
                case "srt":
                    Subtitles(job, files, target);
                    break;
                case "md":
                case "html":
                case "epub":
                case "txt":
                    WebText(job, files, target);
                    break;
                case "csv":
                case "tsv":
                case "ods":
                    ToTable(job, files, target);
                    break;
            }
        }

        static List<string> Only(Job job, List<string> files, Func<string, bool> ok)
        {
            var list = files.Where(ok).ToList();
            if (list.Count < files.Count) job.Note("이 형식으로 바꿀 수 없는 파일 " + (files.Count - list.Count) + "개는 건너뛰었어요.");
            return list;
        }

        public static bool IsCue(string f)
        {
            string e = Formats.Ext(f);
            return e == ".srt" || e == ".vtt";
        }

        public static bool IsTable(string f)
        {
            switch (Formats.Ext(f))
            {
                case ".csv":
                case ".tsv":
                case ".xlsx":
                case ".xlsm":
                case ".ods":
                    return true;
            }
            return false;
        }

        static List<XlsxSheet> SheetsOf(string f)
        {
            string ext = Formats.Ext(f);
            if (ext == ".ods") return OdsFile.Read(f);
            if (ext == ".csv" || ext == ".tsv") return new List<XlsxSheet> { new XlsxSheet { Name = Path.GetFileNameWithoutExtension(f), Rows = TextFile.ParseDelimited(TextFile.Read(f), ext == ".tsv" ? '\t' : ',') } };
            return OoxmlRead.XlsxRead(f);
        }

        static List<DocBlock> TableBlocks(string f)
        {
            var sheets = SheetsOf(f).Where(s => s.Rows.Count > 0).ToList();
            var blocks = new List<DocBlock>();
            foreach (var s in sheets)
            {
                if (sheets.Count > 1) blocks.Add(DocBlock.Head(s.Name, 2));
                int cols = s.Rows.Max(r => r.Count);
                blocks.Add(new DocBlock { Kind = BlockKind.Table, Rows = s.Rows.Select(r => r.Select(c => c ?? "").Concat(Enumerable.Repeat("", cols - r.Count)).ToList()).ToList() });
            }
            return blocks;
        }

        static List<DocBlock> HwpBlocks(Job job, string f)
        {
            if (Hangul.Installed)
            {
                string dir = Util.TempDir("hwp2html");
                try
                {
                    string t = Path.Combine(dir, "doc.html");
                    job.Use(() => new Hangul(job)).Convert(f, t, "HTML");
                    // 한글이 임시 폴더에 함께 만든 그림만 읽는다.
                    var blocks = MarkdownDoc.FromHtml(TextFile.Read(t), delegate (string src)
                    {
                        string p = System.Net.WebUtility.UrlDecode(src).Replace('/', '\\');
                        if (p.Contains(":") || p.StartsWith("\\") || p.Contains("..")) return null;
                        string full = Path.Combine(dir, p);
                        return File.Exists(full) ? MarkdownDoc.Picture(File.ReadAllBytes(full)) : null;
                    });
                    if (blocks.Count > 0) return blocks;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (UserFacingException ex)
                {
                    Log.Write("한글을 거쳐 HTML로 읽기 실패: " + ex.GetType().Name);
                    if (Formats.Ext(f) != ".hwpx") throw;
                }
                finally
                {
                    Util.TryDeleteDir(dir);
                }
            }
            if (Formats.Ext(f) == ".hwpx") return MarkdownDoc.FromPlain(OoxmlRead.HwpxText(f));
            throw new UserFacingException("HWP 파일을 바꾸려면 이 PC에 한글 프로그램이 있어야 해요.");
        }

        /// 어떤 문서든 글·표·사진 덩어리로 읽는다.
        public static List<DocBlock> BlocksOf(Job job, string f)
        {
            switch (Formats.Ext(f))
            {
                case ".epub": return Epub.Read(f);
                case ".docx": return DocxBlocks.Read(f);
                case ".odt": return OdtFile.Read(f);
            }
            if (IsTable(f)) return TableBlocks(f);
            switch (Formats.KindOf(f))
            {
                case Kind.Text: return BlocksOfText(f);
                case Kind.Pdf: return LocalBlocks(job, f);
                case Kind.Hwp: return HwpBlocks(job, f);
            }
            return Conversions.WithPdf(job, f, delegate (string pdf) { return LocalBlocks(job, pdf); });
        }

        internal static string GroupName(List<string> files, string unit, string ext)
        {
            string stem = Path.GetFileNameWithoutExtension(files[0]);
            string name = files.Count > 1 ? stem + " 외 " + (files.Count - 1) + unit + ext : stem + ext;
            return Util.UniquePath(Path.Combine(Util.OutputDirFor(files[0]), name));
        }

        static List<DocBlock> BlocksOfText(string f)
        {
            string text = TextFile.Read(f);
            switch (Formats.Ext(f))
            {
                case ".md":
                case ".markdown":
                    return MarkdownDoc.Parse(text);
                case ".html":
                case ".htm":
                    return MarkdownDoc.FromHtml(text);
            }
            return MarkdownDoc.FromPlain(text);
        }

        // ── 워드로 ──

        static void PictureFor(string f, out byte[] data, out string type, out int w, out int h)
        {
            var fit = Imaging.Fit(Imaging.Load(f), 2400);
            w = fit.PixelWidth;
            h = fit.PixelHeight;
            string fmt = Formats.ImageFormatOf(f);
            bool lossless = fmt == "png" || fmt == "bmp" || fmt == "gif" || Imaging.MayHaveAlpha(fit);
            if (lossless)
            {
                data = Imaging.Encode(fit, "png", 0);
                type = "png";
                if (!Imaging.MayHaveAlpha(fit) && data.Length > 2 * 1024 * 1024)
                {
                    data = Imaging.Jpeg(fit, 88);
                    type = "jpeg";
                }
                return;
            }
            data = Imaging.Jpeg(fit, 88);
            type = "jpeg";
        }

        static void ToDocx(Job job, List<string> files)
        {
            var list = Only(job, files, f => Formats.KindOf(f) == Kind.Image || Formats.KindOf(f) == Kind.Hwp || Formats.KindOf(f) == Kind.Pdf || Formats.KindOf(f) == Kind.Text || Formats.Ext(f) == ".odt");
            var docs = list.Where(f => Formats.KindOf(f) != Kind.Image).OrderBy(f => (int)Formats.KindOf(f)).ToList();
            var images = list.Where(f => Formats.KindOf(f) == Kind.Image).ToList();
            Conversions.EachFile(job, docs, "워드로 바꾸는 중", delegate (string f)
            {
                Conversions.Produce(job, Util.OutputFile(f, ".docx"), delegate (string d) { OneToDocx(job, f, d); });
            });
            if (images.Count == 0) return;
            Conversions.EachFile(job, images.Take(1).ToList(), "워드로 묶는 중", delegate (string first)
            {
                var blocks = new List<DocBlock>();
                foreach (var f in images)
                {
                    job.Check();
                    byte[] data;
                    string type;
                    int w, h;
                    PictureFor(f, out data, out type, out w, out h);
                    blocks.Add(new DocBlock { Kind = BlockKind.Picture, Image = data, ImageType = type, W = w, H = h });
                }
                Conversions.Produce(job, GroupName(images, "장", ".docx"), delegate (string d) { DocxWriter.Write(d, blocks); });
            });
        }

        static void OneToDocx(Job job, string f, string d)
        {
            switch (Formats.KindOf(f))
            {
                case Kind.Hwp:
                    job.Use(() => new Hangul(job)).Convert(f, d, "OOXML");
                    break;
                case Kind.Pdf:
                    PdfToDocx(job, f, d);
                    break;
                default:
                    DocxWriter.Write(d, BlocksOf(job, f));
                    break;
            }
        }

        /// PDF에서 글자 덩어리를 만든다. 글자가 든 PDF는 그대로 읽고, 스캔본은 글자 인식을 쓴다.
        static List<DocBlock> LocalBlocks(Job job, string f)
        {
            var t = PdfText.Read(f, job);
            string text = t != null && t.Usable ? t.Plain() : Conversions.OcrPdf(job, f);
            return MarkdownDoc.FromPlain(text);
        }

        static void PdfToDocx(Job job, string f, string d)
        {
            if (MsWord.Installed)
            {
                job.Use(() => new MsWord(job)).PdfToDocx(f, d);
                return;
            }
            if (Hangul.Installed)
            {
                try
                {
                    var h = job.Use(() => new Hangul(job));
                    h.OpenPdf(f);
                    try { h.SaveAs(d, "OOXML"); }
                    finally { h.CloseDoc(); }
                    if (OoxmlRead.DocxText(d).Trim().Length > 0) return;
                    Log.Write("한글을 거친 워드 파일이 비어 있음");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (UserFacingException ex)
                {
                    Log.Write("PDF를 한글로 열기 실패: " + ex.GetType().Name);
                }
                Conversions.TryDelete(d);
            }
            DocxWriter.Write(d, LocalBlocks(job, f));
            job.Note(Util.ShortName(f) + ": 글자만 옮겼어요. 표와 배치까지 옮기려면 이 PC에 한글이나 MS 워드가 있어야 해요.");
        }

        // ── 엑셀로 ──

        static void ToXlsx(Job job, List<string> files)
        {
            var list = Only(job, files, f => Formats.KindOf(f) == Kind.Hwp || Formats.KindOf(f) == Kind.Pdf || Formats.Ext(f) == ".csv" || Formats.Ext(f) == ".tsv" || Formats.Ext(f) == ".ods");
            Conversions.EachFile(job, list, "엑셀로 바꾸는 중", delegate (string f)
            {
                List<XlsxSheet> sheets;
                string ext = Formats.Ext(f);
                if (IsTable(f))
                {
                    sheets = SheetsOf(f);
                    if (sheets.All(s => s.Rows.Count == 0)) throw new UserFacingException("빈 파일이에요.");
                }
                else if (Formats.KindOf(f) == Kind.Hwp) sheets = HwpSheets(job, f);
                else sheets = PdfSheets(job, f);
                if (sheets.Count == 0) throw new UserFacingException("문서 안에서 표를 찾지 못했어요. 사진처럼 스캔한 문서라면 'AI로 → 표를 엑셀로'를 써 보세요.");
                Conversions.Produce(job, Util.OutputFile(f, ".xlsx"), delegate (string d) { XlsxWriter.Write(d, sheets); });
                if (sheets.Count > 1) job.Note(Util.ShortName(f) + ": 표 " + sheets.Count + "개를 시트 " + sheets.Count + "개에 나눠 담았어요.");
            });
        }

        static List<XlsxSheet> HwpSheets(Job job, string f)
        {
            if (Formats.Ext(f) == ".hwpx") return OoxmlRead.HwpxTables(f);
            string dir = Util.TempDir("hwp2x");
            try
            {
                string t = Path.Combine(dir, "doc.hwpx");
                job.Use(() => new Hangul(job)).Convert(f, t, "HWPX");
                return OoxmlRead.HwpxTables(t);
            }
            finally
            {
                Util.TryDeleteDir(dir);
            }
        }

        static List<XlsxSheet> PdfSheets(Job job, string f)
        {
            List<XlsxSheet> sheets = null;
            if (Hangul.Installed)
            {
                string dir = Util.TempDir("pdf2x");
                try
                {
                    string t = Path.Combine(dir, "doc.hwpx");
                    var h = job.Use(() => new Hangul(job));
                    h.OpenPdf(f);
                    try { h.SaveAs(t, "HWPX"); }
                    finally { h.CloseDoc(); }
                    sheets = OoxmlRead.HwpxTables(t);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (UserFacingException ex)
                {
                    Log.Write("PDF 표 읽기(한글) 실패: " + ex.GetType().Name);
                }
                finally
                {
                    Util.TryDeleteDir(dir);
                }
            }
            if (sheets == null || sheets.Count == 0)
            {
                var t = PdfText.Read(f, job);
                sheets = t != null && t.Usable ? t.GuessTables() : new List<XlsxSheet>();
                if (sheets.Count > 0) job.Note(Util.ShortName(f) + ": 글자 위치로 표를 짐작해 옮겼어요. 칸이 맞는지 확인해 주세요.");
            }
            return sheets;
        }

        // ── PPT로 ──

        static void PagesAsPictures(Job job, string f, string pdf, string note)
        {
            var slides = new List<PptSlide>();
            using (var doc = new WinPdfDoc(pdf))
            {
                for (int i = 0; i < doc.PageCount; i++)
                {
                    job.Check();
                    if (doc.PageCount > 1) job.Report(Util.ShortName(f) + " · " + (i + 1) + "/" + doc.PageCount + "쪽", (double)i / doc.PageCount);
                    var frame = Imaging.Decoder(doc.RenderPng(i, 160, 2600)).Frames[0];
                    slides.Add(new PptSlide { Image = Imaging.Jpeg(frame, 86), W = frame.PixelWidth, H = frame.PixelHeight });
                }
            }
            if (slides.Count == 0) throw new UserFacingException("쪽이 없는 문서예요.");
            long cx, cy;
            PptxWriter.SizeFor(slides[0].W, slides[0].H, out cx, out cy);
            Conversions.Produce(job, Util.OutputFile(f, ".pptx"), delegate (string d) { PptxWriter.Write(d, slides, cx, cy); });
            job.Note(Util.ShortName(f) + ": " + note);
        }

        static void ToPptx(Job job, List<string> files, bool picture)
        {
            var list = Only(job, files, f => Formats.KindOf(f) == Kind.Image || Formats.KindOf(f) == Kind.Pdf || Formats.KindOf(f) == Kind.Hwp || Formats.KindOf(f) == Kind.Word || Formats.KindOf(f) == Kind.Excel);
            var pages = list.Where(f => Formats.KindOf(f) != Kind.Image).OrderBy(f => Formats.KindOf(f) == Kind.Pdf ? 0 : (int)Formats.KindOf(f)).ToList();
            var images = list.Where(f => Formats.KindOf(f) == Kind.Image).ToList();
            Kind lastKind = Kind.Unknown;
            Conversions.EachFile(job, pages, "PPT로 바꾸는 중", delegate (string f)
            {
                Kind kind = Formats.KindOf(f);
                if (lastKind != Kind.Unknown && kind != lastKind) job.ReleaseResources();
                lastKind = kind;
                Conversions.WithPdf(job, f, delegate (string pdf)
                {
                    if (picture)
                    {
                        PagesAsPictures(job, f, pdf, "쪽을 그림으로 넣었어요. 모양은 그대로지만 글자는 고칠 수 없어요.");
                        return true;
                    }
                    var made = PdfToPpt.Build(job, pdf);
                    if (made == null)
                    {
                        PagesAsPictures(job, f, pdf, "글자를 옮기지 못해 쪽을 그림으로 넣었어요. 글자는 고칠 수 없어요.");
                        return true;
                    }
                    Conversions.Produce(job, Util.OutputFile(f, ".pptx"), delegate (string d) { DeckWriter.Write(d, made.Deck); });
                    string name = Util.ShortName(f) + ": ";
                    if (made.TextPages + made.OcrPages + made.LayerPages > 0) job.Note(name + "글자를 글상자로 옮겼어요. 표의 선과 그림은 배경이라 따로 옮길 수 없어요.");
                    if (made.LayerPages > 0) job.Note(name + "스캔한 " + made.LayerPages + "쪽은 파일에 들어 있던 글자 정보를 썼어요. 틀린 글자가 있는지 확인해 주세요.");
                    if (made.OcrPages > 0) job.Note(name + "스캔한 " + made.OcrPages + "쪽은 글자 인식으로 읽어 얹었어요. 틀린 글자가 있는지 확인해 주세요.");
                    if (made.PicturePages > 0) job.Note(name + "글자를 찾지 못한 " + made.PicturePages + "쪽은 그림 그대로예요.");
                    return true;
                });
            });
            if (images.Count == 0) return;
            Conversions.EachFile(job, images.Take(1).ToList(), "PPT로 묶는 중", delegate (string first)
            {
                var slides = new List<PptSlide>();
                foreach (var f in images)
                {
                    job.Check();
                    byte[] data;
                    string type;
                    int w, h;
                    PictureFor(f, out data, out type, out w, out h);
                    slides.Add(new PptSlide { Image = data, ImageType = type, W = w, H = h });
                }
                Conversions.Produce(job, GroupName(images, "장", ".pptx"), delegate (string d) { PptxWriter.Write(d, slides, 12192000, 6858000); });
            });
        }

        // ── 자막 ──

        static readonly Regex Cue = new Regex(@"(\d{1,2}:)?(\d{2}):(\d{2})[.,](\d{1,3})\s*-->\s*(\d{1,2}:)?(\d{2}):(\d{2})[.,](\d{1,3})");
        static readonly Regex VoiceTag = new Regex(@"</?(v|c|lang|ruby|rt)\b[^>]*>|<\d{1,2}:\d{2}(:\d{2})?\.\d{1,3}>");

        static string Stamp(string h, string m, string s, string ms, char mark)
        {
            string hour = h.Length == 0 ? "00" : h.TrimEnd(':').PadLeft(2, '0');
            return hour + ":" + m + ":" + s + mark + ms.PadRight(3, '0');
        }

        public static string ConvertCues(string text, bool toVtt)
        {
            var blocks = Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n').Trim(), @"\n\s*\n");
            var sb = new StringBuilder();
            if (toVtt) sb.Append("WEBVTT\n\n");
            int n = 0;
            foreach (var b in blocks)
            {
                var lines = b.Split('\n').ToList();
                int at = lines.FindIndex(l => Cue.IsMatch(l));
                if (at < 0) continue;
                var m = Cue.Match(lines[at]);
                char mark = toVtt ? '.' : ',';
                string time = Stamp(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, mark) + " --> " + Stamp(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value, mark);
                n++;
                if (!toVtt) sb.Append(n).Append('\n');
                sb.Append(time).Append('\n');
                foreach (var l in lines.Skip(at + 1))
                {
                    sb.Append(toVtt ? l : VoiceTag.Replace(l, "")).Append('\n');
                }
                sb.Append('\n');
            }
            if (n == 0) throw new UserFacingException("자막 시간을 찾지 못했어요.");
            return sb.ToString();
        }

        static void Subtitles(Job job, List<string> files, string target)
        {
            string from = target == "vtt" ? ".srt" : ".vtt";
            var list = Only(job, files, f => Formats.Ext(f) == from);
            Conversions.EachFile(job, list, "자막 바꾸는 중", delegate (string f)
            {
                string o = ConvertCues(TextFile.Read(f), target == "vtt");
                Conversions.Produce(job, Util.OutputFile(f, "." + target), delegate (string d) { File.WriteAllText(d, o, new UTF8Encoding(false)); });
            });
        }

        // ── 웹 문서·CSV ──

        static bool CanBecomeText(string f, string target)
        {
            string e = Formats.Ext(f);
            if (e == "." + target || (target == "html" && e == ".htm") || (target == "md" && e == ".markdown")) return false;
            if (IsCue(f) || IsTable(f)) return target == "txt";
            switch (Formats.KindOf(f))
            {
                case Kind.Text:
                case Kind.Pdf:
                case Kind.Hwp:
                case Kind.Word:
                    return true;
            }
            return false;
        }

        static string CuesToPlain(string text)
        {
            var sb = new StringBuilder();
            string last = null;
            bool any = false;
            foreach (var b in Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n').Trim(), @"\n\s*\n"))
            {
                var lines = b.Split('\n').ToList();
                int at = lines.FindIndex(l => Cue.IsMatch(l));
                if (at < 0) continue;
                any = true;
                foreach (var raw in lines.Skip(at + 1))
                {
                    string l = VoiceTag.Replace(raw, "").Trim();
                    // 자동 자막은 같은 줄이 이어서 되풀이된다.
                    if (l.Length == 0 || l == last) continue;
                    sb.Append(l).Append("\r\n");
                    last = l;
                }
            }
            if (!any) throw new UserFacingException("자막 시간을 찾지 못했어요.");
            return sb.ToString();
        }

        /// PDF는 눈에 보이는 줄마다 끊겨 있다. 줄이 폭을 거의 채웠고 문장이 끝나지 않았으면 다음 줄과 이어진 글로 본다.
        static List<DocBlock> JoinPdfLines(List<DocBlock> blocks)
        {
            var result = new List<DocBlock>();
            int start = 0;
            while (start < blocks.Count)
            {
                int end = start;
                while (end < blocks.Count && blocks[end].Kind != BlockKind.PageBreak) end++;
                int longest = 0;
                for (int i = start; i < end; i++) longest = Math.Max(longest, blocks[i].Text.Length);
                DocBlock prev = null;
                for (int i = start; i < end; i++)
                {
                    var b = blocks[i];
                    string t = (b.Text ?? "").Trim();
                    bool marker = Regex.IsMatch(t, @"^([-•·※○●□■▶◆]|\d{1,3}[.)]|[①-⑳]|[가-힣][.)]\s)");
                    if (prev == null && result.Count > 0 && i == start) prev = result[result.Count - 1];
                    if (prev != null && !marker && prev.Text.Length >= longest * 0.85 && !Regex.IsMatch(prev.Text, @"[.!?。…:;)\]」』”]$"))
                    {
                        prev.Text += " " + t;
                        continue;
                    }
                    var copy = DocBlock.Para(t);
                    result.Add(copy);
                    prev = copy;
                }
                start = end + 1;
            }
            return result;
        }

        static void WebText(Job job, List<string> files, string target)
        {
            var list = Only(job, files, f => CanBecomeText(f, target));
            var plain = new UTF8Encoding(target == "txt");
            Conversions.EachFile(job, list, "바꾸는 중", delegate (string f)
            {
                string dst = Util.OutputFile(f, "." + target);
                string stem = Path.GetFileNameWithoutExtension(f);
                if (IsCue(f))
                {
                    string cues = CuesToPlain(TextFile.Read(f));
                    Conversions.Produce(job, dst, delegate (string d) { File.WriteAllText(d, cues, plain); });
                    return;
                }
                if (IsTable(f))
                {
                    var sheets = SheetsOf(f);
                    var sheet = sheets.FirstOrDefault(s => s.Rows.Count > 0);
                    if (sheet == null) throw new UserFacingException("빈 파일이에요.");
                    string rows = string.Join("\r\n", sheet.Rows.Select(r => string.Join("\t", r.Select(c => (c ?? "").Replace('\n', ' ').Replace('\t', ' '))))) + "\r\n";
                    Conversions.Produce(job, dst, delegate (string d) { File.WriteAllText(d, rows, plain); });
                    if (sheets.Count > 1) job.Note(Util.ShortName(f) + ": 시트 " + sheets.Count + "개 가운데 첫 시트만 바꿨어요.");
                    return;
                }
                var blocks = BlocksOf(job, f);
                if (Formats.KindOf(f) == Kind.Pdf) blocks = JoinPdfLines(blocks);
                if (!blocks.Any(b => b.Kind != BlockKind.PageBreak)) throw new UserFacingException("옮길 글자를 찾지 못했어요.");
                if (target == "epub") Conversions.Produce(job, dst, delegate (string d) { Epub.Write(d, blocks, stem); });
                else
                {
                    string o = target == "md" ? MarkdownDoc.ToMarkdown(blocks) : target == "html" ? MarkdownDoc.ToHtml(blocks, stem) : MarkdownDoc.ToPlain(blocks);
                    Conversions.Produce(job, dst, delegate (string d) { File.WriteAllText(d, o, plain); });
                    if (target != "html" && blocks.Any(b => b.Kind == BlockKind.Picture)) job.Note(Util.ShortName(f) + ": 이 형식에는 사진을 넣을 수 없어 뺐어요.");
                }
                if (Formats.KindOf(f) == Kind.Pdf) job.Note(Util.ShortName(f) + ": PDF에서는 글자만 옮겼어요. 표와 그림은 옮기지 못해요.");
            });
        }

        static void ToTable(Job job, List<string> files, string target)
        {
            var list = Only(job, files, f => IsTable(f) && Formats.Ext(f) != "." + target);
            Conversions.EachFile(job, list, target.ToUpperInvariant() + "로 바꾸는 중", delegate (string f)
            {
                var sheets = SheetsOf(f);
                var first = sheets.FirstOrDefault(s => s.Rows.Count > 0);
                if (first == null) throw new UserFacingException("빈 파일이에요.");
                if (target == "ods")
                {
                    Conversions.Produce(job, Util.OutputFile(f, ".ods"), delegate (string d) { OdsFile.Write(d, sheets); });
                    return;
                }
                string o = TextFile.ToDelimited(first.Rows.Select(r => (IList<string>)r.Select(c => c ?? "").ToList()), target == "tsv" ? '\t' : ',');
                Conversions.Produce(job, Util.OutputFile(f, "." + target), delegate (string d) { File.WriteAllText(d, o, new UTF8Encoding(true)); });
                if (sheets.Count > 1) job.Note(Util.ShortName(f) + ": 시트 " + sheets.Count + "개 가운데 첫 시트만 바꿨어요.");
            });
        }
    }

    // ── 문서 속 사진 ──

    public static class DocImages
    {
        static readonly HashSet<string> Picture = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp" };

        static string MediaPrefix(string f)
        {
            switch (Formats.Ext(f))
            {
                case ".docx": return "word/media/";
                case ".xlsx": return "xl/media/";
                case ".pptx": return "ppt/media/";
                case ".hwpx": return "BinData/";
            }
            return null;
        }

        static byte[] Bytes(ZipArchiveEntry e)
        {
            using (var s = e.Open())
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        // ── 뽑기 ──

        public static void Extract(Job job, List<string> files)
        {
            Conversions.EachFile(job, files, "사진 찾는 중", delegate (string f)
            {
                var found = new List<KeyValuePair<string, byte[]>>();
                int skipped = 0;
                Kind k = Formats.KindOf(f);
                if (k == Kind.Pdf) FromPdf(job, f, found, ref skipped);
                else if (Formats.Ext(f) == ".hwp")
                {
                    string dir = Util.TempDir("hwp2x");
                    try
                    {
                        string t = Path.Combine(dir, "doc.hwpx");
                        job.Use(() => new Hangul(job)).Convert(f, t, "HWPX");
                        FromZip(t, found, ref skipped);
                    }
                    finally
                    {
                        Util.TryDeleteDir(dir);
                    }
                }
                else FromZip(f, found, ref skipped);
                if (found.Count == 0) throw new UserFacingException("문서 안에서 사진을 찾지 못했어요.");
                string stem = Path.GetFileNameWithoutExtension(f);
                if (found.Count == 1)
                {
                    Conversions.Produce(job, Conversions.Sibling(f, "_사진", found[0].Key), delegate (string d) { File.WriteAllBytes(d, found[0].Value); });
                }
                else
                {
                    string folder = Util.UniquePath(Path.Combine(Util.OutputDirFor(f), stem + "_사진"));
                    Directory.CreateDirectory(folder);
                    int digits = Math.Max(2, found.Count.ToString().Length);
                    for (int i = 0; i < found.Count; i++)
                    {
                        File.WriteAllBytes(Path.Combine(folder, stem + "_" + (i + 1).ToString().PadLeft(digits, '0') + found[i].Key), found[i].Value);
                    }
                    job.Output(folder);
                    job.Note(Util.ShortName(f) + ": 사진 " + found.Count + "장을 '" + Path.GetFileName(folder) + "' 폴더에 담았어요.");
                }
                if (skipped > 0) job.Note(Util.ShortName(f) + ": 꺼낼 수 없는 형식의 그림 " + skipped + "개는 건너뛰었어요.");
            });
        }

        static void FromZip(string path, List<KeyValuePair<string, byte[]>> found, ref int skipped)
        {
            string prefix = MediaPrefix(path);
            if (prefix == null) throw new UserFacingException("이 형식에서는 사진을 꺼낼 수 없어요.");
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var e in z.Entries.Where(x => x.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && x.Name.Length > 0).OrderBy(x => x.Name, Comparer<string>.Create(Util.NaturalCompare)))
                {
                    string ext = Path.GetExtension(e.Name).ToLowerInvariant();
                    if (Picture.Contains(ext)) found.Add(new KeyValuePair<string, byte[]>(ext, Bytes(e)));
                    else skipped++;
                }
            }
        }

        static void FromPdf(Job job, string path, List<KeyValuePair<string, byte[]>> found, ref int skipped)
        {
            var doc = PdfTools.OpenPlain(path);
            var order = doc.Reachable();
            var masks = new HashSet<int>();
            foreach (int n in order)
            {
                var s = doc.Get(n) as PdfStream;
                if (s == null) continue;
                var a = s.Dict["SMask"] as PdfRef;
                if (a != null) masks.Add(a.Num);
                var b = s.Dict["Mask"] as PdfRef;
                if (b != null) masks.Add(b.Num);
            }
            foreach (int n in order)
            {
                job.Check();
                var s = doc.Get(n) as PdfStream;
                if (s == null || s.Dict.NameOf("Subtype") != "Image" || masks.Contains(n)) continue;
                int w = doc.IntOf(s.Dict, "Width", 0), h = doc.IntOf(s.Dict, "Height", 0);
                if (w < 40 && h < 40) continue;
                try
                {
                    PdfDict parms;
                    string filter = PdfDocument.SingleFilter(doc, s.Dict, out parms);
                    if (filter == "DCTDecode" || filter == "DCT")
                    {
                        found.Add(new KeyValuePair<string, byte[]>(".jpg", s.Data));
                        continue;
                    }
                    var bmp = Raw(doc, s, w, h);
                    if (bmp == null)
                    {
                        skipped++;
                        continue;
                    }
                    found.Add(new KeyValuePair<string, byte[]>(".png", Imaging.Encode(bmp, "png", 0)));
                }
                catch (PdfException)
                {
                    skipped++;
                }
            }
        }

        /// 압축을 푼 그림 자료를 비트맵으로. 8비트 회색·RGB·색 번호표와 투명 가리개를 처리한다.
        static BitmapSource Raw(PdfDocument doc, PdfStream s, int w, int h)
        {
            int bpc = doc.IntOf(s.Dict, "BitsPerComponent", 8);
            var image = doc.Resolve(s.Dict["ImageMask"]) as PdfBool;
            if (image != null && image.V) return null;
            byte[] data = doc.Decode(s);
            var cs = doc.Resolve(s.Dict["ColorSpace"]);
            byte[] lookup = null;
            int n = Channels(doc, cs, ref lookup);
            if (n == 0) return null;
            if (bpc != 8 && !(bpc == 1 && (n == 1 || lookup != null)) && !(bpc == 4 && lookup != null) && !(bpc == 2 && lookup != null)) return null;
            var o = new byte[(long)w * h * 4];
            int stride = lookup != null || n == 1 ? (w * bpc + 7) / 8 : w * n;
            if (data.LongLength < (long)stride * h) return null;
            byte[] alpha = Mask(doc, s, w, h);
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte r, g, b;
                    if (lookup != null)
                    {
                        int v = Sample(data, row, x, bpc);
                        int at = v * 3;
                        r = at + 2 < lookup.Length ? lookup[at] : (byte)0;
                        g = at + 2 < lookup.Length ? lookup[at + 1] : (byte)0;
                        b = at + 2 < lookup.Length ? lookup[at + 2] : (byte)0;
                    }
                    else if (n == 1)
                    {
                        int v = Sample(data, row, x, bpc);
                        r = g = b = (byte)(bpc == 8 ? v : v * 255 / ((1 << bpc) - 1));
                    }
                    else
                    {
                        r = data[row + x * 3];
                        g = data[row + x * 3 + 1];
                        b = data[row + x * 3 + 2];
                    }
                    int p = (y * w + x) * 4;
                    o[p] = b;
                    o[p + 1] = g;
                    o[p + 2] = r;
                    o[p + 3] = alpha == null ? (byte)255 : alpha[y * w + x];
                }
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, o, w * 4);
            bmp.Freeze();
            return bmp;
        }

        static int Sample(byte[] d, int row, int x, int bpc)
        {
            if (bpc == 8) return d[row + x];
            int bit = x * bpc;
            int v = d[row + bit / 8];
            return (v >> (8 - bpc - bit % 8)) & ((1 << bpc) - 1);
        }

        static int Channels(PdfDocument doc, PdfObj cs, ref byte[] lookup)
        {
            var name = cs as PdfName;
            if (name != null)
            {
                if (name.V == "DeviceGray" || name.V == "G" || name.V == "CalGray") return 1;
                if (name.V == "DeviceRGB" || name.V == "RGB" || name.V == "CalRGB") return 3;
                return 0;
            }
            var a = cs as PdfArray;
            if (a == null || a.Items.Count < 1) return 0;
            var family = doc.Resolve(a.Items[0]) as PdfName;
            if (family == null) return 0;
            if (family.V == "ICCBased" && a.Items.Count >= 2)
            {
                var st = doc.Resolve(a.Items[1]) as PdfStream;
                int n = st == null ? 0 : doc.IntOf(st.Dict, "N", 0);
                return n == 1 || n == 3 ? n : 0;
            }
            if ((family.V == "Indexed" || family.V == "I") && a.Items.Count >= 4)
            {
                byte[] dummy = null;
                int baseN = Channels(doc, doc.Resolve(a.Items[1]), ref dummy);
                var raw = doc.Resolve(a.Items[3]);
                byte[] table = raw is PdfString ? ((PdfString)raw).V : raw is PdfStream ? doc.Decode((PdfStream)raw) : null;
                if (table == null || (baseN != 1 && baseN != 3)) return 0;
                if (baseN == 1)
                {
                    var rgb = new byte[table.Length * 3];
                    for (int i = 0; i < table.Length; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = table[i];
                    table = rgb;
                }
                lookup = table;
                return 3;
            }
            if (family.V == "CalGray") return 1;
            if (family.V == "CalRGB") return 3;
            return 0;
        }

        static byte[] Mask(PdfDocument doc, PdfStream s, int w, int h)
        {
            var m = doc.Resolve(s.Dict["SMask"]) as PdfStream;
            if (m == null) return null;
            int mw = doc.IntOf(m.Dict, "Width", 0), mh = doc.IntOf(m.Dict, "Height", 0);
            if (mw != w || mh != h || doc.IntOf(m.Dict, "BitsPerComponent", 8) != 8) return null;
            try
            {
                byte[] d = doc.Decode(m);
                return d.LongLength >= (long)w * h ? d : null;
            }
            catch (PdfException)
            {
                return null;
            }
        }

        // ── 줄이기 ──

        public static void Shrink(string src, string dst, int longSide, int quality, Job job)
        {
            string prefix = MediaPrefix(src);
            if (prefix == null) throw new UserFacingException("이 형식은 줄일 수 없어요.");
            bool canRename = Formats.Ext(src) != ".hwpx";
            using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
            using (var os = new FileStream(dst, FileMode.Create, FileAccess.Write))
            using (var o = new ZipArchive(os, ZipArchiveMode.Create))
            {
                var replaced = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var media = z.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0).ToList();
                for (int i = 0; i < media.Count; i++)
                {
                    job.Check();
                    string ext = Path.GetExtension(media[i].Name).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp") continue;
                    byte[] before = Bytes(media[i]);
                    string newExt;
                    byte[] smaller;
                    try
                    {
                        smaller = Recompress(before, ext, longSide, quality, canRename, out newExt);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("문서 속 그림 건너뜀: " + ex.GetType().Name);
                        continue;
                    }
                    if (smaller == null) continue;
                    replaced[media[i].FullName] = smaller;
                    if (newExt != ext) renamed[media[i].FullName] = Path.ChangeExtension(media[i].FullName, newExt).Replace('\\', '/');
                }
                foreach (var e in z.Entries)
                {
                    job.Check();
                    string name = e.FullName;
                    byte[] data;
                    if (replaced.TryGetValue(name, out data))
                    {
                        string nn;
                        if (renamed.TryGetValue(name, out nn)) name = nn;
                    }
                    else
                    {
                        data = Bytes(e);
                        bool rels = name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
                        if (renamed.Count > 0 && (rels || name == "[Content_Types].xml"))
                        {
                            string text = new UTF8Encoding(false).GetString(data);
                            if (rels)
                            {
                                foreach (var kv in renamed) text = text.Replace("media/" + Path.GetFileName(kv.Key), "media/" + Path.GetFileName(kv.Value));
                            }
                            else if (!Regex.IsMatch(text, "Extension=\"jpeg\"", RegexOptions.IgnoreCase))
                            {
                                text = text.Replace("<Default ", "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/><Default ", 1);
                            }
                            data = new UTF8Encoding(false).GetBytes(text);
                        }
                    }
                    // HWPX의 맨 앞 mimetype은 압축하지 않아야 한다.
                    var entry = o.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using (var s = entry.Open()) s.Write(data, 0, data.Length);
                }
            }
        }

        static string Replace(this string s, string a, string b, int count)
        {
            int i = s.IndexOf(a, StringComparison.Ordinal);
            return i < 0 ? s : s.Substring(0, i) + b + s.Substring(i + a.Length);
        }

        static bool AnyTransparent(BitmapSource s)
        {
            if (!Imaging.MayHaveAlpha(s)) return false;
            var c = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
            int w = c.PixelWidth, h = c.PixelHeight;
            var px = new byte[(long)w * h * 4];
            c.CopyPixels(px, w * 4, 0);
            for (int i = 3; i < px.Length; i += 4)
            {
                if (px[i] != 255) return true;
            }
            return false;
        }

        static byte[] Recompress(byte[] before, string ext, int longSide, int quality, bool canRename, out string newExt)
        {
            newExt = ext;
            var frame = Imaging.Decoder(before).Frames[0];
            if (frame.Format == PixelFormats.Cmyk32) return null;
            var src = Imaging.Upright(frame);
            var fit = Imaging.Fit(src, longSide);
            byte[] after;
            if (ext == ".jpg" || ext == ".jpeg") after = Imaging.Jpeg(fit, quality);
            else
            {
                bool clear = AnyTransparent(fit);
                after = Imaging.Encode(fit, "png", 0);
                if (ext == ".bmp") after = Imaging.Encode(Imaging.Flatten(fit), "bmp", 0);
                if (canRename && !clear)
                {
                    byte[] jpeg = Imaging.Jpeg(fit, quality);
                    if (jpeg.Length < after.Length * 0.7)
                    {
                        after = jpeg;
                        newExt = ".jpeg";
                    }
                }
            }
            return after.Length < before.Length * 0.97 ? after : null;
        }
    }
}
