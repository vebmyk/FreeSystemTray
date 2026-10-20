using System.Windows;
using FreeSystemTray.Icons;
using Xunit;

namespace FreeSystemTray.Tests
{
    public class IconGridTests
    {
        [Theory]
        [InlineData(0, 1, 7, 7, 1)]   // a row
        [InlineData(1, 0, 7, 1, 7)]   // a column
        [InlineData(4, 0, 7, 4, 2)]   // rows of four
        [InlineData(4, 0, 3, 3, 1)]
        [InlineData(4, 0, 8, 4, 2)]
        [InlineData(0, 2, 5, 3, 2)]   // two rows that grow to the right
        [InlineData(0, 2, 1, 1, 1)]
        [InlineData(3, 2, 6, 3, 2)]   // a fixed grid, full
        [InlineData(3, 2, 8, 3, 3)]   // expanded by one row
        [InlineData(0, 0, 3, 3, 1)]   // 0 × 0 is a row
        [InlineData(4, 0, 0, 0, 0)]
        public void Icons_take_columns_and_rows_by_the_grid(int columns, int rows, int count, int cols, int rowsTaken)
        {
            Assert.Equal((cols, rowsTaken), new IconGrid(columns, rows).Size(count));
        }

        [Fact]
        public void Sizes_are_kept_within_limits()
        {
            var grid = new IconGrid(-3, 500);
            Assert.Equal(0, grid.Columns);
            Assert.Equal(IconGrid.MaxCells, grid.Rows);
            Assert.False(grid.IsFixed);
            Assert.Equal(0, grid.Capacity);
            Assert.Equal(6, new IconGrid(3, 2).Capacity);
        }

        [Fact]
        public void A_grid_fills_left_to_right_then_top_to_bottom()
        {
            var grid = new IconGrid(3, 0);
            Assert.Equal((0, 0), grid.CellOf(0, 5));
            Assert.Equal((2, 0), grid.CellOf(2, 5));
            Assert.Equal((0, 1), grid.CellOf(3, 5));
            Assert.Equal((0, 4), new IconGrid(1, 0).CellOf(4, 5));
        }

        [Fact]
        public void With_only_the_rows_set_it_fills_top_to_bottom_then_left_to_right()
        {
            var grid = new IconGrid(0, 2);
            Assert.Equal((0, 0), grid.CellOf(0, 5));
            Assert.Equal((0, 1), grid.CellOf(1, 5));
            Assert.Equal((1, 0), grid.CellOf(2, 5));
            Assert.Equal((2, 0), grid.CellOf(4, 5));
        }

        [Fact]
        public void An_expanded_grid_grows_below_or_above_and_its_own_cells_keep_their_order()
        {
            var grid = new IconGrid(2, 2);
            // 7 cells: the 2 × 2 grid and two more rows.
            Assert.Equal((1, 1), grid.CellOf(3, 7));
            Assert.Equal((0, 2), grid.CellOf(4, 7));
            Assert.Equal((0, 3), grid.CellOf(6, 7));
            // Upwards: the grid moves down by the two extra rows; the first extra row is right above it.
            Assert.Equal((0, 2), grid.CellOf(0, 7, expandUp: true));
            Assert.Equal((1, 3), grid.CellOf(3, 7, expandUp: true));
            Assert.Equal((0, 1), grid.CellOf(4, 7, expandUp: true));
            Assert.Equal((1, 1), grid.CellOf(5, 7, expandUp: true));
            Assert.Equal((0, 0), grid.CellOf(6, 7, expandUp: true));
            // Not expanded: upwards changes nothing.
            Assert.Equal((1, 1), grid.CellOf(3, 4, expandUp: true));
        }

        [Fact]
        public void Spacing_goes_between_cells_only()
        {
            Assert.Equal(new Size(3 * 24 + 2 * 2, 24), IconPanel.Extent(IconGrid.Row, 3, new Size(24, 24), 2));
            Assert.Equal(new Size(2 * 24 + 1 * 2, 2 * 24 + 1 * 2), IconPanel.Extent(new IconGrid(2, 0), 3, new Size(24, 24), 2));
            Assert.Equal(new Size(24, 24), IconPanel.Extent(new IconGrid(1, 0), 1, new Size(24, 24), 5));
        }
    }
}
