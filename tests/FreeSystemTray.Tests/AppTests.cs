using System;
using System.IO;
using FreeSystemTray.Placement;
using FreeSystemTray.Scaling;
using FreeSystemTray.Settings;
using FreeSystemTray.ShowHide;
using Xunit;

namespace FreeSystemTray.Tests
{
    public sealed class SettingsStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "FreeSystemTray.Tests." + Guid.NewGuid().ToString("N"));

        private string File1 => Path.Combine(_dir, "settings.json");

        [Fact]
        public void Settings_survive_a_save_and_load()
        {
            var store = new SettingsStore(File1);
            store.Save(new AppSettings { Position = new SavedPosition(1904, 1022, Corner.BottomRight), LockPosition = true, SnapToEdges = false, SnapDistance = 20 });

            AppSettings loaded = store.Load();

            Assert.Equal(new SavedPosition(1904, 1022, Corner.BottomRight), loaded.Position);
            Assert.True(loaded.LockPosition);
            Assert.False(loaded.SnapToEdges);
            Assert.Equal(20, loaded.SnapDistance);
            Assert.Contains("\"anchor\": \"BottomRight\"", File.ReadAllText(File1));
        }

        [Fact]
        public void A_missing_file_gives_the_defaults()
        {
            AppSettings loaded = new SettingsStore(File1).Load();
            Assert.Null(loaded.Position);
            Assert.False(loaded.LockPosition);
            Assert.True(loaded.SnapToEdges);
            Assert.False(loaded.ShowHide.AutoHide);
            Assert.Equal("Ctrl+Alt+Shift+T", loaded.ShowHide.Hotkey);
            Assert.Equal(HotspotLocation.None, loaded.ShowHide.Hotspot);
            Assert.Equal("Dark", loaded.Theme);
        }

        [Fact]
        public void The_theme_survives_a_save_and_an_empty_one_is_the_default()
        {
            var store = new SettingsStore(File1);
            store.Save(new AppSettings { Theme = "Glass" });
            Assert.Equal("Glass", store.Load().Theme);
            File.WriteAllText(File1, "{ \"theme\": \"\" }");
            Assert.Equal("Dark", new SettingsStore(File1).Load().Theme);
        }

        [Fact]
        public void Show_hide_settings_survive_a_save_and_load()
        {
            var store = new SettingsStore(File1);
            store.Save(new AppSettings
            {
                ShowHide = new ShowHideSettings
                {
                    AutoHide = true, HideOnClickAway = false, HideDelayMs = 800, Hotkey = "Win+Alt+Y",
                    Hotspot = HotspotLocation.TopLeft, HotspotDelayMs = 50, Animation = TrayAnimation.Slide, AnimationMs = 300
                }
            });

            ShowHideSettings loaded = store.Load().ShowHide;

            Assert.True(loaded.AutoHide);
            Assert.False(loaded.HideOnClickAway);
            Assert.Equal(800, loaded.HideDelayMs);
            Assert.Equal("Win+Alt+Y", loaded.Hotkey);
            Assert.Equal(HotspotLocation.TopLeft, loaded.Hotspot);
            Assert.Equal(50, loaded.HotspotDelayMs);
            Assert.Equal(TrayAnimation.Slide, loaded.Animation);
            Assert.Equal(300, loaded.AnimationMs);
            Assert.Contains("\"hotspot\": \"TopLeft\"", File.ReadAllText(File1));
        }

        [Fact]
        public void Scaling_settings_survive_a_save_and_load_and_take_numbers_as_pixels()
        {
            var store = new SettingsStore(File1);
            store.Save(new AppSettings
            {
                Scaling = new ScalingSettings
                {
                    IconSize = "167%", TraySize = "40px", SnapToPixels = false, IconScaling = IconScaling.NearestNeighbor,
                    ExeIcons = { "Discord.exe" }
                }
            });
            ScalingSettings loaded = store.Load().Scaling;
            Assert.Equal("167%", loaded.IconSize);
            Assert.Equal("40px", loaded.TraySize);
            Assert.False(loaded.SnapToPixels);
            Assert.Equal(IconScaling.NearestNeighbor, loaded.IconScaling);
            Assert.Equal(new[] { "Discord.exe" }, loaded.ExeIcons);

            File.WriteAllText(File1, "{ \"scaling\": { \"iconSize\": 24, \"traySize\": null, \"exeIcons\": null } }");
            loaded = new SettingsStore(File1).Load().Scaling;
            Assert.Equal("24", loaded.IconSize);
            Assert.Equal("100%", loaded.TraySize);
            Assert.Empty(loaded.ExeIcons);
            Assert.True(loaded.SnapToPixels);
        }

        [Fact]
        public void A_file_from_before_show_hide_or_with_it_null_gets_its_defaults()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(File1, "{ \"lockPosition\": true }");
            Assert.False(new SettingsStore(File1).Load().ShowHide.AutoHide);
            File.WriteAllText(File1, "{ \"showHide\": null }");
            Assert.NotNull(new SettingsStore(File1).Load().ShowHide);
        }

        [Fact]
        public void Reload_ignores_our_own_save_and_picks_up_an_edit()
        {
            var store = new SettingsStore(File1);
            store.Save(new AppSettings { LockPosition = true });
            Assert.Null(store.Reload(out string? error));
            Assert.Null(error);

            File.WriteAllText(File1, File.ReadAllText(File1).Replace("\"autoHide\": false", "\"autoHide\": true"));
            AppSettings? edited = store.Reload(out error);

            Assert.Null(error);
            Assert.True(edited!.ShowHide.AutoHide);
            Assert.True(edited.LockPosition);
            Assert.Null(store.Reload(out _)); // already applied
        }

        [Fact]
        public void Reload_reports_a_broken_edit_and_leaves_the_file_alone()
        {
            var store = new SettingsStore(File1);
            store.Save(new AppSettings());
            File.WriteAllText(File1, "{ \"showHide\": { \"autoHide\": tru");

            Assert.Null(store.Reload(out string? error));

            Assert.NotNull(error);
            Assert.False(File.Exists(File1 + ".bad"));
        }

        [Fact]
        public void A_broken_file_gives_the_defaults_and_is_kept_aside()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(File1, "{ not json");
            string? logged = null;

            AppSettings loaded = new SettingsStore(File1).Load(message => logged = message);

            Assert.Null(loaded.Position);
            Assert.NotNull(logged);
            Assert.Equal("{ not json", File.ReadAllText(File1 + ".bad"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
    }

    public class CommandLineTests
    {
        [Fact]
        public void No_arguments_means_defaults()
        {
            CommandLine o = CommandLine.Parse(Array.Empty<string>());
            Assert.Null(o.SettingsPath);
            Assert.False(o.Verbose);
            Assert.Null(o.WatchdogPid);
            Assert.EndsWith(Path.Combine("FreeSystemTray", "logs"), o.EffectiveLogDir);
        }

        [Fact]
        public void Options_are_read()
        {
            CommandLine o = CommandLine.Parse(new[] { "--settings", @"C:\s.json", "--log-dir", @"C:\logs", "--exit", "--no-watchdog",
                "--no-mirror", "--no-topmost-fix", "--no-autohide-forward", "--no-query-routing" });
            Assert.Equal(@"C:\s.json", o.SettingsPath);
            Assert.Equal(@"C:\logs", o.EffectiveLogDir);
            Assert.True(o.Exit && o.NoWatchdog && o.NoMirror && o.NoTopmostFix && o.NoAutoHideForward && o.NoQueryRouting);
        }

        [Fact]
        public void Theme_commands_are_read()
        {
            Assert.Equal(@"C:\t\Nice.json", CommandLine.Parse(new[] { "--import-theme", @"C:\t\Nice.json" }).ImportTheme);
            Assert.Equal("out.json", CommandLine.Parse(new[] { "--export-theme", "out.json" }).ExportTheme);
            Assert.Throws<ArgumentException>(() => CommandLine.Parse(new[] { "--export-theme" }));
        }

        [Theory]
        [InlineData("--toggle", ShowHideCommand.Toggle)]
        [InlineData("--show", ShowHideCommand.Show)]
        [InlineData("--hide", ShowHideCommand.Hide)]
        public void Show_and_hide_commands_are_read(string arg, ShowHideCommand expected)
        {
            Assert.Equal(expected, CommandLine.Parse(new[] { arg }).ShowHideCommand);
            Assert.Null(CommandLine.Parse(Array.Empty<string>()).ShowHideCommand);
        }

        [Fact]
        public void Diag_implies_verbose_and_logs_next_to_the_state_file()
        {
            CommandLine o = CommandLine.Parse(new[] { "--diag", @"C:\run", "--log-dir", @"C:\other" });
            Assert.True(o.Verbose);
            Assert.Equal(@"C:\run", o.EffectiveLogDir);
        }

        [Fact]
        public void Watchdog_mode_takes_the_pid_and_its_start_time()
        {
            CommandLine o = CommandLine.Parse(new[] { "--watchdog", "1234", "134348440313089427" });
            Assert.Equal(1234, o.WatchdogPid);
            Assert.Equal(134348440313089427, o.WatchdogStartTime);
        }

        [Theory]
        [InlineData("--bogus")]
        [InlineData("--settings")]
        public void Bad_arguments_are_rejected(string arg)
        {
            Assert.Throws<ArgumentException>(() => CommandLine.Parse(new[] { arg }));
        }
    }
}
