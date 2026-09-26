using System;

namespace FreeSystemTray.Scaling
{
    /// <summary>
    /// The tray's sizes, in device-independent pixels, from the two independent scaling settings:
    /// the icon size, and the size of everything around the icons (the padding in each icon's cell,
    /// the grip, the border and the margins). At 100% both give the Phase 1 look: 16 px icons in 24 px
    /// cells, a 10 px grip and a 1 px border, 30 px thick.
    /// </summary>
    public sealed record TrayLayout(double IconSize, double CellSize, double GripWidth, double BorderThickness, double MarginAlong, double MarginAcross)
    {
        /// <summary>What an icon size of 100% is: the standard small icon at 100% Windows scaling.</summary>
        public const double BaseIconSize = 16;

        private const double BasePadding = 4, BaseGrip = 10, BaseBorder = 1, BaseMarginAlong = 3, BaseMarginAcross = 2;

        /// <summary>Sanity limits, not steps: any value in between is used as it is.</summary>
        public const double MinIconSize = 4, MaxIconSize = 512, MaxTrayScale = 20;

        /// <summary>The grip never gets narrower than this, or the tray could not be dragged or reach its menu.</summary>
        public const double MinGripWidth = 4;

        public static readonly TrayLayout Default = Compute(SizeValue.Percent(100), SizeValue.Percent(100));

        /// <summary>The tray's thickness: its height as a row of icons.</summary>
        public double Thickness => CellSize + 2 * (MarginAcross + BorderThickness);

        /// <param name="iconSize">A percentage of 16 px, or pixels.</param>
        /// <param name="traySize">
        /// A percentage scales the padding, grip, border and margins. Pixels set the size of each
        /// icon's cell; the padding is what the icon leaves free, and the rest scales along with it.
        /// </param>
        public static TrayLayout Compute(SizeValue iconSize, SizeValue traySize)
        {
            double icon = Math.Clamp(iconSize.Resolve(BaseIconSize), MinIconSize, MaxIconSize);
            // A cell smaller than its icon leaves no padding: the icons touch.
            double scale = traySize.IsPercent ? traySize.Value / 100 : (traySize.Value - icon) / (2 * BasePadding);
            scale = Math.Clamp(scale, 0, MaxTrayScale);
            return new TrayLayout(
                icon,
                icon + 2 * BasePadding * scale,
                Math.Max(MinGripWidth, BaseGrip * scale),
                BaseBorder * scale,
                BaseMarginAlong * scale,
                BaseMarginAcross * scale);
        }
    }
}
