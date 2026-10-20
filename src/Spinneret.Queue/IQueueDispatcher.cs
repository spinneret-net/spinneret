namespace Spinneret.Queue;

/// <summary>Runs a delivered envelope's handler; the worker-side counterpart to <see cref="IQueue"/>.</summary>
public interface IQueueDispatcher
{
    Task Dispatch(QueueEnvelope envelope, CancellationToken ct);
}
