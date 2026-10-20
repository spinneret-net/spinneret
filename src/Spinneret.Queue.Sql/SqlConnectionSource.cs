using System.Data.Common;

namespace Spinneret.Queue.Sql;

// Every statement goes through here so none silently opts out of the caller's ambient transaction.
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

    public static DbCommand CreateCommand(this DbConnection connection, DbTransaction? transaction, string text)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }
}
