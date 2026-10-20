using Spinneret.Queue.Mssql;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// The SQL Server spelling of the scheduled-jobs table — the single source of truth for every
/// statement and the DDL, built from the configured names so the two can never drift apart.
/// </summary>
internal sealed class MssqlSchedulerDialect : SqlSchedulerDialect
{
    public static readonly MssqlSchedulerDialect Instance = new();

    private MssqlSchedulerDialect()
    {
    }

    public override string QueueDialectName => "mssql";

    public override SqlSchedulerStatements CreateStatements(SqlSchedulerNames names)
    {
        var table = Identifier.Qualify(names.Schema, names.Table);

        return new SqlSchedulerStatements
        {
            // MERGE with HOLDLOCK is SQL Server's single-statement upsert: the key-range lock covers
            // a not-yet-existing key, so racing registrations serialize instead of both inserting.
            // The schedule comparison is binary so it is ordinal, like the schedule text itself; a
            // one-shot row under the same key (Schedule NULL) is taken over and armed. SET reads the
            // pre-update values, so the CASE sees the old schedule whatever the column order.
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

            // UPDLOCK holds the row until the sweep's transaction commits — that lock is the lease —
            // and READPAST lets a competing host's sweep skip straight to the next due job instead
            // of blocking, so hosts sweep in parallel without double-dispatching.
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

            // Runs on the claim's transaction, so the row disappears in the same commit that makes
            // the queue message durable — the message is the record from then on.
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

            // The Schedule NOT NULL guard is what keeps a one-shot job safe: one-shot handles live in
            // the same JobKey namespace and are the rows with no schedule.
            DeleteRecurring = $"""
                DELETE FROM {table}
                WHERE JobKey = @JobKey AND Schedule IS NOT NULL;
                """,

            // The Schedule IS NULL guard mirrors DeleteRecurring's: a backstop behind the
            // handle-prefix check, so a cancel can never delete a recurring job's schedule. A job
            // that already ran is simply gone, which makes this a no-op.
            Cancel = $"""
                DELETE FROM {table}
                WHERE JobKey = @JobKey AND Schedule IS NULL;
                """,
        };
    }

    public override string CreateSchemaScript(SqlSchedulerNames names)
    {
        var table = Identifier.Qualify(names.Schema, names.Table);
        var dueIndex = $"IX_{names.Table}_NextExecuteAt";

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
                    JobKey NVARCHAR(200) NOT NULL CONSTRAINT {Identifier.Quote($"PK_{names.Table}")} PRIMARY KEY,
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
                CREATE INDEX {Identifier.Quote(dueIndex)}
                    ON {table} (NextExecuteAt);
            END;

            COMMIT TRANSACTION;
            """;
    }
}
