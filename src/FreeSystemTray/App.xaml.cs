using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using FreeSystemTray.Diagnostics;
using FreeSystemTray.Logging;
using FreeSystemTray.Settings;
using FreeSystemTray.ShowHide;
using FreeSystemTray.Theming;
using FreeSystemTray.TrayHost;
using ManagedShell.Common.Logging;
using Microsoft.Win32;

namespace FreeSystemTray
{
    public partial class App : Application
    {
        private const string SingleInstanceName = "FreeSystemTray.SingleInstance";

        /// <summary>--debug-crash (wParam 1) and --debug-hang (wParam 2, lParam seconds), for an instance started with --diag.</summary>
        private static readonly uint DebugMessage = NativeMethods.RegisterWindowMessage("FreeSystemTray.Debug");

        private readonly CommandLine _options;
        private Mutex? _singleInstance;
        private ManagedShellTrayHost? _host;
        private ShowHideController? _showHide;
        private SettingsWatcher? _settingsWatcher;
        private ThemeWatcher? _themeWatcher;
        private StateWriter? _stateWriter;
        private AppNotifyIcon? _appIcon;
        private SettingsWindow? _settingsWindow;
        private readonly Autostart _autostart = new();

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
            if (_options.ImportTheme != null || _options.ExportTheme != null)
            {
                Shutdown(RunThemeCommand() ? 0 : 1);
                return;
            }
            if (_options.DebugCrash || _options.DebugHangSeconds != null)
            {
                Shutdown(SendDebugCommand() ? 0 : 1);
                return;
            }

            _singleInstance = new Mutex(true, SingleInstanceName, out bool created);
            if (!created)
            {
                _singleInstance.Dispose();
                _singleInstance = null;
                // Started again by hand: bring up the running one, with its settings. At sign-in
                // (an instance started by hand before the Run key's), just exit.
                if (_options.Autostart)
                {
                    AppLog.Write("Another instance is already running; exiting");
                    Shutdown(1);
                    return;
                }
                AppLog.Write("Another instance is already running; showing its settings");
                Shutdown(SendToRunningInstance(ShowHideCommand.Settings) ? 0 : 1);
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
            TaskScheduler.UnobservedTaskException += (_, ex) => AppLog.Write("Unobserved exception in a background task: " + ex.Exception);
            // WPF shuts the app down when the session ends (sign-out, shutdown); OnExit hands the tray back.
            SessionEnding += (_, ex) => AppLog.Write($"Windows is ending the session ({ex.ReasonSessionEnding}); exiting");
            if (_options.AfterCrash > 0) AppLog.Write($"Started again by the watchdog after a crash ({_options.AfterCrash} in a row)");

            ShellLogger.Severity = _options.Verbose ? LogSeverity.Debug : LogSeverity.Info;
            ShellLogger.Log += (_, le) =>
                AppLog.Write($"[ManagedShell {le.SeverityString}] {le.Message}{(le.Exception != null ? " :: " + le.Exception : "")}");

            var store = new SettingsStore(_options.SettingsPath ?? SettingsStore.DefaultPath);
            AppSettings settings = store.Load(AppLog.Write);
            AppLog.Write($"Settings file: {store.FilePath}");
            if (_options.Autostart) WaitForExplorersTaskbar();

            _host = new ManagedShellTrayHost(new TrayHostOptions
            {
                MirrorToExplorer = !_options.NoMirror,
                KeepExplorerTaskbarTopmost = !_options.NoTopmostFix,
                ForwardAutoHideQueries = !_options.NoAutoHideForward,
                RoutePositionQueries = !_options.NoQueryRouting,
                ImportWithoutToggling = !_options.NoOwnImport,
                KeepHostRole = !_options.NoRoleKeeper,
                SyncWithExplorer = !_options.NoExplorerSync,
                TraceMessages = _options.Verbose,
                Log = AppLog.Write
            });
            var themes = new ThemeStore(ThemeStore.UserDirectoryFor(store.FilePath), ThemeStore.DefaultBuiltInDirectory);
            var window = new TrayWindow(_host, settings, store, themes);
            MainWindow = window;
            _showHide = new ShowHideController(window, _host, settings.ShowHide, AppLog.Write, _options.Verbose);
            window.ShowHide = _showHide;
            _showHide.Start(); // shows the window, unless auto-hide keeps it hidden
            _settingsWatcher = new SettingsWatcher(store, Dispatcher, window.ApplySettings, AppLog.Write);
            _themeWatcher = new ThemeWatcher(themes.UserDirectory, Dispatcher, window.LoadTheme, AppLog.Write);

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

            // After the host started: our own icon goes to our tray, and is mirrored to Explorer's.
            window.OpenSettings = OpenSettings;
            _appIcon = new AppNotifyIcon(() => OpenSettings(null), () => window.ShowAppMenu(null));
            if (settings.AppIcon) _appIcon.Show();
            window.SettingsApplied += (_, _) =>
            {
                if (window.Settings.AppIcon) _appIcon.Show();
                else _appIcon.Hide();
            };

            if (_options.DiagDir != null)
            {
                _stateWriter = new StateWriter(_host, window, _options.DiagDir, () => _appIcon.IsShown, () => _settingsWindow);
                HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())?.AddHook(DebugHook);
            }
            if (!_options.NoWatchdog) Watchdog.Launch(logDir, _options.Args, _options.AfterCrash);
        }

        /// <summary>Opens the settings window, or brings it to the front; at an icon if <paramref name="iconKey"/> is given.</summary>
        private void OpenSettings(string? iconKey)
        {
            if (MainWindow is not TrayWindow tray) return;
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(tray, _autostart);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
                AppLog.Write("Settings window opened");
            }
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            if (iconKey != null) _settingsWindow.SelectIcon(iconKey);
        }

        /// <summary>
        /// At sign-in the Run key can start the app before Explorer's taskbar exists; the tray host
        /// role and the icon import need it. Waits up to a minute.
        /// </summary>
        private static void WaitForExplorersTaskbar()
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (NativeMethods.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero && waited.Elapsed < TimeSpan.FromMinutes(1))
                Thread.Sleep(250);
            AppLog.Write(waited.ElapsedMilliseconds < 250 ? "Started at sign-in; Explorer's taskbar is there"
                : $"Started at sign-in; waited {waited.Elapsed.TotalSeconds:0.#} s for Explorer's taskbar" +
                  (NativeMethods.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero ? ", which is still missing" : ""));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _stateWriter?.Dispose();
            // Before the host goes: the delete is mirrored to Explorer's tray, so no dead icon stays there.
            _appIcon?.Dispose();
            _settingsWatcher?.Dispose();
            _themeWatcher?.Dispose();
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

        /// <summary>
        /// --import-theme copies the file into the themes folder and selects it in the settings (a
        /// running instance applies it at once); --export-theme writes the current theme. The outcome
        /// is in the log and the exit code.
        /// </summary>
        private bool RunThemeCommand()
        {
            var store = new SettingsStore(_options.SettingsPath ?? SettingsStore.DefaultPath);
            var themes = new ThemeStore(ThemeStore.UserDirectoryFor(store.FilePath), ThemeStore.DefaultBuiltInDirectory);
            AppSettings settings = store.Load(AppLog.Write);
            var problems = new List<string>();
            try
            {
                if (_options.ImportTheme is string import)
                {
                    if (!themes.Import(import, out string id, problems))
                    {
                        AppLog.Write($"--import-theme {import}: not imported: {string.Join("; ", problems)}");
                        return false;
                    }
                    themes.Load(id, problems);
                    settings.Theme = id;
                    store.Save(settings);
                    AppLog.Write($"--import-theme: '{id}' copied to {themes.UserDirectory} and selected" +
                                 (problems.Count > 0 ? $"; problems: {string.Join("; ", problems)}" : ""));
                    return true;
                }
                string target = _options.ExportTheme!;
                Theme theme = themes.Load(settings.Theme, problems);
                ThemeStore.Export(theme, target);
                AppLog.Write($"--export-theme: theme '{theme.Id}' written to {Path.GetFullPath(target)}" +
                             (problems.Count > 0 ? $"; problems: {string.Join("; ", problems)}" : ""));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                AppLog.Write($"Theme command failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>--debug-crash, --debug-hang: posted to the running instance, which acts on them only with --diag.</summary>
        private bool SendDebugCommand()
        {
            IntPtr window = TrayWindow.FindRunningInstance();
            bool sent = window != IntPtr.Zero && NativeMethods.PostMessage(window, (int)DebugMessage,
                (IntPtr)(_options.DebugCrash ? 1 : 2), (IntPtr)(_options.DebugHangSeconds ?? 0));
            AppLog.Write($"{(_options.DebugCrash ? "--debug-crash" : "--debug-hang")}: {(sent ? "sent" : "could not send")} to the running instance");
            return sent;
        }

        /// <summary>Test commands for the watchdog: crash with an unhandled exception, or stop answering.</summary>
        private IntPtr DebugHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != DebugMessage || DebugMessage == 0) return IntPtr.Zero;
            handled = true;
            if (wParam.ToInt32() == 1)
            {
                AppLog.Write("--debug-crash: crashing on purpose");
                Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("--debug-crash: a crash to test the watchdog")));
            }
            else
            {
                int seconds = lParam.ToInt32();
                AppLog.Write($"--debug-hang: not answering for {seconds} s");
                Dispatcher.BeginInvoke(() => Thread.Sleep(TimeSpan.FromSeconds(seconds)));
            }
            return IntPtr.Zero;
        }

        /// <summary>--toggle, --show, --hide, --open-settings: posted to the running instance's tray window.</summary>
        private static bool SendToRunningInstance(ShowHideCommand command)
        {
            IntPtr window = TrayWindow.FindRunningInstance();
            if (window == IntPtr.Zero)
            {
                AppLog.Write($"{command}: no running instance");
                return false;
            }
            bool sent = NativeMethods.PostMessage(window, (int)ShowHideController.CommandMessage, (IntPtr)(int)command, IntPtr.Zero);
            AppLog.Write($"{command}: {(sent ? "sent" : "could not send")} to the running instance");
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
