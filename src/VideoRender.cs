// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집의 한 장면 만들기. 본 영상 줄의 조각을 풀어(VideoReader) 가리기 → 색 → 흔들림 줄이기 → 돌리기·뒤집기 → 자르기 →
// 비율에 맞추기·확대·자리 옮기기를 거쳐 화면(캔버스)에 놓고, 조각 사이 전환과 작은 화면(PiP)을 얹는다.
// 미리 보기와 내보내기가 같은 계산을 쓴다(그래서 보이는 대로 저장된다). 글자·그림·스티커·도형은 VideoOverlay.cs가 그린다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    /// 그림(BGRA, 위쪽 줄부터) 다루기
    static class VPx
    {
        public static byte Byte(double v)
        {
            return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
        }

        static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        public static void Fill(byte[] p, uint rgb)
        {
            if (p == null || p.Length < 4) return;
            p[0] = (byte)rgb;
            p[1] = (byte)(rgb >> 8);
            p[2] = (byte)(rgb >> 16);
            p[3] = 255;
            int filled = 4;
            while (filled < p.Length)
            {
                int n = Math.Min(filled, p.Length - filled);
                Buffer.BlockCopy(p, 0, p, filled, n);
                filled += n;
            }
        }

        /// 가로·세로 상자 흐림을 passes번(가우스 흐림에 가깝다). 결과는 a에, b는 같은 크기의 빈 칸.
        public static void BoxBlur(byte[] a, byte[] b, int w, int h, int r, int passes)
        {
            if (r < 1 || w < 2 || h < 2) return;
            for (int n = 0; n < passes; n++)
            {
                byte[] from = a, to = b;
                Parallel.For(0, h, y =>
                {
                    int line = y * w * 4;
                    int div = 2 * r + 1;
                    int s0 = 0, s1 = 0, s2 = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int at = line + (k < 0 ? 0 : k >= w ? w - 1 : k) * 4;
                        s0 += from[at];
                        s1 += from[at + 1];
                        s2 += from[at + 2];
                    }
                    for (int x = 0; x < w; x++)
                    {
                        int o = line + x * 4;
                        to[o] = (byte)(s0 / div);
                        to[o + 1] = (byte)(s1 / div);
                        to[o + 2] = (byte)(s2 / div);
                        to[o + 3] = from[o + 3];
                        int outAt = line + (x - r < 0 ? 0 : x - r) * 4;
                        int inAt = line + (x + r + 1 >= w ? w - 1 : x + r + 1) * 4;
                        s0 += from[inAt] - from[outAt];
                        s1 += from[inAt + 1] - from[outAt + 1];
                        s2 += from[inAt + 2] - from[outAt + 2];
                    }
                });
                byte[] from2 = to, to2 = from;
                Parallel.For(0, w, x =>
                {
                    int div = 2 * r + 1;
                    int s0 = 0, s1 = 0, s2 = 0;
                    int stride = w * 4;
                    for (int k = -r; k <= r; k++)
                    {
                        int at = (k < 0 ? 0 : k >= h ? h - 1 : k) * stride + x * 4;
                        s0 += from2[at];
                        s1 += from2[at + 1];
                        s2 += from2[at + 2];
                    }
                    for (int y = 0; y < h; y++)
                    {
                        int o = y * stride + x * 4;
                        to2[o] = (byte)(s0 / div);
                        to2[o + 1] = (byte)(s1 / div);
                        to2[o + 2] = (byte)(s2 / div);
                        to2[o + 3] = from2[o + 3];
                        int outAt = (y - r < 0 ? 0 : y - r) * stride + x * 4;
                        int inAt = (y + r + 1 >= h ? h - 1 : y + r + 1) * stride + x * 4;
                        s0 += from2[inAt] - from2[outAt];
                        s1 += from2[inAt + 1] - from2[outAt + 1];
                        s2 += from2[inAt + 2] - from2[outAt + 2];
                    }
                });
            }
        }

        sealed class Taps
        {
            public int[] Start, Count;
            public float[] W;
            public int Max;
        }

        static readonly Dictionary<long, Taps> TapsCache = new Dictionary<long, Taps>();

        /// 넓은 칸을 좁은 칸으로 줄일 때 원본 칸마다의 몫(넓이 평균)
        static Taps TapsFor(int src, int dst)
        {
            long key = ((long)src << 32) | (uint)dst;
            lock (TapsCache)
            {
                Taps t;
                if (TapsCache.TryGetValue(key, out t)) return t;
                double scale = (double)src / dst;
                int max = (int)Math.Ceiling(scale) + 2;
                t = new Taps { Start = new int[dst], Count = new int[dst], W = new float[dst * max], Max = max };
                for (int o = 0; o < dst; o++)
                {
                    double a = o * scale, b = (o + 1) * scale;
                    int i0 = (int)Math.Floor(a), i1 = Math.Min(src, (int)Math.Ceiling(b));
                    t.Start[o] = i0;
                    int n = 0;
                    for (int i = i0; i < i1 && n < max; i++, n++) t.W[o * max + n] = (float)((Math.Min(b, i + 1) - Math.Max(a, i)) / scale);
                    t.Count[o] = n;
                }
                if (TapsCache.Count > 64) TapsCache.Clear();
                TapsCache[key] = t;
                return t;
            }
        }

        [ThreadStatic] static float[] mid;

        /// src(srcW 너비)의 (cx,cy)부터 cw×ch 부분을 dst(dstW 너비)의 (ox,oy)부터 fw×fh 칸에 넓이 평균으로 줄여 넣는다.
        public static void ShrinkPart(byte[] src, int srcW, int cx, int cy, int cw, int ch, byte[] dst, int dstW, int ox, int oy, int fw, int fh)
        {
            if (cw <= 0 || ch <= 0 || fw <= 0 || fh <= 0) return;
            Taps tx = TapsFor(cw, fw), ty = TapsFor(ch, fh);
            int need = ch * fw * 3;
            if (mid == null || mid.Length < need) mid = new float[need];
            float[] m = mid;
            Parallel.For(0, ch, y =>
            {
                int row = ((cy + y) * srcW + cx) * 4, o = y * fw * 3;
                for (int x = 0; x < fw; x++, o += 3)
                {
                    float s0 = 0, s1 = 0, s2 = 0;
                    int st = tx.Start[x], n = tx.Count[x], wi = x * tx.Max;
                    for (int k = 0; k < n; k++)
                    {
                        int at = row + (st + k) * 4;
                        float wt = tx.W[wi + k];
                        s0 += src[at] * wt;
                        s1 += src[at + 1] * wt;
                        s2 += src[at + 2] * wt;
                    }
                    m[o] = s0;
                    m[o + 1] = s1;
                    m[o + 2] = s2;
                }
            });
            Parallel.For(0, fh, y =>
            {
                int st = ty.Start[y], n = ty.Count[y], wi = y * ty.Max;
                int d = ((oy + y) * dstW + ox) * 4;
                for (int x = 0; x < fw; x++, d += 4)
                {
                    float s0 = 0, s1 = 0, s2 = 0;
                    for (int k = 0; k < n; k++)
                    {
                        int at = ((st + k) * fw + x) * 3;
                        float wt = ty.W[wi + k];
                        s0 += m[at] * wt;
                        s1 += m[at + 1] * wt;
                        s2 += m[at + 2] * wt;
                    }
                    dst[d] = s0 <= 0 ? (byte)0 : s0 >= 255 ? (byte)255 : (byte)(s0 + 0.5f);
                    dst[d + 1] = s1 <= 0 ? (byte)0 : s1 >= 255 ? (byte)255 : (byte)(s1 + 0.5f);
                    dst[d + 2] = s2 <= 0 ? (byte)0 : s2 >= 255 ? (byte)255 : (byte)(s2 + 0.5f);
                    dst[d + 3] = 255;
                }
            });
        }

        public static void Shrink(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
        {
            ShrinkPart(src, sw, 0, 0, sw, sh, dst, dw, 0, 0, dw, dh);
        }

        /// 작은 그림을 화면 전체를 덮게 늘린다(넘치는 쪽은 잘림). 이웃 칸을 직선으로 섞는다.
        public static void Cover(byte[] s, int sw, int sh, byte[] dst, int ow, int oh)
        {
            double scale = Math.Max(ow / (double)sw, oh / (double)sh);
            double offX = (ow - sw * scale) / 2, offY = (oh - sh * scale) / 2;
            var x0 = new int[ow];
            var x1 = new int[ow];
            var ax = new int[ow];
            for (int x = 0; x < ow; x++)
            {
                double f = (x + 0.5 - offX) / scale - 0.5;
                if (f < 0) f = 0;
                if (f > sw - 1) f = sw - 1;
                int i = (int)f;
                x0[x] = i * 4;
                x1[x] = Math.Min(i + 1, sw - 1) * 4;
                ax[x] = (int)Math.Round((f - i) * 256);
            }
            Parallel.For(0, oh, y =>
            {
                double f = (y + 0.5 - offY) / scale - 0.5;
                if (f < 0) f = 0;
                if (f > sh - 1) f = sh - 1;
                int j = (int)f, j1 = Math.Min(j + 1, sh - 1), ay = (int)Math.Round((f - j) * 256);
                int r0 = j * sw * 4, r1 = j1 * sw * 4, d = y * ow * 4;
                for (int x = 0; x < ow; x++, d += 4)
                {
                    int a = r0 + x0[x], b = r0 + x1[x], c = r1 + x0[x], e = r1 + x1[x], wx = ax[x];
                    for (int k = 0; k < 3; k++)
                    {
                        int top = s[a + k] * (256 - wx) + s[b + k] * wx;
                        int bot = s[c + k] * (256 - wx) + s[e + k] * wx;
                        dst[d + k] = (byte)((top * (256 - ay) + bot * ay + 32768) >> 16);
                    }
                    dst[d + 3] = 255;
                }
            });
        }

        /// 크로마 키(초록 화면 빼기) 값
        public sealed class Keyer
        {
            public double Cb, Cr, Tol;
            public bool GreenSpill;

            public static Keyer For(uint rgb, double tol)
            {
                double r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
                return new Keyer
                {
                    Cb = -0.169 * r - 0.331 * g + 0.5 * b,
                    Cr = 0.5 * r - 0.419 * g - 0.081 * b,
                    Tol = Math.Max(0.05, Math.Min(0.9, tol)),
                    GreenSpill = g > r && g > b
                };
            }

            /// 0(투명)~256(그대로)
            public int Alpha(int b, int g, int r)
            {
                double cb = -0.169 * r - 0.331 * g + 0.5 * b, cr = 0.5 * r - 0.419 * g - 0.081 * b;
                double d = Math.Sqrt((cb - Cb) * (cb - Cb) + (cr - Cr) * (cr - Cr)) / 180.0;
                double lo = Tol * 0.55, hi = Tol;
                if (d <= lo) return 0;
                if (d >= hi) return 256;
                double k = (d - lo) / (hi - lo);
                return (int)(k * k * (3 - 2 * k) * 256);
            }
        }

        /// src(sw×sh)의 clip 부분을 m(원본 픽셀 → 화면 픽셀)으로 dst(dw×dh)에 놓는다. 이웃 네 칸을 섞는다.
        /// oval이면 clip 안의 타원만, key가 있으면 그 색을 뺀다.
        public static void Place(byte[] src, int sw, int sh, Rect clip, byte[] dst, int dw, int dh, Matrix m, double opacity, bool oval, Keyer key)
        {
            if (src == null || !m.HasInverse || opacity <= 0.002) return;
            clip.Intersect(new Rect(0, 0, sw, sh));
            if (clip.IsEmpty || clip.Width < 0.5 || clip.Height < 0.5) return;
            var inv = m;
            inv.Invert();
            var c0 = m.Transform(clip.TopLeft);
            var c1 = m.Transform(clip.TopRight);
            var c2 = m.Transform(clip.BottomLeft);
            var c3 = m.Transform(clip.BottomRight);
            int x0 = Clamp((int)Math.Floor(Math.Min(Math.Min(c0.X, c1.X), Math.Min(c2.X, c3.X))), 0, dw);
            int x1 = Clamp((int)Math.Ceiling(Math.Max(Math.Max(c0.X, c1.X), Math.Max(c2.X, c3.X))), 0, dw);
            int y0 = Clamp((int)Math.Floor(Math.Min(Math.Min(c0.Y, c1.Y), Math.Min(c2.Y, c3.Y))), 0, dh);
            int y1 = Clamp((int)Math.Ceiling(Math.Max(Math.Max(c0.Y, c1.Y), Math.Max(c2.Y, c3.Y))), 0, dh);
            if (x1 <= x0 || y1 <= y0) return;
            double cl = clip.Left, ct = clip.Top, cr = clip.Right, cb = clip.Bottom;
            double ocx = (cl + cr) / 2, ocy = (ct + cb) / 2, orx = Math.Max(0.5, (cr - cl) / 2), ory = Math.Max(0.5, (cb - ct) / 2);
            int alpha = (int)Math.Round(Math.Max(0, Math.Min(1, opacity)) * 256);
            double m11 = inv.M11, m12 = inv.M12, m21 = inv.M21, m22 = inv.M22, ox = inv.OffsetX, oy = inv.OffsetY;
            int swm = sw - 1, shm = sh - 1;
            Parallel.For(y0, y1, y =>
            {
                double py = y + 0.5;
                double sx = m11 * (x0 + 0.5) + m21 * py + ox;
                double sy = m12 * (x0 + 0.5) + m22 * py + oy;
                int d = (y * dw + x0) * 4;
                for (int x = x0; x < x1; x++, d += 4, sx += m11, sy += m12)
                {
                    if (sx < cl || sx >= cr || sy < ct || sy >= cb) continue;
                    if (oval)
                    {
                        double u = (sx - ocx) / orx, v = (sy - ocy) / ory;
                        if (u * u + v * v > 1) continue;
                    }
                    double fx = sx - 0.5, fy = sy - 0.5;
                    int ix = fx < 0 ? -1 : (int)fx, iy = fy < 0 ? -1 : (int)fy;
                    int ax = (int)((fx - ix) * 256), ay = (int)((fy - iy) * 256);
                    if (ax > 256) ax = 256;
                    if (ay > 256) ay = 256;
                    int xa = ix < 0 ? 0 : ix > swm ? swm : ix, xb = ix + 1 > swm ? swm : ix + 1 < 0 ? 0 : ix + 1;
                    int ya = iy < 0 ? 0 : iy > shm ? shm : iy, yb = iy + 1 > shm ? shm : iy + 1 < 0 ? 0 : iy + 1;
                    int a = (ya * sw + xa) * 4, b = (ya * sw + xb) * 4, c = (yb * sw + xa) * 4, e = (yb * sw + xb) * 4;
                    int v0 = ((src[a] * (256 - ax) + src[b] * ax) * (256 - ay) + (src[c] * (256 - ax) + src[e] * ax) * ay + 32768) >> 16;
                    int v1 = ((src[a + 1] * (256 - ax) + src[b + 1] * ax) * (256 - ay) + (src[c + 1] * (256 - ax) + src[e + 1] * ax) * ay + 32768) >> 16;
                    int v2 = ((src[a + 2] * (256 - ax) + src[b + 2] * ax) * (256 - ay) + (src[c + 2] * (256 - ax) + src[e + 2] * ax) * ay + 32768) >> 16;
                    int al = alpha;
                    if (key != null)
                    {
                        int k = key.Alpha(v0, v1, v2);
                        if (k <= 0) continue;
                        al = al * k >> 8;
                        if (key.GreenSpill && k < 256)
                        {
                            int lim = Math.Max(v0, v2);
                            if (v1 > lim) v1 = lim;
                        }
                    }
                    if (al >= 256)
                    {
                        dst[d] = (byte)v0;
                        dst[d + 1] = (byte)v1;
                        dst[d + 2] = (byte)v2;
                    }
                    else
                    {
                        int na = 256 - al;
                        dst[d] = (byte)((dst[d] * na + v0 * al) >> 8);
                        dst[d + 1] = (byte)((dst[d + 1] * na + v1 * al) >> 8);
                        dst[d + 2] = (byte)((dst[d + 2] * na + v2 * al) >> 8);
                    }
                    dst[d + 3] = 255;
                }
            });
        }

        /// dst = a × (1-k) + b × k
        public static void Blend(byte[] dst, byte[] a, byte[] b, double k)
        {
            int kb = (int)Math.Round(Math.Max(0, Math.Min(1, k)) * 256), ka = 256 - kb;
            int n = Math.Min(dst.Length, Math.Min(a.Length, b.Length));
            int rows = Math.Max(1, n / 65536);
            Parallel.For(0, rows, r =>
            {
                int s = (int)((long)n * r / rows) & ~3, e = (int)((long)n * (r + 1) / rows) & ~3;
                if (r == rows - 1) e = n;
                for (int i = s; i < e; i++) dst[i] = (byte)((a[i] * ka + b[i] * kb) >> 8);
            });
        }

        /// 색으로 섞기(k=1이면 그 색)
        public static void Toward(byte[] p, uint rgb, double k)
        {
            int kb = (int)Math.Round(Math.Max(0, Math.Min(1, k)) * 256), ka = 256 - kb;
            int b = (int)(rgb & 255) * kb, g = (int)((rgb >> 8) & 255) * kb, r = (int)((rgb >> 16) & 255) * kb;
            int n = p.Length / 4;
            int rows = Math.Max(1, n / 16384);
            Parallel.For(0, rows, part =>
            {
                int s = (int)((long)n * part / rows), e = (int)((long)n * (part + 1) / rows);
                for (int i = s * 4; i < e * 4; i += 4)
                {
                    p[i] = (byte)((p[i] * ka + b) >> 8);
                    p[i + 1] = (byte)((p[i + 1] * ka + g) >> 8);
                    p[i + 2] = (byte)((p[i + 2] * ka + r) >> 8);
                }
            });
        }

        /// WPF가 그린 그림(미리 곱한 BGRA)을 위에 얹는다
        public static void Over(byte[] dst, byte[] pbgra)
        {
            int n = Math.Min(dst.Length, pbgra.Length) / 4;
            int rows = Math.Max(1, n / 16384);
            Parallel.For(0, rows, part =>
            {
                int s = (int)((long)n * part / rows), e = (int)((long)n * (part + 1) / rows);
                for (int i = s * 4; i < e * 4; i += 4)
                {
                    int a = pbgra[i + 3];
                    if (a == 0) continue;
                    if (a == 255)
                    {
                        dst[i] = pbgra[i];
                        dst[i + 1] = pbgra[i + 1];
                        dst[i + 2] = pbgra[i + 2];
                        continue;
                    }
                    int na = 255 - a;
                    dst[i] = (byte)Math.Min(255, pbgra[i] + (dst[i] * na + 127) / 255);
                    dst[i + 1] = (byte)Math.Min(255, pbgra[i + 1] + (dst[i + 1] * na + 127) / 255);
                    dst[i + 2] = (byte)Math.Min(255, pbgra[i + 2] + (dst[i + 2] * na + 127) / 255);
                }
            });
        }

        // ── 가리기 ──

        static void Box(double l, double tp, double w, double h, int W, int H, out int x0, out int y0, out int bw, out int bh)
        {
            x0 = Clamp((int)Math.Floor(l * W), 0, W);
            y0 = Clamp((int)Math.Floor(tp * H), 0, H);
            int x1 = Clamp((int)Math.Ceiling((l + w) * W), 0, W), y1 = Clamp((int)Math.Ceiling((tp + h) * H), 0, H);
            bw = x1 - x0;
            bh = y1 - y0;
        }

        static bool Inside(int x, int y, int bw, int bh, bool oval)
        {
            if (!oval) return true;
            double u = (x + 0.5) / bw * 2 - 1, v = (y + 0.5) / bh * 2 - 1;
            return u * u + v * v <= 1;
        }

        /// 0~1 자리(원본 기준)의 상자를 흐리게·모자이크·색으로 가린다. strength 0~1.
        public static void Redact(byte[] p, int W, int H, double x, double y, double w, double h, string mode, double strength, bool oval, uint rgb)
        {
            int x0, y0, bw, bh;
            Box(x, y, w, h, W, H, out x0, out y0, out bw, out bh);
            if (bw <= 0 || bh <= 0) return;
            int shortSide = Math.Min(W, H);
            strength = Math.Max(0.05, Math.Min(1, strength));
            if (mode == "fill")
            {
                byte b = (byte)rgb, g = (byte)(rgb >> 8), r = (byte)(rgb >> 16);
                for (int yy = 0; yy < bh; yy++)
                {
                    int i = ((y0 + yy) * W + x0) * 4;
                    for (int xx = 0; xx < bw; xx++, i += 4)
                    {
                        if (!Inside(xx, yy, bw, bh, oval)) continue;
                        p[i] = b;
                        p[i + 1] = g;
                        p[i + 2] = r;
                    }
                }
                return;
            }
            if (mode == "mosaic")
            {
                int block = Math.Max(3, (int)Math.Round(strength * shortSide * 0.07));
                for (int by = 0; by < bh; by += block)
                {
                    int hh = Math.Min(block, bh - by);
                    for (int bx = 0; bx < bw; bx += block)
                    {
                        int ww = Math.Min(block, bw - bx);
                        long s0 = 0, s1 = 0, s2 = 0;
                        for (int yy = 0; yy < hh; yy++)
                        {
                            int i = ((y0 + by + yy) * W + x0 + bx) * 4;
                            for (int xx = 0; xx < ww; xx++, i += 4)
                            {
                                s0 += p[i];
                                s1 += p[i + 1];
                                s2 += p[i + 2];
                            }
                        }
                        int n = ww * hh;
                        byte c0 = (byte)(s0 / n), c1 = (byte)(s1 / n), c2 = (byte)(s2 / n);
                        for (int yy = 0; yy < hh; yy++)
                        {
                            int i = ((y0 + by + yy) * W + x0 + bx) * 4;
                            for (int xx = 0; xx < ww; xx++, i += 4)
                            {
                                if (!Inside(bx + xx, by + yy, bw, bh, oval)) continue;
                                p[i] = c0;
                                p[i + 1] = c1;
                                p[i + 2] = c2;
                            }
                        }
                    }
                }
                return;
            }
            // 흐리게: 상자보다 조금 넓게 떼어 흐린 뒤(가장자리가 덜 티 나게) 상자 안만 되돌려 놓는다
            int radius = Math.Max(2, (int)Math.Round(strength * shortSide * 0.05));
            int pad = Math.Min(radius, 24);
            int px0 = Math.Max(0, x0 - pad), py0 = Math.Max(0, y0 - pad);
            int px1 = Math.Min(W, x0 + bw + pad), py1 = Math.Min(H, y0 + bh + pad);
            int pw = px1 - px0, ph = py1 - py0;
            var part = new byte[pw * ph * 4];
            for (int yy = 0; yy < ph; yy++) Buffer.BlockCopy(p, ((py0 + yy) * W + px0) * 4, part, yy * pw * 4, pw * 4);
            // 큰 상자는 줄여서 흐리고 다시 늘린다(빠르고 더 고르게 흐려진다)
            int f = Math.Max(1, radius / 6);
            if (f > 1 && pw / f >= 4 && ph / f >= 4)
            {
                int sw = Math.Max(4, pw / f), sh = Math.Max(4, ph / f);
                var small = new byte[sw * sh * 4];
                Shrink(part, pw, ph, small, sw, sh);
                BoxBlur(small, new byte[small.Length], sw, sh, Math.Max(1, radius / f), 3);
                Cover(small, sw, sh, part, pw, ph);
            }
            else
            {
                BoxBlur(part, new byte[part.Length], pw, ph, Math.Min(radius, Math.Max(pw, ph)), 3);
            }
            for (int yy = 0; yy < bh; yy++)
            {
                int i = ((y0 + yy) * W + x0) * 4, s = ((y0 - py0 + yy) * pw + (x0 - px0)) * 4;
                for (int xx = 0; xx < bw; xx++, i += 4, s += 4)
                {
                    if (!Inside(xx, yy, bw, bh, oval)) continue;
                    p[i] = part[s];
                    p[i + 1] = part[s + 1];
                    p[i + 2] = part[s + 2];
                }
            }
        }

        // ── 색 ──

        /// 사진 손질과 같은 셈(밝기는 감마, 대비는 가운데 기준, 채도는 밝기 축)에 색온도와 영상용 느낌을 더했다.
        public static void Adjust(byte[] p, int w, int h, VClip c)
        {
            string f = c.Filter ?? "";
            double bright = c.Bright, contrast = c.Contrast, sat = 1 + c.Sat, warm = c.Warm;
            switch (f)
            {
                case "warm": warm += 0.35; sat += 0.08; break;
                case "cool": warm -= 0.35; break;
                case "vivid": sat += 0.35; contrast += 0.12; break;
                case "soft": contrast -= 0.18; bright += 0.06; sat -= 0.1; break;
                case "gray": sat = 0; break;
                case "old": sat = 0; break;
            }
            double gamma = Math.Pow(2, -bright * 1.25);
            double cf = contrast >= 0 ? 1 + contrast * 1.6 : 1 + contrast * 0.75;
            var lb = new byte[256];
            var lg = new byte[256];
            var lr = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double v = 255 * Math.Pow(i / 255.0, gamma);
                v = (v - 127.5) * cf + 127.5;
                lr[i] = Byte(v + warm * 28);
                lg[i] = Byte(v + warm * 6);
                lb[i] = Byte(v - warm * 30);
            }
            int s256 = (int)Math.Round(sat * 256);
            bool sepia = f == "old";
            Parallel.For(0, h, y =>
            {
                int i = y * w * 4;
                for (int x = 0; x < w; x++, i += 4)
                {
                    int bl = lb[p[i]], gr = lg[p[i + 1]], rd = lr[p[i + 2]];
                    if (s256 != 256)
                    {
                        int l = (bl * 29 + gr * 150 + rd * 77) >> 8;
                        bl = l + ((bl - l) * s256 >> 8);
                        gr = l + ((gr - l) * s256 >> 8);
                        rd = l + ((rd - l) * s256 >> 8);
                    }
                    if (sepia)
                    {
                        int l = (bl * 29 + gr * 150 + rd * 77) >> 8;
                        rd = l * 270 / 256 + 18;
                        gr = l * 240 / 256 + 8;
                        bl = l * 190 / 256;
                    }
                    p[i] = (byte)(bl < 0 ? 0 : bl > 255 ? 255 : bl);
                    p[i + 1] = (byte)(gr < 0 ? 0 : gr > 255 ? 255 : gr);
                    p[i + 2] = (byte)(rd < 0 ? 0 : rd > 255 ? 255 : rd);
                }
            });
            if (c.Sharp > 0)
            {
                int r = Math.Max(1, Math.Min(w, h) / 500);
                var soft = (byte[])p.Clone();
                BoxBlur(soft, new byte[soft.Length], w, h, r, 2);
                int amount = (int)Math.Round(c.Sharp * 1.8 * 256);
                Parallel.For(0, h, y =>
                {
                    int i = y * w * 4;
                    for (int x = 0; x < w; x++, i += 4)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            int v = p[i + k] + ((p[i + k] - soft[i + k]) * amount >> 8);
                            p[i + k] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                        }
                    }
                });
            }
        }

        /// 밝기만(얼굴 찾기·움직임 따라가기용)
        public static byte[] Gray(byte[] p, int w, int h)
        {
            var g = new byte[w * h];
            for (int i = 0, o = 0; o < g.Length; i += 4, o++) g[o] = (byte)((p[i] * 29 + p[i + 1] * 150 + p[i + 2] * 77) >> 8);
            return g;
        }

        /// 평균 밝기(0~255). 검은 화면 찾기용으로 띄엄띄엄 본다.
        public static double MeanLuma(byte[] p)
        {
            long s = 0;
            int n = 0;
            for (int i = 0; i + 2 < p.Length; i += 4 * 37)
            {
                s += (p[i] * 29 + p[i + 1] * 150 + p[i + 2] * 77) >> 8;
                n++;
            }
            return n == 0 ? 0 : (double)s / n;
        }
    }

    /// 사진 한 장(사진 방향 반영)
    sealed class VPic
    {
        public int W, H;
        public byte[] Px;
        /// 투명한 칸이 있는 그림(PNG 로고·스티커)
        public bool Alpha;

        public static VPic Load(string path, int maxLong)
        {
            BitmapSource src = Imaging.Load(path);
            src = Imaging.Fit(src, Math.Max(16, maxLong));
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            bool alpha = false;
            for (int i = 3; i < px.Length; i += 4)
            {
                if (px[i] == 255) continue;
                alpha = true;
                int a = px[i];
                px[i - 3] = (byte)(px[i - 3] * a / 255);
                px[i - 2] = (byte)(px[i - 2] * a / 255);
                px[i - 1] = (byte)(px[i - 1] * a / 255);
                px[i] = 255;
            }
            return new VPic { W = w, H = h, Px = px, Alpha = alpha };
        }
    }

    /// 파일 정보(한 번 읽어 둔다)
    static class VMedia
    {
        static readonly Dictionary<string, MediaProbe> Probes = new Dictionary<string, MediaProbe>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> Problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static MediaProbe Probe(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (Probes)
            {
                MediaProbe p;
                if (Probes.TryGetValue(path, out p)) return p;
            }
            MediaProbe r = null;
            try
            {
                if (File.Exists(path)) r = MediaProbe.Read(path);
            }
            catch (UserFacingException ex)
            {
                lock (Probes) Problems[path] = ex.Message;
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집: 파일 정보 읽기 실패 " + ex.GetType().Name);
            }
            lock (Probes) Probes[path] = r;
            return r;
        }

        public static void Forget(string path)
        {
            lock (Probes)
            {
                Probes.Remove(path);
                Problems.Remove(path);
            }
        }

        public static string Problem(string path)
        {
            lock (Probes)
            {
                string s;
                return Problems.TryGetValue(path, out s) ? s : null;
            }
        }

        public static void SetProblem(string path, string message)
        {
            lock (Probes) Problems[path] = message;
        }

        public static bool IsVideo(string p)
        {
            return Formats.KindOf(p) == Kind.Video;
        }

        public static bool IsImage(string p)
        {
            return Formats.KindOf(p) == Kind.Image;
        }

        public static bool IsAudio(string p)
        {
            return Formats.KindOf(p) == Kind.Audio;
        }
    }

    /// 움직임: 움직임 표시(키프레임) 사이를 잇고, 나타나기·사라지기를 계산한다
    static class VAnim
    {
        public static double Clamp01(double x)
        {
            return x < 0 ? 0 : x > 1 ? 1 : x;
        }

        public static double Ease(double x)
        {
            x = Clamp01(x);
            return x * x * (3 - 2 * x);
        }

        public static double EaseOut(double x)
        {
            x = Clamp01(x);
            return 1 - (1 - x) * (1 - x) * (1 - x);
        }

        public static double Back(double x)
        {
            x = Clamp01(x);
            const double c1 = 1.70158, c3 = c1 + 1;
            return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
        }

        /// 표시들 사이를 부드럽게 잇는다(앞뒤 밖에서는 처음·마지막 값)
        public static VKey At(List<VKey> keys, double t)
        {
            if (keys == null || keys.Count == 0) return null;
            if (keys.Count == 1 || t <= keys[0].T) return keys[0];
            var last = keys[keys.Count - 1];
            if (t >= last.T) return last;
            for (int i = 0; i + 1 < keys.Count; i++)
            {
                var a = keys[i];
                var b = keys[i + 1];
                if (t < a.T || t > b.T) continue;
                double k = b.T - a.T < 1e-6 ? 1 : Ease((t - a.T) / (b.T - a.T));
                // 기울기는 가까운 쪽으로 돈다(170°→-170°는 20°만). 한쪽에만 있으면 그 값.
                double ra = a.HasRot ? a.Rot : b.Rot, rb = b.HasRot ? b.Rot : a.Rot;
                double dr = ((rb - ra + 180) % 360 + 360) % 360 - 180;
                return new VKey
                {
                    T = t,
                    X = a.X + (b.X - a.X) * k,
                    Y = a.Y + (b.Y - a.Y) * k,
                    W = a.W + (b.W - a.W) * k,
                    H = a.H + (b.H - a.H) * k,
                    Zoom = a.Zoom + (b.Zoom - a.Zoom) * k,
                    PanX = a.PanX + (b.PanX - a.PanX) * k,
                    PanY = a.PanY + (b.PanY - a.PanY) * k,
                    Opacity = a.Opacity + (b.Opacity - a.Opacity) * k,
                    Rot = ra + dr * k,
                    HasRot = a.HasRot || b.HasRot
                };
            }
            return last;
        }

        /// 조각의 확대·자리. 영상 조각의 표시는 원본 시간, 그 밖의 조각은 조각 안 시간에 붙는다.
        public static void ClipMotion(VClip c, double local, double source, out double zoom, out double panX, out double panY)
        {
            zoom = c.Zoom;
            panX = c.PanX;
            panY = c.PanY;
            var k = At(c.Keys, c.Kind == "video" ? source : local);
            if (k == null) return;
            zoom = k.Zoom;
            panX = k.PanX;
            panY = k.PanY;
        }

        /// 사진이 천천히 다가가거나 옆으로 흐르기
        public static void Ken(VClip c, double local, ref double zoom, ref double panX, ref double panY)
        {
            if (c.Kind != "photo") return;
            double u = Ease(local / Math.Max(0.1, c.Length));
            switch (c.Ken)
            {
                case "in": zoom *= 1 + 0.12 * u; break;
                case "out": zoom *= 1.12 - 0.12 * u; break;
                case "left":
                    zoom *= 1.1;
                    panX += 0.035 - 0.07 * u;
                    break;
                case "right":
                    zoom *= 1.1;
                    panX += -0.035 + 0.07 * u;
                    break;
            }
        }

        public sealed class State
        {
            public bool Visible;
            public double X, Y, Scale = 1, Opacity = 1, Dx, Dy;
            /// 기울기(도): 표시가 있으면 표시 사이를 이은 값
            public double Rot;
            /// 글자가 하나씩 나타나기: 보이는 몫(0~1)
            public double Reveal = 1;
        }

        public static State Item(VItem it, double t)
        {
            var s = new State();
            s.Rot = it.Rot;
            if (t < it.Start - 1e-6 || t >= it.End) return s;
            s.Visible = true;
            double local = t - it.Start;
            s.X = it.X;
            s.Y = it.Y;
            s.Opacity = it.Opacity;
            var k = At(it.Keys, local);
            if (k != null)
            {
                s.X = k.X;
                s.Y = k.Y;
                s.Scale = k.Zoom;
                s.Opacity *= k.Opacity;
                if (k.HasRot) s.Rot = k.Rot;
            }
            double a = Math.Max(0.05, Math.Min(it.AnimSec, it.Length / 2));
            double e = local / a;
            switch (it.In)
            {
                case "fade":
                    s.Opacity *= Ease(e);
                    break;
                case "pop":
                    s.Scale *= 0.55 + 0.45 * Back(e);
                    s.Opacity *= Clamp01(e * 2);
                    break;
                case "rise":
                    s.Dy = (1 - EaseOut(e)) * 0.05;
                    s.Opacity *= Ease(e);
                    break;
                case "slide":
                    s.Dx = -(1 - EaseOut(e)) * 0.08;
                    s.Opacity *= Ease(e);
                    break;
                case "type":
                    int n = string.IsNullOrEmpty(it.Text) ? 1 : it.Text.Length;
                    double dur = Math.Min(it.Length * 0.7, Math.Max(a, n * 0.045));
                    s.Reveal = Clamp01(local / dur);
                    break;
            }
            if (it.Out == "fade")
            {
                double left = (it.End - t) / a;
                if (left < 1) s.Opacity *= Ease(left);
            }
            if (s.Opacity <= 0.003) s.Visible = it.In == "type";
            return s;
        }

        /// 가리기 상자의 자리(원본 0~1)
        public static Rect Redact(VRedact r, double source)
        {
            var k = At(r.Keys, source);
            if (k == null) return new Rect(r.X, r.Y, Math.Max(0.002, r.W), Math.Max(0.002, r.H));
            return new Rect(k.X, k.Y, Math.Max(0.002, k.W), Math.Max(0.002, k.H));
        }
    }

    /// 흔들림 줄이기: 장면 사이의 움직임을 재서, 부드럽게 다듬은 길과의 차이만큼 반대로 옮긴다.
    static class VStab
    {
        sealed class Track
        {
            public double[] T, Dx, Dy;
            public volatile bool Ready, Failed;
            public volatile float Progress;
        }

        static readonly Dictionary<string, Track> Tracks = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        /// 흔들림을 숨기려고 이만큼 키운다
        public const double Zoom = 1.08;
        const int AW = 160;

        public static float Progress(string path)
        {
            lock (Tracks)
            {
                Track t;
                return Tracks.TryGetValue(path, out t) ? (t.Ready ? 1f : t.Progress) : 0f;
            }
        }

        public static bool Ready(string path)
        {
            lock (Tracks)
            {
                Track t;
                return Tracks.TryGetValue(path, out t) && t.Ready;
            }
        }

        /// 계산을 시작한다(이미 했으면 그대로). wait이면 끝날 때까지 기다린다.
        public static void Ensure(string path, bool wait, Func<bool> stop)
        {
            Track t;
            bool start = false;
            lock (Tracks)
            {
                if (!Tracks.TryGetValue(path, out t))
                {
                    t = new Track();
                    Tracks[path] = t;
                    start = true;
                }
            }
            if (start)
            {
                var th = new Thread(delegate () { Compute(t, path); }) { IsBackground = true, Name = "흔들림 재기", Priority = ThreadPriority.BelowNormal };
                th.SetApartmentState(ApartmentState.MTA);
                th.Start();
            }
            while (wait && !t.Ready && !t.Failed)
            {
                if (stop != null && stop()) return;
                Thread.Sleep(30);
            }
        }

        /// 원본 시각 t에서 옮길 몫(원본 크기에 대한 비율)
        public static bool At(string path, double t, out double dx, out double dy)
        {
            dx = dy = 0;
            Track tr;
            lock (Tracks)
            {
                if (!Tracks.TryGetValue(path, out tr) || !tr.Ready) return false;
            }
            var T = tr.T;
            if (T.Length == 0) return true;
            int i = Array.BinarySearch(T, t);
            if (i < 0) i = ~i - 1;
            if (i < 0) i = 0;
            if (i >= T.Length - 1)
            {
                dx = tr.Dx[T.Length - 1];
                dy = tr.Dy[T.Length - 1];
                return true;
            }
            double k = T[i + 1] - T[i] < 1e-6 ? 0 : (t - T[i]) / (T[i + 1] - T[i]);
            dx = tr.Dx[i] + (tr.Dx[i + 1] - tr.Dx[i]) * k;
            dy = tr.Dy[i] + (tr.Dy[i + 1] - tr.Dy[i]) * k;
            return true;
        }

        static void Compute(Track tr, string path)
        {
            try
            {
                var times = new List<double>();
                var mx = new List<double>();
                var my = new List<double>();
                using (var r = new VideoReader(path, AW, AW, true))
                {
                    int w = r.Width, h = r.Height;
                    double dur = r.Duration > 0 ? r.Duration : 1;
                    byte[] prev = null;
                    double x = 0, y = 0, ft;
                    double t = 0, last = -1;
                    while (true)
                    {
                        var px = r.At(t, out ft);
                        if (px == null || ft <= last + 1e-6)
                        {
                            if (t > dur + 0.5) break;
                            t += r.Step;
                            if (px == null || t > dur + 0.5) break;
                            continue;
                        }
                        last = ft;
                        var g = VPx.Gray(px, w, h);
                        if (prev != null)
                        {
                            int u, v;
                            Shift(prev, g, w, h, out u, out v);
                            x += u;
                            y += v;
                        }
                        prev = g;
                        times.Add(ft);
                        mx.Add(x / w);
                        my.Add(y / h);
                        tr.Progress = (float)Math.Min(0.99, ft / dur);
                        t = ft + r.Step;
                    }
                }
                int n = times.Count;
                var sx = Smooth(mx, 15);
                var sy = Smooth(my, 15);
                var dx = new double[n];
                var dy = new double[n];
                const double lim = (Zoom - 1) / 2 - 0.004;
                for (int i = 0; i < n; i++)
                {
                    dx[i] = Math.Max(-lim, Math.Min(lim, sx[i] - mx[i]));
                    dy[i] = Math.Max(-lim, Math.Min(lim, sy[i] - my[i]));
                }
                tr.T = times.ToArray();
                tr.Dx = dx;
                tr.Dy = dy;
                tr.Ready = true;
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집: 흔들림 재기 실패 " + ex.GetType().Name);
                tr.T = new double[0];
                tr.Dx = new double[0];
                tr.Dy = new double[0];
                tr.Failed = true;
                tr.Ready = true;
            }
        }

        static double[] Smooth(List<double> v, int half)
        {
            int n = v.Count;
            var o = new double[n];
            for (int pass = 0; pass < 2; pass++)
            {
                var src = pass == 0 ? v.ToArray() : (double[])o.Clone();
                for (int i = 0; i < n; i++)
                {
                    double s = 0;
                    int c = 0;
                    for (int k = Math.Max(0, i - half); k <= Math.Min(n - 1, i + half); k++)
                    {
                        s += src[k];
                        c++;
                    }
                    o[i] = s / c;
                }
            }
            return o;
        }

        /// b가 a에서 얼마나 옮겨졌는지(가운데 부분을 맞춰 본다). 반 크기에서 크게, 제 크기에서 다듬는다.
        static void Shift(byte[] a, byte[] b, int w, int h, out int u, out int v)
        {
            int hw = w / 2, hh = h / 2;
            var a2 = Half(a, w, h);
            var b2 = Half(b, w, h);
            int bu = 0, bv = 0;
            Best(a2, b2, hw, hh, 0, 0, 5, ref bu, ref bv);
            int fu = bu * 2, fv = bv * 2;
            Best(a, b, w, h, fu, fv, 2, ref fu, ref fv);
            u = fu;
            v = fv;
        }

        static byte[] Half(byte[] g, int w, int h)
        {
            int hw = w / 2, hh = h / 2;
            var o = new byte[hw * hh];
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < hw; x++)
                    o[y * hw + x] = (byte)((g[(2 * y) * w + 2 * x] + g[(2 * y) * w + 2 * x + 1] + g[(2 * y + 1) * w + 2 * x] + g[(2 * y + 1) * w + 2 * x + 1]) >> 2);
            return o;
        }

        static void Best(byte[] a, byte[] b, int w, int h, int cu, int cv, int range, ref int bu, ref int bv)
        {
            int mx = w / 6, my = h / 6;
            long best = long.MaxValue;
            int ru = cu, rv = cv;
            for (int dv = cv - range; dv <= cv + range; dv++)
            {
                for (int du = cu - range; du <= cu + range; du++)
                {
                    long s = 0;
                    for (int y = my; y < h - my; y += 2)
                    {
                        int yb = y + dv;
                        if (yb < 0 || yb >= h) continue;
                        int ia = y * w, ib = yb * w + du;
                        for (int x = mx; x < w - mx; x += 2)
                        {
                            int xb = x + du;
                            if (xb < 0 || xb >= w) continue;
                            int d = a[ia + x] - b[ib + x];
                            s += d < 0 ? -d : d;
                        }
                    }
                    // 같은 차이면 덜 움직인 쪽
                    s = s * 64 + Math.Abs(du - cu) + Math.Abs(dv - cv);
                    if (s < best)
                    {
                        best = s;
                        ru = du;
                        rv = dv;
                    }
                }
            }
            bu = ru;
            bv = rv;
        }
    }

    /// 프로젝트의 한 시각을 그림 한 장으로. 한 스레드에서만 쓴다(미리 보기 엔진 또는 내보내기).
    sealed class VRenderer : IDisposable
    {
        public readonly int W, H;
        public readonly bool Export;
        public bool AllowGpu = true;
        /// 마지막으로 그리지 못한 이유(사용자에게 보일 문장)
        public string Problem;

        VProject p;
        int version = -1;
        readonly byte[] canvas, work2, prevBuf;
        byte[] srcWork, small, smallTmp;
        int smallW, smallH;

        sealed class Opened
        {
            public VideoReader Reader;
            public long Used;
        }

        readonly Dictionary<string, Opened> readers = new Dictionary<string, Opened>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, VPic> pics = new Dictionary<string, VPic>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long tick;
        public int MaxReaders = 4;

        // 거꾸로 보여 주기: 0.5초 덩어리
        string revKey;
        List<KeyValuePair<double, byte[]>> revFrames;
        int revW, revH;

        // 전환: 앞 조각의 마지막 장면
        readonly Dictionary<string, byte[]> lastOf = new Dictionary<string, byte[]>();

        public VRenderer(int w, int h, bool export)
        {
            W = Math.Max(2, w & ~1);
            H = Math.Max(2, h & ~1);
            Export = export;
            canvas = new byte[W * H * 4];
            work2 = new byte[W * H * 4];
            prevBuf = new byte[W * H * 4];
            if (export) MaxReaders = 3;
        }

        public byte[] Canvas
        {
            get { return canvas; }
        }

        public VProject Project
        {
            get { return p; }
        }

        /// 새 내용(복사본)으로 바꾼다. 내용이 바뀌면 전환용 장면 기억을 지운다.
        public void Use(VProject snapshot, int ver)
        {
            if (ver == version && snapshot == p) return;
            p = snapshot;
            version = ver;
            lastOf.Clear();
        }

        public byte[] Frame(double t)
        {
            Problem = null;
            if (p == null || p.Clips.Count == 0)
            {
                VPx.Fill(canvas, 0);
                return canvas;
            }
            double local;
            int i = p.ClipAt(t, out local);
            if (i < 0)
            {
                VPx.Fill(canvas, 0);
                return canvas;
            }
            var c = p.Clips[i];
            DrawClip(c, local, canvas);
            // 조각 끝 장면은 다음 조각의 전환에 쓴다(차례로 재생할 때 다시 풀지 않게)
            if (i + 1 < p.Clips.Count && p.Clips[i + 1].Trans != "none" && local >= c.Length - 1.5 / Math.Max(10, p.Fps))
                lastOf[c.Id] = (byte[])canvas.Clone();
            string tr = c.Trans ?? "none";
            if (tr != "none" && c.TransSec > 0.01 && local < c.TransSec && (i > 0 || tr == "black" || tr == "white"))
            {
                double k = VAnim.Ease(local / c.TransSec);
                byte[] from = i > 0 ? LastFrame(i - 1) : null;
                Transition(tr, from, k);
            }
            DrawPips(t);
            if (p.EndFade)
            {
                double d = p.Duration;
                if (t > d - 1.2) VPx.Toward(canvas, 0, VAnim.Ease((t - (d - 1.2)) / 1.2));
            }
            return canvas;
        }

        byte[] LastFrame(int index)
        {
            var c = p.Clips[index];
            byte[] got;
            if (lastOf.TryGetValue(c.Id, out got)) return got;
            var buf = new byte[canvas.Length];
            DrawClip(c, Math.Max(0, c.Length - 1.0 / Math.Max(10, p.Fps)), buf);
            lastOf[c.Id] = buf;
            if (lastOf.Count > 6) lastOf.Remove(lastOf.Keys.First(k => k != c.Id));
            return buf;
        }

        void Transition(string kind, byte[] from, double k)
        {
            if (from == null)
            {
                // 첫 조각: 검정·흰색에서 나타나기
                VPx.Toward(canvas, kind == "white" ? 0xFFFFFFu : 0u, 1 - k);
                return;
            }
            switch (kind)
            {
                case "black":
                case "white":
                    uint color = kind == "white" ? 0xFFFFFFu : 0u;
                    if (k < 0.5)
                    {
                        Buffer.BlockCopy(from, 0, canvas, 0, canvas.Length);
                        VPx.Toward(canvas, color, k * 2);
                    }
                    else VPx.Toward(canvas, color, (1 - k) * 2);
                    break;
                case "slide":
                    {
                        // 새 장면이 오른쪽에서 밀고 들어온다
                        Buffer.BlockCopy(canvas, 0, work2, 0, canvas.Length);
                        int shift = (int)Math.Round(W * (1 - k));
                        int row = W * 4;
                        for (int y = 0; y < H; y++)
                        {
                            int o = y * row;
                            if (shift > 0) Buffer.BlockCopy(from, o + (W - shift) * 4, canvas, o, shift * 4);
                            if (W - shift > 0) Buffer.BlockCopy(work2, o, canvas, o + shift * 4, (W - shift) * 4);
                        }
                        break;
                    }
                case "wipe":
                    {
                        int edge = (int)Math.Round(W * k);
                        int row = W * 4;
                        for (int y = 0; y < H; y++)
                        {
                            int o = y * row;
                            if (W - edge > 0) Buffer.BlockCopy(from, o + edge * 4, canvas, o + edge * 4, (W - edge) * 4);
                        }
                        break;
                    }
                case "zoom":
                    {
                        // 앞 장면이 다가오며 사라진다
                        Buffer.BlockCopy(canvas, 0, work2, 0, canvas.Length);
                        double z = 1 + 0.25 * k;
                        var m = Matrix.Identity;
                        m.Translate(-W / 2.0, -H / 2.0);
                        m.Scale(z, z);
                        m.Translate(W / 2.0, H / 2.0);
                        VPx.Place(from, W, H, new Rect(0, 0, W, H), work2, W, H, m, 1 - k, false, null);
                        Buffer.BlockCopy(work2, 0, canvas, 0, canvas.Length);
                        break;
                    }
                default:
                    VPx.Blend(canvas, from, canvas, k);
                    break;
            }
        }

        // ── 조각 하나 그리기 ──

        void DrawClip(VClip c, double local, byte[] dst)
        {
            if (c.Kind == "color")
            {
                VPx.Fill(dst, c.Rgb);
                return;
            }
            double source = c.SourceAt(local);
            int fw, fh, rot = 0;
            byte[] frame = SourceFrame(c, local, source, out fw, out fh, out rot);
            if (frame == null)
            {
                VPx.Fill(dst, 0x2A2D36);
                return;
            }
            bool redact = c.Redacts.Any(r => !r.Off && source >= r.Start - 1e-6 && source <= r.End + 1e-6);
            if (redact || c.HasColor)
            {
                if (srcWork == null || srcWork.Length != frame.Length) srcWork = new byte[frame.Length];
                Buffer.BlockCopy(frame, 0, srcWork, 0, frame.Length);
                frame = srcWork;
                if (redact)
                {
                    foreach (var r in c.Redacts)
                    {
                        if (r.Off || source < r.Start - 1e-6 || source > r.End + 1e-6) continue;
                        var b = VAnim.Redact(r, source);
                        VPx.Redact(frame, fw, fh, b.X, b.Y, b.Width, b.Height, r.Mode, r.Strength, r.Oval, r.Rgb);
                    }
                }
                if (c.HasColor) VPx.Adjust(frame, fw, fh, c);
            }
            Rect clip;
            bool covers;
            var m = PlaceMatrix(c, local, source, fw, fh, rot, false, out clip, out covers);
            if (!covers) DrawBack(c, local, source, frame, fw, fh, rot, dst);
            VPx.Place(frame, fw, fh, clip, dst, W, H, m, 1, false, null);
        }

        /// 원본 그림 한 장(돌리기 전). rot은 파일에 적힌 돌림.
        byte[] SourceFrame(VClip c, double local, double source, out int fw, out int fh, out int rot)
        {
            fw = fh = 0;
            rot = 0;
            if (string.IsNullOrEmpty(c.Path)) return null;
            if (c.Kind == "photo")
            {
                var pic = Pic(c.Path, PhotoLong(c));
                if (pic == null) return null;
                fw = pic.W;
                fh = pic.H;
                return pic.Px;
            }
            var probe = VMedia.Probe(c.Path);
            if (probe == null)
            {
                Problem = File.Exists(c.Path) ? (VMedia.Problem(c.Path) ?? VideoReader.Unsupported(c.Path)) : "원본 파일을 찾지 못했어요: " + Path.GetFileName(c.Path);
                return null;
            }
            rot = probe.Rotation;
            var r = Reader(c.Path, DecodeLong(c, probe));
            if (r == null) return null;
            fw = r.Width;
            fh = r.Height;
            double ft;
            if (c.Kind == "freeze") return r.At(c.FreezeAt, out ft);
            if (c.Reverse) return Reverse(r, c, source);
            var px = r.At(source, out ft);
            if (px != null && c.Smooth && c.Speed < 0.95)
            {
                // 느린 화면을 부드럽게: 지금 장과 다음 장을 시간 비율대로 섞는다
                double t0 = ft;
                var copy = Scratch2(px.Length);
                Buffer.BlockCopy(px, 0, copy, 0, px.Length);
                double t1;
                var next = r.At(t0 + r.Step * 1.01, out t1);
                if (next != null && t1 > t0 + 1e-4)
                {
                    double k = VAnim.Clamp01((source - t0) / (t1 - t0));
                    VPx.Blend(copy, copy, next, k);
                }
                r.At(source, out ft);
                return copy;
            }
            return px;
        }

        byte[] scratch2;

        byte[] Scratch2(int n)
        {
            if (scratch2 == null || scratch2.Length != n) scratch2 = new byte[n];
            return scratch2;
        }

        byte[] Reverse(VideoReader r, VClip c, double source)
        {
            const double chunk = 0.5;
            int idx = (int)Math.Floor((source - c.In) / chunk);
            double a = c.In + idx * chunk, b = a + chunk;
            string key = r.Source + "|" + r.Width + "|" + idx + "|" + c.In.ToString("F3");
            if (key != revKey || revFrames == null)
            {
                revFrames = r.Range(a - r.Step, b);
                revKey = key;
                revW = r.Width;
                revH = r.Height;
            }
            if (revFrames.Count == 0) return null;
            byte[] best = revFrames[0].Value;
            foreach (var kv in revFrames)
            {
                if (kv.Key <= source + 1e-4) best = kv.Value;
                else break;
            }
            return best;
        }

        int PhotoLong(VClip c)
        {
            return Math.Min(4096, (int)(Math.Max(W, H) * 1.35 * Math.Max(1, c.Zoom)));
        }

        VPic Pic(string path, int maxLong)
        {
            string key = path + "|" + maxLong;
            VPic pic;
            if (pics.TryGetValue(key, out pic)) return pic;
            if (broken.Contains(path)) return null;
            try
            {
                if (!File.Exists(path))
                {
                    Problem = "원본 파일을 찾지 못했어요: " + Path.GetFileName(path);
                    return null;
                }
                pic = VPic.Load(path, maxLong);
            }
            catch (Exception ex)
            {
                broken.Add(path);
                Problem = ex is UserFacingException ? ex.Message : "사진을 열지 못했어요: " + Path.GetFileName(path);
                return null;
            }
            if (pics.Count > 24) pics.Clear();
            pics[key] = pic;
            return pic;
        }

        static readonly int[] Steps = { 256, 384, 512, 640, 768, 960, 1280, 1600, 1920, 2560, 3840 };

        /// 이 조각을 풀 크기(긴 변). 화면에 보일 크기만큼만 풀어 빠르게.
        int DecodeLong(VClip c, MediaProbe pr)
        {
            int nativeLong = Math.Max(pr.Width, pr.Height);
            int rot = ((pr.Rotation + c.Rotate) % 360 + 360) % 360;
            bool side = rot % 180 != 0;
            double ow = side ? pr.Height : pr.Width, oh = side ? pr.Width : pr.Height;
            double cw = Math.Max(0.05, c.CropW) * ow, ch = Math.Max(0.05, c.CropH) * oh;
            double s = c.Fit == "fill" ? Math.Max(W / cw, H / ch) : Math.Min(W / cw, H / ch);
            double z = Math.Max(1, c.Zoom);
            foreach (var k in c.Keys) z = Math.Max(z, k.Zoom);
            if (c.Stabilize) z *= VStab.Zoom;
            double need = nativeLong * s * z * (Export ? 1.0 : 1.15);
            return Quantize(need, nativeLong);
        }

        static int Quantize(double need, int nativeLong)
        {
            foreach (int st in Steps)
                if (st >= need) return Math.Min(st, nativeLong);
            return nativeLong;
        }

        /// 미리 보기에서만: 가벼운 사본이 있으면 원본 대신 읽는다(크게 확대해 그릴 때는 원본)
        public bool UseProxy;
        readonly Dictionary<string, string> proxyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int proxyVer = -1;

        string ProxyFor(string path, int longSide)
        {
            if (!UseProxy || longSide > VProxy.MaxDrawLong) return null;
            int v = VProxy.Version;
            if (v != proxyVer)
            {
                proxyOf.Clear();
                proxyVer = v;
            }
            string px;
            if (!proxyOf.TryGetValue(path, out px))
            {
                px = VProxy.Find(path);
                proxyOf[path] = px;
            }
            return px;
        }

        VideoReader Reader(string path, int longSide)
        {
            string proxy = ProxyFor(path, longSide);
            if (proxy != null)
            {
                string pkey = proxy + "|" + longSide;
                Opened po;
                tick++;
                if (readers.TryGetValue(pkey, out po))
                {
                    po.Used = tick;
                    return po.Reader;
                }
                while (readers.Count >= MaxReaders)
                {
                    var old = readers.OrderBy(kv => kv.Value.Used).First();
                    old.Value.Reader.Dispose();
                    readers.Remove(old.Key);
                }
                try
                {
                    var pr = new VideoReader(proxy, longSide, longSide, AllowGpu);
                    readers[pkey] = new Opened { Reader = pr, Used = tick };
                    return pr;
                }
                catch (Exception ex)
                {
                    // 사본이 깨졌으면 지우고 원본으로
                    Log.Write("영상 편집: 미리 보기 사본 열기 실패 " + ex.GetType().Name);
                    VProxy.Drop(path);
                    proxyOf[path] = null;
                }
            }
            string key = path + "|" + longSide;
            Opened o;
            tick++;
            if (readers.TryGetValue(key, out o))
            {
                o.Used = tick;
                return o.Reader;
            }
            if (broken.Contains(path)) return null;
            if (!File.Exists(path))
            {
                Problem = "원본 파일을 찾지 못했어요: " + Path.GetFileName(path);
                return null;
            }
            while (readers.Count >= MaxReaders)
            {
                var old = readers.OrderBy(kv => kv.Value.Used).First();
                old.Value.Reader.Dispose();
                readers.Remove(old.Key);
            }
            try
            {
                var r = new VideoReader(path, longSide, longSide, AllowGpu);
                readers[key] = new Opened { Reader = r, Used = tick };
                return r;
            }
            catch (Exception ex)
            {
                broken.Add(path);
                string msg = ex is UserFacingException ? ex.Message : VideoReader.Unsupported(path);
                VMedia.SetProblem(path, msg);
                Problem = msg;
                Log.Write("영상 편집: 영상 열기 실패 " + ex.GetType().Name);
                return null;
            }
        }

        public Matrix PlaceMatrix(VClip c, double local, double source, int fw, int fh, int metaRot, bool forBack, out Rect clip, out bool covers)
        {
            return PlaceOn(c, local, source, fw, fh, metaRot, forBack, W, H, out clip, out covers);
        }

        /// 원본 픽셀 → 화면 픽셀. clip은 원본에서 보일 부분, covers는 화면을 다 덮는지.
        public static Matrix PlaceOn(VClip c, double local, double source, int fw, int fh, int metaRot, bool forBack, int W, int H, out Rect clip, out bool covers)
        {
            int rot = ((metaRot + c.Rotate) % 360 + 360) % 360;
            bool side = rot % 180 != 0;
            double ow = side ? fh : fw, oh = side ? fw : fh;
            double cx = Math.Max(0, Math.Min(0.95, c.CropX)), cy = Math.Max(0, Math.Min(0.95, c.CropY));
            double cw = Math.Max(0.05, Math.Min(1 - cx, c.CropW)), ch = Math.Max(0.05, Math.Min(1 - cy, c.CropH));
            double cropW = cw * ow, cropH = ch * oh;
            bool fill = forBack || c.Fit == "fill";
            double baseS = fill ? Math.Max(W / cropW, H / cropH) : Math.Min(W / cropW, H / cropH);
            double zoom = 1, panX = 0, panY = 0, sx = 0, sy = 0;
            if (!forBack)
            {
                VAnim.ClipMotion(c, local, source, out zoom, out panX, out panY);
                VAnim.Ken(c, local, ref zoom, ref panX, ref panY);
                double dx, dy;
                if (c.Stabilize && c.Kind == "video" && VStab.At(c.Path, source, out dx, out dy))
                {
                    sx = dx * fw;
                    sy = dy * fh;
                    zoom *= VStab.Zoom;
                }
            }
            var m = Matrix.Identity;
            m.Translate(-fw / 2.0 + sx, -fh / 2.0 + sy);
            if (rot != 0) m.Rotate(rot);
            if (c.FlipH || c.FlipV) m.Scale(c.FlipH ? -1 : 1, c.FlipV ? -1 : 1);
            m.Translate(-(cx + cw / 2 - 0.5) * ow, -(cy + ch / 2 - 0.5) * oh);
            m.Scale(baseS * zoom, baseS * zoom);
            m.Translate(W / 2.0 + panX * W, H / 2.0 + panY * H);

            var o = Matrix.Identity;
            o.Translate(-fw / 2.0, -fh / 2.0);
            if (rot != 0) o.Rotate(rot);
            if (c.FlipH || c.FlipV) o.Scale(c.FlipH ? -1 : 1, c.FlipV ? -1 : 1);
            o.Invert();
            var a = o.Transform(new Point((cx - 0.5) * ow, (cy - 0.5) * oh));
            var b = o.Transform(new Point((cx + cw - 0.5) * ow, (cy + ch - 0.5) * oh));
            clip = new Rect(a, b);
            clip.Intersect(new Rect(0, 0, fw, fh));
            covers = false;
            if (!clip.IsEmpty && m.HasInverse)
            {
                var inv = m;
                inv.Invert();
                covers = true;
                foreach (var pt in new[] { new Point(0, 0), new Point(W, 0), new Point(0, H), new Point(W, H) })
                {
                    var s = inv.Transform(pt);
                    if (s.X < clip.Left - 0.6 || s.X > clip.Right + 0.6 || s.Y < clip.Top - 0.6 || s.Y > clip.Bottom + 0.6)
                    {
                        covers = false;
                        break;
                    }
                }
            }
            return m;
        }

        /// 영상이 화면을 다 덮지 않을 때 빈 곳: 흐리게(같은 장면을 크게 흐려서) · 검정 · 색
        void DrawBack(VClip c, double local, double source, byte[] frame, int fw, int fh, int rot, byte[] dst)
        {
            string mode = p.Back ?? "blur";
            if (mode == "black")
            {
                VPx.Fill(dst, 0);
                return;
            }
            if (mode == "color")
            {
                VPx.Fill(dst, p.BackRgb);
                return;
            }
            int sw = Math.Max(16, W / 8), sh = Math.Max(16, H / 8);
            if (small == null || smallW != sw || smallH != sh)
            {
                small = new byte[sw * sh * 4];
                smallTmp = new byte[small.Length];
                smallW = sw;
                smallH = sh;
            }
            VPx.Fill(small, 0);
            Rect clip;
            bool covers;
            var m = PlaceMatrix(c, local, source, fw, fh, rot, true, out clip, out covers);
            m.Scale(sw / (double)W, sh / (double)H);
            VPx.Place(frame, fw, fh, clip, small, sw, sh, m, 1, false, null);
            VPx.BoxBlur(small, smallTmp, sw, sh, Math.Max(2, (int)Math.Round(Math.Min(sw, sh) * 0.06)), 3);
            VPx.Cover(small, sw, sh, dst, W, H);
            VPx.Toward(dst, 0, 0.18);
        }

        // ── 작은 화면(영상 위의 영상) ──

        void DrawPips(double t)
        {
            foreach (var it in p.Items)
            {
                if (it.Kind != "pip" || t < it.Start || t >= it.End) continue;
                var st = VAnim.Item(it, t);
                if (!st.Visible || st.Opacity <= 0.003) continue;
                Pip(it, st, t, canvas);
            }
        }

        public static void PipRect(VItem it, VAnim.State st, double srcAspect, int W, int H, out Rect r)
        {
            double w = it.W * W * st.Scale;
            double h = it.Circle ? w : w / Math.Max(0.1, srcAspect);
            double cx = (st.X + st.Dx) * W, cy = (st.Y + st.Dy) * H;
            r = new Rect(cx - w / 2, cy - h / 2, w, h);
        }

        void Pip(VItem it, VAnim.State st, double t, byte[] dst)
        {
            if (string.IsNullOrEmpty(it.Path)) return;
            byte[] frame;
            int fw, fh, rot = 0;
            if (VMedia.IsImage(it.Path))
            {
                var pic = Pic(it.Path, (int)Math.Min(3000, it.W * W * 1.5 * Math.Max(1, st.Scale) + 64));
                if (pic == null) return;
                frame = pic.Px;
                fw = pic.W;
                fh = pic.H;
            }
            else
            {
                var probe = VMedia.Probe(it.Path);
                if (probe == null) return;
                rot = probe.Rotation;
                bool side0 = rot % 180 != 0;
                int nativeLong = Math.Max(probe.Width, probe.Height);
                double shownW = it.W * W * Math.Max(1, st.Scale);
                double shownLong = shownW * Math.Max(probe.Width, probe.Height) / Math.Max(1.0, side0 ? probe.Height : probe.Width);
                var r = Reader(it.Path, Quantize(shownLong * (Export ? 1.0 : 1.1), nativeLong));
                if (r == null) return;
                double ft;
                double src = it.SrcIn + (t - it.Start);
                if (it.SrcOut > it.SrcIn && src > it.SrcOut) src = it.SrcOut;
                frame = r.At(src, out ft);
                if (frame == null) return;
                fw = r.Width;
                fh = r.Height;
            }
            bool side = rot % 180 != 0;
            double ow = side ? fh : fw, oh = side ? fw : fh;
            Rect box;
            PipRect(it, st, ow / oh, W, H, out box);
            // 원은 가운데 정사각형만
            double cropW = ow, cropH = oh;
            if (it.Circle)
            {
                cropW = cropH = Math.Min(ow, oh);
            }
            double s = box.Width / cropW;
            var m = Matrix.Identity;
            m.Translate(-fw / 2.0, -fh / 2.0);
            if (rot != 0) m.Rotate(rot);
            m.Scale(s, s);
            if (Math.Abs(st.Rot) > 0.01) m.Rotate(st.Rot);
            m.Translate(box.X + box.Width / 2, box.Y + box.Height / 2);
            var o = Matrix.Identity;
            o.Translate(-fw / 2.0, -fh / 2.0);
            if (rot != 0) o.Rotate(rot);
            o.Invert();
            var a = o.Transform(new Point(-cropW / 2, -cropH / 2));
            var b = o.Transform(new Point(cropW / 2, cropH / 2));
            var clip = new Rect(a, b);
            var key = it.Chroma ? VPx.Keyer.For(it.ChromaRgb, it.ChromaTol) : null;
            VPx.Place(frame, fw, fh, clip, dst, W, H, m, st.Opacity, it.Circle, key);
        }

        public void Dispose()
        {
            foreach (var o in readers.Values)
            {
                try { o.Reader.Dispose(); } catch { }
            }
            readers.Clear();
            pics.Clear();
            revFrames = null;
            lastOf.Clear();
        }
    }
}
