using System.Runtime.InteropServices;
using Microsoft.JSInterop;
using SwipeMaze.Core;

namespace SwipeMaze.Play.Services;

/// <summary>
/// Plays the game's sounds through the browser's Web Audio (see wwwroot/js/game.js). The clips are the
/// same ones the Android app plays, made by <see cref="SoundSynth"/>, and are handed to the browser once,
/// the first time each is needed. Never throws: a game with no sound is still a game.
/// </summary>
public sealed class WebSoundPlayer(IJSRuntime js) : ISoundPlayer
{
    private readonly IJSInProcessRuntime _js = (IJSInProcessRuntime)js;
    private readonly HashSet<Sound> _loaded = new();

    public void Play(Sound sound)
    {
        try
        {
            Load(sound);
            _js.InvokeVoid("game.playSound", sound.ToString());
        }
        catch (Exception)
        {
            // no audio support: carry on silently
        }
    }

    /// <summary>Hands every clip to the browser one at a time, with a breath in between so the page stays
    /// responsive while the game is starting up.</summary>
    public async Task PreloadAsync()
    {
        foreach (var sound in Enum.GetValues<Sound>())
        {
            try { Load(sound); }
            catch (Exception) { return; }
            await Task.Delay(1);
        }
    }

    private void Load(Sound sound)
    {
        if (!_loaded.Add(sound)) return;
        var samples = SoundSynth.Create(sound);
        _js.InvokeVoid("game.loadSound", sound.ToString(), Convert.ToBase64String(MemoryMarshal.AsBytes(samples.AsSpan())));
    }
}
