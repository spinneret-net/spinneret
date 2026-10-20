using System.Data.Common;

namespace Spinneret.Queue.Sql;

/// <summary>
/// Runs a statement on the ambient transaction when one is active, and on a short-lived connection
/// of its own when none is. Every queue and dead-letter operation needs that same choice, and getting
/// it wrong in one of them would quietly opt that statement out of the caller's unit of work.
/// </summary>
internal sealed class SqlConnectionSource(SqlQueueStorage storage, ISqlTransactionProvider transactions)
{
    public async Task<T> ExecuteAsync<T>(
        Func<DbConnection, DbTransaction?, Task<T>> work, CancellationToken ct)
    {
        if (transactions.Current is { } transaction)
            return await work(ConnectionOf(transaction), transaction);

        await using var ownConnection = await storage.DataSource.OpenConnectionAsync(ct);
        return await work(ownConnection, null);
    }

    public async Task ExecuteAsync(Func<DbConnection, DbTransaction?, Task> work, CancellationToken ct) =>
        await ExecuteAsync<object?>(async (connection, transaction) =>
        {
            await work(connection, transaction);
            return null;
        }, ct);

    /// <summary>The connection a caller-supplied transaction runs on.</summary>
    public static DbConnection ConnectionOf(DbTransaction transaction) =>
        transaction.Connection
        ?? throw new InvalidOperationException("The transaction has no open connection — it has already completed.");
}

internal static class DbCommandExtensions
{
    public static void AddParameter(this DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Creates a command on <paramref name="connection"/>, enlisted in <paramref name="transaction"/>.</summary>
    public static DbCommand CreateCommand(this DbConnection connection, DbTransaction? transaction, string text)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }
}
