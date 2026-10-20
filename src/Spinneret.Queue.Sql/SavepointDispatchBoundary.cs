using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.Sql;

// A savepoint per handler lets a failure discard its writes while the delivery transaction survives
// to book the retry or dead-letter atomically with the acknowledge.
internal sealed class SavepointDispatchBoundary(
    SqlQueueStorage storage,
    ISqlTransactionProvider transactions,
    ILogger<SavepointDispatchBoundary> logger)
    : IQueueDispatchBoundary
{
    private const string SavepointName = "SpinneretDispatch";

    public async Task ExecuteAsync(QueueDeliveryContext context, Func<Task> dispatch, CancellationToken ct)
    {
        if (transactions.Current is not { } transaction)
        {
            await dispatch();
            return;
        }

        await storage.Dialect.SaveAsync(transaction, SavepointName, ct);
        try
        {
            await dispatch();

            // A handler that swallowed a database error can leave the transaction aborted (Postgres);
            // committing it would silently roll back the acknowledge too.
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
                // The transaction is dead (e.g. deadlock victim); booking will fail too and fall back
                // to redelivery, so log rather than mask the handler's exception.
                logger.LogWarning(rollbackEx,
                    "Savepoint rollback failed for {RequestType}; the delivery transaction is no longer usable",
                    context.Envelope.RequestTypeName);
            }

            throw;
        }
    }
}
