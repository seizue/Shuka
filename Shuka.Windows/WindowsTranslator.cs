using Shuka.Core;

namespace Shuka;

/// <summary>
/// Routes translation calls to the user-selected engine.
///
/// Engines:
///   Google              → Raw HttpClient scraper (may hit 429 / CAPTCHA on heavy use)
///   LocalLlama          → In-process Qwen2.5 GGUF (100% offline, ~468MB model)
///   PlaywrightTranslate → Chromium-powered Google Translate (immune to 429s)
///
/// Any engine falls back to Google on non-cancellation failure.
/// </summary>
internal sealed class WindowsTranslator : IAsyncDisposable
{
    private readonly TranslatorSettings    _settings;
    private readonly Translator            _google;
    private          LocalLlamaTranslator? _localLlama;
    private          PlaywrightTranslator? _playwright;

    public WindowsTranslator(TranslatorSettings settings, Translator googleFallback)
    {
        _settings = settings;
        _google   = googleFallback;
    }

    private LocalLlamaTranslator  LocalLlama  => _localLlama ??= new LocalLlamaTranslator();
    private PlaywrightTranslator  PwTranslate => _playwright ??= new PlaywrightTranslator();

    public async Task<string> Translate(string text, Action<string>? log = null,
        CancellationToken ct = default)
    {
        switch (_settings.Engine)
        {
            case TranslatorEngine.LocalLlama:
                try
                {
                    return await LocalLlama.Translate(text, log, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log?.Invoke($"[Local AI failed: {ex.Message} — falling back to Google]");
                    return await _google.Translate(text, log, ct);
                }

            case TranslatorEngine.PlaywrightTranslate:
                try
                {
                    string res = await PwTranslate.Translate(text, log, ct);
                    // If Browser Translate returned untranslated text with significant Chinese, fall back to Google scraper
                    if (System.Text.RegularExpressions.Regex.IsMatch(res, @"[\u4e00-\u9fff]{10,}"))
                    {
                        log?.Invoke("[Browser Translate incomplete/untranslated — falling back to Google]");
                        return await _google.Translate(text, log, ct);
                    }
                    return res;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log?.Invoke($"[Browser Translate failed: {ex.Message} — falling back to Google]");
                    return await _google.Translate(text, log, ct);
                }

            default: // Google
                return await _google.Translate(text, log, ct);
        }
    }

    /// <summary>Drop cached engine instances so they re-initialise with new settings.</summary>
    public void ReloadEngines()
    {
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
