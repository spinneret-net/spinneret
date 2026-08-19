using Microsoft.Extensions.DependencyInjection;
using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql.Tests;

/// <summary>
/// The scheduler's startup schema initializer actually executed against a real SQL Server, rather
/// than its generated SQL inspected as a string.
/// </summary>
/// <remarks>
/// The scheduler has no schema switch of its own: it is gated by the queue's <c>CreateSchema</c>,
/// so one schema-ownership decision covers both packages. That coupling is only visible at runtime,
/// which is what the last test here pins.
/// </remarks>
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlSchedulerSchemaInitializerTests(MssqlContainerFixture fixture)
{
    private const string Stockholm = "Europe/Stockholm";
    private static readonly Schedule Hourly = Schedule.Cron("0 * * * *", Stockholm);

    [Test]
    public async Task Re_running_the_script_over_an_existing_table_keeps_the_jobs_in_it()
    {
        // Every host runs the script at startup, so redeploying must not drop the installed
        // schedules along with their cadence.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await using var first = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, sweeper: false, reuseSuffix: suffix);
        await first.Scheduler.RegisterAsync("survivor", new TickCommand("s"), Hourly);
        var armed = await first.JobNextExecuteAt("survivor");

        await using var second = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, sweeper: false, reuseSuffix: suffix);

        await Assert.That(await second.JobExists("survivor")).IsTrue();
        await Assert.That(await second.JobNextExecuteAt("survivor")).IsEqualTo(armed);
    }

    [Test]
    public async Task Many_hosts_initializing_the_same_table_at_once_all_start()
    {
        // Same non-atomic "IF OBJECT_ID(...) IS NULL CREATE TABLE" as the queue's initializer, and
        // serialized the same way, by the sp_getapplock the scheduler's own script takes. See the
        // queue suite's equivalent for the measurements behind the replica count.
        var suffix = Guid.NewGuid().ToString("N")[..12];

        var hosts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false, reuseSuffix: suffix)));

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
        // What the create race could leave behind before the script took a lock. The sweep selects
        // on NextExecuteAt, so a jobs table stuck without this index scans every job on every tick.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await using var seed = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, sweeper: false, reuseSuffix: Guid.NewGuid().ToString("N")[..12]);
        await seed.ExecuteAsync($"""
            CREATE TABLE [Jobs_{suffix}] (
                JobKey NVARCHAR(200) NOT NULL CONSTRAINT [PK_Jobs_{suffix}] PRIMARY KEY,
                RequestTypeName NVARCHAR(500) NOT NULL,
                PayloadJson NVARCHAR(MAX) NOT NULL,
                Schedule NVARCHAR(500) NULL,
                NextExecuteAt DATETIME2(3) NOT NULL,
                CreatedAt DATETIME2(3) NOT NULL,
                LastRunAt DATETIME2(3) NULL);
            """);

        await using var host = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, sweeper: false, reuseSuffix: suffix);

        await Assert.That(await IndexExists(host, $"Jobs_{suffix}", $"IX_Jobs_{suffix}_NextExecuteAt")).IsTrue();
    }

    [Test]
    public async Task The_queues_schema_switch_governs_the_scheduler_table_too()
    {
        // A host that owns its schema through migrations turns off one switch and expects neither
        // package to create anything. Creating the jobs table anyway would be a silent surprise.
        var suffix = Guid.NewGuid().ToString("N")[..12];

        await using var host = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString,
            sweeper: false,
            configure: services => services.Configure<MssqlQueueOptions>(
                o => o.CreateSchema = false),
            reuseSuffix: suffix);

        await Assert.That(await TableExists(host, $"Jobs_{suffix}")).IsFalse();
        await Assert.That(await TableExists(host, $"Q_{suffix}")).IsFalse();
    }

    private static async Task<bool> IndexExists(SchedulerTestHost host, string table, string index) =>
        await host.ScalarAsync<int>($"""
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'[{table}]', N'U') AND name = N'{index}'
            """) == 1;

    private static async Task<bool> TableExists(SchedulerTestHost host, string table) =>
        await host.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.tables WHERE name = N'{table}'") == 1;
}
