// 도구 시험 중 영상·소리 부분. 시험용 파일은 색띠 영상과 삐 소리뿐이다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    static partial class ToolTest
    {
        static string M(string name)
        {
            return Path.Combine(P("mediajob"), name);
        }

        static MediaProbe Probe(string path)
        {
            try
            {
                return path == "" || !File.Exists(path) ? null : MediaProbe.Read(path);
            }
            catch (Exception ex)
            {
                Cli.Print("    읽기 실패: " + ex.Message);
                return null;
            }
        }

        static string Say(MediaProbe p)
        {
            return p == null ? "없음" : (p.HasVideo ? p.Width + "×" + p.Height + " " + p.Fps.ToString("0.#") + "fps " : "") + (p.HasAudio ? p.AudioRate + "Hz " + p.Channels + "ch " : "") + p.Seconds.ToString("0.00") + "초";
        }

        static bool Near(double a, double b, double within)
        {
            return Math.Abs(a - b) <= within;
        }

        static void MediaTests()
        {
            Cli.Print("[영상·소리]");
            string src = P("media");
            if (!Directory.Exists(src))
            {
                Check("시험용 파일이 있다", false, src);
                return;
            }
            string w = P("mediajob");
            if (Directory.Exists(w)) Directory.Delete(w, true);
            Directory.CreateDirectory(w);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(w, Path.GetFileName(f)));
            File.Copy(Path.Combine(src, "v.mp4"), M("이름 #1 100%.mp4"));

            double t;
            Check("시간 읽기", MediaJobs.ParseTime("90", out t) && t == 90 && MediaJobs.ParseTime("1:30", out t) && t == 90 && MediaJobs.ParseTime("01:02:03", out t) && t == 3723 && MediaJobs.ParseTime("0:01.5", out t) && t == 1.5 && !MediaJobs.ParseTime("1:75", out t) && !MediaJobs.ParseTime("가", out t) && !MediaJobs.ParseTime("", out t) && !MediaJobs.ParseTime("1:2:3:4", out t), "");
            Check("시간 적기", MediaJobs.TimeText(83) == "1:23" && MediaJobs.TimeText(3725) == "1:02:05", MediaJobs.TimeText(3725));

            var v = Probe(M("v.mp4"));
            Check("영상 읽기: 크기·길이·소리", v != null && v.HasVideo && v.HasAudio && v.Width == 320 && v.Height == 240 && Near(v.Fps, 15, 0.01) && Near(v.Seconds, 2, 0.1) && v.AudioRate == 44100, Say(v));
            var a = Probe(M("a.mp3"));
            Check("소리 읽기", a != null && !a.HasVideo && a.HasAudio && Near(a.Seconds, 2, 0.15), Say(a));
            var muted = Probe(M("mute.mp4"));
            Check("소리 없는 영상", muted != null && muted.HasVideo && !muted.HasAudio, Say(muted));
            Check("이름에 #·%가 있어도 읽는다", Probe(M("이름 #1 100%.mp4")) != null, "");

            Func<string, string> opts = delegate (string name) { return string.Join(",", ActionOf("media").OptionsFor(new[] { M(name) }).Select(o => o.Id)); };
            Check("고를 거리: 영상", opts("v.mp4") == "compress,trim,gif,mov,mkv,mp3,wav", opts("v.mp4"));
            Check("고를 거리: AVI에서는 MP4도", opts("v.avi") == "mp4,compress,trim,gif,mov,mkv,mp3,wav", opts("v.avi"));
            Check("고를 거리: 소리", opts("a.mp3") == "m4a,wav,flac,trim" && opts("a.ogg") == "mp3,m4a,wav,flac,trim", opts("a.ogg"));
            Check("영상·소리는 영상 칸과 압축 칸에만", ActionOf("media").Usable(new[] { M("v.mp4") }).Count == 1 && ActionOf("archive").Usable(new[] { M("v.mp4") }).Count == 1 && ActionOf("pdf").Usable(new[] { M("v.mp4") }).Count == 0 && ActionOf("ai").Usable(new[] { M("a.mp3") }).Count == 0, "");

            // 들어오는 형식마다
            foreach (string name in new[] { "v.mov", "v.mkv", "v.avi", "v.wmv", "v.webm", "v.mpg", "v.m4v", "v.ts", "v.m2ts", "v.3gp" })
            {
                var j = RunJob("media", "mp4", new[] { M(name) }, null);
                var made = Probe(Out1(j));
                // .ts·.m2ts는 시작 시각이 0이 아니어서 결과가 0.9초쯤 길게 나온다(2026-11-08 실측).
                bool ok = made != null && made.HasVideo && made.Width == 320 && Near(made.Seconds, 2, name.EndsWith("ts") ? 1.5 : 0.7) && j.Problems.Count == 0;
                Check(name + "→MP4", ok, ok ? Say(made) : string.Join(" / ", j.Problems));
                Cli.Print("    " + name + ": " + (ok ? "됨 " + Say(made) : "안 됨 " + Say(made) + " " + string.Join(" / ", j.Problems)));
            }

            var mov = RunJob("media", "mov", new[] { M("v.mp4") }, null);
            var pmov = Probe(Out1(mov));
            Check("MP4→MOV", pmov != null && pmov.HasVideo && pmov.HasAudio && Near(pmov.Seconds, 2, 0.3), Say(pmov) + " " + string.Join(" / ", mov.Problems));
            var mkv = RunJob("media", "mkv", new[] { M("v.mp4") }, null);
            var pmkv = Probe(Out1(mkv));
            Check("MP4→MKV", pmkv != null && pmkv.HasVideo && pmkv.HasAudio && Near(pmkv.Seconds, 2, 0.3), Say(pmkv) + " " + string.Join(" / ", mkv.Problems));
            var mkv2 = RunJob("media", "mp4", new[] { Out1(mkv) }, null);
            Check("만든 MKV를 다시 MP4로", Probe(Out1(mkv2)) != null, string.Join(" / ", mkv2.Problems));
            var same = RunJob("media", "mp4", new[] { M("v.mp4") }, null);
            Check("같은 형식이면 건너뛴다", same.Outputs.Count == 0 && same.Notes.Any(n => n.Contains("이미")), "");

            // 소리
            foreach (string target in new[] { "mp3", "wav" })
            {
                var j = RunJob("media", target, new[] { M("v.mp4") }, null);
                var made = Probe(Out1(j));
                Check("영상→소리만 " + target, made != null && !made.HasVideo && made.HasAudio && Near(made.Seconds, 2, 0.3), Say(made) + " " + string.Join(" / ", j.Problems));
            }
            var noSound = RunJob("media", "mp3", new[] { M("mute.mp4") }, null);
            Check("소리 없는 영상은 알려 준다", noSound.Outputs.Count == 0 && noSound.Problems.Any(x => x.Contains("소리가 없어요")), string.Join(" / ", noSound.Problems));
            foreach (string name in new[] { "a.wav", "a.m4a", "a.aac", "a.flac", "a.ogg", "a.opus", "a.wma" })
            {
                var j = RunJob("media", "mp3", new[] { M(name) }, null);
                var made = Probe(Out1(j));
                bool ok = made != null && made.HasAudio && Near(made.Seconds, 2, 0.4);
                Check(name + "→MP3", ok, ok ? Say(made) : string.Join(" / ", j.Problems));
                Cli.Print("    " + name + ": " + (ok ? "됨 " + Say(made) : "안 됨 " + string.Join(" / ", j.Problems)));
            }
            foreach (string target in new[] { "m4a", "wav", "flac" })
            {
                var j = RunJob("media", target, new[] { M("a.mp3") }, null);
                var made = Probe(Out1(j));
                Check("MP3→" + target, made != null && made.HasAudio && Near(made.Seconds, 2, 0.3), Say(made) + " " + string.Join(" / ", j.Problems));
            }

            // 자르기
            var cut = RunJob("media", "trim", new[] { M("v.mp4") }, null, "start=0.5", "end=1:50".Replace("1:50", "1.5"));
            var pcut = Probe(Out1(cut));
            Check("영상 자르기: 1초만 남는다", pcut != null && Near(pcut.Seconds, 1, 0.25) && Out1(cut).EndsWith("v_잘라냄.mp4"), Say(pcut) + " " + string.Join(" / ", cut.Problems));
            var cutA = RunJob("media", "trim", new[] { M("a.wav") }, null, "start=1");
            var pcutA = Probe(Out1(cutA));
            Check("소리 자르기: 뒤쪽 1초", pcutA != null && Near(pcutA.Seconds, 1, 0.2) && Out1(cutA).EndsWith(".wav"), Say(pcutA) + " " + string.Join(" / ", cutA.Problems));
            var cutB = RunJob("media", "trim", new[] { M("a.ogg") }, null, "end=1");
            Check("OGG 자르면 M4A로", Out1(cutB).EndsWith(".m4a") && Near(Probe(Out1(cutB)).Seconds, 1, 0.25), Out1(cutB) + " " + string.Join(" / ", cutB.Problems));
            var cutBad = RunJob("media", "trim", new[] { M("v.mp4") }, null, "start=9");
            Check("길이를 넘는 구간은 알려 준다", cutBad.Outputs.Count == 0 && cutBad.Problems.Any(x => x.Contains("전체 길이")), string.Join(" / ", cutBad.Problems));

            // 줄이기
            long bigBefore = new FileInfo(M("big.mp4")).Length;
            var low = RunJob("media", "compress", new[] { M("big.mp4") }, null, "level=hard");
            var plow = Probe(Out1(low));
            Check("용량 줄이기: 짧은 변 480, 훨씬 작게", plow != null && plow.Height == 480 && plow.Width == 852 && new FileInfo(Out1(low)).Length < bigBefore * 0.4 && Out1(low).EndsWith("big_줄임.mp4"), Say(plow) + " " + (Out1(low) == "" ? "" : Conversions.Size(new FileInfo(Out1(low)).Length)) + "/" + Conversions.Size(bigBefore) + " " + string.Join(" / ", low.Problems));
            var odd = RunJob("media", "mp4", new[] { M("odd.avi") }, null);
            var podd = Probe(Out1(odd));
            Check("홀수 크기는 짝수로 맞춘다", podd != null && podd.Width == 322 && podd.Height == 242, Say(podd) + " " + string.Join(" / ", odd.Problems));

            // GIF
            var gif = RunJob("media", "gif", new[] { M("v.mp4") }, null, "width=160", "fps=5");
            string gp = Out1(gif);
            Check("GIF 만들기", gp.EndsWith(".gif") && gif.Problems.Count == 0, string.Join(" / ", gif.Problems));
            if (gp != "")
            {
                var d = new GifBitmapDecoder(new Uri(gp), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check("GIF 프레임 수와 크기", d.Frames.Count >= 9 && d.Frames.Count <= 11 && d.Frames[0].PixelWidth == 160 && d.Frames[0].PixelHeight == 120, d.Frames.Count + "장 " + d.Frames[0].PixelWidth + "×" + d.Frames[0].PixelHeight);
            }
            var gifCut = RunJob("media", "gif", new[] { M("v.mp4") }, null, "start=1", "fps=4", "width=64");
            if (Out1(gifCut) != "")
            {
                var d = new GifBitmapDecoder(new Uri(Out1(gifCut)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check("GIF 구간", d.Frames.Count >= 3 && d.Frames.Count <= 5, d.Frames.Count + "장");
            }
            var rot = Probe(M("rot.mp4"));
            Check("돌려 찍은 영상의 방향을 읽는다", rot != null && rot.Rotation != 0, rot == null ? "" : rot.Rotation + "도");
            var gifRot = RunJob("media", "gif", new[] { M("rot.mp4") }, null, "fps=2", "width=120");
            if (Out1(gifRot) != "")
            {
                var d = new GifBitmapDecoder(new Uri(Out1(gifRot)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check("GIF는 돌린 대로", d.Frames[0].PixelWidth == 120 && d.Frames[0].PixelHeight == 160, d.Frames[0].PixelWidth + "×" + d.Frames[0].PixelHeight);
            }
            var rmp4 = RunJob("media", "mp3", new[] { M("rot.mp4") }, null);
            Check("돌린 영상도 소리를 뽑는다", Probe(Out1(rmp4)) != null, "");
            File.WriteAllBytes(M("깨짐.mp4"), new byte[4000]);
            var broken = RunJob("media", "mov", new[] { M("깨짐.mp4") }, null);
            Check("깨진 파일은 알려 준다", broken.Outputs.Count == 0 && broken.Problems.Count == 1, string.Join(" / ", broken.Problems));
        }
    }
}
