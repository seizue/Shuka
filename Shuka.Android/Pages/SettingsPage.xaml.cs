namespace Shuka.Android.Pages;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        RefreshRadios(App.CurrentTheme);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshRadios(App.CurrentTheme);
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private void OnThemeObsidian(object sender, TappedEventArgs e)  => ApplyAndRefresh(AppTheme.Obsidian);
    private void OnThemeRosewood(object sender, TappedEventArgs e)  => ApplyAndRefresh(AppTheme.Rosewood);
    private void OnThemeSlate(object sender, TappedEventArgs e)     => ApplyAndRefresh(AppTheme.Slate);
    private void OnThemeParchment(object sender, TappedEventArgs e) => ApplyAndRefresh(AppTheme.Frost);

    private void ApplyAndRefresh(AppTheme theme)
    {
        App.ApplyTheme(theme);
        BackgroundColor = (Color)Application.Current!.Resources["BgPage"];
        RefreshRadios(theme);
    }

    private void RefreshRadios(AppTheme theme)
    {
        var on     = (string)Application.Current!.Resources["IconRadioOn"];
        var off    = (string)Application.Current.Resources["IconRadioOff"];
        var accent = (Color)Application.Current.Resources["Accent"];
        var muted  = (Color)Application.Current.Resources["TextMuted"];

        RadioObsidian.Text       = theme == AppTheme.Obsidian ? on : off;
        RadioRosewood.Text       = theme == AppTheme.Rosewood ? on : off;
        RadioSlate.Text          = theme == AppTheme.Slate    ? on : off;
        RadioParchment.Text      = theme == AppTheme.Frost    ? on : off;

        RadioObsidian.TextColor  = theme == AppTheme.Obsidian ? accent : muted;
        RadioRosewood.TextColor  = theme == AppTheme.Rosewood ? accent : muted;
        RadioSlate.TextColor     = theme == AppTheme.Slate    ? accent : muted;
        RadioParchment.TextColor = theme == AppTheme.Frost    ? accent : muted;
    }

    // ── Support ───────────────────────────────────────────────────────────────

    private async void OnBugReportTapped(object sender, TappedEventArgs e)
    {
        try { await Launcher.Default.OpenAsync(new Uri("https://github.com/seizue/Shuka/issues/new")); }
        catch { await DisplayAlert("Error", "Could not open browser.", "OK"); }
    }

    private async void OnAboutTapped(object sender, TappedEventArgs e)
        => await Navigation.PushAsync(new AboutPage());
}
