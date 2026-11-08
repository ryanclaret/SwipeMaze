using Microsoft.JSInterop;

namespace SwipeMaze.Play.Services;

/// <summary>
/// The browser's local storage (see wwwroot/js/game.js), read and written straight away rather than
/// through an await: in WebAssembly the page and the game run on the same thread, so this is instant.
/// Every call is safe: if the browser refuses storage (a private window, or storage turned off), reads
/// give nothing and writes are dropped, and the game still plays — it just won't remember anything.
/// </summary>
public sealed class BrowserStore(IJSRuntime js)
{
    private readonly IJSInProcessRuntime _js = (IJSInProcessRuntime)js;

    public string? Get(string key) => _js.Invoke<string?>("store.get", key);

    public void Set(string key, string value) => _js.InvokeVoid("store.set", key, value);

    public void Remove(string key) => _js.InvokeVoid("store.remove", key);
}
