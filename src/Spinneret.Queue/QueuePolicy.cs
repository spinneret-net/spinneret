using System.Globalization;

namespace Spinneret.Queue;

/// <summary>How a queued handler's error <c>Result</c> is treated; dead-lettered by default, since retrying can't change it.</summary>
public enum ErrorResultAction
{
    /// <summary>Write the task to the dead-letter store immediately (default).</summary>
    DeadLetter = 0,

    /// <summary>Treat the error result like a transient failure: retry per the policy.</summary>
    Retry = 1,

    /// <summary>Log and acknowledge the task; the error result is an acceptable non-outcome.</summary>
    Discard = 2,
}

/// <summary>What happens when a task exhausts its attempts or age; permanent failures always dead-letter.</summary>
public enum ExhaustedAction
{
    /// <summary>Write the task to the dead-letter store (default).</summary>
    DeadLetter = 0,

    /// <summary>Log and drop the task; something else is known to redo or supersede the work.</summary>
    Discard = 1,
}

/// <summary>Per-command retry policy, declared with <see cref="QueuePolicyAttribute"/>.</summary>
public sealed record QueuePolicy
{
    // 7 attempts at the default backoff span ~10 minutes, so a short outage doesn't bulk-dead-letter.
    // static readonly, not const, so a changed default isn't baked into compiled consumers.
    public static readonly int DefaultMaxAttempts = 7;
    public static readonly string DefaultMaxAge = "1.00:00:00";
    public static readonly string DefaultMinBackoff = "00:00:10";
    public static readonly string DefaultMaxBackoff = "00:10:00";

    /// <summary>The channel commands ride on when they declare none.</summary>
    public const string DefaultChannel = "default";

    public static QueuePolicy Default { get; } = new();

    /// <summary>Logical channel the transport maps to a physical queue; null means <see cref="DefaultChannel"/>.</summary>
    public string? Channel { get; init; }

    /// <summary>The channel to route to: the declared one, or <see cref="DefaultChannel"/>.</summary>
    public string ResolvedChannel => Channel ?? DefaultChannel;

    /// <summary>Maximum failed executions before giving up; deferrals don't count.</summary>
    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    /// <summary>
    /// Maximum lifetime from <see cref="QueueEnvelope.EnqueuedAtUtc"/>; an older task gives up on its next failure or deferral.
    /// </summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.Parse(DefaultMaxAge, CultureInfo.InvariantCulture);

    /// <summary>Backoff after the first failed execution; doubles per subsequent failure.</summary>
    public TimeSpan MinBackoff { get; init; } = TimeSpan.Parse(DefaultMinBackoff, CultureInfo.InvariantCulture);

    /// <summary>Upper bound the doubling backoff is capped at.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.Parse(DefaultMaxBackoff, CultureInfo.InvariantCulture);

    public ErrorResultAction OnErrorResult { get; init; } = ErrorResultAction.DeadLetter;

    public ExhaustedAction OnExhausted { get; init; } = ExhaustedAction.DeadLetter;

    /// <summary>
    /// The delay after failed execution number <paramref name="attempt"/> (1-based), capped at <see cref="MaxBackoff"/>.
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
