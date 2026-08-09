using System.Net;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for yamibo.com (百合会).
///
/// 百合会 is the largest Chinese yuri/GL novel community — user-uploaded,
/// original works only (both original fiction and fan-fiction).
///
/// Novel list (recent):  https://www.yamibo.com/novel/list
///                       https://www.yamibo.com/novel/list?page=2&amp;per-page=50
/// Novel list (popular): https://www.yamibo.com/novel/list?sort=-viewCount
///                       https://www.yamibo.com/novel/list?sort=-viewCount&amp;page=2&amp;per-page=50
/// Search:               https://www.yamibo.com/novel/search?q={query}
///                       https://www.yamibo.com/novel/search?q={query}&amp;page=2&amp;per-page=50
///
/// No Cloudflare protection.
/// </summary>
public class YamiboBrowse : IBrowsableAdapter
{
    public string SiteName         => "yamibo.com";
    public string Description      => "百合会 · Yuri / GL original novels";
    public string IconGlyph        => "\uE894"; // language (globe)
    public bool   RequiresCfBypass => true;

    public string GetRecentUrl(int page = 1) =>
        page == 1
            ? "https://www.yamibo.com/novel/list"
            : $"https://www.yamibo.com/novel/list?page={page}&per-page=50";

    public string GetPopularUrl(int page = 1) =>
        page == 1
            ? "https://www.yamibo.com/novel/list?sort=-viewCount"
            : $"https://www.yamibo.com/novel/list?sort=-viewCount&page={page}&per-page=50";

    public string GetSearchUrl(string query, int page = 1)
    {
        string encoded = Uri.EscapeDataString(query);
        return page == 1
            ? $"https://www.yamibo.com/novel/search?q={encoded}"
            : $"https://www.yamibo.com/novel/search?q={encoded}&page={page}&per-page=50";
    }

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Find all /novel/{id} links (e.g. href="/novel/12345" or href="https://www.yamibo.com/novel/12345")
        var linkPat = new Regex(
            @"href=[""']?(?:https?:)?(?://(?:www\.)?yamibo\.com)?/novel/(\d+)[?#""'\s>]/?",
            RegexOptions.IgnoreCase);

        foreach (Match m in linkPat.Matches(html))
        {
            string novelId = m.Groups[1].Value;
            string url = $"https://www.yamibo.com/novel/{novelId}";
            if (!seen.Add(url)) continue;

            // Grab a window of text around this link to extract title/author/metadata
            int start = Math.Max(0, m.Index - 400);
            int length = Math.Min(html.Length - start, 800);
            string window = html.Substring(start, length);

            // Extract title: from <a href=".../novel/123">Title</a>
            string title = "";
            var titleMatch = Regex.Match(window,
                @"href=[""']?[^""'\s>]*?/novel/" + Regex.Escape(novelId) + @"[^""'\s>]*[""']?[^>]*>([\s\S]*?)</a>",
                RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                title = Regex.Replace(titleMatch.Groups[1].Value, @"<[^>]+>", "").Trim();
                title = WebUtility.HtmlDecode(title);
            }

            if (string.IsNullOrWhiteSpace(title) || title.Length < 2 || title == "小说" || title == "百合会")
                continue;

            // Extract author: <a href="/user/space?id={uid}">Author</a> or Author text
            string author = "Unknown";
            var authorMatch = Regex.Match(window,
                @"<a[^>]*href=[""']?/user/space\?id=\d+[""']?[^>]*>([\s\S]*?)</a>",
                RegexOptions.IgnoreCase);
            if (authorMatch.Success)
            {
                author = Regex.Replace(authorMatch.Groups[1].Value, @"<[^>]+>", "").Trim();
                author = WebUtility.HtmlDecode(author);
            }

            // Cover image follows predictable URL pattern from the novel ID
            string? cover = BuildCoverUrl(novelId);

            // Chapter count from td cells / context text
            int? chapterCount = null;
            string? chapterText = null;
            var ch = Regex.Match(window, @"([0-9]{1,5})\s*(?:章|chapters?)", RegexOptions.IgnoreCase);
            if (ch.Success && int.TryParse(ch.Groups[1].Value, out int chCount) && chCount > 0)
            {
                chapterCount = chCount;
                chapterText = $"{chCount} ch";
            }

            novels.Add(new NovelEntry(title, author, url, cover, null, null, chapterCount, chapterText));
        }

        // ── Pagination ────────────────────────────────────────────────────────────
        // <li class="next"><a href="/novel/list?page=2&per-page=50" data-page="1">下一页</a></li>
        bool hasNext = Regex.IsMatch(html,
            @"<li[^>]+class=""next""[^>]*>\s*<a\b",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

        // Derive current page from URL query string or pagination active element
        int currentPage = 1;
        var pageM = Regex.Match(pageUrl, @"[?&]page=(\d+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        if (pageM.Success) int.TryParse(pageM.Groups[1].Value, out currentPage);
        if (currentPage == 0) currentPage = 1;

        return new ListingPage(novels, hasNext && novels.Count > 0, currentPage);
    }

    /// <summary>
    /// Builds the cover URL from a numeric novel ID.
    /// Yamibo pads the ID into three-digit groups: 267137 → /covern/000/267/137.jpg
    /// </summary>
    private static string? BuildCoverUrl(string novelId)
    {
        if (!long.TryParse(novelId, out long id)) return null;

        // Split decimal ID into 3-digit groups (right to left), padded left with zeros
        // to yield the directory segments.  e.g.:
        //   267137  → segments ["267","137"]  → /covern/000/267/137.jpg
        //   12345   → segments ["012","345"]  → /covern/000/012/345.jpg
        //   999     → segments ["000","999"]  → /covern/000/000/999.jpg
        string padded = id.ToString("D6"); // at least 6 digits — typical IDs are 6 chars
        // Ensure length is a multiple of 3
        while (padded.Length % 3 != 0) padded = "0" + padded;

        var segments = new List<string>();
        for (int i = 0; i < padded.Length; i += 3)
            segments.Add(padded.Substring(i, 3));

        // The outermost prefix appears to always be "000" on the live site
        string path = "/covern/000/" + string.Join("/", segments) + ".jpg";
        return "https://www.yamibo.com" + path;
    }
}
