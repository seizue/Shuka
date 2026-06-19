using System.Collections.Specialized;
using Shuka.Android.Behaviors;
using Shuka.Android.Platforms.Android;
using Shuka.Android.Services;

namespace Shuka.Android.Pages;

public partial class HistoryPage : ContentPage
{
    private const string PrefKeyViewMode = "history_view_mode";
    private const int ItemsPerBatch = 20; // Load 20 items per scroll batch for infinite loading

    private readonly Dictionary<Guid, HistoryCard> _cards = new();
    private string _searchQuery = "";

    private enum SortField { Date, Title, Author }
    private SortField _sortField     = SortField.Date;
    private bool      _sortAscending = false; // date defaults to newest-first

    private bool _isOptionsSheetOpen;
    private HistoryEntry? _activeOptionsEntry;
    private bool _isCompactView;
    private double _lastWidth = -1;

    // Pagination state
    private bool _isLoading = false;
    private int _currentLoadedCount = 0;
    private List<HistoryEntry> _filteredAndSortedEntries = new();
    private CancellationTokenSource? _searchDebounceTokenSource;

    public HistoryPage()
    {
        InitializeComponent();
        HistoryService.Instance.Entries.CollectionChanged += OnCollectionChanged;

        _isCompactView = Preferences.Default.Get(PrefKeyViewMode, false);
        RefreshToggleViewPill();

        RefreshSortPills();
        
        // Setup scroll view listeners for pagination
        ListScroll.Scrolled += OnScrolled;
        GridScroll.Scrolled += OnScrolled;
        
        // Show loading skeleton initially
        ShowLoadingSkeleton();
        
        // Load initial batch
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Task.Delay(100); // Brief delay for smooth animation
            await LoadInitialBatch();
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        MainActivity.Instance?.SetTabBarVisible(true);
        TabTransition.Prepare(RootGrid, myTabIndex: 2);
        await TabTransition.SlideInAsync(RootGrid);
    }

    private async Task AnimateIn()
    {
        RootGrid.Opacity      = 1;
        RootGrid.TranslationY = 0;
        await Task.CompletedTask;
    }

    // ── Loading & Pagination ──────────────────────────────────────────────────

    private void ShowLoadingSkeleton()
    {
        LoadingSkeletonView.IsVisible = true;
        ListScroll.IsVisible = false;
        GridScroll.IsVisible = false;
        EmptyState.IsVisible = false;
        NoResultsState.IsVisible = false;
    }

    private async Task HideLoadingSkeleton()
    {
        if (LoadingSkeletonView.IsVisible)
        {
            await LoadingSkeletonView.FadeOut();
        }
    }

    private async Task LoadInitialBatch()
    {
        if (_isLoading) return;

        _isLoading = true;
        _currentLoadedCount = 0;

        try
        {
            // Get sorted and filtered entries
            var sorted = GetSortedEntries();
            var filtered = string.IsNullOrEmpty(_searchQuery)
                ? sorted
                : sorted.Where(e =>
                    e.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                    e.Author.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));

            _filteredAndSortedEntries = filtered.ToList();

            // Build cards for first batch (infinite loading starts here)
            int itemsToLoad = Math.Min(ItemsPerBatch, _filteredAndSortedEntries.Count);
            for (int i = 0; i < itemsToLoad; i++)
            {
                var entry = _filteredAndSortedEntries[i];
                if (!_cards.ContainsKey(entry.Id))
                {
                    var card = BuildCard(entry);
                    _cards[entry.Id] = card;
                }
            }

            _currentLoadedCount = itemsToLoad;

            // Hide skeleton and show results
            await HideLoadingSkeleton();
            UpdateCountLabel();
            RenderCurrentBatch();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task LoadMoreItems()
    {
        if (_isLoading || _currentLoadedCount >= _filteredAndSortedEntries.Count)
            return;

        _isLoading = true;

        try
        {
            // Infinite loading: load next batch as user scrolls
            int itemsToLoad = Math.Min(ItemsPerBatch, _filteredAndSortedEntries.Count - _currentLoadedCount);
            int startIndex = _currentLoadedCount;

            // Create cards in smaller chunks to avoid UI thread blocking
            for (int i = 0; i < itemsToLoad; i++)
            {
                var entry = _filteredAndSortedEntries[startIndex + i];
                if (!_cards.ContainsKey(entry.Id))
                {
                    var card = BuildCard(entry);
                    _cards[entry.Id] = card;
                }
                
                // Yield to UI thread every 5 items to prevent blocking
                if ((i + 1) % 5 == 0)
                {
                    await Task.Delay(1);
                }
            }

            _currentLoadedCount += itemsToLoad;
            
            // Add new items to the view
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                AppendCurrentBatch(startIndex, itemsToLoad);
            });
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void OnScrolled(object? sender, ScrolledEventArgs e)
    {
        if (_isLoading || _currentLoadedCount >= _filteredAndSortedEntries.Count)
            return;

        var scrollView = sender as ScrollView;
        if (scrollView == null) return;

        // Calculate if we're near the bottom
        double scrollingSpace = scrollView.ContentSize.Height - scrollView.Height;
        double threshold = scrollingSpace * 0.8; // Load when 80% scrolled

        if (e.ScrollY >= threshold)
        {
            _ = LoadMoreItems();
        }
    }

    // ── Collection changes ────────────────────────────────────────────────────

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // Reload from scratch when collection changes
            _cards.Clear();
            await LoadInitialBatch();
        });
    }

    private void AddCard(HistoryEntry entry)
    {
        if (_cards.ContainsKey(entry.Id)) return;
        var card = BuildCard(entry);
        _cards[entry.Id] = card;
    }

    private HistoryCard BuildCard(HistoryEntry entry)
    {
        var card = new HistoryCard(entry, _isCompactView);
        card.OpenRequested    += OnOpenRequested;
        card.OptionsRequested += OnOptionsRequested;
        return card;
    }

    // ── Sort ──────────────────────────────────────────────────────────────────

    private async void OnSortDateTapped(object sender, TappedEventArgs e)
    {
        if (_sortField == SortField.Date)
            _sortAscending = !_sortAscending;
        else
        {
            _sortField     = SortField.Date;
            _sortAscending = false; // newest first by default
        }
        RefreshSortPills();
        ShowLoadingSkeleton();
        await LoadInitialBatch();
    }

    private async void OnSortTitleTapped(object sender, TappedEventArgs e)
    {
        if (_sortField == SortField.Title)
            _sortAscending = !_sortAscending;
        else
        {
            _sortField     = SortField.Title;
            _sortAscending = true; // A→Z by default
        }
        RefreshSortPills();
        ShowLoadingSkeleton();
        await LoadInitialBatch();
    }

    private async void OnSortAuthorTapped(object sender, TappedEventArgs e)
    {
        if (_sortField == SortField.Author)
            _sortAscending = !_sortAscending;
        else
        {
            _sortField     = SortField.Author;
            _sortAscending = true; // A→Z by default
        }
        RefreshSortPills();
        ShowLoadingSkeleton();
        await LoadInitialBatch();
    }

    private void RefreshSortPills()
    {
        SetPill(SortDatePill,   SortDateIcon,   SortDateLabel,   SortDateArrow,   SortField.Date);
        SetPill(SortTitlePill,  SortTitleIcon,  SortTitleLabel,  SortTitleArrow,  SortField.Title);
        SetPill(SortAuthorPill, SortAuthorIcon, SortAuthorLabel, SortAuthorArrow, SortField.Author);
    }

    private void SetPill(Border pill, Label icon, Label label, Label arrow, SortField field)
    {
        bool active = _sortField == field;

        if (active)
        {
            pill.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            pill.SetDynamicResource(Border.StrokeProperty, "AccentLight");
            icon.SetDynamicResource(Label.TextColorProperty, "AccentLight");
            label.SetDynamicResource(Label.TextColorProperty, "AccentLight");
            arrow.IsVisible = true;
            arrow.Text = _sortAscending ? "\uE5C7" : "\uE5C5"; // arrow_drop_up / arrow_drop_down
            arrow.SetDynamicResource(Label.TextColorProperty, "AccentLight");
        }
        else
        {
            pill.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
            pill.SetDynamicResource(Border.StrokeProperty, "Stroke");
            icon.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            label.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            arrow.IsVisible = false;
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = e.NewTextValue?.Trim() ?? "";
        ClearSearchBtn.IsVisible = !string.IsNullOrEmpty(_searchQuery);
        
        // Cancel any pending search
        _searchDebounceTokenSource?.Cancel();
        _searchDebounceTokenSource = new CancellationTokenSource();
        var token = _searchDebounceTokenSource.Token;
        
        try
        {
            // Debounce search for 300ms
            await Task.Delay(300, token);
            
            if (!token.IsCancellationRequested)
            {
                ShowLoadingSkeleton();
                await LoadInitialBatch();
            }
        }
        catch (TaskCanceledException)
        {
            // Search was cancelled, ignore
        }
    }

    private async void OnClearSearchTapped(object sender, TappedEventArgs e)
    {
        _searchQuery = "";
        SearchEntry.Text = "";
        ClearSearchBtn.IsVisible = false;
        
        // Cancel any pending search
        _searchDebounceTokenSource?.Cancel();
        
        ShowLoadingSkeleton();
        await LoadInitialBatch();
    }

    // ── Filter + Sort ─────────────────────────────────────────────────────────

    private void RenderCurrentBatch()
    {
        bool hasEntries  = HistoryService.Instance.Entries.Count > 0;
        bool isSearching = !string.IsNullOrEmpty(_searchQuery);

        if (!hasEntries)
        {
            EmptyState.IsVisible     = true;
            NoResultsState.IsVisible = false;
            ListScroll.IsVisible     = false;
            GridScroll.IsVisible     = false;
            LoadingSkeletonView.IsVisible = false;
            return;
        }

        double width = _lastWidth;
        if (width <= 0)
        {
            width = DeviceDisplay.Current.MainDisplayInfo.Width / DeviceDisplay.Current.MainDisplayInfo.Density;
        }

        double cardWidth = 80;
        double cardHeight = 120;
        if (width > 48)
        {
            cardWidth = (width - 48) / 4;
            cardHeight = cardWidth * 1.5;
        }

        // Use BeginUpdate/EndUpdate pattern for better performance
        try
        {
            // Clear and rebuild - but only for items we've loaded so far
            CardList.Clear();
            CardGrid.Children.Clear();
            CardGrid.RowDefinitions.Clear();

            int index = 0;
            int itemsToRender = Math.Min(_currentLoadedCount, _filteredAndSortedEntries.Count);
            
            // Batch add items for better performance
            for (int i = 0; i < itemsToRender; i++)
            {
                var entry = _filteredAndSortedEntries[i];
                if (_cards.TryGetValue(entry.Id, out var card))
                {
                    if (_isCompactView)
                    {
                        card.WidthRequest = cardWidth;
                        card.HeightRequest = cardHeight;

                        int row = index / 4;
                        int col = index % 4;

                        if (col == 0)
                        {
                            CardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        }

                        Grid.SetColumn(card, col);
                        Grid.SetRow(card, row);
                        CardGrid.Children.Add(card);
                    }
                    else
                    {
                        card.WidthRequest = -1;
                        card.HeightRequest = -1;
                        CardList.Add(card);
                    }
                    index++;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HistoryPage] RenderCurrentBatch error: {ex.Message}");
        }

        int visibleCount = _filteredAndSortedEntries.Count;

        EmptyState.IsVisible     = false;
        LoadingSkeletonView.IsVisible = false;
        ListScroll.IsVisible     = visibleCount > 0 && !_isCompactView;
        GridScroll.IsVisible     = visibleCount > 0 && _isCompactView;
        NoResultsState.IsVisible = visibleCount == 0;

        if (visibleCount == 0 && isSearching)
            NoResultsLabel.Text = $"No results for \"{_searchQuery}\"";
    }

    private void AppendCurrentBatch(int startIndex, int count)
    {
        double width = _lastWidth;
        if (width <= 0)
        {
            width = DeviceDisplay.Current.MainDisplayInfo.Width / DeviceDisplay.Current.MainDisplayInfo.Density;
        }

        double cardWidth = 80;
        double cardHeight = 120;
        if (width > 48)
        {
            cardWidth = (width - 48) / 4;
            cardHeight = cardWidth * 1.5;
        }

        try
        {
            for (int i = 0; i < count; i++)
            {
                int index = startIndex + i;
                if (index >= _filteredAndSortedEntries.Count) break;

                var entry = _filteredAndSortedEntries[index];
                if (_cards.TryGetValue(entry.Id, out var card))
                {
                    if (_isCompactView)
                    {
                        card.WidthRequest = cardWidth;
                        card.HeightRequest = cardHeight;

                        int row = index / 4;
                        int col = index % 4;

                        // Add new row definition if we started a new row
                        while (CardGrid.RowDefinitions.Count <= row)
                        {
                            CardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        }

                        Grid.SetColumn(card, col);
                        Grid.SetRow(card, row);
                        CardGrid.Children.Add(card);
                    }
                    else
                    {
                        card.WidthRequest = -1;
                        card.HeightRequest = -1;
                        CardList.Add(card);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HistoryPage] AppendCurrentBatch error: {ex.Message}");
        }
    }

    private IEnumerable<HistoryEntry> GetSortedEntries()
    {
        var entries = HistoryService.Instance.Entries.AsEnumerable();
        return (_sortField, _sortAscending) switch
        {
            (SortField.Date,   false) => entries.OrderByDescending(e => e.CompletedAt),
            (SortField.Date,   true)  => entries.OrderBy(e => e.CompletedAt),
            (SortField.Title,  true)  => entries.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase),
            (SortField.Title,  false) => entries.OrderByDescending(e => e.Title, StringComparer.OrdinalIgnoreCase),
            (SortField.Author, true)  => entries.OrderBy(e => e.Author, StringComparer.OrdinalIgnoreCase),
            (SortField.Author, false) => entries.OrderByDescending(e => e.Author, StringComparer.OrdinalIgnoreCase),
            _                         => entries.OrderByDescending(e => e.CompletedAt),
        };
    }

    private void UpdateCountLabel()
    {
        int total = HistoryService.Instance.Entries.Count;
        CountLabel.Text = total == 0
            ? "Your downloaded novels"
            : total == 1 ? "1 novel" : $"{total} novels";
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private async void OnClearAllTapped(object sender, TappedEventArgs e)
    {
        var btn = (Border)sender;
        await btn.ScaleToAsync(0.95, 80, Easing.CubicOut);
        await btn.ScaleToAsync(1.0, 80, Easing.SpringOut);

        if (HistoryService.Instance.Entries.Count == 0) return;

        bool confirm = await DisplayAlertAsync(
            "Clear History",
            "Remove all novels from your history? EPUB files on disk are not deleted.",
            "Clear", "Cancel");

        if (confirm)
        {
            SearchEntry.Text = "";
            await HistoryService.Instance.ClearAllAsync();
            _cards.Clear();
            _currentLoadedCount = 0;
            _filteredAndSortedEntries.Clear();
            RenderCurrentBatch();
            UpdateCountLabel();
        }
    }

    private async void OnOpenRequested(HistoryEntry entry)
    {
        try
        {
            if (entry == null)
            {
                await DisplayAlertAsync("Error", "Invalid history entry.", "OK");
                return;
            }

            // Resolve the best accessible path — never enqueue a download from here.
            string? epubPath = EpubOpener.ResolveAccessiblePath(entry.EpubPath, entry.Title, entry.Url);

            if (epubPath != null && epubPath != entry.EpubPath)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HistoryPage] Healed EpubPath for '{entry.Title}': '{entry.EpubPath}' → '{epubPath}'");
                entry.EpubPath = epubPath;
                entry.IsFileAvailable = true;
                await HistoryService.Instance.SaveAsync();
            }
            else if (epubPath != null)
            {
                entry.IsFileAvailable = true;
            }

            if (string.IsNullOrWhiteSpace(epubPath))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HistoryPage] Cannot open — no accessible EPUB for '{entry.Title}' " +
                    $"(storedPath='{entry.EpubPath ?? "null"}')");
                await DisplayAlertAsync("File Not Found",
                    "The EPUB file could not be found. It may have been moved or deleted.", "OK");
                return;
            }

            // Prefer a real filesystem path for external readers (Moon+ etc.)
            epubPath = EpubOpener.PreferFilesystemPath(epubPath);

            System.Diagnostics.Debug.WriteLine(
                $"[HistoryPage] Opening EPUB at: {epubPath} for '{entry.Title}'");

            // Stop any queued/active download for this URL — opening must not trigger regeneration.
            DownloadManager.Instance.CancelActiveForUrl(entry.Url);
            try
            {
                EpubOpener.Open(epubPath);
            }
            catch (InvalidOperationException)
            {
                // No EPUB reader installed — fall back to share sheet
                try
                {
                    EpubOpener.Share(epubPath, entry.Title);
                }
                catch
                {
                    await DisplayAlertAsync("No EPUB Reader",
                        "No EPUB reader app is installed. Install one from the Play Store and try again.",
                        "OK");
                }
            }
            catch (FileNotFoundException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HistoryPage] FileNotFoundException opening '{epubPath}' for '{entry.Title}'");
                await DisplayAlertAsync("File Not Found",
                    "The EPUB file could not be found. It may have been moved or deleted.", "OK");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HistoryPage] OnOpenRequested unhandled: {ex.GetType().Name}: {ex.Message}");

            // Last resort: try native share
            try
            {
                if (EpubOpener.IsAccessible(entry?.EpubPath) && entry?.EpubPath is string path)
                    EpubOpener.Share(path, entry.Title);
            }
            catch
            {
                await DisplayAlertAsync("Error",
                    "Could not open or share the EPUB file.", "OK");
            }
        }
    }

    private async void OnShareRequested(HistoryEntry entry)
    {
        string? epubPath = EpubOpener.ResolveAccessiblePath(entry.EpubPath, entry.Title, entry.Url);
        if (epubPath == null) return;

        try
        {
            EpubOpener.Share(EpubOpener.PreferFilesystemPath(epubPath), entry.Title);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HistoryPage] Share failed: {ex.Message}");
            await DisplayAlertAsync("Share Failed",
                "Could not share the EPUB file.", "OK");
        }
    }

    private async void OnDeleteRequested(HistoryEntry entry)
    {
        bool confirm = await DisplayAlertAsync(
            "Remove from History",
            $"Remove \"{entry.Title}\" from your history? The EPUB file on disk is not deleted.",
            "Remove", "Cancel");

        if (confirm)
            await HistoryService.Instance.RemoveAsync(entry);
    }

    private async void OnRedownloadRequested(HistoryEntry entry)
    {
        // ── Guard: check if the EPUB already exists somewhere before re-downloading ──
        // The options sheet only checked the stored EpubPath. The file might still be
        // present at a different location (SAF real path, custom folder, default folder).
        // If we find it, heal the entry and offer to open rather than re-download.
        string? recoveredPath = EpubOpener.ResolveAccessiblePath(entry.EpubPath, entry.Title, entry.Url);
        if (recoveredPath != null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HistoryPage] OnRedownloadRequested — existing EPUB found at '{recoveredPath}'");
        }

        if (recoveredPath != null)
        {
            // File found at a different location — heal the entry and ask the user
            entry.EpubPath = recoveredPath;
            entry.IsFileAvailable = true;
            _ = HistoryService.Instance.SaveAsync();

            bool openExisting = await DisplayAlertAsync(
                "EPUB File Found",
                $"The EPUB for \"{entry.Title}\" was found at:\n{recoveredPath}\n\n" +
                "Open the existing file, or re-download to replace it?",
                "Open File", "Re-download");

            if (openExisting)
            {
                OnOpenRequested(entry);
                return;
            }
            // Fall through: user explicitly chose to re-download
        }
        else
        {
            bool confirm = await DisplayAlertAsync(
                "Re-download",
                $"Re-download \"{entry.Title}\"?\n\nThis will queue a new download using the original URL.",
                "Download", "Cancel");

            if (!confirm) return;
        }

        // Check for an existing active download for this URL
        var existingDownload = DownloadManager.Instance.FindExisting(entry.Url);
        if (existingDownload != null && existingDownload.IsRunning)
        {
            await DisplayAlertAsync("Already Downloading",
                "This novel is already in the download queue.", "OK");
            return;
        }

        System.Diagnostics.Debug.WriteLine(
            $"[HistoryPage] Enqueuing re-download for '{entry.Title}' URL: {entry.Url}");

        // Enqueue via DownloadManager — same as tapping Download on the Home tab
        DownloadManager.Instance.Enqueue(entry.Url, entry.ChapterCount,
            string.IsNullOrWhiteSpace(entry.CoverUrl) ? null : entry.CoverUrl, forceRebuild: true);

        // Navigate to Downloads tab so the user can watch progress
        if (Shell.Current != null)
            await Shell.Current.GoToAsync("//DownloadsPage");
    }

    // ── Options sheet ─────────────────────────────────────────────────────────

    private async void OnOptionsRequested(HistoryEntry entry)
    {
        await ShowOptionsSheetAsync(entry);
    }

    private async Task ShowOptionsSheetAsync(HistoryEntry entry)
    {
        if (_isOptionsSheetOpen || entry == null)
            return;

        _isOptionsSheetOpen = true;
        _activeOptionsEntry = entry;
        OptionsSheetSubtitle.Text = entry.Title;

        string? resolvedPath = EpubOpener.ResolveAccessiblePath(entry.EpubPath, entry.Title, entry.Url);
        bool fileExists = resolvedPath != null;
        System.Diagnostics.Debug.WriteLine(
            $"[HistoryPage] ShowOptionsSheetAsync: title='{entry.Title}' storedPath='{entry.EpubPath}' " +
            $"resolved='{resolvedPath ?? "null"}'");

        if (resolvedPath != null && resolvedPath != entry.EpubPath)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HistoryPage] Healing stale EpubPath for '{entry.Title}': {entry.EpubPath} → {resolvedPath}");
            entry.EpubPath = resolvedPath;
            entry.IsFileAvailable = true;
            _ = HistoryService.Instance.SaveAsync();
        }
        else if (fileExists)
        {
            entry.IsFileAvailable = true;
        }

        OptionsSheetShareBtn.IsVisible = fileExists;
        OptionsSheetRedownloadBtn.IsVisible = !fileExists;

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
        _activeOptionsEntry = null;
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

    private async void OnOptionsSheetShareTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsEntry == null) return;
        var entry = _activeOptionsEntry;
        await HideOptionsSheetAsync();
        OnShareRequested(entry);
    }

    private async void OnOptionsSheetRedownloadTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsEntry == null) return;
        var entry = _activeOptionsEntry;
        await HideOptionsSheetAsync();
        OnRedownloadRequested(entry);
    }

    private async void OnOptionsSheetRemoveTapped(object sender, TappedEventArgs e)
    {
        if (_activeOptionsEntry == null) return;
        var entry = _activeOptionsEntry;
        await HideOptionsSheetAsync();
        OnDeleteRequested(entry);
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

        if (width > 0 && Math.Abs(_lastWidth - width) > 0.1)
        {
            _lastWidth = width;
            RenderCurrentBatch();
        }
    }

    private void RebuildCards()
    {
        _cards.Clear();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            ShowLoadingSkeleton();
            await LoadInitialBatch();
        });
    }

    private void RefreshToggleViewPill()
    {
        if (_isCompactView)
        {
            ToggleViewPill.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            ToggleViewPill.SetDynamicResource(Border.StrokeProperty, "AccentLight");
            ToggleViewIcon.SetDynamicResource(Label.TextColorProperty, "AccentLight");
            ToggleViewIcon.Text = "\uE9B0"; // grid_view
            ToggleViewLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");
            ToggleViewLabel.Text = "Grid";
        }
        else
        {
            ToggleViewPill.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
            ToggleViewPill.SetDynamicResource(Border.StrokeProperty, "Stroke");
            ToggleViewIcon.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            ToggleViewIcon.Text = "\uE8EF"; // view_list
            ToggleViewLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            ToggleViewLabel.Text = "List";
        }
    }

    private async void OnToggleViewTapped(object sender, TappedEventArgs e)
    {
        var btn = (Border)sender;
        await btn.ScaleToAsync(0.95, 70, Easing.CubicOut);
        await btn.ScaleToAsync(1.0, 70, Easing.SpringOut);

        _isCompactView = !_isCompactView;
        Preferences.Default.Set(PrefKeyViewMode, _isCompactView);

        RefreshToggleViewPill();
        RebuildCards();
    }
}
