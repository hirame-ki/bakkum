// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 파일 종류 구분과 메뉴 칸(할 일)·고를 거리 정의.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bakkum
{
    public enum Kind { Unknown, Hwp, Word, Excel, Ppt, Pdf, Image, Text, Archive, Folder, Video, Audio }

    public static class Formats
    {
        public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".svg" };

        public static Kind KindOf(string path)
        {
            if (path.EndsWith("\\", StringComparison.Ordinal)) return Kind.Folder;
            string e = Path.GetExtension(path).ToLowerInvariant();
            switch (e)
            {
                case ".hwp":
                case ".hwpx":
                    return Kind.Hwp;
                case ".doc":
                case ".docx":
                case ".rtf":
                case ".odt":
                    return Kind.Word;
                case ".xls":
                case ".xlsx":
                case ".xlsm":
                case ".csv":
                case ".cell":
                case ".ods":
                    return Kind.Excel;
                case ".ppt":
                case ".pptx":
                case ".show":
                    return Kind.Ppt;
                case ".pdf":
                    return Kind.Pdf;
                case ".txt":
                case ".md":
                case ".markdown":
                case ".html":
                case ".htm":
                case ".srt":
                case ".vtt":
                case ".tsv":
                case ".epub":
                    return Kind.Text;
                case ".zip":
                case ".tar":
                case ".gz":
                case ".tgz":
                    return Kind.Archive;
                case ".mp4":
                case ".m4v":
                case ".mov":
                case ".mkv":
                case ".avi":
                case ".wmv":
                case ".asf":
                case ".webm":
                case ".mpg":
                case ".mpeg":
                case ".ts":
                case ".mts":
                case ".m2ts":
                case ".3gp":
                    return Kind.Video;
                case ".mp3":
                case ".wav":
                case ".m4a":
                case ".aac":
                case ".flac":
                case ".ogg":
                case ".oga":
                case ".opus":
                case ".wma":
                    return Kind.Audio;
            }
            if (Array.IndexOf(ImageExts, e) >= 0) return Kind.Image;
            return Kind.Unknown;
        }

        public static string Ext(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant();
        }

        /// 놓은 것 가운데 폴더는 그 안의 파일(아는 형식만, 탐색기 이름 순)로 바꾼다.
        public static List<string> Expand(IEnumerable<string> paths)
        {
            var list = new List<string>();
            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        // 폴더째 묶을 수 있게 폴더 자리도 남긴다(끝이 \ 인 항목). 드라이브 전체는 뺀다.
                        string full = Path.GetFullPath(p), trimmed = full.TrimEnd('\\');
                        if (!string.Equals(Path.GetPathRoot(full).TrimEnd('\\'), trimmed, StringComparison.OrdinalIgnoreCase)) list.Add(trimmed + "\\");
                        var inner = Directory.GetFiles(Path.GetFullPath(p)).Where(f => KindOf(f) != Kind.Unknown).ToList();
                        inner.Sort(Util.NaturalCompare);
                        list.AddRange(inner);
                    }
                    else if (File.Exists(p)) list.Add(Path.GetFullPath(p));
                    // 너무 긴 경로는 있어도 없다고 나온다. 말없이 빠지지 않게 남겨 두고, 작업이 알기 쉬운 말로 알린다.
                    else if (Util.TooLong(p) && Path.IsPathRooted(p)) list.Add(p);
                }
                catch { }
            }
            // 폴더와 그 안의 파일을 같이 놓아도 한 번만 바꾼다.
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// 이미지 형식 이름(png·jpg·…)을 확장자로 묶어 같은 형식인지 본다.
        public static string ImageFormatOf(string path)
        {
            string e = Ext(path);
            if (e == ".jpeg" || e == ".jfif") return "jpg";
            if (e == ".tif") return "tiff";
            if (e == ".heif") return "heic";
            return e.TrimStart('.');
        }
    }

    public class ActionOption
    {
        public string Id;
        public string Label;
        public string Sub;
        /// 있으면 누를 때 한 단계 더 고른다(AI로 → 요약 → 3줄/한 쪽).
        public List<ActionOption> Children;

        public ActionOption(string id, string label, string sub)
        {
            Id = id;
            Label = label;
            Sub = sub;
        }

        public ActionOption With(params ActionOption[] children)
        {
            Children = children.ToList();
            return this;
        }

        public bool HasChildren
        {
            get { return Children != null && Children.Count > 0; }
        }

        /// 이 고를 거리와 그 아래 고를 거리를 모두 편다.
        public IEnumerable<ActionOption> Flatten()
        {
            yield return this;
            if (Children == null) yield break;
            foreach (var c in Children)
            {
                foreach (var d in c.Flatten()) yield return d;
            }
        }
    }

    public class MenuAction
    {
        public string Id;
        public string Label;
        public string Hint;
        public Kind[] Accepts;

        public MenuAction(string id, string label, string hint, params Kind[] accepts)
        {
            Id = id;
            Label = label;
            Hint = hint;
            Accepts = accepts;
        }

        public bool IsSettings
        {
            get { return Id == "settings"; }
        }

        /// 놓은 파일 가운데 이 칸이 처리할 수 있는 것
        public List<string> Usable(IList<string> files)
        {
            var list = new List<string>();
            if (files == null) return list;
            foreach (var f in files)
            {
                if (Array.IndexOf(Accepts, Formats.KindOf(f)) < 0) continue;
                if (Id == "shrink" && !CanShrink(f)) continue;
                if (Id == "text" && !CanReadText(f)) continue;
                if (Id == "format" && Formats.KindOf(f) == Kind.Excel && !DocConvert.IsTable(f)) continue;
                if (Id == "pdf" && DocConvert.IsCue(f)) continue;
                list.Add(f);
            }
            // PDF 하나만 놓으면 'PDF로' 바꿀 것이 없다. 둘 이상이면 합칠 수 있다.
            if (Id == "pdf" && list.Count < 2 && list.All(f => Formats.KindOf(f) == Kind.Pdf)) list.Clear();
            return list;
        }

        /// 용량 줄이기: 그림·PDF, 그리고 사진을 꺼내 줄일 수 있는 압축형 문서(docx·xlsx·pptx·hwpx)
        static bool CanShrink(string f)
        {
            if (Formats.Ext(f) == ".svg") return false;
            switch (Formats.KindOf(f))
            {
                case Kind.Image:
                case Kind.Pdf:
                    return true;
            }
            string e = Formats.Ext(f);
            return e == ".docx" || e == ".xlsx" || e == ".pptx" || e == ".hwpx";
        }

        static bool CanReadText(string f)
        {
            if (Formats.Ext(f) == ".svg") return false;
            switch (Formats.KindOf(f))
            {
                case Kind.Image:
                case Kind.Pdf:
                case Kind.Hwp:
                    return true;
            }
            string e = Formats.Ext(f);
            return e == ".docx" || e == ".pptx" || e == ".xlsx" || e == ".ods" || e == ".odt";
        }

        public bool CanTake(IList<string> files)
        {
            if (IsSettings) return true;
            if (files == null || files.Count == 0) return true;
            return Usable(files).Count > 0;
        }

        static bool Any(IEnumerable<string> files, params Kind[] kinds)
        {
            return files.Any(f => Array.IndexOf(kinds, Formats.KindOf(f)) >= 0);
        }

        /// 놓은 뒤 한 번 더 고를 거리. 하나뿐이면 바로 실행한다.
        public List<ActionOption> OptionsFor(IList<string> files)
        {
            var usable = Usable(files);
            var opts = new List<ActionOption>();
            switch (Id)
            {
                case "pdf":
                    if (usable.Count >= 2)
                    {
                        opts.Add(new ActionOption("merge", "하나로", "모두 한 PDF에"));
                        opts.Add(new ActionOption("each", "따로따로", "파일마다 PDF"));
                    }
                    else opts.Add(new ActionOption("go", "PDF로", ""));
                    break;
                case "image":
                    opts.Add(new ActionOption("png", "PNG", "선명하게"));
                    opts.Add(new ActionOption("jpg", "JPG", "가볍게"));
                    if (usable.Any(HasPictures)) opts.Add(new ActionOption("extract", "속 사진만", "문서에 든 사진"));
                    break;
                case "format":
                    FormatOptions(usable, opts);
                    break;
                case "shrink":
                    opts.Add(new ActionOption("light", "조금", "긴 변 3000"));
                    opts.Add(new ActionOption("medium", "보통", "긴 변 2000"));
                    opts.Add(new ActionOption("strong", "많이", "긴 변 1400"));
                    opts.Add(new ActionOption("target", "용량 정하기", "○MB 이하로"));
                    break;
                case "text":
                    {
                        bool onlyDocs = usable.Count > 0 && !Any(usable, Kind.Image, Kind.Pdf);
                        if (onlyDocs)
                        {
                            opts.Add(new ActionOption("local", "글자로", ".txt 파일"));
                            break;
                        }
                        opts.Add(new ActionOption("local", "이 PC에서", "인터넷 안 씀"));
                        opts.Add(new ActionOption("docx", "워드로", "읽은 글을 문서로"));
                        if (Ai.Configured) opts.Add(new ActionOption("ai", "AI로", "표까지 읽기"));
                        break;
                    }
                case "photo":
                    opts.Add(new ActionOption("edit", "손질하기", "가리기·자르기·보정"));
                    opts.Add(new ActionOption("face", "얼굴 가리기", "찾아서 흐리게"));
                    opts.Add(new ActionOption("scan", "문서 펴기", "기울기·명암·흑백"));
                    if (usable.Count >= 2) opts.Add(new ActionOption("collage", "콜라주", "여러 장을 한 장에"));
                    if (usable.Any(f => Formats.Ext(f) != ".svg"))
                    {
                        opts.Add(new ActionOption("strip", "정보 지우기", "찍은 장소·기기"));
                        opts.Add(new ActionOption("report", "정보 보기", "글자 파일로 저장"));
                    }
                    break;
                case "pdftool":
                    opts.Add(new ActionOption("pages", "쪽 편집", "순서·돌리기·삭제"));
                    if (usable.Count >= 2) opts.Add(new ActionOption("merge", "합치기", "순서 정해서"));
                    opts.Add(new ActionOption("split", "쪽마다 나누기", "한 쪽씩 파일로"));
                    opts.Add(new ActionOption("pick", "쪽 고르기", "1,3-5처럼"));
                    opts.Add(new ActionOption("lock", "암호 걸기", "열 때 암호"));
                    opts.Add(new ActionOption("unlock", "암호 풀기", "암호를 알 때만"));
                    opts.Add(new ActionOption("watermark", "워터마크", "글자를 흐리게"));
                    opts.Add(new ActionOption("number", "쪽 번호", "쪽마다 번호"));
                    opts.Add(new ActionOption("strip", "정보 지우기", "만든 이·프로그램"));
                    break;
                case "ai":
                    AiOptions(usable, opts);
                    break;
                case "media":
                    MediaOptions(usable, opts);
                    break;
                case "archive":
                    if (usable.Any(f => Formats.KindOf(f) == Kind.Archive)) opts.Add(new ActionOption("unpack", "풀기", "새 폴더에"));
                    opts.Add(new ActionOption("zip", "ZIP", "하나로 묶기"));
                    opts.Add(new ActionOption("tar", "TAR", "압축 없이 묶기"));
                    if (usable.Count == 1 && Formats.KindOf(usable[0]) != Kind.Folder && Formats.KindOf(usable[0]) != Kind.Archive) opts.Add(new ActionOption("gz", "GZIP", "파일 하나 압축"));
                    break;
            }
            return opts;
        }

        static bool HasPictures(string f)
        {
            if (Formats.KindOf(f) == Kind.Pdf) return true;
            string e = Formats.Ext(f);
            return e == ".docx" || e == ".xlsx" || e == ".pptx" || e == ".hwpx" || e == ".hwp";
        }

        static void AddOnce(List<ActionOption> opts, ActionOption o)
        {
            if (!opts.Any(x => x.Id == o.Id)) opts.Add(o);
        }

        static void MediaOptions(List<string> usable, List<ActionOption> opts)
        {
            bool video = usable.Any(f => Formats.KindOf(f) == Kind.Video);
            bool audio = usable.Any(f => Formats.KindOf(f) == Kind.Audio);
            Func<string, bool> other = ext => usable.Any(f => Formats.Ext(f) != ext);
            if (video)
            {
                if (other(".mp4")) opts.Add(new ActionOption("mp4", "MP4", "어디서나 열려요"));
                opts.Add(new ActionOption("compress", "용량 줄이기", "화질을 조금 낮춰"));
                opts.Add(new ActionOption("edit", "영상 편집", "자르기·자막·비율"));
                opts.Add(new ActionOption("gif", "GIF", "움직이는 그림"));
                if (other(".mov")) opts.Add(new ActionOption("mov", "MOV", "맥·아이폰"));
                if (other(".mkv")) opts.Add(new ActionOption("mkv", "MKV", "영상 보관용"));
                opts.Add(new ActionOption("mp3", audio ? "MP3" : "소리만 MP3", ""));
                opts.Add(new ActionOption("wav", audio ? "WAV" : "소리만 WAV", ""));
                return;
            }
            if (!audio) return;
            if (other(".mp3")) opts.Add(new ActionOption("mp3", "MP3", "어디서나 열려요"));
            if (other(".m4a")) opts.Add(new ActionOption("m4a", "M4A", "작고 깨끗해요"));
            if (other(".wav")) opts.Add(new ActionOption("wav", "WAV", "압축 없이"));
            if (other(".flac")) opts.Add(new ActionOption("flac", "FLAC", "음질 그대로"));
            opts.Add(new ActionOption("trim", "자르기", "앞뒤를 잘라내요"));
        }

        static ActionOption ToWord()
        {
            return new ActionOption("docx", "워드로", "");
        }

        static ActionOption ToMd()
        {
            return new ActionOption("md", "마크다운", "글자와 표");
        }

        static ActionOption ToHtml()
        {
            return new ActionOption("html", "HTML", "웹 문서");
        }

        static ActionOption ToBook()
        {
            return new ActionOption("epub", "EPUB", "전자책");
        }

        static ActionOption ToTxt()
        {
            return new ActionOption("txt", "글자 파일", ".txt");
        }

        static ActionOption ToXlsxOpt()
        {
            return new ActionOption("xlsx", "엑셀로", "");
        }

        static ActionOption ToCsvOpt()
        {
            return new ActionOption("csv", "CSV", "쉼표로 나눈 표");
        }

        static ActionOption ToTsvOpt()
        {
            return new ActionOption("tsv", "TSV", "탭으로 나눈 표");
        }

        static ActionOption ToOdsOpt()
        {
            return new ActionOption("ods", "ODS", "오픈 문서 표");
        }

        static void FormatOptions(List<string> usable, List<ActionOption> opts)
        {
            var images = usable.Where(f => Formats.KindOf(f) == Kind.Image).ToList();
            if (images.Count > 0)
            {
                var have = new HashSet<string>(images.Select(f => Formats.ImageFormatOf(f)));
                foreach (var fmt in new[] { "png", "jpg", "webp", "bmp", "gif", "tiff", "ico" })
                {
                    if (have.Count == 1 && have.Contains(fmt)) continue;
                    opts.Add(new ActionOption(fmt, fmt.ToUpperInvariant(), fmt == "webp" ? "손실 없이" : ""));
                }
                opts.Add(new ActionOption("docx", "워드로", "사진을 문서에"));
                opts.Add(new ActionOption("pptx", "PPT로", "사진마다 슬라이드"));
            }
            if (usable.Any(f => Formats.Ext(f) == ".hwp")) opts.Add(new ActionOption("hwpx", "HWPX", "새 한글 형식"));
            if (usable.Any(f => Formats.Ext(f) == ".hwpx")) opts.Add(new ActionOption("hwp", "HWP", "예전 한글 형식"));
            if (Any(usable, Kind.Hwp))
            {
                AddOnce(opts, new ActionOption("docx", "워드로", "한글 프로그램 필요"));
                AddOnce(opts, new ActionOption("xlsx", "엑셀로", "문서 안의 표만"));
            }
            if (Any(usable, Kind.Pdf))
            {
                AddOnce(opts, new ActionOption("docx", "워드로", "글자·표"));
                AddOnce(opts, new ActionOption("xlsx", "엑셀로", "PDF 안의 표"));
                AddOnce(opts, new ActionOption("pptx", "PPT로", "글자 고칠 수 있게"));
                AddOnce(opts, new ActionOption("pptximg", "그림 PPT", "모양 그대로"));
            }
            if (Any(usable, Kind.Hwp, Kind.Word)) AddOnce(opts, new ActionOption("pptx", "PPT로", "글자 고칠 수 있게"));
            if (usable.Any(f => Formats.Ext(f) == ".odt")) AddOnce(opts, ToWord());
            if (Any(usable, Kind.Pdf, Kind.Hwp, Kind.Word))
            {
                AddOnce(opts, ToMd());
                AddOnce(opts, ToHtml());
                AddOnce(opts, ToBook());
            }
            if (Any(usable, Kind.Word)) AddOnce(opts, ToTxt());
            foreach (var f in usable.Where(x => Formats.KindOf(x) == Kind.Text))
            {
                switch (Formats.Ext(f))
                {
                    case ".srt":
                        AddOnce(opts, new ActionOption("vtt", "VTT", "웹 자막"));
                        AddOnce(opts, ToTxt());
                        break;
                    case ".vtt":
                        AddOnce(opts, new ActionOption("srt", "SRT", "영상 자막"));
                        AddOnce(opts, ToTxt());
                        break;
                    case ".tsv":
                        AddOnce(opts, ToXlsxOpt());
                        AddOnce(opts, ToCsvOpt());
                        AddOnce(opts, ToOdsOpt());
                        AddOnce(opts, ToTxt());
                        break;
                    case ".html":
                    case ".htm":
                        AddOnce(opts, ToWord());
                        AddOnce(opts, ToMd());
                        AddOnce(opts, ToBook());
                        AddOnce(opts, ToTxt());
                        break;
                    case ".md":
                    case ".markdown":
                        AddOnce(opts, ToWord());
                        AddOnce(opts, ToHtml());
                        AddOnce(opts, ToBook());
                        AddOnce(opts, ToTxt());
                        break;
                    case ".epub":
                        AddOnce(opts, ToWord());
                        AddOnce(opts, ToMd());
                        AddOnce(opts, ToHtml());
                        AddOnce(opts, ToTxt());
                        break;
                    default:
                        AddOnce(opts, ToWord());
                        AddOnce(opts, ToMd());
                        AddOnce(opts, ToHtml());
                        AddOnce(opts, ToBook());
                        break;
                }
            }
            foreach (var f in usable.Where(x => Formats.KindOf(x) == Kind.Excel))
            {
                string e = Formats.Ext(f);
                if (e != ".xlsx" && e != ".xlsm") AddOnce(opts, ToXlsxOpt());
                if (e != ".csv") AddOnce(opts, ToCsvOpt());
                AddOnce(opts, ToTsvOpt());
                if (e != ".ods") AddOnce(opts, ToOdsOpt());
                AddOnce(opts, ToTxt());
            }
        }

        static void AiOptions(List<string> usable, List<ActionOption> opts)
        {
            opts.Add(new ActionOption("summary", "요약", "3줄·한 쪽").With(
                new ActionOption("sum3", "3줄로", "핵심만"),
                new ActionOption("sum1", "한 쪽으로", "항목으로 정리")));
            var office = new List<ActionOption>
            {
                new ActionOption("gist", "요점", "마감·제출·할 일"),
                new ActionOption("schedule", "일정 뽑기", "엑셀·달력 파일")
            };
            if (usable.Count >= 2) office.Add(new ActionOption("todo", "할 일표", "마감순으로 한 장"));
            opts.Add(new ActionOption("office", "요점 정리", "마감·할 일·일정").With(office.ToArray()));
            opts.Add(new ActionOption("translate", "번역", "여러 언어").With(
                new ActionOption("en", "영어", "English"),
                new ActionOption("zh", "중국어", "中文"),
                new ActionOption("ja", "일본어", "日本語"),
                new ActionOption("vi", "베트남어", "Tiếng Việt"),
                new ActionOption("ru", "러시아어", "Русский"),
                new ActionOption("th", "태국어", "ไทย"),
                new ActionOption("mn", "몽골어", "Монгол"),
                new ActionOption("uz", "우즈베크어", "O'zbekcha"),
                new ActionOption("ko", "한국어로", "외국어 문서를")));
            // 초안은 칸을 늘리지 않게 이 칸 안에 둔다. 표 파일만 놓았을 때는 초안을 쓸 거리가 없다.
            bool drafts = usable.Any(f => Formats.KindOf(f) != Kind.Excel);
            var write = new List<ActionOption>();
            if (drafts)
            {
                write.Add(new ActionOption("reply", "기안문", "공문에 답하기"));
                write.Add(new ActionOption("planning", "계획서", "목적·방침·세부 계획"));
                write.Add(new ActionOption("report", "결과 보고서", "추진 내용·성과"));
                write.Add(new ActionOption("letter", "가정 통신문", "학부모 안내"));
            }
            write.Add(new ActionOption("plain", "쉬운 말로", "학부모·학생용"));
            write.Add(new ActionOption("spell", "맞춤법", "고친 곳 표시"));
            opts.Add(new ActionOption("rewrite", drafts ? "문서 쓰기" : "글 다듬기", drafts ? "초안·쉬운 말·맞춤법" : "쉬운 말·맞춤법").With(write.ToArray()));
            opts.Add(new ActionOption("table", "표를 엑셀로", "문서·사진 속 표"));
            opts.Add(new ActionOption("quiz", "문제 만들기", "정답까지").With(
                new ActionOption("mc", "객관식", "5지선다"),
                new ActionOption("ox", "OX", "참·거짓"),
                new ActionOption("essay", "서술형", "채점 기준까지"),
                new ActionOption("mix", "섞어서", "세 가지 모두"),
                new ActionOption("similar", "비슷한 문제", "시험지를 새 문제로")));
            opts.Add(new ActionOption("material", "수업 자료", "학습지·PPT·지도안").With(
                new ActionOption("worksheet", "학습지", "워드·한글"),
                new ActionOption("slides", "PPT 만들기", "디자인 입혀서"),
                new ActionOption("levels", "수준별 자료", "기초·보통·심화"),
                new ActionOption("lesson", "지도안", "단계별 수업 계획"),
                new ActionOption("rubric", "채점 기준표", "상·중·하")));
            if (Any(usable, Kind.Image, Kind.Pdf)) opts.Add(new ActionOption("hand", "손글씨 읽기", "사진·스캔"));
            opts.Add(new ActionOption("rename", "이름 짓기", "내용 보고 파일 이름"));
        }

        public static readonly List<MenuAction> All = new List<MenuAction>
        {
            new MenuAction("pdf", "PDF로", "문서·그림을 PDF로", Kind.Hwp, Kind.Word, Kind.Excel, Kind.Ppt, Kind.Image, Kind.Pdf, Kind.Text),
            new MenuAction("image", "그림으로", "쪽마다 그림 파일", Kind.Pdf, Kind.Hwp, Kind.Word, Kind.Excel, Kind.Ppt),
            new MenuAction("format", "형식 바꾸기", "워드·엑셀·PNG…", Kind.Image, Kind.Hwp, Kind.Word, Kind.Pdf, Kind.Text, Kind.Excel),
            new MenuAction("shrink", "용량 줄이기", "그림·PDF·문서", Kind.Image, Kind.Pdf, Kind.Word, Kind.Excel, Kind.Ppt, Kind.Hwp),
            new MenuAction("text", "글자 뽑기", "그림·PDF·문서", Kind.Image, Kind.Pdf, Kind.Hwp, Kind.Word, Kind.Ppt, Kind.Excel),
            new MenuAction("photo", "사진 손질", "가리기·자르기·보정", Kind.Image),
            new MenuAction("pdftool", "PDF 편집", "쪽·암호·워터마크", Kind.Pdf),
            new MenuAction("media", "영상·소리", "변환·편집·줄이기", Kind.Video, Kind.Audio),
            new MenuAction("record", "화면 녹화", "내 화면을 영상으로"),
            new MenuAction("archive", "압축", "묶기·풀기", Kind.Unknown, Kind.Hwp, Kind.Word, Kind.Excel, Kind.Ppt, Kind.Pdf, Kind.Image, Kind.Text, Kind.Archive, Kind.Folder, Kind.Video, Kind.Audio),
            new MenuAction("ai", "AI로", "요약·번역·문제", Kind.Pdf, Kind.Hwp, Kind.Word, Kind.Excel, Kind.Ppt, Kind.Image, Kind.Text),
            new MenuAction("settings", "설정", "모양·키·AI")
        };

        /// 메뉴에 보일 차례(설정에서 고른 순서, 숨긴 칸 빼기). 설정 칸은 늘 맨 끝.
        public static List<MenuAction> Ordered()
        {
            var s = AppSettings.Current;
            var hidden = new HashSet<string>(s.HiddenSlots ?? new List<string>());
            var order = (s.SlotOrder ?? new List<string>()).ToList();
            var usage = s.SlotUsage ?? new Dictionary<string, int>();
            var list = All.Where(a => a.IsSettings || !hidden.Contains(a.Id)).ToList();
            Func<MenuAction, int> rank = a =>
            {
                int i = order.IndexOf(a.Id);
                return i >= 0 ? i : 1000 + All.IndexOf(a);
            };
            Func<MenuAction, int> used = a =>
            {
                int n;
                return usage.TryGetValue(a.Id, out n) ? n : 0;
            };
            IEnumerable<MenuAction> sorted = s.AutoOrder
                ? list.OrderBy(a => a.IsSettings ? 1 : 0).ThenByDescending(used).ThenBy(rank)
                : list.OrderBy(a => a.IsSettings ? 1 : 0).ThenBy(rank);
            return sorted.ToList();
        }
    }
}
