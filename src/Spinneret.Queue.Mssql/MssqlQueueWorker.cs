using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// Polls the queue table and delivers messages to <see cref="IQueueDeliveryProcessor"/>, one
/// polling loop per (channel × configured parallelism). Each delivery runs in its own SQL
/// transaction: the destructive dequeue is the lock, the transaction is published as the ambient
/// one so the handler's writes, cascade enqueues, retry bookings and dead-letters join it, and the
/// final commit makes the whole delivery atomic. A crash or rollback puts the message back as-is —
/// the transport-redelivery path that deliberately does not spend the attempt budget.
/// </summary>
internal sealed class MssqlQueueWorker(
    IOptions<MssqlQueueOptions> options,
    MssqlQueueSql sql,
    QueueTypeRegistry registry,
    IMssqlTransactionProvider transactions,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<MssqlQueueWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan UnreadableEnvelopeRetryBackoff = TimeSpan.FromMinutes(1);

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
        await using var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        var claimed = await Dequeue(connection, transaction, channel, ct);
        if (claimed is not var (id, envelopeJson))
            return false;

        // From here on the message is ours until commit/rollback. Scope per message, like the GCP
        // endpoint gets a scope per request; the ambient transaction makes every SQL-touching
        // service in the scope join this delivery.
        using var scope = scopeFactory.CreateScope();
        transactions.Use(transaction);
        try
        {
            var envelope = TryReadEnvelope(envelopeJson);
            if (envelope is null)
            {
                await DeadLetterUnreadable(scope.ServiceProvider, id, envelopeJson, transaction, ct);
                return true;
            }

            var processor = scope.ServiceProvider.GetRequiredService<IQueueDeliveryProcessor>();
            var outcome = await processor.ProcessAsync(envelope, id.InvariantTaskId(), ct);

            if (outcome.Ack)
            {
                await transaction.CommitAsync(CancellationToken.None);
                return true;
            }

            // The processor could not book the outcome (its writes on this transaction failed), so
            // redeliver later without spending the attempt budget: roll the claim back and push the
            // message's visibility out. If even that fails the message just reappears immediately.
            await TryRollback(transaction);
            await Reschedule(id, outcome.RetryAfter!.Value);
            return true;
        }
        finally
        {
            transactions.Use(null);
        }
    }

    private async Task<(long Id, string Envelope)?> Dequeue(
        SqlConnection connection, SqlTransaction transaction, string channel, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.Dequeue;
        command.AddParameter("@Channel", channel);
        command.AddParameter("@Now", timeProvider.GetUtcNow().UtcDateTime);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return (reader.GetInt64(0), reader.GetString(1));
    }

    private static QueueEnvelope? TryReadEnvelope(string envelopeJson)
    {
        try
        {
            return JsonSerializer.Deserialize<QueueEnvelope>(envelopeJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A row whose envelope cannot be read can never be processed: dead-letter the raw content and
    /// commit the delete. If the dead-letter write itself fails, roll back and retry later — never
    /// drop the row without a trace.
    /// </summary>
    private async Task DeadLetterUnreadable(
        IServiceProvider scopedServices, long id, string envelopeJson, SqlTransaction transaction, CancellationToken ct)
    {
        try
        {
            var deadLetterWriter = scopedServices.GetRequiredService<IDeadLetterWriter>();
            await deadLetterWriter.WriteAsync(new DeadLetterEntry
            {
                IdempotencyKey = id.InvariantTaskId(),
                Source = DeadLetterSource.Queue,
                CommandTypeName = "<unreadable envelope>",
                PayloadJson = envelopeJson,
                Error = "The queue message envelope could not be deserialized.",
                Attempts = 1,
            }, ct);

            await transaction.CommitAsync(CancellationToken.None);
            logger.LogError("Dead-lettered queue message {MessageId} with an unreadable envelope", id);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to dead-letter unreadable queue message {MessageId}; it will be redelivered", id);
            await TryRollback(transaction);
            await Reschedule(id, UnreadableEnvelopeRetryBackoff);
        }
    }

    /// <summary>
    /// The delivery transaction may already be dead server-side (e.g. this delivery was a deadlock
    /// victim, which rolls the whole transaction back on the server). Rolling back a completed
    /// transaction throws — and must not, or the Reschedule that applies the retry backoff would
    /// be skipped and the message would hot-loop at the poll interval instead.
    /// </summary>
    private static async Task TryRollback(SqlTransaction transaction)
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

    /// <summary>
    /// Best-effort visibility push on a fresh connection, after the delivery transaction rolled
    /// back. Uses no cancellation token: this is shutdown-safe cleanup, and skipping it only costs
    /// an immediate redelivery.
    /// </summary>
    private async Task Reschedule(long id, TimeSpan delay)
    {
        try
        {
            await using var connection = new SqlConnection(options.Value.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql.Reschedule;
            command.AddParameter("@VisibleAt", (timeProvider.GetUtcNow() + delay).UtcDateTime);
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
