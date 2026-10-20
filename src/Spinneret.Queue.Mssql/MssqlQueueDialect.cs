using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// The SQL Server spelling of the queue's storage — the single source of truth for every statement
/// and the DDL, built from the configured names so the schema and the statements can never drift.
/// </summary>
internal sealed class MssqlQueueDialect : SqlQueueDialect
{
    public static readonly MssqlQueueDialect Instance = new();

    private const string DeadLetterColumns = SqlQueueStatements.DeadLetterColumns;

    /// <summary>
    /// The paging order, repeated by every dead-letter read. Descending on both columns, and the
    /// idempotency key breaks ties so the order is total — without it a cursor sitting inside a
    /// group of entries sharing a millisecond could skip or repeat rows.
    /// </summary>
    private const string DeadLetterOrder = "ORDER BY DeadLetteredAt DESC, IdempotencyKey DESC";

    private MssqlQueueDialect()
    {
    }

    public override string Name => "mssql";

    public override SqlQueueStatements CreateStatements(SqlQueueNames names)
    {
        var queueTable = Qualify(names.Schema, names.QueueTable);
        var deadLetterTable = Qualify(names.Schema, names.DeadLetterTable);

        return new SqlQueueStatements
        {
            // The duplicate is detected by catching the unique-index violation rather than by a
            // pre-check, so two racing producers cannot both slip past; with SQL Server's default
            // XACT_ABORT OFF the violation is a statement-level error, leaving the caller's
            // transaction intact.
            Enqueue = $"""
                BEGIN TRY
                    INSERT INTO {queueTable} (MessageId, Channel, VisibleAt, DedupeKey, Envelope)
                    VALUES (@MessageId, @Channel, @VisibleAt, @DedupeKey, @Envelope);
                    SELECT CAST(1 AS BIT);
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER() IN (2601, 2627) AND @DedupeKey IS NOT NULL
                        SELECT CAST(0 AS BIT);
                    ELSE
                        THROW;
                END CATCH
                """,

            // The DELETE inside the caller's transaction is the lock: the row is invisible to peers
            // exactly while the delivery runs, a rollback puts it back, and READPAST lets concurrent
            // workers skip claimed rows instead of blocking on them. A row a relay holds is being
            // forwarded elsewhere, so it is not this worker's to deliver.
            Dequeue = $"""
                WITH Due AS (
                    SELECT TOP(1) Id, MessageId, Envelope
                    FROM {queueTable} WITH (ROWLOCK, READPAST)
                    WHERE Channel = @Channel AND VisibleAt <= @Now
                      AND (LeasedUntil IS NULL OR LeasedUntil < @Now)
                    ORDER BY VisibleAt
                )
                DELETE FROM Due
                OUTPUT deleted.Id, deleted.MessageId, deleted.Envelope;
                """,

            // A lease rather than a destructive claim: the send is a network call, which must not
            // hold a transaction open, and a relay that dies mid-send must leave the row to be
            // re-leased once the lease runs out. UPDLOCK with READPAST makes concurrent relays skip
            // each other's rows instead of leasing the same ones.
            LeaseForRelay = $"""
                WITH Due AS (
                    SELECT TOP(@Batch) Id, MessageId, Channel, VisibleAt, DedupeKey, Envelope, LeasedUntil, LeaseToken
                    FROM {queueTable} WITH (ROWLOCK, READPAST, UPDLOCK)
                    WHERE VisibleAt <= @Horizon
                      AND (LeasedUntil IS NULL OR LeasedUntil < @Now)
                    ORDER BY VisibleAt
                )
                UPDATE Due
                SET LeasedUntil = @LeaseUntil, LeaseToken = @LeaseToken
                OUTPUT inserted.Id, inserted.MessageId, inserted.Channel, inserted.VisibleAt, inserted.DedupeKey, inserted.Envelope;
                """,

            LeaseById = $"""
                UPDATE {queueTable} WITH (ROWLOCK, READPAST)
                SET LeasedUntil = @LeaseUntil, LeaseToken = @LeaseToken
                OUTPUT inserted.Id, inserted.MessageId, inserted.Channel, inserted.VisibleAt, inserted.DedupeKey, inserted.Envelope
                WHERE MessageId = @MessageId
                  AND VisibleAt <= @Horizon
                  AND (LeasedUntil IS NULL OR LeasedUntil < @Now);
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
                BEGIN TRY
                    INSERT INTO {deadLetterTable}
                        (IdempotencyKey, Source, CommandTypeName, Description, PayloadJson, Error, Attempts, DeadLetteredAt, TraceId)
                    VALUES (@IdempotencyKey, @Source, @CommandTypeName, @Description, @PayloadJson, @Error, @Attempts, @DeadLetteredAt, @TraceId);
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER() NOT IN (2601, 2627)
                        THROW;
                END CATCH
                """,

            ListDeadLetters = $"""
                SELECT TOP(@Take) {DeadLetterColumns}
                FROM {deadLetterTable}
                {DeadLetterOrder};
                """,

            // The keyset form of "everything ordered after this row": strictly older, or the same
            // instant with a lower key.
            ListDeadLettersAfter = $"""
                SELECT TOP(@Take) {DeadLetterColumns}
                FROM {deadLetterTable}
                WHERE DeadLetteredAt < @CursorDeadLetteredAt
                   OR (DeadLetteredAt = @CursorDeadLetteredAt AND IdempotencyKey < @CursorIdempotencyKey)
                {DeadLetterOrder};
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

    /// <summary>
    /// sysname is 128 characters. Configured names used to be capped at 116 — a temp-table limit
    /// that never applied here — while the derived index names could still overflow; checking the
    /// derived names against the real limit covers both.
    /// </summary>
    public override int MaxIdentifierLength => 128;

    public override string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    public override IEnumerable<string> DerivedIdentifiers(SqlQueueNames names) => DerivedNames.Of(names).All;

    public override string CreateSchemaScript(SqlQueueNames names)
    {
        var queueTable = Qualify(names.Schema, names.QueueTable);
        var deadLetterTable = Qualify(names.Schema, names.DeadLetterTable);
        var (queuePrimaryKey, channelIndex, visibleIndex, messageIdDefault, messageIdIndex, dedupeIndex,
            deadLetterPrimaryKey, deadLetterOrderIndex) = DerivedNames.Of(names);

        return $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            -- Serializes the whole script against every other host running it. Without it, two
            -- replicas booting together can both read OBJECT_ID as NULL: one creates the table and
            -- the other fails, and worse, a third can see the table between its CREATE and the
            -- CREATE INDEX statements that follow and skip the indexes as already done. Each
            -- statement autocommits, so there is no implicit transaction making that atomic.
            -- Scoped to this queue's own tables, so unrelated schemas never wait on each other.
            DECLARE @lockResult INT;
            EXEC @lockResult = sp_getapplock
                @Resource = N'Spinneret:QueueSchema:{queueTable}',
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 30000;

            IF @lockResult < 0
            BEGIN
                ROLLBACK TRANSACTION;
                THROW 50000, 'Timed out acquiring the Spinneret queue schema lock.', 1;
            END;

            IF OBJECT_ID(N'{queueTable}', N'U') IS NULL
            BEGIN
                CREATE TABLE {queueTable} (
                    Id BIGINT IDENTITY NOT NULL CONSTRAINT {QuoteIdentifier(queuePrimaryKey)} PRIMARY KEY,
                    MessageId UNIQUEIDENTIFIER NOT NULL CONSTRAINT {QuoteIdentifier(messageIdDefault)} DEFAULT NEWID(),
                    Channel NVARCHAR(100) NOT NULL,
                    VisibleAt DATETIME2(3) NOT NULL,
                    DedupeKey NVARCHAR(200) NULL,
                    Envelope NVARCHAR(MAX) NOT NULL,
                    LeasedUntil DATETIME2(3) NULL,
                    LeaseToken UNIQUEIDENTIFIER NULL
                );
            END;

            -- Columns added after a table may already exist, each guarded on its own existence. The
            -- MessageId default fills rows already waiting, so they keep their place in the queue.
            -- MessageId is the stable name a message is delivered and dead-lettered under; the lease
            -- columns are how a relay to a push transport holds a row while it sends it.
            IF COL_LENGTH(N'{queueTable}', N'MessageId') IS NULL
            BEGIN
                EXEC(N'ALTER TABLE {queueTable} ADD MessageId UNIQUEIDENTIFIER NOT NULL CONSTRAINT {QuoteIdentifier(messageIdDefault)} DEFAULT NEWID() WITH VALUES;');
            END;

            IF COL_LENGTH(N'{queueTable}', N'LeasedUntil') IS NULL
            BEGIN
                EXEC(N'ALTER TABLE {queueTable} ADD LeasedUntil DATETIME2(3) NULL, LeaseToken UNIQUEIDENTIFIER NULL;');
            END;

            -- Every index is guarded on its own existence rather than on the table's, for two
            -- reasons. An index added to this script after a database was created would otherwise
            -- never reach it. And a database left with the table but not the indexes — by the race
            -- above, before the lock existed — would stay that way forever, because the table's
            -- existence is what the guard tests. Guarded individually, the next startup repairs it.
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{queueTable}', N'U')
                  AND name = N'{channelIndex}')
            BEGIN
                CREATE INDEX {QuoteIdentifier(channelIndex)}
                    ON {queueTable} (Channel, VisibleAt);
            END;

            -- Serves the relay, which leases across every channel by due time. Through EXEC because
            -- it names a column this same batch may only just have added, which a statement compiled
            -- with the batch would not yet see.
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{queueTable}', N'U')
                  AND name = N'{visibleIndex}')
            BEGIN
                EXEC(N'CREATE INDEX {QuoteIdentifier(visibleIndex)} ON {queueTable} (VisibleAt) INCLUDE (LeasedUntil);');
            END;

            -- Serves LeaseById, the eager forward of a message just committed, which finds its row by
            -- MessageId; without it every eager forward scans the outbox.
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{queueTable}', N'U')
                  AND name = N'{messageIdIndex}')
            BEGIN
                EXEC(N'CREATE INDEX {QuoteIdentifier(messageIdIndex)} ON {queueTable} (MessageId);');
            END;

            -- Not merely an optimisation: Enqueue detects a duplicate dedupe key by catching this
            -- index's unique violation (2601/2627). Without it the insert simply succeeds and the
            -- enqueue reports that it stored a new message, so deduplication silently stops working.
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{queueTable}', N'U')
                  AND name = N'{dedupeIndex}')
            BEGIN
                CREATE UNIQUE INDEX {QuoteIdentifier(dedupeIndex)}
                    ON {queueTable} (DedupeKey)
                    WHERE DedupeKey IS NOT NULL;
            END;

            IF OBJECT_ID(N'{deadLetterTable}', N'U') IS NULL
            BEGIN
                CREATE TABLE {deadLetterTable} (
                    IdempotencyKey NVARCHAR(200) NOT NULL CONSTRAINT {QuoteIdentifier(deadLetterPrimaryKey)} PRIMARY KEY,
                    Source NVARCHAR(20) NOT NULL,
                    CommandTypeName NVARCHAR(500) NOT NULL,
                    Description NVARCHAR(1000) NULL,
                    PayloadJson NVARCHAR(MAX) NOT NULL,
                    Error NVARCHAR(MAX) NOT NULL,
                    Attempts INT NOT NULL,
                    DeadLetteredAt DATETIME2(3) NOT NULL,
                    TraceId NVARCHAR(32) NULL
                );
            END;

            -- Guarded on its own existence, for the same reason every index below is: the CREATE
            -- above only runs when the table is absent, so a column added to it would never reach a
            -- database that already exists.
            IF COL_LENGTH(N'{deadLetterTable}', N'TraceId') IS NULL
            BEGIN
                EXEC(N'ALTER TABLE {deadLetterTable} ADD TraceId NVARCHAR(32) NULL;');
            END;

            -- Serves the admin page's (DeadLetteredAt DESC, IdempotencyKey DESC) keyset paging.
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{deadLetterTable}', N'U')
                  AND name = N'{deadLetterOrderIndex}')
            BEGIN
                CREATE INDEX {QuoteIdentifier(deadLetterOrderIndex)}
                    ON {deadLetterTable} (DeadLetteredAt DESC, IdempotencyKey DESC);
            END;

            COMMIT TRANSACTION;
            """;
    }

    /// <summary>
    /// Adds a UTC instant as <c>datetime2</c>. The type must be stated: SqlClient infers the legacy
    /// <c>datetime</c> from a <see cref="DateTime"/>, whose ~3.33 ms resolution cannot represent
    /// every value a <c>DATETIME2(3)</c> column holds — which would round the value a keyset cursor
    /// compares for equality, and skip or repeat rows across a page boundary.
    /// </summary>
    public override void AddTimestamp(DbCommand command, string name, DateTimeOffset utc)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.DateTime2;
        parameter.Value = utc.UtcDateTime;
        command.Parameters.Add(parameter);
    }

    /// <summary>DATETIME2 comes back with an unspecified kind; the column holds UTC by construction.</summary>
    public override DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal) =>
        new(reader.GetDateTime(ordinal), TimeSpan.Zero);

    // SqlClient's savepoints are the synchronous SqlTransaction.Save/Rollback(name); the async
    // DbTransaction members are not guaranteed to reach them.
    public override Task SaveAsync(DbTransaction transaction, string savepoint, CancellationToken ct)
    {
        ((SqlTransaction)transaction).Save(savepoint);
        return Task.CompletedTask;
    }

    public override Task RollbackToAsync(DbTransaction transaction, string savepoint, CancellationToken ct)
    {
        ((SqlTransaction)transaction).Rollback(savepoint);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The index and constraint names the DDL derives from the configured table names — in one place,
/// so the script and the length validation can never disagree on them.
/// </summary>
internal readonly record struct DerivedNames(
    string QueuePrimaryKey,
    string ChannelIndex,
    string VisibleIndex,
    string MessageIdDefault,
    string MessageIdIndex,
    string DedupeIndex,
    string DeadLetterPrimaryKey,
    string DeadLetterOrderIndex)
{
    public static DerivedNames Of(SqlQueueNames names) => new(
        QueuePrimaryKey: $"PK_{names.QueueTable}",
        ChannelIndex: $"IX_{names.QueueTable}_Channel_VisibleAt",
        VisibleIndex: $"IX_{names.QueueTable}_VisibleAt",
        MessageIdDefault: $"DF_{names.QueueTable}_MessageId",
        MessageIdIndex: $"IX_{names.QueueTable}_MessageId",
        DedupeIndex: $"UX_{names.QueueTable}_DedupeKey",
        DeadLetterPrimaryKey: $"PK_{names.DeadLetterTable}",
        DeadLetterOrderIndex: $"IX_{names.DeadLetterTable}_DeadLetteredAt");

    public IEnumerable<string> All =>
    [
        QueuePrimaryKey, ChannelIndex, VisibleIndex, MessageIdDefault, MessageIdIndex, DedupeIndex,
        DeadLetterPrimaryKey, DeadLetterOrderIndex,
    ];
}
