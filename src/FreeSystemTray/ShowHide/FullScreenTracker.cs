using System;
using System.Collections.Generic;

namespace FreeSystemTray.ShowHide
{
    /// <summary>
    /// Which full-screen window, if any, is in front on the tray's monitor, so the tray can step
    /// behind it as Explorer's taskbar does. Fed with the shell hook's messages: a window entered or
    /// left full screen (Windows 8 and later), and a window was activated. A full-screen window counts
    /// while it is the last window activated on that monitor: switching to another window there brings
    /// the tray back, working on another monitor does not. Pure logic; window state is asked through
    /// the predicates.
    /// </summary>
    internal sealed class FullScreenTracker
    {
        private const int HistoryLength = 32;

        private readonly HashSet<IntPtr> _fullScreen = new();
        private readonly List<IntPtr> _activated = new(); // most recent first

        public void Entered(IntPtr window) => _fullScreen.Add(window);

        public void Exited(IntPtr window) => _fullScreen.Remove(window);

        /// <summary>A window was activated. The caller leaves out always-on-top windows (flyouts, menus) and the tray's own.</summary>
        public void Activated(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            _activated.Remove(window);
            _activated.Insert(0, window);
            if (_activated.Count > HistoryLength) _activated.RemoveAt(_activated.Count - 1);
        }

        /// <summary>The full-screen window in front on the tray's monitor, or zero.</summary>
        /// <param name="exists">Whether the window still exists.</param>
        /// <param name="isOnTrayMonitor">Whether the window is on the tray's monitor.</param>
        /// <param name="isMinimized">Whether the window is minimized.</param>
        public IntPtr InFront(Func<IntPtr, bool> exists, Func<IntPtr, bool> isOnTrayMonitor, Func<IntPtr, bool> isMinimized)
        {
            _fullScreen.RemoveWhere(w => !exists(w));
            _activated.RemoveAll(w => !exists(w));
            foreach (IntPtr w in _activated)
            {
                if (!isOnTrayMonitor(w)) continue;
                return _fullScreen.Contains(w) && !isMinimized(w) ? w : IntPtr.Zero;
            }
            return IntPtr.Zero;
        }
    }
}
