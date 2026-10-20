using Spinneret.Functional;

namespace Spinneret.Queue;

/// <summary>Puts a dead letter back on the queue and removes it from the store.</summary>
public interface IDeadLetterResender
{
    /// <summary>
    /// Re-enqueues the entry filed under <paramref name="idempotencyKey"/>, then deletes it.
    /// </summary>
    /// <param name="idempotencyKey">The entry to resend.</param>
    /// <param name="payloadJson">
    /// Replacement payload, deserialized as the entry's own command type; null resends the recorded one.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Atomic where the store shares the queue's transaction; otherwise the enqueue happens first,
    /// so an interruption leaves the entry to resend rather than losing it.
    /// </remarks>
    Task<Result<ResendDeadLetterError>> ResendAsync(
        string idempotencyKey, string? payloadJson = null, CancellationToken ct = default);
}

/// <summary>Why a resend did not happen; a closed hierarchy, so a <c>switch</c> stays exhaustive.</summary>
public abstract record ResendDeadLetterError
{
    private ResendDeadLetterError()
    {
    }

    /// <summary>No entry under that key, possibly already resent or discarded.</summary>
    public sealed record NotFound(string IdempotencyKey) : ResendDeadLetterError;

    /// <summary>The recorded command type is no longer registered; the entry is left in the store.</summary>
    public sealed record UnknownCommandType(string CommandTypeName) : ResendDeadLetterError;

    /// <summary>
    /// The payload does not deserialize; <paramref name="Message"/> is the serializer's error.
    /// </summary>
    public sealed record InvalidPayload(string Message) : ResendDeadLetterError;
}
