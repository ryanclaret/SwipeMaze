using System.Diagnostics;
using SwipeMaze.Core;

namespace SwipeMaze;

/// <summary>Single page, no MVVM: shows the start screen, the maze, and the leaderboard, and turns
/// swipes and taps into moves on a <see cref="MazeRun"/>. All the rules live in SwipeMaze.Core.</summary>
public partial class MainPage : ContentPage
{
    private const string NamePreferenceKey = "player_name";
    private const string CenterPreferenceKey = "center_avatar";

    /// <summary>How far (in device-independent units) a finger must travel to count as a swipe.</summary>
    private const double SwipeDistance = 20;

    /// <summary>How far from a trail square a tap can land and still mean that square.</summary>
    private const double TapSlack = 12;

    /// <summary>Pause between squares while the avatar runs along a corridor, so the run can be seen.</summary>
    private const int StepDelayMs = 12;

    private readonly IPlayerProgressRepository _progressStore;
    private readonly TimeProvider _clock;
    private readonly ISoundPlayer _sounds;
    private readonly MazeDrawable _drawable = new();

    private MazeRun? _run;
    private string _playerName = "";
    private PlayerProgress? _progress;
    private bool _starting;
    private bool _asking;
    private bool _animating; // a swipe's run is being shown; further swipes wait

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

    public MainPage(IPlayerProgressRepository progressStore, TimeProvider clock, ISoundPlayer sounds)
    {
        InitializeComponent();
        _progressStore = progressStore;
        _clock = clock;
        _sounds = sounds;
        MazeView.Drawable = _drawable;

        // Whoever played last is most likely playing again.
        NameEntry.Text = Preferences.Default.Get(NamePreferenceKey, "");

        // The "please type your name" reminder has done its job once they start typing.
        NameEntry.TextChanged += (_, _) => NameHintLabel.Text = "";

        // Whether zooming keeps the avatar in the middle: on unless they turned it off.
        _drawable.CenterAvatar = Preferences.Default.Get(CenterPreferenceKey, true);
        UpdateCenterButton();
    }

    // ---------------- dialogs ----------------

    private enum DialogStyle { Primary, Danger, Neutral }

    private TaskCompletionSource<int>? _dialog;

    // Touches stop at the overlay; nothing to do (a stray tap outside the card must not dismiss it).
    private void OnDialogBackdropTapped(object? sender, TappedEventArgs e) { }

    /// <summary>Android's Back button answers the open dialog as "cancel" instead of leaving the game.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_dialog is null) return base.OnBackButtonPressed();
        CloseDialog(-1);
        return true;
    }

    private void CloseDialog(int result)
    {
        var dialog = _dialog;
        if (dialog is null) return;
        _dialog = null;
        DialogOverlay.IsVisible = false;
        DialogButtons.Children.Clear();
        dialog.TrySetResult(result);
    }

    /// <summary>
    /// Shows the game's own dialog and returns the index of the button tapped (-1 if dismissed with the
    /// Back button, or if another dialog is already open). With <paramref name="atBottom"/> the card sits
    /// at the foot of the screen over a lighter shade, so the maze above it stays visible.
    /// </summary>
    private async Task<int> ShowDialogAsync(string title, string message, bool atBottom,
        params (string Text, DialogStyle Style)[] buttons)
    {
        if (_dialog is not null) return -1;
        var dialog = _dialog = new TaskCompletionSource<int>();

        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogMessage.IsVisible = message.Length > 0;

        DialogButtons.Children.Clear();
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var (text, style) = buttons[i];
            var button = new Button
            {
                Text = text,
                FontSize = 20,
                HeightRequest = 52,
                CornerRadius = 16,
                Padding = new Thickness(8, 0),
                BackgroundColor = style switch
                {
                    DialogStyle.Primary => Color.FromArgb("#8BC34A"),
                    DialogStyle.Danger => Color.FromArgb("#C62828"),
                    _ => Colors.Transparent,
                },
                TextColor = style == DialogStyle.Neutral ? Color.FromArgb("#8D6E63") : Colors.White,
                BorderColor = style == DialogStyle.Neutral ? Color.FromArgb("#8D6E63") : Colors.Transparent,
                BorderWidth = style == DialogStyle.Neutral ? 1.5 : 0,
            };
            button.Clicked += (_, _) => CloseDialog(index);
            DialogButtons.Children.Add(button);
        }

        // A long list (many players) scrolls instead of running off the screen.
        DialogScroll.MaximumHeightRequest = Math.Max(160, Height * (atBottom ? 0.3 : 0.5));
        DialogCard.VerticalOptions = atBottom ? LayoutOptions.End : LayoutOptions.Center;
        DialogOverlay.BackgroundColor = Color.FromArgb(atBottom ? "#33000000" : "#99000000");
        DialogOverlay.IsVisible = true;

        return await dialog.Task;
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

    // ---------------- start / quit ----------------

    // Pressing Done on the keyboard just dismisses it; "Let's Play!" starts the game.
    private async void OnNameCompleted(object? sender, EventArgs e)
    {
        NameEntry.Unfocus();
        await NameEntry.HideSoftInputAsync(CancellationToken.None);
    }

    /// <summary>Shows everyone who has played and lets the user pick one. Null if cancelled or nobody has played yet.</summary>
    private async Task<PlayerProgress?> PickPlayerAsync(string title, string cancel)
    {
        var players = await _progressStore.GetAllAsync();
        if (players.Count == 0)
        {
            NameHintLabel.Text = "No players yet — type your name to begin 🙂";
            return null;
        }

        // Same name twice can't happen (names are unique ignoring case), so the label alone identifies the player.
        var labels = players.Select(p => $"{p.Name} · {p.ProgressText}").ToArray();
        var index = await ChooseAsync(title, "", labels, cancel);
        return index < 0 ? null : players[index];
    }

    private async void OnChoosePlayerClicked(object? sender, EventArgs e)
    {
        if (_starting || _asking) return;
        _asking = true;
        try
        {
            NameEntry.Unfocus();
            await NameEntry.HideSoftInputAsync(CancellationToken.None);

            var player = await PickPlayerAsync("Who's playing?", "Cancel");
            if (player is null) return;

            NameEntry.Text = player.Name;
            NameHintLabel.Text = "";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _asking = false;
        }
    }

    private async void OnRemovePlayerClicked(object? sender, EventArgs e)
    {
        if (_starting || _asking) return;
        _asking = true;
        try
        {
            NameEntry.Unfocus();
            await NameEntry.HideSoftInputAsync(CancellationToken.None);

            var player = await PickPlayerAsync("Remove which player?", "Cancel");
            if (player is null) return;

            var sure = await ConfirmAsync($"Remove {player.Name}?",
                $"This deletes {player.Name}'s saved progress ({player.ProgressText}) and takes them off the leaderboard. This can't be undone.",
                "Remove", "Keep", danger: true);
            if (!sure) return;

            await _progressStore.DeleteAsync(player.Name);

            // Don't leave the removed name sitting in the box, or remembered for next launch.
            if (string.Equals(PlayerName.Clean(NameEntry.Text), player.Name, StringComparison.OrdinalIgnoreCase))
                NameEntry.Text = "";
            if (string.Equals(Preferences.Default.Get(NamePreferenceKey, ""), player.Name, StringComparison.OrdinalIgnoreCase))
                Preferences.Default.Remove(NamePreferenceKey);

            NameHintLabel.Text = $"{player.Name} was removed.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _asking = false;
        }
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

            // The maze they were part-way through, with the avatar where they left it. Only usable if it
            // is for the level they would continue from; anything else (or damage) is ignored.
            MazeRun? resumed = null;
            if (progress is not null && !progress.Completed)
            {
                var saved = await _progressStore.LoadGameAsync(name);
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
                    await _progressStore.SaveAsync(progress);
                    await _progressStore.ClearGameAsync(name);
                    resumed = null;
                }
            }

            _playerName = name;
            _progress = progress;
            Preferences.Default.Set(NamePreferenceKey, name);

            _session++;
            _run = resumed ?? new MazeRun(startLevel);
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

    private void OnQuitAppClicked(object? sender, EventArgs e) => Application.Current?.Quit();

    private async void OnQuitGameClicked(object? sender, EventArgs e)
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

    /// <summary>Turns "keep the avatar centered when zoomed in" on or off, and remembers the choice.</summary>
    private void OnCenterClicked(object? sender, EventArgs e)
    {
        _drawable.CenterAvatar = !_drawable.CenterAvatar;
        Preferences.Default.Set(CenterPreferenceKey, _drawable.CenterAvatar);
        UpdateCenterButton();

        FlashStatus(_drawable.CenterAvatar ? "🎯 Avatar stays centered" : "🎯 Avatar not centered");
        if (_drawable.CenterAvatar) _drawable.FollowAvatar(); // take effect straight away
        MazeView.Invalidate();
    }

    /// <summary>Filled green when centering is on, just an outline when it is off.</summary>
    private void UpdateCenterButton()
    {
        var green = Color.FromArgb("#2E7834");
        var on = _drawable.CenterAvatar;
        CenterButton.BackgroundColor = on ? green : Colors.Transparent;
        CenterButton.TextColor = on ? Colors.White : green;
        CenterButton.BorderColor = green;
    }

    /// <summary>
    /// One swipe. The avatar runs along the corridor until there's a choice to make (a fork), hits a dead
    /// end, or reaches the finish — and going back runs back along the trail the same way. The run is
    /// shown square by square, with a sound as it sets off: a buzz if it's blocked by a wall.
    /// </summary>
    private async Task HandleMoveAsync(Direction direction)
    {
        var run = _run;
        if (run is null || run.Phase != RunPhase.Playing || _asking || _animating) return;
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
                    _sounds.Play(SoundFor(result, direction));
                    first = false;
                }
                last = result;

                if (result == MoveResult.Wall)
                {
                    FlashStatus("🧱 Wall!");
                    return;
                }

                _drawable.FollowAvatar();
                MazeView.Invalidate();
                if (result != MoveResult.Finished) await Task.Delay(StepDelayMs);
                if (session != _session) return;
            }

            if (last == MoveResult.Finished)
            {
                await CelebrateAsync(run, session);
                return;
            }

            SetStatus(run.Attempt.IsDeadEnd ? "💥 Dead end! Swipe back ↩️" : "");
            RefreshHud();
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
        RefreshHud();
        var allDone = run.Phase == RunPhase.AllComplete;

        SetStatus(allDone ? "🏆 You finished every level!" : $"🎉 Level {run.Level} complete!");
        // Saved now, so quitting during the fireworks loses nothing.
        await SaveProgressAsync(allDone ? run.Level : run.Level + 1, completed: allDone);
        await _progressStore.ClearGameAsync(_playerName); // solved: nothing left to resume

        await PlayFireworksAsync(session);
        if (session != _session) return;

        if (allDone)
        {
            await ShowLeaderboardAsync($"🏆 All {MazeLevels.Count} levels complete!", $"{_playerName} finished every level!");
            return;
        }

        run.Continue();
        _drawable.View.Reset(); // a new maze, shown whole
        SetStatus("");
        RefreshHud();
    }

    /// <summary>The winning jingle and a few bursts of fireworks over the maze, with a pop for each burst.</summary>
    private async Task PlayFireworksAsync(int session)
    {
        var fireworks = _drawable.Fireworks;
        fireworks.Start(MazeView.Width, MazeView.Height, bursts: 6, span: 1.6);
        _sounds.Play(Sound.Win);

        var clock = Stopwatch.StartNew();
        var previous = 0.0;
        while (fireworks.IsActive && session == _session)
        {
            var now = clock.Elapsed.TotalSeconds;
            var bursts = fireworks.Update(now - previous);
            previous = now;

            for (var i = 0; i < bursts; i++) _sounds.Play(Sound.Pop);
            MazeView.Invalidate();
            await Task.Delay(16);
        }

        fireworks.Clear();
        MazeView.Invalidate();
    }

    /// <summary>A tap on a square of the trail asks whether to go back to it.</summary>
    private async Task HandleTapAsync(PointF touch)
    {
        var run = _run;
        if (run is null || run.Phase != RunPhase.Playing || _asking || _animating) return;
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
            // At the foot of the screen, so the ringed square being asked about stays in view.
            goBack = await ConfirmAsync("Go back here?",
                "Move back to the ringed square? The trail after it will be erased.", "Go back", "Stay", atBottom: true);
        }
        finally
        {
            _asking = false;
            _drawable.Highlight = null;
        }

        if (goBack && _run == run && run.BackUpTo(target.Col, target.Row))
        {
            _sounds.Play(Sound.Back);
            SetStatus("");
            _drawable.FollowAvatar();
            await SaveGameAsync(run, _session);
        }
        RefreshHud();
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
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
            _sounds.Play(Sound.Back);
            SetStatus("");
            _drawable.FollowAvatar(); // back at the start square
            await SaveGameAsync(run, _session);
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
            await _progressStore.SaveGameAsync(_playerName, SavedGame.From(run));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task SaveProgressAsync(int level, bool completed)
    {
        var progress = PlayerProgress.Reached(_playerName, level, completed, _clock.GetUtcNow(), _progress);
        await _progressStore.SaveAsync(progress);
        _progress = progress;
    }
}
