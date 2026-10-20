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

        await Assert.That(host.DeadLetters.Entries).Count().IsEqualTo(2);
        await Assert.That(await host.JobExists("first")).IsTrue();
        await Assert.That(await host.JobExists("second")).IsTrue();
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
    public async Task Failed_one_shot_dispatch_is_removed_with_a_dead_letter()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var handle = await ScheduleOneShot(host, new TestRequest("doomed"), DateTimeOffset.UtcNow);
        host.Queue.FailWith = new InvalidOperationException("transport is down");

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
        host.Queue.FailWith = new InvalidOperationException("transport is down");
        host.DeadLetters.FailWith = new InvalidOperationException("dead-letter store is down too");

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.DeadLetters.Entries).IsEmpty();
        await Assert.That(await host.JobExists(handle)).IsTrue();

        host.Queue.FailWith = null;
        host.DeadLetters.FailWith = null;
        await host.MakeDue(handle);
        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(Enqueued(host, "precious")).IsEqualTo(1);
        await Assert.That(await host.JobExists(handle)).IsFalse();
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
