// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 도구 시험 중 전자책·ODS·압축·SVG·글자 문서 PDF 부분. 예시 파일은 이 시험이 직접 만든다(진짜 문서 안 씀).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    static partial class ToolTest
    {
        static string F2(string name)
        {
            return Path.Combine(P("fmtjob"), name);
        }

        static string Put(string name, string text)
        {
            string p = F2(name);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text, new UTF8Encoding(false));
            return p;
        }

        static string ReadU(string path)
        {
            return path == "" || !File.Exists(path) ? "" : TextFile.Read(path);
        }

        static string Out1(Job j)
        {
            return j.Outputs.Count == 0 ? "" : j.Outputs[0];
        }

        static string PdfWords(string pdf)
        {
            if (pdf == "" || !File.Exists(pdf)) return "";
            var t = PdfText.Read(pdf, null);
            return t == null ? "" : t.Plain();
        }

        sealed class RawEntry
        {
            public byte[] Name;
            public int Flags;
            public byte[] Data = new byte[0];
            public int Host;
            public uint External;

            public RawEntry(string name)
            {
                Name = new UTF8Encoding(false).GetBytes(name);
                Flags = 0x800;
            }
        }

        static void RawZip(string path, params RawEntry[] items)
        {
            using (var fs = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                var offsets = new List<uint>();
                foreach (var e in items)
                {
                    offsets.Add((uint)fs.Position);
                    w.Write(0x04034B50u);
                    w.Write((ushort)20);
                    w.Write((ushort)e.Flags);
                    w.Write((ushort)0);
                    w.Write((ushort)0);
                    w.Write((ushort)0x5021);
                    w.Write(Crc32.Of(e.Data));
                    w.Write((uint)e.Data.Length);
                    w.Write((uint)e.Data.Length);
                    w.Write((ushort)e.Name.Length);
                    w.Write((ushort)0);
                    w.Write(e.Name);
                    w.Write(e.Data);
                }
                uint start = (uint)fs.Position;
                for (int i = 0; i < items.Length; i++)
                {
                    var e = items[i];
                    w.Write(0x02014B50u);
                    w.Write((byte)20);
                    w.Write((byte)e.Host);
                    w.Write((ushort)20);
                    w.Write((ushort)e.Flags);
                    w.Write((ushort)0);
                    w.Write((ushort)0);
                    w.Write((ushort)0x5021);
                    w.Write(Crc32.Of(e.Data));
                    w.Write((uint)e.Data.Length);
                    w.Write((uint)e.Data.Length);
                    w.Write((ushort)e.Name.Length);
                    w.Write((ushort)0);
                    w.Write((ushort)0);
                    w.Write((ushort)0);
                    w.Write((ushort)0);
                    w.Write(e.External);
                    w.Write(offsets[i]);
                    w.Write(e.Name);
                }
                uint size = (uint)fs.Position - start;
                w.Write(0x06054B50u);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)items.Length);
                w.Write((ushort)items.Length);
                w.Write(size);
                w.Write(start);
                w.Write((ushort)0);
            }
        }

        static byte[] Ascii8(string s)
        {
            return Encoding.ASCII.GetBytes(s);
        }

        /// 폴더 안의 파일을 상대 경로로 모은다(내용 비교용).
        static Dictionary<string, string> Tree(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) map[f.Substring(root.Length + 1)] = Convert.ToBase64String(File.ReadAllBytes(f));
            return map;
        }

        static bool SameTree(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            return a.Count == b.Count && a.All(kv => b.ContainsKey(kv.Key) && b[kv.Key] == kv.Value);
        }

        static byte[] TarBlock(string name, char type, long size, string magic = "ustar")
        {
            var h = new byte[512];
            Encoding.ASCII.GetBytes(name).CopyTo(h, 0);
            Encoding.ASCII.GetBytes("0000644\0").CopyTo(h, 100);
            Encoding.ASCII.GetBytes("0000000\0").CopyTo(h, 108);
            Encoding.ASCII.GetBytes("0000000\0").CopyTo(h, 116);
            Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(h, 124);
            Encoding.ASCII.GetBytes("00000000000\0").CopyTo(h, 136);
            for (int i = 148; i < 156; i++) h[i] = 32;
            h[156] = (byte)type;
            Encoding.ASCII.GetBytes(magic).CopyTo(h, 257);
            h[263] = 48;
            h[264] = 48;
            Encoding.ASCII.GetBytes(Convert.ToString(h.Sum(b => (int)b), 8).PadLeft(6, '0') + "\0 ").CopyTo(h, 148);
            return h;
        }

        static void TarAdd(Stream o, string name, char type, byte[] data)
        {
            o.Write(TarBlock(name, type, data.Length), 0, 512);
            o.Write(data, 0, data.Length);
            int extra = data.Length % 512;
            if (extra > 0) o.Write(new byte[512 - extra], 0, 512 - extra);
        }

        static void FormatTests()
        {
            Cli.Print("[형식]");
            string w = P("fmtjob");
            if (Directory.Exists(w)) Directory.Delete(w, true);
            Directory.CreateDirectory(w);

            // ── 글자 문서 → PDF(프로그램 없이) ──
            const string md = "# 큰 제목\n\n첫 문단 **굵게** 입니다. English 漢字 かな 한글 ✓ 이모지😀.\n\n## 작은 제목\n\n- 항목 하나\n- 항목 둘\n\n1. 첫째\n2. 둘째\n\n| 이름 | 값 |\n| --- | --- |\n| 가 | 1 |\n| 나<br>다 | 2 |\n\n끝 문단.\n";
            string a = Put("a.md", md);
            string pdf = Out1(RunJob("pdf", "go", new[] { a }, null));
            string words = PdfWords(pdf);
            Check("마크다운→PDF: 글자를 고를 수 있다", words.Contains("첫 문단") && words.Contains("굵게") && words.Contains("English") && words.Contains("漢字") && words.Contains("항목 둘") && words.Contains("끝 문단"), words.Replace("\n", " / "));
            Check("마크다운→PDF: 한 쪽", pdf != "" && PageCount(pdf) == 1, "");
            Check("마크다운→PDF: 글꼴이 들어 있고 가볍다", pdf != "" && new FileInfo(pdf).Length < 400 * 1024, pdf == "" ? "" : Conversions.Size(new FileInfo(pdf).Length));
            var longDoc = new StringBuilder("# 긴 문서\n\n");
            for (int i = 1; i <= 220; i++) longDoc.Append("문단 ").Append(i).Append("번 입니다. 줄이 길어지면 낱말 사이에서 다음 줄로 넘어가야 합니다. 이 문장은 일부러 조금 길게 적었습니다.\n\n");
            string longPdf = Out1(RunJob("pdf", "go", new[] { Put("long.txt", longDoc.ToString()) }, null));
            string longWords = PdfWords(longPdf);
            Check("긴 글→PDF: 여러 쪽, 끝까지", longPdf != "" && PageCount(longPdf) > 5 && longWords.Contains("문단 220번"), longPdf == "" ? "" : PageCount(longPdf) + "쪽");
            var table = new StringBuilder("| 번호 | 내용 |\n| --- | --- |\n");
            for (int i = 1; i <= 150; i++) table.Append("| ").Append(i).Append(" | 표 내용 ").Append(i).Append(" |\n");
            string tablePdf = Out1(RunJob("pdf", "go", new[] { Put("table.md", table.ToString()) }, null));
            string tableWords = PdfWords(tablePdf);
            Check("긴 표→PDF: 쪽을 넘어가고 머리글이 되풀이된다", tablePdf != "" && PageCount(tablePdf) >= 3 && tableWords.Contains("표 내용 150") && System.Text.RegularExpressions.Regex.Matches(tableWords, "번호").Count >= 3, tablePdf == "" ? "" : PageCount(tablePdf) + "쪽");
            string htmlPdf = Out1(RunJob("pdf", "go", new[] { Put("b.html", "<!doctype html><html><head><title>t</title><style>p{color:red}</style><script>alert(1)</script></head><body><h1>웹 제목</h1><p>웹 문단입니다.</p><script>document.write('나쁜 글자')</script></body></html>") }, null));
            string htmlWords = PdfWords(htmlPdf);
            Check("HTML→PDF: 스크립트는 안 읽는다", htmlWords.Contains("웹 문단입니다") && !htmlWords.Contains("나쁜") && !htmlWords.Contains("alert"), htmlWords.Replace("\n", " / "));
            Check("SRT는 PDF 칸에 안 나온다", ActionOf("pdf").Usable(new[] { F2("x.srt") }).Count == 0 && ActionOf("pdf").Usable(new[] { F2("x.md"), F2("y.epub") }).Count == 2, "");

            // ── 전자책 ──
            string epub = Out1(RunJob("format", "epub", new[] { a }, null));
            bool stored = false;
            if (epub != "")
            {
                byte[] head = File.ReadAllBytes(epub).Take(58).ToArray();
                stored = head[0] == 0x50 && head[1] == 0x4B && BitConverter.ToUInt16(head, 8) == 0 && Encoding.ASCII.GetString(head, 30, 8) == "mimetype" && Encoding.ASCII.GetString(head, 38, 20) == "application/epub+zip";
            }
            Check("마크다운→EPUB: mimetype이 맨 앞에 압축 없이", stored, epub);
            var back = epub == "" ? new List<DocBlock>() : Epub.Read(epub);
            string backText = MarkdownDoc.ToPlain(back);
            Check("EPUB 되읽기: 제목·문단·목록·표", back.Any(b => b.Kind == BlockKind.Heading && b.Text == "큰 제목") && backText.Contains("굵게") && back.Count(b => b.Kind == BlockKind.Bullet) == 2 && back.Count(b => b.Kind == BlockKind.Number) == 2 && back.Any(b => b.Kind == BlockKind.Table && b.Rows.Count == 3), backText.Replace("\n", " / "));
            string multi = Put("multi.md", "# 첫 장\n\n첫 장 글.\n\n# 둘째 장\n\n둘째 장 글.\n");
            string multiEpub = Out1(RunJob("format", "epub", new[] { multi }, null));
            int chapters = 0;
            if (multiEpub != "") using (var z = ZipFile2(multiEpub)) chapters = z.Entries.Count(e => e.Name.StartsWith("ch") && e.Name.EndsWith(".xhtml"));
            Check("EPUB: 큰 제목마다 장을 나눈다", chapters == 2, chapters + "장");
            Check("EPUB→글자·마크다운·HTML·워드·PDF", ReadU(Out1(RunJob("format", "txt", new[] { epub }, null))).Contains("끝 문단")
                && ReadU(Out1(RunJob("format", "md", new[] { epub }, null))).Contains("## 작은 제목")
                && ReadU(Out1(RunJob("format", "html", new[] { epub }, null))).Contains("<h1>큰 제목</h1>")
                && Out1(RunJob("format", "docx", new[] { epub }, null)) != ""
                && PdfWords(Out1(RunJob("pdf", "go", new[] { epub }, null))).Contains("끝 문단"), "");
            string drm = F2("drm.epub");
            RawZip(drm, new RawEntry("mimetype") { Data = Ascii8("application/epub+zip") }, new RawEntry("META-INF/encryption.xml") { Data = Ascii8("<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><EncryptedData xmlns=\"http://www.w3.org/2001/04/xmlenc#\"><EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#aes128-cbc\"/></EncryptedData></encryption>") });
            var drmJob = RunJob("format", "txt", new[] { drm }, null);
            Check("복제 방지 전자책은 열지 않는다", drmJob.Outputs.Count == 0 && drmJob.Problems.Any(x => x.Contains("복제 방지")), string.Join(" / ", drmJob.Problems));

            // 사진 든 HTML → 전자책 → HTML
            var dotPng = new WriteableBitmap(8, 8, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);
            byte[] pngBytes = Imaging.Encode(dotPng, "png", 0);
            string picHtml = Put("pic.html", "<html><body><h1>사진 문서</h1><p>앞 글</p><img src=\"data:image/png;base64," + Convert.ToBase64String(pngBytes) + "\"><p>뒤 글</p><img src=\"https://example.invalid/x.png\"></body></html>");
            string picEpub = Out1(RunJob("format", "epub", new[] { picHtml }, null));
            var picBack = picEpub == "" ? new List<DocBlock>() : Epub.Read(picEpub);
            Check("HTML의 data 그림은 전자책까지 옮기고 바깥 주소는 안 읽는다", picBack.Count(b => b.Kind == BlockKind.Picture) == 1, picBack.Count + "덩어리");

            // ── 워드 읽기 ──
            var docBlocks = new List<DocBlock>
            {
                DocBlock.Head("워드 제목", 1),
                DocBlock.Para("워드 **굵은** 문단"),
                new DocBlock { Kind = BlockKind.Table, Rows = new List<List<string>> { new List<string> { "칸1", "칸2" }, new List<string> { "값1", "값2" } } },
                new DocBlock { Kind = BlockKind.Picture, Image = pngBytes, ImageType = "png", W = 8, H = 8 }
            };
            string docx = F2("d.docx");
            DocxWriter.Write(docx, docBlocks);
            var docBack = DocxBlocks.Read(docx);
            Check("워드 읽기: 제목·굵게·표·사진", docBack.Any(b => b.Kind == BlockKind.Heading && b.Text == "워드 제목") && docBack.Any(b => b.Text.Contains("**굵은**")) && docBack.Any(b => b.Kind == BlockKind.Table && b.Rows[1][1] == "값2") && docBack.Any(b => b.Kind == BlockKind.Picture), docBack.Count + "덩어리");
            Check("워드→마크다운·HTML·글자", ReadU(Out1(RunJob("format", "md", new[] { docx }, null))).Contains("# 워드 제목") && ReadU(Out1(RunJob("format", "html", new[] { docx }, null))).Contains("data:image/") && ReadU(Out1(RunJob("format", "txt", new[] { docx }, null))).Contains("값1\t값2"), "");
            // PDF는 마크다운·HTML·EPUB·글자 파일로 바꾸지 않는다(표가 한 줄로 뭉개짐). 같이 놓은 워드만 바꾸고 '글자 뽑기'를 알려 준다.
            var pdfDocx = RunJob("format", "html", new[] { tablePdf, docx }, null);
            Check("PDF+워드→HTML: 워드만 바꾸고 PDF는 글자 뽑기로 안내", pdfDocx.Outputs.Count == 1 && Path.GetFileNameWithoutExtension(pdfDocx.Outputs[0]).StartsWith("d") && pdfDocx.Notes.Any(n => n.Contains("글자 뽑기")), pdfDocx.Outputs.Count + "개 / " + string.Join(" / ", pdfDocx.Notes));
            var pdfOnly = RunJob("format", "md", new[] { tablePdf }, null);
            Check("PDF만 마크다운으로: 만들지 않고 까닭을 알린다", pdfOnly.Outputs.Count == 0 && pdfOnly.Problems.Any(x => x.Contains("글자 뽑기")), string.Join(" / ", pdfOnly.Problems));
            if (!MsWord.Installed)
            {
                var nw = RunJob("pdf", "go", new[] { docx }, null);
                Check("MS 워드 없이 워드→PDF: 글자는 옮기고 알려 준다", PdfWords(Out1(nw)).Contains("워드 제목") && nw.Notes.Any(n => n.Contains("MS 워드")), string.Join(" / ", nw.Notes));
            }

            // ── 표 파일 ──
            string csv = F2("t.csv");
            File.WriteAllText(csv, "번호,이름,값,전화\n1,\"쉼표, 있음\",12.5,01012345678\n2,\"줄\n바꿈\",7,\n", Encoding.GetEncoding(949));
            string ods = Out1(RunJob("format", "ods", new[] { csv }, null));
            var odsRows = ods == "" ? null : OdsFile.Read(ods);
            Check("CSV→ODS 되읽기: 값·앞 0·줄바꿈", odsRows != null && odsRows[0].Rows.Count == 3 && odsRows[0].Rows[1][1] == "쉼표, 있음" && odsRows[0].Rows[1][2] == "12.5" && odsRows[0].Rows[1][3] == "01012345678" && odsRows[0].Rows[2][1] == "줄\n바꿈", odsRows == null ? "" : odsRows[0].Rows.Count + "줄");
            if (ods != "") using (var z = ZipFile2(ods)) Check("ODS: mimetype이 맨 앞", z.Entries[0].FullName == "mimetype" && z.Entries[0].CompressedLength == z.Entries[0].Length, "");
            string xlsxFromOds = Out1(RunJob("format", "xlsx", new[] { ods }, null));
            Check("ODS→엑셀", xlsxFromOds != "" && OoxmlRead.XlsxRead(xlsxFromOds)[0].Rows[1][1] == "쉼표, 있음", "");
            string tsv = Out1(RunJob("format", "tsv", new[] { csv }, null));
            string tsvText = ReadU(tsv);
            Check("CSV→TSV", tsvText.Contains("번호\t이름\t값") && tsvText.Contains("1\t쉼표, 있음\t12.5"), tsvText.Replace("\r\n", " / "));
            Check("TSV→CSV·ODS", ReadU(Out1(RunJob("format", "csv", new[] { tsv }, null))).Contains("\"쉼표, 있음\"") && Out1(RunJob("format", "ods", new[] { tsv }, null)) != "", "");
            // 표만 골랐을 땐 '글자 파일'이 고를 거리에 없다(TSV와 똑같다). 문서와 섞어 고른 묶음에서 '글자 파일'을 고르면 표도 탭으로 나눈 글자가 된다.
            var mixTxt = RunJob("format", "txt", new[] { docx, csv }, null);
            string csvTxt = mixTxt.Outputs.FirstOrDefault(o => Path.GetFileNameWithoutExtension(o).StartsWith("t")) ?? "";
            Check("문서와 섞어 고른 표→글자 파일: 탭으로", mixTxt.Outputs.Count == 2 && ReadU(csvTxt).Contains("1\t쉼표, 있음\t12.5"), mixTxt.Outputs.Count + "개");
            // 빈 칸·빈 줄을 '여러 번 되풀이'로 적은 ODS(프로그램이 만드는 흔한 모양)
            string craft = F2("craft.ods");
            RawZip(craft, new RawEntry("mimetype") { Data = Ascii8("application/vnd.oasis.opendocument.spreadsheet") }, new RawEntry("content.xml") { Data = new UTF8Encoding(false).GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"><office:body><office:spreadsheet><table:table table:name=\"첫 시트\"><table:table-row><table:table-cell office:value-type=\"string\" table:number-columns-repeated=\"3\"><text:p>같은값</text:p></table:table-cell><table:table-cell table:number-columns-repeated=\"1000\"/></table:table-row><table:table-row table:number-rows-repeated=\"2\"><table:table-cell/></table:table-row><table:table-row><table:table-cell office:value-type=\"float\" office:value=\"3.5\"><text:p>3,5</text:p></table:table-cell><table:table-cell office:value-type=\"date\" office:date-value=\"2026-03-02\"><text:p>3월 2일</text:p></table:table-cell><table:table-cell office:value-type=\"string\"><text:p>가<text:s text:c=\"2\"/>나</text:p></table:table-cell></table:table-row><table:table-row table:number-rows-repeated=\"1048570\"><table:table-cell table:number-columns-repeated=\"1024\"/></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>") });
            var cr = OdsFile.Read(craft);
            Check("ODS 되풀이: 값만 펴고 뒤쪽 빈 칸은 안 만든다", cr[0].Name == "첫 시트" && cr[0].Rows.Count == 4 && cr[0].Rows[0].Count == 3 && cr[0].Rows[0][2] == "같은값" && cr[0].Rows[1].Count == 0 && cr[0].Rows[3][0] == "3.5" && cr[0].Rows[3][1] == "2026-03-02" && cr[0].Rows[3][2] == "가  나", cr[0].Rows.Count + "줄");

            // ── 자막 → 글자 ──
            string srt = Put("cap.srt", "1\n00:00:01,000 --> 00:00:03,500\n안녕하세요\n\n2\n00:00:04,000 --> 00:00:06,250\n두 번째 자막\n둘째 줄\n\n3\n00:00:06,250 --> 00:00:07,000\n둘째 줄\n\n");
            string srtText = ReadU(Out1(RunJob("format", "txt", new[] { srt }, null)));
            Check("SRT→글자: 시간은 빼고 되풀이는 한 번", srtText.Contains("안녕하세요") && !srtText.Contains("-->") && System.Text.RegularExpressions.Regex.Matches(srtText, "둘째 줄").Count == 1, srtText.Replace("\r\n", " / "));
            string vtt = Put("cap.vtt", "WEBVTT\n\n00:01.000 --> 00:03.500\n<v 진행자>첫 자막\n\n00:04.000 --> 00:06.000\n둘째 <c.x>자막</c>\n");
            string vttText = ReadU(Out1(RunJob("format", "txt", new[] { vtt }, null)));
            Check("VTT→글자: 표시 태그는 뺀다", vttText.Contains("첫 자막") && vttText.Contains("둘째 자막") && !vttText.Contains("<"), vttText.Replace("\r\n", " / "));

            // ── 묶기·풀기 ──
            string folder = Path.Combine(w, "묶음폴더");
            Directory.CreateDirectory(Path.Combine(folder, "안쪽 폴더"));
            Directory.CreateDirectory(Path.Combine(folder, "빈 폴더"));
            File.WriteAllText(Path.Combine(folder, "첫 파일.txt"), "첫 파일 내용", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(folder, "안쪽 폴더", "사진.jpg"), Enumerable.Range(0, 60000).Select(i => (byte)(i * 7)).ToArray());
            string longName = new string('가', 60) + ".txt";
            File.WriteAllText(Path.Combine(folder, "안쪽 폴더", longName), "긴 이름", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "Thumbs.db"), "x");
            var expect = Tree(folder);
            expect.Remove("Thumbs.db");
            var expanded = Formats.Expand(new[] { folder });
            Check("폴더를 놓으면 폴더 자리와 안의 파일", expanded[0] == folder + "\\" && expanded.Contains(Path.Combine(folder, "첫 파일.txt")) && Formats.KindOf(expanded[0]) == Kind.Folder, string.Join(" | ", expanded.Select(Util.ShortName)));
            Check("폴더 자리는 압축 칸과 AI 칸(폴더 정리)만 받는다", ActionOf("archive").Usable(expanded).Contains(expanded[0]) && !ActionOf("pdf").Usable(expanded).Contains(expanded[0]) && ActionOf("ai").Usable(expanded).Contains(expanded[0]), "");
            string zip = Out1(RunJob("archive", "zip", expanded, null));
            Check("폴더→ZIP", zip.EndsWith("묶음폴더.zip"), zip);
            var zipItems = new List<string>();
            bool utf8Flag = false;
            using (var fs = File.OpenRead(zip))
            {
                var central = ArchiveJobs.ReadCentral(fs);
                zipItems = central.Select(x => x.Name).ToList();
                utf8Flag = central.All(x => (x.Flags & 0x800) != 0);
            }
            Check("ZIP: 다른 프로그램도 한글 이름을 읽게 표시한다", utf8Flag, "");
            Check("ZIP: 폴더 이름이 맨 위, 빈 폴더 유지, Thumbs.db 뺌", zipItems.Contains("묶음폴더/안쪽 폴더/사진.jpg") && zipItems.Contains("묶음폴더/빈 폴더/") && !zipItems.Any(x => x.Contains("Thumbs")), string.Join(" | ", zipItems.Select(x => x.Length > 30 ? x.Substring(0, 30) : x)));
            var un1 = RunJob("archive", "unpack", new[] { zip }, null);
            string out1 = Out1(un1);
            Check("ZIP 풀기: 내용 그대로, 같은 이름 폴더가 겹치지 않게", out1 != "" && SameTree(expect, Tree(out1)) && Directory.Exists(Path.Combine(out1, "빈 폴더")), out1);
            string tar = Out1(RunJob("archive", "tar", expanded, null));
            var un2 = RunJob("archive", "unpack", new[] { tar }, null);
            Check("폴더→TAR→풀기: 긴 한글 이름까지", Out1(un2) != "" && SameTree(expect, Tree(Out1(un2))), Out1(un2));
            string one = Put("한개.txt", new string('글', 5000));
            string gz = Out1(RunJob("archive", "gz", new[] { one }, null));
            Check("GZIP: 파일 하나 압축", gz.EndsWith("한개.txt.gz") && new FileInfo(gz).Length < new FileInfo(one).Length / 3, gz);
            string ungz = Out1(RunJob("archive", "unpack", new[] { gz }, null));
            Check("GZIP 풀기", ungz != "" && ReadU(ungz) == ReadU(one), ungz);
            var twoGz = RunJob("archive", "gz", new[] { one, a }, null);
            Check("GZIP은 파일 하나만", twoGz.Outputs.Count == 0 && twoGz.Problems.Count == 1, "");
            string tgz = F2("묶음.tar.gz");
            using (var raw = File.Create(tgz))
            using (var g = new GZipStream(raw, CompressionLevel.Optimal))
            using (var t = File.OpenRead(tar)) t.CopyTo(g);
            string untgz = Out1(RunJob("archive", "unpack", new[] { tgz }, null));
            Check(".tar.gz 풀기", untgz != "" && SameTree(expect, Tree(untgz)), untgz);
            string mixed = Out1(RunJob("archive", "zip", new[] { one, a, F2("없는 파일.txt") }, null));
            Check("여러 파일→ZIP 이름은 '첫 외 N개'", Path.GetFileName(mixed) == "한개 외 1개.zip", Path.GetFileName(mixed));
            string badNames = F2("위험.zip");
            RawZip(badNames, new RawEntry("../밖으로.txt") { Data = Ascii8("x") }, new RawEntry("a/../../밖2.txt") { Data = Ascii8("x") }, new RawEntry("C:/절대.txt") { Data = Ascii8("x") }, new RawEntry("/루트.txt") { Data = Ascii8("x") },
                new RawEntry("CON.txt") { Data = Ascii8("x") }, new RawEntry("이름:흐름.txt") { Data = Ascii8("x") }, new RawEntry("끝점.") { Data = Ascii8("x") },
                new RawEntry("괜찮음.txt") { Data = Ascii8("ok") }, new RawEntry("겹침.txt") { Data = Ascii8("1") }, new RawEntry("겹침.TXT") { Data = Ascii8("2") },
                new RawEntry("링크") { Data = Ascii8("/etc/passwd"), Host = 3, External = 0xA1FF0000 });
            var bad = RunJob("archive", "unpack", new[] { badNames }, null);
            string badOut = Out1(bad);
            var badFiles = badOut == "" ? new string[0] : Directory.GetFiles(badOut, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray();
            Check("위험한 이름은 막는다: 밖으로·절대경로·장치 이름·흐름·링크·겹침", badFiles.Length == 2 && badFiles.Contains("괜찮음.txt") && badFiles.Contains("겹침.txt") && !File.Exists(Path.Combine(w, "밖으로.txt")) && !File.Exists(Path.Combine(Path.GetDirectoryName(w), "밖2.txt")) && bad.Notes.Any(n => n.Contains("풀지 않았어요")), string.Join(", ", badFiles) + " · " + string.Join(" / ", bad.Notes));
            Check("겹치면 먼저 나온 것을 남긴다", badOut != "" && ReadU(Path.Combine(badOut, "겹침.txt")) == "1", "");
            string onlyBad = F2("전부위험.zip");
            RawZip(onlyBad, new RawEntry("../a.txt") { Data = Ascii8("x") });
            var ob = RunJob("archive", "unpack", new[] { onlyBad }, null);
            Check("풀 것이 없으면 빈 폴더를 남기지 않는다", ob.Outputs.Count == 0 && ob.Problems.Count == 1 && !Directory.Exists(F2("전부위험")), string.Join(" / ", ob.Problems));
            string enc = F2("암호.zip");
            RawZip(enc, new RawEntry("a.txt") { Data = Ascii8("x"), Flags = 0x801 });
            var encJob = RunJob("archive", "unpack", new[] { enc }, null);
            Check("암호 걸린 ZIP은 알려 주고 안 푼다", encJob.Outputs.Count == 0 && encJob.Problems.Any(x => x.Contains("암호")), string.Join(" / ", encJob.Problems));
            string cp = F2("옛이름.zip");
            RawZip(cp, new RawEntry("x") { Name = Encoding.GetEncoding(949).GetBytes("한글이름.txt"), Flags = 0, Data = Ascii8("cp") }, new RawEntry("y") { Name = new UTF8Encoding(false).GetBytes("맥이름.txt".Normalize(NormalizationForm.FormD)), Flags = 0, Data = Ascii8("mac") }, new RawEntry("__MACOSX/._a") { Data = Ascii8("j") });
            string cpOut = Out1(RunJob("archive", "unpack", new[] { cp }, null));
            Check("옛 한국어 이름·맥 자모를 바로 읽고 맥 찌꺼기는 뺀다", cpOut != "" && File.Exists(Path.Combine(cpOut, "한글이름.txt")) && File.Exists(Path.Combine(cpOut, "맥이름.txt")) && !Directory.Exists(Path.Combine(cpOut, "__MACOSX")), cpOut == "" ? "" : string.Join(", ", Directory.GetFiles(cpOut).Select(Path.GetFileName)));
            // 내용이 든 부분을 망가뜨린다(압축된 것, 안 된 것 둘 다).
            string corrupt = F2("깨짐.zip");
            var goodBytes = File.ReadAllBytes(zip);
            int bigAt = 0;
            using (var fs = File.OpenRead(zip))
            {
                var big = ArchiveJobs.ReadCentral(fs).OrderByDescending(x => x.Packed).First();
                bigAt = (int)big.Offset + 30 + BitConverter.ToUInt16(goodBytes, (int)big.Offset + 26) + BitConverter.ToUInt16(goodBytes, (int)big.Offset + 28) + (int)(big.Packed / 2);
            }
            goodBytes[bigAt] ^= 0x55;
            goodBytes[bigAt + 1] ^= 0x55;
            File.WriteAllBytes(corrupt, goodBytes);
            string corrupt2 = F2("깨짐2.zip");
            RawZip(corrupt2, new RawEntry("a.txt") { Data = Ascii8("abcdefghij") });
            var cb = File.ReadAllBytes(corrupt2);
            cb[30 + 5 + 3] ^= 1;
            File.WriteAllBytes(corrupt2, cb);
            var co2 = RunJob("archive", "unpack", new[] { corrupt2 }, null);
            Check("압축 없이 담긴 내용이 망가져도 알아챈다", co2.Outputs.Count == 0 && co2.Problems.Count == 1 && !Directory.Exists(F2("깨짐2")), string.Join(" / ", co2.Problems));
            var co = RunJob("archive", "unpack", new[] { corrupt }, null);
            Check("손상된 ZIP은 멈추고 조각을 안 남긴다", co.Outputs.Count == 0 && co.Problems.Count == 1 && !Directory.Exists(F2("깨짐")), string.Join(" / ", co.Problems));
            File.WriteAllBytes(F2("가짜.zip"), new byte[100]);
            var fake = RunJob("archive", "unpack", new[] { F2("가짜.zip") }, null);
            Check("ZIP이 아닌 파일은 알려 준다", fake.Outputs.Count == 0 && fake.Problems.Count == 1, string.Join(" / ", fake.Problems));
            string tarT = F2("링크.tar");
            using (var o = File.Create(tarT))
            {
                TarAdd(o, "멀쩡한.txt".Length > 0 ? "ok.txt" : "", '0', Ascii8("ok"));
                TarAdd(o, "../out.txt", '0', Ascii8("x"));
                o.Write(TarBlock("sym", '2', 0), 0, 512);
                byte[] longN = new UTF8Encoding(false).GetBytes("아주/긴/경로의/이름.txt\0");
                TarAdd(o, "././@LongLink", 'L', longN);
                TarAdd(o, "cut", '0', Ascii8("gnu"));
                o.Write(new byte[1024], 0, 1024);
            }
            var tl = RunJob("archive", "unpack", new[] { tarT }, null);
            string tlOut = Out1(tl);
            Check("TAR: 링크·밖으로는 막고 긴 이름은 읽는다", tlOut != "" && File.Exists(Path.Combine(tlOut, "ok.txt")) && File.Exists(Path.Combine(tlOut, "아주", "긴", "경로의", "이름.txt")) && !File.Exists(F2("out.txt")) && Directory.GetFiles(tlOut, "*", SearchOption.AllDirectories).Length == 2, string.Join(" / ", tl.Notes));

            // ── SVG ──
            string svg = Put("g.svg", "<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 100 100\"><defs><linearGradient id=\"lg\" x1=\"0\" x2=\"1\" y1=\"0\" y2=\"0\"><stop offset=\"0\" stop-color=\"#00ff00\"/><stop offset=\"1\" stop-color=\"#00ff00\"/></linearGradient><rect id=\"r\" width=\"10\" height=\"10\" fill=\"#123456\"/><clipPath id=\"cp\"><rect x=\"0\" y=\"80\" width=\"10\" height=\"10\"/></clipPath></defs><style>.k{fill:#ff00ff}</style><rect width=\"50\" height=\"50\" fill=\"red\"/><circle cx=\"75\" cy=\"75\" r=\"20\" style=\"fill:rgb(0,0,255)\"/><g transform=\"translate(60,10)\"><rect width=\"20\" height=\"20\" fill=\"url(#lg)\"/></g><use xlink:href=\"#r\" x=\"0\" y=\"60\"/><path class=\"k\" d=\"M20,60h10v10h-10z\"/><path d=\"M40 60a5 5 0 015 5 5 5 0 01-10 0z\" fill=\"#00ffff\"/><rect x=\"0\" y=\"80\" width=\"30\" height=\"20\" fill=\"#ffa500\" clip-path=\"url(#cp)\"/><text x=\"50\" y=\"98\" font-size=\"6\" text-anchor=\"middle\" fill=\"#333\">글자 Text</text><script>alert(1)</script><image xlink:href=\"https://example.invalid/a.png\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></svg>");
            string svgPng = Out1(RunJob("format", "png", new[] { svg }, null));
            Check("SVG→PNG", svgPng != "" && File.Exists(svgPng), "");
            if (svgPng != "")
            {
                var bmp = Bgra.From(Imaging.Load(svgPng));
                Func<double, double, int> at = delegate (double x, double y)
                {
                    int px = (int)(x * bmp.W / 100.0), py = (int)(y * bmp.H / 100.0);
                    int p = (py * bmp.W + px) * 4;
                    return (bmp.P[p + 3] << 24) | (bmp.P[p + 2] << 16) | (bmp.P[p + 1] << 8) | bmp.P[p];
                };
                Check("SVG 크기: 긴 변 2048", bmp.W == 2048 && bmp.H == 2048, bmp.W + "×" + bmp.H);
                Check("SVG 네모·동그라미·투명 바탕", at(25, 25) == unchecked((int)0xFFFF0000) && at(75, 75) == unchecked((int)0xFF0000FF) && (at(95, 5) >> 24) == 0, at(25, 25).ToString("X8") + " " + at(75, 75).ToString("X8") + " " + at(95, 5).ToString("X8"));
                Check("SVG 옮기기·그라데이션", at(70, 20) == unchecked((int)0xFF00FF00), at(70, 20).ToString("X8"));
                Check("SVG use·스타일시트·호", at(5, 65) == unchecked((int)0xFF123456) && at(25, 65) == unchecked((int)0xFFFF00FF) && at(40, 66) == unchecked((int)0xFF00FFFF), at(5, 65).ToString("X8") + " " + at(25, 65).ToString("X8") + " " + at(40, 66).ToString("X8"));
                Check("SVG 자르기", at(5, 85) == unchecked((int)0xFFFFA500) && (at(20, 85) >> 24) == 0, at(5, 85).ToString("X8") + " " + at(20, 85).ToString("X8"));
            }
            string svgJpg = Out1(RunJob("format", "jpg", new[] { svg }, null));
            Check("SVG→JPG·PDF·워드", svgJpg != "" && PageCount(Out1(RunJob("pdf", "go", new[] { svg }, null))) == 1 && Out1(RunJob("format", "docx", new[] { svg }, null)) != "", "");
            string pxe = Put("entity.svg", "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY c \"#ff0000\"><!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"&c;\"/><text>&x;</text></svg>");
            var pxj = RunJob("format", "png", new[] { pxe }, null);
            Check("SVG 문서 안 약어는 읽고 바깥 파일은 안 읽는다", pxj.Outputs.Count == 1 || pxj.Problems.Count == 1, Out1(pxj));
            var lol = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaaaaaaaaa\">");
            for (int i = 1; i < 10; i++) lol.Append("<!ENTITY a").Append(i).Append(" \"").Append(string.Concat(Enumerable.Repeat("&a" + (i == 1 ? "" : (i - 1).ToString()) + ";", 10))).Append("\">");
            lol.Append("]><svg xmlns=\"http://www.w3.org/2000/svg\"><text>&a9;</text></svg>");
            var lj = RunJob("format", "png", new[] { Put("bomb.svg", lol.ToString()) }, null);
            Check("SVG 부풀리기는 멈춘다", lj.Outputs.Count == 0 && lj.Problems.Count == 1, string.Join(" / ", lj.Problems));
            Check("SVG는 줄이기·글자 뽑기 칸에 안 나온다", ActionOf("shrink").Usable(new[] { svg }).Count == 0 && ActionOf("text").Usable(new[] { svg }).Count == 0 && ActionOf("format").Usable(new[] { svg }).Count == 1, "");

            // ── 쪽 고르기 다른 형식 ──
            string numbered = F2("쪽번호.pdf");
            var pagesBlocks = new List<DocBlock>();
            for (int i = 1; i <= 5; i++)
            {
                if (i > 1) pagesBlocks.Add(DocBlock.Break());
                pagesBlocks.Add(DocBlock.Para("쪽번호" + i));
            }
            TextPdf.Write(numbered, pagesBlocks, "쪽번호", new Job(ActionOf("pdf"), new ActionOption("go", "", ""), new List<string>()));
            var rev = RunJob("pdftool", "pick", new[] { numbered }, null, "pages=5-1", "as=pdf");
            string revWords = PdfWords(Out1(rev));
            Check("쪽 고르기: 거꾸로 PDF", Out1(rev) != "" && PageCount(Out1(rev)) == 5 && revWords.IndexOf("쪽번호5", StringComparison.Ordinal) >= 0 && revWords.IndexOf("쪽번호5", StringComparison.Ordinal) < revWords.IndexOf("쪽번호1", StringComparison.Ordinal), revWords.Replace("\n", " / "));
            var pickMd = RunJob("pdftool", "pick", new[] { numbered }, null, "pages=2", "as=txt");
            Check("쪽 고르기→글자: 임시 폴더가 아니라 원본 옆에", Path.GetDirectoryName(Out1(pickMd)) == w && ReadU(Out1(pickMd)).Contains("쪽번호2") && !ReadU(Out1(pickMd)).Contains("쪽번호3"), Out1(pickMd));
            if (File.Exists(P("a6.pdf")))
            {
                string src = F2("a6.pdf");
                File.Copy(P("a6.pdf"), src, true);
                var ppng = RunJob("pdftool", "pick", new[] { src }, null, "pages=3,1", "as=png");
                Check("쪽 고르기→PNG: 고른 쪽만 폴더에", Out1(ppng) != "" && Directory.Exists(Out1(ppng)) && Directory.GetFiles(Out1(ppng)).Length == 2 && Path.GetDirectoryName(Out1(ppng)) == w, Out1(ppng));
                var pjpg = RunJob("pdftool", "pick", new[] { src }, null, "pages=2", "as=jpg");
                Check("쪽 고르기→JPG 한 쪽", Out1(pjpg).EndsWith(".jpg"), Out1(pjpg));
                // 윈도우 PDF 엔진은 화면 배율만큼 크게 그린다(125% 화면이면 1.25배). 파일로 남기는 그림은 화면과 상관없이 200dpi여야 한다.
                if (Out1(pjpg) != "")
                {
                    var shot = Imaging.Decoder(File.ReadAllBytes(Out1(pjpg))).Frames[0];
                    var page2 = PdfText.Read(src, null).Pages[1];
                    double ew = page2.Width * 200 / 72.0, eh = page2.Height * 200 / 72.0;
                    bool exact = Math.Abs(shot.PixelWidth - ew) <= Math.Max(3, ew * 0.005) && Math.Abs(shot.PixelHeight - eh) <= Math.Max(3, eh * 0.005);
                    Check("PDF→그림: 화면 배율과 상관없이 200dpi", exact, shot.PixelWidth + "×" + shot.PixelHeight + ", 바라는 크기 " + Math.Round(ew) + "×" + Math.Round(eh) + ", 화면 배율 " + MenuWindow.DpiScale().ToString("0.##"));
                }
                var ptxt = RunJob("pdftool", "pick", new[] { src }, null, "pages=3", "as=txt");
                string ptext = ReadU(Out1(ptxt));
                Check("쪽 고르기→글자: 그 쪽 글자만", ptext.Contains("3쪽") && !ptext.Contains("1쪽"), ptext.Replace("\n", " / "));
                var pppt = RunJob("pdftool", "pick", new[] { src }, null, "pages=1-2", "as=pptx");
                Check("쪽 고르기→PPT", Out1(pppt).EndsWith(".pptx") && OoxmlRead.PptxText(Out1(pppt)).Length > 0, Out1(pppt));
            }

            // ── 사진 정보 보고서 ──
            if (File.Exists(P("scene.jpg")))
            {
                string sc = F2("scene.jpg");
                File.Copy(P("scene.jpg"), sc, true);
                string rep = Out1(RunJob("photo", "report", new[] { sc }, null));
                string rt = ReadU(rep);
                Check("사진 정보 보고서", rep.EndsWith("_사진정보.txt") && rt.Contains("scene.jpg") && rt.Split('\n').Length > 3, rt.Length + "자");
            }

            // ── 고를 거리 ──
            Func<string, string> opts = delegate (string name)
            {
                return string.Join(",", ActionOf("format").OptionsFor(new[] { F2(name) }).Select(o => o.Id));
            };
            Check("형식 칸: 전자책", opts("a.epub") == "docx,md,html,txt", opts("a.epub"));
            // 의미 없는 변환은 고를 거리에 없다(2026-11-13): PDF → 마크다운·HTML·EPUB(글자만 남고 표가 뭉개짐),
            // 표 → 글자 파일(TSV와 똑같은 파일), TSV → 글자 파일(이름만 바뀜), 글자 파일 → 마크다운·HTML(글자 그대로·문단 틀만).
            Check("형식 칸: PDF", opts("a.pdf") == "docx,xlsx,pptx,pptximg", opts("a.pdf"));
            Check("형식 칸: 워드", opts("a.docx") == "pptx,md,html,epub,txt", opts("a.docx"));
            Check("형식 칸: 표", opts("a.csv") == "xlsx,tsv,ods" && opts("a.xlsx") == "csv,tsv,ods" && opts("a.ods") == "xlsx,csv,tsv" && opts("a.tsv") == "xlsx,csv,ods", opts("a.csv") + " / " + opts("a.xlsx") + " / " + opts("a.ods") + " / " + opts("a.tsv"));
            Check("형식 칸: 자막", opts("a.srt") == "vtt,txt" && opts("a.vtt") == "srt,txt", opts("a.srt"));
            Check("형식 칸: 글자·마크다운·HTML", opts("a.txt") == "docx,epub" && opts("a.md") == "docx,html,epub,txt" && opts("a.html") == "docx,md,epub,txt", opts("a.txt"));
            // 일부만 옮겨지는 변환은 고를 때 작은 줄로 미리 알린다
            Func<string[], string, string> sub = delegate (string[] names, string id)
            {
                var o = ActionOf("format").OptionsFor(names.Select(F2).ToList()).FirstOrDefault(x => x.Id == id);
                return o == null ? "(없음)" : o.Sub;
            };
            Check("형식 칸 작은 줄: 마크다운은 사진이 빠진다고", sub(new[] { "a.docx" }, "md") == "사진은 빠져요" && sub(new[] { "a.hwp" }, "md") == "사진은 빠져요" && sub(new[] { "a.html" }, "md") == "사진은 빠져요", sub(new[] { "a.docx" }, "md"));
            Check("형식 칸 작은 줄: 시트가 여럿일 수 있으면 첫 시트만", sub(new[] { "a.xlsx" }, "csv") == "첫 시트만" && sub(new[] { "a.ods" }, "tsv") == "첫 시트만" && sub(new[] { "a.tsv", "b.xlsx" }, "csv") == "첫 시트만"
                && sub(new[] { "a.xls" }, "csv") == "첫 시트만" && sub(new[] { "a.cell" }, "tsv") == "첫 시트만", sub(new[] { "a.xlsx" }, "csv") + " / " + sub(new[] { "a.tsv", "b.xlsx" }, "csv") + " / " + sub(new[] { "a.xls" }, "csv"));
            Check("형식 칸 작은 줄: 시트가 하나뿐인 CSV·TSV는 그대로", sub(new[] { "a.csv" }, "tsv") == "탭으로 나눈 표" && sub(new[] { "a.tsv" }, "csv") == "쉼표로 나눈 표", sub(new[] { "a.csv" }, "tsv"));
            Check("형식 칸: PDF와 워드를 같이 놓으면 워드 몫은 남는다", ActionOf("format").OptionsFor(new[] { F2("a.pdf"), F2("b.docx") }).Any(o => o.Id == "html"), "");
            Func<string[], string> packs = delegate (string[] names)
            {
                return string.Join(",", ActionOf("archive").OptionsFor(names.Select(F2).ToList()).Select(o => o.Id));
            };
            Check("압축 칸 고를 거리", packs(new[] { "a.txt" }) == "zip,tar,gz" && packs(new[] { "a.zip" }) == "unpack,zip,tar" && packs(new[] { "a.txt", "b.txt" }) == "zip,tar", packs(new[] { "a.zip" }));
        }

        static ZipArchive ZipFile2(string path)
        {
            return new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
        }
    }
}
