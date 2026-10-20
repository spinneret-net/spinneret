using Spinneret.Scheduler;

namespace Spinneret.Sql.Conformance;

/// <summary>
/// The scheduler's startup schema initializer actually executed against a real database, rather
/// than its generated SQL inspected as a string.
/// </summary>
/// <remarks>
/// The scheduler has no schema switch of its own: it is gated by the queue's <c>CreateSchema</c>,
/// so one schema-ownership decision covers both packages. That coupling is only visible at runtime,
/// which is what the last test here pins.
/// </remarks>
public abstract class SqlSchedulerSchemaConformance(SqlTestDialect dialect)
{
    private const string Stockholm = "Europe/Stockholm";
    private static readonly Schedule Hourly = Schedule.Cron("0 * * * *", Stockholm);

    protected SqlTestDialect Dialect => dialect;

    [Test]
    public async Task Re_running_the_script_over_an_existing_table_keeps_the_jobs_in_it()
    {
        // Every host runs the script at startup, so redeploying must not drop the installed
        // schedules along with their cadence.
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
        // The same non-atomic "create the table if it is missing" as the queue's initializer, and
        // serialized the same way, by the lock the scheduler's own script takes. See the queue
        // suite's equivalent for the measurements behind the replica count.
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
        // The sweep selects on NextExecuteAt, so a jobs table stuck without this index scans every
        // job on every tick.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var table = $"Jobs_{suffix}";
        await dialect.ExecuteAsync(dialect.LegacyJobsTableSql(table));

        await using var host = await SchedulerTestHost.StartAsync(dialect, sweeper: false, reuseSuffix: suffix);

        await Assert.That(await dialect.IndexExists(table, dialect.SchedulerDueIndex(table))).IsTrue();
    }

    [Test]
    public async Task The_queues_schema_switch_governs_the_scheduler_table_too()
    {
        // A host that owns its schema through migrations turns off one switch and expects neither
        // package to create anything. Creating the jobs table anyway would be a silent surprise.
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
