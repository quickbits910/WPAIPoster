using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPAIPoster.Llm;

public class ModelsResponse
{
    [JsonPropertyName("data")]
    public List<ModelInfo> Data { get; set; } = new();
}

public class ModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

public class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();
}

/// <summary>
/// Represents a chat message. For text-only messages, set <see cref="Content"/>.
/// For multimodal (vision) messages, set <see cref="ContentParts"/> instead.
/// A custom converter ensures only the appropriate property is serialized.
/// </summary>
[JsonConverter(typeof(ChatMessageConverter))]
public class ChatMessage
{
    public string Role { get; set; } = string.Empty;

    /// <summary>Plain text content (text-only messages).</summary>
    public string? Content { get; set; }

    /// <summary>Multipart content for vision requests.</summary>
    public List<ContentPart>? ContentParts { get; set; }
}

// --- Content part hierarchy (OpenAI vision format) ---

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContentPart), "text")]
[JsonDerivedType(typeof(ImageUrlContentPart), "image_url")]
public abstract class ContentPart { }

public class TextContentPart : ContentPart
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class ImageUrlContentPart : ContentPart
{
    [JsonPropertyName("image_url")]
    public ImageUrlDetail ImageUrl { get; set; } = new();
}

public class ImageUrlDetail
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

// --- Response DTOs ---

public class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<ChatChoice> Choices { get; set; } = new();
}

public class ChatChoice
{
    [JsonPropertyName("message")]
    public ChatResponseMessage Message { get; set; } = new();
}

public class ChatResponseMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Serializes ChatMessage so that:
///   - When ContentParts is set  → "content" is the JSON array of parts
///   - When Content is set       → "content" is a plain string
/// This matches the OpenAI chat completions schema.
/// </summary>
public class ChatMessageConverter : JsonConverter<ChatMessage>
{
    public override ChatMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var msg = new ChatMessage
        {
            Role = root.GetProperty("role").GetString() ?? string.Empty
        };

        if (root.TryGetProperty("content", out var contentEl)
            && contentEl.ValueKind == JsonValueKind.String)
        {
            msg.Content = contentEl.GetString();
        }

        return msg;
    }

    public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", value.Role);

        if (value.ContentParts is { Count: > 0 })
        {
            writer.WritePropertyName("content");
            JsonSerializer.Serialize(writer, value.ContentParts, options);
        }
        else if (value.Content is not null)
        {
            writer.WriteString("content", value.Content);
        }

        writer.WriteEndObject();
    }
}
