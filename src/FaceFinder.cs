// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
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
            return Widen(boxes, w, h);
        }

        /// 찾은 상자(픽셀)를 머리카락·턱까지 넓혀 비율로
        static List<Rect> Widen(List<Rect> boxes, int w, int h)
        {
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

        /// 영상 장면을 여러 장 훑을 때: 얼굴 찾는 기계를 한 번만 만들어 계속 쓴다(MTA 스레드에서).
        public sealed class Session
        {
            WinFace.FaceDetector detector;

            /// 회색 그림(한 칸 1바이트, 위쪽 줄부터)에서 얼굴 자리(비율, 조금 넓힘)
            public List<Rect> Find(byte[] gray, int w, int h)
            {
                var boxes = WinRtCall.Mta(delegate
                {
                    if (detector == null)
                    {
                        if (!WinFace.FaceDetector.IsSupported) throw new UserFacingException("이 PC에서는 얼굴 찾기를 쓸 수 없어요. 화면을 끌어 직접 가려 주세요.");
                        detector = WinRtCall.Await(WinFace.FaceDetector.CreateAsync());
                        try { detector.MinDetectableFaceSize = new WinGraphics.BitmapSize { Width = 24, Height = 24 }; } catch { }
                    }
                    var writer = new WinStreams.DataWriter();
                    writer.WriteBytes(gray);
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
                return Widen(boxes, w, h);
            }
        }
    }
}
