namespace Shuka.Android.Controls;

/// <summary>
/// Custom pill-style tab bar. Each tab page creates its own instance.
/// All instances share ActiveIndex via a static so they stay in sync.
/// </summary>
public partial class CustomTabBar : Grid
{
    public static int ActiveIndex { get; private set; } = 0;
    private static readonly List<WeakReference<CustomTabBar>> _all = [];

    public CustomTabBar()
    {
        InitializeComponent();
        _all.Add(new WeakReference<CustomTabBar>(this));
        Unloaded += (_, _) => _all.RemoveAll(r => !r.TryGetTarget(out _));
        ApplyColors();
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    public static void SetActive(int index)
    {
        ActiveIndex = index;
        foreach (var wr in _all.ToList())
            if (wr.TryGetTarget(out var bar))
                bar.ApplyColors();
    }

    public static void RefreshAll()
    {
        foreach (var wr in _all.ToList())
            if (wr.TryGetTarget(out var bar))
                MainThread.BeginInvokeOnMainThread(bar.ApplyColors);
    }

    // ── Instance ──────────────────────────────────────────────────────────────

    public void ApplyColors()
    {
        var res = Application.Current?.Resources;
        if (res == null) return;

        Color accent   = res.TryGetValue("AccentLight",      out var a)  ? (Color)a  : Colors.White;
        Color accentBg = res.TryGetValue("AccentContainer",  out var ab) ? (Color)ab : Colors.Transparent;
        Color muted    = res.TryGetValue("NavBarUnselected", out var m)  ? (Color)m  : Colors.Gray;
        Color bg       = res.TryGetValue("NavBar",           out var nb) ? (Color)nb : Colors.Black;

        BarBg.BackgroundColor = bg;

        // Use named fields directly — avoids any array-index offset issues
        SetTab(PillHome,      IconHome,      LabelHome,      0, accent, accentBg, muted);
        SetTab(PillDownloads, IconDownloads, LabelDownloads, 1, accent, accentBg, muted);
        SetTab(PillHistory,   IconHistory,   LabelHistory,   2, accent, accentBg, muted);
        SetTab(PillSettings,  IconSettings,  LabelSettings,  3, accent, accentBg, muted);
    }

    private static void SetTab(Border pill, Label icon, Label label,
        int tabIndex, Color accent, Color accentBg, Color muted)
    {
        bool active = tabIndex == ActiveIndex;
        pill.BackgroundColor = active ? accentBg : Colors.Transparent;
        icon.TextColor       = active ? accent   : muted;
        label.TextColor      = active ? accent   : muted;
    }

    // ── Taps ──────────────────────────────────────────────────────────────────

    private async void OnHomeTapped(object sender, TappedEventArgs e)
        => await NavigateTo("//MainPage", 0);

    private async void OnDownloadsTapped(object sender, TappedEventArgs e)
        => await NavigateTo("//DownloadsPage", 1);

    private async void OnHistoryTapped(object sender, TappedEventArgs e)
        => await NavigateTo("//HistoryPage", 2);

    private async void OnSettingsTapped(object sender, TappedEventArgs e)
        => await NavigateTo("//SettingsPage", 3);

    private static async Task NavigateTo(string route, int index)
    {
        if (ActiveIndex == index) return;
        // Update the highlight immediately on tap — no waiting for OnAppearing.
        // SetActive only updates CustomTabBar.ActiveIndex (visual highlight).
        // AppShell.LastTabIndex / ActiveTabIndex are updated by OnShellNavigating
        // which fires from GoToAsync, so the slide direction is still correct.
        SetActive(index);
        // animate: false — suppress Shell's native fragment transition so it
        // doesn't bounce/slide the header independently of our custom TabTransition
        // body-only animation that runs in each page's OnAppearing.
        await Shell.Current.GoToAsync(route, animate: false);
    }
}
