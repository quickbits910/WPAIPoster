using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPAIPoster.Llm;

/// <summary>
/// Sends requests to any OpenAI-compatible chat completions endpoint.
/// Used directly for custom endpoints; also the backing implementation for
/// <see cref="LmStudioClient"/>, <see cref="OllamaClient"/>, and <see cref="OpenAiClient"/>.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class OpenAiCompatibleClient(
    HttpClient httpClient,
    string baseUrl,
    string model,
    string? apiKey = null) : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task<string?> SendAsync(string promptText, string? base64Image, string? mimeType)
    {
        if (base64Image is not null && mimeType is not null)
            return SendAsync(promptText, new[] { (base64Image, mimeType) });

        return SendAsync(promptText, Array.Empty<(string, string)>());
    }

    public async Task<string?> SendAsync(
        string promptText, IReadOnlyList<(string Base64, string MimeType)> images)
    {
        var messages = new List<ChatMessage>();

        if (images.Count > 0)
        {
            var parts = new List<ContentPart> { new TextContentPart { Text = promptText } };
            foreach (var (b64, mime) in images)
            {
                parts.Add(new ImageUrlContentPart
                {
                    ImageUrl = new ImageUrlDetail { Url = $"data:{mime};base64,{b64}" }
                });
            }
            messages.Add(new ChatMessage { Role = "user", ContentParts = parts });
        }
        else
        {
            messages.Add(new ChatMessage { Role = "user", Content = promptText });
        }

        var requestBody = new ChatCompletionRequest { Model = model, Messages = messages };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions)
        };

        if (apiKey is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>();
        return result?.Choices is { Count: > 0 }
            ? result.Choices[0].Message.Content
            : null;
    }
}
