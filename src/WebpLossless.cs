// 손실 없는 WEBP(VP8L) 저장. 윈도우에는 WEBP를 쓰는 부품이 없어서 공개 규격(WebP Lossless Bitstream)대로 직접 쓴다.
// 쓰는 변환: 초록 빼기 + 예측(16칸 묶음마다 가장 좋은 방식) + LZ77 + 허프만. 저장한 뒤 윈도우 코덱으로 되읽어 시험했다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    public static class WebpLossless
    {
        const int MaxSide = 16384;
        const int BlockBits = 4;
        const int MaxCopy = 4096;
        const int MaxDistance = (1 << 20) - 120;
        const int HashBits = 18;
        const int MaxChain = 24;
        static readonly int[] Modes = { 1, 2, 4, 7, 11, 12, 13 };
        static readonly int[] CodeLengthOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        sealed class BitWriter
        {
            byte[] buf = new byte[1 << 16];
            int pos;
            ulong acc;
            int count;

            public void Put(uint value, int bits)
            {
                acc |= (ulong)value << count;
                count += bits;
                while (count >= 8)
                {
                    if (pos == buf.Length) Array.Resize(ref buf, buf.Length * 2);
                    buf[pos++] = (byte)acc;
                    acc >>= 8;
                    count -= 8;
                }
            }

            public byte[] Finish()
            {
                if (count > 0) Put(0, 8 - count);
                var r = new byte[pos];
                Buffer.BlockCopy(buf, 0, r, 0, pos);
                return r;
            }
        }

        sealed class Code
        {
            public int[] Lengths;
            public uint[] Bits;
            public bool Single;

            public void Write(BitWriter w, int symbol)
            {
                if (!Single) w.Put(Bits[symbol], Lengths[symbol]);
            }
        }

        // ── 허프만 ──

        static int[] BuildLengths(int[] counts, int maxLength)
        {
            int n = counts.Length;
            var lengths = new int[n];
            var used = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (counts[i] > 0) used.Add(i);
            }
            if (used.Count == 0) return lengths;
            if (used.Count == 1)
            {
                lengths[used[0]] = 1;
                return lengths;
            }
            for (long floor = 1; ; floor *= 2)
            {
                long min = floor;
                var leaves = used.OrderBy(i => Math.Max(counts[i], min)).ThenBy(i => i).ToArray();
                int m = leaves.Length;
                var weight = new long[2 * m - 1];
                var parent = new int[2 * m - 1];
                for (int i = 0; i < m; i++) weight[i] = Math.Max(counts[leaves[i]], min);
                int a = 0, b = m, next = m;
                while (next < 2 * m - 1)
                {
                    int x = a < m && (b >= next || weight[a] <= weight[b]) ? a++ : b++;
                    int y = a < m && (b >= next || weight[a] <= weight[b]) ? a++ : b++;
                    weight[next] = weight[x] + weight[y];
                    parent[x] = next;
                    parent[y] = next;
                    next++;
                }
                var depth = new int[2 * m - 1];
                for (int i = 2 * m - 3; i >= 0; i--) depth[i] = depth[parent[i]] + 1;
                int deepest = 0;
                for (int i = 0; i < m; i++)
                {
                    lengths[leaves[i]] = depth[i];
                    if (depth[i] > deepest) deepest = depth[i];
                }
                if (deepest <= maxLength) return lengths;
            }
        }

        static Code MakeCode(int[] counts, int maxLength)
        {
            var lengths = BuildLengths(counts, maxLength);
            var code = new Code { Lengths = lengths, Bits = new uint[lengths.Length] };
            code.Single = lengths.Count(l => l > 0) <= 1;
            var perLength = new int[maxLength + 2];
            foreach (int l in lengths) perLength[l]++;
            perLength[0] = 0;
            var next = new uint[maxLength + 2];
            uint c = 0;
            for (int l = 1; l <= maxLength; l++)
            {
                c = (c + (uint)perLength[l - 1]) << 1;
                next[l] = c;
            }
            for (int s = 0; s < lengths.Length; s++)
            {
                int l = lengths[s];
                if (l == 0) continue;
                code.Bits[s] = Reverse(next[l]++, l);
            }
            return code;
        }

        static uint Reverse(uint v, int bits)
        {
            uint r = 0;
            for (int i = 0; i < bits; i++)
            {
                r = (r << 1) | (v & 1);
                v >>= 1;
            }
            return r;
        }

        static void WriteCode(BitWriter w, Code code)
        {
            var used = new List<int>();
            for (int i = 0; i < code.Lengths.Length; i++)
            {
                if (code.Lengths[i] > 0) used.Add(i);
            }
            if (used.Count == 0)
            {
                w.Put(1, 4);
                return;
            }
            if (used.Count <= 2 && used.All(s => s < 256))
            {
                w.Put(1, 1);
                w.Put((uint)(used.Count - 1), 1);
                if (used[0] <= 1)
                {
                    w.Put(0, 1);
                    w.Put((uint)used[0], 1);
                }
                else
                {
                    w.Put(1, 1);
                    w.Put((uint)used[0], 8);
                }
                if (used.Count == 2) w.Put((uint)used[1], 8);
                return;
            }
            w.Put(0, 1);
            var tokens = new List<int>();
            var extra = new List<int>();
            Action<int, int> add = (t, e) =>
            {
                tokens.Add(t);
                extra.Add(e);
            };
            int prev = 8;
            int n = code.Lengths.Length;
            for (int i = 0; i < n;)
            {
                int v = code.Lengths[i];
                int run = 1;
                while (i + run < n && code.Lengths[i + run] == v) run++;
                i += run;
                if (v == 0)
                {
                    while (run >= 11)
                    {
                        int r = Math.Min(run, 138);
                        add(18, r - 11);
                        run -= r;
                    }
                    if (run >= 3)
                    {
                        add(17, run - 3);
                        run = 0;
                    }
                    for (; run > 0; run--) add(0, 0);
                    continue;
                }
                if (v != prev)
                {
                    add(v, 0);
                    prev = v;
                    run--;
                }
                while (run >= 3)
                {
                    int r = Math.Min(run, 6);
                    add(16, r - 3);
                    run -= r;
                }
                for (; run > 0; run--) add(v, 0);
            }
            var histogram = new int[19];
            foreach (int t in tokens) histogram[t]++;
            var lengthCode = MakeCode(histogram, 7);
            int last = 18;
            while (last > 3 && lengthCode.Lengths[CodeLengthOrder[last]] == 0) last--;
            w.Put((uint)(last + 1 - 4), 4);
            for (int i = 0; i <= last; i++) w.Put((uint)lengthCode.Lengths[CodeLengthOrder[i]], 3);
            w.Put(0, 1);
            for (int i = 0; i < tokens.Count; i++)
            {
                int t = tokens[i];
                lengthCode.Write(w, t);
                if (t == 16) w.Put((uint)extra[i], 2);
                else if (t == 17) w.Put((uint)extra[i], 3);
                else if (t == 18) w.Put((uint)extra[i], 7);
            }
        }

        // ── 값 하나를 (기호, 덧붙일 비트)로 ──

        static void Prefix(int value, out int code, out int bits, out int extra)
        {
            if (value <= 4)
            {
                code = value - 1;
                bits = 0;
                extra = 0;
                return;
            }
            int v = value - 1;
            int high = 31;
            while ((v >> high) == 0) high--;
            int second = (v >> (high - 1)) & 1;
            bits = high - 1;
            extra = v & ((1 << bits) - 1);
            code = 2 * high + second;
        }

        // ── 예측 ──

        static uint Avg(uint a, uint b)
        {
            return (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);
        }

        static int Clip(int v)
        {
            return v < 0 ? 0 : v > 255 ? 255 : v;
        }

        static uint Select(uint l, uint t, uint tl)
        {
            int sum = 0;
            for (int s = 0; s < 32; s += 8)
            {
                int lv = (int)((l >> s) & 255), tv = (int)((t >> s) & 255), c = (int)((tl >> s) & 255);
                sum += Math.Abs(lv - c) - Math.Abs(tv - c);
            }
            return sum <= 0 ? t : l;
        }

        static uint ClampFull(uint l, uint t, uint tl)
        {
            uint r = 0;
            for (int s = 0; s < 32; s += 8)
            {
                r |= (uint)Clip((int)((l >> s) & 255) + (int)((t >> s) & 255) - (int)((tl >> s) & 255)) << s;
            }
            return r;
        }

        static uint ClampHalf(uint avg, uint tl)
        {
            uint r = 0;
            for (int s = 0; s < 32; s += 8)
            {
                int a = (int)((avg >> s) & 255), b = (int)((tl >> s) & 255);
                r |= (uint)Clip(a + (a - b) / 2) << s;
            }
            return r;
        }

        static uint Predict(int mode, uint l, uint t, uint tl)
        {
            switch (mode)
            {
                case 1: return l;
                case 2: return t;
                case 4: return tl;
                case 7: return Avg(l, t);
                case 11: return Select(l, t, tl);
                case 12: return ClampFull(l, t, tl);
                case 13: return ClampHalf(Avg(l, t), tl);
            }
            return 0xFF000000u;
        }

        static uint Sub(uint a, uint b)
        {
            uint ag = 0x00FF00FFu + (a & 0xFF00FF00u) - (b & 0xFF00FF00u);
            uint rb = 0xFF00FF00u + (a & 0x00FF00FFu) - (b & 0x00FF00FFu);
            return (ag & 0xFF00FF00u) | (rb & 0x00FF00FFu);
        }

        static readonly byte[] Wrapped = MakeWrapped();

        static byte[] MakeWrapped()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++) t[i] = (byte)(i < 128 ? i : 256 - i);
            return t;
        }

        static int Cost(uint r)
        {
            return Wrapped[r & 255] + Wrapped[(r >> 8) & 255] + Wrapped[(r >> 16) & 255] + Wrapped[r >> 24];
        }

        // ── 쓰기 ──

        public static byte[] Encode(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w > MaxSide || h > MaxSide) throw new UserFacingException("WEBP는 가로·세로 16384칸까지만 저장할 수 있어요.");
            var b = Bgra.From(src);
            var px = new uint[(long)w * h];
            Buffer.BlockCopy(b.P, 0, px, 0, b.P.Length);
            bool alpha = false;
            for (int i = 0; i < px.Length; i++)
            {
                if ((px[i] >> 24) != 255)
                {
                    alpha = true;
                    break;
                }
            }
            for (int i = 0; i < px.Length; i++)
            {
                // 투명한 칸의 색은 보이지 않으니 같은 값으로 맞춰 잘 눌리게 한다.
                if ((px[i] >> 24) == 0)
                {
                    px[i] = 0;
                    continue;
                }
                uint p = px[i];
                uint g = (p >> 8) & 255;
                uint r = (((p >> 16) & 255) - g) & 255;
                uint bl = ((p & 255) - g) & 255;
                px[i] = (p & 0xFF00FF00u) | (r << 16) | bl;
            }

            int blockSize = 1 << BlockBits;
            int bw = (w + blockSize - 1) >> BlockBits, bh = (h + blockSize - 1) >> BlockBits;
            var modes = new byte[bw * bh];
            Parallel.For(0, bh, by =>
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int bestMode = 1;
                    long bestCost = long.MaxValue;
                    int x0 = bx << BlockBits, y0 = by << BlockBits;
                    int x1 = Math.Min(w, x0 + blockSize), y1 = Math.Min(h, y0 + blockSize);
                    foreach (int mode in Modes)
                    {
                        long cost = 0;
                        for (int y = Math.Max(y0, 1); y < y1; y++)
                        {
                            for (int x = Math.Max(x0, 1); x < x1; x++)
                            {
                                int i = y * w + x;
                                cost += Cost(Sub(px[i], Predict(mode, px[i - 1], px[i - w], px[i - w - 1])));
                            }
                        }
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            bestMode = mode;
                        }
                    }
                    modes[by * bw + bx] = (byte)bestMode;
                }
            });
            var residual = new uint[px.Length];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    uint pred;
                    if (y == 0) pred = x == 0 ? 0xFF000000u : px[i - 1];
                    else if (x == 0) pred = px[i - w];
                    else pred = Predict(modes[(y >> BlockBits) * bw + (x >> BlockBits)], px[i - 1], px[i - w], px[i - w - 1]);
                    residual[i] = Sub(px[i], pred);
                }
            });

            var o = new BitWriter();
            o.Put(0x2F, 8);
            o.Put((uint)(w - 1), 14);
            o.Put((uint)(h - 1), 14);
            o.Put(alpha ? 1u : 0u, 1);
            o.Put(0, 3);
            // 초록 빼기
            o.Put(1, 1);
            o.Put(2, 2);
            // 예측
            o.Put(1, 1);
            o.Put(0, 2);
            o.Put((uint)(BlockBits - 2), 3);
            WriteModes(o, modes);
            // 변환 끝, 본 그림
            o.Put(0, 1);
            o.Put(0, 1);
            o.Put(0, 1);
            WritePixels(o, residual);
            byte[] data = o.Finish();

            using (var ms = new MemoryStream(data.Length + 32))
            using (var f = new BinaryWriter(ms))
            {
                int pad = data.Length & 1;
                f.Write(Encoding.ASCII.GetBytes("RIFF"));
                f.Write(4 + 8 + data.Length + pad);
                f.Write(Encoding.ASCII.GetBytes("WEBP"));
                f.Write(Encoding.ASCII.GetBytes("VP8L"));
                f.Write(data.Length);
                f.Write(data);
                if (pad == 1) f.Write((byte)0);
                f.Flush();
                return ms.ToArray();
            }
        }

        static void WriteModes(BitWriter o, byte[] modes)
        {
            o.Put(0, 1);
            var green = new int[280];
            foreach (byte m in modes) green[m]++;
            var red = new int[256];
            red[0] = modes.Length;
            var blue = new int[256];
            blue[0] = modes.Length;
            var alpha = new int[256];
            alpha[255] = modes.Length;
            var dist = new int[40];
            var cg = MakeCode(green, 15);
            var cr = MakeCode(red, 15);
            var cb = MakeCode(blue, 15);
            var ca = MakeCode(alpha, 15);
            var cd = MakeCode(dist, 15);
            WriteCode(o, cg);
            WriteCode(o, cr);
            WriteCode(o, cb);
            WriteCode(o, ca);
            WriteCode(o, cd);
            foreach (byte m in modes)
            {
                cg.Write(o, m);
                cr.Write(o, 0);
                cb.Write(o, 0);
                ca.Write(o, 255);
            }
        }

        static void WritePixels(BitWriter o, uint[] p)
        {
            int n = p.Length;
            var head = new int[1 << HashBits];
            for (int i = 0; i < head.Length; i++) head[i] = -1;
            var prev = new int[n];
            var matchAt = new List<int>();
            var matchLen = new List<int>();
            var matchDist = new List<int>();
            var green = new int[280];
            var red = new int[256];
            var blue = new int[256];
            var alpha = new int[256];
            var dist = new int[40];
            int code, bits, extra;
            for (int i = 0; i < n;)
            {
                int bestLen = 0, bestDist = 0;
                if (i + 1 < n)
                {
                    int cand = head[HashOf(p[i], p[i + 1])];
                    int steps = MaxChain;
                    int limit = Math.Min(MaxCopy, n - i);
                    while (cand >= 0 && i - cand <= MaxDistance && steps-- > 0)
                    {
                        if (bestLen < limit && p[cand + bestLen] == p[i + bestLen])
                        {
                            int l = 0;
                            while (l < limit && p[cand + l] == p[i + l]) l++;
                            if (l > bestLen)
                            {
                                bestLen = l;
                                bestDist = i - cand;
                                if (l == limit) break;
                            }
                        }
                        cand = prev[cand];
                    }
                }
                int need = bestDist <= 64 ? 3 : bestDist <= 8192 ? 4 : 5;
                int advance = 1;
                if (bestLen >= need)
                {
                    matchAt.Add(i);
                    matchLen.Add(bestLen);
                    matchDist.Add(bestDist);
                    Prefix(bestLen, out code, out bits, out extra);
                    green[256 + code]++;
                    Prefix(bestDist + 120, out code, out bits, out extra);
                    dist[code]++;
                    advance = bestLen;
                }
                else
                {
                    uint v = p[i];
                    green[(v >> 8) & 255]++;
                    red[(v >> 16) & 255]++;
                    blue[v & 255]++;
                    alpha[v >> 24]++;
                }
                for (int j = i; j < i + advance; j++)
                {
                    if (j + 1 < n)
                    {
                        int h = HashOf(p[j], p[j + 1]);
                        prev[j] = head[h];
                        head[h] = j;
                    }
                }
                i += advance;
            }
            var cg = MakeCode(green, 15);
            var cr = MakeCode(red, 15);
            var cb = MakeCode(blue, 15);
            var ca = MakeCode(alpha, 15);
            var cd = MakeCode(dist, 15);
            WriteCode(o, cg);
            WriteCode(o, cr);
            WriteCode(o, cb);
            WriteCode(o, ca);
            WriteCode(o, cd);
            int at = 0;
            for (int i = 0; i < n;)
            {
                if (at < matchAt.Count && matchAt[at] == i)
                {
                    int len = matchLen[at], d = matchDist[at];
                    at++;
                    Prefix(len, out code, out bits, out extra);
                    cg.Write(o, 256 + code);
                    if (bits > 0) o.Put((uint)extra, bits);
                    Prefix(d + 120, out code, out bits, out extra);
                    cd.Write(o, code);
                    if (bits > 0) o.Put((uint)extra, bits);
                    i += len;
                    continue;
                }
                uint v = p[i];
                cg.Write(o, (int)((v >> 8) & 255));
                cr.Write(o, (int)((v >> 16) & 255));
                cb.Write(o, (int)(v & 255));
                ca.Write(o, (int)(v >> 24));
                i++;
            }
        }

        static int HashOf(uint a, uint b)
        {
            return (int)((a * 0xC6A4A793u + b * 0x5BD1E996u) >> (32 - HashBits));
        }
    }
}
