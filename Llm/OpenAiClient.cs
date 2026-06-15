using System.Diagnostics.CodeAnalysis;

namespace WPAIPoster.Llm;

/// <summary>
/// OpenAI client — sends requests to <c>https://api.openai.com</c>.
/// The API key is required and is sent as a Bearer token.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class OpenAiClient(HttpClient httpClient, string model, string apiKey)
    : ILlmClient
{
    private const string BaseUrl = "https://api.openai.com";

    private readonly OpenAiCompatibleClient _inner =
        new(httpClient, BaseUrl, model, apiKey);

    public Task<string?> SendAsync(string promptText, string? base64Image, string? mimeType)
        => _inner.SendAsync(promptText, base64Image, mimeType);

    public Task<string?> SendAsync(string promptText, IReadOnlyList<(string Base64, string MimeType)> images)
        => _inner.SendAsync(promptText, images);
}
