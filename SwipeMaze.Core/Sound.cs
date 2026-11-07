namespace SwipeMaze.Core;

public enum Sound
{
    SwipeUp, SwipeRight, SwipeDown, SwipeLeft,
    /// <summary>Backing up along the trail.</summary>
    Back,
    /// <summary>Swiping into a wall: a buzz.</summary>
    Wall,
    /// <summary>The maze is solved.</summary>
    Win,
    /// <summary>A firework bursting.</summary>
    Pop,
}

public interface ISoundPlayer
{
    /// <summary>Plays the sound now, cutting off any earlier play of the same sound. Never throws:
    /// a game with no sound is still a game.</summary>
    void Play(Sound sound);
}

/// <summary>
/// The game's sound effects, made from maths rather than recorded: each one is a short mono clip of
/// 16-bit samples at <see cref="SampleRate"/>. Generated once at start-up and played from memory.
/// The noise in the pop is seeded, so every run makes the identical clip.
/// </summary>
public static class SoundSynth
{
    public const int SampleRate = 22050;

    public static short[] Create(Sound sound) => sound switch
    {
        Sound.SwipeUp => Sweep(520, 900, 0.13),
        Sound.SwipeDown => Sweep(760, 400, 0.13),
        Sound.SwipeRight => Sweep(470, 640, 0.13),
        Sound.SwipeLeft => Sweep(640, 470, 0.13),
        Sound.Back => Back(),
        Sound.Wall => Buzz(),
        Sound.Win => Win(),
        Sound.Pop => Pop(),
        _ => throw new ArgumentOutOfRangeException(nameof(sound), sound, null),
    };

    public static TimeSpan Duration(short[] samples) => TimeSpan.FromSeconds((double)samples.Length / SampleRate);

    /// <summary>A quick whoosh: a sine tone sliding from one pitch to another, fading out fast.</summary>
    private static short[] Sweep(double fromHz, double toHz, double seconds)
    {
        var buffer = new float[(int)(seconds * SampleRate)];
        AddSweep(buffer, 0, fromHz, toHz, seconds, volume: 0.45, decayPerSecond: 16, Wave.Sine);
        return ToPcm(buffer);
    }

    /// <summary>Backing up: a softer, lower, falling blip.</summary>
    private static short[] Back()
    {
        var buffer = new float[(int)(0.16 * SampleRate)];
        AddSweep(buffer, 0, 430, 250, 0.16, volume: 0.45, decayPerSecond: 14, Wave.Triangle);
        return ToPcm(buffer);
    }

    /// <summary>The wall buzz: two slightly out-of-tune low sawtooth tones beat against each other, with a
    /// fast wobble on top.</summary>
    private static short[] Buzz()
    {
        const double seconds = 0.3;
        var buffer = new float[(int)(seconds * SampleRate)];
        AddSweep(buffer, 0, 96, 96, seconds, volume: 0.28, decayPerSecond: 0, Wave.Saw);
        AddSweep(buffer, 0, 102, 102, seconds, volume: 0.28, decayPerSecond: 0, Wave.Saw);
        AddSweep(buffer, 0, 192, 192, seconds, volume: 0.12, decayPerSecond: 0, Wave.Square);

        for (var i = 0; i < buffer.Length; i++)
        {
            var t = (double)i / SampleRate;
            buffer[i] *= (float)(0.65 + 0.35 * Math.Sin(2 * Math.PI * 38 * t)); // the wobble
            buffer[i] *= Fade(t, seconds, attack: 0.004, release: 0.07);
        }
        return ToPcm(buffer);
    }

    /// <summary>The winning jingle: a rising C major arpeggio, then a held chord with a few sparkles.</summary>
    private static short[] Win()
    {
        var buffer = new float[(int)(1.5 * SampleRate)];

        double[] arpeggio = { 523.25, 659.25, 783.99, 1046.5 }; // C5 E5 G5 C6
        for (var i = 0; i < arpeggio.Length; i++)
        {
            var start = i * 0.11;
            AddSweep(buffer, start, arpeggio[i], arpeggio[i], 0.22, volume: 0.35, decayPerSecond: 7, Wave.Sine);
            AddSweep(buffer, start, arpeggio[i] * 2, arpeggio[i] * 2, 0.22, volume: 0.1, decayPerSecond: 9, Wave.Sine);
        }

        double[] chord = { 1046.5, 1318.5, 1568.0 }; // C6 E6 G6
        foreach (var hz in chord)
            AddSweep(buffer, 0.46, hz, hz, 1.0, volume: 0.2, decayPerSecond: 2.6, Wave.Sine);

        double[] sparkleHz = { 2093, 2637, 3136, 2349 };
        for (var i = 0; i < sparkleHz.Length; i++)
            AddSweep(buffer, 0.7 + i * 0.16, sparkleHz[i], sparkleHz[i], 0.09, volume: 0.12, decayPerSecond: 30, Wave.Sine);

        return ToPcm(buffer, peak: 0.85);
    }

    /// <summary>A firework burst: a soft low thump plus a crackle of dulled noise.</summary>
    private static short[] Pop()
    {
        const double seconds = 0.4;
        var buffer = new float[(int)(seconds * SampleRate)];
        AddSweep(buffer, 0, 170, 45, seconds, volume: 0.5, decayPerSecond: 13, Wave.Sine);

        var noise = new Random(1234);
        var lowPassed = 0f;
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = (double)i / SampleRate;
            lowPassed += 0.22f * ((float)(noise.NextDouble() * 2 - 1) - lowPassed); // takes the hiss off the noise
            buffer[i] += lowPassed * (float)(0.9 * Math.Exp(-9 * t)) * Fade(t, seconds, attack: 0.003, release: 0.02);
        }
        return ToPcm(buffer, peak: 0.75);
    }

    private enum Wave { Sine, Triangle, Saw, Square }

    /// <summary>Mixes a tone into the buffer, starting at <paramref name="startSeconds"/>, its pitch moving
    /// in a straight line from one frequency to the other. The volume decays exponentially, and always
    /// ramps in and out over a few milliseconds so there is no click at either end.</summary>
    private static void AddSweep(float[] buffer, double startSeconds, double fromHz, double toHz,
        double seconds, double volume, double decayPerSecond, Wave wave)
    {
        var first = (int)(startSeconds * SampleRate);
        var count = Math.Min((int)(seconds * SampleRate), buffer.Length - first);
        var phase = 0.0;

        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var hz = fromHz + (toHz - fromHz) * (t / seconds);
            phase += hz / SampleRate;
            phase -= Math.Floor(phase);

            var sample = wave switch
            {
                Wave.Sine => Math.Sin(2 * Math.PI * phase),
                Wave.Triangle => 4 * Math.Abs(phase - 0.5) - 1,
                Wave.Saw => 2 * phase - 1,
                _ => phase < 0.5 ? 1.0 : -1.0,
            };

            var envelope = Math.Exp(-decayPerSecond * t) * Fade(t, seconds, attack: 0.004, release: 0.012);
            buffer[first + i] += (float)(sample * volume * envelope);
        }
    }

    /// <summary>0 to 1 over the first <paramref name="attack"/> seconds and back to 0 over the last
    /// <paramref name="release"/>.</summary>
    private static float Fade(double t, double total, double attack, double release) =>
        (float)Math.Clamp(Math.Min(t / attack, (total - t) / release), 0, 1);

    /// <summary>Converts to 16-bit samples, turning the whole clip down if it would otherwise go past
    /// <paramref name="peak"/> of full scale (so it can never distort).</summary>
    private static short[] ToPcm(float[] samples, double peak = 0.9)
    {
        var loudest = 0f;
        foreach (var s in samples) loudest = Math.Max(loudest, Math.Abs(s));
        var scale = loudest > peak ? peak / loudest : 1.0;

        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            pcm[i] = (short)Math.Round(samples[i] * scale * short.MaxValue);
        return pcm;
    }
}
