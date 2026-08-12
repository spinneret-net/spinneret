namespace Spinneret.Queue;

/// <summary>
/// Brackets the handler invocation of one queue delivery, between the transport handing the
/// envelope to <see cref="IQueueDeliveryProcessor"/> and the processor booking the outcome.
/// Transports use this to scope transactional state to the handler alone — e.g. the MSSQL
/// transport sets a transaction savepoint here, so a failed handler's partial writes are rolled
/// back while the delivery transaction stays usable for booking the retry or dead-letter
/// atomically with the dequeue. The default implementation invokes the handler directly.
/// </summary>
public interface IQueueDispatchBoundary
{
    /// <param name="envelope">The envelope being delivered.</param>
    /// <param name="dispatch">Invokes the handler; exceptions must propagate to the caller.</param>
    /// <param name="ct"></param>
    Task ExecuteAsync(QueueEnvelope envelope, Func<Task> dispatch, CancellationToken ct);
}

/// <summary>
/// The default pass-through boundary. Public — unlike other default implementations — so a
/// transport can recognize it in the service collection and replace it with its own boundary
/// without clobbering a custom one the host registered deliberately.
/// </summary>
public sealed class DirectDispatchBoundary : IQueueDispatchBoundary
{
    public Task ExecuteAsync(QueueEnvelope envelope, Func<Task> dispatch, CancellationToken ct) => dispatch();
}
