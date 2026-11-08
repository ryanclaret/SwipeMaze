using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SwipeMaze.Core;
using SwipeMaze.Play;
using SwipeMaze.Play.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BrowserStore>();
builder.Services.AddSingleton<IPlayerProgressRepository, BrowserProgressRepository>();
builder.Services.AddSingleton<WebSoundPlayer>();
builder.Services.AddSingleton<ISoundPlayer>(sp => sp.GetRequiredService<WebSoundPlayer>());

await builder.Build().RunAsync();
