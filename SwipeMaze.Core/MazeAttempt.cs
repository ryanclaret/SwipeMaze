namespace SwipeMaze.Core;

public enum MoveResult
{
    /// <summary>A wall is in the way; nothing happened.</summary>
    Wall,
    /// <summary>Moved one square forward.</summary>
    Moved,
    /// <summary>Moved one square back along the path (the last square of the trail is rubbed out).</summary>
    BackedUp,
    /// <summary>Moved onto the finish square.</summary>
    Finished,
}

/// <summary>
/// One walk through a maze from the start square. Each move is exactly one square. The squares walked
/// so far form the trail; you can back up one square at a time by moving back onto the previous square,
/// or in one go with <see cref="BackUpTo"/>. Backing up rubs the trail out behind you, so a square is
/// only ever on the trail once.
/// </summary>
public sealed class MazeAttempt
{
    private readonly bool[] _onPath;
    private readonly List<(int Col, int Row)> _path = new();

    public MazeAttempt(Maze maze)
    {
        Maze = maze;
        _onPath = new bool[maze.Cols * maze.Rows];
        var (col, row) = maze.Start;
        Col = col;
        Row = row;
        _onPath[row * maze.Cols + col] = true;
        _path.Add((col, row));
    }

    public Maze Maze { get; }
    public int Col { get; private set; }
    public int Row { get; private set; }

    /// <summary>The trail: every square from the start to the avatar's, in order.</summary>
    public IReadOnlyList<(int Col, int Row)> Path => _path;

    /// <summary>True once the avatar has moved off the start square.</summary>
    public bool HasMoved => _path.Count > 1;

    public bool IsFinished => (Col, Row) == Maze.Finish;

    public bool IsOnPath(int col, int row) => Maze.InBounds(col, row) && _onPath[row * Maze.Cols + col];

    /// <summary>True if there's a way forward onto a square that isn't already on the trail.</summary>
    public bool HasForwardMoves()
    {
        foreach (var d in Enum.GetValues<Direction>())
            if (Maze.IsOpen(Col, Row, d) && !IsOnPath(Col + d.Dx(), Row + d.Dy())) return true;
        return false;
    }

    /// <summary>Nowhere to go but back.</summary>
    public bool IsDeadEnd => !IsFinished && !HasForwardMoves();

    public MoveResult Move(Direction d)
    {
        if (IsFinished) throw new InvalidOperationException("This maze is already solved.");

        if (!Maze.IsOpen(Col, Row, d)) return MoveResult.Wall;

        var nc = Col + d.Dx();
        var nr = Row + d.Dy();

        if (IsOnPath(nc, nr))
        {
            // Back onto the square just left is stepping back. Any other square already on the trail
            // can't be walked onto (in a proper maze there isn't one next to you anyway).
            if (_path.Count >= 2 && _path[^2] == (nc, nr))
            {
                _onPath[Row * Maze.Cols + Col] = false;
                _path.RemoveAt(_path.Count - 1);
                Col = nc;
                Row = nr;
                return MoveResult.BackedUp;
            }
            return MoveResult.Wall;
        }

        Col = nc;
        Row = nr;
        _onPath[nr * Maze.Cols + nc] = true;
        _path.Add((nc, nr));
        return IsFinished ? MoveResult.Finished : MoveResult.Moved;
    }

    /// <summary>
    /// One swipe: moves in that direction and keeps going without further swipes until there is a choice
    /// to make, yielding the result of every square moved. Going forward the avatar follows the corridor
    /// (round bends too) and stops at a fork, a dead end or the finish. Going back along the trail it
    /// stops at the first square that has another way to go, or at the start. A swipe into a wall yields
    /// a single <see cref="MoveResult.Wall"/>. The attempt is updated as the results are enumerated, so
    /// a caller can pause between squares to animate it.
    /// </summary>
    public IEnumerable<MoveResult> Run(Direction first)
    {
        var from = (Col, Row);
        var result = Move(first);
        yield return result;

        if (result is MoveResult.Wall or MoveResult.Finished) yield break;

        if (result == MoveResult.BackedUp)
        {
            while (_path.Count > 1 && !HasBranchOtherThan(from))
            {
                from = (Col, Row);
                var towards = _path[^2];
                yield return Move(DirectionBetween((Col, Row), towards));
            }
            yield break;
        }

        while (true)
        {
            // A corridor has exactly one way on. None means a dead end, more than one a fork.
            Direction? only = null;
            var ways = 0;
            foreach (var d in Enum.GetValues<Direction>())
            {
                if (!Maze.IsOpen(Col, Row, d) || IsOnPath(Col + d.Dx(), Row + d.Dy())) continue;
                ways++;
                only = d;
            }
            if (ways != 1) yield break;

            result = Move(only!.Value);
            yield return result;
            if (result == MoveResult.Finished) yield break;
        }
    }

    /// <summary>True if there's a way to go from here, besides the square just left, onto a square that
    /// isn't on the trail — i.e. backing up has reached a fork.</summary>
    private bool HasBranchOtherThan((int Col, int Row) left)
    {
        foreach (var d in Enum.GetValues<Direction>())
        {
            var target = (Col + d.Dx(), Row + d.Dy());
            if (Maze.IsOpen(Col, Row, d) && !IsOnPath(target.Item1, target.Item2) && target != left) return true;
        }
        return false;
    }

    private static Direction DirectionBetween((int Col, int Row) from, (int Col, int Row) to) =>
        Enum.GetValues<Direction>().Single(d => from.Col + d.Dx() == to.Col && from.Row + d.Dy() == to.Row);

    /// <summary>Puts the avatar back on an earlier square of the trail and rubs out the trail after it.
    /// False (nothing changes) if that square isn't on the trail or is where the avatar already is.</summary>
    public bool BackUpTo(int col, int row)
    {
        if (IsFinished || !IsOnPath(col, row) || (col, row) == (Col, Row)) return false;

        var keep = _path.IndexOf((col, row)) + 1;
        for (var i = keep; i < _path.Count; i++) _onPath[_path[i].Row * Maze.Cols + _path[i].Col] = false;
        _path.RemoveRange(keep, _path.Count - keep);
        Col = col;
        Row = row;
        return true;
    }

    /// <summary>
    /// Which earlier trail square did a tap at (<paramref name="col"/>, <paramref name="row"/>) mean?
    /// The position is in cell units with every square's centre at a whole number (so square (3,4) is
    /// centred on 3.0, 4.0). Finds the closest square of the trail, as long as it's within
    /// <paramref name="maxDistance"/> and isn't the avatar's own square — tapping the avatar means
    /// nothing, and a tap nearer the avatar than anything else must not be read as "go back one".
    /// </summary>
    public bool TryFindBackUpTarget(double col, double row, double maxDistance, out (int Col, int Row) target)
    {
        target = default;
        if (IsFinished) return false;

        var best = double.MaxValue;
        (int Col, int Row)? nearest = null;
        foreach (var cell in _path)
        {
            var dx = cell.Col - col;
            var dy = cell.Row - row;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < best)
            {
                best = distance;
                nearest = cell;
            }
        }

        if (nearest is not { } found || best > maxDistance || found == (Col, Row)) return false;
        target = found;
        return true;
    }
}
