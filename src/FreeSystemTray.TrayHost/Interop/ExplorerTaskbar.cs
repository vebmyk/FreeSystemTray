using System;

namespace FreeSystemTray.TrayHost.Interop
{
    /// <summary>Explorer's own taskbar windows. None of these calls send messages or wait.</summary>
    internal static class ExplorerTaskbar
    {
        private const string TrayClass = "Shell_TrayWnd";
        private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";
        private const string OverflowClass = "NotifyIconOverflowWindow";

        /// <summary>The first Shell_TrayWnd in Z-order that is not ours: Explorer's taskbar.</summary>
        public static IntPtr Find(IntPtr ours)
        {
            IntPtr h = IntPtr.Zero;
            while ((h = NativeMethods.FindWindowEx(IntPtr.Zero, h, TrayClass, null)) != IntPtr.Zero)
            {
                if (h != ours) return h;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// The identifier Explorer passes to Shell_NotifyIconGetRect for its hidden-icons chevron
        /// (hWnd 0, uID -1), to place the overflow flyout. Seen on Windows 10 22H2.
        /// </summary>
        public static readonly Guid ChevronGuid = new("964b6542-bbad-44ee-848a-3a95d85951ea");

        /// <summary>Screen rectangle of the hidden-icons chevron: the visible Button in Explorer's TrayNotifyWnd.</summary>
        public static bool TryGetChevronRect(IntPtr explorerTray, out PixelRect rect)
        {
            rect = default;
            IntPtr notify = NativeMethods.FindWindowEx(explorerTray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify == IntPtr.Zero) return false;
            IntPtr h = IntPtr.Zero;
            while ((h = NativeMethods.FindWindowEx(notify, h, "Button", null)) != IntPtr.Zero)
            {
                if (NativeMethods.IsWindowVisible(h) && NativeMethods.GetWindowRect(h, out NativeMethods.RECT r) && r.Right > r.Left)
                {
                    rect = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Whether <paramref name="hwnd"/> is one of Explorer's taskbars (primary or secondary), not ours.</summary>
        public static bool IsExplorerTaskbar(IntPtr hwnd, IntPtr ours)
        {
            if (hwnd == IntPtr.Zero || hwnd == ours) return false;
            var sb = new System.Text.StringBuilder(32);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            return cls == TrayClass || cls == SecondaryTrayClass;
        }

        /// <summary>The window Shell_NotifyIcon sends to: the highest Shell_TrayWnd.</summary>
        public static IntPtr CurrentHost() => NativeMethods.FindWindow(TrayClass, null);

        public static bool IsTopmost(IntPtr hwnd) =>
            (NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;

        /// <summary>
        /// Make Explorer's taskbar always-on-top again while keeping our Shell_TrayWnd above it, so
        /// FindWindow keeps returning ours. Inserting it directly below our (topmost) window makes it
        /// topmost without ever putting it above ours.
        /// </summary>
        public static bool MakeTopmostBelow(IntPtr explorer, IntPtr ours)
        {
            const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
            if (ours != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(explorer, ours, 0, 0, 0, 0, flags);
                if (IsTopmost(explorer)) return true;
            }
            // Fallback: raise Explorer's, then ours above it. Messages sent in between go to Explorer only.
            NativeMethods.SetWindowPos(explorer, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
            if (ours != IntPtr.Zero) NativeMethods.SetWindowPos(ours, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
            return IsTopmost(explorer);
        }

        /// <summary>
        /// Whether the foreground window covers its whole monitor (a full-screen app, game or video).
        /// Explorer takes away its taskbar's always-on-top flag while one is in front.
        /// </summary>
        public static bool FullScreenAppInFront()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero || !NativeMethods.GetWindowRect(fg, out NativeMethods.RECT r)) return false;
            var sb = new System.Text.StringBuilder(32);
            NativeMethods.GetClassName(fg, sb, sb.Capacity);
            if (sb.ToString() is "Progman" or "WorkerW") return false; // the desktop covers the monitor too
            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info)) return false;
            NativeMethods.RECT m = info.rcMonitor;
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        }

        /// <summary>Whether the point is over Explorer's taskbar (any monitor) or its hidden-icons flyout.</summary>
        public static bool IsOverExplorerTray(PixelPoint p, IntPtr ours)
        {
            IntPtr h = IntPtr.Zero;
            while ((h = NativeMethods.FindWindowEx(IntPtr.Zero, h, TrayClass, null)) != IntPtr.Zero)
            {
                if (h != ours && Contains(h, p)) return true;
            }
            h = IntPtr.Zero;
            while ((h = NativeMethods.FindWindowEx(IntPtr.Zero, h, SecondaryTrayClass, null)) != IntPtr.Zero)
            {
                if (Contains(h, p)) return true;
            }
            IntPtr overflow = NativeMethods.FindWindow(OverflowClass, null);
            return overflow != IntPtr.Zero && NativeMethods.IsWindowVisible(overflow) && Contains(overflow, p);
        }

        private static bool Contains(IntPtr hwnd, PixelPoint p) =>
            NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT r) &&
            new PixelRect(r.Left, r.Top, r.Right, r.Bottom).Contains(p);
    }
}
