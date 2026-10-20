using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.Sql;

/// <summary>
/// Forwards the outbox to a push transport (Cloud Tasks): leases due rows, sends each under a stable
/// name, and deletes it once the transport has it. The outbox is the source of truth throughout — a
/// message leaves it only after the transport accepted it.
/// </summary>
/// <remarks>
/// <para>
/// Every message is sent under a name the transport deduplicates on: its dedupe key when the
/// producer gave one, otherwise its <c>MessageId</c>. That is what makes forwarding idempotent. A
/// relay that crashes between sending and deleting leaves the row leased; once the lease runs out a
/// later pass sends it again, the transport answers "already exists", and the row is deleted. A
/// duplicate is possible only if the row outlives the transport's memory of the name — about an hour
/// after the task ran, for Cloud Tasks.
/// </para>
/// <para>
/// A lease rather than a destructive claim: the send is a network call, which must not hold a
/// transaction open, and a lease that runs out is what returns a stranded row to the pool.
/// </para>
/// </remarks>
internal sealed class SqlOutboxRelay(
    SqlQueueStorage storage,
    IQueueTransport transport,
    QueueRelaySettings settings,
    IDeadLetterWriter deadLetterWriter,
    TimeProvider timeProvider,
    ILogger<SqlOutboxRelay> logger)
    : IQueueRelay
{
    /// <summary>How long a message that failed to send stays leased before a pass tries it again.</summary>
    private static readonly TimeSpan SendFailureBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Sends in flight at once within one leased batch.</summary>
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

            // Drained, or the transport refuses everything: leave the rest to the next pass, so the
            // trigger's interval is the backoff rather than a tight loop of failing sends.
            if (batch.Count < settings.BatchSize || sent == 0)
                break;
        }

        return forwarded == 0 && failed == 0
            ? RelayResult.Nothing
            : new RelayResult { MessagesForwarded = forwarded, MessagesFailed = failed };
    }

    /// <summary>
    /// Forwards specific messages now — the ones a unit of work just committed — so they do not wait
    /// for the next pass. Best effort: a message another relay already holds, or one due beyond the
    /// forwarding horizon, is skipped, and one that fails is left for the passes to retry.
    /// </summary>
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

    /// <summary>Sends one leased message and removes it; true when the transport has it.</summary>
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

            // Sent, or already sent by an earlier pass whose delete did not land: either way the
            // transport has it, so the outbox no longer needs to.
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

    /// <summary>
    /// A row whose envelope cannot be read can never be delivered: dead-letter the raw content, as the
    /// in-host worker would, rather than retrying it forever.
    /// </summary>
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

    /// <summary>
    /// Not cancellable: the transport already has the message, so abandoning the delete only costs a
    /// resend the transport will refuse as a duplicate — but finishing it costs nothing.
    /// </summary>
    private async Task DeleteRelayed(LeasedMessage message)
    {
        await using var connection = await storage.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand(transaction: null, storage.Statements.DeleteRelayed);
        command.AddParameter("@Id", message.Id);
        command.AddParameter("@LeaseToken", message.LeaseToken);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Best effort: if the backoff cannot be recorded, the lease running out retries the row anyway.</summary>
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

/// <summary>
/// Runs the relay on a timer in this host, unless the transport's settings leave it to an HTTP
/// trigger (a host that scales to zero has no thread to tick).
/// </summary>
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
                // The pass itself failed — the database still starting up, a transient outage. Never
                // fatal: the next tick tries again.
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

/// <summary>
/// Hears about every outbox insert and every committed unit of work, so a relay can forward what was
/// just committed instead of waiting for its next pass. The default hears nothing: with in-host
/// delivery the worker picks rows up itself.
/// </summary>
internal interface ISqlOutboxObserver
{
    /// <summary>
    /// A message was inserted into the outbox — joining <paramref name="transaction"/>, or committed
    /// on its own when that is null.
    /// </summary>
    Task InsertedAsync(Guid messageId, DbTransaction? transaction, CancellationToken ct);

    /// <summary>A unit of work's own transaction committed.</summary>
    Task CommittedAsync(DbTransaction transaction);
}

internal sealed class NoSqlOutboxObserver : ISqlOutboxObserver
{
    public Task InsertedAsync(Guid messageId, DbTransaction? transaction, CancellationToken ct) => Task.CompletedTask;

    public Task CommittedAsync(DbTransaction transaction) => Task.CompletedTask;
}

/// <summary>
/// Forwards messages to the transport as soon as they are durable: an auto-committed insert at once,
/// one inside a unit of work when that unit commits. A host that is CPU-throttled outside requests
/// (Cloud Run) would otherwise leave them for a relay pass that may not run until the next request,
/// or the next external tick. Rows inserted into a transaction the application owns cannot be seen
/// to commit, so those wait for the passes.
/// </summary>
/// <remarks>
/// Best effort and bounded: forwarding never fails the operation that triggered it, and the passes
/// remain the guarantee.
/// </remarks>
internal sealed class EagerForwardingObserver(SqlOutboxRelay relay, ILogger<EagerForwardingObserver> logger)
    : ISqlOutboxObserver
{
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(10);

    // Weak, so pending ids of a transaction that is never seen to commit — the application's own —
    // go away with it.
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
