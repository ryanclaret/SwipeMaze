using SwipeMaze.Core;

namespace SwipeMaze.Tests;

public class ViewTransformTests
{
    private static ViewTransform View(double w = 400, double h = 800, double maxZoom = 10)
    {
        var view = new ViewTransform();
        view.SetView(w, h, maxZoom);
        return view;
    }

    [Fact]
    public void A_new_view_is_not_zoomed_and_maps_points_to_themselves()
    {
        var view = View();

        Assert.False(view.IsZoomed);
        Assert.Equal((123.0, 456.0), view.ToWorld(123, 456));
        Assert.Equal((123.0, 456.0), view.ToScreen(123, 456));
    }

    [Fact]
    public void Spreading_two_fingers_zooms_in_and_keeps_the_spot_under_them_still()
    {
        var view = View();
        view.BeginPinch(300, 200);
        var under = view.ToWorld(300, 200);

        view.UpdatePinch(300, 200, scale: 3);

        Assert.Equal(3, view.Zoom, 6);
        Assert.True(view.IsZoomed);
        var (sx, sy) = view.ToScreen(under.X, under.Y);
        Assert.Equal(300, sx, 6);
        Assert.Equal(200, sy, 6);
    }

    [Fact]
    public void Dragging_both_fingers_pans()
    {
        var view = View();
        view.BeginPinch(200, 400);
        view.UpdatePinch(200, 400, scale: 4);
        var before = view.ToWorld(200, 400);

        // Same spread, midpoint dragged 60 right and 40 down: the maze follows the fingers.
        view.BeginPinch(200, 400);
        view.UpdatePinch(260, 440, scale: 1);

        var (sx, sy) = view.ToScreen(before.X, before.Y);
        Assert.Equal(260, sx, 6);
        Assert.Equal(440, sy, 6);
        Assert.Equal(4, view.Zoom, 6);
    }

    [Fact]
    public void A_second_pinch_carries_on_from_the_zoom_already_reached()
    {
        var view = View();
        view.BeginPinch(200, 400);
        view.UpdatePinch(200, 400, scale: 2);

        view.BeginPinch(200, 400);
        view.UpdatePinch(200, 400, scale: 2);

        Assert.Equal(4, view.Zoom, 6);
    }

    [Fact]
    public void Zoom_stops_at_the_limit_and_cannot_go_below_the_whole_maze()
    {
        var view = View(maxZoom: 6);

        view.BeginPinch(200, 400);
        view.UpdatePinch(200, 400, scale: 50);
        Assert.Equal(6, view.Zoom, 6);

        view.BeginPinch(200, 400);
        view.UpdatePinch(200, 400, scale: 0.01);
        Assert.Equal(1, view.Zoom, 6);
        Assert.False(view.IsZoomed);
    }

    [Fact]
    public void Zooming_back_out_puts_the_maze_exactly_where_it_started()
    {
        var view = View();
        view.BeginPinch(350, 700);
        view.UpdatePinch(350, 700, scale: 5);
        Assert.NotEqual(0, view.X);

        view.BeginPinch(350, 700);
        view.UpdatePinch(350, 700, scale: 0.1);

        Assert.Equal((0.0, 0.0), (view.X, view.Y));
    }

    [Fact]
    public void The_maze_cannot_be_panned_off_its_edges()
    {
        var view = View(w: 400, h: 800);
        view.BeginPinch(0, 0);
        view.UpdatePinch(0, 0, scale: 4);

        // Drag far to the right/down, then far left/up.
        view.BeginPinch(0, 0);
        view.UpdatePinch(900, 900, scale: 1);
        Assert.Equal((0.0, 0.0), (view.X, view.Y)); // the top-left corner can't come away from the top-left

        view.BeginPinch(400, 800);
        view.UpdatePinch(-5000, -5000, scale: 1);
        Assert.Equal(400 - 400 * 4, view.X, 6);     // the bottom-right corner can't come in from the bottom-right
        Assert.Equal(800 - 800 * 4, view.Y, 6);
    }

    [Fact]
    public void Zooming_in_near_a_corner_does_not_show_empty_space_beyond_it()
    {
        var view = View(w: 400, h: 800);
        view.BeginPinch(5, 5);
        view.UpdatePinch(5, 5, scale: 8);

        var (sx, sy) = view.ToScreen(0, 0);
        Assert.True(sx <= 0 && sy <= 0);
        var (ex, ey) = view.ToScreen(400, 800);
        Assert.True(ex >= 400 && ey >= 800);
    }

    [Fact]
    public void Reset_goes_back_to_the_whole_maze()
    {
        var view = View();
        view.BeginPinch(100, 100);
        view.UpdatePinch(100, 100, scale: 5);

        view.Reset();

        Assert.False(view.IsZoomed);
        Assert.Equal((0.0, 0.0), (view.X, view.Y));
    }

    [Fact]
    public void Centering_puts_the_point_in_the_middle_of_the_view()
    {
        var view = View(w: 400, h: 800);
        view.BeginPinch(0, 0);
        view.UpdatePinch(0, 0, scale: 4);       // zoomed in, showing the top-left of the maze
        var (wx, wy) = (200.0, 400.0);          // a world point comfortably inside the maze

        view.CenterOn(wx, wy);

        var (sx, sy) = view.ToScreen(wx, wy);
        Assert.Equal(200, sx, 6);
        Assert.Equal(400, sy, 6);
    }

    [Fact]
    public void Centering_a_point_already_in_the_middle_changes_nothing()
    {
        var view = View();
        view.BeginPinch(200, 400);
        view.UpdatePinch(200, 400, scale: 4);
        var (x0, y0) = (view.X, view.Y);
        var (wx, wy) = view.ToWorld(200, 400);

        view.CenterOn(wx, wy);

        Assert.Equal(x0, view.X, 6);
        Assert.Equal(y0, view.Y, 6);
    }

    [Fact]
    public void Centering_near_the_maze_edge_stops_at_the_edge_instead_of_showing_empty_space()
    {
        var view = View(w: 400, h: 800);
        view.BeginPinch(200, 400);
        view.UpdatePinch(200, 400, scale: 4);

        view.CenterOn(5, 5);                    // the top-left corner of the maze

        Assert.Equal((0.0, 0.0), (view.X, view.Y));   // can't slide past the corner
        view.CenterOn(395, 795);                // the bottom-right corner
        Assert.Equal(400 - 400 * 4, view.X, 6);
        Assert.Equal(800 - 800 * 4, view.Y, 6);
    }

    [Fact]
    public void Following_the_avatar_step_by_step_keeps_it_in_the_middle()
    {
        var view = View(w: 400, h: 800);
        view.BeginPinch(0, 0);
        view.UpdatePinch(0, 0, scale: 5);

        for (var step = 0; step < 20; step++)
        {
            var (wx, wy) = (150 + step * 6.0, 300 + step * 9.0);   // inside the maze, away from the edges
            view.CenterOn(wx, wy);
            var (sx, sy) = view.ToScreen(wx, wy);
            Assert.Equal(200, sx, 6);
            Assert.Equal(400, sy, 6);
        }
    }

    [Fact]
    public void Nothing_follows_when_the_maze_is_not_zoomed()
    {
        var view = View();

        view.CenterOn(1000, 1000);

        Assert.Equal((0.0, 0.0), (view.X, view.Y));
    }

    [Fact]
    public void Shrinking_the_view_keeps_everything_valid()
    {
        var view = View(w: 400, h: 800, maxZoom: 10);
        view.BeginPinch(0, 0);
        view.UpdatePinch(0, 0, scale: 9);

        view.SetView(400, 800, maxZoom: 3); // e.g. a different maze that can't zoom as far

        Assert.Equal(3, view.Zoom, 6);
        Assert.True(view.X >= 400 - 400 * 3 - 1e-9 && view.X <= 0);
    }
}
