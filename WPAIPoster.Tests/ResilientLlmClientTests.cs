using System.Text.Json;
using WPAIPoster.Llm;

namespace WPAIPoster.Tests;

public class ResilientLlmClientTests
{
    private static readonly HttpRequestException Down = new("LLM server returned 400 (Bad Request): Failed to load model");

    private static readonly LlmRecoveryOptions Opts =
        new(TimeSpan.FromMinutes(2), 3, [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)]);

    private sealed class Harness
    {
        public List<LlmOutage> Pauses { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        public bool ResumeAfterPause { get; set; } = true;

        public ResilientLlmClient Wrap(ILlmClient inner, LlmRecoveryOptions? options = null) =>
            new(inner, "vision", "http://127.0.0.1:1234", "gemma", options ?? Opts,
                o => { Pauses.Add(o); return Task.FromResult(ResumeAfterPause); },
                delay: d => { Delays.Add(d); return Task.CompletedTask; });
    }

    [Fact]
    public async Task SucceedsOnSecondAttempt_NoPause()
    {
        var h = new Harness();
        var inner = new ScriptedLlmClient(Down, "ok");

        string? reply = await h.Wrap(inner).SendAsync("p", null, null);

        Assert.Equal("ok", reply);
        Assert.Equal(2, inner.Calls);
        Assert.Empty(h.Pauses);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, h.Delays);
    }

    [Fact]
    public async Task FailsThreeTimes_Pauses_FinalAttemptSucceeds()
    {
        var h = new Harness();
        var inner = new ScriptedLlmClient(Down, Down, Down, "ok");

        string? reply = await h.Wrap(inner).SendAsync("p", Array.Empty<(string, string)>());

        Assert.Equal("ok", reply);
        Assert.Equal(4, inner.Calls);
        LlmOutage pause = Assert.Single(h.Pauses);
        Assert.Equal("vision", pause.Label);
        Assert.Equal("http://127.0.0.1:1234", pause.Endpoint);
        Assert.Equal("gemma", pause.Model);
        Assert.Equal(3, pause.Attempts);
        Assert.Contains("Failed to load model", pause.LastError);
        Assert.Equal(TimeSpan.FromMinutes(2), pause.RecoveryWait);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) }, h.Delays);
    }

    [Fact]
    public async Task FinalAttemptFails_Throws_ThenLatchedCallsTryOnceWithoutPause()
    {
        var h = new Harness();
        var inner = new ScriptedLlmClient(Down); // always fails
        var client = h.Wrap(inner);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync("p", null, null));
        Assert.Equal(4, inner.Calls);
        Assert.True(client.IsLatchedDown);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync("p", null, null));
        Assert.Equal(5, inner.Calls);
        Assert.Single(h.Pauses);
    }

    [Fact]
    public async Task Latched_ThenSuccess_ClearsLatch_LaterFailurePausesAgain()
    {
        var h = new Harness();
        var inner = new ScriptedLlmClient(Down, Down, Down, Down, "back", Down);
        var client = h.Wrap(inner);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync("p", null, null));
        Assert.True(client.IsLatchedDown);

        Assert.Equal("back", await client.SendAsync("p", null, null));
        Assert.False(client.IsLatchedDown);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync("p", null, null));
        Assert.Equal(2, h.Pauses.Count);
    }

    [Fact]
    public async Task UserSkips_NoFinalAttempt()
    {
        var h = new Harness { ResumeAfterPause = false };
        var inner = new ScriptedLlmClient(Down, Down, Down, "never");
        var client = h.Wrap(inner);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync("p", null, null));

        Assert.Equal(3, inner.Calls);
        Assert.Single(h.Pauses);
        Assert.True(client.IsLatchedDown);
    }

    [Fact]
    public async Task RecoveryDisabled_ThrowsAfterRetries_NoPause()
    {
        var h = new Harness();
        var inner = new ScriptedLlmClient(Down, Down, Down, "never");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => h.Wrap(inner, Opts with { RecoveryWait = TimeSpan.Zero }).SendAsync("p", null, null));

        Assert.Equal(3, inner.Calls);
        Assert.Empty(h.Pauses);
    }

    [Fact]
    public async Task NonRetryableException_RethrownImmediately()
    {
        var h = new Harness();
        var inner = new ScriptedLlmClient(new JsonException("bad reply"), "ok");

        await Assert.ThrowsAsync<JsonException>(() => h.Wrap(inner).SendAsync("p", null, null));

        Assert.Equal(1, inner.Calls);
        Assert.Empty(h.Pauses);
        Assert.Empty(h.Delays);
    }

    public static TheoryData<Exception, bool> RetryableCases => new()
    {
        { new HttpRequestException("refused"), true },
        { new TaskCanceledException("timeout"), true },
        { new TimeoutException(), true },
        { new JsonException("bad"), false },
        { new InvalidOperationException(), false },
    };

    [Theory]
    [MemberData(nameof(RetryableCases))]
    public void IsRetryable_ClassifiesExceptions(Exception ex, bool expected)
    {
        Assert.Equal(expected, ResilientLlmClient.IsRetryable(ex));
    }
}
