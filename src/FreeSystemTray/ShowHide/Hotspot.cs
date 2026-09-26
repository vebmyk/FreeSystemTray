using System;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray.ShowHide
{
    public enum HotspotLocation
    {
        None,
        Left,
        Top,
        Right,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public enum TrayAnimation
    {
        None,
        Fade,

        /// <summary>The tray slides out through its own edge that faces the nearest screen edge.</summary>
        Slide
    }

    public static class Hotspot
    {
        /// <summary>Thickness of an edge hotspot, in device-independent pixels.</summary>
        public const double EdgeThickness = 2;

        /// <summary>Side of a corner hotspot, in device-independent pixels.</summary>
        public const double CornerSize = 8;

        /// <summary>
        /// The hotspot's rectangle on a monitor, in physical pixels: a strip along the whole edge, or a
        /// square in the corner. Empty for <see cref="HotspotLocation.None"/>. The monitor's full bounds
        /// are used, not its work area: the pointer stops at the edge of the screen.
        /// </summary>
        public static PixelRect Zone(HotspotLocation location, PixelRect monitor, double dpiScale)
        {
            int edge = Math.Max(1, (int)Math.Round(EdgeThickness * dpiScale));
            int corner = Math.Max(1, (int)Math.Round(CornerSize * dpiScale));
            PixelRect m = monitor;
            return location switch
            {
                HotspotLocation.Left => new(m.Left, m.Top, m.Left + edge, m.Bottom),
                HotspotLocation.Top => new(m.Left, m.Top, m.Right, m.Top + edge),
                HotspotLocation.Right => new(m.Right - edge, m.Top, m.Right, m.Bottom),
                HotspotLocation.Bottom => new(m.Left, m.Bottom - edge, m.Right, m.Bottom),
                HotspotLocation.TopLeft => new(m.Left, m.Top, m.Left + corner, m.Top + corner),
                HotspotLocation.TopRight => new(m.Right - corner, m.Top, m.Right, m.Top + corner),
                HotspotLocation.BottomLeft => new(m.Left, m.Bottom - corner, m.Left + corner, m.Bottom),
                HotspotLocation.BottomRight => new(m.Right - corner, m.Bottom - corner, m.Right, m.Bottom),
                _ => default
            };
        }
    }
}
