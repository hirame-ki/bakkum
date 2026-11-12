// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 키프레임(움직임 표시) 다루기: 이 시각에 넣기·지우기, 앞뒤 표시 찾기, 마지막 표시를 지울 때 지금 모습 굳히기.
// 편집 창의 단추·화면 끌기·슬라이더와 자동 시험이 같이 쓴다.
// 얹은 것의 표시는 '시작에서 지난 시간', 영상 조각의 표시는 '원본 시간', 사진·색 조각의 표시는 '조각 안 시간'에 붙는다.
using System;
using System.Collections.Generic;

namespace Bakkum
{
    static class VKeys
    {
        /// 이만큼 가까우면 같은 시각의 표시로 본다(초당 30장 영상의 한 장 남짓)
        public const double Near = 0.04;

        public static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        public static void Sort(List<VKey> keys)
        {
            keys.Sort((a, b) => a.T.CompareTo(b.T));
        }

        /// t에 가장 가까운 표시(Near 안), 없으면 null
        public static VKey Find(List<VKey> keys, double t)
        {
            VKey best = null;
            double bd = Near + 1e-9;
            foreach (var k in keys)
            {
                double d = Math.Abs(k.T - t);
                if (d <= bd)
                {
                    best = k;
                    bd = d;
                }
            }
            return best;
        }

        /// t보다 앞(dir<0)·뒤(dir>0)의 가장 가까운 표시 시각, 없으면 NaN
        public static double Neighbor(List<VKey> keys, double t, int dir)
        {
            double best = double.NaN;
            foreach (var k in keys)
            {
                if (dir < 0 && k.T < t - Near / 2 && (double.IsNaN(best) || k.T > best)) best = k.T;
                if (dir > 0 && k.T > t + Near / 2 && (double.IsNaN(best) || k.T < best)) best = k.T;
            }
            return best;
        }

        /// 얹은 것의 표시 시각(시작에서 지난 시간, 0~길이)
        public static double LocalOf(VItem it, double t)
        {
            return Clamp(t - it.Start, 0, it.Length);
        }

        /// 예전 표시를 지금 방식으로 맞춘다: 기울기가 없는 표시에는 그것의 기울기를 채우고,
        /// 그것 전체의 투명도는 표시마다 곱해 넣는다(그래야 표시마다 투명도를 따로 바꿀 수 있다).
        public static void Normalize(VItem it)
        {
            if (it.Keys.Count == 0) return;
            foreach (var k in it.Keys)
            {
                if (k.HasRot) continue;
                k.Rot = it.Rot;
                k.HasRot = true;
            }
            if (Math.Abs(it.Opacity - 1) > 1e-9)
            {
                foreach (var k in it.Keys) k.Opacity = Clamp(k.Opacity * it.Opacity, 0, 1);
                it.Opacity = 1;
            }
        }

        /// local 시각의 표시. 없으면 그 시각의 모습(자리·크기·기울기·투명도)으로 새로 만든다.
        public static VKey Ensure(VItem it, double local)
        {
            if (it.Keys.Count == 0)
            {
                var first = new VKey { T = local, X = it.X, Y = it.Y, Zoom = 1, Opacity = it.Opacity, Rot = it.Rot, HasRot = true };
                it.Opacity = 1;
                it.Keys.Add(first);
                return first;
            }
            Normalize(it);
            var k = Find(it.Keys, local);
            if (k != null) return k;
            k = VAnim.At(it.Keys, local).Copy();
            k.T = local;
            it.Keys.Add(k);
            Sort(it.Keys);
            return k;
        }

        /// 이 시각에 표시가 없으면 넣고(true), 있으면 지운다(false). 마지막 표시를 지우면 그 모습을 굳힌다.
        public static bool Toggle(VItem it, double local)
        {
            var k = Find(it.Keys, local);
            if (k == null)
            {
                Ensure(it, local);
                return true;
            }
            if (it.Keys.Count == 1)
            {
                Bake(it, local);
                return false;
            }
            it.Keys.Remove(k);
            return false;
        }

        /// 표시를 모두 지우고 local 시각의 모습(자리·크기·기울기·투명도)을 그것에 굳힌다
        public static void Bake(VItem it, double local)
        {
            if (it.Keys.Count == 0) return;
            Normalize(it);
            var k = VAnim.At(it.Keys, local);
            it.X = k.X;
            it.Y = k.Y;
            it.Rot = k.HasRot ? k.Rot : it.Rot;
            it.Opacity = Clamp(k.Opacity, 0, 1);
            double zoom = k.Zoom;
            it.Keys.Clear();
            ScaleBy(it, zoom);
        }

        /// 크기를 곱해 굳힌다. 글자는 글자 크기와 줄 너비를 같이 바꿔 줄 바꿈이 그대로다.
        public static void ScaleBy(VItem it, double z)
        {
            if (z <= 0 || double.IsNaN(z) || Math.Abs(z - 1) < 1e-6) return;
            switch (it.Kind)
            {
                case "text":
                    {
                        double size = Clamp(it.Size * z, 0.015, 0.3);
                        double f = size / Math.Max(1e-6, it.Size);
                        it.Size = size;
                        it.W = Clamp(it.W * f, 0.05, 2);
                        break;
                    }
                case "shape":
                    it.W = Clamp(it.W * z, -3, 3);
                    it.H = Clamp(it.H * z, -3, 3);
                    it.Thick = Clamp(it.Thick * z, 0.001, 0.2);
                    break;
                default:
                    it.W = Clamp(it.W * z, 0.02, 1.5);
                    break;
            }
        }

        /// 글자 모서리를 끌 때: 글자 크기와 줄 너비를 같은 비율로(줄이 세로로 쌓이지 않게)
        public static void ScaleText(VItem it, double origSize, double origW, double k)
        {
            double size = Clamp(origSize * k, 0.015, 0.3);
            double f = size / Math.Max(1e-6, origSize);
            it.Size = size;
            it.W = Clamp(origW * f, 0.05, 2);
        }

        /// 글자 옆 손잡이를 끌 때의 줄 너비(화면 너비에 대한 비율).
        /// blockPx: 끌기 시작할 때 글 덩이 너비, movedPx: 손잡이가 바깥으로 간 거리(안으로는 음수). 둘 다 크기 배율을 뺀 화면 픽셀.
        /// 양옆이 같이 움직이니 두 배로 더하고, 1픽셀 여유를 둬 끌기 시작만 해도 가장 긴 줄이 꺾이지 않게 한다.
        public static double TextWidthFor(double blockPx, double movedPx, double canvasW)
        {
            return Clamp((blockPx + movedPx * 2 + 1) / Math.Max(1, canvasW), 0.05, 2);
        }

        // ── 조각(확대·옮기기) ──

        /// 조각의 표시 시각: 영상은 원본 시간, 그 밖의 조각은 조각 안 시간
        public static double ClipTime(VClip c, double local, double source)
        {
            return c.Kind == "video" ? source : local;
        }

        public static VKey EnsureClip(VClip c, double local, double source)
        {
            double kt = ClipTime(c, local, source);
            var k = Find(c.Keys, kt);
            if (k != null) return k;
            double z, px, py;
            VAnim.ClipMotion(c, local, source, out z, out px, out py);
            k = new VKey { T = kt, Zoom = z, PanX = px, PanY = py };
            c.Keys.Add(k);
            Sort(c.Keys);
            return k;
        }

        /// 조각: 이 시각에 표시가 없으면 넣고(true), 있으면 지운다(false). 마지막을 지우면 그 확대·자리를 굳힌다.
        public static bool ToggleClip(VClip c, double local, double source)
        {
            double kt = ClipTime(c, local, source);
            var k = Find(c.Keys, kt);
            if (k == null)
            {
                EnsureClip(c, local, source);
                return true;
            }
            if (c.Keys.Count == 1)
            {
                c.Zoom = k.Zoom;
                c.PanX = k.PanX;
                c.PanY = k.PanY;
                c.Keys.Clear();
                return false;
            }
            c.Keys.Remove(k);
            return false;
        }

        /// 조각 표시를 모두 지우고 지금 확대·자리를 굳힌다
        public static void BakeClip(VClip c, double local, double source)
        {
            if (c.Keys.Count == 0) return;
            double z, px, py;
            VAnim.ClipMotion(c, local, source, out z, out px, out py);
            c.Zoom = z;
            c.PanX = px;
            c.PanY = py;
            c.Keys.Clear();
        }
    }
}
