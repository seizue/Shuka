using System.Net;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Site adapter for noveldex.io — an English web-novel aggregator.
///
/// Novel index URL : https://noveldex.io/series/novel/{slug}
/// Chapter URL     : https://noveldex.io/series/novel/{slug}/chapter/{num}
///
/// The site is a Next.js SPA; pages are server-side rendered so the raw HTML
/// still contains the content, but it must be loaded through the WebView to get
/// the full rendered DOM.  No Cloudflare protection, but JS is required.
/// Content is already in English — no translation needed.
/// </summary>
public class NoveldexAdapter : ISiteAdapter
{
    public string SiteName => "noveldex.io";
    public bool RequiresCfBypass => true;   // JS-rendered Next.js SPA — needs WebView

    public bool Matches(string url) =>
        url.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;

        // Strip ?ref=... or any other query/fragment suffixes first
        url = Regex.Replace(url, @"[?#].*$", "");

        // If pasted a chapter URL, redirect to the series index
        // e.g. /series/novel/{slug}/chapter/{num}  →  /series/novel/{slug}
        var chapterM = Regex.Match(url,
            @"(https?://noveldex\.io/series/[^/]+/[^/]+)/chapter/\d+",
            RegexOptions.IgnoreCase);
        if (chapterM.Success) return chapterM.Groups[1].Value;

        return url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        string slug = Regex.Match(indexUrl,
            @"/series/(?:[^/]+/)*([^/?#]+)/?$", RegexOptions.IgnoreCase).Groups[1].Value;

        // Strip the noscript block first — it contains a duplicate "JavaScript Required" h1
        // that would shadow the real title if matched first.
        string cleanHtml = Regex.Replace(html, @"<noscript[\s\S]*?</noscript>", "", RegexOptions.IgnoreCase);
        cleanHtml = Regex.Replace(cleanHtml, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        cleanHtml = Regex.Replace(cleanHtml, @"<style[\s\S]*?</style>",  "", RegexOptions.IgnoreCase);

        // ── Title ────────────────────────────────────────────────────────────
        // Rendered as <h1 ...>Title</h1> on the series page
        string title = "";
        var h1 = Regex.Match(cleanHtml, @"<h1[^>]*>\s*([^<]+?)\s*</h1>", RegexOptions.IgnoreCase);
        if (h1.Success) title = WebUtility.HtmlDecode(h1.Groups[1].Value.Trim());

        if (string.IsNullOrWhiteSpace(title))
        {
            // og:title fallback
            var ogT = Regex.Match(html,
                @"<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (!ogT.Success)
                ogT = Regex.Match(html,
                    @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:title[""']",
                    RegexOptions.IgnoreCase);
            if (ogT.Success) title = WebUtility.HtmlDecode(ogT.Groups[1].Value.Trim());
        }

        if (string.IsNullOrWhiteSpace(title))
            title = slug.Replace('-', ' ');

        // ── Author / Translation group ────────────────────────────────────────
        // noveldex.io shows the translation team name, not the original author.
        // We capture it as the "author" since it's the most available metadata.
        string author = "Unknown";
        // <a href="/team/...">Team Name</a>
        var teamM = Regex.Match(cleanHtml,
            @"<a[^>]+href=""/team/[^""]+""[^>]*>\s*([^<]+?)\s*</a>",
            RegexOptions.IgnoreCase);
        if (teamM.Success)
            author = WebUtility.HtmlDecode(teamM.Groups[1].Value.Trim());

        // ── Chapter list ──────────────────────────────────────────────────────
        // Read total count from __NEXT_DATA__ JSON or page headers/links, and synthesise all URLs 1..N.
        int totalChapters = 0;

        // Strategy 1: Check __NEXT_DATA__ JSON
        var nextDataM = Regex.Match(html,
            @"<script[^>]+id=[""']__NEXT_DATA__[""'][^>]*>\s*(\{[\s\S]*?\})\s*</script>",
            RegexOptions.IgnoreCase);
        if (nextDataM.Success)
        {
            string json = nextDataM.Groups[1].Value;
            var jsonM = Regex.Match(json, @"""(?:totalChapters|chaptersCount|chapterCount|chapters_count|total_chapters|total|count)""\s*:\s*(\d+)");
            if (jsonM.Success && int.TryParse(jsonM.Groups[1].Value, out int jsonCount) && jsonCount > 0)
            {
                totalChapters = jsonCount;
            }

            // globalLastChapter is a reliable field that contains the true total even
            // when the chapter list is paginated (the page only renders 100 at a time).
            var globalLastM = Regex.Match(json, @"""globalLastChapter""\s*:\s*(\d+)");
            if (globalLastM.Success && int.TryParse(globalLastM.Groups[1].Value, out int globalLast) && globalLast > totalChapters)
                totalChapters = globalLast;

            // Also check for highest chapter number in JSON
            foreach (Match m in Regex.Matches(json, @"""(?:number|chapter_number|chapterNum)""\s*:\s*(\d+)"))
            {
                if (int.TryParse(m.Groups[1].Value, out int c) && c > totalChapters)
                    totalChapters = c;
            }
        }

        // Strategy 1b: globalLastChapter in full HTML (React Flight / RSC payload).
        // This field is injected by noveldex.io even when __NEXT_DATA__ is absent.
        // It always reflects the real total chapter count regardless of pagination.
        {
            var globalM = Regex.Match(html, @"globalLastChapter[^:]{0,10}:\s*(\d+)");
            if (globalM.Success && int.TryParse(globalM.Groups[1].Value, out int globalLast) && globalLast > totalChapters)
                totalChapters = globalLast;
        }

        // Strategy 2: Find max chapter number from any /chapter/(\d+) links in HTML
        foreach (Match m in Regex.Matches(html, @"/chapter/(\d+)", RegexOptions.IgnoreCase))
        {
            if (int.TryParse(m.Groups[1].Value, out int c) && c > totalChapters)
                totalChapters = c;
        }

        // Strategy 3: Explicit "Chapters (N)", "Total Chapters: N", or "N Chapters" in HTML
        if (totalChapters == 0)
        {
            foreach (Match m in Regex.Matches(cleanHtml, @"(\d+)\s*Chapters?|Chapters?\s*\(?\s*(\d+)\s*\)?", RegexOptions.IgnoreCase))
            {
                string numStr = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (int.TryParse(numStr, out int c) && c > totalChapters)
                    totalChapters = c;
            }
        }

        // Scrape chapter titles from the visible links
        var chapterTitles = new Dictionary<int, string>();
        var titlePattern = new Regex(
            @"href=[""']/series/(?:[^/""']+/)*" + Regex.Escape(slug) +
            @"/chapter/(\d+)[""'][^>]*>\s*([^<]{2,200}?)\s*</a>",
            RegexOptions.IgnoreCase);

        foreach (Match m in titlePattern.Matches(html))
        {
            if (!int.TryParse(m.Groups[1].Value, out int num)) continue;
            if (chapterTitles.ContainsKey(num)) continue;
            string ct = WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
            ct = Regex.Replace(ct, @"^Chapter\s+\d+\s+Chapter\s+\d+\s*[-–—]\s*", "", RegexOptions.IgnoreCase);
            ct = Regex.Replace(ct, @"^Chapter\s+\d+\s*[-–—]\s*", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(ct))
                chapterTitles[num] = ct;
        }

        // Ensure totalChapters is at least as large as the highest scraped chapter index
        int maxScraped = chapterTitles.Count > 0 ? chapterTitles.Keys.Max() : 0;
        totalChapters = Math.Max(totalChapters, maxScraped);

        // Build the full chapter list: synthesise URLs for all 1..totalChapters
        string baseChapterUrl = indexUrl.TrimEnd('/') + "/chapter/";
        var chapters = Enumerable.Range(1, totalChapters)
            .Select(n =>
            {
                string chTitle = chapterTitles.TryGetValue(n, out string? t) && !string.IsNullOrWhiteSpace(t)
                    ? t
                    : $"Chapter {n}";
                return new ChapterRef(baseChapterUrl + n, chTitle);
            })
            .ToList();

        // ── Cover ─────────────────────────────────────────────────────────────
        string? cover = null;

        // Priority 1: Look for cover images specifically (most reliable)
        var coverImg = Regex.Match(html,
            @"src=[""'](https://media\.noveldex\.io/[^""']*cover[^""']*)[""']",
            RegexOptions.IgnoreCase);
        if (coverImg.Success) cover = coverImg.Groups[1].Value.Trim();

        // Priority 2: Any media.noveldex.io CDN URLs
        if (cover == null)
        {
            var mediaImg = Regex.Match(html,
                @"src=[""'](https://media\.noveldex\.io/series/[^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (mediaImg.Success) cover = mediaImg.Groups[1].Value.Trim();
        }

        // Priority 3: og:image meta tag
        if (cover == null)
        {
            var ogImg = Regex.Match(html,
                @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (!ogImg.Success)
                ogImg = Regex.Match(html,
                    @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
                    RegexOptions.IgnoreCase);
            if (ogImg.Success) cover = ogImg.Groups[1].Value.Trim();
        }

        // Priority 4: Next.js image component - decode the inner url parameter
        if (cover == null)
        {
            var nextImg = Regex.Match(html,
                @"src=[""'](https://noveldex\.io/_next/image\?url=([^""'&]+)[^""']*)[""']",
                RegexOptions.IgnoreCase);
            if (nextImg.Success)
            {
                // Decode the inner url= value to get the real CDN URL
                string decoded = Uri.UnescapeDataString(nextImg.Groups[2].Value);
                // Strip any query parameters from the decoded URL to get original quality
                if (decoded.Contains('?'))
                    decoded = decoded.Substring(0, decoded.IndexOf('?'));
                cover = decoded.StartsWith("http") ? decoded : nextImg.Groups[1].Value;
            }
        }

        // Priority 5: Extract from __NEXT_DATA__ JSON (dynamic data)
        if (cover == null)
        {
            var coverNextDataM = Regex.Match(html,
                @"<script[^>]+id=[""']__NEXT_DATA__[""'][^>]*>\s*(\{[\s\S]*?\})\s*</script>",
                RegexOptions.IgnoreCase);
            if (coverNextDataM.Success)
            {
                string json = coverNextDataM.Groups[1].Value;
                // Look for media.noveldex.io URLs in the JSON
                var cdnMatch = Regex.Match(json, @"""(https://media\.noveldex\.io/[^""]+)""");
                if (cdnMatch.Success) cover = cdnMatch.Groups[1].Value;
            }
        }

        return new IndexInfo(title, author, chapters, cover);
    }

    public List<string> ExtractChapterText(string html)
    {
        var result = new List<string>();

        // ── Paywall / locked-chapter detection ─────────────────────────────────
        // noveldex.io locked chapters show "Unlock to continue reading — N coins"
        // instead of actual content. Return empty so the caller skips this chapter
        // rather than saving coin-paywall boilerplate as chapter text.
        bool isPaywalled =
            html.Contains("Unlock to continue reading", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("coinsSign in to Unlock",     StringComparison.OrdinalIgnoreCase) ||
            html.Contains("Sign in to Unlock",          StringComparison.OrdinalIgnoreCase) ||
            (html.Contains("coins",            StringComparison.OrdinalIgnoreCase) &&
             html.Contains("Unlock",           StringComparison.OrdinalIgnoreCase) &&
             html.Contains("permanent access", StringComparison.OrdinalIgnoreCase));

        if (isPaywalled) return result; // empty — chapter is paywalled

        // ── Strategy 1: Paragraph (<p>) tags from rendered DOM ─────────────────
        foreach (Match pm in Regex.Matches(html, @"<p[^>]*>([\s\S]+?)</p>", RegexOptions.IgnoreCase))
        {
            string raw = pm.Groups[1].Value;
            if (raw.Contains("<img", StringComparison.OrdinalIgnoreCase)) continue;
            string text = Regex.Replace(raw, @"<[^>]+>", "").Trim();
            text = System.Net.WebUtility.HtmlDecode(text);
            if (text.Length >= 10 &&
                !Regex.IsMatch(text, @"^(?:JavaScript|Cookies|Privacy|Terms|Copyright|Unlock|Sign in|Please enable)", RegexOptions.IgnoreCase) &&
                !text.Contains("permanent access", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Unlocking grants",  StringComparison.OrdinalIgnoreCase))
                result.Add(text);
        }
        if (result.Count > 0) return result;

        // ── Strategy 2: __NEXT_DATA__ JSON (reliable, present before hydration) ──
        var nextDataM = Regex.Match(html,
            @"<script[^>]+id=[""']__NEXT_DATA__[""'][^>]*>\s*(\{[\s\S]*?\})\s*</script>",
            RegexOptions.IgnoreCase);
        if (nextDataM.Success)
        {
            var fromJson = ExtractFromNextData(nextDataM.Groups[1].Value);
            if (fromJson.Count > 0) return fromJson;
        }

        // ── Strategy 3: hydrated DOM fallback ─────────────────────────────────
        if (html.Contains("JavaScript is required to view this content",
                StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("</p>", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>();
        }

        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>",   "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<nav[\s\S]*?</nav>",       "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<header[\s\S]*?</header>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<footer[\s\S]*?</footer>", "", RegexOptions.IgnoreCase);

        string? content = null;
        foreach (string pattern in new[]
        {
            @"<div[^>]+class=[""'][^""']*\bprose\b[^""']*[""'][^>]*>([\s\S]+?)</div>\s*</div>",
            @"<article[^>]*>([\s\S]+?)</article>",
            @"<div[^>]+class=[""'][^""']*\bchapter(?:-content|-body)?\b[^""']*[""'][^>]*>([\s\S]+?)</div>\s*</div>",
            @"<div[^>]+class=[""'][^""']*\bcontent\b[^""']*[""'][^>]*>([\s\S]+?)</div>\s*</div>",
            @"<div[^>]+id=[""']content[""'][^>]*>([\s\S]+?)</div>",
        })
        {
            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Length > 200)
            {
                content = m.Groups[1].Value;
                break;
            }
        }

        content ??= html;
        content = Regex.Replace(content, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"<p[^>]*>",  "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"<[^>]+>",   "");
        content = System.Net.WebUtility.HtmlDecode(content);

        var lines = new List<string>();
        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length < 3) continue;
            if (Regex.IsMatch(trimmed, @"^(?:prev(?:ious)?|next|←|→|\d+\s*/\s*\d+)$", RegexOptions.IgnoreCase))
                continue;
            lines.Add(trimmed);
        }
        return lines;
    }

    /// <summary>
    /// Extracts chapter text paragraphs from the Next.js <c>__NEXT_DATA__</c> JSON blob.
    /// Walks the entire JSON looking for the longest string value that looks like
    /// chapter content (HTML or plain text with multiple lines / long sentences).
    /// </summary>
    private static List<string> ExtractFromNextData(string json)
    {
        var result = new List<string>();

        // Find all string values in the JSON — look for the one that contains
        // paragraph content. It will have <p> tags or \n-separated lines.
        // Regex finds JSON string values (handles basic escape sequences).
        string best = "";
        foreach (Match m in Regex.Matches(json,
            @"""((?:[^""\\]|\\[\s\S]){200,})"""))   // strings longer than 200 chars
        {
            string val = Regex.Unescape(m.Groups[1].Value);
            // Prefer values that contain multiple sentence-like chunks
            if (val.Length > best.Length &&
                (val.Contains("<p") || val.Contains("\\n") || val.Split('\n').Length > 3))
            {
                best = val;
            }
        }

        if (string.IsNullOrWhiteSpace(best)) return result;

        // best may be HTML or plain text with \n separators
        if (best.Contains("<p"))
        {
            // Strip HTML and split on <p>/<br>
            best = Regex.Replace(best, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            best = Regex.Replace(best, @"<p[^>]*>",  "\n", RegexOptions.IgnoreCase);
            best = Regex.Replace(best, @"<[^>]+>",   "");
            best = System.Net.WebUtility.HtmlDecode(best);
        }

        foreach (string line in best.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length < 10) continue;
            if (Regex.IsMatch(trimmed, @"^(?:prev(?:ious)?|next|←|→|\d+\s*/\s*\d+)$", RegexOptions.IgnoreCase))
                continue;
            result.Add(trimmed);
        }

        return result;
    }
}
