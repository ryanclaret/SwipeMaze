using System.Globalization;

namespace SwipeMaze.Core;

/// <summary>One player's progress, which is also their leaderboard line.</summary>
/// <param name="BestLevel">The furthest level they have ever reached. This is what the leaderboard ranks.</param>
/// <param name="CurrentLevel">Where they continue from. Equals <paramref name="BestLevel"/> unless they started over.</param>
/// <param name="Completed">True once they have solved every level.</param>
/// <param name="When">When they last reached a new level.</param>
public sealed record PlayerProgress(string Name, int BestLevel, int CurrentLevel, bool Completed, DateTimeOffset When)
{
    /// <summary>Orders players: a higher level is bigger, and finishing everything beats all.</summary>
    public int Rank => BestLevel + (Completed ? 1 : 0);

    public string ProgressText => Completed ? $"All {MazeLevels.Count} levels!" : $"Level {BestLevel}";

    /// <summary>
    /// The record after reaching <paramref name="level"/> (starting it, having just solved the one
    /// before). Keeps the best level and the finished-everything flag from <paramref name="previous"/>.
    /// </summary>
    public static PlayerProgress Reached(string name, int level, bool completed, DateTimeOffset when, PlayerProgress? previous) =>
        new(name,
            Math.Max(previous?.BestLevel ?? 1, level),
            level,
            completed || previous?.Completed == true,
            when);
}

public interface IPlayerProgressRepository
{
    /// <summary>This player's saved progress, or null if they haven't got any. Names match regardless of case.</summary>
    Task<PlayerProgress?> GetAsync(string name);

    /// <summary>Saves the progress, replacing that player's earlier record.</summary>
    Task SaveAsync(PlayerProgress progress);

    /// <summary>The leaderboard: furthest first, at most <see cref="Leaderboard.MaxEntries"/>.</summary>
    Task<IReadOnlyList<PlayerProgress>> GetTopAsync();

    /// <summary>Everyone who has played, most recently active first (for picking an existing name).</summary>
    Task<IReadOnlyList<PlayerProgress>> GetAllAsync();

    /// <summary>Removes this player and their progress. Returns false if there was no such player.</summary>
    Task<bool> DeleteAsync(string name);

    /// <summary>Saves the maze this player is part-way through (replacing any earlier one).</summary>
    Task SaveGameAsync(string name, SavedGame game);

    /// <summary>The maze this player is part-way through, or null if none (or it can't be read).</summary>
    Task<SavedGame?> LoadGameAsync(string name);

    /// <summary>Forgets the player's game in progress, if any.</summary>
    Task ClearGameAsync(string name);
}

public static class Leaderboard
{
    public const int MaxEntries = 10;

    /// <summary>Furthest first; of two equal results, whoever got there first ranks higher.</summary>
    public static IReadOnlyList<PlayerProgress> Rank(IEnumerable<PlayerProgress> players) =>
        players.OrderByDescending(p => p.Rank).ThenBy(p => p.When).Take(MaxEntries).ToList();
}

/// <summary>
/// Progress for every player, kept in one small tab-separated text file (best level, current level,
/// completed, unix time, name — name last because it's the only free text). Plain text on purpose: no
/// database, no reflection serializer to go wrong under the Android linker. Unreadable lines are
/// skipped, not fatal.
/// </summary>
public sealed class FilePlayerProgressRepository(string path) : IPlayerProgressRepository
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<PlayerProgress?> GetAsync(string name)
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadAllAsync()).FirstOrDefault(p => SameName(p.Name, name));
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(PlayerProgress progress)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAllAsync();
            all.RemoveAll(p => SameName(p.Name, progress.Name));
            all.Add(progress);

            // Write-then-replace so a crash mid-write can't leave a half-written file.
            var temp = path + ".tmp";
            await File.WriteAllLinesAsync(temp, all.Select(Format));
            File.Move(temp, path, overwrite: true);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<PlayerProgress>> GetTopAsync()
    {
        await _lock.WaitAsync();
        try { return Leaderboard.Rank(await ReadAllAsync()); }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<PlayerProgress>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try { return (await ReadAllAsync()).OrderByDescending(p => p.When).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        finally { _lock.Release(); }
    }

    public async Task<bool> DeleteAsync(string name)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAllAsync();
            var removed = all.RemoveAll(p => SameName(p.Name, name)) > 0;
            if (removed)
            {
                var temp = path + ".tmp";
                await File.WriteAllLinesAsync(temp, all.Select(Format));
                File.Move(temp, path, overwrite: true);
            }

            DeleteGameFile(name);
            return removed;
        }
        finally { _lock.Release(); }
    }

    // Each player's game in progress is a small file of its own in a folder beside the progress file,
    // named by a hash of the (case-insensitive) player name so any name makes a safe file name.
    private string GamesDirectory => path + ".games";

    private string GamePath(string name) =>
        Path.Combine(GamesDirectory, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToUpperInvariant())))[..24] + ".txt");

    public async Task SaveGameAsync(string name, SavedGame game)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(GamesDirectory);
            var file = GamePath(name);
            var temp = file + ".tmp";
            await File.WriteAllTextAsync(temp, game.Serialize());
            File.Move(temp, file, overwrite: true);
        }
        finally { _lock.Release(); }
    }

    public async Task<SavedGame?> LoadGameAsync(string name)
    {
        await _lock.WaitAsync();
        try
        {
            var file = GamePath(name);
            return File.Exists(file) ? SavedGame.Parse(await File.ReadAllTextAsync(file)) : null;
        }
        finally { _lock.Release(); }
    }

    public async Task ClearGameAsync(string name)
    {
        await _lock.WaitAsync();
        try { DeleteGameFile(name); }
        finally { _lock.Release(); }
    }

    /// <summary>File.Delete ignores a missing file but throws if the whole folder is missing (nobody has saved a game yet).</summary>
    private void DeleteGameFile(string name)
    {
        var file = GamePath(name);
        if (File.Exists(file)) File.Delete(file);
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private async Task<List<PlayerProgress>> ReadAllAsync()
    {
        var players = new List<PlayerProgress>();
        if (!File.Exists(path)) return players;

        foreach (var line in await File.ReadAllLinesAsync(path))
            if (TryParse(line, out var player)) players.Add(player);
        return players;
    }

    private static string Format(PlayerProgress p) =>
        string.Join('\t', p.BestLevel, p.CurrentLevel, p.Completed ? 1 : 0, p.When.ToUnixTimeSeconds(), p.Name);

    private static bool TryParse(string line, out PlayerProgress player)
    {
        player = null!;
        var parts = line.Split('\t', 5);
        if (parts.Length != 5 || parts[4].Length == 0) return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var best) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var current) ||
            !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            return false;

        if (best < 1 || best > MazeLevels.Count || current < 1 || current > MazeLevels.Count) return false;

        player = new PlayerProgress(parts[4], best, current, parts[2] == "1", DateTimeOffset.FromUnixTimeSeconds(unix));
        return true;
    }
}

public static class PlayerName
{
    public const int MaxLength = 20;

    /// <summary>Trims, collapses runs of whitespace, drops control characters (tabs and newlines
    /// included, which the progress file relies on) and caps the length. Returns "" if nothing
    /// usable is left — callers should treat that as "no name".</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var chars = raw.Where(c => !char.IsControl(c)).ToArray();
        var collapsed = string.Join(' ', new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return collapsed.Length <= MaxLength ? collapsed : collapsed[..MaxLength].TrimEnd();
    }
}
