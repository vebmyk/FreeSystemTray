using System;
using System.IO;
using System.Windows.Input;
using FreeSystemTray.Icons;
using FreeSystemTray.Settings;
using FreeSystemTray.ShowHide;
using Microsoft.Win32;
using Xunit;

namespace FreeSystemTray.Tests
{
    /// <summary>
    /// Autostart against keys of the tests' own under HKCU\Software\FreeSystemTray.Tests, deleted
    /// afterwards; Windows' Run key is never touched.
    /// </summary>
    public sealed class AutostartTests : IDisposable
    {
        private readonly string _root = @"Software\FreeSystemTray.Tests\" + Guid.NewGuid().ToString("N");
        private readonly Autostart _autostart;

        public AutostartTests() => _autostart = new Autostart(_root + @"\Run", _root + @"\Approved");

        [Fact]
        public void Off_until_enabled_then_off_again()
        {
            Assert.False(_autostart.IsEnabled);
            Assert.Null(_autostart.Command);

            _autostart.Enable("\"C:\\Tools\\FreeSystemTray.exe\" --autostart");
            Assert.True(_autostart.IsEnabled);
            Assert.True(_autostart.StartsExe(@"c:\tools\FreeSystemTray.exe"));
            Assert.False(_autostart.StartsExe(@"C:\Other\FreeSystemTray.exe"));

            _autostart.Disable();
            Assert.False(_autostart.IsEnabled);
            Assert.Null(_autostart.Command);
        }

        [Fact]
        public void An_entry_turned_off_in_Task_Manager_is_off_and_enabling_turns_it_back_on()
        {
            _autostart.Enable("x.exe");
            // What Task Manager writes when it turns an entry off: an odd first byte and the time.
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(_root + @"\Approved"))
                key.SetValue(Autostart.ValueName, new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, RegistryValueKind.Binary);
            Assert.True(_autostart.TurnedOffInTaskManager);
            Assert.False(_autostart.IsEnabled);

            _autostart.Enable("x.exe");
            Assert.False(_autostart.TurnedOffInTaskManager);
            Assert.True(_autostart.IsEnabled);
        }

        [Fact]
        public void The_command_marks_sign_in_and_names_a_settings_file_that_is_not_the_default()
        {
            Assert.Equal("\"C:\\Tools\\FreeSystemTray.exe\" --autostart", Autostart.CommandFor(@"C:\Tools\FreeSystemTray.exe", SettingsStore.DefaultPath));
            Assert.Equal("\"C:\\Tools\\FreeSystemTray.exe\" --autostart --settings \"D:\\My settings.json\"",
                Autostart.CommandFor(@"C:\Tools\FreeSystemTray.exe", @"D:\My settings.json"));
        }

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
            using (RegistryKey? parent = Registry.CurrentUser.OpenSubKey(@"Software\FreeSystemTray.Tests"))
            {
                if (parent == null || parent.SubKeyCount > 0 || parent.ValueCount > 0) return;
            }
            Registry.CurrentUser.DeleteSubKey(@"Software\FreeSystemTray.Tests", throwOnMissingSubKey: false);
        }
    }

    public class HotkeyFormatTests
    {
        [Theory]
        [InlineData(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.T, "Ctrl+Alt+Shift+T")]
        [InlineData(ModifierKeys.Windows | ModifierKeys.Shift, Key.F12, "Shift+Win+F12")]
        [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.D5, "Ctrl+Alt+5")]
        [InlineData(ModifierKeys.Alt, Key.OemTilde, "Alt+OemTilde")]
        [InlineData(ModifierKeys.None, Key.F9, "F9")]
        [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.Next, "Ctrl+Alt+PageDown")]
        public void A_pressed_chord_is_written_the_way_the_settings_read_it(ModifierKeys modifiers, Key key, string expected)
        {
            string? text = HotkeyGesture.Format(modifiers, key);
            Assert.Equal(expected, text);
            Assert.Equal(expected, HotkeyGesture.Parse(text)!.ToString());
        }

        [Theory]
        [InlineData(Key.LeftCtrl)]
        [InlineData(Key.RightShift)]
        [InlineData(Key.LWin)]
        [InlineData(Key.System)]
        public void Modifiers_alone_are_not_a_hotkey_yet(Key key) => Assert.Null(HotkeyGesture.Format(ModifierKeys.Control, key));
    }

    public sealed class IconSettingsFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "FreeSystemTray.Tests." + Guid.NewGuid().ToString("N"));

        private string File1 => Path.Combine(_dir, "settings.json");

        [Fact]
        public void Icon_settings_survive_a_save_and_load_and_unset_rule_values_are_left_out()
        {
            var store = new SettingsStore(File1);
            store.Save(new AppSettings
            {
                AppIcon = false,
                Icons = new IconSettings
                {
                    Default = DefaultPlacement.Windows, HideWindowsHidden = false, MaxTrayIcons = 8,
                    Rules = { new IconRule { Match = "Discord.exe", Placement = IconPlacement.Overflow }, new IconRule { Match = "a.exe:1", Tooltip = "{tip}!" } },
                    Order = { "a.exe:1", "7820ae73-23e3-4229-82c1-e41cb67d5b9c" }
                }
            });
            AppSettings loaded = store.Load();
            Assert.False(loaded.AppIcon);
            Assert.Equal(DefaultPlacement.Windows, loaded.Icons.Default);
            Assert.False(loaded.Icons.HideWindowsHidden);
            Assert.Equal(8, loaded.Icons.MaxTrayIcons);
            Assert.Equal(IconPlacement.Overflow, loaded.Icons.Rules[0].Placement);
            Assert.Null(loaded.Icons.Rules[0].Tooltip);
            Assert.Equal("{tip}!", loaded.Icons.Rules[1].Tooltip);
            Assert.Equal(new[] { "a.exe:1", "7820ae73-23e3-4229-82c1-e41cb67d5b9c" }, loaded.Icons.Order);
            string json = File.ReadAllText(File1);
            Assert.Contains("\"placement\": \"Overflow\"", json);
            Assert.DoesNotContain("\"label\"", json);
        }

        [Fact]
        public void Missing_or_null_icon_settings_get_the_defaults()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(File1, "{ \"lockPosition\": true }");
            AppSettings loaded = new SettingsStore(File1).Load();
            Assert.True(loaded.AppIcon);
            Assert.Equal(DefaultPlacement.Tray, loaded.Icons.Default);
            Assert.True(loaded.Icons.HideWindowsHidden);

            File.WriteAllText(File1, "{ \"icons\": { \"rules\": [ null, { \"placement\": \"Hidden\" } ], \"order\": [ \"\", null, \"a.exe:1\" ] } }");
            loaded = new SettingsStore(File1).Load();
            Assert.Single(loaded.Icons.Rules);
            Assert.Equal("", loaded.Icons.Rules[0].Match);
            Assert.Equal(new[] { "a.exe:1" }, loaded.Icons.Order);
        }

        [Fact]
        public void A_clone_is_independent()
        {
            var settings = new AppSettings { Icons = new IconSettings { Rules = { new IconRule { Match = "a.exe", Label = "A" } } } };
            AppSettings copy = SettingsStore.Clone(settings);
            copy.Icons.Rules[0].Label = "B";
            copy.Icons.Order.Add("x");
            Assert.Equal("A", settings.Icons.Rules[0].Label);
            Assert.Empty(settings.Icons.Order);
        }

        [Fact]
        public void Editing_a_rule_adds_it_and_removes_it_once_it_sets_nothing()
        {
            var icons = new IconSettings { Rules = { new IconRule { Match = "b.exe", Placement = IconPlacement.Hidden } } };
            icons.EditRule("A.exe:1", r => r.Label = "Mine");
            Assert.Equal(2, icons.Rules.Count);
            icons.EditRule("a.exe:1", r => r.Placement = IconPlacement.Tray);
            Assert.Equal(2, icons.Rules.Count);
            Assert.Equal(IconPlacement.Tray, icons.Rules[1].Placement);
            icons.EditRule("a.exe:1", r => { r.Label = null; r.Placement = null; });
            Assert.Single(icons.Rules);
            Assert.Equal("b.exe", icons.Rules[0].Match);
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
    }
}
