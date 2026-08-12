using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Spinneret.Mediator;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Gcp;

internal sealed class GcpSchedulerDispatcher(
    FirestoreDb db,
    IOptions<GcpSchedulerOptions> options,
    QueueTypeRegistry typeRegistry,
    IQueuePayloadSerializer serializer,
    IQueue queue,
    IDeadLetterWriter deadLetterWriter,
    ILogger<GcpSchedulerDispatcher> logger)
{
    private string Collection => options.Value.Collection;
    private Duration OneShotLeaseWindow => Duration.FromTimeSpan(options.Value.OneShotLeaseWindow);

    /// <summary>How far a job with an unreadable schedule is pushed out before the sweep sees it again.</summary>
    private static readonly Duration QuarantineWindow = Duration.FromMinutes(5);

    public async Task DispatchDueJobsAsync(CancellationToken ct)
    {
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var snapshot = await db.Collection(Collection)
            .WhereEqualTo(ScheduledJob.Fields.Status, ScheduledJob.StatusValues.Pending)
            .WhereLessThanOrEqualTo(ScheduledJob.Fields.NextExecuteAt, now)
            .GetSnapshotAsync(ct);

        foreach (var doc in snapshot.Documents)
        {
            try
            {
                await EnqueueJobAsync(doc, ct);
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
    }

    private Task EnqueueJobAsync(DocumentSnapshot doc, CancellationToken ct)
    {
        Schedule? schedule;
        try
        {
            schedule = ResolveSchedule(doc);
        }
        catch (FormatException ex)
        {
            // Written by a newer version before a rollback, or corrupted. Quarantine instead of
            // failing: push the run out and dead-letter the occurrence, leaving the document
            // pending so a host version that understands it can still pick it up.
            return QuarantineUnreadableAsync(doc, ex, ct);
        }

        return schedule is not null
            ? EnqueueRecurringJobAsync(doc, schedule, ct)
            : EnqueueOneShotJobAsync(doc, ct);
    }

    private async Task QuarantineUnreadableAsync(DocumentSnapshot doc, FormatException failure, CancellationToken ct)
    {
        logger.LogError(failure,
            "Scheduled job {JobId} has an unreadable schedule; quarantining for {Quarantine}",
            doc.Id, QuarantineWindow);

        // The lease doubles as the multi-node guard: only the sweep that wins it books the
        // dead letter, so competing sweeps don't record the same occurrence.
        if (!await TryLeaseAsync(doc.Reference, now => now + QuarantineWindow, ct))
            return;

        var requestTypeName = doc.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = doc.GetValue<string>(ScheduledJob.Fields.PayloadJson);
        await WriteDeadLetterAsync(
            $"{doc.Id}:{DateTimeOffset.UtcNow.Ticks}", doc.Id, requestTypeName, payloadJson, failure.Message, ct);
    }

    /// <summary>The job's recurrence, read from the canonical schedule field; null for a one-shot job.</summary>
    private static Schedule? ResolveSchedule(DocumentSnapshot doc) =>
        doc.ContainsField(ScheduledJob.Fields.Schedule)
            ? Schedule.Parse(doc.GetValue<string>(ScheduledJob.Fields.Schedule))
            : null;

    private async Task EnqueueOneShotJobAsync(DocumentSnapshot doc, CancellationToken ct)
    {
        // Lease the job by hiding it for a visibility window rather than flipping it to a terminal
        // "executing" state: if the dispatcher crashes mid-dispatch the lease lapses and a later
        // sweep retries it, so a one-shot job can never get permanently stuck. Downstream commands
        // are idempotent, so the resulting at-least-once delivery is safe.
        if (!await TryLeaseAsync(doc.Reference, now => now + OneShotLeaseWindow, ct))
            return;

        var requestTypeName = doc.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = doc.GetValue<string>(ScheduledJob.Fields.PayloadJson);

        try
        {
            await EnqueueAsync(requestTypeName, payloadJson, ct);
            // Terminal: a one-shot job runs once, so mark it done to drop it from future sweeps.
            await SetStatusAsync(doc.Reference, ScheduledJob.StatusValues.Enqueued, ct);

            logger.LogInformation("Scheduled job {JobId} ({Type}) enqueued", doc.Id, requestTypeName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled job {JobId} ({Type}) failed to enqueue", doc.Id, requestTypeName);
            await WriteDeadLetterAsync(doc.Id, doc.Id, requestTypeName, payloadJson, ex.Message, ct);
            await SetStatusAsync(doc.Reference, ScheduledJob.StatusValues.Failed, ct);
        }
    }

    private async Task EnqueueRecurringJobAsync(DocumentSnapshot doc, Schedule schedule, CancellationToken ct)
    {
        // Lease the next scheduled run before doing any work. The advanced NextExecuteAt is the
        // lock: a competing or subsequent sweep won't re-select the job until that run is due, and
        // the job stays Pending — so recurrence never gets stuck after a crash, and a failed
        // occurrence never stops future runs.
        if (!await TryLeaseAsync(doc.Reference, schedule.NextRun, ct))
            return;

        var requestTypeName = doc.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = doc.GetValue<string>(ScheduledJob.Fields.PayloadJson);

        try
        {
            await EnqueueAsync(requestTypeName, payloadJson, ct);
            logger.LogInformation(
                "Recurring job {JobId} ({Type}) enqueued; next run per {Schedule}",
                doc.Id, requestTypeName, schedule);
        }
        catch (Exception ex)
        {
            // Dead-letter this occurrence but leave the schedule armed — the next interval still runs.
            logger.LogError(ex,
                "Recurring job {JobId} ({Type}) failed to enqueue; schedule remains active",
                doc.Id, requestTypeName);
            // Each failed occurrence is distinct, so suffix the key rather than dedupe on the job id.
            await WriteDeadLetterAsync($"{doc.Id}:{DateTimeOffset.UtcNow.Ticks}", doc.Id, requestTypeName, payloadJson, ex.Message, ct);
        }
    }

    private async Task EnqueueAsync(string requestTypeName, string payloadJson, CancellationToken ct)
    {
        var requestType = typeRegistry.Resolve(requestTypeName).RequestType;
        var request = (IRequest<Unit>)(serializer.Deserialize(payloadJson, requestType)
            ?? throw new InvalidOperationException($"Deserialized null for '{requestTypeName}'."));

        await queue.Enqueue(request, ct: ct);
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
        DocumentReference docRef, Func<Instant, Instant> nextRunFrom, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var nextRun = Timestamp.FromDateTimeOffset(
            nextRunFrom(Instant.FromDateTimeOffset(now)).ToDateTimeOffset());

        try
        {
            return await db.RunTransactionAsync(async tx =>
            {
                var snapshot = await tx.GetSnapshotAsync(docRef, ct);
                if (!snapshot.Exists ||
                    snapshot.GetValue<string>(ScheduledJob.Fields.Status) != ScheduledJob.StatusValues.Pending)
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

    private async Task WriteDeadLetterAsync(
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
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to write dead-letter for scheduled job {JobId} ({Type}). Payload: {Payload}",
                jobId, requestTypeName, payloadJson);
        }
    }

    private static Task SetStatusAsync(DocumentReference docRef, string status, CancellationToken ct) =>
        docRef.UpdateAsync(
            new Dictionary<string, object> { [ScheduledJob.Fields.Status] = status },
            cancellationToken: ct);
}
