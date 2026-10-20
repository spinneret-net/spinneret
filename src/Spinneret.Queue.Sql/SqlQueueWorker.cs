using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Sql;

/// <summary>
/// In-host delivery: polls the queue table and delivers messages to the
/// <see cref="QueueDeliveryPipeline"/>, one polling loop per (channel × configured parallelism).
/// Each delivery runs in its own unit of work (<see cref="SqlUnitOfWorkFactory"/>): the destructive
/// dequeue is the lock, the transaction is the ambient one so the handler's writes, cascade
/// enqueues, retry bookings and dead-letters join it, and the commit the pipeline makes on
/// acknowledge makes the whole delivery atomic. A crash or rollback puts the message back as-is —
/// the transport-redelivery path that deliberately does not spend the attempt budget.
/// </summary>
internal sealed class SqlQueueWorker(
    IOptions<InHostDeliveryOptions> options,
    SqlQueueStorage storage,
    QueueTypeRegistry registry,
    ISqlTransactionProvider transactions,
    SqlUnitOfWorkFactory unitOfWork,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SqlQueueWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channels = registry.DeclaredChannels
            .Append(QueuePolicy.DefaultChannel)
            .Distinct(StringComparer.Ordinal);

        var loops = channels
            .SelectMany(channel => Enumerable
                .Range(0, options.Value.ChannelParallelism.GetValueOrDefault(channel, 1))
                .Select(_ => PollLoop(channel, stoppingToken)))
            .ToArray();

        await Task.WhenAll(loops);
    }

    private async Task PollLoop(string channel, CancellationToken ct)
    {
        logger.LogInformation("Queue worker started for channel {Channel}", channel);

        while (!ct.IsCancellationRequested)
        {
            bool delivered;
            try
            {
                delivered = await TryDeliverOne(channel, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A poll failure (connection loss, timeout) must never kill the loop: the message —
                // if one was claimed — rolled back and will be redelivered.
                logger.LogError(ex,
                    "Queue delivery on channel {Channel} failed; polling continues", channel);
                delivered = false;
            }

            if (!delivered)
            {
                try
                {
                    await Task.Delay(options.Value.PollInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Queue worker stopped for channel {Channel}", channel);
    }

    /// <summary>Claims and processes at most one message; false means the channel was empty.</summary>
    private async Task<bool> TryDeliverOne(string channel, CancellationToken ct)
    {
        var delivery = await unitOfWork.ExecuteAsync(async (unit, token) =>
        {
            var transaction = transactions.Current
                ?? throw new InvalidOperationException("The delivery unit of work published no transaction.");

            var claimed = await Dequeue(transaction, channel, token);
            if (claimed is not var (id, messageId, envelopeJson))
                return (Claimed: false, Id: 0L, Outcome: QueueDeliveryOutcome.Acked);

            // From here on the message is ours until the unit commits or rolls back. Scope per
            // message, like the Cloud Tasks endpoint gets a scope per request; the ambient transaction
            // makes every SQL-touching service in the scope join this delivery.
            using var scope = scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<QueueDeliveryPipeline>();
            var outcome = await pipeline.DeliverAsync(unit, envelopeJson, messageId.ToString("N"), token);
            return (Claimed: true, Id: id, Outcome: outcome);
        }, ct);

        if (!delivery.Claimed)
            return false;

        // Not acknowledged: the outcome could not be booked, so the unit rolled the claim back. Push
        // the message's visibility out so the redelivery honors the delay, without spending the
        // attempt budget. If even that fails the message just reappears immediately.
        if (delivery.Outcome.RetryAfter is { } retryAfter)
            await Reschedule(delivery.Id, retryAfter);

        return true;
    }

    private async Task<(long Id, Guid MessageId, string Envelope)?> Dequeue(
        DbTransaction transaction, string channel, CancellationToken ct)
    {
        await using var command = SqlConnectionSource.ConnectionOf(transaction)
            .CreateCommand(transaction, storage.Statements.Dequeue);
        command.AddParameter("@Channel", channel);
        storage.Dialect.AddTimestamp(command, "@Now", timeProvider.GetUtcNow());

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return (reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2));
    }

    /// <summary>
    /// Best-effort visibility push on a fresh connection, after the delivery transaction rolled
    /// back. Uses no cancellation token: this is shutdown-safe cleanup, and skipping it only costs
    /// an immediate redelivery.
    /// </summary>
    private async Task Reschedule(long id, TimeSpan delay)
    {
        try
        {
            await using var connection = await storage.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand(transaction: null, storage.Statements.Reschedule);
            storage.Dialect.AddTimestamp(command, "@VisibleAt", timeProvider.GetUtcNow() + delay);
            command.AddParameter("@Id", id);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to delay redelivery of queue message {MessageId}; it will be redelivered immediately", id);
        }
    }
}
