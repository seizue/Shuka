using Shuka.Core;
using Shuka.Android.Platform;

namespace Shuka.Android.Pages;

public partial class SourceBrowsePage : ContentPage
{
    private readonly IBrowsableAdapter _source;
    private readonly DiscoverService   _service;

    private enum BrowseMode { Recent, Popular, Search }
    private BrowseMode _mode    = BrowseMode.Recent;
    private int        _page    = 1;
    private bool       _loading = false;
    private bool       _hasMore = true;
    private string     _query   = "";

    private bool _isImageContextMenuOpen;
    private string? _currentImageContextMenuUrl;

    public SourceBrowsePage(IBrowsableAdapter source, string? initialQuery = null)
    {
        InitializeComponent();
        _source  = source;
        _service = new DiscoverService(new WebViewCloudflareBypass());

        TitleLabel.Text = source.SiteName;
        SearchEntry.TextChanged += (_, e) =>
            SearchClearBtn.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);

        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            _query = initialQuery;
            _mode  = BrowseMode.Search;
            SearchEntry.Text = initialQuery;
            SearchClearBtn.IsVisible = true;
        }

        RefreshPills();
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
        
        // Only restore tab bar if we're going back to a page that needs it
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

    /// <summary>
    /// Intercepts the hardware/gesture back button and pops the page so it
    /// behaves the same as the in-app back button.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        _ = Shell.Current.Navigation.PopAsync();
        return true;
    }

    private async void OnBackTapped(object sender, TappedEventArgs e)
        => await Shell.Current.Navigation.PopAsync();

    // ── Filter pills ──────────────────────────────────────────────────────────

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

    private void RefreshPills()
    {
        SetPillActive(PillRecent,  _mode == BrowseMode.Recent);
        SetPillActive(PillPopular, _mode == BrowseMode.Popular);
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
                BrowseMode.Search  => await _service.SearchAsync(_source, _query, _page),
                _                  => await _service.GetRecentAsync(_source, _page),
            };

            _hasMore = result.HasNextPage;
            _page++;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                LoadingState.IsVisible = false;

                if (result.Novels.Count == 0 && reset)
                {
                    EmptyState.IsVisible = true;
                    ListScroll.IsVisible = false;
                    return;
                }

                ListScroll.IsVisible = true;
                EmptyState.IsVisible = false;

                foreach (var novel in result.Novels)
                    NovelList.Children.Add(BuildNovelCard(novel));

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

    // ── Novel card ────────────────────────────────────────────────────────────

    private View BuildNovelCard(NovelEntry novel)
    {
        // Cover
        View coverView;
        if (!string.IsNullOrWhiteSpace(novel.CoverUrl) &&
            Uri.TryCreate(novel.CoverUrl, UriKind.Absolute, out var coverUri))
        {
            var img = new Image
            {
                Source            = ImageSource.FromUri(coverUri),
                Aspect            = Aspect.AspectFill,
                WidthRequest      = 64,
                HeightRequest     = 92,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
            };
            var coverBorder = new Border
            {
                StrokeThickness = 0,
                StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                WidthRequest    = 64,
                HeightRequest   = 92,
                Content         = img,
            };
            coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
            coverView = coverBorder;
            AttachCoverImageLongPress(coverBorder, novel.CoverUrl.Trim());
        }
        else
        {
            var ph = new Label
            {
                Text              = "\uEA78",
                FontFamily        = "MaterialSymbols",
                FontSize          = 28,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
            };
            ph.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            coverView = new Border
            {
                StrokeThickness = 0,
                StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                WidthRequest    = 64,
                HeightRequest   = 92,
                Content         = ph,
            };
            ((Border)coverView).SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
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

        // Chapter count badge
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

        return card;
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
        // Pre-fill the Home tab URL entry and switch to it
        Services.DownloadManager.Instance.Enqueue(novel.Url, 0,
            string.IsNullOrWhiteSpace(novel.CoverUrl) ? null : novel.CoverUrl);

        // Navigate to Downloads tab
        if (Shell.Current != null)
            _ = Shell.Current.GoToAsync("//DownloadsPage");
    }

    // ── Cover image long-press → same options as Shuka Quest (list uses MAUI Image, not WebView) ──

    private void AttachCoverImageLongPress(Border coverBorder, string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) ||
            !Uri.TryCreate(imageUrl, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return;

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
                    var urlCopy = imageUrl;
                    MainThread.BeginInvokeOnMainThread(() => _ = ShowImageContextMenuAsync(urlCopy));
                }
                catch (OperationCanceledException) { /* short tap */ }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SourceBrowsePage] Cover long-press: {ex.Message}");
            }
        };

        pointerGesture.PointerReleased += (_, _) =>
        {
            try
            {
                if (lpCts != null && !lpCts.Token.IsCancellationRequested)
                    lpCts.Cancel();
            }
            catch { /* ignore */ }
        };

        coverBorder.GestureRecognizers.Add(pointerGesture);
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

    private async Task ShowImageContextMenuAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return;

        _currentImageContextMenuUrl = imageUrl;
        await ShowImageContextMenuSheetAsync(imageUrl);
    }

    private async Task ShowImageContextMenuSheetAsync(string imageUrl)
    {
        if (_isImageContextMenuOpen)
            return;

        _isImageContextMenuOpen = true;
        ImageContextMenuUrlLabel.Text = imageUrl;
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
}
