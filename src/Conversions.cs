// 메뉴 칸마다 하는 일. 파일 하나가 실패해도 나머지 파일은 계속한다.
// 결과는 원본 옆(쓸 수 없으면 문서\변환 결과)에 두고, 이름이 겹치면 번호를 붙인다. 원본은 건드리지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Bakkum
{
    public static partial class Conversions
    {
        const double ImageDpi = 200;
        const double OcrDpi = 300;
        const int MaxRender = 9000;
        const int OcrMaxSide = 6000;
        const int AiLongSide = 1600;
        const int AiChunk = 5;
        const int AiMaxPages = 100;

        internal delegate void FileWork(string path);

        public static void Execute(Job job)
        {
            // 경로가 너무 길거나, 놓은 뒤에 옮겨지거나 지워진 파일은 열 수 없으니 먼저 알려 주고 뺀다.
            var reachable = new List<string>();
            foreach (var f in job.Files)
            {
                // 폴더째 다루는 칸은 압축뿐이다. 다른 칸에는 폴더 안의 파일이 따로 들어 있다.
                if (ArchiveJobs.IsFolder(f))
                {
                    if (job.Action.Id == "archive" && Directory.Exists(f)) reachable.Add(f);
                    continue;
                }
                if (Util.TooLong(f)) job.Problem(f, Util.LongPathMessage);
                else if (!File.Exists(f)) job.Problem(f, "파일을 찾지 못했어요. 옮겨졌거나 지워졌는지 확인해 주세요.");
                else reachable.Add(f);
            }
            var files = job.Action.Usable(reachable);
            if (files.Count == 0)
            {
                if (job.Problems.Count > 0) return;
                throw new UserFacingException("이 칸에서 바꿀 수 있는 파일이 없어요. (" + job.Action.Hint + ")");
            }
            // 놓은 파일 가운데 이 칸이 못 받는 것은 이름을 알려 준다(말없이 빠지면 빠진 줄 모른다).
            var left = reachable.Where(f => !files.Contains(f)).ToList();
            if (left.Count > 0)
            {
                string names = string.Join(", ", left.Take(3).Select(Util.ShortName));
                if (left.Count > 3) names += " 외 " + (left.Count - 3) + "개";
                job.Note("이 칸에서 못 바꾸는 파일 " + left.Count + "개는 건너뛰었어요: " + names);
            }
            switch (job.Action.Id)
            {
                case "pdf": ToPdf(job, files); break;
                case "image": ToImages(job, files); break;
                case "format": Reformat(job, files); break;
                case "shrink": Shrink(job, files); break;
                case "text": ExtractText(job, files); break;
                case "photo": Photo(job, files); break;
                case "pdftool": PdfTool(job, files); break;
                case "archive": ArchiveJobs.Run(job, files); break;
                case "media": MediaJobs.Run(job, files); break;
                case "ai": AiTasks.Run(job, files); break;
                default: throw new UserFacingException("아직 없는 기능이에요.");
            }
        }

        // ── 공통 ──

        internal static void EachFile(Job job, List<string> files, string verb, FileWork work)
        {
            for (int i = 0; i < files.Count; i++)
            {
                job.Check();
                string f = files[i];
                string count = files.Count > 1 ? " (" + (i + 1) + "/" + files.Count + ")" : "";
                job.Report(Util.ShortName(f) + " " + verb + count, files.Count > 1 ? (double)i / files.Count : -1);
                try
                {
                    work(f);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (UserFacingException ex)
                {
                    job.Problem(f, ex.Message);
                    job.ReleaseResources();
                }
                catch (PathTooLongException)
                {
                    // 원본은 열렸어도 만들 파일(…_그림\…_001.png 등)의 경로가 한도를 넘을 수 있다.
                    job.Problem(f, Util.LongPathMessage);
                    job.ReleaseResources();
                }
                catch (Exception ex)
                {
                    Log.Write("파일 실패(" + job.Action.Id + " " + Formats.Ext(f) + "): " + ex.GetType().Name + " @ " + ex.TargetSite);
                    job.Problem(f, "바꾸지 못했어요. (" + ex.GetType().Name + ")");
                    job.ReleaseResources();
                }
            }
        }

        /// dst를 만들고, 실패하거나 비어 있으면 지운다. 성공하면 결과 목록에 넣는다.
        internal static void Produce(Job job, string dst, FileWork make)
        {
            try
            {
                make(dst);
            }
            catch
            {
                TryDelete(dst);
                throw;
            }
            var fi = new FileInfo(dst);
            if (!fi.Exists || fi.Length == 0)
            {
                TryDelete(dst);
                throw new UserFacingException("결과 파일이 만들어지지 않았어요.");
            }
            job.Output(dst);
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        internal static string Sibling(string src, string suffix, string ext)
        {
            return Util.UniquePath(Path.Combine(Util.OutputDirFor(src), Path.GetFileNameWithoutExtension(src) + suffix + ext));
        }

        public static string Size(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + "MB";
            return Math.Max(1, (int)Math.Round(bytes / 1024.0)) + "KB";
        }

        /// 문서(한글·워드·엑셀·PPT)는 임시 PDF로 바꿔서, PDF는 그대로 쓴다. 다 쓰면 임시 폴더를 지운다.
        internal static T WithPdf<T>(Job job, string src, Func<string, T> use)
        {
            if (Formats.KindOf(src) == Kind.Pdf) return use(src);
            string dir = Util.TempDir("topdf");
            try
            {
                string pdf = Path.Combine(dir, "doc.pdf");
                OfficeConvert.ToPdf(job, src, pdf);
                return use(pdf);
            }
            finally
            {
                Util.TryDeleteDir(dir);
            }
        }

        // ── PDF로 ──

        static void ToPdf(Job job, List<string> files)
        {
            if (job.Option.Id == "merge")
            {
                MergeAny(job, files);
                return;
            }
            var images = files.Where(f => Formats.KindOf(f) == Kind.Image).ToList();
            int already = files.Count(f => Formats.KindOf(f) == Kind.Pdf);
            if (already > 0) job.Note("이미 PDF인 파일 " + already + "개는 그대로 두었어요.");
            // 문서는 종류별로 모아(한글 → 워드 → 엑셀 → PPT) 바꾸고, 종류가 바뀌면 앞 프로그램을 닫는다.
            // 한글·한셀·한쇼를 한꺼번에 띄워 두지 않아 메모리가 적은 학교 PC에서도 덜 무겁다.
            var docs = files.Where(f => Formats.KindOf(f) != Kind.Image && Formats.KindOf(f) != Kind.Pdf).OrderBy(f => (int)Formats.KindOf(f)).ToList();
            Kind last = Kind.Unknown;
            EachFile(job, docs, "PDF로 바꾸는 중", delegate (string f)
            {
                Kind k = Formats.KindOf(f);
                if (last != Kind.Unknown && k != last) job.ReleaseResources();
                last = k;
                Produce(job, Util.OutputFile(f, ".pdf"), delegate (string dst) { OfficeConvert.ToPdf(job, f, dst); });
            });
            if (images.Count == 0) return;
            EachFile(job, images, "PDF로 바꾸는 중", delegate (string f)
            {
                Produce(job, Util.OutputFile(f, ".pdf"), delegate (string dst) { ImagePdf.Write(new List<string> { f }, dst, job); });
            });
        }

        // ── 그림으로(쪽마다) ──

        static void ToImages(Job job, List<string> files)
        {
            if (job.Option.Id == "extract")
            {
                DocImages.Extract(job, files);
                return;
            }
            string fmt = job.Option.Id == "jpg" ? "jpg" : "png";
            EachFile(job, files, "그림으로 바꾸는 중", delegate (string f)
            {
                WithPdf(job, f, delegate (string pdf)
                {
                    using (var doc = new WinPdfDoc(pdf))
                    {
                        int n = doc.PageCount;
                        if (n == 0) throw new UserFacingException("쪽이 없는 문서예요.");
                        string stem = Path.GetFileNameWithoutExtension(f);
                        string folder = null;
                        if (n > 1)
                        {
                            folder = Util.UniquePath(Path.Combine(Util.OutputDirFor(f), stem + "_그림"));
                            Directory.CreateDirectory(folder);
                        }
                        int digits = Math.Max(2, n.ToString().Length);
                        try
                        {
                            for (int i = 0; i < n; i++)
                            {
                                job.Check();
                                if (n > 1) job.Report(Util.ShortName(f) + " · " + (i + 1) + "/" + n + "쪽", (double)i / n);
                                byte[] png = doc.RenderPng(i, ImageDpi, MaxRender);
                                byte[] data = fmt == "png" ? png : Imaging.Jpeg(Imaging.Decoder(png).Frames[0], 90);
                                string path = n == 1 ? Util.OutputFile(f, "." + fmt) : Path.Combine(folder, stem + "_" + (i + 1).ToString().PadLeft(digits, '0') + "." + fmt);
                                File.WriteAllBytes(path, data);
                                if (n == 1) job.Output(path);
                            }
                        }
                        catch
                        {
                            if (folder != null) Util.TryDeleteDir(folder);
                            throw;
                        }
                        if (folder != null)
                        {
                            job.Output(folder);
                            job.Note(Util.ShortName(f) + ": " + n + "쪽을 '" + Path.GetFileName(folder) + "' 폴더에 담았어요.");
                        }
                    }
                    return true;
                });
            });
        }

        // ── 형식 바꾸기 ──

        static void Reformat(Job job, List<string> files)
        {
            string target = job.Option.Id;
            if (target == "hwpx" || target == "hwp")
            {
                string from = target == "hwpx" ? ".hwp" : ".hwpx";
                var docs = files.Where(f => Formats.Ext(f) == from).ToList();
                if (docs.Count < files.Count) job.Note(target.ToUpperInvariant() + "로 바꿀 수 없는 파일 " + (files.Count - docs.Count) + "개는 건너뛰었어요.");
                EachFile(job, docs, target.ToUpperInvariant() + "로 바꾸는 중", delegate (string f)
                {
                    Produce(job, Util.OutputFile(f, "." + target), delegate (string dst)
                    {
                        job.Use(() => new Hangul(job)).Convert(f, dst, target == "hwpx" ? "HWPX" : "HWP");
                    });
                });
                return;
            }
            if (DocConvert.Handles(target))
            {
                DocConvert.Run(job, files, target);
                return;
            }
            var images = files.Where(f => Formats.KindOf(f) == Kind.Image).ToList();
            if (images.Count < files.Count) job.Note("그림이 아닌 파일 " + (files.Count - images.Count) + "개는 건너뛰었어요.");
            EachFile(job, images, target.ToUpperInvariant() + "로 바꾸는 중", delegate (string f)
            {
                string have = Formats.ImageFormatOf(f);
                if (have == target)
                {
                    job.Note(Util.ShortName(f) + ": 이미 " + target.ToUpperInvariant() + " 파일이에요.");
                    return;
                }
                var dec = Imaging.Decoder(f);
                int frames = dec.Frames.Count;
                if (frames > 1 && have == "tiff")
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    int digits = Math.Max(2, frames.ToString().Length);
                    for (int i = 0; i < frames; i++)
                    {
                        job.Check();
                        string path = Util.UniquePath(Path.Combine(Util.OutputDirFor(f), stem + "_" + (i + 1).ToString().PadLeft(digits, '0') + "." + target));
                        Produce(job, path, delegate (string dst) { File.WriteAllBytes(dst, PhotoRender.Encode(Imaging.Upright(dec.Frames[i]), target, 92)); });
                    }
                    return;
                }
                if (frames > 1) job.Note(Util.ShortName(f) + ": 움직이는 그림은 첫 장면만 바꿨어요.");
                Produce(job, Util.OutputFile(f, "." + target), delegate (string dst) { File.WriteAllBytes(dst, PhotoRender.Encode(Imaging.Upright(dec.Frames[0]), target, 92)); });
            });
        }

        // ── 용량 줄이기 ──

        static void Shrink(Job job, List<string> files)
        {
            if (job.Option.Id == "target")
            {
                ShrinkToTarget(job, files);
                return;
            }
            int longSide, quality;
            switch (job.Option.Id)
            {
                case "light": longSide = 3000; quality = 85; break;
                case "strong": longSide = 1400; quality = 60; break;
                default: longSide = 2000; quality = 75; break;
            }
            EachFile(job, files, "줄이는 중", delegate (string f)
            {
                long before = new FileInfo(f).Length;
                if (Formats.KindOf(f) == Kind.Pdf)
                {
                    // 윈도우 PDF 엔진이 열지 못하는(손상된·덜 받은) PDF는 줄이지 않는다. 다른 칸과 같은 기준이다.
                    using (new WinPdfDoc(f)) { }
                    string dst = Sibling(f, "_압축", ".pdf");
                    PdfShrink.Result r = null;
                    Produce(job, dst, delegate (string d)
                    {
                        r = PdfShrink.Run(f, d, longSide, quality, job);
                        job.Report("줄인 PDF 확인 중", -1);
                        if (!SamePdf(f, d)) throw new UserFacingException("줄인 PDF를 확인해 보니 원본과 달라 보여서 저장하지 않았어요. 원본은 그대로예요.");
                    });
                    long after = new FileInfo(dst).Length;
                    if (!WorthKeeping(before, after))
                    {
                        TryDelete(dst);
                        job.Outputs.Remove(dst);
                        job.Note(Util.ShortName(f) + ": 더 줄일 부분이 거의 없어 그대로 두었어요.");
                        return;
                    }
                    job.Note(Util.ShortName(f) + ": " + Size(before) + " → " + Size(after) + (r != null && r.Pictures > 0 ? " (그림 " + r.Pictures + "개 중 " + r.Recoded + "개 줄임)" : ""));
                    return;
                }
                if (Formats.KindOf(f) != Kind.Image)
                {
                    string doc = Sibling(f, "_압축", Formats.Ext(f));
                    Produce(job, doc, delegate (string d) { DocImages.Shrink(f, d, longSide, quality, job); });
                    long shrunk = new FileInfo(doc).Length;
                    if (!WorthKeeping(before, shrunk))
                    {
                        TryDelete(doc);
                        job.Outputs.Remove(doc);
                        job.Note(Util.ShortName(f) + ": 줄일 사진이 거의 없어 그대로 두었어요.");
                        return;
                    }
                    job.Note(Util.ShortName(f) + ": " + Size(before) + " → " + Size(shrunk));
                    return;
                }
                string have = Formats.ImageFormatOf(f);
                if (have == "gif")
                {
                    job.Note(Util.ShortName(f) + ": GIF는 줄이지 않아요.");
                    return;
                }
                // 여러 쪽짜리 TIFF를 그림 한 장으로 줄이면 둘째 쪽부터 사라진다.
                if (have == "tiff" && Imaging.PageCount(f) > 1)
                {
                    job.Note(Util.ShortName(f) + ": 여러 쪽짜리 TIFF는 줄이지 않아요. 'PDF로' 칸에서 PDF로 바꾼 뒤 줄여 보세요.");
                    return;
                }
                var src = Imaging.Load(f);
                var fit = Imaging.Fit(src, longSide);
                string ext = have == "png" ? ".png" : ".jpg";
                byte[] data = ext == ".png" ? Imaging.Encode(fit, "png", 0) : Imaging.Jpeg(fit, quality);
                if (!WorthKeeping(before, data.LongLength))
                {
                    job.Note(Util.ShortName(f) + ": 이미 충분히 작아서 그대로 두었어요.");
                    return;
                }
                string target = Sibling(f, "_압축", ext);
                Produce(job, target, delegate (string d) { File.WriteAllBytes(d, data); });
                job.Note(Util.ShortName(f) + ": " + Size(before) + " → " + Size(data.LongLength));
            });
        }

        /// 3% 넘게 줄고, 알림에 보이는 크기도 달라야 새 파일을 남긴다.
        /// (40KB가 39KB 되는 정도나, 알림에 '1.0MB → 1.0MB'로 보일 차이는 원본과 같다고 본다.)
        static bool WorthKeeping(long before, long after)
        {
            return after < before * 0.97 && Size(after) != Size(before);
        }

        /// 줄인 PDF가 원본과 같은 문서로 보이는지 윈도우 PDF 엔진으로 확인한다.
        /// 쪽 수가 같고, 첫 쪽·가운데 쪽·끝 쪽을 작게(24dpi) 그려 회색 밝기 차이가 평균 12(0~255) 안이어야 한다.
        /// 그림을 다시 누른 차이는 이 크기에서 3 안팎이고, 쪽 내용이 빠지면 그보다 훨씬 커진다.
        static bool SamePdf(string original, string shrunk)
        {
            try
            {
                using (var a = new WinPdfDoc(original))
                using (var b = new WinPdfDoc(shrunk))
                {
                    if (a.PageCount == 0 || a.PageCount != b.PageCount) return false;
                    foreach (int i in new[] { 0, a.PageCount / 2, a.PageCount - 1 }.Distinct())
                    {
                        double diff = GrayDifference(a.RenderPng(i, 24, 400), b.RenderPng(i, 24, 400));
                        if (diff > 12)
                        {
                            Log.Write("줄인 PDF 확인: " + (i + 1) + "쪽 밝기 차이 " + diff.ToString("0.0"));
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("줄인 PDF 확인 실패: " + ex.GetType().Name);
                return false;
            }
        }

        static double GrayDifference(byte[] pngA, byte[] pngB)
        {
            var a = Imaging.ToGray8(Imaging.Decoder(pngA).Frames[0]);
            var b = Imaging.ToGray8(Imaging.Decoder(pngB).Frames[0]);
            if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return 255;
            int w = a.PixelWidth, h = a.PixelHeight;
            var pa = new byte[w * h];
            var pb = new byte[w * h];
            a.CopyPixels(pa, w, 0);
            b.CopyPixels(pb, w, 0);
            long sum = 0;
            for (int i = 0; i < pa.Length; i++) sum += Math.Abs(pa[i] - pb[i]);
            return pa.Length == 0 ? 255 : (double)sum / pa.Length;
        }

        // ── 글자 뽑기 ──

        static void ExtractText(Job job, List<string> files)
        {
            var docs = files.Where(DocRead.IsDocument).ToList();
            var others = files.Where(f => !DocRead.IsDocument(f)).ToList();
            EachFile(job, docs, "글자 읽는 중", delegate (string f)
            {
                string text = DocRead.Text(job, f);
                if (text.Trim().Length == 0) throw new UserFacingException("글자가 없는 문서예요.");
                SaveText(job, f, text);
            });
            if (others.Count == 0) return;
            if (job.Option.Id == "ai")
            {
                Ai.EnsureReady(job);
                int pages = 0;
                foreach (var f in others)
                {
                    job.Check();
                    if (Formats.KindOf(f) == Kind.Image) pages += Imaging.PageCount(f);
                    else
                    {
                        try
                        {
                            using (var d = new WinPdfDoc(f)) pages += d.PageCount;
                        }
                        catch (UserFacingException) { }
                    }
                }
                if (pages > AiMaxPages) throw new UserFacingException("AI로는 한 번에 " + AiMaxPages + "쪽까지 보낼 수 있어요. (지금 " + pages + "쪽)");
                var answer = Ai.AskConsent(job, pages);
                if (answer == Consent.Cancel) throw new OperationCanceledException();
                if (answer == Consent.Send)
                {
                    AiText(job, others);
                    return;
                }
                job.Note("AI로 보내지 않고 이 PC에서 읽었어요.");
            }
            LocalText(job, others);
        }

        /// 글자 뽑기 결과 저장: '워드로'를 골랐으면 docx, 아니면 txt
        static void SaveText(Job job, string f, string text)
        {
            if (job.Option.Id == "docx")
            {
                Produce(job, Util.OutputFile(f, ".docx"), delegate (string dst) { DocxWriter.Write(dst, MarkdownDoc.FromPlain(text)); });
                return;
            }
            Produce(job, Util.OutputFile(f, ".txt"), delegate (string dst) { File.WriteAllText(dst, text, new UTF8Encoding(true)); });
        }

        /// PDF를 쪽마다 그려 글자 인식으로 읽는다.
        internal static string OcrPdf(Job job, string f)
        {
            var ocr = job.Use(() => new WinOcr());
            var sb = new StringBuilder();
            using (var doc = new WinPdfDoc(f))
            {
                int n = doc.PageCount;
                for (int i = 0; i < n; i++)
                {
                    job.Check();
                    if (n > 1) job.Report(Util.ShortName(f) + " · " + (i + 1) + "/" + n + "쪽 읽는 중", (double)i / n);
                    AppendPage(sb, i, n, ocr.Read(doc.RenderPng(i, OcrDpi, OcrMaxSide)));
                }
            }
            return sb.ToString();
        }

        static void LocalText(Job job, List<string> files)
        {
            EachFile(job, files, "글자 읽는 중", delegate (string f)
            {
                // 글자가 든 PDF는 그림으로 그려 인식하지 않고 들어 있는 글자를 그대로 읽는다(틀릴 일이 없다).
                if (Formats.KindOf(f) == Kind.Pdf)
                {
                    var layer = PdfText.Read(f, job);
                    if (layer != null && layer.Usable)
                    {
                        SaveText(job, f, layer.Plain());
                        return;
                    }
                }
                var ocr = job.Use(() => new WinOcr());
                if (!ocr.Korean && !job.Notes.Any(n => n.StartsWith("한국어 글자 인식")))
                {
                    job.Note("한국어 글자 인식이 이 PC에 없어 '" + ocr.LanguageTag + "' 인식으로 읽었어요.");
                }
                string text;
                if (Formats.KindOf(f) == Kind.Image)
                {
                    // 여러 쪽짜리 TIFF(스캔·팩스)는 쪽마다 읽는다.
                    var pages = Imaging.Pages(f);
                    var sb = new StringBuilder();
                    for (int i = 0; i < pages.Count; i++)
                    {
                        job.Check();
                        if (pages.Count > 1) job.Report(Util.ShortName(f) + " · " + (i + 1) + "/" + pages.Count + "쪽 읽는 중", (double)i / pages.Count);
                        string page = ocr.Read(Imaging.Encode(Imaging.Fit(pages[i], OcrMaxSide), "bmp", 0));
                        AppendPage(sb, i, pages.Count, page);
                    }
                    text = sb.ToString();
                }
                else text = OcrPdf(job, f);
                if (StripMarkers(text).Trim().Length == 0) throw new UserFacingException("글자를 찾지 못했어요.");
                SaveText(job, f, text);
            });
        }

        /// 여러 쪽이면 쪽마다 '--- n쪽 ---' 줄을 붙여 잇는다.
        static void AppendPage(StringBuilder sb, int index, int count, string page)
        {
            if (count > 1)
            {
                if (sb.Length > 0) sb.Append("\r\n\r\n");
                sb.Append("--- ").Append(index + 1).Append("쪽 ---\r\n\r\n");
            }
            sb.Append(page);
        }

        static string StripMarkers(string s)
        {
            var sb = new StringBuilder();
            foreach (var line in s.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("--- ") && t.EndsWith("쪽 ---")) continue;
                sb.AppendLine(t);
            }
            return sb.ToString();
        }

        /// 사진·PDF를 몇 쪽씩 묶어 AI에게 읽힌다. maxPages까지만. 조각마다 part(읽은 글, 전체 쪽 수)를 부른다.
        internal static void TranscribeFile(Job job, string f, int maxPages, Action<string, int> part)
        {
            if (Formats.KindOf(f) == Kind.Image)
            {
                // 여러 쪽짜리 TIFF는 PDF처럼 몇 쪽씩 묶어 보낸다.
                var pages = Imaging.Pages(f);
                int total = Math.Min(pages.Count, maxPages);
                for (int start = 0; start < total; start += AiChunk)
                {
                    int end = Math.Min(total, start + AiChunk);
                    var chunk = new List<byte[]>();
                    for (int i = start; i < end; i++)
                    {
                        job.Check();
                        if (total > 1) job.Report(Util.ShortName(f) + " · " + (i + 1) + "/" + total + "쪽 준비 중", (double)i / total);
                        chunk.Add(Imaging.Jpeg(Imaging.Fit(pages[i], AiLongSide), 85));
                    }
                    if (total > 1) job.Report(Util.ShortName(f) + " · " + (start + 1) + "~" + end + "쪽 AI가 읽는 중", (double)start / total);
                    part(Ai.Transcribe(chunk, start + 1, job), total);
                }
                return;
            }
            using (var doc = new WinPdfDoc(f))
            {
                int total = Math.Min(doc.PageCount, maxPages);
                for (int start = 0; start < total; start += AiChunk)
                {
                    int end = Math.Min(total, start + AiChunk);
                    var chunk = new List<byte[]>();
                    for (int i = start; i < end; i++)
                    {
                        job.Check();
                        job.Report(Util.ShortName(f) + " · " + (i + 1) + "/" + total + "쪽 준비 중", (double)i / total);
                        byte[] png = doc.RenderPng(i, ImageDpi, AiLongSide);
                        chunk.Add(Imaging.Jpeg(Imaging.Decoder(png).Frames[0], 85));
                    }
                    job.Report(Util.ShortName(f) + " · " + (start + 1) + "~" + end + "쪽 AI가 읽는 중", (double)start / total);
                    part(Ai.Transcribe(chunk, start + 1, job), total);
                }
            }
        }

        static void AiText(Job job, List<string> files)
        {
            string stopped = null;
            EachFile(job, files, "AI로 읽는 중", delegate (string f)
            {
                if (stopped != null) throw new UserFacingException("앞 파일의 AI 오류로 건너뛰었어요. (" + stopped + ")");
                var parts = new List<string>();
                int total = 0;
                try
                {
                    TranscribeFile(job, f, int.MaxValue, delegate (string md, int pages)
                    {
                        parts.Add(md);
                        total = pages;
                    });
                }
                catch (UserFacingException ex)
                {
                    stopped = ex.Message;
                    if (parts.Count == 0) throw;
                    string partial = string.Join("\n\n", parts) + "\n\n<!-- 여기까지 읽었어요: " + total + "쪽 중 " + Math.Min(total, parts.Count * AiChunk) + "쪽 -->\n";
                    Produce(job, Util.OutputFile(f, ".md"), delegate (string dst) { File.WriteAllText(dst, partial, new UTF8Encoding(false)); });
                    throw new UserFacingException(ex.Message + " (앞부분만 저장했어요)");
                }
                string md2 = string.Join("\n\n", parts) + "\n";
                Produce(job, Util.OutputFile(f, ".md"), delegate (string dst) { File.WriteAllText(dst, md2, new UTF8Encoding(false)); });
            });
        }
    }
}
