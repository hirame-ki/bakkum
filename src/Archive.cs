// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 압축 칸: ZIP·TAR·GZIP으로 묶고, 새 폴더에 푼다.
// 파일 안의 이름은 믿지 않는다. 폴더 밖으로 나가는 경로, 링크, 이름이 겹치는 항목은 풀지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Bakkum
{
    public static class Crc32
    {
        static readonly uint[] Table = Make();

        static uint[] Make()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        public static uint Update(uint crc, byte[] data, int offset, int count)
        {
            uint c = ~crc;
            for (int i = offset; i < offset + count; i++) c = Table[(c ^ data[i]) & 255] ^ (c >> 8);
            return ~c;
        }

        public static uint Of(byte[] data)
        {
            return Update(0, data, 0, data.Length);
        }
    }

    /// 전자책·ODS처럼 첫 항목을 정말로 압축하지 않아야 하는 파일을 쓴다.
    /// (이 .NET의 ZipArchive는 '압축 안 함'을 골라도 deflate 형식으로 적어서 규격에 맞지 않는다.)
    public sealed class StoredFirstZip : IDisposable
    {
        sealed class Item
        {
            public byte[] Name;
            public uint Crc, Size, Packed, Offset;
            public ushort Method;
        }

        readonly Stream output;
        readonly List<Item> items = new List<Item>();
        readonly ushort time, date;
        bool closed;

        public StoredFirstZip(Stream s)
        {
            output = s;
            var n = DateTime.Now;
            time = (ushort)((n.Hour << 11) | (n.Minute << 5) | (n.Second / 2));
            date = (ushort)(((n.Year - 1980) << 9) | (n.Month << 5) | n.Day);
        }

        void U16(BinaryWriter w, int v)
        {
            w.Write((ushort)v);
        }

        void Header(BinaryWriter w, Item it)
        {
            U16(w, 20);
            U16(w, 0x0800);
            U16(w, it.Method);
            U16(w, time);
            U16(w, date);
            w.Write(it.Crc);
            w.Write(it.Packed);
            w.Write(it.Size);
            U16(w, it.Name.Length);
            U16(w, 0);
        }

        public void AddText(string name, string text)
        {
            Add(name, new UTF8Encoding(false).GetBytes(text), false);
        }

        public void Add(string name, byte[] data, bool stored)
        {
            byte[] packed = data;
            ushort method = 0;
            if (!stored)
            {
                using (var m = new MemoryStream())
                {
                    using (var d = new DeflateStream(m, CompressionLevel.Optimal, true)) d.Write(data, 0, data.Length);
                    if (m.Length < data.Length)
                    {
                        packed = m.ToArray();
                        method = 8;
                    }
                }
            }
            var it = new Item { Name = new UTF8Encoding(false).GetBytes(name), Crc = Crc32.Of(data), Size = (uint)data.Length, Packed = (uint)packed.Length, Offset = (uint)output.Position, Method = method };
            items.Add(it);
            var w = new BinaryWriter(output, Encoding.ASCII, true);
            w.Write(0x04034B50u);
            Header(w, it);
            w.Write(it.Name);
            w.Write(packed);
            w.Flush();
        }

        public void Dispose()
        {
            if (closed) return;
            closed = true;
            var w = new BinaryWriter(output, Encoding.ASCII, true);
            uint start = (uint)output.Position;
            foreach (var it in items)
            {
                w.Write(0x02014B50u);
                U16(w, 20);
                Header(w, it);
                U16(w, 0);
                U16(w, 0);
                U16(w, 0);
                w.Write(0u);
                w.Write(it.Offset);
                w.Write(it.Name);
            }
            uint size = (uint)output.Position - start;
            w.Write(0x06054B50u);
            U16(w, 0);
            U16(w, 0);
            U16(w, items.Count);
            U16(w, items.Count);
            w.Write(size);
            w.Write(start);
            U16(w, 0);
            w.Flush();
        }
    }

    public static class ArchiveJobs
    {
        const long MaxBytes = 4L * 1024 * 1024 * 1024;
        const int MaxFiles = 20000;
        const int BufferSize = 1 << 16;
        static readonly HashSet<string> Packed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif", ".zip", ".gz", ".tgz", ".7z", ".rar",
            ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv",
            ".docx", ".xlsx", ".pptx", ".hwpx", ".pdf", ".epub", ".ods", ".odt"
        };

        public static bool IsFolder(string f)
        {
            return f.EndsWith("\\", StringComparison.Ordinal);
        }

        public static bool IsArchive(string f)
        {
            return Formats.KindOf(f) == Kind.Archive;
        }

        public static void Run(Job job, List<string> files)
        {
            switch (job.Option.Id)
            {
                case "unpack": Unpack(job, files.Where(IsArchive).ToList()); break;
                case "zip": PackAll(job, files, MakeZip, ".zip"); break;
                case "tar": PackAll(job, files, MakeTar, ".tar"); break;
                case "gz": GzipOne(job, files); break;
                default: throw new UserFacingException("아직 없는 기능이에요.");
            }
        }

        // ── 묶기 ──

        sealed class PackItem
        {
            public string Source;
            public string Name;
            public bool Dir;
            public DateTime Time;
            public long Size;
        }

        delegate void Maker(Job job, List<PackItem> items, string dst, long total);

        static string UniqueTop(HashSet<string> used, string name)
        {
            string ext = Path.GetExtension(name);
            string stem = name.Substring(0, name.Length - ext.Length);
            string c = name;
            for (int i = 2; !used.Add(c); i++) c = stem + " (" + i + ")" + ext;
            return c;
        }

        static bool Junk(string name)
        {
            return string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase) || string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase);
        }

        static void Walk(Job job, string dir, string rel, List<PackItem> items, ref int skipped)
        {
            job.Check();
            var di = new DirectoryInfo(dir);
            items.Add(new PackItem { Source = dir, Name = rel, Dir = true, Time = di.LastWriteTime });
            List<FileSystemInfo> inner;
            try
            {
                inner = di.GetFileSystemInfos().ToList();
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
                return;
            }
            inner.Sort((a, b) => Util.NaturalCompare(a.Name, b.Name));
            foreach (var e in inner)
            {
                // 바로가기처럼 다른 곳을 가리키는 폴더는 따라가지 않는다(끝없이 돌거나 다른 자료가 섞일 수 있다).
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped++;
                    continue;
                }
                if (Junk(e.Name)) continue;
                string name = rel + "/" + e.Name;
                if (Util.TooLong(e.FullName))
                {
                    skipped++;
                    continue;
                }
                if ((e.Attributes & FileAttributes.Directory) != 0) Walk(job, e.FullName, name, items, ref skipped);
                else items.Add(new PackItem { Source = e.FullName, Name = name, Time = e.LastWriteTime, Size = ((FileInfo)e).Length });
            }
        }

        static void PackAll(Job job, List<string> files, Maker make, string ext)
        {
            var folders = files.Where(IsFolder).ToList();
            // 폴더를 놓으면 그 안의 파일도 목록에 함께 있다. 폴더째 묶으니 따로 넣지 않는다.
            var plain = files.Where(f => !IsFolder(f) && !folders.Any(d => f.StartsWith(d, StringComparison.OrdinalIgnoreCase))).ToList();
            var items = new List<PackItem>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            string first = null;
            int tops = 0;
            foreach (string folder in folders)
            {
                string path = folder.TrimEnd('\\');
                if (first == null) first = path;
                tops++;
                Walk(job, path, UniqueTop(used, Path.GetFileName(path)), items, ref skipped);
            }
            foreach (string f in plain)
            {
                if (first == null) first = f;
                tops++;
                var fi = new FileInfo(f);
                items.Add(new PackItem { Source = f, Name = UniqueTop(used, fi.Name), Time = fi.LastWriteTime, Size = fi.Length });
            }
            if (first == null) throw new UserFacingException("묶을 파일이 없어요.");
            int count = items.Count(x => !x.Dir);
            if (count == 0) throw new UserFacingException("폴더 안에 묶을 파일이 없어요.");
            if (count > MaxFiles) throw new UserFacingException("파일이 너무 많아요. 한 번에 " + MaxFiles.ToString("N0") + "개까지 묶을 수 있어요.");
            long total = items.Sum(x => x.Size);
            string stem = IsFolderPath(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first);
            string name = (tops > 1 ? stem + " 외 " + (tops - 1) + "개" : stem) + ext;
            string dst = Util.UniquePath(Path.Combine(Util.OutputDirFor(first), name));
            Conversions.Produce(job, dst, delegate (string d) { make(job, items, d, total); });
            job.Note(Path.GetFileName(dst) + ": 파일 " + count + "개를 묶었어요. (" + Conversions.Size(total) + " → " + Conversions.Size(new FileInfo(dst).Length) + ")");
            if (skipped > 0) job.Note("열 수 없거나 다른 곳을 가리키는 항목 " + skipped + "개는 넣지 않았어요.");
        }

        static bool IsFolderPath(string p)
        {
            return Directory.Exists(p);
        }

        static Stream OpenSource(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
            }
            catch (IOException)
            {
                throw new UserFacingException(Util.ShortName(path) + " 파일이 다른 프로그램에서 쓰고 있어 읽지 못했어요.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new UserFacingException(Util.ShortName(path) + " 파일을 읽을 권한이 없어요.");
            }
        }

        static void CopyAll(Job job, Stream from, Stream to, ref long done, long total, string what)
        {
            var buf = new byte[BufferSize];
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0)
            {
                job.Check();
                to.Write(buf, 0, n);
                done += n;
                if (total > 0) job.Report(what, Math.Min(1.0, (double)done / total));
            }
        }

        static void MakeZip(Job job, List<PackItem> items, string dst, long total)
        {
            int expected = 0;
            long done = 0;
            using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            // Encoding.UTF8 그대로여야 'UTF-8 이름' 표시가 켜진다(BOM 없는 것을 새로 만들면 다른 인코딩으로 쳐서 표시를 안 켠다).
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                foreach (var it in items)
                {
                    job.Check();
                    string name = it.Dir ? it.Name + "/" : it.Name;
                    var e = z.CreateEntry(name, !it.Dir && Packed.Contains(Path.GetExtension(it.Name)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    if (it.Time.Year >= 1980 && it.Time.Year <= 2107) e.LastWriteTime = it.Time;
                    expected++;
                    if (it.Dir) continue;
                    using (var src = OpenSource(it.Source))
                    using (var o = e.Open()) CopyAll(job, src, o, ref done, total, "묶는 중");
                }
            }
            using (var fs = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (ReadCentral(fs).Count != expected) throw new UserFacingException("만든 압축 파일을 확인해 보니 항목 수가 맞지 않아 저장하지 않았어요.");
            }
        }

        // ── TAR 쓰기 ──

        static void Octal(byte[] h, int at, int len, long v)
        {
            string s = Convert.ToString(v, 8).PadLeft(len - 1, '0');
            for (int i = 0; i < len - 1; i++) h[at + i] = (byte)s[i];
        }

        static void Ascii(byte[] h, int at, string s)
        {
            for (int i = 0; i < s.Length; i++) h[at + i] = (byte)s[i];
        }

        static byte[] TarHeader(string name, long size, DateTime time, char type)
        {
            var h = new byte[512];
            Ascii(h, 0, name);
            Octal(h, 100, 8, type == '5' ? 493 : 420);
            Octal(h, 108, 8, 0);
            Octal(h, 116, 8, 0);
            Octal(h, 124, 12, size);
            Octal(h, 136, 12, Math.Max(0, (long)(time.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds));
            for (int i = 148; i < 156; i++) h[i] = 32;
            h[156] = (byte)type;
            Ascii(h, 257, "ustar");
            h[263] = 48;
            h[264] = 48;
            int sum = h.Sum(b => (int)b);
            Octal(h, 148, 7, sum);
            h[154] = 0;
            h[155] = 32;
            return h;
        }

        static string PaxRecord(string key, string value)
        {
            int body = new UTF8Encoding(false).GetByteCount(" " + key + "=" + value + "\n");
            int len = body + body.ToString().Length;
            if ((len).ToString().Length != body.ToString().Length) len = body + len.ToString().Length;
            return len + " " + key + "=" + value + "\n";
        }

        static void Pad(Stream o, long size)
        {
            int extra = (int)(size % 512);
            if (extra > 0) o.Write(new byte[512 - extra], 0, 512 - extra);
        }

        static bool FitsUstar(string name)
        {
            return name.Length <= 100 && name.All(c => c > 31 && c < 127);
        }

        static void MakeTar(Job job, List<PackItem> items, string dst, long total)
        {
            const long MaxOctal = 8589934591L;
            int expected = 0;
            long done = 0;
            int serial = 0;
            using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                foreach (var it in items)
                {
                    job.Check();
                    string name = it.Dir ? it.Name + "/" : it.Name;
                    long size = it.Dir ? 0 : it.Size;
                    string shortName = name;
                    var pax = new StringBuilder();
                    if (!FitsUstar(name))
                    {
                        pax.Append(PaxRecord("path", name));
                        serial++;
                        shortName = "bakkum-" + serial + (it.Dir ? "/" : "");
                    }
                    if (size > MaxOctal) pax.Append(PaxRecord("size", size.ToString()));
                    if (pax.Length > 0)
                    {
                        byte[] body = new UTF8Encoding(false).GetBytes(pax.ToString());
                        byte[] ph = TarHeader("PaxHeaders/" + serial, body.Length, it.Time, 'x');
                        o.Write(ph, 0, 512);
                        o.Write(body, 0, body.Length);
                        Pad(o, body.Length);
                    }
                    byte[] h = TarHeader(shortName, size > MaxOctal ? 0 : size, it.Time, it.Dir ? '5' : '0');
                    o.Write(h, 0, 512);
                    expected++;
                    if (it.Dir) continue;
                    long before = done;
                    using (var src = OpenSource(it.Source)) CopyAll(job, src, o, ref done, total, "묶는 중");
                    long wrote = done - before;
                    if (wrote != size) throw new UserFacingException(Util.ShortName(it.Source) + " 파일이 묶는 도중에 바뀌었어요. 다시 해 주세요.");
                    Pad(o, wrote);
                }
                o.Write(new byte[1024], 0, 1024);
            }
            int seen = 0;
            using (var fs = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            {
                ReadTar(job, fs, delegate (TarEntry e) { seen++; return null; });
            }
            if (seen != expected) throw new UserFacingException("만든 압축 파일을 확인해 보니 항목 수가 맞지 않아 저장하지 않았어요.");
        }

        static void GzipOne(Job job, List<string> files)
        {
            var list = files.Where(x => !IsFolder(x)).ToList();
            if (list.Count != 1) throw new UserFacingException("GZIP은 파일 하나만 압축할 수 있어요. 여러 개는 ZIP을 골라 주세요.");
            string f = list[0];
            long size = new FileInfo(f).Length;
            Conversions.Produce(job, Util.UniquePath(Path.Combine(Util.OutputDirFor(f), Path.GetFileName(f) + ".gz")), delegate (string d)
            {
                long done = 0;
                using (var src = OpenSource(f))
                using (var o = new FileStream(d, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
                using (var z = new GZipStream(o, CompressionLevel.Optimal)) CopyAll(job, src, z, ref done, size, "압축하는 중");
                long back = 0;
                using (var i = new FileStream(d, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
                using (var z = new GZipStream(i, CompressionMode.Decompress))
                {
                    var buf = new byte[BufferSize];
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0)
                    {
                        job.Check();
                        back += n;
                    }
                }
                if (back != size) throw new UserFacingException("만든 압축 파일을 확인해 보니 크기가 맞지 않아 저장하지 않았어요.");
            });
        }

        // ── 이름 검사 ──

        static readonly Regex Device = new Regex(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9]|conin\$|conout\$)(\..*)?$", RegexOptions.IgnoreCase);

        enum NameCheck { Ok, Outside, Bad }

        /// 압축 안의 이름을 이 PC의 상대 경로로 바꾼다. 폴더 밖으로 나가거나 윈도우에서 뜻이 달라지는 이름은 막는다.
        internal static string Relative(string raw, out int verdict)
        {
            verdict = (int)NameCheck.Ok;
            string n = raw.Replace('\\', '/');
            if (n.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(n, @"^[A-Za-z]:"))
            {
                verdict = (int)NameCheck.Outside;
                return null;
            }
            var parts = new List<string>();
            foreach (string p in n.Split('/'))
            {
                if (p.Length == 0 || p == ".") continue;
                if (p == "..")
                {
                    verdict = (int)NameCheck.Outside;
                    return null;
                }
                if (p.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0 || p.Any(c => c < 32) || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal) || Device.IsMatch(p))
                {
                    verdict = (int)NameCheck.Bad;
                    return null;
                }
                parts.Add(p);
            }
            return string.Join("\\", parts);
        }

        /// UTF-8로 맞으면 UTF-8, 아니면 한국어 윈도우 인코딩으로 읽는다. 맥에서 나뉜 자모는 합친다.
        internal static string DecodeName(byte[] b, bool utf8Flag)
        {
            string s;
            if (utf8Flag) s = new UTF8Encoding(false).GetString(b);
            else
            {
                try { s = new UTF8Encoding(false, true).GetString(b); }
                catch (ArgumentException) { s = Encoding.GetEncoding(949).GetString(b); }
            }
            try { return s.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { return s; }
        }

        static bool ArchiveJunk(string name)
        {
            string n = name.Replace('\\', '/');
            return n.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) || n.Contains("/__MACOSX/") || n.EndsWith("/.DS_Store", StringComparison.OrdinalIgnoreCase) || n.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase);
        }

        // ── 인터넷에서 받은 압축 파일 표시 잇기 ──

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        static byte[] ZoneOf(string path)
        {
            try
            {
                using (var h = CreateFileW(path + ":Zone.Identifier", 0x80000000, 1, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) return null;
                    using (var fs = new FileStream(h, FileAccess.Read))
                    {
                        var buf = new byte[4096];
                        int n = fs.Read(buf, 0, buf.Length);
                        return buf.Take(n).ToArray();
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        static void PutZone(string path, byte[] zone)
        {
            if (zone == null || zone.Length == 0) return;
            try
            {
                using (var h = CreateFileW(path + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) return;
                    using (var fs = new FileStream(h, FileAccess.Write)) fs.Write(zone, 0, zone.Length);
                }
            }
            catch (Exception ex)
            {
                Log.Write("인터넷 파일 표시 잇기 실패: " + ex.GetType().Name);
            }
        }

        // ── 풀기 ──

        sealed class Unpacking
        {
            public Job Job;
            public string Folder;
            public byte[] Zone;
            public long Declared;
            public long Written;
            public int Files, Links, Outside, Bad, Duplicates, Unsupported;
            public readonly Dictionary<string, bool> Seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public string Strip;

            public void Reserve()
            {
                if (++Files > MaxFiles) throw new UserFacingException("압축 안의 파일이 너무 많아요. 한 번에 " + MaxFiles.ToString("N0") + "개까지 풀 수 있어요.");
            }
        }

        static string StripTop(string rel, string top)
        {
            if (top == null) return rel;
            return rel.Length <= top.Length ? "" : rel.Substring(top.Length + 1);
        }

        /// 맨 위 폴더 하나에 모두 들어 있으면 그 폴더를 벗긴다(풀린 폴더 안에 같은 이름의 폴더가 또 생기지 않게).
        static string SingleTop(IEnumerable<string> names)
        {
            string top = null;
            foreach (string n in names)
            {
                int cut = n.IndexOf('\\');
                if (cut <= 0) return null;
                string head = n.Substring(0, cut);
                if (top == null) top = head;
                else if (!string.Equals(top, head, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return top;
        }

        static string Place(Unpacking u, string name, bool directory)
        {
            int v;
            string rel = Relative(name, out v);
            if (v == (int)NameCheck.Outside)
            {
                u.Outside++;
                return null;
            }
            if (v == (int)NameCheck.Bad)
            {
                u.Bad++;
                return null;
            }
            rel = StripTop(rel, u.Strip);
            if (rel.Length == 0) return null;
            string path = Path.Combine(u.Folder, rel);
            if (Util.TooLong(path))
            {
                u.Bad++;
                return null;
            }
            bool wasDir;
            if (u.Seen.TryGetValue(rel, out wasDir))
            {
                if (directory && wasDir) return null;
                u.Duplicates++;
                return null;
            }
            // 앞에서 파일로 풀린 이름 아래에 또 파일을 넣을 수는 없다.
            for (int cut = rel.IndexOf('\\'); cut > 0; cut = rel.IndexOf('\\', cut + 1))
            {
                if (u.Seen.TryGetValue(rel.Substring(0, cut), out wasDir) && !wasDir)
                {
                    u.Duplicates++;
                    return null;
                }
                u.Seen[rel.Substring(0, cut)] = true;
            }
            u.Seen[rel] = directory;
            return path;
        }

        static void Pump(Unpacking u, Stream from, string path, DateTime time, long expected, uint? crc)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var buf = new byte[BufferSize];
            uint c = 0;
            long wrote = 0;
            using (var o = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            {
                while (expected < 0 || wrote < expected)
                {
                    u.Job.Check();
                    int want = expected < 0 ? buf.Length : (int)Math.Min(buf.Length, expected - wrote);
                    int n = from.Read(buf, 0, want);
                    if (n <= 0) break;
                    wrote += n;
                    u.Written += n;
                    if (u.Written > MaxBytes) throw new UserFacingException("풀었을 때 크기가 4GB를 넘어요. 비정상적으로 부풀리는 압축 파일일 수 있어 멈췄어요.");
                    if (crc != null) c = Crc32.Update(c, buf, 0, n);
                    o.Write(buf, 0, n);
                    if (u.Declared > 0) u.Job.Report("푸는 중", Math.Min(1.0, (double)u.Written / u.Declared));
                }
            }
            if ((expected >= 0 && wrote != expected) || (crc != null && c != crc.Value)) throw new UserFacingException("압축 파일이 손상되어 풀지 못했어요.");
            if (time.Year > 1980)
            {
                try { File.SetLastWriteTime(path, time); }
                catch (Exception) { }
            }
            PutZone(path, u.Zone);
        }

        static void CheckRoom(string folder, long declared)
        {
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(folder));
                if (declared > d.AvailableFreeSpace) throw new UserFacingException("저장 공간이 모자라요. 풀려면 " + Conversions.Size(declared) + "가 필요해요.");
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void Unpack(Job job, List<string> files)
        {
            Conversions.EachFile(job, files, "푸는 중", delegate (string f)
            {
                string ext = Formats.Ext(f);
                string stem = Path.GetFileNameWithoutExtension(f);
                bool tgz = ext == ".tgz" || (ext == ".gz" && stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase));
                if (tgz && stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - 4);
                if (ext == ".gz" && !tgz)
                {
                    GunzipOne(job, f, stem);
                    return;
                }
                string folder = Util.UniquePath(Path.Combine(Util.OutputDirFor(f), stem));
                var u = new Unpacking { Job = job, Folder = folder, Zone = ZoneOf(f) };
                try
                {
                    Directory.CreateDirectory(folder);
                    if (ext == ".zip") UnzipInto(u, f);
                    else UntarInto(u, f, tgz);
                }
                catch
                {
                    Util.TryDeleteDir(folder);
                    throw;
                }
                if (u.Seen.Values.All(x => x))
                {
                    Util.TryDeleteDir(folder);
                    throw new UserFacingException(Blocked(u).Length > 0 ? "풀 수 있는 파일이 없어요. " + Blocked(u) : "압축 파일 안이 비어 있어요.");
                }
                job.Output(folder);
                job.Note(Util.ShortName(f) + ": 파일 " + u.Seen.Values.Count(x => !x) + "개를 '" + Path.GetFileName(folder) + "' 폴더에 풀었어요.");
                string blocked = Blocked(u);
                if (blocked.Length > 0) job.Note(Util.ShortName(f) + ": " + blocked);
            });
        }

        static string Blocked(Unpacking u)
        {
            var parts = new List<string>();
            if (u.Outside > 0) parts.Add("폴더 밖을 가리키는 항목 " + u.Outside + "개");
            if (u.Links > 0) parts.Add("링크 " + u.Links + "개");
            if (u.Bad > 0) parts.Add("윈도우에서 쓸 수 없는 이름 " + u.Bad + "개");
            if (u.Duplicates > 0) parts.Add("이름이 겹치는 항목 " + u.Duplicates + "개");
            if (u.Unsupported > 0) parts.Add("풀 수 없는 압축 방식 " + u.Unsupported + "개");
            return parts.Count == 0 ? "" : string.Join(", ", parts) + "는 안전을 위해 풀지 않았어요.";
        }

        static void GunzipOne(Job job, string f, string stem)
        {
            string name = stem.Length > 0 ? stem : "풀린 파일";
            var u = new Unpacking { Job = job, Zone = ZoneOf(f) };
            int v;
            if (Relative(name, out v) == null) throw new UserFacingException("윈도우에서 쓸 수 없는 이름이에요.");
            Conversions.Produce(job, Util.UniquePath(Path.Combine(Util.OutputDirFor(f), name)), delegate (string d)
            {
                using (var i = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize))
                using (var z = new GZipStream(i, CompressionMode.Decompress))
                {
                    try
                    {
                        Pump(u, z, d, DateTime.MinValue, -1, null);
                    }
                    catch (InvalidDataException)
                    {
                        throw new UserFacingException("압축 파일이 손상되었거나 GZIP 형식이 아니에요.");
                    }
                }
            });
        }

        // ── ZIP 풀기 ──

        internal sealed class ZipItem
        {
            public string Name;
            public int Flags, Method, Host;
            public long Packed, Size, Offset;
            public uint Crc, External;
            public DateTime Time;

            public bool IsDir
            {
                get { return Name.EndsWith("/", StringComparison.Ordinal) || Name.EndsWith("\\", StringComparison.Ordinal) || (Host == 0 && (External & 0x10) != 0); }
            }

            public bool IsLink
            {
                get { return Host == 3 && ((External >> 16) & 0xF000) == 0xA000; }
            }
        }

        static long U64(byte[] b, int at)
        {
            return (long)BitConverter.ToUInt64(b, at);
        }

        static UserFacingException NotZip()
        {
            return new UserFacingException("압축 파일이 손상되었거나 ZIP 형식이 아니에요.");
        }

        static byte[] ReadAt(Stream s, long at, int count)
        {
            s.Position = at;
            var b = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = s.Read(b, got, count - got);
                if (n <= 0) throw NotZip();
                got += n;
            }
            return b;
        }

        static DateTime DosTime(int time, int date)
        {
            try
            {
                return new DateTime(1980 + (date >> 9), Math.Max(1, (date >> 5) & 15), Math.Max(1, date & 31), time >> 11, (time >> 5) & 63, Math.Min(58, (time & 31) * 2));
            }
            catch (ArgumentException)
            {
                return DateTime.MinValue;
            }
        }

        internal static List<ZipItem> ReadCentral(Stream s)
        {
            if (s.Length < 22) throw NotZip();
            int tail = (int)Math.Min(s.Length, 22 + 65535);
            byte[] end = ReadAt(s, s.Length - tail, tail);
            int at = -1;
            for (int i = end.Length - 22; i >= 0; i--)
            {
                if (end[i] == 0x50 && end[i + 1] == 0x4B && end[i + 2] == 5 && end[i + 3] == 6)
                {
                    at = i;
                    break;
                }
            }
            if (at < 0) throw NotZip();
            long count = BitConverter.ToUInt16(end, at + 10);
            long size = BitConverter.ToUInt32(end, at + 12);
            long start = BitConverter.ToUInt32(end, at + 16);
            long eocd = s.Length - tail + at;
            if (eocd >= 20)
            {
                byte[] loc = ReadAt(s, eocd - 20, 20);
                if (BitConverter.ToUInt32(loc, 0) == 0x07064B50)
                {
                    long z64 = U64(loc, 8);
                    if (z64 < 0 || z64 + 56 > s.Length) throw NotZip();
                    byte[] rec = ReadAt(s, z64, 56);
                    if (BitConverter.ToUInt32(rec, 0) != 0x06064B50) throw NotZip();
                    count = U64(rec, 32);
                    size = U64(rec, 40);
                    start = U64(rec, 48);
                }
            }
            if (count < 0 || count > MaxFiles * 4L) throw new UserFacingException("압축 안의 파일이 너무 많아요. 한 번에 " + MaxFiles.ToString("N0") + "개까지 풀 수 있어요.");
            if (start < 0 || size < 0 || size > 256 * 1024 * 1024 || start + size > s.Length) throw NotZip();
            byte[] dir = ReadAt(s, start, (int)size);
            var list = new List<ZipItem>();
            int p = 0;
            for (long i = 0; i < count; i++)
            {
                if (p + 46 > dir.Length || BitConverter.ToUInt32(dir, p) != 0x02014B50) throw NotZip();
                int n = BitConverter.ToUInt16(dir, p + 28), x = BitConverter.ToUInt16(dir, p + 30), c = BitConverter.ToUInt16(dir, p + 32);
                if (p + 46 + n + x + c > dir.Length) throw NotZip();
                var it = new ZipItem
                {
                    Host = dir[p + 5],
                    Flags = BitConverter.ToUInt16(dir, p + 8),
                    Method = BitConverter.ToUInt16(dir, p + 10),
                    Time = DosTime(BitConverter.ToUInt16(dir, p + 12), BitConverter.ToUInt16(dir, p + 14)),
                    Crc = BitConverter.ToUInt32(dir, p + 16),
                    Packed = BitConverter.ToUInt32(dir, p + 20),
                    Size = BitConverter.ToUInt32(dir, p + 24),
                    External = BitConverter.ToUInt32(dir, p + 38),
                    Offset = BitConverter.ToUInt32(dir, p + 42)
                };
                var name = new byte[n];
                Buffer.BlockCopy(dir, p + 46, name, 0, n);
                it.Name = DecodeName(name, (it.Flags & 0x800) != 0);
                // ZIP64 추가 정보
                int e = p + 46 + n, stop = e + x;
                while (e + 4 <= stop)
                {
                    int id = BitConverter.ToUInt16(dir, e), len = BitConverter.ToUInt16(dir, e + 2);
                    if (id == 1)
                    {
                        int q = e + 4;
                        if (it.Size == 0xFFFFFFFFL && q + 8 <= stop)
                        {
                            it.Size = U64(dir, q);
                            q += 8;
                        }
                        if (it.Packed == 0xFFFFFFFFL && q + 8 <= stop)
                        {
                            it.Packed = U64(dir, q);
                            q += 8;
                        }
                        if (it.Offset == 0xFFFFFFFFL && q + 8 <= stop) it.Offset = U64(dir, q);
                    }
                    e += 4 + len;
                }
                if (it.Size < 0 || it.Packed < 0 || it.Offset < 0) throw NotZip();
                list.Add(it);
                p += 46 + n + x + c;
            }
            return list;
        }

        static void UnzipInto(Unpacking u, string f)
        {
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize))
            {
                var all = ReadCentral(fs).Where(x => !ArchiveJunk(x.Name)).ToList();
                if (all.Any(x => (x.Flags & 1) != 0 || x.Method == 99)) throw new UserFacingException("암호가 걸린 압축 파일은 풀 수 없어요. 압축 프로그램에서 암호를 넣어 풀어 주세요.");
                var names = new List<string>();
                foreach (var it in all)
                {
                    if (it.IsLink) continue;
                    int v;
                    string rel = Relative(it.Name, out v);
                    if (v != (int)NameCheck.Ok) continue;
                    if (rel.Length == 0) continue;
                    names.Add(it.IsDir ? rel + "\\" : rel);
                }
                u.Strip = SingleTop(names);
                u.Declared = all.Sum(x => x.Size);
                if (u.Declared > MaxBytes) throw new UserFacingException("풀었을 때 크기가 4GB를 넘어요. 더 작은 압축 파일로 나눠 주세요.");
                CheckRoom(u.Folder, u.Declared);
                foreach (var it in all)
                {
                    u.Job.Check();
                    if (it.IsLink)
                    {
                        u.Links++;
                        continue;
                    }
                    string path = Place(u, it.Name.TrimEnd('/', '\\'), it.IsDir);
                    if (path == null) continue;
                    if (it.IsDir)
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    if (it.Method != 0 && it.Method != 8)
                    {
                        u.Unsupported++;
                        u.Seen.Remove(path.Substring(u.Folder.Length + 1));
                        continue;
                    }
                    u.Reserve();
                    byte[] local = ReadAt(fs, it.Offset, 30);
                    if (BitConverter.ToUInt32(local, 0) != 0x04034B50) throw NotZip();
                    fs.Position = it.Offset + 30 + BitConverter.ToUInt16(local, 26) + BitConverter.ToUInt16(local, 28);
                    if (it.Method == 0)
                    {
                        Pump(u, fs, path, it.Time, it.Size, it.Crc);
                        continue;
                    }
                    try
                    {
                        using (var d = new DeflateStream(fs, CompressionMode.Decompress, true)) Pump(u, d, path, it.Time, it.Size, it.Crc);
                    }
                    catch (InvalidDataException)
                    {
                        throw new UserFacingException("압축 파일이 손상되어 풀지 못했어요.");
                    }
                }
            }
        }

        // ── TAR 풀기 ──

        internal sealed class TarEntry
        {
            public string Name;
            public char Type;
            public long Size;
            public DateTime Time;
            public Stream Body;
        }

        /// 항목마다 부른다. 내용을 다 읽지 않으면 남은 만큼 건너뛴다.
        internal delegate object TarVisit(TarEntry e);

        static int FillBlock(Stream s, byte[] b)
        {
            int got = 0;
            while (got < 512)
            {
                int n = s.Read(b, got, 512 - got);
                if (n <= 0) break;
                got += n;
            }
            return got;
        }

        static long TarNumber(byte[] h, int at, int len)
        {
            if ((h[at] & 0x80) != 0)
            {
                long big = 0;
                for (int i = at + 4; i < at + len; i++) big = (big << 8) | h[i];
                return big;
            }
            long v = 0;
            for (int i = at; i < at + len; i++)
            {
                byte c = h[i];
                if (c == 0 || c == 32)
                {
                    if (v > 0) break;
                    continue;
                }
                if (c < 48 || c > 55) throw new UserFacingException("압축 파일이 손상되었거나 TAR 형식이 아니에요.");
                v = v * 8 + (c - 48);
            }
            return v;
        }

        static string TarText(byte[] h, int at, int len)
        {
            int n = 0;
            while (n < len && h[at + n] != 0) n++;
            var b = new byte[n];
            Buffer.BlockCopy(h, at, b, 0, n);
            return DecodeName(b, false);
        }

        static byte[] ReadExact(Stream s, long size)
        {
            if (size > 1 << 24) throw new UserFacingException("압축 파일이 손상되었거나 TAR 형식이 아니에요.");
            var b = new byte[(int)size];
            int got = 0;
            while (got < b.Length)
            {
                int n = s.Read(b, got, b.Length - got);
                if (n <= 0) throw new UserFacingException("압축 파일이 손상되어 풀지 못했어요.");
                got += n;
            }
            return b;
        }

        static void Discard(Stream s, long count)
        {
            var buf = new byte[BufferSize];
            while (count > 0)
            {
                int n = s.Read(buf, 0, (int)Math.Min(buf.Length, count));
                if (n <= 0) throw new UserFacingException("압축 파일이 손상되어 풀지 못했어요.");
                count -= n;
            }
        }

        static void SkipPad(Stream s, long size)
        {
            int extra = (int)(size % 512);
            if (extra > 0) Discard(s, 512 - extra);
        }

        static Dictionary<string, string> PaxRecords(byte[] data)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            int p = 0;
            while (p < data.Length)
            {
                int sp = Array.IndexOf(data, (byte)32, p);
                if (sp < 0) break;
                int len;
                if (!int.TryParse(Encoding.ASCII.GetString(data, p, sp - p), out len) || len <= 0 || p + len > data.Length) break;
                string record = new UTF8Encoding(false).GetString(data, sp + 1, len - (sp - p) - 2);
                int eq = record.IndexOf('=');
                if (eq > 0) map[record.Substring(0, eq)] = record.Substring(eq + 1);
                p += len;
            }
            return map;
        }

        internal sealed class CountedStream : Stream
        {
            readonly Stream inner;
            public long Left;

            public CountedStream(Stream s, long count)
            {
                inner = s;
                Left = count;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Left <= 0) return 0;
                int n = inner.Read(buffer, offset, (int)Math.Min(count, Left));
                Left -= n;
                return n;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        internal static void ReadTar(Job job, Stream s, TarVisit visit)
        {
            var h = new byte[512];
            string longName = null, paxPath = null;
            long paxSize = -1;
            int count = 0;
            while (true)
            {
                job.Check();
                int got = FillBlock(s, h);
                if (got == 0) return;
                if (got < 512) throw new UserFacingException("압축 파일이 손상되어 풀지 못했어요.");
                if (h.All(b => b == 0)) return;
                long stored = TarNumber(h, 148, 8);
                int sum = 0;
                for (int i = 0; i < 512; i++) sum += i >= 148 && i < 156 ? 32 : h[i];
                if (sum != stored) throw new UserFacingException("압축 파일이 손상되었거나 TAR 형식이 아니에요.");
                if (++count > MaxFiles * 8) throw new UserFacingException("압축 안의 파일이 너무 많아요. 한 번에 " + MaxFiles.ToString("N0") + "개까지 풀 수 있어요.");
                char type = (char)h[156];
                long size = TarNumber(h, 124, 12);
                if (type == 'x' || type == 'g')
                {
                    byte[] body = ReadExact(s, size);
                    SkipPad(s, size);
                    if (type == 'x')
                    {
                        var rec = PaxRecords(body);
                        string v;
                        if (rec.TryGetValue("path", out v)) paxPath = v;
                        long big;
                        if (rec.TryGetValue("size", out v) && long.TryParse(v, out big)) paxSize = big;
                    }
                    continue;
                }
                if (type == 'L')
                {
                    longName = DecodeName(ReadExact(s, size).TakeWhile(b => b != 0).ToArray(), false);
                    SkipPad(s, size);
                    continue;
                }
                if (type == 'K')
                {
                    Discard(s, size);
                    SkipPad(s, size);
                    continue;
                }
                if (paxSize >= 0) size = paxSize;
                string name = TarText(h, 0, 100);
                if (h[257] == 'u' && h[258] == 's' && h[259] == 't' && h[260] == 'a' && h[261] == 'r' && h[263] != 32)
                {
                    string prefix = TarText(h, 345, 155);
                    if (prefix.Length > 0) name = prefix + "/" + name;
                }
                if (longName != null) name = longName;
                if (paxPath != null) name = paxPath;
                try { name = name.Normalize(NormalizationForm.FormC); }
                catch (ArgumentException) { }
                longName = paxPath = null;
                paxSize = -1;
                if (type == '\0') type = name.EndsWith("/", StringComparison.Ordinal) ? '5' : '0';
                if (type == '7') type = '0';
                long bodySize = "123456".IndexOf(type) >= 0 ? 0 : size;
                var counted = new CountedStream(s, bodySize);
                visit(new TarEntry { Name = name, Type = type, Size = bodySize, Time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(TarNumber(h, 136, 12)).ToLocalTime(), Body = counted });
                if (counted.Left > 0) Discard(s, counted.Left);
                SkipPad(s, bodySize);
            }
        }

        static Stream TarSource(string f, bool gz, out Stream raw)
        {
            raw = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
            return gz ? new GZipStream(raw, CompressionMode.Decompress) : raw;
        }

        static void UntarInto(Unpacking u, string f, bool gz)
        {
            var names = new List<string>();
            long total = 0;
            Stream raw;
            try
            {
                // 한 번 훑어 이름과 크기를 먼저 본다(압축된 TAR도 앞에서부터 읽기만 하면 된다).
                using (var s = TarSource(f, gz, out raw))
                {
                    ReadTar(u.Job, s, delegate (TarEntry e)
                    {
                        if (ArchiveJunk(e.Name) || (e.Type != '0' && e.Type != '5')) return null;
                        total += e.Size;
                        if (total > MaxBytes) throw new UserFacingException("풀었을 때 크기가 4GB를 넘어요. 비정상적으로 부풀리는 압축 파일일 수 있어 멈췄어요.");
                        int v;
                        string rel = Relative(e.Name, out v);
                        if (v == (int)NameCheck.Ok && rel.Length > 0) names.Add(e.Type == '5' ? rel + "\\" : rel);
                        return null;
                    });
                }
                raw.Dispose();
                u.Strip = SingleTop(names);
                u.Declared = total;
                CheckRoom(u.Folder, total);
                using (var s = TarSource(f, gz, out raw))
                {
                    ReadTar(u.Job, s, delegate (TarEntry e)
                    {
                        if (ArchiveJunk(e.Name)) return null;
                        if (e.Type == '1' || e.Type == '2')
                        {
                            u.Links++;
                            return null;
                        }
                        if (e.Type != '0' && e.Type != '5' && e.Type != 'S')
                        {
                            u.Unsupported++;
                            return null;
                        }
                        string path = Place(u, e.Name.TrimEnd('/', '\\'), e.Type == '5');
                        if (path == null) return null;
                        if (e.Type == '5')
                        {
                            Directory.CreateDirectory(path);
                            return null;
                        }
                        u.Reserve();
                        Pump(u, e.Body, path, e.Time, e.Size, null);
                        return null;
                    });
                }
            }
            catch (InvalidDataException)
            {
                throw new UserFacingException("압축 파일이 손상되었거나 형식이 맞지 않아요.");
            }
        }
    }
}
