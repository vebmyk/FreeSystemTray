using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray.ShowHide
{
    /// <summary>
    /// Watches the whole desktop while an auto-hiding tray is shown: mouse buttons pressed anywhere
    /// (a low-level mouse hook, for click-away), foreground changes, and Win32 popup menus opening
    /// and closing. The hooks run on a thread of their own: a low-level hook delays every click in the
    /// system until it returns, and the UI thread is also the tray thread, busy with icon messages.
    /// Each event is only time-stamped here and handed to the UI thread's dispatcher.
    /// </summary>
    internal sealed class ShellWatcher : IDisposable
    {
        private readonly Dispatcher _ui;
        private readonly Action<ShellWatcher, PixelPoint> _buttonDown;
        private readonly Action<ShellWatcher, IntPtr, TimeSpan> _foreground;
        private readonly Action<ShellWatcher, IntPtr, bool, TimeSpan> _menu;
        private readonly Action<string> _log;
        private NativeMethods.LowLevelMouseProc? _mouseProc;
        private NativeMethods.WinEventProc? _winEventProc;
        private volatile bool _stop;
        private volatile uint _threadId;

        public ShellWatcher(Dispatcher ui, bool watchClicks,
            Action<ShellWatcher, PixelPoint> buttonDown,
            Action<ShellWatcher, IntPtr, TimeSpan> foreground,
            Action<ShellWatcher, IntPtr, bool, TimeSpan> menu,
            Action<string> log)
        {
            _ui = ui;
            WatchesClicks = watchClicks;
            _buttonDown = buttonDown;
            _foreground = foreground;
            _menu = menu;
            _log = log;
            new Thread(Run) { IsBackground = true, Name = "Shell watcher" }.Start();
        }

        public bool WatchesClicks { get; }

        /// <summary>Monotonic time, comparable across threads.</summary>
        public static TimeSpan Now() => Stopwatch.GetElapsedTime(0);

        private void Run()
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE); // creates the message queue
            if (_stop) return;

            IntPtr mouseHook = IntPtr.Zero, foregroundHook = IntPtr.Zero, menuHook = IntPtr.Zero;
            try
            {
                _winEventProc = OnWinEvent;
                const uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;
                foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, _winEventProc, 0, 0, flags);
                menuHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_MENUPOPUPSTART, NativeMethods.EVENT_SYSTEM_MENUPOPUPEND,
                    IntPtr.Zero, _winEventProc, 0, 0, flags);
                if (foregroundHook == IntPtr.Zero || menuHook == IntPtr.Zero) Log("SetWinEventHook failed; menus opened from the tray may not keep it shown");
                if (WatchesClicks)
                {
                    _mouseProc = OnMouse;
                    mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
                    if (mouseHook == IntPtr.Zero) Log($"SetWindowsHookEx(WH_MOUSE_LL) failed ({Marshal.GetLastWin32Error()}); click-away is off");
                }

                while (!_stop && NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }
            finally
            {
                if (mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(mouseHook);
                if (foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(foregroundHook);
                if (menuHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(menuHook);
            }
        }

        private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            // Must return quickly: every mouse event in the system waits for it.
            if (code >= 0 && !_stop && (int)wParam is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
                    or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN)
            {
                // MSLLHOOKSTRUCT starts with the point, in per-monitor-aware (physical) screen coordinates.
                var pt = new PixelPoint(Marshal.ReadInt32(lParam, 0), Marshal.ReadInt32(lParam, 4));
                _ui.BeginInvoke(DispatcherPriority.Input, () => _buttonDown(this, pt));
            }
            return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (_stop || hwnd == IntPtr.Zero) return;
            TimeSpan at = Now();
            switch (eventType)
            {
                case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                    _ui.BeginInvoke(DispatcherPriority.Input, () => _foreground(this, hwnd, at));
                    break;
                case NativeMethods.EVENT_SYSTEM_MENUPOPUPSTART:
                case NativeMethods.EVENT_SYSTEM_MENUPOPUPEND:
                    bool started = eventType == NativeMethods.EVENT_SYSTEM_MENUPOPUPSTART;
                    _ui.BeginInvoke(DispatcherPriority.Input, () => _menu(this, hwnd, started, at));
                    break;
            }
        }

        private void Log(string message) => _ui.BeginInvoke(DispatcherPriority.Background, () => _log(message));

        /// <summary>Stops the thread and removes its hooks. Does not wait for it.</summary>
        public void Dispose()
        {
            _stop = true;
            uint id = _threadId;
            // If the thread has no id yet, it sees _stop before it enters its message loop.
            if (id != 0) NativeMethods.PostThreadMessage(id, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
