// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 'look' 묶음: 화면 스타일·색감(Look.cs)과 이용자 수 알리기(Usage.cs) 시험.
// 이용 신호는 이 PC 안의 가짜 서버(127.0.0.1)로만 보낸다. 인터넷으로는 아무것도 나가지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

namespace Bakkum
{
    static partial class ToolTest
    {
        static readonly uint[][] BaseHues =
        {
            new uint[] { 0xFF8473, 0xFFE8E3, 0xB53A28 }, new uint[] { 0xFFAA4D, 0xFFEEDB, 0xA85A0B }, new uint[] { 0xFFD447, 0xFFF6D6, 0x8A6A00 },
            new uint[] { 0x52D3A0, 0xDDF7EC, 0x13795A }, new uint[] { 0x6CA8FF, 0xE3EEFF, 0x2259B8 }, new uint[] { 0xB596FF, 0xEFE8FF, 0x6237C2 },
            new uint[] { 0xFF8FC2, 0xFFE6F1, 0xB02A6B }, new uint[] { 0xAEB6C5, 0xEEF0F4, 0x465063 }, new uint[] { 0x4FC3E8, 0xDFF5FC, 0x0E6F8C },
            new uint[] { 0xA5D64C, 0xF0F9DF, 0x4E7A0C }, new uint[] { 0x38C7B8, 0xDFF7F4, 0x0D6F66 }, new uint[] { 0x8E9BFF, 0xE9EBFF, 0x3B4BC0 }
        };

        static void LookTests()
        {
            Cli.Print("[스타일·색감]");
            Check("스타일 여섯, 색감 열", Look.Styles.Length == 6 && Look.Moods.Length == 10 && Look.Styles[0].Id == "paper", Look.Styles.Length + "·" + Look.Moods.Length);
            var plain = Look.Make(Look.Styles[0], null);
            var h0 = Look.HueFor(plain, "coral", 0xFF8473, 0xFFE8E3, 0xB53A28);
            Check("기본 스타일·기본 색감은 색종이 그대로", plain.Plain && plain.Desk == Theme.Hex(0xF6F3EE) && plain.Ink3 == Theme.Hex(0x696F7D) && h0.Vivid == Theme.Hex(0xFF8473) && h0.Deep == Theme.Hex(0xB53A28)
                && Look.Restyle(Theme.Xaml, plain) == Theme.Xaml, null);
            Check("모르는 이름은 기본으로", Look.StyleOf("없는것") == Look.Styles[0] && Look.MoodOf("") == null && Look.MoodOf(null) == null, null);

            var bad = new List<string>();
            int parsed = 0;
            foreach (var st in Look.Styles)
            {
                foreach (var mood in new UiMood[] { null }.Concat(Look.Moods))
                {
                    var p = Look.Make(st, mood);
                    string where = st.Name + "/" + (mood == null ? "기본" : mood.Name);
                    // 기본(색종이)은 DESIGN.md 값을 그대로 쓴다(주황 깊은 색이 옅은 칠 위 4.4:1). 새로 만든 것만 잰다.
                    if (p.Plain) continue;
                    if (Look.Contrast(p.Ink, p.Sheet) < 7 || Look.Contrast(p.Ink, p.Desk) < 7) bad.Add(where + " 잉크");
                    if (Look.Contrast(p.Ink2, p.Sheet) < 7) bad.Add(where + " 본문");
                    if (Look.Contrast(p.Ink3, p.Sheet) < 4.5 || Look.Contrast(p.Ink3, p.Desk) < 4.5) bad.Add(where + " 도움말");
                    foreach (var b in BaseHues)
                    {
                        var h = Look.HueFor(p, "x", b[0], b[1], b[2]);
                        if (Look.Contrast(p.Ink, h.Vivid) < 6) bad.Add(where + " 진한 칠 #" + b[0].ToString("X6"));
                        if (Look.Contrast(h.Deep, h.Tint) < 4.5 || Look.Contrast(h.Deep, p.Sheet) < 4.5) bad.Add(where + " 깊은 색 #" + b[2].ToString("X6"));
                    }
                    try
                    {
                        var dict = (ResourceDictionary)XamlReader.Parse(Look.Restyle(Theme.Xaml, p));
                        if (dict["Primary"] is Style && dict["TextField"] is Style) parsed++;
                    }
                    catch (Exception ex)
                    {
                        bad.Add(where + " 꾸밈 읽기 실패 " + ex.GetType().Name);
                    }
                }
            }
            Check("모든 스타일·색감에서 글자 대비가 기준 이상(잉크 7, 도움말 4.5, 진한 칠 위 6, 깊은 색 4.5)", bad.Count(x => !x.Contains("꾸밈")) == 0, string.Join(", ", bad.Take(6)));
            Check("모든 스타일·색감의 앱 꾸밈(XAML)이 읽힌다", parsed == Look.Styles.Length * (Look.Moods.Length + 1) - 1, parsed + "개");
            var cartoon = Look.Restyle(Theme.Xaml, Look.Make(Look.StyleOf("cartoon"), null));
            Check("카툰풍: 굵은 테두리·딱딱한 그림자·큰 모서리", cartoon.Contains("BorderThickness='2'") && !cartoon.Contains("BorderThickness='1.5'") && cartoon.Contains("BlurRadius='0'") && cartoon.Contains("Direction='315'") && cartoon.Contains("CornerRadius='15.6'"), null);
            var clay = Look.Restyle(Theme.Xaml, Look.Make(Look.StyleOf("clay"), Look.MoodOf("rose")));
            Check("클레이모피즘: 부드러운 큰 그림자, 색감이 섞인다", clay.Contains("BlurRadius='21'") && clay.Contains("Direction='270'") && !clay.Contains("#efeae2"), null);

            Cli.Print("[이용자 수 알리기]");
            UsageTests();
        }

        /// 가짜 서버: 받은 요청 본문을 모으고 200을 돌려준다(HttpListener는 관리자 권한이 필요해서 TCP로 직접 받는다).
        sealed class FakeServer : IDisposable
        {
            readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            public readonly List<string> Bodies = new List<string>();
            volatile bool stop;

            public FakeServer()
            {
                listener.Start();
                new Thread(Serve) { IsBackground = true }.Start();
            }

            public string Url
            {
                get { return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/v1/documents/commit"; }
            }

            void Serve()
            {
                while (!stop)
                {
                    try
                    {
                        using (var c = listener.AcceptTcpClient())
                        using (var s = c.GetStream())
                        {
                            var head = new StringBuilder();
                            while (!head.ToString().EndsWith("\r\n\r\n"))
                            {
                                int b = s.ReadByte();
                                if (b < 0) break;
                                head.Append((char)b);
                            }
                            int len = 0;
                            foreach (var line in head.ToString().Split('\n'))
                            {
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(15).Trim(), out len);
                            }
                            var body = new byte[len];
                            int got = 0;
                            while (got < len)
                            {
                                int n = s.Read(body, got, len - got);
                                if (n <= 0) break;
                                got += n;
                            }
                            lock (Bodies) Bodies.Add(Encoding.UTF8.GetString(body, 0, got));
                            byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 11\r\nConnection: close\r\n\r\n{\"ok\":true}");
                            s.Write(reply, 0, reply.Length);
                        }
                    }
                    catch (Exception)
                    {
                        if (stop) return;
                    }
                }
            }

            public void Dispose()
            {
                stop = true;
                try { listener.Stop(); } catch { }
            }
        }

        static Dictionary<string, object> OpOf(string body)
        {
            var d = Json.Parse(body);
            var ops = Json.A(d, "operations");
            return ops == null || ops.Length != 1 ? null : Json.Obj(ops[0]);
        }

        static void UsageTests()
        {
            var s = AppSettings.Current;
            string api = Usage.Api;
            string today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using (var server = new FakeServer())
                {
                    Usage.Api = server.Url;
                    s.UsageOff = false;
                    s.InstallId = "";
                    s.UsageSent = "";
                    bool sent = Usage.Ping();
                    var op = server.Bodies.Count == 1 ? OpOf(server.Bodies[0]) : null;
                    var data = op == null ? null : Json.Obj(op["data"]);
                    string path = op == null ? "" : Json.S(op, "path");
                    Check("처음 켠 날: 임의 번호·버전·처음·마지막 날만 보낸다", sent && op != null && Json.S(op, "type") == "set" && System.Text.RegularExpressions.Regex.IsMatch(path, "^bakkum_usage/[0-9a-f]{16}$")
                        && data != null && data.Count == 3 && Json.S(data, "version") == AppInfo.Version && Json.S(data, "first") == today && Json.S(data, "last") == today
                        && s.InstallId == path.Substring(13) && s.UsageSent == today, server.Bodies.Count + "번 " + path);
                    Check("보낸 것에 PC 이름·사용자 이름이 없다", !server.Bodies[0].Contains(Environment.MachineName) && !server.Bodies[0].Contains(Environment.UserName), null);

                    Check("같은 날 다시 켜면 보내지 않는다", !Usage.Ping() && server.Bodies.Count == 1, null);

                    s.UsageSent = "2000-01-01";
                    Usage.Ping();
                    op = server.Bodies.Count == 2 ? OpOf(server.Bodies[1]) : null;
                    data = op == null ? null : Json.Obj(op["data"]);
                    Check("다음 날에는 같은 번호로 마지막 날만 고친다", data != null && Json.S(op, "path") == path && !data.ContainsKey("first") && Json.S(data, "last") == today, null);

                    s.UsageOff = true;
                    bool removed = Usage.Ping();
                    op = server.Bodies.Count == 3 ? OpOf(server.Bodies[2]) : null;
                    Check("끄면 남긴 문서를 지우고 번호도 버린다", removed && op != null && Json.S(op, "type") == "delete" && Json.S(op, "path") == path && s.InstallId == "", null);
                    Check("끈 뒤로는 보내지 않는다", !Usage.Ping() && server.Bodies.Count == 3, null);
                }

                // 서버에 닿지 않을 때: 조용히 넘어가고 다음에 다시 한다
                var gone = new TcpListener(IPAddress.Loopback, 0);
                gone.Start();
                int port = ((IPEndPoint)gone.LocalEndpoint).Port;
                gone.Stop();
                Usage.Api = "http://127.0.0.1:" + port + "/v1/documents/commit";
                s.UsageOff = false;
                s.UsageSent = "";
                bool ok = Usage.Ping();
                Check("서버에 닿지 않으면 조용히 넘어가고 다음에 다시 한다", !ok && s.UsageSent == "" && s.InstallId == "", null);
            }
            finally
            {
                Usage.Api = api;
                s.UsageOff = false;
                s.InstallId = "";
                s.UsageSent = "";
            }
        }
    }
}
