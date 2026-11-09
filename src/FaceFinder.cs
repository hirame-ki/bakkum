// 얼굴 찾기. 윈도우에 든 Windows.Media.FaceAnalysis를 쓰므로 사진은 이 PC 밖으로 나가지 않는다.
// 완벽하지 않다. 옆모습·가려진 얼굴·아주 작은 얼굴은 놓칠 수 있어, 화면에서 사용자가 꼭 확인하게 한다.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using WinFace = Windows.Media.FaceAnalysis;
using WinGraphics = Windows.Graphics.Imaging;
using WinStreams = Windows.Storage.Streams;

namespace Bakkum
{
    public static class FaceFinder
    {
        const int LongSide = 2600;

        /// 얼굴 자리들(사진 크기에 대한 비율). 머리카락과 턱까지 덮이게 조금 넓혀서 돌려준다.
        public static List<Rect> Find(BitmapSource src)
        {
            var gray = Imaging.ToGray8(Imaging.Fit(src, LongSide));
            int w = gray.PixelWidth, h = gray.PixelHeight;
            var px = new byte[(long)w * h];
            gray.CopyPixels(px, w, 0);
            var boxes = WinRtCall.Mta(delegate
            {
                if (!WinFace.FaceDetector.IsSupported) throw new UserFacingException("이 PC에서는 얼굴 찾기를 쓸 수 없어요. 드래그해서 직접 가려 주세요.");
                var detector = WinRtCall.Await(WinFace.FaceDetector.CreateAsync());
                var size = new WinGraphics.BitmapSize { Width = 28, Height = 28 };
                try { detector.MinDetectableFaceSize = size; } catch { }
                var writer = new WinStreams.DataWriter();
                writer.WriteBytes(px);
                var buffer = writer.DetachBuffer();
                writer.Dispose();
                var found = new List<Rect>();
                using (var bmp = WinGraphics.SoftwareBitmap.CreateCopyFromBuffer(buffer, WinGraphics.BitmapPixelFormat.Gray8, w, h))
                {
                    foreach (var face in WinRtCall.Await(detector.DetectFacesAsync(bmp)))
                    {
                        var b = face.FaceBox;
                        found.Add(new Rect(b.X, b.Y, b.Width, b.Height));
                    }
                }
                return found;
            });
            var result = new List<Rect>();
            foreach (var b in boxes)
            {
                double mx = b.Width * 0.14, top = b.Height * 0.30, bottom = b.Height * 0.14;
                double x0 = Math.Max(0, b.X - mx), y0 = Math.Max(0, b.Y - top);
                double x1 = Math.Min(w, b.Right + mx), y1 = Math.Min(h, b.Bottom + bottom);
                result.Add(new Rect(x0 / w, y0 / h, (x1 - x0) / w, (y1 - y0) / h));
            }
            return result;
        }
    }
}
