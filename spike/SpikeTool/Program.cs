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
    ///   watch-zorder MS           for MS milliseconds, print (with the time) each change of the highest
    ///                             Shell_TrayWnd (the tray host; polled every 2 ms) and the WinEvents that
    ///                             could announce it: EVENT_OBJECT_REORDER, foreground changes, and
    ///                             Shell_TrayWnd windows created, shown or destroyed
    ///   raise-explorer [MS]       put Explorer's taskbar above every other Shell_TrayWnd (it takes the
    ///                             tray host role), and keep it there for MS milliseconds (default: once)
    ///   watch-tooltips MS         for MS milliseconds, print (with the time) every tooltip window that
    ///                             appears, moves or disappears, with its text and owner process
    ///   fg                        show the foreground window
    ///   click X Y [left|right|middle|double] [shift]   with shift, Shift is held during the click
    ///   move X Y                  move the cursor (hover)
    ///   drag X1 Y1 X2 Y2          left-button drag
    ///   key KEYS                  press a key or chord: esc, enter, or modifiers (ctrl, alt, shift, win)
    ///                             plus a letter, digit or f1..f24, for example ctrl+alt+shift+t or win+b
    ///   idle                      milliseconds since the last keyboard or mouse input (any, also simulated)
    ///   wait-idle MS [TIMEOUT]    wait until nobody has touched mouse or keyboard for MS milliseconds;
    ///                             exit code 3 if that did not happen within TIMEOUT ms (default 60000)
    ///   click-target X Y [left|right]  show a window of our own at X,Y (120x60, never activated),
    ///                             click it, close it: a harmless place to click "somewhere else"
    ///   fullscreen-probe [X Y]    show a full-screen window, then turn a normal window full screen, on
    ///                             the primary monitor (1.5 s each); log the shell hook's and Explorer's
    ///                             appbar notifications, Explorer's topmost flag, and the colour at X,Y
    ///   fullscreen-window MS [f11]  a full-screen window on the primary monitor for MS milliseconds; with
    ///                             f11, a normal window that turns full screen for most of that time
    ///   pixel X Y                 the colour on screen at X,Y (#RRGGBB)
    ///   mouse-down X Y, mouse-up  press the left button at X,Y, and release it (to look at a pressed state)
    ///   post "WINDOW TITLE" N     PostMessage(WM_APP+N)
    ///   taskbarcreated [HWND]     broadcast the TaskbarCreated message (or post it to one window)
    ///   appbar                    what apps see from the tray host: ABM_GETTASKBARPOS, ABM_GETSTATE,
    ///                             ABM_GETAUTOHIDEBAR(EX) per edge; plus Explorer's own ABM_GETSTATE answer
    ///   autohide-probe [EDGE]     register a temporary auto-hide appbar (default: top edge), ask the
    ///                             host for the edge's auto-hide bar, then remove it again
    ///   iconrect HWND UID [GUID]  Shell_NotifyIconGetRect, as the icon's app would call it
    ///   explorer-delete HWND UID [GUID]  NIM_DELETE sent to Explorer's tray only (it forgets the icon)
    ///   explorer-add HWND UID GUID|- CALLBACK "EXE PATH" "TIP"  NIM_ADD sent to Explorer's tray only,
    ///                             with a placeholder icon and the given exe path in shell32's field
    ///   explorer-hide HWND UID on|off  NIM_MODIFY with NIS_HIDDEN set or cleared, sent to Explorer's tray
    ///                             only: Explorer hides the icon, the tray host's copy stays visible
    ///   explorer-tip HWND UID "TIP"   NIM_MODIFY of the tooltip, sent to Explorer's tray only
    ///   explorer-... raise        any of the explorer-add/delete/hide/tip commands followed by "raise":
    ///                             then put Explorer's taskbar above the tray host's for a moment, as if
    ///                             the message had arrived while Explorer held the tray host role
    ///   explorer-tray             Explorer's taskbar child windows (tray area, chevron, clock) with rects
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
                if (args.Length > 1 && args[0].StartsWith("explorer-", StringComparison.OrdinalIgnoreCase) && args[^1] == "raise")
                {
                    int code = Main(args[..^1]);
                    RaiseExplorer(0);
                    return code;
                }
                switch (args[0].ToLowerInvariant())
                {
                    case "explorer-icons": ExplorerIcons(); break;
                    case "traywnds": TrayWnds(); break;
                    case "windows": Windows(args.Length > 1 ? args[1] : null); break;
                    case "watch-tooltips": WatchTooltips(I(args[1])); break;
                    case "watch-zorder": WatchZOrder(I(args[1])); break;
                    case "raise-explorer": RaiseExplorer(args.Length > 1 ? I(args[1]) : 0); break;
                    case "fg": Foreground(); break;
                    case "click":
                        // "shift" after the kind holds Shift down during the click.
                        if (args.Length > 4 && args[4] == "shift") ShiftClick(I(args[1]), I(args[2]), args[3]);
                        else Click(I(args[1]), I(args[2]), args.Length > 3 ? args[3] : "left");
                        break;
                    case "move": W.SetCursorPos(I(args[1]), I(args[2])); break;
                    case "drag": Drag(I(args[1]), I(args[2]), I(args[3]), I(args[4])); break;
                    case "key": Key(args[1]); break;
                    case "idle": Console.WriteLine($"idle {IdleMs()} ms"); break;
                    case "wait-idle": return WaitIdle(I(args[1]), args.Length > 2 ? I(args[2]) : 60_000);
                    case "click-target": ClickTarget(I(args[1]), I(args[2]), args.Length > 3 ? args[3] : "left"); break;
                    case "fullscreen-probe": FullScreenProbe(args); break;
                    case "mouse-down": W.SetCursorPos(I(args[1]), I(args[2])); Thread.Sleep(60); W.SendInput(1, new[] { new W.INPUT { type = 0, mi = new W.MOUSEINPUT { dwFlags = 0x0002 } } }, Marshal.SizeOf<W.INPUT>()); break;
                    case "mouse-up": W.SendInput(1, new[] { new W.INPUT { type = 0, mi = new W.MOUSEINPUT { dwFlags = 0x0004 } } }, Marshal.SizeOf<W.INPUT>()); break;
                    case "fullscreen-window": FullScreenWindow(I(args[1]), args.Length > 2 && args[2] == "f11"); break;
                    case "pixel":
                    {
                        IntPtr dc = W.GetDC(IntPtr.Zero);
                        uint c = W.GetPixel(dc, I(args[1]), I(args[2]));
                        W.ReleaseDC(IntPtr.Zero, dc);
                        Console.WriteLine($"#{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}"); // COLORREF is 0x00BBGGRR
                        break;
                    }
                    case "post": Post(args[1], I(args[2])); break;
                    case "replay-add": ReplayAdd(args); break;
                    case "taskbarcreated":
                        if (args.Length > 1)
                        {
                            // Only one app re-registers: post it to that app's window.
                            IntPtr target = new IntPtr(Convert.ToInt64(args[1], 16));
                            Console.WriteLine($"TaskbarCreated posted to {Describe(target)} -> {W.PostMessage(target, W.RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero)}");
                            break;
                        }
                        W.SendNotifyMessage(new IntPtr(0xFFFF), (uint)W.RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
                        Console.WriteLine("TaskbarCreated broadcast sent");
                        break;
                    case "appbar": AppBar(); break;
                    case "autohide-probe": AutoHideProbe(args.Length > 1 ? Edge(args[1]) : 1); break;
                    case "iconrect": IconRect(args); break;
                    case "explorer-delete": ExplorerDelete(args); break;
                    case "explorer-add": ExplorerAdd(args); break;
                    case "explorer-hide": ExplorerHide(args); break;
                    case "explorer-tip": ExplorerTip(args); break;
                    case "explorer-tray": ExplorerTray(); break;
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
            // Windows 10 first shows a newly added icon here, on the taskbar, before it moves it to
            // the overflow. The direct ToolbarWindow32 child of TrayNotifyWnd.
            IntPtr systemPromoted = W.FindWindowEx(notify, IntPtr.Zero, "ToolbarWindow32", null);
            IntPtr overflowWnd = W.FindWindow("NotifyIconOverflowWindow", null);
            IntPtr overflow = overflowWnd == IntPtr.Zero ? IntPtr.Zero : W.FindWindowEx(overflowWnd, IntPtr.Zero, "ToolbarWindow32", null);

            int total = 0;
            total += DumpToolbar("system-promoted", systemPromoted);
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
                    Console.WriteLine($"  #{i}{(hidden ? " HIDDEN" : "")} '{Clean(td.szIconText)}' owner={Proc(owner)}({owner}) hwnd=0x{td.hWnd.ToInt64():X} alive={W.IsWindow(td.hWnd)} uid={td.uID} ver={td.uVersion} cb=0x{td.uCallbackMessage:X} state=0x{td.dwState:X} pref={td.dwUserPref} hicon=0x{td.hIcon.ToInt64():X}{guid}{rect}");
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

        private static string Describe2(IntPtr h)
        {
            if (h == IntPtr.Zero) return "0";
            W.GetWindowThreadProcessId(h, out uint pid);
            return $"0x{h.ToInt64():X} [{Cls(h)}] {Proc(pid)}";
        }

        private static void WatchZOrder(int ms)
        {
            var watch = Stopwatch.StartNew();
            IntPtr host = W.FindWindow("Shell_TrayWnd", null);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} HOST {Describe2(host)}");
            W.WinEventProc proc = (hook, evt, hwnd, idObject, idChild, thread, time) =>
            {
                string cls = hwnd == IntPtr.Zero ? "" : Cls(hwnd);
                bool tray = cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
                string what = evt switch
                {
                    0x0003 => "FOREGROUND",
                    0x8004 => "REORDER",
                    0x8000 => tray && idObject == 0 ? "CREATE" : null,
                    0x8001 => tray && idObject == 0 ? "DESTROY" : null,
                    0x8002 => tray && idObject == 0 ? "SHOW" : null,
                    _ => null
                } ?? "";
                if (what == "") return;
                if (evt == 0x8004 && idObject != 0) what += $" obj={idObject}";
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} EVENT {what} {Describe2(hwnd)} (top host now {Describe2(W.FindWindow("Shell_TrayWnd", null))})");
            };
            IntPtr h1 = W.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, proc, 0, 0, 0x0002);
            IntPtr h2 = W.SetWinEventHook(0x8000, 0x8004, IntPtr.Zero, proc, 0, 0, 0x0002);
            try
            {
                while (watch.ElapsedMilliseconds < ms)
                {
                    while (W.PeekMessage(out W.MSG msg, IntPtr.Zero, 0, 0, 1))
                    {
                        W.TranslateMessage(ref msg);
                        W.DispatchMessage(ref msg);
                    }
                    IntPtr now = W.FindWindow("Shell_TrayWnd", null);
                    if (now != host)
                    {
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} HOST {Describe2(now)}");
                        host = now;
                    }
                    Thread.Sleep(2);
                }
            }
            finally
            {
                W.UnhookWinEvent(h1);
                W.UnhookWinEvent(h2);
                GC.KeepAlive(proc);
            }
        }

        private static void RaiseExplorer(int ms)
        {
            IntPtr explorer = ExplorerTrayWnd();
            var watch = Stopwatch.StartNew();
            do
            {
                W.SetWindowPos(explorer, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
                Thread.Sleep(1);
            } while (watch.ElapsedMilliseconds < ms);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} raised Explorer's taskbar {Describe2(explorer)}; host now {Describe2(W.FindWindow("Shell_TrayWnd", null))}");
        }

        /// <summary>Polls every 20 ms for visible tooltips_class32 windows and prints each change.</summary>
        private static void WatchTooltips(int ms)
        {
            var shown = new Dictionary<IntPtr, string>();
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < ms)
            {
                var now = new Dictionary<IntPtr, string>();
                W.EnumWindows((h, _) =>
                {
                    if (Cls(h) != "tooltips_class32" || !W.IsWindowVisible(h)) return true;
                    W.GetWindowRect(h, out var r);
                    W.GetWindowThreadProcessId(h, out uint pid);
                    var sb = new StringBuilder(512);
                    W.SendMessageTimeout(h, 0x000D /* WM_GETTEXT */, (IntPtr)sb.Capacity, sb, 2, 200, out _);
                    now[h] = $"0x{h.ToInt64():X} {Proc(pid)} '{Clean(sb.ToString())}' rect=[{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}]";
                    return true;
                }, IntPtr.Zero);
                foreach (var (h, text) in now)
                    if (!shown.TryGetValue(h, out string? before) || before != text) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} TOOLTIP {text}");
                foreach (var h in shown.Keys)
                    if (!now.ContainsKey(h)) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} TOOLTIP GONE 0x{h.ToInt64():X}");
                shown = now;
                Thread.Sleep(20);
            }
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

        private static void ShiftClick(int x, int y, string kind)
        {
            W.SendInput(1, new[] { new W.INPUT { type = 1, ki = new W.KEYBDINPUT { wVk = 0x10 } } }, Marshal.SizeOf<W.INPUT>());
            try
            {
                Click(x, y, kind);
            }
            finally
            {
                Thread.Sleep(40);
                W.SendInput(1, new[] { new W.INPUT { type = 1, ki = new W.KEYBDINPUT { wVk = 0x10, dwFlags = 0x0002 } } }, Marshal.SizeOf<W.INPUT>());
            }
        }

        /// <summary>Left-button drag from (x1, y1) to (x2, y2) in 20 steps.</summary>
        private static void Drag(int x1, int y1, int x2, int y2)
        {
            W.SetCursorPos(x1, y1);
            Thread.Sleep(60);
            W.SendInput(1, new[] { new W.INPUT { type = 0, mi = new W.MOUSEINPUT { dwFlags = 0x0002 } } }, Marshal.SizeOf<W.INPUT>());
            const int steps = 20;
            for (int i = 1; i <= steps; i++)
            {
                Thread.Sleep(15);
                W.SetCursorPos(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
            }
            Thread.Sleep(60);
            W.SendInput(1, new[] { new W.INPUT { type = 0, mi = new W.MOUSEINPUT { dwFlags = 0x0004 } } }, Marshal.SizeOf<W.INPUT>());
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

        private static void Key(string chord)
        {
            var vks = new List<ushort>();
            foreach (string part in chord.ToLowerInvariant().Split('+'))
            {
                vks.Add(part switch
                {
                    "esc" => 0x1B,
                    "enter" => 0x0D,
                    "ctrl" => 0x11,
                    "alt" => 0x12,
                    "shift" => 0x10,
                    "win" => 0x5B,
                    _ when part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]) => char.ToUpperInvariant(part[0]),
                    _ when part.Length > 1 && part[0] == 'f' && int.TryParse(part[1..], out int f) && f is >= 1 and <= 24 => (ushort)(0x6F + f),
                    _ => throw new ArgumentException(chord)
                });
            }
            var list = new List<W.INPUT>();
            foreach (var vk in vks) list.Add(new W.INPUT { type = 1, ki = new W.KEYBDINPUT { wVk = vk } });
            for (int i = vks.Count - 1; i >= 0; i--) list.Add(new W.INPUT { type = 1, ki = new W.KEYBDINPUT { wVk = vks[i], dwFlags = 0x0002 } });
            W.SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<W.INPUT>());
        }

        private static uint IdleMs()
        {
            var info = new W.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<W.LASTINPUTINFO>() };
            W.GetLastInputInfo(ref info);
            return unchecked((uint)Environment.TickCount - info.dwTime);
        }

        /// <summary>Waits for the user to leave mouse and keyboard alone, so simulated input does not mix with theirs.</summary>
        private static int WaitIdle(int ms, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (IdleMs() < ms)
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    Console.WriteLine($"not idle for {ms} ms within {timeoutMs} ms");
                    return 3;
                }
                Thread.Sleep(100);
            }
            Console.WriteLine($"idle {IdleMs()} ms after waiting {sw.ElapsedMilliseconds} ms");
            return 0;
        }

        /// <summary>
        /// Clicks "somewhere else" without touching anyone else's window: a plain always-on-top window
        /// of this process, shown for the click and closed again. It never activates (WS_EX_NOACTIVATE),
        /// so the click is the only thing that happens: no change of foreground window.
        /// </summary>
        private static void ClickTarget(int x, int y, string kind)
        {
            const int WS_POPUP = unchecked((int)0x80000000), WS_BORDER = 0x00800000, SS_NOTIFY = 0x0100;
            const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            // SS_NOTIFY: a static control is otherwise transparent to clicks, which would land on whatever is below.
            IntPtr h = W.CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "SpikeTool click target",
                WS_POPUP | WS_BORDER | SS_NOTIFY, x, y, 120, 60, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (h == IntPtr.Zero) throw new InvalidOperationException("could not create the click target");
            try
            {
                // Created hidden and shown without activation: a new process's first window created
                // visible takes the foreground even with WS_EX_NOACTIVATE.
                W.ShowWindow(h, 4); // SW_SHOWNOACTIVATE
                Pump(200);
                Click(x + 60, y + 30, kind);
                Pump(300);
                Console.WriteLine($"clicked target 0x{h.ToInt64():X} at {x + 60},{y + 30}; foreground now 0x{W.GetForegroundWindow().ToInt64():X}");
            }
            finally
            {
                W.DestroyWindow(h);
            }
        }

        private static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                while (W.PeekMessage(out W.MSG msg, IntPtr.Zero, 0, 0, 1))
                {
                    W.TranslateMessage(ref msg);
                    W.DispatchMessage(ref msg);
                }
                Thread.Sleep(10);
            }
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

        #region Phase 1 probes: appbar queries, icon rectangles, Explorer-only messages

        private static int Edge(string s) => s.ToLowerInvariant() switch
        {
            "left" => 0,
            "top" => 1,
            "right" => 2,
            "bottom" => 3,
            _ => throw new ArgumentException("edge must be left, top, right or bottom")
        };

        private static readonly string[] EdgeNames = { "left", "top", "right", "bottom" };

        /// <summary>Explorer's own taskbar: the Shell_TrayWnd owned by explorer.exe.</summary>
        private static IntPtr ExplorerTrayWnd()
        {
            IntPtr h = IntPtr.Zero;
            while ((h = W.FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
            {
                W.GetWindowThreadProcessId(h, out uint p);
                if (Proc(p).Equals("explorer", StringComparison.OrdinalIgnoreCase)) return h;
            }
            return IntPtr.Zero;
        }

        private static string Describe(IntPtr h)
        {
            if (h == IntPtr.Zero) return "0";
            W.GetWindowThreadProcessId(h, out uint pid);
            return $"0x{h.ToInt64():X} [{Cls(h)}] {Proc(pid)}({pid})";
        }

        private static W.RECT PrimaryMonitor() => new() { Left = 0, Top = 0, Right = W.GetSystemMetrics(0), Bottom = W.GetSystemMetrics(1) };

        private static void AppBar()
        {
            IntPtr host = W.FindWindow("Shell_TrayWnd", null);
            Console.WriteLine($"tray host (FindWindow): {Describe(host)}");

            var abd = new W.APPBARDATA { cbSize = Marshal.SizeOf<W.APPBARDATA>() };
            ulong ok = (ulong)W.SHAppBarMessage(5 /*ABM_GETTASKBARPOS*/, ref abd);
            string edge = abd.uEdge < 4 ? EdgeNames[abd.uEdge] : abd.uEdge.ToString(CultureInfo.InvariantCulture);
            Console.WriteLine($"ABM_GETTASKBARPOS -> {ok}: rect=[{abd.rc.Left},{abd.rc.Top},{abd.rc.Right - abd.rc.Left},{abd.rc.Bottom - abd.rc.Top}] edge={edge}");

            abd = new W.APPBARDATA { cbSize = Marshal.SizeOf<W.APPBARDATA>() };
            ulong state = (ulong)W.SHAppBarMessage(4 /*ABM_GETSTATE*/, ref abd);
            Console.WriteLine($"ABM_GETSTATE -> {state} (autohide={(state & 1) != 0}, alwaysontop={(state & 2) != 0})");

            IntPtr explorer = ExplorerTrayWnd();
            if (explorer != IntPtr.Zero)
                Console.WriteLine($"ABM_GETSTATE sent to Explorer's taskbar directly -> {SendAppBarDirect(explorer, 4, 0)}");

            W.RECT monitor = PrimaryMonitor();
            for (int e = 0; e < 4; e++)
            {
                abd = new W.APPBARDATA { cbSize = Marshal.SizeOf<W.APPBARDATA>(), uEdge = (uint)e };
                IntPtr bar = (IntPtr)(long)(ulong)W.SHAppBarMessage(7 /*ABM_GETAUTOHIDEBAR*/, ref abd);
                abd.rc = monitor;
                IntPtr barEx = (IntPtr)(long)(ulong)W.SHAppBarMessage(11 /*ABM_GETAUTOHIDEBAREX*/, ref abd);
                Console.WriteLine($"ABM_GETAUTOHIDEBAR {EdgeNames[e],-6} -> {Describe(bar)}; EX (primary monitor) -> {Describe(barEx)}");
            }
        }

        /// <summary>An appbar message sent straight to a window as WM_COPYDATA (no shared memory: GETSTATE only).</summary>
        private static long SendAppBarDirect(IntPtr target, int abm, uint edge)
        {
            var payload = new byte[64];
            BitConverter.GetBytes(40).CopyTo(payload, 0);            // APPBARDATA.cbSize (32-bit layout)
            BitConverter.GetBytes(edge).CopyTo(payload, 12);         // uEdge
            BitConverter.GetBytes(abm).CopyTo(payload, 40);          // dwMessage
            BitConverter.GetBytes(Environment.ProcessId).CopyTo(payload, 56);
            return SendCopyData(target, 0, payload).ToInt64();
        }

        private static IntPtr SendCopyData(IntPtr target, long dwData, byte[] payload, IntPtr wParam = default)
        {
            IntPtr data = Marshal.AllocHGlobal(payload.Length);
            IntPtr cds = Marshal.AllocHGlobal(Marshal.SizeOf<W.COPYDATASTRUCT>());
            try
            {
                Marshal.Copy(payload, 0, data, payload.Length);
                Marshal.StructureToPtr(new W.COPYDATASTRUCT { dwData = (IntPtr)dwData, cbData = payload.Length, lpData = data }, cds, false);
                return W.SendMessage(target, 0x004A /*WM_COPYDATA*/, wParam, cds);
            }
            finally
            {
                Marshal.FreeHGlobal(cds);
                Marshal.FreeHGlobal(data);
            }
        }

        private static void AutoHideProbe(int edge)
        {
            IntPtr wnd = W.CreateWindowEx(0x80 /*WS_EX_TOOLWINDOW*/, "STATIC", "SpikeTool auto-hide probe", unchecked((int)0x80000000) /*WS_POPUP*/,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Console.WriteLine($"probe window {Describe(wnd)}; tray host {Describe(W.FindWindow("Shell_TrayWnd", null))}");
            var abd = new W.APPBARDATA { cbSize = Marshal.SizeOf<W.APPBARDATA>(), hWnd = wnd, uCallbackMessage = 0x8000 + 77, uEdge = (uint)edge };
            try
            {
                Console.WriteLine($"ABM_NEW -> {(ulong)W.SHAppBarMessage(0, ref abd)}");
                abd.lParam = (IntPtr)1;
                Console.WriteLine($"ABM_SETAUTOHIDEBAR {EdgeNames[edge]} on -> {(ulong)W.SHAppBarMessage(8, ref abd)}");

                var q = new W.APPBARDATA { cbSize = Marshal.SizeOf<W.APPBARDATA>(), uEdge = (uint)edge };
                IntPtr bar = (IntPtr)(long)(ulong)W.SHAppBarMessage(7 /*ABM_GETAUTOHIDEBAR*/, ref q);
                q.rc = PrimaryMonitor();
                IntPtr barEx = (IntPtr)(long)(ulong)W.SHAppBarMessage(11 /*ABM_GETAUTOHIDEBAREX*/, ref q);
                Console.WriteLine($"ABM_GETAUTOHIDEBAR {EdgeNames[edge]} -> {Describe(bar)} {(bar == wnd ? "(the probe: PASS)" : "(not the probe: FAIL)")}");
                Console.WriteLine($"ABM_GETAUTOHIDEBAREX {EdgeNames[edge]} -> {Describe(barEx)} {(barEx == wnd ? "(the probe: PASS)" : "(not the probe: FAIL)")}");
            }
            finally
            {
                abd.lParam = IntPtr.Zero;
                Console.WriteLine($"ABM_SETAUTOHIDEBAR off -> {(ulong)W.SHAppBarMessage(8, ref abd)}");
                Console.WriteLine($"ABM_REMOVE -> {(ulong)W.SHAppBarMessage(1, ref abd)}");
                W.DestroyWindow(wnd);
            }
        }

        /// <summary>
        /// What tells an app that a full-screen window is in front: the shell hook (RUDEAPPACTIVATED,
        /// FULLSCREENENTER/EXIT) and Explorer's appbar notification ABN_FULLSCREENAPP. Shows two kinds
        /// of full-screen window on the primary monitor for a few seconds each: a new one (a game or
        /// video player) and a normal window that turns full screen without a change of foreground
        /// (F11 in a browser). Also logs whether Explorer's taskbar is always-on-top at each step, and
        /// the colour at a point given as X Y (for example over the tray window).
        /// </summary>
        private static void FullScreenProbe(string[] a)
        {
            int px = a.Length > 2 ? I(a[1]) : -1, py = a.Length > 2 ? I(a[2]) : -1;
            const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000, SS_BLACKRECT = 0x4;
            int shellHook = W.RegisterWindowMessage("SHELLHOOK");
            int appBarMsg = W.RegisterWindowMessage("SpikeTool.FullScreenProbe");
            var sw = Stopwatch.StartNew();
            var log = new List<string>();
            void Note(string s) => log.Add($"{sw.ElapsedMilliseconds,6} ms  {s}");
            string State()
            {
                IntPtr fg = W.GetForegroundWindow();
                IntPtr explorer = ExplorerTrayWnd();
                string pixel = "";
                if (px >= 0)
                {
                    IntPtr dc = W.GetDC(IntPtr.Zero);
                    pixel = $", pixel at {px},{py} = 0x{W.GetPixel(dc, px, py):X6}";
                    W.ReleaseDC(IntPtr.Zero, dc);
                }
                return $"foreground {Describe(fg)}, Explorer's taskbar topmost={(W.GetWindowLong(explorer, -20) & 8) != 0}{pixel}";
            }

            IntPtr listener = W.CreateWindowEx(0x80, "STATIC", "SpikeTool full-screen probe", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            IntPtr original = IntPtr.Zero;
            W.WndProc proc = (h, m, wp, lp) =>
            {
                if (m == shellHook)
                {
                    int code = (int)wp.ToInt64();
                    string name = code switch { 4 => "WINDOWACTIVATED", 0x8004 => "RUDEAPPACTIVATED", 53 => "FULLSCREENENTER", 54 => "FULLSCREENEXIT", 16 => "MONITORCHANGED", _ => null! };
                    if (name != null) Note($"SHELLHOOK {name} {Describe(lp)}");
                }
                else if (m == appBarMsg)
                {
                    Note($"APPBAR ABN={wp.ToInt64()}{(wp.ToInt64() == 2 ? " (ABN_FULLSCREENAPP)" : "")} lParam={lp.ToInt64()}");
                }
                return W.CallWindowProc(original, h, m, wp, lp);
            };
            original = W.SetWindowLongPtr(listener, -4, Marshal.GetFunctionPointerForDelegate(proc));
            Console.WriteLine($"RegisterShellHookWindow -> {W.RegisterShellHookWindow(listener)}; tray host {Describe(W.FindWindow("Shell_TrayWnd", null))}");
            var abd = new W.APPBARDATA { cbSize = Marshal.SizeOf<W.APPBARDATA>(), hWnd = listener, uCallbackMessage = (uint)appBarMsg };
            Console.WriteLine($"ABM_NEW -> {(ulong)W.SHAppBarMessage(0, ref abd)}");
            W.RECT m = PrimaryMonitor();
            try
            {
                Pump(500);
                Note("start: " + State());

                IntPtr fs = W.CreateWindowEx(0, "STATIC", "SpikeTool full screen", WS_POPUP | WS_VISIBLE | SS_BLACKRECT,
                    m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Note($"new full-screen window {Describe(fs)} shown; SetForegroundWindow -> {W.SetForegroundWindow(fs)}");
                Pump(1500);
                Note("after 1.5 s: " + State());
                W.DestroyWindow(fs);
                Note("full-screen window closed");
                Pump(1500);
                Note("after 1.5 s: " + State());

                IntPtr win = W.CreateWindowEx(0, "STATIC", "SpikeTool window", WS_OVERLAPPEDWINDOW | WS_VISIBLE | SS_BLACKRECT,
                    m.Left + 200, m.Top + 200, 400, 300, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Note($"normal window {Describe(win)} shown; SetForegroundWindow -> {W.SetForegroundWindow(win)}");
                Pump(1000);
                Note("after 1 s: " + State());
                W.SetWindowLongPtr(win, -16, (IntPtr)(WS_POPUP | WS_VISIBLE | SS_BLACKRECT));
                W.SetWindowPos(win, IntPtr.Zero, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, 0x0020 /*FRAMECHANGED*/ | 0x0004 /*NOZORDER*/);
                Note("same window made full screen (F11)");
                Pump(1500);
                Note("after 1.5 s: " + State());
                W.SetWindowLongPtr(win, -16, (IntPtr)(WS_OVERLAPPEDWINDOW | WS_VISIBLE | SS_BLACKRECT));
                W.SetWindowPos(win, IntPtr.Zero, m.Left + 200, m.Top + 200, 400, 300, 0x0020 | 0x0004);
                Note("back to a normal window");
                Pump(1500);
                Note("after 1.5 s: " + State());
                W.DestroyWindow(win);
                Pump(500);
                Note("end: " + State());
            }
            finally
            {
                Console.WriteLine($"ABM_REMOVE -> {(ulong)W.SHAppBarMessage(1, ref abd)}");
                W.DeregisterShellHookWindow(listener);
                W.SetWindowLongPtr(listener, -4, original);
                W.DestroyWindow(listener);
                GC.KeepAlive(proc);
            }
            foreach (string line in log) Console.WriteLine(line);
        }

        /// <summary>
        /// A full-screen window on the primary monitor for MS milliseconds, like a game or a video
        /// player. With "f11": a normal window that turns full screen after 1 s without a change of
        /// foreground, and back to normal 1 s before it closes, like F11 in a browser.
        /// </summary>
        private static void FullScreenWindow(int ms, bool f11)
        {
            const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000, SS_BLACKRECT = 0x4;
            W.RECT m = PrimaryMonitor();
            var sw = Stopwatch.StartNew();
            IntPtr h = f11
                ? W.CreateWindowEx(0, "STATIC", "SpikeTool window", WS_OVERLAPPEDWINDOW | WS_VISIBLE | SS_BLACKRECT, m.Left + 200, m.Top + 200, 400, 300, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
                : W.CreateWindowEx(0, "STATIC", "SpikeTool full screen", WS_POPUP | WS_VISIBLE | SS_BLACKRECT, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            // A process's first ShowWindow follows how it was started (a hidden start would hide the
            // window), so show it twice.
            W.ShowWindow(h, 5);
            W.ShowWindow(h, 5);
            Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  {(f11 ? "normal" : "full-screen")} window {Describe(h)}; SetForegroundWindow -> {W.SetForegroundWindow(h)}");
            try
            {
                if (f11)
                {
                    Pump(1000);
                    W.SetWindowLongPtr(h, -16, (IntPtr)(WS_POPUP | WS_VISIBLE | SS_BLACKRECT));
                    W.SetWindowPos(h, IntPtr.Zero, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, 0x0020 | 0x0004); // FRAMECHANGED | NOZORDER
                    Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  made full screen");
                    Pump(Math.Max(0, ms - 2000));
                    W.SetWindowLongPtr(h, -16, (IntPtr)(WS_OVERLAPPEDWINDOW | WS_VISIBLE | SS_BLACKRECT));
                    W.SetWindowPos(h, IntPtr.Zero, m.Left + 200, m.Top + 200, 400, 300, 0x0020 | 0x0004);
                    Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  back to a normal window");
                    Pump(1000);
                }
                else
                {
                    Pump(ms);
                }
            }
            finally
            {
                W.DestroyWindow(h);
                Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  closed");
            }
        }

        private static void IconRect(string[] a)
        {
            var id = new W.NOTIFYICONIDENTIFIER
            {
                cbSize = Marshal.SizeOf<W.NOTIFYICONIDENTIFIER>(),
                hWnd = new IntPtr(Convert.ToInt64(a[1], 16)),
                uID = uint.Parse(a[2], CultureInfo.InvariantCulture),
                guidItem = a.Length > 3 ? Guid.Parse(a[3]) : Guid.Empty
            };
            int hr = W.Shell_NotifyIconGetRect(ref id, out W.RECT r);
            Console.WriteLine(hr == 0 ? $"icon rect [{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}]" : $"Shell_NotifyIconGetRect failed: 0x{hr:X8}");
        }

        /// <summary>NIM_DELETE sent only to Explorer's taskbar, so Explorer forgets an icon the host still has.</summary>
        private static void ExplorerDelete(string[] a)
        {
            IntPtr explorer = ExplorerTrayWnd();
            if (explorer == IntPtr.Zero) { Console.WriteLine("Explorer's taskbar not found"); return; }
            // Same shape as shell32's message on Windows 10 22H2: 1484 bytes (SHELLTRAYDATA with a
            // 32-bit NOTIFYICONDATAW, cbSize 956, then 520 more bytes), wParam = the icon's window.
            var payload = new byte[1484];
            uint hwnd = (uint)Convert.ToInt64(a[1], 16);
            BitConverter.GetBytes(0x34753423u).CopyTo(payload, 0);   // signature
            BitConverter.GetBytes(2u).CopyTo(payload, 4);            // NIM_DELETE
            BitConverter.GetBytes(956).CopyTo(payload, 8);           // cbSize
            BitConverter.GetBytes(hwnd).CopyTo(payload, 12);
            BitConverter.GetBytes(uint.Parse(a[2], CultureInfo.InvariantCulture)).CopyTo(payload, 16);
            if (a.Length > 3)
            {
                BitConverter.GetBytes(0x20u).CopyTo(payload, 20);    // NIF_GUID
                Guid.Parse(a[3]).ToByteArray().CopyTo(payload, 8 + 936);
            }
            Console.WriteLine($"NIM_DELETE to Explorer's taskbar {Describe(explorer)} -> {SendCopyData(explorer, 1, payload, new IntPtr(hwnd))}");
        }

        private static void ExplorerTip(string[] a)
        {
            IntPtr explorer = ExplorerTrayWnd();
            if (explorer == IntPtr.Zero) { Console.WriteLine("Explorer's taskbar not found"); return; }
            var payload = new byte[1484];
            uint hwnd = (uint)Convert.ToInt64(a[1], 16);
            BitConverter.GetBytes(0x34753423u).CopyTo(payload, 0);   // signature
            BitConverter.GetBytes(1u).CopyTo(payload, 4);            // NIM_MODIFY
            BitConverter.GetBytes(956).CopyTo(payload, 8);           // cbSize
            BitConverter.GetBytes(hwnd).CopyTo(payload, 12);
            BitConverter.GetBytes(uint.Parse(a[2], CultureInfo.InvariantCulture)).CopyTo(payload, 16);
            BitConverter.GetBytes(0x04u).CopyTo(payload, 20);        // NIF_TIP
            byte[] tip = Encoding.Unicode.GetBytes(a[3]);
            Array.Copy(tip, 0, payload, 32, Math.Min(tip.Length, 254));
            Console.WriteLine($"NIM_MODIFY tip '{a[3]}' to Explorer's taskbar -> {SendCopyData(explorer, 1, payload, new IntPtr(hwnd))}");
        }

        /// <summary>
        /// NIM_MODIFY with NIS_HIDDEN set or cleared, sent only to Explorer's taskbar: Explorer hides
        /// (or shows) the icon while the tray host's copy stays as the app registered it, as Windows
        /// does with Meet Now while it is turned off.
        /// </summary>
        private static void ExplorerHide(string[] a)
        {
            IntPtr explorer = ExplorerTrayWnd();
            if (explorer == IntPtr.Zero) { Console.WriteLine("Explorer's taskbar not found"); return; }
            var payload = new byte[1484];
            uint hwnd = (uint)Convert.ToInt64(a[1], 16);
            bool hide = a[3] == "on";
            BitConverter.GetBytes(0x34753423u).CopyTo(payload, 0);   // signature
            BitConverter.GetBytes(1u).CopyTo(payload, 4);            // NIM_MODIFY
            BitConverter.GetBytes(956).CopyTo(payload, 8);           // cbSize
            BitConverter.GetBytes(hwnd).CopyTo(payload, 12);
            BitConverter.GetBytes(uint.Parse(a[2], CultureInfo.InvariantCulture)).CopyTo(payload, 16);
            BitConverter.GetBytes(0x08u).CopyTo(payload, 20);        // NIF_STATE
            BitConverter.GetBytes(hide ? 1u : 0u).CopyTo(payload, 8 + 280);   // dwState: NIS_HIDDEN
            BitConverter.GetBytes(1u).CopyTo(payload, 8 + 284);               // dwStateMask
            Console.WriteLine($"NIM_MODIFY NIS_HIDDEN={hide} to Explorer's taskbar -> {SendCopyData(explorer, 1, payload, new IntPtr(hwnd))}");
        }

        /// <summary>
        /// NIM_ADD sent only to Explorer's taskbar, shaped like shell32's (1484 bytes, exe path after
        /// NOTIFYICONDATAW), to find out which exe path forms Explorer accepts for a GUID icon.
        /// </summary>
        private static void ExplorerAdd(string[] a)
        {
            IntPtr explorer = ExplorerTrayWnd();
            if (explorer == IntPtr.Zero) { Console.WriteLine("Explorer's taskbar not found"); return; }
            var payload = new byte[1484];
            uint hwnd = (uint)Convert.ToInt64(a[1], 16);
            uint flags = 0x1 | 0x2 | 0x4;                            // NIF_MESSAGE | NIF_ICON | NIF_TIP
            BitConverter.GetBytes(0x34753423u).CopyTo(payload, 0);   // signature
            BitConverter.GetBytes(0u).CopyTo(payload, 4);            // NIM_ADD
            BitConverter.GetBytes(956).CopyTo(payload, 8);           // cbSize
            BitConverter.GetBytes(hwnd).CopyTo(payload, 12);
            BitConverter.GetBytes(uint.Parse(a[2], CultureInfo.InvariantCulture)).CopyTo(payload, 16);
            BitConverter.GetBytes(Convert.ToUInt32(a[4], 16)).CopyTo(payload, 24);   // uCallbackMessage
            BitConverter.GetBytes((uint)W.LoadIcon(IntPtr.Zero, new IntPtr(32518)).ToInt64()).CopyTo(payload, 28); // IDI_SHIELD
            byte[] tip = Encoding.Unicode.GetBytes(a[6]);
            Array.Copy(tip, 0, payload, 32, Math.Min(tip.Length, 254));
            if (a[3] != "-")
            {
                flags |= 0x20;                                       // NIF_GUID
                Guid.Parse(a[3]).ToByteArray().CopyTo(payload, 8 + 936);
            }
            BitConverter.GetBytes(flags).CopyTo(payload, 20);
            byte[] path = Encoding.Unicode.GetBytes(a[5]);
            Array.Copy(path, 0, payload, 964, Math.Min(path.Length, 518));
            Console.WriteLine($"NIM_ADD '{a[5]}' to Explorer's taskbar -> {SendCopyData(explorer, 1, payload, new IntPtr(hwnd))}");
        }

        private static void ExplorerTray()
        {
            IntPtr explorer = ExplorerTrayWnd();
            if (explorer == IntPtr.Zero) { Console.WriteLine("Explorer's taskbar not found"); return; }
            W.GetWindowRect(explorer, out W.RECT tr);
            Console.WriteLine($"{Describe(explorer)} rect=[{tr.Left},{tr.Top},{tr.Right - tr.Left},{tr.Bottom - tr.Top}]");
            W.EnumChildWindows(explorer, (h, _) =>
            {
                W.GetWindowRect(h, out W.RECT r);
                Console.WriteLine($"  0x{h.ToInt64():X} [{Cls(h)}] '{Title(h)}' rect=[{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}]{(W.IsWindowVisible(h) ? "" : " hidden")}");
                return true;
            }, IntPtr.Zero);
        }

        #endregion

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

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONIDENTIFIER
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize, dwTime; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

        public delegate IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] public static extern IntPtr CallWindowProc(IntPtr prev, IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool RegisterShellHookWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool DeregisterShellHookWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr h, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA d);
        [DllImport("user32.dll")] public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr name);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
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
