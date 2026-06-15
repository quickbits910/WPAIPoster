using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPAIPoster.Llm;

/// <summary>
/// Anthropic Messages API client.
/// Sends requests to <c>https://api.anthropic.com/v1/messages</c>.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class AnthropicClient(HttpClient httpClient, string model, string apiKey) : ILlmClient
{
    private const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

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
        var content = new List<AnthropicContentBlock>
        {
            new AnthropicTextBlock { Text = promptText }
        };

        foreach (var (b64, mime) in images)
        {
            content.Add(new AnthropicImageBlock
            {
                Source = new AnthropicImageSource
                {
                    Type = "base64",
                    MediaType = mime,
                    Data = b64
                }
            });
        }

        var requestBody = new AnthropicRequest
        {
            Model = model,
            Messages = new List<AnthropicMessage>
            {
                new AnthropicMessage { Role = "user", Content = content }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions)
        };

        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>();
        return result?.Content.FirstOrDefault(b => b.Type == "text")?.Text;
    }
}
