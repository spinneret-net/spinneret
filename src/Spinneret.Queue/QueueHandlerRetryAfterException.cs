namespace Spinneret.Queue;

/// <summary>
/// Thrown by a handler to defer the task — an upstream rate limit, a paused integration, a precondition
/// that time will satisfy. A deferral is a wait, not a failure: the delivery is re-enqueued as a fresh
/// task scheduled <see cref="RetryAfter"/> from now, so it never consumes the policy's
/// <see cref="QueuePolicy.MaxAttempts"/>. Only <see cref="QueuePolicy.MaxAge"/> bounds how long a task
/// may keep deferring.
/// </summary>
public sealed class QueueHandlerRetryAfterException(TimeSpan retryAfter, string? message = null, Exception? inner = null)
    : Exception(message ?? $"Queue handler requested a retry in {retryAfter.TotalSeconds:N0}s.", inner)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
