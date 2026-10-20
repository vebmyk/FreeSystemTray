using System;

namespace FreeSystemTray.Icons
{
    /// <summary>
    /// How the tray arranges its icons: <see cref="Columns"/> × <see cref="Rows"/>, where 0 means as
    /// many as needed. 0 × 1 is a row, 1 × 0 a column, 4 × 0 rows of four, 0 × 2 two rows that grow
    /// to the right. With both set the grid has a fixed number of cells (<see cref="Capacity"/>):
    /// what does not fit is behind a button in the last cell, which expands the tray with more rows
    /// of the same width. Pure logic.
    /// </summary>
    public sealed record IconGrid
    {
        public const int MaxCells = 64;

        public static readonly IconGrid Row = new(0, 1);

        /// <summary>Out-of-range values are clamped; 0 × 0 is a row.</summary>
        public IconGrid(int columns, int rows)
        {
            Columns = Math.Clamp(columns, 0, MaxCells);
            Rows = Math.Clamp(rows, 0, MaxCells);
            if (Columns == 0 && Rows == 0) Rows = 1;
        }

        public int Columns { get; }
        public int Rows { get; }

        /// <summary>Both sizes are set: the tray never grows past them, except when expanded.</summary>
        public bool IsFixed => Columns > 0 && Rows > 0;

        /// <summary>How many cells the tray has (the expand button included); 0 for no limit.</summary>
        public int Capacity => IsFixed ? Columns * Rows : 0;

        /// <summary>A single column: the grip goes on top, as for a vertical taskbar.</summary>
        public bool IsColumn => Columns == 1 && Rows != 1;

        /// <summary>Only the number of rows is set: the cells fill each column top to bottom, then the next one.</summary>
        private bool ByColumn => Columns == 0;

        /// <summary>How many columns and rows <paramref name="count"/> cells take.</summary>
        public (int Columns, int Rows) Size(int count)
        {
            if (count <= 0) return (0, 0);
            if (ByColumn) return ((count + Rows - 1) / Rows, Math.Min(count, Rows));
            return (Math.Min(count, Columns), (count + Columns - 1) / Columns);
        }

        /// <summary>
        /// The column and row of the <paramref name="index"/>-th of <paramref name="count"/> cells:
        /// left to right, then top to bottom (top to bottom, then left to right with only the rows
        /// set). Cells past <see cref="Capacity"/> (an expanded tray) go in rows below the grid, or
        /// above it with <paramref name="expandUp"/>, the first nearest to it, so the grid's own
        /// cells stay where they are.
        /// </summary>
        public (int Column, int Row) CellOf(int index, int count, bool expandUp = false)
        {
            if (ByColumn) return (index / Rows, index % Rows);
            int column = index % Columns, row = index / Columns;
            if (!expandUp || !IsFixed || count <= Capacity) return (column, row);
            int extra = (count - Capacity + Columns - 1) / Columns;
            return row < Rows ? (column, row + extra) : (column, extra - 1 - (row - Rows));
        }

        public override string ToString() => $"{(Columns == 0 ? "any" : Columns)} × {(Rows == 0 ? "any" : Rows)}";
    }
}
