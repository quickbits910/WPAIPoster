using System.Net;
using System.Text.Json;

namespace WPAIPoster.Llm;

/// <summary>
/// Turns a non-success LLM HTTP response into an exception that carries the <em>server's</em> error text.
/// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> discards the body, which hides the actual cause
/// — e.g. LM Studio answers a vision request with <c>400</c> and
/// <c>{"error":{"message":"Failed to load model …"}}</c> when the model can't fit in memory.
/// </summary>
public static class LlmHttpErrors
{
    /// <summary>Longest raw (non-JSON) body excerpt included in the message.</summary>
    private const int MaxBodyExcerpt = 500;

    /// <summary>Throws an <see cref="HttpRequestException"/> (with the server's error text) unless the response succeeded.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? body = null;
        try { body = await response.Content.ReadAsStringAsync(); }
        catch { /* best-effort: fall back to the status line alone */ }

        throw new HttpRequestException(
            BuildMessage((int)response.StatusCode, response.ReasonPhrase, body),
            inner: null, statusCode: response.StatusCode);
    }

    /// <summary>
    /// Pure: <c>"400 (Bad Request): &lt;detail&gt;"</c>, where the detail is the OpenAI/Anthropic-style
    /// <c>error.message</c> (or a string <c>error</c>) when the body is JSON, else a trimmed body excerpt.
    /// </summary>
    public static string BuildMessage(int statusCode, string? reasonPhrase, string? body)
    {
        string reason = string.IsNullOrWhiteSpace(reasonPhrase)
            ? ((HttpStatusCode)statusCode).ToString()
            : reasonPhrase;
        string status = $"LLM server returned {statusCode} ({reason})";

        string? detail = ExtractDetail(body);
        return string.IsNullOrEmpty(detail) ? status : $"{status}: {detail}";
    }

    private static string? ExtractDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out JsonElement msg) && msg.ValueKind == JsonValueKind.String)
                    return msg.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the raw excerpt.
        }

        string trimmed = body.Trim();
        return trimmed.Length <= MaxBodyExcerpt ? trimmed : trimmed[..MaxBodyExcerpt] + "…";
    }
}
