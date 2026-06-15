namespace WPAIPoster.Llm;

/// <summary>
/// Builds an <see cref="ILlmClient"/> for a given provider string, mirroring the provider switch
/// used across the sibling ImageTagger app. Defaults to LM Studio (local) when the provider is
/// unknown or unset.
/// </summary>
public static class LlmClientFactory
{
    /// <summary>
    /// Creates a client for <paramref name="provider"/> using <paramref name="model"/>.
    /// API keys fall back to the standard provider environment variables when <paramref name="apiKey"/> is null.
    /// </summary>
    public static ILlmClient Create(
        HttpClient httpClient,
        string? provider,
        string model,
        string? baseUrl,
        string? apiKey)
    {
        string p = (provider ?? "lmstudio").Trim().ToLowerInvariant();
        string? key = apiKey ?? GetProviderApiKeyFallback(p);

        return p switch
        {
            "anthropic"         => new AnthropicClient(httpClient, model, key ?? string.Empty),
            "openai"            => new OpenAiClient(httpClient, model, key ?? string.Empty),
            "openai-compatible" => new OpenAiCompatibleClient(httpClient, baseUrl ?? LmStudioClient.DefaultBaseUrl, model, key),
            "ollama"            => new OllamaClient(httpClient, model, baseUrl),
            _                   => new LmStudioClient(httpClient, model, baseUrl) // lmstudio (default)
        };
    }

    /// <summary>Returns the provider's API key from the environment, or null when none applies.</summary>
    public static string? GetProviderApiKeyFallback(string provider) => provider switch
    {
        "anthropic"                     => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        "openai" or "openai-compatible" => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        _                               => null
    };
}
