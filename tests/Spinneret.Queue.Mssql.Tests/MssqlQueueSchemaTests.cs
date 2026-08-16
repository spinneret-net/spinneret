namespace Spinneret.Queue.Mssql.Tests;

public sealed class MssqlQueueSchemaTests
{
    [Test]
    public async Task CreateScript_uses_the_configured_names()
    {
        var options = new MssqlQueueOptions
        {
            SchemaName = "queues",
            QueueTableName = "MyQueue",
            DeadLetterTableName = "MyDeadLetters",
        };

        var script = MssqlQueueSchema.CreateScript(options);

        await Assert.That(script).Contains("[queues].[MyQueue]");
        await Assert.That(script).Contains("[queues].[MyDeadLetters]");
    }

    [Test]
    public async Task CreateScript_is_idempotent_by_guarding_on_object_id()
    {
        var script = MssqlQueueSchema.CreateScript(new MssqlQueueOptions());

        await Assert.That(script).Contains("IF OBJECT_ID(N'[dbo].[SpinneretQueue]', N'U') IS NULL");
        await Assert.That(script).Contains("IF OBJECT_ID(N'[dbo].[SpinneretDeadLetters]', N'U') IS NULL");
    }

    [Test]
    public async Task CreateScript_indexes_channel_visibility_and_dedupe()
    {
        var script = MssqlQueueSchema.CreateScript(new MssqlQueueOptions());

        await Assert.That(script).Contains("(Channel, VisibleAt)");
        await Assert.That(script).Contains("UNIQUE INDEX");
        await Assert.That(script).Contains("WHERE DedupeKey IS NOT NULL");
    }

    [Test]
    public async Task CreateScript_indexes_the_dead_letter_paging_order()
    {
        var script = MssqlQueueSchema.CreateScript(new MssqlQueueOptions());

        await Assert.That(script).Contains("(DeadLetteredAt DESC, IdempotencyKey DESC)");
    }

    [Test]
    [Arguments("IX_SpinneretQueue_Channel_VisibleAt")]
    [Arguments("UX_SpinneretQueue_DedupeKey")]
    [Arguments("IX_SpinneretDeadLetters_DeadLetteredAt")]
    public async Task CreateScript_guards_every_index_on_its_own_existence(string index)
    {
        // Never on the owning table's existence. Two databases would otherwise keep an index
        // forever missing: one created before the index was added to this script, and one whose
        // table was created by a host that lost the create race before it had run the CREATE INDEX
        // statements. Both are invisible until a query silently goes to a scan — or, for the dedupe
        // index, until deduplication stops happening and says nothing.
        var script = MssqlQueueSchema.CreateScript(new MssqlQueueOptions());

        await Assert.That(script).Contains($"AND name = N'{index}'");
    }

    [Test]
    public async Task CreateScript_creates_no_index_inside_a_table_block()
    {
        // The structural half of the rule above: as many sys.indexes guards as index creations, so
        // no index can be riding on a table block's guard instead of its own. Counted over the
        // statements only — the script's own comments discuss CREATE INDEX and would inflate this.
        var statements = string.Join('\n', MssqlQueueSchema.CreateScript(new MssqlQueueOptions())
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        var guards = statements.Split("FROM sys.indexes").Length - 1;
        var creates = statements.Split("CREATE INDEX").Length - 1
            + statements.Split("CREATE UNIQUE INDEX").Length - 1;

        await Assert.That(creates).IsEqualTo(3);
        await Assert.That(guards).IsEqualTo(creates);
    }

    [Test]
    public async Task CreateScript_serializes_itself_against_other_hosts_running_it()
    {
        // Every host runs this at startup, so a fleet scaling from zero runs it from every replica
        // at once. "IF OBJECT_ID(...) IS NULL CREATE TABLE" is not atomic and nothing here shares an
        // implicit transaction, so without the lock two replicas can both decide to create the
        // table. Held for the transaction, so it is released by the commit either way.
        var script = MssqlQueueSchema.CreateScript(new MssqlQueueOptions());

        await Assert.That(script).Contains("BEGIN TRANSACTION");
        await Assert.That(script).Contains("sp_getapplock");
        await Assert.That(script).Contains("@Resource = N'Spinneret:QueueSchema:[dbo].[SpinneretQueue]'");
        await Assert.That(script).Contains("@LockMode = 'Exclusive'");
        await Assert.That(script).Contains("@LockOwner = 'Transaction'");
        await Assert.That(script).Contains("COMMIT TRANSACTION");
        // A lock that timed out must not be mistaken for one that was granted.
        await Assert.That(script).Contains("IF @lockResult < 0");
    }

    // ------------------------------------------------------------------------ identifiers ---

    [Test]
    [Arguments("SpinneretQueue", true)]
    [Arguments("_private", true)]
    [Arguments("Table1", true)]
    [Arguments("", false)]
    [Arguments("   ", false)]
    [Arguments("1Table", false)]
    [Arguments("bad name", false)]
    [Arguments("semi;colon", false)]
    [Arguments("bracket]name", false)]
    [Arguments("quote'name", false)]
    public async Task Identifier_validation_accepts_plain_identifiers_only(string identifier, bool valid)
    {
        await Assert.That(Identifier.IsValid(identifier)).IsEqualTo(valid);
    }

    [Test]
    public async Task Identifier_quoting_escapes_closing_brackets()
    {
        // Defense in depth: validation rejects brackets, but quoting must still be correct.
        await Assert.That(Identifier.Quote("a]b")).IsEqualTo("[a]]b]");
    }
}
