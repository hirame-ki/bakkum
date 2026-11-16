// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki, moonsune.zip - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 도구 시험 중 한컴오피스 찾기·켜기(#82): /selftest-tools 시험폴더 hncfind
// 가짜 레지스트리로 찾는 차례를 보고, 이 PC에 한컴오피스가 있으면 보통 길(이름표로 찾기 → 켜기)을 일부러 막아
// 새 길로만 한글·한셀·한쇼를 켜고 지어낸 HWP를 읽는다. 레지스트리에는 아무것도 쓰지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Bakkum
{
    static partial class ToolTest
    {
        /// 가짜 레지스트리: (사용자·PC, 32·64비트 자리, 경로) → 기본값
        sealed class FakeRegistry
        {
            readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public void Set(RegistryHive hive, RegistryView view, string path, string value)
            {
                values[hive + "|" + view + "|" + path] = value;
            }

            public string Read(RegistryHive hive, RegistryView view, string path)
            {
                string v;
                return values.TryGetValue(hive + "|" + view + "|" + path, out v) ? v : null;
            }

            /// 프로그램 등록(LocalServer32와 이름표). 파일도 있는 것으로 한다.
            public void Server(RegistryHive hive, RegistryView view, Guid clsid, string exe, string command, string progId)
            {
                string k = @"SOFTWARE\Classes\CLSID\" + clsid.ToString("B") + @"\";
                Set(hive, view, k + "LocalServer32", command);
                if (progId != null) Set(hive, view, k + "ProgID", progId);
                if (exe != null) Files.Add(exe);
            }

            public ComClass Search(string progId)
            {
                return ComFind.Search(progId, Read, Files.Contains);
            }
        }

        static void HncFindTests()
        {
            Cli.Print("[한컴오피스 찾기·켜기]");
            ComFindLogicTests();
            ComFindRealTests();
        }

        static void ComFindLogicTests()
        {
            const string Hwp = "HWPFrame.HwpObject";
            var lm = RegistryHive.LocalMachine;
            var cu = RegistryHive.CurrentUser;
            var r64 = RegistryView.Registry64;
            var r32 = RegistryView.Registry32;
            var known = new Guid("2291CF00-64A1-4877-A9B4-68CFE89612D6");
            var newer = new Guid("13131313-2024-4000-8000-000000000130");
            var older = new Guid("12121212-2022-4000-8000-000000000120");
            string exe = @"C:\Program Files (x86)\Hnc\Office 2024\HOffice130\Bin\Hwp.exe";
            string oldExe = @"C:\Program Files (x86)\Hnc\Office 2022\HOffice120\Bin\Hwp.exe";

            var reg = new FakeRegistry();
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject\CurVer", "HWPFrame.HwpObject.3");
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject.3\CLSID", newer.ToString("B"));
            reg.Server(lm, r32, newer, exe, exe + " -Automation", "HWPFrame.HwpObject.3");
            var c = reg.Search(Hwp);
            Check("가짜 레지스트리: CurVer를 따라가 32비트 자리의 등록을 찾음", c != null && c.Clsid == newer && c.Type == null && c.How.Contains(@"HKLM\SOFTWARE\Classes\HWPFrame.HwpObject.3"), c == null ? "못 찾음" : c.How);

            reg = new FakeRegistry();
            reg.Set(lm, r64, @"SOFTWARE\Classes\Wow6432Node\HWPFrame.HwpObject\CLSID", newer.ToString("B"));
            reg.Server(lm, r32, newer, exe, exe + " -Automation", null);
            c = reg.Search(Hwp);
            Check("가짜 레지스트리: 이름표가 32비트 자리(Wow6432Node)에만 있어도 찾음", c != null && c.Clsid == newer && c.How.Contains("Wow6432Node"), c == null ? "못 찾음" : c.How);

            reg = new FakeRegistry();
            reg.Set(cu, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject\CLSID", newer.ToString("B"));
            reg.Server(cu, r32, newer, exe, "\"" + exe + "\" -Automation", null);
            c = reg.Search(Hwp);
            Check("가짜 레지스트리: 사용자 등록(HKCU)만 있어도 찾음", c != null && c.Clsid == newer && c.How.StartsWith("레지스트리(HKCU"), c == null ? "못 찾음" : c.How);

            reg = new FakeRegistry();
            reg.Set(cu, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject\CLSID", older.ToString("B"));
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject\CLSID", newer.ToString("B"));
            reg.Server(lm, r32, newer, exe, exe + " -Automation", null);
            c = reg.Search(Hwp);
            Check("가짜 레지스트리: 사용자 등록에 남은 옛 이름표가 PC 등록을 가려도 찾음", c != null && c.Clsid == newer && c.How.StartsWith("레지스트리(HKLM"), c == null ? "못 찾음" : c.How);

            reg = new FakeRegistry();
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject.2\CLSID", older.ToString("B"));
            reg.Server(lm, r32, older, oldExe, oldExe + " -Automation", "HWPFrame.HwpObject.2");
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject.3\CLSID", newer.ToString("B"));
            reg.Server(lm, r32, newer, exe, exe + " -Automation", "HWPFrame.HwpObject.3");
            c = reg.Search(Hwp);
            Check("가짜 레지스트리: 판 번호 이름표만 남았으면 새 판부터 찾음", c != null && c.Clsid == newer, c == null ? "못 찾음" : c.How);

            reg = new FakeRegistry();
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject\CLSID", older.ToString("B"));
            reg.Server(lm, r32, known, exe, exe + " -Automation", "HWPFrame.HwpObject.2");
            c = reg.Search(Hwp);
            Check("가짜 레지스트리: 이름표가 지운 판을 가리키면 알려진 고유 번호로 찾음", c != null && c.Clsid == known && c.How == "고유 번호", c == null ? "못 찾음" : c.How);

            reg = new FakeRegistry();
            reg.Server(lm, r32, known, exe, exe + " -Automation", "Other.Application");
            c = reg.Search(Hwp);
            Check("가짜 레지스트리: 고유 번호가 다른 프로그램 것이면 쓰지 않음", c == null, c == null ? "" : c.How);

            reg = new FakeRegistry();
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject\CLSID", newer.ToString("B"));
            reg.Server(lm, r32, newer, null, exe + " -Automation", null);
            c = reg.Search(Hwp);
            Check("가짜 레지스트리: 등록만 남고 프로그램 파일이 없으면 쓰지 않음", c == null, c == null ? "" : c.How);

            reg = new FakeRegistry();
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject\CurVer", "HWPFrame.HwpObject.1");
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject.1\CurVer", "HWPFrame.HwpObject");
            var sw = Stopwatch.StartNew();
            c = reg.Search(Hwp);
            Check("가짜 레지스트리: CurVer가 돌고 돌아도 멈춤", c == null && sw.ElapsedMilliseconds < 1000, sw.ElapsedMilliseconds + "ms");

            reg = new FakeRegistry();
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject\CLSID", "{엉터리}");
            reg.Set(lm, r64, @"SOFTWARE\Classes\HWPFrame.HwpObject.2\CLSID", " " + older.ToString("D") + " ");
            reg.Server(lm, r32, older, oldExe, oldExe, null);
            c = reg.Search(Hwp);
            Check("가짜 레지스트리: 엉터리 고유 번호는 건너뛰고 괄호 없는 값도 읽음", c != null && c.Clsid == older, c == null ? "못 찾음" : c.How);

            reg = new FakeRegistry();
            reg.Set(lm, r64, @"SOFTWARE\Classes\Word.Application\CLSID", newer.ToString("B"));
            reg.Server(lm, r32, newer, exe, exe, null);
            Check("가짜 레지스트리: MS 오피스는 예전 길 그대로(레지스트리로 찾지 않음)", reg.Search("Word.Application") == null, "");

            var paths = new[]
            {
                new[] { @"C:\Program Files (x86)\Hnc\Office 2022\HOffice120\bin\hwp.exe -Automation", @"C:\Program Files (x86)\Hnc\Office 2022\HOffice120\bin\hwp.exe" },
                new[] { "\"C:\\Program Files (x86)\\Hnc\\Office 2024\\HOffice130\\Bin\\HCell.exe\" -Automation", @"C:\Program Files (x86)\Hnc\Office 2024\HOffice130\Bin\HCell.exe" },
                new[] { "\"C:\\Program Files (x86)\\Hnc\\Office 2022\\HOffice120\\Bin\\hwp.exe\",0", @"C:\Program Files (x86)\Hnc\Office 2022\HOffice120\Bin\hwp.exe" },
                new[] { @"C:\Tools\a.exes\Hwp.EXE /Automation", @"C:\Tools\a.exes\Hwp.EXE" },
                new[] { @"C:\Hnc\Hwp.exe", @"C:\Hnc\Hwp.exe" },
                new[] { @"C:\Hnc\Hwp", null },
                new[] { "  ", null }
            };
            var wrong = paths.Where(p => ComFind.ExePath(p[0]) != p[1]).Select(p => p[0] + " → " + (ComFind.ExePath(p[0]) ?? "null")).ToList();
            Check("등록 값에서 프로그램 파일 경로 떼기(따옴표·뒤에 붙은 말)", wrong.Count == 0, string.Join(" / ", wrong));
            string expanded = ComFind.ExePath(@"%SystemRoot%\Hwp.exe -Automation");
            Check("등록 값의 %환경 변수%를 풂", expanded != null && !expanded.Contains("%") && expanded.EndsWith(@"\Hwp.exe"), expanded);

            Check("다시 켜 보는 실패: 등록 없음·파일 없음", ComFind.NotRegistered(unchecked((int)0x80040154)) && ComFind.NotRegistered(unchecked((int)0x80070002)), "");
            Check("다시 켜지 않는 실패: 켜다 멈춤·권한 없음", !ComFind.NotRegistered(unchecked((int)0x80080005)) && !ComFind.NotRegistered(unchecked((int)0x80070005)), "");

            var none = ComFind.MissingFor("한글", "이 PC에 한글(한컴오피스)이 없어요.", null);
            Check("안내: 한글 파일도 없으면 예전 말 그대로", !none.Installed && none.Message == "이 PC에 한글(한컴오피스)이 없어요.", none.Message);
            var hidden = ComFind.MissingFor("한글", "이 PC에 한글(한컴오피스)이 없어요.", exe);
            Check("안내: 한글은 있는데 부를 수 없으면 다시 설치하라고", hidden.Installed && hidden.Message.StartsWith("한글은 깔려 있지만") && hidden.Message.Contains("한글을 쓸 수 있게") && hidden.Message.Contains("다시 설치"), hidden.Message);
            var show = ComFind.MissingFor("한쇼", "이 PC에 한쇼가 없어요.", exe);
            Check("안내: 받침 없는 이름은 '는·를'", show.Message.StartsWith("한쇼는") && show.Message.Contains("한쇼를"), show.Message);
        }

        static void ComFindRealTests()
        {
            var normal = ComFind.Find("HWPFrame.HwpObject");
            if (normal == null)
            {
                Cli.Print("    이 PC에서 한글을 찾지 못해 나머지는 시험하지 못함");
                return;
            }
            Check("이 PC: 한글을 보통 길(이름표)로 찾음", normal.Type != null && normal.How == null, normal.Clsid.ToString("B"));
            string w = P("hncfind");
            if (Directory.Exists(w)) Directory.Delete(w, true);
            Directory.CreateDirectory(w);
            var hwpBefore = Util.PidsOf("Hwp");
            var cellBefore = Util.PidsOf("HCell");
            var showBefore = Util.PidsOf("HShow");
            string hwp = Path.Combine(w, "찾기시험.hwp");
            var make = FindJob();
            try
            {
                using (var h = new Hangul(make))
                {
                    h.InsertText("바꿈 한글 찾기 시험\r\n지어낸 안내문: 11월 20일에 운동회를 해요.");
                    h.SaveAs(hwp, "HWP");
                }
            }
            catch (Exception ex)
            {
                Check("시험용 HWP 만들기", false, ex.GetType().Name + ": " + ex.Message);
                return;
            }
            try
            {
                ComFind.TestNoProgId = true;
                var c = ComFind.Find("HWPFrame.HwpObject");
                Check("이름표를 못 찾는 PC: 레지스트리에서 찾음", c != null && c.Type == null && c.How != null && c.How.StartsWith("레지스트리"), c == null ? "못 찾음" : c.How);
                Check("그때도 '한글 있음'(AI 문서의 한글 저장 칸이 나옴)", Hangul.Installed, "");
                ReadByFallback("레지스트리로 찾은 한글로 HWP 읽기(AI 요약·글자 뽑기 길)", hwp, "한글: 레지스트리");

                ComFind.TestNoRegistry = true;
                c = ComFind.Find("HWPFrame.HwpObject");
                Check("이름표·레지스트리 이름표가 없는 PC: 고유 번호로 찾음", c != null && c.How == "고유 번호", c == null ? "못 찾음" : c.How);
                ReadByFallback("고유 번호로 찾은 한글로 HWP 읽기", hwp, "한글: 고유 번호");

                ComFind.TestNoProgId = false;
                ComFind.TestNoRegistry = false;
                ComFind.TestNoDefault = true;
                ReadByFallback("보통 켜기가 막힌 PC: 32비트 방식으로 켜서 HWP 읽기", hwp, "한글: 이름표에서 찾아 32비트 방식으로 켬");

                ComFind.TestNoProgId = true;
                StartByFallback("한셀", "HCell.Application", delegate (Job j) { return new Hancell(j); });
                StartByFallback("한쇼", "HShow.Application", delegate (Job j) { return new Hanshow(j); });

                ComFind.TestNoDefault = false;
                ComFind.TestNoRegistry = true;
                ComFind.TestNoKnown = true;
                Check("아무 데서도 못 찾으면 '한글 없음'으로 봄", !Hangul.Installed, "");
                var j1 = FindJob();
                try
                {
                    using (new Hangul(j1)) { }
                    Check("한글은 있는데 부를 수 없으면 다시 설치하라고 알림", false, "켜졌음");
                }
                catch (ProgramMissingException ex)
                {
                    Check("한글은 있는데 부를 수 없으면 다시 설치하라고 알림", ex.Installed && ex.Message.Contains("다시 설치"), ex.Message);
                }
                var j2 = FindJob();
                try
                {
                    DocRead.Text(j2, hwp);
                    Check("AI 요약·글자 뽑기는 HWPX로 저장하는 길도 알림", false, "읽혔음");
                }
                catch (UserFacingException ex)
                {
                    Check("AI 요약·글자 뽑기는 HWPX로 저장하는 길도 알림", ex.Message.Contains("다시 설치") && ex.Message.Contains("HWPX"), ex.Message);
                }
                finally
                {
                    j2.ReleaseResources();
                }
                var j3 = FindJob();
                try
                {
                    DocConvert.BlocksOf(j3, hwp);
                    Check("HWP 내용 읽기(형식 바꾸기)도 다시 설치하라고 알림", false, "읽혔음");
                }
                catch (UserFacingException ex)
                {
                    Check("HWP 내용 읽기(형식 바꾸기)도 다시 설치하라고 알림", ex.Message.Contains("다시 설치"), ex.Message);
                }
                finally
                {
                    j3.ReleaseResources();
                }
                var conv = RunJob("format", "hwpx", new[] { hwp }, null);
                Check("HWP → HWPX도 다시 설치하라고 알림", conv.Outputs.Count == 0 && conv.Problems.Any(x => x.Contains("다시 설치")), string.Join(" / ", conv.Problems));
                string log = LogText();
                string miss = LastLog(log, "한글 못 찾음:");
                Check("못 찾은 까닭을 기록(이름표·고유 번호·프로그램 파일)", miss.Contains("이름표") && miss.Contains("고유 번호") && miss.IndexOf("hwp.exe", StringComparison.OrdinalIgnoreCase) >= 0, miss);
            }
            finally
            {
                ComFind.TestNoProgId = false;
                ComFind.TestNoRegistry = false;
                ComFind.TestNoKnown = false;
                ComFind.TestNoDefault = false;
            }
            Check("길을 막지 않으면 다시 보통 길로 찾음", ComFind.Find("HWPFrame.HwpObject") != null && ComFind.Find("HWPFrame.HwpObject").How == null, "");
            System.Threading.Thread.Sleep(1500);
            var left = new List<string>();
            foreach (var p in new[] { new { Name = "Hwp", Before = hwpBefore }, new { Name = "HCell", Before = cellBefore }, new { Name = "HShow", Before = showBefore } })
            {
                var now = Util.PidsOf(p.Name);
                now.ExceptWith(p.Before);
                if (now.Count > 0) left.Add(p.Name + " " + now.Count + "개");
            }
            Check("시험이 켠 한글·한셀·한쇼가 남지 않음", left.Count == 0, string.Join(", ", left));
        }

        static Job FindJob()
        {
            return new Job(ActionOf("format"), new ActionOption("hwpx", "hwpx", ""), new List<string>()) { Headless = true, AutoYes = true };
        }

        /// 막아 둔 길을 지나 한글을 켜고 HWP 글자를 읽는다(AI 요약이 쓰는 DocRead.Text). 기록에 그 길이 남았는지도 본다.
        static void ReadByFallback(string name, string hwp, string logMark)
        {
            var j = FindJob();
            var sw = Stopwatch.StartNew();
            string text;
            try
            {
                text = DocRead.Text(j, hwp);
            }
            catch (Exception ex)
            {
                text = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                j.ReleaseResources();
            }
            string log = LogText();
            bool ok = text.Contains("운동회") && log.Contains(logMark);
            Check(name, ok, text.Replace("\r", "").Replace("\n", " / ") + ", " + sw.Elapsed.TotalSeconds.ToString("0.0") + "초 · " + LastLog(log, logMark.Split(':')[0] + ":"));
        }

        static void StartByFallback(string friendly, string progId, Func<Job, IDisposable> start)
        {
            if (ComFind.Find(progId) == null)
            {
                Cli.Print("    " + friendly + "을(를) 찾지 못해 건너뜀");
                return;
            }
            var j = FindJob();
            try
            {
                using (start(j)) { }
                string line = LastLog(LogText(), friendly + ":");
                Check(friendly + ": 레지스트리로 찾아 32비트 방식으로 켬", line.Contains(friendly + ": 레지스트리(") && line.Contains("32비트 방식으로 켬"), line);
            }
            catch (Exception ex)
            {
                Check(friendly + ": 레지스트리로 찾아 32비트 방식으로 켬", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        static string LogText()
        {
            string p = Path.Combine(AppInfo.DataDir, "log.txt");
            try { return File.Exists(p) ? File.ReadAllText(p) : ""; }
            catch (Exception) { return ""; }
        }

        /// 기록에서 mark가 든 마지막 줄
        static string LastLog(string log, string mark)
        {
            var line = log.Split('\n').LastOrDefault(l => l.Contains(mark));
            return line == null ? "(기록 없음)" : line.Trim();
        }
    }
}
