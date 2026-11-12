// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 도구 시험 중 영상 편집 부분: /selftest-tools 시험폴더 video
// 사람 얼굴·목소리·학생 자료는 쓰지 않는다. 시험이 직접 만든 무늬 그림·삐 소리·마우스 기록으로 영상을 만들어 확인한다.
// 자동 저장·마우스 기록·내 꾸밈·수업 자료 쪽 그림은 임시 자료 폴더(AppInfo.DataDirOverride)에 쓴다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    static partial class ToolTest
    {
        static string V(string name)
        {
            return Path.Combine(P("videojob"), name);
        }

        static void VideoTests()
        {
            Cli.Print("[영상 편집]");
            if (string.IsNullOrEmpty(AppInfo.DataDirOverride))
            {
                Check("자료 폴더를 임시로 바꾸고 시험한다", false, "선생님의 진짜 자료 폴더를 건드리지 않으려고 멈췄어요.");
                return;
            }
            string w = P("videojob");
            if (Directory.Exists(w)) Directory.Delete(w, true);
            Directory.CreateDirectory(w);
            VWaveOut.Silent = true;
            Check("자동 저장·마우스 기록은 임시 폴더에", VAutoSave.Folder.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), VAutoSave.Folder);

            // ── 재료: 무늬 그림 두 장, 삐 소리(쉼 1.2초·0.5초), 화음 음악 ──
            string bg = V("바탕.png"), tex = V("무늬.png"), beeps = V("삐소리.wav"), music = V("음악.wav");
            SavePng(bg, 640, 360, BlockNoise(640, 360, 8, 11));
            SavePng(tex, 96, 96, RandomCells(96, 96, 8, 7));
            var rnd = new Random(5);
            WriteWav(beeps, 5.0, delegate (double t)
            {
                bool tone = t < 1.0 || (t >= 2.2 && t < 3.2) || t >= 3.7;
                return tone ? 0.3 * Math.Sin(2 * Math.PI * 440 * t) : 0.001 * (rnd.NextDouble() * 2 - 1);
            });
            WriteWav(music, 3.0, delegate (double t)
            {
                return 0.12 * (Math.Sin(2 * Math.PI * 262 * t) + Math.Sin(2 * Math.PI * 330 * t) + Math.Sin(2 * Math.PI * 392 * t)) / 3;
            });

            // ── 1. 무늬가 움직이는 영상을 내보내기로 만든다(4초 동안 보였다가 사라진다) ──
            var mp = new VProject { Aspect = "16:9", SourceW = 640, SourceH = 360, Fps = 30, Duck = false };
            mp.Clips.Add(new VClip { Kind = "photo", Path = bg, Dur = 5, Ken = "none", Fit = "fill" });
            var mover = new VItem { Kind = "image", Path = tex, Start = 0, End = 4, X = 0.25, Y = 0.45, W = 0.15, In = "none", Out = "none" };
            mover.Keys.Add(new VKey { T = 0, X = 0.25, Y = 0.45 });
            mover.Keys.Add(new VKey { T = 4, X = 0.75, Y = 0.55 });
            mp.Items.Add(mover);
            mp.Sounds.Add(new VSound { Kind = "file", Path = beeps, Start = 0, In = 0, Out = 5 });
            string moving = V("움직임.mp4");
            var sw = Stopwatch.StartNew();
            var mk = ExportNow(mp, PlanFor(moving, 640, 360, 0, 5), null);
            var pm = Probe(moving);
            Check("내보내기: 사진 + 움직이는 그림 + 소리로 MP4", pm != null && pm.HasVideo && pm.HasAudio && pm.Width == 640 && pm.Height == 360 && Near(pm.Seconds, 5, 0.15) && mk.Outputs.Count == 1, Say(pm) + " · " + sw.ElapsedMilliseconds + "ms " + string.Join(" / ", mk.Problems));
            if (pm == null) return;
            BrokenFiles();

            // 장면 꺼내기: 앞뒤로 건너뛰어도 그 시각의 장면, 얹은 그림은 계산한 자리에
            string seekNote = "", placeNote = "";
            bool seekOk = false, placeOk = false;
            WinRtCall.Mta(delegate
            {
                using (var r = new VideoReader(moving, 640, 640, true))
                {
                    double worst = 0;
                    bool all = true;
                    foreach (double t in new[] { 2.5, 1.0, 3.5, 0.5, 4.9, 2.0 })
                    {
                        double ft;
                        var px = r.At(t, out ft);
                        if (px == null)
                        {
                            all = false;
                            continue;
                        }
                        worst = Math.Max(worst, Math.Abs(ft - t));
                        if (t == 2.0)
                        {
                            var st = VAnim.Item(mover, 2.0);
                            double cx, cy, bw, bh;
                            CheckerBox(px, r.Width, r.Height, out cx, out cy, out bw, out bh);
                            placeOk = Near(cx, st.X, 0.006) && Near(cy, st.Y, 0.01) && Near(bw, 0.15, 0.01) && !HasChecker(px, r.Width, r.Height, 0.9, 0.12, 14);
                            placeNote = "무늬 가운데 " + cx.ToString("0.000") + "," + cy.ToString("0.000") + " (계산 " + st.X.ToString("0.000") + "," + st.Y.ToString("0.000") + ") 너비 " + bw.ToString("0.000");
                        }
                    }
                    seekOk = all && worst <= 0.05;
                    seekNote = "가장 크게 어긋난 시각 " + worst.ToString("0.000") + "초";
                }
                return 0;
            });
            Check("장면 꺼내기: 앞뒤로 건너뛰어도 정확", seekOk, seekNote);
            Check("얹은 그림이 움직임 표시대로 그 자리에", placeOk, placeNote);

            // ── 2. 조용한 곳 찾기·자르기 ──
            var track = AudioCache.Wait(moving, null);
            Check("소리 풀기: 48kHz, 길이 그대로", track.Ready && Near(track.Seconds, 5, 0.12), track.Ready ? track.Seconds.ToString("0.00") + "초" : (track.Error ?? "실패"));
            var gaps = track.Ready ? VSilence.Find(track, 0, track.Seconds, 0, 0.7, 0.15) : new List<VSilence.Gap>();
            Check("조용한 곳: 1.2초 쉼만 찾고(0.5초 쉼은 빼고) 앞뒤 0.15초 남김", gaps.Count == 1 && Near(gaps[0].Start, 1.15, 0.07) && Near(gaps[0].End, 2.05, 0.07), GapText(gaps));
            var gaps2 = track.Ready ? VSilence.Find(track, 0, track.Seconds, 0, 0.4, 0) : new List<VSilence.Gap>();
            Check("조용한 곳: 0.4초 넘는 쉼은 둘 다", gaps2.Count == 2 && Near(gaps2[0].Start, 1.0, 0.07) && Near(gaps2[0].End, 2.2, 0.07) && Near(gaps2[1].Start, 3.2, 0.07) && Near(gaps2[1].End, 3.7, 0.07), GapText(gaps2));
            if (gaps.Count == 1)
            {
                foreach (double speed in new[] { 1.0, 2.0 })
                {
                    var sp = new VProject();
                    var sc = new VClip { Path = moving, In = 0, Out = 5, Speed = speed };
                    sp.Clips.Add(sc);
                    var after = new VItem { Kind = "text", Subtitle = true, Text = "쉼 뒤 자막", Start = 3.0 / speed, End = 3.5 / speed };
                    sp.Items.Add(after);
                    sp.Markers.Add(new VMarker { T = 4.0 / speed, Label = "정리" });
                    double before = sp.Duration;
                    // 화면의 [조용한 곳 자르기]와 같은 계산: 원본 시간 → 시간 막대, 뒤에서부터 자른다
                    var marks = gaps.Select(g => new[] { sp.StartOf(sc) + (g.Start - sc.In) / sc.Speed, sp.StartOf(sc) + (g.End - sc.In) / sc.Speed }).OrderByDescending(g => g[0]).ToList();
                    double cutLen = marks.Sum(g => g[1] - g[0]);
                    var f = sp.Follow();
                    foreach (var g in marks) sp.Cut(g[0], g[1]);
                    f();
                    bool ok = Near(sp.Duration, before - cutLen, 0.01) && sp.Clips.Count == 2 && Near(after.Start, 3.0 / speed - cutLen, 0.01) && Near(sp.Markers[0].T, 4.0 / speed - cutLen, 0.01);
                    Check("조용한 곳 자르기(빠르기 " + speed + "배): 길이가 줄고 뒤 자막·표시가 따라온다", ok, VProject.Clock(before, true) + " → " + VProject.Clock(sp.Duration, true) + ", 자막 " + after.Start.ToString("0.00") + ", 표시 " + sp.Markers[0].T.ToString("0.00"));
                }
            }

            // ── 3. 가리기 상자 따라가기 ──
            double bwN = 0.12, bhN = 0.12 * 640 / 360.0;
            var s0 = VAnim.Item(mover, 0.5);
            var box = new Rect(s0.X - bwN / 2, s0.Y - bhN / 2, bwN, bhN);
            var keysA = new List<VKey>();
            var keysB = new List<VKey>();
            sw.Restart();
            double lostA = WinRtCall.Mta(delegate { return VTrack.Follow(moving, 0.5, 3.8, box, keysA, delegate (double x) { }, () => false); });
            long msA = sw.ElapsedMilliseconds;
            double lostB = WinRtCall.Mta(delegate { return VTrack.Follow(moving, 0.5, 4.8, box, keysB, delegate (double x) { }, () => false); });
            string whereA;
            double worstA = TrackError(keysA, mover, out whereA);
            Check("따라가기: 움직이는 무늬를 끝까지(어긋남 화면의 2% 안)", lostA < 0 && keysA.Count >= 15 && worstA < 0.02 && keysA[keysA.Count - 1].T > 3.6, "표시 " + keysA.Count + "개 · 가장 크게 어긋남 " + worstA.ToString("0.0000") + " " + whereA + " · " + msA + "ms");
            string whereB;
            double worstB = TrackError(keysB, mover, out whereB);
            Check("따라가기: 무늬가 사라지면 놓친 때를 알려 준다", lostB >= 3.95 && lostB <= 4.3 && keysB.Count >= 15 && worstB < 0.02, "놓친 때 " + lostB.ToString("0.00") + "초 · 마지막 표시 " + (keysB.Count > 0 ? keysB[keysB.Count - 1].T.ToString("0.00") : "-") + " · 어긋남 " + worstB.ToString("0.0000") + " " + whereB);

            // ── 4. 얼굴 찾기(사람 얼굴 없이 확인할 수 있는 것만) ──
            var noFaces = new List<VRedact>();
            string faceErr = null;
            sw.Restart();
            try
            {
                WinRtCall.Mta(delegate
                {
                    VFaces.Scan(moving, 0, 5, 0, new FaceFinder.Session(), new VRedact(), noFaces, delegate (double x) { }, () => false);
                    return 0;
                });
            }
            catch (Exception ex)
            {
                faceErr = ex.GetType().Name + ": " + ex.Message;
            }
            Check("얼굴 찾기: 얼굴 없는 무늬 영상에서는 0개(엉뚱한 곳을 가리지 않음)", faceErr == null && noFaces.Count == 0, faceErr ?? (noFaces.Count + "개 · " + sw.ElapsedMilliseconds + "ms"));
            FaceMath();

            // ── 5. 가리기가 그림에 들어가는지(보이게 둔 얼굴은 빼고, 움직임 표시를 따라) ──
            RedactRender(moving);

            // ── 6. 화면 녹화 다듬기: 마우스 기록 → 누른 곳 확대·마우스 강조·누른 자리 ──
            CursorPolish(moving, beeps);

            // ── 7. 제목 화면·유튜브 챕터·내 꾸밈·자막 파일 ──
            TitleCards();
            Chapters();
            Styles();
            SrtFiles();

            // ── 8. 수업 자료(PDF) → 쪽 그림, PPT 발표자 노트 읽기 ──
            Lecture();
            LectureNotes();

            // 긴 영상 미리 보기 사본(12초 영상으로 가볍게)
            ProxyTests(false);

            // ── 9. 저장·되돌리기·자동 저장 정리 ──
            SaveAndTidy(moving);

            // ── 10. 내보내기 전 점검 ──
            ExportChecks(moving);

            // ── 11. 내보내기: 구간만·소리만·표지·멈추기·모든 꾸밈 함께 ──
            ExportKinds(moving, bg, tex, music);

            foreach (var f in new[] { moving, beeps, music }) AudioCache.Forget(f);
        }

        // ── 재료 만들기 ──

        static void SavePng(string path, int w, int h, byte[] bgra)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        /// 칸 덩어리마다 다른 중간 밝기(아주 어둡거나 밝은 칸은 없다)
        static byte[] BlockNoise(int w, int h, int block, int seed)
        {
            var rnd = new Random(seed);
            int bw = (w + block - 1) / block, bh = (h + block - 1) / block;
            var v = new byte[bw * bh * 3];
            rnd.NextBytes(v);
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int b = ((y / block) * bw + x / block) * 3;
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(60 + v[b] / 3);
                    px[o + 1] = (byte)(60 + v[b + 1] / 3);
                    px[o + 2] = (byte)(60 + v[b + 2] / 3);
                    px[o + 3] = 255;
                }
            return px;
        }

        /// 검정·흰 칸을 제멋대로 늘어놓은 무늬. 바둑판처럼 되풀이되는 무늬는 한 칸 어긋나도 똑같아 보여서
        /// 따라가기가 옆 칸에 붙어도 맞는 줄 안다(2026-11-12 첫 시험에서 대각선으로 한 칸 어긋남). 그래서 되풀이 없는 무늬로 시험한다.
        static byte[] RandomCells(int w, int h, int cell, int seed)
        {
            var rnd = new Random(seed);
            int cw = (w + cell - 1) / cell, ch = (h + cell - 1) / cell;
            var dark = new bool[cw * ch];
            for (int i = 0; i < dark.Length; i++) dark[i] = rnd.Next(2) == 0;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    byte v = dark[(y / cell) * cw + x / cell] ? (byte)20 : (byte)235;
                    px[o] = v;
                    px[o + 1] = v;
                    px[o + 2] = v;
                    px[o + 3] = 255;
                }
            return px;
        }

        /// 48kHz 두 줄 16비트 WAV
        static void WriteWav(string path, double seconds, Func<double, double> wave)
        {
            int n = (int)Math.Round(seconds * 48000);
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + n * 4);
                bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                bw.Write(16);
                bw.Write((short)1);
                bw.Write((short)2);
                bw.Write(48000);
                bw.Write(48000 * 4);
                bw.Write((short)4);
                bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(n * 4);
                for (int i = 0; i < n; i++)
                {
                    double v = Math.Max(-1, Math.Min(1, wave(i / 48000.0)));
                    short s = (short)Math.Round(v * 32000);
                    bw.Write(s);
                    bw.Write(s);
                }
            }
        }

        static VExportPlan PlanFor(string output, int w, int h, double from, double to)
        {
            return new VExportPlan { Kind = "video", W = w, H = h, Fps = 30, VideoBps = 2500000, AudioBytesPerSec = 24000, From = from, To = to, Output = output, Label = "시험" };
        }

        /// 영상 편집 내보내기를 이 자리에서 돌린다(알림 창 없이). 문제는 job.Problems에.
        static Job ExportNow(VProject p, VExportPlan plan, Action<Job> during)
        {
            var job = new Job(ActionOf("media"), new ActionOption("vexport", "영상 편집 내보내기", ""), new List<string>()) { Payload = new VExportPayload { Project = p, Plan = plan } };
            if (during != null) during(job);
            try
            {
                VideoExport.Run(job);
            }
            catch (OperationCanceledException)
            {
                job.Cancelled = true;
            }
            catch (UserFacingException ex)
            {
                job.Problem(null, ex.Message);
            }
            catch (Exception ex)
            {
                job.Problem(null, ex.GetType().Name + ": " + ex.Message + " @ " + ex.TargetSite);
            }
            finally
            {
                job.ReleaseResources();
            }
            return job;
        }

        /// 둘레에 아주 어두운 칸과 아주 밝은 칸이 다 있으면(체크 무늬) 참
        static bool HasChecker(byte[] bgra, int w, int h, double nx, double ny, int half)
        {
            int cx = (int)Math.Round(nx * w), cy = (int)Math.Round(ny * h);
            int dark = 0, bright = 0;
            for (int y = Math.Max(0, cy - half); y < Math.Min(h, cy + half); y++)
                for (int x = Math.Max(0, cx - half); x < Math.Min(w, cx + half); x++)
                {
                    int o = (y * w + x) * 4;
                    int l = (bgra[o] + bgra[o + 1] + bgra[o + 2]) / 3;
                    if (l < 50) dark++;
                    if (l > 200) bright++;
                }
            return dark > 20 && bright > 20;
        }

        static bool IsColor(byte[] bgra, int w, double nx, double ny, uint rgb)
        {
            int x = (int)Math.Round(nx * w), y = (int)Math.Round(ny * (bgra.Length / 4 / w));
            int o = (y * w + x) * 4;
            int r = (int)(rgb >> 16) & 255, g = (int)(rgb >> 8) & 255, b = (int)rgb & 255;
            return Math.Abs(bgra[o + 2] - r) < 24 && Math.Abs(bgra[o + 1] - g) < 24 && Math.Abs(bgra[o] - b) < 24;
        }

        /// PNG 머리에서 크기만 읽는다(파일을 잡아 두지 않게)
        static bool PngSize(string path, out int w, out int h)
        {
            w = h = 0;
            var head = new byte[24];
            using (var fs = File.OpenRead(path))
                if (fs.Read(head, 0, 24) < 24) return false;
            if (head[0] != 0x89 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G') return false;
            w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            return true;
        }

        static string GapText(List<VSilence.Gap> gaps)
        {
            return gaps.Count == 0 ? "없음" : string.Join(", ", gaps.Select(g => g.Start.ToString("0.00") + "~" + g.End.ToString("0.00")));
        }

        /// 따라간 상자 가운데와 실제 무늬 가운데의 가장 큰 차이(화면 비율)
        static double TrackError(List<VKey> keys, VItem mover, out string where)
        {
            double worst = keys.Count == 0 ? 1 : 0;
            where = "";
            foreach (var k in keys)
            {
                if (k.T >= mover.End) continue;
                var st = VAnim.Item(mover, k.T);
                double dx = k.X + k.W / 2 - st.X, dy = k.Y + k.H / 2 - st.Y;
                double e = Math.Max(Math.Abs(dx), Math.Abs(dy));
                if (e > worst)
                {
                    worst = e;
                    where = "(" + k.T.ToString("0.00") + "초 가로 " + dx.ToString("+0.000;-0.000") + " 세로 " + dy.ToString("+0.000;-0.000") + ")";
                }
            }
            return worst;
        }

        /// 아주 어둡거나 밝은 칸(체크 무늬)이 차지한 곳의 가운데와 크기(화면 비율)
        static void CheckerBox(byte[] bgra, int w, int h, out double cx, out double cy, out double bw, out double bh)
        {
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    int l = (bgra[o] + bgra[o + 1] + bgra[o + 2]) / 3;
                    if (l >= 40 && l <= 210) continue;
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y);
                }
            cx = x1 < 0 ? -1 : (x0 + x1 + 1) / 2.0 / w;
            cy = y1 < 0 ? -1 : (y0 + y1 + 1) / 2.0 / h;
            bw = x1 < 0 ? 0 : (x1 - x0 + 1) / (double)w;
            bh = y1 < 0 ? 0 : (y1 - y0 + 1) / (double)h;
        }

        /// 열 수 없는 파일은 이유를 알려 준다. 압축 방식(HEVC·AV1·VP9)을 알면 그에 맞는 확장을 안내한다.
        static void BrokenFiles()
        {
            // 안내 문구: 파일 안에 적힌 압축 방식이 확장자보다 먼저(갤럭시는 HEVC를 .mp4에 담는다)
            string hevcMp4 = VideoReader.Unsupported("수업.mp4", VideoReader.Hevc);
            string av1 = VideoReader.Unsupported("수업.mp4", VideoReader.Av1);
            string vp9 = VideoReader.Unsupported("수업.webm", VideoReader.Vp9);
            string movUnknown = VideoReader.Unsupported("수업.mov");
            string mp4Unknown = VideoReader.Unsupported("수업.mp4");
            // H.264(이 PC가 원래 푸는 방식)라고 적힌 .mov는 HEVC 탓이 아니므로 HEVC를 말하지 않는다
            string movH264 = VideoReader.Unsupported("수업.mov", new Guid("34363248-0000-0010-8000-00aa00389b71"));
            bool says = hevcMp4.Contains("HEVC 비디오 확장") && av1.Contains("AV1 비디오 확장") && vp9.Contains("VP9 비디오 확장")
                && movUnknown.Contains("HEVC") && !mp4Unknown.Contains("HEVC") && !movH264.Contains("HEVC") && !av1.Contains("HEVC");
            Check("못 여는 영상 안내: 압축 방식에 맞는 확장을 알려 준다(HEVC·AV1·VP9)", says, hevcMp4 + " / " + movH264);

            var junk = new byte[4096];
            new Random(3).NextBytes(junk);
            string mp4 = V("깨진 영상.mp4"), mov = V("깨진 영상.mov");
            File.WriteAllBytes(mp4, junk);
            File.WriteAllBytes(mov, junk);
            string m1 = null, m2 = null;
            foreach (var f in new[] { mp4, mov })
            {
                string msg = null;
                try
                {
                    WinRtCall.Mta(delegate
                    {
                        using (var r = new VideoReader(f, 640, 640, false)) { }
                        return 0;
                    });
                }
                catch (UserFacingException ex)
                {
                    msg = ex.Message;
                }
                catch (Exception ex)
                {
                    msg = "다른 오류 " + ex.GetType().Name;
                }
                if (f == mp4) m1 = msg;
                else m2 = msg;
            }
            // 망가진 파일은 압축 방식을 알기 전에 윈도우가 못 읽는다고 알린다(HEVC 확장을 깔라는 엉뚱한 안내는 하지 않는다)
            Func<string, bool> told = delegate (string m) { return m != null && (m.Contains("읽지 못해요") || m.Contains("풀지 못해요")) && !m.StartsWith("다른 오류"); };
            Check("깨진 영상: 멈추지 않고 열지 못한 이유를 알려 준다", told(m1) && told(m2), (m1 ?? "알림 없음") + " / " + (m2 ?? "알림 없음"));
            var probe = VMedia.Probe(mp4);
            Check("깨진 영상: 파일 정보 읽기는 멈추지 않고 비워 둔다", probe == null, "");
        }

        // ── 얼굴: 돌린 영상의 자리 계산, 장면마다 찾은 얼굴 잇기 ──

        static void FaceMath()
        {
            const int w = 8, h = 4, u0 = 6, v0 = 1;
            var g = new byte[w * h];
            g[v0 * w + u0] = 255;
            var bad = new List<string>();
            foreach (int rot in new[] { 0, 90, 180, 270 })
            {
                int ow, oh;
                var d = VFaces.Upright(g, w, h, rot, out ow, out oh);
                int at = Array.IndexOf(d, (byte)255);
                bool sizeOk = rot % 180 == 0 ? ow == w && oh == h : ow == h && oh == w;
                var back = VFaces.FromUpright(new Rect((at % ow) / (double)ow, (at / ow) / (double)oh, 1.0 / ow, 1.0 / oh), rot);
                var want = new Rect(u0 / (double)w, v0 / (double)h, 1.0 / w, 1.0 / h);
                if (!sizeOk || Math.Abs(back.X - want.X) > 1e-9 || Math.Abs(back.Y - want.Y) > 1e-9 || Math.Abs(back.Width - want.Width) > 1e-9 || Math.Abs(back.Height - want.Height) > 1e-9) bad.Add(rot + "도");
            }
            // 시계 방향 90도: 원본 오른쪽 위 칸은 세운 그림의 오른쪽 아래로
            int ow2, oh2;
            var corner = new byte[w * h];
            corner[w - 1] = 255;
            var c90 = VFaces.Upright(corner, w, h, 90, out ow2, out oh2);
            bool clockwise = Array.IndexOf(c90, (byte)255) == ow2 * oh2 - 1;
            Check("얼굴 찾기: 돌려 찍은 영상도 세워서 찾고 원본 자리로 되돌린다", bad.Count == 0 && clockwise, bad.Count == 0 ? "" : "어긋남: " + string.Join(", ", bad));
            Check("얼굴 찾기: 훑는 간격(5분까지 0.5초, 긴 영상은 1.5초까지)", VFaces.StepFor(60) == 0.5 && VFaces.StepFor(300) == 0.5 && Near(VFaces.StepFor(600), 1.0, 1e-9) && VFaces.StepFor(3600) == 1.5, "");

            // 장면마다 찾은 얼굴(가짜 자리): A는 오른쪽으로 걸어가고, B는 1초 동안 안 보였다가 다시, C는 한 번만
            const double step = 0.5;
            var frames = new List<KeyValuePair<double, List<Rect>>>();
            for (int i = 0; i <= 8; i++)
            {
                double t = i * step;
                var list = new List<Rect> { new Rect(0.10 + 0.025 * i, 0.2, 0.1, 0.18) };
                if (t < 1.9 || t > 2.6) list.Add(new Rect(0.6, 0.5, 0.12, 0.2));
                if (Math.Abs(t - 3.0) < 1e-9) list.Add(new Rect(0.85, 0.1, 0.05, 0.09));
                frames.Add(new KeyValuePair<double, List<Rect>>(t, list));
            }
            var proto = new VRedact { Mode = "mosaic", Strength = 0.3 };
            var made = VFaces.FromFrames(frames, 0, 4.0, step, proto);
            var a = made.FirstOrDefault(r => r.Keys.Count > 0 && r.Keys[0].X < 0.2);
            var b = made.FirstOrDefault(r => r.X > 0.5 && r.X < 0.7);
            var c = made.FirstOrDefault(r => r.X > 0.8);
            bool linked = made.Count == 3 && a != null && b != null && c != null && a.Keys.Count == 9 && b.Keys.Count == 7 && c.Keys.Count == 0;
            bool shape = made.All(r => r.Face && r.Oval && r.Mode == "mosaic" && r.Strength >= 0.6);
            bool times = a != null && Near(a.Start, 0, 1e-9) && Near(a.End, 4.0, 1e-9) && c != null && Near(c.Start, 2.4, 1e-9) && Near(c.End, 3.6, 1e-9) && Near(c.W, 0.05 * 1.12, 1e-9);
            bool follows = a != null && Near(VAnim.Redact(a, 4.0).X + VAnim.Redact(a, 4.0).Width / 2, 0.30 + 0.05, 0.01) && Near(VAnim.Redact(a, 0).X + VAnim.Redact(a, 0).Width / 2, 0.10 + 0.05, 0.02);
            Check("얼굴 잇기: 같은 얼굴은 상자 하나(잠깐 안 보여도), 따로 나온 얼굴은 따로", linked && shape && times && follows, "상자 " + made.Count + "개 · 표시 " + string.Join("/", made.Select(r => r.Keys.Count)));

            // 다시 찾았을 때 '보이게 둠'을 이어 가는 데 쓰는 같은 얼굴 판단
            var o1 = new VRedact { Start = 1, End = 3, X = 0.4, Y = 0.4, W = 0.1, H = 0.2 };
            var o2 = new VRedact { Start = 1.2, End = 3.5, X = 0.41, Y = 0.41, W = 0.1, H = 0.2 };
            var o3 = new VRedact { Start = 1, End = 3, X = 0.7, Y = 0.4, W = 0.1, H = 0.2 };
            var o4 = new VRedact { Start = 3.1, End = 5, X = 0.4, Y = 0.4, W = 0.1, H = 0.2 };
            Check("다시 찾아도 '보이게 둠' 얼굴을 알아본다", VFaces.Same(o1, o2) && !VFaces.Same(o1, o3) && !VFaces.Same(o1, o4), "");
        }

        static void RedactRender(string moving)
        {
            var rp = new VProject { Aspect = "16:9", SourceW = 640, SourceH = 360 };
            var rc = new VClip { Path = moving, In = 0, Out = 5 };
            rp.Clips.Add(rc);
            var still = new VRedact { Start = 0, End = 5, X = 0.05, Y = 0.05, W = 0.2, H = 0.2, Mode = "fill", Rgb = 0xFF00FF };
            var walking = new VRedact { Start = 0, End = 5, X = 0.6, Y = 0.6, W = 0.15, H = 0.15, Mode = "fill", Rgb = 0x00FF00 };
            walking.Keys.Add(new VKey { T = 1, X = 0.6, Y = 0.6, W = 0.15, H = 0.15 });
            walking.Keys.Add(new VKey { T = 3, X = 0.8, Y = 0.6, W = 0.15, H = 0.15 });
            rc.Redacts.Add(still);
            rc.Redacts.Add(walking);
            byte[] f1 = null, f3 = null, off = null;
            string err = null;
            try
            {
                WinRtCall.Mta(delegate
                {
                    using (var r = new VRenderer(640, 360, true))
                    {
                        r.Use(rp, 1);
                        f1 = (byte[])r.Frame(1.0).Clone();
                        f3 = (byte[])r.Frame(3.0).Clone();
                        still.Off = true;
                        r.Use(rp, 2);
                        off = (byte[])r.Frame(1.0).Clone();
                    }
                    return 0;
                });
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
            }
            bool ok = err == null && IsColor(f1, 640, 0.15, 0.15, 0xFF00FF) && !IsColor(off, 640, 0.15, 0.15, 0xFF00FF);
            Check("가리기: 그림에 들어가고, '보이게 둠'으로 끈 상자는 빠진다", ok, err ?? "");
            bool walk = err == null && IsColor(f1, 640, 0.675, 0.675, 0x00FF00) && IsColor(f3, 640, 0.875, 0.675, 0x00FF00) && !IsColor(f3, 640, 0.66, 0.675, 0x00FF00);
            Check("가리기: 움직임 표시를 따라 상자가 옮겨 간다", walk, err ?? "");
        }

        static void CursorPolish(string moving, string other)
        {
            var log = new StringBuilder();
            log.Append("bakkum-cursor 1 1920 1080\n");
            foreach (var line in new[]
            {
                "0 0.5 0.5 0", "0.5 0.5 0.5 0", "1.0 0.52 0.5 0", "1.3 0.7 0.3 0", "2.0 0.8 0.2 1", "2.1 0.8 0.2 0",
                "2.9 0.25 0.75 0", "3.0 0.2 0.8 1", "3.05 0.2 0.8 1", "3.1 0.2 0.8 0", "2.5 0.1 0.1 0", "깨진 줄",
                "4.0 1.2 0.5 1", "4.1 1.2 0.5 0", "4.6 0.5 0.5 0"
            }) log.Append(line).Append('\n');
            var parsed = VCursor.Parse(log.ToString());
            double x, y;
            bool before = parsed != null && !parsed.At(-1, out x, out y);
            bool mid = parsed != null && parsed.At(2.05, out x, out y) && x == 0.8 && y == 0.2;
            bool mid2 = parsed != null && parsed.At(2.95, out x, out y) && x == 0.25 && y == 0.75;
            Check("마우스 기록 읽기: 누른 순간 2번(화면 밖·누른 채 이어진 것·거꾸로 간 줄은 빼고)", parsed != null && parsed.W == 1920 && parsed.T.Length == 13 && parsed.Clicks.Count == 2 && parsed.ClicksIn(0, 5) == 2 && before && mid && mid2, parsed == null ? "못 읽음" : parsed.T.Length + "줄 · 누름 " + parsed.Clicks.Count);
            Check("마우스 기록: 머리줄이 다르면 읽지 않는다", VCursor.Parse("something 1 2 3\n0 0 0 0") == null && VCursor.Parse("") == null, "");

            string logPath = CursorLog.PathFor(moving);
            File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
            string copy = V("이름 바꾼 녹화.mp4");
            File.Copy(moving, copy);
            var cur = VCursor.Of(copy);
            bool where = logPath.StartsWith(VAutoSave.Folder, StringComparison.OrdinalIgnoreCase) && !Directory.GetFiles(P("videojob")).Any(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            Check("마우스 기록: 영상 옆이 아니라 바꿈 자료 폴더에, 이름·자리를 바꿔도 같은 기록", where && CursorLog.PathFor(copy) == logPath && CursorLog.PathFor(other) != logPath && cur != null && cur.Clicks.Count == 2, Path.GetFileName(logPath));
            if (cur == null) return;

            var pp = new VProject { Aspect = "source", SourceW = 640, SourceH = 360 };
            var clip = new VClip { Path = copy, In = 0, Out = 5 };
            pp.Clips.Add(clip);
            var pr = VMedia.Probe(copy);
            int W, H;
            pp.CanvasSizeLong(1920, out W, out H);
            int clicks, items;
            VPolish.Apply(pp, clip, cur, pr, true, 1.8, true, true, 0xFFD447, W, H, out clicks, out items);
            var spot = pp.Items.FirstOrDefault(i => i.Shape == "spot");
            var ripples = pp.Items.Where(i => i.Shape == "ripple").OrderBy(i => i.Start).ToList();
            Check("다듬기: 누른 곳 2군데 확대 + 마우스 강조 1 + 누른 자리 2", clicks == 2 && items == 3 && spot != null && ripples.Count == 2 && Near(ripples[0].Start, 2.0, 0.01) && Near(ripples[1].Start, 3.0, 0.01) && Near(ripples[0].Length, 0.6, 0.01), "누름 " + clicks + " · 얹은 것 " + items);
            var ks = clip.Keys;
            bool shape = ks.Count >= 6 && Near(ks[0].T, 0, 1e-9) && ks[0].Zoom == 1 && ks.Any(k => Near(k.Zoom, 1.8, 1e-9)) && Near(ks[ks.Count - 1].T, 5, 1e-9) && ks[ks.Count - 1].Zoom == 1;
            Check("다듬기: 누르기 앞에서 다가가고 끝에서 돌아온다", shape, string.Join(" ", ks.Select(k => k.T.ToString("0.00") + "×" + k.Zoom.ToString("0.0"))));

            // 확대·옮김 동안 빈 곳이 보이지 않고, 누른 곳은 화면 가운데 쪽으로 온다
            bool covered = true;
            string gap = "";
            for (double t = 0; t <= 5.0001; t += 0.05)
            {
                double local;
                clip.LocalOf(t, out local);
                Rect r;
                bool covers;
                VRenderer.PlaceOn(clip, local, t, pr.Width, pr.Height, pr.Rotation, false, W, H, out r, out covers);
                if (!covers)
                {
                    covered = false;
                    gap = t.ToString("0.00") + "초";
                    break;
                }
            }
            Check("다듬기: 확대·옮기는 동안 빈 곳이 보이지 않는다", covered, gap);
            Point p2 = CanvasOf(clip, 2.0, 0.8, 0.2, pr, W, H), p3 = CanvasOf(clip, 3.0, 0.2, 0.8, pr, W, H);
            Check("다듬기: 누른 곳이 가운데 쪽으로(가장자리는 붙잡음)", Near(p2.X, 0.64, 0.01) && Near(p2.Y, 0.36, 0.01) && Near(p3.X, 0.36, 0.01) && Near(p3.Y, 0.64, 0.01), p2.X.ToString("0.000") + "," + p2.Y.ToString("0.000") + " / " + p3.X.ToString("0.000") + "," + p3.Y.ToString("0.000"));
            var sp2 = VAnim.Item(spot, 2.05);
            var sp0 = VAnim.Item(spot, 0.2);
            var spOut = VAnim.Item(spot, 4.05);
            Check("다듬기: 마우스 강조가 확대된 화면에서도 마우스를 따라간다", Near(sp2.X, 0.64, 0.015) && Near(sp2.Y, 0.36, 0.015) && Near(sp0.X, 0.5, 0.01) && Near(sp0.Y, 0.5, 0.01) && spOut.Opacity < 0.05, sp2.X.ToString("0.000") + "," + sp2.Y.ToString("0.000") + " · 화면 밖 " + spOut.Opacity.ToString("0.00"));
            var ov = new VOverlayRenderer(640, 360).Render(pp, 1, 2.05);
            bool drawn = ov != null && ov[((int)Math.Round(sp2.Y * 360) * 640 + (int)Math.Round(sp2.X * 640)) * 4 + 3] > 40;
            Check("다듬기: 마우스 강조가 그려진다", drawn, "");
            VPolish.Apply(pp, clip, cur, pr, true, 1.8, true, true, 0xFFD447, W, H, out clicks, out items);
            Check("다듬기: 다시 넣어도 겹치지 않는다", pp.Items.Count == 3, pp.Items.Count + "개");
            int removed = VPolish.Clear(pp, clip, true);
            Check("다듬기 지우기", removed == 3 && pp.Items.Count == 0 && clip.Keys.Count == 0, removed + "개");
        }

        static Point CanvasOf(VClip c, double s, double ux, double uy, MediaProbe pr, int W, int H)
        {
            double local;
            c.LocalOf(s, out local);
            Rect clip;
            bool covers;
            var m = VRenderer.PlaceOn(c, local, s, pr.Width, pr.Height, pr.Rotation, false, W, H, out clip, out covers);
            var q = m.Transform(new Point(ux * pr.Width, uy * pr.Height));
            return new Point(q.X / W, q.Y / H);
        }

        static void TitleCards()
        {
            var tp = new VProject();
            var c1 = new VClip { Path = V("1반_모둠_발표.mp4"), In = 0, Out = 10 };
            var c2 = new VClip { Path = V("group_b.mp4"), In = 0, Out = 10 };
            var c3 = new VClip { Kind = "color", Dur = 2 };
            var c4 = new VClip { Path = V("마무리.mp4"), In = 0, Out = 5 };
            tp.Clips.AddRange(new[] { c1, c2, c3, c4 });
            tp.Split(15);
            var sub = new VItem { Kind = "text", Subtitle = true, Text = "첫 자막", Start = 1, End = 2 };
            tp.Items.Add(sub);
            var targets = VPolish.TitleTargets(tp);
            int made = VPolish.AddTitleCards(tp);
            var titles = tp.Items.Where(i => i.Kind == "text" && !i.Subtitle).OrderBy(i => i.Start).ToList();
            bool ok = targets.SequenceEqual(new[] { 0, 1 }) && made == 2 && tp.Clips.Count == 7 && tp.Clips[0].Kind == "color" && tp.Clips[2].Kind == "color"
                && titles.Count == 2 && titles[0].Text == "1반 모둠 발표" && titles[1].Text == "2. 제목을 써 주세요"
                && Near(titles[0].Start, 0, 1e-6) && Near(titles[1].Start, 12.5, 1e-6) && Near(titles[0].End, 2.5, 1e-6) && Near(sub.Start, 3.5, 1e-6);
            Check("제목 화면: 영상마다 하나(나눈 조각 뒤쪽·색 화면 뒤는 건너뜀), 뒤 자막은 밀려난다", ok, "자리 " + string.Join(",", targets) + " · " + string.Join(" | ", titles.Select(t => t.Text)) + " · 자막 " + sub.Start);
            Check("제목 화면: 이미 넣은 곳에는 또 넣지 않는다", VPolish.TitleTargets(tp).Count == 0, "");
        }

        static void Chapters()
        {
            var warns = new List<string>();
            string one = VideoEditor.ChapterText(new List<VMarker>(), 120, warns);
            bool few = one == "0:00 시작" && warns.Count == 1 && warns[0].Contains("3개");
            warns.Clear();
            var good = VideoEditor.ChapterText(new List<VMarker> { new VMarker { T = 190, Label = "정리" }, new VMarker { T = 0.5, Label = "도입" }, new VMarker { T = 65, Label = "모둠\n활동" } }, 300, warns);
            bool sorted = good == "0:00 도입\r\n1:05 모둠 활동\r\n3:10 정리" && warns.Count == 0;
            var warns2 = new List<string>();
            var shortOne = VideoEditor.ChapterText(new List<VMarker> { new VMarker { T = 30 }, new VMarker { T = 35, Label = "둘째" } }, 100, warns2);
            bool shortWarn = shortOne.StartsWith("0:00 시작\r\n0:30 구간 1\r\n0:35 둘째") && warns2.Count == 1 && warns2[0].Contains("0:30");
            Check("유튜브 챕터: 0:00부터, 순서대로, 줄바꿈은 띄어쓰기로", few && sorted, good.Replace("\r\n", " / "));
            Check("유튜브 챕터: 3개 미만·10초보다 짧은 챕터는 알려 준다", few && shortWarn, string.Join(" / ", warns2));
        }

        static void Styles()
        {
            var list = new List<VItem>
            {
                new VItem { Kind = "text", Name = "노란 자막", Text = "예시", Rgb = 0xFFD447, Size = 0.07, Box = true, In = "pop" },
                new VItem { Kind = "shape", Shape = "bar", Name = "이름 띠", Rgb = 0x2A9D8F, W = 0.4, H = 0.08 }
            };
            VideoEditor.SaveStyles(list);
            var back = VideoEditor.LoadStyles();
            string path = Path.Combine(VAutoSave.Folder, "styles.json");
            bool ok = back.Count == 2 && back[0].Name == "노란 자막" && back[0].Rgb == 0xFFD447 && back[0].Box && back[0].In == "pop" && back[1].Shape == "bar" && Near(back[1].W, 0.4, 1e-9) && File.Exists(path) && !File.Exists(path + ".tmp");
            Check("내 꾸밈: 저장했다 다시 불러오기", ok, back.Count + "개");
            File.WriteAllText(path, "{망가진", Encoding.UTF8);
            List<VItem> broken = null;
            try
            {
                broken = VideoEditor.LoadStyles();
            }
            catch (Exception)
            {
            }
            Check("내 꾸밈: 파일이 망가져도 창이 멈추지 않는다", broken != null && broken.Count == 0, broken == null ? "예외" : broken.Count + "개");
        }

        static void SrtFiles()
        {
            string srt = "﻿1\r\n00:00:01,500 --> 00:00:03,000\r\n<i>안녕하세요</i>\r\n둘째 줄\r\n\r\n2\r\n00:00:04.2 --> 00:00:05,000 X1:10\r\n{\\an8}위쪽 자막\r\n\r\n3\r\n시간이 없는 덩어리\r\n\r\n4\r\n00:00:07,000 --> 00:00:06,000\r\n거꾸로\r\n";
            var subs = VideoEditor.ParseSrt(Encoding.UTF8.GetBytes(srt));
            bool ok = subs.Count == 2 && Near(subs[0].Start, 1.5, 1e-9) && Near(subs[0].End, 3, 1e-9) && subs[0].Text == "안녕하세요\n둘째 줄" && subs[0].Subtitle && Near(subs[1].Start, 4.2, 1e-9) && subs[1].Text == "위쪽 자막";
            var ansi = VideoEditor.ParseSrt(Encoding.GetEncoding(949).GetBytes("1\r\n00:00:00,000 --> 00:00:01,000\r\n한글 자막\r\n"));
            Check("자막 파일 읽기: 꾸밈 표시 빼고, 깨진 덩어리는 건너뜀", ok, string.Join(" | ", subs.Select(s => s.Start + "~" + s.End + " " + s.Text.Replace("\n", "/"))));
            Check("자막 파일 읽기: 옛 한글(CP949) 파일도", ansi.Count == 1 && ansi[0].Text == "한글 자막", ansi.Count == 1 ? ansi[0].Text : "");
            Check("자막 파일 시간 적기", VideoEditor.SrtClock(3725.5) == "01:02:05,500" && VideoEditor.SrtClock(0.0004) == "00:00:00,000", VideoEditor.SrtClock(3725.5));
        }

        static void Lecture()
        {
            // 시험 폴더에 수업자료.pdf를 넣어 두면 그것을 쓰고, 없으면 지어낸 세 쪽짜리 자료를 바꿈의 글자 → PDF로 만들어 쓴다.
            string pdf = P("수업자료.pdf");
            if (!File.Exists(pdf))
            {
                pdf = V("수업자료.pdf");
                var blocks = new List<DocBlock>();
                string[] heads = { "물의 순환", "구름이 생기는 까닭", "정리해 봅시다" };
                for (int i = 0; i < heads.Length; i++)
                {
                    if (i > 0) blocks.Add(DocBlock.Break());
                    blocks.Add(DocBlock.Head(heads[i], 1));
                    blocks.Add(DocBlock.Para("시험용으로 지어낸 수업 자료의 " + (i + 1) + "쪽입니다."));
                }
                try
                {
                    if (File.Exists(pdf)) File.Delete(pdf);
                    TextPdf.Write(pdf, blocks, "시험용 수업 자료", new Job(ActionOf("pdf"), new ActionOption("go", "", ""), new List<string>()));
                }
                catch (Exception ex)
                {
                    Cli.Print("       참고: 시험용 수업 자료를 만들지 못함 " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            if (!File.Exists(pdf))
            {
                Check("수업 자료: 시험용 PDF가 있다", false, pdf);
                return;
            }
            string folder = VideoEditor.LectureFolder(pdf);
            var pages = new List<string>();
            int total = 0;
            string err = null;
            var sw = Stopwatch.StartNew();
            try
            {
                total = WinRtCall.Mta(delegate { return VideoEditor.LecturePages(pdf, folder, pages, delegate (string s, double f) { }, () => false); });
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
            }
            long first = sw.ElapsedMilliseconds;
            bool sizes = pages.Count > 0 && pages.All(p =>
            {
                int pw, ph;
                return PngSize(p, out pw, out ph) && Math.Max(pw, ph) <= 2560 && pw > 100;
            });
            int fw = 0, fh = 0;
            if (pages.Count > 0) PngSize(pages[0], out fw, out fh);
            Check("수업 자료: PDF 쪽마다 그림(바꿈 자료 폴더 안에)", err == null && total > 0 && pages.Count == total && sizes && folder.StartsWith(VAutoSave.Folder, StringComparison.OrdinalIgnoreCase), err ?? (total + "쪽 · 그림 " + pages.Count + "장 · 첫 쪽 " + fw + "×" + fh + " · " + first + "ms"));
            if (pages.Count == 0) return;
            var stamp = File.GetLastWriteTimeUtc(pages[0]);
            var again = new List<string>();
            sw.Restart();
            WinRtCall.Mta(delegate { return VideoEditor.LecturePages(pdf, VideoEditor.LectureFolder(pdf), again, delegate (string s, double f) { }, () => false); });
            Check("수업 자료: 같은 자료를 또 넣으면 만든 그림을 다시 쓴다", again.SequenceEqual(pages) && File.GetLastWriteTimeUtc(pages[0]) == stamp && !Directory.GetFiles(folder, "*.tmp").Any(), sw.ElapsedMilliseconds + "ms");
            bool stopped = false;
            try
            {
                Directory.Delete(folder, true);
                WinRtCall.Mta(delegate { return VideoEditor.LecturePages(pdf, VideoEditor.LectureFolder(pdf), new List<string>(), delegate (string s, double f) { }, () => true); });
            }
            catch (OperationCanceledException)
            {
                stopped = true;
            }
            Check("수업 자료: [멈추기]를 누르면 바로 멈춘다", stopped, "");
        }

        /// 발표자 노트가 든 작은 pptx(그림 없이 노트만). 순서는 슬라이드 목록을 따르고(파일 이름과 다르게),
        /// 둘째 슬라이드는 숨김, 셋째는 노트 없음. 첫 노트에는 쪽 그림·쪽 번호 칸도 있다.
        static void MakeNotesPptx(string path)
        {
            const string Ns = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
            const string R = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">";
            const string NotesType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide";
            Func<string, string, string> sp = (type, paras) => "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"x\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"" + type + "\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/>" + paras + "</p:txBody></p:sp>";
            Func<string, string> para = s => "<a:p><a:r><a:t>" + System.Security.SecurityElement.Escape(s) + "</a:t></a:r></a:p>";
            var parts = new Dictionary<string, string>
            {
                { "ppt/presentation.xml", "<p:presentation " + Ns + "><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/><p:sldId id=\"257\" r:id=\"rId3\"/><p:sldId id=\"258\" r:id=\"rId4\"/></p:sldIdLst></p:presentation>" },
                { "ppt/_rels/presentation.xml.rels", R + "<Relationship Id=\"rId3\" Type=\"x/slide\" Target=\"/ppt/slides/slide1.xml\"/><Relationship Id=\"rId2\" Type=\"x/slide\" Target=\"slides/slide2.xml\"/><Relationship Id=\"rId4\" Type=\"x/slide\" Target=\"slides/slide3.xml\"/></Relationships>" },
                { "ppt/slides/slide1.xml", "<p:sld " + Ns + " show=\"0\"><p:cSld><p:spTree/></p:cSld></p:sld>" },
                { "ppt/slides/slide2.xml", "<p:sld " + Ns + "><p:cSld><p:spTree/></p:cSld></p:sld>" },
                { "ppt/slides/slide3.xml", "<p:sld " + Ns + "><p:cSld><p:spTree/></p:cSld></p:sld>" },
                { "ppt/slides/_rels/slide2.xml.rels", R + "<Relationship Id=\"rId1\" Type=\"x/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"" + NotesType + "\" Target=\"../notesSlides/notesSlide7.xml\"/></Relationships>" },
                { "ppt/slides/_rels/slide1.xml.rels", R + "<Relationship Id=\"rId9\" Type=\"" + NotesType + "\" Target=\"../notesSlides/notesSlide8.xml\"/></Relationships>" },
                { "ppt/notesSlides/notesSlide7.xml", "<p:notes " + Ns + "><p:cSld><p:spTree>" + sp("sldImg", "") + sp("body", para("• 오늘은 물의 순환을 배워요. 구름은 어떻게 생길까요?") + para("1. 바다의 물이 증발해요.") + para("원주율은 3.14예요.")) + "<p:sp><p:nvSpPr><p:cNvPr id=\"4\" name=\"n\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"sldNum\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:p><a:fld id=\"{1}\" type=\"slidenum\"><a:t>1</a:t></a:fld></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:notes>" },
                { "ppt/notesSlides/notesSlide8.xml", "<p:notes " + Ns + "><p:cSld><p:spTree>" + sp("body", para("숨긴 쪽의 노트")) + "</p:spTree></p:cSld></p:notes>" }
            };
            if (File.Exists(path)) File.Delete(path);
            using (var z = new System.IO.Compression.ZipArchive(File.Create(path), System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var kv in parts)
                {
                    using (var w = new StreamWriter(z.CreateEntry(kv.Key).Open(), new UTF8Encoding(false))) w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + kv.Value);
                }
            }
        }

        static void LectureNotes()
        {
            string pptx = V("노트자료.pptx");
            MakeNotesPptx(pptx);
            List<OoxmlRead.SlideNote> notes = null;
            string err = null;
            try
            {
                notes = OoxmlRead.PptxNotes(pptx);
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
            }
            Check("발표자 노트: 슬라이드 순서대로, 노트 칸 글만(쪽 번호는 빼고)", err == null && notes != null && notes.Count == 3
                && notes[0].Text == "• 오늘은 물의 순환을 배워요. 구름은 어떻게 생길까요?\n1. 바다의 물이 증발해요.\n원주율은 3.14예요." && !notes[0].Hidden
                && notes[1].Text == "숨긴 쪽의 노트" && notes[1].Hidden && notes[2].Text == "" && !notes[2].Hidden,
                err ?? string.Join(" | ", notes.Select(n => (n.Hidden ? "[숨김] " : "") + n.Text.Replace("\n", "/"))));
            if (notes == null) return;

            var sent = VLecture.Sentences(notes[0].Text);
            Check("발표자 노트: 문장으로 나누고 글머리표·번호는 뗀다(3.14는 그대로)", sent.SequenceEqual(new[] { "오늘은 물의 순환을 배워요.", "구름은 어떻게 생길까요?", "바다의 물이 증발해요.", "원주율은 3.14예요." }), string.Join(" | ", sent));
            var longOne = VLecture.Sentences("먼저 바다와 강과 호수의 물이 햇볕을 받아 수증기가 되어 하늘로 올라가고, 높은 곳에서 차가워진 수증기가 작은 물방울이 되어 구름을 만들어요.");
            Check("발표자 노트: 아주 긴 문장은 가운데 쉼표에서 한 번 나눈다", longOne.Count == 2 && longOne[0].EndsWith(",") && longOne.All(s => s.Length <= 60), string.Join(" | ", longOne));
            var tiny = VLecture.Sentences("자, 시작해요\n네");
            Check("발표자 노트: 아주 짧은 조각은 앞 문장에 붙인다", tiny.Count == 1 && tiny[0] == "자, 시작해요 네", string.Join(" | ", tiny));

            var visible = VLecture.MapNotes(notes, 2, 2);
            var every = VLecture.MapNotes(notes, 3, 3);
            Check("발표자 노트: PDF에서 숨긴 슬라이드가 빠졌으면 보이는 것만 잇는다", visible != null && visible.Count == 2 && visible[0].StartsWith("• 오늘은") && visible[1] == "", visible == null ? "없음" : string.Join(" | ", visible));
            Check("발표자 노트: 숨긴 슬라이드도 PDF에 있으면 모두 잇는다", every != null && every.Count == 3 && every[1] == "숨긴 쪽의 노트", every == null ? "없음" : every.Count.ToString());
            Check("발표자 노트: 쪽 수가 안 맞으면 잇지 않는다", VLecture.MapNotes(notes, 5, 5) == null && VLecture.MapNotes(notes, 2, 1).Count == 1, null);

            var fakeVoices = new List<VTts.Voice> { new VTts.Voice { Id = "ko1", Lang = "ko-KR", Korean = true }, new VTts.Voice { Id = "en1", Lang = "en-US" } };
            Check("발표자 노트: 영어 문장은 영어 목소리로", VLecture.VoiceFor("Hello everyone, let's start.", "ko1", fakeVoices) == "en1" && VLecture.VoiceFor("오늘은 Hello를 배워요.", "ko1", fakeVoices) == "ko1"
                && VLecture.VoiceFor("Hello", "ko1", new List<VTts.Voice> { fakeVoices[0] }) == "ko1", null);

            // 시간 막대에 놓기: 있던 사진 조각 앞에 두 쪽(첫 쪽만 노트 두 문장), 있던 자막은 따라 밀린다
            string page1 = V("쪽1.png"), page2 = V("쪽2.png"), old = V("있던.png"), w1 = V("읽기1.wav"), w2 = V("읽기2.wav");
            SavePng(page1, 64, 36, BlockNoise(64, 36, 8, 3));
            SavePng(page2, 64, 36, BlockNoise(64, 36, 8, 4));
            SavePng(old, 64, 36, BlockNoise(64, 36, 8, 5));
            WriteWav(w1, 1.0, x => 0.2 * Math.Sin(2 * Math.PI * 300 * x));
            WriteWav(w2, 0.5, x => 0.2 * Math.Sin(2 * Math.PI * 500 * x));
            var p = new VProject { Aspect = "16:9" };
            p.Clips.Add(new VClip { Kind = "photo", Path = old, Dur = 4 });
            var oldSub = new VItem { Kind = "text", Subtitle = true, Text = "있던 자막", Start = 1, End = 2, X = 0.5, Y = 0.8 };
            p.Items.Add(oldSub);
            var spoken = new List<List<VLecture.Spoken>>
            {
                new List<VLecture.Spoken> { new VLecture.Spoken { Text = "첫 문장", Wav = w1, Len = 1.0 }, new VLecture.Spoken { Text = "둘째 문장", Wav = w2, Len = 0.5 } },
                new List<VLecture.Spoken>()
            };
            int placed = VLecture.Insert(p, 0, new List<string> { page1, page2 }, spoken, 3, true, oldSub);
            double firstLen = VLecture.Lead + 1.0 + VLecture.Gap + 0.5 + VLecture.Tail;
            var voices = p.Sounds.Where(s => s.Name == "노트 읽기").OrderBy(s => s.Start).ToList();
            var subs = p.Items.Where(i => i.Subtitle && i != oldSub).OrderBy(i => i.Start).ToList();
            bool ok = placed == 2 && p.Clips.Count == 3 && Near(p.Clips[0].Length, firstLen, 0.001) && Near(p.Clips[1].Length, 3, 0.001) && p.Clips[2].Path == old
                && voices.Count == 2 && Near(voices[0].Start, VLecture.Lead, 0.001) && Near(voices[1].Start, VLecture.Lead + 1.0 + VLecture.Gap, 0.001)
                && subs.Count == 2 && subs[0].Text == "첫 문장" && Near(subs[0].Start, voices[0].Start, 0.001) && subs[0].End > voices[0].Start + 1.0 && subs[0].End <= voices[1].Start
                && voices[0].ItemId == subs[0].Id && Near(subs[0].Y, 0.8, 0.001)
                && Near(oldSub.Start, firstLen + 3 + 1, 0.01);
            Check("발표자 노트: 쪽 길이는 읽는 길이에 맞추고, 소리·자막이 그 쪽 위에, 뒤의 자막은 밀린다", ok,
                "쪽 " + string.Join(", ", p.Clips.Select(c => c.Length.ToString("0.00"))) + " · 읽기 " + string.Join(", ", voices.Select(v => v.Start.ToString("0.00"))) + " · 있던 자막 " + oldSub.Start.ToString("0.00"));

            // 진짜 윈도우 목소리로 한 문장(이 PC 안에서만 만든다)
            List<VTts.Voice> real = null;
            try
            {
                real = VTts.Voices();
            }
            catch (Exception ex)
            {
                Cli.Print("    목소리 목록 실패: " + ex.GetType().Name);
            }
            var ko = real == null ? null : real.FirstOrDefault(v => v.Korean);
            if (ko == null)
            {
                Cli.Print("    한국어 목소리가 없어 진짜 읽기는 건너뜀");
                return;
            }
            string wav = null;
            err = null;
            try
            {
                wav = VTts.Speak("오늘은 물의 순환을 배워요.", ko.Id, 1.0);
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
            }
            var pr = wav == null ? null : Probe(wav);
            Check("발표자 노트: 윈도우 목소리로 한 문장 읽기", err == null && pr != null && pr.HasAudio && pr.Seconds > 0.8 && pr.Seconds < 6, err ?? Say(pr));
        }

        static void SaveAndTidy(string moving)
        {
            var p = new VProject { Aspect = "9:16", Back = "color", BackRgb = 0x123456 };
            var c = new VClip { Path = moving, In = 0.5, Out = 4, Speed = 1.25, Rotate = 90, FlipH = true, Bright = 0.2, Trans = "wipe", Stabilize = true };
            c.Keys.Add(new VKey { T = 1, Zoom = 1.5, PanX = 0.1 });
            c.Redacts.Add(new VRedact { Face = true, Off = true, Start = 1, End = 2, X = 0.1, Y = 0.1, W = 0.2, H = 0.2 });
            p.Clips.Add(c);
            p.Items.Add(new VItem { Kind = "text", Text = "줄\n바꿈 \"따옴표\"", Emph = "바꿈", Subtitle = true, Start = 0, End = 1 });
            p.Sounds.Add(new VSound { Kind = "music", Path = moving, Loop = true, Volume = 0.4 });
            p.Markers.Add(new VMarker { T = 2, Label = "표시" });
            p.Opened.Add(moving);
            string json = p.ToJson();
            var back = VProject.FromJson(json);
            Check("저장(JSON): 모든 값이 그대로 돌아온다", back.ToJson() == json && back.Clips[0].Redacts[0].Off && back.Items[0].Text == "줄\n바꿈 \"따옴표\"", json.Length + "자");

            var h = new VHistory();
            var a = p.ToJson();
            h.Push(a);
            p.Clips[0].Speed = 2;
            var b = p.ToJson();
            string undo = h.Undo(b);
            string redo = h.Redo(undo);
            Check("되돌리기·다시 하기", undo == a && redo == b && h.CanUndo && !h.CanRedo, "");

            VAutoSave.Save(p);
            var loaded = VAutoSave.Load(new[] { moving });
            Check("자동 저장 → 이어서 하기", loaded != null && loaded.ToJson() == p.ToJson(), "");
            VAutoSave.Forget(new[] { moving });
            Check("자동 저장 지우기", VAutoSave.Load(new[] { moving }) == null, "");

            string cursorDir = Path.Combine(VAutoSave.Folder, "cursor"), lectureDir = Path.Combine(VAutoSave.Folder, "lecture");
            Directory.CreateDirectory(cursorDir);
            string oldLog = Path.Combine(cursorDir, "오래됨.txt"), newLog = Path.Combine(cursorDir, "최근.txt"), oldPages = Path.Combine(lectureDir, "오래된자료");
            File.WriteAllText(oldLog, "x");
            File.WriteAllText(newLog, "x");
            Directory.CreateDirectory(oldPages);
            File.WriteAllText(Path.Combine(oldPages, "001.png"), "x");
            File.SetLastWriteTime(oldLog, DateTime.Now.AddDays(-130));
            Directory.SetLastWriteTime(oldPages, DateTime.Now.AddDays(-130));
            VAutoSave.Tidy();
            Check("오래된 마우스 기록·쪽 그림 정리(120일)", !File.Exists(oldLog) && File.Exists(newLog) && !Directory.Exists(oldPages), "");
            File.Delete(newLog);
        }

        static void ExportChecks(string moving)
        {
            var cp = new VProject { Aspect = "16:9", SourceW = 640, SourceH = 360 };
            cp.Clips.Add(new VClip { Path = moving, In = 0, Out = 5 });
            cp.Clips.Add(new VClip { Path = V("없는 영상.mp4"), In = 0, Out = 3 });
            cp.Items.Add(new VItem { Kind = "text", Subtitle = true, Text = "아주 빠르게 지나가는 아주 긴 자막 한 줄입니다", Start = 1, End = 1.5 });
            cp.Items.Add(new VItem { Kind = "text", Text = "  ", Start = 2, End = 3 });
            cp.Items.Add(new VItem { Kind = "text", Text = "화면 밖", X = 3.0, Y = 0.5, Start = 3, End = 4 });
            cp.Export.Preset = "reels";
            var plan = VideoExport.Plan(cp);
            plan.Output = V("점검.mp4");
            var checks = VideoExport.Checks(cp, plan);
            Func<Func<VCheck, bool>, bool> has = delegate (Func<VCheck, bool> f) { return checks.Any(f); };
            Check("점검: 없는 원본은 멈추고 [다시 찾기]", has(c => c.Level == "stop" && c.Fix == "relink" && c.Text.Contains("없는 영상")), "");
            Check("점검: 릴스인데 가로면 9:16 고치기", has(c => c.Level == "warn" && c.Fix == "aspect916"), "");
            Check("점검: 빨리 지나가는 자막·빈 글자·화면 밖 글자", has(c => c.Fix == "subtitle_slow" && Near(c.T, 1, 1e-9)) && has(c => c.Level == "info" && c.Text.Contains("비어")) && has(c => c.Text.Contains("화면 밖")), "");
            Check("점검: 크기·예상 용량과 원본 파일 정보 안내", has(c => c.Level == "info" && c.Text.StartsWith(plan.W + "×" + plan.H)) && has(c => c.Text.Contains("찍은 장소")), plan.W + "×" + plan.H);
            int n = cp.Relink(V("없는 영상.mp4"), moving);
            var after = VideoExport.Checks(cp, plan);
            Check("점검: 원본을 다시 이어 주면 멈춤이 사라진다", n == 1 && !after.Any(c => c.Level == "stop"), n + "개 바꿈");

            var vp = new VProject { Aspect = "9:16", SourceW = 640, SourceH = 360 };
            vp.Clips.Add(new VClip { Path = moving, In = 0, Out = 200 });
            vp.Items.Add(new VItem { Kind = "text", Text = "아래쪽 글자", Y = 0.93, Start = 0, End = 3 });
            vp.Export.Preset = "shorts";
            var vplan = VideoExport.Plan(vp);
            vplan.Output = V("점검2.mp4");
            var vchecks = VideoExport.Checks(vp, vplan);
            Check("점검: 세로 영상의 가려지는 곳·쇼츠 3분", vchecks.Any(c => c.Text.Contains("가려질")) && vchecks.Any(c => c.Text.Contains("3분")) && vplan.W == 1080 && vplan.H == 1920, vplan.W + "×" + vplan.H);
            var empty = VideoExport.Checks(new VProject(), vplan);
            Check("점검: 빈 편집은 만들 수 없다고 알린다", empty.Count == 1 && empty[0].Level == "stop", "");

            // 가로 화면 오른쪽 끝에 둔 글자는 화면 안이다(첫 글자도)
            var wide = new VProject { Aspect = "16:9", SourceW = 640, SourceH = 360 };
            wide.Clips.Add(new VClip { Path = moving, In = 0, Out = 5 });
            wide.Items.Add(new VItem { Kind = "text", Text = "오른쪽 글자", X = 0.88, Y = 0.5, Start = 0, End = 2 });
            var wplan = VideoExport.Plan(wide);
            wplan.Output = V("점검3.mp4");
            Check("점검: 화면 오른쪽 끝 글자를 화면 밖으로 잘못 알리지 않는다", !VideoExport.Checks(wide, wplan).Any(c => c.Text.Contains("화면 밖")), "");
        }

        static void ExportKinds(string moving, string bg, string tex, string music)
        {
            // 구간만
            var p = new VProject { Aspect = "16:9", SourceW = 640, SourceH = 360 };
            p.Clips.Add(new VClip { Path = moving, In = 0, Out = 5 });
            p.Opened.Add(moving);
            p.Export.RangeOnly = true;
            p.Export.RangeStart = 1;
            p.Export.RangeEnd = 3;
            var plan = VideoExport.Plan(p);
            plan.Output = V("구간.mp4");
            var j = ExportNow(p, plan, null);
            var pr = Probe(plan.Output);
            Check("내보내기: 고른 구간만(1~3초)", pr != null && Near(pr.Seconds, 2, 0.15) && pr.Width == 640 && Near(plan.From, 1, 1e-9), Say(pr) + " " + string.Join(" / ", j.Problems));

            // 그래픽 인코더가 쓰는 도중 멈추면: 앞쪽(4번째 장)이면 CPU 인코더로 처음부터 다시, 뒤쪽(101번째 장)이면 알리고 만들던 파일을 지운다.
            // 어느 쪽이든 그 뒤의 내보내기는 CPU 인코더로 한다(VEncoder.CpuOnly).
            var gp = new VProject { Aspect = "16:9", SourceW = 640, SourceH = 360 };
            gp.Clips.Add(new VClip { Path = moving, In = 0, Out = 5 });
            string early = V("그래픽 앞쪽 멈춤.mp4"), late = V("그래픽 뒤쪽 멈춤.mp4"), after = V("그래픽 멈춘 뒤.mp4");
            Job ej, lj2, nj;
            bool cpuAfterEarly, cpuAfterLate;
            try
            {
                VEncoder.FailHardwareAt = 3;
                ej = ExportNow(gp, PlanFor(early, 640, 360, 0, 5), null);
                cpuAfterEarly = VEncoder.CpuOnly;
                VEncoder.CpuOnly = false;
                VEncoder.FailHardwareAt = 100;
                lj2 = ExportNow(gp, PlanFor(late, 640, 360, 0, 5), null);
                cpuAfterLate = VEncoder.CpuOnly;
                VEncoder.FailHardwareAt = -1;
                nj = ExportNow(gp, PlanFor(after, 640, 360, 0, 5), null);
            }
            finally
            {
                VEncoder.FailHardwareAt = -1;
                VEncoder.CpuOnly = false;
            }
            var epr = Probe(early);
            Check("내보내기: 그래픽 인코더가 앞쪽에서 멈추면 CPU 인코더로 처음부터 다시 만든다", ej.Problems.Count == 0 && epr != null && epr.HasVideo && epr.HasAudio && Near(epr.Seconds, 5, 0.15) && ej.Notes.Any(x => x.Contains("그래픽 인코더 없이")) && cpuAfterEarly, Say(epr) + " " + string.Join(" / ", ej.Problems.Concat(ej.Notes)));
            Check("내보내기: 뒤쪽에서 멈추면 다시 내보내면 된다고 알리고 만들던 파일을 지운다", lj2.Problems.Any(x => x.Contains("다시 내보내면 그래픽 인코더 없이")) && !File.Exists(late) && lj2.Outputs.Count == 0 && cpuAfterLate, string.Join(" / ", lj2.Problems));
            Check("내보내기: 그래픽 인코더가 멈춘 뒤의 내보내기는 CPU 인코더로", nj.Problems.Count == 0 && File.Exists(after) && nj.Notes.Any(x => x.Contains("그래픽 인코더 없이")), string.Join(" / ", nj.Problems.Concat(nj.Notes)));
            Check("그래픽 인코더 탓으로 보는 오류: 인코더 오류·메모리 부족은 넣고, 파일 쪽 오류(USB 뽑힘 등)는 뺀다",
                VEncoder.GpuFault(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80004005))) && VEncoder.GpuFault(new OutOfMemoryException())
                && !VEncoder.GpuFault(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80070015))) && !VEncoder.GpuFault(new IOException("x")), "");

            // 소리만
            foreach (var fmt in new[] { "m4a", "wav" })
            {
                var ap = p.Clone();
                ap.Export.RangeOnly = false;
                ap.Export.Preset = "audio";
                ap.Export.AudioFormat = fmt;
                var aplan = VideoExport.Plan(ap);
                aplan.Output = V("소리만." + fmt);
                var aj = ExportNow(ap, aplan, null);
                var apr = Probe(aplan.Output);
                Check("내보내기: 소리만 " + fmt.ToUpperInvariant(), apr != null && apr.HasAudio && !apr.HasVideo && Near(apr.Seconds, 5, 0.15), Say(apr) + " " + string.Join(" / ", aj.Problems));
            }

            // 표지 그림
            var cpj = p.Clone();
            cpj.Export.RangeOnly = false;
            cpj.Export.Preset = "cover";
            cpj.Export.CoverAt = 2.0;
            var cplan = VideoExport.Plan(cpj);
            cplan.Output = V("표지.png");
            var cj = ExportNow(cpj, cplan, null);
            bool coverOk = false;
            if (File.Exists(cplan.Output))
            {
                var d = BitmapDecoder.Create(new Uri(cplan.Output), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                coverOk = d.Frames[0].PixelWidth == cplan.W && d.Frames[0].PixelHeight == cplan.H;
            }
            Check("내보내기: 표지 그림(PNG)", coverOk && cplan.Kind == "cover", cplan.W + "×" + cplan.H + " " + string.Join(" / ", cj.Problems));

            // 만드는 도중 [멈추기]: 반쯤 만든 파일을 남기지 않는다
            var lp = new VProject { Aspect = "16:9", SourceW = 640, SourceH = 360 };
            for (int i = 0; i < 4; i++) lp.Clips.Add(new VClip { Path = moving, In = 0, Out = 5 });
            string partial = V("멈춘.mp4");
            bool sawFile = false;
            var lj = ExportNow(lp, PlanFor(partial, 1280, 720, 0, lp.Duration), delegate (Job job)
            {
                var watcher = new Thread(delegate ()
                {
                    var until = DateTime.Now.AddSeconds(30);
                    while (DateTime.Now < until)
                    {
                        if (File.Exists(partial) && new FileInfo(partial).Length > 0)
                        {
                            sawFile = true;
                            Thread.Sleep(300);
                            break;
                        }
                        Thread.Sleep(30);
                    }
                    job.Cancelled = true;
                }) { IsBackground = true };
                watcher.Start();
            });
            Check("내보내기 [멈추기]: 만들던 파일을 지운다", lj.Cancelled && sawFile && !File.Exists(partial) && lj.Outputs.Count == 0, "만들던 파일 봄 " + sawFile + " · " + string.Join(" / ", lj.Problems));

            // 모든 꾸밈을 함께: 색·가리기·빠르기·돌리기·멈춘 화면·거꾸로·사진·색 화면·전환·자막·도형·작은 화면·스티커·음악·끝 어둡게
            var fp = new VProject { Aspect = "9:16", SourceW = 640, SourceH = 360, Back = "blur", Level = true, EndFade = true, Duck = true };
            var a1 = new VClip { Path = moving, In = 0, Out = 2, Bright = 0.2, Sat = -0.3, Warm = 0.2, FadeIn = 0.3, Denoise = 0.5, Fit = "fill" };
            a1.Redacts.Add(new VRedact { Start = 0, End = 2, X = 0.1, Y = 0.1, W = 0.2, H = 0.3, Mode = "mosaic", Strength = 0.8 });
            fp.Clips.Add(a1);
            fp.Clips.Add(new VClip { Path = moving, In = 2, Out = 4, Speed = 1.5, Trans = "fade", Rotate = 90, FlipH = true, Pitch = 3, Stabilize = true });
            fp.Clips.Add(new VClip { Kind = "freeze", Path = moving, FreezeAt = 1.0, Dur = 1, Trans = "zoom" });
            fp.Clips.Add(new VClip { Path = moving, In = 1, Out = 2, Reverse = true, Trans = "slide" });
            fp.Clips.Add(new VClip { Kind = "photo", Path = bg, Dur = 1.5, Ken = "in", Trans = "black" });
            fp.Clips.Add(new VClip { Kind = "color", Dur = 1, Rgb = 0x2A9D8F, Trans = "wipe" });
            fp.Items.Add(new VItem { Kind = "text", Subtitle = true, Text = "모둠 활동 시작", Emph = "모둠", Start = 0.2, End = 1.8, Y = 0.8 });
            fp.Items.Add(new VItem { Kind = "text", Text = "제목", In = "type", Start = 5.5, End = 7.5, Size = 0.08 });
            fp.Items.Add(new VItem { Kind = "shape", Shape = "arrow", Start = 0, End = 2, X = 0.5, Y = 0.4, W = 0.3, H = 0.05, Rgb = 0xE5483B });
            fp.Items.Add(new VItem { Kind = "shape", Shape = "bar", Start = 2, End = 4, X = 0.4, Y = 0.7, W = 0.6, H = 0.07, Rgb = 0x2A9D8F });
            fp.Items.Add(new VItem { Kind = "pip", Path = moving, SrcIn = 0, SrcOut = 2, Start = 0.5, End = 2.5, X = 0.75, Y = 0.25, W = 0.35, Circle = true });
            fp.Items.Add(new VItem { Kind = "sticker", Sticker = "star", Start = 3, End = 4.5, X = 0.3, Y = 0.3, W = 0.15, Rgb = 0xFFD447 });
            var img = new VItem { Kind = "image", Path = tex, Start = 1, End = 3, X = 0.3, Y = 0.6, W = 0.2 };
            img.Keys.Add(new VKey { T = 0, X = 0.3, Y = 0.6 });
            img.Keys.Add(new VKey { T = 2, X = 0.6, Y = 0.6, Zoom = 1.3 });
            fp.Items.Add(img);
            fp.Sounds.Add(new VSound { Kind = "music", Path = music, Loop = true, Volume = 0.6 });
            fp.Markers.Add(new VMarker { T = 2, Label = "가운데" });
            fp.Opened.Add(moving);
            string all = V("모든꾸밈.mp4");
            var sw = Stopwatch.StartNew();
            var fj = ExportNow(fp, PlanFor(all, 360, 640, 0, fp.Duration), null);
            var fpr = Probe(all);
            Check("내보내기: 모든 꾸밈을 함께 넣어도 끝까지 만든다", fpr != null && fpr.HasVideo && fpr.HasAudio && fpr.Width == 360 && fpr.Height == 640 && Near(fpr.Seconds, fp.Duration, 0.2), Say(fpr) + " · 편집 길이 " + fp.Duration.ToString("0.00") + "초 · " + sw.ElapsedMilliseconds + "ms " + string.Join(" / ", fj.Problems));
            List<VCheck> loud = null;
            try
            {
                loud = VideoExport.LoudnessChecks(fp, null);
            }
            catch (Exception)
            {
            }
            Check("점검: 소리 크기 재기", loud != null, loud == null ? "예외" : string.Join(" / ", loud.Select(x => x.Text)));

            // 미리 보기 사본도 그래픽 인코더가 앞쪽에서 멈추면 CPU 인코더로 다시 만든다(맨 끝: 이 뒤로는 사본을 쓰지 않게)
            string proxy = VProxy.PathFor(moving), made = null, proxyErr = null;
            bool cpuProxy = false;
            try
            {
                if (File.Exists(proxy)) File.Delete(proxy);
                VEncoder.FailHardwareAt = 3;
                made = VProxy.Make(moving, null, () => false);
                cpuProxy = VEncoder.CpuOnly;
            }
            catch (Exception ex)
            {
                proxyErr = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                VEncoder.FailHardwareAt = -1;
                VEncoder.CpuOnly = false;
            }
            var ppr = made != null ? Probe(made) : null;
            Check("미리 보기 사본: 그래픽 인코더가 앞쪽에서 멈추면 CPU 인코더로 다시 만든다", proxyErr == null && cpuProxy && ppr != null && ppr.HasVideo && Near(ppr.Seconds, 5, 0.2), proxyErr ?? Say(ppr));
        }
    }
}
