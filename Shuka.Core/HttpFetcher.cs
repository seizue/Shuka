using System.Text;
using System.Text.RegularExpressions;

namespace Shuka.Core;

/// <summary>
/// Fetches web pages with charset auto-detection and Cloudflare detection.
/// On Android, inject a ICloudflareBypass implementation to handle CF challenges
/// via a WebView instead of Playwright.
/// </summary>
public class HttpFetcher : IDisposable
{
    private readonly HttpClient _site;
    private readonly ICloudflareBypass? _cfBypass;

    public HttpFetcher(ICloudflareBypass? cfBypass = null)
    {
        _cfBypass = cfBypass;

        var sh = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        _site = new HttpClient(sh) { Timeout = TimeSpan.FromSeconds(30) };
        _site.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _site.DefaultRequestHeaders.Add("Accept-Language", "zh-TW,zh;q=0.9,zh-CN;q=0.8");
        _site.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    }

    public async Task<string> Fetch(string url, int retries = 4, Action<string>? log = null,
        CancellationToken ct = default)
    {
        int delay = 1000;
        Exception? last = null;

        for (int i = 0; i <= retries; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var uri = new Uri(url);
                req.Headers.Add("Referer", $"{uri.Scheme}://{uri.Host}/");

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(30));
                var resp = await _site.SendAsync(req, linked.Token);

                // Detect Cloudflare block (403/503 with cf-ray header or cloudflare server)
                bool isCf = resp.Headers.Contains("cf-ray") || resp.Headers.Server.ToString().Contains("cloudflare");
                if (isCf && ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 503))
                {
                    if (_cfBypass != null)
                    {
                        log?.Invoke("[cloudflare] Using bypass...");
                        return await _cfBypass.FetchAsync(url);
                    }
                    throw new Exception("Cloudflare blocked the request and no bypass is configured.");
                }

                // czbooks.net sometimes returns 200 but with a CF challenge page — detect it
                if (resp.IsSuccessStatusCode)
                {
                    byte[] rawBytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    string ascii = Encoding.ASCII.GetString(rawBytes);

                    bool isCfChallenge = ascii.Contains("cf-browser-verification") ||
                                         ascii.Contains("jschl-answer") ||
                                         ascii.Contains("challenge-form") ||
                                         (ascii.Contains("cloudflare") && ascii.Contains("checking your browser"));

                    // Also detect the czbooks login-wall stub (tiny page with no real content)
                    bool isLoginWall = rawBytes.Length < 2000 &&
                                       (ascii.Contains("Facebook") || ascii.Contains("Google") || ascii.Contains("Line")) &&
                                       ascii.Contains("czbooks");

                    if (isCfChallenge || isLoginWall)
                    {
                        if (_cfBypass != null)
                        {
                            log?.Invoke(isCfChallenge
                                ? "[cloudflare] JS challenge detected, using bypass..."
                                : "[cloudflare] Login wall detected, using bypass...");
                            return await _cfBypass.FetchAsync(url);
                        }
                        throw new Exception("Cloudflare/login wall detected and no bypass is configured.");
                    }

                    var cm = Regex.Match(ascii, @"charset\s*=\s*[""']?\s*([\w-]+)", RegexOptions.IgnoreCase);
                    string charset = cm.Success ? cm.Groups[1].Value.Trim() : "utf-8";
                    Encoding enc;
                    try   { enc = Encoding.GetEncoding(charset); }
                    catch { enc = Encoding.UTF8; }
                    return enc.GetString(rawBytes);
                }

                resp.EnsureSuccessStatusCode(); // throws for non-success
                return ""; // unreachable
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // propagate user cancellation immediately
            }
            catch (Exception ex) { last = ex; await Task.Delay(delay, ct); delay = Math.Min(delay * 2, 16000); }
        }

        throw new Exception($"Fetch failed: {url} — {last?.Message}");
    }

    public void Dispose() => _site.Dispose();
}

/// <summary>
/// Implement this interface per platform to handle Cloudflare challenges.
/// Windows: use Playwright. Android: use a WebView.
/// </summary>
public interface ICloudflareBypass
{
    Task<string> FetchAsync(string url);
}
