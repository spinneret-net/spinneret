namespace Spinneret.Queue;

/// <summary>
/// Thrown by <see cref="QueueDispatcher"/> when the handler returned a <c>Result&lt;...&gt;</c> in error
/// state. What happens next is the command's <see cref="QueuePolicy.OnErrorResult"/> decision — by
/// default the task is dead-lettered immediately, since an error result is a deterministic business
/// outcome that retrying cannot change.
/// </summary>
public sealed class QueueHandlerFailedException(object? error)
    : Exception($"Queue handler returned an error result: {error}")
{
    public object? Error { get; } = error;
}
