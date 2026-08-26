namespace Shuka.Core;

// Site adapter interface — implement this to add a new site
public interface ISiteAdapter
{
    string SiteName { get; }
    bool Matches(string url);
    string NormalizeUrl(string url);
    IndexInfo ParseIndex(string html, string indexUrl);
    List<string> ExtractChapterText(string html);
    bool RequiresCfBypass => false;
    /// <summary>
    /// When true, the download stops at the very first locked/empty chapter
    /// instead of waiting for 3 consecutive locked chapters.
    /// </summary>
    bool StopOnFirstLockedChapter => false;
}

// Parsed index result from an adapter
public record IndexInfo(string Title, string Author, List<ChapterRef> ChapterUrls, string? CoverUrl,
    string? CoverHintUrl = null);

// A chapter reference (URL + optional display title)
public record ChapterRef(string Url, string Title);

// BookInfo — holds all metadata and chapter list for one novel
public record BookInfo(string IndexUrl, string Title, string Author,
    List<ChapterRef> ChapterUrls, int Total, int ChapterLimit,
    string? CoverUrl, ISiteAdapter Adapter)
{
    public string? TitleEn    { get; set; }
    public string? AuthorEn   { get; set; }
    /// <summary>1-based start chapter (1 = first). 0 means start from beginning.</summary>
    public int ChapterFrom    { get; set; } = 0;
}

// Progress event args for download/translate reporting
public class ProgressEventArgs : EventArgs
{
    public int Current  { get; init; }
    public int Total    { get; init; }
    public string Message { get; init; } = "";
}
