using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// Wraps each handler invocation in a transaction savepoint. A failed handler's partial writes —
/// including any cascade enqueues it made — are rolled back to the savepoint, while the delivery
/// transaction survives so the processor can still book the retry or dead-letter atomically with
/// the dequeue. Without this, either a failed handler's half-finished writes would commit alongside
/// the retry booking, or the whole delivery would roll back and lose the attempt accounting.
/// </summary>
internal sealed class MssqlDispatchBoundary(
    IMssqlTransactionProvider transactions,
    ILogger<MssqlDispatchBoundary> logger)
    : IQueueDispatchBoundary
{
    private const string SavepointName = "SpinneretDispatch";

    public async Task ExecuteAsync(QueueDeliveryContext context, Func<Task> dispatch, CancellationToken ct)
    {
        var envelope = context.Envelope;

        // No ambient SQL transaction (or a non-SQL one): nothing to bracket. Savepoints only exist
        // on SqlTransaction, and only the delivery worker publishes one here.
        if (transactions.Current is not SqlTransaction transaction)
        {
            await dispatch();
            return;
        }

        transaction.Save(SavepointName);
        try
        {
            await dispatch();
        }
        catch
        {
            try
            {
                transaction.Rollback(SavepointName);
            }
            catch (Exception rollbackEx)
            {
                // The transaction itself is dead (e.g. this delivery was a deadlock victim). The
                // processor's booking will fail on the same dead transaction and fall back to
                // transport redelivery, which is exactly the safe outcome — so log, don't mask
                // the handler's exception.
                logger.LogWarning(rollbackEx,
                    "Savepoint rollback failed for {RequestType}; the delivery transaction is no longer usable",
                    envelope.RequestTypeName);
            }

            throw;
        }
    }
}
