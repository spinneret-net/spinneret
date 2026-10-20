using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.Sql;

/// <summary>
/// Wraps each handler invocation in a transaction savepoint. A failed handler's partial writes —
/// including any cascade enqueues it made — are rolled back to the savepoint, while the delivery
/// transaction survives so the processor can still book the retry or dead-letter atomically with the
/// acknowledge. Without this, either a failed handler's half-finished writes would commit alongside
/// the retry booking, or the whole delivery would roll back and lose the attempt accounting.
/// </summary>
internal sealed class SavepointDispatchBoundary(
    SqlQueueStorage storage,
    ISqlTransactionProvider transactions,
    ILogger<SavepointDispatchBoundary> logger)
    : IQueueDispatchBoundary
{
    private const string SavepointName = "SpinneretDispatch";

    public async Task ExecuteAsync(QueueDeliveryContext context, Func<Task> dispatch, CancellationToken ct)
    {
        // No ambient transaction: nothing to bracket. Only a delivery's unit of work publishes one
        // around the processor.
        if (transactions.Current is not { } transaction)
        {
            await dispatch();
            return;
        }

        await storage.Dialect.SaveAsync(transaction, SavepointName, ct);
        try
        {
            await dispatch();

            // A handler that caught a database error and returned normally can leave the transaction
            // refusing all further work (Postgres aborts it on any error). Committing it would then
            // silently roll back — the handler's writes and the acknowledge with it — while the
            // delivery reported success. Treated as the failure it is, the savepoint below restores
            // the transaction and the attempt is booked like any other.
            if (await storage.Dialect.IsAbortedAsync(transaction, ct))
                throw new InvalidOperationException(
                    $"The handler for {context.Envelope.RequestTypeName} returned normally, but a database error "
                    + "it caught aborted the delivery's transaction. Its writes were rolled back and the "
                    + "attempt fails like any other; let the exception propagate, or roll back to a "
                    + "savepoint of the handler's own around the statement that may fail.");
        }
        catch
        {
            try
            {
                await storage.Dialect.RollbackToAsync(transaction, SavepointName, CancellationToken.None);
            }
            catch (Exception rollbackEx)
            {
                // The transaction itself is dead (e.g. this delivery was a deadlock victim). The
                // processor's booking will fail on the same dead transaction and fall back to
                // transport redelivery, which is exactly the safe outcome — so log, don't mask the
                // handler's exception.
                logger.LogWarning(rollbackEx,
                    "Savepoint rollback failed for {RequestType}; the delivery transaction is no longer usable",
                    context.Envelope.RequestTypeName);
            }

            throw;
        }
    }
}
