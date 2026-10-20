using Microsoft.Extensions.Logging.Abstractions;

namespace Spinneret.Scheduler.Tests;

public class RecurringJobInstallerTests
{
    private const string Stockholm = "Europe/Stockholm";

    private static readonly Schedule EveryFiveMinutes = Schedule.Cron("*/5 * * * *", Stockholm);
    private static readonly Schedule Hourly = Schedule.Cron("0 * * * *", Stockholm);

    /// <summary>Instant backoff; the delay throws after <paramref name="maxRounds"/>, as on shutdown, to end the loop.</summary>
    private static (RecurringJobInstaller Installer, List<TimeSpan> Delays) CreateInstaller(
        RecordingRecurringJobScheduler scheduler,
        IRecurringJob[] jobs,
        IRetiredRecurringJob[]? retired = null,
        int maxRounds = 20)
    {
        var delays = new List<TimeSpan>();
        var installer = new RecurringJobInstaller(
            scheduler, jobs, retired ?? [], NullLogger<RecurringJobInstaller>.Instance,
            (delay, _) =>
            {
                delays.Add(delay);
                return delays.Count > maxRounds
                    ? throw new OperationCanceledException()
                    : Task.CompletedTask;
            });
        return (installer, delays);
    }

    private static async Task RunAsync(RecurringJobInstaller installer)
    {
        await installer.StartAsync(CancellationToken.None);
        await installer.ExecuteTask!;
    }

    [Test]
    public async Task Installs_every_registered_job_with_its_key_schedule_and_request()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var requestA = new TestRequest("a");
        var requestB = new OtherTestRequest(2);
        var (installer, _) = CreateInstaller(scheduler, [
            new FakeRecurringJob("job-a", EveryFiveMinutes, requestA),
            new FakeRecurringJob("job-b", Hourly, requestB),
        ]);

        await RunAsync(installer);

        await Assert.That(scheduler.Registrations.Count).IsEqualTo(2);
        await Assert.That(scheduler.Registrations[0].Key).IsEqualTo("job-a");
        await Assert.That(scheduler.Registrations[0].Schedule).IsEqualTo(EveryFiveMinutes);
        await Assert.That(scheduler.Registrations[0].Request).IsSameReferenceAs(requestA);
        await Assert.That(scheduler.Registrations[1].Key).IsEqualTo("job-b");
        await Assert.That(scheduler.Registrations[1].Schedule).IsEqualTo(Hourly);
        await Assert.That(scheduler.Registrations[1].Request).IsSameReferenceAs(requestB);
    }

    [Test]
    public async Task Installs_jobs_of_differing_response_types_together()
    {
        // The installer holds non-generic IRecurringJobs, so each job must register itself with its TResponse.
        var scheduler = new RecordingRecurringJobScheduler();
        var unitRequest = new TestRequest("a");
        var stringRequest = new StringResponseRequest("b");
        var (installer, _) = CreateInstaller(scheduler, [
            new FakeRecurringJob("job-unit", EveryFiveMinutes, unitRequest),
            new FakeStringResponseJob("job-string", Hourly, stringRequest),
        ]);

        await RunAsync(installer);

        await Assert.That(scheduler.Registrations.Count).IsEqualTo(2);
        await Assert.That(scheduler.Registrations[0].Request).IsSameReferenceAs(unitRequest);
        await Assert.That(scheduler.Registrations[1].Key).IsEqualTo("job-string");
        await Assert.That(scheduler.Registrations[1].Request).IsSameReferenceAs(stringRequest);
    }

    [Test]
    public async Task With_no_jobs_registers_nothing_and_completes()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, _) = CreateInstaller(scheduler, []);

        await RunAsync(installer);

        await Assert.That(scheduler.Registrations).IsEmpty();
    }

    [Test]
    public async Task Passes_the_stopping_token_to_the_scheduler()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, _) = CreateInstaller(
            scheduler, [new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a"))]);

        await RunAsync(installer);

        // The background service's stopping token, not the startup token.
        await Assert.That(scheduler.Registrations[0].Ct.CanBeCanceled).IsTrue();
    }

    [Test]
    public async Task Retries_a_failing_job_until_it_succeeds()
    {
        // A transient outage must not strand the job until the next restart.
        var scheduler = new RecordingRecurringJobScheduler { FailingAttempts = { ["flaky"] = 2 } };
        var (installer, delays) = CreateInstaller(
            scheduler, [new FakeRecurringJob("flaky", EveryFiveMinutes, new TestRequest("a"))]);

        await RunAsync(installer);

        await Assert.That(scheduler.Registrations.Select(r => r.Key)).IsEquivalentTo(["flaky"]);
        await Assert.That(scheduler.Attempts["flaky"]).IsEqualTo(3);
        await Assert.That(delays.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Stops_retrying_once_every_job_is_installed()
    {
        // Re-asserting on a timer would make revisions overwrite each other during a rolling deploy.
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, delays) = CreateInstaller(
            scheduler, [new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a"))]);

        await RunAsync(installer);

        await Assert.That(scheduler.Attempts["job-a"]).IsEqualTo(1);
        await Assert.That(delays).IsEmpty();
    }

    [Test]
    public async Task A_job_that_succeeded_is_not_reinstalled_while_another_is_still_retrying()
    {
        var scheduler = new RecordingRecurringJobScheduler { FailingAttempts = { ["flaky"] = 3 } };
        var (installer, _) = CreateInstaller(scheduler, [
            new FakeRecurringJob("steady", EveryFiveMinutes, new TestRequest("s")),
            new FakeRecurringJob("flaky", Hourly, new TestRequest("f")),
        ]);

        await RunAsync(installer);

        await Assert.That(scheduler.Attempts["steady"]).IsEqualTo(1);
        await Assert.That(scheduler.Attempts["flaky"]).IsEqualTo(4);
    }

    [Test]
    public async Task A_permanently_failing_job_does_not_block_the_others()
    {
        var scheduler = new RecordingRecurringJobScheduler { FailingKeys = { "job-a" } };
        var (installer, _) = CreateInstaller(scheduler, [
            new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a")),
            new FakeRecurringJob("job-b", EveryFiveMinutes, new TestRequest("b")),
        ], maxRounds: 3);

        await RunAsync(installer);

        await Assert.That(scheduler.Registrations.Select(r => r.Key)).IsEquivalentTo(["job-b"]);
    }

    [Test]
    public async Task A_throwing_schedule_is_retried_like_any_other_failure()
    {
        // A schedule built from configuration can throw.
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, delays) = CreateInstaller(scheduler, [
            new ThrowingScheduleJob("job-a"),
            new FakeRecurringJob("job-b", EveryFiveMinutes, new TestRequest("b")),
        ], maxRounds: 3);

        await RunAsync(installer);

        await Assert.That(scheduler.Registrations.Select(r => r.Key)).IsEquivalentTo(["job-b"]);
        await Assert.That(delays.Count).IsGreaterThan(1);
    }

    [Test]
    public async Task A_throwing_create_request_is_retried_like_any_other_failure()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, _) = CreateInstaller(scheduler, [
            new ThrowingCreateRequestJob("job-a"),
            new FakeRecurringJob("job-b", EveryFiveMinutes, new TestRequest("b")),
        ], maxRounds: 3);

        await RunAsync(installer);

        await Assert.That(scheduler.Registrations.Select(r => r.Key)).IsEquivalentTo(["job-b"]);
    }

    [Test]
    public async Task Shutdown_ends_the_loop_without_faulting()
    {
        // The loop treats Task.Delay's cancellation as "stop".
        var scheduler = new RecordingRecurringJobScheduler { FailingKeys = { "job-a" } };
        var (installer, _) = CreateInstaller(
            scheduler, [new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a"))],
            maxRounds: 2);

        await RunAsync(installer);

        await Assert.That(installer.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task Backoff_doubles_from_the_base_delay()
    {
        await Assert.That(RecurringJobInstaller.BaseDelayForAttempt(1))
            .IsEqualTo(RecurringJobInstaller.BaseRetryDelay);
        await Assert.That(RecurringJobInstaller.BaseDelayForAttempt(2))
            .IsEqualTo(RecurringJobInstaller.BaseRetryDelay * 2);
        await Assert.That(RecurringJobInstaller.BaseDelayForAttempt(3))
            .IsEqualTo(RecurringJobInstaller.BaseRetryDelay * 4);
    }

    [Test]
    [Arguments(10)]
    [Arguments(100)]
    [Arguments(100_000)]
    public async Task Backoff_is_capped_and_never_overflows(int attempt)
    {
        // Retrying is indefinite, so the cap bounds the load and the attempt counter can grow unbounded.
        await Assert.That(RecurringJobInstaller.BaseDelayForAttempt(attempt))
            .IsEqualTo(RecurringJobInstaller.MaxRetryDelay);
    }

    [Test]
    public async Task Actual_delays_stay_within_the_jitter_band_of_their_attempt()
    {
        // Equal jitter: half the delay plus a random share of the other half.
        var scheduler = new RecordingRecurringJobScheduler { FailingKeys = { "job-a" } };
        var (installer, delays) = CreateInstaller(
            scheduler, [new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a"))],
            maxRounds: 6);

        await RunAsync(installer);

        foreach (var (delay, index) in delays.Select((d, i) => (d, i)))
        {
            var full = RecurringJobInstaller.BaseDelayForAttempt(index + 1);
            await Assert.That(delay).IsGreaterThanOrEqualTo(full / 2);
            await Assert.That(delay).IsLessThanOrEqualTo(full);
        }
    }

    [Test]
    public async Task Unregisters_every_retired_key()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, _) = CreateInstaller(
            scheduler,
            [new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a"))],
            [new RetiredJob("gone-one"), new RetiredJob("gone-two")]);

        await RunAsync(installer);

        await Assert.That(scheduler.Unregistrations.Select(u => u.Key))
            .IsEquivalentTo(["gone-one", "gone-two"]);
        // Retiring doesn't replace installing: the live job is still asserted.
        await Assert.That(scheduler.Registrations.Select(r => r.Key)).IsEquivalentTo(["job-a"]);
    }

    [Test]
    public async Task With_no_retirements_unregisters_nothing()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, _) = CreateInstaller(
            scheduler, [new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a"))]);

        await RunAsync(installer);

        await Assert.That(scheduler.Unregistrations).IsEmpty();
    }

    [Test]
    public async Task Retries_a_failing_retirement_until_it_succeeds()
    {
        var scheduler = new RecordingRecurringJobScheduler { FailingAttempts = { ["gone"] = 2 } };
        var (installer, _) = CreateInstaller(scheduler, [], [new RetiredJob("gone")]);

        await RunAsync(installer);

        await Assert.That(scheduler.Unregistrations.Select(u => u.Key)).IsEquivalentTo(["gone"]);
        await Assert.That(scheduler.Attempts["gone"]).IsEqualTo(3);
    }

    [Test]
    public async Task A_permanently_failing_retirement_does_not_block_the_others()
    {
        var scheduler = new RecordingRecurringJobScheduler { FailingKeys = { "gone-one" } };
        var (installer, _) = CreateInstaller(
            scheduler, [], [new RetiredJob("gone-one"), new RetiredJob("gone-two")], maxRounds: 3);

        await RunAsync(installer);

        await Assert.That(scheduler.Unregistrations.Select(u => u.Key)).IsEquivalentTo(["gone-two"]);
    }

    [Test]
    public async Task Duplicate_job_keys_fail_startup()
    {
        // A code bug that retrying can't fix, so it fails startup instead.
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, _) = CreateInstaller(scheduler, [
            new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a")),
            new FakeRecurringJob("job-a", Hourly, new TestRequest("b")),
        ]);

        await Assert.That(() => installer.StartAsync(CancellationToken.None))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("job-a");
    }

    [Test]
    public async Task Duplicate_job_keys_differing_only_in_case_fail_startup()
    {
        // A case-insensitive store (SQL Server's usual collation) collapses these into one row.
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, _) = CreateInstaller(scheduler, [
            new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a")),
            new FakeRecurringJob("JOB-A", Hourly, new TestRequest("b")),
        ]);

        await Assert.That(() => installer.StartAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_key_both_declared_and_retired_fails_startup()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, _) = CreateInstaller(
            scheduler,
            [new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a"))],
            [new RetiredJob("job-a")]);

        await Assert.That(() => installer.StartAsync(CancellationToken.None))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("job-a");
    }

    [Test]
    public async Task Validation_runs_before_anything_is_installed()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, _) = CreateInstaller(scheduler, [
            new FakeRecurringJob("job-a", EveryFiveMinutes, new TestRequest("a")),
            new FakeRecurringJob("job-b", Hourly, new TestRequest("b")),
            new FakeRecurringJob("job-b", Hourly, new TestRequest("c")),
        ]);

        await Assert.That(() => installer.StartAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(scheduler.Registrations).IsEmpty();
    }

    [Test]
    public async Task StopAsync_before_start_completes_without_touching_the_scheduler()
    {
        var scheduler = new RecordingRecurringJobScheduler();
        var (installer, _) = CreateInstaller(scheduler, []);

        await installer.StopAsync(CancellationToken.None);

        await Assert.That(scheduler.Registrations).IsEmpty();
    }
}
