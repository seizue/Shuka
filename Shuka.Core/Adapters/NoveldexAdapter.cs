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
    public bool RequiresCfBypass => true;   // Next.js SPA — chapter content requires JS execution; WebView is needed.
    public bool StopOnFirstLockedChapter => true;  // Noveldex paywall is contiguous — once locked, all following are locked too.

    public bool Matches(string url) => IsNoveldexUrl(url);

    /// <summary>noveldex.io content is already in English — downloads should skip translation.</summary>
    public static bool IsNoveldexUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase);

    /// <summary>Default site-wide og:image — not a novel cover.</summary>
    public static bool IsSiteDefaultOgImage(string url) =>
        url.Contains("uploads/settings/ogImage", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decode /_next/image proxy URLs and normalise relative paths.</summary>
    public static string? NormalizeCoverUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        if (url.Contains("/_next/image", StringComparison.OrdinalIgnoreCase) &&
            url.Contains("url=", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(url, @"[?&]url=([^&]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string inner = Uri.UnescapeDataString(m.Groups[1].Value);
                if (inner.Contains('?')) inner = inner[..inner.IndexOf('?')];
                if (!string.IsNullOrWhiteSpace(inner)) url = inner;
            }
        }

        if (url.StartsWith("//")) url = "https:" + url;
        else if (url.StartsWith('/')) url = "https://noveldex.io" + url;

        return url;
    }

    /// <summary>Referer required by media.noveldex.io CDN hotlink checks.</summary>
    public static string? GetCoverReferer(string url) =>
        IsNoveldexUrl(url) || url.Contains("media.noveldex.io", StringComparison.OrdinalIgnoreCase)
            ? "https://noveldex.io/"
            : null;

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

        // Priority 0: og:image injected by WebView bypass with the real CDN URL
        var injectedOg = Regex.Match(html,
            @"<meta[^>]+data-shuka-injected=[""']1[""'][^>]+content=[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (!injectedOg.Success)
            injectedOg = Regex.Match(html,
                @"<meta[^>]+content=[""']([^""']+)[""'][^>]+data-shuka-injected=[""']1[""']",
                RegexOptions.IgnoreCase);
        if (injectedOg.Success)
            cover = NormalizeCoverUrl(injectedOg.Groups[1].Value.Trim());

        // Priority 1: Direct CDN URLs with "cover" in the path
        if (cover == null)
        {
            var coverImg = Regex.Match(html,
                @"(?:src|content)=[""'](https://media\.noveldex\.io/[^""']*cover[^""']*)[""']",
                RegexOptions.IgnoreCase);
            if (coverImg.Success) cover = NormalizeCoverUrl(coverImg.Groups[1].Value.Trim());
        }

        // Priority 2: Next.js /_next/image proxy — prefer decoded URLs containing "cover"
        if (cover == null)
        {
            foreach (Match nextImg in Regex.Matches(html,
                         @"src=[""']((?:https://noveldex\.io)?/_next/image\?url=([^""']+?)(?:&amp;|&)[^""']*)[""']",
                         RegexOptions.IgnoreCase))
            {
                string decoded = Uri.UnescapeDataString(nextImg.Groups[2].Value.Trim());
                if (decoded.Contains('?')) decoded = decoded[..decoded.IndexOf('?')];
                if (decoded.Contains("cover", StringComparison.OrdinalIgnoreCase) && decoded.StartsWith("http"))
                {
                    cover = decoded;
                    break;
                }
            }
        }

        // Priority 3: Any /_next/image proxy pointing at media.noveldex.io
        if (cover == null)
        {
            var nextImg = Regex.Match(html,
                @"src=[""']((?:https://noveldex\.io)?/_next/image\?url=([^""'&]+)[^""']*)[""']",
                RegexOptions.IgnoreCase);
            if (!nextImg.Success)
                nextImg = Regex.Match(html,
                    @"src=[""']((?:https://noveldex\.io)?/_next/image\?url=([^""']+?)(?:&amp;|&)[^""']*)[""']",
                    RegexOptions.IgnoreCase);
            if (nextImg.Success)
            {
                string rawParam = nextImg.Groups[2].Value.Trim();
                string decoded  = Uri.UnescapeDataString(rawParam);
                if (decoded.Contains('?')) decoded = decoded[..decoded.IndexOf('?')];
                if (decoded.StartsWith("http"))
                    cover = decoded;
                else if (nextImg.Groups[1].Value.StartsWith("http"))
                    cover = NormalizeCoverUrl(nextImg.Groups[1].Value);
            }
        }

        // Priority 4: __NEXT_DATA__ JSON — prefer URLs with "cover" in the path
        if (cover == null)
        {
            var coverNextDataM = Regex.Match(html,
                @"<script[^>]+id=[""']__NEXT_DATA__[""'][^>]*>\s*(\{[\s\S]*?\})\s*</script>",
                RegexOptions.IgnoreCase);
            if (coverNextDataM.Success)
            {
                string json = coverNextDataM.Groups[1].Value;
                var coverCdn = Regex.Match(json,
                    @"""(https://media\.noveldex\.io/[^""']*cover[^""']*)""",
                    RegexOptions.IgnoreCase);
                if (coverCdn.Success)
                    cover = coverCdn.Groups[1].Value;
                else
                {
                    var cdnMatch = Regex.Match(json, @"""(https://media\.noveldex\.io/[^""]+)""");
                    if (cdnMatch.Success) cover = cdnMatch.Groups[1].Value;
                }
            }
        }

        // Priority 5: og:image meta tag (skip the site-wide default logo)
        if (cover == null)
        {
            var ogImg = Regex.Match(html,
                @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (!ogImg.Success)
                ogImg = Regex.Match(html,
                    @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
                    RegexOptions.IgnoreCase);
            if (ogImg.Success)
            {
                string og = ogImg.Groups[1].Value.Trim();
                if (!IsSiteDefaultOgImage(og))
                    cover = NormalizeCoverUrl(og);
            }
        }

        // Priority 6: Any CDN series image that explicitly contains "cover"
        if (cover == null)
        {
            var mediaImg = Regex.Match(html,
                @"src=[""'](https://media\.noveldex\.io/series/[^""']*cover[^""']*)[""']",
                RegexOptions.IgnoreCase);
            if (mediaImg.Success) cover = mediaImg.Groups[1].Value.Trim();
        }

        cover = NormalizeCoverUrl(cover);

        return new IndexInfo(title, author, chapters, cover);
    }

    /// <summary>
    /// Checks if the page HTML or visible body text indicates a paywalled/locked chapter on noveldex.io.
    /// </summary>
    public static bool IsNoveldexPaywalled(string htmlOrText)
    {
        if (string.IsNullOrWhiteSpace(htmlOrText)) return false;

        if (htmlOrText.Contains("Unlock to continue reading", StringComparison.OrdinalIgnoreCase) ||
            htmlOrText.Contains("coinsSign in to Unlock",     StringComparison.OrdinalIgnoreCase) ||
            htmlOrText.Contains("Sign in to Unlock",          StringComparison.OrdinalIgnoreCase) ||
            htmlOrText.Contains("Unlock Chapter",             StringComparison.OrdinalIgnoreCase) ||
            htmlOrText.Contains("Unlock this chapter",        StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Proximity check: "unlock" and "coins" within 40 chars of each other
        if (Regex.IsMatch(htmlOrText, @"\bunlock\b.{1,40}\bcoins?\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(htmlOrText, @"\bcoins?\b.{1,40}\bunlock\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        return false;
    }

    public List<string> ExtractChapterText(string html)
    {
        var result = new List<string>();

        // ── Paywall / locked-chapter detection ─────────────────────────────────
        // noveldex.io locked chapters show "Unlock to continue reading — N coins"
        // instead of actual content. Return empty so the caller skips this chapter
        // rather than saving coin-paywall boilerplate as chapter text.
        if (IsNoveldexPaywalled(html)) return result; // empty — chapter is paywalled

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
