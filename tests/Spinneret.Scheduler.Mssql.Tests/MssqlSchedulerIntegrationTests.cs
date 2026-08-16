using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Scheduler.Mssql.Tests;

/// <summary>
/// End-to-end tests against a real SQL Server (Docker via Testcontainers): registration
/// semantics, the dispatch sweep, transactional one-shots, and failure compensation.
/// </summary>
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlSchedulerIntegrationTests(MssqlContainerFixture fixture)
{
    private const string Stockholm = "Europe/Stockholm";
    private static readonly TimeZoneInfo StockholmZone = TimeZoneInfo.FindSystemTimeZoneById(Stockholm);

    private static readonly Schedule Hourly = Schedule.Cron("0 * * * *", Stockholm);
    private static readonly Schedule EverySecond = Schedule.Cron("* * * * * *", Stockholm);

    /// <summary>A slot no test run can be close to: a fixed daily time roughly half a day away.</summary>
    private static Schedule FarOff()
    {
        var localHour = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, StockholmZone).Hour;
        return Schedule.Cron($"13 {(localHour + 12) % 24} * * *", Stockholm);
    }
    // ---------------------------------------------------------------------- registration ---

    [Test]
    public async Task RegisterAsync_creates_a_job_with_the_first_run_armed()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);

        await host.Scheduler.RegisterAsync("hourly", new TickCommand("h"), Hourly);

        await Assert.That(await host.JobExists("hourly")).IsTrue();
        // An hourly slot lands somewhere in the next hour rather than a fixed distance out.
        var next = await host.JobNextExecuteAt("hourly");
        await Assert.That(next > DateTime.UtcNow).IsTrue();
        await Assert.That(next <= DateTime.UtcNow.AddMinutes(60)).IsTrue();
        await Assert.That(await host.ScalarAsync<string>(
                $"SELECT Schedule FROM [{host.JobsTable}] WHERE JobKey = N'hourly'"))
            .IsEqualTo("cron:Europe/Stockholm:0 * * * *");
    }

    [Test]
    public async Task RegisterAsync_with_unchanged_schedule_keeps_the_cadence()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        var schedule = Hourly;

        await host.Scheduler.RegisterAsync("stable", new TickCommand("v1"), schedule);
        var armed = await host.JobNextExecuteAt("stable");

        await Task.Delay(100);
        await host.Scheduler.RegisterAsync("stable", new TickCommand("v2"), schedule);

        // The definition refreshed but the already-scheduled run did not move.
        await Assert.That(await host.JobNextExecuteAt("stable")).IsEqualTo(armed);
        await Assert.That(await host.ScalarAsync<string>(
                $"SELECT PayloadJson FROM [{host.JobsTable}] WHERE JobKey = N'stable'"))
            .Contains("v2");
    }

    [Test]
    public async Task RegisterAsync_with_a_changed_schedule_rearms_the_job()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);

        await host.Scheduler.RegisterAsync("moving", new TickCommand("m"), Hourly);
        var armed = await host.JobNextExecuteAt("moving");

        var moved = FarOff();
        await host.Scheduler.RegisterAsync("moving", new TickCommand("m"), moved);

        // Re-armed onto the new schedule's next slot. The replacement never falls on the hour, so it
        // cannot coincide with what the hourly schedule had armed.
        var next = await host.JobNextExecuteAt("moving");
        var expected = moved.NextRun(DateTimeOffset.UtcNow).UtcDateTime;
        await Assert.That(next).IsNotEqualTo(armed);
        await Assert.That(Math.Abs((next - expected).TotalSeconds) < 5).IsTrue();
        await Assert.That(await host.ScalarAsync<string>(
                $"SELECT Schedule FROM [{host.JobsTable}] WHERE JobKey = N'moving'"))
            .IsEqualTo(moved.ToString());
    }

    [Test]
    public async Task RegisterAsync_recreates_a_job_that_is_gone()
    {
        // A job that ran, was cancelled, or had its failure dead-lettered no longer exists, so the
        // next registration has to re-create it rather than assume a row is there to refresh.
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        await host.Scheduler.RegisterAsync("revived", new TickCommand("r"), Hourly);
        await host.ExecuteAsync($"DELETE FROM [{host.JobsTable}] WHERE JobKey = N'revived'");

        await host.Scheduler.RegisterAsync("revived", new TickCommand("r"), Hourly);

        await Assert.That(await host.JobExists("revived")).IsTrue();
        await Assert.That(await host.JobNextExecuteAt("revived") > DateTime.UtcNow).IsTrue();
    }

    [Test]
    public async Task RecurringJobInstaller_installs_declared_jobs_at_startup()
    {
        await using var host = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString,
            sweeper: false,
            configure: services => services.AddRecurringJob(
                "declared-job", Schedule.Cron("0 */2 * * *", Stockholm), () => new TickCommand("declared")));

        await Wait.Until(
            async () => await host.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM [{host.JobsTable}] WHERE JobKey = N'declared-job'") == 1,
            "the declared job to be installed");
        await Assert.That(await host.JobExists("declared-job")).IsTrue();
    }

    [Test]
    public async Task RegisterAsync_from_many_instances_at_once_yields_one_job()
    {
        // Ten instances of the same application all assert the same job as they start. JobKey is the
        // primary key and the register-or-refresh holds a key-range lock (UPDLOCK, HOLDLOCK) over
        // it, so the concurrent attempts serialise into one insert and nine in-place refreshes.
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            host.Scheduler.RegisterAsync("contested-parallel", new TickCommand($"instance-{i}"), Hourly)));

        await Assert.That(await host.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM [{host.JobsTable}] WHERE JobKey = N'contested-parallel'"))
            .IsEqualTo(1);
        await Assert.That(await host.JobExists("contested-parallel")).IsTrue();
    }

    [Test]
    public async Task Parallel_installers_converge_on_one_job_per_key()
    {
        // The same thing one level up: ten hosts sharing the tables, each running its own installer.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var hosts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => SchedulerTestHost.StartAsync(
            fixture.ConnectionString,
            sweeper: false,
            configure: services => services.AddRecurringJob(
                "fleet-wide", Schedule.Cron("0 */3 * * *", Stockholm), () => new TickCommand("fleet")),
            reuseSuffix: suffix)));

        try
        {
            await Wait.Until(
                async () => await hosts[0].ScalarAsync<int>(
                    $"SELECT COUNT(*) FROM [{hosts[0].JobsTable}] WHERE JobKey = N'fleet-wide'") == 1,
                "the fleet-wide job to be installed exactly once");
        }
        finally
        {
            foreach (var host in hosts)
                await host.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------------- retiring ---

    [Test]
    public async Task UnregisterAsync_removes_a_recurring_job()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        await host.Scheduler.RegisterAsync("doomed", new TickCommand("d"), Hourly);

        await host.Scheduler.UnregisterAsync("doomed");

        await Assert.That(await host.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM [{host.JobsTable}] WHERE JobKey = N'doomed'"))
            .IsEqualTo(0);
    }

    [Test]
    public async Task UnregisterAsync_for_an_unknown_key_is_a_no_op()
    {
        // A retirement stays declared across deploys, so it re-runs long after the job is gone.
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);

        await host.Scheduler.UnregisterAsync("never-existed");
        await host.Scheduler.UnregisterAsync("never-existed");
    }

    [Test]
    public async Task UnregisterAsync_leaves_a_one_shot_job_untouched()
    {
        // One-shot handles share the JobKey namespace with recurring keys; only a recurring job
        // carries a schedule, and only a recurring job may be removed this way.
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);

        await using var connection = await host.OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var handle = await host.TransactionalScheduler.ScheduleJobAsync(
            transaction, new TickCommand("keep"), DateTimeOffset.UtcNow.AddHours(1));
        await transaction.CommitAsync();

        await host.Scheduler.UnregisterAsync(handle);

        await Assert.That(await host.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM [{host.JobsTable}] WHERE JobKey = N'{handle}'"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task RecurringJobInstaller_retires_declared_keys_at_startup()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await using (var first = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString,
            sweeper: false,
            configure: services => services.AddRecurringJob(
                "seasonal", Schedule.Cron("0 */2 * * *", Stockholm), () => new TickCommand("seasonal")),
            reuseSuffix: suffix))
        {
            await Wait.Until(
                async () => await first.ScalarAsync<int>(
                    $"SELECT COUNT(*) FROM [{first.JobsTable}] WHERE JobKey = N'seasonal'") == 1,
                "the declared job to be installed");
        }

        // The next release drops the job and retires its key against the same tables.
        await using var second = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString,
            sweeper: false,
            configure: services => services.RetireRecurringJob("seasonal"),
            reuseSuffix: suffix);

        await Wait.Until(
            async () => await second.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM [{second.JobsTable}] WHERE JobKey = N'seasonal'") == 0,
            "the retired job to be removed");
    }

    // ------------------------------------------------------------------------- dispatch ---

    [Test]
    public async Task Due_recurring_job_is_enqueued_delivered_and_rearmed()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString);

        // Six fields schedule to the second; with a 100ms sweep the job should tick repeatedly.
        await host.Scheduler.RegisterAsync("ticker", new TickCommand("t"), EverySecond);

        await Wait.Until(() => host.Log.DeliveryCount("tick:t") >= 2, "the recurring job to run at least twice");
        await Assert.That(await host.JobExists("ticker")).IsTrue();
        await Assert.That(await host.DeadLetterCount()).IsEqualTo(0);
    }

    [Test]
    public async Task Due_recurring_job_with_a_non_unit_response_is_enqueued_and_delivered()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString);

        // By the time the sweep enqueues this job it has only the stored type name, so the response
        // type comes from the registry rather than the call site — the path that has to reach the
        // typed IQueue.Enqueue<TResponse> without the caller ever naming string.
        await host.Scheduler.RegisterAsync("reporter", new ReportCommand("r"), EverySecond);

        await Wait.Until(() => host.Log.DeliveryCount("report:r") >= 1, "the non-Unit recurring job to run");
        await Assert.That(await host.JobExists("reporter")).IsTrue();
        await Assert.That(await host.DeadLetterCount()).IsEqualTo(0);
    }

    [Test]
    public async Task Daily_schedule_far_in_the_future_is_not_dispatched()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString);

        await host.Scheduler.RegisterAsync("nightly", new TickCommand("n"), FarOff());

        await Task.Delay(700);
        await Assert.That(host.Log.DeliveryCount("tick:n")).IsEqualTo(0);
        await Assert.That(await host.JobExists("nightly")).IsTrue();
    }

    [Test]
    public async Task Competing_sweeps_dispatch_a_due_job_exactly_once()
    {
        // Two hosts sweep the same tables — the claim's row lock must keep them from
        // double-dispatching the same due slot.
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString);
        await using var rival = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, reuseSuffix: host.Suffix,
            configure: services => services.AddSingleton(host.Log)); // shared delivery log

        // One due slot of a schedule whose next run is half a day out: whichever sweep wins books
        // that run, so seeing two deliveries would prove a double dispatch.
        await host.Scheduler.RegisterAsync("contested", new TickCommand("c"), FarOff());
        await host.ExecuteAsync(
            $"UPDATE [{host.JobsTable}] SET NextExecuteAt = SYSUTCDATETIME() WHERE JobKey = N'contested'");

        await Wait.Until(() => host.Log.DeliveryCount("tick:c") >= 1, "the contested job to run");
        await Task.Delay(700);
        await Assert.That(host.Log.DeliveryCount("tick:c")).IsEqualTo(1);
    }

    // ------------------------------------------------------------- transactional one-shot ---

    [Test]
    public async Task One_shot_in_a_committed_transaction_runs_once_and_is_removed()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString);
        string handle;

        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            handle = await host.TransactionalScheduler.ScheduleJobAsync(
                transaction, new TickCommand("once"), DateTimeOffset.UtcNow);
            await transaction.CommitAsync();
        }

        await Wait.Until(() => host.Log.DeliveryCount("tick:once") == 1, "the one-shot to run");
        await Wait.Until(async () => !await host.JobExists(handle), "the one-shot row to be removed");
        await Task.Delay(500);
        await Assert.That(host.Log.DeliveryCount("tick:once")).IsEqualTo(1);
    }

    [Test]
    public async Task One_shot_in_a_rolled_back_transaction_never_runs()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString);

        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            await host.TransactionalScheduler.ScheduleJobAsync(
                transaction, new TickCommand("phantom"), DateTimeOffset.UtcNow);
            await transaction.RollbackAsync();
        }

        await Task.Delay(700);
        await Assert.That(host.Log.DeliveryCount("tick:phantom")).IsEqualTo(0);
        await Assert.That(await host.ScalarAsync<int>($"SELECT COUNT(*) FROM [{host.JobsTable}]")).IsEqualTo(0);
    }

    [Test]
    public async Task Cancelled_one_shot_never_runs()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString);
        string handle;

        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            handle = await host.TransactionalScheduler.ScheduleJobAsync(
                transaction, new TickCommand("cancelled"),
                DateTimeOffset.UtcNow.AddSeconds(2));
            await host.TransactionalScheduler.CancelJobAsync(transaction, handle);
            await transaction.CommitAsync();
        }

        await Task.Delay(3000);
        await Assert.That(host.Log.DeliveryCount("tick:cancelled")).IsEqualTo(0);
        await Assert.That(await host.JobExists(handle)).IsFalse();
    }

    [Test]
    public async Task One_shot_handles_are_prefixed_so_they_cannot_be_mistaken_for_a_recurring_key()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);

        await using var connection = await host.OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var handle = await host.TransactionalScheduler.ScheduleJobAsync(
            transaction, new TickCommand("prefixed"), DateTimeOffset.UtcNow.AddHours(1));
        await transaction.RollbackAsync();

        await Assert.That(handle).StartsWith("oneshot-");
    }

    [Test]
    public async Task Cancelling_with_a_recurring_key_is_rejected_and_leaves_the_schedule_intact()
    {
        // Cancel deletes, so without the guard a caller reaching for the wrong retirement method
        // would silently destroy a live schedule. UnregisterAsync is the one that retires these.
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        await host.Scheduler.RegisterAsync("nightly-cleanup", new TickCommand("n"), FarOff());

        await using var connection = await host.OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await Assert.That(async () =>
                await host.TransactionalScheduler.CancelJobAsync(transaction, "nightly-cleanup"))
            .Throws<ArgumentException>();
        await transaction.RollbackAsync();

        await Assert.That(await host.JobExists("nightly-cleanup")).IsTrue();
    }

    // ------------------------------------------------------------------- failure paths ---

    [Test]
    public async Task Failed_recurring_dispatch_dead_letters_the_occurrence_but_keeps_the_schedule()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        await host.Scheduler.RegisterAsync("broken", new TickCommand("b"), EverySecond);
        // Sabotage the persisted type name so the sweep cannot resolve the request.
        await host.ExecuteAsync(
            $"UPDATE [{host.JobsTable}] SET RequestTypeName = N'No.Such.Type', NextExecuteAt = SYSUTCDATETIME() " +
            $"WHERE JobKey = N'broken'");

        await using var sweeping = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, reuseSuffix: host.Suffix);

        await Wait.Until(async () => await host.DeadLetterCount() >= 1, "the occurrence to be dead-lettered");
        await Assert.That(await host.JobExists("broken")).IsTrue();
        var source = await host.ScalarAsync<string>(
            $"SELECT TOP(1) Source FROM [{host.QueueOptions.DeadLetterTableName}]");
        await Assert.That(source).IsEqualTo("Scheduler");
    }

    [Test]
    public async Task Unreadable_schedule_is_quarantined_without_blocking_other_jobs()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        await host.Scheduler.RegisterAsync("poison", new TickCommand("p"), EverySecond);
        await host.Scheduler.RegisterAsync("healthy", new TickCommand("h"), EverySecond);
        // Corrupt the poison job's schedule and make it the oldest-due row, so a sweep that fails
        // on it would starve everything behind it.
        await host.ExecuteAsync(
            $"UPDATE [{host.JobsTable}] SET Schedule = N'cron:Mars/Olympus:0 3 * * *', " +
            "NextExecuteAt = DATEADD(minute, -10, SYSUTCDATETIME()) WHERE JobKey = N'poison'");
        await host.ExecuteAsync(
            $"UPDATE [{host.JobsTable}] SET NextExecuteAt = SYSUTCDATETIME() WHERE JobKey = N'healthy'");

        await using var sweeping = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, reuseSuffix: host.Suffix,
            configure: services => services.AddSingleton(host.Log));

        // The healthy job must still dispatch, and the poison one is quarantined: dead-lettered,
        // kept, and pushed out of the sweep's way.
        await Wait.Until(() => host.Log.DeliveryCount("tick:h") >= 1, "the healthy job to dispatch");
        await Wait.Until(async () => await host.DeadLetterCount() >= 1, "the poison job to be dead-lettered");
        await Assert.That(await host.JobExists("poison")).IsTrue();
        var next = await host.JobNextExecuteAt("poison");
        await Assert.That(next > DateTime.UtcNow.AddMinutes(3)).IsTrue();
    }

    [Test]
    public async Task Failed_one_shot_dispatch_is_removed_with_a_dead_letter()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        string handle;
        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            handle = await host.TransactionalScheduler.ScheduleJobAsync(
                transaction, new TickCommand("bad"), DateTimeOffset.UtcNow);
            await transaction.CommitAsync();
        }
        await host.ExecuteAsync(
            $"UPDATE [{host.JobsTable}] SET RequestTypeName = N'No.Such.Type' WHERE JobKey = N'{handle}'");

        await using var sweeping = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, reuseSuffix: host.Suffix);

        await Wait.Until(async () => !await host.JobExists(handle), "the failed one-shot row to be removed");
        await Assert.That(await host.DeadLetterCount()).IsEqualTo(1);
    }
}
