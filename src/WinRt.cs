// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 윈도우에 들어 있는 기능(WinRT): PDF 쪽 그리기(Windows.Data.Pdf)와 글자 인식(Windows.Media.Ocr).
// 근거: Microsoft Learn의 Windows.Media.Ocr.OcrEngine · Windows.Graphics.Imaging · Windows.Data.Pdf 문서.
// SDK 없이 윈도우 기본 컴파일러로 만들기 위해 System.Runtime.WindowsRuntime의 확장 메서드(AsTask 등)는 쓰지 않고,
// 비동기 작업은 Completed 알림을, 스트림은 DataReader·DataWriter를 직접 쓴다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinFoundation = Windows.Foundation;
using WinPdf = Windows.Data.Pdf;
using WinImaging = Windows.Graphics.Imaging;
using WinOcrNs = Windows.Media.Ocr;
using WinStorage = Windows.Storage;
using WinStreams = Windows.Storage.Streams;

namespace Bakkum
{
    static class WinRtCall
    {
        /// WinRT 일은 MTA(스레드 풀)에서 하고 끝날 때까지 기다린다. 작업 스레드는 STA라서.
        public static T Mta<T>(Func<T> f)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) return f();
            try
            {
                return Task.Run(f).Result;
            }
            catch (AggregateException ae)
            {
                var inner = ae.Flatten().InnerExceptions.FirstOrDefault() ?? ae;
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }

        public static T Await<T>(WinFoundation.IAsyncOperation<T> op)
        {
            using (var done = new ManualResetEventSlim(false))
            {
                op.Completed = delegate (WinFoundation.IAsyncOperation<T> o, WinFoundation.AsyncStatus s) { done.Set(); };
                done.Wait();
            }
            Check(op);
            return op.GetResults();
        }

        public static void Await(WinFoundation.IAsyncAction op)
        {
            using (var done = new ManualResetEventSlim(false))
            {
                op.Completed = delegate (WinFoundation.IAsyncAction o, WinFoundation.AsyncStatus s) { done.Set(); };
                done.Wait();
            }
            Check(op);
            op.GetResults();
        }

        /// 진행률을 알려 주는 일. stop이 참을 돌려주면 취소한다.
        public static void Await(WinFoundation.IAsyncActionWithProgress<double> op, Action<double> progress, Func<bool> stop)
        {
            using (var done = new ManualResetEventSlim(false))
            {
                op.Progress = delegate (WinFoundation.IAsyncActionWithProgress<double> o, double p) { progress(p); };
                op.Completed = delegate (WinFoundation.IAsyncActionWithProgress<double> o, WinFoundation.AsyncStatus s) { done.Set(); };
                while (!done.Wait(200))
                {
                    if (!stop()) continue;
                    op.Cancel();
                    done.Wait();
                    throw new OperationCanceledException();
                }
            }
            Check(op);
            op.GetResults();
        }

        static void Check(WinFoundation.IAsyncInfo info)
        {
            if (info.Status == WinFoundation.AsyncStatus.Error)
            {
                var ex = info.ErrorCode;
                if (ex != null) ExceptionDispatchInfo.Capture(ex).Throw();
                throw new InvalidOperationException("WinRT 작업 실패");
            }
            if (info.Status == WinFoundation.AsyncStatus.Canceled) throw new OperationCanceledException();
        }

        public static WinStreams.InMemoryRandomAccessStream FromBytes(byte[] data)
        {
            var ras = new WinStreams.InMemoryRandomAccessStream();
            var w = new WinStreams.DataWriter(ras);
            w.WriteBytes(data);
            Await((WinFoundation.IAsyncOperation<uint>)w.StoreAsync());
            Await(w.FlushAsync());
            w.DetachStream();
            w.Dispose();
            ras.Seek(0);
            return ras;
        }

        public static byte[] ReadAll(WinStreams.IRandomAccessStream ras)
        {
            uint size = (uint)ras.Size;
            var data = new byte[size];
            if (size == 0) return data;
            using (var input = ras.GetInputStreamAt(0))
            {
                var r = new WinStreams.DataReader(input);
                Await((WinFoundation.IAsyncOperation<uint>)r.LoadAsync(size));
                r.ReadBytes(data);
                r.DetachStream();
                r.Dispose();
            }
            return data;
        }
    }

    /// 윈도우 PDF 엔진으로 쪽을 그림으로 그린다.
    public sealed class WinPdfDoc : IDisposable
    {
        WinPdf.PdfDocument doc;
        // 파일은 우리가 연 스트림으로 읽고, 다 쓰면 그 스트림을 바로 닫는다.
        // PdfDocument.LoadFromFileAsync는 파일을 메모리 정리 때까지 붙잡고 있어서(2026-11-08 실측),
        // 변환이 끝난 뒤에도 선생님이 그 PDF를 고쳐 저장하지 못할 수 있었다.
        WinStreams.IRandomAccessStream stream;
        public readonly int PageCount;

        public WinPdfDoc(string path) : this(path, null) { }

        /// password가 있으면 그 암호로 연다(암호 걸기 결과 확인에 쓴다).
        public WinPdfDoc(string path, string password)
        {
            string full = System.IO.Path.GetFullPath(path);
            try
            {
                WinStreams.IRandomAccessStream opened = null;
                try
                {
                    doc = WinRtCall.Mta(() =>
                    {
                        var file = WinRtCall.Await(WinStorage.StorageFile.GetFileFromPathAsync(full));
                        opened = WinRtCall.Await(file.OpenAsync(WinStorage.FileAccessMode.Read));
                        return WinRtCall.Await(password == null ? WinPdf.PdfDocument.LoadFromStreamAsync(opened) : WinPdf.PdfDocument.LoadFromStreamAsync(opened, password));
                    });
                    stream = opened;
                }
                catch
                {
                    Close(opened);
                    throw;
                }
            }
            catch (Exception ex)
            {
                if (ex.HResult == unchecked((int)0x8007052B)) throw new UserFacingException("암호가 걸린 PDF예요. 암호를 푼 뒤에 다시 해 주세요.");
                if (ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070020)) throw new UserFacingException("PDF를 열 수 없어요. 다른 프로그램이 쓰고 있는지 확인해 주세요.");
                Log.Write("윈도우 PDF 열기 실패: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                // 윈도우 엔진은 AES-256 암호 PDF를 '암호 필요'가 아닌 다른 오류로 돌려준다.
                if (LooksEncrypted(full)) throw new UserFacingException("암호가 걸린 PDF예요. 암호를 푼 뒤에 다시 해 주세요.");
                throw new UserFacingException("이 PDF를 열지 못했어요. 손상되었을 수 있어요.");
            }
            PageCount = (int)doc.PageCount;
        }

        static bool LooksEncrypted(string path)
        {
            try
            {
                return PdfDocument.Load(path).Encrypted;
            }
            catch
            {
                return false;
            }
        }

        /// 쪽(0부터)을 PNG로. 긴 변이 maxSide를 넘으면 줄인다.
        /// 화면에 보여 줄 그림(전후 비교, 쪽 편집 작은 그림)용: 윈도우 PDF 엔진이 화면 배율만큼 크게 그려
        /// (125% 화면이면 1.25배) 배율 높은 화면에서도 또렷하다. 파일로 남기는 그림·글자 인식·AI로 보내는 그림에는 RenderPngExact를 쓴다.
        public byte[] RenderPng(int index, double dpi, int maxSide)
        {
            var d = doc;
            return WinRtCall.Mta(() =>
            {
                using (var page = d.GetPage((uint)index))
                {
                    var size = page.Size;
                    double w = size.Width * dpi / 96.0, h = size.Height * dpi / 96.0;
                    double big = Math.Max(w, h);
                    if (big > maxSide)
                    {
                        w *= maxSide / big;
                        h *= maxSide / big;
                    }
                    var opts = new WinPdf.PdfPageRenderOptions();
                    opts.DestinationWidth = (uint)Math.Max(1, Math.Round(w));
                    opts.DestinationHeight = (uint)Math.Max(1, Math.Round(h));
                    using (var ras = new WinStreams.InMemoryRandomAccessStream())
                    {
                        WinRtCall.Await(page.RenderToStreamAsync(ras, opts));
                        return WinRtCall.ReadAll(ras);
                    }
                }
            });
        }

        /// 쪽 크기(1/96인치 단위, 쪽 돌림 반영)
        public void PageSizeDip(int index, out double width, out double height)
        {
            var d = doc;
            var size = WinRtCall.Mta(() =>
            {
                using (var page = d.GetPage((uint)index)) return page.Size;
            });
            width = size.Width;
            height = size.Height;
        }

        /// 윈도우 PDF 엔진이 요청한 크기에 곱하는 화면 배율(처음 그릴 때 재서 기억한다. 0이면 아직 모름)
        static double dipScale;

        /// RenderPng와 같지만 화면 배율과 상관없이 요청한 픽셀 크기로 그린다.
        /// 윈도우 PDF 엔진은 DestinationWidth/Height를 화면 배율 단위로 받아서, 125% 화면에서는 1.25배 큰 그림을 돌려준다
        /// (2026-11-12 실측: A4 200dpi 요청 1654×2339 → 2066×2920, 긴 변 제한 2560을 넘음).
        /// 나온 그림의 실제 크기를 PNG 머리에서 읽어 배율을 재고, 어긋났으면 그 배율로 나눠 한 번 더 그린다.
        /// 파일로 남기는 그림(그림으로, PDF→PPT, 그림 PPT, 영상 수업 자료), 글자 인식, AI로 보내는 쪽 그림은 이것을 쓴다.
        /// 2026-11-12 비교: 글자 인식은 이쪽이 같거나 나았고 더 빨랐다. AI 읽기는 긴 변 1600이 2000만큼 읽었다.
        public byte[] RenderPngExact(int index, double dpi, int maxSide)
        {
            var d = doc;
            return WinRtCall.Mta(() =>
            {
                using (var page = d.GetPage((uint)index))
                {
                    var size = page.Size;
                    double w = size.Width * dpi / 96.0, h = size.Height * dpi / 96.0;
                    double big = Math.Max(w, h);
                    if (big > maxSide)
                    {
                        w *= maxSide / big;
                        h *= maxSide / big;
                    }
                    double k = dipScale > 0 ? dipScale : 1;
                    uint dw = (uint)Math.Max(1, Math.Round(w / k));
                    byte[] png = Draw(page, dw, (uint)Math.Max(1, Math.Round(h / k)));
                    int gw, gh;
                    // 아주 작은 그림은 반올림 탓에 배율을 잘못 잴 수 있어 재지 않는다.
                    if (dw >= 200 && PngSize(png, out gw, out gh) && Math.Abs(gw - Math.Round(w)) > Math.Max(2, w * 0.01))
                    {
                        double s = gw / (double)dw;
                        if (s > 0.5 && s < 4)
                        {
                            dipScale = s;
                            png = Draw(page, (uint)Math.Max(1, Math.Round(w / s)), (uint)Math.Max(1, Math.Round(h / s)));
                        }
                    }
                    return png;
                }
            });
        }

        static byte[] Draw(WinPdf.PdfPage page, uint w, uint h)
        {
            var opts = new WinPdf.PdfPageRenderOptions();
            opts.DestinationWidth = w;
            opts.DestinationHeight = h;
            using (var ras = new WinStreams.InMemoryRandomAccessStream())
            {
                WinRtCall.Await(page.RenderToStreamAsync(ras, opts));
                return WinRtCall.ReadAll(ras);
            }
        }

        /// PNG 머리(IHDR)에 적힌 가로·세로
        internal static bool PngSize(byte[] png, out int w, out int h)
        {
            w = h = 0;
            if (png == null || png.Length < 24 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return false;
            w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return w > 0 && h > 0;
        }

        /// 다 쓴 문서를 놓아주고 파일 스트림을 닫는다. 닫지 않으면 메모리 정리 때까지 PDF 파일이 열린 채로 남아,
        /// 선생님이 그 파일을 다른 프로그램에서 고쳐 저장하려 할 때 '사용 중'이 될 수 있다(2026-11-08 실측).
        public void Dispose()
        {
            var d = doc;
            doc = null;
            var s = stream;
            stream = null;
            if (d != null)
            {
                try { System.Runtime.InteropServices.Marshal.ReleaseComObject(d); }
                catch (Exception ex) { Log.Write("PDF 문서 놓기 실패: " + ex.GetType().Name); }
            }
            Close(s);
        }

        static void Close(WinStreams.IRandomAccessStream s)
        {
            if (s == null) return;
            try { WinRtCall.Mta<object>(() => { s.Dispose(); return null; }); }
            catch (Exception ex) { Log.Write("PDF 파일 닫기 실패: " + ex.GetType().Name); }
        }
    }

    public sealed class OcrBox
    {
        public double X, Y, W, H;
        public string Text;
    }

    public sealed class OcrLine
    {
        public string Text;
        public readonly List<OcrBox> Words = new List<OcrBox>();
    }

    /// 윈도우 글자 인식(Windows.Media.Ocr).
    /// 엔진은 이 PC에 설치된 인식 언어(AvailableRecognizerLanguages) 가운데 한국어 → 윈도우 사용자 언어
    /// (TryCreateFromUserProfileLanguages) → 설치된 다른 언어 순으로 고른다. 선생님 문서는 대부분 한국어라서다.
    /// 문서대로, 맞는 언어가 없으면 엔진 만들기는 null을 돌려준다.
    public sealed class WinOcr : IDisposable
    {
        readonly WinOcrNs.OcrEngine engine;
        public readonly string LanguageTag;
        public readonly bool Korean;

        public WinOcr()
        {
            engine = WinRtCall.Mta<WinOcrNs.OcrEngine>(ChooseEngine);
            if (engine == null)
            {
                throw new UserFacingException("이 PC에 글자 인식 기능이 없어요. 윈도우 설정 → 시간 및 언어 → 언어 및 지역 → 한국어 ⋯ → 언어 옵션에서 '광학 문자 인식'을 설치해 주세요.");
            }
            var eng = engine;
            LanguageTag = WinRtCall.Mta(() => eng.RecognizerLanguage.LanguageTag);
            Korean = LanguageTag.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
        }

        static WinOcrNs.OcrEngine ChooseEngine()
        {
            var installed = new List<Windows.Globalization.Language>();
            try
            {
                foreach (var lang in WinOcrNs.OcrEngine.AvailableRecognizerLanguages) installed.Add(lang);
            }
            catch (Exception ex)
            {
                Log.Write("글자 인식 언어 목록을 읽지 못함: " + ex.GetType().Name);
            }
            var engine = EngineFor(installed.FirstOrDefault(l => l.LanguageTag.StartsWith("ko", StringComparison.OrdinalIgnoreCase)));
            if (engine != null) return engine;
            try
            {
                engine = WinOcrNs.OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch (Exception ex)
            {
                Log.Write("사용자 언어로 글자 인식 엔진을 만들지 못함: " + ex.GetType().Name);
            }
            if (engine != null) return engine;
            foreach (var lang in installed)
            {
                engine = EngineFor(lang);
                if (engine != null) return engine;
            }
            return null;
        }

        static WinOcrNs.OcrEngine EngineFor(Windows.Globalization.Language lang)
        {
            if (lang == null) return null;
            try
            {
                return WinOcrNs.OcrEngine.TryCreateFromLanguage(lang);
            }
            catch (Exception ex)
            {
                Log.Write("글자 인식 엔진을 만들지 못함: " + ex.GetType().Name);
                return null;
            }
        }

        /// 인코딩된 그림(PNG·JPG·BMP…)에서 글자를 읽어 줄·문단으로 돌려준다.
        /// 그림은 엔진이 받는 최대 크기(MaxImageDimension) 안에 들게 맞춘다.
        /// 글자가 작으면 키워서 한 번 더 읽는다. 496×701 그림에서 엉망이던 결과가 3배로 키우자 거의 맞았다(2026-11-07 실측).
        public string Read(byte[] encoded)
        {
            var eng = engine;
            return WinRtCall.Mta(() =>
            {
                using (var ras = WinRtCall.FromBytes(encoded))
                {
                    var dec = WinRtCall.Await(WinImaging.BitmapDecoder.CreateAsync(ras));
                    double ceiling = LargestScale(dec.PixelWidth, dec.PixelHeight);
                    double scale = Math.Min(1.0, ceiling);
                    var result = Recognize(eng, dec, scale);
                    double bigger = Math.Min(Enlargement(result, scale, dec.PixelWidth, dec.PixelHeight), ceiling);
                    if (bigger >= scale * 1.25)
                    {
                        var second = Recognize(eng, dec, bigger);
                        if (second.Lines.Count > 0) result = second;
                    }
                    return Arrange(result);
                }
            });
        }

        /// 글자와 함께 낱말 자리까지 돌려준다. 자리는 받은 그림의 픽셀 기준.
        public List<OcrLine> ReadLines(byte[] encoded)
        {
            var eng = engine;
            return WinRtCall.Mta(() =>
            {
                using (var ras = WinRtCall.FromBytes(encoded))
                {
                    var dec = WinRtCall.Await(WinImaging.BitmapDecoder.CreateAsync(ras));
                    double ceiling = LargestScale(dec.PixelWidth, dec.PixelHeight);
                    double scale = Math.Min(1.0, ceiling);
                    var result = Recognize(eng, dec, scale);
                    double bigger = Math.Min(Enlargement(result, scale, dec.PixelWidth, dec.PixelHeight), ceiling);
                    if (bigger >= scale * 1.25)
                    {
                        var second = Recognize(eng, dec, bigger);
                        if (second.Lines.Count > 0)
                        {
                            result = second;
                            scale = bigger;
                        }
                    }
                    var lines = new List<OcrLine>();
                    foreach (var line in result.Lines)
                    {
                        string text = (line.Text ?? "").Trim();
                        if (text.Length == 0) continue;
                        var o = new OcrLine { Text = text };
                        foreach (var w in line.Words)
                        {
                            var r = w.BoundingRect;
                            o.Words.Add(new OcrBox { X = r.X / scale, Y = r.Y / scale, W = r.Width / scale, H = r.Height / scale, Text = w.Text });
                        }
                        if (o.Words.Count > 0) lines.Add(o);
                    }
                    return lines;
                }
            });
        }

        /// 이 배율을 넘기면 가로나 세로가 엔진 최대 크기보다 커진다. 문서에 값이 없으니 0이면 제한이 없다고 본다.
        static double LargestScale(uint width, uint height)
        {
            double max = WinOcrNs.OcrEngine.MaxImageDimension;
            if (max <= 0) return double.MaxValue;
            return max / Math.Max(1u, Math.Max(width, height));
        }

        /// 낱말 높이(가운데값)가 이보다 작으면 키운다. 키울 때는 이 높이쯤 되게, 최대 3배까지.
        const double SmallText = 18, TargetText = 32, MaxUpscale = 3;

        /// 한 번 더 읽을 배율. 키울 필요가 없으면 0.
        static double Enlargement(WinOcrNs.OcrResult result, double scale, uint width, uint height)
        {
            double word = MedianWordHeight(result);
            if (word > 0 && word < SmallText) return Math.Min(scale * TargetText / word, MaxUpscale);
            if (word == 0 && Math.Max(width, height) <= 2000) return 2;
            return 0;
        }

        static WinOcrNs.OcrResult Recognize(WinOcrNs.OcrEngine eng, WinImaging.BitmapDecoder dec, double scale)
        {
            var transform = new WinImaging.BitmapTransform();
            if (Math.Abs(scale - 1) >= 0.01)
            {
                // 크기는 원본 기준(문서: Scale이 맨 먼저). 문서는 NearestNeighbor → Fant로 갈수록 품질이 좋다고 한다.
                // 줄일 때는 Fant, 키울 때는 2026-11-07 실측에서 잘 읽힌 Cubic을 쓴다.
                transform.ScaledWidth = (uint)Math.Max(1, Math.Floor(dec.PixelWidth * scale));
                transform.ScaledHeight = (uint)Math.Max(1, Math.Floor(dec.PixelHeight * scale));
                transform.InterpolationMode = scale < 1 ? WinImaging.BitmapInterpolationMode.Fant : WinImaging.BitmapInterpolationMode.Cubic;
            }
            // 휴대폰 사진은 EXIF 방향대로 세워서 읽는다.
            using (var bmp = WinRtCall.Await(dec.GetSoftwareBitmapAsync(WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Premultiplied, transform,
                WinImaging.ExifOrientationMode.RespectExifOrientation, WinImaging.ColorManagementMode.DoNotColorManage)))
            {
                return WinRtCall.Await(eng.RecognizeAsync(bmp));
            }
        }

        static double MedianWordHeight(WinOcrNs.OcrResult result)
        {
            var heights = new List<double>();
            foreach (var line in result.Lines)
            {
                foreach (var w in line.Words) heights.Add(w.BoundingRect.Height);
            }
            if (heights.Count == 0) return 0;
            heights.Sort();
            return heights[heights.Count / 2];
        }

        /// 줄 사이가 평균 줄 높이의 0.8배보다 벌어지면 문단을 나눈다.
        static string Arrange(WinOcrNs.OcrResult result)
        {
            var lines = new List<Tuple<double, double, string>>();
            foreach (var line in result.Lines)
            {
                double top = double.MaxValue, bottom = double.MinValue;
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    top = Math.Min(top, r.Y);
                    bottom = Math.Max(bottom, r.Y + r.Height);
                }
                string text = (line.Text ?? "").Trim();
                if (text.Length == 0) continue;
                if (top == double.MaxValue)
                {
                    top = 0;
                    bottom = 0;
                }
                lines.Add(Tuple.Create(top, bottom, text));
            }
            if (lines.Count == 0) return "";
            double avg = lines.Average(l => Math.Max(1, l.Item2 - l.Item1));
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    double gap = lines[i].Item1 - lines[i - 1].Item2;
                    sb.Append(gap > avg * 0.8 ? "\r\n\r\n" : "\r\n");
                }
                sb.Append(lines[i].Item3);
            }
            return sb.ToString();
        }

        public void Dispose() { }
    }
}
