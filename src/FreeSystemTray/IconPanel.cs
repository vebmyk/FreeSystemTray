using System;
using System.Windows;
using System.Windows.Controls;
using FreeSystemTray.Theming;

namespace FreeSystemTray
{
    /// <summary>
    /// Lays out the icon cells in a row, a column, or rows of a fixed number of columns, with a gap
    /// between cells. Every cell gets the size of the largest one. Collapsed children, and children
    /// whose content is collapsed (no size), take no place.
    /// </summary>
    public sealed class IconPanel : Panel
    {
        public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
            nameof(Layout), typeof(IconLayout), typeof(IconPanel), new FrameworkPropertyMetadata(IconLayout.Row, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
            nameof(Columns), typeof(int), typeof(IconPanel), new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
            nameof(Spacing), typeof(double), typeof(IconPanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public IconLayout Layout { get => (IconLayout)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
        public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
        public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

        private Size _cell;

        /// <summary>How many columns and rows <paramref name="count"/> visible icons take.</summary>
        public static (int Columns, int Rows) Grid(IconLayout layout, int columns, int count)
        {
            if (count <= 0) return (0, 0);
            return layout switch
            {
                IconLayout.Column => (1, count),
                IconLayout.Grid => (Math.Min(Math.Max(1, columns), count), (count + Math.Max(1, columns) - 1) / Math.Max(1, columns)),
                _ => (count, 1)
            };
        }

        /// <summary>The column and row of the <paramref name="index"/>-th visible icon: left to right, then top to bottom.</summary>
        public static (int Column, int Row) CellOf(IconLayout layout, int columns, int index) => layout switch
        {
            IconLayout.Column => (0, index),
            IconLayout.Grid => (index % Math.Max(1, columns), index / Math.Max(1, columns)),
            _ => (index, 0)
        };

        /// <summary>The size of <paramref name="count"/> cells of <paramref name="cell"/> size with <paramref name="spacing"/> between them.</summary>
        public static Size Extent(IconLayout layout, int columns, int count, Size cell, double spacing)
        {
            (int cols, int rows) = Grid(layout, columns, count);
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
            return Extent(Layout, Columns, count, _cell, Spacing);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int index = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (TakesNoPlace(child))
                {
                    child.Arrange(default);
                    continue;
                }
                (int col, int row) = CellOf(Layout, Columns, index++);
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
