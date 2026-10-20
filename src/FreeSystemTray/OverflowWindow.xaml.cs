using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FreeSystemTray.Icons;
using FreeSystemTray.Placement;
using FreeSystemTray.ShowHide;
using FreeSystemTray.Theming;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray
{
    /// <summary>Where the flyout goes: next to the tray window, centred on its chevron.</summary>
    internal readonly record struct FlyoutAnchor(PixelRect Tray, PixelRect Chevron, ScreenEdge Edge);

    /// <summary>
    /// The hidden-icons flyout: the icons of <see cref="IconModel.Overflow"/> in a grid next to the
    /// tray's chevron, on the side away from the nearest screen edge. Like the tray window it is a
    /// non-activating, topmost tool window. It closes on a click anywhere else or a switch to another
    /// window, except in a menu or flyout opened from one of the tray's icons (the same rule as
    /// auto-hide), and when the tray hides.
    /// </summary>
    public partial class OverflowWindow : Window
    {
        public static readonly DependencyProperty GridProperty = DependencyProperty.Register(
            nameof(Grid), typeof(IconGrid), typeof(OverflowWindow), new PropertyMetadata(new IconGrid(1, 0)));

        private readonly ITrayHost _host;
        private readonly IconModel _model;
        private readonly Func<FlyoutAnchor?> _anchor;
        private readonly Action<string> _log;
        private readonly Dictionary<ITrayIcon, PixelRect> _iconBounds = new(ReferenceEqualityComparer.Instance);
        private readonly TrayInteraction _interaction = new();
        private readonly uint _pid = (uint)Environment.ProcessId;
        private IntPtr _hwnd;
        private ShellWatcher? _watcher;
        private bool _placementQueued, _boundsQueued;
        private PixelRect _bounds;
        private (TrayBlur Blur, Color Tint, double CornerRadius) _effects;
        private (TrayBlur Blur, int Width, int Height, int Radius) _applied;

        internal OverflowWindow(TrayWindow tray, ITrayHost host, IconModel model, Func<FlyoutAnchor?> anchor, Action<string> log)
        {
            _host = host;
            _model = model;
            _anchor = anchor;
            _log = log;
            InitializeComponent();
            Resources = tray.Resources; // sizes, theme and templates, kept in step with the tray
            IconCell.SetHost(this, tray);
            IconList.ItemsSource = model.Overflow;
            model.Overflow.CollectionChanged += Overflow_CollectionChanged;
            UpdateColumns();

            ContentHost.ContentSizeChanged += (_, _) => QueuePlacement();
            DpiChanged += (_, _) => QueuePlacement();
            LayoutUpdated += (_, _) => QueueBoundsReport();
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
                WindowEffects.DoNotRoundCorners(_hwnd);
            };
        }

        /// <summary>How the icons are arranged: a square-ish grid.</summary>
        public IconGrid Grid
        {
            get => (IconGrid)GetValue(GridProperty);
            set => SetValue(GridProperty, value);
        }

        public bool IsOpen => IsVisible;

        /// <summary>The flyout's bounds in physical pixels while it is open, else empty.</summary>
        public PixelRect ScreenBounds => IsOpen ? _bounds : default;

        /// <summary>Each shown icon's bounds as last reported to the host (diagnostics).</summary>
        public IReadOnlyDictionary<ITrayIcon, PixelRect> IconBounds => _iconBounds;

        /// <summary>The cell of an icon in the flyout, if it has one.</summary>
        internal IconCell? CellOf(IconItem item) =>
            IconList.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject container ? TrayWindow.FindDescendant<IconCell>(container) : null;

        public void Open(string reason)
        {
            if (IsOpen) return;
            UpdateColumns();
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            ContentHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place();
            Show();
            Place(); // measured again once shown, at the monitor's DPI
            _interaction.Reset();
            _watcher = new ShellWatcher(Dispatcher, watchClicks: true, OnButtonDown, OnForeground, OnMenu, _log);
            _log($"Hidden-icons flyout opened ({reason}): {_model.Overflow.Count} icons at {_bounds}");
        }

        public void CloseFlyout(string reason)
        {
            if (!IsOpen) return;
            _watcher?.Dispose();
            _watcher = null;
            Hide();
            _host.SetOverflowBounds(default);
            _iconBounds.Clear();
            _log($"Hidden-icons flyout closed ({reason})");
        }

        /// <summary>A button went down or up on one of the tray's icons: what opens next belongs to the tray.</summary>
        internal void NoteClick() => _interaction.Clicked(ShellWatcher.Now());

        /// <summary>The theme's blur behind the flyout, as behind the tray window, clipped to its rounded corners.</summary>
        internal void SetEffects(TrayBlur blur, Color tint, double cornerRadius)
        {
            _effects = (blur, tint, cornerRadius);
            ApplyEffects();
        }

        private void ApplyEffects()
        {
            if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT r)) return;
            int radius = _effects.Blur == TrayBlur.None ? 0 : (int)Math.Round(_effects.CornerRadius * VisualTreeHelper.GetDpi(this).DpiScaleX);
            var wanted = (_effects.Blur, r.Right - r.Left, r.Bottom - r.Top, radius);
            if (wanted == _applied) return;
            if (wanted.Blur != _applied.Blur || wanted.Blur == TrayBlur.Acrylic) WindowEffects.SetBlur(_hwnd, _effects.Blur, _effects.Tint);
            WindowEffects.SetRoundedRegion(_hwnd, wanted.Item2, wanted.Item3, radius);
            _applied = wanted;
        }

        private void Overflow_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateColumns();
            if (e.OldItems != null)
            {
                foreach (IconItem item in e.OldItems) _iconBounds.Remove(item.Icon);
            }
        }

        private void UpdateColumns() => Grid = new IconGrid(Math.Max(1, (int)Math.Ceiling(Math.Sqrt(_model.Overflow.Count))), 0);

        #region Placement

        private void QueuePlacement()
        {
            if (_placementQueued || !IsOpen) return;
            _placementQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Send, Place);
        }

        /// <summary>Sizes the flyout around its icons and puts it next to the tray's chevron.</summary>
        internal void Place()
        {
            _placementQueued = false;
            if (_hwnd == IntPtr.Zero || _anchor() is not FlyoutAnchor anchor) return;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Size content = ContentHost.ContentSize;
            var size = new PixelSize((int)Math.Ceiling(content.Width * dpi.DpiScaleX), (int)Math.Ceiling(content.Height * dpi.DpiScaleY));
            if (size.Width <= 0 || size.Height <= 0) return;
            IReadOnlyList<MonitorInfo> monitors = Monitors.GetAll();
            PixelRect workArea = monitors.Count > 0 ? PlacementMath.MonitorFor(anchor.Tray, monitors).WorkArea : anchor.Tray;
            PixelRect rect = PlacementMath.FlyoutRect(anchor.Tray, anchor.Chevron, anchor.Edge, size, workArea, (int)Math.Round(4 * dpi.DpiScaleX));
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, NativeMethods.SWP_NOACTIVATE);
            _bounds = rect;
            if (IsOpen) _host.SetOverflowBounds(rect);
            ApplyEffects();
            QueueBoundsReport();
        }

        private void QueueBoundsReport()
        {
            if (_boundsQueued || !IsOpen) return;
            _boundsQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, ReportIconBounds);
        }

        /// <summary>Tells each icon where it is, for apps that ask to place their menus.</summary>
        private void ReportIconBounds()
        {
            _boundsQueued = false;
            if (!IsOpen) return;
            foreach (IconItem item in _model.Overflow)
            {
                if (CellOf(item) is { IsVisible: true, ActualWidth: > 0 } cell)
                {
                    PixelRect r = cell.ScreenBounds();
                    if (_iconBounds.TryGetValue(item.Icon, out PixelRect old) && old == r) continue;
                    _iconBounds[item.Icon] = r;
                    item.Icon.SetScreenBounds(r);
                }
            }
        }

        #endregion

        #region Closing on a click elsewhere

        private void OnButtonDown(ShellWatcher source, PixelPoint point)
        {
            if (source != _watcher || !IsOpen) return;
            IntPtr root = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = point.X, Y = point.Y }), NativeMethods.GA_ROOT);
            // Our windows: the flyout, the tray (its chevron closes the flyout itself), their menus.
            if (IsOurs(root) || _interaction.Owns(root, ShowHideController.IsPopup)) return;
            CloseFlyout($"click at {point.X},{point.Y}");
        }

        private void OnForeground(ShellWatcher source, IntPtr window, TimeSpan at)
        {
            if (source != _watcher || !IsOpen) return;
            if (_interaction.ForegroundChanged(window, at) || IsOurs(window)) return;
            CloseFlyout("switched to another window");
        }

        private void OnMenu(ShellWatcher source, IntPtr menu, bool started, TimeSpan at)
        {
            if (source != _watcher) return;
            if (started) _interaction.MenuStarted(menu, at);
            else _interaction.MenuEnded(menu);
        }

        private bool IsOurs(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == _pid;
        }

        #endregion
    }
}
