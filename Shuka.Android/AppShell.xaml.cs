using Shuka.Android.Pages;

namespace Shuka.Android;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(nameof(AboutPage), typeof(AboutPage));
        
        // Subscribe to navigation events for smooth transitions
        Navigating += OnNavigating;
        Navigated += OnNavigated;
    }

    private async void OnNavigating(object? sender, ShellNavigatingEventArgs e)
    {
        // Smooth fade out during navigation
        if (CurrentPage != null)
        {
            await CurrentPage.FadeToAsync(0.85, 100, Easing.CubicOut);
        }
    }

    private async void OnNavigated(object? sender, ShellNavigatedEventArgs e)
    {
        // Smooth fade in after navigation
        if (CurrentPage != null)
        {
            CurrentPage.Opacity = 0.85;
            await CurrentPage.FadeToAsync(1.0, 150, Easing.CubicOut);
        }
    }
}
