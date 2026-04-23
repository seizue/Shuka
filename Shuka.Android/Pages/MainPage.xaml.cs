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
        UrlEntry.IsEnabled      = false;
        CoverEntry.IsEnabled    = false;
        ChaptersEntry.IsEnabled = false;
        UrlEntry.IsEnabled      = true;
        CoverEntry.IsEnabled    = true;
        ChaptersEntry.IsEnabled = true;

        // ── Duplicate check ───────────────────────────────────────────────────
        var existing = DownloadManager.Instance.FindExisting(url);
        if (existing != null)
        {
            bool shouldQueue = await HandleDuplicate(existing);
            if (!shouldQueue) return;
        }

        // ── Enqueue ───────────────────────────────────────────────────────────
        DownloadManager.Instance.Enqueue(url, chapters, coverUrl);

        // Clear inputs for next novel
        UrlEntry.Text      = "";
        CoverEntry.Text    = "";
        ChaptersEntry.Text = "0";

        // Show confirmation banner briefly
        QueuedBanner.IsVisible = true;
        await Task.Delay(3000);
        QueuedBanner.IsVisible = false;
    }

    /// <summary>
    /// Shows the appropriate duplicate prompt based on the existing item's status.
    /// Returns true if the caller should proceed with a new download, false to abort.
    /// </summary>
    private async Task<bool> HandleDuplicate(DownloadItem existing)
    {
        string title  = string.IsNullOrWhiteSpace(existing.Title) || existing.Title == "Loading..."
            ? "this novel"
            : $"\"{existing.Title}\"";

        switch (existing.Status)
        {
            // ── Already running / queued ──────────────────────────────────────
            case DownloadStatus.Running:
            case DownloadStatus.Queued:
            {
                string choice = await DisplayActionSheet(
                    $"Already downloading {title}",
                    "Cancel",
                    null,
                    "Go to Downloads tab",
                    "Download again anyway");

                if (choice == "Go to Downloads tab")
                {
                    // Switch to the Downloads tab (index 1)
                    if (Shell.Current != null)
                        await Shell.Current.GoToAsync("//DownloadsPage");
                    return false;
                }

                if (choice == "Download again anyway")
                    return true;

                return false; // tapped Cancel / dismissed
            }

            // ── Already completed ─────────────────────────────────────────────
            case DownloadStatus.Done:
            {
                string choice = await DisplayActionSheet(
                    $"{title} was already downloaded",
                    "Cancel",
                    null,
                    "Download again (re-translate)",
                    "Open existing EPUB",
                    "Go to Downloads tab");

                if (choice == "Download again (re-translate)")
                    return true;

                if (choice == "Open existing EPUB" && existing.EpubPath != null
                    && File.Exists(existing.EpubPath))
                {
                    try
                    {
                        await Launcher.Default.OpenAsync(new OpenFileRequest
                        {
                            Title = "Open EPUB",
                            File  = new ReadOnlyFile(existing.EpubPath, "application/epub+zip")
                        });
                    }
                    catch
                    {
                        await Share.Default.RequestAsync(new ShareFileRequest
                        {
                            Title = "Open EPUB",
                            File  = new ShareFile(existing.EpubPath, "application/epub+zip")
                        });
                    }
                    return false;
                }

                if (choice == "Go to Downloads tab")
                {
                    if (Shell.Current != null)
                        await Shell.Current.GoToAsync("//DownloadsPage");
                    return false;
                }

                return false;
            }

            // ── Previously failed or cancelled — offer to retry ───────────────
            case DownloadStatus.Failed:
            case DownloadStatus.Cancelled:
            {
                string statusWord = existing.Status == DownloadStatus.Failed ? "failed" : "cancelled";
                string choice = await DisplayActionSheet(
                    $"A previous download of {title} {statusWord}",
                    "Cancel",
                    null,
                    "Download again",
                    "Go to Downloads tab");

                if (choice == "Download again")
                {
                    // Remove the old failed/cancelled entry first to keep the list clean
                    DownloadManager.Instance.Dismiss(existing);
                    return true;
                }

                if (choice == "Go to Downloads tab")
                {
                    if (Shell.Current != null)
                        await Shell.Current.GoToAsync("//DownloadsPage");
                    return false;
                }

                return false;
            }

            default:
                return true;
        }
    }
}
