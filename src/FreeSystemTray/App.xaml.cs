using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using FreeSystemTray.Diagnostics;
using FreeSystemTray.Logging;
using FreeSystemTray.Settings;
using FreeSystemTray.ShowHide;
using FreeSystemTray.TrayHost;
using ManagedShell.Common.Logging;
using Microsoft.Win32;

namespace FreeSystemTray
{
    public partial class App : Application
    {
        private const string SingleInstanceName = "FreeSystemTray.SingleInstance";

        private readonly CommandLine _options;
        private Mutex? _singleInstance;
        private ManagedShellTrayHost? _host;
        private ShowHideController? _showHide;
        private SettingsWatcher? _settingsWatcher;
        private StateWriter? _stateWriter;

        public App(CommandLine options)
        {
            _options = options;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string logDir = _options.EffectiveLogDir;
            AppLog.Init(Path.Combine(logDir, "FreeSystemTray.log"));
            AppLog.Write($"FreeSystemTray {Version} starting (pid {Environment.ProcessId}). Args: {string.Join(" ", e.Args)}");
            AppLog.Write($"OS: {WindowsVersion()}");

            if (_options.Exit)
            {
                Shutdown(ExitRunningInstance() ? 0 : 1);
                return;
            }
            if (_options.ShowHideCommand is ShowHideCommand command)
            {
                Shutdown(SendToRunningInstance(command) ? 0 : 1);
                return;
            }

            _singleInstance = new Mutex(true, SingleInstanceName, out bool created);
            if (!created)
            {
                AppLog.Write("Another instance is already running; exiting");
                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown(1);
                return;
            }

            DispatcherUnhandledException += (_, ex) =>
            {
                AppLog.Write("Unhandled exception on the UI thread: " + ex.Exception);
                AppLog.Flush(TimeSpan.FromSeconds(2));
            };
            AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            {
                AppLog.Write("Unhandled exception: " + ex.ExceptionObject);
                AppLog.Flush(TimeSpan.FromSeconds(2));
            };

            ShellLogger.Severity = _options.Verbose ? LogSeverity.Debug : LogSeverity.Info;
            ShellLogger.Log += (_, le) =>
                AppLog.Write($"[ManagedShell {le.SeverityString}] {le.Message}{(le.Exception != null ? " :: " + le.Exception : "")}");

            var store = new SettingsStore(_options.SettingsPath ?? SettingsStore.DefaultPath);
            AppSettings settings = store.Load(AppLog.Write);
            AppLog.Write($"Settings file: {store.FilePath}");

            _host = new ManagedShellTrayHost(new TrayHostOptions
            {
                MirrorToExplorer = !_options.NoMirror,
                KeepExplorerTaskbarTopmost = !_options.NoTopmostFix,
                ForwardAutoHideQueries = !_options.NoAutoHideForward,
                RoutePositionQueries = !_options.NoQueryRouting,
                TraceMessages = _options.Verbose,
                Log = AppLog.Write
            });
            var window = new TrayWindow(_host, settings, store);
            MainWindow = window;
            _showHide = new ShowHideController(window, _host, settings.ShowHide, AppLog.Write, _options.Verbose);
            window.ShowHide = _showHide;
            _showHide.Start(); // shows the window, unless auto-hide keeps it hidden
            _settingsWatcher = new SettingsWatcher(store, Dispatcher, window.ApplySettings, AppLog.Write);

            try
            {
                _host.Start();
            }
            catch (Exception ex)
            {
                AppLog.Write("Could not take the tray host role: " + ex);
                MessageBox.Show("FreeSystemTray could not take over the notification area:\n\n" + ex.Message,
                    "FreeSystemTray", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(3);
                return;
            }

            if (_options.DiagDir != null) _stateWriter = new StateWriter(_host, window, _options.DiagDir);
            if (!_options.NoWatchdog) Watchdog.Launch(logDir);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _stateWriter?.Dispose();
            _settingsWatcher?.Dispose();
            _showHide?.Dispose();
            if (_host != null)
            {
                AppLog.Write("Exiting: handing the tray back to Explorer");
                try
                {
                    _host.Dispose();
                }
                catch (Exception ex)
                {
                    AppLog.Write("Stopping the tray host failed: " + ex);
                }
            }
            AppLog.Write($"Exited (code {e.ApplicationExitCode})");
            AppLog.Flush(TimeSpan.FromSeconds(2));
            if (_singleInstance != null)
            {
                _singleInstance.ReleaseMutex();
                _singleInstance.Dispose();
            }
            base.OnExit(e);
        }

        /// <summary>--exit: closes the running instance the same way its Exit menu item does, and waits for it.</summary>
        private static bool ExitRunningInstance()
        {
            IntPtr window = TrayWindow.FindRunningInstance();
            if (window == IntPtr.Zero)
            {
                AppLog.Write("--exit: no running instance");
                return false;
            }
            NativeMethods.GetWindowThreadProcessId(window, out uint pid);
            IntPtr process = NativeMethods.OpenProcess(NativeMethods.SYNCHRONIZE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            try
            {
                NativeMethods.PostMessage(window, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (process == IntPtr.Zero)
                {
                    AppLog.Write($"--exit: asked pid {pid} to close; cannot wait for it");
                    return true;
                }
                bool exited = NativeMethods.WaitForSingleObject(process, 10_000) == 0;
                uint exitCode = 0;
                if (exited) NativeMethods.GetExitCodeProcess(process, out exitCode);
                AppLog.Write($"--exit: asked pid {pid} to close; {(exited ? $"it exited with code {exitCode}" : "it did not exit within 10 s")}");
                return exited && exitCode == 0;
            }
            finally
            {
                if (process != IntPtr.Zero) NativeMethods.CloseHandle(process);
            }
        }

        /// <summary>--toggle, --show, --hide: posted to the running instance's tray window.</summary>
        private static bool SendToRunningInstance(ShowHideCommand command)
        {
            IntPtr window = TrayWindow.FindRunningInstance();
            if (window == IntPtr.Zero)
            {
                AppLog.Write($"--{command.ToString().ToLowerInvariant()}: no running instance");
                return false;
            }
            bool sent = NativeMethods.PostMessage(window, (int)ShowHideController.CommandMessage, (IntPtr)(int)command, IntPtr.Zero);
            AppLog.Write($"--{command.ToString().ToLowerInvariant()}: {(sent ? "sent" : "could not send")} to the running instance");
            return sent;
        }

        private static string Version =>
            typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

        private static string WindowsVersion()
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return $"{key?.GetValue("ProductName")} {key?.GetValue("DisplayVersion")} build {key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")}";
        }
    }
}
