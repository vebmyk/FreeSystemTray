using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FreeSystemTray.Logging;
using FreeSystemTray.Placement;
using FreeSystemTray.Settings;
using FreeSystemTray.ShowHide;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray
{
    /// <summary>
    /// The floating tray: a borderless, topmost tool window that never takes focus (WS_EX_NOACTIVATE,
    /// WS_EX_TOOLWINDOW), so it stays out of Alt+Tab and apps can still take the foreground for their
    /// menus. It sizes itself around the icons with its anchor corner fixed, and can be dragged by the
    /// grip on its left edge unless the position is locked. <see cref="ShowHide"/> shows and hides it.
    /// </summary>
    public partial class TrayWindow : Window
    {
        public const string WindowTitle = "FreeSystemTray";
        private const double IconSize = 16;

        private readonly ITrayHost _host;
        private readonly SettingsStore _store;
        private readonly Dictionary<ITrayIcon, PixelRect> _iconBounds = new();
        private AppSettings _settings;
        private IntPtr _hwnd;
        private bool _placementQueued;
        private bool _boundsQueued;
        private PixelRect _reportedBounds;
        private ScreenEdge _reportedEdge = ScreenEdge.Bottom;
        private IntPtr _behind;
        private bool _gripMenuOpen;
        private bool _dragging;
        private PixelPoint _dragStartCursor;
        private PixelRect _dragStartBounds;
        private IReadOnlyList<MonitorInfo> _dragMonitors = Array.Empty<MonitorInfo>();

        public TrayWindow(ITrayHost host, AppSettings settings, SettingsStore store)
        {
            _host = host;
            _settings = settings;
            _store = store;
            InitializeComponent();
            Title = WindowTitle;

            Resources["IconSize"] = IconSize;
            Resources["CellSize"] = Math.Max(24, IconSize + 8);
            IconList.ItemsSource = host.Icons;
            ((INotifyCollectionChanged)host.Icons).CollectionChanged += Icons_CollectionChanged;

            ContentHost.ContentSizeChanged += (_, _) => QueuePlacement();
            SourceInitialized += TrayWindow_SourceInitialized;
            LocationChanged += (_, _) => QueueBoundsReport();
            LayoutUpdated += (_, _) => QueueBoundsReport();
            DpiChanged += (_, _) => QueuePlacement();
            UpdateGrip();
        }

        /// <summary>The window's bounds as last reported to the host, in physical pixels.</summary>
        public PixelRect ScreenBounds => _reportedBounds;

        /// <summary>The screen edge the window is nearest to, as last reported to the host.</summary>
        public ScreenEdge Edge => _reportedEdge;

        /// <summary>Each visible icon's bounds as last reported to the host, in physical pixels.</summary>
        public IReadOnlyDictionary<ITrayIcon, PixelRect> IconBounds => _iconBounds;

        public IntPtr Handle => _hwnd;

        /// <summary>Shows and hides the window; set once by the app before the window is shown.</summary>
        internal ShowHideController? ShowHide { get; set; }

        /// <summary>The element the slide animation moves.</summary>
        internal UIElement AnimatedContent => ContentHost;

        /// <summary>The window is being dragged or its menu is open: auto-hide waits.</summary>
        public bool IsBusy => _dragging || _gripMenuOpen;

        /// <summary>Lays the window out and places it before it is shown again: it may have changed size while hidden.</summary>
        internal void PrepareToShow()
        {
            UpdateLayout();
            ApplyPlacement();
        }

        /// <summary>
        /// Puts the window on top of the other always-on-top windows, without activating it. Behind a
        /// full-screen window this brings it up over that window (HWND_TOP would not: a window of a
        /// background process cannot go above the foreground window that way), until
        /// <see cref="SetBehindFullScreen"/> puts it back.
        /// </summary>
        internal void BringToTop()
        {
            if (_hwnd == IntPtr.Zero) return;
            Topmost = true;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        /// <summary>The full-screen window the tray stays behind, or zero while it is always-on-top.</summary>
        internal IntPtr BehindFullScreen => _behind;

        /// <summary>
        /// Steps behind a full-screen window: no longer always-on-top, and directly below that window
        /// in the Z-order, as Explorer's taskbar does. Zero makes it always-on-top again.
        /// </summary>
        internal void SetBehindFullScreen(IntPtr fullScreenWindow)
        {
            if (_hwnd == IntPtr.Zero) return;
            _behind = fullScreenWindow;
            if (fullScreenWindow == IntPtr.Zero)
            {
                Topmost = true; // SetWindowPos(HWND_TOPMOST)
                return;
            }
            Topmost = false; // HWND_NOTOPMOST: the top of the ordinary windows, still above the full-screen one
            NativeMethods.SetWindowPos(_hwnd, fullScreenWindow, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        /// <summary>Reports the window's and icons' positions to the host again (after the show animation moved them).</summary>
        internal void RefreshBounds() => QueueBoundsReport();

        /// <summary>Applies settings read from the settings file after it was edited.</summary>
        internal void ApplySettings(AppSettings settings)
        {
            _settings = settings;
            UpdateGrip();
            QueuePlacement();
            ShowHide?.ApplySettings(settings.ShowHide);
        }

        /// <summary>The tray window of another running instance, if any.</summary>
        public static IntPtr FindRunningInstance()
        {
            string ourExe = Path.GetFileName(Environment.ProcessPath ?? "FreeSystemTray.exe");
            IntPtr h = IntPtr.Zero;
            while ((h = NativeMethods.FindWindowEx(IntPtr.Zero, h, null, WindowTitle)) != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(h, out uint pid);
                if (pid != Environment.ProcessId && ProcessInfo.ImagePath(pid) is string exe &&
                    string.Equals(Path.GetFileName(exe), ourExe, StringComparison.OrdinalIgnoreCase))
                    return h;
            }
            return IntPtr.Zero;
        }

        private void TrayWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

            // Size and place the window before it is first shown.
            ContentHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            ApplyPlacement();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Monitors or work areas changed: keep the window on screen and re-report the nearest edge.
            if (msg == NativeMethods.WM_DISPLAYCHANGE ||
                (msg == NativeMethods.WM_SETTINGCHANGE && wParam.ToInt64() == NativeMethods.SPI_SETWORKAREA))
            {
                _reportedBounds = default;
                QueuePlacement();
            }
            return IntPtr.Zero;
        }

        #region Placement

        private void QueuePlacement()
        {
            if (_placementQueued) return;
            _placementQueued = true;
            // Send priority: resize before anything else runs, so at most one frame shows the old size.
            Dispatcher.BeginInvoke(DispatcherPriority.Send, ApplyPlacement);
        }

        /// <summary>Sizes the window around its content and puts its anchor corner at the saved position.</summary>
        private void ApplyPlacement()
        {
            _placementQueued = false;
            if (_hwnd == IntPtr.Zero || _dragging) return;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Size content = ContentHost.ContentSize;
            var size = new PixelSize((int)Math.Ceiling(content.Width * dpi.DpiScaleX), (int)Math.Ceiling(content.Height * dpi.DpiScaleY));
            if (size.Width <= 0 || size.Height <= 0) return;
            PixelRect rect = PlacementMath.Place(_settings.Position, size, Monitors.GetAll());
            // Behind a full-screen window (or brought up over it), keep the Z-order; otherwise make sure it is always-on-top.
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
                NativeMethods.SWP_NOACTIVATE | (_behind != IntPtr.Zero ? NativeMethods.SWP_NOZORDER : 0));
            QueueBoundsReport();
        }

        private void QueueBoundsReport()
        {
            if (_boundsQueued || _hwnd == IntPtr.Zero) return;
            _boundsQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, ReportBounds);
        }

        /// <summary>Tells the host where the window and each icon are; apps ask for these to place menus and flyouts.</summary>
        private void ReportBounds()
        {
            _boundsQueued = false;
            if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT r)) return;
            var bounds = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
            if (bounds != _reportedBounds)
            {
                IReadOnlyList<MonitorInfo> monitors = Monitors.GetAll();
                ScreenEdge edge = monitors.Count > 0
                    ? PlacementMath.NearestEdge(bounds, PlacementMath.MonitorFor(bounds, monitors).WorkArea)
                    : ScreenEdge.Bottom;
                _reportedBounds = bounds;
                _reportedEdge = edge;
                _host.SetTrayWindowBounds(bounds, edge);
            }
            UpdateIconBounds();
        }

        private void UpdateIconBounds()
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            foreach (ITrayIcon icon in _host.Icons)
            {
                if (IconList.ItemContainerGenerator.ContainerFromItem(icon) is FrameworkElement cell && cell.IsVisible && cell.ActualWidth > 0)
                    SetIconBounds(icon, ScreenBoundsOf(cell, dpi));
                else
                    _iconBounds.Remove(icon);
            }
        }

        private void SetIconBounds(ITrayIcon icon, PixelRect bounds)
        {
            if (_iconBounds.TryGetValue(icon, out PixelRect old) && old == bounds) return;
            _iconBounds[icon] = bounds;
            icon.SetScreenBounds(bounds);
        }

        private static PixelRect ScreenBoundsOf(FrameworkElement element, DpiScale dpi)
        {
            Point p = element.PointToScreen(new Point(0, 0));
            return PixelRect.FromPointAndSize(
                new PixelPoint((int)Math.Round(p.X), (int)Math.Round(p.Y)),
                new PixelSize((int)Math.Round(element.ActualWidth * dpi.DpiScaleX), (int)Math.Round(element.ActualHeight * dpi.DpiScaleY)));
        }

        private void Icons_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (ITrayIcon icon in e.OldItems) _iconBounds.Remove(icon);
            }
            QueueBoundsReport();
        }

        #endregion

        #region Dragging

        private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_settings.LockPosition || _hwnd == IntPtr.Zero) return;
            if (!NativeMethods.GetCursorPos(out NativeMethods.POINT p) || !NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT r)) return;
            _dragStartCursor = new PixelPoint(p.X, p.Y);
            _dragStartBounds = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
            _dragMonitors = Monitors.GetAll();
            _dragging = _dragMonitors.Count > 0 && Grip.CaptureMouse();
        }

        private void Grip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || !NativeMethods.GetCursorPos(out NativeMethods.POINT p)) return;
            var cursor = new PixelPoint(p.X, p.Y);
            PixelRect proposed = _dragStartBounds.Offset(cursor.X - _dragStartCursor.X, cursor.Y - _dragStartCursor.Y);
            MonitorInfo monitor = PlacementMath.MonitorFor(cursor, _dragMonitors);
            if (_settings.SnapToEdges) proposed = PlacementMath.Snap(proposed, monitor, SnapThreshold());
            proposed = PlacementMath.KeepInside(proposed, monitor.Bounds);
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, proposed.Left, proposed.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        private void Grip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            EndDrag();
        }

        private void Grip_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

        /// <summary>Saves where the window ended up, as the corner facing the nearest corner of the screen.</summary>
        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            if (Grip.IsMouseCaptured) Grip.ReleaseMouseCapture();
            if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT r)) return;
            var bounds = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
            if (bounds == _dragStartBounds && _settings.Position != null) return;

            MonitorInfo monitor = PlacementMath.MonitorFor(bounds, _dragMonitors);
            Corner anchor = PlacementMath.AnchorFor(bounds, monitor.WorkArea);
            PixelPoint point = PlacementMath.AnchorPoint(bounds, anchor);
            _settings.Position = new SavedPosition(point.X, point.Y, anchor);
            SaveSettings();
            AppLog.Write($"Tray window moved to {bounds} on {monitor.DeviceName}; {anchor} corner saved at {point.X},{point.Y}");
            // The DPI may have changed on the way; size the window for the monitor it is on now.
            QueuePlacement();
        }

        private int SnapThreshold() => (int)Math.Round(_settings.SnapDistance * VisualTreeHelper.GetDpi(this).DpiScaleX);

        #endregion

        #region Menu

        private void GripMenu_Opened(object sender, RoutedEventArgs e)
        {
            _gripMenuOpen = true;
            LockMenuItem.IsChecked = _settings.LockPosition;
            SnapMenuItem.IsChecked = _settings.SnapToEdges;
            AutoHideMenuItem.IsChecked = _settings.ShowHide.AutoHide;
            // Hiding needs a way back: the hotkey or the hotspot.
            HideMenuItem.IsEnabled = ShowHide?.CanShowAgain == true;
            HideMenuItem.InputGestureText = ShowHide?.Hotkey?.ToString() ?? "";
            HideMenuItem.ToolTip = ShowHide?.CanShowAgain == true ? null
                : "Set a hotkey or a hotspot in the settings file to be able to hide the tray." +
                  (ShowHide?.HotkeyProblem is string problem ? " Hotkey: " + problem : "");
            ToolTipService.SetShowOnDisabled(HideMenuItem, true);
            // The window never activates by itself (WS_EX_NOACTIVATE), so without this the menu gets
            // no keyboard input (Esc, arrows). Explorer's taskbar menus take the foreground the same way.
            NativeMethods.SetForegroundWindow(_hwnd);
            ((ContextMenu)sender).Focus();
        }

        private void LockMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _settings.LockPosition = LockMenuItem.IsChecked;
            SaveSettings();
            UpdateGrip();
        }

        private void GripMenu_Closed(object sender, RoutedEventArgs e) => _gripMenuOpen = false;

        private void SnapMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _settings.SnapToEdges = SnapMenuItem.IsChecked;
            SaveSettings();
        }

        private void AutoHideMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _settings.ShowHide.AutoHide = AutoHideMenuItem.IsChecked;
            SaveSettings();
            ShowHide?.ApplySettings(_settings.ShowHide);
        }

        private void HideMenuItem_Click(object sender, RoutedEventArgs e) => ShowHide?.Hide("menu");

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

        private void UpdateGrip()
        {
            Grip.Cursor = _settings.LockPosition ? Cursors.Arrow : Cursors.SizeAll;
            Grip.ToolTip = _settings.LockPosition
                ? "FreeSystemTray (position locked). Right-click for options."
                : "FreeSystemTray: drag to move. Right-click for options.";
        }

        private void SaveSettings()
        {
            try
            {
                _store.Save(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"Could not save settings to {_store.FilePath}: {ex.Message}");
            }
        }

        #endregion

        #region Mouse routing to the icons' apps

        private static ITrayIcon? IconOf(object sender) => (sender as FrameworkElement)?.DataContext as ITrayIcon;

        private void Icon_MouseEnter(object sender, MouseEventArgs e)
        {
            if (IconOf(sender) is not { } icon || sender is not FrameworkElement cell) return;
            // Apps ask where their icon is right after it is clicked; make sure the answer is current.
            SetIconBounds(icon, ScreenBoundsOf(cell, VisualTreeHelper.GetDpi(cell)));
            icon.OnMouseEnter();
            e.Handled = true;
        }

        private void Icon_MouseLeave(object sender, MouseEventArgs e)
        {
            IconOf(sender)?.OnMouseLeave();
            e.Handled = true;
        }

        private void Icon_MouseMove(object sender, MouseEventArgs e)
        {
            IconOf(sender)?.OnMouseMove();
            e.Handled = true;
        }

        // Clicks are noted before the app hears of them: the menu or flyout it opens in response
        // counts as part of the tray for auto-hide.
        private void Icon_MouseDown(object sender, MouseButtonEventArgs e)
        {
            ShowHide?.NoteTrayClick();
            IconOf(sender)?.OnMouseDown(e.ChangedButton);
            e.Handled = true;
        }

        private void Icon_MouseUp(object sender, MouseButtonEventArgs e)
        {
            ShowHide?.NoteTrayClick();
            IconOf(sender)?.OnMouseUp(e.ChangedButton);
            e.Handled = true;
        }

        #endregion
    }
}
