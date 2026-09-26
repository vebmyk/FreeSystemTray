using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FreeSystemTray.Placement;
using FreeSystemTray.Settings;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray.ShowHide
{
    /// <summary>What <c>--toggle</c>, <c>--show</c> and <c>--hide</c> send to the running instance (the wParam of <see cref="ShowHideController.CommandMessage"/>).</summary>
    public enum ShowHideCommand
    {
        Toggle = 0,
        Show = 1,
        Hide = 2
    }

    /// <summary>
    /// Shows and hides the tray window: the global hotkey, the hotspot, auto-hide (click-away and the
    /// timer), commands from other instances, and the animations. Hiding only hides the window; the
    /// tray host keeps running and icons keep registering. While the window is hidden,
    /// <see cref="ITrayHost.TrayWindowVisible"/> is false, so Explorer answers position queries and
    /// menus and flyouts open at the taskbar. Everything runs on the UI thread.
    /// </summary>
    internal sealed class ShowHideController : IDisposable
    {
        private const int HotkeyId = 0x4654;
        private static readonly TimeSpan AutoHideTick = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan HotspotPoll = TimeSpan.FromMilliseconds(100);

        /// <summary>Registered message another instance posts to the tray window; wParam is a <see cref="ShowHideCommand"/>.</summary>
        public static readonly uint CommandMessage = NativeMethods.RegisterWindowMessage("FreeSystemTray.ShowHide");

        private readonly TrayWindow _window;
        private readonly ITrayHost _host;
        private readonly Action<string> _log;
        private readonly bool _trace;
        private readonly AutoHidePolicy _policy = new();
        private readonly TrayInteraction _interaction = new();
        private readonly TrayAnimator _animator;
        private readonly DispatcherTimer _autoHideTimer;
        private readonly DispatcherTimer _hotspotTimer;
        private readonly uint _pid = (uint)Environment.ProcessId;
        private ShowHideSettings _settings;
        private IntPtr _hwnd;
        private HotkeyGesture? _hotkey;
        private bool _hotkeyRegistered;
        private string? _hotkeyProblem;
        private ShellWatcher? _watcher;
        private bool _shown;
        private ShowCause _shownBy;
        private TimeSpan? _inHotspotSince;
        private bool _hotspotArmed;
        private bool _disposed;

        /// <param name="trace">Log every click, foreground change and menu the shell watcher reports (--verbose).</param>
        public ShowHideController(TrayWindow window, ITrayHost host, ShowHideSettings settings, Action<string> log, bool trace = false)
        {
            _window = window;
            _host = host;
            _settings = settings;
            _log = log;
            _trace = trace;
            _animator = new TrayAnimator(window.AnimatedContent);
            _autoHideTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AutoHideTick };
            _autoHideTimer.Tick += (_, _) => AutoHideTick_Tick();
            _hotspotTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = HotspotPoll };
            _hotspotTimer.Tick += (_, _) => HotspotTimer_Tick();
        }

        /// <summary>Whether the window is shown or being shown (false while it hides).</summary>
        public bool IsShown => _shown;

        public bool AutoHide => _settings.AutoHide;

        /// <summary>The registered hotkey, or null.</summary>
        public HotkeyGesture? Hotkey => _hotkeyRegistered ? _hotkey : null;

        /// <summary>Why the configured hotkey is not registered, or null.</summary>
        public string? HotkeyProblem => _hotkeyProblem;

        /// <summary>Whether a hidden tray can be brought back without a restart: a registered hotkey or a hotspot.</summary>
        public bool CanShowAgain => _hotkeyRegistered || _settings.Hotspot != HotspotLocation.None;

        /// <summary>The hotspot's rectangle in physical pixels, empty if none (diagnostics).</summary>
        public PixelRect HotspotZone => CurrentHotspot(out _);

        /// <summary>
        /// Creates the window's handle, registers the hotkey and shows the window, or leaves it hidden
        /// when auto-hide is on.
        /// </summary>
        public void Start()
        {
            _hwnd = new WindowInteropHelper(_window).EnsureHandle();
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            ApplySettings(_settings, starting: true);
            if (_settings.AutoHide && CanShowAgain)
            {
                _shown = false;
                _host.TrayWindowVisible = false;
                _log("Starting hidden (auto-hide)");
                StartHotspotPolling();
            }
            else
            {
                _shown = true;
                _shownBy = ShowCause.Startup;
                _host.TrayWindowVisible = true;
                _policy.Shown(ShowCause.Startup);
                _window.Show();
                UpdateWatchers();
            }
        }

        /// <summary>Applies changed settings (the grip menu, or the settings file edited while running).</summary>
        public void ApplySettings(ShowHideSettings settings) => ApplySettings(settings, starting: false);

        private void ApplySettings(ShowHideSettings settings, bool starting)
        {
            bool wasAutoHide = _settings.AutoHide;
            _settings = settings;
            RegisterHotkey(settings.Hotkey);

            _policy.AutoHide = settings.AutoHide;
            _policy.HideOnClickAway = settings.HideOnClickAway;
            _policy.HideDelay = TimeSpan.FromMilliseconds(Math.Clamp(settings.HideDelayMs, 0, 600_000));
            _policy.CanShowAgain = CanShowAgain;
            _log($"Show/hide: {(settings.AutoHide ? $"auto-hide (click-away {(settings.HideOnClickAway ? "on" : "off")}, timer {_policy.HideDelay.TotalMilliseconds:0} ms)" : "always visible")}, " +
                 $"hotspot {settings.Hotspot}, animation {(AnimationKind() == settings.Animation ? $"{settings.Animation} {AnimationDuration().TotalMilliseconds:0} ms" : "off (\"Show animations in Windows\" is off)")}");
            if (settings.AutoHide && !CanShowAgain)
                _log("Auto-hide is on, but there is no working hotkey and no hotspot to show the tray again; it stays visible");

            if (!starting)
            {
                // Back to always visible, or nothing left that could show it: show it now.
                if (!_shown && ((wasAutoHide && !settings.AutoHide) || !CanShowAgain)) Show(ShowCause.Settings);
                else if (_shown && settings.AutoHide && !wasAutoHide) _policy.Shown(ShowCause.Settings);
                if (!_shown) StartHotspotPolling();
            }
            UpdateWatchers();
        }

        #region Commands

        public void Toggle(ShowCause cause)
        {
            if (_shown) Hide(cause.ToString().ToLowerInvariant());
            else Show(cause);
        }

        public void Show(ShowCause cause)
        {
            if (_disposed) return;
            if (_shown)
            {
                if (cause is ShowCause.Hotkey or ShowCause.Hotspot or ShowCause.Command) _window.BringToTop();
                return;
            }
            _shown = true;
            _shownBy = cause;
            _host.TrayWindowVisible = true;
            _hotspotTimer.Stop();
            _interaction.Reset();
            _policy.Shown(cause);

            TrayAnimation kind = AnimationKind();
            if (!_window.IsVisible)
            {
                _window.PrepareToShow();
                _animator.Jump(0, kind, _window.Edge);
                _window.Show();
            }
            _window.BringToTop();
            _animator.Run(1, kind, AnimationDuration(), _window.Edge, _window.RefreshBounds);
            UpdateWatchers();
            _log($"Shown ({Describe(cause)})");
        }

        /// <summary>Hides the window. Refused when nothing could show it again.</summary>
        public void Hide(string reason)
        {
            if (_disposed || !_shown) return;
            if (!CanShowAgain)
            {
                _log($"Not hiding ({reason}): no working hotkey and no hotspot could show the tray again");
                return;
            }
            _shown = false;
            _host.TrayWindowVisible = false;
            UpdateWatchers();
            _animator.Run(0, AnimationKind(), AnimationDuration(), _window.Edge, () =>
            {
                _window.Hide();
                _animator.Jump(1, TrayAnimation.None, _window.Edge);
                StartHotspotPolling();
            });
            _log($"Hidden ({reason})");
        }

        /// <summary>A mouse button went down or up on one of the tray's icons.</summary>
        public void NoteTrayClick() => _interaction.Clicked(ShellWatcher.Now());

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt64() == HotkeyId)
            {
                handled = true;
                Toggle(ShowCause.Hotkey);
            }
            else if (msg == CommandMessage && CommandMessage != 0)
            {
                handled = true;
                switch ((ShowHideCommand)wParam.ToInt32())
                {
                    case ShowHideCommand.Show: Show(ShowCause.Command); break;
                    case ShowHideCommand.Hide: Hide("command"); break;
                    default: Toggle(ShowCause.Command); break;
                }
            }
            return IntPtr.Zero;
        }

        private void RegisterHotkey(string? text)
        {
            HotkeyGesture? gesture;
            try
            {
                gesture = HotkeyGesture.Parse(text);
            }
            catch (FormatException ex)
            {
                UnregisterHotkey();
                _hotkey = null;
                _hotkeyProblem = ex.Message;
                _log("Hotkey not registered: " + ex.Message);
                return;
            }
            if (_hotkeyRegistered && gesture == _hotkey) return;
            UnregisterHotkey();
            _hotkey = gesture;
            _hotkeyProblem = null;
            if (gesture == null)
            {
                _log("No hotkey configured");
                return;
            }
            if (NativeMethods.RegisterHotKey(_hwnd, HotkeyId, (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)gesture.VirtualKey))
            {
                _hotkeyRegistered = true;
                _log($"Hotkey {gesture} registered");
                return;
            }
            int error = Marshal.GetLastWin32Error();
            _hotkeyProblem = error == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"{gesture} is already in use by another program"
                : $"{gesture} could not be registered (error {error})";
            _log("Hotkey not registered: " + _hotkeyProblem);
        }

        private void UnregisterHotkey()
        {
            if (!_hotkeyRegistered) return;
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _hotkeyRegistered = false;
        }

        #endregion

        #region Auto-hide

        /// <summary>Starts or stops the auto-hide timer and the shell watcher to match the current state.</summary>
        private void UpdateWatchers()
        {
            bool autoHiding = _shown && !_disposed && _settings.AutoHide && CanShowAgain;
            bool timer = autoHiding && _policy.HideDelay > TimeSpan.Zero;
            bool clickAway = autoHiding && _settings.HideOnClickAway;

            if (timer) _autoHideTimer.Start();
            else _autoHideTimer.Stop();

            // The watcher also sees the menus and flyouts that keep the tray shown, so the timer needs it too.
            if (!(timer || clickAway))
            {
                StopWatcher();
            }
            else if (_watcher == null || _watcher.WatchesClicks != clickAway)
            {
                StopWatcher();
                _watcher = new ShellWatcher(_window.Dispatcher, clickAway, OnButtonDown, OnForeground, OnMenu, _log);
            }
        }

        private void StopWatcher()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        private void AutoHideTick_Tick()
        {
            if (!_shown || !NativeMethods.GetCursorPos(out NativeMethods.POINT p)) return;
            var cursor = new PixelPoint(p.X, p.Y);
            bool onTray = _window.ScreenBounds.Contains(cursor) ||
                          (_shownBy == ShowCause.Hotspot && CurrentHotspot(out _).Contains(cursor));
            // Not "our window has the foreground": it keeps it after its own menu closes (IsBusy covers the menu).
            bool inUse = _window.IsBusy || _interaction.IsOpen(NativeMethods.GetForegroundWindow(), IsOpenMenu, IsPopup);
            if (_policy.HideOnTimer(ShellWatcher.Now(), onTray, inUse))
                Hide($"{_policy.HideDelay.TotalMilliseconds:0} ms after the pointer left");
        }

        private void OnButtonDown(ShellWatcher source, PixelPoint point)
        {
            if (source != _watcher) return;
            IntPtr root = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = point.X, Y = point.Y }), NativeMethods.GA_ROOT);
            // Our windows include the tray's own menu and tooltips.
            bool ours = IsOurs(root), fromTray = !ours && _interaction.Owns(root, IsPopup);
            if (_trace) _log($"SHOWHIDE click at {point.X},{point.Y} on {DescribeWindow(root)}: {(ours ? "ours" : fromTray ? "opened from the tray" : "away")}");
            if (ours || fromTray || !_shown || !_policy.HidesOnClickAway) return;
            Hide($"click away at {point.X},{point.Y} on {DescribeWindow(root)}");
        }

        private void OnForeground(ShellWatcher source, IntPtr window, TimeSpan at)
        {
            if (source != _watcher) return;
            bool fromTray = _interaction.ForegroundChanged(window, at);
            if (_trace) _log($"SHOWHIDE foreground {DescribeWindow(window)}{(IsPopup(window) ? " (popup)" : "")}: {(fromTray ? "opened from the tray" : "elsewhere")}");
            if (fromTray || !_shown || IsOurs(window) || !_policy.HidesOnClickAway) return;
            Hide($"switched to {DescribeWindow(window)}");
        }

        private void OnMenu(ShellWatcher source, IntPtr menu, bool started, TimeSpan at)
        {
            if (source != _watcher) return;
            if (started) _interaction.MenuStarted(menu, at);
            bool fromTray = _interaction.Owns(menu, IsPopup);
            if (!started) _interaction.MenuEnded(menu);
            if (_trace) _log($"SHOWHIDE menu {(started ? "opened" : "closed")} 0x{menu.ToInt64():X}: {(fromTray ? "from the tray" : "not the tray's")}");
        }

        private bool IsOurs(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == _pid;
        }

        private static bool IsOpenMenu(IntPtr hwnd) => NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd);

        /// <summary>
        /// A menu or flyout rather than an app's main window: shown, not cloaked, and always-on-top.
        /// Windows 10's volume, network and power flyouts and apps' popup menus are all topmost.
        /// </summary>
        private static bool IsPopup(IntPtr hwnd)
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return false;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            return (NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;
        }

        #endregion

        #region Hotspot

        private void StartHotspotPolling()
        {
            if (_shown || _disposed || _settings.Hotspot == HotspotLocation.None)
            {
                _hotspotTimer.Stop();
                return;
            }
            // The pointer must leave the hotspot first if it is there already (for example after the
            // hotkey hid a tray the hotspot had shown).
            _hotspotArmed = !(NativeMethods.GetCursorPos(out NativeMethods.POINT p) && CurrentHotspot(out _).Contains(new PixelPoint(p.X, p.Y)));
            _inHotspotSince = null;
            _hotspotTimer.Start();
        }

        private void HotspotTimer_Tick()
        {
            if (_shown || _settings.Hotspot == HotspotLocation.None)
            {
                _hotspotTimer.Stop();
                return;
            }
            if (!NativeMethods.GetCursorPos(out NativeMethods.POINT p)) return;
            PixelRect zone = CurrentHotspot(out PixelRect monitor);
            if (!zone.Contains(new PixelPoint(p.X, p.Y)))
            {
                _hotspotArmed = true;
                _inHotspotSince = null;
                return;
            }
            if (!_hotspotArmed) return;
            TimeSpan now = ShellWatcher.Now();
            _inHotspotSince ??= now;
            if (now - _inHotspotSince.Value < TimeSpan.FromMilliseconds(Math.Clamp(_settings.HotspotDelayMs, 0, 10_000))) return;

            _hotspotArmed = false;
            _inHotspotSince = null;
            // Like Explorer's auto-hidden taskbar: not over a full-screen game or video.
            if (ForegroundCovers(monitor))
            {
                _log($"Hotspot reached, but a full-screen window is in front ({DescribeWindow(NativeMethods.GetForegroundWindow())}); not showing");
                return;
            }
            Show(ShowCause.Hotspot);
        }

        /// <summary>The hotspot on the tray's monitor, in physical pixels (empty if none is set).</summary>
        private PixelRect CurrentHotspot(out PixelRect monitorBounds)
        {
            monitorBounds = default;
            if (_settings.Hotspot == HotspotLocation.None) return default;
            var monitors = Monitors.GetAll();
            if (monitors.Count == 0) return default;
            PixelRect tray = _window.ScreenBounds;
            MonitorInfo monitor = tray.IsEmpty ? monitors[0] : PlacementMath.MonitorFor(tray, monitors);
            monitorBounds = monitor.Bounds;
            return Hotspot.Zone(_settings.Hotspot, monitor.Bounds, VisualTreeHelper.GetDpi(_window).DpiScaleX);
        }

        /// <summary>
        /// A full-screen game, video or slide show in front on that monitor. A maximized window covers
        /// the whole monitor too when the taskbar auto-hides, so maximized windows do not count.
        /// </summary>
        private static bool ForegroundCovers(PixelRect monitor)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero || NativeMethods.IsZoomed(fg) || !NativeMethods.GetWindowRect(fg, out NativeMethods.RECT r)) return false;
            if (ClassOf(fg) is "Progman" or "WorkerW") return false; // the desktop covers the monitor too
            return new PixelRect(r.Left, r.Top, r.Right, r.Bottom).Contains(monitor);
        }

        #endregion

        // ClientAreaAnimation is Windows' "Show animations in Windows" (Ease of Access).
        private TrayAnimation AnimationKind() => SystemParameters.ClientAreaAnimation ? _settings.Animation : TrayAnimation.None;

        private TimeSpan AnimationDuration() => TimeSpan.FromMilliseconds(Math.Clamp(_settings.AnimationMs, 0, 2000));

        private string Describe(ShowCause cause) => cause switch
        {
            ShowCause.Hotkey => $"hotkey {_hotkey}",
            ShowCause.Hotspot => $"hotspot {_settings.Hotspot}",
            _ => cause.ToString().ToLowerInvariant()
        };

        private static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(64);
            return NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        private static string DescribeWindow(IntPtr hwnd)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            string process = ProcessInfo.ImagePath(pid) is string exe ? Path.GetFileName(exe) : $"pid {pid}";
            return $"0x{hwnd.ToInt64():X} [{ClassOf(hwnd)}] {process}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _autoHideTimer.Stop();
            _hotspotTimer.Stop();
            _animator.Stop();
            StopWatcher();
            UnregisterHotkey();
        }
    }
}
