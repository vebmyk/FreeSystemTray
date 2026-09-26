using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FreeSystemTray.Logging;

namespace FreeSystemTray
{
    /// <summary>
    /// FreeSystemTray's own tray icon, registered with Shell_NotifyIcon like any app's: it shows in
    /// the tray (the host is this process) and, through mirroring, in Explorer's tray, so the menu and
    /// the settings can be reached while the tray is hidden. Left-click opens the settings; right-click
    /// the app's menu. It is registered again when a new taskbar broadcasts TaskbarCreated.
    /// </summary>
    internal sealed class AppNotifyIcon : IDisposable
    {
        private const uint Uid = 1;
        private const int CallbackMessage = 0x8000 + 0x46; // WM_APP + 'F'
        private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
        private const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, WM_CONTEXTMENU = 0x007B;
        private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

        private readonly Action _open;
        private readonly Action _menu;
        private readonly HwndSource _window;
        private IntPtr _icon;
        private bool _added;

        /// <param name="open">Left-click: open the settings.</param>
        /// <param name="menu">Right-click: the app's menu, at the pointer.</param>
        public AppNotifyIcon(Action open, Action menu)
        {
            _open = open;
            _menu = menu;
            // A hidden top-level window: it gets the icon's callbacks and the TaskbarCreated broadcast,
            // which message-only windows do not.
            _window = new HwndSource(new HwndSourceParameters("FreeSystemTray app icon") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) /* WS_POPUP */ });
            _window.AddHook(WndProc);
        }

        public bool IsShown => _added;

        public void Show()
        {
            if (_added) return;
            if (_icon == IntPtr.Zero) _icon = LoadSmallIcon();
            NOTIFYICONDATA data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            _added = Shell_NotifyIcon(NIM_ADD, ref data);
            if (!_added)
            {
                // Already there (a re-add after TaskbarCreated that raced another): update it instead.
                _added = Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
            data.uVersion = 4;
            if (_added) Shell_NotifyIcon(NIM_SETVERSION, ref data);
            AppLog.Write(_added ? "FreeSystemTray's own icon added" : "FreeSystemTray's own icon could not be added");
        }

        public void Hide()
        {
            if (!_added) return;
            NOTIFYICONDATA data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
            AppLog.Write("FreeSystemTray's own icon removed");
        }

        private NOTIFYICONDATA Data(uint flags) => new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = Uid,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = "FreeSystemTray",
            szInfo = "",
            szInfoTitle = ""
        };

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == CallbackMessage)
            {
                handled = true;
                // Version 4: the event is in lParam's low word.
                switch ((int)(lParam.ToInt64() & 0xFFFF))
                {
                    case NIN_SELECT:
                    case NIN_KEYSELECT:
                        _open();
                        break;
                    case WM_CONTEXTMENU:
                        _menu();
                        break;
                }
            }
            else if (msg == TaskbarCreated && TaskbarCreated != 0 && _added)
            {
                // A new taskbar (ours when the tray host starts, or Explorer's after it restarts) has no icons yet.
                _added = false;
                Show();
            }
            return IntPtr.Zero;
        }

        /// <summary>The exe's icon at the small-icon size of the system DPI.</summary>
        private static IntPtr LoadSmallIcon()
        {
            int size = (int)Math.Round(16 * GetDpiForSystem() / 96.0);
            string? exe = Environment.ProcessPath;
            if (exe != null && SHDefExtractIcon(exe, 0, 0, out IntPtr icon, IntPtr.Zero, (uint)size) == 0 && icon != IntPtr.Zero) return icon;
            return LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
        }

        public void Dispose()
        {
            Hide();
            _window.Dispose();
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public int uCallbackMessage;
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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out IntPtr large, IntPtr small, uint iconSize);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);
    }
}
