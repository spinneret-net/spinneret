using System.Globalization;

namespace Spinneret.Queue;

/// <summary>
/// How the transport treats a queued handler that returned a <c>Result</c> in error state. An error
/// result is a deterministic business outcome — the same input yields the same error — which is why the
/// default is to dead-letter immediately instead of spending retries on a decision that will not change.
/// </summary>
public enum ErrorResultAction
{
    /// <summary>Write the task to the dead-letter store immediately (default).</summary>
    DeadLetter,

    /// <summary>Treat the error result like a transient failure: retry per the policy.</summary>
    Retry,

    /// <summary>Log and acknowledge the task; the error result is an acceptable non-outcome.</summary>
    Discard,
}

/// <summary>
/// The terminal action when a task's retry budget is exhausted — <see cref="QueuePolicy.MaxAttempts"/>
/// failures reached, or <see cref="QueuePolicy.MaxAge"/> exceeded on a failure or deferral. Distinct
/// from permanent failures, which always dead-letter: exhaustion of a self-healing task (one a
/// recurring sweep redoes anyway) is safe to discard, while a permanent failure is a defect worth
/// surfacing regardless.
/// </summary>
public enum ExhaustedAction
{
    /// <summary>Write the task to the dead-letter store (default).</summary>
    DeadLetter,

    /// <summary>Log and drop the task; something else is known to redo or supersede the work.</summary>
    Discard,
}

/// <summary>
/// Per-command retry policy, declared with <see cref="QueuePolicyAttribute"/> on the command type and
/// resolved through <see cref="QueueTypeRegistry"/>. The application owns task termination: the transport
/// is configured as an effectively unlimited backstop, and every delivery ends in an explicit decision
/// here — acknowledge, retry after a computed backoff, or dead-letter.
/// </summary>
public sealed record QueuePolicy
{
    // 7 attempts at the default backoff spread ~10 minutes of retrying — matching the transport
    // window this design replaced, so an outage short enough for the old queue config to absorb
    // never bulk-dead-letters unannotated commands (notably every outbox delivery).
    public const int DefaultMaxAttempts = 7;
    public const string DefaultMaxAge = "1.00:00:00";
    public const string DefaultMinBackoff = "00:00:10";
    public const string DefaultMaxBackoff = "00:10:00";

    /// <summary>The channel commands ride on when they declare none.</summary>
    public const string DefaultChannel = "default";

    public static readonly QueuePolicy Default = new();

    /// <summary>
    /// Named transport channel this command rides on. Channels are logical names the transport maps
    /// to physical queues (e.g. a rate-limited one); null resolves to <see cref="DefaultChannel"/>.
    /// </summary>
    public string? Channel { get; init; }

    /// <summary>The channel to route to: the declared one, or <see cref="DefaultChannel"/>.</summary>
    public string ResolvedChannel => Channel ?? DefaultChannel;

    /// <summary>
    /// Maximum failed executions before the task is dead-lettered. Deferrals
    /// (<see cref="QueueHandlerRetryAfterException"/>) do not count — they are bounded by
    /// <see cref="MaxAge"/> instead.
    /// </summary>
    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    /// <summary>
    /// Maximum total lifetime of the task measured from <see cref="QueueEnvelope.EnqueuedAtUtc"/>,
    /// spanning retries and deferrals alike. A task older than this is dead-lettered on its next
    /// failure or deferral.
    /// </summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.Parse(DefaultMaxAge, CultureInfo.InvariantCulture);

    /// <summary>Backoff after the first failed execution; doubles per subsequent failure.</summary>
    public TimeSpan MinBackoff { get; init; } = TimeSpan.Parse(DefaultMinBackoff, CultureInfo.InvariantCulture);

    /// <summary>Upper bound the doubling backoff is capped at.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.Parse(DefaultMaxBackoff, CultureInfo.InvariantCulture);

    public ErrorResultAction OnErrorResult { get; init; } = ErrorResultAction.DeadLetter;

    public ExhaustedAction OnExhausted { get; init; } = ExhaustedAction.DeadLetter;

    /// <summary>
    /// The delay before redelivery after failed execution number <paramref name="attempt"/> (1-based),
    /// capped at <see cref="MaxBackoff"/>. Returns the cap directly when doubling would overshoot it,
    /// so no policy/attempt combination can overflow the underlying tick arithmetic.
    /// </summary>
    public TimeSpan BackoffFor(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        if (MinBackoff.Ticks > MaxBackoff.Ticks / factor)
            return MaxBackoff;

        var backoff = MinBackoff * factor;
        return backoff > MaxBackoff ? MaxBackoff : backoff;
    }
}
