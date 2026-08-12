namespace Spinneret.Queue.Mssql;

/// <summary>
/// DDL for the queue tables. The startup initializer runs it when
/// <see cref="MssqlQueueOptions.CreateSchema"/> is on; hosts that own their schema through
/// migrations run it themselves and turn that off.
/// </summary>
public static class MssqlQueueSchema
{
    /// <summary>Idempotent creation script for the queue and dead-letter tables.</summary>
    public static string CreateScript(MssqlQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var queueTable = Identifier.Qualify(options.SchemaName, options.QueueTableName);
        var deadLetterTable = Identifier.Qualify(options.SchemaName, options.DeadLetterTableName);

        return $"""
            IF OBJECT_ID(N'{queueTable}', N'U') IS NULL
            BEGIN
                CREATE TABLE {queueTable} (
                    Id BIGINT IDENTITY NOT NULL CONSTRAINT {Identifier.Quote($"PK_{options.QueueTableName}")} PRIMARY KEY,
                    Channel NVARCHAR(100) NOT NULL,
                    VisibleAt DATETIME2(3) NOT NULL,
                    DedupeKey NVARCHAR(200) NULL,
                    Envelope NVARCHAR(MAX) NOT NULL
                );

                CREATE INDEX {Identifier.Quote($"IX_{options.QueueTableName}_Channel_VisibleAt")}
                    ON {queueTable} (Channel, VisibleAt);

                CREATE UNIQUE INDEX {Identifier.Quote($"UX_{options.QueueTableName}_DedupeKey")}
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
            """;
    }
}
