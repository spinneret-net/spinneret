using Google.Cloud.Firestore;
using Grpc.Core;

namespace Spinneret.Queue.Firestore.Tests;

/// <summary>
/// The write side against a real Firestore (the emulator, via Testcontainers): that a dead letter
/// lands as the document shape the contract promises, and that the first write wins however many
/// times the transport redelivers.
/// </summary>
/// <remarks>
/// What the emulator does not prove: it never enforces composite-index requirements, so a query it
/// serves happily can still be rejected in production with FAILED_PRECONDITION. These tests pin
/// document shape, ordering and transaction semantics — not index provisioning.
/// </remarks>
[ClassDataSource<FirestoreEmulatorFixture>(Shared = SharedType.PerTestSession)]
public sealed class FirestoreDeadLetterWriterTests(FirestoreEmulatorFixture fixture)
{
    private static DeadLetterEntry Entry(
        string key = "task-1",
        string? description = "nightly sync",
        DeadLetterSource source = DeadLetterSource.Queue) =>
        new()
        {
            IdempotencyKey = key,
            Source = source,
            CommandTypeName = typeof(PingCommand).FullName!,
            Description = description,
            PayloadJson = """{"name":"ada"}""",
            Error = "boom",
            Attempts = 3,
        };

    [Test]
    public async Task Stores_every_field_with_the_type_the_contract_promises()
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await host.Writer.WriteAsync(Entry());

        var fields = await host.RawFields("task-1");
        await Assert.That(fields).IsNotNull();
        await Assert.That(fields![DeadLetterDocument.Fields.Source]).IsEqualTo("Queue");
        await Assert.That(fields[DeadLetterDocument.Fields.CommandTypeName])
            .IsEqualTo(typeof(PingCommand).FullName!);
        await Assert.That(fields[DeadLetterDocument.Fields.Description]).IsEqualTo("nightly sync");
        await Assert.That(fields[DeadLetterDocument.Fields.PayloadJson]).IsEqualTo("""{"name":"ada"}""");
        await Assert.That(fields[DeadLetterDocument.Fields.Error]).IsEqualTo("boom");
        // Firestore widens every integer to 64 bits, whatever width was written.
        await Assert.That(fields[DeadLetterDocument.Fields.Attempts]).IsEqualTo(3L);
        await Assert.That(fields[DeadLetterDocument.Fields.DeadLetteredAt])
            .IsEqualTo(Timestamp.FromDateTimeOffset(host.Clock.Now));
    }

    [Test]
    public async Task Files_the_document_under_the_idempotency_key_without_duplicating_it_into_a_field()
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await host.Writer.WriteAsync(Entry("some-task-id"));

        var fields = await host.RawFields("some-task-id");
        await Assert.That(fields).IsNotNull();
        await Assert.That(fields!.Keys).DoesNotContain("idempotencyKey");
    }

    [Test]
    public async Task Stores_a_null_description_as_null_rather_than_omitting_the_field()
    {
        // Readers see a consistent shape either way, which is what the docs promise.
        await using var host = DeadLetterTestHost.Start(fixture);

        await host.Writer.WriteAsync(Entry(description: null));

        var fields = await host.RawFields("task-1");
        await Assert.That(fields!.ContainsKey(DeadLetterDocument.Fields.Description)).IsTrue();
        await Assert.That(fields[DeadLetterDocument.Fields.Description]).IsNull();
    }

    [Test]
    public async Task A_redelivered_write_keeps_the_instant_the_failure_actually_happened()
    {
        // Create-not-Set: a Set would move deadLetteredAt forward on every redelivery.
        await using var host = DeadLetterTestHost.Start(fixture);
        var first = host.Clock.Now;

        await host.Writer.WriteAsync(Entry());
        host.Clock.Now = first.AddHours(2);
        await host.Writer.WriteAsync(Entry() with { Error = "a later, different complaint" });

        var stored = await host.Store.GetAsync("task-1");
        await Assert.That(stored!.DeadLetteredAt).IsEqualTo(first);
        await Assert.That(stored.Error).IsEqualTo("boom");
        await Assert.That(await host.DocumentCount()).IsEqualTo(1);
    }

    [Test]
    public async Task Two_writers_racing_on_one_key_yield_a_single_document()
    {
        // Genuine concurrency, not a sequential redelivery: the loser takes the AlreadyExists
        // branch against a real Firestore rather than a hand-built RpcException.
        await using var host = DeadLetterTestHost.Start(fixture);

        await Task.WhenAll(host.Writer.WriteAsync(Entry()), host.Writer.WriteAsync(Entry()));

        await Assert.That(await host.DocumentCount()).IsEqualTo(1);
    }

    [Test]
    public async Task Heavier_contention_still_never_produces_a_second_document()
    {
        // Beyond two writers the emulator serializes same-document writes with a pessimistic lock
        // and starts returning Aborted ("Transaction lock timeout") to the ones that wait too long.
        // That is emulator vocabulary, not a library fault, and propagating it is what the writer
        // is supposed to do: QueueDeliveryProcessor retries a failed dead-letter write rather than
        // acknowledging the task. So the assertion is the invariant that survives either outcome —
        // a writer may fail, but it may never end up having written a second entry.
        await using var host = DeadLetterTestHost.Start(fixture);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try
            {
                await host.Writer.WriteAsync(Entry());
                return true;
            }
            catch (RpcException)
            {
                return false;
            }
        }));

        await Assert.That(await host.DocumentCount()).IsEqualTo(1);
        await Assert.That(outcomes).Contains(true);
    }

    [Test]
    [Arguments(DeadLetterSource.Queue)]
    [Arguments(DeadLetterSource.Scheduler)]
    public async Task Round_trips_every_source_through_Firestore(DeadLetterSource source)
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await host.Writer.WriteAsync(Entry(source: source));

        await Assert.That((await host.Store.GetAsync("task-1"))!.Source).IsEqualTo(source);
    }
}
