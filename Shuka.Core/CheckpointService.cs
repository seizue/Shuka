using System.Text.Json;

namespace Shuka.Core;

/// <summary>
/// Saves and loads per-chapter translation checkpoints so downloads can
/// resume from where they left off after a failure or cancellation.
///
/// Checkpoint files are stored alongside the temp EPUB in the cache directory.
/// They are deleted automatically when the download completes successfully.
/// </summary>
public static class CheckpointService
{
    private record ChapterRecord(int Index, string Title, string Text);
    private record CheckpointData(string Url, List<ChapterRecord> Chapters);

    /// <summary>
    /// Returns the checkpoint file path for a given download URL.
    /// The path is deterministic so retries always find the same file.
    /// </summary>
    public static string GetCheckpointPath(string cacheDir, string url)
    {
        // Use a stable MD5 hash of the URL as the filename (GetHashCode is randomized per-run in .NET Core)
        byte[] hashBytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        string hex = Convert.ToHexString(hashBytes);
        return Path.Combine(cacheDir, $"_checkpoint_{hex}.json");
    }

    /// <summary>
    /// Loads previously completed chapters from a checkpoint file.
    /// Returns an empty array if the file doesn't exist or is corrupt.
    /// </summary>
    public static async Task<(string title, string text)?[]> LoadAsync(
        string checkpointPath, int totalChapters)
    {
        var results = new (string title, string text)?[totalChapters];
        if (!File.Exists(checkpointPath)) return results;

        try
        {
            string json = await File.ReadAllTextAsync(checkpointPath);
            var data = JsonSerializer.Deserialize<CheckpointData>(json);
            if (data?.Chapters == null) return results;

            foreach (var ch in data.Chapters)
            {
                if (ch.Index >= 0 && ch.Index < totalChapters)
                    results[ch.Index] = (ch.Title, ch.Text);
            }
        }
        catch { /* corrupt checkpoint — start fresh */ }

        return results;
    }

    /// <summary>
    /// Appends a completed chapter to the checkpoint file.
    /// Called after each chapter finishes so progress is never lost.
    /// </summary>
    public static async Task SaveChapterAsync(
        string checkpointPath, string url,
        int index, string title, string text,
        SemaphoreSlim writeLock)
    {
        await writeLock.WaitAsync();
        try
        {
            // Load existing data
            CheckpointData data;
            if (File.Exists(checkpointPath))
            {
                try
                {
                    string existing = await File.ReadAllTextAsync(checkpointPath);
                    data = JsonSerializer.Deserialize<CheckpointData>(existing)
                           ?? new CheckpointData(url, new List<ChapterRecord>());
                }
                catch { data = new CheckpointData(url, new List<ChapterRecord>()); }
            }
            else
            {
                data = new CheckpointData(url, new List<ChapterRecord>());
            }

            // Add this chapter (avoid duplicates)
            data.Chapters.RemoveAll(c => c.Index == index);
            data.Chapters.Add(new ChapterRecord(index, title, text));

            await File.WriteAllTextAsync(checkpointPath,
                JsonSerializer.Serialize(data));
        }
        finally { writeLock.Release(); }
    }

    /// <summary>Deletes the checkpoint file after a successful download.</summary>
    public static void Delete(string checkpointPath)
    {
        try { if (File.Exists(checkpointPath)) File.Delete(checkpointPath); }
        catch { }
    }

    /// <summary>Returns how many chapters are already saved in the checkpoint.</summary>
    public static int CountSaved(string checkpointPath)
    {
        if (!File.Exists(checkpointPath)) return 0;
        try
        {
            string json = File.ReadAllText(checkpointPath);
            var data = JsonSerializer.Deserialize<CheckpointData>(json);
            return data?.Chapters?.Count ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Loads all saved chapters from a checkpoint file, ordered by chapter index.
    /// </summary>
    public static async Task<List<(int Idx, string Title, string Text)>> LoadAllSavedChaptersAsync(string checkpointPath)
    {
        var list = new List<(int Idx, string Title, string Text)>();
        if (!File.Exists(checkpointPath)) return list;

        try
        {
            string json = await File.ReadAllTextAsync(checkpointPath);
            var data = JsonSerializer.Deserialize<CheckpointData>(json);
            if (data?.Chapters != null)
            {
                foreach (var ch in data.Chapters.OrderBy(c => c.Index))
                {
                    list.Add((ch.Index + 1, ch.Title, ch.Text));
                }
            }
        }
        catch { /* corrupt checkpoint */ }

        return list;
    }

    /// <summary>
    /// Scans <paramref name="cacheDir"/> for all checkpoint files and returns
    /// a summary of each one: (FilePath, Url, ChapterCount).
    /// Files that cannot be read or have no chapters are skipped.
    /// </summary>
    public static List<(string FilePath, string Url, int Count)> ListAllCheckpoints(string cacheDir)
    {
        var results = new List<(string FilePath, string Url, int Count)>();
        if (!Directory.Exists(cacheDir)) return results;

        foreach (string file in Directory.GetFiles(cacheDir, "_checkpoint_*.json"))
        {
            try
            {
                string json = File.ReadAllText(file);
                var data = JsonSerializer.Deserialize<CheckpointData>(json);
                if (data?.Url != null && data.Chapters?.Count > 0)
                    results.Add((file, data.Url, data.Chapters.Count));
            }
            catch { /* skip corrupt files */ }
        }

        // Most-recently-modified first
        results.Sort((a, b) =>
            File.GetLastWriteTimeUtc(b.FilePath).CompareTo(File.GetLastWriteTimeUtc(a.FilePath)));

        return results;
    }
}
