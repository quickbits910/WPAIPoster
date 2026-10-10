using WPAIPoster.Config;

namespace WPAIPoster.Llm;

/// <summary>An LLM endpoint that has exhausted its retries, handed to the pause/recovery prompt.</summary>
/// <param name="Label">Which client failed ("text" / "vision").</param>
/// <param name="Endpoint">Base URL of the endpoint (null = provider default).</param>
/// <param name="Model">Model the requests were sent to.</param>
/// <param name="Attempts">How many attempts failed before the pause.</param>
/// <param name="LastError">One-line description of the last failure.</param>
/// <param name="RecoveryWait">How long to wait for the user to fix it before the final attempt.</param>
public sealed record LlmOutage(
    string Label, string? Endpoint, string Model, int Attempts, string LastError, TimeSpan RecoveryWait);

/// <summary>Retry/recovery tuning for <see cref="ResilientLlmClient"/>.</summary>
/// <param name="RecoveryWait">Pause window after the retries fail; zero/negative disables the pause.</param>
/// <param name="Attempts">Attempts before pausing (at least 1).</param>
/// <param name="Backoff">Delay before each retry (index = retry number - 1; the last entry repeats).</param>
public sealed record LlmRecoveryOptions(TimeSpan RecoveryWait, int Attempts, IReadOnlyList<TimeSpan> Backoff)
{
    public static LlmRecoveryOptions Default(TimeSpan recoveryWait) =>
        new(recoveryWait, AppLimits.LlmRetryAttempts, AppLimits.LlmRetryBackoff);
}

/// <summary>
/// An <see cref="ILlmClient"/> decorator that rides out an LLM endpoint outage (LM Studio OOM-killed, or a
/// <c>400 "Failed to load model"</c> because the model can't fit in memory). A failed request is retried
/// with a short backoff; if every attempt fails, <c>waitForRecovery</c> pauses the run so the user can fix
/// the server (it returns when they press Enter or the window ends, or <c>false</c> to skip), then one
/// final attempt is made. If that also fails the endpoint is <em>latched down</em>: later calls try once
/// with no retries or pause, so a loop over many requests (vision scoring) doesn't pause per request. Any
/// success clears the latch. Only transport-level failures (<see cref="IsRetryable"/>) are retried.
/// </summary>
public sealed class ResilientLlmClient(
    ILlmClient inner,
    string label,
    string? endpoint,
    string model,
    LlmRecoveryOptions options,
    Func<LlmOutage, Task<bool>> waitForRecovery,
    Action<string>? onRetry = null,
    Func<TimeSpan, Task>? delay = null) : ILlmClient
{
    private readonly Func<TimeSpan, Task> _delay = delay ?? (d => Task.Delay(d));

    /// <summary>True after a final (post-pause) attempt failed and no call has succeeded since.</summary>
    public bool IsLatchedDown { get; private set; }

    public Task<string?> SendAsync(string promptText, string? base64Image, string? mimeType)
        => ExecuteAsync(() => inner.SendAsync(promptText, base64Image, mimeType));

    public Task<string?> SendAsync(string promptText, IReadOnlyList<(string Base64, string MimeType)> images)
        => ExecuteAsync(() => inner.SendAsync(promptText, images));

    private async Task<string?> ExecuteAsync(Func<Task<string?>> call)
    {
        if (IsLatchedDown)
            return await AttemptFinalAsync(call);

        int attempts = Math.Max(1, options.Attempts);
        Exception? last = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                TimeSpan backoff = BackoffFor(attempt - 1);
                onRetry?.Invoke($"{Name} request failed ({LlmHttpErrors.Describe(last!)}) — retry {attempt - 1}/{attempts - 1} in {backoff.TotalSeconds:0}s");
                if (backoff > TimeSpan.Zero)
                    await _delay(backoff);
            }

            try
            {
                return await call();
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                last = ex;
            }
        }

        if (options.RecoveryWait <= TimeSpan.Zero)
            throw Rethrow(last!);

        var outage = new LlmOutage(label, endpoint, model, attempts, LlmHttpErrors.Describe(last!), options.RecoveryWait);
        if (!await waitForRecovery(outage))
        {
            IsLatchedDown = true;
            throw Rethrow(last!);
        }

        return await AttemptFinalAsync(call);
    }

    /// <summary>One attempt with no retries: success clears the latch, a retryable failure sets it.</summary>
    private async Task<string?> AttemptFinalAsync(Func<Task<string?>> call)
    {
        try
        {
            string? reply = await call();
            IsLatchedDown = false;
            return reply;
        }
        catch (Exception ex) when (IsRetryable(ex))
        {
            IsLatchedDown = true;
            throw;
        }
    }

    private string Name => string.IsNullOrEmpty(label) ? "LLM" : $"{char.ToUpperInvariant(label[0])}{label[1..]}";

    private TimeSpan BackoffFor(int retry)
    {
        IReadOnlyList<TimeSpan> b = options.Backoff;
        if (b.Count == 0)
            return TimeSpan.Zero;
        return b[Math.Min(retry - 1, b.Count - 1)];
    }

    // Preserve the original stack trace when rethrowing outside its catch block.
    private static Exception Rethrow(Exception ex)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        return ex; // unreachable
    }

    /// <summary>
    /// Pure: whether <paramref name="ex"/> is an endpoint/transport failure worth retrying — an
    /// <see cref="HttpRequestException"/> (connection refused/reset, or a non-success status such as LM
    /// Studio's 400 "Failed to load model") or an HTTP timeout (<see cref="TaskCanceledException"/> /
    /// <see cref="TimeoutException"/>). Anything else (e.g. a <see cref="System.Text.Json.JsonException"/>
    /// reading the reply) is a bug or bad reply, not an outage, and is surfaced immediately.
    /// </summary>
    public static bool IsRetryable(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException;
}
