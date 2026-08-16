namespace Spinneret.Queue.Mssql;

/// <summary>
/// The single source of truth for the queue's SQL — every statement the transport executes is
/// built here from the configured names, so the schema and the statements can never drift apart.
/// </summary>
internal sealed class MssqlQueueSql(MssqlQueueOptions options)
{
    /// <summary>
    /// The dead-letter projection, in the order <see cref="MssqlDeadLetterStore"/> reads its
    /// ordinals. Shared by the list and lookup statements so the two can never disagree.
    /// </summary>
    private const string DeadLetterColumns =
        "IdempotencyKey, Source, CommandTypeName, Description, PayloadJson, Error, Attempts, DeadLetteredAt";

    /// <summary>
    /// The paging order, repeated by every dead-letter read. Descending on both columns, and the
    /// idempotency key breaks ties so the order is total — without it a cursor sitting inside a
    /// group of entries sharing a millisecond could skip or repeat rows.
    /// </summary>
    private const string DeadLetterOrder = "ORDER BY DeadLetteredAt DESC, IdempotencyKey DESC";

    public string QueueTable { get; } = Identifier.Qualify(options.SchemaName, options.QueueTableName);
    public string DeadLetterTable { get; } = Identifier.Qualify(options.SchemaName, options.DeadLetterTableName);

    /// <summary>
    /// Inserts one message and selects 1, or selects 0 when the dedupe key is already pending. The
    /// duplicate is detected by catching the unique-index violation rather than by a pre-check, so
    /// two racing producers cannot both slip past; with SQL Server's default XACT_ABORT OFF the
    /// violation is a statement-level error, leaving the caller's transaction intact.
    /// </summary>
    public string Enqueue { get; } = $"""
        BEGIN TRY
            INSERT INTO {Identifier.Qualify(options.SchemaName, options.QueueTableName)} (Channel, VisibleAt, DedupeKey, Envelope)
            VALUES (@Channel, @VisibleAt, @DedupeKey, @Envelope);
            SELECT CAST(1 AS BIT);
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() IN (2601, 2627) AND @DedupeKey IS NOT NULL
                SELECT CAST(0 AS BIT);
            ELSE
                THROW;
        END CATCH
        """;

    /// <summary>
    /// Destructively claims the oldest-due message on a channel. The DELETE inside the caller's
    /// transaction is the lock: the row is invisible to peers exactly while the delivery runs, a
    /// rollback puts it back, and READPAST lets concurrent workers skip claimed rows instead of
    /// blocking on them.
    /// </summary>
    public string Dequeue { get; } = $"""
        WITH Due AS (
            SELECT TOP(1) Id, Envelope
            FROM {Identifier.Qualify(options.SchemaName, options.QueueTableName)} WITH (ROWLOCK, READPAST)
            WHERE Channel = @Channel AND VisibleAt <= @Now
            ORDER BY VisibleAt
        )
        DELETE FROM Due
        OUTPUT deleted.Id, deleted.Envelope;
        """;

    /// <summary>
    /// Pushes a message's visibility into the future — the RetryAfter fallback after the delivery
    /// transaction rolled back, so the redelivery honors the requested delay instead of hammering.
    /// </summary>
    public string Reschedule { get; } = $"""
        UPDATE {Identifier.Qualify(options.SchemaName, options.QueueTableName)}
        SET VisibleAt = @VisibleAt
        WHERE Id = @Id;
        """;

    /// <summary>
    /// Records a dead letter, idempotently: a duplicate idempotency key means the entry was already
    /// recorded by an earlier delivery of the same task, which is success, not an error.
    /// </summary>
    public string WriteDeadLetter { get; } = $"""
        BEGIN TRY
            INSERT INTO {Identifier.Qualify(options.SchemaName, options.DeadLetterTableName)}
                (IdempotencyKey, Source, CommandTypeName, Description, PayloadJson, Error, Attempts, DeadLetteredAt)
            VALUES (@IdempotencyKey, @Source, @CommandTypeName, @Description, @PayloadJson, @Error, @Attempts, @DeadLetteredAt);
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() NOT IN (2601, 2627)
                THROW;
        END CATCH
        """;

    /// <summary>The newest page of dead letters. One row past the page size is asked for.</summary>
    public string ListDeadLetters { get; } = $"""
        SELECT TOP(@Take) {DeadLetterColumns}
        FROM {Identifier.Qualify(options.SchemaName, options.DeadLetterTableName)}
        {DeadLetterOrder};
        """;

    /// <summary>
    /// The page following a cursor. The predicate is the keyset form of "everything ordered after
    /// this row": strictly older, or the same instant with a lower key.
    /// </summary>
    public string ListDeadLettersAfter { get; } = $"""
        SELECT TOP(@Take) {DeadLetterColumns}
        FROM {Identifier.Qualify(options.SchemaName, options.DeadLetterTableName)}
        WHERE DeadLetteredAt < @CursorDeadLetteredAt
           OR (DeadLetteredAt = @CursorDeadLetteredAt AND IdempotencyKey < @CursorIdempotencyKey)
        {DeadLetterOrder};
        """;

    public string GetDeadLetter { get; } = $"""
        SELECT {DeadLetterColumns}
        FROM {Identifier.Qualify(options.SchemaName, options.DeadLetterTableName)}
        WHERE IdempotencyKey = @IdempotencyKey;
        """;

    public string DeleteDeadLetter { get; } = $"""
        DELETE FROM {Identifier.Qualify(options.SchemaName, options.DeadLetterTableName)}
        WHERE IdempotencyKey = @IdempotencyKey;
        """;
}

internal static class Identifier
{
    /// <summary>Brackets and escapes a configured identifier: defense in depth behind startup validation.</summary>
    public static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    public static string Qualify(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";

    public static bool IsValid(string identifier) =>
        !string.IsNullOrWhiteSpace(identifier)
        && identifier.Length <= 116
        && (char.IsAsciiLetter(identifier[0]) || identifier[0] == '_')
        && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
