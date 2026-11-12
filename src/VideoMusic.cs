// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 '소리' 칸의 무료 배경 음악 찾기(2026-11-12 사용자 요청). 바꿈에는 음악을 넣어 두지 않는다(프로그램이 무거워지고, 곡마다 조건이 바뀔 수 있다).
// 사이트를 기본 인터넷 창으로 열어 미리 듣고 받게 하고, 다운로드 폴더에 새로 받은 소리 파일을 [넣기] 한 번에 배경 음악으로 넣는다.
// 받은 파일에 Windows가 붙여 두는 '인터넷에서 받음' 표시(Zone.Identifier)의 주소로 어느 사이트 곡인지 알아보고 출처 밝히기를 알려 준다.
// 그 주소는 사이트 이름을 맞추는 데만 쓰고 어디에도 남기거나 보내지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32.SafeHandles;

namespace Bakkum
{
    /// 무료 음악 사이트 한 곳
    sealed class VMusicSite
    {
        public string Name, Url, Tag, Terms, Credit;
        public string[] Hosts;   // 받은 파일의 주소가 이 도메인(또는 그 아래)이면 이 사이트 곡
        public bool NoCredit;    // 출처를 밝히지 않아도 되는 곳
    }

    static class VMusic
    {
        // 사이트 조건은 바뀔 수 있어 앱에는 짧게만 적고, 곡 화면의 조건을 보게 한다. 고칠 때는 다시 읽고 날짜를 바꾼다.
        // FreePD는 2025년에 문을 닫아(사이트 첫 화면 공지, 2026-11-13 확인) 넣지 않았다.
        public static readonly VMusicSite[] Sites =
        {
            // 근거: pixabay.com/service/license-summary/ 2026-11-13 읽음 — 무료, 출처 표시 필요 없음, 고쳐 써도 됨. 음악만 따로 팔기·상표로 쓰기는 안 됨.
            new VMusicSite
            {
                Name = "픽사베이 음악", Url = "https://pixabay.com/music/", Hosts = new[] { "pixabay.com" }, NoCredit = true,
                Tag = "출처 안 밝혀도 돼요",
                Terms = "음악만 따로 팔거나 상표로 쓰면 안 돼요.",
                Credit = "픽사베이 곡은 출처를 밝히지 않아도 돼요."
            },
            // 근거: 공유마당 음악 목록·곡 화면 2026-11-13 읽음 — 곡마다 '이용조건'(CC BY 등)이 따로 적혀 있다.
            new VMusicSite
            {
                Name = "공유마당 음악", Url = "https://gongu.copyright.or.kr/gongu/wrt/wrtCl/listWrtSound.do?menuNo=200020", Hosts = new[] { "gongu.copyright.or.kr" },
                Tag = "곡마다 달라요",
                Terms = "한국저작권위원회가 모은 곡이에요(국악도 있어요). 곡 화면의 '이용조건'대로 출처를 적어요.",
                Credit = "공유마당 곡이에요. 곡 화면의 '이용조건'대로 영상 설명에 만든 사람과 곡 이름을 적어 주세요."
            },
            // 근거: support.google.com/youtube/answer/3376882 2026-11-13 읽음 — 유튜브 스튜디오에서만 열림, 크리에이티브 커먼즈 곡은 동영상 설명에 출처,
            // '저작자 표시 필요 없음' 거르기가 있음, 유튜브 밖에서 생기는 문제는 안내하지 않는다고 함.
            new VMusicSite
            {
                Name = "유튜브 오디오 보관함", Url = "https://www.youtube.com/audiolibrary", Hosts = new[] { "youtube.com" },
                Tag = "곡마다 달라요",
                Terms = "유튜브에 올릴 영상에 맞아요. 유튜브 로그인이 필요해요. '저작자 표시 필요 없음'으로 거를 수 있어요.",
                Credit = "유튜브 오디오 보관함 곡이에요. '저작자 표시 필요' 곡이면 영상 설명에 출처를 적어 주세요."
            },
            // 근거: incompetech.com/music/royalty-free/faq.html 2026-11-13 읽음 — 무료, CC BY 4.0, 보는 사람이 찾을 수 있는 곳(유튜브는 설명이나 영상 안)에 출처 필수.
            new VMusicSite
            {
                Name = "인컴피텍", Url = "https://incompetech.com/music/royalty-free/music.html", Hosts = new[] { "incompetech.com" },
                Tag = "출처 꼭 적기",
                Terms = "한 작곡가의 무료 곡 모음이에요. 영상 설명이나 영상 안에 출처를 적어요(CC BY 4.0).",
                Credit = "인컴피텍 곡이에요. 영상 설명에 '곡 이름, Kevin MacLeod (incompetech.com), CC BY 4.0'을 꼭 적어 주세요."
            },
        };

        /// 배경 음악으로 넣을 수 있는 소리 파일(배경 음악 고르기 창과 같은 것)
        public static readonly string[] AudioExts = { ".mp3", ".m4a", ".wav", ".aac", ".wma", ".flac" };

        /// 폴더 바로 아래에서 since 뒤에 받은 소리 파일을 새것부터 max개. 받는 중인 파일(.crdownload 등)·빈 파일·숨은 파일은 뺀다.
        /// 폴더가 없거나 읽지 못하면 빈 목록.
        public static List<FileInfo> Recent(string folder, DateTime since, int max)
        {
            var list = new List<FileInfo>();
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles())
                {
                    try
                    {
                        if (Array.IndexOf(AudioExts, f.Extension.ToLowerInvariant()) < 0) continue;
                        if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (f.Length <= 0 || Stamp(f) < since) continue;
                        list.Add(f);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("받은 음악 찾기 실패: " + ex.GetType().Name);
            }
            return list.OrderByDescending(Stamp).Take(max).ToList();
        }

        /// 받은 때: 만든 때와 고친 때 중 늦은 것(고친 때를 서버 시각으로 적는 브라우저도 있다)
        public static DateTime Stamp(FileInfo f)
        {
            return f.CreationTime > f.LastWriteTime ? f.CreationTime : f.LastWriteTime;
        }

        /// '방금' · 'N분 전' · 'N시간 전'(오늘) · '어제' · 'N일 전'
        public static string Ago(DateTime t, DateTime now)
        {
            var d = now - t;
            if (d.TotalMinutes < 2) return "방금";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "분 전";
            int days = (now.Date - t.Date).Days;
            if (days <= 0) return (int)d.TotalHours + "시간 전";
            return days == 1 ? "어제" : days + "일 전";
        }

        // ── 다운로드 폴더 ──

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        static readonly Guid DownloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        /// 다운로드 폴더: 다른 드라이브로 옮겼어도 Windows가 아는 자리. 못 찾으면 사용자 폴더의 Downloads.
        public static string DownloadsFolder()
        {
            IntPtr p = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out p) == 0 && p != IntPtr.Zero)
                {
                    string s = Marshal.PtrToStringUni(p);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch (Exception ex)
            {
                Log.Write("다운로드 폴더 찾기 실패: " + ex.GetType().Name);
            }
            finally
            {
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        // ── 받은 곳 알아보기 ──

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        /// 파일의 '인터넷에서 받음' 표시를 연다(.NET Framework의 FileStream은 'a.mp3:Zone.Identifier' 같은 이름을 받지 않는다). 없으면 잘못된 손잡이.
        internal static SafeFileHandle OpenZone(string path, bool write)
        {
            const uint Read = 0x80000000, Write = 0x40000000, ShareAll = 7, OpenExisting = 3, CreateAlways = 2;
            return CreateFile(path + ":Zone.Identifier", write ? Write : Read, ShareAll, IntPtr.Zero, write ? CreateAlways : OpenExisting, 0, IntPtr.Zero);
        }

        /// '인터넷에서 받음' 표시의 글(앞 4천 자). 없거나 못 읽으면 null.
        public static string ZoneText(string path)
        {
            try
            {
                var h = OpenZone(path, false);
                if (h.IsInvalid)
                {
                    h.Dispose();
                    return null;
                }
                using (var fs = new FileStream(h, FileAccess.Read))
                using (var r = new StreamReader(fs, Encoding.UTF8, true))
                {
                    var buf = new char[4000];
                    int n = r.ReadBlock(buf, 0, buf.Length);
                    return new string(buf, 0, n);
                }
            }
            catch (Exception ex)
            {
                Log.Write("받은 곳 읽기 실패: " + ex.GetType().Name);
                return null;
            }
        }

        /// 받은 파일이 어느 사이트 곡인지. 모르면 null.
        public static VMusicSite SourceOf(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return SiteFromZone(ZoneText(path));
        }

        /// 표시 글의 ReferrerUrl(받은 화면)·HostUrl(받은 주소)에서 도메인을 보고 사이트를 맞춘다. 이름만 비슷한 도메인은 맞추지 않는다.
        public static VMusicSite SiteFromZone(string zone)
        {
            if (string.IsNullOrEmpty(zone)) return null;
            foreach (var raw in zone.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                if (!key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase) && !key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase)) continue;
                Uri u;
                if (!Uri.TryCreate(line.Substring(eq + 1).Trim(), UriKind.Absolute, out u)) continue;
                if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) continue;
                string host = u.Host.ToLowerInvariant();
                foreach (var s in Sites)
                    foreach (var h in s.Hosts)
                        if (host == h || host.EndsWith("." + h, StringComparison.Ordinal)) return s;
            }
            return null;
        }
    }

    public sealed partial class VideoEditor
    {
        /// '받은 음악' 한 줄
        sealed class MusicFile
        {
            public string Path, Name;
            public DateTime When;
            public VMusicSite Site;
        }

        const int MusicDays = 7, MusicMax = 5;
        bool musicHooked;
        string musicShown;   // 칸에 그린 '받은 음악' 목록(바뀌었을 때만 칸을 다시 그린다)

        List<MusicFile> RecentMusic()
        {
            if (demo)
            {
                // 그림으로 찍을 때는 선생님의 진짜 다운로드 폴더를 읽지 않고 예시를 보인다
                var now = DateTime.Now;
                return new List<MusicFile>
                {
                    new MusicFile { Path = "", Name = "잔잔한 아침.mp3", When = now.AddMinutes(-3), Site = VMusic.Sites[0] },
                    new MusicFile { Path = "", Name = "신나는 행진.mp3", When = now.AddDays(-1), Site = VMusic.Sites[3] },
                    new MusicFile { Path = "", Name = "교실 배경음.wav", When = now.AddDays(-2), Site = null }
                };
            }
            return VMusic.Recent(VMusic.DownloadsFolder(), DateTime.Now.AddDays(-MusicDays), MusicMax)
                .Select(f => new MusicFile { Path = f.FullName, Name = f.Name, When = VMusic.Stamp(f), Site = VMusic.SourceOf(f.FullName) })
                .ToList();
        }

        static string MusicKey(List<MusicFile> list)
        {
            return string.Join("|", list.Select(f => f.Path + "@" + f.When.Ticks));
        }

        /// 소리 칸의 '무료 배경 음악 찾기' + '받은 음악'
        void PanelFreeMusic()
        {
            if (!musicHooked)
            {
                musicHooked = true;
                // 인터넷 창에서 곡을 받고 이 창으로 돌아오면 목록을 새로 본다(바뀌었을 때만 다시 그림)
                Activated += delegate
                {
                    if (tab != "sound" || SelectedSound != null || busy || demo) return;
                    if (MusicKey(RecentMusic()) != musicShown) RefreshPanel();
                };
            }

            Label("무료 배경 음악 찾기");
            Hint("바꿈에는 음악을 넣어 두지 않아요. [열기]로 사이트를 열어 미리 들어 보고, 마음에 드는 곡을 받으면 아래 '받은 음악'에 나와요.");
            foreach (var site in VMusic.Sites) SiteCard(site);

            var list = RecentMusic();
            musicShown = MusicKey(list);
            Label("받은 음악");
            if (list.Count == 0) Hint("최근 " + MusicDays + "일 동안 다운로드 폴더에 받은 소리 파일이 없어요. 곡을 받고 이 창으로 돌아오면 저절로 나와요. 다른 곳에 받았다면 위 [배경 음악]으로 골라요.");
            else
            {
                Hint("다운로드 폴더에 최근 " + MusicDays + "일 동안 받은 소리 파일이에요.");
                foreach (var f in list) MusicRow(f);
            }
            var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(SmallButton("새로 보기", delegate { RefreshPanel(); }));
            row.Children.Add(SmallButton("다운로드 폴더 열기", OpenDownloads));
            panel.Children.Add(row);
        }

        void SiteCard(VMusicSite site)
        {
            var dock = new DockPanel();
            var open = SmallButton("열기", delegate { OpenSite(site); });
            open.Margin = new Thickness(10, 0, 0, 0);
            open.VerticalAlignment = VerticalAlignment.Center;
            open.ToolTip = "인터넷 창에서 열어요";
            DockPanel.SetDock(open, Dock.Right);
            dock.Children.Add(open);

            var words = new StackPanel();
            var head = new WrapPanel();
            var name = Theme.Text(site.Name, 13.5, FontWeights.Bold, Theme.Ink);
            name.TextWrapping = TextWrapping.NoWrap;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(0, 0, 6, 2);
            head.Children.Add(name);
            var tag = Theme.Tag(site.Tag, site.NoCredit ? Theme.Green : Theme.Orange);
            tag.Margin = new Thickness(0, 0, 0, 2);
            head.Children.Add(tag);
            words.Children.Add(head);
            var terms = Theme.Text(site.Terms, 12.5, FontWeights.Normal, Theme.Ink3);
            terms.LineHeight = 18;
            terms.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(terms);
            dock.Children.Add(words);

            panel.Children.Add(new Border
            {
                Child = dock,
                Background = Theme.Sheet2,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 10, 10),
                Margin = new Thickness(0, 4, 0, 4)
            });
        }

        void MusicRow(MusicFile f)
        {
            bool added = !string.IsNullOrEmpty(f.Path) && project.Sounds.Any(x => x.IsMusic && string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase));
            var dock = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var add = SmallButton(added ? "넣었어요" : "넣기", delegate { AddSoundFile(f.Path, true); });
            add.Margin = new Thickness(10, 0, 0, 0);
            add.VerticalAlignment = VerticalAlignment.Center;
            add.IsEnabled = !added;
            DockPanel.SetDock(add, Dock.Right);
            dock.Children.Add(add);

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Theme.Text(f.Name, 13.5, FontWeights.Normal, Theme.Ink);
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = f.Name;
            words.Children.Add(name);
            string sub = VMusic.Ago(f.When, DateTime.Now) + " 받음" + (f.Site != null ? " · " + f.Site.Name + " · " + f.Site.Tag : "");
            var meta = Theme.Text(sub, 12, FontWeights.Normal, f.Site == null ? Theme.Ink3 : f.Site.NoCredit ? Theme.Green.DeepBrush : Theme.Orange.DeepBrush);
            meta.TextWrapping = TextWrapping.NoWrap;
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(meta);
            dock.Children.Add(words);
            panel.Children.Add(dock);
        }

        void OpenSite(VMusicSite site)
        {
            try
            {
                Process.Start(new ProcessStartInfo(site.Url) { UseShellExecute = true });
                SetStatus(site.Name + " 사이트를 인터넷 창에 열었어요. 곡을 받고 이 창으로 돌아오면 '받은 음악'에 나와요.");
            }
            catch (Exception ex)
            {
                Log.Write("음악 사이트 열기 실패: " + ex.GetType().Name);
                SetStatus("인터넷 창을 열지 못했어요. 주소: " + site.Url);
            }
        }

        void OpenDownloads()
        {
            try
            {
                string d = VMusic.DownloadsFolder();
                if (!Directory.Exists(d))
                {
                    SetStatus("다운로드 폴더를 찾지 못했어요.");
                    return;
                }
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + d + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Write("다운로드 폴더 열기 실패: " + ex.GetType().Name);
                SetStatus("다운로드 폴더를 열지 못했어요.");
            }
        }

        /// 고른 배경 음악이 출처를 밝혀야 하는 사이트 곡이면 알려 준다
        void MusicCreditNote(VSound s)
        {
            if (s == null || !s.IsMusic) return;
            var site = VMusic.SourceOf(s.Path);
            if (site != null) NoteBox(site.Credit);
        }
    }
}
