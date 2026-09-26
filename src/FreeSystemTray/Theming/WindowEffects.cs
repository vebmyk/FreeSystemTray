using System;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace FreeSystemTray.Theming
{
    /// <summary>
    /// What Windows draws behind the tray window: nothing, a blur, or Windows 10's acrylic, through the
    /// undocumented SetWindowCompositionAttribute (the documented Windows 11 backdrops need a window
    /// that is not layered, and the tray is layered so it can fade). With a blur and rounded corners,
    /// a window region clips the blur to the rounded shape.
    /// </summary>
    internal static class WindowEffects
    {
        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_DISABLED = 0, ACCENT_ENABLE_BLURBEHIND = 3, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor; // AABBGGRR
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        /// <summary>
        /// Windows' "Transparency effects" (Settings › Personalization › Colors). While it is off,
        /// Windows draws a blur or acrylic as solid black, so the tray uses its solid colour instead.
        /// Windows broadcasts WM_SETTINGCHANGE "ImmersiveColorSet" when it changes.
        /// </summary>
        public static bool TransparencyEnabled()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }

        /// <summary>Sets the blur behind the window. Returns false if Windows refused.</summary>
        public static bool SetBlur(IntPtr hwnd, TrayBlur blur, Color tint)
        {
            var policy = new AccentPolicy
            {
                AccentState = blur switch
                {
                    TrayBlur.Blur => ACCENT_ENABLE_BLURBEHIND,
                    TrayBlur.Acrylic => ACCENT_ENABLE_ACRYLICBLURBEHIND,
                    _ => ACCENT_DISABLED
                },
                // Acrylic with a fully transparent tint draws nothing at all.
                GradientColor = (uint)Math.Max((int)tint.A, 1) << 24 | (uint)tint.B << 16 | (uint)tint.G << 8 | tint.R
            };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr data = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, data, false);
                var attribute = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = data, SizeOfData = size };
                return SetWindowCompositionAttribute(hwnd, ref attribute) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        /// <summary>Clips the window to a rounded rectangle of its size in pixels, or removes the clip (radius 0).</summary>
        public static void SetRoundedRegion(IntPtr hwnd, int width, int height, int radius)
        {
            if (radius <= 0 || width <= 0 || height <= 0)
            {
                SetWindowRgn(hwnd, IntPtr.Zero, true);
                return;
            }
            // The region's right and bottom are exclusive; the ellipse is 2r across.
            IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, 2 * radius, 2 * radius);
            if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
        }

        /// <summary>
        /// Windows 11 rounds some windows' corners itself; the tray draws its own. Does nothing on
        /// Windows 10, which does not know the attribute.
        /// </summary>
        public static void DoNotRoundCorners(IntPtr hwnd)
        {
            int preference = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
