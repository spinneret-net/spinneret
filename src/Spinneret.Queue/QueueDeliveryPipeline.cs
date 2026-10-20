using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue;

/// <summary>
/// The tail every delivery shares, whichever transport brought it: read the envelope, dead-letter
/// it if it cannot be read, hand it to <see cref="IQueueDeliveryProcessor"/>, and commit the unit of
/// work when the outcome is an acknowledge. The transport only translates the outcome — an HTTP
/// status for Cloud Tasks, a commit or a reschedule for the in-host worker.
/// </summary>
/// <remarks>
/// A non-acknowledged outcome is returned without committing, so the caller's unit rolls back: the
/// processor could not book the outcome, and the transport is to redeliver the message as it was.
/// </remarks>
internal sealed class QueueDeliveryPipeline(
    IQueueDeliveryProcessor processor,
    IDeadLetterWriter deadLetterWriter,
    ILogger<QueueDeliveryPipeline> logger)
{
    /// <summary>The CommandTypeName recorded for a message whose envelope could not be read.</summary>
    internal const string UnreadableEnvelopeTypeName = "<unreadable envelope>";

    private static readonly TimeSpan UnreadableEnvelopeRetryBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CommitFailureRetryBackoff = TimeSpan.FromSeconds(5);

    /// <param name="unit">The unit of work the delivery runs in, opened by the transport.</param>
    /// <param name="rawEnvelope">The message body as the transport received it.</param>
    /// <param name="taskId">The transport's id for the message, stable across redeliveries.</param>
    /// <param name="ct">Cancellation token.</param>
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

    /// <summary>
    /// A message whose envelope cannot even be read is permanently broken — no retry can repair the
    /// bytes. Dead-letter the raw body for inspection instead of retrying or dropping it, and if even
    /// that write fails, redeliver later: never drop a message without a trace.
    /// </summary>
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

    /// <summary>
    /// Makes the acknowledged delivery durable. Not cancellable: the outcome is decided and its
    /// writes are done, so stopping now would only throw that work away. A failed commit leaves
    /// nothing behind — the handler's writes and the outcome's booking go with it — so the message
    /// is redelivered as it was, without spending an attempt.
    /// </summary>
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
