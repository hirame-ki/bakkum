// PDF 암호 걸기·풀기. 표준 보안 처리기(ISO 32000-2, 7.6절)를 따른다.
// 풀기: RC4(40·128비트), AES-128, AES-256(R5·R6). 사용자가 넣은 암호가 맞을 때만 푼다.
// 걸기: AES-256(R6). 객체 스트림 없이 쓴다(문자열·스트림을 객체마다 잠근다).
// 인쇄·복사 제한만 걸린 PDF는 소유자 암호를 알아야 푼다. 제한을 몰래 없애는 기능은 만들지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bakkum
{
    public sealed class PdfCrypt
    {
        enum Method { None, Rc4, Aes128, Aes256 }

        static readonly byte[] Pad =
        {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
        };

        byte[] key;
        Method stm, str;
        bool encryptMetadata = true;

        /// 소유자 암호로 열었는지
        public bool ViaOwner;
        /// 인쇄·복사 등 제한이 하나도 없는지
        public bool FullPermissions;

        PdfCrypt() { }

        // ── 열기 ──

        /// 암호가 맞으면 만들어 돌려주고, 틀리면 null.
        public static PdfCrypt Open(PdfDocument doc, PdfDict enc, byte[] id0, string password)
        {
            if (enc.NameOf("Filter") != "Standard") throw new UserFacingException("인증서로 잠긴 PDF는 풀 수 없어요. 암호로 잠근 PDF만 풀 수 있어요.");
            int v = doc.IntOf(enc, "V", 0), r = doc.IntOf(enc, "R", 0);
            if (v < 1 || v > 5 || r < 2 || r > 6) throw new UserFacingException("이 방식으로 잠긴 PDF는 풀 수 없어요.");
            var c = new PdfCrypt();
            var em = doc.Resolve(enc["EncryptMetadata"]) as PdfBool;
            c.encryptMetadata = em == null || em.V;
            int bytes = 5;
            if (v >= 2) bytes = doc.IntOf(enc, "Length", 40) / 8;
            if (v == 4 || v == 5)
            {
                var cf = doc.Resolve(enc["CF"]) as PdfDict;
                c.stm = MethodOf(doc, cf, enc.NameOf("StmF") ?? "Identity");
                c.str = MethodOf(doc, cf, enc.NameOf("StrF") ?? "Identity");
                if (v == 4) bytes = 16;
            }
            else c.stm = c.str = Method.Rc4;
            if (bytes < 5 || bytes > 32) bytes = 16;

            byte[] o = Bytes(doc, enc, "O"), u = Bytes(doc, enc, "U");
            var pNum = doc.Resolve(enc["P"]) as PdfNum;
            int p = pNum == null ? 0 : unchecked((int)(long)pNum.V);
            int mask = r == 2 ? 0x3C : 0xF3C;
            c.FullPermissions = (p & mask) == mask;

            if (r >= 5)
            {
                if (!c.OpenAes256(doc, enc, r, o, u, password)) return null;
                return c;
            }
            foreach (byte[] pw in Candidates(password))
            {
                if (c.TryUser(PadPassword(pw), o, u, p, id0, r, bytes)) return c;
                if (c.TryOwner(pw, o, u, p, id0, r, bytes))
                {
                    c.ViaOwner = true;
                    return c;
                }
            }
            return null;
        }

        static IEnumerable<byte[]> Candidates(string password)
        {
            // 옛 방식은 문자 인코딩을 정해 두지 않았다. 한글 암호를 어떤 프로그램이 썼는지 몰라 차례로 맞춰 본다.
            var seen = new HashSet<string>();
            foreach (var enc in new[] { Encoding.GetEncoding(949), Encoding.UTF8, Encoding.GetEncoding(28591) })
            {
                byte[] b = enc.GetBytes(password);
                if (seen.Add(Convert.ToBase64String(b))) yield return b;
            }
        }

        static Method MethodOf(PdfDocument doc, PdfDict cf, string name)
        {
            if (name == "Identity" || cf == null) return Method.None;
            var f = doc.Resolve(cf[name]) as PdfDict;
            if (f == null) return Method.None;
            switch (f.NameOf("CFM"))
            {
                case "V2": return Method.Rc4;
                case "AESV2": return Method.Aes128;
                case "AESV3": return Method.Aes256;
            }
            return Method.None;
        }

        static byte[] Bytes(PdfDocument doc, PdfDict d, string key)
        {
            var s = doc.Resolve(d[key]) as PdfString;
            return s == null ? new byte[0] : s.V;
        }

        static byte[] PadPassword(byte[] pw)
        {
            var o = new byte[32];
            int n = Math.Min(32, pw.Length);
            Buffer.BlockCopy(pw, 0, o, 0, n);
            Buffer.BlockCopy(Pad, 0, o, n, 32 - n);
            return o;
        }

        static byte[] Sub(byte[] b, int start, int count)
        {
            var o = new byte[Math.Max(0, Math.Min(count, b.Length - start))];
            if (o.Length > 0) Buffer.BlockCopy(b, start, o, 0, o.Length);
            return o;
        }

        static byte[] Concat(params byte[][] parts)
        {
            var o = new byte[parts.Sum(x => x.Length)];
            int at = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, o, at, p.Length);
                at += p.Length;
            }
            return o;
        }

        static byte[] Md5(byte[] b)
        {
            using (var m = MD5.Create()) return m.ComputeHash(b);
        }

        static byte[] Rc4(byte[] k, byte[] data)
        {
            var s = new byte[256];
            for (int i = 0; i < 256; i++) s[i] = (byte)i;
            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + s[i] + k[i % k.Length]) & 255;
                byte t = s[i];
                s[i] = s[j];
                s[j] = t;
            }
            var o = new byte[data.Length];
            int a = 0, b = 0;
            for (int n = 0; n < data.Length; n++)
            {
                a = (a + 1) & 255;
                b = (b + s[a]) & 255;
                byte t = s[a];
                s[a] = s[b];
                s[b] = t;
                o[n] = (byte)(data[n] ^ s[(s[a] + s[b]) & 255]);
            }
            return o;
        }

        byte[] FileKey(byte[] padded, byte[] o, int p, byte[] id0, int r, int n)
        {
            var pb = new[] { (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24) };
            var input = Concat(padded, Sub(o, 0, 32), pb, id0, r >= 4 && !encryptMetadata ? new byte[] { 255, 255, 255, 255 } : new byte[0]);
            byte[] h = Md5(input);
            if (r >= 3)
            {
                for (int i = 0; i < 50; i++) h = Md5(Sub(h, 0, n));
            }
            return Sub(h, 0, n);
        }

        bool TryUser(byte[] padded, byte[] o, byte[] u, int p, byte[] id0, int r, int n)
        {
            byte[] k = FileKey(padded, o, p, id0, r, n);
            bool ok;
            if (r == 2) ok = Rc4(k, Pad).SequenceEqual(Sub(u, 0, 32));
            else
            {
                byte[] x = Rc4(k, Md5(Concat(Pad, id0)));
                for (int i = 1; i <= 19; i++) x = Rc4(XorKey(k, i), x);
                ok = x.SequenceEqual(Sub(u, 0, 16));
            }
            if (ok) key = k;
            return ok;
        }

        bool TryOwner(byte[] pw, byte[] o, byte[] u, int p, byte[] id0, int r, int n)
        {
            byte[] h = Md5(PadPassword(pw));
            if (r >= 3)
            {
                for (int i = 0; i < 50; i++) h = Md5(h);
            }
            byte[] k = Sub(h, 0, n);
            byte[] x = Sub(o, 0, 32);
            if (r == 2) x = Rc4(k, x);
            else
            {
                for (int i = 19; i >= 0; i--) x = Rc4(XorKey(k, i), x);
            }
            return TryUser(x, o, u, p, id0, r, n);
        }

        static byte[] XorKey(byte[] k, int i)
        {
            var o = new byte[k.Length];
            for (int n = 0; n < k.Length; n++) o[n] = (byte)(k[n] ^ i);
            return o;
        }

        // ── AES-256 ──

        static byte[] Utf8Password(string password)
        {
            string s = password;
            try { s = s.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { }
            return Sub(Encoding.UTF8.GetBytes(s), 0, 127);
        }

        bool OpenAes256(PdfDocument doc, PdfDict enc, int r, byte[] o, byte[] u, string password)
        {
            if (o.Length < 48 || u.Length < 48) return false;
            byte[] pw = Utf8Password(password);
            byte[] u48 = Sub(u, 0, 48);
            byte[] ue = Bytes(doc, enc, "UE"), oe = Bytes(doc, enc, "OE");
            if (Hash(r, pw, Sub(o, 32, 8), u48).SequenceEqual(Sub(o, 0, 32)))
            {
                key = AesCbc(Hash(r, pw, Sub(o, 40, 8), u48), new byte[16], Sub(oe, 0, 32), false);
                ViaOwner = true;
                return true;
            }
            if (Hash(r, pw, Sub(u, 32, 8), new byte[0]).SequenceEqual(Sub(u, 0, 32)))
            {
                key = AesCbc(Hash(r, pw, Sub(u, 40, 8), new byte[0]), new byte[16], Sub(ue, 0, 32), false);
                return true;
            }
            return false;
        }

        static byte[] Hash(int r, byte[] pw, byte[] salt, byte[] udata)
        {
            byte[] k;
            using (var s = SHA256.Create()) k = s.ComputeHash(Concat(pw, salt, udata));
            if (r == 5) return k;
            // ISO 32000-2 알고리즘 2.B
            byte[] e = new byte[1];
            for (int i = 0; i < 64 || e[e.Length - 1] > i - 32; i++)
            {
                byte[] one = Concat(pw, k, udata);
                var k1 = new byte[one.Length * 64];
                for (int n = 0; n < 64; n++) Buffer.BlockCopy(one, 0, k1, n * one.Length, one.Length);
                e = AesCbc(Sub(k, 0, 16), Sub(k, 16, 16), k1, true);
                int mod = 0;
                for (int n = 0; n < 16; n++) mod += e[n];
                mod %= 3;
                if (mod == 0)
                {
                    using (var s = SHA256.Create()) k = s.ComputeHash(e);
                }
                else if (mod == 1)
                {
                    using (var s = SHA384.Create()) k = s.ComputeHash(e);
                }
                else
                {
                    using (var s = SHA512.Create()) k = s.ComputeHash(e);
                }
            }
            return Sub(k, 0, 32);
        }

        static byte[] AesCbc(byte[] k, byte[] iv, byte[] data, bool encrypt)
        {
            using (var a = new AesManaged())
            {
                a.Mode = CipherMode.CBC;
                a.Padding = PaddingMode.None;
                a.Key = k;
                a.IV = iv;
                using (var t = encrypt ? a.CreateEncryptor() : a.CreateDecryptor()) return t.TransformFinalBlock(data, 0, data.Length);
            }
        }

        // ── 객체 풀기 ──

        byte[] ObjectKey(int num, int gen, bool aes)
        {
            var extra = new[] { (byte)num, (byte)(num >> 8), (byte)(num >> 16), (byte)gen, (byte)(gen >> 8) };
            var salt = aes ? new byte[] { 0x73, 0x41, 0x6C, 0x54 } : new byte[0];
            return Sub(Md5(Concat(key, extra, salt)), 0, Math.Min(key.Length + 5, 16));
        }

        byte[] Decrypt(byte[] data, Method m, int num, int gen)
        {
            switch (m)
            {
                case Method.Rc4: return Rc4(ObjectKey(num, gen, false), data);
                case Method.Aes128: return AesPayload(ObjectKey(num, gen, true), data);
                case Method.Aes256: return AesPayload(key, data);
            }
            return data;
        }

        /// 앞 16바이트가 IV이고 끝에 채움이 붙은 AES 자료를 푼다. 길이가 어긋나면 되는 데까지 읽는다.
        static byte[] AesPayload(byte[] k, byte[] data)
        {
            if (data.Length < 32) return new byte[0];
            int len = (data.Length - 16) & ~15;
            byte[] plain = AesCbc(k, Sub(data, 0, 16), Sub(data, 16, len), false);
            int pad = plain[plain.Length - 1];
            if (pad >= 1 && pad <= 16 && pad <= plain.Length) return Sub(plain, 0, plain.Length - pad);
            return plain;
        }

        public PdfObj DecryptObject(PdfObj o, int num, int gen)
        {
            var st = o as PdfStream;
            if (st != null)
            {
                DecryptDict(st.Dict, num, gen);
                string type = st.Dict.NameOf("Type");
                if (type == "XRef" || (type == "Metadata" && !encryptMetadata) || IdentityFilter(st.Dict)) return st;
                st.Data = Decrypt(st.Data, stm, num, gen);
                return st;
            }
            return Walk(o, num, gen);
        }

        static bool IdentityFilter(PdfDict d)
        {
            // /Filter 에 Crypt가 있고 /Name이 없거나 Identity면 이 스트림은 잠기지 않았다.
            var f = d["Filter"];
            var names = f is PdfArray ? ((PdfArray)f).Items.OfType<PdfName>().Select(x => x.V).ToList() : (f is PdfName ? new List<string> { ((PdfName)f).V } : new List<string>());
            int i = names.IndexOf("Crypt");
            if (i < 0) return false;
            var p = d["DecodeParms"];
            PdfDict prm = p as PdfDict;
            if (p is PdfArray && ((PdfArray)p).Items.Count > i) prm = ((PdfArray)p).Items[i] as PdfDict;
            if (prm == null) return true;
            string name = prm.NameOf("Name");
            return name == null || name == "Identity";
        }

        void DecryptDict(PdfDict d, int num, int gen)
        {
            foreach (var k in d.Keys.ToList()) d[k] = Walk(d[k], num, gen);
        }

        PdfObj Walk(PdfObj o, int num, int gen)
        {
            var s = o as PdfString;
            if (s != null) return new PdfString(Decrypt(s.V, str, num, gen));
            var a = o as PdfArray;
            if (a != null)
            {
                for (int i = 0; i < a.Items.Count; i++) a.Items[i] = Walk(a.Items[i], num, gen);
                return a;
            }
            var d = o as PdfDict;
            if (d != null) DecryptDict(d, num, gen);
            return o;
        }

        // ── 걸기 ──

        static byte[] Random(int n)
        {
            var b = new byte[n];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return b;
        }

        /// order의 객체들을 AES-256으로 잠가 쓴다. 암호를 아는 사람은 열 수도, 다시 풀 수도 있다.
        public static void WriteLocked(PdfDocument doc, List<int> order, Dictionary<int, PdfObj> replace, string path, string password)
        {
            const int EncryptSlot = int.MaxValue;
            byte[] fileKey = Random(32);
            byte[] pw = Utf8Password(password);
            byte[] uv = Random(8), uk = Random(8), ov = Random(8), ok = Random(8);
            byte[] none = new byte[0];
            byte[] u = Concat(Hash(6, pw, uv, none), uv, uk);
            byte[] ue = AesCbc(Hash(6, pw, uk, none), new byte[16], fileKey, true);
            byte[] o = Concat(Hash(6, pw, ov, u), ov, ok);
            byte[] oe = AesCbc(Hash(6, pw, ok, u), new byte[16], fileKey, true);
            const int P = -4;
            byte[] perms = Concat(BitConverter.GetBytes(P), new byte[] { 255, 255, 255, 255 }, new byte[] { (byte)'T', (byte)'a', (byte)'d', (byte)'b' }, Random(4));
            using (var a = new AesManaged())
            {
                a.Mode = CipherMode.ECB;
                a.Padding = PaddingMode.None;
                a.Key = fileKey;
                using (var t = a.CreateEncryptor()) perms = t.TransformFinalBlock(perms, 0, 16);
            }
            var cf = new PdfDict();
            var std = new PdfDict();
            std.SetName("CFM", "AESV3");
            std.SetName("AuthEvent", "DocOpen");
            std.SetNum("Length", 32);
            cf["StdCF"] = std;
            var enc = new PdfDict();
            enc.SetName("Filter", "Standard");
            enc.SetNum("V", 5);
            enc.SetNum("R", 6);
            enc.SetNum("Length", 256);
            enc["P"] = new PdfNum((long)P);
            enc["O"] = new PdfString(o);
            enc["OE"] = new PdfString(oe);
            enc["U"] = new PdfString(u);
            enc["UE"] = new PdfString(ue);
            enc["Perms"] = new PdfString(perms);
            enc["CF"] = cf;
            enc.SetName("StmF", "StdCF");
            enc.SetName("StrF", "StdCF");

            var map = new Dictionary<int, int>();
            for (int i = 0; i < order.Count; i++) map[order[i]] = i + 1;
            map[EncryptSlot] = order.Count + 1;
            Func<PdfRef, int> mapRef = delegate (PdfRef r)
            {
                int n;
                return map.TryGetValue(r.Num, out n) ? n : 0;
            };
            int rootOld = ((PdfRef)doc.Trailer["Root"]).Num;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new PdfWriter(fs, mapRef))
            {
                w.Header("1.7");
                foreach (int old in order)
                {
                    PdfObj x;
                    if (!replace.TryGetValue(old, out x)) x = doc.Get(old);
                    if (old == rootOld)
                    {
                        var cat = ((PdfDict)x).Copy();
                        var ext = new PdfDict();
                        var adbe = new PdfDict();
                        adbe.SetName("BaseVersion", "1.7");
                        adbe.SetNum("ExtensionLevel", 8);
                        ext["ADBE"] = adbe;
                        cat["Extensions"] = ext;
                        x = cat;
                    }
                    w.Object(map[old], Seal(x, fileKey));
                }
                w.Object(order.Count + 1, enc);
                var trailer = new PdfDict();
                trailer["Root"] = doc.Trailer["Root"];
                if (doc.Trailer["Info"] is PdfRef && map.ContainsKey(((PdfRef)doc.Trailer["Info"]).Num)) trailer["Info"] = doc.Trailer["Info"];
                trailer["ID"] = PdfWriter.NewId(path);
                trailer["Encrypt"] = new PdfRef(EncryptSlot, 0);
                w.ClassicXref(order.Count + 2, trailer);
            }
        }

        static byte[] AesSeal(byte[] k, byte[] data)
        {
            int pad = 16 - data.Length % 16;
            var plain = new byte[data.Length + pad];
            Buffer.BlockCopy(data, 0, plain, 0, data.Length);
            for (int i = data.Length; i < plain.Length; i++) plain[i] = (byte)pad;
            byte[] iv = Random(16);
            return Concat(iv, AesCbc(k, iv, plain, true));
        }

        static PdfObj Seal(PdfObj o, byte[] k)
        {
            var st = o as PdfStream;
            if (st != null) return new PdfStream((PdfDict)Seal(st.Dict, k), AesSeal(k, st.Data));
            var s = o as PdfString;
            if (s != null) return new PdfString(AesSeal(k, s.V));
            var a = o as PdfArray;
            if (a != null)
            {
                var na = new PdfArray();
                foreach (var x in a.Items) na.Items.Add(Seal(x, k));
                return na;
            }
            var d = o as PdfDict;
            if (d != null)
            {
                var nd = new PdfDict();
                foreach (var key2 in d.Keys) nd[key2] = Seal(d[key2], k);
                return nd;
            }
            return o;
        }
    }
}
