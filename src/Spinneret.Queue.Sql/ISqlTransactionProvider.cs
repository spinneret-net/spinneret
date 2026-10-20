using System.Data.Common;

namespace Spinneret.Queue.Sql;

/// <summary>The ambient SQL transaction for the current async flow, which <see cref="IQueue"/> enqueues join.</summary>
/// <remarks>Register your own implementation before the storage to replace the AsyncLocal default.</remarks>
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
