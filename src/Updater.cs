// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 앱 안 업데이트. 하루 한 번 새 버전이 있는지만 보고(문서 내용은 보내지 않는다), 선생님이 [바꾸기]를 눌렀을 때만 받는다.
// 프로그램이 직접 받은 파일에는 '인터넷에서 받은 파일' 표시가 붙지 않아 윈도우 PC 보호 창이 뜨지 않는다.
// 주소는 이 프로그램의 공개 저장소·사이트만 받고, 받은 파일은 SHA-256이 맞을 때만 실행한다.
// version.json: {"version":"0.2.1","url":"https://github.com/hirame-ki/bakkum/releases/download/v0.2.1/Bakkum.exe","sha256":"…64자…","notes":"고친 점"}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;

namespace Bakkum
{
    public sealed class UpdateInfo
    {
        public string Version, Url, Sha256, Notes;
    }

    public static class Updater
    {
        const string RealManifest = "https://hirame-ki.github.io/bakkum/version.json";
        const long MostBytes = 40L * 1024 * 1024;
        static readonly string[] Allowed = { "https://github.com/hirame-ki/bakkum/releases/download/", "https://hirame-ki.github.io/bakkum/" };

        static string manifest = RealManifest;
        static string testPrefix;

        /// 알게 된 새 버전(없으면 null)
        public static UpdateInfo Available { get; private set; }

        /// 시험(/selftest-tools update) 동안만: 이 PC 안의 가짜 서버를 쓴다.
        internal static void UseTestServer(string baseUrl)
        {
            Web.RequireLocal(baseUrl);
            manifest = baseUrl + "/version.json";
            testPrefix = baseUrl + "/";
        }

        static int[] Parts(string v)
        {
            if (string.IsNullOrEmpty(v) || !Regex.IsMatch(v, @"^\d{1,6}(\.\d{1,6}){0,3}$")) return null;
            return v.Split('.').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }

        public static bool IsNewer(string candidate, string current)
        {
            int[] a = Parts(candidate), b = Parts(current);
            if (a == null || b == null) return false;
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        static bool UrlAllowed(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            // Uri가 ..를 풀어 준 뒤의 주소로 본다
            string full = u.AbsoluteUri;
            if (testPrefix != null && full.StartsWith(testPrefix, StringComparison.Ordinal)) return true;
            return Allowed.Any(p => full.StartsWith(p, StringComparison.Ordinal));
        }

        /// 버전 파일을 읽는다. 모양이 틀리거나 허용하지 않은 주소면 null.
        public static UpdateInfo Parse(string json)
        {
            Dictionary<string, object> d;
            try { d = Json.Parse(json); }
            catch { return null; }
            if (d == null) return null;
            var info = new UpdateInfo
            {
                Version = Json.S(d, "version"),
                Url = Json.S(d, "url"),
                Sha256 = (Json.S(d, "sha256") ?? "").ToLowerInvariant(),
                Notes = Json.S(d, "notes") ?? ""
            };
            if (Parts(info.Version) == null || !UrlAllowed(info.Url) || !Regex.IsMatch(info.Sha256, "^[0-9a-f]{64}$")) return null;
            return info;
        }

        /// 새 버전이 있으면 그 정보를, 지금이 최신이면 null을 돌려준다. 확인하지 못하면 UserFacingException.
        public static UpdateInfo Fetch()
        {
            var r = Web.Send("GET", manifest, new[] { "Cache-Control", "no-cache" }, null, null, TimeSpan.FromSeconds(15), null, "새 버전 정보");
            if (r.Status != 200) throw new UserFacingException("새 버전 정보를 찾지 못했어요. 잠시 뒤에 다시 해 보세요.");
            var info = Parse(r.Body);
            if (info == null) throw new UserFacingException("새 버전 정보를 읽지 못했어요.");
            bool newer = IsNewer(info.Version, AppInfo.Version);
            Available = newer ? info : null;
            return Available;
        }

        /// 프로그램이 켜져 있는 동안 하루 한 번 확인한다. 켜자마자 인터넷을 쓰지 않게 조금 기다린다.
        public static void StartChecks(Dispatcher ui)
        {
            var t = new Thread(delegate ()
            {
                Thread.Sleep(30000);
                while (true)
                {
                    var s = AppSettings.Current;
                    string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (s.CheckUpdates && s.LastUpdateCheck != today)
                    {
                        try
                        {
                            var info = Fetch();
                            s.LastUpdateCheck = today;
                            s.SaveQuietly();
                            if (info != null) ui.BeginInvoke(new Action(delegate { Offer(info); }));
                        }
                        catch (Exception ex)
                        {
                            Log.Write("새 버전 확인 못 함: " + ex.GetType().Name);
                        }
                    }
                    Thread.Sleep(TimeSpan.FromHours(3));
                }
            });
            t.IsBackground = true;
            t.Name = "update-check";
            t.Start();
        }

        /// 사용자가 메뉴에서 눌렀을 때: 바로 확인하고 결과를 알려 준다.
        public static void CheckNow(Dispatcher ui)
        {
            var t = new Thread(delegate ()
            {
                try
                {
                    var info = Fetch();
                    ui.BeginInvoke(new Action(delegate
                    {
                        if (info != null) Offer(info);
                        else App.Notify("지금이 최신 버전이에요", AppInfo.Name + " " + AppInfo.Version, false);
                    }));
                }
                catch (Exception ex)
                {
                    Log.Write("새 버전 확인 못 함: " + ex.GetType().Name);
                    ui.BeginInvoke(new Action(delegate { App.Notify("새 버전을 확인하지 못했어요", "인터넷 연결을 확인하고 다시 해 보세요.", true); }));
                }
            });
            t.IsBackground = true;
            t.Name = "update-check-now";
            t.Start();
        }

        static void Offer(UpdateInfo info)
        {
            string what = (string.IsNullOrWhiteSpace(info.Notes) ? "새 기능과 고친 점이 들어 있어요." : info.Notes) + " 바꾸는 중에 보안 프로그램이 물어보면 허용해 주세요.";
            Toast.ShowOffer("새 버전이 있어요 · " + info.Version, what, "지금 바꾸기", delegate { Apply(info); });
        }

        public static string Sha256Of(string file)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(file))
            {
                return string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        /// 받아서 맞는지 확인한 뒤 실행 파일 경로를 돌려준다. 틀리면 지우고 UserFacingException.
        public static string Download(UpdateInfo info, Action<double> progress)
        {
            string dir = Util.TempDir("update");
            string exe = Path.Combine(dir, AppInfo.Id + ".exe");
            try
            {
                Web.Download(info.Url, exe, MostBytes, TimeSpan.FromMinutes(5), progress, "새 버전");
                if (Sha256Of(exe) != info.Sha256) throw new UserFacingException("받은 파일이 올바르지 않아서 쓰지 않았어요. 잠시 뒤에 다시 해 보세요.");
                return exe;
            }
            catch
            {
                Util.TryDeleteDir(dir);
                throw;
            }
        }

        /// [지금 바꾸기]: 받고, 확인하고, 새 프로그램에게 맡긴다. 맡은 쪽이 이 프로그램을 끝내고 파일을 바꾼 뒤 다시 켠다.
        public static void Apply(UpdateInfo info)
        {
            if (Jobs.Busy || RecordUi.Active)
            {
                App.Notify("조금 뒤에 해 주세요", "하던 변환이나 녹화가 끝난 뒤에 새 버전으로 바꿀 수 있어요.", true);
                return;
            }
            var toast = Toast.Progress("새 버전 받는 중", Theme.Blue, "down");
            var ui = toast.Dispatcher;
            var t = new Thread(delegate ()
            {
                string exe = null, problem = null;
                try
                {
                    exe = Download(info, delegate (double f)
                    {
                        ui.BeginInvoke(new Action(delegate { toast.SetProgress(Math.Round(f * 100) + "%", f); }));
                    });
                }
                catch (UserFacingException ex)
                {
                    problem = ex.Message;
                }
                catch (Exception ex)
                {
                    Log.Write("새 버전 받기 실패: " + ex.GetType().Name);
                    problem = "새 버전을 받지 못했어요. 잠시 뒤에 다시 해 보세요.";
                }
                ui.BeginInvoke(new Action(delegate
                {
                    toast.Dismiss();
                    if (problem != null) App.Notify("새 버전을 받지 못했어요", problem, true);
                    else Launch(exe);
                }));
            });
            t.IsBackground = true;
            t.Name = "update-get";
            t.Start();
        }

        static void Launch(string exe)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo(exe, "/update") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) })) { }
            }
            catch (Exception ex)
            {
                Log.Write("새 버전 실행 실패: " + ex.GetType().Name);
                App.Notify("새 버전으로 바꾸지 못했어요", "보안 프로그램이 막았을 수 있어요. 소개 페이지에서 새 설치 파일을 받아 주세요.", true);
            }
        }

        /// 받은 새 프로그램 쪽(/update): 켜져 있는 바꿈을 끝내고 실행 파일을 바꾼 뒤 설치된 프로그램을 다시 켠다.
        public static void FinishHere()
        {
            string done = "/updated";
            try
            {
                Installer.ReplaceProgram();
                Log.Write("새 버전으로 바꿈 " + AppInfo.Version);
            }
            catch (Exception ex)
            {
                Log.Write("새 버전으로 바꾸기 실패: " + ex.GetType().Name);
                done = "";
                Dialog.Ask(null, Theme.Coral, "close", "새 버전으로 바꾸지 못했어요", "지금 쓰던 버전은 그대로예요. 소개 페이지에서 새 설치 파일을 받아 주세요.", null, "확인");
            }
            bool came;
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(AppInfo.InstalledExe, done) { UseShellExecute = false, WorkingDirectory = AppInfo.InstallDir }))
                {
                    came = CameBack(App.IsRunning, () => p.HasExited, 500, WaitSteps);
                }
            }
            catch (Exception ex)
            {
                Log.Write("다시 켜기 실패: " + ex.GetType().Name);
                came = false;
            }
            if (came) return;
            // 2026-11-08 실측: 파일은 바뀌었는데 V3의 '프로그램 실행 알림'에서 막히면 새 버전이 아무 말 없이 켜지지 않았다.
            Log.Write("새 버전이 켜지지 않음");
            Dialog.Ask(null, Theme.Orange, "info", "바꿈이 다시 켜지지 않았어요", "V3 같은 보안 프로그램이 확인 창에서 막았을 수 있어요. 바탕 화면이나 시작 메뉴에서 바꿈을 열어 주세요. 확인 창이 뜨면 실행을 허용해 주세요.", null, "확인");
        }

        /// 보안 프로그램의 확인 창에 사람이 답할 때까지 기다린다(5분). 일찍 끝나면 더 기다리지 않는다.
        const int WaitSteps = 600;

        /// 다시 켠 프로그램이 정말 켜졌는지 본다. running: 켜져 있는가, exited: 켠 프로세스가 벌써 끝났는가.
        internal static bool CameBack(Func<bool> running, Func<bool> exited, int stepMs, int maxSteps)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                if (running()) return true;
                if (exited()) break;
                Thread.Sleep(stepMs);
            }
            return running();
        }

        /// 앞서 받아 둔 임시 파일을 지운다.
        public static void CleanUp()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), AppInfo.Id);
                if (!Directory.Exists(dir)) return;
                foreach (var d in Directory.GetDirectories(dir, "update-*")) Util.TryDeleteDir(d);
            }
            catch { }
        }
    }
}
