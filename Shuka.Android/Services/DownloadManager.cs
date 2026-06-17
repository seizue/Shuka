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

    // Lock for queue processing to ensure serialized checks
    private readonly SemaphoreSlim _queueLock = new(1, 1);

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
            UpdateQueuePositions();
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

        if (e.PropertyName == nameof(DownloadItem.Status))
        {
            UpdateQueuePositions();
        }
    }

    private void UpdateQueuePositions()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(UpdateQueuePositions);
            return;
        }

        int position = 1;
        foreach (var item in Downloads)
        {
            if (item.Status == DownloadStatus.Pending)
            {
                if (item.QueuePosition != position)
                {
                    item.QueuePosition = position;
                }
                position++;
            }
            else
            {
                if (item.QueuePosition != 0)
                {
                    item.QueuePosition = 0;
                }
            }
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

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                foreach (var item in list)
                {
                    item.Cts = new CancellationTokenSource();
                    Downloads.Add(item);
                }
            });

            // Wait for history before reconciling — otherwise we cannot match URL → EPUB path.
            await HistoryService.Instance.LoadedTask;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                foreach (var item in list)
                {
                    if (item.Status is DownloadStatus.Downloading or DownloadStatus.Resuming
                        or DownloadStatus.Pending)
                    {
                        item.Status = DownloadStatus.Pending;
                        item.StatusText = "Queued — waiting for slot...";
                        item.ForceRebuild = false;
                    }
                }

                foreach (var item in list.Where(i => i.Status == DownloadStatus.Pending))
                {
                    if (TryAdoptExistingEpub(item))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DownloadManager] LoadQueueAsync adopted existing EPUB for '{item.Title}'");
                    }
                }

                UpdateQueuePositions();
            });

            if (Downloads.Any(d => d.Status == DownloadStatus.Pending))
                await ProcessQueueAsync();
        }
        catch { }
    }

    /// <summary>
    /// Cancel queued/active downloads for a URL. Used when opening an existing EPUB from history
    /// so a stale queue item cannot regenerate the file in the background.
    /// </summary>
    public void CancelActiveForUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var active = Downloads.Where(d =>
                string.Equals(d.Url, url, StringComparison.OrdinalIgnoreCase) &&
                d.Status is DownloadStatus.Pending or DownloadStatus.Downloading
                    or DownloadStatus.Resuming).ToList();

            if (active.Count == 0) return;

            System.Diagnostics.Debug.WriteLine(
                $"[DownloadManager] CancelActiveForUrl: cancelling {active.Count} item(s) for {url}");

            foreach (var item in active)
            {
                item.StatusText = "Cancelled — EPUB already on device";
                item.Status     = DownloadStatus.Cancelled;
                item.Cts.Cancel();
            }
            _ = ProcessQueueAsync();
        });
    }

    /// <summary>
    /// If an accessible EPUB already exists for this item's URL, mark it completed without downloading.
    /// Returns true when an existing file was adopted.
    /// </summary>
    public bool TryAdoptExistingEpub(DownloadItem item)
    {
        if (item.ForceRebuild) return false;

        var existing = HistoryService.Instance.Entries.FirstOrDefault(e =>
            string.Equals(e.Url, item.Url, StringComparison.OrdinalIgnoreCase));

        string? searchTitle = existing?.Title;
        if (string.IsNullOrWhiteSpace(searchTitle) && !string.IsNullOrWhiteSpace(item.Title))
            searchTitle = item.Title;

#if ANDROID
        string? path = existing != null
            ? Platforms.Android.EpubOpener.ResolveAccessiblePath(
                existing.EpubPath, existing.Title, existing.Url)
            : Platforms.Android.EpubOpener.ResolveAccessiblePath(
                item.EpubPath, searchTitle, item.Url);

        if (path == null && !string.IsNullOrWhiteSpace(searchTitle))
            path = Platforms.Android.EpubOpener.FindExistingEpub(searchTitle, item.EpubPath);

        if (path == null) return false;

        System.Diagnostics.Debug.WriteLine(
            $"[DownloadManager] TryAdoptExistingEpub: adopting '{path}' for URL={item.Url}");

        if (existing != null && existing.EpubPath != path)
        {
            existing.EpubPath = path;
            existing.IsFileAvailable = true;
            _ = HistoryService.Instance.SaveAsync();
        }

        if (!string.IsNullOrWhiteSpace(existing?.Title))
        {
            item.Title          = existing.Title;
            item.Author         = existing.Author;
            item.OriginalTitle  = existing.Title;
            item.OriginalAuthor = existing.Author;
        }

        item.EpubPath   = path;
        item.Progress   = 1.0;
        item.StatusText = "Done";
        item.Status     = DownloadStatus.Completed;
        return true;
#else
        return false;
#endif
    }

    /// <summary>
    /// Enqueue a new download.
    /// Returns the new item, or null if the URL is already actively running/queued.
    /// Use <see cref="FindExisting"/> first to check for duplicates before calling this.
    /// </summary>
    public DownloadItem Enqueue(string url, int chapters, string? coverUrl,
        int chapterFrom = 0, bool? translate = null, bool forceRebuild = false)
    {
        bool shouldTranslate = translate ?? Preferences.Default.Get("translate_to_english_enabled", true);

#if ANDROID
        if (!forceRebuild)
        {
            var hist = HistoryService.Instance.Entries.FirstOrDefault(e =>
                string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase));
            string? existingPath = Platforms.Android.EpubOpener.ResolveAccessiblePath(
                hist?.EpubPath, hist?.Title, url);
            if (existingPath != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DownloadManager] Enqueue skipped — EPUB already exists at '{existingPath}' for {url}");
                var adopted = new DownloadItem
                {
                    Url          = url,
                    Chapters     = chapters,
                    CoverUrl     = coverUrl ?? "",
                    ChapterFrom  = chapterFrom,
                    Translate    = shouldTranslate,
                    ForceRebuild = false,
                    Status       = DownloadStatus.Completed,
                    EpubPath     = existingPath,
                    Progress     = 1.0,
                    StatusText   = "Done",
                    EnqueuedAt   = DateTime.UtcNow,
                    Title        = hist?.Title ?? "",
                    Author       = hist?.Author ?? "",
                };
                MainThread.BeginInvokeOnMainThread(() => Downloads.Insert(0, adopted));
                return adopted;
            }
        }
#endif

        var item = new DownloadItem
        {
            Url          = url,
            Chapters     = chapters,
            CoverUrl     = coverUrl ?? "",
            ChapterFrom  = chapterFrom,
            Translate    = shouldTranslate,
            ForceRebuild = forceRebuild,
            Status       = DownloadStatus.Pending,
            EnqueuedAt   = DateTime.UtcNow
        };

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Downloads.Insert(0, item);
            _ = ProcessQueueAsync();
        });
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
        MainThread.BeginInvokeOnMainThread(() =>
        {
            item.StatusText = "Cancelled";
            item.Status     = DownloadStatus.Cancelled;
            item.Cts.Cancel();
            _ = ProcessQueueAsync();
        });
    }

    /// <summary>Cancel all active downloads.</summary>
    public void CancelAll()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var activeOrPending = Downloads.Where(d => d.IsRunning || d.Status == DownloadStatus.Paused).ToList();
            foreach (var item in activeOrPending)
            {
                item.StatusText = "Cancelled";
                item.Status     = DownloadStatus.Cancelled;
                item.Cts.Cancel();
            }
            _ = ProcessQueueAsync();
        });
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
                item.Cts.Cancel();
                _ = ProcessQueueAsync();
            });
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
                _ = ProcessQueueAsync();
            });
        }
    }

    /// <summary>Pause all running/queued downloads.</summary>
    public void PauseAll()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var activeOrPending = Downloads.Where(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Resuming).ToList();
            foreach (var item in activeOrPending)
            {
                item.StatusText = "Paused";
                item.Status     = DownloadStatus.Paused;
                item.Cts.Cancel();
            }
            _ = ProcessQueueAsync();
        });
    }

    /// <summary>Resume all paused downloads.</summary>
    public void ResumeAll()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var paused = Downloads.Where(d => d.Status == DownloadStatus.Paused).ToList();
            foreach (var item in paused)
            {
                item.Cts = new CancellationTokenSource();
                item.StatusText = "Queued — waiting for slot...";
                item.Status     = DownloadStatus.Pending;
            }
            _ = ProcessQueueAsync();
        });
    }

    public void MoveUp(DownloadItem item)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            int index = Downloads.IndexOf(item);
            if (index > 0)
            {
                Downloads.Move(index, index - 1);
                _ = ProcessQueueAsync();
            }
        });
    }

    public void MoveDown(DownloadItem item)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            int index = Downloads.IndexOf(item);
            if (index >= 0 && index < Downloads.Count - 1)
            {
                Downloads.Move(index, index + 1);
                _ = ProcessQueueAsync();
            }
        });
    }

    public void MoveToTop(DownloadItem item)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            int index = Downloads.IndexOf(item);
            if (index > 0)
            {
                Downloads.Move(index, 0);
                _ = ProcessQueueAsync();
            }
        });
    }

    public void MoveToBottom(DownloadItem item)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            int index = Downloads.IndexOf(item);
            if (index >= 0 && index < Downloads.Count - 1)
            {
                Downloads.Move(index, Downloads.Count - 1);
                _ = ProcessQueueAsync();
            }
        });
    }

    public void Sort(string criterion)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            List<DownloadItem> sorted;
            switch (criterion)
            {
                case "Title (A-Z)":
                    sorted = Downloads.OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase).ToList();
                    break;
                case "Title (Z-A)":
                    sorted = Downloads.OrderByDescending(d => d.Title, StringComparer.OrdinalIgnoreCase).ToList();
                    break;
                case "Progress (Highest)":
                    sorted = Downloads.OrderByDescending(d => d.Progress).ToList();
                    break;
                case "Progress (Lowest)":
                    sorted = Downloads.OrderBy(d => d.Progress).ToList();
                    break;
                case "Date Added (Oldest)":
                    sorted = Downloads.OrderBy(d => d.EnqueuedAt).ToList();
                    break;
                case "Date Added (Newest)":
                default:
                    sorted = Downloads.OrderByDescending(d => d.EnqueuedAt).ToList();
                    break;
            }

            for (int i = 0; i < sorted.Count; i++)
            {
                int oldIndex = Downloads.IndexOf(sorted[i]);
                if (oldIndex != i)
                {
                    Downloads.Move(oldIndex, i);
                }
            }

            _ = ProcessQueueAsync();
        });
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

    public async Task ProcessQueueAsync()
    {
        await _queueLock.WaitAsync();
        try
        {
            int maxConcurrent = 2;
            int activeCount = Downloads.Count(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Resuming);

            while (activeCount < maxConcurrent)
            {
                var next = Downloads.FirstOrDefault(d => d.Status == DownloadStatus.Pending);
                if (next == null) break;

                // Set status synchronously to reserve slot
                next.Status = DownloadStatus.Resuming;
                next.StatusText = "Starting...";

                // Start execution asynchronously
                _ = Task.Run(() => RunAsync(next));
                activeCount++;
            }
        }
        catch { }
        finally
        {
            _queueLock.Release();
        }
    }

    private async Task RunAsync(DownloadItem item)
    {
        var ct = item.Cts.Token;

        void Log(string msg) =>
            MainThread.BeginInvokeOnMainThread(() =>
                item.LogText += msg + "\n");

#if ANDROID
        DownloadForegroundService.Start();
#endif

        string tempPath = "";
        try
        {
#if ANDROID
            if (!item.ForceRebuild)
            {
                bool adopted = false;
                await MainThread.InvokeOnMainThreadAsync(() => adopted = TryAdoptExistingEpub(item));
                if (adopted)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DownloadManager.RunAsync] Early-adopted existing EPUB for URL={item.Url}");
                    return;
                }
            }
#endif

            item.Status     = DownloadStatus.Downloading;
            item.StatusText = "Gathering book info...";

            if (!item.ForceRebuild)
            {
                // ── Wait for history to finish loading (avoids the race on startup) ──
                // We give it 3 s max; if it takes longer we proceed with whatever is loaded.
                await HistoryService.Instance.LoadedTask.WaitAsync(TimeSpan.FromSeconds(3))
                    .ContinueWith(_ => { }, TaskContinuationOptions.None); // swallow timeout

                // ── Check history by URL first ──────────────────────────────────
                var existing = HistoryService.Instance.Entries.FirstOrDefault(e =>
                    string.Equals(e.Url, item.Url, StringComparison.OrdinalIgnoreCase));
                System.Diagnostics.Debug.WriteLine(
                    $"[DownloadManager.RunAsync] URL={item.Url} | history match: {(existing == null ? "null" : $"'{existing.Title}' EpubPath='{existing.EpubPath}'")}");

                // ── Determine which title to use for file-system scan ───────────────
                string? searchTitle = existing?.Title;
                if (string.IsNullOrWhiteSpace(searchTitle) && !string.IsNullOrWhiteSpace(item.Title))
                    searchTitle = item.Title;
                System.Diagnostics.Debug.WriteLine(
                    $"[DownloadManager.RunAsync] searchTitle='{searchTitle}'");

                // ── Try to locate a valid EPUB (stored path, filename, then full scan) ─
                string? path = null;
                if (existing != null)
                {
                    path = Platforms.Android.EpubOpener.ResolveAccessiblePath(
                        existing.EpubPath, existing.Title, existing.Url);
                    System.Diagnostics.Debug.WriteLine(
                        $"[DownloadManager.RunAsync] ResolveAccessiblePath(history): '{path ?? "null"}'");
                }

                if (path == null)
                {
                    path = Platforms.Android.EpubOpener.ResolveAccessiblePath(
                        item.EpubPath, searchTitle, item.Url);
                    System.Diagnostics.Debug.WriteLine(
                        $"[DownloadManager.RunAsync] ResolveAccessiblePath(item): '{path ?? "null"}'");
                }

                if (path == null && !string.IsNullOrWhiteSpace(searchTitle))
                {
                    path = Platforms.Android.EpubOpener.FindExistingEpub(searchTitle, item.EpubPath);
                    System.Diagnostics.Debug.WriteLine(
                        $"[DownloadManager.RunAsync] FindExistingEpub('{searchTitle}'): '{path ?? "null"}'");
                }

                if (path != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DownloadManager] Reusing existing EPUB at: {path} for '{searchTitle ?? item.Url}'");
                    Log($"EPUB already exists — reusing: {path}");

                    // Keep history entry in sync
                    if (existing != null && existing.EpubPath != path)
                    {
                        existing.EpubPath = path;
                        existing.IsFileAvailable = true;
                    }
                    _ = HistoryService.Instance.SaveAsync();

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(existing?.Title))
                        {
                            item.Title          = existing.Title;
                            item.Author         = existing.Author;
                            item.OriginalTitle  = existing.Title;
                            item.OriginalAuthor = existing.Author;
                        }
                        item.EpubPath   = path;
                        item.Progress   = 1.0;
                        item.StatusText = "Done";
                        item.Status     = DownloadStatus.Completed;
                    });

#if ANDROID
                    DownloadForegroundService.NotifyDone(item.Title, path);
#endif
                    return;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[DownloadManager.RunAsync] No existing EPUB found — proceeding with full download for '{searchTitle ?? item.Url}'");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DownloadManager.RunAsync] ForceRebuild=true — skipping existing-EPUB check for URL={item.Url}");
            }

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

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                item.Title      = book.TitleEn ?? book.Title;
                item.Author     = book.AuthorEn ?? book.Author;
                item.EpubPath   = finalPath;
                item.Progress   = 1.0;
                item.StatusText = "Done";
                item.Status     = DownloadStatus.Completed;
            });

            // Save to persistent history (cover cached locally)
            await HistoryService.Instance.AddAsync(item);

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
                else if (item.Status == DownloadStatus.Cancelled)
                {
                    Log("Download cancelled.");
                    item.StatusText = "Cancelled";
                }
                else if (item.Status == DownloadStatus.Pending)
                {
                    Log("Download resumed.");
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

            // Release is replaced by calling ProcessQueueAsync:
            _ = ProcessQueueAsync();

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

                string fileName = baseName + ".epub";

                // Reuse an existing on-disk copy before creating a new SAF document.
                foreach (string searchDir in Platforms.Android.EpubOpener.EnumerateDownloadDirectories())
                {
                    string fsPath = Path.Combine(searchDir, fileName);
                    if (File.Exists(fsPath))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DownloadManager] EPUB already on disk — reusing: {fsPath}");
                        try { if (File.Exists(sourcePath)) File.Delete(sourcePath); } catch { }
                        return fsPath;
                    }
                }

                // Try to find existing file first — pass the original tree URI
                var docUri = FindFileInSafTree(cr, treeUri, fileName);

                if (docUri == null)
                {
                    // Build parent document URI to create the file under
                    var treeDocId  = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
                    var parentUri  = global::Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(treeUri, treeDocId!);

                    // Create the document via SAF
                    docUri = global::Android.Provider.DocumentsContract.CreateDocument(
                        cr, parentUri!, "application/epub+zip", baseName);
                }

                if (docUri == null)
                    throw new Exception("Could not create document in selected folder.");

                // Stream the file into the SAF URI
                await using var src  = File.OpenRead(sourcePath);
                await using var dest = cr.OpenOutputStream(docUri, "wt")
                    ?? throw new Exception("Could not open output stream for SAF URI.");

                await src.CopyToAsync(dest, ct);

                return docUri.ToString()!;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[SAF] copy failed: {ex.Message}");
            }
        }
#endif
        // Plain file copy — guard against creating a duplicate if the EPUB already
        // exists at the destination (e.g. the earlier FindExistingEpub check missed
        // it because of a transient SAF permission issue).
        string dir       = GetOutputDirectory();
        string finalPath = Path.Combine(dir, baseName + ".epub");
        if (File.Exists(finalPath))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DownloadManager] EPUB already exists at destination — discarding temp and reusing: {finalPath}");
            try { if (File.Exists(sourcePath)) File.Delete(sourcePath); } catch { }
            return finalPath;
        }

        // Also check default Shuka folder when output dir differs (e.g. custom path changed)
        string defaultDir = GetDefaultOutputDirectory();
        if (!string.Equals(dir, defaultDir, StringComparison.OrdinalIgnoreCase))
        {
            string defaultPath = Path.Combine(defaultDir, baseName + ".epub");
            if (File.Exists(defaultPath))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DownloadManager] EPUB found in default folder — reusing: {defaultPath}");
                try { if (File.Exists(sourcePath)) File.Delete(sourcePath); } catch { }
                return defaultPath;
            }
        }

        File.Move(sourcePath, finalPath, overwrite: true);
        return finalPath;
    }

    private static global::Android.Net.Uri? FindFileInSafTree(global::Android.Content.ContentResolver cr, global::Android.Net.Uri treeUri, string fileName)
    {
        try
        {
            // Must use GetTreeDocumentId (not GetDocumentId) for a tree URI
            var treeDocId   = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
            var childrenUri = global::Android.Provider.DocumentsContract.BuildChildDocumentsUriUsingTree(
                treeUri, treeDocId!);

            string[] projection =
            {
                global::Android.Provider.DocumentsContract.Document.ColumnDocumentId,
                global::Android.Provider.DocumentsContract.Document.ColumnDisplayName
            };

            using var cursor = cr.Query(childrenUri!, projection, null, null, null);
            if (cursor == null) return null;

            int idIdx   = cursor.GetColumnIndex(global::Android.Provider.DocumentsContract.Document.ColumnDocumentId);
            int nameIdx = cursor.GetColumnIndex(global::Android.Provider.DocumentsContract.Document.ColumnDisplayName);

            while (cursor.MoveToNext())
            {
                string? name = cursor.GetString(nameIdx);
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    string? docId = cursor.GetString(idIdx);
                    if (docId == null) continue;
                    return global::Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DownloadManager] FindFileInSafTree error: {ex.Message}");
        }
        return null;
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
