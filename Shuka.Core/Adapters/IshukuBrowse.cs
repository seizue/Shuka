using System.Text.Json;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for ishuku.net (爱书库) — Simplified Chinese novel site.
///
/// Home/Browse: https://ishuku.net/
/// API base:    https://ishuku.net/api/books?sort=updated&amp;page=N
/// Book URLs:   https://ishuku.net/books/{bookId}
/// </summary>
public class IshukuBrowse : IBrowsableAdapter
{
    public string SiteName         => "ishuku.net";
    public string Description      => "Chinese novels · simplified chinese · free";
    public string IconGlyph        => "\uE894"; // language (globe)
    public bool   RequiresCfBypass => false;

    public string HomeUrl => "https://ishuku.net/";

    public string GetRecentUrl(int page = 1) =>
        $"https://ishuku.net/api/books?sort=updated&page={page}";

    public string GetPopularUrl(int page = 1) =>
        $"https://ishuku.net/api/books?sort=popularity&page={page}";

    public string GetSearchUrl(string query, int page = 1) =>
        $"https://ishuku.net/api/books?q={Uri.EscapeDataString(query)}&page={page}";

    public IReadOnlyList<SourceFilter>? Filters => _filters;

    // BL genre slugs: the single "danmei" slug only has ~9 books tagged with it.
    // The actual BL catalogue is spread across chongsheng (穿越重生耽美), jiakong (古代架空耽美),
    // xiandaidushi (现代都市耽美), bltr (BL同人), and chuanyue (穿越重生).
    private const string BlGenres =
        "genre=danmei&genre=chongsheng&genre=jiakong&genre=xiandaidushi&genre=bltr&genre=chuanyue";

    private static readonly SourceFilter[] _filters =
    [
        new SourceFilter("GL",      page => $"https://ishuku.net/api/books?genre=gl&sort=popularity&page={page}"),
        new SourceFilter("BL",      page => $"https://ishuku.net/api/books?{BlGenres}&sort=popularity&page={page}"),
        new SourceFilter("Popular", page => $"https://ishuku.net/api/books?sort=popularity&page={page}"),
        new SourceFilter("Recent",  page => $"https://ishuku.net/api/books?sort=updated&page={page}"),
    ];

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();

        try
        {
            using var doc = JsonDocument.Parse(html);
            var root = doc.RootElement;

            if (!root.TryGetProperty("books", out var booksEl) || booksEl.ValueKind != JsonValueKind.Array)
                return new ListingPage(novels, false, 1);

            foreach (var el in booksEl.EnumerateArray())
            {
                string id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;

                string title = el.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(title)) continue;

                string? author = null;
                if (el.TryGetProperty("author", out var ap) && ap.TryGetProperty("name", out var np))
                    author = np.GetString();

                string? cover = null;
                if (el.TryGetProperty("cover_url", out var cp))
                {
                    string raw = cp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(raw))
                        cover = raw.StartsWith("/") ? "https://ishuku.net" + raw : raw;
                }

                string? description = el.TryGetProperty("description", out var dp) ? dp.GetString() : null;

                string bookUrl = $"https://ishuku.net/books/{id}";

                novels.Add(new NovelEntry(title, author, bookUrl, cover, description, null));
            }

            // Pagination
            int page     = root.TryGetProperty("page",      out var pageProp)     ? pageProp.GetInt32()     : 1;
            int pageSize = root.TryGetProperty("page_size", out var sizeProp)     ? sizeProp.GetInt32()     : 20;
            int total    = root.TryGetProperty("total",     out var totalProp)    ? totalProp.GetInt32()    : 0;

            bool hasNext = (pageSize > 0 && total > 0)
                ? page * pageSize < total
                : novels.Count >= 20;

            return new ListingPage(novels, hasNext, page);
        }
        catch
        {
            return new ListingPage(novels, false, 1);
        }
    }
}
