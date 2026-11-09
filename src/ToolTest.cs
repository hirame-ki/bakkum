// 도구 시험(인터넷·진짜 문서 안 씀): Bakkum.exe /selftest-tools 시험폴더 [photo|pdf|doc]
// 시험폴더에 사람이 만든 예시 파일을 넣어 두면, 그 폴더 안에 결과를 만들고 하나씩 확인한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    static partial class ToolTest
    {
        static int passed, failed;
        static string dir;

        public static int Run(string[] args)
        {
            Cli.Active = true;
            if (args.Length < 2 || !Directory.Exists(args[1]))
            {
                Cli.Print("사용: /selftest-tools 시험폴더 [photo|pdf|doc|format]");
                return 2;
            }
            dir = Path.GetFullPath(args[1]);
            string what = args.Length > 2 ? args[2].ToLowerInvariant() : "all";
            // AI 시험은 설정을 임시 폴더에 두어 선생님의 진짜 설정을 건드리지 않는다.
            if (what == "ai" || what == "aihwp" || what == "update" || what == "record") AppInfo.DataDirOverride = Path.Combine(Path.GetTempPath(), "bakkum-aitest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Install(app);
            try
            {
                if (what == "all" || what == "photo") PhotoTests();
                if (what == "all" || what == "pdf") PdfTests();
                if (what == "all" || what == "doc") DocTests();
                if (what == "all" || what == "format") FormatTests();
                if (what == "all" || what == "media") MediaTests();
                if (what == "record") RecordTests();
                if (what == "hancom") HancomTests();
                if (what == "ai") AiTests(false);
                if (what == "aihwp") AiTests(true);
                if (what == "update")
                {
                    UpdateTests();
                    CompareChecks();
                    SlotChecks();
                }
            }
            catch (Exception ex)
            {
                Check("시험이 끝까지 돈다", false, ex.GetType().Name + ": " + ex.Message + " @ " + ex.TargetSite);
            }
            Cli.Print("통과 " + passed + " · 실패 " + failed);
            return failed == 0 ? 0 : 1;
        }

        static void Check(string name, bool ok, string detail)
        {
            if (ok) passed++;
            else failed++;
            Cli.Print((ok ? "  ok   " : "  FAIL ") + name + (string.IsNullOrEmpty(detail) ? "" : "  — " + detail));
        }

        static string P(string name)
        {
            return Path.Combine(dir, name);
        }

        static MenuAction ActionOf(string id)
        {
            return MenuAction.All.First(a => a.Id == id);
        }

        static Job RunJob(string action, string option, IList<string> files, object payload, params string[] prms)
        {
            var job = new Job(ActionOf(action), new ActionOption(option, option, ""), files.ToList()) { Headless = true, AutoYes = true, Payload = payload };
            foreach (var p in prms)
            {
                int eq = p.IndexOf('=');
                job.Params[p.Substring(0, eq)] = p.Substring(eq + 1);
            }
            Jobs.RunNow(job);
            foreach (var x in job.Problems) Cli.Print("       문제: " + x);
            foreach (var x in job.Notes) Cli.Print("       참고: " + x);
            return job;
        }

        // ── 사진 ──

        static byte[] Bgra32(BitmapSource s)
        {
            var b = Bgra.From(s);
            return b.P;
        }

        static void PhotoTests()
        {
            Cli.Print("[사진]");
            // WEBP: 손실 없이 저장되는지(윈도우 코덱이 있으면 읽어서 칸마다 비교, 없으면 파일만 만들고 파이썬에서 비교)
            foreach (var name in new[] { "graphic.png", "clear.png", "scene.jpg" })
            {
                var src = Imaging.Load(P(name));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                byte[] webp = PhotoRender.Encode(src, "webp", 0);
                sw.Stop();
                string outPath = P(Path.GetFileNameWithoutExtension(name) + "_t.webp");
                File.WriteAllBytes(outPath, webp);
                Check("WEBP 쓰기 " + name, webp.Length > 16 && webp[0] == 'R' && webp[8] == 'W', Conversions.Size(webp.Length) + " · " + sw.ElapsedMilliseconds + "ms");
            }
            // ICO
            byte[] ico = PhotoRender.Encode(Imaging.Load(P("clear.png")), "ico", 0);
            File.WriteAllBytes(P("clear_t.ico"), ico);
            var icoDec = BitmapDecoder.Create(new MemoryStream(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check("ICO 여러 크기", icoDec.Frames.Count >= 5, icoDec.Frames.Count + "개");

            // 가리기: 고른 곳 바깥은 그대로이고, 안쪽은 알아볼 수 없게 달라져야 한다
            var scene = Imaging.Load(P("scene.jpg"));
            var plain = new PhotoEdit();
            var blur = new PhotoEdit();
            var rect = new Rect(0.11, 0.58, 0.32, 0.22);
            blur.Marks.Add(new PhotoMark { Kind = MarkKind.Redact, Mode = "blur", Strength = 0.8, X = rect.X, Y = rect.Y, W = rect.Width, H = rect.Height });
            var a = Bgra.From(PhotoRender.Render(scene, plain));
            var b = Bgra.From(PhotoRender.Render(scene, blur));
            long outsideDiff = 0, inside = 0, insideDiff = 0;
            int x0 = (int)(rect.X * a.W), x1 = (int)(rect.Right * a.W), y0 = (int)(rect.Y * a.H), y1 = (int)(rect.Bottom * a.H);
            for (int y = 0; y < a.H; y++)
            {
                for (int x = 0; x < a.W; x++)
                {
                    int i = (y * a.W + x) * 4;
                    int d = Math.Abs(a.P[i] - b.P[i]) + Math.Abs(a.P[i + 1] - b.P[i + 1]) + Math.Abs(a.P[i + 2] - b.P[i + 2]);
                    if (x < x0 - 1 || x > x1 + 1 || y < y0 - 1 || y > y1 + 1) outsideDiff += d;
                    else if (x > x0 + 2 && x < x1 - 2 && y > y0 + 2 && y < y1 - 2)
                    {
                        inside++;
                        insideDiff += d;
                    }
                }
            }
            Check("블러: 바깥은 그대로", outsideDiff == 0, "바깥 차이 " + outsideDiff);
            Check("블러: 안쪽은 바뀜", insideDiff / Math.Max(1, inside) >= 3, "칸당 평균 차이 " + (insideDiff / Math.Max(1, inside)));
            var whole = new PhotoEdit();
            whole.Marks.Add(new PhotoMark { Kind = MarkKind.Redact, Mode = "mosaic", Strength = 0.7, Whole = true });
            var m = Bgra.From(PhotoRender.Render(scene, whole));
            Check("모자이크: 전체", m.W == a.W && m.H == a.H, m.W + "×" + m.H);

            // 저장 작업: 원본 크기로 손질해서 새 파일로
            string work = P("job");
            Directory.CreateDirectory(work);
            foreach (var f in Directory.GetFiles(work)) File.Delete(f);
            string c1 = Path.Combine(work, "photo.jpg");
            File.Copy(P("scene.jpg"), c1);
            string c2 = Path.Combine(work, "clear.png");
            File.Copy(P("clear.png"), c2);
            var e1 = new PhotoEdit();
            e1.Marks.Add(new PhotoMark { Kind = MarkKind.Redact, Mode = "fill", Rgb = 0x22252F, X = rect.X, Y = rect.Y, W = rect.Width, H = rect.Height });
            e1.Marks.Add(new PhotoMark { Kind = MarkKind.Text, Text = "글자 시험", Size = 0.08, Rgb = 0xE5483B, Bold = true, X = 0.5, Y = 0.2 });
            e1.Marks.Add(new PhotoMark { Kind = MarkKind.Arrow, Size = 0.012, Rgb = 0x2F7BF0, X = 0.1, Y = 0.1, W = 0.3, H = 0.2 });
            e1.RotateRight();
            e1.CropTo(new Rect(0.1, 0.1, 0.8, 0.8), 1600.0 / 2400.0);
            e1.Brightness = 0.1;
            var e2 = new PhotoEdit { Margin = 0.1, Corner = 0.1, BackTransparent = true };
            var save = new PhotoSave { Format = "same" };
            save.Items.Add(new PhotoSaveItem { Path = c1, Edit = e1 });
            save.Items.Add(new PhotoSaveItem { Path = c2, Edit = e2 });
            var job = RunJob("photo", "save", new[] { c1, c2 }, save);
            Check("저장 작업: 파일 2개", job.Outputs.Count == 2 && job.Problems.Count == 0, string.Join(", ", job.Outputs.Select(Path.GetFileName)));
            Check("원본은 그대로", new FileInfo(c1).Length == new FileInfo(P("scene.jpg")).Length, "");
            if (job.Outputs.Count == 2)
            {
                var r1 = Imaging.Load(job.Outputs[0]);
                Check("돌리고 자른 크기", Math.Abs(r1.PixelWidth - 1600 * 0.8) < 3 && Math.Abs(r1.PixelHeight - 2400 * 0.8) < 3, r1.PixelWidth + "×" + r1.PixelHeight);
                Check("투명 바탕은 PNG로", job.Outputs[1].EndsWith(".png"), "");
            }
            foreach (var fmt in new[] { "png", "jpg", "webp" })
            {
                var s2 = new PhotoSave { Format = fmt };
                s2.Items.Add(new PhotoSaveItem { Path = c1, Edit = e1 });
                var j2 = RunJob("photo", "save", new[] { c1 }, s2);
                Check("저장 형식 " + fmt, j2.Outputs.Count == 1 && j2.Outputs[0].EndsWith("." + fmt), "");
            }

            // 정보 지우기
            var infoBefore = PhotoInfo.Read(c1);
            Check("정보 읽기: 장소가 보인다", infoBefore.Any(r => r.Label == "찍은 장소"), string.Join("; ", infoBefore.Select(r => r.Label)));
            var j3 = RunJob("photo", "strip", new[] { c1 }, null);
            Check("정보 지우기: 파일 생김", j3.Outputs.Count == 1, "");
            if (j3.Outputs.Count == 1)
            {
                var infoAfter = PhotoInfo.Read(j3.Outputs[0]);
                Check("정보 지우기: 민감한 줄 없음", !PhotoInfo.HasSensitive(infoAfter), string.Join("; ", infoAfter.Select(r => r.Label)));
                Check("정보 지우기: 그림은 칸까지 같음", Bgra32(Imaging.Load(c1)).SequenceEqual(Bgra32(Imaging.Load(j3.Outputs[0]))), "");
            }

            // 문서 펴기
            var paper = Imaging.Load(P("paper.jpg"));
            int pw, ph;
            var paperProxy = PhotoRender.Proxy(P("paper.jpg"), out pw, out ph);
            var quad = PaperFinder.Find(paperProxy);
            var want = new[] { new Point(420.0 / 2200, 300.0 / 2600), new Point(1830.0 / 2200, 430.0 / 2600), new Point(1950.0 / 2200, 2300.0 / 2600), new Point(260.0 / 2200, 2240.0 / 2600) };
            bool close = quad != null;
            string q = "";
            if (quad != null)
            {
                for (int i = 0; i < 4; i++)
                {
                    double err = Math.Max(Math.Abs(quad[i].X - want[i].X), Math.Abs(quad[i].Y - want[i].Y));
                    q += err.ToString("0.000") + " ";
                    if (err > 0.015) close = false;
                }
            }
            Check("종이 모서리 찾기", close, q);
            if (quad != null)
            {
                var e3 = new PhotoEdit { Quad = quad, Filter = "clean" };
                var flat = PhotoRender.Render(paper, e3);
                File.WriteAllBytes(P("paper_flat.png"), PhotoRender.Encode(flat, "png", 0));
                Check("펴기 결과 비율(세로가 김)", flat.PixelHeight > flat.PixelWidth, flat.PixelWidth + "×" + flat.PixelHeight);
            }

            // 콜라주
            var names = new[] { "c1.jpg", "c2.jpg", "c3.jpg", "c4.jpg", "c5.jpg" }.Select(n => P(n)).ToList();
            var cs = new CollageSave { Order = names.ToList(), Spec = new CollageSpec { LongSide = 1600 }, Format = "png" };
            var j4 = RunJob("photo", "collage", names, cs);
            Check("콜라주", j4.Outputs.Count == 1, j4.Outputs.Count == 1 ? Path.GetFileName(j4.Outputs[0]) : "");
            if (j4.Outputs.Count == 1)
            {
                File.Copy(j4.Outputs[0], P("collage_result.png"), true);
                File.Delete(j4.Outputs[0]);
            }
        }
    }
}
