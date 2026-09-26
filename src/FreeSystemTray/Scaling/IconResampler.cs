using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FreeSystemTray.Scaling
{
    public enum IconScaling
    {
        /// <summary>Bicubic (Catmull-Rom): smooth, the best for sizes that are not whole multiples.</summary>
        Smooth,

        /// <summary>Each screen pixel takes the nearest source pixel: blocky, crisp at 200%, 300% and so on.</summary>
        NearestNeighbor
    }

    /// <summary>
    /// Scales an app's icon image to the exact number of screen pixels it is drawn at, so it can be
    /// drawn 1:1. WPF's own image scaling offers no bicubic filter. Works on premultiplied BGRA, so
    /// transparent edges get no dark fringe.
    /// </summary>
    public static class IconResampler
    {
        /// <summary>
        /// The image at <paramref name="width"/>×<paramref name="height"/> pixels. A source that is
        /// not square keeps its shape and is centred.
        /// </summary>
        public static BitmapSource Resize(BitmapSource source, int width, int height, IconScaling mode)
        {
            BitmapSource pbgra = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            int sw = pbgra.PixelWidth, sh = pbgra.PixelHeight;
            var src = new byte[sw * sh * 4];
            pbgra.CopyPixels(src, sw * 4, 0);

            double fit = Math.Min((double)width / sw, (double)height / sh);
            int fw = Math.Clamp((int)Math.Round(sw * fit), 1, width), fh = Math.Clamp((int)Math.Round(sh * fit), 1, height);
            byte[] fitted = Resample(src, sw, sh, fw, fh, mode);
            byte[] dst = fitted;
            if (fw != width || fh != height)
            {
                dst = new byte[width * height * 4];
                int ox = (width - fw) / 2, oy = (height - fh) / 2;
                for (int y = 0; y < fh; y++)
                    Buffer.BlockCopy(fitted, y * fw * 4, dst, ((oy + y) * width + ox) * 4, fw * 4);
            }
            BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, dst, width * 4);
            result.Freeze();
            return result;
        }

        /// <summary>Resamples premultiplied BGRA pixels (4 bytes each, rows without padding).</summary>
        public static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh, IconScaling mode)
        {
            if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) throw new ArgumentOutOfRangeException(nameof(sw), "Sizes must be positive.");
            if (src.Length < sw * sh * 4) throw new ArgumentException("Too few pixels for the size.", nameof(src));
            if (sw == dw && sh == dh) return (byte[])src.Clone();
            return mode == IconScaling.NearestNeighbor ? Nearest(src, sw, sh, dw, dh) : Bicubic(src, sw, sh, dw, dh);
        }

        private static byte[] Nearest(byte[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y++)
            {
                int sy = Math.Min(sh - 1, (int)((y + 0.5) * sh / dh));
                for (int x = 0; x < dw; x++)
                {
                    int sx = Math.Min(sw - 1, (int)((x + 0.5) * sw / dw));
                    Buffer.BlockCopy(src, (sy * sw + sx) * 4, dst, (y * dw + x) * 4, 4);
                }
            }
            return dst;
        }

        /// <summary>Separable Catmull-Rom, first along rows, then along columns.</summary>
        private static byte[] Bicubic(byte[] src, int sw, int sh, int dw, int dh)
        {
            (int First, float[] Weights)[] wx = Weights(sw, dw), wy = Weights(sh, dh);

            var rows = new float[dw * sh * 4];
            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    (int first, float[] w) = wx[x];
                    float b = 0, g = 0, r = 0, a = 0;
                    for (int k = 0; k < w.Length; k++)
                    {
                        int i = (y * sw + Math.Clamp(first + k, 0, sw - 1)) * 4;
                        b += src[i] * w[k]; g += src[i + 1] * w[k]; r += src[i + 2] * w[k]; a += src[i + 3] * w[k];
                    }
                    int o = (y * dw + x) * 4;
                    rows[o] = b; rows[o + 1] = g; rows[o + 2] = r; rows[o + 3] = a;
                }
            }

            var dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y++)
            {
                (int first, float[] w) = wy[y];
                for (int x = 0; x < dw; x++)
                {
                    float b = 0, g = 0, r = 0, a = 0;
                    for (int k = 0; k < w.Length; k++)
                    {
                        int i = (Math.Clamp(first + k, 0, sh - 1) * dw + x) * 4;
                        b += rows[i] * w[k]; g += rows[i + 1] * w[k]; r += rows[i + 2] * w[k]; a += rows[i + 3] * w[k];
                    }
                    // The filter overshoots at sharp edges; keep the result a valid premultiplied colour.
                    int alpha = ToByte(a, 255);
                    int o = (y * dw + x) * 4;
                    dst[o] = (byte)ToByte(b, alpha); dst[o + 1] = (byte)ToByte(g, alpha); dst[o + 2] = (byte)ToByte(r, alpha); dst[o + 3] = (byte)alpha;
                }
            }
            return dst;
        }

        private static int ToByte(float v, int max) => Math.Clamp((int)MathF.Round(v), 0, max);

        /// <summary>
        /// For each destination pixel, the source pixels it takes from and their weights. When
        /// shrinking, the kernel is widened so every source pixel counts.
        /// </summary>
        private static (int First, float[] Weights)[] Weights(int srcLength, int dstLength)
        {
            double scale = (double)srcLength / dstLength;
            double widen = Math.Max(1, scale);
            double support = 2 * widen;
            var result = new (int, float[])[dstLength];
            for (int i = 0; i < dstLength; i++)
            {
                double center = (i + 0.5) * scale - 0.5;
                int first = (int)Math.Floor(center - support) + 1;
                int last = (int)Math.Ceiling(center + support) - 1;
                var w = new float[last - first + 1];
                double sum = 0;
                for (int j = first; j <= last; j++)
                {
                    double v = CatmullRom((j - center) / widen);
                    w[j - first] = (float)v;
                    sum += v;
                }
                for (int k = 0; k < w.Length; k++) w[k] = (float)(w[k] / sum);
                result[i] = (first, w);
            }
            return result;
        }

        private static double CatmullRom(double x)
        {
            x = Math.Abs(x);
            if (x < 1) return (1.5 * x - 2.5) * x * x + 1;
            if (x < 2) return ((-0.5 * x + 2.5) * x - 4) * x + 2;
            return 0;
        }
    }
}
