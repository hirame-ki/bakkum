// ChatGPT 연결: OpenAI 'Sign in with ChatGPT'(오픈소스·자기 PC에서 도는 앱이 선생님의 ChatGPT 요금제를 쓰는 방식).
// 선생님이 브라우저의 OpenAI 로그인 화면에서 허락하면, 이 프로그램이 그 계정의 앱으로 등록되고 요금제(Plus·Pro) 안에서 쓰는 토큰을 받는다.
// 비밀번호는 보지 않고, 개발자 비밀 키도 없다.
// 근거: OpenAI 문서(developers.openai.com/siwc — quickstart, token-sharing-open-source의 sign-in · profiles-and-sessions ·
//       token-reference · models-and-inference · errors-and-recovery, ui-ux-guidelines), 2026-11-08 읽음.
//       돌아오는 문은 RFC 8252, PKCE는 RFC 7636, ID 토큰 확인은 OpenID Connect Core 1.0의 3.1.3.7절.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bakkum
{
    public static class ChatGpt
    {
        // ── 주소와 값(문서 그대로). 시험 때만 이 PC 안의 가짜 서버로 바꾼다 ──
        static string Issuer = "https://auth.openai.com";
        static string Api = "https://api.openai.com/v1/";
        const string Resource = "https://api.openai.com/v1";
        const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
        const string PlanScope = "chatgpt.tokens.use.direct";   // 이 권한이 있어야 요금제를 쓸 수 있다(ID 토큰만으로는 아님)
        const string FirstClient = "dynamic_agent_client";       // 처음 로그인 때만. 돌아올 때 받은 client_id를 다음부터 쓴다
        const string CallbackPath = "/auth/callback";            // 127.0.0.1만(localhost 아님), 경로는 늘 같고 포트만 바뀐다
        public const string UsagePage = "https://chatgpt.com/settings/usage";
        const string Who = "ChatGPT";
        static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);          // ID 토큰 만료 확인의 시계 오차
        static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(5);  // 액세스 토큰이 이만큼 남으면 새로 고친다

        /// 새로 고침이 이 오류로 거절되면 다시 로그인해야 한다(문서 errors-and-recovery). 토큰만 지우고 client_id·host id는 남긴다.
        static readonly string[] NeedsSignIn =
        {
            "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired", "refresh_token_invalidated", "refresh_token_reused",
        };

        static string AuthorizeUrl { get { return Issuer + "/api/accounts/authorize"; } }
        static string TokenUrl { get { return Issuer + "/api/accounts/oauth/token"; } }
        static string KeysUrl { get { return Issuer + "/.well-known/jwks.json"; } }
        static string DiscoveryUrl { get { return Issuer + "/.well-known/openid-configuration"; } }

        static readonly object gate = new object();         // 아래 값과 chatgpt.json
        static readonly object refreshGate = new object();  // 새로 고침은 한 번에 하나(새로 고침 토큰은 쓸 때마다 바뀐다)
        static Saved saved;
        static string account;                              // 로그인한 계정 이메일: 이번 실행에서만, 저장하지 않는다
        static object[] signingKeys;                        // OpenAI 공개 키 목록(JWKS)
        static string model;
        static DateTime modelAt;

        /// 시험(/selftest-net) 동안만: 이 PC 안의 가짜 서버로 돌린다. 로그인 파일은 AppInfo.DataDirOverride로 임시 폴더에 둔다.
        internal static void UseTestServer(string baseUrl)
        {
            Web.RequireLocal(baseUrl);
            lock (gate)
            {
                Issuer = baseUrl + "/auth";
                Api = baseUrl + "/openai/v1/";
                saved = null;
                account = null;
                signingKeys = null;
                model = null;
            }
        }

        internal static void TestExpireNow()
        {
            lock (gate)
            {
                var s = Current();
                if (s.Refresh == null) return;
                s.ExpiresUtc = DateTime.UtcNow;
                s.EarliestRefreshUtc = DateTime.MinValue;
                Store(s);
            }
        }

        internal static string TestRefreshToken()
        {
            lock (gate) return Current().Refresh;
        }

        internal static string TestClientId()
        {
            lock (gate) return Current().ClientId;
        }

        // ── 저장(chatgpt.json) ──
        // host_id·client_id·안내를 보였는지는 그대로, 토큰은 윈도우 사용자 암호화(DPAPI)로 감싼 한 덩어리(tokens)로만 둔다.

        class Saved
        {
            public string HostId;        // 이 설치의 ext_agent_host_id(urn:uuid:…). 한 번 만들고 계속 쓴다
            public string ClientId;      // 처음 로그인 때 받은 client_id. 로그아웃해도 남긴다
            public bool NoticeShown;     // '요금제를 쓰고 있어요' 안내를 보였는지
            public string Access;
            public string Refresh;       // 있으면 로그인된 것
            public DateTime ExpiresUtc;
            public DateTime EarliestRefreshUtc;
        }

        static string FilePath
        {
            get { return Path.Combine(AppInfo.DataDir, "chatgpt.json"); }
        }

        /// gate 안에서 부른다.
        static Saved Current()
        {
            if (saved != null) return saved;
            var s = new Saved();
            try
            {
                if (File.Exists(FilePath))
                {
                    var d = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                    s.HostId = Json.S(d, "host_id");
                    s.ClientId = Json.S(d, "client_id");
                    s.NoticeShown = Json.B(d, "notice_shown");
                    string inner = AppSettings.Unprotect(Json.S(d, "tokens"));
                    if (inner.Length > 0)
                    {
                        var t = Json.Parse(inner);
                        s.Access = Json.S(t, "access");
                        s.Refresh = Json.S(t, "refresh");
                        s.ExpiresUtc = FromUnix(Number(t, "expires_at"));
                        s.EarliestRefreshUtc = FromUnix(Number(t, "earliest_refresh_at"));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("ChatGPT 로그인 파일 읽기 실패: " + ex.GetType().Name);
            }
            saved = s;
            return s;
        }

        /// gate 안에서 부른다. 임시 파일에 쓴 뒤 바꿔 끼운다.
        static void Store(Saved s)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"host_id\":").Append(Json.Str(s.HostId));
            sb.Append(",\"client_id\":").Append(Json.Str(s.ClientId));
            sb.Append(",\"notice_shown\":").Append(s.NoticeShown ? "true" : "false");
            if (s.Refresh != null)
            {
                string tokens = "{\"access\":" + Json.Str(s.Access) + ",\"refresh\":" + Json.Str(s.Refresh) +
                    ",\"expires_at\":" + ToUnix(s.ExpiresUtc) + ",\"earliest_refresh_at\":" + ToUnix(s.EarliestRefreshUtc) + "}";
                sb.Append(",\"tokens\":").Append(Json.Str(AppSettings.Protect(tokens)));
            }
            sb.Append('}');
            saved = s;
            try
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Write("ChatGPT 로그인 파일 저장 실패: " + ex.GetType().Name);
            }
        }

        /// 토큰만 지운다(다시 로그인할 때 쓸 client_id·host id·안내 여부는 남긴다).
        static void ForgetTokens()
        {
            lock (gate)
            {
                var s = Current();
                s.Access = null;
                s.Refresh = null;
                s.ExpiresUtc = DateTime.MinValue;
                s.EarliestRefreshUtc = DateTime.MinValue;
                account = null;
                model = null;
                Store(s);
            }
        }

        // ── 상태 ──

        public static bool SignedIn
        {
            get { lock (gate) return Current().Refresh != null; }
        }

        /// 로그인한 계정 이메일(ID 토큰). 저장하지 않으므로 이번 실행에서 로그인했을 때만 있다.
        public static string Account
        {
            get { lock (gate) return Current().Refresh != null ? account : null; }
        }

        /// 처음 연결한 뒤 한 번만 true: '선생님의 ChatGPT 요금제를 쓰고 있어요' 안내를 보일 차례(문서 ui-ux-guidelines).
        public static bool TakeFirstNotice()
        {
            lock (gate)
            {
                var s = Current();
                if (s.NoticeShown || s.Refresh == null) return false;
                s.NoticeShown = true;
                Store(s);
                return true;
            }
        }

        // ── 로그인(문서 sign-in) ──
        // 1) 기본 브라우저로 인가 주소를 연다: response_type=code, client_id, (처음만) agent_name_hint, ext_agent_host_id,
        //    redirect_uri=http://127.0.0.1:포트/auth/callback, scope, resource, state, nonce, PKCE(S256).
        // 2) 돌아온 code를 토큰 끝점에서 바꾼다(form: grant_type, client_id, code, code_verifier, redirect_uri, resource).
        // 3) ID 토큰을 확인하고, 토큰 응답의 scope에 요금제 권한이 있을 때만 저장한다.

        /// 브라우저에서 OpenAI 로그인 → 토큰. 오래 걸리니 화면 밖 스레드에서 부른다. 실패하면 false와 쉬운 말 이유(취소면 이유도 null).
        public static bool SignIn(Func<bool> cancelled, out string failure)
        {
            failure = null;
            string hostId, known;
            lock (gate)
            {
                var s = Current();
                if (s.HostId == null)
                {
                    s.HostId = "urn:uuid:" + Guid.NewGuid().ToString("D");
                    Store(s);
                }
                hostId = s.HostId;
                known = s.ClientId;
            }
            string verifier = Web.RandomToken(), state = Web.RandomToken(), nonce = Web.RandomToken();
            string redirect;
            Dictionary<string, string> back;
            try
            {
                using (var door = new Loopback(CallbackPath, false,
                    delegate (Dictionary<string, string> q) { return Value(q, "state") == state; },
                    delegate (Dictionary<string, string> q)
                    {
                        return Value(q, "code") != null
                            ? Web.ResultPage("ChatGPT와 연결하는 중이에요", "이 창은 닫아도 돼요. " + AppInfo.Name + " 설정 창으로 돌아가 주세요.", true)
                            : Web.ResultPage("연결하지 못했어요", AppInfo.Name + " 설정 창에서 다시 해 주세요.", false);
                    }))
                {
                    redirect = "http://127.0.0.1:" + door.Port + CallbackPath;
                    string url = AuthorizeUrl + "?" + Web.Query(
                        "response_type", "code",
                        "client_id", known ?? FirstClient,
                        "agent_name_hint", known == null ? AppInfo.Name : null,
                        "ext_agent_host_id", hostId,
                        "redirect_uri", redirect,
                        "scope", Scopes,
                        "resource", Resource,
                        "state", state,
                        "nonce", nonce,
                        "code_challenge", Web.Challenge(verifier),
                        "code_challenge_method", "S256");
                    if (!Web.OpenBrowser(url))
                    {
                        failure = "브라우저를 열지 못했어요.";
                        return false;
                    }
                    back = door.Wait(TimeSpan.FromMinutes(10), cancelled);
                }
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Log.Write("ChatGPT 로그인 문 열기 실패: " + ex.SocketErrorCode);
                failure = "로그인을 받을 준비를 하지 못했어요. 다시 해 주세요.";
                return false;
            }
            if (back == null)
            {
                if (!cancelled()) failure = "로그인을 기다리다 시간이 다 됐어요. 다시 해 주세요.";
                return false;
            }
            string code = Value(back, "code");
            if (code == null)
            {
                Log.Write("ChatGPT 로그인 거절: " + (Value(back, "error") ?? "code 없음"));
                failure = "OpenAI 화면에서 연결을 허락하지 않았어요.";
                return false;
            }
            // 처음 등록이면 돌아올 때 client_id가 온다(없으면 등록이 끝나지 않은 것). 이미 등록했으면 같은 client_id여야 한다.
            string returned = Value(back, "client_id");
            string clientId = known ?? returned;
            if (clientId == null)
            {
                failure = "OpenAI에서 앱 등록을 마치지 못했어요. 다시 해 주세요.";
                return false;
            }
            if (known != null && returned != null && returned != known)
            {
                Log.Write("ChatGPT 로그인: 돌아온 client_id가 저장한 것과 다름");
                failure = "OpenAI가 돌려준 등록 정보가 이 PC에 저장된 것과 맞지 않아요. 다시 해 주세요.";
                return false;
            }
            try
            {
                return Finish(clientId, code, verifier, redirect, nonce, out failure);
            }
            catch (UserFacingException ex)
            {
                failure = ex.Message;
                return false;
            }
        }

        static bool Finish(string clientId, string code, string verifier, string redirect, string nonce, out string failure)
        {
            failure = null;
            Tokens t;
            string error;
            string problem = Token(Web.Query(
                "grant_type", "authorization_code",
                "client_id", clientId,
                "code", code,
                "code_verifier", verifier,
                "redirect_uri", redirect,
                "resource", Resource), out t, out error);
            if (problem != null)
            {
                failure = problem;
                return false;
            }
            if (t == null)
            {
                failure = "OpenAI가 로그인을 마무리해 주지 않았어요(" + error + "). 다시 해 주세요.";
                return false;
            }
            Dictionary<string, object> claims;
            if (!CheckIdToken(t.IdToken, clientId, nonce, out claims))
            {
                failure = "OpenAI에서 받은 로그인 정보가 맞지 않아요. 다시 해 주세요.";
                return false;
            }
            if (!HasScope(t.Scope, PlanScope))
            {
                failure = "OpenAI 화면에서 ChatGPT 요금제 사용을 허락하지 않았어요. 다시 로그인하면서 허락해 주세요.";
                return false;
            }
            lock (gate)
            {
                var s = Current();
                s.ClientId = clientId;
                Keep(s, t);
                Store(s);
                account = Json.S(claims, "email");
                model = null;
            }
            return true;
        }

        static string Value(Dictionary<string, string> q, string name)
        {
            string v;
            return q != null && q.TryGetValue(name, out v) && v.Length > 0 ? v : null;
        }

        static bool HasScope(string granted, string wanted)
        {
            return granted != null && granted.Split(' ').Contains(wanted);
        }

        // ── 토큰(문서 token-reference) ──

        class Tokens
        {
            public string Access, Refresh, IdToken, Scope;
            public long ExpiresIn;
            public DateTime EarliestRefreshUtc;
        }

        /// 토큰 끝점에 form을 보낸다. 연결이 안 되면 쉬운 말 이유를 돌려준다.
        /// 끝점이 거절하면 t는 null, error에 OAuth 오류 코드. 잘 되면 t에 토큰.
        static string Token(string form, out Tokens t, out string error)
        {
            t = null;
            error = null;
            WebReply r;
            try
            {
                r = Web.Send("POST", TokenUrl, new[] { "Accept", "application/json" }, "application/x-www-form-urlencoded",
                    Encoding.ASCII.GetBytes(form), TimeSpan.FromSeconds(30), null, Who);
            }
            catch (UserFacingException ex)
            {
                return ex.Message;
            }
            Dictionary<string, object> d = null;
            try { d = Json.Parse(r.Body); }
            catch { }
            if (r.Status != 200 || d == null || Json.S(d, "access_token") == null)
            {
                error = Json.S(d, "error") ?? ("http " + r.Status);
                Log.Write("ChatGPT 토큰 거절: " + r.Status + " " + error);
                return null;
            }
            t = new Tokens
            {
                Access = Json.S(d, "access_token"),
                Refresh = Json.S(d, "refresh_token"),
                IdToken = Json.S(d, "id_token"),
                Scope = Json.S(d, "scope"),
                ExpiresIn = Number(d, "expires_in"),
                EarliestRefreshUtc = When(Json.Get(d, "earliest_refresh_at")),
            };
            return null;
        }

        /// gate 안에서. 새로 고침 토큰은 쓸 때마다 바뀌므로 새로 온 것을 저장한다(오지 않았으면 쓰던 것).
        static void Keep(Saved s, Tokens t)
        {
            s.Access = t.Access;
            if (t.Refresh != null) s.Refresh = t.Refresh;
            s.ExpiresUtc = DateTime.UtcNow.AddSeconds(t.ExpiresIn > 0 ? t.ExpiresIn : 3600);
            s.EarliestRefreshUtc = t.EarliestRefreshUtc;
        }

        /// 쓸 수 있는 액세스 토큰. 만료가 가까우면 새로 고친다(한 번에 하나씩).
        static string AccessToken(Job job)
        {
            lock (refreshGate)
            {
                string clientId, refresh;
                lock (gate)
                {
                    var s = Current();
                    if (s.Refresh == null) throw new UserFacingException(NotSignedIn);
                    bool fresh = s.Access != null && DateTime.UtcNow < s.ExpiresUtc - RefreshAhead;
                    bool tooEarly = DateTime.UtcNow < s.EarliestRefreshUtc && s.Access != null && DateTime.UtcNow < s.ExpiresUtc;
                    if (fresh || tooEarly) return s.Access;
                    clientId = s.ClientId;
                    refresh = s.Refresh;
                }
                job.Report("ChatGPT 로그인을 새로 고치는 중", -1);
                Tokens t;
                string error;
                string problem = Token(Web.Query(
                    "grant_type", "refresh_token",
                    "client_id", clientId,
                    "refresh_token", refresh,
                    "resource", Resource), out t, out error);
                if (problem != null) throw new UserFacingException(problem);
                if (t == null)
                {
                    if (NeedsSignIn.Contains(error))
                    {
                        ForgetTokens();
                        throw new UserFacingException("ChatGPT 로그인 기간이 끝나서 지금은 로그인되어 있지 않아요. 설정 → AI 연결에서 [ChatGPT로 계속하기]를 눌러 다시 로그인해 주세요.");
                    }
                    if (error == "invalid_client")
                        throw new UserFacingException("ChatGPT 연결 등록에 문제가 있어요. 설정 → AI 연결에서 로그아웃한 뒤 다시 로그인해 주세요.");
                    throw new UserFacingException("ChatGPT 로그인을 새로 고치지 못했어요. 잠시 뒤 다시 해 보세요.");
                }
                lock (gate)
                {
                    var s = Current();
                    Keep(s, t);
                    Store(s);
                    return s.Access;
                }
            }
        }

        const string NotSignedIn = "ChatGPT에 로그인되어 있지 않아요. 설정 → AI 연결에서 [ChatGPT로 계속하기]를 눌러 로그인해 주세요.";

        // ── ID 토큰 확인(OpenID Connect Core 3.1.3.7 + 문서 token-reference) ──
        // RS256 서명(공개 키 목록에서 kid로 고른 키, 모르는 kid면 목록을 다시 받음), iss, aud(=client_id), exp(작은 시계 오차만), nonce.

        static bool CheckIdToken(string jwt, string clientId, string nonce, out Dictionary<string, object> claims)
        {
            claims = null;
            if (string.IsNullOrEmpty(jwt)) return false;
            string[] parts = jwt.Split('.');
            if (parts.Length != 3) return false;
            Dictionary<string, object> head;
            try
            {
                head = Json.Parse(Encoding.UTF8.GetString(Web.FromB64Url(parts[0])));
                claims = Json.Parse(Encoding.UTF8.GetString(Web.FromB64Url(parts[1])));
            }
            catch
            {
                return false;
            }
            if (head == null || claims == null || Json.S(head, "alg") != "RS256") return false;
            string kid = Json.S(head, "kid");
            var key = FindKey(SigningKeys(false), kid) ?? FindKey(SigningKeys(true), kid);
            if (key == null || !Verify(key, parts[0] + "." + parts[1], parts[2])) return false;
            if (Json.S(claims, "iss") != Issuer) return false;
            if (!ForUs(claims, clientId)) return false;
            long exp = Number(claims, "exp");
            if (exp <= 0 || FromUnix(exp) < DateTime.UtcNow - Skew) return false;
            return Json.S(claims, "nonce") == nonce;
        }

        /// aud가 우리 client_id인지. 여러 대상이면 그중에 있어야 하고 azp가 우리여야 한다.
        static bool ForUs(Dictionary<string, object> claims, string clientId)
        {
            object aud = Json.Get(claims, "aud");
            if (aud is string) return (string)aud == clientId;
            var list = aud as object[];
            if (list == null || !list.Contains(clientId)) return false;
            return list.Length == 1 || Json.S(claims, "azp") == clientId;
        }

        static object[] SigningKeys(bool fresh)
        {
            lock (gate)
            {
                if (!fresh && signingKeys != null) return signingKeys;
            }
            var r = Web.Send("GET", KeysUrl, new[] { "Accept", "application/json" }, null, null, TimeSpan.FromSeconds(30), null, Who);
            if (r.Status != 200) return null;
            object[] list = null;
            try { list = Json.A(Json.Parse(r.Body), "keys"); }
            catch { }
            lock (gate)
            {
                if (list != null) signingKeys = list;
            }
            return list;
        }

        static Dictionary<string, object> FindKey(object[] keys, string kid)
        {
            if (keys == null) return null;
            foreach (object o in keys)
            {
                var k = Json.Obj(o);
                string use = Json.S(k, "use");
                if (Json.S(k, "kty") == "RSA" && Json.S(k, "kid") == kid && (use == null || use == "sig")) return k;
            }
            return null;
        }

        static bool Verify(Dictionary<string, object> jwk, string signed, string signature)
        {
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportParameters(new RSAParameters
                    {
                        Modulus = WithoutLeadingZeros(Web.FromB64Url(Json.S(jwk, "n"))),
                        Exponent = WithoutLeadingZeros(Web.FromB64Url(Json.S(jwk, "e"))),
                    });
                    return rsa.VerifyData(Encoding.ASCII.GetBytes(signed), Web.FromB64Url(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (Exception ex)
            {
                Log.Write("ChatGPT 서명 확인 실패: " + ex.GetType().Name);
                return false;
            }
        }

        static byte[] WithoutLeadingZeros(byte[] b)
        {
            int i = 0;
            while (i < b.Length - 1 && b[i] == 0) i++;
            return i == 0 ? b : b.Skip(i).ToArray();
        }

        // ── 로그아웃(문서 profiles-and-sessions) ──

        /// 이 PC의 토큰을 지우고, OpenAI 쪽 세션도 끝내 달라고 한다: 탐색 문서의 revocation_endpoint로
        /// token=새로 고침 토큰, token_type_hint=refresh_token, client_id를 보낸다(빈 200이 성공).
        /// client_id·host id는 남긴다. OpenAI 쪽 해제를 확인했으면 null, 못 했으면 그 안내 글.
        public static string SignOut()
        {
            string refresh, clientId;
            lock (gate)
            {
                var s = Current();
                refresh = s.Refresh;
                clientId = s.ClientId;
            }
            ForgetTokens();
            if (refresh == null || clientId == null) return null;
            if (Revoke(refresh, clientId)) return null;
            return SignOutUnconfirmed;
        }

        public const string SignOutUnconfirmed = "OpenAI 쪽 연결 해제는 확인하지 못했어요. chatgpt.com 설정에서 연결된 앱을 확인해 주세요.";

        static bool Revoke(string refresh, string clientId)
        {
            try
            {
                var r = Web.Send("GET", DiscoveryUrl, new[] { "Accept", "application/json" }, null, null, TimeSpan.FromSeconds(20), null, Who);
                if (r.Status != 200) return false;
                string end = Json.S(Json.Parse(r.Body), "revocation_endpoint");
                // 토큰은 로그인한 곳(발급처와 같은 scheme·host·port)으로만 보낸다
                if (!SameOrigin(end, Issuer))
                {
                    Log.Write("ChatGPT 세션 끝내기 주소가 발급처와 달라 토큰을 보내지 않음");
                    return false;
                }
                var done = Web.Send("POST", end, new[] { "Accept", "application/json" }, "application/x-www-form-urlencoded",
                    Encoding.ASCII.GetBytes(Web.Query("token", refresh, "token_type_hint", "refresh_token", "client_id", clientId)),
                    TimeSpan.FromSeconds(20), null, Who);
                if (done.Status != 200) Log.Write("ChatGPT 세션 끝내기 실패: " + done.Status);
                return done.Status == 200;
            }
            catch (Exception ex)
            {
                Log.Write("ChatGPT 세션 끝내기 실패: " + ex.GetType().Name);
                return false;
            }
        }

        static bool SameOrigin(string url, string issuer)
        {
            Uri a, b;
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out a) || !Uri.TryCreate(issuer, UriKind.Absolute, out b)) return false;
            return a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
        }

        // ── 읽기(문서 models-and-inference) ──

        /// 쪽 그림과 질문을 보내고 답 글을 받는다.
        public static string Run(string system, string text, IList<byte[]> jpegs, TimeSpan limit, Job job)
        {
            job.Report("ChatGPT 연결 확인 중", -1);
            string access = AccessToken(job);
            string slug = Model(access, job);
            job.Report("ChatGPT가 읽는 중", -1);
            var answer = new StringBuilder();
            string ending = null, code = null, reason = null;
            int pieces = 0;
            var r = Web.Stream("POST", Api + "responses", new[] { "Authorization", "Bearer " + access, "Accept", "text/event-stream" },
                "application/json", Body(slug, system, text, jpegs), limit, job, Who,
                delegate (string line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) return true;   // event: 줄·빈 줄·':' 주석은 넘긴다
                    string data = line.Substring(5).Trim();
                    if (data == "[DONE]") return false;
                    Dictionary<string, object> e;
                    try { e = Json.Parse(data); }
                    catch { return true; }
                    string type = Json.S(e, "type");
                    if (type == "response.output_text.delta")
                    {
                        answer.Append(Json.S(e, "delta"));
                        if (++pieces % 40 == 0) job.Report("ChatGPT가 읽는 중 · 글자 " + answer.Length + "자", -1);
                        return true;
                    }
                    var response = Json.D(e, "response");
                    if (type == "response.completed") ending = "completed";   // 성공은 이것 하나뿐
                    else if (type == "response.incomplete")
                    {
                        ending = "incomplete";
                        reason = Json.S(Json.D(response, "incomplete_details"), "reason");
                    }
                    else if (type == "response.failed")
                    {
                        ending = "failed";
                        code = Json.S(Json.D(response, "error"), "code");
                    }
                    else if (type == "error")
                    {
                        ending = "error";
                        code = Json.S(e, "code") ?? Json.S(Json.D(e, "error"), "code");
                    }
                    return ending == null;
                });
            if (r.Status != 200) throw Failure(r.Status, r.Body);
            if (ending == "completed")
            {
                if (answer.Length == 0) throw new UserFacingException("ChatGPT가 빈 답을 보냈어요. 다시 해 보세요.");
                return answer.ToString();
            }
            if (ending == "incomplete")
            {
                Log.Write("ChatGPT 답이 다 오지 않음: " + (reason ?? "?"));
                if (reason == "content_filter") throw new UserFacingException("ChatGPT가 이 내용을 끝까지 읽지 않았어요.");
                if (answer.Length == 0) throw new UserFacingException("ChatGPT가 답을 끝내지 못했어요. 쪽 수를 줄여 다시 해 보세요.");
                return answer + "\n\n(답이 길어서 여기서 끊겼어요)";
            }
            if (ending == "failed" || ending == "error")
            {
                Log.Write("ChatGPT 읽기 실패: " + ending + " " + (code ?? "?"));
                throw new UserFacingException(ErrorText(code, null) ?? "ChatGPT가 읽다가 멈췄어요. 잠시 뒤 다시 해 주세요.");
            }
            throw new UserFacingException("ChatGPT 답이 중간에 끊겼어요. 다시 해 주세요.");
        }

        /// 요청 본문(Responses API). 안내 글은 instructions 칸에 넣는다(문서: role이 system인 항목은 받지 않음).
        /// 사용자 글에는 쪽 그림들 다음에 질문. store:false, stream:true. temperature·max_output_tokens 같은 칸은 받지 않아 보내지 않는다.
        static byte[] Body(string slug, string system, string text, IList<byte[]> jpegs)
        {
            var b = new StringBuilder();
            b.Append("{\"model\":").Append(Json.Str(slug));
            b.Append(",\"instructions\":").Append(Json.Str(system));
            b.Append(",\"input\":[{\"role\":\"user\",\"content\":[");
            foreach (byte[] j in jpegs)
            {
                b.Append("{\"type\":\"input_image\",\"image_url\":\"data:image/jpeg;base64,").Append(Convert.ToBase64String(j)).Append("\"},");
            }
            b.Append("{\"type\":\"input_text\",\"text\":").Append(Json.Str(text)).Append("}]}]");
            b.Append(",\"store\":false,\"stream\":true}");
            return Encoding.ASCII.GetBytes(b.ToString());
        }

        /// 쓸 모델: GET /v1/models의 models[] 가운데 visibility가 "list"인 첫 slug(한 시간 동안 기억).
        static string Model(string access, Job job)
        {
            lock (gate)
            {
                if (model != null && DateTime.UtcNow - modelAt < TimeSpan.FromHours(1)) return model;
            }
            var r = Web.Send("GET", Api + "models", new[] { "Authorization", "Bearer " + access, "Accept", "application/json" },
                null, null, TimeSpan.FromSeconds(30), job, Who);
            if (r.Status != 200) throw Failure(r.Status, r.Body);
            string pick = null;
            try
            {
                foreach (object o in Json.A(Json.Parse(r.Body), "models") ?? new object[0])
                {
                    var m = Json.Obj(o);
                    string slug = Json.S(m, "slug");
                    if (Json.S(m, "visibility") == "list" && !string.IsNullOrEmpty(slug))
                    {
                        pick = slug;
                        break;
                    }
                }
            }
            catch { }
            if (pick == null) throw new UserFacingException("지금 ChatGPT 요금제로 쓸 수 있는 모델을 찾지 못했어요. 잠시 뒤 다시 해 보세요.");
            lock (gate)
            {
                model = pick;
                modelAt = DateTime.UtcNow;
            }
            return pick;
        }

        // ── 오류(문서 errors-and-recovery) ──

        /// 200이 아닌 답. 스트림 전에 {"detail": "…"}로 올 때도 있다.
        static UserFacingException Failure(int status, string body)
        {
            string code = null, param = null;
            try
            {
                var d = Json.Parse(body);
                var err = Json.D(d, "error");
                code = Json.S(err, "code");
                param = Json.S(err, "param");
            }
            catch { }
            Log.Write("ChatGPT 요청 실패: " + status + " " + (code ?? "-"));
            if (status == 401 || code == "subscription_sharing_invalid_user")
            {
                ForgetTokens();
                return new UserFacingException("OpenAI가 이 PC의 로그인을 받지 않아서 로그아웃시켰어요. 설정 → AI 연결에서 다시 로그인해 주세요.");
            }
            string known = ErrorText(code, param);
            if (known != null) return new UserFacingException(known);
            if (status == 429) return new UserFacingException(LimitText);
            if (status == 503) return new UserFacingException("지금 ChatGPT 쪽에서 요금제 사용을 확인하지 못해요. 잠시 뒤 다시 해 보세요.");
            if (status == 403) return new UserFacingException("OpenAI가 이 요청을 받지 않았어요. 설정 → AI 연결에서 로그아웃한 뒤 다시 로그인해 보세요.");
            return new UserFacingException("ChatGPT가 요청을 받지 않았어요. (" + status + ")");
        }

        const string LimitText = "ChatGPT 요금제 사용 한도에 닿았어요. 설정 → AI 연결의 [사용량 보기]에서 남은 양을 확인해 주세요.";

        /// 문서에 나온 요금제 연결 오류 코드를 쉬운 말로. 모르는 코드면 null.
        static string ErrorText(string code, string param)
        {
            switch (code)
            {
                case "subscription_sharing_usage_limit_exceeded":
                    return LimitText;
                case "subscription_sharing_user_not_eligible":
                    return "이 ChatGPT 계정은 요금제 연결을 쓸 수 없어요(Plus·Pro 요금제가 필요해요). 다른 AI 연결을 골라 주세요.";
                case "subscription_sharing_usage_unavailable":
                case "subscription_sharing_user_unavailable":
                    return "지금 ChatGPT 쪽에서 요금제 사용을 확인하지 못해요. 잠시 뒤 다시 해 보세요.";
                case "subscription_sharing_unsupported_capability":
                    return "ChatGPT 요금제 연결이 이 요청" + (param != null ? "(" + param + ")" : "") + "을 받지 않았어요. 다른 AI 연결을 써 주세요.";
                case "subscription_sharing_route_not_supported":
                    return "ChatGPT 요금제 연결에서 이 요청 방식을 쓸 수 없어요. 다른 AI 연결을 써 주세요.";
                case "chatpass_v2_scope_not_authorized":
                case "chatpass_v2_invalid_authorization_context":
                    return "ChatGPT 요금제 사용 허락이 빠져 있어요. 설정 → AI 연결에서 로그아웃한 뒤, 다시 로그인하면서 허락해 주세요.";
            }
            return null;
        }

        // ── 작은 도우미 ──

        static long Number(Dictionary<string, object> d, string name)
        {
            object v = Json.Get(d, name);
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is decimal) return (long)(decimal)v;
            if (v is double) return (long)(double)v;
            long n;
            return v is string && long.TryParse((string)v, out n) ? n : 0;
        }

        /// earliest_refresh_at: 초(또는 밀리초) 수나 ISO 날짜 글 모두 받는다. 모르면 MinValue.
        static DateTime When(object v)
        {
            if (v is string)
            {
                DateTime t;
                if (DateTime.TryParse((string)v, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out t)) return t;
                return DateTime.MinValue;
            }
            long n = Number(new Dictionary<string, object> { { "v", v } }, "v");
            if (n > 100000000000L) n /= 1000;   // 밀리초
            return FromUnix(n);
        }

        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static DateTime FromUnix(long seconds)
        {
            return seconds <= 0 ? DateTime.MinValue : Epoch.AddSeconds(seconds);
        }

        static long ToUnix(DateTime t)
        {
            return t <= Epoch ? 0 : (long)(t - Epoch).TotalSeconds;
        }
    }
}
