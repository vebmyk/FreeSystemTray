using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using FreeSystemTray.Logging;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray
{
    /// <summary>
    /// A second instance of the exe (<c>--watchdog PID START_TIME</c>) that waits for the tray
    /// process to end. If it ends with a non-zero exit code (crash, killed), the watchdog hands the
    /// tray back to Explorer (<see cref="TrayHandBack.AfterHostDied"/>). A clean exit needs nothing:
    /// ManagedShell broadcasts TaskbarCreated itself when it is disposed.
    /// </summary>
    internal static class Watchdog
    {
        /// <summary>Starts the watchdog for the current process.</summary>
        public static void Launch(string logDir)
        {
            NativeMethods.GetProcessTimes(NativeMethods.GetCurrentProcess(), out long created, out _, out _, out _);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--watchdog");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(created.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--log-dir");
            start.ArgumentList.Add(logDir);
            using Process? watchdog = Process.Start(start);
            AppLog.Write($"Watchdog started (pid {watchdog?.Id})");
        }

        public static int Run(int pid, long startTime, string logDir)
        {
            AppLog.Init(Path.Combine(logDir, "watchdog.log"));
            try
            {
                return Watch(pid, startTime);
            }
            catch (Exception ex)
            {
                AppLog.Write("Watchdog failed: " + ex);
                return 1;
            }
            finally
            {
                AppLog.Flush(TimeSpan.FromSeconds(2));
            }
        }

        private static int Watch(int pid, long startTime)
        {
            IntPtr process = NativeMethods.OpenProcess(NativeMethods.SYNCHRONIZE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (process == IntPtr.Zero)
            {
                AppLog.Write($"Watchdog: process {pid} is already gone");
                HandBackToExplorer();
                return 0;
            }
            try
            {
                // Guards against the pid having been reused before we opened it.
                if (!NativeMethods.GetProcessTimes(process, out long created, out _, out _, out _) || created != startTime)
                {
                    AppLog.Write($"Watchdog: pid {pid} is a different process now");
                    return 1;
                }
                AppLog.Write($"Watchdog watching pid {pid}");
                NativeMethods.WaitForSingleObject(process, NativeMethods.INFINITE);
                NativeMethods.GetExitCodeProcess(process, out uint exitCode);
                if (exitCode == 0)
                {
                    AppLog.Write($"FreeSystemTray (pid {pid}) exited cleanly");
                    return 0;
                }
                AppLog.Write($"FreeSystemTray (pid {pid}) ended with exit code 0x{exitCode:X8}; handing the tray back to Explorer");
                HandBackToExplorer();
                return 0;
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }

        private static void HandBackToExplorer() => AppLog.Write("Hand-back: " + TrayHandBack.AfterHostDied());
    }
}
