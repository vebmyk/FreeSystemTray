using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray.Placement
{
    /// <summary>The current monitors in physical pixels (the process is per-monitor DPI aware).</summary>
    public static class Monitors
    {
        public static IReadOnlyList<MonitorInfo> GetAll()
        {
            var list = new List<MonitorInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT clip, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    list.Add(new MonitorInfo(
                        new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                        new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
                        info.szDevice,
                        (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private const uint MONITORINFOF_PRIMARY = 1;

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT clip, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    }
}
