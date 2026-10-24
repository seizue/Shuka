using System.Text.RegularExpressions;
using Shuka.Core;

namespace Shuka;

/// <summary>
/// Routes translation calls to the appropriate engine with automatic failover:
///
/// 1. Default (Google):
///    Uses fast HttpClient scraper. If rate-limited (429/CAPTCHA or untranslated Chinese),
///    marks Google as rate-limited with a cooldown and automatically falls back to
///    Browser Translate (Playwright).
/// 2. Second-line fallback (Browser Translate / Playwright):
///    Uses bundled Chromium on translate.google.com (immune to scraper 429s).
/// 3. Last-resort fallback (Local AI · Qwen2.5 GGUF):
///    100% offline on-device translation.
///    If the model (~468MB) is NOT installed, throws LocalAiNotInstalledException so the UI
///    can guide the user to download the configuration or exit cleanly.
/// </summary>
internal sealed class WindowsTranslator : IAsyncDisposable
{
    private readonly TranslatorSettings    _settings;
    private readonly Translator            _google;
    private          LocalLlamaTranslator? _localLlama;
    private          PlaywrightTranslator? _playwright;

    // Google scraper rate limit tracking
    private bool     _googleRateLimited;
    private DateTime _googleRateLimitReset = DateTime.MinValue;

    private static readonly Regex SignificantChineseRegex =
        new(@"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]{8,}", RegexOptions.Compiled);

    private static readonly Regex AnyChineseRegex =
        new(@"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]", RegexOptions.Compiled);

    public WindowsTranslator(TranslatorSettings settings, Translator googleFallback)
    {
        _settings = settings;
        _google   = googleFallback;
    }

    public bool IsGoogleRateLimited =>
        _googleRateLimited && DateTime.UtcNow < _googleRateLimitReset;

    public void MarkGoogleRateLimited(TimeSpan? duration = null)
    {
        _googleRateLimited = true;
        _googleRateLimitReset = DateTime.UtcNow + (duration ?? TimeSpan.FromMinutes(20));
    }

    public void ClearGoogleRateLimit()
    {
        _googleRateLimited = false;
        _googleRateLimitReset = DateTime.MinValue;
    }

    public LocalLlamaTranslator LocalLlama  => _localLlama ??= new LocalLlamaTranslator();
    public PlaywrightTranslator PwTranslate => _playwright ??= new PlaywrightTranslator();

    public async Task<string> Translate(string text, Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (!AnyChineseRegex.IsMatch(text)) return text;

        switch (_settings.Engine)
        {
            case TranslatorEngine.LocalLlama:
                return await TranslateViaLocalLlamaWithFallbacks(text, log, ct);

            case TranslatorEngine.PlaywrightTranslate:
                return await TranslateViaPlaywrightWithFallbacks(text, log, ct);

            default: // Google
                return await TranslateViaGoogleWithFallbacks(text, log, ct);
        }
    }

    private async Task<string> TranslateViaGoogleWithFallbacks(string text, Action<string>? log, CancellationToken ct)
    {
        // 1. Try Google scraper if not currently flagged as rate-limited
        if (!IsGoogleRateLimited)
        {
            try
            {
                string res = await _google.Translate(text, log, ct);
                if (!IsUntranslatedChinese(text, res))
                {
                    return res;
                }

                // If result is untranslated Chinese, Google returned chunk (rate-limited / bot blocked)
                MarkGoogleRateLimited();
                log?.Invoke("[Google API rate limit / block detected — auto-falling back to Browser Translate]");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                MarkGoogleRateLimited();
                log?.Invoke($"[Google API failed: {ex.Message} — auto-falling back to Browser Translate]");
            }
        }
        else
        {
            log?.Invoke("[Google API is rate-limited — routing to Browser Translate]");
        }

        // 2. Auto-fallback to Browser Translate (Playwright)
        try
        {
            string pwRes = await PwTranslate.Translate(text, log, ct);
            if (!IsUntranslatedChinese(text, pwRes))
            {
                return pwRes;
            }
            log?.Invoke("[Browser Translate incomplete or returned untranslated text]");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke($"[Browser Translate failed: {ex.Message}]");
        }

        // 3. Last-resort fallback: Local AI (Qwen2.5)
        return await TranslateViaLocalLlamaOrThrow(text, log, ct);
    }

    private async Task<string> TranslateViaPlaywrightWithFallbacks(string text, Action<string>? log, CancellationToken ct)
    {
        try
        {
            string pwRes = await PwTranslate.Translate(text, log, ct);
            if (!IsUntranslatedChinese(text, pwRes))
            {
                return pwRes;
            }
            log?.Invoke("[Browser Translate incomplete or returned untranslated text]");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke($"[Browser Translate failed: {ex.Message}]");
        }

        // If Browser Translate failed, try Google if not rate-limited
        if (!IsGoogleRateLimited)
        {
            try
            {
                string res = await _google.Translate(text, log, ct);
                if (!IsUntranslatedChinese(text, res))
                    return res;
                MarkGoogleRateLimited();
            }
            catch (OperationCanceledException) { throw; }
            catch { MarkGoogleRateLimited(); }
        }

        // Last resort: Local AI
        return await TranslateViaLocalLlamaOrThrow(text, log, ct);
    }

    private async Task<string> TranslateViaLocalLlamaWithFallbacks(string text, Action<string>? log, CancellationToken ct)
    {
        if (!LocalLlama.IsModelDownloaded())
        {
            log?.Invoke("[Local AI selected but model is not installed]");
            throw new LocalAiNotInstalledException();
        }

        try
        {
            string localRes = await LocalLlama.Translate(text, log, ct);
            if (!IsUntranslatedChinese(text, localRes))
                return localRes;
            log?.Invoke("[Local AI returned untranslated text — falling back to Browser Translate]");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke($"[Local AI failed: {ex.Message} — falling back to Browser Translate]");
        }

        // Fallback to Browser Translate
        try
        {
            string pwRes = await PwTranslate.Translate(text, log, ct);
            if (!IsUntranslatedChinese(text, pwRes))
                return pwRes;
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // Fallback to Google if not rate limited
        if (!IsGoogleRateLimited)
        {
            try
            {
                string res = await _google.Translate(text, log, ct);
                if (!IsUntranslatedChinese(text, res))
                    return res;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        throw new Exception("All translation engines (Local AI, Browser Translate, Google) failed.");
    }

    private async Task<string> TranslateViaLocalLlamaOrThrow(string text, Action<string>? log, CancellationToken ct)
    {
        if (!LocalLlama.IsModelDownloaded())
        {
            log?.Invoke("[Offline Local AI fallback required, but model is not installed]");
            throw new LocalAiNotInstalledException();
        }

        log?.Invoke("[Falling back to Local AI · Qwen2.5 (offline)]");
        try
        {
            string localRes = await LocalLlama.Translate(text, log, ct);
            if (!IsUntranslatedChinese(text, localRes))
            {
                return localRes;
            }
            throw new Exception("Local AI completed inference but output contained untranslated Chinese text.");
        }
        catch (OperationCanceledException) { throw; }
        catch (LocalAiNotInstalledException) { throw; }
        catch (Exception ex)
        {
            throw new Exception($"All translation engines failed. Local AI error: {ex.Message}", ex);
        }
    }

    public static bool IsUntranslatedChinese(string original, string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return true;
        if (!AnyChineseRegex.IsMatch(original)) return false;

        // If the translation resulted in the exact same text, it was echoed/untranslated
        if (string.Equals(result.Trim(), original.Trim(), StringComparison.Ordinal))
            return true;

        // If short text (e.g. title) still contains any Chinese characters
        if (original.Length < 30)
            return AnyChineseRegex.IsMatch(result);

        // Count Chinese characters in the result (including ideographs across standard CJK blocks)
        int chineseCount = 0;
        for (int i = 0; i < result.Length; i++)
        {
            char c = result[i];
            if ((c >= 0x4e00 && c <= 0x9fff) ||
                (c >= 0x3400 && c <= 0x4dbf) ||
                (c >= 0xf900 && c <= 0xfaff))
            {
                chineseCount++;
            }
        }

        // If there are 5 or more Chinese characters, it definitely contains untranslated Chinese
        if (chineseCount >= 5)
            return true;

        // Or if Chinese characters make up more than 5% of the string
        if (result.Length > 0 && (double)chineseCount / result.Length > 0.05)
            return true;

        return false;
    }

    /// <summary>Drop cached engine instances so they re-initialise with new settings.</summary>
    public void ReloadEngines()
    {
        ClearGoogleRateLimit();
        _localLlama?.Dispose();
        _localLlama = null;

        if (_playwright != null)
        {
            var old = _playwright;
            _playwright = null;
            Task.Run(async () => { try { await old.DisposeAsync(); } catch { } });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _localLlama?.Dispose();
        if (_playwright != null)
            await _playwright.DisposeAsync();
    }
}
