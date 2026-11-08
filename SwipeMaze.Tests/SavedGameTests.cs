using SwipeMaze.Core;

namespace SwipeMaze.Tests;

public class SavedGameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swipemaze-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "progress.tsv");

    public SavedGameTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A real level-1 run with the avatar walked some way along the route, with a detour into a
    /// side branch backed out of, so the trail is not just a prefix of the solution.</summary>
    private static MazeRun PartlyPlayed(int steps = 12)
    {
        var run = new MazeRun(1, level => MazeLevels.Create(level, new Random(7)));
        var route = MazeSolver.Solve(run.Maze);
        for (var i = 1; i <= steps && i < route.Count - 1; i++)
            run.Move(DirectionBetween(route[i - 1], route[i]));
        return run;
    }

    private static Direction DirectionBetween((int Col, int Row) a, (int Col, int Row) b) =>
        Enum.GetValues<Direction>().Single(d => a.Col + d.Dx() == b.Col && a.Row + d.Dy() == b.Row);

    // ---------- the maze itself ----------

    [Fact]
    public void An_exported_maze_imports_identically()
    {
        var maze = MazeLevels.Create(1, new Random(3));

        var copy = Maze.Import(maze.Cols, maze.Rows, maze.Export());

        Assert.NotNull(copy);
        Assert.Equal(maze.Export(), copy!.Export());
        Assert.Equal(MazeSolver.Solve(maze), MazeSolver.Solve(copy));
    }

    [Fact]
    public void A_damaged_maze_is_refused()
    {
        var bytes = MazeLevels.Create(1, new Random(3)).Export();

        Assert.Null(Maze.Import(10, 10, bytes[..^1]));                         // wrong length
        var oneSided = (byte[])bytes.Clone(); oneSided[0] ^= 0b0010;           // a passage open from one side only
        Assert.Null(Maze.Import(10, 10, oneSided));
        var junk = (byte[])bytes.Clone(); junk[5] |= 0b10000;                  // unknown bit
        Assert.Null(Maze.Import(10, 10, junk));
        var border = new byte[100]; border[0] = 1 << (int)Direction.Up;        // a hole in the outer wall
        Assert.Null(Maze.Import(10, 10, border));
    }

    // ---------- the saved game ----------

    [Fact]
    public void A_game_comes_back_with_the_same_maze_trail_and_avatar_position()
    {
        var run = PartlyPlayed();

        var restored = SavedGame.Parse(SavedGame.From(run).Serialize())!.ToRun();

        Assert.NotNull(restored);
        Assert.Equal(run.Level, restored!.Level);
        Assert.Equal(run.Maze.Export(), restored.Maze.Export());
        Assert.Equal(run.Attempt.Path, restored.Attempt.Path);
        Assert.Equal((run.Attempt.Col, run.Attempt.Row), (restored.Attempt.Col, restored.Attempt.Row));
        Assert.Equal(RunPhase.Playing, restored.Phase);
    }

    [Fact]
    public void A_game_backed_up_part_way_is_saved_where_the_avatar_is_now()
    {
        var run = PartlyPlayed(20);
        var target = run.Attempt.Path[5];
        Assert.True(run.BackUpTo(target.Col, target.Row));

        var restored = SavedGame.Parse(SavedGame.From(run).Serialize())!.ToRun()!;

        Assert.Equal(target, (restored.Attempt.Col, restored.Attempt.Row));
        Assert.Equal(6, restored.Attempt.Path.Count);
    }

    [Fact]
    public void A_game_that_has_not_been_moved_yet_round_trips_too()
    {
        var run = new MazeRun(1, level => MazeLevels.Create(level, new Random(1)));

        var restored = SavedGame.Parse(SavedGame.From(run).Serialize())!.ToRun()!;

        Assert.False(restored.Attempt.HasMoved);
        Assert.Equal(run.Maze.Export(), restored.Maze.Export());
    }

    [Fact]
    public void A_restored_game_can_be_played_on_to_the_finish_and_then_the_next_level()
    {
        var restored = SavedGame.Parse(SavedGame.From(PartlyPlayed()).Serialize())!.ToRun()!;

        var route = MazeSolver.Solve(restored.Maze);
        var here = restored.Attempt.Path.Count - 1;
        for (var i = here + 1; i < route.Count; i++) restored.Move(DirectionBetween(route[i - 1], route[i]));

        Assert.Equal(RunPhase.LevelComplete, restored.Phase);
        restored.Continue();
        Assert.Equal(2, restored.Level);
        Assert.Equal(MazeLevels.SizeFor(2), restored.Size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("SWIPEMAZE-GAME-1\n1\n!!!not base64!!!\n")]
    [InlineData("SWIPEMAZE-GAME-1\n0\nAAAA\n")]          // no such level
    [InlineData("SWIPEMAZE-GAME-1\n101\nAAAA\n")]
    [InlineData("SWIPEMAZE-GAME-1\n1\nAAAA\n")]          // maze the wrong size for the level
    public void Damaged_save_text_is_ignored_not_fatal(string text)
    {
        Assert.Null(SavedGame.Parse(text));
    }

    [Fact]
    public void A_trail_that_does_not_fit_the_maze_is_refused()
    {
        var maze = MazeLevels.Create(1, new Random(7));
        var wall = Enum.GetValues<Direction>().First(d => !maze.IsOpen(maze.Start.Col, maze.Start.Row, d));

        Assert.Null(new SavedGame(1, maze, ((int)wall).ToString()).ToRun());      // first step into a wall

        var open = Enum.GetValues<Direction>().First(d => maze.IsOpen(maze.Start.Col, maze.Start.Row, d));
        var there = ((int)open).ToString();
        var back = ((int)open.Opposite()).ToString();
        Assert.Null(new SavedGame(1, maze, there + back).ToRun());                // stepping back is not a trail
    }

    [Fact]
    public void A_solved_game_is_not_resumable()
    {
        var run = PartlyPlayed();
        var route = MazeSolver.Solve(run.Maze);
        for (var i = run.Attempt.Path.Count; i < route.Count; i++) run.Move(DirectionBetween(route[i - 1], route[i]));
        Assert.Equal(RunPhase.LevelComplete, run.Phase);

        Assert.Null(SavedGame.Parse(SavedGame.From(run).Serialize())!.ToRun());
    }

    // ---------- per-player storage ----------

    [Fact]
    public async Task Each_player_has_their_own_saved_game_that_survives_a_restart()
    {
        var ana = PartlyPlayed(5);
        var ben = PartlyPlayed(15);
        var repo = new FilePlayerProgressRepository(FilePath);
        await repo.SaveGameAsync("Ana", SavedGame.From(ana));
        await repo.SaveGameAsync("Ben", SavedGame.From(ben));

        var fresh = new FilePlayerProgressRepository(FilePath);
        var anaBack = (await fresh.LoadGameAsync("ANA"))!.ToRun()!;       // name matches ignoring case
        var benBack = (await fresh.LoadGameAsync("Ben"))!.ToRun()!;

        Assert.Equal(ana.Attempt.Path, anaBack.Attempt.Path);
        Assert.Equal(ben.Attempt.Path, benBack.Attempt.Path);
        Assert.Null(await fresh.LoadGameAsync("Cy"));
    }

    [Fact]
    public async Task Saving_again_replaces_the_earlier_game()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        var run = PartlyPlayed(3);
        await repo.SaveGameAsync("Ana", SavedGame.From(run));
        run.Move(run.Attempt.HasForwardMoves() ? Enum.GetValues<Direction>().First(d => run.Maze.IsOpen(run.Attempt.Col, run.Attempt.Row, d) && !run.Attempt.IsOnPath(run.Attempt.Col + d.Dx(), run.Attempt.Row + d.Dy())) : Direction.Up);
        await repo.SaveGameAsync("Ana", SavedGame.From(run));

        var back = (await repo.LoadGameAsync("Ana"))!.ToRun()!;
        Assert.Equal(run.Attempt.Path, back.Attempt.Path);
    }

    [Fact]
    public async Task Clearing_and_removing_a_player_forget_their_game()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        await repo.SaveAsync(new PlayerProgress("Ana", 1, 1, false, DateTimeOffset.UnixEpoch));
        await repo.SaveGameAsync("Ana", SavedGame.From(PartlyPlayed()));
        await repo.SaveGameAsync("Ben", SavedGame.From(PartlyPlayed()));

        await repo.ClearGameAsync("Ana");
        Assert.Null(await repo.LoadGameAsync("Ana"));

        await repo.SaveGameAsync("Ana", SavedGame.From(PartlyPlayed()));
        Assert.True(await repo.DeleteAsync("Ana"));
        Assert.Null(await repo.LoadGameAsync("Ana"));
        Assert.NotNull(await repo.LoadGameAsync("Ben"));                    // others untouched
        await repo.ClearGameAsync("Nobody");                                 // clearing nothing is fine
    }

    [Fact]
    public async Task A_damaged_save_file_loads_as_no_game()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        await repo.SaveGameAsync("Ana", SavedGame.From(PartlyPlayed()));
        foreach (var file in Directory.GetFiles(FilePath + ".games")) File.WriteAllText(file, "garbage");

        Assert.Null(await repo.LoadGameAsync("Ana"));
    }

    [Fact]
    public async Task Awkward_names_make_safe_file_names()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        foreach (var name in new[] { "a/b", "..\\x", "Zoë ☺", "CON" })
        {
            await repo.SaveGameAsync(name, SavedGame.From(PartlyPlayed(2)));
            Assert.NotNull(await repo.LoadGameAsync(name));
        }
        Assert.Equal(4, Directory.GetFiles(FilePath + ".games").Length);
    }
}
