using Spinneret.Queue;

namespace Spinneret.Sql.Conformance;

public abstract class SqlQueueSchemaConformance(SqlTestDialect dialect)
{
    protected SqlTestDialect Dialect => dialect;

    private Task<QueueTestHost> StartOn(string suffix, bool worker = false) =>
        QueueTestHost.StartAsync(dialect, worker: worker, suffix: suffix);

    [Test]
    public async Task Re_running_the_script_over_existing_tables_keeps_what_is_in_them()
    {
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
        // The script's lock serializes replicas racing to create missing tables. Sixteen reproduced
        // the race reliably on SQL Server before the lock; fewer makes this test weaker.
        var suffix = QueueTestHost.NewSuffix();

        var hosts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => StartOn(suffix)));

        try
        {
            await Assert.That(hosts).Count().IsEqualTo(16);
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
        // Guarded on the table's existence, the index would never reach an older database.
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
        // CREATE TABLE only runs when the table is absent, so a new column must be added separately.
        var suffix = QueueTestHost.NewSuffix();
        var table = $"DL_{suffix}";
        await using (var existing = await StartOn(suffix))
            await existing.ExecuteAsync($"ALTER TABLE {dialect.Quote(table)} DROP COLUMN TraceId;");

        await using var upgraded = await StartOn(suffix);

        await Assert.That(await dialect.ColumnExists(table, "TraceId")).IsTrue();

        await upgraded.Queue.Enqueue(new PingCommand("after-the-upgrade"));
        await Assert.That(await upgraded.QueueRowCount()).IsEqualTo(1);
    }

    [Test]
    public async Task The_script_is_not_run_at_all_when_the_host_owns_the_schema()
    {
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
        // Postgres folds unquoted names to lower case, so a mismatch would create one table and query another.
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

    [Test]
    public async Task A_table_left_without_its_indexes_and_columns_gets_them_back_on_the_next_start()
    {
        // Guarding each column and index on its own existence is what repairs a database already there.
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
        // The MessageId default fills waiting rows without stranding them or duplicating ids.
        var suffix = QueueTestHost.NewSuffix();
        var table = $"Q_{suffix}";
        await dialect.ExecuteAsync(dialect.LegacyQueueTableSql(table));
        await using (var producer = await StartOn(QueueTestHost.NewSuffix()))
        {
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
        // Without the unique index, SQL Server accepts the duplicate and Postgres fails the enqueue.
        var suffix = QueueTestHost.NewSuffix();
        await dialect.ExecuteAsync(dialect.LegacyQueueTableSql($"Q_{suffix}"));

        await using var host = await StartOn(suffix);
        await host.Queue.Enqueue(new PingCommand("once"), new QueueOptions { DedupeKey = "the-key" });
        await host.Queue.Enqueue(new PingCommand("once"), new QueueOptions { DedupeKey = "the-key" });

        await Assert.That(await host.QueueRowCount()).IsEqualTo(1);
    }
}
