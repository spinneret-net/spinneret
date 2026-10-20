using Spinneret.Queue.Postgres;
using Spinneret.Queue.Sql;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Postgres;

/// <summary>
/// The PostgreSQL spelling of the scheduled-jobs table — the single source of truth for every
/// statement and the DDL, built from the configured names so the two can never drift apart. Like
/// the queue's dialect, no statement fails on an outcome the scheduler expects, so none can abort
/// the transaction it runs in.
/// </summary>
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
            // INSERT … ON CONFLICT DO UPDATE is Postgres's single-statement upsert: racing
            // registrations of a new key serialize on the primary key instead of both inserting. The
            // schedule comparison uses the "C" collation so it is ordinal, like the schedule text
            // itself; a one-shot row under the same key (schedule NULL) is taken over and armed. The
            // CASE reads the row as it was before the update.
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

            // FOR UPDATE holds the row until the sweep's transaction commits — that lock is the
            // lease — and SKIP LOCKED lets a competing host's sweep move straight to the next due
            // job instead of blocking, so hosts sweep in parallel without double-dispatching.
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

            // The mirror guard: a cancel can never delete a recurring job's schedule. A job that
            // already ran is simply gone, which makes this a no-op.
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
