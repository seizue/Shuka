using System.Collections.Specialized;
using System.ComponentModel;
using Shuka.Android.Behaviors;
using Shuka.Android.Platforms.Android;
using Shuka.Android.Services;

namespace Shuka.Android.Pages;

public partial class DownloadsPage : ContentPage
{
    private readonly Dictionary<Guid, DownloadCard> _allCards = new();
    private readonly HashSet<Guid> _completedIds = new();
    private bool _isOngoingTabActive = true;
    private bool _isOptionsSheetOpen;
    private DownloadItem? _activeOptionsItem;

    public DownloadsPage()
    {
        InitializeComponent();
        DownloadManager.Instance.Downloads.CollectionChanged += OnCollectionChanged;

        foreach (var item in DownloadManager.Instance.Downloads)
            AddCard(item);

        // Apply initial tab colors and button visibility without animation
        ApplySubTabColors(ongoing: true);
        CancelAllBtn.IsVisible   = true;
        ClearHistoryBtn.IsVisible = false;

        // Set initial panel empty-state visibility without animation
        bool hasOngoing   = _allCards.Keys.Any(id => !_completedIds.Contains(id));
        bool hasCompleted = _completedIds.Count > 0;
        OngoingEmptyState.IsVisible   = !hasOngoing;
        OngoingListScroll.IsVisible   = hasOngoing;
        CompletedEmptyState.IsVisible = !hasCompleted;
        CompletedListScroll.IsVisible = hasCompleted;

        RefreshSummary();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        MainActivity.Instance?.SetTabBarVisible(true);

        // Re-apply tab colors in case the theme changed while on another tab
        ApplySubTabColors(_isOngoingTabActive);

        TabTransition.Prepare(BodyGrid, myTabIndex: 1);

        var animationTask = TabTransition.SlideInAsync(BodyGrid);
        var loadTask = Task.Run(() =>
            MainThread.BeginInvokeOnMainThread(RefreshSummary));

        await Task.WhenAll(animationTask, loadTask);
    }

    // ── Sub-tab switching ─────────────────────────────────────────────────────

    private void OnTabOngoingTapped(object sender, TappedEventArgs e)
    {
        if (_isOngoingTabActive) return;
        _ = SwitchToSubTabAsync(ongoing: true);
    }

    private void OnTabCompletedTapped(object sender, TappedEventArgs e)
    {
        if (!_isOngoingTabActive) return;
        _ = SwitchToSubTabAsync(ongoing: false);
    }

    private async Task SwitchToSubTabAsync(bool ongoing)
    {
        _isOngoingTabActive = ongoing;
        ApplySubTabColors(ongoing);

        // Context-aware header buttons
        CancelAllBtn.IsVisible   = ongoing;
        ClearHistoryBtn.IsVisible = !ongoing;

        // Panel transition — slide in from the correct direction
        var outPanel = ongoing ? (View)CompletedPanel : OngoingPanel;
        var inPanel  = ongoing ? (View)OngoingPanel   : CompletedPanel;

        if (outPanel.IsVisible)
        {
            await outPanel.FadeToAsync(0, 150, Easing.CubicIn);
            outPanel.IsVisible = false;
        }

        inPanel.TranslationX = ongoing ? -20 : 20;
        inPanel.Opacity      = 0;
        inPanel.IsVisible    = true;

        await Task.WhenAll(
            inPanel.FadeToAsync(1.0, 200, Easing.CubicOut),
            inPanel.TranslateToAsync(0, 0, 200, Easing.CubicOut)
        );
    }

    private void ApplySubTabColors(bool ongoing)
    {
        Color accent      = (Color)(Application.Current!.Resources["AccentLight"]);
        Color textPrimary = (Color)(Application.Current!.Resources["TextPrimary"]);
        Color textMuted   = (Color)(Application.Current!.Resources["TextMuted"]);

        if (ongoing)
        {
            TabOngoingLabel.TextColor   = textPrimary;
            TabOngoingBar.Color         = accent;
            TabCompletedLabel.TextColor = textMuted;
            TabCompletedBar.Color       = Colors.Transparent;
        }
        else
        {
            TabCompletedLabel.TextColor = textPrimary;
            TabCompletedBar.Color       = accent;
            TabOngoingLabel.TextColor   = textMuted;
            TabOngoingBar.Color         = Colors.Transparent;
        }
    }

    // ── Collection change ──────────────────────────────────────────────────────

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (e.NewItems != null)
                foreach (DownloadItem item in e.NewItems)
                    await AddCardWithAnimation(item);

            if (e.OldItems != null)
                foreach (DownloadItem item in e.OldItems)
                    await RemoveCardWithAnimation(item);

            RefreshEmptyState();
            RefreshSummary();
        });
    }

    // ── Card management ────────────────────────────────────────────────────────

    private VerticalStackLayout CardListFor(DownloadItem item)
        => item.IsFinished ? CompletedCardList : OngoingCardList;

    private async Task AddCardWithAnimation(DownloadItem item)
    {
        if (_allCards.ContainsKey(item.Id)) return;

        var card   = CreateCard(item);
        var target = CardListFor(item);
        if (item.IsFinished) _completedIds.Add(item.Id);

        card.Opacity      = 0;
        card.TranslationY = -30;
        card.Scale        = 0.9;
        target.Insert(0, card);

        await Task.WhenAll(
            card.FadeToAsync(1.0, 400, Easing.CubicOut),
            card.TranslateToAsync(0, 0, 400, Easing.CubicOut),
            card.ScaleToAsync(1.0, 400, Easing.CubicOut)
        );
    }

    private void AddCard(DownloadItem item)
    {
        if (_allCards.ContainsKey(item.Id)) return;

        var card   = CreateCard(item);
        var target = CardListFor(item);
        if (item.IsFinished) _completedIds.Add(item.Id);
        target.Insert(0, card);
    }

    private DownloadCard CreateCard(DownloadItem item)
    {
        var card = new DownloadCard(item);
        card.OptionsRequested += OnCardOptionsRequested;
        card.ShareRequested   += OnCardShareRequested;
        card.OpenRequested    += OnCardOpenRequested;
        card.RetryRequested   += OnCardRetryRequested;
        card.DismissRequested += OnCardDismissRequested;

        item.PropertyChanged += OnItemPropertyChanged;
        _allCards[item.Id] = card;
        return card;
    }

    private async Task RemoveCardWithAnimation(DownloadItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
        if (!_allCards.TryGetValue(item.Id, out var card)) return;

        await Task.WhenAll(
            card.FadeToAsync(0, 300, Easing.CubicIn),
            card.TranslateToAsync(-50, 0, 300, Easing.CubicIn),
            card.ScaleToAsync(0.8, 300, Easing.CubicIn)
        );

        RemoveCardFromList(item.Id, card);
    }

    private void RemoveCard(DownloadItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
        if (!_allCards.TryGetValue(item.Id, out var card)) return;
        RemoveCardFromList(item.Id, card);
    }

    private void RemoveCardFromList(Guid id, DownloadCard card)
    {
        if (_completedIds.Contains(id))
            CompletedCardList.Remove(card);
        else
            OngoingCardList.Remove(card);

        _completedIds.Remove(id);
        _allCards.Remove(id);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.Status))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (sender is DownloadItem item)
                    await MoveCardIfNeeded(item);
                RefreshSummary();
            });
        }
    }

    /// <summary>
    /// Moves a card between the Ongoing and Completed lists when its status changes.
    /// </summary>
    private async Task MoveCardIfNeeded(DownloadItem item)
    {
        if (!_allCards.TryGetValue(item.Id, out var card)) return;

        bool shouldBeCompleted  = item.IsFinished;
        bool isCurrentCompleted = _completedIds.Contains(item.Id);

        if (shouldBeCompleted == isCurrentCompleted) return;

        // Animate out of current list
        await Task.WhenAll(
            card.FadeToAsync(0, 180, Easing.CubicIn),
            card.TranslateToAsync(0, -16, 180, Easing.CubicIn)
        );

        if (shouldBeCompleted)
        {
            OngoingCardList.Remove(card);
            _completedIds.Add(item.Id);
            CompletedCardList.Insert(0, card);
        }
        else
        {
            CompletedCardList.Remove(card);
            _completedIds.Remove(item.Id);
            OngoingCardList.Insert(0, card);
        }

        // Animate into new list
        card.TranslationY = -24;
        card.Scale        = 0.95;
        await Task.WhenAll(
            card.FadeToAsync(1.0, 280, Easing.CubicOut),
            card.TranslateToAsync(0, 0, 280, Easing.CubicOut),
            card.ScaleToAsync(1.0, 280, Easing.CubicOut)
        );

        RefreshEmptyState();
    }

    // ── Empty state ────────────────────────────────────────────────────────────

    private void RefreshEmptyState()
    {
        bool hasOngoing   = _allCards.Keys.Any(id => !_completedIds.Contains(id));
        bool hasCompleted = _completedIds.Count > 0;

        _ = RefreshPanelStateAsync(OngoingEmptyState,   OngoingListScroll,   hasOngoing);
        _ = RefreshPanelStateAsync(CompletedEmptyState, CompletedListScroll, hasCompleted);
    }

    private async Task RefreshPanelStateAsync(
        VerticalStackLayout emptyState, ScrollView listScroll, bool hasItems)
    {
        if (hasItems && emptyState.IsVisible)
        {
            await emptyState.FadeToAsync(0, 200);
            emptyState.IsVisible = false;
            listScroll.Opacity   = 0;
            listScroll.IsVisible = true;
            await listScroll.FadeToAsync(1.0, 300);
        }
        else if (!hasItems && !emptyState.IsVisible)
        {
            await listScroll.FadeToAsync(0, 200);
            listScroll.IsVisible    = false;
            emptyState.Opacity      = 0;
            emptyState.TranslationY = 20;
            emptyState.IsVisible    = true;
            await Task.WhenAll(
                emptyState.FadeToAsync(1.0, 400, Easing.CubicOut),
                emptyState.TranslateToAsync(0, 0, 400, Easing.CubicOut)
            );
        }
    }

    // ── Summary pill ───────────────────────────────────────────────────────────

    private async void RefreshSummary()
    {
        var all     = DownloadManager.Instance.Downloads;
        int running = all.Count(d => d.IsRunning);
        int done    = all.Count(d => d.IsDone);

        bool showPill = running > 0 || done > 0;

        DownloadsSubLabel.IsVisible = !showPill;

        if (showPill && !SummaryPill.IsVisible)
        {
            SummaryPill.Opacity      = 0;
            SummaryPill.TranslationY = -10;
            SummaryPill.IsVisible    = true;
            await Task.WhenAll(
                SummaryPill.FadeToAsync(1.0, 250, Easing.CubicOut),
                SummaryPill.TranslateToAsync(0, 0, 250, Easing.CubicOut)
            );
        }
        else if (!showPill && SummaryPill.IsVisible)
        {
            await Task.WhenAll(
                SummaryPill.FadeToAsync(0, 200, Easing.CubicIn),
                SummaryPill.TranslateToAsync(0, -10, 200, Easing.CubicIn)
            );
            SummaryPill.IsVisible = false;
        }

        RunningBadge.IsVisible = running > 0;
        RunningLabel.Text      = running == 1 ? "1 in progress" : $"{running} in progress";
        DoneBadge.IsVisible    = done > 0;
        DoneLabel.Text         = done == 1 ? "1 done" : $"{done} done";
    }

    // ── Header button handlers ─────────────────────────────────────────────────

    private async void OnCancelAllClicked(object sender, TappedEventArgs e)
    {
        var button = (Border)sender;
        await button.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await button.ScaleToAsync(1.0, 100, Easing.CubicOut);

        bool hasActive = DownloadManager.Instance.Downloads.Any(d => d.IsRunning);
        if (!hasActive) return;

        bool confirm = await DisplayAlertAsync(
            "Cancel All", "Cancel all active downloads?", "Cancel All", "Keep");

        if (confirm)
            DownloadManager.Instance.CancelAll();
    }

    private async void OnClearHistoryClicked(object sender, TappedEventArgs e)
    {
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

    // ── Card event handlers ────────────────────────────────────────────────────

    private void OnCardOptionsRequested(DownloadItem item)
        => _ = ShowOptionsSheetAsync(item);

    private void OnCardRetryRequested(DownloadItem item)
        => DownloadManager.Instance.Retry(item);

    private void OnCardDismissRequested(DownloadItem item)
        => DownloadManager.Instance.Dismiss(item);

    private async void OnCardShareRequested(DownloadItem item)
    {
        if (item.EpubPath == null || !EpubOpener.IsAccessible(item.EpubPath)) return;
        try
        {
            EpubOpener.Share(item.EpubPath, item.Title);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DownloadsPage] Share failed: {ex.Message}");
        }
    }

    private async void OnCardOpenRequested(DownloadItem item)
    {
        if (item.EpubPath == null || !EpubOpener.IsAccessible(item.EpubPath)) return;
        try
        {
            EpubOpener.Open(item.EpubPath);
        }
        catch (InvalidOperationException)
        {
            try
            {
                EpubOpener.Share(item.EpubPath, item.Title);
            }
            catch { }
        }
        catch { }
    }

    // ── Options sheet ─────────────────────────────────────────────────────────

    private async Task ShowOptionsSheetAsync(DownloadItem item)
    {
        if (_isOptionsSheetOpen || item == null)
            return;

        _isOptionsSheetOpen = true;
        _activeOptionsItem = item;
        OptionsSheetSubtitle.Text = item.Title;

        // Cancel option is visible if the item is not Completed
        OptionsSheetCancelBtn.IsVisible = item.Status != DownloadStatus.Completed;

        // Pause option is visible if the item is active (Downloading/Pending/Resuming)
        OptionsSheetPauseBtn.IsVisible = item.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Resuming;

        // Resume option is visible if the item is Paused, Failed, or Cancelled
        OptionsSheetResumeBtn.IsVisible = item.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Cancelled;

        // Copy options are only visible if original title/author have been resolved
        OptionsSheetCopyTitleBtn.IsVisible = !string.IsNullOrWhiteSpace(item.OriginalTitle);
        OptionsSheetCopyAuthorBtn.IsVisible = !string.IsNullOrWhiteSpace(item.OriginalAuthor);

        OptionsSheetOverlay.IsVisible = true;
        OptionsSheetOverlay.Opacity = 0;
        OptionsSheet.Opacity = 0;
        OptionsSheet.TranslationY = 28;

        UpdateSheetBottomMargins();

        await Task.WhenAll(
            OptionsSheetOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            OptionsSheet.FadeToAsync(1, 180, Easing.CubicOut),
            OptionsSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async Task HideOptionsSheetAsync()
    {
        if (!_isOptionsSheetOpen)
            return;

        _isOptionsSheetOpen = false;
        await Task.WhenAll(
            OptionsSheet.FadeToAsync(0, 140, Easing.CubicIn),
            OptionsSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            OptionsSheetOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        OptionsSheetOverlay.IsVisible = false;
        _activeOptionsItem = null;
    }

    private async void OnOptionsSheetOverlayTapped(object sender, TappedEventArgs e)
    {
        await HideOptionsSheetAsync();
    }

    private void OnOptionsSheetTapped(object sender, TappedEventArgs e)
    {
        // Swallow tap so overlay handler does not close it.
    }

    private async void OnOptionsSheetCloseTapped(object sender, TappedEventArgs e)
    {
        await HideOptionsSheetAsync();
    }

    private async void OnOptionsSheetCopyTitleTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        string title = string.IsNullOrEmpty(_activeOptionsItem.OriginalTitle) ? _activeOptionsItem.Title : _activeOptionsItem.OriginalTitle;
        await HideOptionsSheetAsync();
        if (!string.IsNullOrEmpty(title))
        {
            try
            {
                await Clipboard.Default.SetTextAsync(title);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DownloadsPage] Copy title failed: {ex.Message}");
            }
        }
    }

    private async void OnOptionsSheetCopyAuthorTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        string author = string.IsNullOrEmpty(_activeOptionsItem.OriginalAuthor) ? _activeOptionsItem.Author : _activeOptionsItem.OriginalAuthor;
        await HideOptionsSheetAsync();
        if (!string.IsNullOrEmpty(author))
        {
            try
            {
                await Clipboard.Default.SetTextAsync(author);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DownloadsPage] Copy author failed: {ex.Message}");
            }
        }
    }

    private async void OnOptionsSheetCancelTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        DownloadManager.Instance.Cancel(item);
    }

    private async void OnOptionsSheetPauseTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        DownloadManager.Instance.Pause(item);
    }

    private async void OnOptionsSheetResumeTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        DownloadManager.Instance.Resume(item);
    }

    private void UpdateSheetBottomMargins()
    {
        double bottomInset = 16;
#if ANDROID
        if (MainActivity.Instance is { } activity)
            bottomInset = Math.Max(bottomInset, activity.GetOverlayBottomInsetDip(14));
#endif

        OptionsSheet.Margin = new Thickness(12, 0, 12, bottomInset);
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        UpdateSheetBottomMargins();
    }
}
