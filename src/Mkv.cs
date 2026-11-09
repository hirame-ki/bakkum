// MP4를 다시 압축하지 않고 다른 상자로 옮겨 담는다. 윈도우에는 MOV·MKV로 저장하는 부품이 없지만
// 안의 영상(H.264)과 소리(AAC)는 세 형식 모두 같은 것을 쓸 수 있다.
// 근거: ISO/IEC 14496-12(MP4 상자 구조), Matroska 명세(RFC 9559), QuickTime File Format의 ftyp.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Bakkum
{
    sealed class IsoTrack
    {
        public bool Video;
        public int Timescale, Width, Height, Channels, SampleRate;
        public byte[] Private;
        public long[] Offsets, Dts;
        public int[] Sizes, Cts;
        public bool[] Key;
        public long Shift;
        public double DelayMs;

        public long Ms(int i)
        {
            return Math.Max(0, (long)Math.Round(DelayMs + (Dts[i] + Cts[i] - Shift) * 1000.0 / Timescale));
        }
    }

    static class IsoFile
    {
        struct Box
        {
            public string Type;
            public int Body, End;
        }

        static uint U32(byte[] d, int at)
        {
            return (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);
        }

        static ulong U64(byte[] d, int at)
        {
            return ((ulong)U32(d, at) << 32) | U32(d, at + 4);
        }

        static int U16(byte[] d, int at)
        {
            return (d[at] << 8) | d[at + 1];
        }

        static string Tag(byte[] d, int at)
        {
            return Encoding.ASCII.GetString(d, at, 4);
        }

        static List<Box> Boxes(byte[] d, int start, int end)
        {
            var list = new List<Box>();
            int at = start;
            while (at + 8 <= end)
            {
                long size = U32(d, at);
                int head = 8;
                if (size == 1)
                {
                    if (at + 16 > end) break;
                    size = (long)U64(d, at + 8);
                    head = 16;
                }
                else if (size == 0) size = end - at;
                if (size < head || at + size > end) break;
                list.Add(new Box { Type = Tag(d, at + 4), Body = at + head, End = at + (int)size });
                at += (int)size;
            }
            return list;
        }

        static bool Find(byte[] d, Box parent, string type, out Box found)
        {
            foreach (var b in Boxes(d, parent.Body, parent.End))
            {
                if (b.Type == type)
                {
                    found = b;
                    return true;
                }
            }
            found = default(Box);
            return false;
        }

        /// ftyp만 QuickTime 것으로 바꾼다. 상자 길이는 그대로라 나머지 위치는 변하지 않는다.
        public static void MakeMov(string path)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var head = new byte[8];
                if (f.Read(head, 0, 8) != 8 || Tag(head, 4) != "ftyp") throw new InvalidOperationException("ftyp 없음");
                long size = U32(head, 0);
                if (size < 16 || size > 1024) throw new InvalidOperationException("ftyp 크기");
                var qt = Encoding.ASCII.GetBytes("qt  ");
                f.Write(qt, 0, 4);
                f.Write(new byte[] { 0, 0, 2, 0 }, 0, 4);
                for (long i = 16; i + 4 <= size; i += 4) f.Write(qt, 0, 4);
            }
        }

        static byte[] LoadMoov(Stream s)
        {
            var head = new byte[16];
            long at = 0;
            while (at + 8 <= s.Length)
            {
                s.Position = at;
                if (s.Read(head, 0, 16) < 8) break;
                long size = U32(head, 0);
                int skip = 8;
                if (size == 1)
                {
                    size = (long)U64(head, 8);
                    skip = 16;
                }
                else if (size == 0) size = s.Length - at;
                if (size < skip) break;
                if (Tag(head, 4) == "moov")
                {
                    if (size > 256 * 1024 * 1024) throw new UserFacingException("영상 정보가 너무 커서 옮기지 못했어요.");
                    var d = new byte[size];
                    s.Position = at;
                    int got = 0;
                    while (got < d.Length)
                    {
                        int n = s.Read(d, got, d.Length - got);
                        if (n <= 0) throw new EndOfStreamException();
                        got += n;
                    }
                    return d;
                }
                at += size;
            }
            throw new UserFacingException("만든 영상에서 정보 상자를 찾지 못했어요.");
        }

        public static List<IsoTrack> Read(Stream s)
        {
            var d = LoadMoov(s);
            var whole = new Box { Type = "file", Body = 0, End = d.Length };
            Box moov, mvhd;
            if (!Find(d, whole, "moov", out moov)) throw new InvalidOperationException("moov 없음");
            double movieScale = 1000;
            if (Find(d, moov, "mvhd", out mvhd)) movieScale = U32(d, mvhd.Body + (d[mvhd.Body] == 1 ? 20 : 12));
            var tracks = new List<IsoTrack>();
            foreach (var trak in Boxes(d, moov.Body, moov.End))
            {
                if (trak.Type != "trak") continue;
                var t = Track(d, trak, movieScale);
                if (t != null) tracks.Add(t);
            }
            return tracks;
        }

        static IsoTrack Track(byte[] d, Box trak, double movieScale)
        {
            Box mdia, mdhd, hdlr, minf, stbl, stsd, stts, stsc, stsz, chunk, box;
            if (!Find(d, trak, "mdia", out mdia) || !Find(d, mdia, "mdhd", out mdhd) || !Find(d, mdia, "hdlr", out hdlr) || !Find(d, mdia, "minf", out minf) || !Find(d, minf, "stbl", out stbl)) return null;
            string handler = Tag(d, hdlr.Body + 8);
            if (handler != "vide" && handler != "soun") return null;
            if (!Find(d, stbl, "stsd", out stsd) || !Find(d, stbl, "stts", out stts) || !Find(d, stbl, "stsc", out stsc) || !Find(d, stbl, "stsz", out stsz)) return null;
            var t = new IsoTrack { Video = handler == "vide", Timescale = (int)U32(d, mdhd.Body + (d[mdhd.Body] == 1 ? 20 : 12)) };
            int entry = stsd.Body + 8;
            string format = Tag(d, entry + 4);
            int entryEnd = entry + (int)U32(d, entry);
            if (t.Video)
            {
                if (format != "avc1") throw new UserFacingException("이 영상 형식은 옮겨 담지 못해요.");
                t.Width = U16(d, entry + 32);
                t.Height = U16(d, entry + 34);
                var inner = new Box { Type = format, Body = entry + 86, End = entryEnd };
                if (!Find(d, inner, "avcC", out box)) throw new InvalidOperationException("avcC 없음");
                t.Private = Slice(d, box.Body, box.End - box.Body);
            }
            else
            {
                if (format != "mp4a") throw new UserFacingException("이 소리 형식은 옮겨 담지 못해요.");
                t.Channels = U16(d, entry + 24);
                t.SampleRate = U16(d, entry + 32);
                var inner = new Box { Type = format, Body = entry + 36, End = entryEnd };
                if (!Find(d, inner, "esds", out box)) throw new InvalidOperationException("esds 없음");
                t.Private = AudioConfig(d, box.Body + 4, box.End);
            }

            int count = (int)U32(d, stsz.Body + 8);
            uint constant = U32(d, stsz.Body + 4);
            t.Sizes = new int[count];
            for (int i = 0; i < count; i++) t.Sizes[i] = constant != 0 ? (int)constant : (int)U32(d, stsz.Body + 12 + i * 4);

            t.Dts = new long[count];
            long time = 0;
            int n = 0, runs = (int)U32(d, stts.Body + 4);
            for (int i = 0; i < runs; i++)
            {
                uint repeat = U32(d, stts.Body + 8 + i * 8), delta = U32(d, stts.Body + 12 + i * 8);
                for (uint r = 0; r < repeat && n < count; r++, n++)
                {
                    t.Dts[n] = time;
                    time += delta;
                }
            }

            t.Cts = new int[count];
            if (Find(d, stbl, "ctts", out box))
            {
                n = 0;
                runs = (int)U32(d, box.Body + 4);
                for (int i = 0; i < runs; i++)
                {
                    uint repeat = U32(d, box.Body + 8 + i * 8);
                    int offset = (int)U32(d, box.Body + 12 + i * 8);
                    for (uint r = 0; r < repeat && n < count; r++, n++) t.Cts[n] = offset;
                }
            }

            t.Key = new bool[count];
            if (Find(d, stbl, "stss", out box))
            {
                int keys = (int)U32(d, box.Body + 4);
                for (int i = 0; i < keys; i++)
                {
                    int at = (int)U32(d, box.Body + 8 + i * 4) - 1;
                    if (at >= 0 && at < count) t.Key[at] = true;
                }
            }
            else
            {
                for (int i = 0; i < count; i++) t.Key[i] = true;
            }

            bool wide = Find(d, stbl, "co64", out chunk);
            if (!wide && !Find(d, stbl, "stco", out chunk)) throw new InvalidOperationException("stco 없음");
            int chunks = (int)U32(d, chunk.Body + 4);
            t.Offsets = new long[count];
            int entries = (int)U32(d, stsc.Body + 4), sample = 0;
            for (int c = 0, e = 0; c < chunks && sample < count; c++)
            {
                while (e + 1 < entries && U32(d, stsc.Body + 8 + (e + 1) * 12) <= (uint)(c + 1)) e++;
                int perChunk = (int)U32(d, stsc.Body + 12 + e * 12);
                long at = wide ? (long)U64(d, chunk.Body + 8 + c * 8) : U32(d, chunk.Body + 8 + c * 4);
                for (int i = 0; i < perChunk && sample < count; i++, sample++)
                {
                    t.Offsets[sample] = at;
                    at += t.Sizes[sample];
                }
            }
            if (sample < count) throw new InvalidOperationException("표가 샘플 수와 맞지 않음");

            Box edts, elst;
            if (Find(d, trak, "edts", out edts) && Find(d, edts, "elst", out elst))
            {
                bool longForm = d[elst.Body] == 1;
                int edits = (int)U32(d, elst.Body + 4), size = longForm ? 20 : 12;
                for (int i = 0; i < edits; i++)
                {
                    int at = elst.Body + 8 + i * size;
                    long start = longForm ? (long)U64(d, at + 8) : (int)U32(d, at + 4);
                    double length = longForm ? (double)U64(d, at) : U32(d, at);
                    if (start == -1)
                    {
                        t.DelayMs += length * 1000.0 / movieScale;
                        continue;
                    }
                    t.Shift = start;
                    break;
                }
            }
            return t;
        }

        static byte[] Slice(byte[] d, int at, int length)
        {
            var r = new byte[length];
            Buffer.BlockCopy(d, at, r, 0, length);
            return r;
        }

        static int Descriptor(byte[] d, ref int at, out int length)
        {
            int tag = d[at++];
            length = 0;
            for (int i = 0; i < 4; i++)
            {
                int b = d[at++];
                length = (length << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            return tag;
        }

        /// esds 안의 AudioSpecificConfig
        static byte[] AudioConfig(byte[] d, int at, int end)
        {
            int length;
            if (Descriptor(d, ref at, out length) != 3) throw new InvalidOperationException("ES 설명 없음");
            int flags = d[at + 2];
            at += 3;
            if ((flags & 0x80) != 0) at += 2;
            if ((flags & 0x40) != 0) at += 1 + d[at];
            if ((flags & 0x20) != 0) at += 2;
            if (Descriptor(d, ref at, out length) != 4) throw new InvalidOperationException("설정 설명 없음");
            at += 13;
            if (at >= end || Descriptor(d, ref at, out length) != 5) throw new InvalidOperationException("소리 설정 없음");
            return Slice(d, at, length);
        }
    }

    static class Mkv
    {
        const uint Segment = 0x18538067, SeekHead = 0x114D9B74, Info = 0x1549A966, Tracks = 0x1654AE6B, Cluster = 0x1F43B675, Cues = 0x1C53BB6B;

        static void Id(Stream o, uint id)
        {
            for (int shift = id > 0xFFFFFF ? 24 : id > 0xFFFF ? 16 : id > 0xFF ? 8 : 0; shift >= 0; shift -= 8) o.WriteByte((byte)(id >> shift));
        }

        static void Size(Stream o, long size)
        {
            o.WriteByte(1);
            for (int shift = 48; shift >= 0; shift -= 8) o.WriteByte((byte)(size >> shift));
        }

        static void Bytes(Stream o, uint id, byte[] data)
        {
            Id(o, id);
            Size(o, data.Length);
            o.Write(data, 0, data.Length);
        }

        static void Number(Stream o, uint id, long value, int bytes = 0)
        {
            if (bytes == 0)
            {
                bytes = 1;
                while (bytes < 8 && (value >> (bytes * 8)) != 0) bytes++;
            }
            var d = new byte[bytes];
            for (int i = 0; i < bytes; i++) d[bytes - 1 - i] = (byte)(value >> (i * 8));
            Bytes(o, id, d);
        }

        static void Float(Stream o, uint id, double value)
        {
            var d = BitConverter.GetBytes(value);
            Array.Reverse(d);
            Bytes(o, id, d);
        }

        static void Text(Stream o, uint id, string s)
        {
            Bytes(o, id, new UTF8Encoding(false).GetBytes(s));
        }

        static byte[] SeekTable(long info, long tracks, long cues)
        {
            var body = new MemoryStream();
            Action<uint, long> entry = delegate (uint target, long at)
            {
                var one = new MemoryStream();
                Number(one, 0x53AB, target, 4);
                Number(one, 0x53AC, at, 8);
                Bytes(body, 0x4DBB, one.ToArray());
            };
            entry(Info, info);
            entry(Tracks, tracks);
            entry(Cues, cues);
            var all = new MemoryStream();
            Bytes(all, SeekHead, body.ToArray());
            return all.ToArray();
        }

        public static void Remux(string mp4, string mkv, Job job)
        {
            using (var input = new FileStream(mp4, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var tracks = IsoFile.Read(input);
                if (tracks.Count == 0 || !tracks.Any(t => t.Video)) throw new UserFacingException("옮겨 담을 영상을 찾지 못했어요.");
                using (var o = new FileStream(mkv, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var head = new MemoryStream();
                    Number(head, 0x4286, 1);
                    Number(head, 0x42F7, 1);
                    Number(head, 0x42F2, 4);
                    Number(head, 0x42F3, 8);
                    Text(head, 0x4282, "matroska");
                    Number(head, 0x4287, 4);
                    Number(head, 0x4285, 2);
                    Bytes(o, 0x1A45DFA3, head.ToArray());

                    Id(o, Segment);
                    long segmentSize = o.Position;
                    Size(o, 0);
                    long segment = o.Position;
                    var blank = SeekTable(0, 0, 0);
                    o.Write(blank, 0, blank.Length);

                    long infoAt = o.Position - segment;
                    var info = new MemoryStream();
                    Number(info, 0x2AD7B1, 1000000);
                    double duration = 0;
                    foreach (var t in tracks)
                    {
                        int last = t.Dts.Length - 1;
                        if (last >= 0) duration = Math.Max(duration, t.Ms(last) + 33);
                    }
                    Float(info, 0x4489, duration);
                    Text(info, 0x4D80, AppInfo.Name);
                    Text(info, 0x5741, AppInfo.Name);
                    Bytes(o, Info, info.ToArray());

                    long tracksAt = o.Position - segment;
                    var list = new MemoryStream();
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        var t = tracks[i];
                        var e = new MemoryStream();
                        Number(e, 0xD7, i + 1);
                        Number(e, 0x73C5, i + 1);
                        Number(e, 0x83, t.Video ? 1 : 2);
                        Number(e, 0x9C, 0);
                        Text(e, 0x86, t.Video ? "V_MPEG4/ISO/AVC" : "A_AAC");
                        Bytes(e, 0x63A2, t.Private);
                        var more = new MemoryStream();
                        if (t.Video)
                        {
                            Number(more, 0xB0, t.Width);
                            Number(more, 0xBA, t.Height);
                            Bytes(e, 0xE0, more.ToArray());
                        }
                        else
                        {
                            Float(more, 0xB5, t.SampleRate);
                            Number(more, 0x9F, t.Channels);
                            Bytes(e, 0xE1, more.ToArray());
                        }
                        Bytes(list, 0xAE, e.ToArray());
                    }
                    Bytes(o, Tracks, list.ToArray());

                    var next = new int[tracks.Count];
                    int video = tracks.FindIndex(t => t.Video);
                    int total = tracks.Sum(t => t.Dts.Length), written = 0;
                    var cues = new MemoryStream();
                    long clusterStart = -1, clusterBase = 0;
                    var data = new byte[1 << 20];
                    while (true)
                    {
                        int pick = -1;
                        double best = double.MaxValue;
                        for (int i = 0; i < tracks.Count; i++)
                        {
                            if (next[i] >= tracks[i].Dts.Length) continue;
                            double when = tracks[i].DelayMs + (tracks[i].Dts[next[i]] - tracks[i].Shift) * 1000.0 / tracks[i].Timescale;
                            if (when < best)
                            {
                                best = when;
                                pick = i;
                            }
                        }
                        if (pick < 0) break;
                        var t = tracks[pick];
                        int s = next[pick]++;
                        long ms = t.Ms(s);
                        bool key = t.Key[s];
                        bool startNew = clusterStart < 0 || ms - clusterBase > 5000 || ms - clusterBase < -20000 || (pick == video && key && ms - clusterBase >= 1000);
                        if (startNew)
                        {
                            CloseCluster(o, clusterStart);
                            clusterBase = ms;
                            clusterStart = o.Position;
                            Id(o, Cluster);
                            Size(o, 0);
                            Number(o, 0xE7, ms);
                            if (pick == video && key)
                            {
                                var position = new MemoryStream();
                                Number(position, 0xF7, video + 1);
                                Number(position, 0xF1, clusterStart - segment);
                                var point = new MemoryStream();
                                Number(point, 0xB3, ms);
                                Bytes(point, 0xB7, position.ToArray());
                                Bytes(cues, 0xBB, point.ToArray());
                            }
                        }
                        if (data.Length < t.Sizes[s]) data = new byte[t.Sizes[s] * 2];
                        input.Position = t.Offsets[s];
                        int got = 0;
                        while (got < t.Sizes[s])
                        {
                            int n = input.Read(data, got, t.Sizes[s] - got);
                            if (n <= 0) throw new EndOfStreamException();
                            got += n;
                        }
                        Id(o, 0xA3);
                        Size(o, t.Sizes[s] + 4);
                        o.WriteByte((byte)(0x80 | (pick + 1)));
                        int relative = (int)(ms - clusterBase);
                        o.WriteByte((byte)(relative >> 8));
                        o.WriteByte((byte)relative);
                        o.WriteByte((byte)(key ? 0x80 : 0));
                        o.Write(data, 0, t.Sizes[s]);
                        written++;
                        if ((written & 255) == 0)
                        {
                            job.Check();
                            job.Report(Util.ShortName(mkv) + " 담는 중", (double)written / total);
                        }
                    }
                    CloseCluster(o, clusterStart);

                    long cuesAt = o.Position - segment;
                    if (cues.Length == 0) throw new UserFacingException("영상의 시작 지점을 찾지 못했어요.");
                    Bytes(o, Cues, cues.ToArray());
                    long end = o.Position;
                    o.Position = segmentSize;
                    Size(o, end - segment);
                    o.Position = segment;
                    var table = SeekTable(infoAt, tracksAt, cuesAt);
                    o.Write(table, 0, table.Length);
                    o.Position = end;
                }
            }
        }

        static void CloseCluster(Stream o, long start)
        {
            if (start < 0) return;
            long end = o.Position;
            o.Position = start + 4;
            Size(o, end - start - 12);
            o.Position = end;
        }
    }
}
