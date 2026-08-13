using Microsoft.Extensions.Logging.Abstractions;

namespace Spinneret.Scheduler.Tests;

public class RecurringJobInstallerTests
{
    private const string Stockholm = "Europe/Stockholm";

    private static readonly Schedule EveryFiveMinutes = Schedule.Cron("*/5 * * * *", Stockholm);
    private static readonly Schedule Hourly = Schedule.Cron("0 * * * *", Stockholm);

    private static RecurringJobInstaller CreateInstaller(
        RecordingRecurringJobScheduler scheduler, params IRecurringJob[] jobs) =>
        new(scheduler, jobs, [], NullLogger<RecurringJobInstaller>.Instance);

    private static RecurringJobInstaller CreateInstaller(
        RecordingRecurringJobScheduler scheduler, IRecurringJob[] jobs, IRetiredRecurringJob[] retired) =>
        new(scheduler, jobs, retired, NullLogger<RecurringJobInstaller>.Instance);

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

    // ---------------------------------------------------------------------------- retiring ---

    [Test]
    public async Task StartAsync_unregisters_every_retired_key()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(
            scheduler,
            [new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a"))],
            [new RetiredJob("gone-one"), new RetiredJob("gone-two")]);

        await installer.StartAsync(CancellationToken.None);

        await Assert.That(scheduler.Unregistrations.Select(u => u.Key))
            .IsEquivalentTo(["gone-one", "gone-two"]);
        // Retiring is cleanup, not a substitute for installing: the live job is still asserted.
        await Assert.That(scheduler.Registrations.Select(r => r.Key)).IsEquivalentTo(["job-a"]);
    }

    [Test]
    public async Task StartAsync_with_no_retirements_unregisters_nothing()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(
            scheduler, new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a")));

        await installer.StartAsync(CancellationToken.None);

        await Assert.That(scheduler.Unregistrations).IsEmpty();
    }

    [Test]
    public async Task StartAsync_retire_failure_for_one_key_does_not_block_the_rest()
    {
        // Removal is as environmental as installation — a transient store failure must not cost the
        // other retirements or the startup.
        var scheduler = new RecordingRecurringJobScheduler { FailingKeys = { "gone-one" } };
        var installer = CreateInstaller(
            scheduler, [], [new RetiredJob("gone-one"), new RetiredJob("gone-two")]);

        await installer.StartAsync(CancellationToken.None);

        await Assert.That(scheduler.Unregistrations.Select(u => u.Key)).IsEquivalentTo(["gone-two"]);
    }

    [Test]
    public async Task StartAsync_passes_the_host_cancellation_token_when_retiring()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(scheduler, [], [new RetiredJob("gone")]);
        using var cts = new CancellationTokenSource();

        await installer.StartAsync(cts.Token);

        await Assert.That(scheduler.Unregistrations[0].Ct).IsEqualTo(cts.Token);
    }

    // -------------------------------------------------------------------------- validation ---

    [Test]
    public async Task StartAsync_duplicate_job_keys_throw()
    {
        // Two jobs under one key install only the last of them, so the other silently never runs.
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(
            scheduler,
            new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a")),
            new FakeRecurringJob("job-a", Hourly, new TestRequest("b")));

        await Assert.That(() => installer.StartAsync(CancellationToken.None))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("job-a");
    }

    [Test]
    public async Task StartAsync_duplicate_job_keys_differing_only_in_case_throw()
    {
        // A case-insensitive store (SQL Server's usual collation) collapses these into one row.
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(
            scheduler,
            new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a")),
            new FakeRecurringJob("JOB-A", Hourly, new TestRequest("b")));

        await Assert.That(() => installer.StartAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task StartAsync_a_key_both_declared_and_retired_throws()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(
            scheduler,
            [new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a"))],
            [new RetiredJob("job-a")]);

        await Assert.That(() => installer.StartAsync(CancellationToken.None))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("job-a");
    }

    [Test]
    public async Task StartAsync_validation_runs_before_anything_is_installed()
    {
        // The throw has to precede the work: half-applying a contradictory set would leave the
        // scheduler in a state no restart converges on.
        var scheduler = new RecordingRecurringJobScheduler();
        var installer = CreateInstaller(
            scheduler,
            new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a")),
            new FakeRecurringJob("job-b", Hourly, new TestRequest("b")),
            new FakeRecurringJob("job-b", Hourly, new TestRequest("c")));

        await Assert.That(() => installer.StartAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(scheduler.Registrations).IsEmpty();
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
