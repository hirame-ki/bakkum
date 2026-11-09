// 새 버전 받기 시험: 이 PC 안의 작은 가짜 서버(127.0.0.1)만 쓴다. 받은 파일을 실행하지는 않는다.
// Bakkum.exe /selftest-tools 시험폴더 update
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Bakkum
{
    static partial class ToolTest
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        // .NET의 File.Exists는 콜론이 든 경로를 늘 '없음'으로 답하므로 API로 직접 연다.
        static bool HasZoneMark(string file)
        {
            IntPtr h = CreateFileW(file + ":Zone.Identifier", 0x80000000, 1, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) return false;
            CloseHandle(h);
            return true;
        }

        static void AddZoneMark(string file)
        {
            IntPtr h = CreateFileW(file + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero);
            if (h != new IntPtr(-1)) CloseHandle(h);
        }

        sealed class TinyServer : IDisposable
        {
            readonly TcpListener door = new TcpListener(IPAddress.Loopback, 0);
            public readonly int Port;
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();

            public TinyServer()
            {
                door.Start();
                Port = ((IPEndPoint)door.LocalEndpoint).Port;
                var t = new Thread(Loop) { IsBackground = true };
                t.Start();
            }

            void Loop()
            {
                while (true)
                {
                    TcpClient c;
                    try { c = door.AcceptTcpClient(); }
                    catch { return; }
                    try
                    {
                        using (c)
                        {
                            var s = c.GetStream();
                            var buf = new byte[4096];
                            int n = s.Read(buf, 0, buf.Length);
                            string first = Encoding.ASCII.GetString(buf, 0, n).Split('\r')[0];
                            string path = first.Split(' ')[1];
                            byte[] body;
                            bool ok = Files.TryGetValue(path, out body);
                            if (!ok) body = new byte[0];
                            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + (ok ? "200 OK" : "404 Not Found") + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                            s.Write(head, 0, head.Length);
                            s.Write(body, 0, body.Length);
                        }
                    }
                    catch { }
                }
            }

            public void Dispose()
            {
                door.Stop();
            }
        }

        static string Manifest(string version, string url, string sha)
        {
            return "{\"version\":\"" + version + "\",\"url\":\"" + url + "\",\"sha256\":\"" + sha + "\",\"notes\":\"고친 점\"}";
        }

        static void UpdateTests()
        {
            Cli.Print("[새 버전]");
            Check("버전 비교", Updater.IsNewer("0.2.1", "0.2.0") && Updater.IsNewer("0.10.0", "0.9.9") && Updater.IsNewer("1.0", "0.9.9")
                && !Updater.IsNewer("0.2.0", "0.2.0") && !Updater.IsNewer("0.2", "0.2.0") && !Updater.IsNewer("0.1.9", "0.2.0") && !Updater.IsNewer("abc", "0.2.0"), null);
            int polled = 0;
            Check("다시 켜지면 더 기다리지 않는다", Updater.CameBack(() => ++polled >= 3, () => false, 1, 50) && polled == 3, polled.ToString());
            polled = 0;
            Check("막혀서 끝나면 바로 알린다", !Updater.CameBack(() => false, () => ++polled >= 2, 1, 50) && polled == 2, polled.ToString());
            polled = 0;
            Check("답이 없어도 언젠가는 알린다", !Updater.CameBack(() => false, () => false, 1, 5), null);
            Check("켜지는 틈을 한 번 더 본다", Updater.CameBack(() => ++polled >= 2, () => true, 1, 50), polled.ToString());

            string good = "https://github.com/hirame-ki/bakkum/releases/download/v9.9.9/Bakkum.exe";
            string sha = new string('a', 64);
            Check("올바른 버전 파일", Updater.Parse(Manifest("9.9.9", good, sha)) != null, null);
            Check("사이트 주소도 받는다", Updater.Parse(Manifest("9.9.9", "https://hirame-ki.github.io/bakkum/Bakkum.exe", sha)) != null, null);
            foreach (var bad in new[]
            {
                "https://evil.example/Bakkum.exe",
                "http://github.com/hirame-ki/bakkum/releases/download/v9/Bakkum.exe",
                "https://github.com/hirame-ki/bakkum/releases/download/../../../other/x.exe",
                "https://github.com/hirame-ki/bakkum-evil/releases/download/v9/Bakkum.exe",
                "https://github.com@evil.example/hirame-ki/bakkum/releases/download/v9/Bakkum.exe",
                "https://hirame-ki.github.io/other/Bakkum.exe",
                "file:///C:/Windows/notepad.exe",
                ""
            })
            {
                Check("받지 않는 주소: " + (bad.Length > 44 ? bad.Substring(0, 44) + "…" : bad), Updater.Parse(Manifest("9.9.9", bad, sha)) == null, null);
            }
            Check("해시 모양이 틀리면 받지 않는다", Updater.Parse(Manifest("9.9.9", good, "abc")) == null, null);
            Check("버전 모양이 틀리면 받지 않는다", Updater.Parse(Manifest("새 버전", good, sha)) == null, null);
            Check("깨진 파일", Updater.Parse("{") == null && Updater.Parse("") == null, null);

            byte[] program = Encoding.UTF8.GetBytes("가짜 프로그램 — 실행하지 않는다 " + new string('x', 200000));
            string work = P("updatejob");
            Directory.CreateDirectory(work);
            string local = Path.Combine(work, "program.bin");
            File.WriteAllBytes(local, program);
            string realSha = Updater.Sha256Of(local);
            string control = Path.Combine(work, "control.bin");
            File.WriteAllBytes(control, new byte[10]);
            bool markBefore = HasZoneMark(control);
            AddZoneMark(control);
            Check("인터넷 표시 검사가 제대로 작동한다", !markBefore && HasZoneMark(control), null);
            ReplaceChecks(work);
            using (var server = new TinyServer())
            {
                string root = "http://127.0.0.1:" + server.Port;
                Updater.UseTestServer(root);
                Check("다른 포트의 주소는 받지 않는다", Updater.Parse(Manifest("9.9.9", "http://127.0.0.1:1/Bakkum.exe", sha)) == null, null);
                server.Files["/Bakkum.exe"] = program;
                server.Files["/version.json"] = Encoding.UTF8.GetBytes(Manifest("9.9.9", root + "/Bakkum.exe", realSha));
                var info = Updater.Fetch();
                Check("새 버전을 알아본다", info != null && info.Version == "9.9.9" && Updater.Available != null, info == null ? "없음" : info.Version);
                Check("고친 점의 한글을 읽는다", info != null && info.Notes == "고친 점", info == null ? "없음" : info.Notes);

                double last = -1;
                string got = Updater.Download(info, delegate (double f) { last = f; });
                Check("받아서 해시를 확인한다", File.Exists(got) && File.ReadAllBytes(got).SequenceEqual(program), Conversions.Size(new FileInfo(got).Length));
                Check("진행 정도를 알린다", last > 0.99, last.ToString("0.00"));
                Check("받은 파일에 인터넷 표시가 없다", !HasZoneMark(got), null);
                Util.TryDeleteDir(Path.GetDirectoryName(got));

                string thrown = null;
                string wrongFolder = null;
                try { wrongFolder = Updater.Download(new UpdateInfo { Version = "9.9.9", Url = root + "/Bakkum.exe", Sha256 = new string('0', 64) }, null); }
                catch (UserFacingException ex) { thrown = ex.Message; }
                Check("해시가 틀리면 쓰지 않는다", thrown != null && wrongFolder == null, thrown);

                thrown = null;
                try { Updater.Download(new UpdateInfo { Version = "9.9.9", Url = root + "/none.exe", Sha256 = realSha }, null); }
                catch (UserFacingException ex) { thrown = ex.Message; }
                Check("파일이 없으면 알린다", thrown != null, thrown);

                thrown = null;
                try { Web.Download(root + "/Bakkum.exe", Path.Combine(work, "big.bin"), 1000, TimeSpan.FromSeconds(30), null, "시험"); }
                catch (UserFacingException ex) { thrown = ex.Message; }
                Check("너무 큰 파일은 멈춘다", thrown != null && !File.Exists(Path.Combine(work, "big.bin")), thrown);

                server.Files["/version.json"] = Encoding.UTF8.GetBytes(Manifest(AppInfo.Version, root + "/Bakkum.exe", realSha));
                Check("같은 버전이면 알리지 않는다", Updater.Fetch() == null && Updater.Available == null, null);

                server.Files.Remove("/version.json");
                thrown = null;
                try { Updater.Fetch(); }
                catch (UserFacingException ex) { thrown = ex.Message; }
                Check("버전 파일이 없으면(사이트를 아직 안 올렸으면) 조용히 실패", thrown != null, thrown);
            }
            Util.TryDeleteDir(work);
        }

        static void ReplaceChecks(string work)
        {
            string target = Path.Combine(work, "app.exe"), fresh = target + ".new", old = target + ".old";
            byte[] was = { 1, 2, 3 }, now = { 4, 5, 6, 7 };
            Action prepare = delegate
            {
                File.WriteAllBytes(target, was);
                File.WriteAllBytes(fresh, now);
            };

            prepare();
            Installer.PutInPlace(fresh, target, 3);
            Check("새 프로그램으로 바뀐다", File.ReadAllBytes(target).SequenceEqual(now) && !File.Exists(fresh) && !File.Exists(old), null);

            prepare();
            var hold = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
            var release = new Thread(delegate () { Thread.Sleep(700); hold.Dispose(); }) { IsBackground = true };
            release.Start();
            Installer.PutInPlace(fresh, target, 20);
            release.Join();
            Check("쓰던 파일을 놓을 때까지 기다렸다 바꾼다", File.ReadAllBytes(target).SequenceEqual(now) && !File.Exists(old), null);

            prepare();
            bool threw = false;
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { Installer.PutInPlace(fresh, target, 2); }
                catch (IOException) { threw = true; }
            }
            Check("끝까지 안 놓으면 쓰던 프로그램이 그대로 남는다", threw && File.ReadAllBytes(target).SequenceEqual(was) && !File.Exists(fresh), null);

            prepare();
            threw = false;
            using (new FileStream(fresh, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { Installer.PutInPlace(fresh, target, 2); }
                catch (IOException) { threw = true; }
            }
            Check("새 파일을 못 놓으면 쓰던 프로그램으로 되돌린다", threw && File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(was), null);
            foreach (var f in new[] { target, fresh, old })
            {
                if (File.Exists(f)) File.Delete(f);
            }
        }

        static void CompareChecks()
        {
            Cli.Print("[전후 비교]");
            string pic = P("scene.jpg"), pdf = P("scan.pdf");
            if (File.Exists(pic) && File.Exists(pdf))
            {
                string job = P("comparejob");
                Directory.CreateDirectory(job);
                string a = Path.Combine(job, "a.jpg"), b = Path.Combine(job, "b.pdf");
                File.Copy(pic, a, true);
                File.Copy(pdf, b, true);
                var real = RunJob("shrink", "medium", new List<string> { a, b }, null);
                var realPairs = CompareWindow.PairsFor(real);
                // 스캔 PDF는 더 줄일 게 없어 결과가 안 생긴다: 만들어진 사진만 짝이 된다
                Check("일부만 줄어도 만들어진 것끼리 짝을 만든다", real.Outputs.Count == 1 && realPairs != null && realPairs.Count == 1
                    && File.Exists(realPairs[0].After) && realPairs[0].Before == a,
                    string.Join(", ", real.Outputs.Select(Path.GetFileName)));
                var numbered = RunJob("pdftool", "number", new List<string> { b }, null);
                var numberedPairs = CompareWindow.PairsFor(numbered);
                Check("실제 쪽 번호 결과로 짝을 만든다", numberedPairs != null && numberedPairs.Count == 1 && File.Exists(numberedPairs[0].After), null);
                Util.TryDeleteDir(job);
            }
            var shrink = ActionOf("shrink");
            var j = new Job(shrink, new ActionOption("medium", "medium", ""), new List<string> { "a.jpg", "b.pdf" });
            j.Output("a_작게.jpg");
            j.Output("b_작게.pdf");
            var pairs = CompareWindow.PairsFor(j);
            Check("용량 줄이기는 비교할 수 있다", pairs != null && pairs.Count == 2 && pairs[1].After == "b_작게.pdf", null);
            var docs = new Job(shrink, new ActionOption("medium", "medium", ""), new List<string> { "a.docx" });
            docs.Output("a_작게.docx");
            Check("미리 볼 수 없는 문서는 뺀다", CompareWindow.PairsFor(docs) == null, null);
            var bad = new Job(shrink, new ActionOption("medium", "medium", ""), new List<string> { "a.jpg", "b.jpg" });
            bad.Output("a_작게.jpg");
            bad.Problem("b.jpg", "못 읽음");
            Check("실패한 파일이 있으면 짝을 맞추지 않는다", CompareWindow.PairsFor(bad) == null, null);
            var other = new Job(ActionOf("image"), new ActionOption("png", "png", ""), new List<string> { "a.jpg" });
            other.Output("a.png");
            Check("형식만 바꾼 일은 비교하지 않는다", CompareWindow.PairsFor(other) == null, null);
            var near = new Job(shrink, new ActionOption("medium", "medium", ""), new List<string> { "a.jpg", "a_b.jpg", "c.jpg" });
            near.Output("a_b_압축.jpg");
            var nearPairs = CompareWindow.PairsFor(near);
            Check("비슷한 이름은 가장 길게 맞는 원본과 짝을 맞춘다", nearPairs != null && nearPairs.Count == 1 && nearPairs[0].Before == "a_b.jpg", null);
            var same = new Job(shrink, new ActionOption("medium", "medium", ""), new List<string> { @"가\a.jpg", @"나\a.jpg", "c.jpg" });
            same.Output("a_압축.jpg");
            Check("어느 원본인지 모르면 짝을 만들지 않는다", CompareWindow.PairsFor(same) == null, null);
            var pt = new Job(ActionOf("pdftool"), new ActionOption("watermark", "watermark", ""), new List<string> { "a.pdf" });
            pt.Output("a_워터마크.pdf");
            Check("워터마크는 비교할 수 있다", CompareWindow.PairsFor(pt) != null, null);
            CompareWindowChecks();
        }

        // 창을 띄운 채로 화면 스레드의 할 일을 처리한다
        static void Pump(Func<bool> until, int ms)
        {
            var clock = Stopwatch.StartNew();
            while (!until() && clock.ElapsedMilliseconds < ms)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(15);
            }
        }

        static void Wheel(CompareWindow w, int delta)
        {
            w.LeftHost.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent });
        }

        static void CompareWindowChecks()
        {
            string before = P("scene.jpg"), after = P("scan.pdf");
            if (!File.Exists(before) || !File.Exists(after)) return;
            // 실제 비교 창은 작업 스레드에서 불러와 화면 스레드에 넘긴다
            foreach (var f in new[] { before, after })
            {
                string file = f;
                CompareWindow.Shot shot = null;
                var done = new ManualResetEvent(false);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    shot = CompareWindow.Read(file, 0);
                    done.Set();
                });
                bool finished = done.WaitOne(30000);
                Check("작업 스레드에서 불러오기 " + Path.GetExtension(file), finished && shot.Picture != null && shot.Picture.IsFrozen, shot == null ? "시간 초과" : shot.Problem);
            }
            var w = CompareWindow.ForTest(new List<ComparePair> { new ComparePair { Before = before, After = after } }, Theme.Green);
            w.ShowInTaskbar = false;
            w.ShowActivated = false;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -20000;
            w.Top = -20000;
            w.Show();
            Pump(delegate { return w.PicturesReady; }, 30000);
            Check("실제 창에 두 그림이 뜬다", w.PicturesReady, null);
            Pump(delegate { return false; }, 300);
            Check("두 칸이 같이 움직인다", w.MovesTogether, null);
            Wheel(w, 120);
            Check("휠을 올리면 1.25배", Math.Abs(w.CurrentZoom - 1.25) < 0.001, w.CurrentZoom.ToString("0.00"));
            for (int i = 0; i < 30; i++) Wheel(w, 120);
            Check("12배에서 멈춘다", Math.Abs(w.CurrentZoom - 12) < 0.001, w.CurrentZoom.ToString("0.00"));
            for (int i = 0; i < 40; i++) Wheel(w, -120);
            Check("처음 크기 아래로 작아지지 않고 제자리로", Math.Abs(w.CurrentZoom - 1) < 0.001 && Math.Abs(w.CurrentPan.X) < 0.5 && Math.Abs(w.CurrentPan.Y) < 0.5,
                w.CurrentZoom.ToString("0.00") + " / " + w.CurrentPan);
            w.Close();
        }

        static void SlotUiChecks()
        {
            SettingsWindow.Demo = true;
            var s = AppSettings.Current;
            s.HiddenSlots = new List<string>();
            s.SlotOrder = new List<string>();
            s.AutoOrder = false;
            var w = new SettingsWindow();
            var first = w.SlotIdsForTest();
            w.MoveSlot(0, 1);
            var second = w.SlotIdsForTest();
            Check("화살표로 칸 차례가 바뀐다", second[0] == first[1] && second[1] == first[0] && MenuAction.Ordered()[0].Id == first[1], string.Join(",", second));
            w.MoveSlot(0, -1);
            w.MoveSlot(second.Count - 1, 1);
            Check("맨 앞·맨 뒤에서는 더 가지 않는다", w.SlotIdsForTest().SequenceEqual(second), null);
            var boxes = w.SlotSwitchesForTest();
            foreach (var box in boxes)
            {
                box.IsChecked = false;
                box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, box));
            }
            Pump(delegate { return false; }, 200);
            Check("칸을 하나도 안 남게 끌 수는 없다", s.HiddenSlots.Count == boxes.Count - 1 && s.HiddenSlots.All(id => id != second[second.Count - 1]), s.HiddenSlots.Count + "/" + boxes.Count);
            Check("끄지 못한 칸의 스위치는 켜진 채로 돌아온다", w.SlotSwitchesForTest().Last().IsChecked == true, null);
            var menu = MenuAction.Ordered();
            Check("메뉴에는 남은 칸과 설정만 나온다", menu.Count == 2 && menu[0].Id == second[second.Count - 1] && menu[1].IsSettings, string.Join(",", menu.Select(a => a.Id)));
            w.Close();
            s.HiddenSlots = new List<string>();
            s.SlotOrder = new List<string>();
            SettingsWindow.Demo = false;
        }

        static void SlotChecks()
        {
            Cli.Print("[메뉴 칸]");
            SlotUiChecks();
            var s = AppSettings.Current;
            s.HiddenSlots = new List<string> { "photo" };
            s.SlotOrder = new List<string> { "ai", "pdf" };
            s.AutoOrder = false;
            var list = MenuAction.Ordered();
            Check("숨긴 칸은 빠진다", list.All(a => a.Id != "photo"), string.Join(",", list.Select(a => a.Id)));
            Check("고른 차례대로", list[0].Id == "ai" && list[1].Id == "pdf", list[0].Id + "," + list[1].Id);
            Check("설정 칸은 늘 맨 끝", list[list.Count - 1].IsSettings, null);
            s.AutoOrder = true;
            s.SlotUsage = new Dictionary<string, int> { { "shrink", 9 }, { "text", 2 } };
            list = MenuAction.Ordered();
            Check("자주 쓰는 칸을 앞으로", list[0].Id == "shrink" && list[1].Id == "text", list[0].Id + "," + list[1].Id);
            Check("숨긴 칸은 자주 써도 안 나온다", list.All(a => a.Id != "photo"), null);
            s.HiddenSlots = new List<string>();
            s.SlotOrder = new List<string>();
            s.SlotUsage = new Dictionary<string, int>();
            s.AutoOrder = false;
        }
    }
}
