using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue;

/// <summary>
/// The result of one delivery: <see cref="Ack"/> finishes the task; <see cref="RetryAfter"/> asks for an uncounted redelivery.
/// </summary>
public sealed record QueueDeliveryOutcome
{
    public static QueueDeliveryOutcome Acked { get; } = new();
    public static QueueDeliveryOutcome RetryIn(TimeSpan after) => new() { RetryAfter = after };

    public TimeSpan? RetryAfter { get; private init; }
    public bool Ack => RetryAfter is null;
}

/// <summary>Decides each delivery's outcome (ack, booked retry or dead letter) from its <see cref="QueuePolicy"/>.</summary>
/// <remarks>
/// Attempts are counted in <see cref="QueueEnvelope.PriorFailures"/>, not the transport's retry counter,
/// so infrastructure redeliveries do not spend the budget.
/// </remarks>
public interface IQueueDeliveryProcessor
{
    Task<QueueDeliveryOutcome> ProcessAsync(QueueDeliveryContext context, CancellationToken ct);
}

internal sealed class QueueDeliveryProcessor(
    QueueTypeRegistry registry,
    IQueueDispatcher dispatcher,
    IQueueDispatchBoundary dispatchBoundary,
    IEnvelopeQueue envelopeQueue,
    IDeadLetterWriter deadLetterWriter,
    TimeProvider timeProvider,
    ILogger<QueueDeliveryProcessor> logger)
    : IQueueDeliveryProcessor
{
    private static readonly TimeSpan DeadLetterWriteRetryBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UnknownTypeRetryBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnknownTypeGracePeriod = TimeSpan.FromMinutes(15);

    public async Task<QueueDeliveryOutcome> ProcessAsync(QueueDeliveryContext context, CancellationToken ct)
    {
        var envelope = context.Envelope;
        var taskId = context.TaskId;

        // Spans the whole delivery, since the unknown-type path never reaches dispatch.
        using var activity = QueueTracing.StartConsumer(envelope, taskId);

        QueuePolicy policy;
        try
        {
            policy = registry.Resolve(envelope.RequestTypeName).Policy;
        }
        catch (UnknownRequestTypeException ex)
        {
            return await UnknownTypeAsync(envelope, taskId, ex, activity, ct);
        }

        // Transport redeliveries are infrastructure noise and deliberately do not advance the attempt.
        var attempt = envelope.PriorFailures + 1;
        var age = timeProvider.GetUtcNow() - envelope.EnqueuedAtUtc;

        activity?.SetTag(QueueTags.Destination, policy.ResolvedChannel);
        activity?.SetTag(QueueTags.MaxAttempts, policy.MaxAttempts);

        try
        {
            await dispatchBoundary.ExecuteAsync(context, () => dispatcher.Dispatch(envelope, ct), ct);
            activity.SetOutcome(QueueOutcome.Ack);
            return QueueDeliveryOutcome.Acked;
        }
        catch (QueueHandlerRetryAfterException ex)
        {
            return await DeferAsync(envelope, policy, attempt, age, ex, taskId, activity, ct);
        }
        catch (QueueHandlerPermanentException ex)
        {
            logger.LogError(ex,
                "Queue handler for {RequestType} failed permanently on attempt {Attempt}; dead-lettering",
                envelope.RequestTypeName, attempt);
            activity.SetOutcome(QueueOutcome.DeadLetter, ex.Message);
            return await DeadLetterAsync(envelope, taskId, attempt, ex.Message, activity, ct);
        }
        catch (QueueHandlerFailedException ex)
        {
            // An unknown value (mixed-version deploy) must fail loudly, not silently act like Retry.
            switch (policy.OnErrorResult)
            {
                case ErrorResultAction.DeadLetter:
                    logger.LogError(ex,
                        "Queue handler for {RequestType} returned an error result on attempt {Attempt}; dead-lettering: {@Error}",
                        envelope.RequestTypeName, attempt, ex.Error);
                    activity.SetOutcome(QueueOutcome.DeadLetter, ex.Message);
                    return await DeadLetterAsync(envelope, taskId, attempt, ex.Message, activity, ct);

                case ErrorResultAction.Discard:
                    logger.LogWarning(ex,
                        "Queue handler for {RequestType} returned an error result; discarding per policy: {@Error}",
                        envelope.RequestTypeName, ex.Error);
                    activity.SetOutcome(QueueOutcome.Discard, ex.Message);
                    return QueueDeliveryOutcome.Acked;

                case ErrorResultAction.Retry:
                    return await FailAsync(envelope, policy, attempt, age, ex, taskId, activity, ct);

                default:
                    throw new InvalidOperationException(
                        $"Unknown {nameof(ErrorResultAction)} value {policy.OnErrorResult} on {envelope.RequestTypeName}.");
            }
        }
        catch (Exception ex)
        {
            return await FailAsync(envelope, policy, attempt, age, ex, taskId, activity, ct);
        }
    }

    // An old instance may receive a type a rolling deploy just added, so redeliver for a grace period
    // before dead-lettering. Transport redelivery, since a re-enqueue needs the unknown type's channel.
    private async Task<QueueDeliveryOutcome> UnknownTypeAsync(
        QueueEnvelope envelope, string taskId, UnknownRequestTypeException ex, Activity? activity,
        CancellationToken ct)
    {
        var age = timeProvider.GetUtcNow() - envelope.EnqueuedAtUtc;
        if (age < UnknownTypeGracePeriod)
        {
            logger.LogInformation(
                "Queue task for unknown request type {RequestType} (age {Age}); redelivering in {Backoff} in case a deploy is rolling out",
                envelope.RequestTypeName, age, UnknownTypeRetryBackoff);
            activity.SetOutcome(QueueOutcome.TransportRetry, ex.Message);
            return QueueDeliveryOutcome.RetryIn(UnknownTypeRetryBackoff);
        }

        logger.LogError(ex, "Queue task for unknown request type {RequestType} (age {Age}); dead-lettering",
            envelope.RequestTypeName, age);
        activity.SetOutcome(QueueOutcome.DeadLetter, ex.Message);
        return await DeadLetterAsync(envelope, taskId, envelope.PriorFailures + 1, ex.Message, activity, ct);
    }

    private async Task<QueueDeliveryOutcome> FailAsync(
        QueueEnvelope envelope, QueuePolicy policy, int attempt, TimeSpan age, Exception ex,
        string taskId, Activity? activity, CancellationToken ct)
    {
        if (attempt >= policy.MaxAttempts || age > policy.MaxAge)
        {
            logger.LogError(ex,
                "Queue handler for {RequestType} failed on attempt {Attempt}/{MaxAttempts} (age {Age}); giving up",
                envelope.RequestTypeName, attempt, policy.MaxAttempts, age);
            return await GiveUpAsync(envelope, policy, taskId, attempt, ex.Message, activity, ct);
        }

        var backoff = policy.BackoffFor(attempt);
        try
        {
            // Book the failure as a fresh task so the transport's retry counter never affects the budget.
            await envelopeQueue.Enqueue(envelope with { PriorFailures = attempt }, backoff, ct);
        }
        catch (Exception enqueueEx)
        {
            // Unbooked failure: an occasional extra attempt is benign.
            logger.LogWarning(enqueueEx,
                "Failed to re-enqueue {RequestType} retry; falling back to transport retry in {Backoff}",
                envelope.RequestTypeName, backoff);
            activity.SetOutcome(QueueOutcome.TransportRetry, ex.Message);
            return QueueDeliveryOutcome.RetryIn(backoff);
        }

        logger.LogWarning(ex,
            "Queue handler for {RequestType} failed on attempt {Attempt}/{MaxAttempts}; retrying in {Backoff}",
            envelope.RequestTypeName, attempt, policy.MaxAttempts, backoff);
        activity.SetOutcome(QueueOutcome.Retry, ex.Message);
        return QueueDeliveryOutcome.Acked;
    }

    private async Task<QueueDeliveryOutcome> DeferAsync(
        QueueEnvelope envelope, QueuePolicy policy, int attempt, TimeSpan age,
        QueueHandlerRetryAfterException ex, string taskId, Activity? activity, CancellationToken ct)
    {
        if (age + ex.RetryAfter > policy.MaxAge)
        {
            logger.LogError(ex,
                "Queue handler for {RequestType} deferred by {RetryAfter} but the task (age {Age}) would exceed its max age {MaxAge}; giving up",
                envelope.RequestTypeName, ex.RetryAfter, age, policy.MaxAge);
            return await GiveUpAsync(envelope, policy, taskId, attempt, ex.Message, activity, ct);
        }

        try
        {
            // Pure reschedule: PriorFailures and EnqueuedAtUtc stay, so MaxAge counts from the original enqueue.
            await envelopeQueue.Enqueue(envelope, ex.RetryAfter, ct);
        }
        catch (Exception enqueueEx)
        {
            logger.LogWarning(enqueueEx,
                "Failed to re-enqueue deferred {RequestType}; falling back to transport retry in {RetryAfter}",
                envelope.RequestTypeName, ex.RetryAfter);
            activity.SetOutcome(QueueOutcome.TransportRetry, ex.Message);
            return QueueDeliveryOutcome.RetryIn(ex.RetryAfter);
        }

        logger.LogInformation(
            "Queue handler for {RequestType} deferred; re-enqueued to run in {RetryAfter}",
            envelope.RequestTypeName, ex.RetryAfter);
        activity.SetOutcome(QueueOutcome.Defer);
        return QueueDeliveryOutcome.Acked;
    }

    // Not used for permanent failures: those always dead-letter, since a defect is worth surfacing.
    private async Task<QueueDeliveryOutcome> GiveUpAsync(
        QueueEnvelope envelope, QueuePolicy policy, string taskId, int attempts, string error,
        Activity? activity, CancellationToken ct)
    {
        if (policy.OnExhausted == ExhaustedAction.Discard)
        {
            logger.LogWarning(
                "Discarding exhausted {RequestType} task per policy after {Attempts} attempt(s): {Error}",
                envelope.RequestTypeName, attempts, error);
            activity.SetOutcome(QueueOutcome.Discard, error);
            return QueueDeliveryOutcome.Acked;
        }

        activity.SetOutcome(QueueOutcome.DeadLetter, error);
        return await DeadLetterAsync(envelope, taskId, attempts, error, activity, ct);
    }

    private async Task<QueueDeliveryOutcome> DeadLetterAsync(
        QueueEnvelope envelope, string taskId, int attempts, string error,
        Activity? activity, CancellationToken ct)
    {
        try
        {
            await deadLetterWriter.WriteAsync(new DeadLetterEntry
            {
                IdempotencyKey = taskId,
                Source = DeadLetterSource.Queue,
                CommandTypeName = envelope.RequestTypeName,
                Description = envelope.Description,
                // The producer's trace id, so every attempt and the dead letter answer one query.
                TraceId = QueueTracing.TraceIdOf(envelope.TraceParent) ?? Activity.Current?.TraceId.ToHexString(),
                PayloadJson = envelope.PayloadJson,
                Error = error,
                Attempts = attempts,
            }, ct);

            return QueueDeliveryOutcome.Acked;
        }
        catch (Exception ex)
        {
            // Never drop a task: redeliver until the write lands.
            logger.LogCritical(ex,
                "Failed to write dead-letter for {CommandType} (task {TaskId}, attempts: {Attempts}). Payload: {Payload}",
                envelope.RequestTypeName, taskId, attempts, envelope.PayloadJson);
            activity.SetOutcome(QueueOutcome.TransportRetry, ex.Message);
            return QueueDeliveryOutcome.RetryIn(DeadLetterWriteRetryBackoff);
        }
    }
}
