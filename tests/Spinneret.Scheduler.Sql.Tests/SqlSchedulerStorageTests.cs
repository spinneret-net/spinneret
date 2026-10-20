using System.Data.Common;
using Spinneret.Queue.Sql;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Sql.Tests;

/// <summary>
/// The scheduler's pairing with the queue storage, and its options validation — what every dialect
/// inherits, tested against stand-in dialects with no database.
/// </summary>
public sealed class SqlSchedulerStorageTests
{
    [Test]
    public async Task A_scheduler_dialect_pairs_with_a_queue_storage_of_its_own_database()
    {
        var queue = Storage(new FakeQueueDialect("alpha"));

        var storage = new SqlSchedulerStorage(queue, new FakeSchedulerDialect(new FakeQueueDialect("alpha")), "jobs");

        await Assert.That(storage.Names).IsEqualTo(new SqlSchedulerNames("s", "jobs"));
    }

    [Test]
    public async Task A_scheduler_dialect_refuses_a_queue_storage_of_another_database()
    {
        // The booking and the enqueue must commit in one transaction; no two databases share one.
        var queue = Storage(new FakeQueueDialect("alpha"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new SqlSchedulerStorage(queue, new FakeSchedulerDialect(new FakeQueueDialect("beta")), "jobs"));

        await Assert.That(ex.Message).Contains("'beta' storage cannot share a transaction with a 'alpha' queue");
    }

    [Test]
    [Arguments("jobs", true)]
    [Arguments("bad name", false)]
    [Arguments("j123456789", false)]
    public async Task The_table_name_and_the_names_it_derives_must_fit_the_queue_dialects_rules(string table, bool valid)
    {
        // The limit is 12; "ix_<table>" is the derived name, so a ten-character table overflows it.
        var validator = new SqlSchedulerOptionsValidator<FakeSchedulerOptions>(
            new FakeSchedulerDialect(new FakeQueueDialect("alpha")), "Scheduler:Fake");

        var result = validator.Validate(null, new FakeSchedulerOptions { TableName = table });

        await Assert.That(result.Succeeded).IsEqualTo(valid);
        if (!valid)
            await Assert.That(result.FailureMessage).Contains("Scheduler:Fake:TableName");
    }

    private static SqlQueueStorage Storage(SqlQueueDialect dialect) =>
        new(new FakeDataSource(), dialect, new SqlQueueNames("s", "q", "d"), createSchema: false);

    private sealed class FakeSchedulerOptions() : SqlSchedulerOptions("jobs");

    private sealed class FakeSchedulerDialect(SqlQueueDialect queueDialect) : SqlSchedulerDialect
    {
        public override SqlQueueDialect QueueDialect => queueDialect;

        public override IEnumerable<string> DerivedIdentifiers(string table) => [$"ix_{table}"];

        public override SqlSchedulerStatements CreateStatements(SqlSchedulerNames names) => new()
        {
            UpsertRecurring = "upsert",
            InsertOneShot = "insert",
            ClaimNextDue = "claim",
            AdvanceRecurring = "advance",
            CompleteOneShot = "complete",
            CompensateRecurring = "compensate-recurring",
            CompensateOneShot = "compensate-one-shot",
            DeleteRecurring = "delete",
            Cancel = "cancel",
        };

        public override string CreateSchemaScript(SqlSchedulerNames names) => "schema";
    }

    private sealed class FakeQueueDialect(string name) : SqlQueueDialect
    {
        public override string Name => name;
        public override int MaxIdentifierLength => 12;
        public override string QuoteIdentifier(string identifier) => identifier;
        public override IEnumerable<string> DerivedIdentifiers(SqlQueueNames names) => [];

        public override SqlQueueStatements CreateStatements(SqlQueueNames names) => new()
        {
            Enqueue = "", Dequeue = "", LeaseForRelay = "", LeaseById = "", DeleteRelayed = "", ReleaseLease = "",
            Reschedule = "", WriteDeadLetter = "", ListDeadLetters = "", ListDeadLettersAfter = "",
            GetDeadLetter = "", DeleteDeadLetter = "",
        };

        public override string CreateSchemaScript(SqlQueueNames names) => "";
        public override void AddTimestamp(DbCommand command, string name, DateTimeOffset utc) => throw new NotSupportedException();
        public override DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal) => throw new NotSupportedException();
    }

    private sealed class FakeDataSource : DbDataSource
    {
        public override string ConnectionString => "fake";
        protected override DbConnection CreateDbConnection() => throw new NotSupportedException();
    }
}
