using Shuka.Android.Behaviors;
using Shuka.Android.Services;
using System.Text.Json;
using System.Text.Json.Serialization;
#if ANDROID
using Android.OS;
using Android.Provider;
using Android.Content;
using AndroidUri = Android.Net.Uri;
#endif

namespace Shuka.Android.Pages;

public partial class SettingsPage : ContentPage
{
    private ReleaseInfo? _pendingRelease;
    private bool         _isUpdating;

    // ── Duplicate resolution sheet state ──────────────────────────────────────
    private bool                        _isDuplicateSheetOpen;
    private TaskCompletionSource<string?>? _duplicateSheetTcs;

    public SettingsPage()
    {
        InitializeComponent();
        FooterVersionLabel.Text = $"Shuka v{UpdateService.InstalledVersion}  ·  Seizue";
        
        // Initialize ad blocker switch state
        AdBlockerSwitch.IsToggled = AdBlockerService.Instance.IsEnabled;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        MainActivity.Instance?.SetTabBarVisible(true);
        TabTransition.Prepare(RootGrid, myTabIndex: 3);
        
        // Run animation and data loading concurrently for better performance
        var animationTask = TabTransition.SlideInAsync(RootGrid);
        var loadTask = Task.Run(() =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RefreshRadios(App.CurrentTheme);
                RefreshDownloadPath();
                RefreshUpdateSection();
            });
        });
        
        await Task.WhenAll(animationTask, loadTask);
    }

    private async Task AnimateIn()
    {
        RootGrid.Opacity      = 1;
        RootGrid.TranslationY = 0;
        await Task.CompletedTask;
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private async void OnThemeObsidian(object sender, TappedEventArgs e)  
    {
        await AnimateThemeSelection((Grid)sender);
        ApplyAndRefresh(AppTheme.Obsidian);
    }
    
    private async void OnThemeRosewood(object sender, TappedEventArgs e)  
    {
        await AnimateThemeSelection((Grid)sender);
        ApplyAndRefresh(AppTheme.Rosewood);
    }
    
    private async void OnThemeSlate(object sender, TappedEventArgs e)     
    {
        await AnimateThemeSelection((Grid)sender);
        ApplyAndRefresh(AppTheme.Slate);
    }
    
    private async void OnThemeParchment(object sender, TappedEventArgs e) 
    {
        await AnimateThemeSelection((Grid)sender);
        ApplyAndRefresh(AppTheme.Frost);
    }

    private async void OnThemeAmoled(object sender, TappedEventArgs e)
    {
        await AnimateThemeSelection((Grid)sender);
        ApplyAndRefresh(AppTheme.Amoled);
    }

    private async void OnThemeParchment2(object sender, TappedEventArgs e)
    {
        await AnimateThemeSelection((Grid)sender);
        ApplyAndRefresh(AppTheme.Parchment);
    }

    private async void OnThemeBlossom(object sender, TappedEventArgs e)
    {
        await AnimateThemeSelection((Grid)sender);
        ApplyAndRefresh(AppTheme.Blossom);
    }

    private async Task AnimateThemeSelection(Grid themeGrid)
    {
        // Quick selection animation
        await themeGrid.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await themeGrid.ScaleToAsync(1.0, 100, Easing.CubicOut);
        
        // Subtle flash effect
        var originalOpacity = themeGrid.Opacity;
        await themeGrid.FadeToAsync(0.7, 50);
        await themeGrid.FadeToAsync(originalOpacity, 150);
    }

    private async void ApplyAndRefresh(AppTheme theme)
    {
        App.ApplyTheme(theme);
        BackgroundColor = (Color)Application.Current!.Resources["BgPage"];
        
        // Animate theme change
        await AnimateThemeChange();
        RefreshRadios(theme);
    }

    private async Task AnimateThemeChange()
    {
        // Subtle page flash to indicate theme change
        var mainContent = (Grid)Content;
        await mainContent.FadeToAsync(0.8, 100);
        await mainContent.FadeToAsync(1.0, 200);
    }

    private void RefreshRadios(AppTheme theme)
    {
        var on     = (string)Application.Current!.Resources["IconRadioOn"];
        var off    = (string)Application.Current.Resources["IconRadioOff"];
        var accent = (Color)Application.Current.Resources["Accent"];
        var muted  = (Color)Application.Current.Resources["TextMuted"];

        RadioObsidian.Text       = theme == AppTheme.Obsidian  ? on : off;
        RadioRosewood.Text       = theme == AppTheme.Rosewood  ? on : off;
        RadioSlate.Text          = theme == AppTheme.Slate      ? on : off;
        RadioParchment.Text      = theme == AppTheme.Frost      ? on : off;
        RadioAmoled.Text         = theme == AppTheme.Amoled     ? on : off;
        RadioParchment2.Text     = theme == AppTheme.Parchment  ? on : off;
        RadioBlossom.Text        = theme == AppTheme.Blossom    ? on : off;

        RadioObsidian.TextColor  = theme == AppTheme.Obsidian  ? accent : muted;
        RadioRosewood.TextColor  = theme == AppTheme.Rosewood  ? accent : muted;
        RadioSlate.TextColor     = theme == AppTheme.Slate      ? accent : muted;
        RadioParchment.TextColor = theme == AppTheme.Frost      ? accent : muted;
        RadioAmoled.TextColor    = theme == AppTheme.Amoled     ? accent : muted;
        RadioParchment2.TextColor= theme == AppTheme.Parchment  ? accent : muted;
        RadioBlossom.TextColor   = theme == AppTheme.Blossom    ? accent : muted;
    }

    // ── Download location ─────────────────────────────────────────────────────

    private void RefreshDownloadPath()
    {
        DownloadPathLabel.Text = DownloadManager.GetOutputDirectory();
    }

    private async void OnChangeDownloadFolderTapped(object sender, TappedEventArgs e)
    {
        // Button press animation
        var grid = (Grid)sender;
        await grid.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await grid.ScaleToAsync(1.0, 100, Easing.CubicOut);

#if ANDROID
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
#pragma warning disable CA1416
            if (!global::Android.OS.Environment.IsExternalStorageManager)
            {
                bool proceed = await DisplayAlertAsync(
                    "Storage Permission Required",
                    "Shuka needs 'All Files Access' to save EPUBs to a custom folder. " +
                    "You'll be taken to the system settings to grant this.",
                    "Open Settings", "Cancel");

                if (!proceed) return;

                var intent = new Intent(Settings.ActionManageAllFilesAccessPermission);
                global::Android.App.Application.Context.StartActivity(
                    intent.AddFlags(ActivityFlags.NewTask));
#pragma warning restore CA1416
                return;
            }
        }

        if (MainActivity.Instance is { } activity)
        {
            var treeUri = await activity.PickFolderAsync();
            if (treeUri == null) return;

            DownloadManager.SetOutputDirectoryFromUri(treeUri);
            await AnimatePathUpdate();
            RefreshDownloadPath();
            await DisplayAlertAsync("Saved",
                $"Downloads will now be saved to:\n{DownloadManager.GetOutputDirectory()}", "OK");
            return;
        }
#else
        string current = DownloadManager.GetOutputDirectory();
        string? result = await DisplayPromptAsync(
            "Download Location",
            "Enter the full folder path where EPUBs will be saved:",
            initialValue: current, maxLength: 300, keyboard: Keyboard.Url);

        if (result == null) return;
        result = result.Trim();
        if (string.IsNullOrWhiteSpace(result))
        {
            await DisplayAlertAsync("Invalid Path", "Path cannot be empty.", "OK");
            return;
        }
        try { Directory.CreateDirectory(result); }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Invalid Path", $"Could not create folder:\n{ex.Message}", "OK");
            return;
        }
        DownloadManager.SetOutputDirectory(result);
        await AnimatePathUpdate();
        RefreshDownloadPath();
        await DisplayAlertAsync("Saved", $"Downloads will now be saved to:\n{result}", "OK");
#endif
    }

    private async Task AnimatePathUpdate()
    {
        // Animate the path label update
        await DownloadPathLabel.FadeToAsync(0.3, 150);
        await DownloadPathLabel.FadeToAsync(1.0, 150);
    }

    private async void OnResetDownloadFolderTapped(object sender, TappedEventArgs e)
    {
        // Button press animation
        var grid = (Grid)sender;
        await grid.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await grid.ScaleToAsync(1.0, 100, Easing.CubicOut);

        DownloadManager.ResetOutputDirectory();
        await AnimatePathUpdate();
        RefreshDownloadPath();
        await DisplayAlertAsync("Reset",
            $"Download location reset to default:\n{DownloadManager.GetOutputDirectory()}", "OK");
    }

    // ── Browser ───────────────────────────────────────────────────────────────

    private void OnAdBlockerToggled(object sender, ToggledEventArgs e)
    {
        AdBlockerService.Instance.IsEnabled = e.Value;
        
        string message = e.Value 
            ? "Ad blocker enabled. Ads and trackers will be blocked in the WebView browser."
            : "Ad blocker disabled. Ads and trackers will load normally.";
        
        System.Diagnostics.Debug.WriteLine($"[Settings] Ad blocker: {(e.Value ? "enabled" : "disabled")}");
    }

    // ── Update ────────────────────────────────────────────────────────────────

    private void RefreshUpdateSection()
    {
        var installed = UpdateService.InstalledVersion;
        UpdateVersionLabel.Text = $"Installed: v{installed}";

        if (UpdateService.HasCachedUpdate())
        {
            UpdateStatusLabel.Text      = "⬆ New version available";
            UpdateStatusLabel.TextColor = (Color)Application.Current!.Resources["Success"];
            UpdateActionLabel.Text      = "Install Update";
            UpdateActionSub.Text        = "Tap to download and install";
            UpdateActionIcon.Text       = "\uF090"; // download icon
            UpdateActionIcon.TextColor  = (Color)Application.Current.Resources["Success"];
        }
        else
        {
            UpdateStatusLabel.Text      = "Up to date";
            UpdateStatusLabel.TextColor = (Color)Application.Current!.Resources["TextMuted"];
            UpdateActionLabel.Text      = "Check for Updates";
            UpdateActionSub.Text        = "Tap to check GitHub releases";
            UpdateActionIcon.Text       = "\uE923"; // system_update icon
            UpdateActionIcon.TextColor  = (Color)Application.Current.Resources["AccentLight"];
        }
    }

    private async void OnUpdateTapped(object sender, TappedEventArgs e)
    {
        // Button press animation
        var grid = (Grid)sender;
        await grid.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await grid.ScaleToAsync(1.0, 100, Easing.CubicOut);

        if (_isUpdating) return;

        // If we already fetched a pending release, go straight to install
        if (_pendingRelease != null && _pendingRelease.IsNewerThan(UpdateService.InstalledVersion))
        {
            await StartInstallAsync(_pendingRelease);
            return;
        }

        // ── Step 1: Check for updates ─────────────────────────────────────────
        await SetUpdateUIWithAnimation(checking: true);

        var release = await UpdateService.GetLatestReleaseAsync();

        if (release == null)
        {
            await SetUpdateUIWithAnimation(checking: false);
            await DisplayAlertAsync("Check Failed",
                "Could not reach GitHub. Check your internet connection.", "OK");
            return;
        }

        var installed = UpdateService.InstalledVersion;

        if (!release.IsNewerThan(installed))
        {
            await SetUpdateUIWithAnimation(checking: false);
            UpdateStatusLabel.Text      = $"Up to date (v{installed})";
            UpdateStatusLabel.TextColor = (Color)Application.Current!.Resources["TextMuted"];
            UpdateActionLabel.Text      = "Check for Updates";
            UpdateActionSub.Text        = "You have the latest version";
            await DisplayAlertAsync("Up to Date",
                $"You're already on the latest version (v{installed}).", "OK");
            return;
        }

        // ── Step 2: Prompt to install ─────────────────────────────────────────
        _pendingRelease = release;
        await SetUpdateUIWithAnimation(checking: false);

        UpdateStatusLabel.Text      = $"⬆ v{release.Version} available";
        UpdateStatusLabel.TextColor = (Color)Application.Current!.Resources["Success"];
        UpdateActionLabel.Text      = "Install Update";
        UpdateActionSub.Text        = $"v{release.Version} · {release.SizeMb:F1} MB";
        UpdateActionIcon.Text       = "\uF090";
        UpdateActionIcon.TextColor  = (Color)Application.Current.Resources["Success"];

        bool confirm = await DisplayAlertAsync(
            $"Update Available — v{release.Version}",
            $"A new version is available.\n\n" +
            $"Current: v{installed}\n" +
            $"Latest:  v{release.Version}\n" +
            $"Size:    {release.SizeMb:F1} MB\n\n" +
            "Download and install now?",
            "Install", "Later");

        if (!confirm) return;

        await StartInstallAsync(release);
    }

    private async Task SetUpdateUIWithAnimation(bool checking)
    {
        UpdateActionLabel.Text = checking ? "Checking..." : "Check for Updates";
        UpdateActionSub.Text   = checking ? "Contacting GitHub..." : "Tap to check GitHub releases";
        UpdateChevron.IsVisible = !checking;
        
        // Animate the status change
        if (checking)
        {
            await UpdateActionIcon.RotateToAsync(360, 1000, Easing.Linear);
            UpdateActionIcon.Rotation = 0;
        }
    }

    private async Task StartInstallAsync(ReleaseInfo release)
    {
        _isUpdating = true;
        UpdateChevron.IsVisible       = false;
        UpdateProgressStack.IsVisible = true;
        UpdateActionLabel.Text        = "Downloading...";
        UpdateActionSub.Text          = $"v{release.Version} · {release.SizeMb:F1} MB";

        // Animate progress stack appearance
        UpdateProgressStack.Opacity = 0;
        UpdateProgressStack.Scale = 0.8;
        await Task.WhenAll(
            UpdateProgressStack.FadeToAsync(1.0, 200),
            UpdateProgressStack.ScaleToAsync(1.0, 200)
        );

        try
        {
            var progress = new Progress<double>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    UpdateProgressBar.Progress  = p;
                    UpdateProgressLabel.Text    = $"{(int)(p * 100)}%";
                    UpdateActionLabel.Text      = $"Downloading... {(int)(p * 100)}%";
                });
            });

            await UpdateService.DownloadAndInstallAsync(release, progress,
                log: msg => MainThread.BeginInvokeOnMainThread(
                    () => UpdateActionSub.Text = msg));

            // The system installer takes over from here.
            UpdateActionLabel.Text = "Installer launched";
            UpdateActionSub.Text   = "Follow the system prompt to complete installation";
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            // "Package conflicts" means the installed APK has a different signing key
            // (e.g. a debug build installed via adb). User must uninstall first.
            if (msg.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE") ||
                msg.Contains("Package conflicts") ||
                msg.Contains("signatures do not match"))
            {
                await DisplayAlertAsync("Signature Mismatch",
                    "The installed version was signed with a different key (likely a debug build).\n\n" +
                    "Please uninstall Shuka first, then install the update.",
                    "OK");
            }
            else
            {
                await DisplayAlertAsync("Download Failed", msg, "OK");
            }
            UpdateActionLabel.Text = "Install Update";
            UpdateActionSub.Text   = "Tap to retry";
        }
        finally
        {
            _isUpdating                   = false;
            UpdateChevron.IsVisible       = true;
            
            // Animate progress stack disappearance
            await Task.WhenAll(
                UpdateProgressStack.FadeToAsync(0, 200),
                UpdateProgressStack.ScaleToAsync(0.8, 200)
            );
            UpdateProgressStack.IsVisible = false;
            UpdateProgressBar.Progress    = 0;
        }
    }

    private void SetUpdateUI(bool checking)
    {
        UpdateActionLabel.Text = checking ? "Checking..." : "Check for Updates";
        UpdateActionSub.Text   = checking ? "Contacting GitHub..." : "Tap to check GitHub releases";
        UpdateChevron.IsVisible = !checking;
    }

    // ── Bookmarks Backup & Restore ────────────────────────────────────────────

    /// <summary>Minimal DTO written to / read from the backup JSON.</summary>
    private sealed class BookmarkBackupEntry
    {
        [JsonPropertyName("url")]          public string Url          { get; set; } = "";
        [JsonPropertyName("title")]        public string Title        { get; set; } = "";
        [JsonPropertyName("siteName")]     public string SiteName     { get; set; } = "";
        [JsonPropertyName("chapterCount")] public int    ChapterCount { get; set; }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
    };

    private async void OnBackupBookmarksTapped(object sender, TappedEventArgs e)
    {
        // Button press animation
        var grid = (Grid)sender;
        await grid.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await grid.ScaleToAsync(1.0, 100, Easing.CubicOut);

        var allBookmarks = BookmarkService.Instance.GetExportSnapshot();
        if (allBookmarks.Count == 0)
        {
            await DisplayAlertAsync("Nothing to Backup",
                "You have no bookmarks saved yet.", "OK");
            return;
        }

        // Serialize to compact backup format
        var entries = allBookmarks.Select(b => new BookmarkBackupEntry
        {
            Url          = b.Url,
            Title        = b.Title,
            SiteName     = b.SiteName,
            ChapterCount = b.ChapterCount,
        }).ToList();

        string json = JsonSerializer.Serialize(entries, _jsonOpts);
        string suggestedName = $"shuka_bookmarks_{DateTime.Now:yyyy-MM-dd}.json";

        try
        {
#if ANDROID
            if (MainActivity.Instance is { } activity)
            {
                var uri = await activity.PickSaveFileAsync(suggestedName);
                if (uri == null) return;  // user cancelled

                activity.WriteStringToUri(uri, json);
                await DisplayAlertAsync("Backup Saved",
                    $"{allBookmarks.Count} bookmark(s) saved successfully.", "OK");
                return;
            }
#else
            // Non-Android fallback: ask for a path
            string defaultPath = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                suggestedName);
            string? path = await DisplayPromptAsync(
                "Save Backup",
                "Enter the full path where the backup file will be saved:",
                initialValue: defaultPath, maxLength: 500, keyboard: Keyboard.Url);

            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.Trim();

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(path, json);
            await DisplayAlertAsync("Backup Saved",
                $"{allBookmarks.Count} bookmark(s) saved to:\n{path}", "OK");
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Backup Failed",
                $"Could not save backup:\n{ex.Message}", "OK");
        }
    }

    /// <summary>Reads the JSON content for Restore — returns null if user cancelled.</summary>
    private async Task<string?> PickRestoreJsonAsync()
    {
#if ANDROID
        if (MainActivity.Instance is { } activity)
        {
            var uri = await activity.PickOpenFileAsync();
            if (uri == null) return null;  // user cancelled
            return activity.ReadUriToString(uri);
        }
#endif
        // Non-Android / fallback: prompt for path
        string? path = await DisplayPromptAsync(
            "Restore Backup",
            "Enter the full path of the backup JSON file:",
            maxLength: 500, keyboard: Keyboard.Url);
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!System.IO.File.Exists(path.Trim()))
        {
            await DisplayAlertAsync("File Not Found",
                "The specified file does not exist.", "OK");
            return null;
        }
        return await System.IO.File.ReadAllTextAsync(path.Trim());
    }

    private async void OnRestoreBookmarksTapped(object sender, TappedEventArgs e)
    {
        // Button press animation
        var grid = (Grid)sender;
        await grid.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await grid.ScaleToAsync(1.0, 100, Easing.CubicOut);

        string? json;
        try
        {
            json = await PickRestoreJsonAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Read Failed",
                $"Could not read backup file:\n{ex.Message}", "OK");
            return;
        }

        if (json == null) return; // user cancelled
        // Parse
        List<BookmarkBackupEntry> entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<BookmarkBackupEntry>>(json, _jsonOpts)
                      ?? new List<BookmarkBackupEntry>();
        }
        catch
        {
            await DisplayAlertAsync("Invalid File",
                "The selected file does not appear to be a valid Shuka bookmark backup.", "OK");
            return;
        }

        if (entries.Count == 0)
        {
            await DisplayAlertAsync("Empty Backup",
                "The backup file contains no bookmarks.", "OK");
            return;
        }

        // Separate into new vs. duplicates
        var service    = BookmarkService.Instance;
        var newEntries = new List<BookmarkBackupEntry>();
        var duplicates = new List<BookmarkBackupEntry>();

        foreach (var entry in entries)
        {
            if (service.IsBookmarked(entry.Url))
                duplicates.Add(entry);
            else
                newEntries.Add(entry);
        }

        // Restore the non-duplicate entries silently
        foreach (var entry in newEntries)
        {
            service.RestoreBookmark(new BookmarkItem
            {
                Url          = entry.Url,
                Title        = entry.Title,
                SiteName     = entry.SiteName,
                ChapterCount = entry.ChapterCount,
                Author       = "",
                BookmarkedAt = DateTime.Now,
                Tags         = new List<string>(),
            }, replace: false);
        }

        int added    = newEntries.Count;
        int replaced = 0;
        int skipped     = 0;
        bool skipAll    = false;
        bool replaceAll = false;

        // Handle duplicates one by one via the in-page sheet
        foreach (var entry in duplicates)
        {
            if (skipAll)
            {
                skipped++;
                continue;
            }

            var existingBm = service.Bookmarks
                .FirstOrDefault(b => string.Equals(b.Url, entry.Url,
                    StringComparison.OrdinalIgnoreCase));

            if (replaceAll)
            {
                service.RestoreBookmark(new BookmarkItem
                {
                    Url          = entry.Url,
                    Title        = entry.Title,
                    SiteName     = entry.SiteName,
                    ChapterCount = entry.ChapterCount,
                    Author       = existingBm?.Author ?? "",
                    BookmarkedAt = existingBm?.BookmarkedAt ?? DateTime.Now,
                    Tags         = existingBm?.Tags ?? new List<string>(),
                }, replace: true);
                replaced++;
                continue;
            }

            string? choice = await ShowDuplicateSheetAsync(entry, existingBm);

            switch (choice)
            {
                case "replace":
                    service.RestoreBookmark(new BookmarkItem
                    {
                        Url          = entry.Url,
                        Title        = entry.Title,
                        SiteName     = entry.SiteName,
                        ChapterCount = entry.ChapterCount,
                        Author       = existingBm?.Author ?? "",
                        BookmarkedAt = existingBm?.BookmarkedAt ?? DateTime.Now,
                        Tags         = existingBm?.Tags ?? new List<string>(),
                    }, replace: true);
                    replaced++;
                    break;

                case "replace_all":
                    service.RestoreBookmark(new BookmarkItem
                    {
                        Url          = entry.Url,
                        Title        = entry.Title,
                        SiteName     = entry.SiteName,
                        ChapterCount = entry.ChapterCount,
                        Author       = existingBm?.Author ?? "",
                        BookmarkedAt = existingBm?.BookmarkedAt ?? DateTime.Now,
                        Tags         = existingBm?.Tags ?? new List<string>(),
                    }, replace: true);
                    replaced++;
                    replaceAll = true;
                    break;

                case "skip_all":
                    skipAll = true;
                    skipped++;
                    break;

                default: // "keep" or dismissed
                    skipped++;
                    break;
            }
        }

        string summary = $"{added} added, {replaced} replaced, {skipped} skipped.";
        await DisplayAlertAsync("Restore Complete", summary, "OK");
    }

    // ── Duplicate Resolution Sheet ────────────────────────────────────────────

    private void UpdateDuplicateSheetMargin()
    {
        double bottomInset = 16;
#if ANDROID
        if (MainActivity.Instance is { } activity)
            bottomInset = Math.Max(bottomInset, activity.GetOverlayBottomInsetDip(14));
#endif
        DuplicateSheet.Margin = new Thickness(12, 0, 12, bottomInset);
    }

    private Task<string?> ShowDuplicateSheetAsync(BookmarkBackupEntry entry, BookmarkItem? existing)
    {
        if (_isDuplicateSheetOpen)
            return Task.FromResult<string?>(null);

        _isDuplicateSheetOpen = true;
        _duplicateSheetTcs    = new TaskCompletionSource<string?>();

        // Populate labels
        DuplicateSheetTitle.Text = $"{entry.Title}  [{entry.SiteName}]";
        DuplicateSheetCurrentChapters.Text = (existing?.ChapterCount ?? 0).ToString();
        DuplicateSheetBackupChapters.Text  = entry.ChapterCount.ToString();

        UpdateDuplicateSheetMargin();

        DuplicateSheetOverlay.IsVisible = true;
        DuplicateSheetOverlay.Opacity   = 0;
        DuplicateSheet.Opacity          = 0;
        DuplicateSheet.TranslationY     = 28;

        _ = Task.WhenAll(
            DuplicateSheetOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            DuplicateSheet.FadeToAsync(1, 180, Easing.CubicOut),
            DuplicateSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));

        return _duplicateSheetTcs.Task;
    }

    private async Task HideDuplicateSheetAsync(string? result)
    {
        if (!_isDuplicateSheetOpen) return;
        _isDuplicateSheetOpen = false;

        await Task.WhenAll(
            DuplicateSheet.FadeToAsync(0, 140, Easing.CubicIn),
            DuplicateSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            DuplicateSheetOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        DuplicateSheetOverlay.IsVisible = false;

        _duplicateSheetTcs?.TrySetResult(result);
        _duplicateSheetTcs = null;
    }

    private void OnDuplicateSheetOverlayTapped(object sender, TappedEventArgs e)
        => _ = HideDuplicateSheetAsync("keep");

    private void OnDuplicateSheetTapped(object sender, TappedEventArgs e) { /* swallow */ }

    private void OnDuplicateSheetCloseTapped(object sender, TappedEventArgs e)
        => _ = HideDuplicateSheetAsync("keep");

    private void OnDuplicateKeepTapped(object sender, TappedEventArgs e)
        => _ = HideDuplicateSheetAsync("keep");

    private void OnDuplicateReplaceTapped(object sender, TappedEventArgs e)
        => _ = HideDuplicateSheetAsync("replace");

    private void OnDuplicateReplaceAllTapped(object sender, TappedEventArgs e)
        => _ = HideDuplicateSheetAsync("replace_all");

    private void OnDuplicateSkipAllTapped(object sender, TappedEventArgs e)
        => _ = HideDuplicateSheetAsync("skip_all");

    // ── Support ───────────────────────────────────────────────────────────────

    private async void OnBugReportTapped(object sender, TappedEventArgs e)
    {
        // Button press animation
        var grid = (Grid)sender;
        await grid.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await grid.ScaleToAsync(1.0, 100, Easing.CubicOut);

        try { await Launcher.Default.OpenAsync(new Uri("https://github.com/seizue/Shuka/issues/new")); }
        catch { await DisplayAlertAsync("Error", "Could not open browser.", "OK"); }
    }

    private async void OnAboutTapped(object sender, TappedEventArgs e)
    {
        // Button press animation
        var grid = (Grid)sender;
        await grid.ScaleToAsync(0.95, 100, Easing.CubicOut);
        await grid.ScaleToAsync(1.0, 100, Easing.CubicOut);

        var topPage = Shell.Current?.Navigation?.NavigationStack?.LastOrDefault();
        if (topPage is AboutPage)
            return;

        await Navigation.PushAsync(new AboutPage());
    }
}
