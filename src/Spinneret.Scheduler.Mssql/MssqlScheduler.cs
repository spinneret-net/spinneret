using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Spinneret.Mediator;
using Spinneret.Queue;
using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql;

internal sealed class MssqlScheduler(
    IOptions<MssqlQueueOptions> queueOptions,
    ScheduledJobSql sql,
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
        var requestTypeName = registry.GetName(requestType);
        var payloadJson = serializer.Serialize(request, requestType);
        var scheduleText = schedule.ToString();

        await using var connection = new SqlConnection(queueOptions.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        var existing = await SelectForRegister(connection, transaction, key, ct);

        if (existing is null)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = sql.Insert;
            insert.AddParameter("@JobKey", key);
            insert.AddParameter("@RequestTypeName", requestTypeName);
            insert.AddParameter("@PayloadJson", payloadJson);
            insert.AddParameter("@Schedule", scheduleText);
            insert.AddParameter("@NextExecuteAt", NextRunFromNow(schedule));
            insert.AddParameter("@CreatedAt", timeProvider.GetUtcNow().UtcDateTime);
            await insert.ExecuteNonQueryAsync(ct);
        }
        else
        {
            // Idempotent refresh: update the definition in place. Re-arm only if the schedule itself
            // changed; an unchanged schedule keeps its cadence so frequent restarts never reset it.
            // A job that went terminal doesn't reach here at all — it was deleted, so the branch
            // above re-creates it.
            var rearm = existing.Schedule != scheduleText;

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = rearm ? sql.UpdateDefinitionAndRearm : sql.UpdateDefinition;
            update.AddParameter("@JobKey", key);
            update.AddParameter("@RequestTypeName", requestTypeName);
            update.AddParameter("@PayloadJson", payloadJson);
            update.AddParameter("@Schedule", scheduleText);
            if (rearm)
                update.AddParameter("@NextExecuteAt", NextRunFromNow(schedule));
            await update.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task UnregisterAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A recurring job requires a stable key.", nameof(key));

        await using var connection = new SqlConnection(queueOptions.Value.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql.DeleteRecurring;
        command.AddParameter("@JobKey", key);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The already-registered job under this key, or null if there is none. Existence is the whole
    /// state — a job that ran, failed or was cancelled was deleted, so a row here is always live.
    /// </summary>
    private async Task<ExistingJob?> SelectForRegister(
        SqlConnection connection, SqlTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.SelectForRegister;
        command.AddParameter("@JobKey", key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new ExistingJob(reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    /// <summary>Schedule is null for a one-shot job sharing the key namespace.</summary>
    private sealed record ExistingJob(string? Schedule);

    private DateTime NextRunFromNow(Schedule schedule) =>
        schedule.NextRun(timeProvider.GetUtcNow()).UtcDateTime;
}
