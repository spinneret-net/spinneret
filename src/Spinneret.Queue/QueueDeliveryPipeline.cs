using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue;

// Shared delivery tail for every transport. A non-ack outcome returns without committing,
// so the unit rolls back and the message is redelivered as it was.
internal sealed class QueueDeliveryPipeline(
    IQueueDeliveryProcessor processor,
    IDeadLetterWriter deadLetterWriter,
    ILogger<QueueDeliveryPipeline> logger)
{
    internal const string UnreadableEnvelopeTypeName = "<unreadable envelope>";

    private static readonly TimeSpan UnreadableEnvelopeRetryBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CommitFailureRetryBackoff = TimeSpan.FromSeconds(5);

    public async Task<QueueDeliveryOutcome> DeliverAsync(
        IQueueUnitOfWork unit, string rawEnvelope, string taskId, CancellationToken ct)
    {
        var envelope = TryRead(rawEnvelope, out var readError);
        if (envelope is null)
            return await DeadLetterUnreadableAsync(unit, rawEnvelope, taskId, readError, ct);

        var outcome = await processor.ProcessAsync(
            new QueueDeliveryContext { Envelope = envelope, TaskId = taskId }, ct);

        return outcome.Ack ? await CommitAsync(unit, envelope.RequestTypeName, taskId) : outcome;
    }

    private static QueueEnvelope? TryRead(string rawEnvelope, out string error)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<QueueEnvelope>(rawEnvelope);
            error = "Envelope deserialized to null.";
            return envelope;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // No retry can fix unreadable bytes: dead-letter the raw body, and if that fails, redeliver rather than drop.
    private async Task<QueueDeliveryOutcome> DeadLetterUnreadableAsync(
        IQueueUnitOfWork unit, string rawEnvelope, string taskId, string error, CancellationToken ct)
    {
        logger.LogError("Received unreadable queue envelope (task {TaskId}): {Error}", taskId, error);

        try
        {
            await deadLetterWriter.WriteAsync(new DeadLetterEntry
            {
                IdempotencyKey = taskId,
                Source = DeadLetterSource.Queue,
                CommandTypeName = UnreadableEnvelopeTypeName,
                PayloadJson = rawEnvelope,
                Error = error,
                Attempts = 1,
            }, ct);

            await unit.CommitAsync(CancellationToken.None);
            return QueueDeliveryOutcome.Acked;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to dead-letter unreadable envelope (task {TaskId}); it will be redelivered. Body: {Body}",
                taskId, rawEnvelope);
            return QueueDeliveryOutcome.RetryIn(UnreadableEnvelopeRetryBackoff);
        }
    }

    // Not cancellable: the work is done. A failed commit discards everything, so redeliver without spending an attempt.
    private async Task<QueueDeliveryOutcome> CommitAsync(IQueueUnitOfWork unit, string requestType, string taskId)
    {
        try
        {
            await unit.CommitAsync(CancellationToken.None);
            return QueueDeliveryOutcome.Acked;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Committing the delivery of {RequestType} (task {TaskId}) failed; redelivering in {Backoff}",
                requestType, taskId, CommitFailureRetryBackoff);
            return QueueDeliveryOutcome.RetryIn(CommitFailureRetryBackoff);
        }
    }
}
