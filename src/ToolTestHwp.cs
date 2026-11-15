// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki, moonsune.zip - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 도구 시험 중 워드·PDF·글자 파일 → 한글(HWP·HWPX). 재료 없이 지어낸 문서를 그 자리에서 만들고 한글(한컴오피스)로 바꾼다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace Bakkum
{
    static partial class ToolTest
    {
        static void ToHwpTests()
        {
            Cli.Print("[워드·PDF·글자 파일 → 한글]");
            Cli.Print("    한글 " + Has(Hangul.Installed));
            string w = P("tohwp");
            if (Directory.Exists(w)) Directory.Delete(w, true);
            Directory.CreateDirectory(w);
            var fmt = ActionOf("format");

            // 1. 메뉴(파일이 없어도 이름으로만 정한다)
            string docx = Path.Combine(w, "현장체험학습안내.docx");
            var wordIds = fmt.OptionsFor(new[] { docx }).Select(o => o.Id).ToList();
            Check("메뉴: 워드 → 맨 앞에 '한글로'·'HWPX'", wordIds.Count >= 2 && wordIds[0] == "hwp" && wordIds[1] == "hwpx", string.Join(",", wordIds));
            var toHwp = fmt.OptionsFor(new[] { docx }).First(o => o.Id == "hwp");
            Check("메뉴: 이름 '한글로'", toHwp.Label == "한글로" && toHwp.Sub == "한글 프로그램 필요", toHwp.Label + " / " + toHwp.Sub);
            foreach (var ext in new[] { ".doc", ".rtf", ".odt" })
            {
                var ids = fmt.OptionsFor(new[] { Path.Combine(w, "문서" + ext) }).Select(o => o.Id).ToList();
                Check("메뉴: " + ext + "에도 한글로·HWPX", ids.Contains("hwp") && ids.Contains("hwpx"), string.Join(",", ids));
            }
            var pdfIds = fmt.OptionsFor(new[] { Path.Combine(w, "안내문.pdf") }).Select(o => o.Id).ToList();
            Check("메뉴: PDF → '워드로' 다음에 한글로·HWPX", pdfIds.IndexOf("docx") == 0 && pdfIds.IndexOf("hwp") == 1 && pdfIds.IndexOf("hwpx") == 2, string.Join(",", pdfIds));
            var oldIds = fmt.OptionsFor(new[] { Path.Combine(w, "예전.hwp") }).Select(o => o.Id).ToList();
            Check("메뉴: .hwp는 그대로(HWPX만)", !oldIds.Contains("hwp") && oldIds.Contains("hwpx"), string.Join(",", oldIds));
            var mix = fmt.OptionsFor(new[] { Path.Combine(w, "새.hwpx"), docx });
            var mixHwp = mix.FirstOrDefault(o => o.Id == "hwp");
            Check("메뉴: .hwpx+워드 → HWP·HWPX 한 칸씩", mix.Count(o => o.Id == "hwp") == 1 && mix.Count(o => o.Id == "hwpx") == 1 && mixHwp != null && mixHwp.Label == "HWP", string.Join(",", mix.Select(o => o.Id + ":" + o.Label)));
            var txtOpts = fmt.OptionsFor(new[] { Path.Combine(w, "알림장.txt") });
            var txtHwpOpt = txtOpts.FirstOrDefault(o => o.Id == "hwp");
            Check("메뉴: 글자 파일 → '워드로' 다음에 한글로·HWPX", string.Join(",", txtOpts.Select(o => o.Id)) == "docx,hwp,hwpx,epub" && txtHwpOpt != null && txtHwpOpt.Label == "한글로", string.Join(",", txtOpts.Select(o => o.Id + ":" + o.Label)));
            foreach (var ext in new[] { ".md", ".html", ".srt", ".tsv" })
            {
                var ids = fmt.OptionsFor(new[] { Path.Combine(w, "글" + ext) }).Select(o => o.Id).ToList();
                Check("메뉴: " + ext + "에는 한글로 없음", !ids.Contains("hwp") && !ids.Contains("hwpx"), string.Join(",", ids));
            }

            // 2. 표 여백 고치기(한글 없이): 표·칸만 고치고, 그림의 여백과 나머지 글은 한 글자도 안 바꾼다
            string m = DocConvert.TableMarginTopBottom.ToString();
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><hs:sec xmlns:hs=\"s\" xmlns:hp=\"p\"><hp:p><hp:run><hp:tbl id=\"1\" rowCnt=\"1\">" +
                "<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/><hp:inMargin left=\"540\" right=\"540\" top=\"0\" bottom=\"0\"/><hp:tr>" +
                "<hp:tc hasMargin=\"1\"><hp:subList><hp:p><hp:run><hp:t>a &gt; b</hp:t></hp:run></hp:p></hp:subList><hp:cellMargin left=\"510\" right=\"510\" top=\"0\" bottom=\"0\"/></hp:tc>" +
                "</hp:tr></hp:tbl><hp:pic href=\"x>y\"><hp:inMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/></hp:pic><!-- <hp:tbl> --></hp:run></hp:p></hs:sec>";
            int changed = 0;
            string fixedXml = DocConvert.FixMarginsXml(xml, ref changed);
            string expect = xml.Replace("<hp:inMargin left=\"540\" right=\"540\" top=\"0\" bottom=\"0\"/>", "<hp:inMargin left=\"540\" right=\"540\" top=\"" + m + "\" bottom=\"" + m + "\"/>")
                .Replace("<hp:cellMargin left=\"510\" right=\"510\" top=\"0\" bottom=\"0\"/>", "<hp:cellMargin left=\"510\" right=\"510\" top=\"" + m + "\" bottom=\"" + m + "\"/>");
            Check("표 여백: 표·칸만 고치고 그림·글은 그대로", changed == 2 && fixedXml == expect, changed + "곳");

            // 2-1. 글자 파일 다듬기(한글 없이): 줄 바꿈은 CRLF로, 탭·빈칸·빈 줄은 그대로, 제어 글자와 끝 줄 바꿈은 뺀다
            string tidy = DocConvert.PlainForHangul("\uFEFF가\n나\r다\r\n\r\n\t라  \f마\0바\u0007\u2028사\n\n");
            Check("글자 다듬기: 줄 바꿈·탭·빈칸·제어 글자", tidy == "가\r\n나\r\n다\r\n\r\n\t라  \r\n마바\r\n사", ShowBreaks(tidy));
            Check("글자 다듬기: 줄 바꿈만 있으면 빈 글", DocConvert.PlainForHangul("\r\n\n\r") == "", ShowBreaks(DocConvert.PlainForHangul("\r\n\n\r")));

            // 2-2. 빈 글자 파일·너무 큰 글자 파일은 한글을 켜기 전에 알린다
            string emptyTxt = Path.Combine(w, "빈글.txt");
            File.WriteAllBytes(emptyTxt, new byte[0]);
            string spaceTxt = Path.Combine(w, "빈칸만.txt");
            File.WriteAllText(spaceTxt, "  \r\n\t\n", new UTF8Encoding(true));
            var jEmpty = RunJob("format", "hwp", new[] { emptyTxt, spaceTxt }, null);
            Check("빈 글자 파일·빈칸만 든 글자 파일 → '빈 파일이에요'", jEmpty.Outputs.Count == 0 && jEmpty.Problems.Count == 2 && jEmpty.Problems.All(x => x.Contains("빈 파일")), string.Join(" / ", jEmpty.Problems));
            string manyTxt = Path.Combine(w, "글자많음.txt");
            File.WriteAllText(manyTxt, new string('가', DocConvert.MaxHangulChars + 1), new UTF8Encoding(false));
            string hugeTxt = Path.Combine(w, "아주큼.txt");
            using (var fs = new FileStream(hugeTxt, FileMode.Create, FileAccess.Write)) fs.SetLength(3L * DocConvert.MaxHangulChars + 4);
            var sizeWatch = System.Diagnostics.Stopwatch.StartNew();
            var jBig = RunJob("format", "hwpx", new[] { manyTxt, hugeTxt }, null);
            Check("너무 큰 글자 파일 → '글자가 너무 많아요'(한글을 켜지 않음)", jBig.Outputs.Count == 0 && jBig.Problems.Count == 2 && jBig.Problems.All(x => x.Contains("글자가 너무 많아요") && x.Contains("300만 자")) && sizeWatch.Elapsed.TotalSeconds < 10,
                string.Join(" / ", jBig.Problems) + ", " + sizeWatch.Elapsed.TotalSeconds.ToString("0.0") + "초");
            File.Delete(manyTxt);
            File.Delete(hugeTxt);

            // 2-3. 시험 글자 파일(지어낸 내용). 인코딩·줄 바꿈이 저마다 다르다
            const string TxtBody = "2026학년도 2학기 학부모 상담 주간 안내\r\n\r\n상담 기간: 11월 24일(월) ~ 11월 28일(금)\r\n\t- 방법: 방문 또는 전화\r\n  ※ 원하는 시간을 알림장에 적어 주세요.";
            string txtU8 = Path.Combine(w, "상담안내_UTF8.txt");
            File.WriteAllText(txtU8, TxtBody.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
            string txtU8Bom = Path.Combine(w, "상담안내_UTF8BOM.txt");
            File.WriteAllText(txtU8Bom, TxtBody + "\r\n", new UTF8Encoding(true));
            string txtAnsi = Path.Combine(w, "상담안내_ANSI.txt");
            File.WriteAllText(txtAnsi, TxtBody, Encoding.GetEncoding(949));
            string txtU16 = Path.Combine(w, "상담안내_UTF16.txt");
            File.WriteAllText(txtU16, TxtBody, Encoding.Unicode);

            // 3. 시험 문서(지어낸 내용)
            var blocks = MarkdownDoc.Parse("# 2026학년도 현장체험학습 안내\n\n학부모님께 알려 드립니다. ○○초등학교 4학년은 아래와 같이 현장체험학습을 갑니다.\n\n" +
                "| 구분 | 내용 | 비고 |\n|---|---|---|\n| 날짜 | 11월 20일(금) | 비가 오면 미룸 |\n| 장소 | □□ 과학관 | 버스로 이동 |\n\n- 준비물: 도시락, 물병\n- 돌아오는 시각: 15시 30분\n");
            // 바꿈이 만든 워드 파일은 표 칸 위·아래 여백이 있다(0.7mm). MS 워드가 만든 파일처럼 0으로 바꿔 둔다.
            string padded = Path.Combine(w, "여백있는표.docx");
            DocxWriter.Write(padded, blocks);
            DocxWriter.Write(docx, blocks);
            WordDefaultTableMargins(docx);
            AddMergedRow(docx);
            if (!Hangul.Installed)
            {
                var none = RunJob("format", "hwp", new[] { docx }, null);
                Check("한글이 없으면 알기 쉽게 알린다", none.Outputs.Count == 0 && none.Problems.Any(x => x.Contains("한글")), string.Join(" / ", none.Problems));
                var noneTxt = RunJob("format", "hwpx", new[] { txtU8 }, null);
                Check("한글이 없으면 글자 파일도 알기 쉽게 알린다", noneTxt.Outputs.Count == 0 && noneTxt.Problems.Any(x => x.Contains("한글")), string.Join(" / ", noneTxt.Problems));
                Cli.Print("    한글이 없어 나머지는 시험하지 못함");
                return;
            }
            var hwordBefore = Util.PidsOf("Hword");
            var hwpBefore = Util.PidsOf("Hwp");

            // 4. 워드 → HWP·HWPX
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var j = RunJob("format", "hwp", new[] { docx }, null);
            string hwp = j.Outputs.FirstOrDefault();
            Check("워드 → HWP", hwp != null && Formats.Ext(hwp) == ".hwp" && j.Problems.Count == 0 && j.Notes.Count == 0, (hwp == null ? string.Join(" / ", j.Problems) : Path.GetFileName(hwp)) + ", " + sw.Elapsed.TotalSeconds.ToString("0.0") + "초(한글 켜기 포함)");
            j = RunJob("format", "hwpx", new[] { docx }, null);
            string hwpx = j.Outputs.FirstOrDefault();
            string text = HwpxTextOf(hwpx);
            Check("워드 → HWPX: 제목·문단·표·목록 글자", text.Contains("2026학년도 현장체험학습 안내") && text.Contains("○○초등학교") && text.Contains("□□ 과학관") && text.Contains("도시락"), text.Replace("\n", " / "));
            int merged = hwpx == null ? 0 : MergedCells(hwpx);
            Check("워드 → HWPX: 가로로 합친 칸이 그대로", merged == 1 && text.Contains("돌아올 때도 같은 버스를 타요"), merged + "칸");
            var margins = hwpx == null ? new List<int>() : TableMargins(hwpx);
            Check("워드 → HWPX: 표 칸 위·아래 여백 0 → " + m, margins.Count > 0 && margins.All(x => x == DocConvert.TableMarginTopBottom), string.Join(",", margins));
            j = RunJob("format", "hwpx", new[] { padded }, null);
            var kept = j.Outputs.Count == 1 ? TableMargins(j.Outputs[0]) : new List<int>();
            Check("여백이 있는 표는 그대로", kept.Count > 0 && kept.All(x => x > 0 && x != DocConvert.TableMarginTopBottom), string.Join(",", kept));
            if (hwp != null)
            {
                using (var h = new Hangul(j))
                {
                    h.Open(hwp);
                    string t = h.Text();
                    string back = Path.Combine(w, "HWP다시보기.hwpx");
                    h.SaveAs(back, "HWPX");
                    h.CloseDoc();
                    Check("워드 → HWP: 한글로 다시 열림", t.Contains("현장체험학습 안내") && t.Contains("□□ 과학관"), t.Length + "자");
                    var hm = TableMargins(back);
                    Check("워드 → HWP: 고친 표 여백이 HWP에도 남음", hm.Count > 0 && hm.All(x => x == DocConvert.TableMarginTopBottom), string.Join(",", hm));
                }
            }

            // 5. 그림만 든 워드, 빈 워드, 암호 걸린 워드(zip이 아님)
            string pic = Path.Combine(w, "그림.png");
            DrawPage(pic, 400, 300, new[] { "그림" });
            string picDocx = Path.Combine(w, "그림만.docx");
            DocxWriter.Write(picDocx, new List<DocBlock> { new DocBlock { Kind = BlockKind.Picture, Image = File.ReadAllBytes(pic), ImageType = "png", W = 400, H = 300 } });
            j = RunJob("format", "hwpx", new[] { picDocx }, null);
            Check("그림만 든 워드 → HWPX(그림이 들어감)", j.Outputs.Count == 1 && j.Problems.Count == 0 && HasBinData(j.Outputs[0]), string.Join(" / ", j.Problems));
            string blank = Path.Combine(w, "빈문서.docx");
            DocxWriter.Write(blank, new List<DocBlock>());
            j = RunJob("format", "hwp", new[] { blank }, null);
            Check("빈 워드 → '빈 파일이에요'", j.Outputs.Count == 0 && j.Problems.Any(x => x.Contains("빈 파일")), string.Join(" / ", j.Problems));
            string locked = Path.Combine(w, "암호.docx");
            File.WriteAllBytes(locked, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.Concat(new byte[1016]).ToArray());
            j = RunJob("format", "hwp", new[] { locked }, null);
            Check("암호 걸린 워드 → 알기 쉬운 안내", j.Outputs.Count == 0 && j.Problems.Any(x => x.Contains("암호")), string.Join(" / ", j.Problems));

            // 6. RTF, 이름만 .doc인 RTF
            string rtf = Path.Combine(w, "알림장.rtf");
            File.WriteAllText(rtf, Rtf("알림장", "내일은 체육복을 입고 옵니다.", "줄넘기를 챙겨 주세요."), Encoding.ASCII);
            j = RunJob("format", "hwpx", new[] { rtf }, null);
            text = HwpxTextOf(j.Outputs.FirstOrDefault());
            Check("RTF → HWPX", text.Contains("체육복") && text.Contains("줄넘기"), j.Problems.Count > 0 ? string.Join(" / ", j.Problems) : text.Replace("\n", " / "));
            string fakeDoc = Path.Combine(w, "알림장(학교 시스템).doc");
            File.Copy(rtf, fakeDoc);
            j = RunJob("format", "hwpx", new[] { fakeDoc }, null);
            Check("이름만 .doc인 RTF → HWPX", HwpxTextOf(j.Outputs.FirstOrDefault()).Contains("체육복"), string.Join(" / ", j.Problems));

            // 7. ODT: 보통 꼴은 한글이 읽고, 한글이 못 읽는 간단한 꼴은 바꿈이 읽어 넣는다(창이 뜨면 여기서 멈춘다)
            string odt = Path.Combine(w, "오픈문서.odt");
            WriteOdt(odt, true);
            j = RunJob("format", "hwpx", new[] { odt }, null);
            text = HwpxTextOf(j.Outputs.FirstOrDefault());
            Check("ODT → HWPX", text.Contains("오픈 문서 제목") && text.Contains("가칸") && text.Contains("표 아래 문단"), j.Problems.Count > 0 ? string.Join(" / ", j.Problems) : text.Replace("\n", " / "));
            string bare = Path.Combine(w, "오픈문서_간단.odt");
            WriteOdt(bare, false);
            sw.Restart();
            j = RunJob("format", "hwp", new[] { bare }, null);
            Check("간단한 ODT → HWP(바꿈이 읽어 넣음)", j.Outputs.Count == 1 && j.Problems.Count == 0 && sw.Elapsed.TotalSeconds < 60, string.Join(" / ", j.Problems) + " " + sw.Elapsed.TotalSeconds.ToString("0.0") + "초");

            // 8. PDF → HWP·HWPX(글자가 든 PDF)
            string pdf = Path.Combine(w, "안내문.pdf");
            TextPdf.Write(pdf, blocks, "안내문", j);
            sw.Restart();
            j = RunJob("format", "hwpx", new[] { pdf }, null);
            text = HwpxTextOf(j.Outputs.FirstOrDefault());
            Check("PDF → HWPX: 글자", text.Contains("현장체험학습") && text.Contains("과학관") && j.Notes.Count == 0, (j.Problems.Count + j.Notes.Count > 0 ? string.Join(" / ", j.Problems.Concat(j.Notes)) : text.Length + "자") + ", " + sw.Elapsed.TotalSeconds.ToString("0.0") + "초");
            j = RunJob("format", "hwp", new[] { pdf }, null);
            Check("PDF → HWP", j.Outputs.Count == 1 && j.Problems.Count == 0, string.Join(" / ", j.Problems));

            // 9. 스캔한 PDF: 한글은 쪽 그림만 넣으므로 글자 인식으로 글자를 옮긴다(이 PC에 글자 인식이 없으면 쪽 그림 그대로)
            string scanPng = Path.Combine(w, "스캔.png");
            DrawPage(scanPng, 1240, 1754, new[] { "현장체험학습 안내", "날짜: 11월 20일 금요일", "준비물: 도시락과 물병" });
            string scanPdf = ImageToPdf(scanPng, Path.Combine(w, "스캔본.pdf"));
            j = RunJob("format", "hwpx", new[] { scanPdf }, null);
            text = HwpxTextOf(j.Outputs.FirstOrDefault());
            bool ocr = j.Notes.Any(x => x.Contains("글자 인식으로"));
            // 글자 인식은 한두 글자 틀릴 수 있다(2026-11-15: 체험 → 제험). 또렷한 낱말만 본다.
            Check("스캔 PDF → HWPX: " + (ocr ? "글자 인식으로 글자" : "쪽 그림 그대로"), j.Outputs.Count == 1 && j.Problems.Count == 0 && (ocr ? text.Contains("11월 20일") && text.Contains("도시락") : j.Notes.Any(x => x.Contains("쪽 그림"))), string.Join(" / ", j.Notes) + " / " + text.Replace("\n", " "));
            string blankPng = Path.Combine(w, "빈쪽.png");
            DrawPage(blankPng, 1240, 1754, new string[0]);
            string blankPdf = ImageToPdf(blankPng, Path.Combine(w, "빈쪽.pdf"));
            j = RunJob("format", "hwp", new[] { blankPdf }, null);
            Check("글자 없는 그림 PDF → 쪽 그림 그대로, 알림", j.Outputs.Count == 1 && j.Notes.Any(x => x.Contains("쪽 그림")), string.Join(" / ", j.Problems.Concat(j.Notes)));

            // 9-1. 글자 파일 → HWPX·HWP. 인코딩(UTF-8·한국어 윈도우 인코딩·UTF-16)과 줄 바꿈(LF·CRLF)이 달라도
            //      한글로 다시 열면 글자·빈 줄·탭·앞 빈칸이 그대로이고, 끝에 빈 문단이 더 생기지 않는다.
            sw.Restart();
            j = RunJob("format", "hwpx", new[] { txtU8, txtAnsi, txtU16 }, null);
            var txtOuts = j.Outputs.ToList();
            Check("글자 파일 → HWPX: UTF-8(LF)·CP949·UTF-16 세 개", txtOuts.Count == 3 && j.Problems.Count == 0 && j.Notes.Count == 0, string.Join(" / ", j.Problems.Concat(j.Notes)) + " " + sw.Elapsed.TotalSeconds.ToString("0.0") + "초");
            j = RunJob("format", "hwp", new[] { txtU8Bom }, null);
            string txtHwp = j.Outputs.FirstOrDefault();
            Check("글자 파일 → HWP: UTF-8 BOM(CRLF)", txtHwp != null && Formats.Ext(txtHwp) == ".hwp" && j.Problems.Count == 0, string.Join(" / ", j.Problems));
            if (txtHwp != null) txtOuts.Add(txtHwp);
            using (var h = new Hangul(j))
            {
                foreach (var o in txtOuts)
                {
                    h.Open(o);
                    string t = h.Text();
                    h.CloseDoc();
                    Check("글자 파일 → " + Path.GetFileName(o) + ": 다시 열면 글자 그대로", t == TxtBody, ShowBreaks(t));
                }
            }
            int txtParas = txtOuts.Count > 0 && Formats.Ext(txtOuts[0]) == ".hwpx" ? HwpxParagraphs(txtOuts[0]) : -1;
            Check("글자 파일 → HWPX: 줄마다 문단 하나(5개)", txtParas == 5, txtParas + "개");

            // 9-2. 큰 글자 파일(2만 줄, 100만 자쯤)
            var bigText = new StringBuilder();
            for (int i = 1; i <= 20000; i++) bigText.Append(i).Append(". 가나다라마바사 아자차카타파하 0123456789 ABCDEFG 한 줄짜리 시험 글\r\n");
            string bigTxt = Path.Combine(w, "큰글자.txt");
            File.WriteAllText(bigTxt, bigText.ToString(), new UTF8Encoding(false));
            sw.Restart();
            j = RunJob("format", "hwpx", new[] { bigTxt }, null);
            int bigParas = j.Outputs.Count == 1 ? HwpxParagraphs(j.Outputs[0]) : -1;
            Check("큰 글자 파일(2만 줄) → HWPX", bigParas == 20000 && j.Problems.Count == 0 && sw.Elapsed.TotalSeconds < 60, bigParas + "문단, " + string.Join(" / ", j.Problems) + " " + sw.Elapsed.TotalSeconds.ToString("0.0") + "초");

            // 10. 섞어 놓기와 원래 있던 HWP → HWPX
            if (hwp != null)
            {
                j = RunJob("format", "hwp", new[] { docx, pdf, txtAnsi, hwp }, null);
                Check("섞어 놓기: 워드·PDF·글자 파일은 HWP로, 이미 HWP인 것은 건너뜀", j.Outputs.Count == 3 && j.Problems.Count == 0 && j.Notes.Any(x => x.Contains("1개는 건너뛰었어요")), string.Join(" / ", j.Problems.Concat(j.Notes)));
                j = RunJob("format", "hwpx", new[] { hwp }, null);
                Check("HWP → HWPX(원래 기능)", HwpxTextOf(j.Outputs.FirstOrDefault()).Contains("현장체험학습"), string.Join(" / ", j.Problems));
            }

            // 11. 한글을 연달아 켜고 끄기. 예전에는 바꿈이 켜 있는 동안 두 번째로 켠 한글부터 끌 때마다 한글이 멈춰서
            //     끄는 데 7초쯤 걸리고 Windows 오류 보고가 떴다(OfficeApp.Dispose가 dynamic으로 COM 객체를 놓던 탓).
            var quits = new List<double>();
            for (int i = 0; i < 3; i++)
            {
                var h = new Hangul(j);
                try
                {
                    h.InsertDoc(docx, "OOXML");
                    h.CloseDoc();
                }
                finally
                {
                    sw.Restart();
                    h.Dispose();
                    quits.Add(sw.Elapsed.TotalSeconds);
                }
            }
            Check("한글을 연달아 켜고 꺼도 바로 꺼짐", quits.All(x => x < 5), string.Join(", ", quits.Select(x => x.ToString("0.0") + "초")));

            // 12. 한워드가 뜨지 않고, 끝나면 한글이 남지 않는다
            var hwordNew = Util.PidsOf("Hword");
            hwordNew.ExceptWith(hwordBefore);
            Check("한워드가 뜨지 않음", hwordNew.Count == 0, hwordNew.Count + "개");
            HashSet<int> left = null;
            for (int i = 0; i < 20; i++)
            {
                left = Util.PidsOf("Hwp");
                left.ExceptWith(hwpBefore);
                if (left.Count == 0) break;
                System.Threading.Thread.Sleep(500);
            }
            Check("끝나면 한글이 남지 않음", left.Count == 0, left.Count + "개");
        }

        /// 워드 파일의 기본 표 칸 여백을 MS 워드 기본값(위·아래 0)으로 바꾼다.
        static void WordDefaultTableMargins(string docx)
        {
            using (var fs = new FileStream(docx, FileMode.Open, FileAccess.ReadWrite))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Update))
            {
                var e = z.GetEntry("word/styles.xml");
                string xml;
                using (var r = new StreamReader(e.Open(), Encoding.UTF8)) xml = r.ReadToEnd();
                xml = xml.Replace("<w:top w:w=\"40\" w:type=\"dxa\"/>", "<w:top w:w=\"0\" w:type=\"dxa\"/>").Replace("<w:bottom w:w=\"40\" w:type=\"dxa\"/>", "<w:bottom w:w=\"0\" w:type=\"dxa\"/>");
                e.Delete();
                var n = z.CreateEntry("word/styles.xml");
                using (var s = new StreamWriter(n.Open(), new UTF8Encoding(false))) s.Write(xml);
            }
        }

        /// 표 맨 아래에 두 칸을 가로로 합친 줄을 넣는다(워드의 gridSpan). 바꿈의 워드 쓰기는 칸을 합치지 않아서 여기서 넣는다.
        static void AddMergedRow(string docx)
        {
            using (var fs = new FileStream(docx, FileMode.Open, FileAccess.ReadWrite))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Update))
            {
                var e = z.GetEntry("word/document.xml");
                string xml;
                using (var r = new StreamReader(e.Open(), Encoding.UTF8)) xml = r.ReadToEnd();
                const string Para = "<w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr><w:r><w:t xml:space=\"preserve\">{0}</w:t></w:r></w:p>";
                string row = "<w:tr><w:tc><w:tcPr><w:tcW w:w=\"3212\" w:type=\"dxa\"/></w:tcPr>" + string.Format(Para, "참고") + "</w:tc>" +
                    "<w:tc><w:tcPr><w:tcW w:w=\"6424\" w:type=\"dxa\"/><w:gridSpan w:val=\"2\"/></w:tcPr>" + string.Format(Para, "돌아올 때도 같은 버스를 타요") + "</w:tc></w:tr>";
                xml = xml.Insert(xml.IndexOf("</w:tbl>", StringComparison.Ordinal), row);
                e.Delete();
                var n = z.CreateEntry("word/document.xml");
                using (var s = new StreamWriter(n.Open(), new UTF8Encoding(false))) s.Write(xml);
            }
        }

        /// HWPX 안 표 칸 가운데 가로로 합친 칸(colSpan 2 이상)의 수
        static int MergedCells(string hwpx)
        {
            int n = 0;
            using (var fs = File.OpenRead(hwpx))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var e in z.Entries.Where(x => x.FullName.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase)))
                {
                    var doc = new XmlDocument();
                    using (var s = e.Open()) doc.Load(s);
                    foreach (XmlNode c in doc.SelectNodes("//*[local-name()='tc']/*[local-name()='cellSpan']"))
                    {
                        var span = c.Attributes["colSpan"];
                        if (span != null && int.Parse(span.Value) >= 2) n++;
                    }
                }
            }
            return n;
        }

        static string HwpxTextOf(string hwpx)
        {
            return hwpx == null || !File.Exists(hwpx) ? "" : OoxmlRead.HwpxText(hwpx);
        }

        /// HWPX 본문(section)의 문단 수
        static int HwpxParagraphs(string hwpx)
        {
            int n = 0;
            using (var fs = File.OpenRead(hwpx))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var e in z.Entries.Where(x => x.FullName.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase)))
                {
                    var doc = new XmlDocument();
                    using (var s = e.Open()) doc.Load(s);
                    n += doc.SelectNodes("/*/*[local-name()='p']").Count;
                }
            }
            return n;
        }

        /// 줄 바꿈·탭을 눈에 보이게(시험 결과 줄에 쓴다)
        static string ShowBreaks(string s)
        {
            return s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        /// HWPX 안 표(tbl)의 칸 안 여백 위·아래 값
        static List<int> TableMargins(string hwpx)
        {
            var list = new List<int>();
            using (var fs = File.OpenRead(hwpx))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var e in z.Entries.Where(x => x.FullName.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase)))
                {
                    var doc = new XmlDocument();
                    using (var s = e.Open()) doc.Load(s);
                    foreach (XmlNode n in doc.SelectNodes("//*[local-name()='tbl']/*[local-name()='inMargin']"))
                    {
                        list.Add(int.Parse(n.Attributes["top"].Value));
                        list.Add(int.Parse(n.Attributes["bottom"].Value));
                    }
                }
            }
            return list;
        }

        static bool HasBinData(string hwpx)
        {
            using (var fs = File.OpenRead(hwpx))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
                return z.Entries.Any(e => e.FullName.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase) && e.Length > 0);
        }

        /// 흰 바탕에 글줄을 그린 그림(스캔한 쪽 흉내)
        static void DrawPage(string path, int width, int height, string[] lines)
        {
            using (var bmp = new Bitmap(width, height))
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font("Malgun Gothic", Math.Max(24, width / 28), FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], font, Brushes.Black, width / 10, height / 12 + i * font.Height * 2);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        static string ImageToPdf(string png, string pdf)
        {
            var j = RunJob("pdf", "each", new[] { png }, null);
            if (j.Outputs.Count == 1) File.Move(j.Outputs[0], pdf);
            return pdf;
        }

        static string Rtf(params string[] paras)
        {
            var sb = new StringBuilder("{\\rtf1\\ansi\\ansicpg949\\deff0{\\fonttbl{\\f0\\fnil\\fcharset129 Malgun Gothic;}}\\uc1\\f0\\fs22 ");
            foreach (var p in paras)
            {
                foreach (char c in p)
                {
                    if (c >= 128) sb.Append("\\u").Append((short)c).Append('?');
                    else
                    {
                        if (c == '\\' || c == '{' || c == '}') sb.Append('\\');
                        sb.Append(c);
                    }
                }
                sb.Append("\\par ");
            }
            return sb.Append('}').ToString();
        }

        /// 지어낸 오픈 문서(.odt). full이면 보통 프로그램이 만드는 꼴(styles·meta·settings까지), 아니면 내용만 든 간단한 꼴.
        static void WriteOdt(string path, bool full)
        {
            const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"";
            var parts = new List<KeyValuePair<string, string>>();
            parts.Add(new KeyValuePair<string, string>("mimetype", "application/vnd.oasis.opendocument.text"));
            string manifest = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>";
            if (full) manifest += "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/><manifest:file-entry manifest:full-path=\"meta.xml\" manifest:media-type=\"text/xml\"/><manifest:file-entry manifest:full-path=\"settings.xml\" manifest:media-type=\"text/xml\"/>";
            parts.Add(new KeyValuePair<string, string>("META-INF/manifest.xml", manifest + "</manifest:manifest>"));
            if (full)
            {
                parts.Add(new KeyValuePair<string, string>("meta.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-meta " + ns + " office:version=\"1.2\"><office:meta><meta:generator>bakkum-test</meta:generator><dc:title>오픈 문서 시험</dc:title></office:meta></office:document-meta>"));
                parts.Add(new KeyValuePair<string, string>("settings.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-settings " + ns + " office:version=\"1.2\"><office:settings/></office:document-settings>"));
                parts.Add(new KeyValuePair<string, string>("styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-styles " + ns + " office:version=\"1.2\"><office:styles><style:default-style style:family=\"paragraph\"><style:text-properties fo:font-size=\"11pt\"/></style:default-style><style:style style:name=\"Standard\" style:family=\"paragraph\"/></office:styles><office:automatic-styles><style:page-layout style:name=\"pm1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\" fo:margin-top=\"2cm\" fo:margin-bottom=\"2cm\" fo:margin-left=\"2cm\" fo:margin-right=\"2cm\"/></style:page-layout></office:automatic-styles><office:master-styles><style:master-page style:name=\"Standard\" style:page-layout-name=\"pm1\"/></office:master-styles></office:document-styles>"));
            }
            parts.Add(new KeyValuePair<string, string>("content.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content " + ns + " office:version=\"1.2\"><office:body><office:text>" +
                "<text:h text:outline-level=\"1\">오픈 문서 제목</text:h><text:p>오픈 문서 첫 문단입니다.</text:p>" +
                "<table:table table:name=\"표1\"><table:table-column table:number-columns-repeated=\"2\"/><table:table-row><table:table-cell office:value-type=\"string\"><text:p>가칸</text:p></table:table-cell><table:table-cell office:value-type=\"string\"><text:p>나칸</text:p></table:table-cell></table:table-row></table:table>" +
                "<text:p>표 아래 문단입니다.</text:p></office:text></office:body></office:document-content>"));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var kv in parts)
                {
                    var e = z.CreateEntry(kv.Key, kv.Key == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using (var s = new StreamWriter(e.Open(), new UTF8Encoding(false))) s.Write(kv.Value);
                }
            }
        }
    }
}
