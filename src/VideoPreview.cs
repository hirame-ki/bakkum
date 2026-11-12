// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 미리 보기: 화면은 한 일꾼(VRenderer)이 그리고, 소리는 다른 일꾼(VWaveOut + VMixer)이 낸다.
// 재생 중에는 소리 장치가 실제로 낸 칸 수가 시계다(소리 장치가 없으면 흐른 시간). 화면이 늦으면 그 사이 장면은 건너뛴다.
// 멈춰 있을 때는 마지막으로 고른 시각만 그린다(빨리 끌어도 밀리지 않게).
using System;
using System.Threading;

namespace Bakkum
{
    sealed class VPreview : IDisposable
    {
        readonly object gate = new object();
        VProject project, before;
        int version;
        int boxW = 960, boxH = 540;
        double want;
        volatile bool playing;
        double playFrom, playEnd = -1, loopFrom = -1;
        long epoch, playedOffset;
        volatile bool closing;
        VWaveOut wave;
        readonly Thread videoThread, audioThread;
        readonly AutoResetEvent wakeVideo = new AutoResetEvent(false), wakeAudio = new AutoResetEvent(false);

        /// 소리 끄고 보기
        public volatile bool MuteAudio;
        /// 가볍게 보기(긴 변 640까지)
        public volatile bool Light;
        /// 아직 준비되지 않은 소리가 있었음
        public volatile bool AudioPending;

        // 내놓은 장면
        public readonly object FrameLock = new object();
        public byte[] Frame;
        public int FrameW, FrameH, FrameSerial;
        public double FrameT = -1;
        public string Problem;
        /// 새 장면(일꾼 스레드에서 부른다)
        public Action FrameReady;
        /// 끝까지 재생함(일꾼 스레드에서 부른다)
        public Action Ended;

        public VPreview()
        {
            videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "미리 보기 화면" };
            videoThread.SetApartmentState(ApartmentState.MTA);
            audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "미리 보기 소리" };
            audioThread.SetApartmentState(ApartmentState.MTA);
            videoThread.Start();
            audioThread.Start();
        }

        // ── 바깥에서 부르는 것(화면 스레드) ──

        /// 새 내용(복사본). 바뀐 내용으로 다시 그린다.
        public void SetProject(VProject snapshot, int ver)
        {
            lock (gate)
            {
                project = snapshot;
                version = ver;
            }
            wakeVideo.Set();
        }

        /// 보정 전 모습을 왼쪽 반에 나란히(null이면 끄기)
        public void Compare(VProject original)
        {
            lock (gate) before = original;
            wakeVideo.Set();
        }

        /// 그릴 자리 크기(화면 픽셀)
        public void SetBox(int w, int h)
        {
            lock (gate)
            {
                boxW = Math.Max(64, w);
                boxH = Math.Max(64, h);
            }
            wakeVideo.Set();
        }

        public bool Playing
        {
            get { return playing; }
        }

        /// 지금 보여 줄 시각
        public double Now
        {
            get
            {
                lock (gate) return NowLocked();
            }
        }

        double NowLocked()
        {
            if (!playing) return want;
            var w = wave;
            long played = w == null ? 0 : w.Played;
            return playFrom + Math.Max(0, played - playedOffset) / (double)AudioTrack.Rate;
        }

        public void Seek(double t)
        {
            lock (gate)
            {
                want = Math.Max(0, t);
                if (playing)
                {
                    playFrom = want;
                    playedOffset = wave == null ? 0 : wave.Played;
                    epoch++;
                }
            }
            wakeVideo.Set();
            wakeAudio.Set();
        }

        /// from부터 재생. end > 0이면 거기서 멈춘다(loop면 from으로 돌아간다).
        public void Play(double from, double end, bool loop)
        {
            lock (gate)
            {
                want = Math.Max(0, from);
                playFrom = want;
                playEnd = end;
                loopFrom = loop ? want : -1;
                playedOffset = wave == null ? 0 : wave.Played;
                epoch++;
                playing = true;
            }
            wakeVideo.Set();
            wakeAudio.Set();
        }

        public void Pause()
        {
            lock (gate)
            {
                if (!playing) return;
                want = NowLocked();
                playing = false;
                epoch++;
            }
            wakeVideo.Set();
            wakeAudio.Set();
        }

        // ── 화면 일꾼 ──

        static void SizeFor(VProject p, int bw, int bh, bool light, out int w, out int h)
        {
            double a = p.AspectRatio;
            double fw = bw, fh = bw / a;
            if (fh > bh)
            {
                fh = bh;
                fw = bh * a;
            }
            double cap = light ? 640 : 1280;
            double s = Math.Min(1, cap / Math.Max(fw, fh));
            w = Math.Max(16, (int)Math.Round(fw * s) & ~1);
            h = Math.Max(16, (int)Math.Round(fh * s) & ~1);
        }

        static VProject LookReset(VProject p)
        {
            var o = p.Clone();
            foreach (var c in o.Clips)
            {
                c.Bright = c.Contrast = c.Sat = c.Warm = c.Sharp = 0;
                c.Filter = "";
            }
            return o;
        }

        void VideoLoop()
        {
            VRenderer r = null, r2 = null;
            int shownVer = -1, shownW = 0, shownH = 0;
            double shownT = -1;
            VProject shownBefore = null;
            try
            {
                while (!closing)
                {
                    VProject p, b;
                    int ver, bw, bh;
                    double t;
                    bool isPlaying, ended = false;
                    lock (gate)
                    {
                        p = project;
                        b = before;
                        ver = version;
                        bw = boxW;
                        bh = boxH;
                        isPlaying = playing;
                        t = NowLocked();
                        if (isPlaying && p != null)
                        {
                            double dur = p.Duration;
                            double end = playEnd > 0 ? Math.Min(playEnd, dur) : dur;
                            if (t >= end - 1e-3)
                            {
                                if (loopFrom >= 0 && end - loopFrom > 0.2)
                                {
                                    playFrom = loopFrom;
                                    playedOffset = wave == null ? 0 : wave.Played;
                                    epoch++;
                                    t = loopFrom;
                                }
                                else
                                {
                                    playing = false;
                                    isPlaying = false;
                                    epoch++;
                                    want = Math.Max(0, end - 0.5 / Math.Max(10, p.Fps));
                                    t = want;
                                    ended = true;
                                }
                            }
                        }
                    }
                    if (ended)
                    {
                        wakeAudio.Set();
                        var e = Ended;
                        if (e != null) e();
                    }
                    if (p == null || p.Clips.Count == 0)
                    {
                        wakeVideo.WaitOne(200);
                        continue;
                    }
                    int w, h;
                    SizeFor(p, bw, bh, Light, out w, out h);
                    double frameDur = 1.0 / Math.Max(10, Math.Min(60, p.Fps));
                    bool need = ver != shownVer || w != shownW || h != shownH || Math.Abs(t - shownT) >= frameDur * 0.5 || b != shownBefore;
                    if (!need)
                    {
                        if (isPlaying) Thread.Sleep(3);
                        else wakeVideo.WaitOne(250);
                        continue;
                    }
                    if (r == null || r.W != w || r.H != h)
                    {
                        if (r != null) r.Dispose();
                        r = new VRenderer(w, h, false) { UseProxy = true };
                    }
                    r.Use(p, ver);
                    double dt = Math.Min(t, Math.Max(0, p.Duration - 1e-4));
                    byte[] px;
                    string problem;
                    try
                    {
                        px = r.Frame(dt);
                        problem = r.Problem;
                        if (b != null)
                        {
                            // 보정 전 모습을 왼쪽 반에
                            if (r2 == null || r2.W != w || r2.H != h)
                            {
                                if (r2 != null) r2.Dispose();
                                r2 = new VRenderer(w, h, false) { UseProxy = true };
                            }
                            r2.Use(LookFor(p), ver);
                            var left = r2.Frame(dt);
                            int half = (w / 2) * 4, stride = w * 4;
                            for (int y = 0; y < h; y++) Buffer.BlockCopy(left, y * stride, px, y * stride, half);
                            // 가운데 금
                            for (int y = 0; y < h; y++)
                            {
                                int o = y * stride + half;
                                for (int k = 0; k < 2 && o + k * 4 + 3 < px.Length; k++)
                                {
                                    px[o + k * 4] = px[o + k * 4 + 1] = px[o + k * 4 + 2] = 255;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("영상 편집 미리 보기 실패: " + ex.GetType().Name + " @ " + ex.TargetSite);
                        problem = ex is UserFacingException ? ex.Message : "이 장면을 그리지 못했어요.";
                        px = null;
                    }
                    Publish(px, w, h, dt, problem);
                    shownVer = ver;
                    shownW = w;
                    shownH = h;
                    shownT = t;
                    shownBefore = b;
                }
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집 미리 보기 멈춤: " + ex.GetType().Name);
            }
            finally
            {
                if (r != null) r.Dispose();
                if (r2 != null) r2.Dispose();
            }
        }

        VProject lookCache;
        VProject lookOf;

        /// 보정 전 모습(같은 내용이면 다시 만들지 않는다)
        VProject LookFor(VProject p)
        {
            if (lookOf != p || lookCache == null)
            {
                lookCache = LookReset(p);
                lookOf = p;
            }
            return lookCache;
        }

        void Publish(byte[] px, int w, int h, double t, string problem)
        {
            lock (FrameLock)
            {
                if (px != null)
                {
                    if (Frame == null || Frame.Length != px.Length) Frame = new byte[px.Length];
                    Buffer.BlockCopy(px, 0, Frame, 0, px.Length);
                    FrameW = w;
                    FrameH = h;
                }
                FrameT = t;
                Problem = problem;
                FrameSerial++;
            }
            var f = FrameReady;
            if (f != null) f();
        }

        // ── 소리 일꾼 ──

        void AudioLoop()
        {
            VWaveOut w = null;
            try
            {
                w = new VWaveOut();
                var mixer = new VMixer();
                var mix = new float[w.ChunkFrames * 2];
                long seen = -1;
                double pos = 0;
                bool running = false;
                Action<float[], int> fill = delegate (float[] dst, int n)
                {
                    VProject p;
                    lock (gate) p = project;
                    if (p == null || MuteAudio)
                    {
                        Array.Clear(dst, 0, n * 2);
                    }
                    else
                    {
                        mixer.Use(p);
                        mixer.Mix(pos, n, dst);
                        AudioPending = mixer.Pending;
                    }
                    pos += n / (double)AudioTrack.Rate;
                };
                lock (gate) wave = w;
                while (!closing)
                {
                    bool isPlaying;
                    long e;
                    lock (gate)
                    {
                        isPlaying = playing;
                        e = epoch;
                        if (isPlaying && e != seen)
                        {
                            // 처음부터 다시: 시계를 0으로 돌리고 그 자리부터 섞는다
                            w.Reset();
                            playedOffset = 0;
                            pos = playFrom;
                            seen = e;
                            running = true;
                        }
                    }
                    if (!isPlaying)
                    {
                        if (running)
                        {
                            lock (gate) w.Reset();
                            running = false;
                        }
                        seen = -1;
                        wakeAudio.WaitOne(120);
                        continue;
                    }
                    try
                    {
                        w.Feed(fill, mix);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("영상 편집 미리 듣기 실패: " + ex.GetType().Name);
                        Array.Clear(mix, 0, mix.Length);
                    }
                    wakeAudio.WaitOne(10);
                }
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집 소리 일꾼 멈춤: " + ex.GetType().Name);
            }
            finally
            {
                lock (gate) wave = null;
                if (w != null) w.Dispose();
            }
        }

        public void Dispose()
        {
            closing = true;
            playing = false;
            wakeVideo.Set();
            wakeAudio.Set();
            videoThread.Join(3000);
            audioThread.Join(3000);
        }
    }
}
