using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FreeSystemTray.Scaling
{
    /// <summary>How icons are drawn. Set once on the icon list; every <see cref="ScaledIcon"/> inherits it.</summary>
    public sealed record IconRenderOptions(IconScaling Scaling, bool SnapToPixels, IReadOnlyList<string> ExeIcons)
    {
        public static readonly IconRenderOptions Default = new(IconScaling.Smooth, true, Array.Empty<string>());

        /// <summary>
        /// Whether an icon is drawn with its app's .exe icon instead of the image the app handed over:
        /// the list names its exe file ("Discord.exe"), its full path, or its id.
        /// </summary>
        public bool UsesExeIcon(string? id, string? ownerPath)
        {
            if (ExeIcons.Count == 0 || string.IsNullOrEmpty(ownerPath)) return false;
            string file = Path.GetFileName(ownerPath);
            foreach (string entry in ExeIcons)
            {
                string e = entry.Trim();
                if (e.Equals(file, StringComparison.OrdinalIgnoreCase) || e.Equals(ownerPath, StringComparison.OrdinalIgnoreCase) ||
                    (id != null && e.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Draws a tray icon at its laid-out size. The image is first scaled to the exact number of
    /// screen pixels the element covers on its monitor (<see cref="IconResampler"/>), so with
    /// snapping on it is drawn 1:1, and it is scaled again when the element moves to a monitor with
    /// another DPI. The app hands over one small image (usually 16 px); there is no larger one to ask
    /// for, so big sizes are upscaled.
    /// </summary>
    public sealed class ScaledIcon : FrameworkElement
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(ImageSource), typeof(ScaledIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IconIdProperty = DependencyProperty.Register(
            nameof(IconId), typeof(string), typeof(ScaledIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty OwnerPathProperty = DependencyProperty.Register(
            nameof(OwnerPath), typeof(string), typeof(ScaledIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty OptionsProperty = DependencyProperty.RegisterAttached(
            "Options", typeof(IconRenderOptions), typeof(ScaledIcon),
            new FrameworkPropertyMetadata(IconRenderOptions.Default, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => (d as ScaledIcon)?.UpdateBitmapScalingMode()));

        private BitmapSource? _scaledFrom;
        private int _scaledWidth, _scaledHeight;
        private IconScaling _scaledMode;
        private BitmapSource? _scaled;

        public ScaledIcon() => UpdateBitmapScalingMode();

        public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
        public string? IconId { get => (string?)GetValue(IconIdProperty); set => SetValue(IconIdProperty, value); }
        public string? OwnerPath { get => (string?)GetValue(OwnerPathProperty); set => SetValue(OwnerPathProperty, value); }
        public IconRenderOptions Options => GetOptions(this);

        public static IconRenderOptions GetOptions(DependencyObject d) => (IconRenderOptions)d.GetValue(OptionsProperty);

        public static void SetOptions(DependencyObject d, IconRenderOptions value) => d.SetValue(OptionsProperty, value);

        /// <summary>The screen pixels last drawn at (diagnostics), or zero.</summary>
        public int DrawnPixels => _scaled?.PixelWidth ?? 0;

        /// <summary>The source's size in pixels (diagnostics): the app's image, or the .exe icon.</summary>
        public int SourcePixels => _scaledFrom?.PixelWidth ?? 0;

        private void UpdateBitmapScalingMode()
        {
            IconRenderOptions o = Options;
            // Snapped, the scaled image covers whole screen pixels and is drawn 1:1: no second filter.
            // Otherwise WPF fits it into a fractional size, with the chosen kind of filter.
            RenderOptions.SetBitmapScalingMode(this, o.SnapToPixels || o.Scaling == IconScaling.NearestNeighbor
                ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var rect = new Rect(0, 0, w, h);
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            // Snapped, these are whole numbers already (up to rounding error).
            int pw = Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX - 0.01));
            int ph = Math.Max(1, (int)Math.Ceiling(h * dpi.DpiScaleY - 0.01));
            IconRenderOptions o = Options;

            BitmapSource? source = null;
            if (OwnerPath is string exe && o.UsesExeIcon(IconId, exe)) source = ExeIcons.Get(exe, Math.Max(pw, ph));
            source ??= Source as BitmapSource;
            if (source == null)
            {
                if (Source != null) dc.DrawImage(Source, rect); // not a bitmap: let WPF scale it
                return;
            }
            if (!ReferenceEquals(source, _scaledFrom) || pw != _scaledWidth || ph != _scaledHeight || o.Scaling != _scaledMode || _scaled == null)
            {
                _scaled = source.PixelWidth == pw && source.PixelHeight == ph ? source : IconResampler.Resize(source, pw, ph, o.Scaling);
                _scaledFrom = source;
                _scaledWidth = pw;
                _scaledHeight = ph;
                _scaledMode = o.Scaling;
            }
            dc.DrawImage(_scaled, rect);
        }
    }

    /// <summary>Apps' .exe icons at a given size, cached.</summary>
    internal static class ExeIcons
    {
        private static readonly Dictionary<(string Path, int Size), BitmapSource?> Cache = new();

        /// <summary>The exe's first icon at <paramref name="size"/> pixels (Windows picks and scales its best image), or null.</summary>
        public static BitmapSource? Get(string path, int size)
        {
            var key = (path.ToUpperInvariant(), size);
            if (Cache.TryGetValue(key, out BitmapSource? cached)) return cached;
            BitmapSource? bitmap = null;
            // nIconSize: the large icon's size in the low word.
            if (SHDefExtractIcon(path, 0, 0, out IntPtr icon, IntPtr.Zero, (uint)Math.Clamp(size, 1, 0xFFFF)) == 0 && icon != IntPtr.Zero)
            {
                try
                {
                    bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                }
                finally
                {
                    DestroyIcon(icon);
                }
            }
            if (Cache.Count > 256) Cache.Clear();
            Cache[key] = bitmap;
            return bitmap;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out IntPtr large, IntPtr small, uint iconSize);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);
    }
}
