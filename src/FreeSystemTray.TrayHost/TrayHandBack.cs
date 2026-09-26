using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
            return topmost + "; broadcast TaskbarCreated, so apps re-register their icons with Explorer; " + RemoveDeadIcons();
        }

        /// <summary>
        /// Removes the icons of windows that no longer exist from Explorer's tray: FreeSystemTray's own
        /// icon, mirrored there, died with the process. Explorer removes such icons only when the
        /// pointer passes over them.
        /// </summary>
        public static string RemoveDeadIcons()
        {
            List<ExplorerTrayButton>? buttons = ExplorerTrayIcons.Read(ExplorerTaskbar.Find(IntPtr.Zero));
            if (buttons == null) return "Explorer's tray could not be read to remove dead icons";
            int removed = 0;
            foreach (ExplorerTrayButton b in buttons.Where(b => b.Window != IntPtr.Zero && !IsWindow(b.Window)))
            {
                var data = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = b.Window,
                    uID = b.Uid,
                    uFlags = b.Guid != Guid.Empty ? NIF_GUID : 0,
                    guidItem = b.Guid
                };
                if (Shell_NotifyIcon(NIM_DELETE, ref data)) removed++;
            }
            return $"removed {removed} icon{(removed == 1 ? "" : "s")} of windows that no longer exist";
        }

        private const uint NIM_DELETE = 2, NIF_GUID = 0x20;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

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
