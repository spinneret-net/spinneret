using Microsoft.Extensions.Logging;

namespace Spinneret.Queue;

/// <summary>
/// Outcome of processing one queue delivery. <see cref="Ack"/> means the task is finished from the
/// transport's perspective — succeeded, dead-lettered, discarded, or re-enqueued as a fresh task (a
/// booked retry or a deferral). <see cref="RetryAfter"/> is the fallback when booking that outcome
/// failed (the re-enqueue or dead-letter write threw): the transport should redeliver after the
/// delay, and that redelivery is deliberately not counted as a failure.
/// </summary>
public sealed record QueueDeliveryOutcome
{
    public static QueueDeliveryOutcome Acked { get; } = new();
    public static QueueDeliveryOutcome RetryIn(TimeSpan after) => new() { RetryAfter = after };

    public TimeSpan? RetryAfter { get; private init; }
    public bool Ack => RetryAfter is null;
}

/// <summary>
/// The transport-agnostic decision engine for queue deliveries. The application — not the transport —
/// owns task termination: every delivery ends in an explicit acknowledge, a retry re-enqueued with a
/// backoff computed from the command's <see cref="QueuePolicy"/>, or a dead-letter. The failure budget
/// is tracked exclusively in <see cref="QueueEnvelope.PriorFailures"/>, never derived from the
/// transport's retry counter: the transport redelivers only when the app never acknowledged (an
/// outage, a crash, an auth failure before this code ran), and such infrastructure noise must not
/// spend the handler's attempts. Those uncounted loops are bounded by <see cref="QueuePolicy.MaxAge"/>
/// once deliveries reach this code again, and by the transport's max retry duration if they never do.
/// </summary>
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

    public async Task<QueueDeliveryOutcome> ProcessAsync(QueueDeliveryContext context, CancellationToken ct)
    {
        var envelope = context.Envelope;
        var taskId = context.TaskId;

        QueuePolicy policy;
        try
        {
            policy = registry.Resolve(envelope.RequestTypeName).Policy;
        }
        catch (UnknownRequestTypeException ex)
        {
            // Producer and consumer are out of sync (e.g. a command type renamed across a deploy).
            // No retry can resolve a type that no longer exists.
            logger.LogError(ex, "Queue task for unknown request type {RequestType}; dead-lettering",
                envelope.RequestTypeName);

            return await DeadLetterAsync(envelope, taskId, envelope.PriorFailures + 1, ex.Message, ct);
        }

        // PriorFailures counts only failures this processor observed and booked via re-enqueue.
        // A transport redelivery of the same task means the app never acknowledged (an outage, a
        // crash, an auth failure before this code ran) — infrastructure noise that deliberately
        // does not advance the attempt.
        var attempt = envelope.PriorFailures + 1;
        var age = timeProvider.GetUtcNow() - envelope.EnqueuedAtUtc;

        try
        {
            await dispatchBoundary.ExecuteAsync(context, () => dispatcher.Dispatch(envelope, ct), ct);
            return QueueDeliveryOutcome.Acked;
        }
        catch (QueueHandlerRetryAfterException ex)
        {
            return await DeferAsync(envelope, policy, attempt, age, ex, taskId, ct);
        }
        catch (QueueHandlerPermanentException ex)
        {
            logger.LogError(ex,
                "Queue handler for {RequestType} failed permanently on attempt {Attempt}; dead-lettering",
                envelope.RequestTypeName, attempt);
            return await DeadLetterAsync(envelope, taskId, attempt, ex.Message, ct);
        }
        catch (QueueHandlerFailedException ex)
        {
            // Every ErrorResultAction is handled explicitly: a value this build does not know
            // (mixed-version deploy) must fail loudly rather than silently behave like Retry.
            switch (policy.OnErrorResult)
            {
                case ErrorResultAction.DeadLetter:
                    logger.LogError(ex,
                        "Queue handler for {RequestType} returned an error result on attempt {Attempt}; dead-lettering: {@Error}",
                        envelope.RequestTypeName, attempt, ex.Error);
                    return await DeadLetterAsync(envelope, taskId, attempt, ex.Message, ct);

                case ErrorResultAction.Discard:
                    logger.LogWarning(ex,
                        "Queue handler for {RequestType} returned an error result; discarding per policy: {@Error}",
                        envelope.RequestTypeName, ex.Error);
                    return QueueDeliveryOutcome.Acked;

                case ErrorResultAction.Retry:
                    return await FailAsync(envelope, policy, attempt, age, ex, taskId, ct);

                default:
                    throw new InvalidOperationException(
                        $"Unknown {nameof(ErrorResultAction)} value {policy.OnErrorResult} on {envelope.RequestTypeName}.");
            }
        }
        catch (Exception ex)
        {
            return await FailAsync(envelope, policy, attempt, age, ex, taskId, ct);
        }
    }

    private async Task<QueueDeliveryOutcome> FailAsync(
        QueueEnvelope envelope, QueuePolicy policy, int attempt, TimeSpan age, Exception ex,
        string taskId, CancellationToken ct)
    {
        if (attempt >= policy.MaxAttempts || age > policy.MaxAge)
        {
            logger.LogError(ex,
                "Queue handler for {RequestType} failed on attempt {Attempt}/{MaxAttempts} (age {Age}); giving up",
                envelope.RequestTypeName, attempt, policy.MaxAttempts, age);
            return await GiveUpAsync(envelope, policy, taskId, attempt, ex.Message, ct);
        }

        var backoff = policy.BackoffFor(attempt);
        try
        {
            // Book the failure by re-enqueueing a fresh task with the incremented count, exactly like
            // deferrals — the transport's own retry counter (which also counts deliveries that never
            // reached the app) then never influences the budget.
            await envelopeQueue.Enqueue(envelope with { PriorFailures = attempt }, backoff, ct);
        }
        catch (Exception enqueueEx)
        {
            // Fall back to transport redelivery. The failure goes unbooked — one occasional extra
            // attempt is benign, unlike the reverse (infrastructure noise spending the budget).
            logger.LogWarning(enqueueEx,
                "Failed to re-enqueue {RequestType} retry; falling back to transport retry in {Backoff}",
                envelope.RequestTypeName, backoff);
            return QueueDeliveryOutcome.RetryIn(backoff);
        }

        logger.LogWarning(ex,
            "Queue handler for {RequestType} failed on attempt {Attempt}/{MaxAttempts}; retrying in {Backoff}",
            envelope.RequestTypeName, attempt, policy.MaxAttempts, backoff);
        return QueueDeliveryOutcome.Acked;
    }

    private async Task<QueueDeliveryOutcome> DeferAsync(
        QueueEnvelope envelope, QueuePolicy policy, int attempt, TimeSpan age,
        QueueHandlerRetryAfterException ex, string taskId, CancellationToken ct)
    {
        if (age + ex.RetryAfter > policy.MaxAge)
        {
            logger.LogError(ex,
                "Queue handler for {RequestType} deferred by {RetryAfter} but the task (age {Age}) would exceed its max age {MaxAge}; giving up",
                envelope.RequestTypeName, ex.RetryAfter, age, policy.MaxAge);
            return await GiveUpAsync(envelope, policy, taskId, attempt, ex.Message, ct);
        }

        try
        {
            // A deferral is a pure reschedule: PriorFailures is unchanged (this execution deferred,
            // it did not fail) and EnqueuedAtUtc keeps MaxAge measuring from the original enqueue.
            await envelopeQueue.Enqueue(envelope, ex.RetryAfter, ct);
        }
        catch (Exception enqueueEx)
        {
            // The current task remains the timer: redeliver it after the requested delay instead.
            logger.LogWarning(enqueueEx,
                "Failed to re-enqueue deferred {RequestType}; falling back to transport retry in {RetryAfter}",
                envelope.RequestTypeName, ex.RetryAfter);
            return QueueDeliveryOutcome.RetryIn(ex.RetryAfter);
        }

        logger.LogInformation(
            "Queue handler for {RequestType} deferred; re-enqueued to run in {RetryAfter}",
            envelope.RequestTypeName, ex.RetryAfter);
        return QueueDeliveryOutcome.Acked;
    }

    /// <summary>
    /// The retry budget (attempts or age) is spent. Permanent failures never come through here —
    /// they dead-letter unconditionally, because a defect is worth surfacing even for work that
    /// something else redoes.
    /// </summary>
    private async Task<QueueDeliveryOutcome> GiveUpAsync(
        QueueEnvelope envelope, QueuePolicy policy, string taskId, int attempts, string error, CancellationToken ct)
    {
        if (policy.OnExhausted == ExhaustedAction.Discard)
        {
            logger.LogWarning(
                "Discarding exhausted {RequestType} task per policy after {Attempts} attempt(s): {Error}",
                envelope.RequestTypeName, attempts, error);
            return QueueDeliveryOutcome.Acked;
        }

        return await DeadLetterAsync(envelope, taskId, attempts, error, ct);
    }

    private async Task<QueueDeliveryOutcome> DeadLetterAsync(
        QueueEnvelope envelope, string taskId, int attempts, string error, CancellationToken ct)
    {
        try
        {
            await deadLetterWriter.WriteAsync(new DeadLetterEntry
            {
                IdempotencyKey = taskId,
                Source = DeadLetterSource.Queue,
                CommandTypeName = envelope.RequestTypeName,
                Description = envelope.Description,
                PayloadJson = envelope.PayloadJson,
                Error = error,
                Attempts = attempts,
            }, ct);

            return QueueDeliveryOutcome.Acked;
        }
        catch (Exception ex)
        {
            // Never drop a task because the dead-letter store is unavailable — keep redelivering until
            // the write lands (the transport's max retry duration is the only backstop).
            logger.LogCritical(ex,
                "Failed to write dead-letter for {CommandType} (task {TaskId}, attempts: {Attempts}). Payload: {Payload}",
                envelope.RequestTypeName, taskId, attempts, envelope.PayloadJson);
            return QueueDeliveryOutcome.RetryIn(DeadLetterWriteRetryBackoff);
        }
    }
}
