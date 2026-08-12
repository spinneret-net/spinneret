using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// Dead letters as rows in the application database. Joins the ambient transaction when one is
/// active — during delivery that is the worker's per-message transaction, so a dead-letter commits
/// atomically with the delete of the message it records. Duplicate idempotency keys are swallowed
/// in SQL, keeping redelivered writes idempotent.
/// </summary>
internal sealed class MssqlDeadLetterWriter(
    IOptions<MssqlQueueOptions> options,
    MssqlQueueSql sql,
    IMssqlTransactionProvider transactions,
    TimeProvider timeProvider)
    : IDeadLetterWriter
{
    public async Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        if (transactions.Current is { } transaction)
        {
            var connection = transaction.Connection
                ?? throw new InvalidOperationException("The ambient transaction has no open connection.");
            await Insert(connection, transaction, entry, ct);
            return;
        }

        await using var ownConnection = new SqlConnection(options.Value.ConnectionString);
        await ownConnection.OpenAsync(ct);
        await Insert(ownConnection, transaction: null, entry, ct);
    }

    private async Task Insert(DbConnection connection, DbTransaction? transaction, DeadLetterEntry entry, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.WriteDeadLetter;
        command.AddParameter("@IdempotencyKey", entry.IdempotencyKey);
        command.AddParameter("@Source", entry.Source.ToString());
        command.AddParameter("@CommandTypeName", entry.CommandTypeName);
        command.AddParameter("@Description", entry.Description);
        command.AddParameter("@PayloadJson", entry.PayloadJson);
        command.AddParameter("@Error", entry.Error);
        command.AddParameter("@Attempts", entry.Attempts);
        command.AddParameter("@DeadLetteredAt", timeProvider.GetUtcNow().UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);
    }
}
