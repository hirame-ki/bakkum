// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집: 긴 영상·큰 영상의 미리 보기용 가벼운 사본 만들기. 뒤에서 천천히 만들고(편집은 그대로 할 수 있게),
// 다 되면 미리 보기가 저절로 사본으로 바뀐다. 내보내기는 늘 원본으로 한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Bakkum
{
    public sealed partial class VideoEditor
    {
        /// 사본을 만드는 중인 원본(없으면 null). 한 번에 하나만.
        string proxyPath;
        double proxyFrac;
        volatile bool proxyStop;
        /// 편집 칸의 진행 글(칸을 다시 그리면 새로 잡힌다)
        TextBlock proxyHint;

        /// 왜 미리 보기가 느릴 수 있는지(한 문장)
        static string ProxyWhy(MediaProbe pr)
        {
            int big = Math.Max(pr.Width, pr.Height);
            if (pr.Seconds >= 600) return "영상이 " + Math.Round(pr.Seconds / 60) + "분으로 길어서 미리 보기에서 넘길 때 느릴 수 있어요.";
            if (big > 1920) return "영상이 커서(" + pr.Width + "×" + pr.Height + ") 미리 보기에서 넘길 때 느릴 수 있어요.";
            return "1초에 " + Math.Round(pr.Fps) + "장을 찍은 영상이라 미리 보기에서 넘길 때 느릴 수 있어요.";
        }

        static string About(double seconds)
        {
            if (seconds < 60) return "1분이 안 걸리고";
            return "대략 " + Math.Ceiling(seconds / 60) + "분 걸리고";
        }

        string ProxyLine()
        {
            return "가벼운 사본을 만드는 중이에요(" + Math.Round(Math.Min(1, proxyFrac) * 100) + "%). 다 되면 미리 보기가 저절로 바뀌어요. 그동안에도 편집할 수 있어요.";
        }

        /// 편집 칸, 고른 영상 조각 아래: 사본이 도움이 되는 영상에만 보인다
        void ProxySection(VClip c)
        {
            proxyHint = null;
            if (c == null || c.Kind != "video" || string.IsNullOrEmpty(c.Path)) return;
            var pr = VMedia.Probe(c.Path);
            if (pr == null) return;
            string path = c.Path;
            bool making = proxyPath != null && string.Equals(proxyPath, path, StringComparison.OrdinalIgnoreCase);
            string have = making ? null : VProxy.Find(path);
            if (!making && have == null && !VProxy.Helps(pr)) return;
            Label("미리 보기");
            if (making)
            {
                var t = Theme.MetaLabel(ProxyLine());
                t.TextWrapping = TextWrapping.Wrap;
                t.Margin = new Thickness(0, 2, 0, 2);
                panel.Children.Add(t);
                proxyHint = t;
                Act("사본 만들기 멈추기", "Line", delegate { proxyStop = true; });
                return;
            }
            if (have != null)
            {
                Hint("가벼운 사본으로 미리 보여 줘서 넘겨 볼 때 빨라요. 크게 확대해 볼 때와 내보낼 때는 원본을 써요.");
                Act("사본 지우기", "Quiet", delegate
                {
                    VProxy.Drop(path);
                    Touch(false);
                    RefreshPanel();
                    SetStatus("사본을 지웠어요. 미리 보기는 원본으로 보여 줘요.");
                });
                return;
            }
            double sec;
            long bytes;
            VProxy.Estimate(pr, out sec, out bytes);
            Hint(ProxyWhy(pr) + " 미리 보기용 가벼운 사본을 만들면 넘겨 볼 때 빨라져요. " + About(sec) + " " + Conversions.Size(bytes) + "쯤 써요. 만드는 동안에도 편집할 수 있고, 내보내기는 늘 원본으로 해요.");
            Act("미리 보기용 가벼운 사본 만들기", "Line", delegate { StartProxy(path); });
        }

        /// 넣은 영상 가운데 사본이 도움이 될 것이 있으면 한 줄 안내(없으면 null)
        internal static string ProxyAdvice(IEnumerable<VClip> clips)
        {
            foreach (var c in clips)
            {
                if (c.Kind != "video" || string.IsNullOrEmpty(c.Path)) continue;
                var pr = VMedia.Probe(c.Path);
                if (pr == null || !VProxy.Helps(pr) || VProxy.Find(c.Path) != null) continue;
                return ProxyWhy(pr) + " 그 조각을 고르면 편집 칸에서 가벼운 사본을 만들 수 있어요.";
            }
            return null;
        }

        void StartProxy(string path)
        {
            if (proxyPath != null)
            {
                SetStatus("다른 영상의 사본을 만드는 중이에요. 끝난 뒤에 다시 눌러 주세요.");
                return;
            }
            var pr = VMedia.Probe(path);
            if (pr == null) return;
            double sec;
            long bytes;
            VProxy.Estimate(pr, out sec, out bytes);
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(VProxy.Folder));
                long need = bytes * 3 / 2 + 300L * 1024 * 1024;
                if (drive.IsReady && drive.AvailableFreeSpace < need)
                {
                    SetStatus("디스크 공간이 모자라 사본을 만들지 않았어요. 약 " + Conversions.Size(need) + "가 비어 있어야 해요(지금 " + Conversions.Size(drive.AvailableFreeSpace) + ").");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Write("미리 보기 사본: 빈 공간 확인 실패 " + ex.GetType().Name);
            }
            proxyPath = path;
            proxyFrac = 0;
            proxyStop = false;
            SetStatus("미리 보기용 가벼운 사본을 만들기 시작했어요. 다 되면 알려 드릴게요.");
            RefreshPanel();
            var th = new Thread(delegate ()
            {
                Exception error = null;
                int lastTick = Environment.TickCount;
                try
                {
                    VProxy.Make(path, delegate (double f)
                    {
                        proxyFrac = f;
                        int now = Environment.TickCount;
                        if (now - lastTick < 500 && f < 1) return;
                        lastTick = now;
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (proxyHint != null) proxyHint.Text = Theme.Keep(ProxyLine());
                        }));
                    }, () => proxyStop || !windowOpen);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    proxyPath = null;
                    if (!windowOpen) return;
                    if (error is OperationCanceledException) SetStatus("사본 만들기를 멈췄어요. 미리 보기는 원본으로 보여 줘요.");
                    else if (error != null)
                    {
                        Log.Write("미리 보기 사본 만들기 실패: " + error.GetType().Name + " 0x" + error.HResult.ToString("X8"));
                        SetStatus(error is UserFacingException ? error.Message : "사본을 만들지 못했어요. 미리 보기는 원본으로 보여 줘요. (" + error.GetType().Name + ")");
                    }
                    else
                    {
                        SetStatus("미리 보기용 가벼운 사본을 다 만들었어요. 이제 넘겨 볼 때 빨라요. 내보내기는 원본으로 해요.");
                        Touch(false);
                    }
                    RefreshPanel();
                }));
            }) { IsBackground = true, Name = "미리 보기 사본", Priority = ThreadPriority.BelowNormal };
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
        }
    }
}
