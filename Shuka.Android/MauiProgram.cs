using Shuka.Android.Platforms.Android;

namespace Shuka.Android;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Register extended encodings (GBK, GB2312, Big5, etc.) for Chinese novel sources
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("MaterialSymbols.ttf", "MaterialSymbols");
                fonts.AddFont("Pacifico-Regular.ttf", "Pacifico");
            })
            .ConfigureMauiHandlers(handlers =>
            {
                handlers.AddHandler<Entry, ThemedEntryHandler>();
#if ANDROID
                handlers.AddHandler<Shell, ShukaShellRenderer>();
                // Re-enable AdBlockingWebViewHandler with improved implementation
                handlers.AddHandler<Microsoft.Maui.Controls.WebView, AdBlockingWebViewHandler>();
#endif
            });

        return builder.Build();
    }
}
