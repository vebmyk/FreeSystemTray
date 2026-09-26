using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TestTrayApp
{
    /// <summary>
    /// A tray app that logs every callback it receives, so tray-host behaviour can be checked
    /// without side effects on real apps.
    ///   TestTrayApp --name Alpha [--uid 1] [--icons N] [--v4] [--showtip] [--icon-px 16] [--color #C0392B]
    ///               [--ignore-taskbarcreated] [--log DIR]
    /// With --icons N it registers N icons from its one window, with uIDs uid to uid+N-1; the commands
    /// apply to all of them. With --ignore-taskbarcreated it does not register again when a taskbar
    /// broadcasts TaskbarCreated, as some apps do not.
    /// Control it by posting WM_APP+n to the window titled "TestTrayApp:NAME":
    ///   +1 modify tooltip, +2 show balloon, +3 NIM_DELETE, +4 NIM_ADD, +5 change icon, +6 exit,
    ///   +7 hide the icon (NIS_HIDDEN), +8 show it again, +9 modify tooltip without NIF_SHOWTIP.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var o = Options.Parse(args);
            Directory.CreateDirectory(o.LogDir);
            var app = new TrayTestWindow(o);
            app.Start();
            Application.Run();
            return 0;
        }
    }

    internal sealed class Options
    {
        public string Name = "Test";
        public uint Uid = 1;
        public int Icons = 1;
        public bool IgnoreTaskbarCreated;
        public bool V4;
        public bool ShowTip;
        public int IconPx = 16;
        public Color Color = Color.FromArgb(0xC0, 0x39, 0x2B);
        public string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraySpike");

        public static Options Parse(string[] a)
        {
            var o = new Options();
            for (int i = 0; i < a.Length; i++)
            {
                string next() => a[++i];
                switch (a[i].ToLowerInvariant())
                {
                    case "--name": o.Name = next(); break;
                    case "--uid": o.Uid = uint.Parse(next(), CultureInfo.InvariantCulture); break;
                    case "--icons": o.Icons = int.Parse(next(), CultureInfo.InvariantCulture); break;
                    case "--ignore-taskbarcreated": o.IgnoreTaskbarCreated = true; break;
                    case "--v4": o.V4 = true; break;
                    case "--showtip": o.ShowTip = true; break;
                    case "--icon-px": o.IconPx = int.Parse(next(), CultureInfo.InvariantCulture); break;
                    case "--color": o.Color = ColorTranslator.FromHtml(next()); break;
                    case "--log": o.LogDir = next(); break;
                }
            }
            return o;
        }
    }

    internal sealed class TrayTestWindow : NativeWindow
    {
        private const int CallbackMsg = Win32.WM_APP + 100;
        private static readonly int TaskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");

        private readonly Options _o;
        private readonly string _logPath;
        private IntPtr _icon;
        private IntPtr _menu;
        private int _tipCounter;
        private int _balloonCounter;
        private int _iconCounter;
        private string _tip;

        public TrayTestWindow(Options o)
        {
            _o = o;
            _logPath = Path.Combine(o.LogDir, $"testtray-{o.Name}.log");
            _tip = $"{o.Name} tip #0";
        }

        public void Start()
        {
            CreateHandle(new CreateParams { Caption = "TestTrayApp:" + _o.Name });
            _icon = MakeIcon(_o.Color);
            _menu = Win32.CreatePopupMenu();
            Win32.AppendMenu(_menu, 0, 1, $"{_o.Name}: test item A");
            Win32.AppendMenu(_menu, 0, 2, $"{_o.Name}: test item B");
            Win32.AppendMenu(_menu, 0x800 /*MF_SEPARATOR*/, 0, null);
            Win32.AppendMenu(_menu, 0, 99, "Exit test app");
            Log($"START pid={Environment.ProcessId} hwnd=0x{Handle.ToInt64():X} uid={_o.Uid} icons={_o.Icons} v4={_o.V4} showtip={_o.ShowTip} iconPx={_o.IconPx} ignoreTaskbarCreated={_o.IgnoreTaskbarCreated}");
            AddIcon("startup");
        }

        private void Log(string message)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{_o.Name}] {message}{Environment.NewLine}";
            try { File.AppendAllText(_logPath, line, Encoding.UTF8); } catch { }
        }

        private static string HostDescription()
        {
            IntPtr tray = Win32.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return "host=<none>";
            Win32.GetWindowThreadProcessId(tray, out uint pid);
            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
            return $"host={name}({pid}) tray=0x{tray.ToInt64():X}";
        }

        private Win32.NOTIFYICONDATA BaseData(uint flags, uint uid)
        {
            return new Win32.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
                hWnd = Handle,
                uID = uid,
                uFlags = flags,
                szTip = "",
                szInfo = "",
                szInfoTitle = ""
            };
        }

        /// <summary>The uIDs of this app's icons.</summary>
        private System.Collections.Generic.IEnumerable<uint> Uids()
        {
            for (int i = 0; i < _o.Icons; i++) yield return _o.Uid + (uint)i;
        }

        /// <summary>A tooltip per icon: the first icon's is the plain one the smoke test looks for.</summary>
        private string TipFor(uint uid) => uid == _o.Uid ? _tip : $"{_tip} (uid {uid})";

        private void AddIcon(string reason)
        {
            foreach (uint uid in Uids())
            {
                uint flags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | (_o.ShowTip ? Win32.NIF_SHOWTIP : 0);
                var d = BaseData(flags, uid);
                d.uCallbackMessage = CallbackMsg;
                d.hIcon = _icon;
                d.szTip = TipFor(uid);
                bool ok = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref d);
                int err = Marshal.GetLastWin32Error();
                Log($"NIM_ADD uid={uid} ({reason}) -> {ok} (err {err}) {HostDescription()}");
                if (_o.V4)
                {
                    var v = BaseData(0, uid);
                    v.uTimeoutOrVersion = 4;
                    bool vok = Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref v);
                    Log($"NIM_SETVERSION 4 uid={uid} -> {vok}");
                }
            }
        }

        private IntPtr MakeIcon(Color color)
        {
            int px = _o.IconPx;
            using var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using var fill = new SolidBrush(color);
                g.FillRectangle(fill, 0, 0, px, px);
                using var pen = new Pen(Color.White, Math.Max(1, px / 16));
                g.DrawRectangle(pen, 0, 0, px - 1, px - 1);
                using var font = new Font("Segoe UI", px * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(_o.Name.Substring(0, 1), font, Brushes.White, new RectangleF(0, 0, px, px), sf);
            }
            return bmp.GetHicon();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TaskbarCreatedMsg)
            {
                Log($"TaskbarCreated received; {HostDescription()}{(_o.IgnoreTaskbarCreated ? "; ignored" : "")}");
                if (!_o.IgnoreTaskbarCreated) AddIcon("TaskbarCreated");
                return;
            }

            switch (m.Msg)
            {
                case CallbackMsg:
                    OnTrayCallback(m.WParam, m.LParam);
                    return;
                case Win32.WM_APP + 1:
                case Win32.WM_APP + 9:
                    _tipCounter++;
                    _tip = $"{_o.Name} tip #{_tipCounter}";
                    bool showTip = _o.ShowTip && m.Msg == Win32.WM_APP + 1;
                    foreach (uint uid in Uids())
                    {
                        var td = BaseData(Win32.NIF_TIP | (showTip ? Win32.NIF_SHOWTIP : 0), uid);
                        td.szTip = TipFor(uid);
                        Log($"NIM_MODIFY tooltip{(showTip ? " (NIF_SHOWTIP)" : "")} uid={uid} -> {Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref td)} '{td.szTip}' {HostDescription()}");
                    }
                    return;
                case Win32.WM_APP + 2:
                    _balloonCounter++;
                    var bd = BaseData(Win32.NIF_INFO, _o.Uid);
                    bd.szInfoTitle = $"Balloon from {_o.Name}";
                    bd.szInfo = $"Test balloon #{_balloonCounter}";
                    bd.dwInfoFlags = 1; // NIIF_INFO
                    Log($"NIM_MODIFY balloon #{_balloonCounter} -> {Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref bd)} {HostDescription()}");
                    return;
                case Win32.WM_APP + 3:
                    foreach (uint uid in Uids())
                    {
                        var dd = BaseData(0, uid);
                        Log($"NIM_DELETE uid={uid} -> {Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref dd)} {HostDescription()}");
                    }
                    return;
                case Win32.WM_APP + 7:
                case Win32.WM_APP + 8:
                    bool hide = m.Msg == Win32.WM_APP + 7;
                    foreach (uint uid in Uids())
                    {
                        var hd = BaseData(Win32.NIF_STATE, uid);
                        hd.dwState = hide ? Win32.NIS_HIDDEN : 0;
                        hd.dwStateMask = Win32.NIS_HIDDEN;
                        Log($"NIM_MODIFY NIS_HIDDEN={hide} uid={uid} -> {Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref hd)} {HostDescription()}");
                    }
                    return;
                case Win32.WM_APP + 4:
                    AddIcon("requested");
                    return;
                case Win32.WM_APP + 5:
                    _iconCounter++;
                    Color[] colors = { Color.SeaGreen, Color.RoyalBlue, Color.DarkOrange, Color.Purple };
                    IntPtr newIcon = MakeIcon(colors[_iconCounter % colors.Length]);
                    foreach (uint uid in Uids())
                    {
                        var id = BaseData(Win32.NIF_ICON, uid);
                        id.hIcon = newIcon;
                        Log($"NIM_MODIFY icon #{_iconCounter} uid={uid} -> {Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref id)} {HostDescription()}");
                    }
                    Win32.DestroyIcon(_icon);
                    _icon = newIcon;
                    return;
                case Win32.WM_APP + 6:
                    Exit("requested");
                    return;
            }
            base.WndProc(ref m);
        }

        private void OnTrayCallback(IntPtr wParam, IntPtr lParam)
        {
            int evt, x, y;
            uint uid;
            long lp = lParam.ToInt64();
            if (_o.V4)
            {
                evt = (int)(lp & 0xFFFF);
                uid = (uint)((lp >> 16) & 0xFFFF);
                long wp = wParam.ToInt64();
                x = (short)(wp & 0xFFFF);
                y = (short)((wp >> 16) & 0xFFFF);
            }
            else
            {
                evt = (int)lp;
                uid = (uint)wParam.ToInt64();
                Win32.GetCursorPos(out var pt);
                x = pt.X;
                y = pt.Y;
            }

            string name = Win32.EventName(evt);
            if (evt == Win32.WM_MOUSEMOVE)
            {
                Log($"EVENT {name} uid={uid} at {x},{y}");
                return;
            }

            string rect = "";
            if (evt is Win32.WM_LBUTTONUP or Win32.WM_RBUTTONUP or Win32.WM_CONTEXTMENU or Win32.NIN_SELECT or Win32.WM_LBUTTONDBLCLK)
            {
                var ident = new Win32.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Win32.NOTIFYICONIDENTIFIER>(), hWnd = Handle, uID = uid };
                int hr = Win32.Shell_NotifyIconGetRect(ref ident, out var r);
                rect = hr == 0 ? $" iconRect=[{r.Left},{r.Top},{r.Right},{r.Bottom}]" : $" iconRect=hr0x{hr:X8}";
            }
            Log($"EVENT {name} uid={uid} at {x},{y}{rect}");

            bool menuTrigger = _o.V4 ? evt == Win32.WM_CONTEXTMENU : evt == Win32.WM_RBUTTONUP;
            if (menuTrigger) ShowMenu(x, y);
        }

        private void ShowMenu(int x, int y)
        {
            bool fgOk = Win32.SetForegroundWindow(Handle);
            bool isFg = Win32.GetForegroundWindow() == Handle;
            Log($"MENU OPEN at {x},{y}; SetForegroundWindow={fgOk} weAreForeground={isFg}");
            int cmd = Win32.TrackPopupMenuEx(_menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON, x, y, Handle, IntPtr.Zero);
            Win32.PostMessage(Handle, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);
            Log($"MENU CLOSED cmd={cmd}");
            if (cmd == 99) Exit("menu");
        }

        private void Exit(string reason)
        {
            foreach (uint uid in Uids())
            {
                var d = BaseData(0, uid);
                Log($"EXIT ({reason}); NIM_DELETE uid={uid} -> {Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref d)}");
            }
            DestroyHandle();
            Application.ExitThread();
        }
    }

    internal static class Win32
    {
        public const int WM_APP = 0x8000;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
        public const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_RBUTTONDBLCLK = 0x0206;
        public const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MBUTTONDBLCLK = 0x0209;
        public const int WM_CONTEXTMENU = 0x007B, WM_MOUSEHOVER = 0x02A1, WM_MOUSELEAVE = 0x02A3;
        public const int NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONSHOW = 0x0402, NIN_BALLOONHIDE = 0x0403;
        public const int NIN_BALLOONTIMEOUT = 0x0404, NIN_BALLOONUSERCLICK = 0x0405, NIN_POPUPOPEN = 0x0406, NIN_POPUPCLOSE = 0x0407;

        public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_STATE = 0x8, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
        public const uint NIS_HIDDEN = 0x1;
        public const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

        public static string EventName(int e) => e switch
        {
            WM_MOUSEMOVE => "WM_MOUSEMOVE",
            WM_LBUTTONDOWN => "WM_LBUTTONDOWN",
            WM_LBUTTONUP => "WM_LBUTTONUP",
            WM_LBUTTONDBLCLK => "WM_LBUTTONDBLCLK",
            WM_RBUTTONDOWN => "WM_RBUTTONDOWN",
            WM_RBUTTONUP => "WM_RBUTTONUP",
            WM_RBUTTONDBLCLK => "WM_RBUTTONDBLCLK",
            WM_MBUTTONDOWN => "WM_MBUTTONDOWN",
            WM_MBUTTONUP => "WM_MBUTTONUP",
            WM_MBUTTONDBLCLK => "WM_MBUTTONDBLCLK",
            WM_CONTEXTMENU => "WM_CONTEXTMENU",
            WM_MOUSEHOVER => "WM_MOUSEHOVER",
            WM_MOUSELEAVE => "WM_MOUSELEAVE",
            NIN_SELECT => "NIN_SELECT",
            NIN_KEYSELECT => "NIN_KEYSELECT",
            NIN_BALLOONSHOW => "NIN_BALLOONSHOW",
            NIN_BALLOONHIDE => "NIN_BALLOONHIDE",
            NIN_BALLOONTIMEOUT => "NIN_BALLOONTIMEOUT",
            NIN_BALLOONUSERCLICK => "NIN_BALLOONUSERCLICK",
            NIN_POPUPOPEN => "NIN_POPUPOPEN",
            NIN_POPUPCLOSE => "NIN_POPUPCLOSE",
            _ => $"0x{e:X4}"
        };

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
        public struct NOTIFYICONIDENTIFIER
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA data);

        [DllImport("shell32.dll")]
        public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string? name);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr hMenu, uint flags, uint id, string? text);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
