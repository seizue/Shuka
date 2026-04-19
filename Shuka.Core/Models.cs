namespace Shuka.Core;

// Site adapter interface — implement this to add a new site
public interface ISiteAdapter
{
    string SiteName { get; }
    bool Matches(string url);
    string NormalizeUrl(string url);
    IndexInfo ParseIndex(string html, string indexUrl);
    List<string> ExtractChapterText(string html);
}

// Parsed index result from an adapter
public record IndexInfo(string Title, string Author, List<ChapterRef> ChapterUrls, string? CoverUrl);

// A chapter reference (URL + optional display title)
public record ChapterRef(string Url, string Title);

// BookInfo — holds all metadata and chapter list for one novel
public record BookInfo(string IndexUrl, string Title, string Author,
    List<ChapterRef> ChapterUrls, int Total, int ChapterLimit,
    string? CoverUrl, ISiteAdapter Adapter)
{
    public string? TitleEn  { get; set; }
    public string? AuthorEn { get; set; }
}

// Progress event args for download/translate reporting
public class ProgressEventArgs : EventArgs
{
    public int Current  { get; init; }
    public int Total    { get; init; }
    public string Message { get; init; } = "";
}
