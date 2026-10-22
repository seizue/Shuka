using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Shuka.Core;

namespace Shuka.Test;

internal static class Program
{
    private const string SampleZh =
        "这一章节讲述的是主角在古代世界中发现了一本神秘的书，" +
        "书中记载了无数强大的修炼功法。他决定将这些秘密传授给自己的弟子，" +
        "从而改变整个修炼界的格局。";

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string engine = args.Length > 0 ? args[0].ToLower() : "all";

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine("  Shuka Translation Engine Live Test");
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine($"Sample text: {SampleZh}");
        Console.WriteLine();

        bool googleOk   = false;
        bool myMemoryOk = false;
        bool pwOk       = false;

        // ── 1. Google HttpClient scraper ──────────────────────────────────────
        if (engine is "all" or "google")
        {
            Console.WriteLine("① Google Engine (Default HttpClient scraper)...");
            (bool ok, string result) = await TestGoogleScraper();
            if (ok)
            {
                Console.WriteLine($"   ✓ Result: {result}");
                googleOk = true;
            }
            else
            {
                Console.WriteLine($"   ✗ BLOCKED/FAILED: {result}");
            }
            Console.WriteLine();
        }

        // ── 2. MyMemory fallback ──────────────────────────────────────────────
        if (engine is "all" or "mymemory")
        {
            Console.WriteLine("② MyMemory API fallback...");
            (bool ok, string result) = await TestMyMemory();
            if (ok)
            {
                Console.WriteLine($"   ✓ Result: {result}");
                myMemoryOk = true;
            }
            else
            {
                Console.WriteLine($"   ✗ FAILED: {result}");
            }
            Console.WriteLine();
        }

        if (engine is "all" or "bing")
        {
            Console.WriteLine("Bing Translator fallback...");
            (bool ok, string result) = await TestBing();
            if (ok)
            {
                Console.WriteLine($"   ✓ Result: {result}");
            }
            else
            {
                Console.WriteLine($"   ✗ FAILED: {result}");
            }
            Console.WriteLine();
        }

        // ── 3. Browser Translate (Playwright) ────────────────────────────────
        if (engine is "all" or "playwright" or "browser")
        {
            Console.WriteLine("③ Browser Translate (Playwright Chromium)...");
            Console.WriteLine("   Launching headless Chrome...");
            (bool ok, string result) = await TestPlaywright();
            if (ok)
            {
                Console.WriteLine($"   ✓ Result: {result}");
                pwOk = true;
            }
            else
            {
                Console.WriteLine($"   ✗ FAILED: {result}");
            }
            Console.WriteLine();
        }

        // ── 4. Local AI (Qwen2.5) ────────────────────────────────────────────
        if (engine is "all" or "llama" or "localai")
        {
            Console.WriteLine("④ Local AI · Qwen2.5 GGUF...");
            string modelPath = LocalLlamaTranslator.ModelPath;
            if (System.IO.File.Exists(modelPath))
            {
                long size = new System.IO.FileInfo(modelPath).Length;
                if (size >= 400_000_000)
                {
                    Console.WriteLine($"   Model present ({size / 1024 / 1024}MB). Testing inference...");
                    (bool ok, string result) = await TestLocalLlama();
                    if (ok)
                        Console.WriteLine($"   ✓ Result: {result}");
                    else
                        Console.WriteLine($"   ✗ FAILED: {result}");
                }
                else
                {
                    Console.WriteLine($"   ⚠ Model incomplete ({size / 1024 / 1024}MB). Re-download via Translation Engine menu.");
                }
            }
            else
            {
                Console.WriteLine("   ○ Model not downloaded. Select 'Local AI · Qwen2.5' in Shuka → Translation Engine to download.");
            }
            Console.WriteLine();
        }

        // ── 5. Integrated WindowsTranslator Orchestrator ─────────────────────
        if (engine is "all" or "orchestrator")
        {
            Console.WriteLine("⑤ Integrated WindowsTranslator Orchestrator (Google Engine with Auto-Fallback)...");
            try
            {
                var settings = new TranslatorSettings { Engine = TranslatorEngine.Google };
                using var http = new HttpClient();
                var google = new Translator(http);
                await using var orchestrator = new WindowsTranslator(settings, google);
                string orchResult = await orchestrator.Translate("第一章 开始新的冒险", msg => Console.WriteLine($"   [Orch 1] {msg}"));
                Console.WriteLine($"   ✓ Translated 1: '第一章 开始新的冒险' → '{orchResult}'");
                Console.WriteLine($"   ✓ Google rate limited state: {orchestrator.IsGoogleRateLimited}");

                Console.WriteLine("   Testing subsequent call (should immediately route to Browser Translate without Google delay):");
                string orchResult2 = await orchestrator.Translate("第二章 探索未知的森林", msg => Console.WriteLine($"   [Orch 2] {msg}"));
                Console.WriteLine($"   ✓ Translated 2: '第二章 探索未知的森林' → '{orchResult2}'");
                Console.WriteLine("   Testing LocalAiNotInstalledException when model is missing:");
                // When Local AI is selected and model is missing:
                var mockSettings = new TranslatorSettings { Engine = TranslatorEngine.LocalLlama };
                await using var localAiTester = new WindowsTranslator(mockSettings, google);
                // Check if model is downloaded
                if (!localAiTester.LocalLlama.IsModelDownloaded())
                {
                    try
                    {
                        await localAiTester.Translate("测试文本");
                        Console.WriteLine("   ✗ Expected LocalAiNotInstalledException but none was thrown!");
                    }
                    catch (LocalAiNotInstalledException ex)
                    {
                        Console.WriteLine($"   ✓ Successfully caught LocalAiNotInstalledException: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine("   (Local AI model is already downloaded on this PC, skipping missing-model throw test)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ FAILED: {ex.Message}");
            }
            Console.WriteLine();
        }

        // ── Summary ───────────────────────────────────────────────────────────
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine("  Summary");
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine($"  Google scraper  : {(googleOk   ? "✓ WORKING" : "✗ BLOCKED (429/CAPTCHA)")}");
        Console.WriteLine($"  MyMemory API    : {(myMemoryOk ? "✓ WORKING" : "✗ FAILED")}");
        Console.WriteLine($"  Browser Translate: {(pwOk      ? "✓ WORKING" : "✗ FAILED / NOT TESTED")}");
        Console.WriteLine();

        return 0;
    }

    // ── Google scraper ────────────────────────────────────────────────────────

    static async Task<(bool, string)> TestGoogleScraper()
    {
        try
        {
            using var http = new HttpClient();
            var translator = new Translator(http);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string result = await translator.Translate(SampleZh, ct: cts.Token);

            if (string.IsNullOrWhiteSpace(result))
                return (false, "Empty result");

            bool hasChineseChars = Regex.IsMatch(result, @"[\u4e00-\u9fff]{5,}");
            if (hasChineseChars)
                return (false, $"Returned Chinese (not translated): {result[..Math.Min(80, result.Length)]}");

            return (true, result);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── MyMemory ──────────────────────────────────────────────────────────────

    static async Task<(bool, string)> TestMyMemory()
    {
        try
        {
            using var http = new HttpClient();
            string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(SampleZh[..Math.Min(480, SampleZh.Length)])}&langpair=zh-CN|en-US";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var resp = await http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}");

            string json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var jdoc = System.Text.Json.JsonDocument.Parse(json);
            int status = jdoc.RootElement.GetProperty("responseStatus").GetInt32();
            if (status != 200) return (false, $"MyMemory status {status}");

            string? text = jdoc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
            if (string.IsNullOrWhiteSpace(text)) return (false, "Empty result");

            bool hasChineseChars = Regex.IsMatch(text, @"[\u4e00-\u9fff]");
            if (hasChineseChars) return (false, $"Returned Chinese: {text}");

            return (true, text);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<(bool, string)> TestBing()
    {
        try
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = true };
            using var http = new HttpClient(handler);
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

            string html = await http.GetStringAsync("https://www.bing.com/translator");
            var igMatch = Regex.Match(html, @"IG:""([a-zA-Z0-9]+)""");
            string ig = igMatch.Success ? igMatch.Groups[1].Value : "";

            var iidMatch = Regex.Match(html, @"data-iid=""([^""]+)""");
            string iid = iidMatch.Success ? iidMatch.Groups[1].Value : "translator.5026";

            var helperMatch = Regex.Match(html, @"params_AbusePreventionHelper\s*=\s*\[\s*(\d+)\s*,\s*""([^""]+)""");
            if (!helperMatch.Success) return (false, "Could not extract Bing AbusePreventionHelper");

            string key = helperMatch.Groups[1].Value;
            string token = helperMatch.Groups[2].Value;

            string postUrl = $"https://www.bing.com/ttranslatev3?isVertical=1&IG={ig}&IID={iid}";
            var form = new Dictionary<string, string>
            {
                { "fromLang", "auto-detect" },
                { "to", "en" },
                { "text", "你好世界" },
                { "token", token },
                { "key", key }
            };

            using var postReq = new HttpRequestMessage(HttpMethod.Post, postUrl);
            postReq.Content = new FormUrlEncodedContent(form);
            var resp = await http.SendAsync(postReq);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {json}");

            return (true, json);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Playwright ────────────────────────────────────────────────────────────

    static async Task<(bool, string)> TestPlaywright()
    {
        try
        {
            await using var pw = new PlaywrightTranslator();
            string result = await pw.Translate(SampleZh,
                msg => Console.WriteLine($"   [PW] {msg}"),
                CancellationToken.None);

            if (string.IsNullOrWhiteSpace(result))
                return (false, "Empty result");

            bool hasChineseChars = Regex.IsMatch(result, @"[\u4e00-\u9fff]{5,}");
            if (hasChineseChars)
                return (false, $"Returned Chinese: {result[..Math.Min(80, result.Length)]}");

            return (true, result);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Local AI ──────────────────────────────────────────────────────────────

    static async Task<(bool, string)> TestLocalLlama()
    {
        try
        {
            using var llama = new LocalLlamaTranslator();
            string result = await llama.Translate(SampleZh,
                msg => Console.WriteLine($"   [Llama] {msg}"),
                CancellationToken.None);

            if (string.IsNullOrWhiteSpace(result))
                return (false, "Empty result");

            bool hasChineseChars = Regex.IsMatch(result, @"[\u4e00-\u9fff]{5,}");
            if (hasChineseChars)
                return (false, $"Returned Chinese: {result[..Math.Min(80, result.Length)]}");

            return (true, result);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
