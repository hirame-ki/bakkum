// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 도구 시험 중 무료 배경 음악 찾기 부분(영상 편집 묶음에서 부른다).
// 진짜 다운로드 폴더는 읽지 않는다. 시험 폴더에 가짜 소리 파일과 '인터넷에서 받음' 표시를 만들어 확인한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Bakkum
{
    static partial class ToolTest
    {
        /// 무료 배경 음악 찾기(2026-11-12 사용자 요청): 받은 음악 고르기와 받은 곳 알아보기
        static void FreeMusicTests()
        {
            string d = P("music_downloads");
            if (Directory.Exists(d)) Directory.Delete(d, true);
            Directory.CreateDirectory(d);
            var now = DateTime.Now;
            Func<string, int, DateTime, string> put = delegate (string name, int bytes, DateTime when)
            {
                string f = Path.Combine(d, name);
                File.WriteAllBytes(f, new byte[bytes]);
                File.SetCreationTime(f, when);
                File.SetLastWriteTime(f, when);
                return f;
            };

            string fresh = put("새곡.mp3", 1000, now.AddMinutes(-10));
            string server = put("서버시각.mp3", 1000, now.AddMinutes(-5));
            File.SetLastWriteTime(server, new DateTime(2020, 1, 1));   // 고친 때를 서버 시각으로 적은 경우: 만든 때로 본다
            put("어제곡.M4A", 1000, now.AddHours(-30));
            put("옛곡.wav", 1000, now.AddDays(-30));
            put("문서.pdf", 1000, now);
            put("받는중.mp3.crdownload", 1000, now);
            put("빈파일.mp3", 0, now);
            File.SetAttributes(put("숨김.mp3", 1000, now), FileAttributes.Hidden);
            Directory.CreateDirectory(Path.Combine(d, "폴더.mp3"));
            Directory.CreateDirectory(Path.Combine(d, "안쪽"));
            File.WriteAllBytes(Path.Combine(d, "안쪽", "안쪽.mp3"), new byte[1000]);
            string[] exts = { ".mp3", ".wav", ".aac", ".wma", ".flac", ".m4a" };
            var songs = new List<string>();
            for (int i = 1; i <= 6; i++) songs.Add(put("곡" + i + exts[i - 1], 1000, now.AddHours(-i)));

            var five = VMusic.Recent(d, now.AddDays(-7), 5).Select(f => f.Name).ToList();
            Check("받은 음악: 새것부터 다섯 개(고친 때가 옛날이면 만든 때로)",
                five.SequenceEqual(new[] { "서버시각.mp3", "새곡.mp3", "곡1.mp3", "곡2.wav", "곡3.aac" }), string.Join(", ", five));
            var all = VMusic.Recent(d, now.AddDays(-7), 50).Select(f => f.Name).ToList();
            string[] never = { "옛곡.wav", "문서.pdf", "받는중.mp3.crdownload", "빈파일.mp3", "숨김.mp3", "폴더.mp3", "안쪽.mp3" };
            Check("받은 음악: 7일 지난 것·소리 아닌 것·받는 중·빈 파일·숨은 파일·안쪽 폴더는 빼고, 대문자 확장자는 넣는다",
                all.Count == 9 && all.Contains("어제곡.M4A") && !all.Intersect(never).Any(), all.Count + "개");
            bool quiet = VMusic.Recent(Path.Combine(d, "없는 폴더"), now.AddDays(-7), 5).Count == 0 && VMusic.Recent(null, now.AddDays(-7), 5).Count == 0;
            Check("받은 음악: 폴더가 없으면 빈 목록(오류 없음)", quiet, null);
            string dl = VMusic.DownloadsFolder();
            Check("다운로드 폴더 자리를 찾는다(폴더 안은 읽지 않음)", !string.IsNullOrEmpty(dl) && Path.IsPathRooted(dl), null);

            // 받은 곳: 받은 화면·받은 주소의 도메인으로만 맞추고, 이름만 비슷한 곳은 맞추지 않는다
            Func<string, string> site = delegate (string zone)
            {
                var s = VMusic.SiteFromZone(zone);
                return s == null ? "-" : s.Name;
            };
            var cases = new[]
            {
                new[] { "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://pixabay.com/music/search/\r\nHostUrl=https://cdn.pixabay.com/download/audio/a.mp3\r\n", "픽사베이 음악" },
                new[] { "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://incompetech.com/music/royalty-free/mp3-royaltyfree/a.mp3", "인컴피텍" },
                new[] { "[ZoneTransfer]\nZoneId=3\nReferrerUrl=https://studio.youtube.com/channel/x/music\nHostUrl=https://example.invalid/a.mp3", "유튜브 오디오 보관함" },
                new[] { "[ZoneTransfer]\r\nZoneId=3\r\nreferrerurl = https://gongu.copyright.or.kr/gongu/wrt/wrt/view.do?wrtSn=1\r\n", "공유마당 음악" },
                new[] { "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://evilpixabay.com/a.mp3", "-" },
                new[] { "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://pixabay.com.evil.example/a.mp3", "-" },
                new[] { "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://pixabay.com@evil.example/a.mp3", "-" },
                new[] { "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=ftp://pixabay.com/a.mp3", "-" },
                new[] { "[ZoneTransfer]\r\nZoneId=3\r\nNote=https://pixabay.com/a.mp3", "-" },
                new[] { "[ZoneTransfer]\r\nZoneId=3\r\n", "-" },
                new[] { "", "-" }
            };
            var wrong = cases.Where(c => site(c[0]) != c[1]).Select(c => c[1] + "≠" + site(c[0])).ToList();
            Check("받은 곳: 네 사이트를 알아보고 비슷한 이름·다른 칸·ftp는 모른다고 한다", wrong.Count == 0, wrong.Count == 0 ? cases.Length + "가지" : string.Join(", ", wrong));

            // 진짜 '인터넷에서 받음' 표시(파일 옆 숨은 칸)를 써 보고 읽는다: 보통 글과 UTF-16(BOM) 둘 다
            bool wrote = WriteZone(fresh, Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://pixabay.com/music/\r\n"))
                && WriteZone(songs[0], Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://incompetech.com/a.mp3\r\n")).ToArray());
            var a = VMusic.SourceOf(fresh);
            var b = VMusic.SourceOf(songs[0]);
            var c0 = VMusic.SourceOf(songs[1]);
            Check("받은 곳: 파일의 '인터넷에서 받음' 표시를 읽는다(UTF-16도), 표시가 없으면 모른다",
                wrote && a != null && a.Name == "픽사베이 음악" && b != null && b.Name == "인컴피텍" && c0 == null && VMusic.SourceOf(Path.Combine(d, "없는 파일.mp3")) == null,
                (wrote ? "" : "표시 쓰기 실패 · ") + (a == null ? "-" : a.Name) + " / " + (b == null ? "-" : b.Name) + " / " + (c0 == null ? "-" : c0.Name));

            var t = new DateTime(2026, 11, 13, 9, 0, 0);
            var ago = new[]
            {
                VMusic.Ago(t.AddSeconds(-30), t), VMusic.Ago(t.AddMinutes(-10), t), VMusic.Ago(t.AddHours(-3), t),
                VMusic.Ago(new DateTime(2026, 11, 12, 23, 0, 0), t), VMusic.Ago(new DateTime(2026, 11, 10, 12, 0, 0), t), VMusic.Ago(t.AddMinutes(5), t)
            };
            Check("받은 때: 방금·분·시간·어제·며칠 전(시계가 앞서도 방금)",
                ago.SequenceEqual(new[] { "방금", "10분 전", "3시간 전", "어제", "3일 전", "방금" }), string.Join(" · ", ago));

            var sites = VMusic.Sites;
            bool shape = sites.Length == 4 && sites.All(s => s.Url.StartsWith("https://", StringComparison.Ordinal) && !string.IsNullOrEmpty(s.Name) && !string.IsNullOrEmpty(s.Tag)
                && !string.IsNullOrEmpty(s.Terms) && !string.IsNullOrEmpty(s.Credit) && s.Hosts.Length > 0)
                && sites.Count(s => s.NoCredit) == 1 && !sites.Any(s => s.Url.Contains("freepd"));
            Check("음악 사이트 네 곳: https 주소·조건·출처 안내가 다 있고, 문을 닫은 곳은 없다", shape, string.Join(", ", sites.Select(s => s.Name)));
        }

        static bool WriteZone(string path, byte[] bytes)
        {
            try
            {
                var h = VMusic.OpenZone(path, true);
                if (h.IsInvalid)
                {
                    h.Dispose();
                    return false;
                }
                using (var fs = new FileStream(h, FileAccess.Write)) fs.Write(bytes, 0, bytes.Length);
                return true;
            }
            catch (Exception ex)
            {
                Cli.Print("  표시 쓰기 실패: " + ex.GetType().Name);
                return false;
            }
        }
    }
}
