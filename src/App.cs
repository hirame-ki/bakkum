// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 프로그램 시작점. 한 번만 뜨게 하고(두 번째 실행은 첫 번째에게 파일을 넘김), 알림 영역 아이콘과 끌기 감지를 켠다.
// 시험용 명령: /render-menu, /render-window, /run   설치용: /install(창), /uninstall
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("바꿈")]
[assembly: AssemblyDescription("파일을 끌다가 키를 누르면 뜨는 변환 메뉴")]
[assembly: AssemblyProduct("바꿈")]
[assembly: AssemblyCompany("hirame.ki")]
[assembly: AssemblyCopyright("Copyright (c) 2026 hirame.ki, moonsune.zip")]
[assembly: AssemblyVersion("0.3.7.0")]
[assembly: AssemblyFileVersion("0.3.7.0")]
[assembly: AssemblyInformationalVersion("0.3.7")]

namespace Bakkum
{
    public class App : Application
    {
        const string MutexName = @"Local\Bakkum.Instance";
        static Mutex instanceMutex;
        static System.Windows.Forms.NotifyIcon tray;
        static DragTrigger trigger;
        static SettingsWindow settingsWindow;

        [DllImport("kernel32.dll")]
        static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int pid);

        public static string PipeName
        {
            get
            {
                string sid = "user";
                try { sid = WindowsIdentity.GetCurrent().User.Value; } catch { }
                return "Bakkum-" + sid + "-" + Process.GetCurrentProcess().SessionId;
            }
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool TerminateProcess(IntPtr process, uint code);

        [STAThread]
        public static int Main(string[] args)
        {
            int code = Start(args);
            // 일을 모두 마친 뒤에는 바로 끝낸다. 윈도우 PDF 엔진을 쓴 프로세스는 끝내는 정리 단계에서
            // CPU를 다 쓰며 멈출 수 있다(2026-11-08 실측). 저장할 것은 이미 다 저장한 뒤다.
            TerminateProcess(GetCurrentProcess(), (uint)code);
            return code;
        }

        static int Start(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                var ex = e.ExceptionObject as Exception;
                Log.Write("처리 못 한 오류: " + (ex == null ? "?" : ex.GetType().Name + " @ " + ex.TargetSite));
            };

            var flags = new List<string>();
            var files = new List<string>();
            foreach (var a in args)
            {
                if (a.StartsWith("/")) flags.Add(a.ToLowerInvariant());
                else files.Add(a);
            }
            string first = args.Length > 0 && args[0].StartsWith("/") ? args[0].ToLowerInvariant() : "";

            try
            {
                switch (first)
                {
                    case "/render-deck": return Cli.RenderDeck(args);
                    case "/render-menu": return Cli.RenderMenu(args);
                    case "/render-window": return Cli.RenderWindow(args);
                    case "/selftest-net": return SelfTest.Run(args);
                    case "/selftest-tools": return ToolTest.Run(args);
                    case "/run": return Cli.Run(args);
                    case "/uninstall": return RunDialogApp(delegate { Installer.UninstallInteractive(flags.Contains("/quiet")); });
                    case "/install": return RunWindowApp(delegate { new InstallWindow().Show(); });
                    case "/update": return RunDialogApp(delegate { Updater.FinishHere(); });
                }

                bool portable = flags.Contains("/portable");
                if (!AppInfo.RunningInstalled && !portable && !flags.Contains("/quit"))
                {
                    return RunWindowApp(delegate { new InstallWindow().Show(); });
                }
                return RunMain(Formats.Expand(files), flags);
            }
            catch (Exception ex)
            {
                Log.Write("시작 실패: " + ex.GetType().Name);
                MessageBox.Show("프로그램을 시작하지 못했어요.\n" + ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        /// 설치·제거 창처럼 창 하나만 쓰고 끝나는 실행
        static int RunWindowApp(Action show)
        {
            var app = new App { ShutdownMode = ShutdownMode.OnLastWindowClose };
            Theme.Install(app);
            app.Startup += delegate { show(); if (app.Windows.Count == 0) app.Shutdown(); };
            return app.Run();
        }

        /// 대화 창 몇 개를 차례로 띄우고 끝나는 실행(지우기). 창 하나를 닫았다고 끝나지 않게, 일이 끝나면 직접 끝낸다.
        static int RunDialogApp(Action work)
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Install(app);
            app.Startup += delegate
            {
                try { work(); }
                finally { app.Shutdown(); }
            };
            return app.Run();
        }

        static int RunMain(List<string> files, List<string> flags)
        {
            // 스타일을 바꾸고 다시 켤 때: 앞 프로세스가 끝나기를 기다린다(같은 이름의 파이프·잠금을 넘겨받게)
            foreach (var f in flags)
            {
                int pid;
                if (!f.StartsWith("/after:") || !int.TryParse(f.Substring(7), out pid)) continue;
                try
                {
                    using (var p = Process.GetProcessById(pid)) p.WaitForExit(10000);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            bool created;
            instanceMutex = new Mutex(true, MutexName, out created);
            if (!created)
            {
                string msg;
                if (flags.Contains("/quit")) msg = "quit";
                else if (files.Count > 0) msg = "menu\n" + string.Join("\n", files);
                else if (flags.Contains("/background")) return 0;
                else msg = "settings";
                for (int i = 0; i < 4; i++)
                {
                    if (SendToRunning(msg)) return 0;
                    Thread.Sleep(400);
                }
                return 1;
            }
            if (flags.Contains("/quit"))
            {
                instanceMutex.ReleaseMutex();
                return 0;
            }

            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Install(app);
            app.Startup += delegate { Start(files, flags); };
            int code = app.Run();
            try { instanceMutex.ReleaseMutex(); } catch { }
            if (restarting) StartAgain(flags);
            return code;
        }

        static bool restarting;

        /// 끝낸 뒤 새로 켠다(스타일·색감을 바꾼 뒤). 새로 켠 쪽은 설정 창을 연다.
        public static void Restart()
        {
            restarting = true;
            Quit();
        }

        static void StartAgain(List<string> flags)
        {
            try
            {
                string args = "/after:" + Process.GetCurrentProcess().Id + (flags.Contains("/portable") ? " /portable" : "");
                using (Process.Start(new ProcessStartInfo(AppInfo.ExePath, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(AppInfo.ExePath) })) { }
            }
            catch (Exception ex)
            {
                Log.Write("다시 켜기 실패: " + ex.GetType().Name);
            }
        }

        /// 이미 떠 있는 프로그램이 있는지(설치·제거 때 확인)
        public static bool IsRunning()
        {
            try
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(MutexName, out m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        public static bool SendToRunning(string msg)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(2500);
                    uint pid;
                    if (GetNamedPipeServerProcessId(c.SafePipeHandle.DangerousGetHandle(), out pid)) AllowSetForegroundWindow((int)pid);
                    byte[] b = Encoding.UTF8.GetBytes(msg);
                    c.Write(b, 0, b.Length);
                    c.Flush();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void Start(List<string> files, List<string> flags)
        {
            StartPipe();
            SetupTray();
            trigger = new DragTrigger();
            trigger.Start();
            AppSettings.Changed += OnSettingsChanged;
            bool quiet = flags.Contains("/background") || flags.Contains("/updated");
            if (flags.Contains("/updated")) Toast.ShowMessage("새 버전으로 바꿨어요", AppInfo.Name + " " + AppInfo.Version, false);
            if (files.Count > 0) ShowMenuForFiles(files);
            else if (!quiet && AppInfo.RunningInstalled && !AppSettings.Current.GuideShown) GuideWindow.Open();
            else if (!quiet) OpenSettings();
            if (AppInfo.RunningInstalled)
            {
                Installer.EnsureAppListedLater();
                Updater.CleanUp();
                Updater.StartChecks(Current.Dispatcher);
                Usage.StartDaily();
            }
            Log.Write("시작 " + AppInfo.Version + (AppInfo.RunningInstalled ? "" : " (설치 안 한 채)"));
        }

        static void StartPipe()
        {
            var disp = Current.Dispatcher;
            var t = new Thread(delegate ()
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            var ms = new MemoryStream();
                            var buf = new byte[8192];
                            int n;
                            while ((n = server.Read(buf, 0, buf.Length)) > 0)
                            {
                                ms.Write(buf, 0, n);
                                if (ms.Length > (1 << 20)) break;
                            }
                            string text = Encoding.UTF8.GetString(ms.ToArray());
                            disp.BeginInvoke(new Action(delegate { HandleMessage(text); }));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("파이프: " + ex.GetType().Name);
                        Thread.Sleep(500);
                    }
                }
            });
            t.IsBackground = true;
            t.Name = "pipe";
            t.Start();
        }

        static void HandleMessage(string text)
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) return;
            if (Quitting && lines[0] != "quit") return;   // 끝내는 중에는 메뉴·설정을 새로 열지 않는다
            switch (lines[0])
            {
                case "menu":
                    ShowMenuForFiles(Formats.Expand(lines.Skip(1)));
                    break;
                case "settings":
                    OpenSettings();
                    break;
                case "quit":
                    Quit();
                    break;
            }
        }

        public static void ShowMenuForFiles(List<string> files)
        {
            MenuWindow.ShowAt(MenuWindow.CursorPx(), false, files);
        }

        // ── 알림 영역 아이콘 ──

        static void SetupTray()
        {
            tray = new System.Windows.Forms.NotifyIcon();
            tray.Icon = LoadIcon(System.Windows.Forms.SystemInformation.SmallIconSize.Width);
            tray.Text = AppInfo.Name;
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("변환 메뉴 열기", null, delegate { ShowMenuForFiles(new List<string>()); });
            var record = new System.Windows.Forms.ToolStripMenuItem("화면 녹화", null, delegate
            {
                if (RecordUi.Recording) RecordUi.Stop();
                else RecordUi.Open();
            });
            menu.Items.Add(record);
            var recent = new System.Windows.Forms.ToolStripMenuItem("최근 결과");
            menu.Items.Add(recent);
            menu.Items.Add("설정", null, delegate { OpenSettings(); });
            menu.Items.Add("사용법", null, delegate { GuideWindow.Open(); });
            menu.Items.Add("업데이트 확인", null, delegate { Updater.CheckNow(Current.Dispatcher); });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Opening += delegate
            {
                record.Text = RecordUi.Recording ? "녹화 끝내기" : "화면 녹화";
                recent.DropDownItems.Clear();
                var made = Jobs.RecentOutputs().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                recent.Enabled = made.Count > 0;
                foreach (string p in made)
                {
                    string path = p;
                    recent.DropDownItems.Add(Util.ShortName(path), null, delegate { Util.Reveal(path); });
                }
            };
            menu.Items.Add("프로그램 종료", null, delegate { if (AskStopJobs(null)) Quit(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate (object s, System.Windows.Forms.MouseEventArgs e)
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) OpenSettings();
            };
            tray.Visible = true;
        }

        static Stream IconStream()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream("Bakkum.app.ico");
        }

        public static System.Drawing.Icon LoadIcon(int size)
        {
            using (var s = IconStream())
            {
                if (s == null) return System.Drawing.SystemIcons.Application;
                return new System.Drawing.Icon(s, size, size);
            }
        }

        static ImageSource windowIcon;

        public static ImageSource WindowIcon
        {
            get
            {
                if (windowIcon == null)
                {
                    try
                    {
                        using (var s = IconStream())
                        {
                            if (s != null)
                            {
                                var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                                var best = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
                                best.Freeze();
                                windowIcon = best;
                            }
                        }
                    }
                    catch { }
                }
                return windowIcon;
            }
        }

        // ── 다른 부분이 부르는 일 ──

        public static void OpenSettings()
        {
            OpenSettings(null);
        }

        /// page: 처음 보일 쪽(SettingsWindow.PageIds). null이면 지난번에 보던 쪽.
        public static void OpenSettings(string page)
        {
            if (settingsWindow != null)
            {
                if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
                if (page != null) settingsWindow.ShowPage(page);
                settingsWindow.Activate();
                return;
            }
            settingsWindow = new SettingsWindow(page);
            settingsWindow.Closed += delegate { settingsWindow = null; };
            settingsWindow.Show();
            settingsWindow.Activate();
        }

        public static void Notify(string title, string message, bool isError)
        {
            if (Cli.Active)
            {
                Cli.Print((isError ? "[알림·오류] " : "[알림] ") + title + ": " + message);
                return;
            }
            Toast.ShowMessage(title, message, isError);
        }

        public static void RunAction(MenuAction a, ActionOption o, List<string> files)
        {
            if (a == null || o == null || files == null || files.Count == 0) return;
            // 창을 여는 일(사진 손질·쪽 편집·콜라주)은 창이 끝난 뒤에 작업이 된다.
            if (Workbench.Open(a, o, files)) return;
            var job = new Job(a, o, files);
            if (!Prompts.Ask(job)) return;
            Jobs.Start(job);
        }

        public static void AskToSetUpAi()
        {
            int n = Dialog.Ask(null, Theme.Sky, "ai", "AI 연결이 아직 없어요",
                "요약·번역·문제 만들기는 AI를 연결한 뒤에 쓸 수 있어요. 쓰고 있는 Claude나 ChatGPT 구독, 무료 Gemini 키 가운데 하나를 고르면 돼요.",
                null, "나중에", "설정 열기");
            if (n == 1) OpenSettings("ai");
        }

        static void OnSettingsChanged()
        {
            if (trigger != null) trigger.Reload();
            if (AppInfo.RunningInstalled) Installer.SetRunAtStartup(AppSettings.Current.StartWithWindows);
        }

        /// 끝내는 중(창들이 닫혀도 '저장 없이 닫기'로 보지 않게)
        public static bool Quitting;

        /// [프로그램 종료](알림 영역·설정 창): 하던 변환이 있으면 멈추고 끝낼지 묻는다. 끝내도 되면 true.
        public static bool AskStopJobs(Window owner)
        {
            return !Jobs.Busy || Dialog.Ask(owner, Theme.Coral, "close", "하던 변환을 멈추고 끝낼까요?",
                "지금 바꾸던 파일은 만들어지지 않아요. 원본은 그대로예요.", null, "계속 바꾸기", "멈추고 끝내기") == 1;
        }

        public static void Quit()
        {
            if (Quitting) return;
            Quitting = true;
            if (MenuWindow.Current != null) MenuWindow.Current.CloseNow();
            RecordUi.StopForQuit();
            try { if (trigger != null) trigger.Dispose(); } catch { }
            trigger = null;
            Jobs.CancelAll();
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            // Main은 끝에서 프로세스를 바로 끝낸다. 그 전에 작업 줄이 취소를 받아 반쯤 쓴 결과를 지우고
            // 띄운 한글·워드를 닫을 틈, 이미 [끝내기]를 누른 녹화가 저장을 마칠 틈을 준다
            // (설치 프로그램은 10초를 기다리니 그보다 짧게). 끝내는 동안 새 작업은 받지 않는다(Jobs.Start).
            // 기다리는 동안 화면 스레드는 막지 않는다. 작업이 화면 스레드에 물을 수 있다.
            var ui = Current.Dispatcher;
            new Thread(delegate ()
            {
                var until = DateTime.UtcNow.AddSeconds(8);
                while ((Jobs.Busy || RecordUi.Active) && DateTime.UtcNow < until) Thread.Sleep(100);
                bool idle = !Jobs.Busy && !RecordUi.Active;
                ui.BeginInvoke(new Action(delegate
                {
                    Log.Write(idle ? "끝냄" : "끝냄(작업이 8초 안에 멈추지 않음)");
                    Current.Shutdown();
                }));
            }) { IsBackground = true, Name = "끝내기" }.Start();
        }
    }

    /// 시험용 명령줄. 결과는 표준 출력(UTF-8)으로 쓴다.
    public static class Cli
    {
        public static bool Active;
        static TextWriter output;

        public static void Print(string line)
        {
            if (output == null)
            {
                output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            }
            output.WriteLine(line);
        }

        /// /render-menu 모양 결과.png [칸번호] [choose:할일] [파일...]
        /// 시험용: /render-deck AI답.json 결과.pptx [디자인]
        public static int RenderDeck(string[] args)
        {
            Active = true;
            if (args.Length < 3) return 2;
            var d = Json.Parse(System.IO.File.ReadAllText(args[1], System.Text.Encoding.UTF8));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(args[2])));
            DeckWriter.Write(args[2], DeckDesign.Build(d, "제목 없음", DeckDesign.StyleOf(args.Length > 3 ? args[3] : "paper")));
            Print("만듦: " + args[2]);
            return 0;
        }

        /// 그림 확인용: style:카툰풍처럼 스타일·색감을 고른다(테마를 읽기 전에, 저장하지 않음).
        static void PickLook(string[] args)
        {
            foreach (var a in args)
            {
                if (a.StartsWith("style:")) AppSettings.Current.UiStyle = a.Substring(6);
                else if (a.StartsWith("mood:")) AppSettings.Current.UiMood = a.Substring(5);
            }
        }

        public static int RenderMenu(string[] args)
        {
            Active = true;
            PickLook(args);
            if (args.Length < 3)
            {
                Print("사용: /render-menu index|fan|wave|honeycomb 결과.png [칸번호] [choose:shrink] [at:x,y] [파일...]");
                return 2;
            }
            int hot = -1;
            string choose = null;
            var files = new List<string>();
            for (int i = 3; i < args.Length; i++)
            {
                int n;
                if (args[i].StartsWith("choose:")) choose = args[i].Substring(7);
                else if (args[i].StartsWith("at:"))
                {
                    var xy = args[i].Substring(3).Split(',');
                    int x, y;
                    if (xy.Length == 2 && int.TryParse(xy[0], out x) && int.TryParse(xy[1], out y)) MenuWindow.PreviewAt = new System.Drawing.Point(x, y);
                }
                else if (int.TryParse(args[i], out n)) hot = n;
                else files.Add(args[i]);
            }
            var app = new App();
            Theme.Install(app);
            MenuWindow.RenderPreview(args[1], args[2], hot, files, choose);
            Print("그림: " + args[2]);
            return 0;
        }

        /// /render-window settings|consent|install|dialog|toast|toast-done|toast-error|notice|photo|video 결과.png [ai:연결] [state:chatgpt,openrouter] [h:높이] (settings: [page:shape|slots|key|ai|other] [full]) (video: [state:칸] [demo] 영상 파일)
        /// 창을 화면 밖에 잠깐 띄워 그림으로 찍는다. 설정은 저장하지 않고, 네트워크·Claude Code도 부르지 않는다.
        public static int RenderWindow(string[] args)
        {
            Active = true;
            PickLook(args);
            if (args.Length < 3)
            {
                Print("사용: /render-window settings|consent|install|dialog|toast|toast-done|toast-error|notice|photo|video 결과.png [ai:연결] [state:...] [h:높이] (settings: [page:쪽] [full]) (video: [state:칸] [demo] [panel] 영상 파일)");
                return 2;
            }
            string what = args[1].ToLowerInvariant();
            string outPng = args[2];
            string ai = null, state = "";
            double height = 0;
            for (int i = 3; i < args.Length; i++)
            {
                if (args[i].StartsWith("ai:")) ai = args[i].Substring(3);
                else if (args[i].StartsWith("state:")) state = args[i].Substring(6);
                else if (args[i].StartsWith("h:")) double.TryParse(args[i].Substring(2), out height);
            }
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Install(app);
            if (ai != null) AppSettings.Current.AiProvider = ai;
            SettingsWindow.Demo = true;
            SettingsWindow.DemoState = state;
            Window w;
            switch (what)
            {
                case "settings":
                {
                    // page:shape|slots|key|ai|other 로 쪽을 고른다. full 을 붙이면 쪽 내용을 스크롤 없이 끝까지 펼쳐 찍는다
                    // (창은 화면보다 길어질 수 없으니 내용을 창에서 떼어 낸다). 없으면 실제 창 크기 그대로.
                    string page = null;
                    foreach (var x in args) if (x.StartsWith("page:")) page = x.Substring(5);
                    var sw = new SettingsWindow(page);
                    if (args.Contains("full"))
                    {
                        SnapDetached(sw, sw.DetachForPreview(), outPng, sw.Width - 16);
                        Print("그림: " + outPng);
                        return 0;
                    }
                    w = sw;
                    break;
                }
                case "consent":
                    w = ConsentWindow.ForPreview(3);
                    break;
                case "install":
                    w = new InstallWindow();
                    break;
                case "dialog":
                    if (state == "autosub" || state == "autosub-base")
                    {
                        w = Dialog.Build(null, Theme.Teal, "subtitle", "자동 자막 모델을 받을까요?", VideoEditor.DownloadBody(state == "autosub" ? WhisperModels.Turbo : WhisperModels.Base), null, "그만두기", "받고 만들기");
                        break;
                    }
                    w = Dialog.Build(null, Theme.Green, "check", "ChatGPT 요금제를 사용하고 있어요",
                        "AI로 글자를 뽑으면 선생님의 ChatGPT 요금제 사용량에서 빠져요. 남은 사용량은 chatgpt.com의 설정 → 사용량에서 볼 수 있어요.",
                        null, "사용량 보기", "확인");
                    break;
                case "toast":
                {
                    var a = MenuAction.All.First(x => x.Id == (ai ?? "pdf"));
                    bool flow = state == "flow";
                    w = Toast.ForPreview(a, a.Label, flow ? "AI가 읽는 중" : "3개 중 2번째 바꾸는 중", flow ? -1 : 0.62);
                    break;
                }
                case "toast-done":
                {
                    var a = MenuAction.All.First(x => x.Id == (ai ?? "pdf"));
                    string example = state == "compare" ? "가.jpg" : "가.hwp";
                    var job = new Job(a, a.OptionsFor(new List<string> { example }).First(), new List<string> { example });
                    job.Output(state == "compare" ? @"C:\예시\체험학습 사진_작게.jpg" : @"C:\예시\2학기 가정통신문.pdf");
                    if (state == "many")
                    {
                        job.Output(@"C:\예시\수행평가 안내.pdf");
                        job.Output(@"C:\예시\학급 규칙.pdf");
                    }
                    if (state == "partial") job.Problem(@"C:\예시\암호 걸린 파일.hwp", "암호가 걸려 있어서 열지 못했어요.");
                    w = Toast.DoneForPreview(job);
                    break;
                }
                case "toast-error":
                    w = Toast.Message("바꾸지 못했어요", "한글 프로그램을 찾지 못했어요. 한컴오피스가 설치돼 있는지 확인해 주세요.", true);
                    break;
                case "notice":
                    w = NoticeWindow.Build(null);
                    break;
                case "photo":
                {
                    // state: 도구 이름. 그 뒤 인자는 사진 파일. demo 를 넣으면 예시 표시를 얹는다.
                    var a = MenuAction.All.First(x => x.Id == "photo");
                    var photos = args.Skip(3).Where(x => File.Exists(x)).ToList();
                    var pe = PhotoEditor.ForPreview(a, photos, ai ?? "edit", state);
                    if (args.Contains("demo")) pe.AddDemoMarks();
                    w = pe;
                    break;
                }
                case "collage":
                {
                    var a = MenuAction.All.First(x => x.Id == "photo");
                    w = new CollageWindow(a, args.Skip(3).Where(x => File.Exists(x)).ToList(), true);
                    break;
                }
                case "compare":
                {
                    var both = args.Skip(3).Where(x => File.Exists(x)).ToList();
                    w = CompareWindow.ForPreview(both[0], both[1], Theme.HueOf("shrink"));
                    break;
                }
                case "guide":
                    w = GuideWindow.ForPreview();
                    break;
                case "record":
                    w = RecordUi.PreviewOptions();
                    break;
                case "record-bar":
                    w = RecordUi.PreviewBar(state == "paused");
                    break;
                case "record-count":
                    w = RecordUi.PreviewCountdown();
                    break;
                case "record-area":
                    w = RecordUi.PreviewPicker();
                    break;
                case "cam-bubble":
                    w = CamBubble.ForPreview(state == "dark");
                    break;
                case "update-toast":
                    w = Toast.Offer("새 버전이 있어요 · 0.3.0", "사진 손질과 'AI로' 칸을 고쳤어요.", "지금 바꾸기", delegate { });
                    break;
                case "aiconsent":
                {
                    var plan = new SendPlan { Files = 1, Chars = 1284, Pictures = state == "pictures" ? 2 : 0, Names = 3, Phones = 1, Others = 0, Plain = "담임교사: 김민수", Masked = "담임교사: [[N1]]" };
                    if (state == "folder")
                    {
                        plan = new SendPlan { Files = 24, Chars = 860, Names = 1, FileNames = true, Plain = "1\t김민수 학생 상담.hwp\t2026-11-02", Masked = "1\t[[N1]] 학생 상담.hwp\t2026-11-02" };
                    }
                    w = SendConsentWindow.ForPreview(plan);
                    break;
                }
                case "organize":
                    w = AiTasks.OrganizePreview(state);
                    break;
                case "prompt":
                {
                    // state: "칸/고를거리" (예: pdftool/lock)
                    var parts = state.Split('/');
                    var a = MenuAction.All.First(x => x.Id == parts[0]);
                    w = Prompts.Preview(new Job(a, new ActionOption(parts[1], parts[1], ""), args.Skip(3).Where(x => File.Exists(x)).ToList()));
                    if (w == null)
                    {
                        Print("물어볼 것이 없는 일: " + state);
                        return 2;
                    }
                    break;
                }
                case "pdfpages":
                {
                    var a = MenuAction.All.First(x => x.Id == "pdftool");
                    w = new PdfPagesWindow(a, args.Skip(3).Where(x => File.Exists(x)).ToList(), true);
                    break;
                }
                case "video":
                {
                    // state: 처음 볼 칸(edit|subtitle|deco|ratio|sound|look|redact|teacher|export). 그 뒤 인자는 영상·사진 파일.
                    // demo 를 넣으면 예시 자막·스티커·표시를 얹는다. 자동 저장·내보내기는 하지 않는다.
                    var a = MenuAction.All.First(x => x.Id == "media");
                    var media = args.Skip(3).Where(x => File.Exists(x)).ToList();
                    if (media.Count == 0)
                    {
                        Print("영상 파일을 넣어 주세요.");
                        return 2;
                    }
                    var ve = VideoEditor.ForPreview(a, media, string.IsNullOrEmpty(state) ? "edit" : state, args.Contains("demo"));
                    if (args.Contains("panel"))
                    {
                        // 오른쪽 칸을 스크롤 없이 끝까지: 창을 화면 밖에 한 번 띄워 칸을 채운 뒤, 칸만 떼어 내 찍는다
                        ve.WindowStartupLocation = WindowStartupLocation.Manual;
                        ve.Left = -30000;
                        ve.Top = -30000;
                        ve.ShowActivated = false;
                        ve.ShowInTaskbar = false;
                        ve.Show();
                        Pump(12);
                        SnapDetached(ve, ve.DetachPanelForPreview(), outPng, 340 - 36);
                        Print("그림: " + outPng);
                        return 0;
                    }
                    w = ve;
                    break;
                }
                default:
                    Print("모르는 창: " + what);
                    return 2;
            }
            Snap(w, outPng, height);
            Print("그림: " + outPng);
            return 0;
        }

        static void Pump(int times)
        {
            for (int i = 0; i < times; i++)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new System.Windows.Threading.DispatcherOperationCallback(delegate (object f) { ((System.Windows.Threading.DispatcherFrame)f).Continue = false; return null; }), frame);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Thread.Sleep(40);
            }
        }

        static void SnapDetached(Window w, FrameworkElement content, string outPng, double width)
        {
            var host = new System.Windows.Controls.Border { Background = Theme.Desk, Child = content, Width = width };
            TextElement.SetFontFamily(host, Theme.Font);
            TextElement.SetForeground(host, Theme.Ink);
            TextOptions.SetTextFormattingMode(host, System.Windows.Media.TextFormattingMode.Display);
            host.Measure(new Size(width, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
            host.UpdateLayout();
            Pump(6);
            host.UpdateLayout();
            double scale = 2;
            int pw = (int)Math.Ceiling(host.ActualWidth * scale), ph = (int)Math.Ceiling(host.ActualHeight * scale);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var f = File.Create(outPng)) enc.Save(f);
            w.Close();
        }

        static void Snap(Window w, string outPng, double height)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -30000;
            w.Top = -30000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            w.Topmost = false;
            if (height > 0)
            {
                w.SizeToContent = SizeToContent.Manual;
                w.Height = height;
            }
            w.Show();
            Pump(12);
            // 내용(w.Content)을 바로 찍으면 바깥 여백만큼 밀려 오른쪽·아래가 잘린다. 창 안쪽 전체(틀의 맨 위 조각)를 찍는다.
            var root = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(w, 0);
            root.UpdateLayout();
            Pump(4);
            double scale = 2;
            int pw = (int)Math.Ceiling(root.ActualWidth * scale), ph = (int)Math.Ceiling(root.ActualHeight * scale);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Media.DrawingVisual();
            using (var dc = bg.RenderOpen())
            {
                var brush = w.Background is System.Windows.Media.SolidColorBrush && ((System.Windows.Media.SolidColorBrush)w.Background).Color.A > 0 ? w.Background : Theme.Desk;
                dc.DrawRectangle(brush, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            rtb.Render(bg);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var f = File.Create(outPng)) enc.Save(f);
            w.Close();
        }

        /// /run 할일 고를거리 파일... [/yes]
        public static int Run(string[] args)
        {
            Active = true;
            if (args.Length < 4)
            {
                Print("사용: /run pdf|image|format|shrink|text 고를거리 파일...");
                return 2;
            }
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Install(app);
            // 시험 때만: 이 실행에서만 쓸 AI 연결(저장하지 않는다)
            foreach (var x in args.Where(y => y.StartsWith("/ai:"))) AppSettings.Current.AiProvider = x.Substring(4);
            var a = MenuAction.All.FirstOrDefault(x => x.Id == args[1]);
            if (a == null || a.IsSettings)
            {
                Print("모르는 할 일: " + args[1]);
                return 2;
            }
            var files = Formats.Expand(args.Skip(3).Where(x => !x.StartsWith("/")));
            var usable = a.Usable(files);
            var opt = a.OptionsFor(usable).SelectMany(x => x.Flatten()).FirstOrDefault(x => x.Id == args[2]);
            if (opt == null)
            {
                Print("이 파일들에 쓸 수 없는 고를거리: " + args[2] + " (가능: " + string.Join(", ", a.OptionsFor(usable).Select(x => x.Id)) + ")");
                return 2;
            }
            var job = new Job(a, opt, files) { Headless = true, AutoYes = args.Any(x => x.ToLowerInvariant() == "/yes") };
            foreach (var x in args.Where(y => y.StartsWith("/p:")))
            {
                int eq = x.IndexOf('=');
                if (eq > 3) job.Params[x.Substring(3, eq - 3)] = x.Substring(eq + 1);
            }
            var sw = Stopwatch.StartNew();
            Jobs.RunNow(job);
            Print("걸린 시간: " + sw.Elapsed.TotalSeconds.ToString("0.0") + "초");
            foreach (var o in job.Outputs) Print("만듦: " + o);
            foreach (var p in job.Problems) Print("문제: " + p);
            foreach (var n in job.Notes) Print("참고: " + n);
            return job.Problems.Count == 0 && job.Outputs.Count > 0 ? 0 : 1;
        }
    }
}
