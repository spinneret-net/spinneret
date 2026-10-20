using System.Data.Common;
using Spinneret.Mediator;
using Spinneret.Queue;
using Spinneret.Queue.Sql;

namespace Spinneret.Scheduler.Sql;

internal sealed class SqlScheduler(
    SqlSchedulerStorage storage,
    QueueTypeRegistry registry,
    IQueuePayloadSerializer serializer,
    TimeProvider timeProvider)
    : IRecurringJobScheduler
{
    public async Task RegisterAsync<TResponse>(
        string key, IRequest<TResponse> request, Schedule schedule, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A recurring job requires a stable key.", nameof(key));
        ArgumentNullException.ThrowIfNull(schedule);

        var requestType = request.GetType();
        var now = timeProvider.GetUtcNow();
        var timestamps = storage.Queue.Dialect;

        // One statement, so replicas registering concurrently at startup converge instead of colliding.
        await using var connection = await storage.Queue.DataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand(transaction: null, storage.Statements.UpsertRecurring);
        command.AddParameter("@JobKey", key);
        command.AddParameter("@RequestTypeName", registry.GetName(requestType));
        command.AddParameter("@PayloadJson", serializer.Serialize(request, requestType));
        command.AddParameter("@Schedule", schedule.ToString());
        timestamps.AddTimestamp(command, "@NextExecuteAt", schedule.NextRun(now));
        timestamps.AddTimestamp(command, "@CreatedAt", now);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UnregisterAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A recurring job requires a stable key.", nameof(key));

        await using var connection = await storage.Queue.DataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand(transaction: null, storage.Statements.DeleteRecurring);
        command.AddParameter("@JobKey", key);
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Schedules and cancels one-shot jobs in a caller-owned SQL transaction, committing atomically with the caller's changes.
/// </summary>
public interface ISqlTransactionalScheduler
{
    /// <summary>
    /// Inserts a one-shot job to run once at <paramref name="executeAt"/> on <paramref name="transaction"/>.
    /// </summary>
    /// <returns>The handle to pass to <see cref="CancelJobAsync"/>.</returns>
    Task<string> ScheduleJobAsync<TResponse>(
        DbTransaction transaction, IRequest<TResponse> request, DateTimeOffset executeAt, CancellationToken ct = default);

    /// <summary>
    /// Cancels the job identified by <paramref name="handle"/>; an unknown or already-run handle is a no-op.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="handle"/> was not issued by <see cref="ScheduleJobAsync{TResponse}"/>; use <c>UnregisterAsync</c> for recurring jobs.
    /// </exception>
    Task CancelJobAsync(DbTransaction transaction, string handle, CancellationToken ct = default);
}

internal sealed class SqlTransactionalScheduler(
    SqlSchedulerStorage storage,
    QueueTypeRegistry registry,
    IQueuePayloadSerializer serializer,
    TimeProvider timeProvider)
    : ISqlTransactionalScheduler
{
    public async Task<string> ScheduleJobAsync<TResponse>(
        DbTransaction transaction, IRequest<TResponse> request, DateTimeOffset executeAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var requestType = request.GetType();
        var handle = ScheduledJobHandle.New();
        var timestamps = storage.Queue.Dialect;

        await using var command = SqlConnectionSource.ConnectionOf(transaction)
            .CreateCommand(transaction, storage.Statements.InsertOneShot);
        command.AddParameter("@JobKey", handle);
        command.AddParameter("@RequestTypeName", registry.GetName(requestType));
        command.AddParameter("@PayloadJson", serializer.Serialize(request, requestType));
        timestamps.AddTimestamp(command, "@NextExecuteAt", executeAt);
        timestamps.AddTimestamp(command, "@CreatedAt", timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(ct);

        return handle;
    }

    public async Task CancelJobAsync(DbTransaction transaction, string handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        if (!ScheduledJobHandle.IsOneShot(handle))
            throw new ArgumentException(
                $"'{handle}' is not a one-shot job handle. Recurring jobs are retired with UnregisterAsync.",
                nameof(handle));

        await using var command = SqlConnectionSource.ConnectionOf(transaction)
            .CreateCommand(transaction, storage.Statements.Cancel);
        command.AddParameter("@JobKey", handle);
        await command.ExecuteNonQueryAsync(ct);
    }
}
