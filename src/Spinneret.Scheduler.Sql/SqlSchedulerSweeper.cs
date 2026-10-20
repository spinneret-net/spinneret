using System.Data.Common;
using Microsoft.Extensions.Logging;
using Spinneret.Queue;
using Spinneret.Queue.Sql;

namespace Spinneret.Scheduler.Sql;

/// <summary>
/// Sweeps for due scheduled jobs and enqueues them. Each job dispatches in its own unit of work: the
/// claim (a locking read plus the booking) and the queue insert commit together, so a run can
/// neither be lost nor double-enqueued — competing hosts' sweeps skip locked rows and dispatch other
/// due jobs in parallel. A dispatch failure compensates in a fresh unit of work: a one-shot job is
/// deleted in the same transaction that records its dead letter; a recurring job dead-letters the
/// occurrence but keeps the schedule armed, because recurrence is owned by the job, not by any single
/// run.
/// </summary>
internal sealed class SqlSchedulerSweeper(
    SqlSchedulerStorage storage,
    SqlUnitOfWorkFactory unitOfWork,
    QueueTypeRegistry registry,
    IQueuePayloadSerializer serializer,
    IQueue queue,
    IDeadLetterWriter deadLetterWriter,
    ISqlTransactionProvider transactions,
    TimeProvider timeProvider,
    ILogger<SqlSchedulerSweeper> logger)
    : ISchedulerSweep
{
    /// <summary>How far an unreadable job is pushed out before the sweep sees it again.</summary>
    private static readonly TimeSpan QuarantineDelay = TimeSpan.FromMinutes(5);

    private SqlSchedulerStatements Sql => storage.Statements;
    private SqlQueueDialect Timestamps => storage.Queue.Dialect;

    /// <summary>
    /// Drains everything currently due, one job per transaction. Draining rather than taking a
    /// fixed batch is safe because each job commits on its own and competing hosts skip locked rows,
    /// so a backlog clears at full speed instead of one job per tick.
    /// </summary>
    /// <remarks>
    /// Returns as soon as a job cannot be booked, deliberately: continuing would re-claim the same
    /// still-due job in a tight loop. The caller's interval is what backs that off — see
    /// <c>Compensate</c>.
    /// </remarks>
    public async Task<SweepResult> SweepAsync(CancellationToken ct)
    {
        using var activity = SchedulerTracing.StartSweep();

        var dispatched = 0;
        while (await TryDispatchNextDue(ct))
            dispatched++;

        activity?.SetTag(SchedulerTags.JobsDispatched, dispatched);
        return SweepResult.Dispatched(dispatched);
    }

    /// <summary>Claims and dispatches at most one due job; false means nothing (more) is due.</summary>
    private async Task<bool> TryDispatchNextDue(CancellationToken ct)
    {
        var attempt = await unitOfWork.ExecuteAsync((unit, token) => ClaimAndDispatch(unit, token), ct);

        // Compensation runs only now, after the claim's transaction is gone: run inside it, its
        // fresh transaction would wait on the row lock the claim still holds.
        return attempt switch
        {
            DispatchAttempt.NothingDue => false,
            DispatchAttempt.Done => true,
            DispatchAttempt.Failed failed =>
                await Compensate(failed.Job, failed.Schedule, failed.Error, CancellationToken.None),
            _ => throw new InvalidOperationException($"Unknown dispatch attempt {attempt}."),
        };
    }

    private async Task<DispatchAttempt> ClaimAndDispatch(IQueueUnitOfWork unit, CancellationToken ct)
    {
        var transaction = transactions.Current
            ?? throw new InvalidOperationException("The sweep's unit of work published no transaction.");

        var now = timeProvider.GetUtcNow();
        var claimed = await ClaimNextDue(transaction, now, ct);
        if (claimed is null)
            return DispatchAttempt.NothingDue.Instance;

        using var activity = SchedulerTracing.StartJob(claimed.JobKey);

        // Parse the recurrence before doing any work. A row this host cannot parse (written by a
        // newer version before a rollback, or corrupted) must not fail the sweep — that would
        // starve every other due job forever, since the oldest-due poison row is re-selected on
        // each pass. Instead, quarantine it: push its run out and dead-letter the occurrence,
        // leaving the row in place so a host version that understands it can still pick it up.
        Schedule? schedule = null;
        if (claimed.ScheduleText is not null)
        {
            try
            {
                schedule = Schedule.Parse(claimed.ScheduleText);
            }
            catch (FormatException ex)
            {
                logger.LogError(ex,
                    "Scheduled job {JobKey} has an unreadable schedule '{ScheduleText}'; quarantining for {Quarantine}",
                    claimed.JobKey, claimed.ScheduleText, QuarantineDelay);
                activity.SetOutcome(SchedulerJobOutcome.Quarantined, ex.Message);
                await QuarantineUnreadable(unit, transaction, claimed, now, ex, ct);
                return DispatchAttempt.Done.Instance;
            }
        }

        activity?.SetTag(SchedulerTags.JobKind,
            schedule is null ? SchedulerJobKind.OneShot : SchedulerJobKind.Recurring);

        try
        {
            // Book the outcome on the claim's transaction: a recurring job's next run advances, a
            // one-shot is deleted. The booking and the enqueue below commit atomically, because the
            // enqueue joins the same ambient transaction.
            await using (var book = SqlConnectionSource.ConnectionOf(transaction)
                .CreateCommand(transaction, schedule is null ? Sql.CompleteOneShot : Sql.AdvanceRecurring))
            {
                book.AddParameter("@JobKey", claimed.JobKey);
                if (schedule is not null)
                {
                    Timestamps.AddTimestamp(book, "@Now", now);
                    Timestamps.AddTimestamp(book, "@NextExecuteAt", schedule.NextRun(now));
                }
                await book.ExecuteNonQueryAsync(ct);
            }

            await Enqueue(claimed, ct);
            await unit.CommitAsync(CancellationToken.None);

            logger.LogInformation("Scheduled job {JobKey} ({Type}) enqueued", claimed.JobKey, claimed.RequestTypeName);
            activity.SetOutcome(SchedulerJobOutcome.Enqueued);
            return DispatchAttempt.Done.Instance;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled job {JobKey} ({Type}) failed to enqueue", claimed.JobKey, claimed.RequestTypeName);
            activity.SetOutcome(SchedulerJobOutcome.Skipped, ex.Message);
            // Returned uncommitted, so the unit rolls the claim back before compensation runs.
            return new DispatchAttempt.Failed(claimed, schedule, ex);
        }
    }

    /// <summary>
    /// Books an unreadable job out of the sweep's way on the claim's transaction: run pushed
    /// forward, occurrence dead-lettered, row left in place so it recovers by itself once a
    /// host that can parse it runs (or the row is repaired).
    /// </summary>
    private async Task QuarantineUnreadable(
        IQueueUnitOfWork unit, DbTransaction transaction, ScheduledJobRow job,
        DateTimeOffset now, Exception failure, CancellationToken ct)
    {
        await using (var book = SqlConnectionSource.ConnectionOf(transaction)
            .CreateCommand(transaction, Sql.AdvanceRecurring))
        {
            book.AddParameter("@JobKey", job.JobKey);
            Timestamps.AddTimestamp(book, "@Now", now);
            Timestamps.AddTimestamp(book, "@NextExecuteAt", now + QuarantineDelay);
            await book.ExecuteNonQueryAsync(ct);
        }

        await deadLetterWriter.WriteAsync(new DeadLetterEntry
        {
            IdempotencyKey = $"{job.JobKey}:{now.UtcTicks}",
            Source = DeadLetterSource.Scheduler,
            CommandTypeName = job.RequestTypeName,
            PayloadJson = job.PayloadJson,
            Error = failure.Message,
            Attempts = 1,
        }, ct);

        await unit.CommitAsync(CancellationToken.None);
    }

    private async Task<ScheduledJobRow?> ClaimNextDue(DbTransaction transaction, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = SqlConnectionSource.ConnectionOf(transaction)
            .CreateCommand(transaction, Sql.ClaimNextDue);
        Timestamps.AddTimestamp(command, "@Now", now);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new ScheduledJobRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private async Task Enqueue(ScheduledJobRow job, CancellationToken ct)
    {
        var registered = registry.Resolve(job.RequestTypeName);
        var request = serializer.Deserialize(job.PayloadJson, registered.RequestType)
            ?? throw new InvalidOperationException($"Deserialized null for '{job.RequestTypeName}'.");

        await ResolvedRequestEnqueuer.Enqueue(queue, request, registered.ResponseType, ct);
    }

    /// <summary>
    /// After a failed dispatch: re-book the job in a fresh unit of work, guarded so a competing
    /// sweep that claimed the job after our rollback wins and this becomes a no-op. Only when the
    /// re-booking applied is the failure dead-lettered — otherwise the occurrence wasn't ours to
    /// record. Returns false when even the compensation failed — the job is then still due, and
    /// the caller must stop draining so the sweep interval provides the backoff.
    /// </summary>
    /// <remarks>
    /// For a one-shot job the re-booking is a delete, and it shares this transaction with the
    /// dead-letter write — so the row is only ever removed together with the record that replaces
    /// it. A failure to write the dead letter rolls both back, leaving the job due to be retried.
    /// </remarks>
    private async Task<bool> Compensate(
        ScheduledJobRow job, Schedule? schedule, Exception failure, CancellationToken ct)
    {
        try
        {
            await unitOfWork.ExecuteAsync<object?>(async (unit, token) =>
            {
                var transaction = transactions.Current!;
                var now = timeProvider.GetUtcNow();

                int booked;
                await using (var command = SqlConnectionSource.ConnectionOf(transaction).CreateCommand(
                    transaction, schedule is null ? Sql.CompensateOneShot : Sql.CompensateRecurring))
                {
                    command.AddParameter("@JobKey", job.JobKey);
                    Timestamps.AddTimestamp(command, "@Now", now);
                    if (schedule is not null)
                        Timestamps.AddTimestamp(command, "@NextExecuteAt", schedule.NextRun(now));
                    booked = await command.ExecuteNonQueryAsync(token);
                }

                if (booked == 1)
                {
                    // Each failed recurring occurrence is distinct, so suffix the key rather than
                    // dedupe on the job key; a one-shot fails at most once, so its key suffices.
                    var idempotencyKey = schedule is null
                        ? job.JobKey
                        : $"{job.JobKey}:{now.UtcTicks}";

                    await deadLetterWriter.WriteAsync(new DeadLetterEntry
                    {
                        IdempotencyKey = idempotencyKey,
                        Source = DeadLetterSource.Scheduler,
                        CommandTypeName = job.RequestTypeName,
                        PayloadJson = job.PayloadJson,
                        Error = failure.Message,
                        Attempts = 1,
                    }, token);
                }

                await unit.CommitAsync(token);
                return null;
            }, ct);

            return true;
        }
        catch (Exception ex)
        {
            // The job is still pending and due, so the next sweep retries it — compensation is
            // about booking the failure, not about keeping the schedule alive.
            logger.LogCritical(ex,
                "Failed to record dispatch failure for scheduled job {JobKey} ({Type}). Payload: {Payload}",
                job.JobKey, job.RequestTypeName, job.PayloadJson);
            return false;
        }
    }

    private sealed record ScheduledJobRow(string JobKey, string RequestTypeName, string PayloadJson, string? ScheduleText);

    /// <summary>What one claim-and-dispatch pass came to, decided inside the claim's unit of work.</summary>
    private abstract record DispatchAttempt
    {
        public sealed record NothingDue : DispatchAttempt
        {
            public static readonly NothingDue Instance = new();
        }

        public sealed record Done : DispatchAttempt
        {
            public static readonly Done Instance = new();
        }

        public sealed record Failed(ScheduledJobRow Job, Schedule? Schedule, Exception Error) : DispatchAttempt;
    }
}
