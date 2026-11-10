// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 도구 시험 중 PDF 부분. 예시 PDF는 파이썬으로 만들어 시험폴더에 넣어 둔다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bakkum
{
    static partial class ToolTest
    {
        static string Work(string name)
        {
            string w = P("pdfjob");
            Directory.CreateDirectory(w);
            string dst = Path.Combine(w, name);
            File.Copy(P(name), dst, true);
            return dst;
        }

        static int PageCount(string path, string password = null)
        {
            using (var d = new WinPdfDoc(path, password)) return d.PageCount;
        }

        static void PdfTests()
        {
            Cli.Print("[PDF]");
            string w = P("pdfjob");
            if (Directory.Exists(w)) Directory.Delete(w, true);
            Directory.CreateDirectory(w);
            string a = Work("a6.pdf"), b = Work("b3.pdf");
            string img = Path.Combine(w, "c1.jpg");
            File.Copy(P("c1.jpg"), img, true);

            // 쪽 범위 읽기
            string err;
            var r = PdfTools.ParseRanges("1, 3-5,8-", 9, out err);
            Check("쪽 범위 1,3-5,8-", r != null && r.SequenceEqual(new[] { 0, 2, 3, 4, 7, 8 }), r == null ? err : string.Join(",", r));
            Check("쪽 범위 초과", PdfTools.ParseRanges("12", 9, out err) == null && err.Contains("9쪽"), err);
            Check("쪽 범위 뒤집기 5-3", PdfTools.ParseRanges("5-3", 9, out err).SequenceEqual(new[] { 4, 3, 2 }), "");

            // 합치기(PDF·그림 섞기)
            var j = RunJob("pdf", "merge", new[] { a, b, img }, null);
            Check("합치기: 파일 생김", j.Outputs.Count == 1 && j.Problems.Count == 0, j.Outputs.Count == 1 ? Path.GetFileName(j.Outputs[0]) : "");
            if (j.Outputs.Count == 1)
            {
                Check("합치기: 쪽 수 6+3+1", PageCount(j.Outputs[0]) == 10, PageCount(j.Outputs[0]).ToString());
                File.Move(j.Outputs[0], Path.Combine(w, "r_merge.pdf"));
            }

            // 고르기
            var j2 = RunJob("pdftool", "pick", new[] { a }, null, "pages=2,4-5");
            Check("쪽 고르기", j2.Outputs.Count == 1 && PageCount(j2.Outputs[0]) == 3, "");
            if (j2.Outputs.Count == 1) File.Move(j2.Outputs[0], Path.Combine(w, "r_pick.pdf"));

            // 나누기
            var j3 = RunJob("pdftool", "split", new[] { a }, null, "every=4");
            bool splitOk = j3.Outputs.Count == 1 && Directory.Exists(j3.Outputs[0]);
            Check("나누기: 4쪽씩 → 파일 2개", splitOk && Directory.GetFiles(j3.Outputs[0]).Length == 2, splitOk ? string.Join(", ", Directory.GetFiles(j3.Outputs[0]).Select(Path.GetFileName)) : "");
            if (splitOk)
            {
                var parts = Directory.GetFiles(j3.Outputs[0]).OrderBy(x => x).ToList();
                Check("나누기: 쪽 수 4와 2", PageCount(parts[0]) == 4 && PageCount(parts[1]) == 2, "");
            }

            // 쪽 편집 저장: 순서 바꿈·돌리기·복제
            var edit = new PdfPagesEdit();
            edit.Items.Add(new PdfPagesEdit.Entry { Path = a, Index = 5, Rotate = 90 });
            edit.Items.Add(new PdfPagesEdit.Entry { Path = a, Index = 0 });
            edit.Items.Add(new PdfPagesEdit.Entry { Path = a, Index = 0, Rotate = 180 });
            edit.Items.Add(new PdfPagesEdit.Entry { Path = b, Index = 1 });
            var j4 = RunJob("pdftool", "pagesave", new[] { a, b }, edit);
            Check("쪽 편집 저장", j4.Outputs.Count == 1 && PageCount(j4.Outputs[0]) == 4, "");
            if (j4.Outputs.Count == 1) File.Move(j4.Outputs[0], Path.Combine(w, "r_pages.pdf"));

            // 돌리기만: 책갈피를 살린다
            var edit2 = new PdfPagesEdit { RotateOnly = true };
            for (int i = 0; i < 6; i++) edit2.Items.Add(new PdfPagesEdit.Entry { Path = a, Index = i, Rotate = i == 1 ? 90 : 0 });
            var j5 = RunJob("pdftool", "pagesave", new[] { a }, edit2);
            Check("돌리기만 저장", j5.Outputs.Count == 1 && PageCount(j5.Outputs[0]) == 6, "");
            if (j5.Outputs.Count == 1) File.Move(j5.Outputs[0], Path.Combine(w, "r_rotate.pdf"));

            // 워터마크(세 가지 자리, 돌아간 쪽 포함)
            foreach (var mode in new[] { "diagonal", "center", "tile" })
            {
                var j6 = RunJob("pdftool", "watermark", new[] { a }, null, "text=복사 금지", "mode=" + mode, "opacity=0.3");
                Check("워터마크 " + mode, j6.Outputs.Count == 1, "");
                if (j6.Outputs.Count == 1) File.Move(j6.Outputs[0], Path.Combine(w, "r_wm_" + mode + ".pdf"));
            }

            // 쪽 번호
            var j7 = RunJob("pdftool", "number", new[] { a }, null, "place=bottom-center", "style=total", "start=1");
            Check("쪽 번호", j7.Outputs.Count == 1, "");
            if (j7.Outputs.Count == 1) File.Move(j7.Outputs[0], Path.Combine(w, "r_num.pdf"));
            var j7b = RunJob("pdftool", "number", new[] { b }, null, "place=top-right", "style=dash", "start=5");
            if (j7b.Outputs.Count == 1) File.Move(j7b.Outputs[0], Path.Combine(w, "r_num2.pdf"));

            // 정보 지우기
            var j8 = RunJob("pdftool", "strip", new[] { a }, null);
            Check("정보 지우기", j8.Outputs.Count == 1, "");
            if (j8.Outputs.Count == 1) File.Move(j8.Outputs[0], Path.Combine(w, "r_strip.pdf"));

            // 다른 프로그램으로 확인할 수 있게 암호 파일을 그대로 남긴다
            var plainDoc = PdfDocument.Load(a);
            string keepAscii = Path.Combine(w, "keep_ascii.pdf"), keepKo = Path.Combine(w, "keep_ko.pdf");
            PdfCrypt.WriteLocked(plainDoc, plainDoc.Reachable(), new Dictionary<int, PdfObj>(), keepAscii, "abc12345");
            PdfCrypt.WriteLocked(plainDoc, plainDoc.Reachable(), new Dictionary<int, PdfObj>(), keepKo, "abc한글12");

            // 암호 걸기
            var j9 = RunJob("pdftool", "lock", new[] { a }, null, "password=abc한글12");
            Check("암호 걸기", j9.Outputs.Count == 1 && j9.Problems.Count == 0, "");
            if (j9.Outputs.Count == 1)
            {
                string locked = Path.Combine(w, "r_locked.pdf");
                File.Move(j9.Outputs[0], locked);
                Check("암호 걸린 PDF: 암호로 열림", PdfTools.Pages(PdfDocument.Load(locked, "abc한글12")).Count == 6, "");
                Check("암호 걸린 PDF: 암호 없으면 안 열림", PdfDocument.Load(locked).Encrypted, "");
                bool wrong = false;
                try { PdfDocument.Load(locked, "틀린암호"); } catch (UserFacingException) { wrong = true; }
                Check("암호 걸린 PDF: 틀린 암호 안 열림", wrong, "");
                bool friendly = false;
                try { PageCount(locked); } catch (UserFacingException ex) { friendly = ex.Message.Contains("암호"); }
                Check("암호 걸린 PDF: 윈도우 엔진 대신 안내", friendly, "");
                var j10 = RunJob("pdftool", "unlock", new[] { locked }, null, "password=틀린암호");
                Check("암호 풀기: 틀린 암호는 거절", j10.Outputs.Count == 0 && j10.Problems.Count == 1, "");
                var j11 = RunJob("pdftool", "unlock", new[] { locked }, null, "password=abc한글12");
                Check("암호 풀기: 맞는 암호", j11.Outputs.Count == 1, "");
                if (j11.Outputs.Count == 1)
                {
                    Check("암호 풀기: 암호 없이 열림", PageCount(j11.Outputs[0]) == 6, "");
                    File.Move(j11.Outputs[0], Path.Combine(w, "r_unlocked.pdf"));
                }
            }

            // 다른 프로그램이 건 암호 풀기
            foreach (var name in new[] { "enc_rc4.pdf", "enc_aes128.pdf", "enc_aes256.pdf" })
            {
                if (!File.Exists(P(name))) continue;
                string f = Work(name);
                var j12 = RunJob("pdftool", "unlock", new[] { f }, null, "password=pw1234");
                Check("풀기 " + name, j12.Outputs.Count == 1 && PageCount(j12.Outputs[0]) == 3, "");
                if (j12.Outputs.Count == 1) File.Move(j12.Outputs[0], Path.Combine(w, "r_" + name));
            }
            // 소유자 암호: 제한만 걸린 PDF는 열리는 암호만으로 못 푼다
            if (File.Exists(P("enc_owner.pdf")))
            {
                string f = Work("enc_owner.pdf");
                var j13 = RunJob("pdftool", "unlock", new[] { f }, null, "password=");
                Check("제한 PDF: 빈 암호로는 못 푼다", j13.Outputs.Count == 0 && j13.Problems.Count == 1, "");
                var j14 = RunJob("pdftool", "unlock", new[] { f }, null, "password=owner999");
                Check("제한 PDF: 소유자 암호로 푼다", j14.Outputs.Count == 1, "");
                if (j14.Outputs.Count == 1) File.Delete(j14.Outputs[0]);
            }

            // 암호 걸린 PDF를 다른 도구에 넣으면 알기 쉽게 알린다
            if (File.Exists(P("enc_aes256.pdf")))
            {
                string f = Work("enc_aes256.pdf");
                var j15 = RunJob("pdftool", "watermark", new[] { f }, null, "text=시험");
                Check("암호 걸린 PDF에 워터마크: 안내", j15.Outputs.Count == 0 && j15.Problems.Any(x => x.Contains("암호")), "");
            }

            // 목표 용량
            if (File.Exists(P("big.pdf")))
            {
                string f = Work("big.pdf");
                long before = new FileInfo(f).Length;
                long target = before / 3;
                var j16 = RunJob("shrink", "target", new[] { f }, null, "mb=" + (target / 1024.0 / 1024.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Check("목표 용량 PDF", j16.Outputs.Count == 1 && new FileInfo(j16.Outputs[0]).Length <= target, j16.Outputs.Count == 1 ? Conversions.Size(before) + " → " + Conversions.Size(new FileInfo(j16.Outputs[0]).Length) + " (목표 " + Conversions.Size(target) + ")" : "");
            }
            string photo = Work("scene.jpg");
            var j17 = RunJob("shrink", "target", new[] { photo }, null, "mb=0.15");
            Check("목표 용량 사진 0.15MB", j17.Outputs.Count == 1 && new FileInfo(j17.Outputs[0]).Length <= 0.15 * 1024 * 1024, j17.Outputs.Count == 1 ? Conversions.Size(new FileInfo(j17.Outputs[0]).Length) : "");
        }

        static string DocWork(string name)
        {
            string w = P("docjob");
            Directory.CreateDirectory(w);
            string dst = Path.Combine(w, name);
            File.Copy(P(name), dst, true);
            return dst;
        }

        static string One(Job j, string keep)
        {
            if (j.Outputs.Count == 0) return "";
            string to = Path.Combine(P("docjob"), keep);
            if (Directory.Exists(j.Outputs[0]))
            {
                if (Directory.Exists(to)) Directory.Delete(to, true);
                Directory.Move(j.Outputs[0], to);
            }
            else File.Copy(j.Outputs[0], to, true);
            return to;
        }

        static void DocTests()
        {
            Cli.Print("[문서]");
            string w = P("docjob");
            if (Directory.Exists(w)) Directory.Delete(w, true);
            Directory.CreateDirectory(w);

            // 자막
            var a = RunJob("format", "vtt", new[] { DocWork("s1.srt") }, null);
            string vtt = One(a, "r_s1.vtt");
            Check("SRT→VTT", vtt != "" && File.ReadAllText(vtt).StartsWith("WEBVTT") && File.ReadAllText(vtt).Contains("00:00:01.000 --> 00:00:03.500"), "");
            var b = RunJob("format", "srt", new[] { DocWork("s2.vtt") }, null);
            string srt = One(b, "r_s2.srt");
            Check("VTT→SRT", srt != "" && File.ReadAllText(srt).Contains("00:00:01,000 --> 00:00:03,500") && !File.ReadAllText(srt).Contains("<v") && File.ReadAllText(srt).Contains("둘째 자막"), srt == "" ? "" : File.ReadAllText(srt).Replace("\n", " / "));

            // 글자 문서
            One(RunJob("format", "docx", new[] { DocWork("n1.md") }, null), "r_n1.docx");
            One(RunJob("format", "docx", new[] { DocWork("n2.txt") }, null), "r_n2.docx");
            One(RunJob("format", "docx", new[] { DocWork("n3.html") }, null), "r_n3.docx");
            One(RunJob("format", "html", new[] { DocWork("n1.md") }, null), "r_n1.html");
            string htmlMd = One(RunJob("format", "md", new[] { DocWork("n3.html") }, null), "r_n3.md");
            Check("글자 문서→워드", File.Exists(Path.Combine(w, "r_n1.docx")) && File.Exists(Path.Combine(w, "r_n2.docx")) && File.Exists(Path.Combine(w, "r_n3.docx")), "");
            string htmlText = htmlMd == "" ? "" : File.ReadAllText(htmlMd, System.Text.Encoding.UTF8);
            Check("HTML→마크다운: 문서 선언 안 섞임, 표 앞 빈 줄", htmlText.StartsWith("# 웹 제목") && htmlText.Contains("\n\n| 열1"), htmlText.Replace("\n", " / "));

            // 표 파일
            One(RunJob("format", "xlsx", new[] { DocWork("t1.csv") }, null), "r_t1.xlsx");
            One(RunJob("format", "xlsx", new[] { DocWork("t2.tsv") }, null), "r_t2.xlsx");
            var c = RunJob("format", "csv", new[] { DocWork("x1.xlsx") }, null);
            string csv = One(c, "r_x1.csv");
            Check("엑셀→CSV", csv != "" && File.ReadAllText(csv).Contains("2026-03-02") && File.ReadAllText(csv).Contains("\"나,다\""), csv == "" ? "" : File.ReadAllText(csv).Replace("\r\n", " / "));

            // 사진
            string p1 = DocWork("c1.jpg"), p2 = DocWork("c2.jpg"), p3 = DocWork("clear.png");
            One(RunJob("format", "docx", new[] { p1, p2, p3 }, null), "r_photos.docx");
            One(RunJob("format", "pptx", new[] { p1, p2, p3 }, null), "r_photos.pptx");
            Check("사진→워드·PPT", File.Exists(Path.Combine(w, "r_photos.docx")) && File.Exists(Path.Combine(w, "r_photos.pptx")), "");

            // PDF → PPT, 글자, 표(글자 위치)
            string tbl = DocWork("tbl.pdf");
            One(RunJob("format", "pptx", new[] { tbl, DocWork("a6.pdf") }, null), "r_tbl.pptx");
            string rt = Path.Combine(w, "r_tbl.pptx");
            string rtText = File.Exists(rt) ? OoxmlRead.PptxText(rt) : "";
            Check("PDF→PPT: 글자를 글상자로 옮긴다", rtText.Contains("표 아래의 문장입니다.") && rtText.Contains("라마바"), rtText.Length + "자");
            One(RunJob("format", "pptximg", new[] { tbl }, null), "r_tblimg.pptx");
            string ri = Path.Combine(w, "r_tblimg.pptx");
            Check("PDF→그림 PPT: 모양 그대로 그림으로", File.Exists(ri) && !OoxmlRead.PptxText(ri).Contains("표 아래의 문장입니다."), "");
            var scanPpt = RunJob("format", "pptx", new[] { DocWork("scan.pdf") }, null);
            Check("스캔 PDF→PPT: 멈추지 않고 만든다", scanPpt.Outputs.Count == 1 && scanPpt.Problems.Count == 0, string.Join(" / ", scanPpt.Notes));
            var layer = PdfText.Read(tbl, null);
            Check("PDF 글자 읽기", layer != null && layer.Usable && layer.Plain().Contains("표 아래의 문장입니다.") && layer.Plain().Contains("라마바"), layer == null ? "null" : layer.Chars + "자, 틀림 " + layer.Pages[0].Bad);
            var guess = layer.GuessTables();
            Check("PDF 표 짐작", guess.Count == 1 && guess[0].Rows.Count == 4 && guess[0].Rows[0].Count == 3 && guess[0].Rows[2][1] == "라마바", guess.Count == 0 ? "" : guess[0].Rows.Count + "줄");
            var sc = PdfText.Read(DocWork("scan.pdf"), null);
            Check("스캔 PDF는 글자 없음으로", sc != null && !sc.Usable, "");
            var ex1 = RunJob("text", "local", new[] { DocWork("a6.pdf") }, null);
            string txt = One(ex1, "r_a6.txt");
            Check("글자 뽑기: PDF 글자 그대로", txt != "" && File.ReadAllText(txt, System.Text.Encoding.UTF8).Contains("A문서 3쪽"), "");

            // 문서에서 글자 읽기
            Check("워드 글자", OoxmlRead.DocxText(P("w1.docx")).Contains("칸1\t칸2"), OoxmlRead.DocxText(P("w1.docx")).Replace("\n", " / ").Replace("\t", " | "));
            Check("PPT 글자", OoxmlRead.PptxText(P("p1.pptx")).Contains("슬라이드 내용") && OoxmlRead.PptxText(P("p1.pptx")).Contains("그림 슬라이드"), "");
            Check("엑셀 글자", OoxmlRead.RowsText(OoxmlRead.XlsxRead(P("x1.xlsx"))).Contains("2026-03-03"), "");
            var ex2 = RunJob("text", "local", new[] { DocWork("w1.docx") }, null);
            Check("글자 뽑기: 워드", ex2.Outputs.Count == 1 && ex2.Problems.Count == 0, "");

            // 문서 속 사진
            string w1 = DocWork("w1.docx");
            var d1 = RunJob("image", "extract", new[] { w1 }, null);
            string pics = One(d1, "r_w1_pics");
            Check("워드 속 사진 뽑기", pics != "" && Directory.Exists(pics) && Directory.GetFiles(pics).Length == 2, "");
            var d2 = RunJob("image", "extract", new[] { DocWork("p1.pptx") }, null);
            Check("PPT 속 사진 뽑기", d2.Outputs.Count == 1, "");
            var d3 = RunJob("image", "extract", new[] { DocWork("tbl.pdf") }, null);
            string pdfPic = One(d3, "r_tbl_pic.jpg");
            Check("PDF 속 사진 뽑기", pdfPic != "" && File.Exists(pdfPic), "");
            var d4 = RunJob("image", "extract", new[] { DocWork("big.pdf") }, null);
            Check("PDF 속 사진 여러 장", d4.Outputs.Count == 1 && Directory.Exists(d4.Outputs[0]) && Directory.GetFiles(d4.Outputs[0]).Length == 4, "");

            // 문서 줄이기
            long before = new FileInfo(w1).Length;
            var s1 = RunJob("shrink", "strong", new[] { w1 }, null);
            string small = One(s1, "r_w1_small.docx");
            Check("워드 줄이기", small != "" && new FileInfo(small).Length < before * 0.8, small == "" ? "" : Conversions.Size(before) + " → " + Conversions.Size(new FileInfo(small).Length));
            var t1 = RunJob("shrink", "target", new[] { w1 }, null, "mb=0.4");
            string tgt = One(t1, "r_w1_target.docx");
            Check("워드 목표 용량", tgt != "" && new FileInfo(tgt).Length <= 0.4 * 1024 * 1024, tgt == "" ? "" : Conversions.Size(new FileInfo(tgt).Length));

            // 한컴이 있어야 하는 것(있을 때만)
            if (File.Exists(P("h1.hwpx")))
            {
                var h1 = RunJob("format", "xlsx", new[] { DocWork("h1.hwpx") }, null);
                string xl = One(h1, "r_h1.xlsx");
                Check("HWPX 표→엑셀(프로그램 없이)", xl != "", "");
                Check("HWPX 글자", OoxmlRead.HwpxText(P("h1.hwpx")).Contains("이것은 예시 문단입니다"), "");
            }
        }

        public static void HancomTests()
        {
            Cli.Print("[한컴]");
            string w = P("docjob");
            Directory.CreateDirectory(w);
            if (!File.Exists(P("h1.hwp")) || !File.Exists(P("h1.hwpx")))
            {
                // 중립 예시 문서를 한글로 만든다.
                var maker = RunJob("text", "local", new string[0], null);
                using (var h = new Hangul(maker))
                {
                    h.Convert(P("h_src.html"), P("h1.hwp"), "HWP");
                    h.Convert(P("h_src.html"), P("h1.hwpx"), "HWPX");
                }
                Check("예시 HWP·HWPX 만들기", File.Exists(P("h1.hwp")) && File.Exists(P("h1.hwpx")), "");
            }
            if (File.Exists(P("h1.hwpx")))
            {
                var hx = RunJob("format", "xlsx", new[] { DocWork("h1.hwpx") }, null);
                Check("HWPX 표→엑셀", One(hx, "r_h1x.xlsx") != "" && hx.Problems.Count == 0, string.Join("; ", hx.Problems));
                Check("HWPX 글자", OoxmlRead.HwpxText(P("h1.hwpx")).Contains("이것은 예시 문단입니다"), "");
                var ht = RunJob("text", "docx", new[] { DocWork("h1.hwpx") }, null);
                Check("HWPX 글자→워드", One(ht, "r_h1x_text.docx") != "" && ht.Problems.Count == 0, string.Join("; ", ht.Problems));
            }
            if (File.Exists(P("h1.hwp")))
            {
                var a = RunJob("format", "docx", new[] { DocWork("h1.hwp") }, null);
                Check("HWP→워드", One(a, "r_h1.docx") != "" && a.Problems.Count == 0, string.Join("; ", a.Problems));
                var b = RunJob("format", "xlsx", new[] { DocWork("h1.hwp") }, null);
                Check("HWP 표→엑셀", One(b, "r_h1b.xlsx") != "" && b.Problems.Count == 0, string.Join("; ", b.Problems));
                var c = RunJob("text", "local", new[] { DocWork("h1.hwp") }, null);
                Check("HWP 글자", One(c, "r_h1.txt") != "", "");
            }
            var d = RunJob("format", "docx", new[] { DocWork("tbl.pdf") }, null);
            Check("PDF→워드(한글 거쳐)", One(d, "r_tbl.docx") != "" && d.Problems.Count == 0, string.Join("; ", d.Problems.Concat(d.Notes)));
            var e = RunJob("format", "xlsx", new[] { DocWork("tbl.pdf") }, null);
            Check("PDF 표→엑셀(한글 거쳐)", One(e, "r_tbl.xlsx") != "" && e.Problems.Count == 0, string.Join("; ", e.Problems.Concat(e.Notes)));
        }
    }
}
