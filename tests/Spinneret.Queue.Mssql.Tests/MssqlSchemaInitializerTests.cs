namespace Spinneret.Queue.Mssql.Tests;

/// <summary>
/// The startup schema initializer actually executed against a real SQL Server, rather than its
/// generated SQL inspected as a string.
/// </summary>
/// <remarks>
/// <see cref="MssqlQueueSchemaTests"/> asserts what the script says; this asserts what running it
/// does — that it is safe on a database that already has the tables, that a fleet of replicas
/// booting together all come up, and that an index added to the script after a database was created
/// still reaches it.
/// </remarks>
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlSchemaInitializerTests(MssqlContainerFixture fixture)
{
    /// <summary>Config pinning a host to a given pair of tables, so several hosts can share them.</summary>
    private static Dictionary<string, string?> Tables(string suffix) => new()
    {
        ["Queue:Mssql:QueueTableName"] = $"Q_{suffix}",
        ["Queue:Mssql:DeadLetterTableName"] = $"DL_{suffix}",
    };

    private static Task<QueueTestHost> StartOn(string connectionString, string suffix, bool worker = false) =>
        QueueTestHost.StartAsync(connectionString, worker: worker, extraConfig: Tables(suffix));

    [Test]
    public async Task Re_running_the_script_over_existing_tables_keeps_what_is_in_them()
    {
        // Every host runs the script at startup, so the second deploy of an application must not
        // wipe the queue it inherits.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await using var first = await StartOn(fixture.ConnectionString, suffix);
        await first.Queue.Enqueue(new PingCommand("survivor"));
        await Assert.That(await first.QueueRowCount()).IsEqualTo(1);

        await using var second = await StartOn(fixture.ConnectionString, suffix);

        await Assert.That(await second.QueueRowCount()).IsEqualTo(1);
    }

    [Test]
    public async Task Many_hosts_initializing_the_same_tables_at_once_all_start()
    {
        // A fleet scaling up from zero boots every replica at the same moment, and
        // "IF OBJECT_ID(...) IS NULL CREATE TABLE" is not atomic — two replicas can both find the
        // table missing. The script's sp_getapplock is what serializes them.
        //
        // Sixteen, because that is what it took to provoke the race reliably while diagnosing it:
        // before the lock, and with the initializer's retry removed so nothing masked the failure,
        // sixteen replicas surfaced it on roughly three runs in four and eight on one in three.
        // With the lock it passes even with the retry removed, which is what makes the fix a fix
        // rather than a wider retry window. Lowering the count makes this test quietly weaker.
        var suffix = Guid.NewGuid().ToString("N")[..12];

        var hosts = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => StartOn(fixture.ConnectionString, suffix)));

        try
        {
            await Assert.That(hosts).HasCount(16);
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
        // The index is created outside the table block on purpose: guarded by the table's own
        // existence it would never reach a database created before the index was added to the
        // script, and the admin page would silently fall back to a scan.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var indexName = $"IX_DL_{suffix}_DeadLetteredAt";
        await using (var existing = await StartOn(fixture.ConnectionString, suffix))
            await existing.ExecuteAsync($"DROP INDEX [{indexName}] ON [DL_{suffix}];");

        await using var upgraded = await StartOn(fixture.ConnectionString, suffix);

        await Assert.That(await IndexExists(upgraded, $"DL_{suffix}", indexName)).IsTrue();
    }

    [Test]
    public async Task The_script_is_not_run_at_all_when_the_host_owns_the_schema()
    {
        // Hosts that manage their schema through migrations turn this off; leaving tables uncreated
        // is the whole point, so it must not quietly create them anyway.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var config = Tables(suffix);
        config["Queue:Mssql:CreateSchema"] = "false";

        await using var host = await QueueTestHost.StartAsync(
            fixture.ConnectionString, worker: false, extraConfig: config);

        await Assert.That(await TableExists(host, $"Q_{suffix}")).IsFalse();
        await Assert.That(await TableExists(host, $"DL_{suffix}")).IsFalse();
    }

    private static async Task<bool> TableExists(QueueTestHost host, string table) =>
        await host.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM sys.tables WHERE name = N'{table}'") == 1;

    private static async Task<bool> IndexExists(QueueTestHost host, string table, string index) =>
        await host.ScalarAsync<int>($"""
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'[{table}]', N'U') AND name = N'{index}'
            """) == 1;

    // ------------------------------------------------------ repairing a torn-open schema ---

    [Test]
    public async Task A_table_left_without_its_indexes_gets_them_back_on_the_next_start()
    {
        // The state the create race could leave behind before the script took a lock: CREATE TABLE
        // committed, and a rival that read OBJECT_ID in the gap before the CREATE INDEX statements
        // skipped them as already done. Guarding each index on the table's existence made that
        // permanent — the table is there, so the block never runs again. Guarding each index on its
        // own existence is what repairs a database already in that state.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await using var seed = await StartOn(fixture.ConnectionString, Guid.NewGuid().ToString("N")[..12]);
        await seed.ExecuteAsync($"""
            CREATE TABLE [Q_{suffix}] (
                Id BIGINT IDENTITY NOT NULL CONSTRAINT [PK_Q_{suffix}] PRIMARY KEY,
                Channel NVARCHAR(100) NOT NULL,
                VisibleAt DATETIME2(3) NOT NULL,
                DedupeKey NVARCHAR(200) NULL,
                Envelope NVARCHAR(MAX) NOT NULL);
            """);

        await using var host = await StartOn(fixture.ConnectionString, suffix);

        await Assert.That(await IndexExists(host, $"Q_{suffix}", $"UX_Q_{suffix}_DedupeKey")).IsTrue();
        await Assert.That(await IndexExists(host, $"Q_{suffix}", $"IX_Q_{suffix}_Channel_VisibleAt")).IsTrue();
    }

    [Test]
    public async Task Dedupe_still_works_on_a_queue_whose_index_was_repaired()
    {
        // Why the previous test matters. Enqueue detects a duplicate dedupe key by catching the
        // unique index's violation, so a queue missing that index does not merely run slower — it
        // accepts the duplicate and reports success, and the caller never learns deduplication
        // stopped happening.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await using var seed = await StartOn(fixture.ConnectionString, Guid.NewGuid().ToString("N")[..12]);
        await seed.ExecuteAsync($"""
            CREATE TABLE [Q_{suffix}] (
                Id BIGINT IDENTITY NOT NULL CONSTRAINT [PK_Q_{suffix}] PRIMARY KEY,
                Channel NVARCHAR(100) NOT NULL,
                VisibleAt DATETIME2(3) NOT NULL,
                DedupeKey NVARCHAR(200) NULL,
                Envelope NVARCHAR(MAX) NOT NULL);
            """);

        await using var host = await StartOn(fixture.ConnectionString, suffix);
        await host.Queue.Enqueue(new PingCommand("once"), new QueueOptions { DedupeKey = "the-key" });
        await host.Queue.Enqueue(new PingCommand("once"), new QueueOptions { DedupeKey = "the-key" });

        await Assert.That(await host.QueueRowCount()).IsEqualTo(1);
    }
}
