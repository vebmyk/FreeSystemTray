using System;
using FreeSystemTray.TrayHost;
using FreeSystemTray.TrayHost.Interop;
using FreeSystemTray.TrayHost.Protocol;
using Xunit;

namespace FreeSystemTray.Tests
{
    public class MirrorPolicyTests
    {
        [Theory]
        [InlineData(Nim.Add, nameof(MirrorFixup.ResendAsModify))]
        [InlineData(Nim.Modify, nameof(MirrorFixup.SendFullAdd))]
        [InlineData(Nim.SetVersion, nameof(MirrorFixup.SendFullAdd))]
        [InlineData(Nim.Delete, nameof(MirrorFixup.None))]
        [InlineData(Nim.SetFocus, nameof(MirrorFixup.None))]
        public void A_rejected_mirror_is_fixed_up_by_message(uint nim, string expected)
        {
            Assert.Equal(Enum.Parse<MirrorFixup>(expected), MirrorPolicy.ForRejected(nim, hostHasIcon: true));
        }

        [Fact]
        public void Without_the_icon_in_ManagedShell_there_is_nothing_to_rebuild()
        {
            Assert.Equal(MirrorFixup.None, MirrorPolicy.ForRejected(Nim.Modify, hostHasIcon: false));
            Assert.Equal(MirrorFixup.None, MirrorPolicy.ForRejected(Nim.Add, hostHasIcon: false));
        }
    }

    public class PositionQueryRoutingTests
    {
        private static readonly PixelRect Tray = new(1500, 1000, 1900, 1030);
        private static readonly PixelPoint OnTray = new(1600, 1010);
        private static readonly PixelPoint Elsewhere = new(900, 500);

        private static readonly PixelRect Overflow = new(1700, 800, 1860, 990);
        private static readonly PixelPoint OnOverflow = new(1750, 900);

        private static bool Route(bool visible, PixelPoint cursor, bool overExplorer, double? secondsSinceClick, PixelRect overflow = default) =>
            PositionQueryRouting.AnswerWithTrayWindow(visible, Tray, overflow, cursor, overExplorer,
                secondsSinceClick is double s ? TimeSpan.FromSeconds(s) : null);

        [Fact]
        public void The_cursor_on_the_tray_window_means_the_tray_window() => Assert.True(Route(true, OnTray, false, null));

        [Fact]
        public void A_recent_click_in_the_tray_window_counts_after_the_cursor_left() => Assert.True(Route(true, Elsewhere, false, 1));

        [Fact]
        public void An_old_click_does_not() => Assert.False(Route(true, Elsewhere, false, 10));

        [Fact]
        public void The_cursor_over_Explorers_tray_means_Explorer_even_right_after_a_click() => Assert.False(Route(true, Elsewhere, true, 0.2));

        [Fact]
        public void A_hidden_tray_window_never_answers() => Assert.False(Route(false, OnTray, false, 0.1));

        [Fact]
        public void Without_bounds_the_tray_window_never_answers() =>
            Assert.False(PositionQueryRouting.AnswerWithTrayWindow(true, default, default, OnTray, false, TimeSpan.Zero));

        [Fact]
        public void The_cursor_on_the_open_hidden_icons_flyout_means_the_tray_window()
        {
            Assert.True(Route(true, OnOverflow, false, null, Overflow));
            Assert.False(Route(true, OnOverflow, false, null)); // closed: nothing of ours is there
            Assert.False(Route(false, OnOverflow, false, null, Overflow));
        }
    }

    public class ShellPathsTests
    {
        private static readonly (Guid, string)[] Folders =
        {
            (new Guid("F38BF404-1D43-42F2-9305-67DE0B28FC23"), @"C:\Windows"),
            (new Guid("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"), @"C:\Windows\System32"),
            (new Guid("D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27"), @"C:\Windows\SysWOW64"),
            (new Guid("6D809377-6AF0-444B-8957-A3773F02200E"), @"C:\Program Files"),
            (new Guid("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E"), @"C:\Program Files (x86)")
        };

        // Expected values are what shell32 put in real NIM_ADD messages on Windows 10 22H2.
        [Theory]
        [InlineData(@"C:\Program Files\GPSoftware\Directory Opus\dopus.exe", @"{6D809377-6AF0-444B-8957-A3773F02200E}\GPSoftware\Directory Opus\dopus.exe")]
        [InlineData(@"C:\Program Files (x86)\Launchy\Launchy.exe", @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Launchy\Launchy.exe")]
        [InlineData(@"C:\Windows\System32\SecurityHealthSystray.exe", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\SecurityHealthSystray.exe")]
        [InlineData(@"C:\Windows\explorer.exe", @"{F38BF404-1D43-42F2-9305-67DE0B28FC23}\explorer.exe")]
        [InlineData(@"C:\Users\me\AppData\Local\Discord\app-1.0.9258\Discord.exe", @"C:\Users\me\AppData\Local\Discord\app-1.0.9258\Discord.exe")]
        [InlineData(@"C:\Program FilesX\app.exe", @"C:\Program FilesX\app.exe")]
        [InlineData(@"c:\program files\Vendor\App.exe", @"{6D809377-6AF0-444B-8957-A3773F02200E}\Vendor\App.exe")]
        public void Paths_are_written_the_way_shell32_writes_them(string path, string expected)
        {
            Assert.Equal(expected, ShellPaths.ToNotifyIconForm(path, Folders));
        }
    }
}
