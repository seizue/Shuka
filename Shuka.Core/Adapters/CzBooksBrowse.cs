using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for czbooks.net.
///
/// czbooks.net is behind Cloudflare and blocks all HTTP fetches (403).
/// It works fine in a real browser/WebView. The URLs below are used by
/// WebBrowsePage to open the site — ParseListing is a best-effort fallback
/// but will typically return empty (the WebView handles browsing directly).
///
/// Recent:  https://czbooks.net/new/1
/// Popular: https://czbooks.net/hot/1
/// Search:  https://czbooks.net/search?q={query}
/// </summary>
public class CzBooksBrowse : IBrowsableAdapter
{
    public string SiteName => "czbooks.net";
    public string Description => "Chinese novels · Cloudflare protected";
    public string IconGlyph => "\uE894"; // language (globe)
    public bool RequiresCfBypass => true;

    public string GetRecentUrl(int page = 1) => "https://czbooks.net/";
    public string GetPopularUrl(int page = 1) => "https://czbooks.net/hot/1";
    public string GetSearchUrl(string query, int page = 1) =>
        $"https://czbooks.net/s/{Uri.EscapeDataString(query)}/{page}";

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // czbooks book links: /n/{bookId}  (no sub-path)
        // Chapter links look like /n/{bookId}/{chapterId} and won't match because
        // the regex requires a quote character directly after the alphanumeric bookId.
        var linkPattern = new Regex(
            @"href=[""'](?:https?://czbooks\.net)?/n/([a-zA-Z0-9]+)[""']",
            RegexOptions.IgnoreCase);

        foreach (Match m in linkPattern.Matches(html))
        {
            string bookId = m.Groups[1].Value;
            if (bookId.Length < 3) continue; // skip noise
            if (!seen.Add(bookId)) continue;

            // Look forward from the link position for card content (title, author, etc.)
            int fwdEnd = Math.Min(html.Length, m.Index + 700);
            string fwd = html.Substring(m.Index, fwdEnd - m.Index);

            // Also grab a small backward window for cover images that precede the link
            int bwdStart = Math.Max(0, m.Index - 150);
            string ctx = html.Substring(bwdStart, fwdEnd - bwdStart);

            // ── Title ─────────────────────────────────────────────────────────────
            string title = "";
            var titleM = Regex.Match(fwd,
                @"novel-item-title[^>]*>\s*([^<]{2,80})", RegexOptions.IgnoreCase);
            if (titleM.Success)
                title = System.Net.WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim());

            if (string.IsNullOrWhiteSpace(title))
            {
                var hM = Regex.Match(fwd,
                    @"<h[1-6][^>]*>\s*([^<]{2,80})\s*</h[1-6]>", RegexOptions.IgnoreCase);
                if (hM.Success) title = System.Net.WebUtility.HtmlDecode(hM.Groups[1].Value.Trim());
            }
            if (string.IsNullOrWhiteSpace(title)) continue;

            // ── Author ────────────────────────────────────────────────────────────
            string? author = null;
            var authM = Regex.Match(fwd,
                @"novel-item-author[^>]*>[\s\S]{0,60}?<a[^>]*>([^<]+)</a>",
                RegexOptions.IgnoreCase);
            if (authM.Success)
                author = System.Net.WebUtility.HtmlDecode(authM.Groups[1].Value.Trim());
            if (string.IsNullOrWhiteSpace(author))
            {
                authM = Regex.Match(ctx, @"作者[：:\s]*([^\s<,，]{1,30})");
                if (authM.Success) author = authM.Groups[1].Value.Trim();
            }

            // ── Cover ─────────────────────────────────────────────────────────────
            string? cover = null;
            var imgM = Regex.Match(ctx,
                @"<img[^>]+src=[""'](https?://[^""']+)[""']", RegexOptions.IgnoreCase);
            if (imgM.Success) cover = imgM.Groups[1].Value;

            var chapterMeta = ExtractChapterMeta(ctx);
            novels.Add(new NovelEntry(
                title, author,
                $"https://czbooks.net/n/{bookId}",
                cover, null, null,
                chapterMeta.count, chapterMeta.text));
        }

        bool hasNext = html.Contains("下一頁") || html.Contains("下一页") ||
                       Regex.IsMatch(html, @"/s/[^/]+/\d+", RegexOptions.IgnoreCase);
        int currentPage = 1;
        var pageM = Regex.Match(pageUrl, @"/(\d+)$");
        if (pageM.Success) int.TryParse(pageM.Groups[1].Value, out currentPage);

        return new ListingPage(novels, hasNext && novels.Count > 0, currentPage);
    }

    private static (int? count, string? text) ExtractChapterMeta(string sample)
    {
        if (string.IsNullOrWhiteSpace(sample))
            return (null, null);

        var cn = Regex.Match(sample, @"(?:共|总)?\s*([0-9]{1,5})\s*章");
        if (cn.Success && int.TryParse(cn.Groups[1].Value, out int cnCount))
            return (cnCount, $"{cnCount} chapters");

        var en = Regex.Match(sample, @"\b([0-9]{1,5})\s*chapters?\b", RegexOptions.IgnoreCase);
        if (en.Success && int.TryParse(en.Groups[1].Value, out int enCount))
            return (enCount, $"{enCount} chapters");

        return (null, null);
    }
}
