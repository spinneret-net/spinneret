using Spinneret.Queue;

namespace Spinneret.Sql.Conformance;

/// <summary>
/// The startup schema initializer actually executed against a real database, rather than its
/// generated SQL inspected as a string.
/// </summary>
/// <remarks>
/// Each dialect's own suite asserts what its script says; this asserts what running it does — that
/// it is safe on a database that already has the tables, that a fleet of replicas booting together
/// all come up, and that an index or column added to the script after a database was created still
/// reaches it.
/// </remarks>
public abstract class SqlQueueSchemaConformance(SqlTestDialect dialect)
{
    protected SqlTestDialect Dialect => dialect;

    private Task<QueueTestHost> StartOn(string suffix, bool worker = false) =>
        QueueTestHost.StartAsync(dialect, worker: worker, suffix: suffix);

    [Test]
    public async Task Re_running_the_script_over_existing_tables_keeps_what_is_in_them()
    {
        // Every host runs the script at startup, so the second deploy of an application must not
        // wipe the queue it inherits.
        var suffix = QueueTestHost.NewSuffix();
        await using var first = await StartOn(suffix);
        await first.Queue.Enqueue(new PingCommand("survivor"));
        await Assert.That(await first.QueueRowCount()).IsEqualTo(1);

        await using var second = await StartOn(suffix);

        await Assert.That(await second.QueueRowCount()).IsEqualTo(1);
    }

    [Test]
    public async Task Many_hosts_initializing_the_same_tables_at_once_all_start()
    {
        // A fleet scaling up from zero boots every replica at the same moment, and "create the
        // table if it is missing" is not atomic — two replicas can both find it missing. The
        // script's lock (sp_getapplock, pg_advisory_xact_lock) is what serializes them.
        //
        // Sixteen, because that is what it took to provoke the race reliably on SQL Server while
        // diagnosing it: before the lock, and with the initializer's retry removed so nothing masked
        // the failure, sixteen replicas surfaced it on roughly three runs in four and eight on one
        // in three. Lowering the count makes this test quietly weaker.
        var suffix = QueueTestHost.NewSuffix();

        var hosts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => StartOn(suffix)));

        try
        {
            await Assert.That(hosts).Count().IsEqualTo(16);
            // One set of tables, shared, and usable from every one of them.
            await hosts[0].Queue.Enqueue(new PingCommand("after-the-race"));
            await Assert.That(await hosts[15].QueueRowCount()).IsEqualTo(1);
        }
        finally
        {
            foreach (var host in hosts)
                await host.DisposeAsync();
        }
    }

    [Test]
    public async Task The_dead_letter_paging_index_reaches_a_database_that_predates_it()
    {
        // Guarded by the table's own existence the index would never reach a database created
        // before the index was added to the script, and the admin page would silently fall back to
        // a scan.
        var suffix = QueueTestHost.NewSuffix();
        var table = $"DL_{suffix}";
        var index = dialect.DeadLetterOrderIndex(table);
        await using (var existing = await StartOn(suffix))
            await existing.ExecuteAsync(dialect.DropIndexSql(table, index));

        await using var upgraded = await StartOn(suffix);

        await Assert.That(await dialect.IndexExists(table, index)).IsTrue();
    }

    [Test]
    public async Task The_trace_id_column_reaches_a_database_that_predates_it()
    {
        // The hazard a new column walks straight into: the CREATE TABLE only runs when the table is
        // absent, so a column added to it would never reach an existing database and every dead
        // letter written there would fail to insert.
        var suffix = QueueTestHost.NewSuffix();
        var table = $"DL_{suffix}";
        await using (var existing = await StartOn(suffix))
            await existing.ExecuteAsync($"ALTER TABLE {dialect.Quote(table)} DROP COLUMN TraceId;");

        await using var upgraded = await StartOn(suffix);

        await Assert.That(await dialect.ColumnExists(table, "TraceId")).IsTrue();

        // And the repair leaves a usable table, not merely a column-shaped one.
        await upgraded.Queue.Enqueue(new PingCommand("after-the-upgrade"));
        await Assert.That(await upgraded.QueueRowCount()).IsEqualTo(1);
    }

    [Test]
    public async Task The_script_is_not_run_at_all_when_the_host_owns_the_schema()
    {
        // Hosts that manage their schema through migrations turn this off; leaving tables uncreated
        // is the whole point, so it must not quietly create them anyway.
        var suffix = QueueTestHost.NewSuffix();

        await using var host = await QueueTestHost.StartAsync(
            dialect, worker: false, suffix: suffix,
            extraConfig: new() { [$"{dialect.StorageSection}:CreateSchema"] = "false" });

        await Assert.That(await dialect.TableExists($"Q_{suffix}")).IsFalse();
        await Assert.That(await dialect.TableExists($"DL_{suffix}")).IsFalse();
    }

    [Test]
    public async Task Mixed_case_table_names_are_created_and_used_as_configured()
    {
        // Configured names are quoted everywhere, so they keep their case — on Postgres, where an
        // unquoted name would fold to lower case, a mismatch would create one table and query another.
        var suffix = QueueTestHost.NewSuffix();
        await using var host = await QueueTestHost.StartAsync(
            dialect, worker: false, suffix: suffix,
            extraConfig: new()
            {
                [$"{dialect.StorageSection}:QueueTableName"] = $"MixedCase_Queue_{suffix}",
                [$"{dialect.StorageSection}:DeadLetterTableName"] = $"MixedCase_Dead_{suffix}",
            });

        await host.Queue.Enqueue(new PingCommand("cased"));

        await Assert.That(await dialect.TableExists($"MixedCase_Queue_{suffix}")).IsTrue();
        await Assert.That(await dialect.CountAsync($"MixedCase_Queue_{suffix}")).IsEqualTo(1);
    }

    // ------------------------------------------------------ repairing a torn-open schema ---

    [Test]
    public async Task A_table_left_without_its_indexes_and_columns_gets_them_back_on_the_next_start()
    {
        // The state an interrupted or older script could leave behind: the table, without the
        // columns and indexes added since. Guarding each on the table's existence would make that
        // permanent; guarding each on its own existence is what repairs a database already there.
        var suffix = QueueTestHost.NewSuffix();
        var table = $"Q_{suffix}";
        await dialect.ExecuteAsync(dialect.LegacyQueueTableSql(table));

        await using var host = await StartOn(suffix);

        await Assert.That(await dialect.IndexExists(table, dialect.DedupeIndex(table))).IsTrue();
        await Assert.That(await dialect.IndexExists(table, dialect.ChannelIndex(table))).IsTrue();
        await Assert.That(await dialect.ColumnExists(table, "MessageId")).IsTrue();
        await Assert.That(await dialect.ColumnExists(table, "LeasedUntil")).IsTrue();
    }

    [Test]
    public async Task Rows_waiting_in_a_table_that_predates_the_message_id_keep_their_place_and_are_delivered()
    {
        // The MessageId column is added with a default that fills the rows already waiting, so an
        // upgrade neither strands them nor gives two rows the same id.
        var suffix = QueueTestHost.NewSuffix();
        var table = $"Q_{suffix}";
        await dialect.ExecuteAsync(dialect.LegacyQueueTableSql(table));
        await using (var producer = await StartOn(QueueTestHost.NewSuffix()))
        {
            // An envelope as the current version writes it, moved into the legacy table by hand.
            await producer.Queue.Enqueue(new PingCommand("waiting"));
            await dialect.ExecuteAsync(
                $"INSERT INTO {dialect.Quote(table)} (Channel, VisibleAt, Envelope) "
                + $"SELECT Channel, VisibleAt, Envelope FROM {dialect.Quote(producer.QueueTable)};");
        }

        await using var host = await StartOn(suffix, worker: true);

        await Wait.Until(() => host.Log.DeliveryCount("ping:waiting") == 1, "the waiting message to be delivered");
    }

    [Test]
    public async Task Dedupe_still_works_on_a_queue_whose_index_was_repaired()
    {
        // Why the previous tests matter: a queue missing the unique index does not merely run
        // slower — on SQL Server it accepts the duplicate and reports success, on Postgres the
        // enqueue fails outright. Either way deduplication stops working.
        var suffix = QueueTestHost.NewSuffix();
        await dialect.ExecuteAsync(dialect.LegacyQueueTableSql($"Q_{suffix}"));

        await using var host = await StartOn(suffix);
        await host.Queue.Enqueue(new PingCommand("once"), new QueueOptions { DedupeKey = "the-key" });
        await host.Queue.Enqueue(new PingCommand("once"), new QueueOptions { DedupeKey = "the-key" });

        await Assert.That(await host.QueueRowCount()).IsEqualTo(1);
    }
}
