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

    // Register extended encodings (GBK, GB2312, Big5, etc.) once per process
    static HttpFetcher()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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

                if (resp.IsSuccessStatusCode)
                {
                    byte[] rawBytes = await resp.Content.ReadAsByteArrayAsync(ct);

                    // Use Latin-1 (ISO-8859-1) to scan the raw bytes — it maps all 256
                    // byte values 1:1, so ASCII-range content (like charset declarations
                    // and CF challenge markers) is preserved intact even in GBK/Big5 pages.
                    // Using ASCII would replace bytes > 127 with '?' and break the regex.
                    string latin1 = Encoding.Latin1.GetString(rawBytes);

                    bool isCfChallenge = latin1.Contains("cf-browser-verification") ||
                                         latin1.Contains("jschl-answer") ||
                                         latin1.Contains("challenge-form") ||
                                         (latin1.Contains("cloudflare") && latin1.Contains("checking your browser"));

                    // Also detect the czbooks login-wall stub (tiny page with no real content)
                    bool isLoginWall = rawBytes.Length < 2000 &&
                                       (latin1.Contains("Facebook") || latin1.Contains("Google") || latin1.Contains("Line")) &&
                                       latin1.Contains("czbooks");

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

                    // Detect charset from HTTP Content-Type header first (most reliable),
                    // then fall back to the HTML meta tag declaration.
                    string charset = "utf-8";
                    string? ctHeader = resp.Content.Headers.ContentType?.CharSet;
                    if (!string.IsNullOrWhiteSpace(ctHeader))
                    {
                        charset = ctHeader.Trim().Trim('"');
                    }
                    else
                    {
                        // Scan only the first 4KB — charset is always in <head>
                        string head = latin1[..Math.Min(latin1.Length, 4096)];
                        var cm = Regex.Match(head, @"charset\s*=\s*[""']?\s*([\w-]+)", RegexOptions.IgnoreCase);
                        if (cm.Success) charset = cm.Groups[1].Value.Trim();
                    }

                    // Normalize common aliases that .NET may not recognise by name
                    charset = charset.ToLowerInvariant() switch
                    {
                        "gb2312" or "gb_2312" or "csgb2312" or "x-gbk" or "chinese" => "gbk",
                        "big5"   or "csbig5"  or "x-x-big5"                         => "big5",
                        _                                                             => charset
                    };

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
