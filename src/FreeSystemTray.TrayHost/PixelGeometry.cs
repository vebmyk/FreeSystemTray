using System;

namespace FreeSystemTray.TrayHost
{
    /// <summary>A point in physical screen pixels (virtual-screen coordinates).</summary>
    public readonly record struct PixelPoint(int X, int Y);

    /// <summary>A size in physical pixels.</summary>
    public readonly record struct PixelSize(int Width, int Height);

    /// <summary>A rectangle in physical screen pixels. Right and Bottom are exclusive, as in RECT.</summary>
    public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public PixelSize Size => new(Width, Height);
        public PixelPoint Center => new(Left + Width / 2, Top + Height / 2);

        public static PixelRect FromPointAndSize(PixelPoint topLeft, PixelSize size) =>
            new(topLeft.X, topLeft.Y, topLeft.X + size.Width, topLeft.Y + size.Height);

        public bool Contains(PixelPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

        public bool Contains(PixelRect r) => r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;

        public PixelRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

        public long IntersectionArea(PixelRect other)
        {
            long w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
            long h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
            return w > 0 && h > 0 ? w * h : 0;
        }

        /// <summary>Squared distance from <paramref name="p"/> to the nearest point of this rectangle (0 inside).</summary>
        public long DistanceSquaredTo(PixelPoint p)
        {
            long dx = p.X < Left ? Left - p.X : p.X >= Right ? p.X - (Right - 1) : 0;
            long dy = p.Y < Top ? Top - p.Y : p.Y >= Bottom ? p.Y - (Bottom - 1) : 0;
            return dx * dx + dy * dy;
        }

        public override string ToString() => $"[{Left},{Top} {Width}x{Height}]";
    }
}
