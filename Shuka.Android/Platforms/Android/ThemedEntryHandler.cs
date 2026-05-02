using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace Shuka.Android.Platforms.Android;

/// <summary>
/// Removes the native Android EditText underline completely and sets
/// background to transparent so MAUI's Border wrapper is the only visual.
/// </summary>
public class ThemedEntryHandler : EntryHandler
{
    protected override MauiAppCompatEditText CreatePlatformView()
    {
        var view = base.CreatePlatformView();
        // Null out the background completely — removes the underline drawable
        view.Background = null;
        view.SetPadding(0, 8, 0, 8);
        return view;
    }

    public static void RefreshAll() { /* no-op */ }
}
