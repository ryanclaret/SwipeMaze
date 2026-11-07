using SwipeMaze.Core;

namespace SwipeMaze.Tests;

public class MazeAttemptTests
{
    /// <summary>
    /// 3 wide, 3 tall. Start (1,0), finish (1,2).
    ///   (1,0) → (1,1) → (1,2)      the way through
    ///   (1,1) → (0,1)              a dead-end side branch
    ///   (1,1) → (2,1) → (2,0)      a longer dead-end side branch
    /// </summary>
    private static Maze BranchingMaze()
    {
        var maze = new Maze(3, 3);
        maze.Carve(1, 0, Direction.Down);
        maze.Carve(1, 1, Direction.Down);
        maze.Carve(1, 1, Direction.Left);
        maze.Carve(1, 1, Direction.Right);
        maze.Carve(2, 1, Direction.Up);
        return maze;
    }

    [Fact]
    public void A_swipe_into_a_wall_does_nothing()
    {
        var attempt = new MazeAttempt(BranchingMaze());

        Assert.Equal(MoveResult.Wall, attempt.Move(Direction.Left));
        Assert.Equal(MoveResult.Wall, attempt.Move(Direction.Up)); // the start opening is not a way out
        Assert.Equal((1, 0), (attempt.Col, attempt.Row));
        Assert.False(attempt.HasMoved);
    }

    [Fact]
    public void Each_move_is_exactly_one_square()
    {
        var attempt = new MazeAttempt(BranchingMaze());

        Assert.Equal(MoveResult.Moved, attempt.Move(Direction.Down));
        Assert.Equal((1, 1), (attempt.Col, attempt.Row));
        Assert.Equal(new[] { (1, 0), (1, 1) }, attempt.Path);
    }

    [Fact]
    public void Swiping_back_steps_onto_the_previous_square_and_rubs_out_the_trail_behind()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);
        attempt.Move(Direction.Right);

        Assert.Equal(MoveResult.BackedUp, attempt.Move(Direction.Left));

        Assert.Equal((1, 1), (attempt.Col, attempt.Row));
        Assert.Equal(new[] { (1, 0), (1, 1) }, attempt.Path);
        Assert.False(attempt.IsOnPath(2, 1)); // no longer on the trail...
        Assert.Equal(MoveResult.Moved, attempt.Move(Direction.Right)); // ...so it can be entered again
    }

    [Fact]
    public void You_can_back_all_the_way_to_the_start_and_try_another_way()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);
        attempt.Move(Direction.Left); // dead end
        attempt.Move(Direction.Right);
        attempt.Move(Direction.Up);

        Assert.Equal((1, 0), (attempt.Col, attempt.Row));
        Assert.False(attempt.HasMoved);
        Assert.Equal(new[] { (1, 0) }, attempt.Path);
    }

    [Fact]
    public void A_square_already_on_the_trail_that_is_not_the_previous_one_cannot_be_walked_onto()
    {
        // A loop: start → right → down → left → up would land back on the start square.
        var maze = new Maze(3, 3);
        maze.Carve(1, 0, Direction.Right);
        maze.Carve(2, 0, Direction.Down);
        maze.Carve(2, 1, Direction.Left);
        maze.Carve(1, 1, Direction.Up);
        var attempt = new MazeAttempt(maze);
        attempt.Move(Direction.Right);
        attempt.Move(Direction.Down);
        attempt.Move(Direction.Left);

        Assert.Equal(MoveResult.Wall, attempt.Move(Direction.Up));
        Assert.Equal((1, 1), (attempt.Col, attempt.Row));
    }

    [Fact]
    public void A_square_with_no_way_forward_is_a_dead_end_but_you_can_still_back_out()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);

        Assert.False(attempt.IsDeadEnd);
        Assert.Equal(MoveResult.Moved, attempt.Move(Direction.Left));
        Assert.True(attempt.IsDeadEnd);
        Assert.False(attempt.IsFinished);

        Assert.Equal(MoveResult.BackedUp, attempt.Move(Direction.Right));
        Assert.False(attempt.IsDeadEnd);
    }

    [Fact]
    public void Reaching_the_finish_square_solves_it()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);

        Assert.Equal(MoveResult.Finished, attempt.Move(Direction.Down));
        Assert.True(attempt.IsFinished);
        Assert.Equal(new[] { (1, 0), (1, 1), (1, 2) }, attempt.Path);
    }

    [Fact]
    public void Moving_after_the_maze_is_solved_is_a_bug_not_a_silent_no_op()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);
        attempt.Move(Direction.Down);

        Assert.Throws<InvalidOperationException>(() => attempt.Move(Direction.Up));
    }

    [Fact]
    public void Backing_up_to_an_earlier_square_rubs_out_the_trail_after_it()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);
        attempt.Move(Direction.Right);
        attempt.Move(Direction.Up); // at (2,0): trail is (1,0) (1,1) (2,1) (2,0)

        Assert.True(attempt.BackUpTo(1, 1));

        Assert.Equal((1, 1), (attempt.Col, attempt.Row));
        Assert.Equal(new[] { (1, 0), (1, 1) }, attempt.Path);
        Assert.False(attempt.IsOnPath(2, 1));
        Assert.False(attempt.IsOnPath(2, 0));
        Assert.Equal(MoveResult.Finished, attempt.Move(Direction.Down)); // and the way on is open again
    }

    [Fact]
    public void Backing_up_can_go_as_far_as_the_start_square()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);
        attempt.Move(Direction.Left);

        Assert.True(attempt.BackUpTo(1, 0));
        Assert.Equal(new[] { (1, 0) }, attempt.Path);
    }

    [Fact]
    public void Backing_up_to_a_square_that_is_not_on_the_trail_or_is_the_current_one_does_nothing()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);

        Assert.False(attempt.BackUpTo(0, 1)); // a real square, but not walked
        Assert.False(attempt.BackUpTo(1, 1)); // where the avatar already is
        Assert.False(attempt.BackUpTo(7, 7)); // not even in the maze
        Assert.Equal(new[] { (1, 0), (1, 1) }, attempt.Path);
    }

    [Fact]
    public void A_tap_means_the_nearest_trail_square()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);
        attempt.Move(Direction.Right);
        attempt.Move(Direction.Up); // trail: (1,0) (1,1) (2,1), avatar on (2,0)

        Assert.True(attempt.TryFindBackUpTarget(1.1, 1.2, maxDistance: 0.6, out var target));
        Assert.Equal((1, 1), target);

        Assert.True(attempt.TryFindBackUpTarget(1.0, 0.1, maxDistance: 0.6, out target));
        Assert.Equal((1, 0), target);
    }

    [Fact]
    public void Tapping_the_avatar_or_nearer_to_it_than_to_any_other_square_means_nothing()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);
        attempt.Move(Direction.Right);

        Assert.False(attempt.TryFindBackUpTarget(2.0, 1.0, maxDistance: 5, out _));   // on the avatar
        Assert.False(attempt.TryFindBackUpTarget(1.8, 1.0, maxDistance: 5, out _));   // beside it, still nearest to it
    }

    [Fact]
    public void Tapping_away_from_the_trail_means_nothing()
    {
        var attempt = new MazeAttempt(BranchingMaze());
        attempt.Move(Direction.Down);
        attempt.Move(Direction.Right);

        Assert.False(attempt.TryFindBackUpTarget(0.0, 2.0, maxDistance: 0.6, out _));
    }
}

public class MazeRunTests
{
    /// <summary>Down, then either left (dead end) or down (finish).</summary>
    private static Maze TinyMaze()
    {
        var maze = new Maze(3, 3);
        maze.Carve(1, 0, Direction.Down);
        maze.Carve(1, 1, Direction.Down);
        maze.Carve(1, 1, Direction.Left);
        return maze;
    }

    private static MazeRun TinyRun(int startLevel = 1, List<int>? created = null) =>
        new(startLevel, level =>
        {
            created?.Add(level);
            return TinyMaze();
        });

    private static void Solve(MazeRun run)
    {
        run.Move(Direction.Down);
        Assert.Equal(MoveResult.Finished, run.Move(Direction.Down));
    }

    [Fact]
    public void A_new_run_starts_on_level_1_which_is_10_by_10()
    {
        var run = new MazeRun();

        Assert.Equal(1, run.Level);
        Assert.Equal(10, run.Size);
        Assert.Equal(RunPhase.Playing, run.Phase);
    }

    [Fact]
    public void A_run_can_pick_up_from_a_later_level()
    {
        var run = new MazeRun(startLevel: 7);

        Assert.Equal(7, run.Level);
        Assert.Equal(16, run.Size);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Starting_on_a_level_that_does_not_exist_throws(int level) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MazeRun(level));

    [Fact]
    public void Solving_a_level_leads_on_to_the_next_with_a_new_maze()
    {
        var created = new List<int>();
        var run = TinyRun(created: created);
        var first = run.Maze;

        Solve(run);
        Assert.Equal(RunPhase.LevelComplete, run.Phase);

        run.Continue();

        Assert.Equal(2, run.Level);
        Assert.Equal(RunPhase.Playing, run.Phase);
        Assert.NotSame(first, run.Maze);
        Assert.False(run.Attempt.HasMoved);
        Assert.Equal(new[] { 1, 2 }, created);
    }

    [Fact]
    public void Each_level_is_one_square_bigger_each_way()
    {
        var run = new MazeRun();
        var sizes = new List<int> { run.Size };

        for (var i = 0; i < 3; i++)
        {
            var route = MazeSolver.Solve(run.Maze);
            for (var step = 1; step < route.Count; step++)
            {
                var d = Enum.GetValues<Direction>().Single(d =>
                    route[step - 1].Col + d.Dx() == route[step].Col && route[step - 1].Row + d.Dy() == route[step].Row);
                run.Move(d);
            }
            run.Continue();
            sizes.Add(run.Size);
        }

        Assert.Equal(new[] { 10, 11, 12, 13 }, sizes);
    }

    [Fact]
    public void A_dead_end_ends_nothing_you_just_back_out_and_carry_on()
    {
        var run = TinyRun();
        run.Move(Direction.Down);
        run.Move(Direction.Left);

        Assert.True(run.Attempt.IsDeadEnd);
        Assert.Equal(RunPhase.Playing, run.Phase);

        Assert.Equal(MoveResult.BackedUp, run.Move(Direction.Right));
        Assert.Equal(MoveResult.Finished, run.Move(Direction.Down));
        Assert.Equal(RunPhase.LevelComplete, run.Phase);
    }

    [Fact]
    public void Retrying_goes_back_to_the_start_of_the_same_maze_as_often_as_you_like()
    {
        var run = TinyRun();
        var maze = run.Maze;

        for (var i = 0; i < 200; i++)
        {
            run.Move(Direction.Down);
            Assert.True(run.CanRetry);
            Assert.True(run.Retry());

            Assert.False(run.Attempt.HasMoved);
            Assert.Same(maze, run.Maze);
            Assert.Equal(RunPhase.Playing, run.Phase);
            Assert.Equal(1, run.Level);
        }
    }

    [Fact]
    public void Retrying_before_moving_is_not_offered()
    {
        var run = TinyRun();

        Assert.False(run.CanRetry);
        Assert.False(run.Retry());
    }

    [Fact]
    public void Backing_up_to_an_earlier_square_works_during_play_only()
    {
        var run = TinyRun();
        run.Move(Direction.Down);
        run.Move(Direction.Left);

        Assert.True(run.BackUpTo(1, 0));
        Assert.Equal(new[] { (1, 0) }, run.Attempt.Path);

        Solve(run);
        Assert.False(run.BackUpTo(1, 0)); // level complete: the trail is frozen
    }

    [Fact]
    public void Nothing_can_be_moved_while_the_level_complete_message_is_up()
    {
        var run = TinyRun();
        Solve(run);

        Assert.Throws<InvalidOperationException>(() => run.Move(Direction.Up));
        Assert.False(run.CanRetry);
    }

    [Fact]
    public void Solving_the_last_level_finishes_the_game()
    {
        var run = TinyRun(startLevel: MazeLevels.Count);
        Assert.True(run.IsLastLevel);

        Solve(run);

        Assert.Equal(RunPhase.AllComplete, run.Phase);
        Assert.True(run.IsOver);
        Assert.Throws<InvalidOperationException>(() => run.Continue());
    }

    [Fact]
    public void Continuing_when_there_is_nothing_to_continue_from_is_an_error()
    {
        Assert.Throws<InvalidOperationException>(() => TinyRun().Continue());
    }

    [Fact]
    public void Every_level_can_be_won_with_one_swipe_per_square()
    {
        var run = new MazeRun();

        while (!run.IsOver)
        {
            var route = MazeSolver.Solve(run.Maze);
            for (var i = 1; i < route.Count; i++)
            {
                var d = Enum.GetValues<Direction>().Single(d =>
                    route[i - 1].Col + d.Dx() == route[i].Col && route[i - 1].Row + d.Dy() == route[i].Row);
                var result = run.Move(d);
                Assert.Equal(i == route.Count - 1 ? MoveResult.Finished : MoveResult.Moved, result);
            }

            if (run.Phase == RunPhase.LevelComplete) run.Continue();
        }

        Assert.Equal(MazeLevels.Count, run.Level);
        Assert.Equal(109, run.Size);
    }
}
