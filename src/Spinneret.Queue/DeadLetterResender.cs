using System.Text.Json;
using Spinneret.Functional;

namespace Spinneret.Queue;

/// <summary>
/// Resend, in terms every transport already provides: read the entry, resolve its command type,
/// deserialize, enqueue, delete. Transport-specific only in how tightly the enqueue and the delete
/// are bound together, which <see cref="IQueueTransactionScope"/> supplies.
/// </summary>
internal sealed class DeadLetterResender(
    IDeadLetterStore store,
    IQueue queue,
    QueueTypeRegistry registry,
    IQueuePayloadSerializer serializer,
    IQueueTransactionScope transactionScope)
    : IDeadLetterResender
{
    public async Task<Result<ResendDeadLetterError>> ResendAsync(
        string idempotencyKey, string? payloadJson = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var deadLetter = await store.GetAsync(idempotencyKey, ct);
        if (deadLetter is null)
            return Result.Error<ResendDeadLetterError>(new ResendDeadLetterError.NotFound(idempotencyKey));

        QueueTypeRegistry.RegisteredRequest registered;
        try
        {
            registered = registry.Resolve(deadLetter.CommandTypeName);
        }
        catch (UnknownRequestTypeException)
        {
            return Result.Error<ResendDeadLetterError>(
                new ResendDeadLetterError.UnknownCommandType(deadLetter.CommandTypeName));
        }

        object? request;
        try
        {
            request = serializer.Deserialize(payloadJson ?? deadLetter.PayloadJson, registered.RequestType);
        }
        // A resent payload may have been edited by hand, so malformed JSON is an expected outcome
        // here rather than a bug — reported back to the operator instead of thrown. NotSupportedException
        // covers a payload whose shape the serializer cannot map onto the command at all.
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Result.Error<ResendDeadLetterError>(new ResendDeadLetterError.InvalidPayload(ex.Message));
        }

        if (request is null)
            return Result.Error<ResendDeadLetterError>(
                new ResendDeadLetterError.InvalidPayload(
                    $"Payload deserialized to null for command type '{deadLetter.CommandTypeName}'."));

        await transactionScope.ExecuteAsync(async token =>
        {
            // Enqueue first: should the delete not happen, the entry is still there to resend, which
            // beats a delete that succeeded ahead of an enqueue that did not and took the payload
            // with it. Under a transactional scope neither is visible until both have committed.
            await ResolvedRequestEnqueuer.Enqueue(queue, request, registered.ResponseType, token);
            await store.DeleteAsync(idempotencyKey, token);
        }, ct);

        return Result.Ok<ResendDeadLetterError>();
    }
}
