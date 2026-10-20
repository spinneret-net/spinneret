namespace Spinneret.Queue;

/// <summary>
/// Enqueues an already-built envelope, e.g. to re-enqueue a deferred delivery; producers use <see cref="IQueue"/>.
/// </summary>
public interface IEnvelopeQueue
{
    Task Enqueue(QueueEnvelope envelope, TimeSpan? delay = null, CancellationToken ct = default);
}
