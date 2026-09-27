using System.Net;
using WPAIPoster.Llm;

namespace WPAIPoster.Tests;

public class LlmHttpErrorsTests
{
    [Fact]
    public void BuildMessage_ExtractsOpenAiStyleErrorMessage()
    {
        // The actual LM Studio reply when the vision model can't be loaded (out of memory).
        const string body = """
            { "error": { "message": "Failed to load model \"google/gemma-4-12b-qat\". Error: Failed to load model.",
                         "type": "invalid_request_error", "param": "model", "code": null } }
            """;

        string msg = LlmHttpErrors.BuildMessage(400, "Bad Request", body);

        Assert.Equal(
            "LLM server returned 400 (Bad Request): Failed to load model \"google/gemma-4-12b-qat\". Error: Failed to load model.",
            msg);
    }

    [Fact]
    public void BuildMessage_HandlesStringError()
        => Assert.Equal("LLM server returned 404 (Not Found): model not found",
            LlmHttpErrors.BuildMessage(404, "Not Found", """{"error":"model not found"}"""));

    [Fact]
    public void BuildMessage_FallsBackToTrimmedRawBody()
        => Assert.Equal("LLM server returned 502 (Bad Gateway): upstream crashed",
            LlmHttpErrors.BuildMessage(502, "Bad Gateway", "  upstream crashed \n"));

    [Fact]
    public void BuildMessage_TruncatesLongRawBody()
    {
        string msg = LlmHttpErrors.BuildMessage(500, "Internal Server Error", new string('x', 2000));
        Assert.EndsWith("…", msg);
        Assert.True(msg.Length < 600);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildMessage_NoBody_IsStatusOnly(string? body)
        => Assert.Equal("LLM server returned 503 (Service Unavailable)",
            LlmHttpErrors.BuildMessage(503, "Service Unavailable", body));

    [Fact]
    public void BuildMessage_MissingReasonPhrase_UsesStatusName()
        => Assert.Equal("LLM server returned 400 (BadRequest)", LlmHttpErrors.BuildMessage(400, null, null));

    [Fact]
    public async Task EnsureSuccessAsync_ThrowsWithServerDetailAndStatusCode()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent("""{"error":{"message":"Failed to load model."}}""")
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => LlmHttpErrors.EnsureSuccessAsync(response));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("Failed to load model.", ex.Message);
    }

    [Fact]
    public async Task EnsureSuccessAsync_SuccessDoesNotThrow()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        await LlmHttpErrors.EnsureSuccessAsync(response);
    }
}
