// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 영상 편집 내용(프로젝트): 본 영상 줄(조각들), 그 위에 얹는 것(글자·그림·스티커·도형·작은 화면), 소리, 표시.
// 시간 계산, 저장(JSON), 되돌리기, 자동 저장을 맡는다.
// 얹은 것과 목소리는 '본 영상의 어느 순간'에 붙어 있어서, 자르거나 빠르기를 바꾸거나 순서를 바꾸면 그 순간을 따라 움직인다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Bakkum
{
    /// 움직임 표시(키프레임). T는 그것이 붙은 것의 시간(조각은 원본 시간, 얹은 것은 시작에서 지난 시간).
    public sealed class VKey
    {
        public double T;
        public double X, Y, W, H;
        public double Zoom = 1, PanX, PanY;
        public double Opacity = 1;
        /// 얹은 것의 기울기(도). HasRot이 false인 예전 표시는 얹은 것의 기울기(VItem.Rot)를 그대로 쓴다.
        public double Rot;
        public bool HasRot;

        public VKey Copy()
        {
            return (VKey)MemberwiseClone();
        }
    }

    /// 가리기(흐리게·모자이크·색 칸). 조각의 원본 화면 기준 자리(0~1, 돌리기 전)와 원본 시간.
    public sealed class VRedact
    {
        public string Id = VProject.NewId();
        public double Start, End;
        public double X, Y, W, H;
        public string Mode = "blur";
        public double Strength = 0.6;
        public bool Oval;
        public uint Rgb = 0x22252F;
        /// 얼굴을 찾아 저절로 만든 것
        public bool Face;
        /// 사용자가 '이 얼굴은 보이게'로 끈 것
        public bool Off;
        public List<VKey> Keys = new List<VKey>();

        public VRedact Copy()
        {
            var r = (VRedact)MemberwiseClone();
            r.Keys = Keys.Select(k => k.Copy()).ToList();
            return r;
        }
    }

    /// 본 영상 줄의 조각 하나
    public sealed class VClip
    {
        public string Id = VProject.NewId();
        /// 나누기 전의 같은 조각에서 나온 조각끼리 같은 값(얹은 것의 자리를 다시 찾을 때 씀)
        public string Line;
        /// video · photo · color · freeze
        public string Kind = "video";
        public string Path;
        public double In, Out;
        /// 사진·색·멈춘 화면의 길이
        public double Dur = 4;
        /// 멈춘 화면이 보여 줄 원본 시각
        public double FreezeAt;
        public uint Rgb = 0x22252F;
        public double Speed = 1;
        /// 느리게 할 때 앞뒤 장면을 섞어 부드럽게
        public bool Smooth;
        public bool Reverse;

        // 소리
        public double Volume = 1;
        public bool Mute;
        public double FadeIn, FadeOut;
        public double Denoise;
        /// 목소리 높낮이(반음, -6~+6). 학생 목소리를 알아보기 어렵게.
        public double Pitch;

        // 화면에 놓기
        /// fit(전체 보이기) · fill(꽉 채우기)
        public string Fit = "fit";
        public double Zoom = 1, PanX, PanY;
        public List<VKey> Keys = new List<VKey>();
        public int Rotate;
        public bool FlipH, FlipV;
        public double CropX, CropY, CropW = 1, CropH = 1;
        public bool Stabilize;

        // 색
        public double Bright, Contrast, Sat, Warm, Sharp;
        public string Filter = "";

        // 앞 조각에서 넘어오는 전환
        public string Trans = "none";
        public double TransSec = 0.6;

        /// 사진: 천천히 다가가기 none · in · out · left · right
        public string Ken = "in";

        public List<VRedact> Redacts = new List<VRedact>();

        public bool IsVideo
        {
            get { return Kind == "video"; }
        }

        /// 원본 시간이 흐르는 조각(영상)인지. 사진·색·멈춤은 원본 시간 대신 지난 시간을 쓴다.
        public double Length
        {
            get
            {
                if (Kind != "video") return Math.Max(0.1, Dur);
                return Math.Max(0.05, (Out - In) / Math.Max(0.05, Speed));
            }
        }

        public bool HasColor
        {
            get { return Bright != 0 || Contrast != 0 || Sat != 0 || Warm != 0 || Sharp != 0 || !string.IsNullOrEmpty(Filter); }
        }

        public bool HasCrop
        {
            get { return CropX > 0.0005 || CropY > 0.0005 || CropW < 0.9995 || CropH < 0.9995; }
        }

        /// 조각 안에서 지난 시간 → 원본 시간
        public double SourceAt(double local)
        {
            if (Kind == "freeze") return FreezeAt;
            if (Kind != "video") return local;
            double s = Reverse ? Out - local * Speed : In + local * Speed;
            return Math.Max(In, Math.Min(Out, s));
        }

        /// 원본 시간 → 조각 안에서 지난 시간. 조각 밖이면 false.
        public bool LocalOf(double source, out double local)
        {
            local = 0;
            if (Kind != "video")
            {
                if (source < -1e-6 || source > Length + 1e-6) return false;
                local = Math.Max(0, Math.Min(Length, source));
                return true;
            }
            if (source < In - 1e-6 || source > Out + 1e-6) return false;
            local = Reverse ? (Out - source) / Speed : (source - In) / Speed;
            local = Math.Max(0, Math.Min(Length, local));
            return true;
        }

        public VClip Copy()
        {
            var c = (VClip)MemberwiseClone();
            c.Keys = Keys.Select(k => k.Copy()).ToList();
            c.Redacts = Redacts.Select(r => r.Copy()).ToList();
            return c;
        }

        /// 다른 영상에도 똑같이: 화면에 놓기·색·소리 다듬기만 옮긴다(자르기·가리기는 영상마다 달라서 옮기지 않는다).
        public void TakeLookFrom(VClip o)
        {
            Fit = o.Fit;
            Zoom = o.Zoom;
            PanX = o.PanX;
            PanY = o.PanY;
            Rotate = o.Rotate;
            FlipH = o.FlipH;
            FlipV = o.FlipV;
            Bright = o.Bright;
            Contrast = o.Contrast;
            Sat = o.Sat;
            Warm = o.Warm;
            Sharp = o.Sharp;
            Filter = o.Filter;
            Volume = o.Volume;
            Mute = o.Mute;
            Denoise = o.Denoise;
            Pitch = o.Pitch;
            Stabilize = o.Stabilize;
        }
    }

    /// 화면 위에 얹는 것: 글자(자막·제목) · 그림 · 스티커 · 도형 · 작은 화면(영상)
    public sealed class VItem
    {
        public string Id = VProject.NewId();
        /// text · image · sticker · shape · pip
        public string Kind = "text";
        /// 자막 줄에 놓이는 글자
        public bool Subtitle;
        public double Start, End;
        /// 화면에 대한 비율(가운데 자리, 너비·높이). 글자는 W가 한 줄의 가장 긴 너비.
        public double X = 0.5, Y = 0.5, W = 0.3, H = 0.2;
        public double Rot;
        public double Opacity = 1;
        public List<VKey> Keys = new List<VKey>();
        /// 나타날 때: none · fade · pop · rise · type · slide   사라질 때: none · fade
        public string In = "fade", Out = "fade";
        public double AnimSec = 0.3;

        // 글자
        public string Text = "";
        public string Font = "맑은 고딕";
        /// 화면 짧은 변에 대한 글자 크기
        public double Size = 0.055;
        public bool Bold = true;
        public uint Rgb = 0xFFFFFF;
        /// 테두리 굵기(글자 크기에 대한 비율, 0이면 없음)
        public double Outline = 0.14;
        public uint OutlineRgb = 0x15161A;
        public bool Shadow;
        public bool Box;
        public uint BoxRgb = 0x15161A;
        public double BoxAlpha = 0.62;
        /// left · center · right
        public string Align = "center";
        /// 다른 색으로 강조할 낱말(쉼표로 여럿)
        public string Emph = "";
        public uint EmphRgb = 0xFFD447;

        // 그림·스티커·도형
        public string Path;
        public string Sticker;
        /// arrow · box · circle · marker · line
        public string Shape;
        public double Thick = 0.012;
        public double Corner;

        // 작은 화면(영상)
        public double SrcIn, SrcOut;
        public double Volume = 1;
        public bool Mute = true;
        public bool Circle;
        public bool Border = true;
        public uint BorderRgb = 0xFFFFFF;
        public bool Chroma;
        public uint ChromaRgb = 0x00B140;
        public double ChromaTol = 0.35;

        /// 사용자가 붙인 이름(내 꾸밈 저장용)
        public string Name;

        public double Length
        {
            get { return Math.Max(0.05, End - Start); }
        }

        public VItem Copy()
        {
            var i = (VItem)MemberwiseClone();
            i.Keys = Keys.Select(k => k.Copy()).ToList();
            return i;
        }

        public void TakeStyleFrom(VItem o)
        {
            Font = o.Font;
            Size = o.Size;
            Bold = o.Bold;
            Rgb = o.Rgb;
            Outline = o.Outline;
            OutlineRgb = o.OutlineRgb;
            Shadow = o.Shadow;
            Box = o.Box;
            BoxRgb = o.BoxRgb;
            BoxAlpha = o.BoxAlpha;
            Align = o.Align;
            EmphRgb = o.EmphRgb;
            In = o.In;
            Out = o.Out;
            AnimSec = o.AnimSec;
        }
    }

    /// 따로 넣는 소리: 배경 음악 · 내 목소리(녹음) · 읽어 주기(AI 목소리) · 효과음
    public sealed class VSound
    {
        public string Id = VProject.NewId();
        /// music · record · voice · effect · file
        public string Kind = "music";
        public string Path;
        public double Start;
        public double In, Out;
        public double Volume = 1;
        public double FadeIn, FadeOut;
        /// 배경 음악을 영상 끝까지 되풀이
        public bool Loop;
        /// 읽어 주기: 읽은 글과 그 자막
        public string Text;
        /// 읽어 주기: 목소리와 빠르기(소리 파일이 없으면 이것으로 다시 만든다. 로밍 프로필로 다른 PC에서 열 때)
        public string Voice;
        public double Rate = 1;
        public string ItemId;
        public string Name;

        public double Length
        {
            get { return Math.Max(0.05, Out - In); }
        }

        public bool IsMusic
        {
            get { return Kind == "music"; }
        }

        public VSound Copy()
        {
            return (VSound)MemberwiseClone();
        }
    }

    public sealed class VMarker
    {
        public string Id = VProject.NewId();
        public double T;
        public string Label = "";
    }

    /// 내보내기 설정
    public sealed class VExport
    {
        /// same · reels · youtube · kakao · size · audio · cover
        public string Preset = "same";
        /// 짧은 변(0이면 원본 크기)
        public int Height;
        /// 초당 장 수(0이면 원본과 같게)
        public double Fps;
        /// 목표 용량(MB). size일 때만.
        public double TargetMb = 25;
        public bool RangeOnly;
        public double RangeStart, RangeEnd;
        /// 소리만: m4a · mp3 · wav
        public string AudioFormat = "m4a";
        public double CoverAt;
        public bool KeepLocation;
    }

    public sealed class VProject
    {
        public int Version = 1;
        /// source · 16:9 · 9:16 · 1:1 · 4:5 · 4:3 · 3:4
        public string Aspect = "source";
        /// 비율이 '원본'일 때의 원본 크기(첫 조각)
        public int SourceW = 1920, SourceH = 1080;
        public double Fps = 30;
        /// 빈 곳: blur · black · color
        public string Back = "blur";
        public uint BackRgb = 0x22252F;
        public List<VClip> Clips = new List<VClip>();
        public List<VItem> Items = new List<VItem>();
        public List<VSound> Sounds = new List<VSound>();
        public List<VMarker> Markers = new List<VMarker>();
        /// 말소리가 날 때 배경 음악 줄이기
        public bool Duck = true;
        public double DuckTo = 0.3;
        /// 조각·소리마다 크기를 비슷하게
        public bool Level;
        /// 끝에서 서서히 어둡게·조용히
        public bool EndFade;
        public VExport Export = new VExport();
        /// 처음 연 파일들(자동 저장 이름)
        public List<string> Opened = new List<string>();
        /// 처음 연 파일들의 내용 지문(Opened와 같은 차례). 영상을 옮기거나 이름을 바꿔도 자동 저장을 찾는다.
        public List<string> Prints = new List<string>();
        public DateTime Saved;

        static int counter;

        public static string NewId()
        {
            return DateTime.UtcNow.Ticks.ToString("x") + "-" + System.Threading.Interlocked.Increment(ref counter).ToString("x");
        }

        // ── 크기 ──

        public static double AspectValue(string aspect, int sw, int sh)
        {
            switch (aspect)
            {
                case "16:9": return 16.0 / 9;
                case "9:16": return 9.0 / 16;
                case "1:1": return 1;
                case "4:5": return 4.0 / 5;
                case "4:3": return 4.0 / 3;
                case "3:4": return 3.0 / 4;
                default: return sh > 0 ? (double)sw / sh : 16.0 / 9;
            }
        }

        public double AspectRatio
        {
            get { return AspectValue(Aspect, SourceW, SourceH); }
        }

        /// 짧은 변이 shortSide인 화면 크기(짝수)
        public void CanvasSize(int shortSide, out int w, out int h)
        {
            double a = AspectRatio;
            if (a >= 1)
            {
                h = shortSide;
                w = (int)Math.Round(shortSide * a);
            }
            else
            {
                w = shortSide;
                h = (int)Math.Round(shortSide / a);
            }
            w = Math.Max(2, w & ~1);
            h = Math.Max(2, h & ~1);
        }

        /// 긴 변이 longSide인 화면 크기(짝수)
        public void CanvasSizeLong(int longSide, out int w, out int h)
        {
            double a = AspectRatio;
            if (a >= 1)
            {
                w = longSide;
                h = (int)Math.Round(longSide / a);
            }
            else
            {
                h = longSide;
                w = (int)Math.Round(longSide * a);
            }
            w = Math.Max(2, w & ~1);
            h = Math.Max(2, h & ~1);
        }

        // ── 시간 ──

        public double Duration
        {
            get
            {
                double d = 0;
                foreach (var c in Clips) d += c.Length;
                return d;
            }
        }

        public double StartOf(int index)
        {
            double t = 0;
            for (int i = 0; i < index && i < Clips.Count; i++) t += Clips[i].Length;
            return t;
        }

        public double StartOf(VClip c)
        {
            return StartOf(Clips.IndexOf(c));
        }

        /// 이 시각의 조각(끝 시각은 다음 조각). 없으면 -1.
        public int ClipAt(double t, out double local)
        {
            local = 0;
            double s = 0;
            for (int i = 0; i < Clips.Count; i++)
            {
                double len = Clips[i].Length;
                if (t < s + len - 1e-9 || i == Clips.Count - 1)
                {
                    local = Math.Max(0, Math.Min(len, t - s));
                    return t >= -1e-9 ? i : -1;
                }
                s += len;
            }
            return -1;
        }

        public VClip FindClip(string id)
        {
            return Clips.FirstOrDefault(c => c.Id == id);
        }

        public VItem FindItem(string id)
        {
            return Items.FirstOrDefault(i => i.Id == id);
        }

        // ── 얹은 것이 본 영상을 따라 움직이기 ──

        sealed class Anchor
        {
            public string Line;
            public string ClipId;
            public double Source;
            public bool Ok;
        }

        Anchor AnchorOf(double t)
        {
            double local;
            int i = ClipAt(t, out local);
            if (i < 0) return new Anchor();
            var c = Clips[i];
            return new Anchor { Line = c.Line ?? c.Id, ClipId = c.Id, Source = c.SourceAt(local), Ok = true };
        }

        /// 편집 뒤 같은 원본 순간이 놓인 시각. 지워졌으면 false.
        bool Resolve(Anchor a, out double t)
        {
            t = 0;
            if (!a.Ok) return false;
            double s = 0;
            // 같은 조각이 있으면 그 조각, 나뉘었으면 같은 줄기에서 원본 시간이 맞는 조각
            foreach (var pass in new[] { 0, 1 })
            {
                s = 0;
                foreach (var c in Clips)
                {
                    bool match = pass == 0 ? c.Id == a.ClipId : (c.Line ?? c.Id) == a.Line;
                    double local;
                    if (match && c.LocalOf(a.Source, out local))
                    {
                        t = s + local;
                        return true;
                    }
                    s += c.Length;
                }
            }
            return false;
        }

        /// 본 영상 줄을 바꾸기 전에 부르고, 바꾼 뒤 돌려받은 동작을 부르면 얹은 것·목소리·표시가 따라 움직인다.
        public Action Follow()
        {
            double before = Duration;
            var items = Items.Select(i => new { I = i, A = AnchorOf(i.Start), B = AnchorOf(Math.Max(i.Start, i.End - 0.02)), Len = i.Length, EndAtEnd = i.End >= before - 0.02 }).ToList();
            var sounds = Sounds.Where(x => !x.IsMusic).Select(x => new { S = x, A = AnchorOf(x.Start) }).ToList();
            var marks = Markers.Select(m => new { M = m, A = AnchorOf(m.T) }).ToList();
            return delegate
            {
                double after = Duration;
                foreach (var x in items)
                {
                    double t0, t1;
                    bool a = Resolve(x.A, out t0), b = Resolve(x.B, out t1);
                    if (!a && !b)
                    {
                        Items.Remove(x.I);
                        continue;
                    }
                    if (!a) t0 = Math.Max(0, t1 - x.Len);
                    if (!b || t1 <= t0) t1 = t0 + x.Len;
                    else t1 += 0.02;
                    if (x.EndAtEnd) t1 = Math.Max(t1, after);
                    x.I.Start = Math.Max(0, t0);
                    x.I.End = Math.Min(Math.Max(after, x.I.Start + 0.1), Math.Max(x.I.Start + 0.1, t1));
                }
                foreach (var x in sounds)
                {
                    double t;
                    if (Resolve(x.A, out t)) x.S.Start = t;
                    else Sounds.Remove(x.S);
                }
                foreach (var x in marks)
                {
                    double t;
                    if (Resolve(x.A, out t)) x.M.T = t;
                    else Markers.Remove(x.M);
                }
                // 영상보다 뒤로 밀려난 것 정리
                Items.RemoveAll(i => i.Start >= after - 0.02 && after > 0);
                foreach (var i in Items) if (i.End > after) i.End = after;
            };
        }

        // ── 본 영상 줄 편집 ──

        /// t에서 조각을 둘로 나눈다. 나뉘었으면 뒤 조각.
        public VClip Split(double t)
        {
            double local;
            int i = ClipAt(t, out local);
            if (i < 0) return null;
            var c = Clips[i];
            if (local < 0.04 || local > c.Length - 0.04) return null;
            var b = c.Copy();
            b.Id = NewId();
            if (c.Line == null) c.Line = c.Id;
            b.Line = c.Line;
            if (c.Kind == "video")
            {
                double s = c.SourceAt(local);
                if (c.Reverse)
                {
                    b.Out = s;
                    c.In = s;
                }
                else
                {
                    c.Out = s;
                    b.In = s;
                }
            }
            else
            {
                b.Dur = c.Dur - local;
                c.Dur = local;
            }
            b.Trans = "none";
            Clips.Insert(i + 1, b);
            return b;
        }

        /// 조각 지우기(뒤 조각이 앞으로 붙는다)
        public void RemoveClip(VClip c)
        {
            Clips.Remove(c);
            if (Clips.Count > 0 && Clips[0].Trans != "none" && Clips[0].Trans != "black" && Clips[0].Trans != "white") Clips[0].Trans = "none";
        }

        /// 구간 [a, b) 잘라 내기(여러 조각에 걸쳐도 된다)
        public void Cut(double a, double b)
        {
            if (b - a < 0.02) return;
            Split(b);
            Split(a);
            double s = 0;
            var gone = new List<VClip>();
            foreach (var c in Clips)
            {
                double mid = s + c.Length / 2;
                if (mid > a && mid < b) gone.Add(c);
                s += c.Length;
            }
            foreach (var c in gone) RemoveClip(c);
        }

        // ── 저장 ──

        static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 64 };
        }

        public string ToJson()
        {
            return Json().Serialize(this);
        }

        public static VProject FromJson(string json)
        {
            var p = Json().Deserialize<VProject>(json);
            if (p.Clips == null) p.Clips = new List<VClip>();
            if (p.Items == null) p.Items = new List<VItem>();
            if (p.Sounds == null) p.Sounds = new List<VSound>();
            if (p.Markers == null) p.Markers = new List<VMarker>();
            if (p.Export == null) p.Export = new VExport();
            if (p.Opened == null) p.Opened = new List<string>();
            if (p.Prints == null) p.Prints = new List<string>();
            foreach (var c in p.Clips)
            {
                if (c.Keys == null) c.Keys = new List<VKey>();
                if (c.Redacts == null) c.Redacts = new List<VRedact>();
                foreach (var r in c.Redacts) if (r.Keys == null) r.Keys = new List<VKey>();
            }
            foreach (var i in p.Items) if (i.Keys == null) i.Keys = new List<VKey>();
            return p;
        }

        public VProject Clone()
        {
            return FromJson(ToJson());
        }

        /// 쓰는 파일 전체(원본 영상·사진·소리)
        public IEnumerable<string> Files()
        {
            foreach (var c in Clips) if (!string.IsNullOrEmpty(c.Path)) yield return c.Path;
            foreach (var i in Items) if (!string.IsNullOrEmpty(i.Path)) yield return i.Path;
            foreach (var s in Sounds) if (!string.IsNullOrEmpty(s.Path)) yield return s.Path;
        }

        /// 원본 파일이 옮겨졌을 때 새 자리로 바꾼다
        public int Relink(string oldPath, string newPath)
        {
            int n = 0;
            foreach (var c in Clips) if (string.Equals(c.Path, oldPath, StringComparison.OrdinalIgnoreCase)) { c.Path = newPath; n++; }
            foreach (var i in Items) if (string.Equals(i.Path, oldPath, StringComparison.OrdinalIgnoreCase)) { i.Path = newPath; n++; }
            foreach (var s in Sounds) if (string.Equals(s.Path, oldPath, StringComparison.OrdinalIgnoreCase)) { s.Path = newPath; n++; }
            return n;
        }

        // ── 시간 표시 ──

        public static string Clock(double t, bool tenths)
        {
            if (t < 0) t = 0;
            int whole = (int)Math.Floor(t + (tenths ? 0 : 0.5));
            int h = whole / 3600, m = whole / 60 % 60, s = whole % 60;
            string head = h > 0 ? h + ":" + m.ToString("00") + ":" + s.ToString("00") : m + ":" + s.ToString("00");
            if (!tenths) return head;
            int tenth = (int)Math.Floor((t - whole) * 10 + 1e-6);
            return head + "." + Math.Max(0, Math.Min(9, tenth));
        }
    }

    /// 되돌리기: 바꾸기 전 모습을 통째로(JSON) 쌓아 둔다
    public sealed class VHistory
    {
        readonly List<string> undo = new List<string>();
        readonly List<string> redo = new List<string>();
        const int Most = 400;

        public bool CanUndo
        {
            get { return undo.Count > 0; }
        }

        public bool CanRedo
        {
            get { return redo.Count > 0; }
        }

        public int Count
        {
            get { return undo.Count; }
        }

        public void Push(string before)
        {
            if (undo.Count > 0 && undo[undo.Count - 1] == before) return;
            undo.Add(before);
            if (undo.Count > Most) undo.RemoveAt(0);
            redo.Clear();
        }

        public string Undo(string now)
        {
            if (undo.Count == 0) return null;
            string s = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            redo.Add(now);
            return s;
        }

        public string Redo(string now)
        {
            if (redo.Count == 0) return null;
            string s = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            undo.Add(now);
            return s;
        }
    }

    /// 자동 저장: 처음 연 파일들마다 %APPDATA%\Bakkum\video 안에 하나
    public static class VAutoSave
    {
        public static string Folder
        {
            get
            {
                string d = Path.Combine(AppInfo.DataDir, "video");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string KeyOf(IEnumerable<string> files)
        {
            var joined = string.Join("|", files.Select(f => Path.GetFullPath(f).ToLowerInvariant()).OrderBy(f => f, StringComparer.Ordinal));
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(joined));
                return BitConverter.ToString(h, 0, 10).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string PathOf(IEnumerable<string> files)
        {
            return Path.Combine(Folder, KeyOf(files) + ".json");
        }

        public static void Save(VProject p)
        {
            if (p.Opened == null || p.Opened.Count == 0) return;
            p.Saved = DateTime.Now;
            string path = PathOf(p.Opened);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, p.ToJson(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// 이어서 할 내용이 있으면 읽어 준다(망가졌으면 null)
        public static VProject Load(IEnumerable<string> files)
        {
            string path = PathOf(files);
            if (!File.Exists(path)) return null;
            try
            {
                var p = VProject.FromJson(File.ReadAllText(path, Encoding.UTF8));
                return p.Clips.Count > 0 ? p : null;
            }
            catch (Exception ex)
            {
                Log.Write("영상 편집 자동 저장 읽기 실패: " + ex.GetType().Name);
                return null;
            }
        }

        /// 파일 내용 지문(미리 보기 사본과 같은 방식: 크기와 앞뒤 조각). 읽지 못하면 null.
        public static List<string> PrintsOf(List<string> files)
        {
            try
            {
                return files.Select(CursorLog.Fingerprint).ToList();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool SamePrints(List<string> a, List<string> b)
        {
            return a != null && b != null && a.Count > 0 && a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x));
        }

        /// 이 파일들의 자동 저장: 경로로 찾은 것과, 원본이 옮겨져 그 자리에 없는 자동 저장 가운데 내용 지문이 같은 것 중
        /// 가장 최근에 저장한 것. 복사본(원본이 그 자리에 있는 것)의 편집은 따로 하는 것으로 보고 고르지 않는다.
        /// 다른 자동 저장에서 찾았으면 옛 자리를 지금 자리로 바꾸고 from에 그 파일을 알려 준다(이어서 하면 Resumed로 지운다).
        /// ponytail: 열 때마다 자동 저장을 모두 훑는다(60일 치라 수십 개). 많아지면 지문 → 파일 이름 목록을 따로 둔다.
        public static VProject Find(List<string> files, List<string> prints, out string from)
        {
            string own = PathOf(files);
            var found = Load(files);
            from = found != null ? own : null;
            if (prints == null) return found;
            foreach (var f in Directory.GetFiles(Folder, "*.json"))
            {
                if (string.Equals(f, own, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string text = File.ReadAllText(f, Encoding.UTF8);
                    if (!prints.All(x => text.Contains(x))) continue;
                    var p = VProject.FromJson(text);
                    if (p.Clips.Count == 0 || p.Prints.Count != p.Opened.Count || !SamePrints(p.Prints, prints) || p.Opened.All(File.Exists)) continue;
                    if (found != null && p.Saved <= found.Saved) continue;
                    found = p;
                    from = f;
                }
                catch (Exception ex)
                {
                    Log.Write("영상 편집 자동 저장 찾기 실패: " + ex.GetType().Name);
                }
            }
            if (found == null || from == own) return found;
            for (int i = 0; i < found.Opened.Count; i++) found.Relink(found.Opened[i], files[prints.IndexOf(found.Prints[i])]);
            found.Opened = files.ToList();
            found.Prints = prints.ToList();
            return found;
        }

        /// 다른 자리의 자동 저장(from)을 이어서 하면: 지금 자리로 바로 저장하고 옛 것은 지운다(편집이 두 갈래로 나뉘지 않게)
        public static void Resumed(VProject p, string from)
        {
            if (from == null || string.Equals(from, PathOf(p.Opened), StringComparison.OrdinalIgnoreCase)) return;
            Save(p);
            try { File.Delete(from); } catch { }
        }

        public static void Forget(IEnumerable<string> files)
        {
            try { File.Delete(PathOf(files)); } catch { }
        }

        static int tidied;

        /// 자동 저장 파일들. 같은 폴더의 내 꾸밈(styles.json)은 빼서, 60일 넘게 안 고친 꾸밈을 지우지 않는다.
        static IEnumerable<string> Saves()
        {
            return Directory.GetFiles(Folder, "*.json").Where(f => !string.Equals(Path.GetFileName(f), "styles.json", StringComparison.OrdinalIgnoreCase));
        }

        /// 오래된 자동 저장(60일)과 다시 만들 수 있는 파일(사본·푼 소리·읽어 주기) 정리. 바꿈을 켤 때마다 한 번만(첫 영상 편집 때).
        /// 파일 날짜만 보는 것은 바로 하고(편집 창이 열리기 전에 끝나야 열면서 쓰는 파일과 엇갈리지 않는다),
        /// 자동 저장을 모두 읽어야 하는 읽어 주기·녹음 정리는 뒤 스레드에서 한다.
        public static void Tidy()
        {
            if (Interlocked.Exchange(ref tidied, 1) == 1) return;
            TidyNow(false);
        }

        /// wait: 시험용(뒤 스레드 일을 이 자리에서 끝까지)
        internal static void TidyNow(bool wait)
        {
            try
            {
                foreach (var f in Saves())
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-60)) File.Delete(f);
                // 화면 녹화의 마우스·웹캠 기록, 수업 자료 쪽 그림은 더 오래(120일) 둔다
                foreach (var sub in new[] { "cursor", "cam", "lecture" })
                {
                    string d = Path.Combine(Folder, sub);
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d))
                        if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-120)) File.Delete(f);
                    foreach (var dd in Directory.GetDirectories(d))
                        if (Directory.GetLastWriteTime(dd) < DateTime.Now.AddDays(-120)) Directory.Delete(dd, true);
                }
                // 미리 보기 사본은 커서 30일 안 쓰면 지운다(쓸 때마다 날짜가 새로워진다). 만들다 멈춘 임시 파일은 하루 뒤.
                string pd = Path.Combine(AppInfo.CacheDir, "proxy");
                if (Directory.Exists(pd))
                {
                    foreach (var f in Directory.GetFiles(pd))
                    {
                        bool half = f.EndsWith(".tmp.mp4", StringComparison.OrdinalIgnoreCase);
                        try
                        {
                            if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(half ? -1 : -30)) File.Delete(f);
                        }
                        catch { }
                    }
                }
                // 푼 소리·다듬은 소리는 크고 다시 만들 수 있어서 7일 안 쓰면 지운다(쓸 때마다 날짜가 새로워진다)
                DeleteOld(Path.Combine(AppInfo.CacheDir, "vaudio"), 7, new HashSet<string>(AudioCache.InUse().Concat(VAudioProc.InUse()), StringComparer.OrdinalIgnoreCase));
            }
            catch { }
            var th = new Thread(TidySounds) { IsBackground = true, Name = "자동 저장 정리", Priority = ThreadPriority.BelowNormal };
            th.Start();
            if (wait) th.Join();
        }

        /// 읽어 주기·내 목소리 녹음은 남아 있는 자동 저장이 쓰면 남긴다. 안 쓰는 읽어 주기는 30일, 녹음은 사용자 자료라 60일 뒤에 지운다.
        /// 자동 저장을 하나라도 못 읽으면(망가졌거나 막 저장하는 중) 그 편집이 쓰는 것을 모르니 이번에는 지우지 않는다.
        static void TidySounds()
        {
            try
            {
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool all = true;
                foreach (var f in Saves())
                {
                    try
                    {
                        foreach (var used in VProject.FromJson(File.ReadAllText(f, Encoding.UTF8)).Files()) keep.Add(Path.GetFullPath(used));
                    }
                    catch (Exception ex)
                    {
                        all = false;
                        Log.Write("자동 저장 정리: 읽지 못함 " + ex.GetType().Name);
                    }
                }
                if (all)
                {
                    DeleteOld(Path.Combine(AppInfo.CacheDir, "voice"), 30, keep);
                    DeleteOld(Path.Combine(Folder, "voice"), 30, keep);
                    DeleteOld(Path.Combine(Folder, "rec"), 60, keep);
                }
                // 0.3.7까지 임시 폴더에 두던 푼 소리(시험 중에는 진짜 임시 폴더를 건드리지 않는다)
                if (AppInfo.DataDirOverride == null) Util.TryDeleteDir(Path.Combine(Path.GetTempPath(), AppInfo.Id, "vaudio"));
            }
            catch { }
        }

        static void DeleteOld(string dir, int days, HashSet<string> keep)
        {
            if (!Directory.Exists(dir)) return;
            var before = DateTime.Now.AddDays(-days);
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (!keep.Contains(f) && File.GetLastWriteTime(f) < before) File.Delete(f);
                }
                catch { }
            }
        }
    }
}
