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

        // If the page contains real novel content or catalog links AND does not have
        // explicit challenge page markers, it is definitely a valid page.
        if (html.Length > 2000 &&
            !html.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("<title>Attention Required!", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("id=\"challenge-form\"", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("id=\"challenge-running\"", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("id=\"challenge-stage\"", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("id=\"challenge-error-text\"", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("cf-chl-opt", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("cf_chl_", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase) &&
            (html.Contains("txtnav", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("bookname", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("mybox", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("yuedu", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("href=\"/txt/", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("href='/txt/", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("href=\"/book/", StringComparison.OrdinalIgnoreCase) ||
             (html.Contains("<h1", StringComparison.OrdinalIgnoreCase) && html.Contains("chapter", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        // True Cloudflare challenge / Turnstile indicators only.
        // NOTE: Never check generic strings like "challenge-platform" (Cloudflare's
        // standard analytics beacon /cdn-cgi/challenge-platform/scripts/jsd/main.js
        // is injected on EVERY legitimate Cloudflare page) or bare "/txt/" (reflected in URL params).
        return html.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("<title>Attention Required!", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("id=\"challenge-form\"", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("id=\"challenge-running\"", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("id=\"challenge-stage\"", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("id=\"challenge-error-text\"", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-chl-opt", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf_chl_", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("challenges.cloudflare.com/turnstile", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("Performing security verification", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("verifies you are not a bot", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("protect against malicious bots", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase) ||
               (html.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) && html.Contains("checking your browser", StringComparison.OrdinalIgnoreCase));
    }
}

