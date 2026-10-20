using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using FreeSystemTray.Scaling;
using FreeSystemTray.Theming;
using Xunit;

namespace FreeSystemTray.Tests
{
    public class ThemeModelTests
    {
        [Fact]
        public void The_default_theme_is_the_phase_1_look()
        {
            Theme t = Theme.Default;
            Assert.Equal(Color.FromRgb(0x2B, 0x2B, 0x2B), t.Background);
            Assert.Equal(1, t.BackgroundOpacity);
            Assert.Equal(TrayBlur.None, t.Blur);
            Assert.Equal(Color.FromRgb(0x6A, 0x6A, 0x6A), t.BorderColor);
            Assert.Equal(Color.FromRgb(0x4A, 0x4A, 0x4A), t.GripColor);
            Assert.Equal(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), t.HoverColor);
            Assert.Equal(GripPosition.Left, t.GripPositionFor(column: false));
            Assert.Equal(GripPosition.Top, t.GripPositionFor(column: true));
        }

        [Fact]
        public void A_file_changes_only_what_it_lists()
        {
            var file = new ThemeFile { Background = new() { Opacity = 0.5 }, Grip = new() { Position = GripPosition.Right } };
            Theme t = Theme.Default.With(file, "Mine", null, new List<string>());
            Assert.Equal("Mine", t.Id);
            Assert.Equal("Mine", t.Name); // no name: the id
            Assert.Equal(0.5, t.BackgroundOpacity);
            Assert.Equal(Theme.Default.Background, t.Background);
            Assert.Equal(GripPosition.Right, t.GripPositionFor(column: true));
        }

        [Fact]
        public void A_layout_in_a_theme_is_reported_and_ignored()
        {
            var problems = new List<string>();
            var file = JsonSerializer.Deserialize<ThemeFile>("{ \"icons\": { \"layout\": \"Grid\", \"gridColumns\": 4, \"spacing\": 2 } }",
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            Theme t = Theme.Default.With(file, "x", null, problems);
            Assert.Equal(2, t.IconSpacing);
            Assert.Contains(problems, p => p.Contains("no longer part of a theme"));
        }

        [Fact]
        public void Bad_colours_are_reported_and_keep_the_value_underneath()
        {
            var problems = new List<string>();
            var file = new ThemeFile { Border = new() { Color = "bluish" }, Icons = new() { Hover = "#80FF0000" } };
            Theme t = Theme.Default.With(file, "x", null, problems);
            Assert.Equal(Theme.Default.BorderColor, t.BorderColor);
            Assert.Equal(Color.FromArgb(0x80, 0xFF, 0, 0), t.HoverColor);
            Assert.Single(problems);
            Assert.Contains("border.color", problems[0]);
        }

        [Theory]
        [InlineData("#F00", 255, 255, 0, 0)]
        [InlineData("#112233", 255, 0x11, 0x22, 0x33)]
        [InlineData("#80112233", 0x80, 0x11, 0x22, 0x33)]
        [InlineData("White", 255, 255, 255, 255)]
        [InlineData(" transparent ", 0, 255, 255, 255)]
        public void Colours_are_written_as_in_WPF(string text, byte a, byte r, byte g, byte b)
        {
            Assert.True(Theme.TryParseColor(text, out Color c));
            Assert.Equal(Color.FromArgb(a, r, g, b), c);
        }

        [Fact]
        public void Numbers_are_kept_within_sensible_limits()
        {
            var file = new ThemeFile
            {
                Background = new() { Opacity = 7 },
                Border = new() { CornerRadius = -3 },
                ToolTip = new() { FontSize = 500 }
            };
            Theme t = Theme.Default.With(file, "x", null, new List<string>());
            Assert.Equal(1, t.BackgroundOpacity);
            Assert.Equal(0, t.CornerRadius);
            Assert.Equal(72, t.ToolTipFontSize);
        }

        [Fact]
        public void An_exported_theme_reads_back_as_the_same_theme()
        {
            Theme original = Theme.Default with
            {
                Id = "Glassy", Name = "Glassy", Background = Color.FromArgb(0xCC, 1, 2, 3), BackgroundOpacity = 0.4, Blur = TrayBlur.Acrylic,
                CornerRadius = 8, IconSpacing = 2, ToolTipFontFamily = "Consolas", GripPosition = GripPosition.Bottom
            };
            var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
            ThemeFile back = JsonSerializer.Deserialize<ThemeFile>(JsonSerializer.Serialize(original.ToFile(), json), json)!;
            Assert.Equal(original, Theme.Default.With(back, "Glassy", null, new List<string>()));
        }

        [Fact]
        public void The_background_is_never_fully_transparent_so_clicks_do_not_fall_through()
        {
            Theme t = Theme.Default with { BackgroundOpacity = 0 };
            Assert.Equal(ThemeResources.MinBackgroundAlpha, ThemeResources.BackgroundColor(t).A);
            Assert.InRange(ThemeResources.BackgroundColor(Theme.Default with { BackgroundOpacity = 0.5 }).A, 127, 128);
        }
    }

    public sealed class ThemeStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "FreeSystemTray.Themes." + Guid.NewGuid().ToString("N"));
        private string User => Path.Combine(_root, "user");
        private string BuiltIn => Path.Combine(_root, "builtin");
        private ThemeStore Store => new(User, BuiltIn);

        public ThemeStoreTests()
        {
            Directory.CreateDirectory(User);
            Directory.CreateDirectory(BuiltIn);
            Write(BuiltIn, "Dark", "{ \"name\": \"Dark\" }");
            Write(BuiltIn, "Glass", "{ \"name\": \"Glass\", // a comment\n \"border\": { \"cornerRadius\": 8 }, \"background\": { \"blur\": \"Blur\" }, }");
        }

        private static void Write(string dir, string id, string json) => File.WriteAllText(Path.Combine(dir, id + ".json"), json);

        [Fact]
        public void Themes_are_listed_by_name_and_the_users_own_replace_built_in_ones()
        {
            Write(User, "Glass", "{ \"name\": \"My glass\" }");
            Write(User, "Zed", "{ }");
            IReadOnlyList<ThemeInfo> list = Store.List();
            Assert.Equal(new[] { "Dark", "My glass", "Zed" }, list.Select(t => t.Name));
            Assert.False(list.Single(t => t.Id == "Glass").BuiltIn);
        }

        [Fact]
        public void A_theme_based_on_another_gets_its_values_and_overrides_some()
        {
            Write(User, "Mine", "{ \"basedOn\": \"Glass\", \"border\": { \"cornerRadius\": 3 } }");
            var problems = new List<string>();
            Theme t = Store.Load("mine", problems);
            Assert.Empty(problems);
            Assert.Equal("mine", t.Id);
            Assert.Equal(3, t.CornerRadius);
            Assert.Equal(TrayBlur.Blur, t.Blur);
        }

        [Fact]
        public void A_cycle_of_basedOn_is_reported_and_stopped()
        {
            Write(User, "A", "{ \"basedOn\": \"B\", \"icons\": { \"spacing\": 1 } }");
            Write(User, "B", "{ \"basedOn\": \"A\" }");
            var problems = new List<string>();
            Theme t = Store.Load("A", problems);
            Assert.Equal(1, t.IconSpacing);
            Assert.Contains(problems, p => p.Contains("A > B > A"));
        }

        [Fact]
        public void A_missing_or_broken_theme_gives_the_defaults_and_says_why()
        {
            var problems = new List<string>();
            Assert.Equal(Theme.Default, Store.Load("Nope", problems));
            Assert.Contains("not found", problems.Single());

            Write(User, "Broken", "{ \"border\": ");
            problems.Clear();
            Assert.Equal(Theme.Default, Store.Load("Broken", problems));
            Assert.Contains("could not be read", problems.Single());
        }

        [Fact]
        public void A_theme_id_cannot_reach_outside_the_theme_folders() => Assert.Null(Store.PathOf(@"..\builtin\Dark"));

        [Fact]
        public void Import_copies_the_theme_and_its_xaml_and_rejects_what_is_not_a_theme()
        {
            string src = Path.Combine(_root, "incoming");
            Directory.CreateDirectory(src);
            Write(src, "Nice", "{ \"name\": \"Nice\", \"xaml\": \"Nice.xaml\" }");
            File.WriteAllText(Path.Combine(src, "Nice.xaml"), "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />");

            var problems = new List<string>();
            Assert.True(Store.Import(Path.Combine(src, "Nice.json"), out string id, problems));
            Assert.Equal("Nice", id);
            Assert.True(File.Exists(Path.Combine(User, "Nice.json")));
            Assert.True(File.Exists(Path.Combine(User, "Nice.xaml")));
            Assert.Equal(Path.Combine(User, "Nice.xaml"), Store.Load("Nice", problems).XamlPath);

            File.WriteAllText(Path.Combine(src, "Bad.json"), "not json");
            Assert.False(Store.Import(Path.Combine(src, "Bad.json"), out _, problems));
            Assert.False(File.Exists(Path.Combine(User, "Bad.json")));
        }

        [Fact]
        public void An_export_needs_no_other_theme()
        {
            Write(User, "Mine", "{ \"basedOn\": \"Glass\", \"border\": { \"cornerRadius\": 3 } }");
            Theme mine = Store.Load("Mine", new List<string>());
            string target = Path.Combine(_root, "out", "Exported.json");
            ThemeStore.Export(mine, target);

            // Read back from a folder where Glass does not exist.
            var elsewhere = new ThemeStore(Path.Combine(_root, "out"), Path.Combine(_root, "none"));
            Theme back = elsewhere.Load("Exported", new List<string>());
            Assert.Equal(mine with { Id = "Exported" }, back);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    public class ThemedLayoutTests
    {
        [Fact]
        public void The_themes_sizes_scale_with_the_tray_size()
        {
            var m = new TrayMetrics(BorderThickness: 2, GripSize: 6, CornerRadius: 8, Spacing: 2, HighlightCornerRadius: 4);
            TrayLayout l = TrayLayout.Compute(SizeValue.Percent(100), SizeValue.Percent(150), m);
            Assert.Equal(3, l.BorderThickness);
            Assert.Equal(9, l.GripWidth);
            Assert.Equal(12, l.CornerRadius);
            Assert.Equal(3, l.Spacing);
            Assert.Equal(6, l.HighlightCornerRadius);
            Assert.Equal(16, l.IconSize); // the icon size is still its own setting
        }

        [Fact]
        public void Rounded_corners_move_the_icons_inwards()
        {
            TrayLayout square = TrayLayout.Compute(SizeValue.Percent(100), SizeValue.Percent(100));
            TrayLayout round = TrayLayout.Compute(SizeValue.Percent(100), SizeValue.Percent(100), new TrayMetrics(CornerRadius: 10));
            Assert.Equal(square.MarginAlong + 3, round.MarginAlong, 6);
            Assert.Equal(square.MarginAcross + 3, round.MarginAcross, 6);
        }
    }
}
