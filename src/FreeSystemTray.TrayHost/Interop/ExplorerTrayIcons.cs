using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FreeSystemTray.TrayHost.Interop
{
    /// <summary>
    /// One button of Explorer's tray toolbars: whose icon it is, where and whether Explorer shows it,
    /// and Explorer's copy of the icon's data (read from Explorer's memory; the icon handle is
    /// Explorer's copy, valid in any process of the session until Explorer replaces it).
    /// </summary>
    internal readonly record struct ExplorerTrayButton(IntPtr Window, uint Uid, Guid Guid, ExplorerPlacement Toolbar, bool Hidden)
    {
        public string? Tip { get; init; }
        public IntPtr Icon { get; init; }
        public uint CallbackMessage { get; init; }
        public uint Version { get; init; }

        /// <summary>
        /// Explorer's state bits. Bit 0 is NIS_HIDDEN: set when the app hid its icon, and also for
        /// icons Windows hides, such as Meet Now (seen on Windows 10 22H2: 0x400001 and 0x200001).
        /// </summary>
        public uint State { get; init; }

        public bool StateHidden => (State & 1) != 0;

        /// <summary>Whether <paramref name="other"/> is the same icon, the way Shell_NotifyIcon identifies icons.</summary>
        public bool SameIcon(ExplorerTrayButton other) =>
            Guid != Guid.Empty || other.Guid != Guid.Empty ? Guid == other.Guid : Window == other.Window && Uid == other.Uid;
    }

    /// <summary>
    /// Reads the icons Explorer's own tray shows, from its three toolbars on Windows 10: the taskbar's,
    /// the "system-promoted" one next to the chevron (new icons first appear there), and the hidden-icons
    /// flyout's. Explorer keeps a button for icons it hides, such as Meet Now while it is turned off,
    /// with the toolbar's hidden state (seen on Windows 10 22H2). Each button's data is Explorer's
    /// TRAYDATA, read from its memory; the same layout ManagedShell reads when it imports Explorer's icons.
    /// The flyout's toolbar is read directly, so unlike ManagedShell's import this never switches off
    /// "hide inactive icons" (which reversed the flyout's order; FINDINGS.md, Phase 0).
    /// Sends messages to Explorer and waits for them: call it off the tray thread, or before the tray
    /// host takes the role.
    /// </summary>
    internal static class ExplorerTrayIcons
    {
        private const int TB_GETBUTTON = 0x0400 + 23, TB_BUTTONCOUNT = 0x0400 + 24;
        private const byte TBSTATE_HIDDEN = 0x08;
        private const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010;
        private const uint MEM_COMMIT = 0x1000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;
        private const uint TimeoutMs = 500;

        /// <summary>
        /// Every button of Explorer's tray toolbars, in the order they appear from left to right: the
        /// hidden-icons flyout's (behind the chevron), the system-promoted ones, the taskbar's. Null if
        /// they cannot be read: Explorer's taskbar or its toolbars are missing (Explorer restarting;
        /// Windows 11 has none), or Explorer did not answer.
        /// </summary>
        public static List<ExplorerTrayButton>? Read(IntPtr explorerTray)
        {
            if (explorerTray == IntPtr.Zero) return null;
            IntPtr notify = FindWindowEx(explorerTray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify == IntPtr.Zero) return null;
            IntPtr pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
            IntPtr taskbar = pager == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
            IntPtr systemPromoted = FindWindowEx(notify, IntPtr.Zero, "ToolbarWindow32", null);
            IntPtr overflowWindow = FindWindow("NotifyIconOverflowWindow", null);
            IntPtr overflow = overflowWindow == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(overflowWindow, IntPtr.Zero, "ToolbarWindow32", null);
            if (taskbar == IntPtr.Zero && overflow == IntPtr.Zero) return null;

            GetWindowThreadProcessId(explorerTray, out uint pid);
            IntPtr process = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ, false, pid);
            if (process == IntPtr.Zero) return null;
            IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)4096, MEM_COMMIT, PAGE_READWRITE);
            try
            {
                if (remote == IntPtr.Zero) return null;
                var buttons = new List<ExplorerTrayButton>();
                foreach ((IntPtr toolbar, ExplorerPlacement placement) in new[]
                         {
                             (overflow, ExplorerPlacement.Overflow), (systemPromoted, ExplorerPlacement.Taskbar), (taskbar, ExplorerPlacement.Taskbar)
                         })
                {
                    if (toolbar != IntPtr.Zero && !ReadToolbar(toolbar, placement, process, remote, buttons)) return null;
                }
                return buttons;
            }
            finally
            {
                if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
                CloseHandle(process);
            }
        }

        private static bool ReadToolbar(IntPtr toolbar, ExplorerPlacement placement, IntPtr process, IntPtr remote, List<ExplorerTrayButton> buttons)
        {
            if (SendMessageTimeout(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, TimeoutMs, out IntPtr count) == IntPtr.Zero) return false;
            for (int i = 0; i < (int)count; i++)
            {
                if (SendMessageTimeout(toolbar, TB_GETBUTTON, (IntPtr)i, remote, SMTO_ABORTIFHUNG, TimeoutMs, out IntPtr ok) == IntPtr.Zero) return false;
                if (ok == IntPtr.Zero || !TryRead(process, remote, out TBBUTTON button) || button.dwData == IntPtr.Zero) continue;
                if (!TryRead(process, button.dwData, out TRAYDATA data)) continue;
                buttons.Add(new ExplorerTrayButton(data.hWnd, data.uID, data.guidItem, placement, (button.fsState & TBSTATE_HIDDEN) != 0)
                {
                    Tip = data.szIconText,
                    Icon = data.hIcon,
                    CallbackMessage = data.uCallbackMessage,
                    Version = data.uVersion,
                    State = data.dwState
                });
            }
            return true;
        }

        /// <summary>
        /// Where Explorer shows the icon with this identity (a GUID, or its window and uID, as
        /// Shell_NotifyIcon identifies icons): on the taskbar or in the hidden-icons flyout if any of
        /// its buttons is visible, hidden if it has only hidden ones, unknown if it has none.
        /// </summary>
        public static ExplorerPlacement PlacementOf(IReadOnlyList<ExplorerTrayButton> buttons, IntPtr window, uint uid, Guid guid)
        {
            ExplorerPlacement found = ExplorerPlacement.Unknown;
            foreach (ExplorerTrayButton b in buttons)
            {
                bool same = guid != Guid.Empty ? b.Guid == guid : b.Window == window && b.Uid == uid;
                if (!same) continue;
                if (!b.Hidden) return b.Toolbar;
                found = ExplorerPlacement.Hidden;
            }
            return found;
        }

        /// <summary>The button of the same icon as <paramref name="icon"/>, preferring a visible one; null if none.</summary>
        public static ExplorerTrayButton? Find(IReadOnlyList<ExplorerTrayButton> buttons, ExplorerTrayButton icon)
        {
            ExplorerTrayButton? found = null;
            foreach (ExplorerTrayButton b in buttons)
            {
                if (!b.SameIcon(icon)) continue;
                if (!b.Hidden) return b;
                found ??= b;
            }
            return found;
        }

        /// <summary>
        /// The buttons with one per icon (the visible one if Explorer has two), in their order. Explorer
        /// can list an icon twice for a moment while it moves it between toolbars.
        /// </summary>
        public static List<ExplorerTrayButton> OnePerIcon(IReadOnlyList<ExplorerTrayButton> buttons)
        {
            var result = new List<ExplorerTrayButton>();
            foreach (ExplorerTrayButton b in buttons)
            {
                int i = result.FindIndex(r => r.SameIcon(b));
                if (i < 0) result.Add(b);
                else if (result[i].Hidden && !b.Hidden) result[i] = b;
            }
            return result;
        }

        private static bool TryRead<T>(IntPtr process, IntPtr address, out T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            IntPtr local = Marshal.AllocHGlobal(size);
            try
            {
                if (!ReadProcessMemory(process, address, local, (IntPtr)size, out IntPtr read) || read.ToInt64() != size)
                {
                    value = default;
                    return false;
                }
                value = Marshal.PtrToStructure<T>(local);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(local);
            }
        }

        // TBBUTTON on x64: the two state bytes are followed by 6 bytes of padding.
        [StructLayout(LayoutKind.Sequential)]
        private struct TBBUTTON
        {
            public int iBitmap;
            public int idCommand;
            public byte fsState;
            public byte fsStyle;
            public byte r0, r1, r2, r3, r4, r5;
            public IntPtr dwData;
            public IntPtr iString;
        }

        // Explorer's data for each tray button (ManagedShell's ExplorerTrayService.TrayItem).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TRAYDATA
        {
            public IntPtr hWnd;
            public uint uID;
            public uint uCallbackMessage;
            public uint dwState;
            public uint uVersion;
            public IntPtr hIcon;
            public IntPtr uIconDemoteTimerID;
            public uint dwUserPref;
            public uint dwLastSoundTime;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szIconText;
            public uint uNumSeconds;
            public Guid guidItem;
        }

        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
