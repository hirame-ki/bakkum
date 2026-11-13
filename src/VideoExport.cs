// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 내보내기: 정해 둔 모양(릴스·쇼츠·유튜브·카카오톡·용량 맞추기·소리만·표지 그림)으로 만들기 전에 점검하고,
// 화면(VRenderer) + 얹은 것(VOverlayRenderer) + 소리(VMixer)를 윈도우 인코더(H.264·AAC)로 MP4에 담는다.
// 찍은 장소 같은 원본 파일 정보는 새 파일에 옮기지 않는다.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    /// 내보내기 계획(크기·빠르기·용량)
    public sealed class VExportPlan
    {
        /// video · audio · cover
        public string Kind = "video";
        public int W, H;
        public double Fps = 30;
        public uint VideoBps;
        public uint AudioBytesPerSec = 24000;
        public double From, To;
        public string AudioFormat = "m4a";
        public string Output;
        public string Label;

        public double Seconds
        {
            get { return Math.Max(0, To - From); }
        }

        /// 예상 크기(바이트)
        public long EstimatedBytes
        {
            get
            {
                double audio = AudioBytesPerSec;
                if (Kind == "audio" && AudioFormat == "wav") audio = AudioTrack.Rate * 4;
                if (Kind == "cover") return (long)(W * (double)H * 0.6);
                double v = Kind == "video" ? VideoBps / 8.0 : 0;
                return (long)((v + audio) * Seconds * 1.03 + 64 * 1024);
            }
        }
    }

    public sealed class VExportPayload
    {
        public VProject Project;
        public VExportPlan Plan;
    }

    /// 내보내기 전 점검 한 줄
    public sealed class VCheck
    {
        /// stop(못 만듦) · warn(확인해 보세요) · info(알려 드려요)
        public string Level = "warn";
        public string Text;
        /// 그 자리로 가기(초), 없으면 -1
        public double T = -1;
        /// 고치기 버튼: relink · level · aspect916 · subtitle_slow · voice_stale · ai_label
        public string Fix;
    }

    static class VideoExport
    {
        public static readonly string[][] Presets =
        {
            new[] { "same", "그대로" }, new[] { "reels", "릴스" }, new[] { "shorts", "쇼츠" }, new[] { "youtube", "유튜브" },
            new[] { "kakao", "카카오톡" }, new[] { "size", "용량 맞추기" }, new[] { "audio", "소리만" }, new[] { "cover", "표지 그림" }
        };

        public static string PresetLabel(string id)
        {
            foreach (var p in Presets) if (p[0] == id) return p[1];
            return id;
        }

        /// 화질 단계(2026-11-12 사용자 요청: 최대 가능한 화질부터 중간·낮음까지)
        public static readonly string[][] Qualities =
        {
            new[] { "max", "최고" }, new[] { "high", "높음" }, new[] { "mid", "중간" }, new[] { "low", "낮음" }
        };

        /// 모르는 값(예전 편집 등)은 중간
        public static string QualityId(string q)
        {
            foreach (var x in Qualities) if (x[0] == q) return q;
            return "mid";
        }

        /// 화질을 고를 수 있는 내보내기인가(용량 맞추기는 목표 용량이 화질을 정하고, 소리만·표지 그림은 영상이 아니다)
        public static bool HasQuality(string preset)
        {
            return preset != "size" && preset != "audio" && preset != "cover";
        }

        /// 화질별 한 화소·한 장에 쓰는 비트(density)와 초당 비트의 바닥·천장. 중간이 예전 값(0.1, 1.2~24Mbps)과 같다.
        /// 최고는 원본에 가깝게(4K도 60Mbps까지), 낮음은 가볍게.
        public static void QualityRate(string q, out double density, out double floor, out double cap)
        {
            switch (QualityId(q))
            {
                case "max": density = 0.25; floor = 4e6; cap = 60e6; return;
                case "high": density = 0.16; floor = 2e6; cap = 40e6; return;
                case "low": density = 0.06; floor = 0.8e6; cap = 12e6; return;
                default: density = 0.1; floor = 1.2e6; cap = 24e6; return;
            }
        }

        static int SourceShort(VProject p)
        {
            int best = 0;
            foreach (var c in p.Clips)
            {
                if (c.Kind != "video") continue;
                var pr = VMedia.Probe(c.Path);
                if (pr != null) best = Math.Max(best, Math.Min(pr.Width, pr.Height));
            }
            return best > 0 ? best : 1080;
        }

        static double SourceFps(VProject p)
        {
            foreach (var c in p.Clips)
            {
                if (c.Kind != "video") continue;
                var pr = VMedia.Probe(c.Path);
                if (pr != null && pr.Fps > 1) return pr.Fps;
            }
            return 30;
        }

        /// 지금 설정으로 만들 크기·빠르기·용량
        public static VExportPlan Plan(VProject p)
        {
            return Plan(p, p.Export.Quality);
        }

        /// 화질만 바꿔 본 계획(내보내기 칸에서 화질마다 예상 크기를 보여 줄 때)
        public static VExportPlan Plan(VProject p, string quality)
        {
            var e = p.Export;
            var plan = new VExportPlan { Label = PresetLabel(e.Preset) };
            double dur = p.Duration;
            plan.From = 0;
            plan.To = dur;
            if (e.RangeOnly && e.RangeEnd > e.RangeStart + 0.1)
            {
                plan.From = Math.Max(0, Math.Min(dur, e.RangeStart));
                plan.To = Math.Max(plan.From, Math.Min(dur, e.RangeEnd));
            }
            int shortSide;
            double fps = Math.Max(10, Math.Min(60, e.Fps > 0 ? e.Fps : SourceFps(p)));
            double density, floor, cap;
            QualityRate(quality, out density, out floor, out cap);
            // 낮음은 소리도 가볍게(AAC 128kbps). 윈도우 AAC 인코더는 96·128·160·192kbps만 받는다.
            // 화질을 고르지 않는 내보내기(소리만 등)에는 남아 있던 값이 끼어들지 않게 한다.
            if (HasQuality(e.Preset) && QualityId(quality) == "low") plan.AudioBytesPerSec = 16000;
            switch (e.Preset)
            {
                case "reels":
                case "shorts":
                    shortSide = 1080;
                    fps = Math.Min(fps, 60);
                    break;
                case "youtube":
                    shortSide = Math.Max(720, Math.Min(2160, e.Height > 0 ? e.Height : 1080));
                    break;
                case "kakao":
                    shortSide = 720;
                    fps = Math.Min(fps, 30);
                    // 같은 화질에서 3/4로 가볍게(중간이면 예전과 같은 0.075)
                    density *= 0.75;
                    plan.AudioBytesPerSec = 16000;
                    break;
                case "size":
                    {
                        double seconds = Math.Max(1, plan.Seconds);
                        double budget = Math.Max(0.5, e.TargetMb) * 1024 * 1024 * 8 * 0.94 / seconds;
                        plan.AudioBytesPerSec = !HasSound(p) ? 0u : budget < 1.2e6 ? 12000u : 16000u;
                        double video = budget - plan.AudioBytesPerSec * 8;
                        shortSide = video > 3.5e6 ? 1080 : video > 1.5e6 ? 720 : video > 0.7e6 ? 540 : 360;
                        shortSide = Math.Min(shortSide, Math.Max(360, SourceShort(p)));
                        fps = Math.Min(fps, video > 1.5e6 ? 30 : 24);
                        int w0, h0;
                        p.CanvasSize(shortSide, out w0, out h0);
                        plan.W = w0;
                        plan.H = h0;
                        plan.Fps = fps;
                        plan.VideoBps = (uint)Math.Max(150000, video);
                        plan.Kind = "video";
                        plan.Output = OutputFor(p, ".mp4", "_" + Math.Round(e.TargetMb) + "MB");
                        return plan;
                    }
                case "audio":
                    plan.Kind = "audio";
                    plan.AudioFormat = e.AudioFormat == "mp3" || e.AudioFormat == "wav" ? e.AudioFormat : "m4a";
                    plan.Output = OutputFor(p, "." + plan.AudioFormat, "_소리");
                    return plan;
                case "cover":
                    plan.Kind = "cover";
                    p.CanvasSize(Math.Min(1080, SourceShort(p)), out plan.W, out plan.H);
                    plan.From = plan.To = Math.Max(0, Math.Min(dur, e.CoverAt));
                    plan.Output = OutputFor(p, ".png", "_표지");
                    return plan;
                default:
                    shortSide = e.Height > 0 ? Math.Min(e.Height, Math.Max(360, SourceShort(p))) : Math.Min(2160, SourceShort(p));
                    break;
            }
            int w, h;
            p.CanvasSize(shortSide, out w, out h);
            plan.W = w;
            plan.H = h;
            plan.Fps = Math.Round(fps * 1000) / 1000;
            double bps = w * (double)h * plan.Fps * density;
            plan.VideoBps = (uint)Math.Max(floor, Math.Min(cap, bps));
            string tag = e.Preset == "reels" ? "_릴스" : e.Preset == "shorts" ? "_쇼츠" : e.Preset == "youtube" ? "_유튜브" : e.Preset == "kakao" ? "_카톡" : "_편집";
            plan.Output = OutputFor(p, ".mp4", tag);
            return plan;
        }

        public static string OutputFor(VProject p, string ext, string suffix)
        {
            string first = p.Opened.FirstOrDefault(File.Exists) ?? p.Clips.Select(c => c.Path).FirstOrDefault(File.Exists);
            if (first == null) return Util.UniquePath(Path.Combine(Util.OutputDirFor(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "x")), "편집한 영상" + suffix + ext));
            return Conversions.Sibling(first, suffix, ext);
        }

        // ── 점검 ──

        /// 바로 알 수 있는 것들(소리 크기는 Loudness로 따로)
        public static List<VCheck> Checks(VProject p, VExportPlan plan)
        {
            var list = new List<VCheck>();
            double dur = p.Duration;
            if (p.Clips.Count == 0 || dur < 0.2)
            {
                list.Add(new VCheck { Level = "stop", Text = "만들 영상이 없어요. 영상이나 사진을 넣어 주세요." });
                return list;
            }
            foreach (var f in p.Files().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(f)) list.Add(new VCheck { Level = "stop", Text = "원본 파일을 찾지 못했어요: " + Path.GetFileName(f), Fix = "relink" });
                else
                {
                    string why = VMedia.Problem(f);
                    if (why != null) list.Add(new VCheck { Level = "stop", Text = Path.GetFileName(f) + ": " + why });
                }
            }
            if (plan.Kind != "audio")
            {
                bool vertical = p.AspectRatio < 0.8;
                if ((p.Export.Preset == "reels" || p.Export.Preset == "shorts") && Math.Abs(p.AspectRatio - 9.0 / 16) > 0.01)
                    list.Add(new VCheck { Level = "warn", Text = "릴스·쇼츠는 세로(9:16)가 알맞아요. 지금은 " + AspectName(p) + "이에요.", Fix = "aspect916" });
                if (p.Export.Preset == "shorts" && plan.Seconds > 180.5)
                    list.Add(new VCheck { Level = "warn", Text = "쇼츠는 3분까지 올라가요. 지금 길이는 " + VProject.Clock(plan.Seconds, false) + "예요." });
                if (vertical) HiddenZones(p, list);
                Subtitles(p, list);
                OffCanvas(p, list);
            }
            if (plan.Kind == "video")
            {
                long need = plan.EstimatedBytes;
                list.Add(new VCheck { Level = "info", Text = plan.W + "×" + plan.H + " · 초당 " + Math.Round(plan.Fps, 2) + "장 · " + VProject.Clock(plan.Seconds, false) + " · 예상 크기 약 " + Conversions.Size(need) });
                try
                {
                    string root = Path.GetPathRoot(Path.GetFullPath(plan.Output));
                    long free = new DriveInfo(root).AvailableFreeSpace;
                    if (free < need + 300L * 1024 * 1024) list.Add(new VCheck { Level = "stop", Text = "저장할 곳의 빈 자리가 모자라요(남은 자리 " + Conversions.Size(free) + ")." });
                }
                catch (Exception) { }
                if (p.Export.Preset == "size")
                {
                    if (TooBig(p, plan))
                    {
                        double target = Math.Max(0.5, p.Export.TargetMb) * 1024 * 1024;
                        double perSec = (plan.VideoBps / 8.0 + plan.AudioBytesPerSec) * 1.03;
                        int fit = (int)Math.Max(1, (target - 64 * 1024) / perSec / 60);
                        list.Add(new VCheck { Level = "stop", Text = "목표 용량(" + Math.Round(p.Export.TargetMb) + "MB)에 맞추기에는 영상이 너무 길어요. 가장 작게 만들어도 약 " + Conversions.Size(need) + "예요. 목표 용량을 늘리거나, '고른 구간만 내보내기'로 약 " + fit + "분씩 나눠 내보내 주세요." });
                    }
                    else if (plan.VideoBps < 400000)
                        list.Add(new VCheck { Level = "warn", Text = "목표 용량에 비해 영상이 길어서 화면이 많이 뭉개질 수 있어요. 용량을 늘리거나 구간만 내보내 보세요." });
                }
            }
            if (plan.Kind != "cover")
            {
                // 소리만 내보낼 때도 음성은 들어간다
                var stale = StaleVoices(p);
                if (stale != null) list.Add(stale);
                var ai = AiVoice(p, plan.Kind == "audio");
                if (ai != null) list.Add(ai);
                list.Add(new VCheck { Level = "info", Text = "찍은 장소·기기 같은 원본 파일 정보는 새 파일에 담지 않아요." });
            }
            return list;
        }

        /// 자연스러운 목소리(AI)로 만든 음성이 있으면, 듣는 사람이 AI 목소리인 줄 알게 밝혔는지 본다.
        /// 목소리 모델의 사용 조건(Open RAIL-M: 기계가 만든 것을 알리지 않고 퍼뜨리지 않기)이다. 자막 칸과 내보내기 점검이 같이 쓴다.
        /// 영상 설명에 밝힐 수도 있어서 막지는 않는다.
        public static VCheck AiVoice(VProject p, bool audioOnly)
        {
            if (!VAiLabel.Uses(p)) return null;
            if (audioOnly) return new VCheck { Level = "info", Text = "AI 목소리가 들어 있어요. 소리 파일을 나눌 때 'AI 목소리로 만들었다'고 함께 밝혀 주세요(목소리 모델의 사용 조건)." };
            if (VAiLabel.Has(p)) return null;
            return new VCheck { Level = "warn", Text = "AI 목소리를 썼는데 화면에 'AI 목소리' 표시가 없어요. 목소리 모델의 사용 조건이라, 화면이나 영상 설명에 꼭 밝혀 주세요.", Fix = "ai_label" };
        }

        /// 음성으로 바꾼 뒤 글을 고친 자막이 있으면 알린다(그 음성은 고치기 전 글을 읽는다). 자막 칸과 내보내기 점검이 같이 쓴다.
        public static VCheck StaleVoices(VProject p)
        {
            var stale = p.StaleVoiceItems();
            if (stale.Count == 0) return null;
            return new VCheck { Level = "warn", Text = "음성으로 바꾼 뒤 글을 고친 자막이 " + stale.Count + "개 있어요. 그 음성은 고치기 전 글을 읽어요.", T = stale[0].Start, Fix = "voice_stale" };
        }

        /// 용량 맞추기: 영상·소리를 가장 낮게(바닥) 해도 목표 용량을 넘는지(만들어 봐야 소용없다)
        public static bool TooBig(VProject p, VExportPlan plan)
        {
            return p.Export.Preset == "size" && plan.Kind == "video" && plan.EstimatedBytes > Math.Max(0.5, p.Export.TargetMb) * 1024 * 1024;
        }

        static string AspectName(VProject p)
        {
            return p.Aspect == "source" ? "원본 비율" : p.Aspect;
        }

        /// 세로 영상에서 앱 단추·글에 가려지는 곳(대략: 위 12%, 아래 22%, 오른쪽 14%)
        static void HiddenZones(VProject p, List<VCheck> list)
        {
            const double W = 1080, H = 1920;
            var top = new System.Windows.Rect(0, 0, W, H * 0.12);
            var bottom = new System.Windows.Rect(0, H * 0.78, W, H * 0.22);
            var right = new System.Windows.Rect(W * 0.86, H * 0.3, W * 0.14, H * 0.5);
            int n = 0;
            double first = -1;
            foreach (var it in p.Items)
            {
                if (it.Kind == "pip" || it.Kind == "shape" && (it.Shape == "spot" || it.Shape == "ripple")) continue;
                double t = it.Start + Math.Min(0.5, it.Length / 2);
                var st = VAnim.Item(it, t);
                if (!st.Visible) continue;
                var box = VOverlay.Box(it, st, W, H);
                if (box.IntersectsWith(top) || box.IntersectsWith(bottom) || box.IntersectsWith(right))
                {
                    n++;
                    if (first < 0 || it.Start < first) first = it.Start;
                }
            }
            if (n > 0) list.Add(new VCheck { Level = "warn", Text = "글자·그림 " + n + "개가 휴대폰 앱의 단추나 설명 글에 가려질 수 있는 곳에 있어요. '안전 구역'을 켜고 확인해 보세요.", T = first });
        }

        static void Subtitles(VProject p, List<VCheck> list)
        {
            int fast = 0, empty = 0;
            double firstFast = -1, firstEmpty = -1;
            foreach (var it in p.Items.Where(i => i.Kind == "text"))
            {
                string text = (it.Text ?? "").Replace(" ", "").Replace("\n", "").Replace("\r", "");
                if (text.Length == 0)
                {
                    empty++;
                    if (firstEmpty < 0) firstEmpty = it.Start;
                    continue;
                }
                if (!it.Subtitle) continue;
                double cps = text.Length / Math.Max(0.05, it.Length);
                if (cps > 14 || it.Length < 0.6)
                {
                    fast++;
                    if (firstFast < 0) firstFast = it.Start;
                }
            }
            if (fast > 0) list.Add(new VCheck { Level = "warn", Text = "읽기에 너무 빨리 지나가는 자막이 " + fast + "개 있어요(1초에 14자 넘게).", T = firstFast, Fix = "subtitle_slow" });
            if (empty > 0) list.Add(new VCheck { Level = "info", Text = "글자가 비어 있는 자막·글자가 " + empty + "개 있어요.", T = firstEmpty });
        }

        static void OffCanvas(VProject p, List<VCheck> list)
        {
            const double H = 1000;
            double W = H * p.AspectRatio;
            var frame = new System.Windows.Rect(0, 0, W, H);
            foreach (var it in p.Items)
            {
                var st = VAnim.Item(it, it.Start + Math.Min(0.5, it.Length / 2));
                if (!st.Visible) continue;
                var box = VOverlay.Box(it, st, W, H);
                if (!box.IntersectsWith(frame))
                {
                    list.Add(new VCheck { Level = "warn", Text = "화면 밖으로 나가서 보이지 않는 글자·그림이 있어요.", T = it.Start });
                    return;
                }
            }
        }

        /// 소리 크기 점검(시간이 걸린다: 뒤에서 부른다)
        public static List<VCheck> LoudnessChecks(VProject p, Func<bool> stop)
        {
            var list = new List<VCheck>();
            double db, clip;
            bool silent;
            VMixer.Loudness(p, stop, out db, out clip, out silent);
            if (stop != null && stop()) return list;
            if (silent) list.Add(new VCheck { Level = "info", Text = "소리가 없는 영상이에요." });
            else if (db < -32) list.Add(new VCheck { Level = "warn", Text = "소리가 작아요(평균 " + Math.Round(db) + "dB). '크기 맞추기'를 켜면 알맞게 키워요.", Fix = "level" });
            else if (db > -9) list.Add(new VCheck { Level = "warn", Text = "소리가 아주 커요(평균 " + Math.Round(db) + "dB). 원본 소리를 조금 줄여 보세요." });
            if (!silent && clip > 0.002) list.Add(new VCheck { Level = "warn", Text = "소리가 너무 커서 찢어지는 곳이 있어요. 원본·음악 크기를 조금 줄여 보세요." });
            return list;
        }

        // ── 만들기 ──

        public static void Run(Job job)
        {
            var pay = job.Payload as VExportPayload;
            if (pay == null || pay.Project == null || pay.Plan == null) throw new UserFacingException("내보낼 내용이 없어요.");
            var p = pay.Project;
            var plan = pay.Plan;
            if (p.Clips.Count == 0) throw new UserFacingException("만들 영상이 없어요.");
            foreach (var f in p.Files().Distinct(StringComparer.OrdinalIgnoreCase))
                if (!File.Exists(f)) throw new UserFacingException("원본 파일을 찾지 못했어요: " + Path.GetFileName(f));
            Prepare(job, p, plan.Kind != "cover");
            switch (plan.Kind)
            {
                case "audio": Audio(job, p, plan); break;
                case "cover": Cover(job, p, plan); break;
                default: Video(job, p, plan); break;
            }
        }

        /// 소리 풀기·다듬기와 흔들림 재기를 먼저 끝낸다
        static void Prepare(Job job, VProject p, bool sound)
        {
            Func<bool> stop = () => job.Cancelled;
            if (sound)
            {
                var tracks = p.Files().Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !VMedia.IsImage(f)).Select(AudioCache.Get).ToList();
                var procs = p.Clips.Where(VAudioProc.Needs).Where(c => !c.Mute).Select(VAudioProc.Get).ToList();
                while (true)
                {
                    job.Check();
                    int left = tracks.Count(t => !t.Ready && !t.Failed) + procs.Count(x => !x.Ready && !x.Failed);
                    if (left == 0) break;
                    double f = (tracks.Sum(t => t.Ready || t.Failed ? 1 : (double)t.Progress) + procs.Sum(x => x.Ready || x.Failed ? 1 : (double)x.Progress)) / Math.Max(1, tracks.Count + procs.Count);
                    job.Report("소리 준비하는 중 " + Math.Round(f * 100) + "%", f * 0.1);
                    Thread.Sleep(120);
                }
            }
            var stab = p.Clips.Where(c => c.Stabilize && c.Kind == "video").Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var s in stab) VStab.Ensure(s, false, null);
            while (stab.Count > 0 && !stab.All(VStab.Ready))
            {
                job.Check();
                double f = stab.Average(s => (double)VStab.Progress(s));
                job.Report("흔들림 재는 중 " + Math.Round(f * 100) + "%", 0.1 * f);
                Thread.Sleep(150);
            }
        }

        static string Eta(DateTime started, double fraction)
        {
            if (fraction < 0.03) return "";
            double left = (DateTime.Now - started).TotalSeconds * (1 - fraction) / fraction;
            if (left < 60) return " · 남은 시간 약 " + Math.Max(1, (int)Math.Round(left)) + "초";
            return " · 남은 시간 약 " + (int)Math.Round(left / 60) + "분";
        }

        sealed class Frame
        {
            public int Index;
            public double T;
            public byte[] Px;
        }

        static void Video(Job job, VProject p, VExportPlan plan)
        {
            try
            {
                Video(job, p, plan, false);
            }
            catch (HardwareEncoderFailed)
            {
                // 그래픽 인코더가 앞쪽 몇 장에서 멈추면(드라이버·그래픽 메모리·동시 사용 한도) CPU 인코더로 처음부터 다시 만든다.
                // 만들던 파일은 Produce가 지웠지만 인코더가 늦게 놓으면 남아 있을 수 있어 잠깐 기다리며 다시 지운다.
                Log.Write("영상 편집 내보내기: 그래픽 인코더가 멈춰서 CPU 인코더로 다시 만듦");
                for (int i = 0; i < 20 && File.Exists(plan.Output); i++)
                {
                    Conversions.TryDelete(plan.Output);
                    if (File.Exists(plan.Output)) Thread.Sleep(100);
                }
                Video(job, p, plan, true);
            }
        }

        static void Video(Job job, VProject p, VExportPlan plan, bool cpu)
        {
            int W = plan.W, H = plan.H;
            double fps = plan.Fps;
            int frames = Math.Max(1, (int)Math.Ceiling(plan.Seconds * fps - 1e-6));
            bool sound = HasSound(p);
            var started = DateTime.Now;
            string doing = cpu ? "CPU로 다시 만드는 중 " : "영상 만드는 중 ";
            Conversions.Produce(job, plan.Output, delegate (string d)
            {
                var free = new BlockingCollection<byte[]>();
                for (int i = 0; i < 6; i++) free.Add(new byte[W * H * 4]);
                var ready = new BlockingCollection<Frame>(3);
                Exception renderError = null;
                string problem = null;
                var cancel = new CancellationTokenSource();
                var render = new Thread(delegate ()
                {
                    VRenderer r = null;
                    try
                    {
                        r = new VRenderer(W, H, true);
                        r.Use(p, 1);
                        for (int i = 0; i < frames; i++)
                        {
                            if (job.Cancelled) break;
                            double t = plan.From + i / fps;
                            var px = r.Frame(Math.Min(t, plan.To - 1e-4));
                            if (r.Problem != null && problem == null) problem = r.Problem;
                            var buf = free.Take(cancel.Token);
                            Buffer.BlockCopy(px, 0, buf, 0, buf.Length);
                            ready.Add(new Frame { Index = i, T = t, Px = buf }, cancel.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        renderError = ex;
                    }
                    finally
                    {
                        if (r != null) r.Dispose();
                        ready.CompleteAdding();
                    }
                }) { IsBackground = true, Name = "영상 만들기" };
                render.SetApartmentState(ApartmentState.MTA);
                render.Start();
                var enc = new VEncoder(d, W, H, fps, plan.VideoBps, plan.AudioBytesPerSec, sound ? p : null, plan.From, plan.To, () => job.Cancelled) { NoHardware = cpu };
                double blackFrom = -1, blackLong = 0, blackAt = -1;
                try
                {
                    enc.Start();
                    var overlay = new VOverlayRenderer(W, H);
                    int done = 0;
                    foreach (var f in ready.GetConsumingEnumerable())
                    {
                        job.Check();
                        var o = overlay.Render(p, 1, f.T);
                        if (o != null) VPx.Over(f.Px, o);
                        // 검은 화면이 1초 넘게 이어지는 곳
                        if (VPx.MeanLuma(f.Px) < 6)
                        {
                            if (blackFrom < 0) blackFrom = f.T;
                            if (f.T - blackFrom > blackLong)
                            {
                                blackLong = f.T - blackFrom;
                                blackAt = blackFrom;
                            }
                        }
                        else blackFrom = -1;
                        enc.AddVideo(f.Px, f.Index, delegate (byte[] used) { free.Add(used); });
                        done++;
                        if (done % 5 == 0 || done == frames)
                        {
                            double frac = done / (double)frames;
                            job.Report(doing + Math.Round(frac * 100) + "%" + Eta(started, frac), 0.1 + 0.9 * frac);
                        }
                    }
                    job.Check();
                    if (renderError != null)
                    {
                        if (renderError is UserFacingException) throw renderError;
                        Log.Write("영상 편집 내보내기: 화면 만들기 실패 " + renderError.GetType().Name + " @ " + renderError.TargetSite);
                        throw new UserFacingException("화면을 만들지 못했어요. (" + renderError.GetType().Name + ")");
                    }
                    job.Report("마무리하는 중", -1);
                    enc.Finish();
                }
                finally
                {
                    cancel.Cancel();
                    enc.Dispose();
                    render.Join(5000);
                }
                if (problem != null) job.Note(problem);
                if (blackLong >= 1.0 && blackAt > 0.2) job.Note("검은 화면이 " + Math.Round(blackLong, 1) + "초 이어지는 곳이 있어요(" + VProject.Clock(blackAt, false) + ").");
                var made = MediaProbe.Read(d);
                if (made.Seconds > 0.5 && Math.Abs(made.Seconds - plan.Seconds) > Math.Max(1.5, plan.Seconds * 0.05))
                {
                    Log.Write("영상 편집 내보내기: 길이 어긋남 기대 " + plan.Seconds.ToString("0.00") + " 실제 " + made.Seconds.ToString("0.00"));
                    throw new UserFacingException("만든 영상의 길이가 맞지 않아 저장하지 않았어요.");
                }
                job.Note(Path.GetFileName(d) + ": " + plan.W + "×" + plan.H + ", " + VProject.Clock(plan.Seconds, false) + ", " + Conversions.Size(new FileInfo(d).Length) + (enc.Hardware ? "" : " (그래픽 인코더 없이 만듦)"));
            });
        }

        static bool HasSound(VProject p)
        {
            if (p.Sounds.Count > 0) return true;
            if (p.Items.Any(i => i.Kind == "pip" && !i.Mute)) return true;
            foreach (var c in p.Clips)
            {
                if (c.Kind != "video" || c.Mute) continue;
                var pr = VMedia.Probe(c.Path);
                if (pr != null && pr.HasAudio) return true;
            }
            return false;
        }

        static void Audio(Job job, VProject p, VExportPlan plan)
        {
            if (!HasSound(p)) throw new UserFacingException("이 영상에는 소리가 없어요.");
            var started = DateTime.Now;
            Conversions.Produce(job, plan.Output, delegate (string d)
            {
                var mixer = new VMixer { Wait = true, Stop = () => job.Cancelled };
                mixer.Use(p);
                const int Chunk = AudioTrack.Rate / 2;
                var buf = new float[Chunk * 2];
                long total = (long)Math.Round(plan.Seconds * AudioTrack.Rate);
                if (plan.AudioFormat == "wav")
                {
                    using (var w = new VWav.Writer(d))
                    {
                        for (long done = 0; done < total; done += Chunk)
                        {
                            job.Check();
                            int n = (int)Math.Min(Chunk, total - done);
                            mixer.Mix(plan.From + done / (double)AudioTrack.Rate, n, buf);
                            w.Add(VDsp.ToShorts(buf, n * 2), n * 2);
                            double frac = (done + n) / (double)total;
                            job.Report("소리 만드는 중 " + Math.Round(frac * 100) + "%" + Eta(started, frac), frac);
                        }
                    }
                    return;
                }
                WinRtCall.Mta<bool>(delegate
                {
                    IMFSinkWriter writer = null;
                    IMFAttributes a = null;
                    IMFMediaType output = null, input = null;
                    try
                    {
                        a = Mf.Attributes(1);
                        a.SetGUID(Mf.Container, plan.AudioFormat == "mp3" ? Mf.ContainerMp3 : Mf.ContainerMp4);
                        writer = Mf.SinkWriter(Path.GetFullPath(d), a);
                        output = Mf.MediaType();
                        output.SetGUID(Mf.MajorType, Mf.Audio);
                        output.SetUINT32(Mf.SampleRate, AudioTrack.Rate);
                        output.SetUINT32(Mf.Channels, 2);
                        if (plan.AudioFormat == "mp3")
                        {
                            output.SetGUID(Mf.Subtype, Mf.Mp3);
                            output.SetUINT32(Mf.BytesPerSecond, 24000);
                        }
                        else
                        {
                            output.SetGUID(Mf.Subtype, Mf.Aac);
                            output.SetUINT32(Mf.BitsPerSample, 16);
                            output.SetUINT32(Mf.BytesPerSecond, 24000);
                        }
                        uint stream;
                        writer.AddStream(output, out stream);
                        input = PcmType();
                        writer.SetInputMediaType(stream, input, null);
                        writer.BeginWriting();
                        for (long done = 0; done < total; done += Chunk)
                        {
                            job.Check();
                            int n = (int)Math.Min(Chunk, total - done);
                            mixer.Mix(plan.From + done / (double)AudioTrack.Rate, n, buf);
                            WritePcm(writer, stream, buf, n, done);
                            double frac = (done + n) / (double)total;
                            job.Report("소리 만드는 중 " + Math.Round(frac * 100) + "%" + Eta(started, frac), frac);
                        }
                        writer.Finish();
                    }
                    finally
                    {
                        Mf.Release(input);
                        Mf.Release(output);
                        Mf.Release(a);
                        Mf.Release(writer);
                    }
                    return true;
                });
            });
        }

        internal static IMFMediaType PcmType()
        {
            var input = Mf.MediaType();
            input.SetGUID(Mf.MajorType, Mf.Audio);
            input.SetGUID(Mf.Subtype, Mf.Pcm);
            input.SetUINT32(Mf.SampleRate, AudioTrack.Rate);
            input.SetUINT32(Mf.Channels, 2);
            input.SetUINT32(Mf.BitsPerSample, 16);
            input.SetUINT32(Mf.BlockAlign, 4);
            input.SetUINT32(Mf.BytesPerSecond, AudioTrack.Rate * 4);
            return input;
        }

        internal static void WritePcm(IMFSinkWriter writer, uint stream, float[] buf, int n, long frame0)
        {
            var pcm = VDsp.ToShorts(buf, n * 2);
            var mb = Mf.Buffer(n * 4);
            try
            {
                IntPtr ptr;
                uint max, cur;
                mb.Lock(out ptr, out max, out cur);
                try { Marshal.Copy(pcm, 0, ptr, n * 2); }
                finally { mb.Unlock(); }
                mb.SetCurrentLength((uint)(n * 4));
                var sample = Mf.Sample(mb, (long)Math.Round(frame0 * 1e7 / AudioTrack.Rate), (long)Math.Round(n * 1e7 / AudioTrack.Rate));
                try { writer.WriteSample(stream, sample); }
                finally { Mf.Release(sample); }
            }
            finally
            {
                Mf.Release(mb);
            }
        }

        static void Cover(Job job, VProject p, VExportPlan plan)
        {
            job.Report("표지 그림 만드는 중", -1);
            byte[] px = null;
            string problem = null;
            WinRtCall.Mta<bool>(delegate
            {
                using (var r = new VRenderer(plan.W, plan.H, true))
                {
                    r.Use(p, 1);
                    px = (byte[])r.Frame(plan.From).Clone();
                    problem = r.Problem;
                }
                return true;
            });
            if (problem != null) job.Note(problem);
            var o = new VOverlayRenderer(plan.W, plan.H).Render(p, 1, plan.From);
            if (o != null) VPx.Over(px, o);
            var bmp = BitmapSource.Create(plan.W, plan.H, 96, 96, PixelFormats.Bgr32, null, px, plan.W * 4);
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Conversions.Produce(job, plan.Output, delegate (string d)
            {
                using (var f = File.Create(d)) enc.Save(f);
            });
        }
    }

    /// 그래픽 인코더가 앞쪽 몇 장을 쓰다 멈췄다(CPU 인코더로 처음부터 다시 만들 수 있다)
    sealed class HardwareEncoderFailed : UserFacingException
    {
        public HardwareEncoderFailed() : base("영상을 저장하지 못했어요. (그래픽 인코더)") { }
    }

    /// H.264 + AAC 저장. 화면은 받는 대로, 소리는 화면보다 조금 앞서 섞어 넣는다(MTA 일꾼 스레드).
    sealed class VEncoder : IDisposable
    {
        sealed class Item
        {
            public byte[] Px;
            public int Index;
            public Action<byte[]> Done;
        }

        readonly string path;
        readonly int W, H;
        readonly double fps;
        readonly uint videoBps, audioBytes;
        readonly VProject soundOf;
        readonly double from, to;
        readonly Func<bool> stop;
        readonly BlockingCollection<Item> queue = new BlockingCollection<Item>(3);
        Thread thread;
        Exception error;
        readonly ManualResetEvent opened = new ManualResetEvent(false);
        public bool Hardware;
        /// 0이 아니면 이 장 수마다 한 번은 통째 그림(키 프레임)을 넣는다(미리 보기 사본: 어디로 넘겨도 빨리 풀리게)
        /// 통째 그림(키프레임) 간격(장). 0이면 인코더 기본값(이 PC의 하드웨어 인코더는 128장).
        public uint KeyEvery;
        /// 인코더가 간격 설정을 받았는지(시험용)
        public bool KeyEveryTaken;
        /// 시험용: 압축 방식(기본 H.264). 진짜 HEVC 시험 영상을 만들 때 바꾼다.
        public Guid Codec = Mf.H264;
        /// 시험용: 그래픽 인코더가 이 장(번호)을 쓸 때 멈춘 것처럼 한다. -1이면 끔.
        internal static int FailHardwareAt = -1;
        /// 그래픽 인코더가 이 장 수를 쓰기 전에 멈추면 HardwareEncoderFailed(처음부터 다시 만들 만큼 앞쪽)
        const int EarlyFrames = 60;
        int written;
        static DateTime cpuUntil;

        /// 그래픽 인코더가 멈춘 적이 있으면 하루 동안(또는 바꿈을 다시 켤 때까지) 내보내기·사본·화면 녹화를 CPU 인코더로 한다
        internal static bool CpuOnly
        {
            get { return DateTime.Now < cpuUntil; }
            set { cpuUntil = value ? DateTime.Now.AddDays(1) : DateTime.MinValue; }
        }

        /// 인코더가 낸 오류인지(그래픽 인코더 탓으로 볼 것). 파일 쪽 오류(0x8007xxxx: USB 뽑힘·네트워크 폴더 끊김·디스크 가득)는 빼고, 메모리 부족은 넣는다.
        internal static bool GpuFault(Exception e)
        {
            if (!(e is COMException || e is OutOfMemoryException)) return false;
            int h = e.HResult;
            return (h & unchecked((int)0xFFFF0000)) != unchecked((int)0x80070000) || h == unchecked((int)0x8007000E);
        }

        /// 시험용: CPU 인코더만 쓴다(그래픽 인코더가 없는 PC 흉내).
        public bool NoHardware;
        static readonly Guid MaxKeyframeSpacing = new Guid("c16eb52b-73a1-476f-8d62-839d6a020652");

        public VEncoder(string path, int w, int h, double fps, uint videoBps, uint audioBytes, VProject soundOf, double from, double to, Func<bool> stop)
        {
            this.path = path;
            W = w;
            H = h;
            this.fps = fps;
            this.videoBps = videoBps;
            this.audioBytes = audioBytes;
            this.soundOf = soundOf;
            this.from = from;
            this.to = to;
            this.stop = stop;
        }

        public void Start()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "영상 저장" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            opened.WaitOne();
            Throw();
        }

        void Throw()
        {
            var e = error;
            if (e == null) return;
            if (e is UserFacingException || e is OperationCanceledException) throw e;
            Log.Write("영상 편집 저장 실패: " + e.GetType().Name + " 0x" + e.HResult.ToString("X8"));
            if (e.HResult == unchecked((int)0x80070070)) throw new UserFacingException("디스크 공간이 모자라 저장하지 못했어요.");
            if (Hardware && GpuFault(e))
            {
                CpuOnly = true;
                if (written < EarlyFrames) throw new HardwareEncoderFailed();
                throw new UserFacingException("그래픽 인코더가 멈춰서 영상을 저장하지 못했어요. 다시 내보내면 그래픽 인코더 없이 만들어요.");
            }
            throw new UserFacingException("영상을 저장하지 못했어요. (" + e.GetType().Name + ")");
        }

        public void AddVideo(byte[] px, int index, Action<byte[]> done)
        {
            Throw();
            while (!queue.TryAdd(new Item { Px = px, Index = index, Done = done }, 200))
            {
                Throw();
                if (stop()) throw new OperationCanceledException();
            }
        }

        public void Finish()
        {
            queue.CompleteAdding();
            if (thread != null) thread.Join();
            Throw();
        }

        IMFSinkWriter Open(bool hardware, out uint video, out uint audio)
        {
            video = audio = 0;
            IMFAttributes a = Mf.Attributes(3);
            IMFSinkWriter w = null;
            bool ok = false;
            IMFMediaType target = null, input = null, at = null, ai = null;
            try
            {
                a.SetGUID(Mf.Container, Mf.ContainerMp4);
                a.SetUINT32(Mf.HardwareTransforms, hardware ? 1u : 0u);
                w = Mf.SinkWriter(Path.GetFullPath(path), a);
                target = Mf.MediaType();
                target.SetGUID(Mf.MajorType, Mf.Video);
                target.SetGUID(Mf.Subtype, Codec);
                target.SetUINT32(Mf.AvgBitrate, videoBps);
                if (Codec == Mf.H264) target.SetUINT32(Mf.Profile, 100);
                if (KeyEvery > 0) target.SetUINT32(MaxKeyframeSpacing, KeyEvery);
                Describe(target);
                w.AddStream(target, out video);
                input = Mf.MediaType();
                input.SetGUID(Mf.MajorType, Mf.Video);
                input.SetGUID(Mf.Subtype, Mf.Rgb32);
                Describe(input);
                input.SetUINT32(Mf.Stride, (uint)(W * 4));
                Mf.SetVideoInput(w, video, input, Codec == Mf.H264, KeyEvery);
                // 미디어 형식의 간격 값은 하드웨어 인코더가 무시해서(11-12 실측) 인코더에 직접 넣는다.
                // 형식과 함께 넘긴 값은 NVIDIA·CPU 인코더가, 지금 넣는 값은 인텔 그래픽 인코더가 지켰다(11-12). 둘 다 한다.
                if (KeyEvery > 0) KeyEveryTaken = Mf.SetCodecValue(w, video, Mf.GopSize, KeyEvery);
                if (soundOf != null)
                {
                    at = Mf.MediaType();
                    at.SetGUID(Mf.MajorType, Mf.Audio);
                    at.SetGUID(Mf.Subtype, Mf.Aac);
                    at.SetUINT32(Mf.SampleRate, AudioTrack.Rate);
                    at.SetUINT32(Mf.Channels, 2);
                    at.SetUINT32(Mf.BitsPerSample, 16);
                    at.SetUINT32(Mf.BytesPerSecond, audioBytes);
                    w.AddStream(at, out audio);
                    ai = VideoExport.PcmType();
                    w.SetInputMediaType(audio, ai, null);
                }
                w.BeginWriting();
                ok = true;
                return w;
            }
            finally
            {
                Mf.Release(target);
                Mf.Release(input);
                Mf.Release(at);
                Mf.Release(ai);
                Mf.Release(a);
                if (!ok) Mf.Release(w);
            }
        }

        void Describe(IMFMediaType t)
        {
            t.SetUINT64(Mf.FrameSize, ((ulong)(uint)W << 32) | (uint)H);
            ulong num = (ulong)Math.Round(fps * 1000), den = 1000;
            if (Math.Abs(fps - 30000.0 / 1001) < 0.01) { num = 30000; den = 1001; }
            else if (Math.Abs(fps - 60000.0 / 1001) < 0.01) { num = 60000; den = 1001; }
            else if (Math.Abs(fps - 24000.0 / 1001) < 0.01) { num = 24000; den = 1001; }
            else if (Math.Abs(fps - Math.Round(fps)) < 0.001) { num = (ulong)Math.Round(fps); den = 1; }
            t.SetUINT64(Mf.FrameRate, (num << 32) | den);
            t.SetUINT64(Mf.PixelAspect, (1UL << 32) | 1u);
            t.SetUINT32(Mf.Interlace, 2);
        }

        void Loop()
        {
            IMFSinkWriter w = null;
            uint vs = 0, aus = 0;
            VMixer mixer = null;
            float[] mix = null;
            long audioDone = 0;
            long audioTotal = (long)Math.Round((to - from) * AudioTrack.Rate);
            const int Chunk = AudioTrack.Rate / 10;
            try
            {
                if (NoHardware || CpuOnly) w = Open(false, out vs, out aus);
                else
                {
                    try
                    {
                        w = Open(true, out vs, out aus);
                        Hardware = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("영상 편집 저장: 그래픽 인코더로 열지 못함 " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                        try { if (File.Exists(path)) File.Delete(path); } catch { }
                        w = Open(false, out vs, out aus);
                    }
                }
                if (soundOf != null)
                {
                    mixer = new VMixer { Wait = true, Stop = stop };
                    mixer.Use(soundOf);
                    mix = new float[Chunk * 2];
                }
                opened.Set();
                int bytes = W * H * 4;
                foreach (var it in queue.GetConsumingEnumerable())
                {
                    long t0 = (long)Math.Round(it.Index * 1e7 / fps);
                    long t1 = (long)Math.Round((it.Index + 1) * 1e7 / fps);
                    // 소리는 화면보다 0.5초 앞서 넣는다
                    if (mixer != null)
                    {
                        long want = Math.Min(audioTotal, (long)((t1 / 1e7 + 0.5) * AudioTrack.Rate));
                        while (audioDone < want)
                        {
                            int n = (int)Math.Min(Chunk, want - audioDone);
                            mixer.Mix(from + audioDone / (double)AudioTrack.Rate, n, mix);
                            VideoExport.WritePcm(w, aus, mix, n, audioDone);
                            audioDone += n;
                        }
                    }
                    var mb = Mf.Buffer(bytes);
                    try
                    {
                        IntPtr ptr;
                        uint max, cur;
                        mb.Lock(out ptr, out max, out cur);
                        try { Marshal.Copy(it.Px, 0, ptr, bytes); }
                        finally { mb.Unlock(); }
                        mb.SetCurrentLength((uint)bytes);
                        if (it.Done != null) it.Done(it.Px);
                        var sample = Mf.Sample(mb, t0, t1 - t0);
                        try
                        {
                            if (Hardware && it.Index == FailHardwareAt) throw new COMException("시험: 그래픽 인코더가 멈춤", unchecked((int)0x80004005));
                            w.WriteSample(vs, sample);
                            written++;
                        }
                        finally { Mf.Release(sample); }
                    }
                    finally
                    {
                        Mf.Release(mb);
                    }
                }
                if (stop()) throw new OperationCanceledException();
                while (mixer != null && audioDone < audioTotal)
                {
                    int n = (int)Math.Min(Chunk, audioTotal - audioDone);
                    mixer.Mix(from + audioDone / (double)AudioTrack.Rate, n, mix);
                    VideoExport.WritePcm(w, aus, mix, n, audioDone);
                    audioDone += n;
                }
                w.Finish();
            }
            catch (Exception ex)
            {
                if (error == null) error = ex;
                // 받는 쪽이 멈추지 않게 남은 것을 비운다
                Item rest;
                while (queue.TryTake(out rest))
                {
                    if (rest.Done != null) rest.Done(rest.Px);
                }
            }
            finally
            {
                opened.Set();
                Mf.Release(w);
            }
        }

        public void Dispose()
        {
            if (!queue.IsAddingCompleted) queue.CompleteAdding();
            if (thread != null && thread.IsAlive) thread.Join(10000);
        }
    }
}
