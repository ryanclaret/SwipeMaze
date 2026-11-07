namespace SwipeMaze.Core;

/// <summary>
/// Zoom and pan for the maze view. "World" is the maze drawn to fit the view (zoom 1); "screen" is what
/// is actually shown: screen = world * <see cref="Zoom"/> + (<see cref="X"/>, <see cref="Y"/>). The view
/// can never be panned past the maze's edges, and zooming all the way out puts it back exactly as it was.
/// </summary>
public sealed class ViewTransform
{
    private double _focalX;
    private double _focalY;
    private double _pinchStartZoom = 1;

    public double Zoom { get; private set; } = 1;

    /// <summary>Where world (0,0) is on screen. Never positive: the maze can only slide left and up.</summary>
    public double X { get; private set; }
    public double Y { get; private set; }

    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }
    public double MaxZoom { get; private set; } = 1;

    public bool IsZoomed => Zoom > 1.001;

    /// <summary>Tells the transform how big the view is and how far it may zoom in.</summary>
    public void SetView(double width, double height, double maxZoom)
    {
        ViewWidth = width;
        ViewHeight = height;
        MaxZoom = Math.Max(1, maxZoom);
        Zoom = Math.Min(Zoom, MaxZoom);
        Clamp();
    }

    public void Reset()
    {
        Zoom = 1;
        X = Y = 0;
    }

    public (double X, double Y) ToWorld(double screenX, double screenY) => ((screenX - X) / Zoom, (screenY - Y) / Zoom);
    public (double X, double Y) ToScreen(double worldX, double worldY) => (worldX * Zoom + X, worldY * Zoom + Y);

    /// <summary>Two fingers have just gone down, their midpoint at (midX, midY): remember which bit of the
    /// maze is under it, so it can be kept there while the fingers move.</summary>
    public void BeginPinch(double midX, double midY)
    {
        _pinchStartZoom = Zoom;
        (_focalX, _focalY) = ToWorld(midX, midY);
    }

    /// <summary>
    /// The fingers have moved: <paramref name="scale"/> is their distance now divided by their distance when
    /// the pinch began, and (midX, midY) is where their midpoint is now. The bit of the maze that was under
    /// the midpoint stays under it, so spreading zooms in on that spot and dragging both fingers pans.
    /// </summary>
    public void UpdatePinch(double midX, double midY, double scale)
    {
        Zoom = Math.Clamp(_pinchStartZoom * scale, 1, MaxZoom);
        X = midX - _focalX * Zoom;
        Y = midY - _focalY * Zoom;
        Clamp();
    }

    /// <summary>
    /// Pans just enough to bring a world point back within <paramref name="margin"/> of the view's edges.
    /// Nothing happens if it is already comfortably inside, or the maze isn't zoomed.
    /// </summary>
    public void EnsureVisible(double worldX, double worldY, double margin)
    {
        if (!IsZoomed) return;

        var (sx, sy) = ToScreen(worldX, worldY);
        margin = Math.Min(margin, Math.Min(ViewWidth, ViewHeight) / 2);

        if (sx < margin) X += margin - sx;
        else if (sx > ViewWidth - margin) X -= sx - (ViewWidth - margin);

        if (sy < margin) Y += margin - sy;
        else if (sy > ViewHeight - margin) Y -= sy - (ViewHeight - margin);

        Clamp();
    }

    private void Clamp()
    {
        X = Math.Clamp(X, ViewWidth - ViewWidth * Zoom, 0);
        Y = Math.Clamp(Y, ViewHeight - ViewHeight * Zoom, 0);
    }
}
