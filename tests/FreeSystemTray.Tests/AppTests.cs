using System;
using System.IO;
using FreeSystemTray.Placement;
using FreeSystemTray.Settings;
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
