using Microsoft.Data.SqlClient;
using Spinneret.Queue;

namespace Spinneret.Queue.Mssql.Tests;

/// <summary>
/// End-to-end tests against a real SQL Server (Docker via Testcontainers): enqueue → poll →
/// dispatch → handler, including the transactional guarantees the transport exists for.
/// </summary>
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlQueueIntegrationTests(MssqlContainerFixture fixture)
{
    // ---------------------------------------------------------------------- happy path ---

    [Test]
    public async Task Enqueued_command_is_delivered_and_the_row_is_consumed()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new PingCommand("hello"));

        await Wait.Until(() => host.Log.DeliveryCount("ping:hello") == 1, "the command to be delivered");
        await Wait.Until(async () => await host.QueueRowCount() == 0, "the queue row to be consumed");
    }

    [Test]
    public async Task Delayed_command_is_not_delivered_before_it_is_due()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new PingCommand("later"), new QueueOptions { Delay = TimeSpan.FromSeconds(2) });

        await Task.Delay(500);
        await Assert.That(host.Log.DeliveryCount("ping:later")).IsEqualTo(0);
        await Wait.Until(() => host.Log.DeliveryCount("ping:later") == 1, "the delayed command to be delivered");
    }

    [Test]
    public async Task Commands_on_a_declared_channel_are_consumed()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new BulkChannelCommand("b1"));

        await Wait.Until(() => host.Log.DeliveryCount("bulk:b1") == 1, "the bulk-channel command to be delivered");
    }

    [Test]
    public async Task Concurrent_messages_are_each_delivered_exactly_once()
    {
        await using var host = await QueueTestHost.StartAsync(
            fixture.ConnectionString,
            extraConfig: new() { ["Queue:Mssql:ChannelParallelism:default"] = "4" });

        for (var i = 0; i < 25; i++)
            await host.Queue.Enqueue(new PingCommand($"n{i}"));

        await Wait.Until(() => host.Log.Deliveries.Count(d => d.StartsWith("ping:n")) >= 25, "all messages to arrive");
        // Settle briefly, then verify no message was delivered twice.
        await Task.Delay(300);
        var deliveries = host.Log.Deliveries.Where(d => d.StartsWith("ping:n")).ToArray();
        await Assert.That(deliveries.Length).IsEqualTo(25);
        await Assert.That(deliveries.Distinct().Count()).IsEqualTo(25);
    }

    // ------------------------------------------------------------- transactional enqueue ---

    [Test]
    public async Task Enqueue_in_a_rolled_back_transaction_is_never_delivered()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            await host.TransactionalQueue.Enqueue(new PingCommand("ghost"), transaction);
            await transaction.RollbackAsync();
        }

        await Task.Delay(400);
        await Assert.That(host.Log.DeliveryCount("ping:ghost")).IsEqualTo(0);
        await Assert.That(await host.QueueRowCount()).IsEqualTo(0);
    }

    [Test]
    public async Task Enqueue_in_a_committed_transaction_is_delivered()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            await host.TransactionalQueue.Enqueue(new PingCommand("committed"), transaction);
            await transaction.CommitAsync();
        }

        await Wait.Until(() => host.Log.DeliveryCount("ping:committed") == 1, "the committed enqueue to be delivered");
    }

    [Test]
    public async Task Enqueue_joins_the_ambient_transaction()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            host.Transactions.Use(transaction);
            try
            {
                // IQueue.Enqueue with no explicit transaction: must ride the ambient one.
                await host.Queue.Enqueue(new PingCommand("ambient"));
                await Assert.That(host.Log.DeliveryCount("ping:ambient")).IsEqualTo(0);
                await transaction.RollbackAsync();
            }
            finally
            {
                host.Transactions.Use(null);
            }
        }

        await Task.Delay(400);
        // Rolled back with the ambient transaction — never delivered.
        await Assert.That(host.Log.DeliveryCount("ping:ambient")).IsEqualTo(0);
    }

    // ------------------------------------------------------------------------- deduping ---

    [Test]
    public async Task Dedupe_key_prevents_a_second_pending_enqueue()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);

        await host.Queue.Enqueue(new PingCommand("a"), new QueueOptions { DedupeKey = "job-1" });
        await host.Queue.Enqueue(new PingCommand("b"), new QueueOptions { DedupeKey = "job-1" });

        await Assert.That(await host.QueueRowCount()).IsEqualTo(1);
    }

    [Test]
    public async Task Dedupe_key_is_reusable_after_the_message_is_consumed()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new PingCommand("first"), new QueueOptions { DedupeKey = "job-2" });
        await Wait.Until(() => host.Log.DeliveryCount("ping:first") == 1, "the first deduped message");

        await host.Queue.Enqueue(new PingCommand("second"), new QueueOptions { DedupeKey = "job-2" });
        await Wait.Until(() => host.Log.DeliveryCount("ping:second") == 1, "the second deduped message");
    }

    // ------------------------------------------------------------------ retries & booking ---

    [Test]
    public async Task Transient_failures_are_retried_with_booked_attempts_until_success()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new FailTwiceCommand("ft"));

        await Wait.Until(() => host.Log.DeliveryCount("failtwice:ft") == 1, "the command to succeed after retries");
        await Assert.That(host.Log.Attempts("ft")).IsEqualTo(3);
        await Assert.That(await host.DeadLetterRowCount()).IsEqualTo(0);
    }

    [Test]
    public async Task Exhausted_retries_dead_letter_the_command()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new AlwaysFailCommand("af"), new QueueOptions { Description = "doomed" });

        await Wait.Until(async () => await host.DeadLetterRowCount() == 1, "the command to be dead-lettered");
        await Assert.That(host.Log.Attempts("af")).IsEqualTo(2); // MaxAttempts = 2
        await Wait.Until(async () => await host.QueueRowCount() == 0, "the queue to be drained");

        var typeName = await host.ScalarAsync<string>(
            $"SELECT CommandTypeName FROM [{host.Options.DeadLetterTableName}]");
        var attempts = await host.ScalarAsync<int>(
            $"SELECT Attempts FROM [{host.Options.DeadLetterTableName}]");
        var description = await host.ScalarAsync<string>(
            $"SELECT Description FROM [{host.Options.DeadLetterTableName}]");
        await Assert.That(typeName).IsEqualTo(typeof(AlwaysFailCommand).FullName!);
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(description).IsEqualTo("doomed");
    }

    [Test]
    public async Task Permanent_failure_dead_letters_without_retrying()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new PermanentFailCommand("pf"));

        await Wait.Until(async () => await host.DeadLetterRowCount() == 1, "the command to be dead-lettered");
        await Assert.That(host.Log.Attempts("pf")).IsEqualTo(1);
    }

    [Test]
    public async Task Deferral_reschedules_without_consuming_attempts()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new DeferOnceCommand("d1"));

        await Wait.Until(() => host.Log.DeliveryCount("defer:d1") == 1, "the deferred command to complete");
        await Assert.That(host.Log.Attempts("d1")).IsEqualTo(2);
        await Assert.That(await host.DeadLetterRowCount()).IsEqualTo(0);
    }

    // ------------------------------------------------------------- savepoint atomicity ---

    [Test]
    public async Task Failed_handlers_partial_writes_are_rolled_back_while_the_failure_is_booked()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);
        var sideTable = $"Side_{host.Suffix}";
        await host.ExecuteAsync($"CREATE TABLE [{sideTable}] (Value NVARCHAR(50) NOT NULL);");

        await host.Queue.Enqueue(new WriteThenFailCommand(sideTable));

        await Wait.Until(async () => await host.DeadLetterRowCount() == 1, "the failure to be dead-lettered");
        // The write happened on the delivery transaction before the failure — the savepoint must
        // have rolled it back even though the delete + dead-letter committed.
        await Assert.That(await host.ScalarAsync<int>($"SELECT COUNT(*) FROM [{sideTable}]")).IsEqualTo(0);
        await Assert.That(await host.QueueRowCount()).IsEqualTo(0);
    }

    [Test]
    public async Task Successful_handlers_writes_commit_with_the_dequeue()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);
        var sideTable = $"Side_{host.Suffix}";
        await host.ExecuteAsync($"CREATE TABLE [{sideTable}] (Value NVARCHAR(50) NOT NULL);");

        await host.Queue.Enqueue(new WriteCommand(sideTable));

        await Wait.Until(
            async () => await host.ScalarAsync<int>($"SELECT COUNT(*) FROM [{sideTable}]") == 1,
            "the handler's write to commit");
    }

    [Test]
    public async Task Failed_handlers_cascade_enqueues_are_rolled_back()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new CascadeThenFailCommand("cf"));

        await Wait.Until(async () => await host.DeadLetterRowCount() == 1, "the failure to be dead-lettered");
        await Task.Delay(400);
        // The cascade enqueue rode the rolled-back savepoint — its ping must never run.
        await Assert.That(host.Log.DeliveryCount("ping:cascade-cf")).IsEqualTo(0);
    }

    [Test]
    public async Task Successful_handlers_cascade_enqueues_are_delivered()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new CascadeCommand("cs"));

        await Wait.Until(() => host.Log.DeliveryCount("ping:cascade-cs") == 1, "the cascade ping to be delivered");
    }

    // ------------------------------------------------------------------ poisoned rows ---

    [Test]
    public async Task Unreadable_envelope_is_dead_lettered_with_the_raw_content()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.ExecuteAsync(
            $"INSERT INTO [{host.Options.QueueTableName}] (Channel, VisibleAt, Envelope) " +
            "VALUES (N'default', SYSUTCDATETIME(), N'this is not json');");

        await Wait.Until(async () => await host.DeadLetterRowCount() == 1, "the unreadable row to be dead-lettered");
        var typeName = await host.ScalarAsync<string>(
            $"SELECT CommandTypeName FROM [{host.Options.DeadLetterTableName}]");
        var payload = await host.ScalarAsync<string>(
            $"SELECT PayloadJson FROM [{host.Options.DeadLetterTableName}]");
        await Assert.That(typeName).IsEqualTo("<unreadable envelope>");
        await Assert.That(payload).IsEqualTo("this is not json");
        await Assert.That(await host.QueueRowCount()).IsEqualTo(0);
    }
}
