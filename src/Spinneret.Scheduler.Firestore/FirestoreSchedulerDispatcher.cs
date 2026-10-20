using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore;

internal sealed class FirestoreSchedulerDispatcher(
    FirestoreDb db,
    IOptions<FirestoreSchedulerOptions> options,
    QueueTypeRegistry typeRegistry,
    IQueuePayloadSerializer serializer,
    IQueue queue,
    IDeadLetterWriter deadLetterWriter,
    TimeProvider timeProvider,
    ILogger<FirestoreSchedulerDispatcher> logger)
    : ISchedulerSweep
{
    private string Collection => options.Value.Collection;
    private TimeSpan OneShotLeaseWindow => options.Value.OneShotLeaseWindow;

    private static readonly TimeSpan QuarantineWindow = TimeSpan.FromMinutes(5);

    // One query snapshot per pass; jobs falling due mid-pass wait for the next sweep.
    public async Task<SweepResult> SweepAsync(CancellationToken ct)
    {
        using var activity = SchedulerTracing.StartSweep();

        var dispatched = 0;
        var now = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow());
        // Documents exist only while they are work, so due-ness is the whole predicate (no composite index).
        var snapshot = await db.Collection(Collection)
            .WhereLessThanOrEqualTo(ScheduledJob.Fields.NextExecuteAt, now)
            .GetSnapshotAsync(ct);

        foreach (var doc in snapshot.Documents)
        {
            try
            {
                if (await EnqueueJobAsync(doc, ct))
                    dispatched++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad document must not abort the sweep, or it would block every future pass.
                logger.LogError(ex, "Scheduled job {JobId} failed to dispatch; sweep continues", doc.Id);
            }
        }

        activity?.SetTag(SchedulerTags.JobsDispatched, dispatched);
        return SweepResult.Dispatched(dispatched);
    }

    private async Task<bool> EnqueueJobAsync(DocumentSnapshot doc, CancellationToken ct)
    {
        using var activity = SchedulerTracing.StartJob(doc.Id);

        Schedule? schedule;
        try
        {
            schedule = ResolveSchedule(doc);
        }
        catch (FormatException ex)
        {
            // Quarantine, keeping the document for a host version that can read it.
            activity.SetOutcome(SchedulerJobOutcome.Quarantined, ex.Message);
            return await QuarantineUnreadableAsync(doc, ex, ct);
        }

        activity?.SetTag(SchedulerTags.JobKind,
            schedule is not null ? SchedulerJobKind.Recurring : SchedulerJobKind.OneShot);

        var enqueued = await (schedule is not null
            ? EnqueueRecurringJobAsync(doc, schedule, ct)
            : EnqueueOneShotJobAsync(doc, ct));

        activity.SetOutcome(enqueued ? SchedulerJobOutcome.Enqueued : SchedulerJobOutcome.Skipped);
        return enqueued;
    }

    private async Task<bool> QuarantineUnreadableAsync(DocumentSnapshot doc, FormatException failure, CancellationToken ct)
    {
        logger.LogError(failure,
            "Scheduled job {JobId} has an unreadable schedule; quarantining for {Quarantine}",
            doc.Id, QuarantineWindow);

        // Only the sweep that wins the lease records the dead letter.
        if (!await TryLeaseAsync(doc.Reference, now => now + QuarantineWindow, ct))
            return false;

        // Kept regardless: the document is unreadable, not finished.
        var requestTypeName = doc.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = doc.GetValue<string>(ScheduledJob.Fields.PayloadJson);
        await WriteDeadLetterAsync(
            $"{doc.Id}:{timeProvider.GetUtcNow().Ticks}", doc.Id, requestTypeName, payloadJson, failure.Message, ct);
        return false;
    }

    private static Schedule? ResolveSchedule(DocumentSnapshot doc) =>
        doc.ContainsField(ScheduledJob.Fields.Schedule)
            ? Schedule.Parse(doc.GetValue<string>(ScheduledJob.Fields.Schedule))
            : null;

    private async Task<bool> EnqueueOneShotJobAsync(DocumentSnapshot doc, CancellationToken ct)
    {
        // Lease rather than delete up front, so a crash mid-dispatch is retried (at-least-once).
        if (!await TryLeaseAsync(doc.Reference, now => now + OneShotLeaseWindow, ct))
            return false;

        var requestTypeName = doc.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = doc.GetValue<string>(ScheduledJob.Fields.PayloadJson);

        try
        {
            await EnqueueAsync(requestTypeName, payloadJson, ct);
            await doc.Reference.DeleteAsync(cancellationToken: ct);

            logger.LogInformation("Scheduled job {JobId} ({Type}) enqueued", doc.Id, requestTypeName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled job {JobId} ({Type}) failed to enqueue", doc.Id, requestTypeName);

            // Delete only once the dead letter is written; otherwise the document is the only copy.
            if (await WriteDeadLetterAsync(doc.Id, doc.Id, requestTypeName, payloadJson, ex.Message, ct))
                await doc.Reference.DeleteAsync(cancellationToken: ct);

            return false;
        }
    }

    private async Task<bool> EnqueueRecurringJobAsync(DocumentSnapshot doc, Schedule schedule, CancellationToken ct)
    {
        // Advancing NextExecuteAt is the lease, so a crash or failed run never stops future runs.
        if (!await TryLeaseAsync(doc.Reference, schedule.NextRun, ct))
            return false;

        var requestTypeName = doc.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = doc.GetValue<string>(ScheduledJob.Fields.PayloadJson);

        try
        {
            await EnqueueAsync(requestTypeName, payloadJson, ct);
            logger.LogInformation(
                "Recurring job {JobId} ({Type}) enqueued; next run per {Schedule}",
                doc.Id, requestTypeName, schedule);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Recurring job {JobId} ({Type}) failed to enqueue; schedule remains active",
                doc.Id, requestTypeName);
            // Each failed occurrence is distinct, so suffix the key.
            await WriteDeadLetterAsync($"{doc.Id}:{timeProvider.GetUtcNow().Ticks}", doc.Id, requestTypeName, payloadJson, ex.Message, ct);
            return false;
        }
    }

    private async Task EnqueueAsync(string requestTypeName, string payloadJson, CancellationToken ct)
    {
        var registered = typeRegistry.Resolve(requestTypeName);
        var request = serializer.Deserialize(payloadJson, registered.RequestType)
            ?? throw new InvalidOperationException($"Deserialized null for '{requestTypeName}'.");

        await ResolvedRequestEnqueuer.Enqueue(queue, request, registered.ResponseType, ct);
    }

    // The advanced timestamp is the lock: no "executing" state, so a crash never strands the job.
    // False if another sweep already leased or finished it.
    private async Task<bool> TryLeaseAsync(
        DocumentReference docRef, Func<DateTimeOffset, DateTimeOffset> nextRunFrom, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var nextRun = Timestamp.FromDateTimeOffset(nextRunFrom(now));

        try
        {
            return await db.RunTransactionAsync(async tx =>
            {
                var snapshot = await tx.GetSnapshotAsync(docRef, ct);
                if (!snapshot.Exists)
                    return false;

                if (snapshot.GetValue<Timestamp>(ScheduledJob.Fields.NextExecuteAt).ToDateTimeOffset() > now)
                    return false;

                tx.Update(docRef, new Dictionary<string, object>
                {
                    [ScheduledJob.Fields.NextExecuteAt] = nextRun,
                    [ScheduledJob.Fields.LastRunAt] = Timestamp.FromDateTimeOffset(now),
                });
                return true;
            }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to lease scheduled job {JobId}", docRef.Id);
            return false;
        }
    }

    // False means the caller must keep the job document: it is the only copy of the payload.
    private async Task<bool> WriteDeadLetterAsync(
        string idempotencyKey, string jobId, string requestTypeName, string payloadJson, string error, CancellationToken ct)
    {
        try
        {
            await deadLetterWriter.WriteAsync(new DeadLetterEntry
            {
                IdempotencyKey  = idempotencyKey,
                Source          = DeadLetterSource.Scheduler,
                CommandTypeName = requestTypeName,
                PayloadJson     = payloadJson,
                Error           = error,
                Attempts        = 1
            }, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to write dead-letter for scheduled job {JobId} ({Type}). Payload: {Payload}",
                jobId, requestTypeName, payloadJson);
            return false;
        }
    }
}
