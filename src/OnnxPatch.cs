// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// ONNX 모델 파일을 바이트 그대로 고치는 작은 도구(onnx 패키지 없이 protobuf 칸을 그대로 옮겨 적는다).
// Hugging Face의 Whisper 디코더(decoder_model_merged)는 If 마디 하나로 '첫 걸음'과 '이어지는 걸음'을 오가는데,
// 윈도우에 들어 있는 Windows ML(onnxruntime 1.17)에서는 이 꼴이 느려서 두 갈래를 따로 된 모델 두 개로 나눈다.
// 마지막 층(단어표 곱하기)은 다른 층처럼 8비트 정수 곱으로 바꾸고, 형식 번호(IR)는 Windows ML이 읽는 9까지로 낮춘다.
// 2026-11-11 시험판(파이썬)과 같은 방법이며, 같은 원본에서 같은 바이트가 나오는지 시험에서 확인한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Bakkum
{
    static class OnnxPatch
    {
        /// Windows ML(onnxruntime 1.17)이 읽는 가장 높은 형식 번호
        public const int MaxIr = 9;

        struct Field
        {
            public int Num, Wt, Vs, Ve, Entry;
        }

        sealed class Named
        {
            public string Name;
            public int S, E;
        }

        static long Varint(byte[] b, ref int pos)
        {
            int shift = 0;
            long result = 0;
            while (true)
            {
                byte x = b[pos++];
                result |= (long)(x & 0x7F) << shift;
                if (x < 0x80) return result;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("ONNX 파일 모양이 예상과 달라요.");
            }
        }

        static List<Field> Fields(byte[] b, int start, int end)
        {
            var list = new List<Field>();
            int pos = start;
            while (pos < end)
            {
                int entry = pos;
                long key = Varint(b, ref pos);
                int num = (int)(key >> 3), wt = (int)(key & 7);
                int vs, ve;
                if (wt == 0)
                {
                    vs = pos;
                    Varint(b, ref pos);
                    ve = pos;
                }
                else if (wt == 1)
                {
                    vs = pos;
                    ve = pos = pos + 8;
                }
                else if (wt == 2)
                {
                    long n = Varint(b, ref pos);
                    vs = pos;
                    ve = pos = checked((int)(pos + n));
                }
                else if (wt == 5)
                {
                    vs = pos;
                    ve = pos = pos + 4;
                }
                else throw new InvalidDataException("ONNX 파일 모양이 예상과 달라요(wire " + wt + ").");
                if (ve > end) throw new InvalidDataException("ONNX 파일이 잘렸어요.");
                list.Add(new Field { Num = num, Wt = wt, Vs = vs, Ve = ve, Entry = entry });
            }
            return list;
        }

        static string Str(byte[] b, Field f)
        {
            return Encoding.UTF8.GetString(b, f.Vs, f.Ve - f.Vs);
        }

        static void PutVarint(Stream s, long n)
        {
            while (true)
            {
                byte b = (byte)(n & 0x7F);
                n >>= 7;
                if (n != 0) s.WriteByte((byte)(b | 0x80));
                else
                {
                    s.WriteByte(b);
                    return;
                }
            }
        }

        /// 길이가 앞에 붙는 칸 하나(번호 num)
        static void PutLd(Stream s, int num, byte[] b, int off, int len)
        {
            PutVarint(s, (num << 3) | 2);
            PutVarint(s, len);
            s.Write(b, off, len);
        }

        static void PutLd(Stream s, int num, byte[] payload)
        {
            PutLd(s, num, payload, 0, payload.Length);
        }

        static void PutLd(Stream s, int num, string text)
        {
            PutLd(s, num, Encoding.UTF8.GetBytes(text));
        }

        static byte[] Ld(int num, byte[] payload)
        {
            var ms = new MemoryStream();
            PutLd(ms, num, payload);
            return ms.ToArray();
        }

        static byte[] Ld(int num, string text)
        {
            return Ld(num, Encoding.UTF8.GetBytes(text));
        }

        static byte[] Cat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        static byte[] VarintBytes(long n)
        {
            var ms = new MemoryStream();
            PutVarint(ms, n);
            return ms.ToArray();
        }

        static byte[] NodeBytes(string op, string[] inputs, string[] outputs, string name, params byte[][] attrs)
        {
            var ms = new MemoryStream();
            foreach (var i in inputs) PutLd(ms, 1, i);
            foreach (var o in outputs) PutLd(ms, 2, o);
            PutLd(ms, 3, name);
            PutLd(ms, 4, op);
            foreach (var a in attrs) PutLd(ms, 5, a);
            return ms.ToArray();
        }

        /// 정수 속성 하나(AttributeProto: name 1, i 3, type 20 = INT 2)
        static byte[] AttrInt(string name, long value)
        {
            return Cat(Ld(1, name), VarintBytes(3 << 3), VarintBytes(value), VarintBytes(20 << 3), VarintBytes(2));
        }

        sealed class Node
        {
            public readonly int S, E;
            public readonly List<string> Inputs = new List<string>(), Outputs = new List<string>();
            public readonly List<Field> Attrs = new List<Field>();
            public string Op = "", Name = "";

            public Node(byte[] b, int s, int e)
            {
                S = s;
                E = e;
                foreach (var f in Fields(b, s, e))
                {
                    if (f.Num == 1) Inputs.Add(Str(b, f));
                    else if (f.Num == 2) Outputs.Add(Str(b, f));
                    else if (f.Num == 3) Name = Str(b, f);
                    else if (f.Num == 4) Op = Str(b, f);
                    else if (f.Num == 5) Attrs.Add(f);
                }
            }

            /// 그래프를 품은 속성(If의 두 갈래 등): (속성 이름, 그래프 자리)
            public List<KeyValuePair<string, Field>> Graphs(byte[] b)
            {
                var list = new List<KeyValuePair<string, Field>>();
                foreach (var a in Attrs)
                {
                    string name = null;
                    var gs = new List<Field>();
                    foreach (var f in Fields(b, a.Vs, a.Ve))
                    {
                        if (f.Num == 1) name = Str(b, f);
                        else if (f.Num == 6 || f.Num == 11) gs.Add(f);
                    }
                    foreach (var g in gs) list.Add(new KeyValuePair<string, Field>(name, g));
                }
                return list;
            }
        }

        sealed class Graph
        {
            public readonly List<Node> Nodes = new List<Node>();
            public readonly List<Named> Inits = new List<Named>(), Inputs = new List<Named>(), Outputs = new List<Named>(), Sparse = new List<Named>();
            public readonly List<Field> ValueInfo = new List<Field>();
            public string Name = "";

            public Graph(byte[] b, int s, int e)
            {
                foreach (var f in Fields(b, s, e))
                {
                    if (f.Num == 1) Nodes.Add(new Node(b, f.Vs, f.Ve));
                    else if (f.Num == 2) Name = Str(b, f);
                    else if (f.Num == 5) Inits.Add(new Named { Name = SubName(b, f, 8), S = f.Vs, E = f.Ve });
                    else if (f.Num == 11) Inputs.Add(new Named { Name = SubName(b, f, 1), S = f.Vs, E = f.Ve });
                    else if (f.Num == 12) Outputs.Add(new Named { Name = SubName(b, f, 1), S = f.Vs, E = f.Ve });
                    else if (f.Num == 13) ValueInfo.Add(f);
                    else if (f.Num == 15) Sparse.Add(new Named { Name = SparseName(b, f), S = f.Vs, E = f.Ve });
                }
            }
        }

        /// 안쪽 칸 num의 글(텐서 이름은 8, ValueInfo 이름은 1)
        static string SubName(byte[] b, Field outer, int num)
        {
            foreach (var f in Fields(b, outer.Vs, outer.Ve))
                if (f.Num == num) return Str(b, f);
            return "";
        }

        static string SparseName(byte[] b, Field outer)
        {
            foreach (var f in Fields(b, outer.Vs, outer.Ve))
                if (f.Num == 1) return SubName(b, f, 8);
            return "";
        }

        /// ValueInfo의 형(칸 2) 바이트. 없으면 null.
        static byte[] InfoType(byte[] b, int s, int e)
        {
            foreach (var f in Fields(b, s, e))
            {
                if (f.Num != 2) continue;
                var t = new byte[f.Ve - f.Vs];
                Buffer.BlockCopy(b, f.Vs, t, 0, t.Length);
                return t;
            }
            return null;
        }

        /// 그래프(와 그 안의 그래프)가 읽지만 스스로 만들지 않는 이름들(차례대로, 겹치지 않게)
        static List<string> OuterRefs(byte[] b, Graph g)
        {
            var made = new HashSet<string>();
            foreach (var x in g.Inits) made.Add(x.Name);
            foreach (var x in g.Sparse) made.Add(x.Name);
            foreach (var x in g.Inputs) made.Add(x.Name);
            foreach (var n in g.Nodes)
                foreach (var o in n.Outputs)
                    if (o.Length > 0) made.Add(o);
            var used = new List<string>();
            foreach (var n in g.Nodes)
            {
                foreach (var i in n.Inputs) if (i.Length > 0) used.Add(i);
                foreach (var kv in n.Graphs(b)) used.AddRange(OuterRefs(b, new Graph(b, kv.Value.Vs, kv.Value.Ve)));
            }
            foreach (var x in g.Outputs) used.Add(x.Name);
            var seen = new HashSet<string>();
            var list = new List<string>();
            foreach (var u in used)
                if (!made.Contains(u) && seen.Add(u)) list.Add(u);
            return list;
        }

        /// 맨 바깥 칸에서 형식 번호(IR, 칸 1)
        static long IrOf(byte[] b)
        {
            foreach (var f in Fields(b, 0, b.Length))
            {
                if (f.Num == 1 && f.Wt == 0)
                {
                    int p = f.Vs;
                    return Varint(b, ref p);
                }
            }
            return 0;
        }

        /// 한 갈래로 된 새 모델을 out에 쓴다. keep(출력 이름)은 남길 출력.
        static void BuildBranch(byte[] b, List<Field> modelFields, Graph main, Node ifNode, Field branch, Func<string, bool> keep, string graphName, Stream outStream)
        {
            var g = new Graph(b, branch.Vs, branch.Ve);
            var refs = OuterRefs(b, g);
            var refSet = new HashSet<string>(refs);
            var mainInputs = main.Inputs.ToDictionary(x => x.Name);
            var mainInits = new Dictionary<string, Named>();
            foreach (var x in main.Inits) mainInits[x.Name] = x;
            var mainSparse = new Dictionary<string, Named>();
            foreach (var x in main.Sparse) mainSparse[x.Name] = x;

            // If 앞의 바깥 마디(encoder_hidden_states를 8비트로 바꾸는 마디 등)도 필요하면 같이 옮긴다
            var producers = new Dictionary<string, Node>();
            foreach (var n in main.Nodes)
                if (n != ifNode)
                    foreach (var o in n.Outputs) producers[o] = n;
            var pre = new List<Node>();
            var todo = new List<string>(refs);
            while (todo.Count > 0)
            {
                string r = todo[todo.Count - 1];
                todo.RemoveAt(todo.Count - 1);
                if (mainInputs.ContainsKey(r) || mainInits.ContainsKey(r) || mainSparse.ContainsKey(r)) continue;
                Node p;
                if (!producers.TryGetValue(r, out p)) throw new InvalidDataException("디코더가 읽는 이름을 만드는 곳이 없어요: " + r);
                if (pre.Contains(p)) continue;
                pre.Add(p);
                foreach (var i in p.Inputs)
                {
                    if (i.Length > 0 && !refSet.Contains(i))
                    {
                        refs.Add(i);
                        refSet.Add(i);
                        todo.Add(i);
                    }
                }
            }
            pre = pre.OrderBy(n => main.Nodes.IndexOf(n)).ToList();

            // 마지막 층: MatMul(hidden, DequantizeLinear(8비트 단어표))는 걸음마다 단어표 전체를 실수로 되돌리므로
            // 다른 층과 같은 DynamicQuantizeLinear + MatMulInteger 꼴로 바꾼다.
            var skip = new HashSet<Node>();
            var replace = new Dictionary<Node, List<byte[]>>();
            var dropInits = new HashSet<string>();
            var users = new Dictionary<string, List<Node>>();
            foreach (var n in g.Nodes)
                foreach (var i in n.Inputs)
                {
                    List<Node> l;
                    if (!users.TryGetValue(i, out l)) users[i] = l = new List<Node>();
                    l.Add(n);
                }
            Func<string, List<Node>> usersOf = name =>
            {
                List<Node> l;
                return users.TryGetValue(name, out l) ? l : new List<Node>();
            };
            int made = 0;
            foreach (var d in g.Nodes)
            {
                if (d.Op != "DequantizeLinear" || d.Inputs.Count < 3) continue;
                var us = usersOf(d.Outputs[0]);
                if (us.Count != 1 || us[0].Op != "MatMul" || us[0].Inputs.Count < 2 || us[0].Inputs[1] != d.Outputs[0]) continue;
                var m = us[0];
                string w = d.Inputs[0], ws = d.Inputs[1], wz = d.Inputs[2];
                string x = m.Inputs[0], y = m.Outputs[0];
                string p = made == 0 ? "lmhead_" : "lmhead" + made + "_";
                made++;
                replace[m] = new List<byte[]>
                {
                    NodeBytes("DynamicQuantizeLinear", new[] { x }, new[] { p + "xq", p + "xs", p + "xz" }, p + "quant"),
                    NodeBytes("MatMulInteger", new[] { p + "xq", w, p + "xz", wz }, new[] { p + "mm" }, p + "matmul"),
                    NodeBytes("Cast", new[] { p + "mm" }, new[] { p + "mmf" }, p + "cast", AttrInt("to", 1)),
                    NodeBytes("Mul", new[] { p + "xs", ws }, new[] { p + "scale" }, p + "scale"),
                    NodeBytes("Mul", new[] { p + "mmf", p + "scale" }, new[] { y }, p + "out"),
                };
                skip.Add(d);
            }
            // 같은 층이 MatMul(hidden, Transpose(실수 단어표))로 적혀 있고, 단어 찾기용 8비트 사본(<이름>_quantized·_scale·_zero_point)이
            // 같이 들어 있으면 8비트 사본을 쓴다(4바이트 단어표를 메모리에 두지 않게).
            var allInits = new HashSet<string>(mainInits.Keys);
            foreach (var x in g.Inits) allInits.Add(x.Name);
            int made2 = 0;
            foreach (var m in g.Nodes)
            {
                if (m.Op != "MatMul" || replace.ContainsKey(m) || m.Inputs.Count != 2) continue;
                var ts = g.Nodes.Where(n => n.Op == "Transpose" && n.Outputs.Count > 0 && n.Outputs[0] == m.Inputs[1]).ToList();
                if (ts.Count != 1 || usersOf(m.Inputs[1]).Count != 1) continue;
                var t = ts[0];
                string src = t.Inputs[0];
                string q = src + "_quantized", qs = src + "_scale", qz = src + "_zero_point";
                if (!allInits.Contains(src) || !allInits.Contains(q) || !allInits.Contains(qs) || !allInits.Contains(qz)) continue;
                string x = m.Inputs[0], y = m.Outputs[0];
                string p = made2 == 0 ? "lmhead2_" : "lmhead2" + made2 + "_";
                made2++;
                replace[m] = new List<byte[]>
                {
                    NodeBytes("Transpose", new[] { q }, new[] { p + "wt" }, p + "transpose"),
                    NodeBytes("DynamicQuantizeLinear", new[] { x }, new[] { p + "xq", p + "xs", p + "xz" }, p + "quant"),
                    NodeBytes("MatMulInteger", new[] { p + "xq", p + "wt", p + "xz", qz }, new[] { p + "mm" }, p + "matmul"),
                    NodeBytes("Cast", new[] { p + "mm" }, new[] { p + "mmf" }, p + "cast", AttrInt("to", 1)),
                    NodeBytes("Mul", new[] { p + "xs", qs }, new[] { p + "scale" }, p + "scale"),
                    NodeBytes("Mul", new[] { p + "mmf", p + "scale" }, new[] { y }, p + "out"),
                };
                skip.Add(t);
                if (usersOf(src).Count == 1) dropInits.Add(src);
                foreach (var extra in new[] { q, qs, qz })
                {
                    if (refSet.Contains(extra)) continue;
                    refs.Add(extra);
                    refSet.Add(extra);
                }
            }

            var gb = new MemoryStream();
            foreach (var n in pre) PutLd(gb, 1, b, n.S, n.E - n.S);
            foreach (var n in g.Nodes)
            {
                if (skip.Contains(n)) continue;
                List<byte[]> rep;
                if (replace.TryGetValue(n, out rep))
                {
                    foreach (var nb in rep) PutLd(gb, 1, nb);
                    continue;
                }
                PutLd(gb, 1, b, n.S, n.E - n.S);
            }
            var names = new List<KeyValuePair<string, byte[]>>();
            for (int i = 0; i < g.Outputs.Count; i++)
            {
                var o = g.Outputs[i];
                string want = ifNode.Outputs[i];
                if (!keep(want)) continue;
                names.Add(new KeyValuePair<string, byte[]>(want, InfoType(b, o.S, o.E)));
                if (o.Name != want) PutLd(gb, 1, Cat(Ld(1, o.Name), Ld(2, want), Ld(3, "rename_" + i), Ld(4, "Identity")));
            }
            PutLd(gb, 2, graphName);
            foreach (var x in g.Inits)
                if (!dropInits.Contains(x.Name)) PutLd(gb, 5, b, x.S, x.E - x.S);
            foreach (var r in refs)
            {
                Named x;
                if (mainInits.TryGetValue(r, out x) && !dropInits.Contains(r)) PutLd(gb, 5, b, x.S, x.E - x.S);
            }
            foreach (var x in main.Inputs)
                if (refSet.Contains(x.Name)) PutLd(gb, 11, b, x.S, x.E - x.S);
            foreach (var kv in names)
            {
                var info = new MemoryStream();
                PutLd(info, 1, kv.Key);
                if (kv.Value != null) PutLd(info, 2, kv.Value);
                PutLd(gb, 12, info.ToArray());
            }
            foreach (var f in g.ValueInfo) PutLd(gb, 13, b, f.Vs, f.Ve - f.Vs);
            foreach (var x in g.Sparse) PutLd(gb, 15, b, x.S, x.E - x.S);
            foreach (var r in refs)
            {
                Named x;
                if (mainSparse.TryGetValue(r, out x)) PutLd(gb, 15, b, x.S, x.E - x.S);
            }

            foreach (var f in modelFields)
            {
                if (f.Num == 7) PutLd(outStream, 7, gb.GetBuffer(), 0, (int)gb.Length);
                else if (f.Num == 1 && f.Wt == 0)
                {
                    int p = f.Vs;
                    long ir = Varint(b, ref p);
                    PutVarint(outStream, 1 << 3);
                    PutVarint(outStream, Math.Min(ir, MaxIr));
                }
                else outStream.Write(b, f.Entry, f.Ve - f.Entry);
            }
        }

        /// 합쳐진 디코더를 '첫 걸음'(캐시 없이)과 '이어지는 걸음'(캐시로) 두 파일로 나눈다.
        /// 첫 파일은 모든 출력, 다음 파일은 인코더 쪽 캐시(.encoder.)를 뺀 출력만 낸다.
        public static void SplitDecoder(string merged, string firstPath, string nextPath)
        {
            byte[] b = File.ReadAllBytes(merged);
            var modelFields = Fields(b, 0, b.Length);
            var gf = modelFields.Where(f => f.Num == 7).ToList();
            if (gf.Count != 1) throw new InvalidDataException("ONNX 파일에 그래프가 없어요.");
            var main = new Graph(b, gf[0].Vs, gf[0].Ve);
            var ifs = main.Nodes.Where(n => n.Op == "If").ToList();
            if (ifs.Count != 1) throw new InvalidDataException("나눌 갈래(If 마디)가 " + ifs.Count + "개예요.");
            var node = ifs[0];
            var branches = node.Graphs(b);
            string past = null;
            foreach (var kv in branches)
            {
                if (OuterRefs(b, new Graph(b, kv.Value.Vs, kv.Value.Ve)).Any(r => r.StartsWith("past_key_values.", StringComparison.Ordinal))) past = kv.Key;
            }
            if (past == null) throw new InvalidDataException("캐시를 읽는 갈래가 없어요.");
            var pastBranch = branches.First(kv => kv.Key == past).Value;
            var firstBranch = branches.First(kv => kv.Key != past).Value;
            using (var f = File.Create(firstPath))
                BuildBranch(b, modelFields, main, node, firstBranch, o => true, "decoder_first", f);
            using (var f = File.Create(nextPath))
                BuildBranch(b, modelFields, main, node, pastBranch, o => o.IndexOf(".encoder.", StringComparison.Ordinal) < 0, "decoder_next", f);
        }

        /// 파일의 형식 번호(IR). 맨 앞 칸들만 읽는다.
        public static long IrVersion(string path)
        {
            long ir;
            int pos, len;
            FindIr(path, out ir, out pos, out len);
            return ir;
        }

        static void FindIr(string path, out long ir, out int pos, out int len)
        {
            ir = 0;
            pos = len = -1;
            using (var s = File.OpenRead(path))
            {
                while (s.Position < s.Length)
                {
                    long key = ReadVarint(s);
                    int num = (int)(key >> 3), wt = (int)(key & 7);
                    if (wt == 0)
                    {
                        long at = s.Position;
                        long v = ReadVarint(s);
                        if (num == 1)
                        {
                            ir = v;
                            pos = (int)at;
                            len = (int)(s.Position - at);
                            return;
                        }
                    }
                    else if (wt == 2) s.Seek(ReadVarint(s), SeekOrigin.Current);
                    else if (wt == 1) s.Seek(8, SeekOrigin.Current);
                    else if (wt == 5) s.Seek(4, SeekOrigin.Current);
                    else throw new InvalidDataException("ONNX 파일 모양이 예상과 달라요.");
                }
            }
        }

        static long ReadVarint(Stream s)
        {
            int shift = 0;
            long result = 0;
            while (true)
            {
                int x = s.ReadByte();
                if (x < 0) throw new InvalidDataException("ONNX 파일이 잘렸어요.");
                result |= (long)(x & 0x7F) << shift;
                if (x < 0x80) return result;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("ONNX 파일 모양이 예상과 달라요.");
            }
        }

        /// 형식 번호가 max보다 높으면 그 자리에서 max로 낮춘다(같은 길이로 적을 수 있을 때만). 바꿨으면 true.
        public static bool LowerIr(string path, int max)
        {
            long ir;
            int pos, len;
            FindIr(path, out ir, out pos, out len);
            if (pos < 0) throw new InvalidDataException("ONNX 파일에 형식 번호가 없어요.");
            if (ir <= max) return false;
            var nb = VarintBytes(max);
            if (nb.Length != len) throw new InvalidDataException("형식 번호를 같은 길이로 바꿀 수 없어요.");
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                s.Position = pos;
                s.Write(nb, 0, nb.Length);
            }
            return true;
        }
    }
}
