using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Queue;
using Spinneret.Queue.Sql;

namespace Spinneret.Composition.Tests;

/// <summary>
/// Cloud Tasks over a SQL outbox: an enqueue commits with the business write that caused it, a relay
/// forwards it to Cloud Tasks exactly once, and a delivery's writes, cascades, bookings and dead
/// letter commit together before the endpoint answers.
/// </summary>
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class CloudTasksOverSqlTests(MssqlContainerFixture fixture)
{
    // ---------------------------------------------------------------------------- producer ---

    [Test]
    public async Task An_enqueue_in_a_rolled_back_transaction_is_never_forwarded()
    {
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);
        var queue = host.Services.GetRequiredService<ISqlTransactionalQueue>();

        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            await queue.Enqueue(new PingCommand("never"), transaction);
            await transaction.RollbackAsync();
        }

        var result = await host.Relay.RelayAsync(CancellationToken.None);

        await Assert.That(result.MessagesForwarded).IsEqualTo(0);
        await Assert.That(host.Tasks.Requests).IsEmpty();
        await Assert.That(await host.OutboxCount()).IsEqualTo(0);
    }

    [Test]
    public async Task An_enqueue_in_a_committed_transaction_is_forwarded_once_and_leaves_the_outbox()
    {
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);
        await EnqueueCommitted(host, new PingCommand("committed"));

        var first = await host.Relay.RelayAsync(CancellationToken.None);
        var second = await host.Relay.RelayAsync(CancellationToken.None);

        await Assert.That(first.MessagesForwarded).IsEqualTo(1);
        await Assert.That(second.MessagesForwarded).IsEqualTo(0);
        await Assert.That(host.Tasks.Requests).Count().IsEqualTo(1);
        await Assert.That(await host.OutboxCount()).IsEqualTo(0);

        var task = host.Tasks.SingleRequest;
        await Assert.That(task.Parent).EndsWith("/queues/default-queue");
        await Assert.That(Guid.TryParseExact(CloudTasksOverSqlHost.TaskIdOf(task), "N", out _)).IsTrue();
        await Assert.That(CloudTasksOverSqlHost.EnvelopeOf(task).RequestTypeName).IsEqualTo(typeof(PingCommand).FullName!);
    }

    [Test]
    public async Task An_enqueue_outside_a_transaction_is_forwarded_as_it_commits()
    {
        // No relay pass: a host that is CPU-throttled outside requests may not run one for a while.
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new PingCommand("eager"));

        await Assert.That(host.Tasks.Requests).Count().IsEqualTo(1);
        await Assert.That(await host.OutboxCount()).IsEqualTo(0);
    }

    [Test]
    public async Task A_dedupe_key_is_the_task_name_so_a_repeat_is_not_delivered_twice()
    {
        // In the outbox the key only blocks a duplicate while the first is pending; once forwarded,
        // Cloud Tasks' memory of the name is what keeps the repeat from becoming a second task.
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new PingCommand("a"), new QueueOptions { DedupeKey = "order-7" });
        await host.Queue.Enqueue(new PingCommand("b"), new QueueOptions { DedupeKey = "order-7" });

        await Assert.That(host.Tasks.Requests).Count().IsEqualTo(1);
        await Assert.That(CloudTasksOverSqlHost.TaskIdOf(host.Tasks.SingleRequest)).IsEqualTo("order-7");
        await Assert.That(await host.OutboxCount()).IsEqualTo(0);
    }

    // ------------------------------------------------------------------------------- relay ---

    [Test]
    public async Task A_send_that_fails_stays_in_the_outbox_and_is_retried_after_a_backoff()
    {
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);
        await EnqueueCommitted(host, new PingCommand("retry"));
        host.Tasks.FailNextCreateTasks(1);

        var failed = await host.Relay.RelayAsync(CancellationToken.None);
        var tooSoon = await host.Relay.RelayAsync(CancellationToken.None);
        host.Clock.Now += TimeSpan.FromSeconds(31);
        var retried = await host.Relay.RelayAsync(CancellationToken.None);

        await Assert.That(failed.MessagesFailed).IsEqualTo(1);
        await Assert.That(tooSoon).IsEqualTo(RelayResult.Nothing);
        await Assert.That(retried.MessagesForwarded).IsEqualTo(1);
        await Assert.That(host.Tasks.Requests).Count().IsEqualTo(1);
        await Assert.That(await host.OutboxCount()).IsEqualTo(0);
    }

    [Test]
    public async Task A_lost_response_does_not_forward_the_message_twice()
    {
        // The task was created but the relay never heard so: the row stays, the retry is refused as
        // a duplicate name, and the refusal is what lets the row go.
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);
        await EnqueueCommitted(host, new PingCommand("lost"));
        host.Tasks.LoseNextCreateTaskResponses(1);

        await host.Relay.RelayAsync(CancellationToken.None);
        var outboxAfterLoss = await host.OutboxCount();
        host.Clock.Now += TimeSpan.FromSeconds(31);
        var retried = await host.Relay.RelayAsync(CancellationToken.None);

        await Assert.That(outboxAfterLoss).IsEqualTo(1);
        await Assert.That(retried.MessagesForwarded).IsEqualTo(1);
        await Assert.That(host.Tasks.Requests).Count().IsEqualTo(1);
        await Assert.That(await host.OutboxCount()).IsEqualTo(0);
    }

    [Test]
    public async Task Concurrent_relays_forward_each_message_once()
    {
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);
        await EnqueueCommitted(host, Enumerable.Range(0, 30).Select(i => new PingCommand($"m{i}")).ToArray());

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => host.Relay.RelayAsync(CancellationToken.None))));

        await Assert.That(results.Sum(r => r.MessagesForwarded)).IsEqualTo(30);
        await Assert.That(host.Tasks.Requests.Select(r => r.Task.Name).Distinct().Count()).IsEqualTo(30);
        await Assert.That(await host.OutboxCount()).IsEqualTo(0);
    }

    [Test]
    public async Task A_message_due_beyond_the_forwarding_horizon_waits_in_the_outbox()
    {
        // Cloud Tasks will not schedule a task more than 30 days out; the outbox holds it until then.
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new PingCommand("later"), new QueueOptions { Delay = TimeSpan.FromDays(40) });
        var early = await host.Relay.RelayAsync(CancellationToken.None);
        host.Clock.Now += TimeSpan.FromDays(39.5);
        var inWindow = await host.Relay.RelayAsync(CancellationToken.None);

        await Assert.That(early).IsEqualTo(RelayResult.Nothing);
        await Assert.That(inWindow.MessagesForwarded).IsEqualTo(1);
        var scheduled = host.Tasks.SingleRequest.Task.ScheduleTime.ToDateTimeOffset();
        await Assert.That(scheduled > host.Clock.Now).IsTrue();
    }

    // ---------------------------------------------------------------------------- delivery ---

    [Test]
    public async Task A_delivery_commits_its_writes_and_forwards_its_cascade_before_answering()
    {
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);
        var table = await host.CreateSideTable();
        await host.Queue.Enqueue(new WriteAndCascadeCommand(table, "child"));

        var status = await host.Deliver(host.Tasks.SingleRequest);

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await host.ScalarAsync<int>($"SELECT COUNT(*) FROM [{table}]")).IsEqualTo(1);
        // The cascade was forwarded as the delivery committed, before the 200.
        await Assert.That(host.Tasks.Requests).Count().IsEqualTo(2);
        await Assert.That(await host.OutboxCount()).IsEqualTo(0);

        await host.Deliver(host.Tasks.Requests[1]);
        await Assert.That(host.Log.DeliveryCount("ping:child")).IsEqualTo(1);
    }

    [Test]
    public async Task A_failing_delivery_rolls_back_its_writes_and_files_its_dead_letter_in_the_same_database()
    {
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);
        var table = await host.CreateSideTable();
        await host.Queue.Enqueue(new WriteThenFailCommand(table));
        var task = host.Tasks.SingleRequest;

        var status = await host.Deliver(task);

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await host.ScalarAsync<int>($"SELECT COUNT(*) FROM [{table}]")).IsEqualTo(0);
        await Assert.That(await host.ScalarAsync<string>($"SELECT IdempotencyKey FROM [{host.DeadLetterTable}]"))
            .IsEqualTo(CloudTasksOverSqlHost.TaskIdOf(task));
    }

    [Test]
    public async Task A_failed_attempt_books_its_retry_through_the_outbox()
    {
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);
        await host.Queue.Enqueue(new FailOnceCommand("k"));

        var first = await host.Deliver(host.Tasks.SingleRequest);
        var retry = host.Tasks.Requests[1];
        var second = await host.Deliver(retry);

        await Assert.That(first).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(CloudTasksOverSqlHost.EnvelopeOf(retry).PriorFailures).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.Log.DeliveryCount("failonce:k")).IsEqualTo(1);
    }

    [Test]
    public async Task A_redelivered_task_dead_letters_once()
    {
        // Cloud Tasks redelivers when it never saw the 200; the dead letter is filed under the task's
        // name, so the second write lands on the first.
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);
        var table = await host.CreateSideTable();
        await host.Queue.Enqueue(new WriteThenFailCommand(table));

        await host.Deliver(host.Tasks.SingleRequest);
        await host.Deliver(host.Tasks.SingleRequest);

        await Assert.That(await host.DeadLetterCount()).IsEqualTo(1);
    }

    // ------------------------------------------------------------------------------ resend ---

    [Test]
    public async Task A_resend_enqueues_and_deletes_in_one_transaction_and_is_forwarded()
    {
        await using var host = await CloudTasksOverSqlHost.StartAsync(fixture.ConnectionString);
        var table = await host.CreateSideTable();
        await host.Queue.Enqueue(new WriteThenFailCommand(table));
        await host.Deliver(host.Tasks.SingleRequest);
        var key = CloudTasksOverSqlHost.TaskIdOf(host.Tasks.SingleRequest);

        var result = await host.Services.GetRequiredService<IDeadLetterResender>().ResendAsync(key);

        await Assert.That(result.Match(() => true, _ => false)).IsTrue();
        await Assert.That(await host.DeadLetterCount()).IsEqualTo(0);
        await Assert.That(host.Tasks.Requests).Count().IsEqualTo(2);
        await Assert.That(CloudTasksOverSqlHost.EnvelopeOf(host.Tasks.Requests[1]).RequestTypeName)
            .IsEqualTo(typeof(WriteThenFailCommand).FullName!);
    }

    // ----------------------------------------------------------------------------- helpers ---

    private static async Task EnqueueCommitted(CloudTasksOverSqlHost host, params PingCommand[] commands)
    {
        var queue = host.Services.GetRequiredService<ISqlTransactionalQueue>();
        await using var connection = await host.OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        foreach (var command in commands)
            await queue.Enqueue(command, transaction);
        await transaction.CommitAsync();
    }
}
