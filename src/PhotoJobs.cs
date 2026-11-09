// 사진 손질 칸에서 시작한 일을 실제로 처리한다(화면 없이 돌아간다).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Bakkum
{
    public static partial class Conversions
    {
        static void Photo(Job job, List<string> files)
        {
            switch (job.Option.Id)
            {
                case "save": PhotoSaveAll(job); break;
                case "collage": CollageSaveAll(job); break;
                case "strip": PhotoStripAll(job, files); break;
                case "report": PhotoReportAll(job, files); break;
                default: throw new UserFacingException("이 일은 창에서 해 주세요.");
            }
        }

        static void PhotoSaveAll(Job job)
        {
            var save = job.Payload as PhotoSave;
            if (save == null) throw new UserFacingException("저장할 내용이 없어요.");
            var byPath = save.Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
            EachFile(job, save.Items.Select(i => i.Path).ToList(), "저장하는 중", delegate (string f)
            {
                var edit = byPath[f].Edit;
                if (Imaging.PageCount(f) > 1) job.Note(Util.ShortName(f) + ": 여러 쪽짜리 TIFF는 첫 쪽만 손질했어요.");
                var img = PhotoRender.Render(Imaging.Load(f), edit);
                bool transparent = edit.HasFrame && edit.BackTransparent;
                string fmt = save.Format == "same" ? PhotoRender.SaveFormatFor(f, transparent) : save.Format;
                if (transparent && fmt == "jpg") job.Note(Util.ShortName(f) + ": JPG에는 투명한 바탕이 없어서 흰색으로 저장했어요.");
                byte[] data = PhotoRender.Encode(img, fmt, 92);
                Produce(job, Sibling(f, "_손질", PhotoRender.ExtensionOf(fmt)), delegate (string d) { File.WriteAllBytes(d, data); });
            });
        }

        static void CollageSaveAll(Job job)
        {
            var save = job.Payload as CollageSave;
            if (save == null) throw new UserFacingException("저장할 내용이 없어요.");
            job.Report("사진을 모으는 중", -1);
            int perPhoto = save.Spec.LongSide;
            var photos = new List<System.Windows.Media.Imaging.BitmapSource>();
            foreach (var f in save.Order)
            {
                job.Check();
                try
                {
                    photos.Add(Imaging.Fit(Imaging.Load(f), perPhoto));
                }
                catch (UserFacingException ex)
                {
                    job.Problem(f, ex.Message);
                }
            }
            if (photos.Count < 2) throw new UserFacingException("콜라주에 넣을 사진이 두 장도 안 돼요.");
            var img = Collage.Render(photos, save.Spec, save.Spec.LongSide);
            byte[] data = PhotoRender.Encode(img, save.Format, 92);
            string first = save.Order[0];
            string dst = Util.UniquePath(Path.Combine(Util.OutputDirFor(first), Path.GetFileNameWithoutExtension(first) + " 외 " + (photos.Count - 1) + "장_콜라주." + save.Format));
            Produce(job, dst, delegate (string d) { File.WriteAllBytes(d, data); });
        }

        /// 사진 안의 정보를 글자 파일로 적는다. 찍은 장소가 들어 있을 수 있어 사용자가 고를 때만 만든다.
        static void PhotoReportAll(Job job, List<string> files)
        {
            EachFile(job, files, "사진 정보 읽는 중", delegate (string f)
            {
                if (Formats.Ext(f) == ".svg")
                {
                    job.Note(Util.ShortName(f) + ": SVG에는 사진 정보가 없어요.");
                    return;
                }
                var rows = PhotoInfo.Read(f);
                var sb = new StringBuilder();
                sb.Append("사진 정보\r\n").Append("파일: ").Append(Path.GetFileName(f)).Append("\r\n\r\n");
                foreach (var r in rows) sb.Append(r.Label).Append(": ").Append(r.Value).Append(r.Sensitive ? "  (알려지면 곤란할 수 있는 정보)" : "").Append("\r\n");
                if (rows.Any(r => r.Sensitive)) sb.Append("\r\n이 글자 파일을 다른 사람에게 보내면 위 정보도 함께 전해져요.\r\n");
                Produce(job, Sibling(f, "_사진정보", ".txt"), delegate (string d) { File.WriteAllText(d, sb.ToString(), new UTF8Encoding(true)); });
            });
        }

        static void PhotoStripAll(Job job, List<string> files)
        {
            EachFile(job, files, "정보 지우는 중", delegate (string f)
            {
                if (Formats.Ext(f) == ".svg")
                {
                    job.Note(Util.ShortName(f) + ": SVG에는 지울 사진 정보가 없어요.");
                    return;
                }
                string ext;
                byte[] data = PhotoStrip.Run(f, out ext);
                if (data == null)
                {
                    job.Note(Util.ShortName(f) + ": 이 형식에는 지울 사진 정보가 없어요.");
                    return;
                }
                Produce(job, Sibling(f, "_정보지움", ext), delegate (string d) { File.WriteAllBytes(d, data); });
            });
        }
    }
}
