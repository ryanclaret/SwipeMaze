using SwipeMaze.Core;

namespace SwipeMaze.Tests;

public class ProgressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swipemaze-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "progress.tsv");

    public ProgressTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DateTimeOffset At(int secs) => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + secs);

    private static PlayerProgress Player(string name, int best, int? current = null, bool completed = false, int secs = 0) =>
        new(name, best, current ?? best, completed, At(secs));

    // ---------- the progress record ----------

    [Fact]
    public void Reaching_a_new_level_raises_the_best_and_moves_the_current_level_on()
    {
        var first = PlayerProgress.Reached("Ana", level: 2, completed: false, At(1), previous: null);
        Assert.Equal((2, 2), (first.BestLevel, first.CurrentLevel));

        var next = PlayerProgress.Reached("Ana", level: 3, completed: false, At(2), previous: first);
        Assert.Equal((3, 3), (next.BestLevel, next.CurrentLevel));
        Assert.Equal(At(2), next.When);
    }

    [Fact]
    public void Starting_over_keeps_the_best_level_on_the_leaderboard()
    {
        var before = Player("Ana", best: 12);

        var afterRestart = PlayerProgress.Reached("Ana", level: 1, completed: false, At(5), before);

        Assert.Equal(12, afterRestart.BestLevel);
        Assert.Equal(1, afterRestart.CurrentLevel);
    }

    [Fact]
    public void Finishing_everything_is_remembered_even_after_starting_over()
    {
        var done = PlayerProgress.Reached("Ana", MazeLevels.Count, completed: true, At(1), previous: null);
        Assert.True(done.Completed);

        var again = PlayerProgress.Reached("Ana", level: 4, completed: false, At(2), done);

        Assert.True(again.Completed);
        Assert.Equal(MazeLevels.Count, again.BestLevel);
        Assert.Equal(4, again.CurrentLevel);
    }

    [Fact]
    public void A_higher_level_ranks_higher_and_finishing_everything_beats_them_all()
    {
        Assert.True(Player("a", 5).Rank > Player("b", 4).Rank);
        Assert.True(Player("a", 100, completed: true).Rank > Player("b", 100).Rank);
        Assert.Equal("Level 5", Player("a", 5).ProgressText);
        Assert.Equal("All 100 levels!", Player("a", 100, completed: true).ProgressText);
    }

    // ---------- the saved file ----------

    [Fact]
    public async Task A_player_with_no_saved_progress_is_null()
    {
        Assert.Null(await new FilePlayerProgressRepository(FilePath).GetAsync("Ana"));
    }

    [Fact]
    public async Task Saved_progress_comes_back_even_after_a_restart()
    {
        await new FilePlayerProgressRepository(FilePath).SaveAsync(Player("Ana Maria", best: 12, current: 9, secs: 42));

        var loaded = await new FilePlayerProgressRepository(FilePath).GetAsync("Ana Maria");

        Assert.NotNull(loaded);
        Assert.Equal((12, 9, false), (loaded!.BestLevel, loaded.CurrentLevel, loaded.Completed));
        Assert.Equal(At(42), loaded.When);
    }

    [Fact]
    public async Task Names_match_without_regard_to_case_and_saving_replaces_instead_of_duplicating()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        await repo.SaveAsync(Player("ana", best: 3));
        await repo.SaveAsync(Player("ANA", best: 5));

        var all = await repo.GetTopAsync();

        var only = Assert.Single(all);
        Assert.Equal(5, only.BestLevel);
        Assert.Equal(5, (await repo.GetAsync("Ana"))!.BestLevel);
    }

    [Fact]
    public async Task All_players_are_listed_most_recently_active_first_and_not_capped_at_ten()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        for (var i = 1; i <= 15; i++) await repo.SaveAsync(Player($"p{i}", best: 1, secs: i));

        var all = await repo.GetAllAsync();

        Assert.Equal(15, all.Count);
        Assert.Equal("p15", all[0].Name);
        Assert.Equal("p1", all[^1].Name);
    }

    [Fact]
    public async Task Listing_everyone_with_no_file_is_empty()
    {
        Assert.Empty(await new FilePlayerProgressRepository(FilePath).GetAllAsync());
    }

    [Fact]
    public async Task Deleting_a_player_removes_only_them_ignoring_case_and_persists()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        await repo.SaveAsync(Player("Ana", best: 5));
        await repo.SaveAsync(Player("Ben", best: 7));

        Assert.True(await repo.DeleteAsync("ANA"));

        var fresh = new FilePlayerProgressRepository(FilePath);
        Assert.Null(await fresh.GetAsync("Ana"));
        Assert.Equal(7, (await fresh.GetAsync("Ben"))!.BestLevel);
        Assert.Equal(new[] { "Ben" }, (await fresh.GetTopAsync()).Select(p => p.Name));
    }

    [Fact]
    public async Task Deleting_someone_who_is_not_there_changes_nothing()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        Assert.False(await repo.DeleteAsync("Nobody"));          // no file at all

        await repo.SaveAsync(Player("Ana", best: 5));
        Assert.False(await repo.DeleteAsync("Nobody"));          // file exists, name doesn't

        Assert.Equal(5, (await repo.GetAsync("Ana"))!.BestLevel);
    }

    [Fact]
    public async Task A_removed_player_can_start_fresh_with_the_same_name()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        await repo.SaveAsync(Player("Ana", best: 30));
        await repo.DeleteAsync("Ana");

        Assert.Null(await repo.GetAsync("Ana"));
        await repo.SaveAsync(Player("Ana", best: 2));
        Assert.Equal(2, (await repo.GetAsync("Ana"))!.BestLevel);
    }

    [Fact]
    public async Task A_completed_run_round_trips()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        await repo.SaveAsync(Player("Champ", best: 100, completed: true));

        var loaded = await new FilePlayerProgressRepository(FilePath).GetAsync("Champ");

        Assert.True(loaded!.Completed);
    }

    [Fact]
    public async Task The_leaderboard_is_furthest_first()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        await repo.SaveAsync(Player("low", 3));
        await repo.SaveAsync(Player("high", 40));
        await repo.SaveAsync(Player("mid", 12));
        await repo.SaveAsync(Player("champ", 100, completed: true));

        var top = await repo.GetTopAsync();

        Assert.Equal(new[] { "champ", "high", "mid", "low" }, top.Select(p => p.Name));
    }

    [Fact]
    public async Task Of_two_equal_levels_the_one_who_got_there_first_ranks_higher()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        await repo.SaveAsync(Player("later", 8, secs: 100));
        await repo.SaveAsync(Player("earlier", 8, secs: 5));

        Assert.Equal(new[] { "earlier", "later" }, (await repo.GetTopAsync()).Select(p => p.Name));
    }

    [Fact]
    public async Task The_board_shows_ten_but_everyone_keeps_their_progress()
    {
        var repo = new FilePlayerProgressRepository(FilePath);
        for (var i = 1; i <= 15; i++) await repo.SaveAsync(Player($"p{i}", best: i, secs: i));

        var top = await repo.GetTopAsync();

        Assert.Equal(Leaderboard.MaxEntries, top.Count);
        Assert.Equal("p15", top[0].Name);
        Assert.DoesNotContain(top, p => p.Name == "p1");
        Assert.NotNull(await repo.GetAsync("p1")); // off the board, but still can continue
    }

    [Fact]
    public async Task Garbled_lines_are_skipped_instead_of_losing_everyone()
    {
        File.WriteAllLines(FilePath, new[]
        {
            "5\t4\t0\t1700000000\tGood",
            "not a record at all",
            "500\t1\t0\t1700000000\tBad level",
            "3\t0\t0\t1700000000\tBad current",
            "3\t3\t0\tNaN\tBad time",
            "3\t3\t0\t1700000000",            // no name
            "7\t7\t1\t1700000000\tAlso good",
        });

        var top = await new FilePlayerProgressRepository(FilePath).GetTopAsync();

        Assert.Equal(new[] { "Also good", "Good" }, top.Select(p => p.Name));
    }

    // ---------- names ----------

    [Theory]
    [InlineData("  Ana  ", "Ana")]
    [InlineData("Ana    Maria", "Ana Maria")]
    [InlineData("A\tB\nC", "ABC")]   // tabs and newlines would corrupt the file
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Player_names_are_cleaned(string? raw, string expected) =>
        Assert.Equal(expected, PlayerName.Clean(raw));

    [Fact]
    public void Long_names_are_cut_to_twenty_characters() =>
        Assert.Equal(PlayerName.MaxLength, PlayerName.Clean(new string('x', 50)).Length);
}
