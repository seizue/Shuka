namespace Shuka.Core;

/// <summary>
/// Centralized Cloudflare challenge, Turnstile, and bot protection detection.
/// </summary>
public static class CloudflareHelper
{
    /// <summary>
    /// Checks whether the supplied HTML or Latin-1 text is a Cloudflare challenge,
    /// Turnstile verification, or bot protection block page.
    /// </summary>
    public static bool IsChallenge(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;

        // If the page contains real novel content or chapter list, it is NOT a challenge page
        if (html.Length > 2000 &&
            (html.Contains("bookname", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("txtnav", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("mybox", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("/txt/", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("yuedu", StringComparison.OrdinalIgnoreCase) ||
             (html.Contains("<h1", StringComparison.OrdinalIgnoreCase) && html.Contains("chapter", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        // True Cloudflare challenge / Turnstile indicators
        return html.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("<title>Attention Required!", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("id=\"challenge-form\"", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("id=\"challenge-running\"", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-chl-opt", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf_chl_", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("Performing security verification", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("verifies you are not a bot", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("protect against malicious bots", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("CloudflarePrivacy", StringComparison.OrdinalIgnoreCase) ||
               (html.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) && html.Contains("checking your browser", StringComparison.OrdinalIgnoreCase)) ||
               (html.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) && html.Contains("Ray ID", StringComparison.OrdinalIgnoreCase));
    }
}

