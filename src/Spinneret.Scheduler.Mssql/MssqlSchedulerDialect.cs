using Spinneret.Queue.Mssql;
using Spinneret.Queue.Sql;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Mssql;

internal sealed class MssqlSchedulerDialect : SqlSchedulerDialect
{
    public static readonly MssqlSchedulerDialect Instance = new();

    private MssqlSchedulerDialect()
    {
    }

    public override SqlQueueDialect QueueDialect => MssqlQueueDialect.Instance;

    public override IEnumerable<string> DerivedIdentifiers(string table) => [PrimaryKey(table), DueIndex(table)];

    private static string PrimaryKey(string table) => $"PK_{table}";

    private static string DueIndex(string table) => $"IX_{table}_NextExecuteAt";

    public override SqlSchedulerStatements CreateStatements(SqlSchedulerNames names)
    {
        var table = MssqlQueueDialect.Instance.Qualify(names.Schema, names.Table);

        return new SqlSchedulerStatements
        {
            // HOLDLOCK's key-range lock serializes racing registrations of a not-yet-existing key.
            // BIN2 makes the schedule comparison ordinal; SET reads pre-update values.
            UpsertRecurring = $"""
                MERGE {table} WITH (HOLDLOCK) AS target
                USING (SELECT @JobKey AS JobKey) AS source
                ON target.JobKey = source.JobKey
                WHEN MATCHED THEN
                    UPDATE SET
                        RequestTypeName = @RequestTypeName,
                        PayloadJson = @PayloadJson,
                        Schedule = @Schedule,
                        NextExecuteAt = CASE
                            WHEN target.Schedule IS NULL OR target.Schedule COLLATE Latin1_General_BIN2 <> @Schedule
                                THEN @NextExecuteAt
                            ELSE target.NextExecuteAt
                        END
                WHEN NOT MATCHED THEN
                    INSERT (JobKey, RequestTypeName, PayloadJson, Schedule, NextExecuteAt, CreatedAt)
                    VALUES (@JobKey, @RequestTypeName, @PayloadJson, @Schedule, @NextExecuteAt, @CreatedAt);
                """,

            InsertOneShot = $"""
                INSERT INTO {table}
                    (JobKey, RequestTypeName, PayloadJson, Schedule, NextExecuteAt, CreatedAt)
                VALUES (@JobKey, @RequestTypeName, @PayloadJson, NULL, @NextExecuteAt, @CreatedAt);
                """,

            // UPDLOCK held to commit is the lease; READPAST lets competing sweeps skip it instead of blocking.
            ClaimNextDue = $"""
                SELECT TOP(1) JobKey, RequestTypeName, PayloadJson, Schedule
                FROM {table} WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE NextExecuteAt <= @Now
                ORDER BY NextExecuteAt;
                """,

            AdvanceRecurring = $"""
                UPDATE {table}
                SET NextExecuteAt = @NextExecuteAt, LastRunAt = @Now
                WHERE JobKey = @JobKey;
                """,

            CompleteOneShot = $"""
                DELETE FROM {table}
                WHERE JobKey = @JobKey;
                """,

            CompensateRecurring = $"""
                UPDATE {table}
                SET NextExecuteAt = @NextExecuteAt, LastRunAt = @Now
                WHERE JobKey = @JobKey AND NextExecuteAt <= @Now;
                """,

            CompensateOneShot = $"""
                DELETE FROM {table}
                WHERE JobKey = @JobKey AND NextExecuteAt <= @Now;
                """,

            // Guard: one-shot rows share the key namespace and have no schedule.
            DeleteRecurring = $"""
                DELETE FROM {table}
                WHERE JobKey = @JobKey AND Schedule IS NOT NULL;
                """,

            // Backstop behind the handle-prefix check: never deletes a recurring job.
            Cancel = $"""
                DELETE FROM {table}
                WHERE JobKey = @JobKey AND Schedule IS NULL;
                """,
        };
    }

    public override string CreateSchemaScript(SqlSchedulerNames names)
    {
        var table = MssqlQueueDialect.Instance.Qualify(names.Schema, names.Table);
        var dueIndex = DueIndex(names.Table);

        return $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            DECLARE @lockResult INT;
            EXEC @lockResult = sp_getapplock
                @Resource = N'Spinneret:SchedulerSchema:{table}',
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 30000;

            IF @lockResult < 0
            BEGIN
                ROLLBACK TRANSACTION;
                THROW 50000, 'Timed out acquiring the Spinneret scheduler schema lock.', 1;
            END;

            IF OBJECT_ID(N'{table}', N'U') IS NULL
            BEGIN
                CREATE TABLE {table} (
                    JobKey NVARCHAR(200) NOT NULL CONSTRAINT {MssqlQueueDialect.Instance.QuoteIdentifier(PrimaryKey(names.Table))} PRIMARY KEY,
                    RequestTypeName NVARCHAR(500) NOT NULL,
                    PayloadJson NVARCHAR(MAX) NOT NULL,
                    Schedule NVARCHAR(500) NULL,
                    NextExecuteAt DATETIME2(3) NOT NULL,
                    CreatedAt DATETIME2(3) NOT NULL,
                    LastRunAt DATETIME2(3) NULL
                );
            END;

            -- Guarded on its own existence, not the table's: the sweep selects on NextExecuteAt, so
            -- a table left without this index scans every job on every tick, forever.
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{table}', N'U')
                  AND name = N'{dueIndex}')
            BEGIN
                CREATE INDEX {MssqlQueueDialect.Instance.QuoteIdentifier(dueIndex)}
                    ON {table} (NextExecuteAt);
            END;

            COMMIT TRANSACTION;
            """;
    }
}
