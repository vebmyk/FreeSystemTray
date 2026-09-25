using System;
using System.Collections.Generic;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray.Placement
{
    /// <summary>The corner of the window that stays in place when the tray changes size.</summary>
    public enum Corner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    /// <summary>A monitor in physical pixels. The work area excludes Explorer's taskbar and other appbars.</summary>
    public sealed record MonitorInfo(PixelRect Bounds, PixelRect WorkArea, string DeviceName, bool IsPrimary);

    /// <summary>Placement rules for the floating tray window. Everything is in physical pixels.</summary>
    public static class PlacementMath
    {
        /// <summary>Gap between the default position and the corner of the primary work area.</summary>
        public const int DefaultMargin = 12;

        /// <summary>
        /// The edge of <paramref name="area"/> nearest to the window's center. Reported to apps as the
        /// taskbar edge, so flyouts open on the side of the window that faces the screen.
        /// </summary>
        public static ScreenEdge NearestEdge(PixelRect window, PixelRect area)
        {
            PixelPoint c = window.Center;
            var distances = new (ScreenEdge Edge, long Distance)[]
            {
                (ScreenEdge.Bottom, area.Bottom - c.Y),
                (ScreenEdge.Top, c.Y - area.Top),
                (ScreenEdge.Right, area.Right - c.X),
                (ScreenEdge.Left, c.X - area.Left)
            };
            (ScreenEdge edge, long best) = distances[0];
            foreach ((ScreenEdge e, long d) in distances)
            {
                if (d < best) (edge, best) = (e, d);
            }
            return edge;
        }

        /// <summary>
        /// The corner to keep fixed: the one facing the nearest corner of the work area, so a tray
        /// placed at the right edge grows to the left and one at the bottom grows upwards.
        /// </summary>
        public static Corner AnchorFor(PixelRect window, PixelRect area)
        {
            PixelPoint c = window.Center, a = area.Center;
            bool right = c.X > a.X, bottom = c.Y > a.Y;
            return bottom ? (right ? Corner.BottomRight : Corner.BottomLeft) : (right ? Corner.TopRight : Corner.TopLeft);
        }

        /// <summary>The position of <paramref name="corner"/>. Right and bottom corners use the exclusive Right/Bottom.</summary>
        public static PixelPoint AnchorPoint(PixelRect window, Corner corner) => corner switch
        {
            Corner.TopLeft => new(window.Left, window.Top),
            Corner.TopRight => new(window.Right, window.Top),
            Corner.BottomLeft => new(window.Left, window.Bottom),
            _ => new(window.Right, window.Bottom)
        };

        public static PixelRect RectFromAnchor(PixelPoint anchor, Corner corner, PixelSize size) => corner switch
        {
            Corner.TopLeft => new(anchor.X, anchor.Y, anchor.X + size.Width, anchor.Y + size.Height),
            Corner.TopRight => new(anchor.X - size.Width, anchor.Y, anchor.X, anchor.Y + size.Height),
            Corner.BottomLeft => new(anchor.X, anchor.Y - size.Height, anchor.X + size.Width, anchor.Y),
            _ => new(anchor.X - size.Width, anchor.Y - size.Height, anchor.X, anchor.Y)
        };

        /// <summary>
        /// Moves the window onto the nearest edge of the monitor's work area or bounds when an edge of
        /// the window is within <paramref name="threshold"/> pixels of it. Each axis snaps independently.
        /// </summary>
        public static PixelRect Snap(PixelRect window, MonitorInfo monitor, int threshold)
        {
            PixelRect wa = monitor.WorkArea, b = monitor.Bounds;
            int dx = SmallestWithin(threshold, wa.Left - window.Left, b.Left - window.Left, wa.Right - window.Right, b.Right - window.Right);
            int dy = SmallestWithin(threshold, wa.Top - window.Top, b.Top - window.Top, wa.Bottom - window.Bottom, b.Bottom - window.Bottom);
            return window.Offset(dx, dy);
        }

        private static int SmallestWithin(int threshold, params int[] deltas)
        {
            int best = 0;
            bool found = false;
            foreach (int d in deltas)
            {
                if (Math.Abs(d) > threshold) continue;
                if (!found || Math.Abs(d) < Math.Abs(best))
                {
                    best = d;
                    found = true;
                }
            }
            return best;
        }

        /// <summary>Moves the window the least distance that puts it fully inside <paramref name="area"/>.</summary>
        public static PixelRect KeepInside(PixelRect window, PixelRect area)
        {
            int dx = window.Width > area.Width || window.Left < area.Left ? area.Left - window.Left
                : window.Right > area.Right ? area.Right - window.Right : 0;
            int dy = window.Height > area.Height || window.Top < area.Top ? area.Top - window.Top
                : window.Bottom > area.Bottom ? area.Bottom - window.Bottom : 0;
            return window.Offset(dx, dy);
        }

        /// <summary>Keeps the window fully on the monitor it overlaps most, or the nearest one if it is off every monitor.</summary>
        public static PixelRect KeepOnScreen(PixelRect window, IReadOnlyList<MonitorInfo> monitors) =>
            monitors.Count == 0 ? window : KeepInside(window, MonitorFor(window, monitors).Bounds);

        public static MonitorInfo MonitorFor(PixelRect window, IReadOnlyList<MonitorInfo> monitors)
        {
            MonitorInfo? best = null;
            long bestArea = 0;
            foreach (MonitorInfo m in monitors)
            {
                long area = m.Bounds.IntersectionArea(window);
                if (area > bestArea)
                {
                    best = m;
                    bestArea = area;
                }
            }
            return best ?? MonitorFor(window.Center, monitors);
        }

        public static MonitorInfo MonitorFor(PixelPoint point, IReadOnlyList<MonitorInfo> monitors)
        {
            MonitorInfo best = monitors[0];
            long bestDistance = long.MaxValue;
            foreach (MonitorInfo m in monitors)
            {
                long d = m.Bounds.DistanceSquaredTo(point);
                if (d < bestDistance)
                {
                    best = m;
                    bestDistance = d;
                }
            }
            return best;
        }

        /// <summary>
        /// Where the window goes at startup or after the monitors change: the saved corner position,
        /// kept on screen; without one, the bottom-right corner of the primary work area.
        /// </summary>
        public static PixelRect Place(SavedPosition? saved, PixelSize size, IReadOnlyList<MonitorInfo> monitors)
        {
            if (monitors.Count == 0) return PixelRect.FromPointAndSize(new PixelPoint(0, 0), size);
            if (saved == null)
            {
                MonitorInfo primary = monitors[0];
                foreach (MonitorInfo m in monitors)
                {
                    if (m.IsPrimary) primary = m;
                }
                var corner = new PixelPoint(primary.WorkArea.Right - DefaultMargin, primary.WorkArea.Bottom - DefaultMargin);
                return KeepOnScreen(RectFromAnchor(corner, Corner.BottomRight, size), monitors);
            }
            return KeepOnScreen(RectFromAnchor(new PixelPoint(saved.X, saved.Y), saved.Anchor, size), monitors);
        }
    }

    /// <summary>A saved window position: where the anchor corner is, in physical pixels.</summary>
    public sealed record SavedPosition(int X, int Y, Corner Anchor);
}
