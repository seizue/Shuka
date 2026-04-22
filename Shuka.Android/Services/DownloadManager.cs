using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Shuka.Core;
using Shuka.Android.Platform;
#if ANDROID
using Shuka.Android.Platforms.Android;
#endif

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

    /// <summary>
    /// Enqueue a new download. Returns null if the URL is already active or queued.
    /// </summary>
    public DownloadItem? Enqueue(string url, int chapters, string? coverUrl)
    {
        bool alreadyActive = Downloads.Any(d =>
            string.Equals(d.Url, url, StringComparison.OrdinalIgnoreCase) && d.IsRunning);

        if (alreadyActive)
            return null;

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

    /// <summary>Retry a failed or cancelled download by re-enqueuing it.</summary>
    public DownloadItem? Retry(DownloadItem failed)
    {
        if (!failed.IsFailed && !failed.IsCancelled) return null;

        MainThread.BeginInvokeOnMainThread(() => Downloads.Remove(failed));

        return Enqueue(failed.Url, failed.Chapters, failed.CoverUrl);
    }

    /// <summary>Dismiss a failed or cancelled item from the list without retrying.</summary>
    public void Dismiss(DownloadItem item)
    {
        if (!item.IsFinished) return;
        MainThread.BeginInvokeOnMainThread(() => Downloads.Remove(item));
    }

    private const string PrefKeyDownloadPath    = "download_output_path";
    private const string PrefKeyDownloadTreeUri = "download_tree_uri";
    private const int MaxRetries = 5;

    private async Task RunAsync(DownloadItem item)
    {
        var ct = item.Cts.Token;

        void Log(string msg) =>
            MainThread.BeginInvokeOnMainThread(() =>
                item.LogText += msg + "\n");

#if ANDROID
        DownloadForegroundService.Start();
#endif

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

            string epubPath = "";
            int attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    epubPath = await service.ProcessBook(book, tempPath, progress, Log, ct);
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < MaxRetries)
                {
                    attempt++;
                    int delaySec = attempt * 5;
                    Log($"Error (attempt {attempt}/{MaxRetries}): {ex.Message}. Retrying in {delaySec}s...");
                    MainThread.BeginInvokeOnMainThread(() =>
                        item.StatusText = $"Retrying ({attempt}/{MaxRetries})...");
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
                }
            }

            ct.ThrowIfCancellationRequested();

            // Build the final filename — prefer the translated title; only fall back
            // to the Chinese title if TitleEn was never populated at all.
            string rawTitle  = book.TitleEn ?? book.Title;
            string finalName = SanitizeFileName(rawTitle);

            if (string.IsNullOrWhiteSpace(finalName))
                finalName = SanitizeFileName(book.Title);
            if (string.IsNullOrWhiteSpace(finalName))
                finalName = SanitizeFileName(
                    Regex.Match(book.IndexUrl, @"/n/([^/?#]+)").Groups[1].Value);
            if (string.IsNullOrWhiteSpace(finalName))
                finalName = $"novel_{item.Id:N8}";

            string finalPath = ResolveUniqueFilePath(dir, finalName);
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
        finally
        {
            // Clean up any leftover temp file
            string tempGlob = Path.Combine(GetOutputDirectory(), $"_shuka_{item.Id:N}.epub");
            try { if (File.Exists(tempGlob)) File.Delete(tempGlob); } catch { }

#if ANDROID
            if (!Downloads.Any(d => d.IsRunning))
                DownloadForegroundService.Stop();
#endif
        }
    }

    public static string GetOutputDirectory()
    {
        // Prefer the tree-URI path (set via folder picker)
#if ANDROID
        string treeUriStr = Preferences.Default.Get(PrefKeyDownloadTreeUri, "");
        if (!string.IsNullOrWhiteSpace(treeUriStr))
        {
            try
            {
                var uri  = global::Android.Net.Uri.Parse(treeUriStr)!;
                var docId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(uri);
                // Convert content URI → real file path for the public Downloads tree
                if (docId != null && docId.StartsWith("primary:"))
                {
                    string rel  = docId["primary:".Length..];
#pragma warning disable CA1422
                    string root = global::Android.OS.Environment
                        .ExternalStorageDirectory!.AbsolutePath;
#pragma warning restore CA1422
                    string path = Path.Combine(root, rel);
                    Directory.CreateDirectory(path);
                    return path;
                }
            }
            catch { /* fall through */ }
        }
#endif
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

#if ANDROID
    /// <summary>Persist a folder chosen via ACTION_OPEN_DOCUMENT_TREE.</summary>
    public static void SetOutputDirectoryFromUri(global::Android.Net.Uri treeUri)    {
        Preferences.Default.Set(PrefKeyDownloadTreeUri, treeUri.ToString());
        Preferences.Default.Remove(PrefKeyDownloadPath); // clear any old manual path
    }
#endif

    public static void ResetOutputDirectory()
    {
        Preferences.Default.Remove(PrefKeyDownloadPath);
        Preferences.Default.Remove(PrefKeyDownloadTreeUri);
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

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = Regex.Replace(name, @"_+", "_").Trim('_');
        return name.Length > 80 ? name[..80] : name;
    }

    /// <summary>
    /// Returns a path that doesn't collide with any existing file.
    /// e.g. Title.epub → Title (2).epub → Title (3).epub …
    /// </summary>
    private static string ResolveUniqueFilePath(string dir, string baseName)
    {
        string candidate = Path.Combine(dir, baseName + ".epub");
        if (!File.Exists(candidate))
            return candidate;

        int n = 2;
        do
        {
            candidate = Path.Combine(dir, $"{baseName} ({n}).epub");
            n++;
        }
        while (File.Exists(candidate));

        return candidate;
    }
}
