namespace Spinneret.Queue;

/// <summary>
/// Server-side counterpart to <see cref="IQueue"/>. Invoked by the transport's HTTP
/// endpoint when a task is delivered.
/// </summary>
public interface IQueueDispatcher
{
    Task Dispatch(QueueEnvelope envelope, CancellationToken ct);
}
