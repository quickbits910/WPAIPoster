using System.Text.Json;
using WPAIPoster.Llm;

namespace WPAIPoster.Tests;

public class LlmClientFactoryTests
{
    private readonly HttpClient _http = new();

    [Theory]
    [InlineData("anthropic", typeof(AnthropicClient))]
    [InlineData("openai", typeof(OpenAiClient))]
    [InlineData("openai-compatible", typeof(OpenAiCompatibleClient))]
    [InlineData("ollama", typeof(OllamaClient))]
    [InlineData("lmstudio", typeof(LmStudioClient))]
    public void Create_SelectsExpectedClient(string provider, Type expected)
    {
        ILlmClient client = LlmClientFactory.Create(_http, provider, "m", "http://localhost:1234", "key");
        Assert.IsType(expected, client);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-unknown")]
    public void Create_DefaultsToLmStudio(string? provider)
    {
        ILlmClient client = LlmClientFactory.Create(_http, provider, "m", null, null);
        Assert.IsType<LmStudioClient>(client);
    }

    [Fact]
    public void Create_IsCaseInsensitive()
    {
        Assert.IsType<AnthropicClient>(LlmClientFactory.Create(_http, "AnThRoPiC", "m", null, "k"));
    }
}

public class ChatMessageSerializationTests
{
    [Fact]
    public void TextMessage_SerializesContentAsString()
    {
        var msg = new ChatMessage { Role = "user", Content = "hello" };
        string json = JsonSerializer.Serialize(msg);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("content").ValueKind);
        Assert.Equal("hello", doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void VisionMessage_SerializesContentAsArray()
    {
        var msg = new ChatMessage
        {
            Role = "user",
            ContentParts = new List<ContentPart>
            {
                new TextContentPart { Text = "describe" },
                new ImageUrlContentPart { ImageUrl = new ImageUrlDetail { Url = "data:image/jpeg;base64,AAA" } }
            }
        };

        string json = JsonSerializer.Serialize(msg);

        using var doc = JsonDocument.Parse(json);
        JsonElement content = doc.RootElement.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/jpeg;base64,AAA",
            content[1].GetProperty("image_url").GetProperty("url").GetString());
    }
}

public class AnthropicModelsTests
{
    [Fact]
    public void Request_DefaultsMaxTokens()
    {
        var req = new AnthropicRequest { Model = "claude" };
        Assert.Equal(4096, req.MaxTokens);
    }

    [Fact]
    public void ImageBlock_SerializesWithSnakeCaseMediaType()
    {
        var block = new AnthropicImageBlock
        {
            Source = new AnthropicImageSource { MediaType = "image/png", Data = "AAA" }
        };

        string json = JsonSerializer.Serialize<AnthropicContentBlock>(block);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("image", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("image/png", doc.RootElement.GetProperty("source").GetProperty("media_type").GetString());
    }
}
