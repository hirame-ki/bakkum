// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 네트워크 연결 시험: Bakkum.exe /selftest-net 포트
// 먼저 이 PC 안에 가짜 서버를 띄운다: python tools\mock_ai.py 포트
// OpenRouter·ChatGPT 연결(로그인 → 키·토큰 받기 → 읽기 → 새로 고침 → 로그아웃)을 가짜 서버에 대고 처음부터 끝까지 돌려 본다.
// Claude Code 연결은 가짜 Claude Code(tools\mock_claude.py)에 대고 돌려 본다(진짜 Claude Code는 부르지 않는다).
// 진짜 서버·계정·설정은 건드리지 않는다: 주소는 127.0.0.1만 받고, 설정·로그인 파일은 임시 폴더에 두었다가 지운다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Bakkum
{
    static class SelfTest
    {
        // 가짜 서버(tools\mock_ai.py)와 글자까지 같아야 한다
        const string Guide = "표는 마크다운 표로 옮겨 주세요.";
        const string Prompt = "이 쪽의 글자를 그대로 옮겨 주세요.";
        const string OrKey = "sk-or-v1-selftest-0123456789abcdef";

        static readonly IList<byte[]> Pictures = new List<byte[]>
        {
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9 },
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 5, 6, 7, 8, 0xFF, 0xD9 },
        };

        static string Base;
        static string Temp;
        static int passed, failed;
        static volatile string lastPage;
        static readonly ManualResetEvent browsed = new ManualResetEvent(true);
        static readonly List<string> probes = new List<string>();

        public static int Run(string[] args)
        {
            Cli.Active = true;
            int port;
            if (args.Length < 2 || !int.TryParse(args[1], out port) || port <= 0 || port > 65535)
            {
                Cli.Print("사용: /selftest-net 포트   (먼저 python tools\\mock_ai.py 포트 로 가짜 서버를 띄운다)");
                return 2;
            }
            Base = "http://127.0.0.1:" + port;
            string temp = Path.Combine(Path.GetTempPath(), "bakkum-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AppInfo.DataDirOverride = temp;
            Temp = temp;
            OpenRouter.UseTestServer(Base);
            ChatGpt.UseTestServer(Base);
            Web.BrowserHook = Browse;
            try
            {
                OpenRouterTests();
                ChatGptTests();
                MockChecks();
                ClaudeTests();
            }
            catch (Exception ex)
            {
                Check("시험이 끝까지 돈다", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Web.BrowserHook = null;
                browsed.WaitOne(5000);
                try { Directory.Delete(temp, true); } catch { }
            }
            Cli.Print("");
            Cli.Print("결과: 통과 " + passed + " · 실패 " + failed);
            return failed == 0 ? 0 : 1;
        }

        // ── OpenRouter ──

        static void OpenRouterTests()
        {
            Cli.Print("[OpenRouter]");
            Set("{\"reset\":true}");
            string failure;

            string key = OpenRouter.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("브라우저 로그인으로 키를 받는다", key == OrKey, failure ?? key);
            Check("브라우저에 '연결하는 중이에요' 쪽이 뜬다", Page("200", "연결하는 중이에요"), Short(lastPage));
            CheckProbes();

            Set("{\"or_auth\":\"badcode\"}");
            string none = OpenRouter.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("키 교환을 거절하면 이유를 알려 준다", none == null && Has(failure, "받아 주지 않았어요"), failure);
            Check("붙여 넣은 키: 따옴표·Bearer를 떼고, 다른 회사 키(sk-or-가 아님)는 받지 않는다",
                OpenRouter.CleanKey(" \"Bearer " + OrKey + "\" ") == OrKey && OpenRouter.CleanKey("AIza" + "SyA-0123456789abcdefghijklmnopqrstu") == null &&
                OpenRouter.CleanKey("sk-or-v1-한글이섞인키0123456789") == null && Web.CleanSecret("AIza" + "SyA-0123456789abcdefghijklmnopqrstu") != null, null);
            string oldGemini = "AIza" + "SyA-0123456789abcdefghijklmnopqrstu", newGemini = "AQ." + "Ab8RN6-0123456789_abcdefghij.klmnopqrstuvwxyz";
            Check("복사한 글에서 Gemini 키만 알아본다: 옛 키·AQ. 키는 받고 다른 글은 지나친다",
                Gemini.FindKey(" \"" + oldGemini + "\" ") == oldGemini && Gemini.FindKey(newGemini + "\r\n") == newGemini &&
                Gemini.FindKey("복사한 보통 문장이에요 0123456789") == null && Gemini.FindKey("sk-or-v1-0123456789abcdefghijklmnopqrstuv") == null &&
                Gemini.FindKey("AQ.short") == null && Gemini.FindKey(new string('A', 5000)) == null && Gemini.FindKey(null) == null, null);
            probes.Clear();

            Check("키를 확인한다", OpenRouter.CheckKey(OrKey) == null, OpenRouter.CheckKey(OrKey));
            Check("틀린 키는 맞지 않다고 한다", Has(OpenRouter.CheckKey("sk-or-v1-wrong-000000000000000000"), "맞지 않거나"), null);
            var q = OpenRouter.TodayQuota(OrKey);
            Check("오늘 무료 사용량을 읽는다(50번 중 38번 남음)", q != null && q.Used == 12 && q.Limit == 50 && q.Remaining == 38,
                q == null ? "null" : q.Used + "/" + q.Limit + "/" + q.Remaining);

            bool threw;
            Set("{\"or_chat\":\"fallback\"}");
            string answer = Read(OrKey, out threw);
            Check("붐빔(429)·개인정보 설정(404) 모델을 건너뛰고 세 번째 무료 그림 모델로 읽는다", !threw && answer == "모델:test/vision-c:free", answer);
            Check("잘 된 모델을 기억한다", AppSettings.Current.OpenRouterLastModel == "test/vision-c:free", AppSettings.Current.OpenRouterLastModel);
            Check("모델을 세 번 써 봤다", Number("or_chat_calls") == 3, Number("or_chat_calls").ToString());

            Set("{\"or_chat\":\"ok\"}");
            answer = Read(OrKey, out threw);
            Check("다음에는 기억한 모델부터 쓴다", !threw && answer == "모델:test/vision-c:free" && Number("or_chat_calls") == 1, answer);

            AppSettings.Current.OpenRouterNoTraining = true;
            Set("{\"or_chat\":\"ok\",\"or_expect_deny\":true}");
            answer = Read(OrKey, out threw);
            Check("'기록·학습하지 않는 곳으로만'을 켜면 data_collection=deny를 함께 보낸다", !threw, answer);
            Set("{\"or_chat\":\"policy\",\"or_expect_deny\":true}");
            answer = Read(OrKey, out threw);
            Check("그런 무료 모델이 없으면 스위치를 끄라고 알려 준다", threw && Has(answer, "기록·학습하지 않는 곳으로만 보내기"), answer);
            AppSettings.Current.OpenRouterNoTraining = false;

            Set("{\"or_chat\":\"policy\",\"or_expect_deny\":false}");
            answer = Read(OrKey, out threw);
            Check("개인정보 설정 때문이면 OpenRouter 설정을 알려 준다", threw && Has(answer, "Privacy"), answer);

            Set("{\"or_chat\":\"daily\"}");
            answer = Read(OrKey, out threw);
            Check("오늘 무료 사용량을 다 쓰면 바로 멈추고 알린다", threw && Has(answer, "무료 사용량을 다 썼어요") && Number("or_chat_calls") == 1, answer);

            Set("{\"or_chat\":\"unauthorized\"}");
            answer = Read(OrKey, out threw);
            Check("키가 지워졌으면(401) 다시 연결하라고 한다", threw && Has(answer, "다시 연결해 주세요") && Number("or_chat_calls") == 1, answer);

            Set("{\"or_chat\":\"length\"}");
            answer = Read(OrKey, out threw);
            Check("답이 길어 끊기면 끝에 알려 준다", !threw && answer == "부분\n\n(답이 길어서 여기서 끊겼어요)", answer);

            Set("{\"or_chat\":\"error200\"}");
            answer = Read(OrKey, out threw);
            Check("200 안에 오류가 오면 다음 모델로 넘어간다", !threw && answer == "모델:google/gemma-4-31b-it:free", answer);
        }

        static string Read(string key, out bool threw)
        {
            threw = false;
            try { return OpenRouter.Run(key, Guide, Prompt, Pictures, TimeSpan.FromSeconds(30), NewJob()); }
            catch (UserFacingException ex)
            {
                threw = true;
                return ex.Message;
            }
        }

        // ── ChatGPT ──

        static void ChatGptTests()
        {
            Cli.Print("[ChatGPT]");
            Set("{\"reset\":true}");
            Check("처음에는 로그인돼 있지 않다", !ChatGpt.SignedIn, null);
            string failure;

            Set("{\"gpt_auth\":\"deny\"}");
            bool ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("OpenAI 화면에서 거절하면 이유를 알려 준다", !ok && Has(failure, "허락하지 않았어요"), failure);
            Check("거절하면 브라우저에 '연결하지 못했어요' 쪽이 뜬다", Page("200", "연결하지 못했어요"), Short(lastPage));
            CheckProbes();

            Set("{\"gpt_auth\":\"ok\",\"gpt_badsig\":true}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("서명이 틀린 로그인 정보는 받지 않는다", !ok && Has(failure, "맞지 않아요"), failure);
            probes.Clear();

            Set("{\"gpt_badsig\":false,\"gpt_aud\":\"someone-else\"}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("다른 앱 앞으로 나온 로그인 정보(aud)는 받지 않는다", !ok && Has(failure, "맞지 않아요"), failure);
            probes.Clear();

            Set("{\"gpt_aud\":null,\"gpt_badnonce\":true}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("nonce가 다른 로그인 정보는 받지 않는다", !ok && Has(failure, "맞지 않아요"), failure);
            probes.Clear();

            Set("{\"gpt_badnonce\":false,\"gpt_scope\":\"noplan\"}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("요금제 사용을 허락하지 않으면 그렇게 알려 준다", !ok && Has(failure, "요금제 사용을 허락하지 않았어요"), failure);
            Check("실패한 로그인은 아무것도 저장하지 않는다", !ChatGpt.SignedIn && ChatGpt.TestClientId() == null, ChatGpt.TestClientId());
            probes.Clear();

            Set("{\"gpt_scope\":\"full\",\"gpt_return_client\":\"none\"}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("처음 등록인데 client_id가 돌아오지 않으면(등록 미완료) 받지 않는다", !ok && Has(failure, "등록을 마치지 못했어요") && ChatGpt.TestClientId() == null, failure);
            probes.Clear();

            Set("{\"gpt_return_client\":\"normal\"}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("로그인한다(PKCE·서명·발급처·대상·nonce 확인)", ok && ChatGpt.SignedIn, failure);
            Check("처음 등록은 dynamic_agent_client + 이름 알림(바꿈)", State("last_authorize_client") == "dynamic_agent_client" && State("last_name_hint") == "바꿈",
                State("last_authorize_client") + " / " + State("last_name_hint"));
            Check("받은 client_id를 저장한다", ChatGpt.TestClientId() == "client-selftest", ChatGpt.TestClientId());
            Check("계정 이메일은 이번 실행에서만 보여 준다", ChatGpt.Account == "teacher@example.com", ChatGpt.Account);
            Check("첫 연결 안내는 한 번만", ChatGpt.TakeFirstNotice() && !ChatGpt.TakeFirstNotice(), null);
            probes.Clear();

            bool threw;
            Set("{\"gpt_stream\":\"ok\"}");
            string answer = Ask(out threw);
            Check("스트리밍 답을 이어 붙여 읽는다(숨긴 모델은 건너뜀)", !threw && answer == "안녕하세요", answer);

            Set("{\"gpt_stream\":\"incomplete\"}");
            answer = Ask(out threw);
            Check("답이 길어 끊기면 끝에 알려 준다", !threw && answer == "부분\n\n(답이 길어서 여기서 끊겼어요)", answer);

            Set("{\"gpt_stream\":\"limit\"}");
            answer = Ask(out threw);
            Check("요금제 사용 한도(스트림 안 오류)를 쉬운 말로 알린다", threw && Has(answer, "사용 한도에 닿았어요"), answer);

            Set("{\"gpt_stream\":\"http429\"}");
            answer = Ask(out threw);
            Check("요금제 사용 한도(429 답)를 쉬운 말로 알린다", threw && Has(answer, "사용 한도에 닿았어요"), answer);

            Set("{\"gpt_stream\":\"failed\"}");
            answer = Ask(out threw);
            Check("읽다가 실패하면(response.failed) 다시 하라고 한다", threw && Has(answer, "읽다가 멈췄어요"), answer);

            Set("{\"gpt_stream\":\"not_eligible\"}");
            answer = Ask(out threw);
            Check("요금제 연결을 쓸 수 없는 계정이면(user_not_eligible) 그렇게 알리고 로그인은 둔다", threw && Has(answer, "Plus·Pro") && ChatGpt.SignedIn, answer);

            Set("{\"gpt_stream\":\"unsupported\"}");
            answer = Ask(out threw);
            Check("받지 않는 요청이면(unsupported_capability) 다른 AI 연결을 권한다", threw && Has(answer, "다른 AI 연결"), answer);

            Set("{\"gpt_stream\":\"detail503\"}");
            answer = Ask(out threw);
            Check("스트림 전에 {\"detail\":…} 503이 오면 잠시 뒤 다시 하라고 한다", threw && Has(answer, "잠시 뒤"), answer);

            ChatGpt.TestExpireNow();
            Set("{\"gpt_stream\":\"ok\"}");
            answer = Ask(out threw);
            Check("토큰이 끝나 가면 새로 고친 뒤 읽는다", !threw && answer == "안녕하세요" && Number("refresh_calls") == 1, answer);
            Check("돌려받은 새 새로 고침 토큰을 저장한다", ChatGpt.TestRefreshToken() == State("refresh"), null);

            ChatGpt.TestExpireNow();
            Set("{\"gpt_refresh\":\"invalid_grant\"}");
            answer = Ask(out threw);
            Check("새로 고침을 거절당하면(invalid_grant) 로그아웃하고 다시 로그인하라고 한다", threw && Has(answer, "로그인되어 있지 않아요") && !ChatGpt.SignedIn, answer);
            Check("로그아웃돼도 등록한 client_id는 남긴다", ChatGpt.TestClientId() == "client-selftest", ChatGpt.TestClientId());

            Set("{\"gpt_refresh\":\"ok\"}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("다시 로그인은 저장한 client_id로, 이름 알림 없이", ok && State("last_authorize_client") == "client-selftest" && State("last_name_hint") == null,
                failure ?? State("last_authorize_client") + " / " + State("last_name_hint"));
            probes.Clear();

            ChatGpt.TestExpireNow();
            Set("{\"gpt_refresh\":\"refresh_token_reused\"}");
            answer = Ask(out threw);
            Check("새로 고침 토큰이 이미 쓰였다고 하면(refresh_token_reused) 로그아웃하고 다시 로그인하라고 한다", threw && Has(answer, "로그인되어 있지 않아요") && !ChatGpt.SignedIn, answer);
            Set("{\"gpt_refresh\":\"ok\"}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            probes.Clear();

            ChatGpt.TestExpireNow();
            Set("{\"gpt_refresh\":\"invalid_client\"}");
            answer = Ask(out threw);
            Check("등록 문제(invalid_client)면 로그인은 두고, 로그아웃 뒤 다시 로그인하라고 한다", ok && threw && Has(answer, "등록에 문제") && ChatGpt.SignedIn, failure ?? answer);
            Set("{\"gpt_refresh\":\"ok\"}");

            Set("{\"gpt_stream\":\"unauthorized\"}");
            answer = Ask(out threw);
            Check("OpenAI가 토큰을 거절하면(401) 이 PC의 로그인을 지운다", threw && Has(answer, "로그아웃시켰어요") && !ChatGpt.SignedIn, answer);

            Set("{\"gpt_stream\":\"ok\"}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            probes.Clear();
            string notice = ChatGpt.SignOut();
            Check("로그아웃하면 이 PC에서 지우고, OpenAI 쪽 세션도 끝낸다(새로 고침 토큰 → revocation_endpoint, 빈 200)",
                ok && notice == null && !ChatGpt.SignedIn && ChatGpt.Account == null && Number("revoke_calls") == 1, failure ?? notice ?? Number("revoke_calls").ToString());
            Check("로그아웃해도 등록(client_id)은 남긴다", ChatGpt.TestClientId() == "client-selftest", ChatGpt.TestClientId());

            Set("{\"gpt_return_client\":\"other\"}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            Check("다시 로그인 때 저장한 것과 다른 client_id가 돌아오면 받지 않는다", !ok && Has(failure, "맞지 않아요") && !ChatGpt.SignedIn && ChatGpt.TestClientId() == "client-selftest", failure);
            probes.Clear();

            Set("{\"gpt_return_client\":\"normal\",\"gpt_revoke\":\"fail\"}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            probes.Clear();
            notice = ChatGpt.SignOut();
            Check("OpenAI 쪽 해제가 실패하면 이 PC에서는 지우고, 확인하지 못했다고 알린다", ok && notice == ChatGpt.SignOutUnconfirmed && !ChatGpt.SignedIn, failure ?? notice);

            Set("{\"gpt_revoke\":\"ok\",\"gpt_revoke_url\":\"foreign\"}");
            ok = ChatGpt.SignIn(Soon(), out failure);
            browsed.WaitOne(20000);
            probes.Clear();
            notice = ChatGpt.SignOut();
            Check("세션 끝내기 주소가 로그인한 곳과 다르면 토큰을 보내지 않는다", ok && notice == ChatGpt.SignOutUnconfirmed && !ChatGpt.SignedIn && Number("foreign_revoke_calls") == 0,
                failure ?? notice + " / " + Number("foreign_revoke_calls"));
        }

        static string Ask(out bool threw)
        {
            threw = false;
            try { return ChatGpt.Run(Guide, Prompt, Pictures, TimeSpan.FromSeconds(30), NewJob()); }
            catch (UserFacingException ex)
            {
                threw = true;
                return ex.Message;
            }
        }

        // ── 가짜 서버가 본 요청 모양 ──

        static void MockChecks()
        {
            Cli.Print("[가짜 서버가 받은 요청]");
            var r = Web.Send("GET", Base + "/test/checks", null, null, null, TimeSpan.FromSeconds(10), null, "시험 서버");
            var list = Json.A(Json.Parse(r.Body), "checks") ?? new object[0];
            Check("요청 모양(주소·머리글·본문·PKCE·토큰)이 모두 맞다", r.Status == 200 && list.Length == 0,
                string.Join(" | ", list.Select(x => x as string)));
        }

        // ── Claude Code(가짜: tools\mock_claude.py) ──

        // tools\mock_claude.py와 글자까지 같아야 한다(명령 창을 거쳐도 그대로 가는 글자만 쓴다)
        const string ClaudeGuide = "You are a test step: copy the page's text, nothing else.";
        static string claudeReport;
        static List<string> lastFails = new List<string>();
        static int lastPid;

        static void ClaudeTests()
        {
            Cli.Print("[Claude Code · 가짜]");
            string script = FindTool("mock_claude.py");
            if (script == null)
            {
                Check("가짜 Claude Code(tools\\mock_claude.py)를 찾는다", false, null);
                return;
            }
            string dir = Path.Combine(Temp, "claude");
            Directory.CreateDirectory(dir);
            // npm으로 깐 claude.cmd처럼 명령 창을 거쳐 부른다. 경로에 한글이 있어도 되게 스크립트 위치는 환경 변수로 넘긴다.
            string shim = Path.Combine(dir, "claude.cmd");
            File.WriteAllText(shim, "@echo off\r\npython -I \"%MOCK_CLAUDE_SCRIPT%\" %*\r\n", Encoding.ASCII);
            claudeReport = Path.Combine(dir, "report.jsonl");
            Environment.SetEnvironmentVariable("MOCK_CLAUDE_SCRIPT", script);
            Environment.SetEnvironmentVariable("MOCK_CLAUDE_REPORT", claudeReport);
            ClaudeCode.UseTestExe(shim);
            try
            {
                Check("시험용 Claude Code를 찾는다", ClaudeCode.FindExe() == shim, ClaudeCode.FindExe());

                var st = ClaudeStatus("status_sub");
                Check("구독 로그인을 알아본다", st.Found && st.LoggedIn && st.Subscription && st.Problem == null, Describe(st));
                Environment.SetEnvironmentVariable("MOCK_CLAUDE_MODE", "status_out");
                st = ClaudeCode.Status();
                Check("상태는 [다시 확인] 전까지 잠시 기억한다", st.LoggedIn, Describe(st));
                st = ClaudeStatus("status_out");
                Check("로그아웃 상태를 알아본다", st.Found && !st.LoggedIn && st.Problem == null, Describe(st));
                st = ClaudeStatus("status_key");
                Check("API 키 로그인은 '쓴 만큼 요금'으로 본다", st.LoggedIn && !st.Subscription, Describe(st));
                st = ClaudeStatus("status_garbage");
                Check("상태 명령이 없는 옛 버전이면 업데이트를 알려 준다", !st.LoggedIn && Has(st.Problem, "claude update"), Describe(st));
                ReadReport();
                Check("상태 확인 명령줄이 맞다(auth status --json)", lastFails.Count == 0, string.Join(" | ", lastFails));

                bool threw;
                double secs;
                Encoding saved = Console.InputEncoding;
                bool bom = SetConsoleInput(new UTF8Encoding(true));   // 'UTF-8 전역 지원'을 켠 PC처럼
                string answer = ClaudeRun("ok", 30, out threw, out secs);
                SetConsoleInput(saved);
                Check("그림·질문을 보내고 답을 받는다", !threw && answer == "안녕하세요", answer);
                Check("명령줄(도구 끔·기록 안 함·모델)·빈 작업 폴더·입력 한 줄(BOM 없음)이 맞다", bom && lastFails.Count == 0,
                    bom ? string.Join(" | ", lastFails) : "BOM 시험을 준비하지 못함");

                answer = ClaudeRun("init_tools", 60, out threw, out secs);
                Check("시작 알림에 도구가 있으면 바로 끈다", threw && Has(answer, "도구를 모두 끈 상태") && secs < 15 && MockGone(), Took(answer, secs));
                answer = ClaudeRun("tool_stream", 60, out threw, out secs);
                Check("도구를 쓰려는 조짐(스트림)이 보이면 바로 끈다", threw && Has(answer, "허락하지 않은") && secs < 15 && MockGone(), Took(answer, secs));
                answer = ClaudeRun("tool_msg", 60, out threw, out secs);
                Check("도구를 쓰려는 답(assistant)이 오면 바로 끈다", threw && Has(answer, "허락하지 않은") && secs < 15 && MockGone(), Took(answer, secs));
                answer = ClaudeRun("denied", 60, out threw, out secs);
                Check("권한 거절이 적혀 오면 그 답을 쓰지 않는다", threw && Has(answer, "허락하지 않은"), answer);
                answer = ClaudeRun("login", 60, out threw, out secs);
                Check("로그인이 풀렸으면 다시 로그인하라고 한다", threw && Has(answer, "로그인 창 열기"), answer);
                answer = ClaudeRun("limit", 60, out threw, out secs);
                Check("사용 한도에 닿으면 쉬운 말로 알린다", threw && Has(answer, "사용 한도에 닿았어요"), answer);
                answer = ClaudeRun("old", 60, out threw, out secs);
                Check("옛 버전(모르는 옵션)이면 업데이트를 알려 준다", threw && Has(answer, "claude update"), answer);
                answer = ClaudeRun("hang", 4, out threw, out secs);
                Check("제한 시간이 지나면 끄고 알린다", threw && Has(answer, "안에 답을 끝내지 못해") && secs < 15 && MockGone(), Took(answer, secs));
                answer = ClaudeRun("slow_exit", 60, out threw, out secs);
                Check("답을 준 뒤 끝나지 않으면 10초 뒤 끄고 그 답을 쓴다", !threw && answer == "안녕하세요" && secs < 25 && MockGone(), Took(answer, secs));
            }
            finally
            {
                ClaudeCode.UseTestExe(null);
                foreach (string v in new[] { "MOCK_CLAUDE_MODE", "MOCK_CLAUDE_SCRIPT", "MOCK_CLAUDE_REPORT" })
                    Environment.SetEnvironmentVariable(v, null);
            }
        }

        static ClaudeCode.StatusInfo ClaudeStatus(string mode)
        {
            Environment.SetEnvironmentVariable("MOCK_CLAUDE_MODE", mode);
            ClaudeCode.Forget();
            return ClaudeCode.Status();
        }

        static string ClaudeRun(string mode, int limitSeconds, out bool threw, out double seconds)
        {
            Environment.SetEnvironmentVariable("MOCK_CLAUDE_MODE", mode);
            try { File.Delete(claudeReport); }
            catch { }
            var watch = Stopwatch.StartNew();
            threw = false;
            string answer;
            try { answer = ClaudeCode.Run(ClaudeGuide, Prompt, Pictures, TimeSpan.FromSeconds(limitSeconds), NewJob()); }
            catch (UserFacingException ex)
            {
                threw = true;
                answer = ex.Message;
            }
            seconds = watch.Elapsed.TotalSeconds;
            ReadReport();
            return answer;
        }

        /// 가짜 Claude Code가 적어 둔 것: 요청에서 틀린 점, 프로세스 번호
        static void ReadReport()
        {
            lastFails = new List<string>();
            lastPid = 0;
            if (!File.Exists(claudeReport)) return;
            foreach (string line in File.ReadAllLines(claudeReport, Encoding.UTF8))
            {
                var d = Json.Parse(line);
                string f = Json.S(d, "fail");
                if (f != null) lastFails.Add(f);
                if (Json.Get(d, "pid") != null) lastPid = Json.I(d, "pid");
            }
            try { File.Delete(claudeReport); }
            catch { }
        }

        /// 끈 뒤에 가짜 Claude Code(python)가 남지 않았는지
        static bool MockGone()
        {
            if (lastPid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(lastPid)) return p.HasExited;
            }
            catch (ArgumentException) { return true; }
        }

        /// .NET이 기억해 둔 콘솔 입력 인코딩을 바꾼다('UTF-8 전역 지원'을 켠 PC를 흉내 내려고).
        static bool SetConsoleInput(Encoding e)
        {
            var f = typeof(Console).GetField("_inputEncoding", BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) return false;
            f.SetValue(null, e);
            return Console.InputEncoding.GetPreamble().Length == e.GetPreamble().Length;
        }

        internal static string FindTool(string name)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string place in new[] { Path.Combine(exeDir, "..", "tools", name), Path.Combine(exeDir, "tools", name), Path.Combine(Environment.CurrentDirectory, "tools", name) })
            {
                try
                {
                    string full = Path.GetFullPath(place);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        static string Describe(ClaudeCode.StatusInfo st)
        {
            if (st == null) return "null";
            return "Found=" + st.Found + " LoggedIn=" + st.LoggedIn + " Subscription=" + st.Subscription + " Problem=" + (st.Problem ?? "없음");
        }

        static string Took(string answer, double seconds)
        {
            return Short(answer) + " (" + seconds.ToString("0.0") + "초, pid " + lastPid + (MockGone() ? " 끝남" : " 남음") + ")";
        }

        // ── 도우미 ──

        static void Check(string name, bool ok, string detail)
        {
            if (ok) passed++;
            else failed++;
            Cli.Print((ok ? "  통과  " : "  실패  ") + name + (ok || string.IsNullOrEmpty(detail) ? "" : "  → " + detail.Replace("\n", "\\n")));
        }

        static bool Has(string s, string part)
        {
            return s != null && s.Contains(part);
        }

        static bool Page(string status, string part)
        {
            string p = lastPage;
            return p != null && p.StartsWith(status + " ") && p.Contains(part);
        }

        static string Short(string s)
        {
            if (s == null) return "null";
            return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }

        /// 로그인을 20초까지만 기다린다(가짜 서버가 멈춰도 시험이 끝나게)
        static Func<bool> Soon()
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            return delegate { return DateTime.UtcNow > until; };
        }

        static Job NewJob()
        {
            return new Job(MenuAction.All.First(a => a.Id == "text"), null, new List<string>());
        }

        static void Set(string json)
        {
            var r = Web.Send("POST", Base + "/test/set", null, "application/json", Encoding.UTF8.GetBytes(json), TimeSpan.FromSeconds(10), null, "시험 서버");
            if (r.Status != 200) throw new InvalidOperationException("시험 서버 설정 실패 (" + r.Status + ")");
        }

        static string State(string name)
        {
            var r = Web.Send("GET", Base + "/test/state", null, null, null, TimeSpan.FromSeconds(10), null, "시험 서버");
            var v = Json.Get(Json.Parse(r.Body), name);
            return v == null ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        static int Number(string name)
        {
            int n;
            return int.TryParse(State(name), out n) ? n : -1;
        }

        static void CheckProbes()
        {
            string[] got;
            lock (probes)
            {
                got = probes.ToArray();
                probes.Clear();
            }
            Check("돌아오는 문은 다른 경로·틀린 state·다른 Host를 받지 않는다(404)", got.Length > 0 && got.All(p => p.EndsWith(" 404")), string.Join(", ", got));
        }

        /// 브라우저 대신: 돌아오는 문을 먼저 엉뚱하게 두드려 본 뒤, 로그인 주소를 따라간다(가짜 서버가 돌아오는 문으로 보낸다).
        static void Browse(string url)
        {
            browsed.Reset();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Probe(url);
                    lastPage = Get(url);
                }
                catch (Exception ex)
                {
                    lastPage = "오류 " + ex.GetType().Name;
                }
                finally
                {
                    browsed.Set();
                }
            });
        }

        static void Probe(string url)
        {
            var q = Web.ParseQuery(new Uri(url).Query.TrimStart('?'));
            string back, state;
            if (q.TryGetValue("redirect_uri", out back) || q.TryGetValue("callback_url", out back))
            {
                var u = new Uri(back);
                string other = Get(u.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
                lock (probes) probes.Add("다른 경로 " + other.Split(' ')[0]);
                string wrong = Get(back + "?code=x&state=wrong&client_id=y");
                lock (probes) probes.Add("틀린 state " + wrong.Split(' ')[0]);
                if (q.TryGetValue("state", out state))
                {
                    // 맞는 state라도 다른 사이트 이름(Host)으로 오면 받지 않아야 한다
                    string elsewhere = Get(back + "?code=x&state=" + Uri.EscapeDataString(state) + "&client_id=y", "evil.example:" + u.Port);
                    lock (probes) probes.Add("다른 Host " + elsewhere.Split(' ')[0]);
                }
            }
        }

        static string Get(string url, string host = null)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            if (host != null) req.Host = host;
            req.AllowAutoRedirect = true;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return (int)resp.StatusCode + " " + sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) return "오류 " + ex.Status;
                int code = (int)resp.StatusCode;
                resp.Close();
                return code + " ";
            }
        }
    }
}
