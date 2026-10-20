using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Composition.Tests;

/// <summary>
/// The SQL scheduler feeding Cloud Tasks: a due job's booking and its outbox insert commit together,
/// and the relay takes it from there — the combination the SQL scheduler could not join before the
/// queue had an outbox.
/// </summary>
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class SchedulerCompositionTests(MssqlContainerFixture fixture)
{
    [Test]
    public async Task A_due_sql_job_is_forwarded_once_through_the_outbox()
    {
        await using var host = await CloudTasksOverSqlHost.StartAsync(
            fixture.ConnectionString,
            configure: (services, configuration) => services.AddScheduler(s => s.UseMssql(configuration)));
        await WaitForSchedulerTable(host);

        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            await host.Services.GetRequiredService<ISqlTransactionalScheduler>()
                .ScheduleJobAsync(transaction, new PingCommand("scheduled"), host.Clock.Now.AddMinutes(-1));
            await transaction.CommitAsync();
        }

        var sweep = host.Services.GetRequiredService<ISchedulerSweep>();
        var first = await sweep.SweepAsync(CancellationToken.None);
        var second = await sweep.SweepAsync(CancellationToken.None);

        await Assert.That(first.JobsDispatched).IsEqualTo(1);
        await Assert.That(second.JobsDispatched).IsEqualTo(0);
        // Forwarded as the sweep's unit of work committed.
        await Assert.That(host.Tasks.Requests).Count().IsEqualTo(1);
        await Assert.That(CloudTasksOverSqlHost.EnvelopeOf(host.Tasks.SingleRequest).RequestTypeName)
            .IsEqualTo(typeof(PingCommand).FullName!);
        await Assert.That(await host.OutboxCount()).IsEqualTo(0);
    }

    /// <summary>
    /// The scheduler's table is created by its hosted initializer, which the web host started along
    /// with everything else; this only waits for it to be there.
    /// </summary>
    private static Task WaitForSchedulerTable(CloudTasksOverSqlHost host) =>
        Wait.Until(async () => await host.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM sys.tables WHERE name = 'Jobs_{host.Suffix}'") == 1,
            "the scheduler table to exist");
}
