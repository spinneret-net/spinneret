using System.Data;
using System.Data.Common;
using Npgsql;
using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Postgres;

/// <summary>
/// The PostgreSQL spelling of the queue's storage — the single source of truth for every statement
/// and the DDL, built from the configured names so the schema and the statements can never drift.
/// </summary>
/// <remarks>
/// <para>
/// Postgres aborts a transaction on any failed statement: everything after it fails until a
/// rollback. So no statement here may fail on an outcome the storage expects — a duplicate dedupe
/// key or dead-letter key is an <c>ON CONFLICT DO NOTHING</c>, never a caught violation — or it
/// would take the caller's transaction down with it.
/// </para>
/// <para>
/// Schema and table names are quoted, so they keep the case they were configured with. Column
/// names are not, so they fold to lower case in the catalog and read the same quoted or not.
/// </para>
/// </remarks>
internal sealed class PostgresQueueDialect : SqlQueueDialect
{
    public static readonly PostgresQueueDialect Instance = new();

    private const string DeadLetterColumns = SqlQueueStatements.DeadLetterColumns;

    /// <summary>
    /// The paging order, repeated by every dead-letter read. Descending on both columns, and the
    /// idempotency key breaks ties so the order is total — without it a cursor sitting inside a
    /// group of entries sharing an instant could skip or repeat rows.
    /// </summary>
    private const string DeadLetterOrder = "ORDER BY DeadLetteredAt DESC, IdempotencyKey DESC";

    private PostgresQueueDialect()
    {
    }

    public override string Name => "postgres";

    /// <summary>NAMEDATALEN - 1 bytes; identifiers are validated to be ASCII, so characters.</summary>
    public override int MaxIdentifierLength => 63;

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override IEnumerable<string> DerivedIdentifiers(SqlQueueNames names) => PostgresDerivedNames.Of(names).All;

    public override SqlQueueStatements CreateStatements(SqlQueueNames names)
    {
        var queueTable = Qualify(names.Schema, names.QueueTable);
        var deadLetterTable = Qualify(names.Schema, names.DeadLetterTable);

        return new SqlQueueStatements
        {
            // The conflict target repeats the partial index's predicate, which is how Postgres picks
            // that index as the arbiter. A duplicate returns no row at all, which the outbox reads as
            // "already pending"; it never raises, so the caller's transaction carries on.
            Enqueue = $"""
                INSERT INTO {queueTable} (MessageId, Channel, VisibleAt, DedupeKey, Envelope)
                VALUES (@MessageId, @Channel, @VisibleAt, @DedupeKey, @Envelope)
                ON CONFLICT (DedupeKey) WHERE DedupeKey IS NOT NULL DO NOTHING
                RETURNING true;
                """,

            // The DELETE inside the caller's transaction is the lock: the row is invisible to peers
            // exactly while the delivery runs, a rollback puts it back, and SKIP LOCKED lets
            // concurrent workers pass over claimed rows instead of blocking on them. A row a relay
            // holds is being forwarded elsewhere, so it is not this worker's to deliver.
            Dequeue = $"""
                DELETE FROM {queueTable}
                WHERE Id = (
                    SELECT Id
                    FROM {queueTable}
                    WHERE Channel = @Channel AND VisibleAt <= @Now
                      AND (LeasedUntil IS NULL OR LeasedUntil < @Now)
                    ORDER BY VisibleAt
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING Id, MessageId, Envelope;
                """,

            // A lease rather than a destructive claim: the send is a network call, which must not
            // hold a transaction open, and a relay that dies mid-send must leave the row to be
            // re-leased once the lease runs out. SKIP LOCKED makes concurrent relays pass over each
            // other's rows instead of leasing the same ones.
            LeaseForRelay = $"""
                UPDATE {queueTable} AS q
                SET LeasedUntil = @LeaseUntil, LeaseToken = @LeaseToken
                FROM (
                    SELECT Id
                    FROM {queueTable}
                    WHERE VisibleAt <= @Horizon
                      AND (LeasedUntil IS NULL OR LeasedUntil < @Now)
                    ORDER BY VisibleAt
                    LIMIT @Batch
                    FOR UPDATE SKIP LOCKED) AS due
                WHERE q.Id = due.Id
                RETURNING q.Id, q.MessageId, q.Channel, q.VisibleAt, q.DedupeKey, q.Envelope;
                """,

            LeaseById = $"""
                UPDATE {queueTable}
                SET LeasedUntil = @LeaseUntil, LeaseToken = @LeaseToken
                WHERE Id = (
                    SELECT Id
                    FROM {queueTable}
                    WHERE MessageId = @MessageId
                      AND VisibleAt <= @Horizon
                      AND (LeasedUntil IS NULL OR LeasedUntil < @Now)
                    FOR UPDATE SKIP LOCKED)
                RETURNING Id, MessageId, Channel, VisibleAt, DedupeKey, Envelope;
                """,

            DeleteRelayed = $"""
                DELETE FROM {queueTable}
                WHERE Id = @Id AND LeaseToken = @LeaseToken;
                """,

            ReleaseLease = $"""
                UPDATE {queueTable}
                SET LeasedUntil = @RetryAt
                WHERE Id = @Id AND LeaseToken = @LeaseToken;
                """,

            Reschedule = $"""
                UPDATE {queueTable}
                SET VisibleAt = @VisibleAt
                WHERE Id = @Id;
                """,

            // A duplicate idempotency key means the entry was already recorded by an earlier
            // delivery of the same task, which is success, not an error.
            WriteDeadLetter = $"""
                INSERT INTO {deadLetterTable}
                    (IdempotencyKey, Source, CommandTypeName, Description, PayloadJson, Error, Attempts, DeadLetteredAt, TraceId)
                VALUES (@IdempotencyKey, @Source, @CommandTypeName, @Description, @PayloadJson, @Error, @Attempts, @DeadLetteredAt, @TraceId)
                ON CONFLICT (IdempotencyKey) DO NOTHING;
                """,

            ListDeadLetters = $"""
                SELECT {DeadLetterColumns}
                FROM {deadLetterTable}
                {DeadLetterOrder}
                LIMIT @Take;
                """,

            // The keyset form of "everything ordered after this row": strictly older, or the same
            // instant with a lower key.
            ListDeadLettersAfter = $"""
                SELECT {DeadLetterColumns}
                FROM {deadLetterTable}
                WHERE DeadLetteredAt < @CursorDeadLetteredAt
                   OR (DeadLetteredAt = @CursorDeadLetteredAt AND IdempotencyKey < @CursorIdempotencyKey)
                {DeadLetterOrder}
                LIMIT @Take;
                """,

            GetDeadLetter = $"""
                SELECT {DeadLetterColumns}
                FROM {deadLetterTable}
                WHERE IdempotencyKey = @IdempotencyKey;
                """,

            DeleteDeadLetter = $"""
                DELETE FROM {deadLetterTable}
                WHERE IdempotencyKey = @IdempotencyKey;
                """,
        };
    }

    public override string CreateSchemaScript(SqlQueueNames names)
    {
        var queueTable = Qualify(names.Schema, names.QueueTable);
        var deadLetterTable = Qualify(names.Schema, names.DeadLetterTable);
        var n = PostgresDerivedNames.Of(names);

        // One DO block is one statement, so the whole script runs in one implicit transaction —
        // atomic, and holding the advisory lock to the end — without the caller managing one.
        return $"""
            DO $spinneret$
            BEGIN
                -- Serializes the whole script against every other host running it. CREATE TABLE IF
                -- NOT EXISTS is not safe against itself: two replicas booting together can both find
                -- the table missing, and the loser fails on a duplicate catalog entry. Scoped to this
                -- queue's own tables, so unrelated schemas never wait on each other.
                PERFORM pg_advisory_xact_lock(hashtextextended('Spinneret:QueueSchema:{Literal(queueTable)}', 0));

                CREATE TABLE IF NOT EXISTS {queueTable} (
                    Id bigint GENERATED ALWAYS AS IDENTITY NOT NULL CONSTRAINT {QuoteIdentifier(n.QueuePrimaryKey)} PRIMARY KEY,
                    MessageId uuid NOT NULL DEFAULT gen_random_uuid(),
                    Channel varchar(100) NOT NULL,
                    VisibleAt timestamptz NOT NULL,
                    DedupeKey varchar(200) NULL,
                    Envelope text NOT NULL,
                    LeasedUntil timestamptz NULL,
                    LeaseToken uuid NULL
                );

                -- Columns added after a table may already exist, each guarded on its own existence,
                -- so a column added to this script reaches a database created before it. The
                -- MessageId default fills rows already waiting, so they keep their place.
                ALTER TABLE {queueTable} ADD COLUMN IF NOT EXISTS MessageId uuid NOT NULL DEFAULT gen_random_uuid();
                ALTER TABLE {queueTable} ADD COLUMN IF NOT EXISTS LeasedUntil timestamptz NULL;
                ALTER TABLE {queueTable} ADD COLUMN IF NOT EXISTS LeaseToken uuid NULL;

                -- Every index is guarded on its own existence rather than on the table's, so an index
                -- added to this script reaches an existing database, and one a failed start left out
                -- is repaired on the next.
                CREATE INDEX IF NOT EXISTS {QuoteIdentifier(n.ChannelIndex)}
                    ON {queueTable} (Channel, VisibleAt);

                -- Serves the relay, which leases across every channel by due time.
                CREATE INDEX IF NOT EXISTS {QuoteIdentifier(n.VisibleIndex)}
                    ON {queueTable} (VisibleAt) INCLUDE (LeasedUntil);

                -- Serves LeaseById, the eager forward of a message just committed.
                CREATE INDEX IF NOT EXISTS {QuoteIdentifier(n.MessageIdIndex)}
                    ON {queueTable} (MessageId);

                -- Not merely an optimisation: it is the arbiter of Enqueue's ON CONFLICT. Without it
                -- the insert fails instead ("no unique or exclusion constraint matching"), so a
                -- database missing it cannot dedupe at all.
                CREATE UNIQUE INDEX IF NOT EXISTS {QuoteIdentifier(n.DedupeIndex)}
                    ON {queueTable} (DedupeKey)
                    WHERE DedupeKey IS NOT NULL;

                CREATE TABLE IF NOT EXISTS {deadLetterTable} (
                    IdempotencyKey varchar(200) NOT NULL CONSTRAINT {QuoteIdentifier(n.DeadLetterPrimaryKey)} PRIMARY KEY,
                    Source varchar(20) NOT NULL,
                    CommandTypeName varchar(500) NOT NULL,
                    Description varchar(1000) NULL,
                    PayloadJson text NOT NULL,
                    Error text NOT NULL,
                    Attempts integer NOT NULL,
                    DeadLetteredAt timestamptz NOT NULL,
                    TraceId varchar(32) NULL
                );

                ALTER TABLE {deadLetterTable} ADD COLUMN IF NOT EXISTS TraceId varchar(32) NULL;

                -- Serves the admin page's (DeadLetteredAt DESC, IdempotencyKey DESC) keyset paging.
                CREATE INDEX IF NOT EXISTS {QuoteIdentifier(n.DeadLetterOrderIndex)}
                    ON {deadLetterTable} (DeadLetteredAt DESC, IdempotencyKey DESC);
            END
            $spinneret$;
            """;
    }

    /// <summary>
    /// Binds a UTC instant as <c>timestamptz</c>. Npgsql maps a <see cref="DateTimeOffset"/> with a
    /// zero offset there; any other offset is rejected, hence the normalization.
    /// </summary>
    public override void AddTimestamp(DbCommand command, string name, DateTimeOffset utc)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.DateTimeOffset;
        parameter.Value = utc.ToUniversalTime();
        command.Parameters.Add(parameter);
    }

    public override DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal) =>
        reader.GetFieldValue<DateTimeOffset>(ordinal);

    /// <summary>
    /// Asks the server: in an aborted transaction every statement fails with
    /// <c>in_failed_sql_transaction</c> until a rollback, so a trivial one tells. Npgsql does not
    /// publish the transaction status it tracks, and a commit would not tell either — Postgres answers
    /// COMMIT of an aborted transaction by rolling it back without an error.
    /// </summary>
    public override async Task<bool> IsAbortedAsync(DbTransaction transaction, CancellationToken ct)
    {
        try
        {
            await using var command = SqlConnectionSource.ConnectionOf(transaction).CreateCommand(transaction, "SELECT 1");
            await command.ExecuteScalarAsync(ct);
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InFailedSqlTransaction)
        {
            return true;
        }
    }

    /// <summary>Escapes a value for a single-quoted SQL string literal.</summary>
    private static string Literal(string value) => value.Replace("'", "''");
}

/// <summary>
/// The index and constraint names the DDL derives from the configured table names — in one place,
/// so the script and the length validation can never disagree on them. Postgres index names are
/// unique per schema, not per table, so each one carries its table's name.
/// </summary>
internal readonly record struct PostgresDerivedNames(
    string QueuePrimaryKey,
    string ChannelIndex,
    string VisibleIndex,
    string MessageIdIndex,
    string DedupeIndex,
    string DeadLetterPrimaryKey,
    string DeadLetterOrderIndex)
{
    public static PostgresDerivedNames Of(SqlQueueNames names) => new(
        QueuePrimaryKey: $"{names.QueueTable}_pkey",
        ChannelIndex: $"{names.QueueTable}_channel_visibleat_idx",
        VisibleIndex: $"{names.QueueTable}_visibleat_idx",
        MessageIdIndex: $"{names.QueueTable}_messageid_idx",
        DedupeIndex: $"{names.QueueTable}_dedupekey_key",
        DeadLetterPrimaryKey: $"{names.DeadLetterTable}_pkey",
        DeadLetterOrderIndex: $"{names.DeadLetterTable}_deadletteredat_idx");

    public IEnumerable<string> All =>
    [
        QueuePrimaryKey, ChannelIndex, VisibleIndex, MessageIdIndex, DedupeIndex,
        DeadLetterPrimaryKey, DeadLetterOrderIndex,
    ];
}
