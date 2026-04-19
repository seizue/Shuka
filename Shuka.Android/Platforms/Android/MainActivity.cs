using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace Shuka.Android;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges =
        ConfigChanges.ScreenSize | ConfigChanges.Orientation |
        ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    public static MainActivity? Instance { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Instance = this;

        var bgColor = (Microsoft.Maui.Graphics.Color)Microsoft.Maui.Controls.Application.Current!.Resources["BgPage"];
        bool lightIcons = App.CurrentTheme != AppTheme.Frost;
        var androidColor = global::Android.Graphics.Color.Argb(
            (int)(bgColor.Alpha * 255),
            (int)(bgColor.Red   * 255),
            (int)(bgColor.Green * 255),
            (int)(bgColor.Blue  * 255));
        ApplyStatusBarColor(androidColor, lightIcons);
    }

    /// <summary>
    /// Updates the status bar background and icon tint to match the current theme.
    /// </summary>
#pragma warning disable CA1416, CA1422
    public void ApplyStatusBarColor(global::Android.Graphics.Color bgColor, bool lightIcons)
    {
        if (Window is null) return;

        // SetStatusBarColor is deprecated on API 35+ (edge-to-edge) but remains
        // functional and is the simplest cross-version tinting approach.
        Window.SetStatusBarColor(bgColor);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            // API 30+: WindowInsetsController
            var appearance = lightIcons
                ? 0
                : (int)WindowInsetsControllerAppearance.LightStatusBars;
            Window.InsetsController?.SetSystemBarsAppearance(
                appearance,
                (int)WindowInsetsControllerAppearance.LightStatusBars);
        }
        else if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            // API 23–29: SystemUiFlags (LightStatusBar requires API 23)
            var flags = Window.DecorView.SystemUiFlags;
            Window.DecorView.SystemUiFlags = lightIcons
                ? flags & ~SystemUiFlags.LightStatusBar
                : flags | SystemUiFlags.LightStatusBar;
        }
        // API 21–22: no light-icon control available; bar color still set above
    }
#pragma warning restore CA1416, CA1422
}
