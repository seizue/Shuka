using Shuka.Android.Services;

namespace Shuka.Android.Pages;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnDownloadClicked(object sender, TappedEventArgs e)
    {
        string url = UrlEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            await DisplayAlert("Missing URL", "Please enter a novel URL.", "OK");
            return;
        }

        int chapters = int.TryParse(ChaptersEntry.Text, out int c) ? c : 0;
        string? coverUrl = string.IsNullOrWhiteSpace(CoverEntry.Text) ? null : CoverEntry.Text.Trim();

        // Dismiss keyboard
        UrlEntry.IsEnabled     = false;
        CoverEntry.IsEnabled   = false;
        ChaptersEntry.IsEnabled = false;
        UrlEntry.IsEnabled     = true;
        CoverEntry.IsEnabled   = true;
        ChaptersEntry.IsEnabled = true;

        // Enqueue — runs in background, no busy lock needed
        var queued = DownloadManager.Instance.Enqueue(url, chapters, coverUrl);
        if (queued == null)
        {
            await DisplayAlert("Already Downloading",
                "This URL is already in the active download queue.", "OK");
            return;
        }

        // Clear inputs for next novel
        UrlEntry.Text      = "";
        CoverEntry.Text    = "";
        ChaptersEntry.Text = "0";

        // Show confirmation banner briefly
        QueuedBanner.IsVisible = true;
        await Task.Delay(3000);
        QueuedBanner.IsVisible = false;
    }
}
