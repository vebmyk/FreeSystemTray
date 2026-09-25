using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FreeSystemTray.TrayHost.Interop
{
    internal static class IconHandles
    {
        /// <summary>
        /// A new 32-bit HICON with the bitmap's pixels, for re-adding an icon to Explorer when the
        /// app's own handle is not at hand. The caller destroys it; Explorer keeps its own copy.
        /// </summary>
        public static IntPtr CreateFromBitmap(BitmapSource source)
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
            if (width <= 0 || height <= 0) return IntPtr.Zero;
            var pixels = new byte[stride * height];
            bgra.CopyPixels(pixels, stride, 0);

            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down, like WPF's pixel buffer
                biPlanes = 1,
                biBitCount = 32
            };
            IntPtr color = NativeMethods.CreateDIBSection(IntPtr.Zero, ref header, 0 /* DIB_RGB_COLORS */, out IntPtr bits, IntPtr.Zero, 0);
            if (color == IntPtr.Zero) return IntPtr.Zero;
            // All-zero AND mask: transparency comes from the alpha channel. Rows are WORD-aligned.
            IntPtr mask = NativeMethods.CreateBitmap(width, height, 1, 1, new byte[(width + 15) / 16 * 2 * height]);
            try
            {
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                var info = new NativeMethods.ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
                return NativeMethods.CreateIconIndirect(ref info);
            }
            finally
            {
                NativeMethods.DeleteObject(color);
                if (mask != IntPtr.Zero) NativeMethods.DeleteObject(mask);
            }
        }
    }
}
