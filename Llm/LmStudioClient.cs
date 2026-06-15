using System.Diagnostics.CodeAnalysis;

namespace WPAIPoster.Llm;

/// <summary>
/// LM Studio client — OpenAI-compatible endpoint at <c>http://127.0.0.1:1234</c> by default.
/// Pass <paramref name="baseUrl"/> to target a different host or port.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class LmStudioClient(HttpClient httpClient, string model, string? baseUrl = null)
    : ILlmClient
{
    public const string DefaultBaseUrl = "http://127.0.0.1:1234";

    private readonly OpenAiCompatibleClient _inner =
        new(httpClient, baseUrl ?? DefaultBaseUrl, model);

    public Task<string?> SendAsync(string promptText, string? base64Image, string? mimeType)
        => _inner.SendAsync(promptText, base64Image, mimeType);

    public Task<string?> SendAsync(string promptText, IReadOnlyList<(string Base64, string MimeType)> images)
        => _inner.SendAsync(promptText, images);
}
