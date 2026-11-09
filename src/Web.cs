// 인터넷 요청 한 번, 그리고 브라우저 로그인이 돌아오는 작은 문(이 PC 안에서만 열림).
// 돌아오는 문(Loopback)은 RFC 8252(OAuth 2.0 for Native Apps) 7.3·8.3절을, PKCE 도우미는 RFC 7636 4.1·4.2절을 보고 짰다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bakkum
{
    /// 요청 하나의 답
    public class WebReply
    {
        public int Status;
        public string Body = "";
    }

    public static class Web
    {
        class Flag
        {
            public volatile bool On;
        }

        public static void EnsureTls()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }
        }

        /// 요청을 보내고 답을 통째로 받는다. 서버에 닿지 못하면 who 이름을 넣은 쉬운 말로 멈춘다.
        /// headers는 이름, 값, 이름, 값… 순서. 작업(job)이 취소되면 요청을 끊는다.
        public static WebReply Send(string method, string url, string[] headers, string contentType, byte[] payload, TimeSpan limit, Job job, string who)
        {
            return Run(method, url, headers, contentType, payload, limit, job, who, null);
        }

        /// 답을 한 줄씩 받는다(server-sent events). onLine이 false를 돌려주면 거기서 그만 받는다.
        /// 200이 아닌 답은 본문을 통째로 돌려준다.
        public static WebReply Stream(string method, string url, string[] headers, string contentType, byte[] payload, TimeSpan limit, Job job, string who, Func<string, bool> onLine)
        {
            return Run(method, url, headers, contentType, payload, limit, job, who, onLine);
        }

        static WebReply Run(string method, string url, string[] headers, string contentType, byte[] payload, TimeSpan limit, Job job, string who, Func<string, bool> onLine)
        {
            EnsureTls();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = AppInfo.Id + "/" + AppInfo.Version;
            int ms = (int)Math.Min(int.MaxValue - 1, limit.TotalMilliseconds);
            req.Timeout = ms;
            req.ReadWriteTimeout = ms;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (headers != null)
            {
                for (int i = 0; i + 1 < headers.Length; i += 2)
                {
                    if (headers[i] == "Accept") req.Accept = headers[i + 1];
                    else req.Headers[headers[i]] = headers[i + 1];
                }
            }
            if (payload != null) req.ContentType = contentType ?? "application/json";

            DateTime deadline = DateTime.UtcNow + limit;
            var late = new Flag();
            var finished = new Flag();
            var reply = new WebReply();
            using (new Timer(delegate
            {
                bool over = DateTime.UtcNow > deadline;
                if (over || (job != null && job.Cancelled))
                {
                    if (over) late.On = true;
                    try { req.Abort(); } catch { }
                }
            }, null, 300, 300))
            {
                try
                {
                    if (payload != null)
                    {
                        req.ContentLength = payload.Length;
                        using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
                    }
                    using (var r = (HttpWebResponse)req.GetResponse())
                    {
                        reply.Status = (int)r.StatusCode;
                        if (onLine != null && reply.Status == 200)
                        {
                            if (ReadLines(r, onLine))
                            {
                                // 끝을 알리는 줄을 받았으면 남은 연결은 기다리지 않고 끊는다.
                                finished.On = true;
                                try { req.Abort(); } catch { }
                            }
                        }
                        else reply.Body = ReadBody(r);
                    }
                }
                catch (WebException ex)
                {
                    if (finished.On) return reply;
                    if (job != null) job.Check();
                    var r = ex.Response as HttpWebResponse;
                    if (r == null)
                    {
                        if (late.On || ex.Status == WebExceptionStatus.Timeout) throw new UserFacingException(who + " 답이 너무 오래 걸려서 멈췄어요.");
                        Log.Write(who + " 연결 실패: " + ex.Status);
                        throw new UserFacingException(who + "에 연결하지 못했어요. 인터넷 연결을 확인해 주세요.");
                    }
                    using (r)
                    {
                        reply.Status = (int)r.StatusCode;
                        try { reply.Body = ReadBody(r); } catch { reply.Body = ""; }
                    }
                }
                catch (IOException)
                {
                    if (finished.On) return reply;
                    if (job != null) job.Check();
                    if (late.On) throw new UserFacingException(who + " 답이 너무 오래 걸려서 멈췄어요.");
                    throw new UserFacingException(who + " 답이 중간에 끊겼어요. 다시 해 주세요.");
                }
            }
            return reply;
        }

        /// 줄마다 넘긴다. 받는 쪽이 그만 받겠다고 하면 true.
        static bool ReadLines(HttpWebResponse r, Func<string, bool> onLine)
        {
            var s = r.GetResponseStream();
            var rd = new StreamReader(s, new UTF8Encoding(false));
            string line;
            while ((line = rd.ReadLine()) != null)
            {
                if (!onLine(line)) return true;
            }
            return false;
        }

        static string ReadBody(HttpWebResponse r)
        {
            using (var s = r.GetResponseStream())
            using (var rd = new StreamReader(s, Encoding.UTF8)) return rd.ReadToEnd();
        }

        /// 파일 하나를 받는다. 다 받지 못하면 받던 파일을 지우고 UserFacingException. progress는 0~1(전체 크기를 알 때만).
        public static void Download(string url, string dst, long maxBytes, TimeSpan limit, Action<double> progress, string who)
        {
            EnsureTls();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = AppInfo.Id + "/" + AppInfo.Version;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            DateTime deadline = DateTime.UtcNow + limit;
            try
            {
                using (var r = (HttpWebResponse)req.GetResponse())
                using (var s = r.GetResponseStream())
                using (var f = File.Create(dst))
                {
                    long total = r.ContentLength;
                    if (total > maxBytes) throw new UserFacingException(who + " 파일이 너무 커요.");
                    var buf = new byte[65536];
                    long got = 0;
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        got += n;
                        if (got > maxBytes) throw new UserFacingException(who + " 파일이 너무 커요.");
                        if (DateTime.UtcNow > deadline) throw new UserFacingException(who + " 받기가 너무 오래 걸려서 멈췄어요.");
                        f.Write(buf, 0, n);
                        if (progress != null && total > 0) progress((double)got / total);
                    }
                    if (total > 0 && got != total) throw new UserFacingException(who + " 받기가 중간에 끊겼어요. 다시 해 주세요.");
                }
            }
            catch (WebException ex)
            {
                Conversions.TryDelete(dst);
                Log.Write(who + " 받기 실패: " + ex.Status);
                throw new UserFacingException(who + " 받기에 실패했어요. 인터넷 연결을 확인해 주세요.");
            }
            catch (IOException)
            {
                Conversions.TryDelete(dst);
                throw new UserFacingException(who + " 받기가 중간에 끊겼어요. 다시 해 주세요.");
            }
            catch (UserFacingException)
            {
                Conversions.TryDelete(dst);
                throw;
            }
        }

        // ── 작은 도우미 ──

        public static string B64Url(byte[] b)
        {
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] FromB64Url(string s)
        {
            string t = s.Replace('-', '+').Replace('_', '/');
            switch (t.Length % 4)
            {
                case 2: t += "=="; break;
                case 3: t += "="; break;
            }
            return Convert.FromBase64String(t);
        }

        /// 맞히기 어려운 무작위 글자: 32바이트를 base64url로(43자). PKCE code_verifier(RFC 7636 4.1)와 state에 쓴다.
        public static string RandomToken()
        {
            var b = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return B64Url(b);
        }

        /// PKCE S256(RFC 7636 4.2): code_challenge = BASE64URL(SHA256(ASCII(code_verifier)))
        public static string Challenge(string verifier)
        {
            using (var sha = SHA256.Create()) return B64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        /// 붙여 넣은 키를 다듬는다: 앞뒤 공백, 감싼 따옴표 한 겹, 'Bearer ' 머리를 뗀다.
        /// 영문·숫자·'-'·'_'·'.'로만 된 20~256자가 아니면(공백·줄바꿈·한글이 섞이면) null.
        public static string CleanSecret(string raw)
        {
            if (raw == null) return null;
            string k = raw.Trim();
            if (k.Length >= 2 && (k[0] == '"' || k[0] == '\'' || k[0] == '`') && k[k.Length - 1] == k[0]) k = k.Substring(1, k.Length - 2).Trim();
            if (k.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) k = k.Substring(7).Trim();
            if (k.Length < 20 || k.Length > 256) return null;
            foreach (char c in k)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
                if (!ok) return null;
            }
            return k;
        }

        /// 이름=값&… (값이 null이면 뺀다)
        public static string Query(params string[] kv)
        {
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                if (kv[i + 1] == null) continue;
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv[i])).Append('=').Append(Uri.EscapeDataString(kv[i + 1]));
            }
            return sb.ToString();
        }

        public static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(q)) return d;
            foreach (string part in q.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                string v = eq < 0 ? "" : part.Substring(eq + 1);
                try
                {
                    k = Uri.UnescapeDataString(k.Replace('+', ' '));
                    v = Uri.UnescapeDataString(v.Replace('+', ' '));
                }
                catch { continue; }
                if (!d.ContainsKey(k)) d[k] = v;
            }
            return d;
        }

        /// 시험(/selftest-net) 동안만: 브라우저 대신 이 함수가 주소를 받아 가짜 서버로 따라간다.
        internal static Action<string> BrowserHook;

        /// 시험 주소는 이 PC 안(127.0.0.1)만 받는다.
        internal static void RequireLocal(string baseUrl)
        {
            if (baseUrl == null || !System.Text.RegularExpressions.Regex.IsMatch(baseUrl, @"^http://127\.0\.0\.1:\d{1,5}$"))
                throw new ArgumentException("시험 서버는 http://127.0.0.1:포트 만 쓸 수 있어요.");
        }

        /// 기본 브라우저로 연다. 못 열면 false.
        public static bool OpenBrowser(string url)
        {
            var hook = BrowserHook;
            if (hook != null)
            {
                hook(url);
                return true;
            }
            try
            {
                using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("브라우저 열기 실패: " + ex.GetType().Name);
                return false;
            }
        }

        static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (c == '<' || c == '>' || c == '&' || c == '"' || c == '\'') sb.Append("&#").Append((int)c).Append(';');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// 로그인 뒤 브라우저에 보여 줄 쪽. 스크립트·바깥 자원 없이 색종이 디자인으로.
        public static string ResultPage(string title, string body, bool ok)
        {
            string dot = ok ? "#52D3A0" : "#FF8473";
            return "<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                "<title>" + Esc(title) + " · " + Esc(AppInfo.Name) + "</title><style>" +
                "html,body{margin:0;height:100%}" +
                "body{display:grid;place-items:center;background:#F6F3EE;color:#22252F;" +
                "font-family:Pretendard,'Malgun Gothic','Apple SD Gothic Neo',system-ui,sans-serif;-webkit-font-smoothing:antialiased}" +
                "main{box-sizing:border-box;width:min(440px,calc(100% - 32px));background:#fff;border-radius:24px;padding:40px 40px 36px;" +
                "box-shadow:0 3px 22px rgba(34,37,47,.08),0 1px 3px rgba(34,37,47,.05);text-align:center}" +
                ".logo{position:relative;width:60px;height:60px;border-radius:16px;background:#22252F;margin:0 auto 26px}" +
                ".logo i{position:absolute;left:12px;height:8px;border-radius:4px}" +
                ".a{top:15px;width:26px;background:#FF8473}.b{top:26px;width:36px;background:#FFD447}.c{top:37px;width:22px;background:#6CA8FF}" +
                ".pill{display:inline-flex;align-items:center;gap:8px;padding:7px 14px;border-radius:999px;background:#F6F3EE;font-size:14px;font-weight:700;margin-bottom:16px}" +
                ".pill b{width:9px;height:9px;border-radius:50%;background:" + dot + "}" +
                "h1{font-size:23px;line-height:1.35;margin:0 0 10px;letter-spacing:-.01em}" +
                "p{font-size:15px;line-height:1.65;color:#4B5160;margin:0}" +
                "</style></head><body><main>" +
                "<div class=\"logo\"><i class=\"a\"></i><i class=\"b\"></i><i class=\"c\"></i></div>" +
                "<div class=\"pill\"><b></b>" + Esc(AppInfo.Name) + "</div>" +
                "<h1>" + Esc(title) + "</h1><p>" + Esc(body) + "</p>" +
                "</main></body></html>";
        }
    }

    /// 브라우저 로그인이 돌아오는 문. RFC 8252(OAuth 2.0 for Native Apps)를 따른다.
    ///  - 7.3절: 루프백 주소(127.0.0.1, [::1])에 운영체제가 고른 빈 포트로 연다(인증 서버는 어떤 포트든 받아야 한다).
    ///    브라우저가 'localhost'를 IPv4·IPv6 어느 쪽으로 찾아올지 모르므로, localhost로 돌아오는 곳(OpenRouter)은
    ///    두 주소의 같은 포트를 함께 연다. IPv6 루프백을 못 쓰는 PC면 127.0.0.1만 연다.
    ///  - 8.3절: 루프백에서만 듣고, 로그인을 시작할 때 열어 답을 받으면 닫는다(Dispose).
    ///  - 정해 둔 경로의 GET이고 Host 머리가 이 문(127.0.0.1·localhost·[::1]:포트)일 때만 본다.
    ///    그 밖이나 accept가 거절한 요청(state가 다름 등)은 404. 처음 받아들인 하나만 쓴다.
    ///  - 브라우저에는 스크립트·바깥 자원 없는 작은 안내 쪽만 돌려준다.
    public sealed class Loopback : IDisposable
    {
        static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

        readonly string path;
        readonly Func<Dictionary<string, string>, bool> accept;
        readonly Func<Dictionary<string, string>, string> pageFor;
        readonly List<TcpListener> doors = new List<TcpListener>();
        readonly ManualResetEvent arrived = new ManualResetEvent(false);
        readonly object gate = new object();
        Dictionary<string, string> answer;
        volatile bool closed;
        public readonly int Port;

        /// path: 받을 경로(예: /callback). alsoIpv6: localhost로 돌아오는 곳이면 [::1]의 같은 포트도 연다.
        /// accept: 기다리던 답인지(예: state가 맞는지). pageFor: 받아들인 뒤 브라우저에 보여 줄 쪽.
        public Loopback(string path, bool alsoIpv6, Func<Dictionary<string, string>, bool> accept, Func<Dictionary<string, string>, string> pageFor)
        {
            this.path = path;
            this.accept = accept;
            this.pageFor = pageFor;
            Port = Open(alsoIpv6 && Socket.OSSupportsIPv6);
            foreach (var door in doors.ToArray()) Listen(door);
        }

        /// 빈 포트를 골라 연다. 두 주소를 다 열어야 하면 그 포트가 IPv6에서도 비어 있을 때까지 몇 번 다시 고른다.
        int Open(bool both)
        {
            for (int attempt = 0; both && attempt < 5; attempt++)
            {
                var v4 = new TcpListener(IPAddress.Loopback, 0);
                v4.Start();
                int port = ((IPEndPoint)v4.LocalEndpoint).Port;
                var v6 = new TcpListener(IPAddress.IPv6Loopback, port);
                try
                {
                    v6.Start();
                    doors.Add(v4);
                    doors.Add(v6);
                    return port;
                }
                catch (SocketException)
                {
                    v4.Stop();
                }
            }
            var only = new TcpListener(IPAddress.Loopback, 0);
            only.Start();
            doors.Add(only);
            return ((IPEndPoint)only.LocalEndpoint).Port;
        }

        void Listen(TcpListener door)
        {
            var t = new Thread(delegate ()
            {
                while (!closed)
                {
                    TcpClient client;
                    try { client = door.AcceptTcpClient(); }
                    catch { return; }   // Dispose가 문을 닫으면 여기서 끝난다
                    ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// 돌아올 때까지 기다린다. 시간이 다 되거나 취소되면 null.
        public Dictionary<string, string> Wait(TimeSpan limit, Func<bool> cancelled)
        {
            DateTime end = DateTime.UtcNow + limit;
            while (true)
            {
                TimeSpan left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return null;
                if (arrived.WaitOne(left < Tick ? left : Tick))
                {
                    lock (gate) return answer;
                }
                if (cancelled != null && cancelled()) return null;
            }
        }

        void Serve(TcpClient client)
        {
            bool taken = false;
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 10000;
                    var stream = client.GetStream();
                    var query = Read(stream);
                    if (query != null)
                    {
                        lock (gate)
                        {
                            if (answer == null && accept(query))
                            {
                                answer = query;
                                taken = true;
                            }
                        }
                    }
                    if (taken) Reply(stream, "200 OK", "text/html; charset=utf-8", pageFor(query));
                    else Reply(stream, "404 Not Found", "text/plain; charset=utf-8", "");
                }
            }
            catch (Exception ex) { Log.Write("로그인 문 요청 처리 실패: " + ex.GetType().Name); }
            finally
            {
                if (taken)
                {
                    try { arrived.Set(); }
                    catch { }
                }
            }
        }

        /// 요청 머리를 읽어 'GET 정해 둔경로?… HTTP/1.x'이고 Host가 이 문이면 쿼리를 돌려준다. 아니면 null.
        Dictionary<string, string> Read(NetworkStream stream)
        {
            string head = Head(stream);
            if (head == null) return null;
            string[] lines = head.Split('\n');
            string[] first = lines[0].TrimEnd('\r').Split(' ');
            if (first.Length != 3 || first[0] != "GET" || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
            string target = first[1];
            int mark = target.IndexOf('?');
            if ((mark < 0 ? target : target.Substring(0, mark)) != path) return null;
            string host = null;
            for (int i = 1; i < lines.Length && host == null; i++)
            {
                string line = lines[i].TrimEnd('\r');
                int colon = line.IndexOf(':');
                if (colon > 0 && string.Equals(line.Substring(0, colon).Trim(), "Host", StringComparison.OrdinalIgnoreCase))
                    host = line.Substring(colon + 1).Trim();
            }
            if (!Here(host)) return null;
            return Web.ParseQuery(mark < 0 ? "" : target.Substring(mark + 1));
        }

        /// Host 머리가 이 문을 가리키는지. 다른 이름으로 온 요청(남의 사이트 주소를 이 PC로 돌린 경우 등)은 받지 않는다.
        bool Here(string host)
        {
            if (host == null) return false;
            foreach (string name in new[] { "127.0.0.1", "localhost", "[::1]" })
            {
                if (string.Equals(host, name + ":" + Port, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// 머리 끝(빈 줄)까지, 8KB까지만 읽는다.
        static string Head(NetworkStream stream)
        {
            var buf = new byte[8192];
            int n = 0;
            while (n < buf.Length)
            {
                int got = stream.Read(buf, n, buf.Length - n);
                if (got <= 0) break;
                n += got;
                if (EndsHead(buf, n)) break;
            }
            return n == 0 ? null : Encoding.ASCII.GetString(buf, 0, n);
        }

        static bool EndsHead(byte[] b, int n)
        {
            for (int i = 3; i < n; i++)
            {
                if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r') return true;
            }
            return false;
        }

        static void Reply(NetworkStream stream, string status, string type, string body)
        {
            byte[] content = Encoding.UTF8.GetBytes(body ?? "");
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            head.Append("Content-Type: ").Append(type).Append("\r\n");
            head.Append("Content-Length: ").Append(content.Length).Append("\r\n");
            head.Append("Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n");   // 글자 모양만, 스크립트·바깥 자원 없음
            head.Append("X-Content-Type-Options: nosniff\r\n");
            head.Append("Referrer-Policy: no-referrer\r\n");   // 돌아온 주소(code)가 다른 곳으로 새지 않게
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: close\r\n\r\n");
            byte[] h = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(h, 0, h.Length);
            if (content.Length > 0) stream.Write(content, 0, content.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            closed = true;
            foreach (var door in doors)
            {
                try { door.Stop(); }
                catch { }
            }
            doors.Clear();
        }
    }
}
