using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace FreeSystemTray.Theming
{
    /// <summary>
    /// A theme's colours and tooltip settings as WPF resources. These keys are what a theme's XAML
    /// dictionary can override (README, "Themes"):
    /// <list type="bullet">
    /// <item>Brushes: Tray.Background, Tray.Foreground, Tray.BorderBrush, Tray.GripBrush, Icon.HoverBrush, Icon.PressedBrush,
    /// ToolTip.Background, ToolTip.Foreground, ToolTip.BorderBrush.</item>
    /// <item>ToolTip.BorderThickness and ToolTip.Padding (Thickness), ToolTip.CornerRadius (CornerRadius),
    /// ToolTip.FontFamily (FontFamily), ToolTip.FontSize (double).</item>
    /// <item>The implicit ToolTip style (see TrayStyles.xaml).</item>
    /// </list>
    /// </summary>
    internal static class ThemeResources
    {
        /// <summary>The lowest alpha the tray's background gets: Windows lets clicks through fully transparent pixels of a layered window.</summary>
        public const byte MinBackgroundAlpha = 1;

        /// <param name="acrylicShown">Windows' acrylic is drawing the tint, so the tray's own background stays (almost) transparent.</param>
        /// <param name="blurUnavailable">The theme asks for a blur that Windows' transparency setting rules out: use the colour, opaque.</param>
        public static ResourceDictionary Build(Theme t, bool acrylicShown, bool blurUnavailable)
        {
            Color background = acrylicShown ? Color.FromArgb(MinBackgroundAlpha, 0, 0, 0)
                : blurUnavailable ? Color.FromRgb(t.Background.R, t.Background.G, t.Background.B)
                : BackgroundColor(t);
            return new ResourceDictionary
            {
                ["Tray.Background"] = Brush(background),
                ["Tray.Foreground"] = Brush(t.Foreground),
                ["Tray.BorderBrush"] = Brush(t.BorderColor),
                ["Tray.GripBrush"] = Brush(t.GripColor),
                ["Icon.HoverBrush"] = Brush(t.HoverColor),
                ["Icon.PressedBrush"] = Brush(t.PressedColor),
                ["ToolTip.Background"] = Brush(t.ToolTipBackground),
                ["ToolTip.Foreground"] = Brush(t.ToolTipForeground),
                ["ToolTip.BorderBrush"] = Brush(t.ToolTipBorder),
                ["ToolTip.BorderThickness"] = new Thickness(t.ToolTipBorderThickness),
                ["ToolTip.CornerRadius"] = new CornerRadius(t.ToolTipCornerRadius),
                ["ToolTip.FontFamily"] = new FontFamily(t.ToolTipFontFamily),
                ["ToolTip.FontSize"] = t.ToolTipFontSize,
                ["ToolTip.Padding"] = new Thickness(t.ToolTipPadding, t.ToolTipPadding / 2, t.ToolTipPadding, t.ToolTipPadding / 2)
            };
        }

        /// <summary>The background colour with the theme's opacity applied, never fully transparent.</summary>
        public static Color BackgroundColor(Theme t) =>
            Color.FromArgb((byte)Math.Max(MinBackgroundAlpha, Math.Round(t.Background.A * t.BackgroundOpacity)), t.Background.R, t.Background.G, t.Background.B);

        /// <summary>
        /// Reads a theme's XAML resource dictionary. XAML can create any object, so it can run code:
        /// only use XAML files you trust.
        /// </summary>
        /// <exception cref="XamlParseException">Not valid XAML.</exception>
        /// <exception cref="IOException">The file could not be read.</exception>
        public static ResourceDictionary LoadXaml(string path)
        {
            using var stream = File.OpenRead(path);
            object loaded = XamlReader.Load(stream, new ParserContext { BaseUri = new Uri(path) });
            return loaded as ResourceDictionary ?? throw new XamlParseException($"{path} is not a ResourceDictionary");
        }

        private static SolidColorBrush Brush(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }
    }
}
