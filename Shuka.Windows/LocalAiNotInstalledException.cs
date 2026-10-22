namespace Shuka;

/// <summary>
/// Thrown when all online translation engines (Google unofficial API, Playwright browser)
/// are exhausted or rate-limited and the offline Local AI fallback (Qwen2.5) cannot be used
/// because its model is not installed.
/// </summary>
public class LocalAiNotInstalledException : Exception
{
    public LocalAiNotInstalledException()
        : base("Translation failure: cannot proceed since Local AI is not installed.")
    {
    }

    public LocalAiNotInstalledException(string message)
        : base(message)
    {
    }

    public LocalAiNotInstalledException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
