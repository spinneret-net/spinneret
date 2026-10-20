namespace Spinneret.Queue;

/// <summary>
/// Thrown when a handler returns an error <c>Result</c>; handled per <see cref="QueuePolicy.OnErrorResult"/> (dead-letter by default).
/// </summary>
public sealed class QueueHandlerFailedException(object? error)
    : Exception($"Queue handler returned an error result: {error}")
{
    public object? Error { get; } = error;
}
