// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 'PDF 편집' 칸과 여러 문서 합치기, 목표 용량 맞추기.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bakkum
{
    /// 쪽 편집 창에서 정한 결과
    public class PdfPagesEdit
    {
        public class Entry
        {
            public string Path;
            public int Index;
            public int Rotate;
        }

        public List<Entry> Items = new List<Entry>();
        /// one(한 파일로) · split(쪽마다 파일로)
        public string Mode = "one";
        /// 원본 전부를 순서 그대로 두고 돌리기만 했는지
        public bool RotateOnly;
    }

    public static partial class Conversions
    {
        static void PdfTool(Job job, List<string> files)
        {
            switch (job.Option.Id)
            {
                case "pagesave": PagesSave(job); break;
                case "merge": MergeAny(job, files); break;
                case "split": SplitPdfs(job, files); break;
                case "pick": PickPages(job, files); break;
                case "lock": LockPdfs(job, files); break;
                case "unlock": UnlockPdfs(job, files); break;
                case "watermark": WatermarkPdfs(job, files); break;
                case "number": NumberPdfs(job, files); break;
                case "strip": StripPdfs(job, files); break;
                default: throw new UserFacingException("이 일은 창에서 해 주세요.");
            }
        }

        static void NoteSigned(Job job, string f, PdfDocument doc)
        {
            if (PdfTools.IsSigned(doc)) job.Note(Util.ShortName(f) + ": 전자 서명이 있는 PDF예요. 고친 사본에서는 서명이 유효하지 않아요.");
        }

        /// 결과를 윈도우 PDF 엔진으로 열어 쪽 수까지 확인한다.
        static void VerifyPdf(string path, int pages, string password)
        {
            using (var check = new WinPdfDoc(path, password))
            {
                if (check.PageCount != pages) throw new UserFacingException("만든 PDF를 확인해 보니 쪽 수가 맞지 않아 저장하지 않았어요.");
                check.RenderPng(0, 24, 300);
            }
        }

        // ── 합치기 ──

        /// PDF·그림·문서를 놓은 차례대로 하나로 합친다. 파일마다 책갈피를 하나씩 단다.
        static void MergeAny(Job job, List<string> files)
        {
            string temp = Util.TempDir("merge");
            try
            {
                var ready = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int counter = 0;
                // 문서는 프로그램별로 모아 바꾼다(종류가 바뀌면 앞 프로그램을 닫아 메모리를 비운다).
                var docs = files.Where(f => Formats.KindOf(f) != Kind.Image && Formats.KindOf(f) != Kind.Pdf).OrderBy(f => (int)Formats.KindOf(f)).ToList();
                Kind last = Kind.Unknown;
                EachFile(job, docs, "PDF로 바꾸는 중", delegate (string f)
                {
                    Kind k = Formats.KindOf(f);
                    if (last != Kind.Unknown && k != last) job.ReleaseResources();
                    last = k;
                    string t = Path.Combine(temp, (++counter) + ".pdf");
                    OfficeConvert.ToPdf(job, f, t);
                    ready[f] = t;
                });
                job.ReleaseResources();
                EachFile(job, files.Where(f => Formats.KindOf(f) == Kind.Image).ToList(), "넣는 중", delegate (string f)
                {
                    string t = Path.Combine(temp, (++counter) + ".pdf");
                    ImagePdf.Write(new List<string> { f }, t, job);
                    ready[f] = t;
                });
                foreach (var f in files.Where(x => Formats.KindOf(x) == Kind.Pdf)) ready[f] = f;
                var picks = new List<PagePick>();
                var marks = new List<PdfMark>();
                var used = new List<string>();
                EachFile(job, files.Where(ready.ContainsKey).ToList(), "모으는 중", delegate (string f)
                {
                    var doc = PdfTools.OpenPlain(ready[f]);
                    var pages = PdfTools.Pages(doc);
                    marks.Add(new PdfMark { Title = Path.GetFileNameWithoutExtension(f), PickIndex = picks.Count });
                    foreach (int p in pages) picks.Add(new PagePick { Doc = doc, Num = p });
                    used.Add(f);
                    if (PdfTools.HasOutlines(doc)) job.Note(Util.ShortName(f) + ": 원래 있던 책갈피는 옮기지 않았어요.");
                });
                if (used.Count == 0) return;
                string first = used[0];
                string name = Path.GetFileNameWithoutExtension(first) + (used.Count > 1 ? " 외 " + (used.Count - 1) + "개" : "_합침") + ".pdf";
                string dst = Util.UniquePath(Path.Combine(Util.OutputDirFor(first), name));
                Produce(job, dst, delegate (string d)
                {
                    PdfTools.Compose(picks, d, marks, job);
                    VerifyPdf(d, picks.Count, null);
                });
            }
            finally
            {
                Util.TryDeleteDir(temp);
            }
        }

        // ── 나누기·쪽 고르기 ──

        static void SplitPdfs(Job job, List<string> files)
        {
            int every;
            if (!int.TryParse(job.Param("every", "1"), out every) || every < 1) every = 1;
            EachFile(job, files, "나누는 중", delegate (string f)
            {
                var doc = PdfTools.OpenPlain(f);
                var pages = PdfTools.Pages(doc);
                NoteSigned(job, f, doc);
                if (pages.Count <= every)
                {
                    job.Note(Util.ShortName(f) + ": " + pages.Count + "쪽뿐이라 나눌 것이 없어요.");
                    return;
                }
                string stem = Path.GetFileNameWithoutExtension(f);
                string folder = Util.UniquePath(Path.Combine(Util.OutputDirFor(f), stem + "_나눔"));
                Directory.CreateDirectory(folder);
                int digits = Math.Max(2, pages.Count.ToString().Length);
                try
                {
                    int parts = (pages.Count + every - 1) / every;
                    for (int part = 0; part < parts; part++)
                    {
                        job.Check();
                        job.Report(Util.ShortName(f) + " · " + (part + 1) + "/" + parts, (double)part / parts);
                        int from = part * every, to = Math.Min(pages.Count, from + every);
                        var picks = new List<PagePick>();
                        for (int i = from; i < to; i++) picks.Add(new PagePick { Doc = doc, Num = pages[i] });
                        string label = to - from == 1 ? (from + 1).ToString().PadLeft(digits, '0') : (from + 1).ToString().PadLeft(digits, '0') + "-" + to.ToString().PadLeft(digits, '0');
                        PdfTools.Compose(picks, Path.Combine(folder, stem + "_" + label + ".pdf"), null, job);
                    }
                }
                catch
                {
                    Util.TryDeleteDir(folder);
                    throw;
                }
                job.Output(folder);
                job.Note(Util.ShortName(f) + ": " + ((pages.Count + every - 1) / every) + "개 파일로 나눠 '" + Path.GetFileName(folder) + "' 폴더에 담았어요.");
            });
        }

        static void PickPages(Job job, List<string> files)
        {
            string text = job.Param("pages", "");
            string output = job.Param("as", "pdf");
            EachFile(job, files, "쪽 고르는 중", delegate (string f)
            {
                var doc = PdfTools.OpenPlain(f);
                var pages = PdfTools.Pages(doc);
                string error;
                var wanted = PdfTools.ParseRanges(text, pages.Count, out error);
                if (wanted == null) throw new UserFacingException(error);
                NoteSigned(job, f, doc);
                var picks = wanted.Select(i => new PagePick { Doc = doc, Num = pages[i] }).ToList();
                if (output != "pdf")
                {
                    string temp = Util.TempDir("pick");
                    try
                    {
                        string pdf = Path.Combine(temp, Path.GetFileNameWithoutExtension(f) + "_고른쪽.pdf");
                        PdfTools.Compose(picks, pdf, null, job);
                        VerifyPdf(pdf, picks.Count, null);
                        PickedAs(job, f, pdf, output);
                    }
                    finally
                    {
                        Util.TryDeleteDir(temp);
                    }
                    return;
                }
                Produce(job, Sibling(f, "_고른쪽", ".pdf"), delegate (string d)
                {
                    PdfTools.Compose(picks, d, null, job);
                    VerifyPdf(d, picks.Count, null);
                });
            });
        }

        /// 고른 쪽으로 만든 임시 PDF를 원하는 형식으로 바꿔 원본 옆에 둔다.
        static void PickedAs(Job job, string source, string pdf, string output)
        {
            string action = output == "png" || output == "jpg" ? "image" : output == "txt" ? "text" : "format";
            var inner = new Job(MenuAction.All.First(a => a.Id == action), new ActionOption(output == "txt" ? "local" : output, "", ""), new List<string> { pdf }) { AutoYes = job.AutoYes };
            try
            {
                job.Check();
                Execute(inner);
                foreach (string n in inner.Notes) job.Note(n);
                foreach (string p in inner.Problems) job.Problem(source, p.Contains(" — ") ? p.Substring(p.IndexOf(" — ", StringComparison.Ordinal) + 3) : p);
                foreach (string o in inner.Outputs)
                {
                    string dst = Util.UniquePath(Path.Combine(Util.OutputDirFor(source), Path.GetFileName(o)));
                    if (Directory.Exists(o))
                    {
                        Directory.CreateDirectory(dst);
                        foreach (string file in Directory.GetFiles(o)) File.Move(file, Path.Combine(dst, Path.GetFileName(file)));
                    }
                    else File.Move(o, dst);
                    job.Output(dst);
                }
            }
            finally
            {
                inner.ReleaseResources();
            }
        }

        // ── 쪽 편집 창에서 저장 ──

        static void PagesSave(Job job)
        {
            var edit = job.Payload as PdfPagesEdit;
            if (edit == null || edit.Items.Count == 0) throw new UserFacingException("저장할 쪽이 없어요.");
            var docs = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
            var pages = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in edit.Items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                docs[path] = PdfTools.OpenPlain(path);
                pages[path] = PdfTools.Pages(docs[path]);
                NoteSigned(job, path, docs[path]);
            }
            string first = edit.Items[0].Path;
            string stem = Path.GetFileNameWithoutExtension(first);
            if (edit.RotateOnly)
            {
                var doc = docs[first];
                var delta = new Dictionary<int, int>();
                foreach (var e in edit.Items)
                {
                    if (e.Rotate % 360 != 0) delta[pages[first][e.Index]] = e.Rotate;
                }
                Produce(job, Sibling(first, "_편집", ".pdf"), delegate (string d)
                {
                    PdfTools.RotateOnly(doc, delta, d);
                    VerifyPdf(d, edit.Items.Count, null);
                });
                return;
            }
            foreach (var kv in docs)
            {
                if (PdfTools.HasOutlines(kv.Value))
                {
                    job.Note(Util.ShortName(kv.Key) + ": 쪽을 새로 짜면서 책갈피는 옮기지 않았어요.");
                }
            }
            var picks = edit.Items.Select(e => new PagePick { Doc = docs[e.Path], Num = pages[e.Path][e.Index], Rotate = e.Rotate }).ToList();
            if (edit.Mode == "split")
            {
                string folder = Util.UniquePath(Path.Combine(Util.OutputDirFor(first), stem + "_나눔"));
                Directory.CreateDirectory(folder);
                int digits = Math.Max(2, picks.Count.ToString().Length);
                try
                {
                    for (int i = 0; i < picks.Count; i++)
                    {
                        job.Check();
                        job.Report("쪽 " + (i + 1) + " / " + picks.Count, (double)i / picks.Count);
                        PdfTools.Compose(new List<PagePick> { picks[i] }, Path.Combine(folder, stem + "_" + (i + 1).ToString().PadLeft(digits, '0') + ".pdf"), null, job);
                    }
                }
                catch
                {
                    Util.TryDeleteDir(folder);
                    throw;
                }
                job.Output(folder);
                job.Note(picks.Count + "개 파일로 나눠 '" + Path.GetFileName(folder) + "' 폴더에 담았어요.");
                return;
            }
            string name = docs.Count > 1 ? stem + " 외 " + (docs.Count - 1) + "개_편집.pdf" : stem + "_편집.pdf";
            Produce(job, Util.UniquePath(Path.Combine(Util.OutputDirFor(first), name)), delegate (string d)
            {
                PdfTools.Compose(picks, d, null, job);
                VerifyPdf(d, picks.Count, null);
            });
        }

        // ── 암호 ──

        static void LockPdfs(Job job, List<string> files)
        {
            string password = job.Param("password", "");
            if (password.Length == 0) throw new UserFacingException("암호를 넣어 주세요.");
            EachFile(job, files, "암호 거는 중", delegate (string f)
            {
                var doc = PdfDocument.Load(f);
                if (doc.Encrypted) throw new UserFacingException("이미 암호가 걸려 있어요.");
                NoteSigned(job, f, doc);
                int pages = PdfTools.Pages(doc).Count;
                Produce(job, Sibling(f, "_암호", ".pdf"), delegate (string d)
                {
                    PdfCrypt.WriteLocked(doc, doc.Reachable(), new Dictionary<int, PdfObj>(), d, password);
                    // 윈도우 PDF 엔진은 AES-256 파일을 열지 못한다(2026-11-08 실측). 그래서 우리 읽기로 다시 풀어 확인한다.
                    var back = PdfDocument.Load(d, password);
                    if (!back.Encrypted || PdfTools.Pages(back).Count != pages) throw new UserFacingException("만든 PDF를 확인해 보니 쪽 수가 맞지 않아 저장하지 않았어요.");
                });
            });
        }

        static void UnlockPdfs(Job job, List<string> files)
        {
            string password = job.Param("password", "");
            EachFile(job, files, "암호 푸는 중", delegate (string f)
            {
                var doc = PdfDocument.Load(f, password);
                if (!doc.Encrypted)
                {
                    job.Note(Util.ShortName(f) + ": 암호가 걸려 있지 않아요.");
                    return;
                }
                if (!doc.Crypt.ViaOwner && !doc.Crypt.FullPermissions)
                {
                    throw new UserFacingException("인쇄·복사 제한이 걸린 PDF예요. 문서를 만든 사람의 소유자 암호를 넣어야 풀 수 있어요.");
                }
                int pages = PdfTools.Pages(doc).Count;
                Produce(job, Sibling(f, "_암호해제", ".pdf"), delegate (string d)
                {
                    PdfRewrite.Write(doc, doc.Reachable(), new Dictionary<int, PdfObj>(), d, false);
                    VerifyPdf(d, pages, null);
                });
            });
        }

        // ── 꾸미기 ──

        static void WatermarkPdfs(Job job, List<string> files)
        {
            double opacity;
            if (!double.TryParse(job.Param("opacity", "0.2"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out opacity)) opacity = 0.2;
            var spec = new WatermarkSpec { Text = job.Param("text", "").Trim(), Mode = job.Param("mode", "diagonal"), Opacity = Math.Max(0.05, Math.Min(0.8, opacity)) };
            if (spec.Text.Length == 0) throw new UserFacingException("워터마크 글자를 적어 주세요.");
            EachFile(job, files, "워터마크 넣는 중", delegate (string f)
            {
                var doc = PdfTools.OpenPlain(f);
                NoteSigned(job, f, doc);
                int pages = PdfTools.Pages(doc).Count;
                Produce(job, Sibling(f, "_워터마크", ".pdf"), delegate (string d)
                {
                    PdfTools.Watermark(doc, d, spec, job);
                    VerifyPdf(d, pages, null);
                });
            });
        }

        static void NumberPdfs(Job job, List<string> files)
        {
            int start;
            if (!int.TryParse(job.Param("start", "1"), out start)) start = 1;
            var spec = new PageNumberSpec { Place = job.Param("place", "bottom-center"), Style = job.Param("style", "plain"), Start = start };
            EachFile(job, files, "쪽 번호 넣는 중", delegate (string f)
            {
                var doc = PdfTools.OpenPlain(f);
                NoteSigned(job, f, doc);
                int pages = PdfTools.Pages(doc).Count;
                Produce(job, Sibling(f, "_쪽번호", ".pdf"), delegate (string d)
                {
                    PdfTools.PageNumbers(doc, d, spec, job);
                    VerifyPdf(d, pages, null);
                });
            });
        }

        static void StripPdfs(Job job, List<string> files)
        {
            EachFile(job, files, "정보 지우는 중", delegate (string f)
            {
                var doc = PdfTools.OpenPlain(f);
                NoteSigned(job, f, doc);
                int pages = PdfTools.Pages(doc).Count;
                Produce(job, Sibling(f, "_정보지움", ".pdf"), delegate (string d)
                {
                    PdfTools.StripInfo(doc, d);
                    VerifyPdf(d, pages, null);
                });
            });
        }

        // ── 목표 용량 ──

        static readonly int[][] TargetLadder =
        {
            new[] { 3000, 88 }, new[] { 2400, 80 }, new[] { 2000, 72 }, new[] { 1600, 64 }, new[] { 1300, 55 }, new[] { 1100, 46 }, new[] { 900, 38 }, new[] { 700, 30 }
        };

        delegate long LadderTry(int longSide, int quality, string temp);

        /// 사다리에서 목표를 넘지 않는 가장 약한 단계를 고른다. 어떤 단계로도 안 되면 가장 작은 결과.
        static string BestOnLadder(string dir, string ext, long target, LadderTry attempt, out long size, out bool reached)
        {
            int lo = 0, hi = TargetLadder.Length - 1;
            string best = null, smallest = null;
            long bestSize = 0, smallestSize = long.MaxValue;
            int counter = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                string t = Path.Combine(dir, "try" + (++counter) + ext);
                long got = attempt(TargetLadder[mid][0], TargetLadder[mid][1], t);
                if (got > 0 && got < smallestSize)
                {
                    smallest = t;
                    smallestSize = got;
                }
                if (got > 0 && got <= target)
                {
                    best = t;
                    bestSize = got;
                    hi = mid - 1;
                }
                else lo = mid + 1;
            }
            reached = best != null;
            size = reached ? bestSize : smallestSize;
            return reached ? best : smallest;
        }

        static void ShrinkToTarget(Job job, List<string> files)
        {
            double mb;
            if (!double.TryParse(job.Param("mb", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mb) || mb <= 0)
            {
                throw new UserFacingException("목표 용량을 숫자로 적어 주세요.");
            }
            long target = (long)(mb * 1024 * 1024);
            EachFile(job, files, "목표 용량에 맞추는 중", delegate (string f)
            {
                long before = new FileInfo(f).Length;
                if (before <= target)
                {
                    job.Note(Util.ShortName(f) + ": 이미 " + Size(before) + "라서 그대로 두었어요.");
                    return;
                }
                string dir = Util.TempDir("target");
                try
                {
                    long size;
                    bool reached;
                    string chosen;
                    string ext;
                    Kind kind = Formats.KindOf(f);
                    if (kind == Kind.Pdf)
                    {
                        using (new WinPdfDoc(f)) { }
                        ext = ".pdf";
                        chosen = BestOnLadder(dir, ext, target, delegate (int side, int q, string t)
                        {
                            job.Check();
                            PdfShrink.Run(f, t, side, q, job);
                            return new FileInfo(t).Length;
                        }, out size, out reached);
                        if (chosen == null) throw new UserFacingException("줄이지 못했어요.");
                        if (!SamePdf(f, chosen)) throw new UserFacingException("줄인 PDF를 확인해 보니 원본과 달라 보여서 저장하지 않았어요. 원본은 그대로예요.");
                    }
                    else if (kind == Kind.Image)
                    {
                        string have = Formats.ImageFormatOf(f);
                        if (have == "gif" || (have == "tiff" && Imaging.PageCount(f) > 1))
                        {
                            job.Note(Util.ShortName(f) + ": 이 형식은 목표 용량으로 줄이지 않아요.");
                            return;
                        }
                        ext = ".jpg";
                        var src = Imaging.Load(f);
                        chosen = BestOnLadder(dir, ext, target, delegate (int side, int q, string t)
                        {
                            job.Check();
                            File.WriteAllBytes(t, Imaging.Jpeg(Imaging.Fit(src, side), q));
                            return new FileInfo(t).Length;
                        }, out size, out reached);
                        if (have == "png" && Imaging.MayHaveAlpha(src)) job.Note(Util.ShortName(f) + ": JPG로 줄이면서 투명한 곳은 흰색이 되었어요.");
                    }
                    else
                    {
                        ext = Formats.Ext(f);
                        chosen = BestOnLadder(dir, ext, target, delegate (int side, int q, string t)
                        {
                            job.Check();
                            DocImages.Shrink(f, t, side, q, job);
                            return new FileInfo(t).Length;
                        }, out size, out reached);
                    }
                    if (chosen == null) throw new UserFacingException("줄이지 못했어요.");
                    if (size >= before)
                    {
                        job.Note(Util.ShortName(f) + ": 더 줄일 수 있는 부분이 없어요.");
                        return;
                    }
                    Produce(job, Sibling(f, "_압축", ext), delegate (string d) { File.Copy(chosen, d, true); });
                    job.Note(Util.ShortName(f) + ": " + Size(before) + " → " + Size(size) + (reached ? "" : " (목표 " + Size(target) + "까지는 줄이지 못했어요. 글자가 많은 부분은 줄지 않아요.)"));
                }
                finally
                {
                    Util.TryDeleteDir(dir);
                }
            });
        }
    }
}
