using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spinneret.Mediator;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// Enqueues onto an explicitly supplied transaction, for call sites that hold their unit of work
/// as a value rather than publishing it through <see cref="IMssqlTransactionProvider"/>. The
/// message insert commits — or rolls back — atomically with the caller's other writes.
/// <see cref="IQueue"/> is the same operation against the ambient transaction (or a standalone
/// auto-committed insert when none is active).
/// </summary>
public interface IMssqlTransactionalQueue
{
    Task Enqueue<TResponse>(IRequest<TResponse> request, DbTransaction transaction, QueueOptions? options = null, CancellationToken ct = default);
}

internal sealed class MssqlQueue(
    IOptions<MssqlQueueOptions> options,
    MssqlQueueSql sql,
    IQueuePayloadSerializer serializer,
    QueueTypeRegistry registry,
    IMssqlTransactionProvider transactions,
    TimeProvider timeProvider,
    ILogger<MssqlQueue> logger)
    : IQueue, IEnvelopeQueue, IMssqlTransactionalQueue
{
    public Task Enqueue<TResponse>(IRequest<TResponse> request, QueueOptions? queueOptions = null, CancellationToken ct = default)
        => EnqueueCore(BuildEnvelope(request, queueOptions), queueOptions?.Delay, queueOptions?.DedupeKey, transactions.Current, ct);

    public Task Enqueue<TResponse>(IRequest<TResponse> request, DbTransaction transaction, QueueOptions? queueOptions = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return EnqueueCore(BuildEnvelope(request, queueOptions), queueOptions?.Delay, queueOptions?.DedupeKey, transaction, ct);
    }

    // IEnvelopeQueue: booked retries and deferrals from the delivery processor. These join the
    // ambient transaction — the worker's per-message transaction — so the retry generation is
    // inserted atomically with the delete of the generation that failed.
    public Task Enqueue(QueueEnvelope envelope, TimeSpan? delay = null, CancellationToken ct = default)
        => EnqueueCore(envelope, delay, dedupeKey: null, transactions.Current, ct);

    private QueueEnvelope BuildEnvelope<TResponse>(IRequest<TResponse> request, QueueOptions? queueOptions)
    {
        var requestType = request.GetType();
        return new QueueEnvelope
        {
            RequestTypeName = registry.GetName(requestType),
            PayloadJson = serializer.Serialize(request, requestType),
            EnqueuedAtUtc = timeProvider.GetUtcNow(),
            Description = queueOptions?.Description,
        };
    }

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
            var connection = transaction.Connection
                ?? throw new InvalidOperationException("The supplied transaction has no open connection.");
            await Insert(connection, transaction, envelope, channel, visibleAt, dedupeKey, envelopeJson, ct);
            return;
        }

        await using var ownConnection = new SqlConnection(options.Value.ConnectionString);
        await ownConnection.OpenAsync(ct);
        await Insert(ownConnection, transaction: null, envelope, channel, visibleAt, dedupeKey, envelopeJson, ct);
    }

    private async Task Insert(
        DbConnection connection, DbTransaction? transaction, QueueEnvelope envelope,
        string channel, DateTimeOffset visibleAt, string? dedupeKey, string envelopeJson, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.Enqueue;
        command.AddParameter("@Channel", channel);
        command.AddParameter("@VisibleAt", visibleAt.UtcDateTime);
        command.AddParameter("@DedupeKey", dedupeKey);
        command.AddParameter("@Envelope", envelopeJson);

        var inserted = (bool)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Queue enqueue returned no result."));

        if (!inserted)
            logger.LogDebug(
                "Skipped enqueue of {RequestType}: dedupe key {DedupeKey} is already pending",
                envelope.RequestTypeName, dedupeKey);
    }
}

internal static class DbCommandExtensions
{
    public static void AddParameter(this DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Adds a UTC instant as <c>datetime2</c>. The type must be stated: SqlClient infers the legacy
    /// <c>datetime</c> from a <see cref="DateTime"/>, whose ~3.33 ms resolution cannot represent
    /// every value a <c>DATETIME2(3)</c> column holds — which would round the value a keyset cursor
    /// compares for equality, and skip or repeat rows across a page boundary.
    /// </summary>
    public static void AddDateTime2Parameter(this DbCommand command, string name, DateTimeOffset value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.DateTime2;
        parameter.Value = value.UtcDateTime;
        command.Parameters.Add(parameter);
    }

    public static string InvariantTaskId(this long id) => id.ToString(CultureInfo.InvariantCulture);
}
