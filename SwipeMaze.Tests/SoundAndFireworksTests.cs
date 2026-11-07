using SwipeMaze.Core;

namespace SwipeMaze.Tests;

public class SoundSynthTests
{
    public static IEnumerable<object[]> AllSounds() => Enum.GetValues<Sound>().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(AllSounds))]
    public void Every_sound_is_audible_and_never_clips(Sound sound)
    {
        var samples = SoundSynth.Create(sound);

        Assert.NotEmpty(samples);
        var peak = samples.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, 8000, 32000); // loud enough to hear, with headroom so it can't distort
    }

    [Theory]
    [MemberData(nameof(AllSounds))]
    public void Every_sound_starts_and_ends_at_silence_so_it_does_not_click(Sound sound)
    {
        var samples = SoundSynth.Create(sound);

        Assert.InRange(Math.Abs((int)samples[0]), 0, 600);
        Assert.InRange(Math.Abs((int)samples[^1]), 0, 600);
    }

    [Theory]
    [MemberData(nameof(AllSounds))]
    public void The_same_sound_is_always_the_same_clip(Sound sound) =>
        Assert.Equal(SoundSynth.Create(sound), SoundSynth.Create(sound));

    [Fact]
    public void The_lengths_suit_what_they_are_for()
    {
        Assert.InRange(SoundSynth.Duration(SoundSynth.Create(Sound.SwipeDown)).TotalSeconds, 0.05, 0.25); // quick: swipes come fast
        Assert.InRange(SoundSynth.Duration(SoundSynth.Create(Sound.Back)).TotalSeconds, 0.05, 0.25);
        Assert.InRange(SoundSynth.Duration(SoundSynth.Create(Sound.Wall)).TotalSeconds, 0.15, 0.5);
        Assert.InRange(SoundSynth.Duration(SoundSynth.Create(Sound.Win)).TotalSeconds, 1.0, 2.0);
        Assert.InRange(SoundSynth.Duration(SoundSynth.Create(Sound.Pop)).TotalSeconds, 0.2, 0.6);
    }

    [Fact]
    public void The_four_swipe_directions_sound_different()
    {
        var clips = new[] { Sound.SwipeUp, Sound.SwipeDown, Sound.SwipeLeft, Sound.SwipeRight }
            .Select(SoundSynth.Create).ToList();

        for (var i = 0; i < clips.Count; i++)
            for (var j = i + 1; j < clips.Count; j++)
                Assert.NotEqual(clips[i], clips[j]);
    }

    [Fact]
    public void The_wall_buzz_is_lower_pitched_than_a_swipe()
    {
        // Zero crossings per second is a stand-in for pitch.
        double Pitch(short[] s)
        {
            var crossings = 0;
            for (var i = 1; i < s.Length; i++) if ((s[i - 1] < 0) != (s[i] < 0)) crossings++;
            return crossings / 2.0 / SoundSynth.Duration(s).TotalSeconds;
        }

        Assert.True(Pitch(SoundSynth.Create(Sound.Wall)) < Pitch(SoundSynth.Create(Sound.SwipeUp)) / 2);
    }
}

public class FireworksTests
{
    [Fact]
    public void A_new_show_is_not_active_until_started()
    {
        var fireworks = new Fireworks(new Random(1));

        Assert.False(fireworks.IsActive);
        Assert.Empty(fireworks.Snapshot());
    }

    [Fact]
    public void The_first_burst_goes_off_straight_away()
    {
        var fireworks = new Fireworks(new Random(1));
        fireworks.Start(400, 700);

        var launched = fireworks.Update(0.01);

        Assert.Equal(1, launched);
        Assert.True(fireworks.Snapshot().Count >= 30);
    }

    [Fact]
    public void Exactly_the_requested_number_of_bursts_go_off()
    {
        var fireworks = new Fireworks(new Random(2));
        fireworks.Start(400, 700, bursts: 7, span: 2.0);

        var launched = 0;
        for (var t = 0.0; t < 6; t += 1 / 60.0) launched += fireworks.Update(1 / 60.0);

        Assert.Equal(7, launched);
    }

    [Fact]
    public void A_burst_starts_inside_the_view_and_the_whole_show_ends_by_itself()
    {
        var fireworks = new Fireworks(new Random(3));
        fireworks.Start(400, 700);
        fireworks.Update(0.01);

        Assert.All(fireworks.Snapshot(), p =>
        {
            Assert.InRange(p.X, 0, 400);
            Assert.InRange(p.Y, 0, 700);
            Assert.InRange(p.ColorIndex, 0, Fireworks.ColorCount - 1);
        });

        for (var t = 0.0; t < 10 && fireworks.IsActive; t += 1 / 60.0) fireworks.Update(1 / 60.0);
        Assert.False(fireworks.IsActive);
        Assert.Empty(fireworks.Snapshot());
    }

    [Fact]
    public void Sparks_fly_outwards_fall_and_fade()
    {
        var fireworks = new Fireworks(new Random(4));
        fireworks.Start(400, 700, bursts: 1);
        fireworks.Update(0.01);
        var early = fireworks.Snapshot();

        for (var i = 0; i < 40; i++) fireworks.Update(1 / 60.0); // about two thirds of a second on
        var later = fireworks.Snapshot();

        Assert.True(later.Average(p => p.Alpha) < early.Average(p => p.Alpha));
        Assert.True(later.Average(p => p.Y) > early.Average(p => p.Y));            // gravity
        Assert.True(later.Max(p => p.X) - later.Min(p => p.X) > early.Max(p => p.X) - early.Min(p => p.X)); // spread out
        Assert.All(later, p => Assert.InRange(p.Alpha, 0, 1));
    }

    [Fact]
    public void One_burst_is_all_one_colour()
    {
        var fireworks = new Fireworks(new Random(5));
        fireworks.Start(400, 700, bursts: 1);
        fireworks.Update(0.01);

        Assert.Single(fireworks.Snapshot().Select(p => p.ColorIndex).Distinct());
    }

    [Fact]
    public void The_same_seed_gives_the_same_show()
    {
        List<FireworkParticle> Show()
        {
            var fireworks = new Fireworks(new Random(9));
            fireworks.Start(400, 700);
            for (var i = 0; i < 30; i++) fireworks.Update(1 / 60.0);
            return fireworks.Snapshot().ToList();
        }

        Assert.Equal(Show(), Show());
    }

    [Fact]
    public void Clear_stops_everything()
    {
        var fireworks = new Fireworks(new Random(6));
        fireworks.Start(400, 700);
        fireworks.Update(0.5);

        fireworks.Clear();

        Assert.False(fireworks.IsActive);
        Assert.Empty(fireworks.Snapshot());
    }
}
