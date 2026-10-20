using System.Text.Json;
using Spinneret.Functional;

namespace Spinneret.Queue;

internal sealed class DeadLetterResender(
    IDeadLetterStore store,
    IQueue queue,
    QueueTypeRegistry registry,
    IQueuePayloadSerializer serializer,
    IQueueUnitOfWorkFactory unitOfWork)
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
        // Hand-edited payloads make bad JSON an expected outcome, not a bug.
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Result.Error<ResendDeadLetterError>(new ResendDeadLetterError.InvalidPayload(ex.Message));
        }

        if (request is null)
            return Result.Error<ResendDeadLetterError>(
                new ResendDeadLetterError.InvalidPayload(
                    $"Payload deserialized to null for command type '{deadLetter.CommandTypeName}'."));

        await unitOfWork.ExecuteAndCommitAsync(async token =>
        {
            // Enqueue before delete: if the delete fails the entry survives to be resent, never lost.
            await ResolvedRequestEnqueuer.Enqueue(queue, request, registered.ResponseType, token);
            await store.DeleteAsync(idempotencyKey, token);
        }, ct);

        return Result.Ok<ResendDeadLetterError>();
    }
}
