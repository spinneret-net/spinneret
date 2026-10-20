namespace Spinneret.Queue.Mssql.Tests;

public sealed class MssqlQueueSchemaTests
{
    [Test]
    public async Task CreateScript_uses_the_configured_names()
    {
        var options = new MssqlStorageOptions
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
        var script = MssqlQueueSchema.CreateScript(new MssqlStorageOptions());

        await Assert.That(script).Contains("IF OBJECT_ID(N'[dbo].[SpinneretQueue]', N'U') IS NULL");
        await Assert.That(script).Contains("IF OBJECT_ID(N'[dbo].[SpinneretDeadLetters]', N'U') IS NULL");
    }

    [Test]
    public async Task CreateScript_indexes_channel_visibility_and_dedupe()
    {
        var script = MssqlQueueSchema.CreateScript(new MssqlStorageOptions());

        await Assert.That(script).Contains("(Channel, VisibleAt)");
        await Assert.That(script).Contains("UNIQUE INDEX");
        await Assert.That(script).Contains("WHERE DedupeKey IS NOT NULL");
    }

    [Test]
    public async Task CreateScript_indexes_the_dead_letter_paging_order()
    {
        var script = MssqlQueueSchema.CreateScript(new MssqlStorageOptions());

        await Assert.That(script).Contains("(DeadLetteredAt DESC, IdempotencyKey DESC)");
    }

    [Test]
    [Arguments("IX_SpinneretQueue_Channel_VisibleAt")]
    [Arguments("UX_SpinneretQueue_DedupeKey")]
    [Arguments("IX_SpinneretDeadLetters_DeadLetteredAt")]
    public async Task CreateScript_guards_every_index_on_its_own_existence(string index)
    {
        // Guarding on the table would leave an index forever missing on older or race-losing databases.
        var script = MssqlQueueSchema.CreateScript(new MssqlStorageOptions());

        await Assert.That(script).Contains($"AND name = N'{index}'");
    }

    [Test]
    public async Task CreateScript_creates_no_index_inside_a_table_block()
    {
        // One sys.indexes guard per index creation, counted over statements since comments mention CREATE INDEX.
        var statements = string.Join('\n', MssqlQueueSchema.CreateScript(new MssqlStorageOptions())
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        var guards = statements.Split("FROM sys.indexes").Length - 1;
        var creates = statements.Split("CREATE INDEX").Length - 1
            + statements.Split("CREATE UNIQUE INDEX").Length - 1;

        await Assert.That(creates).IsEqualTo(5);
        await Assert.That(guards).IsEqualTo(creates);
    }

    [Test]
    public async Task CreateScript_adds_the_message_id_and_lease_columns_to_an_existing_table()
    {
        // Older tables get the columns too; the MessageId default lets waiting rows keep their place.
        var script = MssqlQueueSchema.CreateScript(new MssqlStorageOptions());

        await Assert.That(script).Contains("IF COL_LENGTH(N'[dbo].[SpinneretQueue]', N'MessageId') IS NULL");
        await Assert.That(script).Contains("DEFAULT NEWID() WITH VALUES");
        await Assert.That(script).Contains("IF COL_LENGTH(N'[dbo].[SpinneretQueue]', N'LeasedUntil') IS NULL");
        await Assert.That(script).Contains("[IX_SpinneretQueue_VisibleAt]");
    }

    [Test]
    public async Task CreateScript_serializes_itself_against_other_hosts_running_it()
    {
        // Every replica runs this at startup and IF OBJECT_ID ... CREATE TABLE isn't atomic, hence the lock.
        var script = MssqlQueueSchema.CreateScript(new MssqlStorageOptions());

        await Assert.That(script).Contains("BEGIN TRANSACTION");
        await Assert.That(script).Contains("sp_getapplock");
        await Assert.That(script).Contains("@Resource = N'Spinneret:QueueSchema:[dbo].[SpinneretQueue]'");
        await Assert.That(script).Contains("@LockMode = 'Exclusive'");
        await Assert.That(script).Contains("@LockOwner = 'Transaction'");
        await Assert.That(script).Contains("COMMIT TRANSACTION");
        // A lock that timed out must not be mistaken for one that was granted.
        await Assert.That(script).Contains("IF @lockResult < 0");
    }

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
    [Arguments("x123456789x123456789x123456789x123456789x123456789x123456789x123456789x123456789x123456789x123456789x123456789x123456789x1234567", true)]
    [Arguments("x123456789x123456789x123456789x123456789x123456789x123456789x123456789x123456789x123456789x123456789x123456789x123456789x12345678", false)]
    public async Task Identifier_validation_accepts_plain_identifiers_only(string identifier, bool valid)
    {
        await Assert.That(MssqlQueueDialect.Instance.IsValidIdentifier(identifier)).IsEqualTo(valid);
    }

    [Test]
    public async Task Identifier_quoting_escapes_closing_brackets()
    {
        // Validation rejects brackets, but quoting must still be correct.
        await Assert.That(MssqlQueueDialect.Instance.QuoteIdentifier("a]b")).IsEqualTo("[a]]b]");
    }

    [Test]
    public async Task CreateScript_indexes_the_message_id_the_eager_forward_looks_rows_up_by()
    {
        var script = MssqlQueueSchema.CreateScript(new MssqlStorageOptions { QueueTableName = "Q" });

        await Assert.That(script).Contains("[IX_Q_MessageId] ON [dbo].[Q] (MessageId)");
        await Assert.That(script).Contains("name = N'IX_Q_MessageId'");
    }
}
