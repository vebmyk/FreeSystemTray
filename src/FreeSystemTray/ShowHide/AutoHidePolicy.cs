using System;
using System.Collections.Generic;

namespace FreeSystemTray.ShowHide
{
    public enum ShowCause
    {
        Startup,
        Hotkey,
        Hotspot,
        Command,
        Menu,
        Settings
    }

    /// <summary>
    /// When an auto-hiding tray hides by itself. Pure logic: <see cref="ShowHideController"/> feeds it
    /// what it observes. Times are monotonic.
    /// </summary>
    internal sealed class AutoHidePolicy
    {
        private bool _visited;
        private TimeSpan? _idleSince;

        public bool AutoHide { get; set; }
        public bool HideOnClickAway { get; set; }

        /// <summary>How long after the pointer leaves the tray it hides. Zero or less: never on a timer.</summary>
        public TimeSpan HideDelay { get; set; }

        /// <summary>
        /// Whether the tray can be brought back (a registered hotkey or a hotspot). Without that it
        /// never hides by itself, or it would be lost until a restart.
        /// </summary>
        public bool CanShowAgain { get; set; }

        /// <summary>Starts a new period of being shown.</summary>
        public void Shown(ShowCause cause)
        {
            // Shown by the hotspot, the pointer is already where the user wants the tray. Shown by the
            // hotkey, the timer waits until the pointer has visited the tray, so it does not vanish
            // before the user gets there; click-away still hides it.
            _visited = cause == ShowCause.Hotspot;
            _idleSince = null;
        }

        /// <summary>
        /// The user clicked in the tray: it has been visited, even if the pointer came and went between
        /// two of the timer's checks (a quick right-click, then away to the menu).
        /// </summary>
        public void Clicked() => _visited = true;

        /// <summary>Called periodically while the tray is shown.</summary>
        /// <param name="pointerOnTray">The pointer is over the tray window, or in the hotspot that showed it.</param>
        /// <param name="inUse">A menu or flyout opened from the tray is open, or the tray is being dragged or its own menu is open.</param>
        public bool HideOnTimer(TimeSpan now, bool pointerOnTray, bool inUse)
        {
            if (pointerOnTray) _visited = true;
            if (pointerOnTray || inUse)
            {
                _idleSince = null;
                return false;
            }
            if (!AutoHide || !CanShowAgain || HideDelay <= TimeSpan.Zero || !_visited) return false;
            _idleSince ??= now;
            return now - _idleSince.Value >= HideDelay;
        }

        /// <summary>Whether a click outside the tray, or a switch to another window, hides it.</summary>
        public bool HidesOnClickAway => AutoHide && HideOnClickAway && CanShowAgain;
    }

    /// <summary>
    /// The windows that an interaction with the tray opened: popup menus and flyouts that appear right
    /// after a click on one of its icons. A click in one of them is not a click away, and the tray
    /// stays while one is open. Pure logic; window handles are only compared, and window state is
    /// asked through the predicates.
    /// </summary>
    internal sealed class TrayInteraction
    {
        /// <summary>How soon after a click in the tray a new menu or foreground window counts as opened by it.</summary>
        public static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

        private readonly HashSet<IntPtr> _menus = new();
        private TimeSpan? _lastClick;
        private IntPtr _foreground;

        public void Clicked(TimeSpan at) => _lastClick = at;

        /// <summary>A Win32 popup menu opened (EVENT_SYSTEM_MENUPOPUPSTART).</summary>
        public void MenuStarted(IntPtr menu, TimeSpan at)
        {
            // Submenus open later, while their parent is still open.
            if (IsRecentClick(at) || _menus.Count > 0) _menus.Add(menu);
        }

        public void MenuEnded(IntPtr menu) => _menus.Remove(menu);

        /// <summary>Another window came to the foreground. Returns whether it came from a click in the tray.</summary>
        public bool ForegroundChanged(IntPtr window, TimeSpan at)
        {
            _foreground = IsRecentClick(at) ? window : IntPtr.Zero;
            return _foreground != IntPtr.Zero;
        }

        /// <summary>
        /// Whether a click on <paramref name="rootWindow"/> belongs to the interaction: it is one of
        /// the tray's menus, or the popup (a flyout) that came to the foreground after a click in the
        /// tray. An ordinary app window opened by a click does not count.
        /// </summary>
        public bool Owns(IntPtr rootWindow, Func<IntPtr, bool> isPopup) =>
            rootWindow != IntPtr.Zero && (_menus.Contains(rootWindow) || (rootWindow == _foreground && isPopup(rootWindow)));

        /// <summary>Whether a menu opened from the tray is open, or its flyout is still in the foreground.</summary>
        public bool IsOpen(IntPtr foreground, Func<IntPtr, bool> isOpenMenu, Func<IntPtr, bool> isPopup)
        {
            _menus.RemoveWhere(m => !isOpenMenu(m)); // in case a menu's end was missed
            return _menus.Count > 0 || (foreground != IntPtr.Zero && foreground == _foreground && isPopup(foreground));
        }

        public void Reset()
        {
            _menus.Clear();
            _foreground = IntPtr.Zero;
        }

        private bool IsRecentClick(TimeSpan at) => _lastClick is TimeSpan click && at >= click && at - click <= Grace;
    }
}
