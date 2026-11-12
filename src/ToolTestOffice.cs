// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 옛 오피스 형식을 새 형식으로(#28): 옛 엑셀(.xls)·한셀(.cell) → xlsx·csv·tsv, 옛 PPT(.ppt) → pptx.
// 재료 파일 없이 돈다. 바꿈이 지어낸 xlsx·pptx를 쓰고, 옛 형식은 이 PC의 MS 오피스·한컴오피스로 그 자리에서 만든다.
// 한쇼 고유 형식(.show)은 자동화로 만들 방법을 찾지 못해(저장 형식 번호가 파워포인트와 같다, 2026-11-13) 시험하지 못한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Bakkum
{
    static partial class ToolTest
    {
        static void OfficeLegacyTests()
        {
            Cli.Print("[옛 오피스 형식 → 새 형식]");
            Cli.Print("    MS 엑셀 " + Has(MsExcel.Installed) + " · 한셀 " + Has(Hancell.Installed) + " · MS 파워포인트 " + Has(MsPowerPoint.Installed) + " · 한쇼 " + Has(Hanshow.Installed));
            string w = P("office_legacy");
            if (Directory.Exists(w)) Directory.Delete(w, true);
            Directory.CreateDirectory(w);
            var rows = new List<List<string>> { new List<string> { "번호", "준비물" }, new List<string> { "1", "색연필" }, new List<string> { "2", "가위" } };
            string xlsx = Path.Combine(w, "준비물표.xlsx");
            XlsxWriter.Write(xlsx, new List<XlsxSheet> { new XlsxSheet { Name = "표", Rows = rows } });
            var fmt = ActionOf("format");
            string xls = Path.Combine(w, "준비물표.xls"), ppt = Path.Combine(w, "물의순환.ppt");

            // 0. 섞어 놓았을 때의 메뉴(파일이 없어도 이름으로만 정한다)
            string pic = Path.Combine(w, "사진.png"), hwp = Path.Combine(w, "안내문.hwp"), newDeck = Path.Combine(w, "새것.pptx");
            var mixPic = fmt.OptionsFor(new[] { pic, ppt });
            Check("메뉴: 사진+.ppt → PPT 고르기 하나", mixPic.Count(o => o.Id == "pptx") == 1, string.Join(",", mixPic.Select(o => o.Id)));
            var mixSheet = fmt.OptionsFor(new[] { xlsx, xls }).Select(o => o.Id).ToList();
            Check("메뉴: .xlsx+.xls → XLSX·CSV", fmt.Usable(new[] { xlsx, xls }).Count == 2 && mixSheet.Contains("xlsx") && mixSheet.Contains("csv"), string.Join(",", mixSheet));
            Check("메뉴: .pptx+.ppt → .ppt만", fmt.Usable(new[] { newDeck, ppt }).SequenceEqual(new[] { ppt }), string.Join(",", fmt.Usable(new[] { newDeck, ppt }).Select(Path.GetFileName)));
            var mixHwp = fmt.OptionsFor(new[] { hwp, ppt }).FirstOrDefault(o => o.Id == "pptx");
            Check("메뉴: 한글+.ppt면 'PPT로' 이름 그대로", mixHwp != null && mixHwp.Label == "PPT로", mixHwp == null ? "없음" : mixHwp.Label);

            // 1. 요즘 한셀의 .cell(속이 매크로 통합 문서와 같다)은 프로그램 없이 읽는다
            string cell = Path.Combine(w, "준비물표.cell");
            File.Copy(xlsx, cell);
            AsMacroWorkbook(cell);
            var cellOpts = fmt.OptionsFor(new[] { cell }).Select(o => o.Id).ToList();
            Check("메뉴: .cell도 형식 바꾸기에(XLSX·CSV)", fmt.CanTake(new[] { cell }) && cellOpts.Contains("xlsx") && cellOpts.Contains("csv"), string.Join(",", cellOpts));
            var j = RunJob("format", "csv", new[] { cell }, null);
            string csv = j.Outputs.FirstOrDefault();
            Check(".cell → CSV(프로그램 없이 읽음)", csv != null && File.ReadAllText(csv, Encoding.UTF8).Contains("색연필"), csv == null ? string.Join(" / ", j.Problems) : Path.GetFileName(csv));
            if (Hancell.Installed)
            {
                // 한셀 길은 한셀이 직접 저장한 .cell로 본다. 바꿈이 흉내 낸 .cell은 한셀이 표로 알아보지 못하고 글자로 읽었다(2026-11-13).
                string real = Path.Combine(w, "한셀이저장.cell");
                string by = MakeLegacy(xlsx, real, -1, false, true);
                Check("시험용 .cell 만들기(" + by + ")", File.Exists(real), by);
                if (File.Exists(real))
                {
                    j = RunJob("format", "xlsx", new[] { real }, null);
                    string fromCell = j.Outputs.FirstOrDefault();
                    Check(".cell → XLSX(한셀)", fromCell != null && IsSpreadsheetXlsx(fromCell) && HasCell(fromCell, "색연필"),
                        fromCell == null ? string.Join(" / ", j.Problems) : Path.GetFileName(fromCell));
                    j = RunJob("format", "csv", new[] { real }, null);
                    string realCsv = j.Outputs.FirstOrDefault();
                    Check("한셀이 저장한 .cell → CSV", realCsv != null && File.ReadAllText(realCsv, Encoding.UTF8).Contains("가위"), realCsv == null ? string.Join(" / ", j.Problems) : Path.GetFileName(realCsv));
                }
            }
            else
            {
                j = RunJob("format", "xlsx", new[] { cell }, null);
                string fromCell = j.Outputs.FirstOrDefault();
                Check(".cell → XLSX(한셀 없이 값만)", fromCell != null && IsSpreadsheetXlsx(fromCell) && HasCell(fromCell, "색연필") && j.Notes.Any(n => n.Contains("값만")),
                    fromCell == null ? string.Join(" / ", j.Problems) : Path.GetFileName(fromCell) + " " + string.Join(" / ", j.Notes));
            }

            // 2. 옛 엑셀(.xls): MS 엑셀이나 한셀로 만들어 바꾼다
            if (MsExcel.Installed || Hancell.Installed)
            {
                string by = MakeLegacy(xlsx, xls, 56, false, false);
                Check("시험용 .xls 만들기(" + by + ")", File.Exists(xls), by);
                if (File.Exists(xls))
                {
                    Check("메뉴: .xls도 형식 바꾸기에", fmt.CanTake(new[] { xls }), null);
                    j = RunJob("format", "xlsx", new[] { xls }, null);
                    string x2 = j.Outputs.FirstOrDefault();
                    Check(".xls → XLSX", x2 != null && IsSpreadsheetXlsx(x2) && HasCell(x2, "색연필"), x2 == null ? string.Join(" / ", j.Problems) : Path.GetFileName(x2));
                    j = RunJob("format", "tsv", new[] { xls }, null);
                    string tsv = j.Outputs.FirstOrDefault();
                    Check(".xls → TSV", tsv != null && File.ReadAllText(tsv, Encoding.UTF8).Contains("가위"), tsv == null ? string.Join(" / ", j.Problems) : Path.GetFileName(tsv));
                }
            }
            else Cli.Print("    MS 엑셀·한셀이 없어 .xls 시험은 건너뜀");

            // 3. 옛 PPT(.ppt) → pptx. 이미 새 형식인 .pptx는 이 칸에서 받지 않는다
            Check("메뉴: .pptx는 형식 바꾸기에 없음", !fmt.CanTake(new[] { Path.Combine(w, "새것.pptx") }), null);
            if (MsPowerPoint.Installed || Hanshow.Installed)
            {
                string pptx = Path.Combine(w, "물의순환.pptx");
                MakeLectureDeck(pptx);
                string by = MakeLegacy(pptx, ppt, 1, true, false);
                Check("시험용 .ppt 만들기(" + by + ")", File.Exists(ppt), by);
                if (File.Exists(ppt))
                {
                    var pptOpts = fmt.OptionsFor(new[] { ppt }).Select(o => o.Id).ToList();
                    Check("메뉴: .ppt → PPTX 고르기", pptOpts.Contains("pptx"), string.Join(",", pptOpts));
                    j = RunJob("format", "pptx", new[] { ppt }, null);
                    string back = j.Outputs.FirstOrDefault();
                    Check(".ppt → PPTX(글자 그대로)", back != null && back.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) && OoxmlRead.PptxText(back).Contains("구름"),
                        back == null ? string.Join(" / ", j.Problems) : Path.GetFileName(back));
                }
            }
            else Cli.Print("    MS 파워포인트·한쇼가 없어 .ppt 시험은 건너뜀");

            // 4. MS 오피스가 없는 PC(관리자 PC처럼)에서 쓰는 한컴 길: 한셀로 .xls → xlsx, 한쇼로 .ppt → pptx.
            //    MS 오피스가 있으면 바꿈은 그쪽을 먼저 고르므로, 같은 저장 함수를 한컴 프로그램으로 직접 불러 본다.
            if (Hancell.Installed && File.Exists(xls))
            {
                string hx = Path.Combine(w, "한셀로바꿈.xlsx");
                string by = MakeLegacy(xls, hx, 51, false, true);
                Check("한셀: .xls → XLSX", File.Exists(hx) && IsSpreadsheetXlsx(hx) && HasCell(hx, "색연필"), by);
            }
            if (Hanshow.Installed && File.Exists(ppt))
            {
                string hp = Path.Combine(w, "한쇼로바꿈.pptx");
                string by = MakeLegacy(ppt, hp, 24, true, true);
                Check("한쇼: .ppt → PPTX", File.Exists(hp) && OoxmlRead.PptxText(hp).Contains("구름"), by);
            }
            Cli.Print("    한쇼 고유 형식(.show)은 자동화로 만들 수 없어 시험하지 못함");
        }

        static string Has(bool installed)
        {
            return installed ? "있음" : "없음";
        }

        /// xlsx의 통합 문서 형식을 매크로 통합 문서로 바꾼다(요즘 한셀이 .cell로 저장하는 꼴)
        static void AsMacroWorkbook(string path)
        {
            using (var zip = new ZipArchive(File.Open(path, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
            {
                var e = zip.GetEntry("[Content_Types].xml");
                string x;
                using (var r = new StreamReader(e.Open(), Encoding.UTF8)) x = r.ReadToEnd();
                e.Delete();
                var n = zip.CreateEntry("[Content_Types].xml");
                using (var wr = new StreamWriter(n.Open(), new UTF8Encoding(false))) wr.Write(x.Replace("spreadsheetml.sheet.main+xml", "ms-excel.sheet.macroEnabled.main+xml"));
            }
        }

        /// 엑셀이 여는 보통 xlsx인지(매크로 통합 문서 형식이 아니라)
        static bool IsSpreadsheetXlsx(string path)
        {
            try
            {
                using (var zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read))
                {
                    var e = zip.GetEntry("[Content_Types].xml");
                    if (e == null) return false;
                    using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd().Contains("spreadsheetml.sheet.main+xml");
                }
            }
            catch (InvalidDataException) { return false; }
        }

        static bool HasCell(string xlsx, string text)
        {
            return OoxmlRead.XlsxRead(xlsx).Any(s => s.Rows.Any(r => r.Contains(text)));
        }

        /// 시험용 옛 형식 파일을 이 PC의 프로그램으로 만든다. hancom이면 한컴 프로그램만 쓴다. 만든 프로그램 이름(실패하면 까닭).
        static string MakeLegacy(string src, string dst, int format, bool deck, bool hancom)
        {
            var job = new Job(ActionOf("format"), new ActionOption("make", "make", ""), new List<string>()) { Headless = true };
            try
            {
                if (deck)
                {
                    if (MsPowerPoint.Installed && !hancom)
                    {
                        job.Use(() => new MsPowerPoint(job)).SaveAs(src, dst, format, "시험 파일로 저장하지");
                        return "MS 파워포인트";
                    }
                    job.Use(() => new Hanshow(job)).SaveAs(src, dst, format, "시험 파일로 저장하지");
                    return "한쇼";
                }
                if (MsExcel.Installed && !hancom)
                {
                    job.Use(() => new MsExcel(job)).SaveAs(src, dst, format);
                    return "MS 엑셀";
                }
                job.Use(() => new Hancell(job)).SaveAs(src, dst, format);
                return "한셀";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                job.ReleaseResources();
            }
        }
    }
}
