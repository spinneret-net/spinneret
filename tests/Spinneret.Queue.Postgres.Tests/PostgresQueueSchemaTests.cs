using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Spinneret.Queue.Sql;
using Spinneret.Sql.Conformance;

namespace Spinneret.Queue.Postgres.Tests;

/// <summary>
/// What the PostgreSQL script and statements say — the properties that keep them safe on a
/// database that aborts a transaction on any error. What running them does is the conformance
/// suites' job.
/// </summary>
public sealed class PostgresQueueSchemaTests
{
    private static readonly SqlQueueStatements Statements =
        PostgresQueueDialect.Instance.CreateStatements(new SqlQueueNames("app", "Q", "DL"));

    [Test]
    public async Task CreateScript_uses_the_configured_names_quoted()
    {
        var script = PostgresQueueSchema.CreateScript(new PostgresStorageOptions
        {
            SchemaName = "queues",
            QueueTableName = "MyQueue",
            DeadLetterTableName = "MyDeadLetters",
        });

        await Assert.That(script).Contains("\"queues\".\"MyQueue\"");
        await Assert.That(script).Contains("\"queues\".\"MyDeadLetters\"");
    }

    [Test]
    public async Task CreateScript_is_one_atomic_block_serialized_by_an_advisory_lock()
    {
        var script = PostgresQueueSchema.CreateScript(new PostgresStorageOptions());

        await Assert.That(script.TrimStart()).StartsWith("DO $spinneret$");
        await Assert.That(script.TrimEnd()).EndsWith("$spinneret$;");
        // Transaction-scoped, so it is released with the block whatever happens — and works behind
        // a transaction-pooling proxy, which session-level locks do not.
        await Assert.That(script).Contains("pg_advisory_xact_lock(hashtextextended('Spinneret:QueueSchema:\"public\".\"spinneret_queue\"', 0))");
    }

    [Test]
    public async Task CreateScript_guards_every_table_column_and_index_on_its_own_existence()
    {
        var script = PostgresQueueSchema.CreateScript(new PostgresStorageOptions());

        await Assert.That(script).Contains("CREATE TABLE IF NOT EXISTS \"public\".\"spinneret_queue\"");
        await Assert.That(script).Contains("CREATE TABLE IF NOT EXISTS \"public\".\"spinneret_dead_letters\"");
        await Assert.That(script).Contains("ADD COLUMN IF NOT EXISTS MessageId uuid NOT NULL DEFAULT gen_random_uuid()");
        await Assert.That(script).Contains("ADD COLUMN IF NOT EXISTS LeasedUntil timestamptz NULL");
        await Assert.That(script).Contains("ADD COLUMN IF NOT EXISTS LeaseToken uuid NULL");
        await Assert.That(script).Contains("ADD COLUMN IF NOT EXISTS TraceId varchar(32) NULL");
        await Assert.That(script).Contains("CREATE INDEX IF NOT EXISTS \"spinneret_queue_channel_visibleat_idx\"");
        await Assert.That(script).Contains("CREATE INDEX IF NOT EXISTS \"spinneret_queue_visibleat_idx\"");
        await Assert.That(script).Contains("CREATE INDEX IF NOT EXISTS \"spinneret_queue_messageid_idx\"");
        await Assert.That(script).Contains("CREATE UNIQUE INDEX IF NOT EXISTS \"spinneret_queue_dedupekey_key\"");
        await Assert.That(script).Contains("CREATE INDEX IF NOT EXISTS \"spinneret_dead_letters_deadletteredat_idx\"");
    }

    [Test]
    public async Task CreateScript_keeps_payloads_as_text_and_timestamps_with_time_zone()
    {
        var script = PostgresQueueSchema.CreateScript(new PostgresStorageOptions());

        // Text, not jsonb: an envelope must come back byte for byte as it was written.
        await Assert.That(script).Contains("Envelope text NOT NULL");
        await Assert.That(script).DoesNotContain("jsonb");
        await Assert.That(script).DoesNotContain("timestamp NOT NULL");
        await Assert.That(script).Contains("VisibleAt timestamptz NOT NULL");
    }

    [Test]
    public async Task Expected_conflicts_are_resolved_in_the_statement_never_raised()
    {
        // A raised violation would abort the caller's whole transaction on Postgres.
        await Assert.That(Statements.Enqueue).Contains("ON CONFLICT (DedupeKey) WHERE DedupeKey IS NOT NULL DO NOTHING");
        await Assert.That(Statements.WriteDeadLetter).Contains("ON CONFLICT (IdempotencyKey) DO NOTHING");
    }

    [Test]
    public async Task Claims_skip_rows_other_transactions_hold()
    {
        await Assert.That(Statements.Dequeue).Contains("FOR UPDATE SKIP LOCKED");
        await Assert.That(Statements.LeaseForRelay).Contains("FOR UPDATE SKIP LOCKED");
        await Assert.That(Statements.LeaseById).Contains("FOR UPDATE SKIP LOCKED");
    }

    // ------------------------------------------------------------------------ identifiers ---

    [Test]
    [Arguments("spinneret_queue", true)]
    [Arguments("MixedCase", true)]
    [Arguments("1table", false)]
    [Arguments("bad name", false)]
    [Arguments("quote\"name", false)]
    [Arguments("x23456789x23456789x23456789x23456789x23456789x23456789x234567890", false)]
    [Arguments("x23456789x23456789x23456789x23456789x23456789x23456789x23456789", true)]
    public async Task Identifiers_fit_namedatalen(string identifier, bool valid)
    {
        await Assert.That(PostgresQueueDialect.Instance.IsValidIdentifier(identifier)).IsEqualTo(valid);
    }

    [Test]
    public async Task Identifier_quoting_escapes_double_quotes()
    {
        // Defense in depth: validation rejects quotes, but quoting must still be correct.
        await Assert.That(PostgresQueueDialect.Instance.QuoteIdentifier("a\"b")).IsEqualTo("\"a\"\"b\"");
    }

    [Test]
    public async Task Default_names_derive_index_names_within_the_limit()
    {
        var names = SqlQueueRegistration.NamesOf(new PostgresStorageOptions());

        await Assert.That(PostgresQueueDialect.Instance.DerivedIdentifiers(names).All(n => n.Length <= 63)).IsTrue();
    }

    // ------------------------------------------------------------------- data source ---

    [Test]
    public async Task A_data_source_built_from_the_connection_string_is_disposed_with_the_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddQueue(q => q
            .AddRequestAssemblies(typeof(PostgresQueueSchemaTests).Assembly)
            .UsePostgresStorage(o => o.ConnectionString = new PostgresTestDialect().SampleConnectionString));
        var provider = services.BuildServiceProvider();
        var dataSource = (NpgsqlDataSource)provider.GetRequiredService<SqlQueueStorage>().DataSource;

        await provider.DisposeAsync();

        // Opening is where a disposed data source refuses — before it would reach for the network.
        await Assert.That(async () => await dataSource.OpenConnectionAsync()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task A_host_data_source_survives_the_container()
    {
        // The host owns it (and typically shares it with its own data access): the storage must
        // use it as given and leave its lifetime to the host.
        await using var hostOwned = NpgsqlDataSource.Create(new PostgresTestDialect().SampleConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddQueue(q => q
            .AddRequestAssemblies(typeof(PostgresQueueSchemaTests).Assembly)
            .UsePostgresStorage(_ => hostOwned, new ConfigurationBuilder().Build()));

        await using (var provider = services.BuildServiceProvider())
            await Assert.That(provider.GetRequiredService<SqlQueueStorage>().DataSource).IsSameReferenceAs(hostOwned);

        // Not disposed: opening gets as far as the network, where nothing is listening.
        var ex = await Assert.That(async () => await hostOwned.OpenConnectionAsync()).ThrowsException();
        await Assert.That(ex).IsNotTypeOf<ObjectDisposedException>();
    }
}
