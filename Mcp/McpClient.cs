using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Headers;
using WPAIPoster.Config;
using WPAIPoster.Mcp;

namespace WPAIPoster.Mcp;

/// <summary>
/// Talks to a remote MCP server and exposes the subset of MCP needed by web research: initialize, then call
/// <c>fetch_content</c>. The transport is auto-detected: streamable HTTP first, SSE when the endpoint is
/// marked as SSE or when the streamable endpoint answers with a fallback status.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class McpClient(HttpClient httpClient, McpServerConfig config) : IMcpPageFetcher
{
    private bool _Initialized;
    private bool _UseSse;
    private string? _SessionId;

    public Task<string?> FetchContentAsync(string url, int maxLength)
        => FetchContentInternalAsync(url, maxLength);

    private async Task<string?> FetchContentInternalAsync(string pageUrl, int maxLength)
    {
        string endpoint = config.EffectiveUrl ?? throw new InvalidOperationException("MCP server URL is not configured.");

        _UseSse = await DetectSseAndMaybeInitializeAsync(endpoint);
        if (!_Initialized)
            await InitializeAsync(endpoint, _UseSse);

        string request = McpProtocol.BuildToolCallRequest(2, McpProtocol.FetchContentToolName, pageUrl, maxLength);
        using HttpResponseMessage response = await PostAsync(endpoint, request, _UseSse);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(McpProtocol.BuildHttpErrorMessage((int)response.StatusCode, response.ReasonPhrase, body));

        string? data = _UseSse || McpProtocol.LooksLikeSse(body)
            ? McpProtocol.ParseSseResponse(body, 2)
            : body;

        string? error = McpProtocol.ParseJsonRpcError(data);
        if (error is not null)
            throw new HttpRequestException($"MCP JSON-RPC error: {error}");

        return McpProtocol.ParseToolResultText(data);
    }

    private async Task<bool> DetectSseAndMaybeInitializeAsync(string endpoint)
    {
        if (config.IsSseEndpoint)
            return true;

        try
        {
            using HttpResponseMessage response = await PostAsync(endpoint, McpProtocol.BuildInitializeRequest(1), false);
            string body = await response.Content.ReadAsStringAsync();
            RememberSessionId(response);

            if (response.IsSuccessStatusCode)
            {
                string? error = McpProtocol.ParseJsonRpcError(body);
                if (error is not null)
                    throw new HttpRequestException($"MCP JSON-RPC error: {error}");

                if (McpProtocol.LooksLikeSse(body))
                {
                    _UseSse = true;
                    _Initialized = true;
                    return true;
                }

                _UseSse = false;
                _Initialized = true;
                return false;
            }

            if (!McpProtocol.ShouldFallbackToSse((int)response.StatusCode, endpoint))
                throw new HttpRequestException(McpProtocol.BuildHttpErrorMessage((int)response.StatusCode, response.ReasonPhrase, body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // Try SSE below.
        }
        return true;
    }

    private async Task InitializeAsync(string endpoint, bool sse)
    {
        if (_Initialized)
            return;

        using HttpResponseMessage response = await PostAsync(endpoint, McpProtocol.BuildInitializeRequest(1), sse);
        string body = await response.Content.ReadAsStringAsync();
        RememberSessionId(response);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(McpProtocol.BuildHttpErrorMessage((int)response.StatusCode, response.ReasonPhrase, body));

        string? data = sse || McpProtocol.LooksLikeSse(body)
            ? McpProtocol.ParseSseResponse(body, 1)
            : body;

        string? error = McpProtocol.ParseJsonRpcError(data);
        if (error is not null)
            throw new HttpRequestException($"MCP JSON-RPC error: {error}");

        _Initialized = true;
    }

    private async Task<HttpResponseMessage> PostAsync(string endpoint, string json, bool sse)
    {
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };

        request.Headers.Add("Accept", "application/json, text/event-stream");
        request.Headers.Add("Mcp-Protocol-Version", McpProtocol.ProtocolVersion);
        if (_SessionId is not null)
            request.Headers.Add("Mcp-Session-Id", _SessionId);
        return await httpClient.SendAsync(request);
    }

    private void RememberSessionId(HttpResponseMessage response)
    {
        if (_SessionId is null && response.Headers.TryGetValues("mcp-session-id", out var values))
            _SessionId = values.FirstOrDefault();
    }


}
