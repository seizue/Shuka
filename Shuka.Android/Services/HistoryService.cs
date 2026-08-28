using System.Collections.ObjectModel;
using System.Text.Json;
using Shuka.Core.Adapters;

namespace Shuka.Android.Services;

/// <summary>
/// Persists completed downloads as history entries.
/// Covers are downloaded and cached locally so they display offline.
/// </summary>
public class HistoryService
{
    public static readonly HistoryService Instance = new();

    public ObservableCollectionEx<HistoryEntry> Entries { get; } = new();

    private static readonly Dictionary<string, ImageSource> _coverImageCache = new();
    private static readonly object _coverImageLock = new();

    /// <summary>
    /// Returns a cached ImageSource for the local cover path, or the default cover.
    /// </summary>
    public static ImageSource GetCoverImageSource(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            return ImageSource.FromFile("lily.png");

        lock (_coverImageLock)
        {
            if (_coverImageCache.TryGetValue(localPath, out var source))
                return source;

            source = ImageSource.FromFile(localPath);
            _coverImageCache[localPath] = source;
            return source;
        }
    }

    private static bool IsEpubAccessible(string? path)
    {
        return Shuka.Android.Platforms.Android.EpubOpener.IsAccessible(path);
    }

    private static string HistoryFile =>
        Path.Combine(FileSystem.AppDataDirectory, "history.json");

    private static string CoversDir =>
        Path.Combine(FileSystem.AppDataDirectory, "covers");

    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
            { "Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8" }
        }
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly TaskCompletionSource _loadedTcs = new();

    /// <summary>Awaitable task that completes once history has been loaded from disk.</summary>
    public Task LoadedTask => _loadedTcs.Task;

    private HistoryService()
    {
        _ = LoadAsync();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when a download completes. Saves the entry and caches the cover.
    /// If an entry for the same URL already exists it is updated in-place
    /// rather than inserting a duplicate.
    /// </summary>
    public async Task AddAsync(DownloadItem item)
    {
        if (item.Status != DownloadStatus.Completed) return;

        // Check for an existing entry by URL only — EpubPath can change between runs
        var existing = Entries.FirstOrDefault(e =>
            string.Equals(e.Url, item.Url, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            // Update in-place: refresh the path and timestamp, but keep cover/id
            System.Diagnostics.Debug.WriteLine(
                $"[HistoryService] Updating existing entry for '{existing.Title}' (path: {item.EpubPath})");

            bool pathChanged = existing.EpubPath != item.EpubPath;
            existing.EpubPath        = item.EpubPath;
            existing.IsFileAvailable = IsEpubAccessible(existing.EpubPath);
            // Note: CompletedAt is init-only — we intentionally keep the original date.

            await SaveAsync();
            return;
        }

        var entry = new HistoryEntry
        {
            Id           = item.Id,
            Title        = item.Title,
            Author       = item.Author,
            Url          = item.Url,
            EpubPath     = item.EpubPath,
            CoverUrl     = string.IsNullOrWhiteSpace(item.CoverUrl) ? null : item.CoverUrl,
            ChapterCount = item.TotalChapters > 0 ? item.TotalChapters : item.Chapters,
            CompletedAt  = DateTime.Now,
        };

        // Cache cover image locally
        if (!string.IsNullOrWhiteSpace(entry.CoverUrl))
            entry.CoverLocalPath = await CacheCoverAsync(entry.Id, entry.CoverUrl);

        // Fallback: extract cover image directly from the generated EPUB if network cache was skipped or failed
        if (string.IsNullOrWhiteSpace(entry.CoverLocalPath) || !File.Exists(entry.CoverLocalPath))
            entry.CoverLocalPath = TryExtractCoverFromEpub(entry.EpubPath, entry.Id);

        entry.IsFileAvailable  = IsEpubAccessible(entry.EpubPath);
        entry.IsCoverAvailable = !string.IsNullOrWhiteSpace(entry.CoverLocalPath) && File.Exists(entry.CoverLocalPath);

        MainThread.BeginInvokeOnMainThread(() => Entries.Insert(0, entry));
        await SaveAsync();
    }

    /// <summary>Remove a single entry and delete its cached cover.</summary>
    public async Task RemoveAsync(HistoryEntry entry)
    {
        MainThread.BeginInvokeOnMainThread(() => Entries.Remove(entry));

        // Delete cached cover
        if (!string.IsNullOrWhiteSpace(entry.CoverLocalPath))
        {
            lock (_coverImageLock)
            {
                _coverImageCache.Remove(entry.CoverLocalPath);
            }

            if (File.Exists(entry.CoverLocalPath))
            {
                try { File.Delete(entry.CoverLocalPath); } catch { }
            }
        }

        await SaveAsync();
    }

    /// <summary>Clear all history entries and cached covers.</summary>
    public async Task ClearAllAsync()
    {
        var entries = Entries.ToList();
        MainThread.BeginInvokeOnMainThread(() => Entries.Clear());

        lock (_coverImageLock)
        {
            _coverImageCache.Clear();
        }

        foreach (var e in entries)
        {
            if (!string.IsNullOrWhiteSpace(e.CoverLocalPath) &&
                File.Exists(e.CoverLocalPath))
            {
                try { File.Delete(e.CoverLocalPath); } catch { }
            }
        }

        await SaveAsync();
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        try
        {
            Directory.CreateDirectory(CoversDir);
            if (!File.Exists(HistoryFile))
            {
                _loadedTcs.TrySetResult(); // no file — signal immediately
                return;
            }

            string json = await File.ReadAllTextAsync(HistoryFile);
            var list = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
            if (list == null)
            {
                _loadedTcs.TrySetResult();
                return;
            }

            // ── Fast pass: set file/cover availability (no ZIP reads) ──────────
            bool needsSave = false;
            foreach (var entry in list)
            {
                string? resolved = Shuka.Android.Platforms.Android.EpubOpener
                    .ResolveAccessiblePath(entry.EpubPath, entry.Title, entry.Url);
                if (resolved != null)
                {
                    if (entry.EpubPath != resolved)
                    {
                        entry.EpubPath = resolved;
                        needsSave = true;
                    }
                    entry.IsFileAvailable = true;
                }
                else
                {
                    entry.IsFileAvailable = false;
                }
                entry.IsCoverAvailable = !string.IsNullOrWhiteSpace(entry.CoverLocalPath)
                    && File.Exists(entry.CoverLocalPath);
            }

            // ── Populate collection then signal — RunAsync can now proceed ─────
            await MainThread.InvokeOnMainThreadAsync(() => Entries.AddRange(list));
            _loadedTcs.TrySetResult(); // signal BEFORE slow chapter migration

            // ── Slow pass: count chapters & extract missing covers from EPUB (migration) ─
            foreach (var entry in list)
            {
                if (entry.ChapterCount == 0)
                {
                    int count = TryCountChaptersFromEpub(entry.EpubPath);
                    if (count > 0)
                    {
                        entry.ChapterCount = count;
                        needsSave = true;
                    }
                }

                if (!entry.IsCoverAvailable || string.IsNullOrWhiteSpace(entry.CoverLocalPath) || !File.Exists(entry.CoverLocalPath))
                {
                    string? extracted = TryExtractCoverFromEpub(entry.EpubPath, entry.Id);
                    if (!string.IsNullOrWhiteSpace(extracted) && File.Exists(extracted))
                    {
                        entry.CoverLocalPath = extracted;
                        entry.IsCoverAvailable = true;
                        needsSave = true;
                    }
                }
            }

            if (needsSave)
                await SaveAsync();
        }
        catch { /* corrupt file — start fresh */ }
        finally
        {
            // Safety net: ensure signal is always fired even on exception.
            _loadedTcs.TrySetResult();
        }
    }

    /// <summary>
    /// Opens the EPUB zip and counts chapter spine items from content.opf.
    /// The spine contains: cover, titlepage, ch1, ch2, ... chN.
    /// Returns 0 if the file can't be read.
    /// </summary>
    private static int TryCountChaptersFromEpub(string? epubPath)
    {
        if (string.IsNullOrWhiteSpace(epubPath)) return 0;

        try
        {
            Stream? stream = null;
            if (epubPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                var ctx = global::Android.App.Application.Context;
                var uri = global::Android.Net.Uri.Parse(epubPath);
                if (uri == null) return 0;
                stream = ctx.ContentResolver?.OpenInputStream(uri);
            }
            else
            {
                if (!File.Exists(epubPath)) return 0;
                stream = File.OpenRead(epubPath);
            }

            if (stream == null) return 0;

            using (stream)
            {
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
                var opf = archive.GetEntry("OEBPS/content.opf");
                if (opf == null) return 0;

                using var reader = new StreamReader(opf.Open());
                string content = reader.ReadToEnd();

                // Count <itemref idref="chN"/> entries — each chapter has id="chN"
                int count = System.Text.RegularExpressions.Regex.Matches(
                    content,
                    @"<itemref\s+idref=""ch\d+""",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;

                return count;
            }
        }
        catch { return 0; }
    }

    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var list = Entries.ToList();
            string json = JsonSerializer.Serialize(list,
                new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(HistoryFile, json);
        }
        catch { }
        finally { _saveLock.Release(); }
    }

    // ── Cover caching & extraction ───────────────────────────────────────────

    private static string? TryExtractCoverFromEpub(string? epubPath, Guid id)
    {
        if (string.IsNullOrWhiteSpace(epubPath)) return null;

        try
        {
            Stream? stream = null;
            if (epubPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                var ctx = global::Android.App.Application.Context;
                var uri = global::Android.Net.Uri.Parse(epubPath);
                if (uri == null) return null;
                stream = ctx.ContentResolver?.OpenInputStream(uri);
            }
            else
            {
                if (!File.Exists(epubPath)) return null;
                stream = File.OpenRead(epubPath);
            }

            if (stream == null) return null;

            using (stream)
            {
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

                // Look for cover image entry in EPUB (exclude SVG and HTML/XML)
                var coverEntry = archive.Entries.FirstOrDefault(e =>
                {
                    string name = e.FullName.ToLowerInvariant();
                    return (name.Contains("cover.") || name.StartsWith("oebps/cover.") || name.EndsWith("/cover.jpg") || name.EndsWith("/cover.png") || name.EndsWith("/cover.jpeg") || name.EndsWith("/cover.webp") || name.EndsWith("/cover.gif"))
                        && (name.EndsWith(".jpg") || name.EndsWith(".jpeg") || name.EndsWith(".png") || name.EndsWith(".webp") || name.EndsWith(".gif"));
                });

                // Fallback: any image file in archive
                coverEntry ??= archive.Entries.FirstOrDefault(e =>
                {
                    string name = e.FullName.ToLowerInvariant();
                    return (name.EndsWith(".jpg") || name.EndsWith(".jpeg") || name.EndsWith(".png") || name.EndsWith(".webp") || name.EndsWith(".gif"))
                        && !name.EndsWith(".svg");
                });

                if (coverEntry == null) return null;

                string ext = Path.GetExtension(coverEntry.Name).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";

                Directory.CreateDirectory(CoversDir);
                string outPath = Path.Combine(CoversDir, $"{id:N}{ext}");

                using var entryStream = coverEntry.Open();
                using var outStream = File.Create(outPath);
                entryStream.CopyTo(outStream);

                return outPath;
            }
        }
        catch { return null; }
    }

    private static async Task<string?> CacheCoverAsync(Guid id, string url)
    {
        try
        {
            string fetchUrl = NoveldexAdapter.NormalizeCoverUrl(url) ?? url;
            string ext  = Path.GetExtension(new Uri(fetchUrl).AbsolutePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
            Directory.CreateDirectory(CoversDir);
            string path = Path.Combine(CoversDir, $"{id:N}{ext}");

            if (File.Exists(path)) return path;

            using var req = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
            string? referer = NoveldexAdapter.GetCoverReferer(fetchUrl);
            if (referer != null)
            {
                try { req.Headers.Referrer = new Uri(referer); } catch { }
            }

            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
