using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Spinneret.Mediator;
using Spinneret.Queue;
using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// Sweeps for due scheduled jobs and enqueues them. Each job dispatches in its own SQL
/// transaction: the claim (an UPDLOCK read plus the booking update) and the queue insert commit
/// together, so a run can neither be lost nor double-enqueued — competing hosts' sweeps skip
/// locked rows via READPAST and dispatch other due jobs in parallel. A dispatch failure
/// compensates on a fresh transaction, mirroring the GCP dispatcher: a one-shot job goes
/// terminal-failed with a dead letter; a recurring job dead-letters the occurrence but keeps the
/// schedule armed, because recurrence is owned by the job, not by any single run.
/// </summary>
internal sealed class MssqlSchedulerSweeper(
    IOptions<MssqlQueueOptions> queueOptions,
    IOptions<MssqlSchedulerOptions> schedulerOptions,
    ScheduledJobSql sql,
    QueueTypeRegistry registry,
    IQueuePayloadSerializer serializer,
    IQueue queue,
    IDeadLetterWriter deadLetterWriter,
    IMssqlTransactionProvider transactions,
    TimeProvider timeProvider,
    ILogger<MssqlSchedulerSweeper> logger)
    : BackgroundService
{
    /// <summary>How far an unreadable job is pushed out before the sweep sees it again.</summary>
    private static readonly TimeSpan QuarantineDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Scheduler sweep started (every {SweepInterval})", schedulerOptions.Value.SweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Drain everything currently due, one job per transaction, before going back to sleep.
                while (await TryDispatchNextDue(stoppingToken))
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler sweep failed; next sweep continues");
            }

            try
            {
                await Task.Delay(schedulerOptions.Value.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Scheduler sweep stopped");
    }

    /// <summary>Claims and dispatches at most one due job; false means nothing (more) is due.</summary>
    private async Task<bool> TryDispatchNextDue(CancellationToken ct)
    {
        await using var connection = new SqlConnection(queueOptions.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        var now = timeProvider.GetUtcNow();
        var claimed = await ClaimNextDue(connection, transaction, now, ct);
        if (claimed is null)
            return false;

        // Parse the recurrence before doing any work. A row this host cannot parse (written by a
        // newer version before a rollback, or corrupted) must not fail the sweep — that would
        // starve every other due job forever, since the oldest-due poison row is re-selected on
        // each pass. Instead, quarantine it: push its run out and dead-letter the occurrence,
        // leaving it pending so a host version that understands it can still pick it up.
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
                await QuarantineUnreadable(connection, transaction, claimed, now, ex, ct);
                return true;
            }
        }

        try
        {
            // Book the outcome on the claim's transaction: a recurring job's next run advances, a
            // one-shot goes terminal. The booking and the enqueue below commit atomically.
            await using (var book = connection.CreateCommand())
            {
                book.Transaction = transaction;
                book.CommandText = schedule is null ? sql.CompleteOneShot : sql.AdvanceRecurring;
                book.AddParameter("@JobKey", claimed.JobKey);
                book.AddParameter("@Now", now.UtcDateTime);
                if (schedule is null)
                    book.AddParameter("@Status", ScheduledJobStatus.Enqueued);
                else
                    book.AddParameter("@NextExecuteAt", NextRun(schedule, now));
                await book.ExecuteNonQueryAsync(ct);
            }

            transactions.Use(transaction);
            try
            {
                await Enqueue(claimed, ct);
            }
            finally
            {
                transactions.Use(null);
            }

            await transaction.CommitAsync(CancellationToken.None);
            logger.LogInformation("Scheduled job {JobKey} ({Type}) enqueued", claimed.JobKey, claimed.RequestTypeName);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled job {JobKey} ({Type}) failed to enqueue", claimed.JobKey, claimed.RequestTypeName);
            await TryRollback(transaction);
            // A failed compensation leaves the job due, so continuing the drain would re-claim it
            // in a tight loop — report "nothing dispatched" instead so the sweep interval backs off.
            return await Compensate(claimed, schedule, ex, CancellationToken.None);
        }
    }

    /// <summary>
    /// Books an unreadable job out of the sweep's way on the claim's transaction: run pushed
    /// forward, occurrence dead-lettered, status left pending so it recovers by itself once a
    /// host that can parse it runs (or the row is repaired).
    /// </summary>
    private async Task QuarantineUnreadable(
        SqlConnection connection, SqlTransaction transaction, ScheduledJobRow job,
        DateTimeOffset now, Exception failure, CancellationToken ct)
    {
        await using (var book = connection.CreateCommand())
        {
            book.Transaction = transaction;
            book.CommandText = sql.AdvanceRecurring;
            book.AddParameter("@JobKey", job.JobKey);
            book.AddParameter("@Now", now.UtcDateTime);
            book.AddParameter("@NextExecuteAt", (now + QuarantineDelay).UtcDateTime);
            await book.ExecuteNonQueryAsync(ct);
        }

        transactions.Use(transaction);
        try
        {
            await deadLetterWriter.WriteAsync(new DeadLetterEntry
            {
                IdempotencyKey = $"{job.JobKey}:{now.UtcTicks}",
                Source = DeadLetterSource.Scheduler,
                CommandTypeName = job.RequestTypeName,
                PayloadJson = job.PayloadJson,
                Error = failure.Message,
                Attempts = 1,
            }, ct);
        }
        finally
        {
            transactions.Use(null);
        }

        await transaction.CommitAsync(CancellationToken.None);
    }

    /// <summary>
    /// The claim transaction may already be dead server-side (deadlock victim); rolling back a
    /// completed transaction throws, and the claim is released either way.
    /// </summary>
    private static async Task TryRollback(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Already rolled back by the server.
        }
    }

    private async Task<ScheduledJobRow?> ClaimNextDue(
        SqlConnection connection, SqlTransaction transaction, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.ClaimNextDue;
        command.AddParameter("@Status", ScheduledJobStatus.Pending);
        command.AddParameter("@Now", now.UtcDateTime);

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
        var requestType = registry.Resolve(job.RequestTypeName).RequestType;
        var request = (IRequest<Unit>)(serializer.Deserialize(job.PayloadJson, requestType)
            ?? throw new InvalidOperationException($"Deserialized null for '{job.RequestTypeName}'."));

        await queue.Enqueue(request, ct: ct);
    }

    /// <summary>
    /// After a failed dispatch: re-book the job on a fresh transaction, guarded so a competing
    /// sweep that claimed the job after our rollback wins and this becomes a no-op. Only when the
    /// re-booking applied is the failure dead-lettered — otherwise the occurrence wasn't ours to
    /// record. Returns false when even the compensation failed — the job is then still due, and
    /// the caller must stop draining so the sweep interval provides the backoff.
    /// </summary>
    private async Task<bool> Compensate(
        ScheduledJobRow job, Schedule? schedule, Exception failure, CancellationToken ct)
    {
        try
        {
            var now = timeProvider.GetUtcNow();

            await using var connection = new SqlConnection(queueOptions.Value.ConnectionString);
            await connection.OpenAsync(ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

            int booked;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.AddParameter("@JobKey", job.JobKey);
                command.AddParameter("@Now", now.UtcDateTime);
                if (schedule is null)
                {
                    command.CommandText = sql.CompensateOneShot;
                    command.AddParameter("@FailedStatus", ScheduledJobStatus.Failed);
                    command.AddParameter("@PendingStatus", ScheduledJobStatus.Pending);
                }
                else
                {
                    command.CommandText = sql.CompensateRecurring;
                    command.AddParameter("@Status", ScheduledJobStatus.Pending);
                    command.AddParameter("@NextExecuteAt", NextRun(schedule, now));
                }
                booked = await command.ExecuteNonQueryAsync(ct);
            }

            if (booked == 1)
            {
                // Each failed recurring occurrence is distinct, so suffix the key rather than
                // dedupe on the job key; a one-shot fails at most once, so its key suffices.
                var idempotencyKey = schedule is null
                    ? job.JobKey
                    : $"{job.JobKey}:{now.UtcTicks}";

                transactions.Use(transaction);
                try
                {
                    await deadLetterWriter.WriteAsync(new DeadLetterEntry
                    {
                        IdempotencyKey = idempotencyKey,
                        Source = DeadLetterSource.Scheduler,
                        CommandTypeName = job.RequestTypeName,
                        PayloadJson = job.PayloadJson,
                        Error = failure.Message,
                        Attempts = 1,
                    }, ct);
                }
                finally
                {
                    transactions.Use(null);
                }
            }

            await transaction.CommitAsync(ct);
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

    private static DateTime NextRun(Schedule schedule, DateTimeOffset now) =>
        schedule.NextRun(Instant.FromDateTimeOffset(now)).ToDateTimeUtc();

    private sealed record ScheduledJobRow(string JobKey, string RequestTypeName, string PayloadJson, string? ScheduleText);
}
