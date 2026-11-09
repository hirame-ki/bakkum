// 그림 읽기·돌리기·줄이기·저장. 윈도우에 든 그림 코덱(WIC)을 쓴다.
// 사진의 EXIF 방향은 실제 픽셀에 반영하고, 새로 저장할 때 EXIF(위치 정보 포함)는 남기지 않는다.
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    public class JpegInfo
    {
        public int Width;
        public int Height;
        public int Components;
        public int Precision;
        public int Orientation = 1;
        public bool Progressive;
        public bool Arithmetic;
    }

    public static class Imaging
    {
        public static BitmapDecoder Decoder(string path)
        {
            if (Formats.Ext(path) == ".svg") return Decoder(SvgImage.Png(path));
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                }
            }
            catch (Exception ex)
            {
                if (ex is PathTooLongException) throw new UserFacingException(Util.LongPathMessage);
                if (ex is FileNotFoundException || ex is DirectoryNotFoundException) throw new UserFacingException("파일을 찾지 못했어요. 옮겨졌거나 지워졌는지 확인해 주세요.");
                if (ex is IOException && !(ex is FileFormatException)) throw new UserFacingException("파일을 열 수 없어요. 다른 프로그램이 쓰고 있는지 확인해 주세요.");
                string e = Formats.Ext(path);
                if (e == ".heic" || e == ".heif") throw new UserFacingException("HEIC 사진을 열려면 Microsoft Store의 'HEIF 이미지 확장'이 있어야 해요.");
                if (e == ".webp") throw new UserFacingException("WEBP 그림을 열려면 Microsoft Store의 'WebP 이미지 확장'이 있어야 해요.");
                throw new UserFacingException("그림을 열지 못했어요. 손상되었거나 윈도우가 모르는 형식이에요.");
            }
        }

        public static BitmapDecoder Decoder(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            {
                return BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }
        }

        /// 첫 장(사진 방향 반영)
        public static BitmapSource Load(string path)
        {
            var dec = Decoder(path);
            return Upright(dec.Frames[0]);
        }

        /// 쪽마다 한 장(사진 방향 반영). 여러 쪽짜리는 TIFF(스캔·팩스 문서)만 쪽으로 보고,
        /// 움직이는 GIF 같은 다른 형식은 첫 장면 한 장으로 본다. PDF로 묶을 때와 같은 기준이다.
        public static System.Collections.Generic.List<BitmapSource> Pages(string path)
        {
            var dec = Decoder(path);
            int n = Formats.ImageFormatOf(path) == "tiff" ? dec.Frames.Count : 1;
            var list = new System.Collections.Generic.List<BitmapSource>();
            for (int i = 0; i < n; i++) list.Add(Upright(dec.Frames[i]));
            return list;
        }

        /// 쪽 수(Pages와 같은 기준). 픽셀은 풀지 않는다.
        public static int PageCount(string path)
        {
            if (Formats.ImageFormatOf(path) != "tiff") return 1;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    return Math.Max(1, dec.Frames.Count);
                }
            }
            catch
            {
                return 1;
            }
        }

        public static BitmapSource Upright(BitmapFrame f)
        {
            BitmapSource s = Oriented(f, Orientation(f));
            if (s.CanFreeze && !s.IsFrozen) s.Freeze();
            return s;
        }

        public static int Orientation(BitmapFrame f)
        {
            try
            {
                var m = f.Metadata as BitmapMetadata;
                if (m == null) return 1;
                object v = null;
                foreach (var q in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                {
                    try { v = m.GetQuery(q); } catch { v = null; }
                    if (v != null) break;
                }
                if (v == null) return 1;
                int o = System.Convert.ToInt32(v);
                return o >= 1 && o <= 8 ? o : 1;
            }
            catch
            {
                return 1;
            }
        }

        public static BitmapSource Oriented(BitmapSource src, int o)
        {
            if (o <= 1 || o > 8) return src;
            var g = new TransformGroup();
            switch (o)
            {
                case 2: g.Children.Add(new ScaleTransform(-1, 1)); break;
                case 3: g.Children.Add(new RotateTransform(180)); break;
                case 4: g.Children.Add(new ScaleTransform(1, -1)); break;
                case 5: g.Children.Add(new RotateTransform(90)); g.Children.Add(new ScaleTransform(-1, 1)); break;
                case 6: g.Children.Add(new RotateTransform(90)); break;
                case 7: g.Children.Add(new RotateTransform(270)); g.Children.Add(new ScaleTransform(-1, 1)); break;
                case 8: g.Children.Add(new RotateTransform(270)); break;
            }
            var tb = new TransformedBitmap(src, g);
            tb.Freeze();
            return tb;
        }

        /// 긴 변이 longSide를 넘으면 줄인다(키우지는 않음).
        public static BitmapSource Fit(BitmapSource src, int longSide)
        {
            int m = Math.Max(src.PixelWidth, src.PixelHeight);
            if (m <= longSide) return src;
            double s = (double)longSide / m;
            var tb = new TransformedBitmap(src, new ScaleTransform(s, s));
            tb.Freeze();
            return tb;
        }

        public static bool IsGray(BitmapSource s)
        {
            var f = s.Format;
            return f == PixelFormats.Gray8 || f == PixelFormats.Gray16 || f == PixelFormats.Gray4 || f == PixelFormats.Gray2
                || f == PixelFormats.BlackWhite || f == PixelFormats.Gray32Float;
        }

        public static bool MayHaveAlpha(BitmapSource s)
        {
            var f = s.Format;
            if (f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64
                || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float) return true;
            if (s.Palette != null) return s.Palette.Colors.Any(c => c.A < 255);
            return false;
        }

        /// 투명한 곳은 흰 종이로 채운 RGB(Bgr24)
        public static BitmapSource Flatten(BitmapSource src)
        {
            if (src.Format == PixelFormats.Bgr24) return src;
            if (!MayHaveAlpha(src))
            {
                var c = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
                c.Freeze();
                return c;
            }
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            int s4 = w * 4;
            var px = new byte[(long)s4 * h];
            bgra.CopyPixels(px, s4, 0);
            int s3 = (w * 3 + 3) & ~3;
            var o = new byte[(long)s3 * h];
            for (int y = 0; y < h; y++)
            {
                int a0 = y * s4, b0 = y * s3;
                for (int x = 0; x < w; x++)
                {
                    int i = a0 + x * 4, j = b0 + x * 3;
                    int a = px[i + 3];
                    if (a == 255)
                    {
                        o[j] = px[i];
                        o[j + 1] = px[i + 1];
                        o[j + 2] = px[i + 2];
                    }
                    else
                    {
                        int ia = 255 - a;
                        o[j] = (byte)((px[i] * a + 255 * ia + 127) / 255);
                        o[j + 1] = (byte)((px[i + 1] * a + 255 * ia + 127) / 255);
                        o[j + 2] = (byte)((px[i + 2] * a + 255 * ia + 127) / 255);
                    }
                }
            }
            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, o, s3);
            bs.Freeze();
            return bs;
        }

        public static BitmapSource ToGray8(BitmapSource src)
        {
            if (src.Format == PixelFormats.Gray8) return src;
            var c = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
            c.Freeze();
            return c;
        }

        public static byte[] Jpeg(BitmapSource src, int quality)
        {
            BitmapSource s = IsGray(src) ? ToGray8(src) : Flatten(src);
            var enc = new JpegBitmapEncoder { QualityLevel = Math.Max(1, Math.Min(100, quality)) };
            enc.Frames.Add(BitmapFrame.Create(s));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }

        public static byte[] Encode(BitmapSource src, string format, int jpegQuality)
        {
            if (format == "jpg") return Jpeg(src, jpegQuality);
            BitmapEncoder enc;
            BitmapSource s = src;
            switch (format)
            {
                case "png": enc = new PngBitmapEncoder(); break;
                case "bmp": enc = new BmpBitmapEncoder(); s = IsGray(src) ? ToGray8(src) : Flatten(src); break;
                case "gif": enc = new GifBitmapEncoder(); break;
                case "tiff": enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw }; break;
                default: throw new UserFacingException("모르는 그림 형식이에요: " + format);
            }
            enc.Frames.Add(BitmapFrame.Create(s));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }

        /// 8비트 RGB(또는 회색) 바이트 배열(행 사이 빈칸 없음). PDF 그림에 쓴다.
        public static byte[] Samples(BitmapSource src, bool gray, out int w, out int h)
        {
            w = src.PixelWidth;
            h = src.PixelHeight;
            if (gray)
            {
                var g = ToGray8(src);
                var px = new byte[(long)w * h];
                g.CopyPixels(px, w, 0);
                return px;
            }
            var bgr = Flatten(src);
            int st = (w * 3 + 3) & ~3;
            var raw = new byte[(long)st * h];
            bgr.CopyPixels(raw, st, 0);
            var rgb = new byte[(long)w * 3 * h];
            for (int y = 0; y < h; y++)
            {
                int a = y * st, b = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    rgb[b + x * 3] = raw[a + x * 3 + 2];
                    rgb[b + x * 3 + 1] = raw[a + x * 3 + 1];
                    rgb[b + x * 3 + 2] = raw[a + x * 3];
                }
            }
            return rgb;
        }

        public static BitmapSource FromSamples(byte[] samples, int w, int h, int components)
        {
            var fmt = components == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24;
            var bs = BitmapSource.Create(w, h, 96, 96, fmt, null, samples, w * components);
            bs.Freeze();
            return bs;
        }

        /// JPEG 머리 정보(크기·색 성분·EXIF 방향)를 픽셀을 풀지 않고 읽는다.
        public static JpegInfo ReadJpeg(byte[] d)
        {
            if (d == null || d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
            var info = new JpegInfo();
            int i = 2;
            while (i + 4 <= d.Length)
            {
                if (d[i] != 0xFF) return info.Width > 0 ? info : null;
                byte m = d[i + 1];
                if (m == 0xFF) { i++; continue; }
                if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
                if (m == 0xD9 || m == 0xDA) break;
                int len = (d[i + 2] << 8) | d[i + 3];
                if (len < 2 || i + 2 + len > d.Length) return null;
                int seg = i + 4;
                if (m == 0xE1 && len >= 16 && d[seg] == (byte)'E' && d[seg + 1] == (byte)'x' && d[seg + 2] == (byte)'i' && d[seg + 3] == (byte)'f')
                {
                    int o = ExifOrientation(d, seg + 6, len - 8);
                    if (o > 0) info.Orientation = o;
                }
                bool sof = m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC;
                if (sof && len >= 8)
                {
                    info.Precision = d[seg];
                    info.Height = (d[seg + 1] << 8) | d[seg + 2];
                    info.Width = (d[seg + 3] << 8) | d[seg + 4];
                    info.Components = d[seg + 5];
                    info.Progressive = m == 0xC2 || m == 0xC6 || m == 0xCA || m == 0xCE;
                    info.Arithmetic = m >= 0xC9;
                }
                i += 2 + len;
            }
            return info.Width > 0 ? info : null;
        }

        static int ExifOrientation(byte[] d, int t, int len)
        {
            try
            {
                if (len < 8 || t + 8 > d.Length) return 0;
                bool le = d[t] == (byte)'I' && d[t + 1] == (byte)'I';
                if (!le && !(d[t] == (byte)'M' && d[t + 1] == (byte)'M')) return 0;
                Func<int, int> u16 = p => le ? d[p] | (d[p + 1] << 8) : (d[p] << 8) | d[p + 1];
                Func<int, int> u32 = p => le ? d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24) : (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
                int ifd = t + u32(t + 4);
                if (ifd + 2 > t + len || ifd + 2 > d.Length) return 0;
                int n = u16(ifd);
                for (int k = 0; k < n; k++)
                {
                    int e = ifd + 2 + k * 12;
                    if (e + 12 > d.Length) break;
                    if (u16(e) == 0x0112) return u16(e + 8);
                }
            }
            catch { }
            return 0;
        }
    }
}
