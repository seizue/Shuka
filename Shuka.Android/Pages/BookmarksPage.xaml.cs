using Shuka.Android.Services;

namespace Shuka.Android.Pages;

/// <summary>
/// Displays all bookmarked novels organized by source site.
/// Supports search, filtering, multi-select, tagging, and batch operations.
/// </summary>
public partial class BookmarksPage : ContentPage
{
    private static readonly string[] _predefinedTags =
        { "Downloaded", "Reading", "Completed", "Favorite", "To Read" };

    private string? _filterSiteName;  // Changed to non-readonly to allow dynamic filtering
    private bool _selectMode = false;
    private readonly HashSet<string> _selectedUrls = new();
    private string _searchQuery = "";
    private string _sortFilter = "latest"; // latest, chapters
    private bool _isTagSheetOpen;
    private BookmarkItem? _tagSheetBookmark;
    private bool _isRebuildingList = false;
    private readonly object _rebuildLock = new();
    private bool _isRemoveBookmarkSheetOpen;
    private BookmarkItem? _removeBookmarkTarget;
    private bool _isCardActionSheetOpen;
    private BookmarkItem? _cardActionTarget;

    // ── Cover image loading (with header support for hotlink-protected CDNs) ─
    private static readonly HttpClient _coverHttp = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
            { "Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8" }
        }
    };
    private static readonly Dictionary<string, byte[]> _coverBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _coverCacheLock = new();


    /// <summary>
    /// Creates a bookmarks page showing all bookmarks or filtered by site.
    /// </summary>
    /// <param name="filterSiteName">If provided, only shows bookmarks from this site</param>
    public BookmarksPage(string? filterSiteName = null)
    {
        InitializeComponent();
        _filterSiteName = filterSiteName;

        if (!string.IsNullOrEmpty(filterSiteName))
        {
            TitleLabel.Text = $"{filterSiteName} Bookmarks";
        }

        BuildFilterChips();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        MainActivity.Instance?.SetTabBarVisible(true);
        UpdateSheetBottomMargins();
        BuildFilterChips();   // refresh source chips in case bookmarks changed
        BuildBookmarksList();
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        UpdateSheetBottomMargins();
    }

    private async void OnBackTapped(object sender, TappedEventArgs e)
    {
        if (_selectMode)
        {
            // Exit select mode instead of going back
            ExitSelectMode();
        }
        else
        {
            await Shell.Current.Navigation.PopAsync();
        }
    }

    private void OnPageActionTapped(object sender, TappedEventArgs e)
    {
        if (_selectMode)
            ExitSelectMode();
        else
            ShowPageActionSheet();
    }

    private async void ShowPageActionSheet()
    {
        PageActionSheetOverlay.IsVisible = true;
        await Task.WhenAll(
            PageActionSheet.TranslateToAsync(0, 0, 220, Easing.CubicOut),
            PageActionSheet.FadeToAsync(1, 180, Easing.CubicOut)
        );
    }

    private async void HidePageActionSheet()
    {
        await Task.WhenAll(
            PageActionSheet.TranslateToAsync(0, 28, 200, Easing.CubicIn),
            PageActionSheet.FadeToAsync(0, 160, Easing.CubicIn)
        );
        PageActionSheetOverlay.IsVisible = false;
    }

    private void OnPageActionSheetOverlayTapped(object sender, TappedEventArgs e)
    {
        HidePageActionSheet();
    }

    private void OnPageActionSheetTapped(object sender, TappedEventArgs e)
    {
        // Consume tap so overlay backdrop doesn't close sheet
    }

    private void OnPageActionSheetCloseTapped(object sender, TappedEventArgs e)
    {
        HidePageActionSheet();
    }

    private void OnPageActionSelectTapped(object sender, TappedEventArgs e)
    {
        HidePageActionSheet();
        EnterSelectMode();
    }

    private async void OnPageActionClearAllTapped(object sender, TappedEventArgs e)
    {
        HidePageActionSheet();
        // Small delay so the sheet closes before the dialog appears
        await Task.Delay(220);
        OnClearAllTapped(sender, e);
    }

    private void OnSelectModeTapped(object sender, TappedEventArgs e)
    {
        if (_selectMode)
            ExitSelectMode();
        else
            EnterSelectMode();
    }

    private void EnterSelectMode()
    {
        _selectMode = true;
        _selectedUrls.Clear();

        System.Diagnostics.Debug.WriteLine("[BookmarksPage] Entering select mode");

        // Switch the 3-dots icon to a close (X) icon to cancel select mode
        PageActionIcon.Text = "\uE5CD";
        PageActionIcon.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        // Change title to show we're in select mode
        TitleLabel.Text = "Select Bookmarks";

        SelectionActionBar.SetDynamicResource(Border.StrokeProperty, "Stroke");

        BuildBookmarksList();
    }

    private void ExitSelectMode()
    {
        _selectMode = false;
        _selectedUrls.Clear();

        System.Diagnostics.Debug.WriteLine("[BookmarksPage] Exiting select mode");

        // Restore 3-dots icon
        PageActionIcon.Text = "\uE5D4";
        PageActionIcon.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        // Restore original title
        TitleLabel.Text = !string.IsNullOrEmpty(_filterSiteName)
            ? $"{_filterSiteName} Bookmarks"
            : "Bookmarks";

        SelectionActionBar.IsVisible = false;

        BuildBookmarksList();
    }

    private void OnActionButtonTapped(object sender, TappedEventArgs e)
    {
        // Legacy - kept for compatibility; real entry point is OnPageActionClearAllTapped
        OnClearAllTapped(sender, e);
    }

    private async void OnClearAllTapped(object sender, TappedEventArgs e)
    {
        bool confirm = await DisplayAlertAsync("Clear All Bookmarks",
            "Are you sure you want to remove all bookmarks? This cannot be undone.",
            "Clear All", "Cancel");

        if (confirm)
        {
            if (!string.IsNullOrEmpty(_filterSiteName))
            {
                // Clear only bookmarks for this site
                var bookmarks = BookmarkService.Instance.GetBookmarksForSite(_filterSiteName);
                foreach (var bookmark in bookmarks)
                {
                    BookmarkService.Instance.RemoveBookmark(bookmark.Url);
                }
            }
            else
            {
                // Clear all bookmarks
                BookmarkService.Instance.ClearAll();
            }
            BuildBookmarksList();
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = e.NewTextValue?.Trim() ?? "";
        SearchClearBtn.IsVisible = !string.IsNullOrEmpty(_searchQuery);
        BuildBookmarksList();
    }

    private void OnSearchClearTapped(object sender, TappedEventArgs e)
    {
        SearchEntry.Text = "";
        _searchQuery = "";
        SearchClearBtn.IsVisible = false;
        BuildBookmarksList();
    }

    // ── Filter chips ──────────────────────────────────────────────────────────

    private void BuildFilterChips()
    {
        FilterChips.Clear();

        // ── Sort chips ───────────────────────────────────────────────────────
        var latestChip = CreateFilterChip("Latest", "latest", true);
        FilterChips.Add(latestChip);

        // ── Divider ──────────────────────────────────────────────────────────
        var divider = new BoxView
        {
            WidthRequest = 1,
            HeightRequest = 20,
            VerticalOptions = LayoutOptions.Center,
        };
        divider.SetDynamicResource(BoxView.ColorProperty, "Stroke");
        FilterChips.Add(divider);


        // ── Source filter chips ──────────────────────────────────────────────
        // Collect distinct site names that actually have bookmarks
        var siteGroups = BookmarkService.Instance.Bookmarks
            .Where(b => !string.IsNullOrWhiteSpace(b.SiteName))
            .GroupBy(b => b.SiteName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .ToList();

        if (siteGroups.Count > 1)
        {
            // "All" chip — no source filter
            var totalCount = siteGroups.Sum(g => g.Count());
            var allChip = CreateSourceChip("All", null, _filterSiteName == null, totalCount);
            FilterChips.Add(allChip);

            foreach (var group in siteGroups)
            {
                var siteChip = CreateSourceChip(group.Key, group.Key,
                    string.Equals(_filterSiteName, group.Key, StringComparison.OrdinalIgnoreCase),
                    group.Count());
                FilterChips.Add(siteChip);
            }
        }
    }

    private Border CreateSourceChip(string label, string? siteValue, bool isActive, int count = -1)
    {
        var chipLabel = new Label
        {
            Text = label,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
        };

        var chip = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(10, 6),
        };

        if (isActive)
        {
            chip.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
            chip.SetDynamicResource(Border.StrokeProperty, "TextMuted");
            chipLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimary");
        }
        else
        {
            chip.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
            chip.SetDynamicResource(Border.StrokeProperty, "Stroke");
            chipLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");
        }

        // Build chip content: label + optional count badge
        if (count >= 0)
        {
            var countBadge = new Border
            {
                Padding = new Thickness(5, 1),
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                VerticalOptions = LayoutOptions.Center,
            };

            var countLabel = new Label
            {
                Text = count.ToString(),
                FontSize = 9,
                FontAttributes = FontAttributes.Bold,
                VerticalOptions = LayoutOptions.Center,
            };

            if (isActive)
            {
                countBadge.SetDynamicResource(Border.BackgroundColorProperty, "Stroke");
                countLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimary");
            }
            else
            {
                countBadge.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
                countLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            }

            countBadge.Content = countLabel;

            var row = new HorizontalStackLayout
            {
                Spacing = 5,
                VerticalOptions = LayoutOptions.Center,
                Children = { chipLabel, countBadge }
            };

            chip.Content = row;
        }
        else
        {
            chip.Content = chipLabel;
        }

        chip.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await chip.ScaleToAsync(0.92, 70, Easing.CubicOut);
                await chip.ScaleToAsync(1.0, 70, Easing.SpringOut);

                _filterSiteName = siteValue;

                // Update title unless in select mode or a fixed single-source view
                if (!_selectMode)
                    TitleLabel.Text = siteValue == null ? "Bookmarks" : $"{siteValue} Bookmarks";

                BuildFilterChips();
                BuildBookmarksList();
            })
        });

        return chip;
    }

    private Border CreateFilterChip(string label, string filterValue, bool isActive)
    {
        var chipLabel = new Label
        {
            Text = label,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
        };

        var chip = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(12, 6),
        };

        if (isActive)
        {
            chip.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            chip.SetDynamicResource(Border.StrokeProperty, "AccentLight");
            chipLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");
        }
        else
        {
            chip.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
            chip.SetDynamicResource(Border.StrokeProperty, "Stroke");
            chipLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
        }

        chip.Content = chipLabel;
        chip.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await chip.ScaleToAsync(0.92, 70, Easing.CubicOut);
                await chip.ScaleToAsync(1.0, 70, Easing.SpringOut);

                _sortFilter = filterValue;
                BuildFilterChips();
                BuildBookmarksList();
            })
        });

        return chip;
    }

    // ── Build list ────────────────────────────────────────────────────────────

    private void BuildBookmarksList()
    {
        lock (_rebuildLock)
        {
            if (_isRebuildingList)
            {
                System.Diagnostics.Debug.WriteLine("[BookmarksPage] BuildBookmarksList already in progress, skipping");
                return;
            }
            _isRebuildingList = true;
        }

        try
        {
            ContentStack.Clear();

            System.Diagnostics.Debug.WriteLine($"[BookmarksPage] BuildBookmarksList called. Current selected: {_selectedUrls.Count}");

            var allBookmarks = BookmarkService.Instance.Bookmarks.ToList();

        // Filter by site if specified
        if (!string.IsNullOrEmpty(_filterSiteName))
        {
            allBookmarks = allBookmarks
                .Where(b => string.Equals(b.SiteName, _filterSiteName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Filter by search query
        if (!string.IsNullOrEmpty(_searchQuery))
        {
            allBookmarks = allBookmarks
                .Where(b =>
                    b.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                    b.Author.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                    b.Tags.Any(t => t.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // Apply sorting
        allBookmarks = _sortFilter switch
        {
            "chapters" => allBookmarks.OrderByDescending(b => b.ChapterCount).ToList(),
            _ => allBookmarks.OrderByDescending(b => b.BookmarkedAt).ToList() // latest
        };

        // Show empty state if no bookmarks
        if (allBookmarks.Count == 0)
        {
            EmptyState.IsVisible = true;

            if (!string.IsNullOrEmpty(_searchQuery))
            {
                EmptyStateTitle.Text = "No results found";
            }
            else
            {
                EmptyStateTitle.Text = "No bookmarks yet";
            }
            return;
        }

        EmptyState.IsVisible = false;

        // Group by site
        var groupedBookmarks = allBookmarks
            .GroupBy(b => b.SiteName)
            .OrderBy(g => g.Key);

        foreach (var group in groupedBookmarks)
        {
            // Site header
            var siteHeader = new Label
            {
                Text = $"{group.Key} ({group.Count()})",
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                Margin = new Thickness(4, 8, 0, 8),
                CharacterSpacing = 1.2,
            };
            siteHeader.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            ContentStack.Add(siteHeader);

            // Bookmark cards
            foreach (var bookmark in group)
            {
                ContentStack.Add(BuildBookmarkCard(bookmark));
            }
        }

        UpdateSelectionCount();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Error in BuildBookmarksList: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Stack trace: {ex.StackTrace}");
        }
        finally
        {
            lock (_rebuildLock)
            {
                _isRebuildingList = false;
            }
        }
    }

    private View BuildBookmarkCard(BookmarkItem bookmark)
    {
        bool isSelected = _selectedUrls.Contains(bookmark.Url);

        // ── Cover thumbnail ────────────────────────────────────────────────
        string? coverUrl = NormalizeBookmarkCoverUrl(bookmark.CoverUrl, bookmark.SiteName, bookmark.Url);
        View coverThumbnail;
        if (!string.IsNullOrWhiteSpace(coverUrl) &&
            Uri.TryCreate(coverUrl, UriKind.Absolute, out var bmCoverUri))
        {
            var placeholderLily = new Image
            {
                Source = ImageSource.FromFile("lily.png"),
                Aspect = Aspect.AspectFit,
                WidthRequest = 20,
                HeightRequest = 20,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Opacity = 0.35,
            };

            var coverImg = new Image
            {
                Aspect = Aspect.AspectFill,
                WidthRequest = 44,
                HeightRequest = 62,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                IsVisible = false,
            };

            var coverGrid = new Grid
            {
                WidthRequest = 44,
                HeightRequest = 62,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };
            coverGrid.Add(placeholderLily);
            coverGrid.Add(coverImg);

            var coverBorder = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                WidthRequest = 44,
                HeightRequest = 62,
                VerticalOptions = LayoutOptions.Center,
                Content = coverGrid,
            };
            coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
            coverThumbnail = coverBorder;

            LoadBookmarkCoverAsync(coverImg, coverUrl, placeholderLily, bookmark.SiteName);
        }
        else
        {
            var fallback = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                WidthRequest = 44,
                HeightRequest = 62,
                VerticalOptions = LayoutOptions.Center,
                Content = new Image
                {
                    Source = ImageSource.FromFile("lily.png"),
                    Aspect = Aspect.AspectFit,
                    WidthRequest = 22,
                    HeightRequest = 22,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Opacity = 0.45,
                },
            };
            fallback.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            coverThumbnail = fallback;
        }

        // ── Text info ────────────────────────────────────────────────────
        var titleLabel = new Label
        {
            Text = bookmark.Title, FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2,
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimary");

        var infoLabel = new Label
        {
            Text = bookmark.ChapterCount > 0
                ? $"{bookmark.Author} \u2022 {bookmark.ChapterCount} ch"
                : bookmark.Author,
            FontSize = 10, LineBreakMode = LineBreakMode.TailTruncation,
        };
        infoLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var dateLabel = new Label { Text = FormatDate(bookmark.BookmarkedAt), FontSize = 9 };
        dateLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var textStack = new VerticalStackLayout
        {
            Spacing = 2, VerticalOptions = LayoutOptions.Center,
            Children = { titleLabel, infoLabel, dateLabel },
        };

        if (bookmark.Tags.Count > 0)
        {
            var tagsStack = new HorizontalStackLayout { Spacing = 4, Margin = new Thickness(0, 3, 0, 0) };
            foreach (var tag in bookmark.Tags.Take(3))
                tagsStack.Add(CreateTagBadge(tag));
            if (bookmark.Tags.Count > 3)
            {
                var ml = new Label { Text = $"+{bookmark.Tags.Count - 3}", FontSize = 9, VerticalOptions = LayoutOptions.Center };
                ml.SetDynamicResource(Label.TextColorProperty, "TextMuted");
                tagsStack.Add(ml);
            }
            textStack.Add(tagsStack);
        }

        // ── Right widget: checkmark (select mode) or three-dot menu ───────────
        View rightWidget;
        if (_selectMode)
        {
            var checkIcon = new Label
            {
                Text = isSelected ? "\uE876" : "\uE835",
                FontFamily = "MaterialSymbols", FontSize = 22,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
            };
            checkIcon.SetDynamicResource(Label.TextColorProperty, isSelected ? "AccentLight" : "TextMuted");
            rightWidget = checkIcon;
        }
        else
        {
            var menuIcon = new Label
            {
                Text = "\uE5D4",  // more_vert
                FontFamily = "MaterialSymbols", FontSize = 22,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
            };
            menuIcon.SetDynamicResource(Label.TextColorProperty, "TextMuted");

            var menuBtn = new Grid
            {
                WidthRequest = 36, HeightRequest = 36,
                VerticalOptions = LayoutOptions.Center,
            };
            menuBtn.Add(menuIcon);
            menuBtn.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    await menuBtn.ScaleToAsync(0.85, 60, Easing.CubicOut);
                    await menuBtn.ScaleToAsync(1.0, 60, Easing.SpringOut);
                    await ShowCardActionSheetAsync(bookmark);
                })
            });
            rightWidget = menuBtn;
        }

        // ── Card layout: cover | text | right widget ──────────────────────
        var mainContent = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            ColumnSpacing = 10,
        };
        mainContent.Add(coverThumbnail, 0, 0);
        mainContent.Add(textStack, 1, 0);
        mainContent.Add(rightWidget, 2, 0);

        var card = new Border
        {
            StrokeThickness = (isSelected && _selectMode) ? 3 : 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Padding = new Thickness(10),
            Content = mainContent,
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        card.SetDynamicResource(Border.StrokeProperty, (isSelected && _selectMode) ? "AccentLight" : "Stroke");

        // ── Gestures: Long-press to select, tap to open / toggle ──────────
        CancellationTokenSource? lpCts = null;
        bool isLongPress = false;
        Point pressStartPoint = Point.Zero;
        var pointerGesture = new PointerGestureRecognizer();

        pointerGesture.PointerPressed += async (s, e) =>
        {
            try
            {
                lpCts?.Cancel();
                lpCts?.Dispose();
                isLongPress = false;
                var pt = e.GetPosition(card);
                pressStartPoint = pt ?? Point.Zero;

                var cts = new CancellationTokenSource();
                lpCts = cts;

                try
                {
                    await Task.Delay(600, cts.Token);
                    isLongPress = true;

                    // Haptic feedback
#if ANDROID
#pragma warning disable CA1416
                    try
                    {
                        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.S)
                        {
                            var vm = global::Android.App.Application.Context.GetSystemService(global::Android.Content.Context.VibratorManagerService) as global::Android.OS.VibratorManager;
                            var vib = vm?.DefaultVibrator;
                            if (vib?.HasVibrator == true)
                                vib.Vibrate(global::Android.OS.VibrationEffect.CreateOneShot(50, global::Android.OS.VibrationEffect.DefaultAmplitude));
                        }
                        else
                        {
#pragma warning disable CA1422
                            var vib = global::Android.App.Application.Context.GetSystemService(global::Android.Content.Context.VibratorService) as global::Android.OS.Vibrator;
                            if (vib?.HasVibrator == true)
                                vib.Vibrate(global::Android.OS.VibrationEffect.CreateOneShot(50, global::Android.OS.VibrationEffect.DefaultAmplitude));
#pragma warning restore CA1422
                        }
                    }
                    catch { }
#pragma warning restore CA1416
#endif

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (!_selectMode) EnterSelectMode();
                        if (!_selectedUrls.Contains(bookmark.Url))
                        {
                            _selectedUrls.Add(bookmark.Url);
                            BuildBookmarksList();
                        }
                    });
                }
                catch (OperationCanceledException) { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BookmarksPage] PointerPressed: {ex.Message}");
            }
        };

        void CancelLongPress()
        {
            try
            {
                if (lpCts != null && !lpCts.IsCancellationRequested)
                    lpCts.Cancel();
            }
            catch { }
        }

        pointerGesture.PointerMoved += (s, e) =>
        {
            // If finger moves more than 8 pixels (scrolling / dragging), cancel long press immediately
            var cur = e.GetPosition(card);
            if (cur.HasValue)
            {
                double dx = Math.Abs(cur.Value.X - pressStartPoint.X);
                double dy = Math.Abs(cur.Value.Y - pressStartPoint.Y);
                if (dx > 8 || dy > 8)
                {
                    CancelLongPress();
                }
            }
            else
            {
                CancelLongPress();
            }
        };

        pointerGesture.PointerExited   += (s, e) => CancelLongPress();
        pointerGesture.PointerReleased += (s, e) => CancelLongPress();

        card.GestureRecognizers.Add(pointerGesture);

        // Tap handling via TapGestureRecognizer (avoids conflicting with scroll gestures)
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                if (isLongPress)
                {
                    isLongPress = false;
                    return;
                }

                if (_selectMode)
                {
                    if (_selectedUrls.Contains(bookmark.Url))
                        _selectedUrls.Remove(bookmark.Url);
                    else
                        _selectedUrls.Add(bookmark.Url);

                    BuildBookmarksList();
                }
                else
                {
                    try
                    {
                        await card.ScaleToAsync(0.95, 50, Easing.CubicOut);
                        await card.ScaleToAsync(1.0, 100, Easing.SpringOut);
                        var webPage = new WebBrowsePage(bookmark.Url);
                        var nav = Shell.Current?.Navigation;
                        if (nav != null && !(nav.NavigationStack?.LastOrDefault() is WebBrowsePage))
                            await nav.PushAsync(webPage);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BookmarksPage] OpenWebView: {ex.Message}");
                    }
                }
            })
        });

        return card;
    }

    private static string? NormalizeBookmarkCoverUrl(string? coverUrl, string? siteName, string? novelUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return null;
        string url = coverUrl.Trim();

        // Extract and decode Next.js proxy url query param (e.g. /_next/image?url=https%3A%2F%2Fmedia.noveldex.io...)
        if (url.Contains("/_next/image") && url.Contains("url="))
        {
            var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]url=([^&]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string inner = Uri.UnescapeDataString(m.Groups[1].Value);
                if (inner.Contains('?')) inner = inner.Substring(0, inner.IndexOf('?'));
                if (!string.IsNullOrWhiteSpace(inner)) url = inner;
            }
        }

        if (url.StartsWith("//"))
            url = "https:" + url;
        else if (url.StartsWith("/"))
        {
            if (!string.IsNullOrWhiteSpace(novelUrl) && Uri.TryCreate(novelUrl, UriKind.Absolute, out var nUri))
                url = $"{nUri.Scheme}://{nUri.Host}" + url;
            else if (!string.IsNullOrWhiteSpace(siteName) && siteName.Contains("noveldex", StringComparison.OrdinalIgnoreCase))
                url = "https://noveldex.io" + url;
            else
                url = "https://" + url.TrimStart('/');
        }

        return Uri.IsWellFormedUriString(url, UriKind.Absolute) ? url : null;
    }

    /// <summary>
    /// Downloads a cover image with proper browser headers so hotlink-protected
    /// CDNs (e.g. media.noveldex.io) return the real image instead of a 403.
    /// Uses an in-memory byte cache so repeated rebuilds skip the network.
    /// </summary>
    private static void LoadBookmarkCoverAsync(Image targetImg, string? coverUrl, View? placeholderView = null, string? siteName = null)
    {
        if (string.IsNullOrWhiteSpace(coverUrl) ||
            !Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri))
        {
            targetImg.IsVisible = false;
            if (placeholderView != null) placeholderView.IsVisible = true;
            return;
        }

        // Check cache first
        byte[]? cached;
        lock (_coverCacheLock)
            _coverBytesCache.TryGetValue(coverUrl, out cached);

        if (cached != null)
        {
            targetImg.Source = ImageSource.FromStream(() => new MemoryStream(cached));
            targetImg.IsVisible = true;
            if (placeholderView != null) placeholderView.IsVisible = false;
            return;
        }

        // Show placeholder while downloading
        targetImg.IsVisible = false;
        if (placeholderView != null) placeholderView.IsVisible = true;

        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);

                // Set Referer to the page origin so CDN hotlink checks pass.
                // For noveldex CDN (media.noveldex.io), referer must be https://noveldex.io/
                string referer = (siteName?.Contains("noveldex", StringComparison.OrdinalIgnoreCase) == true ||
                                  uri.Host.Contains("noveldex", StringComparison.OrdinalIgnoreCase))
                    ? "https://noveldex.io/"
                    : $"{uri.Scheme}://{uri.Host}/";

                try { req.Headers.Referrer = new Uri(referer); } catch { }

                using var resp = await _coverHttp.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadAsByteArrayAsync();
                    if (data != null && data.Length > 0)
                    {
                        lock (_coverCacheLock)
                            _coverBytesCache[coverUrl] = data;

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            targetImg.Source = ImageSource.FromStream(() => new MemoryStream(data));
                            targetImg.IsVisible = true;
                            if (placeholderView != null) placeholderView.IsVisible = false;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Cover load failed for {coverUrl}: {ex.Message}");
            }
        });
    }



    private Border CreateActionButton(string icon, string label, Func<Task> action, bool isDestructive = false)
    {
        var iconLabel = new Label
        {
            Text = icon,
            FontFamily = "MaterialSymbols",
            FontSize = 14,
            VerticalOptions = LayoutOptions.Center,
        };

        var textLabel = new Label
        {
            Text = label.ToUpper(),
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
        };

        if (isDestructive)
        {
            iconLabel.SetDynamicResource(Label.TextColorProperty, "Warning");
            textLabel.SetDynamicResource(Label.TextColorProperty, "Warning");
        }
        else
        {
            iconLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
            textLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
        }

        var stack = new HorizontalStackLayout
        {
            Spacing = 4,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { iconLabel, textLabel },
        };

        var button = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(10, 6),
            Content = stack,
        };
        button.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        button.SetDynamicResource(Border.StrokeProperty, "Stroke");

        button.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                try
                {
                    await button.ScaleToAsync(0.85, 70, Easing.CubicOut);
                    await button.ScaleToAsync(1.0, 70, Easing.SpringOut);
                    await action();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Error in action button: {ex.Message}");
                }
            })
        });

        return button;
    }

    private Border CreateTagBadge(string tag)
    {
        var tagLabel = new Label
        {
            Text = tag,
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
        };
        tagLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        var badge = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(6, 2),
            Content = tagLabel,
        };
        badge.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");

        return badge;
    }

    private Border CreateActionButton(string icon, string label, bool isDestructive = false)
    {
        var iconLabel = new Label
        {
            Text = icon,
            FontFamily = "MaterialSymbols",
            FontSize = 14,
            VerticalOptions = LayoutOptions.Center,
        };

        var textLabel = new Label
        {
            Text = label.ToUpper(),
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
        };

        if (isDestructive)
        {
            iconLabel.SetDynamicResource(Label.TextColorProperty, "Warning");
            textLabel.SetDynamicResource(Label.TextColorProperty, "Warning");
        }
        else
        {
            iconLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
            textLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
        }

        var stack = new HorizontalStackLayout
        {
            Spacing = 4,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { iconLabel, textLabel },
        };

        var button = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(10, 6),
            Content = stack,
        };
        button.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        button.SetDynamicResource(Border.StrokeProperty, "Stroke");

        return button;
    }

    // ── Selection actions ─────────────────────────────────────────────────────

    private void UpdateSelectionCount()
    {
        SelectionCountLabel.Text = $"{_selectedUrls.Count} selected";

        // Show action bar only when items are selected
        SelectionActionBar.IsVisible = _selectMode && _selectedUrls.Count > 0;

        System.Diagnostics.Debug.WriteLine($"[BookmarksPage] UpdateSelectionCount: {_selectedUrls.Count}, ActionBar visible: {SelectionActionBar.IsVisible}");
    }

    private async void OnDownloadSelectedTapped(object sender, TappedEventArgs e)
    {
        try
        {
            if (_selectedUrls.Count == 0)
            {
                await DisplayAlertAsync("No Selection", "Please select bookmarks to download.", "OK");
                return;
            }

            var selectedBookmarks = BookmarkService.Instance.Bookmarks
                .Where(b => _selectedUrls.Contains(b.Url))
                .ToList();

            string message;
            if (selectedBookmarks.Count == 1)
            {
                message = $"Download \"{selectedBookmarks[0].Title}\"?";
            }
            else
            {
                message = $"Download {selectedBookmarks.Count} novels?\n\nNote: 2 novels will download simultaneously. Others will be queued.";
            }

            bool confirm = await DisplayAlertAsync("Download Selected",
                message,
                "Download", "Cancel");

            if (confirm)
            {
                foreach (var bookmark in selectedBookmarks)
                {
                    DownloadManager.Instance.Enqueue(bookmark.Url, 0, null);
                }

                string resultMessage = selectedBookmarks.Count == 1
                    ? $"\"{selectedBookmarks[0].Title}\" queued for download!"
                    : $"{selectedBookmarks.Count} novel(s) queued for download!\n\n2 will start immediately, others are queued.";

                await DisplayAlertAsync("Queued", resultMessage, "OK");

                ExitSelectMode();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Error in OnDownloadSelectedTapped: {ex.Message}");
            await DisplayAlertAsync("Error", "An error occurred while downloading selected items.", "OK");
        }
    }

    private async void OnDeleteSelectedTapped(object sender, TappedEventArgs e)
    {
        try
        {
            if (_selectedUrls.Count == 0)
            {
                await DisplayAlertAsync("No Selection", "Please select bookmarks to delete.", "OK");
                return;
            }

            bool confirm = await DisplayAlertAsync("Delete Selected",
                $"Delete {_selectedUrls.Count} bookmark(s)? This cannot be undone.",
                "Delete", "Cancel");

            if (confirm)
            {
                foreach (var url in _selectedUrls.ToList())
                {
                    BookmarkService.Instance.RemoveBookmark(url);
                }

                ExitSelectMode();
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        await Task.Delay(100);
                        BuildBookmarksList();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Error in BuildBookmarksList (DeleteSelected): {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Error in OnDeleteSelectedTapped: {ex.Message}");
            await DisplayAlertAsync("Error", "An error occurred while deleting selected items.", "OK");
        }
    }

    // ── Tag dialog ────────────────────────────────────────────────────────────

    private async Task ShowTagDialogAsync(BookmarkItem bookmark)
    {
        _tagSheetBookmark = bookmark;
        await ShowTagSheetAsync();
    }

    // ── Download helper ───────────────────────────────────────────────────────

    private async Task DownloadBookmarkAsync(BookmarkItem bookmark)
    {
        var existing = DownloadManager.Instance.FindExisting(bookmark.Url);
        if (existing != null)
        {
            string title = string.IsNullOrWhiteSpace(existing.Title) || existing.Title == "Loading..."
                ? "this novel" : $"\"{existing.Title}\"";

            bool alreadyActive = existing.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Resuming or DownloadStatus.Paused;
            string message = alreadyActive
                ? $"Already downloading {title}."
                : $"{title} was already downloaded.";

            string? choice = await DisplayActionSheetAsync(message, "Cancel", null,
                "Download again", "Go to Downloads");

            if (choice == "Go to Downloads")
            {
                await Shell.Current.GoToAsync("//DownloadsPage");
                return;
            }
            if (choice != "Download again") return;

            if (existing.IsFinished)
                DownloadManager.Instance.Dismiss(existing);
        }

        DownloadManager.Instance.Enqueue(bookmark.Url, 0, null);
        await DisplayAlertAsync("Queued", $"\"{bookmark.Title}\" queued for download!", "OK");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string FormatDate(DateTime date)
    {
        var now = DateTime.Now;
        var diff = now - date;

        if (diff.TotalMinutes < 1)
            return "Just now";
        if (diff.TotalMinutes < 60)
            return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24)
            return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7)
            return $"{(int)diff.TotalDays}d ago";
        if (diff.TotalDays < 30)
            return $"{(int)(diff.TotalDays / 7)}w ago";

        return date.ToString("MMM d, yyyy");
    }

    private void BuildTagSheetOptions()
    {
        if (_tagSheetBookmark == null)
            return;

        TagSheetOptionsList.Clear();
        TagSheetSubtitle.Text = _tagSheetBookmark.Title;
        TagSheetClearAllBtn.IsVisible = _tagSheetBookmark.Tags.Count > 0;

        foreach (var tag in _predefinedTags)
        {
            bool selected = _tagSheetBookmark.Tags.Contains(tag);

            var row = new Border
            {
                StrokeThickness = 1,
                Padding = new Thickness(12, 10),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            };
            row.SetDynamicResource(Border.BackgroundColorProperty, selected ? "AccentContainer" : "BgInput");
            row.SetDynamicResource(Border.StrokeProperty, selected ? "AccentLight" : "Stroke");

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 10
            };

            var icon = new Label
            {
                Text = selected ? "\uE876" : "\uE835", // check_box / check_box_outline_blank
                FontFamily = "MaterialSymbols",
                FontSize = 18,
                VerticalOptions = LayoutOptions.Center
            };
            icon.SetDynamicResource(Label.TextColorProperty, selected ? "AccentLight" : "TextMuted");

            var title = new Label
            {
                Text = tag,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                VerticalOptions = LayoutOptions.Center
            };
            title.SetDynamicResource(Label.TextColorProperty, selected ? "AccentLight" : "TextPrimary");

            var chevron = new Label
            {
                Text = "\uE5CC",
                FontFamily = "MaterialSymbols",
                FontSize = 18,
                VerticalOptions = LayoutOptions.Center
            };
            chevron.SetDynamicResource(Label.TextColorProperty, selected ? "AccentLight" : "TextMuted");

            grid.Add(icon, 0, 0);
            grid.Add(title, 1, 0);
            grid.Add(chevron, 2, 0);
            row.Content = grid;
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() =>
                {
                    try
                    {
                        ToggleTag(tag);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Error in tag toggle: {ex.Message}");
                    }
                })
            });

            TagSheetOptionsList.Add(row);
        }
    }

    private void ToggleTag(string tag)
    {
        if (_tagSheetBookmark == null)
            return;

        if (_tagSheetBookmark.Tags.Contains(tag))
            BookmarkService.Instance.RemoveTag(_tagSheetBookmark.Url, tag);
        else
            BookmarkService.Instance.AddTag(_tagSheetBookmark.Url, tag);

        // Refresh current bookmark snapshot and UI.
        _tagSheetBookmark = BookmarkService.Instance.Bookmarks
            .FirstOrDefault(b => b.Url == _tagSheetBookmark.Url) ?? _tagSheetBookmark;
        BuildTagSheetOptions();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Task.Delay(100);
                BuildBookmarksList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Error in BuildBookmarksList (ToggleTag): {ex.Message}");
            }
        });
    }

    private async Task ShowTagSheetAsync()
    {
        if (_isTagSheetOpen || _tagSheetBookmark == null)
            return;

        _isTagSheetOpen = true;
        BuildTagSheetOptions();
        TagSheetOverlay.IsVisible = true;
        TagSheetOverlay.Opacity = 0;
        TagSheet.Opacity = 0;
        TagSheet.TranslationY = 28;

        await Task.WhenAll(
            TagSheetOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            TagSheet.FadeToAsync(1, 180, Easing.CubicOut),
            TagSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async Task HideTagSheetAsync()
    {
        if (!_isTagSheetOpen)
            return;

        _isTagSheetOpen = false;
        await Task.WhenAll(
            TagSheet.FadeToAsync(0, 140, Easing.CubicIn),
            TagSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            TagSheetOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        TagSheetOverlay.IsVisible = false;
        _tagSheetBookmark = null;
    }

    private async void OnTagSheetOverlayTapped(object sender, TappedEventArgs e)
    {
        await HideTagSheetAsync();
    }

    private void OnTagSheetTapped(object sender, TappedEventArgs e)
    {
        // Swallow tap so overlay handler does not close it.
    }

    private async void OnTagSheetCloseTapped(object sender, TappedEventArgs e)
    {
        await HideTagSheetAsync();
    }

    private void OnTagSheetClearAllTapped(object sender, TappedEventArgs e)
    {
        if (_tagSheetBookmark == null)
            return;

        BookmarkService.Instance.UpdateBookmarkTags(_tagSheetBookmark.Url, new List<string>());
        _tagSheetBookmark = BookmarkService.Instance.Bookmarks
            .FirstOrDefault(b => b.Url == _tagSheetBookmark.Url) ?? _tagSheetBookmark;
        BuildTagSheetOptions();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Task.Delay(100);
                BuildBookmarksList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Error in BuildBookmarksList (ClearAll): {ex.Message}");
            }
        });
    }

    private async void OnTagSheetAddCustomTapped(object sender, TappedEventArgs e)
    {
        if (_tagSheetBookmark == null)
            return;

        string? customTag = await DisplayPromptAsync("Add Tag",
            "Enter a custom tag:",
            "Add", "Cancel",
            maxLength: 20);

        if (string.IsNullOrWhiteSpace(customTag))
            return;

        BookmarkService.Instance.AddTag(_tagSheetBookmark.Url, customTag.Trim());
        _tagSheetBookmark = BookmarkService.Instance.Bookmarks
            .FirstOrDefault(b => b.Url == _tagSheetBookmark.Url) ?? _tagSheetBookmark;
        BuildTagSheetOptions();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Task.Delay(100);
                BuildBookmarksList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Error in BuildBookmarksList (AddCustom): {ex.Message}");
            }
        });
    }

    private async Task ShowRemoveBookmarkSheetAsync(BookmarkItem bookmark)
    {
        if (_isRemoveBookmarkSheetOpen)
            return;

        _isRemoveBookmarkSheetOpen = true;
        _removeBookmarkTarget = bookmark;
        RemoveBookmarkSheetSubtitle.Text = $"Remove \"{bookmark.Title}\" from bookmarks?";

        RemoveBookmarkSheetOverlay.IsVisible = true;
        RemoveBookmarkSheetOverlay.Opacity = 0;
        RemoveBookmarkSheet.Opacity = 0;
        RemoveBookmarkSheet.TranslationY = 28;

        await Task.WhenAll(
            RemoveBookmarkSheetOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            RemoveBookmarkSheet.FadeToAsync(1, 180, Easing.CubicOut),
            RemoveBookmarkSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async Task HideRemoveBookmarkSheetAsync()
    {
        if (!_isRemoveBookmarkSheetOpen)
            return;

        _isRemoveBookmarkSheetOpen = false;
        await Task.WhenAll(
            RemoveBookmarkSheet.FadeToAsync(0, 140, Easing.CubicIn),
            RemoveBookmarkSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            RemoveBookmarkSheetOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        RemoveBookmarkSheetOverlay.IsVisible = false;
        _removeBookmarkTarget = null;
    }

    private async void OnRemoveBookmarkSheetOverlayTapped(object sender, TappedEventArgs e)
    {
        await HideRemoveBookmarkSheetAsync();
    }

    private void OnRemoveBookmarkSheetTapped(object sender, TappedEventArgs e)
    {
        // Swallow tap so overlay handler does not close it.
    }

    private async void OnRemoveBookmarkCancelTapped(object sender, TappedEventArgs e)
    {
        await HideRemoveBookmarkSheetAsync();
    }

    private async void OnRemoveBookmarkConfirmTapped(object sender, TappedEventArgs e)
    {
        if (_removeBookmarkTarget != null)
        {
            BookmarkService.Instance.RemoveBookmark(_removeBookmarkTarget.Url);
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    await Task.Delay(100);
                    BuildBookmarksList();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BookmarksPage] Error in BuildBookmarksList (RemoveBookmark): {ex.Message}");
                }
            });
        }
        await HideRemoveBookmarkSheetAsync();
    }

    private void UpdateSheetBottomMargins()
    {
        double bottomInset = 16;
#if ANDROID
        if (MainActivity.Instance is { } activity)
            bottomInset = Math.Max(bottomInset, activity.GetOverlayBottomInsetDip(14));
#endif

        TagSheet.Margin = new Thickness(12, 0, 12, bottomInset);
        RemoveBookmarkSheet.Margin = new Thickness(12, 0, 12, bottomInset);
        CardActionSheet.Margin = new Thickness(12, 0, 12, bottomInset);
        PageActionSheet.Margin = new Thickness(12, 0, 12, bottomInset);
    }

    // ── Card action sheet ─────────────────────────────────────────────────────

    private async Task ShowCardActionSheetAsync(BookmarkItem bookmark)
    {
        if (_isCardActionSheetOpen) return;
        _isCardActionSheetOpen = true;
        _cardActionTarget = bookmark;

        CardActionSheetTitle.Text = bookmark.Title;

        CardActionSheetOverlay.IsVisible = true;
        CardActionSheetOverlay.Opacity = 0;
        CardActionSheet.Opacity = 0;
        CardActionSheet.TranslationY = 28;

        await Task.WhenAll(
            CardActionSheetOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            CardActionSheet.FadeToAsync(1, 180, Easing.CubicOut),
            CardActionSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async Task HideCardActionSheetAsync()
    {
        if (!_isCardActionSheetOpen) return;
        _isCardActionSheetOpen = false;
        await Task.WhenAll(
            CardActionSheet.FadeToAsync(0, 140, Easing.CubicIn),
            CardActionSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            CardActionSheetOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        CardActionSheetOverlay.IsVisible = false;
        _cardActionTarget = null;
    }

    private async void OnCardActionSheetOverlayTapped(object sender, TappedEventArgs e)
        => await HideCardActionSheetAsync();

    private void OnCardActionSheetTapped(object sender, TappedEventArgs e) { /* swallow */ }

    private async void OnCardActionSheetCloseTapped(object sender, TappedEventArgs e)
        => await HideCardActionSheetAsync();

    private async void OnCardActionDownloadTapped(object sender, TappedEventArgs e)
    {
        var target = _cardActionTarget;
        await HideCardActionSheetAsync();
        if (target != null) await DownloadBookmarkAsync(target);
    }

    private async void OnCardActionFetchTapped(object sender, TappedEventArgs e)
    {
        var target = _cardActionTarget;
        await HideCardActionSheetAsync();
        if (target == null) return;
        if (MainPage.Instance != null)
            WebBrowsePage.OnUrlFetched = MainPage.Instance.FillUrlFromWebView;
        WebBrowsePage.OnUrlFetched?.Invoke(target.Url);
        await Shell.Current.GoToAsync("//MainPage");
    }

    private async void OnCardActionTagTapped(object sender, TappedEventArgs e)
    {
        var target = _cardActionTarget;
        await HideCardActionSheetAsync();
        if (target != null) await ShowTagDialogAsync(target);
    }

    private async void OnCardActionRemoveTapped(object sender, TappedEventArgs e)
    {
        var target = _cardActionTarget;
        await HideCardActionSheetAsync();
        if (target != null) await ShowRemoveBookmarkSheetAsync(target);
    }
}

