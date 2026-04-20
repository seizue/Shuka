using Shuka.Android.Services;

namespace Shuka.Android.Pages;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        RefreshRadios(App.CurrentTheme);
        RefreshDownloadPath();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshRadios(App.CurrentTheme);
        RefreshDownloadPath();
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

    // ── Download location ─────────────────────────────────────────────────────

    private void RefreshDownloadPath()
    {
        DownloadPathLabel.Text = DownloadManager.GetOutputDirectory();
    }

    private async void OnChangeDownloadFolderTapped(object sender, TappedEventArgs e)
    {
        // On Android we can't use a native folder picker without SAF/intent plumbing,
        // so we let the user type a path manually via a prompt.
        string current = DownloadManager.GetOutputDirectory();
        string? result = await DisplayPromptAsync(
            "Download Location",
            "Enter the full folder path where EPUBs will be saved:",
            initialValue: current,
            maxLength: 300,
            keyboard: Keyboard.Url);

        if (result == null) return; // cancelled

        result = result.Trim();
        if (string.IsNullOrWhiteSpace(result))
        {
            await DisplayAlert("Invalid Path", "Path cannot be empty.", "OK");
            return;
        }

        // Try to create the directory to validate the path
        try
        {
            Directory.CreateDirectory(result);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Invalid Path", $"Could not create folder:\n{ex.Message}", "OK");
            return;
        }

        DownloadManager.SetOutputDirectory(result);
        RefreshDownloadPath();
        await DisplayAlert("Saved", $"Downloads will now be saved to:\n{result}", "OK");
    }

    private async void OnResetDownloadFolderTapped(object sender, TappedEventArgs e)
    {
        DownloadManager.ResetOutputDirectory();
        RefreshDownloadPath();
        await DisplayAlert("Reset", $"Download location reset to default:\n{DownloadManager.GetOutputDirectory()}", "OK");
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
