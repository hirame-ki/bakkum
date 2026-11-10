// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// PDF 읽기·다시 쓰기. 용량 줄이기와 그림→PDF에 쓴다.
// 읽기: 보통 xref·xref 스트림(/Prev 따라가기)·섞인 형식(/XRefStm)·객체 스트림을 읽고, 깨진 파일은 처음부터 훑어 다시 짠다.
// 쓰기: 쓰이는 객체만 1번부터 다시 번호를 매겨, 객체 스트림 + xref 스트림(PDF 1.5)으로 쓴다.
// 줄이기 규칙은 PDF 1.7 표준(ISO 32000-1) 8.9절 '그림'을 보고 정했다(아래 PdfShrink 설명).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bakkum
{
    public class PdfException : Exception
    {
        public PdfException(string m) : base(m) { }
    }

    public abstract class PdfObj { }

    public sealed class PdfNull : PdfObj
    {
        public static readonly PdfNull Value = new PdfNull();
        PdfNull() { }
    }

    public sealed class PdfBool : PdfObj
    {
        public readonly bool V;
        public static readonly PdfBool True = new PdfBool(true);
        public static readonly PdfBool False = new PdfBool(false);
        PdfBool(bool v) { V = v; }
    }

    public sealed class PdfNum : PdfObj
    {
        public readonly string Raw;
        public readonly double V;

        public PdfNum(string raw, double v)
        {
            Raw = raw;
            V = v;
        }

        public PdfNum(long v)
        {
            V = v;
            Raw = v.ToString(CultureInfo.InvariantCulture);
        }

        public PdfNum(double v)
        {
            V = v;
            Raw = Fmt(v);
        }

        public static string Fmt(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public int I { get { return (int)V; } }
        public long L { get { return (long)V; } }
    }

    public sealed class PdfName : PdfObj
    {
        public readonly string V;
        public PdfName(string v) { V = v; }
    }

    public sealed class PdfString : PdfObj
    {
        public readonly byte[] V;
        public PdfString(byte[] v) { V = v; }

        public static PdfString Text(string s)
        {
            var b = new List<byte> { 0xFE, 0xFF };
            b.AddRange(Encoding.BigEndianUnicode.GetBytes(s));
            return new PdfString(b.ToArray());
        }
    }

    public sealed class PdfArray : PdfObj
    {
        public readonly List<PdfObj> Items = new List<PdfObj>();

        public PdfArray() { }

        public PdfArray(params PdfObj[] items)
        {
            Items.AddRange(items);
        }

        public static PdfArray Nums(params double[] v)
        {
            var a = new PdfArray();
            foreach (var x in v) a.Items.Add(new PdfNum(x));
            return a;
        }
    }

    public sealed class PdfDict : PdfObj
    {
        readonly List<string> order = new List<string>();
        readonly Dictionary<string, PdfObj> map = new Dictionary<string, PdfObj>(StringComparer.Ordinal);

        public IEnumerable<string> Keys { get { return order; } }

        public PdfObj this[string key]
        {
            get
            {
                PdfObj v;
                return map.TryGetValue(key, out v) ? v : null;
            }
            set
            {
                if (value == null || value is PdfNull)
                {
                    Remove(key);
                    return;
                }
                if (!map.ContainsKey(key)) order.Add(key);
                map[key] = value;
            }
        }

        public void Remove(string key)
        {
            if (map.Remove(key)) order.Remove(key);
        }

        public string NameOf(string key)
        {
            var n = this[key] as PdfName;
            return n == null ? null : n.V;
        }

        public PdfDict Copy()
        {
            var d = new PdfDict();
            foreach (var k in order) d[k] = map[k];
            return d;
        }

        public void SetName(string key, string name) { this[key] = new PdfName(name); }
        public void SetNum(string key, double v) { this[key] = new PdfNum(v); }
    }

    public sealed class PdfRef : PdfObj
    {
        public readonly int Num;
        public readonly int Gen;

        public PdfRef(int num, int gen)
        {
            Num = num;
            Gen = gen;
        }
    }

    public sealed class PdfStream : PdfObj
    {
        public readonly PdfDict Dict;
        public byte[] Data;

        public PdfStream(PdfDict dict, byte[] data)
        {
            Dict = dict;
            Data = data;
        }
    }

    // ── 낱말 읽기 ──

    sealed class PdfLexer
    {
        readonly byte[] d;
        readonly int end;
        public int Pos;

        public PdfLexer(byte[] data, int pos, int end)
        {
            d = data;
            Pos = pos;
            this.end = Math.Min(end, data.Length);
        }

        public static bool IsWs(int b)
        {
            return b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;
        }

        public static bool IsDelim(int b)
        {
            return b == '(' || b == ')' || b == '<' || b == '>' || b == '[' || b == ']' || b == '{' || b == '}' || b == '/' || b == '%';
        }

        public void SkipWs()
        {
            while (Pos < end)
            {
                byte b = d[Pos];
                if (IsWs(b)) Pos++;
                else if (b == '%')
                {
                    while (Pos < end && d[Pos] != 10 && d[Pos] != 13) Pos++;
                }
                else break;
            }
        }

        public int Peek()
        {
            return Pos < end ? d[Pos] : -1;
        }

        public string Token()
        {
            int s = Pos;
            while (Pos < end && !IsWs(d[Pos]) && !IsDelim(d[Pos])) Pos++;
            return Latin1(d, s, Pos - s);
        }

        public static string Latin1(byte[] b, int start, int count)
        {
            var c = new char[count];
            for (int i = 0; i < count; i++) c[i] = (char)b[start + i];
            return new string(c);
        }

        public bool Keyword(string kw)
        {
            SkipWs();
            int s = Pos;
            if (Token() == kw) return true;
            Pos = s;
            return false;
        }

        public int Int()
        {
            SkipWs();
            string t = Token();
            int v;
            if (!int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) throw new PdfException("정수 아님");
            return v;
        }

        public long Long()
        {
            SkipWs();
            string t = Token();
            long v;
            if (!long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) throw new PdfException("정수 아님");
            return v;
        }

        static bool AllDigits(string t)
        {
            if (t.Length == 0) return false;
            foreach (char c in t) if (c < '0' || c > '9') return false;
            return true;
        }

        public PdfObj Read()
        {
            SkipWs();
            if (Pos >= end) throw new PdfException("끝");
            byte c = d[Pos];
            if (c == '/') return ReadName();
            if (c == '(') return ReadLiteral();
            if (c == '<') return Pos + 1 < end && d[Pos + 1] == '<' ? (PdfObj)ReadDict() : ReadHex();
            if (c == '[') return ReadArray();
            if (c == '+' || c == '-' || c == '.' || (c >= '0' && c <= '9')) return ReadNumberOrRef();
            string t = Token();
            if (t == "true") return PdfBool.True;
            if (t == "false") return PdfBool.False;
            if (t == "null") return PdfNull.Value;
            if (t.Length == 0) Pos++;
            throw new PdfException("모르는 낱말 " + t);
        }

        PdfObj ReadNumberOrRef()
        {
            string t = Token();
            if (AllDigits(t))
            {
                int save = Pos;
                SkipWs();
                int p = Peek();
                if (p >= '0' && p <= '9')
                {
                    string t2 = Token();
                    if (AllDigits(t2))
                    {
                        SkipWs();
                        if (Peek() == 'R' && (Pos + 1 >= end || IsWs(d[Pos + 1]) || IsDelim(d[Pos + 1])))
                        {
                            Pos++;
                            int n, g;
                            if (int.TryParse(t, out n) && int.TryParse(t2, out g)) return new PdfRef(n, g);
                        }
                    }
                }
                Pos = save;
            }
            double v;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                // "--5" 같은 잘못된 숫자는 앞부분만 읽거나 0으로 둔다
                string clean = new string(t.Where(ch => (ch >= '0' && ch <= '9') || ch == '.').ToArray());
                if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) v = 0;
                if (t.StartsWith("-")) v = -v;
                return new PdfNum(v);
            }
            return new PdfNum(t, v);
        }

        PdfName ReadName()
        {
            Pos++;
            var sb = new StringBuilder();
            while (Pos < end && !IsWs(d[Pos]) && !IsDelim(d[Pos]))
            {
                byte b = d[Pos++];
                if (b == '#' && Pos + 1 < end && Hex(d[Pos]) >= 0 && Hex(d[Pos + 1]) >= 0)
                {
                    sb.Append((char)(Hex(d[Pos]) * 16 + Hex(d[Pos + 1])));
                    Pos += 2;
                }
                else sb.Append((char)b);
            }
            return new PdfName(sb.ToString());
        }

        static int Hex(int b)
        {
            if (b >= '0' && b <= '9') return b - '0';
            if (b >= 'a' && b <= 'f') return b - 'a' + 10;
            if (b >= 'A' && b <= 'F') return b - 'A' + 10;
            return -1;
        }

        PdfString ReadLiteral()
        {
            Pos++;
            int depth = 1;
            var o = new List<byte>();
            while (Pos < end)
            {
                byte b = d[Pos++];
                if (b == '\\')
                {
                    if (Pos >= end) break;
                    byte e = d[Pos++];
                    switch (e)
                    {
                        case (byte)'n': o.Add(10); break;
                        case (byte)'r': o.Add(13); break;
                        case (byte)'t': o.Add(9); break;
                        case (byte)'b': o.Add(8); break;
                        case (byte)'f': o.Add(12); break;
                        case 13:
                            if (Pos < end && d[Pos] == 10) Pos++;
                            break;
                        case 10:
                            break;
                        default:
                            if (e >= '0' && e <= '7')
                            {
                                int v = e - '0';
                                for (int k = 0; k < 2 && Pos < end && d[Pos] >= '0' && d[Pos] <= '7'; k++) v = v * 8 + (d[Pos++] - '0');
                                o.Add((byte)(v & 0xFF));
                            }
                            else o.Add(e);
                            break;
                    }
                }
                else if (b == '(')
                {
                    depth++;
                    o.Add(b);
                }
                else if (b == ')')
                {
                    depth--;
                    if (depth == 0) break;
                    o.Add(b);
                }
                else if (b == 13)
                {
                    o.Add(10);
                    if (Pos < end && d[Pos] == 10) Pos++;
                }
                else o.Add(b);
            }
            return new PdfString(o.ToArray());
        }

        PdfString ReadHex()
        {
            Pos++;
            var o = new List<byte>();
            int hi = -1;
            while (Pos < end)
            {
                byte b = d[Pos++];
                if (b == '>') break;
                int h = Hex(b);
                if (h < 0) continue;
                if (hi < 0) hi = h;
                else
                {
                    o.Add((byte)(hi * 16 + h));
                    hi = -1;
                }
            }
            if (hi >= 0) o.Add((byte)(hi * 16));
            return new PdfString(o.ToArray());
        }

        PdfArray ReadArray()
        {
            Pos++;
            var a = new PdfArray();
            while (true)
            {
                SkipWs();
                if (Pos >= end) throw new PdfException("배열 끝 없음");
                if (d[Pos] == ']')
                {
                    Pos++;
                    return a;
                }
                a.Items.Add(Read());
            }
        }

        PdfDict ReadDict()
        {
            Pos += 2;
            var dict = new PdfDict();
            while (true)
            {
                SkipWs();
                if (Pos >= end) throw new PdfException("사전 끝 없음");
                if (d[Pos] == '>' && Pos + 1 < end && d[Pos + 1] == '>')
                {
                    Pos += 2;
                    return dict;
                }
                if (d[Pos] != '/')
                {
                    // 망가진 사전: 키가 아닌 것은 건너뛴다
                    Read();
                    continue;
                }
                string key = ReadName().V;
                SkipWs();
                if (Pos + 1 < end && d[Pos] == '>' && d[Pos + 1] == '>') continue;
                dict[key] = Read();
            }
        }
    }

    // ── 문서 ──

    public sealed class PdfDocument
    {
        public readonly byte[] Data;
        public string Version = "1.4";
        public PdfDict Trailer = new PdfDict();
        public bool Repaired;

        struct XEntry
        {
            public byte Type;
            public long Offset;
            public int Stm;
        }

        readonly Dictionary<int, XEntry> xref = new Dictionary<int, XEntry>();
        readonly HashSet<int> freed = new HashSet<int>();
        readonly Dictionary<int, PdfObj> cache = new Dictionary<int, PdfObj>();
        readonly Dictionary<int, Dictionary<int, PdfObj>> objStms = new Dictionary<int, Dictionary<int, PdfObj>>();
        Dictionary<int, int> scanned;
        int loading;

        public static PdfDocument Load(string path)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (IOException) { throw new UserFacingException("PDF를 열 수 없어요. 다른 프로그램이 쓰고 있는지 확인해 주세요."); }
            try { return new PdfDocument(data); }
            catch (UserFacingException) { throw; }
            catch (Exception ex)
            {
                Log.Write("PDF 읽기 실패: " + ex.GetType().Name + " " + ex.Message);
                throw new UserFacingException("이 PDF를 읽지 못했어요. 손상되었거나 특이한 형식이에요.");
            }
        }

        /// 암호로 잠긴 PDF를 암호를 넣어 연다(틀리면 알기 쉬운 말로 알린다).
        public static PdfDocument Load(string path, string password)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (IOException) { throw new UserFacingException("PDF를 열 수 없어요. 다른 프로그램이 쓰고 있는지 확인해 주세요."); }
            try { return new PdfDocument(data, password); }
            catch (UserFacingException) { throw; }
            catch (Exception ex)
            {
                Log.Write("PDF 읽기 실패: " + ex.GetType().Name + " " + ex.Message);
                throw new UserFacingException("이 PDF를 읽지 못했어요. 손상되었거나 특이한 형식이에요.");
            }
        }

        public PdfDocument(byte[] data) : this(data, null) { }

        public PdfDocument(byte[] data, string password)
        {
            Data = data;
            ReadVersion();
            bool ok;
            try { ok = ReadXrefChain(); }
            catch (Exception) { ok = false; }
            if (ok && Trailer["Encrypt"] != null)
            {
                // 잠긴 문서는 객체 스트림까지 잠겨 있어 뿌리를 읽어 볼 수 없다. 암호 없이 열면 '잠김'만 알린다.
                if (password == null) return;
                OpenCrypt(password);
            }
            if (!ok || !(Trailer["Root"] is PdfRef) || Get(((PdfRef)Trailer["Root"]).Num) as PdfDict == null) Reconstruct();
        }

        public bool Encrypted
        {
            get { return Trailer["Encrypt"] != null; }
        }

        /// 암호로 열어 낸 문서의 암호 정보(암호 없이 열었으면 null)
        public PdfCrypt Crypt
        {
            get { return crypt; }
        }

        PdfCrypt crypt;
        int encryptObject = -1;
        int lastGen;

        void OpenCrypt(string password)
        {
            var enc = Resolve(Trailer["Encrypt"]) as PdfDict;
            if (enc == null) throw new PdfException("Encrypt 사전 없음");
            var er = Trailer["Encrypt"] as PdfRef;
            if (er != null) encryptObject = er.Num;
            var ids = Resolve(Trailer["ID"]) as PdfArray;
            byte[] id0 = new byte[0];
            if (ids != null && ids.Items.Count > 0)
            {
                var s = Resolve(ids.Items[0]) as PdfString;
                if (s != null) id0 = s.V;
            }
            var opened = PdfCrypt.Open(this, enc, id0, password);
            if (opened == null) throw new UserFacingException("암호가 맞지 않아요.");
            crypt = opened;
            // 암호를 몰랐을 때 읽어 둔 것은 잠긴 그대로이니 버린다(Encrypt 사전만 남긴다).
            PdfObj keep = encryptObject >= 0 ? enc : null;
            cache.Clear();
            objStms.Clear();
            if (keep != null) cache[encryptObject] = keep;
        }

        PdfObj Unlock(PdfObj o, int num, int gen)
        {
            if (crypt == null || num == encryptObject) return o;
            return crypt.DecryptObject(o, num, gen);
        }

        void ReadVersion()
        {
            int i = IndexOf(Data, "%PDF-", 0, Math.Min(Data.Length, 1024));
            if (i < 0) throw new PdfException("PDF 머리 없음");
            int s = i + 5, e = s;
            while (e < Data.Length && e < s + 4 && ((Data[e] >= '0' && Data[e] <= '9') || Data[e] == '.')) e++;
            string v = PdfLexer.Latin1(Data, s, e - s);
            if (v.Length >= 3) Version = v;
        }

        public static int IndexOf(byte[] d, string pat, int start, int end)
        {
            int n = pat.Length;
            for (int i = Math.Max(0, start); i <= end - n; i++)
            {
                int k = 0;
                while (k < n && d[i + k] == pat[k]) k++;
                if (k == n) return i;
            }
            return -1;
        }

        static int LastIndexOf(byte[] d, string pat, int from)
        {
            int n = pat.Length;
            for (int i = Math.Min(from, d.Length - n); i >= 0; i--)
            {
                int k = 0;
                while (k < n && d[i + k] == pat[k]) k++;
                if (k == n) return i;
            }
            return -1;
        }

        bool ReadXrefChain()
        {
            int sx = LastIndexOf(Data, "startxref", Data.Length - 1);
            if (sx < 0 || sx < Data.Length - 65536) return false;
            var lx = new PdfLexer(Data, sx + 9, Data.Length);
            long off = lx.Long();
            var seen = new HashSet<long>();
            bool first = true;
            while (off > 0 && off < Data.Length && seen.Add(off))
            {
                var t = new PdfLexer(Data, (int)off, Data.Length);
                PdfDict trailer;
                if (t.Keyword("xref"))
                {
                    trailer = ReadXrefTable(t);
                }
                else
                {
                    var s = ReadIndirectAt((int)off) as PdfStream;
                    if (s == null || s.Dict.NameOf("Type") != "XRef") return false;
                    ReadXrefStream(s);
                    trailer = s.Dict;
                }
                MergeTrailer(trailer, first);
                first = false;
                var prev = trailer["Prev"] as PdfNum;
                off = prev == null ? -1 : prev.L;
            }
            return xref.Count > 0;
        }

        void MergeTrailer(PdfDict t, bool first)
        {
            foreach (var k in new[] { "Root", "Info", "ID", "Encrypt" })
            {
                if (Trailer[k] == null && t[k] != null) Trailer[k] = t[k];
            }
        }

        PdfDict ReadXrefTable(PdfLexer t)
        {
            var inUse = new List<KeyValuePair<int, long>>();
            var free = new List<int>();
            while (true)
            {
                t.SkipWs();
                int save = t.Pos;
                string tok = t.Token();
                if (tok == "trailer") break;
                int start, count;
                if (!int.TryParse(tok, out start)) throw new PdfException("xref 표 깨짐");
                count = t.Int();
                for (int i = 0; i < count; i++)
                {
                    long o = t.Long();
                    t.Int();
                    t.SkipWs();
                    string k = t.Token();
                    if (k == "n") inUse.Add(new KeyValuePair<int, long>(start + i, o));
                    else free.Add(start + i);
                }
                if (t.Pos == save) throw new PdfException("xref 표 깨짐");
            }
            var trailer = t.Read() as PdfDict;
            if (trailer == null) throw new PdfException("trailer 없음");
            foreach (var e in inUse)
            {
                if (e.Value > 0 && !xref.ContainsKey(e.Key) && !freed.Contains(e.Key)) xref[e.Key] = new XEntry { Type = 1, Offset = e.Value };
            }
            var xs = trailer["XRefStm"] as PdfNum;
            if (xs != null && xs.L > 0 && xs.L < Data.Length)
            {
                try
                {
                    var s = ReadIndirectAt((int)xs.L) as PdfStream;
                    if (s != null) ReadXrefStream(s);
                }
                catch { }
            }
            foreach (var f in free)
            {
                if (!xref.ContainsKey(f)) freed.Add(f);
            }
            return trailer;
        }

        void ReadXrefStream(PdfStream s)
        {
            var wArr = Resolve(s.Dict["W"]) as PdfArray;
            if (wArr == null || wArr.Items.Count < 3) throw new PdfException("W 없음");
            int[] w = wArr.Items.Select(x => ((PdfNum)Resolve(x)).I).ToArray();
            var idx = Resolve(s.Dict["Index"]) as PdfArray;
            var ranges = new List<int>();
            if (idx != null) ranges.AddRange(idx.Items.Select(x => ((PdfNum)Resolve(x)).I));
            else ranges.AddRange(new[] { 0, ((PdfNum)Resolve(s.Dict["Size"])).I });
            byte[] data = Decode(s);
            int p = 0, row = w[0] + w[1] + w[2];
            for (int r = 0; r + 1 < ranges.Count; r += 2)
            {
                for (int i = 0; i < ranges[r + 1]; i++)
                {
                    if (p + row > data.Length) return;
                    long f1 = w[0] == 0 ? 1 : BE(data, p, w[0]);
                    long f2 = BE(data, p + w[0], w[1]);
                    long f3 = BE(data, p + w[0] + w[1], w[2]);
                    p += row;
                    int num = ranges[r] + i;
                    if (xref.ContainsKey(num) || freed.Contains(num)) continue;
                    if (f1 == 0) freed.Add(num);
                    else if (f1 == 1) xref[num] = new XEntry { Type = 1, Offset = f2 };
                    else if (f1 == 2) xref[num] = new XEntry { Type = 2, Stm = (int)f2 };
                }
            }
        }

        static long BE(byte[] d, int p, int n)
        {
            long v = 0;
            for (int i = 0; i < n; i++) v = (v << 8) | d[p + i];
            return v;
        }

        PdfObj ReadIndirectAt(int offset)
        {
            int num;
            return ReadIndirectAt(offset, out num);
        }

        PdfObj ReadIndirectAt(int offset, out int num)
        {
            var lx = new PdfLexer(Data, offset, Data.Length);
            num = lx.Int();
            lastGen = lx.Int();
            if (!lx.Keyword("obj")) throw new PdfException("obj 없음");
            var o = lx.Read();
            var dict = o as PdfDict;
            if (dict == null) return o;
            int save = lx.Pos;
            if (!lx.Keyword("stream"))
            {
                lx.Pos = save;
                return o;
            }
            int p = lx.Pos;
            if (p < Data.Length && Data[p] == 13) p++;
            if (p < Data.Length && Data[p] == 10) p++;
            int start = p;
            int len = -1;
            var L = dict["Length"];
            if (L is PdfNum) len = ((PdfNum)L).I;
            else if (L is PdfRef && loading < 4)
            {
                try
                {
                    loading++;
                    var r = Resolve(L) as PdfNum;
                    if (r != null) len = r.I;
                }
                catch { }
                finally { loading--; }
            }
            int stop = -1;
            if (len >= 0 && (long)start + len <= Data.Length && EndstreamAt(start + len)) stop = start + len;
            if (stop < 0)
            {
                int e = IndexOf(Data, "endstream", start, Data.Length);
                if (e < 0) throw new PdfException("endstream 없음");
                stop = e;
                if (stop > start && Data[stop - 1] == 10) stop--;
                if (stop > start && Data[stop - 1] == 13) stop--;
            }
            var raw = new byte[stop - start];
            Buffer.BlockCopy(Data, start, raw, 0, raw.Length);
            return new PdfStream(dict, raw);
        }

        bool EndstreamAt(int p)
        {
            while (p < Data.Length && PdfLexer.IsWs(Data[p])) p++;
            return IndexOf(Data, "endstream", p, Math.Min(Data.Length, p + 9)) == p;
        }

        public PdfObj Get(int num)
        {
            PdfObj o;
            if (cache.TryGetValue(num, out o)) return o;
            cache[num] = PdfNull.Value; // 자기 참조 고리 막기
            XEntry e;
            o = null;
            try
            {
                if (xref.TryGetValue(num, out e))
                {
                    if (e.Type == 1) o = LoadAt(num, e.Offset);
                    else if (e.Type == 2) o = FromObjStm(e.Stm, num);
                }
                else if (Repaired || scanned != null) o = FromScan(num);
            }
            catch (Exception)
            {
                o = null;
            }
            if (o == null) o = PdfNull.Value;
            cache[num] = o;
            return o;
        }

        PdfObj LoadAt(int num, long offset)
        {
            try
            {
                int n;
                var o = ReadIndirectAt((int)offset, out n);
                if (n == num) return Unlock(o, num, lastGen);
            }
            catch { }
            return FromScan(num);
        }

        PdfObj FromScan(int num)
        {
            EnsureScanned();
            int off;
            if (!scanned.TryGetValue(num, out off)) return null;
            int n;
            var o = ReadIndirectAt(off, out n);
            return n == num ? Unlock(o, num, lastGen) : null;
        }

        PdfObj FromObjStm(int stm, int num)
        {
            Dictionary<int, PdfObj> members;
            if (!objStms.TryGetValue(stm, out members))
            {
                members = new Dictionary<int, PdfObj>();
                objStms[stm] = members;
                var s = Get(stm) as PdfStream;
                if (s != null) ParseObjStm(s, members);
            }
            PdfObj o;
            return members.TryGetValue(num, out o) ? o : null;
        }

        void ParseObjStm(PdfStream s, Dictionary<int, PdfObj> members)
        {
            byte[] data = Decode(s);
            int n = ((PdfNum)Resolve(s.Dict["N"])).I;
            int first = ((PdfNum)Resolve(s.Dict["First"])).I;
            var lx = new PdfLexer(data, 0, data.Length);
            var nums = new int[n];
            var offs = new int[n];
            for (int k = 0; k < n; k++)
            {
                nums[k] = lx.Int();
                offs[k] = lx.Int();
            }
            for (int k = 0; k < n; k++)
            {
                try
                {
                    var l2 = new PdfLexer(data, first + offs[k], data.Length);
                    if (!members.ContainsKey(nums[k])) members[nums[k]] = l2.Read();
                }
                catch { }
            }
        }

        void EnsureScanned()
        {
            if (scanned != null) return;
            scanned = new Dictionary<int, int>();
            byte[] d = Data;
            int len = d.Length;
            for (int i = 0; i < len - 6; i++)
            {
                byte b = d[i];
                if (b < '0' || b > '9') continue;
                if (i > 0 && !PdfLexer.IsWs(d[i - 1]) && !PdfLexer.IsDelim(d[i - 1])) continue;
                int p = i;
                long num = 0;
                while (p < len && d[p] >= '0' && d[p] <= '9' && p - i < 10) num = num * 10 + (d[p++] - '0');
                if (p >= len || !PdfLexer.IsWs(d[p])) continue;
                while (p < len && PdfLexer.IsWs(d[p])) p++;
                int q = p;
                while (p < len && d[p] >= '0' && d[p] <= '9' && p - q < 6) p++;
                if (p == q || p >= len || !PdfLexer.IsWs(d[p])) continue;
                while (p < len && PdfLexer.IsWs(d[p])) p++;
                if (p + 3 <= len && d[p] == 'o' && d[p + 1] == 'b' && d[p + 2] == 'j' && (p + 3 == len || PdfLexer.IsWs(d[p + 3]) || PdfLexer.IsDelim(d[p + 3])))
                {
                    if (num < int.MaxValue) scanned[(int)num] = i;
                    i = p + 2;
                }
            }
        }

        void Reconstruct()
        {
            Repaired = true;
            xref.Clear();
            freed.Clear();
            cache.Clear();
            objStms.Clear();
            Trailer = new PdfDict();
            EnsureScanned();
            foreach (var kv in scanned) xref[kv.Key] = new XEntry { Type = 1, Offset = kv.Value };
            foreach (var num in scanned.Keys.ToList())
            {
                try
                {
                    var s = Get(num) as PdfStream;
                    if (s == null || s.Dict.NameOf("Type") != "ObjStm") continue;
                    var members = new Dictionary<int, PdfObj>();
                    ParseObjStm(s, members);
                    objStms[num] = members;
                    foreach (var m in members.Keys)
                    {
                        if (!xref.ContainsKey(m)) xref[m] = new XEntry { Type = 2, Stm = num };
                    }
                }
                catch { }
            }
            int t = LastIndexOf(Data, "trailer", Data.Length - 1);
            while (t >= 0 && Trailer["Root"] == null)
            {
                try
                {
                    var d = new PdfLexer(Data, t + 7, Data.Length).Read() as PdfDict;
                    if (d != null) MergeTrailer(d, true);
                }
                catch { }
                t = t > 0 ? LastIndexOf(Data, "trailer", t - 1) : -1;
            }
            if (Trailer["Root"] == null)
            {
                foreach (var num in xref.Keys.ToList())
                {
                    var o = Get(num);
                    var d = o is PdfStream ? ((PdfStream)o).Dict : o as PdfDict;
                    if (d == null) continue;
                    if (d.NameOf("Type") == "XRef" && d["Root"] != null) MergeTrailer(d, true);
                }
            }
            var root = Trailer["Root"] as PdfRef;
            if (root == null || !(Get(root.Num) is PdfDict))
            {
                Trailer.Remove("Root");
                foreach (var num in xref.Keys.OrderBy(x => x))
                {
                    var d = Get(num) as PdfDict;
                    if (d != null && d.NameOf("Type") == "Catalog")
                    {
                        Trailer["Root"] = new PdfRef(num, 0);
                        break;
                    }
                }
            }
            if (Trailer["Root"] == null) throw new PdfException("문서 뿌리(Root) 없음");
        }

        public PdfObj Resolve(PdfObj o)
        {
            int guard = 0;
            while (o is PdfRef && guard++ < 32) o = Get(((PdfRef)o).Num);
            return o ?? PdfNull.Value;
        }

        public int IntOf(PdfDict d, string key, int fallback)
        {
            var n = Resolve(d[key]) as PdfNum;
            return n == null ? fallback : n.I;
        }

        // ── 압축 풀기 ──

        public byte[] Decode(PdfStream s)
        {
            var filters = new List<string>();
            var parms = new List<PdfDict>();
            var f = Resolve(s.Dict["Filter"]);
            if (f is PdfName) filters.Add(((PdfName)f).V);
            else if (f is PdfArray) filters.AddRange(((PdfArray)f).Items.Select(x => Resolve(x) as PdfName).Where(x => x != null).Select(x => x.V));
            var p = Resolve(s.Dict["DecodeParms"]);
            if (p is PdfDict) parms.Add((PdfDict)p);
            else if (p is PdfArray) parms.AddRange(((PdfArray)p).Items.Select(x => Resolve(x) as PdfDict));
            byte[] data = s.Data;
            for (int i = 0; i < filters.Count; i++)
            {
                PdfDict prm = i < parms.Count ? parms[i] : null;
                switch (filters[i])
                {
                    case "FlateDecode":
                    case "Fl":
                        data = Predict(Zlib.Inflate(data), prm);
                        break;
                    case "ASCIIHexDecode":
                    case "AHx":
                        data = AsciiHex(data);
                        break;
                    case "ASCII85Decode":
                    case "A85":
                        data = Ascii85(data);
                        break;
                    case "RunLengthDecode":
                    case "RL":
                        data = RunLength(data);
                        break;
                    case "Crypt":
                        break;
                    default:
                        throw new PdfException("풀 수 없는 압축 " + filters[i]);
                }
            }
            return data;
        }

        public static string SingleFilter(PdfDocument doc, PdfDict dict, out PdfDict parms)
        {
            parms = null;
            var f = doc.Resolve(dict["Filter"]);
            var p = doc.Resolve(dict["DecodeParms"]);
            if (f is PdfNull) return "";
            if (f is PdfName)
            {
                parms = p as PdfDict;
                return ((PdfName)f).V;
            }
            var a = f as PdfArray;
            if (a == null) return null;
            if (a.Items.Count == 0) return "";
            if (a.Items.Count != 1) return null;
            var only = doc.Resolve(a.Items[0]) as PdfName;
            if (only == null) return null;
            if (p is PdfArray && ((PdfArray)p).Items.Count > 0) parms = doc.Resolve(((PdfArray)p).Items[0]) as PdfDict;
            else parms = p as PdfDict;
            return only.V;
        }

        byte[] Predict(byte[] data, PdfDict prm)
        {
            if (prm == null) return data;
            int pred = IntOf(prm, "Predictor", 1);
            if (pred < 2) return data;
            int colors = IntOf(prm, "Colors", 1), bpc = IntOf(prm, "BitsPerComponent", 8), cols = IntOf(prm, "Columns", 1);
            return Unpredict(data, pred, colors, bpc, cols);
        }

        public static byte[] Unpredict(byte[] data, int pred, int colors, int bpc, int cols)
        {
            int bpp = Math.Max(1, (colors * bpc + 7) / 8);
            int rowBytes = (colors * bpc * cols + 7) / 8;
            if (pred == 2)
            {
                if (bpc != 8) throw new PdfException("TIFF 예측 " + bpc + "비트");
                var o = (byte[])data.Clone();
                for (int r = 0; r + rowBytes <= o.Length; r += rowBytes)
                {
                    for (int i = bpp; i < rowBytes; i++) o[r + i] = (byte)(o[r + i] + o[r + i - bpp]);
                }
                return o;
            }
            int rows = data.Length / (rowBytes + 1);
            var output = new byte[(long)rows * rowBytes];
            var prev = new byte[rowBytes];
            for (int r = 0; r < rows; r++)
            {
                int src = r * (rowBytes + 1);
                int ft = data[src];
                int dst = r * rowBytes;
                for (int i = 0; i < rowBytes; i++)
                {
                    int x = data[src + 1 + i];
                    int a = i >= bpp ? output[dst + i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    int v;
                    switch (ft)
                    {
                        case 1: v = x + a; break;
                        case 2: v = x + b; break;
                        case 3: v = x + ((a + b) >> 1); break;
                        case 4:
                            {
                                int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                                v = x + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                                break;
                            }
                        default: v = x; break;
                    }
                    output[dst + i] = (byte)v;
                }
                Buffer.BlockCopy(output, dst, prev, 0, rowBytes);
            }
            return output;
        }

        static byte[] AsciiHex(byte[] d)
        {
            var o = new List<byte>();
            int hi = -1;
            foreach (byte b in d)
            {
                if (b == '>') break;
                int h = b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;
                if (h < 0) continue;
                if (hi < 0) hi = h;
                else
                {
                    o.Add((byte)(hi * 16 + h));
                    hi = -1;
                }
            }
            if (hi >= 0) o.Add((byte)(hi * 16));
            return o.ToArray();
        }

        static byte[] Ascii85(byte[] d)
        {
            var o = new List<byte>();
            uint acc = 0;
            int n = 0;
            for (int i = 0; i < d.Length; i++)
            {
                byte b = d[i];
                if (b == '~') break;
                if (PdfLexer.IsWs(b)) continue;
                if (b == 'z' && n == 0)
                {
                    o.AddRange(new byte[4]);
                    continue;
                }
                if (b < '!' || b > 'u') continue;
                acc = acc * 85 + (uint)(b - '!');
                if (++n == 5)
                {
                    o.Add((byte)(acc >> 24));
                    o.Add((byte)(acc >> 16));
                    o.Add((byte)(acc >> 8));
                    o.Add((byte)acc);
                    acc = 0;
                    n = 0;
                }
            }
            if (n > 1)
            {
                for (int k = n; k < 5; k++) acc = acc * 85 + 84;
                for (int k = 0; k < n - 1; k++) o.Add((byte)(acc >> (24 - 8 * k)));
            }
            return o.ToArray();
        }

        static byte[] RunLength(byte[] d)
        {
            var o = new List<byte>();
            int i = 0;
            while (i < d.Length)
            {
                int len = d[i++];
                if (len == 128) break;
                if (len < 128)
                {
                    for (int k = 0; k <= len && i < d.Length; k++) o.Add(d[i++]);
                }
                else if (i < d.Length)
                {
                    byte b = d[i++];
                    for (int k = 0; k < 257 - len; k++) o.Add(b);
                }
            }
            return o.ToArray();
        }

        /// 문서 뿌리(Root)와 정보(Info)에서 닿는 객체 번호(넓이 우선 차례)
        public List<int> Reachable()
        {
            var order = new List<int>();
            var seen = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (var key in new[] { "Root", "Info" })
            {
                var r = Trailer[key] as PdfRef;
                if (r != null && seen.Add(r.Num)) queue.Enqueue(r.Num);
            }
            var stack = new Stack<PdfObj>();
            while (queue.Count > 0)
            {
                int num = queue.Dequeue();
                var o = Get(num);
                if (o is PdfNull) continue;
                order.Add(num);
                stack.Push(o);
                while (stack.Count > 0)
                {
                    var x = stack.Pop();
                    var r = x as PdfRef;
                    if (r != null)
                    {
                        if (seen.Add(r.Num)) queue.Enqueue(r.Num);
                        continue;
                    }
                    var a = x as PdfArray;
                    if (a != null)
                    {
                        for (int i = a.Items.Count - 1; i >= 0; i--) stack.Push(a.Items[i]);
                        continue;
                    }
                    var s = x as PdfStream;
                    var d = s != null ? s.Dict : x as PdfDict;
                    if (d == null) continue;
                    foreach (var k in d.Keys)
                    {
                        if (s != null && k == "Length") continue;
                        stack.Push(d[k]);
                    }
                }
            }
            return order;
        }
    }

    public static class Zlib
    {
        public static byte[] Inflate(byte[] data)
        {
            int start = 0;
            if (data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0) start = 2;
            using (var ms = new MemoryStream(data, start, data.Length - start))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                var buf = new byte[65536];
                try
                {
                    int n;
                    while ((n = ds.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
                }
                catch (InvalidDataException)
                {
                    if (o.Length == 0) throw;
                }
                return o.ToArray();
            }
        }

        public static byte[] Deflate(byte[] data)
        {
            using (var o = new MemoryStream())
            {
                o.WriteByte(0x78);
                o.WriteByte(0xDA);
                using (var ds = new DeflateStream(o, CompressionLevel.Optimal, true)) ds.Write(data, 0, data.Length);
                uint a = 1, b = 0;
                for (int i = 0; i < data.Length; )
                {
                    int n = Math.Min(5552, data.Length - i);
                    for (int k = 0; k < n; k++)
                    {
                        a += data[i++];
                        b += a;
                    }
                    a %= 65521;
                    b %= 65521;
                }
                uint adler = (b << 16) | a;
                o.WriteByte((byte)(adler >> 24));
                o.WriteByte((byte)(adler >> 16));
                o.WriteByte((byte)(adler >> 8));
                o.WriteByte((byte)adler);
                return o.ToArray();
            }
        }

        /// PNG 줄 거르기(행마다 가장 잘 줄어드는 방식을 고름) — PDF /Predictor 15
        public static byte[] PngFilter(byte[] s, int w, int h, int bpp)
        {
            int row = w * bpp;
            var o = new byte[(long)(row + 1) * h];
            var prev = new byte[row];
            var cand = new byte[5][];
            for (int k = 0; k < 5; k++) cand[k] = new byte[row];
            for (int y = 0; y < h; y++)
            {
                int cur = y * row;
                long best = long.MaxValue;
                int bestK = 0;
                for (int k = 0; k < 5; k++)
                {
                    long sum = 0;
                    var c = cand[k];
                    for (int i = 0; i < row; i++)
                    {
                        int x = s[cur + i];
                        int a = i >= bpp ? s[cur + i - bpp] : 0;
                        int b = prev[i];
                        int cc = i >= bpp ? prev[i - bpp] : 0;
                        int v;
                        switch (k)
                        {
                            case 1: v = x - a; break;
                            case 2: v = x - b; break;
                            case 3: v = x - ((a + b) >> 1); break;
                            case 4:
                                {
                                    int pa = Math.Abs(b - cc), pb = Math.Abs(a - cc), pc = Math.Abs(a + b - 2 * cc);
                                    v = x - (pa <= pb && pa <= pc ? a : pb <= pc ? b : cc);
                                    break;
                                }
                            default: v = x; break;
                        }
                        byte bv = (byte)v;
                        c[i] = bv;
                        sum += bv < 128 ? bv : 256 - bv;
                    }
                    if (sum < best)
                    {
                        best = sum;
                        bestK = k;
                    }
                }
                long dst = (long)y * (row + 1);
                o[dst] = (byte)bestK;
                Buffer.BlockCopy(cand[bestK], 0, o, (int)dst + 1, row);
                Buffer.BlockCopy(s, cur, prev, 0, row);
            }
            return o;
        }
    }

    // ── 쓰기 ──

    public sealed class PdfWriter : IDisposable
    {
        readonly Stream s;
        long pos;
        readonly Func<PdfRef, int> mapRef;
        public readonly Dictionary<int, long> Offsets = new Dictionary<int, long>();

        public PdfWriter(Stream s, Func<PdfRef, int> mapRef)
        {
            this.s = s;
            this.mapRef = mapRef;
        }

        public void Raw(string latin1)
        {
            var b = new byte[latin1.Length];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)latin1[i];
            Bytes(b);
        }

        public void Bytes(byte[] b)
        {
            s.Write(b, 0, b.Length);
            pos += b.Length;
        }

        public void Header(string version)
        {
            Raw("%PDF-" + version + "\n");
            Bytes(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });
        }

        public void Object(int num, PdfObj o)
        {
            Offsets[num] = pos;
            Raw(num + " 0 obj\n");
            var st = o as PdfStream;
            if (st != null)
            {
                st.Dict["Length"] = new PdfNum(st.Data.Length);
                Bytes(Serialize(st.Dict));
                Raw("\nstream\n");
                Bytes(st.Data);
                Raw("\nendstream");
            }
            else Bytes(Serialize(o));
            Raw("\nendobj\n");
        }

        public byte[] Serialize(PdfObj o)
        {
            var sb = new StringBuilder();
            Emit(sb, o);
            var b = new byte[sb.Length];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)sb[i];
            return b;
        }

        void Emit(StringBuilder sb, PdfObj o)
        {
            if (o == null || o is PdfNull) { sb.Append("null"); return; }
            var num = o as PdfNum;
            if (num != null) { sb.Append(num.Raw); return; }
            var name = o as PdfName;
            if (name != null) { EmitName(sb, name.V); return; }
            var r = o as PdfRef;
            if (r != null)
            {
                int n = mapRef(r);
                if (n > 0) sb.Append(n).Append(" 0 R");
                else sb.Append("null");
                return;
            }
            var str = o as PdfString;
            if (str != null)
            {
                sb.Append('<');
                foreach (byte b in str.V) sb.Append(b.ToString("x2"));
                sb.Append('>');
                return;
            }
            var bo = o as PdfBool;
            if (bo != null) { sb.Append(bo.V ? "true" : "false"); return; }
            var a = o as PdfArray;
            if (a != null)
            {
                sb.Append('[');
                for (int i = 0; i < a.Items.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    Emit(sb, a.Items[i]);
                }
                sb.Append(']');
                return;
            }
            var st = o as PdfStream;
            var d = st != null ? st.Dict : o as PdfDict;
            if (d != null)
            {
                sb.Append("<<");
                foreach (var k in d.Keys)
                {
                    EmitName(sb, k);
                    var v = d[k];
                    if (!(v is PdfName || v is PdfArray || v is PdfDict || v is PdfString)) sb.Append(' ');
                    Emit(sb, v);
                }
                sb.Append(">>");
                return;
            }
            sb.Append("null");
        }

        static void EmitName(StringBuilder sb, string n)
        {
            sb.Append('/');
            foreach (char c in n)
            {
                if (c < 0x21 || c > 0x7E || "()<>[]{}/%#".IndexOf(c) >= 0) sb.Append('#').Append(((int)c & 0xFF).ToString("X2"));
                else sb.Append(c);
            }
        }

        public void ClassicXref(int size, PdfDict trailer)
        {
            long at = pos;
            var sb = new StringBuilder();
            sb.Append("xref\n0 ").Append(size).Append('\n');
            sb.Append("0000000000 65535 f\r\n");
            for (int n = 1; n < size; n++)
            {
                long off;
                if (Offsets.TryGetValue(n, out off)) sb.Append(off.ToString("D10")).Append(" 00000 n\r\n");
                else sb.Append("0000000000 65535 f\r\n");
            }
            Raw(sb.ToString());
            trailer["Size"] = new PdfNum(size);
            Raw("trailer\n");
            Bytes(Serialize(trailer));
            Raw("\nstartxref\n" + at + "\n%%EOF\n");
        }

        /// xref 스트림(객체 스트림 안의 객체는 type 2)
        public void XrefStream(int xrefNum, Dictionary<int, KeyValuePair<int, int>> packed, PdfDict trailer)
        {
            long at = pos;
            int size = xrefNum + 1;
            var rows = new byte[size * 7];
            rows[0] = 0;
            rows[5] = 0xFF;
            rows[6] = 0xFF;
            for (int n = 1; n < size; n++)
            {
                int p = n * 7;
                KeyValuePair<int, int> where;
                long off;
                if (n == xrefNum) off = at;
                else if (packed.TryGetValue(n, out where))
                {
                    rows[p] = 2;
                    Put(rows, p + 1, where.Key, 4);
                    Put(rows, p + 5, where.Value, 2);
                    continue;
                }
                else if (!Offsets.TryGetValue(n, out off))
                {
                    continue;
                }
                rows[p] = 1;
                Put(rows, p + 1, off, 4);
            }
            var d = new PdfDict();
            d.SetName("Type", "XRef");
            d.SetNum("Size", size);
            d["W"] = PdfArray.Nums(1, 4, 2);
            foreach (var k in new[] { "Root", "Info", "ID" }) if (trailer[k] != null) d[k] = trailer[k];
            d.SetName("Filter", "FlateDecode");
            Object(xrefNum, new PdfStream(d, Zlib.Deflate(rows)));
            Raw("startxref\n" + at + "\n%%EOF\n");
        }

        static void Put(byte[] b, int p, long v, int n)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                b[p + i] = (byte)(v & 0xFF);
                v >>= 8;
            }
        }

        public void Dispose()
        {
            s.Flush();
        }

        public static PdfArray NewId(string seed)
        {
            using (var md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(seed + "|" + DateTime.Now.Ticks + "|" + Guid.NewGuid()));
                return new PdfArray(new PdfString(h), new PdfString(h));
            }
        }

        public static PdfString Date(DateTime t)
        {
            var off = TimeZoneInfo.Local.GetUtcOffset(t);
            string z = (off < TimeSpan.Zero ? "-" : "+") + Math.Abs(off.Hours).ToString("00") + "'" + Math.Abs(off.Minutes).ToString("00") + "'";
            return new PdfString(Encoding.ASCII.GetBytes("D:" + t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + z));
        }
    }

    /// 쓰이는 객체만 새 번호로 다시 쓰기
    public static class PdfRewrite
    {
        public static void Write(PdfDocument doc, List<int> order, Dictionary<int, PdfObj> replace, string path, bool compressPlain)
        {
            var map = new Dictionary<int, int>();
            for (int i = 0; i < order.Count; i++) map[order[i]] = i + 1;
            Func<PdfRef, int> mapRef = delegate (PdfRef r)
            {
                int n;
                return map.TryGetValue(r.Num, out n) ? n : 0;
            };
            string version = string.CompareOrdinal(doc.Version, "1.5") < 0 ? "1.5" : doc.Version;
            var trailer = new PdfDict();
            trailer["Root"] = doc.Trailer["Root"];
            if (doc.Trailer["Info"] is PdfRef && map.ContainsKey(((PdfRef)doc.Trailer["Info"]).Num)) trailer["Info"] = doc.Trailer["Info"];
            var id = doc.Resolve(doc.Trailer["ID"]) as PdfArray;
            trailer["ID"] = id != null && id.Items.Count == 2 ? id : PdfWriter.NewId(path);
            WritePacked(order, delegate (int old)
            {
                PdfObj o;
                return replace.TryGetValue(old, out o) ? o : doc.Get(old);
            }, map, mapRef, version, trailer, path, compressPlain);
        }

        /// 스트림은 그대로, 나머지는 객체 스트림에 묶어 xref 스트림으로 쓴다.
        public static void WritePacked(IList<int> order, Func<int, PdfObj> get, Dictionary<int, int> map, Func<PdfRef, int> mapRef, string version, PdfDict trailer, string path, bool compressPlain)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new PdfWriter(fs, mapRef))
            {
                w.Header(version);
                var loose = new List<KeyValuePair<int, PdfObj>>();
                foreach (int old in order)
                {
                    PdfObj o = get(old);
                    int n = map[old];
                    var s = o as PdfStream;
                    if (s != null)
                    {
                        if (compressPlain) s = MaybeCompress(s);
                        w.Object(n, s);
                    }
                    else loose.Add(new KeyValuePair<int, PdfObj>(n, o));
                }
                int next = order.Count + 1;
                var packed = new Dictionary<int, KeyValuePair<int, int>>();
                for (int start = 0; start < loose.Count; start += 100)
                {
                    var chunk = loose.Skip(start).Take(100).ToList();
                    var head = new StringBuilder();
                    var body = new MemoryStream();
                    for (int k = 0; k < chunk.Count; k++)
                    {
                        head.Append(chunk[k].Key).Append(' ').Append(body.Length).Append(' ');
                        var b = w.Serialize(chunk[k].Value);
                        body.Write(b, 0, b.Length);
                        body.WriteByte(10);
                    }
                    var hb = Encoding.ASCII.GetBytes(head.ToString());
                    var content = new byte[hb.Length + body.Length];
                    Buffer.BlockCopy(hb, 0, content, 0, hb.Length);
                    Buffer.BlockCopy(body.ToArray(), 0, content, hb.Length, (int)body.Length);
                    int stm = next++;
                    var d = new PdfDict();
                    d.SetName("Type", "ObjStm");
                    d.SetNum("N", chunk.Count);
                    d.SetNum("First", hb.Length);
                    d.SetName("Filter", "FlateDecode");
                    w.Object(stm, new PdfStream(d, Zlib.Deflate(content)));
                    for (int k = 0; k < chunk.Count; k++) packed[chunk[k].Key] = new KeyValuePair<int, int>(stm, k);
                }
                w.XrefStream(next, packed, trailer);
            }
        }

        static PdfStream MaybeCompress(PdfStream s)
        {
            if (s.Dict["Filter"] != null || s.Dict["DecodeParms"] != null || s.Data.Length < 256) return s;
            if (s.Dict.NameOf("Type") == "Metadata") return s;
            var z = Zlib.Deflate(s.Data);
            if (z.Length > s.Data.Length * 0.9) return s;
            var d = s.Dict.Copy();
            d.SetName("Filter", "FlateDecode");
            return new PdfStream(d, z);
        }
    }

    /// 쪽 크기 바로잡기. 한쇼가 만든 PDF는 쪽 크기가 슬라이드보다 작게 적힌다
    /// (2026-11-07 실측: 4:3 슬라이드 720×540pt가 303×228pt, 내용도 그만큼 줄여 그림).
    /// 쪽 상자를 늘리고 내용 앞뒤에 확대 줄(q … cm / Q)을 따로 붙인다. 원래 내용 스트림은 건드리지 않는다.
    public static class PdfPageSize
    {
        static readonly string[] Boxes = { "MediaBox", "CropBox", "BleedBox", "TrimBox", "ArtBox" };

        /// 모든 쪽을 width×height(pt)로 늘린다. 이미 2% 안으로 맞거나, 가로세로 비율이 달라 늘리면 찌그러질 쪽은 그대로 둔다.
        /// 하나라도 바꿨으면 파일을 고쳐 쓰고 true.
        public static bool FitTo(string path, double width, double height)
        {
            var doc = PdfDocument.Load(path);
            if (doc.Encrypted) return false;
            var order = doc.Reachable();
            if (order.Count == 0) return false;
            var pages = new List<int>();
            CollectPages(doc, doc.Resolve(doc.Trailer["Root"]) as PdfDict, pages);
            var replace = new Dictionary<int, PdfObj>();
            var added = new List<int>();
            var wrappers = new Dictionary<string, int[]>();
            int next = order.Max() + 1;
            foreach (int num in pages)
            {
                var page = doc.Get(num) as PdfDict;
                if (page == null) continue;
                var media = Box(doc, Inherited(doc, page, "MediaBox"));
                if (media == null) continue;
                double pw = media[2] - media[0], ph = media[3] - media[1];
                if (pw <= 0 || ph <= 0) continue;
                var rotNum = Inherited(doc, page, "Rotate") as PdfNum;
                int rot = rotNum == null ? 0 : ((rotNum.I % 360) + 360) % 360;
                double tw = rot == 90 || rot == 270 ? height : width;
                double th = rot == 90 || rot == 270 ? width : height;
                double sx = tw / pw, sy = th / ph;
                if (Math.Abs(sx - 1) < 0.02 && Math.Abs(sy - 1) < 0.02) continue;
                if (Math.Abs(sx / sy - 1) > 0.03) continue;

                var np = page.Copy();
                foreach (var key in Boxes)
                {
                    var b = Box(doc, key == "MediaBox" || key == "CropBox" ? Inherited(doc, page, key) : doc.Resolve(page[key]));
                    if (b != null) np[key] = PdfArray.Nums(b[0] * sx, b[1] * sy, b[2] * sx, b[3] * sy);
                }

                string scaleKey = PdfNum.Fmt(sx) + " " + PdfNum.Fmt(sy);
                int[] wrap;
                if (!wrappers.TryGetValue(scaleKey, out wrap))
                {
                    wrap = new[] { next++, next++ };
                    replace[wrap[0]] = new PdfStream(new PdfDict(), Encoding.ASCII.GetBytes("q " + PdfNum.Fmt(sx) + " 0 0 " + PdfNum.Fmt(sy) + " 0 0 cm\n"));
                    replace[wrap[1]] = new PdfStream(new PdfDict(), Encoding.ASCII.GetBytes("\nQ\n"));
                    added.Add(wrap[0]);
                    added.Add(wrap[1]);
                    wrappers[scaleKey] = wrap;
                }
                var contents = new PdfArray();
                contents.Items.Add(new PdfRef(wrap[0], 0));
                var oldContents = page["Contents"];
                var oldArray = doc.Resolve(oldContents) as PdfArray;
                if (oldArray != null) contents.Items.AddRange(oldArray.Items);
                else if (oldContents is PdfRef) contents.Items.Add(oldContents);
                contents.Items.Add(new PdfRef(wrap[1], 0));
                np["Contents"] = contents;

                var annotsObj = page["Annots"];
                var annots = doc.Resolve(annotsObj) as PdfArray;
                if (annots != null)
                {
                    var na = new PdfArray();
                    foreach (var item in annots.Items)
                    {
                        var ad = doc.Resolve(item) as PdfDict;
                        var r = item as PdfRef;
                        if (ad == null || (r != null && replace.ContainsKey(r.Num)))
                        {
                            na.Items.Add(item);
                            continue;
                        }
                        var nd = ad.Copy();
                        ScaleNums(doc, nd, "Rect", sx, sy);
                        ScaleNums(doc, nd, "QuadPoints", sx, sy);
                        if (r != null)
                        {
                            replace[r.Num] = nd;
                            na.Items.Add(item);
                        }
                        else na.Items.Add(nd);
                    }
                    if (annotsObj is PdfRef) replace[((PdfRef)annotsObj).Num] = na;
                    else np["Annots"] = na;
                }
                replace[num] = np;
            }
            if (added.Count == 0) return false;
            var all = new List<int>(order);
            all.AddRange(added);
            string tmp = path + ".tmp";
            try
            {
                PdfRewrite.Write(doc, all, replace, tmp, false);
                try { File.Replace(tmp, path, null); }
                catch (Exception)
                {
                    File.Copy(tmp, path, true);
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return true;
        }

        internal static void CollectPages(PdfDocument doc, PdfDict catalog, List<int> pages)
        {
            if (catalog == null) return;
            var root = catalog["Pages"] as PdfRef;
            if (root == null) return;
            var seen = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(root.Num);
            while (stack.Count > 0)
            {
                int num = stack.Pop();
                if (!seen.Add(num)) continue;
                var node = doc.Get(num) as PdfDict;
                if (node == null) continue;
                var kids = doc.Resolve(node["Kids"]) as PdfArray;
                if (node.NameOf("Type") == "Pages" || (kids != null && node.NameOf("Type") != "Page"))
                {
                    if (kids == null) continue;
                    for (int i = kids.Items.Count - 1; i >= 0; i--)
                    {
                        var k = kids.Items[i] as PdfRef;
                        if (k != null) stack.Push(k.Num);
                    }
                }
                else pages.Add(num);
            }
        }

        /// 쪽에 없으면 부모(Pages)에서 물려받은 값
        internal static PdfObj Inherited(PdfDocument doc, PdfDict page, string key)
        {
            var d = page;
            for (int depth = 0; d != null && depth < 64; depth++)
            {
                var v = d[key];
                if (v != null) return doc.Resolve(v);
                d = doc.Resolve(d["Parent"]) as PdfDict;
            }
            return null;
        }

        internal static double[] Box(PdfDocument doc, PdfObj o)
        {
            var a = doc.Resolve(o) as PdfArray;
            if (a == null || a.Items.Count != 4) return null;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                var n = doc.Resolve(a.Items[i]) as PdfNum;
                if (n == null) return null;
                v[i] = n.V;
            }
            return new[] { Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]) };
        }

        static void ScaleNums(PdfDocument doc, PdfDict d, string key, double sx, double sy)
        {
            var a = doc.Resolve(d[key]) as PdfArray;
            if (a == null) return;
            var na = new PdfArray();
            for (int i = 0; i < a.Items.Count; i++)
            {
                var n = doc.Resolve(a.Items[i]) as PdfNum;
                if (n == null) return;
                na.Items.Add(new PdfNum(n.V * (i % 2 == 0 ? sx : sy)));
            }
            d[key] = na;
        }
    }

    /// PDF 용량 줄이기. 그림을 골라 JPEG으로 다시 누르고, 긴 변을 longSide 안으로 줄인다.
    /// 고르는 규칙(PDF 1.7 표준 ISO 32000-1 8.9절 '그림'을 보고 정함):
    ///  - 다른 그림의 투명도(SMask)로 쓰이는 그림, 1비트 마스크 그림(ImageMask), 색 키 마스크(/Mask 배열)가 붙은 그림은 그대로 둔다.
    ///    JPEG은 색을 조금씩 바꾸므로 색 키가 어긋나고 마스크 경계가 번진다.
    ///    마스크 그림(/Mask 스트림)은 바탕 그림과 크기가 달라도 되므로(표준 8.9.6.3) 바탕 그림은 줄여도 된다.
    ///  - 성분마다 8비트인 회색·RGB(ICC 프로필 포함) 그림만 다룬다. /Decode가 기본값이 아니면(색 뒤집기 등) 그대로 둔다.
    ///  - 풀 수 있는 것: JPEG(DCT), 그리고 압축 없음·Flate·ASCII85·ASCIIHex·RunLength(이어 붙인 것 포함). JPX·JBIG2·CCITT는 둔다.
    ///  - 32KB보다 작은 그림은 두고, 다시 누른 결과가 원래보다 10% 넘게 작을 때만 바꾼다. 조금 줄이자고 화질을 버리지 않는다.
    public static class PdfShrink
    {
        const int SmallPicture = 32 * 1024;
        const double MustSave = 0.10;
        static readonly HashSet<string> Rewritten = new HashSet<string> { "Length", "Filter", "DecodeParms", "Width", "Height", "BitsPerComponent", "ColorSpace", "Decode", "DL" };

        public class Result
        {
            public int Pictures;
            public int Recoded;
            public long Before;
            public long After;
        }

        public static Result Run(string src, string dst, int longSide, int quality, Job job)
        {
            var doc = PdfDocument.Load(src);
            if (doc.Encrypted) throw new UserFacingException("암호나 편집 제한이 걸린 PDF는 줄일 수 없어요.");
            var order = doc.Reachable();
            var softMasks = SoftMaskPictures(doc, order);
            var pictures = new List<int>();
            foreach (int num in order)
            {
                var s = doc.Get(num) as PdfStream;
                if (s != null && s.Dict.NameOf("Subtype") == "Image" && !softMasks.Contains(num)) pictures.Add(num);
            }
            var res = new Result { Pictures = pictures.Count };
            var replace = new Dictionary<int, PdfObj>();
            for (int i = 0; i < pictures.Count; i++)
            {
                job.Check();
                job.Report("그림 " + (i + 1) + " / " + pictures.Count + " 살펴보는 중", (double)i / pictures.Count);
                var original = (PdfStream)doc.Get(pictures[i]);
                PdfStream smaller = null;
                try
                {
                    smaller = Recompress(doc, original, longSide, quality);
                }
                catch (Exception ex)
                {
                    Log.Write("그림 하나 건너뜀: " + ex.GetType().Name);
                }
                if (smaller == null || smaller.Data.Length > original.Data.Length * (1 - MustSave)) continue;
                replace[pictures[i]] = smaller;
                res.Recoded++;
                res.Before += original.Data.Length;
                res.After += smaller.Data.Length;
            }
            job.Report("새 PDF로 쓰는 중", -1);
            PdfRewrite.Write(doc, order, replace, dst, true);
            return res;
        }

        /// 다른 그림이 /SMask로 가리키는 그림 번호들
        static HashSet<int> SoftMaskPictures(PdfDocument doc, IEnumerable<int> order)
        {
            var set = new HashSet<int>();
            foreach (int num in order)
            {
                var s = doc.Get(num) as PdfStream;
                var target = s == null ? null : s.Dict["SMask"] as PdfRef;
                if (target != null) set.Add(target.Num);
            }
            return set;
        }

        static PdfStream Recompress(PdfDocument doc, PdfStream s, int longSide, int quality)
        {
            var d = s.Dict;
            if (s.Data.Length < SmallPicture) return null;
            var stencil = doc.Resolve(d["ImageMask"]) as PdfBool;
            if (stencil != null && stencil.V) return null;
            if (doc.Resolve(d["Mask"]) is PdfArray) return null;
            if (doc.IntOf(d, "BitsPerComponent", 0) != 8) return null;
            int w = doc.IntOf(d, "Width", 0), h = doc.IntOf(d, "Height", 0);
            if (w < 1 || h < 1) return null;
            bool profile;
            int channels = ColorChannels(doc, d, out profile);
            if (channels == 0 || !PlainDecode(doc, d, channels)) return null;
            var pixels = Pixels(doc, s, w, h, channels);
            if (pixels == null) return null;
            var fitted = Imaging.Fit(pixels, longSide);
            byte[] jpeg = channels == 1 ? Imaging.Jpeg(Imaging.ToGray8(fitted), quality) : Imaging.Jpeg(fitted, quality);
            var nd = new PdfDict();
            foreach (var k in d.Keys) if (!Rewritten.Contains(k)) nd[k] = d[k];
            nd.SetNum("Width", fitted.PixelWidth);
            nd.SetNum("Height", fitted.PixelHeight);
            nd.SetNum("BitsPerComponent", 8);
            nd["ColorSpace"] = profile ? d["ColorSpace"] : new PdfName(channels == 1 ? "DeviceGray" : "DeviceRGB");
            nd.SetName("Filter", "DCTDecode");
            return new PdfStream(nd, jpeg);
        }

        /// 그림의 픽셀. 풀 수 없거나 모양이 맞지 않으면 null.
        static System.Windows.Media.Imaging.BitmapSource Pixels(PdfDocument doc, PdfStream s, int w, int h, int channels)
        {
            PdfDict parms;
            string only = PdfDocument.SingleFilter(doc, s.Dict, out parms);
            if (only == "DCTDecode" || only == "DCT")
            {
                var info = Imaging.ReadJpeg(s.Data);
                if (info == null || info.Orientation != 1 || info.Components != channels) return null;
                return Imaging.Decoder(s.Data).Frames[0];
            }
            byte[] samples;
            try
            {
                samples = doc.Decode(s);
            }
            catch (PdfException)
            {
                return null;
            }
            if (samples.LongLength < (long)w * h * channels) return null;
            return Imaging.FromSamples(samples, w, h, channels);
        }

        /// 회색 1, RGB 3. ICC 프로필(N이 1이나 3)도 받는다. 그 밖의 색 공간(CMYK·색 번호표·Lab 등)은 0.
        static int ColorChannels(PdfDocument doc, PdfDict d, out bool profile)
        {
            profile = false;
            var cs = doc.Resolve(d["ColorSpace"]);
            var name = cs as PdfName;
            if (name != null)
            {
                if (name.V == "DeviceGray" || name.V == "G") return 1;
                if (name.V == "DeviceRGB" || name.V == "RGB") return 3;
                return 0;
            }
            var arr = cs as PdfArray;
            if (arr == null || arr.Items.Count < 2) return 0;
            var family = doc.Resolve(arr.Items[0]) as PdfName;
            var icc = doc.Resolve(arr.Items[1]) as PdfStream;
            if (family == null || family.V != "ICCBased" || icc == null) return 0;
            int n = doc.IntOf(icc.Dict, "N", 0);
            if (n != 1 && n != 3) return 0;
            profile = true;
            return n;
        }

        /// /Decode가 없거나 기본값([0 1 0 1 …])이면 true
        static bool PlainDecode(PdfDocument doc, PdfDict d, int channels)
        {
            var raw = doc.Resolve(d["Decode"]);
            if (raw is PdfNull) return true;
            var arr = raw as PdfArray;
            if (arr == null || arr.Items.Count != channels * 2) return false;
            for (int i = 0; i < arr.Items.Count; i++)
            {
                var n = doc.Resolve(arr.Items[i]) as PdfNum;
                if (n == null || Math.Abs(n.V - (i % 2)) > 1e-6) return false;
            }
            return true;
        }
    }

    /// 그림 여러 장을 A4 PDF로
    public static class ImagePdf
    {
        const double A4W = 595.276, A4H = 841.89, Margin = 28;

        class Picture
        {
            public PdfStream Stream;
            public int W;
            public int H;
        }

        public static void Write(IList<string> images, string path, Job job)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new PdfWriter(fs, r => r.Num))
            {
                w.Header("1.4");
                int next = 4;
                var kids = new PdfArray();
                int pageCount = 0;
                for (int i = 0; i < images.Count; i++)
                {
                    job.Check();
                    job.Report("그림 " + (i + 1) + " / " + images.Count + " 넣는 중", (double)i / images.Count);
                    foreach (var pic in Pictures(images[i]))
                    {
                        bool landscape = pic.W > pic.H;
                        double pw = landscape ? A4H : A4W, ph = landscape ? A4W : A4H;
                        double scale = Math.Min(Math.Min((pw - 2 * Margin) / pic.W, (ph - 2 * Margin) / pic.H), 0.75);
                        double dw = pic.W * scale, dh = pic.H * scale;
                        double x = (pw - dw) / 2, y = (ph - dh) / 2;
                        int imgNum = next++, contNum = next++, pageNum = next++;
                        w.Object(imgNum, pic.Stream);
                        string content = "q " + PdfNum.Fmt(dw) + " 0 0 " + PdfNum.Fmt(dh) + " " + PdfNum.Fmt(x) + " " + PdfNum.Fmt(y) + " cm /Im0 Do Q";
                        w.Object(contNum, new PdfStream(new PdfDict(), Encoding.ASCII.GetBytes(content)));
                        var page = new PdfDict();
                        page.SetName("Type", "Page");
                        page["Parent"] = new PdfRef(2, 0);
                        page["MediaBox"] = PdfArray.Nums(0, 0, pw, ph);
                        var xo = new PdfDict();
                        xo["Im0"] = new PdfRef(imgNum, 0);
                        var res = new PdfDict();
                        res["XObject"] = xo;
                        page["Resources"] = res;
                        page["Contents"] = new PdfRef(contNum, 0);
                        w.Object(pageNum, page);
                        kids.Items.Add(new PdfRef(pageNum, 0));
                        pageCount++;
                    }
                }
                var pages = new PdfDict();
                pages.SetName("Type", "Pages");
                pages["Kids"] = kids;
                pages.SetNum("Count", pageCount);
                w.Object(2, pages);
                var cat = new PdfDict();
                cat.SetName("Type", "Catalog");
                cat["Pages"] = new PdfRef(2, 0);
                w.Object(1, cat);
                var info = new PdfDict();
                info["Producer"] = PdfString.Text(AppInfo.Name + " " + AppInfo.Version);
                info["CreationDate"] = PdfWriter.Date(DateTime.Now);
                w.Object(3, info);
                var trailer = new PdfDict();
                trailer["Root"] = new PdfRef(1, 0);
                trailer["Info"] = new PdfRef(3, 0);
                trailer["ID"] = PdfWriter.NewId(path);
                w.ClassicXref(next, trailer);
            }
        }

        static IEnumerable<Picture> Pictures(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            var ji = Imaging.ReadJpeg(file);
            if (ji != null && ji.Orientation == 1 && (ji.Components == 1 || ji.Components == 3) && ji.Precision == 8 && !ji.Arithmetic)
            {
                yield return new Picture { Stream = ImageStream(file, ji.Width, ji.Height, ji.Components == 1, "DCTDecode", null), W = ji.Width, H = ji.Height };
                yield break;
            }
            var dec = Imaging.Decoder(path);
            string ext = Formats.Ext(path);
            bool lossless = ext == ".png" || ext == ".bmp" || ext == ".gif" || ext == ".tif" || ext == ".tiff";
            int frames = ext == ".tif" || ext == ".tiff" ? dec.Frames.Count : 1;
            for (int f = 0; f < frames; f++)
            {
                var bmp = Imaging.Upright(dec.Frames[f]);
                bool gray = Imaging.IsGray(bmp);
                if (lossless)
                {
                    int w, h;
                    byte[] samples = Imaging.Samples(bmp, gray, out w, out h);
                    int c = gray ? 1 : 3;
                    var parms = new PdfDict();
                    parms.SetNum("Predictor", 15);
                    parms.SetNum("Colors", c);
                    parms.SetNum("BitsPerComponent", 8);
                    parms.SetNum("Columns", w);
                    byte[] z = Zlib.Deflate(Zlib.PngFilter(samples, w, h, c));
                    yield return new Picture { Stream = ImageStream(z, w, h, gray, "FlateDecode", parms), W = w, H = h };
                }
                else
                {
                    byte[] jpg = Imaging.Jpeg(bmp, 92);
                    yield return new Picture { Stream = ImageStream(jpg, bmp.PixelWidth, bmp.PixelHeight, gray, "DCTDecode", null), W = bmp.PixelWidth, H = bmp.PixelHeight };
                }
            }
        }

        static PdfStream ImageStream(byte[] data, int w, int h, bool gray, string filter, PdfDict parms)
        {
            var d = new PdfDict();
            d.SetName("Type", "XObject");
            d.SetName("Subtype", "Image");
            d.SetNum("Width", w);
            d.SetNum("Height", h);
            d.SetName("ColorSpace", gray ? "DeviceGray" : "DeviceRGB");
            d.SetNum("BitsPerComponent", 8);
            d.SetName("Filter", filter);
            if (parms != null) d["DecodeParms"] = parms;
            return new PdfStream(d, data);
        }
    }
}
