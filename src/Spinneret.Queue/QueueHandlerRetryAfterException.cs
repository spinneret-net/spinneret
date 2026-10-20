namespace Spinneret.Queue;

/// <summary>
/// Thrown to defer the task by <see cref="RetryAfter"/>; deferrals spend no attempts and are bounded only by <see cref="QueuePolicy.MaxAge"/>.
/// </summary>
public sealed class QueueHandlerRetryAfterException(TimeSpan retryAfter, string? message = null, Exception? inner = null)
    : Exception(message ?? $"Queue handler requested a retry in {retryAfter.TotalSeconds:N0}s.", inner)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
