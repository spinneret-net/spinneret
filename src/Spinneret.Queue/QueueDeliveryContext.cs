namespace Spinneret.Queue;

/// <summary>
/// Everything a transport knows about one delivery, passed to
/// <see cref="IQueueDeliveryProcessor"/> and <see cref="IQueueDispatchBoundary"/>.
/// New members are added as optional init-only properties so existing transports keep compiling.
/// </summary>
public sealed record QueueDeliveryContext
{
    /// <summary>The delivered envelope.</summary>
    public required QueueEnvelope Envelope { get; init; }

    /// <summary>
    /// Transport task id for this delivery, stable across redeliveries of the same task —
    /// used as the dead-letter idempotency key.
    /// </summary>
    public required string TaskId { get; init; }
}
