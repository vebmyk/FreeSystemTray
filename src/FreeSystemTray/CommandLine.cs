using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FreeSystemTray.ShowHide;

namespace FreeSystemTray
{
    /// <summary>
    /// Command-line options. Everything except the watchdog arguments is optional:
    /// <code>
    ///   --settings FILE   settings file (default %LOCALAPPDATA%\FreeSystemTray\settings.json)
    ///   --log-dir DIR     log directory (default %LOCALAPPDATA%\FreeSystemTray\logs)
    ///   --verbose         log every tray message and position query, and ManagedShell's debug output
    ///   --diag DIR        write state.json (icons, their screen rectangles, host status) for the smoke
    ///                     test into DIR; implies --log-dir DIR and --verbose
    ///   --exit            close the running instance and wait for it to exit
    ///   --toggle, --show, --hide   show or hide the running instance's window
    ///   --open-settings   show the running instance's tray and its settings window (what starting the
    ///                     exe again does too)
    ///   --autostart       started at sign-in: wait for Explorer's taskbar; if an instance is running
    ///                     already, exit quietly
    ///   --import-theme FILE   copy a theme file into the themes folder and switch to it
    ///   --export-theme FILE   write the current theme, every value spelled out, to FILE
    ///   --no-watchdog     do not start the watchdog process
    ///   --after-crash N   internal: started again by the watchdog after a crash, the Nth time in a row
    ///   --debug-crash, --debug-hang SECONDS
    ///                     make the running instance crash, or stop answering for SECONDS, to test the
    ///                     watchdog; only an instance started with --diag does
    ///   --no-mirror, --no-topmost-fix, --no-autohide-forward, --no-query-routing, --no-own-import,
    ///   --no-role-keeper, --no-explorer-sync
    ///                     turn off one workaround (ManagedShell as shipped), for comparison tests
    ///   --watchdog PID START_TIME [--restarts N] -- APP ARGS
    ///                     internal: run as the watchdog of process PID, which was started with APP ARGS
    /// </code>
    /// </summary>
    public sealed class CommandLine
    {
        /// <summary>The arguments as given.</summary>
        public IReadOnlyList<string> Args { get; private set; } = Array.Empty<string>();

        public string? SettingsPath { get; private set; }
        public string? LogDir { get; private set; }
        public string? DiagDir { get; private set; }
        public bool Verbose { get; private set; }
        public bool Exit { get; private set; }

        /// <summary>--toggle, --show or --hide: sent to the running instance.</summary>
        public ShowHideCommand? ShowHideCommand { get; private set; }

        /// <summary>Started from the Run key at sign-in.</summary>
        public bool Autostart { get; private set; }

        public string? ImportTheme { get; private set; }
        public string? ExportTheme { get; private set; }

        public bool NoWatchdog { get; private set; }
        public bool NoMirror { get; private set; }
        public bool NoTopmostFix { get; private set; }
        public bool NoAutoHideForward { get; private set; }
        public bool NoQueryRouting { get; private set; }
        public bool NoOwnImport { get; private set; }
        public bool NoRoleKeeper { get; private set; }
        public bool NoExplorerSync { get; private set; }
        public int? WatchdogPid { get; private set; }
        public long WatchdogStartTime { get; private set; }

        /// <summary>The watchdog: how often in a row its process was started again after a crash.</summary>
        public int WatchdogRestarts { get; private set; }

        /// <summary>The watchdog: its process's arguments (after <c>--</c>), to start it again with.</summary>
        public IReadOnlyList<string> AppArgs { get; private set; } = Array.Empty<string>();

        /// <summary>Started again by the watchdog after a crash: the Nth time in a row (0: not).</summary>
        public int AfterCrash { get; private set; }

        public bool DebugCrash { get; private set; }
        public int? DebugHangSeconds { get; private set; }

        /// <exception cref="ArgumentException">Unknown option or missing value.</exception>
        public static CommandLine Parse(IReadOnlyList<string> args)
        {
            var o = new CommandLine { Args = args.ToArray() };
            for (int i = 0; i < args.Count; i++)
            {
                string option = args[i];
                string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"Missing value for {option}");
                switch (option.ToLowerInvariant())
                {
                    case "--settings": o.SettingsPath = Next(); break;
                    case "--log-dir": o.LogDir = Next(); break;
                    case "--diag": o.DiagDir = Next(); o.Verbose = true; break;
                    case "--verbose": o.Verbose = true; break;
                    case "--exit": o.Exit = true; break;
                    case "--toggle": o.ShowHideCommand = ShowHide.ShowHideCommand.Toggle; break;
                    case "--show": o.ShowHideCommand = ShowHide.ShowHideCommand.Show; break;
                    case "--hide": o.ShowHideCommand = ShowHide.ShowHideCommand.Hide; break;
                    case "--open-settings": o.ShowHideCommand = ShowHide.ShowHideCommand.Settings; break;
                    case "--autostart": o.Autostart = true; break;
                    case "--import-theme": o.ImportTheme = Next(); break;
                    case "--export-theme": o.ExportTheme = Next(); break;
                    case "--no-watchdog": o.NoWatchdog = true; break;
                    case "--no-mirror": o.NoMirror = true; break;
                    case "--no-topmost-fix": o.NoTopmostFix = true; break;
                    case "--no-autohide-forward": o.NoAutoHideForward = true; break;
                    case "--no-query-routing": o.NoQueryRouting = true; break;
                    case "--no-own-import": o.NoOwnImport = true; break;
                    case "--no-role-keeper": o.NoRoleKeeper = true; break;
                    case "--no-explorer-sync": o.NoExplorerSync = true; break;
                    case "--after-crash": o.AfterCrash = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--debug-crash": o.DebugCrash = true; break;
                    case "--debug-hang": o.DebugHangSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--watchdog":
                        o.WatchdogPid = int.Parse(Next(), CultureInfo.InvariantCulture);
                        o.WatchdogStartTime = long.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--restarts": o.WatchdogRestarts = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--":
                        o.AppArgs = args.Skip(i + 1).ToArray();
                        i = args.Count;
                        break;
                    default: throw new ArgumentException($"Unknown option {option}");
                }
            }
            return o;
        }

        /// <summary><paramref name="args"/> without any <c>--after-crash N</c>.</summary>
        public static List<string> WithoutAfterCrash(IReadOnlyList<string> args)
        {
            var result = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                if (string.Equals(args[i], "--after-crash", StringComparison.OrdinalIgnoreCase)) i++;
                else result.Add(args[i]);
            }
            return result;
        }

        /// <summary>Where logs go: --diag, then --log-dir, then the default.</summary>
        public string EffectiveLogDir => DiagDir ?? LogDir ?? System.IO.Path.Combine(AppPaths.DataDirectory, "logs");
    }
}
