using System.Security.Cryptography;
using System.Text;
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
    private TimeSpan EnqueueRetryWindow => options.Value.EnqueueRetryWindow;

    private static readonly TimeSpan QuarantineWindow = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);
    private const int EscalateAfterFailures = 5;

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

        var outcome = await (schedule is not null
            ? EnqueueRecurringJobAsync(doc, schedule, ct)
            : EnqueueOneShotJobAsync(doc, ct));

        activity.SetOutcome(outcome switch
        {
            DispatchOutcome.Enqueued => SchedulerJobOutcome.Enqueued,
            DispatchOutcome.Retrying => SchedulerJobOutcome.Retrying,
            DispatchOutcome.Skipped => SchedulerJobOutcome.Skipped,
        });
        return outcome == DispatchOutcome.Enqueued;
    }

    private async Task<bool> QuarantineUnreadableAsync(DocumentSnapshot doc, FormatException failure, CancellationToken ct)
    {
        logger.LogError(failure,
            "Scheduled job {JobId} has an unreadable schedule; quarantining for {Quarantine}",
            doc.Id, QuarantineWindow);

        // Only the sweep that wins the lease records the dead letter.
        if (await TryLeaseAsync(doc.Reference, now => now + QuarantineWindow, ct) is null)
            return false;

        // Kept regardless: the document is unreadable, not finished.
        var requestTypeName = doc.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = doc.GetValue<string>(ScheduledJob.Fields.PayloadJson);
        await WriteDeadLetterAsync(
            $"{doc.Id}:{timeProvider.GetUtcNow().Ticks}", doc.Id, requestTypeName, payloadJson, failure.Message, 1, ct);
        return false;
    }

    private static Schedule? ResolveSchedule(DocumentSnapshot doc) =>
        doc.ContainsField(ScheduledJob.Fields.Schedule)
            ? Schedule.Parse(doc.GetValue<string>(ScheduledJob.Fields.Schedule))
            : null;

    private async Task<DispatchOutcome> EnqueueOneShotJobAsync(DocumentSnapshot doc, CancellationToken ct)
    {
        // Lease rather than delete up front, so a crash mid-dispatch is retried (at-least-once).
        if (await TryLeaseAsync(doc.Reference, now => now + OneShotLeaseWindow, ct) is not { } leased)
            return DispatchOutcome.Skipped;

        var requestTypeName = leased.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = leased.GetValue<string>(ScheduledJob.Fields.PayloadJson);

        if (!TryResolveRequest(leased, out var request, out var failure))
        {
            // Delete only once the dead letter is written; otherwise the document is the only copy.
            await DeadLetterOneShotAsync(leased, requestTypeName, payloadJson, failure.Message, 1, ct);
            return DispatchOutcome.Skipped;
        }

        // The handle names exactly one run, so a retry after an enqueue that landed unseen is a duplicate.
        var dedupeKey = DedupeKey(leased.Id);
        try
        {
            await ResolvedRequestEnqueuer.Enqueue(
                queue, request.Request, request.ResponseType, new QueueOptions { DedupeKey = dedupeKey }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (await RetryLaterAsync(leased, requestTypeName, pendingDedupeKey: null, ex, ct) is { } exhausted)
            {
                await DeadLetterOneShotAsync(leased, requestTypeName, payloadJson, exhausted.Error, exhausted.Attempts, ct);
                return DispatchOutcome.Skipped;
            }
            return DispatchOutcome.Retrying;
        }

        await doc.Reference.DeleteAsync(cancellationToken: ct);
        LogEnqueued("Scheduled job {JobId} ({Type}) enqueued", leased, requestTypeName);
        return DispatchOutcome.Enqueued;
    }

    private async Task<DispatchOutcome> EnqueueRecurringJobAsync(DocumentSnapshot doc, Schedule schedule, CancellationToken ct)
    {
        // Advancing NextExecuteAt to the next slot is the lease, so a crash never stops future runs.
        if (await TryLeaseAsync(doc.Reference, schedule.NextRun, ct) is not { } leased)
            return DispatchOutcome.Skipped;

        var requestTypeName = leased.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        var payloadJson = leased.GetValue<string>(ScheduledJob.Fields.PayloadJson);

        if (!TryResolveRequest(leased, out var request, out var failure))
        {
            // Each failed occurrence is distinct, so suffix the key.
            await WriteDeadLetterAsync(
                $"{leased.Id}:{timeProvider.GetUtcNow().Ticks}", leased.Id, requestTypeName, payloadJson, failure.Message, 1, ct);
            return DispatchOutcome.Skipped;
        }

        // Kept across retries: a timed-out enqueue may have landed, and its retry must then be a duplicate.
        var dedupeKey = leased.TryGetValue<string>(ScheduledJob.Fields.PendingDedupeKey, out var pending)
            ? pending
            : DedupeKey($"{leased.Id}:{leased.GetValue<Timestamp>(ScheduledJob.Fields.NextExecuteAt).ToDateTimeOffset().UtcTicks}");
        try
        {
            await ResolvedRequestEnqueuer.Enqueue(
                queue, request.Request, request.ResponseType, new QueueOptions { DedupeKey = dedupeKey }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (await RetryLaterAsync(leased, requestTypeName, dedupeKey, ex, ct) is { } exhausted)
            {
                // The lease already moved the job to its next slot, so giving up resumes the schedule.
                await WriteDeadLetterAsync(
                    $"{leased.Id}:{timeProvider.GetUtcNow().Ticks}", leased.Id, requestTypeName, payloadJson,
                    exhausted.Error, exhausted.Attempts, ct);
                return DispatchOutcome.Skipped;
            }
            return DispatchOutcome.Retrying;
        }

        LogEnqueued("Recurring job {JobId} ({Type}) enqueued", leased, requestTypeName);
        return DispatchOutcome.Enqueued;
    }

    // A request that cannot be rebuilt never will be, so it is dead-lettered rather than retried.
    private bool TryResolveRequest(DocumentSnapshot leased, out ResolvedRequest request, out Exception failure)
    {
        var requestTypeName = leased.GetValue<string>(ScheduledJob.Fields.RequestTypeName);
        try
        {
            var registered = typeRegistry.Resolve(requestTypeName);
            var payload = serializer.Deserialize(leased.GetValue<string>(ScheduledJob.Fields.PayloadJson), registered.RequestType)
                ?? throw new InvalidOperationException($"Deserialized null for '{requestTypeName}'.");
            request = new ResolvedRequest(payload, registered.ResponseType);
            failure = null!;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled job {JobId} ({Type}) cannot be rebuilt; dead-lettering", leased.Id, requestTypeName);
            request = null!;
            failure = ex;
            return false;
        }
    }

    // An enqueue failure is the transport's, so the job stays due rather than dead-lettered at once; slots
    // missed meanwhile coalesce into the run that lands, since every occurrence carries the same request.
    // The window bounds it: a failure that never clears (a deleted queue, a payload too large) must
    // reach the dead letters, where someone looks, not retry unseen forever.
    // Returns why the job is given up on, or null once its retry is booked.
    private async Task<Exhausted?> RetryLaterAsync(
        DocumentSnapshot leased, string requestTypeName, string? pendingDedupeKey, Exception failure, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var failures = PriorFailures(leased) + 1;
        var firstFailedAt = leased.TryGetValue<Timestamp>(ScheduledJob.Fields.FirstFailedAt, out var stored)
            ? stored.ToDateTimeOffset()
            : now;

        if (now - firstFailedAt >= EnqueueRetryWindow)
        {
            logger.LogError(failure,
                "Scheduled job {JobId} ({Type}) failed to enqueue for {Window} ({Attempts} attempts); dead-lettering",
                leased.Id, requestTypeName, EnqueueRetryWindow, failures);
            return new Exhausted(
                $"Enqueue kept failing for {EnqueueRetryWindow} ({failures} attempts): {failure.Message}", failures);
        }

        var delay = RetryDelay(failures);

        // An enqueue that keeps failing will not recover by itself, so escalate it to an error.
        if (failures < EscalateAfterFailures)
            logger.LogWarning(failure,
                "Scheduled job {JobId} ({Type}) failed to enqueue (attempt {Attempt}); retrying in {Delay}",
                leased.Id, requestTypeName, failures, delay);
        else
            logger.LogError(failure,
                "Scheduled job {JobId} ({Type}) failed to enqueue on {Attempt} consecutive attempts; retrying in {Delay}",
                leased.Id, requestTypeName, failures, delay);

        var update = new Dictionary<string, object>
        {
            [ScheduledJob.Fields.NextExecuteAt] = Timestamp.FromDateTimeOffset(now + delay),
            [ScheduledJob.Fields.DispatchFailures] = failures,
            [ScheduledJob.Fields.FirstFailedAt] = Timestamp.FromDateTimeOffset(firstFailedAt),
        };
        if (pendingDedupeKey is not null)
            update[ScheduledJob.Fields.PendingDedupeKey] = pendingDedupeKey;

        try
        {
            // An update, not a set: an unregistered or cancelled job must stay gone.
            await leased.Reference.UpdateAsync(update, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A one-shot is still retried once its lease lapses; a recurring occurrence is lost to its next slot.
            logger.LogError(ex,
                "Scheduled job {JobId} ({Type}) could not record its retry", leased.Id, requestTypeName);
        }
        return null;
    }

    private async Task DeadLetterOneShotAsync(
        DocumentSnapshot leased, string requestTypeName, string payloadJson, string error, int attempts, CancellationToken ct)
    {
        // Delete only once the dead letter is written; otherwise the document is the only copy.
        if (await WriteDeadLetterAsync(leased.Id, leased.Id, requestTypeName, payloadJson, error, attempts, ct))
            await leased.Reference.DeleteAsync(cancellationToken: ct);
    }

    private void LogEnqueued(string message, DocumentSnapshot leased, string requestTypeName)
    {
        var failures = PriorFailures(leased);
        if (failures == 0)
            logger.LogInformation(message, leased.Id, requestTypeName);
        else
            logger.LogInformation(message + " after {Failures} failed attempts", leased.Id, requestTypeName, failures);
    }

    private static int PriorFailures(DocumentSnapshot leased) =>
        leased.TryGetValue<int>(ScheduledJob.Fields.DispatchFailures, out var failures) ? failures : 0;

    private static TimeSpan RetryDelay(int failures)
    {
        var doubled = FirstRetryDelay * Math.Pow(2, Math.Min(failures - 1, 16));
        return doubled < MaxRetryDelay ? doubled : MaxRetryDelay;
    }

    // Hashed rather than the job key itself: every transport accepts [A-Za-z0-9_-] up to 200 characters,
    // and Cloud Tasks wants task names spread, not sharing a prefix.
    private static string DedupeKey(string occurrence) =>
        "scheduled-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(occurrence)))[..32];

    // The advanced timestamp is the lock: no "executing" state, so a crash never strands the job.
    // Returns the job as it was before the lease, or null if another sweep already leased or finished it.
    // Clears the retry state optimistically: a failed enqueue writes it back, and a crash before then
    // must not leave a stale dedupe key to swallow a later occurrence.
    private async Task<DocumentSnapshot?> TryLeaseAsync(
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
                    return null;

                if (snapshot.GetValue<Timestamp>(ScheduledJob.Fields.NextExecuteAt).ToDateTimeOffset() > now)
                    return null;

                tx.Update(docRef, new Dictionary<string, object>
                {
                    [ScheduledJob.Fields.NextExecuteAt] = nextRun,
                    [ScheduledJob.Fields.LastRunAt] = Timestamp.FromDateTimeOffset(now),
                    [ScheduledJob.Fields.DispatchFailures] = FieldValue.Delete,
                    [ScheduledJob.Fields.PendingDedupeKey] = FieldValue.Delete,
                    [ScheduledJob.Fields.FirstFailedAt] = FieldValue.Delete,
                });
                return snapshot;
            }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to lease scheduled job {JobId}", docRef.Id);
            return null;
        }
    }

    // False means the caller must keep the job document: it is the only copy of the payload.
    private async Task<bool> WriteDeadLetterAsync(
        string idempotencyKey, string jobId, string requestTypeName, string payloadJson, string error, int attempts,
        CancellationToken ct)
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
                Attempts        = attempts
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

    private sealed record ResolvedRequest(object Request, Type ResponseType);

    private sealed record Exhausted(string Error, int Attempts);

    private enum DispatchOutcome
    {
        Enqueued,
        Retrying,
        Skipped,
    }
}
