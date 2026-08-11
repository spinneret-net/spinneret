namespace Spinneret.Queue;

/// <summary>
/// Transport-level enqueue of an already-built envelope. Used to re-enqueue a deferred delivery as a
/// fresh task so the wait does not consume transport retry attempts, while
/// <see cref="QueueEnvelope.EnqueuedAtUtc"/> and <see cref="QueueEnvelope.PriorFailures"/> carry the
/// task's history across generations. Producers enqueue requests through <see cref="IQueue"/>.
/// </summary>
public interface IEnvelopeQueue
{
    Task Enqueue(QueueEnvelope envelope, TimeSpan? delay = null, CancellationToken ct = default);
}
