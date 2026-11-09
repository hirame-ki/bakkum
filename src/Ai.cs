// AI 연결: 선생님 본인의 Claude 구독(이 PC의 Claude Code), ChatGPT 요금제(OpenAI 로그인), Gemini 키, OpenRouter 무료 모델.
// 비밀번호는 받지 않는다. Claude 로그인은 Claude Code 자체 화면에서, ChatGPT·OpenRouter 로그인은 각 회사의 브라우저 화면에서 한다.
// ChatGPT는 ChatGpt.cs, OpenRouter는 OpenRouter.cs, 인터넷 요청은 Web.cs.
// Claude Code를 부르는 방식과 그 근거(공식 문서)는 아래 ClaudeCode 설명에 있다.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bakkum
{
    public enum Consent { Send, Local, Cancel }

    /// 아주 작은 JSON 도우미. 쓰기는 ASCII만 쓰도록(한글은 \uXXXX) 해서 인코딩 문제를 없앤다.
    static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static Dictionary<string, object> Parse(string s)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
            return ser.DeserializeObject(s) as Dictionary<string, object>;
        }

        public static Dictionary<string, object> Obj(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static object Get(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v : null;
        }

        public static string S(Dictionary<string, object> d, string k)
        {
            return Get(d, k) as string;
        }

        public static object[] A(Dictionary<string, object> d, string k)
        {
            return Get(d, k) as object[];
        }

        public static Dictionary<string, object> D(Dictionary<string, object> d, string k)
        {
            return Get(d, k) as Dictionary<string, object>;
        }

        public static bool B(Dictionary<string, object> d, string k)
        {
            var v = Get(d, k);
            return v is bool && (bool)v;
        }

        public static int I(Dictionary<string, object> d, string k)
        {
            var v = Get(d, k);
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            if (v is decimal) return (int)(decimal)v;
            if (v is double) return (int)(double)v;
            return 0;
        }
    }

    public static class Ai
    {
        public const string ClaudeId = "claude-code";
        public const string ChatGptId = "chatgpt";
        public const string GeminiId = "gemini-key";
        public const string OpenRouterId = "openrouter";

        /// 글자 뽑기 메뉴에 'AI로'를 보일지
        public static bool Configured
        {
            get
            {
                var s = AppSettings.Current;
                if (s.AiProvider == ClaudeId) return ClaudeCode.FindExe() != null;
                if (s.AiProvider == ChatGptId) return ChatGpt.SignedIn;
                if (s.AiProvider == GeminiId) return !string.IsNullOrEmpty(s.GeminiKey);
                if (s.AiProvider == OpenRouterId) return !string.IsNullOrEmpty(s.OpenRouterKey);
                return false;
            }
        }

        public static bool IsClaude
        {
            get { return AppSettings.Current.AiProvider == ClaudeId; }
        }

        /// 연결마다 정해진 색(DESIGN.md): 쓰지 않음=회청, Claude=주황, ChatGPT=초록, Gemini=파랑, OpenRouter=보라
        public static Hue HueOf(string provider)
        {
            if (provider == ClaudeId) return Theme.Orange;
            if (provider == ChatGptId) return Theme.Green;
            if (provider == GeminiId) return Theme.Blue;
            if (provider == OpenRouterId) return Theme.Purple;
            return Theme.Slate;
        }

        /// 연결 이름(화면에 보일 이름)
        public static string NameOf(string provider)
        {
            if (provider == ClaudeId) return "Claude";
            if (provider == ChatGptId) return "ChatGPT";
            if (provider == GeminiId) return "Gemini";
            if (provider == OpenRouterId) return "OpenRouter";
            return "AI";
        }

        const string SystemPrompt =
            "You are the text-recognition step of a file conversion program running on a teacher's own PC. " +
            "You have no tools of your own: you cannot read or change files, run commands, browse the web or call any tool, and you must not try. " +
            "You receive images of document pages and reply with the text on them, nothing else.";

        static string Prompt(int first, int last)
        {
            string which = first == last
                ? "The image above is page " + first + " of a document."
                : "The images above are pages " + first + " to " + last + " of one document, in order.";
            return which + " Transcribe all text on them, printed or handwritten, exactly as written, in the document's own language (usually Korean). " +
                "Do not translate, summarize, correct, explain or add anything.\n\n" +
                "Format:\n" +
                "- Start each page with a line <!-- p. N --> where N is the page number (" + first + " for the first image).\n" +
                "- Use Markdown: # headings for titles, - for list items, and Markdown tables for tables. " +
                "For merged cells, write the text once in the first cell and leave the covered cells empty; use <br> for line breaks inside a cell.\n" +
                "- Keep the reading order of the page. Keep numbers, symbols and blanks such as ( ) or ____ as they appear.\n" +
                "- Write [?] for characters you cannot read. A page without text gets only its marker line.\n\n" +
                "Reply with the transcription only, with no introduction or closing remarks.";
        }

        /// 보내기 전에: 연결이 쓸 수 있는 상태인지(로그인·요금 방식) 확인한다.
        public static void EnsureReady(Job job)
        {
            var s = AppSettings.Current;
            if (s.AiProvider == ClaudeId)
            {
                job.Report("Claude 연결 확인 중", -1);
                var st = ClaudeCode.Status();
                if (!st.Found) throw new UserFacingException("이 PC에서 Claude Code를 찾지 못했어요. 설정 → AI 연결을 확인해 주세요.");
                if (st.Problem != null && !st.LoggedIn) throw new UserFacingException(st.Problem);
                if (!st.LoggedIn) throw new UserFacingException("Claude에 로그인되어 있지 않아요. 설정 → AI 연결에서 [로그인 창 열기]를 눌러 로그인해 주세요.");
                if (!st.Subscription && !s.ClaudeAllowMetered)
                {
                    throw new UserFacingException("Claude Code가 구독이 아닌 '쓴 만큼 요금' 방식으로 로그인되어 있어요. 요금이 나가도 쓰려면 설정 → AI 연결에서 '그래도 사용'을 켜 주세요.");
                }
                return;
            }
            if (s.AiProvider == ChatGptId)
            {
                if (!ChatGpt.SignedIn) throw new UserFacingException("ChatGPT에 로그인되어 있지 않아요. 설정 → AI 연결에서 [ChatGPT로 계속하기]를 눌러 로그인해 주세요.");
                return;
            }
            if (s.AiProvider == GeminiId)
            {
                if (string.IsNullOrEmpty(s.GeminiKey)) throw new UserFacingException("Gemini 키가 없어요. 설정 → AI 연결에서 키를 넣어 주세요.");
                return;
            }
            if (s.AiProvider == OpenRouterId)
            {
                if (string.IsNullOrEmpty(s.OpenRouterKey)) throw new UserFacingException("OpenRouter에 연결되어 있지 않아요. 설정 → AI 연결에서 연결해 주세요.");
                return;
            }
            throw new UserFacingException("AI 연결이 꺼져 있어요. 설정 → AI 연결에서 골라 주세요.");
        }

        /// 보내기 동의(작업마다 한 번). 시험 모드는 /yes일 때만 보낸다.
        public static Consent AskConsent(Job job, int pictures)
        {
            return job.Ask(() => ConsentWindow.AskUser(pictures), job.AutoYes ? Consent.Send : Consent.Cancel);
        }

        /// 쪽 그림(JPEG) 몇 장 → 마크다운 글자. first는 첫 그림의 쪽 번호.
        public static string Transcribe(IList<byte[]> jpegs, int first, Job job)
        {
            string prompt = Prompt(first, first + jpegs.Count - 1);
            bool signedIn = IsClaude || AppSettings.Current.AiProvider == ChatGptId;
            string answer = Ask(SystemPrompt, prompt, jpegs, TimeSpan.FromSeconds((signedIn ? 240 : 180) + 30 * jpegs.Count), job);
            answer = StripFence(answer).Trim();
            if (!answer.StartsWith("<!--")) answer = "<!-- p. " + first + " -->\n\n" + answer;
            return answer;
        }

        /// 글과 쪽 그림을 보내고 답을 받는다. 어느 연결이든 같은 모양이다.
        public static string Ask(string system, string prompt, IList<byte[]> jpegs, TimeSpan limit, Job job)
        {
            var s = AppSettings.Current;
            if (s.AiProvider == ClaudeId) return ClaudeCode.Run(system, prompt, jpegs, limit, job);
            if (s.AiProvider == ChatGptId) return ChatGpt.Run(system, prompt, jpegs, limit, job);
            if (s.AiProvider == OpenRouterId) return OpenRouter.Run(s.OpenRouterKey, system, prompt, jpegs, limit, job);
            return Gemini.Run(s.GeminiKey, system, prompt, jpegs, limit, job);
        }

        internal static string StripFence(string s)
        {
            string t = s.Trim();
            if (!t.StartsWith("```")) return s;
            int nl = t.IndexOf('\n');
            if (nl < 0) return s;
            t = t.Substring(nl + 1);
            if (t.TrimEnd().EndsWith("```")) t = t.TrimEnd().Substring(0, t.TrimEnd().Length - 3);
            return t;
        }
    }

    // ── Claude Code ──
    // 근거: Claude Code 공식 문서(code.claude.com/docs/en/headless · cli-reference · env-vars · setup, 2026-11-08 읽음)와
    //       이 PC의 Claude Code 2.1.311 'claude --help'·'claude auth --help'.
    //  - 한 번 묻고 끝나는 print 모드(-p). 입력·출력은 stream-json(한 줄에 JSON 하나)이고, 이때는 --verbose가 있어야 한다.
    //  - 도구는 하나도 주지 않는다: --tools ""(내장 도구 끔), --disallowedTools "*"(모든 도구 뺌), --strict-mcp-config(MCP 서버 없음),
    //    --restricted(명령·코드 실행 도구와 웹 가져오기를 빼고 사용자·프로젝트 설정을 읽지 않음, 2.1.248부터),
    //    --permission-prompts none(권한 질문에 답할 사람이 없으니 거절, 2.1.259부터).
    //  - 남기지 않는다: --no-session-persistence(대화를 디스크에 저장하지 않음), --disable-slash-commands(스킬·명령 끔).
    //  - 빈 임시 폴더에서 돌리고, 자동 업데이트·원격 측정 같은 부수 통신은 끈다(CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1).
    //  - 입력은 한 줄(쪽 그림들 + 질문)을 쓰고 바로 닫는다. 차례가 끝났고 입력이 닫혀 있으면 실행이 끝난다.
    //  - 시작 알림(system/init)에 도구나 MCP 서버가 보이거나, 도구를 쓰려 하거나, 권한 질문·거절이 보이면 그 자리에서 끈다.
    //  - 로그인 상태는 'claude auth status'(JSON, 로그인이면 종료 코드 0)의 authMethod로 본다:
    //    claude.ai·oauth_token은 구독, api_key·api_key_helper·third_party는 쓴 만큼 내는 요금.
    public static class ClaudeCode
    {
        public class StatusInfo
        {
            public bool Found;        // 이 PC에 Claude Code가 있다
            public bool LoggedIn;     // 로그인돼 있다
            public bool Subscription; // 구독(Pro·Max)으로 로그인돼 있다
            public string Problem;    // 상태를 읽지 못했으면 그 까닭(쉬운 말)
        }

        const string Outdated = "이 PC의 Claude Code가 오래된 버전이에요. PowerShell에서 claude update 를 실행한 뒤 다시 해 주세요.";
        static readonly Regex OldVersion = new Regex(@"unknown option|unknown command|unknown argument", RegexOptions.IgnoreCase);
        static readonly Regex SignedOut = new Regex(@"/login|not logged in|log ?in again|invalid api key|authentication|unauthori[sz]ed|oauth token|\b401\b", RegexOptions.IgnoreCase);
        static readonly Regex LimitHit = new Regex(@"usage limit|limit reached|hit your limit|rate.?limit|\b429\b", RegexOptions.IgnoreCase);
        static readonly Regex Busy = new Regex(@"overloaded|\b529\b|\b50[023]\b|server error", RegexOptions.IgnoreCase);

        static string testExe;
        static readonly object statusGate = new object();
        static volatile StatusInfo lastStatus;
        static DateTime lastStatusAt;

        /// 자체 시험: 가짜 Claude Code(tools\mock_claude.py를 부르는 .cmd)를 쓴다. null이면 원래대로.
        public static void UseTestExe(string path)
        {
            testExe = path;
            Forget();
        }

        /// 공식 설치 위치(%USERPROFILE%\.local\bin\claude.exe) → PATH(이 프로그램·사용자·컴퓨터) → npm 기본 위치 순서로 찾는다. 없으면 null.
        public static string FindExe()
        {
            if (testExe != null) return File.Exists(testExe) ? testExe : null;
            var places = new List<string>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) places.Add(Path.Combine(home, ".local", "bin", "claude.exe"));
            foreach (string dir in PathDirs())
            {
                places.Add(Path.Combine(dir, "claude.exe"));
                places.Add(Path.Combine(dir, "claude.cmd"));
            }
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(roaming)) places.Add(Path.Combine(roaming, "npm", "claude.cmd"));
            foreach (string place in places)
            {
                try { if (File.Exists(place)) return place; }
                catch { }
            }
            return null;
        }

        /// PATH의 폴더들. 설치한 직후에는 이 프로그램이 받은 PATH가 옛것일 수 있어 사용자·컴퓨터 PATH도 읽는다. 상대 경로는 쓰지 않는다.
        static List<string> PathDirs()
        {
            var dirs = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in new[] { EnvPath(null), EnvPath(EnvironmentVariableTarget.User), EnvPath(EnvironmentVariableTarget.Machine) })
            {
                if (string.IsNullOrEmpty(source)) continue;
                foreach (string part in Environment.ExpandEnvironmentVariables(source).Split(';'))
                {
                    string dir = part.Trim().Trim('"');
                    if (dir.Length == 0 || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                    try { if (!Path.IsPathRooted(dir)) continue; }
                    catch { continue; }
                    if (seen.Add(dir)) dirs.Add(dir);
                }
            }
            return dirs;
        }

        static string EnvPath(EnvironmentVariableTarget? target)
        {
            try { return target == null ? Environment.GetEnvironmentVariable("PATH") : Environment.GetEnvironmentVariable("PATH", target.Value); }
            catch { return null; }
        }

        /// 로그인 상태(5분 동안 기억한다). 설정 창의 [다시 확인]은 Forget() 뒤에 다시 부른다.
        public static StatusInfo Status()
        {
            lock (statusGate)
            {
                var known = lastStatus;
                if (known != null && DateTime.UtcNow - lastStatusAt < TimeSpan.FromMinutes(5)) return known;
                var st = ReadStatus();
                lastStatusAt = DateTime.UtcNow;
                lastStatus = st;
                return st;
            }
        }

        public static void Forget()
        {
            lastStatus = null;
        }

        static StatusInfo ReadStatus()
        {
            var st = new StatusInfo();
            string exe = FindExe();
            if (exe == null) return st;
            st.Found = true;
            Captured c;
            try { c = Capture(exe, "auth status --json", TimeSpan.FromSeconds(30)); }
            catch (Exception ex)
            {
                Log.Write("Claude 상태 확인 실행 실패: " + ex.GetType().Name);
                st.Problem = "Claude Code를 실행하지 못했어요. [다시 확인]을 눌러 보세요.";
                return st;
            }
            if (c.TimedOut)
            {
                st.Problem = "Claude Code가 답하지 않아요. 잠시 뒤 [다시 확인]을 눌러 주세요.";
                return st;
            }
            var d = JsonIn(c.Out);
            if (d == null)
            {
                Log.Write("Claude 상태를 읽지 못함: 종료 코드 " + c.Exit);
                st.Problem = OldVersion.IsMatch(c.Err + "\n" + c.Out) ? Outdated : "Claude Code의 로그인 상태를 읽지 못했어요. [다시 확인]을 눌러 보세요.";
                return st;
            }
            string method = Json.S(d, "authMethod") ?? "";
            object flag = Json.Get(d, "loggedIn");
            bool saysOut = flag is bool && !(bool)flag;
            st.LoggedIn = c.Exit == 0 && !saysOut && method != "none";
            st.Subscription = st.LoggedIn && (method == "claude.ai" || method == "oauth_token");
            return st;
        }

        /// 출력에서 JSON 덩어리 하나를 꺼낸다(앞뒤에 다른 글이 붙어 있어도).
        static Dictionary<string, object> JsonIn(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int a = s.IndexOf('{'), b = s.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            try { return Json.Parse(s.Substring(a, b - a + 1)); }
            catch { return null; }
        }

        /// 로그인은 Claude Code 자체 화면에서 한다: 'claude auth login --claudeai'(구독으로 로그인)를 새 명령 창으로 연다.
        public static bool OpenLogin()
        {
            string exe = FindExe();
            if (exe == null) return false;
            try
            {
                var psi = Launch(exe, "auth login --claudeai");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = false;
                psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                using (Process.Start(psi)) { }
                Forget();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Claude 로그인 창 열기 실패: " + ex.GetType().Name);
                return false;
            }
        }

        /// 쪽 그림(JPEG)과 질문을 보내고 답 글을 받는다. limit 안에 끝나지 않거나 작업을 취소하면 끈다.
        public static string Run(string system, string prompt, IList<byte[]> jpegs, TimeSpan limit, Job job)
        {
            job.Check();
            string exe = FindExe();
            if (exe == null) throw new UserFacingException("이 PC에서 Claude Code를 찾지 못했어요. 설정 → AI 연결을 확인해 주세요.");
            // npm으로 깐 claude.cmd는 명령 창을 거친다. 안내 글은 우리 상수라 아래 글자가 없지만, 생기면 바로 알 수 있게 막아 둔다.
            if (IsScript(exe) && system.IndexOfAny(new[] { '%', '"', '\r', '\n' }) >= 0)
                throw new InvalidOperationException("안내 글에 명령 창이 바꿔 읽는 글자가 있어요.");
            string args =
                "-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages" +
                " --tools \"\" --disallowedTools \"*\" --strict-mcp-config --restricted --permission-prompts none" +
                " --no-session-persistence --disable-slash-commands --model sonnet" +
                " --system-prompt " + Util.QuoteArg(system);
            string line = UserLine(prompt, jpegs) + "\n";
            string work = Path.Combine(Path.GetTempPath(), "bakkum-claude-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            var r = new Reading();
            string stopped = null;
            int exit = -1;
            Directory.CreateDirectory(work);
            try
            {
                job.Report("Claude 준비 중", -1);
                InputWithoutBom();
                using (var p = Process.Start(StartInfo(exe, args, true, work)))
                {
                    var readOut = Pump(p.StandardOutput, r.Take);
                    var readErr = Pump(p.StandardError, r.TakeError);
                    // 입력이 커도(그림 여러 장) 기다리는 동안 시간·취소를 볼 수 있게 따로 쓴다.
                    var write = new Thread(delegate ()
                    {
                        try
                        {
                            p.StandardInput.Write(line);
                            p.StandardInput.Close();
                        }
                        catch (Exception ex) { r.InputFailed = ex.GetType().Name; }
                    });
                    write.IsBackground = true;
                    write.Start();

                    var began = DateTime.UtcNow;
                    var shown = DateTime.MinValue;
                    while (!p.WaitForExit(200))
                    {
                        var now = DateTime.UtcNow;
                        if (job.Cancelled) stopped = "cancel";
                        else if (r.Unsafe != null) stopped = "unsafe";
                        else if (now - began > limit) stopped = "timeout";
                        else if (r.Done && now - r.DoneAt > TimeSpan.FromSeconds(10)) stopped = "lingering";   // 답은 왔는데 끝나지 않음
                        if (stopped != null)
                        {
                            Util.KillTree(p.Id);
                            p.WaitForExit(5000);
                            break;
                        }
                        if (now - shown > TimeSpan.FromSeconds(1))
                        {
                            shown = now;
                            int n = r.Received;
                            job.Report(n > 0 ? "Claude가 읽는 중 · " + n.ToString("N0") + "자" : "Claude가 읽는 중", -1);
                        }
                    }
                    readOut.Join(5000);
                    readErr.Join(5000);
                    write.Join(1000);
                    if (p.HasExited) exit = p.ExitCode;
                }
            }
            finally
            {
                Util.TryDeleteDir(work);
            }
            job.Check();
            return Outcome(r, stopped, exit, limit);
        }

        static string Outcome(Reading r, string stopped, int exit, TimeSpan limit)
        {
            if (r.Unsafe != null)
            {
                Log.Write("Claude Code를 멈춤: " + r.Unsafe);
                if (r.Unsafe.StartsWith("init"))
                    throw new UserFacingException("Claude Code가 도구를 모두 끈 상태로 시작하지 않아 바로 멈췄어요. PowerShell에서 claude update 를 실행한 뒤 다시 해 주세요.");
                throw new UserFacingException("Claude가 이 프로그램이 허락하지 않은 일(도구 쓰기)을 하려 해서 바로 멈췄어요. 다시 해도 같으면 다른 AI 연결을 써 주세요.");
            }
            if (stopped == "timeout")
                throw new UserFacingException("Claude가 " + (int)Math.Ceiling(limit.TotalMinutes) + "분 안에 답을 끝내지 못해 멈췄어요. 쪽 수를 줄이거나 잠시 뒤 다시 해 보세요.");
            if (r.Done && !r.IsError && (r.Subtype == null || r.Subtype == "success"))
            {
                string text = string.IsNullOrWhiteSpace(r.Result) ? r.LastText : r.Result;
                if (!string.IsNullOrWhiteSpace(text)) return text;
                throw new UserFacingException("Claude가 빈 답을 보냈어요. 다시 해 보세요.");
            }
            string said = (r.Result ?? "") + "\n" + r.Errors;
            if (OldVersion.IsMatch(said)) throw new UserFacingException(Outdated);
            if (SignedOut.IsMatch(said))
            {
                Forget();
                throw new UserFacingException("Claude 로그인이 필요해요. 설정 → AI 연결에서 [로그인 창 열기]를 눌러 다시 로그인해 주세요.");
            }
            if (LimitHit.IsMatch(said)) throw new UserFacingException("Claude 사용 한도에 닿았어요. 한도가 다시 채워진 뒤 해 주세요(남은 양은 claude.ai 설정의 사용량에서 볼 수 있어요).");
            if (Busy.IsMatch(said)) throw new UserFacingException("지금 Claude 쪽이 붐벼요. 잠시 뒤 다시 해 보세요.");
            Log.Write("Claude Code 실패: 종료 코드 " + exit + " · " + (r.Done ? "result " + (r.Subtype ?? "?") : "result 없음") +
                (r.InputFailed != null ? " · 입력 " + r.InputFailed : "") + " · " + Clip(said, 200));
            throw new UserFacingException("Claude Code가 답을 주지 못하고 끝났어요. 잠시 뒤 다시 해 보세요.");
        }

        static string Clip(string s, int max)
        {
            s = (s ?? "").Replace("\r", "").Replace("\n", " / ").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        /// stream-json 입력 한 줄: 쪽 그림들 다음에 질문 글. JSON은 ASCII로만 쓴다(Json.Str이 한글을 \uXXXX로 바꾼다).
        static string UserLine(string prompt, IList<byte[]> jpegs)
        {
            var sb = new StringBuilder("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[");
            foreach (byte[] jpg in jpegs)
            {
                sb.Append("{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/jpeg\",\"data\":\"");
                sb.Append(Convert.ToBase64String(jpg));
                sb.Append("\"}},");
            }
            sb.Append("{\"type\":\"text\",\"text\":").Append(Json.Str(prompt)).Append("}]},\"parent_tool_use_id\":null}");
            return sb.ToString();
        }

        /// .NET Framework의 Process는 표준 입력을 Console.InputEncoding으로 열고 곧바로 비운다.
        /// 그 인코딩이 BOM 있는 UTF-8이면(윈도우의 'UTF-8 전역 지원'을 켠 PC 등) BOM 3바이트가 맨 앞에 먼저 나가 첫 JSON 줄이 깨진다.
        /// 콘솔이 없는 창 프로그램은 Console.InputEncoding을 바꿀 수 없어서(SetConsoleCP 실패) 기억해 둔 값을 BOM 없는 UTF-8로 바꾼다.
        static void InputWithoutBom()
        {
            try
            {
                if (Console.InputEncoding.GetPreamble().Length == 0) return;
                var f = typeof(Console).GetField("_inputEncoding", BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) f.SetValue(null, new UTF8Encoding(false));
                if (Console.InputEncoding.GetPreamble().Length > 0) Log.Write("표준 입력의 BOM을 끄지 못함");
            }
            catch (Exception ex) { Log.Write("표준 입력 인코딩 확인 실패: " + ex.GetType().Name); }
        }

        static bool IsScript(string exe)
        {
            string ext = Path.GetExtension(exe);
            return string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase);
        }

        /// npm으로 깐 claude.cmd는 명령 창(cmd.exe)을 거쳐 실행한다. /s는 맨 앞뒤 따옴표만 벗긴다.
        static ProcessStartInfo Launch(string exe, string args)
        {
            if (!IsScript(exe)) return new ProcessStartInfo(exe, args);
            return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"\"" + exe + "\" " + args + "\"");
        }

        static ProcessStartInfo StartInfo(string exe, string args, bool input, string workDir)
        {
            var psi = Launch(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = input;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WorkingDirectory = workDir;
            psi.EnvironmentVariables["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            return psi;
        }

        class Captured
        {
            public int Exit = -1;
            public bool TimedOut;
            public string Out = "";
            public string Err = "";
        }

        /// 짧은 명령(auth status)을 돌려 출력을 모두 받는다.
        static Captured Capture(string exe, string args, TimeSpan limit)
        {
            var c = new Captured();
            using (var p = Process.Start(StartInfo(exe, args, false, Path.GetTempPath())))
            {
                var output = p.StandardOutput.ReadToEndAsync();
                var errors = p.StandardError.ReadToEndAsync();
                if (p.WaitForExit((int)limit.TotalMilliseconds)) c.Exit = p.ExitCode;
                else
                {
                    c.TimedOut = true;
                    Util.KillTree(p.Id);
                }
                try { if (output.Wait(5000)) c.Out = output.Result; }
                catch { }
                try { if (errors.Wait(5000)) c.Err = errors.Result; }
                catch { }
            }
            return c;
        }

        static Thread Pump(StreamReader from, Action<string> to)
        {
            var t = new Thread(delegate ()
            {
                try
                {
                    string l;
                    while ((l = from.ReadLine()) != null) to(l);
                }
                catch (Exception ex) { Log.Write("Claude 출력 읽기 멈춤: " + ex.GetType().Name); }
            });
            t.IsBackground = true;
            t.Start();
            return t;
        }

        /// Claude Code가 한 줄씩 내보내는 stream-json을 읽는다(읽는 스레드 하나만 부른다).
        class Reading
        {
            public volatile string Unsafe;   // 끈 까닭(도구·권한)
            public volatile bool Done;       // result 줄을 받았다
            public DateTime DoneAt;
            public bool IsError;
            public string Subtype;
            public string Result;
            public string LastText;          // 마지막 assistant 글(result에 글이 없을 때 쓴다)
            public volatile int Received;    // 받은 글자 수(진행 표시)
            public volatile string InputFailed;
            readonly StringBuilder errors = new StringBuilder();

            public string Errors
            {
                get { lock (errors) return errors.ToString(); }
            }

            public void TakeError(string line)
            {
                lock (errors)
                {
                    if (errors.Length < 4000) errors.AppendLine(line);
                }
            }

            public void Take(string line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                Dictionary<string, object> m;
                try { m = Json.Parse(line); }
                catch { return; }   // JSON이 아닌 줄은 넘긴다
                if (m == null) return;
                string type = Json.S(m, "type");
                if (type == "system")
                {
                    if (Json.S(m, "subtype") != "init") return;
                    var tools = Json.A(m, "tools");
                    if (Some(tools)) Stop("init: 도구 " + tools.Length + "개");
                    else if (Some(Json.A(m, "mcp_servers"))) Stop("init: MCP 서버");
                }
                else if (type == "stream_event")
                {
                    var ev = Json.D(m, "event");
                    string kind = Json.S(ev, "type");
                    if (kind == "content_block_start")
                    {
                        string block = Json.S(Json.D(ev, "content_block"), "type");
                        if (!Plain(block)) Stop("블록 시작: " + block);
                    }
                    else if (kind == "content_block_delta")
                    {
                        var delta = Json.D(ev, "delta");
                        if (Json.S(delta, "type") == "text_delta") Received += (Json.S(delta, "text") ?? "").Length;
                    }
                }
                else if (type == "assistant")
                {
                    var text = new StringBuilder();
                    foreach (object o in Json.A(Json.D(m, "message"), "content") ?? new object[0])
                    {
                        var b = Json.Obj(o);
                        string block = Json.S(b, "type");
                        if (!Plain(block))
                        {
                            Stop("assistant 블록: " + block);
                            return;
                        }
                        if (block == "text") text.Append(Json.S(b, "text"));
                    }
                    if (text.Length > 0) LastText = text.ToString();
                }
                else if (type == "user")
                {
                    // 도구 결과가 돌아오는 자리. 도구를 주지 않았으니 tool_result가 오면 안 된다.
                    foreach (object o in Json.A(Json.D(m, "message"), "content") ?? new object[0])
                    {
                        if (Json.S(Json.Obj(o), "type") == "tool_result") Stop("도구 결과");
                    }
                }
                else if (type == "control_request")
                {
                    Stop("권한 질문");   // 이 프로그램은 답하지 않는다
                }
                else if (type == "result")
                {
                    IsError = Json.B(m, "is_error");
                    Subtype = Json.S(m, "subtype");
                    Result = Json.S(m, "result");
                    if (Some(Json.A(m, "permission_denials"))) Stop("권한 거절");
                    DoneAt = DateTime.UtcNow;
                    Done = true;
                }
            }

            void Stop(string why)
            {
                if (Unsafe == null) Unsafe = why;
            }

            static bool Some(object[] list)
            {
                return list != null && list.Length > 0;
            }

            /// 글·생각 블록만 받는다. 그 밖(tool_use·server_tool_use 등)은 도구를 쓰려는 것이다.
            static bool Plain(string block)
            {
                return block == "text" || block == "thinking" || block == "redacted_thinking";
            }
        }
    }

    // ── Gemini (선생님 키) ──

    public static class Gemini
    {
        static readonly string[] Models = { "gemini-3.1-flash-lite", "gemini-flash-lite-latest" };
        const string Base = "https://generativelanguage.googleapis.com/v1beta/";
        public const string KeyPage = "https://aistudio.google.com/apikey";

        /// 복사한 글이 Gemini API 키 모양이면 다듬은 키를, 아니면 null. 모양만 보고 글은 어디에도 남기지 않는다.
        /// 옛 키는 AIza로 시작하는 39자, 요즘 AI Studio가 만드는 키는 AQ.로 시작한다.
        public static string FindKey(string copied)
        {
            if (copied == null || copied.Length > 600) return null;
            string k = Web.CleanSecret(copied);
            if (k == null) return null;
            if (k.StartsWith("AIza", StringComparison.Ordinal) && k.Length >= 30 && k.Length <= 64) return k;
            if (k.StartsWith("AQ.", StringComparison.Ordinal) && k.Length >= 30) return k;
            return null;
        }

        static void EnsureTls()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }
        }

        public static string Run(string key, string system, string text, IList<byte[]> jpegs, TimeSpan limit, Job job)
        {
            var body = new StringBuilder();
            body.Append("{\"systemInstruction\":{\"parts\":[{\"text\":").Append(Json.Str(system)).Append("}]},");
            body.Append("\"contents\":[{\"role\":\"user\",\"parts\":[");
            foreach (var j in jpegs)
            {
                body.Append("{\"inline_data\":{\"mime_type\":\"image/jpeg\",\"data\":\"").Append(System.Convert.ToBase64String(j)).Append("\"}},");
            }
            body.Append("{\"text\":").Append(Json.Str(text)).Append("}]}]}");
            byte[] payload = Encoding.ASCII.GetBytes(body.ToString());
            job.Report("Gemini가 읽는 중", -1);
            for (int m = 0; m < Models.Length; m++)
            {
                int status;
                string resp;
                Send("POST", Base + "models/" + Models[m] + ":generateContent", key, payload, limit, job, out status, out resp);
                if (status == 404 && m + 1 < Models.Length) continue;
                if (status != 200) throw Failure(status, resp);
                return Answer(resp);
            }
            throw new UserFacingException("Gemini 모델을 찾지 못했어요.");
        }

        /// 키가 맞는지(모델 목록만 물어봄, 내용은 보내지 않음). 맞으면 null.
        public static string CheckKey(string key)
        {
            int status;
            string resp;
            try
            {
                Send("GET", Base + "models?pageSize=1", key, null, TimeSpan.FromSeconds(20), null, out status, out resp);
                if (status == 401 && key.StartsWith("AQ.", StringComparison.Ordinal))
                {
                    // 새 형식 키가 호출에 따라 거절됐다는 신고가 있어, 실제로 쓰는 호출로 한 번 더 본다(문서 내용은 보내지 않음).
                    byte[] ping = Encoding.ASCII.GetBytes("{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"ping\"}]}],\"generationConfig\":{\"maxOutputTokens\":1}}");
                    for (int m = 0; m < Models.Length; m++)
                    {
                        Send("POST", Base + "models/" + Models[m] + ":generateContent", key, ping, TimeSpan.FromSeconds(20), null, out status, out resp);
                        if (status != 404) break;
                    }
                }
            }
            catch (UserFacingException ex)
            {
                return ex.Message;
            }
            if (status == 200) return null;
            return Failure(status, resp).Message;
        }

        static void Send(string method, string url, string key, byte[] payload, TimeSpan limit, Job job, out int status, out string text)
        {
            EnsureTls();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Headers["x-goog-api-key"] = key;
            req.Timeout = (int)limit.TotalMilliseconds;
            req.ReadWriteTimeout = (int)limit.TotalMilliseconds;
            req.UserAgent = AppInfo.Id + "/" + AppInfo.Version;
            if (payload != null) req.ContentType = "application/json; charset=utf-8";
            using (new Timer(delegate { if (job != null && job.Cancelled) { try { req.Abort(); } catch { } } }, null, 300, 300))
            {
                try
                {
                    if (payload != null)
                    {
                        using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
                    }
                    using (var r = (HttpWebResponse)req.GetResponse())
                    {
                        status = (int)r.StatusCode;
                        text = ReadBody(r);
                    }
                }
                catch (WebException ex)
                {
                    if (job != null) job.Check();
                    var r = ex.Response as HttpWebResponse;
                    if (r == null)
                    {
                        if (ex.Status == WebExceptionStatus.Timeout) throw new UserFacingException("Gemini 답이 너무 오래 걸려서 멈췄어요.");
                        Log.Write("Gemini 연결 실패: " + ex.Status);
                        throw new UserFacingException("Gemini에 연결하지 못했어요. 인터넷 연결을 확인해 주세요.");
                    }
                    using (r)
                    {
                        status = (int)r.StatusCode;
                        text = ReadBody(r);
                    }
                }
            }
        }

        static string ReadBody(HttpWebResponse r)
        {
            using (var s = r.GetResponseStream())
            using (var rd = new StreamReader(s, Encoding.UTF8)) return rd.ReadToEnd();
        }

        static string Answer(string resp)
        {
            var d = Json.Parse(resp);
            string block = Json.S(Json.D(d, "promptFeedback"), "blockReason");
            if (block != null) throw new UserFacingException("Gemini가 이 내용을 받지 않았어요. (" + block + ")");
            var cands = Json.A(d, "candidates");
            if (cands == null || cands.Length == 0) throw new UserFacingException("Gemini가 빈 답을 보냈어요.");
            var c = Json.Obj(cands[0]);
            var parts = Json.A(Json.D(c, "content"), "parts");
            var sb = new StringBuilder();
            if (parts != null)
            {
                foreach (var x in parts)
                {
                    var part = Json.Obj(x);
                    if (Json.B(part, "thought")) continue;
                    string t = Json.S(part, "text");
                    if (t != null) sb.Append(t);
                }
            }
            string reason = Json.S(c, "finishReason");
            if (sb.Length == 0) throw new UserFacingException("Gemini가 답하지 못했어요. (" + (reason ?? "빈 답") + ")");
            if (reason == "MAX_TOKENS") sb.Append("\n\n(답이 길어서 여기서 끊겼어요)");
            return sb.ToString();
        }

        static Exception Failure(int status, string body)
        {
            string reason = "";
            try
            {
                var e = Json.D(Json.Parse(body), "error");
                reason = (Json.S(e, "status") ?? "") + " " + (Json.S(e, "message") ?? "");
                var details = Json.A(e, "details");
                if (details != null) foreach (var x in details) reason += " " + (Json.S(Json.Obj(x), "reason") ?? "");
            }
            catch { }
            Log.Write("Gemini 실패: " + status + " " + (reason.Length > 60 ? reason.Substring(0, 60) : reason));
            if (status == 401 || reason.Contains("API_KEY_INVALID") || reason.Contains("API key not valid")) return new UserFacingException("Gemini 키가 맞지 않아요. 설정 → AI 연결에서 키를 다시 넣어 주세요.");
            if (status == 429) return new UserFacingException("Gemini 사용량 한도에 닿았어요. 잠시 뒤나 내일 다시 해 주세요.");
            if (status == 403) return new UserFacingException("이 Gemini 키로는 쓸 수 없어요. Google AI Studio에서 키를 확인해 주세요.");
            if (status == 400 && reason.Contains("FAILED_PRECONDITION")) return new UserFacingException("이 계정·지역에서는 Gemini 무료 사용이 안 돼요.");
            if (status >= 500) return new UserFacingException("Gemini가 지금 붐벼요. 잠시 뒤에 다시 해 주세요.");
            return new UserFacingException("Gemini가 요청을 받지 않았어요. (" + status + ")");
        }
    }

    // ── 보내기 전 동의 창 ──

    public class ConsentWindow : Window
    {
        Consent answer = Consent.Cancel;

        public static Consent AskUser(int pictures)
        {
            var w = new ConsentWindow(pictures);
            w.ShowDialog();
            return w.answer;
        }

        /// 보기용(그림 확인): 띄우지 않은 창
        public static Window ForPreview(int pictures)
        {
            return new ConsentWindow(pictures);
        }

        ConsentWindow(int pictures)
        {
            string provider = AppSettings.Current.AiProvider;
            Title = "AI로 보내기 전에 · " + AppInfo.Name;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            Theme.Polish(this);
            try { Icon = App.WindowIcon; } catch { }

            var hue = Ai.HueOf(provider);
            var body = new StackPanel();
            body.Children.Add(Theme.DialogHead(Ai.NameOf(provider) + "로 보낼까요?", "보내기 전에 한 번 더 확인해 주세요.", hue, "spark"));

            var facts = new StackPanel();
            AddRow(facts, "보낼 곳", WhereText(provider), false);
            AddRow(facts, "보낼 것", "쪽 그림 " + pictures + "장 (파일 이름은 보내지 않아요)", false);
            AddRow(facts, "학습", TrainingText(provider), true);
            var box = new Border
            {
                Background = Theme.Sheet2,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 4, 16, 4),
                Margin = new Thickness(0, 20, 0, 0),
                Child = facts
            };
            body.Children.Add(box);

            var warn = Theme.Warning("학생 이름·성적·연락처 같은 개인정보가 담긴 문서는 보내지 마세요. 그런 문서는 [이 PC에서 읽기]를 눌러 주세요.");
            warn.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(warn);

            var send = Theme.MakeButton("보내기", "Primary");
            var local = Theme.MakeButton("이 PC에서 읽기", "Line");
            var cancel = Theme.MakeButton("취소", "Quiet");
            cancel.IsCancel = true;
            send.Click += delegate { answer = Consent.Send; Close(); };
            local.Click += delegate { answer = Consent.Local; Close(); };
            cancel.Click += delegate { answer = Consent.Cancel; Close(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            cancel.VerticalAlignment = VerticalAlignment.Center;
            local.Margin = new Thickness(10, 0, 0, 0);
            send.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(cancel);
            buttons.Children.Add(local);
            buttons.Children.Add(send);
            body.Children.Add(buttons);

            var card = Theme.Card(body, 26);
            card.Margin = new Thickness(20);
            Content = card;
            Loaded += delegate
            {
                local.Focus();
                Activate();
            };
        }

        public static string WhereText(string provider)
        {
            if (provider == Ai.ClaudeId) return "Anthropic의 Claude — 선생님의 Claude 구독 계정으로";
            if (provider == Ai.ChatGptId) return "OpenAI의 ChatGPT — 선생님의 ChatGPT 요금제로";
            if (provider == Ai.OpenRouterId) return "OpenRouter를 거쳐 무료 AI 모델로 — 선생님의 OpenRouter 계정으로";
            return "Google의 Gemini — 선생님이 넣은 키로";
        }

        public static string TrainingText(string provider)
        {
            if (provider == Ai.ClaudeId) return "Claude 계정 설정을 따라요. claude.ai → 설정 → 개인정보에서 '모델 개선에 도움'을 끄면 학습에 쓰이지 않아요.";
            if (provider == Ai.ChatGptId) return "OpenAI에 '저장하지 않기'로 보내요. 다만 이 연결 방식에서 학습에 쓰이는지는 OpenAI가 따로 밝히지 않았어요.";
            if (provider == Ai.OpenRouterId)
            {
                return AppSettings.Current.OpenRouterNoTraining
                    ? "기록·학습하지 않는다고 밝힌 곳으로만 보내요."
                    : "무료 모델은 그 모델을 운영하는 회사가 보낸 내용을 기록하거나 학습에 쓸 수 있어요.";
            }
            return "무료 키는 보낸 내용이 Google 서비스 개선에 쓰이고, 사람이 검토할 수도 있어요.";
        }

        internal static void AddRow(StackPanel list, string label, string value, bool last)
        {
            var g = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = Theme.Text(label, 13, FontWeights.Bold, Theme.Ink3);
            l.Margin = new Thickness(0, 2, 10, 0);
            var v = Theme.Body(value);
            v.Foreground = Theme.Ink;
            v.FontSize = 14;
            Grid.SetColumn(v, 1);
            g.Children.Add(l);
            g.Children.Add(v);
            list.Children.Add(g);
            if (!last) list.Children.Add(new Border { Height = 1, Background = Theme.Line });
        }
    }
}
