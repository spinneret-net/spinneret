using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spinneret.Mediator;

namespace Spinneret.Queue.Sql;

/// <summary>
/// Enqueues onto an explicitly supplied transaction, for call sites that hold their unit of work
/// as a value rather than publishing it through <see cref="ISqlTransactionProvider"/>. The
/// message insert commits — or rolls back — atomically with the caller's other writes.
/// <see cref="IQueue"/> is the same operation against the ambient transaction (or a standalone
/// auto-committed insert when none is active).
/// </summary>
public interface ISqlTransactionalQueue
{
    Task Enqueue<TResponse>(IRequest<TResponse> request, DbTransaction transaction, QueueOptions? options = null, CancellationToken ct = default);
}

/// <summary>
/// The queue as a table in the application database — an outbox. Every enqueue is an insert that
/// joins the ambient (or supplied) transaction, so it is atomic with the business write that caused
/// it; whoever drains the table — the in-host worker, or the relay to a push transport — picks it up
/// once that transaction commits.
/// </summary>
internal sealed class SqlOutboxQueue(
    SqlQueueStorage storage,
    SqlConnectionSource connections,
    QueueEnvelopeFactory envelopes,
    QueueTypeRegistry registry,
    ISqlTransactionProvider transactions,
    ISqlOutboxObserver observer,
    TimeProvider timeProvider,
    ILogger<SqlOutboxQueue> logger)
    : IQueue, IEnvelopeQueue, ISqlTransactionalQueue
{
    public Task Enqueue<TResponse>(IRequest<TResponse> request, QueueOptions? options = null, CancellationToken ct = default)
        => EnqueueCore(envelopes.Create(request, options), options?.Delay, options?.DedupeKey, transactions.Current, ct);

    public Task Enqueue<TResponse>(IRequest<TResponse> request, DbTransaction transaction, QueueOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return EnqueueCore(envelopes.Create(request, options), options?.Delay, options?.DedupeKey, transaction, ct);
    }

    // IEnvelopeQueue: booked retries and deferrals from the delivery processor. These join the
    // ambient transaction — the delivery's unit of work — so the retry generation is inserted
    // atomically with the acknowledge of the generation that failed.
    public Task Enqueue(QueueEnvelope envelope, TimeSpan? delay = null, CancellationToken ct = default)
        => EnqueueCore(envelope, delay, dedupeKey: null, transactions.Current, ct);

    private async Task EnqueueCore(
        QueueEnvelope envelope, TimeSpan? delay, string? dedupeKey, DbTransaction? transaction, CancellationToken ct)
    {
        var channel = registry.Resolve(envelope.RequestTypeName).Policy.ResolvedChannel;

        using var activity = QueueTracing.StartProducer(channel, envelope, dedupeKey);
        envelope = QueueTracing.StampTraceContext(envelope);

        var now = timeProvider.GetUtcNow();
        var visibleAt = delay is { } d && d > TimeSpan.Zero ? now + d : now;
        // The envelope itself uses default serialization; only PayloadJson uses the host serializer.
        var envelopeJson = JsonSerializer.Serialize(envelope);

        if (transaction is not null)
        {
            await Insert(SqlConnectionSource.ConnectionOf(transaction), transaction, envelope, channel, visibleAt, dedupeKey, envelopeJson, ct);
            return;
        }

        await connections.ExecuteAsync(
            (connection, ambient) => Insert(connection, ambient, envelope, channel, visibleAt, dedupeKey, envelopeJson, ct), ct);
    }

    private async Task Insert(
        DbConnection connection, DbTransaction? transaction, QueueEnvelope envelope,
        string channel, DateTimeOffset visibleAt, string? dedupeKey, string envelopeJson, CancellationToken ct)
    {
        var messageId = Guid.NewGuid();

        await using var command = connection.CreateCommand(transaction, storage.Statements.Enqueue);
        command.AddParameter("@MessageId", messageId);
        command.AddParameter("@Channel", channel);
        storage.Dialect.AddTimestamp(command, "@VisibleAt", visibleAt);
        command.AddParameter("@DedupeKey", dedupeKey);
        command.AddParameter("@Envelope", envelopeJson);

        // No row at all is how a dialect that absorbs the conflict in the statement itself reports it.
        var result = await command.ExecuteScalarAsync(ct);
        var inserted = result is not null and not DBNull && Convert.ToBoolean(result);

        if (!inserted)
        {
            logger.LogDebug(
                "Skipped enqueue of {RequestType}: dedupe key {DedupeKey} is already pending",
                envelope.RequestTypeName, dedupeKey);
            return;
        }

        await observer.InsertedAsync(messageId, transaction, ct);
    }
}
