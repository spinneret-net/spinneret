using Spinneret.Queue.Postgres;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Postgres.Tests;

public sealed class PostgresSchedulerSchemaTests
{
    private static readonly SqlSchedulerStatements Statements =
        PostgresSchedulerDialect.Instance.CreateStatements(new SqlSchedulerNames("app", "Jobs"));

    [Test]
    public async Task Schema_script_uses_the_configured_names()
    {
        var script = PostgresSchedulerSchema.CreateScript(
            new PostgresStorageOptions { SchemaName = "jobs" },
            new PostgresSchedulerOptions { TableName = "MyJobs" });

        await Assert.That(script).Contains("CREATE TABLE IF NOT EXISTS \"jobs\".\"MyJobs\"");
        await Assert.That(script).Contains("CREATE INDEX IF NOT EXISTS \"MyJobs_nextexecuteat_idx\"");
        // Existence is the state, so there is no status column to index on.
        await Assert.That(script).DoesNotContain("Status");
    }

    [Test]
    public async Task Schema_script_is_one_atomic_block_serialized_by_an_advisory_lock()
    {
        var script = PostgresSchedulerSchema.CreateScript(new PostgresStorageOptions(), new PostgresSchedulerOptions());

        await Assert.That(script.TrimStart()).StartsWith("DO $spinneret$");
        await Assert.That(script).Contains("pg_advisory_xact_lock(hashtextextended('Spinneret:SchedulerSchema:");
    }

    [Test]
    public async Task Registration_is_one_upsert_that_rearms_only_on_a_changed_schedule()
    {
        await Assert.That(Statements.UpsertRecurring).Contains("ON CONFLICT (JobKey) DO UPDATE");
        await Assert.That(Statements.UpsertRecurring).Contains("COLLATE \"C\"");
        await Assert.That(Statements.UpsertRecurring).Contains("ELSE target.NextExecuteAt");
    }

    [Test]
    public async Task The_sweep_claims_with_a_row_lock_that_skips_rows_other_sweeps_hold()
    {
        await Assert.That(Statements.ClaimNextDue).Contains("FOR UPDATE SKIP LOCKED");
    }

    [Test]
    public async Task Default_table_name_is_snake_case()
    {
        await Assert.That(new PostgresSchedulerOptions().TableName).IsEqualTo("spinneret_scheduled_jobs");
    }
}
