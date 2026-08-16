using System.Data.Common;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// Reads and removes the rows <see cref="MssqlDeadLetterWriter"/> stored. Every statement goes
/// through <see cref="MssqlConnectionSource"/>, so a delete inside a caller's transaction — a resend
/// wrapped by <see cref="MssqlQueueTransactionScope"/> — commits with the enqueue that replaced it.
/// </summary>
internal sealed class MssqlDeadLetterStore(MssqlQueueSql sql, MssqlConnectionSource connections)
    : IDeadLetterStore
{
    public async Task<DeadLetterPage> ListAsync(DeadLetterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var position = query.Cursor is { } cursor ? DeadLetterCursor.Decode(cursor) : (DeadLetterCursor?)null;

        var rows = await connections.ExecuteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = position is null ? sql.ListDeadLetters : sql.ListDeadLettersAfter;

            // One more than asked for: whether that extra row came back is what distinguishes a full
            // last page from a full page with more behind it, without a second count query.
            command.AddParameter("@Take", query.PageSize + 1);

            if (position is { } after)
            {
                command.AddDateTime2Parameter("@CursorDeadLetteredAt", after.DeadLetteredAt);
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
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql.GetDeadLetter;
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
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql.DeleteDeadLetter;
            command.AddParameter("@IdempotencyKey", idempotencyKey);

            // The row count is the only way to tell a discard from a key that was already gone.
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, ct);
    }

    /// <summary>Ordinals follow the projection order in <see cref="MssqlQueueSql"/>.</summary>
    private static DeadLetter ReadDeadLetter(DbDataReader reader) =>
        new()
        {
            IdempotencyKey = reader.GetString(0),
            Source = DeadLetterStorage.ParseSource(reader.GetString(1)),
            CommandTypeName = reader.GetString(2),
            Description = reader.IsDBNull(3) ? null : reader.GetString(3),
            PayloadJson = reader.GetString(4),
            Error = reader.GetString(5),
            Attempts = reader.GetInt32(6),
            // DATETIME2 comes back with an unspecified kind; the column holds UTC by construction.
            DeadLetteredAt = new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
        };
}
