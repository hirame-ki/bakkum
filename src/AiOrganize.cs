// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 'AI로' 칸의 폴더 정리: 폴더 바로 안의 파일을 비슷한 것끼리 하위 폴더로 나누고, 이름을 알아보기 쉽게 다듬는다.
// AI에게는 파일 이름과 고친 날짜만 보낸다(내용은 보내지 않는다). 옮기기 전에 고칠 수 있는 창으로 한 번 더 묻는다.
// 지우거나 덮어쓰지 않고, 고른 폴더 밖으로는 옮기지 않는다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Bakkum
{
    sealed class OrganizePlan
    {
        public string File, Folder, Name;
        public TextBox FolderBox, NameBox;
    }

    public static partial class AiTasks
    {
        const int MaxOrganize = 300;

        public static bool IsOrganize(string task)
        {
            return task == "orgsort" || task == "orgname" || task == "orgboth";
        }

        /// 정리할 파일: 폴더 바로 안의 파일. 숨긴·시스템 파일, 바로 가기, 열린 문서의 임시 파일(~$)은 뺀다.
        static List<string> OrganizeFiles(string dir)
        {
            var list = new List<string>();
            foreach (var f in Directory.GetFiles(dir))
            {
                string e = Formats.Ext(f);
                if (Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal) || e == ".lnk" || e == ".url" || e == ".tmp") continue;
                try
                {
                    if ((File.GetAttributes(f) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                list.Add(f);
            }
            list.Sort(Util.NaturalCompare);
            return list;
        }

        static string OrganizePrompt(bool sort, bool rename, bool hasFolders)
        {
            var sb = new StringBuilder("The document lists the files in one folder on a Korean teacher's PC, one per line: a number, the file name and the date it was last changed.");
            if (hasFolders) sb.Append(" The first line names the subfolders that are already there.");
            if (sort) sb.Append(" Sort the files into subfolders by what they are for, such as a subject, an event, a kind of document or a school term. Use 2 to 8 short Korean folder names of at most 15 characters, reuse a folder that is already there when it fits, and give an empty folder to a file that fits no group so that it stays where it is.");
            if (rename) sb.Append(" Suggest a clearer Korean name for each file without the extension: keep its meaning and every date, number and proper noun, remove clutter such as (1), 복사본, 최종 or long random numbers, and keep the name as it is when it is already clear. Never add details that the name does not give.");
            sb.Append(" Reply with JSON only: an array with one object per file, with these keys: n (the file number)");
            if (sort) sb.Append(", folder (the folder name, or an empty string)");
            if (rename) sb.Append(", name (the new name)");
            return sb.Append('.').ToString();
        }

        static void Organize(Job job, string dir)
        {
            string task = job.Option.Id;
            bool sort = task != "orgname", rename = task != "orgsort";
            List<string> files;
            var subs = new List<string>();
            try
            {
                files = OrganizeFiles(dir);
                if (sort)
                {
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        if ((new DirectoryInfo(d).Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0) subs.Add(Path.GetFileName(d));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                throw new UserFacingException("이 폴더는 열어 볼 수 없어요.");
            }
            if (files.Count == 0)
            {
                job.Problem(dir, "정리할 파일이 없어요.");
                return;
            }
            if (files.Count > MaxOrganize)
            {
                job.Note(Util.ShortName(dir) + ": 파일이 많아서 이름 순으로 앞 " + MaxOrganize + "개만 정리해요.");
                files = files.Take(MaxOrganize).ToList();
            }
            var sb = new StringBuilder();
            if (subs.Count > 0) sb.Append("이미 있는 폴더: ").Append(string.Join(", ", subs)).Append("\n\n");
            for (int i = 0; i < files.Count; i++)
            {
                sb.Append(i + 1).Append('\t').Append(Path.GetFileName(files[i])).Append('\t').Append(File.GetLastWriteTime(files[i]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
            }
            string text = sb.ToString();
            var masker = new Masker();
            masker.Learn(text);
            var plan = new SendPlan
            {
                Files = files.Count,
                Chars = text.Length,
                Plain = text,
                Masked = masker.Apply(text),
                Names = masker.Names,
                Phones = masker.Phones,
                Others = masker.Others,
                FileNames = true
            };
            Masker m = AskToSend(job, plan).Mask ? masker : null;

            job.Report(Util.ShortName(dir) + " AI가 정리하는 중", -1);
            var list = AskJson(job, Rule(OrganizePrompt(sort, rename, subs.Count > 0), m), m == null ? plan.Plain : plan.Masked, '[', ']', m) as object[];
            if (list == null) throw new AiBadAnswer("AI 답을 읽지 못했어요. 다시 해 보세요.");
            var plans = new List<OrganizePlan>();
            var seen = new HashSet<int>();
            foreach (object o in list)
            {
                var d = Json.Obj(o);
                int n = d == null ? 0 : Json.I(d, "n");
                if (n < 1 || n > files.Count || !seen.Add(n)) continue;
                string f = files[n - 1];
                var p = new OrganizePlan { File = f, Folder = sort ? CleanName(Json.S(d, "folder")) : "", Name = NewStem(rename ? Json.S(d, "name") : null, f) };
                if (p.Folder.Length == 0 && p.Name == Path.GetFileNameWithoutExtension(f)) continue;
                plans.Add(p);
            }
            if (plans.Count == 0)
            {
                job.Note(Util.ShortName(dir) + ": AI가 보기에 이미 잘 정리돼 있어요.");
                return;
            }
            // 같은 폴더로 갈 것끼리 모아 보이고, 그 자리에서 이름만 바뀌는 것은 뒤에 둔다.
            plans = plans.OrderBy(p => p.Folder.Length == 0 ? 1 : 0).ThenBy(p => p.Folder, StringComparer.CurrentCulture).ToList();
            if (!job.Ask(() => ConfirmOrganize(plans, sort, rename), job.AutoYes)) throw new OperationCanceledException();
            ApplyOrganize(job, dir, plans);
        }

        /// 새 이름(확장자 뺀 것). 비었거나 원래 이름과 같으면 원래 이름 그대로 둔다(다듬으면 40자로 잘리므로 고친 이름만 다듬는다).
        static string NewStem(string given, string file)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            given = (given ?? "").Trim();
            if (given.Length == 0 || string.Equals(given, stem, StringComparison.OrdinalIgnoreCase) || string.Equals(given, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) return stem;
            string c = CleanName(given);
            return c.Length == 0 || string.Equals(c, stem, StringComparison.OrdinalIgnoreCase) ? stem : c;
        }

        /// 그림 확인용(/render-window organize [state:sort|name])
        public static Window OrganizePreview(string state)
        {
            bool sort = state != "name", rename = state != "sort";
            var rows = new[]
            {
                new[] { "3월 가정통신문 (1).hwp", "가정통신문", "3월 가정통신문" },
                new[] { "현장체험학습 안내문 최종.hwp", "가정통신문", "현장체험학습 안내문" },
                new[] { "과학 5단원 수업 최종_진짜.pptx", "수업 자료", "과학 5단원 수업" },
                new[] { "KakaoTalk_20261102_093011.jpg", "사진", "KakaoTalk_20261102_093011" },
                new[] { "학급 규칙 복사본.docx", "", "학급 규칙" }
            };
            var plans = new List<OrganizePlan>();
            foreach (var r in rows)
            {
                var p = new OrganizePlan { File = @"C:\받은 파일\" + r[0], Folder = sort ? r[1] : "", Name = rename ? r[2] : Path.GetFileNameWithoutExtension(r[0]) };
                if (p.Folder.Length > 0 || p.Name != Path.GetFileNameWithoutExtension(r[0])) plans.Add(p);
            }
            string title, body;
            var list = OrganizeList(plans, sort, rename, out title, out body);
            return Dialog.Build(null, Theme.Sky, "ai", title, body, list, "취소", "정리하기");
        }

        static bool ConfirmOrganize(List<OrganizePlan> plans, bool sort, bool rename)
        {
            string title, body;
            var list = OrganizeList(plans, sort, rename, out title, out body);
            if (Dialog.Ask(null, Theme.Sky, "ai", title, body, list, "취소", "정리하기") != 1) return false;
            foreach (var p in plans)
            {
                if (p.FolderBox != null) p.Folder = p.FolderBox.Text;
                if (p.NameBox != null) p.Name = p.NameBox.Text;
            }
            return true;
        }

        /// 확인 창 안쪽: 파일마다 옮길 폴더·새 이름 칸. 그림 확인에서도 부른다.
        static FrameworkElement OrganizeList(List<OrganizePlan> plans, bool sort, bool rename, out string title, out string body)
        {
            var stack = new StackPanel();
            foreach (var p in plans)
            {
                var from = Theme.MetaLabel(Path.GetFileName(p.File));
                from.Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 12, 0, 4);
                stack.Children.Add(from);
                var row = new Grid();
                if (sort)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = rename ? new GridLength(150) : new GridLength(1, GridUnitType.Star) });
                    p.FolderBox = new TextBox { Style = Theme.GetStyle("TextField"), Text = p.Folder, Height = 40, Tag = "그대로 두기" };
                    row.Children.Add(p.FolderBox);
                }
                if (rename)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    p.NameBox = new TextBox { Style = Theme.GetStyle("TextField"), Text = p.Name, Height = 40, Tag = "새 이름" };
                    if (sort) p.NameBox.Margin = new Thickness(8, 0, 0, 0);
                    Grid.SetColumn(p.NameBox, row.ColumnDefinitions.Count - 1);
                    row.Children.Add(p.NameBox);
                }
                stack.Children.Add(row);
            }
            int groups = plans.Select(p => p.Folder).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            title = sort && rename ? "이렇게 정리할까요?" : sort ? "폴더 " + groups + "개로 나눌까요?" : "이 이름으로 바꿀까요?";
            body = (sort && rename ? "왼쪽 칸이 옮길 폴더예요. " : "") + (sort ? "폴더 칸을 비우면 그 파일은 그대로 둬요. " : "") + "고칠 곳은 고쳐 주세요. 지우거나 덮어쓰지 않아요.";
            return new ScrollViewer { Content = stack, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        static void ApplyOrganize(Job job, string dir, List<OrganizePlan> plans)
        {
            int moved = 0, renamed = 0;
            var folders = new List<string>();
            foreach (var p in plans)
            {
                // 고친 칸도 다시 거른다. \ / 같은 글자와 CON·NUL 같은 장치 이름을 지우므로 고른 폴더 밖으로 가거나 사라지지 않는다.
                string folder = CleanName(p.Folder), name = NewStem(p.Name, p.File);
                bool sameName = name == Path.GetFileNameWithoutExtension(p.File);
                if (folder.Length == 0 && sameName) continue;
                string target = folder.Length == 0 ? dir : Path.Combine(dir, folder);
                try
                {
                    Directory.CreateDirectory(target);
                    string dst = Util.UniquePath(Path.Combine(target, sameName ? Path.GetFileName(p.File) : name + Path.GetExtension(p.File)));
                    File.Move(p.File, dst);
                    if (!sameName) renamed++;
                    if (folder.Length == 0) job.Output(dst);
                    else
                    {
                        moved++;
                        if (!folders.Contains(target, StringComparer.OrdinalIgnoreCase)) folders.Add(target);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    job.Problem(p.File, "이 폴더에서는 옮기거나 이름을 바꿀 수 없어요.");
                }
                catch (IOException)
                {
                    job.Problem(p.File, folder.Length == 0 ? "이름을 바꾸지 못했어요. 파일이 열려 있는지 확인해 주세요." : "옮기지 못했어요. 파일이 열려 있거나 같은 이름의 파일이 있는지 확인해 주세요: " + folder);
                }
                catch (ArgumentException)
                {
                    job.Problem(p.File, "폴더 이름으로 쓸 수 없어요: " + folder);
                }
                catch (NotSupportedException)
                {
                    job.Problem(p.File, "폴더 이름으로 쓸 수 없어요: " + folder);
                }
            }
            foreach (var f in folders) job.Output(f);
            var done = new List<string>();
            if (moved > 0) done.Add("파일 " + moved + "개를 폴더 " + folders.Count + "개로 나눴어요");
            if (renamed > 0) done.Add("이름 " + renamed + "개를 바꿨어요");
            if (done.Count > 0) job.Note(Util.ShortName(dir) + ": " + string.Join(" · ", done) + ".");
        }
    }
}
