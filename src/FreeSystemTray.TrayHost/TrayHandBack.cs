using System;
using FreeSystemTray.TrayHost.Interop;

namespace FreeSystemTray.TrayHost
{
    /// <summary>
    /// Returns the tray to Explorer after our host process died without cleaning up (crash, killed).
    /// Used by the watchdog process. Mirroring normally keeps Explorer's tray current already; this
    /// covers whatever it missed.
    /// </summary>
    public static class TrayHandBack
    {
        /// <summary>
        /// Broadcasts TaskbarCreated so running apps register their icons with Explorer again, after
        /// making sure Explorer's taskbar is always-on-top. Returns a description for the log.
        /// </summary>
        public static string AfterHostDied()
        {
            // Our Shell_TrayWnd died with the process, so Explorer's taskbar is the host again.
            if (ExplorerTaskbar.CurrentHost() == IntPtr.Zero)
                return "Explorer's taskbar is not running; it collects the icons itself when it starts";
            string topmost = RestoreExplorerTopmost();
            uint taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            NativeMethods.SendNotifyMessage(NativeMethods.HWND_BROADCAST, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
            return topmost + "; broadcast TaskbarCreated, so apps re-register their icons with Explorer";
        }

        /// <summary>
        /// Gives Explorer's taskbar back the always-on-top flag ManagedShell removes when it takes the
        /// role, unless a full-screen app is in front (Explorer drops the flag itself then).
        /// </summary>
        public static string RestoreExplorerTopmost()
        {
            IntPtr explorer = ExplorerTaskbar.Find(IntPtr.Zero);
            if (explorer == IntPtr.Zero) return "Explorer's taskbar not found";
            if (ExplorerTaskbar.IsTopmost(explorer)) return "Explorer's taskbar is always-on-top";
            if (ExplorerTaskbar.FullScreenAppInFront()) return "Explorer's taskbar is not always-on-top, but a full-screen app is in front; left alone";
            bool ok = ExplorerTaskbar.MakeTopmostBelow(explorer, IntPtr.Zero);
            return ok ? "made Explorer's taskbar always-on-top again" : "could not make Explorer's taskbar always-on-top";
        }
    }
}
