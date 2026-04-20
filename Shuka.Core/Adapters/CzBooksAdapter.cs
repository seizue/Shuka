using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

// czbooks.net adapter
// Chapter URL format: https://czbooks.net/n/{bookId}/{chapterId}
// (The old ?chapterNumber= query string no longer exists)
public class CzBooksAdapter : ISiteAdapter
{
    public string SiteName => "czbooks.net";

    public bool Matches(string url) =>
        url.Contains("czbooks.net", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;
        // Strip any trailing chapter path — keep only /n/{bookId}
        var m = Regex.Match(url, @"(https?://czbooks\.net/n/[^/?#]+)(?:[/?#].*)?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        // ── Title ────────────────────────────────────────────────────────────
        string title = Regex.Match(html, @"《([^》]+)》").Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(html, @"<title[^>]*>([^<|]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(indexUrl, @"/n/([^/?#]+)").Groups[1].Value;

        // ── Author ───────────────────────────────────────────────────────────
        string author = "Unknown";
        var am = Regex.Match(html, @"作者[：:\s]*<[^>]+>([^<]+)<", RegexOptions.IgnoreCase);
        if (!am.Success) am = Regex.Match(html, @"作者[：:\s]+([^\s<\n,，]+)");
        if (am.Success) author = am.Groups[1].Value.Trim();

        // ── Chapter list ─────────────────────────────────────────────────────
        string bookId = Regex.Match(indexUrl, @"/n/([^/?#]+)").Groups[1].Value;

        var chapters = ParseChapterList(html, bookId);

        // ── Cover ────────────────────────────────────────────────────────────
        string? cover = null;
        var imgM = Regex.Match(html,
            @"<img[^>]+src=[""'](https?://(?:img\.)?czbooks\.net/[^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (imgM.Success) cover = imgM.Groups[1].Value;

        if (cover == null)
        {
            var ogM = Regex.Match(html,
                @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (!ogM.Success)
                ogM = Regex.Match(html,
                    @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
                    RegexOptions.IgnoreCase);
            if (ogM.Success) cover = ogM.Groups[1].Value.Trim();
        }

        return new IndexInfo(title, author, chapters, cover);
    }

    private static List<ChapterRef> ParseChapterList(string html, string bookId)
    {
        var results = new List<ChapterRef>();

        // ── Strategy 1: new URL format /n/{bookId}/{chapterId} ────────────────
        // Matches: href="/n/abc123/xyz456" or href="https://czbooks.net/n/abc123/xyz456"
        var pattern1 = new Regex(
            @"href=[""'](?:https?://czbooks\.net)?/n/" + Regex.Escape(bookId) + @"/([^""'/?#\s]+)[""']",
            RegexOptions.IgnoreCase);

        var matches1 = pattern1.Matches(html);
        if (matches1.Count > 0)
        {
            int num = 1;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in matches1)
            {
                string chapterId = m.Groups[1].Value;
                if (!seen.Add(chapterId)) continue;
                // Skip non-chapter IDs (e.g. "info", "comment", etc.)
                if (chapterId.Equals("info", StringComparison.OrdinalIgnoreCase)) continue;
                if (chapterId.Equals("comment", StringComparison.OrdinalIgnoreCase)) continue;

                string url = $"https://czbooks.net/n/{bookId}/{chapterId}";

                // Try to extract chapter title from surrounding <a> text
                string chTitle = TryExtractLinkTitle(html, m.Index) ?? $"Chapter {num}";
                results.Add(new ChapterRef(url, chTitle));
                num++;
            }
        }

        // ── Strategy 2: old ?chapterNumber= format (fallback) ────────────────
        if (results.Count == 0)
        {
            var pattern2 = new Regex(
                @"href=[""'][^""']*(/n/" + Regex.Escape(bookId) + @"/([^""'?]+))\?chapterNumber=(\d+)[""']",
                RegexOptions.IgnoreCase);

            results = pattern2.Matches(html)
                .Cast<Match>()
                .Select(m => new
                {
                    Url  = "https://czbooks.net" + m.Groups[1].Value,
                    Code = m.Groups[2].Value,
                    Num  = int.Parse(m.Groups[3].Value)
                })
                .DistinctBy(x => x.Code)
                .OrderBy(x => x.Num)
                .Select(x => new ChapterRef(x.Url, $"Chapter {x.Num}"))
                .ToList();
        }

        // ── Strategy 3: any /n/{bookId}/ link as last resort ─────────────────
        if (results.Count == 0)
        {
            var pattern3 = new Regex(
                @"href=[""'](?:https?://czbooks\.net)?(/n/" + Regex.Escape(bookId) + @"/[^""'/?#\s]+)[""']",
                RegexOptions.IgnoreCase);

            int num = 1;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in pattern3.Matches(html))
            {
                string path = m.Groups[1].Value;
                if (!seen.Add(path)) continue;
                results.Add(new ChapterRef("https://czbooks.net" + path, $"Chapter {num++}"));
            }
        }

        return results;
    }

    /// <summary>
    /// Tries to extract the visible text of the &lt;a&gt; tag at the given match position.
    /// </summary>
    private static string? TryExtractLinkTitle(string html, int hrefPos)
    {
        // Find the enclosing <a ...> tag start
        int tagStart = html.LastIndexOf('<', hrefPos);
        if (tagStart < 0) return null;

        // Find the closing >
        int tagEnd = html.IndexOf('>', hrefPos);
        if (tagEnd < 0) return null;

        // Find </a>
        int closeTag = html.IndexOf("</a>", tagEnd, StringComparison.OrdinalIgnoreCase);
        if (closeTag < 0) return null;

        string innerText = html.Substring(tagEnd + 1, closeTag - tagEnd - 1);
        // Strip any nested tags
        innerText = Regex.Replace(innerText, @"<[^>]+>", "").Trim();
        innerText = System.Net.WebUtility.HtmlDecode(innerText);

        return string.IsNullOrWhiteSpace(innerText) ? null : innerText;
    }

    public List<string> ExtractChapterText(string html)
    {
        // Remove noise
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>",   "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<nav[\s\S]*?</nav>",       "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<header[\s\S]*?</header>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<footer[\s\S]*?</footer>", "", RegexOptions.IgnoreCase);

        // Try known content containers in priority order
        string? content = null;

        // czbooks uses <div class="content"> or <div id="content">
        foreach (var pattern in new[]
        {
            @"<div[^>]+(?:id|class)=[""'][^""']*\bcontent\b[^""']*[""'][^>]*>([\s\S]*?)</div>",
            @"<article[^>]*>([\s\S]*?)</article>",
            @"<div[^>]+(?:id|class)=[""'][^""']*\bchapter\b[^""']*[""'][^>]*>([\s\S]*?)</div>",
            @"<div[^>]+(?:id|class)=[""'][^""']*\btext\b[^""']*[""'][^>]*>([\s\S]*?)</div>",
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

        // Strip remaining tags and decode entities
        content = Regex.Replace(content, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"<p[^>]*>",  "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"<[^>]+>",   "");
        content = System.Net.WebUtility.HtmlDecode(content);

        var result = new List<string>();
        foreach (var line in content.Split('\n'))
        {
            string trimmed = line.Trim().TrimStart('\u3000').Trim();
            // Keep lines that contain CJK characters (Chinese text)
            if (trimmed.Length > 0 && Regex.IsMatch(trimmed, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]"))
                result.Add(trimmed);
        }
        return result;
    }
}
