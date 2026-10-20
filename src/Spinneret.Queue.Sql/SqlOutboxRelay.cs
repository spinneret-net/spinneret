using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.Sql;

// Sends under a name the transport deduplicates on, so a resend after a crash is refused as a duplicate.
// Leases rather than claims, because the network send must not hold a transaction open.
internal sealed class SqlOutboxRelay(
    SqlQueueStorage storage,
    IQueueTransport transport,
    QueueRelaySettings settings,
    IDeadLetterWriter deadLetterWriter,
    TimeProvider timeProvider,
    ILogger<SqlOutboxRelay> logger)
    : IQueueRelay
{
    private static readonly TimeSpan SendFailureBackoff = TimeSpan.FromSeconds(30);

    private const int SendParallelism = 8;

    public async Task<RelayResult> RelayAsync(CancellationToken ct)
    {
        var forwarded = 0;
        var failed = 0;

        while (true)
        {
            var batch = await LeaseBatchAsync(ct);
            if (batch.Count == 0)
                break;

            var sent = 0;
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions { MaxDegreeOfParallelism = SendParallelism, CancellationToken = ct },
                async (message, token) =>
                {
                    if (await SendAsync(message, token))
                        Interlocked.Increment(ref sent);
                });

            forwarded += sent;
            failed += batch.Count - sent;

            // Stop when every send failed, so the trigger interval is the backoff, not a tight loop.
            if (batch.Count < settings.BatchSize || sent == 0)
                break;
        }

        return forwarded == 0 && failed == 0
            ? RelayResult.Nothing
            : new RelayResult { MessagesForwarded = forwarded, MessagesFailed = failed };
    }

    // Best effort: held, not-yet-due or failed messages are left for the passes.
    public async Task ForwardAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken ct)
    {
        foreach (var messageId in messageIds)
        {
            if (await LeaseByIdAsync(messageId, ct) is { } message)
                await SendAsync(message, ct);
        }
    }

    private async Task<List<LeasedMessage>> LeaseBatchAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var token = Guid.NewGuid();

        await using var connection = await storage.DataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand(transaction: null, storage.Statements.LeaseForRelay);
        command.AddParameter("@Batch", settings.BatchSize);
        storage.Dialect.AddTimestamp(command, "@Horizon", now + settings.ForwardAhead);
        storage.Dialect.AddTimestamp(command, "@Now", now);
        storage.Dialect.AddTimestamp(command, "@LeaseUntil", now + settings.LeaseDuration);
        command.AddParameter("@LeaseToken", token);

        return await ReadLeased(command, token, ct);
    }

    private async Task<LeasedMessage?> LeaseByIdAsync(Guid messageId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var token = Guid.NewGuid();

        await using var connection = await storage.DataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand(transaction: null, storage.Statements.LeaseById);
        command.AddParameter("@MessageId", messageId);
        storage.Dialect.AddTimestamp(command, "@Horizon", now + settings.ForwardAhead);
        storage.Dialect.AddTimestamp(command, "@Now", now);
        storage.Dialect.AddTimestamp(command, "@LeaseUntil", now + settings.LeaseDuration);
        command.AddParameter("@LeaseToken", token);

        return (await ReadLeased(command, token, ct)).SingleOrDefault();
    }

    private async Task<List<LeasedMessage>> ReadLeased(DbCommand command, Guid token, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);

        var leased = new List<LeasedMessage>();
        while (await reader.ReadAsync(ct))
        {
            leased.Add(new LeasedMessage(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                storage.Dialect.ReadTimestamp(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                token));
        }

        return leased;
    }

    private async Task<bool> SendAsync(LeasedMessage message, CancellationToken ct)
    {
        QueueEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<QueueEnvelope>(message.Envelope);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        try
        {
            if (envelope is null)
            {
                await DeadLetterUnreadable(message, ct);
            }
            else
            {
                var now = timeProvider.GetUtcNow();
                await transport.SendAsync(new OutboundMessage(
                    envelope,
                    message.Channel,
                    DeliverAt: message.VisibleAt > now ? message.VisibleAt : null,
                    MessageName: message.DedupeKey ?? message.MessageId.ToString("N")), ct);
            }

            await DeleteRelayed(message);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Failed to forward queue message {MessageId} on channel {Channel}; retrying in {Backoff}",
                message.MessageId, message.Channel, SendFailureBackoff);
            await TryReleaseLease(message);
            return false;
        }
    }

    private Task DeadLetterUnreadable(LeasedMessage message, CancellationToken ct)
    {
        logger.LogError("Queue message {MessageId} has an unreadable envelope; dead-lettering", message.MessageId);
        return deadLetterWriter.WriteAsync(new DeadLetterEntry
        {
            IdempotencyKey = message.MessageId.ToString("N"),
            Source = DeadLetterSource.Queue,
            CommandTypeName = QueueDeliveryPipeline.UnreadableEnvelopeTypeName,
            PayloadJson = message.Envelope,
            Error = "The queue message envelope could not be deserialized.",
            Attempts = 1,
        }, ct);
    }

    // Not cancellable: the transport already has the message.
    private async Task DeleteRelayed(LeasedMessage message)
    {
        await using var connection = await storage.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand(transaction: null, storage.Statements.DeleteRelayed);
        command.AddParameter("@Id", message.Id);
        command.AddParameter("@LeaseToken", message.LeaseToken);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    // Best effort: an expiring lease retries the row anyway.
    private async Task TryReleaseLease(LeasedMessage message)
    {
        try
        {
            await using var connection = await storage.DataSource.OpenConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand(transaction: null, storage.Statements.ReleaseLease);
            command.AddParameter("@Id", message.Id);
            command.AddParameter("@LeaseToken", message.LeaseToken);
            storage.Dialect.AddTimestamp(command, "@RetryAt", timeProvider.GetUtcNow() + SendFailureBackoff);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to back off queue message {MessageId}; its lease will expire instead", message.MessageId);
        }
    }

    private sealed record LeasedMessage(
        long Id, Guid MessageId, string Channel, DateTimeOffset VisibleAt, string? DedupeKey, string Envelope, Guid LeaseToken);
}

internal sealed class SqlOutboxRelayService(
    SqlOutboxRelay relay,
    QueueRelaySettings settings,
    ILogger<SqlOutboxRelayService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.RunInBackground)
            return;

        logger.LogInformation("Queue relay started (every {PollInterval})", settings.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await relay.RelayAsync(stoppingToken);
                if (result.MessagesFailed > 0)
                    logger.LogWarning("Queue relay forwarded {Forwarded} message(s); {Failed} failed and will be retried",
                        result.MessagesForwarded, result.MessagesFailed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never fatal: the next tick tries again.
                logger.LogError(ex, "Queue relay pass failed; next pass continues");
            }

            try
            {
                await Task.Delay(settings.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Queue relay stopped");
    }
}

// Lets a relay forward just-committed messages instead of waiting for its next pass.
internal interface ISqlOutboxObserver
{
    // A null transaction means the insert auto-committed.
    Task InsertedAsync(Guid messageId, DbTransaction? transaction, CancellationToken ct);

    Task CommittedAsync(DbTransaction transaction);
}

internal sealed class NoSqlOutboxObserver : ISqlOutboxObserver
{
    public Task InsertedAsync(Guid messageId, DbTransaction? transaction, CancellationToken ct) => Task.CompletedTask;

    public Task CommittedAsync(DbTransaction transaction) => Task.CompletedTask;
}

// Forwards on commit because a CPU-throttled host (Cloud Run) may not run a relay pass until the next
// request. Best effort; the passes remain the guarantee.
internal sealed class EagerForwardingObserver(SqlOutboxRelay relay, ILogger<EagerForwardingObserver> logger)
    : ISqlOutboxObserver
{
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(10);

    // Weak, so ids of a transaction never seen to commit (the application's own) are collected with it.
    private readonly ConditionalWeakTable<DbTransaction, List<Guid>> _pending = new();

    public Task InsertedAsync(Guid messageId, DbTransaction? transaction, CancellationToken ct)
    {
        if (transaction is null)
            return ForwardAsync([messageId]);

        var ids = _pending.GetOrCreateValue(transaction);
        lock (ids)
            ids.Add(messageId);
        return Task.CompletedTask;
    }

    public Task CommittedAsync(DbTransaction transaction)
    {
        if (!_pending.TryGetValue(transaction, out var ids))
            return Task.CompletedTask;

        _pending.Remove(transaction);
        lock (ids)
            return ForwardAsync([.. ids]);
    }

    private async Task ForwardAsync(IReadOnlyCollection<Guid> ids)
    {
        using var timeout = new CancellationTokenSource(ForwardTimeout);
        try
        {
            await relay.ForwardAsync(ids, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Eager forward of {Count} queue message(s) failed; the relay will pick them up", ids.Count);
        }
    }
}
