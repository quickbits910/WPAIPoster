using System.Text.Json.Serialization;

namespace WPAIPoster.Llm;

// --- Request DTOs ---

public class AnthropicRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 4096;

    [JsonPropertyName("messages")]
    public List<AnthropicMessage> Messages { get; set; } = new();
}

public class AnthropicMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public List<AnthropicContentBlock> Content { get; set; } = new();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnthropicTextBlock), "text")]
[JsonDerivedType(typeof(AnthropicImageBlock), "image")]
public abstract class AnthropicContentBlock { }

public class AnthropicTextBlock : AnthropicContentBlock
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class AnthropicImageBlock : AnthropicContentBlock
{
    [JsonPropertyName("source")]
    public AnthropicImageSource Source { get; set; } = new();
}

public class AnthropicImageSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "base64";

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;
}

// --- Response DTOs ---

public class AnthropicResponse
{
    [JsonPropertyName("content")]
    public List<AnthropicResponseBlock> Content { get; set; } = new();
}

public class AnthropicResponseBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}
