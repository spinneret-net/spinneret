using System.Data.Common;

namespace Spinneret.Queue.Sql;

// Joins the delivery's transaction, so a dead-letter commits atomically with the acknowledge.
internal sealed class SqlDeadLetterWriter(
    SqlQueueStorage storage,
    SqlConnectionSource connections,
    TimeProvider timeProvider)
    : IDeadLetterWriter
{
    public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default) =>
        connections.ExecuteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand(transaction, storage.Statements.WriteDeadLetter);
            command.AddParameter("@IdempotencyKey", entry.IdempotencyKey);
            command.AddParameter("@Source", DeadLetterStorage.FormatSource(entry.Source));
            command.AddParameter("@CommandTypeName", entry.CommandTypeName);
            command.AddParameter("@Description", entry.Description);
            command.AddParameter("@PayloadJson", entry.PayloadJson);
            command.AddParameter("@Error", entry.Error);
            command.AddParameter("@Attempts", entry.Attempts);
            command.AddParameter("@TraceId", entry.TraceId);
            storage.Dialect.AddTimestamp(command, "@DeadLetteredAt", timeProvider.GetUtcNow());
            await command.ExecuteNonQueryAsync(ct);
        }, ct);
}

// A delete inside a caller's transaction (a resend) commits with the enqueue that replaced it.
internal sealed class SqlDeadLetterStore(SqlQueueStorage storage, SqlConnectionSource connections)
    : IDeadLetterStore
{
    public async Task<DeadLetterPage> ListAsync(DeadLetterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var position = query.Cursor is { } cursor ? DeadLetterCursor.Decode(cursor) : (DeadLetterCursor?)null;
        var statements = storage.Statements;

        var rows = await connections.ExecuteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand(
                transaction, position is null ? statements.ListDeadLetters : statements.ListDeadLettersAfter);

            // One extra row tells whether another page exists, without a count query.
            command.AddParameter("@Take", query.PageSize + 1);

            if (position is { } after)
            {
                storage.Dialect.AddTimestamp(command, "@CursorDeadLetteredAt", after.DeadLetteredAt);
                command.AddParameter("@CursorIdempotencyKey", after.IdempotencyKey);
            }

            await using var reader = await command.ExecuteReaderAsync(ct);

            var items = new List<DeadLetter>(query.PageSize + 1);
            while (await reader.ReadAsync(ct))
                items.Add(ReadDeadLetter(reader));

            return items;
        }, ct);

        var hasMore = rows.Count > query.PageSize;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        return new DeadLetterPage
        {
            Items = rows,
            NextCursor = hasMore
                ? new DeadLetterCursor(rows[^1].DeadLetteredAt, rows[^1].IdempotencyKey).Encode()
                : null,
        };
    }

    public Task<DeadLetter?> GetAsync(string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        return connections.ExecuteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand(transaction, storage.Statements.GetDeadLetter);
            command.AddParameter("@IdempotencyKey", idempotencyKey);

            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadDeadLetter(reader) : null;
        }, ct);
    }

    public Task<bool> DeleteAsync(string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        return connections.ExecuteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand(transaction, storage.Statements.DeleteDeadLetter);
            command.AddParameter("@IdempotencyKey", idempotencyKey);

            // The row count is the only way to tell a discard from a key that was already gone.
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, ct);
    }

    // Ordinals follow SqlQueueStatements.DeadLetterColumns.
    private DeadLetter ReadDeadLetter(DbDataReader reader) =>
        new()
        {
            IdempotencyKey = reader.GetString(0),
            Source = DeadLetterStorage.ParseSource(reader.GetString(1)),
            CommandTypeName = reader.GetString(2),
            Description = reader.IsDBNull(3) ? null : reader.GetString(3),
            PayloadJson = reader.GetString(4),
            Error = reader.GetString(5),
            Attempts = reader.GetInt32(6),
            DeadLetteredAt = storage.Dialect.ReadTimestamp(reader, 7),
            TraceId = reader.IsDBNull(8) ? null : reader.GetString(8),
        };
}
