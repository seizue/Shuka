using System.Collections.ObjectModel;
using System.Text.Json;

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
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) return true;
        return File.Exists(path);
    }

    private static string HistoryFile =>
        Path.Combine(FileSystem.AppDataDirectory, "history.json");

    private static string CoversDir =>
        Path.Combine(FileSystem.AppDataDirectory, "covers");

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private HistoryService()
    {
        _ = LoadAsync();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when a download completes. Saves the entry and caches the cover.
    /// </summary>
    public async Task AddAsync(DownloadItem item)
    {
        if (item.Status != DownloadStatus.Completed) return;

        // Don't add duplicates
        if (Entries.Any(e => e.Url == item.Url && e.EpubPath == item.EpubPath))
            return;

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

        entry.IsFileAvailable = IsEpubAccessible(entry.EpubPath);
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
            if (!File.Exists(HistoryFile)) return;
            string json = await File.ReadAllTextAsync(HistoryFile);
            var list = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
            if (list == null) return;

            // Migrate: patch any entries that were saved with ChapterCount = 0
            bool needsSave = false;
            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                entry.IsFileAvailable = IsEpubAccessible(entry.EpubPath);
                entry.IsCoverAvailable = !string.IsNullOrWhiteSpace(entry.CoverLocalPath) && File.Exists(entry.CoverLocalPath);

                if (entry.ChapterCount == 0)
                {
                    int count = TryCountChaptersFromEpub(entry.EpubPath);
                    if (count > 0)
                    {
                        entry.ChapterCount = count;
                        needsSave = true;
                    }
                }
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Entries.AddRange(list);
            });

            if (needsSave)
                await SaveAsync();
        }
        catch { /* corrupt file — start fresh */ }
    }

    /// <summary>
    /// Opens the EPUB zip and counts chapter spine items from content.opf.
    /// The spine contains: cover, titlepage, ch1, ch2, ... chN.
    /// Returns 0 if the file can't be read.
    /// </summary>
    private static int TryCountChaptersFromEpub(string? epubPath)
    {
        if (string.IsNullOrWhiteSpace(epubPath)) return 0;
        // SAF content URIs can't be opened with ZipFile
        if (epubPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) return 0;
        if (!File.Exists(epubPath)) return 0;

        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(epubPath);
            var opf = zip.GetEntry("OEBPS/content.opf");
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
        catch { return 0; }
    }

    private async Task SaveAsync()
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

    // ── Cover caching ─────────────────────────────────────────────────────────

    private static async Task<string?> CacheCoverAsync(Guid id, string url)
    {
        try
        {
            string ext  = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
            string path = Path.Combine(CoversDir, $"{id:N}{ext}");

            if (File.Exists(path)) return path;

            byte[] bytes = await _http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
