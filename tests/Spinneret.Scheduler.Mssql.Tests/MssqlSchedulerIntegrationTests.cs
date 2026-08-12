using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Spinneret.Scheduler.Mssql.Tests;

/// <summary>
/// End-to-end tests against a real SQL Server (Docker via Testcontainers): registration
/// semantics, the dispatch sweep, transactional one-shots, and failure compensation.
/// </summary>
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlSchedulerIntegrationTests(MssqlContainerFixture fixture)
{
    // ---------------------------------------------------------------------- registration ---

    [Test]
    public async Task RegisterAsync_creates_a_pending_job_with_the_first_run_armed()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);

        await host.Scheduler.RegisterAsync("hourly", new TickCommand("h"), Schedule.Every(Duration.FromHours(1)));

        await Assert.That(await host.JobStatus("hourly")).IsEqualTo("pending");
        var next = await host.JobNextExecuteAt("hourly");
        await Assert.That(next > DateTime.UtcNow.AddMinutes(55)).IsTrue();
        await Assert.That(next < DateTime.UtcNow.AddMinutes(65)).IsTrue();
        await Assert.That(await host.ScalarAsync<string>(
                $"SELECT Schedule FROM [{host.JobsTable}] WHERE JobKey = N'hourly'"))
            .IsEqualTo("every:0:01:00:00");
    }

    [Test]
    public async Task RegisterAsync_with_unchanged_schedule_keeps_the_cadence()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        var schedule = Schedule.Every(Duration.FromHours(1));

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

        await host.Scheduler.RegisterAsync("moving", new TickCommand("m"), Schedule.Every(Duration.FromHours(1)));
        await host.Scheduler.RegisterAsync("moving", new TickCommand("m"), Schedule.Every(Duration.FromHours(6)));

        var next = await host.JobNextExecuteAt("moving");
        await Assert.That(next > DateTime.UtcNow.AddHours(5)).IsTrue();
        await Assert.That(await host.ScalarAsync<string>(
                $"SELECT Schedule FROM [{host.JobsTable}] WHERE JobKey = N'moving'"))
            .IsEqualTo("every:0:06:00:00");
    }

    [Test]
    public async Task RegisterAsync_rearms_a_terminal_job()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        await host.Scheduler.RegisterAsync("revived", new TickCommand("r"), Schedule.Every(Duration.FromHours(1)));
        await host.ExecuteAsync($"UPDATE [{host.JobsTable}] SET Status = N'cancelled' WHERE JobKey = N'revived'");

        await host.Scheduler.RegisterAsync("revived", new TickCommand("r"), Schedule.Every(Duration.FromHours(1)));

        await Assert.That(await host.JobStatus("revived")).IsEqualTo("pending");
    }

    [Test]
    public async Task RecurringJobInstaller_installs_declared_jobs_at_startup()
    {
        await using var host = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString,
            sweeper: false,
            configure: services => services.AddRecurringJob(
                "declared-job", Schedule.Every(Duration.FromHours(2)), () => new TickCommand("declared")));

        await Wait.Until(
            async () => await host.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM [{host.JobsTable}] WHERE JobKey = N'declared-job'") == 1,
            "the declared job to be installed");
        await Assert.That(await host.JobStatus("declared-job")).IsEqualTo("pending");
    }

    // ------------------------------------------------------------------------- dispatch ---

    [Test]
    public async Task Due_recurring_job_is_enqueued_delivered_and_rearmed()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString);

        // Minimum interval is 1s; with a 100ms sweep the job should tick repeatedly.
        await host.Scheduler.RegisterAsync("ticker", new TickCommand("t"), Schedule.Every(Duration.FromSeconds(1)));

        await Wait.Until(() => host.Log.DeliveryCount("tick:t") >= 2, "the recurring job to run at least twice");
        await Assert.That(await host.JobStatus("ticker")).IsEqualTo("pending");
        await Assert.That(await host.DeadLetterCount()).IsEqualTo(0);
    }

    [Test]
    public async Task Daily_schedule_far_in_the_future_is_not_dispatched()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString);
        var stockholm = DateTimeZoneProviders.Tzdb["Europe/Stockholm"];

        await host.Scheduler.RegisterAsync(
            "nightly", new TickCommand("n"), Schedule.Daily(stockholm, new LocalTime(1, 0)));

        await Task.Delay(700);
        await Assert.That(host.Log.DeliveryCount("tick:n")).IsEqualTo(0);
        await Assert.That(await host.JobStatus("nightly")).IsEqualTo("pending");
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

        // One due slot of a very long interval: whichever sweep wins books the next run an hour
        // out, so seeing two deliveries would prove a double dispatch.
        await host.Scheduler.RegisterAsync("contested", new TickCommand("c"), Schedule.Every(Duration.FromHours(1)));
        await host.ExecuteAsync(
            $"UPDATE [{host.JobsTable}] SET NextExecuteAt = SYSUTCDATETIME() WHERE JobKey = N'contested'");

        await Wait.Until(() => host.Log.DeliveryCount("tick:c") >= 1, "the contested job to run");
        await Task.Delay(700);
        await Assert.That(host.Log.DeliveryCount("tick:c")).IsEqualTo(1);
    }

    // ------------------------------------------------------------- transactional one-shot ---

    [Test]
    public async Task One_shot_in_a_committed_transaction_runs_once_and_goes_terminal()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString);
        string handle;

        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            handle = await host.TransactionalScheduler.ScheduleJobAsync(
                transaction, new TickCommand("once"), Instant.FromDateTimeOffset(DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }

        await Wait.Until(() => host.Log.DeliveryCount("tick:once") == 1, "the one-shot to run");
        await Wait.Until(async () => await host.JobStatus(handle) == "enqueued", "the one-shot to go terminal");
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
                transaction, new TickCommand("phantom"), Instant.FromDateTimeOffset(DateTimeOffset.UtcNow));
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
                Instant.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)));
            await host.TransactionalScheduler.CancelJobAsync(transaction, handle);
            await transaction.CommitAsync();
        }

        await Task.Delay(3000);
        await Assert.That(host.Log.DeliveryCount("tick:cancelled")).IsEqualTo(0);
        await Assert.That(await host.JobStatus(handle)).IsEqualTo("cancelled");
    }

    // ------------------------------------------------------------------- failure paths ---

    [Test]
    public async Task Failed_recurring_dispatch_dead_letters_the_occurrence_but_keeps_the_schedule()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        await host.Scheduler.RegisterAsync("broken", new TickCommand("b"), Schedule.Every(Duration.FromSeconds(1)));
        // Sabotage the persisted type name so the sweep cannot resolve the request.
        await host.ExecuteAsync(
            $"UPDATE [{host.JobsTable}] SET RequestTypeName = N'No.Such.Type', NextExecuteAt = SYSUTCDATETIME() " +
            $"WHERE JobKey = N'broken'");

        await using var sweeping = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, reuseSuffix: host.Suffix);

        await Wait.Until(async () => await host.DeadLetterCount() >= 1, "the occurrence to be dead-lettered");
        await Assert.That(await host.JobStatus("broken")).IsEqualTo("pending");
        var source = await host.ScalarAsync<string>(
            $"SELECT TOP(1) Source FROM [{host.QueueOptions.DeadLetterTableName}]");
        await Assert.That(source).IsEqualTo("Scheduler");
    }

    [Test]
    public async Task Unreadable_schedule_is_quarantined_without_blocking_other_jobs()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        await host.Scheduler.RegisterAsync("poison", new TickCommand("p"), Schedule.Every(Duration.FromSeconds(1)));
        await host.Scheduler.RegisterAsync("healthy", new TickCommand("h"), Schedule.Every(Duration.FromSeconds(1)));
        // Corrupt the poison job's schedule and make it the oldest-due row, so a sweep that fails
        // on it would starve everything behind it.
        await host.ExecuteAsync(
            $"UPDATE [{host.JobsTable}] SET Schedule = N'daily:Mars/Olympus:99:99:99', " +
            "NextExecuteAt = DATEADD(minute, -10, SYSUTCDATETIME()) WHERE JobKey = N'poison'");
        await host.ExecuteAsync(
            $"UPDATE [{host.JobsTable}] SET NextExecuteAt = SYSUTCDATETIME() WHERE JobKey = N'healthy'");

        await using var sweeping = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, reuseSuffix: host.Suffix,
            configure: services => services.AddSingleton(host.Log));

        // The healthy job must still dispatch, and the poison one is quarantined: dead-lettered,
        // still pending, and pushed out of the sweep's way.
        await Wait.Until(() => host.Log.DeliveryCount("tick:h") >= 1, "the healthy job to dispatch");
        await Wait.Until(async () => await host.DeadLetterCount() >= 1, "the poison job to be dead-lettered");
        await Assert.That(await host.JobStatus("poison")).IsEqualTo("pending");
        var next = await host.JobNextExecuteAt("poison");
        await Assert.That(next > DateTime.UtcNow.AddMinutes(3)).IsTrue();
    }

    [Test]
    public async Task Failed_one_shot_dispatch_goes_terminal_failed_with_a_dead_letter()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture.ConnectionString, sweeper: false);
        string handle;
        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            handle = await host.TransactionalScheduler.ScheduleJobAsync(
                transaction, new TickCommand("bad"), Instant.FromDateTimeOffset(DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }
        await host.ExecuteAsync(
            $"UPDATE [{host.JobsTable}] SET RequestTypeName = N'No.Such.Type' WHERE JobKey = N'{handle}'");

        await using var sweeping = await SchedulerTestHost.StartAsync(
            fixture.ConnectionString, reuseSuffix: host.Suffix);

        await Wait.Until(async () => await host.JobStatus(handle) == "failed", "the one-shot to go terminal-failed");
        await Assert.That(await host.DeadLetterCount()).IsEqualTo(1);
    }
}
