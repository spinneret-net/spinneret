namespace Spinneret.Queue.Mssql;

/// <summary>
/// DDL for the queue tables. The startup initializer runs it when
/// <see cref="MssqlQueueOptions.CreateSchema"/> is on; hosts that own their schema through
/// migrations run it themselves and turn that off.
/// </summary>
public static class MssqlQueueSchema
{
    /// <summary>
    /// Idempotent creation script for the queue and dead-letter tables. Safe to run concurrently:
    /// every host runs it at startup, and a fleet scaling from zero runs it from every replica at
    /// once.
    /// </summary>
    public static string CreateScript(MssqlQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var queueTable = Identifier.Qualify(options.SchemaName, options.QueueTableName);
        var deadLetterTable = Identifier.Qualify(options.SchemaName, options.DeadLetterTableName);
        var channelIndex = $"IX_{options.QueueTableName}_Channel_VisibleAt";
        var dedupeIndex = $"UX_{options.QueueTableName}_DedupeKey";
        var deadLetterOrderIndex = $"IX_{options.DeadLetterTableName}_DeadLetteredAt";

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
                    Id BIGINT IDENTITY NOT NULL CONSTRAINT {Identifier.Quote($"PK_{options.QueueTableName}")} PRIMARY KEY,
                    Channel NVARCHAR(100) NOT NULL,
                    VisibleAt DATETIME2(3) NOT NULL,
                    DedupeKey NVARCHAR(200) NULL,
                    Envelope NVARCHAR(MAX) NOT NULL
                );
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
                CREATE INDEX {Identifier.Quote(channelIndex)}
                    ON {queueTable} (Channel, VisibleAt);
            END;

            -- Not merely an optimisation: Enqueue detects a duplicate dedupe key by catching this
            -- index's unique violation (2601/2627). Without it the insert simply succeeds and the
            -- enqueue reports that it stored a new message, so deduplication silently stops working.
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{queueTable}', N'U')
                  AND name = N'{dedupeIndex}')
            BEGIN
                CREATE UNIQUE INDEX {Identifier.Quote(dedupeIndex)}
                    ON {queueTable} (DedupeKey)
                    WHERE DedupeKey IS NOT NULL;
            END;

            IF OBJECT_ID(N'{deadLetterTable}', N'U') IS NULL
            BEGIN
                CREATE TABLE {deadLetterTable} (
                    IdempotencyKey NVARCHAR(200) NOT NULL CONSTRAINT {Identifier.Quote($"PK_{options.DeadLetterTableName}")} PRIMARY KEY,
                    Source NVARCHAR(20) NOT NULL,
                    CommandTypeName NVARCHAR(500) NOT NULL,
                    Description NVARCHAR(1000) NULL,
                    PayloadJson NVARCHAR(MAX) NOT NULL,
                    Error NVARCHAR(MAX) NOT NULL,
                    Attempts INT NOT NULL,
                    DeadLetteredAt DATETIME2(3) NOT NULL
                );
            END;

            -- Serves the admin page's (DeadLetteredAt DESC, IdempotencyKey DESC) keyset paging.
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{deadLetterTable}', N'U')
                  AND name = N'{deadLetterOrderIndex}')
            BEGIN
                CREATE INDEX {Identifier.Quote(deadLetterOrderIndex)}
                    ON {deadLetterTable} (DeadLetteredAt DESC, IdempotencyKey DESC);
            END;

            COMMIT TRANSACTION;
            """;
    }
}
