using SwipeMaze.Core;

namespace SwipeMaze;

/// <summary>Single page, no MVVM: shows the start screen, the maze, and the leaderboard, and turns
/// swipes and taps into moves on a <see cref="MazeRun"/>. All the rules live in SwipeMaze.Core.</summary>
public partial class MainPage : ContentPage
{
    private const string NamePreferenceKey = "player_name";

    /// <summary>How far (in device-independent units) a finger must travel to count as a swipe.</summary>
    private const double SwipeDistance = 20;

    /// <summary>How far from a trail square a tap can land and still mean that square.</summary>
    private const double TapSlack = 12;

    private const int SolvedPauseMs = 1400;

    private readonly IPlayerProgressRepository _progressStore;
    private readonly TimeProvider _clock;
    private readonly MazeDrawable _drawable = new();

    private MazeRun? _run;
    private string _playerName = "";
    private PlayerProgress? _progress;
    private bool _starting;
    private bool _asking;

    /// <summary>Bumped whenever the game on screen changes, so a pause that was in flight (the
    /// "level complete" message) can tell it no longer belongs to the game on screen and stop.</summary>
    private int _session;

    private string _baseStatus = "";
    private int _statusToken;

    private PointF _touchStart;
    private PointF _touchLast;
    private bool _touching;

    // Two fingers down means a pinch/pan, never a swipe or a tap, even once one finger has lifted.
    private bool _multiTouch;
    private bool _pinching;
    private double _pinchStartDistance;

    public MainPage(IPlayerProgressRepository progressStore, TimeProvider clock)
    {
        InitializeComponent();
        _progressStore = progressStore;
        _clock = clock;
        MazeView.Drawable = _drawable;

        // Whoever played last is most likely playing again.
        NameEntry.Text = Preferences.Default.Get(NamePreferenceKey, "");

        // The "please type your name" reminder has done its job once they start typing.
        NameEntry.TextChanged += (_, _) => NameHintLabel.Text = "";
    }

    // ---------------- start / quit ----------------

    // Pressing Done on the keyboard just dismisses it; "Let's Play!" starts the game.
    private async void OnNameCompleted(object? sender, EventArgs e)
    {
        NameEntry.Unfocus();
        await NameEntry.HideSoftInputAsync(CancellationToken.None);
    }

    private async void OnPlayClicked(object? sender, EventArgs e)
    {
        // Several awaits happen before the maze appears, so a quick double-tap could otherwise start two games.
        if (_starting) return;
        _starting = true;
        try
        {
            var name = PlayerName.Clean(NameEntry.Text);
            if (name.Length == 0)
            {
                NameHintLabel.Text = "Please type your name first 🙂";
                NameEntry.Focus();
                return;
            }

            NameHintLabel.Text = "";
            NameEntry.Text = name;
            // Unfocus() alone leaves the soft keyboard up on Android, where it would then sit over the maze.
            NameEntry.Unfocus();
            await NameEntry.HideSoftInputAsync(CancellationToken.None);

            var progress = await _progressStore.GetAsync(name);
            var startLevel = 1;
            if (progress is not null && (progress.Completed || progress.CurrentLevel > 1))
            {
                var choice = await AskHowToStartAsync(name, progress);
                if (choice == StartChoice.Cancel) return;

                if (choice == StartChoice.Continue)
                {
                    startLevel = progress.CurrentLevel;
                }
                else
                {
                    // Starting over: the best level stays on the leaderboard, only the place to continue from resets.
                    progress = progress with { CurrentLevel = 1 };
                    await _progressStore.SaveAsync(progress);
                }
            }

            _playerName = name;
            _progress = progress;
            Preferences.Default.Set(NamePreferenceKey, name);

            _session++;
            _run = new MazeRun(startLevel);
            _drawable.Run = _run;
            _drawable.Highlight = null;
            _drawable.View.Reset();

            StartPanel.IsVisible = false;
            LeaderboardPanel.IsVisible = false;
            HeaderPanel.IsVisible = true;
            ButtonBar.IsVisible = true;
            MazeView.IsVisible = true;

            SetStatus("");
            RefreshHud();
        }
        finally
        {
            _starting = false;
        }
    }

    private enum StartChoice { Continue, StartOver, Cancel }

    private async Task<StartChoice> AskHowToStartAsync(string name, PlayerProgress progress)
    {
        if (progress.Completed)
        {
            var again = "Play again from Level 1";
            var answer = await DisplayActionSheetAsync($"Welcome back, {name}! You finished every level 🏆", "Cancel", null, again);
            return answer == again ? StartChoice.StartOver : StartChoice.Cancel;
        }

        var continueText = $"Continue from Level {progress.CurrentLevel}";
        var startOverText = "Start over from Level 1";
        var picked = await DisplayActionSheetAsync($"Welcome back, {name}!", "Cancel", null, continueText, startOverText);
        if (picked == continueText) return StartChoice.Continue;
        if (picked == startOverText) return StartChoice.StartOver;
        return StartChoice.Cancel;
    }

    private void OnQuitAppClicked(object? sender, EventArgs e) => Application.Current?.Quit();

    private async void OnQuitGameClicked(object? sender, EventArgs e)
    {
        var run = _run;
        if (run is null || _asking) return;

        _asking = true;
        bool quit;
        try
        {
            quit = await DisplayAlertAsync("Quit this game?",
                run.Level > 1
                    ? $"Your progress is saved. You can continue from Level {run.Level} next time."
                    : "Back to the main screen?",
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
        _drawable.Run = null;
        _drawable.Highlight = null;

        MazeView.IsVisible = false;
        LeaderboardPanel.IsVisible = false;
        HeaderPanel.IsVisible = false;
        ButtonBar.IsVisible = false;
        StartPanel.IsVisible = true;
        NameHintLabel.Text = "";
    }

    // ---------------- leaderboard ----------------

    private async void OnLeaderboardClicked(object? sender, EventArgs e) =>
        await ShowLeaderboardAsync("🏆 Furthest Level", "");

    private void OnLeaderboardBackClicked(object? sender, EventArgs e) => ShowStartScreen();

    private async Task ShowLeaderboardAsync(string title, string summary)
    {
        var top = await _progressStore.GetTopAsync();

        _session++;
        MazeView.IsVisible = false;
        HeaderPanel.IsVisible = false;
        ButtonBar.IsVisible = false;
        StartPanel.IsVisible = false;

        LeaderboardTitle.Text = title;
        SummaryLabel.Text = summary;
        SummaryLabel.IsVisible = summary.Length > 0;
        LeaderboardLabel.Text = top.Count == 0
            ? "No levels completed yet — you could be the first!"
            : string.Join("\n", top.Select((p, i) =>
                $"{i + 1}. {p.Name} · {p.ProgressText} · {p.When.ToLocalTime():MMM d, h:mm tt}"));
        LeaderboardPanel.IsVisible = true;
    }

    // ---------------- swipes and taps ----------------

    private void OnMazeTouchStart(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0) return;
        _touching = true;
        _multiTouch = false;
        _pinching = false;
        _touchStart = _touchLast = e.Touches[0];
        if (e.Touches.Length >= 2) HandlePinch(e.Touches);
    }

    private void OnMazeTouchDrag(object? sender, TouchEventArgs e)
    {
        if (!_touching || e.Touches.Length == 0) return;
        _touchLast = e.Touches[0];

        if (e.Touches.Length >= 2) HandlePinch(e.Touches);
        else _pinching = false; // one finger lifted: if a second goes down again, that's a fresh pinch
    }

    private void OnMazeTouchCancel(object? sender, EventArgs e)
    {
        _touching = false;
        _multiTouch = false;
        _pinching = false;
    }

    private async void OnMazeTouchEnd(object? sender, TouchEventArgs e)
    {
        if (!_touching) return;
        _touching = false;

        if (_multiTouch)
        {
            // Whatever the fingers did was zooming/panning; the last finger leaving is not a swipe.
            _multiTouch = false;
            _pinching = false;
            return;
        }

        var end = e.Touches.Length > 0 ? e.Touches[0] : _touchLast;
        // Use the end point if it shows a swipe; otherwise the last point seen while dragging.
        var direction =
            SwipeDetector.Detect(end.X - _touchStart.X, end.Y - _touchStart.Y, SwipeDistance) ??
            SwipeDetector.Detect(_touchLast.X - _touchStart.X, _touchLast.Y - _touchStart.Y, SwipeDistance);

        if (direction is { } d) await HandleMoveAsync(d);
        else await HandleTapAsync(end);
    }

    /// <summary>Two fingers on the maze: zoom by how far apart they are, pan by where their midpoint goes.</summary>
    private void HandlePinch(PointF[] touches)
    {
        _multiTouch = true;

        var a = touches[0];
        var b = touches[1];
        var midX = (a.X + b.X) / 2.0;
        var midY = (a.Y + b.Y) / 2.0;
        var distance = Math.Max(Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)), 1);

        if (!_pinching)
        {
            _pinching = true;
            _pinchStartDistance = distance;
            _drawable.View.BeginPinch(midX, midY);
        }
        else
        {
            _drawable.View.UpdatePinch(midX, midY, distance / _pinchStartDistance);
        }

        MazeView.Invalidate();
        UpdateFitButton();
    }

    private void OnFitClicked(object? sender, EventArgs e)
    {
        _drawable.View.Reset();
        MazeView.Invalidate();
        UpdateFitButton();
    }

    private async Task HandleMoveAsync(Direction direction)
    {
        var run = _run;
        if (run is null || run.Phase != RunPhase.Playing || _asking) return;
        var session = _session;

        switch (run.Move(direction))
        {
            case MoveResult.Wall:
                FlashStatus("🧱 Wall!");
                return;

            case MoveResult.BackedUp:
                SetStatus("");
                _drawable.KeepAvatarInView();
                RefreshHud();
                return;

            case MoveResult.Moved:
                SetStatus(run.Attempt.IsDeadEnd ? "💥 Dead end! Swipe back ↩️" : "");
                _drawable.KeepAvatarInView();
                RefreshHud();
                return;

            case MoveResult.Finished:
                RefreshHud();
                if (run.Phase == RunPhase.AllComplete)
                {
                    SetStatus("🏆 You finished every level!");
                    await SaveProgressAsync(run.Level, completed: true);
                    await Task.Delay(SolvedPauseMs);
                    if (session != _session) return;
                    await ShowLeaderboardAsync($"🏆 All {MazeLevels.Count} levels complete!", $"{_playerName} finished every level!");
                    return;
                }

                SetStatus($"🎉 Level {run.Level} complete!");
                await SaveProgressAsync(run.Level + 1, completed: false); // saved now, so quitting mid-pause loses nothing
                await Task.Delay(SolvedPauseMs);
                if (session != _session) return;

                run.Continue();
                _drawable.View.Reset(); // a new maze, shown whole
                SetStatus("");
                RefreshHud();
                return;
        }
    }

    /// <summary>A tap on a square of the trail asks whether to go back to it.</summary>
    private async Task HandleTapAsync(PointF touch)
    {
        var run = _run;
        if (run is null || run.Phase != RunPhase.Playing || _asking) return;
        if (!_drawable.TryGetCellPosition(touch, out var col, out var row)) return;

        // The slack is a fixed size on screen, so on the big mazes (tiny squares) it spans several
        // squares and the nearest trail square wins.
        var maxDistance = Math.Max(0.6, TapSlack / Math.Max(_drawable.CellSize, 1));
        if (!run.Attempt.TryFindBackUpTarget(col, row, maxDistance, out var target)) return;

        // Ring the square being asked about, since on a big maze it's hard to tell which one was meant.
        _asking = true;
        _drawable.Highlight = target;
        MazeView.Invalidate();
        bool goBack;
        try
        {
            goBack = await DisplayAlertAsync("Go back here?",
                "Move back to the ringed square? The trail after it will be erased.", "Go back", "Stay");
        }
        finally
        {
            _asking = false;
            _drawable.Highlight = null;
        }

        if (goBack && _run == run && run.BackUpTo(target.Col, target.Row))
        {
            SetStatus("");
            _drawable.KeepAvatarInView();
        }
        RefreshHud();
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        var run = _run;
        if (run is null || !run.CanRetry || _asking) return;

        // A long trail is a lot of swipes, so don't let a stray tap throw it away.
        _asking = true;
        bool restart;
        try
        {
            restart = await DisplayAlertAsync("Start this maze over?", "Your trail will be erased.", "Start over", "Keep going");
        }
        finally
        {
            _asking = false;
        }

        if (restart && _run == run && run.Retry())
        {
            SetStatus("");
            _drawable.KeepAvatarInView(); // back at the start square
        }
        RefreshHud();
    }

    // ---------------- screen updates ----------------

    /// <summary>Redraws the maze and updates the level line and the Retry button.</summary>
    private void RefreshHud()
    {
        if (_run is not { } run) return;

        LevelLabel.Text = $"Level {run.Level} · {run.Size} × {run.Size}";
        RetryButton.IsEnabled = run.CanRetry;
        RetryButton.Opacity = run.CanRetry ? 1 : 0.4;
        UpdateFitButton();

        MazeView.Invalidate();
    }

    private void UpdateFitButton()
    {
        var zoomed = _drawable.View.IsZoomed;
        FitButton.IsEnabled = zoomed;
        FitButton.Opacity = zoomed ? 1 : 0.4;
    }

    /// <summary>Sets the message that stays until the next one replaces it.</summary>
    private void SetStatus(string text)
    {
        _baseStatus = text;
        _statusToken++; // a newer message cancels the pending clear of an older flash
        StatusLabel.Text = text;
    }

    /// <summary>A brief message that then goes back to whatever was showing before.</summary>
    private async void FlashStatus(string text)
    {
        var token = ++_statusToken;
        StatusLabel.Text = text;
        await Task.Delay(800);
        if (token == _statusToken) StatusLabel.Text = _baseStatus;
    }

    private async Task SaveProgressAsync(int level, bool completed)
    {
        var progress = PlayerProgress.Reached(_playerName, level, completed, _clock.GetUtcNow(), _progress);
        await _progressStore.SaveAsync(progress);
        _progress = progress;
    }
}
