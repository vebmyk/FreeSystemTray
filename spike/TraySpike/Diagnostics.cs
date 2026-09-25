using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TraySpike.Tray;

namespace TraySpike
{
    /// <summary>Plain append-only text log.</summary>
    public static class Log
    {
        private static readonly object Sync = new();
        private static string? _path;

        public static void Init(string path) => _path = path;

        public static void Write(string message)
        {
            if (_path == null) return;
            string line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
            lock (Sync)
            {
                try { File.AppendAllText(_path, line, Encoding.UTF8); } catch { /* diagnostics only */ }
            }
        }
    }

    public static class SysInfo
    {
        public static string WindowsBuildString()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return $"{key?.GetValue("ProductName")} {key?.GetValue("DisplayVersion")} build {key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")}";
        }
    }

    /// <summary>Describes an icon for the log and for state.json (read by the test harness).</summary>
    public static class IconDescriber
    {
        public static Dictionary<string, object?> Describe(ITrayIcon icon, Rect? screenPx)
        {
            var d = new Dictionary<string, object?>
            {
                ["id"] = icon.Id,
                ["tip"] = icon.ToolTip,
                ["hidden"] = icon.IsHidden,
                ["path"] = icon.OwnerPath,
            };
            if (icon is ManagedShellTrayIcon ms)
            {
                var ni = ms.Source;
                Native.GetWindowThreadProcessId(ni.HWnd, out uint pid);
                d["hwnd"] = $"0x{ni.HWnd.ToInt64():X}";
                d["pid"] = pid;
                d["process"] = ProcessName(pid);
                d["uid"] = ni.UID;
                d["guid"] = ni.GUID == Guid.Empty ? null : ni.GUID.ToString();
                d["version"] = ni.Version;
                d["callbackMsg"] = $"0x{ni.CallbackMessage:X}";
                d["windowAlive"] = Native.IsWindow(ni.HWnd);
            }
            if (icon.Image is BitmapSource bs)
                d["iconPx"] = $"{bs.PixelWidth}x{bs.PixelHeight}";
            if (screenPx is Rect r)
                d["rect"] = new[] { (int)r.Left, (int)r.Top, (int)r.Width, (int)r.Height };
            return d;
        }

        public static string OneLine(ITrayIcon icon)
        {
            var d = Describe(icon, null);
            return string.Join(" ", d.Where(kv => kv.Value != null).Select(kv => $"{kv.Key}={kv.Value}"));
        }

        // Not Process.GetProcessById: it performs a wait, and a managed wait on the STA thread pumps
        // sent messages, which re-enters ManagedShell's tray WndProc (see FINDINGS.md).
        private static string? ProcessName(uint pid)
        {
            IntPtr h = Native.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? Path.GetFileNameWithoutExtension(sb.ToString()) : null;
            }
            finally { Native.CloseHandle(h); }
        }
    }

    /// <summary>Snapshot of shell state for the harness: which window is the tray host, Explorer's taskbar flags.</summary>
    public static class ShellState
    {
        public static Dictionary<string, object?> Capture(IntPtr ourTray)
        {
            IntPtr first = Native.FindWindow("Shell_TrayWnd", null);
            IntPtr explorer = FindOtherTray(ourTray);
            var d = new Dictionary<string, object?>
            {
                ["ourTray"] = $"0x{ourTray.ToInt64():X}",
                ["findWindowResult"] = $"0x{first.ToInt64():X}",
                ["weAreHost"] = first == ourTray && ourTray != IntPtr.Zero,
                ["explorerTray"] = $"0x{explorer.ToInt64():X}",
            };
            if (explorer != IntPtr.Zero)
            {
                int ex = Native.GetWindowLong(explorer, Native.GWL_EXSTYLE);
                d["explorerTrayTopmost"] = (ex & Native.WS_EX_TOPMOST) != 0;
                d["explorerTrayVisible"] = Native.IsWindowVisible(explorer);
                Native.GetWindowThreadProcessId(explorer, out uint pid);
                d["explorerPid"] = pid;
            }
            return d;
        }

        public static IntPtr FindOtherTray(IntPtr ours)
        {
            IntPtr h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
            {
                if (h != ours) return h;
            }
            return IntPtr.Zero;
        }
    }

    public static class StateFile
    {
        private static string? _last;

        public static void WriteIfChanged(string path, object state)
        {
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            if (json == _last) return;
            _last = json;
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, path, true);
            }
            catch { /* diagnostics only */ }
        }
    }

    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public static readonly IntPtr HWND_TOPMOST = new(-1);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);
    }
}
