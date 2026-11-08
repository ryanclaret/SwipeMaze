using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SwipeMaze.Core;
using SwipeMaze.Play.Services;

namespace SwipeMaze.Play.Pages;

/// <summary>
/// Single page: the start screen, the maze and the leaderboard, turning swipes and taps into moves on a
/// <see cref="MazeRun"/>. All the rules live in SwipeMaze.Core; this is the web counterpart of the Android
/// app's MainPage, and follows it closely. Painting and raw touch handling are in wwwroot/js/game.js.
/// </summary>
public partial class Home : IDisposable
{
    private const string NameKey = "swipemaze.name";
    private const string CenterKey = "swipemaze.center";

    /// <summary>How far from a trail square a tap can land and still mean that square.</summary>
    private const double TapSlack = 12;

    /// <summary>Pause between squares while the avatar runs along a corridor, so the run can be seen.</summary>
    private const int StepDelayMs = 12;

    /// <summary>Pixels kept clear above and below the maze, beyond the arrows, for the Start and Finish words.</summary>
    private const double LabelBand = 24;

    /// <summary>The zoomed-in square is allowed to get this big (in screen units) before zooming stops.</summary>
    private const double MaxCellOnScreen = 48;

    private enum Screen { Start, Game, Leaderboard }
    private enum DialogStyle { Primary, Danger, Neutral }
    private enum StartChoice { Continue, StartOver, Cancel }

    private sealed record DialogModel(string Title, string Message, bool AtBottom, (string Text, DialogStyle Style)[] Buttons);

    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private IPlayerProgressRepository Store { get; set; } = null!;
    [Inject] private WebSoundPlayer WebSounds { get; set; } = null!;
    [Inject] private BrowserStore Settings { get; set; } = null!;
    [Inject] private TimeProvider Clock { get; set; } = null!;

    private IJSInProcessRuntime _js = null!;
    private DotNetObjectReference<Home>? _self;

    private Screen _screen = Screen.Start;
    private MazeRun? _run;
    private string _playerName = "";
    private PlayerProgress? _progress;
    private bool _starting;
    private bool _asking;
    private bool _animating;   // a swipe's run is being shown; further swipes wait

    /// <summary>Bumped whenever the game on screen changes, so a pause that was in flight (the "level
    /// complete" message) can tell it no longer belongs to the game on screen and stop.</summary>
    private int _session;

    private string _name = "";
    private string _hint = "";
    private string _status = "";
    private string _baseStatus = "";
    private int _statusToken;

    private string _boardTitle = "";
    private string _boardSummary = "";
    private List<string> _boardLines = new();

    // ---- the view: zoom and pan, and where the maze sits in it ----
    private readonly ViewTransform _view = new();
    private readonly Fireworks _fireworks = new();
    private bool _centerAvatar = true;
    private (int Col, int Row)? _highlight;
    private double _viewW, _viewH;
    private double _cell, _left, _top;
    private Maze? _sentMaze;

    private DialogModel? _dialog;
    private TaskCompletionSource<int>? _dialogResult;

    private string LevelText => _run is { } run ? $"Level {run.Level} · {run.Size} × {run.Size}" : "";

    private static string StyleClass(DialogStyle style) => style switch
    {
        DialogStyle.Primary => "primary",
        DialogStyle.Danger => "danger",
        _ => "neutral",
    };

    protected override void OnInitialized()
    {
        _js = (IJSInProcessRuntime)JS;

        // Whoever played last is most likely playing again.
        _name = Settings.Get(NameKey) ?? "";
        _centerAvatar = Settings.Get(CenterKey) != "0";
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            _js.InvokeVoid("game.init", _self, "maze");
        }

        // The maze area only has a size once the page has shown it, so measure it now rather than waiting
        // to be told: the browser's "resized" notification can lag, or not come at all if the page isn't
        // being painted (a background tab).
        if (_screen == Screen.Game)
        {
            var size = _js.Invoke<double[]>("game.size");
            _viewW = size[0];
            _viewH = size[1];
            Redraw();
        }

        if (firstRender) await WebSounds.PreloadAsync();
    }

    public void Dispose() => _self?.Dispose();

    // ================================================================ dialogs

    /// <summary>Shows the game's own dialog and returns the index of the button tapped (-1 if dismissed
    /// with Escape or if another dialog is already open).</summary>
    private async Task<int> ShowDialogAsync(string title, string message, bool atBottom,
        params (string Text, DialogStyle Style)[] buttons)
    {
        if (_dialog is not null) return -1;
        var result = _dialogResult = new TaskCompletionSource<int>();
        _dialog = new DialogModel(title, message, atBottom, buttons);
        StateHasChanged();
        return await result.Task;
    }

    private void CloseDialog(int result)
    {
        var pending = _dialogResult;
        if (pending is null) return;
        _dialogResult = null;
        _dialog = null;
        StateHasChanged();
        pending.TrySetResult(result);
    }

    /// <summary>A yes/no question: true if the first button (<paramref name="accept"/>) was tapped.</summary>
    private async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel,
        bool danger = false, bool atBottom = false) =>
        await ShowDialogAsync(title, message, atBottom,
            (accept, danger ? DialogStyle.Danger : DialogStyle.Primary), (cancel, DialogStyle.Neutral)) == 0;

    /// <summary>Pick one of <paramref name="options"/>; returns its index, or -1 if cancelled.</summary>
    private async Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, string cancel)
    {
        var buttons = options.Select(o => (o, DialogStyle.Primary)).Append((cancel, DialogStyle.Neutral)).ToArray();
        var picked = await ShowDialogAsync(title, message, atBottom: false, buttons);
        return picked >= 0 && picked < options.Count ? picked : -1;
    }

    [JSInvokable]
    public void OnEscape()
    {
        if (_dialog is not null) CloseDialog(-1);
    }

    // ================================================================ start / quit

    private void OnNameInput(ChangeEventArgs e)
    {
        _name = e.Value?.ToString() ?? "";
        _hint = ""; // the "please type your name" reminder has done its job once they start typing
    }

    // Pressing Done on the keyboard just dismisses it; "Let's Play!" starts the game.
    private void OnNameKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") _js.InvokeVoid("game.blur");
    }

    /// <summary>Shows everyone who has played and lets the user pick one. Null if cancelled or nobody has played yet.</summary>
    private async Task<PlayerProgress?> PickPlayerAsync(string title, string cancel)
    {
        var players = await Store.GetAllAsync();
        if (players.Count == 0)
        {
            _hint = "No players yet — type your name to begin 🙂";
            return null;
        }

        var labels = players.Select(p => $"{p.Name} · {p.ProgressText}").ToArray();
        var index = await ChooseAsync(title, "", labels, cancel);
        return index < 0 ? null : players[index];
    }

    private async Task OnChoosePlayerClicked()
    {
        if (_starting || _asking) return;
        _asking = true;
        try
        {
            _js.InvokeVoid("game.blur");
            var player = await PickPlayerAsync("Who's playing?", "Cancel");
            if (player is null) return;

            _name = player.Name;
            _hint = "";
        }
        finally
        {
            _asking = false;
        }
    }

    private async Task OnRemovePlayerClicked()
    {
        if (_starting || _asking) return;
        _asking = true;
        try
        {
            _js.InvokeVoid("game.blur");
            var player = await PickPlayerAsync("Remove which player?", "Cancel");
            if (player is null) return;

            var sure = await ConfirmAsync($"Remove {player.Name}?",
                $"This deletes {player.Name}'s saved progress ({player.ProgressText}) and takes them off the leaderboard. This can't be undone.",
                "Remove", "Keep", danger: true);
            if (!sure) return;

            await Store.DeleteAsync(player.Name);

            // Don't leave the removed name sitting in the box, or remembered for next time.
            if (string.Equals(PlayerName.Clean(_name), player.Name, StringComparison.OrdinalIgnoreCase)) _name = "";
            if (string.Equals(Settings.Get(NameKey), player.Name, StringComparison.OrdinalIgnoreCase)) Settings.Remove(NameKey);

            _hint = $"{player.Name} was removed.";
        }
        finally
        {
            _asking = false;
        }
    }

    private async Task OnPlayClicked()
    {
        // Several awaits happen before the maze appears, so a quick double-tap could otherwise start two games.
        if (_starting || _asking) return;
        _starting = true;
        try
        {
            var name = PlayerName.Clean(_name);
            if (name.Length == 0)
            {
                _hint = "Please type your name first 🙂";
                _js.InvokeVoid("game.focus", "nameInput");
                return;
            }

            _hint = "";
            _name = name;
            _js.InvokeVoid("game.blur"); // so the on-screen keyboard doesn't sit over the maze

            var progress = await Store.GetAsync(name);

            // The maze they were part-way through, with the avatar where they left it. Only usable if it is
            // for the level they would continue from; anything else (or damage) is ignored.
            MazeRun? resumed = null;
            if (progress is not null && !progress.Completed)
            {
                var saved = await Store.LoadGameAsync(name);
                if (saved is not null && saved.Level == progress.CurrentLevel) resumed = saved.ToRun();
            }

            var startLevel = 1;
            if (progress is not null && (progress.Completed || progress.CurrentLevel > 1 || resumed is not null))
            {
                var choice = await AskHowToStartAsync(name, progress, resumed is not null);
                if (choice == StartChoice.Cancel) return;

                if (choice == StartChoice.Continue)
                {
                    startLevel = progress.CurrentLevel;
                }
                else
                {
                    // Starting over: the best level stays on the leaderboard, only the place to continue from resets.
                    progress = progress with { CurrentLevel = 1 };
                    await Store.SaveAsync(progress);
                    await Store.ClearGameAsync(name);
                    resumed = null;
                }
            }

            _playerName = name;
            _progress = progress;
            Settings.Set(NameKey, name);

            _session++;
            _run = resumed ?? new MazeRun(startLevel);
            _highlight = null;
            _view.Reset();
            _screen = Screen.Game;
            SetStatus("");
            Refresh();
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task<StartChoice> AskHowToStartAsync(string name, PlayerProgress progress, bool hasSavedMaze)
    {
        if (progress.Completed)
        {
            var again = "Play again from Level 1";
            var answer = await ChooseAsync($"Welcome back, {name}!", "You finished every level 🏆", new[] { again }, "Cancel");
            return answer == 0 ? StartChoice.StartOver : StartChoice.Cancel;
        }

        var continueText = hasSavedMaze
            ? $"Continue Level {progress.CurrentLevel} where I left off"
            : $"Continue from Level {progress.CurrentLevel}";
        var startOverText = "Start over from Level 1";
        var picked = await ChooseAsync($"Welcome back, {name}!", "", new[] { continueText, startOverText }, "Cancel");
        return picked switch { 0 => StartChoice.Continue, 1 => StartChoice.StartOver, _ => StartChoice.Cancel };
    }

    private async Task OnQuitGameClicked()
    {
        var run = _run;
        if (run is null || _asking) return;

        _asking = true;
        bool quit;
        try
        {
            quit = await ConfirmAsync("Quit this game?",
                "Your progress is saved. You can pick up right where you are next time.",
                "Quit", "Keep playing");
        }
        finally
        {
            _asking = false;
        }

        if (quit && _run == run) ShowStartScreen();
    }

    /// <summary>Back to the initial screen, ready for a (possibly different) player.</summary>
    private void ShowStartScreen()
    {
        _session++;
        _run = null;
        _highlight = null;
        _hint = "";
        _screen = Screen.Start;
        StateHasChanged();
    }

    // ================================================================ leaderboard

    private async Task OnLeaderboardClicked() => await ShowLeaderboardAsync("🏆 Furthest Level", "");

    private void OnLeaderboardBackClicked() => ShowStartScreen();

    private async Task ShowLeaderboardAsync(string title, string summary)
    {
        var top = await Store.GetTopAsync();

        _session++;
        _boardTitle = title;
        _boardSummary = summary;
        _boardLines = top.Select(p => $"{p.Name} · {p.ProgressText} · {p.When.ToLocalTime():MMM d, h:mm tt}").ToList();
        _screen = Screen.Leaderboard;
        StateHasChanged();
    }

    // ================================================================ input from the page (game.js)

    [JSInvokable]
    public void OnResize(double width, double height)
    {
        _viewW = width;
        _viewH = height;
        Redraw();
    }

    [JSInvokable]
    public void OnPinchStart(double midX, double midY) => _view.BeginPinch(midX, midY);

    /// <summary>Two fingers on the maze: zoom by how far apart they are, pan by where their midpoint goes.</summary>
    [JSInvokable]
    public void OnPinch(double midX, double midY, double scale)
    {
        _view.UpdatePinch(midX, midY, scale);
        Redraw();
        StateHasChanged(); // the Fit button depends on whether the maze is zoomed
    }

    /// <summary>A PC's mouse wheel: zoom around the pointer.</summary>
    [JSInvokable]
    public void OnWheel(double x, double y, double factor)
    {
        if (_run is null || _asking) return;
        _view.BeginPinch(x, y);
        _view.UpdatePinch(x, y, factor);
        Redraw();
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnSwipe(int direction)
    {
        try { await HandleMoveAsync((Direction)direction); }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    [JSInvokable]
    public async Task OnTap(double col, double row, double cellOnScreen)
    {
        try { await HandleTapAsync(col, row, cellOnScreen); }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private void OnFitClicked()
    {
        _view.Reset();
        Refresh();
    }

    /// <summary>Turns "keep the avatar centered when zoomed in" on or off, and remembers the choice.</summary>
    private void OnCenterClicked()
    {
        _centerAvatar = !_centerAvatar;
        Settings.Set(CenterKey, _centerAvatar ? "1" : "0");

        FlashStatus(_centerAvatar ? "🎯 Avatar stays centered" : "🎯 Avatar not centered");
        if (_centerAvatar) FollowAvatar(); // take effect straight away
        Redraw();
    }

    // ================================================================ playing

    /// <summary>
    /// One swipe. The avatar runs along the corridor until there's a choice to make (a fork), hits a dead
    /// end, or reaches the finish — and going back runs back along the trail the same way. The run is
    /// shown square by square, with a sound as it sets off: a buzz if it's blocked by a wall.
    /// </summary>
    private async Task HandleMoveAsync(Direction direction)
    {
        var run = _run;
        if (_screen != Screen.Game || run is null || run.Phase != RunPhase.Playing || _asking || _animating) return;
        var session = _session;

        _animating = true;
        try
        {
            var first = true;
            var last = MoveResult.Wall;
            foreach (var result in run.Run(direction))
            {
                if (first)
                {
                    WebSounds.Play(SoundFor(result, direction));
                    first = false;
                }
                last = result;

                if (result == MoveResult.Wall)
                {
                    FlashStatus("🧱 Wall!");
                    return;
                }

                FollowAvatar();
                Redraw();
                if (result != MoveResult.Finished) await Task.Delay(StepDelayMs);
                if (session != _session) return;
            }

            if (last == MoveResult.Finished)
            {
                await CelebrateAsync(run, session);
                return;
            }

            SetStatus(run.Attempt.IsDeadEnd ? "💥 Dead end! Swipe back ↩️" : "");
            Refresh();
            await SaveGameAsync(run, session);
        }
        finally
        {
            _animating = false;
        }
    }

    private static Sound SoundFor(MoveResult first, Direction direction) => first switch
    {
        MoveResult.Wall => Sound.Wall,
        MoveResult.BackedUp => Sound.Back,
        _ => direction switch
        {
            Direction.Up => Sound.SwipeUp,
            Direction.Right => Sound.SwipeRight,
            Direction.Down => Sound.SwipeDown,
            _ => Sound.SwipeLeft,
        },
    };

    /// <summary>The maze is solved: save it, play the winning sound with fireworks, then on to the next
    /// level (or the leaderboard, after the last one).</summary>
    private async Task CelebrateAsync(MazeRun run, int session)
    {
        var allDone = run.Phase == RunPhase.AllComplete;

        SetStatus(allDone ? "🏆 You finished every level!" : $"🎉 Level {run.Level} complete!");
        Refresh();
        // Saved now, so quitting during the fireworks loses nothing.
        await SaveProgressAsync(allDone ? run.Level : run.Level + 1, completed: allDone);
        await Store.ClearGameAsync(_playerName); // solved: nothing left to resume

        await PlayFireworksAsync(session);
        if (session != _session) return;

        if (allDone)
        {
            await ShowLeaderboardAsync($"🏆 All {MazeLevels.Count} levels complete!", $"{_playerName} finished every level!");
            return;
        }

        run.Continue();
        _view.Reset(); // a new maze, shown whole
        SetStatus("");
        Refresh();
    }

    /// <summary>The winning jingle and a few bursts of fireworks over the maze, with a pop for each burst.</summary>
    private async Task PlayFireworksAsync(int session)
    {
        _fireworks.Start(_viewW, _viewH, bursts: 6, span: 1.6);
        WebSounds.Play(Sound.Win);

        var clock = Stopwatch.StartNew();
        var previous = 0.0;
        while (_fireworks.IsActive && session == _session)
        {
            var now = clock.Elapsed.TotalSeconds;
            var bursts = _fireworks.Update(now - previous);
            previous = now;

            for (var i = 0; i < bursts; i++) WebSounds.Play(Sound.Pop);
            Redraw();
            await Task.Delay(16);
        }

        _fireworks.Clear();
        Redraw();
    }

    /// <summary>A tap on a square of the trail asks whether to go back to it.</summary>
    private async Task HandleTapAsync(double col, double row, double cellOnScreen)
    {
        var run = _run;
        if (_screen != Screen.Game || run is null || run.Phase != RunPhase.Playing || _asking || _animating) return;

        // The slack is a fixed size on screen, so on the big mazes (tiny squares) it spans several
        // squares and the nearest trail square wins.
        var maxDistance = Math.Max(0.6, TapSlack / Math.Max(cellOnScreen, 1));
        if (!run.Attempt.TryFindBackUpTarget(col, row, maxDistance, out var target)) return;

        // Ring the square being asked about, since on a big maze it's hard to tell which one was meant.
        _asking = true;
        _highlight = target;
        Redraw();
        bool goBack;
        try
        {
            // At the foot of the screen, so the ringed square being asked about stays in view.
            goBack = await ConfirmAsync("Go back here?",
                "Move back to the ringed square? The trail after it will be erased.", "Go back", "Stay", atBottom: true);
        }
        finally
        {
            _asking = false;
            _highlight = null;
        }

        if (goBack && _run == run && run.BackUpTo(target.Col, target.Row))
        {
            WebSounds.Play(Sound.Back);
            SetStatus("");
            FollowAvatar();
            Refresh();
            await SaveGameAsync(run, _session);
        }
        else
        {
            Refresh();
        }
    }

    private async Task OnRetryClicked()
    {
        var run = _run;
        if (run is null || !run.CanRetry || _asking || _animating) return;

        // A long trail is a lot of swipes, so don't let a stray tap throw it away.
        _asking = true;
        bool restart;
        try
        {
            restart = await ConfirmAsync("Start this maze over?", "Your trail will be erased.", "Start over", "Keep going");
        }
        finally
        {
            _asking = false;
        }

        if (restart && _run == run && run.Retry())
        {
            WebSounds.Play(Sound.Back);
            SetStatus("");
            FollowAvatar(); // back at the start square
            Refresh();
            await SaveGameAsync(run, _session);
        }
        else
        {
            Refresh();
        }
    }

    // ================================================================ drawing

    /// <summary>Works out where the maze sits in the view at zoom 1 (with a spare square above and below for
    /// the Start and Finish arrows) and tells the zoom how far it may go. False if there is nothing to draw.</summary>
    private bool UpdateLayout()
    {
        if (_run is not { } run || _viewW < 1 || _viewH < 1) return false;

        // The arrows take about 3/4 of a square each, and the words "Start" / "Finish" a fixed LabelBand of
        // pixels beyond them, so the words keep their full size even in a wide, short window.
        var maze = run.Maze;
        var cell = Math.Min(_viewW / maze.Cols, (_viewH - 2 * LabelBand) / (maze.Rows + 1.5));
        if (cell < 1) return false;

        _cell = cell;
        _left = (_viewW - cell * maze.Cols) / 2;
        _top = (_viewH - cell * maze.Rows) / 2;

        // Zoom in until a square is a comfortable size to see, and no further. Small mazes get little or no
        // zoom; the 100x100 one gets a lot.
        _view.SetView(_viewW, _viewH, maxZoom: Math.Max(1.5, MaxCellOnScreen / cell));
        return true;
    }

    /// <summary>Pans, if the maze is zoomed in, to follow the avatar: centered, or just kept off the edge.</summary>
    private void FollowAvatar()
    {
        if (_run is not { } run || !UpdateLayout()) return;

        var x = _left + (run.Attempt.Col + 0.5) * _cell;
        var y = _top + (run.Attempt.Row + 0.5) * _cell;
        if (_centerAvatar) _view.CenterOn(x, y);
        else _view.EnsureVisible(x, y, margin: Math.Min(_view.ViewWidth, _view.ViewHeight) * 0.2);
    }

    /// <summary>Repaints the maze. Cheap enough to call for every square of a run.</summary>
    private void Redraw()
    {
        if (_screen != Screen.Game || _run is not { } run || !UpdateLayout()) return;

        var maze = run.Maze;
        if (!ReferenceEquals(_sentMaze, maze))
        {
            _js.InvokeVoid("game.setMaze", maze.Cols, maze.Rows, maze.Export().Select(b => (int)b).ToArray());
            _sentMaze = maze;
        }

        var path = run.Attempt.Path;
        var trail = new int[path.Count * 2];
        for (var i = 0; i < path.Count; i++)
        {
            trail[2 * i] = path[i].Col;
            trail[2 * i + 1] = path[i].Row;
        }

        double[] sparks = Array.Empty<double>();
        if (_fireworks.IsActive)
        {
            var snapshot = _fireworks.Snapshot();
            sparks = new double[snapshot.Count * 5];
            for (var i = 0; i < snapshot.Count; i++)
            {
                var p = snapshot[i];
                sparks[5 * i] = p.X;
                sparks[5 * i + 1] = p.Y;
                sparks[5 * i + 2] = p.Size;
                sparks[5 * i + 3] = p.ColorIndex;
                sparks[5 * i + 4] = p.Alpha;
            }
        }

        _js.InvokeVoid("game.draw", _cell, _left, _top, _view.Zoom, _view.X, _view.Y, trail,
            run.Attempt.Col, run.Attempt.Row, _highlight?.Col ?? -1, _highlight?.Row ?? -1, sparks);
    }

    /// <summary>Updates the page (level line, buttons) and repaints the maze.</summary>
    private void Refresh()
    {
        StateHasChanged();
        Redraw();
    }

    /// <summary>Sets the message that stays until the next one replaces it.</summary>
    private void SetStatus(string text)
    {
        _baseStatus = text;
        _statusToken++; // a newer message cancels the pending clear of an older flash
        _status = text;
    }

    /// <summary>A brief message that then goes back to whatever was showing before.</summary>
    private async void FlashStatus(string text)
    {
        var token = ++_statusToken;
        _status = text;
        StateHasChanged();
        await Task.Delay(800);
        if (token != _statusToken) return;
        _status = _baseStatus;
        StateHasChanged();
    }

    // ================================================================ saving

    /// <summary>
    /// Remembers the maze and where the avatar is on it, so the player can pick up exactly here later.
    /// The first save also puts a new player on the leaderboard record at Level 1 (so they can be chosen
    /// by name next time). A failed save never interrupts play; the previous save simply stays.
    /// </summary>
    private async Task SaveGameAsync(MazeRun run, int session)
    {
        if (session != _session || _run != run || run.Phase != RunPhase.Playing) return;
        try
        {
            if (_progress is null) await SaveProgressAsync(run.Level, completed: false);
            await Store.SaveGameAsync(_playerName, SavedGame.From(run));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
    }

    private async Task SaveProgressAsync(int level, bool completed)
    {
        var progress = PlayerProgress.Reached(_playerName, level, completed, Clock.GetUtcNow(), _progress);
        await Store.SaveAsync(progress);
        _progress = progress;
    }
}
