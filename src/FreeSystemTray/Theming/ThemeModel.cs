using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace FreeSystemTray.Theming
{
    public enum TrayBlur
    {
        None,

        /// <summary>What is behind the tray, blurred, seen through the background colour.</summary>
        Blur,

        /// <summary>Windows 10's acrylic: blurred and tinted with the background colour.</summary>
        Acrylic
    }

    public enum GripPosition
    {
        /// <summary>Left for a row or grid, top for a column (the layout setting).</summary>
        Auto,
        Left,
        Top,
        Right,
        Bottom
    }

    /// <summary>
    /// A theme file as written. Every value is optional: a missing one comes from the theme named in
    /// <see cref="BasedOn"/>, and in the end from the built-in Dark look. Sizes are device-independent
    /// pixels at a tray size of 100%, and scale with the tray size.
    /// </summary>
    public sealed class ThemeFile
    {
        public string? Name { get; set; }
        public string? BasedOn { get; set; }

        /// <summary>The colour of what the tray draws itself, such as the hidden-icons chevron.</summary>
        public string? Foreground { get; set; }

        public BackgroundPart? Background { get; set; }
        public BorderPart? Border { get; set; }
        public GripPart? Grip { get; set; }
        public IconsPart? Icons { get; set; }

        [JsonPropertyName("tooltip")]
        public ToolTipPart? ToolTip { get; set; }

        /// <summary>A XAML resource dictionary, relative to the theme file, that can override the theme's resources.</summary>
        public string? Xaml { get; set; }

        public sealed class BackgroundPart
        {
            public string? Color { get; set; }
            public double? Opacity { get; set; }
            public TrayBlur? Blur { get; set; }
        }

        public sealed class BorderPart
        {
            public string? Color { get; set; }
            public double? Thickness { get; set; }
            public double? CornerRadius { get; set; }
        }

        public sealed class GripPart
        {
            public string? Color { get; set; }
            public double? Size { get; set; }
            public GripPosition? Position { get; set; }
        }

        public sealed class IconsPart
        {
            /// <summary>No longer part of a theme (now the layout setting); read only to say so.</summary>
            public JsonElement? Layout { get; set; }

            /// <summary>No longer part of a theme (now the layout setting); read only to say so.</summary>
            public JsonElement? GridColumns { get; set; }

            public double? Spacing { get; set; }
            public string? Hover { get; set; }
            public string? Pressed { get; set; }
            public double? HighlightCornerRadius { get; set; }
        }

        public sealed class ToolTipPart
        {
            public string? Background { get; set; }
            public string? Foreground { get; set; }
            public string? Border { get; set; }
            public double? BorderThickness { get; set; }
            public double? CornerRadius { get; set; }
            public string? FontFamily { get; set; }
            public double? FontSize { get; set; }
            public double? Padding { get; set; }
        }
    }

    /// <summary>A theme with every value filled in. <see cref="Default"/> is the Dark look of Phases 1 to 3.</summary>
    public sealed record Theme
    {
        public string Id { get; init; } = "Dark";
        public string Name { get; init; } = "Dark";

        public Color Foreground { get; init; } = Color.FromRgb(0xE0, 0xE0, 0xE0);

        public Color Background { get; init; } = Color.FromRgb(0x2B, 0x2B, 0x2B);
        public double BackgroundOpacity { get; init; } = 1;
        public TrayBlur Blur { get; init; } = TrayBlur.None;

        public Color BorderColor { get; init; } = Color.FromRgb(0x6A, 0x6A, 0x6A);
        public double BorderThickness { get; init; } = 1;
        public double CornerRadius { get; init; }

        public Color GripColor { get; init; } = Color.FromRgb(0x4A, 0x4A, 0x4A);
        public double GripSize { get; init; } = 10;
        public GripPosition GripPosition { get; init; } = GripPosition.Auto;

        public double IconSpacing { get; init; }
        public Color HoverColor { get; init; } = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        public Color PressedColor { get; init; } = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
        public double HighlightCornerRadius { get; init; }

        public Color ToolTipBackground { get; init; } = Color.FromRgb(0x2B, 0x2B, 0x2B);
        public Color ToolTipForeground { get; init; } = Colors.White;
        public Color ToolTipBorder { get; init; } = Color.FromRgb(0x6A, 0x6A, 0x6A);
        public double ToolTipBorderThickness { get; init; } = 1;
        public double ToolTipCornerRadius { get; init; }
        public string ToolTipFontFamily { get; init; } = "Segoe UI";
        public double ToolTipFontSize { get; init; } = 12;
        public double ToolTipPadding { get; init; } = 6;

        /// <summary>Full path of the theme's XAML resource dictionary, or null.</summary>
        public string? XamlPath { get; init; }

        public static readonly Theme Default = new();

        /// <summary>
        /// The grip's actual side: <see cref="GripPosition.Auto"/> puts it at the start of the icons,
        /// on top of a <paramref name="column"/>.
        /// </summary>
        public GripPosition GripPositionFor(bool column) =>
            GripPosition != GripPosition.Auto ? GripPosition : column ? GripPosition.Top : GripPosition.Left;

        /// <summary>
        /// This theme with <paramref name="file"/>'s values on top. Invalid values are left out (they
        /// keep this theme's value) and described in <paramref name="problems"/>; numbers outside
        /// sensible limits are clamped.
        /// </summary>
        public Theme With(ThemeFile file, string id, string? directory, List<string> problems)
        {
            Theme t = this with { Id = id, Name = string.IsNullOrWhiteSpace(file.Name) ? id : file.Name.Trim() };
            t = t with { Foreground = ParseColor(file.Foreground, t.Foreground, "foreground", problems) };
            if (file.Background is { } bg)
            {
                t = t with
                {
                    Background = ParseColor(bg.Color, t.Background, "background.color", problems),
                    BackgroundOpacity = Clamp(bg.Opacity, t.BackgroundOpacity, 0, 1),
                    Blur = bg.Blur ?? t.Blur
                };
            }
            if (file.Border is { } border)
            {
                t = t with
                {
                    BorderColor = ParseColor(border.Color, t.BorderColor, "border.color", problems),
                    BorderThickness = Clamp(border.Thickness, t.BorderThickness, 0, 50),
                    CornerRadius = Clamp(border.CornerRadius, t.CornerRadius, 0, 200)
                };
            }
            if (file.Grip is { } grip)
            {
                t = t with
                {
                    GripColor = ParseColor(grip.Color, t.GripColor, "grip.color", problems),
                    GripSize = Clamp(grip.Size, t.GripSize, 0, 200),
                    GripPosition = grip.Position ?? t.GripPosition
                };
            }
            if (file.Icons is { } icons)
            {
                if (icons.Layout != null || icons.GridColumns != null)
                    problems.Add("icons.layout and icons.gridColumns are no longer part of a theme: set the layout in the settings instead (columns and rows); ignored");
                t = t with
                {
                    IconSpacing = Clamp(icons.Spacing, t.IconSpacing, 0, 200),
                    HoverColor = ParseColor(icons.Hover, t.HoverColor, "icons.hover", problems),
                    PressedColor = ParseColor(icons.Pressed, t.PressedColor, "icons.pressed", problems),
                    HighlightCornerRadius = Clamp(icons.HighlightCornerRadius, t.HighlightCornerRadius, 0, 200)
                };
            }
            if (file.ToolTip is { } tip)
            {
                t = t with
                {
                    ToolTipBackground = ParseColor(tip.Background, t.ToolTipBackground, "tooltip.background", problems),
                    ToolTipForeground = ParseColor(tip.Foreground, t.ToolTipForeground, "tooltip.foreground", problems),
                    ToolTipBorder = ParseColor(tip.Border, t.ToolTipBorder, "tooltip.border", problems),
                    ToolTipBorderThickness = Clamp(tip.BorderThickness, t.ToolTipBorderThickness, 0, 20),
                    ToolTipCornerRadius = Clamp(tip.CornerRadius, t.ToolTipCornerRadius, 0, 50),
                    ToolTipFontFamily = string.IsNullOrWhiteSpace(tip.FontFamily) ? t.ToolTipFontFamily : tip.FontFamily.Trim(),
                    ToolTipFontSize = Clamp(tip.FontSize, t.ToolTipFontSize, 4, 72),
                    ToolTipPadding = Clamp(tip.Padding, t.ToolTipPadding, 0, 50)
                };
            }
            if (!string.IsNullOrWhiteSpace(file.Xaml))
                t = t with { XamlPath = directory == null ? file.Xaml : Path.GetFullPath(Path.Combine(directory, file.Xaml)) };
            return t;
        }

        /// <summary>This theme as a complete file (no basedOn), for export.</summary>
        public ThemeFile ToFile() => new()
        {
            Name = Name,
            Foreground = Format(Foreground),
            Background = new() { Color = Format(Background), Opacity = BackgroundOpacity, Blur = Blur },
            Border = new() { Color = Format(BorderColor), Thickness = BorderThickness, CornerRadius = CornerRadius },
            Grip = new() { Color = Format(GripColor), Size = GripSize, Position = GripPosition },
            Icons = new()
            {
                Spacing = IconSpacing,
                Hover = Format(HoverColor), Pressed = Format(PressedColor), HighlightCornerRadius = HighlightCornerRadius
            },
            ToolTip = new()
            {
                Background = Format(ToolTipBackground), Foreground = Format(ToolTipForeground), Border = Format(ToolTipBorder),
                BorderThickness = ToolTipBorderThickness, CornerRadius = ToolTipCornerRadius,
                FontFamily = ToolTipFontFamily, FontSize = ToolTipFontSize, Padding = ToolTipPadding
            },
            Xaml = XamlPath
        };

        /// <summary>"#RRGGBB" when opaque, else "#AARRGGBB".</summary>
        public static string Format(Color c) =>
            c.A == 255 ? string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}")
                       : string.Create(CultureInfo.InvariantCulture, $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");

        /// <summary>A colour as WPF writes them: "#RGB", "#RRGGBB", "#AARRGGBB", or a name such as "White".</summary>
        public static bool TryParseColor(string? text, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                if (ColorConverter.ConvertFromString(text.Trim()) is Color c)
                {
                    color = c;
                    return true;
                }
            }
            catch (FormatException)
            {
            }
            return false;
        }

        private static Color ParseColor(string? text, Color fallback, string property, List<string> problems)
        {
            if (text == null) return fallback;
            if (TryParseColor(text, out Color c)) return c;
            problems.Add($"{property}: '{text}' is not a colour (use #RRGGBB, #AARRGGBB or a name like White)");
            return fallback;
        }

        private static double Clamp(double? value, double fallback, double min, double max) =>
            value is double v && !double.IsNaN(v) ? Math.Clamp(v, min, max) : fallback;
    }
}
