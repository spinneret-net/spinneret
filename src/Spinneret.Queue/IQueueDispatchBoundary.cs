namespace Spinneret.Queue;

// Brackets the handler call so storage can roll back its writes (e.g. a SQL savepoint)
// while the delivery transaction stays usable for booking the outcome.
internal interface IQueueDispatchBoundary
{
    // dispatch's exceptions must propagate to the caller.
    Task ExecuteAsync(QueueDeliveryContext context, Func<Task> dispatch, CancellationToken ct);
}

internal sealed class DirectDispatchBoundary : IQueueDispatchBoundary
{
    public Task ExecuteAsync(QueueDeliveryContext context, Func<Task> dispatch, CancellationToken ct) => dispatch();
}
