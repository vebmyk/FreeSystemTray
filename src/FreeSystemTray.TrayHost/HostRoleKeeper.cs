using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FreeSystemTray.TrayHost
{
    /// <summary>A time Explorer's taskbar was above our Shell_TrayWnd, so apps' icon messages went to Explorer only.</summary>
    /// <param name="LostAt">Stopwatch timestamp when it was first seen above ours.</param>
    /// <param name="RegainedAt">Stopwatch timestamp when ours was the highest again.</param>
    /// <param name="Raises">How often we raised our window meanwhile.</param>
    public readonly record struct RoleGap(long LostAt, long RegainedAt, int Raises)
    {
        public TimeSpan Duration => Stopwatch.GetElapsedTime(LostAt, RegainedAt);
    }

    /// <summary>
    /// Keeps our Shell_TrayWnd the highest one, so Shell_NotifyIcon keeps sending to us. Explorer puts
    /// its taskbar above ours whenever the taskbar gets the foreground (a click on it, the Start menu,
    /// closing one of its flyouts: FINDINGS.md, Phase 6), and ManagedShell only checks every 100 ms. Every
    /// change of the top-level Z-order fires EVENT_OBJECT_REORDER on the desktop window, so this thread
    /// checks then and raises ours at once; it confirms with short timer checks, since the raise itself
    /// is carried out by the tray thread. Raises are limited, so a shell that insists on being on top is
    /// not fought in a busy loop. Each gap is reported once ours is on top again.
    /// Runs on a thread of its own and never waits for the tray thread (the raise is posted to it).
    /// </summary>
    internal sealed class HostRoleKeeper : IDisposable
    {
        private const uint EVENT_OBJECT_REORDER = 0x8004;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const int WM_TIMER = 0x0113, WM_QUIT = 0x0012;
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;

        /// <summary>Checks this often while the role is lost.</summary>
        private const uint RecheckMs = 5;

        /// <summary>At most this many raises per second; beyond that, one every <see cref="BackOffMs"/>.</summary>
        private const int MaxRaisesPerSecond = 40;
        private const int BackOffMs = 250;

        private readonly IntPtr _ours;
        private readonly Action<RoleGap> _onGap;
        private readonly Action<string> _log;
        private readonly Thread _thread;
        private readonly WinEventProc _proc;
        private readonly IntPtr _desktop = GetDesktopWindow();
        private readonly ManualResetEventSlim _running = new();
        private uint _threadId;
        private long _lostAt;
        private int _raises;
        private long _secondStart;
        private int _raisesThisSecond;
        private long _lastRaise;
        private bool _backingOff;
        private UIntPtr _timer;

        /// <param name="onGap">Called on this keeper's thread when a gap ends; must not wait.</param>
        public HostRoleKeeper(IntPtr ours, Action<RoleGap> onGap, Action<string> log)
        {
            _ours = ours;
            _onGap = onGap;
            _log = log;
            _proc = OnWinEvent;
            _thread = new Thread(Run) { IsBackground = true, Name = "Tray host role" };
            _thread.Start();
        }

        private void Run()
        {
            _threadId = GetCurrentThreadId();
            IntPtr hook = SetWinEventHook(EVENT_OBJECT_REORDER, EVENT_OBJECT_REORDER, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            if (hook == IntPtr.Zero) _log("Could not watch the Z-order (SetWinEventHook failed); ManagedShell's 100 ms check keeps the tray host role");
            // The thread has a message queue now (the hook made one), so Dispose can post to it.
            _running.Set();
            Check();
            try
            {
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_TIMER && msg.hwnd == IntPtr.Zero) Check();
                    else DispatchMessage(ref msg);
                }
            }
            finally
            {
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                StopTimer();
            }
        }

        private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            // Top-level windows are the desktop window's children.
            if (hwnd == _desktop) Check();
        }

        private void Check()
        {
            IntPtr top = FindWindow("Shell_TrayWnd", null);
            if (top == _ours || top == IntPtr.Zero)
            {
                if (_lostAt != 0)
                {
                    var gap = new RoleGap(_lostAt, Stopwatch.GetTimestamp(), _raises);
                    _lostAt = 0;
                    _raises = 0;
                    _backingOff = false;
                    StopTimer();
                    _onGap(gap);
                }
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (_lostAt == 0) _lostAt = now;
            if (Stopwatch.GetElapsedTime(_secondStart, now) >= TimeSpan.FromSeconds(1))
            {
                _secondStart = now;
                _raisesThisSecond = 0;
            }
            bool allowed = _raisesThisSecond < MaxRaisesPerSecond || Stopwatch.GetElapsedTime(_lastRaise, now) >= TimeSpan.FromMilliseconds(BackOffMs);
            if (allowed)
            {
                if (_raisesThisSecond >= MaxRaisesPerSecond && !_backingOff)
                {
                    _backingOff = true;
                    _log($"Another Shell_TrayWnd keeps going above ours; raising ours every {BackOffMs} ms until it stops");
                }
                // Posted to the tray thread, which owns the window: this thread never waits for it.
                SetWindowPos(_ours, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                _raises++;
                _raisesThisSecond++;
                _lastRaise = now;
            }
            StartTimer(_backingOff ? BackOffMs : RecheckMs);
        }

        private void StartTimer(uint ms)
        {
            // A thread timer (no window): WM_TIMER arrives with hwnd 0. SetTimer with the same id resets it.
            _timer = SetTimer(IntPtr.Zero, _timer, ms, IntPtr.Zero);
        }

        private void StopTimer()
        {
            if (_timer == UIntPtr.Zero) return;
            KillTimer(IntPtr.Zero, _timer);
            _timer = UIntPtr.Zero;
        }

        public void Dispose()
        {
            if (_running.Wait(TimeSpan.FromSeconds(1))) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(1));
            GC.KeepAlive(_proc);
        }

        private static readonly IntPtr HWND_TOPMOST = new(-1);

        private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam, lParam;
            public uint time;
            public int x, y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
