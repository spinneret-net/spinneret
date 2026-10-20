namespace Spinneret.Queue;

/// <summary>
/// The outbound wire of a push transport such as Cloud Tasks: hands one message to the transport,
/// which later delivers it to the host's dispatch endpoint. Used by <see cref="DirectQueue"/> when
/// the host has no message storage, and by a storage package's relay when it does.
/// </summary>
/// <remarks>Internal until a second transport gives the shape a reason to be published.</remarks>
internal interface IQueueTransport
{
    /// <summary>
    /// Sends <paramref name="message"/>. Returns false when the transport already holds a message
    /// under <see cref="OutboundMessage.MessageName"/> — a duplicate, which is success rather than an
    /// error — and true when it created one. Throws on any other failure.
    /// </summary>
    Task<bool> SendAsync(OutboundMessage message, CancellationToken ct);
}

/// <param name="Envelope">The envelope to deliver, already stamped with its trace context.</param>
/// <param name="Channel">The channel the request type rides, resolved from its <see cref="QueuePolicy"/>.</param>
/// <param name="DeliverAt">The earliest delivery time, or null to deliver as soon as possible.</param>
/// <param name="MessageName">
/// A stable name the transport deduplicates on, or null for an anonymous message. A
/// <see cref="QueueOptions.DedupeKey"/> when the producer gave one.
/// </param>
internal sealed record OutboundMessage(
    QueueEnvelope Envelope, string Channel, DateTimeOffset? DeliverAt, string? MessageName);
