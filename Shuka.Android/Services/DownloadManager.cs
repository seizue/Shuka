using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Shuka.Core;
using Shuka.Android.Platform;

namespace Shuka.Android.Services;

/// <summary>
/// Singleton service that manages all download jobs.
/// Supports multiple concurrent downloads and per-job cancellation.
/// </summary>
public class DownloadManager
{
    public static readonly DownloadManager Instance = new();

    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    private DownloadManager() { }

    /// <summary>Enqueue a new download and start it immediately.</summary>
    public DownloadItem Enqueue(string url, int chapters, string? coverUrl)
    {
        var item = new DownloadItem
        {
            Url      = url,
            Chapters = chapters,
            CoverUrl = coverUrl ?? ""
        };

        MainThread.BeginInvokeOnMainThread(() => Downloads.Insert(0, item));
        _ = RunAsync(item);
        return item;
    }

    /// <summary>Cancel a single download.</summary>
    public void Cancel(DownloadItem item)
    {
        item.Cts.Cancel();
    }

    /// <summary>Cancel all active downloads.</summary>
    public void CancelAll()
    {
        foreach (var item in Downloads.Where(d => d.IsRunning))
            item.Cts.Cancel();
    }

    /// <summary>Remove all finished (done/cancelled/failed) items from the list.</summary>
    public void ClearHistory()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var finished = Downloads.Where(d => d.IsFinished).ToList();
            foreach (var item in finished)
                Downloads.Remove(item);
        });
    }

    private const string PrefKeyDownloadPath = "download_output_path";

    private async Task RunAsync(DownloadItem item)
    {
        var ct = item.Cts.Token;

        void Log(string msg) =>
            MainThread.BeginInvokeOnMainThread(() =>
                item.LogText += msg + "\n");

        try
        {
            item.Status     = DownloadStatus.Running;
            item.StatusText = "Gathering book info...";

            var service = new BookService(new WebViewCloudflareBypass());

            ct.ThrowIfCancellationRequested();

            var book = await service.GatherBookInfo(
                item.Url, item.Chapters,
                string.IsNullOrWhiteSpace(item.CoverUrl) ? null : item.CoverUrl,
                Log, ct);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.Title  = book.TitleEn ?? book.Title;
                item.Author = book.AuthorEn ?? book.Author;
            });

            Log($"Title:    {book.Title}");
            Log($"Author:   {book.Author}");
            Log($"Chapters: {book.Total}");

            item.StatusText = $"Downloading {book.Total} chapters...";

            var progress = new Progress<ProgressEventArgs>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    item.Progress    = (double)p.Current / p.Total;
                    item.StatusText  = p.Message;
                });
            });

            string dir      = GetOutputDirectory();
            string tempPath = Path.Combine(dir, $"_shuka_{item.Id:N}.epub");

            string epubPath = await service.ProcessBook(book, tempPath, progress, Log, ct);

            ct.ThrowIfCancellationRequested();

            // Build the final filename — prefer English title, fall back to URL slug
            string rawTitle  = book.TitleEn ?? book.Title;
            string finalName = IsEnglishTitle(rawTitle)
                ? SanitizeFileName(rawTitle)
                : SanitizeFileName(book.Title);   // keep original CJK if translation failed

            // If both are unusable, fall back to the URL slug
            if (string.IsNullOrWhiteSpace(finalName))
                finalName = SanitizeFileName(
                    Regex.Match(book.IndexUrl, @"/n/([^/?#]+)").Groups[1].Value);
            if (string.IsNullOrWhiteSpace(finalName))
                finalName = $"novel_{item.Id:N8}";

            string finalPath = Path.Combine(dir, finalName + ".epub");
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(epubPath, finalPath);

            Log($"Saved: {finalPath}");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.Title      = book.TitleEn ?? book.Title;
                item.Author     = book.AuthorEn ?? book.Author;
                item.EpubPath   = finalPath;
                item.Progress   = 1.0;
                item.StatusText = "Done";
                item.Status     = DownloadStatus.Done;
            });
        }
        catch (OperationCanceledException)
        {
            Log("Download cancelled.");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.StatusText = "Cancelled";
                item.Status     = DownloadStatus.Cancelled;
            });
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.StatusText = $"Failed: {ex.Message}";
                item.Status     = DownloadStatus.Failed;
            });
        }
    }

    // ── Output directory (configurable, persisted) ────────────────────────────

    public static string GetOutputDirectory()
    {
        string saved = Preferences.Default.Get(PrefKeyDownloadPath, "");
        if (!string.IsNullOrWhiteSpace(saved))
        {
            try { Directory.CreateDirectory(saved); return saved; }
            catch { /* fall through to default */ }
        }
        return GetDefaultOutputDirectory();
    }

    public static void SetOutputDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Preferences.Default.Set(PrefKeyDownloadPath, path);
    }

    public static void ResetOutputDirectory()
    {
        Preferences.Default.Remove(PrefKeyDownloadPath);
    }

    private static string GetDefaultOutputDirectory()
    {
#if ANDROID
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

    /// <summary>
    /// Returns true if the title is predominantly Latin/English
    /// (i.e. translation actually worked and didn't return Chinese).
    /// </summary>
    private static bool IsEnglishTitle(string title) =>
        !Regex.IsMatch(title, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff\u3000-\u303f]");

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        // Collapse multiple underscores and trim
        name = Regex.Replace(name, @"_+", "_").Trim('_');
        return name.Length > 80 ? name[..80] : name;
    }
}
