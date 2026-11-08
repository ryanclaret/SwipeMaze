namespace SwipeMaze.Core;

/// <summary>
/// A rectangular maze of cells addressed (col, row), row 0 at the top. Walls are stored as the
/// passages that are OPEN between neighbouring cells; everything else is wall. The outer border is
/// solid except for two openings that are implied rather than stored: the maze is entered from the
/// top of the middle column (<see cref="Start"/>) and left through the bottom of the middle column
/// (<see cref="Finish"/>) — the same layout as the printed maze book this game is modelled on.
/// </summary>
public sealed class Maze
{
    private readonly byte[] _open;

    public Maze(int cols, int rows)
    {
        if (cols < 2) throw new ArgumentOutOfRangeException(nameof(cols), cols, "A maze needs at least 2 columns.");
        if (rows < 2) throw new ArgumentOutOfRangeException(nameof(rows), rows, "A maze needs at least 2 rows.");
        Cols = cols;
        Rows = rows;
        _open = new byte[cols * rows];
    }

    public int Cols { get; }
    public int Rows { get; }

    public (int Col, int Row) Start => (Cols / 2, 0);
    public (int Col, int Row) Finish => (Cols / 2, Rows - 1);

    public bool InBounds(int col, int row) => col >= 0 && col < Cols && row >= 0 && row < Rows;

    /// <summary>True if you can walk from this cell in that direction (no wall in the way).</summary>
    public bool IsOpen(int col, int row, Direction d) =>
        InBounds(col, row) && (_open[row * Cols + col] & (1 << (int)d)) != 0;

    /// <summary>Knocks down the wall between a cell and its neighbour in that direction.</summary>
    public void Carve(int col, int row, Direction d)
    {
        var nc = col + d.Dx();
        var nr = row + d.Dy();
        if (!InBounds(col, row) || !InBounds(nc, nr))
            throw new ArgumentOutOfRangeException(nameof(d), d, "Can't carve through the outer border.");

        _open[row * Cols + col] |= (byte)(1 << (int)d);
        _open[nr * Cols + nc] |= (byte)(1 << (int)d.Opposite());
    }

    /// <summary>The maze's passages as bytes (one per cell, row by row), for saving. See <see cref="Import"/>.</summary>
    public byte[] Export() => (byte[])_open.Clone();

    /// <summary>Rebuilds a maze from <see cref="Export"/> bytes. Null if they don't describe a sound maze:
    /// wrong length, unknown bits, a passage through the border, or a passage open from one side only.</summary>
    public static Maze? Import(int cols, int rows, byte[] data)
    {
        if (cols < 2 || rows < 2 || data.Length != cols * rows) return null;

        var maze = new Maze(cols, rows);
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var bits = data[row * cols + col];
                if (bits > 0b1111) return null;
                foreach (var d in Enum.GetValues<Direction>())
                {
                    if ((bits & (1 << (int)d)) == 0) continue;
                    var nc = col + d.Dx();
                    var nr = row + d.Dy();
                    if (!maze.InBounds(nc, nr)) return null;
                    if ((data[nr * cols + nc] & (1 << (int)d.Opposite())) == 0) return null;
                }
            }
        }

        Buffer.BlockCopy(data, 0, maze._open, 0, data.Length);
        return maze;
    }

    /// <summary>How many walls have been knocked down. A "perfect" maze (exactly one route between
    /// any two cells) has Cols * Rows - 1.</summary>
    public int PassageCount
    {
        get
        {
            var bits = 0;
            foreach (var b in _open) bits += System.Numerics.BitOperations.PopCount(b);
            return bits / 2; // every passage is stored from both sides
        }
    }
}
