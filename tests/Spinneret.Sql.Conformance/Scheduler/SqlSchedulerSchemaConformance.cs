using Spinneret.Scheduler;

namespace Spinneret.Sql.Conformance;

// The scheduler has no schema switch of its own: the queue's CreateSchema gates both.
public abstract class SqlSchedulerSchemaConformance(SqlTestDialect dialect)
{
    private const string Stockholm = "Europe/Stockholm";
    private static readonly Schedule Hourly = Schedule.Cron("0 * * * *", Stockholm);

    protected SqlTestDialect Dialect => dialect;

    [Test]
    public async Task Re_running_the_script_over_an_existing_table_keeps_the_jobs_in_it()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await using var first = await SchedulerTestHost.StartAsync(dialect, sweeper: false, reuseSuffix: suffix);
        await first.Scheduler.RegisterAsync("survivor", new TickCommand("s"), Hourly);
        var armed = await first.JobNextExecuteAt("survivor");

        await using var second = await SchedulerTestHost.StartAsync(dialect, sweeper: false, reuseSuffix: suffix);

        await Assert.That(await second.JobExists("survivor")).IsTrue();
        await Assert.That(await second.JobNextExecuteAt("survivor")).IsEqualTo(armed);
    }

    [Test]
    public async Task Many_hosts_initializing_the_same_table_at_once_all_start()
    {
        // Serialized by the script's lock; see the queue suite for the replica count.
        var suffix = Guid.NewGuid().ToString("N")[..12];

        var hosts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            SchedulerTestHost.StartAsync(dialect, sweeper: false, reuseSuffix: suffix)));

        try
        {
            await Assert.That(hosts).Count().IsEqualTo(16);
            await hosts[0].Scheduler.RegisterAsync("after-the-race", new TickCommand("a"), Hourly);
            await Assert.That(await hosts[15].JobExists("after-the-race")).IsTrue();
        }
        finally
        {
            foreach (var host in hosts)
                await host.DisposeAsync();
        }
    }

    [Test]
    public async Task A_table_left_without_its_due_index_gets_it_back_on_the_next_start()
    {
        // The sweep selects on NextExecuteAt, so without this index every tick scans every job.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var table = $"Jobs_{suffix}";
        await dialect.ExecuteAsync(dialect.LegacyJobsTableSql(table));

        await using var host = await SchedulerTestHost.StartAsync(dialect, sweeper: false, reuseSuffix: suffix);

        await Assert.That(await dialect.IndexExists(table, dialect.SchedulerDueIndex(table))).IsTrue();
    }

    [Test]
    public async Task The_queues_schema_switch_governs_the_scheduler_table_too()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];

        await using var host = await SchedulerTestHost.StartAsync(
            dialect,
            sweeper: false,
            configure: services => dialect.ConfigureStorage(services, o => o.CreateSchema = false),
            reuseSuffix: suffix);

        await Assert.That(await dialect.TableExists($"Jobs_{suffix}")).IsFalse();
        await Assert.That(await dialect.TableExists($"Q_{suffix}")).IsFalse();
    }
}
