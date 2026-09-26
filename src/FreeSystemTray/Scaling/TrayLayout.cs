using System;

namespace FreeSystemTray.Scaling
{
    /// <summary>A theme's sizes at a tray size of 100%, in device-independent pixels.</summary>
    public sealed record TrayMetrics(double BorderThickness = 1, double GripSize = 10, double CornerRadius = 0, double Spacing = 0, double HighlightCornerRadius = 0)
    {
        public static readonly TrayMetrics Default = new();
    }

    /// <summary>
    /// The tray's sizes, in device-independent pixels, from the two independent scaling settings:
    /// the icon size, and the size of everything around the icons (the padding in each icon's cell,
    /// the grip, the border, the margins, and the theme's corner radius and spacing). At 100% with the
    /// default theme this is the Phase 1 look: 16 px icons in 24 px cells, a 10 px grip and a 1 px
    /// border, 30 px thick.
    /// </summary>
    public sealed record TrayLayout(
        double IconSize, double CellSize, double GripWidth, double BorderThickness, double MarginAlong, double MarginAcross,
        double CornerRadius = 0, double Spacing = 0, double HighlightCornerRadius = 0)
    {
        /// <summary>What an icon size of 100% is: the standard small icon at 100% Windows scaling.</summary>
        public const double BaseIconSize = 16;

        private const double BasePadding = 4, BaseMarginAlong = 3, BaseMarginAcross = 2;

        /// <summary>How far a rounded corner reaches in along the diagonal, as a share of its radius (1 - cos 45°).</summary>
        private const double CornerInset = 0.3;

        /// <summary>Sanity limits, not steps: any value in between is used as it is.</summary>
        public const double MinIconSize = 4, MaxIconSize = 512, MaxTrayScale = 20;

        /// <summary>The grip never gets narrower than this, or the tray could not be dragged or reach its menu.</summary>
        public const double MinGripWidth = 4;

        public static readonly TrayLayout Default = Compute(SizeValue.Percent(100), SizeValue.Percent(100));

        /// <summary>The tray's thickness: its height as a row of icons.</summary>
        public double Thickness => CellSize + 2 * (MarginAcross + BorderThickness);

        /// <param name="iconSize">A percentage of 16 px, or pixels.</param>
        /// <param name="traySize">
        /// A percentage scales the padding, grip, border, margins and the theme's corner radius and
        /// spacing. Pixels set the size of each icon's cell; the padding is what the icon leaves free,
        /// and the rest scales along with it.
        /// </param>
        /// <param name="metrics">The theme's sizes at 100%.</param>
        public static TrayLayout Compute(SizeValue iconSize, SizeValue traySize, TrayMetrics? metrics = null)
        {
            TrayMetrics m = metrics ?? TrayMetrics.Default;
            double icon = Math.Clamp(iconSize.Resolve(BaseIconSize), MinIconSize, MaxIconSize);
            // A cell smaller than its icon leaves no padding: the icons touch.
            double scale = traySize.IsPercent ? traySize.Value / 100 : (traySize.Value - icon) / (2 * BasePadding);
            scale = Math.Clamp(scale, 0, MaxTrayScale);
            double radius = m.CornerRadius * scale;
            // Keep the icons clear of rounded corners.
            double inset = radius * CornerInset;
            return new TrayLayout(
                icon,
                icon + 2 * BasePadding * scale,
                Math.Max(MinGripWidth, m.GripSize * scale),
                m.BorderThickness * scale,
                BaseMarginAlong * scale + inset,
                BaseMarginAcross * scale + inset,
                radius,
                m.Spacing * scale,
                m.HighlightCornerRadius * scale);
        }
    }
}
