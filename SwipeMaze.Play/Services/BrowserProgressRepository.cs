using SwipeMaze.Core;

namespace SwipeMaze.Play.Services;

/// <summary>
/// Everyone's progress and saved mazes, kept in the browser's local storage on this device: one entry for
/// the player list (one line per player, the same text the Android app keeps in its file) and one entry
/// per player for the maze they are part-way through. Nothing leaves the device.
/// </summary>
public sealed class BrowserProgressRepository(BrowserStore store) : IPlayerProgressRepository
{
    private const string ProgressKey = "swipemaze.progress";
    private const string GamePrefix = "swipemaze.game.";

    // Names match without regard to case, so the saved game's key is made from the upper-cased name.
    private static string GameKey(string name) => GamePrefix + Uri.EscapeDataString(name.ToUpperInvariant());

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private List<PlayerProgress> ReadAll()
    {
        var players = new List<PlayerProgress>();
        var text = store.Get(ProgressKey);
        if (string.IsNullOrEmpty(text)) return players;

        foreach (var line in text.Split('\n'))
            if (PlayerProgressLine.TryParse(line.TrimEnd('\r'), out var player)) players.Add(player);
        return players;
    }

    private void WriteAll(List<PlayerProgress> players) =>
        store.Set(ProgressKey, string.Join('\n', players.Select(PlayerProgressLine.Format)));

    public Task<PlayerProgress?> GetAsync(string name) =>
        Task.FromResult(ReadAll().FirstOrDefault(p => SameName(p.Name, name)));

    public Task SaveAsync(PlayerProgress progress)
    {
        var all = ReadAll();
        all.RemoveAll(p => SameName(p.Name, progress.Name));
        all.Add(progress);
        WriteAll(all);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PlayerProgress>> GetTopAsync() =>
        Task.FromResult(Leaderboard.Rank(ReadAll()));

    public Task<IReadOnlyList<PlayerProgress>> GetAllAsync() =>
        Task.FromResult<IReadOnlyList<PlayerProgress>>(
            ReadAll().OrderByDescending(p => p.When).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<bool> DeleteAsync(string name)
    {
        var all = ReadAll();
        var removed = all.RemoveAll(p => SameName(p.Name, name)) > 0;
        if (removed) WriteAll(all);
        store.Remove(GameKey(name));
        return Task.FromResult(removed);
    }

    public Task SaveGameAsync(string name, SavedGame game)
    {
        store.Set(GameKey(name), game.Serialize());
        return Task.CompletedTask;
    }

    public Task<SavedGame?> LoadGameAsync(string name) =>
        Task.FromResult(SavedGame.Parse(store.Get(GameKey(name))));

    public Task ClearGameAsync(string name)
    {
        store.Remove(GameKey(name));
        return Task.CompletedTask;
    }
}
