using System.Data.Common;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// The ambient SQL transaction for the current async flow — the seam that makes queue operations
/// transactional without threading a transaction parameter through every call site. It carries in
/// both directions: application code publishes its unit-of-work transaction here so
/// <see cref="IQueue"/> enqueues join it, and the delivery worker publishes its per-message
/// transaction here so a handler's writes, cascade enqueues, retry bookings and dead-letters all
/// commit atomically with the dequeue. Hosts with their own ambient-transaction mechanism can
/// replace the default AsyncLocal-backed implementation by registering theirs before
/// <c>AddMssqlQueue</c>.
/// </summary>
public interface IMssqlTransactionProvider
{
    /// <summary>The transaction for the current async flow, or null when none is active.</summary>
    DbTransaction? Current { get; }

    /// <summary>Publishes <paramref name="transaction"/> to the current async flow; null clears it.</summary>
    void Use(DbTransaction? transaction);
}

internal sealed class AsyncLocalMssqlTransactionProvider : IMssqlTransactionProvider
{
    private readonly AsyncLocal<DbTransaction?> _current = new();

    public DbTransaction? Current => _current.Value;

    public void Use(DbTransaction? transaction) => _current.Value = transaction;
}
