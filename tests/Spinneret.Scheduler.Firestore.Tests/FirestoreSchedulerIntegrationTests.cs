using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Functional;
using Spinneret.Mediator;

namespace Spinneret.Scheduler.Firestore.Tests;

// The emulator never enforces composite indexes, so these tests don't cover index provisioning.
[ClassDataSource<FirestoreEmulatorFixture>(Shared = SharedType.PerTestSession)]
public sealed class FirestoreSchedulerIntegrationTests(FirestoreEmulatorFixture fixture)
{
    private const string Stockholm = "Europe/Stockholm";

    private static readonly Schedule Hourly = Schedule.Cron("0 * * * *", Stockholm);
    private static readonly Schedule EveryTwoHours = Schedule.Cron("0 */2 * * *", Stockholm);

    /// <summary>Never falls due together with <see cref="Hourly"/>, so the re-arm test is independent of clock time.</summary>
    private static readonly Schedule HalfPastEveryHour = Schedule.Cron("30 * * * *", Stockholm);

    [Test]
    public async Task RegisterAsync_creates_a_job_with_the_first_run_armed()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        await host.Scheduler.RegisterAsync("hourly", new TestRequest("h"), Hourly);

        await Assert.That(await host.JobExists("hourly")).IsTrue();
        // An hourly slot lands somewhere in the next hour rather than a fixed distance out.
        var next = await host.JobNextExecuteAt("hourly");
        await Assert.That(next > DateTimeOffset.UtcNow).IsTrue();
        await Assert.That(next <= DateTimeOffset.UtcNow.AddMinutes(60)).IsTrue();
        await Assert.That(await host.JobField<string>("hourly", ScheduledJob.Fields.Schedule))
            .IsEqualTo("cron:Europe/Stockholm:0 * * * *");
    }

    [Test]
    public async Task RegisterAsync_stores_the_request_type_and_payload_the_sweep_will_read()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        await host.Scheduler.RegisterAsync("typed", new TestRequest("ada"), Hourly);

        await Assert.That(await host.JobField<string>("typed", ScheduledJob.Fields.RequestTypeName))
            .IsEqualTo(typeof(TestRequest).FullName!);
        await Assert.That(await host.JobField<string>("typed", ScheduledJob.Fields.PayloadJson))
            .IsEqualTo("""{"name":"ada"}""");
    }

    [Test]
    public async Task RegisterAsync_with_unchanged_schedule_keeps_the_cadence()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        await host.Scheduler.RegisterAsync("stable", new TestRequest("v1"), Hourly);
        var armed = await host.JobNextExecuteAt("stable");

        await host.Scheduler.RegisterAsync("stable", new TestRequest("v2"), Hourly);

        await Assert.That(await host.JobNextExecuteAt("stable")).IsEqualTo(armed);
        await Assert.That(await host.JobField<string>("stable", ScheduledJob.Fields.PayloadJson))
            .Contains("v2");
    }

    [Test]
    public async Task RegisterAsync_that_changes_nothing_writes_nothing()
    {
        // Firestore bills a write even for identical values, and every instance re-asserts every job at startup.
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        await host.Scheduler.RegisterAsync("settled", new TestRequest("v1"), Hourly);
        var written = await host.JobUpdateTime("settled");

        await host.Scheduler.RegisterAsync("settled", new TestRequest("v1"), Hourly);

        await Assert.That(await host.JobUpdateTime("settled")).IsEqualTo(written);
    }

    [Test]
    public async Task RegisterAsync_with_a_changed_schedule_rearms_the_job()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        await host.Scheduler.RegisterAsync("moving", new TestRequest("m"), HalfPastEveryHour);
        var armed = await host.JobNextExecuteAt("moving");

        await host.Scheduler.RegisterAsync("moving", new TestRequest("m"), Hourly);

        await Assert.That(await host.JobNextExecuteAt("moving")).IsNotEqualTo(armed);
        await Assert.That(await host.JobField<string>("moving", ScheduledJob.Fields.Schedule))
            .IsEqualTo("cron:Europe/Stockholm:0 * * * *");
    }

    [Test]
    public async Task RegisterAsync_recreates_a_job_that_is_gone()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("revived", new TestRequest("r"), Hourly);
        await host.Job("revived").DeleteAsync();

        await host.Scheduler.RegisterAsync("revived", new TestRequest("r"), Hourly);

        await Assert.That(await host.JobExists("revived")).IsTrue();
        await Assert.That(await host.JobField<string>("revived", ScheduledJob.Fields.Schedule))
            .IsEqualTo("cron:Europe/Stockholm:0 * * * *");
    }

    [Test]
    public async Task RegisterAsync_from_many_instances_at_once_yields_one_job()
    {
        // The emulator may abort contending transactions ("Transaction lock timeout"); that is emulator
        // behaviour, so only the one-job invariant is asserted.
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            try
            {
                await host.Scheduler.RegisterAsync(
                    "contested-parallel", new TestRequest($"instance-{i}"), Hourly);
                return true;
            }
            catch (RpcException)
            {
                return false;
            }
        }));

        await Assert.That(outcomes).Contains(true);
        await Assert.That(await host.JobCount()).IsEqualTo(1);
        await Assert.That(await host.JobExists("contested-parallel")).IsTrue();
    }

    [Test]
    public async Task RecurringJobInstaller_installs_declared_jobs_at_startup()
    {
        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            configure: services => services.AddRecurringJob(
                "declared-job", EveryTwoHours, () => new TestRequest("declared")));

        await Wait.Until(() => host.JobExists("declared-job"), "the declared job to be installed");
    }

    [Test]
    public async Task RecurringJobInstaller_retires_declared_keys_at_startup()
    {
        var collection = $"jobs_{Guid.NewGuid():N}";
        await using (var seeding = await SchedulerTestHost.StartAsync(fixture, reuseCollection: collection))
            await seeding.Scheduler.RegisterAsync("obsolete", new TestRequest("o"), Hourly);

        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            configure: services => services.RetireRecurringJob("obsolete"),
            reuseCollection: collection);

        await Wait.Until(async () => !await host.JobExists("obsolete"), "the retired job to be removed");
    }

    [Test]
    public async Task Parallel_installers_converge_on_one_job_per_key()
    {
        var collection = $"jobs_{Guid.NewGuid():N}";
        var hosts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => SchedulerTestHost.StartAsync(
            fixture,
            configure: services => services.AddRecurringJob(
                "fleet-wide", EveryTwoHours, () => new TestRequest("fleet")),
            reuseCollection: collection)));

        try
        {
            await Wait.Until(
                async () => await hosts[0].JobCount() == 1, "the fleet-wide job to be installed exactly once");
            await Assert.That(await hosts[0].JobExists("fleet-wide")).IsTrue();
        }
        finally
        {
            foreach (var host in hosts)
                await host.DisposeAsync();
        }
    }

    [Test]
    public async Task UnregisterAsync_removes_a_recurring_job()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("going", new TestRequest("g"), Hourly);

        await host.Scheduler.UnregisterAsync("going");

        await Assert.That(await host.JobExists("going")).IsFalse();
    }

    [Test]
    public async Task UnregisterAsync_for_an_unknown_key_is_a_no_op()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        await host.Scheduler.UnregisterAsync("never-registered");

        await Assert.That(await host.JobCount()).IsEqualTo(0);
    }

    [Test]
    public async Task UnregisterAsync_leaves_a_one_shot_job_untouched()
    {
        // One-shots share the collection, so a key collision must not delete one.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var handle = await ScheduleOneShot(host, new TestRequest("once"), DateTimeOffset.UtcNow.AddHours(1));

        await host.Scheduler.UnregisterAsync(handle);

        await Assert.That(await host.JobExists(handle)).IsTrue();
    }

    private static async Task<string> ScheduleOneShot(
        SchedulerTestHost host, IRequest<Unit> request, DateTimeOffset executeAt)
    {
        string handle = null!;
        await host.Db.RunTransactionAsync(transaction =>
        {
            handle = host.TransactionalScheduler.ScheduleJob(transaction, request, executeAt);
            return Task.CompletedTask;
        });
        return handle;
    }
}
