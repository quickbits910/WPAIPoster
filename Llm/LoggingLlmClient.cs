using WPAIPoster.Ui;

namespace WPAIPoster.Llm;

/// <summary>
/// An <see cref="ILlmClient"/> decorator that records every prompt and raw reply to the run log (at DEBUG)
/// before delegating to the wrapped client. This captures the full model I/O for a run so behaviour like
/// JSON repair, theme generation, and editor scoring can be debugged after the fact.
/// </summary>
public sealed class LoggingLlmClient(ILlmClient inner, RunLogger logger, string label = "llm") : ILlmClient
{
    public async Task<string?> SendAsync(string promptText, string? base64Image, string? mimeType)
    {
        logger.Write("DEBUG", $"{label} prompt{(base64Image is null ? "" : " (+1 image)")}:\n{promptText}");
        string? reply = await inner.SendAsync(promptText, base64Image, mimeType);
        logger.Write("DEBUG", $"{label} reply:\n{reply ?? "<null>"}");
        return reply;
    }

    public async Task<string?> SendAsync(string promptText, IReadOnlyList<(string Base64, string MimeType)> images)
    {
        logger.Write("DEBUG", $"{label} prompt (+{images.Count} image(s)):\n{promptText}");
        string? reply = await inner.SendAsync(promptText, images);
        logger.Write("DEBUG", $"{label} reply:\n{reply ?? "<null>"}");
        return reply;
    }
}
