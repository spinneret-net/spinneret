using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Sql.Tests;

/// <summary>The identifier rules and options validation every dialect inherits.</summary>
public sealed class SqlStorageOptionsTests
{
    [Test]
    [Arguments("queue", true)]
    [Arguments("_private", true)]
    [Arguments("Table1", true)]
    [Arguments("", false)]
    [Arguments("   ", false)]
    [Arguments("1Table", false)]
    [Arguments("bad name", false)]
    [Arguments("semi;colon", false)]
    [Arguments("quote'name", false)]
    [Arguments("double\"quote", false)]
    [Arguments("bracket]name", false)]
    [Arguments("ümlaut", false)]
    [Arguments("x23456789x", true)]
    [Arguments("x23456789x1", false)]
    public async Task Identifiers_are_plain_and_within_the_dialects_limit(string identifier, bool valid)
    {
        var dialect = new FakeDialect(maxIdentifierLength: 10);

        await Assert.That(dialect.IsValidIdentifier(identifier)).IsEqualTo(valid);
    }

    [Test]
    public async Task Qualify_quotes_schema_and_table_through_the_dialect()
    {
        await Assert.That(new FakeDialect().Qualify("s", "t")).IsEqualTo("<s>.<t>");
    }

    [Test]
    public async Task Valid_options_pass()
    {
        var validator = new SqlStorageOptionsValidator<FakeOptions>(new FakeDialect(), "Queue:Fake", requiresConnectionString: true);

        await Assert.That(validator.Validate(null, new FakeOptions()).Succeeded).IsTrue();
    }

    [Test]
    public async Task A_derived_name_over_the_limit_fails_naming_the_keys_and_the_name()
    {
        // "queue_twenty_chars_" fits the 30-character limit; "ix_<it>_long_suffix" does not.
        var validator = new SqlStorageOptionsValidator<FakeOptions>(new FakeDialect(), "Queue:Fake", requiresConnectionString: true);
        var options = new FakeOptions { QueueTableName = "queue_twenty_chars_" };

        var result = validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Queue:Fake:QueueTableName");
        await Assert.That(result.FailureMessage).Contains("ix_queue_twenty_chars__long_suffix");
    }

    [Test]
    public async Task The_connection_string_is_only_required_without_a_host_data_source()
    {
        var options = new FakeOptions { ConnectionString = "" };

        var withoutSource = new SqlStorageOptionsValidator<FakeOptions>(new FakeDialect(), "Queue:Fake", requiresConnectionString: true);
        var withSource = new SqlStorageOptionsValidator<FakeOptions>(new FakeDialect(), "Queue:Fake", requiresConnectionString: false);

        await Assert.That(withoutSource.Validate(null, options).FailureMessage).Contains("Queue:Fake:ConnectionString");
        await Assert.That(withSource.Validate(null, options).Succeeded).IsTrue();
    }

    [Test]
    public async Task The_storage_disposes_a_data_source_it_owns()
    {
        var dataSource = new FakeDataSource();
        var storage = new SqlQueueStorage(dataSource, new FakeDialect(), new SqlQueueNames("s", "q", "d"), createSchema: false);

        await storage.DisposeAsync();

        await Assert.That(dataSource.Disposed).IsTrue();
    }

    [Test]
    public async Task The_storage_leaves_a_host_data_source_to_the_host()
    {
        var dataSource = new FakeDataSource();
        var storage = new SqlQueueStorage(
            dataSource, new FakeDialect(), new SqlQueueNames("s", "q", "d"), createSchema: false, ownsDataSource: false);

        await storage.DisposeAsync();

        await Assert.That(dataSource.Disposed).IsFalse();
    }
}
