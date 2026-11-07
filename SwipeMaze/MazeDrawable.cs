using SwipeMaze.Core;

namespace SwipeMaze;

/// <summary>Draws the current maze: walls, the trail walked so far, START/FINISH arrows and the
/// avatar. Sized to fit whatever area it's given, so a 10x10 maze and a 100x100 one both fill the
/// screen width, and then zoomed and panned by <see cref="View"/>.</summary>
public sealed class MazeDrawable : IDrawable
{
    private static readonly Color PaperColor = Colors.White;
    private static readonly Color WallColor = Color.FromArgb("#1B5E20");
    private static readonly Color TrailColor = Color.FromArgb("#A5D6A7");
    private static readonly Color StartTint = Color.FromArgb("#C8E6C9");
    private static readonly Color FinishTint = Color.FromArgb("#FFCDD2");
    private static readonly Color StartArrow = Color.FromArgb("#2E7D32");
    private static readonly Color FinishArrow = Color.FromArgb("#C62828");
    private static readonly Color AvatarFill = Color.FromArgb("#FFB300");
    private static readonly Color AvatarEdge = Color.FromArgb("#E65100");
    private static readonly Color FaceColor = Color.FromArgb("#3E2723");
    private static readonly Color HighlightColor = Color.FromArgb("#1E88E5");

    // One colour per firework burst, indexed by FireworkParticle.ColorIndex.
    private static readonly Color[] FireworkColors =
    {
        Color.FromArgb("#FFC107"), Color.FromArgb("#F44336"), Color.FromArgb("#42A5F5"),
        Color.FromArgb("#66BB6A"), Color.FromArgb("#EC407A"), Color.FromArgb("#AB47BC"),
    };

    /// <summary>The "Start" / "Finish" words stay this size (in screen units) however far the maze is zoomed.</summary>
    private const float LabelFontSize = 15f;

    /// <summary>The zoomed-in square is allowed to get this big (in screen units) before zooming stops.</summary>
    private const float MaxCellOnScreen = 48f;

    // Where the maze was last drawn at zoom 1, so touches can be turned back into squares.
    private float _cell;
    private float _left;
    private float _top;

    // The walls never change for a given maze and layout, and building them means looking at every
    // square (10,000 on the biggest maze) — so keep the result instead of redoing it for every frame
    // of a pinch.
    private Maze? _wallsMaze;
    private float _wallsCell;
    private float _wallsLeft;
    private float _wallsTop;
    private PathF? _walls;

    public MazeRun? Run { get; set; }

    /// <summary>Zoom and pan.</summary>
    public ViewTransform View { get; } = new();

    /// <summary>The celebration drawn over the maze when a level is solved. Its positions are in screen
    /// units, so zooming and panning don't move it.</summary>
    public Fireworks Fireworks { get; } = new();

    /// <summary>A square to ring in blue, e.g. the one being asked about in "go back here?".</summary>
    public (int Col, int Row)? Highlight { get; set; }

    /// <summary>Side of one square as it appears on screen right now (so it grows when zoomed in), in the
    /// same units as touch coordinates. 0 before the first draw.</summary>
    public float CellSize => (float)(_cell * View.Zoom);

    /// <summary>
    /// Turns a touch position into cell units, where the centre of square (3,4) is exactly (3.0, 4.0),
    /// allowing for the current zoom and pan. False if nothing has been drawn yet.
    /// </summary>
    public bool TryGetCellPosition(PointF touch, out double col, out double row)
    {
        col = row = 0;
        if (_cell <= 0) return false;

        var (wx, wy) = View.ToWorld(touch.X, touch.Y);
        col = (wx - _left) / _cell - 0.5;
        row = (wy - _top) / _cell - 0.5;
        return true;
    }

    /// <summary>Pans, if the maze is zoomed in, so the avatar is not near or past the edge of the view.</summary>
    public void KeepAvatarInView()
    {
        if (Run is not { } run || _cell <= 0) return;

        var x = _left + (run.Attempt.Col + 0.5) * _cell;
        var y = _top + (run.Attempt.Row + 0.5) * _cell;
        View.EnsureVisible(x, y, margin: Math.Min(View.ViewWidth, View.ViewHeight) * 0.2);
    }

    public void Draw(ICanvas canvas, RectF area)
    {
        if (Run is not { } run) return;

        var maze = run.Maze;
        var attempt = run.Attempt;

        // One extra cell above and below the maze for the START / FINISH arrows.
        var cell = MathF.Min(area.Width / maze.Cols, area.Height / (maze.Rows + 2f));
        if (cell < 1f) return;

        var width = cell * maze.Cols;
        var height = cell * maze.Rows;
        var left = area.X + (area.Width - width) / 2f;
        var top = area.Y + (area.Height - height) / 2f;
        (_cell, _left, _top) = (cell, left, top);

        // Zoom in until a square is a comfortable size to see, and no further. Small mazes get little
        // or no zoom; the 100x100 one gets a lot.
        View.SetView(area.Width, area.Height, maxZoom: Math.Max(1.5, MaxCellOnScreen / cell));

        canvas.SaveState();
        // MAUI doesn't clip a view's drawing to its own bounds, so a zoomed-in maze would otherwise be
        // painted over the level title above it and behind the buttons below it.
        canvas.ClipRectangle(area.X, area.Y, area.Width, area.Height);
        canvas.Translate((float)View.X, (float)View.Y);
        canvas.Scale((float)View.Zoom, (float)View.Zoom);

        float CenterX(int col) => left + (col + 0.5f) * cell;
        float CenterY(int row) => top + (row + 0.5f) * cell;

        canvas.FillColor = PaperColor;
        canvas.FillRectangle(left, top, width, height);

        var (startCol, startRow) = maze.Start;
        var (finishCol, finishRow) = maze.Finish;
        canvas.FillColor = StartTint;
        canvas.FillRectangle(left + startCol * cell, top + startRow * cell, cell, cell);
        canvas.FillColor = FinishTint;
        canvas.FillRectangle(left + finishCol * cell, top + finishRow * cell, cell, cell);

        // The trail walked so far.
        if (attempt.Path.Count > 1)
        {
            var trail = new PathF();
            trail.MoveTo(CenterX(attempt.Path[0].Col), CenterY(attempt.Path[0].Row));
            for (var i = 1; i < attempt.Path.Count; i++)
                trail.LineTo(CenterX(attempt.Path[i].Col), CenterY(attempt.Path[i].Row));

            canvas.StrokeColor = TrailColor;
            canvas.StrokeSize = cell * 0.45f;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(trail);
        }

        canvas.StrokeColor = WallColor;
        // Thin walls on the big mazes, or the corridors between them would disappear.
        canvas.StrokeSize = MathF.Max(1f, cell * 0.11f);
        canvas.StrokeLineCap = LineCap.Round;
        canvas.DrawPath(WallsFor(maze, left, top, cell));

        var startTip = top - cell * 0.15f;
        var finishTip = top + height + cell * 0.85f;
        DrawArrow(canvas, CenterX(startCol), startTip, cell, StartArrow);
        DrawArrow(canvas, CenterX(finishCol), finishTip, cell, FinishArrow);

        // The words go beyond the arrows, at a size that doesn't change with the zoom.
        var zoom = (float)View.Zoom;
        var arrowHeight = 0.6f * MathF.Max(cell, 6f);
        DrawLabel(canvas, "Start", CenterX(startCol), startTip - arrowHeight - 2f / zoom, area.Y, above: true, StartArrow, zoom);
        DrawLabel(canvas, "Finish", CenterX(finishCol), finishTip + 2f / zoom, area.Y + area.Height, above: false, FinishArrow, zoom);

        DrawAvatar(canvas, CenterX(attempt.Col), CenterY(attempt.Row), cell);

        if (Highlight is { } h)
        {
            canvas.StrokeColor = HighlightColor;
            canvas.StrokeSize = MathF.Max(2f, cell * 0.15f);
            canvas.DrawCircle(CenterX(h.Col), CenterY(h.Row), MathF.Max(cell * 0.7f, 7f));
        }

        canvas.RestoreState();

        // The fireworks sit on top of everything, in screen units (outside the zoom).
        if (Fireworks.IsActive)
        {
            foreach (var p in Fireworks.Snapshot())
            {
                canvas.FillColor = FireworkColors[p.ColorIndex].WithAlpha((float)p.Alpha);
                canvas.FillCircle((float)p.X, (float)p.Y, (float)p.Size);
            }
        }
    }

    /// <summary>
    /// A word beside an arrow: its edge nearest the arrow at <paramref name="edgeY"/>, extending away from
    /// it. Drawn in maze coordinates but sized by dividing by the zoom, so it looks the same size on
    /// screen at any zoom. When showing the whole maze, it shrinks to fit if the room is tight.
    /// </summary>
    private static void DrawLabel(ICanvas canvas, string text, float centerX, float edgeY, float limitY,
        bool above, Color color, float zoom)
    {
        var fontSize = LabelFontSize / zoom;
        if (zoom <= 1.001f)
        {
            var room = above ? edgeY - limitY : limitY - edgeY;
            fontSize = MathF.Min(fontSize, room / 1.4f);
            if (fontSize < 6f) return;
        }

        var width = 90f / zoom;
        var height = fontSize * 1.4f;
        canvas.Font = Microsoft.Maui.Graphics.Font.DefaultBold;
        canvas.FontSize = fontSize;
        canvas.FontColor = color;
        canvas.DrawString(text, centerX - width / 2f, above ? edgeY - height : edgeY, width, height,
            HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    /// <summary>All the walls as one path: the border (open at the start and finish) plus every closed
    /// wall between neighbouring cells.</summary>
    private PathF WallsFor(Maze maze, float left, float top, float cell)
    {
        if (_walls is not null && ReferenceEquals(_wallsMaze, maze) &&
            _wallsCell == cell && _wallsLeft == left && _wallsTop == top)
            return _walls;

        var walls = new PathF();
        void Line(float x1, float y1, float x2, float y2)
        {
            walls.MoveTo(x1, y1);
            walls.LineTo(x2, y2);
        }

        var width = cell * maze.Cols;
        var height = cell * maze.Rows;
        var (startCol, _) = maze.Start;
        var (finishCol, _) = maze.Finish;

        for (var row = 0; row < maze.Rows; row++)
        {
            var y = top + row * cell;
            Line(left, y, left, y + cell);                                   // left border
            Line(left + width, y, left + width, y + cell);                   // right border
        }
        for (var col = 0; col < maze.Cols; col++)
        {
            var x = left + col * cell;
            if (col != startCol) Line(x, top, x + cell, top);                // top border, minus the start opening
            if (col != finishCol) Line(x, top + height, x + cell, top + height); // bottom border, minus the finish opening
        }
        for (var row = 0; row < maze.Rows; row++)
        {
            for (var col = 0; col < maze.Cols; col++)
            {
                var x = left + col * cell;
                var y = top + row * cell;
                if (col < maze.Cols - 1 && !maze.IsOpen(col, row, Direction.Right))
                    Line(x + cell, y, x + cell, y + cell);
                if (row < maze.Rows - 1 && !maze.IsOpen(col, row, Direction.Down))
                    Line(x, y + cell, x + cell, y + cell);
            }
        }

        (_wallsMaze, _wallsCell, _wallsLeft, _wallsTop, _walls) = (maze, cell, left, top, walls);
        return walls;
    }

    /// <summary>A down-pointing triangle whose tip is at (x, tipY). Never smaller than a few pixels,
    /// so it can still be seen on the big mazes.</summary>
    private static void DrawArrow(ICanvas canvas, float x, float tipY, float cell, Color color)
    {
        var size = MathF.Max(cell, 6f);
        var arrow = new PathF();
        arrow.MoveTo(x - size * 0.35f, tipY - size * 0.6f);
        arrow.LineTo(x + size * 0.35f, tipY - size * 0.6f);
        arrow.LineTo(x, tipY);
        arrow.Close();
        canvas.FillColor = color;
        canvas.FillPath(arrow);
    }

    private static void DrawAvatar(ICanvas canvas, float x, float y, float cell)
    {
        // On the biggest mazes a square is only a few pixels wide; keep the avatar findable.
        var radius = MathF.Max(cell * 0.38f, 2f);

        canvas.FillColor = AvatarFill;
        canvas.FillCircle(x, y, radius);
        canvas.StrokeColor = AvatarEdge;
        canvas.StrokeSize = MathF.Max(0.8f, radius * 0.16f);
        canvas.DrawCircle(x, y, radius);

        // A little face, so it reads as a character rather than a dot.
        var eye = MathF.Max(0.6f, radius * 0.16f);
        canvas.FillColor = FaceColor;
        canvas.FillCircle(x - radius * 0.34f, y - radius * 0.2f, eye);
        canvas.FillCircle(x + radius * 0.34f, y - radius * 0.2f, eye);

        var smile = new PathF();
        smile.MoveTo(x - radius * 0.4f, y + radius * 0.18f);
        smile.QuadTo(x, y + radius * 0.72f, x + radius * 0.4f, y + radius * 0.18f);
        canvas.StrokeColor = FaceColor;
        canvas.StrokeSize = MathF.Max(0.5f, radius * 0.12f);
        canvas.StrokeLineCap = LineCap.Round;
        canvas.DrawPath(smile);
    }
}
