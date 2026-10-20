using System.Data.Common;

namespace Spinneret.Queue.Sql;

internal sealed class SqlUnitOfWorkFactory(
    SqlQueueStorage storage,
    ISqlTransactionProvider transactions,
    ISqlOutboxObserver observer)
    : IQueueUnitOfWorkFactory
{
    public async Task<T> ExecuteAsync<T>(
        Func<IQueueUnitOfWork, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Join the caller's transaction; a second connection would deadlock against its rows.
        if (transactions.Current is not null)
            return await work(JoinedUnit.Instance, ct);

        await using var connection = await storage.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var unit = new OwnedUnit(transaction);

        transactions.Use(transaction);
        try
        {
            return await work(unit, ct);
        }
        finally
        {
            // Cleared first, so neither later work nor the observer enlists in a finished transaction.
            transactions.Use(null);
            if (unit.Committed)
                await observer.CommittedAsync(transaction);
            else
                await TryRollback(transaction);
        }
    }

    // The server may already have rolled back (deadlock victim); that throw must not mask the work's exception.
    private static async Task TryRollback(DbTransaction transaction)
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

    private sealed class OwnedUnit(DbTransaction transaction) : IQueueUnitOfWork
    {
        public bool Committed { get; private set; }

        public async Task CommitAsync(CancellationToken ct)
        {
            await transaction.CommitAsync(ct);
            Committed = true;
        }
    }

    private sealed class JoinedUnit : IQueueUnitOfWork
    {
        public static readonly JoinedUnit Instance = new();

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
