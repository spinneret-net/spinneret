using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spinneret.Mediator;

namespace Spinneret.Queue.Sql;

/// <summary>Enqueues within an explicitly supplied transaction, committing atomically with the caller's writes.</summary>
public interface ISqlTransactionalQueue
{
    Task Enqueue<TResponse>(IRequest<TResponse> request, DbTransaction transaction, QueueOptions? options = null, CancellationToken ct = default);
}

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

    // Retries join the delivery's transaction, committing atomically with the failed attempt's acknowledge.
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
        // Default serialization; only PayloadJson uses the host serializer.
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

        // No row means the dialect absorbed the dedupe conflict in the statement.
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
