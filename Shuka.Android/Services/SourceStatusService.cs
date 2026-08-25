using Shuka.Core;

namespace Shuka.Android.Services;

/// <summary>
/// Pings every Discover source and caches whether it is reachable.
/// A HEAD request with a short timeout is used so the check is fast and non-blocking.
///
/// Status lifecycle:
///   - Pending  : no result yet (HasResult = false)
///   - Online   : IsUp = true
///   - Down     : IsDown = true
///
/// The initial check runs once at app start (deferred after first frame).
/// Call <see cref="RefreshChecks"/> to force a re-check — it is rate-limited to
/// at most once every <see cref="RefreshCooldown"/> so it is safe to call on every
/// tab-appear event.
/// </summary>
public sealed class SourceStatusService
{
    // ── Singleton ─────────────────────────────────────────────────────────────

    public static readonly SourceStatusService Instance = new();
    private SourceStatusService() { }

    // ── Config ────────────────────────────────────────────────────────────────

    /// <summary>Minimum time between full re-check runs.</summary>
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(30);

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, bool> _statusCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _checkRunning;
    private DateTime _lastCheckStarted = DateTime.MinValue;
    private readonly object _lock = new();

    /// <summary>
    /// Fired on the main thread whenever one or more source statuses are updated.
    /// Subscribe to refresh the UI after the initial build.
    /// </summary>
    public event EventHandler? StatusUpdated;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns true when the source is confirmed down.</summary>
    public bool IsDown(string siteName)
    {
        lock (_lock)
        {
            return _statusCache.TryGetValue(siteName, out bool up) && !up;
        }
    }

    /// <summary>Returns true only when the status for this site has been resolved.</summary>
    public bool HasResult(string siteName)
    {
        lock (_lock)
        {
            return _statusCache.ContainsKey(siteName);
        }
    }

    /// <summary>
    /// Starts the background ping for all sources if no check is currently running
    /// and the cooldown has elapsed. Safe to call on every tab-appear event.
    /// </summary>
    public void RefreshChecks()
    {
        lock (_lock)
        {
            if (_checkRunning) return;
            if (DateTime.UtcNow - _lastCheckStarted < RefreshCooldown) return;
            _checkRunning = true;
            _lastCheckStarted = DateTime.UtcNow;
        }

        _ = Task.Run(RunChecksAsync);
    }

    /// <summary>
    /// Alias kept for compatibility — behaves like <see cref="RefreshChecks"/>.
    /// </summary>
    public void StartChecksIfNeeded() => RefreshChecks();

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task RunChecksAsync()
    {
        try
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
        finally
        {
            lock (_lock)
            {
                _checkRunning = false;
            }
        }
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

            // 2xx / 3xx = reachable; 5xx = server error = down
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
