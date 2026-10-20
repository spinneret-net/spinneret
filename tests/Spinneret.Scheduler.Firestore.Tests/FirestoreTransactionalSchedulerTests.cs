using Google.Cloud.Firestore;

namespace Spinneret.Scheduler.Firestore.Tests;

[ClassDataSource<FirestoreEmulatorFixture>(Shared = SharedType.PerTestSession)]
public sealed class FirestoreTransactionalSchedulerTests(FirestoreEmulatorFixture fixture)
{
    private const string Stockholm = "Europe/Stockholm";
    private static readonly Schedule Hourly = Schedule.Cron("0 * * * *", Stockholm);

    [Test]
    public async Task A_one_shot_in_a_committed_transaction_is_stored_and_runs_once()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        var handle = await Commit(host, tx =>
            host.TransactionalScheduler.ScheduleJob(tx, new TestRequest("once"), DateTimeOffset.UtcNow));

        await Assert.That(await host.JobExists(handle)).IsTrue();

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.Queue.CountOf<TestRequest>(r => r.Name == "once")).IsEqualTo(1);
        await Assert.That(await host.JobExists(handle)).IsFalse();
    }

    [Test]
    public async Task A_one_shot_commits_atomically_with_the_callers_own_write()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var businessDoc = host.Db.Collection($"business_{Guid.NewGuid():N}").Document("termination");

        var handle = await Commit(host, tx =>
        {
            var id = host.TransactionalScheduler.ScheduleJob(
                tx, new TestRequest("offboard"), DateTimeOffset.UtcNow);
            tx.Set(businessDoc, new Dictionary<string, object> { ["state"] = "terminated" });
            return id;
        });

        await Assert.That(await host.JobExists(handle)).IsTrue();
        await Assert.That((await businessDoc.GetSnapshotAsync()).Exists).IsTrue();
    }

    [Test]
    public async Task A_one_shot_in_a_failed_transaction_never_runs()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var businessDoc = host.Db.Collection($"business_{Guid.NewGuid():N}").Document("termination");

        await Assert.That(() => host.Db.RunTransactionAsync(tx =>
        {
            host.TransactionalScheduler.ScheduleJob(tx, new TestRequest("phantom"), DateTimeOffset.UtcNow);
            tx.Set(businessDoc, new Dictionary<string, object> { ["state"] = "terminated" });
            throw new InvalidOperationException("the caller's own work failed");
        })).Throws<InvalidOperationException>();

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.Queue.Enqueued).IsEmpty();
        await Assert.That(await host.JobCount()).IsEqualTo(0);
        await Assert.That((await businessDoc.GetSnapshotAsync()).Exists).IsFalse();
    }

    [Test]
    public async Task A_cancelled_one_shot_never_runs()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var handle = await Commit(host, tx =>
            host.TransactionalScheduler.ScheduleJob(tx, new TestRequest("cancelled"), DateTimeOffset.UtcNow));

        await host.Db.RunTransactionAsync(tx =>
        {
            host.TransactionalScheduler.CancelJob(tx, handle);
            return Task.CompletedTask;
        });

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(await host.JobExists(handle)).IsFalse();
        await Assert.That(host.Queue.Enqueued).IsEmpty();
    }

    [Test]
    public async Task Cancelling_a_job_that_already_ran_is_a_silent_no_op()
    {
        // Firestore requires reads before writes, so CancelJob stays write-only; a delete needs no read.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var handle = await Commit(host, tx =>
            host.TransactionalScheduler.ScheduleJob(tx, new TestRequest("gone"), DateTimeOffset.UtcNow));
        await host.Sweep.SweepAsync(CancellationToken.None);

        await host.Db.RunTransactionAsync(tx =>
        {
            host.TransactionalScheduler.CancelJob(tx, handle);
            return Task.CompletedTask;
        });

        await Assert.That(await host.JobCount()).IsEqualTo(0);
    }

    [Test]
    public async Task Cancelling_with_a_recurring_key_is_rejected_and_leaves_the_schedule_intact()
    {
        // Guarded on the handle, since reading the document would break the write-only contract.
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        await host.Scheduler.RegisterAsync("nightly", new TestRequest("n"), Hourly);

        await Assert.That(() => host.Db.RunTransactionAsync(tx =>
        {
            host.TransactionalScheduler.CancelJob(tx, "nightly");
            return Task.CompletedTask;
        })).Throws<ArgumentException>();

        await Assert.That(await host.JobExists("nightly")).IsTrue();
    }

    [Test]
    public async Task One_shot_handles_are_prefixed_so_they_cannot_be_mistaken_for_a_recurring_key()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        var handle = await Commit(host, tx =>
            host.TransactionalScheduler.ScheduleJob(tx, new TestRequest("h"), DateTimeOffset.UtcNow));

        await Assert.That(handle).StartsWith(ScheduledJob.OneShotHandlePrefix);
    }

    [Test]
    public async Task A_one_shot_carries_no_schedule_field()
    {
        // Its absence is what marks a document as one-shot.
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        var handle = await Commit(host, tx =>
            host.TransactionalScheduler.ScheduleJob(tx, new TestRequest("bare"), DateTimeOffset.UtcNow.AddHours(1)));

        var snapshot = await host.Job(handle).GetSnapshotAsync();
        await Assert.That(snapshot.ContainsField(ScheduledJob.Fields.Schedule)).IsFalse();
        await Assert.That(snapshot.GetValue<string>(ScheduledJob.Fields.RequestTypeName))
            .IsEqualTo(typeof(TestRequest).FullName!);
    }

    [Test]
    public async Task A_one_shot_scheduled_for_later_is_not_swept_yet()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);
        var handle = await Commit(host, tx =>
            host.TransactionalScheduler.ScheduleJob(tx, new TestRequest("later"), DateTimeOffset.UtcNow.AddHours(1)));

        await host.Sweep.SweepAsync(CancellationToken.None);

        await Assert.That(host.Queue.Enqueued).IsEmpty();
        await Assert.That(await host.JobExists(handle)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Cancelling_a_blank_handle_is_rejected(string? handle)
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture);

        await Assert.That(() => host.Db.RunTransactionAsync(tx =>
        {
            host.TransactionalScheduler.CancelJob(tx, handle!);
            return Task.CompletedTask;
        })).Throws<ArgumentException>();
    }

    private static async Task<string> Commit(SchedulerTestHost host, Func<Transaction, string> work)
    {
        string result = null!;
        await host.Db.RunTransactionAsync(tx =>
        {
            result = work(tx);
            return Task.CompletedTask;
        });
        return result;
    }
}
