using Shuka.Android.Behaviors;
using Shuka.Android.Platforms.Android;
using Shuka.Android.Services;
using Shuka.Core;
using Shuka.Core.Adapters;
using System.Text.RegularExpressions;

namespace Shuka.Android.Pages;

public partial class MainPage : ContentPage
{
    public enum SearchScope
    {
        Global,
        SelectedSource,
        PinnedSources
    }

    public static MainPage? Instance { get; private set; }

    private readonly DiscoverService _discoverService;
    private bool _discoverBuilt = false;
    private CancellationTokenSource? _discoverBannerCts;

    private SearchScope _currentScope = SearchScope.Global;
    private IBrowsableAdapter? _selectedSource;
    private int _currentPage = 1;
    private bool _hasMore = false;
    private string _currentQuery = "";
    private CancellationTokenSource? _searchCts;
    private double _pageWidth = 0; // updated in OnSizeAllocated, used for adaptive grid columns

    // Cache definitions
    private record SearchCacheKey(string Query, SearchScope Scope, string? SelectedSourceSiteName, int Page);
    private record SearchCacheValue(
        List<SourceSearchResult> SourceResults,
        List<(NovelEntry Novel, IBrowsableAdapter Source)> MergedResults,
        DateTime Timestamp
    );
    private static readonly Dictionary<SearchCacheKey, SearchCacheValue> _searchCache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private const string PrefKeyLastSelectedSource = "discover_last_selected_source";

    // Progress trackers
    private class SourceSearchProgressTracker
    {
        public IBrowsableAdapter Source { get; set; } = null!;
        public Label StatusLabel { get; set; } = null!;
        public ActivityIndicator Spinner { get; set; } = null!;
        public Border ContainerBorder { get; set; } = null!;
    }
    private readonly Dictionary<IBrowsableAdapter, SourceSearchProgressTracker> _progressTrackers = new();

    public MainPage()
    {
        InitializeComponent();
        Instance = this;
        _discoverService = new DiscoverService(new Platform.WebViewCloudflareBypass());

        UrlEntry.TextChanged += (_, e) =>
        {
            UrlClearBtn.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);
            // Hide preview card when URL is cleared
            if (string.IsNullOrEmpty(e.NewTextValue))
                PreviewInfoCard.IsVisible = false;
        };
        CoverEntry.TextChanged += (_, e) => CoverClearBtn.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);
        GlobalSearchEntry.TextChanged += (_, e) =>
            GlobalSearchClearBtn.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);

        // Load last selected source
        string lastSelected = Preferences.Default.Get(PrefKeyLastSelectedSource, "");
        _selectedSource = DiscoverService.Sources.FirstOrDefault(s => s.SiteName == lastSelected) 
                          ?? DiscoverService.Sources[0];

        // Initialize Scope UI
        UpdateScopeUi();

        // Subscribe to bookmark changes to update the badge counts
        BookmarkService.Instance.BookmarksChanged += OnBookmarksChanged;

        SetActiveTab(download: true);
    }

    private void OnBookmarksChanged(object? sender, EventArgs e)
    {
        // Rebuild discover sources if they've been built
        if (_discoverBuilt)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                BuildDiscoverSources();
            });
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        MainActivity.Instance?.SetTabBarVisible(true);

        TabTransition.Prepare(RootGrid, myTabIndex: 0);

        // Restore draft inputs that were saved before the app went to background
        string savedUrl = Preferences.Default.Get("draft_url", "");
        string savedCover = Preferences.Default.Get("draft_cover", "");
        string savedChapters = Preferences.Default.Get("draft_chapters", "0");

        if (!string.IsNullOrEmpty(savedUrl) && string.IsNullOrEmpty(UrlEntry.Text))
            UrlEntry.Text = savedUrl;
        if (!string.IsNullOrEmpty(savedCover) && string.IsNullOrEmpty(CoverEntry.Text))
            CoverEntry.Text = savedCover;
        if (ChaptersEntry.Text == "0" || string.IsNullOrEmpty(ChaptersEntry.Text))
            ChaptersEntry.Text = savedChapters;

        // Restore translation preference
        bool translate = Preferences.Default.Get("translate_to_english_enabled", true);
        TranslateSwitch.IsToggled = translate;
        UpdateTranslateOptionUi(translate);

        // Re-apply tab colors in case the theme changed while on another tab
        SetActiveTab(DownloadPanel.IsVisible);
        UpdateDiscoverBottomInset();

        await TabTransition.SlideInAsync(RootGrid);
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0) _pageWidth = width;
        UpdateDiscoverBottomInset();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // Persist draft inputs so they survive app backgrounding / process death
        Preferences.Default.Set("draft_url", UrlEntry.Text ?? "");
        Preferences.Default.Set("draft_cover", CoverEntry.Text ?? "");
        Preferences.Default.Set("draft_chapters", ChaptersEntry.Text ?? "0");
    }

    // ── Top tab switching ─────────────────────────────────────────────────────

    private void OnTabDownloadTapped(object sender, TappedEventArgs e) => SetActiveTab(download: true);
    private void OnTabDiscoverTapped(object sender, TappedEventArgs e)
    {
        SetActiveTab(download: false);
        if (!_discoverBuilt)
        {
            BuildDiscoverSources();
            _discoverBuilt = true;
        }
    }

    private async void OnAllBookmarksTapped(object sender, TappedEventArgs e)
    {
        try
        {
            var btn = AllBookmarksBtn;
            await btn.ScaleToAsync(0.85, 70, Easing.CubicOut);
            await btn.ScaleToAsync(1.0, 70, Easing.SpringOut);

            var nav = Shell.Current?.Navigation;
            if (nav == null) return;
            if (nav.NavigationStack?.LastOrDefault() is BookmarksPage)
                return;

            // Open all-bookmarks view (no source filter — user can filter in-page)
            await nav.PushAsync(new BookmarksPage(filterSiteName: null));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainPage] AllBookmarks tap error: {ex.Message}");
        }
    }

    private void SetActiveTab(bool download)
    {
        DownloadPanel.IsVisible = download;
        DiscoverPanel.IsVisible = !download;
        if (!download)
            UpdateDiscoverBottomInset();

        // Resolve the accent color once from the app resources
        Color accent = (Color)(Application.Current!.Resources["AccentLight"]);
        Color textPrimary = (Color)(Application.Current!.Resources["TextPrimary"]);
        Color textMuted = (Color)(Application.Current!.Resources["TextMuted"]);

        if (download)
        {
            TabDownloadLabel.TextColor = textPrimary;
            TabDownloadBar.Color = accent;
            TabDiscoverLabel.TextColor = textMuted;
            TabDiscoverBar.Color = Colors.Transparent;
        }
        else
        {
            TabDiscoverLabel.TextColor = textPrimary;
            TabDiscoverBar.Color = accent;
            TabDownloadLabel.TextColor = textMuted;
            TabDownloadBar.Color = Colors.Transparent;
        }
    }

    private void UpdateDiscoverBottomInset()
    {
        double bottomInset = 40;
#if ANDROID
        if (MainActivity.Instance is { } activity)
            bottomInset = Math.Max(bottomInset, activity.GetOverlayBottomInsetDip(8));
#endif

        var sourcePad = DiscoverSourceList.Padding;
        DiscoverSourceList.Padding = new Thickness(sourcePad.Left, sourcePad.Top, sourcePad.Right, bottomInset);

        var resultPad = SearchResultsList.Padding;
        SearchResultsList.Padding = new Thickness(resultPad.Left, resultPad.Top, resultPad.Right, bottomInset);
    }

    // ── Fetch callback from WebBrowsePage ────────────────────────────────────

    /// <summary>
    /// Called by WebBrowsePage when the user taps Fetch.
    /// Switches to the Download tab and pre-fills the URL entry.
    /// </summary>
    public void FillUrlFromWebView(string url)
    {
        SetActiveTab(download: true);
        UrlEntry.Text = url;
        // Scroll to top so the URL field is visible
        _ = DownloadPanel.ScrollToAsync(0, 0, false);
    }

    // ── Discover: pin persistence ─────────────────────────────────────────────

    // Pins stored as ordered list of SiteNames (oldest pin = index 0 = shown first)
    private const string PrefKeyPins = "discover_pinned_sources";

    private List<string> LoadPins()
    {
        string raw = Preferences.Default.Get(PrefKeyPins, "");
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split('|').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private void SavePins(List<string> pins)
        => Preferences.Default.Set(PrefKeyPins, string.Join("|", pins));

    private bool IsPinned(string siteName)
        => LoadPins().Contains(siteName);

    private void TogglePin(string siteName)
    {
        var pins = LoadPins();
        if (pins.Contains(siteName))
            pins.Remove(siteName);
        else
            pins.Add(siteName); // append = newest pin last, oldest first
        SavePins(pins);
    }

    // ── Discover: source cards ────────────────────────────────────────────────

    private void BuildDiscoverSources() => RebuildSourceList();

    private void RebuildSourceList()
    {
        var pins = LoadPins();
        var sources = DiscoverService.Sources;

        // Sort: pinned first (oldest pin = lowest index = first), then alphabetical
        var sorted = sources
            .OrderBy(s =>
            {
                int idx = pins.IndexOf(s.SiteName);
                return idx >= 0 ? idx : int.MaxValue; // pinned items by pin age
            })
            .ThenBy(s => s.SiteName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DiscoverSourceList.Children.Clear();

        if (sorted.Count == 0)
        {
            var empty = new Label
            {
                Text = "No sources match your filter.",
                FontSize = 13,
                HorizontalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 24),
            };
            empty.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            DiscoverSourceList.Children.Add(empty);
            return;
        }

        // Section header for pinned
        bool shownPinnedHeader = false;
        bool shownAllHeader = false;

        foreach (var source in sorted)
        {
            bool pinned = pins.Contains(source.SiteName);

            if (pinned && !shownPinnedHeader)
            {
                DiscoverSourceList.Children.Add(MakeSectionHeader("PINNED"));
                shownPinnedHeader = true;
            }
            else if (!pinned && !shownAllHeader && shownPinnedHeader)
            {
                DiscoverSourceList.Children.Add(MakeSectionHeader("ALL SOURCES"));
                shownAllHeader = true;
            }
            else if (!pinned && !shownAllHeader && !shownPinnedHeader)
            {
                DiscoverSourceList.Children.Add(MakeSectionHeader("ALL SOURCES"));
                shownAllHeader = true;
            }

            DiscoverSourceList.Children.Add(BuildSourceCard(source, pinned));
        }
    }

    private Label MakeSectionHeader(string text)
    {
        var lbl = new Label
        {
            Text = text,
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(4, 8, 0, 2),
            CharacterSpacing = 1.2,
        };
        lbl.SetDynamicResource(Label.TextColorProperty, "TextMuted");
        return lbl;
    }

    private View BuildShukaQuestCard()
    {
        // ── Left icon badge ──────────────────────────────────────────────────
        var iconLabel = new Label
        {
            Text = "\uE8B6", // search icon
            FontFamily = "MaterialSymbols",
            FontSize = 22,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        iconLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        var iconBadge = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            WidthRequest = 48,
            HeightRequest = 48,
            VerticalOptions = LayoutOptions.Center,
            Content = iconLabel,
        };
        iconBadge.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");

        // ── Row layout (globe-only) ─────────────────────────────────────────
        // Goal: replace the “Shuka Quest” card text with a globe icon container only.
        // The outer card styling still matches the Source cards.
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto }, // globe
            },
            Padding = new Thickness(14, 14),
        };
        row.Add(iconBadge, 0, 0);

        var card = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            Padding = new Thickness(0),
            Content = row,
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        card.SetDynamicResource(Border.StrokeProperty, "AccentLight"); // Accent border to make it stand out

        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                try
                {
                    var topPage = Shell.Current?.Navigation?.NavigationStack?.LastOrDefault();
                    if (topPage is ShukaQuestPage)
                        return;

                    // Immediate visual feedback
                    var scaleTask = card.ScaleToAsync(0.95, 50, Easing.CubicOut);

                    // Register the fetch callback before opening the WebView
                    ShukaQuestPage.OnUrlFetched = FillUrlFromWebView;

                    // Create ShukaQuestPage with Google as default
                    var questPage = new ShukaQuestPage("https://www.google.com");

                    // Wait for animation and navigate
                    await scaleTask;
                    await card.ScaleToAsync(1.0, 100, Easing.SpringOut);

                    // Ensure the page is not cached by Shell
                    Shell.SetPresentationMode(questPage, PresentationMode.NotAnimated);
                    var nav = Shell.Current?.Navigation;
                    if (nav != null)
                        await nav.PushAsync(questPage, true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainPage] Shuka Quest card tap error: {ex.Message}");
                    await DisplayAlertAsync("Navigation Error",
                        $"Could not open Shuka Quest:\n{ex.Message}", "OK");
                }
            })
        });

        return card;
    }

    private View BuildSourceCard(IBrowsableAdapter source, bool pinned)
    {
        // ── Left icon badge ──────────────────────────────────────────────────
        var iconLabel = new Label
        {
            Text = source.IconGlyph,
            FontFamily = "MaterialSymbols",
            FontSize = 22,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        iconLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        var iconBadge = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            WidthRequest = 48,
            HeightRequest = 48,
            VerticalOptions = LayoutOptions.Center,
            Content = iconLabel,
        };
        iconBadge.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");

        // ── Text stack ───────────────────────────────────────────────────────
        var titleLabel = new Label
        {
            Text = source.SiteName,
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimary");

        var descLabel = new Label
        {
            Text = source.Description,
            FontSize = 11,
        };
        descLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        // CF bypass badge — only shown when the source needs it
        var cfBadge = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(6, 2),
            HorizontalOptions = LayoutOptions.Start,
            IsVisible = source.RequiresCfBypass,
        };
        cfBadge.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
        var cfLabel = new Label
        {
            Text = "CF bypass",
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
        };
        cfLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");
        cfBadge.Content = cfLabel;

        var textStack = new VerticalStackLayout
        {
            Spacing = 3,
            VerticalOptions = LayoutOptions.Center,
            Children = { titleLabel, descLabel, cfBadge },
        };

        // ── Pin button ───────────────────────────────────────────────────────
        var pinIcon = new Label
        {
            Text = pinned ? "\uE9C9" : "\uE9C7", // active pin / default pushpin-style
            FontFamily = "MaterialSymbols",
            FontSize = 20,
            Rotation = 0,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        pinIcon.SetDynamicResource(Label.TextColorProperty,
            pinned ? "AccentLight" : "TextMuted");

        var pinBtn = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 40,
            HeightRequest = 40,
            VerticalOptions = LayoutOptions.Center,
            Content = pinIcon,
        };
        pinBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                TogglePin(source.SiteName);
                RebuildSourceList();
            })
        });

        // ── Bookmark button ──────────────────────────────────────────────────
        int bookmarkCount = BookmarkService.Instance.GetBookmarkCountForSite(source.SiteName);

        var bookmarkIcon = new Label
        {
            Text = bookmarkCount > 0 ? "\uE866" : "\uE867", // bookmark filled / outlined
            FontFamily = "MaterialSymbols",
            FontSize = 20,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        bookmarkIcon.SetDynamicResource(Label.TextColorProperty,
            bookmarkCount > 0 ? "AccentLight" : "TextMuted");

        // Badge showing bookmark count (only if > 0)
        var bookmarkBadgeContainer = new Grid
        {
            WidthRequest = 40,
            HeightRequest = 40,
            VerticalOptions = LayoutOptions.Center,
        };
        bookmarkBadgeContainer.Add(bookmarkIcon);

        if (bookmarkCount > 0)
        {
            // Small circular badge with count
            var badgeCircle = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.Ellipse(),
                WidthRequest = 16,
                HeightRequest = 16,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Start,
                Margin = new Thickness(0, 0, 0, 0),
            };
            badgeCircle.SetDynamicResource(Border.BackgroundColorProperty, "AccentLight");

            var badgeLabel = new Label
            {
                Text = bookmarkCount > 99 ? "99+" : bookmarkCount.ToString(),
                FontSize = 8,
                FontAttributes = FontAttributes.Bold,
                TextColor = Colors.White,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };
            badgeCircle.Content = badgeLabel;

            bookmarkBadgeContainer.Add(badgeCircle);
        }

        var bookmarkBtn = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Content = bookmarkBadgeContainer,
        };
        bookmarkBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await bookmarkBtn.ScaleToAsync(0.85, 70, Easing.CubicOut);
                await bookmarkBtn.ScaleToAsync(1.0, 70, Easing.SpringOut);

                var nav = Shell.Current?.Navigation;
                if (nav == null) return;
                if (nav.NavigationStack?.LastOrDefault() is BookmarksPage)
                    return;

                // Navigate to bookmarks page filtered by this source
                await nav.PushAsync(
                    new BookmarksPage(source.SiteName));
            })
        });

        // ── Chevron ──────────────────────────────────────────────────────────
        var chevron = new Label
        {
            Text = "\uE5CC",
            FontFamily = "MaterialSymbols",
            FontSize = 20,
            VerticalOptions = LayoutOptions.Center,
        };
        chevron.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        // ── Row layout ───────────────────────────────────────────────────────
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },   // icon
                new ColumnDefinition { Width = GridLength.Star },   // text
                new ColumnDefinition { Width = GridLength.Auto },   // bookmark
                new ColumnDefinition { Width = GridLength.Auto },   // pin
                new ColumnDefinition { Width = GridLength.Auto },   // chevron
            },
            ColumnSpacing = 12,
            Padding = new Thickness(14, 14),
        };
        row.Add(iconBadge, 0, 0);
        row.Add(textStack, 1, 0);
        row.Add(bookmarkBtn, 2, 0);
        row.Add(pinBtn, 3, 0);
        row.Add(chevron, 4, 0);

        var card = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            Padding = new Thickness(0),
            Content = row,
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        card.SetDynamicResource(Border.StrokeProperty, "Stroke");

        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                try
                {
                    // Immediate visual feedback - faster animation
                    var scaleTask = card.ScaleToAsync(0.95, 50, Easing.CubicOut);

                    // Get the URL and validate it
                    string url = source.HomeUrl;
                    if (string.IsNullOrWhiteSpace(url))
                        url = source.GetRecentUrl(1);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        await scaleTask;
                        await card.ScaleToAsync(1.0, 100, Easing.SpringOut);
                        await DisplayAlertAsync("Error",
                            $"Could not get browse URL for {source.SiteName}", "OK");
                        return;
                    }

                    var topPage = Shell.Current?.Navigation?.NavigationStack?.LastOrDefault();
                    if (topPage is WebBrowsePage)
                        return;

                    // Register the fetch callback before opening the WebView
                    WebBrowsePage.OnUrlFetched = FillUrlFromWebView;

                    // Wait for animation
                    await scaleTask;
                    await card.ScaleToAsync(1.0, 100, Easing.SpringOut);

                    // WebBrowsePage MUST be created on the main thread (MAUI requirement)
                    var webPage = new WebBrowsePage(url);

                    var nav = Shell.Current?.Navigation;
                    if (nav != null)
                        await nav.PushAsync(webPage, true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainPage] Source card tap error: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[MainPage] Stack trace: {ex.StackTrace}");

                    await DisplayAlertAsync("Navigation Error",
                        $"Could not open {source.SiteName}:\n{ex.Message}", "OK");
                }
            })
        });

        return card;
    }

    // ── Discover: global search ────────────────────────────────────────────────

    private async void OnGlobalSearchCompleted(object sender, EventArgs e)
    {
        string query = GlobalSearchEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query)) return;
        await RunGlobalSearchAsync(query);
    }

    private async void OnGlobalSearchClearTapped(object sender, TappedEventArgs e)
    {
        GlobalSearchEntry.Text = "";
        GlobalSearchClearBtn.IsVisible = false;
        _searchCts?.Cancel();
        ShowSourceList();
    }

    private async void OnBrowserIconTapped(object sender, TappedEventArgs e)
    {
        try
        {
            var nav = Shell.Current?.Navigation;
            if (nav == null) return;
            if (nav.NavigationStack?.LastOrDefault() is ShukaQuestPage)
                return;

            // Register the fetch callback before opening the WebView
            ShukaQuestPage.OnUrlFetched = FillUrlFromWebView;

            // Open Shuka Quest browser
            var questPage = new ShukaQuestPage("https://www.google.com");
            await nav.PushAsync(questPage);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainPage] Browser icon tap error: {ex.Message}");
        }
    }

    private void ShowSourceList()
    {
        SearchProgressScrollView.IsVisible = false;
        SearchResultsView.IsVisible = false;
        DiscoverSourceScrollView.IsVisible = true;
    }

    private async void OnChipGlobalTapped(object sender, EventArgs e)
    {
        if (_currentScope != SearchScope.Global)
        {
            _currentScope = SearchScope.Global;
            UpdateScopeUi();
            string query = GlobalSearchEntry.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(query))
                await RunGlobalSearchAsync(query);
        }
    }

    private async void OnChipSelectedTapped(object sender, EventArgs e)
    {
        if (_currentScope != SearchScope.SelectedSource)
        {
            _currentScope = SearchScope.SelectedSource;
            UpdateScopeUi();
            string query = GlobalSearchEntry.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(query))
                await RunGlobalSearchAsync(query);
        }
        else
        {
            await SelectSourceAsync();
        }
    }

    private async void OnChipPinnedTapped(object sender, EventArgs e)
    {
        if (_currentScope != SearchScope.PinnedSources)
        {
            _currentScope = SearchScope.PinnedSources;
            UpdateScopeUi();
            string query = GlobalSearchEntry.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(query))
                await RunGlobalSearchAsync(query);
        }
    }

    private async Task SelectSourceAsync()
    {
        await OpenSourceSelectModalAsync();
    }

    private async Task OpenSourceSelectModalAsync()
    {
        SourceSelectOptionsContainer.Children.Clear();
        
        foreach (var source in DiscoverService.Sources)
        {
            var isSelected = _selectedSource != null && _selectedSource.SiteName == source.SiteName;
            SourceSelectOptionsContainer.Children.Add(BuildSourceOptionRow(source, isSelected));
        }
        
        // Dynamically adjust padding for bottom inset (tab bar / navigation bar overlap)
        double bottomInset = 32;
#if ANDROID
        if (MainActivity.Instance is { } activity)
            bottomInset = Math.Max(bottomInset, activity.GetOverlayBottomInsetDip(8));
#endif
        SourceSelectBottomSheet.Padding = new Thickness(20, 24, 20, bottomInset);

        SourceSelectModal.IsVisible = true;
        
        SourceSelectBottomSheet.TranslationY = 400;
        SourceSelectModal.Opacity = 0;
        
        await Task.WhenAll(
            SourceSelectModal.FadeToAsync(1, 200, Easing.CubicOut),
            SourceSelectBottomSheet.TranslateToAsync(0, 0, 250, Easing.CubicOut)
        );
    }

    private async Task CloseSourceSelectModalAsync()
    {
        await Task.WhenAll(
            SourceSelectModal.FadeToAsync(0, 200, Easing.CubicIn),
            SourceSelectBottomSheet.TranslateToAsync(0, 400, 200, Easing.CubicIn)
        );
        SourceSelectModal.IsVisible = false;
    }

    private async void OnSourceSelectModalBackgroundTapped(object sender, TappedEventArgs e)
    {
        await CloseSourceSelectModalAsync();
    }

    private async void OnCloseSourceSelectModalTapped(object sender, TappedEventArgs e)
    {
        await CloseSourceSelectModalAsync();
    }

    private View BuildSourceOptionRow(IBrowsableAdapter source, bool isSelected)
    {
        var iconLabel = new Label
        {
            Text = source.IconGlyph,
            FontFamily = "MaterialSymbols",
            FontSize = 18,
            VerticalOptions = LayoutOptions.Center
        };
        iconLabel.SetDynamicResource(Label.TextColorProperty, isSelected ? "AccentLight" : "TextMuted");

        var nameLabel = new Label
        {
            Text = source.SiteName,
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimary");

        var descLabel = new Label
        {
            Text = source.Description,
            FontSize = 11,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center
        };
        descLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var textStack = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { nameLabel, descLabel }
        };

        var checkIcon = new Label
        {
            Text = isSelected ? "\uE876" : "",
            FontFamily = "MaterialSymbols",
            FontSize = 20,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center
        };
        checkIcon.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 12,
            Padding = new Thickness(14, 12)
        };
        rowGrid.Add(iconLabel, 0, 0);
        rowGrid.Add(textStack, 1, 0);
        rowGrid.Add(checkIcon, 2, 0);

        var border = new Border
        {
            StrokeThickness = isSelected ? 1.5 : 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = rowGrid
        };
        
        if (isSelected)
        {
            border.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            border.SetDynamicResource(Border.StrokeProperty, "AccentLight");
        }
        else
        {
            border.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
            border.SetDynamicResource(Border.StrokeProperty, "Stroke");
        }

        border.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await border.ScaleToAsync(0.97, 50, Easing.CubicOut);
                await border.ScaleToAsync(1.0, 50, Easing.CubicIn);
                
                _selectedSource = source;
                Preferences.Default.Set(PrefKeyLastSelectedSource, source.SiteName);
                UpdateScopeUi();
                
                await CloseSourceSelectModalAsync();

                string query = GlobalSearchEntry.Text?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(query))
                {
                    await RunGlobalSearchAsync(query);
                }
            })
        });

        return border;
    }


    private void UpdateScopeUi()
    {
        SetChipActive(ChipGlobal, _currentScope == SearchScope.Global);
        SetChipActive(ChipSelected, _currentScope == SearchScope.SelectedSource);
        SetChipActive(ChipPinned, _currentScope == SearchScope.PinnedSources);

        ChipSelectedLabel.Text = _selectedSource != null ? $"Selected: {_selectedSource.SiteName}" : "Selected Source";
        GlobalSearchEntry.Placeholder = _currentScope switch
        {
            SearchScope.Global => "Search all sources...",
            SearchScope.SelectedSource => $"Search {_selectedSource?.SiteName ?? "selected source"}...",
            SearchScope.PinnedSources => "Search pinned sources...",
            _ => "Search..."
        };
    }

    private void SetChipActive(Border chip, bool active)
    {
        if (active)
        {
            chip.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            chip.SetDynamicResource(Border.StrokeProperty, "AccentLight");
            if (chip.Content is HorizontalStackLayout layout)
            {
                foreach (var child in layout.Children)
                {
                    if (child is Label lbl)
                        lbl.SetDynamicResource(Label.TextColorProperty, "AccentLight");
                }
            }
        }
        else
        {
            chip.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
            chip.SetDynamicResource(Border.StrokeProperty, "Stroke");
            if (chip.Content is HorizontalStackLayout layout)
            {
                foreach (var child in layout.Children)
                {
                    if (child is Label lbl)
                        lbl.SetDynamicResource(Label.TextColorProperty, "TextMuted");
                }
            }
        }
    }

    private async Task RunGlobalSearchAsync(string query, bool isLoadMore = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query)) return;

            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            if (!isLoadMore)
            {
                _currentPage = 1;
                _currentQuery = query;
                _hasMore = false;
                SearchResultsList.Children.Clear();
                _progressTrackers.Clear();
                SearchProgressList.Children.Clear();

                DiscoverSourceScrollView.IsVisible = false;
                SearchResultsView.IsVisible = false;
                SearchProgressScrollView.IsVisible = true;
                SearchProgressSpinner.IsRunning = true;
                SearchProgressHeader.Text = "Searching sources...";
            }

            List<IBrowsableAdapter> sourcesToSearch = new();
            if (_currentScope == SearchScope.Global)
            {
                sourcesToSearch = DiscoverService.Sources.ToList();
            }
            else if (_currentScope == SearchScope.PinnedSources)
            {
                var pins = LoadPins();
                sourcesToSearch = DiscoverService.Sources.Where(s => pins.Contains(s.SiteName)).ToList();
            }
            else if (_currentScope == SearchScope.SelectedSource && _selectedSource != null)
            {
                sourcesToSearch = new List<IBrowsableAdapter> { _selectedSource };
            }

            if (sourcesToSearch.Count == 0)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    SearchProgressSpinner.IsRunning = false;
                    SearchProgressHeader.Text = "No sources to search";
                    var emptyLabel = new Label
                    {
                        Text = _currentScope == SearchScope.PinnedSources 
                            ? "No pinned sources. Pin favorite sources below." 
                            : "No source selected.",
                        FontSize = 13,
                        HorizontalOptions = LayoutOptions.Center,
                        Margin = new Thickness(0, 24),
                    };
                    emptyLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");
                    SearchProgressList.Children.Add(emptyLabel);
                });
                return;
            }

            if (!isLoadMore)
            {
                foreach (var source in sourcesToSearch)
                {
                    SearchProgressList.Children.Add(BuildProgressRow(source));
                }
            }

            var cacheKey = new SearchCacheKey(
                query.Trim().ToLowerInvariant(),
                _currentScope,
                _currentScope == SearchScope.SelectedSource ? _selectedSource?.SiteName : null,
                _currentPage
            );

            List<SourceSearchResult> results = new();
            List<(NovelEntry Novel, IBrowsableAdapter Source)> merged = new();
            bool cacheHit = false;

            if (_searchCache.TryGetValue(cacheKey, out var cachedEntry) && 
                cachedEntry != null &&
                DateTime.UtcNow - cachedEntry.Timestamp < CacheDuration)
            {
                results = cachedEntry.SourceResults;
                merged = cachedEntry.MergedResults;
                cacheHit = true;

                foreach (var r in results)
                {
                    if (r.IsSuccess)
                        UpdateProgress(r.Source, $"{r.Results.Novels.Count} result{(r.Results.Novels.Count == 1 ? "" : "s")} (Cached)", false, true);
                    else
                        UpdateProgress(r.Source, "Failed (Cached)", false, false);
                }
            }
            else
            {
                var cfSources = sourcesToSearch.Where(s => s.RequiresCfBypass).ToList();
                var normalSources = sourcesToSearch.Where(s => !s.RequiresCfBypass).ToList();

                var fetchTasks = normalSources.Select(async source =>
                {
                    UpdateProgress(source, "Searching...", true, false);
                    try
                    {
                        var resultsPage = await _discoverService.SearchAsync(source, query, _currentPage, ct: ct);
                        
                        if (source is ShukuBrowse)
                        {
                            var filtered = resultsPage.Novels.Where(n =>
                                (n.Title != null && n.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                                (n.Author != null && n.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
                            ).ToList();
                            resultsPage = new ListingPage(filtered, resultsPage.HasNextPage, resultsPage.CurrentPage);
                        }

                        UpdateProgress(source, $"{resultsPage.Novels.Count} result{(resultsPage.Novels.Count == 1 ? "" : "s")}", false, true);
                        return new SourceSearchResult(source, resultsPage, true, null);
                    }
                    catch (Exception ex)
                    {
                        UpdateProgress(source, "Failed", false, false);
                        return new SourceSearchResult(source, new ListingPage(new List<NovelEntry>(), false, _currentPage), false, ex.Message);
                    }
                }).ToList();

                var normalResults = await Task.WhenAll(fetchTasks);
                results.AddRange(normalResults);

                foreach (var source in cfSources)
                {
                    UpdateProgress(source, "Searching...", true, false);
                    SourceSearchResult? sourceResult = null;

                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            if (attempt > 1) await Task.Delay(1000, ct);
                            var resultsPage = await _discoverService.SearchAsync(source, query, _currentPage, ct: ct);
                            UpdateProgress(source, $"{resultsPage.Novels.Count} result{(resultsPage.Novels.Count == 1 ? "" : "s")}", false, true);
                            sourceResult = new SourceSearchResult(source, resultsPage, true, null);
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (attempt == 2)
                            {
                                UpdateProgress(source, "Failed", false, false);
                                sourceResult = new SourceSearchResult(source, new ListingPage(new List<NovelEntry>(), false, _currentPage), false, ex.Message);
                            }
                        }
                    }

                    if (sourceResult != null)
                    {
                        results.Add(sourceResult);
                    }

                    if (cfSources.IndexOf(source) < cfSources.Count - 1)
                    {
                        await Task.Delay(500, ct);
                    }
                }
            }

            var successful = results.Where(r => r.IsSuccess).ToList();
            var failed = results.Where(r => !r.IsSuccess).ToList();

            if (!cacheHit)
            {
                if (_currentScope == SearchScope.SelectedSource)
                {
                    var sr = successful.FirstOrDefault();
                    if (sr != null)
                    {
                        merged = sr.Results.Novels.Select(n => (n, sr.Source)).ToList();
                    }
                }
                else
                {
                    merged = MergeAndRankResults(results, query);
                }
                
                CacheSearchResults(cacheKey, results, merged);
            }

            if (_currentScope == SearchScope.SelectedSource)
            {
                var sr = successful.FirstOrDefault();
                _hasMore = sr != null && sr.Results.HasNextPage;
            }
            else
            {
                _hasMore = successful.Any(r => r.Results.HasNextPage) && merged.Count > 0;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                var existingLoadMoreBtn = SearchResultsList.Children.FirstOrDefault(c => c.AutomationId == "LoadMoreButton");
                if (existingLoadMoreBtn != null)
                    SearchResultsList.Children.Remove(existingLoadMoreBtn);

                if (!isLoadMore)
                {
                    if (_currentScope == SearchScope.SelectedSource)
                    {
                        var sourceName = _selectedSource?.SiteName ?? "Selected Source";
                        SearchResultsLabel.Text = merged.Count == 0 && failed.Count == 0
                            ? $"No results for \"{query}\" on {sourceName}"
                            : $"Found {merged.Count} result{(merged.Count == 1 ? "" : "s")} from {sourceName}";
                    }
                    else
                    {
                        int searchedCount = sourcesToSearch.Count;
                        SearchResultsLabel.Text = $"Searched {searchedCount} source{(searchedCount == 1 ? "" : "s")} · Found {merged.Count} result{(merged.Count == 1 ? "" : "s")}";
                    }
                }

                AppendResultPairsToList(merged);

                foreach (var fail in failed)
                {
                    SearchResultsList.Children.Add(BuildSourceUnavailableRow(fail.Source, fail.ErrorMessage, query));
                }

                if (_hasMore)
                {
                    SearchResultsList.Children.Add(BuildLoadMoreButton());
                }

                SearchProgressScrollView.IsVisible = false;
                SearchProgressSpinner.IsRunning = false;
                SearchResultsView.IsVisible = true;
            });
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[MainPage] Search cancelled.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainPage] Search error: {ex}");
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                SearchProgressSpinner.IsRunning = false;
                SearchProgressHeader.Text = "Search failed";
                await DisplayAlertAsync("Search Error", $"An error occurred during search:\n{ex.Message}", "OK");
            });
        }
    }

    private View BuildProgressRow(IBrowsableAdapter source)
    {
        var icon = new Label
        {
            Text = source.IconGlyph,
            FontFamily = "MaterialSymbols",
            FontSize = 18,
            VerticalOptions = LayoutOptions.Center
        };
        icon.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        var nameLabel = new Label
        {
            Text = source.SiteName,
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimary");

        var statusLabel = new Label
        {
            Text = "Queued",
            FontSize = 11,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center
        };
        statusLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var spinner = new ActivityIndicator
        {
            IsRunning = false,
            IsVisible = false,
            Color = (Color)(Application.Current?.Resources["AccentLight"] ?? Colors.DeepPink),
            WidthRequest = 14,
            HeightRequest = 14,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center
        };

        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            ColumnSpacing = 10,
            Padding = new Thickness(12, 8),
        };

        rowGrid.Add(icon, 0, 0);
        rowGrid.Add(nameLabel, 1, 0);
        rowGrid.Add(spinner, 2, 0);
        rowGrid.Add(statusLabel, 3, 0);

        var border = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = rowGrid
        };
        border.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        border.SetDynamicResource(Border.StrokeProperty, "Stroke");

        var tracker = new SourceSearchProgressTracker
        {
            Source = source,
            StatusLabel = statusLabel,
            Spinner = spinner,
            ContainerBorder = border
        };
        _progressTrackers[source] = tracker;

        return border;
    }

    private void UpdateProgress(IBrowsableAdapter source, string status, bool isSearching, bool isSuccess)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_progressTrackers.TryGetValue(source, out var tracker) && tracker != null)
            {
                tracker.StatusLabel.Text = status;
                tracker.Spinner.IsRunning = isSearching;
                tracker.Spinner.IsVisible = isSearching;
                if (!isSearching)
                {
                    tracker.StatusLabel.SetDynamicResource(Label.TextColorProperty,
                        isSuccess ? "AccentLight" : "Danger");
                }
            }
        });
    }

    private List<(NovelEntry Novel, IBrowsableAdapter Source)> MergeAndRankResults(
        List<SourceSearchResult> sourceResults, string query)
    {
        var allItems = new List<(NovelEntry Novel, IBrowsableAdapter Source)>();
        foreach (var sr in sourceResults)
        {
            if (sr != null && sr.IsSuccess && sr.Results != null && sr.Results.Novels != null)
            {
                foreach (var novel in sr.Results.Novels)
                {
                    allItems.Add((novel, sr.Source));
                }
            }
        }

        // Group by relevance score
        var groups = allItems
            .GroupBy(item => GetRelevanceScore(item.Novel, query))
            .OrderByDescending(g => g.Key);

        var mergedList = new List<(NovelEntry Novel, IBrowsableAdapter Source)>();

        foreach (var group in groups)
        {
            // Interleave items in this group by source to prevent single source dominance
            var sourceGroups = group
                .GroupBy(item => item.Source.SiteName)
                .Select(g => g.ToList())
                .ToList();

            bool itemsRemaining = true;
            int index = 0;
            while (itemsRemaining)
            {
                itemsRemaining = false;
                foreach (var sg in sourceGroups)
                {
                    if (index < sg.Count)
                    {
                        mergedList.Add(sg[index]);
                        itemsRemaining = true;
                    }
                }
                index++;
            }
        }

        return mergedList;
    }

    private int GetRelevanceScore(NovelEntry novel, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        
        string title = novel.Title ?? "";
        string author = novel.Author ?? "";
        string desc = novel.Description ?? "";

        if (title.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 100;

        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 80;

        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 60;

        if (author.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 40;

        if (desc.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 20;

        return 10;
    }

    private void CacheSearchResults(
        SearchCacheKey key, 
        List<SourceSearchResult> sourceResults, 
        List<(NovelEntry Novel, IBrowsableAdapter Source)> mergedResults)
    {
        var now = DateTime.UtcNow;
        var expired = _searchCache.Where(kvp => now - kvp.Value.Timestamp > CacheDuration).Select(kvp => kvp.Key).ToList();
        foreach (var k in expired) _searchCache.Remove(k);

        _searchCache[key] = new SearchCacheValue(sourceResults, mergedResults, now);
    }

    private View BuildSourceUnavailableRow(IBrowsableAdapter source, string? errorMessage, string query)
    {
        bool likelyCloudflare =
            source.RequiresCfBypass ||
            (!string.IsNullOrWhiteSpace(errorMessage) &&
             (errorMessage.Contains("403", StringComparison.OrdinalIgnoreCase) ||
              errorMessage.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ||
              errorMessage.Contains("forbidden", StringComparison.OrdinalIgnoreCase)));

        string primary = likelyCloudflare
            ? $"Source temporarily blocked ({source.SiteName})"
            : $"Source unavailable right now ({source.SiteName})";
        string secondary = likelyCloudflare
            ? "Cloudflare/site protection blocked this request. You can retry."
            : "The source failed to respond. Tap to retry.";

        var msg = new Label
        {
            Text = primary,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold
        };
        msg.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var detail = new Label
        {
            Text = string.IsNullOrWhiteSpace(errorMessage) ? secondary : $"{secondary}\n{errorMessage}",
            FontSize = 10,
            MaxLines = 2,
            LineBreakMode = LineBreakMode.TailTruncation
        };
        detail.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var retryLabel = new Label
        {
            Text = "Retry",
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        retryLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        Border box = new Border();

        var retryBtn = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            HeightRequest = 28,
            Padding = new Thickness(12, 0),
            HorizontalOptions = LayoutOptions.Start,
            Content = retryLabel
        };
        retryBtn.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        retryBtn.SetDynamicResource(Border.StrokeProperty, "AccentLight");
        retryBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await retryBtn.ScaleToAsync(0.93, 70, Easing.CubicOut);
                await retryBtn.ScaleToAsync(1.0, 70, Easing.SpringOut);
                await RetrySingleSourceAsync(source, query, box);
            })
        });

        box = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Padding = new Thickness(10, 8),
            Content = new VerticalStackLayout
            {
                Spacing = 6,
                Children = { msg, detail, retryBtn }
            }
        };
        box.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        box.SetDynamicResource(Border.StrokeProperty, "Stroke");
        return box;
    }

    private async Task RetrySingleSourceAsync(IBrowsableAdapter source, string query, View currentRow)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        int rowIndex = SearchResultsList.Children.IndexOf(currentRow);
        if (rowIndex < 0)
            return;

        var loading = new ActivityIndicator
        {
            IsRunning = true,
            Color = (Color)(Application.Current?.Resources["AccentLight"] ?? Colors.DeepPink),
            HeightRequest = 32,
            HorizontalOptions = LayoutOptions.Center
        };
        SearchResultsList.Children[rowIndex] = loading;

        var result = await _discoverService.SearchSourceWithStatusAsync(source, query);
        
        if (result.IsSuccess && source is ShukuBrowse)
        {
            var filtered = result.Results.Novels.Where(n =>
                (n.Title != null && n.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (n.Author != null && n.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
            ).ToList();
            result = new SourceSearchResult(source, new ListingPage(filtered, result.Results.HasNextPage, result.Results.CurrentPage), true, null);
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (rowIndex >= SearchResultsList.Children.Count)
                return;

            SearchResultsList.Children.RemoveAt(rowIndex);
            if (result.IsSuccess && result.Results.Novels.Count > 0)
            {
                var retryPairs = result.Results.Novels.Select(n => (n, source)).ToList();
                var retryCards = BuildResultCardRows(retryPairs);
                for (int i = 0; i < retryCards.Count; i++)
                    SearchResultsList.Children.Insert(rowIndex + i, retryCards[i]);
            }
            else
            {
                SearchResultsList.Children.Insert(rowIndex,
                    BuildSourceUnavailableRow(source, result.ErrorMessage, query));
            }
        });
    }

    private View BuildLoadMoreButton()
    {
        var lbl = new Label
        {
            Text = "Load more",
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        lbl.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        var spinner = new ActivityIndicator
        {
            IsRunning = false,
            IsVisible = false,
            Color = (Color)(Application.Current?.Resources["AccentLight"] ?? Colors.DeepPink),
            WidthRequest = 20,
            HeightRequest = 20,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };

        var contentGrid = new Grid
        {
            Children = { lbl, spinner }
        };

        var btn = new Border
        {
            AutomationId = "LoadMoreButton",
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            HeightRequest = 44,
            HorizontalOptions = LayoutOptions.Fill,
            Content = contentGrid,
        };
        btn.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        btn.SetDynamicResource(Border.StrokeProperty, "Stroke");

        btn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                lbl.IsVisible = false;
                spinner.IsVisible = true;
                spinner.IsRunning = true;
                
                _currentPage++;
                await RunGlobalSearchAsync(_currentQuery, isLoadMore: true);
            })
        });

        return btn;
    }

    /// <summary>
    /// Returns the number of grid columns for search results.
    /// 3 columns on phones, 4 on tablets (≥ 600 dp wide).
    /// </summary>
    private int ComputeSearchGridColumns()
    {
        double width = _pageWidth > 0
            ? _pageWidth
            : DeviceDisplay.Current.MainDisplayInfo.Width / DeviceDisplay.Current.MainDisplayInfo.Density;

        // Tablets and large foldables in landscape
        if (width >= 600) return 4;

        // Phones (portrait and landscape)
        return 3;
    }

    /// <summary>
    /// Groups merged results into adaptive-column row Grids and appends them to SearchResultsList.
    /// </summary>
    private void AppendResultPairsToList(List<(NovelEntry Novel, IBrowsableAdapter Source)> items)
    {
        var rows = BuildResultCardRows(items);
        foreach (var row in rows)
            SearchResultsList.Children.Add(row);
    }

    /// <summary>
    /// Builds a list of N-column row Grid views from the provided novel+source pairs.
    /// The last row is padded with transparent fillers when items don't divide evenly.
    /// </summary>
    private List<View> BuildResultCardRows(List<(NovelEntry Novel, IBrowsableAdapter Source)> items)
    {
        int cols = ComputeSearchGridColumns();
        var rows = new List<View>();

        for (int i = 0; i < items.Count; i += cols)
        {
            var colDefs = new ColumnDefinitionCollection();
            for (int c = 0; c < cols; c++)
                colDefs.Add(new ColumnDefinition { Width = GridLength.Star });

            var rowGrid = new Grid
            {
                ColumnDefinitions = colDefs,
                ColumnSpacing = 8,
            };

            for (int c = 0; c < cols; c++)
            {
                int idx = i + c;
                if (idx < items.Count)
                    rowGrid.Add(BuildSearchResultCard(items[idx].Source, items[idx].Novel), c, 0);
                else
                    rowGrid.Add(new BoxView { Color = Colors.Transparent }, c, 0); // filler
            }

            rows.Add(rowGrid);
        }
        return rows;
    }

    private View BuildSearchResultCard(IBrowsableAdapter source, NovelEntry novel)
    {
        // Portrait-style card: cover on top, info below — fits nicely in 3-column grid
        const double coverHeight = 110;
        bool suppressCardTap = false;

        // ── Cover ────────────────────────────────────────────────────────────
        View coverView;
        if (!string.IsNullOrWhiteSpace(novel.CoverUrl) &&
            Uri.TryCreate(novel.CoverUrl, UriKind.Absolute, out var coverUri))
        {
            var img = new Image
            {
                Source = ImageSource.FromUri(coverUri),
                Aspect = Aspect.AspectFill,
                HeightRequest = coverHeight,
                HorizontalOptions = LayoutOptions.Fill,
            };
            coverView = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(12, 12, 0, 0) },
                HeightRequest = coverHeight,
                HorizontalOptions = LayoutOptions.Fill,
                Content = img,
            };
            ((Border)coverView).SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
        }
        else
        {
            var lilyImg = new Image
            {
                Source = ImageSource.FromFile("lily.png"),
                Aspect = Aspect.AspectFit,
                WidthRequest = 36,
                HeightRequest = 36,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Opacity = 0.35,
            };
            coverView = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(12, 12, 0, 0) },
                HeightRequest = coverHeight,
                HorizontalOptions = LayoutOptions.Fill,
                Content = lilyImg,
            };
            ((Border)coverView).SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
        }

        // ── Bookmark saved badge overlay ─────────────────────────────────────
        bool isBookmarked = BookmarkService.Instance.IsBookmarked(novel.Url, source!.SiteName);
        if (isBookmarked)
        {
            var savedBadge = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 5 },
                Padding = new Thickness(4, 2),
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Start,
                Margin = new Thickness(0, 4, 4, 0),
                Content = new Label
                {
                    Text = "\uE866",
                    FontFamily = "MaterialSymbols",
                    FontSize = 9,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                }
            };
            savedBadge.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            ((Label)savedBadge.Content).SetDynamicResource(Label.TextColorProperty, "AccentLight");

            var coverGrid = new Grid();
            coverGrid.Add(coverView);
            coverGrid.Add(savedBadge);
            coverView = coverGrid;
        }

        // ── Source badge ─────────────────────────────────────────────────────
        Border? sourceBadge = null;
        if (source != null)
        {
            sourceBadge = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 5 },
                Padding = new Thickness(5, 2),
                HorizontalOptions = LayoutOptions.Start,
            };
            sourceBadge.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            var badgeLbl = new Label { Text = source.SiteName, FontSize = 8, FontAttributes = FontAttributes.Bold };
            badgeLbl.SetDynamicResource(Label.TextColorProperty, "AccentLight");
            sourceBadge.Content = badgeLbl;
        }

        // ── Title ────────────────────────────────────────────────────────────
        var titleLbl = new Label
        {
            Text = novel.Title,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 2
        };
        titleLbl.SetDynamicResource(Label.TextColorProperty, "TextPrimary");

        // ── Author ───────────────────────────────────────────────────────────
        string authorText = string.IsNullOrWhiteSpace(novel.Author) ? "Unknown" : novel.Author;
        var authorLbl = new Label
        {
            Text = authorText,
            FontSize = 9,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };
        authorLbl.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        // ── Chapter count ─────────────────────────────────────────────────────
        string chapterText = GetChapterSummary(novel);
        var chapterLbl = new Label
        {
            Text = string.IsNullOrWhiteSpace(chapterText) ? "" : $"Ch. {chapterText}",
            FontSize = 9,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };
        chapterLbl.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        // ── Download button ──────────────────────────────────────────────────
        var dlIcon = new Label
        {
            Text = "\uF090",
            FontFamily = "MaterialSymbols",
            FontSize = 11,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
        dlIcon.SetDynamicResource(Label.TextColorProperty, "TextOnAccent");

        var dlBtn = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            WidthRequest = 26,
            HeightRequest = 26,
            Padding = new Thickness(0),
            Content = dlIcon,
        };
        dlBtn.SetDynamicResource(Border.BackgroundColorProperty, "Accent");
        dlBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                suppressCardTap = true;
                await dlBtn.ScaleToAsync(0.93, 70, Easing.CubicOut);
                await dlBtn.ScaleToAsync(1.0, 70, Easing.SpringOut);
                bool translate = Preferences.Default.Get("translate_to_english_enabled", true);
                DownloadManager.Instance.Enqueue(novel.Url, 0,
                    string.IsNullOrWhiteSpace(novel.CoverUrl) ? null : novel.CoverUrl, 0, translate);
                if (Shell.Current != null)
                    await Shell.Current.GoToAsync("//DownloadsPage");
                await Task.Delay(80);
                suppressCardTap = false;
            })
        });

        // ── Bookmark button ──────────────────────────────────────────────────
        var bmIcon = new Label
        {
            Text = isBookmarked ? "\uE866" : "\uE867",
            FontFamily = "MaterialSymbols",
            FontSize = 11,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
        bmIcon.SetDynamicResource(Label.TextColorProperty, isBookmarked ? "AccentLight" : "TextSecondary");

        var bmBtn = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            WidthRequest = 26,
            HeightRequest = 26,
            Padding = new Thickness(0),
            Content = bmIcon,
        };
        bmBtn.SetDynamicResource(Border.BackgroundColorProperty, isBookmarked ? "AccentContainer" : "BgCard");
        bmBtn.SetDynamicResource(Border.StrokeProperty, isBookmarked ? "AccentLight" : "Stroke");
        bmBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                suppressCardTap = true;
                await bmBtn.ScaleToAsync(0.93, 70, Easing.CubicOut);
                await bmBtn.ScaleToAsync(1.0, 70, Easing.SpringOut);

                if (BookmarkService.Instance.IsBookmarked(novel.Url, source!.SiteName))
                {
                    BookmarkService.Instance.RemoveBookmark(novel.Url);
                    bmIcon.Text = "\uE867";
                    bmIcon.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
                    bmBtn.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
                    bmBtn.SetDynamicResource(Border.StrokeProperty, "Stroke");
                    await ShowDiscoverBookmarkBannerAsync($"Removed: {novel.Title}");
                }
                else
                {
                    int knownCount = TryExtractChapterCount(novel);
                    BookmarkService.Instance.AddBookmark(
                        novel.Url,
                        novel.Title,
                        novel.Author ?? "Unknown",
                        source!.SiteName,
                        knownCount,
                        novel.CoverUrl);
                    if (knownCount == 0)
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                int n = await _discoverService.GetChapterCountAsync(novel.Url);
                                if (n > 0)
                                    BookmarkService.Instance.UpdateBookmarkChapterCount(novel.Url, n);
                            }
                            catch { }
                        });
                    bmIcon.Text = "\uE866";
                    bmIcon.SetDynamicResource(Label.TextColorProperty, "AccentLight");
                    bmBtn.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
                    bmBtn.SetDynamicResource(Border.StrokeProperty, "AccentLight");
                    await ShowDiscoverBookmarkBannerAsync($"Saved: {novel.Title}");
                }

                await Task.Delay(80);
                suppressCardTap = false;
            })
        });

        // ── Action row ───────────────────────────────────────────────────────
        var actionRow = new HorizontalStackLayout
        {
            Spacing = 4,
            HorizontalOptions = LayoutOptions.End,
            Children = { dlBtn, bmBtn }
        };

        // ── Info stack (bottom of card) ──────────────────────────────────────
        var infoRows = new VerticalStackLayout { Spacing = 0 };
        if (sourceBadge != null) infoRows.Children.Add(sourceBadge);
        infoRows.Children.Add(titleLbl);
        infoRows.Children.Add(authorLbl);
        if (!string.IsNullOrWhiteSpace(chapterLbl.Text)) infoRows.Children.Add(chapterLbl);

        var bottomSection = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Padding = new Thickness(8, 6),
            ColumnSpacing = 4,
        };
        bottomSection.Add(infoRows, 0, 0);
        bottomSection.Add(actionRow, 1, 0);
        Grid.SetRowSpan(bottomSection.Children[0] as View ?? new BoxView(), 1);

        // ── Assemble portrait card ───────────────────────────────────────────
        var cardContent = new VerticalStackLayout
        {
            Spacing = 0,
            Children = { coverView, bottomSection }
        };

        var card = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(0),
            Content = cardContent,
            HorizontalOptions = LayoutOptions.Fill,
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        card.SetDynamicResource(Border.StrokeProperty, "Stroke");
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                if (suppressCardTap)
                    return;

                var scaleTask = card.ScaleToAsync(0.95, 50, Easing.CubicOut);

                WebBrowsePage? webPage = null;
                await Task.Run(() =>
                {
                    webPage = new WebBrowsePage(novel.Url);
                });

                await scaleTask;
                await card.ScaleToAsync(1.0, 100, Easing.SpringOut);

                if (webPage != null)
                    await Shell.Current.Navigation.PushAsync(webPage);
            })
        });
        return card;
    }

    private async Task ShowDiscoverBookmarkBannerAsync(string message)
    {
        _discoverBannerCts?.Cancel();
        _discoverBannerCts = new CancellationTokenSource();
        var token = _discoverBannerCts.Token;

        DiscoverBookmarkBannerLabel.Text = message;
        DiscoverBookmarkBanner.IsVisible = true;
        DiscoverBookmarkBanner.Opacity = 0;
        DiscoverBookmarkBanner.TranslationY = 10;
        await Task.WhenAll(
            DiscoverBookmarkBanner.FadeToAsync(1, 160, Easing.CubicOut),
            DiscoverBookmarkBanner.TranslateToAsync(0, 0, 160, Easing.CubicOut));

        try
        {
            await Task.Delay(1800, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        await Task.WhenAll(
            DiscoverBookmarkBanner.FadeToAsync(0, 150, Easing.CubicIn),
            DiscoverBookmarkBanner.TranslateToAsync(0, 8, 150, Easing.CubicIn));
        DiscoverBookmarkBanner.IsVisible = false;
    }

    private static string GetChapterSummary(NovelEntry novel)
    {
        if (novel.ChapterCount is > 0)
            return $"{novel.ChapterCount.Value}";

        if (!string.IsNullOrWhiteSpace(novel.ChapterText))
            return novel.ChapterText!;

        foreach (var value in new[] { novel.Tags, novel.Description })
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var chapterCn = Regex.Match(value, @"第\s*([0-9零一二三四五六七八九十百千万两]+)\s*章");
            if (chapterCn.Success)
                return chapterCn.Value.Replace(" ", "");

            var chapterEn = Regex.Match(value, @"\b(?:chapter|ch)\.?\s*([0-9]{1,6})\b", RegexOptions.IgnoreCase);
            if (chapterEn.Success)
                return $"Chapter {chapterEn.Groups[1].Value}";
        }

        return "N/A";
    }

    private static int TryExtractChapterCount(NovelEntry novel)
    {
        foreach (var value in new[] { novel.Tags, novel.Description })
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var chapterCount = Regex.Match(value, @"\b([1-9][0-9]{0,4})\s*chapters?\b", RegexOptions.IgnoreCase);
            if (chapterCount.Success && int.TryParse(chapterCount.Groups[1].Value, out int parsed))
                return parsed;
        }

        return 0;
    }



    // ── Download handlers (unchanged) ─────────────────────────────────────────

    private async void OnUrlPreviewTapped(object sender, TappedEventArgs e)
    {
        await AnimateButtonPress(UrlPreviewBtn);

        string url = UrlEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            await DisplayAlertAsync("Missing URL", "Please enter a novel URL first.", "OK");
            return;
        }

        // Show loading state
        PreviewInfoCard.IsVisible = true;
        PreviewTitle.Text = "Loading...";
        PreviewAuthor.Text = "";
        PreviewChapters.Text = "";

        try
        {
            var service = new BookService(new Platform.WebViewCloudflareBypass());

            // Fetch just the index page to get title, author, and chapter count
            var book = await Task.Run(async () =>
            {
                return await service.GatherBookInfo(url, 0, null,
                    msg => { /* ignore log messages */ },
                    CancellationToken.None, 0);
            });

            // Display the preview info
            MainThread.BeginInvokeOnMainThread(() =>
            {
                PreviewTitle.Text = book.TitleEn ?? book.Title;
                PreviewAuthor.Text = $"by {book.AuthorEn ?? book.Author}";
                PreviewChapters.Text = $"{book.Total} Chapters Available";

                // Animate the card appearance
                PreviewInfoCard.Opacity = 0;
                PreviewInfoCard.TranslationY = -10;
                _ = Task.WhenAll(
                    PreviewInfoCard.FadeToAsync(1.0, 250, Easing.CubicOut),
                    PreviewInfoCard.TranslateToAsync(0, 0, 250, Easing.CubicOut));
            });
        }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                PreviewInfoCard.IsVisible = false;
                await DisplayAlertAsync("Preview Failed",
                    $"Could not fetch novel information:\n{ex.Message}", "OK");
            });
        }
    }

    private async void OnDownloadClicked(object sender, TappedEventArgs e)
    {
        await AnimateButtonPress(DownloadBtn);

        string url = UrlEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            await DisplayAlertAsync("Missing URL", "Please enter a novel URL.", "OK");
            return;
        }

        int chapters = 0, chapterFrom = 0;
        string chapText = ChaptersEntry.Text?.Trim() ?? "0";
        if (chapText.Contains('-'))
        {
            var parts = chapText.Split('-');
            if (parts.Length == 2 &&
                int.TryParse(parts[0].Trim(), out int from) &&
                int.TryParse(parts[1].Trim(), out int to) &&
                from >= 1 && to >= from)
            {
                chapterFrom = from;
                chapters = to - from + 1;
            }
            else
            {
                await DisplayAlertAsync("Invalid Range", "Use format: 100-200 (from chapter 100 to 200)", "OK");
                return;
            }
        }
        else
        {
            chapters = int.TryParse(chapText, out int n) ? n : 0;
        }

        string? coverUrl = string.IsNullOrWhiteSpace(CoverEntry.Text) ? null : CoverEntry.Text.Trim();

        UrlEntry.IsEnabled = CoverEntry.IsEnabled = ChaptersEntry.IsEnabled = false;
        await Task.Delay(50);
        UrlEntry.IsEnabled = CoverEntry.IsEnabled = ChaptersEntry.IsEnabled = true;

        var existing = DownloadManager.Instance.FindExisting(url);
        bool forceRebuild = false;
        if (existing != null)
        {
            bool shouldQueue = await HandleDuplicate(existing);
            if (!shouldQueue) return;
            forceRebuild = true;
        }
        else
        {
            var historyEntry = HistoryService.Instance.Entries.FirstOrDefault(e => e.Url == url);
            if (historyEntry != null && Platforms.Android.EpubOpener.IsAccessible(historyEntry.EpubPath))
            {
                var tempItem = new DownloadItem
                {
                    Url = url,
                    Title = historyEntry.Title,
                    Status = DownloadStatus.Completed,
                    EpubPath = historyEntry.EpubPath
                };
                bool shouldQueue = await HandleDuplicate(tempItem);
                if (!shouldQueue) return;
                forceRebuild = true;
            }
        }

        DownloadManager.Instance.Enqueue(url, chapters, coverUrl, chapterFrom, TranslateSwitch.IsToggled, forceRebuild);
        // Clear the saved draft — the user has submitted it
        Preferences.Default.Remove("draft_url");
        Preferences.Default.Remove("draft_cover");
        Preferences.Default.Set("draft_chapters", "0");
        await AnimateClearInputs();
        await ShowQueuedBanner();
    }

    private async Task AnimateButtonPress(Border button)
    {
        await button.ScaleToAsync(0.95, 80, Easing.CubicOut);
        await button.ScaleToAsync(1.0, 80, Easing.SpringOut);
    }

    private async Task AnimateClearInputs()
    {
        var entries = new[] { UrlEntry, CoverEntry, ChaptersEntry };
        await Task.WhenAll(entries.Select(e => e.FadeToAsync(0.5, 150)));
        UrlEntry.Text = ""; CoverEntry.Text = ""; ChaptersEntry.Text = "0";
        await Task.WhenAll(entries.Select(e => e.FadeToAsync(1.0, 150)));
    }

    private async Task ShowQueuedBanner()
    {
        QueuedBanner.Opacity = 0; QueuedBanner.TranslationY = -20; QueuedBanner.IsVisible = true;
        await Task.WhenAll(QueuedBanner.FadeToAsync(1.0, 300, Easing.CubicOut),
                           QueuedBanner.TranslateToAsync(0, 0, 300, Easing.CubicOut));
        await Task.Delay(3000);
        await Task.WhenAll(QueuedBanner.FadeToAsync(0, 300, Easing.CubicIn),
                           QueuedBanner.TranslateToAsync(0, -20, 300, Easing.CubicIn));
        QueuedBanner.IsVisible = false;
    }

    private async Task<bool> HandleDuplicate(DownloadItem existing)
    {
        string title = string.IsNullOrWhiteSpace(existing.Title) || existing.Title == "Loading..."
            ? "this novel" : $"\"{existing.Title}\"";

        switch (existing.Status)
        {
            case DownloadStatus.Downloading:
            case DownloadStatus.Pending:
            case DownloadStatus.Resuming:
            case DownloadStatus.Paused:
                {
                    string? choice = await DisplayActionSheetAsync($"Already downloading {title}", "Cancel", null,
                        "Go to Downloads tab", "Download again anyway");
                    if (choice == "Go to Downloads tab") { await Shell.Current.GoToAsync("//DownloadsPage"); return false; }
                    return choice == "Download again anyway";
                }
            case DownloadStatus.Completed:
                {
                    string? choice = await DisplayActionSheetAsync($"{title} was already downloaded", "Cancel", null,
                        "Download again (re-translate)", "Open existing EPUB", "Go to Downloads tab");
                    if (choice == "Download again (re-translate)") return true;
                    if (choice == "Open existing EPUB" && existing.EpubPath != null && EpubOpener.IsAccessible(existing.EpubPath))
                    {
                        try
                        {
                            EpubOpener.Open(existing.EpubPath);
                        }
                        catch (InvalidOperationException)
                        {
                            // No EPUB reader — fall back to share sheet
                            try { EpubOpener.Share(existing.EpubPath, existing.Title); }
                            catch { }
                        }
                        catch { }
                        return false;
                    }
                    if (choice == "Go to Downloads tab") { await Shell.Current.GoToAsync("//DownloadsPage"); return false; }
                    return false;
                }
            case DownloadStatus.Failed:
            case DownloadStatus.Cancelled:
                {
                    string statusWord = existing.Status == DownloadStatus.Failed ? "failed" : "cancelled";
                    string? choice = await DisplayActionSheetAsync($"A previous download of {title} {statusWord}",
                        "Cancel", null, "Download again", "Go to Downloads tab");
                    if (choice == "Download again") { DownloadManager.Instance.Dismiss(existing); return true; }
                    if (choice == "Go to Downloads tab") { await Shell.Current.GoToAsync("//DownloadsPage"); return false; }
                    return false;
                }
            default: return true;
        }
    }

    private async void OnUrlPasteTapped(object sender, TappedEventArgs e)
    {
        string? text = await Clipboard.Default.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) UrlEntry.Text = text.Trim();
    }
    private void OnUrlClearTapped(object sender, TappedEventArgs e) => UrlEntry.Text = "";
    private async void OnCoverPasteTapped(object sender, TappedEventArgs e)
    {
        string? text = await Clipboard.Default.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) CoverEntry.Text = text.Trim();
    }
    private void OnCoverClearTapped(object sender, TappedEventArgs e) => CoverEntry.Text = "";

    private void OnTranslateToggled(object sender, ToggledEventArgs e)
    {
        Preferences.Default.Set("translate_to_english_enabled", e.Value);
        UpdateTranslateOptionUi(e.Value);
    }

    private void UpdateTranslateOptionUi(bool translate)
    {
        if (translate)
        {
            TranslateOptionSub.Text = "Translates title, author, and chapters to English using Google Translate";
            DownloadBtnLabel.Text = "Download & Translate";
        }
        else
        {
            TranslateOptionSub.Text = "Skipping translation; EPUB will be in the original language";
            DownloadBtnLabel.Text = "Download Original";
        }
    }
}
