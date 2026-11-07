using SwipeMaze.Core;

namespace SwipeMaze.Tests;

public class RunToForkTests
{
    /// <summary>
    /// 5 wide, 5 tall. Start (2,0), finish (2,4).
    ///
    ///   (2,0) ↓ (2,1) ↓ (2,2)          a straight corridor to a three-way fork at (2,2)
    ///   (2,2) ← (1,2)                  left: a one-square dead end
    ///   (2,2) → (3,2) ↓ (3,3) → (4,3) ↓ (4,4)    right: a corridor with two bends, ending in a dead end
    ///   (2,2) ↓ (2,3) ↓ (2,4)          down: the way to the finish
    /// </summary>
    private static Maze ForkMaze()
    {
        var maze = new Maze(5, 5);
        maze.Carve(2, 0, Direction.Down);
        maze.Carve(2, 1, Direction.Down);
        maze.Carve(2, 2, Direction.Left);
        maze.Carve(2, 2, Direction.Right);
        maze.Carve(3, 2, Direction.Down);
        maze.Carve(3, 3, Direction.Right);
        maze.Carve(4, 3, Direction.Down);
        maze.Carve(2, 2, Direction.Down);
        maze.Carve(2, 3, Direction.Down);
        return maze;
    }

    private static MazeAttempt AtFork()
    {
        var attempt = new MazeAttempt(ForkMaze());
        attempt.Run(Direction.Down).ToList();
        return attempt;
    }

    [Fact]
    public void A_swipe_into_a_wall_is_just_a_wall()
    {
        var attempt = new MazeAttempt(ForkMaze());

        Assert.Equal(new[] { MoveResult.Wall }, attempt.Run(Direction.Left).ToList());
        Assert.Equal((2, 0), (attempt.Col, attempt.Row));
    }

    [Fact]
    public void A_swipe_runs_along_a_corridor_and_stops_at_the_fork()
    {
        var attempt = new MazeAttempt(ForkMaze());

        var results = attempt.Run(Direction.Down).ToList();

        Assert.Equal(new[] { MoveResult.Moved, MoveResult.Moved }, results);
        Assert.Equal((2, 2), (attempt.Col, attempt.Row));
        Assert.Equal(new[] { (2, 0), (2, 1), (2, 2) }, attempt.Path);
        Assert.False(attempt.IsDeadEnd);
    }

    [Fact]
    public void A_run_follows_the_corridor_round_its_bends_to_the_dead_end()
    {
        var attempt = AtFork();

        var results = attempt.Run(Direction.Right).ToList();

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(MoveResult.Moved, r));
        Assert.Equal((4, 4), (attempt.Col, attempt.Row));
        Assert.True(attempt.IsDeadEnd);
    }

    [Fact]
    public void A_run_stops_at_the_finish()
    {
        var attempt = AtFork();

        var results = attempt.Run(Direction.Down).ToList();

        Assert.Equal(new[] { MoveResult.Moved, MoveResult.Finished }, results);
        Assert.True(attempt.IsFinished);
    }

    [Fact]
    public void A_one_square_branch_is_a_run_of_one()
    {
        var attempt = AtFork();

        Assert.Equal(new[] { MoveResult.Moved }, attempt.Run(Direction.Left).ToList());
        Assert.True(attempt.IsDeadEnd);
    }

    [Fact]
    public void Swiping_back_runs_back_to_the_fork_and_stops_there()
    {
        var attempt = AtFork();
        attempt.Run(Direction.Right).ToList(); // ...to the dead end at (4,4)

        var results = attempt.Run(Direction.Up).ToList();

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(MoveResult.BackedUp, r));
        Assert.Equal((2, 2), (attempt.Col, attempt.Row));
        Assert.Equal(new[] { (2, 0), (2, 1), (2, 2) }, attempt.Path);
    }

    [Fact]
    public void Backing_out_of_a_one_square_branch_stops_at_once_because_the_fork_is_right_there()
    {
        var attempt = AtFork();
        attempt.Run(Direction.Left).ToList();

        var results = attempt.Run(Direction.Right).ToList();

        Assert.Equal(new[] { MoveResult.BackedUp }, results);
        Assert.Equal((2, 2), (attempt.Col, attempt.Row));
    }

    [Fact]
    public void Swiping_back_with_no_fork_on_the_way_runs_all_the_way_to_the_start()
    {
        var attempt = AtFork();

        var results = attempt.Run(Direction.Up).ToList();

        Assert.Equal(new[] { MoveResult.BackedUp, MoveResult.BackedUp }, results);
        Assert.Equal((2, 0), (attempt.Col, attempt.Row));
        Assert.False(attempt.HasMoved);
    }

    [Fact]
    public void Backing_up_stops_where_a_branch_you_walked_away_from_is_open_again()
    {
        // The branch walked and rubbed out counts as a way to go: it's a real choice to make again.
        var attempt = AtFork();
        attempt.Run(Direction.Right).ToList();
        attempt.Run(Direction.Up).ToList();   // back to the fork
        attempt.Run(Direction.Up).ToList();   // and on back to the start

        Assert.Equal((2, 0), (attempt.Col, attempt.Row));
        Assert.Equal(new[] { (2, 0) }, attempt.Path);
    }

    [Fact]
    public void A_run_does_not_go_round_a_loop_forever()
    {
        var maze = new Maze(3, 3);
        maze.Carve(1, 0, Direction.Right);
        maze.Carve(2, 0, Direction.Down);
        maze.Carve(2, 1, Direction.Left);
        maze.Carve(1, 1, Direction.Up); // back to the start square: a loop
        var attempt = new MazeAttempt(maze);

        var results = attempt.Run(Direction.Right).ToList();

        Assert.Equal(3, results.Count);
        Assert.Equal((1, 1), (attempt.Col, attempt.Row));
        Assert.True(attempt.IsDeadEnd); // the only way on is the square it started from
    }

    [Fact]
    public void Nothing_moves_until_the_run_is_enumerated_and_it_can_be_stopped_part_way()
    {
        var attempt = new MazeAttempt(ForkMaze());

        var run = attempt.Run(Direction.Down);
        Assert.Equal((2, 0), (attempt.Col, attempt.Row));

        run.First();
        Assert.Equal((2, 1), (attempt.Col, attempt.Row)); // one square, though the run would go on to (2,2)
    }

    // ---------- through MazeRun ----------

    private static MazeRun ForkRun() => new(1, _ => ForkMaze());

    [Fact]
    public void Reaching_the_finish_during_a_run_completes_the_level()
    {
        var run = ForkRun();
        run.Run(Direction.Down).ToList();

        var results = run.Run(Direction.Down).ToList();

        Assert.Equal(MoveResult.Finished, results[^1]);
        Assert.Equal(RunPhase.LevelComplete, run.Phase);
    }

    [Fact]
    public void The_phase_changes_the_moment_the_finish_is_reached()
    {
        var run = ForkRun();
        run.Run(Direction.Down).ToList();

        foreach (var result in run.Run(Direction.Down))
            Assert.Equal(result == MoveResult.Finished ? RunPhase.LevelComplete : RunPhase.Playing, run.Phase);
    }

    [Fact]
    public void Nothing_can_run_while_the_level_complete_message_is_up()
    {
        var run = ForkRun();
        run.Run(Direction.Down).ToList();
        run.Run(Direction.Down).ToList();

        Assert.Throws<InvalidOperationException>(() => run.Run(Direction.Up).ToList());
    }

    [Fact]
    public void Every_level_can_be_won_with_one_swipe_per_choice()
    {
        // At each fork swipe the way the solution goes; the run does the corridor in between. Every one of
        // the 100 random mazes must be finished this way, and in far fewer swipes than squares.
        var run = new MazeRun();
        var swipes = 0;
        var squares = 0;

        while (!run.IsOver)
        {
            var route = MazeSolver.Solve(run.Maze);
            while (run.Phase == RunPhase.Playing)
            {
                var here = run.Attempt.Path.Count - 1;
                var d = Enum.GetValues<Direction>().Single(d =>
                    route[here].Col + d.Dx() == route[here + 1].Col && route[here].Row + d.Dy() == route[here + 1].Row);

                var results = run.Run(d).ToList();
                swipes++;
                squares += results.Count;
                Assert.All(results, r => Assert.True(r is MoveResult.Moved or MoveResult.Finished));
            }

            if (run.Phase == RunPhase.LevelComplete) run.Continue();
        }

        Assert.Equal(MazeLevels.Count, run.Level);
        Assert.True(swipes * 3 < squares, $"{swipes} swipes for {squares} squares: runs should save a lot of swiping");
    }
}
