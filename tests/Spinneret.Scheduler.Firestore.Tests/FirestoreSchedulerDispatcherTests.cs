using System.Text.RegularExpressions;
using Google.Cloud.Firestore;
using Spinneret.Functional;
using Spinneret.Mediator;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore.Tests;

// The emulator never enforces composite indexes, so these tests don't cover index provisioning.
[ClassDataSource<FirestoreEmulatorFixture>(Shared = SharedType.PerTestSession)]
public sealed class FirestoreSchedulerDispatcherTests(FirestoreEmulatorFixture fixture)
{
    private const string Stockholm = "Europe/Stockholm";
    private static readonly Schedule Hourly = Schedule.Cron("0 * * * *", Stockholm);

    private static int Enqueued(SchedulerTestHost host, string name) =>
        host.Queue.CountOf<TestRequest>(r => r.Name == name);

    [Test]
    public async Task Due_recurring_job_is_enqueued_and_rearmed()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("due", new TestRequest("d"), Hourly);
        await host.MakeDue("due");

        var result = await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(result.JobsDispatched).IsEqualTo(1);
        await Assert.That(Enqueued(host, "d")).IsEqualTo(1);
        await Assert.That(await host.JobExists("due")).IsTrue();
        await Assert.That(await host.JobNextExecuteAt("due") > DateTimeOffset.UtcNow).IsTrue();
    }

    [Test]
    public async Task A_dispatched_recurring_job_records_when_it_last_ran()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("observed", new TestRequest("o"), Hourly);
        await host.MakeDue("observed");

        await host.Sweep.SweepAsync(CancellationToken.None);

        var lastRun = (await host.JobField<Timestamp>("observed", ScheduledJob.Fields.LastRunAt))
            .ToDateTimeOffset();
        await Assert.That(lastRun).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Test]
    public async Task Due_recurring_job_with_a_non_unit_response_is_enqueued()
    {
        // The sweep enqueues via reflection on the stored type name, so a non-Unit response matters.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("reporting", new ReportRequest("r"), Hourly);
        await host.MakeDue("reporting");

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.Queue.CountOf<ReportRequest>(r => r.Name == "r")).IsEqualTo(1);
    }

    [Test]
    public async Task A_job_that_is_not_due_is_left_alone()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("later", new TestRequest("l"), Hourly);
        var armed = await host.JobNextExecuteAt("later");

        var result = await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(result.JobsDispatched).IsEqualTo(0);
        await Assert.That(host.Queue.Enqueued).IsEmpty();
        await Assert.That(await host.JobNextExecuteAt("later")).IsEqualTo(armed);
    }

    [Test]
    public async Task Competing_sweeps_dispatch_a_due_job_exactly_once()
    {
        // Only the lease (advancing nextExecuteAt in a transaction) prevents a double dispatch.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await using var rival = await SchedulerTestHost.StartAsync(fixture, reuseCollection: host.Collection);

        await host.Scheduler.RegisterAsync("contested", new TestRequest("c"), Hourly);
        await host.MakeDue("contested");

        await Task.WhenAll(
            host.Sweep.SweepAsync(CancellationToken.None),
            rival.Sweep.SweepAsync(CancellationToken.None));

        await Assert.That(Enqueued(host, "c") + Enqueued(rival, "c")).IsEqualTo(1);
    }

    [Test]
    public async Task A_second_sweep_does_not_redispatch_a_job_it_already_leased()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("once-only", new TestRequest("x"), Hourly);
        await host.MakeDue("once-only");

        await host.Sweep.SweepAsync(CancellationToken.None);
        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(Enqueued(host, "x")).IsEqualTo(1);
    }

    [Test]
    public async Task The_sweeper_dispatches_a_due_job_without_being_asked()
    {
        // Via the timer, so the hosted service and its interval are exercised.
        await using var host = await SchedulerTestHost.StartAsync(fixture, sweeper: true);
        await host.Scheduler.RegisterAsync("timed", new TestRequest("t"), Hourly);
        await host.MakeDue("timed");

        await Wait.Until(() => Enqueued(host, "t") == 1, "the sweeper to dispatch the due job");
    }

    [Test]
    public async Task Failed_recurring_dispatch_dead_letters_the_occurrence_but_keeps_the_schedule()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("broken", new TestRequest("b"), Hourly);
        // Corrupt the stored type name so the request can't be resolved.
        await host.SetField("broken", ScheduledJob.Fields.RequestTypeName, "No.Such.Type");
        await host.MakeDue("broken");

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.DeadLetters.Entries).Count().IsEqualTo(1);
        var entry = host.DeadLetters.Entries.Single();
        await Assert.That(entry.Source).IsEqualTo(DeadLetterSource.Scheduler);
        await Assert.That(entry.CommandTypeName).IsEqualTo("No.Such.Type");
        await Assert.That(await host.JobExists("broken")).IsTrue();
        await Assert.That(await host.JobNextExecuteAt("broken") > DateTimeOffset.UtcNow).IsTrue();
    }

    [Test]
    public async Task Each_failed_recurring_occurrence_is_dead_lettered_separately()
    {
        // Unlike queue redeliveries, each failed occurrence is a distinct event.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("repeatedly-broken", new TestRequest("rb"), Hourly);
        await host.SetField("repeatedly-broken", ScheduledJob.Fields.RequestTypeName, "No.Such.Type");

        await host.MakeDue("repeatedly-broken");
        await host.Sweep.SweepAsync(CancellationToken.None);
        await host.MakeDue("repeatedly-broken");
        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.DeadLetters.Entries).Count().IsEqualTo(2);
        await Assert.That(host.DeadLetters.Entries.Select(e => e.IdempotencyKey).Distinct()).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Unreadable_schedule_is_quarantined_without_blocking_other_jobs()
    {
        // E.g. written by a newer version, then rolled back: must neither block others nor be deleted.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("poison", new TestRequest("p"), Hourly);
        await host.Scheduler.RegisterAsync("healthy", new TestRequest("h"), Hourly);
        await host.SetField("poison", ScheduledJob.Fields.Schedule, "cron:Mars/Olympus:0 3 * * *");
        // Oldest-due, so a sweep that failed on it would never reach the healthy job.
        await host.MakeDue("poison", TimeSpan.FromMinutes(10));
        await host.MakeDue("healthy");

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(Enqueued(host, "h")).IsEqualTo(1);
        await Assert.That(host.Queue.CountOf<TestRequest>(r => r.Name == "p")).IsEqualTo(0);
        await Assert.That(await host.JobExists("poison")).IsTrue();
        await Assert.That(await host.JobNextExecuteAt("poison") > DateTimeOffset.UtcNow.AddMinutes(4)).IsTrue();
        await Assert.That(host.DeadLetters.Entries).Count().IsEqualTo(1);
    }

    [Test]
    public async Task A_failing_enqueue_does_not_stop_the_rest_of_the_sweep()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("first", new TestRequest("1"), Hourly);
        await host.Scheduler.RegisterAsync("second", new TestRequest("2"), Hourly);
        await host.MakeDue("first", TimeSpan.FromMinutes(10));
        await host.MakeDue("second");
        host.Queue.FailWith = new InvalidOperationException("transport is down");

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(await host.JobField<int>("first", ScheduledJob.Fields.DispatchFailures)).IsEqualTo(1);
        await Assert.That(await host.JobField<int>("second", ScheduledJob.Fields.DispatchFailures)).IsEqualTo(1);
    }

    [Test]
    public async Task A_recurring_job_whose_enqueue_fails_stays_due_instead_of_being_dead_lettered()
    {
        // A transport blip must neither page anyone nor cost a daily job its day.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("flaky", new TestRequest("f"), Hourly);
        await host.MakeDue("flaky");
        host.Queue.FailWith = new InvalidOperationException("DeadlineExceeded");

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.DeadLetters.Entries).IsEmpty();
        await Assert.That(await host.JobField<int>("flaky", ScheduledJob.Fields.DispatchFailures)).IsEqualTo(1);
        await Assert.That(await host.JobNextExecuteAt("flaky") < DateTimeOffset.UtcNow.AddMinutes(1)).IsTrue();
    }

    [Test]
    public async Task Consecutive_enqueue_failures_are_counted()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("down", new TestRequest("dn"), Hourly);
        host.Queue.FailWith = new InvalidOperationException("transport is down");

        for (var i = 0; i < 3; i++)
        {
            await host.MakeDue("down");
            await host.Sweep.SweepAsync(CancellationToken.None);
        }

        await Assert.That(await host.JobField<int>("down", ScheduledJob.Fields.DispatchFailures)).IsEqualTo(3);
        await Assert.That(host.DeadLetters.Entries).IsEmpty();
    }

    [Test]
    public async Task A_recurring_job_failing_past_the_retry_window_is_dead_lettered_and_resumes_its_schedule()
    {
        // A failure that never clears (a deleted queue) must surface, not retry unseen forever.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("stuck", new TestRequest("st"), Hourly);
        await host.MakeDue("stuck");
        host.Queue.FailWith = new InvalidOperationException("Queue does not exist");
        await host.Sweep.SweepAsync(CancellationToken.None);

        await host.SetField("stuck", ScheduledJob.Fields.FirstFailedAt,
            Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow - TimeSpan.FromHours(1)));
        await host.MakeDue("stuck");
        await host.Sweep.SweepAsync(CancellationToken.None);

        var entry = host.DeadLetters.Entries.Single();
        await Assert.That(entry.Attempts).IsEqualTo(2);
        await Assert.That(entry.Error).Contains("Queue does not exist");
        await Assert.That(await host.JobNextExecuteAt("stuck")).IsEqualTo(Hourly.NextRun(DateTimeOffset.UtcNow));
        var snapshot = await host.Job("stuck").GetSnapshotAsync();
        await Assert.That(snapshot.ContainsField(ScheduledJob.Fields.DispatchFailures)).IsFalse();
        await Assert.That(snapshot.ContainsField(ScheduledJob.Fields.FirstFailedAt)).IsFalse();
    }

    [Test]
    public async Task A_one_shot_failing_past_the_retry_window_is_dead_lettered_and_removed()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var handle = await ScheduleOneShot(host, new TestRequest("given-up"), DateTimeOffset.UtcNow);
        host.Queue.FailWith = new InvalidOperationException("transport is down");
        await host.Sweep.SweepAsync(CancellationToken.None);

        await host.SetField(handle, ScheduledJob.Fields.FirstFailedAt,
            Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow - TimeSpan.FromHours(1)));
        await host.MakeDue(handle);
        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.DeadLetters.Entries.Single().Attempts).IsEqualTo(2);
        await Assert.That(await host.JobExists(handle)).IsFalse();
    }

    [Test]
    public async Task A_retried_recurring_job_runs_once_then_returns_to_its_schedule()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("recovers", new TestRequest("rc"), Hourly);
        await host.MakeDue("recovers");
        host.Queue.FailWith = new InvalidOperationException("transport is down");
        await host.Sweep.SweepAsync(CancellationToken.None);

        host.Queue.FailWith = null;
        await host.MakeDue("recovers");
        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(Enqueued(host, "rc")).IsEqualTo(1);
        await Assert.That(await host.JobNextExecuteAt("recovers")).IsEqualTo(Hourly.NextRun(DateTimeOffset.UtcNow));
        var snapshot = await host.Job("recovers").GetSnapshotAsync();
        await Assert.That(snapshot.ContainsField(ScheduledJob.Fields.DispatchFailures)).IsFalse();
        await Assert.That(snapshot.ContainsField(ScheduledJob.Fields.PendingDedupeKey)).IsFalse();
        await Assert.That(snapshot.ContainsField(ScheduledJob.Fields.FirstFailedAt)).IsFalse();
    }

    [Test]
    public async Task A_retried_occurrence_reuses_its_dedupe_key()
    {
        // The failed enqueue may have landed (a timeout), and its retry must then be a duplicate.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("timed-out", new TestRequest("to"), Hourly);
        await host.MakeDue("timed-out");
        host.Queue.FailWith = new InvalidOperationException("DeadlineExceeded");
        await host.Sweep.SweepAsync(CancellationToken.None);

        host.Queue.FailWith = null;
        await host.MakeDue("timed-out");
        await host.Sweep.SweepAsync(CancellationToken.None);

        var keys = DedupeKeys(host, "to");
        await Assert.That(keys).Count().IsEqualTo(2);
        await Assert.That(keys.Distinct()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Each_recurring_occurrence_has_its_own_dedupe_key()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("distinct", new TestRequest("ds"), Hourly);

        await host.MakeDue("distinct");
        await host.Sweep.SweepAsync(CancellationToken.None);
        await host.MakeDue("distinct", TimeSpan.FromSeconds(1));
        await host.Sweep.SweepAsync(CancellationToken.None);

        var keys = DedupeKeys(host, "ds");
        await Assert.That(keys).Count().IsEqualTo(2);
        await Assert.That(keys.Distinct()).Count().IsEqualTo(2);
    }

    [Test]
    public async Task The_dedupe_key_suits_every_transport_whatever_the_job_key()
    {
        // Cloud Tasks takes [A-Za-z0-9_-] only; the SQL outboxes take 200 characters.
        var jobKey = "billing:nightly run.v2 " + new string('x', 300);
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync(jobKey, new TestRequest("odd"), Hourly);
        await host.MakeDue(jobKey);

        await host.Sweep.SweepAsync(CancellationToken.None);

        var key = DedupeKeys(host, "odd").Single();
        await Assert.That(Regex.IsMatch(key, "^[A-Za-z0-9_-]{1,200}$")).IsTrue();
    }

    [Test]
    public async Task Due_one_shot_is_enqueued_and_its_document_removed()
    {
        // Deleting once enqueued keeps the collection bounded.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var handle = await ScheduleOneShot(host, new TestRequest("once"), DateTimeOffset.UtcNow);

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(Enqueued(host, "once")).IsEqualTo(1);
        await Assert.That(await host.JobExists(handle)).IsFalse();
    }

    [Test]
    public async Task A_one_shot_is_never_dispatched_twice()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await ScheduleOneShot(host, new TestRequest("single"), DateTimeOffset.UtcNow);

        await host.Sweep.SweepAsync(CancellationToken.None);
        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(Enqueued(host, "single")).IsEqualTo(1);
    }

    [Test]
    public async Task Competing_sweeps_dispatch_a_due_one_shot_exactly_once()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await using var rival = await SchedulerTestHost.StartAsync(fixture, reuseCollection: host.Collection);
        await ScheduleOneShot(host, new TestRequest("contested-once"), DateTimeOffset.UtcNow);

        await Task.WhenAll(
            host.Sweep.SweepAsync(CancellationToken.None),
            rival.Sweep.SweepAsync(CancellationToken.None));

        await Assert.That(Enqueued(host, "contested-once") + Enqueued(rival, "contested-once")).IsEqualTo(1);
    }

    [Test]
    public async Task A_one_shot_whose_enqueue_fails_is_kept_and_retried()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var handle = await ScheduleOneShot(host, new TestRequest("persistent"), DateTimeOffset.UtcNow);
        host.Queue.FailWith = new InvalidOperationException("transport is down");
        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.DeadLetters.Entries).IsEmpty();
        await Assert.That(await host.JobField<int>(handle, ScheduledJob.Fields.DispatchFailures)).IsEqualTo(1);

        host.Queue.FailWith = null;
        await host.MakeDue(handle);
        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(Enqueued(host, "persistent")).IsEqualTo(1);
        await Assert.That(await host.JobExists(handle)).IsFalse();
        await Assert.That(DedupeKeys(host, "persistent").Distinct()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task A_one_shot_that_cannot_be_rebuilt_is_removed_with_a_dead_letter()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var handle = await ScheduleOneShot(host, new TestRequest("doomed"), DateTimeOffset.UtcNow);
        await host.SetField(handle, ScheduledJob.Fields.RequestTypeName, "No.Such.Type");

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.DeadLetters.Entries).Count().IsEqualTo(1);
        await Assert.That(host.DeadLetters.Entries.Single().Source).IsEqualTo(DeadLetterSource.Scheduler);
        await Assert.That(await host.JobExists(handle)).IsFalse();
    }

    [Test]
    public async Task A_one_shot_survives_a_dead_letter_write_that_did_not_land()
    {
        // The document is the only copy left, so it must stay for the lapsed lease to retry.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var handle = await ScheduleOneShot(host, new TestRequest("precious"), DateTimeOffset.UtcNow);
        var typeName = await host.JobField<string>(handle, ScheduledJob.Fields.RequestTypeName);
        await host.SetField(handle, ScheduledJob.Fields.RequestTypeName, "No.Such.Type");
        host.DeadLetters.FailWith = new InvalidOperationException("dead-letter store is down");

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.DeadLetters.Entries).IsEmpty();
        await Assert.That(await host.JobExists(handle)).IsTrue();

        // E.g. a later deploy registers the type again.
        await host.SetField(handle, ScheduledJob.Fields.RequestTypeName, typeName);
        host.DeadLetters.FailWith = null;
        await host.MakeDue(handle);
        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(Enqueued(host, "precious")).IsEqualTo(1);
        await Assert.That(await host.JobExists(handle)).IsFalse();
    }

    private static string[] DedupeKeys(SchedulerTestHost host, string name) =>
        [.. host.Queue.Attempts
            .Where(a => a.Request is TestRequest r && r.Name == name)
            .Select(a => a.Options?.DedupeKey ?? "")];

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
