using SwipeMaze.Core;

namespace SwipeMaze.Tests;

public class MazeTests
{
    private static readonly Direction[] AllDirections = Enum.GetValues<Direction>();

    [Fact]
    public void Carving_opens_the_passage_from_both_sides()
    {
        var maze = new Maze(3, 3);
        maze.Carve(1, 1, Direction.Right);

        Assert.True(maze.IsOpen(1, 1, Direction.Right));
        Assert.True(maze.IsOpen(2, 1, Direction.Left));
        Assert.False(maze.IsOpen(1, 1, Direction.Up));
        Assert.Equal(1, maze.PassageCount);
    }

    [Fact]
    public void The_outer_border_cannot_be_carved_through()
    {
        var maze = new Maze(3, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => maze.Carve(0, 0, Direction.Left));
        Assert.Throws<ArgumentOutOfRangeException>(() => maze.Carve(2, 2, Direction.Down));
    }

    [Fact]
    public void Start_is_the_top_middle_and_finish_the_bottom_middle()
    {
        var even = new Maze(10, 10);
        Assert.Equal((5, 0), even.Start);
        Assert.Equal((5, 9), even.Finish);

        var odd = new Maze(11, 11);
        Assert.Equal((5, 0), odd.Start);
        Assert.Equal((5, 10), odd.Finish);
    }

    [Fact]
    public void Levels_grow_one_square_per_side_from_10_to_109()
    {
        Assert.Equal(100, MazeLevels.Count);
        Assert.Equal(10, MazeLevels.SizeFor(1));
        Assert.Equal(11, MazeLevels.SizeFor(2));
        Assert.Equal(50, MazeLevels.SizeFor(41));
        Assert.Equal(100, MazeLevels.SizeFor(91));
        Assert.Equal(109, MazeLevels.SizeFor(100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public void A_level_that_does_not_exist_throws(int level) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MazeLevels.SizeFor(level));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(13)]
    [InlineData(41)]
    [InlineData(91)]
    [InlineData(100)]
    public void A_level_gives_a_square_maze_of_its_size(int level)
    {
        var maze = MazeLevels.Create(level, new Random(level));
        Assert.Equal(MazeLevels.SizeFor(level), maze.Cols);
        Assert.Equal(MazeLevels.SizeFor(level), maze.Rows);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(109)]
    public void Random_mazes_are_perfect_and_solvable(int size)
    {
        for (var seed = 0; seed < 5; seed++)
        {
            var maze = MazeGenerator.GenerateWinding(size, new Random(seed));

            // A tree: exactly cells-1 passages and every cell reachable from the start — one single
            // route between any two squares, no loops, no walled-off areas.
            Assert.Equal(size * size - 1, maze.PassageCount);
            Assert.Equal(size * size, CountReachable(maze));

            var route = MazeSolver.Solve(maze);
            Assert.NotEmpty(route);
            Assert.Equal(maze.Start, route[0]);
            Assert.Equal(maze.Finish, route[^1]);
        }
    }

    [Fact]
    public void The_outer_border_stays_solid()
    {
        var maze = MazeLevels.Create(20, new Random(3));
        for (var c = 0; c < maze.Cols; c++)
        {
            Assert.False(maze.IsOpen(c, 0, Direction.Up));
            Assert.False(maze.IsOpen(c, maze.Rows - 1, Direction.Down));
        }
        for (var r = 0; r < maze.Rows; r++)
        {
            Assert.False(maze.IsOpen(0, r, Direction.Left));
            Assert.False(maze.IsOpen(maze.Cols - 1, r, Direction.Right));
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(20)]
    [InlineData(35)]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(109)]
    public void The_route_to_the_finish_winds_instead_of_going_straight_there(int size)
    {
        // The direct distance from start to finish is size-1 steps. Every maze must have a route at
        // least three times that, with no long straight stretches along it.
        var rng = new Random(size);
        for (var i = 0; i < 20; i++)
        {
            var route = MazeSolver.Measure(MazeGenerator.GenerateWinding(size, rng));

            Assert.True(route.Length >= 3 * (size - 1), $"size {size}: route of only {route.Length} steps");
            Assert.True(route.LongestStraight <= MazeGenerator.MaxStraightRun(size),
                $"size {size}: {route.LongestStraight} squares in a straight line");
            Assert.True(route.Turns >= 5, $"size {size}: only {route.Turns} turns");
        }
    }

    [Fact]
    public void Every_maze_is_different()
    {
        var a = MazeLevels.Create(5);
        var b = MazeLevels.Create(5);

        Assert.False(Same(a, b));
    }

    [Fact]
    public void The_same_random_sequence_gives_the_same_maze()
    {
        Assert.True(Same(MazeGenerator.GenerateWinding(12, new Random(7)), MazeGenerator.GenerateWinding(12, new Random(7))));
    }

    [Fact]
    public void Measuring_a_straight_corridor()
    {
        var maze = new Maze(3, 3);
        maze.Carve(1, 0, Direction.Down);
        maze.Carve(1, 1, Direction.Down);

        Assert.Equal(new RouteStats(Length: 2, LongestStraight: 2, Turns: 0), MazeSolver.Measure(maze));
    }

    [Fact]
    public void Measuring_a_bendy_route()
    {
        // (1,0) down to (1,1), left to (0,1), down to (0,2), right to (1,2).
        var maze = new Maze(3, 3);
        maze.Carve(1, 0, Direction.Down);
        maze.Carve(1, 1, Direction.Left);
        maze.Carve(0, 1, Direction.Down);
        maze.Carve(0, 2, Direction.Right);

        Assert.Equal(new RouteStats(Length: 4, LongestStraight: 1, Turns: 3), MazeSolver.Measure(maze));
    }

    [Fact]
    public void A_maze_with_no_way_through_has_no_route()
    {
        var maze = new Maze(3, 3);
        maze.Carve(1, 0, Direction.Down);

        Assert.Empty(MazeSolver.Solve(maze));
        Assert.Equal(new RouteStats(0, 0, 0), MazeSolver.Measure(maze));
    }

    [Fact]
    public void The_winding_limits_scale_with_the_size()
    {
        Assert.Equal(27, MazeGenerator.MinRouteLength(10));   // 3 x the 9-step direct distance
        Assert.Equal(600, MazeGenerator.MinRouteLength(100)); // 6% of the squares
        Assert.Equal(5, MazeGenerator.MaxStraightRun(10));
        Assert.Equal(20, MazeGenerator.MaxStraightRun(100));
    }

    private static int CountReachable(Maze maze)
    {
        var seen = new bool[maze.Cols * maze.Rows];
        var stack = new Stack<(int Col, int Row)>();
        stack.Push(maze.Start);
        seen[maze.Start.Row * maze.Cols + maze.Start.Col] = true;
        var count = 0;
        while (stack.Count > 0)
        {
            var (c, r) = stack.Pop();
            count++;
            foreach (var d in AllDirections)
            {
                if (!maze.IsOpen(c, r, d)) continue;
                var (nc, nr) = (c + d.Dx(), r + d.Dy());
                if (seen[nr * maze.Cols + nc]) continue;
                seen[nr * maze.Cols + nc] = true;
                stack.Push((nc, nr));
            }
        }
        return count;
    }

    private static bool Same(Maze a, Maze b)
    {
        if (a.Cols != b.Cols || a.Rows != b.Rows) return false;
        for (var r = 0; r < a.Rows; r++)
            for (var c = 0; c < a.Cols; c++)
                foreach (var d in AllDirections)
                    if (a.IsOpen(c, r, d) != b.IsOpen(c, r, d)) return false;
        return true;
    }
}

public class SwipeDetectorTests
{
    [Theory]
    [InlineData(60, 5, Direction.Right)]
    [InlineData(-60, 5, Direction.Left)]
    [InlineData(5, 60, Direction.Down)]
    [InlineData(5, -60, Direction.Up)]
    [InlineData(-50, -40, Direction.Left)]   // diagonal: the longer axis wins
    [InlineData(40, 50, Direction.Down)]
    public void The_longer_axis_decides_the_direction(double dx, double dy, Direction expected) =>
        Assert.Equal(expected, SwipeDetector.Detect(dx, dy, minDistance: 20));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, -12)]
    [InlineData(-19.9, 3)]
    public void A_short_movement_is_a_tap_not_a_swipe(double dx, double dy) =>
        Assert.Null(SwipeDetector.Detect(dx, dy, minDistance: 20));
}
