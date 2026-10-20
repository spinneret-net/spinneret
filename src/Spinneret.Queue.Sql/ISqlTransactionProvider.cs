using System.Data.Common;

namespace Spinneret.Queue.Sql;

/// <summary>
/// The ambient SQL transaction for the current async flow — the seam that makes queue operations
/// transactional without threading a transaction parameter through every call site. It carries in
/// both directions: application code publishes its unit-of-work transaction here so
/// <see cref="IQueue"/> enqueues join it, and a delivery publishes its own transaction here so a
/// handler's writes, cascade enqueues, retry bookings and dead-letters all commit atomically with the
/// acknowledge. Hosts with their own ambient-transaction mechanism can replace the default
/// AsyncLocal-backed implementation by registering theirs before the storage.
/// </summary>
public interface ISqlTransactionProvider
{
    /// <summary>The transaction for the current async flow, or null when none is active.</summary>
    DbTransaction? Current { get; }

    /// <summary>Publishes <paramref name="transaction"/> to the current async flow; null clears it.</summary>
    void Use(DbTransaction? transaction);
}

internal sealed class AsyncLocalSqlTransactionProvider : ISqlTransactionProvider
{
    private readonly AsyncLocal<DbTransaction?> _current = new();

    public DbTransaction? Current => _current.Value;

    public void Use(DbTransaction? transaction) => _current.Value = transaction;
}
