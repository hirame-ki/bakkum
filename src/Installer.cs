// 설치·지우기. 관리자 권한 없이 이 PC의 내 계정에만 설치한다.
//  - 프로그램: %LOCALAPPDATA%\Programs\Bakkum\Bakkum.exe
//  - 시작 메뉴·바탕 화면·보내기(SendTo) 바로 가기, 윈도우 시작 시 켜기(시작 프로그램 폴더의 바로 가기)
//  - 앱 목록 등록(HKCU Uninstall)은 설치된 프로그램이 처음 켜질 때 한다
// 지우기는 위에서 만든 것과 설정·기록만 지운다. 변환 결과 파일은 건드리지 않는다.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Bakkum
{
    public static class Installer
    {
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppInfo.Id;

        static string StartupLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppInfo.Name + ".lnk"); }
        }

        static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name + ".lnk"); }
        }

        static string SendToLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), AppInfo.Name + ".lnk"); }
        }

        static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppInfo.Name + ".lnk"); }
        }

        public static bool Installed
        {
            get { return File.Exists(AppInfo.InstalledExe); }
        }

        public static void Install(bool runAtStartup)
        {
            StopRunning();
            Directory.CreateDirectory(AppInfo.InstallDir);
            string target = AppInfo.InstalledExe;
            if (!string.Equals(Path.GetFullPath(AppInfo.ExePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                CopySelfTo(target);
            }
            File.WriteAllText(Path.Combine(AppInfo.InstallDir, "NOTICE.txt"), SettingsWindow.NoticeText(), new UTF8Encoding(true));

            CreateShortcut(StartMenuLink, target, AppInfo.Name + " 설정");
            CreateShortcut(SendToLink, target, AppInfo.Name + "으로 보내기");
            CreateShortcut(DesktopLink, target, AppInfo.Name + " 설정");

            var s = AppSettings.Current;
            s.StartWithWindows = runAtStartup;
            s.Save();
            SetRunAtStartup(runAtStartup);
            Log.Write("설치 " + AppInfo.Version);
        }

        /// 새 파일로 써서 '인터넷에서 받은 파일' 표시(Zone.Identifier)가 따라오지 않게 한다.
        static void CopySelfTo(string target)
        {
            string fresh = target + ".new";
            File.WriteAllBytes(fresh, File.ReadAllBytes(AppInfo.ExePath));
            PutInPlace(fresh, target, 20);
        }

        /// fresh를 target 자리에 놓는다. 쓰던 파일은 다 놓을 때까지 .old로 비켜 두고, 못 놓으면 되돌린다(프로그램이 사라지면 안 된다).
        /// 바로 앞에 끝낸 프로그램이나 백신이 파일을 놓기까지 잠깐 걸릴 수 있어서 몇 번 다시 해 본다.
        internal static void PutInPlace(string fresh, string target, int retries)
        {
            string old = target + ".old";
            try
            {
                for (int i = 0; ; i++)
                {
                    try
                    {
                        // target이 없으면 앞선 시도에서 이미 비켜 둔 것이므로 .old를 건드리지 않는다
                        if (File.Exists(target))
                        {
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(target, old);
                        }
                        File.Move(fresh, target);
                        break;
                    }
                    catch (IOException)
                    {
                        if (i >= retries) throw;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (i >= retries) throw;
                    }
                    Thread.Sleep(300);
                }
            }
            catch
            {
                try
                {
                    if (!File.Exists(target) && File.Exists(old)) File.Move(old, target);
                }
                catch (Exception ex)
                {
                    Log.Write("쓰던 파일 되돌리기 실패: " + ex.GetType().Name);
                }
                TryDeleteFile(fresh);
                throw;
            }
            TryDeleteFile(old);
        }

        /// 앱 안 업데이트(/update): 켜져 있는 프로그램을 끝내고 실행 파일만 바꾼다. 설정·바로 가기는 그대로 둔다.
        public static void ReplaceProgram()
        {
            StopRunning();
            Directory.CreateDirectory(AppInfo.InstallDir);
            CopySelfTo(AppInfo.InstalledExe);
            File.WriteAllText(Path.Combine(AppInfo.InstallDir, "NOTICE.txt"), SettingsWindow.NoticeText(), new UTF8Encoding(true));
        }

        static string ListedVersion()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                return k == null ? null : k.GetValue("DisplayVersion") as string;
            }
        }

        static void WriteAppEntry()
        {
            string target = AppInfo.InstalledExe;
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppInfo.Name);
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", AppInfo.Publisher);
                k.SetValue("DisplayIcon", target + ",0");
                k.SetValue("InstallLocation", AppInfo.InstallDir);
                k.SetValue("UninstallString", "\"" + target + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + target + "\" /uninstall /quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(target).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        /// 윈도우 설정 → 앱 목록에 올린다. 설치된 프로그램이 켜질 때 스스로 한다.
        /// 2026-11-08 실측: 설치 파일(내려받은 자리에서 도는 프로세스)이 쓴 Uninstall 키는 창도 기록도 없이 남지 않았고, 같은 키를 PowerShell로 쓰면 남았다.
        /// 그때 V3는 설치 파일의 등록 단계에서 'Persistence/MDP.AutoRun.M224'로 멈추기도 했다. 그래서 설치 단계에서는 쓰지 않는다.
        /// 남았는지 잠시 뒤 다시 보아 기록하고, 안 남으면 그 버전에서는 다시 시도하지 않는다.
        public static void EnsureAppListedLater()
        {
            var t = new Thread(delegate ()
            {
                try
                {
                    Thread.Sleep(4000);
                    if (ListedVersion() == AppInfo.Version) return;
                    var s = AppSettings.Current;
                    if (s.AppListTried == AppInfo.Version) return;
                    s.AppListTried = AppInfo.Version;
                    s.SaveQuietly();
                    WriteAppEntry();
                    Thread.Sleep(8000);
                    Log.Write(ListedVersion() == AppInfo.Version ? "앱 목록 등록: 남아 있음" : "앱 목록 등록: 남지 않음");
                }
                catch (Exception ex)
                {
                    Log.Write("앱 목록 등록 실패: " + ex.GetType().Name);
                }
            });
            t.IsBackground = true;
            t.Name = "app-list";
            t.Start();
        }

        /// 윈도우를 켤 때 같이 켜기. 레지스트리 Run 값 대신 시작 프로그램 폴더에 바로 가기를 둔다.
        /// 2026-11-08: '자기를 %LOCALAPPDATA%에 복사하고 HKCU Run에 시작 인자와 함께 등록'하는 순서가 V3의 행위 진단
        /// 'Persistence/MDP.AutoRun.M224'에 걸렸다. 안랩 ASEC 글에서는 같은 이름이 이 순서를 쓰는 랜섬웨어에 붙어 있다.
        /// 이 방식에 V3가 어떻게 반응하는지는 아직 재지 않았다.
        /// 설정을 저장할 때마다 불리므로, 이미 있으면 다시 쓰지 않는다.
        public static void SetRunAtStartup(bool on)
        {
            try
            {
                if (!on) TryDeleteFile(StartupLink);
                else if (!File.Exists(StartupLink)) CreateShortcut(StartupLink, AppInfo.InstalledExe, AppInfo.Name, "/background");
            }
            catch (Exception ex)
            {
                Log.Write("시작 프로그램 설정 실패: " + ex.GetType().Name);
            }
        }

        static void CreateShortcut(string lnk, string target, string description)
        {
            CreateShortcut(lnk, target, description, null);
        }

        /// 바로 가기(.lnk)를 만든다. 윈도우 스크립트 호스트(WScript.Shell)를 거치지 않고 셸의 IShellLink로 바로 만든다.
        /// 스크립트 호스트를 막아 둔 PC에서도 되게 하려는 것이다.
        static void CreateShortcut(string lnk, string target, string description, string args)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            var link = (IShellLinkW)new ShellLinkObject();
            try
            {
                link.SetPath(target);
                if (!string.IsNullOrEmpty(args)) link.SetArguments(args);
                link.SetWorkingDirectory(Path.GetDirectoryName(target));
                link.SetIconLocation(target, 0);
                link.SetDescription(description);
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnk, true);
            }
            catch (COMException)
            {
                throw new UserFacingException("바로 가기를 만들 수 없어요.");
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkObject { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        /// 켜져 있는 이 프로그램을 끝내고, 끝날 때까지(최대 10초) 기다린다.
        static void StopRunning()
        {
            if (!App.IsRunning()) return;
            App.SendToRunning("quit");
            for (int i = 0; i < 50 && App.IsRunning(); i++) Thread.Sleep(200);
            if (!App.IsRunning()) return;
            // 끝나지 않으면 설치된 파일에서 켜진 것만 끈다.
            foreach (var p in Process.GetProcessesByName(AppInfo.Id))
            {
                try
                {
                    if (p.Id == Process.GetCurrentProcess().Id) continue;
                    string path = p.MainModule.FileName;
                    if (string.Equals(path, AppInfo.InstalledExe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        public static void UninstallInteractive(bool quiet)
        {
            if (!quiet)
            {
                int r = Dialog.Ask(null, Theme.Coral, "box", AppInfo.Name + "을 지울까요?",
                    "이 PC에서 프로그램과 설정·기록을 지워요. 만들어 둔 변환 결과 파일은 그대로 둬요.", null, "취소", "지우기");
                if (r != 1) return;
            }
            bool folderLater;
            try
            {
                folderLater = Uninstall();
            }
            catch (Exception ex)
            {
                if (!quiet) Dialog.Ask(null, Theme.Coral, "close", "다 지우지 못했어요", "(" + ex.GetType().Name + ") 잠시 뒤에 다시 해 보거나, 윈도우 설정 → 앱에서 지워 주세요.", null, "확인");
                return;
            }
            if (!quiet) Dialog.Ask(null, Theme.Green, "check", "지웠어요", "그동안 " + AppInfo.Name + "을 써 주셔서 고마워요.", null, "확인");
            // 마지막 창을 닫은 뒤에야 이 프로그램이 끝나므로, 폴더 지우기는 여기서 시작한다.
            if (folderLater) DeleteFolderAfterExit(AppInfo.InstallDir);
        }

        /// 이 프로그램이 끝난 뒤 폴더를 지운다. 실행 파일이 아직 켜져 있으면 지워지지 않으니 1초마다 다시 해 보고, 2분 뒤 그만둔다.
        /// (2026-11-07 시험: 2초 뒤 한 번만 지우게 했더니 '지웠어요' 창이 떠 있는 동안 실행 파일이 잠겨 폴더가 남았다.)
        static void DeleteFolderAfterExit(string dir)
        {
            string q = "\"" + dir + "\"";
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/d /c for /l %i in (1,1,120) do @(rd /s /q " + q + " 2>nul & if not exist " + q + " (exit 0) else (ping -n 2 127.0.0.1 >nul))")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            };
            try { Process.Start(psi); }
            catch { }
        }

        /// 지울 것을 지운다. 프로그램 폴더를 이 프로그램이 끝난 뒤에 지워야 하면 true.
        static bool Uninstall()
        {
            StopRunning();
            // ChatGPT에 로그인해 두었으면 OpenAI 쪽 연결도 끊는다. 인터넷이 안 되면 기다리지 않고,
            // 이 PC의 로그인 파일은 아래에서 설정 폴더째 지운다.
            try
            {
                if (ChatGpt.SignedIn)
                {
                    var t = System.Threading.Tasks.Task.Run(() => ChatGpt.SignOut());
                    if (!t.Wait(TimeSpan.FromSeconds(10))) Log.Write("지우기: ChatGPT 연결 해제를 기다리지 못함");
                }
            }
            catch (Exception ex)
            {
                Log.Write("지우기: ChatGPT 연결 해제 실패 " + ex.GetType().Name);
            }
            SetRunAtStartup(false);
            TryDeleteFile(StartMenuLink);
            TryDeleteFile(SendToLink);
            TryDeleteFile(DesktopLink);
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Id);
            Util.TryDeleteDir(data);
            Util.TryDeleteDir(Path.Combine(Path.GetTempPath(), AppInfo.Id));
            string dir = AppInfo.InstallDir;
            if (!dir.EndsWith(Path.Combine("Programs", AppInfo.Id), StringComparison.OrdinalIgnoreCase)) return false;
            if (AppInfo.RunningInstalled) return true;
            Util.TryDeleteDir(dir);
            return false;
        }

        static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    public class InstallWindow : Window
    {
        readonly CheckBox startup;
        readonly Button install, cancel;
        readonly StatusPill status;
        readonly StackPanel body;
        Border error;

        public InstallWindow()
        {
            Title = AppInfo.Name + " 설치";
            Width = 580;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Polish(this);
            try { Icon = App.WindowIcon; } catch { }

            body = new StackPanel();
            var head = new DockPanel();
            var logo = Theme.Logo(56);
            logo.Margin = new Thickness(0, 0, 16, 0);
            DockPanel.SetDock(logo, Dock.Left);
            head.Children.Add(logo);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text(AppInfo.Name + " 설치", 22, FontWeights.Bold, Theme.Ink));
            var sub = Theme.MetaLabel("이 PC의 내 계정에만 설치해요. 관리자 권한이 필요 없어요.");
            sub.FontSize = 13.5;
            sub.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(sub);
            head.Children.Add(words);
            body.Children.Add(head);

            var list = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
            list.Children.Add(Feature(Theme.Purple, "shape", "파일을 끌다가 Shift", "탐색기에서 파일을 끄는 동안 Shift를 누르면 바꿈 메뉴가 떠요."));
            list.Children.Add(Feature(Theme.Blue, "link", "오른쪽 클릭 → 보내기", "파일을 오른쪽 클릭 → 보내기에도 '" + AppInfo.Name + "'이 생겨요."));
            list.Children.Add(Feature(Theme.Green, "shield", "이 PC 안에서 바꿔요", "'AI로' 칸을 고를 때만, 먼저 물어본 뒤 보내요."));
            list.Children.Add(Feature(Theme.Coral, "box", "지울 때도 쉽게", "설정 → [프로그램 지우기], 또는 윈도우 설정 → 앱에서 지워요."));
            body.Children.Add(list);

            startup = Theme.Switch("윈도우를 켤 때 같이 켜기", "꺼 두면 윈도우를 켤 때마다 바꿈을 직접 열어야 메뉴가 떠요.", true, delegate (bool on) { });
            startup.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(startup);
            if (Installer.Installed)
            {
                var again = Theme.Note("이미 설치돼 있어요. 다시 설치하면 지금 파일로 바꾸고, 설정은 그대로 둬요.", Theme.Blue);
                again.Margin = new Thickness(0, 16, 0, 0);
                body.Children.Add(again);
            }
            status = new StatusPill { Margin = new Thickness(0, 16, 0, 0) };
            body.Children.Add(status);

            install = Theme.MakeButton("설치", "Primary");
            install.IsDefault = true;
            install.MinWidth = 104;
            cancel = Theme.MakeButton("취소", "Quiet");
            cancel.IsCancel = true;
            cancel.VerticalAlignment = VerticalAlignment.Center;
            cancel.Click += delegate { Close(); };
            install.Click += delegate { Run(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            install.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(cancel);
            buttons.Children.Add(install);
            body.Children.Add(buttons);

            var card = Theme.Card(body, 28);
            card.Margin = new Thickness(20);
            Content = card;
            Loaded += delegate { install.Focus(); };
        }

        static FrameworkElement Feature(Hue hue, string glyph, string title, string text)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var chip = Theme.GlyphChip(glyph, hue, 36);
            chip.VerticalAlignment = VerticalAlignment.Top;
            chip.Margin = new Thickness(0, 1, 14, 0);
            DockPanel.SetDock(chip, Dock.Left);
            row.Children.Add(chip);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text(title, 14.5, FontWeights.Bold, Theme.Ink));
            var t = Theme.MetaLabel(text);
            t.FontSize = 13.5;
            t.LineHeight = 20;
            t.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(t);
            row.Children.Add(words);
            return row;
        }

        void Run()
        {
            install.IsEnabled = false;
            cancel.IsEnabled = false;
            startup.IsEnabled = false;
            status.Set("설치하는 중", Theme.Slate);
            if (error != null)
            {
                body.Children.Remove(error);
                error = null;
            }
            bool on = startup.IsChecked == true;
            var t = new Thread(delegate ()
            {
                string problem = null;
                try
                {
                    Installer.Install(on);
                }
                catch (UserFacingException ex)
                {
                    problem = ex.Message;
                }
                catch (Exception ex)
                {
                    Log.Write("설치 실패: " + ex.GetType().Name);
                    problem = "설치하지 못했어요. (" + ex.GetType().Name + ")";
                }
                Dispatcher.BeginInvoke(new Action(delegate { Finished(problem); }));
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        void Finished(string problem)
        {
            if (problem != null)
            {
                status.Set("", Theme.Slate);
                error = Theme.Warning(problem);
                error.Margin = new Thickness(0, 16, 0, 0);
                body.Children.Insert(body.Children.IndexOf(status), error);
                install.IsEnabled = true;
                cancel.IsEnabled = true;
                startup.IsEnabled = true;
                return;
            }
            status.Set("설치했어요 · " + AppInfo.Name + "을 켜요", Theme.Green);
            try
            {
                Process.Start(new ProcessStartInfo(AppInfo.InstalledExe) { UseShellExecute = false, WorkingDirectory = AppInfo.InstallDir });
            }
            catch (Exception ex)
            {
                Log.Write("설치 뒤 실행 실패: " + ex.GetType().Name);
            }
            Close();
        }
    }
}
