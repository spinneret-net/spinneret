using System.Data.Common;
using Spinneret.Mediator;
using Spinneret.Queue;
using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// Schedules and cancels one-shot jobs as part of a caller-owned SQL transaction, so the job row
/// commits atomically with the caller's other changes (e.g. scheduling an employee's removal in
/// the same transaction that records the termination). The job row is identical to those the
/// standalone scheduler writes, so the shared dispatch sweep picks it up uniformly.
/// </summary>
public interface IMssqlTransactionalScheduler
{
    /// <summary>
    /// Inserts a one-shot job — to run once at <paramref name="executeAt"/> — on the given
    /// <paramref name="transaction"/>. Returns the handle to pass to <see cref="CancelJobAsync"/>.
    /// </summary>
    Task<string> ScheduleJobAsync<TResponse>(
        DbTransaction transaction, IRequest<TResponse> request, DateTimeOffset executeAt, CancellationToken ct = default);

    /// <summary>Cancels the job identified by <paramref name="handle"/> within the transaction.</summary>
    Task CancelJobAsync(DbTransaction transaction, string handle, CancellationToken ct = default);
}

internal sealed class MssqlTransactionalScheduler(
    ScheduledJobSql sql,
    QueueTypeRegistry registry,
    IQueuePayloadSerializer serializer,
    TimeProvider timeProvider)
    : IMssqlTransactionalScheduler
{
    public async Task<string> ScheduleJobAsync<TResponse>(
        DbTransaction transaction, IRequest<TResponse> request, DateTimeOffset executeAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The supplied transaction has no open connection.");

        var requestType = request.GetType();
        var handle = $"oneshot-{Guid.NewGuid():N}";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.Insert;
        command.AddParameter("@JobKey", handle);
        command.AddParameter("@RequestTypeName", registry.GetName(requestType));
        command.AddParameter("@PayloadJson", serializer.Serialize(request, requestType));
        command.AddParameter("@Status", ScheduledJobStatus.Pending);
        command.AddParameter("@Schedule", null);
        command.AddParameter("@NextExecuteAt", executeAt.UtcDateTime);
        command.AddParameter("@CreatedAt", timeProvider.GetUtcNow().UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);

        return handle;
    }

    public async Task CancelJobAsync(DbTransaction transaction, string handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The supplied transaction has no open connection.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.Cancel;
        command.AddParameter("@JobKey", handle);
        command.AddParameter("@CancelledStatus", ScheduledJobStatus.Cancelled);
        command.AddParameter("@PendingStatus", ScheduledJobStatus.Pending);
        await command.ExecuteNonQueryAsync(ct);
    }
}
