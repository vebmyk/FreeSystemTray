using System.Collections.Generic;
using FreeSystemTray.Placement;
using FreeSystemTray.TrayHost;
using Xunit;

namespace FreeSystemTray.Tests
{
    public class PlacementMathTests
    {
        // 1920x1080 with Explorer's 40 px taskbar at the bottom (the Phase 1 test machine), and a
        // 1280x1024 monitor to its left without a taskbar.
        private static readonly MonitorInfo Primary = new(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), @"\\.\DISPLAY1", true);
        private static readonly MonitorInfo Second = new(new PixelRect(-1280, 0, 0, 1024), new PixelRect(-1280, 0, 0, 1024), @"\\.\DISPLAY2", false);
        private static readonly IReadOnlyList<MonitorInfo> Both = new[] { Primary, Second };
        private static readonly PixelSize Tray = new(402, 30);

        private static PixelRect At(int left, int top) => PixelRect.FromPointAndSize(new PixelPoint(left, top), Tray);

        [Theory]
        [InlineData(800, 5, ScreenEdge.Top)]
        [InlineData(800, 990, ScreenEdge.Bottom)]
        [InlineData(3, 500, ScreenEdge.Left)]
        [InlineData(1500, 500, ScreenEdge.Right)]
        public void NearestEdge_is_the_work_area_edge_closest_to_the_window_center(int left, int top, ScreenEdge expected)
        {
            Assert.Equal(expected, PlacementMath.NearestEdge(At(left, top), Primary.WorkArea));
        }

        [Theory]
        [InlineData(100, 100, Corner.TopLeft)]
        [InlineData(1500, 100, Corner.TopRight)]
        [InlineData(100, 900, Corner.BottomLeft)]
        [InlineData(1500, 900, Corner.BottomRight)]
        public void AnchorFor_picks_the_corner_facing_the_nearest_screen_corner(int left, int top, Corner expected)
        {
            Assert.Equal(expected, PlacementMath.AnchorFor(At(left, top), Primary.WorkArea));
        }

        [Theory]
        [InlineData(Corner.TopLeft)]
        [InlineData(Corner.TopRight)]
        [InlineData(Corner.BottomLeft)]
        [InlineData(Corner.BottomRight)]
        public void RectFromAnchor_inverts_AnchorPoint(Corner corner)
        {
            PixelRect window = At(123, 456);
            Assert.Equal(window, PlacementMath.RectFromAnchor(PlacementMath.AnchorPoint(window, corner), corner, window.Size));
        }

        [Fact]
        public void A_bottom_right_anchor_keeps_that_corner_when_the_tray_grows()
        {
            PixelRect before = At(1500, 1000);
            PixelPoint anchor = PlacementMath.AnchorPoint(before, Corner.BottomRight);
            PixelRect after = PlacementMath.RectFromAnchor(anchor, Corner.BottomRight, new PixelSize(426, 30));
            Assert.Equal(new PixelRect(1476, 1000, 1902, 1030), after);
        }

        [Fact]
        public void Snap_moves_an_edge_within_the_threshold_onto_the_work_area_edge()
        {
            // Bottom 5 px above the taskbar; left 7 px from the screen edge.
            PixelRect snapped = PlacementMath.Snap(new PixelRect(7, 1005, 409, 1035), Primary, 12);
            Assert.Equal(new PixelRect(0, 1010, 402, 1040), snapped);
        }

        [Fact]
        public void Snap_leaves_the_window_alone_beyond_the_threshold()
        {
            var window = new PixelRect(20, 990, 422, 1020);
            Assert.Equal(window, PlacementMath.Snap(window, Primary, 12));
        }

        [Fact]
        public void Snap_also_snaps_to_the_monitor_edge_under_the_taskbar()
        {
            // 35 px below the work area, 5 px above the bottom of the monitor.
            PixelRect snapped = PlacementMath.Snap(new PixelRect(500, 1045, 902, 1075), Primary, 12);
            Assert.Equal(new PixelRect(500, 1050, 902, 1080), snapped);
        }

        [Fact]
        public void KeepInside_moves_the_window_the_least_distance()
        {
            Assert.Equal(new PixelRect(1518, 100, 1920, 130), PlacementMath.KeepInside(new PixelRect(1600, 100, 2002, 130), Primary.Bounds));
            Assert.Equal(new PixelRect(0, 0, 402, 30), PlacementMath.KeepInside(new PixelRect(-50, -10, 352, 20), Primary.Bounds));
        }

        [Fact]
        public void KeepInside_aligns_an_axis_where_the_window_is_too_large_to_the_start_of_the_area()
        {
            Assert.Equal(new PixelRect(0, 50, 2000, 80), PlacementMath.KeepInside(new PixelRect(100, 50, 2100, 80), Primary.Bounds));
        }

        [Fact]
        public void KeepOnScreen_keeps_the_window_on_the_monitor_it_overlaps_most()
        {
            // Mostly on the left monitor, sticking out over its left edge.
            PixelRect kept = PlacementMath.KeepOnScreen(new PixelRect(-1300, 500, -898, 530), Both);
            Assert.Equal(new PixelRect(-1280, 500, -878, 530), kept);
        }

        [Fact]
        public void KeepOnScreen_brings_back_a_window_left_on_a_disconnected_monitor()
        {
            PixelRect kept = PlacementMath.KeepOnScreen(new PixelRect(-900, 500, -498, 530), new[] { Primary });
            Assert.Equal(new PixelRect(0, 500, 402, 530), kept);
        }

        [Fact]
        public void MonitorFor_point_prefers_the_containing_monitor_then_the_nearest()
        {
            Assert.Same(Second, PlacementMath.MonitorFor(new PixelPoint(-10, 10), Both));
            Assert.Same(Primary, PlacementMath.MonitorFor(new PixelPoint(3000, 500), Both));
            Assert.Same(Second, PlacementMath.MonitorFor(new PixelPoint(-5000, 500), Both));
        }

        [Fact]
        public void Place_without_a_saved_position_uses_the_bottom_right_of_the_primary_work_area()
        {
            PixelRect placed = PlacementMath.Place(null, Tray, new[] { Second, Primary });
            Assert.Equal(new PixelRect(1920 - 12 - 402, 1040 - 12 - 30, 1920 - 12, 1040 - 12), placed);
        }

        [Fact]
        public void Place_puts_the_saved_corner_back()
        {
            var saved = new SavedPosition(-800, 40, Corner.TopLeft);
            Assert.Equal(new PixelRect(-800, 40, -398, 70), PlacementMath.Place(saved, Tray, Both));
        }

        [Fact]
        public void Place_keeps_a_position_from_a_missing_monitor_on_screen()
        {
            var saved = new SavedPosition(-800, 40, Corner.TopLeft);
            Assert.Equal(new PixelRect(0, 40, 402, 70), PlacementMath.Place(saved, Tray, new[] { Primary }));
        }

        private static readonly PixelRect WorkArea = new(0, 0, 1920, 1040);

        [Fact]
        public void The_flyout_opens_above_a_tray_at_the_bottom_centred_on_the_chevron()
        {
            var tray = new PixelRect(1000, 998, 1400, 1028);
            var chevron = new PixelRect(1200, 1001, 1224, 1025);
            Assert.Equal(new PixelRect(1172, 914, 1252, 994), PlacementMath.FlyoutRect(tray, chevron, ScreenEdge.Bottom, new PixelSize(80, 80), WorkArea, 4));
        }

        [Fact]
        public void The_flyout_does_not_reach_past_the_end_of_the_tray()
        {
            // The chevron at the tray's right end: the flyout ends where the tray does.
            var tray = new PixelRect(1554, 998, 1908, 1028);
            var chevron = new PixelRect(1880, 1001, 1904, 1025);
            Assert.Equal(new PixelRect(1852, 962, 1908, 994), PlacementMath.FlyoutRect(tray, chevron, ScreenEdge.Bottom, new PixelSize(56, 32), WorkArea, 4));
            // Longer than the tray: centred, and inside the work area.
            Assert.Equal(new PixelRect(1420, 962, 1920, 994), PlacementMath.FlyoutRect(tray, chevron, ScreenEdge.Bottom, new PixelSize(500, 32), WorkArea, 4));
        }

        [Theory]
        [InlineData(ScreenEdge.Top, 1172, 1032)]
        [InlineData(ScreenEdge.Left, 1404, 948)]
        [InlineData(ScreenEdge.Right, 916, 948)]
        public void The_flyout_opens_on_the_side_away_from_the_trays_edge(ScreenEdge edge, int left, int top)
        {
            var tray = new PixelRect(1000, 500, 1400, 1028);
            var chevron = new PixelRect(1200, 1001, 1224, 1025);
            var workArea = new PixelRect(0, 0, 1920, 1200);
            PixelRect r = PlacementMath.FlyoutRect(tray, chevron, edge, new PixelSize(80, 80), workArea, 4);
            Assert.Equal((left, top), (r.Left, r.Top));
        }
    }
}
