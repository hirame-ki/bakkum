// 영상의 한 구간을 움직이는 GIF로. 프레임은 임시 파일에 두어 메모리를 적게 쓰고,
// 색은 전체 프레임에서 모은 256색 한 벌을 함께 쓴다. 앞 장과 같은 칸은 투명으로 두어 파일을 줄인다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Bakkum
{
    static class GifMaker
    {
        const int Colors = 255, Clear = 255;
        static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        public static void Write(Job job, string src, string dst, MediaProbe p, double from, double to, int width, int fps)
        {
            bool sideways = p.Rotation == 90 || p.Rotation == 270;
            int shownW = sideways ? p.Height : p.Width, shownH = sideways ? p.Width : p.Height;
            int gw = Math.Max(1, Math.Min(width, shownW));
            int gh = Math.Max(1, (int)Math.Round((double)shownH * gw / shownW));
            int sw = sideways ? gh : gw, sh = sideways ? gw : gh;
            string name = Util.ShortName(src);
            string temp = Util.TempDir("gif");
            try
            {
                string raw = Path.Combine(temp, "frames.raw");
                var times = new List<double>();
                var hist = new int[32768];
                ReadFrames(job, src, raw, hist, times, from, to, fps, sw, sh, gw, gh, p.Rotation, name);
                if (times.Count == 0) throw new UserFacingException("이 구간에서 프레임을 찾지 못했어요.");
                job.Report(name + " 색 고르는 중", 0.7);
                var palette = MedianCut(hist, Colors);
                var lut = Nearest(palette);
                using (var input = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    Header(o, gw, gh, palette);
                    var frame = new byte[gw * gh * 3];
                    var now = new byte[gw * gh];
                    byte[] before = null;
                    for (int i = 0; i < times.Count; i++)
                    {
                        job.Check();
                        ReadAll(input, frame);
                        Index(frame, now, gw, gh, lut);
                        double next = i + 1 < times.Count ? times[i + 1] : times[i] + 1.0 / fps;
                        int delay = Math.Max(2, (int)Math.Round((next - from) * 100) - (int)Math.Round((times[i] - from) * 100));
                        Frame(o, now, before, gw, gh, delay);
                        if (before == null) before = new byte[now.Length];
                        Buffer.BlockCopy(now, 0, before, 0, now.Length);
                        job.Report(name + " GIF 만드는 중", 0.7 + 0.3 * (i + 1) / times.Count);
                    }
                    o.WriteByte(0x3B);
                }
            }
            finally
            {
                Util.TryDeleteDir(temp);
            }
        }

        static void ReadFrames(Job job, string src, string raw, int[] hist, List<double> times, double from, double to, int fps, int sw, int sh, int gw, int gh, int rotation, string name)
        {
            WinRtCall.Mta<object>(delegate ()
            {
                using (var frames = new VideoFrames(src))
                using (var o = new FileStream(raw, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    if (from > 0) frames.Seek(from);
                    double step = 1.0 / fps, next = from;
                    byte[] pixels = null;
                    var small = new byte[sw * sh * 3];
                    var turned = new byte[gw * gh * 3];
                    double at;
                    while (frames.Next(out at, ref pixels))
                    {
                        job.Check();
                        if (at < from - 0.0005) continue;
                        if (at >= to) break;
                        if (at < next - step * 0.5) continue;
                        Scale(pixels, frames.Width, frames.Height, small, sw, sh);
                        byte[] output = rotation == 0 ? small : Rotate(small, sw, sh, turned, gw, gh, rotation);
                        Count(output, gw, gh, hist);
                        o.Write(output, 0, output.Length);
                        times.Add(at);
                        next += step;
                        while (next <= at + 1e-6) next += step;
                        job.Report(name + " 프레임 읽는 중", 0.7 * (at - from) / Math.Max(0.001, to - from));
                    }
                }
                return null;
            });
        }

        static void ReadAll(Stream s, byte[] data)
        {
            int got = 0;
            while (got < data.Length)
            {
                int n = s.Read(data, got, data.Length - got);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
        }

        /// BGRA를 BGR로 줄인다. 넓은 칸은 칸마다 최대 4×4점을 평균한다.
        static void Scale(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
        {
            int at = 0;
            for (int y = 0; y < dh; y++)
            {
                int y0 = (int)((long)y * sh / dh), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sh / dh));
                int yStep = Math.Max(1, (y1 - y0) / 4);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = (int)((long)x * sw / dw), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sw / dw));
                    int xStep = Math.Max(1, (x1 - x0) / 4);
                    int b = 0, g = 0, r = 0, n = 0;
                    for (int yy = y0; yy < y1; yy += yStep)
                    {
                        int line = yy * sw * 4;
                        for (int xx = x0; xx < x1; xx += xStep)
                        {
                            int i = line + xx * 4;
                            b += src[i];
                            g += src[i + 1];
                            r += src[i + 2];
                            n++;
                        }
                    }
                    dst[at++] = (byte)(b / n);
                    dst[at++] = (byte)(g / n);
                    dst[at++] = (byte)(r / n);
                }
            }
        }

        static byte[] Rotate(byte[] src, int sw, int sh, byte[] dst, int dw, int dh, int degrees)
        {
            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    int nx, ny;
                    if (degrees == 90)
                    {
                        nx = sh - 1 - y;
                        ny = x;
                    }
                    else if (degrees == 270)
                    {
                        nx = y;
                        ny = sw - 1 - x;
                    }
                    else
                    {
                        nx = sw - 1 - x;
                        ny = sh - 1 - y;
                    }
                    int a = (y * sw + x) * 3, c = (ny * dw + nx) * 3;
                    dst[c] = src[a];
                    dst[c + 1] = src[a + 1];
                    dst[c + 2] = src[a + 2];
                }
            }
            return dst;
        }

        static void Count(byte[] bgr, int w, int h, int[] hist)
        {
            for (int y = 0; y < h; y += 2)
            {
                int at = y * w * 3;
                for (int x = 0; x < w; x += 2, at += 6) hist[((bgr[at + 2] >> 3) << 10) | ((bgr[at + 1] >> 3) << 5) | (bgr[at] >> 3)]++;
            }
        }

        sealed class ColorBox
        {
            public int Start, End;
            public long Count;
            public int Channel, Range;
        }

        static int Part(int bin, int channel)
        {
            return channel == 0 ? bin >> 10 : channel == 1 ? (bin >> 5) & 31 : bin & 31;
        }

        static void Measure(ColorBox box, int[] bins, int[] hist)
        {
            box.Count = 0;
            int[] low = { 31, 31, 31 }, high = { 0, 0, 0 };
            for (int i = box.Start; i < box.End; i++)
            {
                box.Count += hist[bins[i]];
                for (int c = 0; c < 3; c++)
                {
                    int v = Part(bins[i], c);
                    if (v < low[c]) low[c] = v;
                    if (v > high[c]) high[c] = v;
                }
            }
            box.Channel = 0;
            box.Range = -1;
            int[] weight = { 3, 4, 2 };
            for (int c = 0; c < 3; c++)
            {
                int r = (high[c] - low[c]) * weight[c];
                if (r > box.Range)
                {
                    box.Range = r;
                    box.Channel = c;
                }
            }
        }

        /// 많이 쓰인 색 영역부터 나눠 대표색을 고른다. 돌려주는 값은 R,G,B가 이어진 배열.
        static byte[] MedianCut(int[] hist, int want)
        {
            var bins = Enumerable.Range(0, hist.Length).Where(i => hist[i] > 0).ToArray();
            var boxes = new List<ColorBox>();
            var first = new ColorBox { Start = 0, End = bins.Length };
            Measure(first, bins, hist);
            boxes.Add(first);
            while (boxes.Count < want)
            {
                ColorBox pick = null;
                double best = 0;
                foreach (var b in boxes)
                {
                    if (b.End - b.Start < 2 || b.Range <= 0) continue;
                    double score = Math.Sqrt(b.Count) * b.Range;
                    if (score > best)
                    {
                        best = score;
                        pick = b;
                    }
                }
                if (pick == null) break;
                int channel = pick.Channel;
                Array.Sort(bins, pick.Start, pick.End - pick.Start, Comparer<int>.Create((a, b) => Part(a, channel).CompareTo(Part(b, channel))));
                long half = pick.Count / 2, sum = 0;
                int cut = pick.Start + 1;
                for (int i = pick.Start; i < pick.End - 1; i++)
                {
                    sum += hist[bins[i]];
                    cut = i + 1;
                    if (sum >= half && Part(bins[i], channel) != Part(bins[i + 1], channel)) break;
                }
                var right = new ColorBox { Start = cut, End = pick.End };
                pick.End = cut;
                Measure(pick, bins, hist);
                Measure(right, bins, hist);
                boxes.Add(right);
            }
            var palette = new byte[boxes.Count * 3];
            for (int i = 0; i < boxes.Count; i++)
            {
                long r = 0, g = 0, b = 0, n = 0;
                for (int k = boxes[i].Start; k < boxes[i].End; k++)
                {
                    int bin = bins[k], w = hist[bin];
                    r += w * ((Part(bin, 0) << 3) | 4);
                    g += w * ((Part(bin, 1) << 3) | 4);
                    b += w * ((Part(bin, 2) << 3) | 4);
                    n += w;
                }
                if (n == 0) n = 1;
                palette[i * 3] = (byte)(r / n);
                palette[i * 3 + 1] = (byte)(g / n);
                palette[i * 3 + 2] = (byte)(b / n);
            }
            return palette;
        }

        static byte[] Nearest(byte[] palette)
        {
            int n = palette.Length / 3;
            var lut = new byte[32768];
            for (int bin = 0; bin < lut.Length; bin++)
            {
                int r = (Part(bin, 0) << 3) | 4, g = (Part(bin, 1) << 3) | 4, b = (Part(bin, 2) << 3) | 4;
                int best = int.MaxValue, at = 0;
                for (int i = 0; i < n; i++)
                {
                    int dr = palette[i * 3] - r, dg = palette[i * 3 + 1] - g, db = palette[i * 3 + 2] - b;
                    int d = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
                    if (d < best)
                    {
                        best = d;
                        at = i;
                    }
                }
                lut[bin] = (byte)at;
            }
            return lut;
        }

        static int Clamp(int v)
        {
            return v < 0 ? 0 : v > 255 ? 255 : v;
        }

        /// 고정된 무늬로 점을 섞는다. 프레임마다 무늬가 같아야 앞 장과 같은 칸이 많이 남는다.
        static void Index(byte[] bgr, byte[] index, int w, int h, byte[] lut)
        {
            int at = 0, to = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++, at += 3)
                {
                    int shift = (Bayer[(y & 3) * 4 + (x & 3)] * 2 - 15) / 2;
                    index[to++] = lut[(Clamp(bgr[at + 2] + shift) >> 3) << 10 | (Clamp(bgr[at + 1] + shift) >> 3) << 5 | (Clamp(bgr[at] + shift) >> 3)];
                }
            }
        }

        static void Le16(Stream o, int v)
        {
            o.WriteByte((byte)v);
            o.WriteByte((byte)(v >> 8));
        }

        static void Header(Stream o, int w, int h, byte[] palette)
        {
            var sig = Encoding.ASCII.GetBytes("GIF89a");
            o.Write(sig, 0, sig.Length);
            Le16(o, w);
            Le16(o, h);
            o.WriteByte(0xF7);
            o.WriteByte(0);
            o.WriteByte(0);
            var table = new byte[768];
            Buffer.BlockCopy(palette, 0, table, 0, Math.Min(palette.Length, 768));
            o.Write(table, 0, table.Length);
            var loop = new byte[] { 0x21, 0xFF, 11, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0', 3, 1, 0, 0, 0 };
            o.Write(loop, 0, loop.Length);
        }

        static void Frame(Stream o, byte[] now, byte[] before, int w, int h, int delay)
        {
            int left = 0, top = 0, width = w, height = h;
            byte[] data = now;
            if (before != null)
            {
                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0, i = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++, i++)
                    {
                        if (now[i] == before[i]) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
                if (maxX < 0)
                {
                    left = top = 0;
                    width = height = 1;
                    data = new byte[] { Clear };
                }
                else
                {
                    left = minX;
                    top = minY;
                    width = maxX - minX + 1;
                    height = maxY - minY + 1;
                    data = new byte[width * height];
                    for (int y = 0, c = 0; y < height; y++)
                    {
                        int line = (y + top) * w + left;
                        for (int x = 0; x < width; x++, c++) data[c] = now[line + x] == before[line + x] ? (byte)Clear : now[line + x];
                    }
                }
            }
            o.WriteByte(0x21);
            o.WriteByte(0xF9);
            o.WriteByte(4);
            o.WriteByte((byte)(before == null ? 0x04 : 0x05));
            Le16(o, delay);
            o.WriteByte(Clear);
            o.WriteByte(0);
            o.WriteByte(0x2C);
            Le16(o, left);
            Le16(o, top);
            Le16(o, width);
            Le16(o, height);
            o.WriteByte(0);
            new Lzw(o).Encode(data);
        }

        /// GIF의 LZW 압축. 낱말장이 가득 차면 지우고 다시 시작한다.
        sealed class Lzw
        {
            const int Bits = 12, Hash = 5003, Limit = 4096, ClearCode = 256, EndCode = 257;
            readonly Stream o;
            readonly byte[] block = new byte[256];
            readonly int[] keys = new int[Hash];
            readonly short[] codes = new short[Hash];
            int fill, buffer, used, size, top, free;
            bool cleared;

            public Lzw(Stream output)
            {
                o = output;
            }

            void Reset()
            {
                for (int i = 0; i < Hash; i++) keys[i] = -1;
            }

            static int Top(int bits)
            {
                return (1 << bits) - 1;
            }

            void Emit(int code)
            {
                buffer |= code << used;
                used += size;
                while (used >= 8)
                {
                    Byte(buffer & 0xFF);
                    buffer >>= 8;
                    used -= 8;
                }
                if (free > top || cleared)
                {
                    if (cleared)
                    {
                        size = 9;
                        top = Top(size);
                        cleared = false;
                    }
                    else
                    {
                        size++;
                        top = size == Bits ? Limit : Top(size);
                    }
                }
            }

            void Byte(int b)
            {
                block[++fill] = (byte)b;
                if (fill == 255) Flush();
            }

            void Flush()
            {
                if (fill == 0) return;
                block[0] = (byte)fill;
                o.Write(block, 0, fill + 1);
                fill = 0;
            }

            public void Encode(byte[] data)
            {
                o.WriteByte(8);
                size = 9;
                top = Top(size);
                free = EndCode + 1;
                Reset();
                Emit(ClearCode);
                int ent = data[0];
                for (int k = 1; k < data.Length; k++)
                {
                    int c = data[k];
                    int key = (c << Bits) + ent;
                    int i = (c << 4) ^ ent;
                    int step = i == 0 ? 1 : Hash - i;
                    bool found = false;
                    while (keys[i] >= 0)
                    {
                        if (keys[i] == key)
                        {
                            ent = codes[i];
                            found = true;
                            break;
                        }
                        i -= step;
                        if (i < 0) i += Hash;
                    }
                    if (found) continue;
                    Emit(ent);
                    ent = c;
                    if (free < Limit)
                    {
                        codes[i] = (short)free++;
                        keys[i] = key;
                    }
                    else
                    {
                        Reset();
                        free = EndCode + 1;
                        cleared = true;
                        Emit(ClearCode);
                    }
                }
                Emit(ent);
                Emit(EndCode);
                while (used > 0)
                {
                    Byte(buffer & 0xFF);
                    buffer >>= 8;
                    used = Math.Max(0, used - 8);
                }
                Flush();
                o.WriteByte(0);
            }
        }
    }
}
