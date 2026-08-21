using Shuka.Core;
using Shuka.Core.Adapters;
using Shuka.Android.Platform;
using System.Text.RegularExpressions;
using System.IO;

namespace Shuka.Android.Pages;

public partial class SourceBrowsePage : ContentPage
{
    private readonly IBrowsableAdapter _source;
    private readonly DiscoverService   _service;

    private enum BrowseMode { Recent, Popular, Top500, Search }
    private BrowseMode _mode    = BrowseMode.Recent;
    private int        _page    = 1;
    private bool       _loading = false;
    private bool       _hasMore = true;
    private string     _query   = "";

    // true = tap card shows detail sheet; false = tap card opens WebView
    private bool _cardViewMode = true;

    // true = translate titles in card view
    private bool _translateTitles = false;

    // Page width for adaptive column grid
    private double _pageWidth = 0;

    private bool _isImageContextMenuOpen;
    private string? _currentImageContextMenuUrl;
    private string? _currentContextMenuNovelTitle;
    private string? _currentContextMenuNovelUrl;

    // Detail sheet state
    private bool _isDetailSheetOpen;
    private NovelEntry? _activeDetailNovel;
    private CancellationTokenSource? _translateCts;

    // All loaded novels (kept for adaptive re-layout on width change)
    private readonly List<NovelEntry> _loadedNovels = new();

    // Cache for translated titles (original title -> translated title)
    private readonly Dictionary<string, string> _translatedTitles = new(StringComparer.OrdinalIgnoreCase);

    // Cache for remote cover image bytes (URL -> byte[])
    private static readonly Dictionary<string, byte[]> _coverBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _coverImageLock = new();
    private static readonly HttpClient _coverHttp = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
            { "Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8" }
        }
    };

    // Custom filter pills state
    private SourceFilter? _activeFilter;
    private readonly List<(Border Pill, SourceFilter Filter)> _customFilterPills = new();

    public SourceBrowsePage(IBrowsableAdapter source, string? initialQuery = null)
    {
        InitializeComponent();
        _source  = source;
        _service = new DiscoverService(new WebViewCloudflareBypass());

        TitleLabel.Text = source.SiteName;
        SearchEntry.TextChanged += (_, e) =>
            SearchClearBtn.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);

        // Hide translate toggle for English-only sources (noveldex.io)
        bool isEnglishSource = source.SiteName.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase);
        TranslateToggleBtn.IsVisible = !isEnglishSource;

        // Custom filter pills if supported by the source
        if (source.Filters != null && source.Filters.Count > 0)
        {
            FilterPillsStack.Children.Clear();
            _customFilterPills.Clear();
            _activeFilter = source.Filters[0];

            foreach (var filter in source.Filters)
            {
                var pill = BuildFilterPill(filter);
                _customFilterPills.Add((pill, filter));
                FilterPillsStack.Children.Add(pill);
            }
        }
        else
        {
            // Show Top 500 pill only for ShukuBrowse (52shuku.net)
            PillTop500.IsVisible = source is ShukuBrowse;
        }

        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            _query = initialQuery;
            _mode  = BrowseMode.Search;
            SearchEntry.Text = initialQuery;
            SearchClearBtn.IsVisible = true;
        }

        RefreshPills();
        RefreshViewToggle();
        RefreshTranslateToggle();
        _ = LoadPageAsync(reset: true);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        MainActivity.Instance?.SetTabBarVisible(false);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        
        var navStack = Navigation?.NavigationStack ?? Shell.Current?.Navigation?.NavigationStack;
        bool isPopped = Navigation == null || !Navigation.NavigationStack.Contains(this);
        if (isPopped)
        {
            var previousPage = navStack?.LastOrDefault(p => p != this);
            if (previousPage == null || 
                (previousPage is not AboutPage &&
                 previousPage is not SourceBrowsePage &&
                 previousPage is not WebBrowsePage &&
                 previousPage is not ShukaQuestPage))
            {
                MainActivity.Instance?.SetTabBarVisible(true);
            }
        }
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    protected override bool OnBackButtonPressed()
    {
        if (_isDetailSheetOpen)
        {
            _ = HideNovelDetailAsync();
            return true;
        }
        _ = Shell.Current.Navigation.PopAsync();
        return true;
    }

    private async void OnBackTapped(object sender, TappedEventArgs e)
        => await Shell.Current.Navigation.PopAsync();

    // ── View toggle (top-right link icon opens browser directly) ────────────────

    private void OnViewToggleTapped(object sender, TappedEventArgs e)
    {
        var homeUrl = _source?.HomeUrl ?? _source?.GetRecentUrl(1);
        if (string.IsNullOrWhiteSpace(homeUrl)) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var nav = Shell.Current?.Navigation;
                if (nav == null) return;
                if (nav.NavigationStack?.LastOrDefault() is WebBrowsePage) return;
                var webPage = new WebBrowsePage(homeUrl);
                await nav.PushAsync(webPage, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SourceBrowsePage] ViewToggle open web error: {ex.Message}");
            }
        });
    }

    private void RefreshViewToggle()
    {
        ViewToggleIcon.Text = "\uE157"; // link/globe icon
        ViewToggleBtn.AutomationId = "Open in Web Browser";
    }

    private void RefreshTranslateToggle()
    {
        if (_translateTitles)
        {
            TranslateToggleIcon.TextColor = Colors.White;
            TranslateToggleIcon.SetDynamicResource(Label.TextColorProperty, "AccentLight");
        }
        else
        {
            TranslateToggleIcon.SetDynamicResource(Label.TextColorProperty, "TextMuted");
        }
    }

    private async void OnTranslateToggleTapped(object sender, TappedEventArgs e)
    {
        _translateTitles = !_translateTitles;
        RefreshTranslateToggle();

        if (_translateTitles)
        {
            // Translate all loaded novel titles
            await TranslateAllTitlesAsync();
        }
        else
        {
            // Rebuild grid with original titles
            RebuildNovelGrid();
        }
    }

    /// <summary>
    /// Returns a cached ImageSource for the remote cover URL, or null if not cached.
    /// Creates a new ImageSource from cached bytes to safely bind to controls.
    /// </summary>
    private static ImageSource? GetCachedCoverImage(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
            return null;

        lock (_coverImageLock)
        {
            if (_coverBytesCache.TryGetValue(coverUrl, out var bytes))
                return ImageSource.FromStream(() => new MemoryStream(bytes));

            // Not cached yet - will be loaded asynchronously
            return null;
        }
    }

    /// <summary>
    /// Caches image bytes for a remote cover URL.
    /// </summary>
    private static void CacheCoverBytes(string coverUrl, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(coverUrl) || bytes == null || bytes.Length == 0)
            return;

        lock (_coverImageLock)
        {
            _coverBytesCache[coverUrl] = bytes;
        }
    }

    /// <summary>
    /// Asynchronously loads a cover image using HttpClient with browser headers,
    /// caches the downloaded bytes in memory, and updates the Image control smoothly.
    /// Avoids MAUI UriImageSource layout glitches and hotlink failures.
    /// </summary>
    private void LoadCoverImageAsync(Image targetImg, string? coverUrl, View? placeholderView = null)
    {
        if (string.IsNullOrWhiteSpace(coverUrl) || !Uri.TryCreate(coverUrl, UriKind.Absolute, out var coverUri))
        {
            targetImg.IsVisible = false;
            if (placeholderView != null) placeholderView.IsVisible = true;
            return;
        }

        // Check cache first
        var cached = GetCachedCoverImage(coverUrl);
        if (cached != null)
        {
            targetImg.Source = cached;
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
                using var req = new HttpRequestMessage(HttpMethod.Get, coverUri);
                if (!string.IsNullOrWhiteSpace(_source?.HomeUrl))
                {
                    try { req.Headers.Referrer = new Uri(_source.HomeUrl); } catch { }
                }

                using var resp = await _coverHttp.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadAsByteArrayAsync();
                    if (data != null && data.Length > 0)
                    {
                        CacheCoverBytes(coverUrl, data);
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
                System.Diagnostics.Debug.WriteLine($"[SourceBrowsePage] Failed to load cover: {ex.Message}");
            }
        });
    }

    private async Task TranslateAllTitlesAsync()
    {
        if (_loadedNovels.Count == 0) return;

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        var translator = new Shuka.Core.Translator(http);

        // Translate titles that haven't been translated yet
        var tasks = _loadedNovels
            .Where(n => !string.IsNullOrWhiteSpace(n.Title) && !_translatedTitles.ContainsKey(n.Title))
            .Select(async novel =>
            {
                try
                {
                    var translated = await translator.Translate(novel.Title);
                    if (!string.IsNullOrWhiteSpace(translated) &&
                        !string.Equals(translated, novel.Title, StringComparison.OrdinalIgnoreCase))
                    {
                        _translatedTitles[novel.Title] = translated;
                    }
                }
                catch { /* Ignore translation failures */ }
            });

        await Task.WhenAll(tasks);

        // Rebuild grid with translated titles
        RebuildNovelGrid();
    }

    // ── Adaptive layout ───────────────────────────────────────────────────────

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0 && Math.Abs(width - _pageWidth) > 1)
        {
            _pageWidth = width;
            if (NovelList.Children.Count > 0)
                RebuildNovelGrid();
        }
    }

    private void RebuildNovelGrid()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            NovelList.Children.Clear();
            RenderNovelsIntoGrid(_loadedNovels);
            if (_hasMore)
                NovelList.Children.Add(BuildLoadMoreButton());
        });
    }

    private void RenderNovelsIntoGrid(IEnumerable<NovelEntry> novels)
    {
        // Adaptive column count: 3 on mobile phones, 4 on large phones/phablets, 5 on small tablets, 6 on large tablets
        int cols = _pageWidth switch
        {
            > 750 => 6,
            > 550 => 5,
            > 420 => 4,
            _     => 3,
        };

        var list = novels.ToList();
        for (int i = 0; i < list.Count; i += cols)
        {
            var colDefs = new ColumnDefinitionCollection();
            for (int c = 0; c < cols; c++)
                colDefs.Add(new ColumnDefinition { Width = GridLength.Star });

            var row = new Grid
            {
                ColumnDefinitions = colDefs,
                ColumnSpacing = 6,
                RowSpacing    = 0,
            };

            for (int c = 0; c < cols && i + c < list.Count; c++)
                row.Add(BuildCompactCard(list[i + c]), c, 0);

            NovelList.Children.Add(row);
        }
    }

    private async void OnRecentTapped(object sender, TappedEventArgs e)
    {
        if (_mode == BrowseMode.Recent) return;
        _mode = BrowseMode.Recent;
        _query = "";
        SearchEntry.Text = "";
        RefreshPills();
        await LoadPageAsync(reset: true);
    }

    private async void OnPopularTapped(object sender, TappedEventArgs e)
    {
        if (_mode == BrowseMode.Popular) return;
        _mode = BrowseMode.Popular;
        _query = "";
        SearchEntry.Text = "";
        RefreshPills();
        await LoadPageAsync(reset: true);
    }

    private async void OnTop500Tapped(object sender, TappedEventArgs e)
    {
        if (_mode == BrowseMode.Top500) return;
        _mode = BrowseMode.Top500;
        _query = "";
        SearchEntry.Text = "";
        RefreshPills();
        await LoadPageAsync(reset: true);
    }

    private Border BuildFilterPill(SourceFilter filter)
    {
        var label = new Label
        {
            Text = filter.Name,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold
        };
        var pill = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            Padding = new Thickness(14, 6),
            Content = label
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (_activeFilter == filter && _mode != BrowseMode.Search) return;
            _activeFilter = filter;
            _mode = BrowseMode.Recent;
            _query = "";
            SearchEntry.Text = "";
            RefreshPills();
            await LoadPageAsync(reset: true);
        };

        label.GestureRecognizers.Add(tap);
        pill.GestureRecognizers.Add(tap);
        return pill;
    }

    private void RefreshPills()
    {
        if (_source.Filters != null && _source.Filters.Count > 0)
        {
            foreach (var (pill, filter) in _customFilterPills)
            {
                SetPillActive(pill, filter == _activeFilter && _mode != BrowseMode.Search);
            }
        }
        else
        {
            SetPillActive(PillRecent,  _mode == BrowseMode.Recent);
            SetPillActive(PillPopular, _mode == BrowseMode.Popular);
            if (PillTop500 != null)
                SetPillActive(PillTop500, _mode == BrowseMode.Top500);
        }
    }

    private void SetPillActive(Border pill, bool active)
    {
        if (active)
        {
            pill.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            pill.SetDynamicResource(Border.StrokeProperty, "AccentLight");
            if (pill.Content is Label lbl)
                lbl.SetDynamicResource(Label.TextColorProperty, "AccentLight");
        }
        else
        {
            pill.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
            pill.SetDynamicResource(Border.StrokeProperty, "Stroke");
            if (pill.Content is Label lbl)
                lbl.SetDynamicResource(Label.TextColorProperty, "TextMuted");
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private async void OnSearchCompleted(object sender, EventArgs e)
    {
        string q = SearchEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(q)) return;
        _query = q;
        _mode  = BrowseMode.Search;
        RefreshPills();
        await LoadPageAsync(reset: true);
    }

    private async void OnSearchClearTapped(object sender, TappedEventArgs e)
    {
        SearchEntry.Text = "";
        _query = "";
        SearchClearBtn.IsVisible = false;
        _mode = BrowseMode.Recent;
        if (_source.Filters != null && _source.Filters.Count > 0)
            _activeFilter = _source.Filters[0];
        RefreshPills();
        await LoadPageAsync(reset: true);
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    private async Task LoadPageAsync(bool reset = false)
    {
        if (_loading) return;
        if (!reset && !_hasMore) return;

        _loading = true;

        if (reset)
        {
            _page = 1;
            _hasMore = true;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _loadedNovels.Clear();
                NovelList.Children.Clear();
                LoadingState.IsVisible = true;
                EmptyState.IsVisible   = false;
                ListScroll.IsVisible   = false;
            });
        }

        try
        {
            ListingPage result = _mode switch
            {
                BrowseMode.Popular => await _service.GetPopularAsync(_source, _page),
                BrowseMode.Top500  => await GetTop500PageAsync(_source, _page),
                BrowseMode.Search  => await _service.SearchAsync(_source, _query, _page),
                _                  => _activeFilter != null
                                        ? await _service.FetchPageAsync(_source, _activeFilter.UrlGenerator(_page))
                                        : await _service.GetRecentAsync(_source, _page),
            };

            _hasMore = result.HasNextPage;
            _page++;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                LoadingState.IsVisible = false;

                if (result.Novels.Count == 0 && reset)
                {
                    // CF-protected or parse-failure sources get a WebView-only prompt
                    if (_source.RequiresCfBypass)
                    {
                        ShowWebViewOnlyState();
                    }
                    else
                    {
                        EmptyState.IsVisible = true;
                    }
                    ListScroll.IsVisible = false;
                    return;
                }

                ListScroll.IsVisible = true;
                EmptyState.IsVisible = false;

                // Remove old "load more" button before appending new cards
                var oldLoadMore = NovelList.Children.LastOrDefault(
                    c => c is Border b && b.AutomationId == "LoadMoreBtn");
                if (oldLoadMore != null) NovelList.Children.Remove(oldLoadMore);

                _loadedNovels.AddRange(result.Novels);
                RenderNovelsIntoGrid(result.Novels);

                // Load more button if there are more pages
                if (_hasMore)
                {
                    var loadMoreBtn = BuildLoadMoreButton();
                    NovelList.Children.Add(loadMoreBtn);
                }
            });
        }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LoadingState.IsVisible = false;

                // For CF-protected sources, show WebView-only state on error
                if (_source.RequiresCfBypass && NovelList.Children.Count == 0)
                {
                    ShowWebViewOnlyState();
                    return;
                }

                EmptyState.IsVisible   = NovelList.Children.Count == 0;
                ListScroll.IsVisible   = NovelList.Children.Count > 0;
                if (NovelList.Children.Count == 0)
                {
                    var errLabel = new Label
                    {
                        Text              = $"Failed to load: {ex.Message}",
                        FontSize          = 12,
                        HorizontalOptions = LayoutOptions.Center,
                        Margin            = new Thickness(16),
                    };
                    errLabel.SetDynamicResource(Label.TextColorProperty, "Danger");
                    NovelList.Children.Add(errLabel);
                    ListScroll.IsVisible = true;
                    EmptyState.IsVisible = false;
                }
            });
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task<ListingPage> GetTop500PageAsync(IBrowsableAdapter source, int page)
    {
        if (source is ShukuBrowse shuku)
        {
            var url = shuku.GetTop500Url(page);
            return await _service.FetchPageAsync(source, url);
        }
        return await _service.GetPopularAsync(source, page);
    }

    // ── Novel card ────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows a friendly prompt for CF-protected or parse-incompatible sources
    /// telling the user to browse in WebView instead.
    /// </summary>
    private void ShowWebViewOnlyState()
    {
        EmptyState.IsVisible = false;
        ListScroll.IsVisible = true;
        NovelList.Children.Clear();

        var icon = new Label
        {
            Text = "\uE894",
            FontFamily = "MaterialSymbols",
            FontSize = 48,
            HorizontalOptions = LayoutOptions.Center,
        };
        icon.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var heading = new Label
        {
            Text = "Browse in WebView",
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Center,
        };
        heading.SetDynamicResource(Label.TextColorProperty, "TextPrimary");

        var sub = new Label
        {
            Text = $"{_source.SiteName} requires a real browser session.\nTap below to open it in WebView.",
            FontSize = 13,
            HorizontalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        sub.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var btnLabel = new Label
        {
            Text = "Open in WebView",
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        btnLabel.SetDynamicResource(Label.TextColorProperty, "TextOnAccent");

        var openBtn = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            HeightRequest = 46,
            Padding = new Thickness(24, 0),
            HorizontalOptions = LayoutOptions.Center,
            Content = btnLabel,
        };
        openBtn.SetDynamicResource(Border.BackgroundColorProperty, "Accent");
        openBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await openBtn.ScaleToAsync(0.95, 60, Easing.CubicOut);
                await openBtn.ScaleToAsync(1.0, 60, Easing.SpringOut);
                var homeUrl = _source.HomeUrl;
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        var nav = Shell.Current?.Navigation;
                        if (nav == null) return;
                        var webPage = new WebBrowsePage(homeUrl);
                        await nav.PushAsync(webPage, true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SourceBrowsePage] WebViewOnly open error: {ex.Message}");
                    }
                });
            })
        });

        var stack = new VerticalStackLayout
        {
            Spacing = 14,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(24, 60),
            Children = { icon, heading, sub, openBtn },
        };
        NovelList.Children.Add(stack);
    }

    private View BuildNovelCard(NovelEntry novel) => BuildCompactCard(novel);

    /// <summary>Compact vertical card — cover fills full height + title overlay at bottom.</summary>
    private View BuildCompactCard(NovelEntry novel)
    {
        const double cardH = 135;

        // ── Cover / fallback ──────────────────────────────────────────────────
        View coverContent;
        if (!string.IsNullOrWhiteSpace(novel.CoverUrl) &&
            Uri.TryCreate(novel.CoverUrl, UriKind.Absolute, out var coverUri))
        {
            var placeholderLily = new Image
            {
                Source            = ImageSource.FromFile("lily.png"),
                Aspect            = Aspect.AspectFit,
                WidthRequest      = 28,
                HeightRequest     = 28,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
                Opacity           = 0.35,
            };

            var coverImg = new Image
            {
                Aspect            = Aspect.AspectFill,
                HeightRequest     = cardH,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions   = LayoutOptions.Fill,
            };

            var coverGrid = new Grid
            {
                HeightRequest     = cardH,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions   = LayoutOptions.Fill,
            };
            coverGrid.SetDynamicResource(Grid.BackgroundColorProperty, "BgInput");
            coverGrid.Add(placeholderLily);
            coverGrid.Add(coverImg);
            coverContent = coverGrid;

            LoadCoverImageAsync(coverImg, novel.CoverUrl, placeholderLily);
        }
        else
        {
            // lily.png centred on AccentContainer, exactly like HistoryCard compact mode
            var lilyImg = new Image
            {
                Source            = ImageSource.FromFile("lily.png"),
                Aspect            = Aspect.AspectFit,
                WidthRequest      = 28,
                HeightRequest     = 28,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
                Opacity           = 0.45,
            };
            var fallback = new Grid
            {
                HeightRequest     = cardH,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions   = LayoutOptions.Fill,
            };
            fallback.SetDynamicResource(Grid.BackgroundColorProperty, "AccentContainer");
            fallback.Add(lilyImg);
            coverContent = fallback;
        }

        // ── Title overlay at bottom ───────────────────────────────────────────
        string displayTitle = System.Net.WebUtility.HtmlDecode(novel.Title ?? "");
        displayTitle = Regex.Replace(displayTitle, @"[【\[][^】\]]*[】\]]", "").Trim();
        displayTitle = Regex.Replace(displayTitle, @"\s*\(\d+\)\s*$", "").Trim();
        if (string.IsNullOrWhiteSpace(displayTitle)) displayTitle = novel.Title ?? "";

        // Use translated title if available and translate toggle is enabled
        if (_translateTitles && !string.IsNullOrWhiteSpace(novel.Title) &&
            _translatedTitles.TryGetValue(novel.Title, out string? translatedTitle))
        {
            displayTitle = translatedTitle;
        }

        var titleLbl = new Label
        {
            Text           = displayTitle,
            FontSize       = 10,
            FontAttributes = FontAttributes.Bold,
            TextColor      = Colors.White,
            LineBreakMode  = LineBreakMode.TailTruncation,
            MaxLines       = 2,
            Margin         = new Thickness(4, 2),
        };

        var titleOverlay = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Color.FromRgba(0, 0, 0, 170),
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle
            {
                CornerRadius = new CornerRadius(0, 0, 10, 10)
            },
            VerticalOptions   = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.Fill,
            Content           = titleLbl,
        };

        // Cover grid stacks cover + overlay; HorizontalOptions.Fill makes it stretch to column width
        var cardGrid = new Grid
        {
            HeightRequest     = cardH,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions   = LayoutOptions.Fill,
        };
        cardGrid.Add(coverContent);
        cardGrid.Add(titleOverlay);

        // The card itself has a fixed aspect-like height so every card row is uniform
        var card = new Border
        {
            StrokeThickness   = 1,
            StrokeShape       = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Padding           = new Thickness(0),
            HeightRequest     = cardH,
            HorizontalOptions = LayoutOptions.Fill,
            Content           = cardGrid,
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        card.SetDynamicResource(Border.StrokeProperty, "Stroke");

        // ── Tap → detail sheet or WebView ─────────────────────────────────────
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await card.ScaleToAsync(0.95, 55, Easing.CubicOut);
                await card.ScaleToAsync(1.0,  55, Easing.SpringOut);
                await HandleCardTapAsync(novel);
            })
        });

        // ── Card long press (Card Options) ───────────────────────────────────
        AttachCardLongPress(card, novel);

        return card;
    }
    private View BuildListCard(NovelEntry novel)
    {
        // Cover
        View coverView;
        if (!string.IsNullOrWhiteSpace(novel.CoverUrl) &&
            Uri.TryCreate(novel.CoverUrl, UriKind.Absolute, out var coverUri))
        {
            var placeholderLily = new Image
            {
                Source            = ImageSource.FromFile("lily.png"),
                Aspect            = Aspect.AspectFit,
                WidthRequest      = 22,
                HeightRequest     = 22,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
                Opacity           = 0.35,
            };

            var img = new Image
            {
                Aspect            = Aspect.AspectFill,
                WidthRequest      = 64,
                HeightRequest     = 92,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
            };

            var coverGrid = new Grid
            {
                WidthRequest      = 64,
                HeightRequest     = 92,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
            };
            coverGrid.Add(placeholderLily);
            coverGrid.Add(img);

            var coverBorder = new Border
            {
                StrokeThickness = 0,
                StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                WidthRequest    = 64,
                HeightRequest   = 92,
                Content         = coverGrid,
            };
            coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
            coverView = coverBorder;
            AttachCoverImageLongPress(coverBorder, novel.CoverUrl.Trim());

            LoadCoverImageAsync(img, novel.CoverUrl, placeholderLily);
        }
        else
        {
            // No cover — lily.png fallback on AccentContainer, same as history
            var lilyImg = new Image
            {
                Source            = ImageSource.FromFile("lily.png"),
                Aspect            = Aspect.AspectFit,
                WidthRequest      = 28,
                HeightRequest     = 28,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
                Opacity           = 0.45,
            };
            coverView = new Border
            {
                StrokeThickness = 0,
                StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                WidthRequest    = 64,
                HeightRequest   = 92,
                Content         = lilyImg,
            };
            ((Border)coverView).SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
        }

        var titleLbl = new Label
        {
            Text          = novel.Title,
            FontSize      = 14,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines      = 2,
        };
        titleLbl.SetDynamicResource(Label.TextColorProperty, "TextPrimary");

        var authorLbl = new Label
        {
            Text      = novel.Author ?? "",
            FontSize  = 12,
            IsVisible = !string.IsNullOrWhiteSpace(novel.Author),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines  = 1,
        };
        authorLbl.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        string chapterText = novel.ChapterText
            ?? (novel.ChapterCount.HasValue ? $"{novel.ChapterCount} ch" : "");
        var chapterLbl = new Label
        {
            Text      = chapterText,
            FontSize  = 10,
            IsVisible = !string.IsNullOrWhiteSpace(chapterText),
            LineBreakMode = LineBreakMode.NoWrap,
        };
        chapterLbl.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        var descLbl = new Label
        {
            Text      = novel.Description ?? "",
            FontSize  = 11,
            IsVisible = !string.IsNullOrWhiteSpace(novel.Description),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines  = 2,
        };
        descLbl.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        // Download button
        var dlIcon = new Label
        {
            Text            = "\uF090",
            FontFamily      = "MaterialSymbols",
            FontSize        = 14,
            VerticalOptions = LayoutOptions.Center,
            Margin          = new Thickness(0, 0, 4, 0),
        };
        dlIcon.SetDynamicResource(Label.TextColorProperty, "TextOnAccent");
        var dlText = new Label
        {
            Text            = "Download",
            FontSize        = 11,
            FontAttributes  = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
        };
        dlText.SetDynamicResource(Label.TextColorProperty, "TextOnAccent");

        var dlBtn = new Border
        {
            StrokeThickness = 0,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            HeightRequest   = 32,
            Padding         = new Thickness(10, 0),
            HorizontalOptions = LayoutOptions.Start,
            Content         = new HorizontalStackLayout
            {
                Spacing           = 0,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
                Children          = { dlIcon, dlText }
            },
        };
        dlBtn.SetDynamicResource(Border.BackgroundColorProperty, "Accent");
        dlBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await dlBtn.ScaleToAsync(0.93, 70, Easing.CubicOut);
                await dlBtn.ScaleToAsync(1.0,  70, Easing.SpringOut);
                OnDownloadTapped(novel);
            })
        });

        var textStack = new VerticalStackLayout
        {
            Spacing         = 4,
            VerticalOptions = LayoutOptions.Center,
            Children        = { titleLbl, authorLbl, chapterLbl, descLbl, dlBtn }
        };

        var contentGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
            },
            ColumnSpacing = 12,
            Padding       = new Thickness(14),
        };
        contentGrid.Add(coverView,  0, 0);
        contentGrid.Add(textStack,  1, 0);

        var card = new Border
        {
            StrokeThickness = 1,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
            Padding         = new Thickness(0),
            Content         = contentGrid,
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        card.SetDynamicResource(Border.StrokeProperty, "Stroke");

        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await card.ScaleToAsync(0.97, 60, Easing.CubicOut);
                await card.ScaleToAsync(1.0,  60, Easing.SpringOut);
                await HandleCardTapAsync(novel);
            })
        });

        // ── Card long press (Card Options) ───────────────────────────────────
        AttachCardLongPress(card, novel);

        return card;
    }

    private async Task HandleCardTapAsync(NovelEntry novel)
    {
        if (_cardViewMode)
        {
            await ShowNovelDetailAsync(novel);
        }
        else
        {
            if (Shell.Current?.Navigation == null) return;
            var topPage = Shell.Current.Navigation.NavigationStack?.LastOrDefault();
            if (topPage is WebBrowsePage) return;

            // WebBrowsePage constructor touches native views — must run on main thread.
            // Use BeginInvokeOnMainThread to avoid potential deadlock from InvokeOnMainThreadAsync
            // when already on the main thread in some code paths.
            var novelUrl = novel.Url;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    var webPage = new WebBrowsePage(novelUrl);
                    var nav = Shell.Current?.Navigation;
                    if (nav != null)
                        await nav.PushAsync(webPage, true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SourceBrowsePage] WebView nav error: {ex.Message}");
                }
            });
        }
    }

    private View BuildLoadMoreButton()
    {
        var lbl = new Label
        {
            Text              = "Load more",
            FontSize          = 13,
            FontAttributes    = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions   = LayoutOptions.Center,
        };
        lbl.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        var btn = new Border
        {
            StrokeThickness   = 1,
            StrokeShape       = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            HeightRequest     = 44,
            HorizontalOptions = LayoutOptions.Fill,
            Content           = lbl,
        };
        btn.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        btn.SetDynamicResource(Border.StrokeProperty, "Stroke");

        btn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                // Remove this button, load next page
                NovelList.Children.Remove(btn);
                await LoadPageAsync(reset: false);
            })
        });

        return btn;
    }

    private void OnDownloadTapped(NovelEntry novel)
    {
        // English-only sources (noveldex.io) should never be translated
        bool isEnglishSource = _source.SiteName.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase);
        bool? translateOverride = isEnglishSource ? false : null; // null = use user's global preference

        Services.DownloadManager.Instance.Enqueue(novel.Url, 0,
            string.IsNullOrWhiteSpace(novel.CoverUrl) ? null : novel.CoverUrl,
            translate: translateOverride);

        // Navigate to Downloads tab
        if (Shell.Current != null)
            _ = Shell.Current.GoToAsync("//DownloadsPage");
    }

    // ── Card long-press options ─────────────────────────────────────────────

    private void AttachCardLongPress(View targetView, NovelEntry novel)
    {
        CancellationTokenSource? lpCts = null;
        var pointerGesture = new PointerGestureRecognizer();

        pointerGesture.PointerPressed += async (_, _) =>
        {
            try
            {
                lpCts?.Cancel();
                lpCts?.Dispose();
                var cts = new CancellationTokenSource();
                lpCts = cts;
                try
                {
                    await Task.Delay(500, cts.Token);
                    TryHapticLight();
                    string? coverUrl = string.IsNullOrWhiteSpace(novel.CoverUrl) ? null : novel.CoverUrl.Trim();
                    string title = novel.Title;
                    string novelUrl = novel.Url;
                    MainThread.BeginInvokeOnMainThread(() => _ = ShowImageContextMenuAsync(coverUrl, title, novelUrl));
                }
                catch (OperationCanceledException) { /* short tap or scroll cancelled */ }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SourceBrowsePage] Card long-press error: {ex.Message}");
            }
        };

        void CancelLongPress()
        {
            try
            {
                if (lpCts != null && !lpCts.IsCancellationRequested)
                    lpCts.Cancel();
            }
            catch { /* ignore */ }
        }

        pointerGesture.PointerReleased += (_, _) => CancelLongPress();
        pointerGesture.PointerMoved    += (_, _) => CancelLongPress();
        pointerGesture.PointerExited   += (_, _) => CancelLongPress();

        targetView.GestureRecognizers.Add(pointerGesture);
    }

    private void AttachCoverImageLongPress(View coverView, string imageUrl, string? novelTitle = null)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) ||
            !Uri.TryCreate(imageUrl, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return;

        AttachCardLongPress(coverView, new NovelEntry(novelTitle ?? "", null, "", imageUrl, null, null, null, null));
    }

#if ANDROID
    private static void TryHapticLight()
    {
        try
        {
#pragma warning disable CA1416
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.S)
            {
                var vibratorManager = global::Android.App.Application.Context
                    .GetSystemService(global::Android.Content.Context.VibratorManagerService) as global::Android.OS.VibratorManager;
                var vibrator = vibratorManager?.DefaultVibrator;
                if (vibrator?.HasVibrator == true)
                {
                    var effect = global::Android.OS.VibrationEffect.CreateOneShot(
                        50, global::Android.OS.VibrationEffect.DefaultAmplitude);
                    vibrator.Vibrate(effect);
                }
            }
            else if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            {
#pragma warning disable CA1422
                var vibrator = global::Android.App.Application.Context
                    .GetSystemService(global::Android.Content.Context.VibratorService) as global::Android.OS.Vibrator;
#pragma warning restore CA1422
                if (vibrator?.HasVibrator == true)
                {
                    var effect = global::Android.OS.VibrationEffect.CreateOneShot(
                        50, global::Android.OS.VibrationEffect.DefaultAmplitude);
                    vibrator.Vibrate(effect);
                }
            }
#pragma warning restore CA1416
        }
        catch { /* ignore */ }
    }
#else
    private static void TryHapticLight() { }
#endif

    private async Task ShowImageContextMenuAsync(string? imageUrl, string? novelTitle = null, string? novelUrl = null)
    {
        _currentImageContextMenuUrl = imageUrl;
        _currentContextMenuNovelTitle = novelTitle;
        _currentContextMenuNovelUrl = novelUrl;
        await ShowImageContextMenuSheetAsync(imageUrl, novelTitle, novelUrl);
    }

    private async Task ShowImageContextMenuSheetAsync(string? imageUrl, string? novelTitle = null, string? novelUrl = null)
    {
        if (_isImageContextMenuOpen)
            return;

        _isImageContextMenuOpen = true;
        ImageContextMenuTitleLabel.Text = novelTitle ?? "";
        ImageContextMenuTitleLabel.IsVisible = !string.IsNullOrWhiteSpace(novelTitle);

        string subtitle = !string.IsNullOrWhiteSpace(novelUrl) ? novelUrl : (imageUrl ?? "");
        ImageContextMenuUrlLabel.Text = subtitle;
        ImageContextMenuUrlLabel.IsVisible = !string.IsNullOrWhiteSpace(subtitle);

        bool hasImage = !string.IsNullOrWhiteSpace(imageUrl);
        ImageContextMenuOpenNewTabBtn.IsVisible = hasImage;
        ImageContextMenuCopyImageBtn.IsVisible = hasImage;
        ImageContextMenuCopyUrlBtn.IsVisible = hasImage;
        ImageContextMenuCopyTitleBtn.IsVisible = !string.IsNullOrWhiteSpace(novelTitle);
        ImageContextMenuCopyNovelUrlBtn.IsVisible = !string.IsNullOrWhiteSpace(novelUrl);

        ImageContextMenuOverlay.IsVisible = true;
        ImageContextMenuOverlay.Opacity = 0;
        ImageContextMenuSheet.Opacity = 0;
        ImageContextMenuSheet.TranslationY = 30;

        await Task.WhenAll(
            ImageContextMenuOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            ImageContextMenuSheet.FadeToAsync(1, 180, Easing.CubicOut),
            ImageContextMenuSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async Task HideImageContextMenuSheetAsync()
    {
        if (!_isImageContextMenuOpen)
            return;

        _isImageContextMenuOpen = false;
        await Task.WhenAll(
            ImageContextMenuSheet.FadeToAsync(0, 140, Easing.CubicIn),
            ImageContextMenuSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            ImageContextMenuOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        ImageContextMenuOverlay.IsVisible = false;
    }

    private async void OnImageContextMenuOverlayTapped(object sender, TappedEventArgs e)
        => await HideImageContextMenuSheetAsync();

    private void OnImageContextMenuSheetTapped(object sender, TappedEventArgs e) { }

    private async void OnImageContextMenuCloseTapped(object sender, TappedEventArgs e)
        => await HideImageContextMenuSheetAsync();

    private async void OnImageContextMenuOpenNewTabTapped(object sender, TappedEventArgs e)
    {
        var url = _currentImageContextMenuUrl;
        await HideImageContextMenuSheetAsync();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return;
        if (Shell.Current == null)
            return;

        await ShowImageActionToastAsync("Opening…", displayMs: 600);
        await Shell.Current.Navigation.PushAsync(new ShukaQuestPage(url));
    }

    private async void OnImageContextMenuCopyImageTapped(object sender, TappedEventArgs e)
    {
        var url = _currentImageContextMenuUrl;
        await HideImageContextMenuSheetAsync();
        if (!string.IsNullOrWhiteSpace(url))
            await CopyImageToClipboardAsync(url);
    }

    private async void OnImageContextMenuCopyTitleTapped(object sender, TappedEventArgs e)
    {
        var title = _currentContextMenuNovelTitle;
        await HideImageContextMenuSheetAsync();
        if (!string.IsNullOrWhiteSpace(title))
        {
            await Clipboard.Default.SetTextAsync(title);
            await ShowImageActionToastAsync($"Copied: {title}");
        }
    }

    private async void OnImageContextMenuCopyNovelUrlTapped(object sender, TappedEventArgs e)
    {
        var url = _currentContextMenuNovelUrl;
        await HideImageContextMenuSheetAsync();
        if (!string.IsNullOrWhiteSpace(url))
        {
            await Clipboard.Default.SetTextAsync(url);
            await ShowImageActionToastAsync($"Copied link address");
        }
    }

    private async void OnImageContextMenuCopyUrlTapped(object sender, TappedEventArgs e)
    {
        var url = _currentImageContextMenuUrl;
        await HideImageContextMenuSheetAsync();
        if (!string.IsNullOrWhiteSpace(url))
        {
            await Clipboard.Default.SetTextAsync(url);
            await ShowImageActionToastAsync("Image URL copied!");
        }
    }

    /// <summary>
    /// Same approach as ShukaQuestPage: download bytes, write cache file, Android clipboard URI.
    /// </summary>
    private async Task CopyImageToClipboardAsync(string imageUrl)
    {
        try
        {
            await ShowImageActionToastAsync("Downloading image…", displayMs: 2000);

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(20);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 Chrome/120 Mobile Safari/537.36");

            var bytes = await httpClient.GetByteArrayAsync(imageUrl);

            string ext = ".jpg";
            var lowerUrl = imageUrl.ToLowerInvariant();
            if (lowerUrl.Contains(".png", StringComparison.Ordinal)) ext = ".png";
            else if (lowerUrl.Contains(".gif", StringComparison.Ordinal)) ext = ".gif";
            else if (lowerUrl.Contains(".webp", StringComparison.Ordinal)) ext = ".webp";

            var cachePath = Path.Combine(FileSystem.CacheDirectory, $"shuka_srcbrowse_img_copy{ext}");
            await File.WriteAllBytesAsync(cachePath, bytes);

#if ANDROID
            var ctx = global::Android.App.Application.Context;
            var file = new Java.IO.File(cachePath);
            var uri = global::AndroidX.Core.Content.FileProvider.GetUriForFile(
                ctx, "com.seizue.shuka.fileprovider", file);

            var clip = global::Android.Content.ClipData.NewUri(
                ctx.ContentResolver, "Copied Image", uri);
            var clipboard = (global::Android.Content.ClipboardManager)
                ctx.GetSystemService(global::Android.Content.Context.ClipboardService)!;
            clipboard.PrimaryClip = clip;
#endif
            await ShowImageActionToastAsync("Image copied!");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SourceBrowsePage] CopyImageToClipboardAsync: {ex.Message}");
            try
            {
                await Clipboard.Default.SetTextAsync(imageUrl);
                await ShowImageActionToastAsync("Copied image URL (download failed)");
            }
            catch { /* ignore */ }
        }
    }

    private async Task ShowImageActionToastAsync(string message, int displayMs = 2500)
    {
        ImageActionToastLabel.Text = message;
        ImageActionToast.Opacity = 0;
        ImageActionToast.TranslationY = 20;
        ImageActionToast.IsVisible = true;

        await Task.WhenAll(
            ImageActionToast.FadeToAsync(1.0, 250, Easing.CubicOut),
            ImageActionToast.TranslateToAsync(0, 0, 250, Easing.CubicOut));

        await Task.Delay(displayMs);

        await Task.WhenAll(
            ImageActionToast.FadeToAsync(0, 250, Easing.CubicIn),
            ImageActionToast.TranslateToAsync(0, 20, 250, Easing.CubicIn));

        ImageActionToast.IsVisible = false;
    }

    // ── Novel detail bottom sheet ─────────────────────────────────────────────

    private async Task ShowNovelDetailAsync(NovelEntry novel)
    {
        if (_isDetailSheetOpen) return;

        _activeDetailNovel = novel;

        // Hide translate button for English-only sources (noveldex.io)
        bool isEnglishSource = _source.SiteName.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase);
        TranslateBtn.IsVisible = !isEnglishSource;

        // Reset translated labels
        DetailTitleEnLabel.IsVisible   = false;
        DetailAuthorEnLabel.IsVisible  = false;
        DetailSummaryEnLabel.IsVisible = false;
        TranslateBtnLabel.Text = "Translate";
        TranslateBtnIcon.Text  = "\uE8E2"; // translate icon
        TranslateSpinner.IsRunning = false;
        TranslateSpinner.IsVisible = false;

        // Cover
        if (!string.IsNullOrWhiteSpace(novel.CoverUrl) &&
            Uri.TryCreate(novel.CoverUrl, UriKind.Absolute, out var coverUri))
        {
            DetailCoverBorder.IsVisible   = true;
            DetailCoverFallback.IsVisible = false;
            LoadCoverImageAsync(DetailCoverImage, novel.CoverUrl);
        }
        else
        {
            DetailCoverBorder.IsVisible  = false;
            DetailCoverFallback.IsVisible = true;
        }

        // Title / Author / Chapters
        DetailTitleLabel.Text = novel.Title;

        if (!string.IsNullOrWhiteSpace(novel.Author))
        {
            DetailAuthorLabel.Text      = novel.Author;
            DetailAuthorLabel.IsVisible = true;
        }
        else
        {
            DetailAuthorLabel.IsVisible = false;
        }

        string chapterText = novel.ChapterText
            ?? (novel.ChapterCount.HasValue ? $"{novel.ChapterCount} chapters" : "");
        DetailChapterLabel.Text      = chapterText;
        DetailChapterLabel.IsVisible = !string.IsNullOrWhiteSpace(chapterText);

        // Summary
        bool hasSummary = !string.IsNullOrWhiteSpace(novel.Description);
        DetailSummaryLabel.Text      = novel.Description ?? "";
        DetailSummaryLabel.IsVisible = hasSummary;
        DetailNoSummaryLabel.IsVisible = !hasSummary;

        // Show sheet
        _isDetailSheetOpen = true;
        NovelDetailOverlay.IsVisible    = true;
        NovelDetailOverlay.Opacity      = 0;
        NovelDetailSheet.TranslationY   = 600;
        NovelDetailSheet.Opacity        = 1;

        await Task.WhenAll(
            NovelDetailOverlay.FadeToAsync(1, 200, Easing.CubicOut),
            NovelDetailSheet.TranslateToAsync(0, 0, 260, Easing.CubicOut));
    }

    private async Task HideNovelDetailAsync()
    {
        if (!_isDetailSheetOpen) return;

        // Cancel any in-progress translation
        _translateCts?.Cancel();
        _translateCts?.Dispose();
        _translateCts = null;

        _isDetailSheetOpen = false;

        await Task.WhenAll(
            NovelDetailSheet.TranslateToAsync(0, 600, 220, Easing.CubicIn),
            NovelDetailOverlay.FadeToAsync(0, 200, Easing.CubicIn));

        NovelDetailOverlay.IsVisible = false;
        _activeDetailNovel = null;
    }

    private void OnNovelDetailOverlayTapped(object sender, TappedEventArgs e)
        => _ = HideNovelDetailAsync();

    private void OnNovelDetailSheetTapped(object sender, TappedEventArgs e) { /* absorb */ }

    private void OnNovelDetailCloseTapped(object sender, TappedEventArgs e)
        => _ = HideNovelDetailAsync();

    // ── Translate (title + author + summary only) ─────────────────────────────

    private async void OnTranslateTapped(object sender, TappedEventArgs e)
    {
        var novel = _activeDetailNovel;
        if (novel == null) return;

        // If already translated, toggle off
        bool alreadyTranslated = DetailTitleEnLabel.IsVisible
                              || DetailSummaryEnLabel.IsVisible;
        if (alreadyTranslated)
        {
            DetailTitleEnLabel.IsVisible   = false;
            DetailAuthorEnLabel.IsVisible  = false;
            DetailSummaryEnLabel.IsVisible = false;
            TranslateBtnLabel.Text = "Translate";
            TranslateBtnIcon.Text  = "\uE8E2";
            return;
        }

        // Start translation
        _translateCts?.Cancel();
        _translateCts?.Dispose();
        _translateCts = new CancellationTokenSource();
        var ct = _translateCts.Token;

        TranslateSpinner.IsRunning = true;
        TranslateSpinner.IsVisible = true;
        TranslateBtnLabel.Text     = "Translating…";
        TranslateBtnIcon.Text      = "\uE8E2";

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var translator = new Shuka.Core.Translator(http);

            // Translate title, author and summary concurrently
            var titleTask  = string.IsNullOrWhiteSpace(novel.Title)
                ? Task.FromResult<string?>(null)
                : TranslateSafeAsync(translator, novel.Title, ct);

            var authorTask = string.IsNullOrWhiteSpace(novel.Author)
                ? Task.FromResult<string?>(null)
                : TranslateSafeAsync(translator, novel.Author, ct);

            var summaryTask = string.IsNullOrWhiteSpace(novel.Description)
                ? Task.FromResult<string?>(null)
                : TranslateSafeAsync(translator, novel.Description, ct);

            await Task.WhenAll(titleTask, authorTask, summaryTask);

            ct.ThrowIfCancellationRequested();

            string? titleEn  = await titleTask;
            string? authorEn = await authorTask;
            string? summaryEn = await summaryTask;

            // Show translated labels only if result differs from original
            if (!string.IsNullOrWhiteSpace(titleEn) &&
                !titleEn.Equals(novel.Title, StringComparison.OrdinalIgnoreCase))
            {
                DetailTitleEnLabel.Text      = titleEn;
                DetailTitleEnLabel.IsVisible = true;
            }

            if (!string.IsNullOrWhiteSpace(authorEn) &&
                !authorEn.Equals(novel.Author, StringComparison.OrdinalIgnoreCase))
            {
                DetailAuthorEnLabel.Text      = authorEn;
                DetailAuthorEnLabel.IsVisible = true;
            }

            if (!string.IsNullOrWhiteSpace(summaryEn))
            {
                DetailSummaryEnLabel.Text      = summaryEn;
                DetailSummaryEnLabel.IsVisible = true;
                // Hide "no summary" label if we got a translation
                DetailNoSummaryLabel.IsVisible = false;
            }

            TranslateBtnLabel.Text = "Hide translation";
            TranslateBtnIcon.Text  = "\uE8E2";
        }
        catch (OperationCanceledException)
        {
            // Sheet was closed mid-translation — silently discard
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SourceBrowsePage] Translate error: {ex.Message}");
            TranslateBtnLabel.Text = "Translation failed";
        }
        finally
        {
            TranslateSpinner.IsRunning = false;
            TranslateSpinner.IsVisible = false;
        }
    }

    /// <summary>Translate a short string, swallowing non-cancellation exceptions.</summary>
    private static async Task<string?> TranslateSafeAsync(
        Shuka.Core.Translator translator, string text, CancellationToken ct)
    {
        try { return await translator.Translate(text, ct: ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ── Detail sheet action buttons ───────────────────────────────────────────

    private void OnDetailOpenWebTapped(object sender, TappedEventArgs e)
    {
        var novel = _activeDetailNovel;
        _ = HideNovelDetailAsync();
        if (novel == null || string.IsNullOrWhiteSpace(novel.Url)) return;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var nav = Shell.Current?.Navigation;
                if (nav == null) return;
                if (nav.NavigationStack?.LastOrDefault() is WebBrowsePage) return;
                var webPage = new WebBrowsePage(novel.Url);
                await nav.PushAsync(webPage, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SourceBrowsePage] DetailOpenWeb error: {ex.Message}");
            }
        });
    }

    private async void OnDetailMenuTapped(object sender, TappedEventArgs e)
    {
        var novel = _activeDetailNovel;
        if (novel == null) return;
        await HideNovelDetailAsync();
        string? coverUrl = string.IsNullOrWhiteSpace(novel.CoverUrl) ? null : novel.CoverUrl.Trim();
        await ShowImageContextMenuAsync(coverUrl, novel.Title, novel.Url);
    }

    private async void OnDetailDownloadTapped(object sender, TappedEventArgs e)
    {
        var novel = _activeDetailNovel;
        await HideNovelDetailAsync();
        if (novel == null) return;
        OnDownloadTapped(novel);
    }
}
