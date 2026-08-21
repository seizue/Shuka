using System.Collections.Specialized;
using System.ComponentModel;
using Shuka.Android.Behaviors;
using Shuka.Android.Platforms.Android;
using Shuka.Android.Services;

namespace Shuka.Android.Pages;

public partial class DownloadsPage : ContentPage
{
    public enum DownloadCategory
    {
        Downloading,
        Queued,
        Paused,
        Completed,
        Failed
    }

    private const string PrefKeyLastCategory = "last_download_category";

    private DownloadCategory _activeCategory = DownloadCategory.Downloading;
    private bool _isOptionsSheetOpen;
    private DownloadItem? _activeOptionsItem;
    private bool _isPageActionsSheetOpen;
    private bool _isCategoryPickerOpen;
    private bool _isSortSheetOpen;
    private System.Threading.CancellationTokenSource? _refreshCts;
    private List<Guid>? _currentFilteredIds;

    public DownloadsPage()
    {
        InitializeComponent();

        _activeCategory = (DownloadCategory)Preferences.Default.Get(PrefKeyLastCategory, (int)DownloadCategory.Queued);

        DownloadManager.Instance.Downloads.CollectionChanged += OnCollectionChanged;

        foreach (var item in DownloadManager.Instance.Downloads)
            item.PropertyChanged += OnItemPropertyChanged;

        // Apply initial selector UI
        UpdateCategorySelectorUI(_activeCategory);
        RefreshUI(immediate: true);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        MainActivity.Instance?.SetTabBarVisible(true);

        UpdateCategorySelectorUI(_activeCategory);
        RefreshUI(immediate: true);

        TabTransition.Prepare(RootGrid, myTabIndex: 1);
        await TabTransition.SlideInAsync(RootGrid);
    }

    // ── Collection / Property Changes ──────────────────────────────────────────

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (DownloadItem item in e.OldItems)
                item.PropertyChanged -= OnItemPropertyChanged;
        }
        if (e.NewItems != null)
        {
            foreach (DownloadItem item in e.NewItems)
                item.PropertyChanged += OnItemPropertyChanged;
        }

        MainThread.BeginInvokeOnMainThread(() => RefreshUI(immediate: false));
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.Status))
        {
            MainThread.BeginInvokeOnMainThread(() => RefreshUI(immediate: false));
        }
    }

    // ── UI Refreshing & Filtering ──────────────────────────────────────────────

    private void RefreshUI(bool immediate = false)
    {
        _refreshCts?.Cancel();
        _refreshCts = null;

        if (immediate)
        {
            UpdateCategoryCounts();
            RefreshSummary();
            _ = FilterListAsync(_activeCategory);
            return;
        }

        var cts = new System.Threading.CancellationTokenSource();
        _refreshCts = cts;
        var token = cts.Token;

        Task.Delay(100, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (token.IsCancellationRequested) return;
                UpdateCategoryCounts();
                RefreshSummary();
                _ = FilterListAsync(_activeCategory);
            });
        });
    }

    private void UpdateCategoryCounts()
    {
        var all = DownloadManager.Instance.Downloads;

        int downloading = all.Count(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Resuming);
        int queued      = all.Count(d => d.Status == DownloadStatus.Pending);
        int paused      = all.Count(d => d.Status == DownloadStatus.Paused);
        int completed   = all.Count(d => d.Status == DownloadStatus.Completed);
        int failed      = all.Count(d => d.Status is DownloadStatus.Failed or DownloadStatus.Cancelled);

        // Update picker sheet counts
        CategoryPickerDownloadingCount.Text = downloading.ToString();
        CategoryPickerQueuedCount.Text      = queued.ToString();
        CategoryPickerPausedCount.Text      = paused.ToString();
        CategoryPickerCompletedCount.Text   = completed.ToString();
        CategoryPickerFailedCount.Text      = failed.ToString();

        // Update selector badge with the active category's count
        int activeCount = _activeCategory switch
        {
            DownloadCategory.Downloading => downloading,
            DownloadCategory.Queued      => queued,
            DownloadCategory.Paused      => paused,
            DownloadCategory.Completed   => completed,
            DownloadCategory.Failed      => failed,
            _                            => 0
        };
        CategorySelectorCount.Text      = activeCount.ToString();
        CategorySelectorBadge.IsVisible = activeCount > 0;
    }

    private async Task FilterListAsync(DownloadCategory category)
    {
        var filteredList = await Task.Run(() =>
        {
            var all = DownloadManager.Instance.Downloads.ToList();
            return category switch
            {
                DownloadCategory.Downloading => all.Where(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Resuming).ToList(),
                DownloadCategory.Queued      => all.Where(d => d.Status == DownloadStatus.Pending).ToList(),
                DownloadCategory.Paused      => all.Where(d => d.Status == DownloadStatus.Paused).ToList(),
                DownloadCategory.Completed   => all.Where(d => d.Status == DownloadStatus.Completed).ToList(),
                DownloadCategory.Failed      => all.Where(d => d.Status is DownloadStatus.Failed or DownloadStatus.Cancelled).ToList(),
                _ => new List<DownloadItem>()
            };
        });

        MainThread.BeginInvokeOnMainThread(() =>
        {
            bool isIdentical = _currentFilteredIds != null && _currentFilteredIds.Count == filteredList.Count;
            if (isIdentical)
            {
                for (int i = 0; i < filteredList.Count; i++)
                {
                    if (_currentFilteredIds![i] != filteredList[i].Id)
                    {
                        isIdentical = false;
                        break;
                    }
                }
            }

            if (!isIdentical)
            {
                _currentFilteredIds = filteredList.Select(x => x.Id).ToList();
                DownloadsCollectionView.ItemsSource = filteredList;
            }

            bool hasItems = filteredList.Count > 0;
            EmptyStateLayout.IsVisible = !hasItems;
            DownloadsCollectionView.IsVisible = hasItems;

            if (!hasItems)
            {
                UpdateEmptyState(category);
            }
        });
    }

    private void UpdateEmptyState(DownloadCategory category)
    {
        switch (category)
        {
            case DownloadCategory.Downloading:
                EmptyStateIcon.Text = "\uE2C4"; // download
                EmptyStateTitle.Text = "No active downloads";
                EmptyStateSubtitle.Text = "Start a download from the Home tab";
                break;
            case DownloadCategory.Queued:
                EmptyStateIcon.Text = "\uE8B6"; // schedule/pending
                EmptyStateTitle.Text = "No queued downloads";
                EmptyStateSubtitle.Text = "Novels waiting for a slot appear here";
                break;
            case DownloadCategory.Paused:
                EmptyStateIcon.Text = "\uE034"; // pause
                EmptyStateTitle.Text = "No paused downloads";
                EmptyStateSubtitle.Text = "Paused downloads appear here";
                break;
            case DownloadCategory.Completed:
                EmptyStateIcon.Text = "\uE876"; // check/completed
                EmptyStateTitle.Text = "No completed downloads";
                EmptyStateSubtitle.Text = "Finished novels will appear here";
                break;
            case DownloadCategory.Failed:
                EmptyStateIcon.Text = "\uE5CD"; // close/failed
                EmptyStateTitle.Text = "No failed downloads";
                EmptyStateSubtitle.Text = "Failed or cancelled jobs appear here";
                break;
        }
    }

    private void UpdateCategorySelectorUI(DownloadCategory category)
    {
        (string icon, string label) = category switch
        {
            DownloadCategory.Downloading => ("\uE2C4", "Downloading"),
            DownloadCategory.Queued      => ("\uE8B6", "Queued"),
            DownloadCategory.Paused      => ("\uE034", "Paused"),
            DownloadCategory.Completed   => ("\uE876", "Completed"),
            DownloadCategory.Failed      => ("\uE5CD", "Failed / Cancelled"),
            _                            => ("\uE8B6", "Queued")
        };
        CategorySelectorIcon.Text = icon;
        CategorySelectorText.Text = label;

        Color accentContainer = (Color)(Application.Current!.Resources["AccentContainer"]);
        Color accentLight     = (Color)(Application.Current!.Resources["AccentLight"]);
        Color bgInput         = (Color)(Application.Current!.Resources["BgInput"]);
        Color stroke          = (Color)(Application.Current!.Resources["Stroke"]);

        void SetPickerRow(Border btn, Label check, bool isActive)
        {
            btn.BackgroundColor = isActive ? accentContainer : bgInput;
            btn.Stroke          = isActive ? accentLight : stroke;
            check.IsVisible     = isActive;
        }

        SetPickerRow(CategoryPickerDownloadingBtn, CategoryPickerDownloadingCheck, category == DownloadCategory.Downloading);
        SetPickerRow(CategoryPickerQueuedBtn,      CategoryPickerQueuedCheck,      category == DownloadCategory.Queued);
        SetPickerRow(CategoryPickerPausedBtn,      CategoryPickerPausedCheck,      category == DownloadCategory.Paused);
        SetPickerRow(CategoryPickerCompletedBtn,   CategoryPickerCompletedCheck,   category == DownloadCategory.Completed);
        SetPickerRow(CategoryPickerFailedBtn,      CategoryPickerFailedCheck,      category == DownloadCategory.Failed);
    }

    private void RefreshSummary()
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
            _ = Task.WhenAll(
                SummaryPill.FadeToAsync(1.0, 250, Easing.CubicOut),
                SummaryPill.TranslateToAsync(0, 0, 250, Easing.CubicOut)
            );
        }
        else if (!showPill && SummaryPill.IsVisible)
        {
            _ = Task.WhenAll(
                SummaryPill.FadeToAsync(0, 200, Easing.CubicIn),
                SummaryPill.TranslateToAsync(0, -10, 200, Easing.CubicIn)
            ).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() => SummaryPill.IsVisible = false));
        }

        RunningBadge.IsVisible = running > 0;
        RunningLabel.Text      = running == 1 ? "1 in progress" : $"{running} in progress";
        DoneBadge.IsVisible    = done > 0;
        DoneLabel.Text         = done == 1 ? "1 done" : $"{done} done";
    }

    // ── Category Tab Switch Taps ────────────────────────────────────────────────

    // ── Category Selector Tap ──────────────────────────────────────────────────

    private void OnCategorySelectorTapped(object sender, TappedEventArgs e) => _ = ShowCategoryPickerAsync();

    private void SwitchCategory(DownloadCategory category)
    {
        if (_activeCategory == category) return;
        _activeCategory = category;
        Preferences.Default.Set(PrefKeyLastCategory, (int)category);

        UpdateCategorySelectorUI(category);
        RefreshUI(immediate: true);
    }

    // ── Bubbled Events from DownloadCard ───────────────────────────────────────

    internal void HandleOptionsRequested(DownloadItem item) => _ = ShowOptionsSheetAsync(item);
    internal void HandleRetryRequested(DownloadItem item) => DownloadManager.Instance.Retry(item);
    internal void HandleDismissRequested(DownloadItem item) => DownloadManager.Instance.Dismiss(item);

    internal void HandleShareRequested(DownloadItem item)
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

    internal void HandleOpenRequested(DownloadItem item)
    {
        string? epubPath = EpubOpener.ResolveAccessiblePath(item.EpubPath, item.Title, item.Url);
        if (epubPath == null) return;

        epubPath = EpubOpener.PreferFilesystemPath(epubPath);
        System.Diagnostics.Debug.WriteLine($"[DownloadsPage] Opening existing EPUB at: {epubPath} for '{item.Title}'");
        try
        {
            EpubOpener.Open(epubPath);
        }
        catch (InvalidOperationException)
        {
            try
            {
                EpubOpener.Share(epubPath, item.Title);
            }
            catch { }
        }
        catch { }
    }

    // ── Individual Card Options Sheet ──────────────────────────────────────────

    private async Task ShowOptionsSheetAsync(DownloadItem item)
    {
        if (_isOptionsSheetOpen || item == null)
            return;

        _isOptionsSheetOpen = true;
        _activeOptionsItem = item;
        OptionsSheetSubtitle.Text = item.Title;

        // Options visibility configurations
        bool isCompleted = item.Status == DownloadStatus.Completed;
        OptionsSheetOpenBtn.IsVisible = isCompleted;
        OptionsSheetShareBtn.IsVisible = isCompleted;
        OptionsSheetDismissBtn.IsVisible = isCompleted || item.Status == DownloadStatus.Failed || item.Status == DownloadStatus.Cancelled;

        OptionsSheetCancelBtn.IsVisible = item.Status != DownloadStatus.Completed;
        OptionsSheetPauseBtn.IsVisible = item.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Resuming;
        OptionsSheetResumeBtn.IsVisible = item.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Cancelled;

        OptionsSheetCopyTitleBtn.IsVisible = !string.IsNullOrWhiteSpace(item.OriginalTitle);
        OptionsSheetCopyAuthorBtn.IsVisible = !string.IsNullOrWhiteSpace(item.OriginalAuthor);

        // Queue reordering actions (only for queued/Pending novels)
        bool isQueued = item.Status == DownloadStatus.Pending;
        OptionsSheetMoveToTopBtn.IsVisible = isQueued;
        OptionsSheetMoveUpBtn.IsVisible = isQueued;
        OptionsSheetMoveDownBtn.IsVisible = isQueued;
        OptionsSheetMoveToBottomBtn.IsVisible = isQueued;

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

    private async void OnOptionsSheetOverlayTapped(object sender, TappedEventArgs e) => await HideOptionsSheetAsync();
    private void OnOptionsSheetTapped(object sender, TappedEventArgs e) { }
    private async void OnOptionsSheetCloseTapped(object sender, TappedEventArgs e) => await HideOptionsSheetAsync();

    private async void OnOptionsSheetOpenTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        HandleOpenRequested(item);
    }

    private async void OnOptionsSheetShareTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        HandleShareRequested(item);
    }

    private async void OnOptionsSheetDismissTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        DownloadManager.Instance.Dismiss(item);
    }

    private async void OnOptionsSheetCopyTitleTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        string title = string.IsNullOrEmpty(_activeOptionsItem.OriginalTitle) ? _activeOptionsItem.Title : _activeOptionsItem.OriginalTitle;
        await HideOptionsSheetAsync();
        if (!string.IsNullOrEmpty(title))
        {
            try { await Clipboard.Default.SetTextAsync(title); } catch { }
        }
    }

    private async void OnOptionsSheetCopyAuthorTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        string author = string.IsNullOrEmpty(_activeOptionsItem.OriginalAuthor) ? _activeOptionsItem.Author : _activeOptionsItem.OriginalAuthor;
        await HideOptionsSheetAsync();
        if (!string.IsNullOrEmpty(author))
        {
            try { await Clipboard.Default.SetTextAsync(author); } catch { }
        }
    }

    private async void OnOptionsSheetCancelTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        DownloadManager.Instance.Cancel(item);
    }

    private async void OnOptionsSheetCopyLogTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        string logText = _activeOptionsItem.LogText;
        await HideOptionsSheetAsync();
        if (!string.IsNullOrWhiteSpace(logText))
        {
            try
            {
                await Clipboard.Default.SetTextAsync(logText);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DownloadsPage] Copy log error: {ex.Message}");
            }
        }
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

    private async void OnOptionsSheetMoveToTopTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        DownloadManager.Instance.MoveToTop(item);
    }

    private async void OnOptionsSheetMoveUpTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        DownloadManager.Instance.MoveUp(item);
    }

    private async void OnOptionsSheetMoveDownTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        DownloadManager.Instance.MoveDown(item);
    }

    private async void OnOptionsSheetMoveToBottomTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsItem == null) return;
        var item = _activeOptionsItem;
        await HideOptionsSheetAsync();
        DownloadManager.Instance.MoveToBottom(item);
    }

    // ── Page Actions Menu Sheet (Three-dot) ────────────────────────────────────

    private void OnPageActionsClicked(object sender, TappedEventArgs e) => _ = ShowPageActionsSheetAsync();

    private async Task ShowPageActionsSheetAsync()
    {
        if (_isPageActionsSheetOpen)
            return;

        _isPageActionsSheetOpen = true;

        PageActionsSheetOverlay.IsVisible = true;
        PageActionsSheetOverlay.Opacity = 0;
        PageActionsSheet.Opacity = 0;
        PageActionsSheet.TranslationY = 28;

        UpdatePageSheetBottomMargins();

        await Task.WhenAll(
            PageActionsSheetOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            PageActionsSheet.FadeToAsync(1, 180, Easing.CubicOut),
            PageActionsSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async Task HidePageActionsSheetAsync()
    {
        if (!_isPageActionsSheetOpen)
            return;

        _isPageActionsSheetOpen = false;
        await Task.WhenAll(
            PageActionsSheet.FadeToAsync(0, 140, Easing.CubicIn),
            PageActionsSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            PageActionsSheetOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        PageActionsSheetOverlay.IsVisible = false;
    }

    private async void OnPageActionsSheetOverlayTapped(object sender, TappedEventArgs e) => await HidePageActionsSheetAsync();
    private void OnPageActionsSheetTapped(object sender, TappedEventArgs e) { }
    private async void OnPageActionsSheetCloseTapped(object sender, TappedEventArgs e) => await HidePageActionsSheetAsync();

    private async void OnPageActionPauseAllTapped(object sender, TappedEventArgs e)
    {
        await HidePageActionsSheetAsync();
        DownloadManager.Instance.PauseAll();
    }

    private async void OnPageActionResumeAllTapped(object sender, TappedEventArgs e)
    {
        await HidePageActionsSheetAsync();
        DownloadManager.Instance.ResumeAll();
    }

    private async void OnPageActionCancelAllTapped(object sender, TappedEventArgs e)
    {
        await HidePageActionsSheetAsync();

        bool hasActive = DownloadManager.Instance.Downloads.Any(d => d.IsRunning);
        if (!hasActive) return;

        bool confirm = await DisplayAlertAsync(
            "Cancel All", "Cancel all active downloads?", "Cancel All", "Keep");

        if (confirm)
            DownloadManager.Instance.CancelAll();
    }

    private async void OnPageActionClearCompletedTapped(object sender, TappedEventArgs e)
    {
        await HidePageActionsSheetAsync();

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

    private async void OnPageActionSortTapped(object sender, TappedEventArgs e)
    {
        await HidePageActionsSheetAsync();
        await ShowSortSheetAsync();
    }

    // ── Sort Sheet ─────────────────────────────────────────────────────────────

    private async Task ShowSortSheetAsync()
    {
        if (_isSortSheetOpen) return;
        _isSortSheetOpen = true;

        double bottomInset = 16;
#if ANDROID
        if (MainActivity.Instance is { } activity)
            bottomInset = Math.Max(bottomInset, activity.GetOverlayBottomInsetDip(14));
#endif
        SortSheet.Margin = new Thickness(12, 0, 12, bottomInset);

        SortSheetOverlay.IsVisible = true;
        SortSheetOverlay.Opacity   = 0;
        SortSheet.Opacity     = 0;
        SortSheet.TranslationY = 28;

        await Task.WhenAll(
            SortSheetOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            SortSheet.FadeToAsync(1, 180, Easing.CubicOut),
            SortSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async Task HideSortSheetAsync()
    {
        if (!_isSortSheetOpen) return;
        _isSortSheetOpen = false;
        await Task.WhenAll(
            SortSheet.FadeToAsync(0, 140, Easing.CubicIn),
            SortSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            SortSheetOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        SortSheetOverlay.IsVisible = false;
    }

    private async void OnSortSheetOverlayTapped(object sender, TappedEventArgs e) => await HideSortSheetAsync();
    private void OnSortSheetTapped(object sender, TappedEventArgs e) { }
    private async void OnSortSheetCloseTapped(object sender, TappedEventArgs e) => await HideSortSheetAsync();

    private async void OnSortNewestTapped(object sender, TappedEventArgs e)
    {
        await HideSortSheetAsync();
        DownloadManager.Instance.Sort("Date Added (Newest)");
    }

    private async void OnSortOldestTapped(object sender, TappedEventArgs e)
    {
        await HideSortSheetAsync();
        DownloadManager.Instance.Sort("Date Added (Oldest)");
    }

    private async void OnSortTitleAZTapped(object sender, TappedEventArgs e)
    {
        await HideSortSheetAsync();
        DownloadManager.Instance.Sort("Title (A-Z)");
    }

    private async void OnSortTitleZATapped(object sender, TappedEventArgs e)
    {
        await HideSortSheetAsync();
        DownloadManager.Instance.Sort("Title (Z-A)");
    }

    private async void OnSortProgressHighestTapped(object sender, TappedEventArgs e)
    {
        await HideSortSheetAsync();
        DownloadManager.Instance.Sort("Progress (Highest)");
    }

    private async void OnSortProgressLowestTapped(object sender, TappedEventArgs e)
    {
        await HideSortSheetAsync();
        DownloadManager.Instance.Sort("Progress (Lowest)");
    }

    // ── Category Picker Sheet ──────────────────────────────────────────────────

    private async Task ShowCategoryPickerAsync()
    {
        if (_isCategoryPickerOpen) return;
        _isCategoryPickerOpen = true;

        double bottomInset = 16;
#if ANDROID
        if (MainActivity.Instance is { } activity)
            bottomInset = Math.Max(bottomInset, activity.GetOverlayBottomInsetDip(14));
#endif
        CategoryPickerSheet.Margin = new Thickness(12, 0, 12, bottomInset);

        CategoryPickerOverlay.IsVisible = true;
        CategoryPickerOverlay.Opacity   = 0;
        CategoryPickerSheet.Opacity     = 0;
        CategoryPickerSheet.TranslationY = 28;

        await Task.WhenAll(
            CategoryPickerOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            CategoryPickerSheet.FadeToAsync(1, 180, Easing.CubicOut),
            CategoryPickerSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async Task HideCategoryPickerAsync()
    {
        if (!_isCategoryPickerOpen) return;
        _isCategoryPickerOpen = false;
        await Task.WhenAll(
            CategoryPickerSheet.FadeToAsync(0, 140, Easing.CubicIn),
            CategoryPickerSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            CategoryPickerOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        CategoryPickerOverlay.IsVisible = false;
    }

    private async void OnCategoryPickerOverlayTapped(object sender, TappedEventArgs e) => await HideCategoryPickerAsync();
    private void OnCategoryPickerSheetTapped(object sender, TappedEventArgs e) { }
    private async void OnCategoryPickerCloseTapped(object sender, TappedEventArgs e) => await HideCategoryPickerAsync();

    private async void OnCategoryPickerDownloadingTapped(object sender, TappedEventArgs e)
    {
        await HideCategoryPickerAsync();
        SwitchCategory(DownloadCategory.Downloading);
    }

    private async void OnCategoryPickerQueuedTapped(object sender, TappedEventArgs e)
    {
        await HideCategoryPickerAsync();
        SwitchCategory(DownloadCategory.Queued);
    }

    private async void OnCategoryPickerPausedTapped(object sender, TappedEventArgs e)
    {
        await HideCategoryPickerAsync();
        SwitchCategory(DownloadCategory.Paused);
    }

    private async void OnCategoryPickerCompletedTapped(object sender, TappedEventArgs e)
    {
        await HideCategoryPickerAsync();
        SwitchCategory(DownloadCategory.Completed);
    }

    private async void OnCategoryPickerFailedTapped(object sender, TappedEventArgs e)
    {
        await HideCategoryPickerAsync();
        SwitchCategory(DownloadCategory.Failed);
    }

    // ── Layout Margin/Inset Calculations ───────────────────────────────────────

    private void UpdateSheetBottomMargins()
    {
        double bottomInset = 16;
#if ANDROID
        if (MainActivity.Instance is { } activity)
            bottomInset = Math.Max(bottomInset, activity.GetOverlayBottomInsetDip(14));
#endif
        OptionsSheet.Margin = new Thickness(12, 0, 12, bottomInset);
        if (SortSheet != null)
            SortSheet.Margin = new Thickness(12, 0, 12, bottomInset);
    }

    private void UpdatePageSheetBottomMargins()
    {
        double bottomInset = 16;
#if ANDROID
        if (MainActivity.Instance is { } activity)
            bottomInset = Math.Max(bottomInset, activity.GetOverlayBottomInsetDip(14));
#endif
        PageActionsSheet.Margin = new Thickness(12, 0, 12, bottomInset);
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        UpdateSheetBottomMargins();
        UpdatePageSheetBottomMargins();
    }
}
