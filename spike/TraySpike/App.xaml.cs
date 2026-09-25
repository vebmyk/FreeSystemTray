using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using ManagedShell.Common.Logging;
using TraySpike.Tray;

namespace TraySpike
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private ManagedShellTrayHost? _host;

        public static SpikeOptions Options { get; private set; } = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Options = SpikeOptions.Parse(e.Args);
            Directory.CreateDirectory(Options.LogDir);
            Log.Init(Path.Combine(Options.LogDir, "spike.log"));
            Log.Write($"TraySpike starting. Args: {string.Join(" ", e.Args)}");
            Log.Write($"OS: {Environment.OSVersion} ({SysInfo.WindowsBuildString()})");

            _singleInstance = new Mutex(true, "TraySpike.SingleInstance", out bool created);
            if (!created)
            {
                Log.Write("Another instance is running; exiting.");
                Shutdown(1);
                return;
            }

            ShellLogger.Severity = LogSeverity.Debug;
            int stacksLogged = 0;
            ShellLogger.Log += (_, le) =>
            {
                string reentrant = ManagedShellTrayHost.HandlerDepth > 0 ? "[REENTRANT inside our CollectionChanged handler] " : "";
                Log.Write($"[MS {le.SeverityString}] {reentrant}{le.Message}{(le.Exception != null ? " :: " + le.Exception : "")}");
                if (reentrant.Length > 0 && stacksLogged++ < 2)
                    Log.Write("REENTRANCY STACK:" + Environment.NewLine + Environment.StackTrace);
            };
            ManagedShellTrayHost.DeferSync = Options.DeferSync;

            DispatcherUnhandledException += (_, ex) =>
            {
                Log.Write("UNHANDLED: " + ex.Exception);
            };

            _host = new ManagedShellTrayHost
            {
                MirrorToExplorer = Options.Mirror,
                KeepExplorerTopmost = Options.FixTopmost
            };
            var window = new MainWindow(_host);
            MainWindow = window;
            window.Show();

            _host.Start();
            Log.Write($"Tray host started. Our Shell_TrayWnd = 0x{_host.TrayWindowHandle.ToInt64():X}");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Write("Exiting: disposing tray host (destroys our Shell_TrayWnd, broadcasts TaskbarCreated).");
            try
            {
                _host?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Write("Dispose failed: " + ex);
            }
            Log.Write("Exited.");
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }

    public sealed class SpikeOptions
    {
        public string LogDir { get; private set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraySpike");
        public int? X { get; private set; }
        public int? Y { get; private set; }
        public double IconSize { get; private set; } = 16;
        public bool Activating { get; private set; }
        public bool DeferSync { get; private set; }
        public bool Mirror { get; private set; }
        public bool FixTopmost { get; private set; }

        public static SpikeOptions Parse(string[] args)
        {
            var o = new SpikeOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value for " + args[i]);
                switch (args[i].ToLowerInvariant())
                {
                    case "--log": o.LogDir = next(); break;
                    case "--x": o.X = int.Parse(next(), CultureInfo.InvariantCulture); break;
                    case "--y": o.Y = int.Parse(next(), CultureInfo.InvariantCulture); break;
                    case "--icon-size": o.IconSize = double.Parse(next(), CultureInfo.InvariantCulture); break;
                    case "--activating": o.Activating = true; break;
                    case "--defer-sync": o.DeferSync = true; break;
                    case "--mirror": o.Mirror = true; break;
                    case "--fix-topmost": o.FixTopmost = true; break;
                }
            }
            return o;
        }
    }
}
