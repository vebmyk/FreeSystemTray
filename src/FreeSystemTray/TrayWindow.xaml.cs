using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using FreeSystemTray.Icons;
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
    /// It shows the icons the icon settings put in the tray (<see cref="IconModel"/>), then a chevron
    /// that opens the others in the hidden-icons flyout (<see cref="OverflowWindow"/>). In a grid of a
    /// fixed size (<see cref="IconGrid.IsFixed"/>) the chevron instead expands the tray in place to
    /// show them. Icons can be dragged to a new place, between the two as well.
    /// </summary>
    public partial class TrayWindow : Window, IIconCellHost
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
        private readonly IconModel _model;
        private readonly ChevronItem _chevron = new();
        private readonly CollectionContainer _expandedIcons = new();
        private readonly OverflowWindow _overflow;
        private IconGrid _grid;
        private bool _expanded;
        private bool _expandUp;
        private IconDrag? _iconDrag;
        private bool _iconMenuOpen;

        public TrayWindow(ITrayHost host, AppSettings settings, SettingsStore store, ThemeStore themes)
        {
            _host = host;
            _settings = settings;
            _store = store;
            _themes = themes;
            _grid = settings.Layout.Grid;
            _expandUp = ExpandsUp(settings);
            InitializeComponent();
            Title = WindowTitle;

            _model = new IconModel(host, settings.Icons, Dispatcher, _grid.Capacity);
            IconCell.SetHost(this, this);
            ChevronCell.SetToggle(this, () => ToggleOverflow("chevron"));
            // While the tray is expanded the hidden icons follow the chevron, so the chevron stays where it is.
            IconList.ItemsSource = new CompositeCollection { new CollectionContainer { Collection = _model.Tray }, _chevron, _expandedIcons };
            _model.Tray.CollectionChanged += TrayIcons_CollectionChanged;
            _model.Arranged += (_, _) => OnArranged();
            _overflow = new OverflowWindow(this, host, _model, OverflowAnchor, AppLog.Write);
            LoadTheme();
            OnArranged();
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible) return;
                _overflow.CloseFlyout("the tray hid");
                SetExpanded(false, "the tray hid");
            };

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

        /// <summary>Each shown icon's bounds as last reported to the host, in physical pixels: in the tray, or in the open flyout.</summary>
        public IReadOnlyDictionary<ITrayIcon, PixelRect> IconBounds =>
            _overflow.IconBounds.Count == 0 ? _iconBounds
                : _iconBounds.Concat(_overflow.IconBounds).ToDictionary(p => p.Key, p => p.Value, (IEqualityComparer<ITrayIcon>)ReferenceEqualityComparer.Instance);

        /// <summary>The icons, arranged by the icon settings.</summary>
        internal IconModel Icons => _model;

        internal ThemeStore Themes => _themes;

        /// <summary>The settings file in use.</summary>
        internal string SettingsPath => _store.FilePath;

        /// <summary>The hidden-icons flyout.</summary>
        internal OverflowWindow Overflow => _overflow;

        public IntPtr Handle => _hwnd;

        /// <summary>Shows and hides the window; set once by the app before the window is shown.</summary>
        internal ShowHideController? ShowHide { get; set; }

        /// <summary>The element the slide animation moves.</summary>
        internal UIElement AnimatedContent => ContentHost;

        /// <summary>The window or an icon is being dragged, or a menu of the tray or its flyout is open: auto-hide waits.</summary>
        public bool IsBusy => _dragging || _appMenuOpen || _iconMenuOpen || _iconDrag != null || _overflow.IsOpen;

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

        /// <summary>Applies settings: read from the settings file after it was edited, or changed in the app.</summary>
        internal void ApplySettings(AppSettings settings)
        {
            _settings = settings;
            _grid = settings.Layout.Grid;
            if (!_grid.IsFixed) SetExpanded(false, "the grid has no fixed size");
            // A fixed-size grid shows its hidden icons in place, not in the flyout.
            else _overflow.CloseFlyout("the grid has a fixed size");
            UpdateGrip();
            QueuePlacement();
            ShowHide?.ApplySettings(settings.ShowHide);
            _model.ApplySettings(settings.Icons, _grid.Capacity);
            LoadTheme();
            SettingsApplied?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The sizes in use, from the scaling settings and the theme.</summary>
        internal TrayLayout Layout { get; private set; } = TrayLayout.Default;

        /// <summary>The theme in use.</summary>
        internal Theme Theme => _theme;

        /// <summary>The icons' arrangement, from the layout settings.</summary>
        internal IconGrid Grid => _grid;

        /// <summary>A fixed-size grid shows its hidden icons in place (its chevron expanded it).</summary>
        internal bool IsExpanded => _expanded;

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
            AppLog.Write($"Theme {theme.Id} ('{theme.Name}'): " +
                         $"background {Theme.Format(theme.Background)} at {theme.BackgroundOpacity:0.##}, blur {theme.Blur}" +
                         (theme.Blur != TrayBlur.None && !_transparency ? " (Windows' transparency effects are off: solid background instead)" : "") +
                         $", corner radius {theme.CornerRadius:0.##}" +
                         (_themeXaml != null ? $", XAML {theme.XamlPath}" : ""));
            ApplyAppearance();
        }

        /// <summary>
        /// Applies the scaling and layout settings and the theme: sizes, colours, the icons' arrangement,
        /// the grip's side, and the blur. The window then resizes itself around its anchor corner.
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
            Resources["Icons.Grid"] = _grid;
            Resources["Icons.ExpandUp"] = _expandUp;
            Resources["Icons.Spacing"] = layout.Spacing;
            Resources["IconListMargin"] = _grid.IsColumn
                ? new Thickness(layout.MarginAcross, layout.MarginAlong, layout.MarginAcross, layout.MarginAlong)
                : new Thickness(layout.MarginAlong, layout.MarginAcross, layout.MarginAlong, layout.MarginAcross);
            Resources["OverflowPadding"] = new Thickness(Math.Max(layout.MarginAlong, layout.MarginAcross));
            // The chevron: 10 by 5 at an icon size of 16, drawn with a line 1/12 of the icon thick.
            Resources["Chevron.Width"] = layout.IconSize * 0.625;
            Resources["Chevron.Height"] = layout.IconSize * 0.3125;
            Resources["Chevron.Stroke"] = Math.Max(1, layout.IconSize / 12);
            PlaceGrip(t.GripPositionFor(_grid.IsColumn), layout);
            UpdateChevronAngle();
            UpdateThemeDictionaries();
            UseLayoutRounding = s.SnapToPixels;
            SnapsToDevicePixels = s.SnapToPixels;
            ScaledIcon.SetOptions(IconList, new IconRenderOptions(s.IconScaling, s.SnapToPixels, s.ExeIcons.ToArray()));
            AppLog.Write($"Layout: {_grid}" + (_grid.IsFixed ? $", {_grid.Capacity} cells" : ""));
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
            UpdateTrayEffects();
            // The flyout shares the tray's brushes, so it needs the same blur (or none) behind it.
            _overflow.SetEffects(_blurShown ? _blurKind : TrayBlur.None, ThemeResources.BackgroundColor(_theme), Layout.CornerRadius);
        }

        private void UpdateTrayEffects()
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
        internal ScaledIcon? IconElementOf(ITrayIcon icon)
        {
            if (_model.ItemFor(icon) is not { } item) return null;
            IconCell? cell = CellOf(item) ?? _overflow.CellOf(item);
            return cell == null ? null : FindDescendant<ScaledIcon>(cell);
        }

        /// <summary>The cell of an icon in the tray window, if it has one.</summary>
        internal IconCell? CellOf(IconItem item) =>
            IconList.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject container ? FindDescendant<IconCell>(container) : null;

        internal static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
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
            UpdateExpandDirection();
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
                UpdateChevronAngle();
                if (_overflow.IsOpen) _overflow.Place();
            }
            UpdateIconBounds();
        }

        private void UpdateIconBounds()
        {
            var shown = new HashSet<ITrayIcon>(ReferenceEqualityComparer.Instance);
            foreach (IconItem item in _expanded ? _model.Tray.Concat(_model.Overflow) : _model.Tray)
            {
                shown.Add(item.Icon);
                if (CellOf(item) is { IsVisible: true, ActualWidth: > 0 } cell)
                    SetIconBounds(item.Icon, cell.ScreenBounds());
                else
                    _iconBounds.Remove(item.Icon);
            }
            // The hidden icons of a tray that is no longer expanded.
            foreach (ITrayIcon gone in _iconBounds.Keys.Where(i => !shown.Contains(i)).ToList()) _iconBounds.Remove(gone);
        }

        private void SetIconBounds(ITrayIcon icon, PixelRect bounds)
        {
            if (_iconBounds.TryGetValue(icon, out PixelRect old) && old == bounds) return;
            _iconBounds[icon] = bounds;
            icon.SetScreenBounds(bounds);
        }

        /// <summary>An element's screen rectangle in physical pixels.</summary>
        internal static PixelRect ScreenBoundsOf(FrameworkElement element)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(element);
            Point p = element.PointToScreen(new Point(0, 0));
            return PixelRect.FromPointAndSize(
                new PixelPoint((int)Math.Round(p.X), (int)Math.Round(p.Y)),
                new PixelSize((int)Math.Round(element.ActualWidth * dpi.DpiScaleX), (int)Math.Round(element.ActualHeight * dpi.DpiScaleY)));
        }

        private void TrayIcons_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (IconItem item in e.OldItems) _iconBounds.Remove(item.Icon);
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
            if (_dragging) _overflow.CloseFlyout("the tray is being moved");
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

        /// <summary>Opens the settings window, at an icon (its key) or at the start. Set by the app.</summary>
        internal Action<string?>? OpenSettings { get; set; }

        /// <summary>The app's menu is open (from the grip or the app's own icon): auto-hide waits.</summary>
        private bool _appMenuOpen;

        private void Grip_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowAppMenu(Grip);
        }

        /// <summary>
        /// The app's menu at the pointer: from the grip, or from FreeSystemTray's own icon (then
        /// <paramref name="target"/> is null, and the tray may be hidden).
        /// </summary>
        internal void ShowAppMenu(UIElement? target)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            var settings = new MenuItem { Header = "_Settings…", FontWeight = FontWeights.SemiBold };
            settings.Click += (_, _) => OpenSettings?.Invoke(null);
            menu.Items.Add(settings);
            menu.Items.Add(ThemeMenu());
            menu.Items.Add(new Separator());
            AddCheck(menu, "_Lock position", _settings.LockPosition, on => UpdateSettings(s => s.LockPosition = on));
            AddCheck(menu, "S_nap to screen edges", _settings.SnapToEdges, on => UpdateSettings(s => s.SnapToEdges = on));
            menu.Items.Add(new Separator());
            AddCheck(menu, "_Auto-hide", _settings.ShowHide.AutoHide, on => UpdateSettings(s => s.ShowHide.AutoHide = on));
            if (ShowHide is { IsShown: false } showHide)
            {
                var show = new MenuItem { Header = "Sh_ow the tray", InputGestureText = showHide.Hotkey?.ToString() ?? "" };
                show.Click += (_, _) => showHide.Show(ShowCause.Menu);
                menu.Items.Add(show);
            }
            else
            {
                // Hiding needs a way back: the hotkey or the hotspot.
                bool canHide = ShowHide?.CanShowAgain == true;
                var hide = new MenuItem
                {
                    Header = "_Hide",
                    InputGestureText = ShowHide?.Hotkey?.ToString() ?? "",
                    IsEnabled = canHide,
                    ToolTip = canHide ? null : "Set a hotkey or a hotspot in the settings to be able to hide the tray." +
                                               (ShowHide?.HotkeyProblem is string problem ? " Hotkey: " + problem : "")
                };
                ToolTipService.SetShowOnDisabled(hide, true);
                hide.Click += (_, _) => ShowHide?.Hide("menu");
                menu.Items.Add(hide);
            }
            menu.Items.Add(new Separator());
            var exit = new MenuItem { Header = "E_xit FreeSystemTray" };
            exit.Click += (_, _) => Close();
            menu.Items.Add(exit);

            OpenMenu(menu, "App menu", open => _appMenuOpen = open);
        }

        /// <summary>
        /// Opens one of the tray's own menus so that it gets the keyboard (Esc, arrows): the tray never
        /// activates by itself (WS_EX_NOACTIVATE), so the tray window takes the foreground, as Explorer's
        /// taskbar does for its menus. It must do so before the menu opens: taking the foreground once
        /// the menu is open, with any window but the one the menu belongs to (its own popup window, the
        /// tray window for a menu in the flyout or one opened from FreeSystemTray's icon), makes WPF close
        /// the menu at once.
        /// </summary>
        private void OpenMenu(ContextMenu menu, string what, Action<bool> setOpen)
        {
            menu.Opened += (_, _) =>
            {
                setOpen(true);
                menu.Focus();
                AppLog.Write($"{what} opened");
            };
            menu.Closed += (_, _) =>
            {
                setOpen(false);
                AppLog.Write($"{what} closed");
            };
            NativeMethods.SetForegroundWindow(_hwnd);
            menu.IsOpen = true;
        }

        private static void AddCheck(ContextMenu menu, string header, bool isChecked, Action<bool> changed)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            item.Click += (_, _) => changed(item.IsChecked);
            menu.Items.Add(item);
        }

        /// <summary>One item per theme file; choosing one switches to it at once and saves it.</summary>
        private MenuItem ThemeMenu()
        {
            var menu = new MenuItem { Header = "_Theme" };
            foreach (ThemeInfo info in _themes.List())
            {
                var item = new MenuItem
                {
                    Header = MenuText(info.Name),
                    IsCheckable = true,
                    IsChecked = string.Equals(info.Id, _theme.Id, StringComparison.OrdinalIgnoreCase),
                    ToolTip = info.Path
                };
                string id = info.Id;
                item.Click += (_, _) => UpdateSettings(s => s.Theme = id);
                menu.Items.Add(item);
            }
            menu.IsEnabled = menu.Items.Count > 0;
            return menu;
        }

        /// <summary>Raised after settings were applied: changed here, in the settings window, or in the file.</summary>
        internal event EventHandler? SettingsApplied;

        /// <summary>The settings in use. Change them with <see cref="UpdateSettings"/>.</summary>
        internal AppSettings Settings => _settings;

        /// <summary>Changes the settings, saves them and applies them.</summary>
        internal void UpdateSettings(Action<AppSettings> change)
        {
            change(_settings);
            SaveSettings();
            ApplySettings(_settings);
        }

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

        #region Icons: the chevron, the hidden-icons flyout, dragging, the icon menu

        bool IIconCellHost.IsDraggingIcon => _iconDrag != null;

        void IIconCellHost.NoteIconClick()
        {
            ShowHide?.NoteTrayClick();
            _overflow.NoteClick();
        }

        void IIconCellHost.BeginIconDrag(IconCell cell, IconItem item)
        {
            if (_iconDrag != null) return;
            var drag = new IconDrag(this, _overflow, _model, GetWindow(cell) ?? this, item);
            drag.Ended += IconDrag_Ended;
            _iconDrag = drag;
            UpdateChevron();
            AppLog.Write($"Dragging icon {item} from the {(item.Placement == IconPlacement.Tray ? "tray" : "hidden icons")}");
            drag.Start();
        }

        private void IconDrag_Ended(IconDrag drag, IconSettings? changed)
        {
            if (_iconDrag != drag) return;
            _iconDrag = null;
            if (changed != null)
            {
                UpdateSettings(s => s.Icons = changed);
                IList<IconItem> group = drag.Item.Placement == IconPlacement.Tray ? _model.Tray : _model.Overflow;
                AppLog.Write($"Icon {drag.Item} dropped: {(drag.Item.Placement == IconPlacement.Tray ? "tray" : "hidden icons")}, place {group.IndexOf(drag.Item) + 1} of {group.Count}");
            }
            else
            {
                _model.EndPreview();
                AppLog.Write($"Icon {drag.Item} not moved");
            }
            OnArranged();
        }

        private void OnArranged()
        {
            UpdateChevron();
            if (_iconDrag == null && _model.Overflow.Count == 0)
            {
                if (_overflow.IsOpen) _overflow.CloseFlyout("no hidden icons left");
                SetExpanded(false, "no hidden icons left");
            }
        }

        /// <summary>The chevron shows while there are hidden icons, and during a drag, to drop icons on.</summary>
        private void UpdateChevron() => _chevron.IsShown = _model.Overflow.Count > 0 || _iconDrag != null;

        /// <summary>
        /// The chevron points to where the hidden icons appear: the flyout opens away from the nearest
        /// screen edge; a fixed-size grid expands away from its anchor corner, and points back once expanded.
        /// </summary>
        private void UpdateChevronAngle() =>
            _chevron.Angle = !_grid.IsFixed ? ChevronItem.AngleFor(_reportedEdge) : (_expandUp ? 0 : 180) + (_expanded ? 180 : 0);

        /// <summary>
        /// An expanded grid grows away from the window's anchor corner, as the window does: upwards
        /// from a bottom corner, so the grid's own icons and the chevron stay where they are.
        /// </summary>
        private void UpdateExpandDirection()
        {
            bool up = ExpandsUp(_settings);
            if (up == _expandUp) return;
            _expandUp = up;
            Resources["Icons.ExpandUp"] = up;
            UpdateChevronAngle();
        }

        private static bool ExpandsUp(AppSettings settings) =>
            (settings.Position?.Anchor ?? Corner.BottomRight) is Corner.BottomLeft or Corner.BottomRight;

        /// <summary>Shows or hides the hidden icons of a fixed-size grid in the tray, after its chevron.</summary>
        internal void SetExpanded(bool expanded, string reason)
        {
            if ((expanded && !_grid.IsFixed) || expanded == _expanded) return;
            _expanded = expanded;
            _expandedIcons.Collection = expanded ? _model.Overflow : null;
            UpdateChevronAngle();
            QueueBoundsReport();
            AppLog.Write($"Tray {(expanded ? "expanded" : "collapsed")} ({reason}): {_model.Overflow.Count} hidden icons");
        }

        /// <summary>The hidden icons are on screen: in the open flyout, or in the expanded tray.</summary>
        internal bool OverflowShown => _overflow.IsOpen || _expanded;

        /// <summary>The chevron's screen rectangle in physical pixels, empty while it is not shown.</summary>
        internal PixelRect ChevronBounds() =>
            IconList.ItemContainerGenerator.ContainerFromItem(_chevron) is DependencyObject container &&
            FindDescendant<ChevronCell>(container) is { IsVisible: true, ActualWidth: > 0 } cell
                ? ScreenBoundsOf(cell) : default;

        private FlyoutAnchor? OverflowAnchor() => _reportedBounds.IsEmpty ? null : new FlyoutAnchor(_reportedBounds, ChevronBounds(), _reportedEdge);

        /// <summary>Shows the hidden icons: expands a fixed-size grid, else opens the flyout.</summary>
        internal void OpenOverflow(string reason)
        {
            if (_model.Overflow.Count == 0 && _iconDrag == null) return;
            if (_grid.IsFixed) SetExpanded(true, reason);
            else _overflow.Open(reason);
        }

        internal void ToggleOverflow(string reason)
        {
            if (_overflow.IsOpen) _overflow.CloseFlyout(reason);
            else if (_expanded) SetExpanded(false, reason);
            else OpenOverflow(reason);
        }

        /// <summary>Shift+right-click on an icon: where it goes, as a rule for it.</summary>
        void IIconCellHost.ShowIconMenu(IconCell cell, IconItem item)
        {
            var menu = new ContextMenu { PlacementTarget = cell, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            menu.Items.Add(new MenuItem { Header = MenuText(item.Name), IsEnabled = false, FontWeight = FontWeights.SemiBold });
            menu.Items.Add(new Separator());
            IconPlacement automatic = IconArrangement.PlacementOf(
                new IconFacts(item.Identity, item.Icon.IsHidden, item.Icon.ExplorerPlacement), EffectiveRule.None, _model.Settings);
            IconPlacement? current = item.Rule.Placement;
            AddPlacementItem(menu, item, null, $"_Automatic ({Describe(automatic)})", current == null);
            AddPlacementItem(menu, item, IconPlacement.Tray, "Always in the _tray", current == IconPlacement.Tray);
            AddPlacementItem(menu, item, IconPlacement.Overflow, "In the hidden _icons", current == IconPlacement.Overflow);
            AddPlacementItem(menu, item, IconPlacement.Hidden, "_Hide", current == IconPlacement.Hidden);
            menu.Items.Add(new Separator());
            var rename = new MenuItem { Header = "_Name and tooltip…" };
            rename.Click += (_, _) => OpenSettings?.Invoke(item.Key);
            menu.Items.Add(rename);
            OpenMenu(menu, $"Icon menu for {item}", open => _iconMenuOpen = open);
        }

        private void AddPlacementItem(ContextMenu menu, IconItem item, IconPlacement? placement, string header, bool isChecked)
        {
            var menuItem = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            menuItem.Click += (_, _) => SetIconPlacement(item, placement);
            menu.Items.Add(menuItem);
        }

        private void SetIconPlacement(IconItem item, IconPlacement? placement)
        {
            UpdateSettings(s => s.Icons.EditRule(item.Key, r => r.Placement = placement));
            AppLog.Write($"Icon {item}: {(placement == null ? "placed automatically" : Describe(placement.Value))}");
        }

        private static string Describe(IconPlacement placement) => placement switch
        {
            IconPlacement.Tray => "in the tray",
            IconPlacement.Overflow => "in the hidden icons",
            _ => "hidden"
        };

        /// <summary>Menu text shown as it is: an underscore would be an access key.</summary>
        private static string MenuText(string text) => text.Replace("_", "__");

        #endregion
    }
}
