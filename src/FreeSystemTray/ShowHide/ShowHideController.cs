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
    /// <summary>
    /// What <c>--toggle</c>, <c>--show</c>, <c>--hide</c> and <c>--open-settings</c> send to the running
    /// instance (the wParam of <see cref="ShowHideController.CommandMessage"/>).
    /// </summary>
    public enum ShowHideCommand
    {
        Toggle = 0,
        Show = 1,
        Hide = 2,

        /// <summary>Show the tray and open the settings window (also sent when the exe is started again).</summary>
        Settings = 3
    }

    /// <summary>
    /// Shows and hides the tray window: the global hotkey, the hotspot, auto-hide (click-away and the
    /// timer), commands from other instances, and the animations. It also steps the window behind a
    /// full-screen app in front on its monitor, as Explorer's taskbar does. Hiding only hides the
    /// window; the tray host keeps running and icons keep registering. While the window is hidden or
    /// covered by a full-screen app, <see cref="ITrayHost.TrayWindowVisible"/> is false, so Explorer
    /// answers position queries and menus and flyouts open at the taskbar. Everything runs on the UI
    /// thread.
    /// </summary>
    internal sealed class ShowHideController : IDisposable
    {
        private const int HotkeyId = 0x4654;
        private static readonly TimeSpan AutoHideTick = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan HotspotPoll = TimeSpan.FromMilliseconds(100);

        /// <summary>Registered message another instance posts to the tray window; wParam is a <see cref="ShowHideCommand"/>.</summary>
        public static readonly uint CommandMessage = NativeMethods.RegisterWindowMessage("FreeSystemTray.ShowHide");

        /// <summary>What shell hook windows receive (RegisterShellHookWindow).</summary>
        private static readonly uint ShellHookMessage = NativeMethods.RegisterWindowMessage("SHELLHOOK");

        private readonly TrayWindow _window;
        private readonly ITrayHost _host;
        private readonly Action<string> _log;
        private readonly bool _trace;
        private readonly AutoHidePolicy _policy = new();
        private readonly TrayInteraction _interaction = new();
        private readonly FullScreenTracker _fullScreen = new();
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
        private bool _shellHookRegistered;

        /// <summary>Behind a full-screen window, but brought up over it (hotkey or --show) until it is activated again.</summary>
        private bool _inFrontOfFullScreen;

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
        public PixelRect HotspotZone => CurrentHotspot();

        /// <summary>
        /// Creates the window's handle, registers the hotkey and shows the window, or leaves it hidden
        /// when auto-hide is on.
        /// </summary>
        public void Start()
        {
            _hwnd = new WindowInteropHelper(_window).EnsureHandle();
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            // Full-screen windows and activations, for stepping behind full-screen apps.
            _shellHookRegistered = ShellHookMessage != 0 && NativeMethods.RegisterShellHookWindow(_hwnd);
            if (!_shellHookRegistered) _log("RegisterShellHookWindow failed; the tray will not step behind full-screen apps");
            NoteActivated(NativeMethods.GetForegroundWindow());
            ApplySettings(_settings, starting: true);
            if (_settings.AutoHide && CanShowAgain)
            {
                _shown = false;
                _log("Starting hidden (auto-hide)");
                StartHotspotPolling();
            }
            else
            {
                _shown = true;
                _shownBy = ShowCause.Startup;
                _policy.Shown(ShowCause.Startup);
                _window.Show();
                UpdateWatchers();
            }
            UpdateTrayWindowVisible();
        }

        /// <summary>
        /// Apps and Explorer see the tray window only while it is shown and not covered by a
        /// full-screen window; otherwise Explorer answers their position queries.
        /// </summary>
        private void UpdateTrayWindowVisible() =>
            _host.TrayWindowVisible = _shown && (_window.BehindFullScreen == IntPtr.Zero || _inFrontOfFullScreen);

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
            string reason = cause.ToString().ToLowerInvariant();
            bool behind = _window.BehindFullScreen != IntPtr.Zero;
            // Behind a full-screen window the tray is shown but covered: bring it up over that window.
            if (!_shown || (behind && !_inFrontOfFullScreen)) Show(cause);
            // Brought up over it: always visible means back behind it, not hidden.
            else if (behind && !_settings.AutoHide) SendBehindFullScreen(reason);
            else Hide(reason);
        }

        public void Show(ShowCause cause)
        {
            if (_disposed) return;
            bool explicitly = cause is ShowCause.Hotkey or ShowCause.Command or ShowCause.Menu;
            if (_shown)
            {
                if (!explicitly) return;
                _window.BringToTop();
                if (_window.BehindFullScreen != IntPtr.Zero && !_inFrontOfFullScreen)
                {
                    // Over the full-screen window until the hotkey, a click elsewhere, or activating it again.
                    _inFrontOfFullScreen = true;
                    UpdateTrayWindowVisible();
                    UpdateWatchers();
                    _log($"Brought in front of the full-screen window ({Describe(cause)})");
                }
                return;
            }
            _shown = true;
            _shownBy = cause;
            _inFrontOfFullScreen = _window.BehindFullScreen != IntPtr.Zero;
            UpdateTrayWindowVisible();
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
            _animator.Run(1, kind, AnimationDuration(), _window.Edge, () =>
            {
                _window.SetEffectsSuspended(false); // the blur is back once the tray is fully shown
                _window.RefreshBounds();
            });
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
            _inFrontOfFullScreen = false;
            UpdateTrayWindowVisible();
            UpdateWatchers();
            _window.SetEffectsSuspended(true); // a blur would stay at full strength behind the fading tray
            _animator.Run(0, AnimationKind(), AnimationDuration(), _window.Edge, () =>
            {
                _window.Hide();
                _animator.Jump(1, TrayAnimation.None, _window.Edge);
                StartHotspotPolling();
            });
            _log($"Hidden ({reason})");
        }

        /// <summary>Puts a tray that was brought up over a full-screen window back behind it.</summary>
        private void SendBehindFullScreen(string reason)
        {
            if (!_inFrontOfFullScreen || _window.BehindFullScreen == IntPtr.Zero) return;
            _window.SetBehindFullScreen(_window.BehindFullScreen);
            _inFrontOfFullScreen = false;
            UpdateTrayWindowVisible();
            UpdateWatchers();
            _log($"Back behind the full-screen window ({reason})");
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
                    case ShowHideCommand.Settings:
                        Show(ShowCause.Command);
                        _window.OpenSettings?.Invoke(null);
                        break;
                    default: Toggle(ShowCause.Command); break;
                }
            }
            else if (msg == ShellHookMessage && ShellHookMessage != 0)
            {
                OnShellHook((int)wParam.ToInt64(), lParam);
            }
            else if (msg == NativeMethods.WM_DISPLAYCHANGE)
            {
                UpdateFullScreen(); // the tray may be on another monitor now
            }
            return IntPtr.Zero;
        }

        #endregion

        #region Full-screen apps

        /// <summary>
        /// Shell hook messages (RegisterShellHookWindow). Windows 8 and later report windows entering
        /// and leaving full screen, with the same judgement that makes Explorer's taskbar step back.
        /// </summary>
        private void OnShellHook(int code, IntPtr window)
        {
            switch (code)
            {
                case NativeMethods.HSHELL_FULLSCREENENTER:
                    _fullScreen.Entered(window);
                    // In case its activation was not reported (a window made visible without being shown).
                    if (window == NativeMethods.GetForegroundWindow()) NoteActivated(window);
                    if (_trace) _log($"SHOWHIDE full screen entered by {DescribeWindow(window)}");
                    break;
                case NativeMethods.HSHELL_FULLSCREENEXIT:
                    _fullScreen.Exited(window);
                    if (_trace) _log($"SHOWHIDE full screen left by 0x{window.ToInt64():X}");
                    break;
                case NativeMethods.HSHELL_WINDOWACTIVATED:
                case NativeMethods.HSHELL_RUDEAPPACTIVATED:
                    bool noted = NoteActivated(window);
                    if (_trace) _log($"SHOWHIDE activated {DescribeWindow(window)}{(noted ? "" : " (ignored: ours or always-on-top)")}");
                    if (!noted) return;
                    break;
                default:
                    return;
            }
            UpdateFullScreen();
        }

        /// <summary>
        /// Always-on-top windows (flyouts, menus, Explorer's taskbar) and the tray's own windows (its
        /// menu takes the foreground) are left out: opening one over a full-screen app changes nothing.
        /// </summary>
        private bool NoteActivated(IntPtr window)
        {
            if (window == IntPtr.Zero || IsOurs(window) || IsTopmost(window)) return false;
            _fullScreen.Activated(window);
            return true;
        }

        /// <summary>Steps behind the full-screen window in front on the tray's monitor, or comes back on top.</summary>
        private void UpdateFullScreen()
        {
            if (_disposed) return;
            IntPtr trayMonitor = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            // A hidden window (an app's message window taking the foreground for its menu) is on no
            // monitor in particular: activating it ends any full screen.
            IntPtr inFront = _fullScreen.InFront(NativeMethods.IsWindow,
                w => !NativeMethods.IsWindowVisible(w) || NativeMethods.MonitorFromWindow(w, NativeMethods.MONITOR_DEFAULTTONEAREST) == trayMonitor,
                NativeMethods.IsIconic);
            IntPtr before = _window.BehindFullScreen;
            if (inFront == IntPtr.Zero && before == IntPtr.Zero) return;
            // Also when it is the same window: activated again, it covers the tray again.
            _window.SetBehindFullScreen(inFront);
            _inFrontOfFullScreen = false;
            UpdateTrayWindowVisible();
            UpdateWatchers();
            if (inFront != before)
                _log(inFront == IntPtr.Zero ? "No full-screen window in front any more; always-on-top again"
                    : $"Full-screen window in front on the tray's monitor: {DescribeWindow(inFront)}; staying behind it");
        }

        private static bool IsTopmost(IntPtr hwnd) =>
            (NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;

        #endregion

        #region Hotkey

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
            // A tray brought up over a full-screen window goes back behind it on a click elsewhere.
            bool clickAway = (autoHiding && _settings.HideOnClickAway) || (_shown && !_disposed && _inFrontOfFullScreen);

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
                          (_shownBy == ShowCause.Hotspot && CurrentHotspot().Contains(cursor));
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
            if (ours || fromTray || !_shown) return;
            if (_policy.HidesOnClickAway) Hide($"click away at {point.X},{point.Y} on {DescribeWindow(root)}");
            else SendBehindFullScreen($"click at {point.X},{point.Y} on {DescribeWindow(root)}");
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

        internal static bool IsOpenMenu(IntPtr hwnd) => NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd);

        /// <summary>
        /// A menu or flyout rather than an app's main window: shown, not cloaked, and always-on-top.
        /// Windows 10's volume, network and power flyouts and apps' popup menus are all topmost.
        /// </summary>
        internal static bool IsPopup(IntPtr hwnd)
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
            _hotspotArmed = !(NativeMethods.GetCursorPos(out NativeMethods.POINT p) && CurrentHotspot().Contains(new PixelPoint(p.X, p.Y)));
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
            PixelRect zone = CurrentHotspot();
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
            if (_window.BehindFullScreen != IntPtr.Zero)
            {
                _log($"Hotspot reached, but a full-screen window is in front ({DescribeWindow(_window.BehindFullScreen)}); not showing");
                return;
            }
            Show(ShowCause.Hotspot);
        }

        /// <summary>The hotspot on the tray's monitor, in physical pixels (empty if none is set).</summary>
        private PixelRect CurrentHotspot()
        {
            if (_settings.Hotspot == HotspotLocation.None) return default;
            var monitors = Monitors.GetAll();
            if (monitors.Count == 0) return default;
            PixelRect tray = _window.ScreenBounds;
            MonitorInfo monitor = tray.IsEmpty ? monitors[0] : PlacementMath.MonitorFor(tray, monitors);
            return Hotspot.Zone(_settings.Hotspot, monitor.Bounds, VisualTreeHelper.GetDpi(_window).DpiScaleX);
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
            if (_shellHookRegistered) NativeMethods.DeregisterShellHookWindow(_hwnd);
        }
    }
}
