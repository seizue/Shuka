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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshRadios(App.CurrentTheme);
        RefreshDownloadPath();
        RefreshUpdateSection();
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private void OnThemeObsidian(object sender, TappedEventArgs e)  => ApplyAndRefresh(AppTheme.Obsidian);
    private void OnThemeRosewood(object sender, TappedEventArgs e)  => ApplyAndRefresh(AppTheme.Rosewood);
    private void OnThemeSlate(object sender, TappedEventArgs e)     => ApplyAndRefresh(AppTheme.Slate);
    private void OnThemeParchment(object sender, TappedEventArgs e) => ApplyAndRefresh(AppTheme.Frost);

    private void ApplyAndRefresh(AppTheme theme)
    {
        App.ApplyTheme(theme);
        BackgroundColor = (Color)Application.Current!.Resources["BgPage"];
        RefreshRadios(theme);
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
        RefreshDownloadPath();
        await DisplayAlertAsync("Saved", $"Downloads will now be saved to:\n{result}", "OK");
#endif
    }

    private async void OnResetDownloadFolderTapped(object sender, TappedEventArgs e)
    {
        DownloadManager.ResetOutputDirectory();
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
        if (_isUpdating) return;

        // If we already fetched a pending release, go straight to install
        if (_pendingRelease != null && _pendingRelease.IsNewerThan(UpdateService.InstalledVersion))
        {
            await StartInstallAsync(_pendingRelease);
            return;
        }

        // ── Step 1: Check for updates ─────────────────────────────────────────
        SetUpdateUI(checking: true);

        var release = await UpdateService.GetLatestReleaseAsync();

        if (release == null)
        {
            SetUpdateUI(checking: false);
            await DisplayAlertAsync("Check Failed",
                "Could not reach GitHub. Check your internet connection.", "OK");
            return;
        }

        var installed = UpdateService.InstalledVersion;

        if (!release.IsNewerThan(installed))
        {
            SetUpdateUI(checking: false);
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
        SetUpdateUI(checking: false);

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

    private async Task StartInstallAsync(ReleaseInfo release)
    {
        _isUpdating = true;
        UpdateChevron.IsVisible       = false;
        UpdateProgressStack.IsVisible = true;
        UpdateActionLabel.Text        = "Downloading...";
        UpdateActionSub.Text          = $"v{release.Version} · {release.SizeMb:F1} MB";

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
        try { await Launcher.Default.OpenAsync(new Uri("https://github.com/seizue/Shuka/issues/new")); }
        catch { await DisplayAlertAsync("Error", "Could not open browser.", "OK"); }
    }

    private async void OnAboutTapped(object sender, TappedEventArgs e)
        => await Navigation.PushAsync(new AboutPage());
}
