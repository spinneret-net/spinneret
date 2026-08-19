namespace Spinneret.Queue.Mssql;

/// <summary>
/// Dead letters as rows in the application database. Joins the ambient transaction when one is
/// active — during delivery that is the worker's per-message transaction, so a dead-letter commits
/// atomically with the delete of the message it records. Duplicate idempotency keys are swallowed
/// in SQL, keeping redelivered writes idempotent.
/// </summary>
internal sealed class MssqlDeadLetterWriter(
    MssqlQueueSql sql,
    MssqlConnectionSource connections,
    TimeProvider timeProvider)
    : IDeadLetterWriter
{
    public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default) =>
        connections.ExecuteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql.WriteDeadLetter;
            command.AddParameter("@IdempotencyKey", entry.IdempotencyKey);
            command.AddParameter("@Source", DeadLetterStorage.FormatSource(entry.Source));
            command.AddParameter("@CommandTypeName", entry.CommandTypeName);
            command.AddParameter("@Description", entry.Description);
            command.AddParameter("@PayloadJson", entry.PayloadJson);
            command.AddParameter("@Error", entry.Error);
            command.AddParameter("@Attempts", entry.Attempts);
            command.AddParameter("@TraceId", entry.TraceId);
            command.AddDateTime2Parameter("@DeadLetteredAt", timeProvider.GetUtcNow());
            await command.ExecuteNonQueryAsync(ct);
        }, ct);
}
