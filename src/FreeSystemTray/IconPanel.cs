using System;
using System.Windows;
using System.Windows.Controls;
using FreeSystemTray.Icons;

namespace FreeSystemTray
{
    /// <summary>
    /// Lays out the icon cells in an <see cref="IconGrid"/> (a row, a column, or a grid), with a gap
    /// between cells. Every cell gets the size of the largest one. Collapsed children, and children
    /// whose content is collapsed (no size), take no place.
    /// </summary>
    public sealed class IconPanel : Panel
    {
        public static readonly DependencyProperty GridProperty = DependencyProperty.Register(
            nameof(Grid), typeof(IconGrid), typeof(IconPanel), new FrameworkPropertyMetadata(IconGrid.Row, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ExpandUpProperty = DependencyProperty.Register(
            nameof(ExpandUp), typeof(bool), typeof(IconPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
            nameof(Spacing), typeof(double), typeof(IconPanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public IconGrid Grid { get => (IconGrid)GetValue(GridProperty); set => SetValue(GridProperty, value); }

        /// <summary>The rows of an expanded fixed grid go above it (a tray anchored at the bottom grows upwards).</summary>
        public bool ExpandUp { get => (bool)GetValue(ExpandUpProperty); set => SetValue(ExpandUpProperty, value); }

        public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

        private Size _cell;
        private int _count;

        /// <summary>The size of <paramref name="count"/> cells of <paramref name="cell"/> size with <paramref name="spacing"/> between them.</summary>
        public static Size Extent(IconGrid grid, int count, Size cell, double spacing)
        {
            (int cols, int rows) = grid.Size(count);
            if (cols == 0) return default;
            return new Size(cols * cell.Width + (cols - 1) * spacing, rows * cell.Height + (rows - 1) * spacing);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
            double w = 0, h = 0;
            int count = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(infinite);
                if (TakesNoPlace(child)) continue;
                w = Math.Max(w, child.DesiredSize.Width);
                h = Math.Max(h, child.DesiredSize.Height);
                count++;
            }
            _cell = new Size(w, h);
            _count = count;
            return Extent(Grid, count, _cell, Spacing);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            IconGrid grid = Grid;
            int index = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (TakesNoPlace(child))
                {
                    child.Arrange(default);
                    continue;
                }
                (int col, int row) = grid.CellOf(index++, _count, ExpandUp);
                child.Arrange(new Rect(col * (_cell.Width + Spacing), row * (_cell.Height + Spacing), _cell.Width, _cell.Height));
            }
            return finalSize;
        }

        private static bool TakesNoPlace(UIElement child) =>
            child.Visibility == Visibility.Collapsed || (child.DesiredSize.Width <= 0 && child.DesiredSize.Height <= 0);
    }

    /// <summary>Whether a mouse button is down on an icon's cell, for the theme's "pressed" look.</summary>
    public static class CellState
    {
        public static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
            "IsPressed", typeof(bool), typeof(CellState), new PropertyMetadata(false));

        public static bool GetIsPressed(DependencyObject d) => (bool)d.GetValue(IsPressedProperty);

        public static void SetIsPressed(DependencyObject d, bool value) => d.SetValue(IsPressedProperty, value);
    }
}
