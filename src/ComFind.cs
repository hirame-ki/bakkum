// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki, moonsune.zip - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 자동화로 부를 한컴오피스(한글·한셀·한쇼)를 찾고 켠다.
// 바꿈은 64비트로 돌고 한컴오피스는 32비트라서 보통 길(이름표로 찾기 → 켜기)이 막힌 PC가 있다
// (2026-11-16 선생님 제보: 한글 2024·윈도우 11에서 한글이 깔려 있는데 HWP AI 요약이 '한글이 없어요'라고 함, #82).
// 보통 길로 못 찾으면 레지스트리에서 한 번 더 찾고, 켜기가 '등록 없음' 꼴로 실패하면 32비트 → 64비트 방식으로 다시 켠다.
// MS 오피스는 이 PC에 없어 시험할 수 없으므로 예전 길 그대로 둔다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Bakkum
{
    /// 찾은 자동화 프로그램
    public sealed class ComClass
    {
        public string ProgId;
        public Guid Clsid;
        /// 보통 길(이름표)로 찾았을 때만 있다
        public Type Type;
        /// 보통 길이 아니면 어디서 찾았는지(기록용)
        public string How;
    }

    /// 자동화로 부를 프로그램을 찾지 못함. Installed면 프로그램 파일은 이 PC에 있고 등록만 빠졌다.
    public sealed class ProgramMissingException : UserFacingException
    {
        public readonly bool Installed;

        public ProgramMissingException(string message, bool installed) : base(message)
        {
            Installed = installed;
        }
    }

    public static class ComFind
    {
        sealed class Family
        {
            public string ProgId;
            public string Exe;
            public string HncKey;
            public Guid[] Known;
        }

        // 고유 번호(CLSID)는 한컴오피스 2022(12.0)에서 읽었다. 판마다 다르면 이름표·프로그램 파일 검사에서 걸러져 쓰이지 않는다.
        static readonly Family[] Hancom =
        {
            new Family { ProgId = "HWPFrame.HwpObject", Exe = "Hwp.exe", HncKey = "Hwp", Known = new[] { new Guid("2291CF00-64A1-4877-A9B4-68CFE89612D6") } },
            new Family { ProgId = "HCell.Application", Exe = "HCell.exe", HncKey = "HCell", Known = new[] { new Guid("AC00E9F1-3B40-46EB-9DE9-FF7327AE2417") } },
            new Family { ProgId = "HShow.Application", Exe = "HShow.exe", HncKey = "HShow", Known = new[] { new Guid("11D90292-3D2E-49A5-BB3F-611E9C67F3A3") } }
        };

        static readonly RegistryHive[] Hives = { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        static readonly RegistryView[] Views = { RegistryView.Registry32, RegistryView.Registry64 };
        // 이름표는 32·64비트가 함께 쓰는 자리에 있다. 32비트 자리(Wow6432Node)에 적힌 이름표는 윈도우가 읽지 않지만 바꿈은 읽어 본다.
        static readonly string[] ClassRoots = { @"SOFTWARE\Classes\", @"SOFTWARE\Classes\Wow6432Node\" };

        // 시험(묶음 hncfind)에서 길을 하나씩 막아 다음 길만 지나게 한다.
        internal static bool TestNoProgId, TestNoRegistry, TestNoKnown, TestNoDefault;

        const uint LocalServer = 0x4, Activate32 = 0x40000, Activate64 = 0x80000;
        static readonly Guid IidDispatch = new Guid("00020400-0000-0000-C000-000000000046");

        [DllImport("ole32.dll", ExactSpelling = true)]
        static extern int CoCreateInstance([In] ref Guid clsid, IntPtr outer, uint context, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

        static Family FamilyOf(string progId)
        {
            return Hancom.FirstOrDefault(f => string.Equals(f.ProgId, progId, StringComparison.OrdinalIgnoreCase));
        }

        /// 자동화로 부를 프로그램을 찾는다. 없으면 null.
        public static ComClass Find(string progId)
        {
            Type t = TestNoProgId ? null : Type.GetTypeFromProgID(progId);
            if (t != null) return new ComClass { ProgId = progId, Clsid = t.GUID, Type = t };
            return Search(progId, RealRead, File.Exists);
        }

        /// 보통 길로 못 찾은 한컴오피스를 레지스트리에서 찾는다.
        /// 1) 이름표: 사용자·PC 등록, 32비트 자리, CurVer, 판 번호가 붙은 이름표(새 판부터) 2) 알려진 고유 번호.
        /// 프로그램 파일이 실제로 있는 등록만 쓴다(지운 판이 남긴 등록을 피한다).
        internal static ComClass Search(string progId, Func<RegistryHive, RegistryView, string, string> read, Func<string, bool> exists)
        {
            var fam = FamilyOf(progId);
            if (fam == null) return null;
            if (!TestNoRegistry)
            {
                var names = new List<string> { fam.ProgId };
                for (int n = 20; n >= 1; n--) names.Add(fam.ProgId + "." + n);
                foreach (string name in names)
                {
                    // 사용자 등록에 지운 판의 이름표가 남아 PC 등록을 가려도 다음 것을 본다.
                    foreach (var found in ClsidsOf(name, read))
                    {
                        if (ServerOf(found.Key, read, exists) != null)
                            return new ComClass { ProgId = fam.ProgId, Clsid = found.Key, How = "레지스트리(" + found.Value + ")" };
                    }
                }
            }
            if (!TestNoKnown)
            {
                foreach (Guid g in fam.Known)
                {
                    if (ServerOf(g, read, exists) != null && Belongs(g, fam.ProgId, read))
                        return new ComClass { ProgId = fam.ProgId, Clsid = g, How = "고유 번호" };
                }
            }
            return null;
        }

        /// 이름표가 가리키는 고유 번호와 그 자리. 사용자 등록 → PC 등록, 함께 쓰는 자리 → 32비트 자리 차례로 모두 모은다.
        /// CurVer는 세 번까지 따라간다(돌고 도는 등록에서 멈추게).
        internal static List<KeyValuePair<Guid, string>> ClsidsOf(string progId, Func<RegistryHive, RegistryView, string, string> read)
        {
            var list = new List<KeyValuePair<Guid, string>>();
            foreach (var hive in Hives)
            {
                foreach (string root in ClassRoots)
                {
                    string id = progId;
                    for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(id); hop++)
                    {
                        Guid g;
                        if (TryGuid(read(hive, RegistryView.Registry64, root + id + @"\CLSID"), out g))
                        {
                            list.Add(new KeyValuePair<Guid, string>(g, (hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM") + @"\" + root + id));
                            break;
                        }
                        string next = read(hive, RegistryView.Registry64, root + id + @"\CurVer");
                        next = next == null ? null : next.Trim();
                        if (string.Equals(next, id, StringComparison.OrdinalIgnoreCase)) break;
                        id = next;
                    }
                }
            }
            return list;
        }

        /// 고유 번호에 등록된 프로그램 파일(LocalServer32). 사용자·PC 등록, 32·64비트 자리를 다 본다. 파일이 없으면 null.
        internal static string ServerOf(Guid clsid, Func<RegistryHive, RegistryView, string, string> read, Func<string, bool> exists)
        {
            string key = @"SOFTWARE\Classes\CLSID\" + clsid.ToString("B") + @"\LocalServer32";
            foreach (var hive in Hives)
            {
                foreach (var view in Views)
                {
                    string exe = ExePath(read(hive, view, key));
                    if (exe != null && exists(exe)) return exe;
                }
            }
            return null;
        }

        /// 고유 번호의 이름표가 그 프로그램의 것인가('HWPFrame.HwpObject', 'HWPFrame.HwpObject.2' 등)
        static bool Belongs(Guid clsid, string progId, Func<RegistryHive, RegistryView, string, string> read)
        {
            string key = @"SOFTWARE\Classes\CLSID\" + clsid.ToString("B") + @"\";
            foreach (var hive in Hives)
            {
                foreach (var view in Views)
                {
                    foreach (string sub in new[] { "ProgID", "VersionIndependentProgID" })
                    {
                        string v = read(hive, view, key + sub);
                        if (v == null) continue;
                        v = v.Trim();
                        if (string.Equals(v, progId, StringComparison.OrdinalIgnoreCase) || v.StartsWith(progId + ".", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            return false;
        }

        /// 등록 값에서 프로그램 파일 경로만 뗀다. 따옴표가 있을 수도 없을 수도 있고, 뒤에 ' -Automation'·',0' 같은 것이 붙는다.
        internal static string ExePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string s = Environment.ExpandEnvironmentVariables(command.Trim());
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                return end > 1 ? s.Substring(1, end - 1) : null;
            }
            int from = 0;
            while (true)
            {
                int at = s.IndexOf(".exe", from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return null;
                int after = at + 4;
                if (after == s.Length || char.IsWhiteSpace(s[after]) || s[after] == ',' || s[after] == '"') return s.Substring(0, after);
                from = after;
            }
        }

        static bool TryGuid(string s, out Guid g)
        {
            g = Guid.Empty;
            return s != null && Guid.TryParse(s.Trim(), out g) && g != Guid.Empty;
        }

        static string RealRead(RegistryHive hive, RegistryView view, string path)
        {
            try
            {
                using (var b = RegistryKey.OpenBaseKey(hive, view))
                using (var k = b.OpenSubKey(path))
                {
                    return k == null ? null : k.GetValue("") as string;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// 찾은 프로그램을 켠다. 한컴오피스는 보통 켜기가 '등록 없음' 꼴로 실패하면 32비트 → 64비트 방식으로 다시 켜고,
        /// 이름표가 옛 판의 고유 번호를 가리키면(지운 판이 남긴 등록) 레지스트리에서 찾은 다른 등록으로 켠다.
        /// 켜다가 멈춘 것(시간 초과 등)은 다시 켜지 않는다(프로그램이 둘 뜨지 않게).
        public static object Create(ComClass c, string friendly)
        {
            bool hancom = FamilyOf(c.ProgId) != null;
            var tried = new List<string>();
            string way;
            int last;
            object o = TryCreate(c, hancom, friendly, tried, out way, out last);
            if (o == null && c.Type != null && !TestNoRegistry)
            {
                var other = Search(c.ProgId, RealRead, File.Exists);
                if (other != null && other.Clsid != c.Clsid)
                {
                    c = other;
                    o = TryCreate(c, hancom, friendly, tried, out way, out last);
                }
            }
            if (o == null)
            {
                Log.Write(friendly + " 켜기 실패: " + string.Join(", ", tried) + " · " + (c.How ?? "이름표"));
                throw new COMException(friendly + " 켜기 실패", last);
            }
            if (c.How != null || way != "보통")
                Log.Write(friendly + ": " + (c.How ?? "이름표") + "에서 찾아 " + way + " 방식으로 켬" + (tried.Count > 0 ? " (" + string.Join(", ", tried) + ")" : ""));
            return o;
        }

        /// 보통 → 32비트 → 64비트 방식으로 켜 본다. 모두 '등록 없음' 꼴로 실패하면 null, 다른 실패는 그대로 던진다.
        static object TryCreate(ComClass c, bool hancom, string friendly, List<string> tried, out string way, out int last)
        {
            way = "보통";
            last = 0;
            if (!(TestNoDefault && hancom))
            {
                try
                {
                    return Activator.CreateInstance(c.Type ?? Type.GetTypeFromCLSID(c.Clsid, true));
                }
                catch (Exception ex)
                {
                    if (!hancom || !NotRegistered(ex.HResult)) throw;
                    tried.Add("보통 " + Hex(ex.HResult));
                    last = ex.HResult;
                }
            }
            foreach (uint bits in new[] { Activate32, Activate64 })
            {
                way = bits == Activate32 ? "32비트" : "64비트";
                Guid clsid = c.Clsid;
                Guid iid = IidDispatch;
                object o;
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, LocalServer | bits, ref iid, out o);
                if (hr >= 0 && o != null) return o;
                tried.Add(way + " " + Hex(hr));
                last = hr;
                if (!NotRegistered(hr))
                {
                    Log.Write(friendly + " 켜기 실패: " + string.Join(", ", tried) + " · " + (c.How ?? "이름표"));
                    throw new COMException(friendly + " 켜기 실패", hr);
                }
            }
            return null;
        }

        /// '등록이 없다·프로그램 파일이 없다' 꼴의 실패인가. 이때만 다른 방식으로 다시 켠다.
        internal static bool NotRegistered(int hr)
        {
            switch (unchecked((uint)hr))
            {
                case 0x80040154: // REGDB_E_CLASSNOTREG
                case 0x80040111: // CLASS_E_CLASSNOTAVAILABLE
                case 0x800401F5: // CO_E_APPNOTFOUND
                case 0x80070002: // 파일 없음
                case 0x80070003: // 경로 없음
                case 0x800700C1: // 실행 파일 꼴이 아님
                    return true;
            }
            return false;
        }

        public static string Hex(int hr)
        {
            return unchecked((uint)hr).ToString("X8");
        }

        /// 프로그램을 찾지 못했을 때 알릴 말. 프로그램 파일은 있는데 자동화로 부를 수 없으면 다시 설치하라고 한다.
        public static ProgramMissingException Missing(string progId, string friendly, string notInstalled)
        {
            var fam = FamilyOf(progId);
            return MissingFor(friendly, notInstalled, fam == null ? null : InstalledExe(fam));
        }

        internal static ProgramMissingException MissingFor(string friendly, string notInstalled, string exe)
        {
            if (exe == null) return new ProgramMissingException(notInstalled, false);
            return new ProgramMissingException(friendly + (Batchim(friendly) ? "은" : "는") + " 깔려 있지만, 다른 프로그램이 " + friendly + (Batchim(friendly) ? "을" : "를") +
                " 쓸 수 있게 하는 등록이 빠져 있어요. 한컴오피스를 다시 설치해 주세요.", true);
        }

        static bool Batchim(string word)
        {
            char ch = word[word.Length - 1];
            return ch >= 0xAC00 && ch <= 0xD7A3 && (ch - 0xAC00) % 28 != 0;
        }

        /// 한컴오피스 프로그램 파일이 이 PC에 있는가(자동화 등록과 따로 본다). 레지스트리의 HNC 정보, 없으면 Program Files.
        static string InstalledExe(Family fam)
        {
            foreach (var view in Views)
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = b.OpenSubKey(@"SOFTWARE\HNC\" + fam.HncKey))
                    {
                        if (k == null) continue;
                        foreach (string ver in k.GetSubKeyNames())
                        {
                            using (var cap = k.OpenSubKey(ver + @"\Capabilities"))
                            {
                                string exe = cap == null ? null : ExePath(cap.GetValue("ApplicationIcon") as string);
                                if (exe != null && File.Exists(exe)) return exe;
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
            foreach (var pf in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                try
                {
                    string hnc = Path.Combine(Environment.GetFolderPath(pf), "Hnc");
                    if (!Directory.Exists(hnc)) continue;
                    foreach (string office in Directory.GetDirectories(hnc, "Office*"))
                    {
                        foreach (string h in Directory.GetDirectories(office, "HOffice*"))
                        {
                            string exe = Path.Combine(h, "Bin", fam.Exe);
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        /// 찾지 못했을 때 무엇을 봤는지 기록한다(다음 제보 때 까닭을 알 수 있게). 경로는 끝 세 마디만 남긴다.
        public static void LogMissing(string progId, string friendly)
        {
            var fam = FamilyOf(progId);
            if (fam == null) return;
            try
            {
                var parts = new List<string>();
                parts.Add((Environment.Is64BitProcess ? "64" : "32") + "비트 바꿈");
                var names = ClsidsOf(fam.ProgId, RealRead);
                if (names.Count == 0) parts.Add("이름표 없음");
                foreach (var found in names)
                    parts.Add("이름표 " + found.Value + " → " + found.Key.ToString("B") + (ServerOf(found.Key, RealRead, File.Exists) == null ? " (프로그램 등록 없음)" : ""));
                foreach (Guid k in fam.Known)
                {
                    string cmd = null;
                    foreach (var hive in Hives)
                    {
                        foreach (var view in Views)
                        {
                            if (cmd == null) cmd = RealRead(hive, view, @"SOFTWARE\Classes\CLSID\" + k.ToString("B") + @"\LocalServer32");
                        }
                    }
                    string exe = ExePath(cmd);
                    parts.Add("고유 번호 " + (exe == null ? "등록 없음" : Short(exe) + (File.Exists(exe) ? "" : " (파일 없음)")));
                }
                string installed = InstalledExe(fam);
                parts.Add(installed == null ? "프로그램 파일 못 찾음" : "프로그램 파일 " + Short(installed));
                Log.Write(friendly + " 못 찾음: " + string.Join(" · ", parts));
            }
            catch (Exception ex)
            {
                Log.Write(friendly + " 못 찾음(살피다 " + ex.GetType().Name + ")");
            }
        }

        /// 경로의 끝 세 마디(예: HOffice130\Bin\Hwp.exe). 기록에 사용자 폴더 이름이 남지 않게.
        static string Short(string path)
        {
            var parts = path.Split('\\');
            return string.Join("\\", parts.Skip(Math.Max(0, parts.Length - 3)));
        }
    }
}
