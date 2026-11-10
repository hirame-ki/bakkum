// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 탐색기에서 파일을 끄는 동안 정해 둔 키(Shift·Ctrl·Q)를 누르면 커서 자리에 메뉴를 띄운다.
// 전역 저수준 훅(WH_MOUSE_LL·WH_KEYBOARD_LL)으로 마우스 왼쪽 단추가 눌린 채 움직였는지와 키가 새로 눌렸는지만 본다.
// 누른 키나 마우스 위치를 어디에도 저장하거나 보내지 않는다.
//
// 훅은 화면(UI) 스레드가 아니라 이 일만 하는 스레드에 건다. 저수준 훅은 처리가 잠깐만 늦어도 윈도우가 알리지 않고
// 떼어 버리는데, 화면 스레드는 창을 그리느라 바쁠 때가 있다. (2026-11-08 사용자 시험: 설치 뒤 Shift를 눌러도 메뉴가 안 떴다.)
// 그래도 떨어질 때를 대비해 2초마다 훅이 살아 있는지 보고, 떨어졌으면 다시 건다.
// 끄는 동안에는 키 상태도 짧은 간격으로 직접 확인해서, 키보드 훅이 키를 못 받는 PC에서도 메뉴가 뜨게 한다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Bakkum
{
    public sealed class DragTrigger : IDisposable
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint ms, IntPtr proc);

        [DllImport("user32.dll")]
        static extern bool KillTimer(IntPtr hWnd, IntPtr id);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        const uint WM_TIMER = 0x0113, WM_QUIT = 0x0012;
        const int SM_SWAPBUTTON = 23;

        HookProc keyboardProc, mouseProc;
        IntPtr keyboardHook, mouseHook;
        Thread thread;
        uint threadId;
        IntPtr watchTimer, pollTimer;
        int lastHookTick = Environment.TickCount;

        // 아래 끌기 상태는 훅 스레드에서만 다룬다.
        bool buttonDown, moved, fired, pollWasDown;
        POINT downAt;
        IntPtr downWindow;
        readonly HashSet<uint> held = new HashSet<uint>();

        // 설정 창(화면 스레드)에서 바꾸는 값
        volatile uint[] keys = new uint[0];
        volatile bool swallow;
        volatile bool resetHeld;

        // 기록은 문제를 찾는 데 필요한 만큼만, 자주 남기지 않는다(키·파일 이름은 적지 않음).
        bool firstFireLogged, rehookLogged, sourceLogged, hookFailLogged;
        int lastRehookLog, lastSourceLog, lastHookFailLog;

        public void Start()
        {
            Reload();
            keyboardProc = KeyboardProc;
            mouseProc = MouseProc;
            var ready = new ManualResetEvent(false);
            thread = new Thread(delegate () { Run(ready); });
            thread.IsBackground = true;
            thread.Name = "drag-trigger";
            thread.Start();
            ready.WaitOne(3000);
        }

        public void Reload()
        {
            uint[] k;
            bool sw;
            switch (AppSettings.Current.TriggerKey)
            {
                case "Shift":
                    k = new uint[] { 0xA0, 0xA1, 0x10 };
                    sw = false;
                    break;
                case "Ctrl":
                    k = new uint[] { 0xA2, 0xA3, 0x11 };
                    sw = false;
                    break;
                case "Q":
                    k = new uint[] { 0x51 };
                    sw = true;
                    break;
                default:
                    k = new uint[0];
                    sw = false;
                    break;
            }
            keys = k;
            swallow = sw;
            resetHeld = true;
        }

        void Run(ManualResetEvent ready)
        {
            // 이 스레드에서 난 오류가 프로그램 전체를 끄지 않게 모두 여기서 받는다.
            try
            {
                try
                {
                    threadId = GetCurrentThreadId();
                    Hook();
                    watchTimer = SetTimer(IntPtr.Zero, IntPtr.Zero, 2000, IntPtr.Zero);
                }
                finally
                {
                    ready.Set();
                }
                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message != WM_TIMER) continue;
                    try
                    {
                        if (msg.wParam == watchTimer) Watch();
                        else if (msg.wParam == pollTimer) Poll();
                    }
                    catch (Exception ex)
                    {
                        Log.Write("끌기 감지 오류: " + ex.GetType().Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("끌기 감지 스레드 오류: " + ex.GetType().Name);
            }
            finally
            {
                StopPoll();
                if (watchTimer != IntPtr.Zero) KillTimer(IntPtr.Zero, watchTimer);
                watchTimer = IntPtr.Zero;
                Unhook();
            }
        }

        void Hook()
        {
            IntPtr mod = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, mod, 0);
            bool mouseOk = mouseHook != IntPtr.Zero;
            int mouseErr = mouseOk ? 0 : Marshal.GetLastWin32Error();
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, mod, 0);
            bool keyOk = keyboardHook != IntPtr.Zero;
            int keyErr = keyOk ? 0 : Marshal.GetLastWin32Error();
            lastHookTick = Environment.TickCount;
            // 훅을 못 걸면 2초마다 다시 걸어 보므로, 기록은 1분에 한 번만 남긴다.
            if ((!mouseOk || !keyOk) && (!hookFailLogged || unchecked(Environment.TickCount - lastHookFailLog) > 60000))
            {
                hookFailLogged = true;
                lastHookFailLog = Environment.TickCount;
                Log.Write("끌기 감지 훅 실패: 마우스 " + (mouseOk ? "됨" : mouseErr.ToString()) + " · 키보드 " + (keyOk ? "됨" : keyErr.ToString()));
            }
        }

        void Unhook()
        {
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            keyboardHook = IntPtr.Zero;
            mouseHook = IntPtr.Zero;
        }

        /// 마우스나 키보드를 썼는데 훅이 그동안 아무것도 못 받았으면, 윈도우가 훅을 뗀 것으로 보고 다시 건다.
        void Watch()
        {
            var li = new LASTINPUTINFO();
            li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref li)) return;
            int lastInput = unchecked((int)li.dwTime);
            if (unchecked(lastInput - lastHookTick) < 1500) return;
            Unhook();
            Hook();
            if (!rehookLogged || unchecked(Environment.TickCount - lastRehookLog) > 60000)
            {
                Log.Write("끌기 감지: 훅이 떨어져서 다시 걸었어요");
                rehookLogged = true;
                lastRehookLog = Environment.TickCount;
            }
        }

        IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
        {
            lastHookTick = Environment.TickCount;
            if (code >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP || (msg == WM_MOUSEMOVE && buttonDown && !moved))
                {
                    var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if (msg == WM_LBUTTONDOWN)
                    {
                        buttonDown = true;
                        moved = false;
                        fired = false;
                        downAt = info.pt;
                        downWindow = WindowFromPoint(info.pt);
                        ResyncHeld();
                    }
                    else if (msg == WM_LBUTTONUP)
                    {
                        buttonDown = false;
                        StopPoll();
                    }
                    else if (Math.Abs(info.pt.X - downAt.X) > 6 || Math.Abs(info.pt.Y - downAt.Y) > 6)
                    {
                        moved = true;
                        StartPoll();
                    }
                }
            }
            return CallNextHookEx(mouseHook, code, wParam, lParam);
        }

        IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
        {
            lastHookTick = Environment.TickCount;
            uint[] k = keys;
            if (code >= 0 && k.Length > 0)
            {
                if (resetHeld)
                {
                    held.Clear();
                    resetHeld = false;
                }
                int msg = wParam.ToInt32();
                var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if (Array.IndexOf(k, info.vkCode) >= 0)
                {
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        bool fresh = held.Add(info.vkCode);
                        if (fresh && buttonDown && moved && !fired && FromAllowedSource())
                        {
                            fired = true;
                            pollWasDown = true;
                            Fire("키보드 훅");
                            if (swallow) return (IntPtr)1;
                        }
                    }
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        held.Remove(info.vkCode);
                    }
                }
            }
            return CallNextHookEx(keyboardHook, code, wParam, lParam);
        }

        /// 실제로 눌려 있지 않은 키는 '누르고 있음'에서 뺀다. 키를 뗀 것을 놓쳐서 다음 누름을 새 누름으로 못 알아보는 일을 막는다.
        void ResyncHeld()
        {
            if (resetHeld)
            {
                held.Clear();
                resetHeld = false;
            }
            held.RemoveWhere(vk => (GetAsyncKeyState((int)vk) & 0x8000) == 0);
        }

        void StartPoll()
        {
            if (pollTimer != IntPtr.Zero) return;
            pollWasDown = AnyKeyDown();
            pollTimer = SetTimer(IntPtr.Zero, IntPtr.Zero, 40, IntPtr.Zero);
        }

        void StopPoll()
        {
            if (pollTimer == IntPtr.Zero) return;
            KillTimer(IntPtr.Zero, pollTimer);
            pollTimer = IntPtr.Zero;
        }

        /// 끄는 동안 40ms마다 키 상태를 직접 본다. 키보드 훅이 키를 못 받아도 여기서 메뉴를 띄운다.
        void Poll()
        {
            int primary = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? 0x02 : 0x01;
            if (!buttonDown || (GetAsyncKeyState(primary) & 0x8000) == 0)
            {
                // 단추를 뗀 것을 훅이 놓친 경우도 여기서 정리한다.
                buttonDown = false;
                StopPoll();
                return;
            }
            bool down = AnyKeyDown();
            if (down && !pollWasDown && moved && !fired && FromAllowedSource())
            {
                fired = true;
                Fire("키 확인");
            }
            pollWasDown = down;
        }

        bool AnyKeyDown()
        {
            foreach (uint vk in keys)
            {
                if ((GetAsyncKeyState((int)vk) & 0x8000) != 0) return true;
            }
            return false;
        }

        bool FromAllowedSource()
        {
            if (AppSettings.Current.AnySource) return true;
            string name = null;
            try
            {
                uint pid;
                GetWindowThreadProcessId(downWindow, out pid);
                if (pid != 0)
                {
                    using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName;
                }
            }
            catch { }
            if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase)) return true;
            if (!sourceLogged || unchecked(Environment.TickCount - lastSourceLog) > 60000)
            {
                Log.Write("끌기 메뉴: 탐색기가 아닌 곳(" + (name ?? "?") + ")에서 끌어서 띄우지 않음");
                sourceLogged = true;
                lastSourceLog = Environment.TickCount;
            }
            return false;
        }

        void Fire(string how)
        {
            if (!firstFireLogged)
            {
                firstFireLogged = true;
                Log.Write("끌기 메뉴 띄움(" + how + ")");
            }
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    MenuWindow.ShowAt(MenuWindow.CursorPx(), true, new List<string>());
                }
                catch (Exception ex)
                {
                    Log.Write("메뉴 띄우기 실패: " + ex.GetType().Name);
                }
            }));
        }

        public void Dispose()
        {
            var t = thread;
            if (t == null) return;
            thread = null;
            if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            t.Join(1000);
        }
    }
}
