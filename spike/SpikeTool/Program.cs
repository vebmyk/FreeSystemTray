using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SpikeTool
{
    /// <summary>
    /// Test harness for the Phase 0 spike.
    ///   explorer-icons            list icons in Explorer's own tray toolbars (promoted + overflow)
    ///   traywnds                  list Shell_TrayWnd windows in Z-order (the first one receives Shell_NotifyIcon)
    ///   windows [filter]          list visible top-level windows (menus, flyouts, ...)
    ///   fg                        show the foreground window
    ///   click X Y [left|right|middle|double]
    ///   move X Y                  move the cursor (hover)
    ///   key esc|enter|win+a|win+b
    ///   post "WINDOW TITLE" N     PostMessage(WM_APP+N)
    ///   taskbarcreated            broadcast the TaskbarCreated message
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            W.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: physical pixels everywhere
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0) { Console.WriteLine("usage: see source"); return 1; }
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "explorer-icons": ExplorerIcons(); break;
                    case "traywnds": TrayWnds(); break;
                    case "windows": Windows(args.Length > 1 ? args[1] : null); break;
                    case "fg": Foreground(); break;
                    case "click": Click(I(args[1]), I(args[2]), args.Length > 3 ? args[3] : "left"); break;
                    case "move": W.SetCursorPos(I(args[1]), I(args[2])); break;
                    case "key": Key(args[1]); break;
                    case "post": Post(args[1], I(args[2])); break;
                    case "replay-add": ReplayAdd(args); break;
                    case "taskbarcreated":
                        W.SendNotifyMessage(new IntPtr(0xFFFF), (uint)W.RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
                        Console.WriteLine("TaskbarCreated broadcast sent");
                        break;
                    default: Console.WriteLine("unknown command"); return 1;
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                return 2;
            }
        }

        private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        private static string Proc(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
        }

        private static string Cls(IntPtr h)
        {
            var sb = new StringBuilder(256);
            W.GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string Title(IntPtr h)
        {
            var sb = new StringBuilder(512);
            W.GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        #region Explorer tray contents

        private static void ExplorerIcons()
        {
            // Explorer's taskbar is the Shell_TrayWnd that belongs to explorer.exe (there may be ours too).
            IntPtr tray = IntPtr.Zero, h = IntPtr.Zero;
            while ((h = W.FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
            {
                W.GetWindowThreadProcessId(h, out uint p);
                if (Proc(p).Equals("explorer", StringComparison.OrdinalIgnoreCase)) { tray = h; break; }
            }
            if (tray == IntPtr.Zero) { Console.WriteLine("Explorer taskbar not found"); return; }

            IntPtr notify = W.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            IntPtr pager = W.FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
            IntPtr promoted = W.FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
            IntPtr overflowWnd = W.FindWindow("NotifyIconOverflowWindow", null);
            IntPtr overflow = overflowWnd == IntPtr.Zero ? IntPtr.Zero : W.FindWindowEx(overflowWnd, IntPtr.Zero, "ToolbarWindow32", null);

            int total = 0;
            total += DumpToolbar("promoted", promoted);
            total += DumpToolbar("overflow", overflow);
            Console.WriteLine($"TOTAL {total}");
        }

        private static int DumpToolbar(string label, IntPtr toolbar)
        {
            if (toolbar == IntPtr.Zero) { Console.WriteLine($"[{label}] toolbar not found"); return 0; }
            int count = (int)W.SendMessage(toolbar, W.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            Console.WriteLine($"[{label}] toolbar 0x{toolbar.ToInt64():X}: {count} buttons");
            W.GetWindowThreadProcessId(toolbar, out uint pid);
            IntPtr hp = W.OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400, false, pid); // VM_OPERATION|VM_READ|VM_WRITE|QUERY_INFORMATION
            if (hp == IntPtr.Zero) { Console.WriteLine("  OpenProcess failed"); return 0; }
            IntPtr remote = W.VirtualAllocEx(hp, IntPtr.Zero, (UIntPtr)4096, 0x1000, 0x04);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    W.SendMessage(toolbar, W.TB_GETBUTTON, (IntPtr)i, remote);
                    var tb = Read<TBBUTTON>(hp, remote);
                    bool hidden = (tb.fsState & 0x08) != 0;
                    string rect = "";
                    if (W.SendMessage(toolbar, W.TB_GETITEMRECT, (IntPtr)i, remote) != IntPtr.Zero)
                    {
                        var r = Read<W.RECT>(hp, remote);
                        var pts = new W.POINT[] { new() { X = r.Left, Y = r.Top }, new() { X = r.Right, Y = r.Bottom } };
                        W.MapWindowPoints(toolbar, IntPtr.Zero, pts, 2);
                        rect = $" rect=[{pts[0].X},{pts[0].Y},{pts[1].X - pts[0].X},{pts[1].Y - pts[0].Y}]";
                    }
                    if (tb.dwData == IntPtr.Zero) { Console.WriteLine($"  #{i} (no data)"); continue; }
                    var td = Read<TRAYDATA>(hp, tb.dwData);
                    W.GetWindowThreadProcessId(td.hWnd, out uint owner);
                    string guid = td.guidItem == Guid.Empty ? "" : $" guid={td.guidItem}";
                    Console.WriteLine($"  #{i}{(hidden ? " HIDDEN" : "")} '{Clean(td.szIconText)}' owner={Proc(owner)}({owner}) hwnd=0x{td.hWnd.ToInt64():X} alive={W.IsWindow(td.hWnd)} uid={td.uID} ver={td.uVersion} cb=0x{td.uCallbackMessage:X}{guid}{rect}");
                }
            }
            finally
            {
                W.VirtualFreeEx(hp, remote, UIntPtr.Zero, 0x8000);
                W.CloseHandle(hp);
            }
            return count;
        }

        private static string Clean(string s) => s.Replace("\r", " ").Replace("\n", " | ");

        private static T Read<T>(IntPtr hp, IntPtr addr) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            IntPtr local = Marshal.AllocHGlobal(size);
            try
            {
                W.ReadProcessMemory(hp, addr, local, (IntPtr)size, out _);
                return Marshal.PtrToStructure<T>(local);
            }
            finally { Marshal.FreeHGlobal(local); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TBBUTTON
        {
            public int iBitmap;
            public int idCommand;
            public byte fsState;
            public byte fsStyle;
            public byte r0, r1, r2, r3, r4, r5; // padding on x64
            public IntPtr dwData;
            public IntPtr iString;
        }

        // Layout used by Explorer's tray toolbar buttons (same as ManagedShell's ExplorerTrayService.TrayItem).
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

        #endregion

        private static void TrayWnds()
        {
            int n = 0;
            W.EnumWindows((h, _) =>
            {
                if (Cls(h) == "Shell_TrayWnd")
                {
                    W.GetWindowThreadProcessId(h, out uint pid);
                    int ex = W.GetWindowLong(h, -20);
                    Console.WriteLine($"  z#{n} 0x{h.ToInt64():X} {Proc(pid)}({pid}) topmost={(ex & 8) != 0} visible={W.IsWindowVisible(h)}");
                }
                n++;
                return true;
            }, IntPtr.Zero);
            IntPtr first = W.FindWindow("Shell_TrayWnd", null);
            W.GetWindowThreadProcessId(first, out uint fp);
            Console.WriteLine($"FindWindow(Shell_TrayWnd) -> 0x{first.ToInt64():X} {Proc(fp)}({fp})");
        }

        private static void Windows(string? filter)
        {
            int z = 0;
            W.EnumWindows((h, _) =>
            {
                z++;
                if (!W.IsWindowVisible(h)) return true;
                W.GetWindowRect(h, out var r);
                if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;
                W.GetWindowThreadProcessId(h, out uint pid);
                int ex = W.GetWindowLong(h, -20);
                bool cloaked = W.DwmGetWindowAttribute(h, 14, out int c, 4) == 0 && c != 0;
                string line = $"  z{z} 0x{h.ToInt64():X} [{Cls(h)}] '{Title(h)}' {Proc(pid)}({pid}) rect=[{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}]{((ex & 8) != 0 ? " TOPMOST" : "")}{(cloaked ? " CLOAKED" : "")}";
                if (filter == null || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine(line);
                return true;
            }, IntPtr.Zero);
        }

        private static void Foreground()
        {
            IntPtr h = W.GetForegroundWindow();
            W.GetWindowThreadProcessId(h, out uint pid);
            Console.WriteLine($"foreground 0x{h.ToInt64():X} [{Cls(h)}] '{Title(h)}' {Proc(pid)}({pid})");
        }

        #region Input

        private static void Click(int x, int y, string kind)
        {
            W.SetCursorPos(x, y);
            Thread.Sleep(60);
            switch (kind)
            {
                case "left": Button(0x0002, 0x0004); break;
                case "right": Button(0x0008, 0x0010); break;
                case "middle": Button(0x0020, 0x0040); break;
                case "double": Button(0x0002, 0x0004); Thread.Sleep(80); Button(0x0002, 0x0004); break;
                default: throw new ArgumentException(kind);
            }
        }

        private static void Button(uint down, uint up)
        {
            var inputs = new W.INPUT[2];
            inputs[0].type = 0; inputs[0].mi.dwFlags = down;
            inputs[1].type = 0; inputs[1].mi.dwFlags = up;
            W.SendInput(1, new[] { inputs[0] }, Marshal.SizeOf<W.INPUT>());
            Thread.Sleep(40);
            W.SendInput(1, new[] { inputs[1] }, Marshal.SizeOf<W.INPUT>());
        }

        private static void Key(string name)
        {
            ushort[] vks = name.ToLowerInvariant() switch
            {
                "esc" => new ushort[] { 0x1B },
                "enter" => new ushort[] { 0x0D },
                "win+a" => new ushort[] { 0x5B, 0x41 },
                "win+b" => new ushort[] { 0x5B, 0x42 },
                _ => throw new ArgumentException(name)
            };
            var list = new List<W.INPUT>();
            foreach (var vk in vks) list.Add(new W.INPUT { type = 1, ki = new W.KEYBDINPUT { wVk = vk } });
            for (int i = vks.Length - 1; i >= 0; i--) list.Add(new W.INPUT { type = 1, ki = new W.KEYBDINPUT { wVk = vks[i], dwFlags = 0x0002 } });
            W.SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<W.INPUT>());
        }

        #endregion

        /// <summary>
        /// replay-add HWND UID CALLBACKMSG VERSION GUID|- "TIP": re-register an icon on behalf of its owner window
        /// with whichever Shell_TrayWnd is on top (placeholder image), to test handing icons back to Explorer.
        /// </summary>
        private static void ReplayAdd(string[] a)
        {
            var d = new W.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<W.NOTIFYICONDATA>(),
                hWnd = new IntPtr(Convert.ToInt64(a[1], 16)),
                uID = uint.Parse(a[2], CultureInfo.InvariantCulture),
                uCallbackMessage = Convert.ToUInt32(a[3], 16),
                uFlags = 0x1 | 0x2 | 0x4 | 0x80, // MESSAGE | ICON | TIP | SHOWTIP
                hIcon = W.LoadIcon(IntPtr.Zero, new IntPtr(32516)), // IDI_INFORMATION placeholder
                szTip = a.Length > 6 ? a[6] : "",
                szInfo = "",
                szInfoTitle = ""
            };
            if (a[5] != "-")
            {
                d.guidItem = Guid.Parse(a[5]);
                d.uFlags |= 0x20; // NIF_GUID
            }
            bool ok = W.Shell_NotifyIcon(0 /*NIM_ADD*/, ref d);
            int err = Marshal.GetLastWin32Error();
            d.uTimeoutOrVersion = uint.Parse(a[4], CultureInfo.InvariantCulture);
            bool vok = W.Shell_NotifyIcon(4 /*NIM_SETVERSION*/, ref d);
            Console.WriteLine($"replay NIM_ADD hwnd={a[1]} uid={a[2]} guid={a[5]} -> {ok} (err {err}), SETVERSION -> {vok}");
        }

        private static void Post(string title, int n)
        {
            IntPtr h = W.FindWindow(null, title);
            if (h == IntPtr.Zero) { Console.WriteLine($"window '{title}' not found"); return; }
            Console.WriteLine($"PostMessage(WM_APP+{n}) to 0x{h.ToInt64():X} -> {W.PostMessage(h, 0x8000 + n, IntPtr.Zero, IntPtr.Zero)}");
        }
    }

    internal static class W
    {
        public const int TB_GETBUTTON = 0x0417, TB_BUTTONCOUNT = 0x0418, TB_GETITEMRECT = 0x041D;

        public delegate bool EnumProc(IntPtr h, IntPtr l);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public MOUSEINPUT mi;
            [FieldOffset(8)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
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
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA d);
        [DllImport("user32.dll")] public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr name);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SendNotifyMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string s);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern int MapWindowPoints(IntPtr from, IntPtr to, [In, Out] POINT[] pts, int n);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr hp, IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr hp, IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr hp, IntPtr addr, IntPtr buf, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    }
}
