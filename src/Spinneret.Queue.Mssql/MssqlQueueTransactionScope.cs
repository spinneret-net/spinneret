using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// Runs grouped queue operations in one database transaction, published through
/// <see cref="IMssqlTransactionProvider"/> so <see cref="MssqlQueue"/> and
/// <see cref="MssqlDeadLetterStore"/> enlist in it without being handed it. That is what makes a
/// resend's enqueue and its dead-letter delete a single commit here, where a transport without a
/// shared database can only order them.
/// </summary>
internal sealed class MssqlQueueTransactionScope(
    IOptions<MssqlQueueOptions> options,
    IMssqlTransactionProvider transactions)
    : IQueueTransactionScope
{
    public async Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Already inside a caller's unit of work — the enclosing transaction is the wider guarantee,
        // and opening a second connection here would deadlock against the rows it holds.
        if (transactions.Current is not null)
        {
            await work(ct);
            return;
        }

        await using var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        transactions.Use(transaction);
        try
        {
            await work(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            // Cleared before the transaction is disposed, so nothing further in this async flow can
            // enlist in a transaction that is on its way out. A throw leaves the commit unreached and
            // disposal rolls back.
            transactions.Use(null);
        }
    }
}
