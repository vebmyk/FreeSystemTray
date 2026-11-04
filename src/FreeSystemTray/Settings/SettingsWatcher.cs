using System;
using System.IO;
using System.Windows.Threading;

namespace FreeSystemTray.Settings
{
    /// <summary>
    /// Reloads the settings file when it changes on disk, so edits apply without a restart. Our own
    /// saves are recognised by their content and ignored. Changes are applied on the UI thread, after
    /// a short pause so an editor's several writes count as one.
    /// </summary>
    internal sealed class SettingsWatcher : IDisposable
    {
        private readonly SettingsStore _store;
        private readonly Action<AppSettings> _apply;
        private readonly Action<string> _log;
        private readonly FileSystemWatcher? _watcher;
        private readonly DispatcherTimer _debounce;

        public SettingsWatcher(SettingsStore store, Dispatcher dispatcher, Action<AppSettings> apply, Action<string> log)
        {
            _store = store;
            _apply = apply;
            _log = log;
            _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
            _debounce.Tick += (_, _) => Reload();

            string full = Path.GetFullPath(store.FilePath);
            string dir = Path.GetDirectoryName(full)!;
            try
            {
                Directory.CreateDirectory(dir);
                _watcher = new FileSystemWatcher(dir, Path.GetFileName(full))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                // Raised on a thread-pool thread.
                FileSystemEventHandler changed = (_, _) => dispatcher.BeginInvoke(DispatcherPriority.Background, Restart);
                _watcher.Changed += changed;
                _watcher.Created += changed;
                _watcher.Renamed += (_, _) => dispatcher.BeginInvoke(DispatcherPriority.Background, Restart);
                // Windows dropped notifications (its buffer overflowed, or the folder became unreachable):
                // a change may have been missed, so say so and read the file again.
                _watcher.Error += (_, e) => dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _log($"Watching {full} for changes failed ({e.GetException().Message}); reading it again");
                    Restart();
                });
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _log($"Cannot watch {full} for changes ({ex.Message}); edits apply after a restart");
            }
        }

        private void Restart()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void Reload()
        {
            _debounce.Stop();
            AppSettings? settings = _store.Reload(out string? error);
            if (error != null)
            {
                _log($"Settings file changed but could not be read ({error}); keeping the current settings");
                return;
            }
            if (settings == null) return; // our own save
            _log("Settings file changed; applying it");
            _apply(settings);
        }

        public void Dispose()
        {
            _debounce.Stop();
            _watcher?.Dispose();
        }
    }
}
