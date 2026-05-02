using System.Collections.Specialized;
using System.ComponentModel;
using Shuka.Android.Services;

namespace Shuka.Android.Pages;

public partial class DownloadsPage : ContentPage
{
    private readonly Dictionary<Guid, DownloadCard> _cards = new();

    public DownloadsPage()
    {
        InitializeComponent();
        DownloadManager.Instance.Downloads.CollectionChanged += OnCollectionChanged;

        foreach (var item in DownloadManager.Instance.Downloads)
            AddCard(item);

        RefreshEmptyState();
        RefreshSummary();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await AnimateIn();
    }

    private async Task AnimateIn()
    {
        // Animate body content in — same pattern as all other pages
        BodyGrid.Opacity = 0;
        BodyGrid.TranslationY = 18;

        await Task.WhenAll(
            BodyGrid.FadeToAsync(1.0, 220, Easing.CubicOut),
            BodyGrid.TranslateToAsync(0, 0, 220, Easing.CubicOut)
        );
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (e.NewItems != null)
            {
                foreach (DownloadItem item in e.NewItems)
                {
                    await AddCardWithAnimation(item);
                }
            }

            if (e.OldItems != null)
            {
                foreach (DownloadItem item in e.OldItems)
                {
                    await RemoveCardWithAnimation(item);
                }
            }

            RefreshEmptyState();
            RefreshSummary();
        });
    }

    private async Task AddCardWithAnimation(DownloadItem item)
    {
        if (_cards.ContainsKey(item.Id)) return;

        var card = new DownloadCard(item);
        card.CancelRequested  += OnCardCancelRequested;
        card.ShareRequested   += OnCardShareRequested;
        card.OpenRequested    += OnCardOpenRequested;
        card.RetryRequested   += OnCardRetryRequested;
        card.DismissRequested += OnCardDismissRequested;

        item.PropertyChanged += OnItemPropertyChanged;

        _cards[item.Id] = card;
        
        // Start hidden and animate in
        card.Opacity = 0;
        card.TranslationY = -30;
        card.Scale = 0.9;
        
        CardList.Insert(0, card);
        
        // Animate in
        await Task.WhenAll(
            card.FadeToAsync(1.0, 400, Easing.CubicOut),
            card.TranslateToAsync(0, 0, 400, Easing.CubicOut),
            card.ScaleToAsync(1.0, 400, Easing.CubicOut)
        );
    }

    private void AddCard(DownloadItem item)
    {
        if (_cards.ContainsKey(item.Id)) return;

        var card = new DownloadCard(item);
        card.CancelRequested  += OnCardCancelRequested;
        card.ShareRequested   += OnCardShareRequested;
        card.OpenRequested    += OnCardOpenRequested;
        card.RetryRequested   += OnCardRetryRequested;
        card.DismissRequested += OnCardDismissRequested;

        item.PropertyChanged += OnItemPropertyChanged;

        _cards[item.Id] = card;
        CardList.Insert(0, card);
    }

    private async Task RemoveCardWithAnimation(DownloadItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;

        if (!_cards.TryGetValue(item.Id, out var card)) return;
        
        // Animate out
        await Task.WhenAll(
            card.FadeToAsync(0, 300, Easing.CubicIn),
            card.TranslateToAsync(-50, 0, 300, Easing.CubicIn),
            card.ScaleToAsync(0.8, 300, Easing.CubicIn)
        );
        
        CardList.Remove(card);
        _cards.Remove(item.Id);
    }

    private void RemoveCard(DownloadItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;

        if (!_cards.TryGetValue(item.Id, out var card)) return;
        CardList.Remove(card);
        _cards.Remove(item.Id);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.Status))
            MainThread.BeginInvokeOnMainThread(RefreshSummary);
    }

    private async void RefreshEmptyState()
    {
        bool hasItems = DownloadManager.Instance.Downloads.Count > 0;
        
        if (hasItems && EmptyState.IsVisible)
        {
            // Hide empty state with animation
            await EmptyState.FadeToAsync(0, 200);
            EmptyState.IsVisible = false;
            
            // Show list with animation
            ListScroll.Opacity = 0;
            ListScroll.IsVisible = true;
            await ListScroll.FadeToAsync(1.0, 300);
        }
        else if (!hasItems && !EmptyState.IsVisible)
        {
            // Hide list with animation
            await ListScroll.FadeToAsync(0, 200);
            ListScroll.IsVisible = false;
            
            // Show empty state with animation
            EmptyState.Opacity = 0;
            EmptyState.TranslationY = 20;
            EmptyState.IsVisible = true;
            await Task.WhenAll(
                EmptyState.FadeToAsync(1.0, 400, Easing.CubicOut),
                EmptyState.TranslateToAsync(0, 0, 400, Easing.CubicOut)
            );
        }
    }

    private async void RefreshSummary()
    {
        var all     = DownloadManager.Instance.Downloads;
        int running = all.Count(d => d.IsRunning);
        int done    = all.Count(d => d.IsDone);

        bool showPill = running > 0 || done > 0;
        
        if (showPill && !SummaryPill.IsVisible)
        {
            // Show pill with animation
            SummaryPill.Opacity = 0;
            SummaryPill.TranslationY = -10;
            SummaryPill.IsVisible = true;
            await Task.WhenAll(
                SummaryPill.FadeToAsync(1.0, 250, Easing.CubicOut),
                SummaryPill.TranslateToAsync(0, 0, 250, Easing.CubicOut)
            );
        }
        else if (!showPill && SummaryPill.IsVisible)
        {
            // Hide pill with animation
            await Task.WhenAll(
                SummaryPill.FadeToAsync(0, 200, Easing.CubicIn),
                SummaryPill.TranslateToAsync(0, -10, 200, Easing.CubicIn)
            );
            SummaryPill.IsVisible = false;
        }

        RunningBadge.IsVisible = running > 0;
        RunningLabel.Text      = running == 1 ? "1 in progress" : $"{running} in progress";

        DoneBadge.IsVisible = done > 0;
        DoneLabel.Text      = done == 1 ? "1 done" : $"{done} done";
    }

    private async void OnCancelAllClicked(object sender, TappedEventArgs e)
    {
        // Button press animation
        var button = (Border)sender;
        await button.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await button.ScaleToAsync(1.0, 100, Easing.CubicOut);

        bool hasActive = DownloadManager.Instance.Downloads.Any(d => d.IsRunning);
        if (!hasActive) return;

        bool confirm = await DisplayAlertAsync(
            "Cancel All",
            "Cancel all active downloads?",
            "Cancel All", "Keep");

        if (confirm)
            DownloadManager.Instance.CancelAll();
    }

    private async void OnClearHistoryClicked(object sender, TappedEventArgs e)
    {
        // Button press animation
        var button = (Border)sender;
        await button.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await button.ScaleToAsync(1.0, 100, Easing.CubicOut);

        bool hasFinished = DownloadManager.Instance.Downloads.Any(d => d.IsFinished);
        if (!hasFinished)
        {
            await DisplayAlertAsync("Nothing to clear", "No completed downloads to remove.", "OK");
            return;
        }

        bool confirm = await DisplayAlertAsync(
            "Clear History",
            "Remove all completed, cancelled, and failed downloads from the list? Files on disk are not deleted.",
            "Clear", "Cancel");

        if (confirm)
            DownloadManager.Instance.ClearHistory();
    }

    private void OnCardCancelRequested(DownloadItem item)
        => DownloadManager.Instance.Cancel(item);

    private void OnCardRetryRequested(DownloadItem item)
        => DownloadManager.Instance.Retry(item);

    private void OnCardDismissRequested(DownloadItem item)
        => DownloadManager.Instance.Dismiss(item);

    private async void OnCardShareRequested(DownloadItem item)
    {
        if (item.EpubPath == null || !File.Exists(item.EpubPath)) return;
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Share EPUB",
            File  = new ShareFile(item.EpubPath, "application/epub+zip")
        });
    }

    private async void OnCardOpenRequested(DownloadItem item)
    {
        if (item.EpubPath == null || !File.Exists(item.EpubPath)) return;
        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                Title = "Open EPUB",
                File  = new ReadOnlyFile(item.EpubPath, "application/epub+zip")
            });
        }
        catch
        {
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Open EPUB",
                File  = new ShareFile(item.EpubPath, "application/epub+zip")
            });
        }
    }
}
