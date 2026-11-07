namespace SwipeMaze.Core;

public enum Direction { Up, Right, Down, Left }

public static class DirectionExtensions
{
    public static int Dx(this Direction d) => d switch { Direction.Right => 1, Direction.Left => -1, _ => 0 };
    public static int Dy(this Direction d) => d switch { Direction.Down => 1, Direction.Up => -1, _ => 0 };
    public static Direction Opposite(this Direction d) => (Direction)(((int)d + 2) % 4);
}

public static class SwipeDetector
{
    /// <summary>
    /// Turns a finger movement (screen coordinates: x grows right, y grows down) into a direction.
    /// The longer axis wins, so a slightly diagonal swipe still counts. Returns null if the finger
    /// moved less than <paramref name="minDistance"/> — that's a tap, not a swipe.
    /// </summary>
    public static Direction? Detect(double dx, double dy, double minDistance)
    {
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        if (Math.Max(ax, ay) < minDistance) return null;
        if (ax >= ay) return dx > 0 ? Direction.Right : Direction.Left;
        return dy > 0 ? Direction.Down : Direction.Up;
    }
}
