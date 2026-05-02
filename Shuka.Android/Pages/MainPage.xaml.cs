using Shuka.Android.Services;

namespace Shuka.Android.Pages;

public partial class MainPage : ContentPage
{
    private bool _isPageLoaded = false;

    public MainPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        
        if (!_isPageLoaded)
        {
            await AnimatePageLoad();
            _isPageLoaded = true;
        }
    }

    private async Task AnimatePageLoad()
    {
        // Simple, smooth page load animation
        var mainContent = (Grid)Content;
        mainContent.Opacity = 0;
        
        // Brief delay for smooth transition
        await Task.Delay(50);
        
        // Smooth fade in
        await mainContent.FadeToAsync(1.0, 250, Easing.CubicOut);
        
        // Animate form elements with subtle stagger
        await AnimateFormElements();
    }

    private async Task AnimateFormElements()
    {
        // Use named reference instead of fragile index-based child access
        var stackLayout = (VerticalStackLayout)BodyScrollView.Content;
        
        // Animate each card with a slight delay
        var cards = stackLayout.Children.Where(c => c is Border).Cast<VisualElement>().ToList();
        
        foreach (var card in cards)
        {
            card.Opacity = 0;
            card.TranslationY = 8;
        }

        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            int index = i;
            _ = Task.Run(async () =>
            {
                await Task.Delay(index * 30); // Subtle stagger
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await Task.WhenAll(
                        card.FadeToAsync(1.0, 200, Easing.CubicOut),
                        card.TranslateToAsync(0, 0, 200, Easing.CubicOut)
                    );
                });
            });
        }
    }

    private async void OnDownloadClicked(object sender, TappedEventArgs e)
    {
        // Add button press animation — sender is the inner Grid, DownloadBtn is the outer Border
        await AnimateButtonPress(DownloadBtn);

        string url = UrlEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            await DisplayAlertAsync("Missing URL", "Please enter a novel URL.", "OK");
            return;
        }

        int chapters = int.TryParse(ChaptersEntry.Text, out int c) ? c : 0;
        string? coverUrl = string.IsNullOrWhiteSpace(CoverEntry.Text) ? null : CoverEntry.Text.Trim();

        // Show loading state
        await ShowDownloadingState(true);

        // Dismiss keyboard
        UrlEntry.IsEnabled      = false;
        CoverEntry.IsEnabled    = false;
        ChaptersEntry.IsEnabled = false;
        
        await Task.Delay(100); // Brief delay for UX
        
        UrlEntry.IsEnabled      = true;
        CoverEntry.IsEnabled    = true;
        ChaptersEntry.IsEnabled = true;

        // ── Duplicate check ───────────────────────────────────────────────────
        var existing = DownloadManager.Instance.FindExisting(url);
        if (existing != null)
        {
            await ShowDownloadingState(false);
            bool shouldQueue = await HandleDuplicate(existing);
            if (!shouldQueue) return;
        }

        // ── Enqueue ───────────────────────────────────────────────────────────
        DownloadManager.Instance.Enqueue(url, chapters, coverUrl);

        await ShowDownloadingState(false);

        // Clear inputs for next novel with animation
        await AnimateClearInputs();

        // Show confirmation banner with animation
        await ShowQueuedBanner();
    }

    private async Task AnimateButtonPress(Border button)
    {
        await button.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await button.ScaleToAsync(1.0, 100, Easing.CubicOut);
    }

    private async Task ShowDownloadingState(bool isDownloading)
    {
        if (isDownloading)
        {
            await LoadingOverlay.ShowAsync("Processing...", "Preparing download");
        }
        else
        {
            await LoadingOverlay.HideAsync();
        }
        
        if (isDownloading)
        {
            DownloadBtnLabel.Text = "Processing...";
            await DownloadBtn.FadeToAsync(0.7, 150);
        }
        else
        {
            DownloadBtnLabel.Text = "Download & Translate";
            await DownloadBtn.FadeToAsync(1.0, 150);
        }
    }

    private async Task AnimateClearInputs()
    {
        var entries = new[] { UrlEntry, CoverEntry, ChaptersEntry };
        
        // Fade out current values
        await Task.WhenAll(entries.Select(e => e.FadeToAsync(0.5, 150)));
        
        // Clear values
        UrlEntry.Text = "";
        CoverEntry.Text = "";
        ChaptersEntry.Text = "0";
        
        // Fade back in
        await Task.WhenAll(entries.Select(e => e.FadeToAsync(1.0, 150)));
    }

    private async Task ShowQueuedBanner()
    {
        QueuedBanner.Opacity = 0;
        QueuedBanner.TranslationY = -20;
        QueuedBanner.IsVisible = true;
        
        // Slide in from top
        await Task.WhenAll(
            QueuedBanner.FadeToAsync(1.0, 300, Easing.CubicOut),
            QueuedBanner.TranslateToAsync(0, 0, 300, Easing.CubicOut)
        );
        
        await Task.Delay(3000);
        
        // Slide out to top
        await Task.WhenAll(
            QueuedBanner.FadeToAsync(0, 300, Easing.CubicIn),
            QueuedBanner.TranslateToAsync(0, -20, 300, Easing.CubicIn)
        );
        
        QueuedBanner.IsVisible = false;
    }

    private async Task<bool> HandleDuplicate(DownloadItem existing)
    {
        string title = string.IsNullOrWhiteSpace(existing.Title) || existing.Title == "Loading..."
            ? "this novel"
            : $"\"{existing.Title}\"";

        switch (existing.Status)
        {
            case DownloadStatus.Running:
            case DownloadStatus.Queued:
            {
                string? choice = await DisplayActionSheetAsync(
                    $"Already downloading {title}",
                    "Cancel",
                    null,
                    "Go to Downloads tab",
                    "Download again anyway");

                if (choice == "Go to Downloads tab")
                {
                    if (Shell.Current != null)
                        await Shell.Current.GoToAsync("//DownloadsPage");
                    return false;
                }
                return choice == "Download again anyway";
            }

            case DownloadStatus.Done:
            {
                string? choice = await DisplayActionSheetAsync(
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

            case DownloadStatus.Failed:
            case DownloadStatus.Cancelled:
            {
                string statusWord = existing.Status == DownloadStatus.Failed ? "failed" : "cancelled";
                string? choice = await DisplayActionSheetAsync(
                    $"A previous download of {title} {statusWord}",
                    "Cancel",
                    null,
                    "Download again",
                    "Go to Downloads tab");

                if (choice == "Download again")
                {
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
