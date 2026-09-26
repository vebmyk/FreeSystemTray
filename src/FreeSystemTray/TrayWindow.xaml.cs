using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using FreeSystemTray.Logging;
using FreeSystemTray.Placement;
using FreeSystemTray.Scaling;
using FreeSystemTray.Settings;
using FreeSystemTray.ShowHide;
using FreeSystemTray.Theming;
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
        private readonly ThemeStore _themes;
        private Theme _theme = Theme.Default;
        private ResourceDictionary? _themeXaml;
        private bool _effectsSuspended;
        private bool _blurShown;
        private TrayBlur _blurKind;
        private (int Width, int Height, int Radius) _region;
        private (bool AcrylicShown, bool BlurUnavailable) _background;
        private bool _transparency = WindowEffects.TransparencyEnabled();

        public TrayWindow(ITrayHost host, AppSettings settings, SettingsStore store, ThemeStore themes)
        {
            _host = host;
            _settings = settings;
            _store = store;
            _themes = themes;
            InitializeComponent();
            Title = WindowTitle;

            LoadTheme();
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
            LoadTheme();
        }

        /// <summary>The sizes in use, from the scaling settings and the theme.</summary>
        internal TrayLayout Layout { get; private set; } = TrayLayout.Default;

        /// <summary>The theme in use.</summary>
        internal Theme Theme => _theme;

        /// <summary>What was wrong with the theme's files when it was last loaded (diagnostics).</summary>
        internal IReadOnlyList<string> ThemeProblems { get; private set; } = Array.Empty<string>();

        /// <summary>Whether the theme's blur is being drawn now (it is off during show/hide animations).</summary>
        internal bool BlurShown => _blurShown;

        /// <summary>Windows' "Transparency effects" setting, as last read.</summary>
        internal bool TransparencyEffects => _transparency;

        /// <summary>(Re)reads the theme named in the settings from its files and applies it.</summary>
        internal void LoadTheme()
        {
            var problems = new List<string>();
            string requested = string.IsNullOrWhiteSpace(_settings.Theme) ? ThemeStore.DefaultId : _settings.Theme.Trim();
            Theme theme = _themes.Load(requested, problems);
            foreach (string p in problems) AppLog.Write("Theme: " + p);
            ThemeProblems = problems;
            // The theme in use could not be read now (for example half-saved in an editor): keep it
            // until its file reads again, rather than falling back to the defaults.
            if (!string.Equals(theme.Id, requested, StringComparison.OrdinalIgnoreCase) && string.Equals(_theme.Id, requested, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Write($"Theme {requested} could not be loaded; keeping the version in use");
                ApplyAppearance();
                return;
            }
            _theme = theme;

            _themeXaml = null;
            if (theme.XamlPath != null)
            {
                try
                {
                    _themeXaml = ThemeResources.LoadXaml(theme.XamlPath);
                }
                catch (Exception ex) when (ex is XamlParseException or IOException or UnauthorizedAccessException)
                {
                    AppLog.Write($"Theme: {theme.XamlPath} could not be loaded: {ex.Message}");
                    ThemeProblems = problems.Append($"{theme.XamlPath}: {ex.Message}").ToList();
                }
            }
            AppLog.Write($"Theme {theme.Id} ('{theme.Name}'): {theme.Layout}{(theme.Layout == IconLayout.Grid ? $" of {theme.GridColumns}" : "")}, " +
                         $"background {Theme.Format(theme.Background)} at {theme.BackgroundOpacity:0.##}, blur {theme.Blur}" +
                         (theme.Blur != TrayBlur.None && !_transparency ? " (Windows' transparency effects are off: solid background instead)" : "") +
                         $", corner radius {theme.CornerRadius:0.##}" +
                         (_themeXaml != null ? $", XAML {theme.XamlPath}" : ""));
            ApplyAppearance();
        }

        /// <summary>
        /// Applies the scaling settings and the theme: sizes, colours, the icons' arrangement, the grip's
        /// side, and the blur. The window then resizes itself around its anchor corner.
        /// </summary>
        private void ApplyAppearance()
        {
            ScalingSettings s = _settings.Scaling;
            Theme t = _theme;
            TrayLayout layout = TrayLayout.Compute(ParseSize(s.IconSize, "iconSize"), ParseSize(s.TraySize, "traySize"),
                new TrayMetrics(t.BorderThickness, t.GripSize, t.CornerRadius, t.IconSpacing, t.HighlightCornerRadius));
            Layout = layout;
            Resources["IconSize"] = layout.IconSize;
            Resources["CellSize"] = layout.CellSize;
            Resources["TrayBorder"] = new Thickness(layout.BorderThickness);
            Resources["Tray.CornerRadius"] = new CornerRadius(layout.CornerRadius);
            Resources["Icon.HighlightCornerRadius"] = new CornerRadius(layout.HighlightCornerRadius);
            Resources["Icons.Layout"] = t.Layout;
            Resources["Icons.Columns"] = t.GridColumns;
            Resources["Icons.Spacing"] = layout.Spacing;
            Resources["IconListMargin"] = t.Layout == IconLayout.Column
                ? new Thickness(layout.MarginAcross, layout.MarginAlong, layout.MarginAcross, layout.MarginAlong)
                : new Thickness(layout.MarginAlong, layout.MarginAcross, layout.MarginAlong, layout.MarginAcross);
            PlaceGrip(t.EffectiveGripPosition, layout);
            UpdateThemeDictionaries();
            UseLayoutRounding = s.SnapToPixels;
            SnapsToDevicePixels = s.SnapToPixels;
            ScaledIcon.SetOptions(IconList, new IconRenderOptions(s.IconScaling, s.SnapToPixels, s.ExeIcons.ToArray()));
            AppLog.Write($"Scaling: icons {layout.IconSize:0.##} px ({s.IconSize}) in {layout.CellSize:0.##} px cells (tray size {s.TraySize}), " +
                         $"{s.IconScaling}, {(s.SnapToPixels ? "snapped to whole pixels" : "exact sizes")}" +
                         (s.ExeIcons.Count > 0 ? $", exe icons for {string.Join(", ", s.ExeIcons)}" : ""));
            UpdateWindowEffects();
        }

        /// <summary>The grip on one side of the icons, as long as the tray, with the tray's rounded corners on its outer side.</summary>
        private void PlaceGrip(GripPosition side, TrayLayout layout)
        {
            DockPanel.SetDock(Grip, side switch
            {
                GripPosition.Top => Dock.Top,
                GripPosition.Right => Dock.Right,
                GripPosition.Bottom => Dock.Bottom,
                _ => Dock.Left
            });
            bool across = side is GripPosition.Top or GripPosition.Bottom;
            Grip.Width = across ? double.NaN : layout.GripWidth;
            Grip.Height = across ? layout.GripWidth : double.NaN;
            double r = Math.Max(0, layout.CornerRadius - layout.BorderThickness);
            Resources["Grip.CornerRadius"] = side switch
            {
                GripPosition.Top => new CornerRadius(r, r, 0, 0),
                GripPosition.Right => new CornerRadius(0, r, r, 0),
                GripPosition.Bottom => new CornerRadius(0, 0, r, r),
                _ => new CornerRadius(r, 0, 0, r)
            };
        }

        /// <summary>
        /// The theme's values and its own XAML as merged dictionaries after the styles, so the XAML
        /// wins. With acrylic shown, Windows draws the tint and the tray's background stays clear.
        /// </summary>
        private void UpdateThemeDictionaries()
        {
            var merged = Resources.MergedDictionaries;
            while (merged.Count > 1) merged.RemoveAt(merged.Count - 1);
            _background = BackgroundState();
            merged.Add(ThemeResources.Build(_theme, _background.AcrylicShown, _background.BlurUnavailable));
            if (_themeXaml != null) merged.Add(_themeXaml);
        }

        /// <summary>
        /// How the background is drawn: clear while Windows draws acrylic (it draws the tint), opaque
        /// when the theme wants a blur but Windows' transparency effects are off, as Windows does with
        /// its own blurred surfaces.
        /// </summary>
        private (bool AcrylicShown, bool BlurUnavailable) BackgroundState() =>
            (_blurShown && _blurKind == TrayBlur.Acrylic, _theme.Blur != TrayBlur.None && !_transparency);

        /// <summary>
        /// Turns the theme's blur on or off to match the theme, Windows' transparency setting and
        /// whether an animation runs, and clips the window to its rounded corners while the blur is on
        /// (the blur would show square corners).
        /// </summary>
        private void UpdateWindowEffects()
        {
            if (_hwnd == IntPtr.Zero) return;
            bool blur = _theme.Blur != TrayBlur.None && !_effectsSuspended && _transparency;
            if (blur != _blurShown || _theme.Blur != _blurKind)
            {
                if (!WindowEffects.SetBlur(_hwnd, blur ? _theme.Blur : TrayBlur.None, ThemeResources.BackgroundColor(_theme)))
                    AppLog.Write($"SetWindowCompositionAttribute failed; no {_theme.Blur} behind the tray");
                _blurShown = blur;
                _blurKind = _theme.Blur;
            }
            else if (blur && _theme.Blur == TrayBlur.Acrylic)
            {
                WindowEffects.SetBlur(_hwnd, TrayBlur.Acrylic, ThemeResources.BackgroundColor(_theme)); // the tint may have changed
            }
            if (BackgroundState() != _background) UpdateThemeDictionaries();

            int radius = 0;
            if (blur && NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT r))
            {
                radius = (int)Math.Round(Layout.CornerRadius * VisualTreeHelper.GetDpi(this).DpiScaleX);
                var region = (r.Right - r.Left, r.Bottom - r.Top, radius);
                if (region == _region) return;
                _region = region;
                WindowEffects.SetRoundedRegion(_hwnd, region.Item1, region.Item2, radius);
                return;
            }
            if (_region != default)
            {
                _region = default;
                WindowEffects.SetRoundedRegion(_hwnd, 0, 0, 0);
            }
        }

        /// <summary>
        /// Show/hide animations turn the blur off while they run: Windows would keep drawing it at full
        /// strength behind a fading or sliding tray.
        /// </summary>
        internal void SetEffectsSuspended(bool suspended)
        {
            if (_effectsSuspended == suspended) return;
            _effectsSuspended = suspended;
            UpdateWindowEffects();
        }

        /// <summary>The element drawing an icon, if its cell exists (diagnostics).</summary>
        internal ScaledIcon? IconElementOf(ITrayIcon icon) =>
            IconList.ItemContainerGenerator.ContainerFromItem(icon) is DependencyObject container ? FindDescendant<ScaledIcon>(container) : null;

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                if (FindDescendant<T>(child) is T deeper) return deeper;
            }
            return null;
        }

        private static SizeValue ParseSize(string text, string setting)
        {
            try
            {
                return SizeValue.Parse(text);
            }
            catch (FormatException ex)
            {
                AppLog.Write($"Setting {setting}: {ex.Message}; using 100%");
                return SizeValue.Percent(100);
            }
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
            WindowEffects.DoNotRoundCorners(_hwnd);

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
            // Personalization colours changed, which includes "Transparency effects".
            if (msg == NativeMethods.WM_SETTINGCHANGE && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            {
                bool transparency = WindowEffects.TransparencyEnabled();
                if (transparency != _transparency)
                {
                    _transparency = transparency;
                    AppLog.Write($"Windows' transparency effects turned {(transparency ? "on" : "off")}");
                    UpdateWindowEffects();
                }
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
            UpdateWindowEffects(); // the rounded region follows the size
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
            FillThemeMenu();
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

        /// <summary>One item per theme file; choosing one switches to it at once and saves it.</summary>
        private void FillThemeMenu()
        {
            ThemeMenuItem.Items.Clear();
            foreach (ThemeInfo info in _themes.List())
            {
                var item = new MenuItem
                {
                    Header = info.Name.Replace("_", "__"), // an underscore would be an access key
                    IsCheckable = true,
                    IsChecked = string.Equals(info.Id, _theme.Id, StringComparison.OrdinalIgnoreCase),
                    ToolTip = info.Path
                };
                string id = info.Id;
                item.Click += (_, _) => SelectTheme(id);
                ThemeMenuItem.Items.Add(item);
            }
            ThemeMenuItem.IsEnabled = ThemeMenuItem.Items.Count > 0;
        }

        private void SelectTheme(string id)
        {
            _settings.Theme = id;
            SaveSettings();
            LoadTheme();
        }

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
            if (sender is DependencyObject cell) CellState.SetIsPressed(cell, false);
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
            if (sender is DependencyObject cell) CellState.SetIsPressed(cell, true);
            IconOf(sender)?.OnMouseDown(e.ChangedButton);
            e.Handled = true;
        }

        private void Icon_MouseUp(object sender, MouseButtonEventArgs e)
        {
            ShowHide?.NoteTrayClick();
            if (sender is DependencyObject cell) CellState.SetIsPressed(cell, false);
            IconOf(sender)?.OnMouseUp(e.ChangedButton);
            e.Handled = true;
        }

        #endregion
    }
}
