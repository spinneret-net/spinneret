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

    /// <summary>How far a job with an unreadable schedule is pushed out before the sweep sees it again.</summary>
    private static readonly TimeSpan QuarantineWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Dispatches the jobs due as of one query snapshot. Unlike the SQL sweep, which drains until
    /// nothing is left, anything falling due mid-pass waits for the next sweep — so the trigger's
    /// interval bounds how late a job can run.
    /// </summary>
    public async Task<SweepResult> SweepAsync(CancellationToken ct)
    {
        using var activity = SchedulerTracing.StartSweep();

        var dispatched = 0;
        var now = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow());
        // No status filter: a document exists only while it is still work to do, so being due is the
        // whole predicate. That also keeps this to a single-field index, which Firestore maintains
        // automatically — no composite index to provision.
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
                // One bad document must never abort the sweep — the jobs after it in this
                // snapshot are still due and every future sweep would hit the same document first.
                logger.LogError(ex, "Scheduled job {JobId} failed to dispatch; sweep continues", doc.Id);
            }
        }

        activity?.SetTag(SchedulerTags.JobsDispatched, dispatched);
        return SweepResult.Dispatched(dispatched);
    }

    /// <summary>True when the job was handed to the queue; false when it was skipped or quarantined.</summary>
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
            // Written by a newer version before a rollback, or corrupted. Quarantine instead of
            // failing: push the run out and dead-letter the occurrence, leaving the document in
            // place so a host version that understands it can still pick it up.
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

        // The lease doubles as the multi-node guard: only the sweep that wins it books the
        // dead letter, so competing sweeps don't record the same occurrence.
        if (!await TryLeaseAsync(doc.Reference, now => now + QuarantineWindow, ct))
            return false;

        // The document is deliberately kept whatever the dead-letter write does: it is unreadable,
        // not finished.
        var requestTypeName = doc.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = doc.GetValue<string>(ScheduledJob.Fields.PayloadJson);
        await WriteDeadLetterAsync(
            $"{doc.Id}:{timeProvider.GetUtcNow().Ticks}", doc.Id, requestTypeName, payloadJson, failure.Message, ct);
        return false;
    }

    /// <summary>The job's recurrence, read from the canonical schedule field; null for a one-shot job.</summary>
    private static Schedule? ResolveSchedule(DocumentSnapshot doc) =>
        doc.ContainsField(ScheduledJob.Fields.Schedule)
            ? Schedule.Parse(doc.GetValue<string>(ScheduledJob.Fields.Schedule))
            : null;

    private async Task<bool> EnqueueOneShotJobAsync(DocumentSnapshot doc, CancellationToken ct)
    {
        // Lease the job by hiding it for a visibility window rather than deleting it up front: if the
        // dispatcher crashes mid-dispatch the lease lapses and a later sweep retries it, so a one-shot
        // job can never get permanently stuck. Downstream commands are idempotent, so the resulting
        // at-least-once delivery is safe.
        if (!await TryLeaseAsync(doc.Reference, now => now + OneShotLeaseWindow, ct))
            return false;

        var requestTypeName = doc.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = doc.GetValue<string>(ScheduledJob.Fields.PayloadJson);

        try
        {
            await EnqueueAsync(requestTypeName, payloadJson, ct);
            // A one-shot job runs once, so the document has served its purpose. Deleting rather than
            // flagging it is what keeps the collection bounded — the queue message is now the record.
            await doc.Reference.DeleteAsync(cancellationToken: ct);

            logger.LogInformation("Scheduled job {JobId} ({Type}) enqueued", doc.Id, requestTypeName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled job {JobId} ({Type}) failed to enqueue", doc.Id, requestTypeName);

            // Delete only once the payload is safe in the dead-letter store. If that write failed the
            // document is all that is left of the job, so keep it: the lease lapses and a later sweep
            // retries, which also recovers the job outright if the failure was transient.
            if (await WriteDeadLetterAsync(doc.Id, doc.Id, requestTypeName, payloadJson, ex.Message, ct))
                await doc.Reference.DeleteAsync(cancellationToken: ct);

            return false;
        }
    }

    private async Task<bool> EnqueueRecurringJobAsync(DocumentSnapshot doc, Schedule schedule, CancellationToken ct)
    {
        // Lease the next scheduled run before doing any work. The advanced NextExecuteAt is the
        // lock: a competing or subsequent sweep won't re-select the job until that run is due, and
        // the document is never removed — so recurrence never gets stuck after a crash, and a
        // failed occurrence never stops future runs.
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
            // Dead-letter this occurrence but leave the schedule armed — the next slot still runs.
            logger.LogError(ex,
                "Recurring job {JobId} ({Type}) failed to enqueue; schedule remains active",
                doc.Id, requestTypeName);
            // Each failed occurrence is distinct, so suffix the key rather than dedupe on the job id.
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

    /// <summary>
    /// Atomically leases a due job by pushing its next run into the future (computed by
    /// <paramref name="nextRunFrom"/>) while leaving it Pending. The advanced timestamp is the
    /// lock: it hides the job from concurrent and subsequent sweeps without any terminal
    /// "executing" state, so a crash before the work completes never strands the job — the lease
    /// lapses and a later sweep retries it. Returns false if another sweep already leased it (or
    /// it is no longer pending).
    /// </summary>
    private async Task<bool> TryLeaseAsync(
        DocumentReference docRef, Func<DateTimeOffset, DateTimeOffset> nextRunFrom, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var nextRun = Timestamp.FromDateTimeOffset(nextRunFrom(now));

        try
        {
            return await db.RunTransactionAsync(async tx =>
            {
                // Gone means another sweep already finished it — there is no terminal state to check
                // for beyond the document's own existence.
                var snapshot = await tx.GetSnapshotAsync(docRef, ct);
                if (!snapshot.Exists)
                    return false;

                // A competing sweep already leased it past now.
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

    /// <summary>
    /// Records a dead letter. Returns false if it could not be written — the caller must then keep
    /// the job document, because it is the only remaining copy of the payload.
    /// </summary>
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
