using Shuka.Android.Platforms.Android;

namespace Shuka.Android;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("MaterialSymbols.ttf", "MaterialSymbols");
            })
            .ConfigureMauiHandlers(handlers =>
            {
                handlers.AddHandler<Entry, ThemedEntryHandler>();
            });

        return builder.Build();
    }
}
