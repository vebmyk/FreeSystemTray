using System;
using System.Text;

namespace FreeSystemTray
{
    internal static class ProcessInfo
    {
        /// <summary>
        /// The process's exe path, or null. Not Process.GetProcessById: that waits, and a managed wait
        /// on the UI thread lets tray messages run in the middle (FINDINGS.md, "Other findings" 1).
        /// </summary>
        public static string? ImagePath(uint pid)
        {
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }
    }
}
