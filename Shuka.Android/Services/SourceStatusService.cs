using Shuka.Core;

namespace Shuka.Android.Services;

/// <summary>
/// Pings every Discover source once at app start and caches whether it is reachable.
/// A HEAD request with a short timeout is used so the check is fast and non-blocking.
/// </summary>
public sealed class SourceStatusService
{
    // ── Singleton ─────────────────────────────────────────────────────────────

    public static readonly SourceStatusService Instance = new();
    private SourceStatusService() { }

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, bool> _statusCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _checkStarted;
    private readonly object _lock = new();

    /// <summary>
    /// Fired on the main thread whenever one or more source statuses are updated.
    /// Subscribe to refresh the UI after the initial build.
    /// </summary>
    public event EventHandler? StatusUpdated;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the source is known to be down.
    /// Returns false when up or when the check has not completed yet.
    /// </summary>
    public bool IsDown(string siteName)
    {
        lock (_lock)
        {
            // Only report down when we have a definitive failure result
            return _statusCache.TryGetValue(siteName, out bool up) && !up;
        }
    }

    /// <summary>
    /// Returns true only when the status for this site has been resolved.
    /// </summary>
    public bool HasResult(string siteName)
    {
        lock (_lock)
        {
            return _statusCache.ContainsKey(siteName);
        }
    }

    /// <summary>
    /// Starts the background ping for all sources.
    /// Safe to call multiple times — only runs once per app session.
    /// </summary>
    public void StartChecksIfNeeded()
    {
        lock (_lock)
        {
            if (_checkStarted) return;
            _checkStarted = true;
        }

        _ = Task.Run(RunChecksAsync);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task RunChecksAsync()
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };

        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36");

        // Check all sources in parallel
        var tasks = DiscoverService.Sources.Select(source => CheckSourceAsync(http, source));
        await Task.WhenAll(tasks);
    }

    private async Task CheckSourceAsync(HttpClient http, IBrowsableAdapter source)
    {
        string url = source.HomeUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        bool isUp;
        try
        {
            // Try HEAD first (lightweight); fall back to GET if server rejects HEAD
            HttpResponseMessage response;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (HttpRequestException)
            {
                // Retry with GET in case the server doesn't support HEAD
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            }

            // 2xx / 3xx = reachable; anything else is treated as down
            isUp = (int)response.StatusCode < 500;
        }
        catch
        {
            // Timeout, DNS failure, connection refused, etc.
            isUp = false;
        }

        bool changed;
        lock (_lock)
        {
            bool hadPrevious = _statusCache.TryGetValue(source.SiteName, out bool prev);
            changed = !hadPrevious || prev != isUp;
            _statusCache[source.SiteName] = isUp;
        }

        if (changed)
        {
            // Fire on the main thread so subscribers can update UI safely
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusUpdated?.Invoke(this, EventArgs.Empty));
        }
    }
}
