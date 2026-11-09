// 사진 손질 계산. 화면 미리보기와 저장이 같은 코드를 쓴다(미리보기는 줄인 사진으로).
// 자리·크기는 모두 사진 크기에 대한 비율(0~1)이라, 미리보기와 원본의 크기가 달라도 같은 곳에 적용된다.
// 저장하면 픽셀을 새로 쓰므로 가린 곳은 원래 그림이 파일에 남지 않고, 사진 정보(EXIF·위치)도 따라가지 않는다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bakkum
{
    public enum MarkKind { Redact, Text, Arrow, Highlight, Box }

    /// 사진 위에 얹는 것 하나(가린 곳·글자·화살표·형광펜·네모)
    public class PhotoMark
    {
        public MarkKind Kind;
        /// 화살표는 (X,Y)에서 (X+W,Y+H)로 간다. 나머지는 왼쪽 위 + 크기.
        public double X, Y, W, H;
        /// blur · mosaic · fill
        public string Mode = "blur";
        public double Strength = 0.6;
        public bool Oval, Whole, Bold;
        public uint Rgb = 0x22252F;
        public string Text = "";
        /// 글자 크기·선 굵기(사진 짧은 변에 대한 비율)
        public double Size = 0.05;
        public List<Point> Points;

        public PhotoMark Copy()
        {
            var m = (PhotoMark)MemberwiseClone();
            if (Points != null) m.Points = new List<Point>(Points);
            return m;
        }

        public Rect Bounds
        {
            get
            {
                if (Whole) return new Rect(0, 0, 1, 1);
                if (Kind == MarkKind.Arrow) return new Rect(new Point(X, Y), new Point(X + W, Y + H));
                return new Rect(X, Y, Math.Max(0, W), Math.Max(0, H));
            }
        }

        public void Map(Func<Point, Point> f)
        {
            if (Kind == MarkKind.Arrow)
            {
                Point a = f(new Point(X, Y)), b = f(new Point(X + W, Y + H));
                X = a.X;
                Y = a.Y;
                W = b.X - a.X;
                H = b.Y - a.Y;
                return;
            }
            var r = new Rect(f(new Point(X, Y)), f(new Point(X + W, Y + H)));
            X = r.X;
            Y = r.Y;
            W = r.Width;
            H = r.Height;
            if (Points != null) Points = Points.Select(f).ToList();
        }

        public void Offset(double dx, double dy)
        {
            X += dx;
            Y += dy;
            if (Points != null) Points = Points.Select(p => new Point(p.X + dx, p.Y + dy)).ToList();
        }

        /// 이 자리를 눌렀는지(tol은 비율 단위 허용 거리)
        public bool Hit(Point p, double tol)
        {
            if (Kind == MarkKind.Arrow) return SegDist(p, new Point(X, Y), new Point(X + W, Y + H)) <= tol;
            if (Kind == MarkKind.Highlight && Points != null)
            {
                for (int i = 0; i + 1 < Points.Count; i++)
                {
                    if (SegDist(p, Points[i], Points[i + 1]) <= tol + Size / 2) return true;
                }
                return false;
            }
            if (Kind == MarkKind.Box)
            {
                var b = Bounds;
                var outer = new Rect(b.X - tol, b.Y - tol, b.Width + tol * 2, b.Height + tol * 2);
                var inner = new Rect(b.X + tol, b.Y + tol, Math.Max(0, b.Width - tol * 2), Math.Max(0, b.Height - tol * 2));
                return outer.Contains(p) && !inner.Contains(p);
            }
            return Bounds.Contains(p);
        }

        static double SegDist(Point p, Point a, Point b)
        {
            Vector v = b - a, w = p - a;
            double len2 = v.LengthSquared;
            double t = len2 <= 1e-12 ? 0 : Math.Max(0, Math.Min(1, (w.X * v.X + w.Y * v.Y) / len2));
            return (p - (a + v * t)).Length;
        }
    }

    /// 사진 한 장의 손질 내용. 적용 순서: 문서 펴기 → 돌리기·뒤집기 → 기울기 → 자르기 → 보정 → 가리기 → 글·표시 → 크기 → 꾸미기
    public class PhotoEdit
    {
        /// 문서 펴기: 세운 원본 기준 네 귀퉁이(왼위, 오위, 오아래, 왼아래)
        public Point[] Quad;
        /// 시계 방향 90도 횟수
        public int Turns;
        /// 돌리기 전에 좌우 뒤집기
        public bool Mirror;
        /// 기울기 바로잡기(도)
        public double Angle;
        public Rect Crop = new Rect(0, 0, 1, 1);
        public double Brightness, Contrast, Saturation, Sharpness;
        /// "" · gray · bw · clean
        public string Filter = "";
        public List<PhotoMark> Marks = new List<PhotoMark>();
        /// 0이면 그대로
        public double ResizePercent;
        public int ResizeW, ResizeH;
        /// 짧은 변에 대한 비율
        public double Margin, Corner;
        /// 0이면 그대로. 가로÷세로.
        public double CanvasRatio;
        public uint BackRgb = 0xFFFFFF;
        public bool BackTransparent, BackBlur;

        public PhotoEdit Copy()
        {
            var e = (PhotoEdit)MemberwiseClone();
            e.Marks = Marks.Select(m => m.Copy()).ToList();
            if (Quad != null) e.Quad = (Point[])Quad.Clone();
            return e;
        }

        public bool HasGeometry
        {
            get { return Quad != null || Turns != 0 || Mirror || Math.Abs(Angle) > 0.001 || !IsFull(Crop); }
        }

        public bool HasFrame
        {
            get { return Margin > 0 || Corner > 0 || CanvasRatio > 0; }
        }

        public bool HasTone
        {
            get { return Brightness != 0 || Contrast != 0 || Saturation != 0 || Sharpness != 0 || !string.IsNullOrEmpty(Filter); }
        }

        public bool HasResize
        {
            get { return ResizePercent > 0 || ResizeW > 0 || ResizeH > 0; }
        }

        public bool IsUntouched
        {
            get { return !HasGeometry && !HasFrame && !HasTone && !HasResize && Marks.Count == 0; }
        }

        static bool IsFull(Rect r)
        {
            return r.X <= 1e-6 && r.Y <= 1e-6 && r.Width >= 1 - 1e-6 && r.Height >= 1 - 1e-6;
        }

        /// 이 값이 같으면 기하 단계의 결과도 같다.
        public string GeometryKey()
        {
            var sb = new System.Text.StringBuilder();
            if (Quad != null)
            {
                foreach (var p in Quad) sb.Append(p.X.ToString("0.0000")).Append(',').Append(p.Y.ToString("0.0000")).Append(';');
            }
            sb.Append(Turns).Append(Mirror ? 'm' : '-').Append(Angle.ToString("0.00")).Append('|');
            sb.Append(Crop.X.ToString("0.00000")).Append(',').Append(Crop.Y.ToString("0.00000")).Append(',').Append(Crop.Width.ToString("0.00000")).Append(',').Append(Crop.Height.ToString("0.00000"));
            return sb.ToString();
        }

        void MapAll(Func<Point, Point> f, bool swap)
        {
            var c = new Rect(f(new Point(Crop.Left, Crop.Top)), f(new Point(Crop.Right, Crop.Bottom)));
            Crop = c;
            foreach (var m in Marks)
            {
                if (!m.Whole) m.Map(f);
            }
            if (swap)
            {
                int t = ResizeW;
                ResizeW = ResizeH;
                ResizeH = t;
                if (CanvasRatio > 0) CanvasRatio = 1 / CanvasRatio;
            }
        }

        public void RotateRight()
        {
            Turns = (Turns + 1) % 4;
            MapAll(p => new Point(1 - p.Y, p.X), true);
        }

        public void RotateLeft()
        {
            Turns = (Turns + 3) % 4;
            MapAll(p => new Point(p.Y, 1 - p.X), true);
        }

        public void FlipH()
        {
            Turns = (4 - Turns) % 4;
            Mirror = !Mirror;
            Angle = -Angle;
            MapAll(p => new Point(1 - p.X, p.Y), false);
        }

        public void FlipV()
        {
            Turns = (6 - Turns) % 4;
            Mirror = !Mirror;
            Angle = -Angle;
            MapAll(p => new Point(p.X, 1 - p.Y), false);
        }

        /// 지금 보이는 사진에서 c(0~1)만 남긴다. aspect는 지금 사진의 가로÷세로.
        public void CropTo(Rect c, double aspect)
        {
            Crop = new Rect(Crop.X + c.X * Crop.Width, Crop.Y + c.Y * Crop.Height, c.Width * Crop.Width, c.Height * Crop.Height);
            double s0 = Math.Min(aspect, 1), s1 = Math.Min(aspect * c.Width, c.Height);
            double scale = s1 > 0 ? s0 / s1 : 1;
            Func<Point, Point> f = p => new Point((p.X - c.X) / c.Width, (p.Y - c.Y) / c.Height);
            var keep = new List<PhotoMark>();
            foreach (var m in Marks)
            {
                if (m.Whole)
                {
                    keep.Add(m);
                    continue;
                }
                m.Map(f);
                m.Size *= scale;
                m.Strength *= scale;
                var b = m.Bounds;
                if (b.Right <= 0 || b.Bottom <= 0 || b.Left >= 1 || b.Top >= 1) continue;
                keep.Add(m);
            }
            Marks = keep;
            ResizeW = 0;
            ResizeH = 0;
        }
    }

    /// 파랑·초록·빨강·투명 순서의 8비트 픽셀 묶음
    public sealed class Bgra
    {
        public readonly int W, H;
        public readonly byte[] P;

        public Bgra(int w, int h)
        {
            W = w;
            H = h;
            P = new byte[(long)w * h * 4];
        }

        public static Bgra From(BitmapSource s)
        {
            BitmapSource c = s.Format == PixelFormats.Bgra32 ? s : new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
            var b = new Bgra(c.PixelWidth, c.PixelHeight);
            c.CopyPixels(b.P, b.W * 4, 0);
            return b;
        }

        public BitmapSource ToBitmap()
        {
            var bs = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, P, W * 4);
            bs.Freeze();
            return bs;
        }
    }

    public static class PhotoRender
    {
        public const int ProxySide = 1600;

        /// 화면에서 쓸 줄인 사진(세운 상태). 실제 크기도 알려 준다.
        public static BitmapSource Proxy(string path, out int fullW, out int fullH)
        {
            var src = Imaging.Load(path);
            fullW = src.PixelWidth;
            fullH = src.PixelHeight;
            return Bgra.From(Imaging.Fit(src, ProxySide)).ToBitmap();
        }

        public static BitmapSource Render(BitmapSource src, PhotoEdit e)
        {
            return Finish(Geometry(src, e), e, false);
        }

        // ── 기하 ──

        public static BitmapSource Geometry(BitmapSource src, PhotoEdit e)
        {
            BitmapSource s = src;
            if (e.Quad != null) s = Warp(s, e.Quad);
            if (e.Turns != 0 || e.Mirror)
            {
                var g = new TransformGroup();
                if (e.Mirror) g.Children.Add(new ScaleTransform(-1, 1));
                if (e.Turns != 0) g.Children.Add(new RotateTransform(90 * e.Turns));
                var t = new TransformedBitmap(s, g);
                t.Freeze();
                s = t;
            }
            if (Math.Abs(e.Angle) > 0.001) s = Straighten(s, e.Angle);
            int w = s.PixelWidth, h = s.PixelHeight;
            int cx = Clamp((int)Math.Round(e.Crop.X * w), 0, w - 1);
            int cy = Clamp((int)Math.Round(e.Crop.Y * h), 0, h - 1);
            int cw = Clamp((int)Math.Round(e.Crop.Width * w), 1, w - cx);
            int ch = Clamp((int)Math.Round(e.Crop.Height * h), 1, h - cy);
            if (cx != 0 || cy != 0 || cw != w || ch != h)
            {
                var c = new CroppedBitmap(s, new Int32Rect(cx, cy, cw, ch));
                c.Freeze();
                s = c;
            }
            return s;
        }

        static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        static byte Byte(double v)
        {
            return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
        }

        /// 네 귀퉁이가 직사각형이 되도록 편다.
        static BitmapSource Warp(BitmapSource s, Point[] q)
        {
            var src = Bgra.From(s);
            double[] x = new double[4], y = new double[4];
            for (int i = 0; i < 4; i++)
            {
                x[i] = q[i].X * (src.W - 1);
                y[i] = q[i].Y * (src.H - 1);
            }
            Func<int, int, double> len = (a, b) => Math.Sqrt((x[a] - x[b]) * (x[a] - x[b]) + (y[a] - y[b]) * (y[a] - y[b]));
            double avgW = (len(0, 1) + len(3, 2)) / 2, avgH = (len(0, 3) + len(1, 2)) / 2;
            // 비스듬히 찍으면 변 길이만으로는 비율이 틀어진다. 원근으로 진짜 가로세로 비를 구해 본다(안 되면 변 길이대로).
            double ratio;
            if (TrueAspect(x, y, (src.W - 1) / 2.0, (src.H - 1) / 2.0, out ratio))
            {
                double h2 = avgH, w2 = h2 * ratio;
                if (w2 < avgW)
                {
                    w2 = avgW;
                    h2 = w2 / ratio;
                }
                avgW = w2;
                avgH = h2;
            }
            int outW = Math.Max(8, (int)Math.Round(avgW));
            int outH = Math.Max(8, (int)Math.Round(avgH));
            // 네모 한 칸 → 사각형으로 가는 사영 변환(Heckbert, 1989)
            double dx1 = x[1] - x[2], dx2 = x[3] - x[2], sx = x[0] - x[1] + x[2] - x[3];
            double dy1 = y[1] - y[2], dy2 = y[3] - y[2], sy = y[0] - y[1] + y[2] - y[3];
            double den = dx1 * dy2 - dx2 * dy1;
            double g = Math.Abs(den) < 1e-9 ? 0 : (sx * dy2 - dx2 * sy) / den;
            double h = Math.Abs(den) < 1e-9 ? 0 : (dx1 * sy - sx * dy1) / den;
            double a0 = x[1] - x[0] + g * x[1], b0 = x[3] - x[0] + h * x[3], c0 = x[0];
            double d0 = y[1] - y[0] + g * y[1], e0 = y[3] - y[0] + h * y[3], f0 = y[0];
            var dst = new Bgra(outW, outH);
            Parallel.For(0, outH, j =>
            {
                double v = outH > 1 ? j / (double)(outH - 1) : 0;
                int o = j * outW * 4;
                for (int i = 0; i < outW; i++, o += 4)
                {
                    double u = outW > 1 ? i / (double)(outW - 1) : 0;
                    double w = g * u + h * v + 1;
                    Sample(src, (a0 * u + b0 * v + c0) / w, (d0 * u + e0 * v + f0) / w, dst.P, o);
                }
            });
            return dst.ToBitmap();
        }

        /// 네 귀퉁이(왼위·오른위·오른아래·왼아래)로 실제 종이의 가로÷세로를 구한다.
        /// Zhang·He, "Whiteboard scanning and image enhancement"(2007)의 식. 렌즈 중심이 사진 가운데라고 본다.
        /// 거의 정면이면 계산이 불안정하니 false(변 길이로 충분하다).
        static bool TrueAspect(double[] x, double[] y, double cx, double cy, out double ratio)
        {
            ratio = 0;
            double[] m1 = { x[0] - cx, y[0] - cy, 1 }, m2 = { x[1] - cx, y[1] - cy, 1 }, m3 = { x[3] - cx, y[3] - cy, 1 }, m4 = { x[2] - cx, y[2] - cy, 1 };
            Func<double[], double[], double[]> cross = (a, b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
            Func<double[], double[], double> dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
            double d2 = dot(cross(m2, m4), m3), d3 = dot(cross(m3, m4), m2);
            if (Math.Abs(d2) < 1e-6 || Math.Abs(d3) < 1e-6) return false;
            double k2 = dot(cross(m1, m4), m3) / d2, k3 = dot(cross(m1, m4), m2) / d3;
            double[] n2 = { k2 * m2[0] - m1[0], k2 * m2[1] - m1[1], k2 - 1 };
            double[] n3 = { k3 * m3[0] - m1[0], k3 * m3[1] - m1[1], k3 - 1 };
            double z = n2[2] * n3[2];
            double diag2 = cx * cx * 4 + cy * cy * 4;
            if (Math.Abs(z) < 1e-5) return false;
            double f2 = -(n2[0] * n3[0] + n2[1] * n3[1]) / z;
            // 초점이 사진 대각선의 0.3~8배 밖이면 믿지 않는다.
            if (f2 < 0.09 * diag2 || f2 > 64 * diag2) return false;
            double a2 = (n2[0] * n2[0] + n2[1] * n2[1]) / f2 + n2[2] * n2[2];
            double a3 = (n3[0] * n3[0] + n3[1] * n3[1]) / f2 + n3[2] * n3[2];
            if (a2 <= 0 || a3 <= 0) return false;
            ratio = Math.Sqrt(a2 / a3);
            return ratio > 0.2 && ratio < 5;
        }

        static void Sample(Bgra s, double fx, double fy, byte[] o, int at)
        {
            if (fx < 0) fx = 0;
            if (fy < 0) fy = 0;
            if (fx > s.W - 1) fx = s.W - 1;
            if (fy > s.H - 1) fy = s.H - 1;
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(s.W - 1, x0 + 1), y1 = Math.Min(s.H - 1, y0 + 1);
            double tx = fx - x0, ty = fy - y0;
            int p00 = (y0 * s.W + x0) * 4, p10 = (y0 * s.W + x1) * 4, p01 = (y1 * s.W + x0) * 4, p11 = (y1 * s.W + x1) * 4;
            var p = s.P;
            for (int c = 0; c < 4; c++)
            {
                double top = p[p00 + c] * (1 - tx) + p[p10 + c] * tx;
                double bot = p[p01 + c] * (1 - tx) + p[p11 + c] * tx;
                o[at + c] = (byte)(top * (1 - ty) + bot * ty + 0.5);
            }
        }

        /// 가운데를 축으로 돌린다. 귀퉁이가 비지 않게 살짝 키운다.
        static BitmapSource Straighten(BitmapSource s, double deg)
        {
            var src = Bgra.From(s);
            double rad = deg * Math.PI / 180, c = Math.Cos(rad), sn = Math.Sin(rad);
            double w = src.W, h = src.H;
            double zoom = Math.Max((w * Math.Abs(c) + h * Math.Abs(sn)) / w, (w * Math.Abs(sn) + h * Math.Abs(c)) / h);
            double ic = c / zoom, isn = sn / zoom;
            double cx = (w - 1) / 2, cy = (h - 1) / 2;
            var dst = new Bgra(src.W, src.H);
            Parallel.For(0, src.H, j =>
            {
                int o = j * src.W * 4;
                double dy = j - cy;
                for (int i = 0; i < src.W; i++, o += 4)
                {
                    double dx = i - cx;
                    Sample(src, cx + dx * ic + dy * isn, cy - dx * isn + dy * ic, dst.P, o);
                }
            });
            return dst.ToBitmap();
        }

        // ── 마무리 단계 ──

        public static BitmapSource Finish(BitmapSource geo, PhotoEdit e, bool preview)
        {
            BitmapSource img = geo;
            bool redact = e.Marks.Any(m => m.Kind == MarkKind.Redact);
            if (e.HasTone || redact)
            {
                var b = Bgra.From(geo);
                Adjust(b, e);
                ApplyFilter(b, e.Filter);
                foreach (var m in e.Marks)
                {
                    if (m.Kind == MarkKind.Redact) Redact(b, m);
                }
                img = b.ToBitmap();
            }
            if (e.Marks.Any(m => m.Kind != MarkKind.Redact)) img = DrawMarks(img, e);
            if (!preview) img = Resize(img, e);
            return Frame(img, e);
        }

        static void Adjust(Bgra b, PhotoEdit e)
        {
            if (e.Brightness != 0 || e.Contrast != 0 || e.Saturation != 0)
            {
                var lut = new byte[256];
                double gamma = Math.Pow(2, -e.Brightness * 1.25);
                double cf = e.Contrast >= 0 ? 1 + e.Contrast * 1.6 : 1 + e.Contrast * 0.75;
                for (int i = 0; i < 256; i++)
                {
                    double v = 255 * Math.Pow(i / 255.0, gamma);
                    lut[i] = Byte((v - 127.5) * cf + 127.5);
                }
                double sat = 1 + e.Saturation;
                byte[] p = b.P;
                int w = b.W;
                Parallel.For(0, b.H, y =>
                {
                    int i = y * w * 4;
                    for (int x = 0; x < w; x++, i += 4)
                    {
                        double bl = lut[p[i]], gr = lut[p[i + 1]], rd = lut[p[i + 2]];
                        if (sat != 1)
                        {
                            double l = 0.114 * bl + 0.587 * gr + 0.299 * rd;
                            bl = l + (bl - l) * sat;
                            gr = l + (gr - l) * sat;
                            rd = l + (rd - l) * sat;
                        }
                        p[i] = Byte(bl);
                        p[i + 1] = Byte(gr);
                        p[i + 2] = Byte(rd);
                    }
                });
            }
            if (e.Sharpness > 0)
            {
                int r = Math.Max(1, Math.Min(b.W, b.H) / 700);
                byte[] soft = BoxBlur((byte[])b.P.Clone(), b.W, b.H, r, 2);
                double amount = e.Sharpness * 1.8;
                byte[] p = b.P;
                int w = b.W;
                Parallel.For(0, b.H, y =>
                {
                    int i = y * w * 4;
                    for (int x = 0; x < w; x++, i += 4)
                    {
                        for (int c = 0; c < 3; c++) p[i + c] = Byte(p[i + c] + (p[i + c] - soft[i + c]) * amount);
                    }
                });
            }
        }

        static void ApplyFilter(Bgra b, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return;
            byte[] p = b.P;
            int w = b.W, h = b.H;
            var gray = new byte[(long)w * h];
            Parallel.For(0, h, y =>
            {
                int i = y * w * 4, o = y * w;
                for (int x = 0; x < w; x++, i += 4, o++) gray[o] = (byte)((p[i] * 29 + p[i + 1] * 150 + p[i + 2] * 77) >> 8);
            });
            if (filter == "gray")
            {
                Parallel.For(0, h, y =>
                {
                    int i = y * w * 4, o = y * w;
                    for (int x = 0; x < w; x++, i += 4, o++) p[i] = p[i + 1] = p[i + 2] = gray[o];
                });
                return;
            }
            // 종이 밝기(넓게 흐린 회색)를 구해 그 기준으로 나눈다. 그늘이 져도 바탕이 고르게 희어진다.
            byte[] back = BoxBlurGray(gray, w, h, Math.Max(10, Math.Min(w, h) / 22), 2);
            if (filter == "bw")
            {
                Parallel.For(0, h, y =>
                {
                    int i = y * w * 4, o = y * w;
                    for (int x = 0; x < w; x++, i += 4, o++)
                    {
                        byte v = gray[o] < back[o] * 0.84 ? (byte)0 : (byte)255;
                        p[i] = p[i + 1] = p[i + 2] = v;
                    }
                });
                return;
            }
            var curve = new byte[256];
            for (int i = 0; i < 256; i++) curve[i] = Byte((i - 46) * 255.0 / (232 - 46));
            Parallel.For(0, h, y =>
            {
                int i = y * w * 4, o = y * w;
                for (int x = 0; x < w; x++, i += 4, o++)
                {
                    double scale = 245.0 / Math.Max(30, (int)back[o]);
                    for (int c = 0; c < 3; c++) p[i + c] = curve[Byte(p[i + c] * scale)];
                }
            });
        }

        /// 가로·세로 상자 흐림을 passes번(가우스 흐림에 가깝다). 투명값은 그대로.
        public static byte[] BoxBlur(byte[] src, int w, int h, int r, int passes)
        {
            byte[] a = src, b = new byte[src.Length];
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
            return a;
        }

        static byte[] BoxBlurGray(byte[] src, int w, int h, int r, int passes)
        {
            byte[] a = (byte[])src.Clone(), b = new byte[src.Length];
            int div = 2 * r + 1;
            for (int n = 0; n < passes; n++)
            {
                byte[] from = a, to = b;
                Parallel.For(0, h, y =>
                {
                    int line = y * w, s = 0;
                    for (int k = -r; k <= r; k++) s += from[line + (k < 0 ? 0 : k >= w ? w - 1 : k)];
                    for (int x = 0; x < w; x++)
                    {
                        to[line + x] = (byte)(s / div);
                        s += from[line + (x + r + 1 >= w ? w - 1 : x + r + 1)] - from[line + (x - r < 0 ? 0 : x - r)];
                    }
                });
                Parallel.For(0, w, x =>
                {
                    int s = 0;
                    for (int k = -r; k <= r; k++) s += to[(k < 0 ? 0 : k >= h ? h - 1 : k) * w + x];
                    for (int y = 0; y < h; y++)
                    {
                        from[y * w + x] = (byte)(s / div);
                        s += to[(y + r + 1 >= h ? h - 1 : y + r + 1) * w + x] - to[(y - r < 0 ? 0 : y - r) * w + x];
                    }
                });
            }
            return a;
        }

        static void Redact(Bgra b, PhotoMark m)
        {
            int sh = Math.Min(b.W, b.H);
            Rect r = m.Bounds;
            int x0 = Clamp((int)Math.Floor(r.Left * b.W), 0, b.W), y0 = Clamp((int)Math.Floor(r.Top * b.H), 0, b.H);
            int x1 = Clamp((int)Math.Ceiling(r.Right * b.W), 0, b.W), y1 = Clamp((int)Math.Ceiling(r.Bottom * b.H), 0, b.H);
            int w = x1 - x0, h = y1 - y0;
            if (w <= 0 || h <= 0) return;
            bool oval = m.Oval && !m.Whole;
            switch (m.Mode)
            {
                case "fill":
                    FillRegion(b, x0, y0, w, h, m.Rgb, oval);
                    break;
                case "mosaic":
                    MosaicRegion(b, x0, y0, w, h, Math.Max(3, (int)Math.Round(m.Strength * sh * 0.07)), oval);
                    break;
                default:
                    BlurRegion(b, x0, y0, w, h, Math.Max(2, (int)Math.Round(m.Strength * sh * 0.05)), oval);
                    break;
            }
        }

        static bool InOval(int x, int y, int w, int h)
        {
            double nx = (x + 0.5) / w - 0.5, ny = (y + 0.5) / h - 0.5;
            return nx * nx + ny * ny <= 0.25;
        }

        static void FillRegion(Bgra b, int x0, int y0, int w, int h, uint rgb, bool oval)
        {
            byte cr = (byte)(rgb >> 16), cg = (byte)(rgb >> 8), cb = (byte)rgb;
            for (int y = 0; y < h; y++)
            {
                int i = ((y0 + y) * b.W + x0) * 4;
                for (int x = 0; x < w; x++, i += 4)
                {
                    if (oval && !InOval(x, y, w, h)) continue;
                    b.P[i] = cb;
                    b.P[i + 1] = cg;
                    b.P[i + 2] = cr;
                    b.P[i + 3] = 255;
                }
            }
        }

        static void MosaicRegion(Bgra b, int x0, int y0, int w, int h, int block, bool oval)
        {
            for (int by = 0; by < h; by += block)
            {
                int bh = Math.Min(block, h - by);
                for (int bx = 0; bx < w; bx += block)
                {
                    int bw = Math.Min(block, w - bx);
                    long s0 = 0, s1 = 0, s2 = 0;
                    for (int y = 0; y < bh; y++)
                    {
                        int i = ((y0 + by + y) * b.W + x0 + bx) * 4;
                        for (int x = 0; x < bw; x++, i += 4)
                        {
                            s0 += b.P[i];
                            s1 += b.P[i + 1];
                            s2 += b.P[i + 2];
                        }
                    }
                    int n = bw * bh;
                    byte c0 = (byte)(s0 / n), c1 = (byte)(s1 / n), c2 = (byte)(s2 / n);
                    for (int y = 0; y < bh; y++)
                    {
                        int i = ((y0 + by + y) * b.W + x0 + bx) * 4;
                        for (int x = 0; x < bw; x++, i += 4)
                        {
                            if (oval && !InOval(bx + x, by + y, w, h)) continue;
                            b.P[i] = c0;
                            b.P[i + 1] = c1;
                            b.P[i + 2] = c2;
                        }
                    }
                }
            }
        }

        static void BlurRegion(Bgra b, int x0, int y0, int w, int h, int radius, bool oval)
        {
            var part = new byte[(long)w * h * 4];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(b.P, ((y0 + y) * b.W + x0) * 4, part, y * w * 4, w * 4);
            byte[] soft = BoxBlur(part, w, h, Math.Min(radius, Math.Max(w, h)), 3);
            if (!oval)
            {
                for (int y = 0; y < h; y++) Buffer.BlockCopy(soft, y * w * 4, b.P, ((y0 + y) * b.W + x0) * 4, w * 4);
                return;
            }
            for (int y = 0; y < h; y++)
            {
                int i = ((y0 + y) * b.W + x0) * 4, o = y * w * 4;
                for (int x = 0; x < w; x++, i += 4, o += 4)
                {
                    if (!InOval(x, y, w, h)) continue;
                    b.P[i] = soft[o];
                    b.P[i + 1] = soft[o + 1];
                    b.P[i + 2] = soft[o + 2];
                }
            }
        }

        // ── 글자·화살표·형광펜·네모 ──

        public static FormattedText TextOf(PhotoMark m, double shortSide)
        {
            Brush brush = new SolidColorBrush(Color.FromRgb((byte)(m.Rgb >> 16), (byte)(m.Rgb >> 8), (byte)m.Rgb));
            brush.Freeze();
            var face = new Typeface(Theme.Font, FontStyles.Normal, m.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            return new FormattedText(string.IsNullOrEmpty(m.Text) ? " " : m.Text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, face, Math.Max(4, m.Size * shortSide), brush);
        }

        static BitmapSource DrawMarks(BitmapSource img, PhotoEdit e)
        {
            int w = img.PixelWidth, h = img.PixelHeight;
            double sh = Math.Min(w, h);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(img, new Rect(0, 0, w, h));
                foreach (var m in e.Marks)
                {
                    if (m.Kind == MarkKind.Redact) continue;
                    var color = Color.FromRgb((byte)(m.Rgb >> 16), (byte)(m.Rgb >> 8), (byte)m.Rgb);
                    Brush brush = new SolidColorBrush(color);
                    brush.Freeze();
                    double t = Math.Max(1, m.Size * sh);
                    switch (m.Kind)
                    {
                        case MarkKind.Text:
                            {
                                var ft = TextOf(m, sh);
                                m.W = ft.WidthIncludingTrailingWhitespace / w;
                                m.H = ft.Height / h;
                                dc.DrawText(ft, new Point(m.X * w, m.Y * h));
                                break;
                            }
                        case MarkKind.Arrow:
                            DrawArrow(dc, new Point(m.X * w, m.Y * h), new Point((m.X + m.W) * w, (m.Y + m.H) * h), t, brush);
                            break;
                        case MarkKind.Box:
                            {
                                var pen = new Pen(brush, t) { LineJoin = PenLineJoin.Round };
                                pen.Freeze();
                                dc.DrawRoundedRectangle(null, pen, new Rect(m.X * w, m.Y * h, m.W * w, m.H * h), t * 1.2, t * 1.2);
                                break;
                            }
                        case MarkKind.Highlight:
                            if (m.Points != null && m.Points.Count >= 2)
                            {
                                Brush soft = new SolidColorBrush(Color.FromArgb(0x78, color.R, color.G, color.B));
                                soft.Freeze();
                                var pen = new Pen(soft, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                                pen.Freeze();
                                var g = new StreamGeometry();
                                using (var c = g.Open())
                                {
                                    c.BeginFigure(new Point(m.Points[0].X * w, m.Points[0].Y * h), false, false);
                                    for (int i = 1; i < m.Points.Count; i++) c.LineTo(new Point(m.Points[i].X * w, m.Points[i].Y * h), true, true);
                                }
                                g.Freeze();
                                dc.DrawGeometry(null, pen, g);
                            }
                            break;
                    }
                }
            }
            return Rasterize(dv, w, h);
        }

        static BitmapSource Rasterize(Visual v, int w, int h)
        {
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(v);
            rtb.Freeze();
            return rtb;
        }

        static void DrawArrow(DrawingContext dc, Point a, Point b, double t, Brush brush)
        {
            Vector v = b - a;
            double len = v.Length;
            if (len < 1) return;
            v /= len;
            Vector n = new Vector(-v.Y, v.X);
            double head = Math.Min(Math.Max(t * 3.4, 10), len * 0.75);
            Point neck = b - v * head * 0.8;
            var pen = new Pen(brush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Flat };
            pen.Freeze();
            dc.DrawLine(pen, a, neck);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(b, true, true);
                c.LineTo(b - v * head + n * head * 0.58, true, true);
                c.LineTo(b - v * head - n * head * 0.58, true, true);
            }
            g.Freeze();
            var edge = new Pen(brush, t * 0.4) { LineJoin = PenLineJoin.Round };
            edge.Freeze();
            dc.DrawGeometry(brush, edge, g);
        }

        // ── 크기·꾸미기 ──

        /// 저장했을 때의 크기(자르기까지 반영된 w×h 기준)
        public static void ResultSize(int w, int h, PhotoEdit e, out int rw, out int rh)
        {
            rw = w;
            rh = h;
            if (e.ResizeW > 0 || e.ResizeH > 0)
            {
                rw = e.ResizeW > 0 ? e.ResizeW : (int)Math.Round((double)w * e.ResizeH / h);
                rh = e.ResizeH > 0 ? e.ResizeH : (int)Math.Round((double)h * e.ResizeW / w);
            }
            else if (e.ResizePercent > 0)
            {
                rw = (int)Math.Round(w * e.ResizePercent / 100);
                rh = (int)Math.Round(h * e.ResizePercent / 100);
            }
            rw = Math.Max(1, Math.Min(20000, rw));
            rh = Math.Max(1, Math.Min(20000, rh));
        }

        static BitmapSource Resize(BitmapSource img, PhotoEdit e)
        {
            if (!e.HasResize) return img;
            int w = img.PixelWidth, h = img.PixelHeight, rw, rh;
            ResultSize(w, h, e, out rw, out rh);
            if (rw == w && rh == h) return img;
            var t = new TransformedBitmap(img, new ScaleTransform((double)rw / w, (double)rh / h));
            t.Freeze();
            return t;
        }

        /// 꾸민 뒤 사진 자리(바탕 안에서의 비율). 화면에서 표시 위치를 맞출 때 쓴다.
        public static Rect ContentBox(int w, int h, PhotoEdit e)
        {
            double cw, ch;
            CanvasSize(w, h, e, out cw, out ch);
            return new Rect((cw - w) / 2 / cw, (ch - h) / 2 / ch, w / cw, h / ch);
        }

        static void CanvasSize(int w, int h, PhotoEdit e, out double cw, out double ch)
        {
            double m = Math.Round(e.Margin * Math.Min(w, h));
            cw = w + 2 * m;
            ch = h + 2 * m;
            if (e.CanvasRatio > 0)
            {
                if (cw / ch < e.CanvasRatio) cw = Math.Round(ch * e.CanvasRatio);
                else ch = Math.Round(cw / e.CanvasRatio);
            }
        }

        static BitmapSource Frame(BitmapSource img, PhotoEdit e)
        {
            if (!e.HasFrame) return img;
            int w = img.PixelWidth, h = img.PixelHeight;
            double cw, ch;
            CanvasSize(w, h, e, out cw, out ch);
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
            {
                var full = new Rect(0, 0, cw, ch);
                if (!e.BackTransparent)
                {
                    if (e.BackBlur)
                    {
                        // 바탕: 사진을 작게 줄여 흐린 뒤 꽉 차게 펼친다.
                        var small = Bgra.From(Imaging.Fit(img, 120));
                        var soft = new Bgra(small.W, small.H);
                        Buffer.BlockCopy(BoxBlur(small.P, small.W, small.H, 6, 3), 0, soft.P, 0, small.P.Length);
                        double scale = Math.Max(cw / small.W, ch / small.H);
                        double dw = small.W * scale, dh = small.H * scale;
                        dc.PushClip(new RectangleGeometry(full));
                        dc.DrawImage(soft.ToBitmap(), new Rect((cw - dw) / 2, (ch - dh) / 2, dw, dh));
                        dc.Pop();
                    }
                    else
                    {
                        Brush back = new SolidColorBrush(Color.FromRgb((byte)(e.BackRgb >> 16), (byte)(e.BackRgb >> 8), (byte)e.BackRgb));
                        back.Freeze();
                        dc.DrawRectangle(back, null, full);
                    }
                }
                var r = new Rect((cw - w) / 2, (ch - h) / 2, w, h);
                if (e.Corner > 0)
                {
                    double rad = e.Corner * Math.Min(w, h);
                    dc.PushClip(new RectangleGeometry(r, rad, rad));
                    dc.DrawImage(img, r);
                    dc.Pop();
                }
                else dc.DrawImage(img, r);
            }
            return Rasterize(dv, (int)cw, (int)ch);
        }

        // ── 저장 ──

        /// 투명한 곳이 없으면 PNG에 투명 칸을 넣지 않아 파일이 작아진다.
        static BitmapSource DropAlphaIfOpaque(BitmapSource s)
        {
            if (!Imaging.MayHaveAlpha(s) || s.Palette != null) return s;
            var b = Bgra.From(s);
            for (int i = 3; i < b.P.Length; i += 4)
            {
                if (b.P[i] != 255) return s;
            }
            var c = new FormatConvertedBitmap(b.ToBitmap(), PixelFormats.Bgr24, null, 0);
            c.Freeze();
            return c;
        }

        public static byte[] Encode(BitmapSource img, string fmt, int quality)
        {
            switch (fmt)
            {
                case "webp": return WebpLossless.Encode(img);
                case "ico": return IcoWriter.Encode(img);
                case "png": return Imaging.Encode(DropAlphaIfOpaque(img), "png", 0);
                default: return Imaging.Encode(img, fmt, quality);
            }
        }

        /// 원래 형식으로 저장할 때 실제로 쓸 형식
        public static string SaveFormatFor(string path, bool hasTransparency)
        {
            string have = Formats.ImageFormatOf(path);
            switch (have)
            {
                case "png":
                case "bmp":
                case "tiff":
                case "webp":
                    return have;
                case "gif":
                    return "png";
                default:
                    return hasTransparency ? "png" : "jpg";
            }
        }

        public static string ExtensionOf(string fmt)
        {
            return fmt == "tiff" ? ".tif" : "." + fmt;
        }
    }

    /// .ico 파일: 16~256 크기를 PNG로 담는다(비스타 이후 표준). 정사각형이 아니면 투명 여백을 넣는다.
    public static class IcoWriter
    {
        public static byte[] Encode(BitmapSource src)
        {
            int side = Math.Max(src.PixelWidth, src.PixelHeight);
            var sizes = new[] { 256, 128, 64, 48, 32, 24, 16 };
            var images = new List<byte[]>();
            var used = new List<int>();
            foreach (int s in sizes)
            {
                var dv = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
                using (var dc = dv.RenderOpen())
                {
                    double scale = (double)s / side;
                    double w = src.PixelWidth * scale, h = src.PixelHeight * scale;
                    dc.DrawImage(src, new Rect((s - w) / 2, (s - h) / 2, w, h));
                }
                var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                images.Add(Imaging.Encode(rtb, "png", 0));
                used.Add(s);
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)images.Count);
                int offset = 6 + 16 * images.Count;
                for (int i = 0; i < images.Count; i++)
                {
                    int s = used[i];
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var png in images) w.Write(png);
                w.Flush();
                return ms.ToArray();
            }
        }
    }

    /// 사진 속 정보(찍은 때·기기·위치) 읽기
    public static class PhotoInfo
    {
        public class Row
        {
            public string Label, Value;
            public bool Sensitive;
        }

        static object Query(BitmapMetadata m, params string[] paths)
        {
            foreach (var q in paths)
            {
                try
                {
                    var v = m.GetQuery(q);
                    if (v != null) return v;
                }
                catch { }
            }
            return null;
        }

        static double? Dms(object v)
        {
            try
            {
                var a = v as ulong[];
                if (a != null && a.Length >= 3)
                {
                    Func<ulong, double> r = x =>
                    {
                        uint num = (uint)x, den = (uint)(x >> 32);
                        return den == 0 ? 0 : (double)num / den;
                    };
                    return r(a[0]) + r(a[1]) / 60 + r(a[2]) / 3600;
                }
                var d = v as double[];
                if (d != null && d.Length >= 3) return d[0] + d[1] / 60 + d[2] / 3600;
            }
            catch { }
            return null;
        }

        static string Safe(Func<string> f)
        {
            try
            {
                string s = f();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
            catch
            {
                return null;
            }
        }

        public static List<Row> Read(string path)
        {
            var rows = new List<Row>();
            // 정보만 읽으므로 그림을 풀지 않는다. 그래서 스트림을 연 채로 안에서 다 읽는다.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = dec.Frames[0];
                rows.Add(new Row { Label = "크기", Value = frame.PixelWidth + " × " + frame.PixelHeight + " · " + Conversions.Size(fs.Length) });
                var m = frame.Metadata as BitmapMetadata;
                if (m != null) ReadMeta(m, rows);
            }
            return rows;
        }

        static void ReadMeta(BitmapMetadata m, List<Row> rows)
        {
            Action<string, string, bool> add = (label, value, sensitive) =>
            {
                if (value != null) rows.Add(new Row { Label = label, Value = value, Sensitive = sensitive });
            };
            add("찍은 때", Safe(() => m.DateTaken), true);
            string maker = Safe(() => m.CameraManufacturer), model = Safe(() => m.CameraModel);
            if (maker != null || model != null)
            {
                string cam = model ?? maker;
                if (maker != null && model != null && !model.StartsWith(maker, StringComparison.OrdinalIgnoreCase)) cam = maker + " " + model;
                add("기기", cam, true);
            }
            add("만든 프로그램", Safe(() => m.ApplicationName), false);
            add("글쓴이", Safe(() => m.Author == null ? null : string.Join(", ", m.Author)), true);
            add("설명", Safe(() => m.Comment ?? m.Subject ?? m.Title), true);
            var lat = Dms(Query(m, "/app1/ifd/gps/{ushort=2}", "/ifd/gps/{ushort=2}", "System.GPS.Latitude"));
            var lon = Dms(Query(m, "/app1/ifd/gps/{ushort=4}", "/ifd/gps/{ushort=4}", "System.GPS.Longitude"));
            if (lat.HasValue && lon.HasValue && (lat.Value != 0 || lon.Value != 0))
            {
                string la = (Query(m, "/app1/ifd/gps/{ushort=1}", "/ifd/gps/{ushort=1}", "System.GPS.LatitudeRef") as string) ?? "N";
                string lo = (Query(m, "/app1/ifd/gps/{ushort=3}", "/ifd/gps/{ushort=3}", "System.GPS.LongitudeRef") as string) ?? "E";
                double y = la.StartsWith("S", StringComparison.OrdinalIgnoreCase) ? -lat.Value : lat.Value;
                double x = lo.StartsWith("W", StringComparison.OrdinalIgnoreCase) ? -lon.Value : lon.Value;
                add("찍은 장소", y.ToString("0.0000", CultureInfo.InvariantCulture) + ", " + x.ToString("0.0000", CultureInfo.InvariantCulture), true);
            }
        }

        /// 지울 만한 정보(찍은 때·기기·장소 등)가 있는지
        public static bool HasSensitive(List<Row> rows)
        {
            return rows.Any(r => r.Sensitive);
        }
    }

    /// 화질을 건드리지 않고 사진 정보만 지운다(JPEG·PNG·WEBP). 다른 형식은 다시 저장한다.
    public static class PhotoStrip
    {
        /// 지운 결과(바이트). 어떤 형식인지는 원본과 같다.
        public static byte[] Run(string path, out string ext)
        {
            byte[] d = File.ReadAllBytes(path);
            string fmt = Formats.ImageFormatOf(path);
            ext = Path.GetExtension(path);
            switch (fmt)
            {
                case "jpg": return Jpeg(d);
                case "png": return Png(d);
                case "webp": return Webp(d);
                case "tiff":
                    return Imaging.Encode(Imaging.Load(path), "tiff", 0);
                case "bmp":
                    return null;
                case "gif":
                    return null;
                default:
                    // HEIC·AVIF는 윈도우가 쓸 수 없는 형식이라 JPG로 저장한다.
                    ext = ".jpg";
                    return Imaging.Jpeg(Imaging.Load(path), 94);
            }
        }

        public static byte[] Jpeg(byte[] d)
        {
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new UserFacingException("JPEG 파일이 아니에요.");
            var info = Imaging.ReadJpeg(d);
            using (var o = new MemoryStream(d.Length))
            {
                o.WriteByte(0xFF);
                o.WriteByte(0xD8);
                int i = 2;
                bool wroteOrientation = false;
                while (i + 1 < d.Length)
                {
                    if (d[i] != 0xFF)
                    {
                        i++;
                        continue;
                    }
                    byte m = d[i + 1];
                    if (m == 0xFF)
                    {
                        i++;
                        continue;
                    }
                    if (m == 0xD9)
                    {
                        // 그림 끝. 뒤에 붙은 것(휴대폰의 깊이 지도·미리 보기 등)은 버린다.
                        o.WriteByte(0xFF);
                        o.WriteByte(0xD9);
                        return o.ToArray();
                    }
                    if (i + 3 >= d.Length) break;
                    int len = (d[i + 2] << 8) | d[i + 3];
                    if (len < 2 || i + 2 + len > d.Length) break;
                    bool drop = false;
                    if (m >= 0xE0 && m <= 0xEF)
                    {
                        // APP0(JFIF)·APP2의 색 프로파일·APP14(Adobe 색 변환)만 그림에 필요하다.
                        drop = true;
                        if (m == 0xE0) drop = false;
                        if (m == 0xEE) drop = false;
                        if (m == 0xE2 && len >= 14 && Ascii(d, i + 4, 11) == "ICC_PROFILE") drop = false;
                    }
                    else if (m == 0xFE) drop = true;
                    if (!drop && !wroteOrientation && m != 0xE0 && info != null && info.Orientation > 1)
                    {
                        // 세로 사진이 눕지 않도록 방향 값 하나만 남긴다.
                        wroteOrientation = true;
                        WriteOrientation(o, info.Orientation);
                    }
                    if (!drop) o.Write(d, i, 2 + len);
                    i += 2 + len;
                    if (m == 0xDA)
                    {
                        // 압축된 그림 자료: 다음 마커까지 그대로 옮긴다.
                        int start = i;
                        while (i + 1 < d.Length)
                        {
                            if (d[i] == 0xFF && d[i + 1] != 0 && !(d[i + 1] >= 0xD0 && d[i + 1] <= 0xD7) && d[i + 1] != 0xFF) break;
                            i++;
                        }
                        o.Write(d, start, i - start);
                    }
                }
                return o.ToArray();
            }
        }

        static string Ascii(byte[] d, int at, int n)
        {
            return System.Text.Encoding.ASCII.GetString(d, at, n);
        }

        static void WriteOrientation(Stream o, int orientation)
        {
            var b = new byte[]
            {
                0xFF, 0xE1, 0x00, 34,
                (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
                (byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0,
                1, 0,
                0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0,
                0, 0, 0, 0
            };
            o.Write(b, 0, b.Length);
        }

        public static byte[] Png(byte[] d)
        {
            if (d.Length < 8) throw new UserFacingException("PNG 파일이 아니에요.");
            using (var o = new MemoryStream(d.Length))
            {
                o.Write(d, 0, 8);
                int i = 8;
                while (i + 12 <= d.Length)
                {
                    int len = (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
                    if (len < 0 || i + 12 + len > d.Length) break;
                    string type = Ascii(d, i + 4, 4);
                    bool drop = type == "tEXt" || type == "zTXt" || type == "iTXt" || type == "eXIf" || type == "tIME";
                    if (!drop) o.Write(d, i, 12 + len);
                    i += 12 + len;
                    if (type == "IEND") break;
                }
                return o.ToArray();
            }
        }

        public static byte[] Webp(byte[] d)
        {
            if (d.Length < 12 || Ascii(d, 0, 4) != "RIFF" || Ascii(d, 8, 4) != "WEBP") throw new UserFacingException("WEBP 파일이 아니에요.");
            using (var o = new MemoryStream(d.Length))
            {
                o.Write(d, 0, 12);
                int i = 12;
                while (i + 8 <= d.Length)
                {
                    int len = BitConverter.ToInt32(d, i + 4);
                    if (len < 0 || i + 8 + len > d.Length) break;
                    string type = Ascii(d, i, 4);
                    int whole = 8 + len + (len & 1);
                    if (i + whole > d.Length) whole = d.Length - i;
                    if (type != "EXIF" && type != "XMP ")
                    {
                        long at = o.Position;
                        o.Write(d, i, whole);
                        if (type == "VP8X" && len >= 1)
                        {
                            var buf = o.GetBuffer();
                            buf[at + 8] = (byte)(buf[at + 8] & ~0x0C);
                        }
                    }
                    i += whole;
                }
                var result = o.ToArray();
                byte[] size = BitConverter.GetBytes(result.Length - 8);
                Buffer.BlockCopy(size, 0, result, 4, 4);
                return result;
            }
        }
    }

    /// 문서 사진: 종이 네 귀퉁이 찾기와 글줄 기울기 재기
    public static class PaperFinder
    {
        /// 밝은 종이가 어두운 바탕 위에 있을 때 귀퉁이(왼위, 오위, 오아래, 왼아래). 못 찾으면 null.
        public static Point[] Find(BitmapSource src)
        {
            var small = Imaging.ToGray8(Imaging.Fit(src, 480));
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h];
            small.CopyPixels(px, w, 0);
            // 오츠 문턱값
            var hist = new int[256];
            foreach (byte b in px) hist[b]++;
            double total = px.Length, sum = 0;
            for (int i = 0; i < 256; i++) sum += i * hist[i];
            double sumB = 0, wB = 0, best = -1;
            int threshold = 128;
            for (int i = 0; i < 256; i++)
            {
                wB += hist[i];
                if (wB == 0) continue;
                double wF = total - wB;
                if (wF == 0) break;
                sumB += i * hist[i];
                double mB = sumB / wB, mF = (sum - sumB) / wF;
                double between = wB * wF * (mB - mF) * (mB - mF);
                if (between > best)
                {
                    best = between;
                    threshold = i;
                }
            }
            // 가장 큰 밝은 덩어리
            var label = new int[w * h];
            var stack = new Stack<int>();
            int bestLabel = 0, bestSize = 0, next = 0;
            for (int s = 0; s < px.Length; s++)
            {
                if (px[s] <= threshold || label[s] != 0) continue;
                next++;
                int size = 0;
                stack.Push(s);
                label[s] = next;
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    size++;
                    int x = p % w, y = p / w;
                    if (x > 0 && px[p - 1] > threshold && label[p - 1] == 0) { label[p - 1] = next; stack.Push(p - 1); }
                    if (x < w - 1 && px[p + 1] > threshold && label[p + 1] == 0) { label[p + 1] = next; stack.Push(p + 1); }
                    if (y > 0 && px[p - w] > threshold && label[p - w] == 0) { label[p - w] = next; stack.Push(p - w); }
                    if (y < h - 1 && px[p + w] > threshold && label[p + w] == 0) { label[p + w] = next; stack.Push(p + w); }
                }
                if (size > bestSize)
                {
                    bestSize = size;
                    bestLabel = next;
                }
            }
            if (bestLabel == 0 || bestSize < px.Length * 0.15 || bestSize > px.Length * 0.985) return null;
            int tl = -1, tr = -1, br = -1, bl = -1;
            int mTl = int.MaxValue, mTr = int.MinValue, mBr = int.MinValue, mBl = int.MaxValue;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x;
                    if (label[p] != bestLabel) continue;
                    if (x + y < mTl) { mTl = x + y; tl = p; }
                    if (x - y > mTr) { mTr = x - y; tr = p; }
                    if (x + y > mBr) { mBr = x + y; br = p; }
                    if (x - y < mBl) { mBl = x - y; bl = p; }
                }
            }
            Func<int, Point> at = p => new Point((p % w + 0.5) / w, (p / w + 0.5) / h);
            return new[] { at(tl), at(tr), at(br), at(bl) };
        }

        /// 글줄이 수평에서 몇 도 기울었는지. 고칠 값(도)을 돌려준다. 글이 없으면 0.
        public static double Skew(BitmapSource src)
        {
            var small = Imaging.ToGray8(Imaging.Fit(src, 1000));
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h];
            small.CopyPixels(px, w, 0);
            long total = 0;
            foreach (byte b in px) total += b;
            double mean = (double)total / px.Length;
            var xs = new List<int>();
            var ys = new List<int>();
            for (int y = 0; y < h; y += 1)
            {
                for (int x = 0; x < w; x += 1)
                {
                    if (px[y * w + x] < mean * 0.62)
                    {
                        xs.Add(x);
                        ys.Add(y);
                    }
                }
            }
            if (xs.Count < 200) return 0;
            int step = Math.Max(1, xs.Count / 60000);
            Func<double, double> score = deg =>
            {
                double rad = deg * Math.PI / 180, sn = Math.Sin(rad), c = Math.Cos(rad);
                int bins = h * 2;
                var counts = new int[bins];
                for (int i = 0; i < xs.Count; i += step)
                {
                    int b = (int)(ys[i] * c - xs[i] * sn + h / 2);
                    if (b >= 0 && b < bins) counts[b]++;
                }
                double s = 0;
                foreach (int n in counts) s += (double)n * n;
                return s;
            };
            double bestDeg = 0, bestScore = double.MinValue;
            for (double d = -12; d <= 12.001; d += 0.5)
            {
                double s = score(d);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestDeg = d;
                }
            }
            double center = bestDeg;
            for (double d = center - 0.5; d <= center + 0.501; d += 0.1)
            {
                double s = score(d);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestDeg = d;
                }
            }
            return Math.Round(-bestDeg, 1);
        }
    }

    /// 여러 장을 한 장으로
    public class CollageSpec
    {
        /// 0이면 저절로(장 수에 맞는 격자)
        public int Columns;
        /// 긴 변(픽셀)
        public int LongSide = 2400;
        /// 짧은 변에 대한 비율
        public double Gap = 0.015, Corner = 0.0;
        public uint BackRgb = 0xFFFFFF;
        /// 칸을 꽉 채운다(넘는 부분 잘림). 끄면 사진 전체가 보이게.
        public bool Fill = true;
        /// 가로÷세로. 0이면 칸을 정사각형으로.
        public double CellRatio = 4.0 / 3;
    }

    public static class Collage
    {
        public static int AutoColumns(int n)
        {
            if (n <= 3) return n;
            if (n == 4) return 2;
            if (n <= 9) return 3;
            return (int)Math.Ceiling(Math.Sqrt(n));
        }

        public static BitmapSource Render(IList<BitmapSource> photos, CollageSpec s, int longSide)
        {
            int n = photos.Count;
            int cols = s.Columns > 0 ? Math.Min(s.Columns, n) : AutoColumns(n);
            int rows = (int)Math.Ceiling((double)n / cols);
            double ratio = s.CellRatio > 0 ? s.CellRatio : 1;
            // 칸 크기를 먼저 정하고 전체 크기를 맞춘다.
            double unit = 1000, gapUnit = 1000 * s.Gap * 2;
            double cw = unit, ch = unit / ratio;
            double tw = cols * cw + (cols + 1) * gapUnit, th = rows * ch + (rows + 1) * gapUnit;
            double scale = longSide / Math.Max(tw, th);
            int W = (int)Math.Round(tw * scale), H = (int)Math.Round(th * scale);
            double g = gapUnit * scale;
            cw *= scale;
            ch *= scale;
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
            {
                Brush back = new SolidColorBrush(Color.FromRgb((byte)(s.BackRgb >> 16), (byte)(s.BackRgb >> 8), (byte)s.BackRgb));
                back.Freeze();
                dc.DrawRectangle(back, null, new Rect(0, 0, W, H));
                int start = n % cols == 0 ? cols : n % cols;
                for (int i = 0; i < n; i++)
                {
                    int col = i % cols, row = i / cols;
                    double x = g + col * (cw + g), y = g + row * (ch + g);
                    // 마지막 줄이 덜 차면 가운데로
                    if (row == rows - 1 && start < cols) x += (cols - start) * (cw + g) / 2;
                    var cell = new Rect(x, y, cw, ch);
                    var photo = photos[i];
                    double rad = s.Corner * Math.Min(cw, ch);
                    dc.PushClip(new RectangleGeometry(cell, rad, rad));
                    double pw = photo.PixelWidth, ph = photo.PixelHeight;
                    double fit = s.Fill ? Math.Max(cw / pw, ch / ph) : Math.Min(cw / pw, ch / ph);
                    double dw = pw * fit, dh = ph * fit;
                    dc.DrawImage(photo, new Rect(x + (cw - dw) / 2, y + (ch - dh) / 2, dw, dh));
                    dc.Pop();
                }
            }
            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
