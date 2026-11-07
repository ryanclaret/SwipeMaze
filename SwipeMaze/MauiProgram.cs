using SwipeMaze.Core;

namespace SwipeMaze;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton(TimeProvider.System);

        // Private to the app, survives updates.
        var progressPath = Path.Combine(FileSystem.AppDataDirectory, "progress.tsv");
        builder.Services.AddSingleton<IPlayerProgressRepository>(_ => new FilePlayerProgressRepository(progressPath));

        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
