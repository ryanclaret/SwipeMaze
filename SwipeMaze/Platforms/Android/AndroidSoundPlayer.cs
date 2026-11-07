using Android.Media;
using SwipeMaze.Core;

namespace SwipeMaze;

/// <summary>
/// Plays the generated sound effects. Each clip is written once into a "static" <see cref="AudioTrack"/>
/// (the whole clip held in memory), so playing it is just rewind-and-go — fast enough for swipes that
/// come one after another. Sounds go through the media volume, like any game.
/// </summary>
public sealed class AndroidSoundPlayer : ISoundPlayer
{
    /// <summary>The fireworks fire several pops close together, so a few tracks take turns to let
    /// them overlap rather than cutting each other off.</summary>
    private const int PopVoices = 3;

    private readonly Dictionary<Sound, AudioTrack[]> _tracks = new();
    private readonly Dictionary<Sound, int> _next = new();

    public AndroidSoundPlayer()
    {
        foreach (var sound in Enum.GetValues<Sound>())
        {
            try
            {
                var samples = SoundSynth.Create(sound);
                var voices = sound == Sound.Pop ? PopVoices : 1;
                _tracks[sound] = Enumerable.Range(0, voices).Select(_ => CreateTrack(samples)).ToArray();
                _next[sound] = 0;
            }
            catch
            {
                // No sound beats no game: leave this one silent.
            }
        }
    }

    public void Play(Sound sound)
    {
        try
        {
            if (!_tracks.TryGetValue(sound, out var voices)) return;

            var track = voices[_next[sound] % voices.Length];
            _next[sound]++;

            track.Stop();
            track.ReloadStaticData(); // rewind to the start of the clip
            track.Play();
        }
        catch
        {
            // Never let a sound problem interrupt play.
        }
    }

    private static AudioTrack CreateTrack(short[] samples)
    {
        var attributes = new AudioAttributes.Builder()!
            .SetUsage(AudioUsageKind.Game)!
            .SetContentType(AudioContentType.Sonification)!
            .Build()!;
        var format = new AudioFormat.Builder()!
            .SetSampleRate(SoundSynth.SampleRate)!
            .SetEncoding(Android.Media.Encoding.Pcm16bit)!
            .SetChannelMask(ChannelOut.Mono)!
            .Build()!;

        var track = new AudioTrack(attributes, format, samples.Length * sizeof(short),
            AudioTrackMode.Static, AudioManager.AudioSessionIdGenerate);
        track.Write(samples, 0, samples.Length);
        return track;
    }
}
