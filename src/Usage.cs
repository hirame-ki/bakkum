// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 이용자 수 파악(ㄱ할게 GOE와 같은 방식): 켠 날 하루 한 번, 임의 번호·버전·날짜만 문수네집 서버에 남긴다(설치 하나에 문서 하나).
// 프로그램을 지워도 서버의 문서는 남는다(마지막 날짜가 멈추므로 최근 7·30일 수에서 저절로 빠진다).
// 파일·이름·PC 정보는 넣지 않는다. 설정 → 그 밖에서 끄면 보내지 않고, 남긴 문서와 임의 번호도 지운다.
// 서버에 닿지 않으면(학교망 등) 조용히 넘어가고 다음에 다시 한다. 설치해서 쓸 때만 보낸다(시험·무설치 실행은 세지 않음).
// 데이터 등급: 영구(backend 일반 문서 저장소 bakkum_usage/<임의 번호> = {version, first, last}).
// 세는 곳: 문수네집 서버 모니터의 '설치형 앱 이용 현황'.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Bakkum
{
    public static class Usage
    {
        internal static string Api = "https://api.moonsunezip.com/v1/documents/commit";
        const string Collection = "bakkum_usage";
        static readonly object Gate = new object();

        public static void StartDaily()
        {
            var t = new Thread(delegate ()
            {
                Thread.Sleep(20000);
                while (true)
                {
                    Ping();
                    Thread.Sleep(TimeSpan.FromHours(3));
                }
            }) { IsBackground = true, Name = "이용 신호" };
            t.Start();
        }

        /// 설정의 스위치. 바꾼 값을 저장하고, 보내거나 지우는 일은 뒤에서 한다(화면을 막지 않게).
        public static void Set(bool on)
        {
            var s = AppSettings.Current;
            s.UsageOff = !on;
            s.UsageSent = "";
            s.SaveQuietly();
            // 설치하지 않은 채 켠 것(시험·무설치)은 세지 않는다. 지우기는 어디서든 한다.
            if (AppInfo.RunningInstalled || !on) new Thread(delegate () { Ping(); }) { IsBackground = true, Name = "이용 신호" }.Start();
        }

        /// 켜 있으면 오늘 한 번 남기고, 꺼 있으면 전에 남긴 문서를 지운다. 실패하면 다음에 다시 한다.
        public static bool Ping()
        {
            lock (Gate)
            {
                var s = AppSettings.Current;
                string id = s.InstallId ?? "";
                if (s.UsageOff)
                {
                    if (id.Length == 0) return false;
                    if (!Commit(Op("delete", id, null))) return false;
                    s.InstallId = "";
                    s.SaveQuietly();
                    return true;
                }
                string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (s.UsageSent == today) return false;
                var data = new Dictionary<string, object> { { "version", AppInfo.Version }, { "last", today } };
                // 아직 한 번도 닿지 않은 번호면 처음 날도 적는다
                if (string.IsNullOrEmpty(s.UsageSent)) data["first"] = today;
                if (id.Length == 0)
                {
                    // 보내기 전에 저장한다. 서버에는 닿았는데 답이 늦게 와도 다음에 같은 번호를 써서 두 번 세지 않고, 끌 때 지울 수 있다.
                    id = NewId();
                    s.InstallId = id;
                    s.SaveQuietly();
                }
                if (!Commit(Op("set", id, data))) return false;
                s.UsageSent = today;
                s.SaveQuietly();
                return true;
            }
        }

        static Dictionary<string, object> Op(string type, string id, Dictionary<string, object> data)
        {
            var op = new Dictionary<string, object> { { "type", type }, { "path", Collection + "/" + id } };
            if (data != null)
            {
                op["data"] = data;
                op["merge"] = true;
            }
            return op;
        }

        static bool Commit(Dictionary<string, object> op)
        {
            var body = new Dictionary<string, object>
            {
                { "requestId", Guid.NewGuid().ToString() },
                { "reads", new object[0] },
                { "operations", new object[] { op } }
            };
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                var r = Web.Send("POST", Api, null, "application/json", payload, TimeSpan.FromSeconds(8), null, "문수네집 서버");
                if (r.Status >= 200 && r.Status < 300) return true;
                Log.Write("이용 신호 못 보냄: " + r.Status);
            }
            catch (Exception ex)
            {
                Log.Write("이용 신호 못 보냄: " + ex.GetType().Name);
            }
            return false;
        }

        /// 16자리 임의 번호(설치를 가리키는 것 말고는 아무 뜻이 없다)
        static string NewId()
        {
            var b = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
