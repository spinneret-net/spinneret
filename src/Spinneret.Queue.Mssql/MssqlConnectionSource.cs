using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// Runs a statement on the ambient transaction when one is active, and on a short-lived connection
/// of its own when none is. Every dead-letter operation needs that same choice, and getting it wrong
/// in one of them would quietly opt that statement out of the caller's unit of work.
/// </summary>
internal sealed class MssqlConnectionSource(
    IOptions<MssqlQueueOptions> options,
    IMssqlTransactionProvider transactions)
{
    public async Task<T> ExecuteAsync<T>(
        Func<DbConnection, DbTransaction?, Task<T>> work, CancellationToken ct)
    {
        if (transactions.Current is { } transaction)
        {
            var connection = transaction.Connection
                ?? throw new InvalidOperationException("The ambient transaction has no open connection.");
            return await work(connection, transaction);
        }

        await using var ownConnection = new SqlConnection(options.Value.ConnectionString);
        await ownConnection.OpenAsync(ct);
        return await work(ownConnection, null);
    }

    public async Task ExecuteAsync(Func<DbConnection, DbTransaction?, Task> work, CancellationToken ct) =>
        await ExecuteAsync<object?>(async (connection, transaction) =>
        {
            await work(connection, transaction);
            return null;
        }, ct);
}
