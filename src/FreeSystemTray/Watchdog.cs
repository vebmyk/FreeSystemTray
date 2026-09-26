using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using FreeSystemTray.Logging;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray
{
    /// <summary>
    /// When FreeSystemTray ends without cleaning up, whether to start it again. A crash (an unhandled
    /// exception, an access violation, or the watchdog ending a hung tray) is started again; a process
    /// someone ended on purpose is not (Task Manager's End task and taskkill /f give exit code 1,
    /// PowerShell's Stop-Process -1). A tray that keeps crashing soon after it starts is left alone.
    /// </summary>
    public static class CrashPolicy
    {
        /// <summary>The exit code the watchdog ends a hung tray with ("HANG").</summary>
        public const uint HungExitCode = 0x48414E47;

        /// <summary>What an unhandled .NET exception ends the process with.</summary>
        public const uint ClrExceptionCode = 0xE0434352;

        /// <summary>STATUS_CONTROL_C_EXIT: Ctrl+C or a closed console, not a crash.</summary>
        private const uint ControlCExit = 0xC000013A;

        /// <summary>A tray that ran this long before it crashed was working: its count of quick restarts starts over.</summary>
        public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(10);

        /// <summary>At most this many restarts in a row of a tray that crashed before <see cref="StableAfter"/>.</summary>
        public const int MaxQuickRestarts = 3;

        public static bool IsCrash(uint exitCode) =>
            exitCode == HungExitCode || exitCode == ClrExceptionCode ||
            (exitCode >= 0xC0000000 && exitCode != 0xFFFFFFFF && exitCode != ControlCExit);

        /// <summary>
        /// Whether to start the tray again after it ended with <paramref name="exitCode"/> after running
        /// for <paramref name="uptime"/>. <paramref name="quickRestarts"/> is how often it was started
        /// again in a row so far; <paramref name="next"/> is the count to give the new one.
        /// </summary>
        public static bool ShouldRestart(uint exitCode, TimeSpan uptime, int quickRestarts, out int next, out string why)
        {
            next = 0;
            if (!IsCrash(exitCode))
            {
                why = "it did not crash (ended by someone, or it could not start)";
                return false;
            }
            int before = uptime >= StableAfter ? 0 : quickRestarts;
            if (before >= MaxQuickRestarts)
            {
                why = $"it crashed {before + 1} times in a row, each within {StableAfter.TotalMinutes:0} minutes of starting";
                return false;
            }
            next = before + 1;
            why = $"start {next} of at most {MaxQuickRestarts} in a row";
            return true;
        }
    }

    /// <summary>
    /// A second instance of the exe (<c>--watchdog PID START_TIME [--restarts N] -- APP ARGS</c>) that
    /// watches the tray process. If it ends with a non-zero exit code (crash, killed), the watchdog
    /// hands the tray back to Explorer (<see cref="TrayHandBack.AfterHostDied"/>), and after a crash
    /// starts it again with the same arguments (<see cref="CrashPolicy"/>, and the setting
    /// "restartAfterCrash"). If the tray thread stops answering messages for
    /// <see cref="HangProbes"/> probes in a row, the tray is hung: apps' icon messages to it fail, and
    /// with them the mirroring to Explorer. The watchdog then ends it and treats that as a crash. A
    /// clean exit needs nothing: ManagedShell broadcasts TaskbarCreated itself when it is disposed.
    /// </summary>
    internal static class Watchdog
    {
        private const uint PROCESS_TERMINATE = 0x0001, PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint WAIT_TIMEOUT = 0x102;
        private const int WM_NULL = 0, SM_SHUTTINGDOWN = 0x2000;
        private const uint SMTO_BLOCK = 0x1, SMTO_ABORTIFHUNG = 0x2;

        /// <summary>How often the tray thread is asked to answer, and how long it may take.</summary>
        private const uint ProbeMs = 5000;

        /// <summary>After an unanswered probe, the next one comes this much sooner.</summary>
        private const uint RetryMs = 1000;

        /// <summary>
        /// Unanswered probes in a row after which the tray counts as hung: about 25 s after it stopped
        /// answering. (SMTO_ABORTIFHUNG does not cut a probe of the hidden Shell_TrayWnd short.)
        /// </summary>
        private const int HangProbes = 4;

        /// <summary>Starts the watchdog for the current process.</summary>
        /// <param name="appArgs">This process's arguments, to start it again with after a crash.</param>
        /// <param name="restarts">How often it was started again after a crash in a row (<c>--after-crash</c>).</param>
        public static void Launch(string logDir, IReadOnlyList<string> appArgs, int restarts)
        {
            NativeMethods.GetProcessTimes(NativeMethods.GetCurrentProcess(), out long created, out _, out _, out _);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--watchdog");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(created.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--log-dir");
            start.ArgumentList.Add(logDir);
            start.ArgumentList.Add("--restarts");
            start.ArgumentList.Add(restarts.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--");
            foreach (string a in CommandLine.WithoutAfterCrash(appArgs)) start.ArgumentList.Add(a);
            using Process? watchdog = Process.Start(start);
            AppLog.Write($"Watchdog started (pid {watchdog?.Id})");
        }

        public static int Run(CommandLine options)
        {
            AppLog.Init(Path.Combine(options.EffectiveLogDir, "watchdog.log"));
            try
            {
                return Watch(options);
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

        private static int Watch(CommandLine options)
        {
            int pid = options.WatchdogPid!.Value;
            IntPtr process = NativeMethods.OpenProcess(NativeMethods.SYNCHRONIZE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION |
                                                       PROCESS_QUERY_INFORMATION | PROCESS_TERMINATE, false, (uint)pid);
            if (process == IntPtr.Zero)
            {
                AppLog.Write($"Watchdog: process {pid} is already gone");
                HandBackToExplorer();
                return 0;
            }
            try
            {
                // Guards against the pid having been reused before we opened it.
                if (!NativeMethods.GetProcessTimes(process, out long created, out _, out _, out _) || created != options.WatchdogStartTime)
                {
                    AppLog.Write($"Watchdog: pid {pid} is a different process now");
                    return 1;
                }
                AppLog.Write($"Watchdog watching pid {pid}" + (options.WatchdogRestarts > 0 ? $" (started again after a crash, {options.WatchdogRestarts} in a row)" : ""));
                WaitForExit(process, pid);
                NativeMethods.GetExitCodeProcess(process, out uint exitCode);
                NativeMethods.GetProcessTimes(process, out _, out long exited, out _, out _);
                TimeSpan uptime = TimeSpan.FromTicks(Math.Max(0, exited - created));
                if (exitCode == 0)
                {
                    AppLog.Write($"FreeSystemTray (pid {pid}) exited cleanly");
                    return 0;
                }
                AppLog.Write($"FreeSystemTray (pid {pid}) ended with exit code 0x{exitCode:X8} after {uptime.TotalSeconds:0} s; handing the tray back to Explorer");
                HandBackToExplorer();
                RestartIfCrashed(options, exitCode, uptime);
                return 0;
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }

        /// <summary>Waits for the process to end, and ends it if its tray thread stops answering.</summary>
        private static void WaitForExit(IntPtr process, int pid)
        {
            int unanswered = 0;
            while (NativeMethods.WaitForSingleObject(process, unanswered > 0 ? RetryMs : ProbeMs) == WAIT_TIMEOUT)
            {
                IntPtr tray = TrayWindowOf(pid);
                if (tray == IntPtr.Zero)
                {
                    unanswered = 0; // not hosting (yet): nothing to probe
                    continue;
                }
                bool answered = SendMessageTimeout(tray, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_BLOCK | SMTO_ABORTIFHUNG, ProbeMs, out _) != IntPtr.Zero;
                if (answered || (CheckRemoteDebuggerPresent(process, out bool debugged) && debugged))
                {
                    if (unanswered > 0) AppLog.Write($"FreeSystemTray (pid {pid}) answers again");
                    unanswered = 0;
                    continue;
                }
                unanswered++;
                AppLog.Write($"FreeSystemTray (pid {pid}) did not answer ({unanswered} of {HangProbes})");
                if (unanswered < HangProbes) continue;
                AppLog.Write($"FreeSystemTray (pid {pid}) is hung: apps' icon messages to it fail; ending it");
                TerminateProcess(process, CrashPolicy.HungExitCode);
                NativeMethods.WaitForSingleObject(process, 10_000);
                return;
            }
        }

        /// <summary>The process's Shell_TrayWnd, which its tray thread owns; zero if it has none.</summary>
        private static IntPtr TrayWindowOf(int pid)
        {
            IntPtr h = IntPtr.Zero;
            while ((h = NativeMethods.FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(h, out uint owner);
                if (owner == pid) return h;
            }
            return IntPtr.Zero;
        }

        private static void RestartIfCrashed(CommandLine options, uint exitCode, TimeSpan uptime)
        {
            if (GetSystemMetrics(SM_SHUTTINGDOWN) != 0)
            {
                AppLog.Write("Not starting FreeSystemTray again: Windows is ending the session");
                return;
            }
            if (!CrashPolicy.ShouldRestart(exitCode, uptime, options.WatchdogRestarts, out int next, out string why))
            {
                AppLog.Write($"Not starting FreeSystemTray again: {why}");
                return;
            }
            string settings = SettingsFileIn(options.AppArgs);
            if (!RestartAfterCrashSetting(settings))
            {
                AppLog.Write($"Not starting FreeSystemTray again: \"restartAfterCrash\" is off in {settings}");
                return;
            }
            // Apps are registering with Explorer again after the hand-back's TaskbarCreated; let them.
            Thread.Sleep(1000);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (string a in CommandLine.WithoutAfterCrash(options.AppArgs)) start.ArgumentList.Add(a);
            start.ArgumentList.Add("--after-crash");
            start.ArgumentList.Add(next.ToString(CultureInfo.InvariantCulture));
            using Process? restarted = Process.Start(start);
            AppLog.Write($"Started FreeSystemTray again after the crash (pid {restarted?.Id}; {why})");
        }

        /// <summary>The settings file the app was started with (--settings), else the default.</summary>
        private static string SettingsFileIn(IReadOnlyList<string> args)
        {
            for (int i = 0; i + 1 < args.Count; i++)
            {
                if (string.Equals(args[i], "--settings", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return Settings.SettingsStore.DefaultPath;
        }

        /// <summary>Reads "restartAfterCrash" from the settings file (on unless it says false).</summary>
        private static bool RestartAfterCrashSetting(string path)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return !(doc.RootElement.ValueKind == JsonValueKind.Object &&
                         doc.RootElement.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, "restartAfterCrash", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.False });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return true;
            }
        }

        private static void HandBackToExplorer() => AppLog.Write("Hand-back: " + TrayHandBack.AfterHostDied());

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CheckRemoteDebuggerPresent(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool present);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
    }
}
