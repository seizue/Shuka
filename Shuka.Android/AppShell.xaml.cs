using Shuka.Android.Pages;
using Shuka.Android.Behaviors;

namespace Shuka.Android;

public partial class AppShell : Shell
{
    public static readonly string[] TabRoutes =
        ["MainPage", "DownloadsPage", "HistoryPage", "SettingsPage"];

    public static int LastTabIndex   { get; private set; } = 0;
    public static int ActiveTabIndex { get; private set; } = 0;

    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(nameof(AboutPage), typeof(AboutPage));
        Routing.RegisterRoute(nameof(SourceBrowsePage), typeof(SourceBrowsePage));

        Navigating += OnShellNavigating;
        Navigated  += (_, _) => SyncTabBarToCurrentPage();

        // Eagerly instantiate background tabs after startup to avoid delayed first-transition
        Dispatcher.Dispatch(() =>
        {
            DownloadsContent.Content = new DownloadsPage();
            HistoryContent.Content = new HistoryPage();
            SettingsContent.Content = new SettingsPage();
        });
    }

    /// <summary>
    /// Keeps the persistent tab bar highlight aligned with the Shell's current tab page.
    /// </summary>
    public static void SyncTabBarToCurrentPage()
    {
        var page = Shell.Current?.CurrentPage;
        if (page == null) return;

        int index = Array.IndexOf(TabRoutes, page.GetType().Name);
        if (index >= 0)
            Controls.CustomTabBar.SetActive(index);
    }

    private void OnShellNavigating(object? sender, ShellNavigatingEventArgs e)
    {
        string segment = e.Target?.Location?.OriginalString?.Split('/').LastOrDefault() ?? "";
        int newIndex = Array.IndexOf(TabRoutes, segment);
        if (newIndex < 0) return;

        if (ActiveTabIndex != newIndex)
        {
            LastTabIndex   = ActiveTabIndex;
            ActiveTabIndex = newIndex;
            TabTransition.SetTargetIndex(newIndex);
        }
    }
}

