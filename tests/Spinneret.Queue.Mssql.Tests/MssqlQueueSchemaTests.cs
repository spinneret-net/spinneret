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
