namespace Shuka.Core;

/// <summary>
/// A novel entry shown in the Discover browse listing.
/// </summary>
public record NovelEntry(
    string Title,
    string? Author,
    string Url,
    string? CoverUrl,
    string? Description,
    string? Tags,
    int? ChapterCount = null,
    string? ChapterText = null
);

/// <summary>
/// A page of novel listings returned by a browse/search request.
/// </summary>
public record ListingPage(
    List<NovelEntry> Novels,
    bool HasNextPage,
    int CurrentPage
);

/// <summary>
/// Implement on adapters that support browsing/discovery.
/// </summary>
public interface IBrowsableAdapter
{
    /// <summary>Human-readable source name shown in the Discover tab.</summary>
    string SiteName { get; }

    /// <summary>Short description of the source content type.</summary>
    string Description { get; }

    /// <summary>Material Symbols codepoint to use as the source icon.</summary>
    string IconGlyph { get; }

    /// <summary>Whether this source requires Cloudflare bypass to browse.</summary>
    bool RequiresCfBypass { get; }

    /// <summary>
    /// The home/landing URL to open when the user taps the source card in the browser.
    /// Defaults to <see cref="GetRecentUrl"/> page 1 if not overridden.
    /// </summary>
    string HomeUrl => GetRecentUrl(1);

    /// <summary>URL for the "Recent" listing page (page 1).</summary>
    string GetRecentUrl(int page = 1);

    /// <summary>URL for the "Popular" listing page (page 1).</summary>
    string GetPopularUrl(int page = 1);

    /// <summary>URL for a search query.</summary>
    string GetSearchUrl(string query, int page = 1);

    /// <summary>Parse a listing/search HTML page into novel entries.</summary>
    ListingPage ParseListing(string html, string pageUrl);

    /// <summary>
    /// If non-null, the search should be performed via HTTP POST instead of GET.
    /// Returns (postBody, charset) where postBody is already URL-form-encoded and
    /// charset is the encoding name for the Content-Type header (e.g. "gb2312").
    /// </summary>
    (string postBody, string charset)? GetSearchPostBody(string query, int page = 1) => null;

    /// <summary>
    /// Optional list of category/ranking filters supported by this source.
    /// If null or empty, standard Recent/Popular pills are used.
    /// </summary>
    IReadOnlyList<SourceFilter>? Filters => null;
}

/// <summary>
/// A category or ranking filter option for a browsable novel source.
/// </summary>
public record SourceFilter(
    string Name,
    Func<int, string> UrlGenerator
);

/// <summary>
/// A per-source global search result including status information.
/// </summary>
public record SourceSearchResult(
    IBrowsableAdapter Source,
    ListingPage Results,
    bool IsSuccess,
    string? ErrorMessage
);
