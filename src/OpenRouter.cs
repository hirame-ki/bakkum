// OpenRouter 연결: 선생님의 OpenRouter 계정으로 무료(:free) AI 모델을 쓴다.
// 키는 브라우저 로그인(OAuth PKCE)으로 받거나, 선생님이 만든 키를 붙여 넣는다. 키는 윈도우 사용자 암호화로 저장한다.
// 근거: OpenRouter 문서(openrouter.ai/docs: OAuth PKCE, 키 정보·사용 한도, 채팅 요청·오류, 공급자 고르기), 2026-11-08 읽음.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bakkum
{
    public static class OpenRouter
    {
        static string Api = "https://openrouter.ai/api/v1/";
        static string AuthPage = "https://openrouter.ai/auth";
        public const string KeysPage = "https://openrouter.ai/settings/keys";
        public const string PrivacyPage = "https://openrouter.ai/settings/privacy";
        const string Who = "OpenRouter";

        /// 그림을 읽을 수 있는 무료 모델 중 먼저 써 볼 순서(2026-11-08 OpenRouter 목록 기준).
        /// 무료 모델은 자주 바뀌므로, 쓸 때마다 목록에 아직 있는지 확인하고 없으면 다른 무료 그림 모델을 쓴다.
        static readonly string[] Preferred =
        {
            "google/gemma-4-31b-it:free",
            "nvidia/nemotron-3.5-super-vl:free",
            "google/gemma-4-26b-a4b-it:free",
            "dots-studio/dots-3-note-preview:free",
            "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free",
        };

        /// 한 번 읽을 때 차례로 써 볼 모델 수(붐비는 모델을 건너뛸 때)
        const int MaxTries = 4;

        static readonly Regex Daily = new Regex(@"free-models-per-day", RegexOptions.IgnoreCase);
        static readonly Regex DataPolicy = new Regex(@"data policy|privacy", RegexOptions.IgnoreCase);

        static readonly object CacheGate = new object();
        static List<string> cachedModels;
        static DateTime cachedAt;

        /// 시험(/selftest-net) 동안만: 이 PC 안의 가짜 서버로 돌린다.
        internal static void UseTestServer(string baseUrl)
        {
            Web.RequireLocal(baseUrl);
            Api = baseUrl + "/openrouter/api/v1/";
            AuthPage = baseUrl + "/openrouter/auth";
            lock (CacheGate) cachedModels = null;
        }

        /// 붙여 넣은 키를 다듬는다. OpenRouter 키는 'sk-or-'로 시작한다(openrouter.ai의 Keys 화면에서 만든 키 모양).
        /// 다른 회사 키를 잘못 붙여 넣었을 때 그 키가 OpenRouter로 가지 않도록, 모양이 다르면 null.
        public static string CleanKey(string raw)
        {
            string k = Web.CleanSecret(raw);
            return k != null && k.StartsWith("sk-or-", StringComparison.Ordinal) ? k : null;
        }

        static string[] Headers(string key)
        {
            return new[] { "Authorization", "Bearer " + key, "X-Title", AppInfo.Id };
        }

        // ── 로그인: OAuth PKCE(openrouter.ai/docs/guides/overview/auth/oauth) ──
        //  1) openrouter.ai/auth?callback_url=…&code_challenge=…&code_challenge_method=S256&state=… 를 기본 브라우저로 연다.
        //     돌아오는 곳은 이 PC 안 문이다(문서: localhost는 어느 포트든 받는다). state는 보낸 그대로 돌아온다.
        //  2) 돌아온 code를 POST /api/v1/auth/keys {code, code_verifier, code_challenge_method}로 보내면 {key}가 온다.
        //     403은 code나 code_verifier가 맞지 않거나 code가 만료된 것이다.

        /// 브라우저에서 OpenRouter 로그인 → 키. 오래 걸리니 화면 밖 스레드에서 부른다.
        /// 실패하면 null과 쉬운 말 이유(취소면 이유도 null).
        public static string SignIn(Func<bool> cancelled, out string failure)
        {
            failure = null;
            string verifier = Web.RandomToken();
            string state = Web.RandomToken();
            Dictionary<string, string> back;
            try
            {
                using (var door = new Loopback("/callback", true,
                    delegate (Dictionary<string, string> q) { return Value(q, "state") == state; },
                    delegate (Dictionary<string, string> q)
                    {
                        return Value(q, "code") != null
                            ? Web.ResultPage("OpenRouter와 연결하는 중이에요", "이 창은 닫아도 돼요. " + AppInfo.Name + " 설정 창으로 돌아가 주세요.", true)
                            : Web.ResultPage("연결하지 못했어요", AppInfo.Name + " 설정 창에서 다시 해 주세요.", false);
                    }))
                {
                    string url = AuthPage + "?" + Web.Query(
                        "callback_url", "http://localhost:" + door.Port + "/callback",
                        "code_challenge", Web.Challenge(verifier),
                        "code_challenge_method", "S256",
                        "state", state);
                    if (!Web.OpenBrowser(url))
                    {
                        failure = "브라우저를 열지 못했어요.";
                        return null;
                    }
                    back = door.Wait(TimeSpan.FromMinutes(10), cancelled);
                }
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Log.Write("OpenRouter 로그인 문 열기 실패: " + ex.SocketErrorCode);
                failure = "로그인을 받을 준비를 하지 못했어요. 다시 해 주세요.";
                return null;
            }
            if (back == null)
            {
                if (!cancelled()) failure = "로그인을 기다리다 시간이 다 됐어요. 다시 해 주세요.";
                return null;
            }
            string code = Value(back, "code");
            if (code == null)
            {
                failure = "OpenRouter에서 연결을 허락하지 않았어요.";
                return null;
            }
            return Exchange(code, verifier, out failure);
        }

        static string Value(Dictionary<string, string> q, string name)
        {
            string v;
            return q != null && q.TryGetValue(name, out v) && v.Length > 0 ? v : null;
        }

        /// 돌아온 code를 키로 바꾼다(POST /api/v1/auth/keys).
        static string Exchange(string code, string verifier, out string failure)
        {
            failure = null;
            string body = "{\"code\":" + Json.Str(code) + ",\"code_verifier\":" + Json.Str(verifier) + ",\"code_challenge_method\":\"S256\"}";
            WebReply r;
            try
            {
                r = Web.Send("POST", Api + "auth/keys", null, "application/json", Encoding.ASCII.GetBytes(body), TimeSpan.FromSeconds(30), null, Who);
            }
            catch (UserFacingException ex)
            {
                failure = ex.Message;
                return null;
            }
            if (r.Status == 403)
            {
                failure = "OpenRouter가 이번 로그인을 받아 주지 않았어요(시간이 너무 지났거나 확인 값이 맞지 않아요). 다시 해 주세요.";
                return null;
            }
            if (r.Status < 200 || r.Status > 299)
            {
                Log.Write("OpenRouter 키 교환 실패: " + r.Status);
                failure = "OpenRouter가 키를 주지 않았어요. (" + r.Status + ")";
                return null;
            }
            string key = null;
            try { key = CleanKey(Json.S(Json.Parse(r.Body), "key")); }
            catch { }
            if (key == null) failure = "OpenRouter가 보낸 키를 읽지 못했어요.";
            return key;
        }

        // ── 키 정보: GET /api/v1/key(openrouter.ai/docs/api-reference/limits) ──
        // data.free_model_daily_requests = {used, limit, remaining}: 오늘(UTC 기준 하루) 무료 모델 사용량.

        /// 키 정보만 묻는다(문서 내용은 보내지 않는다). 연결이 안 되면 UserFacingException.
        static WebReply KeyInfo(string key)
        {
            return Web.Send("GET", Api + "key", Headers(key), null, null, TimeSpan.FromSeconds(20), null, Who);
        }

        /// 키가 살아 있는지. 맞으면 null, 아니면 쉬운 말 이유.
        public static string CheckKey(string key)
        {
            WebReply r;
            try { r = KeyInfo(key); }
            catch (UserFacingException ex)
            {
                return ex.Message;
            }
            if (r.Status == 200) return null;
            if (r.Status == 401 || r.Status == 403) return "OpenRouter 키가 맞지 않거나 지워졌어요.";
            return "OpenRouter가 키를 확인해 주지 않았어요. (" + r.Status + ")";
        }

        public class Quota
        {
            public int Used, Limit, Remaining;
        }

        /// 오늘 무료 모델 사용량. 알 수 없으면 null.
        public static Quota TodayQuota(string key)
        {
            WebReply r;
            try { r = KeyInfo(key); }
            catch (UserFacingException)
            {
                return null;
            }
            if (r.Status != 200) return null;
            Dictionary<string, object> daily = null;
            try { daily = Json.D(Json.D(Json.Parse(r.Body), "data"), "free_model_daily_requests"); }
            catch { }
            int limit, used, left;
            bool hasLimit = Whole(daily, "limit", out limit);
            bool hasUsed = Whole(daily, "used", out used);
            bool hasLeft = Whole(daily, "remaining", out left);
            if (!hasLimit || limit <= 0 || !(hasUsed || hasLeft)) return null;
            if (!hasLeft) left = limit - used;
            if (!hasUsed) used = limit - left;
            return new Quota { Used = Math.Max(0, used), Limit = limit, Remaining = Math.Max(0, Math.Min(limit, left)) };
        }

        /// 숫자 칸을 정수로 읽는다. 없거나 숫자가 아니면 false.
        static bool Whole(Dictionary<string, object> d, string name, out int value)
        {
            value = 0;
            object v = Json.Get(d, name);
            if (v is int) value = (int)v;
            else if (v is long) value = (int)(long)v;
            else if (v is decimal) value = (int)(decimal)v;
            else if (v is double) value = (int)(double)v;
            else return false;
            return true;
        }

        // ── 모델 고르기 ──

        /// OpenRouter 목록에서 그림을 받는 무료 모델들(한 시간 동안 기억). 못 읽으면 null.
        static List<string> FreeVisionModels()
        {
            lock (CacheGate)
            {
                if (cachedModels != null && DateTime.UtcNow - cachedAt < TimeSpan.FromHours(1)) return cachedModels;
            }
            try
            {
                var r = Web.Send("GET", Api + "models", null, null, null, TimeSpan.FromSeconds(30), null, Who);
                if (r.Status != 200) return null;
                var data = Json.A(Json.Parse(r.Body), "data");
                if (data == null) return null;
                var list = new List<string>();
                foreach (var x in data)
                {
                    var m = Json.Obj(x);
                    string id = Json.S(m, "id");
                    if (id == null || !id.EndsWith(":free")) continue;
                    var arch = Json.D(m, "architecture");
                    var input = Json.A(arch, "input_modalities");
                    var output = Json.A(arch, "output_modalities");
                    if (input == null || !input.Contains("image")) continue;
                    if (output != null && !output.Contains("text")) continue;
                    list.Add(id);
                }
                lock (CacheGate)
                {
                    cachedModels = list;
                    cachedAt = DateTime.UtcNow;
                }
                return list;
            }
            catch (Exception ex)
            {
                Log.Write("OpenRouter 모델 목록 실패: " + ex.GetType().Name);
                return null;
            }
        }

        /// 써 볼 모델 순서: 지난번에 잘 된 모델 → 정해 둔 순서 → 그 밖의 무료 그림 모델.
        static List<string> Candidates()
        {
            var listed = FreeVisionModels();
            var order = new List<string>();
            string last = AppSettings.Current.OpenRouterLastModel;
            if (!string.IsNullOrEmpty(last) && (listed == null || listed.Contains(last))) order.Add(last);
            foreach (var p in Preferred)
            {
                if (!order.Contains(p) && (listed == null || listed.Contains(p))) order.Add(p);
            }
            if (listed != null)
            {
                foreach (var m in listed)
                {
                    if (!order.Contains(m)) order.Add(m);
                }
            }
            return order;
        }

        static string Short(string model)
        {
            string m = model;
            int slash = m.IndexOf('/');
            if (slash >= 0) m = m.Substring(slash + 1);
            if (m.EndsWith(":free")) m = m.Substring(0, m.Length - 5);
            return m;
        }

        // ── 읽기 ──

        public static string Run(string key, string system, string text, IList<byte[]> jpegs, TimeSpan limit, Job job)
        {
            job.Report("OpenRouter 무료 모델을 고르는 중", -1);
            var models = Candidates();
            if (models.Count == 0) throw new UserFacingException("지금 OpenRouter에 그림을 읽을 수 있는 무료 모델이 없어요. 다른 AI 연결을 골라 주세요.");
            bool noTraining = AppSettings.Current.OpenRouterNoTraining;
            Problem kept = null;
            int tries = 0;
            foreach (string model in models)
            {
                if (tries++ >= MaxTries) break;
                job.Check();
                job.Report("OpenRouter가 읽는 중 · " + Short(model), -1);
                byte[] payload = Body(model, system, text, jpegs, noTraining);
                var r = Web.Send("POST", Api + "chat/completions", Headers(key), "application/json", payload, limit, job, Who);
                Problem p;
                if (r.Status == 200)
                {
                    string answer = Answer(r.Body, out p);
                    if (answer != null)
                    {
                        if (AppSettings.Current.OpenRouterLastModel != model)
                        {
                            AppSettings.Current.OpenRouterLastModel = model;
                            AppSettings.Current.SaveQuietly();
                        }
                        return answer;
                    }
                }
                else p = Failure(r.Status, r.Body, noTraining);
                if (!p.TryNext) throw new UserFacingException(p.Message);
                // 다음 모델로. 알릴 이유는 더 쓸모 있는 것을 남긴다(개인정보 설정 > 그 밖).
                if (kept == null || p.Weight > kept.Weight) kept = p;
            }
            throw new UserFacingException(kept != null ? kept.Message : "그림을 읽을 수 있는 OpenRouter 무료 모델을 찾지 못했어요.");
        }

        /// 시스템 안내를 사용자 글 맨 앞에 붙인다: 시스템 역할을 받지 않는 무료 모델도 있어서.
        static byte[] Body(string model, string system, string text, IList<byte[]> jpegs, bool noTraining)
        {
            var b = new StringBuilder();
            b.Append("{\"model\":").Append(Json.Str(model));
            b.Append(",\"messages\":[{\"role\":\"user\",\"content\":[");
            b.Append("{\"type\":\"text\",\"text\":").Append(Json.Str(system)).Append("},");
            foreach (var j in jpegs)
            {
                b.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,").Append(Convert.ToBase64String(j)).Append("\"}},");
            }
            b.Append("{\"type\":\"text\",\"text\":").Append(Json.Str(text)).Append("}]}]");
            if (noTraining) b.Append(",\"provider\":{\"data_collection\":\"deny\"}");
            b.Append(",\"stream\":false}");
            return Encoding.ASCII.GetBytes(b.ToString());
        }

        class Problem
        {
            public string Message;
            public bool TryNext;
            public int Weight;
        }

        static Problem Next(string message, int weight)
        {
            return new Problem { Message = message, TryNext = true, Weight = weight };
        }

        static Problem Stop(string message)
        {
            return new Problem { Message = message, TryNext = false };
        }

        static string Answer(string body, out Problem problem)
        {
            problem = null;
            Dictionary<string, object> d;
            try { d = Json.Parse(body); }
            catch
            {
                problem = Next("OpenRouter 답을 읽지 못했어요.", 1);
                return null;
            }
            var err = Json.D(d, "error");
            if (err != null)
            {
                int code = Json.I(err, "code");
                problem = Failure(code > 0 ? code : 502, body, AppSettings.Current.OpenRouterNoTraining);
                return null;
            }
            var choices = Json.A(d, "choices");
            if (choices == null || choices.Length == 0)
            {
                problem = Next("OpenRouter 모델이 빈 답을 보냈어요.", 1);
                return null;
            }
            var c = Json.Obj(choices[0]);
            if (Json.D(c, "error") != null)
            {
                problem = Next("OpenRouter 모델이 답하다 멈췄어요. 잠시 뒤에 다시 해 주세요.", 1);
                return null;
            }
            string reason = Json.S(c, "finish_reason");
            var msg = Json.D(c, "message");
            string text = null;
            object content = Json.Get(msg, "content");
            if (content is string) text = (string)content;
            else if (content is object[])
            {
                var sb = new StringBuilder();
                foreach (var x in (object[])content)
                {
                    string t = Json.S(Json.Obj(x), "text");
                    if (t != null) sb.Append(t);
                }
                text = sb.ToString();
            }
            if (reason == "content_filter")
            {
                problem = Stop("OpenRouter 모델이 이 내용을 받지 않았어요.");
                return null;
            }
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                problem = Next("OpenRouter 모델이 빈 답을 보냈어요.", 1);
                return null;
            }
            if (reason == "length") text += "\n\n(답이 길어서 여기서 끊겼어요)";
            return text;
        }

        static Problem Failure(int status, string body, bool noTraining)
        {
            string message = "";
            string raw = "";
            try
            {
                var e = Json.D(Json.Parse(body), "error");
                message = Json.S(e, "message") ?? "";
                var meta = Json.D(e, "metadata");
                raw = (Json.S(meta, "raw") ?? "") + " " + (Json.S(meta, "provider_name") ?? "");
            }
            catch { }
            string all = message + " " + raw;
            Log.Write("OpenRouter 실패: " + status + " " + (message.Length > 80 ? message.Substring(0, 80) : message));
            if (status == 401) return Stop("OpenRouter 키가 맞지 않거나 지워졌어요. 설정 → AI 연결에서 다시 연결해 주세요.");
            if (status == 402) return Stop("OpenRouter 계정의 크레딧이 모자라서 쓸 수 없어요. openrouter.ai에서 계정을 확인해 주세요.");
            if (status == 429 && Daily.IsMatch(all)) return Stop("오늘 OpenRouter 무료 사용량을 다 썼어요. 내일 다시 하거나 설정에서 다른 AI 연결을 골라 주세요.");
            if (status == 403) return Stop("OpenRouter가 이 내용을 받지 않았어요.");
            if (status == 404 && DataPolicy.IsMatch(all))
            {
                return Next(noTraining
                    ? "'기록·학습하지 않는 곳으로만 보내기'에 맞는 무료 모델이 지금 없어요. 설정 → AI 연결에서 이 스위치를 끄면 쓸 수 있어요."
                    : "OpenRouter 개인정보 설정 때문에 무료 모델을 쓸 수 없어요. openrouter.ai → Settings → Privacy에서 무료 모델 사용을 허락해 주세요.", 3);
            }
            if (status == 429) return Next("OpenRouter 무료 모델이 지금 붐벼요. 1분쯤 뒤에 다시 해 주세요.", 2);
            if (status == 404) return Next("쓸 수 있는 OpenRouter 무료 모델을 찾지 못했어요.", 1);
            if (status == 400 || status == 408 || status == 413 || status >= 500)
            {
                return Next("OpenRouter 무료 모델이 지금 답하지 못해요. 잠시 뒤에 다시 해 주세요.", 1);
            }
            return Stop("OpenRouter가 요청을 받지 않았어요. (" + status + ")");
        }
    }
}
