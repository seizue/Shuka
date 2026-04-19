using Shuka.Core;
using Shuka.Android.Platform;

namespace Shuka.Android.Pages;

public partial class MainPage : ContentPage
{
    private string? _lastEpubPath;
    private bool    _isBusy;

    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnDownloadClicked(object sender, TappedEventArgs e)
    {
        if (_isBusy) return;

        string url = UrlEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            await DisplayAlert("Missing URL", "Please enter a novel URL.", "OK");
            return;
        }

        int chapters = int.TryParse(ChaptersEntry.Text, out int c) ? c : 0;
        string? coverUrl = string.IsNullOrWhiteSpace(CoverEntry.Text) ? null : CoverEntry.Text.Trim();

        SetBusy(true);
        ClearLog();
        ResultFrame.IsVisible = false;
        _lastEpubPath = null;

        try
        {
            var service = new BookService(new WebViewCloudflareBypass());

            Log("Gathering book info...");
            var book = await service.GatherBookInfo(url, chapters, coverUrl, Log);

            Log($"Title:    {book.Title}");
            Log($"Author:   {book.Author}");
            Log($"Chapters: {book.Total}");

            UpdateStatus($"Downloading {book.Total} chapters...", 0);

            var progress = new Progress<ProgressEventArgs>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    double pct = (double)p.Current / p.Total;
                    ProgressBar.Progress = pct;
                    ProgressPct.Text     = $"{(int)(pct * 100)}%";
                    StatusLabel.Text     = p.Message;
                });
            });

            string dir = GetOutputDirectory();
            // Use a temp path during processing; ProcessBook translates the title first,
            // so we rename to the English title once it's available.
            string tempPath = Path.Combine(dir, "_shuka_temp.epub");
            _lastEpubPath = await service.ProcessBook(book, tempPath, progress, Log);

            // Rename to the English title now that translation is done
            string finalName = SanitizeFileName(book.TitleEn ?? book.Title);
            string finalPath = Path.Combine(dir, finalName + ".epub");
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(_lastEpubPath, finalPath);
            _lastEpubPath = finalPath;

            Log($"Saved: {_lastEpubPath}");
            ResultLabel.Text      = Path.GetFileName(_lastEpubPath);
            ResultFrame.IsVisible = true;
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            await DisplayAlert("Error", ex.Message, "OK");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnShareClicked(object sender, TappedEventArgs e)
    {
        if (_lastEpubPath == null || !File.Exists(_lastEpubPath)) return;
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Share EPUB",
            File  = new ShareFile(_lastEpubPath, "application/epub+zip")
        });
    }

    private async void OnOpenFolderClicked(object sender, TappedEventArgs e)
    {
        if (_lastEpubPath == null) return;
        try
        {
            // On Android, open the file directly — the OS will show it in the file manager
            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                Title = "Open EPUB",
                File  = new ReadOnlyFile(_lastEpubPath, "application/epub+zip")
            });
        }
        catch
        {
            // Fallback: share so user can at least access it
            if (File.Exists(_lastEpubPath))
                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = "Open EPUB",
                    File  = new ShareFile(_lastEpubPath, "application/epub+zip")
                });
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            DownloadBtnLabel.Text      = busy ? "Working..." : "Download & Translate";
            DownloadSpinner.IsRunning  = busy;
            DownloadSpinner.IsVisible  = busy;
            DownloadIcon.IsVisible     = !busy;
            ProgressCard.IsVisible     = busy;
            LogFrame.IsVisible         = true;
            if (!busy) { ProgressBar.Progress = 1; ProgressPct.Text = "100%"; }
        });
    }

    private void UpdateStatus(string message, double progress)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text     = message;
            ProgressBar.Progress = progress;
            ProgressPct.Text     = $"{(int)(progress * 100)}%";
        });
    }

    private void Log(string msg) =>
        MainThread.BeginInvokeOnMainThread(() => LogLabel.Text += msg + "\n");

    private void ClearLog() =>
        MainThread.BeginInvokeOnMainThread(() => LogLabel.Text = "");

    private static string GetOutputDirectory()
    {
#if ANDROID
        // Save to the public Downloads folder so the file is visible in any file manager
        var downloads = global::Android.OS.Environment.GetExternalStoragePublicDirectory(
            global::Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
        string dir = Path.Combine(downloads, "Shuka");
#else
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Shuka");
#endif
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim('_')[..Math.Min(name.Length, 60)];
    }
}
