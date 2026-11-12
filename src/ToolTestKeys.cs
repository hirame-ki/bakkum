// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 도구 시험 중 움직임 표시(키프레임)·자막 너비 부분: /selftest-tools 시험폴더 keys (video 시험도 맨 앞에서 부른다)
// 파일을 만들지 않는 가벼운 시험이다. 화면에 그리는 것은 메모리 안 그림으로만 본다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Bakkum
{
    static partial class ToolTest
    {
        static bool Close(double a, double b)
        {
            return Math.Abs(a - b) < 1e-6;
        }

        static void KeyframeTests()
        {
            Cli.Print("[움직임 표시·자막 너비]");

            // ── 1. 기울기·투명도도 표시 사이를 잇는다 ──
            var it = new VItem { Kind = "sticker", Sticker = "star", Start = 0, End = 4, X = 0.5, Y = 0.5, W = 0.2, Rot = 10, Opacity = 0.8, In = "none", Out = "none" };
            var k0 = VKeys.Ensure(it, 0);
            Check("첫 표시는 지금 모습(기울기·투명도)을 담는다", Close(k0.Rot, 10) && k0.HasRot && Close(k0.Opacity, 0.8) && Close(it.Opacity, 1),
                "기울기 " + k0.Rot + " · 표시 투명도 " + k0.Opacity + " · 그것 투명도 " + it.Opacity);
            var k2 = VKeys.Ensure(it, 2);
            k2.Rot = 90;
            k2.Opacity = 0.4;
            k2.X = 0.7;
            var mid = VAnim.Item(it, 1);
            Check("표시 사이(가운데): 자리·기울기·투명도가 반씩", Close(mid.X, 0.6) && Close(mid.Rot, 50) && Close(mid.Opacity, 0.6),
                "X " + mid.X.ToString("0.###") + " · 기울기 " + mid.Rot.ToString("0.#") + " · 투명도 " + mid.Opacity.ToString("0.###"));
            var after = VAnim.Item(it, 3);
            Check("마지막 표시 뒤는 마지막 모습 그대로", Close(after.Rot, 90) && Close(after.Opacity, 0.4) && Close(after.X, 0.7), "기울기 " + after.Rot + " · 투명도 " + after.Opacity);

            var turn = new VItem { Kind = "sticker", Sticker = "star", Start = 0, End = 4, In = "none", Out = "none" };
            turn.Keys.Add(new VKey { T = 0, X = 0.5, Y = 0.5, Rot = 170, HasRot = true });
            turn.Keys.Add(new VKey { T = 2, X = 0.5, Y = 0.5, Rot = -170, HasRot = true });
            double r1 = VAnim.Item(turn, 1).Rot;
            Check("170°→-170°는 가까운 쪽(20°)으로 돈다", Close(((r1 % 360) + 360) % 360, 180), "가운데 " + r1.ToString("0.#") + "°");

            // 예전에 저장한 표시(기울기 없음, 투명도는 그것 전체 값)도 전과 똑같이 보인다
            var old = new VItem { Kind = "sticker", Sticker = "star", Start = 0, End = 4, Rot = 30, Opacity = 0.5, In = "none", Out = "none" };
            old.Keys.Add(new VKey { T = 0, X = 0.2, Y = 0.5 });
            old.Keys.Add(new VKey { T = 2, X = 0.8, Y = 0.5 });
            var o1 = VAnim.Item(old, 1);
            Check("예전 표시: 기울기·투명도는 그것의 값 그대로", Close(o1.Rot, 30) && Close(o1.Opacity, 0.5), "기울기 " + o1.Rot + " · 투명도 " + o1.Opacity);
            VKeys.Normalize(old);
            var o2 = VAnim.Item(old, 1);
            Check("예전 표시를 맞춰도 모습은 같다", Close(o2.Rot, 30) && Close(o2.Opacity, 0.5) && Close(old.Opacity, 1) && old.Keys.All(k => k.HasRot && Close(k.Opacity, 0.5)),
                "기울기 " + o2.Rot + " · 투명도 " + o2.Opacity);

            // ── 2. ◆ 넣기·빼기, 마지막 표시를 빼면 그 모습을 굳힌다 ──
            var tx = new VItem { Kind = "text", Text = "움직이는 제목", Start = 0, End = 4, X = 0.3, Y = 0.4, W = 0.5, Size = 0.06, In = "none", Out = "none" };
            bool added = VKeys.Toggle(tx, 1.0);
            Check("◆ 누르면 표시가 생긴다", added && tx.Keys.Count == 1, "표시 " + tx.Keys.Count + "개");
            bool removed = !VKeys.Toggle(tx, 1.02);
            Check("같은 시각(0.04초 안)에서 다시 누르면 빠지고, 마지막이면 모습을 굳힌다", removed && tx.Keys.Count == 0 && Close(tx.X, 0.3) && Close(tx.Y, 0.4), "표시 " + tx.Keys.Count + "개, X " + tx.X);
            VKeys.Ensure(tx, 0);
            var kb = VKeys.Ensure(tx, 2);
            kb.X = 0.9;
            kb.Zoom = 2;
            Check("표시 둘", tx.Keys.Count == 2, tx.Keys.Count + "개");
            VKeys.Toggle(tx, 2);
            Check("둘 중 하나를 빼면 하나 남는다", tx.Keys.Count == 1, tx.Keys.Count + "개");
            kb = VKeys.Ensure(tx, 2);
            kb.X = 0.9;
            kb.Zoom = 2;
            VKeys.Bake(tx, 1);
            Check("모두 지우기: 그 시각 모습(자리·크기)으로 굳힌다", tx.Keys.Count == 0 && Close(tx.X, 0.6) && Close(tx.Size, 0.09) && Close(tx.W, 0.75),
                "X " + tx.X.ToString("0.###") + " · 크기 " + tx.Size.ToString("0.###") + " · 줄 너비 " + tx.W.ToString("0.###"));

            // ── 3. 앞뒤 표시 찾기 ──
            var ks = new List<VKey> { new VKey { T = 0 }, new VKey { T = 1 }, new VKey { T = 2 } };
            Check("앞·뒤 표시 찾기", Close(VKeys.Neighbor(ks, 1, -1), 0) && Close(VKeys.Neighbor(ks, 1, 1), 2) && double.IsNaN(VKeys.Neighbor(ks, 2, 1)),
                "앞 " + VKeys.Neighbor(ks, 1, -1) + " · 뒤 " + VKeys.Neighbor(ks, 1, 1));
            Check("0.04초 안은 같은 표시로 본다", VKeys.Find(ks, 1.03) == ks[1] && VKeys.Find(ks, 1.06) == null, "");

            // ── 4. 조각의 확대·자리 표시(영상은 원본 시각에 붙는다) ──
            var c = new VClip { Kind = "video", In = 10, Out = 20, Zoom = 1.2, PanX = 0.1 };
            var ck = VKeys.EnsureClip(c, 2, c.SourceAt(2));
            Check("영상 조각의 표시는 원본 시각에", Close(ck.T, 12) && Close(ck.Zoom, 1.2) && Close(ck.PanX, 0.1), "T " + ck.T);
            ck.Zoom = 2;
            bool cAdded = VKeys.ToggleClip(c, 2, 12);
            Check("조각의 마지막 표시를 빼면 그 크기를 굳힌다", !cAdded && c.Keys.Count == 0 && Close(c.Zoom, 2), "크기 " + c.Zoom);
            var ph = new VClip { Kind = "photo", Dur = 4 };
            var pk = VKeys.EnsureClip(ph, 1.5, ph.SourceAt(1.5));
            Check("사진 조각의 표시는 조각 안 시각에", Close(pk.T, 1.5), "T " + pk.T);

            // ── 5. 자막 너비: 모서리로 키워도 줄이 세로로 쌓이지 않는다(2026-11-12 사용자 제보) ──
            double cw = 640, chh = 360;
            var sub = new VItem { Kind = "text", Subtitle = true, Text = "오늘은 물이 어떻게 하늘로 올라가 구름이 되는지 알아봐요", Size = 0.055, W = 0.3, X = 0.5, Y = 0.86 };
            int before = VOverlay.Layout(sub, cw, chh).Lines.Count;
            var oldWay = sub.Copy();
            oldWay.Size = 0.11;
            int oldLines = VOverlay.Layout(oldWay, cw, chh).Lines.Count;
            VKeys.ScaleText(sub, 0.055, 0.3, 2);
            int nowLines = VOverlay.Layout(sub, cw, chh).Lines.Count;
            Check("모서리로 두 배: 글자와 줄 너비가 같이 커져 줄 수가 그대로", nowLines == before && oldLines > before && Close(sub.W, 0.6),
                "처음 " + before + "줄 → 지금 " + nowLines + "줄 (예전 방식은 " + oldLines + "줄)");
            Check("옆 손잡이: 끈 만큼 양옆으로", Close(VKeys.TextWidthFor(200, 50, 640), 301.0 / 640), VKeys.TextWidthFor(200, 50, 640).ToString("0.####"));
            var wide = new VItem { Kind = "text", Subtitle = true, Text = sub.Text, Size = 0.055, W = 0.3 };
            var lay = VOverlay.Layout(wide, cw, chh);
            int narrow = lay.Lines.Count;
            wide.W = VKeys.TextWidthFor(lay.BlockW, 200, cw);
            int widened = VOverlay.Layout(wide, cw, chh).Lines.Count;
            Check("옆 손잡이로 넓히면 줄이 준다", widened < narrow, narrow + "줄 → " + widened + "줄");
            var srt = Bakkum.VideoEditor.ParseSrt(System.Text.Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\n불러온 자막\n"));
            Check("새 자막(SRT 불러오기)의 줄 너비는 화면의 86%", srt.Count == 1 && Close(srt[0].W, 0.86), srt.Count > 0 ? srt[0].W.ToString() : "없음");

            // ── 6. 저장·되돌리기(JSON)에도 기울기 표시가 남는다 ──
            var p = new VProject { Aspect = "16:9" };
            p.Clips.Add(new VClip { Kind = "color", Dur = 4 });
            var bar = new VItem { Kind = "shape", Shape = "marker", Start = 0, End = 4, X = 0.5, Y = 0.5, W = 0.4, H = 0.06, Rgb = 0xFFD447, In = "none", Out = "none" };
            bar.Keys.Add(new VKey { T = 0, X = 0.5, Y = 0.5, Rot = 0, HasRot = true });
            bar.Keys.Add(new VKey { T = 2, X = 0.5, Y = 0.5, Rot = 90, HasRot = true });
            p.Items.Add(bar);
            var back = VProject.FromJson(p.ToJson());
            var bk = back.Items[0].Keys;
            Check("기울기 표시가 저장·되돌리기에 남는다", bk.Count == 2 && bk[1].HasRot && Close(bk[1].Rot, 90), bk.Count + "개");

            // ── 7. 화면·내보내기 그림이 표시의 기울기대로 돌아간다 ──
            var hitUp = new Point(320, 100);
            Check("기울기 표시대로 누르는 자리도 돈다",
                VOverlay.HitTest(p, 0, cw, chh, hitUp) == null && VOverlay.HitTest(p, 2.5, cw, chh, hitUp) == bar,
                "가로(0초)에선 위쪽이 비고, 세로(2.5초)에선 잡힘");
            var rd = new VOverlayRenderer((int)cw, (int)chh);
            var px0 = rd.Render(p, 1, 0);
            byte a0 = px0 == null ? (byte)0 : px0[(100 * (int)cw + 320) * 4 + 3];
            var px2 = rd.Render(p, 1, 2.5);
            byte a2 = px2 == null ? (byte)0 : px2[(100 * (int)cw + 320) * 4 + 3];
            Check("내보내기 그림도 표시대로 돈다", a0 == 0 && a2 > 40, "위쪽 점 불투명도: 0초 " + a0 + " → 2.5초 " + a2);
        }
    }
}
