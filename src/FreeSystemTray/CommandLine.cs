using System;
using System.Collections.Generic;
using System.Globalization;

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
    ///   --no-watchdog     do not start the watchdog process
    ///   --no-mirror, --no-topmost-fix, --no-autohide-forward, --no-query-routing
    ///                     turn off one workaround (ManagedShell as shipped), for comparison tests
    ///   --watchdog PID START_TIME   internal: run as the watchdog of process PID
    /// </code>
    /// </summary>
    public sealed class CommandLine
    {
        public string? SettingsPath { get; private set; }
        public string? LogDir { get; private set; }
        public string? DiagDir { get; private set; }
        public bool Verbose { get; private set; }
        public bool Exit { get; private set; }
        public bool NoWatchdog { get; private set; }
        public bool NoMirror { get; private set; }
        public bool NoTopmostFix { get; private set; }
        public bool NoAutoHideForward { get; private set; }
        public bool NoQueryRouting { get; private set; }
        public int? WatchdogPid { get; private set; }
        public long WatchdogStartTime { get; private set; }

        /// <exception cref="ArgumentException">Unknown option or missing value.</exception>
        public static CommandLine Parse(IReadOnlyList<string> args)
        {
            var o = new CommandLine();
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
                    case "--no-watchdog": o.NoWatchdog = true; break;
                    case "--no-mirror": o.NoMirror = true; break;
                    case "--no-topmost-fix": o.NoTopmostFix = true; break;
                    case "--no-autohide-forward": o.NoAutoHideForward = true; break;
                    case "--no-query-routing": o.NoQueryRouting = true; break;
                    case "--watchdog":
                        o.WatchdogPid = int.Parse(Next(), CultureInfo.InvariantCulture);
                        o.WatchdogStartTime = long.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    default: throw new ArgumentException($"Unknown option {option}");
                }
            }
            return o;
        }

        /// <summary>Where logs go: --diag, then --log-dir, then the default.</summary>
        public string EffectiveLogDir => DiagDir ?? LogDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeSystemTray", "logs");
    }
}
