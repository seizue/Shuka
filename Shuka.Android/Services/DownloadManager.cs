using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Text.Json;
using Shuka.Core;
using Shuka.Android.Platform;
#if ANDROID
using Shuka.Android.Platforms.Android;
#endif

namespace Shuka.Android.Services;

/// <summary>
/// Singleton service that manages all download jobs.
/// Limits concurrent downloads to 2 — beyond that, jobs queue and start
/// automatically when a slot frees up. This keeps Google Translate load
/// manageable and prevents rate-limiting with large queues.
/// </summary>
public class DownloadManager
{
    public static readonly DownloadManager Instance = new();

    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    // Max 2 novels downloading/translating at the same time
    private static readonly SemaphoreSlim _downloadSem = new(2, 2);

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _savePending;

    private DownloadManager()
    {
        Downloads.CollectionChanged += (s, e) =>
        {
            if (e.OldItems != null)
            {
                foreach (DownloadItem item in e.OldItems)
                    item.PropertyChanged -= OnItemPropertyChanged;
            }
            if (e.NewItems != null)
            {
                foreach (DownloadItem item in e.NewItems)
                    RegisterItemChange(item);
            }
            _ = SaveQueueAsync();
        };

        _ = LoadQueueAsync();
    }

    public async Task SaveQueueAsync()
    {
        if (_savePending) return;
        _savePending = true;
        // Debounce to avoid rapid-fire writes on progress updates
        await Task.Delay(500);
        _savePending = false;

        await _saveLock.WaitAsync();
        try
        {
            var list = Downloads.ToList();
            string json = JsonSerializer.Serialize(list);
            string path = Path.Combine(FileSystem.AppDataDirectory, "downloads.json");
            await File.WriteAllTextAsync(path, json);
        }
        catch { }
        finally { _saveLock.Release(); }
    }

    private void RegisterItemChange(DownloadItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
        item.PropertyChanged += OnItemPropertyChanged;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItem.Status) or 
                              nameof(DownloadItem.Progress) or 
                              nameof(DownloadItem.Title) or 
                              nameof(DownloadItem.Author) or 
                              nameof(DownloadItem.EpubPath) or 
                              nameof(DownloadItem.LogText))
        {
            _ = SaveQueueAsync();
        }
    }

    private async Task LoadQueueAsync()
    {
        try
        {
            string path = Path.Combine(FileSystem.AppDataDirectory, "downloads.json");
            if (!File.Exists(path)) return;

            string json = await File.ReadAllTextAsync(path);
            var list = JsonSerializer.Deserialize<List<DownloadItem>>(json);
            if (list == null) return;

            // Load items on the main thread to ensure CollectionChanged triggers and UI updates properly
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var item in list)
                {
                    item.Cts = new CancellationTokenSource();
                    Downloads.Add(item);
                }

                // Auto-recover active/interrupted downloads
                foreach (var item in list)
                {
                    if (item.Status is DownloadStatus.Downloading or DownloadStatus.Resuming or DownloadStatus.Pending)
                    {
                        item.Status = DownloadStatus.Pending;
                        item.StatusText = "Queued — waiting for slot...";
                        _ = RunAsync(item);
                    }
                }
            });
        }
        catch { }
    }

    /// <summary>
    /// Enqueue a new download.
    /// Returns the new item, or null if the URL is already actively running/queued.
    /// Use <see cref="FindExisting"/> first to check for duplicates before calling this.
    /// </summary>
    public DownloadItem Enqueue(string url, int chapters, string? coverUrl,
        int chapterFrom = 0, bool? translate = null)
    {
        bool shouldTranslate = translate ?? Preferences.Default.Get("translate_to_english_enabled", true);
        var item = new DownloadItem
        {
            Url          = url,
            Chapters     = chapters,
            CoverUrl     = coverUrl ?? "",
            ChapterFrom  = chapterFrom,
            Translate    = shouldTranslate,
            Status       = DownloadStatus.Pending
        };

        MainThread.BeginInvokeOnMainThread(() => Downloads.Insert(0, item));
        _ = RunAsync(item);
        return item;
    }

    /// <summary>
    /// Returns any existing download item for the given URL, or null if none.
    /// </summary>
    public DownloadItem? FindExisting(string url) =>
        Downloads.FirstOrDefault(d =>
            string.Equals(d.Url, url, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Pause a running/queued download.</summary>
    public void Pause(DownloadItem item)
    {
        if (item.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Resuming)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.StatusText = "Paused";
                item.Status     = DownloadStatus.Paused;
            });
            item.Cts.Cancel();
        }
    }

    /// <summary>Resume a paused, failed, or cancelled download.</summary>
    public void Resume(DownloadItem item)
    {
        if (item.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Cancelled)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.Cts = new CancellationTokenSource();
                item.StatusText = "Queued — waiting for slot...";
                item.Status     = DownloadStatus.Pending;
                _ = RunAsync(item);
            });
        }
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

    /// <summary>Retry a failed or cancelled download — resumes from checkpoint if available.</summary>
    public DownloadItem? Retry(DownloadItem failed)
    {
        if (!failed.IsFailed && !failed.IsCancelled) return null;
        Resume(failed);
        return failed;
    }

    /// <summary>Dismiss a failed or cancelled item from the list without retrying.</summary>
    public void Dismiss(DownloadItem item)
    {
        if (!item.IsFinished) return;
        MainThread.BeginInvokeOnMainThread(() => Downloads.Remove(item));
    }

    private const string PrefKeyDownloadPath    = "download_output_path";
    private const string PrefKeyDownloadTreeUri = "download_tree_uri";

    private async Task RunAsync(DownloadItem item)
    {
        var ct = item.Cts.Token;

        void Log(string msg) =>
            MainThread.BeginInvokeOnMainThread(() =>
                item.LogText += msg + "\n");

        // Wait for a download slot — show queued status while waiting
        if (_downloadSem.CurrentCount == 0)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                item.StatusText = "Queued — waiting for slot...");
        }

        try { await _downloadSem.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (item.Status == DownloadStatus.Paused)
                {
                    item.StatusText = "Paused";
                }
                else
                {
                    item.StatusText = "Cancelled";
                    item.Status     = DownloadStatus.Cancelled;
                }
            });
            return;
        }

#if ANDROID
        DownloadForegroundService.Start();
#endif

        string tempPath = "";
        try
        {
            item.Status     = DownloadStatus.Downloading;
            item.StatusText = "Gathering book info...";

            var service = new BookService(new WebViewCloudflareBypass());

            ct.ThrowIfCancellationRequested();

            var book = await service.GatherBookInfo(
                item.Url, item.Chapters,
                string.IsNullOrWhiteSpace(item.CoverUrl) ? null : item.CoverUrl,
                Log, ct, item.ChapterFrom);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.Title          = book.TitleEn ?? book.Title;
                item.Author         = book.AuthorEn ?? book.Author;
                item.OriginalTitle  = book.Title;
                item.OriginalAuthor = book.Author;
                item.TotalChapters  = book.Total;
            });

            Log($"Title:    {book.Title}");
            Log($"Author:   {book.Author}");
            Log($"Chapters: {book.Total}");
            Log($"Translate: {(item.Translate ? "Yes" : "No")}");

            item.StatusText = item.Translate ? $"Downloading & translating {book.Total} chapters..." : $"Downloading {book.Total} chapters...";

            var progress = new Progress<ProgressEventArgs>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    item.Progress    = (double)p.Current / p.Total;
                    item.StatusText  = p.Message;
                });
            });

            // Always write the EPUB to app-private cache first — avoids
            // UnauthorizedAccessException on scoped storage (Android 10+).
            // We copy/move to the user's chosen folder afterwards via SAF.
            string cacheDir        = GetCacheDirectory();
            tempPath               = Path.Combine(cacheDir, $"_shuka_{item.Id:N}.epub");
            string checkpointPath  = CheckpointService.GetCheckpointPath(cacheDir, item.Url);

            int savedCount = CheckpointService.CountSaved(checkpointPath);
            if (savedCount > 0)
                Log($"Resuming: {savedCount} chapters already done, continuing from ch{savedCount + 1}...");

            string epubPath = "";
            try
            {
                epubPath = await service.ProcessBook(book, tempPath, progress, Log, ct,
                    checkpointPath, item.Translate);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new Exception("A request timed out during processing. Please retry.");
            }

            ct.ThrowIfCancellationRequested();

            string rawTitle  = book.TitleEn ?? book.Title;
            string finalName = SanitizeFileName(rawTitle);

            if (string.IsNullOrWhiteSpace(finalName))
                finalName = SanitizeFileName(book.Title);
            if (string.IsNullOrWhiteSpace(finalName))
                finalName = SanitizeFileName(
                    Regex.Match(book.IndexUrl, @"/n/([^/?#]+)").Groups[1].Value);
            if (string.IsNullOrWhiteSpace(finalName))
                finalName = $"novel_{item.Id:N8}";

            // Copy from cache to the user's chosen output folder via SAF
            string finalPath = await CopyToOutputAsync(epubPath, finalName, ct);

            Log($"Saved: {finalPath}");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.Title      = book.TitleEn ?? book.Title;
                item.Author     = book.AuthorEn ?? book.Author;
                item.EpubPath   = finalPath;
                item.Progress   = 1.0;
                item.StatusText = "Done";
                item.Status     = DownloadStatus.Completed;
            });

            // Save to persistent history (cover cached locally)
            _ = HistoryService.Instance.AddAsync(item);

#if ANDROID
            DownloadForegroundService.NotifyDone(book.TitleEn ?? book.Title, finalPath);
#endif
        }
        catch (OperationCanceledException)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (item.Status == DownloadStatus.Paused)
                {
                    Log("Download paused.");
                    item.StatusText = "Paused";
                }
                else
                {
                    Log("Download cancelled.");
                    item.StatusText = "Cancelled";
                    item.Status     = DownloadStatus.Cancelled;
                }
            });
        }
        catch (Shuka.Core.CloudflareExpiredException ex)
        {
            Log($"Cloudflare cookie expired for {ex.SiteUrl}.");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.StatusText = "Failed: Cloudflare cookie expired";
                item.Status     = DownloadStatus.Failed;
            });
            NotifyCloudflareCookieExpired(ex.SiteUrl);
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
            // Clean up temp file in cache
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

            // Release the download slot so the next queued novel can start
            _downloadSem.Release();

#if ANDROID
            if (!Downloads.Any(d => d.IsRunning))
                DownloadForegroundService.Stop();
#endif
        }
    }

    /// <summary>
    /// Copies the EPUB from the app cache to the user's chosen output folder.
    /// On Android uses SAF DocumentsContract when a tree URI is set so scoped
    /// storage restrictions are bypassed. Falls back to a plain File.Move on
    /// other platforms or when no tree URI is configured.
    /// Returns the final file path (or content URI string on Android SAF).
    /// </summary>
    private static async Task<string> CopyToOutputAsync(
        string sourcePath, string baseName, CancellationToken ct)
    {
#if ANDROID
        string treeUriStr = Preferences.Default.Get(PrefKeyDownloadTreeUri, "");
        if (!string.IsNullOrWhiteSpace(treeUriStr))
        {
            try
            {
                var treeUri = global::Android.Net.Uri.Parse(treeUriStr)!;
                var ctx     = global::Android.App.Application.Context;
                var cr      = ctx.ContentResolver!;

                // Resolve a unique file name inside the SAF tree
                string fileName = baseName + ".epub";

                // Check for existing documents with the same name
                var treeDocId   = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
                var existingUri = global::Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(
                    treeUri, treeDocId!);

                // Create the document via SAF — this works regardless of scoped storage
                var docUri = global::Android.Provider.DocumentsContract.CreateDocument(
                    cr, existingUri!, "application/epub+zip", baseName);

                if (docUri == null)
                    throw new Exception("Could not create document in selected folder.");

                // Stream the file into the SAF URI
                await using var src  = File.OpenRead(sourcePath);
                await using var dest = cr.OpenOutputStream(docUri)
                    ?? throw new Exception("Could not open output stream for SAF URI.");

                await src.CopyToAsync(dest, ct);

                // Return the content URI as string so the share/open flow works
                return docUri.ToString()!;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // SAF failed — fall through to plain file copy into default dir
                System.Diagnostics.Debug.WriteLine($"[SAF] copy failed: {ex.Message}");
            }
        }
#endif
        // Plain file copy — works for default Downloads/Shuka dir and non-Android
        string dir       = GetDefaultOutputDirectory();
        string finalPath = ResolveUniqueFilePath(dir, baseName);
        File.Move(sourcePath, finalPath, overwrite: true);
        return finalPath;
    }

    /// <summary>App-private cache directory — always writable, no permissions needed.</summary>
    private static string GetCacheDirectory()
    {
#if ANDROID
        string dir = global::Android.App.Application.Context.CacheDir!.AbsolutePath;
#else
        string dir = Path.GetTempPath();
#endif
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Posts a notification telling the user their Cloudflare cookie has expired.
    /// Tapping the notification opens the site in the browser so they can solve
    /// the challenge — after which the download can be retried.
    /// </summary>
    private static void NotifyCloudflareCookieExpired(string siteHost)
    {
#if ANDROID
        const string ChannelId = "shuka_cf_channel";
        var ctx = global::Android.App.Application.Context;

        // Ensure notification channel exists
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
        {
#pragma warning disable CA1416
            var nm = (global::Android.App.NotificationManager?)
                ctx.GetSystemService(global::Android.Content.Context.NotificationService);
            if (nm?.GetNotificationChannel(ChannelId) == null)
            {
                var ch = new global::Android.App.NotificationChannel(
                    ChannelId, "Cloudflare Alerts",
                    global::Android.App.NotificationImportance.High)
                {
                    Description = "Alerts when Cloudflare cookie needs renewal"
                };
                nm?.CreateNotificationChannel(ch);
            }
#pragma warning restore CA1416
        }

        // Intent: open the site in the browser so the user can solve the challenge
        var browserIntent = new global::Android.Content.Intent(
            global::Android.Content.Intent.ActionView,
            global::Android.Net.Uri.Parse($"https://{siteHost}"));
        browserIntent.AddFlags(global::Android.Content.ActivityFlags.NewTask);

#pragma warning disable CA1416
        var pendingFlags = global::Android.OS.Build.VERSION.SdkInt >=
                           global::Android.OS.BuildVersionCodes.M
            ? global::Android.App.PendingIntentFlags.UpdateCurrent |
              global::Android.App.PendingIntentFlags.Immutable
            : global::Android.App.PendingIntentFlags.UpdateCurrent;
#pragma warning restore CA1416

        var pi = global::Android.App.PendingIntent.GetActivity(
            ctx, siteHost.GetHashCode(), browserIntent, pendingFlags);

        if (pi == null) return;

#pragma warning disable CS8602
        var notification = new AndroidX.Core.App.NotificationCompat.Builder(ctx, ChannelId)
            .SetContentTitle("Cloudflare verification needed")
            .SetContentText($"Tap to open {siteHost} in your browser and complete the check, then retry the download.")
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysWarning)
            .SetAutoCancel(true)
            .SetContentIntent(pi)
            .SetPriority(AndroidX.Core.App.NotificationCompat.PriorityHigh)
            .Build()!;
#pragma warning restore CS8602

        var mgr = AndroidX.Core.App.NotificationManagerCompat.From(ctx);
        mgr?.Notify(Math.Abs(siteHost.GetHashCode() % 9000) + 3000, notification);
#endif
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
