using System.Data.Common;

namespace Spinneret.Queue.Sql;

/// <summary>
/// Runs a unit of work in one database transaction, published through
/// <see cref="ISqlTransactionProvider"/> so the outbox, the dead-letter writer and the dead-letter
/// store enlist in it without being handed it. That is what makes a delivery's handler writes,
/// bookings and acknowledge one commit, and a resend's enqueue and dead-letter delete another —
/// where storage without a shared database can only order them.
/// </summary>
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

        // Already inside a caller's unit of work — the enclosing transaction is the wider guarantee,
        // and opening a second connection here would deadlock against the rows it holds.
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
            // Cleared before the transaction is disposed, so nothing further in this async flow can
            // enlist in a transaction that is on its way out.
            transactions.Use(null);
            if (unit.Committed)
                // After clearing the ambient transaction, so whatever the observer does — forwarding
                // the messages this unit committed — runs outside a transaction that is finished.
                await observer.CommittedAsync(transaction);
            else
                await TryRollback(transaction);
        }
    }

    /// <summary>
    /// Rolls back explicitly rather than leaving it to disposal, because the transaction may already
    /// be dead server-side (a deadlock victim is rolled back by the server), and rolling back a
    /// completed transaction throws — which must not replace the exception the work threw, or turn a
    /// handled outcome into an error.
    /// </summary>
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
