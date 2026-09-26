using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreeSystemTray.Scaling;
using Xunit;

namespace FreeSystemTray.Tests
{
    public class SizeValueTests
    {
        [Theory]
        [InlineData("167%", 167, true)]
        [InlineData(" 50 % ", 50, true)]
        [InlineData("24px", 24, false)]
        [InlineData("24.5PX", 24.5, false)]
        [InlineData("24", 24, false)]
        [InlineData("0.5", 0.5, false)]
        public void Sizes_are_percentages_or_pixels(string text, double value, bool percent)
        {
            Assert.Equal(new SizeValue(value, percent), SizeValue.Parse(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData("big")]
        [InlineData("24pt")]
        [InlineData("-5%")]
        [InlineData("NaN")]
        [InlineData(null)]
        public void Anything_else_is_rejected(string? text) => Assert.Throws<FormatException>(() => SizeValue.Parse(text));

        [Fact]
        public void A_percentage_is_of_the_base_size_and_pixels_are_taken_as_they_are()
        {
            Assert.Equal(26.72, SizeValue.Parse("167%").Resolve(16), 6);
            Assert.Equal(24, SizeValue.Parse("24px").Resolve(16));
            Assert.Equal("167%", SizeValue.Parse("167%").ToString());
            Assert.Equal("24.5px", SizeValue.Parse("24.5").ToString());
        }
    }

    public class TrayLayoutTests
    {
        private static TrayLayout L(string icon, string tray) => TrayLayout.Compute(SizeValue.Parse(icon), SizeValue.Parse(tray));

        [Fact]
        public void At_100_percent_it_is_the_phase_1_tray()
        {
            TrayLayout l = L("100%", "100%");
            Assert.Equal(new TrayLayout(16, 24, 10, 1, 3, 2), l);
            Assert.Equal(30, l.Thickness);
        }

        [Fact]
        public void The_icon_size_leaves_the_padding_grip_and_border_alone()
        {
            TrayLayout l = L("167%", "100%");
            Assert.Equal(26.72, l.IconSize, 6);
            Assert.Equal(26.72 + 8, l.CellSize, 6);
            Assert.Equal(10, l.GripWidth);
            Assert.Equal(1, l.BorderThickness);
        }

        [Fact]
        public void The_tray_size_as_a_percentage_scales_everything_but_the_icons()
        {
            TrayLayout l = L("100%", "150%");
            Assert.Equal(new TrayLayout(16, 28, 15, 1.5, 4.5, 3), l);
        }

        [Fact]
        public void The_tray_size_in_pixels_is_the_cell_size_whatever_the_icon_size()
        {
            Assert.Equal(40, L("16px", "40px").CellSize);
            Assert.Equal(40, L("32px", "40px").CellSize);
            Assert.Equal(1, L("32px", "40px").BorderThickness); // (40 - 32) / 8: the 100% padding
        }

        [Fact]
        public void A_cell_smaller_than_its_icon_just_loses_its_padding_and_the_grip_stays_usable()
        {
            TrayLayout l = L("48px", "20px");
            Assert.Equal(48, l.CellSize);
            Assert.Equal(0, l.BorderThickness);
            Assert.Equal(TrayLayout.MinGripWidth, l.GripWidth);
        }

        [Theory]
        [InlineData("1px", 4)]
        [InlineData("10000%", 512)]
        [InlineData("13.3px", 13.3)]
        [InlineData("333%", 53.28)]
        public void Icon_sizes_are_used_as_given_within_sanity_limits(string icon, double expected)
        {
            Assert.Equal(expected, L(icon, "100%").IconSize, 6);
        }
    }

    public class IconResamplerTests
    {
        private static byte[] Solid(int w, int h, byte b, byte g, byte r, byte a) =>
            Enumerable.Range(0, w * h).SelectMany(_ => new[] { b, g, r, a }).ToArray();

        private static byte[] At(byte[] px, int w, int x, int y) => px.Skip((y * w + x) * 4).Take(4).ToArray();

        [Fact]
        public void Nearest_neighbour_at_200_percent_turns_each_pixel_into_a_2x2_block()
        {
            // 2x1: red, blue.
            byte[] src = { 0, 0, 255, 255, 255, 0, 0, 255 };
            byte[] dst = IconResampler.Resample(src, 2, 1, 4, 2, IconScaling.NearestNeighbor);
            foreach ((int x, int y) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) }) Assert.Equal(new byte[] { 0, 0, 255, 255 }, At(dst, 4, x, y));
            foreach ((int x, int y) in new[] { (2, 0), (3, 0), (2, 1), (3, 1) }) Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(dst, 4, x, y));
        }

        [Theory]
        [InlineData(IconScaling.Smooth)]
        [InlineData(IconScaling.NearestNeighbor)]
        public void The_same_size_gives_the_same_pixels(IconScaling mode)
        {
            var random = new Random(1);
            byte[] src = new byte[16 * 16 * 4];
            for (int i = 0; i < src.Length; i += 4)
            {
                byte a = (byte)random.Next(256);
                src[i] = (byte)random.Next(a + 1); src[i + 1] = (byte)random.Next(a + 1); src[i + 2] = (byte)random.Next(a + 1); src[i + 3] = a;
            }
            Assert.Equal(src, IconResampler.Resample(src, 16, 16, 16, 16, mode));
        }

        [Theory]
        [InlineData(16, 27)]
        [InlineData(16, 48)]
        [InlineData(32, 20)]
        public void Smooth_scaling_keeps_a_flat_colour_flat(int from, int to)
        {
            byte[] dst = IconResampler.Resample(Solid(from, from, 40, 120, 200, 255), from, from, to, to, IconScaling.Smooth);
            for (int i = 0; i < dst.Length; i += 4) Assert.Equal(new byte[] { 40, 120, 200, 255 }, dst.Skip(i).Take(4).ToArray());
        }

        [Fact]
        public void Smooth_scaling_of_an_edge_against_transparency_gives_no_dark_fringe()
        {
            // Opaque red on the left half, transparent on the right, 8x8, scaled to 27x27.
            byte[] src = new byte[8 * 8 * 4];
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 4; x++)
            {
                int i = (y * 8 + x) * 4;
                src[i + 2] = 255; src[i + 3] = 255;
            }
            byte[] dst = IconResampler.Resample(src, 8, 8, 27, 27, IconScaling.Smooth);
            for (int i = 0; i < dst.Length; i += 4)
            {
                byte a = dst[i + 3];
                // Premultiplied: a pure red pixel at any alpha has red = alpha and nothing else.
                Assert.Equal(0, dst[i]);
                Assert.Equal(0, dst[i + 1]);
                Assert.Equal(a, dst[i + 2]);
            }
            Assert.Contains(dst.Where((_, i) => i % 4 == 3), a => a > 0 && a < 255); // there is a soft edge
        }

        [Fact]
        public void Shrinking_averages_fine_detail_instead_of_skipping_it()
        {
            // A 1 px black and white checkerboard, halved.
            byte[] src = new byte[8 * 8 * 4];
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                byte v = (byte)((x + y) % 2 == 0 ? 255 : 0);
                int i = (y * 8 + x) * 4;
                src[i] = v; src[i + 1] = v; src[i + 2] = v; src[i + 3] = 255;
            }
            byte[] dst = IconResampler.Resample(src, 8, 8, 4, 4, IconScaling.Smooth);
            for (int i = 0; i < dst.Length; i += 4) Assert.InRange(dst[i], 110, 145);
        }

        [Fact]
        public void A_bitmap_is_resized_to_the_exact_pixels_and_a_wide_one_is_centred()
        {
            BitmapSource wide = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Pbgra32, null, Solid(4, 2, 0, 0, 255, 255), 16);
            BitmapSource r = IconResampler.Resize(wide, 8, 8, IconScaling.NearestNeighbor);
            Assert.Equal(8, r.PixelWidth);
            Assert.Equal(8, r.PixelHeight);
            var px = new byte[8 * 8 * 4];
            r.CopyPixels(px, 32, 0);
            Assert.Equal(0, At(px, 8, 3, 0)[3]);   // above the image: transparent
            Assert.Equal(255, At(px, 8, 3, 3)[3]); // rows 2 to 5 hold the image
            Assert.Equal(0, At(px, 8, 3, 7)[3]);
        }
    }

    public class IconRenderOptionsTests
    {
        private static readonly IconRenderOptions Options = new(IconScaling.Smooth, true, new[] { "Discord.exe", @"C:\Tools\app.exe", "7820ae73-23e3-4229-82c1-e41cb67d5b9c" });

        [Theory]
        [InlineData(null, @"C:\Users\me\AppData\Local\Discord\app-1.0.9258\discord.EXE", true)]
        [InlineData(null, @"c:\tools\APP.exe", true)]
        [InlineData("7820ae73-23e3-4229-82c1-e41cb67d5b9c", @"C:\Windows\explorer.exe", true)]
        [InlineData("7820ae74-23e3-4229-82c1-e41cb67d5b9c", @"C:\Windows\explorer.exe", false)]
        [InlineData(null, @"C:\Other\app.exe", false)]
        [InlineData("Discord.exe", null, false)]
        public void The_exe_icon_list_matches_exe_names_paths_and_ids(string? id, string? path, bool expected)
        {
            Assert.Equal(expected, Options.UsesExeIcon(id, path));
        }
    }
}
