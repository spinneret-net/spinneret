namespace Spinneret.Queue;

/// <summary>What a transport knows about one delivery.</summary>
public sealed record QueueDeliveryContext
{
    /// <summary>The delivered envelope.</summary>
    public required QueueEnvelope Envelope { get; init; }

    /// <summary>Transport task id, stable across redeliveries; used as the dead-letter key.</summary>
    public required string TaskId { get; init; }
}
