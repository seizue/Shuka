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

    /// <summary>
    /// Returns all directories where EPUBs and backups can be stored
    /// (including user custom folder, SAF tree directory, and default Download/Shuka).
    /// </summary>
    public static IEnumerable<string> GetExternalBackupDirs()
    {
        var dirs = new List<string>();
#if ANDROID
        try
        {
            foreach (string dir in Shuka.Android.Platforms.Android.EpubOpener.EnumerateDownloadDirectories())
            {
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { }
                    if (Directory.Exists(dir))
                        dirs.Add(dir);
                }
            }
        }
        catch { }
#endif
        return dirs;
    }

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
            // Update existing entry with latest details, path, and completion timestamp
            System.Diagnostics.Debug.WriteLine(
                $"[HistoryService] Updating existing entry for '{existing.Title}' (path: {item.EpubPath})");

            existing.Title           = !string.IsNullOrWhiteSpace(item.Title) ? item.Title : existing.Title;
            existing.Author          = !string.IsNullOrWhiteSpace(item.Author) ? item.Author : existing.Author;
            existing.EpubPath        = item.EpubPath;
            existing.ChapterCount    = item.TotalChapters > 0 ? item.TotalChapters : (item.Chapters > 0 ? item.Chapters : existing.ChapterCount);
            existing.CompletedAt     = DateTime.Now;
            existing.IsFileAvailable = IsEpubAccessible(existing.EpubPath);

            // Re-check or update cover if remote cover became available
            if (string.IsNullOrWhiteSpace(existing.CoverLocalPath) || !File.Exists(existing.CoverLocalPath))
            {
                string? coverToUse = !string.IsNullOrWhiteSpace(item.CoverUrl) ? item.CoverUrl : existing.CoverUrl;
                if (!string.IsNullOrWhiteSpace(coverToUse))
                    existing.CoverLocalPath = await CacheCoverAsync(existing.Id, coverToUse);

                if (string.IsNullOrWhiteSpace(existing.CoverLocalPath) || !File.Exists(existing.CoverLocalPath))
                    existing.CoverLocalPath = TryExtractCoverFromEpub(existing.EpubPath, existing.Id);

                existing.IsCoverAvailable = !string.IsNullOrWhiteSpace(existing.CoverLocalPath) && File.Exists(existing.CoverLocalPath);
            }

            // Move updated entry to the front of the list so it appears first in history
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                int currentIndex = Entries.IndexOf(existing);
                if (currentIndex > 0)
                {
                    Entries.Move(currentIndex, 0);
                }
                else if (currentIndex < 0)
                {
                    Entries.Insert(0, existing);
                }
                else
                {
                    // Item was already at index 0, but trigger a collection reset or re-sort so UI refreshes cards
                    Entries.RemoveAt(0);
                    Entries.Insert(0, existing);
                }
            });

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

            string? json = null;
            if (File.Exists(HistoryFile))
            {
                json = await File.ReadAllTextAsync(HistoryFile);
            }
            else
            {
                // Restore from external backup across any known download directory (custom or default)
                foreach (string dir in GetExternalBackupDirs())
                {
                    try
                    {
                        string backupPath = Path.Combine(dir, ".shuka_history_backup.json");
                        if (File.Exists(backupPath))
                        {
                            json = await File.ReadAllTextAsync(backupPath);
                            if (!string.IsNullOrWhiteSpace(json))
                            {
                                await File.WriteAllTextAsync(HistoryFile, json);
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }

            List<HistoryEntry>? list = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try { list = JsonSerializer.Deserialize<List<HistoryEntry>>(json); } catch { }
            }

            bool needsSave = false;

            // Fallback: If still no entries, scan download directories for existing EPUBs to auto-reconstruct history
            if (list == null || list.Count == 0)
            {
                list = ReconstructHistoryFromEpubFiles();
                if (list.Count > 0)
                    needsSave = true;
            }

            if (list == null || list.Count == 0)
            {
                _loadedTcs.TrySetResult();
                return;
            }

            // ── Fast pass: set file/cover availability (no ZIP reads) ──────────
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

            // Also mirror backup across all known download directories (custom and default) so history survives app uninstalls / updates
            try
            {
                foreach (string dir in GetExternalBackupDirs())
                {
                    try
                    {
                        string backupPath = Path.Combine(dir, ".shuka_history_backup.json");
                        if (list.Count > 0)
                            await File.WriteAllTextAsync(backupPath, json);
                        else if (File.Exists(backupPath))
                            File.Delete(backupPath);
                    }
                    catch { }
                }
            }
            catch { }
        }
        catch { }
        finally { _saveLock.Release(); }
    }

    /// <summary>
    /// If history was wiped (e.g. clean install or uninstall), scans all known EPUB
    /// download folders and automatically reconstructs history records from existing EPUB files.
    /// </summary>
    private static List<HistoryEntry> ReconstructHistoryFromEpubFiles()
    {
        var recovered = new List<HistoryEntry>();
        try
        {
            foreach (string dir in Shuka.Android.Platforms.Android.EpubOpener.EnumerateDownloadDirectories())
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.EnumerateFiles(dir, "*.epub"))
                {
                    try
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.StartsWith("_sample_", StringComparison.OrdinalIgnoreCase)) continue;

                        string title = fileName.Replace('_', ' ');
                        string author = "Unknown";
                        int chapterCount = TryCountChaptersFromEpub(file);

                        // Try extracting OPF title/creator from the EPUB package
                        try
                        {
                            using var stream = File.OpenRead(file);
                            using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
                            var opf = archive.GetEntry("OEBPS/content.opf");
                            if (opf != null)
                            {
                                using var reader = new StreamReader(opf.Open());
                                string opfText = reader.ReadToEnd();
                                var tMatch = System.Text.RegularExpressions.Regex.Match(opfText, @"<dc:title[^>]*>(.*?)</dc:title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (tMatch.Success && !string.IsNullOrWhiteSpace(tMatch.Groups[1].Value))
                                    title = System.Net.WebUtility.HtmlDecode(tMatch.Groups[1].Value.Trim());

                                var aMatch = System.Text.RegularExpressions.Regex.Match(opfText, @"<dc:creator[^>]*>(.*?)</dc:creator>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (aMatch.Success && !string.IsNullOrWhiteSpace(aMatch.Groups[1].Value))
                                    author = System.Net.WebUtility.HtmlDecode(aMatch.Groups[1].Value.Trim());
                            }
                        }
                        catch { }

                        var entry = new HistoryEntry
                        {
                            Id = Guid.NewGuid(),
                            Title = title,
                            Author = author,
                            Url = "",
                            EpubPath = file,
                            ChapterCount = chapterCount,
                            CompletedAt = File.GetLastWriteTime(file),
                            IsFileAvailable = true
                        };
                        recovered.Add(entry);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return recovered;
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
            Directory.CreateDirectory(CoversDir);

            // Do NOT use the URL path extension for cover file naming — CDN script URLs
            // like jjwxc.net's authorimagespace.php return a JPEG despite having a .php
            // extension, and Android's ImageSource can't load a .php file.
            // We download first, detect the real MIME from magic bytes, then save with
            // the correct extension.  An existing cached file with the right extension
            // is returned immediately (avoid re-downloading on every history AddAsync).
            foreach (string candidate in new[] { $"{id:N}.jpg", $"{id:N}.jpeg", $"{id:N}.png", $"{id:N}.gif", $"{id:N}.webp" })
            {
                string candidatePath = Path.Combine(CoversDir, candidate);
                if (File.Exists(candidatePath)) return candidatePath;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
            string? referer = NoveldexAdapter.GetCoverReferer(fetchUrl);
            if (referer != null)
            {
                try { req.Headers.Referrer = new Uri(referer); } catch { }
            }

            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();

            // Determine real extension from magic bytes, not URL
            string ext = ".jpg";
            if (bytes.Length >= 4)
            {
                if      (bytes[0] == 0x89 && bytes[1] == 0x50) ext = ".png";
                else if (bytes[0] == 0x47 && bytes[1] == 0x49) ext = ".gif";
                else if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46) ext = ".webp";
                // 0xFF 0xD8 → JPEG → ".jpg" (default)
            }

            string path = Path.Combine(CoversDir, $"{id:N}{ext}");
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
