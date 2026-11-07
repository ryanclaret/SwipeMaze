namespace SwipeMaze.Core;

/// <summary>
/// One maze per level, and a new random one every time a level is played. Level 1 is 10 x 10; each
/// level after that adds one square to each side, all the way up to level 100 at 109 x 109.
/// </summary>
public static class MazeLevels
{
    public const int FirstSize = 10;
    public const int LastSize = 109;

    /// <summary>100 levels: 10x10, 11x11, ... 109x109.</summary>
    public const int Count = LastSize - FirstSize + 1;

    /// <param name="level">1-based.</param>
    public static int SizeFor(int level)
    {
        if (level < 1 || level > Count) throw new ArgumentOutOfRangeException(nameof(level), level, $"Levels are 1..{Count}.");
        return FirstSize + level - 1;
    }

    /// <summary>A fresh random winding maze for the level.</summary>
    public static Maze Create(int level, Random? random = null) =>
        MazeGenerator.GenerateWinding(SizeFor(level), random ?? Random.Shared);
}
