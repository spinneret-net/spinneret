using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace Spinneret.Scheduler.Tests;

public class RecurringJobInstallerTests
{
    private static readonly DateTimeZone Stockholm = DateTimeZoneProviders.Tzdb["Europe/Stockholm"];

    private static readonly Schedule EveryFiveMinutes = Schedule.Cron(Stockholm, "*/5 * * * *");
    private static readonly Schedule Hourly = Schedule.Cron(Stockholm, "0 * * * *");

    private static RecurringJobInstaller CreateInstaller(
        RecordingRecurringJobScheduler scheduler, params IRecurringJob[] jobs) =>
        new(scheduler, jobs, NullLogger<RecurringJobInstaller>.Instance);

    [Test]
    public async Task StartAsync_installs_every_registered_job_with_its_key_schedule_and_request()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var requestA = new TestRequest("a");
        var requestB = new OtherTestRequest(2);
        var installer = CreateInstaller(
            scheduler,
            new FakeRecurringJob("job-a", EveryFiveMinutes, requestA),
            new FakeRecurringJob("job-b", Hourly, requestB));

        await installer.StartAsync(CancellationToken.None);

        await Assert.That(scheduler.Registrations.Count).IsEqualTo(2);
        await Assert.That(scheduler.Registrations[0].Key).IsEqualTo("job-a");
        await Assert.That(scheduler.Registrations[0].Schedule).IsEqualTo(EveryFiveMinutes);
        await Assert.That(scheduler.Registrations[0].Request).IsSameReferenceAs(requestA);
        await Assert.That(scheduler.Registrations[1].Key).IsEqualTo("job-b");
        await Assert.That(scheduler.Registrations[1].Schedule).IsEqualTo(Hourly);
        await Assert.That(scheduler.Registrations[1].Request).IsSameReferenceAs(requestB);
    }

    [Test]
    public async Task StartAsync_with_no_jobs_registers_nothing_and_completes()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(scheduler);

        await installer.StartAsync(CancellationToken.None);

        await Assert.That(scheduler.Registrations).IsEmpty();
    }

    [Test]
    public async Task StartAsync_scheduler_failure_for_one_job_does_not_block_the_rest()
    {
        var scheduler = new RecordingRecurringJobScheduler { FailingKeys = { "job-a" } };
        var installer = CreateInstaller(
            scheduler,
            new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a")),
            new FakeRecurringJob("job-b", EveryFiveMinutes, new TestRequest("b")));

        await installer.StartAsync(CancellationToken.None);

        await Assert.That(scheduler.Registrations.Count).IsEqualTo(1);
        await Assert.That(scheduler.Registrations[0].Key).IsEqualTo("job-b");
    }

    [Test]
    public async Task StartAsync_schedule_failure_for_one_job_does_not_block_the_rest()
    {
        // A job is free to build its schedule from configuration, so the property itself can throw
        // — on an unparseable setting, say. One job's bad setting must not cost the others.
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(
            scheduler,
            new ThrowingScheduleJob("job-a"),
            new FakeRecurringJob("job-b", EveryFiveMinutes, new TestRequest("b")));

        await installer.StartAsync(CancellationToken.None);

        await Assert.That(scheduler.Registrations.Count).IsEqualTo(1);
        await Assert.That(scheduler.Registrations[0].Key).IsEqualTo("job-b");
    }

    [Test]
    public async Task StartAsync_create_request_failure_for_one_job_does_not_block_the_rest()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(
            scheduler,
            new ThrowingCreateRequestJob("job-a"),
            new FakeRecurringJob("job-b", EveryFiveMinutes, new TestRequest("b")));

        await installer.StartAsync(CancellationToken.None);

        await Assert.That(scheduler.Registrations.Count).IsEqualTo(1);
        await Assert.That(scheduler.Registrations[0].Key).IsEqualTo("job-b");
    }

    [Test]
    public async Task StartAsync_passes_the_host_cancellation_token_to_the_scheduler()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(
            scheduler,
            new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a")));
        using var cts = new CancellationTokenSource();

        await installer.StartAsync(cts.Token);

        await Assert.That(scheduler.Registrations[0].Ct).IsEqualTo(cts.Token);
    }

    [Test]
    public async Task StopAsync_completes_without_touching_the_scheduler()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(scheduler);

        await installer.StopAsync(CancellationToken.None);

        await Assert.That(scheduler.Registrations).IsEmpty();
    }
}
