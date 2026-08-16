using Spinneret.Functional;

namespace Spinneret.Queue;

/// <summary>
/// Puts a dead letter back on the queue and removes it from the store — the "try this again"
/// action on an admin page, optionally with a payload an operator corrected first.
/// </summary>
/// <remarks>
/// Library-implemented; registered by <c>AddQueueCore</c>, so any host with both an
/// <see cref="IQueue"/> and an <see cref="IDeadLetterStore"/> can inject it.
/// </remarks>
public interface IDeadLetterResender
{
    /// <summary>
    /// Re-enqueues the entry filed under <paramref name="idempotencyKey"/>, then deletes it.
    /// </summary>
    /// <param name="idempotencyKey">The entry to resend.</param>
    /// <param name="payloadJson">
    /// Replaces the stored payload — for an operator fixing what made the command fail. Null resends
    /// the payload as recorded. The replacement must deserialize to the entry's own command type;
    /// the type itself is never taken from the caller.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Where the queue can enlist the delete in the enqueue's transaction (SQL Server) the two are
    /// atomic. Where it cannot (Cloud Tasks over Firestore) the enqueue happens first, so an
    /// interruption leaves the entry in place to be resent again rather than losing the work —
    /// at-least-once, matching the delivery guarantee the queue already gives.
    /// </remarks>
    Task<Result<ResendDeadLetterError>> ResendAsync(
        string idempotencyKey, string? payloadJson = null, CancellationToken ct = default);
}

/// <summary>
/// Why a resend did not happen. A closed hierarchy — the private constructor keeps the cases to the
/// ones nested here, so a <c>switch</c> over them stays exhaustive as the library grows.
/// </summary>
public abstract record ResendDeadLetterError
{
    private ResendDeadLetterError()
    {
    }

    /// <summary>No entry under that key — already resent or discarded, possibly by someone else.</summary>
    public sealed record NotFound(string IdempotencyKey) : ResendDeadLetterError;

    /// <summary>
    /// The recorded command type is no longer registered with the queue: it was renamed, moved, or
    /// its assembly is not among the host's request assemblies. The payload is still readable, so
    /// the entry is left in the store rather than being discarded.
    /// </summary>
    public sealed record UnknownCommandType(string CommandTypeName) : ResendDeadLetterError;

    /// <summary>
    /// The payload does not deserialize into the command — nearly always an operator's edit.
    /// <paramref name="Message"/> is the serializer's own complaint, suitable for showing back.
    /// </summary>
    public sealed record InvalidPayload(string Message) : ResendDeadLetterError;
}
