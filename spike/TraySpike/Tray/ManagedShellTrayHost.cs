using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;

namespace TraySpike.Tray
{
    /// <summary>ITrayHost on top of ManagedShell's NotificationArea (tray service only).</summary>
    public sealed class ManagedShellTrayHost : ITrayHost
    {
        private readonly ShellManager _shell;
        private readonly ObservableCollection<ITrayIcon> _icons = new();
        private bool _started;

        public ManagedShellTrayHost()
        {
            // Explorer stays the shell; we only take the tray host role.
            EnvironmentHelper.IsAppRunningAsShell = NativeMethods.GetShellWindow() == IntPtr.Zero;

            var config = ShellManager.DefaultShellConfig;
            config.EnableTasksService = false;
            config.AutoStartTasksService = false;
            config.EnableTrayService = true;
            config.AutoStartTrayService = false;
            config.PinnedNotifyIcons = Array.Empty<string>();

            _shell = new ShellManager(config);
            Icons = new ReadOnlyObservableCollection<ITrayIcon>(_icons);
        }

        public ReadOnlyObservableCollection<ITrayIcon> Icons { get; }

        public bool MirrorToExplorer { get; set; }
        public bool KeepExplorerTopmost { get; set; }

        public event EventHandler<TrayBalloonEventArgs>? BalloonReceived;

        /// <summary>Handle of our Shell_TrayWnd (diagnostics).</summary>
        public IntPtr TrayWindowHandle => _shell.NotificationArea.Handle;

        public void Start()
        {
            if (_started) return;
            _started = true;

            NotificationArea area = _shell.NotificationArea;
            area.TrayIcons.CollectionChanged += TrayIcons_CollectionChanged;
            area.NotificationBalloonShown += Area_NotificationBalloonShown;
            area.Initialize();

            if (area.IsFailed)
                throw new InvalidOperationException("ManagedShell NotificationArea failed to initialize.");

            // Install before the TaskbarCreated responses (sent messages) are dispatched to us.
            if (MirrorToExplorer) EnableMirrorToExplorer();
            if (KeepExplorerTopmost) RestoreExplorerTaskbarTopmost();

            // Icons imported from Explorer during Initialize() were added before we could observe them.
            SyncAll();
        }

        #region Experiments: mirror icon messages to Explorer, keep Explorer's taskbar topmost

        private const int WM_COPYDATA = 0x004A;
        private const int GWLP_WNDPROC = -4;
        private delegate IntPtr WndProcHook(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private WndProcHook? _mirrorHook;
        private IntPtr _originalWndProc;

        /// <summary>
        /// ManagedShell returns without forwarding every Shell_NotifyIcon message it handles, so Explorer's
        /// tray goes stale. Subclass our Shell_TrayWnd and send each icon message (WM_COPYDATA, dwData=1)
        /// on to Explorer's Shell_TrayWnd as well. Messages ManagedShell already forwarded arrive twice;
        /// NIM_* operations are idempotent enough for that to be harmless.
        /// </summary>
        public void EnableMirrorToExplorer()
        {
            if (_mirrorHook != null) return;
            _mirrorHook = MirrorHook;
            _originalWndProc = SetWindowLongPtr(TrayWindowHandle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_mirrorHook));
            TraySpike.Log.Write($"Mirroring icon messages to Explorer enabled (orig wndproc 0x{_originalWndProc.ToInt64():X})");
        }

        private IntPtr MirrorHook(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            IntPtr result = CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
            if (msg == WM_COPYDATA && lParam != IntPtr.Zero)
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                if (cds.dwData == (IntPtr)1)
                {
                    IntPtr explorer = TraySpike.ShellState.FindOtherTray(hWnd);
                    if (explorer != IntPtr.Zero)
                    {
                        IntPtr ok = SendMessageTimeout(explorer, WM_COPYDATA, wParam, lParam, 0x0002 /*SMTO_ABORTIFHUNG*/, 1000, out IntPtr exResult);
                        if (ok == IntPtr.Zero) TraySpike.Log.Write("MIRROR: SendMessageTimeout to Explorer failed/timed out");
                        // SHELLTRAYDATA: dwSignature, dwMessage, then NOTIFYICONDATA with 32-bit handles.
                        int nim = Marshal.ReadInt32(cds.lpData, 4);
                        uint hwnd32 = (uint)Marshal.ReadInt32(cds.lpData, 12);
                        uint uid = (uint)Marshal.ReadInt32(cds.lpData, 16);
                        uint nif = (uint)Marshal.ReadInt32(cds.lpData, 20);
                        TraySpike.Log.Write($"MIRROR NIM{nim} hwnd=0x{hwnd32:X} uid={uid} nif=0x{nif:X} ours={result} explorer={exResult}");
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// TrayService.Resume() pushes Explorer's taskbar to HWND_BOTTOM, which drops its topmost flag.
        /// Put it back, then re-raise our (invisible) Shell_TrayWnd above it so FindWindow still finds ours.
        /// </summary>
        public void RestoreExplorerTaskbarTopmost()
        {
            IntPtr explorer = TraySpike.ShellState.FindOtherTray(TrayWindowHandle);
            if (explorer == IntPtr.Zero) return;
            const uint flags = 0x0001 | 0x0002 | 0x0010; // NOSIZE | NOMOVE | NOACTIVATE
            SetWindowPos(explorer, new IntPtr(-1), 0, 0, 0, 0, flags);
            SetWindowPos(TrayWindowHandle, new IntPtr(-1), 0, 0, 0, 0, flags);
            TraySpike.Log.Write("Restored Explorer taskbar topmost and re-raised our Shell_TrayWnd");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        #endregion

        public void SetHostBounds(Rect screenPx, ScreenEdge edge)
        {
            _shell.NotificationArea.SetTrayHostSizeData(new TrayHostSizeData
            {
                edge = (NativeMethods.ABEdge)(int)edge,
                rc = new NativeMethods.Rect((int)screenPx.Left, (int)screenPx.Top, (int)screenPx.Right, (int)screenPx.Bottom)
            });
        }

        private void Area_NotificationBalloonShown(object? sender, NotificationBalloonEventArgs e)
        {
            ITrayIcon? icon = _icons.OfType<ManagedShellTrayIcon>().FirstOrDefault(i => ReferenceEquals(i.Source, e.Balloon.NotifyIcon));
            BalloonReceived?.Invoke(this, new TrayBalloonEventArgs(icon, e.Balloon.Title ?? "", e.Balloon.Info ?? ""));
        }

        /// <summary>
        /// When true, our UI collection is updated after ManagedShell's CollectionChanged has returned
        /// (Dispatcher.BeginInvoke) instead of inside it. Tray messages arrive as sent WM_COPYDATA, so
        /// anything that pumps messages inside the handler re-enters ManagedShell's WndProc.
        /// </summary>
        public static bool DeferSync { get; set; }

        /// <summary>Non-zero while our CollectionChanged handler runs (diagnostics for re-entrancy).</summary>
        public static int HandlerDepth { get; private set; }

        private bool _syncQueued;

        private void TrayIcons_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (DeferSync)
            {
                if (_syncQueued) return;
                _syncQueued = true;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                {
                    _syncQueued = false;
                    SyncAll();
                });
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            HandlerDepth++;
            try
            {
                ApplyChange(e);
            }
            finally
            {
                HandlerDepth--;
                if (sw.ElapsedMilliseconds > 20)
                    TraySpike.Log.Write($"SLOW CollectionChanged handler: {sw.ElapsedMilliseconds} ms ({e.Action})");
            }
        }

        private void ApplyChange(NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add when e.NewItems != null:
                    foreach (NotifyIcon ni in e.NewItems) Add(ni);
                    break;
                case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                    foreach (NotifyIcon ni in e.OldItems) Remove(ni);
                    break;
                default:
                    SyncAll();
                    break;
            }
        }

        private void Add(NotifyIcon ni)
        {
            if (_icons.OfType<ManagedShellTrayIcon>().Any(i => ReferenceEquals(i.Source, ni))) return;
            _icons.Add(new ManagedShellTrayIcon(ni));
        }

        private void Remove(NotifyIcon ni)
        {
            var existing = _icons.OfType<ManagedShellTrayIcon>().FirstOrDefault(i => ReferenceEquals(i.Source, ni));
            if (existing == null) return;
            existing.Detach();
            _icons.Remove(existing);
        }

        private void SyncAll()
        {
            HandlerDepth++;
            try
            {
                // Snapshot: changing our UI collection can pump sent messages, which re-enters
                // ManagedShell's WndProc and modifies TrayIcons while we iterate.
                var source = _shell.NotificationArea.TrayIcons.ToList();
                // Reference identity on purpose: NotifyIcon.Equals matches on hwnd+uID OR GUID, so a
                // replaced icon object still "equals" its successor and would never be removed.
                foreach (var stale in _icons.OfType<ManagedShellTrayIcon>().Where(i => !source.Any(s => ReferenceEquals(s, i.Source))).ToList())
                {
                    stale.Detach();
                    _icons.Remove(stale);
                }
                var live = _shell.NotificationArea.TrayIcons;
                foreach (NotifyIcon ni in source)
                {
                    // Skip icons removed by a re-entrant tray message while we were iterating.
                    if (live.Any(s => ReferenceEquals(s, ni))) Add(ni);
                }
            }
            finally
            {
                HandlerDepth--;
            }
        }

        public void Dispose()
        {
            // Destroys our Shell_TrayWnd and broadcasts TaskbarCreated so apps re-register with Explorer.
            _shell.Dispose();
        }
    }

    public sealed class ManagedShellTrayIcon : ITrayIcon
    {
        public ManagedShellTrayIcon(NotifyIcon source)
        {
            Source = source;
            Source.PropertyChanged += Source_PropertyChanged;
        }

        public NotifyIcon Source { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id => Source.Identifier;
        public ImageSource? Image => Source.Icon;
        public string? ToolTip => Source.Title;
        public bool IsHidden => Source.IsHidden;
        public string? OwnerPath => Source.Path;

        public void SetScreenBounds(Rect screenPx)
        {
            // Shell_NotifyIconGetRect asks twice: message 1 wants the top-left point, message 2 the size.
            // ManagedShell answers message 2 with Placement.Right/Bottom, so those carry width/height.
            Source.Placement = new NativeMethods.Rect
            {
                Left = (int)Math.Round(screenPx.Left),
                Top = (int)Math.Round(screenPx.Top),
                Right = (int)Math.Round(screenPx.Width),
                Bottom = (int)Math.Round(screenPx.Height)
            };
        }

        public void OnMouseEnter() => Source.IconMouseEnter(MouseHelper.GetCursorPositionParam());

        public void OnMouseLeave() => Source.IconMouseLeave(MouseHelper.GetCursorPositionParam());

        public void OnMouseMove() => Source.IconMouseMove(MouseHelper.GetCursorPositionParam());

        // Same sequencing as RetroBar: left button down is sent on release, other buttons on press.
        public void OnMouseDown(MouseButton button)
        {
            if (button != MouseButton.Left)
                Source.IconMouseDown(button, MouseHelper.GetCursorPositionParam(), System.Windows.Forms.SystemInformation.DoubleClickTime);
        }

        public void OnMouseUp(MouseButton button)
        {
            uint pos = MouseHelper.GetCursorPositionParam();
            int dbl = System.Windows.Forms.SystemInformation.DoubleClickTime;
            if (button == MouseButton.Left)
                Source.IconMouseDown(button, pos, dbl);
            Source.IconMouseUp(button, pos, dbl);
        }

        internal void Detach() => Source.PropertyChanged -= Source_PropertyChanged;

        private void Source_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            string? mapped = e.PropertyName switch
            {
                nameof(NotifyIcon.Icon) => nameof(Image),
                nameof(NotifyIcon.Title) => nameof(ToolTip),
                nameof(NotifyIcon.IsHidden) => nameof(IsHidden),
                _ => null
            };
            if (mapped != null)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(mapped));
        }
    }
}
