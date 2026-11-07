namespace SwipeMaze.Core;

/// <summary>How twisty the route from start to finish is.</summary>
/// <param name="Length">Squares to cross from start to finish (steps, not cells).</param>
/// <param name="LongestStraight">The longest run of steps in one direction along the route.</param>
/// <param name="Turns">How many times the route changes direction.</param>
public readonly record struct RouteStats(int Length, int LongestStraight, int Turns);

public static class MazeGenerator
{
    /// <summary>
    /// Chance of carrying on from the newest cell rather than a random earlier one. High, so the maze
    /// is made of long winding corridors with a long, bendy route rather than lots of short stubs.
    /// </summary>
    private const double WindingBias = 0.92;

    /// <summary>How many random mazes to try before settling for the most winding one seen.</summary>
    private const int MaxAttempts = 40;

    /// <summary>The shortest route (in steps) a maze of this size may have: at least three times the
    /// straight-line distance from start to finish (the rule that matters for small mazes), and at
    /// least 6% of all the squares (for big ones, where a route that long is still well under what
    /// these mazes normally produce, about 8-10%).</summary>
    public static int MinRouteLength(int size) => Math.Max(3 * (size - 1), size * size * 6 / 100);

    /// <summary>The longest straight run the route may have.</summary>
    public static int MaxStraightRun(int size) => Math.Max(5, size / 5);

    public static bool IsWinding(RouteStats route, int size) =>
        route.Length >= MinRouteLength(size) && route.LongestStraight <= MaxStraightRun(size);

    /// <summary>
    /// A new random square maze whose route from start to finish winds about instead of heading
    /// there directly: mazes that come out with a short or long-straight route are thrown away and
    /// regenerated (if none of <see cref="MaxAttempts"/> tries is good enough, the best is used).
    /// </summary>
    public static Maze GenerateWinding(int size, Random random)
    {
        Maze? best = null;
        var bestScore = double.MinValue;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var maze = Generate(size, size, random.Next(), WindingBias);
            var route = MazeSolver.Measure(maze);
            if (IsWinding(route, size)) return maze;

            // For the fallback: how far short of each limit it falls, summed (bigger is better).
            var score = (double)route.Length / MinRouteLength(size) - (double)route.LongestStraight / MaxStraightRun(size);
            if (score > bestScore)
            {
                bestScore = score;
                best = maze;
            }
        }

        return best!;
    }

    /// <summary>
    /// Builds a "perfect" maze (every cell reachable, exactly one route between any two cells) with
    /// the growing-tree algorithm. <paramref name="newestBias"/> is the chance of continuing from the
    /// most recently carved cell instead of a random earlier one: near 0 gives lots of short side
    /// branches and a short route, near 1 gives long winding corridors and a long route. The same
    /// seed always gives the same maze.
    /// </summary>
    public static Maze Generate(int cols, int rows, int seed, double newestBias)
    {
        var maze = new Maze(cols, rows);
        var rng = new Random(seed);
        var visited = new bool[cols * rows];
        var active = new List<int>();

        var (startCol, startRow) = maze.Start;
        var startIndex = startRow * cols + startCol;
        visited[startIndex] = true;
        active.Add(startIndex);

        var options = new Direction[4];
        while (active.Count > 0)
        {
            var pick = rng.NextDouble() < newestBias ? active.Count - 1 : rng.Next(active.Count);
            var cell = active[pick];
            var col = cell % cols;
            var row = cell / cols;

            var n = 0;
            foreach (var d in Enum.GetValues<Direction>())
            {
                var nc = col + d.Dx();
                var nr = row + d.Dy();
                if (maze.InBounds(nc, nr) && !visited[nr * cols + nc]) options[n++] = d;
            }

            if (n == 0)
            {
                // Dead end: this cell is finished. Removing from the END is O(1), and when it isn't the
                // end (a random pick) the order of the rest still has to stay "oldest first".
                active.RemoveAt(pick);
                continue;
            }

            var dir = options[rng.Next(n)];
            maze.Carve(col, row, dir);
            var next = (row + dir.Dy()) * cols + (col + dir.Dx());
            visited[next] = true;
            active.Add(next);
        }

        return maze;
    }
}

public static class MazeSolver
{
    /// <summary>The route from <see cref="Maze.Start"/> to <see cref="Maze.Finish"/>, both ends
    /// included, or an empty list if there isn't one. In a perfect maze this is the only route.</summary>
    public static IReadOnlyList<(int Col, int Row)> Solve(Maze maze)
    {
        var cols = maze.Cols;
        var cameFrom = new int[cols * maze.Rows];
        Array.Fill(cameFrom, -2);

        var (sc, sr) = maze.Start;
        var (fc, fr) = maze.Finish;
        var startIndex = sr * cols + sc;
        var finishIndex = fr * cols + fc;

        var queue = new Queue<int>();
        cameFrom[startIndex] = -1;
        queue.Enqueue(startIndex);

        while (queue.Count > 0 && cameFrom[finishIndex] == -2)
        {
            var cell = queue.Dequeue();
            var col = cell % cols;
            var row = cell / cols;
            foreach (var d in Enum.GetValues<Direction>())
            {
                if (!maze.IsOpen(col, row, d)) continue;
                var next = (row + d.Dy()) * cols + (col + d.Dx());
                if (cameFrom[next] != -2) continue;
                cameFrom[next] = cell;
                queue.Enqueue(next);
            }
        }

        if (cameFrom[finishIndex] == -2) return Array.Empty<(int, int)>();

        var path = new List<(int Col, int Row)>();
        for (var at = finishIndex; at != -1; at = cameFrom[at]) path.Add((at % cols, at / cols));
        path.Reverse();
        return path;
    }

    /// <summary>Length, longest straight run and number of turns of the route from start to finish.</summary>
    public static RouteStats Measure(Maze maze)
    {
        var route = Solve(maze);
        if (route.Count < 2) return new RouteStats(0, 0, 0);

        int longest = 0, run = 0, turns = 0;
        (int Dx, int Dy)? previous = null;
        for (var i = 1; i < route.Count; i++)
        {
            var step = (route[i].Col - route[i - 1].Col, route[i].Row - route[i - 1].Row);
            if (step == previous)
            {
                run++;
            }
            else
            {
                if (previous is not null) turns++;
                run = 1;
            }
            longest = Math.Max(longest, run);
            previous = step;
        }

        return new RouteStats(route.Count - 1, longest, turns);
    }
}
