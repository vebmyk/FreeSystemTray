using System;
using System.Collections.Generic;
using FreeSystemTray.ShowHide;
using FreeSystemTray.TrayHost;
using Xunit;

namespace FreeSystemTray.Tests
{
    public class HotkeyGestureTests
    {
        [Theory]
        [InlineData("Ctrl+Alt+Shift+T", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x54, "Ctrl+Alt+Shift+T")]
        [InlineData("shift + control + alt + t", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x54, "Ctrl+Alt+Shift+T")]
        [InlineData("Win+Shift+F12", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x7B, "Shift+Win+F12")]
        [InlineData("Ctrl+Alt+5", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x35, "Ctrl+Alt+5")]
        [InlineData("Alt+Space", HotkeyModifiers.Alt, 0x20, "Alt+Space")]
        [InlineData("Ctrl+Esc", HotkeyModifiers.Ctrl, 0x1B, "Ctrl+Escape")]
        [InlineData("Win+PgDn", HotkeyModifiers.Win, 0x22, "Win+PageDown")]
        [InlineData("Ctrl+NumPad7", HotkeyModifiers.Ctrl, 0x67, "Ctrl+NumPad7")]
        [InlineData("F9", HotkeyModifiers.None, 0x78, "F9")]
        [InlineData("Pause", HotkeyModifiers.None, 0x13, "Pause")]
        public void Gestures_are_parsed_and_written_back_in_a_fixed_order(string text, HotkeyModifiers modifiers, int vk, string canonical)
        {
            HotkeyGesture g = HotkeyGesture.Parse(text)!;
            Assert.Equal(modifiers, g.Modifiers);
            Assert.Equal(vk, g.VirtualKey);
            Assert.Equal(canonical, g.ToString());
            Assert.Equal(g, HotkeyGesture.Parse(g.ToString()));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("none")]
        public void Empty_means_no_hotkey(string? text) => Assert.Null(HotkeyGesture.Parse(text));

        [Theory]
        [InlineData("Hyper+T")]    // unknown modifier
        [InlineData("Ctrl+Alt+")]  // no key
        [InlineData("Ctrl+Bogus")] // unknown key
        [InlineData("Ctrl+42")]    // a number, not a key name
        [InlineData("T")]          // would take over typing a "t"
        [InlineData("Shift+T")]    // ...or a "T"
        [InlineData("Shift+Space")]
        public void Invalid_or_typing_gestures_are_rejected(string text) =>
            Assert.Throws<FormatException>(() => HotkeyGesture.Parse(text));
    }

    public class HotspotTests
    {
        private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

        [Theory]
        [InlineData(HotspotLocation.Left, 0, 0, 2, 1080)]
        [InlineData(HotspotLocation.Top, 0, 0, 1920, 2)]
        [InlineData(HotspotLocation.Right, 1918, 0, 1920, 1080)]
        [InlineData(HotspotLocation.Bottom, 0, 1078, 1920, 1080)]
        [InlineData(HotspotLocation.TopLeft, 0, 0, 8, 8)]
        [InlineData(HotspotLocation.TopRight, 1912, 0, 1920, 8)]
        [InlineData(HotspotLocation.BottomLeft, 0, 1072, 8, 1080)]
        [InlineData(HotspotLocation.BottomRight, 1912, 1072, 1920, 1080)]
        public void Zones_are_edge_strips_and_corner_squares_of_the_monitor(HotspotLocation location, int l, int t, int r, int b)
        {
            Assert.Equal(new PixelRect(l, t, r, b), Hotspot.Zone(location, Monitor, 1.0));
        }

        [Fact]
        public void The_pointer_in_the_screen_corner_is_in_a_corner_zone() =>
            Assert.True(Hotspot.Zone(HotspotLocation.BottomRight, Monitor, 1.0).Contains(new PixelPoint(1919, 1079)));

        [Fact]
        public void Zones_scale_with_the_monitors_DPI_and_follow_its_position()
        {
            var left = new PixelRect(-2560, 0, 0, 1440);
            Assert.Equal(new PixelRect(-3, 0, 0, 1440), Hotspot.Zone(HotspotLocation.Right, left, 1.5));
            Assert.Equal(new PixelRect(-2560, 0, -2548, 12), Hotspot.Zone(HotspotLocation.TopLeft, left, 1.5));
        }

        [Fact]
        public void No_hotspot_is_an_empty_zone() => Assert.True(Hotspot.Zone(HotspotLocation.None, Monitor, 1.0).IsEmpty);
    }

    public class AutoHidePolicyTests
    {
        private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

        private static AutoHidePolicy Policy(bool autoHide = true, double delay = 1.5, bool canShowAgain = true) => new()
        {
            AutoHide = autoHide,
            HideOnClickAway = true,
            HideDelay = S(delay),
            CanShowAgain = canShowAgain
        };

        [Fact]
        public void Shown_by_the_hotkey_it_waits_for_the_pointer_to_visit_before_the_timer_runs()
        {
            AutoHidePolicy p = Policy();
            p.Shown(ShowCause.Hotkey);
            Assert.False(p.HideOnTimer(S(0), false, false));
            Assert.False(p.HideOnTimer(S(10), false, false));
            Assert.False(p.HideOnTimer(S(11), true, false));  // visits
            Assert.False(p.HideOnTimer(S(12), false, false)); // leaves
            Assert.False(p.HideOnTimer(S(13.4), false, false));
            Assert.True(p.HideOnTimer(S(13.5), false, false));
        }

        [Fact]
        public void Shown_by_the_hotspot_the_timer_runs_once_the_pointer_leaves_it()
        {
            AutoHidePolicy p = Policy();
            p.Shown(ShowCause.Hotspot);
            Assert.False(p.HideOnTimer(S(0), true, false));
            Assert.False(p.HideOnTimer(S(1), false, false));
            Assert.True(p.HideOnTimer(S(2.5), false, false));
        }

        [Fact]
        public void Coming_back_restarts_the_countdown()
        {
            AutoHidePolicy p = Policy();
            p.Shown(ShowCause.Hotspot);
            Assert.False(p.HideOnTimer(S(0), false, false));
            Assert.False(p.HideOnTimer(S(1), true, false));
            Assert.False(p.HideOnTimer(S(2), false, false));
            Assert.False(p.HideOnTimer(S(3), false, false));
            Assert.True(p.HideOnTimer(S(3.5), false, false));
        }

        [Fact]
        public void A_menu_or_flyout_from_the_tray_keeps_it_and_the_countdown_starts_when_it_closes()
        {
            AutoHidePolicy p = Policy();
            p.Shown(ShowCause.Hotspot);
            Assert.False(p.HideOnTimer(S(0), false, true));
            Assert.False(p.HideOnTimer(S(60), false, true));
            Assert.False(p.HideOnTimer(S(61), false, false));
            Assert.True(p.HideOnTimer(S(62.5), false, false));
        }

        [Fact]
        public void A_new_shown_period_forgets_the_old_one()
        {
            AutoHidePolicy p = Policy();
            p.Shown(ShowCause.Hotspot);
            Assert.False(p.HideOnTimer(S(0), false, false));
            p.Shown(ShowCause.Hotkey);
            Assert.False(p.HideOnTimer(S(5), false, false));
        }

        [Theory]
        [InlineData(false, 1.5, true)] // always visible
        [InlineData(true, 0, true)]    // timer off
        [InlineData(true, 1.5, false)] // nothing could show it again
        public void Never_hides_on_a_timer_when(bool autoHide, double delay, bool canShowAgain)
        {
            AutoHidePolicy p = Policy(autoHide, delay, canShowAgain);
            p.Shown(ShowCause.Hotspot);
            Assert.False(p.HideOnTimer(S(0), false, false));
            Assert.False(p.HideOnTimer(S(100), false, false));
        }

        [Fact]
        public void Click_away_needs_auto_hide_the_option_and_a_way_back()
        {
            Assert.True(Policy().HidesOnClickAway);
            Assert.False(Policy(autoHide: false).HidesOnClickAway);
            Assert.False(Policy(canShowAgain: false).HidesOnClickAway);
            AutoHidePolicy p = Policy();
            p.HideOnClickAway = false;
            Assert.False(p.HidesOnClickAway);
        }
    }

    public class TrayInteractionTests
    {
        private static readonly IntPtr Menu = new(0x100), SubMenu = new(0x200), Flyout = new(0x300), AppWindow = new(0x400);
        private static readonly HashSet<IntPtr> Popups = new() { Menu, SubMenu, Flyout };
        private static bool IsPopup(IntPtr h) => Popups.Contains(h);
        private static bool AnyOpen(IntPtr h) => true;
        private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

        [Fact]
        public void A_menu_right_after_a_click_in_the_tray_belongs_to_it_until_it_closes()
        {
            var t = new TrayInteraction();
            t.Clicked(S(10));
            t.MenuStarted(Menu, S(10.3));
            Assert.True(t.Owns(Menu, IsPopup));
            Assert.True(t.IsOpen(IntPtr.Zero, AnyOpen, IsPopup));
            t.MenuEnded(Menu);
            Assert.False(t.Owns(Menu, IsPopup));
            Assert.False(t.IsOpen(IntPtr.Zero, AnyOpen, IsPopup));
        }

        [Fact]
        public void Other_menus_do_not()
        {
            var t = new TrayInteraction();
            t.MenuStarted(Menu, S(1));
            t.Clicked(S(10));
            t.MenuStarted(SubMenu, S(15));
            Assert.False(t.Owns(Menu, IsPopup));
            Assert.False(t.Owns(SubMenu, IsPopup));
            Assert.False(t.IsOpen(IntPtr.Zero, AnyOpen, IsPopup));
        }

        [Fact]
        public void A_submenu_opened_later_belongs_to_the_tray_while_its_parent_is_open()
        {
            var t = new TrayInteraction();
            t.Clicked(S(10));
            t.MenuStarted(Menu, S(10.2));
            t.MenuStarted(SubMenu, S(20));
            Assert.True(t.Owns(SubMenu, IsPopup));
        }

        [Fact]
        public void A_menu_that_vanished_without_its_end_event_no_longer_keeps_the_tray()
        {
            var t = new TrayInteraction();
            t.Clicked(S(10));
            t.MenuStarted(Menu, S(10.2));
            Assert.False(t.IsOpen(IntPtr.Zero, h => h != Menu, IsPopup));
        }

        [Fact]
        public void A_flyout_that_takes_the_foreground_after_a_click_belongs_to_the_tray_while_it_has_the_foreground()
        {
            var t = new TrayInteraction();
            t.Clicked(S(10));
            Assert.True(t.ForegroundChanged(Flyout, S(10.4)));
            Assert.True(t.Owns(Flyout, IsPopup));
            Assert.True(t.IsOpen(Flyout, AnyOpen, IsPopup));
            Assert.False(t.IsOpen(AppWindow, AnyOpen, IsPopup));
        }

        [Fact]
        public void An_app_window_opened_by_a_click_is_not_part_of_the_tray()
        {
            var t = new TrayInteraction();
            t.Clicked(S(10));
            Assert.True(t.ForegroundChanged(AppWindow, S(10.4)));
            Assert.False(t.Owns(AppWindow, IsPopup));
            Assert.False(t.IsOpen(AppWindow, AnyOpen, IsPopup));
        }

        [Fact]
        public void A_later_foreground_change_is_not_from_the_tray_and_ends_the_flyouts_claim()
        {
            var t = new TrayInteraction();
            t.Clicked(S(10));
            t.ForegroundChanged(Flyout, S(10.4));
            Assert.False(t.ForegroundChanged(AppWindow, S(30)));
            Assert.False(t.Owns(Flyout, IsPopup));
        }

        [Fact]
        public void Reset_forgets_menus_and_flyouts()
        {
            var t = new TrayInteraction();
            t.Clicked(S(10));
            t.MenuStarted(Menu, S(10.1));
            t.ForegroundChanged(Flyout, S(10.2));
            t.Reset();
            Assert.False(t.Owns(Menu, IsPopup));
            Assert.False(t.Owns(Flyout, IsPopup));
        }
    }
}
