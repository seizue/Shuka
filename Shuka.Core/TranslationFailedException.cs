namespace Shuka.Core;

/// <summary>
/// Thrown by <see cref="Translator"/> when every translation endpoint (Google GTX,
/// MyMemory, Google Mobile Web) has been exhausted without producing a usable result.
/// Callers should catch this and mark the chapter as untranslated rather than silently
/// embedding Chinese text in the EPUB.
/// </summary>
public sealed class TranslationFailedException : Exception
{
    public TranslationFailedException()
        : base("All translation endpoints failed — rate limit or network error.") { }

    public TranslationFailedException(string message)
        : base(message) { }
}
