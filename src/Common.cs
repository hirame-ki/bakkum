// 프로그램 전체에서 쓰는 이름·경로·기록·작은 도우미.
// C# 5(윈도우에 든 csc.exe)로 컴파일하므로 $"", ?., nameof, => 멤버를 쓰지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Bakkum
{
    public static class AppInfo
    {
        public const string Id = "Bakkum";
        public const string Name = "바꿈";
        public const string Version = "0.3.3";
        public const string Publisher = "hirame.ki";

        /// 시험(/selftest-net) 동안만: 설정·로그인 파일을 임시 폴더에 둔다(선생님의 진짜 설정을 건드리지 않게).
        internal static string DataDirOverride;

        public static string DataDir
        {
            get
            {
                string d = DataDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Id);
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Id); }
        }

        public static string InstalledExe
        {
            get { return Path.Combine(InstallDir, Id + ".exe"); }
        }

        public static string ExePath
        {
            get { return System.Reflection.Assembly.GetEntryAssembly().Location; }
        }

        public static bool RunningInstalled
        {
            get { return string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// 문제를 찾을 때 보는 기록. 파일 경로는 남기지 않고 확장자와 이유만 남긴다(학교 문서 이름이 남지 않게).
    public static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    string p = Path.Combine(AppInfo.DataDir, "log.txt");
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    /// 사용자에게 그대로 보여 줄 수 있는 실패. 메시지는 쉬운 한국어로 쓴다.
    public class UserFacingException : Exception
    {
        public UserFacingException(string message) : base(message) { }
    }

    public static class Util
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);

        /// 탐색기와 같은 이름 순서(2가 10보다 앞).
        public static int NaturalCompare(string a, string b)
        {
            return StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b));
        }

        /// 이미 있으면 "이름 (2).확장자"처럼 비어 있는 이름을 찾는다.
        public static string UniquePath(string desired)
        {
            if (!File.Exists(desired) && !Directory.Exists(desired)) return desired;
            string dir = Path.GetDirectoryName(desired);
            string stem = Path.GetFileNameWithoutExtension(desired);
            string ext = Path.GetExtension(desired);
            for (int i = 2; i < 10000; i++)
            {
                string p = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(p) && !Directory.Exists(p)) return p;
            }
            throw new UserFacingException("저장할 이름을 정하지 못했어요.");
        }

        /// 이번 작업에서 원본 폴더 대신 문서\변환 결과에 둔 적이 있는지. 작업은 한 번에 하나씩 돌아서 하나로 충분하다.
        public static volatile bool UsedFallbackDir;

        /// 결과를 둘 폴더. 원본 폴더에 쓸 수 없으면 문서\변환 결과로 보낸다.
        public static string OutputDirFor(string source)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(source));
            if (CanWrite(dir)) return dir;
            string alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "변환 결과");
            Directory.CreateDirectory(alt);
            UsedFallbackDir = true;
            return alt;
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllBytes(probe, new byte[0]);
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// 결과 파일 경로: 원본 이름에 새 확장자, 겹치면 번호를 붙인다.
        public static string OutputFile(string source, string newExt)
        {
            string dir = OutputDirFor(source);
            return UniquePath(Path.Combine(dir, Path.GetFileNameWithoutExtension(source) + newExt));
        }

        public static string TempDir(string tag)
        {
            string d = Path.Combine(Path.GetTempPath(), AppInfo.Id, tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        public static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        /// 윈도우 명령줄 규칙에 맞춰 인자 하나를 감싼다(빈 문자열은 "").
        public static string QuoteArg(string arg)
        {
            if (arg == null) arg = "";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', slashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', slashes);
                    sb.Append(c);
                }
                slashes = 0;
            }
            sb.Append('\\', slashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static void KillTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F") { CreateNoWindow = true, UseShellExecute = false };
                using (var p = Process.Start(psi)) { p.WaitForExit(10000); }
            }
            catch { }
        }

        public static HashSet<int> PidsOf(string processName)
        {
            var set = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName(processName))
            {
                set.Add(p.Id);
                p.Dispose();
            }
            return set;
        }

        /// 결과 파일을 탐색기에서 골라 보여 준다.
        public static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select," + QuoteArg(path));
                else if (Directory.Exists(path)) Process.Start("explorer.exe", QuoteArg(path));
            }
            catch { }
        }

        /// 파일을 그 형식의 기본 프로그램으로 연다(PDF는 PDF 보기 프로그램 등).
        public static void OpenFile(string path)
        {
            try
            {
                if (File.Exists(path)) using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
            }
            catch (Exception ex) { Log.Write("결과 열기 실패: " + ex.GetType().Name); }
        }

        public static string Plural(int n, string unit)
        {
            return n + unit;
        }

        /// 다른 프로그램이 읽기까지 막아 둔 파일은 한글 등이 '암호·손상' 같은 엉뚱한 말로 실패한다.
        /// 넘기기 전에 열어 보고 알기 쉬운 말로 알린다. (한글에 열려 있는 문서는 읽기가 되므로 그대로 바꾼다.)
        public static void EnsureReadable(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
            }
            catch (PathTooLongException) { throw new UserFacingException(LongPathMessage); }
            catch (FileNotFoundException) { throw new UserFacingException("파일을 찾지 못했어요. 옮겨졌거나 지워졌는지 확인해 주세요."); }
            catch (DirectoryNotFoundException) { throw new UserFacingException("파일을 찾지 못했어요. 옮겨졌거나 지워졌는지 확인해 주세요."); }
            catch (UnauthorizedAccessException) { throw new UserFacingException("이 파일을 읽을 권한이 없어요."); }
            catch (IOException) { throw new UserFacingException("파일을 열 수 없어요. 다른 프로그램이 쓰고 있는지 확인해 주세요."); }
        }

        public const string LongPathMessage ="경로가 너무 길어서 파일을 열거나 만들 수 없어요. 파일을 바탕 화면처럼 짧은 경로로 옮긴 뒤 다시 해 주세요.";

        /// 윈도우의 옛 경로 길이 한도(파일 260자, 폴더 248자)를 넘는 경로. 바꿈과 한글은 이런 경로의 파일을 열지 못한다.
        /// 경로 함수는 이런 경로에서 오류를 내므로 글자 수로만 본다.
        public static bool TooLong(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\?\")) return false;
            int cut = path.LastIndexOf('\\');
            return path.Length >= 260 || cut >= 248;
        }

        public static string ShortName(string path)
        {
            string n = Path.GetFileName(path.TrimEnd('\\'));
            return n.Length > 34 ? n.Substring(0, 22) + "…" + n.Substring(n.Length - 10) : n;
        }
    }
}
