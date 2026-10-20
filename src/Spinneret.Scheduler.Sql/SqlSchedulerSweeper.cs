using System.Data.Common;
using Microsoft.Extensions.Logging;
using Spinneret.Queue;
using Spinneret.Queue.Sql;

namespace Spinneret.Scheduler.Sql;

// Each job's claim, booking and enqueue commit in one transaction, so a run is never lost or
// double-enqueued. A failure compensates in a fresh one; a recurring job stays armed.
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
    private static readonly TimeSpan QuarantineDelay = TimeSpan.FromMinutes(5);

    private SqlSchedulerStatements Sql => storage.Statements;
    private SqlQueueDialect Timestamps => storage.Queue.Dialect;

    // Drains until nothing is due, but returns once a job cannot be booked: continuing would
    // re-claim it in a tight loop, so the caller's interval provides the backoff.
    public async Task<SweepResult> SweepAsync(CancellationToken ct)
    {
        using var activity = SchedulerTracing.StartSweep();

        var dispatched = 0;
        while (await TryDispatchNextDue(ct))
            dispatched++;

        activity?.SetTag(SchedulerTags.JobsDispatched, dispatched);
        return SweepResult.Dispatched(dispatched);
    }

    private async Task<bool> TryDispatchNextDue(CancellationToken ct)
    {
        var attempt = await unitOfWork.ExecuteAsync((unit, token) => ClaimAndDispatch(unit, token), ct);

        // Compensate after the claim's transaction ends, or it would wait on the claim's row lock.
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

        // An unparseable row would be re-selected every pass and starve other jobs, so quarantine
        // it, leaving the row for a host version that can read it.
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
            // The enqueue joins this ambient transaction, so booking and enqueue commit atomically.
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
            // Uncommitted, so the claim rolls back before compensation runs.
            return new DispatchAttempt.Failed(claimed, schedule, ex);
        }
    }

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

    // Guarded: if a competing sweep re-claimed the job, this is a no-op and nothing is
    // dead-lettered. False means the job is still due and the caller must stop draining.
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
                    // Each failed recurring occurrence is distinct; a one-shot fails at most once.
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
            // The job is still due, so the next sweep retries it.
            logger.LogCritical(ex,
                "Failed to record dispatch failure for scheduled job {JobKey} ({Type}). Payload: {Payload}",
                job.JobKey, job.RequestTypeName, job.PayloadJson);
            return false;
        }
    }

    private sealed record ScheduledJobRow(string JobKey, string RequestTypeName, string PayloadJson, string? ScheduleText);

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
