using System.Collections.Specialized;
using System.ComponentModel;
using Shuka.Android.Services;

namespace Shuka.Android.Pages;

public partial class DownloadsPage : ContentPage
{
    // Maps each DownloadItem.Id → its card view
    private readonly Dictionary<Guid, DownloadCard> _cards = new();

    public DownloadsPage()
    {
        InitializeComponent();

        // Observe the shared collection
        DownloadManager.Instance.Downloads.CollectionChanged += OnCollectionChanged;

        // Populate any items that already exist (e.g. page recreated)
        foreach (var item in DownloadManager.Instance.Downloads)
            AddCard(item);

        RefreshEmptyState();
        RefreshSummary();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (e.NewItems != null)
                foreach (DownloadItem item in e.NewItems)
                    AddCard(item);

            if (e.OldItems != null)
                foreach (DownloadItem item in e.OldItems)
                    RemoveCard(item);

            RefreshEmptyState();
            RefreshSummary();
        });
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

        // Watch status changes to keep the summary pill live
        item.PropertyChanged += OnItemPropertyChanged;

        _cards[item.Id] = card;
        CardList.Insert(0, card); // newest on top
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

    private void RefreshEmptyState()
    {
        bool hasItems = DownloadManager.Instance.Downloads.Count > 0;
        EmptyState.IsVisible = !hasItems;
        ListScroll.IsVisible  = hasItems;
    }

    private void RefreshSummary()
    {
        var all     = DownloadManager.Instance.Downloads;
        int running = all.Count(d => d.IsRunning);
        int done    = all.Count(d => d.IsDone);

        bool showPill = running > 0 || done > 0;
        SummaryPill.IsVisible = showPill;

        RunningBadge.IsVisible = running > 0;
        RunningLabel.Text      = running == 1 ? "1 in progress" : $"{running} in progress";

        DoneBadge.IsVisible = done > 0;
        DoneLabel.Text      = done == 1 ? "1 done" : $"{done} done";
    }

    private async void OnCancelAllClicked(object sender, TappedEventArgs e)
    {
        bool hasActive = DownloadManager.Instance.Downloads.Any(d => d.IsRunning);
        if (!hasActive) return;

        bool confirm = await DisplayAlert(
            "Cancel All",
            "Cancel all active downloads?",
            "Cancel All", "Keep");

        if (confirm)
            DownloadManager.Instance.CancelAll();
    }

    private async void OnClearHistoryClicked(object sender, TappedEventArgs e)
    {
        bool hasFinished = DownloadManager.Instance.Downloads.Any(d => d.IsFinished);
        if (!hasFinished)
        {
            await DisplayAlert("Nothing to clear", "No completed downloads to remove.", "OK");
            return;
        }

        bool confirm = await DisplayAlert(
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
