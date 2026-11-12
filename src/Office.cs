// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 한컴오피스(한글·한셀·한쇼)와 MS 오피스를 자동화로 불러 PDF·HWPX 등으로 저장한다.
// 우리가 띄운 프로세스만 정리하고, 이미 떠 있던 프로그램(선생님이 쓰는 중)은 끄지 않는다.
// 프로그램이 띄운 안내 창 가운데 시험에서 내용을 확인한 '호환 문서'(배치가 바뀔 수 있다는 안내)에만 Enter를 대신 누른다.
// 보안 승인 같은 다른 창은 절대 누르지 않고, 선생님이 고를 때까지 기다린다.
// (한글이 PDF를 열 때 뜨는 '글' 안내 창은 PDF를 여는 그 호출 동안에만 대신 누른다. 제목이 흔해서 항상 누르지 않는다.)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Bakkum
{
    public sealed class OfficeApp : IDisposable
    {
        public dynamic Obj;
        public int Pid;
        public bool Shared;
        public volatile bool TimedOut;
        /// 잠깐 동안만 대신 눌러도 되는 안내 창 제목(PDF 열기 안내)
        public volatile string ExtraKnown;
        /// 잠깐 동안 그냥 두는 진행 창 제목: 누르지도, 고르라고 알리지도 않는다
        public volatile string ExtraQuiet;
        readonly string processName;
        readonly string friendly;
        readonly Job job;
        readonly string[] known;
        readonly Thread guard;
        volatile bool stopping;
        long deadlineTicks = long.MaxValue;
        readonly HashSet<IntPtr> reported = new HashSet<IntPtr>();

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        OfficeApp(string processName, string friendly, Job job, string[] known)
        {
            this.processName = processName;
            this.friendly = friendly;
            this.job = job;
            this.known = known ?? new string[0];
            guard = new Thread(Guard) { IsBackground = true, Name = "office-guard" };
        }

        public static bool Available(string progId)
        {
            try { return Type.GetTypeFromProgID(progId) != null; }
            catch { return false; }
        }

        public static OfficeApp Start(string progId, string processName, string friendly, Job job, string[] known)
        {
            Type t = Type.GetTypeFromProgID(progId);
            if (t == null) return null;
            var before = Util.PidsOf(processName);
            if (job != null) job.Report(friendly + " 여는 중…", -1);
            object o;
            try
            {
                o = Activator.CreateInstance(t);
            }
            catch (Exception ex)
            {
                Log.Write(friendly + " 시작 실패: " + ex.GetType().Name);
                throw new UserFacingException(friendly + " 프로그램을 시작하지 못했어요.");
            }
            var app = new OfficeApp(processName, friendly, job, known);
            app.Obj = o;
            for (int i = 0; i < 30 && app.Pid == 0; i++)
            {
                var now = Util.PidsOf(processName);
                now.ExceptWith(before);
                if (now.Count == 1) app.Pid = now.First();
                else if (now.Count > 1) break;
                else Thread.Sleep(100);
            }
            app.Shared = app.Pid == 0 && before.Count > 0;
            app.guard.Start();
            return app;
        }

        public void Arm(TimeSpan limit)
        {
            Interlocked.Exchange(ref deadlineTicks, DateTime.UtcNow.Add(limit).Ticks);
        }

        public void Disarm()
        {
            Interlocked.Exchange(ref deadlineTicks, long.MaxValue);
        }

        /// 자동화 호출이 실패했을 때 선생님께 보일 말로 바꾼다.
        public Exception Failure(Exception ex, string what)
        {
            if (job != null && job.Cancelled) return new OperationCanceledException();
            if (TimedOut) return new UserFacingException(friendly + " 작업이 너무 오래 걸려서 멈췄어요.");
            Log.Write(friendly + " " + what + " 실패: " + ex.GetType().Name + " " + (ex is COMException ? ((COMException)ex).ErrorCode.ToString("X8") : ""));
            return new UserFacingException(friendly + "에서 " + what + " 못했어요.");
        }

        void Guard()
        {
            while (!stopping)
            {
                Thread.Sleep(300);
                if (stopping) break;
                bool cancel = job != null && job.Cancelled;
                bool late = DateTime.UtcNow.Ticks > Interlocked.Read(ref deadlineTicks);
                if ((cancel || late) && Pid != 0 && !Shared)
                {
                    if (late) TimedOut = true;
                    Log.Write(friendly + (late ? " 시간 초과로" : " 멈춤 요청으로") + " 끔");
                    Util.KillTree(Pid);
                    Pid = 0;
                    continue;
                }
                if (Pid != 0) WatchDialogs();
            }
        }

        void WatchDialogs()
        {
            var found = new List<IntPtr>();
            uint target = (uint)Pid;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == target && IsWindowVisible(h)) found.Add(h);
                return true;
            }, IntPtr.Zero);
            foreach (var h in found)
            {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                string title = sb.ToString();
                RECT r;
                GetWindowRect(h, out r);
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                bool small = w < 900 && ht < 600;
                if (!small) continue;
                if (title == ExtraQuiet) continue;
                if (known.Contains(title) || title == ExtraKnown)
                {
                    PostMessage(h, 0x0100, (IntPtr)0x0D, IntPtr.Zero);
                    PostMessage(h, 0x0101, (IntPtr)0x0D, IntPtr.Zero);
                    Log.Write(friendly + " 안내 창에 Enter: " + title);
                    continue;
                }
                // 모르는 창(보안 승인 등): 누르지 않고 선생님께 알린 뒤, 고르는 동안은 시간을 재지 않는다.
                Arm(TimeSpan.FromMinutes(10));
                if (reported.Add(h))
                {
                    try { SetForegroundWindow(h); } catch { }
                    if (job != null) job.Report(friendly + " 프로그램이 확인 창을 띄웠어요. 그 창에서 직접 골라 주세요.", -1);
                    Log.Write(friendly + " 확인 창 기다림");
                }
            }
        }

        public void Dispose()
        {
            stopping = true;
            try { if (Obj != null) Marshal.FinalReleaseComObject(Obj); } catch { }
            Obj = null;
            if (Pid != 0 && !Shared)
            {
                try
                {
                    using (var p = System.Diagnostics.Process.GetProcessById(Pid))
                    {
                        if (!p.WaitForExit(8000))
                        {
                            Log.Write(friendly + " 끝나지 않아 끔");
                            Util.KillTree(Pid);
                        }
                    }
                }
                catch (ArgumentException) { }
                catch (Exception ex) { Log.Write(friendly + " 정리: " + ex.GetType().Name); }
            }
        }
    }

    /// 한글(HWPFrame.HwpObject). 한글·워드 문서를 열어 PDF·HWPX·HWP·글자로 저장한다.
    public sealed class Hangul : IDisposable
    {
        static readonly string[] Known = { "호환 문서" };
        readonly OfficeApp app;

        public static bool Installed
        {
            get { return OfficeApp.Available("HWPFrame.HwpObject"); }
        }

        public Hangul(Job job)
        {
            app = OfficeApp.Start("HWPFrame.HwpObject", "Hwp", "한글", job, Known);
            if (app == null) throw new UserFacingException("이 PC에 한글(한컴오피스)이 없어요.");
            dynamic h = app.Obj;
            try { h.RegisterModule("FilePathCheckDLL", "FilePathCheckerModule"); } catch { }
            try { h.XHwpWindows.Item(0).Visible = false; } catch { }
        }

        public void Open(string path)
        {
            Util.EnsureReadable(path);
            string ext = Formats.Ext(path);
            string fmt = ext == ".hwpx" ? "HWPX" : ext == ".hwp" ? "HWP" : "";
            bool ok;
            app.Arm(TimeSpan.FromMinutes(3));
            try { ok = app.Obj.Open(path, fmt, "forceopen:true;versionwarning:false;suspendpassword:true"); }
            catch (Exception ex) { throw app.Failure(ex, "열지"); }
            finally { app.Disarm(); }
            if (!ok) throw new UserFacingException("한글에서 열지 못했어요. 암호가 걸렸거나 손상된 파일일 수 있어요.");
        }

        /// PDF를 한글로 읽어 들인다(글·표를 되살려 연다). 한글이 띄우는 안내 창은 이 호출 동안에만 대신 누른다.
        public void OpenPdf(string path)
        {
            Util.EnsureReadable(path);
            bool ok;
            app.ExtraKnown = "\uF53A글";
            app.ExtraQuiet = "PDF를 오피스 문서로 변환하기";
            app.Arm(TimeSpan.FromMinutes(8));
            try { ok = app.Obj.Open(path, "", ""); }
            catch (Exception ex) { throw app.Failure(ex, "PDF를 열지"); }
            finally
            {
                app.Disarm();
                app.ExtraKnown = null;
                app.ExtraQuiet = null;
            }
            if (!ok) throw new UserFacingException("한글에서 이 PDF를 열지 못했어요. 암호가 걸렸거나 손상된 파일일 수 있어요.");
        }

        public void SaveAs(string path, string format)
        {
            bool ok;
            app.Arm(TimeSpan.FromMinutes(5));
            try { ok = app.Obj.SaveAs(path, format, ""); }
            catch (Exception ex) { throw app.Failure(ex, "저장하지"); }
            finally { app.Disarm(); }
            if (!ok || !File.Exists(path)) throw new UserFacingException("한글에서 저장하지 못했어요.");
        }

        public string Text()
        {
            app.Arm(TimeSpan.FromMinutes(3));
            try { return (string)app.Obj.GetTextFile("UNICODE", ""); }
            catch (Exception ex) { throw app.Failure(ex, "글자를 읽지"); }
            finally { app.Disarm(); }
        }

        public void CloseDoc()
        {
            try { app.Obj.Clear(1); } catch { }
        }

        public void Convert(string src, string dst, string format)
        {
            Open(src);
            try { SaveAs(dst, format); }
            finally { CloseDoc(); }
        }

        /// HTML로 열면 쪽 나눔과 줄 앞 빈칸이 사라진다(CSS도 안 먹는다, 2026-11-09 실측).
        /// 표시 글자를 적은 HTML을 열어 그 자리에서 쪽을 나누고 빈칸을 넣는다.
        /// 표시 글자가 하나라도 남으면 그 문서는 버리고 표시 없는 HTML로 다시 저장한다.
        public void ConvertMarked(string marked, string plain, string dst, string format, string pageMark, string indentMark, string indent)
        {
            bool clean = false;
            Open(marked);
            try
            {
                clean = ApplyMarks(pageMark, indentMark, indent);
                if (clean) SaveAs(dst, format);
            }
            finally { CloseDoc(); }
            if (!clean) Convert(plain, dst, format);
        }

        bool ApplyMarks(string pageMark, string indentMark, string indent)
        {
            app.Arm(TimeSpan.FromMinutes(2));
            try
            {
                dynamic h = app.Obj;
                h.MovePos(2, 0, 0);
                for (int n = 0; n < 200; n++)
                {
                    dynamic find = h.CreateAction("RepeatFind");
                    dynamic set = find.CreateSet();
                    find.GetDefault(set);
                    set.SetItem("FindString", pageMark);
                    set.SetItem("Direction", 0);
                    set.SetItem("IgnoreMessage", 1);
                    if (!(bool)find.Execute(set)) break;
                    h.HAction.Run("Delete");
                    h.HAction.Run("BreakPage");
                }
                h.MovePos(2, 0, 0);
                dynamic all = h.CreateAction("AllReplace");
                dynamic replace = all.CreateSet();
                all.GetDefault(replace);
                replace.SetItem("FindString", indentMark);
                replace.SetItem("ReplaceString", indent);
                replace.SetItem("Direction", 0);
                replace.SetItem("IgnoreMessage", 1);
                all.Execute(replace);
                string text = (string)h.GetTextFile("UNICODE", "");
                return !text.Contains(pageMark) && !text.Contains(indentMark);
            }
            catch (Exception ex)
            {
                Log.Write("쪽 나누기 실패: " + ex.GetType().Name);
                return false;
            }
            finally { app.Disarm(); }
        }

        public void Dispose()
        {
            try { app.Obj.Clear(1); } catch { }
            try { app.Obj.Quit(); } catch { }
            app.Dispose();
        }
    }

    public sealed class Hancell : IDisposable
    {
        readonly OfficeApp app;

        public static bool Installed
        {
            get { return OfficeApp.Available("HCell.Application"); }
        }

        public Hancell(Job job)
        {
            app = OfficeApp.Start("HCell.Application", "HCell", "한셀", job, null);
            if (app == null) throw new UserFacingException("이 PC에 한셀이 없어요.");
            try { app.Obj.DisplayAlerts = false; } catch { }
        }

        public void ToPdf(string src, string dst)
        {
            dynamic wb = null;
            app.Arm(TimeSpan.FromMinutes(5));
            try
            {
                wb = app.Obj.Workbooks.Open(src);
                wb.ExportAsFixedFormat(0, dst);
            }
            catch (Exception ex) { throw app.Failure(ex, "PDF로 저장하지"); }
            finally
            {
                app.Disarm();
                if (wb != null) { try { wb.Close(false); } catch { } }
            }
            if (!File.Exists(dst)) throw new UserFacingException("한셀에서 PDF를 만들지 못했어요.");
        }

        /// 다른 형식으로 저장한다. 형식 번호는 엑셀과 같다(51 xlsx, 56 xls). 2026-11-13 한컴오피스 2024(13.0)에서 51로 xlsx가 나오는 것을 확인.
        /// format이 0 이하면 형식을 정하지 않는다(이름이 .cell이면 한셀 문서로 저장, 시험 파일 만들 때).
        public void SaveAs(string src, string dst, int format)
        {
            dynamic wb = null;
            app.Arm(TimeSpan.FromMinutes(5));
            try
            {
                wb = app.Obj.Workbooks.Open(src);
                if (format > 0) wb.SaveAs(dst, format);
                else wb.SaveAs(dst);
            }
            catch (Exception ex) { throw app.Failure(ex, "다른 형식으로 저장하지"); }
            finally
            {
                app.Disarm();
                if (wb != null) { try { wb.Close(false); } catch { } }
            }
            if (!File.Exists(dst) || new FileInfo(dst).Length == 0) throw new UserFacingException("한셀에서 파일을 만들지 못했어요.");
        }

        public void Dispose()
        {
            try { app.Obj.Quit(); } catch { }
            app.Dispose();
        }
    }

    public sealed class Hanshow : IDisposable
    {
        readonly OfficeApp app;

        public static bool Installed
        {
            get { return OfficeApp.Available("HShow.Application"); }
        }

        public Hanshow(Job job)
        {
            app = OfficeApp.Start("HShow.Application", "HShow", "한쇼", job, null);
            if (app == null) throw new UserFacingException("이 PC에 한쇼가 없어요.");
        }

        public void ToPdf(string src, string dst)
        {
            SaveAs(src, dst, 32, "PDF로 저장하지");
            if (!File.Exists(dst)) throw new UserFacingException("한쇼에서 PDF를 만들지 못했어요.");
        }

        /// 다른 형식으로 저장한다. 형식 번호는 파워포인트와 같다(24 pptx, 1 ppt, 32 pdf).
        /// 한쇼는 이름 끝에 형식의 확장자를 덧붙일 때가 있다('가.show'를 기본 형식으로 → '가.show.pptx', 2026-11-13 실측).
        public void SaveAs(string src, string dst, int format, string what)
        {
            dynamic pres = null;
            app.Arm(TimeSpan.FromMinutes(5));
            try
            {
                pres = app.Obj.Presentations.Open(src, true, false, false);
                // 한쇼는 옛 .ppt의 슬라이드 내용을 나중에 불러온다. 열자마자 PPTX로 저장하면 슬라이드가 텅 빈다(도형 0개).
                // 슬라이드마다 도형 수를 한 번 읽으면 다 불러온다(2026-11-13 한컴오피스 2024 실측. PDF 저장은 그대로도 괜찮았다).
                int count = (int)pres.Slides.Count;
                for (int i = 1; i <= count; i++) { var n = pres.Slides.Item(i).Shapes.Count; }
                pres.SaveAs(dst, format);
            }
            catch (Exception ex) { throw app.Failure(ex, what); }
            finally
            {
                app.Disarm();
                if (pres != null) { try { pres.Close(); } catch { } }
            }
            string added = dst + (format == 1 ? ".ppt" : format == 24 ? ".pptx" : ".pdf");
            if (!File.Exists(dst) && File.Exists(added)) File.Move(added, dst);
        }

        public void Dispose()
        {
            try { app.Obj.Quit(); } catch { }
            app.Dispose();
        }
    }

    // ── MS 오피스(이 PC에는 없어 시험하지 못함. 공개된 자동화 방법 그대로) ──

    public sealed class MsWord : IDisposable
    {
        readonly OfficeApp app;

        public static bool Installed
        {
            get { return OfficeApp.Available("Word.Application"); }
        }

        public MsWord(Job job)
        {
            app = OfficeApp.Start("Word.Application", "WINWORD", "MS 워드", job, null);
            if (app == null) throw new UserFacingException("이 PC에 MS 워드가 없어요.");
            try { if (!app.Shared) app.Obj.Visible = false; } catch { }
            try { app.Obj.DisplayAlerts = 0; } catch { }
        }

        public void ToPdf(string src, string dst)
        {
            dynamic doc = null;
            app.Arm(TimeSpan.FromMinutes(5));
            try
            {
                // 암호가 걸린 문서는 묻는 창 대신 오류가 나도록 아무 암호나 넘긴다(암호 없는 문서는 무시됨).
                doc = app.Obj.Documents.Open(src, false, true, false, "hirameki-no-password");
                doc.ExportAsFixedFormat(dst, 17);
            }
            catch (Exception ex) { throw app.Failure(ex, "PDF로 저장하지"); }
            finally
            {
                app.Disarm();
                if (doc != null) { try { doc.Close(0); } catch { } }
            }
            if (!File.Exists(dst)) throw new UserFacingException("MS 워드에서 PDF를 만들지 못했어요.");
        }

        /// PDF를 워드로 열어 docx로 저장한다.
        public void PdfToDocx(string src, string dst)
        {
            dynamic doc = null;
            app.Arm(TimeSpan.FromMinutes(10));
            try
            {
                doc = app.Obj.Documents.Open(src, false, true, false);
                doc.SaveAs2(dst, 16);
            }
            catch (Exception ex) { throw app.Failure(ex, "워드로 저장하지"); }
            finally
            {
                app.Disarm();
                if (doc != null) { try { doc.Close(0); } catch { } }
            }
            if (!File.Exists(dst)) throw new UserFacingException("MS 워드에서 파일을 만들지 못했어요.");
        }

        public void Dispose()
        {
            if (!app.Shared) { try { app.Obj.Quit(0); } catch { } }
            app.Dispose();
        }
    }

    public sealed class MsExcel : IDisposable
    {
        readonly OfficeApp app;

        public static bool Installed
        {
            get { return OfficeApp.Available("Excel.Application"); }
        }

        public MsExcel(Job job)
        {
            app = OfficeApp.Start("Excel.Application", "EXCEL", "MS 엑셀", job, null);
            if (app == null) throw new UserFacingException("이 PC에 MS 엑셀이 없어요.");
            try { if (!app.Shared) app.Obj.Visible = false; } catch { }
            try { app.Obj.DisplayAlerts = false; } catch { }
        }

        public void ToPdf(string src, string dst)
        {
            dynamic wb = null;
            app.Arm(TimeSpan.FromMinutes(5));
            try
            {
                wb = app.Obj.Workbooks.Open(src, 0, true, Type.Missing, "hirameki-no-password");
                wb.ExportAsFixedFormat(0, dst);
            }
            catch (Exception ex) { throw app.Failure(ex, "PDF로 저장하지"); }
            finally
            {
                app.Disarm();
                if (wb != null) { try { wb.Close(false); } catch { } }
            }
            if (!File.Exists(dst)) throw new UserFacingException("MS 엑셀에서 PDF를 만들지 못했어요.");
        }

        /// 다른 형식으로 저장한다(51 xlsx, 56 xls). 2026-11-13 협업자 PC의 MS 엑셀에서 .xls → xlsx 확인.
        public void SaveAs(string src, string dst, int format)
        {
            dynamic wb = null;
            app.Arm(TimeSpan.FromMinutes(5));
            try
            {
                wb = app.Obj.Workbooks.Open(src, 0, true, Type.Missing, "hirameki-no-password");
                wb.SaveAs(dst, format);
            }
            catch (Exception ex) { throw app.Failure(ex, "다른 형식으로 저장하지"); }
            finally
            {
                app.Disarm();
                if (wb != null) { try { wb.Close(false); } catch { } }
            }
            if (!File.Exists(dst) || new FileInfo(dst).Length == 0) throw new UserFacingException("MS 엑셀에서 파일을 만들지 못했어요.");
        }

        public void Dispose()
        {
            if (!app.Shared) { try { app.Obj.Quit(); } catch { } }
            app.Dispose();
        }
    }

    public sealed class MsPowerPoint : IDisposable
    {
        readonly OfficeApp app;

        public static bool Installed
        {
            get { return OfficeApp.Available("PowerPoint.Application"); }
        }

        public MsPowerPoint(Job job)
        {
            app = OfficeApp.Start("PowerPoint.Application", "POWERPNT", "MS 파워포인트", job, null);
            if (app == null) throw new UserFacingException("이 PC에 MS 파워포인트가 없어요.");
            try { app.Obj.DisplayAlerts = 1; } catch { }
        }

        public void ToPdf(string src, string dst)
        {
            SaveAs(src, dst, 32, "PDF로 저장하지");
            if (!File.Exists(dst)) throw new UserFacingException("MS 파워포인트에서 PDF를 만들지 못했어요.");
        }

        /// 다른 형식으로 저장한다(24 pptx, 1 ppt, 32 pdf). 2026-11-13 협업자 PC의 파워포인트에서 .ppt → pptx 확인.
        public void SaveAs(string src, string dst, int format, string what)
        {
            dynamic pres = null;
            app.Arm(TimeSpan.FromMinutes(5));
            try
            {
                pres = app.Obj.Presentations.Open(src, -1, 0, 0);
                pres.SaveAs(dst, format);
            }
            catch (Exception ex) { throw app.Failure(ex, what); }
            finally
            {
                app.Disarm();
                if (pres != null) { try { pres.Close(); } catch { } }
            }
        }

        public void Dispose()
        {
            if (!app.Shared)
            {
                try
                {
                    if ((int)app.Obj.Presentations.Count == 0) app.Obj.Quit();
                }
                catch { }
            }
            app.Dispose();
        }
    }

    /// 파일 종류에 맞는 프로그램을 골라 PDF로 저장한다. 고른 프로그램 이름을 돌려준다.
    public static class OfficeConvert
    {
        /// 옛 엑셀(.xls)·한셀(.cell) → xlsx. .cell은 한셀만 열고, .xls는 MS 엑셀이 있으면 엑셀, 없으면 한셀로 연다.
        public static bool CanMakeXlsx(string src)
        {
            return Formats.Ext(src) == ".cell" ? Hancell.Installed : MsExcel.Installed || Hancell.Installed;
        }

        public static string ToXlsx(Job job, string src, string dst)
        {
            Util.EnsureReadable(src);
            bool cell = Formats.Ext(src) == ".cell";
            if (!cell && MsExcel.Installed)
            {
                job.Use(() => new MsExcel(job)).SaveAs(src, dst, 51);
                return "MS 엑셀";
            }
            if (Hancell.Installed)
            {
                job.Use(() => new Hancell(job)).SaveAs(src, dst, 51);
                return "한셀";
            }
            throw new UserFacingException(cell ? "한셀 파일을 바꾸려면 한셀이 있어야 해요." : "옛 엑셀 파일(.xls)을 바꾸려면 MS 엑셀이나 한셀이 있어야 해요.");
        }

        /// 옛 PPT(.ppt)·한쇼(.show) → pptx. .show는 한쇼만 열고, .ppt는 MS 파워포인트가 있으면 파워포인트, 없으면 한쇼로 연다.
        public static string ToPptx(Job job, string src, string dst)
        {
            Util.EnsureReadable(src);
            bool show = Formats.Ext(src) == ".show";
            if (!show && MsPowerPoint.Installed)
            {
                job.Use(() => new MsPowerPoint(job)).SaveAs(src, dst, 24, "PPTX로 저장하지");
                if (!File.Exists(dst)) throw new UserFacingException("MS 파워포인트에서 PPTX를 만들지 못했어요.");
                return "MS 파워포인트";
            }
            if (Hanshow.Installed)
            {
                job.Use(() => new Hanshow(job)).SaveAs(src, dst, 24, "PPTX로 저장하지");
                if (!File.Exists(dst)) throw new UserFacingException("한쇼에서 PPTX를 만들지 못했어요.");
                return "한쇼";
            }
            throw new UserFacingException(show ? "한쇼 파일을 바꾸려면 한쇼가 있어야 해요." : "옛 PPT 파일(.ppt)을 바꾸려면 MS 파워포인트나 한쇼가 있어야 해요.");
        }

        public static string ToPdf(Job job, string src, string dst)
        {
            Util.EnsureReadable(src);
            string ext = Formats.Ext(src);
            switch (Formats.KindOf(src))
            {
                case Kind.Text:
                    TextPdf.Write(dst, DocConvert.BlocksOf(job, src), Path.GetFileNameWithoutExtension(src), job);
                    return "바꿈";
                case Kind.Hwp:
                    job.Use(() => new Hangul(job)).Convert(src, dst, "PDF");
                    return "한글";
                case Kind.Word:
                    if (MsWord.Installed)
                    {
                        job.Use(() => new MsWord(job)).ToPdf(src, dst);
                        return "MS 워드";
                    }
                    if (ext == ".docx" || ext == ".odt") return WithoutProgram(job, src, dst, "MS 워드");
                    // 한컴오피스 2022의 한글은 워드 파일을 한워드로 넘기고(창이 떠서 남음), 한워드는 자동 PDF 저장을 못 한다(2026-11-07 실측).
                    throw new UserFacingException("이 PC에 MS 워드가 없어 워드 파일은 바꿀 수 없어요. (한컴 한워드는 자동 변환을 지원하지 않아요.) 파일을 직접 열어 PDF로 저장해 주세요.");
                case Kind.Excel:
                    if (ext != ".cell" && MsExcel.Installed)
                    {
                        job.Use(() => new MsExcel(job)).ToPdf(src, dst);
                        return "MS 엑셀";
                    }
                    if (Hancell.Installed && ext != ".ods")
                    {
                        job.Use(() => new Hancell(job)).ToPdf(src, dst);
                        return "한셀";
                    }
                    if (DocConvert.IsTable(src)) return WithoutProgram(job, src, dst, "엑셀");
                    throw new UserFacingException("엑셀 파일을 바꾸려면 MS 엑셀이나 한셀이 있어야 해요.");
                case Kind.Ppt:
                    if (ext != ".show" && MsPowerPoint.Installed)
                    {
                        job.Use(() => new MsPowerPoint(job)).ToPdf(src, dst);
                        return "MS 파워포인트";
                    }
                    if (Hanshow.Installed)
                    {
                        job.Use(() => new Hanshow(job)).ToPdf(src, dst);
                        FixSlidePages(src, dst);
                        return "한쇼";
                    }
                    throw new UserFacingException("PPT 파일을 바꾸려면 MS 파워포인트나 한쇼가 있어야 해요.");
            }
            throw new UserFacingException("이 파일은 PDF로 바꿀 수 없어요.");
        }

        static string WithoutProgram(Job job, string src, string dst, string program)
        {
            TextPdf.Write(dst, DocConvert.BlocksOf(job, src), Path.GetFileNameWithoutExtension(src), job);
            job.Note(Util.ShortName(src) + ": 이 PC에 " + program + "가 없어 글자·표·사진만 옮겼어요. 쪽 모양은 원본과 다를 수 있어요.");
            return "바꿈";
        }

        /// 한쇼 PDF는 쪽이 슬라이드보다 작게 적혀서(내용은 맞음) pptx에 적힌 슬라이드 크기로 되돌린다. 못 하면 그대로 둔다.
        static void FixSlidePages(string src, string dst)
        {
            double w, h;
            if (!SlideSize.TryRead(src, out w, out h)) return;
            try
            {
                if (PdfPageSize.FitTo(dst, w, h)) Log.Write("한쇼 PDF 쪽 크기를 슬라이드 크기(" + Math.Round(w) + "×" + Math.Round(h) + "pt)로 바로잡음");
            }
            catch (Exception ex)
            {
                Log.Write("쪽 크기 바로잡기 실패: " + ex.GetType().Name + " " + ex.Message);
            }
        }
    }

    /// pptx 안 ppt/presentation.xml에 적힌 슬라이드 크기(pt). 오피스 XML 형식이 아니거나 못 읽으면 false.
    static class SlideSize
    {
        static readonly HashSet<string> Ooxml = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm" };

        public static bool TryRead(string path, out double width, out double height)
        {
            width = height = 0;
            if (!Ooxml.Contains(Path.GetExtension(path))) return false;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
                {
                    var entry = zip.GetEntry("ppt/presentation.xml");
                    if (entry == null) return false;
                    var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
                    using (var s = entry.Open())
                    using (var xr = System.Xml.XmlReader.Create(s, settings))
                    {
                        while (xr.Read())
                        {
                            if (xr.NodeType != System.Xml.XmlNodeType.Element || xr.LocalName != "sldSz") continue;
                            long cx, cy;
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            if (!long.TryParse(xr.GetAttribute("cx"), System.Globalization.NumberStyles.Integer, inv, out cx)) return false;
                            if (!long.TryParse(xr.GetAttribute("cy"), System.Globalization.NumberStyles.Integer, inv, out cy)) return false;
                            if (cx <= 0 || cy <= 0) return false;
                            width = cx / 12700.0;
                            height = cy / 12700.0;
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("슬라이드 크기 읽기 실패: " + ex.GetType().Name);
            }
            return false;
        }
    }
}
