using Spinneret.Queue.Postgres;
using Spinneret.Queue.Sql;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Postgres;

// No statement fails on an expected outcome, since a Postgres error aborts the whole transaction.
internal sealed class PostgresSchedulerDialect : SqlSchedulerDialect
{
    public static readonly PostgresSchedulerDialect Instance = new();

    private PostgresSchedulerDialect()
    {
    }

    public override SqlQueueDialect QueueDialect => PostgresQueueDialect.Instance;

    public override IEnumerable<string> DerivedIdentifiers(string table) => [PrimaryKey(table), DueIndex(table)];

    private static string PrimaryKey(string table) => $"{table}_pkey";

    private static string DueIndex(string table) => $"{table}_nextexecuteat_idx";

    public override SqlSchedulerStatements CreateStatements(SqlSchedulerNames names)
    {
        var table = QueueDialect.Qualify(names.Schema, names.Table);

        return new SqlSchedulerStatements
        {
            // Racing registrations serialize on the primary key. "C" collation makes the comparison ordinal.
            UpsertRecurring = $"""
                INSERT INTO {table} AS target
                    (JobKey, RequestTypeName, PayloadJson, Schedule, NextExecuteAt, CreatedAt)
                VALUES (@JobKey, @RequestTypeName, @PayloadJson, @Schedule, @NextExecuteAt, @CreatedAt)
                ON CONFLICT (JobKey) DO UPDATE SET
                    RequestTypeName = EXCLUDED.RequestTypeName,
                    PayloadJson = EXCLUDED.PayloadJson,
                    Schedule = EXCLUDED.Schedule,
                    NextExecuteAt = CASE
                        WHEN target.Schedule IS NULL OR target.Schedule COLLATE "C" <> EXCLUDED.Schedule COLLATE "C"
                            THEN EXCLUDED.NextExecuteAt
                        ELSE target.NextExecuteAt
                    END;
                """,

            InsertOneShot = $"""
                INSERT INTO {table}
                    (JobKey, RequestTypeName, PayloadJson, Schedule, NextExecuteAt, CreatedAt)
                VALUES (@JobKey, @RequestTypeName, @PayloadJson, NULL, @NextExecuteAt, @CreatedAt);
                """,

            // FOR UPDATE held to commit is the lease; SKIP LOCKED lets competing sweeps skip it instead of blocking.
            ClaimNextDue = $"""
                SELECT JobKey, RequestTypeName, PayloadJson, Schedule
                FROM {table}
                WHERE NextExecuteAt <= @Now
                ORDER BY NextExecuteAt
                LIMIT 1
                FOR UPDATE SKIP LOCKED;
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
        var table = QueueDialect.Qualify(names.Schema, names.Table);

        return $"""
            DO $spinneret$
            BEGIN
                -- Serializes the script against every other host running it, for the same reason as
                -- the queue's: CREATE TABLE IF NOT EXISTS is not safe against itself.
                PERFORM pg_advisory_xact_lock(hashtextextended('Spinneret:SchedulerSchema:{table.Replace("'", "''")}', 0));

                CREATE TABLE IF NOT EXISTS {table} (
                    JobKey varchar(200) NOT NULL CONSTRAINT {QueueDialect.QuoteIdentifier(PrimaryKey(names.Table))} PRIMARY KEY,
                    RequestTypeName varchar(500) NOT NULL,
                    PayloadJson text NOT NULL,
                    Schedule varchar(500) NULL,
                    NextExecuteAt timestamptz NOT NULL,
                    CreatedAt timestamptz NOT NULL,
                    LastRunAt timestamptz NULL
                );

                -- Guarded on its own existence, not the table's: the sweep selects on NextExecuteAt,
                -- so a table left without this index scans every job on every tick, forever.
                CREATE INDEX IF NOT EXISTS {QueueDialect.QuoteIdentifier(DueIndex(names.Table))}
                    ON {table} (NextExecuteAt);
            END
            $spinneret$;
            """;
    }
}
