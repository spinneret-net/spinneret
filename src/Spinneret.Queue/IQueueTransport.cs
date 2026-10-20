namespace Spinneret.Queue;

internal interface IQueueTransport
{
    // False when a message with the same MessageName already exists (a duplicate, i.e. success).
    Task<bool> SendAsync(OutboundMessage message, CancellationToken ct);
}

// MessageName: the name the transport deduplicates on, or null for an anonymous message.
internal sealed record OutboundMessage(
    QueueEnvelope Envelope, string Channel, DateTimeOffset? DeliverAt, string? MessageName);
