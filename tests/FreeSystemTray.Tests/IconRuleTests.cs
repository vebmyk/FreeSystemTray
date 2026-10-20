using System;
using System.Collections.Generic;
using System.Linq;
using FreeSystemTray.Icons;
using FreeSystemTray.TrayHost;
using FreeSystemTray.TrayHost.Interop;
using Xunit;

namespace FreeSystemTray.Tests
{
    public class IconIdentityTests
    {
        private static readonly Guid MeetNow = new("7820ae83-23e3-4229-82c1-e41cb67d5b9c");
        private static readonly IconIdentity PowerToys = new(@"C:\Program Files\PowerToys\PowerToys.exe:1", Guid.Empty, @"C:\Program Files\PowerToys\PowerToys.exe", 1);
        private static readonly IconIdentity Explorer = new(MeetNow.ToString(), MeetNow, @"C:\Windows\explorer.exe", 0);

        [Fact]
        public void The_key_is_the_GUID_or_the_exe_name_and_uID()
        {
            Assert.Equal("7820ae83-23e3-4229-82c1-e41cb67d5b9c", Explorer.Key);
            Assert.Equal("PowerToys.exe:1", PowerToys.Key);
            // Without a known exe the host's id is all there is.
            Assert.Equal(":7", new IconIdentity(":7", Guid.Empty, null, 7).Key);
        }

        [Theory]
        [InlineData("PowerToys.exe:1", 3)]
        [InlineData("powertoys.EXE:1", 3)]
        [InlineData(@"C:\Program Files\PowerToys\PowerToys.exe:1", 3)]
        [InlineData(@"C:\Program Files\PowerToys\PowerToys.exe", 2)]
        [InlineData("C:/Program Files/PowerToys/PowerToys.exe", 2)]
        [InlineData("PowerToys.exe", 1)]
        [InlineData(" PowerToys.exe ", 1)]
        [InlineData("PowerToys.exe:2", 0)]
        [InlineData("PowerToys", 0)]
        [InlineData(@"D:\PowerToys\PowerToys.exe", 0)]
        [InlineData("", 0)]
        public void Rules_name_an_icon_more_or_less_exactly(string match, int expected) => Assert.Equal(expected, PowerToys.MatchStrength(match));

        [Theory]
        [InlineData("7820ae83-23e3-4229-82c1-e41cb67d5b9c", 3)]
        [InlineData("{7820AE83-23E3-4229-82C1-E41CB67D5B9C}", 3)]
        [InlineData("explorer.exe", 1)]
        [InlineData("explorer.exe:0", 3)]
        [InlineData("7820ae73-23e3-4229-82c1-e41cb67d5b9c", 0)]
        public void GUID_icons_are_named_by_their_GUID_or_their_exe(string match, int expected) => Assert.Equal(expected, Explorer.MatchStrength(match));

        [Fact]
        public void A_GUID_never_names_an_icon_without_one() => Assert.Equal(0, PowerToys.MatchStrength(Guid.Empty.ToString()));
    }

    public class IconArrangementTests
    {
        private static IconFacts Icon(string exe, uint uid = 1, bool appHidden = false, ExplorerPlacement explorer = ExplorerPlacement.Unknown) =>
            new(new IconIdentity($@"C:\Apps\{exe}:{uid}", Guid.Empty, $@"C:\Apps\{exe}", uid), appHidden, explorer);

        private static readonly IconFacts A = Icon("a.exe"), B = Icon("b.exe"), C = Icon("c.exe"), D = Icon("d.exe");

        private static IReadOnlyList<IconFacts> Icons(params IconFacts[] icons) => icons;

        /// <summary>"tray: a b | overflow: c | hidden: d".</summary>
        private static string Layout(IReadOnlyList<IconFacts> icons, IconSettings settings, int capacity = 0)
        {
            var arranged = IconArrangement.Arrange(icons, settings, capacity);
            string Group(IconPlacement p) => string.Join(" ", arranged.Where(a => a.Placement == p).Select(a => icons[a.Index].Identity.ExeName!.Replace(".exe", "")));
            return $"tray: {Group(IconPlacement.Tray)} | overflow: {Group(IconPlacement.Overflow)} | hidden: {Group(IconPlacement.Hidden)}";
        }

        [Fact]
        public void Without_settings_every_icon_is_in_the_tray_in_the_order_it_arrived() =>
            Assert.Equal("tray: a b c | overflow:  | hidden: ", Layout(Icons(A, B, C), new IconSettings()));

        [Fact]
        public void Rules_place_icons()
        {
            var settings = new IconSettings
            {
                Rules = { new IconRule { Match = "b.exe", Placement = IconPlacement.Overflow }, new IconRule { Match = "c.exe:1", Placement = IconPlacement.Hidden } }
            };
            Assert.Equal("tray: a d | overflow: b | hidden: c", Layout(Icons(A, B, C, D), settings));
        }

        [Fact]
        public void An_icon_its_app_hid_stays_hidden_whatever_the_rules_say()
        {
            var settings = new IconSettings { Rules = { new IconRule { Match = "a.exe", Placement = IconPlacement.Tray } } };
            Assert.Equal("tray: b | overflow:  | hidden: a", Layout(Icons(Icon("a.exe", appHidden: true), B), settings));
        }

        [Fact]
        public void Icons_Windows_hides_are_hidden_unless_turned_off_or_a_rule_shows_them()
        {
            IconFacts meetNow = Icon("explorer.exe", 0, explorer: ExplorerPlacement.Hidden);
            Assert.Equal("tray: a | overflow:  | hidden: explorer", Layout(Icons(A, meetNow), new IconSettings()));
            Assert.Equal("tray: a explorer | overflow:  | hidden: ", Layout(Icons(A, meetNow), new IconSettings { HideWindowsHidden = false }));
            var rule = new IconSettings { Rules = { new IconRule { Match = "explorer.exe", Placement = IconPlacement.Overflow } } };
            Assert.Equal("tray: a | overflow: explorer | hidden: ", Layout(Icons(A, meetNow), rule));
        }

        [Fact]
        public void The_default_can_be_the_flyout_or_as_in_Windows()
        {
            var icons = Icons(Icon("a.exe", explorer: ExplorerPlacement.Taskbar), Icon("b.exe", explorer: ExplorerPlacement.Overflow), Icon("c.exe"));
            Assert.Equal("tray:  | overflow: a b c | hidden: ", Layout(icons, new IconSettings { Default = DefaultPlacement.Overflow }));
            // Unknown to Explorer (its tray unreadable, or the icon missing there): the tray.
            Assert.Equal("tray: a c | overflow: b | hidden: ", Layout(icons, new IconSettings { Default = DefaultPlacement.Windows }));
        }

        [Fact]
        public void The_saved_order_sorts_each_group_and_new_icons_follow_it()
        {
            var settings = new IconSettings
            {
                Order = { "c.exe:1", "gone.exe:1", "A.EXE:1" },
                Rules = { new IconRule { Match = "b.exe", Placement = IconPlacement.Overflow }, new IconRule { Match = "d.exe", Placement = IconPlacement.Overflow } }
            };
            Assert.Equal("tray: c a | overflow: b d | hidden: ", Layout(Icons(A, B, C, D), settings));
            settings.Order.Add("d.exe:1");
            Assert.Equal("tray: c a | overflow: d b | hidden: ", Layout(Icons(A, B, C, D), settings));
        }

        [Fact]
        public void A_full_tray_spills_into_the_flyout_and_rules_for_the_tray_keep_their_place()
        {
            var settings = new IconSettings { MaxTrayIcons = 2 };
            Assert.Equal("tray: a b | overflow: c d | hidden: ", Layout(Icons(A, B, C, D), settings));
            settings.Rules.Add(new IconRule { Match = "d.exe", Placement = IconPlacement.Tray });
            Assert.Equal("tray: a d | overflow: b c | hidden: ", Layout(Icons(A, B, C, D), settings));
            var arranged = IconArrangement.Arrange(Icons(A, B, C, D), settings);
            Assert.True(arranged.Single(a => a.Index == 1).Spilled);
        }

        [Fact]
        public void A_fixed_grid_keeps_its_last_cell_for_the_button_when_there_is_more_to_show()
        {
            var settings = new IconSettings();
            // Everything fits: no button.
            Assert.Equal("tray: a b c d | overflow:  | hidden: ", Layout(Icons(A, B, C, D), settings, capacity: 4));
            // One too many: three icons and the button.
            Assert.Equal("tray: a b | overflow: c d | hidden: ", Layout(Icons(A, B, C, D), settings, capacity: 3));
            Assert.True(IconArrangement.Arrange(Icons(A, B, C, D), settings, 3).Single(a => a.Index == 2).Spilled);
            // Icons in the flyout need the button too.
            settings.Rules.Add(new IconRule { Match = "d.exe", Placement = IconPlacement.Overflow });
            Assert.Equal("tray: a b c | overflow: d | hidden: ", Layout(Icons(A, B, C, D), settings, capacity: 4));
            // The smaller of the two limits wins.
            settings.MaxTrayIcons = 1;
            Assert.Equal("tray: a | overflow: b c d | hidden: ", Layout(Icons(A, B, C, D), settings, capacity: 4));
            // A single cell holds only the button.
            settings.MaxTrayIcons = 0;
            Assert.Equal("tray:  | overflow: a b c d | hidden: ", Layout(Icons(A, B, C, D), settings, capacity: 1));
        }

        [Fact]
        public void The_most_exact_rule_wins_for_each_value()
        {
            var id = A.Identity;
            var rules = new List<IconRule>
            {
                new() { Match = "a.exe", Placement = IconPlacement.Overflow, Tooltip = "all of a" },
                new() { Match = "a.exe:1", Label = "Mine", Placement = IconPlacement.Tray },
                new() { Match = "a.exe", Label = "loses" },
                new() { Match = "b.exe", Placement = IconPlacement.Hidden }
            };
            Assert.Equal(new EffectiveRule(IconPlacement.Tray, "Mine", "all of a"), EffectiveRule.For(id, rules));
            Assert.Same(EffectiveRule.None, EffectiveRule.For(C.Identity, rules));
        }

        [Theory]
        [InlineData(null, "App", "App")]
        [InlineData("Chat", "App", "Chat")]
        [InlineData("Chat: {tip}", "3 unread", "Chat: 3 unread")]
        [InlineData("Chat: {TIP}", null, "Chat: ")]
        [InlineData("", "App", null)]
        [InlineData(null, "", null)]
        public void Tooltips_can_be_replaced(string? rule, string? app, string? expected) =>
            Assert.Equal(expected, new EffectiveRule(null, null, rule).ToolTipFor(app));
    }

    public class IconOrderTests
    {
        private static List<string> Move(string order, string shown, string group, string key, int index) =>
            IconOrder.Move(Split(order), Split(shown), Split(group), key, index);

        private static List<string> Split(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        [Fact]
        public void Moving_within_a_group_writes_the_whole_order()
        {
            Assert.Equal(Split("b a c"), Move("", "a b c", "a b c", "b", 0));
            Assert.Equal(Split("a c b"), Move("", "a b c", "a b c", "b", 2));
            Assert.Equal(Split("a c b"), Move("", "a b c", "a b c", "b", 99));
        }

        [Fact]
        public void Icons_that_are_not_running_keep_their_place()
        {
            // x is not running; it stays between a and b.
            Assert.Equal(Split("a x c b"), Move("a x b c", "a b c", "a b c", "b", 2));
            Assert.Equal(Split("b a x c"), Move("a x b c", "a b c", "a b c", "b", 0));
        }

        [Fact]
        public void Moving_into_another_group_puts_it_next_to_its_new_neighbours()
        {
            // Tray a b, flyout c d: b goes to the flyout between c and d.
            Assert.Equal(Split("a c b d"), Move("", "a b c d", "c d", "b", 1));
            // Into an empty group.
            Assert.Equal(Split("a c d b"), Move("", "a b c d", "", "b", 0));
        }

        [Fact]
        public void Keys_are_compared_without_case_and_written_once()
        {
            // The moved icon is written with its key as it is now.
            Assert.Equal(Split("b a"), Move("a B", "a b", "a b", "b", 0));
            Assert.Equal(Split("A b"), Move("A B", "a b", "a b", "b", 1));
        }
    }

    public class ExplorerPlacementTests
    {
        private static readonly IntPtr W1 = new(0x100), W2 = new(0x200);
        private static readonly Guid G = new("7820ae83-23e3-4229-82c1-e41cb67d5b9c");

        private static readonly ExplorerTrayButton[] Buttons =
        {
            new(W1, 1, Guid.Empty, ExplorerPlacement.Taskbar, false),
            new(W1, 2, Guid.Empty, ExplorerPlacement.Overflow, false),
            new(W2, 0, G, ExplorerPlacement.Overflow, true),
            new(W2, 5, Guid.Empty, ExplorerPlacement.Overflow, true),
            new(W2, 5, Guid.Empty, ExplorerPlacement.Taskbar, false)
        };

        [Fact]
        public void Icons_are_found_by_GUID_or_by_window_and_uID()
        {
            Assert.Equal(ExplorerPlacement.Taskbar, ExplorerTrayIcons.PlacementOf(Buttons, W1, 1, Guid.Empty));
            Assert.Equal(ExplorerPlacement.Overflow, ExplorerTrayIcons.PlacementOf(Buttons, W1, 2, Guid.Empty));
            Assert.Equal(ExplorerPlacement.Hidden, ExplorerTrayIcons.PlacementOf(Buttons, IntPtr.Zero, 0, G));
            Assert.Equal(ExplorerPlacement.Unknown, ExplorerTrayIcons.PlacementOf(Buttons, W1, 3, Guid.Empty));
        }

        [Fact]
        public void A_visible_button_wins_over_a_hidden_one() =>
            Assert.Equal(ExplorerPlacement.Taskbar, ExplorerTrayIcons.PlacementOf(Buttons, W2, 5, Guid.Empty));
    }
}
