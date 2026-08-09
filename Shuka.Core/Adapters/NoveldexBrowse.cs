using System.Net;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for noveldex.io — an English web-novel aggregator.
///
/// noveldex.io is a Next.js SPA. The listing pages are server-side rendered
/// so content is still in the raw HTML, but the site requires a real browser
/// (WebView) for full interaction.  The URLs below are used both for HTTP
/// fetching (best-effort ParseListing) and for WebView direct browsing.
///
/// Browse (recent):  https://noveldex.io/series?type=novel&amp;sort=recently-updated
/// Browse (popular): https://noveldex.io/series?type=novel&amp;sort=most-bookmarked
/// Search:           https://noveldex.io/series?type=novel&amp;q={query}
///
/// The series listing uses infinite scroll — pagination is cursor-based in
/// the API, not reflected in the HTML URL.  We treat the browse page as a
/// single-page result and set HasNextPage = false.
/// </summary>
public class NoveldexBrowse : IBrowsableAdapter
{
    public string SiteName         => "noveldex.io";
    public string Description      => "English web novels · Korean & Japanese";
    public string IconGlyph        => "\uE894"; // language (globe) — matching other sources
    public bool   RequiresCfBypass => false;

    private const string AllTypes = "type=Light+Novel%2CWeb+Novel%2CPublished+Novel%2COriginal+Fiction%2COne+Shot%2CFanfiction%2CNovel";

    // The ?ref=browse param is stripped on normalisation so we don't include it.
    public string HomeUrl => $"https://noveldex.io/series?{AllTypes}&sort=recently-updated";

    public string GetRecentUrl(int page = 1) =>
        $"https://noveldex.io/series?{AllTypes}&sort=recently-updated";

    public string GetPopularUrl(int page = 1) =>
        $"https://noveldex.io/series?{AllTypes}&sort=most-bookmarked";

    public string GetSearchUrl(string query, int page = 1) =>
        $"https://noveldex.io/series?{AllTypes}&q={Uri.EscapeDataString(query)}";

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Detect if the site is blocking the request (Cloudflare/bot challenge page).
        // Use specific patterns that only appear on actual block pages, not valid listing HTML.
        bool isBlocked =
            html.Length < 300 ||
            html.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("JavaScript is required to view this page", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("suspicious activity", StringComparison.OrdinalIgnoreCase) ||
            // Specific CF challenge patterns (not just the word "challenge"):
            html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("cf_chl_", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("just a moment", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("checking your browser", StringComparison.OrdinalIgnoreCase) ||
            // Cloudflare ray ID is only present on error/block pages:
            (html.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) &&
             html.Contains("Ray ID", StringComparison.OrdinalIgnoreCase));

        if (isBlocked)
        {
            // Return empty results to trigger WebView-only state in SourceBrowsePage
            return new ListingPage(novels, false, 1);
        }

        // Series card links: href="/series/{type}/{slug}?ref=browse"  or  "/series/{type}/{slug}"
        // Each card has the novel URL, title, and optionally rating + chapter links nearby.
        var linkPattern = new Regex(
            @"href=[""'](/series/([^/?""']+)/([a-z0-9-]+))(?:\?[^""']*)?[""']",
            RegexOptions.IgnoreCase);

        foreach (Match m in linkPattern.Matches(html))
        {
            string path     = m.Groups[1].Value;
            string category = m.Groups[2].Value;
            string slug     = m.Groups[3].Value;

            // Skip paths that are actually chapter URLs or non-series paths
            if (path.Contains("/chapter/") || string.Equals(category, "chapter", StringComparison.OrdinalIgnoreCase))
                continue;

            string url = "https://noveldex.io" + path;
            if (!seen.Add(slug)) continue;

            // Grab context around this link for metadata extraction (expanded window to catch <img> tags before <a>)
            int start  = Math.Max(0, m.Index - 1200);
            int end    = Math.Min(html.Length, m.Index + 1200);
            string ctx = html.Substring(start, end - start);

            // ── Title ─────────────────────────────────────────────────────────
            string title = "";
            // The card title is a second <a> with the same href and the text directly
            // inside (or inside a nested span).
            var titleM = Regex.Match(ctx,
                @"href=[""']/series/(?:[^/""']+/)*" + Regex.Escape(slug) + @"(?:\?[^""']*)?[""'][^>]*>\s*([^<]{2,150}?)\s*</a>",
                RegexOptions.IgnoreCase);
            if (titleM.Success)
                title = WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim());

            // Fallback: <h2> or <h3> near the link
            if (string.IsNullOrWhiteSpace(title))
            {
                var hM = Regex.Match(ctx,
                    @"<h[2-4][^>]*>\s*([^<]{2,150})\s*</h[2-4]>", RegexOptions.IgnoreCase);
                if (hM.Success) title = WebUtility.HtmlDecode(hM.Groups[1].Value.Trim());
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                // Last resort: humanise the slug
                title = System.Globalization.CultureInfo.CurrentCulture.TextInfo
                    .ToTitleCase(slug.Replace('-', ' '));
            }

            // ── Cover ─────────────────────────────────────────────────────────
            string? cover = null;

            // Strategy 1: Look for _next/image?url= in src, srcset, or data-src
            var nextImgM = Regex.Match(ctx,
                @"(?:src|srcset|data-src)=[""']?(?:https?://noveldex\.io)?/_next/image\?url=([^""'&\s>]+)",
                RegexOptions.IgnoreCase);
            if (nextImgM.Success)
            {
                string rawUrl = WebUtility.UrlDecode(nextImgM.Groups[1].Value);
                if (rawUrl.StartsWith("//")) rawUrl = "https:" + rawUrl;
                else if (rawUrl.StartsWith("/")) rawUrl = "https://noveldex.io" + rawUrl;

                if (Uri.IsWellFormedUriString(rawUrl, UriKind.Absolute))
                {
                    cover = rawUrl;
                }
            }

            // Strategy 2: Look for media.noveldex.io in context
            if (cover == null)
            {
                var mediaM = Regex.Match(ctx,
                    @"(?:https?:)?//media\.noveldex\.io/[^""'&\s>]+",
                    RegexOptions.IgnoreCase);
                if (mediaM.Success)
                {
                    string mediaUrl = mediaM.Value;
                    if (mediaUrl.StartsWith("//")) mediaUrl = "https:" + mediaUrl;
                    cover = mediaUrl;
                }
            }

            // Strategy 3: General image URL in context
            if (cover == null)
            {
                var generalImgM = Regex.Match(ctx,
                    @"(?:src|srcset|data-src)=[""']?(https?://[^""'\s>]+\.(?:jpg|jpeg|png|webp))",
                    RegexOptions.IgnoreCase);
                if (generalImgM.Success)
                {
                    cover = generalImgM.Groups[1].Value;
                }
            }

            // ── Chapter count ─────────────────────────────────────────────────
            // Card shows "Ch. NNN" labels in the recent-chapter links
            int? chapterCount = null;
            string? chapterText = null;
            var chM = Regex.Match(ctx, @"\bCh\.?\s*(\d+)\b", RegexOptions.IgnoreCase);
            if (chM.Success && int.TryParse(chM.Groups[1].Value, out int chNum) && chNum > 0)
            {
                chapterCount = chNum;
                chapterText  = $"{chNum} ch";
            }

            // ── Tags / Type ───────────────────────────────────────────────────
            string? tags = null;
            if (!string.IsNullOrWhiteSpace(category))
            {
                tags = System.Globalization.CultureInfo.CurrentCulture.TextInfo
                    .ToTitleCase(category.Replace('-', ' '));
            }

            novels.Add(new NovelEntry(title, null, url, cover, null, tags, chapterCount, chapterText));
        }

        // noveldex uses infinite scroll — no meaningful next-page URL in the HTML
        return new ListingPage(novels, false, 1);
    }
}
