using Android.Content;
using Android.OS;
using AndroidX.Core.Content;
using System.IO;
using Microsoft.Maui.Storage;

namespace Shuka.Android.Platforms.Android;

/// <summary>
/// Opens or shares EPUB files using native Android intents with proper
/// URI permission granting. Handles both SAF <c>content://</c> URIs and
/// regular file paths via FileProvider.
/// </summary>
public static class EpubOpener
{
    private const string Authority = "com.seizue.shuka.fileprovider";

    /// <summary>
    /// Returns <c>true</c> if the given EPUB path is accessible — either a
    /// SAF <c>content://</c> URI (verified via content resolver) or a file that exists on disk.
    /// </summary>
    public static bool IsAccessible(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            // Primary check: try opening via ContentResolver.
            try
            {
                var ctx = global::Android.App.Application.Context;
                var uri = global::Android.Net.Uri.Parse(path);
                if (uri != null)
                {
                    using var fd = ctx.ContentResolver?.OpenFileDescriptor(uri, "r");
                    if (fd != null)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[EpubOpener.IsAccessible] ContentResolver OK for '{path}'");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[EpubOpener.IsAccessible] ContentResolver failed for '{path}': {ex.Message}");
            }

            // Fallback: ContentResolver can throw SecurityException after app restart
            // for SAF URIs even though the physical file still exists on disk.
            // Decode the document URI to a real filesystem path and check File.Exists.
            try
            {
                string? realPath = SafContentUriToRealPath(path);
                if (realPath != null && File.Exists(realPath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[EpubOpener.IsAccessible] ContentResolver failed but real path exists: '{realPath}'");
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[EpubOpener.IsAccessible] SAF real-path fallback failed: {ex.Message}");
            }

            return false;
        }
        return File.Exists(path);
    }

    /// <summary>
    /// Attempts to decode a SAF <c>content://</c> document URI to a real filesystem path.
    /// Only works for primary storage (internal storage) documents.
    /// Returns <c>null</c> for external/SD-card URIs or on any error.
    /// </summary>
    public static string? SafContentUriToRealPath(string? contentUri)
    {
        if (string.IsNullOrWhiteSpace(contentUri)) return null;
        if (!contentUri.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var uri = global::Android.Net.Uri.Parse(contentUri);
            if (uri == null) return null;

            // GetDocumentId works on a document URI (not a tree URI).
            string? docId = global::Android.Provider.DocumentsContract.GetDocumentId(uri);
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.SafContentUriToRealPath] contentUri='{contentUri}' docId='{docId}'");

            if (docId != null && docId.StartsWith("primary:", StringComparison.OrdinalIgnoreCase))
            {
                string rel = docId["primary:".Length..];
#pragma warning disable CA1422
                string root = global::Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath;
#pragma warning restore CA1422
                string realPath = Path.Combine(root, rel);
                System.Diagnostics.Debug.WriteLine(
                    $"[EpubOpener.SafContentUriToRealPath] realPath='{realPath}' exists={File.Exists(realPath)}");
                return realPath;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.SafContentUriToRealPath] failed for '{contentUri}': {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Returns <c>true</c> if the given EPUB is accessible and is a valid non-corrupted ZIP/EPUB file.
    /// </summary>
    public static bool IsValidEpub(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!IsAccessible(path)) return false;
        try
        {
            if (path.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                var ctx = global::Android.App.Application.Context;
                var uri = global::Android.Net.Uri.Parse(path);
                if (uri == null) return false;
                using var stream = ctx.ContentResolver?.OpenInputStream(uri);
                if (stream == null) return false;
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
                return archive.Entries.Count > 0;
            }
            else
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                return zip.Entries.Count > 0;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Open an EPUB file in an external reader app using <c>ACTION_VIEW</c>.
    /// </summary>
    /// <exception cref="FileNotFoundException">The EPUB file doesn't exist on disk.</exception>
    /// <exception cref="InvalidOperationException">No EPUB reader app is installed.</exception>
    public static void Open(string epubPath)
    {
        var intent = BuildViewIntent(epubPath);
        Launch(intent, "No EPUB reader app is installed. Install one from the Play Store and try again.");
    }

    /// <summary>
    /// Share an EPUB file via the system share sheet using <c>ACTION_SEND</c>.
    /// </summary>
    /// <exception cref="FileNotFoundException">The EPUB file doesn't exist on disk.</exception>
    /// <exception cref="InvalidOperationException">No app found to share the EPUB file.</exception>
    public static void Share(string epubPath, string title)
    {
        var sendIntent = BuildSendIntent(epubPath, title);
        var chooser = Intent.CreateChooser(sendIntent, "Share EPUB")!;
        Launch(chooser, "No app found to share the EPUB file.");
    }

    // ── Intent builders ─────────────────────────────────────────────────────

    private static Intent BuildViewIntent(string epubPath)
    {
        var ctx = global::Android.App.Application.Context;

        if (epubPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = global::Android.Net.Uri.Parse(epubPath)!;
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, "application/epub+zip");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            return intent;
        }

        if (File.Exists(epubPath))
        {
            var file = new Java.IO.File(epubPath);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(ctx, Authority, file);
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, "application/epub+zip");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            return intent;
        }

        throw new FileNotFoundException("EPUB file not found.", epubPath);
    }

    private static Intent BuildSendIntent(string epubPath, string title)
    {
        var ctx = global::Android.App.Application.Context;

        if (epubPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = global::Android.Net.Uri.Parse(epubPath)!;
            var intent = new Intent(Intent.ActionSend);
            intent.SetType("application/epub+zip");
            intent.PutExtra(Intent.ExtraStream, uri);
            intent.PutExtra(Intent.ExtraSubject, title);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            return intent;
        }

        if (File.Exists(epubPath))
        {
            var file = new Java.IO.File(epubPath);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(ctx, Authority, file);
            var intent = new Intent(Intent.ActionSend);
            intent.SetType("application/epub+zip");
            intent.PutExtra(Intent.ExtraStream, uri);
            intent.PutExtra(Intent.ExtraSubject, title);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            return intent;
        }

        throw new FileNotFoundException("EPUB file not found.", epubPath);
    }

    // ── Launch helper ───────────────────────────────────────────────────────

    private static void Launch(Intent intent, string noAppMessage)
    {
        var ctx = global::Android.App.Application.Context;

        // NOTE: Do NOT check ResolveActivity() here — on Android 11+
        // (API 30) package-visibility restrictions cause it to return null
        // for implicit intents even when capable apps are installed.
        // StartActivity() itself is unaffected and resolves correctly.

        try
        {
            // Prefer the current Activity for proper back-stack behaviour
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity != null)
            {
                activity.StartActivity(intent);
            }
            else
            {
                intent.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(intent);
            }
        }
        catch (global::Android.Content.ActivityNotFoundException)
        {
            throw new InvalidOperationException(noAppMessage);
        }
    }

    /// <summary>
    /// Searches the SAF tree URI for a file with the given display name and returns its content URI if found.
    /// </summary>
    public static string? FindFileInSafTree(string treeUriStr, string displayName)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var treeUri = global::Android.Net.Uri.Parse(treeUriStr);
            if (treeUri == null) return null;

            var treeDocId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
            var childrenUri = global::Android.Provider.DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, treeDocId!);
            if (childrenUri == null) return null;

            var cr = ctx.ContentResolver;
            if (cr == null) return null;

            string[] projection = {
                global::Android.Provider.DocumentsContract.Document.ColumnDocumentId,
                global::Android.Provider.DocumentsContract.Document.ColumnDisplayName
            };

            using var cursor = cr.Query(childrenUri, projection, null, null, null);
            if (cursor != null)
            {
                while (cursor.MoveToNext())
                {
                    string? name = cursor.GetString(1);
                    if (string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                    {
                        string? docId = cursor.GetString(0);
                        if (docId == null) continue;
                        var docUri = global::Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId);
                        return docUri?.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SAF] FindFileInSafTree error: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Resolves the best accessible EPUB path for a history/download entry.
    /// Checks the stored path first, then scans every known download location.
    /// Returns a filesystem path when possible (heals stale SAF content URIs).
    /// </summary>
    public static string? ResolveAccessiblePath(string? storedPath, string? title, string? url = null)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[EpubOpener.ResolveAccessiblePath] storedPath='{storedPath ?? "null"}' " +
            $"title='{title ?? "null"}' url='{url ?? "null"}'");

        // 1. Stored path (as-is, then decoded real path for content:// URIs)
        if (TryResolveSinglePath(storedPath, "stored", out string? resolved))
            return resolved;

        // 2. Filename from stored path — title sanitization may have changed since download
        string? storedFileName = GetEpubFileName(storedPath);
        if (!string.IsNullOrWhiteSpace(storedFileName))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.ResolveAccessiblePath] scanning by stored filename '{storedFileName}'");
            if (TryFindByFileName(storedFileName, storedPath, out resolved))
                return resolved;
        }

        // 3. URL slug (czbooks/quanben book id) — often the actual on-disk filename
        if (!string.IsNullOrWhiteSpace(url))
        {
            resolved = TryFindByUrlSlug(url, storedPath);
            if (resolved != null)
                return resolved;
        }

        // 4. Title-derived filename across all download locations
        if (!string.IsNullOrWhiteSpace(title))
        {
            resolved = FindExistingEpub(title, storedPath);
            if (resolved != null)
                return resolved;
        }

        // 5. Last resort: scan download folders for any .epub matching slug or title
        resolved = ScanDownloadFolders(storedPath, title, url);
        if (resolved != null)
            return resolved;

        System.Diagnostics.Debug.WriteLine("[EpubOpener.ResolveAccessiblePath] no accessible EPUB found");
        return null;
    }

    /// <summary>
    /// Prefer a real filesystem path over a SAF content URI for opening/sharing.
    /// </summary>
    public static string PreferFilesystemPath(string path)
    {
        if (!path.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            return path;

        string? realPath = SafContentUriToRealPath(path);
        if (realPath != null && File.Exists(realPath))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.PreferFilesystemPath] '{path}' → '{realPath}'");
            return realPath;
        }

        return path;
    }

    /// <summary>
    /// Checks every known download location for an EPUB matching the given title.
    /// Optionally uses <paramref name="storedPath"/> to recover the original filename.
    /// </summary>
    public static string? FindExistingEpub(string title, string? storedPath = null)
    {
        string fileName = SanitizeFileName(title) + ".epub";
        System.Diagnostics.Debug.WriteLine(
            $"[EpubOpener.FindExistingEpub] title='{title}' fileName='{fileName}' storedPath='{storedPath ?? "null"}'");

        if (TryFindByFileName(fileName, storedPath, out string? resolved))
            return resolved;

        System.Diagnostics.Debug.WriteLine(
            $"[EpubOpener.FindExistingEpub] No EPUB found for '{title}'");
        return null;
    }

    private static bool TryResolveSinglePath(string? path, string source, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.TryResolveSinglePath] ({source}) path is null/empty");
            return false;
        }

        if (IsAccessible(path))
        {
            resolved = PreferFilesystemPath(path);
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.TryResolveSinglePath] ({source}) accessible: '{path}' → '{resolved}'");
            return true;
        }

        if (path.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            string? realPath = SafContentUriToRealPath(path);
            bool exists = realPath != null && File.Exists(realPath);
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.TryResolveSinglePath] ({source}) content URI not accessible via resolver; " +
                $"realPath='{realPath ?? "null"}' exists={exists}");
            if (exists)
            {
                resolved = realPath;
                return true;
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.TryResolveSinglePath] ({source}) file path not accessible: '{path}' exists={File.Exists(path)}");
        }

        return false;
    }

    private static bool TryFindByFileName(string fileName, string? hintPath, out string? resolved)
    {
        resolved = null;

        string treeUriStr = Preferences.Default.Get("download_tree_uri", "");
        if (!string.IsNullOrWhiteSpace(treeUriStr))
        {
            string? safUri = FindFileInSafTree(treeUriStr, fileName);
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.TryFindByFileName] SAF content-URI for '{fileName}': '{safUri ?? "null"}'");
            if (safUri != null && TryResolveSinglePath(safUri, "saf-uri", out resolved))
                return true;
        }

        foreach (string dir in EnumerateDownloadDirectories(hintPath))
        {
            string candidate = Path.Combine(dir, fileName);
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.TryFindByFileName] checking '{candidate}'");
            if (TryResolveSinglePath(candidate, "scan", out resolved))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns every directory that may contain downloaded EPUBs (default, custom, SAF-decoded).
    /// </summary>
    public static IEnumerable<string> EnumerateDownloadDirectories(string? hintPath = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                string full = Path.GetFullPath(dir);
                if (seen.Add(full))
                    System.Diagnostics.Debug.WriteLine(
                        $"[EpubOpener.EnumerateDownloadDirectories] + '{full}'");
            }
            catch { /* invalid path */ }
        }

        var downloads = global::Android.OS.Environment.GetExternalStoragePublicDirectory(
            global::Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
        TryAdd(Path.Combine(downloads, "Shuka"));

        string savedPath = Preferences.Default.Get("download_output_path", "");
        TryAdd(savedPath);

        string? safDir = DecodeSafTreeDirectory();
        TryAdd(safDir);

        // Include the parent folder from a stored path (covers old custom locations)
        if (!string.IsNullOrWhiteSpace(hintPath))
        {
            if (hintPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                string? realPath = SafContentUriToRealPath(hintPath);
                TryAdd(realPath != null ? Path.GetDirectoryName(realPath) : null);
            }
            else
            {
                TryAdd(Path.GetDirectoryName(hintPath));
            }
        }

        return seen;
    }

    private static string? DecodeSafTreeDirectory()
    {
        string treeUriStr = Preferences.Default.Get("download_tree_uri", "");
        if (string.IsNullOrWhiteSpace(treeUriStr)) return null;

        try
        {
            var treeUri = global::Android.Net.Uri.Parse(treeUriStr);
            if (treeUri == null) return null;

            string? docId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
            if (docId == null || !docId.StartsWith("primary:", StringComparison.OrdinalIgnoreCase))
                return null;

            string rel = docId["primary:".Length..];
#pragma warning disable CA1422
            string root = global::Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath;
#pragma warning restore CA1422
            return Path.Combine(root, rel);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.DecodeSafTreeDirectory] failed: {ex.Message}");
            return null;
        }
    }

    private static string? GetEpubFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (path.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            string? realPath = SafContentUriToRealPath(path);
            if (!string.IsNullOrWhiteSpace(realPath))
                return Path.GetFileName(realPath);

            // Last segment of the URI may encode the filename
            try
            {
                var uri = global::Android.Net.Uri.Parse(path);
                string? last = uri?.LastPathSegment;
                if (!string.IsNullOrWhiteSpace(last))
                    return Uri.UnescapeDataString(last);
            }
            catch { }
            return null;
        }

        return Path.GetFileName(path);
    }

    /// <summary>Extracts the book id slug from czbooks/quanben-style URLs.</summary>
    public static string? ExtractBookSlug(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            url, @"/n/([^/?#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? TryFindByUrlSlug(string url, string? hintPath)
    {
        string? slug = ExtractBookSlug(url);
        if (string.IsNullOrWhiteSpace(slug)) return null;

        System.Diagnostics.Debug.WriteLine(
            $"[EpubOpener.TryFindByUrlSlug] slug='{slug}'");

        string[] candidates =
        [
            SanitizeFileName(slug) + ".epub",
            slug + ".epub",
        ];

        foreach (string fileName in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryFindByFileName(fileName, hintPath, out string? resolved))
                return resolved;
        }

        return null;
    }

    /// <summary>
    /// Lists .epub files in all download directories and picks the best match by slug or title.
    /// </summary>
    private static string? ScanDownloadFolders(string? hintPath, string? title, string? url)
    {
        string? slug = ExtractBookSlug(url);
        string? slugKey = slug != null ? SanitizeFileName(slug).ToLowerInvariant() : null;
        string? titleKey = !string.IsNullOrWhiteSpace(title)
            ? SanitizeFileName(title).ToLowerInvariant()
            : null;
        string? storedKey = GetEpubFileName(hintPath)?.ToLowerInvariant();

        if (slugKey == null && titleKey == null && storedKey == null)
            return null;

        System.Diagnostics.Debug.WriteLine(
            $"[EpubOpener.ScanDownloadFolders] slugKey='{slugKey}' titleKey='{titleKey}' storedKey='{storedKey}'");

        string? bestPath = null;
        int bestScore = 0;

        foreach (string dir in EnumerateDownloadDirectories(hintPath))
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.epub"))
                {
                    string name = Path.GetFileName(file);
                    string nameKey = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                    int score = ScoreEpubName(nameKey, slugKey, titleKey, storedKey);
                    if (score > bestScore && TryResolveSinglePath(file, "folder-scan", out _))
                    {
                        bestScore = score;
                        bestPath = PreferFilesystemPath(file);
                        System.Diagnostics.Debug.WriteLine(
                            $"[EpubOpener.ScanDownloadFolders] candidate '{file}' score={score}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[EpubOpener.ScanDownloadFolders] error in '{dir}': {ex.Message}");
            }
        }

        if (bestPath != null)
            System.Diagnostics.Debug.WriteLine(
                $"[EpubOpener.ScanDownloadFolders] best match: '{bestPath}' score={bestScore}");

        return bestScore >= 80 ? bestPath : null;
    }

    private static int ScoreEpubName(string nameKey, string? slugKey, string? titleKey, string? storedKey)
    {
        string? storedStem = storedKey != null
            ? Path.GetFileNameWithoutExtension(storedKey).ToLowerInvariant()
            : null;

        if (storedStem != null && nameKey == storedStem) return 100;
        if (slugKey != null && nameKey == slugKey) return 95;
        if (titleKey != null && nameKey == titleKey) return 90;
        if (slugKey != null && nameKey.StartsWith(slugKey, StringComparison.Ordinal)) return 85;
        if (titleKey != null && nameKey.StartsWith(titleKey, StringComparison.Ordinal)) return 82;
        if (slugKey != null && nameKey.Contains(slugKey, StringComparison.Ordinal)) return 80;
        return 0;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = System.Text.RegularExpressions.Regex.Replace(name, @"_+", "_").Trim('_');
        return name.Length > 80 ? name[..80] : name;
    }
}
