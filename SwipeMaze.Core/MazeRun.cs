namespace SwipeMaze.Core;

public enum RunPhase
{
    /// <summary>Waiting for swipes.</summary>
    Playing,
    /// <summary>The level's maze was just solved. Input is ignored until <see cref="MazeRun.Continue"/>.</summary>
    LevelComplete,
    /// <summary>The last level was solved. The run is over.</summary>
    AllComplete,
}

/// <summary>
/// Playing through the levels from a starting level. There is no way to lose: you can retry the maze
/// as often as you like and back up as far as you like, and the only way on is to reach the finish.
/// Solving a level moves you to the next, with a brand-new random maze one square bigger each way.
/// </summary>
public sealed class MazeRun
{
    private readonly Func<int, Maze> _createMaze;

    /// <param name="startLevel">1-based; where to pick up from.</param>
    /// <param name="createMaze">level → a new maze. Defaults to the shipped random winding mazes;
    /// tests pass tiny hand-made ones.</param>
    public MazeRun(int startLevel = 1, Func<int, Maze>? createMaze = null)
    {
        if (startLevel < 1 || startLevel > MazeLevels.Count)
            throw new ArgumentOutOfRangeException(nameof(startLevel), startLevel, $"Levels are 1..{MazeLevels.Count}.");

        _createMaze = createMaze ?? (level => MazeLevels.Create(level));
        BeginLevel(startLevel);
    }

    /// <summary>Picks up a game that was saved part-way: this maze, with the avatar already on its trail.</summary>
    internal MazeRun(int level, Maze maze, MazeAttempt attempt, Func<int, Maze>? createMaze)
    {
        _createMaze = createMaze ?? (l => MazeLevels.Create(l));
        Level = level;
        Maze = maze;
        Attempt = attempt;
        Phase = RunPhase.Playing;
    }

    public int Level { get; private set; }
    public RunPhase Phase { get; private set; }
    public Maze Maze { get; private set; } = null!;
    public MazeAttempt Attempt { get; private set; } = null!;

    public int Size => Maze.Cols;
    public bool IsLastLevel => Level == MazeLevels.Count;
    public bool IsOver => Phase == RunPhase.AllComplete;

    /// <summary>Retrying only means something once you've moved.</summary>
    public bool CanRetry => Phase == RunPhase.Playing && Attempt.HasMoved;

    public MoveResult Move(Direction d)
    {
        if (Phase != RunPhase.Playing) throw new InvalidOperationException($"Can't move while {Phase}.");

        var result = Attempt.Move(d);
        if (result == MoveResult.Finished) Phase = IsLastLevel ? RunPhase.AllComplete : RunPhase.LevelComplete;
        return result;
    }

    /// <summary>
    /// One swipe: see <see cref="MazeAttempt.Run"/>. Yields the result of each square moved; the run's
    /// phase changes the moment the finish is reached. Enumerate it fully (or stop partway) — nothing
    /// moves until it is enumerated.
    /// </summary>
    public IEnumerable<MoveResult> Run(Direction d)
    {
        if (Phase != RunPhase.Playing) throw new InvalidOperationException($"Can't move while {Phase}.");

        foreach (var result in Attempt.Run(d))
        {
            if (result == MoveResult.Finished) Phase = IsLastLevel ? RunPhase.AllComplete : RunPhase.LevelComplete;
            yield return result;
        }
    }

    /// <summary>Back to the start of the same maze. Unlimited. False (nothing happens) if there's
    /// nothing to start over.</summary>
    public bool Retry()
    {
        if (!CanRetry) return false;
        Attempt = new MazeAttempt(Maze);
        return true;
    }

    /// <summary>Puts the avatar back on an earlier square of its trail.</summary>
    public bool BackUpTo(int col, int row) => Phase == RunPhase.Playing && Attempt.BackUpTo(col, row);

    /// <summary>After <see cref="RunPhase.LevelComplete"/>: starts the next level with a new maze.</summary>
    public void Continue()
    {
        if (Phase != RunPhase.LevelComplete) throw new InvalidOperationException($"Nothing to continue from {Phase}.");
        BeginLevel(Level + 1);
    }

    private void BeginLevel(int level)
    {
        Level = level;
        Maze = _createMaze(level);
        Attempt = new MazeAttempt(Maze);
        Phase = RunPhase.Playing;
    }
}
