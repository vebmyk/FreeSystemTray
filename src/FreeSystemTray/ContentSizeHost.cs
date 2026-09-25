using System;
using System.Windows;
using System.Windows.Controls;

namespace FreeSystemTray
{
    /// <summary>
    /// Measures its child without limits and reports that size, so the window can size itself around
    /// the content and move its anchor corner in a single SetWindowPos.
    /// </summary>
    public sealed class ContentSizeHost : Decorator
    {
        /// <summary>The child's desired size in device-independent pixels.</summary>
        public Size ContentSize { get; private set; }

        /// <summary>Raised during layout; handlers should defer window changes.</summary>
        public event EventHandler? ContentSizeChanged;

        protected override Size MeasureOverride(Size constraint)
        {
            UIElement? child = Child;
            if (child == null) return default;
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size desired = child.DesiredSize;
            if (desired != ContentSize)
            {
                ContentSize = desired;
                ContentSizeChanged?.Invoke(this, EventArgs.Empty);
            }
            return new Size(Math.Min(desired.Width, constraint.Width), Math.Min(desired.Height, constraint.Height));
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Child?.Arrange(new Rect(arrangeSize));
            return arrangeSize;
        }
    }
}
