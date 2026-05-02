using Shuka.Android.Services;
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

    public SettingsPage()
    {
        InitializeComponent();
        RefreshRadios(App.CurrentTheme);
        RefreshDownloadPath();
        RefreshUpdateSection();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await AnimateIn();
        RefreshRadios(App.CurrentTheme);
        RefreshDownloadPath();
        RefreshUpdateSection();
    }

    private async Task AnimateIn()
    {
        // Animate body content in — same pattern as all other pages
        BodyScrollView.Opacity = 0;
        BodyScrollView.TranslationY = 18;

        await Task.WhenAll(
            BodyScrollView.FadeToAsync(1.0, 220, Easing.CubicOut),
            BodyScrollView.TranslateToAsync(0, 0, 220, Easing.CubicOut)
        );
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

        RadioObsidian.Text       = theme == AppTheme.Obsidian ? on : off;
        RadioRosewood.Text       = theme == AppTheme.Rosewood ? on : off;
        RadioSlate.Text          = theme == AppTheme.Slate    ? on : off;
        RadioParchment.Text      = theme == AppTheme.Frost    ? on : off;

        RadioObsidian.TextColor  = theme == AppTheme.Obsidian ? accent : muted;
        RadioRosewood.TextColor  = theme == AppTheme.Rosewood ? accent : muted;
        RadioSlate.TextColor     = theme == AppTheme.Slate    ? accent : muted;
        RadioParchment.TextColor = theme == AppTheme.Frost    ? accent : muted;
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
            // The app may be killed and reinstalled — nothing more to do.
            UpdateActionLabel.Text = "Installer launched";
            UpdateActionSub.Text   = "Follow the system prompt to complete installation";
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Download Failed", ex.Message, "OK");
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

        await Navigation.PushAsync(new AboutPage());
    }
}
