using System.Text;

namespace SwipeMaze.Core;

/// <summary>
/// A game in progress, as saved for one player: the level, the exact maze (mazes are random, so it can't
/// be rebuilt from the level alone) and the trail walked so far, which also says where the avatar is.
/// Stored as four lines of plain text: a header, the level, the maze (base64) and the trail as one
/// digit per step (0 up, 1 right, 2 down, 3 left, from the start square).
/// </summary>
public sealed class SavedGame
{
    private const string Header = "SWIPEMAZE-GAME-1";

    public SavedGame(int level, Maze maze, string trail)
    {
        Level = level;
        Maze = maze;
        Trail = trail;
    }

    public int Level { get; }
    public Maze Maze { get; }
    public string Trail { get; }

    /// <summary>Snapshot of a run that is being played.</summary>
    public static SavedGame From(MazeRun run)
    {
        var path = run.Attempt.Path;
        var trail = new StringBuilder(path.Count);
        for (var i = 1; i < path.Count; i++)
        {
            var d = Enum.GetValues<Direction>().Single(d => path[i - 1].Col + d.Dx() == path[i].Col && path[i - 1].Row + d.Dy() == path[i].Row);
            trail.Append((char)('0' + (int)d));
        }
        return new SavedGame(run.Level, run.Maze, trail.ToString());
    }

    public string Serialize() =>
        string.Join('\n', Header, Level.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(Maze.Export()), Trail);

    /// <summary>Reads <see cref="Serialize"/> text. Null for anything damaged or not from this game.</summary>
    public static SavedGame? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if (lines.Length != 4 || lines[0] != Header) return null;

        if (!int.TryParse(lines[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var level) ||
            level < 1 || level > MazeLevels.Count)
            return null;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(lines[2]); }
        catch (FormatException) { return null; }

        var size = MazeLevels.SizeFor(level);
        var maze = Maze.Import(size, size, bytes);
        if (maze is null || lines[3].Any(c => c < '0' || c > '3')) return null;

        return new SavedGame(level, maze, lines[3]);
    }

    /// <summary>
    /// Puts the avatar back where it was. Null if the trail doesn't fit the maze (every step must be an
    /// open passage onto a new square, and it can't already be at the finish).
    /// </summary>
    public MazeRun? ToRun(Func<int, Maze>? createMaze = null)
    {
        var attempt = new MazeAttempt(Maze);
        foreach (var c in Trail)
            if (attempt.Move((Direction)(c - '0')) != MoveResult.Moved) return null;

        return new MazeRun(Level, Maze, attempt, createMaze);
    }
}
