using System;
using System.Runtime.CompilerServices;
using System.Windows;

namespace FreeSystemTray
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            CommandLine options;
            try
            {
                options = CommandLine.Parse(args);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                ShowError(ex.Message);
                return 2;
            }

            if (options.WatchdogPid != null)
                return Watchdog.Run(options);
            return RunApp(options);
        }

        // WPF is only touched in these two methods, so the watchdog process never loads it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunApp(CommandLine options)
        {
            var app = new App(options);
            app.InitializeComponent();
            return app.Run();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ShowError(string message) =>
            MessageBox.Show(message, "FreeSystemTray", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
